#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Bxdfs/Brdf.slang"
#include "../Geometry/Intersection.slang"
#include "../Lights/EnvironmentLight.slang"

static const int BAKE_SAMPLES = 64;
static const int PROBE_STRIDE = 8; // float4 slots per probe

// Each probe: 8 float4s
// [0] = float4(position, pad)
// [1] = SH_R coeffs 0-3
// [2] = SH_R coeffs 4-7
// [3] = SH_G coeffs 0-3, .w = SH_R coeff 8
// [4] = SH_G coeffs 4-7
// [5] = SH_B coeffs 0-2, .w = SH_G coeff 7
// [6] = SH_B coeffs 4-7
// [7] = float4(SH_B coeff 7, SH_B coeff 8, SH_G coeff 8, pad)

[[vk::binding(16, 0)]] RWStructuredBuffer<float4> probeData;

struct BakePushConstants
{
    uint probeBaseIndex;
    uint totalProbes;
    uint gridDimX;
    uint gridDimY;
};

[[vk::push_constant]] ConstantBuffer<BakePushConstants> pc;

groupshared float3 shAccum[BAKE_SAMPLES][9];

void evalSH(float3 dir, out float sh[9])
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

[shader("compute")]
[numthreads(BAKE_SAMPLES, 1, 1)]
void main(uint3 id : SV_DispatchThreadID, uint3 gtid : SV_GroupThreadID, uint3 gid : SV_GroupID)
{
    uint probeIdx = pc.probeBaseIndex + gid.x;
    uint sampleIdx = gtid.x;

    uint2 seed = pcg2d(uint2(probeIdx * BAKE_SAMPLES + sampleIdx, 0xA5E1B3C9u));
    uint rngState = seed.x ^ seed.y;
    if (rngState == 0) rngState = 1;

    float3 pos = probeData[probeIdx * PROBE_STRIDE].xyz;

    float sh[9];
    float weight;
    float3 dir = sampleUniformHemisphere(float3(0.0, 1.0, 0.0), rngState, weight);
    dir = normalize(dir);
    evalSH(dir, sh);

    float tMin = computeRayOriginBias(pos);
    float tMax = 1000.0;

    Intersection hit = traceRay(pos, dir, tMin, tMax);
    float3 radiance;
    if (hit.miss)
    {
        radiance = evaluateEnvironmentRadiance(dir, sceneSettings.Environment);
    }
    else
    {
        SurfaceData surf = loadSurface(hit);
        float3 envDir = surf.normal;
        float3 env = evaluateEnvironmentRadiance(envDir, sceneSettings.Environment);
        radiance = surf.emission + env * surf.albedo * (1.0 / PI);
    }

    [unroll] for (int c = 0; c < 9; c++) shAccum[sampleIdx][c] = radiance * sh[c];

    GroupMemoryBarrierWithGroupSync();

    if (sampleIdx == 0)
    {
        float3 sumSH[9] = { float3(0.0), float3(0.0), float3(0.0), float3(0.0), float3(0.0),
                            float3(0.0), float3(0.0), float3(0.0), float3(0.0) };
        [unroll] for (int s = 0; s < BAKE_SAMPLES; s++)
            [unroll] for (int c = 0; c < 9; c++) sumSH[c] += shAccum[s][c];

        float inv = 2.0 * PI / float(BAKE_SAMPLES);
        [unroll] for (int c = 0; c < 9; c++) sumSH[c] *= inv;

        uint b = probeIdx * PROBE_STRIDE;
        probeData[b] = float4(pos, 0.0);
        probeData[b + 1] = float4(sumSH[0].x, sumSH[1].x, sumSH[2].x, sumSH[3].x);
        probeData[b + 2] = float4(sumSH[4].x, sumSH[5].x, sumSH[6].x, sumSH[7].x);
        probeData[b + 3] = float4(sumSH[0].y, sumSH[1].y, sumSH[2].y, sumSH[8].x);
        probeData[b + 4] = float4(sumSH[3].y, sumSH[4].y, sumSH[5].y, sumSH[6].y);
        probeData[b + 5] = float4(sumSH[0].z, sumSH[1].z, sumSH[2].z, sumSH[7].y);
        probeData[b + 6] = float4(sumSH[3].z, sumSH[4].z, sumSH[5].z, sumSH[6].z);
        probeData[b + 7] = float4(sumSH[7].z, sumSH[8].z, sumSH[8].y, 0.0);
    }
}
