using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace OnedriveBackuper.App.Views;

/// <summary>Keeps a list scrolled to its newest item, like a terminal. Used for the activity log.</summary>
public static class AutoScroll
{
    public static readonly DependencyProperty ToEndProperty = DependencyProperty.RegisterAttached(
        "ToEnd", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetToEnd(DependencyObject element) => (bool)element.GetValue(ToEndProperty);

    public static void SetToEnd(DependencyObject element, bool value) => element.SetValue(ToEndProperty, value);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is ListBox list && e.NewValue is true)
        {
            ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add && list.Items.Count > 0)
                {
                    list.ScrollIntoView(list.Items[^1]);
                }
            };
        }
    }
}
