#ifndef ACCUMULATION_GLSL
#define ACCUMULATION_GLSL

float accumulateWeightedScalar(float historyValue, float historyWeight, float newValue, float newWeight)
{
    float totalWeight = historyWeight + newWeight;
    if (totalWeight <= 0.0)
        return 0.0;
    return (historyValue * historyWeight + newValue * newWeight) / totalWeight;
}

vec3 accumulateWeightedColor(vec3 historyColor, float historyWeight, vec3 newColor, float newWeight)
{
    float totalWeight = historyWeight + newWeight;
    if (totalWeight <= 0.0)
        return vec3(0.0);
    return (historyColor * historyWeight + newColor * newWeight) / totalWeight;
}

#endif // ACCUMULATION_GLSL
