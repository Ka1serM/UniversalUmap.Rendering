namespace UniversalUmap.Rendering;

public static class Shaders
{
    public static string MainVertexSource = @"
    #version 450

    // Vertex attributes
    layout(location = 0) in vec3 position;
    layout(location = 1) in vec3 color;
    layout(location = 2) in vec3 normal;

    // Instance attributes
    layout(location = 3) in vec4 transformRow0;
    layout(location = 4) in vec4 transformRow1;
    layout(location = 5) in vec4 transformRow2;
    layout(location = 6) in vec4 transformRow3;

    // Vertex shader outputs
    layout(location = 0) out vec3 fragColor;
    layout(location = 1) out vec3 fragNormalWorld;

    // Uniform buffer
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
    ";
    
    public static string MainFragmentSource = @"
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
    ";
    
    public static string FullscreenQuadVertexSource = @"
    #version 450
     
    layout(location = 0) in vec3 in_position;
    layout(location = 1) in vec2 in_texture_coordinate;
     
    layout(location = 0) out vec2 pass_texture_coordinate;
     
    void main()
    {
       gl_Position = vec4(in_position, 1.0f);
            pass_texture_coordinate = in_texture_coordinate;
    }
    ";
    public static string FullscreenQuadFragmentSource = @"
    #version 450
     
    layout(set = 0, binding = 0) uniform texture2D TextureColor;
    layout(set = 0, binding = 1) uniform sampler SamplerColor;
     
    layout(location = 0) in vec2 pass_texture_coordinate;
     
    layout(location = 0) out vec4 out_color;
     
    void main()
    {
            out_color = texture(sampler2D(TextureColor, SamplerColor), pass_texture_coordinate);
    }
    ";
}