#version 460
#extension GL_EXT_nonuniform_qualifier : require

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform PushConstants
{
    vec4 params0; // x = rotationDeg
} pc;

layout(set = 0, binding = 0) uniform sampler2D environmentTexture;

void main()
{
    vec2 uv = vUv * 2.0 - 1.0;

    float rr = dot(uv, uv);
    float r = sqrt(max(rr, 0.0));
    float edge = fwidth(r) * 1.5;
    float alpha = 1.0 - smoothstep(1.0 - edge, 1.0 + edge, r);
    if (alpha <= 0.0)
        discard;

    float z = sqrt(max(0.0, 1.0 - rr));
    vec3 n = normalize(vec3(uv, z));
    vec3 v = vec3(0.0, 0.0, 1.0);
    vec3 rdir = reflect(-v, n);
    vec3 invRdir = -rdir;

    float rot = radians(pc.params0.x);
    float s = sin(rot);
    float c = cos(rot);
    vec3 d = invRdir;
    d.x = invRdir.x * c - invRdir.z * s;
    d.z = invRdir.x * s + invRdir.z * c;
    d.y = -d.y;

    float u = atan(d.z, d.x) / (2.0 * 3.14159265359) + 0.5;
    float envV = acos(clamp(d.y, -1.0, 1.0)) / 3.14159265359;

    vec3 env = vec3(0.34, 0.47, 0.72);
    vec3 hdr = texture(environmentTexture, vec2(u, envV)).rgb;
    env = hdr / (hdr + vec3(1.0));
    env = pow(env, vec3(1.0 / 2.2));

    float ndotv = max(dot(n, v), 0.0);
    float fresnel = pow(1.0 - ndotv, 3.0);
    float inverseFresnel = 1.0 - fresnel;

    // Keep env-map detail, but use inverse Fresnel for center-bright / edge-dark shaping.
    vec3 centerLift = vec3(1.5, 1.5, 1.5);
    vec3 edgeAtten = vec3(1.0, 1.0, 1.0);
    vec3 shade = mix(edgeAtten, centerLift, inverseFresnel);
    vec3 color = env * shade;
    color = clamp(color, 0.0, 1.0);
    outColor = vec4(color, alpha);
}
