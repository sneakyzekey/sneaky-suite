#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SDX = SharpDX;
using D2D = SharpDX.Direct2D1;
#endregion

// SneakyRsiStrengthZones
// NinjaTrader 8 port of "RSI Strength & Consolidation Zones" by Zeiierman (TradingView, Pine v6).
// Original work (c) Zeiierman, licensed CC BY-NC-SA 4.0
//   https://creativecommons.org/licenses/by-nc-sa/4.0/
// This port is an adaptation and is therefore distributed under the same license:
// attribution to Zeiierman required, NON-COMMERCIAL use only, share-alike.
//
// What it does (same as the original)
//   * Enhanced RSI: the average of a simple-average RSI and Wilder's RSI, each smoothed by a one-pole
//     "Kalman" filter (Trend Confirmation = filter gain).
//   * Strength fill: the RSI is pushed away from 50 by (ADX - 20) / 3 when ADX > 20. The band between
//     the RSI and that pushed line is green when +DI > -DI, red otherwise (+DI/-DI/ADX are each the
//     average of a rolling-sum DMI and Wilder's DMI, then filtered).
//   * Faint white band between the 5-bar WMAs of the two RSI variants, shaded 30-70 background.
//   * Consolidation zones: while Wilder ADX <= 20 a box tracks the high/low of the quiet stretch on the
//     PRICE panel. After the box closes, the first close through its high (up arrow) or low (down arrow)
//     is the breakout signal; one signal per box.
//
// Notes
//   * Set Calculate to "On bar close" (default). The zone/breakout state machine is bar-by-bar.
//   * Series for strategies: BreakoutUp / BreakoutDown (1 on the signal bar), InZone (1 while a zone is
//     active), TrendUp (1 when +DI > -DI), EnhancedRsi, RsiStrength, AdxFiltered.
//   * Boxes and arrows are drawn on the price panel through the normal NinjaTrader drawing tools, so
//     the indicator can sit in its own sub-panel (add it with "New panel").
//   * One deliberate deviation from the Pine source: if a filtered input ever becomes undefined (e.g. a
//     completely flat market makes DI undefined) the filter holds its last value instead of going
//     undefined for the rest of the chart.

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakyRsiStrengthZones : Indicator
    {
        // ---------------------------------------------------------------- state
        private Series<double> upS, dnS, upR, dnR, rsiCK, rsiWK;
        private Series<double> trS, pdmS, mdmS, sTRs, sPDMs, sMDMs, pcS, mcS, dxS, adxC;
        private Series<double> pdS, mdS, adS, aK, pK, mK;
        private Series<double> rsiS, strS, w1S, w2S, trendS;
        private Series<double> brkUp, brkDn, zoneS;

        private bool   inZone, fired;
        private double zoneHigh, zoneLow, lastBoxHigh, lastBoxLow, prevLastHigh, prevLastLow;
        private int    zoneStart;

        private Brush boxOutline;
        private D2D.Brush dxUp, dxDn, dxBg, dxWhite;

        // ---------------------------------------------------------------- lifecycle
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "RSI strength fill + ADX consolidation boxes with breakout arrows (port of Zeiierman).";
                Name                     = "SneakyRsiStrengthZones";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = false;
                IsAutoScale              = true;
                DisplayInDataBox         = true;
                PaintPriceMarkers        = true;
                DrawOnPricePanel         = true;
                IsSuspendedWhileInactive = true;
                MaximumBarsLookBack      = MaximumBarsLookBack.Infinite;

                RsiLength      = 14;
                DmiLength      = 14;
                AdxSmoothing   = 14;
                FilterStrength = 0.10;

                RsiBrush       = Rgb(0x32, 0xD3, 0xFF);
                StrengthUpBrush = Rgb(0x00, 0xE6, 0x76);
                StrengthDnBrush = Rgb(0xF2, 0x36, 0x45);
                FillBrush      = Rgb(0x32, 0xD3, 0xFF);
                BoxBrush       = Rgb(0x29, 0x62, 0xFF);
                BreakUpBrush   = Rgb(0x5E, 0xFF, 0x32);
                BreakDnBrush   = Rgb(0xFF, 0x32, 0x32);

                ShowBoxes      = true;
                ShowBreakouts  = true;
                BoxOpacity     = 15;
                BreakoutAlerts = true;
                ArrowSize      = 22;
                ArrowDistance  = 22;

                AddPlot(new Stroke(RsiBrush, 1), PlotStyle.Line, "EnhancedRsi");
                AddPlot(new Stroke(FillBrush, 1), PlotStyle.Line, "UpperBand");
                AddPlot(new Stroke(FillBrush, 1), PlotStyle.Line, "LowerBand");
                AddPlot(new Stroke(FillBrush, DashStyleHelper.Dash, 1), PlotStyle.Line, "MidLine");

                // ---- automation plots (invisible, readable by strategies / third-party tools) ----
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "BuySignal");       // 4: +1 on the bar that closes above a finished box, else 0
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SellSignal");      // 5: -1 on the bar that closes below a finished box, else 0
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "InZone");          // 6: 1 while a consolidation zone is active, else 0
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "TrendDirection");  // 7: +1 when +DI > -DI, else -1
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "BoxHigh");         // 8: high of the active box, else of the last finished box, else 0
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "BoxLow");          // 9: low of the active box, else of the last finished box, else 0
            }
            else if (State == State.Configure)
            {
                Plots[0].Brush = RsiBrush;
                Plots[1].Brush = FillBrush;
                Plots[2].Brush = FillBrush;
                Brush mid = FillBrush.Clone();
                mid.Opacity = 0.5;
                mid.Freeze();
                Plots[3].Brush = mid;

                Brush ol = BoxBrush.Clone();
                ol.Opacity = Math.Min(1.0, 2.0 * BoxOpacity / 100.0);
                ol.Freeze();
                boxOutline = ol;
            }
            else if (State == State.DataLoaded)
            {
                upS = S(); dnS = S(); upR = S(); dnR = S(); rsiCK = S(); rsiWK = S();
                trS = S(); pdmS = S(); mdmS = S(); sTRs = S(); sPDMs = S(); sMDMs = S(); pcS = S(); mcS = S(); dxS = S(); adxC = S();
                pdS = S(); mdS = S(); adS = S(); aK = S(); pK = S(); mK = S();
                rsiS = S(); strS = S(); w1S = S(); w2S = S(); trendS = S();
                brkUp = S(); brkDn = S(); zoneS = S();

                inZone = false; fired = false;
                zoneHigh = zoneLow = double.NaN;
                lastBoxHigh = lastBoxLow = prevLastHigh = prevLastLow = double.NaN;
                zoneStart = -1;
            }
            else if (State == State.Terminated)
            {
                DisposeBrushes();
            }
        }

        private Series<double> S() { return new Series<double>(this, MaximumBarsLookBack.Infinite); }

        private static Brush Rgb(byte r, byte g, byte b)
        {
            SolidColorBrush br = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        // ---------------------------------------------------------------- Pine helpers
        // ta.rma: seeded with the SMA of the first `len` valid values (valid from bar `first`), then Wilder smoothing.
        private double RmaStep(Series<double> src, Series<double> dst, int len, int first)
        {
            int b = CurrentBar;
            if (b < first + len - 1) return double.NaN;
            if (b == first + len - 1)
            {
                double s = 0;
                for (int i = 0; i < len; i++) s += src[i];
                return double.IsNaN(s) ? double.NaN : s / len;
            }
            double prev = dst[1], x = src[0];
            if (double.IsNaN(prev) || double.IsNaN(x)) return double.NaN;
            return (x + (len - 1) * prev) / len;
        }

        // kf(): one-pole filter, seeded by the first valid input (see header note about holding on undefined input)
        private double Kf(Series<double> st, double src)
        {
            double prev = CurrentBar > 0 ? st[1] : double.NaN;
            if (double.IsNaN(src)) return prev;
            if (double.IsNaN(prev)) return src;
            return prev + FilterStrength * (src - prev);
        }

        private static double Avg(double a, double b)
        {
            return (double.IsNaN(a) || double.IsNaN(b)) ? double.NaN : (a + b) * 0.5;
        }

        private double Wma5(Series<double> s)
        {
            if (CurrentBar < 4) return double.NaN;
            double num = 0, den = 0;
            for (int i = 0; i < 5; i++)
            {
                double v = s[i];
                if (double.IsNaN(v)) return double.NaN;
                int w = 5 - i;
                num += v * w; den += w;
            }
            return num / den;
        }

        // ---------------------------------------------------------------- calculation
        protected override void OnBarUpdate()
        {
            int b = CurrentBar;
            int L = RsiLength, D = DmiLength;

            // ---- simple-average ("Cutler") RSI, undefined history counts as zero change like the Pine loop
            double g = 0, l = 0;
            for (int i = 1; i <= L; i++)
            {
                if (b - i < 0) continue;
                double ch = Close[i - 1] - Close[i];
                if (ch > 0) g += ch; else if (ch < 0) l -= ch;
            }
            double ag = g / L, al = l / L;
            double rsiC = al == 0 ? 100.0 - 100.0 / 101.0 : 100.0 - 100.0 / (1.0 + ag / al);   // rs = 100 when avgLoss == 0
            rsiCK[0] = Kf(rsiCK, rsiC);

            // ---- Wilder RSI (ta.rsi)
            if (b >= 1)
            {
                double chg = Close[0] - Close[1];
                upS[0] = Math.Max(chg, 0.0);
                dnS[0] = -Math.Min(chg, 0.0);
            }
            else { upS[0] = double.NaN; dnS[0] = double.NaN; }
            upR[0] = RmaStep(upS, upR, L, 1);
            dnR[0] = RmaStep(dnS, dnR, L, 1);
            double rsiW = double.NaN;
            if (!double.IsNaN(upR[0]) && !double.IsNaN(dnR[0]))
                rsiW = dnR[0] == 0 ? 100.0 : upR[0] == 0 ? 0.0 : 100.0 - 100.0 / (1.0 + upR[0] / dnR[0]);
            rsiWK[0] = Kf(rsiWK, rsiW);

            double rsi = Avg(rsiWK[0], rsiCK[0]);

            // ---- rolling-sum DMI (the original's custom DMI)
            double pD = double.NaN, mD = double.NaN, aD = double.NaN;
            if (b >= D)
            {
                double sTR = 0, sP = 0, sM = 0;
                for (int i = 1; i <= D; i++)
                {
                    double hc = High[i - 1], lc = Low[i - 1], pc = Close[i];
                    sTR += Math.Max(hc - lc, Math.Max(Math.Abs(hc - pc), Math.Abs(lc - pc)));
                    double up = High[i - 1] - High[i];
                    double dn = Low[i] - Low[i - 1];
                    if (up > dn && up > 0) sP += up;
                    if (dn > up && dn > 0) sM += dn;
                }
                if (sTR > 0)
                {
                    pD = 100.0 * sP / sTR;
                    mD = 100.0 * sM / sTR;
                    double sum = pD + mD;
                    aD = sum > 0 ? 100.0 * Math.Abs(pD - mD) / sum : double.NaN;
                }
            }
            pdS[0] = pD; mdS[0] = mD; adS[0] = aD;

            // ---- Wilder DMI / ADX (CoreDmi)
            if (b >= 1)
            {
                double up = High[0] - High[1], dn = Low[1] - Low[0];
                trS[0]  = Math.Max(High[0] - Low[0], Math.Max(Math.Abs(High[0] - Close[1]), Math.Abs(Low[0] - Close[1])));
                pdmS[0] = (up > dn && up > 0) ? up : 0.0;
                mdmS[0] = (dn > up && dn > 0) ? dn : 0.0;
            }
            else { trS[0] = double.NaN; pdmS[0] = double.NaN; mdmS[0] = double.NaN; }
            sTRs[0]  = RmaStep(trS, sTRs, D, 1);
            sPDMs[0] = RmaStep(pdmS, sPDMs, D, 1);
            sMDMs[0] = RmaStep(mdmS, sMDMs, D, 1);
            double pcv = double.NaN, mcv = double.NaN, dx = double.NaN;
            if (!double.IsNaN(sTRs[0]) && !double.IsNaN(sPDMs[0]) && !double.IsNaN(sMDMs[0]))
            {
                pcv = sTRs[0] == 0 ? 0.0 : 100.0 * sPDMs[0] / sTRs[0];
                mcv = sTRs[0] == 0 ? 0.0 : 100.0 * sMDMs[0] / sTRs[0];
                double ds = pcv + mcv;
                dx = ds == 0 ? 0.0 : 100.0 * Math.Abs(pcv - mcv) / ds;
            }
            pcS[0] = pcv; mcS[0] = mcv; dxS[0] = dx;
            adxC[0] = RmaStep(dxS, adxC, AdxSmoothing, D);

            // ---- filtered DI / ADX
            aK[0] = Kf(aK, Avg(aD, adxC[0]));
            pK[0] = Kf(pK, Avg(pD, pcv));
            mK[0] = Kf(mK, Avg(mD, mcv));
            double a = aK[0], p = pK[0], m = mK[0];

            // ---- strength line
            double str = (!double.IsNaN(a) && a > 20) ? (a - 20.0) / 3.0 : 0.0;
            double rsiStrength = double.IsNaN(rsi) ? double.NaN : (rsi > 50 ? rsi + str : rsi - str);

            rsiS[0]   = rsi;
            strS[0]   = rsiStrength;
            trendS[0] = (!double.IsNaN(p) && !double.IsNaN(m) && p > m) ? 1.0 : 0.0;
            w1S[0]    = Wma5(rsiWK);
            w2S[0]    = Wma5(rsiCK);

            Values[1][0] = 70;
            Values[2][0] = 30;
            Values[3][0] = 50;
            if (!double.IsNaN(rsi)) Values[0][0] = rsi;

            // ---- consolidation zones + breakouts (price panel)
            brkUp[0] = 0; brkDn[0] = 0;
            double ac = adxC[0];
            bool conditionMet = !double.IsNaN(ac) && ac <= 20;
            double hi = High[0], lo = Low[0], cl = Close[0];

            if (conditionMet)
            {
                if (!inZone)
                {
                    inZone = true;
                    zoneHigh = hi; zoneLow = lo; zoneStart = b;
                    fired = false;
                    lastBoxHigh = double.NaN; lastBoxLow = double.NaN;
                }
                else
                {
                    zoneHigh = Math.Max(zoneHigh, hi);
                    zoneLow  = Math.Min(zoneLow, lo);
                }
                if (ShowBoxes)
                    Draw.Rectangle(this, "RSCZ_box_" + zoneStart, false, b - zoneStart, zoneHigh, 0, zoneLow, boxOutline, BoxBrush, BoxOpacity);
            }
            else
            {
                if (inZone)
                {
                    inZone = false;
                    if (ShowBoxes && b - zoneStart >= 1)
                        Draw.Rectangle(this, "RSCZ_box_" + zoneStart, false, b - zoneStart, zoneHigh, 1, zoneLow, boxOutline, BoxBrush, BoxOpacity);
                    lastBoxHigh = zoneHigh;
                    lastBoxLow  = zoneLow;
                    zoneHigh = double.NaN; zoneLow = double.NaN; zoneStart = -1;
                }

                if (!double.IsNaN(lastBoxHigh) && !fired && b > 0)
                {
                    double c1 = Close[1];
                    // ta.crossover / ta.crossunder compare against the level as it was on the previous bar
                    if (!double.IsNaN(prevLastHigh) && cl > lastBoxHigh && c1 <= prevLastHigh)
                    {
                        fired = true;
                        brkUp[0] = 1;
                        if (ShowBreakouts) Draw.Text(this, "RSCZ_up_" + b, false, "\u25B2", 0, Low[0], ArrowDistance, BreakUpBrush, new SimpleFont("Arial", ArrowSize), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                        if (BreakoutAlerts && State == State.Realtime)
                            Alert("RSCZ_up_" + b, Priority.Medium, "RSI zone breakout UP " + Instrument.FullName, NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Black, Brushes.LimeGreen);
                    }
                    if (!double.IsNaN(prevLastLow) && cl < lastBoxLow && c1 >= prevLastLow)
                    {
                        fired = true;
                        brkDn[0] = 1;
                        if (ShowBreakouts) Draw.Text(this, "RSCZ_dn_" + b, false, "\u25BC", 0, High[0], -ArrowDistance, BreakDnBrush, new SimpleFont("Arial", ArrowSize), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                        if (BreakoutAlerts && State == State.Realtime)
                            Alert("RSCZ_dn_" + b, Priority.Medium, "RSI zone breakout DOWN " + Instrument.FullName, NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Black, Brushes.OrangeRed);
                    }
                }
            }
            zoneS[0] = inZone ? 1 : 0;
            Values[4][0] = brkUp[0] > 0.5 ? 1 : 0;
            Values[5][0] = brkDn[0] > 0.5 ? -1 : 0;
            Values[6][0] = zoneS[0];
            Values[7][0] = trendS[0] > 0.5 ? 1 : -1;
            double bh = inZone ? zoneHigh : lastBoxHigh;
            double bl = inZone ? zoneLow  : lastBoxLow;
            Values[8][0] = double.IsNaN(bh) ? 0 : bh;
            Values[9][0] = double.IsNaN(bl) ? 0 : bl;
            prevLastHigh = lastBoxHigh;
            prevLastLow  = lastBoxLow;
        }

        // keep the sub-panel scale driven by the RSI plots only (the invisible automation plots sit at -1/0/1)
        public override void OnCalculateMinMax()
        {
            MinValue = double.MaxValue;
            MaxValue = double.MinValue;
            if (ChartBars == null) { base.OnCalculateMinMax(); return; }
            for (int idx = ChartBars.FromIndex; idx <= ChartBars.ToIndex; idx++)
                for (int k = 0; k < 4; k++)
                {
                    if (!Values[k].IsValidDataPointAt(idx)) continue;
                    double v = Values[k].GetValueAt(idx);
                    if (double.IsNaN(v)) continue;
                    if (v < MinValue) MinValue = v;
                    if (v > MaxValue) MaxValue = v;
                }
            if (MinValue > MaxValue) { MinValue = 0; MaxValue = 100; }
        }

        // ---------------------------------------------------------------- render (fills under the plot lines)
        public override void OnRenderTargetChanged()
        {
            DisposeBrushes();
            if (RenderTarget == null) return;
            dxUp    = StrengthUpBrush.ToDxBrush(RenderTarget);
            dxDn    = StrengthDnBrush.ToDxBrush(RenderTarget);
            dxBg    = FillBrush.ToDxBrush(RenderTarget);
            dxBg.Opacity = 0.10f;
            dxWhite = Brushes.White.ToDxBrush(RenderTarget);
            dxWhite.Opacity = 0.20f;
        }

        private static void Kill(ref D2D.Brush b) { if (b != null) { b.Dispose(); b = null; } }
        private void DisposeBrushes() { Kill(ref dxUp); Kill(ref dxDn); Kill(ref dxBg); Kill(ref dxWhite); }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (Bars == null || ChartBars == null || RenderTarget == null || rsiS == null || dxUp == null)
            {
                base.OnRender(chartControl, chartScale);
                return;
            }
            try { DrawFills(chartControl, chartScale); }
            catch { }
            base.OnRender(chartControl, chartScale);
        }

        private void DrawFills(ChartControl cc, ChartScale cs)
        {
            // 30-70 background across the panel
            float y70 = cs.GetYByValue(70), y30 = cs.GetYByValue(30);
            RenderTarget.FillRectangle(new SDX.RectangleF(ChartPanel.X, y70, ChartPanel.W, y30 - y70), dxBg);

            int from = Math.Max(ChartBars.FromIndex, 1);
            int to   = Math.Min(ChartBars.ToIndex, Bars.Count - 1);
            if (to <= from) return;

            D2D.Brush[] dirBrush = new D2D.Brush[] { dxDn, dxUp };
            FillBand(cc, cs, from, to, strS, rsiS, i => trendS.GetValueAt(i) > 0.5 ? 1 : 0, dirBrush);
            FillBand(cc, cs, from, to, w1S, w2S, i => 0, new D2D.Brush[] { dxWhite });
        }

        private void FillBand(ChartControl cc, ChartScale cs, int from, int to, Series<double> A, Series<double> B,
                              Func<int, int> grp, D2D.Brush[] brushes)
        {
            int runStart = -1, runGrp = -1;
            for (int i = from; i <= to + 1; i++)
            {
                bool ok = i <= to && Valid(A, i) && Valid(B, i);
                int g = ok ? grp(i) : -1;
                if (runStart >= 0 && g != runGrp)
                {
                    EmitRun(cc, cs, runStart, i - 1, A, B, brushes[runGrp]);
                    runStart = -1;
                    if (ok && Valid(A, i - 1) && Valid(B, i - 1)) { runStart = i - 1; runGrp = g; continue; }
                }
                if (runStart < 0 && ok) { runStart = i; runGrp = g; }
            }
        }

        private static bool Valid(Series<double> s, int i)
        {
            if (i < 0 || !s.IsValidDataPointAt(i)) return false;
            double v = s.GetValueAt(i);
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private void EmitRun(ChartControl cc, ChartScale cs, int s, int e, Series<double> A, Series<double> B, D2D.Brush brush)
        {
            if (e <= s) return;
            using (D2D.PathGeometry geo = new D2D.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
            {
                D2D.GeometrySink sink = geo.Open();
                sink.BeginFigure(new SDX.Vector2(cc.GetXByBarIndex(ChartBars, s), cs.GetYByValue(A.GetValueAt(s))), D2D.FigureBegin.Filled);
                for (int k = s + 1; k <= e; k++)
                    sink.AddLine(new SDX.Vector2(cc.GetXByBarIndex(ChartBars, k), cs.GetYByValue(A.GetValueAt(k))));
                for (int k = e; k >= s; k--)
                    sink.AddLine(new SDX.Vector2(cc.GetXByBarIndex(ChartBars, k), cs.GetYByValue(B.GetValueAt(k))));
                sink.EndFigure(D2D.FigureEnd.Closed);
                sink.Close();
                sink.Dispose();
                RenderTarget.FillGeometry(geo, brush);
            }
        }

        // ---------------------------------------------------------------- properties
        #region Properties
        [Range(1, 200)]
        [Display(Name = "RSI length", GroupName = "1. RSI & DMI", Order = 0, Description = "Bars used by both RSI variants.")]
        public int RsiLength { get; set; }

        [Range(1, 200)]
        [Display(Name = "DMI length", GroupName = "1. RSI & DMI", Order = 1, Description = "Bars used by the +DI / -DI / ADX calculations. Zones use Wilder ADX <= 20.")]
        public int DmiLength { get; set; }

        [Range(1, 200)]
        [Display(Name = "ADX smoothing", GroupName = "1. RSI & DMI", Order = 2, Description = "Wilder smoothing length of the DX values that form ADX.")]
        public int AdxSmoothing { get; set; }

        [Range(0.01, 1.0)]
        [Display(Name = "Trend confirmation (filter gain)", GroupName = "2. Trend factor", Order = 0, Description = "Gain of the one-pole filter applied to RSI and DMI. Higher reacts faster.")]
        public double FilterStrength { get; set; }

        [XmlIgnore][Display(Name = "Enhanced RSI", GroupName = "3. Style", Order = 0)]
        public Brush RsiBrush { get; set; }
        [Browsable(false)] public string RsiBrushSerialize { get { return Serialize.BrushToString(RsiBrush); } set { RsiBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Bullish strength", GroupName = "3. Style", Order = 1)]
        public Brush StrengthUpBrush { get; set; }
        [Browsable(false)] public string StrengthUpBrushSerialize { get { return Serialize.BrushToString(StrengthUpBrush); } set { StrengthUpBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Bearish strength", GroupName = "3. Style", Order = 2)]
        public Brush StrengthDnBrush { get; set; }
        [Browsable(false)] public string StrengthDnBrushSerialize { get { return Serialize.BrushToString(StrengthDnBrush); } set { StrengthDnBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Bands / background", GroupName = "3. Style", Order = 3)]
        public Brush FillBrush { get; set; }
        [Browsable(false)] public string FillBrushSerialize { get { return Serialize.BrushToString(FillBrush); } set { FillBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Consolidation box", GroupName = "3. Style", Order = 4)]
        public Brush BoxBrush { get; set; }
        [Browsable(false)] public string BoxBrushSerialize { get { return Serialize.BrushToString(BoxBrush); } set { BoxBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Breakout up", GroupName = "3. Style", Order = 5)]
        public Brush BreakUpBrush { get; set; }
        [Browsable(false)] public string BreakUpBrushSerialize { get { return Serialize.BrushToString(BreakUpBrush); } set { BreakUpBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Breakout down", GroupName = "3. Style", Order = 6)]
        public Brush BreakDnBrush { get; set; }
        [Browsable(false)] public string BreakDnBrushSerialize { get { return Serialize.BrushToString(BreakDnBrush); } set { BreakDnBrush = Serialize.StringToBrush(value); } }

        [Range(1, 100)]
        [Display(Name = "Box opacity (%)", GroupName = "3. Style", Order = 7, Description = "Fill opacity of the consolidation boxes.")]
        public int BoxOpacity { get; set; }

        [Display(Name = "Show consolidation boxes", GroupName = "4. Zones", Order = 0)]
        public bool ShowBoxes { get; set; }

        [Display(Name = "Show breakout arrows", GroupName = "4. Zones", Order = 1)]
        public bool ShowBreakouts { get; set; }

        [Display(Name = "Breakout alerts", GroupName = "4. Zones", Order = 2)]
        public bool BreakoutAlerts { get; set; }

        [Range(8, 60)]
        [Display(Name = "Arrow size (pt)", GroupName = "4. Zones", Order = 3, Description = "Font size of the breakout arrow.")]
        public int ArrowSize { get; set; }

        [Range(0, 120)]
        [Display(Name = "Arrow distance from bar (px)", GroupName = "4. Zones", Order = 4, Description = "Pixels between the breakout bar's high/low and the arrow.")]
        public int ArrowDistance { get; set; }

        [Browsable(false)][XmlIgnore] public Series<double> EnhancedRsi  { get { return Values[0]; } }
        [Browsable(false)][XmlIgnore] public Series<double> RsiStrength  { get { return strS; } }
        [Browsable(false)][XmlIgnore] public Series<double> AdxFiltered  { get { return aK; } }
        [Browsable(false)][XmlIgnore] public Series<double> TrendUp      { get { return trendS; } }
        [Browsable(false)][XmlIgnore] public Series<double> BuySignal    { get { return Values[4]; } }
        [Browsable(false)][XmlIgnore] public Series<double> SellSignal   { get { return Values[5]; } }
        [Browsable(false)][XmlIgnore] public Series<double> ZoneActive   { get { return Values[6]; } }
        [Browsable(false)][XmlIgnore] public Series<double> TrendDirection { get { return Values[7]; } }
        [Browsable(false)][XmlIgnore] public Series<double> BoxHigh      { get { return Values[8]; } }
        [Browsable(false)][XmlIgnore] public Series<double> BoxLow       { get { return Values[9]; } }
        [Browsable(false)][XmlIgnore] public Series<double> BreakoutUp   { get { return brkUp; } }
        [Browsable(false)][XmlIgnore] public Series<double> BreakoutDown { get { return brkDn; } }
        [Browsable(false)][XmlIgnore] public Series<double> InZone       { get { return zoneS; } }
        #endregion
    }
}
