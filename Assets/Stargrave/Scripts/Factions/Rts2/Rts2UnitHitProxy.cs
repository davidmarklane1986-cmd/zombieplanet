using UnityEngine;

namespace Stargrave.Rts2
{
    /// <summary>Collider proxy so player raycasts/projectiles can hit realized Rts2 unit visuals.</summary>
    [DisallowMultipleComponent]
    public sealed class Rts2UnitHitProxy : MonoBehaviour
    {
        public int UnitIndex { get; private set; } = -1;
        public int FactionId { get; private set; } = -1;

        public void Bind(int unitIndex, int factionId)
        {
            UnitIndex = unitIndex;
            FactionId = factionId;
        }
    }
}
