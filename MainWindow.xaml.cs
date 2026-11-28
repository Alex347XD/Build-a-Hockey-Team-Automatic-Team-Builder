using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Media = System.Windows.Media;
using HockeyRoller.Models;
using HockeyRoller.Services;

namespace HockeyRoller;

public partial class MainWindow : Window
{
    private AppConfig _config = new();
    private Bitmap? _previewBmp;
    private PickMode _pickMode = PickMode.None;
    private System.Windows.Point _dragStart;
    private System.Windows.Point _dragStartBitmap;
    private bool _dragging = false;
    private CancellationTokenSource? _cts;
    private readonly List<WindowInfo> _windows = new();

    public MainWindow()
    {
        Win32.SetProcessDPIAware();
        InitializeComponent();
        _config = AppConfig.Load();
        if (_config.Rarities.Count == 0) _config.Rarities = RarityCatalog.Defaults();
        LoadConfigToUI();
        RefreshWindows_Click(null, null);
        RefreshRarityCombos();
        AppendLog("OCR available: " + OcrService.Available);
        UpdateElevationStatus();
        LegacyMouseEventCheck.IsChecked = InputSim.UseLegacyMouseEvent;
        InputSim.UseLegacyMouseEvent = LegacyMouseEventCheck.IsChecked == true;
        _ = Task.Run(async () => { _roster = await TeamRoster.LoadAsync(); AppendLog($"Team roster: {_roster.Count} player(s) loaded."); });
        _ = Task.Run(async () => await CheckForUpdatesAsync());   // auto-update from GitHub releases
    }


    /// <summary>Checks GitHub releases and installs a newer version automatically.</summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var upd = await UpdateService.CheckAsync();
            if (upd == null)
            {
                AppendLog($"Up to date (v{UpdateService.CurrentVersion}).");
                return;
            }
            AppendLog($"Update available: {upd.Tag} (current v{upd.CurrentVersion}) - downloading {UpdateService.AssetName}...");
            bool ok = await UpdateService.DownloadAndInstallAsync(upd);
            if (ok)
            {
                AppendLog("Update installed - restarting...");
                Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
            }
            else AppendLog("Update download/checksum failed - staying on current version.");
        }
        catch (Exception ex) { AppendLog("Update check failed: " + ex.Message); }
    }    // ---------------- ESC = stop rolling and close ----------------
    private static Win32.LowLevelKeyboardProc? _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hookProc = HookProc;
        _hookHandle = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _hookProc, Win32.GetModuleHandle(null), 0);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam.ToInt32() & 0x0100) != 0) // WM_KEYDOWN
        {
            int vk = Marshal.ReadInt32(lParam);
            if (vk == 0x1B) // ESC
            {
                Dispatcher.BeginInvoke(new Action(() => StopAndClose()));
            }
        }
        return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void StopAndClose()
    {
        try
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                AppendLog("ESC pressed - stopping rolling.");
            }
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        }
        catch { }
    }

    private void UpdateElevationStatus()
    {
        bool toolAdmin = Win32.IsElevated();
        var w = SelectedWindow();
        bool gameAdmin = w != null && Win32.IsWindowElevated(w.Hwnd);
        ElevationText.Text = $"This tool: {(toolAdmin ? "administrator" : "standard user")} | Game window: {(w == null ? "n/a" : gameAdmin ? "administrator" : "standard user")}";
        if (gameAdmin && !toolAdmin)
            ElevationText.Foreground = System.Windows.Media.Brushes.OrangeRed;
        else
            ElevationText.Foreground = System.Windows.Media.Brushes.Gray;
    }

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                Verb = "runas",
                UseShellExecute = true
            });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not restart as administrator: " + ex.Message);
        }
    }

    // ---------------- Window enumeration ----------------
    private class WindowInfo
    {
        public IntPtr Hwnd { get; set; }
        public string Title { get; set; } = "";
        public override string ToString() => Title;
    }

    private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateElevationStatus();

    protected override void OnClosed(EventArgs e)
    {
        if (_hookHandle != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        base.OnClosed(e);
    }

    private void RefreshWindows_Click(object? sender, RoutedEventArgs? e)
    {
        _windows.Clear();
        Win32.EnumWindows((h, l) =>
        {
            if (!Win32.IsWindowVisible(h)) return true;
            int len = Win32.GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            Win32.GetWindowText(h, sb, sb.Capacity);
            _windows.Add(new WindowInfo { Hwnd = h, Title = sb.ToString() });
            return true;
        }, IntPtr.Zero);

        var sorted = _windows
            .OrderByDescending(w => w.Title.IndexOf("Roblox", StringComparison.OrdinalIgnoreCase) >= 0)
            .ThenBy(w => w.Title)
            .ToList();
        _windows.Clear();
        _windows.AddRange(sorted);

        WindowCombo.ItemsSource = null;
        WindowCombo.ItemsSource = _windows;
        WindowCombo.DisplayMemberPath = "Title";

        if (_config.Hwnd.Value != 0)
        {
            var idx = _windows.FindIndex(w => w.Hwnd.ToInt64() == _config.Hwnd.Value);
            if (idx >= 0) WindowCombo.SelectedIndex = idx;
        }
        if (WindowCombo.SelectedIndex < 0 && _windows.Count > 0)
            WindowCombo.SelectedIndex = 0;
    }

    private WindowInfo? SelectedWindow() => WindowCombo.SelectedItem as WindowInfo;

    // ---------------- Config ----------------
    private void LoadConfigToUI()
    {
        RollPtText.Text = _config.RollX == 0 && _config.RollY == 0 ? "" : $"{_config.RollX},{_config.RollY}";

        RefreshPtText.Text = _config.RefreshX == 0 && _config.RefreshY == 0 ? "" : $"{_config.RefreshX},{_config.RefreshY}";
        OddsBtnPtText.Text = _config.OddsBtnX < 0 ? "" : $"{_config.OddsBtnX},{_config.OddsBtnY}";
        OddsClosePtText.Text = _config.OddsCloseX < 0 ? "" : $"{_config.OddsCloseX},{_config.OddsCloseY}";
        OddsTeamTabPtText.Text = _config.OddsTeamTabX < 0 ? "" : $"{_config.OddsTeamTabX},{_config.OddsTeamTabY}";
        OddsRegionText.Text = _config.OddsW == 0 ? "" : $"{_config.OddsX},{_config.OddsY} {_config.OddsW}x{_config.OddsH}";
        BlockedRegionText.Text = _config.BlockedW == 0 ? "" : $"{_config.BlockedX},{_config.BlockedY} {_config.BlockedW}x{_config.BlockedH}";
        CardRegionText.Text = _config.CardW == 0 ? "" : $"{_config.CardX},{_config.CardY} {_config.CardW}x{_config.CardH}";
        CardCountCombo.SelectedIndex = Math.Clamp(_config.CardCount - 1, 0, 3);
        CardLayoutCombo.SelectedIndex = -1;
        for (int i = 0; i < CardLayoutCombo.Items.Count; i++)
            if (CardLayoutCombo.Items[i] is ComboBoxItem ci && ci.Content?.ToString() == _config.CardLayout)
            { CardLayoutCombo.SelectedIndex = i; break; }
        if (CardLayoutCombo.SelectedIndex < 0) CardLayoutCombo.SelectedIndex = 2;   // 2 x 2 grid
        DelayText.Text = _config.DelayMs.ToString();
        SettleText.Text = _config.SettleMaxMs.ToString();
        if (_config.SettleMaxMs <= 0) _config.SettleMaxMs = 5000;
        RefreshCapText.Text = _config.RefreshCap.ToString();
        StrategyCombo.SelectedIndex = _config.Strategy;
        GroupRivalryCheck.IsChecked = _config.GroupRivalry;
        MinOvrText.Text = _config.MinOvr.ToString();
        AcceptAnyByOvrCheck.IsChecked = _config.AcceptAnyTierByOvr;
        OvrOnlyCheck.IsChecked = _config.OvrOnly;
        CaptureMethodCombo.SelectedIndex = _config.CaptureMethod;
        RarityList.ItemsSource = null;
        RarityList.ItemsSource = _config.Rarities;
    }

    private bool _updatingLayout;
    private static string ComboContent(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Content != null)
            return item.Content.ToString() ?? fallback;
        if (combo.SelectedItem is string s) return s;
        return fallback;
    }

    private void CardLayout_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingLayout || _config == null) return;
        _updatingLayout = true;
        try
        {
            _config.CardCount = CardCountCombo.SelectedIndex >= 0 ? CardCountCombo.SelectedIndex + 1 : 1;
            _config.CardLayout = ComboContent(CardLayoutCombo, "2 x 2 grid");
            // a 2x2 grid only makes sense with 4 cards
            if (_config.CardLayout == "2 x 2 grid" && _config.CardCount != 4)
            {
                _config.CardCount = 4;
                CardCountCombo.SelectedIndex = 3;
            }
            AppendLog($"Card region: {_config.CardCount} card(s), layout {_config.CardLayout}");
        }
        finally { _updatingLayout = false; }
    }

    private List<Rectangle> SliceRegions(Rectangle region, int count, string layout)
    {
        var list = new List<Rectangle>();
        if (count <= 1) { list.Add(region); return list; }
        if (layout == "2 x 2 grid" && count == 4)
        {
            int w = region.Width / 2, h = region.Height / 2;
            int insetX = (int)(w * 0.03), insetY = (int)(h * 0.03);
            void Add(int x, int y)
            {
                list.Add(new Rectangle(x + insetX, y + insetY, w - insetX * 2, h - insetY * 2));
            }
            Add(region.X, region.Y);
            Add(region.X + w, region.Y);
            Add(region.X, region.Y + h);
            Add(region.X + w, region.Y + h);
        }
        else if (layout == "Stacked (vertical)")
        {
            int h = region.Height / count;
            for (int i = 0; i < count; i++)
                list.Add(new Rectangle(region.X, region.Y + i * h, region.Width, h));
        }
        else
        {
            int w = region.Width / count;
            for (int i = 0; i < count; i++)
                list.Add(new Rectangle(region.X + i * w, region.Y, w, region.Height));
        }
        return list;
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        var w = SelectedWindow();
        if (w != null) { _config.Hwnd = IntPtrHwnd.FromIntPtr(w.Hwnd); _config.WindowTitle = w.Title; }
        int.TryParse(DelayText.Text, out int d); _config.DelayMs = d > 0 ? d : 1500;
        int.TryParse(SettleText.Text, out int sm); _config.SettleMaxMs = sm > 0 ? sm : 9000;
        int.TryParse(MinOvrText.Text, out int mo); _config.MinOvr = mo > 0 ? mo : 0;
        _config.AcceptAnyTierByOvr = AcceptAnyByOvrCheck.IsChecked == true;
        _config.OvrOnly = OvrOnlyCheck.IsChecked == true;
        int.TryParse(RefreshCapText.Text, out int rc); _config.RefreshCap = rc > 0 ? rc : 0;
        _config.MinRarity = MinRarityCombo.SelectedItem?.ToString() ?? _config.MinRarity;
        _config.MaxRarity = MaxRarityCombo.SelectedItem?.ToString() ?? _config.MaxRarity;
        _config.Strategy = StrategyCombo.SelectedIndex >= 0 ? StrategyCombo.SelectedIndex : 0;
        _config.CaptureMethod = CaptureMethodCombo.SelectedIndex >= 0 ? CaptureMethodCombo.SelectedIndex : 0;
        _config.GroupRivalry = GroupRivalryCheck.IsChecked == true;
        _config.CardCount = CardCountCombo.SelectedIndex >= 0 ? CardCountCombo.SelectedIndex + 1 : 1;
        _config.CardLayout = ComboContent(CardLayoutCombo, "2 x 2 grid");
        InputSim.UseLegacyMouseEvent = LegacyMouseEventCheck.IsChecked == true;
        _ = Task.Run(async () => { _roster = await TeamRoster.LoadAsync(); AppendLog($"Team roster: {_roster.Count} player(s) loaded."); });
        try { _config.Save(); AppendLog("Config saved."); }
        catch (Exception ex) { AppendLog("Save failed: " + ex.Message); }
        RefreshRarityCombos();
    }

    // ---------------- Preview ----------------
    private void CapturePreview_Click(object sender, RoutedEventArgs e)
    {
        var w = SelectedWindow();
        if (w == null) { MessageBox.Show("Select a window first."); return; }
        var bmp = WindowCapture.Capture(w.Hwnd, CaptureMethodCombo.SelectedIndex);
        if (bmp == null) { MessageBox.Show("Capture failed."); return; }
        _previewBmp?.Dispose();
        _previewBmp = bmp;
        PreviewImage.Source = BitmapToImageSource(bmp);
        FitPreview();
        AppendLog("Capture: " + WindowCapture.LastInfo);
        PickStatusText.Text = "Preview captured (" + bmp.Width + "x" + bmp.Height + "). Choose a pick mode then click/drag on preview.";
    }

    private void FitPreviewCheck_Changed(object sender, RoutedEventArgs e) => FitPreview();
    private void SaveDebugShot_Click(object sender, RoutedEventArgs e)
    {
        var w = SelectedWindow();
        if (w == null) { MessageBox.Show("Select a window first."); return; }
        var bmp = WindowCapture.Capture(w.Hwnd, CaptureMethodCombo.SelectedIndex);
        if (bmp == null) { MessageBox.Show("Capture failed."); return; }

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            void Marker(int x, int y, Color c, string label)
            {
                if (x == 0 && y == 0) return;
                using var pen = new Pen(c, 3);
                g.DrawEllipse(pen, x - 14, y - 14, 28, 28);
                g.DrawLine(pen, x - 22, y, x + 22, y);
                g.DrawLine(pen, x, y - 22, x, y + 22);
                using var font = new System.Drawing.Font("Arial", 14, System.Drawing.FontStyle.Bold);
                using var brush = new SolidBrush(c);
                g.DrawString(label, font, brush, x + 18, y - 24);
            }
            void Box(int x, int y, int w2, int h2, Color c, string label)
            {
                if (w2 <= 0 || h2 <= 0) return;
                using var pen = new Pen(c, 3);
                g.DrawRectangle(pen, x, y, w2, h2);
                using var font = new System.Drawing.Font("Arial", 14, System.Drawing.FontStyle.Bold);
                using var brush = new SolidBrush(c);
                g.DrawString(label, font, brush, x + 4, y + 4);
            }

            Box(_config.CardX, _config.CardY, _config.CardW, _config.CardH, Color.Lime, "CARDS");
            if (_config.CardW > 0 && _config.CardCount == 4)
            {
                var slices = SliceRegions(new Rectangle(_config.CardX, _config.CardY, _config.CardW, _config.CardH), 4, _config.CardLayout);
                int i = 1;
                foreach (var s in slices) { Box(s.X, s.Y, s.Width, s.Height, Color.Yellow, "C" + i); i++; }
            }
            Box(_config.OddsX, _config.OddsY, _config.OddsW, _config.OddsH, Color.Orange, "ODDS");
            Marker(_config.RollX, _config.RollY, Color.Red, "ROLL");

            Marker(_config.RefreshX, _config.RefreshY, Color.Yellow, "REFRESH");
            Marker(_config.OddsBtnX, _config.OddsBtnY, Color.Orange, "ODDSBTN");
            Marker(_config.OddsCloseX, _config.OddsCloseY, Color.OrangeRed, "ODDSCLOSE");
        }

        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HockeyRoller");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "debug_screen.png");
        bmp.Save(path, ImageFormat.Png);
        bmp.Dispose();

        // also save the card region on its own
        if (_config.CardW > 0)
        {
            var bmp2 = WindowCapture.Capture(w.Hwnd, CaptureMethodCombo.SelectedIndex);
            if (bmp2 != null)
            {
                using var card = WindowCapture.Crop(bmp2, new Rectangle(_config.CardX, _config.CardY, _config.CardW, _config.CardH));
                bmp2.Dispose();
                card.Save(Path.Combine(dir, "debug_cards.png"), ImageFormat.Png);
            }
        }
        AppendLog("Debug images saved to: " + dir);
        MessageBox.Show("Saved:\n" + Path.Combine(dir, "debug_screen.png") + "\n" + Path.Combine(dir, "debug_cards.png"));
    }

    private double _previewScale = 1.0;

    private void FitPreview()
    {
        if (_previewBmp == null) return;
        if (FitPreviewCheck.IsChecked == true)
        {
            double vw = PreviewScroll.ViewportWidth > 0 ? PreviewScroll.ViewportWidth : 900;
            double vh = PreviewScroll.ViewportHeight > 0 ? PreviewScroll.ViewportHeight : 500;
            double s = Math.Min(vw / _previewBmp.Width, vh / _previewBmp.Height);
            if (s > 1) s = 1;
            _previewScale = s;
            PreviewImage.Stretch = Media.Stretch.Uniform;
            PreviewImage.Width = _previewBmp.Width * s;
            PreviewImage.Height = _previewBmp.Height * s;
        }
        else
        {
            _previewScale = 1.0;
            PreviewImage.Stretch = Media.Stretch.None;
            PreviewImage.Width = double.NaN;
            PreviewImage.Height = double.NaN;
        }
    }

    private static BitmapImage BitmapToImageSource(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = ms;
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    // ---------------- Picking ----------------
    private enum PickMode { None, Roll, CardRegion, Dismiss, Reroll, Refresh, OddsBtn, OddsRegion, Blocked, OddsClose, OddsTeamTab }

    private void PickRoll_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.Roll; PickStatusText.Text = "Pick mode: Roll Button - click on the roll button."; }
    private void PickCardRegion_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.CardRegion; _dragging = false; PickStatusText.Text = "Pick mode: Card Region - drag a rectangle over the card."; }

    private void PickRefresh_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.Refresh; PickStatusText.Text = "Pick mode: Refresh Button - click."; }
    private void PickOddsBtn_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.OddsBtn; PickStatusText.Text = "Pick mode: Odds Button - click."; }
    private void PickOddsRegion_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.OddsRegion; _dragging = false; PickStatusText.Text = "Pick mode: Odds Region - drag."; }
    private void PickOddsClose_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.OddsClose; PickStatusText.Text = "Pick mode: Odds Close - click the button that shuts the odds panel."; }
    private void PickOddsTeamTab_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.OddsTeamTab; PickStatusText.Text = "Pick mode: Odds scroll pos - click inside the odds list (the scroll position; the THIS TEAM tab)."; }
    private void PickBlocked_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.Blocked; _dragging = false; PickStatusText.Text = "Pick mode: Blocked Region - drag."; }
    private void ClearBlocked_Click(object s, RoutedEventArgs e) { _config.BlockedX = -1; _config.BlockedY = -1; _config.BlockedW = 0; _config.BlockedH = 0; _config.BlockedR = -1; BlockedRegionText.Text = ""; }

    private void SampleBlockedColor_Click(object sender, RoutedEventArgs e)
    {
        if (_previewBmp == null) { MessageBox.Show("Capture preview first."); return; }
        if (_config.BlockedW <= 0) { MessageBox.Show("Set blocked region first."); return; }
        var r = new Rectangle(_config.BlockedX, _config.BlockedY, _config.BlockedW, _config.BlockedH);
        var c = ColorMatch.AverageFast(_previewBmp, r);
        _config.BlockedR = c.R; _config.BlockedG = c.G; _config.BlockedB = c.B;
        AppendLog($"Blocked colour sampled: {c.R},{c.G},{c.B}");
    }

    private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_previewBmp == null) { PickStatusText.Text = "Capture the preview first."; return; }
        var pos = e.GetPosition(PreviewImage);
        int x = (int)(pos.X / _previewScale), y = (int)(pos.Y / _previewScale);
        if (x < 0 || y < 0 || x >= _previewBmp.Width || y >= _previewBmp.Height) return;

        switch (_pickMode)
        {
            case PickMode.Roll: _config.RollX = x; _config.RollY = y; RollPtText.Text = $"{x},{y}"; break;

            case PickMode.Refresh: _config.RefreshX = x; _config.RefreshY = y; RefreshPtText.Text = $"{x},{y}"; break;
            case PickMode.OddsBtn: _config.OddsBtnX = x; _config.OddsBtnY = y; OddsBtnPtText.Text = $"{x},{y}"; break;
            case PickMode.OddsClose: _config.OddsCloseX = x; _config.OddsCloseY = y; OddsClosePtText.Text = $"{x},{y}"; break;
            case PickMode.OddsTeamTab: _config.OddsTeamTabX = x; _config.OddsTeamTabY = y; OddsTeamTabPtText.Text = $"{x},{y}"; break;

            default:
                _dragStart = pos; _dragStartBitmap = new System.Windows.Point(x, y); _dragging = true;
                PreviewRect.Visibility = Visibility.Visible;
                Canvas.SetLeft(PreviewRect, x); Canvas.SetTop(PreviewRect, y);
                PreviewRect.Width = 0; PreviewRect.Height = 0;
                return;
        }
        PickStatusText.Text = $"Set {_pickMode} to {x},{y}";
        _pickMode = PickMode.None;
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var pos = e.GetPosition(PreviewImage);
        double x = Math.Min(_dragStart.X, pos.X), y = Math.Min(_dragStart.Y, pos.Y);
        double w = Math.Abs(pos.X - _dragStart.X), h = Math.Abs(pos.Y - _dragStart.Y);
        Canvas.SetLeft(PreviewRect, x); Canvas.SetTop(PreviewRect, y);
        PreviewRect.Width = w; PreviewRect.Height = h;
    }

    private void Preview_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        var pos = e.GetPosition(PreviewImage);
        int bx = (int)(pos.X / _previewScale), by = (int)(pos.Y / _previewScale);
        int x1 = (int)Math.Min(_dragStartBitmap.X, bx), y1 = (int)Math.Min(_dragStartBitmap.Y, by);
        int x2 = (int)Math.Max(_dragStartBitmap.X, bx), y2 = (int)Math.Max(_dragStartBitmap.Y, by);
        int w = x2 - x1, h = y2 - y1;
        PreviewRect.Visibility = Visibility.Collapsed;
        if (w < 3 || h < 3) { PickStatusText.Text = "Region too small."; return; }

        switch (_pickMode)
        {
            case PickMode.CardRegion:
                _config.CardX = x1; _config.CardY = y1; _config.CardW = w; _config.CardH = h;
                CardRegionText.Text = $"{x1},{y1} {w}x{h}";
                PickStatusText.Text = $"Card region set {w}x{h}";
                break;
            case PickMode.OddsRegion:
                _config.OddsX = x1; _config.OddsY = y1; _config.OddsW = w; _config.OddsH = h;
                OddsRegionText.Text = $"{x1},{y1} {w}x{h}";
                PickStatusText.Text = $"Odds region set {w}x{h}";
                break;
            case PickMode.Blocked:
                _config.BlockedX = x1; _config.BlockedY = y1; _config.BlockedW = w; _config.BlockedH = h;
                BlockedRegionText.Text = $"{x1},{y1} {w}x{h}";
                PickStatusText.Text = $"Blocked region set {w}x{h}";
                break;
        }
        _pickMode = PickMode.None;
    }

    // ---------------- Rarities ----------------



    private void RefreshRarityCombos()
    {
        var names = _config.Rarities.Select(r => r.Name).ToList();
        MinRarityCombo.ItemsSource = names;
        MaxRarityCombo.ItemsSource = names;
        if (!string.IsNullOrEmpty(_config.MinRarity) && names.Contains(_config.MinRarity)) MinRarityCombo.SelectedItem = _config.MinRarity;
        if (!string.IsNullOrEmpty(_config.MaxRarity) && names.Contains(_config.MaxRarity)) MaxRarityCombo.SelectedItem = _config.MaxRarity;
        if (MinRarityCombo.SelectedItem == null && names.Count > 0) MinRarityCombo.SelectedIndex = 0;
        if (MaxRarityCombo.SelectedItem == null && names.Count > 0) MaxRarityCombo.SelectedIndex = names.Count - 1;
    }

    // ---------------- Detection ----------------


    private async Task<int> LearnTiersFromOddsAsync(IntPtr hwnd, CancellationToken token)
    {
        int learned = 0;
        var (oddsBtnX, oddsBtnY) = ResolveOddsButton(hwnd);
        if (oddsBtnX == 0) return 0;

        ClickAt(hwnd, oddsBtnX, oddsBtnY);
        await Task.Delay(400, token);
        await WaitForStableAsync(hwnd, token, maxMs: 2500);

        // both columns list the rarity names in their tier colour - read the whole panel
        var bmp = WindowCapture.Capture(hwnd, _config.CaptureMethod);
        if (bmp == null) return 0;

        var panelRect = new Rectangle((int)(bmp.Width * 0.24), (int)(bmp.Height * 0.24),
                                      (int)(bmp.Width * 0.52), (int)(bmp.Height * 0.60));
        using var crop = WindowCapture.Crop(bmp, panelRect);
        bmp.Dispose();

        var words = await OcrService.RecognizeWordsAsync(crop);
        if (words.Count == 0) { AppendLog("No words found in the odds panel."); return 0; }

        foreach (var r in _config.Rarities)
        {
            foreach (var w in words)
            {
                string t = w.Text.Trim().TrimEnd(':').ToLowerInvariant();
                if (t.Length < 3) continue;
                bool hit = r.Patterns.Any(p =>
                {
                    string pp = p.ToLowerInvariant();
                    return t == pp || t.StartsWith(pp) || pp.StartsWith(t);
                });
                if (!hit) continue;

                var ink = OcrService.DominantInk(crop, w.Rect);
                if (ink == Color.Black) continue;
                r.R = ink.R; r.G = ink.G; r.B = ink.B;
                r.HasBody = false;      // body colour comes from the card, not the odds text
                learned++;
                AppendLog($"Learned {r.Name} = #{ink.R:X2}{ink.G:X2}{ink.B:X2} (from odds text '{w.Text}')");
                break;
            }
        }

        // close the panel again
        if (_config.OddsCloseX >= 0) ClickAt(hwnd, _config.OddsCloseX, _config.OddsCloseY);
        else ClickAt(hwnd, oddsBtnX, oddsBtnY);
        await Task.Delay(300, token);

        RarityList.ItemsSource = null; RarityList.ItemsSource = _config.Rarities;
        return learned;
    }


    private async void TestDetection_Click(object sender, RoutedEventArgs e)
    {
        var w = SelectedWindow();
        if (w == null) { MessageBox.Show("Select window."); return; }
        if (_config.CardW <= 0) { MessageBox.Show("Set card region first."); return; }
        var bmp = WindowCapture.Capture(w.Hwnd, CaptureMethodCombo.SelectedIndex);
        if (bmp == null) { AppendLog("Capture failed."); return; }
        var cardRect = new Rectangle(_config.CardX, _config.CardY, _config.CardW, _config.CardH);
        using var region = WindowCapture.Crop(bmp, cardRect);
        bmp.Dispose();
        var slices = SliceRegions(new Rectangle(0, 0, region.Width, region.Height), _config.CardCount, _config.CardLayout);
        for (int i = 0; i < slices.Count; i++)
        {
            using var card = WindowCapture.Crop(region, slices[i]);
            // the tier comes from the OVR number - the only signal
            var (ovrVal, _ovrBox) = await ReadOvrWithBoxAsync(card);
            int ovr = ovrVal != 0 ? ovrVal : await ReadOvrAsync(card);

            bool vsSymbol = await HasVsSymbolAsync(card);
            string? tier = DetectTierShiny(card, ovr, vsSymbol, out bool shiny);
            bool strip = DiagonalStripDetector.HasDiagonalStrip(card, out double stripScore);
            string det = $"ovr={ovr}{(shiny ? " SHINY+2" : "")}{(vsSymbol ? " vs" : "")}";
            AppendLog($"Detection test card {i + 1}/{slices.Count} -> {tier ?? "(unknown)"}  {det}  blockedRibbon={strip} (score {stripScore:F2})");
            StatusText.Text = tier ?? "(unknown)";
        }
    }

    // ---------------- Run loop ----------------
    private uint _lastRegionHash;
    private uint _lastWinHash;
    private int _refreshesWithoutHit = 0;
    private TeamRoster? _roster;
    private readonly HashSet<string> _claimedPlayers = new();   // normalised names already claimed
    private string _currentTeam = "";
    private int _currentTeamYear = 0;
    private void ClickAt(IntPtr hwnd, int clientX, int clientY, int method = 0)
    {
        Win32.GetClientRect(hwnd, out var rc);
        int cw = rc.Right - rc.Left, ch = rc.Bottom - rc.Top;
        bool sizeMismatch = WindowCapture.LastClientW > 0 && (cw != WindowCapture.LastClientW || ch != WindowCapture.LastClientH);

        InputSim.Focus(hwnd);
        bool ok = true;
        var pt = new Win32.POINT { X = clientX, Y = clientY };
        Win32.ClientToScreen(hwnd, ref pt);
        if (method == 1) ok = InputSim.PostClick(hwnd, clientX, clientY);
        else if (method == 2) ok = InputSim.ClickScreen(pt.X, pt.Y, false);   // single press
        else ok = InputSim.ClickScreen(pt.X, pt.Y, DoublePressCheck.IsChecked == true);

        AppendLog("  " + InputSim.LastClickInfo);
        if (!ok) AppendLog("  WARNING: click not delivered.");
        if (sizeMismatch)
            AppendLog($"  WARNING: game window is now {cw}x{ch} but the last capture was {WindowCapture.LastClientW}x{WindowCapture.LastClientH} - re-pick your points!");
    }

    private int _clickMethod = 0;   // method that worked last time

    private async void TestClick_Click(object sender, RoutedEventArgs e)
    {
        var w = SelectedWindow();
        if (w == null) { MessageBox.Show("Select window."); return; }
        var (x, y) = ResolveButton(w.Hwnd, false);
        if (x == 0 && y == 0) { MessageBox.Show("Set a roll button first."); return; }
        AppendLog($"  ROLL button resolved at client({x},{y})");

        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HockeyRoller");
        Directory.CreateDirectory(dir);

        // hover first so the user can SEE whether the cursor lands on the button
        InputSim.Focus(w.Hwnd);
        var pt = new Win32.POINT { X = x, Y = y };
        Win32.ClientToScreen(w.Hwnd, ref pt);
        InputSim.MoveTo(pt.X - 4, pt.Y - 4);
        await Task.Delay(80);
        InputSim.MoveTo(pt.X, pt.Y, 4);
        AppendLog($"  hover at screen({pt.X},{pt.Y}) - is the cursor on the button? does it highlight?");
        await Task.Delay(1200);

        string[] names = { "SendInput", "PostMessage", "SendInput+move" };
        for (int m = 0; m < 3; m++)
        {
            var beforeBmp = WindowCapture.Capture(w.Hwnd, CaptureMethodCombo.SelectedIndex);
            uint before = beforeBmp != null ? HashBitmap(CropSafe(beforeBmp, new Rectangle(0, 0, Math.Min(700, beforeBmp.Width), beforeBmp.Height))) : 0;
            beforeBmp?.Dispose();

            AppendLog($"  trying click method {m} ({names[m]}) at client({x},{y})...");
            ClickAt(w.Hwnd, x, y, m);
            await Task.Delay(1800);

            var afterBmp = WindowCapture.Capture(w.Hwnd, CaptureMethodCombo.SelectedIndex);
            uint after = afterBmp != null ? HashBitmap(CropSafe(afterBmp, new Rectangle(0, 0, Math.Min(700, afterBmp.Width), afterBmp.Height))) : 0;
            if (afterBmp != null)
            {
                afterBmp.Save(Path.Combine(dir, $"debug_click{m + 1}.png"), ImageFormat.Png);
                afterBmp.Dispose();
            }
            if (before != 0 && after != 0 && before != after)
            {
                AppendLog($"  >>> method {m} ({names[m]}) WORKED - whole window changed.");
                _clickMethod = m;
                MessageBox.Show($"Method {m} ({names[m]}) worked.\n\nSaved: {dir}\\debug_click{m + 1}.png");
                return;
            }
            AppendLog($"  method {m} did not change the window.");
        }
        AppendLog("  none of the click methods changed the window.");
        MessageBox.Show("None of the click methods changed the game window.\n\n" +
                        "Saved:\n" + Path.Combine(dir, "debug_click1.png") + "\n" +
                        Path.Combine(dir, "debug_click2.png") + "\n" +
                        Path.Combine(dir, "debug_click3.png") +
                        "\n\nPlease send these to the developer.");
    }

    private uint CaptureHash(IntPtr hwnd)
    {
        var bmp = WindowCapture.Capture(hwnd, _config.CaptureMethod);
        if (bmp == null) return 0;
        var cardRect = new Rectangle(_config.CardX, _config.CardY, _config.CardW, _config.CardH);
        if (_config.CardW <= 0) { bmp.Dispose(); return 0; }
        using var region = WindowCapture.Crop(bmp, cardRect);
        bmp.Dispose();
        return HashBitmap(region);
    }

    private int _rollOffsetX = 0, _rollOffsetY = 0;   // learned nudge that made the click land
    private (int x, int y) ResolveOddsButton(IntPtr hwnd)
    {
        if (_config.OddsBtnX >= 0) return (_config.OddsBtnX, _config.OddsBtnY);
        return (0, 0);
    }
    private (int x, int y) ResolveButton(IntPtr hwnd, bool refresh)
    {
        // in this game ROLL and REROLL are the SAME button; only REFRESH is separate
        if (refresh && _config.RefreshX != 0) return (_config.RefreshX, _config.RefreshY);
        // reroll uses the roll button; fall back to a separately-picked reroll point
        if (_config.RollX != 0) return (_config.RollX, _config.RollY);
        if (_config.RollX != 0) return (_config.RollX, _config.RollY);
        if (refresh && _config.RefreshX != 0) return (_config.RefreshX, _config.RefreshY);
        return (0, 0);
    }

    private void ClickRoll(IntPtr hwnd, bool useRefresh)
    {
        var (bx, by) = ResolveButton(hwnd, useRefresh);
        if (bx == 0 && by == 0)
        {
            // nothing detected and nothing picked
            if (useRefresh && _config.RefreshX != 0) { bx = _config.RefreshX; by = _config.RefreshY; }
            else if (_config.RollX != 0) { bx = _config.RollX; by = _config.RollY; }
            else if (_config.RollX != 0) { bx = _config.RollX; by = _config.RollY; }
        }
        ClickAt(hwnd, bx + _rollOffsetX, by + _rollOffsetY, _clickMethod);
        AppendLog(useRefresh ? "Refreshed (kept team/year)." : "Rerolled (full).");
    }
    private async Task<(List<string> tiers, string text)> ReadRefreshOddsAsync(IntPtr hwnd, CancellationToken token)
    {
        var (oddsBtnX, oddsBtnY) = ResolveOddsButton(hwnd);
        if (oddsBtnX == 0) return (new List<string>(), "");

        ClickAt(hwnd, oddsBtnX, oddsBtnY);
        await Task.Delay(400, token);
        await WaitForStableAsync(hwnd, token, maxMs: 2500);

        // the odds modal sits in the middle of the screen - OCR just that panel
        var bmp = WindowCapture.Capture(hwnd, _config.CaptureMethod);
        if (bmp == null) return (new List<string>(), "");
        var panel = new Rectangle((int)(bmp.Width * 0.24), (int)(bmp.Height * 0.24),
                                  (int)(bmp.Width * 0.52), (int)(bmp.Height * 0.60));
        using var panelShot = WindowCapture.Crop(bmp, panel);
        SaveDebug(panelShot, "odds_panel.png");

        // The panel has two columns:
        //   LEFT  = REROLL odds (a brand new team)
        //   RIGHT = REFRESH odds for the CURRENT team  <-- this is what decides refresh vs reroll
        int splitX = panel.Width / 2;

        var names = new List<string>();
        var leftNames = new List<string>();
        var text = new System.Text.StringBuilder();
        var leftText = new System.Text.StringBuilder();

        // read the refresh column (right of the split) - the margins keep the panel
        // border out of the line detection
        int inset = 14;
        var rightRegion = new Rectangle(splitX + 6, 0, Math.Max(20, panel.Width - splitX - 6 - inset), panel.Height);
        using var rightShot = WindowCapture.Crop(panelShot, rightRegion);
        var rightLines = await OddsReader.ReadRegionAsync(rightShot, 0, rightShot.Width, ResolveTiersFromText);
        foreach (var l in rightLines)
        {
            text.Append(l.Text).Append(" | ");
            foreach (var t in l.Tiers) if (!names.Contains(t)) names.Add(t);
        }

        // read the reroll column (left of the split)
        var leftRegion = new Rectangle(inset, 0, Math.Max(20, splitX - inset - 6), panel.Height);
        using var leftShot = WindowCapture.Crop(panelShot, leftRegion);
        var leftLines = await OddsReader.ReadRegionAsync(leftShot, 0, leftShot.Width, ResolveTiersFromText);
        foreach (var l in leftLines)
        {
            leftText.Append(l.Text).Append(" | ");
            foreach (var t in l.Tiers) if (!leftNames.Contains(t)) leftNames.Add(t);
        }

        AppendLog("Odds (refresh column): " + text.ToString().Trim());
        AppendLog("Odds (reroll column):  " + leftText.ToString().Trim());
        bmp.Dispose();
        _lastRerollColumnTiers = leftNames;

        // the odds list is scrollable - tiers further down (prismatic, limit breaker,
        // generational, rivalry...) are hidden below the fold, so scroll and read again
        var seen = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        // scroll over the odds list - use the THIS TEAM tab as the scroll position
        int scrollX = _config.OddsTeamTabX >= 0 ? _config.OddsTeamTabX : panel.X + (int)(panel.Width * 0.78);
        int scrollY = _config.OddsTeamTabY >= 0 ? _config.OddsTeamTabY : panel.Y + (int)(panel.Height * 0.55);
        for (int i = 0; i < 6; i++)
        {
            InputSim.ScrollWheel(scrollX, scrollY, -3);
            await Task.Delay(300, token);
            await WaitForStableAsync(hwnd, token, maxMs: 1200);

            var b2 = WindowCapture.Capture(hwnd, _config.CaptureMethod);
            if (b2 == null) break;
            using var shot2 = WindowCapture.Crop(b2, panel);
            b2.Dispose();
            using var right2 = WindowCapture.Crop(shot2, rightRegion);
            var more = await OddsReader.ReadRegionAsync(right2, 0, right2.Width, ResolveTiersFromText);
            bool added = false;
            foreach (var l in more)
                foreach (var t in l.Tiers)
                    if (seen.Add(t)) { names.Add(t); added = true; }
            if (!added) break;
        }
        if (seen.Count > 0)
            AppendLog("Odds tiers after scrolling: " + string.Join(", ", seen));

        return (names, text.ToString().Trim());
    }

    private async Task<bool> TeamCompleteReachedAsync(IntPtr hwnd)
    {
        if (StopOnCompleteCheck.IsChecked != true) return false;
        try
        {
            var bmp = WindowCapture.Capture(hwnd, _config.CaptureMethod);
            if (bmp == null) return false;
            // the heading sits in the top centre of the screen
            var region = new Rectangle((int)(bmp.Width * 0.16), (int)(bmp.Height * 0.01),
                                      (int)(bmp.Width * 0.68), (int)(bmp.Height * 0.13));
            using var crop = WindowCapture.Crop(bmp, region);
            bmp.Dispose();

            string text = await OcrService.RecognizeAsync(crop);
            if (string.IsNullOrWhiteSpace(text))
            {
                // try again without the contrast enhancement
                var raw = await OcrService.RecognizeWordsAsync(crop, false);
                text = string.Join(" ", raw.Select(w => w.Text));
            }
            if (string.IsNullOrWhiteSpace(text)) return false;
            string upper = text.ToUpperInvariant();

            bool hasTeam = ContainsWord(upper, "TEAM");
            bool hasComplete = ContainsWord(upper, "COMPLETE");
            if (hasTeam && hasComplete)
            {
                AppendLog("TEAM COMPLETE detected: '" + text.Replace("\n", " ").Trim() + "'");
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static bool ContainsWord(string haystack, string word, int tol = 2)
    {
        if (string.IsNullOrWhiteSpace(haystack)) return false;
        var tokens = haystack.Split(new[] { ' ', '\n', '\r', '\t', ':', ',', '.', ';', '-', '*' },
                                    StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in tokens)
            if (t == word) return true;
        foreach (var t in tokens)
            if (Math.Abs(t.Length - word.Length) <= tol && Levenshtein(t, word) <= tol) return true;
        return false;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
    private async Task<(int? rerolls, int? refreshes)> ReadCountersAsync(IntPtr hwnd)
    {
        int? rerolls = null, refreshes = null;
        try
        {
            if (_config.RollY == 0) return (null, null);
            var bmp = WindowCapture.Capture(hwnd, _config.CaptureMethod);
            if (bmp == null) return (null, null);

            var texts = new List<string>();

            // rerolls counter: just above the ROLL button
            var r1 = new Rectangle(_config.RollX - 140, _config.RollY - 58, 280, 24);
            r1 = Clamp(r1, bmp);
            if (r1.Width > 60 && r1.Height > 10)
            {
                using var strip = WindowCapture.Crop(bmp, r1);
                var lines = await OddsReader.ReadRegionAsync(strip, 0, strip.Width, t => new List<string>());
                foreach (var l in lines) texts.Add(l.Text);
            }

            // refreshes counter: just above the REFRESH button
            var r2 = new Rectangle(_config.RefreshX - 140, _config.RefreshY - 57, 280, 26);
            r2 = Clamp(r2, bmp);
            if (r2.Width > 60 && r2.Height > 10)
            {
                using var strip = WindowCapture.Crop(bmp, r2);
                var lines = await OddsReader.ReadRegionAsync(strip, 0, strip.Width, t => new List<string>());
                foreach (var l in lines) texts.Add(l.Text);
            }
            bmp.Dispose();

            foreach (var t in texts)
            {
                string low = t.ToLowerInvariant();
                if (low.Contains("reroll") && TryGetNumber(t, out int rv)) rerolls = rv;
                else if (low.Contains("refresh") && TryGetNumber(t, out int rf)) refreshes = rf;
            }
            // fallback: the counters are always in order - rerolls first, refreshes second
            if (!rerolls.HasValue && texts.Count > 0 && TryGetNumber(texts[0], out int f1)) rerolls = f1;
            if (!refreshes.HasValue && texts.Count > 1 && TryGetNumber(texts[1], out int f2)) refreshes = f2;
        }
        catch { }
        return (rerolls, refreshes);
    }

    private static Rectangle Clamp(Rectangle r, Bitmap bmp)
    {
        int x = Math.Max(0, Math.Min(r.X, bmp.Width - 10));
        int y = Math.Max(0, Math.Min(r.Y, bmp.Height - 10));
        int w = Math.Min(r.Width, bmp.Width - x);
        int h = Math.Min(r.Height, bmp.Height - y);
        return new Rectangle(x, y, w, h);
    }

    private static bool TryGetNumber(string text, out int value)
    {
        value = 0;
        var digits = new System.Text.StringBuilder();
        foreach (char ch in text)
        {
            if (char.IsDigit(ch)) digits.Append(ch);
            else if (digits.Length > 0) break;      // stop at the first non-digit after digits
        }
        if (digits.Length == 0) return false;
        return int.TryParse(digits.ToString(), out value);
    }

    private List<string> ResolveTiersFromText(string text)
    {
        var found = new List<string>();
        foreach (var r in _config.Rarities)
            if (RarityCatalog.TierInText(r, text) && !found.Contains(r.Name)) found.Add(r.Name);
        return found;
    }

    private List<string> _lastRerollColumnTiers = new();

    private static void SaveDebug(Bitmap bmp, string name)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HockeyRoller");
            Directory.CreateDirectory(dir);
            bmp.Save(Path.Combine(dir, name), ImageFormat.Png);
        }
        catch { }
    }

    private async Task<bool> DecideRollTypeAsync(IntPtr hwnd, CancellationToken token, int tierMin, int tierMax)
    {
        int strategy = _config.Strategy;
        if (strategy == 1) return true;    // always refresh
        if (strategy == 2) return false;   // always reroll

        // smart: use the refresh odds of the CURRENT team
        var (names, text) = await ReadRefreshOddsAsync(hwnd, token);

        var avail = new List<string>();
        foreach (var n in names)
        {
            int gi = RarityCatalog.GroupIndex(_config.Rarities, n, _config.GroupRivalry);
            if (gi >= tierMin && gi <= tierMax) avail.Add(n);
        }

        string range = $"{_config.MinRarity}({tierMin}) .. {_config.MaxRarity}({tierMax})";
        AppendLog($"Odds: refresh column = [{string.Join(", ", names)}]");
        AppendLog($"      target range = {range}");
        AppendLog($"      eligible in refresh odds = [{string.Join(", ", avail)}]  ->  {(avail.Count > 0 ? "REFRESH" : "REROLL")}");

        // odds + sheet + current claimed:
        //   the odds say which tiers this team can roll; the roster sheet says which
        //   players those tiers actually contain; anything already claimed (or blocked)
        //   can't be taken again. If EVERY eligible player in this team is already
        //   owned, refreshing would only waste refreshes - reroll for a new team.
        _currentTeam = ExtractTeamName(text);
        _currentTeamYear = ExtractTeamYear(text);
        bool allEligibleOwned = false;
        if (avail.Count > 0 && _roster != null)
        {
            var players = _roster.TeamPlayers(_currentTeam, _currentTeamYear);
            if (players.Count > 0)
            {
                var elig = players.Where(p => EligiblePlayer(p, tierMin, tierMax)).ToList();
                int owned = elig.Count(p => _claimedPlayers.Contains(TeamRoster.Norm(p.Name)));
                allEligibleOwned = elig.Count > 0 && owned >= elig.Count;
                // per-rarity breakdown: how many of each tier are still claimable
                string Rarity(TeamRoster.Player p) => !string.IsNullOrEmpty(p.Tier) ? p.Tier : (RarityCatalog.TierFromOvr(p.Ovr, false, false) ?? "?");
                var perRarity = elig
                    .GroupBy(Rarity, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Tier: g.Key, Count: g.Count(), Owned: g.Count(p => _claimedPlayers.Contains(TeamRoster.Norm(p.Name)))))
                    .OrderByDescending(x => RarityCatalog.TierOrder(x.Tier))
                    .ToList();
                string breakdown = string.Join(", ", perRarity.Select(x => $"{x.Tier} {x.Count - x.Owned}/{x.Count}"));
                AppendLog($"      roster[{_currentTeam}] = {players.Count} player(s), eligible={elig.Count}, already have={owned}");
                AppendLog($"      available per rarity (claimable/eligible): {breakdown}");
                if (allEligibleOwned)
                    AppendLog("      all eligible players from this team are already claimed -> REROLL instead of refresh");
            }
            else
            {
                AppendLog($"      roster[{_currentTeam}] = (no sheet data)");
            }
        }

        if (names.Count == 0)
            AppendLog("      (nothing could be read from the refresh column - see odds_panel.png)");
        else if (avail.Count == 0)
            AppendLog("      (this team cannot produce a card in the target range - rolling a new team is the only option)");

        // close the odds panel (it is a modal that blocks the roll button)
        await CloseOddsPanelAsync(hwnd, token, new Rectangle(), 0, null);
        return avail.Count > 0 && !allEligibleOwned;
    }

    /// <summary>A roster player counts when its tier (from OVR) is inside the target range.</summary>
    private bool EligiblePlayer(TeamRoster.Player p, int tierMin, int tierMax)
    {
        if (p.Ovr <= 0) return false;
        string? tier = RarityCatalog.TierFromOvr(p.Ovr, false, false);
        if (tier == null) return false;
        int gi = RarityCatalog.GroupIndex(_config.Rarities, tier, _config.GroupRivalry);
        if (gi < tierMin || gi > tierMax) return false;
        // min OVR rule (any rarity) from the app
        if (_config.MinOvr > 0 && p.Ovr < _config.MinOvr) return false;
        return true;
    }


    /// <summary>Pulls the in-game team year (e.g. 1997) out of the odds panel text.</summary>
    private static int ExtractTeamYear(string panelText)
    {
        if (string.IsNullOrWhiteSpace(panelText)) return 0;
        // team keys look like "Anaheim Ducks 1997"; the panel shows the team + year
        var ms = System.Text.RegularExpressions.Regex.Matches(panelText, @"\b(1[89]\d{2}|20\d{2})\b");
        // prefer a 4-digit year that is not a count
        foreach (System.Text.RegularExpressions.Match m in ms)
            if (int.TryParse(m.Value, out int y) && y >= 1880 && y <= 2100) return y;
        return 0;
    }    /// <summary>Pulls the in-game team name out of the odds panel text.</summary>
    private static string ExtractTeamName(string panelText)
    {
        if (string.IsNullOrWhiteSpace(panelText)) return "";
        // the panel header shows "TEAM | REFRESH -- <Team Name>"
        var m = System.Text.RegularExpressions.Regex.Match(panelText,
            @"REFRESH\s*-+\s*(?<t>[^|]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups["t"].Value.Trim();
        m = System.Text.RegularExpressions.Regex.Match(panelText,
            @"\bTeam\b\s*(?<t>[^|]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups["t"].Value.Trim();
        return "";
    }
    private async Task CloseOddsPanelAsync(IntPtr hwnd, CancellationToken token, Rectangle oddsRect, uint beforeHash,
                                            System.Drawing.Point? detectedPanelX)
    {
        if (_config.OddsCloseX >= 0) ClickAt(hwnd, _config.OddsCloseX, _config.OddsCloseY, _clickMethod);
        else
        {
            var (ox, oy) = ResolveOddsButton(hwnd);
            if (ox != 0) ClickAt(hwnd, ox, oy, _clickMethod);
        }
        // no wait after closing the panel - the click on the roll button is enough
        await Task.Delay(150, token);
    }

    private uint CaptureRegionHash(IntPtr hwnd, Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return 0;
        var bmp = WindowCapture.Capture(hwnd, _config.CaptureMethod);
        if (bmp == null) return 0;
        using var crop = WindowCapture.Crop(bmp, rect);
        bmp.Dispose();
        return HashBitmap(crop);
    }

    private async Task<(List<(Rectangle slice, string? tier, bool ribbon, string detail, double score, int ovr, double margin)> found, uint hash, uint winHash)>
        DetectAllAsync(IntPtr hwnd, CancellationToken token)
    {
        var empty = new List<(Rectangle, string?, bool, string, double, int, double)>();
        var bmp = WindowCapture.Capture(hwnd, _config.CaptureMethod);
        if (bmp == null) { AppendLog("Capture failed."); return (empty, 0, 0); }

        var cardRect = new Rectangle(_config.CardX, _config.CardY, _config.CardW, _config.CardH);
        using var region = WindowCapture.Crop(bmp, cardRect);

        uint hash = HashBitmap(region);
        // left panel = roll buttons + counters + card area (excludes the animated roster)
        uint winHash = HashBitmap(CropSafe(bmp, new Rectangle(0, 0, Math.Min(700, bmp.Width), bmp.Height)));
        bmp.Dispose();

        var found = new List<(Rectangle slice, string? tier, bool ribbon, string detail, double score, int ovr, double margin)>();

        SaveDebug(region, "cards_last.png");      // always keep the last region for inspection

        // The ONLY tier signal is the colour of the reference pixel on each card.
        var cells = SliceRegions(new Rectangle(0, 0, region.Width, region.Height),
                                _config.CardCount, _config.CardLayout);

        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            using var card = WindowCapture.Crop(region, cell);

            bool ribbon = DiagonalStripDetector.HasDiagonalStrip(card, out _);

            var (ovr, ovrBox) = await ReadOvrWithBoxAsync(card);
            int ovr2 = ovr;
            if (ovr2 == 0) ovr2 = await ReadOvrAsync(card);

            // The ONLY tier signal is the OVR number.
            bool vsSymbol = await HasVsSymbolAsync(card);      // Rivalry needs it at 112
            string? tier = DetectTierShiny(card, ovr2, vsSymbol, out bool shiny);

            string detail = $"ovr={ovr2}{(shiny ? " SHINY+2" : "")}{(vsSymbol ? " vs" : "")}" +
                            (tier == null ? " (no tier for this OVR)" : "");

            found.Add((cell, tier, ribbon, detail, 0, ovr2, 0));
        }
        return (found, hash, winHash);
    }
    private static bool HasRainbowBorder(Bitmap card)
    {
        try
        {
            var prof = TierProfile.FromWindow(card, new Rectangle(0, 0, card.Width, card.Height));
            // shiny means a genuine rainbow border - require many distinct hues, don't
            // accept a wide hue spread or mild dispersion (those false-positive shiny)
            return prof.RainbowBins >= 8;
        }
        catch { return false; }
    }


    /// <summary>Reads the player name printed on a card.</summary>
    private static async Task<string?> ReadPlayerNameAsync(Bitmap card)
    {
        try
        {
            if (card.Width < 30 || card.Height < 30) return null;
            // the name sits in a band across the middle-lower part of the card
            int top = (int)(card.Height * 0.52), bot = (int)(card.Height * 0.72);
            using var band = WindowCapture.Crop(card, new Rectangle(0, top, card.Width, Math.Max(10, bot - top)));
            string txt = await OcrService.RecognizeAsync(band);
            var parts = txt.Replace("\r", " ").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var name = string.Join(" ", parts.Where(p => p.Any(char.IsLetter)));
            name = name.Trim();
            return name.Length > 1 ? name : null;
        }
        catch { return null; }
    }    private string? DetectTierShiny(Bitmap card, int rawOvr, bool vsSymbol, out bool shiny)
    {
        shiny = false;
        if (rawOvr <= 0) return null;

        bool hasRainbow = HasRainbowBorder(card);
        if (!hasRainbow)
            return RarityCatalog.TierFromOvr(rawOvr, false, vsSymbol);

        // PRISMATIC has a WHITE body inside its rainbow border AND is OVR 105-107; other
        // tiers do not (Franchise is a dark-purple strip over black, etc.). Sample a few
        // pixels just inside the border AND really close to it: white inside + Prismatic
        // OVR => Prismatic. A shiny lower-tier card is not Prismatic.
        bool isPrismatic = rawOvr >= 105 && rawOvr <= 107 && HasWhiteInsideBorder(card);
        if (isPrismatic)
            return RarityCatalog.TierFromOvr(rawOvr, false, vsSymbol);   // Prismatic, never shiny

        // rainbow on a non-Prismatic card = shiny (adds +2 OVR)
        string? tier = RarityCatalog.TierFromOvr(rawOvr, true, vsSymbol);

        // Prismatic and everything above it can never be shiny
        if (RarityCatalog.TierOrder(tier) >= RarityCatalog.TierOrder("Prismatic"))
            return RarityCatalog.TierFromOvr(rawOvr, false, vsSymbol);

        shiny = true;
        return tier;
    }
    private static bool HasWhiteInsideBorder(Bitmap card)
    {
        int w = card.Width, h = card.Height;
        if (w < 12 || h < 12) return false;
        int margin = Math.Max(2, Math.Min(w, h) / 40);     // really close to the border
        int inset = margin + 2;                            // just inside it
        int shift = margin + 4;                            // a bit higher/lower to dodge anything overlaid

        int white = 0, total = 0;
        for (int i = 1; i <= 6; i++)                        // more points along every edge
        {
            int fx = w * i / 7, fy = h * i / 7;
            var pts = new[]
            {
                (fx, margin),  (fx, h - margin),  (margin, fy),  (w - margin, fy),    // close to it
                (fx, inset),   (fx, h - inset),   (inset, fy),   (w - inset, fy),     // just inside it
                (fx, shift),   (fx, h - shift),   (shift, fy),   (w - shift, fy),     // a bit higher/lower
            };
            foreach (var (px, py) in pts)
            {
                if (px < 0 || py < 0 || px >= w || py >= h) continue;
                total++;
                var c = card.GetPixel(px, py);
                if (c.R >= 200 && c.G >= 200 && c.B >= 200) white++;   // white
            }
        }
        return total > 0 && white * 2 >= total;    // majority white
    }
    private static async Task<bool> HasVsSymbolAsync(Bitmap card)
    {
        try
        {
            if (card.Width < 20 || card.Height < 20) return false;
            int w = card.Width / 4, h = card.Height / 4;
            using var corner = WindowCapture.Crop(card,
                new Rectangle(card.Width - w, card.Height - h, w, h));
            using var big = ScaleBitmap(corner, 3f);
            string text = await OcrService.RecognizeAsync(big);
            string t = text.Replace(" ", "").Replace(".", "").Replace(":", "").ToLowerInvariant();
            return t.Contains("vs");
        }
        catch { return false; }
    }
    private static async Task<(int ovr, Rectangle box)> ReadOvrWithBoxAsync(Bitmap cell)
    {
        foreach (var (scale, enhance, grey) in new[]
                 { (2f, true, false), (3f, false, false), (3f, false, true), (2f, false, false) })
        {
            try
            {
                using var scaled = ScaleBitmap(cell, scale);
                using var img = grey ? GreyBitmap(scaled) : null;
                var source = grey ? img! : scaled;
                var words = await OcrService.RecognizeWordsAsync(source, enhance);
                Rectangle best = Rectangle.Empty;
                int value = 0;
                foreach (var wr in words)
                {
                    var r = new Rectangle((int)(wr.Rect.X / scale), (int)(wr.Rect.Y / scale),
                                          Math.Max(1, (int)(wr.Rect.Width / scale)),
                                          Math.Max(1, (int)(wr.Rect.Height / scale)));
                    if (r.X > cell.Width * 0.62) continue;      // only the top-left corner
                    if (r.Y > cell.Height * 0.45) continue;
                    if (r.Height < 6) continue;
                    if (!TryGetNumber(wr.Text, out int v) || v <= 0 || v >= 200) continue;
                    if (best == Rectangle.Empty || r.Height > best.Height) { best = r; value = v; }
                }
                if (best != Rectangle.Empty) return (value, best);
            }
            catch { }
        }
        // last resort: the dedicated OVR reader
        int fallback = await ReadOvrAsync(cell);
        if (fallback > 0) return (fallback, Rectangle.Empty);
        return (0, Rectangle.Empty);
    }
    private static async Task<int> ReadOvrFromCellAsync(Bitmap region, Rectangle cell)
    {
        try
        {
            var area = new Rectangle(cell.X, cell.Y, cell.Width, (int)(cell.Height * 0.34));
            area = Clamp(area, region);
            if (area.Width < 20 || area.Height < 15) return 0;

            using var crop = WindowCapture.Crop(region, area);

            // 2x with the contrast stretch reads the digits reliably
            using var stretched = ScaleBitmap(crop, 2f);
            int v = await ReadNumberFromAsync(stretched, true);
            if (v > 0) return v;

            // 3x greyscale is the other variant that works well
            using var grey = GreyBitmap(ScaleBitmap(crop, 3f));
            v = await ReadNumberFromAsync(grey, false);
            if (v > 0) return v;

            using var plain = ScaleBitmap(crop, 3f);
            return await ReadNumberFromAsync(plain, false);
        }
        catch { return 0; }
    }

    private static async Task<int> ReadNumberFromAsync(Bitmap img, bool enhance)
    {
        var words = await OcrService.RecognizeWordsAsync(img, enhance);
        foreach (var w in words)
        {
            if (w.Rect.Y > img.Height * 0.45) continue;
            if (w.Rect.X > img.Width * 0.92) continue;
            if (TryGetNumber(w.Text, out int v) && v > 0 && v < 200) return v;
        }
        return 0;
    }
    private static async Task<Rectangle> FindOvrAsync(Bitmap cell)
    {
        foreach (var (scale, enhance) in new[] { (2f, false), (2f, true), (3f, false), (4f, false) })
        {
            try
            {
                using var big = ScaleBitmap(cell, scale);
                var words = await OcrService.RecognizeWordsAsync(big, enhance);
                Rectangle best = Rectangle.Empty;
                foreach (var wr in words)
                {
                    var r = new Rectangle((int)(wr.Rect.X / scale), (int)(wr.Rect.Y / scale),
                                          Math.Max(1, (int)(wr.Rect.Width / scale)),
                                          Math.Max(1, (int)(wr.Rect.Height / scale)));
                    if (r.X > cell.Width * 0.62) continue;      // only the top-left corner
                    if (r.Y > cell.Height * 0.45) continue;
                    if (r.Height < 6) continue;
                    if (!TryGetNumber(wr.Text, out int v) || v <= 0 || v >= 200) continue;
                    // the OVR is the big number - keep the tallest one
                    if (best == Rectangle.Empty || r.Height > best.Height) best = r;
                }
                if (best != Rectangle.Empty) return best;
            }
            catch { }
        }
        return Rectangle.Empty;
    }

    private static Bitmap GreyBitmap(Bitmap src)
    {
        var b = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var c = src.GetPixel(x, y);
                int l = (c.R * 30 + c.G * 59 + c.B * 11) / 100;
                b.SetPixel(x, y, Color.FromArgb(l, l, l));
            }
        return b;
    }
    private static async Task<List<(int value, Rectangle rect)>> ReadOvrNumbersAsync(Bitmap region)
    {
        var result = new List<(int, Rectangle)>();
        try
        {
            if (region.Width < 40 || region.Height < 40) return result;

            // 2x makes the digits readable, and a white border gives the OCR some margin
            using var big = ScaleBitmap(region, 2f);
            using var padded = PadBitmap(big, 20, Color.White);
            var words = await OcrService.RecognizeWordsAsync(padded, false);

            foreach (var w in words)
            {
                if (!TryGetNumber(w.Text, out int v) || v <= 0 || v >= 200) continue;
                // undo the padding and the 2x scale to get region coordinates
                int x = (w.Rect.X - 20) / 2;
                int y = (w.Rect.Y - 20) / 2;
                int ww = w.Rect.Width / 2, hh = w.Rect.Height / 2;
                result.Add((v, new Rectangle(x, y, ww, hh)));
            }
        }
        catch { }
        return result;
    }
    private static int OvrIn(Rectangle card, List<(int value, Rectangle rect)> numbers)
    {
        // a number counts as this card's if it sits in the upper-left part of the card
        var rx = (int)(card.Width * 0.45);
        var ry = (int)(card.Height * 0.30);
        var area = new Rectangle(card.X, card.Y, rx, ry);

        int best = 0, bestDist = int.MaxValue;
        foreach (var (value, rect) in numbers)
        {
            if (!area.IntersectsWith(rect)) continue;
            int d = Math.Abs(rect.X - card.X) + Math.Abs(rect.Y - card.Y);
            if (d < bestDist) { bestDist = d; best = value; }
        }
        if (best > 0) return best;

        // fall back to the closest number to the card's top-left corner
        foreach (var (value, rect) in numbers)
        {
            int d = Math.Abs(rect.X - card.X) + Math.Abs(rect.Y - card.Y);
            if (d < bestDist) { bestDist = d; best = value; }
        }
        // only accept it if it is reasonably close to this card
        return bestDist <= (card.Width + card.Height) / 3 ? best : 0;
    }
    private static async Task<int> ReadOvrAsync(Bitmap card)
    {
        try
        {
            if (card.Width < 60 || card.Height < 60) return 0;

            var variants = new List<Bitmap>();
            try
            {
                // 2x is the most reliable variant - the digits are big and crisp
                variants.Add(ScaleBitmap(card, 2f));                       // 0
                variants.Add(new Bitmap(card));                            // 1
                variants.Add(ScaleBitmap(BinaryBitmap(card), 2f));         // 2 - low contrast cards
                variants.Add(ScaleBitmap(InvertBitmap(card), 2f));         // 3 - light text on dark
                variants.Add(PadBitmap(ScaleBitmap(card, 2f), 20, Color.White));  // 4
                variants.Add(PadBitmap(new Bitmap(card), 20, Color.White));       // 5
                // tight crop of the number itself, padded to a size the OCR likes
                var numRect = new Rectangle((int)(card.Width * 0.02), (int)(card.Height * 0.01),
                                            (int)(card.Width * 0.28), (int)(card.Height * 0.17));
                numRect = Clamp(numRect, card);
                using (var num = WindowCapture.Crop(card, numRect))
                {
                    int targetW = Math.Max(170, num.Width * 3);
                    int targetH = Math.Max(120, num.Height * 3);
                    var padded = new Bitmap(targetW, targetH, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(padded))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(num, (targetW - num.Width) / 2, (targetH - num.Height) / 2, num.Width * 2, num.Height * 2);
                    }
                    variants.Add(padded);                                   // 6
                }
            }
            catch { }

            foreach (var v in variants)
            {
                try
                {
                    var words = await OcrService.RecognizeWordsAsync(v, false);
                    foreach (var w in words)
                    {
                        if (w.Rect.Y > v.Height * 0.34) continue;
                        if (w.Rect.X > v.Width * 0.55) continue;
                        if (TryGetNumber(w.Text, out int ovr) && ovr > 0 && ovr < 200) return ovr;
                    }
                }
                catch { }
            }
            foreach (var v in variants) v.Dispose();
        }
        catch { }
        return 0;
    }

    private static Bitmap ScaleBitmap(Bitmap src, float factor)
    {
        var b = new Bitmap((int)(src.Width * factor), (int)(src.Height * factor), PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(b);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, b.Width, b.Height);
        return b;
    }

    private static Bitmap PadBitmap(Bitmap src, int pad, Color bg)
    {
        var b = new Bitmap(src.Width + pad * 2, src.Height + pad * 2, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(b);
        g.Clear(bg);
        g.DrawImage(src, pad, pad);
        return b;
    }
    private static Bitmap BinaryBitmap(Bitmap src)
    {
        var b = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        int min = 255, max = 0;
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var c = src.GetPixel(x, y);
                int l = (c.R * 30 + c.G * 59 + c.B * 11) / 100;
                if (l < min) min = l;
                if (l > max) max = l;
            }
        int cut = (min + max) / 2;
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var c = src.GetPixel(x, y);
                int l = (c.R * 30 + c.G * 59 + c.B * 11) / 100;
                byte v = (byte)(l > cut ? 255 : 0);
                b.SetPixel(x, y, Color.FromArgb(v, v, v));
            }
        return b;
    }
    private static Bitmap InvertBitmap(Bitmap src)
    {
        var b = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var c = src.GetPixel(x, y);
                int l = 255 - (c.R * 30 + c.G * 59 + c.B * 11) / 100;
                b.SetPixel(x, y, Color.FromArgb(l, l, l));
            }
        return b;
    }
    private async Task<bool> WaitForStableAsync(IntPtr hwnd, CancellationToken token, int maxMs = 0, int stableMs = 250, int pollMs = 80)
    {
        if (WaitAnimCheck.IsChecked != true) return true;   // animation waiting disabled
        if (maxMs <= 0) maxMs = _config.SettleMaxMs;
        if (maxMs <= 0) maxMs = 9000;

        byte[]? prev = null;
        int stableFor = 0;
        int elapsed = 0;

        while (elapsed < maxMs && !token.IsCancellationRequested)
        {
            byte[]? cur = null;
            var bmp = WindowCapture.Capture(hwnd, _config.CaptureMethod);
            if (bmp != null)
            {
                // watch the left panel (cards + counters + buttons), not the whole window
                using var region = CropSafe(bmp, new Rectangle(0, 0, Math.Min(700, bmp.Width), bmp.Height));
                cur = SampleGrid(region, 48);
                bmp.Dispose();
            }

            if (cur != null)
            {
                if (prev != null)
                {
                    double changed = ChangeRatio(prev, cur);
                    // a few percent of pixels moving (shimmer) still counts as settled
                    if (changed < 0.02) stableFor += pollMs;
                    else stableFor = 0;
                }
                prev = cur;
            }

            if (stableFor >= stableMs) return true;

            await Task.Delay(pollMs, token);
            elapsed += pollMs;
        }
        return false;
    }

    private static byte[] SampleGrid(Bitmap bmp, int n)
    {
        var buf = new byte[n * n];
        for (int y = 0; y < n; y++)
        {
            int sy = Math.Min(bmp.Height - 1, y * bmp.Height / n);
            for (int x = 0; x < n; x++)
            {
                int sx = Math.Min(bmp.Width - 1, x * bmp.Width / n);
                var c = bmp.GetPixel(sx, sy);
                buf[y * n + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
            }
        }
        return buf;
    }

    private static double ChangeRatio(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 1;
        int diff = 0;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 10) diff++;
        return (double)diff / a.Length;
    }

    private static Bitmap CropSafe(Bitmap bmp, Rectangle r)
    {
        var rect = new Rectangle(
            Math.Max(0, r.X), Math.Max(0, r.Y),
            Math.Min(r.Width, bmp.Width - r.X), Math.Min(r.Height, bmp.Height - r.Y));
        if (rect.Width <= 0 || rect.Height <= 0) return new Bitmap(1, 1);
        return WindowCapture.Crop(bmp, rect);
    }

    private static uint HashBitmap(Bitmap bmp)
    {
        // cheap 32x32 greyscale hash used to tell "screen changed" from "screen identical"
        const int n = 32;
        uint hash = 2166136261;
        for (int y = 0; y < n; y++)
        {
            int sy = Math.Min(bmp.Height - 1, y * bmp.Height / n);
            for (int x = 0; x < n; x++)
            {
                int sx = Math.Min(bmp.Width - 1, x * bmp.Width / n);
                var c = bmp.GetPixel(sx, sy);
                int g = (c.R * 30 + c.G * 59 + c.B * 11) / 100;
                hash ^= (uint)g;
                hash *= 16777619;
            }
        }
        return hash;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var w = SelectedWindow();
        if (w == null) { MessageBox.Show("Select window."); return; }
        if (_config.Rarities.Count == 0) { MessageBox.Show("No rarity tiers loaded."); return; }
        if (_config.CardW == 0) { MessageBox.Show("Set card region."); return; }
        if (_config.RollX == 0 && _config.RollY == 0) { MessageBox.Show("Set roll button."); return; }

        _config.Hwnd = IntPtrHwnd.FromIntPtr(w.Hwnd);
        _config.WindowTitle = w.Title;
        int.TryParse(DelayText.Text, out int d); _config.DelayMs = d > 0 ? d : 1500;
        int.TryParse(SettleText.Text, out int sm); _config.SettleMaxMs = sm > 0 ? sm : 9000;
        int.TryParse(MinOvrText.Text, out int mo); _config.MinOvr = mo > 0 ? mo : 0;
        _config.AcceptAnyTierByOvr = AcceptAnyByOvrCheck.IsChecked == true;
        _config.OvrOnly = OvrOnlyCheck.IsChecked == true;
        int.TryParse(RefreshCapText.Text, out int rc); _config.RefreshCap = rc > 0 ? rc : 0;
        _config.MinRarity = MinRarityCombo.SelectedItem?.ToString() ?? _config.MinRarity;
        _config.MaxRarity = MaxRarityCombo.SelectedItem?.ToString() ?? _config.MaxRarity;
        _config.Strategy = StrategyCombo.SelectedIndex >= 0 ? StrategyCombo.SelectedIndex : 0;
        _config.CaptureMethod = CaptureMethodCombo.SelectedIndex >= 0 ? CaptureMethodCombo.SelectedIndex : 0;
        _config.GroupRivalry = GroupRivalryCheck.IsChecked == true;
        _config.CardCount = CardCountCombo.SelectedIndex >= 0 ? CardCountCombo.SelectedIndex + 1 : 1;
        _config.CardLayout = ComboContent(CardLayoutCombo, "2 x 2 grid");

        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        AppendLog("Started.");
        InputSim.Focus(w.Hwnd);
        await Task.Delay(500);
        if (MinimizeWhileRunningCheck.IsChecked == true) WindowState = WindowState.Minimized;

        try
        {
            int tierMin = RarityCatalog.GroupIndex(_config.Rarities, _config.MinRarity, _config.GroupRivalry);
            int tierMax = RarityCatalog.GroupIndex(_config.Rarities, _config.MaxRarity, _config.GroupRivalry);
            if (tierMin > tierMax) { var tmp = tierMin; tierMin = tierMax; tierMax = tmp; }

            // the odds belong to the current team - refreshing keeps the same team/year,
            // so only re-read them after a reroll (new team)
            bool? teamOdds = null;
            // after picking a card the game requires a full reroll before the next pick
            bool rerollNext = false;

            while (!token.IsCancellationRequested)
            {
              try
              {
                // stop as soon as the team is complete
                if (await TeamCompleteReachedAsync(w.Hwnd))
                {
                    AppendLog("TEAM COMPLETE - stopping rolling.");
                    _cts?.Cancel();
                    break;
                }

                // stop when we run out of rerolls or refreshes
                var (rerollsLeft, refreshesLeft) = await ReadCountersAsync(w.Hwnd);
                if (rerollsLeft == 0 || refreshesLeft == 0)
                {
                    AppendLog(rerollsLeft == 0 && refreshesLeft == 0
                        ? "Out of rerolls AND refreshes - stopping."
                        : (rerollsLeft == 0 ? "Out of rerolls - stopping." : "Out of refreshes - stopping."));
                    _cts?.Cancel();
                    break;
                }
                if (rerollsLeft.HasValue || refreshesLeft.HasValue)
                    AppendLog($"Remaining: rerolls={rerollsLeft?.ToString() ?? "?"} refreshes={refreshesLeft?.ToString() ?? "?"}");

                bool useRefresh;
                if (rerollNext)
                {
                    // a card was just picked - the game wants a full reroll now
                    rerollNext = false;
                    useRefresh = false;
                    teamOdds = null;
                    AppendLog("Card picked - rerolling for a new team.");
                }
                else if (teamOdds.HasValue)
                {
                    // same team as last cycle - the odds cannot have changed
                    useRefresh = teamOdds.Value;
                }
                else
                {
                    // the refresh decision always uses the rarity range + the odds menu,
                    // even in OVR-only mode (that only changes which card gets taken)
                    useRefresh = await DecideRollTypeAsync(w.Hwnd, token, tierMin, tierMax);
                    teamOdds = useRefresh;
                }

                // refresh cap: if we keep refreshing the same team without getting an
                // eligible card, eventually roll a new team instead
                int cap = _config.RefreshCap;
                bool capTriggered = false;   // this cycle was a forced reroll after the cap
                if (cap > 0 && useRefresh)
                {
                    _refreshesWithoutHit++;
                    if (_refreshesWithoutHit >= cap)
                    {
                        AppendLog($"Refresh cap reached ({_refreshesWithoutHit}/{cap}) with no eligible card - rerolling for a new team.");
                        useRefresh = false;
                        capTriggered = true;
                    }
                }
                if (!useRefresh)
                {
                    _refreshesWithoutHit = 0;   // new team -> counter restarts
                    teamOdds = null;             // new team -> re-read the odds next cycle
                }
                if (capTriggered)
                    AppendLog("Cap cycle: not claiming anything unless a card is eligible.");

                var (btnX, btnY) = ResolveButton(w.Hwnd, useRefresh);
                AppendLog($"cycle: {(useRefresh ? "Refresh" : "Reroll")} button at client({btnX},{btnY})");
                ClickRoll(w.Hwnd, useRefresh);
                await Task.Delay(_config.DelayMs, token);
                // then wait until the roll animation has actually finished
                await WaitForStableAsync(w.Hwnd, token);

                var (found, hash, winHash) = await DetectAllAsync(w.Hwnd, token);

                // if the screen did not change, try small offsets around the button and
                // then the other click methods (a pick on a button border often misses)
                var attempts = new (int dx, int dy, int method)[]
                {
                    (0, 0, _clickMethod),
                    (0, 14, _clickMethod),
                    (0, -14, _clickMethod),
                    (0, 0, 1),
                    (0, 0, 2),
                };
                int tries = 0;
                while (tries < attempts.Length && !token.IsCancellationRequested)
                {
                    bool changed = (winHash != 0 && winHash != _lastWinHash) || (hash != 0 && hash != _lastRegionHash);
                    if (changed) break;
                    var a = attempts[tries];
                    tries++;
                    var (rx, ry) = ResolveButton(w.Hwnd, useRefresh);
                    AppendLog($"Screen unchanged - retry {tries}: offset ({a.dx},{a.dy}) method {a.method} at ({rx},{ry}).");
                    ClickAt(w.Hwnd, rx + a.dx, ry + a.dy, a.method);
                    await Task.Delay(_config.DelayMs, token);
                    (found, hash, winHash) = await DetectAllAsync(w.Hwnd, token);
                    if (winHash != 0 && winHash != _lastWinHash)
                    {
                        _clickMethod = a.method;
                        _rollOffsetX = a.dx; _rollOffsetY = a.dy;
                        if (a.dx != 0 || a.dy != 0)
                            AppendLog($"Learned click offset ({a.dx},{a.dy}) - using it from now on.");
                    }
                }
                _lastRegionHash = hash;
                _lastWinHash = winHash;

                for (int i = 0; i < found.Count; i++)
                    AppendLog($"  card {i + 1}/{found.Count}: {found[i].tier ?? "(unknown)"}  ribbon={found[i].ribbon}  {found[i].detail}");

                // Target rules:
                //   - cards whose rarity is inside [Min..Max] are always taken
                //   - with "accept any tier by OVR" ticked, a card is ALSO taken when its
                //     rating is at/above the minimum OVR, no matter the rarity
                // The highest rarity wins; for equal rarity the higher OVR wins.
                int minOvr = _config.MinOvr;
                bool anyByOvr = _config.AcceptAnyTierByOvr && minOvr > 0;
                bool ovrOnly = _config.OvrOnly && minOvr > 0;   // min OVR of 0 = switch ignored
                var targets = found
                    // in OVR-only mode the rarity is irrelevant, so a card with an
                    // unknown tier still counts
                    .Where(f => f.tier != null || ovrOnly)
                    .Select(f =>
                    {
                        int grp = f.tier != null
                            ? RarityCatalog.GroupIndex(_config.Rarities, f.tier, _config.GroupRivalry)
                            : -1;
                        bool inRange = grp >= tierMin && grp <= tierMax;
                        // a card rated at/above the minimum counts no matter what tier it
                        // was detected as - the rating is the signal that matters here
                        bool byOvr = (anyByOvr || ovrOnly) && f.ovr >= minOvr;
                        return (f, grp, inRange, byOvr);
                    })
                    .Where(x => ovrOnly ? x.byOvr : (x.inRange || x.byOvr))
                    .OrderByDescending(x => ovrOnly ? x.f.ovr : ((x.byOvr && !x.inRange) ? int.MaxValue : 0))
                    .ThenByDescending(x => x.grp)
                    .ThenByDescending(x => x.f.ovr)
                    .ToList();

                var bestTarget = targets.Count > 0 ? targets[0].f
                                : default((Rectangle slice, string? tier, bool ribbon, string detail, double score, int ovr, double margin));

                if (targets.Count > 0)
                {
                    // if the highest tier is blocked (already on team / position full) try the next best
                    if (bestTarget.ribbon)
                    {
                        AppendLog($"Blocked (position full / already on team) on {bestTarget.tier ?? "card"} - skipping.");
                        var alt = targets.Select(t => t.f).FirstOrDefault(f => !f.ribbon);
                        bestTarget = alt.slice != Rectangle.Empty ? alt : default;
                    }

                    if (bestTarget.slice != Rectangle.Empty)
                    {
                        // the colour of the reference pixel decides everything - a card
                        // only has a tier when the colour was close to that tier's colour
                        // AND clearly closer than to any other tier (see DetectAllAsync).
                        // A card that qualifies on its OVR may have an unknown tier,
                        // because the rarity is irrelevant for that rule.
                        bool ovrQualified = targets.Count > 0 && targets[0].byOvr;

                        bool blocked = false;
                        if (_config.BlockedW > 0 && _config.BlockedR >= 0)
                        {
                            var bbmp = WindowCapture.Capture(w.Hwnd, _config.CaptureMethod);
                            if (bbmp != null)
                            {
                                var br = new Rectangle(_config.BlockedX, _config.BlockedY, _config.BlockedW, _config.BlockedH);
                                var bc = ColorMatch.AverageFast(bbmp, br);
                                double bd = ColorMatch.Distance(bc, Color.FromArgb(_config.BlockedR, _config.BlockedG, _config.BlockedB));
                                if (bd <= _config.BlockedThreshold) blocked = true;
                                bbmp.Dispose();
                            }
                        }
                        if (blocked)
                        {
                            AppendLog("Blocked (manual region) - skipping claim.");
                        }
                        else
                        {
                            int cx = _config.CardX + bestTarget.slice.X + bestTarget.slice.Width / 2;
                            int cy = _config.CardY + bestTarget.slice.Y + bestTarget.slice.Height / 2;
                            ClickAt(w.Hwnd, cx, cy);
                            AppendLog($"TARGET FOUND: {bestTarget.tier ?? "unknown tier"}{(ovrQualified ? $" (by OVR {bestTarget.ovr})" : "")} - clicked card {found.IndexOf(bestTarget) + 1}.");
                            _refreshesWithoutHit = 0;   // got what we came for
                            rerollNext = true;          // only one pick per team - reroll next
                            // remember the player so it counts as "already claimed"
                            try
                            {
                                var full = WindowCapture.Capture(w.Hwnd, _config.CaptureMethod);
                                if (full != null)
                                {
                                    var sliceRect = new Rectangle(_config.CardX + bestTarget.slice.X, _config.CardY + bestTarget.slice.Y, bestTarget.slice.Width, bestTarget.slice.Height);
                                    using var claimedCard = WindowCapture.Crop(full, sliceRect);
                                    full.Dispose();
                                    string? pname = await ReadPlayerNameAsync(claimedCard);
                                    if (!string.IsNullOrWhiteSpace(pname)) _claimedPlayers.Add(TeamRoster.Norm(pname));
                                }
                            }
                            catch { }
                            await Task.Delay(700, token);
                            await WaitForStableAsync(w.Hwnd, token);
                        }
                    }
                    else
                    {
                    }
                }
                else
                {
                    // nothing eligible - the next roll button press dismisses the card
                }

                await Task.Delay(200, token);
              }
              catch (OperationCanceledException) { break; }
              catch (Exception ex)
              {
                  // one bad cycle must never kill the whole run
                  AppendLog("Cycle error: " + ex.Message + " - continuing.");
                  try { await Task.Delay(600, token); } catch { break; }
              }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendLog("Error: " + ex.Message); }
        finally
        {
            StartBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            AppendLog("Stopped.");
        }
    }

    private List<string> AvailableTiers(string text, int tierMin, int tierMax)
    {
        var result = new List<string>();
        foreach (var r in _config.Rarities)
        {
            int ti = RarityCatalog.GroupIndex(_config.Rarities, r.Name, _config.GroupRivalry);
            if (ti < tierMin || ti > tierMax) continue;
            if (RarityCatalog.TierInText(r, text) && !result.Contains(r.Name)) result.Add(r.Name);
        }
        return result;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private int TierIndex(string name)
    {
        for (int i = 0; i < _config.Rarities.Count; i++)
            if (string.Equals(_config.Rarities[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private void AppendLog(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            LogBox.ScrollToEnd();
        });
    }
}

// Simple input dialog
public class InputDialog : Window
{
    private readonly TextBox _tb = new();
    public string ResponseText => _tb.Text;
    public InputDialog(string prompt, string defaultText)
    {
        Title = "Input";
        Width = 360; Height = 160;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var sp = new StackPanel { Margin = new Thickness(10) };
        sp.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 5) });
        _tb.Text = defaultText;
        sp.Children.Add(_tb);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 5, 0) };
        ok.Click += (s, e) => { DialogResult = true; Close(); };
        var cancel = new Button { Content = "Cancel", Width = 70 };
        cancel.Click += (s, e) => { DialogResult = false; Close(); };
        btns.Children.Add(ok); btns.Children.Add(cancel);
        sp.Children.Add(btns);
        Content = sp;
    }
}
