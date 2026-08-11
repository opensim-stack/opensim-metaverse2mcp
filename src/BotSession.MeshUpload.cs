using LibreMetaverse;
using LibreMetaverse.Assets.Gltf;
using CoreJ2K.Configuration;
using LibreMetaverse.Imaging;
using LibreMetaverse.Imaging.Skia;
using LibreMetaverse.ImportExport;
using LibreMetaverse.StructuredData;
using System.Reflection;
using Vertex = LibreMetaverse.Rendering.Vertex;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private static readonly SkiaTextureCodec GltfTextureCodec = new();

    private sealed record GltfLoadContext(
        GltfDocument Document,
        Func<string, CancellationToken, Task<byte[]>> ResolveExternalUriAsync,
        string SourceLabel);

    private sealed class TextureIngestStats
    {
        public int Converted;
        public int Passthrough;
        public int Failed;
        public readonly HashSet<int> UsedImageIndices = new();
    }

    private sealed record GltfBuildResult(List<ModelPrim> Prims, List<string> Warnings, TextureIngestStats TextureStats);

    public async Task<AssetTransferResult> MeshUploadGltfAsync(
        string source,
        string name,
        string? description,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return AssetTransferResult.FailResult("source is required (local .glb/.gltf path or HTTP/HTTPS URL).");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return AssetTransferResult.FailResult("name is required.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var context = await LoadGltfDocumentFromSourceAsync(source, token).ConfigureAwait(false);
            var build = await BuildModelPrimsFromGltfAsync(context, token).ConfigureAwait(false);
            if (build.Prims.Count == 0)
            {
                var detail = build.Warnings.Count == 0
                    ? "No uploadable triangle primitives were found in the model."
                    : string.Join(" ", build.Warnings.Take(5));
                return AssetTransferResult.FailResult(detail);
            }

            foreach (var prim in build.Prims)
            {
                prim.CreateAsset(client.Self.AgentID);
            }

            var uploader = new ModelUploader(
                client,
                build.Prims,
                name.Trim(),
                description?.Trim() ?? string.Empty);

            var uploadResult = await UploadMeshWithPrepareUploaderAsync(client, uploader, token).ConfigureAwait(false);
            if (uploadResult is not OSDMap map)
            {
                return AssetTransferResult.FailResult("Mesh upload failed: simulator returned no response payload.");
            }

            var itemId = map.ContainsKey("new_inventory_item") ? map["new_inventory_item"].AsUUID() : UUID.Zero;
            var assetId = map.ContainsKey("new_asset") ? map["new_asset"].AsUUID() : UUID.Zero;

            if (itemId == UUID.Zero || assetId == UUID.Zero)
            {
                var state = map.ContainsKey("state") ? map["state"].AsString() : "(unknown)";
                var message = map.ContainsKey("message") ? map["message"].AsString() : "upload did not return new inventory identifiers";
                return AssetTransferResult.FailResult($"Mesh upload failed (state={state}): {message}");
            }

            var warningSuffix = build.Warnings.Count == 0
                ? string.Empty
                : $" Warnings: {string.Join(" | ", build.Warnings.Take(3))}";
            var bytes = build.Prims.Sum(p => p.Asset?.Length ?? 0);

            return AssetTransferResult.OkResult(
                itemId.ToString(),
                assetId.ToString(),
                bytes,
                $"Uploaded GLTF mesh as {build.Prims.Count} prim asset(s).{warningSuffix}");
        }, cancellationToken).ConfigureAwait(false);
    }

    // Work around grids where MeshUploader/MeshUploadFlag cap routing is inconsistent:
    // run prepare, then POST phase-2 bytes to the exact uploader URI returned by prepare.
    private static async Task<OSD?> UploadMeshWithPrepareUploaderAsync(
        GridClient client,
        ModelUploader uploader,
        CancellationToken cancellationToken)
    {
        var prepared = await uploader.PrepareUploadAsync(cancellationToken).ConfigureAwait(false);
        if (prepared is not OSDMap prepMap)
        {
            return null;
        }

        if (!prepMap.ContainsKey("uploader"))
        {
            return prepMap;
        }

        var uploaderUriText = prepMap["uploader"].AsString();
        if (string.IsNullOrWhiteSpace(uploaderUriText) || !Uri.TryCreate(uploaderUriText, UriKind.Absolute, out var uploaderUri))
        {
            return prepMap;
        }

        var payload = BuildUploaderPayloadViaReflection(uploader);
        if (payload == null)
        {
            return prepMap;
        }

        var (response, data) = await client.HttpCapsClient.PostAsync(uploaderUri, OSDFormat.Xml, payload, cancellationToken)
            .ConfigureAwait(false);
        if (response == null || data == null || data.Length == 0)
        {
            return null;
        }

        return OSDParser.Deserialize(data);
    }

    private static OSD? BuildUploaderPayloadViaReflection(ModelUploader uploader)
    {
        var method = typeof(ModelUploader).GetMethod("AssetResources", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null)
        {
            return null;
        }

        try
        {
            return method.Invoke(uploader, new object[] { true }) as OSD;
        }
        catch
        {
            return null;
        }
    }

    public async Task<MeshInspectResult> MeshInspectGltfAsync(
        string source,
        int maxWarnings,
        bool strict,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return MeshInspectResult.FailResult("source is required (local .glb/.gltf path or HTTP/HTTPS URL).");
        }

        try
        {
            var context = await LoadGltfDocumentFromSourceAsync(source, cancellationToken).ConfigureAwait(false);
            var build = await BuildModelPrimsFromGltfAsync(context, cancellationToken).ConfigureAwait(false);

            var warningLimit = Math.Clamp(maxWarnings, 1, 200);
            var warnings = build.Warnings.Take(warningLimit).ToList();
            var skippedWarningCount = Math.Max(0, build.Warnings.Count - warnings.Count);
            if (skippedWarningCount > 0)
            {
                warnings.Add($"... {skippedWarningCount} additional warning(s) omitted.");
            }

            var totalPrimitiveCount = context.Document.Meshes.Sum(m => m.Primitives.Count);
            var trianglePrimitiveCount = context.Document.Meshes.Sum(m => m.Primitives.Count(p => p.Mode == GltfPrimitiveMode.Triangles));
            var skippedPrimitiveCount = Math.Max(0, totalPrimitiveCount - build.Prims.Count);

            var estimatedBytes = 0;
            foreach (var prim in build.Prims)
            {
                prim.CreateAsset(UUID.Zero);
                estimatedBytes += prim.Asset?.Length ?? 0;
            }

            var imageSummaries = BuildImageSummaries(context.Document, build.TextureStats.UsedImageIndices);

            var strictViolations = new List<string>();
            if (strict && skippedPrimitiveCount > 0)
            {
                strictViolations.Add($"{skippedPrimitiveCount} primitive(s) would be skipped (non-triangle or invalid). ");
            }

            if (strict && build.TextureStats.Failed > 0)
            {
                strictViolations.Add($"{build.TextureStats.Failed} texture image(s) failed to ingest/transcode.");
            }

            var strictFailed = strictViolations.Count > 0;
            var message = strictFailed
                ? $"Strict inspection failed: {string.Join(" ", strictViolations)}"
                : $"Inspection complete for {source}. Uploadable prims: {build.Prims.Count}.";

            return new MeshInspectResult(
                !strictFailed,
                message,
                source,
                context.Document.Meshes.Count,
                totalPrimitiveCount,
                trianglePrimitiveCount,
                skippedPrimitiveCount,
                context.Document.Materials.Count,
                context.Document.Textures.Count,
                context.Document.Images.Count,
                build.Prims.Count,
                estimatedBytes,
                build.TextureStats.Converted,
                build.TextureStats.Passthrough,
                build.TextureStats.Failed,
                imageSummaries,
                warnings);
        }
        catch (Exception ex)
        {
            return MeshInspectResult.FailResult($"Inspection failed: {ex.Message}");
        }
    }

    private static async Task<GltfLoadContext> LoadGltfDocumentFromSourceAsync(string source, CancellationToken cancellationToken)
    {
        var bytes = await ReadBinarySourceAsync(source, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < 4)
        {
            throw new InvalidOperationException("Source file is empty or too small to be a valid glTF/glb.");
        }

        var isGlb = bytes[0] == 0x67 && bytes[1] == 0x6C && bytes[2] == 0x54 && bytes[3] == 0x46;
        if (isGlb)
        {
            return new GltfLoadContext(
                GltfDocument.Load(bytes),
                static (_, _) => Task.FromException<byte[]>(new InvalidOperationException("GLB source does not provide a base URI for external image resolution.")),
                source);
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var sourceUri)
            && (sourceUri.Scheme == Uri.UriSchemeHttp || sourceUri.Scheme == Uri.UriSchemeHttps))
        {
            var document = GltfDocument.Load(bytes, relativePath =>
            {
                var resolved = new Uri(sourceUri, relativePath);
                return SharedHttpClient.GetByteArrayAsync(resolved).GetAwaiter().GetResult();
            });
            return new GltfLoadContext(
                document,
                (relativePath, token) =>
                {
                    var resolved = new Uri(sourceUri, relativePath);
                    return SharedHttpClient.GetByteArrayAsync(resolved, token);
                },
                sourceUri.ToString());
        }

        var fullPath = Path.GetFullPath(source);
        var baseDirectory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var gltf = GltfDocument.Load(bytes, relativePath =>
        {
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var resolved = Path.GetFullPath(Path.Combine(baseDirectory, normalized));
            return File.ReadAllBytes(resolved);
        });
        return new GltfLoadContext(
            gltf,
            (relativePath, token) =>
            {
                var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
                var resolved = Path.GetFullPath(Path.Combine(baseDirectory, normalized));
                return File.ReadAllBytesAsync(resolved, token);
            },
            fullPath);
    }

    private static async Task<GltfBuildResult> BuildModelPrimsFromGltfAsync(
        GltfLoadContext context,
        CancellationToken cancellationToken)
    {
        var document = context.Document;
        var prims = new List<ModelPrim>();
        var warnings = new List<string>();
        var encodedTextureCache = new Dictionary<int, byte[]?>();
        var textureStats = new TextureIngestStats();

        for (var meshIndex = 0; meshIndex < document.Meshes.Count; meshIndex++)
        {
            var mesh = document.Meshes[meshIndex];
            for (var primitiveIndex = 0; primitiveIndex < mesh.Primitives.Count; primitiveIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var primitive = mesh.Primitives[primitiveIndex];
                if (primitive.Mode != GltfPrimitiveMode.Triangles)
                {
                    warnings.Add($"Skipped mesh {meshIndex} primitive {primitiveIndex}: mode {primitive.Mode} is not supported (only TRIANGLES)." );
                    continue;
                }

                var positions = document.GetPositions(primitive);
                if (positions.Length < 3)
                {
                    warnings.Add($"Skipped mesh {meshIndex} primitive {primitiveIndex}: less than 3 vertices.");
                    continue;
                }

                var normals = document.GetNormals(primitive);
                var texCoords = document.GetTexCoords(primitive, 0);
                var indices = document.GetIndices(primitive);
                if (indices.Length == 0)
                {
                    var generated = new List<uint>(positions.Length);
                    for (uint i = 0; i < (uint)positions.Length; i++)
                    {
                        generated.Add(i);
                    }
                    indices = generated.ToArray();
                }

                var modelPrim = new ModelPrim
                {
                    ID = string.IsNullOrWhiteSpace(mesh.Name)
                        ? $"mesh_{meshIndex}_prim_{primitiveIndex}"
                        : $"{mesh.Name}_{primitiveIndex}",
                    Position = Vector3.Zero,
                    Scale = Vector3.One,
                    Rotation = Quaternion.Identity
                };

                var modelFace = new ModelFace
                {
                    MaterialID = primitive.Material.ToString(),
                    Material = await BuildModelMaterialAsync(
                        context,
                        primitive.Material,
                        encodedTextureCache,
                        textureStats,
                        warnings,
                        cancellationToken).ConfigureAwait(false)
                };

                var appendedTriangleCount = 0;
                for (var i = 0; i + 2 < indices.Length; i += 3)
                {
                    var ia = (int)indices[i];
                    var ib = (int)indices[i + 1];
                    var ic = (int)indices[i + 2];

                    if (ia < 0 || ib < 0 || ic < 0
                        || ia >= positions.Length
                        || ib >= positions.Length
                        || ic >= positions.Length)
                    {
                        continue;
                    }

                    var aPos = positions[ia];
                    var bPos = positions[ib];
                    var cPos = positions[ic];

                    var computedNormal = Vector3.Zero;
                    var edge1 = bPos - aPos;
                    var edge2 = cPos - aPos;
                    var cross = Vector3.Cross(edge1, edge2);
                    if (cross != Vector3.Zero)
                    {
                        computedNormal = Vector3.Normalize(cross);
                    }

                    var a = new Vertex
                    {
                        Position = aPos,
                        Normal = ia < normals.Length ? normals[ia] : computedNormal,
                        TexCoord = ia < texCoords.Length ? texCoords[ia] : Vector2.Zero
                    };
                    var b = new Vertex
                    {
                        Position = bPos,
                        Normal = ib < normals.Length ? normals[ib] : computedNormal,
                        TexCoord = ib < texCoords.Length ? texCoords[ib] : Vector2.Zero
                    };
                    var c = new Vertex
                    {
                        Position = cPos,
                        Normal = ic < normals.Length ? normals[ic] : computedNormal,
                        TexCoord = ic < texCoords.Length ? texCoords[ic] : Vector2.Zero
                    };

                    modelFace.AddVertex(a);
                    modelFace.AddVertex(b);
                    modelFace.AddVertex(c);
                    appendedTriangleCount++;
                }

                if (appendedTriangleCount == 0)
                {
                    warnings.Add($"Skipped mesh {meshIndex} primitive {primitiveIndex}: no valid triangles after index validation.");
                    continue;
                }

                modelPrim.Faces.Add(modelFace);
                NormalizePrimGeometry(modelPrim);
                prims.Add(modelPrim);
            }
        }

        return new GltfBuildResult(prims, warnings, textureStats);
    }

    private static async Task<ModelMaterial> BuildModelMaterialAsync(
        GltfLoadContext context,
        int materialIndex,
        Dictionary<int, byte[]?> encodedTextureCache,
        TextureIngestStats textureStats,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var document = context.Document;
        var material = new ModelMaterial
        {
            ID = materialIndex.ToString(),
            DiffuseColor = Color4.White,
            Texture = string.Empty,
            TextureData = null!
        };

        if (materialIndex < 0 || materialIndex >= document.Materials.Count)
        {
            return material;
        }

        var source = document.Materials[materialIndex];
        material.DiffuseColor = source.BaseColorFactor;

        var textureIndex = source.BaseColorTexture?.Index ?? -1;
        if (textureIndex < 0 || textureIndex >= document.Textures.Count)
        {
            return material;
        }

        var imageIndex = document.Textures[textureIndex].Source;
        if (imageIndex < 0 || imageIndex >= document.Images.Count)
        {
            warnings.Add($"Material {materialIndex} references texture {textureIndex} with invalid image source index {imageIndex}.");
            return material;
        }

        if (!encodedTextureCache.TryGetValue(imageIndex, out var encodedJ2k))
        {
            encodedJ2k = await LoadAndEncodeTextureImageAsync(context, imageIndex, textureStats, warnings, cancellationToken).ConfigureAwait(false);
            encodedTextureCache[imageIndex] = encodedJ2k;
        }

        textureStats.UsedImageIndices.Add(imageIndex);

        if (encodedJ2k == null || encodedJ2k.Length == 0)
        {
            return material;
        }

        material.Texture = $"gltf-image-{imageIndex}";
        material.TextureData = encodedJ2k;
        return material;
    }

    private static async Task<byte[]?> LoadAndEncodeTextureImageAsync(
        GltfLoadContext context,
        int imageIndex,
        TextureIngestStats textureStats,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var image = context.Document.Images[imageIndex];
        byte[] rawBytes;
        string descriptor;

        if (!string.IsNullOrWhiteSpace(image.Uri))
        {
            descriptor = image.Uri!;
            if (image.Uri!.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = image.Uri.IndexOf(',');
                if (comma < 0)
                {
                    textureStats.Failed++;
                    warnings.Add($"Image {imageIndex} has malformed data URI.");
                    return null;
                }

                rawBytes = Convert.FromBase64String(image.Uri.Substring(comma + 1));
            }
            else
            {
                try
                {
                    rawBytes = await context.ResolveExternalUriAsync(image.Uri!, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    textureStats.Failed++;
                    warnings.Add($"Image {imageIndex} could not be loaded from '{image.Uri}': {ex.Message}");
                    return null;
                }
            }
        }
        else if (image.BufferView >= 0)
        {
            descriptor = $"bufferView:{image.BufferView}";
            if (!TryReadBufferView(context.Document, image.BufferView, out rawBytes, out var error))
            {
                textureStats.Failed++;
                warnings.Add($"Image {imageIndex} could not be read from {descriptor}: {error}");
                return null;
            }
        }
        else
        {
            textureStats.Failed++;
            warnings.Add($"Image {imageIndex} has no uri or bufferView and cannot be uploaded.");
            return null;
        }

        if (rawBytes.Length == 0)
        {
            textureStats.Failed++;
            warnings.Add($"Image {imageIndex} ({descriptor}) resolved to empty bytes.");
            return null;
        }

        var mime = image.MimeType;
        if (LooksLikeJ2k(descriptor, mime))
        {
            textureStats.Passthrough++;
            return rawBytes;
        }

        try
        {
            ManagedImage decoded;
            using (var stream = new MemoryStream(rawBytes, writable: false))
            {
                if (LooksLikeTga(descriptor, mime))
                {
                    decoded = Targa.DecodeToManagedImage(stream);
                }
                else
                {
                    decoded = GltfTextureCodec.Decode(stream);
                }
            }

            ResizeForTextureUpload(decoded);
            textureStats.Converted++;
            return CompleteConfigurationPresets.Streaming.WithFileFormat(false).Encode(decoded);
        }
        catch (Exception ex)
        {
            textureStats.Failed++;
            warnings.Add($"Image {imageIndex} ({descriptor}) could not be transcoded to JPEG2000: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<MeshInspectImageSummary> BuildImageSummaries(GltfDocument document, HashSet<int> usedImageIndices)
    {
        var list = new List<MeshInspectImageSummary>(document.Images.Count);
        for (var i = 0; i < document.Images.Count; i++)
        {
            var image = document.Images[i];
            var descriptor = !string.IsNullOrWhiteSpace(image.Uri)
                ? image.Uri!
                : (image.BufferView >= 0 ? $"bufferView:{image.BufferView}" : "(none)");

            list.Add(new MeshInspectImageSummary(
                i,
                descriptor,
                image.MimeType,
                image.BufferView >= 0 ? image.BufferView : null,
                !string.IsNullOrWhiteSpace(image.Uri) && image.Uri!.StartsWith("data:", StringComparison.OrdinalIgnoreCase),
                !string.IsNullOrWhiteSpace(image.Uri),
                image.BufferView >= 0,
                LooksLikeJ2k(descriptor, image.MimeType),
                usedImageIndices.Contains(i)
            ));
        }

        return list;
    }

    private static bool TryReadBufferView(GltfDocument document, int bufferViewIndex, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;

        if (bufferViewIndex < 0 || bufferViewIndex >= document.BufferViews.Count)
        {
            error = "bufferView index out of range";
            return false;
        }

        var view = document.BufferViews[bufferViewIndex];
        if (view.Buffer < 0 || view.Buffer >= document.Buffers.Count)
        {
            error = "buffer index out of range";
            return false;
        }

        var buffer = document.Buffers[view.Buffer];
        if (buffer.Data == null)
        {
            error = "buffer data not loaded";
            return false;
        }

        if (view.ByteOffset < 0 || view.ByteLength <= 0 || view.ByteOffset + view.ByteLength > buffer.Data.Length)
        {
            error = "bufferView byte range is invalid";
            return false;
        }

        bytes = new byte[view.ByteLength];
        Buffer.BlockCopy(buffer.Data, view.ByteOffset, bytes, 0, view.ByteLength);
        return true;
    }

    private static bool LooksLikeJ2k(string descriptor, string? mimeType)
    {
        if (!string.IsNullOrWhiteSpace(mimeType)
            && (mimeType.Contains("jp2", StringComparison.OrdinalIgnoreCase)
                || mimeType.Contains("j2c", StringComparison.OrdinalIgnoreCase)
                || mimeType.Contains("jpeg2000", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var lowered = descriptor.ToLowerInvariant();
        return lowered.EndsWith(".jp2") || lowered.EndsWith(".j2c") || lowered.EndsWith(".j2k") || lowered.EndsWith(".jpeg2000");
    }

    private static bool LooksLikeTga(string descriptor, string? mimeType)
    {
        if (!string.IsNullOrWhiteSpace(mimeType) && mimeType.Contains("tga", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lowered = descriptor.ToLowerInvariant();
        return lowered.EndsWith(".tga") || lowered.EndsWith(".targa");
    }

    private static void ResizeForTextureUpload(ManagedImage image)
    {
        var width = image.Width;
        var height = image.Height;

        if (!IsPowerOfTwo((uint)width) || !IsPowerOfTwo((uint)height) || width > 1024 || height > 1024)
        {
            var resizedWidth = Math.Min(1024, ClosestPowerOfTwoFloor(width));
            var resizedHeight = Math.Min(1024, ClosestPowerOfTwoFloor(height));
            image.ResizeBilinear(Math.Max(1, resizedWidth), Math.Max(1, resizedHeight));
        }
    }

    private static int ClosestPowerOfTwoFloor(int n)
    {
        if (n <= 1)
        {
            return 1;
        }

        var value = 1;
        while (value < n)
        {
            value <<= 1;
        }

        return value > 1 ? value >> 1 : 1;
    }

    private static bool IsPowerOfTwo(uint n) => n != 0 && (n & (n - 1)) == 0;

    private static void NormalizePrimGeometry(ModelPrim prim)
    {
        var allVertices = prim.Faces.SelectMany(f => f.Vertices).ToList();
        if (allVertices.Count == 0)
        {
            prim.Scale = Vector3.One;
            prim.Position = Vector3.Zero;
            return;
        }

        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var vertex in allVertices)
        {
            min = new Vector3(
                MathF.Min(min.X, vertex.Position.X),
                MathF.Min(min.Y, vertex.Position.Y),
                MathF.Min(min.Z, vertex.Position.Z));
            max = new Vector3(
                MathF.Max(max.X, vertex.Position.X),
                MathF.Max(max.Y, vertex.Position.Y),
                MathF.Max(max.Z, vertex.Position.Z));
        }

        var extent = max - min;
        var safeExtent = new Vector3(
            extent.X < 1e-4f ? 0.01f : extent.X,
            extent.Y < 1e-4f ? 0.01f : extent.Y,
            extent.Z < 1e-4f ? 0.01f : extent.Z);

        for (var faceIndex = 0; faceIndex < prim.Faces.Count; faceIndex++)
        {
            var face = prim.Faces[faceIndex];
            for (var vertexIndex = 0; vertexIndex < face.Vertices.Count; vertexIndex++)
            {
                var vertex = face.Vertices[vertexIndex];
                var p = vertex.Position;
                vertex.Position = new Vector3(
                    ((p.X - min.X) / safeExtent.X) - 0.5f,
                    ((p.Y - min.Y) / safeExtent.Y) - 0.5f,
                    ((p.Z - min.Z) / safeExtent.Z) - 0.5f);
                face.Vertices[vertexIndex] = vertex;
            }
        }

        prim.BoundMin = min;
        prim.BoundMax = max;
        prim.Scale = safeExtent;
        prim.Position = min + (extent / 2f);
        prim.Positions = allVertices.Select(v => v.Position).ToList();
    }
}

internal sealed record MeshInspectImageSummary(
    int ImageIndex,
    string Source,
    string? MimeType,
    int? BufferView,
    bool IsDataUri,
    bool HasUri,
    bool HasBufferView,
    bool IsLikelyJpeg2000,
    bool ReferencedByMaterials);

internal sealed record MeshInspectResult(
    bool Ok,
    string Message,
    string Source,
    int MeshCount,
    int PrimitiveCount,
    int TrianglePrimitiveCount,
    int SkippedPrimitiveCount,
    int MaterialCount,
    int TextureCount,
    int ImageCount,
    int UploadablePrimCount,
    int EstimatedMeshAssetBytes,
    int ConvertedTextureCount,
    int PassthroughTextureCount,
    int FailedTextureCount,
    IReadOnlyList<MeshInspectImageSummary> Images,
    IReadOnlyList<string> Warnings)
{
    public static MeshInspectResult OkResult(
        string source,
        int meshCount,
        int primitiveCount,
        int trianglePrimitiveCount,
        int skippedPrimitiveCount,
        int materialCount,
        int textureCount,
        int imageCount,
        int uploadablePrimCount,
        int estimatedMeshAssetBytes,
        int convertedTextureCount,
        int passthroughTextureCount,
        int failedTextureCount,
        IReadOnlyList<MeshInspectImageSummary> images,
        IReadOnlyList<string> warnings,
        string message)
        => new(
            true,
            message,
            source,
            meshCount,
            primitiveCount,
            trianglePrimitiveCount,
            skippedPrimitiveCount,
            materialCount,
            textureCount,
            imageCount,
            uploadablePrimCount,
            estimatedMeshAssetBytes,
            convertedTextureCount,
            passthroughTextureCount,
            failedTextureCount,
            images,
            warnings);

    public static MeshInspectResult FailResult(string message)
        => new(
            false,
            message,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            Array.Empty<MeshInspectImageSummary>(),
            Array.Empty<string>());
}
