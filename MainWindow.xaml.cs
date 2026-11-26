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
        LoadReferences();
    }

    // ---------------- ESC = stop rolling and close ----------------
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
        RerollPtText.Text = _config.RerollX == 0 && _config.RerollY == 0 ? "" : $"{_config.RerollX},{_config.RerollY}";
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
        RaritySampleText.Text = _config.RaritySampleW == 0 ? "" : $"{_config.RaritySampleX},{_config.RaritySampleY} {_config.RaritySampleW}x{_config.RaritySampleH}";
        DismissPtText.Text = _config.DismissX < 0 ? "" : $"{_config.DismissX},{_config.DismissY}";
        DelayText.Text = _config.DelayMs.ToString();
        ThresholdText.Text = _config.MatchThreshold.ToString();
        SettleText.Text = _config.SettleMaxMs.ToString();
        if (_config.SettleMaxMs <= 0) _config.SettleMaxMs = 5000;
        RefThresholdText.Text = _config.ReferenceThreshold.ToString();
        RefreshCapText.Text = _config.RefreshCap.ToString();
        StrategyCombo.SelectedIndex = _config.Strategy;
        DismissEscCheck.IsChecked = _config.DismissWithEsc;
        GroupRivalryCheck.IsChecked = _config.GroupRivalry;
        CaptureMethodCombo.SelectedIndex = _config.CaptureMethod;
        DetectModeCombo.SelectedIndex = _config.DetectMode;
        RarityList.ItemsSource = null;
        RarityList.ItemsSource = _config.Rarities;
    }

    private bool _updatingLayout;

    /// <summary>Reads the selected combo item's Content (not ToString(), which yields the type name).</summary>
    private static string ComboContent(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Content != null)
            return item.Content.ToString() ?? fallback;
        if (combo.SelectedItem is string s) return s;
        return fallback;
    }

    private async Task DismissAsync(IntPtr hwnd, CancellationToken token)
    {
        if (_config.DismissWithEsc)
        {
            InputSim.PressEscape();
            await Task.Delay(400, token);
        }
        else if (_config.DismissX >= 0)
        {
            ClickAt(hwnd, _config.DismissX, _config.DismissY);
            await Task.Delay(400, token);
        }
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
        int.TryParse(ThresholdText.Text, out int t); _config.MatchThreshold = t > 0 ? t : 60;
        int.TryParse(SettleText.Text, out int sm); _config.SettleMaxMs = sm > 0 ? sm : 9000;
        int.TryParse(RefThresholdText.Text, out int rt); _config.ReferenceThreshold = rt > 0 ? rt : 45;
        int.TryParse(RefreshCapText.Text, out int rc); _config.RefreshCap = rc > 0 ? rc : 0;
        _config.MinRarity = MinRarityCombo.SelectedItem?.ToString() ?? _config.MinRarity;
        _config.MaxRarity = MaxRarityCombo.SelectedItem?.ToString() ?? _config.MaxRarity;
        _config.Strategy = StrategyCombo.SelectedIndex >= 0 ? StrategyCombo.SelectedIndex : 0;
        _config.CaptureMethod = CaptureMethodCombo.SelectedIndex >= 0 ? CaptureMethodCombo.SelectedIndex : 0;
        _config.DetectMode = DetectModeCombo.SelectedIndex >= 0 ? DetectModeCombo.SelectedIndex : 0;
        _config.GroupRivalry = GroupRivalryCheck.IsChecked == true;
        _config.DismissWithEsc = DismissEscCheck.IsChecked == true;
        _config.CardCount = CardCountCombo.SelectedIndex >= 0 ? CardCountCombo.SelectedIndex + 1 : 1;
        _config.CardLayout = ComboContent(CardLayoutCombo, "2 x 2 grid");
        InputSim.UseLegacyMouseEvent = LegacyMouseEventCheck.IsChecked == true;
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

    /// <summary>
    /// Saves the current game window capture with markers on every configured point/region,
    /// so the exact click targets can be verified.
    /// </summary>
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
            Marker(_config.RerollX, _config.RerollY, Color.Cyan, "REROLL");
            Marker(_config.RefreshX, _config.RefreshY, Color.Yellow, "REFRESH");
            Marker(_config.OddsBtnX, _config.OddsBtnY, Color.Orange, "ODDSBTN");
            Marker(_config.OddsCloseX, _config.OddsCloseY, Color.OrangeRed, "ODDSCLOSE");
            Marker(_config.DismissX, _config.DismissY, Color.Magenta, "DISMISS");
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
    private enum PickMode { None, Roll, CardRegion, RaritySample, Dismiss, Reroll, Refresh, OddsBtn, OddsRegion, Blocked, OddsClose, OddsTeamTab }

    private void PickRoll_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.Roll; PickStatusText.Text = "Pick mode: Roll Button - click on the roll button."; }
    private void PickCardRegion_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.CardRegion; _dragging = false; PickStatusText.Text = "Pick mode: Card Region - drag a rectangle over the card."; }
    private void PickRaritySample_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.RaritySample; _dragging = false; PickStatusText.Text = "Pick mode: Rarity Sample - drag a small box on the rarity colour area."; }
    private void PickDismiss_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.Dismiss; PickStatusText.Text = "Pick mode: Dismiss - click a point."; }
    private void ClearDismiss_Click(object s, RoutedEventArgs e) { _config.DismissX = -1; _config.DismissY = -1; DismissPtText.Text = ""; }
    private void PickReroll_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.Reroll; PickStatusText.Text = "Pick mode: Reroll Button - click."; }
    private void PickRefresh_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.Refresh; PickStatusText.Text = "Pick mode: Refresh Button - click."; }
    private void PickOddsBtn_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.OddsBtn; PickStatusText.Text = "Pick mode: Odds Button - click."; }
    private void PickOddsRegion_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.OddsRegion; _dragging = false; PickStatusText.Text = "Pick mode: Odds Region - drag."; }
    private void PickOddsClose_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.OddsClose; PickStatusText.Text = "Pick mode: Odds Close - click the button that shuts the odds panel."; }
    private void PickOddsTeamTab_Click(object s, RoutedEventArgs e) { _pickMode = PickMode.OddsTeamTab; PickStatusText.Text = "Pick mode: THIS TEAM tab - click the refresh-odds tab inside the odds panel."; }
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
            case PickMode.Dismiss: _config.DismissX = x; _config.DismissY = y; DismissPtText.Text = $"{x},{y}"; break;
            case PickMode.Reroll: _config.RerollX = x; _config.RerollY = y; RerollPtText.Text = $"{x},{y}"; break;
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
            case PickMode.RaritySample:
                _config.RaritySampleX = x1 - _config.CardX;
                _config.RaritySampleY = y1 - _config.CardY;
                _config.RaritySampleW = w; _config.RaritySampleH = h;
                RaritySampleText.Text = $"{_config.RaritySampleX},{_config.RaritySampleY} {w}x{h} (rel)";
                PickStatusText.Text = $"Rarity sample set {w}x{h}";
                break;
        }
        _pickMode = PickMode.None;
    }

    // ---------------- Rarities ----------------
    private void SampleColor_Click(object sender, RoutedEventArgs e)
    {
        if (RarityList.SelectedItem is not RarityColor rc) { MessageBox.Show("Select a tier in the list first."); return; }
        if (_previewBmp == null) { MessageBox.Show("Capture preview first."); return; }
        var fs = ColorMatch.SampleFrame(_previewBmp, new Rectangle(_config.CardX, _config.CardY, _config.CardW, _config.CardH));
        if (!fs.Valid) { MessageBox.Show("Set the card region first."); return; }
        rc.R = fs.Peak.R; rc.G = fs.Peak.G; rc.B = fs.Peak.B;
        var body = ColorMatch.DominantColor(_previewBmp, new Rectangle(_config.CardX, _config.CardY, _config.CardW, _config.CardH));
        rc.BodyR = body.R; rc.BodyG = body.G; rc.BodyB = body.B; rc.HasBody = true;
        RarityList.ItemsSource = null; RarityList.ItemsSource = _config.Rarities;
        AppendLog($"{rc.Name} frame = #{rc.R:X2}{rc.G:X2}{rc.B:X2} body = #{body.R:X2}{body.G:X2}{body.B:X2} (from preview)");
    }

    private async void AssignCurrentCard_Click(object sender, RoutedEventArgs e)
    {
        if (RarityList.SelectedItem is not RarityColor rc) { MessageBox.Show("Select the tier the card on screen is."); return; }
        var w = SelectedWindow();
        if (w == null) { MessageBox.Show("Select window."); return; }
        if (_config.CardW <= 0) { MessageBox.Show("Set the card region first."); return; }
        var bmp = WindowCapture.Capture(w.Hwnd, CaptureMethodCombo.SelectedIndex);
        if (bmp == null) { MessageBox.Show("Capture failed."); return; }
        var card = WindowCapture.Crop(bmp, new Rectangle(_config.CardX, _config.CardY, _config.CardW, _config.CardH));
        bmp.Dispose();
        var fs = ColorMatch.SampleFrame(card, new Rectangle(0, 0, card.Width, card.Height));
        if (!fs.Valid) { MessageBox.Show("Could not sample the card frame."); return; }
        rc.R = fs.Peak.R; rc.G = fs.Peak.G; rc.B = fs.Peak.B;
        var body = ColorMatch.DominantColor(card, new Rectangle(0, 0, card.Width, card.Height));
        if (rc.RequireRedYellow)
        {
            // rivalry cards: frame = red, body = gold/yellow
            var redC = ColorMatch.DominantInHueBand(card, new Rectangle(0, 0, card.Width, card.Height), 340, 20, 0.35, 0.18);
            var yellowC = ColorMatch.DominantInHueBand(card, new Rectangle(0, 0, card.Width, card.Height), 35, 68, 0.35, 0.30);
            if (redC != Color.Black) { rc.R = redC.R; rc.G = redC.G; rc.B = redC.B; }
            if (yellowC != Color.Black) { body = yellowC; }
        }
        rc.BodyR = body.R; rc.BodyG = body.G; rc.BodyB = body.B; rc.HasBody = true;
        int sparkles = SparkleDetector.CountSparkles(card);
        rc.Sparkles = sparkles > 0; rc.SparklesKnown = true;
        card.Dispose();
        RarityList.ItemsSource = null; RarityList.ItemsSource = _config.Rarities;
        AppendLog($"{rc.Name} frame = #{rc.R:X2}{rc.G:X2}{rc.B:X2} body = #{body.R:X2}{body.G:X2}{body.B:X2} sparkles={sparkles} (hue dispersion {fs.Dispersion:F2}, sat {fs.Saturation:F2})");
    }

    private async void AssignAltColor_Click(object sender, RoutedEventArgs e)
    {
        if (RarityList.SelectedItem is not RarityColor rc) { MessageBox.Show("Select a tier in the list first."); return; }
        var w = SelectedWindow();
        if (w == null) { MessageBox.Show("Select window."); return; }
        if (_config.CardW <= 0) { MessageBox.Show("Set the card region first."); return; }
        var bmp = WindowCapture.Capture(w.Hwnd, CaptureMethodCombo.SelectedIndex);
        if (bmp == null) { MessageBox.Show("Capture failed."); return; }
        using var card = WindowCapture.Crop(bmp, new Rectangle(_config.CardX, _config.CardY, _config.CardW, _config.CardH));
        bmp.Dispose();
        var fs = ColorMatch.SampleFrame(card, new Rectangle(0, 0, card.Width, card.Height));
        if (!fs.Valid) { MessageBox.Show("Could not sample the card frame."); return; }
        rc.AltR = fs.Peak.R; rc.AltG = fs.Peak.G; rc.AltB = fs.Peak.B; rc.HasAlt = true;
        RarityList.ItemsSource = null; RarityList.ItemsSource = _config.Rarities;
        AppendLog($"{rc.Name} alt colour = #{rc.AltR:X2}{rc.AltG:X2}{rc.AltB:X2}");
    }

    private void ResetRarities_Click(object sender, RoutedEventArgs e)
    {
        _config.Rarities = RarityCatalog.Defaults();
        RarityList.ItemsSource = null; RarityList.ItemsSource = _config.Rarities;
        RefreshRarityCombos();
        AppendLog("Rarity colours reset to defaults.");
    }

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
    private List<TierProfile> _profiles = new();

    private void LoadReferences()
    {
        _profiles = ReferenceLibrary.Load(ReferenceLibrary.DefaultFolder, _config.Rarities.Select(r => r.Name));

        // the reference art is ground truth: use its frame colour for the tier
        foreach (var p in _profiles)
        {
            var tier = _config.Rarities.FirstOrDefault(r =>
                string.Equals(r.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (tier == null) continue;
            if (p.Peak.R + p.Peak.G + p.Peak.B > 0)
            {
                tier.R = p.Peak.R; tier.G = p.Peak.G; tier.B = p.Peak.B;
                tier.HasBody = false;      // the guessed body colour is less reliable than the art
            }
        }

        string msg = _profiles.Count > 0
            ? $"{_profiles.Count} reference(s): {string.Join(", ", _profiles.Select(p => p.Name))}"
            : $"none found in {ReferenceLibrary.DefaultFolder} - add PNGs named after the tiers";
        if (ReferenceText != null) ReferenceText.Text = msg;
        AppendLog("Reference images: " + msg);
    }

    private void ReloadReferences_Click(object sender, RoutedEventArgs e) => LoadReferences();

    private void OpenReferenceFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ReferenceLibrary.DefaultFolder);
            System.Diagnostics.Process.Start("explorer.exe", ReferenceLibrary.DefaultFolder);
            AppendLog("Opened: " + ReferenceLibrary.DefaultFolder);
        }
        catch (Exception ex) { AppendLog("Could not open folder: " + ex.Message); }
    }

    /// <summary>
    /// Learns each tier's colour from the game's own odds panel: every rarity name is
    /// drawn in that rarity's colour, so the text colour IS the tier colour.
    /// </summary>
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

    private async void LearnFromOdds_Click(object sender, RoutedEventArgs e)
    {
        var w = SelectedWindow();
        if (w == null) { MessageBox.Show("Select window."); return; }
        AppendLog("Learning tier colours from the odds panel...");
        int n = await LearnTiersFromOddsAsync(w.Hwnd, CancellationToken.None);
        AppendLog(n > 0 ? $"Learned {n} tier colour(s)." : "Nothing learned - is the odds panel visible?");
        MessageBox.Show(n > 0
            ? $"Learned {n} tier colour(s) from the odds panel."
            : "Could not read the odds panel. Make sure the odds panel opens on screen.");
    }

    private async Task<(string? tier, string detail)> DetectTierAsync(Bitmap card, int detectMode)
    {
        if (card.Width <= 0 || card.Height <= 0) return (null, "no card region");

        string detail = "";
        string? tier = null;

        if (detectMode != 2)
        {
            string text = await OcrService.RecognizeAsync(card);
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            detail = "OCR[" + (text.Length > 90 ? text.Substring(0, 90) : text) + "]";
            tier = RarityCatalog.MatchText(text, _config.Rarities);
            if (tier != null) return (tier, detail);
        }

        // reference image matching (uses the actual card artwork you supplied)
        if (_profiles.Count > 0)
        {
            var cardProfile = TierProfile.FromWindow(card, new Rectangle(0, 0, card.Width, card.Height));
            var ranked = _profiles.Select(p => (p, d: cardProfile.DistanceTo(p))).OrderBy(x => x.d).ToList();
            var best = ranked[0];
            double second = ranked.Count > 1 ? ranked[1].d : double.MaxValue;
            detail += $" REF[{best.p.Name} d={best.d:F0} next={second:F0}]";
            if (best.d <= _config.ReferenceThreshold && (second == double.MaxValue || second - best.d > 4))
                return (best.p.Name, detail);
            detail += " (ref match too weak)";
        }

        if (detectMode != 1)
        {
            var fs = ColorMatch.SampleFrame(card, new Rectangle(0, 0, card.Width, card.Height));
            if (fs.Valid)
            {
                detail += $" COL[avg #{fs.Average.R:X2}{fs.Average.G:X2}{fs.Average.B:X2} peak #{fs.Peak.R:X2}{fs.Peak.G:X2}{fs.Peak.B:X2} disp {fs.Dispersion:F2} sat {fs.Saturation:F2}]";
                // rainbow frames (prismatic) first
                foreach (var r in _config.Rarities.Where(x => x.MultiHue))
                {
                    if (fs.Dispersion >= 0.30 && fs.Saturation >= 0.18)
                        return (r.Name, detail);
                }
                RarityColor? best = null; double bestD = double.MaxValue;
                var cardRectFull = new Rectangle(0, 0, card.Width, card.Height);
                double redFrac = ColorMatch.HueBandFraction(card, cardRectFull, 340, 20, 0.35, 0.18);
                double yellowFrac = ColorMatch.HueBandFraction(card, cardRectFull, 35, 68, 0.35, 0.30);
                detail += $" red={redFrac:P1} yellow={yellowFrac:P1}";

                var frameDist = new Dictionary<RarityColor, double>();
                var bodyDist = new Dictionary<RarityColor, double>();
                var bodyColor = ColorMatch.DominantColor(card, cardRectFull);
                foreach (var r in _config.Rarities.Where(x => !x.MultiHue))
                {
                    double fd = Math.Min(ColorMatch.Distance(fs.Average, r.ToColor()),
                                         ColorMatch.Distance(fs.Peak, r.ToColor()));
                    if (r.HasAlt)
                        fd = Math.Min(fd, Math.Min(ColorMatch.Distance(fs.Average, Color.FromArgb(r.AltR, r.AltG, r.AltB)),
                                                   ColorMatch.Distance(fs.Peak, Color.FromArgb(r.AltR, r.AltG, r.AltB))));
                    frameDist[r] = fd;
                    bodyDist[r] = r.HasBody
                        ? ColorMatch.Distance(bodyColor, Color.FromArgb(r.BodyR, r.BodyG, r.BodyB))
                        : double.MaxValue;
                    if (fd < bestD) { bestD = fd; best = r; }
                }

                double thr = _config.MatchThreshold;
                // a tier qualifies if EITHER its frame colour or its body colour is a close match
                bool Qualifies(RarityColor r)
                {
                    if (frameDist.TryGetValue(r, out double fd) && fd <= thr) return true;
                    if (bodyDist.TryGetValue(r, out double bd) && bd <= thr) return true;
                    return false;
                }
                var candidates = _config.Rarities.Where(Qualifies).ToList();

                if (candidates.Count == 0)
                {
                    // nothing close enough - report the closest tier for diagnostics but claim nothing
                    var nearest = _config.Rarities.Where(x => !x.MultiHue)
                        .Select(x => (x, d: frameDist.TryGetValue(x, out var d2) ? d2 : 9999))
                        .OrderBy(t => t.d).FirstOrDefault();
                    detail += $" (no tier within threshold; nearest {(nearest.x == null ? "?" : nearest.x.Name)} d={(nearest.x == null ? -1 : nearest.d):F0})";
                    return (null, detail);
                }

                if (candidates.Count > 0)
                {
                    // tiers that need BOTH red and yellow on the card
                    var needBoth = candidates.Where(r => r.RequireRedYellow).ToList();
                    if (needBoth.Count > 0)
                    {
                        var ok = needBoth.Where(r => redFrac >= 0.015 && yellowFrac >= 0.015).ToList();
                        if (ok.Count > 0) candidates = ok;
                        else if (needBoth.Count == candidates.Count)
                        {
                            // every candidate is a rivalry tier and the card has no red+yellow,
                            // so it is definitely not one of them
                            detail += " (rivalry filter: no red+yellow on the card)";
                            return (null, detail);
                        }
                        else candidates = candidates.Where(r => !r.RequireRedYellow).ToList();
                    }

                    int sparkles = SparkleDetector.CountSparkles(card);
                    detail += $" sparkles={sparkles}";
                    if (sparkles > 0)
                    {
                        var withSparkle = candidates.Where(r => r.SparklesKnown && r.Sparkles).ToList();
                        if (withSparkle.Count > 0) candidates = withSparkle;
                    }
                    else
                    {
                        var withoutSparkle = candidates.Where(r => r.SparklesKnown && !r.Sparkles).ToList();
                        if (withoutSparkle.Count > 0) candidates = withoutSparkle;
                    }

                    if (candidates.Count == 0) { detail += " (no candidates left)"; return (null, detail); }

                    double Score(RarityColor r) =>
                        r.HasBody ? (frameDist[r] + bodyDist[r]) / 2.0 : frameDist[r];
                    var winner = candidates.OrderBy(Score).First();

                    // confidence gate: never claim a card unless the match is convincing.
                    // With a body reference BOTH signals must be close; without one the
                    // frame colour must be a tight match and clearly better than the runner-up.
                    double frameD = frameDist[winner];
                    bool confident;
                    double bodyD = -1;
                    if (winner.HasBody)
                    {
                        bodyD = bodyDist[winner];
                        double combined = (frameD + bodyD) / 2.0;
                        confident = combined <= thr && frameD <= thr * 1.8 && bodyD <= thr * 1.8;
                    }
                    else
                    {
                        confident = frameD <= thr * 0.8;
                    }

                    // the winner must also be clearly better than the next best tier
                    var ordered = candidates.OrderBy(Score).ToList();
                    double margin = ordered.Count > 1 ? Score(ordered[1]) - Score(ordered[0]) : 999;
                    if (margin < 6) confident = false;

                    detail += $" {winner.Name} frame d={frameD:F0} body d={(winner.HasBody ? bodyD : -1):F0} margin={margin:F0} conf={(confident ? "yes" : "LOW")}";
                    if (!confident) return (null, detail);
                    return (winner.Name, detail);
                }
                detail += $" (nearest {best?.Name} d={bestD:F0} > threshold {thr})";
            }
        }
        return (null, detail);
    }

    private async void TestDetection_Click(object sender, RoutedEventArgs e)
    {
        var w = SelectedWindow();
        if (w == null) { MessageBox.Show("Select window."); return; }
        if (_config.CardW <= 0) { MessageBox.Show("Set card region first."); return; }
        var bmp = WindowCapture.Capture(w.Hwnd, CaptureMethodCombo.SelectedIndex);
        if (bmp == null) { AppendLog("Capture failed."); return; }
        int mode = DetectModeCombo.SelectedIndex;
        var cardRect = new Rectangle(_config.CardX, _config.CardY, _config.CardW, _config.CardH);
        using var region = WindowCapture.Crop(bmp, cardRect);
        bmp.Dispose();
        var slices = SliceRegions(new Rectangle(0, 0, region.Width, region.Height), _config.CardCount, _config.CardLayout);
        for (int i = 0; i < slices.Count; i++)
        {
            using var card = WindowCapture.Crop(region, slices[i]);
            var (tier, detail) = await DetectTierAsync(card, mode);
            bool strip = DiagonalStripDetector.HasDiagonalStrip(card, out double stripScore);
            AppendLog($"Detection test card {i + 1}/{slices.Count} -> {tier ?? "(unknown)"}  {detail}  blockedRibbon={strip} (score {stripScore:F2})");
            StatusText.Text = tier ?? "(unknown)";
        }
    }

    // ---------------- Run loop ----------------
    private uint _lastRegionHash;
    private uint _lastWinHash;
    private int _refreshesWithoutHit = 0;

    /// <summary>Focus the game window first, then click, and log exactly what was sent.</summary>
    /// <summary>
    /// Click a point in the game window.
    /// method 0 = SendInput (real cursor + injected press)
    /// method 1 = PostMessage (window messages)
    /// method 2 = SendInput with the move inside the same injected batch
    /// </summary>
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

    /// <summary>The manually picked ODDS button position.</summary>
    private (int x, int y) ResolveOddsButton(IntPtr hwnd)
    {
        if (_config.OddsBtnX >= 0) return (_config.OddsBtnX, _config.OddsBtnY);
        return (0, 0);
    }

    /// <summary>The manually picked ROLL / REFRESH button positions.</summary>
    private (int x, int y) ResolveButton(IntPtr hwnd, bool refresh)
    {
        if (refresh && _config.RefreshX != 0) return (_config.RefreshX, _config.RefreshY);
        if (!refresh && _config.RollX != 0) return (_config.RollX, _config.RollY);
        if (!refresh && _config.RerollX != 0) return (_config.RerollX, _config.RerollY);
        if (refresh && _config.RerollX != 0) return (_config.RerollX, _config.RerollY);
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
            else if (_config.RerollX != 0) { bx = _config.RerollX; by = _config.RerollY; }
        }
        ClickAt(hwnd, bx + _rollOffsetX, by + _rollOffsetY, _clickMethod);
        AppendLog(useRefresh ? "Refreshed (kept team/year)." : "Rerolled (full).");
    }

    /// <summary>
    /// Reads the odds panel: opens it, switches to the THIS TEAM tab (which lists the
    /// refresh odds for the current team) and OCRs the rarity names on that side.
    /// Fully automatic - no region picking needed.
    /// </summary>
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
        int scrollX = panel.X + (int)(panel.Width * 0.78);
        int scrollY = panel.Y + (int)(panel.Height * 0.55);
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

    /// <summary>
    /// Detects the "TEAM COMPLETE" screen (big white heading at the top of the screen).
    /// </summary>
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

    /// <summary>
    /// Reads the "Rerolls Available" and "Refreshes Available" counters from the left
    /// panel (one capture, one OCR pass).
    /// </summary>
    private async Task<(int? rerolls, int? refreshes)> ReadCountersAsync(IntPtr hwnd)
    {
        int? rerolls = null, refreshes = null;
        try
        {
            if (_config.RollY == 0) return (null, null);
            var bmp = WindowCapture.Capture(hwnd, _config.CaptureMethod);
            if (bmp == null) return (null, null);

            int rx = _config.RollX - 170;
            int ry = _config.RollY - 105;
            int rw = 340;
            int rh = Math.Max(90, (_config.RefreshY - _config.RollY) + 150);
            rx = Math.Max(0, Math.Min(rx, bmp.Width - 10));
            ry = Math.Max(0, Math.Min(ry, bmp.Height - 10));
            rw = Math.Min(rw, bmp.Width - rx);
            rh = Math.Min(rh, bmp.Height - ry);
            if (rw < 60 || rh < 30) { bmp.Dispose(); return (null, null); }

            using var strip = WindowCapture.Crop(bmp, new Rectangle(rx, ry, rw, rh));
            bmp.Dispose();

            var lines = await OddsReader.ReadRegionAsync(strip, 0, strip.Width, t => new List<string>());
            foreach (var l in lines)
            {
                string t = l.Text.ToLowerInvariant();
                if (t.Contains("reroll") && TryGetNumber(l.Text, out int rv)) rerolls = rv;
                else if (t.Contains("refresh") && TryGetNumber(l.Text, out int rf)) refreshes = rf;
            }
        }
        catch { }
        return (rerolls, refreshes);
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
        if (names.Count == 0)
            AppendLog("      (nothing could be read from the refresh column - see odds_panel.png)");
        else if (avail.Count == 0)
            AppendLog("      (this team cannot produce a card in the target range - rolling a new team is the only option)");

        // close the odds panel (it is a modal that blocks the roll button)
        await CloseOddsPanelAsync(hwnd, token, new Rectangle(), 0, null);
        return avail.Count > 0;
    }

    /// <summary>
    /// Closes the odds modal using the manually picked close point.
    /// (The odds panel is a modal - while it is open the roll button cannot be clicked.)
    /// </summary>
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

    private async Task<(List<(Rectangle slice, string? tier, bool ribbon, string detail, double score)> found, uint hash, uint winHash)>
        DetectAllAsync(IntPtr hwnd, CancellationToken token)
    {
        var empty = new List<(Rectangle, string?, bool, string, double)>();
        var bmp = WindowCapture.Capture(hwnd, _config.CaptureMethod);
        if (bmp == null) { AppendLog("Capture failed."); return (empty, 0, 0); }

        var cardRect = new Rectangle(_config.CardX, _config.CardY, _config.CardW, _config.CardH);
        using var region = WindowCapture.Crop(bmp, cardRect);

        uint hash = HashBitmap(region);
        // left panel = roll buttons + counters + card area (excludes the animated roster)
        uint winHash = HashBitmap(CropSafe(bmp, new Rectangle(0, 0, Math.Min(700, bmp.Width), bmp.Height)));
        bmp.Dispose();

        var found = new List<(Rectangle slice, string? tier, bool ribbon, string detail, double score)>();

        if (_profiles.Count > 0)
        {
            // search the region for cards using the reference art - this finds them even
            // when the picked region is not perfectly aligned with the cards
            var hits = CardFinder.Find(region, _profiles, _config.CardCount, _config.CardLayout,
                                       _config.ReferenceThreshold, _config.CardCount);
            if (hits.Count == 0)
            {
                AppendLog("No card found in the region (reference matching).");
                SaveDebug(region, "cards_last.png");
                found.Add((new Rectangle(0, 0, region.Width, region.Height), null, false, "REF: nothing matched", 999));
            }
            foreach (var hit in hits)
            {
                using var card = WindowCapture.Crop(region, hit.Rect);
                bool ribbon = DiagonalStripDetector.HasDiagonalStrip(card, out _);
                bool shiny = hit.Rainbow && !hit.Tier.Equals("Prismatic", StringComparison.OrdinalIgnoreCase);
                string label = shiny ? $"SHINY {hit.Tier}" : hit.Tier;
                found.Add((hit.Rect, hit.Tier, ribbon, $"REF d={hit.Distance:F0} margin={hit.Margin:F0}{(shiny ? " SHINY" : "")}", hit.Distance));
            }
            return (found, hash, winHash);
        }

        var slices = SliceRegions(new Rectangle(0, 0, region.Width, region.Height),
                                  _config.CardCount, _config.CardLayout);

        for (int i = 0; i < slices.Count; i++)
        {
            using var card = WindowCapture.Crop(region, slices[i]);
            var (tier, detail) = await DetectTierAsync(card, _config.DetectMode);
            bool ribbon = DiagonalStripDetector.HasDiagonalStrip(card, out _);
            found.Add((slices[i], tier, ribbon, detail, 0));
        }
        return (found, hash, winHash);
    }

    /// <summary>
    /// Waits until the game stops changing (animation finished) instead of a fixed delay.
    /// Uses a change *ratio* so a continuously shimmering card still counts as settled.
    /// </summary>
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
        int.TryParse(ThresholdText.Text, out int t); _config.MatchThreshold = t > 0 ? t : 60;
        int.TryParse(SettleText.Text, out int sm); _config.SettleMaxMs = sm > 0 ? sm : 9000;
        int.TryParse(RefThresholdText.Text, out int rt); _config.ReferenceThreshold = rt > 0 ? rt : 45;
        int.TryParse(RefreshCapText.Text, out int rc); _config.RefreshCap = rc > 0 ? rc : 0;
        _config.MinRarity = MinRarityCombo.SelectedItem?.ToString() ?? _config.MinRarity;
        _config.MaxRarity = MaxRarityCombo.SelectedItem?.ToString() ?? _config.MaxRarity;
        _config.Strategy = StrategyCombo.SelectedIndex >= 0 ? StrategyCombo.SelectedIndex : 0;
        _config.CaptureMethod = CaptureMethodCombo.SelectedIndex >= 0 ? CaptureMethodCombo.SelectedIndex : 0;
        _config.DetectMode = DetectModeCombo.SelectedIndex >= 0 ? DetectModeCombo.SelectedIndex : 0;
        _config.GroupRivalry = GroupRivalryCheck.IsChecked == true;
        _config.DismissWithEsc = DismissEscCheck.IsChecked == true;
        _config.CardCount = CardCountCombo.SelectedIndex >= 0 ? CardCountCombo.SelectedIndex + 1 : 1;
        _config.CardLayout = ComboContent(CardLayoutCombo, "2 x 2 grid");

        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        AppendLog("Started.");
        LoadReferences();          // pick up any images added since launch
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
                if (teamOdds.HasValue)
                {
                    // same team as last cycle - the odds cannot have changed
                    useRefresh = teamOdds.Value;
                }
                else
                {
                    useRefresh = await DecideRollTypeAsync(w.Hwnd, token, tierMin, tierMax);
                    teamOdds = useRefresh;
                }

                // refresh cap: if we keep refreshing the same team without getting an
                // eligible card, eventually roll a new team instead
                int cap = _config.RefreshCap;
                if (cap > 0 && useRefresh)
                {
                    _refreshesWithoutHit++;
                    if (_refreshesWithoutHit >= cap)
                    {
                        AppendLog($"Refresh cap reached ({_refreshesWithoutHit}/{cap}) with no eligible card - rerolling for a new team.");
                        useRefresh = false;
                    }
                }
                if (!useRefresh)
                {
                    _refreshesWithoutHit = 0;   // new team -> counter restarts
                    teamOdds = null;             // new team -> re-read the odds next cycle
                }

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

                // keep only cards inside the target range, prefer the HIGHEST rarity
                var targets = found
                    .Where(f => f.tier != null)
                    .Select(f => (f, grp: RarityCatalog.GroupIndex(_config.Rarities, f.tier, _config.GroupRivalry)))
                    .Where(x => x.grp >= tierMin && x.grp <= tierMax)
                    .OrderByDescending(x => x.grp)
                    .ToList();

                var bestTarget = targets.Count > 0 ? targets[0].f
                                : default((Rectangle slice, string? tier, bool ribbon, string detail, double score));

                if (targets.Count > 0)
                {
                    // if the highest tier is blocked (already on team / position full) try the next best
                    if (bestTarget.ribbon)
                    {
                        AppendLog($"Blocked (position full / already on team) on {bestTarget.tier} - skipping.");
                        var alt = targets.Select(t => t.f).FirstOrDefault(f => !f.ribbon);
                        bestTarget = alt.tier != null ? alt : default;
                    }

                    if (bestTarget.tier != null)
                    {
                        // a weak reference match is not worth claiming - it could be the
                        // wrong card entirely
                        if (_profiles.Count > 0 && bestTarget.score > _config.ClaimMaxDistance)
                        {
                            AppendLog($"Match too weak (d={bestTarget.score:F0} > {_config.ClaimMaxDistance}) - not claiming.");
                            await DismissAsync(w.Hwnd, token);
                            await Task.Delay(200, token);
                            continue;
                        }

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
                            await DismissAsync(w.Hwnd, token);
                        }
                        else
                        {
                            int cx = _config.CardX + bestTarget.slice.X + bestTarget.slice.Width / 2;
                            int cy = _config.CardY + bestTarget.slice.Y + bestTarget.slice.Height / 2;
                            ClickAt(w.Hwnd, cx, cy);
                            AppendLog($"TARGET FOUND: {bestTarget.tier} - clicked card {found.IndexOf(bestTarget) + 1}.");
                            _refreshesWithoutHit = 0;   // got what we came for
                            await Task.Delay(700, token);
                            await WaitForStableAsync(w.Hwnd, token);
                        }
                    }
                    else
                    {
                        await DismissAsync(w.Hwnd, token);
                    }
                }
                else
                {
                    // nothing eligible - the next roll button press dismisses the card
                    await DismissAsync(w.Hwnd, token);
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
