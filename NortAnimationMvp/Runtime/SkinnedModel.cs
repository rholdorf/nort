namespace NortAnimationMvp.Runtime;

public sealed class SkinnedModel
{
    public required Skeleton Skeleton { get; init; }
    public required SkinnedMeshPart[] MeshParts { get; init; }
    public bool PreferCullClockwiseFace { get; init; }
}

public sealed class AnimationSet
{
    public required SkinnedModel Model { get; init; }
    public required Dictionary<string, AnimationClip> Clips { get; init; }
}
