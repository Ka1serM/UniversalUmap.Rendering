namespace UniversalUmap.Rendering.Models;

public sealed class AutoTextureItem
{
    public string Parameter { get; set; }
    public string Name { get; set; }
    public string Blacklist { get; set; }
    public bool R { get; set; }
    public bool G { get; set; }
    public bool B { get; set; }
    public bool A { get; set; }

    public AutoTextureItem(
        string parameter,
        string name,
        string blacklist,
        bool r = true,
        bool g = true,
        bool b = true,
        bool a = false)
    {
        Parameter = parameter;
        Name = name;
        Blacklist = blacklist;
        R = r;
        G = g;
        B = b;
        A = a;
    }
}
