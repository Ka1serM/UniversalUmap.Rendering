using System.Numerics;
using System.Runtime.InteropServices;
using UniversalUmap.Rendering.Resources;
using Veldrid;
using Texture = UniversalUmap.Rendering.Models.Materials.Texture;

namespace UniversalUmap.Rendering.Models;

public class Skybox : IDisposable
{
    private readonly CommandList CommandList;
    
    private readonly DeviceBuffer VertexBuffer;
    private readonly DeviceBuffer IndexBuffer;

    private readonly Pipeline Pipeline;
    private readonly ResourceSet ResourceSet;

    private readonly List<IDisposable> Disposables;

    public Skybox(GraphicsDevice graphicsDevice, CommandList commandList, ResourceLayout textureLayout, OutputDescription outputDescription, DeviceBuffer cameraBuffer)
    {
        CommandList = commandList;
        Disposables = [];
        
        VertexBuffer = graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription((uint)Vertices.Length * (uint)Marshal.SizeOf<Vector3>(), BufferUsage.VertexBuffer));
        Disposables.Add(VertexBuffer);
        graphicsDevice.UpdateBuffer(VertexBuffer, 0, Vertices);

        IndexBuffer = graphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription((uint)Indices.Length * (uint)Marshal.SizeOf<ushort>(), BufferUsage.IndexBuffer));
        Disposables.Add(IndexBuffer);
        graphicsDevice.UpdateBuffer(IndexBuffer, 0, Indices);
        
        Texture textureCube = new Texture(graphicsDevice, textureLayout, ["px", "nx", "ny", "py", "pz", "nz"]);
        Disposables.Add(textureCube);
        
        TextureView textureView = graphicsDevice.ResourceFactory.CreateTextureView(new TextureViewDescription(textureCube.VeldridTexture));
        var vertexLayout = new VertexLayoutDescription(
            new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3)
        );
        Disposables.Add(textureView);
        
        var shaders = ShaderLoader.Load(graphicsDevice, "Skybox");
        Disposables.Add(shaders[0]);
        Disposables.Add(shaders[1]);

        var resourceLayout = graphicsDevice.ResourceFactory.CreateResourceLayout(
            new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("cameraUbo", ResourceKind.UniformBuffer, ShaderStages.Vertex),
                new ResourceLayoutElementDescription("cubeTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("pointSampler", ResourceKind.Sampler, ShaderStages.Fragment)
            )
        );
        Disposables.Add(resourceLayout);
        var pipelineDescription = new GraphicsPipelineDescription(
            BlendStateDescription.SingleAlphaBlend,
            new DepthStencilStateDescription
            {
                DepthTestEnabled = false,
                DepthWriteEnabled = false,
                DepthComparison = ComparisonKind.LessEqual,
            },
            new RasterizerStateDescription(
                FaceCullMode.None,
                PolygonFillMode.Solid,
                FrontFace.Clockwise,
                depthClipEnabled: true,
                scissorTestEnabled: false
            ),
            PrimitiveTopology.TriangleList,
            new ShaderSetDescription(
                [vertexLayout],
                shaders
            ),
            resourceLayout,
            outputDescription
        );
        Pipeline = graphicsDevice.ResourceFactory.CreateGraphicsPipeline(ref pipelineDescription);
        Disposables.Add(Pipeline);
        ResourceSet = graphicsDevice.ResourceFactory.CreateResourceSet(new ResourceSetDescription(resourceLayout, cameraBuffer, textureView, graphicsDevice.PointSampler));
        Disposables.Add(ResourceSet);
    }

    public void Render()
    {
        CommandList.SetVertexBuffer(0, VertexBuffer);
        CommandList.SetIndexBuffer(IndexBuffer, IndexFormat.UInt16);
        CommandList.SetPipeline(Pipeline);
        CommandList.SetGraphicsResourceSet(0, ResourceSet);
        CommandList.DrawIndexed((uint)Indices.Length, 1, 0, 0, 0);
    }
    
     private static readonly Vector3[] Vertices =
     [
         // Top
            new Vector3(-20.0f,20.0f,-20.0f),
            new Vector3(20.0f,20.0f,-20.0f),
            new Vector3(20.0f,20.0f,20.0f),
            new Vector3(-20.0f,20.0f,20.0f),
            // Bottom
            new Vector3(-20.0f,-20.0f,20.0f),
            new Vector3(20.0f,-20.0f,20.0f),
            new Vector3(20.0f,-20.0f,-20.0f),
            new Vector3(-20.0f,-20.0f,-20.0f),
            // Left
            new Vector3(-20.0f,20.0f,-20.0f),
            new Vector3(-20.0f,20.0f,20.0f),
            new Vector3(-20.0f,-20.0f,20.0f),
            new Vector3(-20.0f,-20.0f,-20.0f),
            // Right
            new Vector3(20.0f,20.0f,20.0f),
            new Vector3(20.0f,20.0f,-20.0f),
            new Vector3(20.0f,-20.0f,-20.0f),
            new Vector3(20.0f,-20.0f,20.0f),
            // Back
            new Vector3(20.0f,20.0f,-20.0f),
            new Vector3(-20.0f,20.0f,-20.0f),
            new Vector3(-20.0f,-20.0f,-20.0f),
            new Vector3(20.0f,-20.0f,-20.0f),
            // Front
            new Vector3(-20.0f,20.0f,20.0f),
            new Vector3(20.0f,20.0f,20.0f),
            new Vector3(20.0f,-20.0f,20.0f),
            new Vector3(-20.0f,-20.0f,20.0f)
     ];
        private static readonly ushort[] Indices =
        [
            0,1,2, 0,2,3,
            4,5,6, 4,6,7,
            8,9,10, 8,10,11,
            12,13,14, 12,14,15,
            16,17,18, 16,18,19,
            20,21,22, 20,22,23
        ];
        
        public void Dispose()
        {
            Disposables.Reverse();
            foreach(var disposable in Disposables)
                disposable.Dispose();
        }
}
            
            