#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool that renders the SAME coloured Mercator-projection map of the procedural planet as
/// <see cref="PlanetMercatorMapExporter"/>, but reduced to EXACTLY <see cref="PALETTE_SIZE"/> colours
/// using deterministic MEDIAN-CUT colour quantization, then writes the result as both a JPG and a PNG.
///
/// Sampling and projection are intentionally identical to the full-colour exporter (longitude -> X over
/// [-180,180]; latitude via the Mercator formula clamped to +/-85 deg; north = +Y; colour read from
/// <see cref="Planet.GetSurfaceColorAtPosition"/> at the TRUE terrain elevation per direction by
/// replicating the planet's ShapeGenerator). The only difference is the post-process quantization.
///
/// Quantization: build a colour box over all sampled pixels, then recursively split the box with the
/// largest single-channel range at its median (along that channel) until we have PALETTE_SIZE boxes.
/// Each box's palette colour is the average of its pixels. Every pixel is then remapped to its nearest
/// palette colour (squared RGB distance). This does not modify the original full-colour export.
/// </summary>
[InitializeOnLoad]
public static class PlanetMercatorMap16ColourExporter
{
    // ---- Tunables (single, clearly-labeled consts so they're easy to change) ----
    const int PALETTE_SIZE_16 = 16;      // EXACT number of colours for the 16-colour export
    const int PALETTE_SIZE_8 = 8;        // EXACT number of colours for the 8-colour export
    const int MAP_WIDTH = 2048;          // output image width in pixels (longitude axis)
    const float LAT_CLAMP_DEG = 85f;     // Mercator latitude clamp (poles diverge to infinity)
    const int JPG_QUALITY = 95;          // EncodeToJPG quality (high: flat colour regions show JPG ringing)

    const string ExportFolderAsset = "Assets/Stargrave/Exports";

    // Trigger-file fallback (same pattern as PlanetMercatorMapExporter): the MCP bridge's
    // execute_menu_item is unreliable in this project, but a forced recompile (refresh_unity)
    // reliably reloads this assembly. On each domain reload we check for trigger files on disk;
    // if present we delete them and run the matching export. Drop a file + recompile to run.
    static readonly string TmpDir =
        Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "tmp"));
    static readonly string Trigger16Path = Path.Combine(TmpDir, "mercator16_export.trigger");
    static readonly string Trigger8Path = Path.Combine(TmpDir, "mercator8_export.trigger");

    static PlanetMercatorMap16ColourExporter()
    {
        EditorApplication.delayCall += () =>
        {
            try
            {
                if (File.Exists(Trigger16Path))
                {
                    File.Delete(Trigger16Path);
                    Debug.Log("[PlanetMercatorMap16ColourExporter] 16-colour trigger file detected on domain reload — running export.");
                    ExportPlanetMercatorMap16();
                }
                if (File.Exists(Trigger8Path))
                {
                    File.Delete(Trigger8Path);
                    Debug.Log("[PlanetMercatorMap16ColourExporter] 8-colour trigger file detected on domain reload — running export.");
                    ExportPlanetMercatorMap8();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PlanetMercatorMap16ColourExporter] Trigger check failed: {e}");
            }
        };
    }

    [MenuItem("Tools/Stargrave/Export Planet Mercator Map (16 Colours)")]
    public static void ExportPlanetMercatorMap16()
    {
        ExportQuantizedMercatorMap(PALETTE_SIZE_16, "PlanetMercator_16");
    }

    [MenuItem("Tools/Stargrave/Export Planet Mercator Map (8 Colours)")]
    public static void ExportPlanetMercatorMap8()
    {
        ExportQuantizedMercatorMap(PALETTE_SIZE_8, "PlanetMercator_8");
    }

    /// <summary>
    /// Shared pipeline: sample the planet's surface into a Mercator image, median-cut to exactly
    /// <paramref name="paletteSize"/> colours, remap, and write &lt;baseName&gt;.png + &lt;baseName&gt;.jpg.
    /// </summary>
    static void ExportQuantizedMercatorMap(int paletteSize, string baseName)
    {
        string title = $"Export Planet Mercator Map ({paletteSize} Colours)";

        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogError("[PlanetMercatorMap16ColourExporter] Unity is still compiling/importing. " +
                           "Wait for the spinner (bottom-right) to finish, then run again.");
            return;
        }

        // 1) Find the planet.
        Planet planet = Object.FindAnyObjectByType<Planet>();
        if (planet == null)
        {
            Debug.LogError("[PlanetMercatorMap16ColourExporter] No Planet found in the open scene. " +
                           "Open the scene containing the planet (e.g. SampleScene) and try again.");
            return;
        }

        // 2-3) Sample the planet surface into a Mercator Color32 buffer (generation + projection + true
        // terrain elevation handled inside the shared sampler). Returns null + logs on any failure.
        Color32[] pixels = SamplePlanetMercator(planet, out int height, true, title);
        if (pixels == null)
            return;

        // 4) Median-cut quantization -> exactly paletteSize palette colours, then remap pixels.
        Color32[] palette;
        try
        {
            EditorUtility.DisplayProgressBar(title,
                $"Quantizing to {paletteSize} colours (median-cut)...", 0.9f);
            palette = MedianCutPalette(pixels, paletteSize);
            RemapToPalette(pixels, palette);
        }
        catch (System.Exception e)
        {
            EditorUtility.ClearProgressBar();
            Debug.LogError($"[PlanetMercatorMap16ColourExporter] Quantization failed: {e}");
            return;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        // 5) Encode to PNG (lossless, crisp) and JPG (quality 95).
        var texture = new Texture2D(MAP_WIDTH, height, TextureFormat.RGBA32, false);
        texture.SetPixels32(pixels);
        texture.Apply(false);

        byte[] png = texture.EncodeToPNG();
        byte[] jpg = texture.EncodeToJPG(JPG_QUALITY);
        Object.DestroyImmediate(texture);

        if (png == null || png.Length == 0 || jpg == null || jpg.Length == 0)
        {
            Debug.LogError("[PlanetMercatorMap16ColourExporter] Encoding returned no data (PNG or JPG empty).");
            return;
        }

        if (!AssetDatabase.IsValidFolder(ExportFolderAsset))
        {
            if (!AssetDatabase.IsValidFolder("Assets/Stargrave"))
                AssetDatabase.CreateFolder("Assets", "Stargrave");
            AssetDatabase.CreateFolder("Assets/Stargrave", "Exports");
        }

        string jpgAsset = ExportFolderAsset + "/" + baseName + ".jpg";
        string pngAsset = ExportFolderAsset + "/" + baseName + ".png";
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string absJpg = Path.GetFullPath(Path.Combine(projectRoot, jpgAsset));
        string absPng = Path.GetFullPath(Path.Combine(projectRoot, pngAsset));

        try
        {
            File.WriteAllBytes(absPng, png);
            File.WriteAllBytes(absJpg, jpg);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[PlanetMercatorMap16ColourExporter] Failed to write output files: {e}");
            return;
        }

        bool pngBeforeRefresh = File.Exists(absPng);
        bool jpgBeforeRefresh = File.Exists(absJpg);

        AssetDatabase.Refresh();

        // Diagnostic + self-heal: AssetDatabase.Refresh imports the freshly written PNG and has been
        // observed to remove the on-disk source in this project. Re-check after the import and rewrite
        // the PNG without importing it if it went missing, so the user reliably gets a crisp PNG.
        bool pngAfterRefresh = File.Exists(absPng);
        bool jpgAfterRefresh = File.Exists(absJpg);
        if (!pngAfterRefresh)
        {
            try
            {
                File.WriteAllBytes(absPng, png);
                Debug.Log($"[PlanetMercatorMap16ColourExporter] PNG missing after AssetDatabase.Refresh — rewrote it. exists now = {File.Exists(absPng)}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PlanetMercatorMap16ColourExporter] Failed to rewrite PNG after refresh: {e}");
            }
        }
        Debug.Log($"[PlanetMercatorMap16ColourExporter] Disk check — beforeRefresh: png={pngBeforeRefresh} jpg={jpgBeforeRefresh}; afterRefresh: png={pngAfterRefresh} jpg={jpgAfterRefresh}; finalPngExists={File.Exists(absPng)}");

        var sb = new StringBuilder();
        for (int i = 0; i < palette.Length; i++)
        {
            Color32 p = palette[i];
            sb.Append($"\n    [{i:00}] #{p.r:X2}{p.g:X2}{p.b:X2}  rgb({p.r},{p.g},{p.b})");
        }

        Debug.Log($"[PlanetMercatorMap16ColourExporter] SUCCESS. Wrote {paletteSize}-colour Mercator map (median-cut).\n" +
                  $"  JPG absolute path: {absJpg}\n" +
                  $"  PNG absolute path: {absPng}\n" +
                  $"  Asset paths:       {jpgAsset} , {pngAsset}\n" +
                  $"  Dimensions:        {MAP_WIDTH} x {height} px\n" +
                  $"  JPG bytes:         {jpg.Length}   PNG bytes: {png.Length}\n" +
                  $"  Projection:        Mercator, longitude [-180,180] -> X, latitude clamp +/-{LAT_CLAMP_DEG} deg, north = +Y\n" +
                  $"  Palette ({palette.Length} colours):{sb}");
    }

    /// <summary>
    /// Compute an adaptive palette of exactly <paramref name="paletteSize"/> colours from the LIVE planet,
    /// using the SAME live-surface sampling + median-cut quantization as the Mercator map export — so the
    /// returned colours match the PlanetMercator_N map exactly. Returns null (and logs) on failure. Does NOT
    /// write any files. Reused by the 16-colour foliage palette builder.
    /// </summary>
    public static Color32[] ComputePlanetPalette(Planet planet, int paletteSize)
    {
        if (planet == null)
        {
            Debug.LogError("[PlanetMercatorMap16ColourExporter] ComputePlanetPalette: planet is null.");
            return null;
        }
        Color32[] pixels = SamplePlanetMercator(planet, out _, false, $"Compute {paletteSize}-colour palette");
        if (pixels == null)
            return null;
        return MedianCutPalette(pixels, paletteSize);
    }

    /// <summary>
    /// Samples the planet surface into a Mercator-projected <see cref="Color32"/> buffer (width =
    /// <see cref="MAP_WIDTH"/>, out <paramref name="height"/>). Generation, projection math and true
    /// per-direction terrain elevation are identical to the map export, and the colour comes from
    /// <see cref="Planet.GetSurfaceColorAtPosition"/>. Returns null (and logs) on failure.
    /// </summary>
    internal static Color32[] SamplePlanetMercator(Planet planet, out int height, bool showProgress, string title)
    {
        height = 0;
        if (planet == null)
            return null;

        if (planet.colourSettings == null || planet.shapeSettings == null)
        {
            Debug.LogError("[PlanetMercatorMap16ColourExporter] Planet is missing colourSettings/shapeSettings — cannot sample colours.");
            return null;
        }

        // Ensure the planet is generated so the baked colour table / biome lookup exist.
        try
        {
            planet.GeneratePlanet();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[PlanetMercatorMap16ColourExporter] GeneratePlanet() failed: {e}");
            return null;
        }

        Vector3 center = planet.transform.position;
        float baseRadius = planet.GetBaseRadiusWorld();
        if (baseRadius <= 0f)
        {
            Debug.LogError("[PlanetMercatorMap16ColourExporter] Planet base radius is <= 0; cannot sample surface.");
            return null;
        }

        // Replicate the planet's own shape generator to get true elevation per direction, so we sample at the
        // real terrain surface rather than flat sea level (identical to the full-colour exporter).
        float planetRadiusLocal = planet.shapeSettings.planetRadius;
        float worldScale = (planetRadiusLocal > 1e-6f) ? baseRadius / planetRadiusLocal : 1f;
        var shape = new ShapeGenerator();
        shape.UpdateSettings(planet.shapeSettings);

        // Build the Mercator image dimensions (identical math to the full-colour exporter).
        float latClamp = LAT_CLAMP_DEG * Mathf.Deg2Rad;
        float R = MAP_WIDTH / (2f * Mathf.PI);
        float yMax = R * Mathf.Log(Mathf.Tan(Mathf.PI / 4f + latClamp / 2f));
        height = Mathf.Max(1, Mathf.RoundToInt(2f * yMax));

        int pixelCount = MAP_WIDTH * height;
        var pixels = new Color32[pixelCount];

        try
        {
            if (showProgress)
                EditorUtility.DisplayProgressBar(title, "Sampling planet surface colours...", 0f);

            for (int py = 0; py < height; py++)
            {
                float fb = (py + 0.5f) / height;            // 0 at bottom (south) -> 1 at top (north)
                float mercY = yMax * (2f * fb - 1f);         // [-yMax, +yMax]
                float lat = 2f * Mathf.Atan(Mathf.Exp(mercY / R)) - Mathf.PI / 2f;
                float sinLat = Mathf.Sin(lat);
                float cosLat = Mathf.Cos(lat);

                int rowOffset = py * MAP_WIDTH;
                for (int px = 0; px < MAP_WIDTH; px++)
                {
                    float fx = (px + 0.5f) / MAP_WIDTH;       // 0..1 across longitude
                    float lon = (fx - 0.5f) * 2f * Mathf.PI;  // [-pi, pi]

                    Vector3 dir = new Vector3(cosLat * Mathf.Cos(lon), sinLat, cosLat * Mathf.Sin(lon));

                    float localElevation = shape.CalculatePointOnPlanet(dir).magnitude;
                    Vector3 worldPos = center + dir * (localElevation * worldScale);

                    Color c = planet.GetSurfaceColorAtPosition(worldPos);
                    pixels[rowOffset + px] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255),
                        255);
                }

                if (showProgress && (py & 31) == 0)
                    EditorUtility.DisplayProgressBar(title,
                        $"Sampling planet surface colours... row {py}/{height}", py / (float)height);
            }
        }
        catch (System.Exception e)
        {
            if (showProgress)
                EditorUtility.ClearProgressBar();
            Debug.LogError($"[PlanetMercatorMap16ColourExporter] Failed while sampling surface colours: {e}");
            return null;
        }
        finally
        {
            if (showProgress)
                EditorUtility.ClearProgressBar();
        }

        return pixels;
    }

    // ---------------------------------------------------------------------------------------------
    // Median-cut quantization
    // ---------------------------------------------------------------------------------------------

    struct ColorBox
    {
        public int start;   // inclusive index into the (shared) indices array
        public int end;     // exclusive
        public int rMin, rMax, gMin, gMax, bMin, bMax;
    }

    /// <summary>
    /// Build an adaptive palette of exactly <paramref name="paletteSize"/> colours via median-cut.
    /// Deterministic and dependency-free. Operates on indices into <paramref name="pixels"/> so the
    /// source pixels are not reordered.
    /// </summary>
    static Color32[] MedianCutPalette(Color32[] pixels, int paletteSize)
    {
        int n = pixels.Length;
        var indices = new int[n];
        for (int i = 0; i < n; i++) indices[i] = i;

        var boxes = new List<ColorBox> { MakeBox(pixels, indices, 0, n) };

        // Split until we have paletteSize boxes (or can no longer split any box).
        while (boxes.Count < paletteSize)
        {
            // Pick the box with the largest single-channel range that still has > 1 pixel.
            int bestIdx = -1;
            int bestRange = -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                ColorBox b = boxes[i];
                if (b.end - b.start <= 1) continue;
                int range = LongestChannelRange(b, out _);
                if (range > bestRange)
                {
                    bestRange = range;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) break; // every box is a single pixel — cannot split further

            ColorBox box = boxes[bestIdx];
            LongestChannelRange(box, out int channel);

            // Sort the box's slice along the longest channel, then split at the median.
            SortSliceByChannel(pixels, indices, box.start, box.end, channel);
            int mid = (box.start + box.end) / 2;
            // Guarantee progress: both halves must be non-empty.
            if (mid <= box.start) mid = box.start + 1;
            if (mid >= box.end) mid = box.end - 1;

            ColorBox left = MakeBox(pixels, indices, box.start, mid);
            ColorBox right = MakeBox(pixels, indices, mid, box.end);

            boxes[bestIdx] = left;
            boxes.Add(right);
        }

        // Palette colour = average of each box's pixels.
        var palette = new Color32[boxes.Count];
        for (int i = 0; i < boxes.Count; i++)
        {
            ColorBox b = boxes[i];
            long sr = 0, sg = 0, sb = 0;
            int count = b.end - b.start;
            for (int k = b.start; k < b.end; k++)
            {
                Color32 c = pixels[indices[k]];
                sr += c.r; sg += c.g; sb += c.b;
            }
            if (count <= 0) count = 1;
            palette[i] = new Color32(
                (byte)(sr / count),
                (byte)(sg / count),
                (byte)(sb / count),
                255);
        }

        return palette;
    }

    static ColorBox MakeBox(Color32[] pixels, int[] indices, int start, int end)
    {
        int rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0;
        for (int k = start; k < end; k++)
        {
            Color32 c = pixels[indices[k]];
            if (c.r < rMin) rMin = c.r; if (c.r > rMax) rMax = c.r;
            if (c.g < gMin) gMin = c.g; if (c.g > gMax) gMax = c.g;
            if (c.b < bMin) bMin = c.b; if (c.b > bMax) bMax = c.b;
        }
        return new ColorBox
        {
            start = start,
            end = end,
            rMin = rMin, rMax = rMax,
            gMin = gMin, gMax = gMax,
            bMin = bMin, bMax = bMax
        };
    }

    // Returns the largest channel range in the box; out channel is 0=R,1=G,2=B.
    static int LongestChannelRange(ColorBox b, out int channel)
    {
        int rRange = b.rMax - b.rMin;
        int gRange = b.gMax - b.gMin;
        int bRange = b.bMax - b.bMin;
        if (rRange >= gRange && rRange >= bRange) { channel = 0; return rRange; }
        if (gRange >= rRange && gRange >= bRange) { channel = 1; return gRange; }
        channel = 2; return bRange;
    }

    // In-place sort of indices[start,end) by the chosen channel of the referenced pixel.
    static void SortSliceByChannel(Color32[] pixels, int[] indices, int start, int end, int channel)
    {
        System.Array.Sort(indices, start, end - start, Comparer<int>.Create((a, b) =>
        {
            byte ca = ChannelValue(pixels[a], channel);
            byte cb = ChannelValue(pixels[b], channel);
            return ca.CompareTo(cb);
        }));
    }

    static byte ChannelValue(Color32 c, int channel)
    {
        if (channel == 0) return c.r;
        if (channel == 1) return c.g;
        return c.b;
    }

    static void RemapToPalette(Color32[] pixels, Color32[] palette)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 c = pixels[i];
            int best = 0;
            int bestDist = int.MaxValue;
            for (int p = 0; p < palette.Length; p++)
            {
                Color32 pc = palette[p];
                int dr = c.r - pc.r;
                int dg = c.g - pc.g;
                int db = c.b - pc.b;
                int dist = dr * dr + dg * dg + db * db;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = p;
                }
            }
            pixels[i] = palette[best];
        }
    }
}
#endif
