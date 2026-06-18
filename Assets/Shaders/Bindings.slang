#ifndef _BINDINGS_SLANG_
#define _BINDINGS_SLANG_

[[vk::binding(0, 0)]] StructuredBuffer<InstanceGpu, Std430DataLayout> instances;
[[vk::binding(1, 0)]] [format("rgba32f")] RWTexture2D<float4> outputColor;
[[vk::binding(2, 0)]] [format("rgba8")] RWTexture2D<float4> outputAlbedo;
[[vk::binding(3, 0)]] [format("rgba16f")] RWTexture2D<float4> outputNormal;
[[vk::binding(4, 0)]] [format("r32ui")] RWTexture2D<uint> outputCrypto;
[[vk::binding(5, 0)]] [format("rgba16f")] RWTexture2D<float4> outputPosition;
[[vk::binding(6, 0)]] [format("rgba16f")] RWTexture2D<float4> outputMaterial;
#define outputAdaptiveState outputMaterial
[[vk::binding(7, 0)]] StructuredBuffer<MeshAddressesGpu, Std430DataLayout> meshes;
[[vk::binding(8, 0)]] StructuredBuffer<SceneSettingsDataGpu, ScalarDataLayout> sceneSettingsBuffer;
#define sceneSettings sceneSettingsBuffer[0]
[[vk::binding(9, 0)]] Sampler2D textureSamplers[];

// Raster-only bindings beyond 9
[[vk::binding(10, 0)]] StructuredBuffer<MeshRasterMetadataGpu, Std430DataLayout> rasterMeshes;
[[vk::binding(11, 0)]] RWStructuredBuffer<uint> visibleInstanceIds;
[[vk::binding(12, 0)]] RWStructuredBuffer<uint> visibleCounts;
[[vk::binding(13, 0)]] RWStructuredBuffer<DrawIndirectCommandGpu> indirectCommands;
[[vk::binding(14, 0)]] [format("r32f")] RWTexture2D<float> rasterShadowDepth; // CSM atlas

// Light probe
[[vk::binding(16, 0)]] RWStructuredBuffer<float4> lightProbeData;

// TAA resources
[[vk::binding(19, 0)]] Sampler2D<float2> rasterVelocity;
[[vk::binding(20, 0)]] Sampler2D<float4> rasterHistory;
[[vk::binding(21, 0)]] [format("rgba32f")] RWTexture2D<float4> taaOutputColor;

// Pointer wrappers
struct VertexBuffer { Vertex data[]; };
struct IndexBuffer { uint data[]; };
struct FaceBuffer { Face data[]; };
struct MaterialBuffer { MaterialData data[]; };
struct BVHNodeBuffer { BvhNodeGpu data[]; };
struct BVHIndexBuffer { uint data[]; };

#endif
