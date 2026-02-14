using Assimp;
using Assimp.Configs;
using Assimp.Unmanaged;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;
using NortAnimationMvp.Runtime;
using Quaternion = Microsoft.Xna.Framework.Quaternion;

namespace NortAnimationMvp.Assets;

public sealed class FbxRuntimeLoader
{
    private static bool _assimpNativeConfigured;

    public AnimationSet Load(
        GraphicsDevice graphicsDevice,
        string modelPath,
        IReadOnlyDictionary<string, string> clipFiles)
    {
        ConfigureAssimpNativeResolver();
        using var importer = new AssimpContext();
        importer.SetConfig(new FBXImportEmbeddedTexturesConfig(true));
        importer.SetConfig(new FBXImportEmbeddedTexturesLegacyNamingConfig(true));
        importer.SetConfig(new FBXPreservePivotsConfig(true));
        var scene = importer.ImportFile(modelPath, PostProcessSteps.None);

        if (scene is null || scene.RootNode is null)
        {
            throw new InvalidOperationException($"Falha ao importar modelo: {modelPath}");
        }

        var skeleton = BuildSkeleton(scene);
        var meshParts = BuildMeshParts(
            graphicsDevice,
            scene,
            skeleton,
            Path.GetDirectoryName(modelPath) ?? Directory.GetCurrentDirectory());

        var clips = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
        using var clipImporter = new AssimpContext();
        foreach (var (name, clipPath) in clipFiles)
        {
            clips[name] = LoadClip(clipImporter, clipPath, skeleton, name);
        }

        return new AnimationSet
        {
            Model = new SkinnedModel
            {
                Skeleton = skeleton,
                MeshParts = meshParts,
                PreferCullClockwiseFace = ShouldUseCullClockwiseFace(meshParts)
            },
            Clips = clips
        };
    }

    private static void ConfigureAssimpNativeResolver()
    {
        if (_assimpNativeConfigured)
        {
            return;
        }

        var probingPaths = new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "runtimes", "osx", "native"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "osx-x64", "native"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "osx-arm64", "native")
        };

        var existingProbingPaths = probingPaths.Where(Directory.Exists).Distinct().ToArray();
        var assimpLibrary = AssimpLibrary.Instance;
        var resolver = assimpLibrary.Resolver;

        if (existingProbingPaths.Length > 0 && resolver is not null)
        {
            resolver.SetProbingPaths(existingProbingPaths);
        }

        if (resolver is not null)
        {
            resolver.SetOverrideLibraryName("libassimp.dylib");
            resolver.SetFallbackLibraryNames(
                "libassimp.dylib",
                "libassimp.7.dylib",
                "libassimp.6.dylib",
                "libassimp.5.dylib");
        }

        _assimpNativeConfigured = true;
    }

    private static Skeleton BuildSkeleton(Scene scene)
    {
        var names = new List<string>();
        var parents = new List<int>();
        var locals = new List<Matrix>();

        void Walk(Node node, int parentIndex)
        {
            var index = names.Count;
            names.Add(node.Name);
            parents.Add(parentIndex);
            locals.Add(ToXna(node.Transform));

            foreach (var child in node.Children)
            {
                Walk(child, index);
            }
        }

        Walk(scene.RootNode, -1);

        var exactNameToIndex = names
            .Select((value, index) => (value, index))
            .ToDictionary(x => x.value, x => x.index, StringComparer.OrdinalIgnoreCase);
        var normalizedNameToIndex = BuildNormalizedBoneNameMap(names.ToArray());

        var bindPositions = new Vector3[names.Count];
        var bindRotations = new Quaternion[names.Count];
        var bindScales = new Vector3[names.Count];
        var bindGlobal = new Matrix[names.Count];
        var inverseBind = new Matrix[names.Count];

        for (var i = 0; i < names.Count; i++)
        {
            if (!locals[i].Decompose(out var scale, out var rotation, out var translation))
            {
                scale = Vector3.One;
                rotation = Quaternion.Identity;
                translation = Vector3.Zero;
            }

            bindPositions[i] = translation;
            bindRotations[i] = Quaternion.Normalize(rotation);
            bindScales[i] = scale;

            bindGlobal[i] = parents[i] < 0 ? locals[i] : locals[i] * bindGlobal[parents[i]];
            inverseBind[i] = Matrix.Invert(bindGlobal[i]);
        }

        foreach (var mesh in scene.Meshes)
        {
            foreach (var bone in mesh.Bones)
            {
                if (!TryResolveBoneIndex(bone.Name, exactNameToIndex, normalizedNameToIndex, out var boneIndex))
                {
                    continue;
                }

                inverseBind[boneIndex] = ToXna(bone.OffsetMatrix);
            }
        }

        return new Skeleton
        {
            BoneNames = names.ToArray(),
            ParentIndices = parents.ToArray(),
            BindPositions = bindPositions,
            BindRotations = bindRotations,
            BindScales = bindScales,
            InverseBindPose = inverseBind
        };
    }

    private static SkinnedMeshPart[] BuildMeshParts(
        GraphicsDevice graphicsDevice,
        Scene scene,
        Skeleton skeleton,
        string modelDirectory)
    {
        var exactBoneNameToIndex = skeleton.BoneNames
            .Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        var normalizedBoneNameToIndex = BuildNormalizedBoneNameMap(skeleton.BoneNames);
        var materialTextures = LoadTexturesByMaterial(graphicsDevice, scene, modelDirectory);
        var materialColors = LoadColorsByMaterial(scene);

        var parts = new List<SkinnedMeshPart>(scene.MeshCount);

        for (var meshIndex = 0; meshIndex < scene.MeshCount; meshIndex++)
        {
            var mesh = scene.Meshes[meshIndex];
            var vertices = new SkinnedVertex[mesh.VertexCount];
            var influences = new BoneInfluence[mesh.VertexCount, 4];
            var matchedMeshBones = 0;
            var unmatchedMeshBones = 0;
            var weightEntryCount = 0;
            var positiveWeightCount = 0;
            var maxWeight = 0f;

            for (var i = 0; i < mesh.VertexCount; i++)
            {
                var pos = mesh.Vertices[i];
                var normal = mesh.HasNormals ? mesh.Normals[i] : new Vector3D(0f, 1f, 0f);
                var uv = mesh.HasTextureCoords(0) ? mesh.TextureCoordinateChannels[0][i] : new Vector3D(0f, 0f, 0f);

                vertices[i] = new SkinnedVertex
                {
                    Position = new Vector3(pos.X, pos.Y, pos.Z),
                    Normal = SafeNormalize(new Vector3(normal.X, normal.Y, normal.Z), Vector3.Up),
                    TexCoord = new Vector2(uv.X, uv.Y),
                    BlendIndices = new Byte4(0, 0, 0, 0),
                    BlendWeights = Vector4.Zero
                };
            }

            foreach (var bone in mesh.Bones)
            {
                if (!TryResolveBoneIndex(bone.Name, exactBoneNameToIndex, normalizedBoneNameToIndex, out var boneIndex))
                {
                    unmatchedMeshBones++;
                    continue;
                }
                matchedMeshBones++;

                foreach (var weight in bone.VertexWeights)
                {
                    weightEntryCount++;
                    if (weight.Weight > 0f)
                    {
                        positiveWeightCount++;
                    }

                    if (weight.Weight > maxWeight)
                    {
                        maxWeight = weight.Weight;
                    }

                    InsertInfluence(influences, weight.VertexID, boneIndex, weight.Weight);
                }
            }

            var usedBones = new HashSet<int>();
            var fallbackVertices = 0;
            for (var vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
            {
                var total = influences[vertexIndex, 0].Weight + influences[vertexIndex, 1].Weight + influences[vertexIndex, 2].Weight + influences[vertexIndex, 3].Weight;
                if (total <= 0.00001f)
                {
                    fallbackVertices++;
                    influences[vertexIndex, 0] = new BoneInfluence { BoneIndex = 0, Weight = 1f };
                    influences[vertexIndex, 1] = default;
                    influences[vertexIndex, 2] = default;
                    influences[vertexIndex, 3] = default;
                    total = 1f;
                }

                for (var j = 0; j < 4; j++)
                {
                    influences[vertexIndex, j].Weight /= total;
                    if (influences[vertexIndex, j].Weight > 0f)
                    {
                        usedBones.Add(influences[vertexIndex, j].BoneIndex);
                    }
                }
            }

            var remap = usedBones.OrderBy(x => x).ToArray();
            if (remap.Length == 0)
            {
                remap = new[] { 0 };
            }

            var globalToLocal = new Dictionary<int, int>(remap.Length);
            for (var i = 0; i < remap.Length; i++)
            {
                globalToLocal[remap[i]] = i;
            }

            for (var vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
            {
                vertices[vertexIndex].BlendIndices = new Byte4(
                    (byte)Math.Clamp(MapToLocalBoneIndex(globalToLocal, influences[vertexIndex, 0].BoneIndex), 0, 255),
                    (byte)Math.Clamp(MapToLocalBoneIndex(globalToLocal, influences[vertexIndex, 1].BoneIndex), 0, 255),
                    (byte)Math.Clamp(MapToLocalBoneIndex(globalToLocal, influences[vertexIndex, 2].BoneIndex), 0, 255),
                    (byte)Math.Clamp(MapToLocalBoneIndex(globalToLocal, influences[vertexIndex, 3].BoneIndex), 0, 255));

                vertices[vertexIndex].BlendWeights = new Vector4(
                    influences[vertexIndex, 0].Weight,
                    influences[vertexIndex, 1].Weight,
                    influences[vertexIndex, 2].Weight,
                    influences[vertexIndex, 3].Weight);
            }

            var indices = new List<int>(mesh.FaceCount * 3);
            foreach (var face in mesh.Faces)
            {
                if (face.IndexCount < 3)
                {
                    continue;
                }

                var i0 = face.Indices[0];
                for (var i = 1; i < face.IndexCount - 1; i++)
                {
                    indices.Add(i0);
                    indices.Add(face.Indices[i]);
                    indices.Add(face.Indices[i + 1]);
                }
            }

            parts.Add(new SkinnedMeshPart
            {
                Name = $"MeshPart_{meshIndex}",
                Vertices = vertices,
                Indices = indices.ToArray(),
                BoneRemap = remap,
                DiffuseTexture = materialTextures.TryGetValue(mesh.MaterialIndex, out var texture) ? texture : null,
                MaterialColor = materialColors.TryGetValue(mesh.MaterialIndex, out var color) ? color : Vector3.One
            });
        }

        return parts.ToArray();
    }

    private static Dictionary<int, Texture2D?> LoadTexturesByMaterial(GraphicsDevice graphicsDevice, Scene scene, string modelDirectory)
    {
        var result = new Dictionary<int, Texture2D?>(scene.MaterialCount);
        var loadedAny = false;

        for (var materialIndex = 0; materialIndex < scene.MaterialCount; materialIndex++)
        {
            var material = scene.Materials[materialIndex];
            if (!TryGetPrimaryTextureSlot(material, out var textureSlot, out var chosenType))
            {
                result[materialIndex] = null;
                continue;
            }

            var filePath = textureSlot.FilePath ?? string.Empty;
            var texture = TryLoadTexture(graphicsDevice, scene, modelDirectory, filePath, textureSlot.TextureIndex);
            result[materialIndex] = texture;
            loadedAny |= texture is not null;
        }

        if (!loadedAny)
        {
            var fallbackTexture = TryLoadDirectoryFallbackTexture(graphicsDevice, modelDirectory);
            if (fallbackTexture is not null)
            {
                for (var materialIndex = 0; materialIndex < scene.MaterialCount; materialIndex++)
                {
                    result[materialIndex] = fallbackTexture;
                }

            }
        }

        return result;
    }

    private static Dictionary<int, Vector3> LoadColorsByMaterial(Scene scene)
    {
        var result = new Dictionary<int, Vector3>(scene.MaterialCount);

        for (var materialIndex = 0; materialIndex < scene.MaterialCount; materialIndex++)
        {
            var material = scene.Materials[materialIndex];

            Vector3 color;
            if (material.HasColorDiffuse)
            {
                color = ToVector3(material.ColorDiffuse);
            }
            else if (material.HasColorAmbient)
            {
                color = ToVector3(material.ColorAmbient);
            }
            else if (material.HasColorReflective)
            {
                color = ToVector3(material.ColorReflective);
            }
            else
            {
                color = new Vector3(0.95f, 0.95f, 0.95f);
            }

            result[materialIndex] = EnsureVisibleColor(color);
        }

        return result;
    }

    private static Texture2D? TryLoadDirectoryFallbackTexture(GraphicsDevice graphicsDevice, string modelDirectory)
    {
        if (!Directory.Exists(modelDirectory))
        {
            return null;
        }

        var candidates = Directory
            .EnumerateFiles(modelDirectory)
            .Where(path =>
            {
                var ext = Path.GetExtension(path);
                return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(ScoreTextureCandidate)
            .ToArray();

        foreach (var path in candidates)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var texture = Texture2D.FromStream(graphicsDevice, stream);
                texture.Name = Path.GetFileName(path);
                return texture;
            }
            catch
            {
                // Skip invalid image and continue trying other files.
            }
        }

        return null;
    }

    private static int ScoreTextureCandidate(string path)
    {
        var file = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        var score = 0;

        if (file.Contains("ybot") || file.Contains("y-bot"))
        {
            score += 50;
        }

        if (file.Contains("diffuse") || file.Contains("albedo") || file.Contains("basecolor") || file.Contains("color"))
        {
            score += 20;
        }

        if (file.Contains("body") || file.Contains("skin"))
        {
            score += 10;
        }

        return score;
    }

    private static bool ShouldUseCullClockwiseFace(SkinnedMeshPart[] meshParts)
    {
        double orientationScore = 0.0;

        foreach (var part in meshParts)
        {
            var vertices = part.Vertices;
            var indices = part.Indices;

            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var i0 = indices[i];
                var i1 = indices[i + 1];
                var i2 = indices[i + 2];

                if (i0 < 0 || i1 < 0 || i2 < 0 ||
                    i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length)
                {
                    continue;
                }

                var p0 = vertices[i0].Position;
                var p1 = vertices[i1].Position;
                var p2 = vertices[i2].Position;

                var edge1 = p1 - p0;
                var edge2 = p2 - p0;
                var faceNormal = Vector3.Cross(edge1, edge2);
                var faceLenSq = faceNormal.LengthSquared();
                if (faceLenSq < 0.0000001f)
                {
                    continue;
                }

                faceNormal /= MathF.Sqrt(faceLenSq);

                var n0 = vertices[i0].Normal;
                var n1 = vertices[i1].Normal;
                var n2 = vertices[i2].Normal;
                var avgNormal = n0 + n1 + n2;
                var avgLenSq = avgNormal.LengthSquared();
                if (avgLenSq < 0.0000001f)
                {
                    continue;
                }

                avgNormal /= MathF.Sqrt(avgLenSq);
                orientationScore += Vector3.Dot(faceNormal, avgNormal);
            }
        }

        // Positive score usually indicates CCW front faces (use CullClockwiseFace).
        return orientationScore > 0.0;
    }

    private static Vector3 ToVector3(Color4D color)
    {
        return new Vector3(
            Math.Clamp(color.R, 0f, 1f),
            Math.Clamp(color.G, 0f, 1f),
            Math.Clamp(color.B, 0f, 1f));
    }

    private static Vector3 EnsureVisibleColor(Vector3 color)
    {
        if (color.LengthSquared() < 0.0001f)
        {
            return new Vector3(0.85f, 0.85f, 0.85f);
        }

        return color;
    }

    private static bool TryGetPrimaryTextureSlot(Material material, out TextureSlot textureSlot, out TextureType chosenType)
    {
        if (material.GetMaterialTexture(TextureType.Diffuse, 0, out textureSlot))
        {
            chosenType = TextureType.Diffuse;
            return true;
        }

        if (material.GetMaterialTexture(TextureType.BaseColor, 0, out textureSlot))
        {
            chosenType = TextureType.BaseColor;
            return true;
        }

        foreach (var textureType in Enum.GetValues<TextureType>())
        {
            if (textureType is TextureType.None or TextureType.Diffuse or TextureType.BaseColor)
            {
                continue;
            }

            if (material.GetMaterialTexture(textureType, 0, out textureSlot))
            {
                chosenType = textureType;
                return true;
            }
        }

        chosenType = TextureType.None;
        return false;
    }

    private static Texture2D? TryLoadTexture(GraphicsDevice graphicsDevice, Scene scene, string modelDirectory, string filePath, int textureIndex)
    {
        var embedded = TryLoadEmbeddedTexture(graphicsDevice, scene, filePath, textureIndex);
        if (embedded is not null)
        {
            return embedded;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var normalized = filePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var absolutePath = Path.IsPathRooted(normalized) ? normalized : Path.Combine(modelDirectory, normalized);
        if (!File.Exists(absolutePath))
        {
            return null;
        }

        using var stream = File.OpenRead(absolutePath);
        return Texture2D.FromStream(graphicsDevice, stream);
    }

    private static Texture2D? TryLoadEmbeddedTexture(GraphicsDevice graphicsDevice, Scene scene, string filePath, int textureIndex)
    {
        EmbeddedTexture? embedded = null;

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            embedded = scene.GetEmbeddedTexture(filePath.Trim());
        }

        if (embedded is null && textureIndex >= 0 && textureIndex < scene.TextureCount)
        {
            embedded = scene.Textures[textureIndex];
        }

        if (embedded is null && scene.TextureCount > 0)
        {
            var fallbackIndex = TryResolveEmbeddedTextureIndex(filePath);
            if (fallbackIndex >= 0 && fallbackIndex < scene.TextureCount)
            {
                embedded = scene.Textures[fallbackIndex];
            }
        }

        if (embedded is null)
        {
            return null;
        }

        if (embedded.HasCompressedData && embedded.CompressedData is { Length: > 0 } compressed)
        {
            using var compressedStream = new MemoryStream(compressed, writable: false);
            return Texture2D.FromStream(graphicsDevice, compressedStream);
        }

        if (embedded.HasNonCompressedData &&
            embedded.NonCompressedData is { Length: > 0 } texels &&
            embedded.Width > 0 &&
            embedded.Height > 0)
        {
            var width = embedded.Width;
            var height = embedded.Height;
            var pixelCount = width * height;
            if (texels.Length < pixelCount)
            {
                return null;
            }

            var pixels = new Color[pixelCount];
            for (var i = 0; i < pixelCount; i++)
            {
                var texel = texels[i];
                pixels[i] = new Color(texel.R, texel.G, texel.B, texel.A);
            }

            var texture = new Texture2D(graphicsDevice, width, height, false, SurfaceFormat.Color);
            texture.SetData(pixels);
            return texture;
        }

        return null;
    }

    private static int TryResolveEmbeddedTextureIndex(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return -1;
        }

        var trimmed = filePath.Trim();

        if (trimmed.StartsWith("*", StringComparison.Ordinal) &&
            int.TryParse(trimmed.AsSpan(1), out var starIndex))
        {
            return starIndex;
        }

        if (int.TryParse(trimmed, out var rawIndex))
        {
            return rawIndex;
        }

        var fileName = Path.GetFileNameWithoutExtension(trimmed);
        if (!string.IsNullOrWhiteSpace(fileName) && int.TryParse(fileName, out var fileIndex))
        {
            return fileIndex;
        }

        return -1;
    }

    private static AnimationClip LoadClip(AssimpContext importer, string clipPath, Skeleton skeleton, string clipName)
    {
        var scene = importer.ImportFile(clipPath, PostProcessSteps.None);
        if (scene is null || scene.AnimationCount == 0)
        {
            throw new InvalidOperationException($"Arquivo sem animação: {clipPath}");
        }

        var animation = scene.Animations[0];
        var tps = animation.TicksPerSecond > 0 ? (float)animation.TicksPerSecond : 25f;
        var duration = animation.DurationInTicks > 0 ? (float)(animation.DurationInTicks / tps) : 0f;

        var exactNameToIndex = skeleton.BoneNames
            .Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        var normalizedNameToIndex = BuildNormalizedBoneNameMap(skeleton.BoneNames);

        var tracks = new AnimationTrack?[skeleton.BoneCount];
        var matchedChannels = 0;
        var unmatchedChannels = 0;
        var sampleUnmatched = new List<string>(8);

        foreach (var channel in animation.NodeAnimationChannels)
        {
            if (!TryResolveBoneIndex(channel.NodeName, exactNameToIndex, normalizedNameToIndex, out var boneIndex))
            {
                unmatchedChannels++;
                if (sampleUnmatched.Count < 8)
                {
                    sampleUnmatched.Add(channel.NodeName);
                }
                continue;
            }

            matchedChannels++;

            var positionTimes = new float[channel.PositionKeyCount];
            var positions = new Vector3[channel.PositionKeyCount];
            for (var i = 0; i < channel.PositionKeyCount; i++)
            {
                var key = channel.PositionKeys[i];
                positionTimes[i] = (float)(key.Time / tps);
                positions[i] = new Vector3(key.Value.X, key.Value.Y, key.Value.Z);
            }

            var rotationTimes = new float[channel.RotationKeyCount];
            var rotations = new Quaternion[channel.RotationKeyCount];
            for (var i = 0; i < channel.RotationKeyCount; i++)
            {
                var key = channel.RotationKeys[i];
                rotationTimes[i] = (float)(key.Time / tps);
                rotations[i] = Quaternion.Normalize(new Quaternion(key.Value.X, key.Value.Y, key.Value.Z, key.Value.W));
            }

            var scaleTimes = new float[channel.ScalingKeyCount];
            var scales = new Vector3[channel.ScalingKeyCount];
            for (var i = 0; i < channel.ScalingKeyCount; i++)
            {
                var key = channel.ScalingKeys[i];
                scaleTimes[i] = (float)(key.Time / tps);
                scales[i] = new Vector3(key.Value.X, key.Value.Y, key.Value.Z);
            }

            if (positionTimes.Length > 0)
            {
                duration = Math.Max(duration, positionTimes[^1]);
            }

            if (rotationTimes.Length > 0)
            {
                duration = Math.Max(duration, rotationTimes[^1]);
            }

            if (scaleTimes.Length > 0)
            {
                duration = Math.Max(duration, scaleTimes[^1]);
            }

            tracks[boneIndex] = new AnimationTrack
            {
                PositionTimes = positionTimes,
                Positions = positions,
                RotationTimes = rotationTimes,
                Rotations = rotations,
                ScaleTimes = scaleTimes,
                Scales = scales
            };
        }

        return new AnimationClip
        {
            Name = clipName,
            DurationSeconds = Math.Max(duration, 0.01f),
            Looping = !clipName.Equals("bash", StringComparison.OrdinalIgnoreCase),
            TracksByBone = tracks
        };
    }

    private static Dictionary<string, int> BuildNormalizedBoneNameMap(string[] boneNames)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < boneNames.Length; i++)
        {
            var normalized = NormalizeBoneName(boneNames[i]);
            if (normalized.Length == 0 || map.ContainsKey(normalized))
            {
                continue;
            }

            map[normalized] = i;
        }

        return map;
    }

    private static bool TryResolveBoneIndex(
        string channelNodeName,
        Dictionary<string, int> exactNameToIndex,
        Dictionary<string, int> normalizedNameToIndex,
        out int boneIndex)
    {
        if (exactNameToIndex.TryGetValue(channelNodeName, out boneIndex))
        {
            return true;
        }

        var normalized = NormalizeBoneName(channelNodeName);
        return normalized.Length > 0 && normalizedNameToIndex.TryGetValue(normalized, out boneIndex);
    }

    private static string NormalizeBoneName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var segment = name.Trim();

        var assimpMarker = segment.IndexOf("_$AssimpFbx$_", StringComparison.OrdinalIgnoreCase);
        if (assimpMarker >= 0)
        {
            segment = segment[..assimpMarker];
        }

        var pipeIndex = segment.LastIndexOf('|');
        if (pipeIndex >= 0 && pipeIndex < segment.Length - 1)
        {
            segment = segment[(pipeIndex + 1)..];
        }

        var colonIndex = segment.LastIndexOf(':');
        if (colonIndex >= 0 && colonIndex < segment.Length - 1)
        {
            segment = segment[(colonIndex + 1)..];
        }

        var chars = new char[segment.Length];
        var count = 0;
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (char.IsLetterOrDigit(c))
            {
                chars[count++] = char.ToLowerInvariant(c);
            }
        }

        return count == 0 ? string.Empty : new string(chars, 0, count);
    }

    private static Matrix ToXna(Assimp.Matrix4x4 source)
    {
        return new Matrix(
            source.A1, source.A2, source.A3, source.A4,
            source.B1, source.B2, source.B3, source.B4,
            source.C1, source.C2, source.C3, source.C4,
            source.D1, source.D2, source.D3, source.D4);
    }

    private static void InsertInfluence(BoneInfluence[,] influences, int vertexIndex, int boneIndex, float weight)
    {
        if (weight <= 0f)
        {
            return;
        }

        var smallestSlot = 0;
        for (var i = 1; i < 4; i++)
        {
            if (influences[vertexIndex, i].Weight < influences[vertexIndex, smallestSlot].Weight)
            {
                smallestSlot = i;
            }
        }

        if (weight <= influences[vertexIndex, smallestSlot].Weight)
        {
            return;
        }

        influences[vertexIndex, smallestSlot] = new BoneInfluence
        {
            BoneIndex = boneIndex,
            Weight = weight
        };
    }

    private static int MapToLocalBoneIndex(Dictionary<int, int> globalToLocal, int globalBoneIndex)
    {
        if (globalToLocal.TryGetValue(globalBoneIndex, out var local))
        {
            return local;
        }

        // Zero-weight slots can reference default-initialized bone indices not present in the remap.
        // Route them to the first palette entry to keep vertex data valid.
        return 0;
    }

    private struct BoneInfluence
    {
        public int BoneIndex;
        public float Weight;
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        var lengthSquared = value.LengthSquared();
        if (lengthSquared < 0.000001f)
        {
            return fallback;
        }

        return value / MathF.Sqrt(lengthSquared);
    }

}
