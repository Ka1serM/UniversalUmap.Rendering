#ifndef _AGX_GLSL_
#define _AGX_GLSL_

const mat3 LINEAR_REC2020_TO_LINEAR_SRGB = mat3(
    1.6605, -0.1246, -0.0182,
    -0.5876, 1.1329, -0.1006,
    -0.0728, -0.0083, 1.1187
);

const mat3 LINEAR_SRGB_TO_LINEAR_REC2020 = mat3(
    0.6274, 0.0691, 0.0164,
    0.3293, 0.9195, 0.0880,
    0.0433, 0.0113, 0.8956
);

const mat3 AgXInsetMatrix = mat3(
    0.856627153315983, 0.137318972929847, 0.11189821299995,
    0.0951212405381588, 0.761241990602591, 0.0767994186031903,
    0.0482516061458583, 0.101439036467562, 0.811302368396859
);

const mat3 AgXOutsetMatrix = mat3(
    1.1271005818144368, -0.1413297634984383, -0.14132976349843826,
    -0.11060664309660323, 1.157823702216272, -0.11060664309660294,
    -0.016493938717834573, -0.016493938717834257, 1.2519364065950405
);

const float AgxMinEv = -12.47393;
const float AgxMaxEv = 4.026069;

vec3 agxCdl(vec3 color, vec3 slope, vec3 offset, vec3 power, float saturation)
{
    color = LINEAR_SRGB_TO_LINEAR_REC2020 * color;
    color = AgXInsetMatrix * color;
    color = max(color, vec3(1e-10));

    color = clamp(log2(color), vec3(AgxMinEv), vec3(AgxMaxEv));
    color = (color - AgxMinEv) / (AgxMaxEv - AgxMinEv);
    color = clamp(color, 0.0, 1.0);

    vec3 x2 = color * color;
    vec3 x4 = x2 * x2;
    color = +15.5 * x4 * x2
            -40.14 * x4 * color
            +31.96 * x4
            -6.868 * x2 * color
            +0.4298 * x2
            +0.1191 * color
            -0.00232;

    color = pow(color * slope + offset, power);
    const vec3 lw = vec3(0.2126, 0.7152, 0.0722);
    float luma = dot(color, lw);
    color = luma + saturation * (color - luma);

    color = AgXOutsetMatrix * color;
    color = pow(max(vec3(0.0), color), vec3(2.2));
    color = LINEAR_REC2020_TO_LINEAR_SRGB * color;
    return clamp(color, 0.0, 1.0);
}

vec3 agx(vec3 color)
{
    return agxCdl(color, vec3(1.0), vec3(0.0), vec3(1.0), 1.0);
}

vec3 agxGolden(vec3 color)
{
    return agxCdl(color, vec3(1.0, 0.9, 0.5), vec3(0.0), vec3(0.8), 1.3);
}

vec3 agxPunchy(vec3 color)
{
    return agxCdl(color, vec3(1.0), vec3(0.0), vec3(1.35), 1.4);
}

#endif
