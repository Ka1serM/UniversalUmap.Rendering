using System.Collections.Generic;

namespace UniversalUmap.Rendering.Inspector;

/// <summary>Implemented by inspectables whose detail groups can't be described with [Detail] attributes (e.g. a runtime Unreal property bag).</summary>
public interface IDynamicDetails
{
    IEnumerable<DetailGroup> BuildDynamicGroups();
}
