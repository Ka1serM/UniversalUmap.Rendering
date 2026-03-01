#version 460
#pragma shader_stage(miss)

#extension GL_EXT_ray_tracing : enable

layout(location = 1) rayPayloadInEXT uint shadowPayload;

void main()
{
    // Miss means no occluder along shadow ray.
    shadowPayload = 0u;
}
