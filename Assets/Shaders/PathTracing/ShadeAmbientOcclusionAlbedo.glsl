#ifndef SHADE_AMBIENT_OCCLUSION_ALBEDO_GLSL
#define SHADE_AMBIENT_OCCLUSION_ALBEDO_GLSL

vec3 shadeAmbientOcclusionOnly(float ao)
{
    return vec3(ao);
}

vec3 shadeAmbientOcclusionWithAlbedo(vec3 albedo, float ao)
{
    return albedo * ao;
}

#endif
