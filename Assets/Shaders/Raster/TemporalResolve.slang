#include "../Shared.slang"
#include "../Bindings.slang"

struct TaaPushConstants
{
    int frame;
    int enabled;
    float blendFactor;
    int isMoving;
};

[[vk::push_constant]] ConstantBuffer<TaaPushConstants> pc;

float3 clipAABB(float3 color, float3 minColor, float3 maxColor)
{
    float3 center = (maxColor + minColor) * 0.5;
    float3 delta = color - center;
    float3 halfExtent = (maxColor - minColor) * 0.5 + 1.0e-6;
    float3 clamped = delta / halfExtent;
    float maxComp = max(max(abs(clamped.x), abs(clamped.y)), abs(clamped.z));
    return maxComp > 1.0
        ? center + delta * (1.0 / maxComp)
        : color;
}

float luminance(float3 color)
{
    return dot(color, float3(0.2126, 0.7152, 0.0722));
}

[shader("compute")]
[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint width, height;
    outputColor.GetDimensions(width, height);
    int2 pixel = int2(dispatchThreadID.xy);
    if (pixel.x >= int(width) || pixel.y >= int(height)) return;

    if (pc.enabled == 0 || pc.frame == 0)
    {
        taaOutputColor[pixel] = outputColor[pixel];
        return;
    }

    float4 curr = outputColor[pixel];
    if (pc.isMoving != 0 && outputPosition[pixel].w <= 0.5)
    {
        taaOutputColor[pixel] = curr;
        return;
    }

    float2 pixelUv = (float2(pixel) + 0.5) / float2(width, height);
    float2 velocity = rasterVelocity.SampleLevel(pixelUv, 0.0);
    float2 historyUv = (float2(pixel) + 0.5) / float2(width, height) + velocity;

    if (any(historyUv < 0.0) || any(historyUv > 1.0))
    {
        taaOutputColor[pixel] = curr;
        return;
    }

    float4 hist = rasterHistory.SampleLevel(historyUv, 0.0);

    float3 minc = curr.rgb;
    float3 maxc = curr.rgb;
    [unroll]
    for (int dy = -1; dy <= 1; dy++)
    {
        [unroll]
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            int2 np = clamp(pixel + int2(dx, dy), int2(0), int2(width - 1, height - 1));
            float3 nc = outputColor[np].rgb;
            minc = min(minc, nc);
            maxc = max(maxc, nc);
        }
    }

    float3 clamped = clipAABB(hist.rgb, minc, maxc);
    float lumDelta = abs(luminance(curr.rgb) - luminance(clamped));
    float lumScale = max(max(luminance(curr.rgb), luminance(clamped)), 1.0e-3);
    float historyWeight = pc.blendFactor * (1.0 - saturate(lumDelta / lumScale));
    taaOutputColor[pixel] = float4(lerp(curr.rgb, clamped, historyWeight), curr.a);
}
