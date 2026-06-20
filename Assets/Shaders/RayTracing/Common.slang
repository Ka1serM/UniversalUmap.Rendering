#ifndef RAYTRACING_COMMON_SLANG
#define RAYTRACING_COMMON_SLANG

#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Core/RenderPolicy.slang"
#include "../Core/Accumulation.slang"
#include "../Core/Payload.slang"
#include "../Geometry/Intersection.slang"
#include "../Geometry/SurfaceData.slang"
#include "../Bxdfs/Brdf.slang"
#include "../Bxdfs/PbrBxdf.slang"
#include "../Lights/DirectionalLight.slang"
#include "../Cameras/PerspectiveCamera.slang"
#include "../Cameras/ThinLensCamera.slang"
#include "../Cameras/OrthographicCamera.slang"
#include "../Cameras/FisheyeCamera.slang"
#include "../Samplers/R2Sampler.slang"
#include "../Materials/PbrMaterial.slang"
#include "../Lights/EnvironmentLight.slang"

[[vk::binding(0, 1)]] RWStructuredBuffer<int> queueCounters;

#ifdef USE_RAY_QUERY
[[vk::binding(0, 2)]] RaytracingAccelerationStructure topLevelAS;
#endif

struct PathState
{
    float3 Throughput; uint RngState;
    float3 Radiance; uint Depth;
    float3 RayOrigin; uint Flags;
    float3 RayDirection; uint PrimaryObjectIndex;
    float3 PrimaryAlbedo; uint PackedCounters;
    float3 PrimaryNormal; float LastBsdfPdf;
    float3 PrimaryPosition; uint Pad0;
};

struct PathRayWorkItem
{
    float3 Origin;
    uint SampleIndex;
    float3 Direction;
    uint Pad0;
};

struct HitWorkItem
{
    float3 RayDirection;
    uint SampleIndex;
    float2 Barycentrics;
    uint InstanceIndex;
    uint PrimitiveIndex;
    uint Pad0;
};

struct ShadowWorkItem
{
    float3 Origin;
    float TMin;
    float3 Direction;
    float TMax;
    float3 Contribution;
    uint SampleIndex;
};

[[vk::binding(1, 1)]] RWStructuredBuffer<PathState, Std430DataLayout> pathStates;
[[vk::binding(2, 1)]] RWStructuredBuffer<PathRayWorkItem, Std430DataLayout> inputRayQueue;
[[vk::binding(3, 1)]] RWStructuredBuffer<PathRayWorkItem, Std430DataLayout> outputRayQueue;
[[vk::binding(4, 1)]] RWStructuredBuffer<HitWorkItem, Std430DataLayout> hitQueue;
[[vk::binding(5, 1)]] RWStructuredBuffer<ShadowWorkItem, Std430DataLayout> shadowQueue;

static const uint QUEUE_COUNTER_ACTIVE = 0u;
static const uint QUEUE_COUNTER_NEXT = 1u;
static const uint QUEUE_COUNTER_HIT = 2u;
static const uint QUEUE_COUNTER_SHADOW = 3u;
static const uint QUEUE_GROUP_SIZE = 128u;
static const uint PATH_COUNTER_DIFFUSE_SHIFT = 0u;
static const uint PATH_COUNTER_SPECULAR_SHIFT = 8u;
static const uint PATH_COUNTER_TRANSMISSION_SHIFT = 16u;
static const uint PATH_COUNTER_HIT_SHIFT = 24u;
static const uint PATH_COUNTER_MASK = 0xFFu;

struct TraceHit
{
    uint InstanceIndex;
    uint PrimitiveIndex;
    float3 Barycentrics;
    float T;
};

uint appendNextRay(PathRayWorkItem item)
{
    int index;
    InterlockedAdd(queueCounters[QUEUE_COUNTER_NEXT], 1, index);
    outputRayQueue[uint(index)] = item;
    return uint(index);
}

uint appendHit(HitWorkItem item)
{
    int index;
    InterlockedAdd(queueCounters[QUEUE_COUNTER_HIT], 1, index);
    hitQueue[uint(index)] = item;
    return uint(index);
}

uint appendShadow(ShadowWorkItem item)
{
    int index;
    InterlockedAdd(queueCounters[QUEUE_COUNTER_SHADOW], 1, index);
    shadowQueue[uint(index)] = item;
    return uint(index);
}

bool popQueueIndex(uint counterIndex, out uint workIndex)
{
    int previousCount;
    InterlockedAdd(queueCounters[counterIndex], -1, previousCount);
    if (previousCount <= 0) {
        queueCounters[counterIndex] = 0;
        workIndex = 0u;
        return false;
    }

    workIndex = uint(previousCount - 1);
    return true;
}

int2 computeShadingScreenSize(int2 screenSize)
{
    return effectiveShadingScreenSize(screenSize);
}

uint computeShadingPixelCount(int2 screenSize)
{
    int2 shadingScreenSize = computeShadingScreenSize(screenSize);
    return uint(max(shadingScreenSize.x, 1) * max(shadingScreenSize.y, 1));
}

uint computePrimarySampleCount()
{
    return uint(useInteractivePathTracingPreview() ? 1 : effectivePrimarySampleCount());
}

uint computeSampleSlot(int2 shadingPixel, int2 shadingScreenSize, uint sampleIndex, uint sampleCount)
{
    return (uint(shadingPixel.y * shadingScreenSize.x + shadingPixel.x) * sampleCount) + sampleIndex;
}

uint packPathCounters(uint diffuseCount, uint specularCount, uint transmissionCount, bool hitAnything)
{
    return ((diffuseCount & PATH_COUNTER_MASK) << PATH_COUNTER_DIFFUSE_SHIFT) |
        ((specularCount & PATH_COUNTER_MASK) << PATH_COUNTER_SPECULAR_SHIFT) |
        ((transmissionCount & PATH_COUNTER_MASK) << PATH_COUNTER_TRANSMISSION_SHIFT) |
        ((hitAnything ? 1u : 0u) << PATH_COUNTER_HIT_SHIFT);
}

uint getDiffuseCount(PathState state) { return (state.PackedCounters >> PATH_COUNTER_DIFFUSE_SHIFT) & PATH_COUNTER_MASK; }
uint getSpecularCount(PathState state) { return (state.PackedCounters >> PATH_COUNTER_SPECULAR_SHIFT) & PATH_COUNTER_MASK; }
uint getTransmissionCount(PathState state) { return (state.PackedCounters >> PATH_COUNTER_TRANSMISSION_SHIFT) & PATH_COUNTER_MASK; }
bool getHitAnything(PathState state) { return ((state.PackedCounters >> PATH_COUNTER_HIT_SHIFT) & 1u) != 0u; }
void setHitAnything(inout PathState state, bool hitAnything)
{
    state.PackedCounters = (state.PackedCounters & ~(1u << PATH_COUNTER_HIT_SHIFT)) | ((hitAnything ? 1u : 0u) << PATH_COUNTER_HIT_SHIFT);
}
void incrementDiffuseCount(inout PathState state) { state.PackedCounters += (1u << PATH_COUNTER_DIFFUSE_SHIFT); }
void incrementSpecularCount(inout PathState state) { state.PackedCounters += (1u << PATH_COUNTER_SPECULAR_SHIFT); }
void incrementTransmissionCount(inout PathState state) { state.PackedCounters += (1u << PATH_COUNTER_TRANSMISSION_SHIFT); }

void initializePathState(
    uint sampleIndex,
    float3 origin,
    float3 direction,
    uint rngState)
{
    PathState state;
    state.Throughput = float3(1.0);
    state.RngState = rngState == 0u ? 1u : rngState;
    state.Radiance = float3(0.0);
    state.Depth = 0u;
    state.RayOrigin = origin;
    state.Flags = 0u;
    state.RayDirection = direction;
    state.PrimaryObjectIndex = InvalidInstance;
    state.PrimaryAlbedo = float3(0.0);
    state.PackedCounters = 0u;
    state.PrimaryNormal = float3(0.0);
    state.LastBsdfPdf = 0.0;
    state.PrimaryPosition = float3(0.0);
    state.Pad0 = 0u;
    pathStates[sampleIndex] = state;
}

bool traceSceneRay(float3 rayOrigin, float3 rayDirection, float tMin, float tMax, out TraceHit traceHit)
{
#ifdef USE_RAY_QUERY
    RayDesc ray;
    ray.Origin = rayOrigin;
    ray.Direction = rayDirection;
    ray.TMin = tMin;
    ray.TMax = tMax;

    RayQuery<RAY_FLAG_FORCE_OPAQUE> rayQuery;
    rayQuery.TraceRayInline(topLevelAS, RAY_FLAG_FORCE_OPAQUE, 0xFF, ray);
    while (rayQuery.Proceed()) { }

    if (rayQuery.CommittedStatus() != COMMITTED_TRIANGLE_HIT) {
        traceHit.InstanceIndex = InvalidInstance;
        traceHit.PrimitiveIndex = InvalidInstance;
        traceHit.Barycentrics = float3(0.0);
        traceHit.T = tMax;
        return false;
    }

    float2 bary = rayQuery.CommittedTriangleBarycentrics();
    traceHit.InstanceIndex = rayQuery.CommittedInstanceID();
    traceHit.PrimitiveIndex = rayQuery.CommittedPrimitiveIndex();
    traceHit.Barycentrics = float3(1.0 - bary.x - bary.y, bary.x, bary.y);
    traceHit.T = rayQuery.CommittedRayT();
    return true;
#else
    HitInfo hit = traceScene(rayOrigin, rayDirection, tMin, tMax);
    traceHit.InstanceIndex = hit.instanceIndex;
    traceHit.PrimitiveIndex = hit.primitiveIndex;
    traceHit.Barycentrics = hit.barycentrics;
    traceHit.T = hit.t;
    return hit.instanceIndex != InvalidInstance;
#endif
}

bool traceShadowPath(float3 rayOrigin, float3 rayDirection, float tMin, float tMax)
{
#ifdef USE_RAY_QUERY
    RayDesc ray;
    ray.Origin = rayOrigin;
    ray.Direction = rayDirection;
    ray.TMin = tMin;
    ray.TMax = tMax;

    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE> rayQuery;
    rayQuery.TraceRayInline(topLevelAS, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE, 0xFF, ray);
    while (rayQuery.Proceed()) { }
    return rayQuery.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
#else
    return traceShadowRay(rayOrigin, rayDirection, tMin, tMax);
#endif
}

float3 evaluateMissRadiance(float3 worldRayDirection, uint depth, float lastBsdfPdf, out uint missFlags)
{
    missFlags = uint(BounceType.RAY_TERMINATED);

    float3 envColor = depth == 0u
        ? evaluateEnvironmentVisibleRadiance(worldRayDirection, sceneSettings.Environment)
        : evaluateEnvironmentRadiance(worldRayDirection, sceneSettings.Environment);

    if (depth > 0u && lastBsdfPdf > 0.0) {
        float neeLightPdf = evaluateNeeLightPdfForDirection(worldRayDirection, sceneSettings.Environment);
        float wBsdf = powerHeuristic(lastBsdfPdf, neeLightPdf);
        envColor *= wBsdf;
    }

    if ((depth == 0u && sceneSettings.Environment.Visible == 0) ||
        (depth == 0u && sceneSettings.RenderSettings.TransparentBackground != 0))
        missFlags |= uint(BounceType.ENV_TRANSPARENT);

    return envColor;
}

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
    } else {
        payload.Attenuation = transmissionColor / max(1.0 - reflectProbability, EPSILON);
        payload.NextDirection = refractedDir;
        payload.LastBsdfPdf = 0.0;
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
    payload.LastBsdfPdf = combinedPdf;
    payload.Flags |= sampledSpecular ? uint(BounceType.BOUNCE_SPECULAR) : uint(BounceType.BOUNCE_DIFFUSE);
}

void queueDirectionalShadowRay(
    uint sampleIndex,
    float3 worldPosition,
    float3 shadingNormal,
    float3 geometricNormal,
    float3 viewDir,
    float3 albedo,
    float specular,
    float metallic,
    float roughness,
    float swSpecular,
    inout uint rngState)
{
    if (!hasDirectionalLight(sceneSettings.Environment))
        return;

    // Sample within the sun's soft-angle cone so the shadow penumbra widens with the
    // configured angle. A zero angle returns the exact direction (hard shadows).
    float3 sunDir = sampleDirectionalLightDirection(
        sceneSettings.Environment, float2(rand(rngState), rand(rngState)));
    float NdotLGeom = max(dot(geometricNormal, sunDir), 0.0);
    if (NdotLGeom <= 0.0)
        return;

    PbrBxdf bxdf;
    BxdfEval bxdfEval = bxdf.evalOpaque(
        viewDir,
        shadingNormal,
        geometricNormal,
        sunDir,
        albedo,
        specular,
        metallic,
        roughness,
        swSpecular);

    float shadowBias = computeShadowBias(worldPosition, NdotLGeom) * 2.0;
    ShadowWorkItem item;
    item.Origin = worldPosition + geometricNormal * shadowBias;
    item.TMin = shadowBias;
    item.Direction = sunDir;
    item.TMax = 1000.0;
    item.Contribution = bxdfEval.f * float3(sceneSettings.Environment.DirectionalIntensity) * NdotLGeom;
    item.SampleIndex = sampleIndex;
    appendShadow(item);
}

void queueEnvironmentShadowRay(
    uint sampleIndex,
    float3 shadowPosition,
    float3 shadingNormal,
    float3 geometricNormal,
    float3 viewDir,
    float3 albedo,
    float specular,
    float metallic,
    float roughness,
    float swSpecular,
    inout uint rngState)
{
    EnvironmentDataGpu environmentData = sceneSettings.Environment;
    if (environmentData.TextureIndex < 0 || environmentData.CdfTextureIndex < 0)
        return;

    float lightPdf;
    float3 lightDir = sampleEnvironmentDirection(environmentData, rngState, lightPdf);
    float3 Li = sampleEnvironmentMapColor(lightDir, environmentData, environmentData.LightingExposureScale);
    float NdotLGeom = max(dot(geometricNormal, lightDir), 0.0);
    if (NdotLGeom <= 0.0 || lightPdf <= 0.0)
        return;

    PbrBxdf bxdf;
    BxdfEval bxdfEval = bxdf.evalOpaque(
        viewDir,
        shadingNormal,
        geometricNormal,
        lightDir,
        albedo,
        specular,
        metallic,
        roughness,
        swSpecular);
    if (bxdfEval.pdf <= 0.0)
        return;

    float wLight = powerHeuristic(lightPdf, bxdfEval.pdf);
    float shadowBias = computeShadowBias(shadowPosition, NdotLGeom);

    ShadowWorkItem item;
    item.Origin = shadowPosition + geometricNormal * shadowBias;
    item.TMin = shadowBias;
    item.Direction = lightDir;
    item.TMax = 1000.0;
    item.Contribution = bxdfEval.f * Li * (NdotLGeom * wLight / max(lightPdf, EPSILON));
    item.SampleIndex = sampleIndex;
    appendShadow(item);
}

void enqueueContinuationRay(uint sampleIndex, PathState state)
{
    PathRayWorkItem nextRay;
    nextRay.Origin = state.RayOrigin;
    nextRay.SampleIndex = sampleIndex;
    nextRay.Direction = state.RayDirection;
    nextRay.Pad0 = 0u;
    appendNextRay(nextRay);
    pathStates[sampleIndex] = state;
}

#endif
