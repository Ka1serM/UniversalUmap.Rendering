#ifndef SURFACE_DATA_SLANG
#define SURFACE_DATA_SLANG

#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Materials/PbrMaterial.slang"
#include "Intersection.slang"

struct SurfaceData
{
    float3 WorldPosition;
    float3 WorldShadowPosition;
    float3 ShadingNormal;
    float3 GeometricNormal;
    float3 Albedo;
    float3 Emission;
    float Metallic;
    float Specular;
    float Roughness;
    float Opacity;
    float Transmission;
    float Ior;
    float3 TransmissionColor;
};

SurfaceData loadComputeSurfaceData(uint instanceIndex, uint primitiveIndex, float3 barycentrics)
{
    SurfaceData surface;

    const InstanceGpu inst = instances[instanceIndex];
    const MeshAddressesGpu mesh = meshes[inst.MeshId];
    const Face face = reinterpret<FaceBuffer*>(mesh.FaceAddress)->data[primitiveIndex];
    const MaterialData material = reinterpret<MaterialBuffer*>(mesh.MaterialAddress)->data[face.MaterialIndex];

    const Vertex v0 = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[3 * primitiveIndex + 0]];
    const Vertex v1 = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[3 * primitiveIndex + 1]];
    const Vertex v2 = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[3 * primitiveIndex + 2]];

    float3 localPos = interpolateBarycentric(barycentrics, v0.Position, v1.Position, v2.Position);
    float3 localShadowPos = computeShadowTerminatorPointLocal(
        localPos,
        barycentrics,
        v0.Position,
        v1.Position,
        v2.Position,
        v0.Normal,
        v1.Normal,
        v2.Normal);
    float3 geometricNormalLocal = normalize(cross(v1.Position - v0.Position, v2.Position - v0.Position));
    float3 shadingNormalLocal = normalize(interpolateBarycentric(barycentrics, v0.Normal, v1.Normal, v2.Normal));
    float3 localTan = normalize(interpolateBarycentric(barycentrics, v0.Tangent, v1.Tangent, v2.Tangent));
    float tangentSign = dot(float3(v0.TangentSign, v1.TangentSign, v2.TangentSign), barycentrics);
    float2 uv = interpolateBarycentric(barycentrics, v0.UV, v1.UV, v2.UV);

    samplePbrMaterial(
        material,
        uv,
        surface.Albedo,
        surface.Emission,
        surface.Metallic,
        surface.Specular,
        surface.Roughness,
        surface.Opacity,
        surface.Transmission);
    surface.Ior = material.Ior;
    surface.TransmissionColor = material.TransmissionColor;

    surface.WorldPosition = mul(float4(localPos, 1.0), inst.Transform).xyz;
    surface.WorldShadowPosition = mul(float4(localShadowPos, 1.0), inst.Transform).xyz;

    float3x3 normalMatrix = (float3x3)inst.NormalTransform;
    surface.GeometricNormal = normalize(mul(geometricNormalLocal, normalMatrix));
    surface.ShadingNormal = sampleMaterialShadingNormal(
        material.NormalIndex,
        uv,
        normalize(mul(shadingNormalLocal, normalMatrix)),
        normalize(mul(localTan, normalMatrix)),
        tangentSign);

    return surface;
}

#ifdef USE_RTX
SurfaceData loadRayTracingSurfaceData(uint tlasInstanceIndex, uint primitiveIndex, float2 hitBary)
{
    SurfaceData surface;

    const float3 bary = float3(1.0 - hitBary.x - hitBary.y, hitBary.x, hitBary.y);
    const InstanceGpu inst = instances[tlasInstanceIndex];
    const MeshAddressesGpu mesh = meshes[inst.MeshId];

    const Face face = reinterpret<FaceBuffer*>(mesh.FaceAddress)->data[primitiveIndex];
    const MaterialData material = reinterpret<MaterialBuffer*>(mesh.MaterialAddress)->data[face.MaterialIndex];

    const Vertex v0 = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[3 * primitiveIndex + 0]];
    const Vertex v1 = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[3 * primitiveIndex + 1]];
    const Vertex v2 = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[3 * primitiveIndex + 2]];

    float3 localPosition = interpolateBarycentric(bary, v0.Position, v1.Position, v2.Position);
    float3 localShadowPosition = computeShadowTerminatorPointLocal(
        localPosition,
        bary,
        v0.Position,
        v1.Position,
        v2.Position,
        v0.Normal,
        v1.Normal,
        v2.Normal);
    float3 geometricNormalLocal = normalize(cross(v1.Position - v0.Position, v2.Position - v0.Position));
    float3 shadingNormalLocal = normalize(interpolateBarycentric(bary, v0.Normal, v1.Normal, v2.Normal));
    float3 localTangent = normalize(interpolateBarycentric(bary, v0.Tangent, v1.Tangent, v2.Tangent));
    float tangentSign = dot(float3(v0.TangentSign, v1.TangentSign, v2.TangentSign), bary);
    float2 uv = interpolateBarycentric(bary, v0.UV, v1.UV, v2.UV);

    samplePbrMaterial(
        material,
        uv,
        surface.Albedo,
        surface.Emission,
        surface.Metallic,
        surface.Specular,
        surface.Roughness,
        surface.Opacity,
        surface.Transmission);
    surface.Ior = material.Ior;
    surface.TransmissionColor = material.TransmissionColor;

    float4x3 objectToWorld = ObjectToWorld4x3();
    surface.WorldPosition = mul(float4(localPosition, 1.0), objectToWorld);
    surface.WorldShadowPosition = mul(float4(localShadowPosition, 1.0), objectToWorld);

    float3x3 normalMatrix = (float3x3)inst.NormalTransform;
    surface.GeometricNormal = normalize(mul(geometricNormalLocal, normalMatrix));
    surface.ShadingNormal = sampleMaterialShadingNormal(
        material.NormalIndex,
        uv,
        normalize(mul(shadingNormalLocal, normalMatrix)),
        normalize(mul(localTangent, normalMatrix)),
        tangentSign);

    return surface;
}
#endif

#endif // SURFACE_DATA_SLANG
