using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KospetMapMaker.Core;

namespace KospetMapMaker;

public sealed class AppSettings
{
    public bool En { get; set; }
    public string OutDir { get; set; } = "";
    public string PbfPath { get; set; } = "";
    public string RegionId { get; set; } = "";
    public bool UseFile { get; set; }
    public bool Roads { get; set; } = true;
    public bool Paths { get; set; } = true;
    public bool Service { get; set; } = true;
    public bool Tracks { get; set; } = true;
    public bool Water { get; set; } = true;
    public bool Rivers { get; set; } = true;
    public bool Landuse { get; set; } = true;
    public bool Extra { get; set; } = true;
    public bool Contours { get; set; } = true;
    public int Step { get; set; } = 100;

    private static string FilePath => Path.Combine(Sources.AppDir, "settings.json");
    public static AppSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new AppSettings();
    }
    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}

public partial class MainWindow : Window
{
    private List<Region> _regions = new();
    private CancellationTokenSource? _cts;
    private string? _lastMapDir;
    private readonly AppSettings _set;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _set = AppSettings.Load();
        Loc.En = _set.En;
        LangBox.SelectedIndex = _set.En ? 1 : 0;
        if (string.IsNullOrEmpty(_set.OutDir))
            _set.OutDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "maps");
        OutDir.Text = _set.OutDir;
        PbfPath.Text = _set.PbfPath;
        SrcFile.IsChecked = _set.UseFile; SrcRegion.IsChecked = !_set.UseFile;
        ChkRoads.IsChecked = _set.Roads; ChkPaths.IsChecked = _set.Paths; ChkService.IsChecked = _set.Service; ChkTracks.IsChecked = _set.Tracks;
        ChkWater.IsChecked = _set.Water; ChkRivers.IsChecked = _set.Rivers; ChkLanduse.IsChecked = _set.Landuse; ChkExtra.IsChecked = _set.Extra;
        ChkContours.IsChecked = _set.Contours; StepBox.SelectedIndex = _set.Step == 50 ? 1 : 0;

        LangBox.SelectionChanged += (_, _) => { Loc.En = LangBox.SelectedIndex == 1; ApplyLang(); };
        SearchBox.TextChanged += (_, _) => FilterRegions();
        RegionList.SelectionChanged += (_, _) =>
        {
            if (RegionList.SelectedItem is Region r) { SrcRegion.IsChecked = true; NameBox.Text = Pipeline.Sanitize(r.Id.Split('/').Last()); }
        };
        ReloadBtn.Click += async (_, _) => await LoadRegions(true);
        BrowsePbfBtn.Click += BrowsePbf;
        BrowseOutBtn.Click += BrowseOut;
        BuildBtn.Click += Build;
        CancelBtn.Click += (_, _) => _cts?.Cancel();
        OpenBtn.Click += (_, _) => OpenFolder(OutDir.Text ?? "");
        ChkRoads.IsCheckedChanged += (_, _) => SyncSubs();
        ChkWater.IsCheckedChanged += (_, _) => SyncSubs();
        ChkLanduse.IsCheckedChanged += (_, _) => SyncSubs();
        ChkContours.IsCheckedChanged += (_, _) => SyncSubs();
        Closing += (_, _) => { SaveSettings(); _cts?.Cancel(); };

        ApplyLang();
        SyncSubs();
        Opened += async (_, _) => await LoadRegions(false);
    }

    private void SyncSubs()
    {
        ChkPaths.IsEnabled = ChkService.IsEnabled = ChkTracks.IsEnabled = ChkRoads.IsChecked == true;
        ChkRivers.IsEnabled = ChkWater.IsChecked == true;
        ChkExtra.IsEnabled = ChkLanduse.IsChecked == true;
        StepBox.IsEnabled = ChkContours.IsChecked == true;
    }

    private void ApplyLang()
    {
        Title = Loc.T("title");
        TitleText.Text = "Kospet Map Maker";
        HSource.Text = Loc.T("source");
        SrcRegion.Content = Loc.T("srcRegion");
        SrcFile.Content = Loc.T("srcFile");
        SearchBox.Watermark = Loc.T("search");
        ReloadBtn.Content = Loc.T("reload");
        ChkRefresh.Content = Loc.T("refresh");
        BrowsePbfBtn.Content = Loc.T("browse");
        BrowseOutBtn.Content = Loc.T("browse");
        HLayers.Text = Loc.T("layers");
        ChkRoads.Content = Loc.T("roads");
        ChkPaths.Content = Loc.T("paths");
        ChkService.Content = Loc.T("service");
        ChkTracks.Content = Loc.T("tracks");
        ChkWater.Content = Loc.T("water");
        ChkRivers.Content = Loc.T("rivers");
        ChkLanduse.Content = Loc.T("landuse");
        ChkExtra.Content = Loc.T("extra");
        ChkContours.Content = Loc.T("contours");
        StepLabel.Text = Loc.T("step");
        HOutput.Text = Loc.T("output");
        OutLabel.Text = Loc.T("outDir");
        NameLabel.Text = Loc.T("name");
        HintText.Text = Loc.T("hint");
        BuildBtn.Content = Loc.T("build");
        CancelBtn.Content = Loc.T("cancel");
        OpenBtn.Content = Loc.T("openFolder");
        PreviewLabel.Text = Loc.T("preview");
        LogLabel.Text = Loc.T("log");
        if (!_busy) StatusText.Text = _regions.Count > 0 ? Loc.T("indexLoaded", _regions.Count) : Loc.T("ready");
    }

    private async Task LoadRegions(bool force)
    {
        StatusText.Text = Loc.T("loadingIndex");
        try
        {
            _regions = await Sources.LoadRegionsAsync(force, CancellationToken.None);
            FilterRegions();
            var sel = _regions.FirstOrDefault(r => r.Id == _set.RegionId);
            if (sel != null) { RegionList.SelectedItem = sel; RegionList.ScrollIntoView(sel); }
            StatusText.Text = Loc.T("indexLoaded", _regions.Count);
        }
        catch (Exception e)
        {
            StatusText.Text = Loc.T("indexError", e.Message);
            Log(Loc.T("indexError", e.Message));
        }
    }

    private void FilterRegions()
    {
        var q = (SearchBox.Text ?? "").Trim();
        var sel = RegionList.SelectedItem;
        RegionList.ItemsSource = q.Length == 0 ? _regions
            : _regions.Where(r => r.Path.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Id.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sel != null) RegionList.SelectedItem = sel;
    }

    private async void BrowsePbf(object? s, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("OpenStreetMap PBF") { Patterns = ["*.pbf"] }],
        });
        if (files.Count == 0) return;
        var p = files[0].TryGetLocalPath();
        if (p == null) return;
        PbfPath.Text = p;
        SrcFile.IsChecked = true;
        NameBox.Text = Pipeline.DefaultName(new JobSpec { LocalPbf = p });
    }

    private async void BrowseOut(object? s, RoutedEventArgs e)
    {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (dirs.Count == 0) return;
        var p = dirs[0].TryGetLocalPath();
        if (p != null) OutDir.Text = p;
    }

    private BuildOptions ReadOptions() => new()
    {
        Roads = ChkRoads.IsChecked == true, Paths = ChkPaths.IsChecked == true,
        Service = ChkService.IsChecked == true, Tracks = ChkTracks.IsChecked == true,
        Water = ChkWater.IsChecked == true, Rivers = ChkRivers.IsChecked == true,
        Landuse = ChkLanduse.IsChecked == true, ExtraNatural = ChkExtra.IsChecked == true,
        Contours = ChkContours.IsChecked == true, ContourStep = StepBox.SelectedIndex == 1 ? 50 : 100,
    };

    private void SaveSettings()
    {
        var o = ReadOptions();
        _set.En = Loc.En; _set.OutDir = OutDir.Text ?? ""; _set.PbfPath = PbfPath.Text ?? "";
        _set.UseFile = SrcFile.IsChecked == true;
        if (RegionList.SelectedItem is Region r) _set.RegionId = r.Id;
        _set.Roads = o.Roads; _set.Paths = o.Paths; _set.Service = o.Service; _set.Tracks = o.Tracks;
        _set.Water = o.Water; _set.Rivers = o.Rivers; _set.Landuse = o.Landuse; _set.Extra = o.ExtraNatural;
        _set.Contours = o.Contours; _set.Step = o.ContourStep;
        _set.Save();
    }

    private async void Build(object? s, RoutedEventArgs e)
    {
        var job = new JobSpec { Options = ReadOptions(), OutDir = (OutDir.Text ?? "").Trim(), MapName = NameBox.Text ?? "", RefreshPbf = ChkRefresh.IsChecked == true };
        if (SrcFile.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(PbfPath.Text) || !File.Exists(PbfPath.Text)) { StatusText.Text = Loc.T("noRegion"); return; }
            job.LocalPbf = PbfPath.Text;
        }
        else
        {
            if (RegionList.SelectedItem is not Region r) { StatusText.Text = Loc.T("noRegion"); return; }
            job.Region = r;
        }
        if (job.OutDir.Length == 0) { StatusText.Text = Loc.T("noOut"); return; }
        SaveSettings();

        SetBusy(true);
        LogBox.Text = "";
        _cts = new CancellationTokenSource();
        try
        {
            var mapDir = await Task.Run(() => Pipeline.RunAsync(job,
                msg => Dispatcher.UIThread.Post(() => Log(msg)),
                (stage, f) => Dispatcher.UIThread.Post(() => ShowProgress(stage, f)),
                _cts.Token));
            _lastMapDir = mapDir;
            StatusText.Text = Loc.T("done", mapDir);
            var data = await Task.Run(() => MapPreview.LoadFiles(mapDir));
            Preview.Apply(data);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Loc.T("cancelled"); Log(Loc.T("cancelled"));
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("error", ex.Message); Log(ex.ToString());
        }
        finally
        {
            SetBusy(false);
            _cts.Dispose(); _cts = null;
        }
    }

    private void SetBusy(bool b)
    {
        _busy = b;
        BuildBtn.IsEnabled = !b; CancelBtn.IsEnabled = b; OpenBtn.IsEnabled = !b;
        Prog.IsIndeterminate = false; Prog.Value = b ? 0 : (Prog.Value > 0 ? 1 : 0);
    }

    private void ShowProgress(string stage, double f)
    {
        StatusText.Text = Loc.T("st_" + stage) + (f >= 0 ? $"  {f * 100:F0}%" : "…");
        if (f < 0) Prog.IsIndeterminate = true;
        else { Prog.IsIndeterminate = false; Prog.Value = f; }
    }

    private void Log(string msg)
    {
        var t = (LogBox.Text ?? "") + msg + Environment.NewLine;
        if (t.Length > 200_000) t = t[^150_000..];
        LogBox.Text = t;
        LogBox.CaretIndex = t.Length;
    }

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = false });
        }
        catch { }
    }
}
