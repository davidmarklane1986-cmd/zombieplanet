using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Reusable "colour → asset" foliage layout: an ordered list of <see cref="FoliageColourRule"/>.
/// Rule 0 is conventionally the grass rule (GPU-instanced, green key) so the palette reproduces the
/// existing GpuGrassCarpet behaviour out of the box. Assign on a <see cref="FoliageByColour"/> driver
/// or drop in Assets/Stargrave/Resources so it can be Resources.Load'd by name.
/// </summary>
[CreateAssetMenu(fileName = "FoliagePalette", menuName = "Stargrave/Foliage Palette")]
public class FoliagePalette : ScriptableObject
{
    [Tooltip("Placement rules, evaluated together per scatter point. A point is assigned to the rule with the strongest colour match.")]
    public List<FoliageColourRule> rules = new List<FoliageColourRule>();
}
