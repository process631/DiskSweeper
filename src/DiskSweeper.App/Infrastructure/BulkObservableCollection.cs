using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DiskSweeper.App.Infrastructure;

/// <summary>
/// An ObservableCollection that can change many items with one notification. Adding 20,000 rows one by
/// one would make the list redraw 20,000 times.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        NotifyReset();
    }

    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        for (int i = 0; i < items.Count; i++) Items.Insert(index + i, items[i]);
        NotifyReset();
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0) return;
        if (Items is List<T> list) list.RemoveRange(index, count);
        else for (int i = 0; i < count; i++) Items.RemoveAt(index);
        NotifyReset();
    }

    private void NotifyReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
