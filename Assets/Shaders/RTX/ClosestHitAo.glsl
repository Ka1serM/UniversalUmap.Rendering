#version 460
#pragma shader_stage(closest)

#extension GL_EXT_ray_tracing : enable
#extension GL_EXT_nonuniform_qualifier : enable
#extension GL_EXT_buffer_reference : require
#extension GL_EXT_scalar_block_layout : enable

#include "../SharedStructs.h"
#include "../Bindings.glsl"

layout(location = 0) rayPayloadInEXT AoPayload payload;
layout(location = 1) rayPayloadEXT uint shadowPayload;
hitAttributeEXT vec3 attribs;

layout (push_constant, scalar) uniform PushConstants {
    PushData pushConstants;
};

bool traceShadowRay(vec3 rayOrigin, vec3 rayDirection, float tMin, float tMax) {
   shadowPayload = 1u;
   const uint shadowFlags = gl_RayFlagsTerminateOnFirstHitEXT
       | gl_RayFlagsOpaqueEXT
       | gl_RayFlagsSkipClosestHitShaderEXT;
   traceRayEXT(topLevelAS, shadowFlags, 0xFF, 0, 0, 1, rayOrigin, tMin, rayDirection, tMax, 1);
   return shadowPayload != 0u;
}

vec3 computeShadowTerminatorPointLocal(
    vec3 localPos,
    vec3 bary,
    vec3 v0Pos, vec3 v1Pos, vec3 v2Pos,
    vec3 v0Normal, vec3 v1Normal, vec3 v2Normal)
{
   vec3 n0 = normalize(v0Normal);
   vec3 n1 = normalize(v1Normal);
   vec3 n2 = normalize(v2Normal);

   vec3 tmp0 = localPos - v0Pos;
   vec3 tmp1 = localPos - v1Pos;
   vec3 tmp2 = localPos - v2Pos;

   float d0 = min(0.0, dot(tmp0, n0));
   float d1 = min(0.0, dot(tmp1, n1));
   float d2 = min(0.0, dot(tmp2, n2));

   tmp0 -= d0 * n0;
   tmp1 -= d1 * n1;
   tmp2 -= d2 * n2;

   return localPos + bary.x * tmp0 + bary.y * tmp1 + bary.z * tmp2;
}

#include "../PathTracing/ShadeClosestHit.glsl"

void main() {
   const MeshAddresses mesh = meshes[gl_InstanceCustomIndexEXT];
   VertexBuffer vertexBuf = VertexBuffer(mesh.vertexAddress);
   IndexBuffer indexBuf = IndexBuffer(mesh.indexAddress);
   FaceBuffer faceBuf = FaceBuffer(mesh.faceAddress);
   MaterialBuffer materialBuf = MaterialBuffer(mesh.materialAddress);

   const Face face = faceBuf.data[gl_PrimitiveID];
   const Material material = materialBuf.data[face.materialIndex];

   const uint i0 = indexBuf.data[3 * gl_PrimitiveID + 0];
   const uint i1 = indexBuf.data[3 * gl_PrimitiveID + 1];
   const uint i2 = indexBuf.data[3 * gl_PrimitiveID + 2];

   const Vertex v0 = vertexBuf.data[i0], v1 = vertexBuf.data[i1], v2 = vertexBuf.data[i2];
   const vec3 bary = calculateBarycentric(attribs);

   vec3 localPosition = interpolateBarycentric(bary, v0.position, v1.position, v2.position);
   vec3 localShadowPosition = computeShadowTerminatorPointLocal(
      localPosition,
      bary,
      v0.position, v1.position, v2.position,
      v0.normal, v1.normal, v2.normal);
   vec3 geometricNormalLocal = normalize(cross(v1.position - v0.position, v2.position - v0.position));
   vec3 shadingNormalLocal = normalize(interpolateBarycentric(bary, v0.normal, v1.normal, v2.normal));
   vec3 localTangent = normalize(interpolateBarycentric(bary, v0.tangent, v1.tangent, v2.tangent));
   vec2 interpolatedUV = interpolateBarycentric(bary, v0.uv, v1.uv, v2.uv);

   vec3 worldPosition = (gl_ObjectToWorldEXT * vec4(localPosition, 1.0)).xyz;
   vec3 worldShadowPosition = (gl_ObjectToWorldEXT * vec4(localShadowPosition, 1.0)).xyz;
   mat3 normalMatrix = transpose(inverse(mat3(gl_ObjectToWorldEXT)));

   vec3 geometricNormalWorld = normalize(normalMatrix * geometricNormalLocal);
   vec3 shadingNormalWorld = normalize(normalMatrix * shadingNormalLocal);
   vec3 tangentWorld = normalize(normalMatrix * localTangent);

   shadeClosestHit(worldPosition, worldShadowPosition, shadingNormalWorld, geometricNormalWorld, tangentWorld, interpolatedUV, gl_WorldRayDirectionEXT, material, payload);
   payload.objectIndex = uint(gl_InstanceID);
}
