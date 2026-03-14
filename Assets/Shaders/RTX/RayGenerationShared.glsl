#include "../SharedStructs.h"
#include "../Common.glsl"
#include "../Bindings.glsl"

layout (push_constant, scalar) uniform PushConstants {
    PushDataGpu pushConstants;
};
