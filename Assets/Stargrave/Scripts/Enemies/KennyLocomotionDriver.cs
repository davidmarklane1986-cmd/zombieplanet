using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Drives Kenny zombie idle/run via the Playables API (works with Generic Mecanim clips).
/// Requires an <see cref="Animator"/> on the same GameObject as the skinned hierarchy root (CharacterModel).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
public sealed class KennyLocomotionDriver : MonoBehaviour
{
    public AnimationClip idleClip;
    public AnimationClip runClip;

    [Tooltip("Planar speed above this uses the run clip.")]
    public float moveThreshold = 0.2f;

    Animator _animator;
    PlayableGraph _graph;
    AnimationMixerPlayable _mixer;
    AnimationClipPlayable _idlePlayable;
    AnimationClipPlayable _runPlayable;
    bool _graphReady;
    bool _moving;

    void Awake()
    {
        _animator = GetComponent<Animator>();
        if (_animator != null)
        {
            _animator.applyRootMotion = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _animator.runtimeAnimatorController = null;
        }
    }

    void OnEnable()
    {
        BuildGraph();
    }

    void OnDisable()
    {
        TeardownGraph();
    }

    void OnDestroy()
    {
        TeardownGraph();
    }

    public void SetLocomotion(float planarSpeed, float nominalMoveSpeed, bool animate)
    {
        if (_animator == null)
            return;

        if (!animate)
        {
            if (_animator.enabled)
                _animator.enabled = false;
            return;
        }

        if (!_animator.enabled)
            _animator.enabled = true;

        if (!_graphReady)
            BuildGraph();
        if (!_graphReady)
            return;

        bool moving = planarSpeed > moveThreshold;
        if (moving != _moving)
        {
            _moving = moving;
            _mixer.SetInputWeight(0, moving ? 0f : 1f);
            _mixer.SetInputWeight(1, moving ? 1f : 0f);
            if (moving)
                _runPlayable.SetTime(0);
            else
                _idlePlayable.SetTime(0);
        }

        float nominal = Mathf.Max(0.5f, nominalMoveSpeed);
        float speed = moving ? Mathf.Clamp(planarSpeed / nominal, 0.5f, 1.75f) : 1f;
        _idlePlayable.SetSpeed(1f);
        _runPlayable.SetSpeed(speed);
    }

    void BuildGraph()
    {
        TeardownGraph();
        if (_animator == null)
            _animator = GetComponent<Animator>();
        if (_animator == null)
            return;

        AnimationClip idle = idleClip;
        AnimationClip run = runClip != null ? runClip : idleClip;
        if (idle == null && run == null)
            return;
        if (idle == null)
            idle = run;

        // AnimationClipPlayable requires non-legacy clips.
        if (idle.legacy)
            idle.legacy = false;
        if (run.legacy)
            run.legacy = false;

        _animator.applyRootMotion = false;
        _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        _animator.runtimeAnimatorController = null;
        _animator.enabled = true;

        _graph = PlayableGraph.Create($"KennyLocomotion_{name}");
        _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        _mixer = AnimationMixerPlayable.Create(_graph, 2);
        _idlePlayable = AnimationClipPlayable.Create(_graph, idle);
        _runPlayable = AnimationClipPlayable.Create(_graph, run);
        _idlePlayable.SetApplyFootIK(false);
        _runPlayable.SetApplyFootIK(false);

        _graph.Connect(_idlePlayable, 0, _mixer, 0);
        _graph.Connect(_runPlayable, 0, _mixer, 1);
        _mixer.SetInputWeight(0, 1f);
        _mixer.SetInputWeight(1, 0f);

        var output = AnimationPlayableOutput.Create(_graph, "Animation", _animator);
        output.SetSourcePlayable(_mixer);
        _graph.Play();
        _graphReady = true;
        _moving = false;
    }

    void TeardownGraph()
    {
        if (_graphReady && _graph.IsValid())
            _graph.Destroy();
        _graphReady = false;
    }
}
