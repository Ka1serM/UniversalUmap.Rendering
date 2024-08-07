#version 450

layout(set = 0, binding = 0) uniform cameraUbo
{
    mat4 projection;
    mat4 view;
    vec4 front;
} camera;

layout(location = 0) in vec3 inPosition;
layout(location = 0) out vec3 fragPosition; 

void main()
{
    //Remove translation from the view matrix
    mat4 viewMatrixWithoutTranslation = mat4(
        camera.view[0][0], camera.view[0][1], camera.view[0][2], 0,
        camera.view[1][0], camera.view[1][1], camera.view[1][2], 0,
        camera.view[2][0], camera.view[2][1], camera.view[2][2], 0,
        0, 0, 0, 1
    );
    
    vec4 worldPosition = viewMatrixWithoutTranslation * vec4(inPosition, 1.0);
    gl_Position = camera.projection * worldPosition;
    
    fragPosition = inPosition;
}
