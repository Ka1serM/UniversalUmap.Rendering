#ifndef IMATERIAL_SLANG
#define IMATERIAL_SLANG

#include "../Shared.slang"

struct MaterialSample
{
    float3 albedo;
    float3 emission;
    float metallic;
    float specular;
    float roughness;
    float opacity;
    float transmission;
};

interface IMaterial
{
    MaterialSample sample(MaterialData material, float2 uv);
};

#endif // IMATERIAL_SLANG
