#version 450

// Fragment shader inputs
layout(location = 0) in vec3 fragColor;
layout(location = 1) in vec3 fragNormalWorld;

// Fragment shader output
layout(location = 0) out vec4 outColor;

// Uniform buffer
layout(set = 0, binding = 0) uniform cameraUbo
{
    mat4 projection;
    mat4 view;
    vec4 front;
} ubo;

void main() {
    vec3 viewDirection = normalize(ubo.front.xyz);

    float ambientContribution = 0.3;
    float diffuseContribution = max(dot(-viewDirection, fragNormalWorld), 0.0);

    vec3 finalColor = (ambientContribution + diffuseContribution) * fragColor;
    outColor = vec4(finalColor, 1.0);
}