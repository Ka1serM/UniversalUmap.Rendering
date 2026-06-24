using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace UniversalUmap.Rendering;

internal static class ShaderDefines
{
    public const uint INVALID_INSTANCE = 0xFFFFFFFFu;
    public const uint InvalidInstance = INVALID_INSTANCE;
    public const int GROUP_SIZE = 16;
    public const int MAXLEAFSIZE = 8;
    public const int SAHBINS = 16;

    public const int CAMERA_PERSPECTIVE = 0;
    public const int CAMERA_ORTHOGRAPHIC = 1;
    public const int CAMERA_FISHEYE = 2;
}

[Flags]
public enum SceneDirtyFlags : byte
{
    None = 0,
    Tlas = 1 << 0,
    Meshes = 1 << 1,
    Textures = 1 << 2,
    Accumulation = 1 << 3,
    Settings = 1 << 4,
    Lights = 1 << 5
}

// Read on the GPU via buffer-reference pointers (reinterpret<VertexBuffer*>) under
// Slang's natural/scalar layout, where a float3 packs to 12 bytes. The int pads put
// each float3 (Position/Normal/Tangent) on a 16-byte boundary so the GPU can issue a
// single aligned 128-bit load per vector instead of a split load — this struct is read
// three times per surface hit, so the alignment is worth the 16 bytes. Total: 64 bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Vertex
{
    public Vector3 Position;
    public int Pad0;
    public Vector3 Normal;
    public int Pad1;
    public Vector3 Tangent;
    public int Pad2;
    public float TangentSign;
    public Vector2 UV;
    public int Pad3;
}

// A single index per triangle. There is no float3 to align, so padding would only
// quadruple the per-triangle bandwidth of the material lookup — kept at 4 bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Face
{
    public int MaterialIndex;
}

// Fetched once per surface hit. Each float3 (Albedo/TransmissionColor/Emission) is
// paired with an index/pad so the vectors stay 16-byte aligned for single-fetch loads.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MaterialData
{
    public Vector3 Albedo;
    public int AlbedoIndex;

    public float Specular;
    public float Metallic;
    public float Roughness;
    public float Ior;

    public int SpecularIndex;
    public int MetallicIndex;
    public int RoughnessIndex;
    public int NormalIndex;

    public Vector3 TransmissionColor;
    public int Pad0;

    public float Transmission;
    public float Pad1;

    public Vector3 Emission;
    public int Pad2;

    public float EmissionStrength;
    public int Pad3;

    public int EmissionIndex;
    public int TransmissionIndex;
    public int OpacityIndex;
    public float Opacity;

    public MaterialData()
    {
        Albedo = new Vector3(0.8f, 0.8f, 0.8f);
        AlbedoIndex = -1;
        Specular = 0f;
        Metallic = 0f;
        Roughness = 1f;
        Ior = 1.5f;
        SpecularIndex = -1;
        MetallicIndex = -1;
        RoughnessIndex = -1;
        NormalIndex = -1;
        TransmissionColor = Vector3.One;
        Transmission = 0f;
        Emission = Vector3.Zero;
        EmissionStrength = 0f;
        EmissionIndex = -1;
        TransmissionIndex = -1;
        OpacityIndex = -1;
        Opacity = 1f;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct MeshAddressesGpu
{
    public ulong VertexAddress;
    public ulong IndexAddress;
    public ulong FaceAddress;
    public ulong MaterialAddress;
    public ulong BvhNodeAddress;
    public ulong BvhIndexAddress;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DispatchIndirectCommandGpu
{
    public uint GroupCountX;
    public uint GroupCountY;
    public uint GroupCountZ;
}

// Bound as StructuredBuffer<InstanceGpu, Std430DataLayout>: std430 rounds the struct
// (alignment 16 from the mat4 members) up to a 208-byte array stride, so the trailing
// 12 bytes of padding are required for the C# elements to match the GPU stride.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct InstanceGpu
{
    public Matrix4x4 Transform;
    public Matrix4x4 InverseTransform;
    public Matrix4x4 NormalTransform;
    public uint MeshId;
    public uint Pad1;
    public uint Pad2;
    public uint Pad3;
}

// The pads 16-byte-align MinBounds/MaxBounds so the AABB slab test in BVH traversal —
// one of the hottest GPU loops — loads each bound as a single aligned 128-bit fetch.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct AabbGpu
{
    public Vector3 MinBounds;
    public int Pad0;
    public Vector3 MaxBounds;
    public int Pad1;
}

// 80 bytes: two 32-byte AABBs (bounds stay 16-aligned) plus the child/prim words, with
// a trailing pad keeping the node a multiple of 16 for coalesced traversal reads.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct BvhNodeGpu
{
    public AabbGpu LeftBounds;
    public AabbGpu RightBounds;
    public uint RightChildOrPrimIndex;
    public uint PrimCount;
    public uint SplitAxis;
    public uint Pad0;
}

// Uploaded as a push constant (ConstantBuffer<PushDataGpu>), which uses std430: float3
// members get 16-byte alignment, so the padding below and in CameraDataGpu is required
// to keep the C# Pack=4 offsets in sync with the layout the shader expects. Pad1 aligns
// the embedded Camera (whose first member is a float3) to a 16-byte boundary.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PushDataGpu
{
    public int Frame;
    public int IsMoving;
    public int PixelSizePercent;
    public int Pad1;
    public CameraDataGpu Camera;
}

// Part of SceneSettingsDataGpu, bound with ScalarDataLayout and read once per frame into
// registers. There is no per-element stride and no hot vector load here, so alignment
// padding would buy nothing — kept tightly packed.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct RenderSettingsDataGpu
{
    public int Samples;
    public int DiffuseBounces;
    public int SpecularBounces;
    public int TransmissionBounces;
    public int AdaptiveSamplingEnabled;
    public int AdaptiveMinSamples;
    public float AdaptiveTargetError;
    public int RussianRouletteStartBounce;
    public float Exposure;
    public int TransparentBackground;
    public int RenderMode;
    public int BufferVisualization;
    public int TaaEnabled;
    public int AoSampleAlbedo;
}

// Also ScalarDataLayout and read once per frame (see RenderSettingsDataGpu): scalar
// layout packs DirectionalDirection's float3 with no gap, so no padding is needed.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct EnvironmentDataGpu
{
    public int TextureIndex;
    public int CdfTextureIndex;
    public float RotationSin;
    public float RotationCos;
    public float VisibleExposureScale;
    public float LightingExposureScale;
    public float MaxTextureLod;
    public int Visible;

    public Vector3 DirectionalDirection;

    public float DirectionalIntensity;
    public float Rotation;
    public float VisibleExposure;
    public float LightingExposure;

    public float DirectionalSoftAngle;
}

public enum CameraProjectionType : int
{
    Perspective = 0,
    Orthographic = 1,
    Fisheye = 2
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct CameraDataGpu
{
    public Vector3 Position;
    public float Pad0;
    public Vector3 Direction;
    public float Pad1;
    public Vector3 Horizontal;
    public float Pad2;
    public Vector3 Vertical;
    public float Pad3;
    public float FocalLength;
    public float FocusDistance;
    public float Aperture;
    public float BokehBias;
    public int CameraType;
    public float OrthoHeight;
    public float FisheyeFov;
    public float NearPlane;
    public float FarPlane;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct SceneSettingsDataGpu
{
    public RenderSettingsDataGpu RenderSettings;
    public EnvironmentDataGpu Environment;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct LightGpu
{
    public Vector3 Position;
    public int Type;
    public Vector3 Direction;
    public int Pad0;
    public Vector3 Color;
    public float Intensity;
    public float Range;
    public float InnerConeAngle;
    public float OuterConeAngle;
    public float SourceRadius;
    public float SoftSourceRadius;
    public float SourceLength;
    public float SourceWidth;
    public float SourceHeight;
    public float LightSourceAngle;
    public float LightSourceSoftAngle;
    public float LightFalloffExponent;
    public int UseInverseSquaredFalloff;
    public int Pad1;
    public int Pad2;
    public int Pad3;
    public int Pad4;
}

internal static class StructPacking
{
    public static byte[] ToBytes<T>(ReadOnlySpan<T> values)
        where T : unmanaged
    {
        if (values.IsEmpty)
            return [];
        return MemoryMarshal.AsBytes(values).ToArray();
    }

    public static unsafe int SizeOf<T>()
        where T : unmanaged
    {
        return sizeof(T);
    }
}
