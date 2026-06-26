using UnityEngine;
using UnityEngine.InputSystem;
using Stargrave.UI;

namespace Stargrave.Interaction
{
    public class PlayerInteractionController : MonoBehaviour
    {
        [Header("References")]
        public Transform playerRoot;
        public Camera playerCamera;
        public SimplePromptUI promptUI;

        [Header("Detection")]
        public float proximityRadius = 2.8f;
        [Range(0.2f, 0.98f)]
        public float viewDotThreshold = 0.75f; // higher = must be closer to screen center
        public LayerMask interactMask = ~0;
        public QueryTriggerInteraction triggerMode = QueryTriggerInteraction.Collide;

        [Header("Debug")]
        public bool drawDebug = false;

        private readonly Collider[] hits = new Collider[32];
        private IInteractable current;

        void Reset()
        {
            playerRoot = transform;
            playerCamera = Camera.main;
        }

        void Update()
        {
            if (playerRoot == null) playerRoot = transform;
            if (playerCamera == null) playerCamera = Camera.main;

            FindBestInteractable();

            if (current != null)
            {
                promptUI?.Show($"Press E / Square — {current.GetPromptText()}");

                bool interactPressed =
                    (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame) ||
                    (Gamepad.current != null && Gamepad.current.buttonWest.wasPressedThisFrame);

                if (interactPressed)
                    current.Interact(playerRoot);
            }
            else
            {
                promptUI?.Hide();
            }
        }

        void FindBestInteractable()
        {
            current = null;

            int count = Physics.OverlapSphereNonAlloc(
                playerRoot.position,
                proximityRadius,
                hits,
                interactMask,
                triggerMode
            );

            float bestDot = viewDotThreshold;

            for (int i = 0; i < count; i++)
            {
                var c = hits[i];
                if (c == null) continue;

                // Look for an interactable on the collider or its parents
                var interactableMB = c.GetComponentInParent<MonoBehaviour>();
                if (interactableMB == null) continue;

                if (!(interactableMB is IInteractable interactable)) continue;

                if (!interactable.CanInteract(playerRoot, playerCamera)) continue;

                Vector3 to = (c.bounds.center - playerCamera.transform.position).normalized;
                float dot = Vector3.Dot(playerCamera.transform.forward, to);

                if (dot >= bestDot)
                {
                    bestDot = dot;
                    current = interactable;
                }
            }

            if (drawDebug)
            {
                Debug.DrawRay(playerCamera.transform.position, playerCamera.transform.forward * 10f,
                    current != null ? Color.green : Color.red);
            }
        }
    }
}
