#version 460
#ifndef DIRECTLIGHTING_RAYGEN_GLSL
#define DIRECTLIGHTING_RAYGEN_GLSL

#pragma shader_stage(raygen)

#extension GL_EXT_debug_printf : enable
#extension GL_EXT_nonuniform_qualifier: enable
#extension GL_EXT_buffer_reference: require
#extension GL_EXT_scalar_block_layout: enable
#extension GL_EXT_ray_tracing: enable

#include "../SharedStructs.h"

layout(location = 0) rayPayloadEXT Payload payload;
layout(location = 1) rayPayloadEXT uint shadowPayload;

layout(push_constant, scalar) uniform PushConstants {
    PushDataGpu pushConstants;
};

#include "../Common/PrimaryRay.glsl"
#include "../Common/AdaptiveSampling.glsl"
#include "../Common/PayloadUtils.glsl"
#include "DirectLightingCommon.glsl"
#include "DirectLightingPrimary.glsl"

void main()
{
    const ivec2 pixelCoord = ivec2(gl_LaunchIDEXT.xy);
    const ivec2 screenSize = ivec2(gl_LaunchSizeEXT.xy);
    primaryRayGenDirect(pixelCoord, screenSize);
}

#endif // DIRECTLIGHTING_RAYGEN_GLSL
