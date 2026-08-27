using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Soft cartoon chrome for in-game HUD cards: rounded fill, thick outline, light drop shadow.
/// </summary>
public static class StargraveHudStyle
{
    public static readonly Color CardFill = new Color(0.18f, 0.15f, 0.2f, 0.78f);
    public static readonly Color CardFillWeapon = new Color(0.2f, 0.16f, 0.14f, 0.84f);
    public static readonly Color CardOutline = new Color(0.1f, 0.07f, 0.05f, 0.92f);
    public static readonly Color CardShadow = new Color(0f, 0f, 0f, 0.32f);
    public static readonly Color Cream = new Color(0.98f, 0.95f, 0.88f, 0.95f);
    public static readonly Color Health = new Color(0.95f, 0.42f, 0.36f, 1f);
    public static readonly Color Swim = new Color(0.38f, 0.82f, 0.95f, 1f);
    public static readonly Color Kills = new Color(0.95f, 0.78f, 0.32f, 1f);

    static Sprite _card;

    public static Sprite CardSprite()
    {
        if (_card != null)
            return _card;

        const int size = 64;
        const int radius = 16;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "StargraveHudCard"
        };

        var pixels = new Color32[size * size];
        var on = new Color32(255, 255, 255, 255);
        var off = new Color32(255, 255, 255, 0);
        int r2 = radius * radius;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool cornerX = x < radius || x >= size - radius;
                bool cornerY = y < radius || y >= size - radius;
                bool inside = true;
                if (cornerX && cornerY)
                {
                    int cx = x < radius ? radius : size - 1 - radius;
                    int cy = y < radius ? radius : size - 1 - radius;
                    int dx = x - cx;
                    int dy = y - cy;
                    inside = dx * dx + dy * dy <= r2;
                }

                pixels[y * size + x] = inside ? on : off;
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply();
        _card = Sprite.Create(
            tex,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f,
            0,
            SpriteMeshType.FullRect,
            new Vector4(radius, radius, radius, radius));
        _card.name = "StargraveHudCard";
        return _card;
    }

    public static void ApplyCard(Image image, Color fill)
    {
        if (image == null)
            return;
        image.sprite = CardSprite();
        image.type = Image.Type.Sliced;
        image.pixelsPerUnitMultiplier = 1.35f;
        image.color = fill;
        image.raycastTarget = false;

        var outline = image.GetComponent<Outline>() ?? image.gameObject.AddComponent<Outline>();
        outline.effectColor = CardOutline;
        outline.effectDistance = new Vector2(2.5f, -2.5f);
        outline.useGraphicAlpha = true;

        Shadow drop = null;
        Shadow[] shadows = image.GetComponents<Shadow>();
        for (int i = 0; i < shadows.Length; i++)
        {
            if (shadows[i] != null && shadows[i] is not Outline)
            {
                drop = shadows[i];
                break;
            }
        }

        if (drop == null)
            drop = image.gameObject.AddComponent<Shadow>();
        drop.effectColor = CardShadow;
        drop.effectDistance = new Vector2(3f, -4f);
        drop.useGraphicAlpha = true;
    }

    /// <summary>
    /// Same rounded card chrome as the HUD, tinted by <paramref name="accent"/> for menu buttons.
    /// </summary>
    public static Color MenuButtonFill(Color accent)
    {
        Color baseFill = new Color(CardFill.r, CardFill.g, CardFill.b, 0.9f);
        Color tint = new Color(accent.r, accent.g, accent.b, baseFill.a);
        return Color.Lerp(baseFill, tint, 0.4f);
    }

    public static void ApplyMenuButton(Image image, Button button, Text label, Color accent)
    {
        if (image == null)
            return;

        ApplyCard(image, MenuButtonFill(accent));
        image.raycastTarget = true;

        if (button != null)
        {
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.1f, 1.1f, 1.1f, 1f);
            colors.selectedColor = new Color(1.18f, 1.18f, 1.18f, 1f);
            colors.pressedColor = new Color(0.88f, 0.88f, 0.88f, 1f);
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.65f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
        }

        if (label == null)
            return;

        label.color = Cream;
        label.fontStyle = FontStyle.Bold;
        var labelOutline = label.GetComponent<Outline>() ?? label.gameObject.AddComponent<Outline>();
        labelOutline.effectColor = CardOutline;
        labelOutline.effectDistance = new Vector2(1.25f, -1.25f);
        labelOutline.useGraphicAlpha = true;
    }
}
