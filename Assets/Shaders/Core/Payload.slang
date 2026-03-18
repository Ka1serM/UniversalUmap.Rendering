#ifndef PAYLOAD_SLANG
#define PAYLOAD_SLANG

#include "../SharedStructs.slang"

struct Payload
{
    float3 attenuation; uint flags;
    float3 emission; int pad0;

    float3 position; uint depth;
    float3 nextDirection; uint rngState;

    float3 albedo; float roughness;
    float3 normal; uint objectIndex;

    float lastBsdfPdf;
    float lastNeeLightPdf;
    uint pad1;
    uint pad2;
    uint pad3;

    [mutating]
    void initialize(uint rngState, uint depth)
    {
        attenuation = float3(1.0);
        flags = 0u;
        emission = float3(0.0);
        pad0 = 0;
        position = float3(0.0);
        this.depth = depth;
        nextDirection = float3(0.0);
        this.rngState = rngState;
        albedo = float3(0.0);
        roughness = 0.0;
        normal = float3(0.0);
        objectIndex = INVALID_INSTANCE;
        lastBsdfPdf = 0.0;
        lastNeeLightPdf = 0.0;
        pad1 = 0u;
        pad2 = 0u;
        pad3 = 0u;
    }
};

struct AoPayload
{
    float3 emission; uint flags;
    float3 position; uint rngState;
    float3 albedo; uint objectIndex;
    float3 normal; uint pad0;

    [mutating]
    void initialize(uint rngState)
    {
        emission = float3(0.0);
        flags = 0u;
        position = float3(0.0);
        this.rngState = rngState;
        albedo = float3(0.0);
        objectIndex = INVALID_INSTANCE;
        normal = float3(0.0);
        pad0 = 0u;
    }
};

#endif // PAYLOAD_SLANG
