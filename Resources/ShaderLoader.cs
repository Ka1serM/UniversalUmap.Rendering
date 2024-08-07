using Avalonia.Platform;
using Veldrid;
using Veldrid.SPIRV;

namespace UniversalUmap.Rendering.Resources;

public static class ShaderLoader
{
    public static Shader[] Load(GraphicsDevice graphicsDevice, string name)
    {
        byte[] vertexBytes, fragmentBytes;
        using (Stream stream = AssetLoader.Open(new Uri($"avares://UniversalUmap.Rendering/Assets/Shaders/{name}.vert.spv")))
        {
            using (MemoryStream ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                vertexBytes = ms.ToArray();
            }
        }
        using (Stream stream = AssetLoader.Open(new Uri($"avares://UniversalUmap.Rendering/Assets/Shaders/{name}.frag.spv")))
        {
            using (MemoryStream ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                fragmentBytes = ms.ToArray();
            }
        }
        var vertexShaderDesc = new ShaderDescription(ShaderStages.Vertex, vertexBytes, "main");
        var fragmentShaderDesc = new ShaderDescription(ShaderStages.Fragment, fragmentBytes, "main");
        var shaders = graphicsDevice.ResourceFactory.CreateFromSpirv(vertexShaderDesc, fragmentShaderDesc);
        return shaders;
    }
}