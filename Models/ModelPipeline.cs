using UniversalUmap.Rendering.Resources;
using Veldrid;

namespace UniversalUmap.Rendering.Models;

public class ModelPipeline : IDisposable
{
    private readonly GraphicsDevice GraphicsDevice;
    public Pipeline Pipeline;

    public ResourceSet TextureSamplerResourceSet;
    public ResourceSet AutoTextureResourceSet;

    private List<IDisposable> Disposables;
    
    public ModelPipeline(GraphicsDevice graphicsDevice, ResourceLayout cameraResourceLayout, ResourceLayout textureResourceLayout, OutputDescription outputDescription, DeviceBuffer autoTextureBuffer)
    {
        GraphicsDevice = graphicsDevice;
        Disposables = [];
        
        //Create main pipeline
        var autoTextureResourceLayout = CreateAutoTextureResourceLayout(autoTextureBuffer);
        var textureSamplerResourceLayout = CreateTextureSamplerResourceLayout();
        var combinedResourceLayout = new[]
        {
            autoTextureResourceLayout, cameraResourceLayout, textureSamplerResourceLayout, textureResourceLayout, 
            textureResourceLayout, textureResourceLayout, textureResourceLayout,
            textureResourceLayout, textureResourceLayout, textureResourceLayout, 
            textureResourceLayout
        };
        
        var vertexLayouts = CreateMainVertexLayouts();
        var shaders = ShaderLoader.Load(GraphicsDevice, "Model");
        Disposables.Add(shaders[0]);
        Disposables.Add(shaders[1]);
        var shaderSetDescription = new ShaderSetDescription(vertexLayouts, shaders);
        
        var mainRasterizerDescription = new RasterizerStateDescription(
            cullMode: FaceCullMode.Back,
            fillMode: PolygonFillMode.Solid,
            frontFace: FrontFace.CounterClockwise,
            depthClipEnabled: true,
            scissorTestEnabled: false
        );
        var depthStencilState = new DepthStencilStateDescription(
            depthTestEnabled: true,
            depthWriteEnabled: true,
            comparisonKind: ComparisonKind.LessEqual
        );
        Pipeline = GraphicsDevice.ResourceFactory.CreateGraphicsPipeline(
            new GraphicsPipelineDescription(
                BlendStateDescription.SingleAlphaBlend,
                depthStencilState,
                mainRasterizerDescription,
                PrimitiveTopology.TriangleList,
                shaderSetDescription,
                combinedResourceLayout,
                outputDescription
            )
        );
        Disposables.Add(Pipeline);
    }
    
    private VertexLayoutDescription[] CreateMainVertexLayouts()
    {
        var vertexLayout = new VertexLayoutDescription(
            new VertexElementDescription("position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
            new VertexElementDescription("color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
            new VertexElementDescription("normal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
            new VertexElementDescription("tangent", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
            new VertexElementDescription("uv", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2)
        );
        //instance info layout
        var instanceLayout = new VertexLayoutDescription(
            new VertexElementDescription("matrixRow0", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
            new VertexElementDescription("matrixRow1", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
            new VertexElementDescription("matrixRow2", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
            new VertexElementDescription("matrixRow3", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4)
        );
        instanceLayout.InstanceStepRate = 1;
        return [vertexLayout, instanceLayout];
    }
    
    private ResourceLayout CreateTextureSamplerResourceLayout()
    {
        //resource layout
        var textureSamplerResourceLayout = GraphicsDevice.ResourceFactory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("aniso4xSampler", ResourceKind.Sampler, ShaderStages.Fragment)
        ));
        Disposables.Add(textureSamplerResourceLayout);
        //resource set
        TextureSamplerResourceSet = GraphicsDevice.ResourceFactory.CreateResourceSet(new ResourceSetDescription(textureSamplerResourceLayout, GraphicsDevice.Aniso4xSampler));
        Disposables.Add(TextureSamplerResourceSet);
        return textureSamplerResourceLayout;
    }
    
    private ResourceLayout CreateAutoTextureResourceLayout(DeviceBuffer autoTextureBuffer)
    {
        var autoTextureResourceLayout = GraphicsDevice.ResourceFactory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("autoTextureUbo", ResourceKind.UniformBuffer, ShaderStages.Fragment))
        );
        Disposables.Add(autoTextureResourceLayout);
        // Create the resource set
        AutoTextureResourceSet = GraphicsDevice.ResourceFactory.CreateResourceSet(new ResourceSetDescription(autoTextureResourceLayout, autoTextureBuffer));
        Disposables.Add(AutoTextureResourceSet);
        return autoTextureResourceLayout;
    }
    
    public void Dispose()
    {
        Disposables.Reverse();
        foreach (var disposable in Disposables)
            disposable.Dispose();
    }
}