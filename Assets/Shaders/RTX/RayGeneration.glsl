#version 460
#pragma shader_stage(raygen)

#extension GL_EXT_debug_printf : enable
#extension GL_EXT_ray_tracing: enable
#extension GL_EXT_nonuniform_qualifier: enable
#extension GL_EXT_buffer_reference: require
#extension GL_EXT_scalar_block_layout: enable

#include "RayGenerationPathTracing.glsl"
