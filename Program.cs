using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        using var mutex = new System.Threading.Mutex(true, "Local\\RdpSwitch.Panel", out var first);
        if (!first) return;
        new Application().Run(new PanelWindow());
    }
}

internal sealed class PanelWindow : Window
{
    private readonly StackPanel row = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly List<IntPtr> windowOrder = new();
    private readonly string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpSwitch", "position.json");
    private string signature = "";
    private Point? dragStart;
    private Point windowStart;
    private IntPtr pressedWindow;
    private bool dragged;
    private IntPtr handle;

    public PanelWindow()
    {
        Title = "RdpSwitch";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = new SolidColorBrush(Color.FromRgb(29, 32, 39));
        Left = 80; Top = 10;
        Content = new Border { Padding = new Thickness(4), BorderBrush = new SolidColorBrush(Color.FromRgb(65, 70, 81)), BorderThickness = new Thickness(1), Child = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxWidth = Math.Max(200, SystemParameters.WorkArea.Width - 30) } };
        var menu = new ContextMenu();
        var exit = new MenuItem { Header = "Выход" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit); ContextMenu = menu;
        trayIcon = CreateTrayIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "RdpSwitch — переключатель подключений", Visible = true };
        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Вернуть панель", null, (_, _) => { Left = 80; Top = 10; Refresh(); });
        trayMenu.Items.Add("Выход", null, (_, _) => Close());
        tray.ContextMenuStrip = trayMenu;
        try
        {
            var p = JsonSerializer.Deserialize<double[]>(File.ReadAllText(settings));
            if (p is { Length: 2 } && double.IsFinite(p[0]) && double.IsFinite(p[1]))
                foreach (var screen in Forms.Screen.AllScreens)
                {
                    var source = PresentationSource.FromVisual(this);
                    if (p[0] >= SystemParameters.VirtualScreenLeft && p[0] < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 && p[1] >= SystemParameters.VirtualScreenTop && p[1] < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
                    { Left = p[0]; Top = p[1]; break; }
                }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowLongPtr(handle, -20, Native.GetWindowLongPtr(handle, -20) | 0x08000080);
            Refresh(); timer.Start();
        };
        timer.Tick += (_, _) => Refresh();
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source)
                for (var element = source; element != null; element = VisualTreeHelper.GetParent(element))
                    if (element is System.Windows.Controls.Primitives.ScrollBar) return;
            pressedWindow = IntPtr.Zero;
            if (e.OriginalSource is DependencyObject target)
                for (var element = target; element != null; element = VisualTreeHelper.GetParent(element))
                    if (element is Button { Tag: IntPtr h }) { pressedWindow = h; break; }
            dragStart = PointToScreen(e.GetPosition(this));
            Native.GetWindowRect(handle, out var rect);
            windowStart = new Point(rect.Left, rect.Top);
            dragged = false;
            CaptureMouse();
            e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
            var p = PointToScreen(e.GetPosition(this));
            if (!dragged && Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragged = true;
            Native.SetWindowPos(handle, IntPtr.Zero, (int)(windowStart.X + p.X - start.X), (int)(windowStart.Y + p.Y - start.Y), 0, 0, 0x0015);
            e.Handled = true;
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (dragStart == null) return;
            var target = pressedWindow;
            var wasDragged = dragged;
            dragStart = null; pressedWindow = IntPtr.Zero;
            ReleaseMouseCapture();
            e.Handled = true;
            if (wasDragged) SavePosition();
            else ActivateConnection(target);
        };
        LostMouseCapture += (_, _) => { dragStart = null; pressedWindow = IntPtr.Zero; };
        Closed += (_, _) =>
        {
            timer.Stop(); tray.Dispose(); trayIcon.Dispose();
            SavePosition();
        };
    }

    private void SavePosition()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(settings)!); File.WriteAllText(settings, JsonSerializer.Serialize(new[] { Left, Top })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void ActivateConnection(IntPtr target)
    {
        if (!Native.IsWindow(target)) return;
        if (Native.IsIconic(target)) Native.ShowWindowAsync(target, 9);
        Native.SetForegroundWindow(target);
        Refresh();
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var blue = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(55, 133, 225));
        using var white = new System.Drawing.Pen(System.Drawing.Color.White, 2);
        graphics.FillRectangle(blue, 1, 3, 30, 23);
        graphics.DrawRectangle(white, 4, 6, 24, 16);
        graphics.DrawLine(white, 16, 23, 16, 28);
        graphics.DrawLine(white, 10, 29, 22, 29);
        graphics.DrawLines(white, new[] { new System.Drawing.Point(10, 10), new System.Drawing.Point(7, 13), new System.Drawing.Point(10, 16) });
        graphics.DrawLines(white, new[] { new System.Drawing.Point(22, 12), new System.Drawing.Point(25, 15), new System.Drawing.Point(22, 18) });
        var iconHandle = bitmap.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(iconHandle).Clone(); }
        finally { Native.DestroyIcon(iconHandle); }
    }

    private static string ConnectionLabel(string title, int number)
    {
        // Strip addresses while retaining the human-readable part of the title.
        var label = Regex.Replace(title, @"\[?[0-9a-fA-F]*:[0-9a-fA-F:]*:[0-9a-fA-F:]+\]?(?::\d+)?|\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?", "");
        label = Regex.Replace(label, @"\(\s*\)|\[\s*\]", "");
        label = Regex.Replace(label, @"\s+", " ").Trim(' ', '-', '—', '|', ':');
        return label.Length > 0 ? label : $"Сеанс {number}";
    }

    private void Refresh()
    {
        if (dragStart != null) return;
        var windows = new List<(IntPtr Handle, string Name)>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindow(h, 4) != IntPtr.Zero) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (!process.ProcessName.Equals("mstsc", StringComparison.OrdinalIgnoreCase)) return true;
                var title = new StringBuilder(1024); Native.GetWindowText(h, title, title.Capacity);
                var name = title.ToString();
                foreach (var suffix in new[] { " - Remote Desktop Connection", " — Подключение к удаленному рабочему столу", " - Подключение к удаленному рабочему столу", " - Подключение к удалённому рабочему столу" })
                    if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];
                if (name.Length > 0 && name != "Remote Desktop Connection" && !name.StartsWith("Подключение к удал", StringComparison.OrdinalIgnoreCase)) windows.Add((h, name));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        windowOrder.RemoveAll(h => !windows.Exists(w => w.Handle == h));
        foreach (var window in windows)
            if (!windowOrder.Contains(window.Handle)) windowOrder.Add(window.Handle);
        windows.Sort((a, b) => windowOrder.IndexOf(a.Handle).CompareTo(windowOrder.IndexOf(b.Handle)));
        var active = Native.GetForegroundWindow();
        var next = string.Join("|", windows.ConvertAll(w => $"{w.Handle}:{w.Name}:{w.Handle == active}"));
        if (windows.Count == 0) { Hide(); signature = ""; return; }
        if (!IsVisible) Show();
        Native.SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
        if (signature == next) return;
        signature = next; row.Children.Clear();
        foreach (var window in windows)
        {
            var button = new Button
            {
                Tag = window.Handle,
                Content = ConnectionLabel(window.Name, windowOrder.IndexOf(window.Handle) + 1), ToolTip = window.Name + "\nНажмите для переключения. Потяните для перемещения панели.",
                Foreground = Brushes.White, Background = new SolidColorBrush(window.Handle == active ? Color.FromRgb(48, 104, 170) : Color.FromRgb(43, 47, 57)),
                BorderThickness = new Thickness(0), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(2, 0, 2, 0), FontSize = 12, Focusable = false
            };
            button.Click += (_, _) =>
            {
                ActivateConnection(window.Handle);
            };
            row.Children.Add(button);
        }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
    internal delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr h, uint command);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr h, StringBuilder text, int length);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr h, int command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern long GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern long SetWindowLongPtr(IntPtr h, int index, long value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
