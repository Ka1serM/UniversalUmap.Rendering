using System.Collections.Generic;

namespace UniversalUmap.Rendering.Inspector;

public sealed class DetailGroup
{
    public DetailGroup(string name, IReadOnlyList<DetailItem> items)
    {
        Name = name;
        Items = items;
    }

    public string Name { get; }
    public IReadOnlyList<DetailItem> Items { get; }
}
