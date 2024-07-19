using System;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.SPIRV;
using Veldrid.StartupUtilities;

namespace UniversalUmap.Rendering;

public class GraphicsContext : IDisposable
{
    private Thread RenderThread;
    private bool Exit = false;

    private readonly Sdl2Window Window;
    private readonly GraphicsDevice GraphicsDevice;
    private readonly Swapchain Swapchain;
    private readonly CommandList CommandList;
    private readonly DeviceBuffer VertexBuffer;
    private readonly DeviceBuffer IndexBuffer;
    private readonly Shader[] Shaders;
    private readonly Pipeline Pipeline;
    
    private const string VertexCode = @"
    #version 450

    layout(location = 0) in vec2 Position;
    layout(location = 1) in vec4 Color;

    layout(location = 0) out vec4 fsin_Color;

    void main()
    {
        gl_Position = vec4(Position, 0, 1);
        fsin_Color = Color;
    }";

    private const string FragmentCode = @"
    #version 450

    layout(location = 0) in vec4 fsin_Color;
    layout(location = 0) out vec4 fsout_Color;

    void main()
    {
        fsout_Color = fsin_Color;
    }";
    
    public GraphicsContext(out IntPtr controlHandle, IntPtr instanceHandle)
    {
        GraphicsDeviceOptions options = new GraphicsDeviceOptions
        {
            PreferStandardClipSpaceYDirection = true,
            PreferDepthRangeZeroToOne = true
        };
        
        Window = VeldridStartup.CreateWindow(
            new WindowCreateInfo { X = 0, Y = 0, WindowWidth = 960, WindowHeight = 540, WindowTitle = "UniversalUmap Preview", WindowInitialState = WindowState.Hidden }
        );
        controlHandle = Window.Handle; //out
        Window.Visible = false;
        Window.WindowState = WindowState.Normal;
        NativeWindowExtensions.MakeBorderless(Window.Handle);
        Sdl2Native.SDL_SetRelativeMouseMode(true);
        
        GraphicsDevice = GraphicsDevice.CreateD3D11(options);
        
        var swapchainSource = SwapchainSource.CreateWin32(controlHandle, instanceHandle);
        var swapchainDescription = new SwapchainDescription(swapchainSource, 960, 540, PixelFormat.R32_Float, true);
        
        Swapchain = GraphicsDevice.ResourceFactory.CreateSwapchain(swapchainDescription);
        
        ResourceFactory factory = GraphicsDevice.ResourceFactory;
        
        VertexPositionColor[] quadVertices =
        {
            new VertexPositionColor(new Vector2(-0.75f, 0.75f), RgbaFloat.Red),
            new VertexPositionColor(new Vector2(0.75f, 0.75f), RgbaFloat.Green),
            new VertexPositionColor(new Vector2(-0.75f, -0.75f), RgbaFloat.Blue),
            new VertexPositionColor(new Vector2(0.75f, -0.75f), RgbaFloat.Yellow)
        };

        ushort[] quadIndices = { 0, 1, 2, 3 };

        VertexBuffer = factory.CreateBuffer(new BufferDescription(4 * VertexPositionColor.SizeInBytes, BufferUsage.VertexBuffer));
        IndexBuffer = factory.CreateBuffer(new BufferDescription(4 * sizeof(ushort), BufferUsage.IndexBuffer));

        GraphicsDevice.UpdateBuffer(VertexBuffer, 0, quadVertices);
        GraphicsDevice.UpdateBuffer(IndexBuffer, 0, quadIndices);

        VertexLayoutDescription vertexLayout = new VertexLayoutDescription(
            new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
            new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));

        ShaderDescription vertexShaderDesc = new ShaderDescription(
            ShaderStages.Vertex,
            Encoding.UTF8.GetBytes(VertexCode),
            "main");
        ShaderDescription fragmentShaderDesc = new ShaderDescription(
            ShaderStages.Fragment,
            Encoding.UTF8.GetBytes(FragmentCode),
            "main");

        Shaders = factory.CreateFromSpirv(vertexShaderDesc, fragmentShaderDesc);

        GraphicsPipelineDescription pipelineDescription = new GraphicsPipelineDescription();
        pipelineDescription.BlendState = BlendStateDescription.SingleOverrideBlend;

        pipelineDescription.DepthStencilState = new DepthStencilStateDescription(
            depthTestEnabled: true,
            depthWriteEnabled: true,
            comparisonKind: ComparisonKind.LessEqual);

        pipelineDescription.RasterizerState = new RasterizerStateDescription(
            cullMode: FaceCullMode.Back,
            fillMode: PolygonFillMode.Solid,
            frontFace: FrontFace.Clockwise,
            depthClipEnabled: true,
            scissorTestEnabled: false);

        pipelineDescription.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
        pipelineDescription.ResourceLayouts = Array.Empty<ResourceLayout>();

        pipelineDescription.ShaderSet = new ShaderSetDescription(
            vertexLayouts: new VertexLayoutDescription[] { vertexLayout },
            shaders: Shaders);

        pipelineDescription.Outputs = Swapchain.Framebuffer.OutputDescription;
        Pipeline = factory.CreateGraphicsPipeline(pipelineDescription);

        CommandList = factory.CreateCommandList();
    }
    
    public void Initialize()
    {
        RenderThread = new Thread(RenderLoop) { IsBackground = true };
        RenderThread.Start();
    }
    
    private void RenderLoop()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        long previousTime = stopwatch.ElapsedMilliseconds;
        while (!Exit)
        {
            long currentTime = stopwatch.ElapsedMilliseconds;
            Window.PumpEvents();
            Console.WriteLine(Window.MouseDelta);
            Draw();
            long deltaTime = currentTime - previousTime;
            previousTime = currentTime;
        }
    }

    public void Resize(uint w, uint h)
    {
        if(Swapchain != null)
            Swapchain.Resize(w, h);
    }

    private void Draw()
    {
        CommandList.Begin();
        CommandList.SetFramebuffer(Swapchain.Framebuffer);
        CommandList.ClearColorTarget(0, RgbaFloat.Black);
        
        CommandList.SetVertexBuffer(0, VertexBuffer);
        CommandList.SetIndexBuffer(IndexBuffer, IndexFormat.UInt16);
        CommandList.SetPipeline(Pipeline);
        CommandList.DrawIndexed(4, 1, 0, 0, 0);
        CommandList.End();
        
        GraphicsDevice.SubmitCommands(CommandList);
        GraphicsDevice.SwapBuffers(Swapchain);
    }

    public void Dispose()
    {
        Exit = true;
        RenderThread.Join();
        
        Window.Close();
        Swapchain.Dispose();
        GraphicsDevice.Dispose();
        CommandList.Dispose();
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
        Pipeline.Dispose();
        foreach (var shader in Shaders)
            shader.Dispose();
    }

    public void Update(string[] modelPaths)
    {
    }
}

struct VertexPositionColor
{
    public Vector2 Position; // This is the position, in normalized device coordinates.
    public RgbaFloat Color; // This is the color of the vertex.
    public VertexPositionColor(Vector2 position, RgbaFloat color)
    {
        Position = position;
        Color = color;
    }
    public const uint SizeInBytes = 24;
}