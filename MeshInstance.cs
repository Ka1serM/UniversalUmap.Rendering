using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace UniversalUmap.Rendering;

public sealed class MeshInstance
{
    private readonly IScene owner;
    public string Name { get; }
    public MeshAsset MeshAsset { get; }
    public Matrix4x4 Transform { get; private set; }

    internal MeshInstance(IScene owner, string name, MeshAsset meshAsset, Matrix4x4 transform)
    {
        this.owner = owner;
        Name = name;
        MeshAsset = meshAsset;
        Transform = transform;
    }

    internal static MeshInstance Create(IScene owner, string name, MeshAsset meshAsset, Matrix4x4 transform)
    {
        return new MeshInstance(owner, name, meshAsset, transform);
    }

    internal static bool TryCreate(IScene owner, string name, MeshAsset meshAsset, Matrix4x4 transform, out MeshInstance? instance)
    {
        try
        {
            instance = Create(owner, name, meshAsset, transform);
            return true;
        }
        catch
        {
            instance = null;
            return false;
        }
    }

    public void SetTransform(Matrix4x4 transform)
    {
        Transform = transform;
        owner.SetTlasDirty();
    }

    internal ComputeInstanceGpu BuildComputeInstanceData()
    {
        var transform = Transform;
        Matrix4x4.Invert(transform, out var inverse);

        return new ComputeInstanceGpu
        {
            Transform = transform,
            InverseTransform = inverse,
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
            InstanceCustomIndex = MeshAsset.MeshIndex,
            Mask = 0xFF,
            InstanceShaderBindingTableRecordOffset = 0,
            Flags = GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr
        };

        return instance;
    }
}
