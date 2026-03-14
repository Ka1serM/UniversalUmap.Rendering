#include "RayGenerationShared.glsl"

layout (location = 0) rayPayloadEXT Payload payload;
#include "../PathTracing/PrimaryRayGenDirect.glsl"

void main() {
    const ivec2 pixelCoord = ivec2(gl_LaunchIDEXT.xy);
    const ivec2 screenSize = ivec2(gl_LaunchSizeEXT.xy);
    primaryRayGenDirect(pixelCoord, screenSize);
}
