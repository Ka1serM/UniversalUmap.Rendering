using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using CUE4Parse.UE4.Objects.Core.Math;

namespace UniversalUmap.Rendering.Inspector;

/// <summary>One labeled numeric field within a DetailItem.Vector editor (e.g. X/Y/Z of an FVector), bound back to the owning item's raw value on edit.</summary>
public sealed class VectorComponent
{
    private readonly DetailItem owner;
    private readonly int index;
    private readonly double rawValue;

    public VectorComponent(DetailItem owner, int index, string label, double rawValue)
    {
        this.owner = owner;
        this.index = index;
        Label = label;
        this.rawValue = rawValue;
    }

    public string Label { get; }
    public bool IsReadOnly => owner.IsReadOnlyValue;

    public decimal? Value
    {
        get => (decimal)rawValue;
        set
        {
            if (IsReadOnly || value is null)
                return;
            owner.SetVectorComponent(index, (double)value.Value);
        }
    }
}

public sealed class DetailItem : INotifyPropertyChanged, IDisposable
{
    private static readonly Type[] VectorTypes =
    [
        typeof(FVector), typeof(FVector2D), typeof(FVector4), typeof(FRotator),
        typeof(Vector2), typeof(Vector3), typeof(Vector4)
    ];

    private readonly object? target;
    private readonly PropertyInfo? property;
    private readonly object? staticValue;
    private readonly Type valueType;
    private readonly INotifyPropertyChanged? observableTarget;

    public DetailItem(object target, PropertyInfo property, DetailAttribute attribute)
    {
        this.target = target;
        this.property = property;
        valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        Label = attribute.Label ?? property.Name;
        Group = attribute.Group;
        Order = attribute.Order;
        Unit = attribute.Unit;
        Format = attribute.Format;
        Min = double.IsNaN(attribute.Min) ? 0d : attribute.Min;
        Max = double.IsNaN(attribute.Max) ? 1d : attribute.Max;
        Step = double.IsNaN(attribute.Step) ? Math.Max((Max - Min) / 100d, 0.0001d) : attribute.Step;

        var writable = property.CanWrite && !attribute.ReadOnly;
        EditorKind = ResolveEditorKind(valueType, attribute, writable);

        if (EditorKind == DetailEditorKind.Enum)
            EnumValues = Enum.GetValues(valueType).Cast<object>().ToArray();

        if (target is INotifyPropertyChanged observable)
        {
            observableTarget = observable;
            observable.PropertyChanged += OnTargetPropertyChanged;
        }
    }

    /// <summary>Item for values that don't come from a CLR property (e.g. a dynamic Unreal property bag entry). Editor kind is inferred from the value's runtime type; always read-only.</summary>
    public DetailItem(string label, string group, int order, object? value, string? unit = null, bool hideLabel = false)
    {
        staticValue = value;
        valueType = value?.GetType() ?? typeof(string);

        Label = label;
        Group = group;
        Order = order;
        Unit = unit;
        Min = 0d;
        Max = 1d;
        Step = 0.01d;
        HideLabel = hideLabel;

        EditorKind = valueType == typeof(bool) ? DetailEditorKind.Toggle
            : valueType == typeof(ObjectReferenceValue) ? DetailEditorKind.ObjectReference
            : IsColorType(valueType) ? DetailEditorKind.Color
            : IsVectorType(valueType) ? DetailEditorKind.Vector
            : valueType.IsEnum ? DetailEditorKind.Enum
            : IsNumeric(valueType) ? DetailEditorKind.Number
            : value is string s && s.Contains("::", StringComparison.Ordinal) ? DetailEditorKind.Enum
            : DetailEditorKind.ReadOnlyText;

        if (EditorKind == DetailEditorKind.Enum && valueType.IsEnum)
            EnumValues = Enum.GetValues(valueType).Cast<object>().ToArray();
    }

    public string Label { get; }
    public string Group { get; }
    public int Order { get; }
    public string? Unit { get; }
    public string? Format { get; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }
    public DetailEditorKind EditorKind { get; }
    public bool HideLabel { get; }
    public IReadOnlyList<object> EnumValues { get; internal set; } = Array.Empty<object>();

    public bool IsSlider => EditorKind == DetailEditorKind.Slider;
    public bool IsNumber => EditorKind == DetailEditorKind.Number;
    public bool IsToggle => EditorKind == DetailEditorKind.Toggle;
    public bool IsEnum => EditorKind == DetailEditorKind.Enum;
    public bool IsText => EditorKind == DetailEditorKind.Text;
    public bool IsReadOnly => EditorKind == DetailEditorKind.ReadOnlyText;
    public bool IsColor => EditorKind == DetailEditorKind.Color;
    public bool IsVector => EditorKind == DetailEditorKind.Vector;
    public bool IsObjectReference => EditorKind == DetailEditorKind.ObjectReference;

    /// <summary>True when this item has no backing writable CLR property - used to disable interactive editors (checkbox, color swatch, vector fields) for dynamic Unreal property values.</summary>
    public bool IsReadOnlyValue => property is not { CanWrite: true };

    private object? RawValue => property is not null ? property.GetValue(target) : staticValue;

    public double DoubleValue
    {
        get
        {
            try { return Convert.ToDouble(RawValue ?? 0d, CultureInfo.InvariantCulture); }
            catch { return 0d; }
        }
        set
        {
            if (property is not { CanWrite: true })
                return;
            try
            {
                property.SetValue(target, Convert.ChangeType(value, valueType, CultureInfo.InvariantCulture));
                RaiseValueChanged();
            }
            catch { }
        }
    }

    public bool BoolValue
    {
        get => RawValue is true;
        set
        {
            if (property is not { CanWrite: true })
                return;
            property.SetValue(target, value);
            RaiseValueChanged();
        }
    }

    public object? EnumValue
    {
        get => RawValue;
        set
        {
            if (property is not { CanWrite: true } || value is null)
                return;
            property.SetValue(target, value);
            RaiseValueChanged();
        }
    }

    public string TextValue
    {
        get => RawValue?.ToString() ?? string.Empty;
        set
        {
            if (property is not { CanWrite: true })
                return;
            try
            {
                property.SetValue(target, Convert.ChangeType(value, valueType, CultureInfo.InvariantCulture));
                RaiseValueChanged();
            }
            catch { }
        }
    }

    public Color ColorValue
    {
        get => ToAvaloniaColor(RawValue);
        set
        {
            if (property is not { CanWrite: true })
                return;
            var converted = FromAvaloniaColor(value, valueType);
            if (converted is null)
                return;
            property.SetValue(target, converted);
            RaiseValueChanged();
        }
    }

    public IReadOnlyList<VectorComponent> VectorComponents => ToVectorComponents(RawValue, this);

    public ObjectReferenceValue? ObjectReferenceValue => RawValue as ObjectReferenceValue;

    public object? GetRawValue() => RawValue;

    public void SetVectorComponent(int index, double value)
    {
        if (property is not { CanWrite: true })
            return;
        var updated = WithVectorComponent(RawValue, index, value);
        if (updated is null)
            return;
        property.SetValue(target, updated);
        RaiseValueChanged();
    }

    public string DisplayValue
    {
        get
        {
            var raw = RawValue;
            if (raw is null)
                return string.Empty;

            string text;
            if (raw is IFormattable formattable && !string.IsNullOrEmpty(Format))
                text = formattable.ToString(Format, CultureInfo.InvariantCulture);
            else
                text = raw.ToString() ?? string.Empty;

            return string.IsNullOrEmpty(Unit) ? text : $"{text} {Unit}";
        }
    }

    private static DetailEditorKind ResolveEditorKind(Type type, DetailAttribute attribute, bool writable)
    {
        if (type == typeof(bool))
            return DetailEditorKind.Toggle;
        if (attribute.IsColor && IsVectorType(type))
            return DetailEditorKind.Color;
        if (IsColorType(type))
            return DetailEditorKind.Color;
        if (IsVectorType(type))
            return DetailEditorKind.Vector;
        if (!writable)
            return DetailEditorKind.ReadOnlyText;
        if (type.IsEnum)
            return DetailEditorKind.Enum;
        if (IsNumeric(type))
            return attribute.HasRange ? DetailEditorKind.Slider : DetailEditorKind.Number;
        if (type == typeof(string))
            return DetailEditorKind.Text;
        return DetailEditorKind.ReadOnlyText;
    }

    private static bool IsNumeric(Type type) =>
        type == typeof(float) || type == typeof(double) || type == typeof(int) ||
        type == typeof(uint) || type == typeof(long) || type == typeof(short) ||
        type == typeof(byte) || type == typeof(decimal);

    private static bool IsVectorType(Type type) => Array.IndexOf(VectorTypes, type) >= 0;

    private static bool IsColorType(Type type) =>
        type == typeof(FColor) || type == typeof(FLinearColor) || type == typeof(Color);

    private static IReadOnlyList<VectorComponent> ToVectorComponents(object? raw, DetailItem owner) => raw switch
    {
        FVector v => [new VectorComponent(owner, 0, "X", v.X), new VectorComponent(owner, 1, "Y", v.Y), new VectorComponent(owner, 2, "Z", v.Z)],
        FVector2D v => [new VectorComponent(owner, 0, "X", v.X), new VectorComponent(owner, 1, "Y", v.Y)],
        FVector4 v => [new VectorComponent(owner, 0, "X", v.X), new VectorComponent(owner, 1, "Y", v.Y), new VectorComponent(owner, 2, "Z", v.Z), new VectorComponent(owner, 3, "W", v.W)],
        FRotator r => [new VectorComponent(owner, 0, "Pitch", r.Pitch), new VectorComponent(owner, 1, "Yaw", r.Yaw), new VectorComponent(owner, 2, "Roll", r.Roll)],
        Vector2 v => [new VectorComponent(owner, 0, "X", v.X), new VectorComponent(owner, 1, "Y", v.Y)],
        Vector3 v => [new VectorComponent(owner, 0, "X", v.X), new VectorComponent(owner, 1, "Y", v.Y), new VectorComponent(owner, 2, "Z", v.Z)],
        Vector4 v => [new VectorComponent(owner, 0, "X", v.X), new VectorComponent(owner, 1, "Y", v.Y), new VectorComponent(owner, 2, "Z", v.Z), new VectorComponent(owner, 3, "W", v.W)],
        _ => []
    };

    private static object? WithVectorComponent(object? raw, int index, double value)
    {
        var v = (float)value;
        return raw switch
        {
            FVector p => index switch { 0 => new FVector(v, p.Y, p.Z), 1 => new FVector(p.X, v, p.Z), 2 => new FVector(p.X, p.Y, v), _ => null },
            FVector2D p => index switch { 0 => new FVector2D(v, p.Y), 1 => new FVector2D(p.X, v), _ => null },
            FVector4 p => index switch { 0 => new FVector4(v, p.Y, p.Z, p.W), 1 => new FVector4(p.X, v, p.Z, p.W), 2 => new FVector4(p.X, p.Y, v, p.W), 3 => new FVector4(p.X, p.Y, p.Z, v), _ => null },
            FRotator p => index switch { 0 => new FRotator(v, p.Yaw, p.Roll), 1 => new FRotator(p.Pitch, v, p.Roll), 2 => new FRotator(p.Pitch, p.Yaw, v), _ => null },
            Vector2 p => index switch { 0 => new Vector2(v, p.Y), 1 => new Vector2(p.X, v), _ => null },
            Vector3 p => index switch { 0 => new Vector3(v, p.Y, p.Z), 1 => new Vector3(p.X, v, p.Z), 2 => new Vector3(p.X, p.Y, v), _ => null },
            Vector4 p => index switch { 0 => new Vector4(v, p.Y, p.Z, p.W), 1 => new Vector4(p.X, v, p.Z, p.W), 2 => new Vector4(p.X, p.Y, v, p.W), 3 => new Vector4(p.X, p.Y, p.Z, v), _ => null },
            _ => null
        };
    }

    private static Color ToAvaloniaColor(object? raw) => raw switch
    {
        FColor c => Color.FromArgb(c.A, c.R, c.G, c.B),
        FLinearColor c => Color.FromArgb(ByteClamp(c.A), ByteClamp(c.R), ByteClamp(c.G), ByteClamp(c.B)),
        Vector3 v => Color.FromRgb(ByteClamp(v.X), ByteClamp(v.Y), ByteClamp(v.Z)),
        Color c => c,
        _ => Colors.Transparent
    };

    private static object? FromAvaloniaColor(Color color, Type targetType)
    {
        if (targetType == typeof(FColor))
            return new FColor(color.R, color.G, color.B, color.A);
        if (targetType == typeof(FLinearColor))
            return new FLinearColor(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        if (targetType == typeof(Vector3))
            return new Vector3(color.R / 255f, color.G / 255f, color.B / 255f);
        if (targetType == typeof(Color))
            return color;
        return null;
    }

    private static byte ByteClamp(float normalized) => (byte)Math.Clamp(normalized * 255f, 0f, 255f);

    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == property?.Name)
            RaiseValueChanged();
    }

    private void RaiseValueChanged()
    {
        OnPropertyChanged(nameof(DoubleValue));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(EnumValue));
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(ColorValue));
        OnPropertyChanged(nameof(VectorComponents));
        OnPropertyChanged(nameof(DisplayValue));
    }

    public void Dispose()
    {
        if (observableTarget is not null)
            observableTarget.PropertyChanged -= OnTargetPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
