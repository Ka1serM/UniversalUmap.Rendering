using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.SPIRV;
using Veldrid.StartupUtilities;

namespace UniversalUmap.Rendering;

//Host this in an Avalonia NativeControlHost
public class GraphicsContext : IDisposable
{
    private readonly object Monitor;
    private readonly Thread RenderThread;
    private bool Exit;

    private readonly List<Model> Models;
    private readonly Camera.Camera Camera;
    
    private Sdl2Window Window;
    private uint Width;
    private uint Height;
    
    private GraphicsDevice GraphicsDevice;
    private ResourceFactory Factory;
    private Swapchain SwapChain;
    private Pipeline MainPipeline;
    
    private readonly CommandList CommandList;

    private DeviceBuffer CameraBuffer;
    private ResourceSet CameraResourceSet;

    private readonly TextureSampleCount SampleCount;
    private readonly bool Vsync;

    private Texture OffscreenColor;
    private Framebuffer OffscreenFramebuffer;
    private Pipeline FullscreenQuadPipeline;
    private DeviceBuffer FullscreenQuadPositions;
    private DeviceBuffer FullscreenQuadTextureCoordinates;

    private Texture ResolvedColor;
    private ResourceSet ResolvedColorResourceSet;

    private readonly List<IDisposable> Disposables;
    private Texture OffscreenDepth;
    private ResourceLayout ResolvedColorResourceLayout;
    private TextureView ResolvedColorTextureView;
    
    public GraphicsContext(out IntPtr windowlHandle, IntPtr instanceHandle, uint width, uint height)
    {
        Disposables = [];
        Models = [];
        Width = width;
        Height = height;
        Monitor = new object();
        SampleCount = TextureSampleCount.Count4; //MSAA
        Vsync = true;
        
        windowlHandle = InitializeWindow();
        InitializeGraphicsDevice();
        CreateSwapchain(windowlHandle, instanceHandle);

        CreateFullscreenQuadPipeline();
        CreateMainPipeline();
        
        CommandList = Factory.CreateCommandList();
        Disposables.Add(CommandList);
        
        Camera = new Camera.Camera(new Vector3(0, 0, 0), -Vector3.UnitZ, (float)Width / Height);
        
        Exit = false;
        RenderThread = new Thread(RenderLoop) { IsBackground = true };
        RenderThread.Start();
    }
    
    public void Load(UStaticMesh mesh, List<FTransform> transforms)
    {
        var model = new Model(GraphicsDevice, CommandList, mesh, transforms);
        lock (Monitor)
        {
            Models.Add(model);
            Disposables.Add(model);
        }
    }

    private IntPtr InitializeWindow()
    {
        var windowOptions = new WindowCreateInfo
        {
            WindowWidth = (int)Width, WindowHeight = (int)Height, 
            WindowTitle = "UniversalUmap Preview",
            WindowInitialState = WindowState.Hidden
        };
        Window = VeldridStartup.CreateWindow(windowOptions);
        Window.Visible = false;
        Window.WindowState = WindowState.Normal;
        NativeWindowExtensions.MakeBorderless(Window.Handle);
        Sdl2Native.SDL_SetRelativeMouseMode(true);

        Window.Resized += OnResized;

        return Window.Handle;
    }

    private void OnResized()
    {
        Width = (uint)Window.Width;
        Height = (uint)Window.Height;
        SwapChain.Resize(Width, Height);
        Camera.Resize(Width, Height);
        CreateOffscreenFramebuffer(recreate: true);
        CreateResolvedColorResourceSet(recreate: true);
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
        Disposables.Add(GraphicsDevice);
    }

    private void CreateSwapchain(IntPtr controlHandle, IntPtr instanceHandle)
    {
        var swapchainSource = SwapchainSource.CreateWin32(controlHandle, instanceHandle);
        var swapchainDescription = new SwapchainDescription(
            swapchainSource, 
            Width,
            Height,
            PixelFormat.R32_Float,
            Vsync //v-Sync
        );
        SwapChain = Factory.CreateSwapchain(ref swapchainDescription);
        Disposables.Add(SwapChain);
    }
    
    private ResourceLayout CreateMainResourceLayout()
    {
        //Create and add camera resource layout
        var cameraResourceLayout = Factory.CreateResourceLayout(
            new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("cameraUbo", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment)));
        Disposables.Add(cameraResourceLayout);
        
        //Create uniform buffer
        CameraBuffer = Factory.CreateBuffer(new BufferDescription(CameraUniform.SizeOf(), BufferUsage.UniformBuffer));
        Disposables.Add(CameraBuffer);
        
        // Create the resource set
        CameraResourceSet = Factory.CreateResourceSet(new ResourceSetDescription(cameraResourceLayout, CameraBuffer));
        Disposables.Add(CameraResourceSet);
        
        return cameraResourceLayout;
    }

    private Shader[] CreateShaders(string vertexSource, string fragementSource)
    {
        var vertexShaderDesc = new ShaderDescription(
            ShaderStages.Vertex,
            Encoding.UTF8.GetBytes(vertexSource),
            "main"
        );
        var fragmentShaderDesc = new ShaderDescription(
            ShaderStages.Fragment,
            Encoding.UTF8.GetBytes(fragementSource),
            "main"
        );
        var shaders = Factory.CreateFromSpirv(vertexShaderDesc, fragmentShaderDesc);
        foreach (var shader in shaders)
            Disposables.Add(shader);
        return shaders;
    }
    
    private void CreateMainPipeline()
    {
        var resourceLayout = CreateMainResourceLayout();
        var vertexLayouts = CreateMainVertexLayouts();
        var shaders = CreateShaders(Shaders.MainVertexSource, Shaders.MainFragmentSource);
        var rasterizerStateDescription = new RasterizerStateDescription(
            cullMode: FaceCullMode.Back,
            fillMode: PolygonFillMode.Solid,
            frontFace: FrontFace.CounterClockwise,
            depthClipEnabled: true,
            scissorTestEnabled: false
        );
        MainPipeline = Factory.CreateGraphicsPipeline(
            new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                DepthStencilStateDescription.DepthOnlyLessEqual,
                rasterizerStateDescription,
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(vertexLayouts, shaders),
                resourceLayout,
                OffscreenFramebuffer.OutputDescription
            )
        );
        Disposables.Add(MainPipeline);
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

        //Render to offscreen framebuffer
        CommandList.SetFramebuffer(OffscreenFramebuffer);
        CommandList.SetViewport(0, new Viewport(0, 0, Width, Height, 0, 1));
        CommandList.ClearDepthStencil(1);
        CommandList.ClearColorTarget(0, RgbaFloat.Clear);
        
        CommandList.SetPipeline(MainPipeline);
        CommandList.SetGraphicsResourceSet(0, CameraResourceSet);

        //Draw models
        lock (Monitor)
        {
            foreach (var model in Models)
                model.Render();
        }
        
        //Render to SwapChain framebuffer
        CommandList.SetFramebuffer(SwapChain.Framebuffer);
        CommandList.SetViewport(0, new Viewport(0, 0, Width, Height, 0, 1));
        CommandList.ClearDepthStencil(1);
        CommandList.ClearColorTarget(0, RgbaFloat.Clear);
        
        //Set fullscreen quad pipeline
        CommandList.SetPipeline(FullscreenQuadPipeline);
        CommandList.SetVertexBuffer(0, FullscreenQuadPositions);
        CommandList.SetVertexBuffer(1, FullscreenQuadTextureCoordinates);
        CommandList.SetGraphicsResourceSet(0, ResolvedColorResourceSet);
        
        CommandList.ResolveTexture(OffscreenColor, ResolvedColor);
        
        //Draw fullscreen quad
        CommandList.Draw(4);
        
        CommandList.End();
        GraphicsDevice.SubmitCommands(CommandList);
        GraphicsDevice.SwapBuffers(SwapChain);
        //GraphicsDevice.WaitForIdle();
    }
    
    public void Dispose()
    {
        Exit = true;
        RenderThread.Join();
        
        Window.Close();
        
        Disposables.Reverse();
        foreach (var disposable in Disposables)
            if(disposable != null)
                disposable.Dispose();
    }
    
    private VertexLayoutDescription[] CreateFullscreenQuadVertexLayouts()
    {
        var vertexLayoutPositions = new VertexLayoutDescription(new VertexElementDescription("Positions", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3));
        var vertexLayoutTextureCoordinates = new VertexLayoutDescription(new VertexElementDescription("TextureCoordinates", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));
        return [vertexLayoutPositions, vertexLayoutTextureCoordinates];
    }
    
    private VertexLayoutDescription[] CreateMainVertexLayouts()
    {
        var vertexLayout = new VertexLayoutDescription(
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
        return [vertexLayout, instanceLayout];
    }
    
    private void CreateOffscreenFramebuffer(bool recreate = false)
    {
        if (recreate)
        {
            Disposables.Remove(OffscreenDepth);
            OffscreenDepth.Dispose();
        }
        OffscreenDepth = Factory.CreateTexture(new TextureDescription
        {
            Width = Width,
            Height = Height,
            Depth = 1,
            MipLevels = 1,
            ArrayLayers = 1,
            Format = PixelFormat.R32_Float,
            Type = TextureType.Texture2D,
            SampleCount = SampleCount,
            Usage = TextureUsage.DepthStencil | TextureUsage.Sampled,
        });
        Disposables.Add(OffscreenDepth);
        
        if (recreate)
        {
            Disposables.Remove(OffscreenColor);
            OffscreenColor.Dispose();
        }
        OffscreenColor = Factory.CreateTexture(new TextureDescription
        {
            Width = Width,
            Height = Height,
            Depth = 1,
            MipLevels = 1,
            ArrayLayers = 1,
            Format = PixelFormat.B8_G8_R8_A8_UNorm,
            Type = TextureType.Texture2D,
            SampleCount = SampleCount,
            Usage = TextureUsage.RenderTarget | TextureUsage.Sampled,
        });
        Disposables.Add(OffscreenColor);
        
        if (recreate)
        {
            Disposables.Remove(OffscreenFramebuffer);
            OffscreenFramebuffer.Dispose();
        }
        OffscreenFramebuffer = Factory.CreateFramebuffer(new FramebufferDescription(OffscreenDepth, OffscreenColor));
        Disposables.Add(OffscreenFramebuffer);
    }
    
    private void CreateResolvedColorResourceSet(bool recreate = false)
    {
        if (recreate)
        {
            Disposables.Remove(ResolvedColorResourceLayout);
            ResolvedColorResourceLayout.Dispose();
        }
        ResolvedColorResourceLayout = Factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("TextureColor", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("SamplerColor", ResourceKind.Sampler, ShaderStages.Fragment)
        ));
        Disposables.Add(ResolvedColorResourceLayout);
        
        if (recreate)
        {
            Disposables.Remove(ResolvedColor);
            ResolvedColor.Dispose();
        }
        ResolvedColor = Factory.CreateTexture(new TextureDescription
        {
            Width = Width,
            Height = Height,
            Depth = 1,
            MipLevels = 1,
            ArrayLayers = 1,
            Format = PixelFormat.B8_G8_R8_A8_UNorm,
            Type = TextureType.Texture2D,
            SampleCount = TextureSampleCount.Count1,
            Usage = TextureUsage.RenderTarget | TextureUsage.Sampled,
        });
        Disposables.Add(ResolvedColor);
        
        if (recreate)
        {
            Disposables.Remove(ResolvedColorTextureView);
            ResolvedColorTextureView.Dispose();
        }
        ResolvedColorTextureView = Factory.CreateTextureView(ResolvedColor);
        Disposables.Add(ResolvedColorTextureView);
        
        if (recreate)
        {
            Disposables.Remove(ResolvedColorResourceSet);
            ResolvedColorResourceSet.Dispose();
        }
        ResolvedColorResourceSet = Factory.CreateResourceSet(new ResourceSetDescription(ResolvedColorResourceLayout, ResolvedColorTextureView, GraphicsDevice.PointSampler));
        Disposables.Add(ResolvedColorResourceSet);
    }

    private void CreateFullscreenQuadPipeline()
    {
        CreateOffscreenFramebuffer();
        CreateFullscreenQuadBuffers();
        
        var vertexLayouts = CreateFullscreenQuadVertexLayouts();
        var shaders = CreateShaders(Shaders.FullscreenQuadVertexSource, Shaders.FullscreenQuadFragmentSource);
        CreateResolvedColorResourceSet();
        var pipelineDescription = new GraphicsPipelineDescription(
            BlendStateDescription.SingleAlphaBlend,
            new DepthStencilStateDescription
            {
                DepthTestEnabled = true,
                DepthWriteEnabled = true,
                DepthComparison = ComparisonKind.LessEqual,
            },
            new RasterizerStateDescription
            {
                CullMode = FaceCullMode.None,
                FillMode = PolygonFillMode.Solid,
                FrontFace = FrontFace.CounterClockwise,
                DepthClipEnabled = true,
                ScissorTestEnabled = false,
            },
            PrimitiveTopology.TriangleStrip,
            new ShaderSetDescription(vertexLayouts, shaders),
            [ResolvedColorResourceLayout],
            SwapChain.Framebuffer.OutputDescription
        );
        FullscreenQuadPipeline = Factory.CreateGraphicsPipeline(pipelineDescription);
        Disposables.Add(FullscreenQuadPipeline);
    }

    private void CreateFullscreenQuadBuffers()
    {
        var fullscreenQuadPositions = new[]
        {
            new Vector3(-1, -1, 0),
            new Vector3(1, -1, 0),
            new Vector3(-1, 1, 0),
            new Vector3(1, 1, 0),
        };
        FullscreenQuadPositions = Factory.CreateBuffer(new BufferDescription((uint)(Marshal.SizeOf<Vector3>() * fullscreenQuadPositions.Length), BufferUsage.VertexBuffer));
        Disposables.Add(FullscreenQuadPositions);
        GraphicsDevice.UpdateBuffer(FullscreenQuadPositions, 0, fullscreenQuadPositions);

        var fullscreenQuadTextureCoordinates = GraphicsDevice.IsUvOriginTopLeft
            ? new[]
            {
                new Vector2(0, 1),
                new Vector2(1, 1),
                new Vector2(0, 0),
                new Vector2(1, 0),
            }
            :
            [
                new Vector2(0, 0),
                new Vector2(1, 0),
                new Vector2(0, 1),
                new Vector2(1, 1)
            ];
        FullscreenQuadTextureCoordinates = Factory.CreateBuffer(new BufferDescription((uint)(Marshal.SizeOf<Vector2>() * fullscreenQuadTextureCoordinates.Length), BufferUsage.VertexBuffer));
        Disposables.Add(FullscreenQuadTextureCoordinates);
        GraphicsDevice.UpdateBuffer(FullscreenQuadTextureCoordinates, 0, fullscreenQuadTextureCoordinates);
    }
}
