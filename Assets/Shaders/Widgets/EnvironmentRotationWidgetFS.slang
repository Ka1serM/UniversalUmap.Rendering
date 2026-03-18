struct PushConstants
{
    float4 params0; // x = rotationDeg
};

[[vk::push_constant]] ConstantBuffer<PushConstants> pc;
[[vk::binding(0, 0)]] Sampler2D environmentTexture;

[shader("fragment")]
float4 main(float2 vUv : TEXCOORD0) : SV_Target
{
    float2 uv = vUv * 2.0 - 1.0;

    float rr = dot(uv, uv);
    float r = sqrt(max(rr, 0.0));
    float edge = fwidth(r) * 1.5;
    float alpha = 1.0 - smoothstep(1.0 - edge, 1.0 + edge, r);
    if (alpha <= 0.0)
        discard;

    float z = sqrt(max(0.0, 1.0 - rr));
    float3 n = normalize(float3(uv, z));
    float3 v = float3(0.0, 0.0, 1.0);
    float3 rdir = reflect(-v, n);
    float3 invRdir = -rdir;

    float rot = radians(pc.params0.x);
    float s = sin(rot);
    float c = cos(rot);
    float3 d = invRdir;
    d.x = invRdir.x * c - invRdir.z * s;
    d.z = invRdir.x * s + invRdir.z * c;
    d.y = -d.y;

    float u = atan2(d.z, d.x) / (2.0 * 3.14159265359) + 0.5;
    float envV = acos(clamp(d.y, -1.0, 1.0)) / 3.14159265359;

    float3 env = float3(0.34, 0.47, 0.72);
    // The environment widget is a small preview. Sampling mip 0 avoids the
    // visible horizontal seam introduced by wrapped lat-long maps with a
    // non-seam-aware generated mip chain.
    float3 hdr = environmentTexture.SampleLevel(float2(u, envV), 0.0).rgb;
    env = hdr / (hdr + float3(1.0));
    env = pow(env, float3(1.0 / 2.2));

    float ndotv = max(dot(n, v), 0.0);
    float fresnel = pow(1.0 - ndotv, 3.0);
    float inverseFresnel = 1.0 - fresnel;

    // Keep env-map detail, but use inverse Fresnel for center-bright / edge-dark shaping.
    float3 centerLift = float3(1.5, 1.5, 1.5);
    float3 edgeAtten = float3(1.0, 1.0, 1.0);
    float3 shade = lerp(edgeAtten, centerLift, inverseFresnel);
    float3 color = env * shade;
    color = clamp(color, 0.0, 1.0);
    return float4(color, alpha);
}
