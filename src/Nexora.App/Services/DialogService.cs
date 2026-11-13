using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Nexora.App.Services;

public sealed class DialogService(ILogger<DialogService> logger)
{
    private XamlRoot? _root;
    private bool _open;

    public void Attach(XamlRoot root) => _root = root;

    public async Task<bool> ConfirmAsync(string title, string message, string primary, string close = "Cancel", bool destructive = false)
    {
        var dialog = Create(title, new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords, MaxWidth = 480 }, primary, close, destructive);
        return dialog is not null && await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task<bool> ConfirmAsync(string title, UIElement content, string primary, string close = "Cancel")
    {
        var dialog = Create(title, content, primary, close, false);
        return dialog is not null && await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task InformAsync(string title, string message)
    {
        var dialog = Create(title, new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords, MaxWidth = 480 }, null, "OK", false);
        if (dialog is not null)
        {
            await ShowAsync(dialog);
        }
    }

    private ContentDialog? Create(string title, object content, string? primary, string close, bool destructive)
    {
        _root ??= App.MainXamlRoot;
        if (_root is null)
        {
            logger.LogWarning("Dialog '{Title}' could not be shown: the window is not ready", title);
            return null;
        }

        if (_open)
        {
            logger.LogWarning("Dialog '{Title}' skipped because another dialog is open", title);
            return null;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = _root,
            Title = title,
            Content = content,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = (_root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        if (primary is not null)
        {
            dialog.PrimaryButtonText = primary;
            dialog.DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary;
        }

        return dialog;
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        _open = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _open = false;
        }
    }
}
