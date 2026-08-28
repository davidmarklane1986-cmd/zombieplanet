using UnityEngine;

namespace Stargrave.CameraOcclusion
{
    public static class ScreenCircleOcclusionShader
    {
        public static readonly int PlayerCenterId = Shader.PropertyToID("_StargraveOccPlayerCenter");
        public static readonly int ScreenCenterId = Shader.PropertyToID("_StargraveOccScreenCenter");
        public static readonly int SightDirId = Shader.PropertyToID("_StargraveOccSightDir");
        public static readonly int ScreenRadiusId = Shader.PropertyToID("_StargraveOccScreenRadius");
        public static readonly int PlayerViewDepthId = Shader.PropertyToID("_StargraveOccPlayerViewDepth");
        public static readonly int EdgeSoftnessId = Shader.PropertyToID("_StargraveOccEdgeSoftness");
        public static readonly int DepthMarginId = Shader.PropertyToID("_StargraveOccDepthMargin");

        /// <summary>
        /// screenCenterViewport must come from Camera.WorldToViewportPoint (origin bottom-left).
        /// </summary>
        public static void ApplyGlobals(
            Vector3 playerCenter,
            Vector2 screenCenterViewport,
            Vector3 sightDirection,
            float playerViewDepth,
            float screenRadiusViewport,
            float edgeSoftnessViewport,
            float depthMargin)
        {
            Vector2 shaderCenter = ViewportToShaderUv(screenCenterViewport);
            Shader.SetGlobalVector(PlayerCenterId, playerCenter);
            Shader.SetGlobalVector(ScreenCenterId, shaderCenter);
            Shader.SetGlobalVector(SightDirId, sightDirection);
            Shader.SetGlobalFloat(PlayerViewDepthId, playerViewDepth);
            Shader.SetGlobalFloat(ScreenRadiusId, screenRadiusViewport);
            Shader.SetGlobalFloat(EdgeSoftnessId, edgeSoftnessViewport);
            Shader.SetGlobalFloat(DepthMarginId, depthMargin);
        }

        public static Vector2 ViewportToShaderUv(Vector2 viewportBottomLeft)
        {
            // GetNormalizedScreenSpaceUV performs the platform-specific
            // render-target Y correction inside the shader. WorldToViewportPoint
            // already uses Unity's bottom-left viewport convention, so do not
            // flip this value here as well.
            return viewportBottomLeft;
        }

        public static void ClearGlobals()
        {
            Shader.SetGlobalFloat(ScreenRadiusId, 0f);
        }
    }
}
