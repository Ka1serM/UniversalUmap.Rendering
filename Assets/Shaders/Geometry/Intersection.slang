// Intersection utilities for compute-based ray tracing
// When COMPUTE is defined, uses software BVH traversal
// When COMPUTE is not defined, uses hardware RTX via TraceRay
#ifndef INTERSECTION_SLANG
#define INTERSECTION_SLANG

#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Lights/EnvironmentLight.slang"

#ifndef PRIMARY_MISS_INDEX
    #define PRIMARY_MISS_INDEX 0
#endif
#ifndef SHADOW_MISS_INDEX
    #define SHADOW_MISS_INDEX 1
#endif

struct HitInfo
{
    float t;
    uint instanceIndex;
    uint primitiveIndex;
    float3 barycentrics;
};

#ifdef USE_COMPUTE
    #define BVH_STACK_DEPTH 2048

    bool intersectTriangle(float3 rayOrigin, float3 rayDirection, float3 v0, float3 v1, float3 v2, inout float t, out float3 bary, float tMin) {
        const float EPSILON = 1e-8;
        bary = float3(0.0);

        float3 e1 = v1 - v0;
        float3 e2 = v2 - v0;

        float3 pvec = cross(rayDirection, e2);
        float det = dot(e1, pvec);

        if (abs(det) < EPSILON)
            return false;

        float invDet = 1.0 / det;
        float3 tvec = rayOrigin - v0;

        float u = dot(tvec, pvec) * invDet;
        float3 qvec = cross(tvec, e1);
        float v = dot(rayDirection, qvec) * invDet;

        if (u < 0.0 || v < 0.0 || u + v > 1.0)
            return false;

        float current_t = dot(e2, qvec) * invDet;

        if (current_t > tMin && current_t < t) {
            t = current_t;
            bary = float3(1.0 - u - v, u, v);
            return true;
        }

        return false;
    }

    bool intersectAABB(float3 rayOrigin, float3 invDir, AabbGpu box, out float tmin, out float tmax) {
        const float EPSILON = 1e-6;

        float3 t0s = (box.MinBounds - rayOrigin) * invDir;
        float3 t1s = (box.MaxBounds - rayOrigin) * invDir;

        float3 tsmaller = min(t0s, t1s);
        float3 tbigger  = max(t0s, t1s);

        tmin = max(tsmaller.x, max(tsmaller.y, tsmaller.z));
        tmax = min(tbigger.x, min(tbigger.y, tbigger.z));

        return tmin <= tmax + EPSILON && tmax >= 0.0;
    }

    void traverseBVH(float3 rayOrigin, float3 rayDirection, MeshAddressesGpu mesh, inout HitInfo hit, float tMin) {
        float3 invDir = 1.0 / rayDirection;

        BVHNodeBuffer* bvh = reinterpret<BVHNodeBuffer*>(mesh.BvhNodeAddress);
        BVHIndexBuffer* bvhIndices = reinterpret<BVHIndexBuffer*>(mesh.BvhIndexAddress);
        VertexBuffer* vertices = reinterpret<VertexBuffer*>(mesh.VertexAddress);
        IndexBuffer* indices = reinterpret<IndexBuffer*>(mesh.IndexAddress);

        uint stack[BVH_STACK_DEPTH];
        uint stackPtr = 0;
        stack[stackPtr++] = 0;

        while (stackPtr > 0) {
            uint nodeIndex = stack[--stackPtr];
            BvhNodeGpu node = bvh->data[nodeIndex];

            if (node.PrimCount > 0) {
                for (uint i = 0; i < node.PrimCount; ++i) {
                    uint primIdx = bvhIndices->data[node.RightChildOrPrimIndex + i];

                    uint i0 = indices->data[3 * primIdx + 0];
                    uint i1 = indices->data[3 * primIdx + 1];
                    uint i2 = indices->data[3 * primIdx + 2];

                    float3 v0 = vertices->data[i0].Position;
                    float3 v1 = vertices->data[i1].Position;
                    float3 v2 = vertices->data[i2].Position;

                    float3 currentBary;
                    if (intersectTriangle(rayOrigin, rayDirection, v0, v1, v2, hit.t, currentBary, tMin)) {
                        hit.primitiveIndex = int(primIdx);
                        hit.barycentrics = currentBary;
                    }
                }
                continue;
            }

            float tmin_left, tmax_left, tmin_right, tmax_right;
            bool hit_left = intersectAABB(rayOrigin, invDir, node.LeftBounds, tmin_left, tmax_left);
            bool hit_right = intersectAABB(rayOrigin, invDir, node.RightBounds, tmin_right, tmax_right);

            hit_left = hit_left && tmin_left < hit.t && tmax_left > tMin;
            hit_right = hit_right && tmin_right < hit.t && tmax_right > tMin;

            if (!hit_left && !hit_right) continue;

            uint leftChildIndex = nodeIndex + 1;
            uint rightChildIndex = node.RightChildOrPrimIndex;

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

    HitInfo traceScene(float3 rayOrigin, float3 rayDirection, float tMin, float tMax) {
        HitInfo bestHit;
        bestHit.t = tMax;
        bestHit.instanceIndex = InvalidInstance;
        bestHit.primitiveIndex = InvalidInstance;

        uint instanceCount = 0;
        uint instanceStride = 0;
        instances.GetDimensions(instanceCount, instanceStride);
        for (uint i = 0; i < instanceCount; ++i) {
            InstanceGpu inst = instances[i];

            if (inst.MeshId == 0xFFFFFFFF)
                continue;

            float3 localOrigin = mul(float4(rayOrigin, 1.0), inst.InverseTransform).xyz;
            float3 localDir = mul(float4(rayDirection, 0.0), inst.InverseTransform).xyz;

            HitInfo localHit;
            localHit.t = bestHit.t;
            localHit.primitiveIndex = InvalidInstance;

            MeshAddressesGpu mesh = meshes[inst.MeshId];
            traverseBVH(localOrigin, localDir, mesh, localHit, tMin);

            if (localHit.primitiveIndex != InvalidInstance) {
                float3 localPos = localOrigin + localDir * localHit.t;
                float3 worldPos = mul(float4(localPos, 1.0), inst.Transform).xyz;
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
bool traceShadowRay(float3 rayOrigin, float3 rayDirection, float tMin, float tMax) {
#ifdef USE_COMPUTE
    HitInfo shadowHit = traceScene(rayOrigin, rayDirection, tMin, tMax);
    return shadowHit.instanceIndex != InvalidInstance;
#else
    uint shadowPayload = 1u;
    const uint shadowFlags = RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH
        | RAY_FLAG_FORCE_OPAQUE
        | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER;
    RayDesc shadowRay;
    shadowRay.Origin = rayOrigin;
    shadowRay.Direction = rayDirection;
    shadowRay.TMin = tMin;
    shadowRay.TMax = tMax;
    TraceRay(topLevelAS, shadowFlags, 0xFF, 0, 0, SHADOW_MISS_INDEX, shadowRay, shadowPayload);
    return shadowPayload != 0u;
#endif
}

// Common reflection ray function - works for both Compute and RTX
bool traceReflectionRay(float3 rayOrigin, float3 rayDirection, float roughness, EnvironmentDataGpu environmentData, out float3 hitColor) {
#ifdef USE_COMPUTE
    HitInfo hit = traceScene(rayOrigin, rayDirection, 0.001, 1000.0);

    if (hit.instanceIndex == InvalidInstance) {
        hitColor = sampleEnvironmentMapColorLod(rayDirection, environmentData, environmentData.LightingExposureScale, roughness * max(environmentData.MaxTextureLod, 0.0));
        return false;
    }

    const InstanceGpu inst = instances[hit.instanceIndex];
    const MeshAddressesGpu mesh = meshes[inst.MeshId];
    const Face face = reinterpret<FaceBuffer*>(mesh.FaceAddress)->data[hit.primitiveIndex];
    const MaterialData material = reinterpret<MaterialBuffer*>(mesh.MaterialAddress)->data[face.MaterialIndex];

    const Vertex v0 = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[3 * hit.primitiveIndex + 0]];
    const Vertex v1 = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[3 * hit.primitiveIndex + 1]];
    const Vertex v2 = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[3 * hit.primitiveIndex + 2]];

    float2 uv = interpolateBarycentric(hit.barycentrics, v0.UV, v1.UV, v2.UV);

    float3 albedo = material.Albedo;
    if (material.AlbedoIndex != -1)
        albedo *= textureSamplers[material.AlbedoIndex].Sample(uv).rgb;

    float3 emission = material.Emission * material.EmissionStrength;
    if (material.EmissionIndex != -1)
        emission *= textureSamplers[material.EmissionIndex].Sample(uv).rgb;

    hitColor = emission + albedo * 0.5;
    return true;
#else
    // RTX path - this is handled via separate ray payload in the actual RTX pipeline
    // For DirectLighting, the reflection miss shader handles this
    hitColor = sampleEnvironmentMapColorLod(rayDirection, environmentData, environmentData.LightingExposureScale, roughness * max(environmentData.MaxTextureLod, 0.0));
    return false;
#endif
}

float3 computeShadowTerminatorPointLocal(
    float3 localPos,
    float3 bary,
    float3 v0Pos, float3 v1Pos, float3 v2Pos,
    float3 v0Normal, float3 v1Normal, float3 v2Normal)
{
    float3 n0 = normalize(v0Normal);
    float3 n1 = normalize(v1Normal);
    float3 n2 = normalize(v2Normal);

    float3 tmp0 = localPos - v0Pos;
    float3 tmp1 = localPos - v1Pos;
    float3 tmp2 = localPos - v2Pos;

    float d0 = min(0.0, dot(tmp0, n0));
    float d1 = min(0.0, dot(tmp1, n1));
    float d2 = min(0.0, dot(tmp2, n2));

    tmp0 -= d0 * n0;
    tmp1 -= d1 * n1;
    tmp2 -= d2 * n2;

    return localPos + bary.x * tmp0 + bary.y * tmp1 + bary.z * tmp2;
}

#endif // INTERSECTION_SLANG
