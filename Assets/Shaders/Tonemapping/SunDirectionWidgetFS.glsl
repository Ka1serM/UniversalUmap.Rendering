#version 460

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform PushConstants
{
    vec4 lightDir; // xyz = direction
} pc;

const vec3 BASE_COLOR = vec3(0.20, 0.22, 0.26);

void main()
{
    vec2 uv = vUv * 2.0 - 1.0;
    uv.y = -uv.y;

    float rr = dot(uv, uv);
    float r = sqrt(max(rr, 0.0));
    float edge = fwidth(r) * 1.5;
    float alpha = 1.0 - smoothstep(1.0 - edge, 1.0 + edge, r);
    if (alpha <= 0.0)
        discard;

    float z = sqrt(max(0.0, 1.0 - rr));
    vec3 n = normalize(vec3(uv, z));
    vec3 l = normalize(pc.lightDir.xyz);
    float ndotl = max(dot(n, l), 0.0);
    vec3 v = vec3(0.0, 0.0, 1.0);

    float key = pow(ndotl, 1.75);
    float rim = 0.42 * pow(1.0 - max(dot(n, v), 0.0), 1.45);

    vec3 color = BASE_COLOR + vec3(0.96 * key) + rim * vec3(0.68, 0.76, 0.90);
    color = clamp(color, 0.0, 1.0);

    outColor = vec4(color, alpha);
}
