namespace UniversalUmap.Rendering;

internal interface IGpuSnapshot<out TGpu>
{
    TGpu ToStruct();
}
