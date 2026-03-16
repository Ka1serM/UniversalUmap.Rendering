// AO Ray Generation Entry Point
// Compile with -DUSE_COMPUTE for compute shader, without for RTX raygen
#version 460

#ifdef USE_COMPUTE
#pragma shader_stage(compute)
#else
#pragma shader_stage(raygen)
#endif

#extension GL_EXT_debug_printf : enable
#extension GL_EXT_nonuniform_qualifier: enable
#extension GL_EXT_buffer_reference: require
#extension GL_EXT_scalar_block_layout: enable
#ifndef USE_COMPUTE
#extension GL_EXT_ray_tracing: enable
#endif

#include "../SharedStructs.h"

#ifdef USE_COMPUTE
layout (local_size_x = GROUP_SIZE, local_size_y = GROUP_SIZE) in;
#endif

layout (push_constant, scalar) uniform PushConstants {
    PushDataGpu pushConstants;
};

#ifndef USE_COMPUTE
layout (location = 0) rayPayloadEXT AoPayload payload;
layout (location = 1) rayPayloadEXT uint shadowPayload;
#endif

#include "../Bindings.glsl"
#include "../Common.glsl"
#include "../Common/Brdf.glsl"
#include "../Common/RenderPolicy.glsl"
#include "../Common/PrimaryRay.glsl"
#include "../Common/PayloadUtils.glsl"
#include "../Common/Accumulation.glsl"
#include "../Common/Miss.glsl"
#include "AoCommon.glsl"

#ifdef USE_COMPUTE
void traceRayComputeAo(vec3 rayOrigin, vec3 rayDirection, float tMin, float tMax, inout AoPayload payload) {
    HitInfo hit = traceScene(rayOrigin, rayDirection, tMin, tMax);

    if (hit.instanceIndex == INVALID_INSTANCE) {
        shadeMissAo(rayDirection, payload);
    } else {
        const ComputeInstanceGpu inst = instances[hit.instanceIndex];
        const MeshAddressesGpu mesh = meshes[inst.meshId];
        const Face face = FaceBuffer(mesh.faceAddress).data[hit.primitiveIndex];
        const MaterialData material = MaterialBuffer(mesh.materialAddress).data[face.materialIndex];

        const Vertex v0 = VertexBuffer(mesh.vertexAddress).data[IndexBuffer(mesh.indexAddress).data[3 * hit.primitiveIndex + 0]];
        const Vertex v1 = VertexBuffer(mesh.vertexAddress).data[IndexBuffer(mesh.indexAddress).data[3 * hit.primitiveIndex + 1]];
        const Vertex v2 = VertexBuffer(mesh.vertexAddress).data[IndexBuffer(mesh.indexAddress).data[3 * hit.primitiveIndex + 2]];

        vec3 localPos = interpolateBarycentric(hit.barycentrics, v0.position, v1.position, v2.position);
        vec3 geometricNormalLocal = normalize(cross(v1.position - v0.position, v2.position - v0.position));
        vec3 shadingNormalLocal = normalize(interpolateBarycentric(hit.barycentrics, v0.normal, v1.normal, v2.normal));
        vec3 localTan = normalize(interpolateBarycentric(hit.barycentrics, v0.tangent, v1.tangent, v2.tangent));
        float tangentSign = dot(vec3(v0.tangentSign, v1.tangentSign, v2.tangentSign), hit.barycentrics);
        vec2 uv = interpolateBarycentric(hit.barycentrics, v0.uv, v1.uv, v2.uv);

        vec3 worldPos = (inst.transform * vec4(localPos, 1.0)).xyz;
        mat3 normalMatrix = transpose(mat3(inst.inverseTransform));

        vec3 geometricNormalWorld = normalize(normalMatrix * geometricNormalLocal);
        vec3 shadingNormalWorld = normalize(normalMatrix * shadingNormalLocal);
        vec3 tangentWorld = normalize(normalMatrix * localTan);

        shadeClosestHitAo(worldPos, shadingNormalWorld, geometricNormalWorld, tangentWorld, tangentSign, uv, rayDirection, material, payload);
        payload.objectIndex = hit.instanceIndex;
    }
}
#endif

void primaryRayGenAo(ivec2 pixelCoord, ivec2 screenSize)
{
    if (pixelCoord.x >= screenSize.x || pixelCoord.y >= screenSize.y) return;

    const ivec2 pickPixelCoord = ivec2(pixelCoord.x, (screenSize.y - 1) - pixelCoord.y);
    uvec2 seed = pcg2d(uvec2(pixelCoord) ^ uvec2(pushConstants.frame * 16777619));
    uint rngState = seed.x;

    SamplerState samplerState = initSamplerState(pixelCoord, pushConstants.frame);
    vec3 rayOrigin, rayDirection;
    generatePrimaryRay(pixelCoord, screenSize, sceneSettings.camera, samplerState, true, false, rayOrigin, rayDirection);

#ifdef USE_COMPUTE
    AoPayload payload;
#endif
    initializeAoPayload(payload, rngState);

#ifdef USE_COMPUTE
    traceRayComputeAo(rayOrigin, rayDirection, 0.001, 1000.0, payload);
#else
    traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xff, 0, 0, 0, rayOrigin, 0.001, rayDirection, 1000.0, 0);
#endif

    // Store outputs - same as original
    imageStore(outputCrypto, pickPixelCoord, uvec4(payload.objectIndex, 0, 0, 0));
    
    if (payload.objectIndex != INVALID_INSTANCE) {
        imageStore(outputPosition, pickPixelCoord, vec4(payload.position, 1.0));
    } else {
        imageStore(outputPosition, pickPixelCoord, vec4(0.0));
    }

    // Preserve environment miss color; keep AO surface hits as grayscale.
    vec3 aoColor = (payload.objectIndex == INVALID_INSTANCE)
        ? payload.emission
        : vec3(payload.emission.r);

    float historySamples = 0.0;
    if (pushConstants.frame > 0 && pushConstants.isMoving == 0) {
        historySamples = max(0.0, imageLoad(outputAdaptiveState, pixelCoord).z);
    }

    vec3 accumulatedAoColor = aoColor;
    if (historySamples > 0.0) {
        vec3 previousColor = imageLoad(outputColor, pixelCoord).rgb;
        accumulatedAoColor = accumulateWeightedColor(previousColor, historySamples, aoColor, 1.0);
    }

    imageStore(outputColor, pixelCoord, vec4(accumulatedAoColor, 1.0));
    imageStore(outputAlbedo, pixelCoord, vec4(payload.albedo, 1.0));
    imageStore(outputNormal, pixelCoord, vec4(payload.normal, 0.0));
    imageStore(outputAdaptiveState, pixelCoord, vec4(0.0, 0.0, historySamples + 1.0, 0.0));
}

#ifdef USE_COMPUTE
void main() {
    const ivec2 pixelCoord = ivec2(gl_GlobalInvocationID.xy);
    const ivec2 screenSize = imageSize(outputColor);
    primaryRayGenAo(pixelCoord, screenSize);
}
#else
void main() {
    const ivec2 pixelCoord = ivec2(gl_LaunchIDEXT.xy);
    const ivec2 screenSize = ivec2(gl_LaunchSizeEXT.xy);
    primaryRayGenAo(pixelCoord, screenSize);
}
#endif
