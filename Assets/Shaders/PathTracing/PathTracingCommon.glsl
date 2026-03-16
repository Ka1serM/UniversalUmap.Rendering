#ifndef PATHTRACING_COMMON_GLSL
#define PATHTRACING_COMMON_GLSL

#include "../Common.glsl"
#include "../Common/Intersection.glsl"
#include "../Common/RenderPolicy.glsl"
#include "../Common/PrimaryRay.glsl"
#include "../Common/AdaptiveSampling.glsl"
#include "../Common/PayloadUtils.glsl"
#include "../Common/Miss.glsl"
#include "../Common/Brdf.glsl"
#include "../Common/Material.glsl"
#include "../DirectLighting/DirectLightingCommon.glsl"

#ifdef USE_COMPUTE
void traceRayComputePathTracing(vec3 rayOrigin, vec3 rayDirection, float tMin, float tMax, inout Payload payload);
#endif

float fresnelDielectric(float cosThetaI, float etaI, float etaT)
{
    float entering = step(0.0, cosThetaI);
    float newEtaI = mix(etaT, etaI, entering);
    float newEtaT = mix(etaI, etaT, entering);
    cosThetaI = mix(-cosThetaI, cosThetaI, entering);

    float eta = newEtaI / newEtaT;
    float cosThetaI2 = cosThetaI * cosThetaI;
    float sin2ThetaT = eta * eta * max(0.0, 1.0 - cosThetaI2);
    if (sin2ThetaT >= 1.0)
        return 1.0;

    float cosThetaT = sqrt(1.0 - sin2ThetaT);
    float A = newEtaT * cosThetaI;
    float B = newEtaI * cosThetaT;
    float Rs = (A - B) / (A + B);
    float Rp = (A * cosThetaT - B * cosThetaI) / (A * cosThetaT + B * cosThetaI);
    return 0.5 * (Rs * Rs + Rp * Rp);
}

void handleDielectricBSDF(vec3 viewDir, vec3 shadingNormal, float roughness, float ior, vec3 transmissionColor, inout Payload payload)
{
    payload.flags |= BOUNCE_TRANSMIT;

    vec3 shadingFacingNormal = dot(shadingNormal, viewDir) < 0.0 ? -shadingNormal : shadingNormal;
    vec3 halfVector = sampleVisibleHalfVectorGGX(viewDir, shadingFacingNormal, roughness, payload.rngState);
    float VdotH = max(dot(viewDir, halfVector), 0.0);
    vec3 incident = fastNormalize(-viewDir);
    float etaI = 1.0;
    float etaT = ior;

    if (dot(incident, shadingNormal) > 0.0) {
        etaI = ior;
        etaT = 1.0;
    }

    float reflectProbability = max(fresnelDielectric(VdotH, etaI, etaT), EPSILON);
    vec3 refractedDir = refract(incident, halfVector, etaI / etaT);
    bool cannotRefract = dot(refractedDir, refractedDir) < EPSILON;

    if (cannotRefract || rand(payload.rngState) < reflectProbability) {
        vec3 reflectedDir = reflect(-viewDir, halfVector);
        vec3 brdf = evaluateSpecularBRDF(shadingFacingNormal, viewDir, reflectedDir, vec3(reflectProbability), roughness, halfVector);
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
    vec3 viewDir,
    vec3 shadingNormal,
    vec3 geometricNormal,
    vec3 albedo,
    float metallic,
    float specular,
    float roughness,
    inout Payload payload)
{
    float dielectricF0 = dielectricF0FromSpecular(specular);
    vec3 F0 = mix(vec3(dielectricF0), albedo, metallic);
    float swSpecular = computeSpecularSamplingWeight(viewDir, shadingNormal, albedo, F0, metallic, roughness);

    vec3 halfVector;
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
    vec3 F = fresnelSchlick(F0, VdotH);
    vec3 diffuseBRDF = (vec3(1.0) - F) * evaluateDiffuseBRDF(albedo, metallic);
    vec3 specularBRDF = evaluateSpecularBRDF(shadingNormal, viewDir, payload.nextDirection, F, roughness, halfVector);
    vec3 combinedBRDF = diffuseBRDF + specularBRDF;

    float specPdf = sampledSpecular ? pdfSpecular(viewDir, shadingNormal, halfVector, roughness) : 0.0;
    float diffPdf = pdfDiffuse(shadingNormal, payload.nextDirection);
    float combinedPdf = max(swSpecular * specPdf + (1.0 - swSpecular) * diffPdf, EPSILON);

    payload.attenuation = combinedBRDF * max(dot(shadingNormal, payload.nextDirection), 0.0) / combinedPdf;
    payload.lastBsdfPdf = max(combinedPdf, EPSILON);
    payload.lastNeeLightPdf = max(dot(geometricNormal, payload.nextDirection), 0.0) > 0.0 ? (0.5 / PI) : 0.0;
    payload.flags |= sampledSpecular ? BOUNCE_SPECULAR : BOUNCE_DIFFUSE;
}

void shadeClosestHitPathTracing(
    in vec3 worldPosition,
    in vec3 shadowPosition,
    in vec3 shadingNormal,
    in vec3 geometricNormal,
    in vec3 interpolatedTangent,
    in float interpolatedTangentSign,
    in vec2 interpolatedUV,
    in vec3 worldRayDirection,
    in MaterialData material,
    inout Payload payload)
{
    payload.position = worldPosition;
    payload.lastBsdfPdf = 0.0;
    payload.lastNeeLightPdf = 0.0;

    vec3 viewDir = fastNormalize(-worldRayDirection);
    vec3 geometricFacingNormal = (dot(geometricNormal, viewDir) < 0.0) ? -geometricNormal : geometricNormal;

    float opacity = sampleMaterialOpacity(material, interpolatedUV);

    if (rand(payload.rngState) > opacity) {
        payload.flags |= RAY_TRANSPARENT;
        payload.nextDirection = worldRayDirection;
        payload.position = worldPosition + worldRayDirection * computeRayOriginBias(worldPosition);
        payload.attenuation = vec3(1.0);
        payload.emission = vec3(0.0);
        return;
    }

    vec3 albedo = sampleMaterialAlbedo(material, interpolatedUV);

    vec3 shadingNormalTextured = shadingNormal;
    if (material.normalIndex != -1) {
        vec3 packedNormal = texture(textureSamplers[material.normalIndex], interpolatedUV).xyz;
        shadingNormalTextured = applyNormalMap(shadingNormal, interpolatedTangent, interpolatedTangentSign, packedNormal);
    }

    vec3 emission = sampleMaterialEmission(material, interpolatedUV);
    float metallic = sampleMaterialMetallic(material, interpolatedUV);
    float specular = sampleMaterialSpecular(material, interpolatedUV);
    specular = clamp(specular * 2.0, 0.0, 1.0);

    float roughness = sampleMaterialRoughness(material, interpolatedUV);
    roughness = clamp(roughness, 0.02, 1.0);
    roughness = regularizeSecondaryRoughness(roughness, payload.depth);

    float transmission = sampleMaterialTransmission(material, interpolatedUV);

    vec3 shadingFacingNormal = (dot(shadingNormalTextured, viewDir) < 0.0) ? -shadingNormalTextured : shadingNormalTextured;
    shadingFacingNormal = stabilizeShadingNormal(shadingFacingNormal, geometricFacingNormal);
    shadingFacingNormal = adaptShadingNormalLocal(viewDir, shadingFacingNormal, geometricFacingNormal);
    vec3 facingNormal = shadingFacingNormal;
    float dielectricF0 = dielectricF0FromSpecular(specular);
    vec3 F0 = mix(vec3(dielectricF0), albedo, metallic);
    float swSpecular = computeSpecularSamplingWeight(viewDir, facingNormal, albedo, F0, metallic, roughness);

    payload.albedo = albedo;
    payload.normal = facingNormal * 0.5 + 0.5;
    payload.emission = emission;

    float transmissionProbability = clamp(transmission, 0.0, 1.0);
    float opaqueProbability = max(1.0 - transmissionProbability, EPSILON);
    if (rand(payload.rngState) < transmissionProbability) {
        handleDielectricBSDF(viewDir, facingNormal, roughness, material.ior, material.transmissionColor, payload);
        payload.attenuation /= max(transmissionProbability, EPSILON);
    } else {
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

void primaryRayGenPathTracing(ivec2 pixelCoord, ivec2 screenSize)
{
    if (pixelCoord.x >= screenSize.x || pixelCoord.y >= screenSize.y)
        return;

    const ivec2 pickPixelCoord = ivec2(pixelCoord.x, (screenSize.y - 1) - pixelCoord.y);

#ifdef USE_COMPUTE
    Payload payload;
#endif

    uvec2 seed = pcg2d(uvec2(pixelCoord) ^ uvec2(pushConstants.frame * 16777619));
    uint rngState = seed.x;
    AdaptiveSamplingContext adaptiveContext = loadAdaptiveSamplingContext(pixelCoord);
    vec3 temporalReference = pushConstants.frame > 0
        ? adaptiveContext.prevColorData.rgb / max(adaptiveContext.exposureScale, EPSILON)
        : vec3(0.0);
    int russianRouletteStartBounce = effectiveRussianRouletteStartBounce();
    int maxBounces = effectiveMaxBounceCount();
    int sampleCount = effectivePrimarySampleCount();
    bool adaptiveEnabled = adaptiveContext.adaptiveSamplingEnabled;
    bool canReuseStablePrimary = isInteractiveFrame() || (sceneSettings.camera.aperture <= 0.0);
    Payload stablePrimaryPayload;

    if (shouldSkipAdaptiveSampling(adaptiveContext))
        return;

    vec3 accumulatedColor = vec3(0.0);
    bool hitAnything = false;
    vec3 stableAlbedo = vec3(0.0);
    vec3 stableNormal = vec3(0.0);
    AdaptiveSamplingFrameState adaptiveFrameState = createAdaptiveSamplingFrameState();

    for (int sampleIndex = 0; sampleIndex < sampleCount; ++sampleIndex) {
        if (sampleIndex == 0) {
            SamplerState stableSamplerState = initSamplerState(pixelCoord, pushConstants.frame + sampleIndex);
            vec3 stableRayOrigin, stableRayDirection;
            generatePrimaryRay(pixelCoord, screenSize, sceneSettings.camera, stableSamplerState, true, false, stableRayOrigin, stableRayDirection);

            initializePayload(stablePrimaryPayload, rngState, 0u);

#ifdef USE_COMPUTE
            traceRayComputePathTracing(stableRayOrigin, stableRayDirection, 0.001, 1000.0, stablePrimaryPayload);
#else
            payload = stablePrimaryPayload;
            traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xff, 0, 0, 0, stableRayOrigin, 0.001, stableRayDirection, 1000.0, 0);
            stablePrimaryPayload = payload;
#endif

            imageStore(outputCrypto, pickPixelCoord, uvec4(stablePrimaryPayload.objectIndex, 0, 0, 0));
            if (stablePrimaryPayload.objectIndex != INVALID_INSTANCE)
                imageStore(outputPosition, pickPixelCoord, vec4(stablePrimaryPayload.position, 1.0));
            else
                imageStore(outputPosition, pickPixelCoord, vec4(0));

            bool stablePrimaryVisible = ((stablePrimaryPayload.flags & ENV_TRANSPARENT) == 0u);
            stableAlbedo = stablePrimaryVisible ? stablePrimaryPayload.albedo : vec3(0.0);
            stableNormal = stablePrimaryVisible ? stablePrimaryPayload.normal : vec3(0.0);
        }

        SamplerState samplerState = initSamplerState(pixelCoord, pushConstants.frame + sampleIndex);
        bool deterministicSample = (sampleIndex == 0);
        bool useThinLens = !isInteractiveFrame();

        vec3 rayOrigin, rayDirection;
        generatePrimaryRay(pixelCoord, screenSize, sceneSettings.camera, samplerState, deterministicSample, useThinLens, rayOrigin, rayDirection);

        vec3 throughput = vec3(1.0);
        vec3 sampleRadiance = vec3(0.0);
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
                initializePayload(payload, rngState, uint(bounce));
#ifdef USE_COMPUTE
                traceRayComputePathTracing(rayOrigin, rayDirection, 0.001, 1000.0, payload);
#else
                traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xff, 0, 0, 0, rayOrigin, 0.001, rayDirection, 1000.0, 0);
#endif
            }

            rayOrigin = payload.position;
            rngState = payload.rngState;

            if ((payload.flags & BOUNCE_DIFFUSE) != 0u)
                diffuseCount++;
            if ((payload.flags & BOUNCE_SPECULAR) != 0u)
                specularCount++;
            if ((payload.flags & BOUNCE_TRANSMIT) != 0u)
                transmissionCount++;

            if (diffuseCount > sceneSettings.renderSettings.diffuseBounces ||
                specularCount > sceneSettings.renderSettings.specularBounces ||
                transmissionCount > sceneSettings.renderSettings.transmissionBounces)
                payload.flags |= RAY_TERMINATED;

            if (bounce == 0) {
                if ((payload.flags & RAY_TRANSPARENT) != 0u) {
                    if ((payload.flags & RAY_TERMINATED) == 0u)
                        --bounce;
                    continue;
                }

                hitAnything = ((payload.flags & ENV_TRANSPARENT) == 0u);
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
                    payload.flags |= RAY_TERMINATED;
                else
                    throughput /= russianRouletteSurvivalProbability;
            }

            if ((payload.flags & RAY_TERMINATED) != 0u)
                break;
        }

        vec3 runningReference = temporalReference;
        if (sampleIndex > 0)
            runningReference = max(runningReference, accumulatedColor / float(sampleIndex));

        int bounceCount = diffuseCount + specularCount + transmissionCount;
        vec3 filteredRadiance = suppressFireflies(
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
    vec4 finalColorData;
    vec4 finalAdaptiveData;
    finalizeAdaptiveSampling(adaptiveContext, adaptiveFrameState, accumulatedColor, newAlpha, finalColorData, finalAdaptiveData);

    vec3 finalAlbedo = stableAlbedo;
    vec3 finalNormal = stableNormal;

    imageStore(outputColor, pixelCoord, finalColorData);
    imageStore(outputAlbedo, pixelCoord, vec4(finalAlbedo, 1.0));
    imageStore(outputNormal, pixelCoord, vec4(finalNormal, 0.0));
    imageStore(outputAdaptiveState, pixelCoord, finalAdaptiveData);
}

#endif // PATHTRACING_COMMON_GLSL
