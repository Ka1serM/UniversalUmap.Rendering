#version 460
#pragma shader_stage(miss)

#extension GL_EXT_ray_tracing : enable
#extension GL_EXT_scalar_block_layout : enable

layout(location = 1) rayPayloadInEXT uint shadowPayload;

void main()
{
    shadowPayload = 0u;
}