#version 450

layout(location = 0) in vec3 fragColor;
layout(location = 1) in vec3 fragNormalWorld;

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform cameraUbo
{
    mat4 projection;
    mat4 view;
    vec4 front;
} ubo;

void main() {
    float ambientContribution = 0.3;
    float diffuseContribution = max(dot(-ubo.front.xyz, fragNormalWorld), 0.0);

    vec3 finalColor = (ambientContribution + diffuseContribution) * fragColor;
    outColor = vec4(finalColor, 1.0);
}