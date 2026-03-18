struct VsOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
VsOut main(uint vertexIndex : SV_VertexID)
{
    VsOut o;

    float2 p;
    if (vertexIndex == 0)
        p = float2(-1.0, -1.0);
    else if (vertexIndex == 1)
        p = float2(3.0, -1.0);
    else
        p = float2(-1.0, 3.0);

    o.position = float4(p, 0.0, 1.0);
    o.uv = (p * 0.5) + 0.5;
    return o;
}
