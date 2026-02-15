using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;
using NortAnimationMvp.Runtime;
using Quaternion = Microsoft.Xna.Framework.Quaternion;

namespace NortAnimationMvp.Assets;

public sealed class GlbRuntimeLoader
{
    public AnimationSet Load(
        GraphicsDevice graphicsDevice,
        string modelPath,
        IReadOnlyDictionary<string, string> clipFiles)
    {
        var modelDoc = GlbDocument.Load(modelPath);
        var skeletonData = BuildSkeleton(modelDoc);
        var meshParts = BuildMeshParts(graphicsDevice, modelDoc, skeletonData, Path.GetDirectoryName(modelPath) ?? Directory.GetCurrentDirectory());

        var clips = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, clipPath) in clipFiles)
        {
            var clipDoc = GlbDocument.Load(clipPath);
            clips[name] = BuildClip(clipDoc, skeletonData.Skeleton, name);
        }

        return new AnimationSet
        {
            Model = new SkinnedModel
            {
                Skeleton = skeletonData.Skeleton,
                MeshParts = meshParts,
                PreferCullClockwiseFace = ShouldUseCullClockwiseFace(meshParts)
            },
            Clips = clips
        };
    }

    private static SkeletonBuildData BuildSkeleton(GlbDocument document)
    {
        var nodeCount = document.Root.Nodes?.Length ?? 0;
        if (nodeCount == 0)
        {
            throw new InvalidOperationException("GLB sem nodes para montar skeleton.");
        }

        var parentsByNode = Enumerable.Repeat(-1, nodeCount).ToArray();
        for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
        {
            var children = document.Root.Nodes![nodeIndex].Children;
            if (children is null)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (child >= 0 && child < nodeCount)
                {
                    parentsByNode[child] = nodeIndex;
                }
            }
        }

        var orderedNodeIndices = BuildTopologicalNodeOrder(document.Root, parentsByNode);
        var nodeToBone = new Dictionary<int, int>(orderedNodeIndices.Count);
        for (var boneIndex = 0; boneIndex < orderedNodeIndices.Count; boneIndex++)
        {
            nodeToBone[orderedNodeIndices[boneIndex]] = boneIndex;
        }

        var boneNames = new string[orderedNodeIndices.Count];
        var parentIndices = new int[orderedNodeIndices.Count];
        var bindPositions = new Vector3[orderedNodeIndices.Count];
        var bindRotations = new Quaternion[orderedNodeIndices.Count];
        var bindScales = new Vector3[orderedNodeIndices.Count];
        var bindGlobal = new Matrix[orderedNodeIndices.Count];
        var inverseBind = new Matrix[orderedNodeIndices.Count];

        for (var boneIndex = 0; boneIndex < orderedNodeIndices.Count; boneIndex++)
        {
            var nodeIndex = orderedNodeIndices[boneIndex];
            var node = document.Root.Nodes![nodeIndex];
            boneNames[boneIndex] = string.IsNullOrWhiteSpace(node.Name) ? $"Bone_{nodeIndex}" : node.Name;

            var parentNode = parentsByNode[nodeIndex];
            parentIndices[boneIndex] = parentNode >= 0 && nodeToBone.TryGetValue(parentNode, out var parentBone)
                ? parentBone
                : -1;

            var local = GetNodeLocalMatrix(node);
            if (!local.Decompose(out var scale, out var rotation, out var translation))
            {
                scale = Vector3.One;
                rotation = Quaternion.Identity;
                translation = Vector3.Zero;
            }

            bindPositions[boneIndex] = translation;
            bindRotations[boneIndex] = Quaternion.Normalize(rotation);
            bindScales[boneIndex] = scale;
            bindGlobal[boneIndex] = parentIndices[boneIndex] < 0
                ? local
                : local * bindGlobal[parentIndices[boneIndex]];
            inverseBind[boneIndex] = Matrix.Invert(bindGlobal[boneIndex]);
        }

        // Keep inverse bind derived from hierarchy bind pose.
        // Some GLB exports use matrix conventions that can mismatch this runtime and cause "exploded" skinning.

        var skeleton = new Skeleton
        {
            BoneNames = boneNames,
            ParentIndices = parentIndices,
            BindPositions = bindPositions,
            BindRotations = bindRotations,
            BindScales = bindScales,
            InverseBindPose = inverseBind
        };

        return new SkeletonBuildData(skeleton, nodeToBone);
    }

    private static List<int> BuildTopologicalNodeOrder(GlTfRoot root, int[] parentsByNode)
    {
        var visited = new bool[parentsByNode.Length];
        var order = new List<int>(parentsByNode.Length);

        void Visit(int nodeIndex)
        {
            if (visited[nodeIndex])
            {
                return;
            }

            visited[nodeIndex] = true;
            order.Add(nodeIndex);

            var children = root.Nodes![nodeIndex].Children;
            if (children is null)
            {
                return;
            }

            foreach (var child in children)
            {
                if (child >= 0 && child < parentsByNode.Length)
                {
                    Visit(child);
                }
            }
        }

        if (root.Scenes is { Length: > 0 })
        {
            var sceneIndex = root.Scene ?? 0;
            if (sceneIndex >= 0 && sceneIndex < root.Scenes.Length)
            {
                var sceneRoots = root.Scenes[sceneIndex].Nodes;
                if (sceneRoots is not null)
                {
                    foreach (var rootNode in sceneRoots)
                    {
                        if (rootNode >= 0 && rootNode < parentsByNode.Length)
                        {
                            Visit(rootNode);
                        }
                    }
                }
            }
        }

        for (var i = 0; i < parentsByNode.Length; i++)
        {
            if (parentsByNode[i] < 0)
            {
                Visit(i);
            }
        }

        for (var i = 0; i < parentsByNode.Length; i++)
        {
            Visit(i);
        }

        return order;
    }

    private static SkinnedMeshPart[] BuildMeshParts(
        GraphicsDevice graphicsDevice,
        GlbDocument document,
        SkeletonBuildData skeletonData,
        string modelDirectory)
    {
        var parts = new List<SkinnedMeshPart>();
        var materialTextures = LoadTexturesByMaterial(graphicsDevice, document, modelDirectory);
        var materialColors = LoadColorsByMaterial(document);

        var nodes = document.Root.Nodes;
        if (nodes is null || document.Root.Meshes is null)
        {
            return Array.Empty<SkinnedMeshPart>();
        }

        for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            var node = nodes[nodeIndex];
            if (!node.Mesh.HasValue || node.Mesh.Value < 0 || node.Mesh.Value >= document.Root.Meshes.Length)
            {
                continue;
            }

            var mesh = document.Root.Meshes[node.Mesh.Value];
            var skin = node.Skin.HasValue && document.Root.Skins is { Length: > 0 } && node.Skin.Value >= 0 && node.Skin.Value < document.Root.Skins.Length
                ? document.Root.Skins[node.Skin.Value]
                : null;

            var jointMap = BuildJointMap(skin, skeletonData.NodeToBoneIndex);

            for (var primitiveIndex = 0; primitiveIndex < mesh.Primitives.Length; primitiveIndex++)
            {
                var primitive = mesh.Primitives[primitiveIndex];
                if (primitive.Attributes is null || !primitive.Attributes.TryGetValue("POSITION", out var positionAccessor))
                {
                    continue;
                }

                var positions = ReadVector3Accessor(document, positionAccessor);
                var normals = primitive.Attributes.TryGetValue("NORMAL", out var normalAccessor)
                    ? ReadVector3Accessor(document, normalAccessor)
                    : null;
                var texCoords = primitive.Attributes.TryGetValue("TEXCOORD_0", out var uvAccessor)
                    ? ReadVector2Accessor(document, uvAccessor)
                    : null;
                var joints = primitive.Attributes.TryGetValue("JOINTS_0", out var jointsAccessor)
                    ? ReadUnsignedVec4Accessor(document, jointsAccessor)
                    : null;
                var weights = primitive.Attributes.TryGetValue("WEIGHTS_0", out var weightsAccessor)
                    ? ReadVector4Accessor(document, weightsAccessor)
                    : null;

                var vertexCount = positions.Length;
                var vertices = new SkinnedVertex[vertexCount];
                var influences = new BoneInfluence[vertexCount, 4];

                for (var i = 0; i < vertexCount; i++)
                {
                    var normal = normals is not null && i < normals.Length ? normals[i] : Vector3.Up;
                    var uv = texCoords is not null && i < texCoords.Length ? texCoords[i] : Vector2.Zero;
                    vertices[i] = new SkinnedVertex
                    {
                        Position = positions[i],
                        Normal = SafeNormalize(normal, Vector3.Up),
                        TexCoord = uv,
                        BlendIndices = new Byte4(0, 0, 0, 0),
                        BlendWeights = Vector4.Zero
                    };
                }

                if (joints is not null && weights is not null)
                {
                    for (var i = 0; i < vertexCount; i++)
                    {
                        var j = joints[i];
                        var w = weights[i];
                        InsertInfluence(influences, i, ResolveJoint(jointMap, j.X), w.X);
                        InsertInfluence(influences, i, ResolveJoint(jointMap, j.Y), w.Y);
                        InsertInfluence(influences, i, ResolveJoint(jointMap, j.Z), w.Z);
                        InsertInfluence(influences, i, ResolveJoint(jointMap, j.W), w.W);
                    }
                }

                var usedBones = new HashSet<int>();
                for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
                {
                    var total = influences[vertexIndex, 0].Weight + influences[vertexIndex, 1].Weight + influences[vertexIndex, 2].Weight + influences[vertexIndex, 3].Weight;
                    if (total <= 0.00001f)
                    {
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
                    remap = [0];
                }

                var globalToLocal = new Dictionary<int, int>(remap.Length);
                for (var i = 0; i < remap.Length; i++)
                {
                    globalToLocal[remap[i]] = i;
                }

                for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
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

                var indices = primitive.Indices.HasValue
                    ? ReadIndicesAccessor(document, primitive.Indices.Value)
                    : Enumerable.Range(0, vertexCount).ToArray();

                var materialIndex = primitive.Material ?? -1;
                parts.Add(new SkinnedMeshPart
                {
                    Name = $"Mesh_{node.Mesh.Value}_Prim_{primitiveIndex}",
                    Vertices = vertices,
                    Indices = indices,
                    BoneRemap = remap,
                    DiffuseTexture = materialTextures.TryGetValue(materialIndex, out var texture) ? texture : null,
                    MaterialColor = materialColors.TryGetValue(materialIndex, out var color) ? color : Vector3.One
                });
            }
        }

        return parts.ToArray();
    }

    private static Dictionary<int, int> BuildJointMap(GlTfSkin? skin, Dictionary<int, int> nodeToBone)
    {
        var map = new Dictionary<int, int>();
        if (skin?.Joints is null)
        {
            return map;
        }

        for (var i = 0; i < skin.Joints.Length; i++)
        {
            var nodeIndex = skin.Joints[i];
            if (nodeToBone.TryGetValue(nodeIndex, out var boneIndex))
            {
                map[i] = boneIndex;
            }
        }

        return map;
    }

    private static int ResolveJoint(Dictionary<int, int> jointMap, int joint)
    {
        if (jointMap.TryGetValue(joint, out var bone))
        {
            return bone;
        }

        return 0;
    }

    private static AnimationClip BuildClip(GlbDocument document, Skeleton skeleton, string clipName)
    {
        if (document.Root.Animations is null || document.Root.Animations.Length == 0)
        {
            throw new InvalidOperationException($"Arquivo sem animação: {clipName}");
        }

        var animation = document.Root.Animations[0];
        var tracks = new AnimationTrack?[skeleton.BoneCount];

        var exactNameToIndex = skeleton.BoneNames
            .Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        var normalizedNameToIndex = BuildNormalizedBoneNameMap(skeleton.BoneNames);

        var duration = 0f;

        if (animation.Channels is null || animation.Samplers is null)
        {
            return new AnimationClip
            {
                Name = clipName,
                DurationSeconds = 0.01f,
                Looping = !clipName.Equals("bash", StringComparison.OrdinalIgnoreCase),
                TracksByBone = tracks
            };
        }

        var trackBuilders = new Dictionary<int, TrackBuilder>();

        foreach (var channel in animation.Channels)
        {
            var target = channel.Target;
            if (!target.Node.HasValue || channel.Sampler < 0 || channel.Sampler >= animation.Samplers.Length)
            {
                continue;
            }

            var targetNode = target.Node.Value;
            if (document.Root.Nodes is null || targetNode < 0 || targetNode >= document.Root.Nodes.Length)
            {
                continue;
            }

            var nodeName = document.Root.Nodes[targetNode].Name ?? string.Empty;
            if (!TryResolveBoneIndex(nodeName, exactNameToIndex, normalizedNameToIndex, out var boneIndex))
            {
                continue;
            }

            var sampler = animation.Samplers[channel.Sampler];
            var inputTimes = ReadScalarAccessor(document, sampler.Input);
            if (inputTimes.Length == 0)
            {
                continue;
            }

            duration = Math.Max(duration, inputTimes[^1]);

            if (!trackBuilders.TryGetValue(boneIndex, out var builder))
            {
                builder = new TrackBuilder();
                trackBuilders[boneIndex] = builder;
            }

            var interpolation = sampler.Interpolation ?? "LINEAR";
            switch ((target.Path ?? string.Empty).ToLowerInvariant())
            {
                case "translation":
                {
                    var values = ReadVector3Accessor(document, sampler.Output);
                    values = NormalizeCubicSpline(values, interpolation);
                    builder.PositionTimes = inputTimes;
                    builder.Positions = values;
                    break;
                }
                case "rotation":
                {
                    var values = ReadQuaternionAccessor(document, sampler.Output);
                    values = NormalizeCubicSpline(values, interpolation);
                    builder.RotationTimes = inputTimes;
                    builder.Rotations = values.Select(Quaternion.Normalize).ToArray();
                    break;
                }
                case "scale":
                {
                    var values = ReadVector3Accessor(document, sampler.Output);
                    values = NormalizeCubicSpline(values, interpolation);
                    builder.ScaleTimes = inputTimes;
                    builder.Scales = values;
                    break;
                }
            }
        }

        foreach (var (boneIndex, builder) in trackBuilders)
        {
            tracks[boneIndex] = new AnimationTrack
            {
                PositionTimes = builder.PositionTimes ?? Array.Empty<float>(),
                Positions = builder.Positions ?? Array.Empty<Vector3>(),
                RotationTimes = builder.RotationTimes ?? Array.Empty<float>(),
                Rotations = builder.Rotations ?? Array.Empty<Quaternion>(),
                ScaleTimes = builder.ScaleTimes ?? Array.Empty<float>(),
                Scales = builder.Scales ?? Array.Empty<Vector3>()
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

    private static T[] NormalizeCubicSpline<T>(T[] values, string interpolation)
    {
        if (!interpolation.Equals("CUBICSPLINE", StringComparison.OrdinalIgnoreCase) || values.Length < 3)
        {
            return values;
        }

        var keyCount = values.Length / 3;
        var result = new T[keyCount];
        for (var i = 0; i < keyCount; i++)
        {
            result[i] = values[(i * 3) + 1];
        }

        return result;
    }

    private static Dictionary<int, Texture2D?> LoadTexturesByMaterial(GraphicsDevice graphicsDevice, GlbDocument document, string modelDirectory)
    {
        var result = new Dictionary<int, Texture2D?>();
        if (document.Root.Materials is null)
        {
            return result;
        }

        for (var materialIndex = 0; materialIndex < document.Root.Materials.Length; materialIndex++)
        {
            var material = document.Root.Materials[materialIndex];
            var texIndex = material.PbrMetallicRoughness?.BaseColorTexture?.Index;
            if (!texIndex.HasValue || document.Root.Textures is null || texIndex.Value < 0 || texIndex.Value >= document.Root.Textures.Length)
            {
                result[materialIndex] = null;
                continue;
            }

            var texture = document.Root.Textures[texIndex.Value];
            if (!texture.Source.HasValue || document.Root.Images is null || texture.Source.Value < 0 || texture.Source.Value >= document.Root.Images.Length)
            {
                result[materialIndex] = null;
                continue;
            }

            result[materialIndex] = TryLoadImageTexture(graphicsDevice, document, document.Root.Images[texture.Source.Value], modelDirectory);
        }

        return result;
    }

    private static Texture2D? TryLoadImageTexture(GraphicsDevice graphicsDevice, GlbDocument document, GlTfImage image, string modelDirectory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(image.Uri))
            {
                if (image.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = image.Uri.IndexOf(',');
                    if (comma > 0)
                    {
                        var payload = image.Uri[(comma + 1)..];
                        var bytes = Convert.FromBase64String(payload);
                        using var ms = new MemoryStream(bytes, writable: false);
                        return Texture2D.FromStream(graphicsDevice, ms);
                    }
                }
                else
                {
                    var texturePath = Path.Combine(modelDirectory, image.Uri.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(texturePath))
                    {
                        using var fs = File.OpenRead(texturePath);
                        return Texture2D.FromStream(graphicsDevice, fs);
                    }
                }
            }

            if (image.BufferView.HasValue)
            {
                var bytes = GetBufferViewBytes(document, image.BufferView.Value);
                using var ms = new MemoryStream(bytes, writable: false);
                return Texture2D.FromStream(graphicsDevice, ms);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static Dictionary<int, Vector3> LoadColorsByMaterial(GlbDocument document)
    {
        var result = new Dictionary<int, Vector3>();
        if (document.Root.Materials is null)
        {
            return result;
        }

        for (var i = 0; i < document.Root.Materials.Length; i++)
        {
            var baseColor = document.Root.Materials[i].PbrMetallicRoughness?.BaseColorFactor;
            if (baseColor is { Length: >= 3 })
            {
                result[i] = EnsureVisibleColor(new Vector3(baseColor[0], baseColor[1], baseColor[2]));
            }
            else
            {
                result[i] = new Vector3(0.95f, 0.95f, 0.95f);
            }
        }

        return result;
    }

    private static Vector3 EnsureVisibleColor(Vector3 color)
    {
        if (color.LengthSquared() < 0.0001f)
        {
            return new Vector3(0.85f, 0.85f, 0.85f);
        }

        return color;
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

        return orientationScore > 0.0;
    }

    private static string NormalizeBoneName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var segment = name.Trim();
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

    private static Matrix GetNodeLocalMatrix(GlTfNode node)
    {
        if (node.Matrix is { Length: 16 } m)
        {
            return new Matrix(
                m[0], m[4], m[8], m[12],
                m[1], m[5], m[9], m[13],
                m[2], m[6], m[10], m[14],
                m[3], m[7], m[11], m[15]);
        }

        var translation = node.Translation is { Length: >= 3 }
            ? new Vector3(node.Translation[0], node.Translation[1], node.Translation[2])
            : Vector3.Zero;
        var rotation = node.Rotation is { Length: >= 4 }
            ? Quaternion.Normalize(new Quaternion(node.Rotation[0], node.Rotation[1], node.Rotation[2], node.Rotation[3]))
            : Quaternion.Identity;
        var scale = node.Scale is { Length: >= 3 }
            ? new Vector3(node.Scale[0], node.Scale[1], node.Scale[2])
            : Vector3.One;

        return Matrix.CreateScale(scale) * Matrix.CreateFromQuaternion(rotation) * Matrix.CreateTranslation(translation);
    }

    private static float[] ReadScalarAccessor(GlbDocument doc, int accessorIndex)
    {
        var accessor = doc.Root.Accessors![accessorIndex];
        var stride = GetElementStride(doc, accessor);
        var componentSize = GetComponentSize(accessor.ComponentType);
        var count = accessor.Count;

        var result = new float[count];
        var data = GetAccessorData(doc, accessor);
        for (var i = 0; i < count; i++)
        {
            var offset = i * stride;
            result[i] = ReadComponentAsFloat(data, offset, accessor.ComponentType, accessor.Normalized ?? false);
            if (!float.IsFinite(result[i]))
            {
                result[i] = 0f;
            }
        }

        return result;
    }

    private static Vector2[] ReadVector2Accessor(GlbDocument doc, int accessorIndex)
    {
        var accessor = doc.Root.Accessors![accessorIndex];
        ValidateAccessorType(accessor, "VEC2");
        var stride = GetElementStride(doc, accessor);
        var componentSize = GetComponentSize(accessor.ComponentType);
        var data = GetAccessorData(doc, accessor);

        var result = new Vector2[accessor.Count];
        for (var i = 0; i < accessor.Count; i++)
        {
            var o = i * stride;
            result[i] = new Vector2(
                ReadComponentAsFloat(data, o, accessor.ComponentType, accessor.Normalized ?? false),
                ReadComponentAsFloat(data, o + componentSize, accessor.ComponentType, accessor.Normalized ?? false));
        }

        return result;
    }

    private static Vector3[] ReadVector3Accessor(GlbDocument doc, int accessorIndex)
    {
        var accessor = doc.Root.Accessors![accessorIndex];
        ValidateAccessorType(accessor, "VEC3");
        var stride = GetElementStride(doc, accessor);
        var componentSize = GetComponentSize(accessor.ComponentType);
        var data = GetAccessorData(doc, accessor);

        var result = new Vector3[accessor.Count];
        for (var i = 0; i < accessor.Count; i++)
        {
            var o = i * stride;
            result[i] = new Vector3(
                ReadComponentAsFloat(data, o, accessor.ComponentType, accessor.Normalized ?? false),
                ReadComponentAsFloat(data, o + componentSize, accessor.ComponentType, accessor.Normalized ?? false),
                ReadComponentAsFloat(data, o + (2 * componentSize), accessor.ComponentType, accessor.Normalized ?? false));
        }

        return result;
    }

    private static Vector4[] ReadVector4Accessor(GlbDocument doc, int accessorIndex)
    {
        var accessor = doc.Root.Accessors![accessorIndex];
        ValidateAccessorType(accessor, "VEC4");
        var stride = GetElementStride(doc, accessor);
        var componentSize = GetComponentSize(accessor.ComponentType);
        var data = GetAccessorData(doc, accessor);

        var result = new Vector4[accessor.Count];
        for (var i = 0; i < accessor.Count; i++)
        {
            var o = i * stride;
            result[i] = new Vector4(
                ReadComponentAsFloat(data, o, accessor.ComponentType, accessor.Normalized ?? false),
                ReadComponentAsFloat(data, o + componentSize, accessor.ComponentType, accessor.Normalized ?? false),
                ReadComponentAsFloat(data, o + (2 * componentSize), accessor.ComponentType, accessor.Normalized ?? false),
                ReadComponentAsFloat(data, o + (3 * componentSize), accessor.ComponentType, accessor.Normalized ?? false));
        }

        return result;
    }

    private static Quaternion[] ReadQuaternionAccessor(GlbDocument doc, int accessorIndex)
    {
        var v = ReadVector4Accessor(doc, accessorIndex);
        var q = new Quaternion[v.Length];
        for (var i = 0; i < v.Length; i++)
        {
            q[i] = Quaternion.Normalize(new Quaternion(v[i].X, v[i].Y, v[i].Z, v[i].W));
        }

        return q;
    }

    private static Int4[] ReadUnsignedVec4Accessor(GlbDocument doc, int accessorIndex)
    {
        var accessor = doc.Root.Accessors![accessorIndex];
        ValidateAccessorType(accessor, "VEC4");
        var stride = GetElementStride(doc, accessor);
        var componentSize = GetComponentSize(accessor.ComponentType);
        var data = GetAccessorData(doc, accessor);

        var result = new Int4[accessor.Count];
        for (var i = 0; i < accessor.Count; i++)
        {
            var o = i * stride;
            result[i] = new Int4(
                ReadComponentAsInt(data, o, accessor.ComponentType),
                ReadComponentAsInt(data, o + componentSize, accessor.ComponentType),
                ReadComponentAsInt(data, o + (2 * componentSize), accessor.ComponentType),
                ReadComponentAsInt(data, o + (3 * componentSize), accessor.ComponentType));
        }

        return result;
    }

    private static Matrix[] ReadMatrix4Accessor(GlbDocument doc, int accessorIndex)
    {
        var accessor = doc.Root.Accessors![accessorIndex];
        ValidateAccessorType(accessor, "MAT4");
        var stride = GetElementStride(doc, accessor);
        var data = GetAccessorData(doc, accessor);

        var result = new Matrix[accessor.Count];
        for (var i = 0; i < accessor.Count; i++)
        {
            var o = i * stride;
            var m = new float[16];
            for (var c = 0; c < 16; c++)
            {
                m[c] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(o + (c * 4), 4));
            }

            result[i] = new Matrix(
                m[0], m[4], m[8], m[12],
                m[1], m[5], m[9], m[13],
                m[2], m[6], m[10], m[14],
                m[3], m[7], m[11], m[15]);
        }

        return result;
    }

    private static int[] ReadIndicesAccessor(GlbDocument doc, int accessorIndex)
    {
        var accessor = doc.Root.Accessors![accessorIndex];
        if (!string.Equals(accessor.Type, "SCALAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Accessor de índice inválido.");
        }

        var stride = GetElementStride(doc, accessor);
        var data = GetAccessorData(doc, accessor);
        var result = new int[accessor.Count];

        for (var i = 0; i < accessor.Count; i++)
        {
            var o = i * stride;
            result[i] = ReadComponentAsInt(data, o, accessor.ComponentType);
        }

        return result;
    }

    private static byte[] GetBufferViewBytes(GlbDocument doc, int bufferViewIndex)
    {
        var view = doc.Root.BufferViews![bufferViewIndex];
        var start = view.ByteOffset ?? 0;
        var len = view.ByteLength;
        var bytes = new byte[len];
        doc.Binary.Slice(start, len).CopyTo(bytes);
        return bytes;
    }

    private static ReadOnlySpan<byte> GetAccessorData(GlbDocument doc, GlTfAccessor accessor)
    {
        if (!accessor.BufferView.HasValue || doc.Root.BufferViews is null)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        var view = doc.Root.BufferViews[accessor.BufferView.Value];
        var viewOffset = view.ByteOffset ?? 0;
        var accessorOffset = accessor.ByteOffset ?? 0;
        var start = viewOffset + accessorOffset;
        var length = view.ByteLength - accessorOffset;
        return doc.Binary.Slice(start, Math.Max(0, length)).Span;
    }

    private static int GetElementStride(GlbDocument doc, GlTfAccessor accessor)
    {
        var componentCount = GetComponentCount(accessor.Type);
        var componentSize = GetComponentSize(accessor.ComponentType);
        var packed = componentCount * componentSize;
        if (!accessor.BufferView.HasValue || doc.Root.BufferViews is null)
        {
            return packed;
        }

        var view = doc.Root.BufferViews[accessor.BufferView.Value];
        return view.ByteStride ?? packed;
    }

    private static int GetComponentCount(string type)
    {
        return type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT4" => 16,
            _ => throw new InvalidOperationException($"Tipo de accessor não suportado: {type}")
        };
    }

    private static int GetComponentSize(int componentType)
    {
        return componentType switch
        {
            5120 => 1,
            5121 => 1,
            5122 => 2,
            5123 => 2,
            5125 => 4,
            5126 => 4,
            _ => throw new InvalidOperationException($"ComponentType não suportado: {componentType}")
        };
    }

    private static float ReadComponentAsFloat(ReadOnlySpan<byte> data, int offset, int componentType, bool normalized)
    {
        return componentType switch
        {
            5126 => BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4)),
            5121 => normalized ? data[offset] / 255f : data[offset],
            5123 => normalized
                ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2)) / 65535f
                : BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2)),
            5120 => normalized
                ? MathF.Max((sbyte)data[offset], -127) / 127f
                : (sbyte)data[offset],
            5122 => normalized
                ? MathF.Max(BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2)), -32767) / 32767f
                : BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2)),
            5125 => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)),
            _ => throw new InvalidOperationException($"ComponentType não suportado: {componentType}")
        };
    }

    private static int ReadComponentAsInt(ReadOnlySpan<byte> data, int offset, int componentType)
    {
        return componentType switch
        {
            5121 => data[offset],
            5123 => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2)),
            5125 => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)),
            5120 => (sbyte)data[offset],
            5122 => BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2)),
            _ => throw new InvalidOperationException($"Tipo de índice de junta não suportado: {componentType}")
        };
    }

    private static void ValidateAccessorType(GlTfAccessor accessor, string expected)
    {
        if (!string.Equals(accessor.Type, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Accessor type inválido: esperado {expected}, atual {accessor.Type}");
        }
    }

    private static int MapToLocalBoneIndex(Dictionary<int, int> globalToLocal, int globalBoneIndex)
    {
        if (globalToLocal.TryGetValue(globalBoneIndex, out var local))
        {
            return local;
        }

        return 0;
    }

    private static void InsertInfluence(BoneInfluence[,] influences, int vertexIndex, int boneIndex, float weight)
    {
        if (weight <= 0f || vertexIndex < 0 || vertexIndex >= influences.GetLength(0))
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

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        var lengthSquared = value.LengthSquared();
        if (lengthSquared < 0.000001f)
        {
            return fallback;
        }

        return value / MathF.Sqrt(lengthSquared);
    }

    private readonly record struct SkeletonBuildData(Skeleton Skeleton, Dictionary<int, int> NodeToBoneIndex);

    private readonly record struct Int4(int X, int Y, int Z, int W);

    private struct BoneInfluence
    {
        public int BoneIndex;
        public float Weight;
    }

    private sealed class TrackBuilder
    {
        public float[]? PositionTimes;
        public Vector3[]? Positions;
        public float[]? RotationTimes;
        public Quaternion[]? Rotations;
        public float[]? ScaleTimes;
        public Vector3[]? Scales;
    }

    private sealed class GlbDocument
    {
        public required GlTfRoot Root { get; init; }
        public required ReadOnlyMemory<byte> Binary { get; init; }

        public static GlbDocument Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 20)
            {
                throw new InvalidOperationException($"Arquivo GLB inválido: {path}");
            }

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
            if (magic != 0x46546C67)
            {
                throw new InvalidOperationException($"Arquivo não é GLB: {path}");
            }

            var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
            if (version != 2)
            {
                throw new InvalidOperationException($"Versão GLB não suportada ({version}) em {path}");
            }

            var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
            if (totalLength > bytes.Length)
            {
                throw new InvalidOperationException($"GLB truncado: {path}");
            }

            var offset = 12;
            byte[]? jsonChunk = null;
            ReadOnlyMemory<byte> binChunk = ReadOnlyMemory<byte>.Empty;

            while (offset + 8 <= totalLength)
            {
                var chunkLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
                var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
                offset += 8;

                if (offset + chunkLength > totalLength)
                {
                    break;
                }

                if (chunkType == 0x4E4F534A)
                {
                    jsonChunk = bytes.AsSpan(offset, chunkLength).ToArray();
                }
                else if (chunkType == 0x004E4942)
                {
                    binChunk = bytes.AsMemory(offset, chunkLength);
                }

                offset += chunkLength;
            }

            if (jsonChunk is null)
            {
                throw new InvalidOperationException($"GLB sem chunk JSON: {path}");
            }

            var json = Encoding.UTF8.GetString(jsonChunk).TrimEnd('\0', ' ', '\t', '\r', '\n');
            var root = JsonSerializer.Deserialize<GlTfRoot>(json, JsonOptions)
                ?? throw new InvalidOperationException($"Falha ao ler JSON do GLB: {path}");

            return new GlbDocument
            {
                Root = root,
                Binary = binChunk
            };
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private sealed class GlTfRoot
    {
        [JsonPropertyName("scene")]
        public int? Scene { get; init; }

        [JsonPropertyName("scenes")]
        public GlTfScene[]? Scenes { get; init; }

        [JsonPropertyName("nodes")]
        public GlTfNode[]? Nodes { get; init; }

        [JsonPropertyName("meshes")]
        public GlTfMesh[]? Meshes { get; init; }

        [JsonPropertyName("skins")]
        public GlTfSkin[]? Skins { get; init; }

        [JsonPropertyName("animations")]
        public GlTfAnimation[]? Animations { get; init; }

        [JsonPropertyName("accessors")]
        public GlTfAccessor[]? Accessors { get; init; }

        [JsonPropertyName("bufferViews")]
        public GlTfBufferView[]? BufferViews { get; init; }

        [JsonPropertyName("materials")]
        public GlTfMaterial[]? Materials { get; init; }

        [JsonPropertyName("textures")]
        public GlTfTexture[]? Textures { get; init; }

        [JsonPropertyName("images")]
        public GlTfImage[]? Images { get; init; }
    }

    private sealed class GlTfScene
    {
        [JsonPropertyName("nodes")]
        public int[]? Nodes { get; init; }
    }

    private sealed class GlTfNode
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("children")]
        public int[]? Children { get; init; }

        [JsonPropertyName("mesh")]
        public int? Mesh { get; init; }

        [JsonPropertyName("skin")]
        public int? Skin { get; init; }

        [JsonPropertyName("translation")]
        public float[]? Translation { get; init; }

        [JsonPropertyName("rotation")]
        public float[]? Rotation { get; init; }

        [JsonPropertyName("scale")]
        public float[]? Scale { get; init; }

        [JsonPropertyName("matrix")]
        public float[]? Matrix { get; init; }
    }

    private sealed class GlTfMesh
    {
        [JsonPropertyName("primitives")]
        public GlTfPrimitive[] Primitives { get; init; } = Array.Empty<GlTfPrimitive>();
    }

    private sealed class GlTfPrimitive
    {
        [JsonPropertyName("attributes")]
        public Dictionary<string, int>? Attributes { get; init; }

        [JsonPropertyName("indices")]
        public int? Indices { get; init; }

        [JsonPropertyName("material")]
        public int? Material { get; init; }
    }

    private sealed class GlTfSkin
    {
        [JsonPropertyName("joints")]
        public int[]? Joints { get; init; }

        [JsonPropertyName("inverseBindMatrices")]
        public int? InverseBindMatrices { get; init; }
    }

    private sealed class GlTfAnimation
    {
        [JsonPropertyName("samplers")]
        public GlTfAnimationSampler[]? Samplers { get; init; }

        [JsonPropertyName("channels")]
        public GlTfAnimationChannel[]? Channels { get; init; }
    }

    private sealed class GlTfAnimationSampler
    {
        [JsonPropertyName("input")]
        public int Input { get; init; }

        [JsonPropertyName("output")]
        public int Output { get; init; }

        [JsonPropertyName("interpolation")]
        public string? Interpolation { get; init; }
    }

    private sealed class GlTfAnimationChannel
    {
        [JsonPropertyName("sampler")]
        public int Sampler { get; init; }

        [JsonPropertyName("target")]
        public GlTfAnimationTarget Target { get; init; } = new();
    }

    private sealed class GlTfAnimationTarget
    {
        [JsonPropertyName("node")]
        public int? Node { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }
    }

    private sealed class GlTfAccessor
    {
        [JsonPropertyName("bufferView")]
        public int? BufferView { get; init; }

        [JsonPropertyName("byteOffset")]
        public int? ByteOffset { get; init; }

        [JsonPropertyName("componentType")]
        public int ComponentType { get; init; }

        [JsonPropertyName("count")]
        public int Count { get; init; }

        [JsonPropertyName("type")]
        public string Type { get; init; } = "SCALAR";

        [JsonPropertyName("normalized")]
        public bool? Normalized { get; init; }
    }

    private sealed class GlTfBufferView
    {
        [JsonPropertyName("buffer")]
        public int Buffer { get; init; }

        [JsonPropertyName("byteOffset")]
        public int? ByteOffset { get; init; }

        [JsonPropertyName("byteLength")]
        public int ByteLength { get; init; }

        [JsonPropertyName("byteStride")]
        public int? ByteStride { get; init; }
    }

    private sealed class GlTfMaterial
    {
        [JsonPropertyName("pbrMetallicRoughness")]
        public GlTfPbr? PbrMetallicRoughness { get; init; }
    }

    private sealed class GlTfPbr
    {
        [JsonPropertyName("baseColorFactor")]
        public float[]? BaseColorFactor { get; init; }

        [JsonPropertyName("baseColorTexture")]
        public GlTfTextureInfo? BaseColorTexture { get; init; }
    }

    private sealed class GlTfTextureInfo
    {
        [JsonPropertyName("index")]
        public int Index { get; init; }
    }

    private sealed class GlTfTexture
    {
        [JsonPropertyName("source")]
        public int? Source { get; init; }
    }

    private sealed class GlTfImage
    {
        [JsonPropertyName("uri")]
        public string? Uri { get; init; }

        [JsonPropertyName("bufferView")]
        public int? BufferView { get; init; }

        [JsonPropertyName("mimeType")]
        public string? MimeType { get; init; }
    }
}
