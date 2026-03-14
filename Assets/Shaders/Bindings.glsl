#ifndef _BINDINGS_GLSL_ //include guard to prevent multiple inclusions
#define _BINDINGS_GLSL_

#ifdef USE_COMPUTE
    // For the Compute pipeline, binding 0 is the buffer of instance data.
layout(set = 0, binding = 0) buffer InstanceBuffer { ComputeInstanceGpu instances[]; };
#else
    // For the RTX pipeline, binding 0 is the Top-Level Acceleration Structure.
layout(set = 0, binding = 0) uniform accelerationStructureEXT topLevelAS;
#endif

layout (set = 0, binding = 1, rgba32f) uniform image2D outputColor;
layout (set = 0, binding = 2, rgba8) uniform image2D outputAlbedo;
layout (set = 0, binding = 3, rgba16f) uniform image2D outputNormal;
layout (set = 0, binding = 4, r32ui) uniform uimage2D outputCrypto;
layout (set = 0, binding = 5, rgba16f) uniform image2D outputPosition;
layout (set = 0, binding = 6, rgba32f) uniform image2D outputAdaptiveState;

// Binding 2: Mesh Data Pointers (vertex, index, material addresses, etc.)
layout(set = 0, binding = 7) buffer MeshAddressesBuffer { MeshAddressesGpu meshes[]; };

layout(set = 0, binding = 8, scalar) readonly buffer SceneSettingsBuffer {
    RenderSettingsDataGpu renderSettings;
    CameraDataGpu camera;
    EnvironmentDataGpu environment;
} sceneSettings;

// Binding 3: Global Texture Sampler Array. Keep this last.
layout(set = 0, binding = 9) uniform sampler2D textureSamplers[];

// Buffer Reference Type Definitions 
layout(buffer_reference, scalar) buffer VertexBuffer { Vertex data[]; };
layout(buffer_reference, scalar) buffer IndexBuffer { uint data[]; };
layout(buffer_reference, scalar) buffer FaceBuffer { Face data[]; };
layout(buffer_reference, scalar) buffer MaterialBuffer { MaterialData data[]; };
//only Compute RT
layout(buffer_reference, scalar) buffer BVHNodeBuffer { BvhNodeGpu data[]; };
layout(buffer_reference, scalar) buffer BVHIndexBuffer { uint data[]; };

#endif
