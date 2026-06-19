using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CUE4Parse.UE4.Objects.Core.Math;
using Silk.NET.Vulkan;
using UniversalUmap.Rendering.Vulkan;

namespace UniversalUmap.Rendering.Scenes;

public sealed class MeshInstance : SceneObject
{
    private const float UnrealToRendererScale = 0.01f;
    private static readonly Matrix4x4 UnrealToRendererBasis = new()
    {
        M11 = 0f,
        M21 = 1f,
        M31 = 0f,
        M41 = 0f,
        M12 = 0f,
        M22 = 0f,
        M32 = -1f,
        M42 = 0f,
        M13 = 1f,
        M23 = 0f,
        M33 = 0f,
        M43 = 0f,
        M14 = 0f,
        M24 = 0f,
        M34 = 0f,
        M44 = 1f
    };

    private static readonly Matrix4x4 RendererToUnrealBasis = Matrix4x4.Transpose(UnrealToRendererBasis);

    private IScene? owner;
    public MeshAsset MeshAsset { get; }
    public int HierarchyNodeId { get; }
    public Matrix4x4 Transform { get; private set; }

    public MeshInstance(MeshAsset meshAsset, string? name = null, Matrix4x4? transform = null)
        : base(name ?? (meshAsset ?? throw new ArgumentNullException(nameof(meshAsset))).Name)
    {
        MeshAsset = meshAsset;
        Transform = transform ?? Matrix4x4.Identity;
        HierarchyNodeId = -1;
    }

    internal MeshInstance(IScene owner, string name, MeshAsset meshAsset, Matrix4x4 transform, int hierarchyNodeId = -1)
        : base(name)
    {
        this.owner = owner;
        MeshAsset = meshAsset;
        Transform = transform;
        HierarchyNodeId = hierarchyNodeId;
    }

    internal static MeshInstance Create(IScene owner, string name, MeshAsset meshAsset, Matrix4x4 transform, int hierarchyNodeId = -1)
    {
        return new MeshInstance(owner, name, meshAsset, transform, hierarchyNodeId);
    }

    internal static bool TryCreate(IScene owner, string name, MeshAsset meshAsset, Matrix4x4 transform, out MeshInstance? instance, int hierarchyNodeId = -1)
    {
        try
        {
            instance = Create(owner, name, meshAsset, transform, hierarchyNodeId);
            return true;
        }
        catch
        {
            instance = null;
            return false;
        }
    }

    internal void SetOwner(IScene scene) => owner ??= scene;

    public static bool TryConvertUnrealTransform(FTransform transform, out Matrix4x4 matrix)
    {
        matrix = default;
        if (!IsFinite(transform.Translation.X) || !IsFinite(transform.Translation.Y) || !IsFinite(transform.Translation.Z))
            return false;
        if (!IsFinite(transform.Scale3D.X) || !IsFinite(transform.Scale3D.Y) || !IsFinite(transform.Scale3D.Z))
            return false;
        if (!IsFinite(transform.Rotation.X) || !IsFinite(transform.Rotation.Y) || !IsFinite(transform.Rotation.Z) || !IsFinite(transform.Rotation.W))
            return false;

        var scale = Matrix4x4.CreateScale(transform.Scale3D.X, transform.Scale3D.Y, transform.Scale3D.Z);
        var rotation = Matrix4x4.CreateFromQuaternion(new Quaternion(
            transform.Rotation.X,
            transform.Rotation.Y,
            transform.Rotation.Z,
            transform.Rotation.W));
        var translation = Matrix4x4.CreateTranslation(
            transform.Translation.X * UnrealToRendererScale,
            transform.Translation.Y * UnrealToRendererScale,
            transform.Translation.Z * UnrealToRendererScale);
        var unrealMatrix = scale * rotation * translation;

        var rendererMatrix = RendererToUnrealBasis * unrealMatrix * UnrealToRendererBasis;
        if (!IsFiniteMatrix(rendererMatrix))
            return false;

        matrix = Matrix4x4.Transpose(rendererMatrix);
        return IsFiniteMatrix(matrix);
    }

    internal static bool TryCreateFromUnrealTransform(IScene owner, string name, MeshAsset meshAsset, FTransform transform, out MeshInstance? instance, int hierarchyNodeId = -1)
    {
        instance = null;
        if (!TryConvertUnrealTransform(transform, out var matrix))
            return false;

        return TryCreate(owner, name, meshAsset, matrix, out instance, hierarchyNodeId);
    }

    public void SetTransform(Matrix4x4 transform)
    {
        Transform = transform;
        owner?.SetTlasDirty();
    }

    internal InstanceGpu BuildInstanceData()
    {
        var transform = Transform;
        Matrix4x4.Invert(transform, out var inverse);
        var normalTransform = Matrix4x4.Transpose(inverse);

        return new InstanceGpu
        {
            Transform = transform,
            InverseTransform = inverse,
            NormalTransform = normalTransform,
            MeshId = MeshAsset.MeshIndex
        };
    }

    internal unsafe AccelerationStructureInstanceKHR BuildRtxInstanceData()
    {
        var m = Transform;
        var transformValues = stackalloc float[12]
        {
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34
        };

        var vkTransform = default(TransformMatrixKHR);
        System.Buffer.MemoryCopy(transformValues, &vkTransform, sizeof(float) * 12, sizeof(float) * 12);

        var instance = new AccelerationStructureInstanceKHR
        {
            Transform = vkTransform,
            AccelerationStructureReference = MeshAsset.GetBlasAddress(),
            InstanceCustomIndex = 0,
            Mask = 0xFF,
            InstanceShaderBindingTableRecordOffset = 0,
            Flags = GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr
        };

        return instance;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static bool IsFiniteMatrix(Matrix4x4 matrix)
    {
        return IsFinite(matrix.M11) && IsFinite(matrix.M12) && IsFinite(matrix.M13) && IsFinite(matrix.M14) &&
               IsFinite(matrix.M21) && IsFinite(matrix.M22) && IsFinite(matrix.M23) && IsFinite(matrix.M24) &&
               IsFinite(matrix.M31) && IsFinite(matrix.M32) && IsFinite(matrix.M33) && IsFinite(matrix.M34) &&
               IsFinite(matrix.M41) && IsFinite(matrix.M42) && IsFinite(matrix.M43) && IsFinite(matrix.M44);
    }
}
