#ifndef RAY_GENERATION_GLSL
#define RAY_GENERATION_GLSL

vec2 concentricSampleDisk(float u1, float u2) {
    float offsetX = 2.0 * u1 - 1.0;
    float offsetY = 2.0 * u2 - 1.0;
    if (offsetX == 0.0 && offsetY == 0.0)
    return vec2(0.0);

    float r, theta;
    if (abs(offsetX) > abs(offsetY)) {
        r = offsetX;
        theta = (PI / 4.0) * (offsetY / offsetX);
    } else {
        r = offsetY;
        theta = (PI / 2.0) - (PI / 4.0) * (offsetX / offsetY);
    }
    return r * vec2(cos(theta), sin(theta));
}

vec2 roundBokeh(float u1, float u2, float edgeBias) {
    vec2 diskSample = concentricSampleDisk(u1, u2);
    float r = length(diskSample);
    float newR = pow(r, 1.0 / max(edgeBias, EPSILON));
    if (r > 0.0)
    return diskSample * (newR / r);
    return vec2(0.0);
}

// Generates a primary camera ray using the R2 sampler.
void generatePrimaryRay(
in ivec2 pixelCoord,
in ivec2 screenSize,
in CameraData camera,
inout SamplerState samplerState,
in bool deterministicSample,
out vec3 rayOrigin,
out vec3 rayDirection
) {
    // Keep sample 0 deterministic for stable auxiliary buffers (crypto/position/normal/albedo).
    vec2 jitter = deterministicSample ? vec2(0.0) : (getSample2D(samplerState) - 0.5);

    // Compute normalized UV coordinates with jitter
    vec2 uv = (vec2(pixelCoord) + jitter) / vec2(screenSize);

    // Map UV from [0,1] to sensor space [-0.5, 0.5]
    vec2 sensorOffset = uv - 0.5;

    // Camera parameters
    const vec3 camPos = camera.position;
    const vec3 camDir = normalize(camera.direction);
    const vec3 horizontal = camera.horizontal;
    const vec3 vertical = camera.vertical;
    float focalLength = camera.focalLength * 0.001; // mm to m

    // Compute image plane point
    vec3 imagePlaneCenter = camPos + camDir * focalLength;
    vec3 imagePlanePoint = imagePlaneCenter + horizontal * sensorOffset.x + vertical * sensorOffset.y;

    // Initialize ray properties
    rayOrigin = camPos;
    rayDirection = normalize(imagePlanePoint - rayOrigin);

    // Apply depth of field if aperture is larger than a pinhole
    if (camera.aperture > 0.0) {
        float apertureRadius = (camera.focalLength / camera.aperture) * 0.5 * 0.001;
        // Sample 0 stays deterministic: no lens jitter for stable buffers.
        vec2 lensSampleRaw = deterministicSample ? vec2(0.5) : getSample2D(samplerState);
        vec2 lensSample = roundBokeh(lensSampleRaw.x, lensSampleRaw.y, camera.bokehBias) * apertureRadius;
        vec3 lensU = normalize(horizontal);
        vec3 lensV = normalize(vertical);
        vec3 rayOriginDOF = camPos + lensU * lensSample.x + lensV * lensSample.y;
        vec3 focusPoint = rayOrigin + rayDirection * camera.focusDistance;

        rayOrigin = rayOriginDOF;
        rayDirection = normalize(focusPoint - rayOriginDOF);
    }
}

vec3 accumulateBuffer(vec3 oldValue, vec3 newValue, float alpha, int frame) {
    vec3 newPremult = newValue * alpha;
    float frameF = float(frame);
    return (oldValue * frameF + newPremult) / (frameF + 1.0);
}

void primaryRayGen(ivec2 pixelCoord, ivec2 screenSize) {
    if (pixelCoord.x >= screenSize.x || pixelCoord.y >= screenSize.y)
        return;
    const ivec2 pickPixelCoord = ivec2(pixelCoord.x, (screenSize.y - 1) - pixelCoord.y);
    
    #ifdef USE_COMPUTE
        Payload payload;
    #endif

    uvec2 seed = pcg2d(uvec2(pixelCoord) ^ uvec2(pushConstants.push.frame * 16777619));
    uint rngState = seed.x;

    vec3 accumulatedColor = vec3(0.0);
    bool hitAnything = false;
    vec3 stableAlbedo = vec3(0.0);
    vec3 stableNormal = vec3(0.0);
    bool stableHit = false;

    for (int sampleIndex = 0; sampleIndex < pushConstants.push.samples; ++sampleIndex) {
        payload.lastBsdfPdf = 0.0;
        payload.lastNeeLightPdf = 0.0;
        payload.pad1 = 0u;
        payload.pad2 = 0u;
        payload.pad3 = 0u;

        SamplerState samplerState = initSamplerState(pixelCoord, pushConstants.push.frame + sampleIndex);
        bool deterministicSample = (sampleIndex == 0);

        vec3 rayOrigin, rayDirection;
        generatePrimaryRay(pixelCoord, screenSize, pushConstants.camera, samplerState, deterministicSample, rayOrigin, rayDirection);

        vec3 throughput = vec3(1.0);
        int diffuseCount = 0;
        int specularCount = 0;
        int transmissionCount = 0;
        int maxBounces = max(pushConstants.push.diffuseBounces, max(pushConstants.push.specularBounces, pushConstants.push.transmissionBounces));

        for (int bounce = 0; bounce < maxBounces; ++bounce) {
            payload.rngState = rngState;
            payload.emission = vec3(0.0);
            payload.attenuation = vec3(1.0);
            payload.depth = bounce;
            payload.flags = 0u;
            payload.objectIndex = INVALID_INSTANCE;

            #ifdef USE_COMPUTE
                traceRayCompute(rayOrigin, rayDirection, 0.001, 1000.0, payload);
            #else
                traceRayEXT(topLevelAS, gl_RayFlagsOpaqueEXT, 0xff, 0, 0, 0, rayOrigin, 0.001, rayDirection, 1000.0, 0);
            #endif

            rayOrigin = payload.position;
            rngState = payload.rngState;

            if ((payload.flags & BOUNCE_DIFFUSE) != 0u)
                diffuseCount++;
            if ((payload.flags & BOUNCE_SPECULAR) != 0u)
                specularCount++;
            if ((payload.flags & BOUNCE_TRANSMIT) != 0u)
                transmissionCount++;

            if (diffuseCount > pushConstants.push.diffuseBounces || specularCount > pushConstants.push.specularBounces || transmissionCount > pushConstants.push.transmissionBounces)
                payload.flags |= RAY_TERMINATED;

            if (bounce == 0) {
                if ((payload.flags & RAY_TRANSPARENT) != 0u) {
                    if ((payload.flags & RAY_TERMINATED) == 0u)
                        --bounce;
                    continue;
                }
                
                // Store crypto and position buffer directly from sample 0.
                if(sampleIndex == 0) {
                    imageStore(outputCrypto, pickPixelCoord, uvec4(payload.objectIndex, 0, 0, 0));
                    if (payload.objectIndex != INVALID_INSTANCE)
                        imageStore(outputPosition, pickPixelCoord, vec4(payload.position, 1.0));
                    else
                        imageStore(outputPosition, pickPixelCoord, vec4(0));

                    stableHit = ((payload.flags & ENV_TRANSPARENT) == 0u);
                    stableAlbedo = stableHit ? payload.albedo : vec3(0.0);
                    stableNormal = stableHit ? payload.normal : vec3(0.0);
                }
                
                hitAnything = ((payload.flags & ENV_TRANSPARENT) == 0u);
            }

            accumulatedColor += throughput * payload.emission;
            throughput *= payload.attenuation;
            rayDirection = payload.nextDirection;

            //RUSSIAN ROULETTE TERMINATION
            if (bounce > 2) { //start RR after a few bounces
                float p_continue = clamp(luminance(throughput), 0.05, 1.0);
                if (rand(payload.rngState) > p_continue) 
                  payload.flags |= RAY_TERMINATED;
                else
                  throughput /= p_continue; // keep estimator unbiased
            }

            if ((payload.flags & RAY_TERMINATED) != 0u)
                break;
        }
    }

    // Average per-pixel over samples
    vec3 newColor = accumulatedColor / float(pushConstants.push.samples);
    float newAlpha = float(hitAnything);
    float frameF = float(pushConstants.push.frame);

    // Load previous frame
    vec4 prevColorData = imageLoad(outputColor, pixelCoord);
    vec3 prevColorPremult = prevColorData.rgb * prevColorData.a;
    float prevAlpha = prevColorData.a;

    // Apply exposure
    vec3 newColorWithExposure = newColor * exp2(pushConstants.push.exposure);
    vec3 newColorPremult = newColorWithExposure * newAlpha;

    // Accumulate premultiplied color
    vec3 finalColorPremult = (prevColorPremult * frameF + newColorPremult) / (frameF + 1.0);
    float finalAlpha = (prevAlpha * frameF + newAlpha) / (frameF + 1.0);

    // Un-premultiply for storage
    vec3 finalColor = (finalAlpha > 0.0) ? finalColorPremult / finalAlpha : vec3(0.0);

    // Stable auxiliary buffers from deterministic sample 0 (no temporal accumulation).
    vec3 finalAlbedo = stableAlbedo;
    vec3 finalNormal = stableNormal;

    // Store results
    imageStore(outputColor, pixelCoord, vec4(finalColor, finalAlpha));
    imageStore(outputAlbedo, pixelCoord, vec4(finalAlbedo, 1.0));
    imageStore(outputNormal, pixelCoord, vec4(finalNormal, 0.0));
}

#endif // RAY_GENERATION_GLSL
