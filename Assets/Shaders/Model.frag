#version 450

layout(location = 0) in vec4 fragColor;
layout(location = 1) in vec3 fragNormal;
layout(location = 2) in vec3 fragTangent;
layout(location = 3) in vec2 fragUV;

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform autoTextureUbo
{
    vec4 colorMask;
    vec4 metallicMask;
    vec4 specularMask;
    vec4 roughnessMask;
    vec4 aoMask;
    vec4 normalMask;
    vec4 emissiveMask;
    vec4 alphaMask;
} autoTexture;

layout(set = 1, binding = 0) uniform cameraUbo
{
    mat4 projection;
    mat4 view;
    vec4 front;
} camera;

layout(set = 2, binding = 0) uniform sampler aniso4xSampler;

layout(set = 3, binding = 0) uniform texture2D colorTexture;
layout(set = 4, binding = 0) uniform texture2D metallicTexture;
layout(set = 5, binding = 0) uniform texture2D specularTexture;
layout(set = 6, binding = 0) uniform texture2D roughnessTexture;
layout(set = 7, binding = 0) uniform texture2D aoTexture;
layout(set = 8, binding = 0) uniform texture2D normalTexture;
layout(set = 9, binding = 0) uniform texture2D alphaTexture;
layout(set = 10, binding = 0) uniform texture2D emissiveTexture;

const vec3 lightDir = normalize(vec3(0.5, 1.0, 0.5));

float getMaskedChannel(vec4 textureSample, vec4 mask) {
    // Calculate the greyscale version of the texture sample
    float greyscale = dot(textureSample.rgb, vec3(1.0 / 3.0)); // Average for greyscale
    // Use the mask to select the appropriate channel or the greyscale value
    return dot(textureSample, mask) / max(dot(mask.rgb, vec3(1.0)), 0.001); // Avoid division by zero
}

void main() {
    vec4 color = texture(sampler2D(colorTexture, aniso4xSampler), fragUV) * autoTexture.colorMask;
    float metallic = getMaskedChannel(texture(sampler2D(metallicTexture, aniso4xSampler), fragUV), autoTexture.metallicMask); //dot product returns channel
    float specular = getMaskedChannel(texture(sampler2D(specularTexture, aniso4xSampler), fragUV), autoTexture.specularMask) * 2.0;
    float roughness = getMaskedChannel(texture(sampler2D(roughnessTexture, aniso4xSampler), fragUV), autoTexture.roughnessMask);
    float ao = getMaskedChannel(texture(sampler2D(aoTexture, aniso4xSampler), fragUV), autoTexture.aoMask);
    vec4 normal = (texture(sampler2D(normalTexture, aniso4xSampler), fragUV) * autoTexture.normalMask) * 2.0 - 1.0;
    float alpha = getMaskedChannel(texture(sampler2D(alphaTexture, aniso4xSampler), fragUV), autoTexture.alphaMask);
    vec4 emissive = texture(sampler2D(emissiveTexture, aniso4xSampler), fragUV) * autoTexture.emissiveMask;

    //Normals
    vec3 fragBitangent = -normalize(cross(fragNormal, fragTangent));
    mat3 TBNMatrix = mat3(fragTangent, fragBitangent, fragNormal);
    vec3 worldNormal = normalize(TBNMatrix * normal.rgb);

    // Lighting vectors
    vec3 viewDir = -vec3(camera.front);
    vec3 halfWayDir = normalize(lightDir + viewDir);

    //Diffuse (Half-Lambertian)
    float NdotL = dot(worldNormal, lightDir);
    vec3 diffuse = color.rgb * (NdotL * 0.5 + 0.5);

    //Specular (Blinn-Phong)
    float specularValue = pow(max(dot(worldNormal, halfWayDir), 0.0), mix(16.0, 4.0, roughness));
    vec3 specularColor = mix(vec3(0.04), color.rgb, metallic);
    vec3 specularReflection = specularValue * specularColor * specular;
    
    outColor = vec4(ao * (diffuse + specularReflection), 1.0);
}
