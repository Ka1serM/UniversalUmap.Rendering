#include "IntersectionShared.glsl"
#include "ShadeClosestHitAo.glsl"

void traceRayCompute(vec3 rayOrigin, vec3 rayDirection, float tMin, float tMax, inout AoPayload payload) {
    HitInfo hit = traceScene(rayOrigin, rayDirection, tMin, tMax);

    if (hit.instanceIndex == INVALID_INSTANCE)
        shadeMiss(rayDirection, sceneSettings.environment, sceneSettings.renderSettings, payload);
    else {
        const ComputeInstanceGpu inst = instances[hit.instanceIndex];
        const MeshAddressesGpu mesh = meshes[inst.meshId];
        const Face face = FaceBuffer(mesh.faceAddress).data[hit.primitiveIndex];
        const MaterialData material = MaterialBuffer(mesh.materialAddress).data[face.materialIndex];

        const Vertex v0 = VertexBuffer(mesh.vertexAddress).data[IndexBuffer(mesh.indexAddress).data[3 * hit.primitiveIndex + 0]];
        const Vertex v1 = VertexBuffer(mesh.vertexAddress).data[IndexBuffer(mesh.indexAddress).data[3 * hit.primitiveIndex + 1]];
        const Vertex v2 = VertexBuffer(mesh.vertexAddress).data[IndexBuffer(mesh.indexAddress).data[3 * hit.primitiveIndex + 2]];

        vec3 localPos = interpolateBarycentric(hit.barycentrics, v0.position, v1.position, v2.position);
        vec3 localShadowPos = computeShadowTerminatorPointLocal(
            localPos,
            hit.barycentrics,
            v0.position, v1.position, v2.position,
            v0.normal, v1.normal, v2.normal);
        vec3 geometricNormalLocal = normalize(cross(v1.position - v0.position, v2.position - v0.position));
        vec3 shadingNormalLocal = normalize(interpolateBarycentric(hit.barycentrics, v0.normal, v1.normal, v2.normal));
        vec3 localTan = normalize(interpolateBarycentric(hit.barycentrics, v0.tangent, v1.tangent, v2.tangent));
        vec2 uv = interpolateBarycentric(hit.barycentrics, v0.uv, v1.uv, v2.uv);

        vec3 worldPos = (inst.transform * vec4(localPos, 1.0)).xyz;
        vec3 worldShadowPos = (inst.transform * vec4(localShadowPos, 1.0)).xyz;
        mat3 normalMatrix = transpose(mat3(inst.inverseTransform));

        vec3 geometricNormalWorld = normalize(normalMatrix * geometricNormalLocal);
        vec3 shadingNormalWorld = normalize(normalMatrix * shadingNormalLocal);
        vec3 worldTan = normalize(normalMatrix * localTan);

        shadeClosestHit(worldPos, worldShadowPos, shadingNormalWorld, geometricNormalWorld, worldTan, uv, rayDirection, material, payload);
        payload.objectIndex = hit.instanceIndex;
    }
}
