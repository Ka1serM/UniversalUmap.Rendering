#ifndef CLOSEST_HIT
#define CLOSEST_HIT

#include "../Common.glsl"
#include "ShadeMiss.glsl"

bool traceShadowRay(vec3 rayOrigin, vec3 rayDirection, float tMax);
float fresnelDielectric(float cosThetaI, float etaI, float etaT);

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
    in EnvironmentData environmentData,
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
    float radRotation = radians(environmentData.rotation);
    float s = sin(radRotation);
    float c = cos(radRotation);
    vec3 worldDir = rotatedDir;
    worldDir.x = rotatedDir.x * c + rotatedDir.z * s;
    worldDir.z = -rotatedDir.x * s + rotatedDir.z * c;
    return fastNormalize(worldDir);
}

vec3 evaluateDiffuseBRDF(vec3 albedo, float metallic) {
    return (1 - metallic) * (albedo / PI);
}

float pdfDiffuse(vec3 N, vec3 L) {
    return max(dot(N, L), 0.0) / PI;
}

vec3 sampleGGXVNDF_local(vec3 Vlocal, float roughness, vec2 u) {
    float a = roughness * roughness;
    vec3 Vstretched = fastNormalize(vec3(a * Vlocal.x, a * Vlocal.y, Vlocal.z));
    float phi = 2.0 * PI * u.x;
    float z = (1.0 - u.y) * (1.0 + Vstretched.z) - Vstretched.z;
    float sinTheta = sqrt(clamp(1.0 - z * z, 0.0, 1.0));
    vec3 c = vec3(sinTheta * cos(phi), sinTheta * sin(phi), z);
    vec3 Hstretched = c + Vstretched;
    return fastNormalize(vec3(a * Hstretched.x, a * Hstretched.y, Hstretched.z));
}

vec3 sampleHalfVector(vec3 V, vec3 N, float roughness, inout uint rngState) {
    float u1 = rand(rngState);
    float u2 = rand(rngState);
    vec3 T, B;
    buildCoordinateSystem(N, T, B);
    mat3 TBN = mat3(T, B, N);
    vec3 Vlocal = transpose(TBN) * V;
    vec3 Hlocal = sampleGGXVNDF_local(Vlocal, roughness, vec2(u1, u2));
    return fastNormalize(TBN * Hlocal);
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

float geometrySchlickGGX(float NdotV, float roughness) {
    float k = (roughness * roughness) / 2.0;
    return NdotV / max(NdotV * (1.0 - k) + k, EPSILON);
}

float geometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    float ggx2  = geometrySchlickGGX(NdotV, roughness);
    float ggx1  = geometrySchlickGGX(NdotL, roughness);

    return ggx1 * ggx2;
}

vec3 evaluateSpecularBRDF(vec3 normal, vec3 viewDir, vec3 sampledDir, vec3 F, float roughness, vec3 H) {
    float D = distributionGGX(normal, H, roughness);
    float G = geometrySmith(normal, viewDir, sampledDir, roughness);
    float NdotV = max(dot(normal, viewDir), 0.0);
    float NdotL = max(dot(normal, sampledDir), 0.0);
    return (D * G * F) / max(4.0 * NdotV * NdotL, EPSILON);
}

float pdfSpecular(vec3 V, vec3 N, vec3 H, float roughness) {
    float NdotV = max(dot(N, V), EPSILON);
    float D = distributionGGX(N, H, roughness);
    float G1_V = geometrySchlickGGX(NdotV, roughness);
    return (G1_V * D) / max(4.0 * NdotV, EPSILON);
}

float dielectricF0FromSpecular(float specular) {
    return clamp(0.04 * specular, 0.0, 0.16);
}

float computeSpecularProbability(float ndotVAbs, float metallic, float specular) {
    float baseSpecular = fresnelDielectric(ndotVAbs, 1.0, 1.5);
    float p = mix(baseSpecular, 1.0, metallic) * clamp(specular, 0.0, 1.0);
    return clamp(p, 0.0, 1.0);
}

vec3 evaluateOpaqueBSDFAndPdf(
    vec3 viewDir,
    vec3 normal,
    vec3 lightDir,
    vec3 albedo,
    float specular,
    float metallic,
    float roughness,
    float probSpecular,
    out float bsdfPdf
) {
    float NdotV = max(dot(normal, viewDir), 0.0);
    float NdotL = max(dot(normal, lightDir), 0.0);
    if (NdotV <= 0.0 || NdotL <= 0.0) {
        bsdfPdf = 0.0;
        return vec3(0.0);
    }

    vec3 halfVector = fastNormalize(viewDir + lightDir);
    float dielectricF0 = dielectricF0FromSpecular(specular);
    vec3 F_dielectric = vec3(dielectricF0);
    vec3 F = mix(F_dielectric, albedo, metallic);

    vec3 diffuseBRDF = evaluateDiffuseBRDF(albedo, metallic);
    vec3 specularBRDF = evaluateSpecularBRDF(normal, viewDir, lightDir, F, roughness, halfVector);

    float specPdf = pdfSpecular(viewDir, normal, halfVector, roughness);
    float diffPdf = pdfDiffuse(normal, lightDir);
    bsdfPdf = max(probSpecular * specPdf + (1.0 - probSpecular) * diffPdf, EPSILON);

    return diffuseBRDF + specularBRDF;
}

vec3 estimateDirectLighting(
    vec3 worldPosition,
    vec3 shadingNormal,
    vec3 geometricNormal,
    vec3 viewDir,
    vec3 albedo,
    float specular,
    float metallic,
    float roughness,
    float probSpecular,
    in EnvironmentData environmentData,
    inout uint rngState
) {
    vec3 directContribution = vec3(0.0);

    // Delta directional light: evaluate explicitly (Dirac light), no light-PDF sampling needed.
    if (hasDirectionalLight(environmentData)) {
        vec3 sunDir = getDirectionalLightDirection(environmentData);
        float NdotLGeomSun = max(dot(geometricNormal, sunDir), 0.0);
        if (NdotLGeomSun > 0.0) {
            vec3 shadowOriginSun = worldPosition + geometricNormal * 0.001;
            if (!traceShadowRay(shadowOriginSun, sunDir, 1000.0)) {
                float bsdfPdfSun = 0.0;
                vec3 bsdfSun = evaluateOpaqueBSDFAndPdf(
                    viewDir,
                    shadingNormal,
                    sunDir,
                    albedo,
                    specular,
                    metallic,
                    roughness,
                    probSpecular,
                    bsdfPdfSun);
                directContribution += bsdfSun * vec3(environmentData.directionalIntensity) * NdotLGeomSun;
            }
        }
    }

    // HDRI direct-light sample (continuous), with MIS against BSDF sampling.
    if (environmentData.textureIndex < 0 || environmentData.cdfTextureIndex < 0)
        return directContribution;

    float lightPdf = 0.0;
    vec3 lightDir = sampleEnvironmentDirection(environmentData, rngState, lightPdf);
    vec3 Li = sampleEnvironmentMapColor(lightDir, environmentData, environmentData.lightingExposure);

    // Single-sided visibility must use geometric normal, not bumped shading normal.
    float NdotLGeom = max(dot(geometricNormal, lightDir), 0.0);
    if (NdotLGeom <= 0.0 || lightPdf <= 0.0)
        return directContribution;

    vec3 shadowOrigin = worldPosition + geometricNormal * 0.001;
    if (traceShadowRay(shadowOrigin, lightDir, 1000.0))
        return directContribution;

    float bsdfPdf = 0.0;
    vec3 bsdf = evaluateOpaqueBSDFAndPdf(viewDir, shadingNormal, lightDir, albedo, specular, metallic, roughness, probSpecular, bsdfPdf);
    if (bsdfPdf <= 0.0)
        return directContribution;

    float wLight = powerHeuristic(lightPdf, bsdfPdf);
    directContribution += bsdf * Li * (NdotLGeom * wLight / max(lightPdf, EPSILON));
    return directContribution;
}

float fresnelDielectric(float cosThetaI, float etaI, float etaT) {
    // Branchless swap if ray is inside the surface
    float entering = step(0.0, cosThetaI); // 1 if cosThetaI > 0, else 0
    float newEtaI = mix(etaT, etaI, entering);
    float newEtaT = mix(etaI, etaT, entering);
    cosThetaI = mix(-cosThetaI, cosThetaI, entering);

    // Ratio of indices
    float eta = newEtaI / newEtaT;

    // sin^2(theta_t) using Snell's law
    float cosThetaI2 = cosThetaI * cosThetaI;
    float sin2ThetaT = eta * eta * max(0.0, 1.0 - cosThetaI2);

    // Total internal reflection
    if (sin2ThetaT >= 1.0)
        return 1.0;

    float cosThetaT = sqrt(1.0 - sin2ThetaT);

    // Fresnel equations
    float A = newEtaT * cosThetaI;
    float B = newEtaI * cosThetaT;
    float Rs = (A - B) / (A + B);
    float Rp = (A * cosThetaT - B * cosThetaI) / (A * cosThetaT + B * cosThetaI);

    return 0.5 * (Rs * Rs + Rp * Rp);
}

void handleDielectricBSDF(vec3 viewDir, vec3 shadingNormal, float roughness, float ior, vec3 transmissionColor, inout Payload payload) {
    payload.flags |= BOUNCE_TRANSMIT;

    vec3 Ns_shading = shadingNormal;
    if (dot(Ns_shading, viewDir) < 0.0)
        Ns_shading = -Ns_shading;

    vec3 H = sampleHalfVector(viewDir, Ns_shading, roughness, payload.rngState);
    float VdotH = max(dot(viewDir, H), 0.0);
    vec3 I = normalize(-viewDir);
    float etaI = 1.0, etaT = ior;

    bool exiting = dot(I, shadingNormal) > 0.0;
    if (exiting) {
        etaI = ior; etaT = 1.0;
    }

    float reflectProb = max(fresnelDielectric(VdotH, 1.0, ior), EPSILON);
    vec3 refractedDir = refract(I, H, etaI / etaT);
    bool cannotRefract = length(refractedDir) < 1e-5;

    if (cannotRefract || rand(payload.rngState) < reflectProb) {
        vec3 reflectedDir = reflect(-viewDir, H);
        vec3 brdf = evaluateSpecularBRDF(Ns_shading, viewDir, reflectedDir, vec3(reflectProb), roughness, H);
        float pdf = max(pdfSpecular(viewDir, Ns_shading, H, roughness), EPSILON);

        payload.attenuation = (brdf * max(dot(Ns_shading, reflectedDir), 0.0)) / max(pdf * reflectProb, EPSILON);
        payload.nextDirection = reflectedDir;
        payload.lastBsdfPdf = pdf;
        payload.lastNeeLightPdf = (max(dot(Ns_shading, reflectedDir), 0.0) > 0.0) ? (0.5 / PI) : 0.0;

    } else {
        payload.attenuation = transmissionColor / max(1.0 - reflectProb, EPSILON);
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
    float NdotV = dot(shadingNormal, viewDir);

    vec3 normal = shadingNormal;
    if (NdotV < 0.0)
        normal = -normal;
    
    float dielectricF0 = dielectricF0FromSpecular(specular);
    float probSpecular = computeSpecularProbability(abs(NdotV), metallic, specular);

    vec3 sampledDir;
    vec3 halfVector;
    if (rand(payload.rngState) < probSpecular) {
        payload.flags |= BOUNCE_SPECULAR;
        halfVector = sampleHalfVector(viewDir, normal, roughness, payload.rngState);
        sampledDir = reflect(-viewDir, halfVector);
    } else {
        payload.flags |= BOUNCE_DIFFUSE;
        sampledDir = sampleDiffuse(normal, payload.rngState);
        halfVector = fastNormalize(viewDir + sampledDir); // Fake half-vector only for MIS/Fresnel weighting
    }
    payload.nextDirection = sampledDir;

    // Prevent back-side light leaks on single-sided geometry with strong normal maps.
    if (dot(geometricNormal, sampledDir) <= 0.0) {
        payload.attenuation = vec3(0.0);
        payload.lastBsdfPdf = 0.0;
        payload.lastNeeLightPdf = 0.0;
        payload.flags |= RAY_TERMINATED;
        return;
    }
        
    // Mix with metallic
    vec3 F_dielectric = vec3(dielectricF0);
    vec3 F = mix(F_dielectric, albedo, metallic);

    // BRDF evaluation
    vec3 diffuseBRDF  = evaluateDiffuseBRDF(albedo, metallic);
    vec3 specularBRDF = evaluateSpecularBRDF(normal, viewDir, sampledDir, F, roughness, halfVector);
    vec3 combinedBRDF = diffuseBRDF + specularBRDF;

    // PDFs
    float specPdf = pdfSpecular(viewDir, normal, halfVector, roughness);
    float diffPdf = pdfDiffuse(normal, sampledDir);
    
    float combinedPdf = max(probSpecular * specPdf + (1.0 - probSpecular) * diffPdf, EPSILON);
    
    payload.attenuation = combinedBRDF * max(dot(normal, sampledDir), 0.0) / max(combinedPdf, EPSILON);
    payload.lastBsdfPdf = combinedPdf;
    payload.lastNeeLightPdf = (max(dot(normal, sampledDir), 0.0) > 0.0) ? (0.5 / PI) : 0.0;
}

void shadeClosestHit(
    in vec3 worldPosition,
    in vec3 shadingNormal,
    in vec3 geometricNormal,
    in vec3 interpolatedTangent,
    in vec2 interpolatedUV,
    in vec3 worldRayDirection,
    in Material material,
    inout Payload payload)
{
    payload.position = worldPosition;
    payload.lastBsdfPdf = 0.0;
    payload.lastNeeLightPdf = 0.0;

    float opacity = material.opacity;
    if (material.opacityIndex != -1)
        opacity *= texture(textureSamplers[material.opacityIndex], interpolatedUV).a;

    if (rand(payload.rngState) > opacity) {
        payload.flags |= RAY_TRANSPARENT;
        payload.nextDirection = worldRayDirection;
        payload.attenuation = vec3(1.0);
        payload.emission = vec3(0.0);
        return;
    }

    vec3 albedo = material.albedo;
    if (material.albedoIndex != -1)
        albedo *= texture(textureSamplers[material.albedoIndex], interpolatedUV).rgb;

    vec3 shadingNormalTextured = shadingNormal;
    if (material.normalIndex != -1) {
        vec3 packedNormal = texture(textureSamplers[material.normalIndex], interpolatedUV).xyz;
        shadingNormalTextured = applyNormalMap(shadingNormal, interpolatedTangent, packedNormal);
    }

    vec3 emission = material.emission * material.emissionStrength;
    if (material.emissionIndex != -1)
        emission *= texture(textureSamplers[material.emissionIndex], interpolatedUV).rgb;

    float metallic = material.metallic;
    if (material.metallicIndex != -1)
        metallic *= texture(textureSamplers[material.metallicIndex], interpolatedUV).r;

    float specular = material.specular;
    if (material.specularIndex != -1)
        specular *= texture(textureSamplers[material.specularIndex], interpolatedUV).r;
    specular = clamp(specular * 2.0, 0.0, 1.0);

    float roughness = material.roughness;
    if (material.roughnessIndex != -1)
        roughness *= texture(textureSamplers[material.roughnessIndex], interpolatedUV).r;
    roughness = clamp(roughness, 0.02, 1.0);

    float transmission = material.transmission;
    if (material.transmissionIndex != -1)
        transmission *= texture(textureSamplers[material.transmissionIndex], interpolatedUV).r;


    payload.albedo = albedo;
    payload.normal = shadingNormalTextured * 0.5 + 0.5;
    payload.emission = emission;

    vec3 viewDir = fastNormalize(-worldRayDirection);
    vec3 geometricFacingNormal = (dot(geometricNormal, viewDir) < 0.0) ? -geometricNormal : geometricNormal;
    if (dot(shadingNormalTextured, geometricFacingNormal) < 0.0)
        shadingNormalTextured = -shadingNormalTextured;

    float NdotV = dot(shadingNormalTextured, viewDir);
    vec3 facingNormal = (NdotV < 0.0) ? -shadingNormalTextured : shadingNormalTextured;
    float probSpecular = computeSpecularProbability(abs(NdotV), metallic, specular);

    if (rand(payload.rngState) < transmission)
        handleDielectricBSDF(viewDir, shadingNormalTextured, roughness, material.ior, material.transmissionColor, payload);
    else {
        payload.emission += estimateDirectLighting(
            worldPosition,
            facingNormal,
            geometricFacingNormal,
            viewDir,
            albedo,
            specular,
            metallic,
            roughness,
            probSpecular,
            pushConstants.environment,
            payload.rngState
        );
        handleOpaqueBSDF(viewDir, shadingNormalTextured, geometricFacingNormal, albedo, metallic, specular, roughness, payload);
    }
}

#endif
