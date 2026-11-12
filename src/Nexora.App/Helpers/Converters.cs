using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Nexora.App.ViewModels;

namespace Nexora.App.Helpers;

/// <summary>Maps a <see cref="StatusLevel"/> to the semantic brushes defined in Themes/Theme.xaml.</summary>
public sealed partial class StatusLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is StatusLevel level
            ? level switch
            {
                StatusLevel.Good => "NexoraGoodBrush",
                StatusLevel.Warning => "NexoraWarningBrush",
                StatusLevel.Problem => "NexoraProblemBrush",
                _ => "NexoraNeutralBrush",
            }
            : "NexoraNeutralBrush";
        return Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush b ? b : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? 1.0 : 0.45;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
