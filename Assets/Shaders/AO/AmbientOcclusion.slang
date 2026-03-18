#ifndef AMBIENT_OCCLUSION_SLANG
#define AMBIENT_OCCLUSION_SLANG

#include "../SharedStructs.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Bxdfs/Brdf.slang"
#include "../Geometry/Intersection.slang"

static const float AO_MAX_DISTANCE = 1.5;
enum AoConfig : int
{
    AO_SAMPLES = 4,
};

bool traceAoShadowRay(float3 rayOrigin, float3 rayDirection, float tMin, float tMax)
{
#ifdef USE_COMPUTE
    return traceShadowRay(rayOrigin, rayDirection, tMin, tMax);
#else
    uint shadowPayload = 1u;
    const uint shadowFlags = RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH
        | RAY_FLAG_FORCE_OPAQUE
        | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER;

    RayDesc ray;
    ray.Origin = rayOrigin;
    ray.Direction = rayDirection;
    ray.TMin = tMin;
    ray.TMax = tMax;
    TraceRay(topLevelAS, shadowFlags, 0xFF, 0, 0, 1, ray, shadowPayload);
    return shadowPayload != 0u;
#endif
}

float estimateAmbientOcclusion(float3 worldPosition, float3 geometricNormal, inout uint rngState)
{
    float occluded = 0.0;
    for (int i = 0; i < AoConfig.AO_SAMPLES; ++i) {
        float3 dir = sampleDiffuse(geometricNormal, rngState);
        float nDotL = max(dot(geometricNormal, dir), 0.0);

        float bias = computeShadowBias(worldPosition, nDotL);
        float3 origin = worldPosition + geometricNormal * bias;
        if (traceAoShadowRay(origin, dir, bias, AO_MAX_DISTANCE))
            occluded += 1.0;
    }
    return 1.0 - (occluded / float(AoConfig.AO_SAMPLES));
}

float3 shadeAmbientOcclusionOnly(float ao)
{
    return float3(ao);
}

float3 shadeAmbientOcclusionSurface(float3 worldPosition, float3 geometricNormal, inout uint rngState)
{
    float ao = estimateAmbientOcclusion(worldPosition, geometricNormal, rngState);
    return shadeAmbientOcclusionOnly(ao);
}

#endif // AMBIENT_OCCLUSION_SLANG
