using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace ZKeyAudioLink.App.Services;

/// <summary>Tray icon with show/exit. Closing the window hides the app here.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private bool _disposed;

    public TrayIcon()
    {
        var icon = LoadIcon();

        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9f)
        };
        var showItem = new Forms.ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => RequestShow();
        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => RequestExit();
        _menu.Items.Add(showItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Z 键音频连麦",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.DoubleClick += (_, _) => RequestShow();
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                RequestShow();
            }
        };
    }

    public event Action? ShowRequested;
    public event Action? ExitRequested;

    private static Icon LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource is not null)
            {
                return new Icon(resource.Stream);
            }
        }
        catch
        {
            // fall through
        }

        return SystemIcons.Application;
    }

    private void RequestShow() => ShowRequested?.Invoke();

    private void RequestExit() => ExitRequested?.Invoke();

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);
        }
        catch
        {
            // balloon tips can be disabled by policy
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
