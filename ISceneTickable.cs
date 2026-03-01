using Avalonia;

namespace UniversalUmap.Rendering;

public interface ISceneTickable
{
    void Tick(Scene scene, PixelSize renderSize, float deltaTimeSeconds);
}
