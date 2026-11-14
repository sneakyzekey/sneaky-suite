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

// SneakyGexProfile -- per-strike GEX histogram in its own panel, with CALLS / PUTS / NET totals,
// the MAGNET strike (largest |net GEX|) and the BARY (GEX-weighted centre of mass).
// Data: GexProfile_<NQ|ES|YM>.csv written by the updated fetch_gex_nq.py (same folder as GexLevels_*.csv).
// Display only -- it does not compute GEX itself, and it cannot place orders.
namespace NinjaTrader.NinjaScript.Indicators
{
    public enum GexProfileScope { Near, All }

    public class SneakyGexProfile : Indicator
    {
        private class PRow { public double Strike, Call, Put, Net; }

        private readonly object sync = new object();
        private List<PRow> rowsAll = new List<PRow>(), rowsNear = new List<PRow>();
        private DateTime stamp = DateTime.MinValue, lastCheck = DateTime.MinValue;
        private string info = "profile: not loaded";

        // shared mode + clickable chips
        private static readonly string[] ModeNames = { "SCALPER", "INTRADAY", "SWING" };
        private int modeIdx = 0; private DateTime modeRead = DateTime.MinValue;
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
        private float[] modeRect = new float[4];
        private string echo = ""; private int echoDir; private DateTime echoRead = DateTime.MinValue;
        private void ReadEcho()
        {
            try
            {
                if ((DateTime.Now - echoRead).TotalSeconds < 2) return; echoRead = DateTime.Now;
                string r = Instrument.MasterInstrument.Name.ToUpperInvariant();
                string tk = (r == "ES" || r == "MES") ? "ES" : ((r == "YM" || r == "MYM") ? "YM" : "NQ");
                string f = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "RadarStatus_" + tk + ".txt");
                if (!File.Exists(f)) { echo = ""; return; }
                string[] a = File.ReadAllText(f).Split('|');
                DateTime ts = DateTime.Parse(a[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
                if (a.Length > 12 && int.Parse(a[3]) > 0 && (DateTime.UtcNow - ts) < TimeSpan.FromMinutes(3)) { echo = a[12]; echoDir = int.Parse(a[4]); } else echo = "";
            }
            catch { echo = ""; }
        }
        private double axTop = 1, axBot = -1;
        public override void OnCalculateMinMax()
        {
            if (axTop > axBot) { MinValue = axBot; MaxValue = axTop; }
        }
        private System.Windows.FrameworkElement clickCtl;
        private double DevScale()
        {
            try { var src = System.Windows.PresentationSource.FromVisual(clickCtl); if (src != null && src.CompositionTarget != null) return src.CompositionTarget.TransformToDevice.M11; } catch { }
            return 1.0;
        }
        private void AttachClick() { try { if (ChartControl == null || clickCtl != null) return; clickCtl = ChartControl; clickCtl.PreviewMouseLeftButtonDown += ClickDown; } catch { } }
        private void DetachClick() { try { if (clickCtl == null) return; clickCtl.PreviewMouseLeftButtonDown -= ClickDown; clickCtl = null; } catch { } }
        private void ClickDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (clickCtl == null || modeRect[2] <= 0) return;
                double sc = DevScale(); System.Windows.Point p = e.GetPosition(clickCtl);
                double mx = p.X * sc, my = p.Y * sc;
                if (mx >= modeRect[0] && mx <= modeRect[0] + modeRect[2] && my >= modeRect[1] && my <= modeRect[1] + modeRect[3])
                {
                    CycleMode(); e.Handled = true;
                    clickCtl.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { try { if (clickCtl != null) clickCtl.InvalidateVisual(); } catch { } }));
                }
            }
            catch { }
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Sneaky GEX Profile";
                Description = "Per-strike GEX histogram with calls/puts/net totals, magnet and barycenter, read from GexProfile_<ticker>.csv.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = false;
                IsAutoScale = true;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;
                Scope = GexProfileScope.Near;
                RangePct = 3.0;
                ProfilePath = "";
                ReloadSec = 30;
                ShowBary = true; ClipOutliers = true; ShowCurve = true; FollowMode = true; IvPct = 18;
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Magnet");
            }
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
        private string ResolvePath()
        {
            if (!string.IsNullOrEmpty(ProfilePath)) return ProfilePath;
            string root = Instrument.MasterInstrument.Name.ToUpperInvariant();
            string tk = (root == "ES" || root == "MES") ? "ES" : ((root == "YM" || root == "MYM") ? "YM" : "NQ");
            return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexProfile_" + tk + ".csv");
        }

        private void ReloadIfNeeded()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastCheck).TotalSeconds < ReloadSec && rowsAll.Count > 0) return;
            lastCheck = now;
            try
            {
                string p = ResolvePath();
                if (!File.Exists(p)) { lock (sync) info = "profile file not found - run the updated fetch_gex_nq.py"; return; }
                DateTime st = File.GetLastWriteTimeUtc(p);
                if (st == stamp && rowsAll.Count > 0) return;
                stamp = st;
                string[] lines;
                using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs))
                    lines = sr.ReadToEnd().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                List<PRow> all = new List<PRow>(), near = new List<PRow>();
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("Group,", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] a = line.Split(',');
                    if (a.Length < 5) continue;
                    double k, c, pu, n;
                    if (!double.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out k)) continue;
                    if (!double.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c)) continue;
                    if (!double.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out pu)) continue;
                    if (!double.TryParse(a[4], NumberStyles.Float, CultureInfo.InvariantCulture, out n)) continue;
                    PRow r = new PRow(); r.Strike = k; r.Call = c; r.Put = pu; r.Net = n;
                    if (a[0].Trim().Equals("Near", StringComparison.OrdinalIgnoreCase)) near.Add(r); else all.Add(r);
                }
                lock (sync)
                {
                    rowsAll = all; rowsNear = near;
                    info = "profile: " + all.Count + " strikes (" + st.ToLocalTime().ToString("MM-dd HH:mm") + ")";
                }
            }
            catch (Exception ex) { lock (sync) info = "profile read error: " + ex.Message; }
        }

        protected override void OnBarUpdate()
        {
            ReloadIfNeeded();
        }

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

        private const int CBg = 0x0A0F1A, CCyan = 0x00B8FF, CRed = 0xFF3355, CGold = 0xFFC400, CGrey = 0x8A93A6, CWhite = 0xF2F5FA, CGreen = 0x00E676, CPurple = 0xC77DFF, CBlue = 0x1E88E5;

        private static string Big(double v)
        {
            double a = Math.Abs(v);
            string s = a >= 1e9 ? (a / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + "B"
                     : a >= 1e6 ? (a / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M"
                     : a >= 1e3 ? (a / 1e3).ToString("0", CultureInfo.InvariantCulture) + "K"
                     : a.ToString("0", CultureInfo.InvariantCulture);
            return (v >= 0 ? "+" : "-") + s;
        }

        private float Chip(string s, float x, float y, float px, int rgb, bool bold)
        {
            float w = s.Length * px * 0.62f + 16f;
            RFrame(x, y, w, px + 10f, 5f, rgb, 0.9f, 1.2f);
            Txt(s, x + 8f, y + 4f, w, px, rgb, bold);
            return x + w + 8f;
        }

        // ------------------------------------------------------------------ render
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null || ChartPanel == null || Bars == null || Bars.Count == 0) return;
            base.OnRender(chartControl, chartScale);
            ReadMode(); ReadEcho();
            bool scopeNear = FollowMode ? modeIdx != 2 : Scope == GexProfileScope.Near;
            double rangeE = FollowMode ? (modeIdx == 0 ? 1.5 : (modeIdx == 1 ? 3.0 : 6.0)) : RangePct;
            float L = ChartPanel.X, T = ChartPanel.Y, W = ChartPanel.W, H = ChartPanel.H, R = L + W, B = T + H;
            double spot = Bars.GetClose(Bars.Count - 1);

            lock (sync)
            {
                Fill(L, T, W, H, CBg, 0.92f);
                List<PRow> rows = scopeNear && rowsNear.Count > 0 ? rowsNear : rowsAll;
                string scopeTxt = (scopeNear && rowsNear.Count > 0) ? "NEAR EXP (<=7d)" : "ALL EXPIRATIONS";
                if (rows.Count == 0)
                {
                    Txt(info, L + 12f, T + 12f, W - 24f, 11f, CGrey, false);
                    return;
                }

                // totals over the whole scope
                double calls = 0, puts = 0;
                foreach (PRow r in rows) { calls += r.Call; puts += r.Put; }
                double net = calls + puts;

                // visible window
                double lo = spot * (1.0 - rangeE / 100.0), hi = spot * (1.0 + rangeE / 100.0);
                List<PRow> vis = new List<PRow>();
                foreach (PRow r in rows) if (r.Strike >= lo && r.Strike <= hi) vis.Add(r);
                vis.Sort((a, b) => a.Strike.CompareTo(b.Strike));

                // magnet = strike with the largest |net| (whole scope); bary = |net|-weighted centre of mass (visible)
                double magK = double.NaN, magV = 0;
                foreach (PRow r in rows) if (Math.Abs(r.Net) > Math.Abs(magV)) { magV = r.Net; magK = r.Strike; }
                double sw = 0, sk = 0;
                foreach (PRow r in vis) { sw += Math.Abs(r.Net); sk += r.Strike * Math.Abs(r.Net); }
                double bary = sw > 0 ? sk / sw : double.NaN;

                // bucket strikes (~60 bars across the window) so the bars are readable
                {
                    double rawStep = double.MaxValue;
                    for (int i = 1; i < vis.Count; i++) { double d = vis[i].Strike - vis[i - 1].Strike; if (d > 1e-9 && d < rawStep) rawStep = d; }
                    double[] nice = { 1, 2, 5, 10, 25, 50, 100, 250 };
                    double bkw = nice[nice.Length - 1];
                    foreach (double nv in nice) if (nv >= (hi - lo) / 60.0) { bkw = nv; break; }
                    if (rawStep < double.MaxValue && bkw > rawStep * 1.01)
                    {
                        SortedDictionary<long, PRow> bm = new SortedDictionary<long, PRow>();
                        foreach (PRow r in vis)
                        {
                            long key = (long)Math.Floor(r.Strike / bkw); PRow b;
                            if (!bm.TryGetValue(key, out b)) { b = new PRow(); b.Strike = key * bkw + bkw / 2.0; bm[key] = b; }
                            b.Call += r.Call; b.Put += r.Put; b.Net += r.Net;
                        }
                        vis = new List<PRow>(bm.Values);
                    }
                }

                // header chips
                float hx = L + 8f, hy = T + 20f;
                {
                    string mt = ModeNames[modeIdx] + " (GEX) \u25BE";
                    modeRect[0] = hx; modeRect[1] = hy; modeRect[2] = mt.Length * 10f * 0.62f + 16f; modeRect[3] = 20f;
                    hx = Chip(mt, hx, hy, 10f, CGold, true);
                }
                hx = Chip(scopeTxt, hx, hy, 10f, CCyan, true);
                hx = Chip(net >= 0 ? "+GAMMA (stabilising)" : "-GAMMA (amplifying)", hx, hy, 10f, net >= 0 ? CGreen : CRed, true);
                Txt("CALLS " + Big(calls), hx, hy + 4f, 150f, 10f, CCyan, true); hx += 108f;
                Txt("PUTS " + Big(puts), hx, hy + 4f, 150f, 10f, CRed, true); hx += 108f;
                Txt("NET " + Big(net), hx, hy + 4f, 150f, 10f, net >= 0 ? CCyan : CRed, true); hx += 108f;
                if (!double.IsNaN(magK)) { Txt("MAGNET " + magK.ToString("0.##", CultureInfo.InvariantCulture) + " (" + Big(magV) + ")", hx, hy + 4f, 220f, 10f, CGold, true); hx += 220f; }
                if (ShowBary && !double.IsNaN(bary))
                {
                    // stretch = distance price<->barycenter as % of half the one-day expected move (IV property)
                    double sigD = spot * (IvPct / 100.0) / Math.Sqrt(252.0);
                    double stretch = sigD > 0 ? 100.0 * Math.Abs(spot - bary) / (0.5 * sigD) : 0;
                    string stT = stretch < 35 ? "AT BARY" : (stretch < 75 ? "STRETCHED" : "MAX STRETCH");
                    int stC = stretch < 35 ? CPurple : (stretch < 75 ? CGold : CRed);
                    Txt("BARY " + bary.ToString("0.0", CultureInfo.InvariantCulture) + " [" + Math.Min(999, stretch).ToString("0", CultureInfo.InvariantCulture) + "% " + stT + "]  (" + ((bary - spot) >= 0 ? "+" : "") + (bary - spot).ToString("0.0", CultureInfo.InvariantCulture) + " pts vs price)", hx, hy + 4f, 420f, 10f, stC, true);
                }

                // second chip row: call/put share and session theta
                {
                    float h2 = T + 42f, hx2 = L + 8f;
                    double tot = calls + Math.Abs(puts);
                    double bullP = tot > 0 ? 100.0 * calls / tot : 50.0;
                    hx2 = Chip("BULL " + bullP.ToString("0", CultureInfo.InvariantCulture) + "% vs BEAR " + (100.0 - bullP).ToString("0", CultureInfo.InvariantCulture) + "% (" + (bullP >= 55 ? "Bulls" : (bullP <= 45 ? "Bears" : "Balanced")) + ")", hx2, h2, 10f, bullP >= 55 ? CGreen : (bullP <= 45 ? CRed : CGold), true);
                    double remFrac = 1.0; bool liveS = false; string phase = "";
                    try
                    {
                        DateTime et = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
                        double mins = (et.Hour * 60 + et.Minute) - (9 * 60 + 30);
                        phase = mins < 120 ? "Morning" : (mins < 240 ? "Midday" : (mins < 330 ? "Afternoon" : "Power Hour"));
                        liveS = et.DayOfWeek != DayOfWeek.Saturday && et.DayOfWeek != DayOfWeek.Sunday && mins >= 0 && mins < 390;
                        remFrac = Math.Max(0.0, Math.Min(1.0, 1.0 - mins / 390.0));
                    }
                    catch { }
                    double thetaP = 100.0 * (1.0 - Math.Sqrt(remFrac));
                    // charm multiplier (my rule): how much faster delta decays than at the open = 1 / sqrt(remaining session fraction), floor 10%
                    if (liveS) hx2 = Chip("THETA " + thetaP.ToString("0", CultureInfo.InvariantCulture) + "% | CHARM " + (1.0 / Math.Sqrt(Math.Max(0.1, remFrac))).ToString("0.0", CultureInfo.InvariantCulture) + "x (" + phase + ")", hx2, h2, 10f, CGold, true);
                    else hx2 = Chip("THETA / CHARM n/a (outside 09:30-16:00 ET)", hx2, h2, 10f, CGrey, false);
                    if (echo.Length > 0) Chip(echo, hx2, h2, 10f, echoDir > 0 ? CGreen : CRed, true);
                }

                if (vis.Count == 0)
                {
                    Txt("no strikes within +/-" + rangeE.ToString("0.#", CultureInfo.InvariantCulture) + "% of price", L + 12f, T + 58f, W - 24f, 11f, CGrey, false);
                    return;
                }

                float x0 = L + 34f, x1 = R - 34f, y0 = T + 90f, y1 = B - 22f;
                float midY = (y0 + y1) / 2f, half = (y1 - y0) / 2f - 4f;
                double maxAbs = 1;
                if (ClipOutliers)
                {
                    // scale to the 90th percentile so one huge strike cannot flatten every other bar
                    List<double> av = new List<double>();
                    foreach (PRow r in vis) av.Add(Math.Abs(r.Net));
                    av.Sort();
                    maxAbs = Math.Max(1, av[Math.Min(av.Count - 1, (int)(av.Count * 0.9))] * 1.25);
                }
                else foreach (PRow r in vis) maxAbs = Math.Max(maxAbs, Math.Abs(r.Net));
                Func<double, float> xOf = k => x0 + (float)((k - lo) / (hi - lo)) * (x1 - x0);

                double step = double.MaxValue;
                for (int i = 1; i < vis.Count; i++) { double d = vis[i].Strike - vis[i - 1].Strike; if (d > 0 && d < step) step = d; }
                if (step == double.MaxValue) step = (hi - lo) / 20.0;
                float bw = Math.Max(2f, (float)(step / (hi - lo)) * (x1 - x0) * 0.72f);

                {
                    // make NinjaTrader's own right-hand axis read in real net-GEX units ($K/$M/$B): zero at midY, +/-maxAbs at +/-half px
                    double unit = maxAbs >= 5e8 ? 1e9 : (maxAbs >= 5e5 ? 1e6 : 1e3);
                    double nt = maxAbs * (midY - T) / half / unit, nb = -maxAbs * (B - midY) / half / unit;
                    if (Math.Abs(nt - axTop) > Math.Abs(nt) * 0.005 + 1e-9 || Math.Abs(nb - axBot) > Math.Abs(nb) * 0.005 + 1e-9)
                    {
                        axTop = nt; axBot = nb;
                        try { ForceRefresh(); } catch { }
                    }
                    Txt("axis: net GEX, $" + (unit == 1e9 ? "B" : (unit == 1e6 ? "M" : "K")), R - 170f, T + 66f, 165f, 8.5f, CGrey, false);
                }
                Line(x0, midY, x1, midY, CGrey, 0.5f, 1f, false);
                foreach (PRow r in vis)
                {
                    float bx = xOf(r.Strike) - bw / 2f;
                    double rel = Math.Abs(r.Net) / maxAbs;
                    bool clipped = rel > 1.0;
                    float bh = (float)Math.Min(1.0, rel) * half;
                    bool pos = r.Net >= 0;
                    bool nearPx = Math.Abs(r.Strike - spot) <= step * 2.5;
                    int col = nearPx ? (pos ? CCyan : 0xFF4D6A) : (pos ? CBlue : CRed);
                    float al = nearPx ? 1f : 0.45f;
                    if (pos) Fill(bx, midY - bh, bw, bh, col, al); else Fill(bx, midY, bw, bh, col, al);
                    if (clipped)
                    {
                        float ty = pos ? midY - bh : midY + bh;
                        Fill(bx - 1f, ty - (pos ? 2f : 0f), bw + 2f, 2f, CWhite, 1f);
                        Txt(Big(r.Net), bx - 14f, pos ? ty - 12f : ty + 2f, 70f, 8.5f, CWhite, true);
                    }
                }
                if (ShowCurve && vis.Count >= 3)
                {
                    // smoothed net-gamma curve through the bars (weights 1-2-3-2-1)
                    int rad = 8;
                    float pxPrev = 0, pyPrev = 0; bool have = false;
                    for (int i = 0; i < vis.Count; i++)
                    {
                        double a = 0, ws = 0;
                        for (int j = -rad; j <= rad; j++)
                        {
                            int q = i + j; if (q < 0 || q >= vis.Count) continue;
                            double wq = Math.Exp(-(j * j) / 18.0); a += vis[q].Net * wq; ws += wq;
                        }
                        double v = ws > 0 ? a / ws : 0;
                        float cx = xOf(vis[i].Strike), cy = midY - (float)Math.Max(-1.0, Math.Min(1.0, v / maxAbs)) * half;
                        if (have) Line(pxPrev, pyPrev, cx, cy, CGold, 0.9f, 1.4f, true);
                        pxPrev = cx; pyPrev = cy; have = true;
                    }
                }

                {
                    PRow neg = null; foreach (PRow q in vis) if (q.Net < 0 && (neg == null || q.Net < neg.Net)) neg = q;
                    if (neg != null && Math.Abs(neg.Net) / maxAbs <= 1.0)
                        Txt(Big(neg.Net), xOf(neg.Strike) - 14f, midY + (float)(Math.Abs(neg.Net) / maxAbs) * half + 2f, 80f, 8.5f, CRed, true);
                }
                // strike labels
                float lastLx = -1000f;
                foreach (PRow r in vis)
                {
                    float lx = xOf(r.Strike);
                    if (lx - lastLx < 62f) continue;
                    lastLx = lx;
                    Txt(r.Strike.ToString("0", CultureInfo.InvariantCulture), lx - 16f, y1 + 4f, 60f, 9f, CGrey, false);
                }

                // price, magnet, bary markers
                if (spot >= lo && spot <= hi)
                {
                    float px = xOf(spot);
                    Line(px, y0 - 6f, px, y1, CWhite, 0.9f, 1.2f, true);
                    PRow nr = null; foreach (PRow q in vis) if (nr == null || Math.Abs(q.Strike - spot) < Math.Abs(nr.Strike - spot)) nr = q;
                    Txt("PRICE " + spot.ToString("0.00", CultureInfo.InvariantCulture) + (nr != null ? "  " + Big(nr.Net) : ""), px + 4f, y0 - 8f, 200f, 9.5f, CWhite, true);
                }
                if (!double.IsNaN(magK) && magK >= lo && magK <= hi)
                {
                    float mx = xOf(magK);
                    Line(mx, y0, mx, y1, CGold, 0.9f, 1.6f, false);
                    Txt("MAGNET", mx + 4f, y1 - 14f, 90f, 9f, CGold, true);
                }
                if (ShowBary && !double.IsNaN(bary) && bary >= lo && bary <= hi)
                {
                    float bx2 = xOf(bary);
                    Line(bx2, y0, bx2, y1, CPurple, 0.8f, 1.2f, true);
                    Txt("BARY", bx2 + 4f, y0 + 2f, 60f, 9f, CPurple, true);
                }
                if (!double.IsNaN(magK) && magK >= lo && magK <= hi)
                {
                    float cx = xOf(magK); string t = magK.ToString("0", CultureInfo.InvariantCulture);
                    Fill(cx - 24f, y1 + 2f, 48f, 15f, CBg, 1f); RFrame(cx - 24f, y1 + 2f, 48f, 15f, 4f, CGold, 1f, 1.2f); Txt(t, cx - 16f, y1 + 4f, 60f, 9f, CGold, true);
                }
                if (spot >= lo && spot <= hi)
                {
                    float cx = xOf(spot); string t = spot.ToString("0", CultureInfo.InvariantCulture);
                    Fill(cx - 24f, y1 + 18f, 48f, 15f, CBg, 1f); RFrame(cx - 24f, y1 + 18f, 48f, 15f, 4f, CWhite, 1f, 1.2f); Txt(t, cx - 16f, y1 + 20f, 60f, 9f, CWhite, true);
                }
                Txt(info, L + 8f, B - 14f, W - 16f, 8f, CGrey, false);
            }
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Scope", Description = "Near = expirations within 7 days (scalping view). All = every listed expiration.", GroupName = "Profile", Order = 0)]
        public GexProfileScope Scope { get; set; }

        [NinjaScriptProperty]
        [Range(0.2, 15)]
        [Display(Name = "Range around price (%)", GroupName = "Profile", Order = 1)]
        public double RangePct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show barycenter", GroupName = "Profile", Order = 2)]
        public bool ShowBary { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Clip outlier bars", Description = "Scale bars to the 90th percentile; oversized bars are cut and labelled with their value.", GroupName = "Profile", Order = 3)]
        public bool ClipOutliers { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show gamma curve", GroupName = "Profile", Order = 4)]
        public bool ShowCurve { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Follow global mode", Description = "On: the SCALPER / INTRADAY / SWING chip sets scope and range. Off: use the Scope and Range properties.", GroupName = "Profile", Order = 5)]
        public bool FollowMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "IV for stretch rating (%)", GroupName = "Profile", Order = 6)]
        public double IvPct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Profile CSV (blank = auto)", Description = "Blank picks GexProfile_<NQ|ES|YM>.csv in your NinjaTrader user folder by chart instrument.", GroupName = "Data", Order = 0)]
        public string ProfilePath { get; set; }

        [NinjaScriptProperty]
        [Range(5, 600)]
        [Display(Name = "Reload check (sec)", GroupName = "Data", Order = 1)]
        public int ReloadSec { get; set; }
        #endregion
    }
}
