using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using UniversalUmap.Rendering.Extensions;
using UniversalUmap.Rendering.Input;
using UniversalUmap.Rendering.Models;
using UniversalUmap.Rendering.Resources;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;
using AutoTextureItem = UniversalUmap.Rendering.Models.Materials.AutoTextureItem;
using Texture = Veldrid.Texture;

namespace UniversalUmap.Rendering;

//Host this in an Avalonia NativeControlHost
public class RenderContext : IDisposable
{
    private readonly object Monitor;
    private Thread RenderThread;
    private bool Exit;

    private ModelPipeline ModelPipeline;
    private readonly List<Component> Models;
    
    private readonly Camera Camera;
    private Skybox Skybox;
    
    private Sdl2Window Window;
    private uint Width;
    private uint Height;
    
    private GraphicsDevice GraphicsDevice;
    private ResourceFactory Factory;
    private Swapchain SwapChain;
    private CommandList CommandList;

    private DeviceBuffer CameraBuffer;
    private ResourceLayout CameraResourceLayout;
    private ResourceSet CameraResourceSet;
    
    private DeviceBuffer AutoTextureBuffer;

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
    
    //do this because UniversalUmap.Rendering is seperate
    public static ETexturePlatform TexturePlatform;
    public static AutoTextureItem[] AutoTextureItems;
    
    private static RenderContext instance;

    public static RenderContext GetInstance()
    {
        return instance ??= new RenderContext();
    }
    
    private RenderContext()
    {
        Disposables = [];
        Models = [];
        Width = 1280;
        Height = 720;
        Monitor = new object();
        SampleCount = TextureSampleCount.Count8; //MSAA
        Vsync = true;
        Exit = false;
        Camera = new Camera(new Vector3(0, 0, 100), new Vector3(0f, 0f, 0f), (float)Width/Height);
    }

    public IntPtr Initialize(IntPtr instanceHandle)
    {
        CreateGraphicsDevice();
        var windowHandle = CreateWindowSwapChain(instanceHandle);

        CreateFullscreenQuadPipeline();
        
        CommandList = Factory.CreateCommandList();
        Disposables.Add(CommandList);

        AutoTextureBuffer = GraphicsDevice.ResourceFactory.CreateBuffer(new BufferDescription(AutoTextureMasks.SizeOf(), BufferUsage.UniformBuffer));
        Disposables.Add(AutoTextureBuffer);
        
        CreateCameraLayoutBufferResourceSet();

        ModelPipeline = new ModelPipeline(GraphicsDevice, CameraResourceLayout, OffscreenFramebuffer.OutputDescription, AutoTextureBuffer);
        Disposables.Add(ModelPipeline);
        
        Skybox = new Skybox(GraphicsDevice, CommandList, ModelPipeline.TextureResourceLayout, OffscreenFramebuffer.OutputDescription, CameraBuffer);
        Disposables.Add(Skybox);
        
        RenderThread = new Thread(RenderLoop) { IsBackground = true };
        RenderThread.Start();

        return windowHandle;
    }

    public void LoadAutoTexture(AutoTextureItem[] autoTextureItems)
    {
        AutoTextureItems = autoTextureItems;
        var autoTextureMasks = new AutoTextureMasks();
        foreach (var item in AutoTextureItems)
        {
            var mask = new Vector4(item.R ? 1.0f : 0.0f, item.G ? 1.0f : 0.0f, item.B ? 1.0f : 0.0f, item.A ? 1.0f : 0.0f);
            switch (item.Parameter)
            {
                case "Color":
                    autoTextureMasks.Color = mask;
                    break;
                case "Metallic":
                    autoTextureMasks.Metallic = mask;
                    break;
                case "Specular":
                    autoTextureMasks.Specular = mask;
                    break;
                case "Roughness":
                    autoTextureMasks.Roughness = mask;
                    break;
                case "AO":
                    autoTextureMasks.AO = mask;
                    break;
                case "Normal":
                    autoTextureMasks.Normal = mask;
                    break;
                case "Emissive":
                    autoTextureMasks.Emissive = mask;
                    break;
                case "Alpha":
                    autoTextureMasks.Alpha = mask;
                    break;
            }
        }
        GraphicsDevice.UpdateBuffer(AutoTextureBuffer, 0, autoTextureMasks);
    }
    
    public void Load(UObject component, UStaticMesh mesh, FTransform[] transforms, UObject[] overrideMaterials)
    {
        lock (Monitor)
            Models.Add(new Component(ModelPipeline, GraphicsDevice, CommandList, CameraResourceSet, component, mesh, transforms, overrideMaterials));
    }
    
    public void Load(CStaticMesh mesh)
    {
        lock (Monitor)
            Models.Add(new Component(ModelPipeline, GraphicsDevice, CommandList, CameraResourceSet, mesh));
    }

    public void Clear()
    {
        lock (Monitor)
        {
            foreach (var model in Models)
                model.Dispose();
            Models.Clear();
            ResourceCache.Clear();
        }
    }
    
    private IntPtr CreateWindowSwapChain(IntPtr instanceHandle)
    {
        var windowOptions = new WindowCreateInfo
        {
            WindowWidth = (int)Width, WindowHeight = (int)Height, 
            WindowTitle = "UniversalUmap 3D Viewer",
            WindowInitialState = WindowState.Hidden
        };
        Window = VeldridStartup.CreateWindow(windowOptions);
        Window.Visible = false;
        Window.WindowState = WindowState.Normal;
        NativeWindowExtensions.MakeBorderless(Window.Handle);
        
        Window.MouseDown += @event =>
        {
            if(@event.MouseButton == MouseButton.Right)
                Sdl2Native.SDL_SetRelativeMouseMode(true);
        };
        Window.MouseUp += @event =>
        {
            if (@event.MouseButton == MouseButton.Right)
                Sdl2Native.SDL_SetRelativeMouseMode(false);
        };

        Window.Resized += OnResized;
        
        var swapchainSource = SwapchainSource.CreateWin32(Window.Handle, instanceHandle);
        var swapchainDescription = new SwapchainDescription(
            swapchainSource,
            Width,
            Height,
            PixelFormat.R32_Float,
            Vsync
        );
        SwapChain = Factory.CreateSwapchain(ref swapchainDescription);
        Disposables.Add(SwapChain);

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

    private void CreateGraphicsDevice()
    {
        var options = new GraphicsDeviceOptions
        {
            PreferStandardClipSpaceYDirection = true,
            PreferDepthRangeZeroToOne = true
        };
        GraphicsDevice = GraphicsDevice.CreateD3D11(options);
        Factory = GraphicsDevice.ResourceFactory;
        Disposables.Add(GraphicsDevice);
    }
    
    private void CreateCameraLayoutBufferResourceSet()
    {
        //camera resource layout
        CameraResourceLayout = Factory.CreateResourceLayout(
            new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("cameraUbo", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment)
            )
        );
        Disposables.Add(CameraResourceLayout);
        //Create uniform buffer
        CameraBuffer = Factory.CreateBuffer(new BufferDescription(CameraUniform.SizeOf(), BufferUsage.UniformBuffer));
        Disposables.Add(CameraBuffer);
        // Create the resource set
        CameraResourceSet = Factory.CreateResourceSet(new ResourceSetDescription(CameraResourceLayout, CameraBuffer));
        Disposables.Add(CameraResourceSet);
    }
    
    private void RenderLoop()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        long previousTicks = stopwatch.ElapsedTicks;
    
        int frameCount = 0;
        double elapsedTime = 0;
        while (!Exit)
        {
            long currentTicks = stopwatch.ElapsedTicks;
            double deltaTime = (currentTicks - previousTicks) / (double)Stopwatch.Frequency;
            previousTicks = currentTicks;
            
            Render(deltaTime);
            
            frameCount++;
            elapsedTime += deltaTime;
            // Check if 5 seconds have passed
            if (elapsedTime >= 5.0)
            {
                double fps = frameCount / elapsedTime;
                //Console.WriteLine($"FPS: {fps:F2}");
                frameCount = 0;
                elapsedTime = 0;
            }
        }
    }

    
    private void Render(double deltaTime)
    {
        //update input
        InputTracker.UpdateFrameInput(Window.PumpEvents(), Window);
        //update Camera
        GraphicsDevice.UpdateBuffer(CameraBuffer, 0, Camera.Update(deltaTime));
        
       CommandList.Begin();

        //Render to offscreen framebuffer
        CommandList.SetFramebuffer(OffscreenFramebuffer);
        CommandList.ClearDepthStencil(1);
        CommandList.ClearColorTarget(0, RgbaFloat.Clear);
        
        //Draw models
        Skybox.Render();

        lock (Monitor)
            foreach (var model in Models)
                model.Render(Camera.FrustumPlanes);
        
        CommandList.ResolveTexture(OffscreenColor, ResolvedColor);
        
        //Render to SwapChain framebuffer
        CommandList.SetFramebuffer(SwapChain.Framebuffer);
        CommandList.ClearDepthStencil(1);
        CommandList.ClearColorTarget(0, RgbaFloat.Clear);
        
        //Set fullscreen quad pipeline
        CommandList.SetPipeline(FullscreenQuadPipeline);
        CommandList.SetVertexBuffer(0, FullscreenQuadPositions);
        CommandList.SetVertexBuffer(1, FullscreenQuadTextureCoordinates);
        
        CommandList.SetGraphicsResourceSet(0, ResolvedColorResourceSet);
        
        //Draw fullscreen quad
        CommandList.Draw(4);
        
        CommandList.End();
        GraphicsDevice.SubmitCommands(CommandList);
        GraphicsDevice.SwapBuffers(SwapChain);
    }
    
    public void Dispose()
    {
        Exit = true;
        RenderThread.Join();
        
        Window.Close();
        
        Disposables.Reverse();
        foreach (var disposable in Disposables)
            disposable.Dispose();

        Clear();
        
        instance = null;
    }
    
    private VertexLayoutDescription[] CreateFullscreenQuadVertexLayouts()
    {
        var vertexLayoutPositions = new VertexLayoutDescription(new VertexElementDescription("Positions", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3));
        var vertexLayoutTextureCoordinates = new VertexLayoutDescription(new VertexElementDescription("TextureCoordinates", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));
        return [vertexLayoutPositions, vertexLayoutTextureCoordinates];
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
            Width = SwapChain.Framebuffer.Width,
            Height = SwapChain.Framebuffer.Height,
            Depth = 1,
            MipLevels = 1,
            ArrayLayers = 1,
            Format = PixelFormat.R32_Float,
            Type = TextureType.Texture2D,
            SampleCount = SampleCount,
            Usage = TextureUsage.DepthStencil
        });
        Disposables.Add(OffscreenDepth);
        
        if (recreate)
        {
            Disposables.Remove(OffscreenColor);
            OffscreenColor.Dispose();
        }
        OffscreenColor = Factory.CreateTexture(new TextureDescription
        {
            Width = SwapChain.Framebuffer.Width,
            Height = SwapChain.Framebuffer.Height,
            Depth = 1,
            MipLevels = 1,
            ArrayLayers = 1,
            Format = PixelFormat.R16_G16_B16_A16_Float,
            Type = TextureType.Texture2D,
            SampleCount = SampleCount,
            Usage = TextureUsage.RenderTarget | TextureUsage.Sampled
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
            Width = SwapChain.Framebuffer.Width,
            Height = SwapChain.Framebuffer.Height,
            Depth = 1,
            MipLevels = 1,
            ArrayLayers = 1,
            Format = PixelFormat.R16_G16_B16_A16_Float,
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
        var shaders = ShaderLoader.Load(GraphicsDevice, "FullscreenQuad");
        Disposables.Add(shaders[0]);
        Disposables.Add(shaders[1]);
        
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
