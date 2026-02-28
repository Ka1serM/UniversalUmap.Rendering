using System;

namespace UniversalUmap.Rendering;

internal interface IRaytracer : IDisposable
{
    ImageResource OutputColor { get; }
    void Render(ImageResource image);
}
