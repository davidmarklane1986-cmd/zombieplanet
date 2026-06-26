using TMPro;
using UnityEngine;

namespace Stargrave.UI
{
    public class SimplePromptUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text promptText;

        void Awake()
        {
            Hide();
        }

        public void Show(string msg)
        {
            if (promptText == null) return;
            promptText.gameObject.SetActive(true);
            promptText.text = msg;
        }

        public void Hide()
        {
            if (promptText == null) return;
            promptText.text = "";
            promptText.gameObject.SetActive(false);
        }
    }
}
