using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Clippr;

public sealed partial class LibraryView : UserControl, IDisposable
{
    private readonly MainWindow _mainWindow;
    private readonly ObservableCollection<LibraryItem> _visible = [];
    private readonly List<LibraryItem> _all = [];
    private readonly Queue<LibraryItem> _thumbnailCache = new();
    private readonly Dictionary<string, ClipMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipDat Library", "library.json");
    private FileSystemWatcher? _clipWatcher;
    private CancellationTokenSource? _watchDebounce;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _metadataSaveCancellation;
    private LibraryItem? _selected;
    private string _folder = "";
    private bool _showingSelection;

    public LibraryView(MainWindow mainWindow, string clipFolder)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        _folder = clipFolder;
        MediaGrid.ItemsSource = _visible;
        TypeFilter.SelectedIndex = 0;
        SortFilter.SelectedIndex = 0;
        LoadState();
        ShowSelection(null);
        _ = RefreshAsync();
    }

    public void Dispose()
    {
        _clipWatcher?.Dispose();
        _watchDebounce?.Cancel();
        _scanCancellation?.Cancel();
        _metadataSaveCancellation?.Cancel();
        VideoPlayer.Source = null;
        try { WriteState(SerializeState()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var state = JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(_statePath));
            if (state is null) return;
            foreach (var entry in state.Items ?? []) _metadata[entry.Key] = entry.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { ShowStatus($"Library settings could not be read: {ex.Message}"); }
    }

    private Task SaveStateAsync()
    {
        try
        {
            WriteState(SerializeState());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowStatus($"Library settings could not be saved: {ex.Message}"); }
        return Task.CompletedTask;
    }

    private string SerializeState() => JsonSerializer.Serialize(new LibraryState(_folder,
        new Dictionary<string, ClipMetadata>(_metadata, StringComparer.OrdinalIgnoreCase)));

    private void WriteState(string data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temp = _statePath + ".tmp";
        File.WriteAllText(temp, data);
        File.Move(temp, _statePath, true);
    }

    private void ScheduleMetadataSave()
    {
        _metadataSaveCancellation?.Cancel();
        var cancellation = _metadataSaveCancellation = new CancellationTokenSource();
        _ = SaveLaterAsync(cancellation.Token);
    }

    private async Task SaveLaterAsync(CancellationToken token)
    {
        try { await Task.Delay(350, token); }
        catch (TaskCanceledException) { return; }
        if (!token.IsCancellationRequested) await SaveStateAsync();
    }

    private async Task RefreshAsync()
    {
        _scanCancellation?.Cancel();
        var cancellation = _scanCancellation = new CancellationTokenSource();
        if (!Directory.Exists(_folder))
        {
            _all.Clear();
            ApplyFilter();
            ShowStatus("Choose an existing clips folder to begin.");
            return;
        }
        ShowStatus("Scanning clips...");
        try
        {
            var files = await Task.Run(() => ScanFiles(_folder, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            _all.Clear();
            _all.AddRange(files.Select(file => new LibraryItem(file, GetMetadata(file))));
            ApplyFilter();
            SetupWatchers();
            await SaveStateAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowStatus($"Could not scan clips: {ex.Message}"); }
    }

    private static List<FileInfo> ScanFiles(string folder, CancellationToken token)
    {
        var files = new List<FileInfo>();
        foreach (var directory in new[] { folder, Path.Combine(folder, "Screenshots") })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                if (!LibraryItem.IsSupported(path)) continue;
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length > 0) files.Add(info);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return files;
    }

    private ClipMetadata GetMetadata(FileInfo file) =>
        _metadata.TryGetValue(file.FullName, out var metadata) ? metadata : new ClipMetadata();

    private void SetupWatchers()
    {
        _clipWatcher?.Dispose();
        _clipWatcher = Watch(_folder);
    }

    private FileSystemWatcher? Watch(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        var watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };
        void Changed(object? sender, FileSystemEventArgs args) => DispatcherQueue.TryEnqueue(ScheduleRefresh);
        watcher.Created += Changed;
        watcher.Deleted += Changed;
        watcher.Renamed += (sender, args) => Changed(sender, args);
        watcher.Error += (_, _) => DispatcherQueue.TryEnqueue(ScheduleRefresh);
        return watcher;
    }

    private void ScheduleRefresh()
    {
        _watchDebounce?.Cancel();
        var cancellation = _watchDebounce = new CancellationTokenSource();
        _ = RefreshAfterChangeAsync(cancellation.Token);
    }

    private async Task RefreshAfterChangeAsync(CancellationToken token)
    {
        try { await Task.Delay(500, token); }
        catch (TaskCanceledException) { return; }
        if (!token.IsCancellationRequested) await RefreshAsync();
    }

    private void ApplyFilter()
    {
        var search = SearchBox?.Text?.Trim() ?? "";
        var type = TypeFilter?.SelectedIndex ?? 0;
        IEnumerable<LibraryItem> filtered = _all.Where(item =>
            (type switch { 1 => !item.IsImage, 2 => item.IsImage, 3 => item.Metadata.Favourite, _ => true }) &&
            (search.Length == 0 || item.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             item.Metadata.Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             item.Metadata.Tags.Contains(search, StringComparison.OrdinalIgnoreCase)));
        filtered = (SortFilter?.SelectedIndex ?? 0) switch
        {
            1 => filtered.OrderBy(item => item.Created),
            2 => filtered.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            3 => filtered.OrderByDescending(item => item.Size),
            _ => filtered.OrderByDescending(item => item.Created)
        };
        _visible.Clear();
        foreach (var item in filtered) _visible.Add(item);
        CountText.Text = $"{_visible.Count} files";
        SizeText.Text = FormatSize(_visible.Sum(item => item.Size));
        if (_selected is not null && !_visible.Contains(_selected)) ShowSelection(null);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1_048_576 => $"{bytes / 1024.0:0} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:0.0} MB",
        _ => $"{bytes / 1_073_741_824.0:0.0} GB"
    };

    private void OnFilterChanged(object sender, RoutedEventArgs args) { if (MediaGrid is not null) ApplyFilter(); }
    private void OnRefreshClick(object sender, RoutedEventArgs args) => _ = RefreshAsync();
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        DeleteSelectedButton.IsEnabled = MediaGrid.SelectedItems.Count > 0;
        ShowSelection(MediaGrid.SelectedItems.Count == 1 ? MediaGrid.SelectedItems[0] as LibraryItem : null);
    }
    private void OnItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is LibraryItem item && MediaGrid.SelectedItems.Count <= 1) ShowSelection(item);
    }
    private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: LibraryItem item } && !MediaGrid.SelectedItems.Contains(item))
        {
            MediaGrid.SelectedItems.Clear();
            MediaGrid.SelectedItems.Add(item);
        }
    }

    private async void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: LibraryItem item })
            await RunFileActionAsync(() => WindowsShellFileActions.OpenWithDefaultApplication(
                WindowsShellFileActions.RequireExistingFile(item.Path)));
    }

    private async void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not LibraryItem item || item.Thumbnail is not null ||
            string.Equals(Path.GetExtension(item.Path), ".mkv", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            using var thumbnail = await file.GetThumbnailAsync(item.IsImage ? ThumbnailMode.PicturesView : ThumbnailMode.VideosView, 320);
            if (thumbnail is null || thumbnail.Size == 0) return;
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            item.Thumbnail = bitmap;
            _thumbnailCache.Enqueue(item);
            while (_thumbnailCache.Count > 160) _thumbnailCache.Dequeue().Thumbnail = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException) { }
    }

    private void ShowSelection(LibraryItem? item)
    {
        _selected = item;
        _showingSelection = true;
        VideoPlayer.Source = null;
        VideoPlayer.Visibility = Visibility.Collapsed;
        PhotoPreview.Visibility = Visibility.Collapsed;
        EmptySelectionText.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        SelectedNameText.Text = item?.DisplayName ?? "";
        SelectedDetailsText.Text = item is null ? "" : $"{item.Created:g}  ·  {FormatSize(item.Size)}";
        DescriptionBox.Text = item?.Metadata.Description ?? "";
        TagsBox.Text = item?.Metadata.Tags ?? "";
        DescriptionBox.IsEnabled = item is not null;
        TagsBox.IsEnabled = item is not null;
        OpenSelectedButton.IsEnabled = item is not null;
        ShowSelectedButton.IsEnabled = item is not null;
        FavouriteButton.IsEnabled = item is not null;
        FavouriteButton.Content = item?.Metadata.Favourite == true ? "Remove from favourites" : "Add to favourites";
        if (item is not null)
        {
            if (item.IsImage)
            {
                PhotoPreview.Source = new BitmapImage(new Uri(item.Path));
                PhotoPreview.Visibility = Visibility.Visible;
            }
            else
            {
                VideoPlayer.Source = MediaSource.CreateFromUri(new Uri(item.Path));
                VideoPlayer.Visibility = Visibility.Visible;
            }
        }
        _showingSelection = false;
    }

    private void OnMetadataEdited(object sender, TextChangedEventArgs args)
    {
        if (_showingSelection || _selected is null) return;
        _selected.Metadata.Description = DescriptionBox.Text;
        _selected.Metadata.Tags = TagsBox.Text;
        _metadata[_selected.Path] = _selected.Metadata;
        ScheduleMetadataSave();
    }

    private void OnFavouriteClick(object sender, RoutedEventArgs args)
    {
        if (_selected is null) return;
        _selected.Metadata.Favourite = !_selected.Metadata.Favourite;
        _metadata[_selected.Path] = _selected.Metadata;
        FavouriteButton.Content = _selected.Metadata.Favourite ? "Remove from favourites" : "Add to favourites";
        ScheduleMetadataSave();
        if (TypeFilter.SelectedIndex == 3) ApplyFilter();
    }

    private LibraryItem RequireSelected()
    {
        var item = _selected ?? MediaGrid.SelectedItems.OfType<LibraryItem>().FirstOrDefault() ?? throw new FileNotFoundException("Select one file first.");
        WindowsShellFileActions.RequireExistingFile(item.Path);
        return item;
    }

    private async void OnOpenFileClick(object sender, RoutedEventArgs args) => await RunFileActionAsync(() => WindowsShellFileActions.OpenWithDefaultApplication(RequireSelected().Path));
    private async void OnShowFileClick(object sender, RoutedEventArgs args) => await RunFileActionAsync(() => WindowsShellFileActions.ShowFileInFolder(RequireSelected().Path));
    private async void OnCopyFileClick(object sender, RoutedEventArgs args) => await RunFileActionAsync(() => WindowsShellFileActions.CopyFileToClipboard(RequireSelected().Path));
    private async void OnCopyPathClick(object sender, RoutedEventArgs args) => await RunFileActionAsync(() =>
    {
        var data = new DataPackage();
        data.SetText(RequireSelected().Path);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    });

    private async Task RunFileActionAsync(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException or Win32Exception)
        { await ShowErrorAsync(ex.Message); }
    }

    private async void OnRenameClick(object sender, RoutedEventArgs args)
    {
        LibraryItem item;
        try { item = RequireSelected(); }
        catch (IOException ex) { await ShowErrorAsync(ex.Message); return; }
        var box = new TextBox { Text = Path.GetFileNameWithoutExtension(item.Path) };
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Rename file", Content = box, PrimaryButtonText = "Rename", CloseButtonText = "Cancel" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = box.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { await ShowErrorAsync("Choose a valid file name."); return; }
        var destination = Path.Combine(Path.GetDirectoryName(item.Path)!, name + Path.GetExtension(item.Path));
        try
        {
            if (File.Exists(destination)) throw new IOException("A file with this name already exists.");
            VideoPlayer.Source = null;
            File.Move(item.Path, destination);
            if (_metadata.Remove(item.Path, out var metadata)) _metadata[destination] = metadata;
            await SaveStateAsync();
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await ShowErrorAsync(ex.Message); }
    }

    private async void OnDeleteSelectedClick(object sender, RoutedEventArgs args)
    {
        var selected = MediaGrid.SelectedItems.OfType<LibraryItem>().ToList();
        if (selected.Count == 0 && _selected is not null) selected.Add(_selected);
        if (selected.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = selected.Count == 1 ? "Move this file to the Recycle Bin?" : $"Move {selected.Count} files to the Recycle Bin?",
            Content = "Your recordings can be restored from the Windows Recycle Bin.",
            PrimaryButtonText = "Move to Recycle Bin",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        VideoPlayer.Source = null;
        var failures = new List<string>();
        foreach (var item in selected)
        {
            try
            {
                WindowsShellFileActions.RequireExistingFile(item.Path);
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                _metadata.Remove(item.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            { failures.Add($"{item.DisplayName}: {ex.Message}"); }
        }
        await SaveStateAsync();
        await RefreshAsync();
        if (failures.Count > 0) await ShowErrorAsync(string.Join("\n", failures.Take(4)));
    }

    private async void OnChooseFolderClick(object sender, RoutedEventArgs args)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_mainWindow));
        var choice = await picker.PickSingleFolderAsync();
        if (choice is null) return;
        _folder = choice.Path;
        await RefreshAsync();
    }

    private async void OnOpenFolderClick(object sender, RoutedEventArgs args) => await RunFileActionAsync(() =>
    {
        if (!Directory.Exists(_folder)) throw new DirectoryNotFoundException("The clips folder no longer exists.");
        WindowsShellFileActions.OpenWithDefaultApplication(_folder);
    });

    private void ShowStatus(string message) { if (CountText is not null) CountText.Text = message; }
    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Clip Library", Content = message, CloseButtonText = "Close" };
        await dialog.ShowAsync();
    }
}

public sealed class LibraryItem : INotifyPropertyChanged
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };
    private BitmapImage? _thumbnail;
    public event PropertyChangedEventHandler? PropertyChanged;
    public LibraryItem(FileInfo file, ClipMetadata metadata)
    {
        Path = file.FullName;
        Size = file.Length;
        Created = file.CreationTime;
        Metadata = metadata;
    }
    public static bool IsSupported(string path) => VideoExtensions.Contains(System.IO.Path.GetExtension(path)) || ImageExtensions.Contains(System.IO.Path.GetExtension(path));
    public string Path { get; }
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Path);
    public long Size { get; }
    public DateTime Created { get; }
    public bool IsImage => ImageExtensions.Contains(System.IO.Path.GetExtension(Path));
    public ClipMetadata Metadata { get; }
    public string Summary => $"{Created:g} · {(Size < 1_048_576 ? $"{Size / 1024.0:0} KB" : $"{Size / 1_048_576.0:0.0} MB")}";
    public BitmapImage? Thumbnail { get => _thumbnail; set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); } }
}

public sealed class ClipMetadata
{
    public string Description { get; set; } = "";
    public string Tags { get; set; } = "";
    public bool Favourite { get; set; }
}

public sealed record LibraryState(string? Folder, Dictionary<string, ClipMetadata>? Items);
