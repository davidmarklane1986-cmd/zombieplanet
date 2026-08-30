using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

public sealed class ProceduralPlanetCloudsRendererFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public sealed class Settings
    {
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
        [Tooltip("Use the spherical full-screen volume pass. Disable to use the proxy shell fallback.")]
        public bool useFullscreenPass = false;
    }

    public Settings settings = new Settings();

    CloudPass _pass;

    public override void Create()
    {
        _pass = new CloudPass(settings.renderPassEvent);
    }

    public override void AddRenderPasses(
        ScriptableRenderer renderer,
        ref RenderingData renderingData)
    {
        if (!settings.useFullscreenPass)
            return;

        if (_pass == null ||
            renderingData.cameraData.cameraType == CameraType.Preview ||
            renderingData.cameraData.cameraType == CameraType.Reflection ||
            renderingData.cameraData.cameraType == CameraType.SceneView)
            return;

        Camera camera = renderingData.cameraData.camera;
        if (camera == null || camera.targetTexture != null)
            return;

        ProceduralPlanetClouds clouds = ProceduralPlanetClouds.ActiveInstance;
        if (clouds == null || !clouds.useFullscreenVolume ||
            !clouds.ShouldRenderForCamera(camera) ||
            clouds.FullscreenMaterial == null)
            return;

        _pass.Setup(clouds.FullscreenMaterial);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        _pass = null;
    }

    sealed class CloudPass : ScriptableRenderPass
    {
        Material _material;

        public CloudPass(RenderPassEvent passEvent)
        {
            renderPassEvent = passEvent;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public void Setup(Material material)
        {
            _material = material;
        }

        public override void RecordRenderGraph(
            RenderGraph renderGraph,
            ContextContainer frameContext)
        {
            if (_material == null)
                return;

            UniversalResourceData resourceData =
                frameContext.Get<UniversalResourceData>();
            TextureHandle source = resourceData.activeColorTexture;
            TextureHandle sceneDepth = resourceData.cameraDepthTexture;
            if (!source.IsValid() || !sceneDepth.IsValid())
                return;

            TextureDesc descriptor = source.GetDescriptor(renderGraph);
            descriptor.depthBufferBits = 0;
            descriptor.name = "_ProceduralPlanetCloudsTexture";
            TextureHandle destination = renderGraph.CreateTexture(descriptor);

            RenderGraphUtils.BlitMaterialParameters blitParameters =
                new RenderGraphUtils.BlitMaterialParameters(
                    source, destination, _material, 1);
            using (var builder = renderGraph.AddBlitPass(
                blitParameters, "Procedural Planet Clouds", returnBuilder: true))
            {
                builder.UseTexture(sceneDepth);
            }
            resourceData.cameraColor = destination;
        }
    }
}
