// Intersection utilities for compute-based ray tracing
// When COMPUTE is defined, uses software BVH traversal
// When COMPUTE is not defined, uses hardware RTX via traceRayEXT
#ifndef INTERSECTION_GLSL
#define INTERSECTION_GLSL

#include "../SharedStructs.h"
#include "../Bindings.glsl"
#include "../Common.glsl"
#include "Miss.glsl"

#ifndef PRIMARY_MISS_INDEX
    #define PRIMARY_MISS_INDEX 0
#endif
#ifndef SHADOW_MISS_INDEX
    #define SHADOW_MISS_INDEX 1
#endif

#ifdef USE_COMPUTE
    #define BVH_STACK_DEPTH 512

    bool intersectTriangle(vec3 rayOrigin, vec3 rayDirection, vec3 v0, vec3 v1, vec3 v2, inout float t, out vec3 bary, float tMin) {
        const float EPSILON = 1e-8;

        vec3 e1 = v1 - v0;
        vec3 e2 = v2 - v0;

        vec3 pvec = cross(rayDirection, e2);
        float det = dot(e1, pvec);

        if (abs(det) < EPSILON)
            return false;

        float invDet = 1.0 / det;
        vec3 tvec = rayOrigin - v0;

        float u = dot(tvec, pvec) * invDet;
        vec3 qvec = cross(tvec, e1);
        float v = dot(rayDirection, qvec) * invDet;

        if (u < 0.0 || v < 0.0 || u + v > 1.0)
            return false;

        float current_t = dot(e2, qvec) * invDet;

        if (current_t > tMin && current_t < t) {
            t = current_t;
            bary = vec3(1.0 - u - v, u, v);
            return true;
        }

        return false;
    }

    bool intersectAABB(vec3 rayOrigin, vec3 invDir, AabbGpu box, out float tmin, out float tmax) {
        const float EPSILON = 1e-6;

        vec3 t0s = (box.minBounds - rayOrigin) * invDir;
        vec3 t1s = (box.maxBounds - rayOrigin) * invDir;

        vec3 tsmaller = min(t0s, t1s);
        vec3 tbigger  = max(t0s, t1s);

        tmin = max(tsmaller.x, max(tsmaller.y, tsmaller.z));
        tmax = min(tbigger.x, min(tbigger.y, tbigger.z));

        return tmin <= tmax + EPSILON && tmax >= 0.0;
    }

    void traverseBVH(vec3 rayOrigin, vec3 rayDirection, MeshAddressesGpu mesh, inout HitInfo hit, float tMin) {
        vec3 invDir = 1.0 / rayDirection;

        BVHNodeBuffer bvh = BVHNodeBuffer(mesh.bvhNodeAddress);
        BVHIndexBuffer bvhIndices = BVHIndexBuffer(mesh.bvhIndexAddress);
        VertexBuffer vertices = VertexBuffer(mesh.vertexAddress);
        IndexBuffer indices = IndexBuffer(mesh.indexAddress);

        uint stack[BVH_STACK_DEPTH];
        uint stackPtr = 0;
        stack[stackPtr++] = 0;

        while (stackPtr > 0) {
            uint nodeIndex = stack[--stackPtr];
            BvhNodeGpu node = bvh.data[nodeIndex];

            if (node.primCount > 0) {
                for (uint i = 0; i < node.primCount; ++i) {
                    uint primIdx = bvhIndices.data[node.rightChildOrPrimIndex + i];

                    uint i0 = indices.data[3 * primIdx + 0];
                    uint i1 = indices.data[3 * primIdx + 1];
                    uint i2 = indices.data[3 * primIdx + 2];

                    vec3 v0 = vertices.data[i0].position;
                    vec3 v1 = vertices.data[i1].position;
                    vec3 v2 = vertices.data[i2].position;

                    vec3 currentBary;
                    if (intersectTriangle(rayOrigin, rayDirection, v0, v1, v2, hit.t, currentBary, tMin)) {
                        hit.primitiveIndex = int(primIdx);
                        hit.barycentrics = currentBary;
                    }
                }
                continue;
            }

            float tmin_left, tmax_left, tmin_right, tmax_right;
            bool hit_left = intersectAABB(rayOrigin, invDir, node.leftBounds, tmin_left, tmax_left);
            bool hit_right = intersectAABB(rayOrigin, invDir, node.rightBounds, tmin_right, tmax_right);

            hit_left = hit_left && tmin_left < hit.t && tmax_left > tMin;
            hit_right = hit_right && tmin_right < hit.t && tmax_right > tMin;

            if (!hit_left && !hit_right) continue;

            uint leftChildIndex = nodeIndex + 1;
            uint rightChildIndex = node.rightChildOrPrimIndex;

            if (hit_left && hit_right) {
                if (tmin_left < tmin_right) {
                    stack[stackPtr++] = rightChildIndex;
                    stack[stackPtr++] = leftChildIndex;
                } else {
                    stack[stackPtr++] = leftChildIndex;
                    stack[stackPtr++] = rightChildIndex;
                }
            } else if (hit_left) {
                stack[stackPtr++] = leftChildIndex;
            } else if (hit_right) {
                stack[stackPtr++] = rightChildIndex;
            }
        }
    }

    HitInfo traceScene(vec3 rayOrigin, vec3 rayDirection, float tMin, float tMax) {
        HitInfo bestHit;
        bestHit.t = tMax;
        bestHit.instanceIndex = INVALID_INSTANCE;
        bestHit.primitiveIndex = INVALID_INSTANCE;

        for (int i = 0; i < instances.length(); ++i) {
            ComputeInstanceGpu inst = instances[i];

            if (inst.meshId == 0xFFFFFFFF)
                continue;

            vec3 localOrigin = (inst.inverseTransform * vec4(rayOrigin, 1.0)).xyz;
            vec3 localDir = (inst.inverseTransform * vec4(rayDirection, 0.0)).xyz;

            HitInfo localHit;
            localHit.t = bestHit.t;
            localHit.primitiveIndex = INVALID_INSTANCE;

            MeshAddressesGpu mesh = meshes[inst.meshId];
            traverseBVH(localOrigin, localDir, mesh, localHit, tMin);

            if (localHit.primitiveIndex != INVALID_INSTANCE) {
                vec3 localPos = localOrigin + localDir * localHit.t;
                vec3 worldPos = (inst.transform * vec4(localPos, 1.0)).xyz;
                float worldT = length(worldPos - rayOrigin);

                if (worldT >= tMin && worldT < bestHit.t) {
                    bestHit.t = worldT;
                    bestHit.barycentrics = localHit.barycentrics;
                    bestHit.primitiveIndex = localHit.primitiveIndex;
                    bestHit.instanceIndex = i;
                }
            }
        }
        return bestHit;
    }
#endif // COMPUTE

// Common shadow ray function - works for both Compute and RTX
bool traceShadowRay(vec3 rayOrigin, vec3 rayDirection, float tMin, float tMax) {
#ifdef USE_COMPUTE
    HitInfo shadowHit = traceScene(rayOrigin, rayDirection, tMin, tMax);
    return shadowHit.instanceIndex != INVALID_INSTANCE;
#else
    shadowPayload = 1u;
    const uint shadowFlags = gl_RayFlagsTerminateOnFirstHitEXT
        | gl_RayFlagsOpaqueEXT
        | gl_RayFlagsSkipClosestHitShaderEXT;
    traceRayEXT(topLevelAS, shadowFlags, 0xFF, 0, 0, SHADOW_MISS_INDEX, rayOrigin, tMin, rayDirection, tMax, 1);
    return shadowPayload != 0u;
#endif
}

// Common reflection ray function - works for both Compute and RTX
bool traceReflectionRay(vec3 rayOrigin, vec3 rayDirection, float roughness, EnvironmentDataGpu environmentData, out vec3 hitColor) {
#ifdef USE_COMPUTE
    HitInfo hit = traceScene(rayOrigin, rayDirection, 0.001, 1000.0);

    if (hit.instanceIndex == INVALID_INSTANCE) {
        hitColor = sampleEnvironmentMapColorLod(rayDirection, environmentData, environmentData.lightingExposureScale, roughness * max(environmentData.maxTextureLod, 0.0));
        return false;
    }

    const ComputeInstanceGpu inst = instances[hit.instanceIndex];
    const MeshAddressesGpu mesh = meshes[inst.meshId];
    const Face face = FaceBuffer(mesh.faceAddress).data[hit.primitiveIndex];
    const MaterialData material = MaterialBuffer(mesh.materialAddress).data[face.materialIndex];

    const Vertex v0 = VertexBuffer(mesh.vertexAddress).data[IndexBuffer(mesh.indexAddress).data[3 * hit.primitiveIndex + 0]];
    const Vertex v1 = VertexBuffer(mesh.vertexAddress).data[IndexBuffer(mesh.indexAddress).data[3 * hit.primitiveIndex + 1]];
    const Vertex v2 = VertexBuffer(mesh.vertexAddress).data[IndexBuffer(mesh.indexAddress).data[3 * hit.primitiveIndex + 2]];

    vec2 uv = interpolateBarycentric(hit.barycentrics, v0.uv, v1.uv, v2.uv);

    vec3 albedo = material.albedo;
    if (material.albedoIndex != -1)
        albedo *= texture(textureSamplers[material.albedoIndex], uv).rgb;

    vec3 emission = material.emission * material.emissionStrength;
    if (material.emissionIndex != -1)
        emission *= texture(textureSamplers[material.emissionIndex], uv).rgb;

    hitColor = emission + albedo * 0.5;
    return true;
#else
    // RTX path - this is handled via separate ray payload in the actual RTX pipeline
    // For DirectLighting, the reflection miss shader handles this
    hitColor = sampleEnvironmentMapColorLod(rayDirection, environmentData, environmentData.lightingExposureScale, roughness * max(environmentData.maxTextureLod, 0.0));
    return false;
#endif
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

#endif // INTERSECTION_GLSL
