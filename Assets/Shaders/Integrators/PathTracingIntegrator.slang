#ifndef PATHTRACING_INTEGRATOR_SLANG
#define PATHTRACING_INTEGRATOR_SLANG

#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Geometry/Intersection.slang"
#include "../Geometry/SurfaceData.slang"
#include "../Core/RenderPolicy.slang"
#include "../Core/Accumulation.slang"
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
    payload.Flags |= uint(BounceType.BOUNCE_TRANSMIT);

    float3 shadingFacingNormal = dot(shadingNormal, viewDir) < 0.0 ? -shadingNormal : shadingNormal;
    float3 halfVector = sampleVisibleHalfVectorGGX(viewDir, shadingFacingNormal, roughness, payload.RngState);
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

    if (cannotRefract || rand(payload.RngState) < reflectProbability) {
        float3 reflectedDir = reflect(-viewDir, halfVector);
        float3 brdf = evaluateSpecularBRDF(shadingFacingNormal, viewDir, reflectedDir, float3(reflectProbability), roughness, halfVector);
        float pdf = max(pdfSpecular(viewDir, shadingFacingNormal, halfVector, roughness), EPSILON);

        payload.Attenuation = (brdf * max(dot(shadingFacingNormal, reflectedDir), 0.0)) / max(pdf * reflectProbability, EPSILON);
        payload.NextDirection = reflectedDir;
        payload.LastBsdfPdf = pdf;
        payload.LastNeeLightPdf = max(dot(shadingFacingNormal, reflectedDir), 0.0) > 0.0 ? (0.5 / PI) : 0.0;
    } else {
        payload.Attenuation = transmissionColor / max(1.0 - reflectProbability, EPSILON);
        payload.NextDirection = refractedDir;
        payload.LastBsdfPdf = 0.0;
        payload.LastNeeLightPdf = 0.0;
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
    bool sampledSpecular = rand(payload.RngState) < swSpecular;
    if (sampledSpecular) {
        halfVector = sampleVisibleHalfVectorGGX(viewDir, shadingNormal, roughness, payload.RngState);
        payload.NextDirection = reflect(-viewDir, halfVector);
        for (int i = 0; i < 2 && dot(geometricNormal, payload.NextDirection) <= 0.0; ++i) {
            halfVector = sampleVisibleHalfVectorGGX(viewDir, shadingNormal, roughness, payload.RngState);
            payload.NextDirection = reflect(-viewDir, halfVector);
        }
    } else {
        payload.NextDirection = sampleDiffuse(shadingNormal, payload.RngState);
        halfVector = fastNormalize(viewDir + payload.NextDirection);
    }

    if (dot(geometricNormal, payload.NextDirection) <= 0.0) {
        payload.NextDirection = sampleDiffuse(geometricNormal, payload.RngState);
        halfVector = fastNormalize(viewDir + payload.NextDirection);
        sampledSpecular = false;
    }

    float VdotH = max(dot(viewDir, halfVector), 0.0);
    float NdotL = max(dot(shadingNormal, payload.NextDirection), 0.0);
    float NdotV = max(dot(shadingNormal, viewDir), 0.0);
    float VdotL = dot(viewDir, payload.NextDirection);
    float3 F = fresnelSchlick(F0, VdotH);
    float3 diffuseBRDF = (float3(1.0) - F) * evaluateDiffuseBRDF(albedo, metallic, roughness, NdotL, NdotV, VdotL);
    float3 specularBRDF = evaluateSpecularBRDF(shadingNormal, viewDir, payload.NextDirection, F, roughness, halfVector);
    float3 combinedBRDF = diffuseBRDF + specularBRDF;

    float specPdf = sampledSpecular ? pdfSpecular(viewDir, shadingNormal, halfVector, roughness) : 0.0;
    float diffPdf = pdfDiffuse(shadingNormal, payload.NextDirection);
    float combinedPdf = max(swSpecular * specPdf + (1.0 - swSpecular) * diffPdf, EPSILON);

    payload.Attenuation = combinedBRDF * NdotL / combinedPdf;
    payload.LastBsdfPdf = max(combinedPdf, EPSILON);
    payload.LastNeeLightPdf = max(dot(geometricNormal, payload.NextDirection), 0.0) > 0.0 ? (0.5 / PI) : 0.0;
    payload.Flags |= sampledSpecular ? uint(BounceType.BOUNCE_SPECULAR) : uint(BounceType.BOUNCE_DIFFUSE);
}

struct PathTracingIntegrator<TMaterial> : IIntegrator<Payload>
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
        shadeMiss(worldRayDirection, sceneSettings.Environment, sceneSettings.RenderSettings, payload);
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

        float transmission = materialSample.transmission;

        float3 shadingFacingNormal = computeFacingShadingNormal(shadingNormalTextured, viewDir, geometricFacingNormal);
        float3 facingNormal = shadingFacingNormal;
        payload.Albedo = albedo;
        payload.Normal = facingNormal * 0.5 + 0.5;
        payload.Emission = emission;

        float transmissionProbability = clamp(transmission, 0.0, 1.0);
        float opaqueProbability = max(1.0 - transmissionProbability, EPSILON);
        if (rand(payload.RngState) < transmissionProbability) {
            handleDielectricBSDF(viewDir, facingNormal, roughness, material.Ior, material.TransmissionColor, payload);
            payload.Attenuation /= max(transmissionProbability, EPSILON);
        } else {
            float dielectricF0 = dielectricF0FromSpecular(specular);
            float3 F0 = lerp(float3(dielectricF0), albedo, metallic);
            float swSpecular = computeSpecularSamplingWeight(viewDir, facingNormal, albedo, F0, metallic, roughness);

            payload.Emission += estimateDirectLighting(
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
                sceneSettings.Environment,
                payload.RngState
            );
            handleOpaqueBSDF(viewDir, facingNormal, geometricFacingNormal, albedo, metallic, specular, roughness, payload);
            payload.Attenuation /= opaqueProbability;
        }

        payload.NextDirection = fastNormalize(payload.NextDirection);
        payload.Position = offsetRayOrigin(worldPosition, geometricFacingNormal, payload.NextDirection);
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

        Payload payload;

        uint2 seed = pcg2d(uint2(blockCenterPixel) ^ uint2(pushConstants.Frame * 16777619));
        uint rngState = seed.x;
        float exposureScale = loadExposureScale();
        bool interactivePreview = useInteractivePathTracingPreview();
        int russianRouletteStartBounce = interactivePreview ? 1 : effectiveRussianRouletteStartBounce();
        int maxBounces = interactivePreview ? min(effectiveMaxBounceCount(), 2) : effectiveMaxBounceCount();
        int sampleCount = interactivePreview ? 1 : effectivePrimarySampleCount();
        bool useThinLens = !isInteractiveFrame();
        bool canReuseStablePrimary = isInteractiveFrame() || (pushConstants.Camera.Aperture <= 0.0);
        Payload stablePrimaryPayload;

        float3 accumulatedColor = float3(0.0);
        int samplesTaken = 0;
        bool hitAnything = false;
        float3 stableAlbedo = float3(0.0);
        float3 stableNormal = float3(0.0);

        for (int sampleIndex = 0; sampleIndex < sampleCount; ++sampleIndex) {
            if (sampleIndex == 0) {
                HaltonSampler stableSampler;
                SamplerState stableSamplerState;
                stableSampler.startPixel(blockCenterPixel, pushConstants.Frame + sampleIndex, stableSamplerState);
                float3 stableRayOrigin, stableRayDirection;
                PerspectiveCamera<HaltonSampler> stableCamera;
                stableCamera.generateRay(
                    blockCenterPixel,
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

                bool stablePrimaryVisible = ((stablePrimaryPayload.Flags & uint(BounceType.ENV_TRANSPARENT)) == 0u);
                stableAlbedo = stablePrimaryVisible ? stablePrimaryPayload.Albedo : float3(0.0);
                stableNormal = stablePrimaryVisible ? stablePrimaryPayload.Normal : float3(0.0);
            }

            HaltonSampler sampler;
            SamplerState samplerState;
            sampler.startPixel(blockCenterPixel, pushConstants.Frame + sampleIndex, samplerState);
            bool deterministicSample = (sampleIndex == 0);
            bool reuseStablePrimaryForSample = deterministicSample && canReuseStablePrimary;

            float3 rayOrigin, rayDirection;
            if (!reuseStablePrimaryForSample) {
                if (useThinLens) {
                    ThinLensCamera<HaltonSampler> sceneCamera;
                    sceneCamera.generateRay(
                        blockCenterPixel,
                        screenSize,
                        samplerState,
                        sampler,
                        deterministicSample,
                        rayOrigin,
                        rayDirection);
                } else {
                    PerspectiveCamera<HaltonSampler> sceneCamera;
                    sceneCamera.generateRay(
                        blockCenterPixel,
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

                rayOrigin = payload.Position;
                rngState = payload.RngState;

                if ((payload.Flags & uint(BounceType.BOUNCE_DIFFUSE)) != 0u)
                    diffuseCount++;
                if ((payload.Flags & uint(BounceType.BOUNCE_SPECULAR)) != 0u)
                    specularCount++;
                if ((payload.Flags & uint(BounceType.BOUNCE_TRANSMIT)) != 0u)
                    transmissionCount++;

                if (diffuseCount > sceneSettings.RenderSettings.DiffuseBounces ||
                    specularCount > sceneSettings.RenderSettings.SpecularBounces ||
                    transmissionCount > sceneSettings.RenderSettings.TransmissionBounces)
                    payload.Flags |= uint(BounceType.RAY_TERMINATED);

                if (bounce == 0) {
                    if ((payload.Flags & BounceType.RAY_TRANSPARENT) != 0u) {
                        if ((payload.Flags & BounceType.RAY_TERMINATED) == 0u)
                            --bounce;
                        continue;
                    }

                    hitAnything = ((payload.Flags & BounceType.ENV_TRANSPARENT) == 0u);
                }

                sampleRadiance += throughput * payload.Emission;
                throughput *= payload.Attenuation;
                rayDirection = payload.NextDirection;

                float russianRouletteSurvivalProbability = computeRussianRouletteSurvivalProbability(
                    throughput,
                    bounce,
                    russianRouletteStartBounce);
                if (russianRouletteSurvivalProbability < 1.0) {
                    if (rand(payload.RngState) > russianRouletteSurvivalProbability)
                        payload.Flags |= uint(BounceType.RAY_TERMINATED);
                    else
                        throughput /= russianRouletteSurvivalProbability;
                }

                if ((payload.Flags & BounceType.RAY_TERMINATED) != 0u)
                    break;
            }

            float3 runningReference = float3(0.0);
            if (!interactivePreview && samplesTaken > 0)
                runningReference = accumulatedColor / float(samplesTaken);

            int bounceCount = diffuseCount + specularCount + transmissionCount;
            float3 filteredRadiance = suppressFireflies(
                sampleRadiance,
                runningReference,
                bounceCount,
                diffuseCount,
                specularCount,
                transmissionCount);
            accumulatedColor += filteredRadiance;
            samplesTaken++;
        }

        float3 finalAlbedo = stableAlbedo;
        float3 finalNormal = stableNormal;
        float newAlpha = float(hitAnything);
        float sampleWeight = float(max(samplesTaken, 1));

        for (int y = blockStart.y; y < blockEnd.y; ++y) {
            for (int x = blockStart.x; x < blockEnd.x; ++x) {
                int2 outputPixel = int2(x, y);
                int2 pickPixelCoord = int2(outputPixel.x, (screenSize.y - 1) - outputPixel.y);
                float historySamples = loadHistorySamples(outputPixel);

                outputCrypto[pickPixelCoord] = stablePrimaryPayload.ObjectIndex;
                if (stablePrimaryPayload.ObjectIndex != InvalidInstance)
                    outputPosition[pickPixelCoord] = float4(stablePrimaryPayload.Position, 1.0);
                else
                    outputPosition[pickPixelCoord] = float4(0);

                storeAccumulatedFrame(
                    outputPixel,
                    historySamples,
                    (accumulatedColor / sampleWeight) * exposureScale,
                    newAlpha,
                    sampleWeight);
                outputAlbedo[outputPixel] = float4(finalAlbedo, 1.0);
                outputNormal[outputPixel] = float4(finalNormal, 0.0);
            }
        }
    }
};

#endif // PATHTRACING_INTEGRATOR_SLANG
