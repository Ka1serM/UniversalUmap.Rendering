#ifndef IINTEGRATOR_SLANG
#define IINTEGRATOR_SLANG

#include "../SharedStructs.slang"

interface IIntegrator<TPayload>
{
    void shadeMiss(float3 worldRayDirection, inout TPayload payload);

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
        inout TPayload payload);
};

#endif // IINTEGRATOR_SLANG
