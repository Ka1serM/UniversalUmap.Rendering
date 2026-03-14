using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UniversalUmap.Rendering.Scenes;

public enum SceneHierarchyNodeKind
{
    Unknown = 0,
    Actor,
    StaticMesh,
    InstancedStaticMesh,
    SkeletalMesh,
    Light,
    AtmosphereOrSky,
    CloudOrFog,
    Volume,
    Decal,
    Reflection,
    PostProcess,
    Camera
}

public sealed class SceneHierarchyNode : INotifyPropertyChanged
{
    public string Name { get; }
    public string Type { get; }
    public string OwnerName { get; }
    public SceneHierarchyNodeKind Kind { get; }
    public int InstanceCount { get; }
    public bool IsActor { get; }
    public List<SceneHierarchyNode> Children { get; } = [];
    public bool HasChildren => Children.Count > 0;

    public bool HasInstanceCount => InstanceCount > 1;
    public string InstanceCountText => HasInstanceCount ? $"x{InstanceCount:N0}" : string.Empty;
    public string Subtitle => string.IsNullOrWhiteSpace(OwnerName) ? Type : $"{Type} - {OwnerName}";
    public int InstanceIndexStart { get; private set; } = -1;
    public int InstanceIndexCount { get; private set; }
    private bool isExpanded;
    private bool isSelected;

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (isExpanded == value)
                return;

            isExpanded = value;
            OnPropertyChanged();
        }
    }

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

    public event PropertyChangedEventHandler? PropertyChanged;

    public SceneHierarchyNode(
        string name,
        string type,
        string ownerName,
        SceneHierarchyNodeKind kind,
        bool isActor = false,
        int instanceCount = 1)
    {
        Name = name;
        Type = type;
        OwnerName = ownerName;
        Kind = kind;
        IsActor = isActor;
        InstanceCount = Math.Max(1, instanceCount);
    }

    public void SetInstanceRange(int startIndex, int count)
    {
        InstanceIndexStart = startIndex;
        InstanceIndexCount = Math.Max(0, count);
    }

    public bool ContainsInstanceIndex(int instanceIndex)
    {
        if (instanceIndex < 0)
            return false;

        if (MatchesInstanceIndex(instanceIndex))
            return true;

        foreach (var child in Children)
        {
            if (child.ContainsInstanceIndex(instanceIndex))
                return true;
        }

        return false;
    }

    public bool MatchesInstanceIndex(int instanceIndex)
    {
        return instanceIndex >= 0 &&
               InstanceIndexCount > 0 &&
               instanceIndex >= InstanceIndexStart &&
               instanceIndex < InstanceIndexStart + InstanceIndexCount;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
