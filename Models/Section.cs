namespace UniversalUmap.Rendering.Models;

public class Section
{
    public readonly int MaterialIndex;
    public readonly uint IndexCount;
    public readonly uint FirstIndex;
    
    public Section(int materialIndex, int firstIndex, int faceCount)
    {
        MaterialIndex = materialIndex;
        FirstIndex = (uint)firstIndex;
        IndexCount = (uint)faceCount * 3;
    }
}