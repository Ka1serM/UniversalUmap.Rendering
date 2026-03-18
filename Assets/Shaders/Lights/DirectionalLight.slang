#ifndef DIRECTIONAL_LIGHT_SLANG
#define DIRECTIONAL_LIGHT_SLANG

#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "ILight.slang"

bool hasDirectionalLight(in EnvironmentDataGpu environmentData) {
    return environmentData.directionalIntensity > EPSILON &&
           dot(environmentData.directionalDirection, environmentData.directionalDirection) > EPSILON;
}

float3 getDirectionalLightDirection(in EnvironmentDataGpu environmentData) {
    float3 d = environmentData.directionalDirection;
    float l2 = dot(d, d);
    if (l2 <= EPSILON)
        return float3(0.0, 1.0, 0.0);
    return d * rsqrt(l2);
}

struct DirectionalLight : ILight
{
    LightLiSample sampleLi(float3 referencePoint, float2 u)
    {
        referencePoint = referencePoint;
        u = u;

        LightLiSample sample;
        sample.wi = getDirectionalLightDirection(sceneSettings.environment);
        sample.Li = float3(sceneSettings.environment.directionalIntensity);
        sample.pdf = hasDirectionalLight(sceneSettings.environment) ? 1.0 : 0.0;
        return sample;
    }

    float pdfLi(float3 referencePoint, float3 wi)
    {
        referencePoint = referencePoint;
        wi = wi;
        return 0.0;
    }

    LightLeSample sampleLe(float2 u1, float2 u2)
    {
        u1 = u1;
        u2 = u2;

        LightLeSample sample;
        sample.direction = getDirectionalLightDirection(sceneSettings.environment);
        sample.radiance = float3(sceneSettings.environment.directionalIntensity);
        sample.pdfPos = hasDirectionalLight(sceneSettings.environment) ? 1.0 : 0.0;
        sample.pdfDir = hasDirectionalLight(sceneSettings.environment) ? 1.0 : 0.0;
        return sample;
    }
};

#endif // DIRECTIONAL_LIGHT_SLANG
