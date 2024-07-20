using System.Numerics;
using System.Runtime.InteropServices;
using CUE4Parse.UE4.Objects.Core.Math;
using Veldrid;

namespace UniversalUmap.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct Vertex(Vector3 position, Vector3 color, Vector3 normal)
{
    private Vector3 Position = position;
    private Vector3 Color = color;
    private Vector3 Normal = normal;
    public static uint SizeOf() => (uint)Marshal.SizeOf<Vertex>();
}

[StructLayout(LayoutKind.Sequential)]
public struct CameraUniform(Matrix4x4 projection, Matrix4x4 view, Vector4 front)
{
    private Matrix4x4 projection = projection;       // 64
    private Matrix4x4 view = view;             // 64
    private Vector4 front = front;
    public static uint SizeOf() => (uint)Marshal.SizeOf<CameraUniform>();
}

[StructLayout(LayoutKind.Sequential)]
public struct InstanceInfo
{
    public Matrix4x4 Transform;
    
    public InstanceInfo(FTransform transform)
    {
        Transform = Matrix4x4.CreateScale(transform.Scale3D.X, transform.Scale3D.Z, transform.Scale3D.Y) *
                    Matrix4x4.CreateFromQuaternion(new Quaternion(transform.Rotation.X, transform.Rotation.Z, transform.Rotation.Y, -transform.Rotation.W)) *
                    Matrix4x4.CreateTranslation(transform.Translation.X, transform.Translation.Z, transform.Translation.Y);
    }
    public static uint SizeOf() => (uint)Marshal.SizeOf<InstanceInfo>();
}