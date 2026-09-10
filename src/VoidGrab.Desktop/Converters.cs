using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using VoidGrab.Models;

namespace VoidGrab.Desktop;

/// <summary>Colours a queue row's state chip.</summary>
public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is JobState state
            ? state switch
            {
                JobState.Done => "Success",
                JobState.Failed => "Danger",
                JobState.Cancelled => "Muted",
                JobState.Queued => "Muted",
                _ => "Accent",
            }
            : "Muted";

        // Avalonia's lookup is TryGetResource and takes a theme variant; the WPF
        // spelling (TryFindResource) does not exist here.
        if (Application.Current?.TryGetResource(key, ThemeVariant.Dark, out var found) == true &&
            found is IBrush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EmptyStringToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !string.IsNullOrWhiteSpace(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when a count is zero — drives the queue's empty-state label.</summary>
public sealed class CountToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count == 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
