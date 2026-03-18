#ifndef ACCUMULATION_SLANG
#define ACCUMULATION_SLANG

float accumulateWeightedScalar(float historyValue, float historyWeight, float newValue, float newWeight)
{
    float invWeight = 1.0 / max(historyWeight + newWeight, EPSILON);
    return (historyValue * historyWeight + newValue * newWeight) * invWeight;
}

float3 accumulateWeightedColor(float3 historyColor, float historyWeight, float3 newColor, float newWeight)
{
    float invWeight = 1.0 / max(historyWeight + newWeight, EPSILON);
    return (historyColor * historyWeight + newColor * newWeight) * invWeight;
}

#endif // ACCUMULATION_SLANG
