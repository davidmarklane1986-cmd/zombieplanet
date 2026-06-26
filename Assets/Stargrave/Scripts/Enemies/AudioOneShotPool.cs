using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Reuses AudioSource instances for short 3D one-shot SFX.
/// </summary>
public class AudioOneShotPool : MonoBehaviour
{
    [SerializeField] int prewarmVoices = 8;
    [SerializeField] int maxVoices = 48;

    static AudioOneShotPool _instance;
    public static int ActiveVoiceCount { get; private set; }
    public static int TotalVoiceCount { get; private set; }
    public static int PlayRequests { get; private set; }

    sealed class Voice
    {
        public AudioSource Source;
        public float ReleaseTime;
    }

    readonly List<Voice> _voices = new List<Voice>();

    public static void PlayClip(
        AudioClip clip,
        Vector3 position,
        float volume,
        AudioMixerGroup mixerGroup = null,
        float spatialBlend = 1f)
    {
        if (clip == null)
            return;

        EnsureInstance();
        _instance.PlayInternal(clip, position, volume, mixerGroup, spatialBlend);
    }

    static void EnsureInstance()
    {
        if (_instance != null)
            return;

        GameObject go = new GameObject("AudioOneShotPool");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<AudioOneShotPool>();
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
        Prewarm();
    }

    void Prewarm()
    {
        int count = Mathf.Clamp(prewarmVoices, 0, Mathf.Max(0, maxVoices));
        for (int i = 0; i < count; i++)
            _voices.Add(CreateVoice());
    }

    Voice CreateVoice()
    {
        GameObject go = new GameObject("OneShotVoice");
        go.transform.SetParent(transform, false);
        AudioSource src = go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.spatialBlend = 1f;
        return new Voice { Source = src, ReleaseTime = 0f };
    }

    void PlayInternal(
        AudioClip clip,
        Vector3 position,
        float volume,
        AudioMixerGroup mixerGroup,
        float spatialBlend)
    {
        PlayRequests++;
        Voice voice = GetVoice();
        if (voice == null || voice.Source == null)
            return;

        AudioSource src = voice.Source;
        src.transform.position = position;
        src.outputAudioMixerGroup = mixerGroup;
        src.spatialBlend = Mathf.Clamp01(spatialBlend);
        src.volume = Mathf.Clamp01(volume);
        src.clip = clip;
        src.Play();
        voice.ReleaseTime = Time.time + clip.length + 0.05f;
        RefreshStats();
    }

    Voice GetVoice()
    {
        float now = Time.time;
        for (int i = 0; i < _voices.Count; i++)
        {
            Voice v = _voices[i];
            if (v.Source == null)
                continue;
            if (!v.Source.isPlaying || now >= v.ReleaseTime)
                return v;
        }

        if (_voices.Count < Mathf.Max(1, maxVoices))
        {
            Voice created = CreateVoice();
            _voices.Add(created);
            TotalVoiceCount = _voices.Count;
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

    void LateUpdate() => RefreshStats();

    void RefreshStats()
    {
        TotalVoiceCount = _voices.Count;
        int active = 0;
        float now = Time.time;
        for (int i = 0; i < _voices.Count; i++)
        {
            Voice v = _voices[i];
            if (v?.Source != null && v.Source.isPlaying && now < v.ReleaseTime)
                active++;
        }
        ActiveVoiceCount = active;
    }
}
