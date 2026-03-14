using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace UniversalUmap.Rendering.ViewModels;

public class SuppressibleObservableCollection<T> : ObservableCollection<T>
{
    private bool suppress;

    public void AddRangeSuppressed(IEnumerable<T> items)
    {
        if (items is null)
            return;

        suppress = true;
        foreach (var item in items)
            Add(item);

        suppress = false;
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!suppress)
            base.OnCollectionChanged(e);
    }
}
