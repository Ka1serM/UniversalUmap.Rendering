#version 460
#ifndef PATHTRACING_MISS_GLSL
#define PATHTRACING_MISS_GLSL

#pragma shader_stage(miss)

#extension GL_EXT_ray_tracing : enable
#extension GL_EXT_nonuniform_qualifier : enable
#extension GL_EXT_buffer_reference : require
#extension GL_EXT_scalar_block_layout : enable

#include "../SharedStructs.h"
#include "../Common/Miss.glsl"

layout(push_constant, scalar) uniform PushConstants {
    PushDataGpu pushConstants;
};

layout(location = 0) rayPayloadInEXT Payload payload;

void main()
{
    shadeMiss(gl_WorldRayDirectionEXT, sceneSettings.environment, sceneSettings.renderSettings, payload);
}

#endif // PATHTRACING_MISS_GLSL
