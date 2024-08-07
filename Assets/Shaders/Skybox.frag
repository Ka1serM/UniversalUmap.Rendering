#version 450

layout(set = 0, binding = 2) uniform textureCube cubeTexture;
layout(set = 0, binding = 3) uniform sampler pointSampler;

layout(location = 0) in vec3 fragTexCoord;
layout(location = 0) out vec4 fragColor;

void main()
{
    fragColor = texture(samplerCube(cubeTexture, pointSampler), fragTexCoord);
}
