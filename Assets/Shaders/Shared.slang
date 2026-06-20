// Auto-generated from Shared.cs - do not edit manually

#ifndef SHARED_SLANG
#define SHARED_SLANG

// Constants
static const uint INVALID_INSTANCE = 0xFFFFFFFFu;
static const uint InvalidInstance = INVALID_INSTANCE;
static const int GROUP_SIZE = 16;
static const int MAXLEAFSIZE = 8;
static const int SAHBINS = 16;
static const int CAMERA_PERSPECTIVE = 0;
static const int CAMERA_ORTHOGRAPHIC = 1;
static const int CAMERA_FISHEYE = 2;

struct Vertex
{
    float3 Position;
    int Pad0;
    float3 Normal;
    int Pad1;
    float3 Tangent;
    int Pad2;
    float TangentSign;
    float2 UV;
    int Pad3;
};

struct Face
{
    int MaterialIndex;
};

struct MaterialData
{
    float3 Albedo;
    int AlbedoIndex;
    float Specular;
    float Metallic;
    float Roughness;
    float Ior;
    int SpecularIndex;
    int MetallicIndex;
    int RoughnessIndex;
    int NormalIndex;
    float3 TransmissionColor;
    int Pad0;
    float Transmission;
    float Pad1;
    float3 Emission;
    int Pad2;
    float EmissionStrength;
    int Pad3;
    int EmissionIndex;
    int TransmissionIndex;
    int OpacityIndex;
    float Opacity;
};

struct MeshAddressesGpu
{
    uint64_t VertexAddress;
    uint64_t IndexAddress;
    uint64_t FaceAddress;
    uint64_t MaterialAddress;
    uint64_t BvhNodeAddress;
    uint64_t BvhIndexAddress;
};

struct DispatchIndirectCommandGpu
{
    uint GroupCountX;
    uint GroupCountY;
    uint GroupCountZ;
};

struct InstanceGpu
{
    float4x4 Transform;
    float4x4 InverseTransform;
    float4x4 NormalTransform;
    uint MeshId;
    uint Pad1;
    uint Pad2;
    uint Pad3;
};

struct AabbGpu
{
    float3 MinBounds;
    int Pad0;
    float3 MaxBounds;
    int Pad1;
};

struct BvhNodeGpu
{
    AabbGpu LeftBounds;
    AabbGpu RightBounds;
    uint RightChildOrPrimIndex;
    uint PrimCount;
    uint SplitAxis;
    uint Pad0;
};

struct PushDataGpu
{
    int Frame;
    int IsMoving;
    int PixelSizePercent;
    int Pad1;
    CameraDataGpu Camera;
};

struct RenderSettingsDataGpu
{
    int Samples;
    int DiffuseBounces;
    int SpecularBounces;
    int TransmissionBounces;
    int AdaptiveSamplingEnabled;
    int AdaptiveMinSamples;
    float AdaptiveTargetError;
    int RussianRouletteStartBounce;
    float Exposure;
    int TransparentBackground;
    int RenderMode;
    int BufferVisualization;
    int TaaEnabled;
    int AoSampleAlbedo;
};

struct EnvironmentDataGpu
{
    int TextureIndex;
    int CdfTextureIndex;
    float RotationSin;
    float RotationCos;
    float VisibleExposureScale;
    float LightingExposureScale;
    float MaxTextureLod;
    int Visible;
    float3 DirectionalDirection;
    float DirectionalIntensity;
    float Rotation;
    float VisibleExposure;
    float LightingExposure;
    float DirectionalSoftAngle;
};

struct CameraDataGpu
{
    float3 Position;
    float Pad0;
    float3 Direction;
    float Pad1;
    float3 Horizontal;
    float Pad2;
    float3 Vertical;
    float Pad3;
    float FocalLength;
    float FocusDistance;
    float Aperture;
    float BokehBias;
    int CameraType;
    float OrthoHeight;
    float FisheyeFov;
    float NearPlane;
    float FarPlane;
};

struct SceneSettingsDataGpu
{
    RenderSettingsDataGpu RenderSettings;
    EnvironmentDataGpu Environment;
};


#endif // SHARED_SLANG
