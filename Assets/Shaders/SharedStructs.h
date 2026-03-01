#extension GL_EXT_shader_explicit_arithmetic_types_int64 : require
// NOTE: Keep this file layout-synced with UniversalUmap.Rendering/SharedStructs.cs.

#define INVALID_INSTANCE 0xFFFFFFFFu //max uint32_t
#define GROUP_SIZE 16

#define MAX_LEAF_SIZE 8
#define SAH_BINS 16

struct AABB
{
    vec3 minBounds;
    float _pad0;
    vec3 maxBounds;
    float _pad1;
};

// Node is 64 bytes, aligning perfectly to GPU cache lines.
struct  BVHNode {
    AABB leftBounds;  // 32 bytes
    AABB rightBounds; // 32 bytes
    // For a leaf node: index of the first primitive in the flat index list.
    uint rightChildOrPrimIndex;
    // For an interior node: 0
    // For a leaf node: number of primitives in the leaf.
    uint primCount;
    // For an interior node: split axis (0=x, 1=y, 2=z)
    // For a leaf node: unused
    uint splitAxis;
    uint _pad[7]; // Padding
};

struct PushData {
    int samples, diffuseBounces, specularBounces, transmissionBounces;
    float exposure; int frame, isMoving, visualizeBVH;
};

struct EnvironmentData {
    vec3 color; float intensity;
    int textureIndex; int cdfTextureIndex; float rotation; float exposure;
    int visible, _pad0, _pad1,  _pad2;
};

struct CameraData {
    vec3 position; float aperture;
    vec3 direction; float focusDistance;
    vec3 horizontal; float focalLength;
    vec3 vertical; float bokehBias;
};

struct PushConstantsData {
    PushData push;
    CameraData camera;
    EnvironmentData environment;
};

struct Vertex {
    vec3 position; int _pad0;
    vec3 normal; int _pad1;
    vec3 tangent; int _pad2;
    vec2 uv; int _pad3, _pad4;
};

struct Face {
    int materialIndex, _pad0, _pad1, _pad2;
};

struct Material {
    vec3 albedo; int albedoIndex;
    float specular, metallic, roughness, ior;
    int specularIndex, metallicIndex, roughnessIndex, normalIndex;
    vec3 transmissionColor; float transmission;
    vec3 emission; float emissionStrength;
    int emissionIndex, transmissionIndex, opacityIndex; float opacity;

};

//Vulkan Only
struct MeshAddresses {
    uint64_t vertexAddress;
    uint64_t indexAddress;
    uint64_t faceAddress;
    uint64_t materialAddress;
    // Compute RT
    uint64_t bvhNodeAddress;
    uint64_t bvhIndexAddress;
};

// Sampler struct to keep track of state for the current pixel.
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

    // MIS bookkeeping for environment light.
    float lastBsdfPdf;
    float lastNeeLightPdf;
    uint pad1;
    uint pad2;
    uint pad3;
};


struct HitInfo {
    float t;
    uint instanceIndex;
    uint primitiveIndex;
    vec3 barycentrics;
};

struct ComputeInstance {
    mat4 transform;
    mat4 inverseTransform;
    uint meshId, pad1, pad2, pad3; 
};
