using UnityEngine;

/// <summary>Neutral or owned map town that nobles capture via loyalty drain.</summary>
[DisallowMultipleComponent]
public sealed class ClaimableTown : MonoBehaviour
{
    public FactionController Owner { get; private set; }
    public float Loyalty { get; private set; }
    public float MaxLoyalty { get; private set; }
    public Vector3 SurfaceAxis { get; private set; }
    public bool IsNeutral => Owner == null;
    /// <summary>Time.time when ownership last flipped (0 = never).</summary>
    public float LastOwnershipChangedAt { get; private set; }

    FactionNpc _claimingNoble;
    FactionController _claimingFaction;
    int _claimingUnit = -1;
    FactionSimulation _simulation;
    GameObject _visual;
    Renderer _markerRenderer;
    float _trickleAccumulator;
    Color _lastMarkerColor;

    public static ClaimableTown Create(
        FactionSimulation simulation,
        Vector3 surfaceAxis,
        GameObject prefab)
    {
        if (simulation == null || simulation.planet == null)
            return null;

        Planet planet = simulation.planet;
        Vector3 axis = surfaceAxis.sqrMagnitude > 1e-8f ? surfaceAxis.normalized : Vector3.up;
        Vector3 position = planet.GetSurfacePointWorld(axis);

        var root = new GameObject("ClaimableTown");
        root.transform.SetParent(simulation.transform, false);
        root.transform.position = position;
        root.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);

        var town = root.AddComponent<ClaimableTown>();
        town._simulation = simulation;
        town.SurfaceAxis = axis;
        town.MaxLoyalty = Mathf.Max(1f, simulation.economy.townMaxLoyalty);
        town.Loyalty = town.MaxLoyalty;

        if (prefab != null)
        {
            town._visual = Object.Instantiate(prefab, root.transform);
            town._visual.name = "Town_Visual";
            town._visual.transform.localPosition = Vector3.zero;
            town._visual.transform.localRotation = Quaternion.identity;
            BuildingSpawner.ApplyTownScale(town._visual, BuildingSizeClass.Short);
        }
        else
        {
            town._visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            town._visual.name = "Town_FallbackVisual";
            town._visual.transform.SetParent(root.transform, false);
            town._visual.transform.localPosition = new Vector3(0f, 4f, 0f);
            town._visual.transform.localScale = new Vector3(6f, 8f, 6f);
            Collider col = town._visual.GetComponent<Collider>();
            if (col != null)
                Object.Destroy(col);
        }

        var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = "LoyaltyMarker";
        marker.transform.SetParent(root.transform, false);
        marker.transform.localPosition = new Vector3(0f, 12f, 0f);
        marker.transform.localScale = Vector3.one * 2.2f;
        Collider markerCol = marker.GetComponent<Collider>();
        if (markerCol != null)
            Object.Destroy(markerCol);
        town._markerRenderer = marker.GetComponent<Renderer>();
        town.RefreshMarkerColor();

        FactionRegistry.RegisterTown(town);
        return town;
    }

    public bool IsOnReclaimCooldown
    {
        get
        {
            if (LastOwnershipChangedAt <= 0f || _simulation == null)
                return false;
            float cd = Mathf.Max(10f, _simulation.economy.townReclaimCooldownSeconds);
            return Time.time < LastOwnershipChangedAt + cd;
        }
    }

    /// <summary>Nobles may start claiming neutrals or cooled-down rival towns.</summary>
    public bool CanStartClaim(FactionController faction)
    {
        if (faction == null || faction == Owner)
            return false;
        if (IsOnReclaimCooldown)
            return false;
        return true;
    }

    public void Tick(float deltaTime)
    {
        if (deltaTime <= 0f || _simulation == null)
            return;

        float drain = Mathf.Max(0.1f, _simulation.economy.townLoyaltyDrainPerSecond);
        float recover = Mathf.Max(0.01f, _simulation.economy.townLoyaltyRecoverPerSecond);
        float radius = Mathf.Max(1f, _simulation.economy.townClaimRadius);
        float radiusSq = radius * radius;

        bool claiming = false;
        FactionController claimFaction = null;
        if (_claimingNoble != null &&
            !_claimingNoble.IsDead &&
            _claimingNoble.Role == FactionNpcRole.Noble &&
            (_claimingNoble.transform.position - transform.position).sqrMagnitude <= radiusSq)
        {
            claiming = true;
            claimFaction = _claimingNoble.Faction;
        }
        else
        {
            _claimingNoble = null;
        }

        if (!claiming &&
            _claimingFaction != null &&
            _claimingUnit >= 0 &&
            Stargrave.Rts2.Rts2UnitSim.HasInstance &&
            Stargrave.Rts2.Rts2UnitSim.Instance.TryGetUnit(_claimingUnit, out Stargrave.Rts2.Rts2Unit unit) &&
            unit.alive != 0 &&
            unit.role == Stargrave.Rts2.Rts2Role.Noble &&
            unit.factionId == _claimingFaction.RuntimeIndex)
        {
            Vector3 pos = Stargrave.Rts2.Rts2UnitSim.Instance.GetWorldPosition(_claimingUnit);
            if ((pos - transform.position).sqrMagnitude <= radiusSq)
            {
                claiming = true;
                claimFaction = _claimingFaction;
            }
            else
            {
                _claimingFaction = null;
                _claimingUnit = -1;
            }
        }
        else if (_claimingFaction != null && _claimingNoble == null)
        {
            _claimingFaction = null;
            _claimingUnit = -1;
        }

        if (claiming && claimFaction != null && claimFaction != Owner && claimFaction.CanOwnMoreTowns())
        {
            // Allow drain only if reclaim is allowed (or already mid-claim before cooldown).
            if (CanStartClaim(claimFaction) || _claimingNoble != null || _claimingUnit >= 0)
            {
                Loyalty = Mathf.Max(0f, Loyalty - drain * deltaTime);
                if (Loyalty <= 0f)
                    FlipOwnership(claimFaction);
            }
        }
        else
        {
            Loyalty = Mathf.Min(MaxLoyalty, Loyalty + recover * deltaTime);
        }

        if (Owner != null)
        {
            _trickleAccumulator += deltaTime;
            while (_trickleAccumulator >= 1f)
            {
                _trickleAccumulator -= 1f;
                Owner.AddResource(FactionResourceType.Wood, _simulation.economy.townTrickleWood);
                Owner.AddResource(FactionResourceType.Stone, _simulation.economy.townTrickleStone);
                Owner.AddGold(_simulation.economy.townTrickleGold);
            }
        }

        RefreshMarkerColor();
    }

    public bool TryBeginClaimFromSim(FactionController faction, int unitIndex)
    {
        if (faction == null || unitIndex < 0 || !Stargrave.Rts2.Rts2UnitSim.HasInstance)
            return false;
        if (!CanStartClaim(faction))
            return false;
        if (!faction.CanOwnMoreTowns())
            return false;
        if (!Stargrave.Rts2.Rts2UnitSim.Instance.TryGetUnit(unitIndex, out Stargrave.Rts2.Rts2Unit unit) ||
            unit.alive == 0 ||
            unit.role != Stargrave.Rts2.Rts2Role.Noble ||
            unit.factionId != faction.RuntimeIndex)
            return false;
        float radius = Mathf.Max(1f, (_simulation != null ? _simulation.economy.townClaimRadius : 10f));
        Vector3 pos = Stargrave.Rts2.Rts2UnitSim.Instance.GetWorldPosition(unitIndex);
        if ((pos - transform.position).sqrMagnitude > radius * radius)
            return false;
        _claimingFaction = faction;
        _claimingUnit = unitIndex;
        _claimingNoble = null;
        return true;
    }

    public bool TryBeginClaim(FactionNpc noble)
    {
        if (noble == null || noble.IsDead || noble.Role != FactionNpcRole.Noble)
            return false;
        if (noble.Faction == null || !CanStartClaim(noble.Faction))
            return false;
        if (!noble.Faction.CanOwnMoreTowns())
            return false;
        float radius = Mathf.Max(1f, (_simulation != null ? _simulation.economy.townClaimRadius : 10f));
        if ((noble.transform.position - transform.position).sqrMagnitude > radius * radius)
            return false;
        _claimingNoble = noble;
        return true;
    }

    public void StopClaim(FactionNpc noble)
    {
        if (_claimingNoble == noble)
            _claimingNoble = null;
    }

    public bool IsClaimedBy(FactionNpc noble)
    {
        return _claimingNoble == noble;
    }

    void FlipOwnership(FactionController newOwner)
    {
        FactionController previous = Owner;
        Owner = newOwner;
        Loyalty = MaxLoyalty;
        LastOwnershipChangedAt = Time.time;
        _claimingNoble = null;
        _claimingFaction = null;
        _claimingUnit = -1;
        if (_simulation != null && _simulation.verboseEvents)
        {
            string from = previous != null ? previous.DisplayName : "Neutral";
            string to = newOwner != null ? newOwner.DisplayName : "Neutral";
            Debug.Log($"[FactionSimulation] Town ownership {from} -> {to}.", this);
        }
        RefreshMarkerColor();
        if (previous != null && previous != newOwner)
            previous.NotifyTownLost(this);
        if (newOwner != null)
            newOwner.NotifyTownClaimed(this);
    }

    void RefreshMarkerColor()
    {
        if (_markerRenderer == null)
            return;
        Color color = Owner != null ? Owner.UiColor : new Color(0.75f, 0.75f, 0.7f, 1f);
        float loyalty01 = MaxLoyalty > 0f ? Loyalty / MaxLoyalty : 1f;
        color = Color.Lerp(Color.red, color, Mathf.Clamp01(loyalty01));
        if (Mathf.Abs(color.r - _lastMarkerColor.r) < 0.01f &&
            Mathf.Abs(color.g - _lastMarkerColor.g) < 0.01f &&
            Mathf.Abs(color.b - _lastMarkerColor.b) < 0.01f &&
            Mathf.Abs(color.a - _lastMarkerColor.a) < 0.01f)
            return;
        _lastMarkerColor = color;
        if (_markerRenderer.material != null)
            _markerRenderer.material.color = color;
    }

    void OnDestroy()
    {
        FactionRegistry.UnregisterTown(this);
    }
}
