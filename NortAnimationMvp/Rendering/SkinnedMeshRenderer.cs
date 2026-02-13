using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NortAnimationMvp.Runtime;

namespace NortAnimationMvp.Rendering;

public sealed class SkinnedMeshRenderer : IDisposable
{
    private readonly GraphicsDevice _graphicsDevice;
    private readonly SkinnedEffect _effect;
    private readonly RenderPart[] _parts;
    private readonly Texture2D[] _ownedTextures;
    private readonly Texture2D _whiteTexture;

    public SkinnedMeshRenderer(GraphicsDevice graphicsDevice, SkinnedModel model)
    {
        _graphicsDevice = graphicsDevice;
        _whiteTexture = CreateWhiteTexture(graphicsDevice);
        _effect = new SkinnedEffect(graphicsDevice)
        {
            WeightsPerVertex = 4,
            PreferPerPixelLighting = true
        };
        _effect.Texture = _whiteTexture;
        _effect.EnableDefaultLighting();
        _effect.AmbientLightColor = new Vector3(0.35f, 0.36f, 0.4f);
        _effect.DiffuseColor = new Vector3(0.95f, 0.95f, 0.95f);
        _effect.SpecularColor = new Vector3(0.22f, 0.22f, 0.22f);
        _effect.SpecularPower = 24f;
        _effect.EmissiveColor = new Vector3(0.03f, 0.03f, 0.035f);

        _effect.DirectionalLight0.Direction = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.25f));
        _effect.DirectionalLight0.DiffuseColor = new Vector3(0.95f, 0.93f, 0.88f);
        _effect.DirectionalLight0.SpecularColor = new Vector3(0.7f, 0.7f, 0.65f);

        _effect.DirectionalLight1.Direction = Vector3.Normalize(new Vector3(0.9f, -0.35f, 0.3f));
        _effect.DirectionalLight1.DiffuseColor = new Vector3(0.22f, 0.26f, 0.34f);
        _effect.DirectionalLight1.SpecularColor = new Vector3(0.08f, 0.1f, 0.12f);

        _effect.DirectionalLight2.Direction = Vector3.Normalize(new Vector3(0f, -0.45f, 1f));
        _effect.DirectionalLight2.DiffuseColor = new Vector3(0.15f, 0.13f, 0.11f);
        _effect.DirectionalLight2.SpecularColor = new Vector3(0.05f, 0.05f, 0.05f);

        _parts = model.MeshParts.Select(part => new RenderPart(graphicsDevice, part)).ToArray();
        _ownedTextures = model.MeshParts
            .Select(x => x.DiffuseTexture)
            .Where(x => x is not null)
            .Distinct()
            .Cast<Texture2D>()
            .ToArray();
    }

    public void Draw(Matrix world, Matrix view, Matrix projection, Matrix[] skinTransforms)
    {
        _effect.World = world;
        _effect.View = view;
        _effect.Projection = projection;

        foreach (var part in _parts)
        {
            for (var i = 0; i < part.BoneRemap.Length; i++)
            {
                part.Palette[i] = skinTransforms[part.BoneRemap[i]];
            }

            _effect.SetBoneTransforms(part.Palette);
            _effect.Texture = part.Texture ?? _whiteTexture;
            _effect.DiffuseColor = part.MaterialColor;

            _graphicsDevice.SetVertexBuffer(part.VertexBuffer);
            _graphicsDevice.Indices = part.IndexBuffer;

            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _graphicsDevice.DrawIndexedPrimitives(
                    PrimitiveType.TriangleList,
                    baseVertex: 0,
                    startIndex: 0,
                    primitiveCount: part.PrimitiveCount);
            }
        }
    }

    public void Dispose()
    {
        foreach (var part in _parts)
        {
            part.Dispose();
        }

        foreach (var texture in _ownedTextures)
        {
            texture.Dispose();
        }

        _effect.Dispose();
        _whiteTexture.Dispose();
    }

    private static Texture2D CreateWhiteTexture(GraphicsDevice graphicsDevice)
    {
        var texture = new Texture2D(graphicsDevice, 1, 1, false, SurfaceFormat.Color);
        texture.SetData(new[] { Color.White });
        return texture;
    }

    private sealed class RenderPart : IDisposable
    {
        public VertexBuffer VertexBuffer { get; }
        public IndexBuffer IndexBuffer { get; }
        public int PrimitiveCount { get; }
        public int[] BoneRemap { get; }
        public Matrix[] Palette { get; }
        public Texture2D? Texture { get; }
        public Vector3 MaterialColor { get; }

        public RenderPart(GraphicsDevice graphicsDevice, SkinnedMeshPart part)
        {
            BoneRemap = part.BoneRemap;
            Palette = new Matrix[BoneRemap.Length];
            PrimitiveCount = part.Indices.Length / 3;
            Texture = part.DiffuseTexture;
            MaterialColor = part.MaterialColor;

            VertexBuffer = new VertexBuffer(graphicsDevice, SkinnedVertex.VertexDeclaration, part.Vertices.Length, BufferUsage.WriteOnly);
            VertexBuffer.SetData(part.Vertices);

            IndexBuffer = new IndexBuffer(graphicsDevice, IndexElementSize.ThirtyTwoBits, part.Indices.Length, BufferUsage.WriteOnly);
            IndexBuffer.SetData(part.Indices);
        }

        public void Dispose()
        {
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
        }
    }
}
