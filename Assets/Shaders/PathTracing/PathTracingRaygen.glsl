#version 460
#ifndef PATHTRACING_RAYGEN_GLSL
#define PATHTRACING_RAYGEN_GLSL

#pragma shader_stage(raygen)

#extension GL_EXT_debug_printf : enable
#extension GL_EXT_nonuniform_qualifier : enable
#extension GL_EXT_buffer_reference : require
#extension GL_EXT_scalar_block_layout : enable
#extension GL_EXT_ray_tracing : enable

#include "../SharedStructs.h"

layout(location = 0) rayPayloadEXT Payload payload;
layout(location = 1) rayPayloadEXT uint shadowPayload;

layout(push_constant, scalar) uniform PushConstants {
    PushDataGpu pushConstants;
};

#include "PathTracingCommon.glsl"

void main()
{
    const ivec2 pixelCoord = ivec2(gl_LaunchIDEXT.xy);
    const ivec2 screenSize = ivec2(gl_LaunchSizeEXT.xy);
    primaryRayGenPathTracing(pixelCoord, screenSize);
}

#endif // PATHTRACING_RAYGEN_GLSL
