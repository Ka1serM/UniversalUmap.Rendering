#version 460
#ifndef DIRECTLIGHTING_MISS_GLSL
#define DIRECTLIGHTING_MISS_GLSL

#pragma shader_stage(miss)

#extension GL_EXT_ray_tracing : enable
#extension GL_EXT_nonuniform_qualifier : enable
#extension GL_EXT_buffer_reference : require
#extension GL_EXT_scalar_block_layout : enable

#include "../SharedStructs.h"

layout(push_constant, scalar) uniform PushConstants {
    PushDataGpu pushConstants;
};

layout(location = 0) rayPayloadInEXT Payload payload;
layout(location = 1) rayPayloadEXT uint shadowPayload;

#include "DirectLightingCommon.glsl"

void main()
{
    shadePrimaryMissDirect(gl_WorldRayDirectionEXT, payload);
}

#endif // DIRECTLIGHTING_MISS_GLSL