#ifndef SHADE_AMBIENT_OCCLUSION_GLSL
#define SHADE_AMBIENT_OCCLUSION_GLSL

const float AoMaxDistance = 1.5;

float estimateAmbientOcclusion(vec3 worldPosition, vec3 geometricNormal, inout uint rngState)
{
    int aoSampleCount = effectiveAoSampleCount();
    float occluded = 0.0;
    for (int i = 0; i < aoSampleCount; ++i) {
        vec3 dir = sampleDiffuse(geometricNormal, rngState);
        float nDotL = max(dot(geometricNormal, dir), 0.0);
        if (nDotL <= 0.0)
            continue;

        float bias = computeShadowBias(worldPosition, nDotL);
        vec3 origin = worldPosition + geometricNormal * bias;
        if (traceShadowRay(origin, dir, bias, AoMaxDistance))
            occluded += 1.0;
    }

    return 1.0 - (occluded / float(aoSampleCount));
}

#endif
