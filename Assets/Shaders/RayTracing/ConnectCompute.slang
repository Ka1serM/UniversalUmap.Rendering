#ifndef RAYTRACING_CONNECT_SLANG
#define RAYTRACING_CONNECT_SLANG

#include "../Shared.slang"
#include "../Bindings.slang"

[[vk::push_constant]] ConstantBuffer<PushDataGpu> pushConstants;

#include "Common.slang"

[shader("compute")]
[numthreads(QUEUE_GROUP_SIZE, 1, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    dispatchThreadID = dispatchThreadID;
    while (true) {
        uint workIndex;
        if (!popQueueIndex(QUEUE_COUNTER_SHADOW, workIndex))
            return;

        ShadowWorkItem shadow = shadowQueue[workIndex];
        if (!traceShadowPath(shadow.Origin, shadow.Direction, shadow.TMin, shadow.TMax)) {
            PathState state = pathStates[shadow.SampleIndex];
            state.Radiance += state.Throughput * shadow.Contribution;
            pathStates[shadow.SampleIndex] = state;
        }
    }
}

#endif
