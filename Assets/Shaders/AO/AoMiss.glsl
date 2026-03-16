#version 460
// AO Miss - RTX entry point
#pragma shader_stage(miss)

#extension GL_EXT_ray_tracing : enable
#extension GL_EXT_nonuniform_qualifier : enable
#extension GL_EXT_buffer_reference : require
#extension GL_EXT_scalar_block_layout: enable

#include "../SharedStructs.h"
#include "../Common/Miss.glsl"

layout (push_constant, scalar) uniform PushConstants {
    PushDataGpu pushConstants;
};

layout(location = 0) rayPayloadInEXT AoPayload payload;

void shadeMissAo(in vec3 worldRayDirection, inout AoPayload missPayload)
{
    shadeMiss(
        worldRayDirection,
        sceneSettings.environment,
        sceneSettings.renderSettings,
        missPayload);
}

void main()
{
    shadeMissAo(gl_WorldRayDirectionEXT, payload);
}
