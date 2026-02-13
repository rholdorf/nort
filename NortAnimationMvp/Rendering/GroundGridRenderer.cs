using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace NortAnimationMvp.Rendering;

public sealed class GroundGridRenderer : IDisposable
{
    private readonly GraphicsDevice _graphicsDevice;
    private readonly BasicEffect _effect;
    private readonly VertexBuffer _vertexBuffer;
    private readonly int _lineCount;

    public GroundGridRenderer(GraphicsDevice graphicsDevice, int halfExtentMeters = 20, float spacingMeters = 1f)
    {
        _graphicsDevice = graphicsDevice;
        _effect = new BasicEffect(graphicsDevice)
        {
            VertexColorEnabled = true,
            LightingEnabled = false
        };

        var vertices = BuildGridVertices(halfExtentMeters, spacingMeters);
        _lineCount = vertices.Length / 2;

        _vertexBuffer = new VertexBuffer(
            graphicsDevice,
            VertexPositionColor.VertexDeclaration,
            vertices.Length,
            BufferUsage.WriteOnly);
        _vertexBuffer.SetData(vertices);
    }

    public void Draw(Matrix view, Matrix projection)
    {
        _effect.World = Matrix.Identity;
        _effect.View = view;
        _effect.Projection = projection;

        _graphicsDevice.SetVertexBuffer(_vertexBuffer);
        _graphicsDevice.Indices = null;

        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _graphicsDevice.DrawPrimitives(PrimitiveType.LineList, 0, _lineCount);
        }
    }

    public void Dispose()
    {
        _vertexBuffer.Dispose();
        _effect.Dispose();
    }

    private static VertexPositionColor[] BuildGridVertices(int halfExtentMeters, float spacingMeters)
    {
        var lineCountPerAxis = halfExtentMeters * 2 + 1;
        var vertices = new VertexPositionColor[lineCountPerAxis * 4];
        var index = 0;

        for (var i = -halfExtentMeters; i <= halfExtentMeters; i++)
        {
            var coord = i * spacingMeters;
            var isCenter = i == 0;
            var color = isCenter ? new Color(195, 205, 220) : new Color(120, 140, 165);

            vertices[index++] = new VertexPositionColor(new Vector3(-halfExtentMeters * spacingMeters, 0f, coord), color);
            vertices[index++] = new VertexPositionColor(new Vector3(halfExtentMeters * spacingMeters, 0f, coord), color);

            vertices[index++] = new VertexPositionColor(new Vector3(coord, 0f, -halfExtentMeters * spacingMeters), color);
            vertices[index++] = new VertexPositionColor(new Vector3(coord, 0f, halfExtentMeters * spacingMeters), color);
        }

        return vertices;
    }
}
