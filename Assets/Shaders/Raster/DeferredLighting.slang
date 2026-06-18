#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Bxdfs/Brdf.slang"
#include "../Bxdfs/PbrBxdf.slang"
#include "../Geometry/Intersection.slang"
#include "../Lights/DirectionalLight.slang"
#include "../Lights/EnvironmentLight.slang"

// ─── GTAO constants ─────────────────────────────────────────────────────────
static const float GTAO_RADIUS = 1.5;
static const int GTAO_SLICES = 6;
static const int GTAO_STEPS = 4;
static const float GTAO_POWER = 1.5;
static const float GTAO_MAX_SCREEN_RADIUS = 64.0;
static const float GTAO_THICKNESS = 0.25;

// ─── SSR constants ──────────────────────────────────────────────────────────
static const int SSR_STEPS = 40;
static const int SSR_REFINE_STEPS = 5;
static const float SSR_MAX_DISTANCE = 40.0;
static const float SSR_MAX_ROUGHNESS = 0.82;

// ─── CSM PCF/PCSS constants ────────────────────────────────────────────────
static const int CSM_BLOCKER_SAMPLES = 16;
static const int CSM_FILTER_SAMPLES = 32;
static const float CSM_BLOCKER_RADIUS_TEXELS = 8.0;
static const float CSM_LIGHT_RADIUS_TEXELS = 42.0;
static const float CSM_MIN_FILTER_RADIUS = 1.0;
static const float CSM_MAX_FILTER_RADIUS = 14.0;
static const float CSM_DEPTH_BIAS_MIN = 0.00012;
static const float CSM_DEPTH_BIAS_SLOPE = 0.00125;

// ─── Light probe constants ─────────────────────────────────────────────────
static const int PROBE_STRIDE = 8;

// ─── Surface data ──────────────────────────────────────────────────────────
struct DeferredSurface
{
    bool Valid;
    float3 Position;
    float3 Normal;
    float3 Albedo;
    float3 Emission;
    float Metallic;
    float Specular;
    float Roughness;
    float Transmission;
    float Opacity;
};

// ─── SH evaluation ─────────────────────────────────────────────────────────
void evalSHBasis(float3 dir, out float sh[9])
{
    float x = dir.x, y = dir.y, z = dir.z;
    sh[0] = 0.282095;
    sh[1] = 0.488603 * y;
    sh[2] = 0.488603 * z;
    sh[3] = 0.488603 * x;
    sh[4] = 1.092548 * x * y;
    sh[5] = 1.092548 * y * z;
    sh[6] = 0.315392 * (3.0 * z * z - 1.0);
    sh[7] = 1.092548 * x * z;
    sh[8] = 0.546274 * (x * x - y * y);
}

void unpackProbeSH(float4 slots[8], out float3 sh[9])
{
    sh[0] = float3(slots[1].x, slots[3].x, slots[5].x);
    sh[1] = float3(slots[1].y, slots[3].y, slots[5].y);
    sh[2] = float3(slots[1].z, slots[3].z, slots[5].z);
    sh[3] = float3(slots[1].w, slots[4].x, slots[6].x);
    sh[4] = float3(slots[2].x, slots[4].y, slots[6].y);
    sh[5] = float3(slots[2].y, slots[4].z, slots[6].z);
    sh[6] = float3(slots[2].z, slots[4].w, slots[6].w);
    sh[7] = float3(slots[2].w, slots[5].w, slots[7].x);
    sh[8] = float3(slots[3].w, slots[7].z, slots[7].y);
}

float3 evaluateSH(float3 dir, float3 sh[9])
{
    float basis[9];
    evalSHBasis(dir, basis);
    float3 result = float3(0.0);
    for (int i = 0; i < 9; i++)
        result += sh[i] * basis[i];
    return max(result, float3(0.0));
}

float3 evaluateLightProbeGI(float3 worldPos, float3 normal)
{
    LightProbeGridDataGpu grid = sceneSettings.LightProbeGrid;
    if (grid.GridDimX == 0 || grid.GridDimY == 0 || grid.GridDimZ == 0)
        return float3(0.0);

    float3 gridMin = grid.GridMin;
    float3 gridMax = grid.GridMax;
    float3 cellSize = (gridMax - gridMin) / float3(float(grid.GridDimX), float(grid.GridDimY), float(grid.GridDimZ));
    if (any(cellSize <= 0.0)) return float3(0.0);

    float3 t = (worldPos - gridMin) / (gridMax - gridMin);
    t = saturate(t);
    float3 f = t * float3(float(grid.GridDimX), float(grid.GridDimY), float(grid.GridDimZ)) - 0.5;
    int3 i0 = int3(floor(f));
    int3 i1 = min(i0 + 1, int3(grid.GridDimX - 1, grid.GridDimY - 1, grid.GridDimZ - 1));
    float3 w = frac(f);
    i0 = max(i0, int3(0));

    float3 result = float3(0.0);
    float totalWeight = 0.0;

    for (int k = 0; k <= 1; k++)
    {
        for (int j = 0; j <= 1; j++)
        {
            for (int i = 0; i <= 1; i++)
            {
                int3 idx = int3(
                    i == 0 ? i0.x : i1.x,
                    j == 0 ? i0.y : i1.y,
                    k == 0 ? i0.z : i1.z);

                float weight = (i == 0 ? 1.0 - w.x : w.x)
                             * (j == 0 ? 1.0 - w.y : w.y)
                             * (k == 0 ? 1.0 - w.z : w.z);
                if (weight <= 0.0) continue;

                int linearIdx = idx.z * grid.GridDimY * grid.GridDimX + idx.y * grid.GridDimX + idx.x;
                int baseSlot = linearIdx * PROBE_STRIDE;

                float4 slots[8];
                for (int s = 0; s < 8; s++)
                    slots[s] = lightProbeData[baseSlot + s];

                float3 sh[9];
                unpackProbeSH(slots, sh);

                float3 radiance = evaluateSH(normal, sh);
                result += radiance * weight;
                totalWeight += weight;
            }
        }
    }

    return totalWeight > 0.0 ? result / totalWeight : float3(0.0);
}

// ─── IBL helpers ───────────────────────────────────────────────────────────
float3 sampleDiffuseEnvironmentDeferred(float3 normal, EnvironmentDataGpu envData)
{
    if (envData.IrradianceMapIndex >= 0)
    {
        float2 uv = directionToEnvironmentUv(normal, envData);
        return textureSamplers[envData.IrradianceMapIndex].Sample(uv).rgb * envData.LightingExposureScale;
    }
    return sampleEnvironmentMapColorLod(normal, envData, envData.LightingExposureScale, max(envData.MaxTextureLod, 0.0));
}

float3 sampleSpecularEnvironmentDeferred(float3 reflectedDir, float roughness, EnvironmentDataGpu envData)
{
    float lod = clamp(roughness, 0.0, 1.0) * max(envData.MaxTextureLod, 0.0);
    int specIdx = envData.RadianceMapIndex >= 0 ? envData.RadianceMapIndex : envData.TextureIndex;
    if (specIdx < 0) return float3(0.0);
    float2 uv = directionToEnvironmentUv(reflectedDir, envData);
    return textureSamplers[specIdx].SampleLevel(uv, max(lod, 0.0)).rgb * envData.LightingExposureScale;
}

// ─── Surface loading ───────────────────────────────────────────────────────
DeferredSurface loadSurface(int2 pixel)
{
    DeferredSurface surface;
    float4 packedPos = outputPosition[pixel];
    surface.Valid = packedPos.w > 0.5;
    surface.Position = packedPos.xyz;
    surface.Normal = normalize(outputNormal[pixel].xyz * 2.0 - 1.0);
    surface.Albedo = outputAlbedo[pixel].rgb;
    float4 emissionOpacity = outputColor[pixel];
    surface.Emission = emissionOpacity.rgb;
    surface.Opacity = emissionOpacity.a;
    float4 mat = outputMaterial[pixel];
    surface.Metallic = saturate(mat.x);
    surface.Specular = saturate(mat.y);
    surface.Roughness = saturate(max(outputNormal[pixel].w, mat.z));
    surface.Transmission = saturate(mat.w);
    return surface;
}

// ─── Utility ───────────────────────────────────────────────────────────────
float screenEdgeFade(float2 uv, int2 screenSize)
{
    float aspect = float(screenSize.y) / max(float(screenSize.x), 1.0);
    float2 fadeWidth = 0.05 * float2(aspect, 1.0);
    float2 fadeIn = smoothstep(float2(0.0), fadeWidth, uv);
    float2 fadeOut = 1.0 - smoothstep(1.0 - fadeWidth, float2(1.0), uv);
    return saturate(fadeIn.x * fadeIn.y * fadeOut.x * fadeOut.y);
}

bool projectWorldToUv(float3 worldPos, out float2 uv, out float cameraDistance)
{
    float4 clip = mul(float4(worldPos, 1.0), sceneSettings.RasterCamera.WorldToClip);
    if (clip.w <= 0.0) { uv = float2(0.0); cameraDistance = 0.0; return false; }
    float3 ndc = clip.xyz / clip.w;
    if (ndc.x < -1.0 || ndc.x > 1.0 || ndc.y < -1.0 || ndc.y > 1.0 || ndc.z < 0.0 || ndc.z > 1.0)
    { uv = float2(0.0); cameraDistance = 0.0; return false; }
    uv = ndc.xy * 0.5 + 0.5;
    float3 cf = normalize(sceneSettings.RasterCamera.Direction);
    cameraDistance = max(dot(worldPos - sceneSettings.RasterCamera.Position, cf), sceneSettings.RasterCamera.NearPlane);
    return true;
}

float cameraLinearDepth(float3 worldPos)
{
    float3 cf = normalize(sceneSettings.RasterCamera.Direction);
    return max(dot(worldPos - sceneSettings.RasterCamera.Position, cf), sceneSettings.RasterCamera.NearPlane);
}

float computeScreenSpaceRayOriginBias(float3 worldPos)
{
    return max(computeRayOriginBias(worldPos) * 4.0, cameraLinearDepth(worldPos) * 2.5e-4);
}

// ─── GTAO (Ground Truth Ambient Occlusion) ──────────────────────────────────
bool loadGbufferPosition(int2 pixel, int2 screenSize, out float3 pos, out float linearDepth)
{
    if (pixel.x < 0 || pixel.y < 0 || pixel.x >= screenSize.x || pixel.y >= screenSize.y)
    { pos = float3(0.0); linearDepth = 0.0; return false; }
    float4 packed = outputPosition[pixel];
    if (packed.w <= 0.5) { pos = float3(0.0); linearDepth = 0.0; return false; }
    pos = packed.xyz;
    linearDepth = cameraLinearDepth(pos);
    return true;
}

float3 normalizedDeltaOrZero(float3 from, float3 to)
{
    float3 d = to - from;
    float l = length(d);
    return l > EPSILON ? d / l : float3(0.0);
}

float3 computeScreenSpaceNormal(int2 pixel, int2 screenSize, DeferredSurface surface, float3 viewVec)
{
    float centerDepth = cameraLinearDepth(surface.Position);
    float3 leftPos, rightPos, topPos, bottomPos;
    float leftD, rightD, topD, bottomD;
    bool hl = loadGbufferPosition(pixel + int2(-1, 0), screenSize, leftPos, leftD);
    bool hr = loadGbufferPosition(pixel + int2(1, 0), screenSize, rightPos, rightD);
    bool ht = loadGbufferPosition(pixel + int2(0, -1), screenSize, topPos, topD);
    bool hb = loadGbufferPosition(pixel + int2(0, 1), screenSize, bottomPos, bottomD);

    float4 edges = float4(hl ? leftD - centerDepth : 0.0, hr ? rightD - centerDepth : 0.0,
                          ht ? topD - centerDepth : 0.0, hb ? bottomD - centerDepth : 0.0);
    float slopeLR = (edges.y - edges.x) * 0.5;
    float slopeTB = (edges.w - edges.z) * 0.5;
    float4 slopeAdj = edges + float4(slopeLR, -slopeLR, slopeTB, -slopeTB);
    edges = min(abs(edges), abs(slopeAdj));
    edges = saturate(1.25 - edges / max(centerDepth * 0.011, 1.0e-3));
    edges *= float4(hl ? 1.0 : 0.0, hr ? 1.0 : 0.0, ht ? 1.0 : 0.0, hb ? 1.0 : 0.0);

    float3 lDir = hl ? normalizedDeltaOrZero(surface.Position, leftPos) : float3(0.0);
    float3 rDir = hr ? normalizedDeltaOrZero(surface.Position, rightPos) : float3(0.0);
    float3 tDir = ht ? normalizedDeltaOrZero(surface.Position, topPos) : float3(0.0);
    float3 bDir = hb ? normalizedDeltaOrZero(surface.Position, bottomPos) : float3(0.0);
    float4 accepted = saturate(float4(edges.x * edges.z, edges.z * edges.y, edges.y * edges.w, edges.w * edges.x) + 0.01);
    accepted *= float4((hl && ht) ? 1.0 : 0.0, (ht && hr) ? 1.0 : 0.0, (hr && hb) ? 1.0 : 0.0, (hb && hl) ? 1.0 : 0.0);

    float3 normalSum = accepted.x * cross(lDir, tDir) + accepted.y * cross(tDir, rDir)
                     + accepted.z * cross(rDir, bDir) + accepted.w * cross(bDir, lDir);
    if (length(normalSum) <= EPSILON) return surface.Normal;
    float3 posNormal = normalize(normalSum);
    return dot(posNormal, viewVec) < 0.0 ? -posNormal : posNormal;
}

float computeGtaoVisibility(int2 pixel, int2 screenSize, DeferredSurface surface)
{
    float centerDepth = cameraLinearDepth(surface.Position);
    float effectRadius = min(GTAO_RADIUS, max(centerDepth * 0.5, GTAO_RADIUS * 0.25));

    RasterCameraDataGpu cam = sceneSettings.RasterCamera;
    float pxWX = 2.0 * length(cam.Horizontal) * centerDepth / max(float(screenSize.x), 1.0);
    float pxWY = 2.0 * length(cam.Vertical) * centerDepth / max(float(screenSize.y), 1.0);
    float pxW = max(pxWX, pxWY);
    if (pxW <= 1.0e-4) return 1.0;
    float screenRadius = clamp(effectRadius / pxW, 0.0, GTAO_MAX_SCREEN_RADIUS);
    if (screenRadius < 1.3) return 1.0;

    float3 camRight = normalize(cam.Horizontal);
    float3 camUp = normalize(cam.Vertical);
    float3 viewVec = normalize(cam.Position - surface.Position);
    float3 ssNormal = computeScreenSpaceNormal(pixel, screenSize, surface, viewVec);

    float ao = 0.0;
    for (int si = 0; si < GTAO_SLICES; si++)
    {
        float phi = (float(si) / float(GTAO_SLICES)) * PI;
        float2 sDir = float2(cos(phi), sin(phi));

        [unroll] for (int side = 0; side < 2; side++)
        {
            float sideSign = side == 0 ? -1.0 : 1.0;
            float maxCosHorizon = -1.0;

            [unroll] for (int step = 0; step < GTAO_STEPS; step++)
            {
                float step01 = (float(step) + 0.5) / float(GTAO_STEPS);
                float distPx = max(1.3, pow(step01, 1.8) * screenRadius);
                int2 off = int2(round(sDir * (distPx * sideSign)));
                if (off.x == 0 && off.y == 0) continue;
                int2 sp = pixel + off;

                float3 sampPos; float sampD;
                if (!loadGbufferPosition(sp, screenSize, sampPos, sampD)) continue;

                float3 delta = sampPos - surface.Position;
                float dist = length(delta);
                if (dist <= 1.0e-6 || dist > effectRadius) continue;

                float planeDist = dot(delta, ssNormal);
                float sameSurfEps = max(computeScreenSpaceRayOriginBias(surface.Position), dist * 0.015);
                if (abs(planeDist) <= sameSurfEps) continue;
                if (planeDist < 0.0) continue;

                float3 dir = delta / dist;
                float cosAngle = dot(dir, ssNormal);
                maxCosHorizon = max(maxCosHorizon, cosAngle);
            }

            if (maxCosHorizon >= 0.0)
                ao += maxCosHorizon;
        }
    }

    float totalSlices = float(GTAO_SLICES * 2);
    float visibility = ao / totalSlices;
    visibility = pow(saturate(visibility), GTAO_POWER);
    return max(visibility, 0.03);
}

// ─── SSR ───────────────────────────────────────────────────────────────────
float3 estimateDeferredSurfaceRadiance(DeferredSurface surface)
{
    float3 vDir = fastNormalize(sceneSettings.RasterCamera.Position - surface.Position);
    float nDotV = max(dot(surface.Normal, vDir), 0.0);
    float dielecF0 = dielectricF0FromSpecular(surface.Specular);
    float3 f0 = lerp(float3(dielecF0), surface.Albedo, surface.Metallic);
    float3 diffColor = surface.Albedo * (1.0 - surface.Metallic);
    float3 diffEnv = sampleDiffuseEnvironmentDeferred(surface.Normal, sceneSettings.Environment);
    float3 reflDir = reflect(-vDir, surface.Normal);
    float3 specEnv = sampleSpecularEnvironmentDeferred(reflDir, surface.Roughness, sceneSettings.Environment);
    float3 rad = surface.Emission + diffEnv * diffColor * (1.0 / PI) + specEnv * envBrdfApprox(f0, surface.Roughness, nDotV);

    if (hasDirectionalLight(sceneSettings.Environment))
    {
        float3 lDir = getDirectionalLightDirection(sceneSettings.Environment);
        float nDotL = max(dot(surface.Normal, lDir), 0.0);
        if (nDotL > 0.0)
        {
            PbrBxdf bxdf;
            BxdfEval be = bxdf.evalOpaque(vDir, surface.Normal, surface.Normal, lDir, surface.Albedo,
                surface.Specular, surface.Metallic, surface.Roughness,
                computeSpecularSamplingWeight(vDir, surface.Normal, surface.Albedo, f0, surface.Metallic, surface.Roughness));
            rad += be.f * float3(sceneSettings.Environment.DirectionalIntensity) * nDotL;
        }
    }
    return max(rad, float3(0.0));
}

bool validateSsrHit(DeferredSurface surface, int2 screenSize, float3 reflDir, float3 originNormal,
    float3 rayPoint, float2 hitUv, float rayCamDist, out float3 color, out float conf)
{
    color = float3(0.0); conf = 0.0;
    int2 hp = clamp(int2(hitUv * float2(screenSize)), int2(0), max(screenSize - 1, int2(0)));
    DeferredSurface hs = loadSurface(hp);
    if (!hs.Valid || length(hs.Position - surface.Position) < 0.08) return false;
    if (abs(dot(hs.Position - surface.Position, originNormal)) < computeScreenSpaceRayOriginBias(surface.Position)
        && dot(hs.Normal, originNormal) > 0.8) return false;
    if (dot(hs.Normal, reflDir) > 0.0) return false;
    float hitCamDist = cameraLinearDepth(hs.Position);
    float depthDist = abs(hitCamDist - rayCamDist);
    float thick = max(0.08, length(rayPoint - surface.Position) * 0.025);
    conf = 1.0 - smoothstep(0.0, thick, depthDist);
    conf *= conf * screenEdgeFade(hitUv, screenSize);
    if (conf <= 0.0) return false;
    color = estimateDeferredSurfaceRadiance(hs);
    return true;
}

bool refineSsrHit(DeferredSurface surface, int2 screenSize, float3 reflDir, float3 originNormal,
    float prevDist, float hitDist, out float3 bestColor, out float bestConf)
{
    float minD = prevDist, maxD = hitDist;
    bestColor = float3(0.0); bestConf = 0.0;
    for (int i = 0; i < SSR_REFINE_STEPS; i++)
    {
        float mid = (minD + maxD) * 0.5;
        float3 rp = offsetRayOrigin(surface.Position, originNormal, reflDir) + reflDir * mid;
        float2 uv; float camDist;
        if (!projectWorldToUv(rp, uv, camDist)) { maxD = mid; continue; }
        int2 hp = clamp(int2(uv * float2(screenSize)), int2(0), max(screenSize - 1, int2(0)));
        float4 sp = outputPosition[hp];
        if (sp.w <= 0.5) { minD = mid; continue; }
        float hitCamDist = cameraLinearDepth(sp.xyz);
        if ((camDist - hitCamDist) > 0.0) maxD = mid; else minD = mid;
        float3 c; float cf;
        if (validateSsrHit(surface, screenSize, reflDir, originNormal, rp, uv, camDist, c, cf) && cf > bestConf)
        { bestColor = c; bestConf = cf; }
    }
    return bestConf > 0.0;
}

bool traceScreenSpaceReflection(int2 pixel, DeferredSurface surface, int2 screenSize, float3 viewDir,
    float3 originNormal, out float3 color, out float conf)
{
    color = float3(0.0); conf = 0.0;
    if (surface.Roughness > SSR_MAX_ROUGHNESS) return false;
    float3 reflDir = normalize(reflect(-viewDir, surface.Normal));
    if (dot(reflDir, surface.Normal) <= 0.0) return false;
    float3 origin = offsetRayOrigin(surface.Position, originNormal, reflDir);
    float prevDist = 0.0, prevDDelta = -1.0;
    for (int i = 1; i <= SSR_STEPS; i++)
    {
        float step01 = float(i) / float(SSR_STEPS);
        float rayDist = lerp(0.05, SSR_MAX_DISTANCE, step01 * step01);
        float3 rp = origin + reflDir * rayDist;
        float2 uv; float camDist;
        if (!projectWorldToUv(rp, uv, camDist)) { prevDist = rayDist; continue; }
        int2 sp = clamp(int2(uv * float2(screenSize)), int2(0), max(screenSize - 1, int2(0)));
        float4 samp = outputPosition[sp];
        if (samp.w <= 0.5 || (sp.x == pixel.x && sp.y == pixel.y) || length(samp.xyz - surface.Position) < 0.08)
        { prevDist = rayDist; continue; }
        float hitCamDist = cameraLinearDepth(samp.xyz);
        float thick = max(0.08, rayDist * 0.025);
        float dDelta = camDist - hitCamDist;
        if ((dDelta > 0.0 && dDelta <= thick) || (prevDDelta < 0.0 && dDelta > 0.0))
        {
            if (refineSsrHit(surface, screenSize, reflDir, originNormal, prevDist, rayDist, color, conf)) return true;
            if (validateSsrHit(surface, screenSize, reflDir, originNormal, rp, uv, camDist, color, conf)) return true;
        }
        prevDist = rayDist; prevDDelta = dDelta;
    }
    return false;
}

// ─── CSM PCSS ──────────────────────────────────────────────────────────────
float4x4 selectCascadeMatrix(int cascade)
{
    if (cascade == 0) return sceneSettings.RasterShadow.WorldToClip0;
    if (cascade == 1) return sceneSettings.RasterShadow.WorldToClip1;
    if (cascade == 2) return sceneSettings.RasterShadow.WorldToClip2;
    return sceneSettings.RasterShadow.WorldToClip3;
}

int selectCascade(float linearDepth)
{
    float4 farDists = sceneSettings.RasterShadow.CascadeFarDistances;
    if (linearDepth <= farDists.x) return 0;
    if (linearDepth <= farDists.y) return 1;
    if (linearDepth <= farDists.z) return 2;
    return 3;
}

float2 cascadeToAtlasUv(float2 cascadeUv, int cascade)
{
    float2 quadOffset = float2(float(cascade & 1) * 0.5, float(cascade >> 1) * 0.5);
    return cascadeUv * 0.5 + quadOffset;
}

int2 cascadeTileMinPixel(int cascade, int2 atlasSize)
{
    int2 tileSize = max(atlasSize / 2, int2(1));
    return int2(cascade & 1, cascade >> 1) * tileSize;
}

int2 cascadeTileMaxPixel(int cascade, int2 atlasSize)
{
    int2 tileSize = max(atlasSize / 2, int2(1));
    return cascadeTileMinPixel(cascade, atlasSize) + tileSize - int2(1);
}

float interleavedGradientNoise(int2 pixel)
{
    float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(magic.z * frac(dot(float2(pixel), magic.xy)));
}

float sampleCascadeShadowDepth(float2 atlasUv, int cascade, int2 atlasSize)
{
    int2 tileMin = cascadeTileMinPixel(cascade, atlasSize) + int2(1);
    int2 tileMax = cascadeTileMaxPixel(cascade, atlasSize) - int2(1);
    int2 p = int2(atlasUv * float2(atlasSize));
    p = clamp(p, tileMin, tileMax);
    return rasterShadowDepth[p];
}

float computeCsmPcssVisibility(float3 worldPos, float nDotL, float3 normal, float linearDepth)
{
    if (nDotL <= 0.0) return 1.0;

    int cascade = selectCascade(linearDepth);
    float4 clip = mul(float4(worldPos, 1.0), selectCascadeMatrix(cascade));
    if (clip.w <= 0.0) return 1.0;
    float3 ndc = clip.xyz / clip.w;
    if (ndc.x < -1.0 || ndc.x > 1.0 || ndc.y < -1.0 || ndc.y > 1.0 || ndc.z < 0.0 || ndc.z > 1.0)
        return 1.0;

    float2 cascadeUv = ndc.xy * 0.5 + 0.5;
    // Edge fade
    float edgeDist = min(min(cascadeUv.x, cascadeUv.y), min(1.0 - cascadeUv.x, 1.0 - cascadeUv.y));
    float edgeFade = smoothstep(0.0, 0.05, edgeDist);

    float2 atlasUv = cascadeToAtlasUv(cascadeUv, cascade);
    float receiverDepth = saturate(ndc.z);

    uint atlasW, atlasH;
    rasterShadowDepth.GetDimensions(atlasW, atlasH);
    int2 atlasSize = int2(atlasW, atlasH);
    float2 texelSize = 1.0 / float2(atlasSize);
    float depthBias = CSM_DEPTH_BIAS_MIN + CSM_DEPTH_BIAS_SLOPE * pow(saturate(1.0 - nDotL), 2.0);
    receiverDepth = saturate(receiverDepth - depthBias);
    float noise = interleavedGradientNoise(int2(cascadeUv * float2(atlasSize)));

    // Blocker search
    float blockerSum = 0.0;
    int blockerCount = 0;
    for (int i = 0; i < CSM_BLOCKER_SAMPLES; i++)
    {
        float angle = (float(i) + noise) * 2.399963;
        float radius = sqrt((float(i) + 0.5) / float(CSM_BLOCKER_SAMPLES));
        float2 off = float2(cos(angle), sin(angle)) * radius * CSM_BLOCKER_RADIUS_TEXELS * texelSize;
        float sampleDepth = sampleCascadeShadowDepth(atlasUv + off, cascade, atlasSize);
        if (sampleDepth < receiverDepth)
        {
            blockerSum += sampleDepth;
            blockerCount++;
        }
    }

    float blockerAvg = blockerCount > 0 ? blockerSum / float(blockerCount) : -1.0;
    if (blockerAvg < 0.0) return 1.0;

    // Penumbra estimation
    float blockerGap = max(receiverDepth - blockerAvg, 0.0);
    float penumbra = blockerGap / max(blockerAvg, 0.025);
    float filterRadius = clamp(penumbra * CSM_LIGHT_RADIUS_TEXELS, CSM_MIN_FILTER_RADIUS, CSM_MAX_FILTER_RADIUS);

    // PCF
    float shadow = 0.0;
    for (int i = 0; i < CSM_FILTER_SAMPLES; i++)
    {
        float angle = (float(i) + noise) * 2.399963;
        float radius = sqrt((float(i) + 0.5) / float(CSM_FILTER_SAMPLES));
        float2 off = float2(cos(angle), sin(angle)) * radius * filterRadius * texelSize;
        float sampleDepth = sampleCascadeShadowDepth(atlasUv + off, cascade, atlasSize);
        shadow += sampleDepth < receiverDepth ? 1.0 : 0.0;
    }
    shadow /= float(CSM_FILTER_SAMPLES);

    return lerp(1.0, 1.0 - shadow, edgeFade);
}

// ─── Main ──────────────────────────────────────────────────────────────────
[shader("compute")]
[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint width, height;
    outputColor.GetDimensions(width, height);
    if (dispatchThreadID.x >= width || dispatchThreadID.y >= height) return;

    int2 pixel = int2(dispatchThreadID.xy);
    int2 screenSize = int2(width, height);
    DeferredSurface surface = loadSurface(pixel);
    if (!surface.Valid) return;

    float3 viewDir = fastNormalize(sceneSettings.RasterCamera.Position - surface.Position);
    float3 originNormal = computeScreenSpaceNormal(pixel, screenSize, surface, viewDir);
    float nDotV = max(dot(surface.Normal, viewDir), 0.0);
    float dielectricF0 = dielectricF0FromSpecular(surface.Specular);
    float3 f0 = lerp(float3(dielectricF0), surface.Albedo, surface.Metallic);
    float3 diffuseColor = surface.Albedo * (1.0 - surface.Metallic);

    // GTAO
    float gtaoVis = computeGtaoVisibility(pixel, screenSize, surface);

    // IBL diffuse
    float3 diffuseEnvironment = sampleDiffuseEnvironmentDeferred(surface.Normal, sceneSettings.Environment);

    // IBL specular
    float3 reflectedDir = reflect(-viewDir, surface.Normal);
    float3 specularEnvironment = sampleSpecularEnvironmentDeferred(reflectedDir, surface.Roughness, sceneSettings.Environment);

    // Light probe GI
    float3 probeGI = evaluateLightProbeGI(surface.Position, surface.Normal);

    // Compose ambient
    float3 diffuseAmbient = diffuseEnvironment * diffuseColor * (1.0 / PI) * gtaoVis;
    float3 probeGIComponent = diffuseColor * probeGI * (1.0 / PI);

    float specularAo = computeSpecularOcclusion(nDotV, gtaoVis, surface.Roughness);
    float3 specularAmbient = specularEnvironment * envBrdfApprox(f0, surface.Roughness, nDotV) * specularAo;

    // SSR
    float3 ssrColor; float ssrConfidence;
    if (traceScreenSpaceReflection(pixel, surface, screenSize, viewDir, originNormal, ssrColor, ssrConfidence))
    {
        float ssrWeight = ssrConfidence * saturate(1.0 - surface.Roughness / SSR_MAX_ROUGHNESS);
        specularAmbient = lerp(specularAmbient, ssrColor * envBrdfApprox(f0, surface.Roughness, nDotV), ssrWeight);
    }

    // Direct sun with CSM PCSS
    float3 directSun = float3(0.0);
    if (hasDirectionalLight(sceneSettings.Environment))
    {
        float3 lightDir = getDirectionalLightDirection(sceneSettings.Environment);
        float nDotL = max(dot(surface.Normal, lightDir), 0.0);
        if (nDotL > 0.0)
        {
            PbrBxdf bxdf;
            BxdfEval bxdfEval = bxdf.evalOpaque(
                viewDir, surface.Normal, surface.Normal, lightDir,
                surface.Albedo, surface.Specular, surface.Metallic, surface.Roughness,
                computeSpecularSamplingWeight(viewDir, surface.Normal, surface.Albedo, f0, surface.Metallic, surface.Roughness));

            float shadowVis = computeCsmPcssVisibility(surface.Position, nDotL, surface.Normal, cameraLinearDepth(surface.Position));
            directSun = bxdfEval.f * float3(sceneSettings.Environment.DirectionalIntensity) * nDotL * shadowVis;
        }
    }

    float3 finalColor = max(surface.Emission + diffuseAmbient + probeGIComponent + specularAmbient + directSun, float3(0.0));
    outputColor[pixel] = float4(finalColor, surface.Opacity);
}
