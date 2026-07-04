using System;

namespace UniversalUmap.Rendering.Inspector;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class DetailAttribute : Attribute
{
    public DetailAttribute(string? label = null)
    {
        Label = label;
    }

    public string? Label { get; }

    public string Group { get; set; } = "General";

    public int Order { get; set; }

    public double Min { get; set; } = double.NaN;

    public double Max { get; set; } = double.NaN;

    public double Step { get; set; } = double.NaN;

    public string? Format { get; set; }

    public string? Unit { get; set; }

    public bool ReadOnly { get; set; }

    /// <summary>Marks a Vector2/Vector3/Vector4 property as a color swatch instead of a set of number fields.</summary>
    public bool IsColor { get; set; }

    public bool HasRange => !double.IsNaN(Min) && !double.IsNaN(Max);
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class DetailRefAttribute : Attribute
{
    public DetailRefAttribute(string? label = null)
    {
        Label = label;
    }

    public string? Label { get; }

    public int Order { get; set; }
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class DetailInlineAttribute : Attribute
{
}
