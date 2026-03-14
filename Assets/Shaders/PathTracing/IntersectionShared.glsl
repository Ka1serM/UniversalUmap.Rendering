#include "../Bindings.glsl"
#include "../Common.glsl"
#include "ShadeMiss.glsl"

#define BVH_STACK_DEPTH 512

bool intersectTriangle(vec3 rayOrigin, vec3 rayDirection, vec3 v0, vec3 v1, vec3 v2, inout float t, out vec3 bary, float tMin) {
    const float EPSILON = 1e-8;

    vec3 e1 = v1 - v0;
    vec3 e2 = v2 - v0;

    vec3 pvec = cross(rayDirection, e2);
    float det = dot(e1, pvec);

    // Skip degenerate triangles only
    if (abs(det) < EPSILON)
        return false;

    float invDet = 1.0 / det;
    vec3 tvec = rayOrigin - v0;

    float u = dot(tvec, pvec) * invDet;
    vec3 qvec = cross(tvec, e1);
    float v = dot(rayDirection, qvec) * invDet;

    // Double-sided: accept intersections regardless of triangle winding
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
    const float EPSILON = 1e-6; // small tolerance for numerical precision

    vec3 t0s = (box.minBounds - rayOrigin) * invDir;
    vec3 t1s = (box.maxBounds - rayOrigin) * invDir;

    vec3 tsmaller = min(t0s, t1s);
    vec3 tbigger  = max(t0s, t1s);

    tmin = max(tsmaller.x, max(tsmaller.y, tsmaller.z));
    tmax = min(tbigger.x, min(tbigger.y, tbigger.z));

    // Ensure tiny numerical errors don't reject valid intersections
    return tmin <= tmax + EPSILON && tmax >= 0.0;
}

void traverseBVH(vec3 rayOrigin, vec3 rayDirection, MeshAddressesGpu mesh, inout HitInfo hit, float tMin) {
    vec3 invDir = 1.0 / rayDirection;

    // Construct buffer references from the 64-bit addresses using your new names.
    BVHNodeBuffer bvh = BVHNodeBuffer(mesh.bvhNodeAddress);
    BVHIndexBuffer bvhIndices = BVHIndexBuffer(mesh.bvhIndexAddress);
    VertexBuffer vertices = VertexBuffer(mesh.vertexAddress);
    IndexBuffer indices = IndexBuffer(mesh.indexAddress);

    uint stack[BVH_STACK_DEPTH];
    uint stackPtr = 0;
    stack[stackPtr++] = 0; // Start at the root node (index 0).

    while (stackPtr > 0) {
        uint nodeIndex = stack[--stackPtr];
        BvhNodeGpu node = bvh.data[nodeIndex];

        // --- LEAF NODE ---
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
                // Pass tMin to the triangle intersection test.
                if (intersectTriangle(rayOrigin, rayDirection, v0, v1, v2, hit.t, currentBary, tMin)) {
                    hit.primitiveIndex = int(primIdx);
                    hit.barycentrics = currentBary;
                }
            }
            continue; // Finished with this leaf
        }

        // --- INTERIOR NODE ---
        float tmin_left, tmax_left, tmin_right, tmax_right;
        bool hit_left = intersectAABB(rayOrigin, invDir, node.leftBounds, tmin_left, tmax_left);
        bool hit_right = intersectAABB(rayOrigin, invDir, node.rightBounds, tmin_right, tmax_right);

        // Check if the AABB intersection intervals overlap with the ray's valid interval [tMin, hit.t].
        hit_left = hit_left && tmin_left < hit.t && tmax_left > tMin;
        hit_right = hit_right && tmin_right < hit.t && tmax_right > tMin;

        if (!hit_left && !hit_right) continue;

        uint leftChildIndex = nodeIndex + 1; // Left child is always adjacent in memory.
        uint rightChildIndex = node.rightChildOrPrimIndex;

        // --- Push children to stack in the optimal order (closer one last) ---
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

        // Transform ray into local space.
        // Keep direction unnormalized so parametric t remains consistent with world ray:
        // local(inv(O + t*D)) = localO + t*localD
        vec3 localOrigin = (inst.inverseTransform * vec4(rayOrigin, 1.0)).xyz;
        vec3 localDir = (inst.inverseTransform * vec4(rayDirection, 0.0)).xyz;

        // With unnormalized localDir, local t is directly comparable to world-space t.
        HitInfo localHit;
        localHit.t = bestHit.t;
        localHit.primitiveIndex = INVALID_INSTANCE;

        MeshAddressesGpu mesh = meshes[inst.meshId];
        traverseBVH(localOrigin, localDir, mesh, localHit, tMin);

        if (localHit.primitiveIndex != INVALID_INSTANCE) {
            // Convert hit point back to world space and recompute worldT robustly.
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

bool traceShadowRay(vec3 rayOrigin, vec3 rayDirection, float tMin, float tMax) {
    HitInfo shadowHit = traceScene(rayOrigin, rayDirection, tMin, tMax);
    return shadowHit.instanceIndex != INVALID_INSTANCE;
}

vec3 computeShadowTerminatorPointLocal(
    vec3 localPos,
    vec3 bary,
    vec3 v0Pos, vec3 v1Pos, vec3 v2Pos,
    vec3 v0Normal, vec3 v1Normal, vec3 v2Normal)
{
    // Hanika et al. "hacking the shadow terminator" point shift.
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
