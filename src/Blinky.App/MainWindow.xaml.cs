using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Blinky.Core;
using NAudio.CoreAudioApi;
using Forms = System.Windows.Forms;

namespace Blinky.App;

public partial class MainWindow : Window
{
    private readonly string _dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blinky");
    private readonly RecordingService _recording;
    private readonly AudioWatch _audio = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _trayRecord, _trayPause;
    private readonly Forms.ToolStripMenuItem _meetingRecord;
    private readonly MeetingPolicy _meetingPolicy = new();
    private bool _meetingScanRunning, _meetingPromptPending, _meetingBalloon;
    private readonly Icon _readyIcon, _recordIcon;
    private Settings _settings;
    private bool _loading = true, _transition, _quitting;
    private bool _refreshingAudio;
    private int _tick;
    private HwndSource? _hwnd;
    private Task? _optimizationTask;
    private CancellationTokenSource? _optimizationCancellation;
    private bool _optimizing;
    private string? _optimizationSource, _optimizedPath;
    private string _optimizationCancelMessage = "Optimization cancelled. Original kept.";
    private string SettingsPath => Path.Combine(_dataFolder, "settings.json");
    private string JournalPath => Path.Combine(_dataFolder, "session.json");

    public MainWindow()
    {
        InitializeComponent();
        _settings = (JsonStore.Load<Settings>(SettingsPath) ?? new Settings()).Validated();
        _recording = new RecordingService(Dispatcher, JournalPath);
        _recording.Changed += RenderState;
        _recording.Failed += Alert;
        _audio.Failed += failure => Dispatcher.BeginInvoke(async () =>
        {
            // A refresh or intentional shutdown may retire this session before
            // this second dispatcher callback gets to execute.
            if (!_audio.IsCurrent(failure)) return;
            if (_recording.IsBusy) await _recording.StopAsync(failure.Message);
            else StatusText.Text = failure.Message;
        });
        _readyIcon = CreateTrayIcon(false);
        _recordIcon = CreateTrayIcon(true);
        _tray = new Forms.NotifyIcon { Icon = _readyIcon, Text = "Blinky · Ready", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Blinky", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        _trayRecord = new Forms.ToolStripMenuItem("Start recording", null, (_, _) => Dispatcher.InvokeAsync(ToggleRecording));
        _trayPause = new Forms.ToolStripMenuItem("Pause", null, (_, _) => Dispatcher.Invoke(() => _recording.TogglePause())) { Enabled = false };
        menu.Items.Add(_trayRecord); menu.Items.Add(_trayPause);
        _meetingRecord = new Forms.ToolStripMenuItem("Record detected meeting (audio only)", null, (_, _) => Dispatcher.InvokeAsync(StartMeetingRecordingAsync)) { Enabled = false };
        menu.Items.Add(_meetingRecord);
        menu.Items.Add("Open recordings", null, (_, _) => Dispatcher.Invoke(OpenFolder));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Blinky", null, (_, _) => Dispatcher.InvokeAsync(QuitAsync));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        _tray.BalloonTipClicked += (_, _) =>
        {
            bool acceptedMeeting = _meetingBalloon && _meetingPromptPending;
            Dispatcher.InvokeAsync(async () => { if (acceptedMeeting) await StartMeetingRecordingAsync(); else ShowWindow(); });
        };
        ProfilePicker.ItemsSource = RecordingProfile.All;
        ProfilePicker.SelectedItem = RecordingProfile.All.First(p => p.Name == _settings.Profile);
        FolderText.Text = _settings.OutputFolder;
        CodecPicker.SelectedIndex = _settings.UseHevc ? 1 : 0;
        HardwareCheck.IsChecked = _settings.HardwareEncoding;
        MicVolume.Value = _settings.MicrophoneVolume; SystemVolume.Value = _settings.SystemVolume;
        ModePicker.SelectedIndex = _settings.AudioOnly ? 1 : 0;
        MeetingPromptsCheck.IsChecked = _settings.MeetingPrompts;
        BrowserMeetingsCheck.IsChecked = _settings.IncludeBrowserMeetings;
        foreach (ComboBoxItem item in MeetingDelayPicker.Items)
            if (int.Parse((string)item.Tag) == _settings.MeetingEndDelaySeconds) MeetingDelayPicker.SelectedItem = item;
        _timer.Tick += Tick;
        Loaded += OnLoaded;
        SourceInitialized += (_, _) => RegisterShortcuts();
        Closing += OnClosing;
        StateChanged += (_, _) => { MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□"; if (WindowState == WindowState.Minimized) HideToTray(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshSources();
        _loading = false;
        ApplyRecordingMode();
        Width = Math.Min(1140, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(870, SystemParameters.WorkArea.Height - 24);
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
        await OpenAudioAsync();
        _timer.Start();
        var previous = JsonStore.Load<SessionJournal>(JournalPath);
        if (previous?.State == "complete" && File.Exists(previous.FilePath) && previous.FilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) SetOptimizationSource(previous.FilePath);
        if (previous?.State is "recording" or "interrupted")
        {
            Alert("An earlier recording was interrupted. Its partial file has been kept at:\n" + previous.FilePath + "\n\nTry opening it in VLC. The last unfinished fragment may be missing.");
        }
    }

    private void RefreshSources()
    {
        try
        {
            _loading = true;
            var micId = (MicPicker.SelectedItem as AudioDevice)?.Id ?? _settings.MicrophoneId;
            var systemId = (SystemPicker.SelectedItem as AudioDevice)?.Id ?? _settings.SystemAudioId;
            var oldTarget = (TargetPicker.SelectedItem as CaptureTarget)?.Name;
            var targets = CaptureTarget.Discover(new WindowInteropHelper(this).Handle);
            TargetPicker.ItemsSource = targets;
            TargetPicker.SelectedItem = targets.FirstOrDefault(t => t.Name == oldTarget) ?? targets.FirstOrDefault();
            var mics = AudioWatch.Devices(DataFlow.Capture);
            var outputs = AudioWatch.Devices(DataFlow.Render);
            MicPicker.ItemsSource = mics; SystemPicker.ItemsSource = outputs;
            MicPicker.SelectedItem = mics.FirstOrDefault(d => d.Id == micId) ?? mics.FirstOrDefault();
            SystemPicker.SelectedItem = outputs.FirstOrDefault(d => d.Id == systemId) ?? outputs.FirstOrDefault();
        }
        catch (Exception e) { Alert("Could not discover recording devices: " + e.Message); }
        finally { _loading = false; }
    }

    private async Task OpenAudioAsync()
    {
        if (_refreshingAudio || _recording.IsBusy) return;
        _refreshingAudio = true;
        RecordButton.IsEnabled = false;
        try
        {
            if (MicPicker.SelectedItem is not AudioDevice mic || SystemPicker.SelectedItem is not AudioDevice output)
                throw new InvalidOperationException("Connect a microphone and a system audio output, then refresh sources.");
            _audio.Open(mic.Id, output.Id);
            await Task.Delay(400);
            _audio.Verify();
            StatusText.Text = "Audio ready · speak and play sound to check both meters.";
        }
        catch (Exception e) { StatusText.Text = "Audio unavailable · " + e.Message; }
        finally { _refreshingAudio = false; RecordButton.IsEnabled = true; }
    }

    private Settings ReadSettings() => new Settings
    {
        OutputFolder = Path.GetFullPath(FolderText.Text.Trim()),
        Profile = (ProfilePicker.SelectedItem as RecordingProfile)?.Name ?? "Balanced",
        UseHevc = CodecPicker.SelectedIndex == 1,
        HardwareEncoding = HardwareCheck.IsChecked == true,
        MicrophoneId = (MicPicker.SelectedItem as AudioDevice)?.Id,
        SystemAudioId = (SystemPicker.SelectedItem as AudioDevice)?.Id,
        MicrophoneVolume = MicVolume.Value,
        SystemVolume = SystemVolume.Value,
        AudioOnly = ModePicker.SelectedIndex == 1,
        MeetingPrompts = MeetingPromptsCheck.IsChecked == true,
        IncludeBrowserMeetings = BrowserMeetingsCheck.IsChecked == true,
        MeetingEndDelaySeconds = int.Parse((string)((ComboBoxItem)MeetingDelayPicker.SelectedItem).Tag)
    }.Validated();

    private async Task ToggleRecording()
    {
        if (_transition || _refreshingAudio || _quitting) return;
        _transition = true;
        try
        {
            if (_recording.IsBusy) { await _recording.StopAsync(); return; }
            var target = TargetPicker.SelectedItem as CaptureTarget;
            if (ModePicker.SelectedIndex == 0 && target == null) throw new InvalidOperationException("Choose a screen or window to record.");
            await CancelOptimizationAsync("Optimization cancelled to prioritize recording. Original kept.");
            SettingsPanel.IsEnabled = false;
            UpdateOptimizationControls();
            _settings = ReadSettings();
            JsonStore.Save(SettingsPath, _settings);
            await OpenAudioAsync();
            _audio.Verify();
            if (_settings.AudioOnly) _recording.StartAudio(_settings);
            else
            {
                var size = target!.CurrentSize();
                _recording.Start(_settings, target.CreateSource(), size.Width, size.Height);
            }
        }
        catch (Exception e) { Alert(e.Message); }
        finally
        {
            _transition = false;
            SettingsPanel.IsEnabled = !_recording.IsBusy;
            UpdateOptimizationControls();
        }
    }

    private void RenderState(string message)
    {
        var state = _recording.State;
        StatusText.Text = message;
        StateBadge.Text = state.ToString().ToUpperInvariant();
        SettingsPanel.IsEnabled = !_recording.IsBusy;
        RecordButton.Content = _recording.IsBusy ? "■  Stop and save" : "●  Start recording";
        RecordButton.IsEnabled = state != CaptureState.Stopping;
        PauseButton.IsEnabled = state is CaptureState.Recording or CaptureState.Paused;
        PauseButton.Content = state == CaptureState.Paused ? "Resume" : "Pause";
        _trayRecord.Text = _recording.IsBusy ? "Stop and save" : "Start recording";
        _trayRecord.Enabled = RecordButton.IsEnabled;
        _trayPause.Enabled = PauseButton.IsEnabled;
        _trayPause.Text = state == CaptureState.Paused ? "Resume" : "Pause";
        _tray.Icon = _recording.IsBusy ? _recordIcon : _readyIcon;
        _tray.Text = "Blinky · " + state;
        if (state == CaptureState.Ready) _meetingPolicy.ReleaseRecording();
        if (state == CaptureState.Ready && _recording.FilePath != null && File.Exists(_recording.FilePath)
            && _recording.FilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            && !_recording.FilePath.EndsWith(".recording.mp4", StringComparison.OrdinalIgnoreCase)) SetOptimizationSource(_recording.FilePath);
        UpdateOptimizationControls();
        if (state == CaptureState.Ready && _recording.FilePath != null)
        {
            Notify("Blinky", message, Forms.ToolTipIcon.Info);
            if (!IsVisible) _audio.Suspend();
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        MicMeter.Value = Math.Clamp(_audio.MicPeak * MicVolume.Value, 0, 1);
        SystemMeter.Value = Math.Clamp(_audio.SystemPeak * SystemVolume.Value, 0, 1);
        var elapsed = _recording.Elapsed;
        ClockText.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        ++_tick;
        if (_tick % 20 == 0) _ = ScanMeetingsAsync();
        if (_tick % 7 != 0) return;
        try
        {
            var folder = _recording.IsBusy ? Path.GetDirectoryName(_recording.FilePath)! : FolderText.Text;
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
            StorageText.Text = $"{_recording.FileBytes / 1_000_000d:0.0} MB recorded   ·   {drive.AvailableFreeSpace / 1_000_000_000d:0.0} GB free";
            if (_recording.IsBusy)
            {
                _audio.Verify();
                _recording.CheckDisk();
            }
        }
        catch (Exception error)
        {
            if (_recording.IsBusy) _ = _recording.StopAsync("Recording stopped: " + error.Message);
            else StorageText.Text = "Choose an accessible local recording folder.";
        }
    }

    private void Alert(string message)
    {
        StatusText.Text = message;
        Notify("Blinky needs attention", message, Forms.ToolTipIcon.Warning);
        ShowWindow();
        System.Windows.MessageBox.Show(this, message, "Blinky", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowWindow()
    {
        bool wasHidden = !IsVisible;
        Show(); WindowState = WindowState.Normal; Activate();
        if (wasHidden && !_recording.IsBusy) _ = OpenAudioAsync();
    }
    private void OpenFolder()
    {
        try
        {
            var folder = _recording.FilePath == null ? Path.GetFullPath(FolderText.Text) : Path.GetDirectoryName(_recording.FilePath)!;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception e) { Alert("Could not open recordings: " + e.Message); }
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        if (_transition || _refreshingAudio)
        {
            ShowWindow();
            StatusText.Text = "Finishing the current operation. Quit again when it completes.";
            return;
        }
        _quitting = true;
        try
        {
            await CancelOptimizationAsync("Optimization cancelled while quitting. Original kept.");
            await _recording.StopAsync();
            try { JsonStore.Save(SettingsPath, ReadSettings()); } catch (Exception) { /* A saved recording takes priority over preferences. */ }
            _timer.Stop(); _audio.Dispose(); _recording.Dispose();
            _tray.Visible = false; _tray.Dispose(); _readyIcon.Dispose(); _recordIcon.Dispose();
            if (_hwnd != null) { UnregisterHotKey(_hwnd.Handle, 1); UnregisterHotKey(_hwnd.Handle, 2); _hwnd.RemoveHook(HotkeyHook); }
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception e) { _quitting = false; Alert("Could not finish closing: " + e.Message); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true; HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        if (!_recording.IsBusy) { _audio.Suspend(); StatusText.Text = "Ready in the system tray."; }
    }

    private void RegisterShortcuts()
    {
        _hwnd = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwnd.AddHook(HotkeyHook);
        bool record = RegisterHotKey(_hwnd.Handle, 1, 0x4000 | 0x0002 | 0x0004, 0x78);
        bool pause = RegisterHotKey(_hwnd.Handle, 2, 0x4000 | 0x0002 | 0x0004, 0x79);
        if (!record || !pause) ShortcutText.Text = "A global shortcut is in use by another app. Tray controls are available.";
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312) return IntPtr.Zero;
        handled = true;
        if (wParam.ToInt32() == 1) _ = ToggleRecording();
        else if (wParam.ToInt32() == 2) _recording.TogglePause();
        return IntPtr.Zero;
    }

    private static Icon CreateTrayIcon(bool recording)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(recording ? Color.FromArgb(255, 85, 75) : Color.FromArgb(143, 216, 174));
        graphics.FillEllipse(brush, 4, 4, 24, 24);
        var handle = bitmap.GetHicon();
        using var icon = System.Drawing.Icon.FromHandle(handle);
        var clone = (Icon)icon.Clone();
        DestroyIcon(handle);
        return clone;
    }

    private async void Record_Click(object sender, RoutedEventArgs e) => await ToggleRecording();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
    private void Mode_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) ApplyRecordingMode(); }
    private void ApplyRecordingMode()
    {
        bool audio = ModePicker.SelectedIndex == 1;
        TargetPicker.IsEnabled = !audio;
        VideoSettingsPanel.IsEnabled = !audio;
        HardwareCheck.IsEnabled = !audio;
        if (audio) ProfileHelp.Text = "Audio only · mono AAC at 96 kbps · about 43 MB/hour · no screen capture.";
        else if (ProfilePicker.SelectedItem is RecordingProfile profile) ProfileHelp.Text = $"Up to 1080p · {profile.FramesPerSecond} fps · size varies with content.";
    }

    private async Task ScanMeetingsAsync()
    {
        if (_meetingScanRunning || _quitting) return;
        if (MeetingPromptsCheck.IsChecked != true)
        {
            _meetingPolicy.Reset(); _meetingPromptPending = false; _meetingRecord.Enabled = false;
            MeetingStatusText.Text = "Meeting prompts are off.";
            return;
        }
        _meetingScanRunning = true;
        try
        {
            bool browsers = BrowserMeetingsCheck.IsChecked == true;
            var signal = await Task.Run(() => MeetingDetector.Scan(browsers));
            if (_quitting || MeetingPromptsCheck.IsChecked != true) return;
            int delay = int.Parse((string)((ComboBoxItem)MeetingDelayPicker.SelectedItem).Tag);
            var action = _meetingPolicy.Observe(signal.Active, _recording.IsBusy || _transition, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(delay));
            MeetingStatusText.Text = signal.Description + ". Prompt recordings stop after " + delay + " seconds without meeting mic activity.";
            if (action == MeetingAction.OfferRecording)
            {
                _meetingPromptPending = true; _meetingRecord.Enabled = true;
                _meetingBalloon = true;
                _tray.ShowBalloonTip(10000, "Record this meeting?", signal.Description + ". Click to record microphone + system audio only. No overlay.", Forms.ToolTipIcon.Info);
            }
            if (!_meetingPolicy.HasMeeting) { _meetingPromptPending = false; _meetingRecord.Enabled = false; }
            if (action == MeetingAction.StopManagedRecording && !_transition)
                await _recording.StopAsync("Meeting microphone activity ended. Recording saved.", notifyAsFailure: false);
        }
        finally { _meetingScanRunning = false; }
    }

    private async Task StartMeetingRecordingAsync()
    {
        if (!_meetingPromptPending || _recording.IsBusy || _transition || _quitting) return;
        _meetingPromptPending = false; _meetingRecord.Enabled = false;
        ModePicker.SelectedIndex = 1;
        _meetingPolicy.AcceptPrompt();
        await ToggleRecording();
        if (!_recording.IsBusy) _meetingPolicy.ReleaseRecording();
    }

    private void Automation_Changed(object sender, RoutedEventArgs e) => SaveAutomationPreferences();
    private void MeetingDelay_Changed(object sender, SelectionChangedEventArgs e) => SaveAutomationPreferences();
    private void SaveAutomationPreferences()
    {
        if (_loading) return;
        try
        {
            _settings = ReadSettings(); JsonStore.Save(SettingsPath, _settings);
            if (!_settings.MeetingPrompts) { _meetingPolicy.Reset(); _meetingPromptPending = false; _meetingRecord.Enabled = false; }
        }
        catch (Exception error) { MeetingStatusText.Text = "Could not save meeting preferences: " + error.Message; }
    }

    private void McpSetup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.Combine(_dataFolder, "mcp-config");
            Directory.CreateDirectory(directory);
            var executable = Path.Combine(AppContext.BaseDirectory, "mcp", "Blinky.Mcp.exe");
            McpClientConfiguration.Write(directory, executable, Path.GetFullPath(FolderText.Text), _dataFolder);
            var guide = Path.Combine(AppContext.BaseDirectory, "MCP-SETUP.md");
            if (File.Exists(guide)) File.Copy(guide, Path.Combine(directory, "MCP-SETUP.md"), overwrite: true);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { directory }, UseShellExecute = false });
        }
        catch (Exception error) { Alert("Could not prepare MCP setup: " + error.Message); }
    }
    private void SetOptimizationSource(string path)
    {
        _optimizationSource = path;
        OptimizationSourceText.Text = Path.GetFileName(path);
        OptimizationSourceText.ToolTip = path;
        _optimizedPath = null;
        OpenOptimizedButton.Visibility = Visibility.Collapsed;
        UpdateOptimizationControls();
    }

    private void UpdateOptimizationControls()
    {
        bool available = !_recording.IsBusy && !_transition && !_quitting && !_optimizing;
        OptimizeButton.IsEnabled = available && _optimizationSource != null;
        ChooseOptimizationButton.IsEnabled = available;
        CancelOptimizationButton.IsEnabled = _optimizing && !_quitting;
    }

    private void ChooseOptimization_Click(object sender, RoutedEventArgs e)
    {
        if (_optimizing || _recording.IsBusy || _transition || _quitting) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose a finished recording", Filter = "MP4 recordings (*.mp4)|*.mp4", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) == true)
        {
            SetOptimizationSource(picker.FileName);
            OptimizationStatusText.Text = "Ready to create a smaller HEVC copy. The original will be kept.";
        }
    }

    private async void Optimize_Click(object sender, RoutedEventArgs e)
    {
        if (_optimizing || _recording.IsBusy || _transition || _quitting || _optimizationSource == null) return;
        _optimizationTask = OptimizeAsync(_optimizationSource);
        await _optimizationTask;
    }

    private async Task OptimizeAsync(string source)
    {
        _optimizing = true;
        var cancellation = new CancellationTokenSource();
        _optimizationCancellation = cancellation;
        _optimizationCancelMessage = "Optimization cancelled. Original kept.";
        _optimizedPath = null;
        OpenOptimizedButton.Visibility = Visibility.Collapsed;
        OptimizationBar.Visibility = Visibility.Visible;
        OptimizationBar.IsIndeterminate = true;
        UpdateOptimizationControls();
        var progress = new Progress<OptimizationProgress>(value =>
        {
            if (_optimizationCancellation != cancellation || cancellation.IsCancellationRequested) return;
            OptimizationStatusText.Text = value.Stage + (value.Percent is double percent ? $" {percent:0}%" : "");
            OptimizationBar.IsIndeterminate = value.Percent == null;
            if (value.Percent is double amount) OptimizationBar.Value = amount;
        });
        try
        {
            if (!File.Exists(source) || source.EndsWith(".recording.mp4", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose an existing, finished recording first.");
            // The encoder does its work in a separate process; continuations
            // return here to update WPF without blocking its message loop.
            var tools = await OptimizerTools.EnsureAsync(Path.Combine(_dataFolder, "tools"), progress, cancellation.Token);
            var result = await new MediaOptimizer(tools).OptimizeAsync(source, progress, cancellation.Token);
            if (result.IsSmaller)
            {
                _optimizedPath = result.OutputPath;
                OptimizationStatusText.Text = $"Saved {result.SavedPercent:0.#}% · {result.OriginalBytes / 1_000_000d:0.#} MB → {result.OutputBytes / 1_000_000d:0.#} MB. Original kept.";
                OpenOptimizedButton.Visibility = Visibility.Visible;
                Notify("Smaller copy ready", OptimizationStatusText.Text, Forms.ToolTipIcon.Info);
            }
            else OptimizationStatusText.Text = "The new encode was not smaller. No extra copy kept; original unchanged.";
        }
        catch (OperationCanceledException) { OptimizationStatusText.Text = _optimizationCancelMessage; }
        catch (Exception error)
        {
            OptimizationStatusText.Text = "Could not create a smaller copy: " + error.Message + " Original kept.";
            Notify("Optimization needs attention", OptimizationStatusText.Text, Forms.ToolTipIcon.Warning);
        }
        finally
        {
            _optimizationCancellation = null;
            cancellation.Dispose();
            _optimizing = false;
            OptimizationBar.Visibility = Visibility.Collapsed;
            UpdateOptimizationControls();
        }
    }

    private async Task CancelOptimizationAsync(string message)
    {
        if (!_optimizing) return;
        _optimizationCancelMessage = message;
        _optimizationCancellation?.Cancel();
        if (_optimizationTask != null) await _optimizationTask;
    }

    private void Notify(string title, string message, Forms.ToolTipIcon icon)
    {
        _meetingBalloon = false;
        _tray.ShowBalloonTip(6000, title, message, icon);
    }

    private async void CancelOptimization_Click(object sender, RoutedEventArgs e) => await CancelOptimizationAsync("Optimization cancelled. Original kept.");
    private void OpenOptimized_Click(object sender, RoutedEventArgs e)
    {
        if (_optimizedPath == null) return;
        try { Process.Start(new ProcessStartInfo(_optimizedPath) { UseShellExecute = true }); }
        catch (Exception error) { OptimizationStatusText.Text = "Could not open the smaller copy: " + error.Message; }
    }
    private void Pause_Click(object sender, RoutedEventArgs e) => _recording.TogglePause();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder();
    private async void Refresh_Click(object sender, RoutedEventArgs e) { RefreshSources(); await OpenAudioAsync(); }
    private async void Audio_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) await OpenAudioAsync(); }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var picker = new Forms.FolderBrowserDialog { Description = "Choose where Blinky saves recordings", UseDescriptionForTitle = true };
        if (picker.ShowDialog() == Forms.DialogResult.OK) FolderText.Text = picker.SelectedPath;
    }
    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilePicker.SelectedItem is RecordingProfile profile)
            ProfileHelp.Text = $"Up to 1080p · {profile.FramesPerSecond} fps · quality-based encoding; size varies with content.";
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
