using Microsoft.Xna.Framework;

namespace NortAnimationMvp.Runtime;

public sealed class AnimationTrack
{
    public required float[] PositionTimes { get; init; }
    public required Vector3[] Positions { get; init; }
    public required float[] RotationTimes { get; init; }
    public required Quaternion[] Rotations { get; init; }
    public required float[] ScaleTimes { get; init; }
    public required Vector3[] Scales { get; init; }
}

public sealed class AnimationClip
{
    public required string Name { get; init; }
    public required float DurationSeconds { get; init; }
    public required bool Looping { get; init; }
    public required AnimationTrack?[] TracksByBone { get; init; }
}
