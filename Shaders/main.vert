#version 450

layout(location = 0) in vec3 position;
layout(location = 1) in vec3 color;
layout(location = 2) in vec3 normal;

//Instance attributes
layout(location = 3) in vec4 transformRow0;
layout(location = 4) in vec4 transformRow1;
layout(location = 5) in vec4 transformRow2;
layout(location = 6) in vec4 transformRow3;

layout(location = 0) out vec3 fragColor;
layout(location = 1) out vec3 fragNormalWorld;

layout(set = 0, binding = 0) uniform cameraUbo
{
    mat4 projection;
    mat4 view;
    vec4 front;
} ubo;

void main() {
    mat4 instanceTransform = mat4(transformRow0, transformRow1, transformRow2, transformRow3);
    vec4 worldPosition = instanceTransform * vec4(position, 1.0);

    gl_Position = ubo.projection * ubo.view * worldPosition;

    mat3 normalWorldMatrix = transpose(inverse(mat3(instanceTransform)));
    fragNormalWorld = normalize(normalWorldMatrix * normal);
    fragColor = color;
}