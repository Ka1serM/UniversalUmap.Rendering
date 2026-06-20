using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UniversalUmap.Rendering.Inspector;

public static class InspectorBuilder
{
    private const int MaxRefDepth = 3;
    private const BindingFlags PropertyFlags = BindingFlags.Public | BindingFlags.Instance;

    public static IReadOnlyList<DetailGroup> Build(object? root)
    {
        return root is null ? [] : BuildGroups(root, depth: 0);
    }

    private static List<DetailGroup> BuildGroups(object target, int depth)
    {
        var result = new List<DetailGroup>();

        var collected = new List<(string Group, int Order, DetailItem Item)>();
        CollectDetails(target, collected, inlineDepth: 0);

        foreach (var grouped in collected
                     .Select((entry, index) => (entry, index))
                     .GroupBy(x => x.entry.Group))
        {
            var items = grouped
                .OrderBy(x => x.entry.Order)
                .ThenBy(x => x.index)
                .Select(x => x.entry.Item)
                .ToList();
            result.Add(new DetailGroup(grouped.Key, items));
        }

        if (depth >= MaxRefDepth)
            return result;

        foreach (var (property, refAttribute) in GetMembers<DetailRefAttribute>(target).OrderBy(x => x.Attribute.Order))
        {
            var value = property.GetValue(target);
            if (value is null)
                continue;

            var label = refAttribute.Label ?? property.Name;

            if (value is IEnumerable enumerable and not string)
            {
                var index = 0;
                foreach (var element in enumerable)
                {
                    if (element is not null)
                        AppendRefGroups(element, ElementTitle(element, label, index), result, depth + 1);
                    index++;
                }
            }
            else
            {
                AppendRefGroups(value, (value as IInspectable)?.InspectorTitle ?? label, result, depth + 1);
            }
        }

        return result;
    }

    private static void AppendRefGroups(object refTarget, string title, List<DetailGroup> result, int depth)
    {
        foreach (var childGroup in BuildGroups(refTarget, depth))
        {
            var name = string.IsNullOrEmpty(childGroup.Name) || childGroup.Name == "General"
                ? title
                : $"{title} · {childGroup.Name}";
            result.Add(new DetailGroup(name, childGroup.Items));
        }
    }

    private static void CollectDetails(object target, List<(string, int, DetailItem)> collected, int inlineDepth)
    {
        foreach (var (property, attribute) in GetMembers<DetailAttribute>(target))
            collected.Add((attribute.Group, attribute.Order, new DetailItem(target, property, attribute)));

        if (inlineDepth >= MaxRefDepth)
            return;

        foreach (var property in target.GetType().GetProperties(PropertyFlags))
        {
            if (property.GetCustomAttribute<DetailInlineAttribute>() is null)
                continue;

            var value = property.GetValue(target);
            if (value is not null)
                CollectDetails(value, collected, inlineDepth + 1);
        }
    }

    private static IEnumerable<(PropertyInfo Property, TAttribute Attribute)> GetMembers<TAttribute>(object target)
        where TAttribute : System.Attribute
    {
        foreach (var property in target.GetType().GetProperties(PropertyFlags))
        {
            var attribute = property.GetCustomAttribute<TAttribute>();
            if (attribute is not null && property.GetIndexParameters().Length == 0)
                yield return (property, attribute);
        }
    }

    private static string ElementTitle(object element, string label, int index) =>
        (element as IInspectable)?.InspectorTitle ?? $"{label} {index}";
}
