using System.Numerics;
using System.Reflection;
using CUE4Parse_Conversion.Textures;
using CUE4Parse.UE4.Assets.Exports.Texture;
using SkiaSharp;
using Veldrid;
using PixelFormat = Veldrid.PixelFormat;

namespace UniversalUmap.Rendering.Models.Materials
{
    public class Texture : IDisposable
    {
        private bool IsDisposed;
        
        public Veldrid.Texture VeldridTexture { get; private set; }
        public ResourceSet ResourceSet { get; private set; }
        
    public Texture(GraphicsDevice graphicsDevice, ResourceLayout resourceLayout, UTexture texture)
    {
        try
        {
            var skBitmap = texture.Decode(RenderContext.TexturePlatform);
            InitializeTextureFromBitmap(graphicsDevice, resourceLayout, texture, skBitmap);
        }
        catch
        {
            InitializeTextureFromColor(graphicsDevice, resourceLayout, new Vector4(0.5f));
        }
    }
    
    public Texture(GraphicsDevice graphicsDevice, ResourceLayout resourceLayout, Vector4 color)
    {
        InitializeTextureFromColor(graphicsDevice, resourceLayout, color);
    }

    private void InitializeTextureFromBitmap(GraphicsDevice graphicsDevice, ResourceLayout resourceLayout, UTexture texture, SKBitmap bitmap)
    {
        VeldridTexture = graphicsDevice.ResourceFactory.CreateTexture(new TextureDescription
        {
            Width = (uint)bitmap.Width,
            Height = (uint)bitmap.Height,
            Depth = 1,
            MipLevels = 1, //TODO: Load all mips
            ArrayLayers = 1,
            Format = texture.SRGB ? PixelFormat.R8_G8_B8_A8_UNorm_SRgb : PixelFormat.R8_G8_B8_A8_UNorm,
            Type = TextureType.Texture2D,
            SampleCount = TextureSampleCount.Count1,
            Usage = TextureUsage.Sampled
        });
        graphicsDevice.UpdateTexture(VeldridTexture, bitmap.Bytes, 0, 0, 0, (uint)bitmap.Width, (uint)bitmap.Height, 1, 0, 0);
        ResourceSet = graphicsDevice.ResourceFactory.CreateResourceSet(new ResourceSetDescription(resourceLayout, VeldridTexture));
    }
    
    public Texture(GraphicsDevice graphicsDevice, ResourceLayout resourceLayout, string[] sourceNames)
    {
        var bitmaps = new SKBitmap[6];
        for (uint i = 0; i < bitmaps.Length; i++)
            bitmaps[i] = SKBitmap.Decode(Assembly.GetExecutingAssembly().GetManifestResourceStream($"UniversalUmap.Rendering.Assets.Textures.{sourceNames[i]}.png"));
        InitializeTextureCubeFromBitmaps(graphicsDevice, resourceLayout, bitmaps);
    }

    
    private void InitializeTextureCubeFromBitmaps(GraphicsDevice graphicsDevice, ResourceLayout resourceLayout, SKBitmap[] bitmaps)
    {
        VeldridTexture = graphicsDevice.ResourceFactory.CreateTexture(new TextureDescription
        {
            Width = (uint)bitmaps[0].Width,
            Height = (uint)bitmaps[0].Height,
            Depth = 1,
            MipLevels = 1,
            ArrayLayers = 1,
            Format = PixelFormat.B8_G8_R8_A8_UNorm_SRgb,
            Type = TextureType.Texture2D,
            SampleCount = TextureSampleCount.Count1,
            Usage = TextureUsage.Sampled | TextureUsage.Cubemap
        });
        for (uint i = 0; i < bitmaps.Length; i++)
            graphicsDevice.UpdateTexture(VeldridTexture, bitmaps[i].Bytes, 0, 0, 0, (uint)bitmaps[i].Width, (uint)bitmaps[i].Height, 1, 0, i);
        ResourceSet = graphicsDevice.ResourceFactory.CreateResourceSet(new ResourceSetDescription(resourceLayout, VeldridTexture));
    }
    
    

    private void InitializeTextureFromColor(GraphicsDevice graphicsDevice, ResourceLayout resourceLayout, Vector4 color)
    {
        var bytes = new byte[4];
        bytes[0] = (byte)(color.X * 255); // R
        bytes[1] = (byte)(color.Y * 255); // G
        bytes[2] = (byte)(color.Z * 255); // B
        bytes[3] = (byte)(color.W * 255); // A

        VeldridTexture = graphicsDevice.ResourceFactory.CreateTexture(new TextureDescription
        {
            Width = 1,
            Height = 1,
            Depth = 1,
            MipLevels = 1,
            ArrayLayers = 1,
            Format = PixelFormat.R8_G8_B8_A8_UNorm,
            Type = TextureType.Texture2D,
            SampleCount = TextureSampleCount.Count1,
            Usage = TextureUsage.Sampled
        });
        graphicsDevice.UpdateTexture(VeldridTexture, bytes, 0, 0, 0, 1, 1, 1, 0, 0);
        ResourceSet = graphicsDevice.ResourceFactory.CreateResourceSet(new ResourceSetDescription(resourceLayout, VeldridTexture));
    }
        
        public void Dispose()
        {
            if(IsDisposed)
                return;
            
            ResourceSet.Dispose();
            VeldridTexture.Dispose();

            IsDisposed = true;
        }
    }
}
