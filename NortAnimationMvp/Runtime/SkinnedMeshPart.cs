using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework;

namespace NortAnimationMvp.Runtime;

public sealed class SkinnedMeshPart
{
    public required string Name { get; init; }
    public required SkinnedVertex[] Vertices { get; init; }
    public required int[] Indices { get; init; }
    public required int[] BoneRemap { get; init; }
    public Texture2D? DiffuseTexture { get; init; }
    public Vector3 MaterialColor { get; init; } = Vector3.One;
}
