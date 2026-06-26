using TMPro;
using UnityEngine;

public class PromptUI : MonoBehaviour
{
    [Header("Assign a TextMeshProUGUI here")]
    public TextMeshProUGUI text;

    void Awake()
    {
        if (text == null)
            text = GetComponentInChildren<TextMeshProUGUI>(true);

        Hide();
    }

    public void Show(string msg)
    {
        if (text == null) return;
        text.text = msg;
        text.enabled = true;
    }

    public void Hide()
    {
        if (text == null) return;
        text.text = "";
        text.enabled = false;
    }
}
