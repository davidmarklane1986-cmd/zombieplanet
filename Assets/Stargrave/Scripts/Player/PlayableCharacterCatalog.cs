using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Loads playable character defs from <c>Resources/PlayableCharacters</c> and tracks the player's pick.
/// </summary>
public static class PlayableCharacterCatalog
{
    public const string PrefsKey = "Stargrave.SelectedCharacterId";
    public const string ResourcesFolder = "PlayableCharacters";
    public const string DefaultId = "cowboy";

    static PlayableCharacterDef[] _cached;
    static bool _loaded;

    public static PlayableCharacterDef[] All
    {
        get
        {
            EnsureLoaded();
            return _cached;
        }
    }

    public static void EnsureLoaded()
    {
        if (_loaded && _cached != null)
            return;
        _cached = Resources.LoadAll<PlayableCharacterDef>(ResourcesFolder);
        if (_cached == null)
            _cached = System.Array.Empty<PlayableCharacterDef>();
        System.Array.Sort(_cached, (a, b) =>
        {
            if (a == null && b == null) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            // Cowboy first, then alphabetical.
            if (a.id == DefaultId && b.id != DefaultId) return -1;
            if (b.id == DefaultId && a.id != DefaultId) return 1;
            return string.CompareOrdinal(a.displayName, b.displayName);
        });
        _loaded = true;
    }

    public static string GetSelectedId()
    {
        string id = PlayerPrefs.GetString(PrefsKey, DefaultId);
        if (string.IsNullOrWhiteSpace(id))
            id = DefaultId;
        return id;
    }

    public static void SetSelectedId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            id = DefaultId;
        PlayerPrefs.SetString(PrefsKey, id);
        PlayerPrefs.Save();
    }

    public static PlayableCharacterDef GetSelected()
    {
        return FindById(GetSelectedId()) ?? FindById(DefaultId) ?? (All.Length > 0 ? All[0] : null);
    }

    public static PlayableCharacterDef FindById(string id)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(id) || _cached == null)
            return null;
        for (int i = 0; i < _cached.Length; i++)
        {
            PlayableCharacterDef d = _cached[i];
            if (d != null && d.id == id)
                return d;
        }
        return null;
    }

#if UNITY_EDITOR
    public static void InvalidateCache()
    {
        _loaded = false;
        _cached = null;
    }
#endif
}
