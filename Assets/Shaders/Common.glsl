#ifndef _COMMON_GLSL_ //include guard to prevent multiple inclusions
#define _COMMON_GLSL_


const float PI = 3.14159265358979323846;
const float EPSILON = 0.0001;
const float AABB_EPSILON  = 1e-5;
const float INF = 1.0e30;
const float FIREFLY_ABSOLUTE_LUMINANCE = 32.0;
const float FIREFLY_RELATIVE_LUMINANCE = 12.0;
const float FIREFLY_MIN_REFERENCE = 0.25;

// Bounce Types
const uint RAY_TERMINATED    = 1u << 0; // bit 0
const uint RAY_TRANSPARENT   = 1u << 1; // bit 1 (alpha-tested geometry skip)
const uint BOUNCE_DIFFUSE    = 1u << 2; // bit 2
const uint BOUNCE_SPECULAR   = 1u << 3; // bit 3
const uint BOUNCE_TRANSMIT   = 1u << 4; // bit 4
const uint ENV_TRANSPARENT   = 1u << 5; // bit 5 (invisible environment background)

float luminance(vec3 c) { 
    return 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
}

vec3 compressLuminancePreserveHue(vec3 color, float maxLuminance) {
    float currentLuminance = luminance(color);
    if (currentLuminance <= maxLuminance)
        return color;

    float excess = currentLuminance - maxLuminance;
    float compressedLuminance = maxLuminance + excess / (1.0 + excess / max(maxLuminance, EPSILON));
    return color * (compressedLuminance / max(currentLuminance, EPSILON));
}

vec3 suppressFireflies(
    vec3 sampleRadiance,
    vec3 referenceRadiance,
    int bounceCount,
    int diffuseCount,
    int specularCount,
    int transmissionCount)
{
    float sampleLuminance = luminance(sampleRadiance);
    if (sampleLuminance <= 0.0)
        return max(sampleRadiance, vec3(0.0));

    float referenceLuminance = max(luminance(max(referenceRadiance, vec3(0.0))), FIREFLY_MIN_REFERENCE);
    float allowedLuminance = max(FIREFLY_ABSOLUTE_LUMINANCE, referenceLuminance * FIREFLY_RELATIVE_LUMINANCE);

    float glossyRisk = float(specularCount + transmissionCount);
    float deepRisk = max(float(bounceCount) - 1.0, 0.0);
    float diffuseRelief = 1.0 / (1.0 + 0.2 * float(diffuseCount));
    float riskScale = (1.0 + glossyRisk + 0.35 * deepRisk) * diffuseRelief;
    allowedLuminance /= max(riskScale, 1.0);

    return compressLuminancePreserveHue(max(sampleRadiance, vec3(0.0)), allowedLuminance);
}

float powerHeuristic(float pdfA, float pdfB) {
    float a2 = pdfA * pdfA;
    float b2 = pdfB * pdfB;
    return a2 / max(a2 + b2, EPSILON);
}

float computeRussianRouletteSurvivalProbability(vec3 throughput, int bounce, int startBounce)
{
    if (bounce < startBounce)
        return 1.0;

    float pathEnergy = max(throughput.r, max(throughput.g, throughput.b));
    float depthPenalty = 1.0 / (1.0 + 0.12 * float(max(bounce - startBounce, 0)));
    return clamp(pathEnergy * depthPenalty, 0.1, 0.95);
}

// Barycentric Helpers
vec3 calculateBarycentric(vec3 attribs) {
    return vec3(1.0 - attribs.x - attribs.y, attribs.x, attribs.y);
}

vec3 interpolateBarycentric(vec3 bary, vec3 p0, vec3 p1, vec3 p2) {
    return p0 * bary.x + p1 * bary.y + p2 * bary.z;
}

vec2 interpolateBarycentric(vec3 bary, vec2 p0, vec2 p1, vec2 p2) {
    return p0 * bary.x + p1 * bary.y + p2 * bary.z;
}

// PCG Random Number Generation
uint pcg(inout uint state)
{
    uint prev = state * 747796405u + 2891336453u;
    uint word = ((prev >> ((prev >> 28u) + 4u)) ^ prev) * 277803737u;
    state = prev;
    return (word >> 22u) ^ word;
}

uvec2 pcg2d(uvec2 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v = v ^ (v >> 16u);
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v = v ^ (v >> 16u);
    return v;
}

// RNG float in [0,1)
float rand(inout uint seed)
{
    uint val = pcg(seed);
    return float(val) * (1.0 / 4294967296.0);
}

SamplerState initSamplerState(ivec2 pixelCoord, int frame) {
    SamplerState s;
    // Use pcg2d to generate a high-quality, uncorrelated random offset for each pixel.
    uvec2 pcg_val = pcg2d(uvec2(pixelCoord));
    s.randomOffset = vec2(pcg_val) / float(0xFFFFFFFFu); // Convert to [0,1)
    s.frame = frame;
    s.dimension = 0;
    return s;
}

// R2 sequence constants derived from the plastic constant.
const float G = 1.32471795724;
const float G2 = G * G;
const float G3 = G * G2;
const float G4 = G * G3;

// Pre-calculated alpha vectors for different dimensions.
const vec2 R2_ALPHA_DIM_0_1 = vec2(1.0 / G, 1.0 / G2);  // For Anti-aliasing
const vec2 R2_ALPHA_DIM_2_3 = vec2(1.0 / G3, 1.0 / G4); // For Depth of Field

// Gets the next 2D sample from our scrambled R2 sequence.
vec2 getSample2D(inout SamplerState s) {
    vec2 alpha;
    // Select the correct alpha vector based on the dimension we need.
    // This ensures AA and DoF samples are decorrelated.
    if (s.dimension == 0)
        alpha = R2_ALPHA_DIM_0_1;
    else
        alpha = R2_ALPHA_DIM_2_3;
    s.dimension += 2; // Consume two dimensions

    // The final sample is the sequence point plus the per-pixel random offset.
    return fract((alpha * float(s.frame)) + s.randomOffset);
}

#endif
