#version 460
#ifndef DIRECTLIGHTING_PIPELINE_GLSL
#define DIRECTLIGHTING_PIPELINE_GLSL

#pragma shader_stage(compute)

#extension GL_EXT_debug_printf : enable
#extension GL_EXT_nonuniform_qualifier: enable
#extension GL_EXT_buffer_reference: require
#extension GL_EXT_scalar_block_layout: enable

#include "../SharedStructs.h"

layout(local_size_x = GROUP_SIZE, local_size_y = GROUP_SIZE) in;

layout(push_constant, scalar) uniform PushConstants {
    PushDataGpu pushConstants;
};

#include "../Common/PrimaryRay.glsl"
#include "../Common/AdaptiveSampling.glsl"
#include "../Common/PayloadUtils.glsl"
#include "DirectLightingCommon.glsl"

void traceRayComputeDirect(vec3 rayOrigin, vec3 rayDirection, float tMin, float tMax, inout Payload payload)
{
    HitInfo hit = traceScene(rayOrigin, rayDirection, tMin, tMax);

    if (hit.instanceIndex == INVALID_INSTANCE) {
        shadePrimaryMissDirect(rayDirection, payload);
        return;
    }

    const ComputeInstanceGpu inst = instances[hit.instanceIndex];
    const MeshAddressesGpu mesh = meshes[inst.meshId];
    const Face face = FaceBuffer(mesh.faceAddress).data[hit.primitiveIndex];
    const MaterialData material = MaterialBuffer(mesh.materialAddress).data[face.materialIndex];

    IndexBuffer indexBuf = IndexBuffer(mesh.indexAddress);
    VertexBuffer vertexBuf = VertexBuffer(mesh.vertexAddress);

    const uint i0 = indexBuf.data[3 * hit.primitiveIndex + 0];
    const uint i1 = indexBuf.data[3 * hit.primitiveIndex + 1];
    const uint i2 = indexBuf.data[3 * hit.primitiveIndex + 2];

    const Vertex v0 = vertexBuf.data[i0];
    const Vertex v1 = vertexBuf.data[i1];
    const Vertex v2 = vertexBuf.data[i2];

    vec3 localPos = interpolateBarycentric(hit.barycentrics, v0.position, v1.position, v2.position);
    vec3 localShadowPos = computeShadowTerminatorPointLocal(
        localPos, hit.barycentrics,
        v0.position, v1.position, v2.position,
        v0.normal, v1.normal, v2.normal);
    vec3 geometricNormalLocal = normalize(cross(v1.position - v0.position, v2.position - v0.position));
    vec3 shadingNormalLocal = normalize(interpolateBarycentric(hit.barycentrics, v0.normal, v1.normal, v2.normal));
    vec3 localTan = normalize(interpolateBarycentric(hit.barycentrics, v0.tangent, v1.tangent, v2.tangent));
    float tangentSign = dot(vec3(v0.tangentSign, v1.tangentSign, v2.tangentSign), hit.barycentrics);
    vec2 uv = interpolateBarycentric(hit.barycentrics, v0.uv, v1.uv, v2.uv);

    vec3 worldPos = (inst.transform * vec4(localPos, 1.0)).xyz;
    vec3 worldShadowPos = (inst.transform * vec4(localShadowPos, 1.0)).xyz;
    mat3 normalMatrix = transpose(mat3(inst.inverseTransform));

    vec3 geometricNormalWorld = normalize(normalMatrix * geometricNormalLocal);
    vec3 shadingNormalWorld = normalize(normalMatrix * shadingNormalLocal);
    vec3 tangentWorld = normalize(normalMatrix * localTan);

    shadeClosestHitDirect(
        worldPos,
        worldShadowPos,
        shadingNormalWorld,
        geometricNormalWorld,
        tangentWorld,
        tangentSign,
        uv,
        rayDirection,
        material,
        payload);

    payload.objectIndex = hit.instanceIndex;
}

#include "DirectLightingPrimary.glsl"

void main()
{
    const ivec2 pixelCoord = ivec2(gl_GlobalInvocationID.xy);
    const ivec2 screenSize = imageSize(outputColor);
    primaryRayGenDirect(pixelCoord, screenSize);
}

#endif // DIRECTLIGHTING_PIPELINE_GLSL
