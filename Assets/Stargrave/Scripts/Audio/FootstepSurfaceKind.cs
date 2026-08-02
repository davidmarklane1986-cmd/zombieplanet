/// <summary>
/// Coarse surface/area categories used to pick procedural footstep SFX. Mapped from the planet's own
/// surface colour classification (see <c>Planet.GetFootstepSurface</c>) plus the player's water state.
/// </summary>
public enum FootstepSurfaceKind
{
    Default,
    Grass,
    Sand,
    Snow,
    Rock,
    Water
}
