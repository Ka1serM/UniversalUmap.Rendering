#include "../SharedStructs.h"
#include "../Bindings.glsl"

layout(location = 1) rayPayloadEXT uint shadowPayload;
hitAttributeEXT vec3 attribs;

layout (push_constant, scalar) uniform PushConstants {
    PushDataGpu pushConstants;
};

bool traceShadowRay(vec3 rayOrigin, vec3 rayDirection, float tMin, float tMax) {
   shadowPayload = 1u;
   const uint shadowFlags = gl_RayFlagsTerminateOnFirstHitEXT
       | gl_RayFlagsOpaqueEXT
       | gl_RayFlagsSkipClosestHitShaderEXT;
   traceRayEXT(topLevelAS, shadowFlags, 0xFF, 0, 0, 1, rayOrigin, tMin, rayDirection, tMax, 1);
   return shadowPayload != 0u;
}

vec3 computeShadowTerminatorPointLocal(
    vec3 localPos,
    vec3 bary,
    vec3 v0Pos, vec3 v1Pos, vec3 v2Pos,
    vec3 v0Normal, vec3 v1Normal, vec3 v2Normal)
{
   vec3 n0 = normalize(v0Normal);
   vec3 n1 = normalize(v1Normal);
   vec3 n2 = normalize(v2Normal);

   vec3 tmp0 = localPos - v0Pos;
   vec3 tmp1 = localPos - v1Pos;
   vec3 tmp2 = localPos - v2Pos;

   float d0 = min(0.0, dot(tmp0, n0));
   float d1 = min(0.0, dot(tmp1, n1));
   float d2 = min(0.0, dot(tmp2, n2));

   tmp0 -= d0 * n0;
   tmp1 -= d1 * n1;
   tmp2 -= d2 * n2;

   return localPos + bary.x * tmp0 + bary.y * tmp1 + bary.z * tmp2;
}
