using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibreMetaverse;
using WebRtcVoiceManager = LibreMetaverse.Voice.WebRTC.VoiceManager;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private readonly object _voiceStateLock = new();
    private readonly SemaphoreSlim _voicePlaybackGate = new(1, 1);

    private HttpClient? _piperHttp;
    private WebRtcVoiceManager? _webRtcVoice;
    private bool _voiceRoutingEnabled;
    private string _activeVoiceBackend = "none";

    private sealed record PiperVoicesResponse(IReadOnlyList<string>? Voices, string? DefaultVoice);

    private void InitializeVoiceSupport()
    {
        _voiceRoutingEnabled = _options.VoiceRoutingEnabled;
        _activeVoiceBackend = "none";
        _piperHttp = new HttpClient
        {
            BaseAddress = BuildPiperBaseUri(),
            Timeout = TimeSpan.FromSeconds(_options.PiperRequestTimeoutSeconds)
        };

        if (_voiceRoutingEnabled)
        {
            Console.WriteLine($"[voice] routing requested on startup; backend={NormalizeVoiceBackend(_options.VoiceBackend)} piper={_piperHttp.BaseAddress}");
        }
    }

    private async Task EnsureVoiceBackendOnLoginAsync(GridClient client, CancellationToken cancellationToken)
    {
        if (!_voiceRoutingEnabled)
        {
            return;
        }

        try
        {
            var result = await EnsureVoiceBackendConnectedAsync(client, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                Console.WriteLine($"[voice] startup routing disabled: {result.Message}");
                _voiceRoutingEnabled = false;
                _activeVoiceBackend = "none";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[voice] startup routing disabled due to unexpected error: {ex.Message}");
            _voiceRoutingEnabled = false;
            _activeVoiceBackend = "none";
        }
    }

    private void HandleVoiceDisconnected()
    {
        lock (_voiceStateLock)
        {
            _activeVoiceBackend = "none";
        }
    }

    private void DisposeVoiceSupport()
    {
        lock (_voiceStateLock)
        {
            try
            {
                _webRtcVoice?.StopWavAsMic();
            }
            catch
            {
                // Best effort shutdown.
            }

            try
            {
                _webRtcVoice?.Disconnect();
            }
            catch
            {
                // Best effort shutdown.
            }

            _webRtcVoice = null;
            _activeVoiceBackend = "none";
        }

        try
        {
            _piperHttp?.Dispose();
        }
        catch
        {
            // Best effort shutdown.
        }

        _piperHttp = null;
        _voicePlaybackGate.Dispose();
    }

    public async Task<BotToolResult> SetVoiceRoutingAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            lock (_voiceStateLock)
            {
                _voiceRoutingEnabled = false;
                _activeVoiceBackend = "none";
                try
                {
                    _webRtcVoice?.StopWavAsMic();
                    _webRtcVoice?.Disconnect();
                }
                catch
                {
                    // Best effort disable.
                }
            }

            return BotToolResult.OkResult("Voice routing disabled.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            _voiceRoutingEnabled = true;
            var connect = await EnsureVoiceBackendConnectedAsync(client, token).ConfigureAwait(false);
            if (!connect.Ok)
            {
                _voiceRoutingEnabled = false;
                _activeVoiceBackend = "none";
                return BotToolResult.Fail(connect.Message);
            }

            return BotToolResult.OkResult(connect.Message);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleVoiceCommandAsync(GridClient client, UUID agentId, string from, string arg)
    {
        var sub = (arg ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(sub) || sub == "status")
        {
            SendImText(client, agentId, from, BuildVoiceStatusText());
            return;
        }

        if (sub is "on" or "enable" or "enabled" or "true")
        {
            var enable = await SetVoiceRoutingAsync(true, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, enable.Message);
            return;
        }

        if (sub is "off" or "disable" or "disabled" or "false")
        {
            var disable = await SetVoiceRoutingAsync(false, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, disable.Message);
            return;
        }

        SendImText(client, agentId, from, "Usage: *voice status | *voice on | *voice off");
    }

    private async Task HandleVoicesCommandAsync(GridClient client, UUID agentId, string from)
    {
        var http = _piperHttp;
        if( http == null)
        {
            SendImText(client, agentId, from, "Voice support is not initialized.");
            return;
        }

        try
        {
            using var response = await http.GetAsync(_options.PiperVoicesPath, CancellationToken.None).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                SendImText(client, agentId, from,
                    $"Piper voices request failed: {(int)response.StatusCode} {response.ReasonPhrase}. Detail: {TrimForMessage(payload, 300)}");
                return;
            }

            var parsed = ParsePiperVoicesPayload(payload);
            var voices = parsed.Voices ?? Array.Empty<string>();

            var lines = new List<string>
            {
                $"Piper voices ({voices.Count}):",
                $"endpoint: {BuildPiperEndpoint(_options.PiperVoicesPath)}",
                $"default voice: {parsed.DefaultVoice ?? _options.PiperDefaultVoice}"
            };

            foreach (var voiceName in voices.Take(20))
            {
                lines.Add($"- {voiceName}");
            }

            if (voices.Count > 20)
            {
                lines.Add($"... and {voices.Count - 20} more");
            }

            SendImText(client, agentId, from, string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            SendImText(client, agentId, from, $"Failed to query Piper voices: {ex.Message}");
        }
    }

    private async Task HandleSayCommandAsync(GridClient client, UUID agentId, string from, string arg)
    {
        if (!TryParseSayStarCommand(arg, out var text, out var voiceOverride, out var parseError))
        {
            SendImText(client, agentId, from, parseError);
            return;
        }

        var say = await SayAsync(
            text,
            voiceOverride,
            speaker: null,
            speakerId: null,
            lengthScale: null,
            noiseScale: null,
            noiseW: null,
            sentenceSilence: null,
            CancellationToken.None).ConfigureAwait(false);

        SendImText(client, agentId, from, say.Message);
    }

    public async Task<DataToolResult> ListVoicesAsync(CancellationToken cancellationToken)
    {
        var client = _piperHttp;
        if (client == null)
        {
            return DataToolResult.FailResult("Voice support is not initialized.");
        }

        try
        {
            using var response = await client.GetAsync(_options.PiperVoicesPath, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return DataToolResult.FailResult($"Piper voices request failed: {(int)response.StatusCode} {response.ReasonPhrase}. Detail: {TrimForMessage(payload, 300)}");
            }

            var parsed = ParsePiperVoicesPayload(payload);
            var voiceCount = parsed.Voices?.Count ?? 0;
            var resultPayload = JsonSerializer.Serialize(new
            {
                endpoint = BuildPiperEndpoint(_options.PiperVoicesPath),
                backend = GetConfiguredBackendLabel(),
                routingEnabled = _voiceRoutingEnabled,
                activeBackend = _activeVoiceBackend,
                configuredDefaultVoice = _options.PiperDefaultVoice,
                defaultVoice = parsed.DefaultVoice,
                voices = parsed.Voices,
                raw = TryParseJsonElement(payload)
            });

            return DataToolResult.OkResult($"Found {voiceCount} Piper voice(s).", resultPayload);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DataToolResult.FailResult($"Failed to query Piper voices: {ex.Message}");
        }
    }

    public Task<DataToolResult> QueryVoiceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var initialized = _piperHttp != null;
        var payload = JsonSerializer.Serialize(new
        {
            initialized,
            routingEnabled = _voiceRoutingEnabled,
            configuredBackend = GetConfiguredBackendLabel(),
            activeBackend = _activeVoiceBackend,
            piperBaseUrl = BuildPiperBaseUri().ToString().TrimEnd('/'),
            piperVoicesEndpoint = BuildPiperEndpoint(_options.PiperVoicesPath),
            piperTtsEndpoint = BuildPiperEndpoint(_options.PiperTtsPath),
            defaultVoice = _options.PiperDefaultVoice,
            timeoutSeconds = _options.PiperRequestTimeoutSeconds
        });

        var message = initialized
            ? "Voice state retrieved."
            : "Voice state retrieved (voice support not initialized).";

        return Task.FromResult(DataToolResult.OkResult(message, payload));
    }

    public async Task<BotToolResult> SayAsync(
        string text,
        string? voice,
        int? speaker,
        int? speakerId,
        float? lengthScale,
        float? noiseScale,
        float? noiseW,
        float? sentenceSilence,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return BotToolResult.Fail("text is required.");
        }

        if (!_voiceRoutingEnabled)
        {
            return BotToolResult.Fail("Voice routing is disabled. Enable it with Voice(true) first.");
        }

        await _voicePlaybackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteLockedAsync(async (client, token) =>
            {
                var backend = await EnsureVoiceBackendConnectedAsync(client, token).ConfigureAwait(false);
                if (!backend.Ok)
                {
                    return BotToolResult.Fail(backend.Message);
                }

                var resolvedVoice = string.IsNullOrWhiteSpace(voice) ? _options.PiperDefaultVoice : voice.Trim();
                string? wavPath = null;
                try
                {
                    wavPath = await SynthesizeSpeechToWavAsync(
                        text,
                        resolvedVoice,
                        speaker,
                        speakerId,
                        lengthScale,
                        noiseScale,
                        noiseW,
                        sentenceSilence,
                        token).ConfigureAwait(false);

                    if (string.Equals(_activeVoiceBackend, "webrtc", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_webRtcVoice == null)
                        {
                            return BotToolResult.Fail("WebRTC voice backend is unavailable.");
                        }

                        _webRtcVoice.PlayWavAsMic(wavPath, loop: false);

                        var duration = EstimateWavDuration(wavPath);
                        var wait = duration > TimeSpan.Zero
                            ? duration + TimeSpan.FromMilliseconds(350)
                            : TimeSpan.FromSeconds(4);

                        await Task.Delay(wait, token).ConfigureAwait(false);
                        _webRtcVoice.StopWavAsMic();
                        return BotToolResult.OkResult($"Spoke via {_activeVoiceBackend} using voice '{resolvedVoice}'.");
                    }

                    return BotToolResult.Fail($"Voice backend '{_activeVoiceBackend}' does not support WAV playback yet.");
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(wavPath))
                    {
                        try
                        {
                            File.Delete(wavPath);
                        }
                        catch
                        {
                            // Ignore temp cleanup errors.
                        }
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _voicePlaybackGate.Release();
        }
    }

    private async Task<(bool Ok, string Message)> EnsureVoiceBackendConnectedAsync(GridClient client, CancellationToken cancellationToken)
    {
        var backend = NormalizeVoiceBackend(_options.VoiceBackend);
        if (!string.Equals(backend, "webrtc", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Unsupported voice backend '{backend}'. Only 'webrtc' is available.");
        }

        switch (backend)
        {
            case "webrtc":
                {
                    WebRtcVoiceManager manager;
                    lock (_voiceStateLock)
                    {
                        manager = _webRtcVoice ??= new WebRtcVoiceManager(client);
                    }

                    try
                    {
                        var connected = await manager.ConnectPrimaryRegionAsync().ConfigureAwait(false);
                        if (!connected)
                        {
                            return (false, "WebRTC voice backend failed to connect for current parcel/region.");
                        }

                        lock (_voiceStateLock)
                        {
                            _activeVoiceBackend = "webrtc";
                        }

                        return (true, "Voice routing enabled using WebRTC backend.");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        return (false, $"WebRTC voice backend is unavailable in this region/client session: {ex.Message}");
                    }
                }
            default:
                return (false, $"Unsupported voice backend '{backend}'. Only 'webrtc' is available.");
        }
    }

    private async Task<string> SynthesizeSpeechToWavAsync(
        string text,
        string voice,
        int? speaker,
        int? speakerId,
        float? lengthScale,
        float? noiseScale,
        float? noiseW,
        float? sentenceSilence,
        CancellationToken cancellationToken)
    {
        var client = _piperHttp;
        if (client == null)
        {
            throw new InvalidOperationException("Voice support is not initialized.");
        }

        var requestPayload = new JsonObject
        {
            ["text"] = text,
            ["voice"] = voice
        };

        if (speaker.HasValue)
        {
            requestPayload["speaker"] = speaker.Value;
        }

        if (speakerId.HasValue)
        {
            requestPayload["speaker_id"] = speakerId.Value;
            if (!speaker.HasValue)
            {
                requestPayload["speaker"] = speakerId.Value;
            }
        }

        if (lengthScale.HasValue)
        {
            requestPayload["length_scale"] = lengthScale.Value;
        }

        if (noiseScale.HasValue)
        {
            requestPayload["noise_scale"] = noiseScale.Value;
        }

        if (noiseW.HasValue)
        {
            requestPayload["noise_w"] = noiseW.Value;
        }

        if (sentenceSilence.HasValue)
        {
            requestPayload["sentence_silence"] = sentenceSilence.Value;
        }

        var requestJson = requestPayload.ToJsonString();
        var requestUrl = client.BaseAddress == null
            ? _options.PiperTtsPath
            : new Uri(client.BaseAddress, _options.PiperTtsPath).ToString();
        Console.WriteLine($"[voice][debug] Piper synth request: POST {requestUrl}");
        Console.WriteLine($"[voice][debug] Piper synth payload: {requestJson}");

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.PiperTtsPath)
        {
            // StringContent computes Content-Length; opensim-piper currently reads body by Content-Length.
            Content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = TrimForMessage(System.Text.Encoding.UTF8.GetString(audioBytes), 300);
            throw new InvalidOperationException($"Piper synthesis failed: {(int)response.StatusCode} {response.ReasonPhrase}. Detail: {errorBody}");
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"opensim-piper-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(tempFile, audioBytes, cancellationToken).ConfigureAwait(false);
        return tempFile;
    }

    private static TimeSpan EstimateWavDuration(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs);

            if (new string(reader.ReadChars(4)) != "RIFF")
            {
                return TimeSpan.Zero;
            }

            _ = reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE")
            {
                return TimeSpan.Zero;
            }

            int channels = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;
            int dataBytes = 0;

            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                var chunkId = new string(reader.ReadChars(4));
                var chunkSize = reader.ReadInt32();
                if (chunkSize < 0)
                {
                    break;
                }

                if (chunkId == "fmt ")
                {
                    _ = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    _ = reader.ReadInt32();
                    _ = reader.ReadInt16();
                    bitsPerSample = reader.ReadInt16();

                    var remaining = chunkSize - 16;
                    if (remaining > 0)
                    {
                        reader.BaseStream.Seek(remaining, SeekOrigin.Current);
                    }
                }
                else if (chunkId == "data")
                {
                    dataBytes = chunkSize;
                    reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
                }
                else
                {
                    reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
                }

                if ((chunkSize & 1) != 0)
                {
                    reader.BaseStream.Seek(1, SeekOrigin.Current);
                }
            }

            if (channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0 || dataBytes <= 0)
            {
                return TimeSpan.Zero;
            }

            var bytesPerSample = (bitsPerSample / 8.0) * channels;
            if (bytesPerSample <= 0)
            {
                return TimeSpan.Zero;
            }

            var seconds = dataBytes / (sampleRate * bytesPerSample);
            if (seconds <= 0)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(seconds);
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private Uri BuildPiperBaseUri()
    {
        var builder = new UriBuilder
        {
            Scheme = _options.PiperScheme,
            Host = _options.PiperHost,
            Port = _options.PiperPort,
            Path = "/"
        };

        return builder.Uri;
    }

    private string BuildPiperEndpoint(string path)
    {
        var baseUri = BuildPiperBaseUri().ToString().TrimEnd('/');
        return baseUri + path;
    }

    private static string NormalizeVoiceBackend(string backend)
    {
        var normalized = (backend ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length == 0 ? "webrtc" : normalized;
    }

    private string GetConfiguredBackendLabel() => NormalizeVoiceBackend(_options.VoiceBackend);

    private string BuildVoiceStatusText()
    {
        var lines = new List<string>
        {
            "Voice status:",
            $"routing enabled: {_voiceRoutingEnabled}",
            $"configured backend: {GetConfiguredBackendLabel()}",
            $"active backend: {_activeVoiceBackend}",
            $"piper endpoint: {BuildPiperEndpoint(_options.PiperTtsPath)}",
            $"default voice: {_options.PiperDefaultVoice}",
            $"timeout seconds: {_options.PiperRequestTimeoutSeconds}"
        };

        if (!_voiceRoutingEnabled)
        {
            lines.Add("Tip: run *voice on to enable speech routing.");
        }

        return string.Join("\n", lines);
    }

    private static bool TryParseSayStarCommand(string arg, out string text, out string? voiceOverride, out string error)
    {
        text = string.Empty;
        voiceOverride = null;
        error = "Usage: *say <text> OR *say voice=<voice-name> <text>";

        if (string.IsNullOrWhiteSpace(arg))
        {
            return false;
        }

        var textTokens = new List<string>();
        var parts = arg.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.StartsWith("voice=", StringComparison.OrdinalIgnoreCase))
            {
                var value = part["voice=".Length..].Trim();
                if (value.Length == 0)
                {
                    error = "voice= requires a non-empty voice name. Usage: *say voice=<voice-name> <text>";
                    return false;
                }

                voiceOverride = value;
                continue;
            }

            textTokens.Add(part);
        }

        text = string.Join(' ', textTokens).Trim();
        if (text.Length == 0)
        {
            error = "No speech text provided. Usage: *say <text> OR *say voice=<voice-name> <text>";
            return false;
        }

        return true;
    }

    private static PiperVoicesResponse ParsePiperVoicesPayload(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var voices = new List<string>();
            if (root.TryGetProperty("voices", out var voicesElement) && voicesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in voicesElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            voices.Add(value.Trim());
                        }
                    }
                }
            }

            string? defaultVoice = null;
            if (root.TryGetProperty("default_voice", out var defaultVoiceElement) && defaultVoiceElement.ValueKind == JsonValueKind.String)
            {
                defaultVoice = defaultVoiceElement.GetString();
            }
            else if (root.TryGetProperty("defaultVoice", out var altDefaultVoiceElement) && altDefaultVoiceElement.ValueKind == JsonValueKind.String)
            {
                defaultVoice = altDefaultVoiceElement.GetString();
            }

            return new PiperVoicesResponse(voices, defaultVoice);
        }
        catch
        {
            return new PiperVoicesResponse(Array.Empty<string>(), null);
        }
    }

    private static JsonElement? TryParseJsonElement(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string TrimForMessage(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        return trimmed[..maxChars] + "...";
    }
}