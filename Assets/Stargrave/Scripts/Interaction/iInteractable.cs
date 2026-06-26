using UnityEngine;

namespace Stargrave.Interaction
{
    /// <summary>
    /// Anything the player can interact with must implement this.
    /// IMPORTANT: Interactables must NOT read input themselves.
    /// </summary>
    public interface IInteractable
    {
        string GetPromptText();
        bool CanInteract(Transform playerRoot, Camera cam);
        void Interact(Transform playerRoot);
    }
}
