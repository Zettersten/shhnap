using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Shnapp.App.Capture;

/// <summary>A short-lived webcam preview that returns one photo or null when canceled.</summary>
internal static class CameraCaptureDialog
{
    internal static async Task<byte[]?> CaptureAsync(
        XamlRoot root, string? preferredSourceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CameraSource> sources;
        try
        {
            sources = await CameraCaptureService.EnumerateSourcesAsync();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Shnapp could not check the available cameras. Try again.", exception);
        }

        if (sources.Count == 0)
        {
            throw new InvalidOperationException("No camera is connected. Connect a webcam and try again.");
        }

        var sourcePicker = new ComboBox
        {
            Header = "Camera",
            ItemsSource = sources,
            DisplayMemberPath = nameof(CameraSource.Name),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedItem = sources.FirstOrDefault(source => source.Id == preferredSourceId) ?? sources[0],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(sourcePicker, "Camera source");

        var preview = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            Height = 340,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(preview, "Live camera preview");
        var previewFrame = new Border
        {
            Background = new SolidColorBrush(Colors.Black),
            CornerRadius = new CornerRadius(8),
            Child = preview,
        };
        var status = new TextBlock
        {
            Text = "Starting camera…",
            TextWrapping = TextWrapping.Wrap,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var retry = new Button { Content = "Try again", Visibility = Visibility.Collapsed };
        var privacySettings = new HyperlinkButton
        {
            Content = "Open camera privacy settings",
            Visibility = Visibility.Collapsed,
        };
        privacySettings.Click += async (_, _) =>
            await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-webcam"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(retry);
        actions.Children.Add(privacySettings);
        var content = new StackPanel { Width = 560, Spacing = 12 };
        content.Children.Add(sourcePicker);
        content.Children.Add(previewFrame);
        content.Children.Add(status);
        content.Children.Add(actions);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Take a photo",
            Content = content,
            PrimaryButtonText = "Take photo",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        var sourceGate = new SemaphoreSlim(1, 1);
        CameraCaptureService? camera = null;
        byte[]? photo = null;
        int sourceRevision = 0;
        bool loaded = false;
        bool closed = false;

        async Task StartSelectedSourceAsync()
        {
            if (sourcePicker.SelectedItem is not CameraSource source || closed)
            {
                return;
            }

            int requestedRevision = ++sourceRevision;
            dialog.IsPrimaryButtonEnabled = false;
            retry.Visibility = Visibility.Collapsed;
            privacySettings.Visibility = Visibility.Collapsed;
            status.Text = "Starting " + source.Name + "…";
            await sourceGate.WaitAsync();
            try
            {
                if (closed || requestedRevision != sourceRevision)
                {
                    return;
                }

                camera?.Dispose();
                camera = null;
                var session = new CameraCaptureService();
                session.Failed += message => dialog.DispatcherQueue.TryEnqueue(() =>
                {
                    if (closed || !ReferenceEquals(camera, session))
                    {
                        return;
                    }

                    dialog.IsPrimaryButtonEnabled = false;
                    status.Text = message;
                    retry.Visibility = Visibility.Visible;
                    camera?.Dispose();
                    camera = null;
                });
                try
                {
                    await session.StartAsync(source.Id, preview);
                    if (closed || requestedRevision != sourceRevision)
                    {
                        session.Dispose();
                        return;
                    }

                    camera = session;
                    dialog.IsPrimaryButtonEnabled = true;
                    status.Text = "Position the camera, then take a photo.";
                }
                catch (Exception exception)
                {
                    session.Dispose();
                    if (!closed && requestedRevision == sourceRevision)
                    {
                        bool denied = IsAccessDenied(exception);
                        status.Text = Explain(exception);
                        retry.Visibility = Visibility.Visible;
                        privacySettings.Visibility = denied ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
            finally
            {
                sourceGate.Release();
            }
        }

        sourcePicker.SelectionChanged += (_, _) =>
        {
            if (loaded) { _ = StartSelectedSourceAsync(); }
        };
        retry.Click += (_, _) => _ = StartSelectedSourceAsync();
        dialog.Loaded += (_, _) =>
        {
            if (loaded) { return; }
            loaded = true;
            _ = StartSelectedSourceAsync();
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            args.Cancel = true;
            try
            {
                if (camera is null) { return; }
                dialog.IsPrimaryButtonEnabled = false;
                sourcePicker.IsEnabled = false;
                status.Text = "Taking photo…";
                photo = await camera.CapturePngAsync();
                args.Cancel = false;
            }
            catch (Exception exception)
            {
                status.Text = Explain(exception);
                retry.Visibility = Visibility.Visible;
                privacySettings.Visibility = IsAccessDenied(exception)
                    ? Visibility.Visible : Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = camera is not null;
            }
            finally
            {
                sourcePicker.IsEnabled = true;
                deferral.Complete();
            }
        };

        using var cancellationRegistration = cancellationToken.Register(() =>
            dialog.DispatcherQueue.TryEnqueue(dialog.Hide));
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        finally
        {
            closed = true;
            await sourceGate.WaitAsync();
            try { camera?.Dispose(); }
            finally
            {
                sourceGate.Release();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result == ContentDialogResult.Primary ? photo : null;
    }

    private static bool IsAccessDenied(Exception exception) =>
        exception is UnauthorizedAccessException || exception.HResult == unchecked((int)0x80070005);

    private static string Explain(Exception exception)
    {
        if (IsAccessDenied(exception))
        {
            return "Camera access is blocked in Windows privacy settings. Allow Shnapp to use the camera, then try again.";
        }

        if (exception.HResult == unchecked((int)0x80070020))
        {
            return "This camera is in use by another app. Close that app or choose another camera.";
        }

        return exception is InvalidOperationException or InvalidDataException
            ? exception.Message
            : "The camera could not take a photo. Reconnect it or choose another camera, then try again.";
    }
}
