#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// Mechanical versions of five thesecretmindset.com strategies, for Strategy Analyzer testing.
// Mirrors the Python backtest logic: signals on bar close, entry at next bar open,
// stop/target set in points at entry. Run on a 1-minute (or 5-minute) NQ/MNQ chart.
namespace NinjaTrader.NinjaScript.Strategies
{
    public enum SneakySmMode { ScalpBreakout, ScalpDivergence, ObvBreakout, DoubleDoji, EmaPullback }

    public class SneakySecretMindsetStrategy : Strategy
    {
        private EMA emaFast, emaSlow, ema50, ema200;
        private Stochastics stoch;
        private OBV obv;
        private EMA obvEma;
        private Bollinger bb;
        private MAX maxHigh, maxHighSwing;
        private MIN minLow, minLowSwing;
        private MAX maxHighPiv;
        private MIN minLowPiv;
        private Series<double> bw;

        // divergence state
        private double lastLowPx = double.NaN, lastLowK = double.NaN; private int lastLowBar = -1;
        private double lastHighPx = double.NaN, lastHighK = double.NaN; private int lastHighBar = -1;
        private int pendDir = 0, pendExpiry = 0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Mechanical tests of 5 thesecretmindset.com strategies.";
                Name = "SneakySecretMindsetStrategy";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsInstantiatedOnEachOptimizationIteration = true;
                BarsRequiredToTrade = 20;

                Mode = SneakySmMode.ScalpBreakout;
                Quantity = 1;
                AllowLong = true; AllowShort = true; ShowChartAids = true;
                FixedStopTicks = 60; FixedTargetTicks = 100;     // scalp modes (60t = 15 pts, 100t = 25 pts on NQ)
                RewardRisk = 2.0; MinRiskTicks = 16; MaxRiskTicks = 240;   // swing-stop modes (16t = 4 pts, 240t = 60 pts on NQ)
                BreakoutLookback = 20; SwingLookback = 10;
                FastEma = 200; SlowEma = 1000;
                PivotBars = 5; DivMaxAge = 40;
                DojiBody = 0.15;
            }
            else if (State == State.DataLoaded)
            {
                emaFast = EMA(FastEma); emaSlow = EMA(SlowEma);
                ema50 = EMA(50); ema200 = EMA(200);
                stoch = Stochastics(10, 50, 3);            // periodD, periodK, smooth  == Stoch(50,3,10)
                obv = OBV(); obvEma = EMA(obv, 200);
                bb = Bollinger(2, 20);
                maxHigh = MAX(High, BreakoutLookback); minLow = MIN(Low, BreakoutLookback);
                maxHighSwing = MAX(High, SwingLookback); minLowSwing = MIN(Low, SwingLookback);
                maxHighPiv = MAX(High, 2 * PivotBars + 1); minLowPiv = MIN(Low, 2 * PivotBars + 1);
                bw = new Series<double>(this);

                if (ShowChartAids)
                {
                    switch (Mode)
                    {
                        case SneakySmMode.ScalpBreakout:
                            Style(emaFast, Brushes.Orange, 2); Style(emaSlow, Brushes.DeepSkyBlue, 2);
                            Style(maxHigh, Brushes.Gray, 1); Style(minLow, Brushes.Gray, 1);
                            AddChartIndicator(emaFast); AddChartIndicator(emaSlow);
                            AddChartIndicator(maxHigh); AddChartIndicator(minLow);
                            break;
                        case SneakySmMode.ScalpDivergence:
                            Style(emaFast, Brushes.Orange, 2); Style(emaSlow, Brushes.DeepSkyBlue, 2);
                            AddChartIndicator(emaFast); AddChartIndicator(emaSlow);
                            AddChartIndicator(stoch);                 // own panel
                            break;
                        case SneakySmMode.ObvBreakout:
                            AddChartIndicator(bb);
                            Style(maxHigh, Brushes.Gray, 1); Style(minLow, Brushes.Gray, 1);
                            AddChartIndicator(maxHigh); AddChartIndicator(minLow);
                            AddChartIndicator(obv);                   // own panel
                            break;
                        case SneakySmMode.DoubleDoji:
                            AddChartIndicator(obv);                   // own panel
                            break;
                        case SneakySmMode.EmaPullback:
                            Style(ema50, Brushes.Orange, 2); Style(ema200, Brushes.DeepSkyBlue, 2);
                            AddChartIndicator(ema50); AddChartIndicator(ema200);
                            break;
                    }
                }
            }
        }

        private void Style(Indicators.Indicator ind, Brush brush, int width)
        {
            if (ind != null && ind.Plots.Length > 0) { ind.Plots[0].Brush = brush; ind.Plots[0].Width = width; }
        }

        // OBV-vs-EMA trend tint (OBV modes): green = long-only side, red = short-only side
        private static readonly Brush TintUp = MakeBrush(Color.FromArgb(28, 0, 200, 90));
        private static readonly Brush TintDn = MakeBrush(Color.FromArgb(28, 230, 40, 40));
        private static Brush MakeBrush(Color c) { SolidColorBrush b = new SolidColorBrush(c); b.Freeze(); return b; }

        private void Mark(int dir, double riskPts, double tgtPts)
        {
            if (!ShowChartAids) return;
            string tag = "SM_" + CurrentBar;
            string txt = string.Format("{0}  SL {1}t{2}", dir > 0 ? "LONG" : "SHORT", (int)Math.Round(riskPts / TickSize), tgtPts > 0 ? "  TP " + ((int)Math.Round(tgtPts / TickSize)) + "t" : "");
            if (dir > 0)
            {
                Draw.ArrowUp(this, tag, false, 0, Low[0] - 2 * TickSize * 4, Brushes.LimeGreen);
                Draw.Text(this, tag + "t", false, txt, 0, Low[0] - 2 * TickSize * 16, 0, Brushes.LimeGreen,
                    new NinjaTrader.Gui.Tools.SimpleFont("Arial", 10), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            else
            {
                Draw.ArrowDown(this, tag, false, 0, High[0] + 2 * TickSize * 4, Brushes.Red);
                Draw.Text(this, tag + "t", false, txt, 0, High[0] + 2 * TickSize * 16, 0, Brushes.Red,
                    new NinjaTrader.Gui.Tools.SimpleFont("Arial", 10), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
        }

        private void Go(int dir, double riskPts, double tgtPts)
        {
            if (dir > 0 && !AllowLong) return;
            if (dir < 0 && !AllowShort) return;
            Mark(dir, riskPts, tgtPts);
            int stopTicks = Math.Max(1, (int)Math.Round(riskPts / TickSize));
            SetStopLoss(CalculationMode.Ticks, stopTicks);
            if (tgtPts > 0) SetProfitTarget(CalculationMode.Ticks, Math.Max(1, (int)Math.Round(tgtPts / TickSize)));
            if (dir > 0) EnterLong(Quantity, "SM_L"); else EnterShort(Quantity, "SM_S");
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < Math.Max(210, Mode == SneakySmMode.ScalpBreakout || Mode == SneakySmMode.ScalpDivergence ? SlowEma + 5 : 210)) return;

            // keep bandwidth series current on every bar (needed by ObvBreakout)
            double mid = bb.Middle[0];
            bw[0] = mid > 0 ? (bb.Upper[0] - bb.Lower[0]) / mid : 0;

            if (ShowChartAids)
            {
                if (Mode == SneakySmMode.ObvBreakout || Mode == SneakySmMode.DoubleDoji)
                    BackBrush = obv[0] > obvEma[0] ? TintUp : TintDn;
                if (Mode == SneakySmMode.DoubleDoji && IsDoji(0))
                    Draw.Dot(this, "SMd_" + CurrentBar, false, 0, (High[0] + Low[0]) / 2.0, Brushes.Gold);
                if (Mode == SneakySmMode.ScalpBreakout || Mode == SneakySmMode.ScalpDivergence)
                    BackBrush = emaFast[0] > emaSlow[0] ? TintUp : TintDn;
                if (Mode == SneakySmMode.EmaPullback)
                    BackBrush = ema50[0] > ema200[0] ? TintUp : TintDn;
            }

            if (Mode == SneakySmMode.ScalpDivergence) TrackDivergence();   // must run every bar, even when in a position
            if (Position.MarketPosition != MarketPosition.Flat) return;

            switch (Mode)
            {
                case SneakySmMode.ScalpBreakout: ScalpBreakout(); break;
                case SneakySmMode.ScalpDivergence: ScalpDivergenceEntry(); break;
                case SneakySmMode.ObvBreakout: ObvBreakout(); break;
                case SneakySmMode.DoubleDoji: DoubleDoji(); break;
                case SneakySmMode.EmaPullback: EmaPullback(); break;
            }
        }

        // 1. EMA200/EMA1000 bias, breakout of the prior N-bar range, fixed stop/target
        private void ScalpBreakout()
        {
            double ph = maxHigh[1], pl = minLow[1];
            if (emaFast[0] > emaSlow[0] && Close[0] > ph) Go(1, FixedStopTicks * TickSize, FixedTargetTicks * TickSize);
            else if (emaFast[0] < emaSlow[0] && Close[0] < pl) Go(-1, FixedStopTicks * TickSize, FixedTargetTicks * TickSize);
        }

        // 2. hidden divergence in trend direction + first Stochastic K/D cross
        private void TrackDivergence()
        {
            int pb = PivotBars;
            if (CurrentBar < pb * 2 + 2) return;
            double k = stoch.K[pb];
            if (double.IsNaN(k)) return;
            if (Low[pb] <= minLowPiv[0] + 1e-9)            // pivot low (lowest of window)
            {
                if (!double.IsNaN(lastLowPx) && emaFast[0] > emaSlow[0]
                    && Low[pb] > lastLowPx && k < lastLowK && (CurrentBar - pb) - lastLowBar < 150)
                { pendDir = 1; pendExpiry = CurrentBar + DivMaxAge; }
                lastLowPx = Low[pb]; lastLowK = k; lastLowBar = CurrentBar - pb;
            }
            if (High[pb] >= maxHighPiv[0] - 1e-9)          // pivot high
            {
                if (!double.IsNaN(lastHighPx) && emaFast[0] < emaSlow[0]
                    && High[pb] < lastHighPx && k > lastHighK && (CurrentBar - pb) - lastHighBar < 150)
                { pendDir = -1; pendExpiry = CurrentBar + DivMaxAge; }
                lastHighPx = High[pb]; lastHighK = k; lastHighBar = CurrentBar - pb;
            }
            if (pendDir != 0 && CurrentBar > pendExpiry) pendDir = 0;
        }

        private void ScalpDivergenceEntry()
        {
            if (pendDir == 0) return;
            double k0 = stoch.K[0], k1 = stoch.K[1], d0 = stoch.D[0], d1 = stoch.D[1];
            if (pendDir == 1 && k1 <= d1 && k0 > d0 && emaFast[0] > emaSlow[0]) { pendDir = 0; Go(1, FixedStopTicks * TickSize, FixedTargetTicks * TickSize); }
            else if (pendDir == -1 && k1 >= d1 && k0 < d0 && emaFast[0] < emaSlow[0]) { pendDir = 0; Go(-1, FixedStopTicks * TickSize, FixedTargetTicks * TickSize); }
        }

        // 3. OBV vs its 200 EMA picks the side; Bollinger squeeze in the last 5 bars; range breakout; swing stop; N:1 target
        private void ObvBreakout()
        {
            if (CurrentBar < 120) return;
            double med = Median(bw, 100, 1);
            if (med <= 0) return;
            bool squeezed = false;
            for (int b = 1; b <= 5; b++)
            {
                double m = Median(bw, 100, b);
                if (m > 0 && bw[b] / m < 1.0) { squeezed = true; break; }
            }
            if (!squeezed) return;
            if (obv[0] > obvEma[0] && Close[0] > maxHigh[1])
            {
                double r = Close[0] - minLowSwing[0];
                if (r >= MinRiskTicks * TickSize && r <= MaxRiskTicks * TickSize) Go(1, r, r * RewardRisk);
            }
            else if (obv[0] < obvEma[0] && Close[0] < minLow[1])
            {
                double r = maxHighSwing[0] - Close[0];
                if (r >= MinRiskTicks * TickSize && r <= MaxRiskTicks * TickSize) Go(-1, r, r * RewardRisk);
            }
        }

        private double Median(Series<double> s, int len, int offset)
        {
            List<double> v = new List<double>(len);
            for (int i = offset; i < offset + len && i <= CurrentBar; i++) v.Add(s[i]);
            if (v.Count == 0) return 0;
            v.Sort();
            return v[v.Count / 2];
        }

        // 4. two consecutive dojis, breakout of their range in the OBV trend direction, stop beyond the dojis
        private bool IsDoji(int i)
        {
            double rng = High[i] - Low[i];
            return rng > 0 && Math.Abs(Close[i] - Open[i]) <= DojiBody * rng;
        }

        private void DoubleDoji()
        {
            if (!(IsDoji(2) && IsDoji(1))) return;
            double hi = Math.Max(High[2], High[1]), lo = Math.Min(Low[2], Low[1]);
            if (obv[0] > obvEma[0] && Close[0] > hi)
            {
                double r = Close[0] - lo;
                if (r >= MinRiskTicks * TickSize && r <= MaxRiskTicks * TickSize) Go(1, r, r * RewardRisk);
            }
            else if (obv[0] < obvEma[0] && Close[0] < lo)
            {
                double r = hi - Close[0];
                if (r >= MinRiskTicks * TickSize && r <= MaxRiskTicks * TickSize) Go(-1, r, r * RewardRisk);
            }
        }

        // 5. 50/200 EMA trend, pullback to the 50, re-entry on break of prior bar, swing stop, N:1 target
        private void EmaPullback()
        {
            if (ema50[0] > ema200[0] && Low[1] <= ema50[1] && Close[0] > High[1] && Close[0] > ema50[0])
            {
                double r = Close[0] - minLowSwing[0];
                if (r >= MinRiskTicks * TickSize && r <= MaxRiskTicks * TickSize) Go(1, r, r * RewardRisk);
            }
            else if (ema50[0] < ema200[0] && High[1] >= ema50[1] && Close[0] < Low[1] && Close[0] < ema50[0])
            {
                double r = maxHighSwing[0] - Close[0];
                if (r >= MinRiskTicks * TickSize && r <= MaxRiskTicks * TickSize) Go(-1, r, r * RewardRisk);
            }
        }

        #region Properties
        [NinjaScriptProperty][Display(Name = "Mode", GroupName = "1. Strategy", Order = 1)] public SneakySmMode Mode { get; set; }
        [NinjaScriptProperty][Range(1, 100)][Display(Name = "Quantity", GroupName = "1. Strategy", Order = 2)] public int Quantity { get; set; }
        [NinjaScriptProperty][Display(Name = "Allow Long", GroupName = "1. Strategy", Order = 3)] public bool AllowLong { get; set; }
        [NinjaScriptProperty][Display(Name = "Show indicators & signals on chart", GroupName = "1. Strategy", Order = 5)] public bool ShowChartAids { get; set; }
        [NinjaScriptProperty][Display(Name = "Allow Short", GroupName = "1. Strategy", Order = 4)] public bool AllowShort { get; set; }
        [NinjaScriptProperty][Range(1, 100000)][Display(Name = "Fixed Stop (ticks) - scalp modes", GroupName = "2. Exits", Order = 1)] public int FixedStopTicks { get; set; }
        [NinjaScriptProperty][Range(0, 100000)][Display(Name = "Fixed Target (ticks) - scalp modes", GroupName = "2. Exits", Order = 2)] public int FixedTargetTicks { get; set; }
        [NinjaScriptProperty][Range(0.5, 10)][Display(Name = "Reward:Risk - swing-stop modes", GroupName = "2. Exits", Order = 3)] public double RewardRisk { get; set; }
        [NinjaScriptProperty][Range(1, 100000)][Display(Name = "Min risk (ticks)", GroupName = "2. Exits", Order = 4)] public int MinRiskTicks { get; set; }
        [NinjaScriptProperty][Range(1, 100000)][Display(Name = "Max risk (ticks)", GroupName = "2. Exits", Order = 5)] public int MaxRiskTicks { get; set; }
        [NinjaScriptProperty][Range(2, 500)][Display(Name = "Breakout lookback", GroupName = "3. Signals", Order = 1)] public int BreakoutLookback { get; set; }
        [NinjaScriptProperty][Range(2, 200)][Display(Name = "Swing-stop lookback", GroupName = "3. Signals", Order = 2)] public int SwingLookback { get; set; }
        [NinjaScriptProperty][Range(2, 2000)][Display(Name = "Fast EMA (bias)", GroupName = "3. Signals", Order = 3)] public int FastEma { get; set; }
        [NinjaScriptProperty][Range(2, 5000)][Display(Name = "Slow EMA (bias)", GroupName = "3. Signals", Order = 4)] public int SlowEma { get; set; }
        [NinjaScriptProperty][Range(2, 50)][Display(Name = "Divergence pivot bars", GroupName = "3. Signals", Order = 5)] public int PivotBars { get; set; }
        [NinjaScriptProperty][Range(1, 500)][Display(Name = "Divergence max age (bars)", GroupName = "3. Signals", Order = 6)] public int DivMaxAge { get; set; }
        [NinjaScriptProperty][Range(0.01, 0.5)][Display(Name = "Doji body / range", GroupName = "3. Signals", Order = 7)] public double DojiBody { get; set; }
        #endregion
    }
}
