using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.SPIRV;
using Veldrid.StartupUtilities;

namespace UniversalUmap.Rendering;

public class GraphicsContext : IDisposable
{
    private Thread RenderThread;
    private bool Exit = false;

    private List<Model> Models;

    private Camera.Camera Camera;

    private Sdl2Window Window;

    private GraphicsDevice GraphicsDevice;
    private ResourceFactory Factory;
    private Swapchain Swapchain;
    
    private readonly CommandList CommandList;
    private readonly List<ResourceLayout> ResourceLayouts = new();

    private DeviceBuffer CameraBuffer;
    private ResourceSet CameraResourceSet;
    
    private Shader[] Shaders;
    private Pipeline Pipeline;

    private const string VertexCode = @"
      #version 450

    // Vertex attributes
    layout(location = 0) in vec3 position;
    layout(location = 1) in vec3 color;
    layout(location = 2) in vec3 normal;

    // Instance attributes
    layout(location = 3) in vec4 transformRow0;
    layout(location = 4) in vec4 transformRow1;
    layout(location = 5) in vec4 transformRow2;
    layout(location = 6) in vec4 transformRow3;

    // Vertex shader outputs
    layout(location = 0) out vec3 fragColor;
    layout(location = 1) out vec3 fragNormalWorld;

    // Uniform buffer
    layout(set = 0, binding = 0) uniform cameraUbo
    {
        mat4 projection;
        mat4 view;
        vec4 front;
    } ubo;

    void main() {
        mat4 instanceTransform = mat4(transformRow0, transformRow1, transformRow2, transformRow3);
        vec4 worldPosition = instanceTransform * vec4(position, 1.0);

        gl_Position = ubo.projection * ubo.view * worldPosition;

        mat3 normalWorldMatrix = transpose(inverse(mat3(instanceTransform)));
        fragNormalWorld = normalize(normalWorldMatrix * normal);
        fragColor = color;
    }
    ";

    private const string FragmentCode = @"
    #version 450

    // Fragment shader inputs
    layout(location = 0) in vec3 fragColor;
    layout(location = 1) in vec3 fragNormalWorld;

    // Fragment shader output
    layout(location = 0) out vec4 outColor;

    // Uniform buffer
    layout(set = 0, binding = 0) uniform cameraUbo
    {
        mat4 projection;
        mat4 view;
        vec4 front;
    } ubo;

    void main() {
        vec3 viewDirection = normalize(ubo.front.xyz);

        float ambientContribution = 0.3;
        float diffuseContribution = max(dot(-viewDirection, fragNormalWorld), 0.0);

        vec3 finalColor = (ambientContribution + diffuseContribution) * fragColor;
        outColor = vec4(finalColor, 1.0);
    }
    ";

    public GraphicsContext(out IntPtr windowlHandle, IntPtr instanceHandle)
    {
        windowlHandle = InitializeWindow();
        InitializeGraphicsDevice();
        CreateSwapchain(windowlHandle, instanceHandle);
        CreateBuffers();
        CreateShaders();
        CreatePipeline();

        CommandList = Factory.CreateCommandList();
        
        Camera = new Camera.Camera(new Vector3(0, 0, 0), -Vector3.UnitZ, (float)16 / 9);
        Models = new List<Model>();
    }

    private IntPtr InitializeWindow()
    {
        var windowOptions = new WindowCreateInfo
        {
            X = 0, Y = 0, WindowWidth = 960, WindowHeight = 540, 
            WindowTitle = "UniversalUmap Preview",
            WindowInitialState = WindowState.Hidden
        };
        Window = VeldridStartup.CreateWindow(windowOptions);
        Window.Visible = false;
        Window.WindowState = WindowState.Normal;
        NativeWindowExtensions.MakeBorderless(Window.Handle);
        Sdl2Native.SDL_SetRelativeMouseMode(true);

        return Window.Handle;
    }

    private void InitializeGraphicsDevice()
    {
        GraphicsDeviceOptions options = new GraphicsDeviceOptions
        {
            PreferStandardClipSpaceYDirection = true,
            PreferDepthRangeZeroToOne = true
        };
        GraphicsDevice = GraphicsDevice.CreateD3D11(options);
        Factory = GraphicsDevice.ResourceFactory;
    }

    private void CreateSwapchain(IntPtr controlHandle, IntPtr instanceHandle)
    {
        var swapchainSource = SwapchainSource.CreateWin32(controlHandle, instanceHandle);
        var swapchainDescription = new SwapchainDescription(swapchainSource, 960, 540, PixelFormat.R32_Float, true);
        Swapchain = Factory.CreateSwapchain(ref swapchainDescription);
    }

    private void CreateBuffers()
    {
        //Create and add resource layouts
        var cameraResourceLayout = Factory.CreateResourceLayout(
            new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("cameraUbo", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment)));
        ResourceLayouts.Add(cameraResourceLayout);

        //Create uniform buffer
        CameraBuffer = Factory.CreateBuffer(new BufferDescription(CameraUniform.SizeOf(), BufferUsage.UniformBuffer));
        // Create the resource set
        CameraResourceSet = Factory.CreateResourceSet(new ResourceSetDescription(cameraResourceLayout, CameraBuffer));
    }

    private void CreateShaders()
    {
        var vertexShaderDesc = new ShaderDescription(
            ShaderStages.Vertex,
            Encoding.UTF8.GetBytes(VertexCode),
            "main"
        );
        var fragmentShaderDesc = new ShaderDescription(
            ShaderStages.Fragment,
            Encoding.UTF8.GetBytes(FragmentCode),
            "main"
        );
        Shaders = Factory.CreateFromSpirv(vertexShaderDesc, fragmentShaderDesc);
    }

    private void CreatePipeline()
    {
        VertexLayoutDescription vertexLayout = new VertexLayoutDescription(
            new VertexElementDescription("position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
            new VertexElementDescription("color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
            new VertexElementDescription("normal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3)
        );
        
        //instance info layout
        var instanceLayout = new VertexLayoutDescription(
            new VertexElementDescription("transformRow0", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
            new VertexElementDescription("transformRow1", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
            new VertexElementDescription("transformRow2", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4),
            new VertexElementDescription("transformRow3", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4)
        );
        instanceLayout.InstanceStepRate = 1;
        
        var shaderSet = new ShaderSetDescription(
            vertexLayouts: [vertexLayout, instanceLayout],
            shaders: Shaders
        );
        var rasterizerStateDescription = new RasterizerStateDescription(
            cullMode: FaceCullMode.Back,
            fillMode: PolygonFillMode.Solid,
            frontFace: FrontFace.CounterClockwise,
            depthClipEnabled: true,
            scissorTestEnabled: false
        );
        Pipeline = Factory.CreateGraphicsPipeline(
            new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                DepthStencilStateDescription.DepthOnlyLessEqual,
                rasterizerStateDescription,
                PrimitiveTopology.TriangleList,
                shaderSet,
                ResourceLayouts.ToArray(),
                Swapchain.Framebuffer.OutputDescription
            )
        );
    }

    public void Initialize()
    {
        RenderThread = new Thread(RenderLoop) { IsBackground = true };
        RenderThread.Start();
    }

    private void RenderLoop()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        long previousTicks = stopwatch.ElapsedTicks;
        while (!Exit)
        {
            long currentTicks = stopwatch.ElapsedTicks;
            double deltaTime = (currentTicks - previousTicks) / (double)Stopwatch.Frequency;
            
            UpdateCamera(deltaTime);
            Render(deltaTime);

            previousTicks = currentTicks;
        }
    }

    private void UpdateCamera(double deltaTime)
    {
        GraphicsDevice.UpdateBuffer(CameraBuffer, 0, Camera.Update(deltaTime, Window));
    }

    private void Render(double deltaTime)
    {
        CommandList.Begin();
        CommandList.SetFramebuffer(Swapchain.Framebuffer);
        CommandList.ClearColorTarget(0, RgbaFloat.White);
        CommandList.ClearDepthStencil(1);
        
        CommandList.SetPipeline(Pipeline);
        CommandList.SetGraphicsResourceSet(0, CameraResourceSet);
        
        foreach (var model in Models)
            model.Render();

        CommandList.End();
        GraphicsDevice.SubmitCommands(CommandList);
        GraphicsDevice.SwapBuffers(Swapchain);
    }
    
    public void Resize(uint w, uint h)
    {
        Swapchain.Resize(w, h);
    }

    public void Dispose()
    {
        Exit = true;
        RenderThread.Join();

        Window.Close();
        Swapchain.Dispose();
        CommandList.Dispose();
        CameraBuffer.Dispose();
        Pipeline.Dispose();
        foreach (var shader in Shaders)
            shader.Dispose();
        foreach (var model in Models)
            model.Dispose();
        GraphicsDevice.Dispose();
    }

    public void Load(Dictionary<UStaticMesh, List<FTransform>> models)
    {
        foreach (var model in models)
            Models.Add(new Model(GraphicsDevice, CommandList, model.Key, model.Value));
    }
}
