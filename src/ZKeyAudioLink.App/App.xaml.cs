using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZKeyAudioLink.App.Services;
using ZKeyAudioLink.App.ViewModels;
using ZKeyAudioLink.Core;
using ZKeyAudioLink.Core.Audio;
using ZKeyAudioLink.Core.Configuration;
using ZKeyAudioLink.Core.Diagnostics;
using ZKeyAudioLink.Core.Hotkeys;
using ZKeyAudioLink.Core.Media;
using ZKeyAudioLink.Core.Playback;

namespace ZKeyAudioLink.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppLog? _log;
    private SettingsStore? _store;
    private MediaLibrary? _library;
    private AudioEngine? _engine;
    private PlaybackController? _controller;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private RawInputHotkeyService? _hotkey;
    private TrayIcon? _tray;
    private bool _exitStarted;
    private bool _cleanedUp;

    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 2 && e.Args[0] == "--screenshot")
        {
            RunScreenshotMode(e.Args[1]);
            return;
        }

        if (e.Args.Contains("--smoke"))
        {
            _ = RunSmokeModeAsync();
            return;
        }

        try
        {
            AppPaths.EnsureCreated();
            _log = new AppLog();

            _mutex = new Mutex(initiallyOwned: true, "ZKeyAudioLink.SingleInstance", out var isFirstInstance);
            if (!isFirstInstance)
            {
                MessageBox.Show("Z 键音频连麦已经在运行，请从任务栏托盘打开它。", "Z 键音频连麦",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            SessionEnding += OnSessionEnding;

            _store = new SettingsStore(log: _log);
            _store.Load();
            _library = new MediaLibrary(_store, _log);
            _engine = new AudioEngine(_store, _log);
            _controller = new PlaybackController(_engine, () => _library.Selected, _log);
            _viewModel = new MainViewModel(_engine, _controller, _library, _store, _log, Dispatcher);

            _window = new MainWindow(_viewModel);
            RestoreWindowGeometry();
            _window.Show();

            _tray = new TrayIcon();
            _tray.ShowRequested += OnTrayShowRequested;
            _tray.ExitRequested += OnTrayExitRequested;

            _hotkey = new RawInputHotkeyService();
            _hotkey.SetKey(HotkeyKeys.GetOrDefault(_store.Settings.HotkeyKey));
            _hotkey.ToggleRequested += OnHotkeyToggleRequested;
            _hotkey.Failed += OnHotkeyFailed;
            _viewModel.HotkeyEnabledChanged += OnHotkeyEnabledChanged;
            _viewModel.HotkeyKeyChanged += OnHotkeyKeyChanged;

            _log.Info("application started", new
            {
                version = typeof(App).Assembly.GetName().Version?.ToString(),
                os = Environment.OSVersion.VersionString,
                arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                dataRoot = AppPaths.Root,
                portable = AppPaths.IsPortable
            });

            _ = StartEngineAsync();
        }
        catch (Exception ex)
        {
            _log?.Error("startup failed", ex);
            MessageBox.Show($"启动失败：{ex.Message}", "Z 键音频连麦", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task StartEngineAsync()
    {
        try
        {
            await _engine!.StartAsync();
            _viewModel!.InitializeAfterEngineStart();
            _hotkey!.SetEnabled(_store!.Settings.HotkeyEnabled);
            ShowStartupWarnings();
        }
        catch (Exception ex)
        {
            _log!.Error("audio engine start failed", ex);
            _viewModel!.ShowExternalError($"音频引擎启动失败：{ex.Message}");
        }
    }

    private void ShowStartupWarnings()
    {
        if (_store!.LoadWarning is { Length: > 0 } warning)
        {
            _viewModel!.ShowExternalError(warning);
        }

        var missing = _library!.FindMissingFiles();
        if (missing.Count > 0)
        {
            _viewModel!.ShowExternalError(
                $"有 {missing.Count} 个库内音频文件缺失，相关条目无法播放。请重新导入或从库里删除这些条目。");
        }

        var status = _engine!.Status;
        if (!status.SendAvailable)
        {
            _viewModel!.ShowExternalError(
                "未检测到可用的虚拟发送端（CABLE Input）。请先安装基础版 VB-CABLE 并重启，再用“重新连接”恢复。");
        }
        else if (!status.MonitorAvailable)
        {
            _viewModel!.ShowExternalError("没有可用的耳机输出，按下 Z 时素材会被阻止播放。请选择耳机输出后重新连接。");
        }
        else if (!status.MicrophoneAvailable)
        {
            _viewModel!.ShowExternalError("麦克风当前不可用，对方只能听到素材。请检查麦克风选择和 Windows 隐私权限。");
        }
    }

    private void OnHotkeyToggleRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnHotkeyToggleRequested));
            return;
        }

        _ = ToggleFromHotkeyAsync();
    }

    private async Task ToggleFromHotkeyAsync()
    {
        try
        {
            await _controller!.ToggleAsync();
        }
        catch (Exception ex)
        {
            _log?.Error("hotkey toggle failed", ex);
            _viewModel?.ShowExternalError($"按 Z 切换播放失败：{ex.Message}");
        }
    }

    private void OnHotkeyFailed(string message)
    {
        Dispatcher.BeginInvoke(new Action(() => _viewModel?.OnHotkeyRegistrationFailed(message)));
    }

    private void OnHotkeyEnabledChanged(bool enabled)
    {
        _hotkey?.SetEnabled(enabled);
    }

    private void OnHotkeyKeyChanged(HotkeyKey key)
    {
        _hotkey?.SetKey(key);
    }

    public void HandleWindowHidden(MainWindow window)
    {
        SaveWindowGeometry();
        if (_store is not null && !_store.Settings.TrayHintShown)
        {
            _store.Settings.TrayHintShown = true;
            _store.Save();
            _tray?.ShowBalloon("Z 键音频连麦仍在运行", "窗口已隐藏到托盘，全局 Z 继续有效。完全退出会停止麦克风转发，退出前请把 QQ / OOPZ 的输入切回真实麦克风。");
        }
    }

    private void OnTrayShowRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnTrayShowRequested));
            return;
        }

        _window?.ShowFromTray();
        _window?.Activate();
    }

    private void OnTrayExitRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnTrayExitRequested));
            return;
        }

        _ = ExitApplicationAsync();
    }

    private async Task ExitApplicationAsync()
    {
        if (_exitStarted)
        {
            return;
        }

        _exitStarted = true;
        IsExiting = true;
        try
        {
            SaveWindowGeometry();
            _window?.Close();
            _window = null;
            _viewModel?.Dispose();
            _viewModel = null;
            _hotkey?.Dispose();
            _hotkey = null;
            _tray?.Dispose();
            _tray = null;
            _controller?.Dispose();
            _controller = null;

            if (_engine is not null)
            {
                await _engine.DisposeAsync();
                _engine = null;
            }

            _library?.Dispose();
            _library = null;
            _store?.Save();
            _log?.Info("application exited");
            _store = null;
            _log?.Dispose();
            _log = null;
            _cleanedUp = true;
        }
        catch (Exception ex)
        {
            try
            {
                _log?.Error("exit failed", ex);
            }
            catch
            {
                // ignore
            }
        }
        finally
        {
            ReleaseMutex();
            Shutdown();
        }
    }

    private void RestoreWindowGeometry()
    {
        if (_window is null || _store is null)
        {
            return;
        }

        var settings = _store.Settings;
        if (settings.WindowWidth >= 900 && settings.WindowHeight >= 600)
        {
            _window.Width = settings.WindowWidth;
            _window.Height = settings.WindowHeight;
        }

        if (settings.WindowLeft is { } savedLeft && settings.WindowTop is { } savedTop)
        {
            var left = savedLeft;
            var top = savedTop;
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualWidth = SystemParameters.VirtualScreenWidth;
            var virtualHeight = SystemParameters.VirtualScreenHeight;

            left = Math.Max(virtualLeft, Math.Min(left, virtualLeft + virtualWidth - 160));
            top = Math.Max(virtualTop, Math.Min(top, virtualTop + virtualHeight - 120));
            _window.Left = left;
            _window.Top = top;
        }
    }

    private void SaveWindowGeometry()
    {
        if (_window is null || _store is null)
        {
            return;
        }

        var bounds = _window.WindowState == WindowState.Maximized
            ? _window.RestoreBounds
            : new Rect(_window.Left, _window.Top, _window.Width, _window.Height);
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            _store.Settings.WindowWidth = bounds.Width;
            _store.Settings.WindowHeight = bounds.Height;
            _store.Settings.WindowLeft = bounds.Left;
            _store.Settings.WindowTop = bounds.Top;
            _store.Save();
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("unhandled UI exception", e.Exception);
        _viewModel?.ShowExternalError($"发生未处理错误：{e.Exception.Message}");
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _log?.Error("unhandled exception", ex);
        }
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        SaveWindowGeometry();
        _store?.Save();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CleanupOnExit();
        base.OnExit(e);
    }

    private void CleanupOnExit()
    {
        if (_cleanedUp)
        {
            return;
        }

        IsExiting = true;
        _cleanedUp = true;
        try
        {
            SaveWindowGeometry();
            _viewModel?.Dispose();
            _hotkey?.Dispose();
            _tray?.Dispose();
            _controller?.Dispose();
            if (_engine is not null)
            {
                _engine.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(4));
            }

            _library?.Dispose();
            _store?.Save();
            _log?.Dispose();
        }
        catch
        {
            // exiting anyway
        }
        finally
        {
            ReleaseMutex();
        }
    }

    private void ReleaseMutex()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // not owned
        }

        _mutex.Dispose();
        _mutex = null;
    }

    /// <summary>
    /// Development smoke test: starts the real audio engine without a window,
    /// logs the resulting device status, then exits. Used to verify device
    /// startup on a machine without the virtual cable installed.
    /// </summary>
    private async Task RunSmokeModeAsync()
    {
        AppLog? smokeLog = null;
        AudioEngine? smokeEngine = null;
        RawInputHotkeyService? smokeHotkey = null;
        try
        {
            AppPaths.EnsureCreated();
            smokeLog = new AppLog();
            smokeLog.Info("data root", new { root = AppPaths.Root, portable = AppPaths.IsPortable });
            var store = new SettingsStore(log: smokeLog);
            store.Load();
            smokeEngine = new AudioEngine(store, smokeLog);
            await smokeEngine.StartAsync();
            var status = smokeEngine.Status;
            smokeLog.Info("smoke status", new
            {
                state = status.State,
                message = status.Message,
                mic = status.MicrophoneAvailable,
                send = status.SendAvailable,
                monitor = status.MonitorAvailable,
                micName = status.MicrophoneName,
                sendName = status.SendName,
                monitorName = status.MonitorName
            });
            Console.WriteLine($"SMOKE state={status.State} mic={status.MicrophoneAvailable} send={status.SendAvailable} monitor={status.MonitorAvailable} message={status.Message}");
            smokeHotkey = new RawInputHotkeyService();
            smokeHotkey.SetEnabled(true);
            Console.WriteLine($"SMOKE hotkey enabled={smokeHotkey.IsEnabled} error={smokeHotkey.LastError ?? "none"}");
            await Task.Delay(1200);
        }
        catch (Exception ex)
        {
            smokeLog?.Error("smoke mode failed", ex);
            Console.WriteLine("SMOKE EXCEPTION " + ex);
        }
        finally
        {
            smokeHotkey?.Dispose();
            if (smokeEngine is not null)
            {
                await smokeEngine.DisposeAsync();
            }

            smokeLog?.Info("smoke mode finished");
            smokeLog?.Dispose();
            if (Dispatcher.CheckAccess())
            {
                Shutdown();
            }
            else
            {
                Dispatcher.Invoke(Shutdown);
            }
        }
    }

    /// <summary>
    /// Development helper: renders the main window off-screen to a PNG so the
    /// layout can be inspected without opening an extra window on the desktop.
    /// It never starts the audio engine, hotkey or tray icon.
    /// </summary>
    private void RunScreenshotMode(string outputPath)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(outputDirectory);
        var tempRoot = Path.Combine(outputDirectory, "preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var tempLog = new AppLog(Path.Combine(tempRoot, "logs"));
        SettingsStore? previewStore = null;
        MediaLibrary? previewLibrary = null;
        AudioEngine? previewEngine = null;
        PlaybackController? previewController = null;
        MainViewModel? previewViewModel = null;
        MainWindow? previewWindow = null;

        try
        {
            var mediaDir = Path.Combine(tempRoot, "media");
            Directory.CreateDirectory(mediaDir);
            previewStore = new SettingsStore(Path.Combine(tempRoot, "settings.json"), Path.Combine(tempRoot, "settings.bak"), tempLog);
            previewStore.Settings.Media.Add(CreatePreviewEntry(Path.Combine(mediaDir, "preview-1.wav"), "雨夜电台 - 第 12 期", 225.4, 18.5));
            previewStore.Settings.Media.Add(CreatePreviewEntry(Path.Combine(mediaDir, "preview-2.wav"), "开场白 2026-11", 47.2, 0));
            previewStore.Settings.Media.Add(CreatePreviewEntry(Path.Combine(mediaDir, "preview-3.wav"), "无题 demo（44.1k）", 96.8, 32.75));
            previewStore.Settings.SelectedMediaId = previewStore.Settings.Media[0].Id;
            previewLibrary = new MediaLibrary(previewStore, tempLog, mediaDir);
            previewEngine = new AudioEngine(previewStore, tempLog);
            previewController = new PlaybackController(previewEngine, () => previewLibrary.Selected, tempLog);
            previewViewModel = new MainViewModel(previewEngine, previewController, previewLibrary, previewStore, tempLog, Dispatcher);
            previewWindow = new MainWindow(previewViewModel)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -20000,
                Top = -20000,
                Width = 1160,
                Height = 900
            };

            previewWindow.Show();
            previewViewModel.InitializeAfterEngineStart();
            previewWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            SaveWindowBitmap(previewWindow, outputPath);
        }
        finally
        {
            IsExiting = true;
            try
            {
                previewWindow?.Close();
            }
            catch
            {
                // ignore
            }

            previewViewModel?.Dispose();
            previewController?.Dispose();
            if (previewEngine is not null)
            {
                previewEngine.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }

            previewLibrary?.Dispose();
            previewStore = null;
            tempLog.Dispose();
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // ignore
            }

            Shutdown();
        }
    }

    private static MediaEntry CreatePreviewEntry(string path, string name, double durationSeconds, double startSeconds)
    {
        WriteTinyWav(path, durationSeconds);
        return new MediaEntry
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            LibraryFileName = path,
            SourceFileName = name + ".wav",
            Duration = TimeSpan.FromSeconds(durationSeconds),
            StartOffset = TimeSpan.FromSeconds(startSeconds),
            SourceHash = Guid.NewGuid().ToString("N"),
            ImportedAt = DateTimeOffset.Now.AddDays(-1)
        };
    }

    private static void WriteTinyWav(string path, double durationSeconds)
    {
        const int rate = 8000;
        const int channels = 1;
        var frames = Math.Max(1, (int)(rate * Math.Min(durationSeconds, 2)));
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var dataBytes = frames * channels * 2;
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write(rate);
        writer.Write(rate * channels * 2);
        writer.Write((ushort)(channels * 2));
        writer.Write((ushort)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (var i = 0; i < frames; i++)
        {
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 800));
        }
    }

    private static void SaveWindowBitmap(Window window, string outputPath)
    {
        var width = Math.Max(1, (int)Math.Round(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Round(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }
}

