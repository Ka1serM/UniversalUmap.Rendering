#ifndef RAYTRACING_INTEGRATORS_SLANG
#define RAYTRACING_INTEGRATORS_SLANG

static const float QUEUE_AO_MAX_DISTANCE = 1.5;
static const int QUEUE_AO_SAMPLES = 4;

float estimateQueueAmbientOcclusion(float3 worldPosition, float3 geometricNormal, inout uint rngState)
{
    float occluded = 0.0;
    for (int i = 0; i < QUEUE_AO_SAMPLES; ++i) {
        float3 dir = sampleDiffuse(geometricNormal, rngState);
        float nDotL = max(dot(geometricNormal, dir), 0.0);
        float bias = computeShadowBias(worldPosition, nDotL);
        float3 origin = worldPosition + geometricNormal * bias;
        if (traceShadowPath(origin, dir, bias, QUEUE_AO_MAX_DISTANCE))
            occluded += 1.0;
    }

    return 1.0 - (occluded / float(QUEUE_AO_SAMPLES));
}

interface IPathIntegrator
{
    uint sampleCount();
    void shadeMiss(float3 worldRayDirection, inout PathState state);
    void shadeSurface(SurfaceData surface, float3 worldRayDirection, uint sampleIndex, inout PathState state, inout Payload payload);
    float3 filterRadiance(PathState state, float3 referenceRadiance);
};

struct AoIntegrator : IPathIntegrator
{
    uint sampleCount()
    {
        return uint(max(sceneSettings.RenderSettings.Samples, 1));
    }

    void shadeMiss(float3 worldRayDirection, inout PathState state)
    {
        uint missFlags = uint(BounceType.RAY_TERMINATED);
        float3 envColor = evaluateEnvironmentVisibleRadiance(worldRayDirection, sceneSettings.Environment);
        if (sceneSettings.Environment.Visible == 0 || sceneSettings.RenderSettings.TransparentBackground != 0)
            missFlags |= uint(BounceType.ENV_TRANSPARENT);

        state.Radiance += envColor;
        state.Flags = missFlags;
        setHitAnything(state, (missFlags & uint(BounceType.ENV_TRANSPARENT)) == 0u);
        state.PrimaryObjectIndex = InvalidInstance;
        state.PrimaryAlbedo = getHitAnything(state) ? envColor : float3(0.0);
        state.PrimaryNormal = float3(0.0);
        state.PrimaryPosition = float3(0.0);
    }

    void shadeSurface(SurfaceData surface, float3 worldRayDirection, uint sampleIndex, inout PathState state, inout Payload payload)
    {
        sampleIndex = sampleIndex;
        float3 viewDir = fastNormalize(-worldRayDirection);
        float3 geometricFacingNormal = computeFacingGeometricNormal(surface.GeometricNormal, viewDir);

        if (rand(payload.RngState) > surface.Opacity) {
            state.RngState = payload.RngState;
            state.RayOrigin = surface.WorldPosition + worldRayDirection * computeRayOriginBias(surface.WorldPosition);
            state.RayDirection = worldRayDirection;
            state.Flags = uint(BounceType.RAY_TRANSPARENT);
            enqueueContinuationRay(sampleIndex, state);
            return;
        }

        float3 facingNormal = computeFacingShadingNormal(surface.ShadingNormal, viewDir, geometricFacingNormal);
        float ao = estimateQueueAmbientOcclusion(surface.WorldPosition, geometricFacingNormal, payload.RngState);
        float3 aoColor = sceneSettings.RenderSettings.AoSampleAlbedo != 0
            ? surface.Albedo * ao
            : float3(ao);

        state.Radiance += aoColor;
        setHitAnything(state, true);
        state.PrimaryAlbedo = surface.Albedo;
        state.PrimaryNormal = facingNormal * 0.5 + 0.5;
        state.PrimaryPosition = surface.WorldPosition;
        state.RngState = payload.RngState;
        state.Flags = uint(BounceType.RAY_TERMINATED);
        pathStates[sampleIndex] = state;
    }

    float3 filterRadiance(PathState state, float3 referenceRadiance)
    {
        state = state;
        referenceRadiance = referenceRadiance;
        return max(state.Radiance, float3(0.0));
    }
};

struct PathTracingIntegrator : IPathIntegrator
{
    uint sampleCount()
    {
        return 1u;
    }

    void shadeMiss(float3 worldRayDirection, inout PathState state)
    {
        uint missFlags;
        float3 missRadiance = evaluateMissRadiance(worldRayDirection, state.Depth, state.LastBsdfPdf, missFlags);
        state.Radiance += state.Throughput * missRadiance;
        state.Flags = missFlags;
        if (state.Depth == 0u) {
            setHitAnything(state, (missFlags & uint(BounceType.ENV_TRANSPARENT)) == 0u);
            state.PrimaryObjectIndex = InvalidInstance;
            state.PrimaryPosition = float3(0.0);
            state.PrimaryAlbedo = getHitAnything(state) ? missRadiance : float3(0.0);
            state.PrimaryNormal = float3(0.0);
        }
    }

    void shadeSurface(SurfaceData surface, float3 worldRayDirection, uint sampleIndex, inout PathState state, inout Payload payload)
    {
        float3 viewDir = fastNormalize(-worldRayDirection);
        float3 geometricFacingNormal = computeFacingGeometricNormal(surface.GeometricNormal, viewDir);

        if (rand(payload.RngState) > surface.Opacity) {
            state.RngState = payload.RngState;
            state.RayOrigin = surface.WorldPosition + worldRayDirection * computeRayOriginBias(surface.WorldPosition);
            state.RayDirection = worldRayDirection;
            state.Flags = uint(BounceType.RAY_TRANSPARENT);
            enqueueContinuationRay(sampleIndex, state);
            return;
        }

        float3 albedo = surface.Albedo;
        float3 emission = surface.Emission;
        float metallic = surface.Metallic;
        float specular = surface.Specular;
        float roughness = regularizeSecondaryRoughness(surface.Roughness, state.Depth);
        float transmission = surface.Transmission;
        float3 facingNormal = computeFacingShadingNormal(surface.ShadingNormal, viewDir, geometricFacingNormal);

        if (state.Depth == 0u) {
            setHitAnything(state, true);
            state.PrimaryAlbedo = albedo;
            state.PrimaryNormal = facingNormal * 0.5 + 0.5;
            state.PrimaryPosition = surface.WorldPosition;
        }

        state.Radiance += state.Throughput * emission;

        float transmissionProbability = clamp(transmission, 0.0, 1.0);
        float opaqueProbability = max(1.0 - transmissionProbability, EPSILON);
        if (rand(payload.RngState) < transmissionProbability) {
            handleDielectricBSDF(viewDir, facingNormal, roughness, surface.Ior, surface.TransmissionColor, payload);
            payload.Attenuation /= max(transmissionProbability, EPSILON);
        } else {
            float dielectricF0 = dielectricF0FromSpecular(specular);
            float3 F0 = lerp(float3(dielectricF0), albedo, metallic);
            float swSpecular = computeSpecularSamplingWeight(viewDir, facingNormal, albedo, F0, metallic, roughness);

            queueDirectionalShadowRay(
                sampleIndex,
                surface.WorldPosition,
                facingNormal,
                geometricFacingNormal,
                viewDir,
                albedo,
                specular,
                metallic,
                roughness,
                swSpecular,
                payload.RngState);
            queueEnvironmentShadowRay(
                sampleIndex,
                surface.WorldShadowPosition,
                facingNormal,
                geometricFacingNormal,
                viewDir,
                albedo,
                specular,
                metallic,
                roughness,
                swSpecular,
                payload.RngState);

            handleOpaqueBSDF(viewDir, facingNormal, geometricFacingNormal, albedo, metallic, specular, roughness, payload);
            payload.Attenuation /= opaqueProbability;
        }

        payload.NextDirection = fastNormalize(payload.NextDirection);
        if ((payload.Flags & uint(BounceType.BOUNCE_DIFFUSE)) != 0u)
            incrementDiffuseCount(state);
        if ((payload.Flags & uint(BounceType.BOUNCE_SPECULAR)) != 0u)
            incrementSpecularCount(state);
        if ((payload.Flags & uint(BounceType.BOUNCE_TRANSMIT)) != 0u)
            incrementTransmissionCount(state);

        state.Throughput *= payload.Attenuation;
        state.RngState = payload.RngState;
        state.LastBsdfPdf = payload.LastBsdfPdf;
        state.Flags = payload.Flags;

        if (getDiffuseCount(state) > uint(sceneSettings.RenderSettings.DiffuseBounces) ||
            getSpecularCount(state) > uint(sceneSettings.RenderSettings.SpecularBounces) ||
            getTransmissionCount(state) > uint(sceneSettings.RenderSettings.TransmissionBounces)) {
            pathStates[sampleIndex] = state;
            return;
        }

        float russianRouletteSurvivalProbability = computeRussianRouletteSurvivalProbability(
            state.Throughput,
            int(state.Depth),
            effectiveRussianRouletteStartBounce());
        if (russianRouletteSurvivalProbability < 1.0) {
            if (rand(state.RngState) > russianRouletteSurvivalProbability) {
                pathStates[sampleIndex] = state;
                return;
            }

            state.Throughput /= russianRouletteSurvivalProbability;
        }

        state.RayOrigin = offsetRayOrigin(surface.WorldPosition, geometricFacingNormal, payload.NextDirection);
        state.RayDirection = payload.NextDirection;
        state.Depth++;
        enqueueContinuationRay(sampleIndex, state);
    }

    float3 filterRadiance(PathState state, float3 referenceRadiance)
    {
        return suppressFireflies(
            state.Radiance,
            referenceRadiance,
            int(getDiffuseCount(state) + getSpecularCount(state) + getTransmissionCount(state)),
            int(getDiffuseCount(state)),
            int(getSpecularCount(state)),
            int(getTransmissionCount(state)));
    }
};

#if defined(INTEGRATOR_AO)
#define ACTIVE_INTEGRATOR AoIntegrator
#elif defined(INTEGRATOR_PATH_TRACING)
#define ACTIVE_INTEGRATOR PathTracingIntegrator
#else
#error "Missing integrator define"
#endif

uint integratorSampleCount()
{
    ACTIVE_INTEGRATOR integrator;
    return integrator.sampleCount();
}

void integratorShadeMiss(float3 worldRayDirection, inout PathState state)
{
    ACTIVE_INTEGRATOR integrator;
    integrator.shadeMiss(worldRayDirection, state);
}

void integratorShadeSurface(SurfaceData surface, float3 worldRayDirection, uint sampleIndex, inout PathState state, inout Payload payload)
{
    ACTIVE_INTEGRATOR integrator;
    integrator.shadeSurface(surface, worldRayDirection, sampleIndex, state, payload);
}

float3 integratorFilterRadiance(PathState state, float3 referenceRadiance)
{
    ACTIVE_INTEGRATOR integrator;
    return integrator.filterRadiance(state, referenceRadiance);
}

#endif
