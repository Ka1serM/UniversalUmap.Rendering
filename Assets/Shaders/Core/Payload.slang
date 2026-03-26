#ifndef PAYLOAD_SLANG
#define PAYLOAD_SLANG

#include "../Shared.slang"

struct Payload
{
    float3 Attenuation; uint Flags;
    float3 Emission; int Pad0;

    float3 Position; uint Depth;
    float3 NextDirection; uint RngState;

    float3 Albedo; float Roughness;
    float3 Normal; uint ObjectIndex;

    float LastBsdfPdf;
    float LastNeeLightPdf;
    uint Pad1;
    uint Pad2;
    uint Pad3;

    [mutating]
    void initialize(uint rngState, uint depth)
    {
        Attenuation = float3(1.0);
        Flags = 0u;
        Emission = float3(0.0);
        Pad0 = 0;
        Position = float3(0.0);
        Depth = depth;
        NextDirection = float3(0.0);
        RngState = rngState;
        Albedo = float3(0.0);
        Roughness = 0.0;
        Normal = float3(0.0);
        ObjectIndex = InvalidInstance;
        LastBsdfPdf = 0.0;
        LastNeeLightPdf = 0.0;
        Pad1 = 0u;
        Pad2 = 0u;
        Pad3 = 0u;
    }
};

struct AoPayload
{
    float3 Emission; uint Flags;
    float3 Position; uint RngState;
    float3 Albedo; uint ObjectIndex;
    float3 Normal; uint Pad0;

    [mutating]
    void initialize(uint rngState)
    {
        Emission = float3(0.0);
        Flags = 0u;
        Position = float3(0.0);
        RngState = rngState;
        Albedo = float3(0.0);
        ObjectIndex = InvalidInstance;
        Normal = float3(0.0);
        Pad0 = 0u;
    }
};

#endif // PAYLOAD_SLANG
