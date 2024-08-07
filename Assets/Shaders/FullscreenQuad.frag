#version 450

layout(set = 0, binding = 0) uniform texture2D textureColor;
layout(set = 0, binding = 1) uniform sampler samplerColor;

layout(location = 0) in vec2 fragUV;
layout(location = 0) out vec4 outColor;

vec3 ACES(vec3 color)
{
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((color * (a * color + b)) / (color * (c * color + d) + e), 0.0, 1.0);
}

vec3 sRGB(vec3 linearRGB)
{
    bvec3 cutoff = lessThan(linearRGB, vec3(0.0031308));
    vec3 higher = vec3(1.055)*pow(linearRGB, vec3(1.0/2.4)) - vec3(0.055);
    vec3 lower = linearRGB * vec3(12.92);
    return mix(higher, lower, cutoff);
}

void main()
{
    vec3 hdrColor = texture(sampler2D(textureColor, samplerColor), fragUV).rgb;
    vec3 acesColor = ACES(hdrColor);
    vec3 sRGBColor = sRGB(acesColor);
    outColor = vec4(sRGBColor, 1.0);
}