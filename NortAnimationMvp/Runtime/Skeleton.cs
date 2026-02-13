using Microsoft.Xna.Framework;

namespace NortAnimationMvp.Runtime;

public sealed class Skeleton
{
    public required string[] BoneNames { get; init; }
    public required int[] ParentIndices { get; init; }
    public required Vector3[] BindPositions { get; init; }
    public required Quaternion[] BindRotations { get; init; }
    public required Vector3[] BindScales { get; init; }
    public required Matrix[] InverseBindPose { get; init; }

    public int BoneCount => BoneNames.Length;
}
