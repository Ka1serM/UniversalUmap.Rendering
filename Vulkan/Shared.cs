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
}

[Flags]
public enum SceneDirtyFlags : byte
{
    None = 0,
    Tlas = 1 << 0,
    Meshes = 1 << 1,
    Textures = 1 << 2,
    Accumulation = 1 << 3,
    Settings = 1 << 4
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Vertex
{
    public Vector3 Position;
    public int Pad0;  // Vector3 padding
    public Vector3 Normal;
    public int Pad1;  // Vector3 padding
    public Vector3 Tangent;
    public int Pad2;  // Vector3 padding
    public float TangentSign;
    public Vector2 UV;
    public int Pad3;  // Struct padding to 16 bytes
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Face
{
    public int MaterialIndex;
    public int Pad0;  // Struct padding to 16 bytes
    public int Pad1;  // Struct padding to 16 bytes
    public int Pad2;  // Struct padding to 16 bytes
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MaterialData
{
    public Vector3 Albedo;
    public int AlbedoIndex;  // Also serves as padding for Albedo

    public float Specular;
    public float Metallic;
    public float Roughness;
    public float Ior;

    public int SpecularIndex;
    public int MetallicIndex;
    public int RoughnessIndex;
    public int NormalIndex;

    public Vector3 TransmissionColor;
    public int Pad0;  // Vector3 padding

    public float Transmission;
    public float Pad1;  // Alignment padding

    public Vector3 Emission;
    public int Pad2;  // Vector3 padding

    public float EmissionStrength;
    public int Pad3;  // Alignment padding

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
    public Vector3 LocalBoundsMin;
    public uint IndexCount;
    public Vector3 LocalBoundsMax;
    public uint Pad0;  // Struct padding to 16 bytes
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct MeshRasterMetadataGpu
{
    public uint VisibleInstanceOffset;
    public uint InstanceCapacity;
    public uint IndexCount;
    public uint Pad0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DrawIndirectCommandGpu
{
    public uint VertexCount;
    public uint InstanceCount;
    public uint FirstVertex;
    public uint FirstInstance;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct DispatchIndirectCommandGpu
{
    public uint GroupCountX;
    public uint GroupCountY;
    public uint GroupCountZ;
}

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

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct AabbGpu
{
    public Vector3 MinBounds;
    public int Pad0;  // Vector3 padding
    public Vector3 MaxBounds;
    public int Pad1;  // Vector3 padding
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct BvhNodeGpu
{
    public AabbGpu LeftBounds;
    public AabbGpu RightBounds;
    public uint RightChildOrPrimIndex;
    public uint PrimCount;
    public uint SplitAxis;
    public uint Pad0;  // Struct padding to 16 bytes
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PushDataGpu
{
    public int Frame;
    public int IsMoving;
    public int PixelSizePercent;
    public int Pad1;  // Vulkan alignment padding
    public CameraDataGpu Camera;
}

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
    public int AoSampleAlbedo;
    public int Pad0;  // Struct padding to 16 bytes
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct EnvironmentDataGpu
{
    public int TextureIndex;
    public int CdfTextureIndex;
    public int IrradianceMapIndex;
    public int RadianceMapIndex;
    public float RotationSin;
    public float RotationCos;
    public float VisibleExposureScale;
    public float LightingExposureScale;
    public float MaxTextureLod;
    public int Visible;

    public Vector3 DirectionalDirection;
    public int Pad4;  // Vector3 padding

    public float DirectionalIntensity;
    public float Rotation;
    public float VisibleExposure;
    public float LightingExposure;

    public int Pad0;
    public int Pad1;
    public int Pad2;
    public int Pad3;  // Struct padding to 16 bytes
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct CameraDataGpu
{
    public Vector3 Position;
    public float Pad0;  // Vector3 padding
    public Vector3 Direction;
    public float Pad1;  // Vector3 padding
    public Vector3 Horizontal;
    public float Pad2;  // Vector3 padding
    public Vector3 Vertical;
    public float Pad3;  // Vector3 padding
    public float FocalLength;
    public float FocusDistance;
    public float Aperture;
    public float BokehBias;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct RasterCameraDataGpu
{
    public Vector3 Position;
    public float Pad0;  // Vector3 padding
    public Vector3 Direction;
    public float Pad1;  // Vector3 padding
    public Matrix4x4 WorldToClip;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct SceneSettingsDataGpu
{
    public RenderSettingsDataGpu RenderSettings;
    public EnvironmentDataGpu Environment;
    public RasterCameraDataGpu RasterCamera;
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
