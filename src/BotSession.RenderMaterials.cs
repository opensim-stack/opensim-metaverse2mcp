using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Packets;

namespace Opensim.Metaverse2Mcp;

// PBR (glTF) render materials.
//
// A face with a PBR material usually keeps a blank Blinn-Phong texture entry; what a viewer
// draws comes from a material asset named in the prim's RenderMaterial extra parameter.
// The client library does not keep that parameter, so it is read here from the raw object
// updates, and the material asset (plus any per-face override the simulator pushed) is
// resolved when a prim is inspected.
internal sealed partial class BotSession
{
    private readonly ConcurrentDictionary<(ulong RegionHandle, uint LocalId), IReadOnlyDictionary<int, UUID>> _renderMaterialsByPrim = new();
    private readonly ConcurrentDictionary<UUID, byte[]> _materialAssetDataById = new();
    private static readonly TimeSpan MaterialAssetFetchTimeout = TimeSpan.FromSeconds(8);

    private void RegisterRenderMaterialHooks(GridClient client)
    {
        client.Network.RegisterCallback(PacketType.ObjectUpdate, OnRawObjectUpdateForRenderMaterials);
        client.Network.RegisterCallback(PacketType.ObjectUpdateCompressed, OnRawObjectUpdateCompressedForRenderMaterials);
    }

    private void UnregisterRenderMaterialHooks(GridClient client)
    {
        try { client.Network.UnregisterCallback(PacketType.ObjectUpdate, OnRawObjectUpdateForRenderMaterials); } catch { }
        try { client.Network.UnregisterCallback(PacketType.ObjectUpdateCompressed, OnRawObjectUpdateCompressedForRenderMaterials); } catch { }
    }

    private void OnRawObjectUpdateForRenderMaterials(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectUpdatePacket packet)
        {
            return;
        }

        try
        {
            var regionHandle = packet.RegionData.RegionHandle;
            foreach (var block in packet.ObjectData)
            {
                StoreRenderMaterials(regionHandle, block.ID, ParseRenderMaterialParam(block.ExtraParams, 0));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[materials] could not read an object update: {ex.Message}");
        }
    }

    private void OnRawObjectUpdateCompressedForRenderMaterials(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not ObjectUpdateCompressedPacket packet)
        {
            return;
        }

        try
        {
            var regionHandle = packet.RegionData.RegionHandle;
            foreach (var block in packet.ObjectData)
            {
                if (TryLocateCompressedExtraParams(block.Data, out var localId, out var offset))
                {
                    StoreRenderMaterials(regionHandle, localId, ParseRenderMaterialParam(block.Data, offset));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[materials] could not read a compressed object update: {ex.Message}");
        }
    }

    private void StoreRenderMaterials(ulong regionHandle, uint localId, IReadOnlyDictionary<int, UUID>? materials)
    {
        if (materials == null)
        {
            // The update could not be parsed; keep whatever was known.
            return;
        }

        if (_renderMaterialsByPrim.Count > 100_000)
        {
            _renderMaterialsByPrim.Clear();
        }

        if (materials.Count == 0)
        {
            _renderMaterialsByPrim.TryRemove((regionHandle, localId), out _);
        }
        else
        {
            _renderMaterialsByPrim[(regionHandle, localId)] = materials;
        }
    }

    // Walks the fixed head of a compressed object update up to its extra parameters:
    // id, local id, pcode, state, crc, material, click action, scale, position, rotation,
    // flags, owner, then the optional blocks the flags announce.
    private static bool TryLocateCompressedExtraParams(byte[] data, out uint localId, out int offset)
    {
        localId = 0;
        offset = 0;
        const int fixedHead = 16 + 4 + 1 + 1 + 4 + 1 + 1 + 12 + 12 + 12 + 4 + 16;
        if (data == null || data.Length < fixedHead + 1)
        {
            return false;
        }

        var i = 16;
        localId = BitConverter.ToUInt32(data, i);
        i += 4;
        i += 1 + 1 + 4 + 1 + 1;   // pcode, state, crc, material, click action
        i += 12 + 12 + 12;        // scale, position, rotation
        var flags = (CompressedFlags)BitConverter.ToUInt32(data, i);
        i += 4;
        i += 16;                  // owner

        if ((flags & CompressedFlags.HasAngularVelocity) != 0)
        {
            i += 12;
        }

        if ((flags & CompressedFlags.HasParent) != 0)
        {
            i += 4;
        }

        if ((flags & CompressedFlags.Tree) != 0)
        {
            i += 1;
        }
        else if ((flags & CompressedFlags.ScratchPad) != 0)
        {
            if (i >= data.Length)
            {
                return false;
            }

            i += 1 + data[i];
        }

        if ((flags & CompressedFlags.HasText) != 0)
        {
            while (i < data.Length && data[i] != 0)
            {
                i++;
            }

            i += 1 + 4;           // terminator, text colour
        }

        if ((flags & CompressedFlags.MediaURL) != 0)
        {
            while (i < data.Length && data[i] != 0)
            {
                i++;
            }

            i += 1;
        }

        if ((flags & CompressedFlags.HasParticles) != 0)
        {
            i += 86;
        }

        offset = i;
        return i < data.Length;
    }

    // Extra parameters: count, then {type u16, length u32, payload}. The RenderMaterial
    // payload is: count, then {face index u8, material asset id}.
    // Returns null when the bytes do not parse, an empty map when there is no such parameter.
    private static IReadOnlyDictionary<int, UUID>? ParseRenderMaterialParam(byte[]? data, int offset)
    {
        var empty = new Dictionary<int, UUID>();
        if (data == null || data.Length == 0 || offset >= data.Length)
        {
            return empty;
        }

        var i = offset;
        int count = data[i++];
        for (var n = 0; n < count; n++)
        {
            if (i + 6 > data.Length)
            {
                return null;
            }

            var type = (ushort)(data[i] | (data[i + 1] << 8));
            var length = BitConverter.ToUInt32(data, i + 2);
            i += 6;
            if (length > int.MaxValue || i + (int)length > data.Length)
            {
                return null;
            }

            if (type == (ushort)ExtraParamType.RenderMaterial)
            {
                var result = new Dictionary<int, UUID>();
                var j = i;
                var end = i + (int)length;
                if (j >= end)
                {
                    return result;
                }

                int entries = data[j++];
                for (var k = 0; k < entries && j + 17 <= end; k++)
                {
                    int face = data[j++];
                    var materialId = new UUID(data, j);
                    j += 16;
                    if (materialId != UUID.Zero)
                    {
                        result[face] = materialId;
                    }
                }

                return result;
            }

            i += (int)length;
        }

        return empty;
    }

    private IReadOnlyDictionary<int, UUID> GetRenderMaterialIds(Simulator sim, Primitive prim)
    {
        return _renderMaterialsByPrim.TryGetValue((sim.Handle, prim.LocalID), out var faces)
            ? faces
            : new Dictionary<int, UUID>();
    }

    // Downloads (once) the material assets the given prims use, so inspection can describe them.
    private async Task EnsureRenderMaterialAssetsAsync(GridClient client, Simulator sim, IEnumerable<Primitive> prims, CancellationToken cancellationToken)
    {
        var wanted = prims
            .SelectMany(prim => GetRenderMaterialIds(sim, prim).Values)
            .Where(id => !_materialAssetDataById.ContainsKey(id))
            .Distinct()
            .ToList();

        foreach (var materialId in wanted)
        {
            var data = await DownloadMaterialAssetAsync(client, sim, materialId, cancellationToken).ConfigureAwait(false);
            if (data != null)
            {
                _materialAssetDataById[materialId] = data;
            }
        }
    }

    // Material assets come from the asset capability (?material_id=...); the generic asset
    // request does not serve them, so it is only tried first for grids that do.
    private static async Task<byte[]?> DownloadMaterialAssetAsync(GridClient client, Simulator? sim, UUID materialId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaterialAssetFetchTimeout);
        try
        {
            var capability = (sim ?? client.Network.CurrentSim)?.Caps?.CapabilityURI("ViewerAsset");
            if (capability != null)
            {
                var url = capability.ToString().TrimEnd('/') + "/?material_id=" + materialId;
                using var response = await SharedHttpClient.GetAsync(url, timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                    if (bytes.Length > 0)
                    {
                        return bytes;
                    }
                }
            }

            var asset = await client.Assets.RequestAssetAsync(materialId, AssetType.Material, true, timeout.Token).ConfigureAwait(false);
            return asset?.AssetData is { Length: > 0 } data ? data : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Not served in time; the face is reported with the material id only.
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[materials] could not download material {materialId}: {ex.Message}");
            return null;
        }
    }

    // A material asset is LLSD {type: "GLTF 2.0", version, data: "<glTF JSON>"} whose JSON has one
    // material; texture slots point through textures[] to images[].uri, which holds a texture id.
    // The values are copied into an AssetMaterial so per-face overrides can be applied to them.
    private static AssetMaterial? DecodeMaterialAsset(UUID materialId, byte[] data)
    {
        var json = ExtractGltfJson(data);
        if (json == null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("materials", out var materials)
            || materials.ValueKind != JsonValueKind.Array
            || materials.GetArrayLength() == 0)
        {
            return null;
        }

        var source = materials[0];
        var material = new AssetMaterial();

        UUID ResolveTexture(JsonElement owner, string property)
        {
            if (!owner.TryGetProperty(property, out var reference)
                || !reference.TryGetProperty("index", out var indexElement)
                || !indexElement.TryGetInt32(out var textureIndex)
                || !root.TryGetProperty("textures", out var textures)
                || textures.ValueKind != JsonValueKind.Array
                || textureIndex < 0 || textureIndex >= textures.GetArrayLength()
                || !textures[textureIndex].TryGetProperty("source", out var sourceElement)
                || !sourceElement.TryGetInt32(out var imageIndex)
                || !root.TryGetProperty("images", out var images)
                || images.ValueKind != JsonValueKind.Array
                || imageIndex < 0 || imageIndex >= images.GetArrayLength()
                || !images[imageIndex].TryGetProperty("uri", out var uri))
            {
                return UUID.Zero;
            }

            return UUID.TryParse(uri.GetString() ?? string.Empty, out var id) ? id : UUID.Zero;
        }

        void ApplyTransform(JsonElement owner, string property, int slot)
        {
            if (!owner.TryGetProperty(property, out var reference)
                || !reference.TryGetProperty("extensions", out var extensions)
                || !extensions.TryGetProperty("KHR_texture_transform", out var transform))
            {
                return;
            }

            if (transform.TryGetProperty("offset", out var offset) && offset.ValueKind == JsonValueKind.Array && offset.GetArrayLength() >= 2)
            {
                material.SetTextureOffset(slot, new Vector2(offset[0].GetSingle(), offset[1].GetSingle()));
            }

            if (transform.TryGetProperty("scale", out var scale) && scale.ValueKind == JsonValueKind.Array && scale.GetArrayLength() >= 2)
            {
                material.SetTextureScale(slot, new Vector2(scale[0].GetSingle(), scale[1].GetSingle()));
            }

            if (transform.TryGetProperty("rotation", out var rotation) && rotation.ValueKind == JsonValueKind.Number)
            {
                material.SetTextureRotation(slot, rotation.GetSingle());
            }
        }

        if (source.TryGetProperty("pbrMetallicRoughness", out var pbr))
        {
            material.SetTextureId(AssetMaterial.TEXTURE_BASE_COLOR, ResolveTexture(pbr, "baseColorTexture"), false);
            material.SetTextureId(AssetMaterial.TEXTURE_METALLIC_ROUGHNESS, ResolveTexture(pbr, "metallicRoughnessTexture"), false);
            ApplyTransform(pbr, "baseColorTexture", AssetMaterial.TEXTURE_BASE_COLOR);

            if (pbr.TryGetProperty("baseColorFactor", out var factor) && factor.ValueKind == JsonValueKind.Array && factor.GetArrayLength() >= 4)
            {
                material.SetBaseColorFactor(new Color4(factor[0].GetSingle(), factor[1].GetSingle(), factor[2].GetSingle(), factor[3].GetSingle()), false);
            }

            if (pbr.TryGetProperty("metallicFactor", out var metallic) && metallic.ValueKind == JsonValueKind.Number)
            {
                material.SetMetallicFactor(metallic.GetSingle(), false);
            }

            if (pbr.TryGetProperty("roughnessFactor", out var roughness) && roughness.ValueKind == JsonValueKind.Number)
            {
                material.SetRoughnessFactor(roughness.GetSingle(), false);
            }
        }

        material.SetTextureId(AssetMaterial.TEXTURE_NORMAL, ResolveTexture(source, "normalTexture"), false);
        material.SetTextureId(AssetMaterial.TEXTURE_EMISSIVE, ResolveTexture(source, "emissiveTexture"), false);

        if (source.TryGetProperty("emissiveFactor", out var emissive) && emissive.ValueKind == JsonValueKind.Array && emissive.GetArrayLength() >= 3)
        {
            material.SetEmissiveFactor(new Vector3(emissive[0].GetSingle(), emissive[1].GetSingle(), emissive[2].GetSingle()), false);
        }

        if (source.TryGetProperty("alphaMode", out var alphaMode)
            && Enum.TryParse<GltfAlphaMode>(alphaMode.GetString(), ignoreCase: true, out var parsedAlphaMode))
        {
            material.SetAlphaMode(parsedAlphaMode, false);
        }

        if (source.TryGetProperty("alphaCutoff", out var alphaCutoff) && alphaCutoff.ValueKind == JsonValueKind.Number)
        {
            material.SetAlphaCutoff(alphaCutoff.GetSingle(), false);
        }

        if (source.TryGetProperty("doubleSided", out var doubleSided)
            && (doubleSided.ValueKind == JsonValueKind.True || doubleSided.ValueKind == JsonValueKind.False))
        {
            material.SetDoubleSided(doubleSided.GetBoolean(), false);
        }

        return material;
    }

    private static readonly byte[] LlsdDataStringKey = { (byte)'k', 0, 0, 0, 4, (byte)'d', (byte)'a', (byte)'t', (byte)'a', (byte)'s' };

    private static string? ExtractGltfJson(byte[] data)
    {
        // Binary LLSD: the "data" entry is a key ('k', length 4, "data") followed by a string
        // ('s', big-endian length, bytes).
        var at = data.AsSpan().IndexOf(LlsdDataStringKey);
        if (at >= 0 && at + LlsdDataStringKey.Length + 4 <= data.Length)
        {
            var start = at + LlsdDataStringKey.Length;
            var length = (data[start] << 24) | (data[start + 1] << 16) | (data[start + 2] << 8) | data[start + 3];
            start += 4;
            if (length > 0 && start + length <= data.Length)
            {
                return Encoding.UTF8.GetString(data, start, length);
            }
        }

        // Anything else (XML or notation LLSD, or bare JSON): take the outermost JSON object.
        var text = Encoding.UTF8.GetString(data);
        var first = text.IndexOf("{\"", StringComparison.Ordinal);
        var last = text.LastIndexOf('}');
        return first >= 0 && last > first && text.Contains("\"materials\"", StringComparison.Ordinal)
            ? text.Substring(first, last - first + 1)
            : null;
    }

    private PrimFacePbrInfo? BuildFacePbrInfo(Simulator? sim, Primitive prim, int faceIndex)
    {
        if (sim == null || !GetRenderMaterialIds(sim, prim).TryGetValue(faceIndex, out var materialId))
        {
            return null;
        }

        AssetMaterial? material = null;
        if (_materialAssetDataById.TryGetValue(materialId, out var data))
        {
            try
            {
                material = DecodeMaterialAsset(materialId, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[materials] could not decode material {materialId}: {ex.Message}");
            }
        }

        var hasOverride = false;
        if (material != null
            && sim.GLTFMaterialOverrides.TryGetValue(prim.LocalID, out var overrides)
            && overrides.FaceOverrides != null
            && overrides.FaceOverrides.TryGetValue(faceIndex, out var faceOverride)
            && faceOverride != null)
        {
            try
            {
                material.ApplyOverride(faceOverride);
                hasOverride = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[materials] could not apply the override on prim {prim.LocalID} face {faceIndex}: {ex.Message}");
            }
        }

        if (material == null)
        {
            return new PrimFacePbrInfo(materialId.ToString(), Loaded: false);
        }

        static string? TextureId(AssetMaterial m, int slot)
        {
            var ids = m.TextureIds;
            return ids != null && slot < ids.Length && ids[slot] != UUID.Zero ? ids[slot].ToString() : null;
        }

        var transforms = material.TextureTransforms;
        var baseTransform = transforms != null && transforms.Length > AssetMaterial.TEXTURE_BASE_COLOR
            ? transforms[AssetMaterial.TEXTURE_BASE_COLOR]
            : GltfTextureTransform.Default;

        return new PrimFacePbrInfo(
            materialId.ToString(),
            Loaded: true,
            HasOverride: hasOverride,
            BaseColorTextureId: TextureId(material, AssetMaterial.TEXTURE_BASE_COLOR),
            BaseColorR: material.BaseColorFactor.R,
            BaseColorG: material.BaseColorFactor.G,
            BaseColorB: material.BaseColorFactor.B,
            BaseColorA: material.BaseColorFactor.A,
            AlphaMode: material.AlphaMode.ToString(),
            AlphaCutoff: material.AlphaCutoff,
            DoubleSided: material.DoubleSided,
            BaseColorOffsetU: baseTransform.Offset.X,
            BaseColorOffsetV: baseTransform.Offset.Y,
            BaseColorScaleU: baseTransform.Scale.X,
            BaseColorScaleV: baseTransform.Scale.Y,
            BaseColorRotationRadians: baseTransform.Rotation,
            EmissiveTextureId: TextureId(material, AssetMaterial.TEXTURE_EMISSIVE),
            EmissiveR: material.EmissiveFactor.X,
            EmissiveG: material.EmissiveFactor.Y,
            EmissiveB: material.EmissiveFactor.Z,
            NormalTextureId: TextureId(material, AssetMaterial.TEXTURE_NORMAL),
            MetallicRoughnessTextureId: TextureId(material, AssetMaterial.TEXTURE_METALLIC_ROUGHNESS),
            MetallicFactor: material.MetallicFactor,
            RoughnessFactor: material.RoughnessFactor);
    }
}

// What a viewer draws on a face that has a PBR material. Texture transforms are glTF
// KHR_texture_transform values (offset, scale, rotation about the UV origin).
internal sealed record PrimFacePbrInfo(
    string MaterialId,
    bool Loaded,
    bool HasOverride = false,
    string? BaseColorTextureId = null,
    float BaseColorR = 1f,
    float BaseColorG = 1f,
    float BaseColorB = 1f,
    float BaseColorA = 1f,
    string? AlphaMode = null,
    float AlphaCutoff = 0.5f,
    bool DoubleSided = false,
    float BaseColorOffsetU = 0f,
    float BaseColorOffsetV = 0f,
    float BaseColorScaleU = 1f,
    float BaseColorScaleV = 1f,
    float BaseColorRotationRadians = 0f,
    string? EmissiveTextureId = null,
    float EmissiveR = 0f,
    float EmissiveG = 0f,
    float EmissiveB = 0f,
    string? NormalTextureId = null,
    string? MetallicRoughnessTextureId = null,
    float MetallicFactor = 1f,
    float RoughnessFactor = 1f);
