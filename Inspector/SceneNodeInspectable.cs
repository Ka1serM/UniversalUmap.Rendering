using System.Collections.Generic;
using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.Inspector;

/// <summary>Wraps an outliner SceneHierarchyNode for the Details panel; the node's live UObject is only walked into DetailGroups when this is actually selected.</summary>
public sealed class SceneNodeInspectable(SceneHierarchyNode node) : IInspectable, IDynamicDetails
{
    public string InspectorTitle => node.Name;

    public IEnumerable<DetailGroup> BuildDynamicGroups() =>
        node.Source is null ? [] : UObjectDetails.Build(node.Source);
}
