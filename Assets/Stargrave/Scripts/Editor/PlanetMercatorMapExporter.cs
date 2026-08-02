#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool that renders a COLOURED Mercator-projection map of the procedural planet's
/// surface (biome gradient colours — what the shader actually paints, not an elevation greyscale)
/// and writes it to disk as a JPG.
///
/// Sampling: for each output pixel we convert (lat, lon) -> a unit direction on the planet's
/// local sphere (north = +Y) and ask the planet for the EXACT painted surface colour at
/// planetCenter + dir * baseRadius via <see cref="Planet.GetSurfaceColorAtPosition"/>
/// (=> ColourGenerator.GetFinalSurfaceColor: the same baked colour table + blurred biome lookup
/// the surface shader uses). This matches the biome colours seen on the planet.
///
/// Projection: longitude -> X linearly over [-180,180]; latitude -> Y via the Mercator formula
/// y = ln(tan(pi/4 + lat/2)), clamped to +/-85 deg (poles diverge). Height is derived from the
/// width so the vertical scale is conformally correct for the clamped latitude band.
/// </summary>
[InitializeOnLoad]
public static class PlanetMercatorMapExporter
{
    // ---- Tunables (single, clearly-labeled consts so they're easy to change) ----
    const int MAP_WIDTH = 2048;          // output image width in pixels (longitude axis)
    const float LAT_CLAMP_DEG = 85f;     // Mercator latitude clamp (poles diverge to infinity)
    const int JPG_QUALITY = 90;          // EncodeToJPG quality

    const string ExportFolderAsset = "Assets/Stargrave/Exports";
    const string ExportAssetPath = ExportFolderAsset + "/PlanetMercator.jpg";

    // Trigger-file fallback: the MCP bridge's execute_menu_item is unreliable here, but a forced
    // recompile (refresh_unity) reliably reloads this assembly. On each domain reload we check for a
    // trigger file on disk; if present we delete it and run the export. Drop the file + recompile to run.
    static readonly string TriggerFilePath =
        Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "tmp/mercator_export.trigger"));

    static PlanetMercatorMapExporter()
    {
        Debug.Log($"[PlanetMercatorMapExporter] Static ctor ran (InitializeOnLoad). Watching trigger: {TriggerFilePath}");
        EditorApplication.delayCall += () =>
        {
            try
            {
                bool exists = File.Exists(TriggerFilePath);
                Debug.Log($"[PlanetMercatorMapExporter] delayCall check. Trigger exists = {exists} at {TriggerFilePath}");
                if (exists)
                {
                    File.Delete(TriggerFilePath);
                    Debug.Log("[PlanetMercatorMapExporter] Trigger file detected on domain reload — running export.");
                    ExportPlanetMercatorMap();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PlanetMercatorMapExporter] Trigger check failed: {e}");
            }
        };
    }

    [MenuItem("Tools/Stargrave/Export Planet Mercator Map")]
    public static void ExportPlanetMercatorMap()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogError("[PlanetMercatorMapExporter] Unity is still compiling/importing. " +
                           "Wait for the spinner (bottom-right) to finish, then run again.");
            return;
        }

        // 1) Find the planet.
        Planet planet = Object.FindAnyObjectByType<Planet>();
        if (planet == null)
        {
            Debug.LogError("[PlanetMercatorMapExporter] No Planet found in the open scene. " +
                           "Open the scene containing the planet (e.g. SampleScene) and try again.");
            return;
        }

        if (planet.colourSettings == null || planet.shapeSettings == null)
        {
            Debug.LogError("[PlanetMercatorMapExporter] Planet is missing colourSettings/shapeSettings — cannot sample colours.");
            return;
        }

        // 2) Ensure the planet is generated so the baked colour table / biome lookup exist.
        //    In edit mode IsGenerated may be false; GeneratePlanet() (Initialize + mesh + colours)
        //    builds everything ColourGenerator.GetFinalSurfaceColor needs.
        try
        {
            planet.GeneratePlanet();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[PlanetMercatorMapExporter] GeneratePlanet() failed: {e}");
            return;
        }

        Vector3 center = planet.transform.position;
        float baseRadius = planet.GetBaseRadiusWorld();
        if (baseRadius <= 0f)
        {
            Debug.LogError("[PlanetMercatorMapExporter] Planet base radius is <= 0; cannot sample surface.");
            return;
        }

        // We sample colour at the REAL terrain surface (not the flat base sphere) so the gradient's
        // elevation axis actually varies — otherwise every pixel reads sea-level (water) and the map
        // is a flat blue. Replicate the planet's own shape generator to get true elevation per
        // direction, then sample at center + dir * (localElevation * scale). GetSurfaceColorAtPosition
        // normalizes that distance against the planet's elevationMinMax, giving the painted colour.
        float planetRadiusLocal = planet.shapeSettings.planetRadius;
        float worldScale = (planetRadiusLocal > 1e-6f) ? baseRadius / planetRadiusLocal : 1f;
        var shape = new ShapeGenerator();
        shape.UpdateSettings(planet.shapeSettings);

        // 3) Build the Mercator image dimensions.
        //    R = pixels-per-radian for the longitude axis = width / (2*pi).
        //    Conformal Mercator uses the SAME scale on Y, so the clamped band [-latClamp, +latClamp]
        //    maps to height = 2 * R * ln(tan(pi/4 + latClamp/2)).
        float latClamp = LAT_CLAMP_DEG * Mathf.Deg2Rad;
        float R = MAP_WIDTH / (2f * Mathf.PI);
        float yMax = R * Mathf.Log(Mathf.Tan(Mathf.PI / 4f + latClamp / 2f)); // mercator Y at +latClamp
        int height = Mathf.Max(1, Mathf.RoundToInt(2f * yMax));

        var texture = new Texture2D(MAP_WIDTH, height, TextureFormat.RGB24, false);
        var pixels = new Color[MAP_WIDTH * height];

        try
        {
            EditorUtility.DisplayProgressBar("Export Planet Mercator Map", "Sampling planet surface colours...", 0f);

            for (int py = 0; py < height; py++)
            {
                // py = 0 is the BOTTOM row of the texture (Unity textures are bottom-up), which
                // EncodeToJPG writes as the bottom of the image. So py increasing -> northward.
                float fb = (py + 0.5f) / height;            // 0 at bottom (south) -> 1 at top (north)
                float mercY = yMax * (2f * fb - 1f);         // [-yMax, +yMax]
                // Inverse Mercator: lat = 2*atan(e^(y/R)) - pi/2.
                float lat = 2f * Mathf.Atan(Mathf.Exp(mercY / R)) - Mathf.PI / 2f;
                float sinLat = Mathf.Sin(lat);
                float cosLat = Mathf.Cos(lat);

                int rowOffset = py * MAP_WIDTH;
                for (int px = 0; px < MAP_WIDTH; px++)
                {
                    float fx = (px + 0.5f) / MAP_WIDTH;       // 0..1 across longitude
                    float lon = (fx - 0.5f) * 2f * Mathf.PI;  // [-pi, pi]

                    // Local-sphere direction, north = +Y. Matches ColourGenerator's biome-lookup
                    // convention (cos(lat)cos(lon), sin(lat), cos(lat)sin(lon)) so colours align.
                    Vector3 dir = new Vector3(cosLat * Mathf.Cos(lon), sinLat, cosLat * Mathf.Sin(lon));

                    // True terrain elevation in this direction (local units), then to world distance.
                    float localElevation = shape.CalculatePointOnPlanet(dir).magnitude;
                    Vector3 worldPos = center + dir * (localElevation * worldScale);

                    pixels[rowOffset + px] = planet.GetSurfaceColorAtPosition(worldPos);
                }

                if ((py & 31) == 0)
                    EditorUtility.DisplayProgressBar("Export Planet Mercator Map",
                        $"Sampling planet surface colours... row {py}/{height}", py / (float)height);
            }
        }
        catch (System.Exception e)
        {
            EditorUtility.ClearProgressBar();
            Debug.LogError($"[PlanetMercatorMapExporter] Failed while sampling surface colours: {e}");
            Object.DestroyImmediate(texture);
            return;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        texture.SetPixels(pixels);
        texture.Apply(false);

        // 4) Encode to JPG and write to the project asset path + log the absolute path.
        byte[] jpg = texture.EncodeToJPG(JPG_QUALITY);
        Object.DestroyImmediate(texture);

        if (jpg == null || jpg.Length == 0)
        {
            Debug.LogError("[PlanetMercatorMapExporter] EncodeToJPG returned no data.");
            return;
        }

        if (!AssetDatabase.IsValidFolder(ExportFolderAsset))
        {
            if (!AssetDatabase.IsValidFolder("Assets/Stargrave"))
                AssetDatabase.CreateFolder("Assets", "Stargrave");
            AssetDatabase.CreateFolder("Assets/Stargrave", "Exports");
        }

        // Absolute path: project root is the parent of "Assets".
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string absolutePath = Path.GetFullPath(Path.Combine(projectRoot, ExportAssetPath));

        try
        {
            File.WriteAllBytes(absolutePath, jpg);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[PlanetMercatorMapExporter] Failed to write file to '{absolutePath}': {e}");
            return;
        }

        AssetDatabase.Refresh();

        Debug.Log($"[PlanetMercatorMapExporter] SUCCESS. Wrote coloured Mercator map.\n" +
                  $"  Absolute path: {absolutePath}\n" +
                  $"  Asset path:    {ExportAssetPath}\n" +
                  $"  Dimensions:    {MAP_WIDTH} x {height} px\n" +
                  $"  Projection:    Mercator, longitude [-180,180] -> X, latitude clamp +/-{LAT_CLAMP_DEG} deg, north = +Y\n" +
                  $"  Colour source: Planet.GetSurfaceColorAtPosition (ColourGenerator.GetFinalSurfaceColor — baked biome colour table), sampled at TRUE terrain elevation per direction\n" +
                  $"  Bytes:         {jpg.Length}");
    }
}
#endif
