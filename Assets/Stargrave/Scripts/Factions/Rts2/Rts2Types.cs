using System;
using Unity.Mathematics;
using UnityEngine;

namespace Stargrave.Rts2
{
    public enum Rts2Role : byte
    {
        Worker = 0,
        Infantry = 1,
        Archer = 2,
        Merchant = 3,
        Noble = 4
    }

    public enum Rts2Order : byte
    {
        Idle = 0,
        Move = 1,
        Gather = 2,
        Deposit = 3,
        Build = 4,
        AttackUnit = 5,
        AttackBuilding = 6,
        Claim = 7,
        Trade = 8,
        Flee = 9,
        Relocate = 10,
        Roam = 11
    }

    [Serializable]
    public struct Rts2Unit
    {
        public byte alive;
        public byte factionId;
        public Rts2Role role;
        public Rts2Order order;
        public float3 axis;
        public float radius;
        public float yaw;
        public float hp;
        public float maxHp;
        public float attackCooldown;
        public int targetUnit;
        public int targetBuilding;
        public int targetTown;
        public int resourceSiteId;
        public byte carryType;
        public byte carryAmount;
        public byte gatherTask;
        public int pathId;
        public int pathWaypoint;
    }

    public static class Rts2Roles
    {
        public static bool IsCombat(Rts2Role role) =>
            role == Rts2Role.Infantry || role == Rts2Role.Archer;

        public static RtsUnitRole ToLegacy(Rts2Role role)
        {
            switch (role)
            {
                case Rts2Role.Infantry: return RtsUnitRole.Infantry;
                case Rts2Role.Archer: return RtsUnitRole.Archer;
                case Rts2Role.Merchant: return RtsUnitRole.Merchant;
                case Rts2Role.Noble: return RtsUnitRole.Noble;
                default: return RtsUnitRole.Worker;
            }
        }

        public static Rts2Role FromLegacy(RtsUnitRole role)
        {
            switch (role)
            {
                case RtsUnitRole.Infantry: return Rts2Role.Infantry;
                case RtsUnitRole.Archer: return Rts2Role.Archer;
                case RtsUnitRole.Merchant: return Rts2Role.Merchant;
                case RtsUnitRole.Noble: return Rts2Role.Noble;
                default: return Rts2Role.Worker;
            }
        }
    }
}
