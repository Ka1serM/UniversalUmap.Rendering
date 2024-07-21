#version 450

layout(location = 0) in vec3 in_position;
layout(location = 1) in vec2 in_texture_coordinate;

layout(location = 0) out vec2 pass_texture_coordinate;

void main()
{
    gl_Position = vec4(in_position, 1.0f);
    pass_texture_coordinate = in_texture_coordinate;
}