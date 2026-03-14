#ifndef ADAPTIVE_SAMPLING_COMMON_GLSL
#define ADAPTIVE_SAMPLING_COMMON_GLSL

struct AdaptiveSamplingContext {
    vec4 prevColorData;
    vec4 prevAdaptiveData;
    float exposureScale;
    bool adaptiveSamplingEnabled;
    int adaptiveMinSamples;
    float adaptiveTargetError;
    float previousTotalSamples;
    bool wasConverged;
};

struct AdaptiveSamplingFrameState {
    float metricMean;
    float metricM2;
    int samplesTaken;
};

AdaptiveSamplingContext loadAdaptiveSamplingContext(ivec2 pixelCoord)
{
    AdaptiveSamplingContext context;
    context.prevColorData = imageLoad(outputColor, pixelCoord);
    context.prevAdaptiveData = imageLoad(outputAdaptiveState, pixelCoord);
    context.exposureScale = exp2(sceneSettings.renderSettings.exposure);
    context.adaptiveSamplingEnabled = sceneSettings.renderSettings.adaptiveSamplingEnabled != 0;
    context.adaptiveMinSamples = max(1, sceneSettings.renderSettings.adaptiveMinSamples);
    context.adaptiveTargetError = max(sceneSettings.renderSettings.adaptiveTargetError, 0.0);
    context.previousTotalSamples = pushConstants.frame > 0 ? context.prevAdaptiveData.z : 0.0;
    context.wasConverged = false;
    if (pushConstants.frame > 0 && context.adaptiveSamplingEnabled && context.previousTotalSamples >= float(context.adaptiveMinSamples)) {
        float historyVariance = context.previousTotalSamples > 1.0
            ? context.prevAdaptiveData.y / max(context.previousTotalSamples - 1.0, 1.0)
            : 0.0;
        float historyStandardError = sqrt(max(historyVariance, 0.0) / max(context.previousTotalSamples, 1.0));
        float historyRelativeError = historyStandardError / max(abs(context.prevAdaptiveData.x), EPSILON);
        context.wasConverged = historyRelativeError <= context.adaptiveTargetError;
    }
    return context;
}

AdaptiveSamplingFrameState createAdaptiveSamplingFrameState()
{
    AdaptiveSamplingFrameState state;
    state.metricMean = 0.0;
    state.metricM2 = 0.0;
    state.samplesTaken = 0;
    return state;
}

bool shouldSkipAdaptiveSampling(AdaptiveSamplingContext context)
{
    return context.adaptiveSamplingEnabled && context.wasConverged;
}

void updateAdaptiveSamplingState(
    in AdaptiveSamplingContext context,
    in vec3 sampleColor,
    inout AdaptiveSamplingFrameState state)
{
    state.samplesTaken++;

    float sampleLuminance = luminance(sampleColor);
    float sampleMetric = log2(1.0 + sampleLuminance * context.exposureScale);
    float sampleCountF = float(state.samplesTaken);
    float metricDelta = sampleMetric - state.metricMean;
    state.metricMean += metricDelta / sampleCountF;
    state.metricM2 += metricDelta * (sampleMetric - state.metricMean);
}

float combineAdaptiveMean(float historyMean, float historySamples, float frameMean, int frameSamples)
{
    if (frameSamples <= 0)
        return historyMean;
    if (historySamples <= 0.0)
        return frameMean;

    float frameSamplesF = float(frameSamples);
    float totalSamples = historySamples + frameSamplesF;
    return historyMean + (frameMean - historyMean) * (frameSamplesF / totalSamples);
}

float combineAdaptiveM2(float historyMean, float historyM2, float historySamples, float frameMean, float frameM2, int frameSamples)
{
    if (frameSamples <= 0)
        return historyM2;
    if (historySamples <= 0.0)
        return frameM2;

    float frameSamplesF = float(frameSamples);
    float totalSamples = historySamples + frameSamplesF;
    float meanDelta = frameMean - historyMean;
    return historyM2 + frameM2 + meanDelta * meanDelta * historySamples * frameSamplesF / totalSamples;
}

float computeAdaptiveRelativeError(float metricMean, float metricM2, float totalSamples)
{
    if (totalSamples <= 0.0)
        return 1e30;

    float variance = totalSamples > 1.0 ? metricM2 / max(totalSamples - 1.0, 1.0) : 0.0;
    float standardError = sqrt(max(variance, 0.0) / max(totalSamples, 1.0));
    return standardError / max(abs(metricMean), EPSILON);
}

bool shouldTerminateAdaptiveSampling(
    in AdaptiveSamplingContext context,
    in AdaptiveSamplingFrameState state)
{
    if (!context.adaptiveSamplingEnabled || state.samplesTaken < context.adaptiveMinSamples)
        return false;

    float totalSamples = context.previousTotalSamples + float(state.samplesTaken);
    float combinedMetricMean = combineAdaptiveMean(context.prevAdaptiveData.x, context.previousTotalSamples, state.metricMean, state.samplesTaken);
    float combinedMetricM2 = combineAdaptiveM2(context.prevAdaptiveData.x, context.prevAdaptiveData.y, context.previousTotalSamples, state.metricMean, state.metricM2, state.samplesTaken);
    float relativeError = computeAdaptiveRelativeError(combinedMetricMean, combinedMetricM2, totalSamples);
    return totalSamples >= float(context.adaptiveMinSamples) && relativeError <= context.adaptiveTargetError;
}

void finalizeAdaptiveSampling(
    in AdaptiveSamplingContext context,
    in AdaptiveSamplingFrameState frameState,
    in vec3 accumulatedColor,
    in float newAlpha,
    out vec4 finalColorData,
    out vec4 finalAdaptiveData)
{
    float newSamplesF = float(max(frameState.samplesTaken, 1));
    vec3 newColor = accumulatedColor / newSamplesF;

    float historySamples = context.previousTotalSamples;
    float totalSamples = historySamples + newSamplesF;
    float combinedMetricMean = combineAdaptiveMean(context.prevAdaptiveData.x, historySamples, frameState.metricMean, frameState.samplesTaken);
    float combinedMetricM2 = combineAdaptiveM2(context.prevAdaptiveData.x, context.prevAdaptiveData.y, historySamples, frameState.metricMean, frameState.metricM2, frameState.samplesTaken);
    float combinedRelativeError = computeAdaptiveRelativeError(combinedMetricMean, combinedMetricM2, totalSamples);
    float converged = context.adaptiveSamplingEnabled &&
                      totalSamples >= float(context.adaptiveMinSamples) &&
                      combinedRelativeError <= context.adaptiveTargetError ? 1.0 : 0.0;

    vec3 prevColorPremult = context.prevColorData.rgb * context.prevColorData.a * historySamples;
    float prevAlpha = context.prevColorData.a;

    vec3 newColorWithExposure = newColor * context.exposureScale;
    vec3 newColorPremult = newColorWithExposure * newAlpha;

    vec3 finalColorPremult = totalSamples > 0.0
        ? (prevColorPremult + newColorPremult * newSamplesF) / totalSamples
        : vec3(0.0);
    float finalAlpha = totalSamples > 0.0
        ? ((prevAlpha * historySamples) + (newAlpha * newSamplesF)) / totalSamples
        : 0.0;

    vec3 finalColor = (finalAlpha > 0.0) ? finalColorPremult / finalAlpha : vec3(0.0);
    finalColorData = vec4(finalColor, finalAlpha);
    finalAdaptiveData = vec4(combinedMetricMean, combinedMetricM2, totalSamples, converged);
}

#endif
