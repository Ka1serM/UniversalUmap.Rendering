using System;
using System.Globalization;
using Avalonia.Data.Converters;
using UniversalUmap.Rendering.Scenes;

namespace UniversalUmap.Rendering.Converters;

public sealed class RenderPixelSizeDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not RenderPixelSize pixelSize)
            return string.Empty;

        var scale = (int)pixelSize / 100.0;
        return $"x{scale:0.0#}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value;
    }
}
