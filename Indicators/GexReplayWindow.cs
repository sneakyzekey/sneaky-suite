// ============================================================================
//  GexReplayWindow.cs -- WPF window for the intraday GEX replay scrubber (v1, 2026-09-26).
//
//  Ported from the reference tool's own OFK_ReplayWindow.cs
//  (github.com/Kza56/OFK_Atas_GEX, OFK_ATAS/OFK_ReplayWindow.cs -- 265 lines, read in full
//  before this port). Trimmed for this project's much simpler single-instrument (NQ-only)
//  scope and CSV-based (not JSON) snapshot format -- no GexLoader/GexSnapshot/MetaSnapshot
//  struct system needed here, since GexLevels_v1_0_0.cs already has its own CSV parser
//  (ParseGexCsv) that LoadReplaySnapshot below reuses directly instead of duplicating it.
//
//  Sibling file to GexLevels_v1_0_0.cs in the same Indicators folder -- NinjaTrader compiles
//  every .cs file in a NinjaScript folder together as one assembly, so this class is visible
//  to GexLevels_v1_0_0.cs (and vice versa) with no extra project wiring, same as the
//  reference tool's own file split.
//
//  Displays a slider to navigate through today's timestamped intraday snapshots (written
//  every 15 min during RTH by the companion fetch_gex_nq_intraday.py -- its own Task
//  Scheduler job, separate from the once-daily fetch_gex_nq.py). On each position change,
//  calls back into GexLevels_v1_0_0.LoadReplaySnapshot via the onSnapshotSelected callback.
//
//  "Resume LIVE" button: exits replay mode via onExitReplay and closes this window.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace NinjaTrader.NinjaScript.Indicators
{
    // ── GexSnapshotUtil -- listing today's intraday snapshot files ────────────────────────
    public static class GexSnapshotUtil
    {
        public readonly struct SnapshotInfo
        {
            public readonly DateTime Timestamp; // local time (HHmm parsed from the filename)
            public readonly string Path;
            public SnapshotInfo(DateTime ts, string path) { Timestamp = ts; Path = path; }
        }

        // Expected pattern, matching fetch_gex_nq_intraday.py's own SNAPSHOT_NAME_FMT:
        //   GexLevels_NQ_snapshot_{YYYYMMDD}_{HHMM}.csv
        // Kept close to the reference tool's own "{SYMBOL}_full_levels_{YYYYMMDD}_{HHMM}.json"
        // naming convention (same trailing date_time shape), just adapted to this project's
        // CSV format and "GexLevels_NQ" file-stem convention (matching the live GexLevels_NQ.csv
        // it's a snapshot of).
        /// <summary>
        /// Lists intraday snapshots for a given local date, sorted by ascending timestamp
        /// (empty list if the directory is missing or nothing matches -- never throws).
        /// </summary>
        public static List<SnapshotInfo> ListSnapshots(string intradayDir, DateTime localDate)
        {
            var result = new List<SnapshotInfo>();
            if (string.IsNullOrEmpty(intradayDir) || !Directory.Exists(intradayDir))
                return result;

            string dateStr = localDate.ToString("yyyyMMdd");
            string pattern = "GexLevels_NQ_snapshot_" + dateStr + "_*.csv";
            try
            {
                foreach (var path in Directory.GetFiles(intradayDir, pattern))
                {
                    string stem = Path.GetFileNameWithoutExtension(path);
                    var parts = stem.Split('_');
                    if (parts.Length < 4)
                        continue;
                    string hhmm = parts[parts.Length - 1];
                    if (hhmm.Length != 4)
                        continue;
                    int hh, mm;
                    if (!int.TryParse(hhmm.Substring(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out hh))
                        continue;
                    if (!int.TryParse(hhmm.Substring(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out mm))
                        continue;
                    if (hh > 23 || mm > 59)
                        continue;
                    var ts = new DateTime(localDate.Year, localDate.Month, localDate.Day, hh, mm, 0, DateTimeKind.Local);
                    result.Add(new SnapshotInfo(ts, path));
                }
            }
            catch (Exception)
            {
                // Missing/inaccessible folder, permission error, etc. -- same "return what we
                // have" contract as the reference tool's own ListSnapshots.
            }
            result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            return result;
        }
    }

    // ── GexReplayWindow ─────────────────────────────────────────────────────────────────
    public class GexReplayWindow : Window
    {
        private readonly List<GexSnapshotUtil.SnapshotInfo> _snapshots;
        private readonly Action<string, DateTime> _onSnapshotSelected;
        private readonly Action _onExitReplay;

        private Slider _slider = null;
        private TextBlock _tsLabel = null;
        private TextBlock _statusLabel = null;
        private Button _btnLive = null;
        private Button _btnPrev = null;
        private Button _btnNext = null;

        public GexReplayWindow(List<GexSnapshotUtil.SnapshotInfo> snapshots,
                                Action<string, DateTime> onSnapshotSelected,
                                Action onExitReplay)
        {
            _snapshots = snapshots ?? new List<GexSnapshotUtil.SnapshotInfo>();
            _onSnapshotSelected = onSnapshotSelected;
            _onExitReplay = onExitReplay;
            BuildUI();
            // Initial selection = last snapshot (most recent) -- matches the reference tool.
            if (_snapshots.Count > 0)
            {
                _slider.Value = _snapshots.Count - 1;
                ApplyCurrentSelection();
            }
        }

        private void BuildUI()
        {
            Title = "GEX Intraday Replay -- NQ -- " + _snapshots.Count + " snapshots";
            Width = 620;
            Height = 210;
            Background = new SolidColorBrush(Color.FromRgb(13, 17, 23));
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 480; MinHeight = 190;
            Topmost = true; // sibling to the main floating panel, which is also Topmost
            ShowInTaskbar = true;
            FontFamily = new FontFamily("Segoe UI");
            SourceInitialized += (s, e) => ApplyDarkTitleBar(this);

            var bgColor = Color.FromRgb(22, 27, 39);
            var bgDark = Color.FromRgb(13, 17, 23);
            var borderColor = Color.FromRgb(33, 41, 61);
            var textColor = Color.FromRgb(201, 209, 217);
            var textDimColor = Color.FromRgb(139, 148, 158);
            var accentPurple = Color.FromRgb(189, 147, 249);
            var accentRed = Color.FromRgb(224, 108, 117);

            var root = new Border { Background = new SolidColorBrush(bgColor) };
            var outer = new StackPanel { Orientation = Orientation.Vertical };

            var hdr = new Border
            {
                Background = new SolidColorBrush(bgDark),
                Padding = new Thickness(12, 8, 12, 8),
                BorderBrush = new SolidColorBrush(borderColor),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            hdr.Child = new TextBlock
            {
                Text = "Intraday replay -- NQ",
                Foreground = new SolidColorBrush(accentPurple),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            };
            outer.Children.Add(hdr);

            var tsSec = new Border { Padding = new Thickness(12, 12, 12, 4) };
            _tsLabel = new TextBlock
            {
                Text = "--",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(textColor),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            tsSec.Child = _tsLabel;
            outer.Children.Add(tsSec);

            var rangeSec = new Border { Padding = new Thickness(12, 0, 12, 6) };
            var rangeGrid = new Grid();
            rangeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rangeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rangeLeft = new TextBlock
            {
                Text = _snapshots.Count > 0 ? _snapshots[0].Timestamp.ToString("HH:mm") : "--",
                Foreground = new SolidColorBrush(textDimColor),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var rangeRight = new TextBlock
            {
                Text = _snapshots.Count > 0 ? _snapshots[_snapshots.Count - 1].Timestamp.ToString("HH:mm") : "--",
                Foreground = new SolidColorBrush(textDimColor),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(rangeLeft, 0);
            Grid.SetColumn(rangeRight, 1);
            rangeGrid.Children.Add(rangeLeft);
            rangeGrid.Children.Add(rangeRight);
            rangeSec.Child = rangeGrid;
            outer.Children.Add(rangeSec);

            var sliderSec = new Border { Padding = new Thickness(12, 0, 12, 8) };
            _slider = new Slider
            {
                Minimum = 0,
                Maximum = Math.Max(0, _snapshots.Count - 1),
                Value = 0,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                SmallChange = 1,
                LargeChange = 5,
                Foreground = new SolidColorBrush(accentPurple),
                Background = new SolidColorBrush(borderColor),
            };
            _slider.ValueChanged += (s, e) => ApplyCurrentSelection();
            _slider.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Left) { _slider.Value = Math.Max(_slider.Minimum, _slider.Value - 1); e.Handled = true; }
                if (e.Key == Key.Right) { _slider.Value = Math.Min(_slider.Maximum, _slider.Value + 1); e.Handled = true; }
            };
            sliderSec.Child = _slider;
            outer.Children.Add(sliderSec);

            var btnSec = new Border
            {
                Padding = new Thickness(12, 6, 12, 8),
                BorderBrush = new SolidColorBrush(borderColor),
                BorderThickness = new Thickness(0, 1, 0, 1)
            };
            var btnGrid = new Grid();
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _btnPrev = MakeButton("<  Prev", Color.FromRgb(40, 50, 70), accentPurple);
            _btnPrev.Click += (s, e) => { if (_slider.Value > _slider.Minimum) _slider.Value--; };
            Grid.SetColumn(_btnPrev, 0);
            btnGrid.Children.Add(_btnPrev);

            _btnLive = MakeButton("Resume LIVE", Color.FromRgb(60, 20, 30), accentRed);
            _btnLive.Click += (s, e) =>
            {
                try { _onExitReplay?.Invoke(); } catch { }
                Close();
            };
            Grid.SetColumn(_btnLive, 2);
            btnGrid.Children.Add(_btnLive);

            _btnNext = MakeButton("Next  >", Color.FromRgb(40, 50, 70), accentPurple);
            _btnNext.Click += (s, e) => { if (_slider.Value < _slider.Maximum) _slider.Value++; };
            Grid.SetColumn(_btnNext, 4);
            btnGrid.Children.Add(_btnNext);

            btnSec.Child = btnGrid;
            outer.Children.Add(btnSec);

            var statusSec = new Border { Background = new SolidColorBrush(bgDark), Padding = new Thickness(12, 5, 12, 6) };
            _statusLabel = new TextBlock
            {
                Text = _snapshots.Count + " snapshots -- arrow keys to navigate, or drag the slider",
                Foreground = new SolidColorBrush(textDimColor),
                FontSize = 10
            };
            statusSec.Child = _statusLabel;
            outer.Children.Add(statusSec);

            root.Child = outer;
            Content = root;
        }

        private Button MakeButton(string text, Color bg, Color fg)
        {
            return new Button
            {
                Content = text,
                Height = 30,
                Background = new SolidColorBrush(bg),
                Foreground = new SolidColorBrush(fg),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                BorderBrush = new SolidColorBrush(fg),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
        }

        private void ApplyCurrentSelection()
        {
            int idx = (int)Math.Round(_slider.Value);
            if (idx < 0 || idx >= _snapshots.Count)
                return;
            var snap = _snapshots[idx];
            _tsLabel.Text = snap.Timestamp.ToString("HH:mm");
            _statusLabel.Text = "snapshot " + (idx + 1) + "/" + _snapshots.Count + "  -  " + Path.GetFileName(snap.Path);
            try { _onSnapshotSelected?.Invoke(snap.Path, snap.Timestamp); } catch { }
        }

        // ── Dark title bar (Windows 10 1809+), same DWM call as the reference tool ─────────
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

        private static void ApplyDarkTitleBar(Window w)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                int useDark = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref useDark, sizeof(int));
            }
            catch { }
        }
    }
}
