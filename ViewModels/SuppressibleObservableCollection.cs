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
        try
        {
            foreach (var item in items)
                Add(item);
        }
        finally
        {
            suppress = false;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    public void ReplaceAllSuppressed(IList<T> items)
    {
        if (items is null)
        {
            Clear();
            return;
        }

        suppress = true;
        try
        {
            Items.Clear();
            for (var i = 0; i < items.Count; i++)
                Items.Add(items[i]);
        }
        finally
        {
            suppress = false;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!suppress)
            base.OnCollectionChanged(e);
    }
}
