// BRDF and lighting functions
#ifndef BRDF_GLSL
#define BRDF_GLSL

#include "../SharedStructs.h"
#include "../Bindings.glsl"
#include "../Common.glsl"

// Forward declaration
float fresnelDielectric(float cosThetaI, float etaI, float etaT);

// Fast normalization with epsilon guard - must be defined early as it's used by other functions
vec3 fastNormalize(vec3 v) {
    return v * inversesqrt(max(dot(v, v), EPSILON));
}

// Coordinate system helpers
void buildCoordinateSystem(vec3 N, out vec3 T, out vec3 B) {
    vec3 n = fastNormalize(N);
    if (abs(n.z) < 0.999)
        T = fastNormalize(cross(n, vec3(0.0, 0.0, 1.0)));
    else
        T = fastNormalize(cross(n, vec3(0.0, 1.0, 0.0)));
    B = fastNormalize(cross(T, n));
}

vec3 worldToLocal(vec3 v, vec3 T, vec3 B, vec3 N) {
    return vec3(dot(v, T), dot(v, B), dot(v, N));
}

vec3 localToWorld(vec3 v, vec3 T, vec3 B, vec3 N) {
    return T * v.x + B * v.y + N * v.z;
}

// Normal mapping helpers
const bool NORMAL_MAP_IS_DIRECTX = true;
const bool NORMAL_MAP_GENERAL_FLIP = true;

vec3 decodeTangentNormal(vec3 packedNormal) {
    vec2 xy = packedNormal.xy * 2.0 - 1.0;
    if (NORMAL_MAP_IS_DIRECTX)
        xy.y = -xy.y;
    if (NORMAL_MAP_GENERAL_FLIP)
        xy = -xy;

    float z2 = max(1.0 - dot(xy, xy), 0.0);
    return vec3(xy, sqrt(z2));
}

vec3 applyNormalMap(vec3 baseNormal, vec3 interpolatedTangent, float tangentSign, vec3 packedNormal) {
    vec3 N = fastNormalize(baseNormal);
    vec3 T = interpolatedTangent - N * dot(interpolatedTangent, N);
    float tLenSq = dot(T, T);
    float signValue = (abs(tangentSign) <= EPSILON) ? 1.0 : sign(tangentSign);
    vec3 B;
    if (tLenSq <= EPSILON) {
        buildCoordinateSystem(N, T, B);
        B *= signValue;
    } else {
        T = T * inversesqrt(tLenSq);
        B = fastNormalize(cross(N, T)) * signValue;
    }

    vec3 tangentNormal = decodeTangentNormal(packedNormal);
    vec3 mappedNormal = fastNormalize(mat3(T, B, N) * tangentNormal);

    if (dot(mappedNormal, N) < 0.0)
        mappedNormal = -mappedNormal;

    return mappedNormal;
}

vec3 stabilizeShadingNormal(vec3 shadingNormal, vec3 geometricNormal) {
    vec3 Ns = fastNormalize(shadingNormal);
    vec3 Ng = fastNormalize(geometricNormal);

    if (dot(Ns, Ng) < 0.0)
        Ns = -Ns;

    float alignment = clamp(dot(Ns, Ng), 0.0, 1.0);
    float keepShading = smoothstep(0.2, 0.8, alignment);
    return fastNormalize(mix(Ng, Ns, keepShading));
}

vec3 adaptShadingNormalLocal(vec3 viewDir, vec3 shadingNormal, vec3 geometricNormal) {
    vec3 Ns = fastNormalize(shadingNormal);
    vec3 Ng = fastNormalize(geometricNormal);
    if (dot(Ns, Ng) < 0.0)
        Ns = -Ns;

    vec3 wi = -viewDir;
    vec3 r = reflect(wi, Ns);
    if (dot(r, Ng) < 0.0) {
        vec3 t = r - Ng * dot(Ng, r);
        float tLenSq = dot(t, t);
        if (tLenSq > EPSILON)
            Ns = fastNormalize(-wi + fastNormalize(t));
        else
            Ns = Ng;
    }

    if (dot(Ns, Ng) < 0.0)
        Ns = -Ns;
    return Ns;
}

// Diffuse sampling
vec3 sampleDiffuse(vec3 N, inout uint rngState) {
    float u1 = max(rand(rngState), EPSILON);
    float u2 = rand(rngState);
    float r = sqrt(u1);
    float theta = 2.0 * PI * u2;
    vec3 local = vec3(r * cos(theta), r * sin(theta), sqrt(max(EPSILON, 1.0 - u1)));
    vec3 T, B;
    buildCoordinateSystem(N, T, B);
    return fastNormalize(T * local.x + B * local.y + N * local.z);
}

vec3 sampleUniformHemisphere(vec3 N, inout uint rngState, out float pdf) {
    float u1 = rand(rngState);
    float u2 = rand(rngState);
    float z = u1;
    float r = sqrt(max(0.0, 1.0 - z * z));
    float phi = 2.0 * PI * u2;
    vec3 local = vec3(r * cos(phi), r * sin(phi), z);
    vec3 T, B;
    buildCoordinateSystem(N, T, B);
    pdf = 0.5 / PI;
    return fastNormalize(T * local.x + B * local.y + N * local.z);
}

float regularizeSecondaryRoughness(float roughness, uint depth) {
    if (depth == 0u)
        return roughness;
    float minimumRoughness = 0.08 + 0.02 * min(float(depth - 1u), 3.0);
    return max(roughness, minimumRoughness);
}

vec3 fresnelSchlick(vec3 F0, float cosTheta) {
    float m = clamp(1.0 - cosTheta, 0.0, 1.0);
    float m2 = m * m;
    float m5 = m2 * m2 * m;
    return F0 + (vec3(1.0) - F0) * m5;
}

vec3 envBrdfApprox(vec3 specularColor, float roughness, float nDotV) {
    vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
    vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
    vec4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * nDotV)) * r.x + r.y;
    vec2 ab = vec2(-1.04, 1.04) * a004 + r.zw;
    return specularColor * ab.x + ab.y;
}

float computeSpecularOcclusion(float nDotV, float ao, float roughness) {
    return clamp(pow(max(nDotV + ao, EPSILON), exp2(-16.0 * max(roughness, EPSILON) - 1.0)) - 1.0 + ao, 0.0, 1.0);
}

float pdfDiffuse(vec3 N, vec3 L) {
    return max(dot(N, L), 0.0) / max(PI, EPSILON);
}

vec3 sampleVisibleHalfVectorGGX(vec3 V, vec3 N, float roughness, inout uint rngState) {
    float alpha = max(roughness * roughness, 0.001);
    vec3 T, B;
    buildCoordinateSystem(N, T, B);

    vec3 Vlocal = fastNormalize(worldToLocal(V, T, B, N));
    vec3 Vh = fastNormalize(vec3(alpha * Vlocal.x, alpha * Vlocal.y, Vlocal.z));

    float lensq = Vh.x * Vh.x + Vh.y * Vh.y;
    vec3 T1 = lensq > EPSILON ? vec3(-Vh.y, Vh.x, 0.0) * inversesqrt(lensq) : vec3(1.0, 0.0, 0.0);
    vec3 T2 = cross(Vh, T1);

    float u1 = rand(rngState);
    float u2 = rand(rngState);
    float r = sqrt(u1);
    float phi = 2.0 * PI * u2;
    float t1 = r * cos(phi);
    float t2 = r * sin(phi);
    float s = 0.5 * (1.0 + Vh.z);
    t2 = mix(sqrt(max(0.0, 1.0 - t1 * t1)), t2, s);

    vec3 Nh = t1 * T1 + t2 * T2 + sqrt(max(0.0, 1.0 - t1 * t1 - t2 * t2)) * Vh;
    vec3 Hlocal = fastNormalize(vec3(alpha * Nh.x, alpha * Nh.y, max(Nh.z, 0.0)));
    return fastNormalize(localToWorld(Hlocal, T, B, N));
}

float distributionGGX(vec3 N, vec3 H, float roughness)
{
    float a      = roughness*roughness;
    float a2     = a*a;
    float NdotH  = max(dot(N, H), 0.0);
    float NdotH2 = NdotH*NdotH;

    float num   = a2;
    float denom = (NdotH2 * (a2 - 1.0) + 1.0);
    denom = PI * denom * denom;

    return num / denom;
}

float smithLambdaGGX(vec3 N, vec3 w, float roughness) {
    float NdotW = max(dot(N, w), EPSILON);
    float alpha = roughness * roughness;
    float alpha2 = alpha * alpha;
    float tan2Theta = max(1.0 - NdotW * NdotW, 0.0) / max(NdotW * NdotW, EPSILON);
    return 0.5 * (-1.0 + sqrt(1.0 + alpha2 * tan2Theta));
}

float geometrySmithG1GGX(vec3 N, vec3 w, float roughness)
{
    return 1.0 / (1.0 + smithLambdaGGX(N, w, roughness));
}

float geometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
    return geometrySmithG1GGX(N, V, roughness) * geometrySmithG1GGX(N, L, roughness);
}

vec3 evaluateSpecularBRDF(vec3 normal, vec3 viewDir, vec3 sampledDir, vec3 F, float roughness, vec3 H) {
    float D = distributionGGX(normal, H, roughness);
    float G = geometrySmith(normal, viewDir, sampledDir, roughness);
    float NdotV = max(dot(normal, viewDir), 0.0);
    float NdotL = max(dot(normal, sampledDir), 0.0);
    return (D * G * F) / max(4.0 * NdotV * NdotL, EPSILON);
}

float pdfSpecular(vec3 V, vec3 N, vec3 H, float roughness) {
    float VdotH = max(dot(V, H), EPSILON);
    float NdotV = max(dot(N, V), EPSILON);
    float D = distributionGGX(N, H, roughness);
    float G1 = geometrySmithG1GGX(N, V, roughness);
    float pdfH = D * G1 * VdotH / NdotV;
    return pdfH / max(4.0 * VdotH, EPSILON);
}

float dielectricF0FromSpecular(float specular) {
    return clamp(0.04 * specular, 0.0, 0.16);
}

float computeSpecularSamplingWeight(vec3 viewDir, vec3 normal, vec3 albedo, vec3 F0, float metallic, float roughness) {
    float NdotV = max(dot(normal, viewDir), 0.0);
    vec3 F = fresnelSchlick(F0, NdotV);
    float weightDiffuse = luminance((vec3(1.0) - F) * albedo) * (1.0 - metallic);
    float weightSpecular = luminance(F) + (1.0 - roughness) * 0.1;
    return clamp(weightSpecular / max(weightDiffuse + weightSpecular, EPSILON), 0.0, 1.0);
}

vec3 evaluateDiffuseBRDF(vec3 albedo, float metallic) {
    return (1.0 - metallic) * (albedo / PI);
}

// Environment sampling with CDF
int binarySearchMarginalCdf(int cdfTextureIndex, int height, float xi) {
    int lo = 0;
    int hi = height - 1;
    for (int i = 0; i < 20 && lo < hi; i++) {
        int mid = (lo + hi) >> 1;
        float c = texelFetch(textureSamplers[cdfTextureIndex], ivec2(0, mid), 0).g;
        if (c < xi)
            lo = mid + 1;
        else
            hi = mid;
    }
    return clamp(lo, 0, height - 1);
}

int binarySearchConditionalCdf(int cdfTextureIndex, int width, int y, float xi) {
    int lo = 0;
    int hi = width - 1;
    for (int i = 0; i < 20 && lo < hi; i++) {
        int mid = (lo + hi) >> 1;
        float c = texelFetch(textureSamplers[cdfTextureIndex], ivec2(mid, y), 0).r;
        if (c < xi)
            lo = mid + 1;
        else
            hi = mid;
    }
    return clamp(lo, 0, width - 1);
}

vec3 sampleEnvironmentDirection(
    in EnvironmentDataGpu environmentData,
    inout uint rngState,
    out float lightPdf)
{
    lightPdf = 0.0;
    if (environmentData.textureIndex < 0 || environmentData.cdfTextureIndex < 0)
        return vec3(0.0);

    ivec2 envSize = textureSize(textureSamplers[environmentData.textureIndex], 0);
    if (envSize.x <= 0 || envSize.y <= 0)
        return vec3(0.0);

    float xi1 = rand(rngState);
    float xi2 = rand(rngState);

    int y = binarySearchMarginalCdf(environmentData.cdfTextureIndex, envSize.y, xi1);
    int x = binarySearchConditionalCdf(environmentData.cdfTextureIndex, envSize.x, y, xi2);
    vec4 cdfTexel = texelFetch(textureSamplers[environmentData.cdfTextureIndex], ivec2(x, y), 0);

    float v = (float(y) + 0.5) / float(envSize.y);
    float u = (float(x) + 0.5) / float(envSize.x);
    float theta = v * PI;
    float phi = (u - 0.5) * (2.0 * PI);
    float sinTheta = max(sin(theta), EPSILON);

    float uvPdf = max(cdfTexel.a, 0.0);
    lightPdf = uvPdf / max(2.0 * PI * PI * sinTheta, EPSILON);

    vec3 rotatedDir = vec3(
        cos(phi) * sinTheta,
        cos(theta),
        sin(phi) * sinTheta);

    // Convert sampled env-space direction back into world-space by inverse rotation
    vec3 worldDir = rotatedDir;
    worldDir.x = rotatedDir.x * environmentData.rotationCos + rotatedDir.z * environmentData.rotationSin;
    worldDir.z = -rotatedDir.x * environmentData.rotationSin + rotatedDir.z * environmentData.rotationCos;
    return fastNormalize(worldDir);
}

#endif // BRDF_GLSL
