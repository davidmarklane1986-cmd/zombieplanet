using UnityEngine;

/// <summary>Loads <see cref="WeaponDef"/> assets from Resources/Weapons.</summary>
public static class WeaponCatalog
{
    const string ResourcesFolder = "Weapons";

    static WeaponDef[] _cached;

    public static WeaponDef[] GetAll()
    {
        if (_cached != null && _cached.Length > 0)
            return _cached;

        _cached = Resources.LoadAll<WeaponDef>(ResourcesFolder);
        if (_cached == null)
            _cached = System.Array.Empty<WeaponDef>();
        return _cached;
    }

    public static WeaponDef GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        WeaponDef[] all = GetAll();
        for (int i = 0; i < all.Length; i++)
        {
            WeaponDef w = all[i];
            if (w != null && string.Equals(w.id, id, System.StringComparison.OrdinalIgnoreCase))
                return w;
        }

        return null;
    }

    /// <summary>Weighted random loot weapon, or null if catalog empty.</summary>
    public static WeaponDef RollLootDrop()
    {
        WeaponDef[] all = GetAll();
        float total = 0f;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].dropWeight > 0f)
                total += all[i].dropWeight;
        }

        if (total <= 0f)
            return null;

        float roll = Random.Range(0f, total);
        float acc = 0f;
        for (int i = 0; i < all.Length; i++)
        {
            WeaponDef w = all[i];
            if (w == null || w.dropWeight <= 0f)
                continue;
            acc += w.dropWeight;
            if (roll <= acc)
                return w;
        }

        return all.Length > 0 ? all[all.Length - 1] : null;
    }

    public static void InvalidateCache()
    {
        _cached = null;
    }
}
