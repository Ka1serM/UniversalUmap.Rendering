#ifndef AO_INTEGRATOR_SLANG
#define AO_INTEGRATOR_SLANG

#include "../Bindings.slang"
#include "../Geometry/Intersection.slang"
#include "../Geometry/SurfaceData.slang"
#include "../Core/Payload.slang"
#include "../Core/Accumulation.slang"
#include "../Core/RenderPolicy.slang"
#include "../Cameras/PerspectiveCamera.slang"
#include "../Cameras/ThinLensCamera.slang"
#include "../Samplers/R2Sampler.slang"
#include "../AO/AmbientOcclusion.slang"
#include "IIntegrator.slang"
#include "../Lights/EnvironmentLight.slang"

struct AoIntegrator : IIntegrator<AoPayload>
{
#ifdef USE_COMPUTE
    void traceRayCompute(float3 rayOrigin, float3 rayDirection, float tMin, float tMax, inout AoPayload payload)
    {
        HitInfo hit = traceScene(rayOrigin, rayDirection, tMin, tMax);
        if (hit.instanceIndex == INVALID_INSTANCE) {
            shadeMiss(rayDirection, payload);
            payload.objectIndex = INVALID_INSTANCE;
            return;
        }

        SurfaceData surface = loadComputeSurfaceData(hit.instanceIndex, hit.primitiveIndex, hit.barycentrics);
        shadeClosestHit(
            surface.worldPosition,
            surface.worldShadowPosition,
            surface.shadingNormal,
            surface.geometricNormal,
            surface.tangent,
            surface.tangentSign,
            surface.uv,
            rayDirection,
            surface.material,
            payload);
        payload.objectIndex = hit.instanceIndex;
    }
#endif

    void shadeMiss(float3 worldRayDirection, inout AoPayload payload)
    {
        shadeMiss(
            worldRayDirection,
            sceneSettings.environment,
            sceneSettings.renderSettings,
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
        inout AoPayload payload)
    {
        float3 viewDir = fastNormalize(-worldRayDirection);
        float3 geometricFacingNormal = computeFacingGeometricNormal(geometricNormal, viewDir);

        float3 unusedShadowPosition = shadowPosition;
        payload.position = worldPosition;
        float3 albedo = float3(1.0);

        float3 shadingNormalTextured = sampleMaterialShadingNormal(
            material,
            interpolatedUV,
            shadingNormal,
            interpolatedTangent,
            interpolatedTangentSign);
        float3 shadingFacingNormal = computeFacingShadingNormal(shadingNormalTextured, viewDir, geometricFacingNormal);

        payload.albedo = albedo;
        payload.normal = shadingFacingNormal * 0.5 + 0.5;
        payload.emission = shadeAmbientOcclusionSurface(worldPosition, geometricFacingNormal, payload.rngState);
        payload.flags |= uint(BounceType.RAY_TERMINATED);
    }

    void primaryRayGen(int2 pixelCoord, int2 screenSize)
    {
        if (pixelCoord.x >= screenSize.x || pixelCoord.y >= screenSize.y)
            return;

        const int2 pickPixelCoord = int2(pixelCoord.x, (screenSize.y - 1) - pixelCoord.y);
        uint2 seed = pcg2d(uint2(pixelCoord) ^ uint2(pushConstants.frame * 16777619));
        uint rngState = seed.x;

        R2Sampler sampler;
        SamplerState samplerState;
        sampler.startPixel(pixelCoord, pushConstants.frame, samplerState);
        float3 rayOrigin, rayDirection;
        if (isInteractiveFrame()) {
            PerspectiveCamera<R2Sampler> camera;
            camera.generateRay(pixelCoord, screenSize, samplerState, sampler, true, rayOrigin, rayDirection);
        } else {
            ThinLensCamera<R2Sampler> camera;
            camera.generateRay(pixelCoord, screenSize, samplerState, sampler, true, rayOrigin, rayDirection);
        }

        AoPayload payload;
        payload.initialize(rngState);

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

        outputCrypto[pickPixelCoord] = payload.objectIndex;

        if (payload.objectIndex != INVALID_INSTANCE)
            outputPosition[pickPixelCoord] = float4(payload.position, 1.0);
        else
            outputPosition[pickPixelCoord] = float4(0.0);

        float3 aoColor = (payload.objectIndex == INVALID_INSTANCE)
            ? payload.emission
            : float3(payload.emission.r);

        float historySamples = 0.0;
        if (pushConstants.frame > 0 && pushConstants.isMoving == 0)
            historySamples = max(0.0, outputAdaptiveState[pixelCoord].z);

        float3 accumulatedAoColor = aoColor;
        if (historySamples > 0.0) {
            float3 previousColor = outputColor[pixelCoord].rgb;
            accumulatedAoColor = accumulateWeightedColor(previousColor, historySamples, aoColor, 1.0);
        }

        outputColor[pixelCoord] = float4(accumulatedAoColor, 1.0);
        outputAlbedo[pixelCoord] = float4(payload.albedo, 1.0);
        outputNormal[pixelCoord] = float4(payload.normal, 0.0);
        outputAdaptiveState[pixelCoord] = float4(0.0, 0.0, historySamples + 1.0, 0.0);
    }
};

#endif // AO_INTEGRATOR_SLANG
