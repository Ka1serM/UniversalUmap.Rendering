// Legacy include path kept for callers that still expect PathTracerEntryCommon.glsl.
// The default path tracing variant lives here; AO and direct lighting have explicit entry files.
#include "PathTracerEntryShared.glsl"
#include "../PathTracing/IntersectionPathTracing.glsl"
#include "../PathTracing/PrimaryRayGen.glsl"

void main() {
    const ivec2 pixelCoord = ivec2(gl_GlobalInvocationID.xy);
    const ivec2 screenSize = imageSize(outputColor);
    primaryRayGen(pixelCoord, screenSize);
}
