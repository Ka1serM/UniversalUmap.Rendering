using System;
using System.Collections.Generic;

namespace UniversalUmap.Rendering;

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

public sealed class SceneHierarchyNode
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
}
