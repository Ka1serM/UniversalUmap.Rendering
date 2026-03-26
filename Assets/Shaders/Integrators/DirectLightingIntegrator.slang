#ifndef DIRECTLIGHTING_INTEGRATOR_SLANG
#define DIRECTLIGHTING_INTEGRATOR_SLANG

#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Geometry/Intersection.slang"
#include "../Geometry/SurfaceData.slang"
#include "../Core/Accumulation.slang"
#include "../Core/Payload.slang"
#include "../DirectLighting/DirectLighting.slang"
#include "../Core/RenderPolicy.slang"
#include "../Cameras/PerspectiveCamera.slang"
#include "../Cameras/ThinLensCamera.slang"
#include "../Samplers/R2Sampler.slang"
#include "../Materials/IMaterial.slang"
#include "../Lights/EnvironmentLight.slang"
#include "IIntegrator.slang"

struct DirectLightingIntegrator<TMaterial> : IIntegrator<Payload>
    where TMaterial : IMaterial
{
#ifdef USE_COMPUTE
    void traceRayCompute(float3 rayOrigin, float3 rayDirection, float tMin, float tMax, inout Payload payload)
    {
        HitInfo hit = traceScene(rayOrigin, rayDirection, tMin, tMax);
        if (hit.instanceIndex == InvalidInstance) {
            shadeMiss(rayDirection, payload);
            payload.ObjectIndex = InvalidInstance;
            return;
        }

        SurfaceData surface = loadComputeSurfaceData(hit.instanceIndex, hit.primitiveIndex, hit.barycentrics);
        shadeClosestHit(
            surface.WorldPosition,
            surface.WorldShadowPosition,
            surface.ShadingNormal,
            surface.GeometricNormal,
            surface.Tangent,
            surface.TangentSign,
            surface.UV,
            rayDirection,
            surface.Material,
            payload);
        payload.ObjectIndex = hit.instanceIndex;
    }
#endif

    void shadeMiss(float3 worldRayDirection, inout Payload payload)
    {
        shadeMiss(
            worldRayDirection,
            sceneSettings.Environment,
            sceneSettings.RenderSettings,
            payload);
    }

    void shadeClosestHit(
        float3 worldPosition,
        float3 shadowPosition,
        float3 shadingNormal,
        float3 geometricNormal,
        float3 interpolatedTangent,
        float interpolatedTangentSign,
        float2 interpolatedUV,
        float3 worldRayDirection,
        MaterialData material,
        inout Payload payload)
    {
        payload.Position = worldPosition;
        payload.LastBsdfPdf = 0.0;
        payload.LastNeeLightPdf = 0.0;

        float3 viewDir = fastNormalize(-worldRayDirection);
        float3 geometricFacingNormal = computeFacingGeometricNormal(geometricNormal, viewDir);

        TMaterial materialModel;
        MaterialSample materialSample = materialModel.sample(material, interpolatedUV);
        float opacity = materialSample.opacity;

        if (rand(payload.RngState) > opacity) {
            payload.Flags |= uint(BounceType.RAY_TRANSPARENT);
            payload.NextDirection = worldRayDirection;
            payload.Position = worldPosition + worldRayDirection * computeRayOriginBias(worldPosition);
            payload.Attenuation = float3(1.0);
            payload.Emission = float3(0.0);
            return;
        }

        float3 shadingNormalTextured = sampleMaterialShadingNormal(
            material,
            interpolatedUV,
            shadingNormal,
            interpolatedTangent,
            interpolatedTangentSign);

        float3 albedo = materialSample.albedo;
        float3 emission = materialSample.emission;
        float metallic = materialSample.metallic;
        float specular = materialSample.specular;
        float roughness = materialSample.roughness;
        roughness = regularizeSecondaryRoughness(roughness, payload.Depth);

        float3 shadingFacingNormal = computeFacingShadingNormal(shadingNormalTextured, viewDir, geometricFacingNormal);

        float3 facingNormal = shadingFacingNormal;
        float dielectricF0 = dielectricF0FromSpecular(specular);
        float3 F0 = lerp(float3(dielectricF0), albedo, metallic);
        float swSpecular = computeSpecularSamplingWeight(viewDir, facingNormal, albedo, F0, metallic, roughness);

        payload.Albedo = albedo;
        payload.Normal = facingNormal * 0.5 + 0.5;
        payload.Emission = shadeAmbientOcclusionPbrSurface(
            worldPosition,
            shadowPosition,
            facingNormal,
            geometricFacingNormal,
            viewDir,
            albedo,
            specular,
            metallic,
            roughness,
            swSpecular,
            emission,
            sceneSettings.Environment,
            payload.RngState);

        payload.Attenuation = float3(1.0);
        payload.NextDirection = worldRayDirection;
        payload.Flags |= uint(BounceType.RAY_TERMINATED);
    }

    void primaryRayGen(int2 pixelCoord, int2 screenSize)
    {
        if (pixelCoord.x >= screenSize.x || pixelCoord.y >= screenSize.y)
            return;

        int2 shadingScreenSize = effectiveShadingScreenSize(screenSize);
        int2 shadingPixelCoord = mapOutputPixelToShadingPixel(pixelCoord, screenSize, shadingScreenSize);
        int2 blockStart = shadingPixelStart(shadingPixelCoord, screenSize, shadingScreenSize);
        int2 blockEnd = shadingPixelEndExclusive(shadingPixelCoord, screenSize, shadingScreenSize);
        int2 blockCenterPixel = clamp((blockStart + (blockEnd - int2(1))) / 2, int2(0), screenSize - 1);
        if (!isShadingLeaderPixel(pixelCoord, screenSize, shadingScreenSize))
            return;

        uint2 seed = pcg2d(uint2(blockCenterPixel) ^ uint2(pushConstants.Frame * 16777619));
        uint rngState = seed.x;
        bool useThinLens = !isInteractiveFrame();
        bool canReuseStablePrimary = isInteractiveFrame() || (pushConstants.Camera.Aperture <= 0.0);

        R2Sampler sampler;
        SamplerState samplerState;
        sampler.startPixel(blockCenterPixel, pushConstants.Frame, samplerState);
        float3 stableRayOrigin, stableRayDirection;
        PerspectiveCamera<R2Sampler> stableCamera;
        stableCamera.generateRay(
            blockCenterPixel, screenSize, samplerState, sampler,
            true, stableRayOrigin, stableRayDirection);

        Payload stablePrimaryPayload;
        stablePrimaryPayload.initialize(rngState, 0u);

#ifdef USE_COMPUTE
        traceRayCompute(stableRayOrigin, stableRayDirection, 0.001, 1000.0, stablePrimaryPayload);
#else
        Payload stablePayload = stablePrimaryPayload;
        RayDesc stablePrimaryRay;
        stablePrimaryRay.Origin = stableRayOrigin;
        stablePrimaryRay.Direction = stableRayDirection;
        stablePrimaryRay.TMin = 0.001;
        stablePrimaryRay.TMax = 1000.0;
        TraceRay(topLevelAS, RAY_FLAG_FORCE_OPAQUE, 0xff, 0, 0, 0, stablePrimaryRay, stablePayload);
        stablePrimaryPayload = stablePayload;
#endif

        Payload payload = stablePrimaryPayload;
        if (useThinLens && !canReuseStablePrimary) {
            sampler.startPixel(blockCenterPixel, pushConstants.Frame, samplerState);
            float3 rayOrigin, rayDirection;
            ThinLensCamera<R2Sampler> camera;
            camera.generateRay(
                blockCenterPixel, screenSize, samplerState, sampler,
                true, rayOrigin, rayDirection);
            payload.initialize(rngState, 0u);
#ifdef USE_COMPUTE
                traceRayCompute(rayOrigin, rayDirection, 0.001, 1000.0, payload);
#else
                RayDesc ray;
                ray.Origin = rayOrigin;
                ray.Direction = rayDirection;
                ray.TMin = 0.001;
                ray.TMax = 1000.0;
                TraceRay(topLevelAS, RAY_FLAG_FORCE_OPAQUE, 0xff, 0, 0, 0, ray, payload);
#endif
        }

        float exposureScale = loadExposureScale();
        float historySamples = loadHistorySamples(pixelCoord);
        float newAlpha = float((payload.Flags & BounceType.ENV_TRANSPARENT) == 0u);

        bool stablePrimaryVisible = ((stablePrimaryPayload.Flags & BounceType.ENV_TRANSPARENT) == 0u);
        float3 finalAlbedo = stablePrimaryVisible ? stablePrimaryPayload.Albedo : float3(0.0);
        float3 finalNormal = stablePrimaryVisible ? stablePrimaryPayload.Normal : float3(0.0);

        for (int y = blockStart.y; y < blockEnd.y; ++y) {
            for (int x = blockStart.x; x < blockEnd.x; ++x) {
                int2 outputPixel = int2(x, y);
                int2 pickPixelCoord = int2(outputPixel.x, (screenSize.y - 1) - outputPixel.y);
                outputCrypto[pickPixelCoord] = stablePrimaryPayload.ObjectIndex;
                if (stablePrimaryPayload.ObjectIndex != InvalidInstance)
                    outputPosition[pickPixelCoord] = float4(stablePrimaryPayload.Position, 1.0);
                else
                    outputPosition[pickPixelCoord] = float4(0);

                storeAccumulatedFrame(outputPixel, historySamples, payload.Emission * exposureScale, newAlpha, 1.0);
                outputAlbedo[outputPixel] = float4(finalAlbedo, 1.0);
                outputNormal[outputPixel] = float4(finalNormal, 0.0);
            }
        }
    }
};

#endif // DIRECTLIGHTING_INTEGRATOR_SLANG
