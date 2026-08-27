using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stargrave-style floating pickup: trigger touch applies a timed buff, heal, or shield on the player.
/// Use a trigger collider (e.g. sphere), layer that collides with the player capsule, and optional <see cref="AudioClip"/>.
/// </summary>
public class PowerUpPickup : MonoBehaviour
{
    public enum Kind
    {
        SpeedBoost,
        JumpBoost,
        DamageBoost,
        FireRateBoost,
        HealthPack,
        Shield
    }

    [Header("Effect")]
    public Kind kind = Kind.SpeedBoost;
    [Tooltip("Timed buff duration (seconds). For HealthPack ignored. For Shield = invulnerability seconds.")]
    public float durationSeconds = 15f;
    [Tooltip("Multiplier for speed / jump / damage / fire-rate buffs.")]
    public float multiplier = 1.35f;
    [Tooltip("Flat heal for HealthPack.")]
    public int healAmount = 35;
    [Tooltip("If greater than 0, HealthPack heals this fraction of max health instead of the flat amount.")]
    [Range(0f, 1f)] public float healFractionOfMaxHealth = 0f;

    [Header("Pickup behaviour")]
    public bool destroyOnPickup = true;
    [Min(0f)]
    public float respawnDelaySeconds = 40f;
    [Tooltip("Only one pickup per player per cooldown window (prevents rapid re-trigger).")]
    public float pickupCooldownSeconds = 0.35f;

    [Header("Cosmetic")]
    public float spinSpeedDegrees = 72f;
    [Tooltip("When true, recolours the pickup's materials by kind (original tinted-sphere behaviour). " +
             "Model-based prefabs (e.g. Kenney food models) set this false so their own textures are kept.")]
    public bool tintByKind = true;
    public AudioClip pickupClip;
    [Range(0f, 1f)] public float pickupVolume = 0.85f;

    [Header("Size")]
    [Tooltip("Scales the whole pickup at Awake (2 = double size). The trigger collider scales with the " +
             "transform automatically, so pickup range grows to match.")]
    [Min(0.01f)] public float sizeMultiplier = 2f;

    [Header("Glow (around the model)")]
    [Tooltip("Adds an additive halo sprite (and optional point light) AROUND the pickup. The model keeps " +
             "its full original colours — there is NO emission tint on the model material.")]
    public bool enableGlow = true;
    [Tooltip("Halo diameter as a multiple of the model's size (~1.9 = halo a bit larger than the model).")]
    [Min(0f)] public float glowSize = 1.9f;
    [Tooltip("Base halo brightness (additive).")]
    [Min(0f)] public float glowIntensity = 1.1f;
    [Tooltip("How much the glow brightness/scale swings each pulse (0..1).")]
    [Range(0f, 1f)] public float glowPulseAmount = 0.3f;
    [Tooltip("Pulse speed (radians/second) of the sine driving the glow.")]
    [Min(0f)] public float glowPulseSpeed = 2.6f;
    [Tooltip("Optional glow colour override. When its alpha is 0 the per-kind colour is used instead.")]
    public Color glowColorOverride = new Color(0f, 0f, 0f, 0f);

    [Header("Glow Light (optional)")]
    [Tooltip("Adds a small per-kind point light at the pickup that lights nearby ground.")]
    public bool enableGlowLight = true;
    [Tooltip("Base point-light intensity.")]
    [Min(0f)] public float glowLightIntensity = 2.2f;
    [Tooltip("Point-light range as a multiple of the model size.")]
    [Min(0f)] public float glowLightRangeMultiplier = 3.5f;

    [Header("Collect Animation")]
    [Tooltip("Seconds for the 'sucked into the player' animation when picked up. Effect applies at the end.")]
    [Min(0.01f)] public float collectDuration = 0.35f;
    [Tooltip("Spin speed (deg/sec) reached at the end of the suck-in (accelerates from spinSpeedDegrees).")]
    public float collectSpinSpeed = 720f;
    [Tooltip("Height above the player's feet (along the player's up axis) the pickup homes toward.")]
    public float collectTargetHeight = 1.0f;

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");
    static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");

    static Texture2D _sharedHaloTexture;

    Collider _collider;
    Renderer[] _renderers;
    Color _glowColor = Color.white;
    Vector3 _initialScale = Vector3.one;
    float _nextPickupAllowedTime;
    bool _respawning;
    bool _collecting;

    // Around-the-object glow (created at runtime; no art assets / prefab edits needed).
    Transform _haloTransform;
    MeshRenderer _haloRenderer;
    Material _haloMaterial;
    Light _glowLight;
    float _haloBaseLocalScale = 1f;
    float _glowFade = 1f;
    Camera _glowCamera;

    void Awake()
    {
        // Apply the size multiplier first, then capture it as the base scale the suck-in returns to.
        transform.localScale *= Mathf.Max(0.01f, sizeMultiplier);
        _initialScale = transform.localScale;

        _collider = GetComponent<Collider>();
        if (_collider != null && !_collider.isTrigger)
            _collider.isTrigger = true;

        GatherModelRenderers();
        if (tintByKind)
            ApplyKindTint();
        SetupGlow();
    }

    public void RefreshVisuals()
    {
        GatherModelRenderers();
        if (tintByKind)
            ApplyKindTint();
        SetupGlow();
    }

    // Gathers the MODEL renderers only (excludes the runtime halo quad) so tinting, bounds and
    // visibility toggles never touch the glow.
    void GatherModelRenderers()
    {
        var all = GetComponentsInChildren<Renderer>(true);
        if (_haloRenderer == null)
        {
            _renderers = all;
            return;
        }

        var list = new List<Renderer>(all.Length);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i] != _haloRenderer)
                list.Add(all[i]);
        }
        _renderers = list.ToArray();
    }

    void Update()
    {
        UpdateGlow();

        if (_respawning || _collecting)
            return;
        transform.Rotate(Vector3.up, spinSpeedDegrees * Time.deltaTime, Space.Self);
    }

    void LateUpdate()
    {
        // Billboard the halo to face the camera (it's an additive double-sided quad).
        if (!enableGlow || _haloTransform == null)
            return;
        if (_glowCamera == null)
        {
            _glowCamera = Camera.main;
            if (_glowCamera == null)
                return;
        }
        _haloTransform.rotation = _glowCamera.transform.rotation;
    }

    void OnTriggerEnter(Collider other)
    {
        // _collecting guards against re-triggering / restarting the animation or double-granting.
        if (_respawning || _collecting || Time.time < _nextPickupAllowedTime)
            return;

        var health = other.GetComponentInParent<PlayerHealth>();
        if (health == null)
            return;

        BeginCollect(health);
    }

    void BeginCollect(PlayerHealth health)
    {
        // Latch immediately so a second trigger frame can't re-grant or restart the suck-in.
        _collecting = true;
        _nextPickupAllowedTime = Time.time + pickupCooldownSeconds;

        // Disable the trigger the instant collection starts so it can't fire again.
        if (_collider != null)
            _collider.enabled = false;

        StartCoroutine(CoCollect(health));
    }

    // Flies the pickup toward the player (homing each frame) with an ease-in that accelerates as it
    // nears, shrinking to nothing and spinning up, THEN grants the effect exactly once at the end.
    IEnumerator CoCollect(PlayerHealth health)
    {
        Transform player = health != null ? health.transform : null;
        Vector3 startPos = transform.position;
        Vector3 startScale = transform.localScale;
        float dur = Mathf.Max(0.01f, collectDuration);
        float t = 0f;

        while (t < 1f)
        {
            t += Time.deltaTime / dur;
            float e = Mathf.Clamp01(t);
            float ease = e * e; // quadratic ease-in -> accelerates as it gets sucked in

            // Fade the glow out as it's sucked in so the halo/light don't pop at the end.
            _glowFade = 1f - ease;

            // Re-target the player's chest every frame so it follows them as they move.
            Vector3 target = player != null
                ? player.position + player.up * collectTargetHeight
                : startPos;

            transform.position = Vector3.Lerp(startPos, target, ease);
            transform.localScale = Vector3.Lerp(startScale, Vector3.zero, ease);

            float spin = Mathf.Lerp(spinSpeedDegrees, collectSpinSpeed, e);
            transform.Rotate(Vector3.up, spin * Time.deltaTime, Space.Self);

            // Player went away mid-flight: stop homing but still resolve the pickup below.
            if (player == null || health == null)
                break;

            yield return null;
        }

        // Apply the effect once, at the end of the animation (same grant logic as before).
        if (health != null)
        {
            ApplyEffect(health.transform, health);

            if (pickupClip != null)
                AudioSource.PlayClipAtPoint(pickupClip, transform.position, pickupVolume);
        }

        if (destroyOnPickup)
        {
            Destroy(gameObject);
        }
        else if (respawnDelaySeconds > 0f)
        {
            StartCoroutine(CoRespawn());
        }
        else
        {
            // No-destroy, no-respawn: just restore and re-arm the pickup in place.
            transform.localScale = _initialScale;
            _glowFade = 1f;
            if (_collider != null)
                _collider.enabled = true;
            _collecting = false;
        }
    }

    void ApplyEffect(Transform playerRoot, PlayerHealth health)
    {
        float d = Mathf.Max(0.05f, durationSeconds);
        float m = Mathf.Max(0.05f, multiplier);

        switch (kind)
        {
            case Kind.SpeedBoost:
                ApplyBuff(playerRoot, "PowerUp_Speed", "Speed Boost", d, m, 1f, 1f, 1f, drainWhileUsed: true);
                break;
            case Kind.JumpBoost:
                ApplyBuff(playerRoot, "PowerUp_Jump", "Jump Boost", d, 1f, m, 1f, 1f);
                break;
            case Kind.DamageBoost:
                ApplyBuff(playerRoot, "PowerUp_Damage", "Damage Boost", d, 1f, 1f, m, 1f);
                break;
            case Kind.FireRateBoost:
                ApplyBuff(playerRoot, "PowerUp_RapidFire", "Rapid Fire", d, 1f, 1f, 1f, m, drainWhileUsed: true);
                break;
            case Kind.HealthPack:
                int resolvedHeal = healAmount;
                if (healFractionOfMaxHealth > 0f)
                    resolvedHeal = Mathf.Max(resolvedHeal, Mathf.CeilToInt(health.maxHealth * healFractionOfMaxHealth));
                health.StoreHealthPack(resolvedHeal);
                break;
            case Kind.Shield:
                health.ExtendInvulnerability(d);
                break;
        }
    }

    static void ApplyBuff(Transform playerRoot, string buffId, string displayName, float duration, float spd, float jmp, float dmg, float rof, bool drainWhileUsed = false)
    {
        var buffs = playerRoot.GetComponent<PlayerBuffController>();
        if (buffs != null)
            buffs.ApplyTimedBuff(buffId, duration, spd, jmp, dmg, rof, displayName, drainWhileUsed);
    }

    IEnumerator CoRespawn()
    {
        _respawning = true;
        SetPhysicsAndVisuals(false);
        yield return new WaitForSeconds(respawnDelaySeconds);
        // The suck-in shrank us to ~0 scale; restore before re-showing.
        transform.localScale = _initialScale;
        _glowFade = 1f;
        SetPhysicsAndVisuals(true);
        _respawning = false;
        _collecting = false;
    }

    void SetPhysicsAndVisuals(bool on)
    {
        if (_collider != null)
            _collider.enabled = on;
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] != null)
                _renderers[i].enabled = on;
        }
        if (_haloRenderer != null)
            _haloRenderer.enabled = on;
        if (_glowLight != null)
            _glowLight.enabled = on;
    }

    void ApplyKindTint()
    {
        if (_renderers == null || _renderers.Length == 0)
            return;

        Color c = kind switch
        {
            Kind.SpeedBoost => new Color(0.35f, 0.85f, 0.45f, 1f),
            Kind.JumpBoost => new Color(0.45f, 0.65f, 1f, 1f),
            Kind.DamageBoost => new Color(1f, 0.45f, 0.25f, 1f),
            Kind.FireRateBoost => new Color(1f, 0.85f, 0.25f, 1f),
            Kind.HealthPack => new Color(0.95f, 0.35f, 0.45f, 1f),
            Kind.Shield => new Color(0.55f, 0.85f, 1f, 1f),
            _ => Color.white
        };

        for (int i = 0; i < _renderers.Length; i++)
        {
            var r = _renderers[i];
            if (r == null)
                continue;
            foreach (var mat in r.materials)
            {
                if (mat == null)
                    continue;
                if (mat.HasProperty(BaseColorId))
                    mat.SetColor(BaseColorId, c);
                else if (mat.HasProperty(ColorId))
                    mat.SetColor(ColorId, c);
            }
        }
    }

    // Builds the AROUND-the-object glow at runtime: an additive billboard halo quad sized relative to
    // the model, plus an optional per-kind point light. The model material is never touched, so the
    // model renders its full original colours. Idempotent so RefreshVisuals can re-tune colour/size.
    void SetupGlow()
    {
        _glowColor = ResolveGlowColor();

        if (!enableGlow)
        {
            DestroyGlow();
            return;
        }

        float diameter = ComputeModelWorldDiameter();
        float parentScale = Mathf.Max(transform.lossyScale.x, Mathf.Max(transform.lossyScale.y, transform.lossyScale.z));
        parentScale = Mathf.Max(1e-4f, parentScale);

        EnsureHalo();
        // Convert the desired WORLD halo size into local scale (so the halo follows the parent's
        // scale, including the 2x size and the suck-in shrink, automatically).
        _haloBaseLocalScale = (diameter * glowSize) / parentScale;
        if (_haloTransform != null)
            _haloTransform.localScale = Vector3.one * _haloBaseLocalScale;

        if (enableGlowLight)
        {
            EnsureLight();
            if (_glowLight != null)
            {
                _glowLight.color = _glowColor;
                _glowLight.range = Mathf.Max(0.1f, diameter * glowLightRangeMultiplier);
            }
        }
        else if (_glowLight != null)
        {
            Destroy(_glowLight.gameObject);
            _glowLight = null;
        }
    }

    void UpdateGlow()
    {
        if (!enableGlow)
            return;

        float s = Mathf.Sin(Time.time * glowPulseSpeed);
        float intensity = glowIntensity * (1f + glowPulseAmount * s);
        if (intensity < 0f)
            intensity = 0f;

        if (_haloMaterial != null)
        {
            Color c = _glowColor * (intensity * _glowFade);
            c.a = 1f;
            if (_haloMaterial.HasProperty(BaseColorId))
                _haloMaterial.SetColor(BaseColorId, c);
            else if (_haloMaterial.HasProperty(ColorId))
                _haloMaterial.SetColor(ColorId, c);

            if (_haloTransform != null)
            {
                float scale = _haloBaseLocalScale * (1f + glowPulseAmount * 0.4f * s);
                _haloTransform.localScale = Vector3.one * scale;
            }
        }

        if (_glowLight != null)
            _glowLight.intensity = glowLightIntensity * Mathf.Max(0f, 1f + glowPulseAmount * s) * _glowFade;
    }

    float ComputeModelWorldDiameter()
    {
        if (_renderers == null || _renderers.Length == 0)
            return Mathf.Max(transform.lossyScale.x, Mathf.Max(transform.lossyScale.y, transform.lossyScale.z));

        Bounds b = default;
        bool found = false;
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] == null)
                continue;
            if (!found)
            {
                b = _renderers[i].bounds;
                found = true;
            }
            else
            {
                b.Encapsulate(_renderers[i].bounds);
            }
        }

        if (!found)
            return Mathf.Max(transform.lossyScale.x, Mathf.Max(transform.lossyScale.y, transform.lossyScale.z));
        return Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
    }

    void EnsureHalo()
    {
        if (_haloTransform != null)
            return;

        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "PowerUpGlowHalo";
        var col = go.GetComponent<Collider>();
        if (col != null)
            Destroy(col);

        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;

        _haloRenderer = go.GetComponent<MeshRenderer>();
        _haloRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _haloRenderer.receiveShadows = false;
        _haloRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        _haloRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

        _haloMaterial = CreateHaloMaterial();
        _haloRenderer.sharedMaterial = _haloMaterial;
        _haloTransform = go.transform;
    }

    Material CreateHaloMaterial()
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null)
            sh = Shader.Find("Unlit/Transparent");
        if (sh == null)
            sh = Shader.Find("Sprites/Default");

        var m = new Material(sh);

        if (_sharedHaloTexture == null)
            _sharedHaloTexture = BuildHaloTexture();
        if (m.HasProperty(BaseMapId))
            m.SetTexture(BaseMapId, _sharedHaloTexture);
        if (m.HasProperty("_MainTex"))
            m.SetTexture("_MainTex", _sharedHaloTexture);

        // Additive transparent blend (Src One, Dst One), no depth write, double-sided.
        if (m.HasProperty("_Surface"))
            m.SetFloat("_Surface", 1f);
        if (m.HasProperty("_SrcBlend"))
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
        if (m.HasProperty("_DstBlend"))
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
        if (m.HasProperty("_ZWrite"))
            m.SetFloat("_ZWrite", 0f);
        if (m.HasProperty("_Cull"))
            m.SetFloat("_Cull", 0f);

        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHATEST_ON");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        return m;
    }

    static Texture2D BuildHaloTexture()
    {
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "PowerUpHaloGradient"
        };

        float c = (size - 1) * 0.5f;
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - c) / c;
                float dy = (y - c) / c;
                float d = Mathf.Sqrt(dx * dx + dy * dy); // 0 at centre -> ~1 at edge
                float v = Mathf.Clamp01(1f - d);
                v *= v; // soft radial falloff (white core fading to black -> adds nothing at the rim)
                byte b = (byte)Mathf.RoundToInt(v * 255f);
                px[y * size + x] = new Color32(b, b, b, b);
            }
        }

        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }

    void EnsureLight()
    {
        if (_glowLight != null)
            return;

        var go = new GameObject("PowerUpGlowLight");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;

        _glowLight = go.AddComponent<Light>();
        _glowLight.type = LightType.Point;
        _glowLight.shadows = LightShadows.None;
        _glowLight.renderMode = LightRenderMode.Auto;
    }

    void DestroyGlow()
    {
        if (_haloTransform != null)
        {
            Destroy(_haloTransform.gameObject);
            _haloTransform = null;
            _haloRenderer = null;
            _haloMaterial = null;
        }
        if (_glowLight != null)
        {
            Destroy(_glowLight.gameObject);
            _glowLight = null;
        }
    }

    Color ResolveGlowColor()
    {
        if (glowColorOverride.a > 0f)
            return new Color(glowColorOverride.r, glowColorOverride.g, glowColorOverride.b, 1f);

        // Matches the per-kind tint palette so the glow reads as the same "kind" colour.
        return kind switch
        {
            Kind.SpeedBoost => new Color(0.35f, 0.95f, 0.5f, 1f),
            Kind.JumpBoost => new Color(0.45f, 0.7f, 1f, 1f),
            Kind.DamageBoost => new Color(1f, 0.5f, 0.25f, 1f),
            Kind.FireRateBoost => new Color(1f, 0.6f, 0.15f, 1f),
            Kind.HealthPack => new Color(0.4f, 1f, 0.45f, 1f),
            Kind.Shield => new Color(0.5f, 0.85f, 1f, 1f),
            _ => Color.white
        };
    }
}
