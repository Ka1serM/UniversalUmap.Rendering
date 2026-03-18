struct PushConstants
{
    float4 lightDir; // xyz = direction
};

[[vk::push_constant]] ConstantBuffer<PushConstants> pc;

static const float3 BASE_COLOR = float3(0.20, 0.22, 0.26);

[shader("fragment")]
float4 main(float2 vUv : TEXCOORD0) : SV_Target
{
    float2 uv = vUv * 2.0 - 1.0;
    uv.y = -uv.y;

    float rr = dot(uv, uv);
    float r = sqrt(max(rr, 0.0));
    float edge = fwidth(r) * 1.5;
    float alpha = 1.0 - smoothstep(1.0 - edge, 1.0 + edge, r);
    if (alpha <= 0.0)
        discard;

    float z = sqrt(max(0.0, 1.0 - rr));
    float3 n = normalize(float3(uv, z));
    float3 l = normalize(pc.lightDir.xyz);
    float ndotl = max(dot(n, l), 0.0);
    float3 v = float3(0.0, 0.0, 1.0);

    float key = pow(ndotl, 1.75);
    float rim = 0.42 * pow(1.0 - max(dot(n, v), 0.0), 1.45);

    float3 color = BASE_COLOR + float3(0.96 * key) + rim * float3(0.68, 0.76, 0.90);
    color = clamp(color, 0.0, 1.0);

    return float4(color, alpha);
}
