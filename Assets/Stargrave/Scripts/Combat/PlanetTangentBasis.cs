using UnityEngine;

/// <summary>
/// Planet-local tangent frame for look, lock-on, and aim assist. Uses radial "up" from planet center
/// (same convention as <see cref="GravityAttractor"/>). All yaw and screen-relative aim math should use this,
/// not per-face cube flattening.
/// </summary>
public static class PlanetTangentBasis
{
    /// <summary>Outward unit normal at <paramref name="worldPosition"/> from <paramref name="planetCenter"/>.</summary>
    public static Vector3 GetPlanetUp(Vector3 worldPosition, Vector3 planetCenter)
    {
        Vector3 outward = worldPosition - planetCenter;
        if (outward.sqrMagnitude < 1e-10f)
            return Vector3.up;
        return outward.normalized;
    }

    /// <summary>Same as <see cref="GetPlanetUp"/> but resolves center from tagged Planet or <paramref name="fallbackUp"/>.</summary>
    public static Vector3 ResolvePlanetUp(Vector3 worldPosition, Transform planetCenterOverride, Vector3 fallbackUp)
    {
        if (planetCenterOverride != null)
            return GetPlanetUp(worldPosition, planetCenterOverride.position);

        GameObject tagged = GameObject.FindGameObjectWithTag("Planet");
        if (tagged != null)
        {
            var taggedPlanet = tagged.GetComponentInParent<Planet>();
            if (taggedPlanet != null)
                return GetPlanetUp(worldPosition, taggedPlanet.transform.position);
            return GetPlanetUp(worldPosition, tagged.transform.position);
        }

        Planet planet = Object.FindFirstObjectByType<Planet>(FindObjectsInactive.Exclude);
        if (planet != null)
            return GetPlanetUp(worldPosition, planet.transform.position);

        if (fallbackUp.sqrMagnitude < 1e-10f)
            return Vector3.up;
        return fallbackUp.normalized;
    }

    /// <summary>Project direction onto tangent plane orthogonal to <paramref name="planetUp"/>.</summary>
    public static Vector3 ProjectOnTangentPlane(Vector3 direction, Vector3 planetUp)
    {
        return Vector3.ProjectOnPlane(direction, planetUp);
    }

    /// <summary>Unit forward in tangent plane; if degenerate, returns <paramref name="fallbackForward"/> normalized on plane.</summary>
    public static Vector3 GetTangentForward(Vector3 worldForwardHint, Vector3 planetUp, Vector3 fallbackForward)
    {
        Vector3 flat = ProjectOnTangentPlane(worldForwardHint, planetUp);
        if (flat.sqrMagnitude > 1e-8f)
            return flat.normalized;

        flat = ProjectOnTangentPlane(fallbackForward, planetUp);
        if (flat.sqrMagnitude > 1e-8f)
            return flat.normalized;

        return Vector3.Cross(planetUp, Vector3.forward).normalized;
    }

    /// <summary>
    /// Signed yaw (degrees) from <paramref name="fromTangentForward"/> to <paramref name="toTangentForward"/> around <paramref name="planetUp"/>.
    /// </summary>
    public static float SignedYawDegrees(Vector3 fromTangentForward, Vector3 toTangentForward, Vector3 planetUp)
    {
        return Vector3.SignedAngle(fromTangentForward, toTangentForward, planetUp);
    }

    /// <summary>
    /// World-space look direction from yaw (around planet up at eye) and pitch relative to horizontal plane at eye.
    /// </summary>
    public static Vector3 GetWorldLookDirection(Vector3 eyeWorld, Vector3 planetUp, Vector3 horizontalForward, float pitchDegrees)
    {
        Vector3 f = GetTangentForward(horizontalForward, planetUp, horizontalForward);
        Quaternion yaw = Quaternion.LookRotation(f, planetUp);
        Quaternion pitch = Quaternion.AngleAxis(pitchDegrees, yaw * Vector3.right);
        return pitch * f;
    }

    /// <summary>
    /// Pitch (degrees) to look from <paramref name="eyeWorld"/> toward <paramref name="targetWorld"/> in the
    /// vertical plane through horizontal forward and planet up — matches <see cref="GetWorldLookDirection"/> and
    /// typical FPS rigs that pitch around local X after yaw.
    /// </summary>
    public static float ComputePitchDegreesTowardPoint(
        Vector3 eyeWorld,
        Vector3 planetUp,
        Vector3 horizontalForward,
        Vector3 targetWorld,
        float minPitch,
        float maxPitch)
    {
        Vector3 to = targetWorld - eyeWorld;
        if (to.sqrMagnitude < 1e-10f)
            return 0f;

        Vector3 dir = to.normalized;
        Vector3 f = GetTangentForward(horizontalForward, planetUp, horizontalForward);
        // Same pitch axis as GetWorldLookDirection: yaw = LookRotation(f, planetUp), pitch about yaw * right.
        Vector3 right = Vector3.Cross(planetUp, f);
        if (right.sqrMagnitude < 1e-10f)
            return 0f;
        right.Normalize();

        Vector3 inPlane = Vector3.ProjectOnPlane(dir, right);
        if (inPlane.sqrMagnitude < 1e-10f)
            return 0f;
        inPlane.Normalize();

        float pitch = Vector3.SignedAngle(f, inPlane, right);
        return Mathf.Clamp(pitch, minPitch, maxPitch);
    }
}
