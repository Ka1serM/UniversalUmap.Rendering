using System.ComponentModel;
using System.Runtime.CompilerServices;
using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.ViewModels;

public sealed class OutlinerItem : INotifyPropertyChanged
{
    private bool isSelected;

    public int InstanceIndex { get; }
    public string Name { get; }
    public string? Subtitle { get; }
    public SceneHierarchyNodeKind Kind { get; }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
                return;

            isSelected = value;
            OnPropertyChanged();
        }
    }

    public OutlinerItem(int instanceIndex, string name, string? subtitle, SceneHierarchyNodeKind kind)
    {
        InstanceIndex = instanceIndex;
        Name = name;
        Subtitle = subtitle;
        Kind = kind;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
