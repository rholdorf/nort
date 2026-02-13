using Microsoft.Xna.Framework;
using NortAnimationMvp.Animation;
using NortAnimationMvp.Runtime;

namespace NortAnimationMvp.Tests;

public class AnimatorTests
{
    [Fact]
    public void Update_ChangesSkinTransforms_WhenClipHasAnimatedTrack()
    {
        var skeleton = CreateTestSkeleton();
        var clip = CreateChildRotationClip("idle", looping: true);
        var animator = new Animator(skeleton, new Dictionary<string, AnimationClip> { ["idle"] = clip });

        animator.Play("idle", immediate: true);
        var before = animator.SkinTransforms[1];

        animator.Update(0.5f);
        var after = animator.SkinTransforms[1];

        Assert.True(MatrixDistance(before, after) > 0.01f);
    }

    [Fact]
    public void CrossFade_BlendsBetweenTwoClips()
    {
        var skeleton = CreateTestSkeleton();
        var clipA = CreateRootTranslationClip("idle", x: 0f);
        var clipB = CreateRootTranslationClip("walk", x: 2f);

        var animator = new Animator(
            skeleton,
            new Dictionary<string, AnimationClip>
            {
                ["idle"] = clipA,
                ["walk"] = clipB
            });

        animator.Play("idle", immediate: true);
        animator.CrossFade("walk", durationSeconds: 1f);
        animator.Update(0.5f);

        var blended = animator.SkinTransforms[0].Translation.X;
        Assert.InRange(blended, 0.7f, 1.3f);

        animator.Update(0.6f);
        Assert.Equal("walk", animator.CurrentClipName);
    }

    private static Skeleton CreateTestSkeleton()
    {
        return new Skeleton
        {
            BoneNames = ["Root", "Child"],
            ParentIndices = [-1, 0],
            BindPositions = [Vector3.Zero, new Vector3(0f, 1f, 0f)],
            BindRotations = [Quaternion.Identity, Quaternion.Identity],
            BindScales = [Vector3.One, Vector3.One],
            InverseBindPose = [Matrix.Identity, Matrix.Identity]
        };
    }

    private static AnimationClip CreateChildRotationClip(string name, bool looping)
    {
        var tracks = new AnimationTrack?[2];
        tracks[1] = new AnimationTrack
        {
            PositionTimes = [0f],
            Positions = [new Vector3(0f, 1f, 0f)],
            RotationTimes = [0f, 1f],
            Rotations = [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.Up, MathHelper.ToRadians(90f))],
            ScaleTimes = [0f],
            Scales = [Vector3.One]
        };

        return new AnimationClip
        {
            Name = name,
            DurationSeconds = 1f,
            Looping = looping,
            TracksByBone = tracks
        };
    }

    private static AnimationClip CreateRootTranslationClip(string name, float x)
    {
        var tracks = new AnimationTrack?[2];
        tracks[0] = new AnimationTrack
        {
            PositionTimes = [0f],
            Positions = [new Vector3(x, 0f, 0f)],
            RotationTimes = [0f],
            Rotations = [Quaternion.Identity],
            ScaleTimes = [0f],
            Scales = [Vector3.One]
        };

        return new AnimationClip
        {
            Name = name,
            DurationSeconds = 1f,
            Looping = true,
            TracksByBone = tracks
        };
    }

    private static float MatrixDistance(Matrix a, Matrix b)
    {
        var sum =
            MathF.Abs(a.M11 - b.M11) + MathF.Abs(a.M12 - b.M12) + MathF.Abs(a.M13 - b.M13) + MathF.Abs(a.M14 - b.M14) +
            MathF.Abs(a.M21 - b.M21) + MathF.Abs(a.M22 - b.M22) + MathF.Abs(a.M23 - b.M23) + MathF.Abs(a.M24 - b.M24) +
            MathF.Abs(a.M31 - b.M31) + MathF.Abs(a.M32 - b.M32) + MathF.Abs(a.M33 - b.M33) + MathF.Abs(a.M34 - b.M34) +
            MathF.Abs(a.M41 - b.M41) + MathF.Abs(a.M42 - b.M42) + MathF.Abs(a.M43 - b.M43) + MathF.Abs(a.M44 - b.M44);

        return sum;
    }
}
