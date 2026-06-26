using UnityEngine;
using Stargrave.Interaction;

namespace Stargrave.Gameplay
{
    public class RelicInteractable : MonoBehaviour, IInteractable
    {
        public enum RelicType { JumpBoost, SpeedBoost, DamageBoost, FireRateBoost }

        [Header("Identity")]
        public string relicName = "Relic";
        public RelicType type = RelicType.JumpBoost;

        [Header("Timing")]
        public float activeDuration = 20f;
        public bool reusable = true;

        [Header("Boost Multipliers")]
        public float jumpMultiplier = 1.35f;
        public float speedMultiplier = 1.35f;
        public float damageMultiplier = 1.35f;
        public float fireRateMultiplier = 1.35f;

        [Header("Visual")]
        public Renderer rend;
        public Color idleColor = Color.gray;
        public Color activeColor = Color.cyan;

        bool isActive;
        float endTime;
        bool spent;

        void Awake()
        {
            if (rend == null) rend = GetComponentInChildren<Renderer>();
            SetActive(false);
        }

        void Update()
        {
            if (isActive && Time.time >= endTime)
                SetActive(false);
        }

        public string GetPromptText()
        {
            if (isActive) return $"{relicName} ({type}) ACTIVE";
            if (!reusable && spent) return $"{relicName} SPENT";
            return type switch
            {
                RelicType.DamageBoost => $"Activate {relicName} (damage x{damageMultiplier})",
                RelicType.FireRateBoost => $"Activate {relicName} (fire rate x{fireRateMultiplier})",
                _ => $"Activate {relicName} ({type})"
            };
        }

        public bool CanInteract(Transform playerRoot, Camera cam)
        {
            if (isActive) return false;
            if (!reusable && spent) return false;
            return true;
        }

        public void Interact(Transform playerRoot)
        {
            if (isActive) return;
            if (!reusable && spent) return;

            spent = true;
            isActive = true;
            endTime = Time.time + activeDuration;
            SetActive(true);

            // Buff hook (works if you already have PlayerBuffController, but doesn't require it)
            var buffs = playerRoot.GetComponent<PlayerBuffController>();
            if (buffs != null)
            {
                float spd = type == RelicType.SpeedBoost ? speedMultiplier : 1f;
                float jmp = type == RelicType.JumpBoost ? jumpMultiplier : 1f;
                float dmg = type == RelicType.DamageBoost ? damageMultiplier : 1f;
                float rof = type == RelicType.FireRateBoost ? fireRateMultiplier : 1f;
                buffs.ApplyTimedBuff(relicName, activeDuration, spd, jmp, dmg, rof);
            }

            Debug.Log($"Activated {relicName} ({type}) for {activeDuration}s");
        }

        void SetActive(bool active)
        {
            isActive = active;
            if (rend != null)
                rend.material.color = active ? activeColor : idleColor;
        }
    }
}
