using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace NRSVaultMini;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Width = _vm.Settings.WindowWidth;
        Height = _vm.Settings.WindowHeight;

        _vm.LogAppended += () =>
        {
            if (_vm.Log.Count > 0) LogList.ScrollIntoView(_vm.Log.Count - 1);
        };

        Opened += async (_, _) =>
        {
            _vm.AddLog($"NRSVault Mini {MainViewModel.AppVersion}. Cache and settings: {Core.Scanner.CacheDir}");
            if (File.Exists(_vm.XmlPath)) await _vm.LoadDbAsync();
            else _vm.AddLog("Choose nes20db.xml to begin.");
        };

        Closing += (_, _) =>
        {
            _vm.Settings.WindowWidth = Width;
            _vm.Settings.WindowHeight = Height;
            _vm.SaveSettings();
        };
    }

    // ---------------- Toolbar ----------------

    private async void BrowseRoot_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolder("Choose the collection folder", _vm.RootFolder);
        if (folder != null) _vm.RootFolder = folder;
    }

    private async void BrowseBackup_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolder("Choose a backup folder (outside the collection is best)", _vm.BackupFolder);
        if (folder != null) _vm.BackupFolder = folder;
    }

    private async void BrowseXml_Click(object? sender, RoutedEventArgs e)
    {
        var start = await StartFolder(Path.GetDirectoryName(_vm.XmlPath));
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose nes20db.xml",
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = new[] { new FilePickerFileType("nes20db.xml") { Patterns = new[] { "*.xml" } } },
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } p)
        {
            _vm.XmlPath = p;
            await _vm.LoadDbAsync();
        }
    }

    private async void LoadDb_Click(object? sender, RoutedEventArgs e) => await _vm.LoadDbAsync();
    private async void Scan_Click(object? sender, RoutedEventArgs e) => await _vm.ScanAsync();
    private async void Fix_Click(object? sender, RoutedEventArgs e) => await _vm.FixAsync(Confirm);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => _vm.Cancel();
    private async void ScanSources_Click(object? sender, RoutedEventArgs e) => await _vm.ScanSourcesAsync();
    private void RemoveSource_Click(object? sender, RoutedEventArgs e) => _vm.RemoveSourceFolder();

    private async void AddSource_Click(object? sender, RoutedEventArgs e)
    {
        var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add source folders to search for ROMs (searched recursively, never modified)",
            AllowMultiple = true,
        });
        foreach (var f in res)
            if (f.TryGetLocalPath() is { } p) _vm.AddSourceFolder(p);
        _vm.SaveSettings();
    }

    private async void Dat_Click(object? sender, RoutedEventArgs e)
    {
        if (!_vm.IsScanned && !await Confirm("The collection has not been scanned yet, so no ROM is verified.\n\nGenerate the DAT anyway?"))
            return;
        var path = await SaveFile("Save RomVault DAT", _vm.DefaultDatFileName, _vm.Settings.LastDatFolder, "DAT file", "*.dat");
        if (path != null) await _vm.GenerateDatAsync(path);
    }

    private async void Report_Click(object? sender, RoutedEventArgs e)
    {
        var path = await SaveFile("Export collection report", $"NRSVault report {DateTime.Now:yyyy-MM-dd HHmm}.csv", _vm.Settings.LastDatFolder, "CSV", "*.csv");
        if (path != null) await _vm.ExportReportAsync(path);
    }

    private async void ExportLog_Click(object? sender, RoutedEventArgs e)
    {
        var path = await SaveFile("Export log", $"NRSVault log {DateTime.Now:yyyy-MM-dd HHmm}.txt", _vm.Settings.LastDatFolder, "Text", "*.txt");
        if (path != null) await _vm.ExportLogAsync(path);
    }

    private void ClearLog_Click(object? sender, RoutedEventArgs e) => _vm.Log.Clear();
    private void ExpandAll_Click(object? sender, RoutedEventArgs e) => _vm.ExpandAll(true);
    private void CollapseAll_Click(object? sender, RoutedEventArgs e) => _vm.ExpandAll(false);

    private void Stat_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: StatItem { FilterKey: { } key } }) _vm.ApplyStatFilter(key);
    }

    // ---------------- Tree grid ----------------

    private void Expander_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TreeNode { IsFolder: true } node })
        {
            _vm.Toggle(node);
            e.Handled = true;
        }
    }

    private void Tree_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Tree.SelectedItem is TreeNode { IsFolder: true } node) _vm.Toggle(node);
    }

    private void Extras_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is ExtraFileVm x) e.Row.Background = x.RowBrush;
    }

    /// <summary>Sorting is done on the tree (children sorted within each folder), not on the flat list.</summary>
    private void Tree_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        var key = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(key)) return;
        _vm.SortBy(key);
        var (k, desc) = _vm.SortState;
        foreach (var col in Tree.Columns)
        {
            var text = (col.Header?.ToString() ?? "").TrimEnd(' ', '▲', '▼');
            col.Header = col.SortMemberPath == k ? text + (desc ? " ▼" : " ▲") : text;
        }
    }

    // ---------------- Dialog helpers ----------------

    private async Task<IStorageFolder?> StartFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
        return await StorageProvider.TryGetFolderFromPathAsync(path);
    }

    private async Task<string?> PickFolder(string title, string current)
    {
        var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartFolder(current),
        });
        return res.Count > 0 ? res[0].TryGetLocalPath() : null;
    }

    private async Task<string?> SaveFile(string title, string name, string folder, string typeName, string pattern)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = name,
            SuggestedStartLocation = await StartFolder(folder),
            DefaultExtension = pattern.TrimStart('*', '.'),
            FileTypeChoices = new[] { new FilePickerFileType(typeName) { Patterns = new[] { pattern } } },
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    private async Task<bool> Confirm(string message)
    {
        bool result = false;
        var yes = new Button { Content = "Continue", Width = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        var no = new Button { Content = "Cancel", Width = 100, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var dlg = new Window
        {
            Title = "NRSVault Mini",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { yes, no } },
                },
            },
        };
        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }
}
