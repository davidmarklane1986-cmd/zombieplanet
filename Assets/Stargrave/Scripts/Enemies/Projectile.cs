using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Projectile: damages <see cref="ZombieAI"/>, optional trail, impact sparks, despawn on terrain / world colliders.
/// Expects a trigger (or solid) collider and optional <see cref="Rigidbody"/>.
/// </summary>
[RequireComponent(typeof(Collider))]
public class Projectile : MonoBehaviour
{
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    public int damage = 1;
    public float lifetime = 4f;
    public bool useTrigger = true;

    [Header("Visual — mesh tint")]
    [Tooltip("If a MeshRenderer is present, creates a material instance with a bright tint.")]
    public bool applyTintToRenderer = true;
    public Color tintColor = new Color(1f, 0.55f, 0.08f, 1f);
    [Tooltip("URP Lit: scales _EmissionColor when the property exists. Set 0 to skip emission.")]
    public float emissionBoost = 2.5f;

    [Header("Visual — trail")]
    public bool enableTrail = true;
    public float trailTime = 0.22f;
    public float trailStartWidth = 0.14f;
    public float trailEndWidth = 0.02f;

    [Header("Visual — impact")]
    public bool playImpactOnHit = true;
    [Range(4, 64)]
    public int impactParticleCount = 22;
    public float impactParticleLifetime = 0.32f;
    [Tooltip("Spark speed (world units / sec) range.")]
    public Vector2 impactSpeedRange = new Vector2(1.5f, 4.5f);
    public float impactSpawnRadius = 0.06f;

    [Header("World hit")]
    [Tooltip("Destroy the projectile when it hits something that is not ignored and is not a zombie (terrain, props, etc.).")]
    public bool despawnOnWorldHit = true;

    float _spawnTime;

    void Awake()
    {
        ApplyTintToMesh();
    }

    void Start()
    {
        _spawnTime = Time.time;
        ConfigureTrail();
    }

    void ApplyTintToMesh()
    {
        if (!applyTintToRenderer)
            return;
        var mr = GetComponentInChildren<MeshRenderer>();
        if (mr == null)
            return;
        Material m = mr.material;
        if (m.HasProperty(BaseColorId))
            m.SetColor(BaseColorId, tintColor);
        else if (m.HasProperty(ColorId))
            m.SetColor(ColorId, tintColor);
        if (emissionBoost > 0f && m.HasProperty(EmissionColorId))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor(EmissionColorId, tintColor * emissionBoost);
        }
    }

    void ConfigureTrail()
    {
        if (!enableTrail)
            return;
        var trail = GetComponent<TrailRenderer>();
        if (trail == null)
            trail = gameObject.AddComponent<TrailRenderer>();
        trail.time = trailTime;
        trail.startWidth = trailStartWidth;
        trail.endWidth = trailEndWidth;
        trail.minVertexDistance = 0.015f;
        trail.numCornerVertices = 3;
        trail.numCapVertices = 2;
        trail.shadowCastingMode = ShadowCastingMode.Off;
        trail.receiveShadows = false;
        trail.autodestruct = false;
        trail.emitting = true;

        Shader sh = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                    ?? Shader.Find("Particles/Standard Unlit")
                    ?? Shader.Find("Sprites/Default");
        if (sh != null)
        {
            var mat = new Material(sh);
            if (mat.HasProperty(BaseColorId))
                mat.SetColor(BaseColorId, tintColor);
            else
                mat.color = tintColor;
            trail.material = mat;
        }

        trail.startColor = new Color(1f, 1f, 1f, 0.9f);
        trail.endColor = new Color(tintColor.r, tintColor.g, tintColor.b, 0f);
    }

    void Update()
    {
        if (Time.time - _spawnTime > lifetime)
            Destroy(gameObject);
    }

    void OnTriggerEnter(Collider other)
    {
        if (!useTrigger)
            return;
        ProcessHit(other);
    }

    void OnCollisionEnter(Collision collision)
    {
        if (useTrigger)
            return;
        if (collision.collider == null)
            return;
        ProcessHit(collision.collider);
    }

    bool ShouldIgnoreCollider(Collider other)
    {
        if (other == null)
            return true;
        if (other.isTrigger && other.GetComponentInParent<ZombieAI>() == null)
            return true;
        if (other.CompareTag("Player"))
            return true;
        if (other.GetComponentInParent<Projectile>() != null)
            return true;
        if (other.gameObject.layer == 2)
            return true;
        return false;
    }

    void ProcessHit(Collider other)
    {
        if (ShouldIgnoreCollider(other))
            return;

        Vector3 hitPoint = other.ClosestPoint(transform.position);
        Vector3 hitNormal = HitNormalFromImpact(transform.position, hitPoint);

        if (TryDamageZombie(other, hitPoint, hitNormal))
            return;

        if (despawnOnWorldHit)
        {
            if (playImpactOnHit)
                PlayImpact(hitPoint, hitNormal);
            Destroy(gameObject);
        }
    }

    /// <summary>Outward-ish direction from the contact surface toward the projectile (good for impact burst orientation).</summary>
    static Vector3 HitNormalFromImpact(Vector3 projectilePosition, Vector3 surfaceClosestPoint)
    {
        Vector3 n = projectilePosition - surfaceClosestPoint;
        if (n.sqrMagnitude < 1e-8f)
            return Vector3.up;
        return n.normalized;
    }

    bool TryDamageZombie(Collider other, Vector3 hitPoint, Vector3 hitNormal)
    {
        var zombie = other.GetComponentInParent<ZombieAI>();
        if (zombie == null)
            return false;
        zombie.TakeDamage(damage);
        PlayerShooting.NotifyHitConfirmed();
        if (playImpactOnHit)
            PlayImpact(hitPoint, hitNormal);
        Destroy(gameObject);
        return true;
    }

    void PlayImpact(Vector3 position, Vector3 outwardNormal)
    {
        int count = Mathf.Clamp(impactParticleCount, 4, 64);
        float life = Mathf.Max(0.05f, impactParticleLifetime);
        float destroyAfter = life + 0.4f;

        var go = new GameObject("ProjectileImpactFx");
        go.transform.position = position + outwardNormal * 0.02f;
        if (outwardNormal.sqrMagnitude > 1e-6f)
            go.transform.rotation = Quaternion.LookRotation(outwardNormal);

        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = 0.08f;
        main.startLifetime = life;
        main.startSpeed = new ParticleSystem.MinMaxCurve(impactSpeedRange.x, impactSpeedRange.y);
        main.startSize = new ParticleSystem.MinMaxCurve(0.018f, 0.055f);
        main.startColor = new ParticleSystem.MinMaxGradient(tintColor, tintColor * 0.65f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 64;
        main.gravityModifier = 0.35f;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Hemisphere;
        shape.radius = Mathf.Max(0.01f, impactSpawnRadius);
        shape.randomDirectionAmount = 0.15f;

        var pr = go.GetComponent<ParticleSystemRenderer>();
        pr.renderMode = ParticleSystemRenderMode.Billboard;
        Shader sh = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                    ?? Shader.Find("Particles/Standard Unlit")
                    ?? Shader.Find("Sprites/Default");
        if (sh != null)
            pr.material = new Material(sh);

        ps.Play();
        Destroy(go, destroyAfter);
    }
}
