#version 450

layout(set = 0, binding = 0) uniform texture2D TextureColor;
layout(set = 0, binding = 1) uniform sampler SamplerColor;

layout(location = 0) in vec2 pass_texture_coordinate;

layout(location = 0) out vec4 out_color;

void main()
{
    out_color = texture(sampler2D(TextureColor, SamplerColor), pass_texture_coordinate);
}