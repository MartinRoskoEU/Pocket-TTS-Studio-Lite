using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using PocketTTSStudioLite.Services;

namespace PocketTTSStudioLite;

/// <summary>Hide update labels for models that have no manual registered-installation comparison.</summary>
public sealed class ModelUpdateVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>Read-only display of paths owned by the existing audio and destination pickers.</summary>
public sealed class LocalPathDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string path = value as string ?? "";
        string? part = parameter as string;
        if (part == "Visibility") { return string.IsNullOrWhiteSpace(path) ? Visibility.Collapsed : Visibility.Visible; }
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        return part switch
        {
            "ReferenceName" => Path.GetFileName(path),
            _ => path
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Native status glyphs for the setup service's existing installation states.</summary>
public sealed class ModelInstallationGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        PocketTtsModelInstallationState.InstalledVerified => "\uE73E", // Check mark.
        PocketTtsModelInstallationState.NotInstalled => "",
        PocketTtsModelInstallationState.InstalledUnverified => "\uE7BA", // Warning.
        PocketTtsModelInstallationState.Incompatible => "\uE7BA", // Warning.
        _ => ""
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
/// <summary>Choose theme-aware row styles without changing the model state.</summary>
public sealed class ModelInstallationStyleConverter : IValueConverter
{
    public Style Installed { get; set; } = null!;
    public Style NotInstalled { get; set; } = null!;
    public Style Unverified { get; set; } = null!;
    public Style Incompatible { get; set; } = null!;

    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        PocketTtsModelInstallationState.InstalledVerified => Installed,
        PocketTtsModelInstallationState.NotInstalled => NotInstalled,
        PocketTtsModelInstallationState.Incompatible => Incompatible,
        _ => Unverified,
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>Registered models remain removable regardless of verification or runtime compatibility.</summary>
public sealed class ModelManagementActionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string? action = value switch
        {
            PocketTtsModelSetupEntry { CanRemove: true } => "Remove",
            PocketTtsModelSetupEntry { State: PocketTtsModelInstallationState.NotInstalled } => "Install",
            PocketTtsModelSetupEntry { CanInstall: true } => "Install",
            _ => null,
        };
        return parameter as string == "Visibility" ? (action is null ? Visibility.Collapsed : Visibility.Visible) : action ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class MainWindow
{
    private void ModelRow_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid row) { return; }
        var identifier = row.Children.OfType<TextBlock>().FirstOrDefault();
        var status = row.Children.OfType<Grid>().FirstOrDefault();
        var action = row.Children.OfType<Button>().FirstOrDefault();
        if (identifier is null || status is null || action is null) { return; }
        bool stacked = args.NewSize.Width < 560;
        row.RowSpacing = stacked ? 8 : 0;
        Grid.SetColumnSpan(identifier, stacked ? 2 : 1);
        Grid.SetColumn(status, stacked ? 0 : 1);
        Grid.SetRow(status, stacked ? 1 : 0);
        Grid.SetColumnSpan(status, stacked ? 2 : 1);
        Grid.SetRowSpan(action, stacked ? 2 : 1);
    }
}
