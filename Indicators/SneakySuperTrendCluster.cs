#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SDX = SharpDX;
using D2D = SharpDX.Direct2D1;
#endregion

// SneakySuperTrendCluster
// NinjaTrader 8 port of "SuperTrend Cluster" by Zeiierman (TradingView, Pine v6).
// Original work (c) Zeiierman, licensed CC BY-NC-SA 4.0
//   https://creativecommons.org/licenses/by-nc-sa/4.0/
// This port is an adaptation and is therefore distributed under the same license:
// attribution to Zeiierman required, NON-COMMERCIAL use only, share-alike.
//
// What it does (same as the original)
//   * Five SuperTrends, each built on a moving average of HLC3 (SMA/EMA/LSMA/WMA/HMA/RMA) with its own
//     ATR length, factor and weight.
//   * Consensus: the weight share of bullish vs bearish members. The cluster becomes bullish when the bullish
//     share >= threshold AND the selected base SuperTrend is bullish (bearish likewise). It keeps its last
//     regime until the opposite one qualifies.
//   * Cluster line = weighted average of the SuperTrend lines on the winning side; cloud fills between it and
//     an SMA(HLC3) reference; candles are colored by cluster strength; ▲/▼ boxes mark regime flips; small
//     triangles + "Bull/Bear Cluster xx%" labels mark base SuperTrend flips.
//
// Notes
//   * Keep Calculate on "On bar close" (default).
//   * Automation plots (invisible, written every bar; see README):
//       BuySignal  +1 on the bar the cluster turns bullish (the green ▲ box), else 0
//       SellSignal -1 on the bar the cluster turns bearish (the red ▼ box), else 0
//       Regime     +1 bullish / -1 bearish / 0 none yet (the cluster's current state)
//       BullScore / BearScore  weighted share (0..1) of bullish / bearish members
//       BaseFlip   +1 / -1 on the bar the base SuperTrend flips up / down, else 0
//       ConsensusScore  BullScore - BearScore (-1..+1)
//       ClusterLine  price of the active cluster line (0 when none)
//   * Pine "na" is modelled as NaN; moving averages, ATR and bands start only once they have enough bars,
//     exactly like Pine, so the first ~50 bars of a chart are blank.

namespace NinjaTrader.NinjaScript.Indicators
{
    public enum SneakyStMaType { SMA, EMA, LSMA, WMA, HMA, RMA }

    public class SneakySuperTrendCluster : Indicator
    {
        private const int N = 5;
        private const double EPS = 0.0000001;

        private Series<double> hlc3S, cloudRefS, dLastS, stBS, dBS;
        private Series<double>[] maS, diffS, ubS, lbS, dS, stS;
        private ATR[] atrI;

        private int[]            aLen = new int[N];
        private double[]         fac  = new double[N];
        private SneakyStMaType[] mType = new SneakyStMaType[N];
        private int[]            mLen = new int[N];
        private double[]         wgt  = new double[N];

        private Brush[] barBull, barBear;          // gradient lookup tables (101 steps)
        private D2D.Brush dxCloudBull, dxCloudBear;

        // ------------------------------------------------------------------ lifecycle
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Five weighted SuperTrends combined into one consensus cluster (port of Zeiierman).";
                Name                     = "SneakySuperTrendCluster";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsAutoScale              = false;   // invisible automation plots must not stretch the price scale
                DisplayInDataBox         = true;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = true;
                MaximumBarsLookBack      = MaximumBarsLookBack.Infinite;

                Threshold      = 0.60;
                BaseIndex      = 3;
                UseBarColor    = true;
                ShowLabels     = true;
                ShowDots       = true;
                BullBrush      = Rgb(0x00, 0xE6, 0x76);
                BearBrush      = Rgb(0xF7, 0x52, 0x5F);
                NeutralBrush   = Rgb(0xFF, 0x98, 0x00);

                ShowCloud      = true;
                CloudLength    = 8;
                CloudBullBrush = Rgb(0x00, 0xE6, 0x76);
                CloudBearBrush = Rgb(0xF7, 0x52, 0x5F);
                CloudTransparency = 65;

                PopupAlerts    = true;
                SoundFile      = "Alert2.wav";

                St1Atr = 7;  St1Factor = 1.5; St1Ma = SneakyStMaType.EMA; St1MaLen = 3;  St1Weight = 1.0;
                St2Atr = 10; St2Factor = 2.0; St2Ma = SneakyStMaType.EMA; St2MaLen = 5;  St2Weight = 1.0;
                St3Atr = 14; St3Factor = 2.5; St3Ma = SneakyStMaType.SMA; St3MaLen = 8;  St3Weight = 1.2;
                St4Atr = 21; St4Factor = 3.0; St4Ma = SneakyStMaType.WMA; St4MaLen = 13; St4Weight = 1.4;
                St5Atr = 34; St5Factor = 4.0; St5Ma = SneakyStMaType.HMA; St5MaLen = 21; St5Weight = 1.6;

                AddPlot(new Stroke(Brushes.Lime, 2),        PlotStyle.Line, "ClusterUp");        // 0
                AddPlot(new Stroke(Brushes.Red, 2),         PlotStyle.Line, "ClusterDown");      // 1
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "ClusterLine");      // 2: active line price, else 0
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "BuySignal");        // 3
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SellSignal");       // 4
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Regime");           // 5
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "BullScore");        // 6
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "BearScore");        // 7
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "BaseFlip");         // 8
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "ConsensusScore");   // 9
            }
            else if (State == State.Configure)
            {
                aLen[0] = St1Atr; aLen[1] = St2Atr; aLen[2] = St3Atr; aLen[3] = St4Atr; aLen[4] = St5Atr;
                fac[0]  = St1Factor; fac[1] = St2Factor; fac[2] = St3Factor; fac[3] = St4Factor; fac[4] = St5Factor;
                mType[0] = St1Ma; mType[1] = St2Ma; mType[2] = St3Ma; mType[3] = St4Ma; mType[4] = St5Ma;
                mLen[0] = St1MaLen; mLen[1] = St2MaLen; mLen[2] = St3MaLen; mLen[3] = St4MaLen; mLen[4] = St5MaLen;
                wgt[0]  = St1Weight; wgt[1] = St2Weight; wgt[2] = St3Weight; wgt[3] = St4Weight; wgt[4] = St5Weight;

                Plots[0].Brush = BullBrush;
                Plots[1].Brush = BearBrush;
            }
            else if (State == State.DataLoaded)
            {
                hlc3S     = S(); cloudRefS = S(); dLastS = S(); stBS = S(); dBS = S();
                maS = new Series<double>[N]; diffS = new Series<double>[N]; ubS = new Series<double>[N];
                lbS = new Series<double>[N]; dS = new Series<double>[N]; stS = new Series<double>[N];
                atrI = new ATR[N];
                for (int k = 0; k < N; k++)
                {
                    maS[k] = S(); diffS[k] = S(); ubS[k] = S(); lbS[k] = S(); dS[k] = S(); stS[k] = S();
                    atrI[k] = ATR(Math.Max(1, aLen[k]));
                }
                BuildGradients();
            }
            else if (State == State.Terminated)
            {
                DisposeBrushes();
            }
        }

        private Series<double> S() { return new Series<double>(this, MaximumBarsLookBack.Infinite); }

        private static Brush Rgb(byte r, byte g, byte b)
        {
            Brush br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        private static Color ColorOf(Brush b)
        {
            SolidColorBrush s = b as SolidColorBrush;
            return s != null ? s.Color : Colors.Gray;
        }

        // color.from_gradient(strength, 0, 1, neutral, target) as a 101-step lookup
        private void BuildGradients()
        {
            Color n = ColorOf(NeutralBrush), u = ColorOf(BullBrush), d = ColorOf(BearBrush);
            barBull = new Brush[101]; barBear = new Brush[101];
            for (int i = 0; i <= 100; i++)
            {
                double t = i / 100.0;
                barBull[i] = Lerp(n, u, t);
                barBear[i] = Lerp(n, d, t);
            }
        }

        private static Brush Lerp(Color a, Color b, double t)
        {
            byte r  = (byte)Math.Round(a.R + (b.R - a.R) * t);
            byte g  = (byte)Math.Round(a.G + (b.G - a.G) * t);
            byte bl = (byte)Math.Round(a.B + (b.B - a.B) * t);
            Brush br = new SolidColorBrush(Color.FromRgb(r, g, bl));
            br.Freeze();
            return br;
        }

        // ------------------------------------------------------------------ moving averages (Pine semantics, NaN = na)
        private static double SmaOf(Series<double> s, int len)
        {
            double sum = 0;
            for (int i = 0; i < len; i++) sum += s[i];
            return sum / len;
        }

        // ta.wma: weights len..1, newest bar has the largest weight
        private double WmaOf(Series<double> s, int len)
        {
            if (len < 1) len = 1;
            if (CurrentBar < len - 1) return double.NaN;
            double num = 0, den = len * (len + 1) / 2.0;
            for (int i = 0; i < len; i++) num += s[i] * (len - i);
            return num / den;
        }

        // ta.linreg(src, len, 0): regression line value at the newest bar
        private double LinRegOf(Series<double> s, int len)
        {
            if (CurrentBar < len - 1) return double.NaN;
            if (len == 1) return s[0];
            double sx = 0, sy = 0, sxy = 0, sxx = 0;
            for (int i = 0; i < len; i++)
            {
                double x = i;                     // 0 = oldest
                double y = s[len - 1 - i];
                sx += x; sy += y; sxy += x * y; sxx += x * x;
            }
            double den = len * sxx - sx * sx;
            if (den == 0) return s[0];
            double slope = (len * sxy - sx * sy) / den;
            double icpt  = (sy - slope * sx) / len;
            return icpt + slope * (len - 1);
        }

        private double CalcMa(int k)
        {
            int b = CurrentBar;
            int len = Math.Max(1, mLen[k]);
            switch (mType[k])
            {
                case SneakyStMaType.EMA:
                {
                    double prev = b > 0 ? maS[k][1] : double.NaN;
                    double a = 2.0 / (len + 1);
                    return double.IsNaN(prev) ? hlc3S[0] : a * hlc3S[0] + (1 - a) * prev;
                }
                case SneakyStMaType.RMA:
                {
                    if (b < len - 1) return double.NaN;
                    double prev = b > 0 ? maS[k][1] : double.NaN;
                    return double.IsNaN(prev) ? SmaOf(hlc3S, len) : (prev * (len - 1) + hlc3S[0]) / len;
                }
                case SneakyStMaType.WMA: return WmaOf(hlc3S, len);
                case SneakyStMaType.LSMA: return LinRegOf(hlc3S, len);
                case SneakyStMaType.HMA:
                {
                    int half = Math.Max(1, len / 2);
                    int sq   = Math.Max(1, (int)Math.Round(Math.Sqrt(len), MidpointRounding.AwayFromZero));
                    diffS[k][0] = 2 * WmaOf(hlc3S, half) - WmaOf(hlc3S, len);
                    return WmaOf(diffS[k], sq);
                }
                default: // SMA
                    return b >= len - 1 ? SmaOf(hlc3S, len) : double.NaN;
            }
        }

        // ------------------------------------------------------------------ main
        protected override void OnBarUpdate()
        {
            int b = CurrentBar;
            hlc3S[0] = (High[0] + Low[0] + Close[0]) / 3.0;
            for (int k = 3; k <= 9; k++) Values[k][0] = 0;

            // ---- five SuperTrends (Pine fSt)
            double wSum = 0, wBu = 0, wBe = 0, lnBuNum = 0, lnBeNum = 0;
            for (int k = 0; k < N; k++)
            {
                double src = CalcMa(k);
                maS[k][0] = src;
                double srcPrev = b > 0 ? maS[k][1] : double.NaN;

                double atr = b >= aLen[k] - 1 ? atrI[k][0] : double.NaN;
                double ub0 = src + fac[k] * atr;
                double lb0 = src - fac[k] * atr;

                double ubPrev = b > 0 ? ubS[k][1] : double.NaN;
                double lbPrev = b > 0 ? lbS[k][1] : double.NaN;
                double dPrev  = b > 0 ? dS[k][1]  : double.NaN;

                double ub = double.IsNaN(ubPrev) ? ub0 : ((ub0 < ubPrev || srcPrev > ubPrev) ? ub0 : ubPrev);
                double lb = double.IsNaN(lbPrev) ? lb0 : ((lb0 > lbPrev || srcPrev < lbPrev) ? lb0 : lbPrev);

                double d;
                if (double.IsNaN(dPrev)) d = 1.0;
                else if (dPrev == -1.0 && src > ubPrev) d = 1.0;
                else if (dPrev == 1.0 && src < lbPrev) d = -1.0;
                else d = dPrev;

                ubS[k][0] = ub; lbS[k][0] = lb; dS[k][0] = d;
                double st = d == 1.0 ? lb : ub;
                stS[k][0] = st;

                // ---- consensus accumulation
                double w = wgt[k];
                wSum += w;
                if (d > 0) { wBu += w; lnBuNum += st * w; }
                else if (d < 0) { wBe += w; lnBeNum += st * w; }
            }

            wSum = Math.Max(wSum, EPS);
            double scBu = wBu / wSum, scBe = wBe / wSum, scCl = scBu - scBe, strCl = Math.Abs(scCl);
            double lnBu = wBu > 0 ? lnBuNum / wBu : double.NaN;
            double lnBe = wBe > 0 ? lnBeNum / wBe : double.NaN;

            int baseRow = Math.Max(0, Math.Min(N - 1, BaseIndex - 1));
            double stB = stS[baseRow][0], dB = dS[baseRow][0];
            stBS[0] = stB; dBS[0] = dB;
            double dBPrev = b > 0 ? dBS[1] : double.NaN;
            bool flipBu = b > 0 && dB > 0 && dBPrev <= 0;
            bool flipBe = b > 0 && dB < 0 && dBPrev >= 0;

            // ---- final filtered regime (Pine "var dLast")
            bool isBu = scBu >= Threshold, isBe = scBe >= Threshold;
            bool okBu = isBu && dB > 0, okBe = isBe && dB < 0;
            double dLastPrev = b > 0 ? dLastS[1] : 0.0;
            double dLast = dLastPrev;
            if (okBu && !okBe) dLast = 1.0;
            else if (okBe && !okBu) dLast = -1.0;
            dLastS[0] = dLast;

            double lnCl = dLast > 0 ? lnBu : (dLast < 0 ? lnBe : double.NaN);

            // ---- cloud reference
            cloudRefS[0] = b >= CloudLength - 1 ? SmaOf(hlc3S, Math.Max(1, CloudLength)) : double.NaN;

            // ---- plots
            if (dLast == 1 && !double.IsNaN(lnCl)) Values[0][0] = lnCl; else Values[0].Reset();
            if (dLast == -1 && !double.IsNaN(lnCl)) Values[1][0] = lnCl; else Values[1].Reset();
            Values[2][0] = double.IsNaN(lnCl) ? 0 : lnCl;

            bool majorLong  = b > 0 && dLast > 0 && dLastPrev <= 0;
            bool majorShort = b > 0 && dLast < 0 && dLastPrev >= 0;
            Values[3][0] = majorLong  ? 1 : 0;
            Values[4][0] = majorShort ? -1 : 0;
            Values[5][0] = dLast;
            Values[6][0] = scBu;
            Values[7][0] = scBe;
            Values[8][0] = flipBu ? 1 : (flipBe ? -1 : 0);
            Values[9][0] = scCl;

            // ---- bar / candle coloring
            if (UseBarColor)
            {
                int idx = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, strCl)) * 100.0);
                Brush c = scCl > 0 ? barBull[idx] : barBear[idx];
                BarBrush = c;
                CandleOutlineBrush = c;
            }

            // ---- major regime markers (▲ below bar / ▼ above bar)
            SimpleFont fMark = new SimpleFont("Arial", 11) { Bold = true };
            if (majorLong)
                Draw.Text(this, "stc_ml" + b, false, "▲", 0, Low[0], 18, Brushes.White, fMark,
                    System.Windows.TextAlignment.Center, BullBrush, BullBrush, 70);
            if (majorShort)
                Draw.Text(this, "stc_ms" + b, false, "▼", 0, High[0], -18, Brushes.White, fMark,
                    System.Windows.TextAlignment.Center, BearBrush, BearBrush, 70);

            // ---- base SuperTrend flip dots (colour follows the regime, as in the original)
            if (ShowDots && !double.IsNaN(stB) && (flipBu || flipBe))
            {
                Brush dc = dLast > 0 ? BullBrush : BearBrush;
                if (flipBu) Draw.TriangleUp(this, "stc_du" + b, false, 0, stB, dc);
                else        Draw.TriangleDown(this, "stc_dd" + b, false, 0, stB, dc);
            }

            // ---- flip labels with the cluster share
            SimpleFont fLbl = new SimpleFont("Arial", 9);
            if (ShowLabels && !double.IsNaN(stB))
            {
                if (flipBu)
                    Draw.Text(this, "stc_lu" + b, false, "Bull Cluster\n" + (scBu * 100.0).ToString("0.0") + "%", 0, stB, 26,
                        BullBrush, fLbl, System.Windows.TextAlignment.Center, BullBrush, BullBrush, 10);
                if (flipBe)
                    Draw.Text(this, "stc_ld" + b, false, "Bear Cluster\n" + (scBe * 100.0).ToString("0.0") + "%", 0, stB, -26,
                        BearBrush, fLbl, System.Windows.TextAlignment.Center, BearBrush, BearBrush, 10);
            }

            // ---- alerts (live bars only)
            if (State == State.Realtime && PopupAlerts && (majorLong || majorShort))
            {
                string snd = NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + SoundFile;
                Alert("stc_" + b, Priority.High,
                    Instrument.FullName + (majorLong ? " Bullish clustered SuperTrend signal" : " Bearish clustered SuperTrend signal"),
                    snd, 10, Brushes.Black, majorLong ? Brushes.LimeGreen : Brushes.OrangeRed);
            }
        }

        // ------------------------------------------------------------------ cloud fill (OnRender)
        public override void OnRenderTargetChanged()
        {
            DisposeBrushes();
            if (RenderTarget == null) return;
            float op = (float)(1.0 - CloudTransparency / 100.0);
            dxCloudBull = CloudBullBrush.ToDxBrush(RenderTarget); dxCloudBull.Opacity = op;
            dxCloudBear = CloudBearBrush.ToDxBrush(RenderTarget); dxCloudBear.Opacity = op;
        }

        private static void Kill(ref D2D.Brush b) { if (b != null) { b.Dispose(); b = null; } }
        private void DisposeBrushes() { Kill(ref dxCloudBull); Kill(ref dxCloudBear); }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (ShowCloud && Bars != null && ChartBars != null && RenderTarget != null && dLastS != null && dxCloudBull != null)
            {
                try { DrawCloud(chartControl, chartScale); } catch { }
            }
            base.OnRender(chartControl, chartScale);
        }

        private bool Ok(Series<double> s, int i)
        {
            if (i < 0 || !s.IsValidDataPointAt(i)) return false;
            double v = s.GetValueAt(i);
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private bool CloudOk(int i)
        {
            return Ok(cloudRefS, i) && Ok(Values[2], i) && Values[2].GetValueAt(i) != 0 && Ok(dLastS, i) && dLastS.GetValueAt(i) != 0;
        }

        private void DrawCloud(ChartControl cc, ChartScale cs)
        {
            int from = Math.Max(ChartBars.FromIndex, 1);
            int to   = Math.Min(ChartBars.ToIndex, Bars.Count - 1);
            int runStart = -1, runDir = 0;
            for (int i = from; i <= to + 1; i++)
            {
                bool ok = i <= to && CloudOk(i);
                int dir = ok ? (dLastS.GetValueAt(i) > 0 ? 1 : -1) : 0;
                if (runStart >= 0 && (!ok || dir != runDir))
                {
                    Emit(cc, cs, runStart, i - 1, runDir > 0 ? dxCloudBull : dxCloudBear);
                    runStart = -1;
                    if (ok && CloudOk(i - 1)) { runStart = i - 1; runDir = dir; continue; }
                }
                if (runStart < 0 && ok) { runStart = i; runDir = dir; }
            }
        }

        private void Emit(ChartControl cc, ChartScale cs, int s, int e, D2D.Brush brush)
        {
            if (e <= s) return;
            using (D2D.PathGeometry geo = new D2D.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
            {
                D2D.GeometrySink sink = geo.Open();
                sink.BeginFigure(new SDX.Vector2(cc.GetXByBarIndex(ChartBars, s), cs.GetYByValue(Values[2].GetValueAt(s))), D2D.FigureBegin.Filled);
                for (int k = s + 1; k <= e; k++)
                    sink.AddLine(new SDX.Vector2(cc.GetXByBarIndex(ChartBars, k), cs.GetYByValue(Values[2].GetValueAt(k))));
                for (int k = e; k >= s; k--)
                    sink.AddLine(new SDX.Vector2(cc.GetXByBarIndex(ChartBars, k), cs.GetYByValue(cloudRefS.GetValueAt(k))));
                sink.EndFigure(D2D.FigureEnd.Closed);
                sink.Close();
                RenderTarget.FillGeometry(geo, brush);
            }
        }

        // ------------------------------------------------------------------ outputs for strategies / automation tools
        [Browsable(false), XmlIgnore] public Series<double> ClusterUp      { get { Update(); return Values[0]; } }
        [Browsable(false), XmlIgnore] public Series<double> ClusterDown    { get { Update(); return Values[1]; } }
        [Browsable(false), XmlIgnore] public Series<double> ClusterLine    { get { Update(); return Values[2]; } }
        [Browsable(false), XmlIgnore] public Series<double> BuySignal      { get { Update(); return Values[3]; } }
        [Browsable(false), XmlIgnore] public Series<double> SellSignal     { get { Update(); return Values[4]; } }
        [Browsable(false), XmlIgnore] public Series<double> Regime         { get { Update(); return Values[5]; } }
        [Browsable(false), XmlIgnore] public Series<double> BullScore      { get { Update(); return Values[6]; } }
        [Browsable(false), XmlIgnore] public Series<double> BearScore      { get { Update(); return Values[7]; } }
        [Browsable(false), XmlIgnore] public Series<double> BaseFlip       { get { Update(); return Values[8]; } }
        [Browsable(false), XmlIgnore] public Series<double> ConsensusScore { get { Update(); return Values[9]; } }

        #region Properties
        [NinjaScriptProperty, Range(0.0, 1.0)]
        [Display(Name = "Consensus threshold", GroupName = "1. Cluster engine", Order = 0, Description = "Minimum weighted agreement for a bullish or bearish cluster to become valid.")]
        public double Threshold { get; set; }

        [NinjaScriptProperty, Range(1, 5)]
        [Display(Name = "Base SuperTrend index", GroupName = "1. Cluster engine", Order = 1, Description = "Which of the five SuperTrends drives flip markers, labels and final direction alignment.")]
        public int BaseIndex { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Dynamic bar coloring", GroupName = "2. Visuals", Order = 0)]
        public bool UseBarColor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show cluster labels", GroupName = "2. Visuals", Order = 1)]
        public bool ShowLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show base SuperTrend flip dots", GroupName = "2. Visuals", Order = 2)]
        public bool ShowDots { get; set; }

        [XmlIgnore][Display(Name = "Bull", GroupName = "2. Visuals", Order = 3)]
        public Brush BullBrush { get; set; }
        [Browsable(false)] public string BullBrushSerialize { get { return Serialize.BrushToString(BullBrush); } set { BullBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Bear", GroupName = "2. Visuals", Order = 4)]
        public Brush BearBrush { get; set; }
        [Browsable(false)] public string BearBrushSerialize { get { return Serialize.BrushToString(BearBrush); } set { BearBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Neutral", GroupName = "2. Visuals", Order = 5)]
        public Brush NeutralBrush { get; set; }
        [Browsable(false)] public string NeutralBrushSerialize { get { return Serialize.BrushToString(NeutralBrush); } set { NeutralBrush = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Display(Name = "Show cloud fill", GroupName = "3. Cloud", Order = 0)]
        public bool ShowCloud { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Cloud reference length", GroupName = "3. Cloud", Order = 1)]
        public int CloudLength { get; set; }

        [XmlIgnore][Display(Name = "Bull cloud", GroupName = "3. Cloud", Order = 2)]
        public Brush CloudBullBrush { get; set; }
        [Browsable(false)] public string CloudBullBrushSerialize { get { return Serialize.BrushToString(CloudBullBrush); } set { CloudBullBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Bear cloud", GroupName = "3. Cloud", Order = 3)]
        public Brush CloudBearBrush { get; set; }
        [Browsable(false)] public string CloudBearBrushSerialize { get { return Serialize.BrushToString(CloudBearBrush); } set { CloudBearBrush = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty, Range(0, 95)]
        [Display(Name = "Cloud transparency (%)", GroupName = "3. Cloud", Order = 4)]
        public int CloudTransparency { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert on cluster flips", GroupName = "9. Alerts", Order = 0)]
        public bool PopupAlerts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert sound file", GroupName = "9. Alerts", Order = 1)]
        public string SoundFile { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "ATR length", GroupName = "4. SuperTrend 1", Order = 0)]
        public int St1Atr { get; set; }

        [NinjaScriptProperty, Range(0.01, 100.0)]
        [Display(Name = "Factor", GroupName = "4. SuperTrend 1", Order = 1)]
        public double St1Factor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Smoothing", GroupName = "4. SuperTrend 1", Order = 2)]
        public SneakyStMaType St1Ma { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Smoothing length", GroupName = "4. SuperTrend 1", Order = 3)]
        public int St1MaLen { get; set; }

        [NinjaScriptProperty, Range(0.0, 100.0)]
        [Display(Name = "Weight", GroupName = "4. SuperTrend 1", Order = 4)]
        public double St1Weight { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "ATR length", GroupName = "5. SuperTrend 2", Order = 0)]
        public int St2Atr { get; set; }

        [NinjaScriptProperty, Range(0.01, 100.0)]
        [Display(Name = "Factor", GroupName = "5. SuperTrend 2", Order = 1)]
        public double St2Factor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Smoothing", GroupName = "5. SuperTrend 2", Order = 2)]
        public SneakyStMaType St2Ma { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Smoothing length", GroupName = "5. SuperTrend 2", Order = 3)]
        public int St2MaLen { get; set; }

        [NinjaScriptProperty, Range(0.0, 100.0)]
        [Display(Name = "Weight", GroupName = "5. SuperTrend 2", Order = 4)]
        public double St2Weight { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "ATR length", GroupName = "6. SuperTrend 3", Order = 0)]
        public int St3Atr { get; set; }

        [NinjaScriptProperty, Range(0.01, 100.0)]
        [Display(Name = "Factor", GroupName = "6. SuperTrend 3", Order = 1)]
        public double St3Factor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Smoothing", GroupName = "6. SuperTrend 3", Order = 2)]
        public SneakyStMaType St3Ma { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Smoothing length", GroupName = "6. SuperTrend 3", Order = 3)]
        public int St3MaLen { get; set; }

        [NinjaScriptProperty, Range(0.0, 100.0)]
        [Display(Name = "Weight", GroupName = "6. SuperTrend 3", Order = 4)]
        public double St3Weight { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "ATR length", GroupName = "7. SuperTrend 4", Order = 0)]
        public int St4Atr { get; set; }

        [NinjaScriptProperty, Range(0.01, 100.0)]
        [Display(Name = "Factor", GroupName = "7. SuperTrend 4", Order = 1)]
        public double St4Factor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Smoothing", GroupName = "7. SuperTrend 4", Order = 2)]
        public SneakyStMaType St4Ma { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Smoothing length", GroupName = "7. SuperTrend 4", Order = 3)]
        public int St4MaLen { get; set; }

        [NinjaScriptProperty, Range(0.0, 100.0)]
        [Display(Name = "Weight", GroupName = "7. SuperTrend 4", Order = 4)]
        public double St4Weight { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "ATR length", GroupName = "8. SuperTrend 5", Order = 0)]
        public int St5Atr { get; set; }

        [NinjaScriptProperty, Range(0.01, 100.0)]
        [Display(Name = "Factor", GroupName = "8. SuperTrend 5", Order = 1)]
        public double St5Factor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Smoothing", GroupName = "8. SuperTrend 5", Order = 2)]
        public SneakyStMaType St5Ma { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Smoothing length", GroupName = "8. SuperTrend 5", Order = 3)]
        public int St5MaLen { get; set; }

        [NinjaScriptProperty, Range(0.0, 100.0)]
        [Display(Name = "Weight", GroupName = "8. SuperTrend 5", Order = 4)]
        public double St5Weight { get; set; }
        #endregion
    }
}
