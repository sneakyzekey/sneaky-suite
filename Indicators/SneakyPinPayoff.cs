#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using D2D = SharpDX.Direct2D1;
using DW = SharpDX.DirectWrite;
#endregion

// SneakyPinPayoff -- pin-target panel: a long-butterfly payoff diagram centred on the GEX pin/magnet strike,
// a model "value now" curve that shows theta decay, risk/reward numbers and a time-of-day (Eastern) timeline.
//
// IMPORTANT -- this is an illustration, not a quote and not advice:
//   * The butterfly debit is an ASSUMPTION (Debit % of wing width). Set it to what you are actually quoted.
//   * The "value now" curve is a simple normal-distribution model of the fly's value (IV and time left are inputs),
//     not a live option price.
//   * It places no orders.
namespace NinjaTrader.NinjaScript.Indicators
{
    public enum PinSource { Auto, ProfileMagnet, CsvPinStrike, Manual }
    public enum WingPreset { Min15, Std40, Wide70, Max95 }

    public class SneakyPinPayoff : Indicator
    {
        private readonly object sync = new object();
        private double profMagnet = double.NaN, csvPin = double.NaN, profStep = 0;
        private DateTime profStamp = DateTime.MinValue, lvlStamp = DateTime.MinValue, lastCheck = DateTime.MinValue;
        private string info = "";

        // clickable chips: wing preset (confidence coverage of the expected move) and structure toggle
        private static readonly string[] ModeNames = { "SCALPER", "INTRADAY", "SWING" };
        private int modeIdx = 0; private DateTime modeRead = DateTime.MinValue;
        private bool autoPreset = true; private int wingSteps = 0;
        private DateTime modeStamp = DateTime.MinValue;
        private void ReadMode()
        {
            try
            {
                
                string f = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexMode.txt");
                if (!File.Exists(f)) return;
                DateTime st = File.GetLastWriteTimeUtc(f); if (st == modeStamp) return; modeStamp = st;
                int i = Array.IndexOf(ModeNames, File.ReadAllText(f).Trim().ToUpperInvariant()); if (i >= 0) modeIdx = i;
            }
            catch { }
        }
        private void CycleMode()
        {
            try { modeIdx = (modeIdx + 1) % 3; File.WriteAllText(Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexMode.txt"), ModeNames[modeIdx]); modeRead = DateTime.Now; }
            catch { }
        }
        private double axTop = 1, axBot = -1;
        public override void OnCalculateMinMax()
        {
            if (axTop > axBot) { MinValue = axBot; MaxValue = axTop; }
        }
        private int rOpen, rDir, rScore; private string rWhy = ""; private double rBias; private DateTime rRead = DateTime.MinValue;
        private void ReadRadarInfo()
        {
            try
            {
                if ((DateTime.Now - rRead).TotalSeconds < 2) return; rRead = DateTime.Now;
                string tk = Tk();
                string f = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "RadarStatus_" + tk + ".txt");
                rOpen = 0; rWhy = "";
                if (File.Exists(f))
                {
                    string[] a = File.ReadAllText(f).Split('|');
                    DateTime ts = DateTime.Parse(a[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
                    if ((DateTime.UtcNow - ts) < TimeSpan.FromMinutes(3)) { rOpen = int.Parse(a[3]); rDir = int.Parse(a[4]); rScore = int.Parse(a[5]); if (a.Length > 12) rWhy = a[12]; }
                }
                string g = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexSync_" + tk + ".txt");
                if (File.Exists(g)) { string[] b = File.ReadAllText(g).Split('|'); if (b.Length > 1) rBias = double.Parse(b[1], CultureInfo.InvariantCulture); }
            }
            catch { rOpen = 0; }
        }
        private int preset = 0;              // 0 MIN 15%, 1 STD 40%, 2 WEIT 70%, 3 MAX 95%
        private int structure = 0;           // 0 PIN (long butterfly), 1 CALL (long call spread), 2 PUT (long put spread)
        private static readonly double[] PresetZ = { 0.19, 0.52, 1.04, 1.96 };   // z for 15/40/70/95% two-sided coverage
        private static readonly string[] PresetName = { "MIN 15%", "STD 40%", "WEIT 70%", "MAX 95%" };
        private static readonly string[] StructName = { "PIN", "CALL", "PUT", "BWB" };
        private class Hit { public float X, Y, W, H; public int Id; }
        private List<Hit> hits = new List<Hit>();
        private readonly object hitSync = new object();
        private System.Windows.FrameworkElement clickCtl;
        private double DevScale()
        {
            try { var src = System.Windows.PresentationSource.FromVisual(clickCtl); if (src != null && src.CompositionTarget != null) return src.CompositionTarget.TransformToDevice.M11; } catch { }
            return 1.0;
        }
        private void AttachClick()
        {
            try { if (ChartControl == null || clickCtl != null) return; clickCtl = ChartControl; clickCtl.PreviewMouseLeftButtonDown += ClickDown; } catch { }
        }
        private void DetachClick()
        {
            try { if (clickCtl == null) return; clickCtl.PreviewMouseLeftButtonDown -= ClickDown; clickCtl = null; } catch { }
        }
        private void ClickDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (clickCtl == null) return;
                double sc = DevScale();
                System.Windows.Point p = e.GetPosition(clickCtl);
                double mx = p.X * sc, my = p.Y * sc;
                lock (hitSync)
                    foreach (Hit h in hits)
                        if (mx >= h.X && mx <= h.X + h.W && my >= h.Y && my <= h.Y + h.H)
                        {
                            if (h.Id < 4) { preset = h.Id; autoPreset = false; wingSteps = 0; }
                            else if (h.Id >= 10 && h.Id <= 13) structure = h.Id - 10;
                            else if (h.Id == 20) { wingSteps--; autoPreset = false; }
                            else if (h.Id == 21) { wingSteps++; autoPreset = false; }
                            else if (h.Id == 30) CycleMode();
                            else if (h.Id == 31) { autoPreset = !autoPreset; wingSteps = 0; }
                            e.Handled = true;
                            clickCtl.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { try { if (clickCtl != null) clickCtl.InvalidateVisual(); } catch { } }));
                            return;
                        }
            }
            catch { }
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Sneaky Pin Payoff";
                Description = "Pin-target long-butterfly payoff, theta model and Eastern-time session timeline from GEX files.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = false;
                IsAutoScale = true;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;
                Source = PinSource.Auto; StartPreset = WingPreset.Min15; DebitVertPct = 45;
                ManualStrike = 0;
                WingPoints = 0;
                DebitPct = 20;
                Contracts = 1;
                IvPct = 18;
                ExpiryHHmm = 1600;
                PowerHourHHmm = 1500;
                ReloadSec = 30;
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Pin");
            }
            else if (State == State.Configure) { preset = Math.Max(0, Math.Min(3, (int)StartPreset)); structure = 0; }
            else if (State == State.Historical)
            {
                if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => AttachClick());
            }
            else if (State == State.Terminated)
            {
                DetachClick();
                lock (sync) DisposeGfx();
            }
        }

        // ------------------------------------------------------------------ data
        private string Tk()
        {
            string root = Instrument.MasterInstrument.Name.ToUpperInvariant();
            return (root == "ES" || root == "MES") ? "ES" : ((root == "YM" || root == "MYM") ? "YM" : "NQ");
        }

        private static string[] ReadLines(string p)
        {
            using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader sr = new StreamReader(fs))
                return sr.ReadToEnd().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private void ReloadIfNeeded()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastCheck).TotalSeconds < ReloadSec) return;
            lastCheck = now;
            string msg = "";
            try
            {
                string pp = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexProfile_" + Tk() + ".csv");
                if (File.Exists(pp))
                {
                    DateTime st = File.GetLastWriteTimeUtc(pp);
                    if (st != profStamp)
                    {
                        profStamp = st;
                        double magK = double.NaN, magV = 0, prev = double.NaN, step = double.MaxValue;
                        foreach (string raw in ReadLines(pp))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("Group,", StringComparison.OrdinalIgnoreCase)) continue;
                            string[] a = line.Split(',');
                            if (a.Length < 5 || !a[0].Trim().Equals("Near", StringComparison.OrdinalIgnoreCase)) continue;
                            double k, n;
                            if (!double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out k)) continue;
                            if (!double.TryParse(a[4], NumberStyles.Float, CultureInfo.InvariantCulture, out n)) continue;
                            if (Math.Abs(n) > Math.Abs(magV)) { magV = n; magK = k; }
                            if (!double.IsNaN(prev) && k - prev > 0 && k - prev < step) step = k - prev;
                            prev = k;
                        }
                        lock (sync) { profMagnet = magK; profStep = step == double.MaxValue ? 0 : step; }
                    }
                }
                else msg = "no GexProfile file (update fetch_gex_nq.py); ";

                string lp = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexLevels_" + Tk() + ".csv");
                if (File.Exists(lp))
                {
                    DateTime st = File.GetLastWriteTimeUtc(lp);
                    if (st != lvlStamp)
                    {
                        lvlStamp = st;
                        double pin = double.NaN;
                        foreach (string raw in ReadLines(lp))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("Price,", StringComparison.OrdinalIgnoreCase)) continue;
                            string[] a = line.Split(',');
                            double px;
                            if (a.Length < 3 || !double.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out px)) continue;
                            if (a[2].Trim().Equals("PinStrike", StringComparison.OrdinalIgnoreCase)) pin = px;
                        }
                        lock (sync) csvPin = pin;
                    }
                }
                else msg += "no GexLevels file; ";
            }
            catch (Exception ex) { msg += "read error " + ex.Message; }
            lock (sync) info = msg;
        }

        protected override void OnBarUpdate() { ReloadIfNeeded(); }

        // ------------------------------------------------------------------ gfx helpers
        private Dictionary<long, D2D.SolidColorBrush> brushCache = new Dictionary<long, D2D.SolidColorBrush>();
        private Dictionary<int, DW.TextFormat> fmtCache = new Dictionary<int, DW.TextFormat>();
        private D2D.StrokeStyle stDash;

        private void DisposeGfx()
        {
            foreach (D2D.SolidColorBrush b in brushCache.Values) { try { b.Dispose(); } catch { } }
            brushCache.Clear();
            foreach (DW.TextFormat f in fmtCache.Values) { try { f.Dispose(); } catch { } }
            fmtCache.Clear();
            try { if (stDash != null) stDash.Dispose(); } catch { }
            stDash = null;
        }
        public override void OnRenderTargetChanged() { lock (sync) DisposeGfx(); }

        private D2D.SolidColorBrush Br(int rgb, float a)
        {
            long key = ((long)rgb << 8) | (long)Math.Max(0, Math.Min(255, (int)(a * 255)));
            D2D.SolidColorBrush b;
            if (!brushCache.TryGetValue(key, out b))
            {
                b = new D2D.SolidColorBrush(RenderTarget, new SharpDX.Color4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a));
                brushCache[key] = b;
            }
            return b;
        }
        private DW.TextFormat Fmt(float px, bool bold)
        {
            int key = ((int)(px * 10)) * 2 + (bold ? 1 : 0);
            DW.TextFormat f;
            if (!fmtCache.TryGetValue(key, out f))
            {
                DW.Factory dw = new DW.Factory();
                f = new DW.TextFormat(dw, "Arial", bold ? DW.FontWeight.Bold : DW.FontWeight.Normal, DW.FontStyle.Normal, px);
                f.WordWrapping = DW.WordWrapping.NoWrap;
                dw.Dispose();
                fmtCache[key] = f;
            }
            return f;
        }
        private void Txt(string s, float x, float y, float w, float px, int rgb, bool bold)
        {
            RenderTarget.DrawText(s, Fmt(px, bold), new SharpDX.RectangleF(x, y, w, px + 6f), Br(rgb, 1f));
        }
        private void Fill(float x, float y, float w, float h, int rgb, float a)
        {
            RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)), Br(rgb, a));
        }
        private void RFrame(float x, float y, float w, float h, float r, int rgb, float a, float t)
        {
            D2D.RoundedRectangle rr = new D2D.RoundedRectangle();
            rr.Rect = new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)); rr.RadiusX = r; rr.RadiusY = r;
            RenderTarget.DrawRoundedRectangle(rr, Br(rgb, a), t);
        }
        private void Line(float x1, float y1, float x2, float y2, int rgb, float a, float w, bool dashed)
        {
            if (dashed)
            {
                if (stDash == null)
                {
                    D2D.StrokeStyleProperties sp = new D2D.StrokeStyleProperties(); sp.DashStyle = D2D.DashStyle.Dash;
                    stDash = new D2D.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, sp);
                }
                RenderTarget.DrawLine(new SharpDX.Vector2(x1, y1), new SharpDX.Vector2(x2, y2), Br(rgb, a), w, stDash);
            }
            else RenderTarget.DrawLine(new SharpDX.Vector2(x1, y1), new SharpDX.Vector2(x2, y2), Br(rgb, a), w);
        }
        private float Chip(string s, float x, float y, float px, int rgb, bool bold)
        {
            float w = s.Length * px * 0.62f + 16f;
            RFrame(x, y, w, px + 10f, 5f, rgb, 0.9f, 1.2f);
            Txt(s, x + 8f, y + 4f, w, px, rgb, bold);
            return x + w + 8f;
        }

        private const int CBg = 0x0A0F1A, CCyan = 0x00B8FF, CRed = 0xFF3355, CGold = 0xFFC400, CGrey = 0x8A93A6, CWhite = 0xF2F5FA, CGreen = 0x00E676, CPurple = 0xC77DFF, CYellow = 0xFFE066;

        // ------------------------------------------------------------------ model
        private static int Struct = 0;   // set per render (single UI thread)
        private static int BwbDir = 1;    // +1 = trend up (wide wing above), -1 = trend down (wide wing below)
        private static double Payoff(double s, double k, double w)
        {
            if (Struct == 1) return Math.Max(0, s - k) - Math.Max(0, s - (k + w));       // long call spread K / K+w
            if (Struct == 2) return Math.Max(0, k - s) - Math.Max(0, (k - w) - s);       // long put spread K / K-w
            if (Struct == 3) { double q = BwbDir >= 0 ? s : 2.0 * k - s; return Math.Max(0, q - (k - w)) - 2.0 * Math.Max(0, q - k) + Math.Max(0, q - (k + 2.0 * w)); }   // broken-wing fly, wide wing on the trend side
            return Math.Max(0, s - (k - w)) - 2.0 * Math.Max(0, s - k) + Math.Max(0, s - (k + w));
        }
        // expected fly payoff if the underlying ends ~ Normal(s0, sd)
        private static double ModelValue(double s0, double sd, double k, double w)
        {
            if (sd <= 1e-9) return Payoff(s0, k, w);
            double sum = 0, wsum = 0;
            for (double z = -4.0; z <= 4.0001; z += 0.25)
            {
                double wt = Math.Exp(-0.5 * z * z);
                sum += wt * Payoff(s0 + z * sd, k, w);
                wsum += wt;
            }
            return sum / wsum;
        }
        private static DateTime EasternNow()
        {
            try { return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Eastern Standard Time"); }
            catch { return DateTime.UtcNow.AddHours(-4); }
        }
        private static double MinOfDay(int hhmm) { return (hhmm / 100) * 60.0 + (hhmm % 100); }

        // ------------------------------------------------------------------ render
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null || ChartPanel == null || Bars == null || Bars.Count == 0) return;
            base.OnRender(chartControl, chartScale);
            float L = ChartPanel.X, T = ChartPanel.Y, W = ChartPanel.W, H = ChartPanel.H, R = L + W, B = T + H;
            double spot = Bars.GetClose(Bars.Count - 1);

            double pm, cp, ps; string inf;
            lock (sync) { pm = profMagnet; cp = csvPin; ps = profStep; inf = info; }

            // pin strike
            double K = double.NaN; string kSrc = "";
            if (Source == PinSource.Manual && ManualStrike > 0) { K = ManualStrike; kSrc = "manual"; }
            else if (Source == PinSource.CsvPinStrike) { K = cp; kSrc = "0DTE pin"; }
            else if (Source == PinSource.ProfileMagnet) { K = pm; kSrc = "GEX magnet"; }
            else
            {
                if (!double.IsNaN(cp)) { K = cp; kSrc = "0DTE pin"; }
                else if (!double.IsNaN(pm)) { K = pm; kSrc = "GEX magnet"; }
            }

            lock (sync)
            {
                Fill(L, T, W, H, CBg, 0.92f);
                if (double.IsNaN(K))
                {
                    Txt("No pin/magnet strike available. " + inf, L + 12f, T + 12f, W - 24f, 11f, CGrey, false);
                    return;
                }

                double pv = Instrument.MasterInstrument.PointValue * Contracts;
                ReadMode(); ReadRadarInfo();
                BwbDir = rOpen > 0 ? (rDir >= 0 ? 1 : -1) : (rBias >= 0 ? 1 : -1);
                if (autoPreset) preset = modeIdx == 0 ? 0 : (modeIdx == 1 ? 1 : 2);
                Struct = structure;
                double stp = ps > 0 ? ps : Math.Max(1.0, Instrument.MasterInstrument.TickSize);
                // session clock first: preset wing = z(coverage) x expected 1-sigma move over the time left (floor: 10% of session)
                DateTime et0 = EasternNow();
                double nm0 = et0.Hour * 60.0 + et0.Minute;
                double sl0 = Math.Max(1, MinOfDay(ExpiryHHmm) - (9 * 60 + 30));
                bool live0 = et0.DayOfWeek != DayOfWeek.Saturday && et0.DayOfWeek != DayOfWeek.Sunday && nm0 >= 9 * 60 + 30 && nm0 < MinOfDay(ExpiryHHmm);
                double rf0 = !live0 ? 1.0 : Math.Max(0.10, Math.Min(1.0, (MinOfDay(ExpiryHHmm) - nm0) / sl0));
                double sigma0 = spot * (IvPct / 100.0) * Math.Sqrt(rf0 / 252.0);
                double wing;
                if (WingPoints > 0) wing = WingPoints;
                else wing = Math.Max(stp, Math.Round(PresetZ[preset] * sigma0 / stp) * stp + wingSteps * stp);
                double debit = wing * ((structure == 0 || structure == 3) ? DebitPct : DebitVertPct) / 100.0;
                double maxP = (wing - debit) * pv, maxL = debit * pv;
                if (structure == 3) maxL = (wing + debit) * pv;     // narrow side loses the debit, wide side loses wing + debit
                double rr = structure == 3 ? (maxL > 0 ? maxP / maxL : 0) : (debit > 0 ? (wing - debit) / debit : 0);

                // session clock (Eastern)
                DateTime et = EasternNow();
                double nowMin = et.Hour * 60.0 + et.Minute + et.Second / 60.0;
                double openMin = 9 * 60 + 30, expMin = MinOfDay(ExpiryHHmm), phMin = MinOfDay(PowerHourHHmm);
                double sessLen = Math.Max(1, expMin - openMin);
                double rem = Math.Max(0, Math.Min(sessLen, expMin - nowMin));
                bool sessionLive = et.DayOfWeek != DayOfWeek.Saturday && et.DayOfWeek != DayOfWeek.Sunday && nowMin >= openMin && nowMin < expMin;
                double remFrac = nowMin < openMin ? 1.0 : rem / sessLen;
                double thetaPct = 100.0 * (1.0 - Math.Sqrt(remFrac));
                bool powerHour = sessionLive && nowMin >= phMin;
                double dist = spot - K;

                // header
                float hx = L + 8f, hy = T + 20f;
                hx = Chip(structure == 0 ? "LONG FLY (model)" : (structure == 1 ? "CALL SPREAD (model)" : (structure == 2 ? "PUT SPREAD (model)" : "BWB TREND " + (BwbDir > 0 ? "\u25B2" : "\u25BC") + " (model)")), hx, hy, 10f, structure == 2 ? CRed : CGreen, true);
                hx = Chip("PIN " + K.ToString("0.##", CultureInfo.InvariantCulture) + "  [" + kSrc + "]", hx, hy, 10f, CGold, true);
                Txt("R:R 1:" + rr.ToString("0.0", CultureInfo.InvariantCulture) + "  |  MAX +$" + maxP.ToString("0", CultureInfo.InvariantCulture) + "  /  RISK -$" + maxL.ToString("0", CultureInfo.InvariantCulture)
                    + "  (wing " + wing.ToString("0.#", CultureInfo.InvariantCulture) + " pts, assumed debit " + debit.ToString("0.#", CultureInfo.InvariantCulture) + ")", hx, hy + 4f, 520f, 10f, CGrey, true);

                // banner
                string banner;
                int bannerC;
                if (powerHour && Math.Abs(dist) <= wing)
                { banner = "POWER-HOUR PINNING (THETA " + thetaPct.ToString("0", CultureInfo.InvariantCulture) + "%): Forced Lock on Magnet " + K.ToString("0.##", CultureInfo.InvariantCulture) + "!  (price " + Math.Abs(dist).ToString("0.#", CultureInfo.InvariantCulture) + " pts " + (dist >= 0 ? "above" : "below") + ")"; bannerC = CPurple; }
                else if (sessionLive)
                { banner = "PIN TARGET " + K.ToString("0.##", CultureInfo.InvariantCulture) + " is " + Math.Abs(dist).ToString("0.#", CultureInfo.InvariantCulture) + " pts " + (dist >= 0 ? "below" : "above") + " price  |  model theta decayed " + thetaPct.ToString("0", CultureInfo.InvariantCulture) + "%"; bannerC = CCyan; }
                else
                { banner = "Outside the " + (ExpiryHHmm / 100).ToString("00") + ":" + (ExpiryHHmm % 100).ToString("00") + " ET 0DTE session - showing expiry payoff only"; bannerC = CGrey; }
                if (rOpen > 0 && rWhy.Length > 0) { banner = rWhy; bannerC = rDir > 0 ? CGreen : CRed; }
                float bw = Math.Min(W - 16f, banner.Length * 6.6f + 24f);
                if (rOpen > 0)
                {
                    string rt = (rScore >= 80 ? "\u2605\u2605\u2605" : (rScore >= 60 ? "\u2605\u2605" : "\u2605")) + " RADAR [" + rScore + "%]";
                    Txt(rt, R - rt.Length * 7f - 16f, T + 22f, 200f, 10.5f, CGold, true);
                }
                RFrame(L + 8f, T + 50f, bw, 22f, 6f, bannerC, 0.9f, 1.3f);
                Txt(banner, L + 18f, T + 54f, bw, 10f, bannerC, true);
                if (powerHour && Math.Abs(dist) <= wing) Chip("POWER-HOUR PIN", L + 8f + bw + 10f, T + 49f, 10f, CPurple, true);

                // clickable preset + structure chips
                {
                    List<Hit> nh = new List<Hit>();
                    float cy = T + 76f, cx = L + 8f;
                    {
                        string mt = ModeNames[modeIdx] + " (GEX) \u25BE"; float mw = mt.Length * 6.4f + 16f;
                        RFrame(cx, cy, mw, 20f, 5f, CGold, 1f, 1.2f); Txt(mt, cx + 8f, cy + 4f, mw, 9.5f, CGold, true);
                        Hit hm = new Hit(); hm.X = cx; hm.Y = cy; hm.W = mw; hm.H = 20f; hm.Id = 30; nh.Add(hm); cx += mw + 6f;
                        float aw = 50f;
                        if (autoPreset) Fill(cx, cy, aw, 20f, CGreen, 0.22f);
                        RFrame(cx, cy, aw, 20f, 5f, autoPreset ? CGreen : CGrey, autoPreset ? 1f : 0.6f, 1.2f); Txt("AUTO", cx + 10f, cy + 4f, aw, 9.5f, autoPreset ? CGreen : CGrey, autoPreset);
                        Hit ha = new Hit(); ha.X = cx; ha.Y = cy; ha.W = aw; ha.H = 20f; ha.Id = 31; nh.Add(ha); cx += aw + 14f;
                    }
                    Txt("WING", cx, cy + 4f, 40f, 9f, CGrey, true); cx += 40f;
                    for (int i = 0; i < 4; i++)
                    {
                        float cw = PresetName[i].Length * 6.4f + 16f;
                        bool on = (WingPoints <= 0) && i == preset && wingSteps == 0;
                        if (on) Fill(cx, cy, cw, 20f, CCyan, 0.22f);
                        RFrame(cx, cy, cw, 20f, 5f, on ? CCyan : CGrey, on ? 1f : 0.6f, 1.2f);
                        Txt(PresetName[i], cx + 8f, cy + 4f, cw, 9.5f, on ? CCyan : CGrey, on);
                        Hit h = new Hit(); h.X = cx; h.Y = cy; h.W = cw; h.H = 20f; h.Id = i; nh.Add(h);
                        cx += cw + 6f;
                    }
                    for (int sgn = 0; sgn < 2; sgn++)
                    {
                        float cw = 24f; string t = sgn == 0 ? "\u2212" : "+";
                        RFrame(cx, cy, cw, 20f, 5f, CGrey, 0.8f, 1.2f); Txt(t, cx + 8f, cy + 3f, cw, 11f, CWhite, true);
                        Hit h = new Hit(); h.X = cx; h.Y = cy; h.W = cw; h.H = 20f; h.Id = 20 + sgn; nh.Add(h); cx += cw + 6f;
                    }
                    cx += 14f;
                    for (int i = 0; i < 4; i++)
                    {
                        string sn = i == 3 ? "BWB " + (BwbDir > 0 ? "\u25B2" : "\u25BC") : StructName[i];
                        float cw = sn.Length * 7f + 18f;
                        bool on = i == structure; int sc = i == 2 ? CRed : (i == 1 ? CGreen : (i == 3 ? CPurple : CGold));
                        if (on) Fill(cx, cy, cw, 20f, sc, 0.22f);
                        RFrame(cx, cy, cw, 20f, 5f, on ? sc : CGrey, on ? 1f : 0.6f, 1.2f);
                        Txt(sn, cx + 9f, cy + 4f, cw, 9.5f, on ? sc : CGrey, on);
                        Hit h = new Hit(); h.X = cx; h.Y = cy; h.W = cw; h.H = 20f; h.Id = 10 + i; nh.Add(h);
                        cx += cw + 6f;
                    }
                    if (WingPoints > 0) Txt("(wing fixed by setting: " + WingPoints.ToString("0.#", CultureInfo.InvariantCulture) + " pts)", cx + 8f, cy + 4f, 260f, 9f, CGrey, false);
                    lock (hitSync) hits = nh;
                }

                // plot area
                float x0 = L + 40f, x1 = R - 70f, y0 = T + 112f, y1 = B - 44f;
                if (y1 - y0 < 40f) return;
                double lo = Math.Min(K - wing * 2.4, spot - wing * 0.3), hi = Math.Max(K + wing * 2.4, spot + wing * 0.3);
                double vTop = Math.Max(maxP, 1) * 1.25, vBot = -Math.Max(maxL, maxP * 0.12) * 1.5;
                Func<double, float> xOf = s => x0 + (float)((s - lo) / (hi - lo)) * (x1 - x0);
                Func<double, float> yOf = v => y1 - (float)((v - vBot) / (vTop - vBot)) * (y1 - y0);
                float zeroY = yOf(0);
                {
                    // NinjaTrader's right-hand axis in real dollars: vTop at y0, vBot at y1
                    double span = vTop - vBot, nt = vTop + (y0 - T) / (y1 - y0) * span, nb = vBot - (B - y1) / (y1 - y0) * span;
                    if (Math.Abs(nt - axTop) > Math.Abs(nt) * 0.005 + 1e-9 || Math.Abs(nb - axBot) > Math.Abs(nb) * 0.005 + 1e-9)
                    { axTop = nt; axBot = nb; try { ForceRefresh(); } catch { } }
                }
                List<double> beList = new List<double>();

                Line(x0, zeroY, x1, zeroY, CGrey, 0.6f, 1f, false);

                // expiry payoff (filled by sign)
                int N = 120;
                float prevX = 0, prevY = 0;
                for (int i = 0; i <= N; i++)
                {
                    double s = lo + (hi - lo) * i / N;
                    double v = (Payoff(s, K, wing) - debit) * pv;
                    float px = xOf(s), py = yOf(v);
                    if (i > 0)
                    {
                        double vPrev = (Payoff(lo + (hi - lo) * (i - 1) / N, K, wing) - debit) * pv;
                        if ((vPrev < 0 && v >= 0) || (vPrev >= 0 && v < 0)) beList.Add(lo + (hi - lo) * (i - 1 + vPrev / (vPrev - v)) / N);
                        Line(prevX, prevY, px, py, CCyan, 1f, 2.2f, false);
                        float top = Math.Min(py, zeroY), hgt = Math.Abs(py - zeroY);
                        Fill(px - (px - prevX), top, Math.Max(1f, px - prevX), hgt, v >= 0 ? CGreen : CRed, 0.18f);
                    }
                    prevX = px; prevY = py;
                }

                // model "value now" (theta) curve
                double sd = spot * (IvPct / 100.0) * Math.Sqrt((remFrac * 1.0 / 252.0));
                prevX = 0; prevY = 0;
                for (int i = 0; i <= N; i++)
                {
                    double s = lo + (hi - lo) * i / N;
                    double v = (ModelValue(s, sd, K, wing) - debit) * pv;
                    float px = xOf(s), py = yOf(v);
                    if (i > 0) Line(prevX, prevY, px, py, CYellow, 0.9f, 1.4f, true);
                    prevX = px; prevY = py;
                }

                // breakeven prices on the payoff line
                foreach (double be in beList)
                {
                    float bx = xOf(be); string bt = be.ToString("0.#", CultureInfo.InvariantCulture);
                    Fill(bx - 3f, zeroY - 3f, 6f, 6f, CGold, 1f);
                    Txt(bt, bx - bt.Length * 3.2f, zeroY + 5f, 80f, 9f, CGold, true);
                }
                // markers
                float kx = xOf(K);
                Line(kx, y0, kx, y1, CGold, 0.9f, 1.4f, false);
                Txt("PIN " + K.ToString("0.##", CultureInfo.InvariantCulture), kx + 4f, y0 - 14f, 120f, 9.5f, CGold, true);
                if (spot >= lo && spot <= hi)
                {
                    float sx = xOf(spot);
                    Line(sx, y0, sx, y1, CWhite, 0.9f, 1.2f, true);
                    Txt(spot.ToString("0.00", CultureInfo.InvariantCulture), sx + 4f, y1 - 14f, 100f, 9.5f, CWhite, true);
                    double vNow = (Payoff(spot, K, wing) - debit) * pv;
                    Txt((vNow >= 0 ? "+$" : "-$") + Math.Abs(vNow).ToString("0", CultureInfo.InvariantCulture) + " if expired here", sx + 4f, y0 + 2f, 170f, 9.5f, vNow >= 0 ? CGreen : CRed, true);
                }
                // current price on the model "value now" curve, with its P&L tag
                if (spot >= lo && spot <= hi)
                {
                    double vm = (ModelValue(spot, sd, K, wing) - debit) * pv;
                    float mx = xOf(spot), my = yOf(vm);
                    RenderTarget.FillEllipse(new D2D.Ellipse(new SharpDX.Vector2(mx, my), 5f, 5f), Br(CBg, 1f));
                    RenderTarget.DrawEllipse(new D2D.Ellipse(new SharpDX.Vector2(mx, my), 5f, 5f), Br(CYellow, 1f), 1.6f);
                    string vt = (vm >= 0 ? "+$" : "-$") + Math.Abs(vm).ToString("0", CultureInfo.InvariantCulture) + " now";
                    float vw = vt.Length * 6.4f + 12f;
                    Fill(mx + 10f, my - 18f, vw, 16f, CBg, 0.95f); RFrame(mx + 10f, my - 18f, vw, 16f, 4f, vm >= 0 ? CGreen : CRed, 1f, 1.2f);
                    Txt(vt, mx + 16f, my - 16f, vw, 9.5f, vm >= 0 ? CGreen : CRed, true);
                }
                // value labels on the right
                {
                    string[] tg = { "+$" + maxP.ToString("0", CultureInfo.InvariantCulture), "$0.00", "-$" + maxL.ToString("0", CultureInfo.InvariantCulture) };
                    float[] tyy = { yOf(maxP), zeroY, yOf(-maxL) }; int[] tc = { CGreen, CGrey, CRed };
                    for (int q = 0; q < 3; q++)
                    {
                        Fill(x1 + 4f, tyy[q] - 9f, 60f, 18f, CBg, 0.95f); RFrame(x1 + 4f, tyy[q] - 9f, 60f, 18f, 4f, tc[q], 1f, 1.2f);
                        Txt(tg[q], x1 + 10f, tyy[q] - 6f, 56f, 10f, tc[q], true);
                    }
                    string pk = K.ToString("0.##", CultureInfo.InvariantCulture) + " \u2605"; float pw = pk.Length * 6.6f + 14f;
                    Fill(kx - pw / 2f, y1 + 2f, pw, 16f, CBg, 1f); RFrame(kx - pw / 2f, y1 + 2f, pw, 16f, 5f, CPurple, 1f, 1.3f); Txt(pk, kx - pw / 2f + 7f, y1 + 4f, pw, 9.5f, CWhite, true);
                }
                Txt("solid = value at expiry   dotted = model value now (IV " + IvPct.ToString("0.#", CultureInfo.InvariantCulture) + "%)", x0, y1 + 4f, 420f, 8.5f, CGrey, false);

                // time-of-day timeline (Eastern)
                float tx0 = L + 40f, tx1 = R - 40f, ty = B - 20f;
                Line(tx0, ty, tx1, ty, CGrey, 0.7f, 1.2f, false);
                Func<double, float> tOf = m => tx0 + (float)((m - openMin) / sessLen) * (tx1 - tx0);
                double[] tm = new double[] { openMin, MinOfDay(1130), MinOfDay(1330), phMin, expMin - 15 };
                string[] tl = new string[] { "09:30 START", "11:30 MIDDAY", "13:30 BREAKOUT", PowerHourHHmm.ToString("0000").Insert(2, ":") + " FINAL STRETCH", ((ExpiryHHmm - 15) / 100).ToString("00") + ":" + ((ExpiryHHmm - 15) % 100).ToString("00") + " EXIT!" };
                for (int i = 0; i < tm.Length; i++)
                {
                    if (tm[i] < openMin || tm[i] > expMin) continue;
                    float mx = tOf(tm[i]);
                    Line(mx, ty - 4f, mx, ty + 4f, CGrey, 0.9f, 1.2f, false);
                    Txt(tl[i], Math.Min(mx - 6f, tx1 - 84f), ty + 5f, 100f, 8.5f, i == 3 ? CPurple : CGrey, i == 3 || i == 4);
                }
                if (nowMin >= openMin && nowMin <= expMin)
                {
                    float nx = tOf(nowMin);
                    Fill(nx - 4f, ty - 4f, 8f, 8f, CGold, 1f);
                }
                if (!string.IsNullOrEmpty(inf)) Txt(inf, L + 8f, B - 12f, W - 16f, 8f, CGrey, false);
            }
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Pin source", Description = "Auto = the 0DTE Pin Strike from GexLevels, else the largest-|GEX| strike from the profile.", GroupName = "Pin", Order = 0)]
        public PinSource Source { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(Name = "Manual strike", Description = "Used when Pin source = Manual.", GroupName = "Pin", Order = 1)]
        public double ManualStrike { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "Wing width (pts, 0 = auto)", Description = "Auto = about 0.25% of price, snapped to your strike spacing.", GroupName = "Butterfly (assumption)", Order = 0)]
        public double WingPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 95)]
        [Display(Name = "Assumed debit (% of wing)", Description = "ASSUMPTION - set to the debit you are actually quoted. Drives max profit, risk and R:R.", GroupName = "Butterfly (assumption)", Order = 1)]
        public double DebitPct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Starting wing preset", Description = "Wing = z x expected move: MIN 15%, STD 40%, WIDE 70%, MAX 95% coverage. Click the chips on the panel to change live.", GroupName = "Butterfly (assumption)", Order = 4)]
        public WingPreset StartPreset { get; set; }

        [NinjaScriptProperty]
        [Range(1, 95)]
        [Display(Name = "Assumed debit, call/put spread (% of wing)", Description = "ASSUMPTION for CALL / PUT structures.", GroupName = "Butterfly (assumption)", Order = 5)]
        public double DebitVertPct { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Contracts", GroupName = "Butterfly (assumption)", Order = 2)]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "IV for model curve (%)", GroupName = "Butterfly (assumption)", Order = 3)]
        public double IvPct { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Expiry time ET (HHmm)", GroupName = "Session (Eastern)", Order = 0)]
        public int ExpiryHHmm { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Power hour starts ET (HHmm)", GroupName = "Session (Eastern)", Order = 1)]
        public int PowerHourHHmm { get; set; }

        [NinjaScriptProperty]
        [Range(5, 600)]
        [Display(Name = "Reload check (sec)", GroupName = "Data", Order = 0)]
        public int ReloadSec { get; set; }
        #endregion
    }
}
