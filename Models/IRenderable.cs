using System.Numerics;

namespace UniversalUmap.Rendering.Models;

public interface IRenderable
{
    public void Render(Plane[] frustumPlanes);
}