using System;
using System.Numerics;
using System.Runtime.InteropServices;
using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering;

internal static class SharedShaderDefines
{
    public const uint InvalidInstance = 0xFFFFFFFFu;
    public const int GroupSize = 16;
    public const int MaxLeafSize = 8;
    public const int SahBins = 16;
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

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 44)]
public struct Vertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector3 Tangent;
    public Vector2 UV;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 4)]
public struct Face
{
    public int MaterialIndex;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 96)]
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
    public float Transmission;

    public Vector3 Emission;
    public float EmissionStrength;

    public int EmissionIndex;
    public int TransmissionIndex;
    public int OpacityIndex;
    public float Opacity;

    public MaterialData()
    {
        Albedo = Vector3.One;
        AlbedoIndex = -1;
        Specular = 0.5f;
        Metallic = 0f;
        Roughness = 0f;
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

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
internal struct MeshAddressesGpu
{
    public ulong VertexAddress;
    public ulong IndexAddress;
    public ulong FaceAddress;
    public ulong MaterialAddress;
    public ulong BvhNodeAddress;
    public ulong BvhIndexAddress;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 144)]
internal struct ComputeInstanceGpu
{
    public Matrix4x4 Transform;
    public Matrix4x4 InverseTransform;
    public uint MeshId;
    public uint Pad1;
    public uint Pad2;
    public uint Pad3;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
internal struct AabbGpu
{
    public Vector3 MinBounds;
    public float Pad0;
    public Vector3 MaxBounds;
    public float Pad1;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 76)]
internal unsafe struct BvhNodeGpu
{
    public AabbGpu LeftBounds;
    public AabbGpu RightBounds;
    public uint RightChildOrPrimIndex;
    public uint PrimCount;
    public uint SplitAxis;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
internal struct PushDataGpu
{
    public int Frame;
    public int IsMoving;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 44)]
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
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct EnvironmentDataGpu
{
    public int TextureIndex;
    public int CdfTextureIndex;
    public float Rotation;
    public float VisibleExposure;
    public float LightingExposure;
    public int Visible;
    public Vector3 DirectionalDirection;
    public float DirectionalIntensity;
    public int Pad0;
    public int Pad1;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
internal struct CameraDataGpu
{
    public Vector3 Position;
    public float Aperture;
    public Vector3 Direction;
    public float FocusDistance;
    public Vector3 Horizontal;
    public float FocalLength;
    public Vector3 Vertical;
    public float BokehBias;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 156)]
internal struct SceneSettingsDataGpu
{
    public RenderSettingsDataGpu RenderSettings;
    public CameraDataGpu Camera;
    public EnvironmentDataGpu Environment;
}

internal static class GpuStructLayoutValidator
{
    public static void ValidateOrThrow()
    {
        ValidateType<Vertex>(44, (nameof(Vertex.Position), 0), (nameof(Vertex.Normal), 12), (nameof(Vertex.Tangent), 24), (nameof(Vertex.UV), 36));
        ValidateType<Face>(4, (nameof(Face.MaterialIndex), 0));
        ValidateType<MaterialData>(96, (nameof(MaterialData.Albedo), 0), (nameof(MaterialData.AlbedoIndex), 12), (nameof(MaterialData.Specular), 16), (nameof(MaterialData.Metallic), 20), (nameof(MaterialData.Roughness), 24), (nameof(MaterialData.Ior), 28), (nameof(MaterialData.SpecularIndex), 32), (nameof(MaterialData.MetallicIndex), 36), (nameof(MaterialData.RoughnessIndex), 40), (nameof(MaterialData.NormalIndex), 44), (nameof(MaterialData.TransmissionColor), 48), (nameof(MaterialData.Transmission), 60), (nameof(MaterialData.Emission), 64), (nameof(MaterialData.EmissionStrength), 76), (nameof(MaterialData.EmissionIndex), 80), (nameof(MaterialData.TransmissionIndex), 84), (nameof(MaterialData.OpacityIndex), 88), (nameof(MaterialData.Opacity), 92));
        ValidateType<MeshAddressesGpu>(48, (nameof(MeshAddressesGpu.VertexAddress), 0), (nameof(MeshAddressesGpu.IndexAddress), 8), (nameof(MeshAddressesGpu.FaceAddress), 16), (nameof(MeshAddressesGpu.MaterialAddress), 24), (nameof(MeshAddressesGpu.BvhNodeAddress), 32), (nameof(MeshAddressesGpu.BvhIndexAddress), 40));
        ValidateType<ComputeInstanceGpu>(144, (nameof(ComputeInstanceGpu.Transform), 0), (nameof(ComputeInstanceGpu.InverseTransform), 64), (nameof(ComputeInstanceGpu.MeshId), 128), (nameof(ComputeInstanceGpu.Pad1), 132), (nameof(ComputeInstanceGpu.Pad2), 136), (nameof(ComputeInstanceGpu.Pad3), 140));
        ValidateType<AabbGpu>(32, (nameof(AabbGpu.MinBounds), 0), (nameof(AabbGpu.Pad0), 12), (nameof(AabbGpu.MaxBounds), 16), (nameof(AabbGpu.Pad1), 28));
        ValidateType<BvhNodeGpu>(76, (nameof(BvhNodeGpu.LeftBounds), 0), (nameof(BvhNodeGpu.RightBounds), 32), (nameof(BvhNodeGpu.RightChildOrPrimIndex), 64), (nameof(BvhNodeGpu.PrimCount), 68), (nameof(BvhNodeGpu.SplitAxis), 72));
        ValidateType<PushDataGpu>(8, (nameof(PushDataGpu.Frame), 0), (nameof(PushDataGpu.IsMoving), 4));
        ValidateType<RenderSettingsDataGpu>(44, (nameof(RenderSettingsDataGpu.Samples), 0), (nameof(RenderSettingsDataGpu.DiffuseBounces), 4), (nameof(RenderSettingsDataGpu.SpecularBounces), 8), (nameof(RenderSettingsDataGpu.TransmissionBounces), 12), (nameof(RenderSettingsDataGpu.AdaptiveSamplingEnabled), 16), (nameof(RenderSettingsDataGpu.AdaptiveMinSamples), 20), (nameof(RenderSettingsDataGpu.AdaptiveTargetError), 24), (nameof(RenderSettingsDataGpu.RussianRouletteStartBounce), 28), (nameof(RenderSettingsDataGpu.Exposure), 32), (nameof(RenderSettingsDataGpu.TransparentBackground), 36), (nameof(RenderSettingsDataGpu.RenderMode), 40));
        ValidateType<EnvironmentDataGpu>(48, (nameof(EnvironmentDataGpu.TextureIndex), 0), (nameof(EnvironmentDataGpu.CdfTextureIndex), 4), (nameof(EnvironmentDataGpu.Rotation), 8), (nameof(EnvironmentDataGpu.VisibleExposure), 12), (nameof(EnvironmentDataGpu.LightingExposure), 16), (nameof(EnvironmentDataGpu.Visible), 20), (nameof(EnvironmentDataGpu.DirectionalDirection), 24), (nameof(EnvironmentDataGpu.DirectionalIntensity), 36), (nameof(EnvironmentDataGpu.Pad0), 40), (nameof(EnvironmentDataGpu.Pad1), 44));
        ValidateType<CameraDataGpu>(64, (nameof(CameraDataGpu.Position), 0), (nameof(CameraDataGpu.Aperture), 12), (nameof(CameraDataGpu.Direction), 16), (nameof(CameraDataGpu.FocusDistance), 28), (nameof(CameraDataGpu.Horizontal), 32), (nameof(CameraDataGpu.FocalLength), 44), (nameof(CameraDataGpu.Vertical), 48), (nameof(CameraDataGpu.BokehBias), 60));
        ValidateType<SceneSettingsDataGpu>(156, (nameof(SceneSettingsDataGpu.RenderSettings), 0), (nameof(SceneSettingsDataGpu.Camera), 44), (nameof(SceneSettingsDataGpu.Environment), 108));
    }

    private static void ValidateType<T>(int expectedSize, params (string Field, int Offset)[] offsets)
    {
        var actualSize = Marshal.SizeOf<T>();
        if (actualSize != expectedSize)
            throw new InvalidOperationException($"GPU layout mismatch for {typeof(T).Name}: expected size {expectedSize}, got {actualSize}.");

        foreach (var (field, expectedOffset) in offsets)
        {
            var actualOffset = Marshal.OffsetOf<T>(field).ToInt32();
            if (actualOffset != expectedOffset)
                throw new InvalidOperationException($"GPU layout mismatch for {typeof(T).Name}.{field}: expected offset {expectedOffset}, got {actualOffset}.");
        }
    }
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
}
