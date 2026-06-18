#include "../Shared.slang"
#include "../Bindings.slang"
#include "../Materials/PbrMaterial.slang"

struct VsOutput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
    float LinearDepth : TEXCOORD1;
};

[shader("fragment")]
float main(VsOutput input) : SV_Target0
{
    MeshAddressesGpu mesh = meshes[0]; // Not used for depth-only, but need for material opacity
    return input.LinearDepth;
}
