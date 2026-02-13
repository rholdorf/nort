using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;

namespace NortAnimationMvp.Runtime;

public struct SkinnedVertex : IVertexType
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoord;
    public Byte4 BlendIndices;
    public Vector4 BlendWeights;

    public static readonly VertexDeclaration VertexDeclaration = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(32, VertexElementFormat.Byte4, VertexElementUsage.BlendIndices, 0),
        new VertexElement(36, VertexElementFormat.Vector4, VertexElementUsage.BlendWeight, 0)
    );

    VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;
}
