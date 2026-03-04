#version 460
#extension GL_EXT_nonuniform_qualifier : require

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform PushConstants
{
    vec4 params0; // x = rotationDeg, y = bindless texture index
} pc;

layout(set = 0, binding = 7) uniform sampler2D textureSamplers[];

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

    float rot = radians(pc.params0.x);
    float s = sin(rot);
    float c = cos(rot);
    vec3 d = n;
    d.x = n.x * c - n.z * s;
    d.z = n.x * s + n.z * c;

    float u = atan(d.z, d.x) / (2.0 * 3.14159265359) + 0.5;
    float v = acos(clamp(d.y, -1.0, 1.0)) / 3.14159265359;

    int textureIndex = int(pc.params0.y + 0.5);
    vec3 env;
    if (textureIndex >= 0)
    {
        vec3 hdr = texture(textureSamplers[nonuniformEXT(textureIndex)], vec2(u, v)).rgb;
        env = hdr / (hdr + vec3(1.0));
        env = pow(env, vec3(1.0 / 2.2));
    }
    else
    {
        vec3 sky = vec3(0.28, 0.42, 0.66);
        vec3 horizon = vec3(0.62, 0.63, 0.64);
        vec3 ground = vec3(0.11, 0.12, 0.13);
        env = mix(sky, horizon, smoothstep(0.20, 0.52, v));
        env = mix(env, ground, smoothstep(0.52, 0.92, v));
    }

    float rim = 0.32 * pow(1.0 - max(n.z, 0.0), 1.55);
    vec3 color = clamp(env + rim * vec3(0.7, 0.78, 0.92), 0.0, 1.0);
    outColor = vec4(color, alpha);
}
