namespace UniversalUmap.Rendering.Scenes;

internal interface IGpuSnapshot<out TGpu>
{
    TGpu ToStruct();
}
