#include "ClosestHitShared.glsl"

layout(location = 0) rayPayloadInEXT Payload payload;
#include "../PathTracing/ShadeClosestHitDirect.glsl"

void main() {
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
   mat3 normalMatrix = transpose(mat3(gl_WorldToObjectEXT));

   vec3 geometricNormalWorld = normalize(normalMatrix * geometricNormalLocal);
   vec3 shadingNormalWorld = normalize(normalMatrix * shadingNormalLocal);
   vec3 tangentWorld = normalize(normalMatrix * localTangent);

   shadeClosestHit(worldPosition, worldShadowPosition, shadingNormalWorld, geometricNormalWorld, tangentWorld, interpolatedUV, gl_WorldRayDirectionEXT, material, payload);
   payload.objectIndex = uint(gl_InstanceID);
}
