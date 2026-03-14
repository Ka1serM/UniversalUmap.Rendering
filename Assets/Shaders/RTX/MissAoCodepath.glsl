#include "../SharedStructs.h"
#include "../Common.glsl"
#include "../PathTracing/ShadeMiss.glsl"

layout (push_constant, scalar) uniform PushConstants {
    PushDataGpu pushConstants;
};

layout(location = 0) rayPayloadInEXT AoPayload payload;

void main()
{
    shadeMiss(gl_WorldRayDirectionEXT, sceneSettings.environment, sceneSettings.renderSettings, payload);
}
