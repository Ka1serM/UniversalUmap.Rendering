using CommunityToolkit.Mvvm.ComponentModel;

namespace UniversalUmap.Rendering.Models;

public partial class AutoTextureItem : ObservableObject
{
    [ObservableProperty] private string parameter;
    [ObservableProperty] private string regex;
    [ObservableProperty] private string blacklist;
    [ObservableProperty] private bool r;
    [ObservableProperty] private bool g;
    [ObservableProperty] private bool b;
    [ObservableProperty] private bool a;

    public AutoTextureItem(string parameter, string regex, string blacklist,bool r, bool g, bool b, bool a)
    {
        Parameter = parameter;
        Regex = regex;
        Blacklist = blacklist;
        R = r;
        G = g;
        B = b;
        A = a;
    }
}