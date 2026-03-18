#ifndef DIRECTLIGHTING_INTEGRATOR_SLANG
#define DIRECTLIGHTING_INTEGRATOR_SLANG

#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Geometry/Intersection.slang"
#include "../Geometry/SurfaceData.slang"
#include "../Core/AdaptiveSampling.slang"
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

    void shadeMiss(float3 worldRayDirection, inout Payload payload)
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
        inout Payload payload)
    {
        payload.position = worldPosition;
        payload.lastBsdfPdf = 0.0;
        payload.lastNeeLightPdf = 0.0;

        float3 viewDir = fastNormalize(-worldRayDirection);
        float3 geometricFacingNormal = computeFacingGeometricNormal(geometricNormal, viewDir);

        TMaterial materialModel;
        MaterialSample materialSample = materialModel.sample(material, interpolatedUV);
        float opacity = materialSample.opacity;

        if (rand(payload.rngState) > opacity) {
            payload.flags |= uint(BounceType.RAY_TRANSPARENT);
            payload.nextDirection = worldRayDirection;
            payload.position = worldPosition + worldRayDirection * computeRayOriginBias(worldPosition);
            payload.attenuation = float3(1.0);
            payload.emission = float3(0.0);
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
        roughness = regularizeSecondaryRoughness(roughness, payload.depth);

        float3 shadingFacingNormal = computeFacingShadingNormal(shadingNormalTextured, viewDir, geometricFacingNormal);

        float3 facingNormal = shadingFacingNormal;
        float dielectricF0 = dielectricF0FromSpecular(specular);
        float3 F0 = lerp(float3(dielectricF0), albedo, metallic);
        float swSpecular = computeSpecularSamplingWeight(viewDir, facingNormal, albedo, F0, metallic, roughness);

        payload.albedo = albedo;
        payload.normal = facingNormal * 0.5 + 0.5;
        payload.emission = shadeAmbientOcclusionPbrSurface(
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
            sceneSettings.environment,
            payload.rngState);

        payload.attenuation = float3(1.0);
        payload.nextDirection = worldRayDirection;
        payload.flags |= uint(BounceType.RAY_TERMINATED);
    }

    void primaryRayGen(int2 pixelCoord, int2 screenSize)
    {
        if (pixelCoord.x >= screenSize.x || pixelCoord.y >= screenSize.y)
            return;

        const int2 pickPixelCoord = int2(pixelCoord.x, (screenSize.y - 1) - pixelCoord.y);
        uint2 seed = pcg2d(uint2(pixelCoord) ^ uint2(pushConstants.frame * 16777619));
        uint rngState = seed.x;
        AdaptiveSamplingContext adaptiveContext = loadAdaptiveSamplingContext(pixelCoord);
        bool adaptiveEnabled = adaptiveContext.adaptiveSamplingEnabled;

#ifndef USE_COMPUTE
        if (shouldSkipAdaptiveSampling(adaptiveContext))
            return;
#endif

        R2Sampler sampler;
        SamplerState samplerState;
        sampler.startPixel(pixelCoord, pushConstants.frame, samplerState);
        float3 rayOrigin, rayDirection;
        if (isInteractiveFrame()) {
            PerspectiveCamera<R2Sampler> camera;
            camera.generateRay(
                pixelCoord, screenSize, samplerState, sampler,
                true, rayOrigin, rayDirection);
        } else {
            ThinLensCamera<R2Sampler> camera;
            camera.generateRay(
                pixelCoord, screenSize, samplerState, sampler,
                true, rayOrigin, rayDirection);
        }

        Payload payload;
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

        outputCrypto[pickPixelCoord] = payload.objectIndex;
        if (payload.objectIndex != INVALID_INSTANCE)
            outputPosition[pickPixelCoord] = float4(payload.position, 1.0);
        else
            outputPosition[pickPixelCoord] = float4(0);

        AdaptiveSamplingFrameState adaptiveFrameState = createAdaptiveSamplingFrameState();
        if (adaptiveEnabled)
            updateAdaptiveSamplingState(adaptiveContext, payload.emission, adaptiveFrameState);
        else
            adaptiveFrameState.samplesTaken = 1;

        float newAlpha = float((payload.flags & BounceType.ENV_TRANSPARENT) == 0u);
        float4 finalColorData;
        float4 finalAdaptiveData;
        finalizeAdaptiveSampling(adaptiveContext, adaptiveFrameState, payload.emission, newAlpha, finalColorData, finalAdaptiveData);

        float3 finalAlbedo = newAlpha > 0.0 ? payload.albedo : float3(0.0);
        float3 finalNormal = newAlpha > 0.0 ? payload.normal : float3(0.0);

        outputColor[pixelCoord] = finalColorData;
        outputAlbedo[pixelCoord] = float4(finalAlbedo, 1.0);
        outputNormal[pixelCoord] = float4(finalNormal, 0.0);
        outputAdaptiveState[pixelCoord] = finalAdaptiveData;
    }
};

#endif // DIRECTLIGHTING_INTEGRATOR_SLANG
