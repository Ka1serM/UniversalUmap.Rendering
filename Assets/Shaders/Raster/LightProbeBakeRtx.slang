#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Core/Core.slang"
#include "../Bxdfs/Brdf.slang"
#include "../Lights/EnvironmentLight.slang"

static const int BAKE_SAMPLES = 64;
static const int PROBE_STRIDE = 8;

[[vk::binding(16, 0)]] RWStructuredBuffer<float4> probeData;

struct BakePushConstants
{
    uint probeBaseIndex;
    uint totalProbes;
    uint gridDimX;
    uint gridDimY;
};

[[vk::push_constant]] ConstantBuffer<BakePushConstants> pc;

// Ray tracing acceleration structure binding
[[vk::binding(17, 0)]] RaytracingAccelerationStructure tlas;

struct HitPayload
{
    float3 radiance;
    bool miss;
};

[shader("raygeneration")]
void rayGen()
{
}

[shader("miss")]
void missShader(inout HitPayload payload : SV_RayPayload)
{
    payload.radiance = float3(0.0);
    payload.miss = true;
}

[shader("closesthit")]
void closestHitShader(inout HitPayload payload : SV_RayPayload, BuiltInTriangleIntersectionAttributes attr)
{
    float3 bary = float3(1.0 - attr.barycentrics.x - attr.barycentrics.y, attr.barycentrics.x, attr.barycentrics.y);

    uint instanceId = InstanceIndex();
    uint triangleIndex = PrimitiveIndex();
    InstanceGpu inst = instances[instanceId];
    MeshAddressesGpu mesh = meshes[inst.MeshId];
    Face face = reinterpret<FaceBuffer*>(mesh.FaceAddress)->data[triangleIndex];
    MaterialData mat = reinterpret<MaterialBuffer*>(mesh.MaterialAddress)->data[face.MaterialIndex];

    uint i0 = reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[triangleIndex * 3 + 0];
    uint i1 = reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[triangleIndex * 3 + 1];
    uint i2 = reinterpret<IndexBuffer*>(mesh.IndexAddress)->data[triangleIndex * 3 + 2];

    Vertex v0 = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[i0];
    Vertex v1 = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[i1];
    Vertex v2 = reinterpret<VertexBuffer*>(mesh.VertexAddress)->data[i2];

    float3 pos = v0.Position * bary.x + v1.Position * bary.y + v2.Position * bary.z;
    float3 worldPos = mul(float4(pos, 1.0), inst.Transform).xyz;
    float3 normal = normalize(v0.Normal * bary.x + v1.Normal * bary.y + v2.Normal * bary.z);
    float3x3 normalMat = (float3x3)inst.NormalTransform;
    float3 worldNormal = normalize(mul(normal, normalMat));

    float3 albedo = mat.Albedo;
    float3 emission = mat.Emission * mat.EmissionStrength;
    if (mat.AlbedoIndex != -1)
        albedo *= textureSamplers[mat.AlbedoIndex].SampleLevel(bary.xy * 0.5 + 0.25, 0.0).rgb;

    float3 envDir = worldNormal;
    float3 env = evaluateEnvironmentRadiance(envDir, sceneSettings.Environment);
    payload.radiance = emission + env * albedo * (1.0 / PI);
    payload.miss = false;
}

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

groupshared float3 shAccum[BAKE_SAMPLES][9];

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

    float weight;
    float3 dir = sampleUniformHemisphere(float3(0.0, 1.0, 0.0), rngState, weight);
    dir = normalize(dir);

    float sh[9];
    evalSH(dir, sh);

    float tMin = computeRayOriginBias(pos);
    float tMax = 1000.0;

    RayDesc ray;
    ray.Origin = pos;
    ray.Direction = dir;
    ray.TMin = tMin;
    ray.TMax = tMax;

    HitPayload payload;
    TraceRay(tlas, RAY_FLAG_NONE, 0xFF, 0, 0, 0, ray, payload);

    float3 radiance;
    if (payload.miss)
    {
        radiance = evaluateEnvironmentRadiance(dir, sceneSettings.Environment);
    }
    else
    {
        radiance = payload.radiance;
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
