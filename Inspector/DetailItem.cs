using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace UniversalUmap.Rendering.Inspector;

public sealed class DetailItem : INotifyPropertyChanged, IDisposable
{
    private readonly object target;
    private readonly PropertyInfo property;
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

    public string Label { get; }
    public string Group { get; }
    public int Order { get; }
    public string? Unit { get; }
    public string? Format { get; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }
    public DetailEditorKind EditorKind { get; }
    public IReadOnlyList<object> EnumValues { get; } = Array.Empty<object>();

    public bool IsSlider => EditorKind == DetailEditorKind.Slider;
    public bool IsNumber => EditorKind == DetailEditorKind.Number;
    public bool IsToggle => EditorKind == DetailEditorKind.Toggle;
    public bool IsEnum => EditorKind == DetailEditorKind.Enum;
    public bool IsText => EditorKind == DetailEditorKind.Text;
    public bool IsReadOnly => EditorKind == DetailEditorKind.ReadOnlyText;

    public bool IsTextEditable => EditorKind is DetailEditorKind.Text or DetailEditorKind.Number;

    private object? RawValue => property.GetValue(target);

    public double DoubleValue
    {
        get
        {
            try { return Convert.ToDouble(RawValue ?? 0d, CultureInfo.InvariantCulture); }
            catch { return 0d; }
        }
        set
        {
            if (!property.CanWrite)
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
            if (!property.CanWrite)
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
            if (!property.CanWrite || value is null)
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
            if (!property.CanWrite)
                return;
            try
            {
                property.SetValue(target, Convert.ChangeType(value, valueType, CultureInfo.InvariantCulture));
                RaiseValueChanged();
            }
            catch { }
        }
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
        if (!writable)
            return DetailEditorKind.ReadOnlyText;
        if (type == typeof(bool))
            return DetailEditorKind.Toggle;
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

    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == property.Name)
            RaiseValueChanged();
    }

    private void RaiseValueChanged()
    {
        OnPropertyChanged(nameof(DoubleValue));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(EnumValue));
        OnPropertyChanged(nameof(TextValue));
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
