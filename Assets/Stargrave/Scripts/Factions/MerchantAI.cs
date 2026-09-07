using UnityEngine;

/// <summary>Walks from the home Market to a partner Market, trades, and returns.</summary>
[DisallowMultipleComponent]
public sealed class MerchantAI : MonoBehaviour
{
    enum MerchantState
    {
        Idle,
        GoingHomeMarket,
        Loading,
        TravelToPartner,
        Trading,
        Returning,
        Unloading
    }

    FactionNpc _npc;
    WorkerInventory _inventory;
    FactionController _partner;
    TradeOffer _offer;
    MerchantState _state;
    int _carriedGold;
    float _wait;
    float _lodNext;
    bool _phased;

    public string DebugStatus
    {
        get
        {
            string partner = _partner != null ? _partner.DisplayName : "none";
            string carry = _inventory == null || _inventory.IsEmpty
                ? $"gold:{_carriedGold}"
                : $"{_inventory.Amount} {_inventory.ResourceType} gold:{_carriedGold}";
            return $"{_state} {partner} {carry}";
        }
    }

    void Awake()
    {
        _npc = GetComponent<FactionNpc>();
        _inventory = GetComponent<WorkerInventory>();
    }

    void Start()
    {
        if (_npc != null && _npc.Faction != null && _inventory != null)
            _inventory.Configure(_npc.Faction.Economy.merchantCarryCapacity);
    }

    void OnDestroy()
    {
        DepositAll();
    }

    void Update()
    {
        if (_npc == null || _npc.IsDead || _npc.Faction == null || _npc.Motor == null)
            return;

        if (FactionNpcLod.IsFar(transform.position))
        {
            if (!_phased)
            {
                _phased = true;
                _lodNext = Time.time + FactionNpcLod.PhaseOffset(FactionNpcLod.PhaseSeed(this), FactionNpcLod.AiInterval);
            }
            if (Time.time < _lodNext)
                return;
            _lodNext = Time.time + FactionNpcLod.AiInterval;
        }

        if (_partner != null && FactionTradeSystem.AreFighting(_npc.Faction, _partner) &&
            _state != MerchantState.Returning &&
            _state != MerchantState.Unloading &&
            _state != MerchantState.Idle)
        {
            AbortToHome();
        }

        switch (_state)
        {
            case MerchantState.Idle:
                TickIdle();
                break;
            case MerchantState.GoingHomeMarket:
                TickGoTo(HomeMarketPosition(), MerchantState.Loading);
                break;
            case MerchantState.Loading:
                TickLoading();
                break;
            case MerchantState.TravelToPartner:
                TickGoTo(PartnerMarketPosition(), MerchantState.Trading);
                break;
            case MerchantState.Trading:
                TickTrading();
                break;
            case MerchantState.Returning:
                TickGoTo(HomeMarketPosition(), MerchantState.Unloading);
                break;
            case MerchantState.Unloading:
                TickUnloading();
                break;
        }
    }

    void TickIdle()
    {
        _wait -= Time.deltaTime;
        if (_wait > 0f)
            return;
        if (_npc.Faction.Market == null || !_npc.Faction.Market.IsOperational)
        {
            _wait = 2f;
            return;
        }

        Vector3 origin = HomeMarketPosition();
        MoveTo(origin);
        if (!FactionTradeSystem.TryPickPartner(_npc.Faction, origin, out _partner) ||
            !FactionTradeSystem.TryPlanDeal(_npc.Faction, _partner, out _offer))
        {
            _partner = null;
            _wait = 2f;
            return;
        }

        _state = MerchantState.GoingHomeMarket;
        MoveTo(origin);
    }

    void TickLoading()
    {
        if (_npc.Faction.Market == null || !_npc.Faction.Market.IsOperational)
        {
            AbortToHome();
            return;
        }

        if (!FactionTradeSystem.TryPlanDeal(_npc.Faction, _partner, out _offer))
        {
            _state = MerchantState.Idle;
            _wait = 2f;
            _npc.Motor.Stop();
            return;
        }

        if (_offer.seller == _npc.Faction)
        {
            if (!_npc.Faction.TryWithdrawResource(_offer.resource, _offer.units, out int taken) ||
                taken < _offer.units)
            {
                if (taken > 0)
                    _npc.Faction.AddResource(_offer.resource, taken);
                _state = MerchantState.Idle;
                _wait = 2f;
                return;
            }

            _inventory.Add(_offer.resource, taken);
        }
        else
        {
            if (!_npc.Faction.TrySpendGold(_offer.gold))
            {
                _state = MerchantState.Idle;
                _wait = 2f;
                return;
            }

            _carriedGold += _offer.gold;
        }

        _state = MerchantState.TravelToPartner;
        MoveTo(PartnerMarketPosition());
    }

    void TickTrading()
    {
        if (_partner == null || _partner.Market == null || !_partner.Market.IsOperational)
        {
            AbortToHome();
            return;
        }

        if (_offer.seller == _npc.Faction && _inventory != null && !_inventory.IsEmpty)
        {
            int units = _inventory.Amount;
            FactionResourceType type = _inventory.ResourceType;
            if (FactionTradeSystem.TryCompleteCarriedSale(
                    _npc.Faction, _partner, type, units, out int gold))
            {
                _inventory.TakeAll(out _);
                _carriedGold += gold;
            }
        }
        else if (_offer.buyer == _npc.Faction && _carriedGold > 0)
        {
            if (FactionTradeSystem.TryCompleteCarriedPurchase(
                    _npc.Faction, _partner, _offer.resource, _offer.units, _carriedGold,
                    out int units))
            {
                _carriedGold = 0;
                _inventory.Add(_offer.resource, units);
            }
        }

        _state = MerchantState.Returning;
        MoveTo(HomeMarketPosition());
    }

    void TickUnloading()
    {
        DepositAll();
        _partner = null;
        _offer = default;
        _state = MerchantState.Idle;
        _wait = 1.5f;
        _npc.Motor.Stop();
    }

    void TickGoTo(Vector3 destination, MerchantState next)
    {
        MoveTo(destination);
        if (_npc.Motor.IsStopped)
            _state = next;
    }

    void AbortToHome()
    {
        _state = MerchantState.Returning;
        MoveTo(HomeMarketPosition());
    }

    void MoveTo(Vector3 destination)
    {
        float speed = _npc.Faction.Combat.workerMoveSpeed;
        _npc.Motor.SetDestination(destination, speed, 2.8f);
    }

    void DepositAll()
    {
        if (_npc == null || _npc.Faction == null)
            return;
        if (_inventory != null && !_inventory.IsEmpty)
        {
            int amount = _inventory.TakeAll(out FactionResourceType type);
            if (amount > 0)
                _npc.Faction.AddResource(type, amount);
        }
        if (_carriedGold > 0)
        {
            _npc.Faction.AddGold(_carriedGold);
            _carriedGold = 0;
        }
    }

    Vector3 HomeMarketPosition()
    {
        if (_npc.Faction.Market != null)
            return _npc.Faction.Market.transform.position;
        return _npc.Faction.GetSafePosition();
    }

    Vector3 PartnerMarketPosition()
    {
        if (_partner != null && _partner.Market != null)
            return _partner.Market.transform.position;
        return _partner != null ? _partner.GetSafePosition() : transform.position;
    }
}
