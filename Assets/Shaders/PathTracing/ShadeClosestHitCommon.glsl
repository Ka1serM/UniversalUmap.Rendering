// Shared helpers extracted from ShadeClosestHit.glsl
#ifndef CLOSEST_HIT_COMMON
#define CLOSEST_HIT_COMMON

#include "../Common.glsl"
#include "ShadeMiss.glsl"
#include "RenderPolicyCommon.glsl"

// Forward declarations used by AO helper includes.
vec3 sampleDiffuse(vec3 N, inout uint rngState);
float computeShadowBias(vec3 worldPosition, float nDotLGeom);
bool traceShadowRay(vec3 rayOrigin, vec3 rayDirection, float tMin, float tMax);
vec3 estimateDirectLighting(
    vec3 worldPosition,
    vec3 shadowPosition,
    vec3 shadingNormal,
    vec3 geometricNormal,
    vec3 viewDir,
    vec3 albedo,
    float specular,
    float metallic,
    float roughness,
    float swSpecular,
    in EnvironmentDataGpu environmentData,
    inout uint rngState);
vec3 fresnelSchlick(vec3 F0, float cosTheta);
float dielectricF0FromSpecular(float specular);
vec3 envBrdfApprox(vec3 specularColor, float roughness, float nDotV);
float computeSpecularOcclusion(float nDotV, float ao, float roughness);

#include "ShadeAmbientOcclusion.glsl"
#include "ShadeAmbientOcclusionAlbedo.glsl"

float fresnelDielectric(float cosThetaI, float etaI, float etaT);

vec3 sampleMaterialAlbedo(in MaterialData material, in vec2 interpolatedUV) {
    vec3 albedo = material.albedo;
    if (material.albedoIndex != -1)
        albedo *= texture(textureSamplers[material.albedoIndex], interpolatedUV).rgb;
    return albedo;
}

vec3 shadeAmbientOcclusionSurface(
    in vec3 worldPosition,
    in vec3 geometricFacingNormal,
    inout uint rngState)
{
    float ao = estimateAmbientOcclusion(worldPosition, geometricFacingNormal, rngState);
    return shadeAmbientOcclusionOnly(ao);
}

vec3 shadeAmbientOcclusionPbrSurface(
    in vec3 worldPosition,
    in vec3 shadowPosition,
    in vec3 shadingFacingNormal,
    in vec3 geometricFacingNormal,
    in vec3 viewDir,
    in vec3 albedo,
    in float specular,
    in float metallic,
    in float roughness,
    in float swSpecular,
    in vec3 baseEmission,
    in EnvironmentDataGpu environmentData,
    inout uint rngState)
{
    float ao = estimateAmbientOcclusion(worldPosition, geometricFacingNormal, rngState);
    vec3 directLighting = estimateDirectLighting(
        worldPosition,
        shadowPosition,
        shadingFacingNormal,
        geometricFacingNormal,
        viewDir,
        albedo,
        specular,
        metallic,
        roughness,
        swSpecular,
        environmentData,
        rngState);

    float maxEnvLod = max(environmentData.maxTextureLod, 0.0);
    float perceptualRoughness = clamp(roughness, 0.0, 1.0);
    float specularLod = perceptualRoughness * maxEnvLod;
    float diffuseLod = maxEnvLod;

    vec3 diffuseEnvironment = sampleEnvironmentMapColorLod(
        shadingFacingNormal,
        environmentData,
        environmentData.lightingExposureScale,
        diffuseLod);
    vec3 reflectedDir = reflect(-viewDir, shadingFacingNormal);
    vec3 specularEnvironment = sampleEnvironmentMapColorLod(
        reflectedDir,
        environmentData,
        environmentData.lightingExposureScale,
        specularLod);

    float dielectricF0 = dielectricF0FromSpecular(specular);
    vec3 F0 = mix(vec3(dielectricF0), albedo, metallic);
    float NdotV = max(dot(shadingFacingNormal, viewDir), 0.0);
    vec3 diffuseColor = albedo * (1.0 - metallic);
    vec3 diffuseAmbient = diffuseEnvironment * diffuseColor;
    vec3 specularAmbient = specularEnvironment * envBrdfApprox(F0, perceptualRoughness, NdotV);
    specularAmbient *= computeSpecularOcclusion(NdotV, ao, perceptualRoughness);

    return baseEmission + directLighting + ((diffuseAmbient + specularAmbient) * ao);
}

// Fast normalization with epsilon guard (no expensive NaN/Inf checks).
vec3 fastNormalize(vec3 v) {
    return v * inversesqrt(max(dot(v, v), EPSILON));
}

// Unreal/DX normal maps are +X right, +Y down (DirectX convention).
// For GLSL tangent-space lighting, flip Y to convert to +Y up.
const bool NORMAL_MAP_IS_DIRECTX = true;

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

float regularizeSecondaryRoughness(float roughness, uint depth) {
    if (depth == 0u)
        return roughness;

    float minimumRoughness = 0.08 + 0.02 * min(float(depth - 1u), 3.0);
    return max(roughness, minimumRoughness);
}

vec3 decodeTangentNormal(vec3 packedNormal) {
    vec2 xy = packedNormal.xy * 2.0 - 1.0;
    if (NORMAL_MAP_IS_DIRECTX)
        xy.y = -xy.y;

    // Reconstruct Z for stability (works robustly with BC5-style two-channel normals too).
    float z2 = max(1.0 - dot(xy, xy), 0.0);
    return vec3(xy, sqrt(z2));
}

vec3 applyNormalMap(vec3 baseNormal, vec3 interpolatedTangent, vec3 packedNormal) {
    vec3 N = fastNormalize(baseNormal);
    vec3 T = interpolatedTangent - N * dot(interpolatedTangent, N); // Gram-Schmidt
    float tLenSq = dot(T, T);
    vec3 B;
    if (tLenSq <= EPSILON) {
        buildCoordinateSystem(N, T, B);
    } else {
        T = T * inversesqrt(tLenSq);
        B = fastNormalize(cross(N, T));
    }

    vec3 tangentNormal = decodeTangentNormal(packedNormal);
    vec3 mappedNormal = fastNormalize(mat3(T, B, N) * tangentNormal);

    // Keep normal in the same hemisphere as the base geometric/smooth normal.
    if (dot(mappedNormal, N) < 0.0)
        mappedNormal = -mappedNormal;

    return mappedNormal;
}

vec3 stabilizeShadingNormal(vec3 shadingNormal, vec3 geometricNormal) {
    vec3 Ns = fastNormalize(shadingNormal);
    vec3 Ng = fastNormalize(geometricNormal);

    // Keep shading normal in the same hemisphere as geometric normal.
    if (dot(Ns, Ng) < 0.0)
        Ns = -Ns;

    // Reduce extreme normal deviation at grazing/corner cases (shadow terminator mitigation).
    float alignment = clamp(dot(Ns, Ng), 0.0, 1.0);
    float keepShading = smoothstep(0.2, 0.8, alignment);
    return fastNormalize(mix(Ng, Ns, keepShading));
}

vec3 adaptShadingNormalLocal(vec3 viewDir, vec3 shadingNormal, vec3 geometricNormal) {
    // Keller et al. local shading normal adaption to keep reflection in geometric hemisphere.
    vec3 Ns = fastNormalize(shadingNormal);
    vec3 Ng = fastNormalize(geometricNormal);
    if (dot(Ns, Ng) < 0.0)
        Ns = -Ns;

    vec3 wi = -viewDir; // incident direction toward the surface
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

float computeShadowBias(vec3 worldPosition, float nDotLGeom) {
    // Scale bias with scene extent and increase it at grazing angles.
    float sceneScale = max(max(abs(worldPosition.x), abs(worldPosition.y)), abs(worldPosition.z));
    float baseBias = max(1e-4, sceneScale * 1e-6);
    float grazingScale = 1.0 / max(nDotLGeom, 0.2);
    return baseBias * grazingScale;
}

float computeRayOriginBias(vec3 worldPosition) {
    float sceneScale = max(max(abs(worldPosition.x), abs(worldPosition.y)), abs(worldPosition.z));
    return max(1e-4, sceneScale * 1e-6);
}

vec3 offsetRayOrigin(vec3 worldPosition, vec3 geometricNormal, vec3 outDirection) {
    float bias = computeRayOriginBias(worldPosition);
    float side = (dot(outDirection, geometricNormal) >= 0.0) ? 1.0 : -1.0;
    return worldPosition + geometricNormal * (side * bias);
}

vec3 sampleDiffuse(vec3 N, inout uint rngState) {
    float u1 = rand(rngState);
    float u2 = rand(rngState);
    float r = sqrt(u1);
    float theta = 2.0 * PI * u2;
    vec3 local = vec3(r * cos(theta), r * sin(theta), sqrt(max(0.0, 1.0 - u1)));
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
    pdf = 0.5 / PI; // Uniform hemisphere
    return fastNormalize(T * local.x + B * local.y + N * local.z);
}

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

    // sampleEnvironmentMapColor applies +rotation before lookup.
    // Convert sampled env-space direction back into world-space by inverse rotation.
    vec3 worldDir = rotatedDir;
    worldDir.x = rotatedDir.x * environmentData.rotationCos + rotatedDir.z * environmentData.rotationSin;
    worldDir.z = -rotatedDir.x * environmentData.rotationSin + rotatedDir.z * environmentData.rotationCos;
    return fastNormalize(worldDir);
}

vec3 evaluateDiffuseBRDF(vec3 albedo, float metallic) {
    return (1 - metallic) * (albedo / PI);
}

vec3 fresnelSchlick(vec3 F0, float cosTheta) {
    float m = clamp(1.0 - cosTheta, 0.0, 1.0);
    float m2 = m * m;
    float m5 = m2 * m2 * m;
    return F0 + (vec3(1.0) - F0) * m5;
}

// Unreal-style split-sum approximation for specular IBL.
vec3 envBrdfApprox(vec3 specularColor, float roughness, float nDotV) {
    vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
    vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
    vec4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * nDotV)) * r.x + r.y;
    vec2 ab = vec2(-1.04, 1.04) * a004 + r.zw;
    return specularColor * ab.x + ab.y;
}

// Approximation used by real-time PBR pipelines to damp specular in occluded areas.
float computeSpecularOcclusion(float nDotV, float ao, float roughness) {
    return clamp(pow(nDotV + ao, exp2(-16.0 * roughness - 1.0)) - 1.0 + ao, 0.0, 1.0);
}

float pdfDiffuse(vec3 N, vec3 L) {
    return max(dot(N, L), 0.0) / PI;
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

vec3 sampleHalfVectorLegacy(vec3 V, vec3 N, float roughness, inout uint rngState) {
    float u1 = rand(rngState);
    float u2 = rand(rngState);
    float alpha = roughness * roughness;
    float alpha2 = alpha * alpha;
    float phi = 2.0 * PI * u1;
    float cosTheta = sqrt((1.0 - u2) / max(1.0 + (alpha2 - 1.0) * u2, EPSILON));
    float sinTheta = sqrt(max(1.0 - cosTheta * cosTheta, 0.0));

    vec3 Hlocal = vec3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta);
    vec3 T, B;
    buildCoordinateSystem(N, T, B);
    return fastNormalize(T * Hlocal.x + B * Hlocal.y + N * Hlocal.z);
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

bool buildMicrofacetNormalMap(
    vec3 geometricNormal,
    vec3 perturbedNormal,
    out vec3 wp,
    out vec3 wt,
    out float c,
    out float s)
{
    vec3 Ng = fastNormalize(geometricNormal);
    wp = fastNormalize(perturbedNormal);
    if (dot(wp, Ng) < 0.0)
        wp = -wp;

    c = clamp(dot(wp, Ng), 0.0, 1.0);
    s = sqrt(max(1.0 - c * c, 0.0));
    if (c <= 1e-4 || s <= 1e-4) {
        vec3 T, B;
        buildCoordinateSystem(Ng, T, B);
        wt = T;
        return false;
    }

    vec3 wpPerp = wp - Ng * c;
    wt = fastNormalize(-wpPerp);
    return true;
}

void microfacetProjectedAreas(
    vec3 w,
    vec3 wp,
    vec3 wt,
    float c,
    float s,
    out float ap,
    out float at,
    out float aSum)
{
    float invC = 1.0 / max(c, EPSILON);
    ap = max(dot(w, wp), 0.0) * invC;
    at = max(dot(w, wt), 0.0) * s * invC;
    aSum = ap + at;
}

float microfacetLambdaP(vec3 w, vec3 wp, vec3 wt, float c, float s) {
    float ap, at, aSum;
    microfacetProjectedAreas(w, wp, wt, c, s, ap, at, aSum);
    if (aSum <= EPSILON)
        return 0.0;
    return ap / aSum;
}

float microfacetG1(vec3 w, vec3 wm, vec3 geometricNormal, vec3 wp, vec3 wt, float c, float s) {
    float wdwm = dot(w, wm);
    if (wdwm <= 0.0)
        return 0.0;

    float ap, at, aSum;
    microfacetProjectedAreas(w, wp, wt, c, s, ap, at, aSum);
    if (aSum <= EPSILON)
        return 0.0;

    float wdng = max(dot(w, geometricNormal), 0.0);
    return min(1.0, wdng / aSum);
}

vec3 evaluateBaseOpaqueBSDFAndPdf(
    vec3 viewDir,
    vec3 normal,
    vec3 lightDir,
    vec3 albedo,
    float specular,
    float metallic,
    float roughness,
    float swSpecular,
    out float bsdfPdf
)
{
    float NdotV = max(dot(normal, viewDir), 0.0);
    float NdotL = max(dot(normal, lightDir), 0.0);
    if (NdotV <= 0.0 || NdotL <= 0.0) {
        bsdfPdf = 0.0;
        return vec3(0.0);
    }

    vec3 halfVector = fastNormalize(viewDir + lightDir);
    float dielectricF0 = dielectricF0FromSpecular(specular);
    vec3 F0_dielectric = vec3(dielectricF0);
    vec3 F0 = mix(F0_dielectric, albedo, metallic);
    vec3 F = fresnelSchlick(F0, max(dot(viewDir, halfVector), 0.0));

    vec3 diffuseBRDF = (vec3(1.0) - F) * evaluateDiffuseBRDF(albedo, metallic);
    vec3 specularBRDF = evaluateSpecularBRDF(normal, viewDir, lightDir, F, roughness, halfVector);

    float specPdf = pdfSpecular(viewDir, normal, halfVector, roughness);
    float diffPdf = pdfDiffuse(normal, lightDir);
    bsdfPdf = max(swSpecular * specPdf + (1.0 - swSpecular) * diffPdf, EPSILON);

    return diffuseBRDF + specularBRDF;
}

vec3 evaluateOpaqueBSDFAndPdf(
    vec3 viewDir,
    vec3 perturbedNormal,
    vec3 geometricNormal,
    vec3 lightDir,
    vec3 albedo,
    float specular,
    float metallic,
    float roughness,
    float swSpecular,
    out float bsdfPdf
) {
    float NdotLGeom = max(dot(geometricNormal, lightDir), 0.0);
    if (NdotLGeom <= 0.0) {
        bsdfPdf = 0.0;
        return vec3(0.0);
    }

    return evaluateBaseOpaqueBSDFAndPdf(
        viewDir,
        perturbedNormal,
        lightDir,
        albedo,
        specular,
        metallic,
        roughness,
        swSpecular,
        bsdfPdf);
}

vec3 estimateDirectLighting(
    vec3 worldPosition,
    vec3 shadowPosition,
    vec3 shadingNormal,
    vec3 geometricNormal,
    vec3 viewDir,
    vec3 albedo,
    float specular,
    float metallic,
    float roughness,
    float swSpecular,
    in EnvironmentDataGpu environmentData,
    inout uint rngState
) {
    vec3 directContribution = vec3(0.0);

    if (hasDirectionalLight(environmentData)) {
        vec3 sunDir = getDirectionalLightDirection(environmentData);
        float NdotLGeomSun = max(dot(geometricNormal, sunDir), 0.0);
        if (NdotLGeomSun > 0.0) {
            float shadowBiasSun = computeShadowBias(worldPosition, NdotLGeomSun);
            vec3 shadowOriginSun = shadowPosition + geometricNormal * shadowBiasSun;
            if (!traceShadowRay(shadowOriginSun, sunDir, shadowBiasSun, 1000.0)) {
                float bsdfPdfSun = 0.0;
                vec3 bsdfSun = evaluateOpaqueBSDFAndPdf(
                    viewDir,
                    shadingNormal,
                    geometricNormal,
                    sunDir,
                    albedo,
                    specular,
                    metallic,
                    roughness,
                    swSpecular,
                    bsdfPdfSun);
                directContribution += bsdfSun * vec3(environmentData.directionalIntensity) * NdotLGeomSun;
            }
        }
    }

    if (environmentData.textureIndex < 0 || environmentData.cdfTextureIndex < 0)
        return directContribution;

    float lightPdf = 0.0;
    vec3 lightDir = sampleEnvironmentDirection(environmentData, rngState, lightPdf);
    vec3 Li = sampleEnvironmentMapColor(lightDir, environmentData, environmentData.lightingExposureScale);
    float NdotLGeom = max(dot(geometricNormal, lightDir), 0.0);
    if (NdotLGeom <= 0.0 || lightPdf <= 0.0)
        return directContribution;

    float shadowBias = computeShadowBias(worldPosition, NdotLGeom);
    vec3 shadowOrigin = shadowPosition + geometricNormal * shadowBias;
    if (traceShadowRay(shadowOrigin, lightDir, shadowBias, 1000.0))
        return directContribution;

    float bsdfPdf = 0.0;
    vec3 bsdf = evaluateOpaqueBSDFAndPdf(viewDir, shadingNormal, geometricNormal, lightDir, albedo, specular, metallic, roughness, swSpecular, bsdfPdf);
    if (bsdfPdf <= 0.0)
        return directContribution;

    float wLight = powerHeuristic(lightPdf, bsdfPdf);
    directContribution += bsdf * Li * (NdotLGeom * wLight / max(lightPdf, EPSILON));
    return directContribution;
}

void sampleBaseOpaqueBSDF(
    vec3 viewDir,
    vec3 normal,
    vec3 geometricNormal,
    vec3 albedo,
    float metallic,
    float specular,
    float roughness,
    inout uint rngState,
    out vec3 sampledDir,
    out vec3 attenuation,
    out float combinedPdf,
    out bool sampledSpecular)
{
    float dielectricF0 = dielectricF0FromSpecular(specular);
    vec3 F0 = mix(vec3(dielectricF0), albedo, metallic);
    float swSpecular = computeSpecularSamplingWeight(viewDir, normal, albedo, F0, metallic, roughness);

    vec3 halfVector;
    sampledSpecular = rand(rngState) < swSpecular;
    if (sampledSpecular) {
        halfVector = sampleVisibleHalfVectorGGX(viewDir, normal, roughness, rngState);
        sampledDir = reflect(-viewDir, halfVector);
        for (int i = 0; i < 2 && dot(geometricNormal, sampledDir) <= 0.0; ++i) {
            halfVector = sampleVisibleHalfVectorGGX(viewDir, normal, roughness, rngState);
            sampledDir = reflect(-viewDir, halfVector);
        }
    } else {
        sampledDir = sampleDiffuse(normal, rngState);
        halfVector = fastNormalize(viewDir + sampledDir);
    }

    if (dot(geometricNormal, sampledDir) <= 0.0) {
        sampledDir = sampleDiffuse(geometricNormal, rngState);
        halfVector = fastNormalize(viewDir + sampledDir);
        sampledSpecular = false;
    }

    float VdotH = max(dot(viewDir, halfVector), 0.0);
    vec3 F = fresnelSchlick(F0, VdotH);
    vec3 diffuseBRDF  = (vec3(1.0) - F) * evaluateDiffuseBRDF(albedo, metallic);
    vec3 specularBRDF = evaluateSpecularBRDF(normal, viewDir, sampledDir, F, roughness, halfVector);
    vec3 combinedBRDF = diffuseBRDF + specularBRDF;

    float specPdf = sampledSpecular ? pdfSpecular(viewDir, normal, halfVector, roughness) : 0.0;
    float diffPdf = pdfDiffuse(normal, sampledDir);
    combinedPdf = max(swSpecular * specPdf + (1.0 - swSpecular) * diffPdf, EPSILON);
    attenuation = combinedBRDF * max(dot(normal, sampledDir), 0.0) / combinedPdf;
}

void sampleMicrofacetNormalMappedBSDF(
    vec3 viewDir,
    vec3 perturbedNormal,
    vec3 geometricNormal,
    vec3 albedo,
    float metallic,
    float specular,
    float roughness,
    inout uint rngState,
    out vec3 sampledDir,
    out vec3 attenuation,
    out float bsdfPdf,
    out bool sampledSpecular)
{
    sampleBaseOpaqueBSDF(
        viewDir,
        perturbedNormal,
        geometricNormal,
        albedo,
        metallic,
        specular,
        roughness,
        rngState,
        sampledDir,
        attenuation,
        bsdfPdf,
        sampledSpecular);
}

float fresnelDielectric(float cosThetaI, float etaI, float etaT) {
    float entering = step(0.0, cosThetaI);
    float newEtaI = mix(etaT, etaI, entering);
    float newEtaT = mix(etaI, etaT, entering);
    cosThetaI = mix(-cosThetaI, cosThetaI, entering);

    float eta = newEtaI / newEtaT;
    float cosThetaI2 = cosThetaI * cosThetaI;
    float sin2ThetaT = eta * eta * max(0.0, 1.0 - cosThetaI2);
    if (sin2ThetaT >= 1.0)
        return 1.0;

    float cosThetaT = sqrt(1.0 - sin2ThetaT);
    float A = newEtaT * cosThetaI;
    float B = newEtaI * cosThetaT;
    float Rs = (A - B) / (A + B);
    float Rp = (A * cosThetaT - B * cosThetaI) / (A * cosThetaT + B * cosThetaI);
    return 0.5 * (Rs * Rs + Rp * Rp);
}

void handleDielectricBSDF(vec3 viewDir, vec3 shadingNormal, float roughness, float ior, vec3 transmissionColor, inout Payload payload) {
    payload.flags |= BOUNCE_TRANSMIT;

    vec3 shadingFacingNormal = dot(shadingNormal, viewDir) < 0.0 ? -shadingNormal : shadingNormal;
    vec3 halfVector = sampleVisibleHalfVectorGGX(viewDir, shadingFacingNormal, roughness, payload.rngState);
    float VdotH = max(dot(viewDir, halfVector), 0.0);
    vec3 incident = fastNormalize(-viewDir);
    float etaI = 1.0;
    float etaT = ior;

    if (dot(incident, shadingNormal) > 0.0) {
        etaI = ior;
        etaT = 1.0;
    }

    float reflectProbability = max(fresnelDielectric(VdotH, etaI, etaT), EPSILON);
    vec3 refractedDir = refract(incident, halfVector, etaI / etaT);
    bool cannotRefract = dot(refractedDir, refractedDir) < EPSILON;

    if (cannotRefract || rand(payload.rngState) < reflectProbability) {
        vec3 reflectedDir = reflect(-viewDir, halfVector);
        vec3 brdf = evaluateSpecularBRDF(shadingFacingNormal, viewDir, reflectedDir, vec3(reflectProbability), roughness, halfVector);
        float pdf = max(pdfSpecular(viewDir, shadingFacingNormal, halfVector, roughness), EPSILON);

        payload.attenuation = (brdf * max(dot(shadingFacingNormal, reflectedDir), 0.0)) / max(pdf * reflectProbability, EPSILON);
        payload.nextDirection = reflectedDir;
        payload.lastBsdfPdf = pdf;
        payload.lastNeeLightPdf = max(dot(shadingFacingNormal, reflectedDir), 0.0) > 0.0 ? (0.5 / PI) : 0.0;
    } else {
        payload.attenuation = transmissionColor / max(1.0 - reflectProbability, EPSILON);
        payload.nextDirection = refractedDir;
        payload.lastBsdfPdf = 0.0;
        payload.lastNeeLightPdf = 0.0;
    }
}

void handleOpaqueBSDF(
    vec3 viewDir,
    vec3 shadingNormal,
    vec3 geometricNormal,
    vec3 albedo,
    float metallic,
    float specular,
    float roughness,
    inout Payload payload)
{
    vec3 sampledDir;
    vec3 attenuation;
    float bsdfPdf;
    bool sampledSpecular;
    sampleMicrofacetNormalMappedBSDF(
        viewDir,
        shadingNormal,
        geometricNormal,
        albedo,
        metallic,
        specular,
        roughness,
        payload.rngState,
        sampledDir,
        attenuation,
        bsdfPdf,
        sampledSpecular);

    payload.nextDirection = sampledDir;
    payload.attenuation = attenuation;
    payload.lastBsdfPdf = max(bsdfPdf, EPSILON);
    payload.lastNeeLightPdf = max(dot(geometricNormal, sampledDir), 0.0) > 0.0 ? (0.5 / PI) : 0.0;
    payload.flags |= sampledSpecular ? BOUNCE_SPECULAR : BOUNCE_DIFFUSE;
}

#endif
