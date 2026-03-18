#ifndef SURFACE_DATA_SLANG
#define SURFACE_DATA_SLANG

#include "../SharedStructs.slang"
#include "../Bindings.slang"
#include "Intersection.slang"

struct SurfaceData
{
    float3 worldPosition;
    float3 worldShadowPosition;
    float3 shadingNormal;
    float3 geometricNormal;
    float3 tangent;
    float tangentSign;
    float2 uv;
    MaterialData material;
};

#ifdef USE_COMPUTE
SurfaceData loadComputeSurfaceData(uint instanceIndex, uint primitiveIndex, float3 barycentrics)
{
    SurfaceData surface;

    const InstanceGpu inst = instances[instanceIndex];
    const MeshAddressesGpu mesh = meshes[inst.meshId];
    const Face face = reinterpret<FaceBuffer*>(mesh.faceAddress)->data[primitiveIndex];
    surface.material = reinterpret<MaterialBuffer*>(mesh.materialAddress)->data[face.materialIndex];

    const Vertex v0 = reinterpret<VertexBuffer*>(mesh.vertexAddress)->data[reinterpret<IndexBuffer*>(mesh.indexAddress)->data[3 * primitiveIndex + 0]];
    const Vertex v1 = reinterpret<VertexBuffer*>(mesh.vertexAddress)->data[reinterpret<IndexBuffer*>(mesh.indexAddress)->data[3 * primitiveIndex + 1]];
    const Vertex v2 = reinterpret<VertexBuffer*>(mesh.vertexAddress)->data[reinterpret<IndexBuffer*>(mesh.indexAddress)->data[3 * primitiveIndex + 2]];

    float3 localPos = interpolateBarycentric(barycentrics, v0.position, v1.position, v2.position);
    float3 localShadowPos = computeShadowTerminatorPointLocal(
        localPos,
        barycentrics,
        v0.position,
        v1.position,
        v2.position,
        v0.normal,
        v1.normal,
        v2.normal);
    float3 geometricNormalLocal = normalize(cross(v1.position - v0.position, v2.position - v0.position));
    float3 shadingNormalLocal = normalize(interpolateBarycentric(barycentrics, v0.normal, v1.normal, v2.normal));
    float3 localTan = normalize(interpolateBarycentric(barycentrics, v0.tangent, v1.tangent, v2.tangent));

    surface.tangentSign = dot(float3(v0.tangentSign, v1.tangentSign, v2.tangentSign), barycentrics);
    surface.uv = interpolateBarycentric(barycentrics, v0.uv, v1.uv, v2.uv);

    surface.worldPosition = mul(float4(localPos, 1.0), inst.transform).xyz;
    surface.worldShadowPosition = mul(float4(localShadowPos, 1.0), inst.transform).xyz;

    float3x3 normalMatrix = (float3x3)inst.normalTransform;
    surface.geometricNormal = normalize(mul(geometricNormalLocal, normalMatrix));
    surface.shadingNormal = normalize(mul(shadingNormalLocal, normalMatrix));
    surface.tangent = normalize(mul(localTan, normalMatrix));

    return surface;
}
#endif

#ifndef USE_COMPUTE
SurfaceData loadRayTracingSurfaceData(uint tlasInstanceIndex, uint primitiveIndex, float2 hitBary)
{
    SurfaceData surface;

    const float3 bary = float3(1.0 - hitBary.x - hitBary.y, hitBary.x, hitBary.y);
    const InstanceGpu inst = rtxInstances[tlasInstanceIndex];
    const MeshAddressesGpu mesh = meshes[inst.meshId];

    const Face face = reinterpret<FaceBuffer*>(mesh.faceAddress)->data[primitiveIndex];
    surface.material = reinterpret<MaterialBuffer*>(mesh.materialAddress)->data[face.materialIndex];

    const Vertex v0 = reinterpret<VertexBuffer*>(mesh.vertexAddress)->data[reinterpret<IndexBuffer*>(mesh.indexAddress)->data[3 * primitiveIndex + 0]];
    const Vertex v1 = reinterpret<VertexBuffer*>(mesh.vertexAddress)->data[reinterpret<IndexBuffer*>(mesh.indexAddress)->data[3 * primitiveIndex + 1]];
    const Vertex v2 = reinterpret<VertexBuffer*>(mesh.vertexAddress)->data[reinterpret<IndexBuffer*>(mesh.indexAddress)->data[3 * primitiveIndex + 2]];

    float3 localPosition = interpolateBarycentric(bary, v0.position, v1.position, v2.position);
    float3 localShadowPosition = computeShadowTerminatorPointLocal(
        localPosition,
        bary,
        v0.position,
        v1.position,
        v2.position,
        v0.normal,
        v1.normal,
        v2.normal);
    float3 geometricNormalLocal = normalize(cross(v1.position - v0.position, v2.position - v0.position));
    float3 shadingNormalLocal = normalize(interpolateBarycentric(bary, v0.normal, v1.normal, v2.normal));
    float3 localTangent = normalize(interpolateBarycentric(bary, v0.tangent, v1.tangent, v2.tangent));

    surface.tangentSign = dot(float3(v0.tangentSign, v1.tangentSign, v2.tangentSign), bary);
    surface.uv = interpolateBarycentric(bary, v0.uv, v1.uv, v2.uv);

    float4x3 objectToWorld = ObjectToWorld4x3();
    surface.worldPosition = mul(float4(localPosition, 1.0), objectToWorld);
    surface.worldShadowPosition = mul(float4(localShadowPosition, 1.0), objectToWorld);

    float3x3 normalMatrix = (float3x3)inst.normalTransform;
    surface.geometricNormal = normalize(mul(geometricNormalLocal, normalMatrix));
    surface.shadingNormal = normalize(mul(shadingNormalLocal, normalMatrix));
    surface.tangent = normalize(mul(localTangent, normalMatrix));

    return surface;
}
#endif

#endif // SURFACE_DATA_SLANG
