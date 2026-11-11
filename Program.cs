using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
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
    public sealed class Connection
    {
        public string Name { get; set; } = "";
        public string Target { get; set; } = "";
        [JsonIgnore] public IntPtr Handle { get; set; }
    }
    private readonly List<Connection> connections = new();
    public sealed class Preferences
    {
        public bool MinimizeOtherRdpOnSwitch { get; set; } = true;
    }
    private Preferences preferences = new();
    private Window? settingsWindow;
    private bool prompting;
    private int openMenus;
    private readonly StackPanel row = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly List<IntPtr> windowOrder = new();
    private readonly string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpSwitch", "position.json");
    private string signature = "";
    private Point? dragStart;
    private Point windowStart;
    private Connection? pressedConnection;
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
        var desktopButton = new Button
        {
            Tag = "desktop",
            Content = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M1,1 L17,1 L17,12 L1,12 Z M9,12 L9,16 M5,16 L13,16"),
                Stroke = Brushes.White, StrokeThickness = 1.4, Width = 18, Height = 18,
                Stretch = Stretch.Uniform
            },
            ToolTip = "Показать рабочий стол — свернуть только RDP",
            Background = new SolidColorBrush(Color.FromRgb(43, 47, 57)),
            BorderThickness = new Thickness(0), Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(5, 0, 0, 0), Focusable = false
        };
        System.Windows.Automation.AutomationProperties.SetName(desktopButton, "Свернуть все RDP");
        desktopButton.Click += (_, _) => MinimizeConnections();
        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(desktopButton, Dock.Right);
        layout.Children.Add(desktopButton);
        layout.Children.Add(new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxWidth = Math.Max(200, SystemParameters.WorkArea.Width - 75) });
        Content = new Border { Padding = new Thickness(4), BorderBrush = new SolidColorBrush(Color.FromRgb(65, 70, 81)), BorderThickness = new Thickness(1), Child = layout };
        var menu = CreatePanelMenu();
        var exit = new MenuItem { Header = "Выход" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit); ContextMenu = menu;
        trayIcon = CreateTrayIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "RdpSwitch — переключатель подключений", Visible = true };
        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Opened += (_, _) => openMenus++;
        trayMenu.Closed += (_, _) => { openMenus = Math.Max(0, openMenus - 1); };
        trayMenu.Items.Add("Вернуть панель", null, (_, _) => { Left = 80; Top = 10; Refresh(); });
        trayMenu.Items.Add("Настройки", null, (_, _) => ShowSettings());
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
        try
        {
            preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(settings)!, "settings.json"))) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        try
        {
            var saved = JsonSerializer.Deserialize<List<Connection>>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(settings)!, "connections.json")));
            if (saved != null) connections.AddRange(saved.FindAll(c => c != null && !string.IsNullOrWhiteSpace(c.Name)));
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
                    if (element is System.Windows.Controls.Primitives.ScrollBar || element is Button { Tag: "desktop" }) return;
            pressedConnection = null;
            if (e.OriginalSource is DependencyObject target)
                for (var element = target; element != null; element = VisualTreeHelper.GetParent(element))
                    if (element is Button { Tag: Connection connection }) { pressedConnection = connection; break; }
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
            var target = pressedConnection;
            var wasDragged = dragged;
            dragStart = null; pressedConnection = null;
            ReleaseMouseCapture();
            e.Handled = true;
            if (wasDragged) SavePosition();
            else if (target != null) ActivateConnection(target.Handle);
        };
        LostMouseCapture += (_, _) => { dragStart = null; pressedConnection = null; };
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

    private ContextMenu CreatePanelMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            openMenus++;
            // The popup has its own native window; keep it above the topmost panel.
            if (PresentationSource.FromVisual(menu) is HwndSource popup)
                Native.SetWindowPos(popup.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
        };
        menu.Closed += (_, _) => { openMenus = Math.Max(0, openMenus - 1); };
        return menu;
    }

    private void ActivateConnection(IntPtr target)
    {
        if (!Native.IsWindow(target)) return;
        if (Native.IsIconic(target)) Native.ShowWindowAsync(target, 9);
        if (Native.SetForegroundWindow(target) && preferences.MinimizeOtherRdpOnSwitch)
            MinimizeConnections(target);
        Refresh();
    }

    private void ShowSettings()
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        var dialog = new Window
        {
            Title = "Настройки RdpSwitch", Width = 440, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true, ShowInTaskbar = false, Background = new SolidColorBrush(Color.FromRgb(29, 32, 39)),
            Foreground = Brushes.White
        };
        var content = new StackPanel { Margin = new Thickness(20) };
        var option = new CheckBox
        {
            Content = new TextBlock { Text = "При переключении на RDP сворачивать остальные RDP", TextWrapping = TextWrapping.Wrap },
            IsChecked = preferences.MinimizeOtherRdpOnSwitch, Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 12)
        };
        content.Children.Add(option);
        content.Children.Add(new TextBlock
        {
            Text = "Применяется при выборе подключения на панели. Другие приложения не затрагиваются.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 20)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Сохранить", IsDefault = true, Padding = new Thickness(14, 6, 14, 6) };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) =>
        {
            var updated = new Preferences { MinimizeOtherRdpOnSwitch = option.IsChecked == true };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(settings)!, "settings.json"), JsonSerializer.Serialize(updated));
                preferences = updated;
                dialog.Close();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { MessageBox.Show(dialog, "Не удалось сохранить настройки: " + e.Message, "RdpSwitch"); }
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(); };
        buttons.Children.Add(save); buttons.Children.Add(cancel); content.Children.Add(buttons);
        dialog.Content = content;
        settingsWindow = dialog;
        dialog.Closed += (_, _) => { settingsWindow = null; prompting = false; Refresh(); };
        prompting = true;
        dialog.Show();
        dialog.Activate();
    }

    private void SaveConnections()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(settings)!, "connections.json"), JsonSerializer.Serialize(connections));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void OpenConnection(Connection connection)
    {
        Refresh();
        if (Native.IsWindow(connection.Handle)) { ActivateConnection(connection.Handle); return; }
        prompting = true;
        try
        {
            if (string.IsNullOrWhiteSpace(connection.Target)) return;
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mstsc.exe")) { UseShellExecute = false };
            start.ArgumentList.Add("/v:" + connection.Target);
            Process.Start(start)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { MessageBox.Show("Не удалось запустить RDP: " + e.Message, "RdpSwitch"); }
        finally { prompting = false; Refresh(); }
    }

    private static string ConnectionTarget(string title)
    {
        var parts = Regex.Split(title, @"\s+[—–-]\s+");
        return parts[^1].Trim();
    }

    private void MinimizeConnections(IntPtr except = default)
    {
        Refresh();
        foreach (var target in windowOrder.ToArray())
        {
            if (target == except || !Native.IsWindow(target) || Native.IsIconic(target)) continue;
            Native.GetWindowThreadProcessId(target, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.ProcessName.Equals("mstsc", StringComparison.OrdinalIgnoreCase))
                    Native.ShowWindowAsync(target, 7); // SW_SHOWMINNOACTIVE: preserve the selected window's focus.
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
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
        if (dragStart != null || prompting || openMenus > 0) return;
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
        var changed = false;
        foreach (var connection in connections)
            if (!windows.Exists(w => w.Handle == connection.Handle)) connection.Handle = IntPtr.Zero;
        foreach (var window in windows)
        {
            var target = ConnectionTarget(window.Name);
            var connection = connections.Find(c => c.Handle == window.Handle)
                ?? connections.Find(c => c.Handle == IntPtr.Zero && c.Target.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (connection == null)
            {
                connection = new Connection { Name = ConnectionLabel(window.Name, connections.Count + 1), Target = target };
                connections.Add(connection); changed = true;
            }
            connection.Handle = window.Handle;
        }
        if (changed) SaveConnections();
        var active = Native.GetForegroundWindow();
        var next = string.Join("|", connections.ConvertAll(c => $"{c.Handle}:{c.Name}:{c.Handle == active}"));
        if (connections.Count == 0) { Hide(); signature = ""; return; }
        if (!IsVisible) Show();
        Native.SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
        if (signature == next) return;
        signature = next; row.Children.Clear();
        foreach (var connection in connections)
        {
            var closed = connection.Handle == IntPtr.Zero;
            var button = new Button
            {
                Tag = connection,
                Content = connection.Name, ToolTip = connection.Target + (closed ? "\nЗакрыто. Правый клик → Подключиться." : "\nНажмите для переключения. Потяните для перемещения панели."),
                Foreground = closed ? Brushes.Gray : Brushes.White, Background = new SolidColorBrush(closed ? Color.FromRgb(35, 37, 42) : connection.Handle == active ? Color.FromRgb(48, 104, 170) : Color.FromRgb(43, 47, 57)),
                BorderThickness = new Thickness(0), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(2, 0, 2, 0), FontSize = 12, Focusable = false
            };
            button.Click += (_, _) =>
            {
                ActivateConnection(connection.Handle);
            };
            if (closed)
            {
                var connectionMenu = CreatePanelMenu();
                var connect = new MenuItem { Header = "Подключиться" };
                connect.Click += (_, _) => OpenConnection(connection);
                connectionMenu.Items.Add(connect);
                button.ContextMenu = connectionMenu;
            }
            else
            {
                var connectionMenu = CreatePanelMenu();
                var minimize = new MenuItem { Header = "Свернуть" };
                minimize.Click += (_, _) =>
                {
                    var target = connection.Handle;
                    if (!Native.IsWindow(target)) return;
                    Native.GetWindowThreadProcessId(target, out var pid);
                    try
                    {
                        using var process = Process.GetProcessById((int)pid);
                        if (process.ProcessName.Equals("mstsc", StringComparison.OrdinalIgnoreCase))
                            Native.ShowWindowAsync(target, 7);
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                };
                connectionMenu.Items.Add(minimize);
                button.ContextMenu = connectionMenu;
            }
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
