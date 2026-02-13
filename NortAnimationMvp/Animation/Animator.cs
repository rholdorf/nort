using Microsoft.Xna.Framework;
using NortAnimationMvp.Runtime;

namespace NortAnimationMvp.Animation;

public sealed class Animator
{
    private readonly Skeleton _skeleton;
    private readonly Dictionary<string, AnimationClip> _clips;

    private readonly PoseBuffer _basePose;
    private readonly PoseBuffer _blendTargetPose;
    private readonly PoseBuffer _finalPose;
    private readonly Matrix[] _modelPose;

    private AnimationClip? _currentClip;
    private AnimationClip? _targetClip;
    private float _currentTime;
    private float _targetTime;
    private float _playbackSpeed = 1f;

    private float _transitionElapsed;
    private float _transitionDuration;

    public Matrix[] SkinTransforms { get; }
    public string? CurrentClipName => _currentClip?.Name;

    public Animator(Skeleton skeleton, Dictionary<string, AnimationClip> clips)
    {
        _skeleton = skeleton;
        _clips = clips;
        _basePose = new PoseBuffer(skeleton.BoneCount);
        _blendTargetPose = new PoseBuffer(skeleton.BoneCount);
        _finalPose = new PoseBuffer(skeleton.BoneCount);
        _modelPose = new Matrix[skeleton.BoneCount];
        SkinTransforms = new Matrix[skeleton.BoneCount];
    }

    public void Play(string clipName, bool immediate = false)
    {
        if (!_clips.TryGetValue(clipName, out var clip))
        {
            return;
        }

        _currentClip = clip;
        _currentTime = 0f;
        _targetClip = null;
        _transitionElapsed = 0f;
        _transitionDuration = 0f;

        if (immediate)
        {
            Evaluate(0f);
        }
    }

    public void CrossFade(string clipName, float durationSeconds)
    {
        if (!_clips.TryGetValue(clipName, out var clip))
        {
            return;
        }

        if (_currentClip is null)
        {
            _currentClip = clip;
            _currentTime = 0f;
            return;
        }

        if (_currentClip.Name == clip.Name)
        {
            return;
        }

        _targetClip = clip;
        _targetTime = 0f;
        _transitionElapsed = 0f;
        _transitionDuration = Math.Max(0.01f, durationSeconds);
    }

    public void SetSpeed(float speed)
    {
        _playbackSpeed = speed;
    }

    public void Update(float deltaSeconds)
    {
        Evaluate(deltaSeconds);
    }

    private void Evaluate(float deltaSeconds)
    {
        if (_currentClip is null)
        {
            _finalPose.SetToBindPose(_skeleton);
            BuildSkinTransforms();
            return;
        }

        _currentTime = AdvanceTime(_currentClip, _currentTime, deltaSeconds * _playbackSpeed);
        SampleClip(_currentClip, _currentTime, _basePose);

        if (_targetClip is not null)
        {
            _targetTime = AdvanceTime(_targetClip, _targetTime, deltaSeconds * _playbackSpeed);
            SampleClip(_targetClip, _targetTime, _blendTargetPose);

            _transitionElapsed += deltaSeconds;
            var alpha = MathHelper.Clamp(_transitionElapsed / _transitionDuration, 0f, 1f);
            Blend(_basePose, _blendTargetPose, alpha, _finalPose);

            if (alpha >= 1f)
            {
                _currentClip = _targetClip;
                _currentTime = _targetTime;
                _targetClip = null;
            }
        }
        else
        {
            _finalPose.CopyFrom(_basePose);
        }

        BuildSkinTransforms();
    }

    private void BuildSkinTransforms()
    {
        for (var i = 0; i < _skeleton.BoneCount; i++)
        {
            var local = Matrix.CreateScale(_finalPose.Scales[i]) *
                        Matrix.CreateFromQuaternion(_finalPose.Rotations[i]) *
                        Matrix.CreateTranslation(_finalPose.Positions[i]);

            var parent = _skeleton.ParentIndices[i];
            _modelPose[i] = parent < 0 ? local : local * _modelPose[parent];
            SkinTransforms[i] = _skeleton.InverseBindPose[i] * _modelPose[i];
        }
    }

    private static void Blend(PoseBuffer from, PoseBuffer to, float alpha, PoseBuffer output)
    {
        for (var i = 0; i < output.BoneCount; i++)
        {
            output.Positions[i] = Vector3.Lerp(from.Positions[i], to.Positions[i], alpha);
            output.Rotations[i] = Quaternion.Normalize(Quaternion.Slerp(from.Rotations[i], to.Rotations[i], alpha));
            output.Scales[i] = Vector3.Lerp(from.Scales[i], to.Scales[i], alpha);
        }
    }

    private static float AdvanceTime(AnimationClip clip, float time, float delta)
    {
        var duration = Math.Max(0.0001f, clip.DurationSeconds);
        time += delta;

        if (clip.Looping)
        {
            while (time >= duration)
            {
                time -= duration;
            }

            while (time < 0f)
            {
                time += duration;
            }

            return time;
        }

        return MathHelper.Clamp(time, 0f, duration);
    }

    private void SampleClip(AnimationClip clip, float time, PoseBuffer output)
    {
        output.SetToBindPose(_skeleton);

        for (var bone = 0; bone < _skeleton.BoneCount; bone++)
        {
            var track = clip.TracksByBone[bone];
            if (track is null)
            {
                continue;
            }

            output.Positions[bone] = SampleVector3(track.PositionTimes, track.Positions, time, output.Positions[bone]);
            output.Rotations[bone] = SampleQuaternion(track.RotationTimes, track.Rotations, time, output.Rotations[bone]);
            output.Scales[bone] = SampleVector3(track.ScaleTimes, track.Scales, time, output.Scales[bone]);
        }
    }

    private static Vector3 SampleVector3(float[] times, Vector3[] values, float time, Vector3 fallback)
    {
        if (times.Length == 0 || values.Length == 0)
        {
            return fallback;
        }

        if (times.Length == 1)
        {
            return values[0];
        }

        var index = FindKeyIndex(times, time);
        var next = Math.Min(index + 1, values.Length - 1);
        var t0 = times[index];
        var t1 = times[Math.Min(index + 1, times.Length - 1)];

        if (Math.Abs(t1 - t0) < 0.00001f)
        {
            return values[index];
        }

        var alpha = MathHelper.Clamp((time - t0) / (t1 - t0), 0f, 1f);
        return Vector3.Lerp(values[index], values[next], alpha);
    }

    private static Quaternion SampleQuaternion(float[] times, Quaternion[] values, float time, Quaternion fallback)
    {
        if (times.Length == 0 || values.Length == 0)
        {
            return fallback;
        }

        if (times.Length == 1)
        {
            return Quaternion.Normalize(values[0]);
        }

        var index = FindKeyIndex(times, time);
        var next = Math.Min(index + 1, values.Length - 1);
        var t0 = times[index];
        var t1 = times[Math.Min(index + 1, times.Length - 1)];

        if (Math.Abs(t1 - t0) < 0.00001f)
        {
            return Quaternion.Normalize(values[index]);
        }

        var alpha = MathHelper.Clamp((time - t0) / (t1 - t0), 0f, 1f);
        return Quaternion.Normalize(Quaternion.Slerp(values[index], values[next], alpha));
    }

    private static int FindKeyIndex(float[] times, float time)
    {
        if (time <= times[0])
        {
            return 0;
        }

        for (var i = 0; i < times.Length - 1; i++)
        {
            if (time < times[i + 1])
            {
                return i;
            }
        }

        return Math.Max(0, times.Length - 2);
    }
}
