#version 460
#ifndef DIRECTLIGHTING_CLOSESTHIT_GLSL
#define DIRECTLIGHTING_CLOSESTHIT_GLSL

#pragma shader_stage(closest)

#extension GL_EXT_ray_tracing : enable
#extension GL_EXT_nonuniform_qualifier : enable
#extension GL_EXT_buffer_reference : require
#extension GL_EXT_scalar_block_layout: enable

#include "../SharedStructs.h"

layout(push_constant, scalar) uniform PushConstants {
    PushDataGpu pushConstants;
};

layout(location = 0) rayPayloadInEXT Payload payload;
layout(location = 1) rayPayloadEXT uint shadowPayload;

#include "DirectLightingCommon.glsl"

hitAttributeEXT vec3 attribs;

void main()
{
    const MeshAddressesGpu mesh = meshes[gl_InstanceCustomIndexEXT];
    VertexBuffer vertexBuf = VertexBuffer(mesh.vertexAddress);
    IndexBuffer indexBuf = IndexBuffer(mesh.indexAddress);
    FaceBuffer faceBuf = FaceBuffer(mesh.faceAddress);
    MaterialBuffer materialBuf = MaterialBuffer(mesh.materialAddress);

    const Face face = faceBuf.data[gl_PrimitiveID];
    const MaterialData material = materialBuf.data[face.materialIndex];

    const uint i0 = indexBuf.data[3 * gl_PrimitiveID + 0];
    const uint i1 = indexBuf.data[3 * gl_PrimitiveID + 1];
    const uint i2 = indexBuf.data[3 * gl_PrimitiveID + 2];

    const Vertex v0 = vertexBuf.data[i0];
    const Vertex v1 = vertexBuf.data[i1];
    const Vertex v2 = vertexBuf.data[i2];
    const vec3 bary = calculateBarycentric(attribs);

    vec3 localPosition = interpolateBarycentric(bary, v0.position, v1.position, v2.position);
    vec3 localShadowPosition = computeShadowTerminatorPointLocal(
        localPosition, bary,
        v0.position, v1.position, v2.position,
        v0.normal, v1.normal, v2.normal);
    vec3 geometricNormalLocal = normalize(cross(v1.position - v0.position, v2.position - v0.position));
    vec3 shadingNormalLocal = normalize(interpolateBarycentric(bary, v0.normal, v1.normal, v2.normal));
    vec3 localTangent = normalize(interpolateBarycentric(bary, v0.tangent, v1.tangent, v2.tangent));
    float tangentSign = dot(vec3(v0.tangentSign, v1.tangentSign, v2.tangentSign), bary);
    vec2 interpolatedUV = interpolateBarycentric(bary, v0.uv, v1.uv, v2.uv);

    vec3 worldPosition = (gl_ObjectToWorldEXT * vec4(localPosition, 1.0)).xyz;
    vec3 worldShadowPosition = (gl_ObjectToWorldEXT * vec4(localShadowPosition, 1.0)).xyz;
    mat3 normalMatrix = transpose(mat3(gl_WorldToObjectEXT));

    vec3 geometricNormalWorld = normalize(normalMatrix * geometricNormalLocal);
    vec3 shadingNormalWorld = normalize(normalMatrix * shadingNormalLocal);
    vec3 tangentWorld = normalize(normalMatrix * localTangent);

    shadeClosestHitDirect(
        worldPosition,
        worldShadowPosition,
        shadingNormalWorld,
        geometricNormalWorld,
        tangentWorld,
        tangentSign,
        interpolatedUV,
        gl_WorldRayDirectionEXT,
        material,
        payload);

    payload.objectIndex = uint(gl_InstanceID);
}

#endif // DIRECTLIGHTING_CLOSESTHIT_GLSL
