using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CUE4Parse.UE4.Objects.Core.Math;

namespace UniversalUmap.Rendering.Scenes;

public readonly record struct SceneHierarchyHandle(int Id)
{
    public static SceneHierarchyHandle Invalid { get; } = new(-1);
    public bool IsValid => Id >= 0;
}

public enum SceneNodeRole
{
    World,
    Level,
    Actor,
    Component
}

public enum SceneHierarchyNodeKind
{
    Unknown = 0,
    World,
    Level,
    Actor,
    SceneComponent,
    StaticMesh,
    InstancedStaticMesh,
    SkeletalMesh,
    ChildActor,
    Light,
    AtmosphereOrSky,
    CloudOrFog,
    Volume,
    Decal,
    Reflection,
    PostProcess,
    Camera,
    Arrow,
    Billboard,
    Text,
    Spline
}

public readonly record struct SceneInstanceRange(int Start, int Count);

public sealed class SceneHierarchyNode : INotifyPropertyChanged
{
    private List<SceneInstanceRange>? extraInstanceRanges;

    public int Id { get; }
    public int ParentId { get; }
    public string Name { get; }
    public string Type { get; }
    public string OwnerName { get; }
    public string SourcePath { get; }
    public string? AssetPath { get; }
    public SceneNodeRole Role { get; }
    public SceneHierarchyNodeKind Kind { get; }
    public int InstanceCount { get; }
    public bool IsActor { get; }
    public List<SceneHierarchyNode> Children { get; } = [];
    public bool HasChildren => Children.Count > 0;
    public FTransform LocalTransform { get; }
    public FTransform WorldTransform { get; }

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
        : this(-1, -1, name, type, ownerName, string.Empty, null, isActor ? SceneNodeRole.Actor : SceneNodeRole.Component,
            kind, FTransform.Identity, FTransform.Identity, isActor, instanceCount)
    {
    }

    public SceneHierarchyNode(
        int id,
        int parentId,
        string name,
        string type,
        string ownerName,
        string sourcePath,
        string? assetPath,
        SceneNodeRole role,
        SceneHierarchyNodeKind kind,
        FTransform localTransform,
        FTransform worldTransform,
        bool isActor = false,
        int instanceCount = 1)
    {
        Id = id;
        ParentId = parentId;
        Name = name;
        Type = type;
        OwnerName = ownerName;
        SourcePath = sourcePath;
        AssetPath = assetPath;
        Role = role;
        Kind = kind;
        IsActor = isActor;
        InstanceCount = Math.Max(1, instanceCount);
        LocalTransform = localTransform;
        WorldTransform = worldTransform;
    }

    public void SetInstanceRange(int startIndex, int count)
    {
        InstanceIndexStart = startIndex;
        InstanceIndexCount = Math.Max(0, count);
    }

    public void AddInstanceRange(int startIndex, int count)
    {
        if (count <= 0)
            return;

        if (InstanceIndexCount <= 0)
        {
            SetInstanceRange(startIndex, count);
            return;
        }

        extraInstanceRanges ??= [];
        extraInstanceRanges.Add(new SceneInstanceRange(startIndex, count));
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
        if (instanceIndex < 0)
            return false;

        if (InstanceIndexCount > 0 &&
            instanceIndex >= InstanceIndexStart &&
            instanceIndex < InstanceIndexStart + InstanceIndexCount)
            return true;

        if (extraInstanceRanges is null)
            return false;

        foreach (var range in extraInstanceRanges)
        {
            if (range.Count > 0 && instanceIndex >= range.Start && instanceIndex < range.Start + range.Count)
                return true;
        }

        return false;
    }

    public int FirstInstanceIndexOrDefault()
    {
        if (InstanceIndexCount > 0)
            return InstanceIndexStart;

        foreach (var child in Children)
        {
            var childIndex = child.FirstInstanceIndexOrDefault();
            if (childIndex >= 0)
                return childIndex;
        }

        return -1;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
