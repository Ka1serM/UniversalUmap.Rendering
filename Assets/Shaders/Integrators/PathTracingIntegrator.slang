#ifndef PATHTRACING_INTEGRATOR_SLANG
#define PATHTRACING_INTEGRATOR_SLANG

#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Geometry/Intersection.slang"
#include "../Geometry/SurfaceData.slang"
#include "../Core/RenderPolicy.slang"
#include "../Core/AdaptiveSampling.slang"
#include "../Core/Payload.slang"
#include "../DirectLighting/DirectLighting.slang"
#include "../Bxdfs/Brdf.slang"
#include "../Cameras/PerspectiveCamera.slang"
#include "../Cameras/ThinLensCamera.slang"
#include "../Samplers/HaltonSampler.slang"
#include "../Materials/IMaterial.slang"
#include "../Lights/EnvironmentLight.slang"
#include "IIntegrator.slang"

void handleDielectricBSDF(float3 viewDir, float3 shadingNormal, float roughness, float ior, float3 transmissionColor, inout Payload payload)
{
    payload.flags |= uint(BounceType.BOUNCE_TRANSMIT);

    float3 shadingFacingNormal = dot(shadingNormal, viewDir) < 0.0 ? -shadingNormal : shadingNormal;
    float3 halfVector = sampleVisibleHalfVectorGGX(viewDir, shadingFacingNormal, roughness, payload.rngState);
    float VdotH = max(dot(viewDir, halfVector), 0.0);
    float3 incident = fastNormalize(-viewDir);
    float etaI = 1.0;
    float etaT = ior;

    if (dot(incident, shadingNormal) > 0.0) {
        etaI = ior;
        etaT = 1.0;
    }

    float reflectProbability = max(fresnelDielectric(VdotH, etaI, etaT), EPSILON);
    float3 refractedDir = refract(incident, halfVector, etaI / etaT);
    bool cannotRefract = dot(refractedDir, refractedDir) < EPSILON;

    if (cannotRefract || rand(payload.rngState) < reflectProbability) {
        float3 reflectedDir = reflect(-viewDir, halfVector);
        float3 brdf = evaluateSpecularBRDF(shadingFacingNormal, viewDir, reflectedDir, float3(reflectProbability), roughness, halfVector);
        float pdf = max(pdfSpecular(viewDir, shadingFacingNormal, halfVector, roughness), EPSILON);

        payload.attenuation = (brdf * max(dot(shadingFacingNormal, reflectedDir), 0.0)) / max(pdf * reflectProbability, EPSILON);
        payload.nextDirection = reflectedDir;
        payload.lastBsdfPdf = pdf;
        payload.lastNeeLightPdf = max(dot(shadingFacingNormal, reflectedDir), 0.0) > 0.0 ? (0.5 / PI) : 0.0;
    } else {
        payload.attenuation = transmissionColor / max(1.0 - reflectProbability, EPSILON);
        payload.nextDirection = refractedDir;
        payload.lastBsdfPdf = 0.0;
        payload.lastNeeLightPdf = 0.0;
    }
}

void handleOpaqueBSDF(
    float3 viewDir,
    float3 shadingNormal,
    float3 geometricNormal,
    float3 albedo,
    float metallic,
    float specular,
    float roughness,
    inout Payload payload)
{
    float dielectricF0 = dielectricF0FromSpecular(specular);
    float3 F0 = lerp(float3(dielectricF0), albedo, metallic);
    float swSpecular = computeSpecularSamplingWeight(viewDir, shadingNormal, albedo, F0, metallic, roughness);

    float3 halfVector;
    bool sampledSpecular = rand(payload.rngState) < swSpecular;
    if (sampledSpecular) {
        halfVector = sampleVisibleHalfVectorGGX(viewDir, shadingNormal, roughness, payload.rngState);
        payload.nextDirection = reflect(-viewDir, halfVector);
        for (int i = 0; i < 2 && dot(geometricNormal, payload.nextDirection) <= 0.0; ++i) {
            halfVector = sampleVisibleHalfVectorGGX(viewDir, shadingNormal, roughness, payload.rngState);
            payload.nextDirection = reflect(-viewDir, halfVector);
        }
    } else {
        payload.nextDirection = sampleDiffuse(shadingNormal, payload.rngState);
        halfVector = fastNormalize(viewDir + payload.nextDirection);
    }

    if (dot(geometricNormal, payload.nextDirection) <= 0.0) {
        payload.nextDirection = sampleDiffuse(geometricNormal, payload.rngState);
        halfVector = fastNormalize(viewDir + payload.nextDirection);
        sampledSpecular = false;
    }

    float VdotH = max(dot(viewDir, halfVector), 0.0);
    float NdotL = max(dot(shadingNormal, payload.nextDirection), 0.0);
    float NdotV = max(dot(shadingNormal, viewDir), 0.0);
    float VdotL = dot(viewDir, payload.nextDirection);
    float3 F = fresnelSchlick(F0, VdotH);
    float3 diffuseBRDF = (float3(1.0) - F) * evaluateDiffuseBRDF(albedo, metallic, roughness, NdotL, NdotV, VdotL);
    float3 specularBRDF = evaluateSpecularBRDF(shadingNormal, viewDir, payload.nextDirection, F, roughness, halfVector);
    float3 combinedBRDF = diffuseBRDF + specularBRDF;

    float specPdf = sampledSpecular ? pdfSpecular(viewDir, shadingNormal, halfVector, roughness) : 0.0;
    float diffPdf = pdfDiffuse(shadingNormal, payload.nextDirection);
    float combinedPdf = max(swSpecular * specPdf + (1.0 - swSpecular) * diffPdf, EPSILON);

    payload.attenuation = combinedBRDF * NdotL / combinedPdf;
    payload.lastBsdfPdf = max(combinedPdf, EPSILON);
    payload.lastNeeLightPdf = max(dot(geometricNormal, payload.nextDirection), 0.0) > 0.0 ? (0.5 / PI) : 0.0;
    payload.flags |= sampledSpecular ? uint(BounceType.BOUNCE_SPECULAR) : uint(BounceType.BOUNCE_DIFFUSE);
}

struct PathTracingIntegrator<TMaterial> : IIntegrator<Payload>
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
        shadeMiss(worldRayDirection, sceneSettings.environment, sceneSettings.renderSettings, payload);
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

        float transmission = materialSample.transmission;

        float3 shadingFacingNormal = computeFacingShadingNormal(shadingNormalTextured, viewDir, geometricFacingNormal);
        float3 facingNormal = shadingFacingNormal;
        payload.albedo = albedo;
        payload.normal = facingNormal * 0.5 + 0.5;
        payload.emission = emission;

        float transmissionProbability = clamp(transmission, 0.0, 1.0);
        float opaqueProbability = max(1.0 - transmissionProbability, EPSILON);
        if (rand(payload.rngState) < transmissionProbability) {
            handleDielectricBSDF(viewDir, facingNormal, roughness, material.ior, material.transmissionColor, payload);
            payload.attenuation /= max(transmissionProbability, EPSILON);
        } else {
            float dielectricF0 = dielectricF0FromSpecular(specular);
            float3 F0 = lerp(float3(dielectricF0), albedo, metallic);
            float swSpecular = computeSpecularSamplingWeight(viewDir, facingNormal, albedo, F0, metallic, roughness);

            payload.emission += estimateDirectLighting(
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
                sceneSettings.environment,
                payload.rngState
            );
            handleOpaqueBSDF(viewDir, facingNormal, geometricFacingNormal, albedo, metallic, specular, roughness, payload);
            payload.attenuation /= opaqueProbability;
        }

        payload.nextDirection = fastNormalize(payload.nextDirection);
        payload.position = offsetRayOrigin(worldPosition, geometricFacingNormal, payload.nextDirection);
    }

    void primaryRayGen(int2 pixelCoord, int2 screenSize)
    {
        if (pixelCoord.x >= screenSize.x || pixelCoord.y >= screenSize.y)
            return;

        const int2 pickPixelCoord = int2(pixelCoord.x, (screenSize.y - 1) - pixelCoord.y);

        Payload payload;

        uint2 seed = pcg2d(uint2(pixelCoord) ^ uint2(pushConstants.frame * 16777619));
        uint rngState = seed.x;
        AdaptiveSamplingContext adaptiveContext = loadAdaptiveSamplingContext(pixelCoord);
        float3 temporalReference = pushConstants.frame > 0
            ? adaptiveContext.prevColorData.rgb / max(adaptiveContext.exposureScale, EPSILON)
            : float3(0.0);
        int russianRouletteStartBounce = effectiveRussianRouletteStartBounce();
        int maxBounces = effectiveMaxBounceCount();
        int sampleCount = effectivePrimarySampleCount();
        bool adaptiveEnabled = adaptiveContext.adaptiveSamplingEnabled;
        bool useThinLens = !isInteractiveFrame();
        bool canReuseStablePrimary = isInteractiveFrame() || (sceneSettings.camera.aperture <= 0.0);
        Payload stablePrimaryPayload;

        if (shouldSkipAdaptiveSampling(adaptiveContext))
            return;

        float3 accumulatedColor = float3(0.0);
        bool hitAnything = false;
        float3 stableAlbedo = float3(0.0);
        float3 stableNormal = float3(0.0);
        AdaptiveSamplingFrameState adaptiveFrameState = createAdaptiveSamplingFrameState();

        for (int sampleIndex = 0; sampleIndex < sampleCount; ++sampleIndex) {
            if (sampleIndex == 0) {
                HaltonSampler stableSampler;
                SamplerState stableSamplerState;
                stableSampler.startPixel(pixelCoord, pushConstants.frame + sampleIndex, stableSamplerState);
                float3 stableRayOrigin, stableRayDirection;
                PerspectiveCamera<HaltonSampler> stableCamera;
                stableCamera.generateRay(
                    pixelCoord,
                    screenSize,
                    stableSamplerState,
                    stableSampler,
                    true,
                    stableRayOrigin,
                    stableRayDirection);

                stablePrimaryPayload.initialize(rngState, 0u);

#ifdef USE_COMPUTE
                traceRayCompute(stableRayOrigin, stableRayDirection, 0.001, 1000.0, stablePrimaryPayload);
#else
                payload = stablePrimaryPayload;
                RayDesc stablePrimaryRay;
                stablePrimaryRay.Origin = stableRayOrigin;
                stablePrimaryRay.Direction = stableRayDirection;
                stablePrimaryRay.TMin = 0.001;
                stablePrimaryRay.TMax = 1000.0;
                TraceRay(topLevelAS, RAY_FLAG_FORCE_OPAQUE, 0xff, 0, 0, 0, stablePrimaryRay, payload);
                stablePrimaryPayload = payload;
#endif

                outputCrypto[pickPixelCoord] = stablePrimaryPayload.objectIndex;
                if (stablePrimaryPayload.objectIndex != INVALID_INSTANCE)
                    outputPosition[pickPixelCoord] = float4(stablePrimaryPayload.position, 1.0);
                else
                    outputPosition[pickPixelCoord] = float4(0);
                bool stablePrimaryVisible = ((stablePrimaryPayload.flags & uint(BounceType.ENV_TRANSPARENT)) == 0u);
                stableAlbedo = stablePrimaryVisible ? stablePrimaryPayload.albedo : float3(0.0);
                stableNormal = stablePrimaryVisible ? stablePrimaryPayload.normal : float3(0.0);
            }

            HaltonSampler sampler;
            SamplerState samplerState;
            sampler.startPixel(pixelCoord, pushConstants.frame + sampleIndex, samplerState);
            bool deterministicSample = (sampleIndex == 0);
            bool reuseStablePrimaryForSample = deterministicSample && canReuseStablePrimary;

            float3 rayOrigin, rayDirection;
            if (!reuseStablePrimaryForSample) {
                if (useThinLens) {
                    ThinLensCamera<HaltonSampler> sceneCamera;
                    sceneCamera.generateRay(
                        pixelCoord,
                        screenSize,
                        samplerState,
                        sampler,
                        deterministicSample,
                        rayOrigin,
                        rayDirection);
                } else {
                    PerspectiveCamera<HaltonSampler> sceneCamera;
                    sceneCamera.generateRay(
                        pixelCoord,
                        screenSize,
                        samplerState,
                        sampler,
                        deterministicSample,
                        rayOrigin,
                        rayDirection);
                }
            }

            float3 throughput = float3(1.0);
            float3 sampleRadiance = float3(0.0);
            int diffuseCount = 0;
            int specularCount = 0;
            int transmissionCount = 0;

            for (int bounce = 0; bounce < maxBounces; ++bounce) {
                bool reusePrimaryPayload = (sampleIndex == 0) && canReuseStablePrimary && (bounce == 0);
                if (reusePrimaryPayload) {
#ifndef USE_COMPUTE
                    payload = stablePrimaryPayload;
#endif
                } else {
                    payload.initialize(rngState, uint(bounce));
#ifdef USE_COMPUTE
                    traceRayCompute(rayOrigin, rayDirection, 0.001, 1000.0, payload);
#else
                    RayDesc pathRay;
                    pathRay.Origin = rayOrigin;
                    pathRay.Direction = rayDirection;
                    pathRay.TMin = 0.001;
                    pathRay.TMax = 1000.0;
                    TraceRay(topLevelAS, RAY_FLAG_FORCE_OPAQUE, 0xff, 0, 0, 0, pathRay, payload);
#endif
                }

                rayOrigin = payload.position;
                rngState = payload.rngState;

                if ((payload.flags & uint(BounceType.BOUNCE_DIFFUSE)) != 0u)
                    diffuseCount++;
                if ((payload.flags & uint(BounceType.BOUNCE_SPECULAR)) != 0u)
                    specularCount++;
                if ((payload.flags & uint(BounceType.BOUNCE_TRANSMIT)) != 0u)
                    transmissionCount++;

                if (diffuseCount > sceneSettings.renderSettings.diffuseBounces ||
                    specularCount > sceneSettings.renderSettings.specularBounces ||
                    transmissionCount > sceneSettings.renderSettings.transmissionBounces)
                    payload.flags |= uint(BounceType.RAY_TERMINATED);

                if (bounce == 0) {
                    if ((payload.flags & BounceType.RAY_TRANSPARENT) != 0u) {
                        if ((payload.flags & BounceType.RAY_TERMINATED) == 0u)
                            --bounce;
                        continue;
                    }

                    hitAnything = ((payload.flags & BounceType.ENV_TRANSPARENT) == 0u);
                }

                sampleRadiance += throughput * payload.emission;
                throughput *= payload.attenuation;
                rayDirection = payload.nextDirection;

                float russianRouletteSurvivalProbability = computeRussianRouletteSurvivalProbability(
                    throughput,
                    bounce,
                    russianRouletteStartBounce);
                if (russianRouletteSurvivalProbability < 1.0) {
                    if (rand(payload.rngState) > russianRouletteSurvivalProbability)
                        payload.flags |= uint(BounceType.RAY_TERMINATED);
                    else
                        throughput /= russianRouletteSurvivalProbability;
                }

                if ((payload.flags & BounceType.RAY_TERMINATED) != 0u)
                    break;
            }

            float3 runningReference = temporalReference;
            if (sampleIndex > 0)
                runningReference = max(runningReference, accumulatedColor / float(sampleIndex));

            int bounceCount = diffuseCount + specularCount + transmissionCount;
            float3 filteredRadiance = suppressFireflies(
                sampleRadiance,
                runningReference,
                bounceCount,
                diffuseCount,
                specularCount,
                transmissionCount);
            accumulatedColor += filteredRadiance;
            updateAdaptiveSamplingState(adaptiveContext, filteredRadiance, adaptiveFrameState);
            if (adaptiveEnabled && shouldTerminateAdaptiveSampling(adaptiveContext, adaptiveFrameState))
                break;
        }

        float newAlpha = float(hitAnything);
        float4 finalColorData;
        float4 finalAdaptiveData;
        finalizeAdaptiveSampling(adaptiveContext, adaptiveFrameState, accumulatedColor, newAlpha, finalColorData, finalAdaptiveData);

        float3 finalAlbedo = stableAlbedo;
        float3 finalNormal = stableNormal;

        outputColor[pixelCoord] = finalColorData;
        outputAlbedo[pixelCoord] = float4(finalAlbedo, 1.0);
        outputNormal[pixelCoord] = float4(finalNormal, 0.0);
        outputAdaptiveState[pixelCoord] = finalAdaptiveData;
    }
};

#endif // PATHTRACING_INTEGRATOR_SLANG
