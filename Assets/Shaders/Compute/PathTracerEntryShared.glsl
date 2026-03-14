#include "../SharedStructs.h"

layout (push_constant, scalar) uniform PushConstants {
    PushDataGpu pushConstants;
};

#include "../Bindings.glsl"
#include "../Common.glsl"

layout (local_size_x = GROUP_SIZE, local_size_y = GROUP_SIZE) in;
