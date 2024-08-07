using System.Numerics;
using System.Runtime.InteropServices;
using CUE4Parse.UE4.Objects.Core.Math;

namespace UniversalUmap.Rendering.Resources;

[StructLayout(LayoutKind.Sequential)]
public struct AutoTextureMasks(Vector4 color, Vector4 metallic, Vector4 specular, Vector4 roughness, Vector4 ao, Vector4 normal, Vector4 emissive, Vector4 alpha)
{
    public Vector4 Color = color;
    public Vector4 Metallic = metallic;
    public Vector4 Specular = specular;
    public Vector4 Roughness = roughness;
    public Vector4 AO = ao;
    public Vector4 Normal = normal;
    public Vector4 Alpha = alpha;
    public Vector4 Emissive = emissive;
    public static uint SizeOf() => (uint)Marshal.SizeOf<AutoTextureMasks>();
}

[StructLayout(LayoutKind.Sequential)]
public struct Vertex(Vector3 position, Vector4 color, Vector3 normal, Vector3 tangent, Vector2 uv)
{
    private Vector3 Position = position;
    private Vector4 Color = color;
    private Vector3 Normal = normal;
    private Vector3 Tangent = tangent;
    private Vector2 UV = uv;
    public static uint SizeOf() => (uint)Marshal.SizeOf<Vertex>();
}

[StructLayout(LayoutKind.Sequential)]
public struct CameraUniform(Matrix4x4 projection, Matrix4x4 view, Vector4 front)
{
    private Matrix4x4 Projection = projection;
    private Matrix4x4 View = view;
    private Vector4 Front = front;
    public static uint SizeOf() => (uint)Marshal.SizeOf<CameraUniform>();
}

[StructLayout(LayoutKind.Sequential)]
public struct InstanceInfo
{
    public Matrix4x4 Matrix;
    
    public InstanceInfo(FTransform transform)
    {
        Matrix = Matrix4x4.CreateScale(transform.Scale3D.X, transform.Scale3D.Z, transform.Scale3D.Y) *
                    Matrix4x4.CreateFromQuaternion(new Quaternion(transform.Rotation.X, transform.Rotation.Z, transform.Rotation.Y, -transform.Rotation.W)) *
                    Matrix4x4.CreateTranslation(transform.Translation.X, transform.Translation.Z, transform.Translation.Y);
    }
    public static uint SizeOf() => (uint)Marshal.SizeOf<InstanceInfo>();
}