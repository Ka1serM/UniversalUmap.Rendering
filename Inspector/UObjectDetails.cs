using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.Utils;

namespace UniversalUmap.Rendering.Inspector;

public static class UObjectDetails
{
    private const int MaxStructDepth = 3;
    private const int MaxArrayElements = 50;

    private static readonly HashSet<string> HiddenProperties = new(StringComparer.Ordinal)
    {
        "StreamingTextureData",
    };

    public static IEnumerable<DetailGroup> Build(UObject obj)
    {
        return BuildEntries(obj.Properties, 0);
    }

    private static IEnumerable<DetailGroup> BuildEntries(List<FPropertyTag> tags, int depth)
    {
        foreach (var tag in tags)
        {
            if (tag.Tag is null || HiddenProperties.Contains(tag.Name.Text))
                continue;

            var value = Unwrap(tag.Tag.GenericValue);

            if (value is UScriptArray array)
            {
                yield return BuildArrayGroup(tag.Name.Text, array, depth);
                continue;
            }

            if (depth < MaxStructDepth && value is FStructFallback nestedStruct && nestedStruct.Properties.Count > 0)
            {
                yield return new DetailGroup(tag.Name.Text, BuildEntries(nestedStruct.Properties, depth + 1).ToList<object>());
                continue;
            }

            var label = tag.Name.Text;
            if (value is bool && label.Length > 1 && label[0] == 'b')
                label = label.Substring(1);

            var normalized = NormalizeLeaf(value);

            if (normalized is string s && tag.Tag is EnumProperty && tag.TagData?.EnumName is { } enumName && !s.Contains("::"))
                normalized = $"{enumName}::{s}";

            yield return new DetailGroup("", new List<object> { new DetailItem(label, "", 0, normalized) });
        }
    }

    private static DetailGroup BuildArrayGroup(string propertyName, UScriptArray array, int depth)
    {
        var entries = new List<object>();
        var elementCount = Math.Min(array.Properties.Count, MaxArrayElements);

        var isOverrideMaterials = propertyName == "OverrideMaterials";

        for (var i = 0; i < elementCount; i++)
        {
            var value = Unwrap(array.Properties[i]?.GenericValue);
            var label = isOverrideMaterials ? $"Element {i + 1}" : propertyName;

            if (depth < MaxStructDepth && value is FStructFallback nestedStruct && nestedStruct.Properties.Count > 0)
            {
                entries.Add(new DetailGroup(label, BuildEntries(nestedStruct.Properties, depth + 1).ToList<object>()));
                continue;
            }

            entries.Add(new DetailItem(label, propertyName, i, NormalizeLeaf(value)));
        }

        if (array.Properties.Count > elementCount)
            entries.Add(new DetailItem(isOverrideMaterials ? $"Element {elementCount + 1}" : propertyName, propertyName, elementCount, $"+{array.Properties.Count - elementCount} more"));

        var groupName = isOverrideMaterials ? "Materials" : propertyName;
        return new DetailGroup(groupName, entries);
    }

    private static object? Unwrap(object? value) => value is FScriptStruct scriptStruct ? scriptStruct.StructType : value;

    private static object? NormalizeLeaf(object? value)
    {
        if (value is null)
            return "None";
        if (value is FPackageIndex index)
            return index.TryLoad(out UObject resolved) ? ToObjectReference(resolved) : "None";
        if (value is FName name)
            return name.Text;
        if (value is IEnumerable enumerable and not string)
            return $"<{enumerable.Cast<object>().Count()} items>";
        return value;
    }

    private static ObjectReferenceValue ToObjectReference(UObject obj) =>
        new(obj.Name, obj.GetType().Name, obj.GetPathName());

    private static ObjectReferenceValue ToObjectReference(string assetPath) =>
        new(assetPath.SubstringAfterLast("/").SubstringAfterLast("."), "UTexture2D", assetPath);
}
