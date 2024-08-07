#version 450

layout(location = 0) in vec3 position;
layout(location = 1) in vec4 color;
layout(location = 2) in vec3 normal;
layout(location = 3) in vec3 tangent;
layout(location = 4) in vec2 uv;

layout(location = 5) in vec4 matrixRow0;
layout(location = 6) in vec4 matrixRow1;
layout(location = 7) in vec4 matrixRow2;
layout(location = 8) in vec4 matrixRow3;

layout(location = 0) out vec4 fragColor;
layout(location = 1) out vec3 fragNormal;
layout(location = 2) out vec3 fragTangent;
layout(location = 3) out vec2 fragUV;

layout(set = 1, binding = 0) uniform cameraUbo
{
    mat4 projection;
    mat4 view;
    vec4 front;
} camera;

void main() {
    mat4 instanceTransform = mat4(matrixRow0, matrixRow1, matrixRow2, matrixRow3);
    
    vec4 worldPosition = instanceTransform * vec4(position, 1.0);
    gl_Position = (camera.projection * camera.view) * worldPosition;
    
    fragColor = color;
    mat3 normalWorldMatrix = transpose(inverse(mat3(instanceTransform)));
    fragNormal = normalize(normalWorldMatrix * normal);
    fragTangent = normalize(normalWorldMatrix * tangent);
    fragUV = uv;
}