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
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using DxBrush = SharpDX.Direct2D1.Brush;
using Vec2    = SharpDX.Vector2;
using RectF   = SharpDX.RectangleF;
#endregion

// ─────────────────────────────────────────────────────────────────────────────
//  Wix9ine Ranges — NT8 port of the "Wix9ine Ranges" Pine v6 script
//  (strategy: https://www.youtube.com/watch?v=madtVgPOtgU)
//
//  • Range      = a completed candle on the "story" timeframe (default 60 min).
//                 R1 is the most recent, R2 the one before it, and so on.
//  • Closure    = a close outside a range. Story-TF close of R1 vs R2 sets bias.
//  • Failed     = poke through a range edge and close back inside (wick), or
//    breakout     close outside for only a few bars then back in (trap).
//  • Entry      = Rejection: signal on the failed-breakout bar.
//                 Retest: wait for price to retest the rejected level and hold.
//  • HTF wick   = longs only while price is at/below the story-candle open
//    filter       (shorts at/above).
//
//  Signals evaluate on bar close (same as Pine's barstate.isconfirmed), so the
//  indicator always runs Calculate.OnBarClose.
//  Public series for strategies: Signal (1 long / -1 short / 0), Bias (1/-1/0).
// ─────────────────────────────────────────────────────────────────────────────

public enum SneakyWix9ineEntryMode  { Rejection, Retest }
public enum SneakyWix9ineBiasFilter { Off, NotAgainst, MustAlign }

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakyWix9ineRanges : Indicator
    {
        #region Internals
        private class RangeInfo
        {
            public double Hi, Lo, BTop, BBot, Cl;
            public int    Start;
        }

        private const int F_LONG = 1, F_SHORT = 2, F_FAILDN = 4, F_FAILUP = 8,
                          F_CLOSEUP = 16, F_CLOSEDN = 32, F_HTFUP = 64, F_HTFDN = 128;

        private readonly List<RangeInfo> ranges = new List<RangeInfo>();   // [0] = R1
        private readonly object          sync   = new object();

        private Series<int> flags;
        private Series<int> biasSeries;
        private Series<int> signal;
        private ATR         atr;

        private bool   pendingRange;
        private double pH, pL, pO, pC;
        private int    curStart = -1;
        private double htfOpen  = double.NaN;
        private int    closAbove, closBelow;
        private double pendL = double.NaN, pendS = double.NaN;
        private int    pendLBar, pendSBar;
        private int    lastLongBar = -100000, lastShortBar = -100000;
        private int    bias;
        private int    lastBar = -1;
        private bool   tfError;

        // SharpDX resources
        private bool    dxReady;
        private DxBrush dxRes, dxSup, dxResFade, dxSupFade, dxUpW, dxDnW, dxFull,
                        dxTintUp, dxTintDn, dxText, dxPanel, dxBorder, dxResFail, dxSupFail, dxWhite;
        private SharpDX.Direct2D1.StrokeStyle   dashStyle;
        private SharpDX.DirectWrite.TextFormat  fmtSmall, fmtTiny;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Story-timeframe ranges (R1..Rn) with wick zones, closures and failed-breakout / retest entries. Port of Wix9ine Ranges (Pine).";
                Name                     = "SneakyWix9ineRanges";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                DisplayInDataBox         = true;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsAutoScale              = false;   // exposed plots hold 1/-1/0 values; keep them off the price scale
                IsSuspendedWhileInactive = true;
                BarsRequiredToPlot       = 2;

                StoryMinutes   = 60;
                NumRanges      = 3;
                ShowWicks      = true;
                ShowFull       = false;
                ShowOpen       = true;
                ExtendBars     = 15;

                EntryMode      = SneakyWix9ineEntryMode.Retest;
                BiasFilter     = SneakyWix9ineBiasFilter.NotAgainst;
                WickFilter     = true;
                FailBars       = 3;
                RetestBars     = 12;
                TolATR         = 0.25;
                Cooldown       = 3;

                ShowClosures   = true;
                ShowHtfClose   = true;
                ShowFails      = true;
                ShowBiasBg     = false;
                ShowTable      = true;

                ResColor       = Hex("#EF5350");
                SupColor       = Hex("#26A69A");
                FullColor      = Hex("#787B86");
                WickOpacity    = 15;
                FullOpacity    = 7;
                FadedOpacity   = 45;
                TintOpacity    = 7;

                EnableAlerts     = false;
                AlertEntries     = true;
                AlertStoryClose  = true;
                AlertFails       = false;
                EntrySound       = "Alert2.wav";
                InfoSound        = "Alert1.wav";

                AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Solid, 1), PlotStyle.Line, "StoryOpen");
                // Automation plots (invisible on chart; readable by strategies / 3rd-party tools). All use 1 / -1 / 0.
                AddPlot(new Stroke(Brushes.Transparent, DashStyleHelper.Solid, 1), PlotStyle.Line, "Signal");          // Values[1]: 1 long entry, -1 short entry, 0 none
                AddPlot(new Stroke(Brushes.Transparent, DashStyleHelper.Solid, 1), PlotStyle.Line, "Bias");            // Values[2]: story-TF bias 1 / -1 / 0
                AddPlot(new Stroke(Brushes.Transparent, DashStyleHelper.Solid, 1), PlotStyle.Line, "FailedBreakout");  // Values[3]: 1 failed breakdown (bullish), -1 failed breakout (bearish)
                AddPlot(new Stroke(Brushes.Transparent, DashStyleHelper.Solid, 1), PlotStyle.Line, "CloseOutsideR1");  // Values[4]: 1 closed above R1 high, -1 closed below R1 low
                AddPlot(new Stroke(Brushes.Transparent, DashStyleHelper.Solid, 1), PlotStyle.Line, "StoryClose");      // Values[5]: 1 story candle closed above prior range, -1 below
            }
            else if (State == State.Configure)
            {
                Calculate = Calculate.OnBarClose;
                AddDataSeries(BarsPeriodType.Minute, StoryMinutes);
            }
            else if (State == State.DataLoaded)
            {
                flags      = new Series<int>(this, MaximumBarsLookBack.Infinite);
                biasSeries = new Series<int>(this, MaximumBarsLookBack.Infinite);
                signal     = new Series<int>(this, MaximumBarsLookBack.Infinite);
                atr        = ATR(BarsArray[0], 14);

                BarsPeriodType t = BarsPeriod.BarsPeriodType;
                tfError = (t == BarsPeriodType.Minute && BarsPeriod.Value >= StoryMinutes)
                       || (t == BarsPeriodType.Second && BarsPeriod.Value >= StoryMinutes * 60)
                       || t == BarsPeriodType.Day || t == BarsPeriodType.Week
                       || t == BarsPeriodType.Month || t == BarsPeriodType.Year;
            }
            else if (State == State.Terminated)
            {
                DisposeDx();
            }
        }

        #region Bar logic
        protected override void OnBarUpdate()
        {
            if (tfError)
            {
                if (BarsInProgress == 0 && CurrentBar == 0)
                    Draw.TextFixed(this, "wix9ineErr", "SneakyWix9ineRanges: chart timeframe must be lower than the story timeframe (" + StoryMinutes + " min).", TextPosition.Center);
                return;
            }

            // Story-TF candle completed → queue it; the next primary bar builds the range
            if (BarsInProgress == 1)
            {
                if (CurrentBars[1] < 0) return;
                pH = Highs[1][0]; pL = Lows[1][0]; pO = Opens[1][0]; pC = Closes[1][0];
                pendingRange = true;
                return;
            }
            if (BarsInProgress != 0) return;

            flags[0] = 0; biasSeries[0] = 0; signal[0] = 0;
            for (int pi = 1; pi <= 5; pi++) Values[pi][0] = 0;
            lastBar = CurrentBar;
            if (CurrentBar < 1) return;

            // ── Build a new range each time a story candle completes ──
            bool newHTF = false;
            if (pendingRange)
            {
                pendingRange = false;
                newHTF       = true;
                int s = curStart < 0 ? CurrentBar - 1 : curStart;
                s = Math.Max(0, Math.Max(s, CurrentBar - 9000));
                curStart  = CurrentBar;
                htfOpen   = Open[0];
                closAbove = 0;
                closBelow = 0;

                var r = new RangeInfo { Hi = pH, Lo = pL, BTop = Math.Max(pO, pC), BBot = Math.Min(pO, pC), Cl = pC, Start = s };
                lock (sync)
                {
                    ranges.Insert(0, r);
                    while (ranges.Count > NumRanges) ranges.RemoveAt(ranges.Count - 1);
                }
            }

            if (ShowOpen && !newHTF && !double.IsNaN(htfOpen))
                StoryOpen[0] = htfOpen;

            // ── Story-TF bias: close of R1 relative to R2 ──
            bias = 0;
            if (ranges.Count >= 2)
            {
                RangeInfo r1 = ranges[0], r2 = ranges[1];
                bias = r1.Cl > r2.Hi ? 1 : r1.Cl < r2.Lo ? -1 : 0;
            }
            biasSeries[0] = bias;
            bool htfUp = newHTF && bias == 1;
            bool htfDn = newHTF && bias == -1;

            // ── Chart-TF closures outside R1 ──
            double r1hi = ranges.Count > 0 ? ranges[0].Hi : double.NaN;
            double r1lo = ranges.Count > 0 ? ranges[0].Lo : double.NaN;
            bool closeUp = !double.IsNaN(r1hi) && Close[0] > r1hi && Close[1] <= r1hi;
            bool closeDn = !double.IsNaN(r1lo) && Close[0] < r1lo && Close[1] >= r1lo;
            if (!double.IsNaN(r1hi) && Close[0] > r1hi) closAbove++;
            if (!double.IsNaN(r1lo) && Close[0] < r1lo) closBelow++;

            // ── Failed-breakout detection ──
            double failUpLvl = double.NaN;   // resistance rejected (bearish)
            double failDnLvl = double.NaN;   // support rejected (bullish)
            foreach (RangeInfo rg in ranges)
            {
                if (double.IsNaN(failUpLvl))
                {
                    if (FailUp(rg.Hi, FailBars))      failUpLvl = rg.Hi;
                    else if (FailUp(rg.Lo, FailBars)) failUpLvl = rg.Lo;
                }
                if (double.IsNaN(failDnLvl))
                {
                    if (FailDn(rg.Lo, FailBars))      failDnLvl = rg.Lo;
                    else if (FailDn(rg.Hi, FailBars)) failDnLvl = rg.Hi;
                }
            }

            // ── Entry logic ──
            double tol = atr[0] * TolATR;
            bool trigLong = false, trigShort = false;

            if (EntryMode == SneakyWix9ineEntryMode.Rejection)
            {
                trigLong  = !double.IsNaN(failDnLvl);
                trigShort = !double.IsNaN(failUpLvl);
            }
            else
            {
                // Expire / invalidate armed setups (price accepted through the level)
                if (!double.IsNaN(pendL) && (CurrentBar - pendLBar > RetestBars || Close[0] < pendL - tol)) pendL = double.NaN;
                if (!double.IsNaN(pendS) && (CurrentBar - pendSBar > RetestBars || Close[0] > pendS + tol)) pendS = double.NaN;

                // Retest of the rejected level that holds
                if (!double.IsNaN(pendL) && CurrentBar > pendLBar && Low[0] <= pendL + tol && Close[0] > pendL && Close[0] > Open[0])
                { trigLong = true; pendL = double.NaN; }
                if (!double.IsNaN(pendS) && CurrentBar > pendSBar && High[0] >= pendS - tol && Close[0] < pendS && Close[0] < Open[0])
                { trigShort = true; pendS = double.NaN; }

                // Arm new setups from this bar's failed breakouts
                if (!double.IsNaN(failDnLvl)) { pendL = failDnLvl; pendLBar = CurrentBar; }
                if (!double.IsNaN(failUpLvl)) { pendS = failUpLvl; pendSBar = CurrentBar; }
            }

            bool biasOkLong  = BiasFilter == SneakyWix9ineBiasFilter.Off || (BiasFilter == SneakyWix9ineBiasFilter.NotAgainst ? bias >= 0 : bias == 1);
            bool biasOkShort = BiasFilter == SneakyWix9ineBiasFilter.Off || (BiasFilter == SneakyWix9ineBiasFilter.NotAgainst ? bias <= 0 : bias == -1);
            bool wickOkLong  = !WickFilter || (!double.IsNaN(htfOpen) && Low[0]  <= htfOpen + tol);
            bool wickOkShort = !WickFilter || (!double.IsNaN(htfOpen) && High[0] >= htfOpen - tol);

            bool longSig  = trigLong  && biasOkLong  && wickOkLong  && CurrentBar - lastLongBar  > Cooldown;
            bool shortSig = trigShort && biasOkShort && wickOkShort && CurrentBar - lastShortBar > Cooldown;
            if (longSig)  lastLongBar  = CurrentBar;
            if (shortSig) lastShortBar = CurrentBar;

            // ── Record for rendering / strategies ──
            int f = 0;
            if (longSig)                   f |= F_LONG;
            if (shortSig)                  f |= F_SHORT;
            if (!double.IsNaN(failDnLvl))  f |= F_FAILDN;
            if (!double.IsNaN(failUpLvl))  f |= F_FAILUP;
            if (closeUp)                   f |= F_CLOSEUP;
            if (closeDn)                   f |= F_CLOSEDN;
            if (htfUp)                     f |= F_HTFUP;
            if (htfDn)                     f |= F_HTFDN;
            flags[0]  = f;
            signal[0] = longSig ? 1 : shortSig ? -1 : 0;
            Values[1][0] = signal[0];
            Values[2][0] = bias;
            Values[3][0] = (!double.IsNaN(failDnLvl) && double.IsNaN(failUpLvl)) ? 1 : (!double.IsNaN(failUpLvl) && double.IsNaN(failDnLvl)) ? -1 : 0;
            Values[4][0] = closeUp ? 1 : closeDn ? -1 : 0;
            Values[5][0] = htfUp ? 1 : htfDn ? -1 : 0;

            // ── Alerts ──
            string sym = Instrument.MasterInstrument.Name;
            if (AlertEntries && longSig)   Fire("long",  "RFB long on "  + sym + " at " + Close[0].ToString(), EntrySound);
            if (AlertEntries && shortSig)  Fire("short", "RFB short on " + sym + " at " + Close[0].ToString(), EntrySound);
            if (AlertStoryClose && htfUp)  Fire("htfup", sym + ": story candle closed above the prior range", InfoSound);
            if (AlertStoryClose && htfDn)  Fire("htfdn", sym + ": story candle closed below the prior range", InfoSound);
            if (AlertFails && !double.IsNaN(failDnLvl)) Fire("faildn", sym + ": failed breakdown, support rejected at " + failDnLvl.ToString(), InfoSound);
            if (AlertFails && !double.IsNaN(failUpLvl)) Fire("failup", sym + ": failed breakout, resistance rejected at " + failUpLvl.ToString(), InfoSound);
        }

        // Consecutive closes outside level L ending on the previous bar
        private int Consec(double L, bool above, int maxN)
        {
            int m = 0;
            for (int j = 1; j <= maxN + 1 && j <= CurrentBar; j++)
            {
                bool outside = above ? Close[j] > L : Close[j] < L;
                if (outside) m++; else break;
            }
            return m;
        }

        // Bearish: poked above L and closed back below it
        private bool FailUp(double L, int maxN)
        {
            bool wick = Close[1] <= L && High[0] > L && Close[0] < L;
            int  m    = Consec(L, true, maxN);
            bool trap = Close[0] < L && m >= 1 && m <= maxN;
            return wick || trap;
        }

        // Bullish: poked below L and closed back above it
        private bool FailDn(double L, int maxN)
        {
            bool wick = Close[1] >= L && Low[0] < L && Close[0] > L;
            int  m    = Consec(L, false, maxN);
            bool trap = Close[0] > L && m >= 1 && m <= maxN;
            return wick || trap;
        }

        private void Fire(string id, string msg, string sound)
        {
            if (!EnableAlerts || State != State.Realtime) return;
            string path = string.IsNullOrWhiteSpace(sound) ? string.Empty : NinjaTrader.Core.Globals.InstallDir + @"sounds\" + sound;
            Alert("Wix9_" + id, Priority.High, msg, path, 0, Brushes.Black, Brushes.White);
        }
        #endregion

        #region Rendering
        public override void OnRenderTargetChanged()
        {
            DisposeDx();
        }

        private DxBrush Mk(Brush b, float opacity)
        {
            DxBrush d = b.ToDxBrush(RenderTarget);
            d.Opacity = opacity;
            return d;
        }

        private void EnsureDx(ChartControl cc)
        {
            if (dxReady) return;
            dxRes      = Mk(ResColor, 1f);
            dxSup      = Mk(SupColor, 1f);
            dxResFade  = Mk(ResColor, FadedOpacity / 100f);
            dxSupFade  = Mk(SupColor, FadedOpacity / 100f);
            dxResFail  = Mk(ResColor, 0.6f);
            dxSupFail  = Mk(SupColor, 0.6f);
            dxUpW      = Mk(ResColor, WickOpacity / 100f);
            dxDnW      = Mk(SupColor, WickOpacity / 100f);
            dxFull     = Mk(FullColor, FullOpacity / 100f);
            dxTintUp   = Mk(SupColor, TintOpacity / 100f);
            dxTintDn   = Mk(ResColor, TintOpacity / 100f);
            dxText     = Mk(cc.Properties.ChartText, 1f);
            dxPanel    = Mk(cc.Properties.ChartBackground, 0.85f);
            dxBorder   = Mk(Brushes.Gray, 0.3f);
            dxWhite    = Mk(Brushes.White, 1f);

            dashStyle = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory,
                new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash });
            fmtSmall = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", 11f);
            fmtTiny  = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", SharpDX.DirectWrite.FontWeight.Bold,
                SharpDX.DirectWrite.FontStyle.Normal, 9f);
            dxReady = true;
        }

        private void DisposeDx()
        {
            DxBrush[] all = { dxRes, dxSup, dxResFade, dxSupFade, dxResFail, dxSupFail, dxUpW, dxDnW, dxFull, dxTintUp, dxTintDn, dxText, dxPanel, dxBorder, dxWhite };
            foreach (DxBrush b in all) if (b != null) b.Dispose();
            dxRes = dxSup = dxResFade = dxSupFade = dxResFail = dxSupFail = dxUpW = dxDnW = dxFull = dxTintUp = dxTintDn = dxText = dxPanel = dxBorder = dxWhite = null;
            if (dashStyle != null) { dashStyle.Dispose(); dashStyle = null; }
            if (fmtSmall  != null) { fmtSmall.Dispose();  fmtSmall  = null; }
            if (fmtTiny   != null) { fmtTiny.Dispose();   fmtTiny   = null; }
            dxReady = false;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (tfError || ChartBars == null || RenderTarget == null || flags == null || lastBar < 0)
            {
                base.OnRender(chartControl, chartScale);
                return;
            }
            EnsureDx(chartControl);

            var   rt       = RenderTarget;
            var   oldAA    = rt.AntialiasMode;
            float barW     = (float)chartControl.Properties.BarDistance;
            int   first    = Math.Max(0, ChartBars.FromIndex);
            int   last     = Math.Min(Math.Min(ChartBars.ToIndex, lastBar), Math.Min(flags.Count, BarsArray[0].Count) - 1);
            float panelTop = ChartPanel.Y, panelBot = ChartPanel.Y + ChartPanel.H;

            // ── Bias tint (behind everything) ──
            if (ShowBiasBg)
            {
                rt.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;
                for (int i = first; i <= last; i++)
                {
                    int b = biasSeries.GetValueAt(i);
                    if (b == 0) continue;
                    float x = chartControl.GetXByBarIndex(ChartBars, i);
                    rt.FillRectangle(new RectF(x - barW / 2f, panelTop, barW, panelBot - panelTop), b == 1 ? dxTintUp : dxTintDn);
                }
            }

            // ── Ranges ──
            RangeInfo[] snap;
            lock (sync) snap = ranges.ToArray();

            float rightX = chartControl.GetXByBarIndex(ChartBars, lastBar) + ExtendBars * barW;
            rt.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;
            for (int idx = snap.Length - 1; idx >= 0; idx--)
            {
                RangeInfo r = snap[idx];
                float x1 = chartControl.GetXByBarIndex(ChartBars, r.Start);
                if (rightX < ChartPanel.X || x1 > ChartPanel.X + ChartPanel.W) continue;

                float yHi = chartScale.GetYByValue(r.Hi),   yLo = chartScale.GetYByValue(r.Lo);
                float yBT = chartScale.GetYByValue(r.BTop), yBB = chartScale.GetYByValue(r.BBot);

                if (ShowFull)  rt.FillRectangle(new RectF(x1, yHi, rightX - x1, yLo - yHi), dxFull);
                if (ShowWicks)
                {
                    rt.FillRectangle(new RectF(x1, yHi, rightX - x1, yBT - yHi), dxUpW);
                    rt.FillRectangle(new RectF(x1, yBB, rightX - x1, yLo - yBB), dxDnW);
                }

                bool latest = idx == 0;
                if (latest)
                {
                    rt.DrawLine(new Vec2(x1, yHi), new Vec2(rightX, yHi), dxRes, 2f);
                    rt.DrawLine(new Vec2(x1, yLo), new Vec2(rightX, yLo), dxSup, 2f);
                }
                else
                {
                    rt.DrawLine(new Vec2(x1, yHi), new Vec2(rightX, yHi), dxResFade, 1f, dashStyle);
                    rt.DrawLine(new Vec2(x1, yLo), new Vec2(rightX, yLo), dxSupFade, 1f, dashStyle);
                }
                DrawText("R" + (idx + 1), rightX + 4f, (yHi + yLo) / 2f, dxText, fmtSmall, -1, true);
            }

            // ── Plots (story-candle open) ──
            base.OnRender(chartControl, chartScale);

            // ── Markers ──
            rt.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
            float sz = Math.Max(4f, Math.Min(8f, barW * 0.45f));
            for (int i = first; i <= last; i++)
            {
                int f = flags.GetValueAt(i);
                if (f == 0) continue;
                float x = chartControl.GetXByBarIndex(ChartBars, i);
                float yBelow = chartScale.GetYByValue(BarsArray[0].GetLow(i))  + 4f;
                float yAbove = chartScale.GetYByValue(BarsArray[0].GetHigh(i)) - 4f;

                if ((f & F_LONG) != 0)
                {
                    FillTri(new Vec2(x, yBelow), new Vec2(x - sz, yBelow + sz * 1.6f), new Vec2(x + sz, yBelow + sz * 1.6f), dxSup);
                    yBelow += sz * 1.6f + 1f;
                    DrawText("L", x, yBelow, dxSup, fmtTiny, 0, false);
                    yBelow += 12f;
                }
                if ((f & F_SHORT) != 0)
                {
                    FillTri(new Vec2(x, yAbove), new Vec2(x - sz, yAbove - sz * 1.6f), new Vec2(x + sz, yAbove - sz * 1.6f), dxRes);
                    yAbove -= sz * 1.6f + 13f;
                    DrawText("S", x, yAbove, dxRes, fmtTiny, 0, false);
                    yAbove -= 2f;
                }
                if (ShowFails && EntryMode == SneakyWix9ineEntryMode.Retest)
                {
                    float c = Math.Max(2.5f, sz * 0.6f);
                    if ((f & F_FAILDN) != 0)
                    {
                        float cy = yBelow + c + 1f;
                        rt.DrawLine(new Vec2(x - c, cy - c), new Vec2(x + c, cy + c), dxSupFail, 1.5f);
                        rt.DrawLine(new Vec2(x - c, cy + c), new Vec2(x + c, cy - c), dxSupFail, 1.5f);
                    }
                    if ((f & F_FAILUP) != 0)
                    {
                        float cy = yAbove - c - 1f;
                        rt.DrawLine(new Vec2(x - c, cy - c), new Vec2(x + c, cy + c), dxResFail, 1.5f);
                        rt.DrawLine(new Vec2(x - c, cy + c), new Vec2(x + c, cy - c), dxResFail, 1.5f);
                    }
                }
                if (ShowClosures && (f & (F_CLOSEUP | F_CLOSEDN)) != 0)
                {
                    float yc = chartScale.GetYByValue(BarsArray[0].GetClose(i));
                    float rr = Math.Max(2f, sz * 0.45f);
                    rt.FillEllipse(new SharpDX.Direct2D1.Ellipse(new Vec2(x, yc), rr, rr), (f & F_CLOSEUP) != 0 ? dxSup : dxRes);
                }
                if (ShowHtfClose && (f & F_HTFUP) != 0) DrawTag("H\u25B2", x, panelBot - 16f, dxSup);
                if (ShowHtfClose && (f & F_HTFDN) != 0) DrawTag("H\u25BC", x, panelTop + 2f,  dxRes);
            }

            // ── Dashboard ──
            if (ShowTable) DrawDashboard();

            rt.AntialiasMode = oldAA;
        }

        private void FillTri(Vec2 a, Vec2 b, Vec2 c, DxBrush br)
        {
            using (var g = new SharpDX.Direct2D1.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
            {
                using (var s = g.Open())
                {
                    s.BeginFigure(a, SharpDX.Direct2D1.FigureBegin.Filled);
                    s.AddLine(b);
                    s.AddLine(c);
                    s.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                    s.Close();
                }
                RenderTarget.FillGeometry(g, br);
            }
        }

        // align: -1 left, 0 center, 1 right. vCenter: y is the vertical middle, otherwise y is the top.
        private float DrawText(string s, float x, float y, DxBrush br, SharpDX.DirectWrite.TextFormat fmt, int align, bool vCenter)
        {
            using (var tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, s, fmt, 2000f, 200f))
            {
                float w = tl.Metrics.Width, h = tl.Metrics.Height;
                float px = align < 0 ? x : align == 0 ? x - w / 2f : x - w;
                float py = vCenter ? y - h / 2f : y;
                RenderTarget.DrawTextLayout(new Vec2(px, py), tl, br);
                return w;
            }
        }

        private float Measure(string s, SharpDX.DirectWrite.TextFormat fmt)
        {
            using (var tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, s, fmt, 2000f, 200f))
                return tl.Metrics.Width;
        }

        private void DrawTag(string s, float x, float y, DxBrush bg)
        {
            float w = Measure(s, fmtTiny) + 6f;
            RenderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = new RectF(x - w / 2f, y, w, 14f), RadiusX = 2f, RadiusY = 2f }, bg);
            DrawText(s, x, y + 7f, dxWhite, fmtTiny, 0, true);
        }

        private void DrawDashboard()
        {
            string bTxt = bias == 1 ? "Bullish closure" : bias == -1 ? "Bearish closure" : "Inside / neutral";
            DxBrush bCol = bias == 1 ? dxSup : bias == -1 ? dxRes : dxText;

            string armed;
            if (EntryMode == SneakyWix9ineEntryMode.Rejection) armed = "\u2014";
            else
            {
                double l = pendL, s = pendS;
                armed = (double.IsNaN(l) ? "" : "Long @ " + Instrument.MasterInstrument.FormatPrice(l))
                      + (!double.IsNaN(l) && !double.IsNaN(s) ? "  |  " : "")
                      + (double.IsNaN(s) ? "" : "Short @ " + Instrument.MasterInstrument.FormatPrice(s));
                if (armed == "") armed = "none";
            }

            string[]  keys = { "Story TF", "Bias", "Closes above / below R1", "Armed retest" };
            string[]  vals = { StoryMinutes + " min", bTxt, closAbove + " / " + closBelow, armed };
            DxBrush[] vcol = { dxText, bCol, dxText, dxText };

            float kw = 0, vw = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                kw = Math.Max(kw, Measure(keys[i], fmtSmall));
                vw = Math.Max(vw, Measure(vals[i], fmtSmall));
            }
            float pad = 6f, rowH = 18f;
            float w = kw + vw + pad * 4f, h = rowH * keys.Length + pad;
            float x0 = ChartPanel.X + ChartPanel.W - w - 8f, y0 = ChartPanel.Y + 8f;

            RenderTarget.FillRectangle(new RectF(x0, y0, w, h), dxPanel);
            RenderTarget.DrawRectangle(new RectF(x0, y0, w, h), dxBorder, 1f);
            for (int i = 0; i < keys.Length; i++)
            {
                float cy = y0 + pad / 2f + rowH * i + rowH / 2f;
                DrawText(keys[i], x0 + pad, cy, dxText, fmtSmall, -1, true);
                DrawText(vals[i], x0 + w - pad, cy, vcol[i], fmtSmall, 1, true);
            }
        }
        #endregion

        private static Brush Hex(string h)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFrom(h);
            b.Freeze();
            return b;
        }

        #region Public series
        [Browsable(false)][XmlIgnore]
        public Series<double> StoryOpen { get { return Values[0]; } }

        [Browsable(false)][XmlIgnore]
        public Series<int> Signal { get { Update(); return signal; } }

        [Browsable(false)][XmlIgnore]
        public Series<int> Bias { get { Update(); return biasSeries; } }
        #endregion

        #region Properties
        // 1. Ranges
        [NinjaScriptProperty][Range(1, int.MaxValue)]
        [Display(Name = "Story timeframe (minutes)", Description = "Each completed candle on this timeframe becomes a range. Must be higher than the chart timeframe.", GroupName = "1. Ranges", Order = 0)]
        public int StoryMinutes { get; set; }

        [NinjaScriptProperty][Range(2, 10)]
        [Display(Name = "Ranges to track (R1, R2, R3…)", GroupName = "1. Ranges", Order = 1)]
        public int NumRanges { get; set; }

        [Display(Name = "Shade wick zones", GroupName = "1. Ranges", Order = 2)]
        public bool ShowWicks { get; set; }

        [Display(Name = "Shade full range", GroupName = "1. Ranges", Order = 3)]
        public bool ShowFull { get; set; }

        [Display(Name = "Show current story-candle open", GroupName = "1. Ranges", Order = 4)]
        public bool ShowOpen { get; set; }

        [Range(0, 400)]
        [Display(Name = "Extend levels right (bars)", GroupName = "1. Ranges", Order = 5)]
        public int ExtendBars { get; set; }

        // 2. Signals
        [NinjaScriptProperty]
        [Display(Name = "Entry mode", Description = "Rejection: signal on the failed-breakout bar. Retest: signal when price comes back to test the rejected level and holds it.", GroupName = "2. Signals", Order = 0)]
        public SneakyWix9ineEntryMode EntryMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Story-TF bias filter", GroupName = "2. Signals", Order = 1)]
        public SneakyWix9ineBiasFilter BiasFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Only trade the wick side of the story-candle open", GroupName = "2. Signals", Order = 2)]
        public bool WickFilter { get; set; }

        [NinjaScriptProperty][Range(0, 20)]
        [Display(Name = "Max closes outside before breakout counts as accepted", GroupName = "2. Signals", Order = 3)]
        public int FailBars { get; set; }

        [NinjaScriptProperty][Range(1, 200)]
        [Display(Name = "Retest window (bars)", GroupName = "2. Signals", Order = 4)]
        public int RetestBars { get; set; }

        [NinjaScriptProperty][Range(0, double.MaxValue)]
        [Display(Name = "Level tolerance (× ATR 14)", GroupName = "2. Signals", Order = 5)]
        public double TolATR { get; set; }

        [NinjaScriptProperty][Range(0, int.MaxValue)]
        [Display(Name = "Min bars between same-side signals", GroupName = "2. Signals", Order = 6)]
        public int Cooldown { get; set; }

        // 3. Closures & display
        [Display(Name = "Mark chart closures outside R1", GroupName = "3. Closures & display", Order = 0)]
        public bool ShowClosures { get; set; }

        [Display(Name = "Mark story-TF closures outside prior range", GroupName = "3. Closures & display", Order = 1)]
        public bool ShowHtfClose { get; set; }

        [Display(Name = "Mark failed breakouts (Retest mode)", GroupName = "3. Closures & display", Order = 2)]
        public bool ShowFails { get; set; }

        [Display(Name = "Tint background by story-TF bias", GroupName = "3. Closures & display", Order = 3)]
        public bool ShowBiasBg { get; set; }

        [Display(Name = "Show dashboard", GroupName = "3. Closures & display", Order = 4)]
        public bool ShowTable { get; set; }

        // 4. Colors
        [XmlIgnore]
        [Display(Name = "Range high / bearish", GroupName = "4. Colors", Order = 0)]
        public Brush ResColor { get; set; }
        [Browsable(false)]
        public string ResColorSerialize { get { return Serialize.BrushToString(ResColor); } set { ResColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Range low / bullish", GroupName = "4. Colors", Order = 1)]
        public Brush SupColor { get; set; }
        [Browsable(false)]
        public string SupColorSerialize { get { return Serialize.BrushToString(SupColor); } set { SupColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Full range", GroupName = "4. Colors", Order = 2)]
        public Brush FullColor { get; set; }
        [Browsable(false)]
        public string FullColorSerialize { get { return Serialize.BrushToString(FullColor); } set { FullColor = Serialize.StringToBrush(value); } }

        [Range(0, 100)]
        [Display(Name = "Wick zone opacity %", GroupName = "4. Colors", Order = 3)]
        public int WickOpacity { get; set; }

        [Range(0, 100)]
        [Display(Name = "Full range opacity %", GroupName = "4. Colors", Order = 4)]
        public int FullOpacity { get; set; }

        [Range(0, 100)]
        [Display(Name = "Older range line opacity %", GroupName = "4. Colors", Order = 5)]
        public int FadedOpacity { get; set; }

        [Range(0, 100)]
        [Display(Name = "Bias tint opacity %", GroupName = "4. Colors", Order = 6)]
        public int TintOpacity { get; set; }

        // 5. Alerts
        [Display(Name = "Enable alerts", GroupName = "5. Alerts", Order = 0)]
        public bool EnableAlerts { get; set; }

        [Display(Name = "Entry signals", GroupName = "5. Alerts", Order = 1)]
        public bool AlertEntries { get; set; }

        [Display(Name = "Story-TF closures", GroupName = "5. Alerts", Order = 2)]
        public bool AlertStoryClose { get; set; }

        [Display(Name = "Failed breakouts", GroupName = "5. Alerts", Order = 3)]
        public bool AlertFails { get; set; }

        [Display(Name = "Entry sound (NT sounds folder)", GroupName = "5. Alerts", Order = 4)]
        public string EntrySound { get; set; }

        [Display(Name = "Info sound (NT sounds folder)", GroupName = "5. Alerts", Order = 5)]
        public string InfoSound { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SneakyWix9ineRanges[] cacheSneakyWix9ineRanges;
		public SneakyWix9ineRanges SneakyWix9ineRanges(int storyMinutes, int numRanges, SneakyWix9ineEntryMode entryMode, SneakyWix9ineBiasFilter biasFilter, bool wickFilter, int failBars, int retestBars, double tolATR, int cooldown)
		{
			return SneakyWix9ineRanges(Input, storyMinutes, numRanges, entryMode, biasFilter, wickFilter, failBars, retestBars, tolATR, cooldown);
		}

		public SneakyWix9ineRanges SneakyWix9ineRanges(ISeries<double> input, int storyMinutes, int numRanges, SneakyWix9ineEntryMode entryMode, SneakyWix9ineBiasFilter biasFilter, bool wickFilter, int failBars, int retestBars, double tolATR, int cooldown)
		{
			if (cacheSneakyWix9ineRanges != null)
				for (int idx = 0; idx < cacheSneakyWix9ineRanges.Length; idx++)
					if (cacheSneakyWix9ineRanges[idx] != null && cacheSneakyWix9ineRanges[idx].StoryMinutes == storyMinutes && cacheSneakyWix9ineRanges[idx].NumRanges == numRanges && cacheSneakyWix9ineRanges[idx].EntryMode == entryMode && cacheSneakyWix9ineRanges[idx].BiasFilter == biasFilter && cacheSneakyWix9ineRanges[idx].WickFilter == wickFilter && cacheSneakyWix9ineRanges[idx].FailBars == failBars && cacheSneakyWix9ineRanges[idx].RetestBars == retestBars && cacheSneakyWix9ineRanges[idx].TolATR == tolATR && cacheSneakyWix9ineRanges[idx].Cooldown == cooldown && cacheSneakyWix9ineRanges[idx].EqualsInput(input))
						return cacheSneakyWix9ineRanges[idx];
			return CacheIndicator<SneakyWix9ineRanges>(new SneakyWix9ineRanges(){ StoryMinutes = storyMinutes, NumRanges = numRanges, EntryMode = entryMode, BiasFilter = biasFilter, WickFilter = wickFilter, FailBars = failBars, RetestBars = retestBars, TolATR = tolATR, Cooldown = cooldown }, input, ref cacheSneakyWix9ineRanges);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SneakyWix9ineRanges SneakyWix9ineRanges(int storyMinutes, int numRanges, SneakyWix9ineEntryMode entryMode, SneakyWix9ineBiasFilter biasFilter, bool wickFilter, int failBars, int retestBars, double tolATR, int cooldown)
		{
			return indicator.SneakyWix9ineRanges(Input, storyMinutes, numRanges, entryMode, biasFilter, wickFilter, failBars, retestBars, tolATR, cooldown);
		}

		public Indicators.SneakyWix9ineRanges SneakyWix9ineRanges(ISeries<double> input , int storyMinutes, int numRanges, SneakyWix9ineEntryMode entryMode, SneakyWix9ineBiasFilter biasFilter, bool wickFilter, int failBars, int retestBars, double tolATR, int cooldown)
		{
			return indicator.SneakyWix9ineRanges(input, storyMinutes, numRanges, entryMode, biasFilter, wickFilter, failBars, retestBars, tolATR, cooldown);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SneakyWix9ineRanges SneakyWix9ineRanges(int storyMinutes, int numRanges, SneakyWix9ineEntryMode entryMode, SneakyWix9ineBiasFilter biasFilter, bool wickFilter, int failBars, int retestBars, double tolATR, int cooldown)
		{
			return indicator.SneakyWix9ineRanges(Input, storyMinutes, numRanges, entryMode, biasFilter, wickFilter, failBars, retestBars, tolATR, cooldown);
		}

		public Indicators.SneakyWix9ineRanges SneakyWix9ineRanges(ISeries<double> input , int storyMinutes, int numRanges, SneakyWix9ineEntryMode entryMode, SneakyWix9ineBiasFilter biasFilter, bool wickFilter, int failBars, int retestBars, double tolATR, int cooldown)
		{
			return indicator.SneakyWix9ineRanges(input, storyMinutes, numRanges, entryMode, biasFilter, wickFilter, failBars, retestBars, tolATR, cooldown);
		}
	}
}

#endregion
