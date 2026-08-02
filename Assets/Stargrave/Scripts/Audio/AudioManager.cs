using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Central SFX façade. Auto-bootstraps (no scene wiring) and pools <see cref="AudioSource"/>s for
/// 2D one-shots (UI, shoot, footsteps) so we never allocate via <c>PlayClipAtPoint</c>. 3D one-shots
/// (zombie hit/death etc.) are delegated to the existing <see cref="AudioOneShotPool"/>.
///
/// Pause handling: the frontend pauses the whole game via <c>AudioListener.pause = true</c>. Gameplay
/// SFX (shoot, footsteps) honour that and go silent while paused. UI sounds are played on sources with
/// <c>ignoreListenerPause = true</c> so the pause-menu clicks/rollovers remain audible while paused.
/// </summary>
[DisallowMultipleComponent]
public sealed class AudioManager : MonoBehaviour
{
    [Range(0f, 1f)] public float masterVolume = 1f;
    [Range(0f, 1f)] public float sfxVolume = 0.9f;
    [Range(0f, 1f)] public float uiVolume = 0.7f;

    [Header("Ambient")]
    [Tooltip("Low looping wind ambience (HorrorSFX 'Ambient Wind' clips), rotated for variety.")]
    public bool ambientEnabled = true;
    [Range(0f, 1f)] public float ambientVolume = 0.2f;

    const int PrewarmVoices = 6;
    const int MaxVoices = 24;

    static AudioManager _instance;

    AudioSource _ambient;
    AudioClip[] _ambientClips;

    sealed class Voice
    {
        public AudioSource Source;
        public float ReleaseTime;
    }

    readonly List<Voice> _voices = new List<Voice>();
    AudioSource _footstepVoice;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        EnsureInstance();
    }

    public static AudioManager Instance
    {
        get
        {
            EnsureInstance();
            return _instance;
        }
    }

    static void EnsureInstance()
    {
        if (_instance != null)
            return;

        var go = new GameObject("Stargrave_AudioManager");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<AudioManager>();
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);

        for (int i = 0; i < PrewarmVoices; i++)
            _voices.Add(CreateVoice());
    }

    void Update()
    {
        UpdateAmbient();
    }

    // Low looping wind ambience: a 2D source that obeys AudioListener.pause (so it stops during menus/pause).
    // Rotates to a fresh random wind clip whenever the current one finishes, for variety without crossfade.
    void UpdateAmbient()
    {
        if (!ambientEnabled)
        {
            if (_ambient != null && _ambient.isPlaying)
                _ambient.Stop();
            return;
        }

        // Don't churn while the game is paused — the source auto-pauses (ignoreListenerPause = false).
        if (AudioListener.pause)
            return;

        _ambientClips ??= SfxLibrary.AmbientWind;
        if (_ambientClips == null || _ambientClips.Length == 0)
            return;

        if (_ambient == null)
        {
            var go = new GameObject("AmbientVoice");
            go.transform.SetParent(transform, false);
            _ambient = go.AddComponent<AudioSource>();
            _ambient.playOnAwake = false;
            _ambient.loop = false;            // rotate clips instead of looping one
            _ambient.spatialBlend = 0f;       // 2D background
            _ambient.ignoreListenerPause = false; // stop while paused
        }

        _ambient.volume = Mathf.Clamp01(ambientVolume) * masterVolume;
        if (!_ambient.isPlaying)
        {
            _ambient.clip = _ambientClips[Random.Range(0, _ambientClips.Length)];
            _ambient.Play();
        }
    }

    Voice CreateVoice()
    {
        var go = new GameObject("UiSfxVoice");
        go.transform.SetParent(transform, false);
        var src = go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.spatialBlend = 0f; // 2D
        src.rolloffMode = AudioRolloffMode.Linear;
        return new Voice { Source = src, ReleaseTime = 0f };
    }

    Voice GetVoice()
    {
        float now = Time.unscaledTime;
        for (int i = 0; i < _voices.Count; i++)
        {
            Voice v = _voices[i];
            if (v.Source == null)
                continue;
            if (!v.Source.isPlaying || now >= v.ReleaseTime)
                return v;
        }

        if (_voices.Count < MaxVoices)
        {
            Voice created = CreateVoice();
            _voices.Add(created);
            return created;
        }

        Voice oldest = null;
        for (int i = 0; i < _voices.Count; i++)
        {
            Voice v = _voices[i];
            if (v.Source == null)
                continue;
            if (oldest == null || v.ReleaseTime < oldest.ReleaseTime)
                oldest = v;
        }
        return oldest;
    }

    void Play2D(AudioClip clip, float volume, float pitch, bool ignoreListenerPause)
    {
        if (clip == null)
            return;

        Voice voice = GetVoice();
        if (voice == null || voice.Source == null)
            return;

        AudioSource src = voice.Source;
        src.spatialBlend = 0f;
        src.pitch = pitch;
        src.volume = Mathf.Clamp01(volume) * masterVolume;
        src.ignoreListenerPause = ignoreListenerPause;
        src.clip = clip;
        src.Play();
        // length scales with pitch; use unscaled time so the pool recycles correctly while paused.
        float dur = clip.length / Mathf.Max(0.01f, pitch);
        voice.ReleaseTime = Time.unscaledTime + dur + 0.05f;
    }

    // ---- Static API ----------------------------------------------------------------------

    /// <summary>2D UI sound that plays even while the game is paused (pause-menu clicks/rollovers).</summary>
    public static void PlayUi(AudioClip clip, float volume = 1f)
    {
        if (clip == null)
            return;
        EnsureInstance();
        _instance.Play2D(clip, volume * _instance.uiVolume, 1f, ignoreListenerPause: true);
    }

    /// <summary>2D gameplay one-shot (shoot, footstep). Honours <c>AudioListener.pause</c>.</summary>
    public static void PlaySfx2D(AudioClip clip, float volume = 1f, float pitch = 1f)
    {
        if (clip == null)
            return;
        EnsureInstance();
        _instance.Play2D(clip, volume * _instance.sfxVolume, pitch, ignoreListenerPause: false);
    }

    /// <summary>3D positional one-shot (delegates to the shared pooled 3D voices).</summary>
    public static void PlaySfx3D(AudioClip clip, Vector3 worldPos, float volume = 1f)
    {
        if (clip == null)
            return;
        float master = _instance != null ? _instance.masterVolume : 1f;
        float sfx = _instance != null ? _instance.sfxVolume : 0.9f;
        AudioOneShotPool.PlayClip(clip, worldPos, Mathf.Clamp01(volume) * sfx * master);
    }

    // ---- Convenience helpers -------------------------------------------------------------

    public static void PlayShoot(float volume = 0.7f)
    {
        PlaySfx2D(SfxLibrary.RandomShoot(), volume, Random.Range(0.95f, 1.06f));
    }

    public static void PlayFootstep(float volume = 0.5f)
    {
        EnsureInstance();
        _instance.PlayFootstepClip(SfxLibrary.RandomFootstep(), volume);
    }

    /// <summary>Surface-aware footstep: random short clip for the area + slight pitch jitter (2D).</summary>
    public static void PlayFootstep(FootstepSurfaceKind surface, float volume = 0.5f)
    {
        EnsureInstance();
        _instance.PlayFootstepClip(SfxLibrary.RandomFootstep(surface), volume);
    }

    /// <summary>Hard-stop the current footstep voice (call when the player stops moving).</summary>
    public static void StopFootsteps()
    {
        if (_instance == null || _instance._footstepVoice == null)
            return;
        if (_instance._footstepVoice.isPlaying)
            _instance._footstepVoice.Stop();
    }

    void PlayFootstepClip(AudioClip clip, float volume)
    {
        if (clip == null)
            return;

        if (_footstepVoice == null)
        {
            var go = new GameObject("FootstepVoice");
            go.transform.SetParent(transform, false);
            _footstepVoice = go.AddComponent<AudioSource>();
            _footstepVoice.playOnAwake = false;
            _footstepVoice.loop = false;
            _footstepVoice.spatialBlend = 0f;
            _footstepVoice.ignoreListenerPause = false;
        }

        // One dedicated voice: replace the previous step so long/overlapping tails can't linger.
        _footstepVoice.Stop();
        _footstepVoice.clip = clip;
        _footstepVoice.pitch = Random.Range(0.9f, 1.1f);
        _footstepVoice.volume = Mathf.Clamp01(volume) * sfxVolume * masterVolume;
        _footstepVoice.Play();
    }

    /// <summary>3D impact thud at a world position (e.g. where a shot lands on a zombie).</summary>
    public static void PlayHit(Vector3 worldPos, float volume = 0.9f)
    {
        PlaySfx3D(SfxLibrary.RandomHit(), worldPos, volume);
    }

    /// <summary>2D impact cue (e.g. the player taking damage).</summary>
    public static void PlayHit2D(float volume = 0.7f)
    {
        PlaySfx2D(SfxLibrary.RandomHit(), volume, Random.Range(0.92f, 1.05f));
    }

    public static void PlayUiClick(float volume = 1f)
    {
        PlayUi(SfxLibrary.RandomUiClick(), volume);
    }

    public static void PlayUiRollover(float volume = 0.6f)
    {
        PlayUi(SfxLibrary.RandomUiRollover(), volume);
    }
}
