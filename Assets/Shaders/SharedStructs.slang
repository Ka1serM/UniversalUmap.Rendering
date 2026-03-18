// Generated from UniversalUmap.Rendering/Assets/Shaders/SharedStructs.cs. Do not edit by hand.

#ifndef _SHARED_STRUCTS_SLANG_
#define _SHARED_STRUCTS_SLANG_

#define INVALID_INSTANCE 0xFFFFFFFFu
#define GROUP_SIZE 16
#define MAX_LEAF_SIZE 8
#define SAH_BINS 16

struct Vertex
{
    float3 position;
    float3 normal;
    float3 tangent;
    float tangentSign;
    float2 uv;
};

struct Face
{
    int materialIndex;
};

struct MaterialData
{
    float3 albedo;
    int albedoIndex;
    float specular;
    float metallic;
    float roughness;
    float ior;
    int specularIndex;
    int metallicIndex;
    int roughnessIndex;
    int normalIndex;
    float3 transmissionColor;
    float transmission;
    float3 emission;
    float emissionStrength;
    int emissionIndex;
    int transmissionIndex;
    int opacityIndex;
    float opacity;
};

struct MeshAddressesGpu
{
    uint64_t vertexAddress;
    uint64_t indexAddress;
    uint64_t faceAddress;
    uint64_t materialAddress;
    uint64_t bvhNodeAddress;
    uint64_t bvhIndexAddress;
};

struct InstanceGpu
{
    float4x4 transform;
    float4x4 inverseTransform;
    float4x4 normalTransform;
    uint meshId;
    uint _pad1;
    uint _pad2;
    uint _pad3;
};

struct AabbGpu
{
    float3 minBounds;
    float _pad0;
    float3 maxBounds;
    float _pad1;
};

struct BvhNodeGpu
{
    AabbGpu leftBounds;
    AabbGpu rightBounds;
    uint rightChildOrPrimIndex;
    uint primCount;
    uint splitAxis;
};

struct PushDataGpu
{
    int frame;
    int isMoving;
};

struct RenderSettingsDataGpu
{
    int samples;
    int diffuseBounces;
    int specularBounces;
    int transmissionBounces;
    int adaptiveSamplingEnabled;
    int adaptiveMinSamples;
    float adaptiveTargetError;
    int russianRouletteStartBounce;
    float exposure;
    int transparentBackground;
    int renderMode;
};

struct EnvironmentDataGpu
{
    int textureIndex;
    int cdfTextureIndex;
    int irradianceMapIndex;
    int radianceMapIndex;
    float rotationSin;
    float rotationCos;
    float visibleExposureScale;
    float lightingExposureScale;
    float maxTextureLod;
    int visible;
    float3 directionalDirection;
    float directionalIntensity;
    float rotation;
    float visibleExposure;
    float lightingExposure;
    int _pad0;
    int _pad1;
    int _pad2;
};

struct CameraDataGpu
{
    float3 position;
    float aperture;
    float3 direction;
    float focusDistance;
    float3 horizontal;
    float focalLength;
    float3 vertical;
    float bokehBias;
};

struct SceneSettingsDataGpu
{
    RenderSettingsDataGpu renderSettings;
    CameraDataGpu camera;
    EnvironmentDataGpu environment;
};

#endif // _SHARED_STRUCTS_SLANG_
