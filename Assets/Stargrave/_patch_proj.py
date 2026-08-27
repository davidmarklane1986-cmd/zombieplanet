from pathlib import Path
p = Path(r"d:\Unity\Projects\StargraveOpensourceBase\Assets\Stargrave\Scripts\Enemies\Projectile.cs")
text = p.read_text(encoding="utf-8")
old = "    float _spawnTime;\n\n    void Awake()\n    {\n        ApplyTintToMesh();\n    }\n\n    void Start()\n    {\n        _spawnTime = Time.time;\n        ConfigureTrail();\n    }\n"
new = """    [Header(\"Falloff\")]\n    public bool useDamageFalloff;\n    public float damageFalloffStart = 18f;\n    public float damageFalloffEnd = 50f;\n    [Range(0.05f, 1f)] public float damageFalloffMinMultiplier = 0.4f;\n\n    float _spawnTime;\n    Vector3 _spawnPosition;\n    int _baseDamage;\n\n    void Awake()\n    {\n        _spawnPosition = transform.position;\n        _baseDamage = damage;\n        ApplyTintToMesh();\n    }\n\n    void Start()\n    {\n        _spawnTime = Time.time;\n        _spawnPosition = transform.position;\n        ConfigureTrail();\n    }\n\n    public void ConfigureFromWeapon(Color tint, int baseDamage, bool falloff,\n        float falloffStart, float falloffEnd, float falloffMinMult)\n    {\n        _baseDamage = Mathf.Max(0, baseDamage);\n        damage = _baseDamage;\n        useDamageFalloff = falloff && _baseDamage > 0;\n        damageFalloffStart = falloffStart;\n        damageFalloffEnd = Mathf.Max(falloffStart + 0.01f, falloffEnd);\n        damageFalloffMinMultiplier = Mathf.Clamp(falloffMinMult, 0.05f, 1f);\n        SetTint(tint);\n    }\n\n    int GetDamageAtPoint(Vector3 hitPoint)\n    {\n        if (_baseDamage <= 0)\n            return 0;\n        if (!useDamageFalloff)\n            return _baseDamage;\n\n        float dist = Vector3.Distance(_spawnPosition, hitPoint);\n        if (dist <= damageFalloffStart)\n            return _baseDamage;\n\n        float t = Mathf.InverseLerp(damageFalloffStart, damageFalloffEnd, dist);\n        float mult = Mathf.Lerp(1f, damageFalloffMinMultiplier, t);\n        return Mathf.Max(1, Mathf.RoundToInt(_baseDamage * mult));\n    }\n"""
if old not in text:
    raise SystemExit('block1 missing')
text = text.replace(old, new, 1)
old2 = "        if (damage > 0)\n        {\n            zombie.TakeDamage(damage);\n            PlayerShooting.NotifyHitConfirmed();\n        }\n"
new2 = "        int dmg = GetDamageAtPoint(hitPoint);\n        if (dmg > 0)\n        {\n            zombie.TakeDamage(dmg);\n            PlayerShooting.NotifyHitConfirmed();\n        }\n"
if old2 not in text:
    raise SystemExit('block2 missing')
text = text.replace(old2, new2, 1)
p.write_text(text, encoding='utf-8')
print('Projectile patched')
