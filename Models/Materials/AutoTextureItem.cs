using CommunityToolkit.Mvvm.ComponentModel;

namespace UniversalUmap.Rendering.Models.Materials;

public partial class AutoTextureItem : ObservableObject
{
    [ObservableProperty] private string parameter =  "";
    [ObservableProperty] private string name =  "";
    [ObservableProperty] private string blacklist =  "";
    [ObservableProperty] private bool r = true;
    [ObservableProperty] private bool g = true;
    [ObservableProperty] private bool b = true;
    [ObservableProperty] private bool a = false;

    public AutoTextureItem(string parameter, string name, string blacklist,bool r, bool g, bool b, bool a)
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