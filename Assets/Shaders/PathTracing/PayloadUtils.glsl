#ifndef PAYLOAD_UTILS_GLSL
#define PAYLOAD_UTILS_GLSL

void initializePayload(out Payload payload, uint rngState, uint depth) {
    payload.attenuation = vec3(1.0);
    payload.flags = 0u;
    payload.emission = vec3(0.0);
    payload.pad0 = 0;
    payload.position = vec3(0.0);
    payload.depth = depth;
    payload.nextDirection = vec3(0.0);
    payload.rngState = rngState;
    payload.albedo = vec3(0.0);
    payload.roughness = 0.0;
    payload.normal = vec3(0.0);
    payload.objectIndex = INVALID_INSTANCE;
    payload.lastBsdfPdf = 0.0;
    payload.lastNeeLightPdf = 0.0;
    payload.pad1 = 0u;
    payload.pad2 = 0u;
    payload.pad3 = 0u;
}

void initializeAoPayload(out AoPayload payload, uint rngState) {
    payload.emission = vec3(0.0);
    payload.flags = 0u;
    payload.position = vec3(0.0);
    payload.rngState = rngState;
    payload.albedo = vec3(0.0);
    payload.objectIndex = INVALID_INSTANCE;
    payload.normal = vec3(0.0);
    payload.pad0 = 0u;
}

#endif
