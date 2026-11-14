#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SDX = SharpDX;
using D2D = SharpDX.Direct2D1;
using DW  = SharpDX.DirectWrite;
#endregion

// SneakyBuyersSellersProfile
// NinjaTrader 8 port of "Buyers & Sellers Profile + Dynamic S/R" by Zeiierman (TradingView, Pine v6).
// Original work (c) Zeiierman, licensed CC BY-NC-SA 4.0
//   https://creativecommons.org/licenses/by-nc-sa/4.0/
// This port is an adaptation and is therefore distributed under the same license:
// attribution to Zeiierman required, NON-COMMERCIAL use only, share-alike.
//
// What it does
//   1. Rolling volume profile of the last N bars. Each bar's volume is spread across its high-low range
//      and split buy/sell by where the bar closes inside its range (estimate, not true bid/ask).
//   2. Swing pivots (Swing Length bars each side) are snapped toward a nearby high-volume node of the
//      profile as it looked when the pivot was confirmed (no look-ahead).
//   3. Each candidate gets a 0-100 score (profile evidence, absorption, relative pivot volume, wick
//      rejection). Candidates over Min Score become support (pivot low) / resistance (pivot high).
//   4. Nearby same-side levels merge, the weakest are trimmed past Max Levels, and a level is removed
//      when a bar closes beyond it by Break ATR.
//
// Signals (added in this port, not in the original)
//   BUY  : a bar wicks into an active support (low within Touch ATR of it) and closes back above it as a
//          bullish candle. SELL is the mirror at resistance. Only levels scoring >= Signal min score fire,
//          and a level cannot fire again within Signal cooldown bars. Arrows are drawn on the chart and the
//          indicator exposes BuySignal (1 on a signal bar) and SellSignal (-1) series for strategies.
//
// Chart notes
//   * The profile is drawn to the right of the last bar: give the chart some Right Margin.
//   * Width / Offset are in pixels (the Pine version uses bars).

namespace NinjaTrader.NinjaScript.Indicators
{
    public enum SneakyBspLineStyle { Solid, Dotted, Dashed }

    public class SneakyBuyersSellersProfile : Indicator
    {
        // ---------------------------------------------------------------- constants (same as the Pine source)
        private const int    VOL_LEN      = 40;
        private const double PIV_VOL_X    = 2.2;
        private const double ABS_BODY_MAX = 0.40;
        private const double ABS_VOL_X    = 1.5;

        private class Lvl
        {
            public double P, Sc, Raw, Node, Evid, NStr;
            public bool   Supp;
            public int    Born;      // absolute bar index of the pivot bar
            public int    Broke = -1; // bar index where a close broke it (-1 = still active)
            public int    Created;    // bar where the level was stored/updated
            public int    LastSig = -100000;
            public Lvl Clone() { return (Lvl)MemberwiseClone(); }
        }

        private readonly List<Lvl> lvls = new List<Lvl>();
        private readonly List<Lvl> brokenLvls = new List<Lvl>();
        private readonly object    gate = new object();
        private ATR atr14;
        private SMA volAvg;
        private int lastIdx = -1;

        // ---------------------------------------------------------------- render resources
        private D2D.Brush dxSup, dxRes, dxCore, dxRaw, dxBuy, dxSell, dxPoc, dxWhite;
        private D2D.StrokeStyle stDot, stDash;
        private DW.TextFormat tfCenter, tfLeft;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description        = "Buyers/Sellers rolling volume profile with profile-guided dynamic support/resistance (port of Zeiierman).";
                Name               = "SneakyBuyersSellersProfile";
                Calculate          = Calculate.OnBarClose;
                IsOverlay          = true;
                IsAutoScale        = false;
                DisplayInDataBox   = false;
                PaintPriceMarkers  = false;
                DrawOnPricePanel   = true;
                IsSuspendedWhileInactive = true;
                AddPlot(Brushes.Transparent, "BuySignal");
                AddPlot(Brushes.Transparent, "SellSignal");

                Lookback        = 600;
                Rows            = 72;
                ProfileWidthPx  = 150;
                ProfileOffsetPx = 40;
                ShowProfile     = true;
                ShowPoc         = true;
                ShowLevelRows   = true;

                SwingLength     = 5;
                SearchAtr       = 1.20;
                MinNode         = 0.08;
                MinEvidence     = 0.22;
                ProfilePull     = 0.85;
                MinScore        = 30.0;
                MaxLevels       = 20;
                MergeAtr        = 0.16;
                BreakAtr        = 0.10;

                SupportBrush    = Rgb(31, 199, 116);
                ResistanceBrush = Rgb(239, 75, 75);
                CoreBrush       = Rgb(255, 205, 64);
                RawBrush        = Rgb(160, 166, 178);
                BuyBrush        = Rgb(43, 158, 143);
                SellBrush       = Rgb(74, 88, 182);
                PocBrush        = Rgb(255, 145, 32);
                LevelLineStyle  = SneakyBspLineStyle.Solid;
                ShowPivotDots   = true;
                ShowRawPivot    = true;
                ShowDotScore    = true;
                ShowRightScore  = true;
                ShowSignals     = true;
                SignalMinScore  = 50;
                SignalTouchAtr  = 0.10;
                SignalCooldown  = 5;
                SignalAlerts    = true;
                ShowBrokenLevels = true;
                MaxBrokenLevels = 150;
                DebugLog        = false;
            }
            else if (State == State.DataLoaded)
            {
                atr14   = ATR(14);
                volAvg  = SMA(Volume, VOL_LEN);
                lock (gate) { lvls.Clear(); brokenLvls.Clear(); }
                lastIdx = -1;
            }
            else if (State == State.Terminated)
            {
                DisposeBrushes();
                if (stDot != null)    { stDot.Dispose();    stDot = null; }
                if (stDash != null)   { stDash.Dispose();   stDash = null; }
                if (tfCenter != null) { tfCenter.Dispose(); tfCenter = null; }
                if (tfLeft != null)   { tfLeft.Dispose();   tfLeft = null; }
            }
        }

        private static Brush Rgb(byte r, byte g, byte b)
        {
            SolidColorBrush br = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        // ================================================================ ENGINE
        protected override void OnBarUpdate()
        {
            lastIdx = CurrentBar;
            Values[0][0] = 0;
            Values[1][0] = 0;
            if (CurrentBar <= VOL_LEN + SwingLength * 2 + 5)
            {
                if (DebugLog && CurrentBar == VOL_LEN + SwingLength * 2 + 5) Print("BSP warm-up ends at bar " + (CurrentBar + 1) + " (" + Time[0].ToString("MM/dd HH:mm") + "): earlier pivots are skipped");
                return;
            }

            double buyScore = -1, sellScore = -1;
            lock (gate)
            {
                if (IsPivotLow())  EvalPivot(Low[SwingLength],  true);
                if (IsPivotHigh()) EvalPivot(High[SwingLength], false);
                CheckSignals(atr14[0], out buyScore, out sellScore);
                ManageBreaks(atr14[0]);
                TrimLevels();
            }
            if (!ShowSignals) return;
            double off = Math.Max(atr14[0] * 0.30, TickSize * 2);
            if (buyScore >= 0)
            {
                Values[0][0] = 1;
                Draw.ArrowUp(this, "BspBuy" + CurrentBar, false, 0, Low[0] - off, SupportBrush);
                if (SignalAlerts && State == State.Realtime)
                    Alert("BspBuy" + CurrentBar, Priority.High, "BSP BUY: bounce off support (score " + ((int)Math.Round(buyScore)) + ")",
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.Green, Brushes.White);
            }
            if (sellScore >= 0)
            {
                Values[1][0] = -1;
                Draw.ArrowDown(this, "BspSell" + CurrentBar, false, 0, High[0] + off, ResistanceBrush);
                if (SignalAlerts && State == State.Realtime)
                    Alert("BspSell" + CurrentBar, Priority.High, "BSP SELL: rejection at resistance (score " + ((int)Math.Round(sellScore)) + ")",
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.Red, Brushes.White);
            }
        }

        private bool IsPivotHigh()
        {
            double v = High[SwingLength];
            for (int i = 0; i <= SwingLength * 2; i++)
            {
                if (i == SwingLength) continue;
                if (i < SwingLength) { if (High[i] >= v) return false; }   // newer bars strictly lower
                else                 { if (High[i] >  v) return false; }   // older bars not higher
            }
            return true;
        }

        private bool IsPivotLow()
        {
            double v = Low[SwingLength];
            for (int i = 0; i <= SwingLength * 2; i++)
            {
                if (i == SwingLength) continue;
                if (i < SwingLength) { if (Low[i] <= v) return false; }
                else                 { if (Low[i] <  v) return false; }
            }
            return true;
        }

        private static double Clip(double x) { return Math.Max(0.0, Math.Min(1.0, x)); }

        private int RowId(double p, double bot, double step, int rows)
        {
            return Math.Max(0, Math.Min(rows - 1, (int)Math.Floor((p - bot) / step)));
        }

        // Volume profile over absolute bars [oldest .. newest]. Volume of each bar is spread over its range,
        // buy share = close location inside the range.
        private void BuildProfile(int oldest, int newest, int rows, out double bot, out double step, out double[] tot, out double[] buy)
        {
            double lo = Bars.GetLow(newest), hi = Bars.GetHigh(newest);
            for (int i = oldest; i <= newest; i++)
            {
                lo = Math.Min(lo, Bars.GetLow(i));
                hi = Math.Max(hi, Bars.GetHigh(i));
            }
            double span = Math.Max(hi - lo, TickSize * rows);
            bot  = lo - (span - (hi - lo)) * 0.5;
            step = span / rows;
            tot  = new double[rows];
            buy  = new double[rows];

            for (int i = oldest; i <= newest; i++)
            {
                double l = Bars.GetLow(i), h = Bars.GetHigh(i), c = Bars.GetClose(i);
                double r = h - l;
                double v = Math.Max((double)Bars.GetVolume(i), 0.0);
                double bf = r > 0 ? Clip((c - l) / r) : 0.5;
                int r0 = RowId(l, bot, step, rows), r1 = RowId(h, bot, step, rows);
                for (int j = r0; j <= r1; j++)
                {
                    double rl   = bot + j * step;
                    double ov   = r > 0 ? Math.Max(0.0, Math.Min(h, rl + step) - Math.Max(l, rl)) : step;
                    double frac = r > 0 ? ov / r : 1.0;
                    if (frac > 0)
                    {
                        double av = v * frac;
                        tot[j] += av;
                        buy[j] += av * bf;
                    }
                }
            }
        }

        private void ProfileNode(double raw, bool supp, double atrP, double bot, double step, double[] tot, double[] buy,
                                 out double node, out double evid, out double nStr)
        {
            double mx = 1e-7;
            for (int i = 0; i < tot.Length; i++) mx = Math.Max(mx, tot[i]);
            double rad = Math.Max(atrP * SearchAtr, step * 2.0);
            node = raw; double bestE = -1.0; nStr = 0.0;
            int rows = tot.Length;
            for (int r = 0; r < rows; r++)
            {
                double p = bot + (r + 0.5) * step;
                double d = Math.Abs(p - raw);
                if (d > rad) continue;
                double rv   = tot[r];
                double str  = rv / mx;
                double lv   = r > 0 ? tot[r - 1] : rv;
                double rv2  = r < rows - 1 ? tot[r + 1] : rv;
                double nAvg = Math.Max((lv + rv2) * 0.5, mx * 0.01);
                double pk   = Clip((rv / nAvg - 0.85) / 0.65);
                double bSh  = rv > 0 ? Clip(buy[r] / rv) : 0.5;
                double dir  = supp ? bSh : 1.0 - bSh;
                double prox = 1.0 - d / rad;
                double e    = 0.50 * str + 0.20 * pk + 0.20 * dir + 0.10 * prox;
                if (e > bestE) { bestE = e; node = p; nStr = str; }
            }
            evid = Math.Max(bestE, 0.0);
        }

        private void PivotFeatures(bool supp, out double vSc, out double wSc, out double absSc)
        {
            int L = SwingLength;
            double vb = volAvg[L]; if (vb <= 0) vb = Volume[L];
            double vr = vb > 0 ? Volume[L] / vb : 1.0;
            vSc = Clip((vr - 0.8) / (PIV_VOL_X - 0.8));
            double pr   = Math.Max(High[L] - Low[L], TickSize);
            double wick = supp ? Math.Min(Open[L], Close[L]) - Low[L] : High[L] - Math.Max(Open[L], Close[L]);
            wSc = Clip((wick / pr) / 0.50);

            int absRad = Math.Min(3, L);
            absSc = 0.0;
            for (int n = 0; n <= absRad * 2; n++)
            {
                int o = L - absRad + n;
                double cr   = Math.Max(High[o] - Low[o], TickSize);
                double v    = Math.Max(Volume[o], 0.0);
                double bas  = volAvg[o]; if (bas <= 0) bas = v;
                double vRat = bas > 0 ? v / bas : 1.0;
                double bRat = Math.Abs(Close[o] - Open[o]) / cr;
                double small = Clip((ABS_BODY_MAX - bRat) / ABS_BODY_MAX);
                double av    = Clip((vRat - 1.0) / (ABS_VOL_X - 1.0));
                absSc = Math.Max(absSc, Math.Sqrt(small * av));
            }
        }

        private void EvalPivot(double raw, bool supp)
        {
            int L      = SwingLength;
            int avail  = CurrentBar - L + 1;
            int bars   = Math.Max(1, Math.Min(Lookback, avail));
            int newest = CurrentBar - L;
            int oldest = newest - bars + 1;

            double bot, step; double[] tot, buy;
            BuildProfile(oldest, newest, Rows, out bot, out step, out tot, out buy);

            double atrP = Math.Max(atr14[L], TickSize);
            double node, evid, nStr;
            ProfileNode(raw, supp, atrP, bot, step, tot, buy, out node, out evid, out nStr);

            double vSc, wSc, aSc;
            PivotFeatures(supp, out vSc, out wSc, out aSc);

            bool ok = nStr >= MinNode && evid >= MinEvidence;
            double pull = ok ? ProfilePull * Clip(evid / 0.65) : 0.0;
            double p  = raw + (node - raw) * pull;
            double sc = 100.0 * (0.50 * evid + 0.20 * aSc + 0.15 * vSc + 0.15 * wSc);
            bool accepted = ok && sc >= MinScore;
            if (DebugLog)
                Print("BSP " + Time[L].ToString("MM/dd HH:mm") + (supp ? " SUPPORT" : " RESIST ") + " raw=" + raw.ToString("F2")
                    + " node=" + node.ToString("F2") + " nodeStr=" + nStr.ToString("F3") + " evid=" + evid.ToString("F3")
                    + " score=" + sc.ToString("F1") + (accepted ? "  -> STORED" : ok ? "  -> rejected: score<min" : "  -> rejected: node/evidence"));
            if (accepted)
                StoreLevel(p, raw, node, evid, nStr, supp, sc, newest, atrP);
        }

        private void StoreLevel(double p, double raw, double node, double evid, double nStr, bool supp, double sc, int born, double atrP)
        {
            double tol = Math.Max(atrP * MergeAtr, TickSize);
            int id = -1; double best = 1e20;
            for (int i = 0; i < lvls.Count; i++)
            {
                Lvl lv = lvls[i];
                if (lv.Supp != supp) continue;
                double d = Math.Abs(lv.P - p);
                if (d <= tol && d < best) { best = d; id = i; }
            }
            if (id >= 0)
            {
                Lvl lv = lvls[id];
                if (sc > lv.Sc)
                { lv.P = p; lv.Sc = sc; lv.Raw = raw; lv.Node = node; lv.Evid = evid; lv.NStr = nStr; lv.Born = born; lv.Created = CurrentBar; }
            }
            else
                lvls.Add(new Lvl { P = p, Sc = sc, Raw = raw, Node = node, Evid = evid, NStr = nStr, Supp = supp, Born = born, Created = CurrentBar });
        }

        // Bounce signals. Called before ManageBreaks so a bar that closes through a level never signals on it.
        private void CheckSignals(double atr, out double buyScore, out double sellScore)
        {
            buyScore = -1; sellScore = -1;
            if (!ShowSignals) return;
            double tol = Math.Max(atr * SignalTouchAtr, TickSize);
            foreach (Lvl lv in lvls)
            {
                if (lv.Sc < SignalMinScore || CurrentBar <= lv.Created || CurrentBar - lv.LastSig < SignalCooldown) continue;
                if (lv.Supp)
                {
                    if (Low[0] <= lv.P + tol && Close[0] > lv.P && Close[0] > Open[0])
                    { lv.LastSig = CurrentBar; buyScore = Math.Max(buyScore, lv.Sc); }
                }
                else
                {
                    if (High[0] >= lv.P - tol && Close[0] < lv.P && Close[0] < Open[0])
                    { lv.LastSig = CurrentBar; sellScore = Math.Max(sellScore, lv.Sc); }
                }
            }
        }

        private void ManageBreaks(double atr)
        {
            double buf = Math.Max(atr * BreakAtr, 0.0);
            double c = Close[0];
            for (int i = lvls.Count - 1; i >= 0; i--)
            {
                Lvl lv = lvls[i];
                bool broken = lv.Supp ? c < lv.P - buf : c > lv.P + buf;
                if (broken)
                {
                    if (DebugLog) Print("BSP " + Time[0].ToString("MM/dd HH:mm") + (lv.Supp ? " SUPPORT" : " RESIST ") + " " + lv.P.ToString("F2") + " BROKEN (close " + c.ToString("F2") + ")");
                    lv.Broke = CurrentBar;
                    brokenLvls.Add(lv);
                    while (brokenLvls.Count > MaxBrokenLevels) brokenLvls.RemoveAt(0);
                    lvls.RemoveAt(i);
                }
            }
        }

        private void TrimLevels()
        {
            while (lvls.Count > MaxLevels)
            {
                int weak = 0; double sc = lvls[0].Sc;
                for (int i = 1; i < lvls.Count; i++)
                    if (lvls[i].Sc < sc) { sc = lvls[i].Sc; weak = i; }
                lvls.RemoveAt(weak);
            }
        }

        // ================================================================ RENDER
        public override void OnRenderTargetChanged()
        {
            DisposeBrushes();
            if (RenderTarget == null) return;
            dxSup   = SupportBrush.ToDxBrush(RenderTarget);
            dxRes   = ResistanceBrush.ToDxBrush(RenderTarget);
            dxCore  = CoreBrush.ToDxBrush(RenderTarget);
            dxRaw   = RawBrush.ToDxBrush(RenderTarget);
            dxBuy   = BuyBrush.ToDxBrush(RenderTarget);
            dxSell  = SellBrush.ToDxBrush(RenderTarget);
            dxPoc   = PocBrush.ToDxBrush(RenderTarget);
            dxWhite = Brushes.White.ToDxBrush(RenderTarget);
        }

        private static void Kill(ref D2D.Brush b) { if (b != null) { b.Dispose(); b = null; } }

        private void DisposeBrushes()
        {
            Kill(ref dxSup); Kill(ref dxRes); Kill(ref dxCore); Kill(ref dxRaw);
            Kill(ref dxBuy); Kill(ref dxSell); Kill(ref dxPoc); Kill(ref dxWhite);
        }

        private void EnsureStyles()
        {
            if (stDot == null)
                stDot = new D2D.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, new D2D.StrokeStyleProperties { DashStyle = D2D.DashStyle.Dot });
            if (stDash == null)
                stDash = new D2D.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, new D2D.StrokeStyleProperties { DashStyle = D2D.DashStyle.Dash });
            if (tfCenter == null)
            {
                tfCenter = new DW.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", 10f);
                tfCenter.TextAlignment = DW.TextAlignment.Center;
                tfCenter.ParagraphAlignment = DW.ParagraphAlignment.Center;
            }
            if (tfLeft == null)
            {
                tfLeft = new DW.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", 11f);
                tfLeft.TextAlignment = DW.TextAlignment.Leading;
                tfLeft.ParagraphAlignment = DW.ParagraphAlignment.Center;
            }
        }

        private static D2D.Brush Op(D2D.Brush b, double transparencyPct)
        {
            b.Opacity = (float)Math.Max(0.0, Math.Min(1.0, (100.0 - transparencyPct) / 100.0));
            return b;
        }

        private static double LAlpha(double s) { return s >= 82 ? 8 : s >= 62 ? 22 : s >= 45 ? 38 : 52; }
        private static float  LWidth(double s) { return s >= 82 ? 4f : s >= 62 ? 3f : 2f; }
        private static int    Rings(double s)  { return s >= 82 ? 4 : s >= 66 ? 3 : s >= 50 ? 2 : 1; }
        private static float  RRadius(int r)   { return r >= 4 ? 9f : r == 3 ? 7f : r == 2 ? 5f : 3.5f; }
        private static double RAlpha(int r)    { return r >= 4 ? 68 : r == 3 ? 52 : r == 2 ? 30 : 4; }

        private D2D.Brush LvlBrush(bool supp) { return supp ? dxSup : dxRes; }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (Bars == null || ChartBars == null || RenderTarget == null || chartControl == null) return;
            if (dxSup == null || lastIdx < 0) return;
            EnsureStyles();

            List<Lvl> snap, snapB = new List<Lvl>();
            lock (gate)
            {
                snap = new List<Lvl>(lvls.Count);
                foreach (Lvl l in lvls) snap.Add(l.Clone());
                if (ShowBrokenLevels) foreach (Lvl l in brokenLvls) snapB.Add(l.Clone());
            }

            float xLast = chartControl.GetXByBarIndex(ChartBars, lastIdx);
            D2D.StrokeStyle ls = LevelLineStyle == SneakyBspLineStyle.Dotted ? stDot : LevelLineStyle == SneakyBspLineStyle.Dashed ? stDash : null;

            // ---- profile
            if (ShowProfile) DrawProfile(chartControl, chartScale, snap, xLast);

            // ---- broken levels (faded, end at the bar that broke them)
            foreach (Lvl l in snapB)
            {
                float xb = chartControl.GetXByBarIndex(ChartBars, l.Born);
                float xe = chartControl.GetXByBarIndex(ChartBars, l.Broke);
                float y  = chartScale.GetYByValue(l.P);
                RenderTarget.DrawLine(new SDX.Vector2(xb, y), new SDX.Vector2(xe, y), Op(LvlBrush(l.Supp), 25), 2f, stDash);
                RenderTarget.FillEllipse(new D2D.Ellipse(new SDX.Vector2(xb, y), 4f, 4f), Op(LvlBrush(l.Supp), 10));
                RenderTarget.FillEllipse(new D2D.Ellipse(new SDX.Vector2(xe, y), 3f, 3f), Op(LvlBrush(l.Supp), 10));
                if (ShowDotScore)
                    RenderTarget.DrawText(((int)Math.Round(l.Sc)).ToString(), tfCenter,
                        new SDX.RectangleF(xb - 14, y + (l.Supp ? 6 : -22), 28, 16), Op(LvlBrush(l.Supp), 0));
            }

            // ---- level lines
            foreach (Lvl l in snap)
            {
                float xb = chartControl.GetXByBarIndex(ChartBars, l.Born);
                float y  = chartScale.GetYByValue(l.P);
                D2D.Brush b = Op(LvlBrush(l.Supp), LAlpha(l.Sc));
                if (ls == null) RenderTarget.DrawLine(new SDX.Vector2(xb, y), new SDX.Vector2(xLast, y), b, LWidth(l.Sc));
                else            RenderTarget.DrawLine(new SDX.Vector2(xb, y), new SDX.Vector2(xLast, y), b, LWidth(l.Sc), ls);
            }

            // ---- pivot markers
            if (ShowPivotDots)
            {
                foreach (Lvl l in snap)
                {
                    float xb = chartControl.GetXByBarIndex(ChartBars, l.Born);
                    float y  = chartScale.GetYByValue(l.P);
                    if (ShowRawPivot)
                    {
                        float yr = chartScale.GetYByValue(l.Raw);
                        RenderTarget.DrawLine(new SDX.Vector2(xb, yr), new SDX.Vector2(xb, y), Op(dxCore, 45), 1f, stDot);
                        RenderTarget.FillEllipse(new D2D.Ellipse(new SDX.Vector2(xb, yr), 3f, 3f), Op(dxRaw, 60));
                    }
                    int rc = Rings(l.Sc);
                    for (int r = rc; r >= 1; r--)
                    {
                        D2D.Brush fill = r == 1 ? Op(dxCore, l.Evid >= 0.45 ? 0 : 22) : Op(LvlBrush(l.Supp), RAlpha(r));
                        float rad = RRadius(r);
                        RenderTarget.FillEllipse(new D2D.Ellipse(new SDX.Vector2(xb, y), rad, rad), fill);
                    }
                    if (ShowDotScore)
                        RenderTarget.DrawText(((int)Math.Round(l.Sc)).ToString(), tfCenter,
                            new SDX.RectangleF(xb - 14, y - 8, 28, 16), Op(dxWhite, 0));
                }
            }

            // ---- right-edge score
            if (ShowRightScore)
            {
                foreach (Lvl l in snap)
                {
                    float y = chartScale.GetYByValue(l.P);
                    D2D.Brush b = Op(LvlBrush(l.Supp), 0);
                    RenderTarget.FillEllipse(new D2D.Ellipse(new SDX.Vector2(xLast, y), 3f, 3f), b);
                    RenderTarget.DrawText(((int)Math.Round(l.Sc)).ToString(), tfLeft,
                        new SDX.RectangleF(xLast + 6, y - 9, 40, 18), b);
                }
            }
        }

        private void DrawProfile(ChartControl cc, ChartScale cs, List<Lvl> snap, float xLast)
        {
            int bars = Math.Max(1, Math.Min(Lookback, lastIdx + 1));
            double bot, step; double[] tot, buy;
            BuildProfile(lastIdx - bars + 1, lastIdx, Rows, out bot, out step, out tot, out buy);

            double mx = 0; int poc = 0;
            for (int i = 0; i < tot.Length; i++) if (tot[i] > mx) { mx = tot[i]; poc = i; }
            if (mx <= 0) return;

            float x = xLast + ProfileOffsetPx;
            for (int r = 0; r < Rows; r++)
            {
                double rv = tot[r];
                if (rv <= 0) continue;
                double str = rv / mx;
                double bSh = Clip(buy[r] / rv);
                int tw = Math.Max(1, (int)Math.Round(ProfileWidthPx * str));
                int bw = (int)Math.Round(tw * bSh);
                int sw = tw - bw;
                double p0 = bot + (r + 0.08) * step, p1 = bot + (r + 0.92) * step;
                float yTop = cs.GetYByValue(p1), yBot = cs.GetYByValue(p0);
                float h = Math.Max(1f, yBot - yTop);
                double fade = Math.Round(76.0 - 46.0 * str);

                if (bw > 0) RenderTarget.FillRectangle(new SDX.RectangleF(x, yTop, bw, h), Op(dxBuy, fade));
                if (sw > 0) RenderTarget.FillRectangle(new SDX.RectangleF(x + bw, yTop, sw, h), Op(dxSell, fade));

                if (ShowPoc && r == poc)
                {
                    RenderTarget.FillRectangle(new SDX.RectangleF(x, yTop, tw, h), Op(dxPoc, 88));
                    RenderTarget.DrawRectangle(new SDX.RectangleF(x, yTop, tw, h), Op(dxPoc, 0), 2f);
                }
                if (ShowLevelRows)
                {
                    int side = 0;
                    foreach (Lvl l in snap)
                        if (l.P >= p0 && l.P <= p1) { side = l.Supp ? 1 : -1; break; }
                    if (side != 0)
                    {
                        RenderTarget.FillRectangle(new SDX.RectangleF(x, yTop, tw, h), Op(dxCore, 93));
                        RenderTarget.DrawRectangle(new SDX.RectangleF(x, yTop, tw, h), Op(side > 0 ? dxSup : dxRes, 0), 2f);
                    }
                }
            }

            if (ShowPoc)
            {
                double pp = bot + (poc + 0.5) * step;
                float y = cs.GetYByValue(pp);
                float xs = cc.GetXByBarIndex(ChartBars, Math.Max(0, lastIdx - 18));
                RenderTarget.DrawLine(new SDX.Vector2(xs, y), new SDX.Vector2(x, y), Op(dxPoc, 25), 2f);
                RenderTarget.DrawText("POC " + Instrument.MasterInstrument.FormatPrice(pp), tfLeft,
                    new SDX.RectangleF(x + ProfileWidthPx + 4, y - 9, 140, 18), Op(dxPoc, 0));
            }
        }

        // ================================================================ PROPERTIES
        #region Properties
        [Range(100, 1500)]
        [Display(Name = "Lookback (bars)", GroupName = "1. Rolling profile", Order = 0, Description = "Bars in the rolling profile. Pivots are judged with the same lookback as it stood when they were confirmed.")]
        public int Lookback { get; set; }

        [Range(24, 100)]
        [Display(Name = "Rows", GroupName = "1. Rolling profile", Order = 1)]
        public int Rows { get; set; }

        [Range(20, 800)]
        [Display(Name = "Width (px)", GroupName = "1. Rolling profile", Order = 2, Description = "Width of the POC row in pixels.")]
        public int ProfileWidthPx { get; set; }

        [Range(0, 400)]
        [Display(Name = "Offset (px)", GroupName = "1. Rolling profile", Order = 3, Description = "Gap between the last bar and the profile. Give the chart Right Margin.")]
        public int ProfileOffsetPx { get; set; }

        [Display(Name = "Show profile", GroupName = "1. Rolling profile", Order = 4)]
        public bool ShowProfile { get; set; }

        [Display(Name = "Show POC", GroupName = "1. Rolling profile", Order = 5)]
        public bool ShowPoc { get; set; }

        [Display(Name = "Outline level rows", GroupName = "1. Rolling profile", Order = 6)]
        public bool ShowLevelRows { get; set; }

        [Range(2, 25)]
        [Display(Name = "Swing length", GroupName = "2. Pivot engine", Order = 0, Description = "Bars each side to confirm a pivot (confirmed this many bars late).")]
        public int SwingLength { get; set; }

        [Range(0.2, 3.0)]
        [Display(Name = "Search ATR", GroupName = "2. Pivot engine", Order = 1, Description = "Max ATR distance around the raw pivot to look for a stronger volume node.")]
        public double SearchAtr { get; set; }

        [Range(0.0, 1.0)]
        [Display(Name = "Min node (vs POC)", GroupName = "2. Pivot engine", Order = 2)]
        public double MinNode { get; set; }

        [Range(0.0, 1.0)]
        [Display(Name = "Min evidence", GroupName = "2. Pivot engine", Order = 3)]
        public double MinEvidence { get; set; }

        [Range(0.0, 1.0)]
        [Display(Name = "Profile pull", GroupName = "2. Pivot engine", Order = 4, Description = "Max fraction of the distance from raw pivot toward the volume node.")]
        public double ProfilePull { get; set; }

        [Range(0.0, 100.0)]
        [Display(Name = "Min score", GroupName = "2. Pivot engine", Order = 5)]
        public double MinScore { get; set; }

        [Range(2, 40)]
        [Display(Name = "Max levels", GroupName = "2. Pivot engine", Order = 6)]
        public int MaxLevels { get; set; }

        [Range(0.0, 1.0)]
        [Display(Name = "Merge ATR", GroupName = "2. Pivot engine", Order = 7, Description = "Same-side levels closer than this are merged (stronger wins).")]
        public double MergeAtr { get; set; }

        [Range(0.0, 0.5)]
        [Display(Name = "Break ATR", GroupName = "2. Pivot engine", Order = 8, Description = "A bar must close this many ATR beyond a level to remove it.")]
        public double BreakAtr { get; set; }

        [XmlIgnore][Display(Name = "Support", GroupName = "3. Style", Order = 0)]
        public Brush SupportBrush { get; set; }
        [Browsable(false)] public string SupportBrushSerialize { get { return Serialize.BrushToString(SupportBrush); } set { SupportBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Resistance", GroupName = "3. Style", Order = 1)]
        public Brush ResistanceBrush { get; set; }
        [Browsable(false)] public string ResistanceBrushSerialize { get { return Serialize.BrushToString(ResistanceBrush); } set { ResistanceBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Pivot core", GroupName = "3. Style", Order = 2)]
        public Brush CoreBrush { get; set; }
        [Browsable(false)] public string CoreBrushSerialize { get { return Serialize.BrushToString(CoreBrush); } set { CoreBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Raw pivot", GroupName = "3. Style", Order = 3)]
        public Brush RawBrush { get; set; }
        [Browsable(false)] public string RawBrushSerialize { get { return Serialize.BrushToString(RawBrush); } set { RawBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Profile buy", GroupName = "3. Style", Order = 4)]
        public Brush BuyBrush { get; set; }
        [Browsable(false)] public string BuyBrushSerialize { get { return Serialize.BrushToString(BuyBrush); } set { BuyBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Profile sell", GroupName = "3. Style", Order = 5)]
        public Brush SellBrush { get; set; }
        [Browsable(false)] public string SellBrushSerialize { get { return Serialize.BrushToString(SellBrush); } set { SellBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "POC", GroupName = "3. Style", Order = 6)]
        public Brush PocBrush { get; set; }
        [Browsable(false)] public string PocBrushSerialize { get { return Serialize.BrushToString(PocBrush); } set { PocBrush = Serialize.StringToBrush(value); } }

        [Display(Name = "Line style", GroupName = "3. Style", Order = 7)]
        public SneakyBspLineStyle LevelLineStyle { get; set; }

        [Display(Name = "Pivot dots", GroupName = "3. Style", Order = 8)]
        public bool ShowPivotDots { get; set; }

        [Display(Name = "Raw pivot + pull guide", GroupName = "3. Style", Order = 9)]
        public bool ShowRawPivot { get; set; }

        [Display(Name = "Score on dot", GroupName = "3. Style", Order = 10)]
        public bool ShowDotScore { get; set; }

        [Display(Name = "Score at right edge", GroupName = "3. Style", Order = 11)]
        public bool ShowRightScore { get; set; }

        [Display(Name = "Show broken levels", GroupName = "3. Style", Order = 12, Description = "Keep levels that price has closed through as faded dotted lines ending at the bar that broke them.")]
        public bool ShowBrokenLevels { get; set; }

        [Range(0, 500)]
        [Display(Name = "Max broken levels kept", GroupName = "3. Style", Order = 13)]
        public int MaxBrokenLevels { get; set; }

        [Display(Name = "Show buy/sell signals", GroupName = "4. Signals", Order = 0, Description = "Arrow when a bar wicks into a level and closes back away from it.")]
        public bool ShowSignals { get; set; }

        [Range(0, 100)]
        [Display(Name = "Signal min score", GroupName = "4. Signals", Order = 1, Description = "Only levels scoring at least this fire signals.")]
        public int SignalMinScore { get; set; }

        [Range(0.0, 2.0)]
        [Display(Name = "Touch tolerance (x ATR)", GroupName = "4. Signals", Order = 2, Description = "How close the wick must get to the level.")]
        public double SignalTouchAtr { get; set; }

        [Range(0, 200)]
        [Display(Name = "Cooldown (bars per level)", GroupName = "4. Signals", Order = 3)]
        public int SignalCooldown { get; set; }

        [Display(Name = "Alerts", GroupName = "4. Signals", Order = 4)]
        public bool SignalAlerts { get; set; }

        [Browsable(false)][XmlIgnore]
        public Series<double> BuySignal { get { return Values[0]; } }

        [Browsable(false)][XmlIgnore]
        public Series<double> SellSignal { get { return Values[1]; } }

        [Display(Name = "Debug log (Output window)", GroupName = "5. Debug", Order = 0, Description = "Prints every pivot with its node strength, evidence and score, and why it was stored or rejected.")]
        public bool DebugLog { get; set; }
        #endregion
    }
}
