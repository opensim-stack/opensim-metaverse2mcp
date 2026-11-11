using System.Globalization;
using System.Net;
using System.Net.Sockets;
using LibreMetaverse;
using LibreMetaverse.Interfaces;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.Packets;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private static readonly TimeSpan HypergridMapResolveTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan EventQueueReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LureTeleportInitiationTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PostTeleportSimPruneDelay = TimeSpan.FromSeconds(4);
    private const float DefaultHopCoordinateX = 128f;
    private const float DefaultHopCoordinateY = 128f;
    private const float DefaultHopCoordinateZ = 2f;
    private const uint StaleSimulatorDistanceThresholdMeters = 4096u;

    internal sealed record ParsedTeleportDestination(
        string OriginalInput,
        bool IsHypergrid,
        string? Scheme,
        string? WireScheme,
        string? Host,
        int Port,
        string RegionName,
        bool HasCoordinates,
        Vector3 Position);

    internal sealed record ResolvedMapRegion(
        ulong RegionHandle,
        string RegionName,
        SimAccess Access,
        ushort GridX,
        ushort GridY);

    /// <summary>
    /// Returns true if the input string looks like a URL (hop://, secondlife://, http(s)://, x-grid-location-info://, hg://)
    /// or an OpenSim Hypergrid region descriptor (host:port:RegionName / host:port/RegionName).
    /// </summary>
    internal static bool LooksLikeHypergridOrUrlTarget(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal) || trimmed.Contains("|!!", StringComparison.Ordinal))
        {
            return true;
        }

        // OpenSim shorthand: host.tld:8002:RegionName or host.tld:8002/RegionName or host:8002:RegionName
        var slashOrColon = trimmed.IndexOfAny([':', '/']);
        if (slashOrColon <= 0)
        {
            return false;
        }

        var hostCandidate = trimmed[..slashOrColon].Trim();
        if (hostCandidate.Length == 0 || hostCandidate.Contains(' '))
        {
            return false;
        }

        if (trimmed[slashOrColon] == ':')
        {
            var afterColon = trimmed[(slashOrColon + 1)..];
            var portLen = 0;
            while (portLen < afterColon.Length && char.IsAsciiDigit(afterColon[portLen]))
            {
                portLen++;
            }

            if (portLen > 0)
            {
                return true;
            }
        }

        return hostCandidate.Contains('.', StringComparison.Ordinal) && trimmed[slashOrColon] == '/';
    }

    /// <summary>
    /// Parses hop://, hops://, hg://, x-grid-location-info://, secondlife://, http(s):// SLURLs,
    /// and OpenSim host:port[:/]RegionName[/x/y/z] destinations.
    /// </summary>
    internal static bool TryParseTeleportDestination(
        string? rawInput,
        out ParsedTeleportDestination? destination,
        out string error)
    {
        destination = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawInput))
        {
            error = "URL or destination is required.";
            return false;
        }

        var trimmed = rawInput.Trim();
        if (trimmed.Equals("home", StringComparison.OrdinalIgnoreCase))
        {
            error = "Use TeleportHome to teleport to the bot's home location.";
            return false;
        }

        string? scheme = null;
        var rest = trimmed;

        var schemeIdx = rest.IndexOf("://", StringComparison.Ordinal);
        var schemeSepLen = 3;
        if (schemeIdx < 0)
        {
            schemeIdx = rest.IndexOf("|!!", StringComparison.Ordinal);
        }

        if (schemeIdx == 0)
        {
            error = $"Invalid URL scheme in '{trimmed}'.";
            return false;
        }

        if (schemeIdx > 0)
        {
            // Only treat as a scheme if it appears before any '/' or space, and isn't a host:port prefix
            var firstSlash = rest.IndexOf('/');
            if (firstSlash < 0 || schemeIdx < firstSlash)
            {
                scheme = rest[..schemeIdx].Trim().ToLowerInvariant();
                rest = rest[(schemeIdx + schemeSepLen)..];
            }
        }

        if (string.Equals(scheme, "secondlife", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseSecondLifeSlurlRemainder(trimmed, rest, out destination, out error);
        }

        var isSecure = false;
        if (scheme != null)
        {
            switch (scheme)
            {
                case "hop":
                case "hg":
                case "http":
                case "surl":
                case "x-grid-location-info":
                case "x-grid-info":
                    isSecure = false;
                    break;
                case "hops":
                case "hgs":
                case "https":
                case "surls":
                case "x-grid-location-infos":
                case "x-grid-infos":
                    isSecure = true;
                    break;
                default:
                    error = $"Unsupported teleport URL scheme '{scheme}://'. Supported schemes: hop://, hops://, hg://, x-grid-location-info://, secondlife://, http://, https://.";
                    return false;
            }
        }

        // If no scheme was provided and it does not look like host:port or host.tld/..., treat as a plain local region[/x/y/z].
        if (scheme == null && !LooksLikeHypergridOrUrlTarget(trimmed))
        {
            return TryParseRegionAndCoordinatesRemainder(
                originalInput: trimmed,
                isHypergrid: false,
                scheme: null,
                wireScheme: null,
                host: null,
                port: 0,
                remainder: rest,
                stripRegionPrefixAlways: false,
                out destination,
                out error);
        }

        // Parse optional userinfo@ prefix before host
        var firstDelimiter = rest.IndexOfAny(['/', ':', '|']);
        var atIdx = rest.IndexOf('@');
        if (atIdx >= 0 && (firstDelimiter < 0 || atIdx < firstDelimiter))
        {
            rest = rest[(atIdx + 1)..];
        }

        if (string.IsNullOrWhiteSpace(rest))
        {
            error = $"Missing host in teleport URL '{trimmed}'.";
            return false;
        }

        string host;
        var port = isSecure ? 443 : 80;
        string pathRemainder;

        if (rest.StartsWith('['))
        {
            var closeBracket = rest.IndexOf(']');
            if (closeBracket <= 1)
            {
                error = $"Invalid IPv6 host in teleport URL '{trimmed}'.";
                return false;
            }

            host = rest[..(closeBracket + 1)];
            var afterHost = rest[(closeBracket + 1)..];
            if (!TryConsumeOptionalPort(afterHost, ref port, out pathRemainder, out error))
            {
                return false;
            }
        }
        else
        {
            var hostEnd = rest.IndexOfAny([':', '|', '/', ' ']);
            if (hostEnd < 0)
            {
                host = rest;
                pathRemainder = string.Empty;
            }
            else
            {
                host = rest[..hostEnd];
                var afterHost = rest[hostEnd..];
                if (!TryConsumeOptionalPort(afterHost, ref port, out pathRemainder, out error))
                {
                    return false;
                }
            }
        }

        host = UnescapeUrlComponent(host).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host))
        {
            error = $"Empty host in teleport URL '{trimmed}'.";
            return false;
        }

        // Handle Second Life web SLURLs (maps.secondlife.com / slurl.com) as local-grid region SLURLs
        if (host is "maps.secondlife.com" or "slurl.com" or "www.slurl.com")
        {
            return TryParseRegionAndCoordinatesRemainder(
                originalInput: trimmed,
                isHypergrid: false,
                scheme: scheme,
                wireScheme: null,
                host: null,
                port: 0,
                remainder: pathRemainder,
                stripRegionPrefixAlways: true,
                out destination,
                out error);
        }

        var stripRegionPrefix = scheme is "http" or "https" or "x-grid-location-info" or "x-grid-location-infos";
        var wireScheme = isSecure ? "https://" : "http://";

        return TryParseRegionAndCoordinatesRemainder(
            originalInput: trimmed,
            isHypergrid: true,
            scheme: scheme,
            wireScheme: wireScheme,
            host: host,
            port: port,
            remainder: pathRemainder,
            stripRegionPrefixAlways: stripRegionPrefix,
            out destination,
            out error);
    }

    private static bool TryConsumeOptionalPort(
        string afterHost,
        ref int port,
        out string remainder,
        out string error)
    {
        remainder = afterHost;
        error = string.Empty;

        if (string.IsNullOrEmpty(afterHost))
        {
            return true;
        }

        if (afterHost[0] is ':' or '|')
        {
            var idx = 1;
            var portDigits = 0;
            var parsedPort = 0;
            while (idx < afterHost.Length && char.IsAsciiDigit(afterHost[idx]))
            {
                parsedPort = (parsedPort * 10) + (afterHost[idx] - '0');
                if (parsedPort > 65535)
                {
                    error = "Port in teleport URL must be between 1 and 65535.";
                    return false;
                }

                idx++;
                portDigits++;
            }

            if (portDigits > 0)
            {
                if (parsedPort <= 0)
                {
                    error = "Port in teleport URL must be between 1 and 65535.";
                    return false;
                }

                port = parsedPort;
                remainder = afterHost[idx..];
                return true;
            }

            // Colon without digits (e.g. host:RegionName)
            remainder = afterHost[1..];
        }

        return true;
    }

    private static bool TryParseSecondLifeSlurlRemainder(
        string originalInput,
        string rest,
        out ParsedTeleportDestination? destination,
        out string error)
    {
        destination = null;
        error = string.Empty;

        var trimmedRest = rest.TrimStart('/');
        if (string.IsNullOrWhiteSpace(trimmedRest))
        {
            error = $"Missing region name in SLURL '{originalInput}'.";
            return false;
        }

        return TryParseRegionAndCoordinatesRemainder(
            originalInput: originalInput,
            isHypergrid: false,
            scheme: "secondlife",
            wireScheme: null,
            host: null,
            port: 0,
            remainder: trimmedRest,
            stripRegionPrefixAlways: false,
            out destination,
            out error);
    }

    private static bool TryParseRegionAndCoordinatesRemainder(
        string originalInput,
        bool isHypergrid,
        string? scheme,
        string? wireScheme,
        string? host,
        int port,
        string remainder,
        bool stripRegionPrefixAlways,
        out ParsedTeleportDestination? destination,
        out string error)
    {
        destination = null;
        error = string.Empty;

        // Strip query/fragment if present
        var queryOrFragment = remainder.IndexOfAny(['?', '#']);
        if (queryOrFragment >= 0)
        {
            remainder = remainder[..queryOrFragment];
        }

        // Trim OpenSim region separators between host:port and region name (':', '/', ' ', '|', '+')
        var cleaned = remainder.TrimStart(':', '/', ' ', '|', '+').TrimEnd();
        if (string.IsNullOrEmpty(cleaned))
        {
            if (!isHypergrid)
            {
                error = $"Missing region name in '{originalInput}'.";
                return false;
            }

            // Valid Hypergrid root/default-region URL: e.g. hop://grid.example.com:8002/
            destination = new ParsedTeleportDestination(
                OriginalInput: originalInput,
                IsHypergrid: true,
                Scheme: scheme,
                WireScheme: wireScheme,
                Host: host,
                Port: port,
                RegionName: string.Empty,
                HasCoordinates: false,
                Position: new Vector3(DefaultHopCoordinateX, DefaultHopCoordinateY, DefaultHopCoordinateZ));
            return true;
        }

        // Support OpenSim parenthesized coordinates: RegionName(128,128,25)
        Vector3? parenCoords = null;
        var openParen = cleaned.IndexOf('(');
        if (openParen > 0 && cleaned.EndsWith(')'))
        {
            var coordBody = cleaned[(openParen + 1)..^1];
            var coordParts = coordBody.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (coordParts.Length >= 2
                && TryParseFloatInvariant(coordParts[0], out var px)
                && TryParseFloatInvariant(coordParts[1], out var py))
            {
                var pz = coordParts.Length >= 3 && TryParseFloatInvariant(coordParts[2], out var parsedPz)
                    ? parsedPz
                    : DefaultHopCoordinateZ;
                parenCoords = new Vector3(px, py, pz);
                cleaned = cleaned[..openParen].TrimEnd();
            }
        }

        var segments = cleaned
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (segments.Count == 0)
        {
            if (!isHypergrid)
            {
                error = $"Missing region name in '{originalInput}'.";
                return false;
            }

            destination = new ParsedTeleportDestination(
                OriginalInput: originalInput,
                IsHypergrid: true,
                Scheme: scheme,
                WireScheme: wireScheme,
                Host: host,
                Port: port,
                RegionName: string.Empty,
                HasCoordinates: parenCoords.HasValue,
                Position: parenCoords ?? new Vector3(DefaultHopCoordinateX, DefaultHopCoordinateY, DefaultHopCoordinateZ));
            return true;
        }

        // Strip /app/teleport/ prefix (e.g. secondlife:///app/teleport/Region/128/128/25 or hop://host/app/teleport/Region/...)
        if (segments.Count >= 2
            && segments[0].Equals("app", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("teleport", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveRange(0, 2);
            // In secondlife://<grid>/app/teleport/<region>/... or hop://.../app/teleport/...
        }
        else if (segments.Count >= 3
            && segments[1].Equals("app", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("teleport", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveRange(0, 3);
        }
        else if (segments[0].Equals("region", StringComparison.OrdinalIgnoreCase)
            || segments[0].Equals("secondlife", StringComparison.OrdinalIgnoreCase))
        {
            if (stripRegionPrefixAlways)
            {
                segments.RemoveAt(0);
            }
            else if (segments.Count >= 2 && !TryParseFloatInvariant(segments[1], out _))
            {
                // e.g. hop://host:8002/region/MyRegion/128/128/25 (next token is a region name, not X coordinate)
                segments.RemoveAt(0);
            }
        }

        if (segments.Count == 0)
        {
            error = $"Missing region name after URL prefix in '{originalInput}'.";
            return false;
        }

        var regionName = StripNonPrintable(UnescapeUrlComponent(segments[0]).Trim());
        if (string.IsNullOrWhiteSpace(regionName) && !isHypergrid)
        {
            error = $"Region name in '{originalInput}' is empty.";
            return false;
        }

        var hasCoordinates = parenCoords.HasValue;
        var position = parenCoords ?? new Vector3(DefaultHopCoordinateX, DefaultHopCoordinateY, DefaultHopCoordinateZ);

        if (segments.Count >= 3
            && TryParseFloatInvariant(segments[1], out var sx)
            && TryParseFloatInvariant(segments[2], out var sy))
        {
            var sz = DefaultHopCoordinateZ;
            if (segments.Count >= 4 && TryParseFloatInvariant(segments[3], out var parsedSz))
            {
                sz = parsedSz;
            }

            hasCoordinates = true;
            position = new Vector3(sx, sy, sz);
        }
        else if (segments.Count == 2 && TryParseFloatInvariant(segments[1], out _))
        {
            error = $"Incomplete coordinates in '{originalInput}'; provide at least X and Y (e.g. /Region/128/128/25).";
            return false;
        }

        destination = new ParsedTeleportDestination(
            OriginalInput: originalInput,
            IsHypergrid: isHypergrid,
            Scheme: scheme,
            WireScheme: wireScheme,
            Host: host,
            Port: port,
            RegionName: regionName,
            HasCoordinates: hasCoordinates,
            Position: position);
        return true;
    }

    private static string UnescapeUrlComponent(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        try
        {
            return Uri.UnescapeDataString(raw.Replace('+', ' '));
        }
        catch
        {
            return raw.Replace("%20", " ", StringComparison.OrdinalIgnoreCase).Replace('+', ' ');
        }
    }

    private static string StripNonPrintable(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return new string(value.Where(c => !char.IsControl(c)).ToArray());
    }

    private static bool TryParseFloatInvariant(string text, out float value)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public async Task<BotToolResult> TeleportToUrlAsync(
        string url,
        float? x,
        float? y,
        float? z,
        CancellationToken cancellationToken)
    {
        if (!TryParseTeleportDestination(url, out var parsed, out var parseError) || parsed == null)
        {
            return BotToolResult.Fail(parseError);
        }

        return await ExecuteLockedAsync(
            (client, token) => ExecuteParsedTeleportAsync(client, parsed, x, y, z, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TeleportHomeAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var currentSim = client.Network.CurrentSim;
            if (currentSim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            await EnsureEventQueueReadyForTeleportAsync(client, token).ConfigureAwait(false);
            PruneStaleNonNeighborSimulators(client, "pre-teleport-home");

            var preSim = DescribeSimulator(currentSim);
            var preTeleportSimHandle = currentSim.Handle;
            var preTeleportPosition = client.Self.SimPosition;

            string? capturedAlert = null;
            var earlyFailureTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void AlertHandler(object? _, AlertMessageEventArgs e)
            {
                if (!string.IsNullOrWhiteSpace(e.Message))
                {
                    capturedAlert = e.Message.Trim();
                }
            }

            void FailedPacketHandler(object? _, PacketReceivedEventArgs e)
            {
                if (e.Packet is TeleportFailedPacket failed)
                {
                    var reason = Utils.BytesToString(failed.Info.Reason)?.Trim();
                    if (!string.IsNullOrWhiteSpace(reason))
                    {
                        earlyFailureTcs.TrySetResult(reason);
                    }
                }
            }

            void FailedCapsHandler(string _, IMessage message, Simulator __)
            {
                if (message is TeleportFailedMessage failed && !string.IsNullOrWhiteSpace(failed.Reason))
                {
                    earlyFailureTcs.TrySetResult(failed.Reason.Trim());
                }
            }

            Console.WriteLine($"[teleport] Requesting TeleportHome from {preSim} at {FormatVector(preTeleportPosition)}.");

            client.Self.AlertMessage += AlertHandler;
            client.Network.RegisterCallback(PacketType.TeleportFailed, FailedPacketHandler);
            client.Network.RegisterEventCallback("TeleportFailed", FailedCapsHandler);
            bool ok;
            string? earlyFailureReason = null;
            try
            {
                using var cancelGoHomeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var goHomeTask = client.Self.GoHomeAsync(cancelGoHomeCts.Token);
                var winner = await Task.WhenAny(goHomeTask, earlyFailureTcs.Task).ConfigureAwait(false);
                if (winner == earlyFailureTcs.Task)
                {
                    earlyFailureReason = await earlyFailureTcs.Task.ConfigureAwait(false);
                    cancelGoHomeCts.Cancel();
                    _ = goHomeTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    ok = false;
                }
                else
                {
                    ok = await goHomeTask.ConfigureAwait(false);
                }
            }
            finally
            {
                client.Self.AlertMessage -= AlertHandler;
                client.Network.UnregisterCallback(PacketType.TeleportFailed, FailedPacketHandler);
                client.Network.UnregisterEventCallback("TeleportFailed", FailedCapsHandler);
            }

            if (!ok)
            {
                var baseReason = !string.IsNullOrWhiteSpace(earlyFailureReason)
                    ? earlyFailureReason
                    : client.Self.TeleportMessage;
                var rawReason = !string.IsNullOrWhiteSpace(capturedAlert)
                    ? $"{baseReason} (simulator alert: {capturedAlert})".Trim()
                    : baseReason;
                var failMessage = BuildTeleportFailureMessage(rawReason, "home location");
                Console.WriteLine($"[teleport] TeleportHome failed from {preSim}: {failMessage}");
                EmitRuntimeEvent(
                    "teleport",
                    "teleport.failed",
                    "opensim",
                    failMessage,
                    new Dictionary<string, string?>
                    {
                        ["target"] = "home",
                        ["sourceSimulator"] = preSim,
                        ["alertMessage"] = capturedAlert
                    });
                return BotToolResult.Fail(failMessage);
            }

            var settled = await ReadSettledTeleportPositionAsync(client, preTeleportSimHandle, preTeleportPosition, token).ConfigureAwait(false);
            var postSimName = client.Network.CurrentSim?.Name ?? "unknown";
            SchedulePostTeleportSimulatorCleanup(client, "post-teleport-home");

            Console.WriteLine($"[teleport] TeleportHome succeeded: now in {DescribeSimulator(client.Network.CurrentSim)} at {FormatVector(settled)}.");
            EmitRuntimeEvent(
                "teleport",
                "teleport.succeeded",
                "opensim",
                $"Teleported home to {postSimName} at {FormatVector(settled)}.",
                new Dictionary<string, string?>
                {
                    ["targetRegion"] = postSimName,
                    ["position"] = FormatVector(settled)
                });
            return BotToolResult.OkResult($"Teleported home to {postSimName} at {FormatVector(settled)}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BotToolResult> ExecuteParsedTeleportAsync(
        GridClient client,
        ParsedTeleportDestination parsed,
        float? overrideX,
        float? overrideY,
        float? overrideZ,
        CancellationToken cancellationToken)
    {
        var currentSim = client.Network.CurrentSim;
        if (currentSim == null)
        {
            return BotToolResult.Fail("No current simulator available.");
        }

        var rawPos = new Vector3(
            overrideX ?? parsed.Position.X,
            overrideY ?? parsed.Position.Y,
            overrideZ ?? parsed.Position.Z);

        var preTeleportSimHandle = currentSim.Handle;
        var preTeleportPosition = client.Self.SimPosition;

        // Local same-region shortcut when not a Hypergrid hop
        if (!parsed.IsHypergrid
            && (string.IsNullOrWhiteSpace(parsed.RegionName)
                || string.Equals(parsed.RegionName, currentSim.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var localTarget = ClampLocalPosition(rawPos, currentSim.SizeX, currentSim.SizeY);
            Console.WriteLine($"[teleport] Teleporting within current region {DescribeSimulator(currentSim)} to {FormatVector(localTarget)}.");
            var localOk = await client.Self.TeleportAsync(currentSim.Handle, localTarget, cancellationToken).ConfigureAwait(false);
            if (!localOk)
            {
                var failMsg = BuildTeleportFailureMessage(client.Self.TeleportMessage, currentSim.Name);
                Console.WriteLine($"[teleport] Local teleport failed in {currentSim.Name}: {failMsg}");
                EmitRuntimeEvent(
                    "teleport",
                    "teleport.failed",
                    "opensim",
                    failMsg,
                    new Dictionary<string, string?>
                    {
                        ["targetRegion"] = currentSim.Name,
                        ["targetPosition"] = FormatVector(localTarget)
                    });
                return BotToolResult.Fail(failMsg);
            }

            var settledLocal = await ReadSettledTeleportPositionAsync(client, preTeleportSimHandle, preTeleportPosition, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[teleport] Local teleport succeeded in {currentSim.Name} at {FormatVector(settledLocal)}.");
            EmitRuntimeEvent(
                "teleport",
                "teleport.succeeded",
                "opensim",
                $"Teleported to {currentSim.Name} at {FormatVector(settledLocal)}.",
                new Dictionary<string, string?>
                {
                    ["targetRegion"] = currentSim.Name,
                    ["position"] = FormatVector(settledLocal)
                });
            return BotToolResult.OkResult($"Teleported to {currentSim.Name} at {FormatVector(settledLocal)}.");
        }

        await EnsureEventQueueReadyForTeleportAsync(client, cancellationToken).ConfigureAwait(false);
        PruneStaleNonNeighborSimulators(client, "pre-url-teleport");

        var destinationDisplay = BuildDestinationDisplayLabel(parsed);
        Console.WriteLine($"[teleport] Resolving destination '{destinationDisplay}' via simulator {DescribeSimulator(currentSim)}...");

        var (resolvedRegion, resolveError) = await ResolveDestinationRegionOnCurrentSimAsync(client, parsed, cancellationToken).ConfigureAwait(false);
        if (resolvedRegion == null)
        {
            var failResolve = resolveError ?? $"Unable to resolve destination '{destinationDisplay}'.";
            Console.WriteLine($"[teleport] Map resolution failed for '{destinationDisplay}': {failResolve}");
            EmitRuntimeEvent(
                "teleport",
                "teleport.failed",
                "opensim",
                failResolve,
                new Dictionary<string, string?>
                {
                    ["targetRegion"] = destinationDisplay,
                    ["targetPosition"] = FormatVector(rawPos)
                });
            return BotToolResult.Fail(failResolve);
        }

        // Destination region size may be a varregion (>256m); clamp to MaxRegionExtent and let simulator enforce bounds.
        var target = ClampLocalPosition(rawPos, MaxRegionExtent, MaxRegionExtent);
        var resolvedLabel = string.IsNullOrWhiteSpace(resolvedRegion.RegionName)
            ? $"{destinationDisplay} ({resolvedRegion.RegionHandle})"
            : $"{resolvedRegion.RegionName} ({resolvedRegion.RegionHandle})";

        string? capturedAlert = null;
        var earlyFailureTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void AlertHandler(object? _, AlertMessageEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Message))
            {
                capturedAlert = e.Message.Trim();
            }
        }

        void FailedPacketHandler(object? _, PacketReceivedEventArgs e)
        {
            if (e.Packet is TeleportFailedPacket failed)
            {
                var reason = Utils.BytesToString(failed.Info.Reason)?.Trim();
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    earlyFailureTcs.TrySetResult(reason);
                }
            }
        }

        void FailedCapsHandler(string _, IMessage message, Simulator __)
        {
            if (message is TeleportFailedMessage failed && !string.IsNullOrWhiteSpace(failed.Reason))
            {
                earlyFailureTcs.TrySetResult(failed.Reason.Trim());
            }
        }

        Console.WriteLine($"[teleport] Resolved '{destinationDisplay}' -> handle={resolvedRegion.RegionHandle} grid=<{resolvedRegion.GridX},{resolvedRegion.GridY}> blockName='{resolvedRegion.RegionName}'; requesting teleport to {FormatVector(target)}.");

        client.Self.AlertMessage += AlertHandler;
        client.Network.RegisterCallback(PacketType.TeleportFailed, FailedPacketHandler);
        client.Network.RegisterEventCallback("TeleportFailed", FailedCapsHandler);
        bool ok;
        string? earlyFailureReason = null;
        try
        {
            using var cancelTpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var tpTask = client.Self.TeleportAsync(resolvedRegion.RegionHandle, target, cancelTpCts.Token);
            var winner = await Task.WhenAny(tpTask, earlyFailureTcs.Task).ConfigureAwait(false);
            if (winner == earlyFailureTcs.Task)
            {
                earlyFailureReason = await earlyFailureTcs.Task.ConfigureAwait(false);
                cancelTpCts.Cancel();
                _ = tpTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                ok = false;
            }
            else
            {
                ok = await tpTask.ConfigureAwait(false);
            }
        }
        finally
        {
            client.Self.AlertMessage -= AlertHandler;
            client.Network.UnregisterCallback(PacketType.TeleportFailed, FailedPacketHandler);
            client.Network.UnregisterEventCallback("TeleportFailed", FailedCapsHandler);
        }

        if (!ok)
        {
            var baseFailure = !string.IsNullOrWhiteSpace(earlyFailureReason)
                ? earlyFailureReason
                : client.Self.TeleportMessage;
            var rawFailure = !string.IsNullOrWhiteSpace(capturedAlert)
                ? $"{baseFailure} (simulator alert: {capturedAlert})".Trim()
                : baseFailure;
            var message = BuildTeleportFailureMessage(rawFailure, resolvedLabel);
            Console.WriteLine($"[teleport] Teleport failed to {resolvedLabel} at {FormatVector(target)}: {message}");
            EmitRuntimeEvent(
                "teleport",
                "teleport.failed",
                "opensim",
                message,
                new Dictionary<string, string?>
                {
                    ["targetRegion"] = resolvedLabel,
                    ["targetUrl"] = parsed.OriginalInput,
                    ["targetPosition"] = FormatVector(target),
                    ["alertMessage"] = capturedAlert
                });
            return BotToolResult.Fail(message);
        }

        var settled = await ReadSettledTeleportPositionAsync(client, preTeleportSimHandle, preTeleportPosition, cancellationToken).ConfigureAwait(false);
        var postSim = client.Network.CurrentSim;
        var arrivedRegionLabel = postSim != null && !string.IsNullOrWhiteSpace(postSim.Name)
            ? $"{postSim.Name} ({postSim.Handle})"
            : resolvedLabel;

        SchedulePostTeleportSimulatorCleanup(client, "post-url-teleport");
        Console.WriteLine($"[teleport] Teleport succeeded to {arrivedRegionLabel} at {FormatVector(settled)} (requested '{destinationDisplay}').");

        EmitRuntimeEvent(
            "teleport",
            "teleport.succeeded",
            "opensim",
            $"Teleported to {arrivedRegionLabel} at {FormatVector(settled)}.",
            new Dictionary<string, string?>
            {
                ["targetRegion"] = arrivedRegionLabel,
                ["targetUrl"] = parsed.OriginalInput,
                ["position"] = FormatVector(settled)
            });

        return BotToolResult.OkResult($"Teleported to {arrivedRegionLabel} at {FormatVector(settled)}.");
    }

    private static string BuildDestinationDisplayLabel(ParsedTeleportDestination parsed)
    {
        if (!parsed.IsHypergrid)
        {
            return parsed.RegionName;
        }

        var schemePrefix = parsed.Scheme != null ? $"{parsed.Scheme}://" : "hop://";
        return string.IsNullOrWhiteSpace(parsed.RegionName)
            ? $"{schemePrefix}{parsed.Host}:{parsed.Port}/"
            : $"{schemePrefix}{parsed.Host}:{parsed.Port}/{parsed.RegionName}";
    }

    /// <summary>
    /// Resolves a local or Hypergrid region directly on CurrentSim using MapNameRequest (Flags=0 exact mode),
    /// bypassing LibreMetaverse's GridManager.GetGridRegionAsync which fails when OpenSim rewrites Hypergrid
    /// MapBlockReply names (e.g. "http://host:port/ RegionName" or default landing region names) and which
    /// otherwise retains stale cached RegionHandles from previous grids.
    /// </summary>
    private async Task<(ResolvedMapRegion? Region, string? Error)> ResolveDestinationRegionOnCurrentSimAsync(
        GridClient client,
        ParsedTeleportDestination parsed,
        CancellationToken cancellationToken)
    {
        var queryCandidates = await BuildMapNameQueryCandidatesAsync(parsed, cancellationToken).ConfigureAwait(false);
        string? lastAlert = null;

        foreach (var query in queryCandidates)
        {
            var (region, alert) = await SendMapNameRequestAndAwaitBlockAsync(
                client,
                query,
                parsed.RegionName,
                parsed.IsHypergrid,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(alert))
            {
                lastAlert = alert;
            }

            if (region != null)
            {
                if (region.Access == SimAccess.Down)
                {
                    return (null, $"Destination region '{BuildDestinationDisplayLabel(parsed)}' is reported offline/down.");
                }

                return (region, null);
            }
        }

        var display = BuildDestinationDisplayLabel(parsed);
        if (!string.IsNullOrWhiteSpace(lastAlert))
        {
            return (null, $"Unable to resolve destination '{display}': {lastAlert}");
        }

        if (parsed.IsHypergrid && !string.IsNullOrEmpty(parsed.Host) && !parsed.Host.Contains('.', StringComparison.Ordinal))
        {
            return (
                null,
                $"Unable to resolve Hypergrid destination '{display}'. Note: OpenSim's RegionURI parser requires a dot in the gatekeeper hostname (FQDN or IPv4 address) rather than single-label host '{parsed.Host}'.");
        }

        return (
            null,
            $"Unable to resolve destination '{display}'. Verify the grid gatekeeper/region name is reachable from simulator '{client.Network.CurrentSim?.Name ?? "unknown"}' and HypergridLinker/AllowHypergridMapSearch are enabled.");
    }

    private static async Task<IReadOnlyList<string>> BuildMapNameQueryCandidatesAsync(
        ParsedTeleportDestination parsed,
        CancellationToken cancellationToken)
    {
        if (!parsed.IsHypergrid)
        {
            return [parsed.RegionName];
        }

        var wireScheme = parsed.WireScheme ?? "http://";
        var host = parsed.Host ?? string.Empty;
        var suffix = string.IsNullOrWhiteSpace(parsed.RegionName)
            ? "/"
            : $"/{parsed.RegionName}";

        var candidates = new List<string>();

        // OpenSim's RegionURI.Parse rejects single-label hostnames without a '.' (firstDot <= 0).
        // If host has no dot (e.g. Docker container name or LAN short hostname), try DNS IPv4 resolution first.
        if (!host.Contains('.', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 != null)
                {
                    Console.WriteLine($"[teleport] Resolved single-label Hypergrid host '{host}' -> {ipv4} for OpenSim RegionURI compatibility.");
                    candidates.Add($"{wireScheme}{ipv4}:{parsed.Port}{suffix}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[teleport] DNS lookup for single-label host '{host}' failed: {ex.Message}");
            }
        }

        var primary = $"{wireScheme}{host}:{parsed.Port}{suffix}";
        if (!candidates.Contains(primary, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(primary);
        }

        return candidates;
    }

    private static async Task<(ResolvedMapRegion? Region, string? AlertMessage)> SendMapNameRequestAndAwaitBlockAsync(
        GridClient client,
        string queryString,
        string expectedRegionName,
        bool isHypergrid,
        CancellationToken cancellationToken)
    {
        var collected = new List<ResolvedMapRegion>();
        var sync = new object();
        string? alertText = null;
        var completionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void PacketHandler(object? _, PacketReceivedEventArgs e)
        {
            if (e.Packet is not MapBlockReplyPacket reply || reply.Data == null)
            {
                return;
            }

            var sawClosingSentinel = false;
            lock (sync)
            {
                foreach (var block in reply.Data)
                {
                    var blockName = Utils.BytesToString(block.Name)?.Trim() ?? string.Empty;
                    if (block.X == 0 && block.Y == 0)
                    {
                        // OpenSim MapSearchModule appends a closing sentinel block (X=0, Y=0, Name=mapNameOrig)
                        if (string.Equals(blockName, queryString, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrWhiteSpace(expectedRegionName)
                                && string.Equals(blockName, expectedRegionName, StringComparison.OrdinalIgnoreCase)))
                        {
                            sawClosingSentinel = true;
                        }

                        continue;
                    }

                    if ((SimAccess)block.Access == SimAccess.NonExistent)
                    {
                        continue;
                    }

                    var handle = Utils.UIntsToLong((uint)block.X * 256u, (uint)block.Y * 256u);
                    var candidate = new ResolvedMapRegion(
                        RegionHandle: handle,
                        RegionName: blockName,
                        Access: (SimAccess)block.Access,
                        GridX: block.X,
                        GridY: block.Y);

                    collected.Add(candidate);
                    Console.WriteLine(
                        $"[teleport][map] MapBlockReply candidate for '{queryString}': name='{blockName}' grid=<{block.X},{block.Y}> handle={handle} access={(SimAccess)block.Access}");

                    if (IsExactMapBlockMatch(candidate, queryString, expectedRegionName, isHypergrid))
                    {
                        completionTcs.TrySetResult(true);
                        return;
                    }
                }
            }

            if (sawClosingSentinel)
            {
                completionTcs.TrySetResult(true);
            }
        }

        void AlertHandler(object? _, AlertMessageEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Message))
            {
                alertText = e.Message.Trim();
            }
        }

        client.Network.RegisterCallback(PacketType.MapBlockReply, PacketHandler);
        client.Self.AlertMessage += AlertHandler;
        try
        {
            var packet = new MapNameRequestPacket
            {
                AgentData =
                {
                    AgentID = client.Self.AgentID,
                    SessionID = client.Self.SessionID,
                    // Flags = 0 (EXACT_FLAG) prevents OpenSim from mangling multi-match names (see Firestorm FIRE-31368).
                    Flags = 0,
                    EstateID = 0,
                    Godlike = false
                },
                NameData =
                {
                    Name = Utils.StringToBytes(queryString)
                }
            };

            Console.WriteLine($"[teleport][map] Sending MapNameRequest query='{queryString}' to {DescribeSimulator(client.Network.CurrentSim)}.");
            client.Network.SendPacket(packet);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(HypergridMapResolveTimeout);

            try
            {
                await completionTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"[teleport][map] MapNameRequest timed out after {HypergridMapResolveTimeout.TotalSeconds:F0}s for query='{queryString}'.");
            }
        }
        finally
        {
            client.Network.UnregisterCallback(PacketType.MapBlockReply, PacketHandler);
            client.Self.AlertMessage -= AlertHandler;
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            return (SelectBestMapRegionMatch(collected, queryString, expectedRegionName, isHypergrid), alertText);
        }
    }

    private static bool IsExactMapBlockMatch(
        ResolvedMapRegion candidate,
        string queryString,
        string expectedRegionName,
        bool isHypergrid)
    {
        if (string.IsNullOrWhiteSpace(expectedRegionName))
        {
            // Default/welcome region query (e.g. hop://host:port/): first valid block is our target.
            return isHypergrid;
        }

        if (string.Equals(candidate.RegionName, queryString, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.RegionName, expectedRegionName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extracted = ExtractBaseRegionNameFromMapBlock(candidate.RegionName);
        return string.Equals(extracted, expectedRegionName, StringComparison.OrdinalIgnoreCase);
    }

    private static ResolvedMapRegion? SelectBestMapRegionMatch(
        IReadOnlyList<ResolvedMapRegion> candidates,
        string queryString,
        string expectedRegionName,
        bool isHypergrid)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (IsExactMapBlockMatch(candidate, queryString, expectedRegionName, isHypergrid))
            {
                return candidate;
            }
        }

        // Firestorm solo_result fallback: when querying a Hypergrid URL, OpenSim may redirect to a
        // welcome/fallback region with a different name and return a single block before the (0,0) sentinel.
        if (isHypergrid && candidates.Count == 1)
        {
            return candidates[0];
        }

        if (!string.IsNullOrWhiteSpace(expectedRegionName))
        {
            var partial = candidates.FirstOrDefault(c =>
                ExtractBaseRegionNameFromMapBlock(c.RegionName)
                    .Contains(expectedRegionName, StringComparison.OrdinalIgnoreCase));
            if (partial != null)
            {
                return partial;
            }
        }

        return null;
    }

    internal static string ExtractBaseRegionNameFromMapBlock(string? blockName)
    {
        if (string.IsNullOrWhiteSpace(blockName))
        {
            return string.Empty;
        }

        var s = blockName.Trim();
        var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            s = s[(schemeIdx + 3)..];
        }

        // OpenSim RegionURI.RegionHostPortSpaceName uses "<host>:<port>/ <RegionName>"
        var slashSpaceIdx = s.IndexOf("/ ", StringComparison.Ordinal);
        if (slashSpaceIdx >= 0)
        {
            return s[(slashSpaceIdx + 2)..].Trim();
        }

        // Alternate "<RegionName> @ <host>:<port>" display format
        var atIdx = s.IndexOf('@');
        if (atIdx > 0)
        {
            return s[..atIdx].Trim();
        }

        var lastSlash = s.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            return s[(lastSlash + 1)..].Trim();
        }

        var lastColon = s.LastIndexOf(':');
        if (lastColon > 0 && lastColon + 1 < s.Length && !s[(lastColon + 1)..].All(char.IsAsciiDigit))
        {
            return s[(lastColon + 1)..].Trim();
        }

        return s;
    }

    internal static string? ExtractLureGatekeeperUrl(string? lureMessage)
    {
        if (string.IsNullOrWhiteSpace(lureMessage))
        {
            return null;
        }

        var atIdx = lureMessage.LastIndexOf('@');
        if (atIdx < 0 || atIdx + 1 >= lureMessage.Length)
        {
            return null;
        }

        var candidate = lureMessage[(atIdx + 1)..].Trim();
        if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        return null;
    }

    internal static string? BuildGatekeeperReachabilityHint(string? gatekeeperUrl)
    {
        if (string.IsNullOrWhiteSpace(gatekeeperUrl) || !Uri.TryCreate(gatekeeperUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::1", StringComparison.OrdinalIgnoreCase))
        {
            return $"Lure advertised loopback gatekeeper '{gatekeeperUrl}', which a remote Hypergrid simulator cannot reach.";
        }

        if (!host.Contains('.', StringComparison.Ordinal) && uri.HostNameType == UriHostNameType.Dns)
        {
            return $"Lure advertised single-label internal hostname '{gatekeeperUrl}', which external Hypergrid simulators likely cannot resolve.";
        }

        return null;
    }

    /// <summary>
    /// Ensures the current simulator's CAPS EventQueue is running before initiating teleports or accepting lures
    /// (OpenSim delivers EnableSimulator, EstablishAgentCommunication, TeleportFinish, and TeleportFailed over EventQueue).
    /// </summary>
    internal static async Task EnsureEventQueueReadyForTeleportAsync(GridClient client, CancellationToken cancellationToken)
    {
        var curSim = client.Network.CurrentSim;
        if (curSim?.Caps is { IsEventQueueRunning: true })
        {
            return;
        }

        Console.WriteLine(
            $"[teleport] EventQueue is not yet running on {DescribeSimulator(curSim)} (caps={(curSim?.Caps != null ? "present" : "null")}); waiting up to {EventQueueReadyTimeout.TotalSeconds:F0}s...");

        var queueTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        curSim?.Caps?.EventQueue?.Start();

        void QueueHandler(object? _, EventQueueRunningEventArgs e)
        {
            if (e.Simulator == client.Network.CurrentSim)
            {
                queueTcs.TrySetResult(true);
            }
        }

        client.Network.EventQueueRunning += QueueHandler;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(EventQueueReadyTimeout);
            try
            {
                await queueTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                Console.WriteLine($"[teleport] EventQueue is now running on {DescribeSimulator(client.Network.CurrentSim)}.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine(
                    $"[teleport] Warning: EventQueue still not reported running after {EventQueueReadyTimeout.TotalSeconds:F0}s on {DescribeSimulator(client.Network.CurrentSim)} (running={client.Network.CurrentSim?.Caps?.IsEventQueueRunning ?? false}).");
            }
        }
        finally
        {
            client.Network.EventQueueRunning -= QueueHandler;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Removes stale non-neighbor simulators from NetworkManager.Simulators.
    /// In LibreMetaverse, non-current simulators are never timed out if OpenSim's UDP DisableSimulator packet
    /// is lost after a teleport. Leaving a closed home-grid simulator in NetworkManager.Simulators causes
    /// EnableSimulatorHandler (`if (FindSimulator(endPoint) != null) continue;`) to skip sending UseCircuitCode
    /// when hopping back to that simulator later, causing return teleports/lures to fail cleanly or time out.
    /// </summary>
    internal static void PruneStaleNonNeighborSimulators(GridClient client, string context)
    {
        var currentSim = client.Network.CurrentSim;
        if (currentSim == null || !currentSim.Connected)
        {
            return;
        }

        Simulator[] snapshot;
        try
        {
            snapshot = client.Network.Simulators.ToArray();
        }
        catch
        {
            return;
        }

        if (snapshot.Length <= 1)
        {
            return;
        }

        Utils.LongToUInts(currentSim.Handle, out var curX, out var curY);

        foreach (var sim in snapshot)
        {
            if (sim == null || ReferenceEquals(sim, currentSim))
            {
                continue;
            }

            var shouldPrune = !sim.Connected;
            if (!shouldPrune && sim.Handle != 0 && currentSim.Handle != 0)
            {
                Utils.LongToUInts(sim.Handle, out var simX, out var simY);
                var dx = curX >= simX ? curX - simX : simX - curX;
                var dy = curY >= simY ? curY - simY : simY - curY;
                if (dx > StaleSimulatorDistanceThresholdMeters || dy > StaleSimulatorDistanceThresholdMeters)
                {
                    shouldPrune = true;
                }
            }

            if (!shouldPrune)
            {
                continue;
            }

            try
            {
                Console.WriteLine(
                    $"[teleport][sim-cleanup] ({context}) disconnecting stale non-neighbor simulator {DescribeSimulator(sim)} endpoint={sim.IPEndPoint} while current is {DescribeSimulator(currentSim)}.");
                client.Network.DisconnectSim(sim, false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[teleport][sim-cleanup] ({context}) failed to prune simulator {DescribeSimulator(sim)}: {ex.Message}");
            }
        }
    }

    internal void SchedulePostTeleportSimulatorCleanup(GridClient client, string context)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Wait past OpenSim EntityTransferModule's 2000ms pre-CloseAgent sleep so normal DisableSimulator
                // packets arrive first, then clean up any lingering far-away simulator circuits.
                await Task.Delay(PostTeleportSimPruneDelay, _lifecycleCts.Token).ConfigureAwait(false);
                if (_connected && ReferenceEquals(_client, client))
                {
                    PruneStaleNonNeighborSimulators(client, context);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[teleport][sim-cleanup] background cleanup error ({context}): {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Accepts a teleport lure and awaits actual teleport completion or failure/alert from the simulator,
    /// instead of returning immediately after sending TeleportLureRequestPacket.
    /// </summary>
    private async Task<BotToolResult> AcceptTeleportLureAndWaitAsync(
        GridClient client,
        UUID requesterAgentUuid,
        UUID sessionUuid,
        PendingTeleportMessage? matchedOffer,
        PendingTeleportMessage? latestOfferFromRequester,
        CancellationToken cancellationToken)
    {
        var currentSim = client.Network.CurrentSim;
        if (currentSim == null)
        {
            return BotToolResult.Fail("No current simulator available.");
        }

        var preSimDescription = DescribeSimulator(currentSim);
        var preTeleportSimHandle = currentSim.Handle;
        var preTeleportPosition = client.Self.SimPosition;
        var gatekeeperUrl = ExtractLureGatekeeperUrl(matchedOffer?.Message);
        var reachabilityHint = BuildGatekeeperReachabilityHint(gatekeeperUrl);

        if (matchedOffer == null)
        {
            if (latestOfferFromRequester != null && latestOfferFromRequester.SessionId != sessionUuid)
            {
                Console.WriteLine(
                    $"[teleport] Warning: accepting lure sessionId={sessionUuid} for requester={requesterAgentUuid}, which does not match latest pending sessionId={latestOfferFromRequester.SessionId} (received {latestOfferFromRequester.ReceivedAtUtc:O}).");
            }
            else
            {
                Console.WriteLine(
                    $"[teleport] Warning: accepting lure sessionId={sessionUuid} for requester={requesterAgentUuid}, but no matching pending offer was recorded in this session.");
            }
        }
        else
        {
            Console.WriteLine(
                $"[teleport] Accepting lure from {matchedOffer.FromAgentName} ({requesterAgentUuid}): sessionId={sessionUuid} regionId={matchedOffer.RegionId} pos={FormatVector(matchedOffer.Position)} gatekeeper={(gatekeeperUrl ?? "(local/none)")} currentSim={preSimDescription} message=\"{matchedOffer.Message}\".");
            if (!string.IsNullOrWhiteSpace(reachabilityHint))
            {
                Console.WriteLine($"[teleport] Warning: {reachabilityHint}");
            }
        }

        await EnsureEventQueueReadyForTeleportAsync(client, cancellationToken).ConfigureAwait(false);
        PruneStaleNonNeighborSimulators(client, "pre-lure-accept");

        var teleportStarted = 0;
        string? progressDetail = null;
        string? alertMessage = null;
        var outcomeTcs = new TaskCompletionSource<(bool Ok, string Message)>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnTeleportEvent(object? _, TeleportEventArgs e)
        {
            Console.WriteLine($"[teleport][lure] event status={e.Status} flags={e.Flags} message=\"{e.Message}\"");
            switch (e.Status)
            {
                case TeleportStatus.Start:
                case TeleportStatus.Progress:
                    Interlocked.Exchange(ref teleportStarted, 1);
                    if (!string.IsNullOrWhiteSpace(e.Message))
                    {
                        progressDetail = e.Message.Trim();
                    }
                    break;
                case TeleportStatus.Finished:
                    outcomeTcs.TrySetResult((true, e.Message));
                    break;
                case TeleportStatus.Failed:
                case TeleportStatus.Cancelled:
                    outcomeTcs.TrySetResult((false, string.IsNullOrWhiteSpace(e.Message) ? $"Teleport {e.Status.ToString().ToLowerInvariant()}." : e.Message.Trim()));
                    break;
            }
        }

        void OnAlertEvent(object? _, AlertMessageEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Message))
            {
                return;
            }

            var text = e.Message.Trim();
            alertMessage = text;
            Console.WriteLine($"[teleport][lure] simulator alert during lure accept: \"{text}\"");

            // OpenSim HGLureModule reports lure failures via SendAgentAlertMessage without sending TeleportFailed.
            if (Volatile.Read(ref teleportStarted) == 0)
            {
                outcomeTcs.TrySetResult((false, $"Simulator rejected teleport lure: {text}"));
            }
        }

        void OnRawFailedPacket(object? _, PacketReceivedEventArgs e)
        {
            if (e.Packet is TeleportFailedPacket failed)
            {
                var reason = Utils.BytesToString(failed.Info.Reason)?.Trim();
                outcomeTcs.TrySetResult((false, string.IsNullOrWhiteSpace(reason) ? "Teleport failed." : reason));
            }
        }

        void OnRawFailedCaps(string _, IMessage message, Simulator __)
        {
            if (message is TeleportFailedMessage failed)
            {
                var reason = failed.Reason?.Trim();
                outcomeTcs.TrySetResult((false, string.IsNullOrWhiteSpace(reason) ? "Teleport failed." : reason));
            }
        }

        client.Self.TeleportProgress += OnTeleportEvent;
        client.Self.AlertMessage += OnAlertEvent;
        client.Network.RegisterCallback(PacketType.TeleportFailed, OnRawFailedPacket);
        client.Network.RegisterEventCallback("TeleportFailed", OnRawFailedCaps);
        try
        {
            client.Self.TeleportLureRespond(requesterAgentUuid, sessionUuid, true);

            // Phase 1: Wait up to LureTeleportInitiationTimeout for TeleportStart/Progress/Finish/Failed/Alert.
            // If the simulator silently drops the lure (expired sessionId, non-HG LureModule lure on foreign grid, etc.),
            // fail promptly with actionable diagnostics instead of reporting a false success.
            using var initiationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            initiationCts.CancelAfter(LureTeleportInitiationTimeout);

            var completedInitial = await Task.WhenAny(
                outcomeTcs.Task,
                WaitUntilTeleportStartedOrSimChangedAsync(client, preTeleportSimHandle, () => Volatile.Read(ref teleportStarted) != 0, initiationCts.Token))
                .ConfigureAwait(false);

            if (completedInitial != outcomeTcs.Task && Volatile.Read(ref teleportStarted) == 0 && (client.Network.CurrentSim?.Handle ?? 0UL) == preTeleportSimHandle)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var hints = new List<string>();
                if (matchedOffer == null)
                {
                    if (latestOfferFromRequester != null && latestOfferFromRequester.SessionId != sessionUuid)
                    {
                        hints.Add($"sessionId {sessionUuid} did not match latest pending offer {latestOfferFromRequester.SessionId} from that requester");
                    }
                    else
                    {
                        hints.Add("lure sessionId was not in pending offers (may be expired, already used, or never delivered to this grid)");
                    }
                }
                else if (string.IsNullOrWhiteSpace(gatekeeperUrl))
                {
                    hints.Add("offer message did not include '@<gatekeeperUrl>' Hypergrid metadata (sender grid may be using non-Hypergrid LureModule instead of HGLureModule)");
                }
                else if (!string.IsNullOrWhiteSpace(reachabilityHint))
                {
                    hints.Add(reachabilityHint);
                }
                else
                {
                    hints.Add($"current simulator may have been unable to contact target gatekeeper '{gatekeeperUrl}'");
                }

                if (!string.IsNullOrWhiteSpace(alertMessage))
                {
                    hints.Add($"alert: {alertMessage}");
                }

                hints.Add("Tip: use TeleportHome to return to the bot's home grid or TeleportToUrl with a hop:// URL");

                var noStartMsg =
                    $"Teleport lure {sessionUuid} from {requesterAgentUuid} produced no teleport response within {LureTeleportInitiationTimeout.TotalSeconds:F0}s on {preSimDescription} ({string.Join("; ", hints)}).";
                Console.WriteLine($"[teleport] {noStartMsg}");
                EmitRuntimeEvent(
                    "teleport",
                    "teleport.failed",
                    "opensim",
                    noStartMsg,
                    new Dictionary<string, string?>
                    {
                        ["requesterAgentId"] = requesterAgentUuid.ToString(),
                        ["sessionId"] = sessionUuid.ToString(),
                        ["gatekeeperUrl"] = gatekeeperUrl,
                        ["sourceSimulator"] = preSimDescription,
                        ["alertMessage"] = alertMessage
                    });
                return BotToolResult.Fail(noStartMsg);
            }

            // Phase 2: Teleport has started; wait up to TeleportTimeout for final completion.
            if (!outcomeTcs.Task.IsCompleted)
            {
                var fullTimeoutMs = Math.Max(30_000, client.Settings.Timing.TeleportTimeout);
                using var fullCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                fullCts.CancelAfter(TimeSpan.FromMilliseconds(fullTimeoutMs));

                try
                {
                    await outcomeTcs.Task.WaitAsync(fullCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    var timeoutMsg =
                        $"Teleport via lure {sessionUuid} timed out after starting (lastProgress=\"{progressDetail ?? "(none)"}\", alert=\"{alertMessage ?? "(none)"}\"). Destination region or UDP/CAPS ports may be unreachable.";
                    Console.WriteLine($"[teleport] {timeoutMsg}");
                    EmitRuntimeEvent(
                        "teleport",
                        "teleport.failed",
                        "opensim",
                        timeoutMsg,
                        new Dictionary<string, string?>
                        {
                            ["requesterAgentId"] = requesterAgentUuid.ToString(),
                            ["sessionId"] = sessionUuid.ToString(),
                            ["gatekeeperUrl"] = gatekeeperUrl,
                            ["alertMessage"] = alertMessage
                        });
                    return BotToolResult.Fail(timeoutMsg);
                }
            }

            var (ok, outcomeMsg) = await outcomeTcs.Task.ConfigureAwait(false);
            if (!ok)
            {
                var detail = !string.IsNullOrWhiteSpace(alertMessage) && !outcomeMsg.Contains(alertMessage, StringComparison.OrdinalIgnoreCase)
                    ? $"{outcomeMsg} (alert: {alertMessage})"
                    : outcomeMsg;
                if (!string.IsNullOrWhiteSpace(reachabilityHint))
                {
                    detail = $"{detail} [{reachabilityHint}]";
                }

                var failMsg = $"Teleport offer accept failed for requester {requesterAgentUuid} (session {sessionUuid}): {detail}";
                Console.WriteLine($"[teleport] {failMsg}");
                EmitRuntimeEvent(
                    "teleport",
                    "teleport.failed",
                    "opensim",
                    failMsg,
                    new Dictionary<string, string?>
                    {
                        ["requesterAgentId"] = requesterAgentUuid.ToString(),
                        ["sessionId"] = sessionUuid.ToString(),
                        ["gatekeeperUrl"] = gatekeeperUrl,
                        ["alertMessage"] = alertMessage
                    });
                return BotToolResult.Fail(failMsg);
            }
        }
        finally
        {
            client.Self.TeleportProgress -= OnTeleportEvent;
            client.Self.AlertMessage -= OnAlertEvent;
            client.Network.UnregisterCallback(PacketType.TeleportFailed, OnRawFailedPacket);
            client.Network.UnregisterEventCallback("TeleportFailed", OnRawFailedCaps);
        }

        // Remove consumed pending offer now that teleport succeeded
        RemoveConsumedPendingOffer(requesterAgentUuid, sessionUuid);

        var settled = await ReadSettledTeleportPositionAsync(client, preTeleportSimHandle, preTeleportPosition, cancellationToken).ConfigureAwait(false);
        var arrivedSim = client.Network.CurrentSim;
        var arrivedSimLabel = arrivedSim != null ? $"{arrivedSim.Name} ({arrivedSim.Handle})" : "unknown";

        SchedulePostTeleportSimulatorCleanup(client, "post-lure-teleport");
        Console.WriteLine($"[teleport] Teleport offer accepted and completed: now in {arrivedSimLabel} at {FormatVector(settled)}.");

        EmitRuntimeEvent(
            "teleport",
            "teleport.succeeded",
            "opensim",
            $"Teleported via lure to {arrivedSimLabel} at {FormatVector(settled)}.",
            new Dictionary<string, string?>
            {
                ["requesterAgentId"] = requesterAgentUuid.ToString(),
                ["sessionId"] = sessionUuid.ToString(),
                ["targetRegion"] = arrivedSimLabel,
                ["position"] = FormatVector(settled)
            });

        return BotToolResult.OkResult($"Teleport offer accepted for requester {requesterAgentUuid}; arrived in {arrivedSimLabel} at {FormatVector(settled)}.");
    }

    private static async Task WaitUntilTeleportStartedOrSimChangedAsync(
        GridClient client,
        ulong initialSimHandle,
        Func<bool> isStarted,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (isStarted() || (client.Network.CurrentSim?.Handle ?? 0UL) != initialSimHandle)
            {
                return;
            }

            try
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Normalizes OpenSim Hypergrid visitor avatar names ("First.Last @host:port" or "First.Last@host:port")
    /// into ("First Last", "host:port") when applicable.
    /// </summary>
    internal static bool TryExtractHypergridVisitorIdentity(
        string? avatarName,
        out string normalizedLocalFullName,
        out string authority)
    {
        normalizedLocalFullName = string.Empty;
        authority = string.Empty;

        if (string.IsNullOrWhiteSpace(avatarName))
        {
            return false;
        }

        var trimmed = avatarName.Trim();
        var atIdx = trimmed.IndexOf('@');
        if (atIdx <= 0 || atIdx + 1 >= trimmed.Length)
        {
            return false;
        }

        var namePart = trimmed[..atIdx].Trim();
        var hostPart = trimmed[(atIdx + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(namePart) || string.IsNullOrWhiteSpace(hostPart))
        {
            return false;
        }

        // GatekeeperService formats visitor names as "First.Last" + "@authority"
        if (namePart.Contains('.', StringComparison.Ordinal) && !namePart.Contains(' ', StringComparison.Ordinal))
        {
            var dotIdx = namePart.IndexOf('.');
            var first = namePart[..dotIdx].Trim();
            var last = namePart[(dotIdx + 1)..].Trim();
            if (first.Length > 0 && last.Length > 0)
            {
                normalizedLocalFullName = $"{first} {last}";
                authority = hostPart;
                return true;
            }
        }

        normalizedLocalFullName = string.Join(' ', namePart.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        authority = hostPart;
        return normalizedLocalFullName.Length > 0;
    }
}
