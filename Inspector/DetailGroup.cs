using System.Collections.Generic;

namespace UniversalUmap.Rendering.Inspector;

public sealed class DetailGroup
{
    public DetailGroup(string name, IReadOnlyList<object> entries)
    {
        Name = name;
        Entries = entries;
    }

    public string Name { get; }
    public IReadOnlyList<object> Entries { get; }
}
