using Microsoft.Xna.Framework;
using NortAnimationMvp.Runtime;

namespace NortAnimationMvp.Animation;

public sealed class PoseBuffer
{
    public readonly Vector3[] Positions;
    public readonly Quaternion[] Rotations;
    public readonly Vector3[] Scales;

    public int BoneCount => Positions.Length;

    public PoseBuffer(int boneCount)
    {
        Positions = new Vector3[boneCount];
        Rotations = new Quaternion[boneCount];
        Scales = new Vector3[boneCount];
    }

    public void CopyFrom(PoseBuffer source)
    {
        Array.Copy(source.Positions, Positions, BoneCount);
        Array.Copy(source.Rotations, Rotations, BoneCount);
        Array.Copy(source.Scales, Scales, BoneCount);
    }

    public void SetToBindPose(Skeleton skeleton)
    {
        Array.Copy(skeleton.BindPositions, Positions, BoneCount);
        Array.Copy(skeleton.BindRotations, Rotations, BoneCount);
        Array.Copy(skeleton.BindScales, Scales, BoneCount);
    }
}
