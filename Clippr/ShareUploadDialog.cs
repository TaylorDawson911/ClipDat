using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Clippr;

public static class ShareUploadDialog
{
    public static async Task ShowAsync(XamlRoot root, string path, bool reuseExisting = true,
        bool autoCopy = true, bool openAfter = false, bool showProgress = true, bool keepHistory = true,
        Action<string>? onStatus = null, Func<Task>? onFailure = null)
    {
        ShareUpload? existing = null;
        try { if (reuseExisting) existing = new ShareHistory().FindCurrent(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (existing is not null)
        {
            var previous = existing;
            var priorContent = new StackPanel { Spacing = 10 };
            priorContent.Children.Add(new TextBlock
            {
                Text = "This saved clip already has a share link. Current status: " + previous.Status,
                TextWrapping = TextWrapping.Wrap
            });
            var open = new Button { Content = "Open video" };
            open.Click += async (_, _) => await Launcher.LaunchUriAsync(new Uri(previous.WatchUrl));
            priorContent.Children.Add(open);
            var check = new Button { Content = "View upload status" };
            var currentStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
            check.Click += async (_, _) =>
            {
                try { currentStatus.Text = "Status: " + await new AnonMp4ShareService().GetStatusAsync(previous.VideoId, CancellationToken.None); }
                catch (Exception ex) { currentStatus.Text = "Could not check status: " + ex.Message; }
            };
            priorContent.Children.Add(check);
            priorContent.Children.Add(currentStatus);
            var prior = new ContentDialog
            {
                XamlRoot = root, Title = "Already uploaded",
                Content = priorContent,
                PrimaryButtonText = "Copy link", SecondaryButtonText = "Upload again", CloseButtonText = "Close"
            };
            var choice = await prior.ShowAsync();
            if (choice == ContentDialogResult.Primary) { Copy(existing.WatchUrl); return; }
            if (choice != ContentDialogResult.Secondary) return;
        }
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("The selected clip no longer exists.", path);
        if (file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            file.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select a video clip to upload.");

        var confirm = new ContentDialog
        {
            XamlRoot = root, Title = "Upload to AnonMP4?",
            Content = "This sends the selected video to an external hosting service. People with the link may be able to watch it. Your local clip is kept.",
            PrimaryButtonText = "Upload clip", CloseButtonText = "Cancel"
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        using var cancellation = new CancellationTokenSource();
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true };
        var status = new TextBlock { Text = "Connecting to AnonMP4...", TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 12, MaxWidth = 450 };
        content.Children.Add(new TextBlock { Text = file.Name, TextWrapping = TextWrapping.Wrap });
        if (showProgress) content.Children.Add(bar);
        content.Children.Add(status);
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = "Uploading clip", Content = content, CloseButtonText = "Cancel"
        };
        var running = true;
        var minimized = false;
        var completed = false;
        dialog.SecondaryButtonText = "Minimise";
        dialog.SecondaryButtonClick += (_, _) => { if (!completed) minimized = true; };
        dialog.Closed += (_, _) => { if (running && !minimized) cancellation.Cancel(); };
        var visible = dialog.ShowAsync();
        await Task.Yield();
        try
        {
            var progress = new Progress<(long Sent, long Total)>(value =>
            {
                if (!running) return;
                if (showProgress)
                {
                    bar.IsIndeterminate = false;
                    bar.Value = value.Total == 0 ? 0 : 100.0 * value.Sent / value.Total;
                    status.Text = $"{value.Sent / 1048576.0:F1} MB / {value.Total / 1048576.0:F1} MB";
                }
            });
            var service = new AnonMp4ShareService();
            var upload = await service.UploadAsync(path, progress, cancellation.Token);
            var historySaved = true;
            try { if (keepHistory) new ShareHistory().Save(upload); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { historySaved = false; }
            var copied = false;
            try { if (autoCopy) { Copy(upload.WatchUrl); copied = true; } }
            catch (Exception) { }
            dialog.Title = "Clip uploaded";
            status.Text = (copied ? "Link copied. " : "") +
                "Upload complete — processing video. Playback may not be available yet." +
                (keepHistory && !historySaved ? " Upload history could not be saved." : "") +
                (autoCopy && !copied ? " Clipboard copy failed; use Copy link." : "");
            bar.Value = 100;
            completed = true;
            dialog.PrimaryButtonText = "Copy link";
            dialog.SecondaryButtonText = "Open video";
            dialog.CloseButtonText = "Close";
            dialog.PrimaryButtonClick += (_, _) => Copy(upload.WatchUrl);
            dialog.SecondaryButtonClick += async (_, _) => await Launcher.LaunchUriAsync(new Uri(upload.WatchUrl));
            if (openAfter) await Launcher.LaunchUriAsync(new Uri(upload.WatchUrl));
            onStatus?.Invoke(status.Text);
            _ = PollStatusAsync(service, upload, minimized ? null : status, keepHistory,
                minimized ? CancellationToken.None : cancellation.Token);
        }
        catch (OperationCanceledException) { status.Text = "Upload cancelled."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or
            InvalidDataException or InvalidOperationException or JsonException)
        {
            dialog.Title = "Upload failed";
            status.Text = ex.Message;
            bar.IsIndeterminate = false;
            onStatus?.Invoke("Couldn't upload your clip. " + ex.Message);
            dialog.PrimaryButtonText = "Retry";
        }
        finally { running = false; dialog.CloseButtonText = "Close"; }
        var choiceAfter = await visible;
        cancellation.Cancel();
        if (dialog.Title?.ToString() == "Upload failed" && choiceAfter == ContentDialogResult.Primary)
            await ShowAsync(root, path, false, autoCopy, openAfter, showProgress, keepHistory, onStatus, onFailure);
        else if (dialog.Title?.ToString() == "Upload failed" && onFailure is not null)
            await onFailure();
    }

    private static async Task PollStatusAsync(AnonMp4ShareService service, ShareUpload upload,
        TextBlock? status, bool keepHistory, CancellationToken token)
    {
        try
        {
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(i == 0 ? 1 : 10), token);
                var state = await service.GetStatusAsync(upload.VideoId, token);
                if (state == "processing") continue;
                if (status is not null) status.Text = state == "ready" ? "Video ready." :
                    "AnonMP4 reported a processing failure. Your local clip is safe.";
                if (keepHistory) new ShareHistory().Save(upload with { Status = state });
                return;
            }
            if (status is not null) status.Text = "Upload complete. Processing is still unconfirmed; the link may not play yet.";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (status is not null) status.Text = "Upload complete. Playback status could not be confirmed."; }
    }

    private static void Copy(string value)
    {
        var data = new DataPackage();
        data.SetText(value);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }
}
