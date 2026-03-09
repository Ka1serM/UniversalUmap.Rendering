struct SamplerState {
    vec2 randomOffset;
    int frame;
    int dimension;
};

struct Payload {
    vec3 attenuation; uint flags;
    vec3 emission; int pad0;
    
    vec3 position; uint depth; 
    vec3 nextDirection; uint rngState;
    
    vec3 albedo; float roughness;
    vec3 normal; uint objectIndex;

    float lastBsdfPdf;
    float lastNeeLightPdf;
    uint pad1;
    uint pad2;
    uint pad3;
};

struct AoPayload {
    vec3 emission; uint flags;
    vec3 position; uint rngState;
    vec3 albedo; uint objectIndex;
    vec3 normal; uint pad0;
};

struct HitInfo {
    float t;
    uint instanceIndex;
    uint primitiveIndex;
    vec3 barycentrics;
};
