using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Effects;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Color = System.Windows.Media.Color;
using WinForms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfPoint = System.Windows.Point;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PatchcordInstaller;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string ASAR_URL = "https://patchcord.itssolar.dev/installer/app.asar";
    private const string MANIFEST_URL = "http://patchcord.itssolar.dev/injector/manifest.json";
    private readonly HttpClient _http = new();

    private UpdateManifest? _latestManifest;
    private bool _updateAvailable;
    private bool _forceUpdate;
    
    public ObservableCollection<InstallEntry> Clients { get; set; } = new();
    public ObservableCollection<DiscordProcess> Processes { get; set; } = new();
    public ObservableCollection<LogEntry> ActionLogs { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private string _clientsCountStr = "0";
    public string ClientsCountStr { get => _clientsCountStr; set { _clientsCountStr = value; OnPropertyChanged(nameof(ClientsCountStr)); } }
    
    private string _totalRamStr = "0 MB";
    public string TotalRamStr { get => _totalRamStr; set { _totalRamStr = value; OnPropertyChanged(nameof(TotalRamStr)); } }

    private string _diskFootprintStr = "0 MB";
    public string DiskFootprintStr { get => _diskFootprintStr; set { _diskFootprintStr = value; OnPropertyChanged(nameof(DiskFootprintStr)); } }

    private string _lastPatchedStr = "Never";
    public string LastPatchedStr { get => _lastPatchedStr; set { _lastPatchedStr = value; OnPropertyChanged(nameof(LastPatchedStr)); } }

    private DispatcherTimer _procTimer;
    private ICollectionView _clientsView = null!;
    private string _searchText = "";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _clientsView = CollectionViewSource.GetDefaultView(Clients);
        _clientsView.Filter = FilterClient;
        LB_Clients.ItemsSource = _clientsView;

        LB_Procs.ItemsSource = Processes;
        LogItems.ItemsSource = ActionLogs;
        
        _procTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _procTimer.Tick += (_, _) => RefreshProcesses();
    }

    private bool FilterClient(object obj)
    {
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        if (obj is not InstallEntry entry) return true;
        return entry.Label.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            || entry.StatusLabel.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void TB_Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = TB_Search.Text;
        TB_SearchPlaceholder.Visibility = string.IsNullOrEmpty(_searchText) ? Visibility.Visible : Visibility.Collapsed;
        _clientsView.Refresh();
    }

    private void CB_SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool select = CB_SelectAll.IsChecked == true;
        foreach (var entry in Clients) entry.IsSelected = select;
        _clientsView.Refresh();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Txt_FooterVersion.Text = $"v{GetCurrentVersion().ToString(3)}";
        RefreshClients();
        _procTimer.Start();
        await CheckForUpdatesAsync();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) { ToggleMaximize(); return; }

        // Maximized windows shouldn't be dragged directly; restore first so the
        // cursor lands at a sane spot on the now-smaller window, matching native behavior.
        if (WindowState == WindowState.Maximized)
        {
            var mouse = PointToScreen(e.GetPosition(this));
            WindowState = WindowState.Normal;
            Left = mouse.X - (RestoreBounds.Width / 2);
            Top = mouse.Y - 20;
        }
        DragMove();
    }

    private void Btn_Close_Click(object sender, RoutedEventArgs e) => WpfApplication.Current.Shutdown();
    private void Btn_Min_Click(object sender, RoutedEventArgs e)   => WindowState = WindowState.Minimized;
    private void Btn_Max_Click(object sender, RoutedEventArgs e)   => ToggleMaximize();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            // AllowsTransparency windows overhang the screen edges by the window
            // border/margin when maximized; pull that back in and square off the
            // corners so the content isn't clipped by the monitor bounds.
            WindowRoot.Margin = new Thickness(7);
            WindowRoot.CornerRadius = new CornerRadius(0);
            WindowShadow.Opacity = 0;
            MaxIcon.Data = Geometry.Parse("M6,4H4V22H20V4H6M18,20H6V6H18V20M8,8H16V16H8V8Z");
        }
        else
        {
            WindowRoot.Margin = new Thickness(12);
            WindowRoot.CornerRadius = new CornerRadius(20);
            WindowShadow.Opacity = 0.75;
            MaxIcon.Data = Geometry.Parse("M4,4H20V20H4V4M6,8V18H18V8H6Z");
        }
    }

    private void Nav_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is System.Windows.Controls.RadioButton { IsChecked: true } rb)
        {
            // A force-update lock keeps the user on the Update page until they update.
            if (_forceUpdate && rb.Name != "Nav_Upd")
            {
                Nav_Upd.IsChecked = true;
                return;
            }

            V_Dash.Visibility = rb.Name == "Nav_Dash" ? Visibility.Visible : Visibility.Collapsed;
            V_Proc.Visibility = rb.Name == "Nav_Proc" ? Visibility.Visible : Visibility.Collapsed;
            V_Set.Visibility  = rb.Name == "Nav_Set"  ? Visibility.Visible : Visibility.Collapsed;
            V_Upd.Visibility  = rb.Name == "Nav_Upd"  ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // ── Dashboard / Clients ───────────────────────────────────────────────────

    private void Btn_RefreshDash_Click(object sender, RoutedEventArgs e) => RefreshClients();

    private void RefreshClients()
    {
        Clients.Clear();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var stableDir = FindLatestAppDir(Path.Combine(localAppData, "Discord"));
        if (stableDir != null) Clients.Add(BuildEntry("Discord Stable", Path.Combine(stableDir, "resources")));

        var canaryDir = FindLatestAppDir(Path.Combine(localAppData, "DiscordCanary"));
        if (canaryDir != null) Clients.Add(BuildEntry("Discord Canary", Path.Combine(canaryDir, "resources")));

        var ptbDir = FindLatestAppDir(Path.Combine(localAppData, "DiscordPTB"));
        if (ptbDir != null) Clients.Add(BuildEntry("Discord PTB", Path.Combine(ptbDir, "resources")));

        var devDir = FindLatestAppDir(Path.Combine(localAppData, "DiscordDevelopment"));
        if (devDir != null) Clients.Add(BuildEntry("Discord Development", Path.Combine(devDir, "resources")));

        ClientsCountStr = Clients.Count.ToString();
        
        long totalDisk = 0;
        DateTime lastPatched = DateTime.MinValue;

        foreach (var c in Clients)
        {
            var p = Path.Combine(c.ResourcesDir, "app.asar");
            if (File.Exists(p) && c.Status == "patchcord")
            {
                var fi = new FileInfo(p);
                totalDisk += fi.Length;
                if (fi.LastWriteTime > lastPatched) lastPatched = fi.LastWriteTime;
            }
        }

        DiskFootprintStr = totalDisk > 0 ? $"{totalDisk / 1024 / 1024} MB" : "0 MB";
        LastPatchedStr = lastPatched > DateTime.MinValue ? lastPatched.ToString("g") : "Never";

        Txt_EmptyState.Visibility = Clients.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CB_SelectAll.IsChecked = false;
        _clientsView.Refresh();
    }

    private static string? FindLatestAppDir(string baseDir)
    {
        try
        {
            var dirs = Directory.GetDirectories(baseDir, "app-*")
                .Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^app-\d+\.\d+\.\d+$"))
                .OrderByDescending(d =>
                {
                    var parts = Path.GetFileName(d)[4..].Split('.').Select(int.Parse).ToArray();
                    return parts[0] * 1_000_000 + parts[1] * 1_000 + parts[2];
                }).ToList();
            return dirs.FirstOrDefault();
        }
        catch { return null; }
    }

    private static InstallEntry BuildEntry(string label, string resDir)
    {
        var asarPath = Path.Combine(resDir, "app.asar");
        var backPath = Path.Combine(resDir, "_app.asar");
        string status = "unreadable";

        if (File.Exists(backPath) && File.Exists(asarPath))
        {
            status = InspectAsar(asarPath);
            if (status == "vanilla" || status == "unreadable") status = "patchcord";
        }
        else if (File.Exists(asarPath))
            status = InspectAsar(asarPath);

        return status switch
        {
            "patchcord"     => new InstallEntry { Label = label, ResourcesDir = resDir, Status = status, IsSelected = false,
                StatusLabel = "Patchcord", BadgeBg = BrushOf(0x0C, 0x2A, 0x1A), BadgeFg = BrushOf(0x22, 0xC5, 0x5E) },
            "vencord"       => new InstallEntry { Label = label, ResourcesDir = resDir, Status = status, IsSelected = false,
                StatusLabel = "Vencord", BadgeBg = BrushOf(0x2D, 0x0C, 0x0C), BadgeFg = BrushOf(0xEF, 0x44, 0x44) },
            "betterdiscord" => new InstallEntry { Label = label, ResourcesDir = resDir, Status = status, IsSelected = false,
                StatusLabel = "BetterDiscord", BadgeBg = BrushOf(0x2D, 0x1E, 0x08), BadgeFg = BrushOf(0xF5, 0x9E, 0x0B) },
            _               => new InstallEntry { Label = label, ResourcesDir = resDir, Status = status, IsSelected = true,
                StatusLabel = "Clean", BadgeBg = BrushOf(0x27, 0x27, 0x2A), BadgeFg = BrushOf(0x71, 0x71, 0x7A) },
        };
    }

    private static string InspectAsar(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var pre = new byte[16];
            if (fs.Read(pre, 0, 16) < 16) return "unreadable";
            var strSize = BitConverter.ToUInt32(pre, 12);
            var readLen = (int)Math.Min(strSize, 4096);
            if (readLen == 0) return "unreadable";
            var buf = new byte[readLen];
            fs.Read(buf, 0, readLen);
            var header = Encoding.UTF8.GetString(buf).ToLowerInvariant();
            if (header.Contains("vencord")) return "vencord";
            if (header.Contains("patchcord")) return "patchcord";
            if (header.Contains("betterdiscord")) return "betterdiscord";
            return "vanilla";
        }
        catch { return "unreadable"; }
    }

    private static SolidColorBrush BrushOf(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    // ── Process Manager ───────────────────────────────────────────────────────

    private void RefreshProcesses()
    {
        var exes = new[] { "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment", "Update" };
        var found = new List<DiscordProcess>();
        long totalRam = 0;

        foreach (var name in exes)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    totalRam += p.WorkingSet64;
                    found.Add(new DiscordProcess { Name = p.ProcessName + ".exe", Pid = p.Id, Ram = $"{p.WorkingSet64 / 1024 / 1024} MB" });
                }
            }
            catch { }
        }

        Processes.Clear();
        foreach (var f in found) Processes.Add(f);
        Txt_ProcCount.Text = $"{Processes.Count} Processes Running ({totalRam / 1024 / 1024} MB)";
        TotalRamStr = $"{totalRam / 1024 / 1024} MB";
    }

    private void Btn_KillProc_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is int pid)
        {
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { }
            RefreshProcesses();
        }
    }

    private void Btn_KillAll_Click(object sender, RoutedEventArgs e)
    {
        var exes = new[] { "Discord", "DiscordCanary", "DiscordPTB", "DiscordDevelopment", "Update" };
        foreach (var name in exes)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name)) p.Kill(entireProcessTree: true);
            }
            catch { }
        }
        RefreshProcesses();
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    private void Btn_BrowseAsar_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new WinForms.OpenFileDialog
        {
            Title = "Select a local app.asar file",
            Filter = "asar files (*.asar)|*.asar|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            TB_LocalAsar.Text = dlg.FileName;
    }

    // ── Core Action Logic (Install/Uninstall) ─────────────────────────────────

    private void LogAdd(string prefix, string msg, Color color)
    {
        Dispatcher.Invoke(() =>
        {
            ActionLogs.Add(new LogEntry { Prefix = $"[{prefix}]", Message = msg, Color = new SolidColorBrush(color) });
            LogScroll.ScrollToEnd();
        });
    }

    private async void Btn_PatchSelected_Click(object sender, RoutedEventArgs e) => await RunBulkAction(true);
    private async void Btn_UninstallSelected_Click(object sender, RoutedEventArgs e) => await RunBulkAction(false);
    private void Btn_FinishAction_Click(object sender, RoutedEventArgs e)
    {
        V_Action.Visibility = Visibility.Collapsed;
        RefreshClients();
        if (CB_AutoClose.IsChecked == true) WpfApplication.Current.Shutdown();
    }

    private async Task RunBulkAction(bool install)
    {
        var selected = Clients.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            WpfMessageBox.Show("Please select at least one Discord client to modify.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var verb = install ? "patch" : "uninstall the patch from";
        var confirm = WpfMessageBox.Show(
            $"You're about to {verb} {selected.Count} Discord install(s):\n\n" +
            string.Join("\n", selected.Select(c => $"  •  {c.Label}")) +
            "\n\nAll running Discord processes will be closed first. Continue?",
            install ? "Confirm Patch" : "Confirm Uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        string asarSource = "";
        bool deleteSource = false;

        if (install)
        {
            if (!string.IsNullOrWhiteSpace(TB_LocalAsar.Text))
            {
                asarSource = TB_LocalAsar.Text.Trim();
                if (!File.Exists(asarSource))
                {
                    WpfMessageBox.Show("Local app.asar does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
        }

        V_Action.Visibility = Visibility.Visible;
        Btn_FinishAction.Visibility = Visibility.Collapsed;
        Act_Title.Text = install ? "Patching Clients..." : "Restoring Clients...";
        ActionLogs.Clear();

        try
        {
            LogAdd("STEP", "Closing Discord processes…", Color.FromRgb(0xA8, 0x55, 0xF7));
            Btn_KillAll_Click(null!, null!);
            await Task.Delay(1000);

            if (install && string.IsNullOrEmpty(asarSource))
            {
                LogAdd("STEP", $"Downloading Patchcord from {ASAR_URL}", Color.FromRgb(0x7C, 0x3A, 0xED));
                var tmp = Path.Combine(Path.GetTempPath(), $"patchcord_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.asar");
                using var response = await _http.GetAsync(ASAR_URL, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs);
                fs.Close();
                asarSource = tmp;
                deleteSource = true;
                LogAdd("OK", "Download complete.", Color.FromRgb(0x22, 0xC5, 0x5E));
            }

            foreach (var client in selected)
            {
                LogAdd("STEP", $"Modifying {client.Label}...", Color.FromRgb(0xA8, 0x55, 0xF7));
                await Task.Run(() =>
                {
                    if (install) ApplyPatch(client.ResourcesDir, asarSource);
                    else DoRestoreVanilla(client.ResourcesDir);
                });
                LogAdd("OK", $"Successfully modified {client.Label}", Color.FromRgb(0x22, 0xC5, 0x5E));
            }
        }
        catch (Exception ex)
        {
            LogAdd("ERROR", ex.Message, Color.FromRgb(0xEF, 0x44, 0x44));
        }
        finally
        {
            if (deleteSource && File.Exists(asarSource)) try { File.Delete(asarSource); } catch { }
            LogAdd("DONE", "All operations completed.", Color.FromRgb(0x3B, 0x82, 0xF6));
            Btn_FinishAction.Visibility = Visibility.Visible;
        }
    }

    private void ApplyPatch(string resDir, string downloadedAsar)
    {
        var asarPath   = Path.Combine(resDir, "app.asar");
        var backupPath = Path.Combine(resDir, "_app.asar");

        if (File.Exists(backupPath))
        {
            LogAdd("INFO", "Existing backup found, cleaning before repatch.", Color.FromRgb(0x38, 0xBD, 0xF8));
            if (File.Exists(asarPath)) File.Delete(asarPath);
            var backupKind = InspectAsar(backupPath);
            if (backupKind != "vanilla" && backupKind != "unreadable") TryCleanAsar(resDir, backupPath);
        }
        else
        {
            if (!File.Exists(asarPath)) throw new FileNotFoundException($"app.asar not found in {resDir}");
            var kind = InspectAsar(asarPath);
            if (kind != "vanilla" && kind != "unreadable") TryCleanAsar(resDir, asarPath);
            File.Move(asarPath, backupPath);
        }

        File.Copy(downloadedAsar, asarPath, overwrite: true);
    }

    private void TryCleanAsar(string resDir, string asarPath)
    {
        var siblings = new[] { "app.orig.asar", "app.asar.bak", "original_app.asar" };
        foreach (var s in siblings)
        {
            var p = Path.Combine(resDir, s);
            if (File.Exists(p) && InspectAsar(p) == "vanilla")
            {
                File.Copy(p, asarPath, overwrite: true);
                File.Delete(p);
                return;
            }
        }
    }

    private void DoRestoreVanilla(string resDir)
    {
        var asarPath   = Path.Combine(resDir, "app.asar");
        var backupPath = Path.Combine(resDir, "_app.asar");

        if (!File.Exists(backupPath)) throw new FileNotFoundException("No _app.asar backup found.");
        if (File.Exists(asarPath)) File.Delete(asarPath);
        File.Move(backupPath, asarPath);
    }

    // ── Auto-Updater ──────────────────────────────────────────────────────────

    private static Version GetCurrentVersion()
    {
        // Falls back through a couple of places .NET can surface the <Version> from the csproj.
        var asm = Assembly.GetExecutingAssembly();
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            var plusIdx = infoVer.IndexOf('+');
            if (plusIdx >= 0) infoVer = infoVer[..plusIdx];
            if (Version.TryParse(infoVer, out var v1)) return v1;
        }
        return asm.GetName().Version ?? new Version(0, 0, 0);
    }

    private async Task CheckForUpdatesAsync()
    {
        var current = GetCurrentVersion();
        Upd_CurrentVer.Text = current.ToString(3);

        try
        {
            using var resp = await _http.GetAsync(MANIFEST_URL);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Url))
                throw new InvalidOperationException("Manifest response was malformed.");

            _latestManifest = manifest;
            Upd_LatestVer.Text = manifest.Version;
            Upd_NotesText.Text = string.IsNullOrWhiteSpace(manifest.Notes) ? "No release notes provided." : manifest.Notes;

            if (!Version.TryParse(manifest.Version, out var latest))
                throw new InvalidOperationException($"Manifest version '{manifest.Version}' is not a valid version string.");

            _updateAvailable = latest > current;
            _forceUpdate = _updateAvailable && manifest.ForceUpdate;

            if (_updateAvailable)
            {
                Btn_InstallUpdate.IsEnabled = true;
                Upd_StatusText.Text = _forceUpdate
                    ? $"Version {manifest.Version} is required to continue using Patchcord Installer."
                    : $"Version {manifest.Version} is available.";
                Upd_ForceBanner.Visibility = _forceUpdate ? Visibility.Visible : Visibility.Collapsed;
                UpdDot.Visibility = Visibility.Visible;

                if (_forceUpdate)
                {
                    // Lock the user onto the Update page — no dismiss, no navigating elsewhere.
                    UpdateToast.Visibility = Visibility.Collapsed;
                    Nav_Dash.IsEnabled = false;
                    Nav_Proc.IsEnabled = false;
                    Nav_Set.IsEnabled = false;
                    Nav_Upd.IsChecked = true;
                }
                else
                {
                    Toast_Sub.Text = $"v{manifest.Version} — {(string.IsNullOrWhiteSpace(manifest.Notes) ? "click to view details" : manifest.Notes)}";
                    UpdateToast.Visibility = Visibility.Visible;
                }
            }
            else
            {
                Upd_StatusText.Text = "You're up to date.";
                Upd_NotesText.Text = "";
                Btn_InstallUpdate.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            Upd_LatestVer.Text = "Unknown";
            Upd_StatusText.Text = "Couldn't check for updates.";
            Upd_NotesText.Text = ex.Message;
            Btn_InstallUpdate.IsEnabled = false;
        }
    }

    private async void Btn_CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        Upd_StatusText.Text = "Checking for updates…";
        Upd_LatestVer.Text = "Checking…";
        await CheckForUpdatesAsync();
    }

    private void Toast_Dismiss_Click(object sender, RoutedEventArgs e) => UpdateToast.Visibility = Visibility.Collapsed;

    private void Toast_Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateToast.Visibility = Visibility.Collapsed;
        Nav_Upd.IsChecked = true;
    }

    private async void Btn_InstallUpdate_Click(object sender, RoutedEventArgs e) => await InstallUpdateAsync();

    private async Task InstallUpdateAsync()
    {
        if (_latestManifest is null) return;

        Btn_InstallUpdate.IsEnabled = false;
        Btn_CheckUpdate.IsEnabled = false;
        Upd_Progress.Visibility = Visibility.Visible;
        Upd_Progress.IsIndeterminate = true;
        Upd_StatusText.Text = "Downloading update…";

        string? workDir = null;
        try
        {
            var currentExe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not resolve the running executable path.");

            workDir = Path.Combine(Path.GetTempPath(), $"patchcord_update_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);
            var zipPath = Path.Combine(workDir, "update.zip");
            var extractDir = Path.Combine(workDir, "extracted");

            using (var resp = await _http.GetAsync(_latestManifest.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs);
            }

            Upd_StatusText.Text = "Extracting update…";
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            var newExe = Directory.GetFiles(extractDir, "PatchcordInstaller.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new FileNotFoundException("Update package did not contain an executable.");

            if (!string.IsNullOrWhiteSpace(_latestManifest.Sha256))
            {
                Upd_StatusText.Text = "Verifying update…";
                var actualHash = await ComputeSha256Async(newExe);
                if (!string.Equals(actualHash, _latestManifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Downloaded update failed hash verification. Aborting for safety.");
            }

            Upd_StatusText.Text = "Restarting to finish installing…";

            // The running exe can't overwrite itself, so a tiny detached helper script
            // waits for this process to exit, swaps the file, and relaunches it.
            var scriptPath = Path.Combine(workDir, "apply_update.cmd");
            var script =
                $"@echo off\r\n" +
                $":wait\r\n" +
                $"tasklist /fi \"PID eq {Environment.ProcessId}\" | find \"{Environment.ProcessId}\" >nul\r\n" +
                $"if not errorlevel 1 (\r\n" +
                $"  timeout /t 1 /nobreak >nul\r\n" +
                $"  goto wait\r\n" +
                $")\r\n" +
                $"copy /y \"{newExe}\" \"{currentExe}\" >nul\r\n" +
                $"start \"\" \"{currentExe}\"\r\n" +
                $"rmdir /s /q \"{workDir}\"\r\n";
            await File.WriteAllTextAsync(scriptPath, script);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{scriptPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            WpfApplication.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Upd_Progress.Visibility = Visibility.Collapsed;
            Upd_StatusText.Text = "Update failed.";
            Upd_NotesText.Text = ex.Message;
            Btn_InstallUpdate.IsEnabled = true;
            Btn_CheckUpdate.IsEnabled = true;
            if (workDir != null && Directory.Exists(workDir)) try { Directory.Delete(workDir, true); } catch { }
        }
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public class InstallEntry : INotifyPropertyChanged
{
    public required string Label { get; set; }
    public required string ResourcesDir { get; set; }
    public required string Status { get; set; }
    public required string StatusLabel { get; set; }
    public SolidColorBrush? BadgeBg { get; set; }
    public SolidColorBrush? BadgeFg { get; set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class DiscordProcess
{
    public required string Name { get; set; }
    public required int Pid { get; set; }
    public required string Ram { get; set; }
}

public class LogEntry
{
    public required string Prefix { get; set; }
    public required string Message { get; set; }
    public SolidColorBrush? Color { get; set; }
}

public class UpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    // Optional field: manifest.json can set "forceUpdate": true to require the
    // update before the rest of the app becomes usable.
    [JsonPropertyName("forceUpdate")]
    public bool ForceUpdate { get; set; } = false;
}
