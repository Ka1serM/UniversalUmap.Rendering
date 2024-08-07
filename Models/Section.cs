namespace UniversalUmap.Rendering.Models;

public class Section
{
    public readonly uint MaterialIndex;
    public readonly uint IndexCount;
    public readonly uint FirstIndex;
    
    public Section(int materialIndex, int faceCount, int firstIndex)
    {
        MaterialIndex = (uint)materialIndex;
        IndexCount = (uint)faceCount * 3;
        FirstIndex = (uint)firstIndex;
    }
}