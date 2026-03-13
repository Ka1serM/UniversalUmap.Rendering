#extension GL_EXT_shader_explicit_arithmetic_types_int64 : require
// Generated from UniversalUmap.Rendering/SharedStructs.cs. Do not edit by hand.

#define INVALID_INSTANCE 0xFFFFFFFFu
#define GROUP_SIZE 16
#define MAX_LEAF_SIZE 8
#define SAH_BINS 16

struct Vertex
{
    vec3 position;
    vec3 normal;
    vec3 tangent;
    vec2 uv;
};

struct Face
{
    int materialIndex;
};

struct Material
{
    vec3 albedo;
    int albedoIndex;
    float specular;
    float metallic;
    float roughness;
    float ior;
    int specularIndex;
    int metallicIndex;
    int roughnessIndex;
    int normalIndex;
    vec3 transmissionColor;
    float transmission;
    vec3 emission;
    float emissionStrength;
    int emissionIndex;
    int transmissionIndex;
    int opacityIndex;
    float opacity;
};

struct MeshAddresses
{
    uint64_t vertexAddress;
    uint64_t indexAddress;
    uint64_t faceAddress;
    uint64_t materialAddress;
    uint64_t bvhNodeAddress;
    uint64_t bvhIndexAddress;
};

struct ComputeInstance
{
    mat4 transform;
    mat4 inverseTransform;
    uint meshId;
    uint _pad1;
    uint _pad2;
    uint _pad3;
};

struct AABB
{
    vec3 minBounds;
    float _pad0;
    vec3 maxBounds;
    float _pad1;
};

struct BVHNode
{
    AABB leftBounds;
    AABB rightBounds;
    uint rightChildOrPrimIndex;
    uint primCount;
    uint splitAxis;
};

struct PushData
{
    int frame;
    int isMoving;
};

struct RenderSettingsData
{
    int samples;
    int diffuseBounces;
    int specularBounces;
    int transmissionBounces;
    float exposure;
    int transparentBackground;
    int renderMode;
};

struct EnvironmentData
{
    int textureIndex;
    int cdfTextureIndex;
    float rotation;
    float visibleExposure;
    float lightingExposure;
    int visible;
    vec3 directionalDirection;
    float directionalIntensity;
    int _pad0;
    int _pad1;
};

struct CameraData
{
    vec3 position;
    float aperture;
    vec3 direction;
    float focusDistance;
    vec3 horizontal;
    float focalLength;
    vec3 vertical;
    float bokehBias;
};

// Shader-only declarations.
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
