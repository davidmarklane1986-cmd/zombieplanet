using UnityEngine;

/// <summary>
/// Resolves player grounded/swimming state against spherical planet + optional water shell (Stargrave 1.3).
/// </summary>
public static class PlayerPlanetSwimStateUtil
{
    public readonly struct SwimGroundState
    {
        public readonly bool Swimming;
        public readonly bool Grounded;

        public SwimGroundState(bool swimming, bool grounded)
        {
            Swimming = swimming;
            Grounded = grounded;
        }
    }

    public static SwimGroundState Resolve(
        Transform playerTransform,
        Vector3 rigidbodyPosition,
        Transform planet,
        PlanetWaterLayer waterLayer,
        CapsuleCollider bodyCapsule,
        bool currentlySwimming,
        float swimZonePadding,
        float swimZoneExitBuffer,
        float solidGroundRayLength = 1.2f)
    {
        if (planet == null || playerTransform == null)
            return new SwimGroundState(false, false);

        bool solidGrounded = IsSolidGroundRaycast(playerTransform.position, planet.position, solidGroundRayLength);
        if (waterLayer == null)
            return new SwimGroundState(false, solidGrounded);

        float shell = waterLayer.GetWorldWaterShellRadius();
        if (shell <= 0f)
            return new SwimGroundState(false, solidGrounded);

        Vector3 waterCenter = waterLayer.GetWaterShellWorldCenter();
        Vector3 bodyPoint = GetSwimReferenceWorldPosition(rigidbodyPosition, bodyCapsule);
        float bodyR = Vector3.Distance(bodyPoint, waterCenter);

        float enterR = shell + swimZonePadding;
        float exitR = shell + swimZonePadding + swimZoneExitBuffer;
        bool inVolume = currentlySwimming ? bodyR < exitR : bodyR < enterR;

        bool swimming = inVolume && !solidGrounded;
        bool grounded = swimming || solidGrounded;
        return new SwimGroundState(swimming, grounded);
    }

    static bool IsSolidGroundRaycast(Vector3 playerPosition, Vector3 planetCenter, float rayLength)
    {
        Vector3 downDir = (planetCenter - playerPosition).normalized;
        Ray ray = new Ray(playerPosition, downDir);
        return Physics.Raycast(ray, out _, rayLength);
    }

    static Vector3 GetSwimReferenceWorldPosition(Vector3 rigidbodyPosition, CapsuleCollider bodyCapsule)
    {
        if (bodyCapsule != null)
            return bodyCapsule.transform.TransformPoint(bodyCapsule.center);
        return rigidbodyPosition;
    }
}
