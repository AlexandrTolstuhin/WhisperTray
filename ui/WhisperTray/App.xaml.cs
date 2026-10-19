using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using WhisperEngine;

namespace WhisperTray;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? trayIcon;
    private Icon? applicationIcon;
    private OverlayWindow? overlay;
    private CallDetector? callDetector;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _ = StartupRegistration.TryMigrateLegacyRegistration();

        var paths = AppPaths.Discover(AppContext.BaseDirectory);
        overlay = new OverlayWindow(paths);
        trayIcon = CreateTrayIcon();
        callDetector = new CallDetector();
        callDetector.CallDetected += OnCallDetected;
        callDetector.Start();

        if (e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase))
        {
            overlay.ShowInTaskbar = true;
            ShowOverlay();
        }
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        applicationIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var icon = new Forms.NotifyIcon
        {
            Icon = applicationIcon ?? SystemIcons.Application,
            Text = "Whisper Tray",
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Показать", null, (_, _) => ShowOverlay());
        menu.Items.Add("Открыть транскрибации", null, (_, _) => OpenTranscriptions());
        menu.Items.Add("Выход", null, async (_, _) => await ExitApplicationAsync());
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => ShowOverlay();
        return icon;
    }

    private void OnCallDetected(object? sender, string processName)
    {
        Dispatcher.Invoke(() =>
        {
            overlay!.OfferCall(processName);
            ShowOverlay();
        });
    }

    private void ShowOverlay()
    {
        overlay!.PositionAtBottomRight();
        overlay.Show();
        overlay.Activate();
    }

    private void OpenTranscriptions()
    {
        var path = AppPaths.Discover(AppContext.BaseDirectory).Transcriptions;
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async Task ExitApplicationAsync()
    {
        callDetector?.Dispose();
        trayIcon?.Dispose();
        applicationIcon?.Dispose();
        if (overlay is not null)
        {
            await overlay.CloseForExitAsync();
        }
        Shutdown();
    }
}

