#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// SneakyMarketAdvisor
// NinjaTrader 8 Indicator -- a live, plain-English "market advisor" panel that calls out what it sees as
// bars close: candlestick patterns, swing-based market structure (trend / break of structure / change of
// character), nearby support & resistance, and classic chart patterns (double top/bottom, head & shoulders,
// triangles, wedges, flags/pennants, cup & handle).
//
// Standalone: no SneakySuite/SneakySkin dependency (same lesson as SneakyATM's share build) -- hardcoded
// dark theme, compiles and runs with just this one file.
//
// HONESTY NOTE on the classic chart patterns (double top/bottom, H&S, triangle/wedge, flag/pennant,
// cup & handle): these are geometric, tolerance-based heuristics over detected swing points, not exact
// math -- real chart patterns are fuzzy by nature and no two chartists would draw them identically. Expect
// false positives and missed/late calls, especially on cup & handle (the fuzziest of the set here). Every
// classic-pattern call-out in the feed is tagged "(heuristic)" for that reason. Candlestick patterns and
// swing-structure (trend / BOS / CHOCH / S-R) are on much firmer, well-defined ground.
//
// Calculate = OnBarClose: everything here reacts to a bar CLOSING, same timing convention as the rest of
// the Sneaky Suite. No orders are placed -- this is read-only, observational, like SneakyMarketContext.

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakyMarketAdvisor_v1_0_0 : Indicator
    {
        // ------------------------------------------------------------------ swing / structure state
        class Swing { public int Bar; public double Price; public bool IsHigh; public double Vol; public double Delta; }
        List<Swing> swHighs = new List<Swing>();
        List<Swing> swLows = new List<Swing>();
        string trendState = "Forming...";
        double lastBosUpLevel = double.NaN, lastBosDownLevel = double.NaN;   // last level we already announced a break past, so we don't repeat every bar
        string lastPatternText = "none yet";
        List<SrZone> srZones = new List<SrZone>();
        class SrZone { public double Price; public int Hits; }

        // dedupe trackers for the classic patterns (bar index of the last time each one fired, so a still-forming
        // pattern doesn't spam the feed every single bar while conditions keep holding)
        int lastDoubleTopBar = -1000, lastDoubleBotBar = -1000;
        int lastHsBar = -1000, lastIhsBar = -1000;
        int lastTriBar = -1000, lastWedgeBar = -1000;
        int lastFlagBar = -1000;
        int lastCupBar = -1000;
        int tagStackBar = int.MinValue;
        int tagAboveCount;
        int tagBelowCount;
        int lastVolClimaxBar = -1000, lastVolDivBar = -1000, lastLowVolPbBar = -1000, lastSweepBar = -1000;
        int lastAbsorbBar = -1000, lastTrapBar = -1000, lastDeltaDivBar = -1000;

        ATR atr;
        double tick;

        // ------------------------------------------------------------------ volume
        SMA volAvg;

        // ------------------------------------------------------------------ order flow (true bid/ask delta)
        // Built from raw trade prints classified against the last known bid/ask (quote rule), falling back to
        // the tick rule (vs. the prior trade price) when a trade doesn't cleanly sit at/through either side.
        // OnMarketData fires on live/tick-replay data regardless of Calculate mode, so this only populates
        // once connected live (or replaying) -- it stays at "waiting for live ticks" on pure historical bars,
        // same limitation SneakyAggressionSplit has elsewhere in the suite.
        double lastBid = double.NaN, lastAsk = double.NaN, lastTradePrice = double.NaN;
        double barBuyVol, barSellVol;
        double lastBarBuyVol, lastBarSellVol, lastBarDelta;
        double cumDelta;
        bool haveLiveDelta;
        List<double> cumDeltaHistory = new List<double>();   // cumulative delta as-of each bar's close, indexed by absolute bar number -- lets swings carry "delta at that swing" the same way they already carry Vol

        // ------------------------------------------------------------------ VWAP + bands (session-anchored)
        SessionIterator sessionIterator;
        double cumPV, cumVol, cumPV2;
        double vwapVal, vwapDevUp1, vwapDevDn1, vwapDevUp2, vwapDevDn2;

        // ------------------------------------------------------------------ higher-timeframe alignment + volatility regime
        string htfTrendState = "warming up...";
        SMA atrBaseline;
        string volRegimeState = "warming up...";

        // ------------------------------------------------------------------ UI
        Window win;
        TextBlock trendLine, patternLine, srLine, structureLine;
        TextBlock volLine, vwapLine, flowLine, htfLine, volRegimeLine;
        StackPanel feedStack;
        ScrollViewer feedScroll;
        int feedCount;

        // theme (hardcoded -- standalone, no SneakySkin dependency)
        // NOTE: these are shared, static Freezable (DependencyObject) instances touched from BOTH
        // the calculation thread (Draw.Text(...) inside Fire(), which runs synchronously in
        // OnBarUpdate) and the UI thread (panel controls' Background/Foreground). An un-frozen
        // SolidColorBrush gets permanently "owned" by whichever thread touches it first, and any
        // later access from the other thread throws "The calling thread cannot access this object
        // because a different thread owns it." Freezing makes them immutable and thread-safe to
        // share, which is exactly what we want for a fixed color palette -- hence .Freeze() below.
        static readonly SolidColorBrush Bg = Frz(new SolidColorBrush(Color.FromRgb(18, 20, 26)));
        static readonly SolidColorBrush Card = Frz(new SolidColorBrush(Color.FromRgb(26, 29, 38)));
        static readonly SolidColorBrush Edge = Frz(new SolidColorBrush(Color.FromRgb(50, 54, 66)));
        static readonly SolidColorBrush Text = Frz(new SolidColorBrush(Color.FromRgb(228, 231, 238)));
        static readonly SolidColorBrush Muted = Frz(new SolidColorBrush(Color.FromRgb(146, 152, 168)));
        static readonly SolidColorBrush Green = Frz(new SolidColorBrush(Color.FromRgb(70, 205, 140)));
        static readonly SolidColorBrush Red = Frz(new SolidColorBrush(Color.FromRgb(230, 95, 95)));
        static readonly SolidColorBrush Amber = Frz(new SolidColorBrush(Color.FromRgb(230, 175, 80)));
        static readonly SolidColorBrush Accent = Frz(new SolidColorBrush(Color.FromRgb(90, 160, 230)));

        static SolidColorBrush Frz(SolidColorBrush b)
        {
            b.Freeze();
            return b;
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Live plain-English market advisor: candlestick patterns, swing structure (trend/BOS/CHOCH), support & resistance, and classic chart patterns (double top/bottom, H&S, triangle/wedge, flag, cup & handle).";
                Name = "SneakyMarketAdvisor";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;

                SwingStrength = 3;
                UseCandlePatterns = true;
                UseMarketStructure = true;
                UseClassicPatterns = true;
                UseDoubleTopBottom = true;
                UseHeadShoulders = true;
                UseTriangleWedge = true;
                UseFlagPennant = true;
                UseCupHandle = true;

                SrClusterTicks = 15;
                SwingTolerancePct = 0.15;
                MinRetracePct = 30;
                AtrPeriod = 14;
                PoleAtrMultiple = 2.5;
                FlagMaxAtrMultiple = 1.0;
                CupLookbackBars = 120;
                CupMinBars = 30;
                HandleMaxRetracePct = 35;

                ShowPanel = true;
                ShowChartTags = true;
                FeedMaxLines = 150;
                Use24HourTime = true;

                UseVolumeFeatures = true;
                VolumeAvgPeriod = 20;
                VolClimaxMultiple = 2.5;
                LowVolPullbackMultiple = 0.7;
                UseOrderFlow = true;
                OrderFlowDeltaMultiple = 0.3;

                UseVwap = true;
                UseLiquiditySweep = true;
                UseHtfAlignment = true;
                HtfMinutes = 15;
                UseVolatilityRegime = true;
                VolRegimeLookback = 50;

                // VWAP + 1/2 standard-deviation bands as real plots (continuous lines), not just text --
                // these stay invisible (NaN) whenever UseVwap is off or we're still warming up.
                AddPlot(new Stroke(Accent, 2), PlotStyle.Line, "VWAP");
                AddPlot(new Stroke(Muted, DashStyleHelper.Dash, 1), PlotStyle.Line, "VWAP Upper 1");
                AddPlot(new Stroke(Muted, DashStyleHelper.Dash, 1), PlotStyle.Line, "VWAP Lower 1");
                AddPlot(new Stroke(Muted, DashStyleHelper.Dot, 1), PlotStyle.Line, "VWAP Upper 2");
                AddPlot(new Stroke(Muted, DashStyleHelper.Dot, 1), PlotStyle.Line, "VWAP Lower 2");
            }
            else if (State == State.Configure)
            {
                // secondary, higher-timeframe series for the HTF trend-alignment read. Reading its series
                // (Closes[1], Highs[1], Lows[1]) from inside the PRIMARY series' OnBarUpdate is enough for a
                // simple trend bias -- no need to branch on BarsInProgress for that. We still guard
                // OnBarUpdate against BarsInProgress != 0 below so this series' own bar-close events (which
                // would otherwise run all the 1-minute pattern logic on 15-minute bars) are ignored outright.
                if (UseHtfAlignment)
                    AddDataSeries(BarsPeriodType.Minute, HtfMinutes);
            }
            else if (State == State.DataLoaded)
            {
                tick = TickSize;
                atr = ATR(AtrPeriod);
                volAvg = SMA(Volume, VolumeAvgPeriod);
                atrBaseline = SMA(atr, VolRegimeLookback);
            }
            else if (State == State.Historical)
            {
                if (ShowPanel && ChartControl != null && win == null)
                    ChartControl.Dispatcher.InvokeAsync(BuildPanel);
            }
            else if (State == State.Terminated)
            {
                if (win != null)
                {
                    var w = win; win = null;
                    try { w.Dispatcher.InvokeAsync(() => { try { w.Close(); } catch { } }); } catch { }
                }
            }
        }

        protected override void OnBarUpdate()
        {
            // ignore bar-close events from the secondary (HTF) series added in State.Configure -- we read its
            // values (Closes[1]/Highs[1]/Lows[1]) from here, the primary series' context, instead of handling
            // its own bar closes, so the pattern/structure logic below never runs twice on the wrong timeframe.
            if (BarsInProgress != 0) return;

            // order flow: snapshot this bar's live tick-accumulated buy/sell volume (if any), then reset for
            // the bar that's about to start. Must happen before the early-return below so the counters don't
            // silently build up across skipped warm-up bars.
            lastBarBuyVol = barBuyVol; lastBarSellVol = barSellVol; lastBarDelta = lastBarBuyVol - lastBarSellVol;
            barBuyVol = 0; barSellVol = 0;
            cumDeltaHistory.Add(cumDelta);

            UpdateVwap();
            UpdateHtfTrend();
            UpdateVolRegime();

            if (CurrentBar < Math.Max(2 * SwingStrength + 2, MinBarsNeeded())) return;

            UpdateSwings();
            if (UseMarketStructure) UpdateStructureAndSr();
            if (UseCandlePatterns) CheckCandlePatterns();
            if (UseClassicPatterns)
            {
                if (UseDoubleTopBottom) CheckDoubleTopBottom();
                if (UseHeadShoulders) CheckHeadShoulders();
                if (UseTriangleWedge) CheckTriangleWedge();
                if (UseFlagPennant) CheckFlagPennant();
                if (UseCupHandle) CheckCupAndHandle();
            }
            if (UseVolumeFeatures) CheckVolumeSignals();
            if (UseLiquiditySweep) CheckLiquiditySweep();
            if (UseOrderFlow && haveLiveDelta) CheckOrderFlowSignals();

            RefreshSummaryPanel();
        }

        int MinBarsNeeded() { return 20; }

        // ------------------------------------------------------------------ swing points
        void UpdateSwings()
        {
            int s = SwingStrength;
            if (CurrentBar < 2 * s) return;
            int candidateBarsAgo = s; // the candidate bar sits s bars back from the bar that just closed

            bool isHigh = true, isLow = true;
            double candH = High[candidateBarsAgo], candL = Low[candidateBarsAgo];
            for (int j = 1; j <= s; j++)
            {
                if (High[candidateBarsAgo - j] >= candH) isHigh = false;
                if (High[candidateBarsAgo + j] >= candH) isHigh = false;
                if (Low[candidateBarsAgo - j] <= candL) isLow = false;
                if (Low[candidateBarsAgo + j] <= candL) isLow = false;
            }
            int absBar = CurrentBar - candidateBarsAgo;

            double deltaAtBar = (absBar >= 0 && absBar < cumDeltaHistory.Count) ? cumDeltaHistory[absBar] : 0;

            if (isHigh && (swHighs.Count == 0 || swHighs[swHighs.Count - 1].Bar != absBar))
            {
                swHighs.Add(new Swing { Bar = absBar, Price = candH, IsHigh = true, Vol = Volume[candidateBarsAgo], Delta = deltaAtBar });
                if (swHighs.Count > 60) swHighs.RemoveAt(0);
            }
            if (isLow && (swLows.Count == 0 || swLows[swLows.Count - 1].Bar != absBar))
            {
                swLows.Add(new Swing { Bar = absBar, Price = candL, IsHigh = false, Vol = Volume[candidateBarsAgo], Delta = deltaAtBar });
                if (swLows.Count > 60) swLows.RemoveAt(0);
            }
        }

        // ------------------------------------------------------------------ structure (trend / BOS / CHOCH) + S/R
        void UpdateStructureAndSr()
        {
            if (swHighs.Count >= 2 && swLows.Count >= 2)
            {
                bool hh = swHighs[swHighs.Count - 1].Price > swHighs[swHighs.Count - 2].Price;
                bool hl = swLows[swLows.Count - 1].Price > swLows[swLows.Count - 2].Price;
                bool lh = swHighs[swHighs.Count - 1].Price < swHighs[swHighs.Count - 2].Price;
                bool ll = swLows[swLows.Count - 1].Price < swLows[swLows.Count - 2].Price;

                string newTrend = (hh && hl) ? "Uptrend (HH/HL)" : (lh && ll) ? "Downtrend (LH/LL)" : "Range / mixed structure";
                if (newTrend != trendState)
                {
                    trendState = newTrend;
                    if (trendState.StartsWith("Uptrend"))
                        Fire("STRUCTURE", "Structure turned bullish -- higher high and higher low confirmed.", false);
                    else if (trendState.StartsWith("Downtrend"))
                        Fire("STRUCTURE", "Structure turned bearish -- lower high and lower low confirmed.", false);
                }

                // break of structure / change of character -- edge-triggered so we don't repeat every bar
                if (swHighs.Count > 0)
                {
                    double level = swHighs[swHighs.Count - 1].Price;
                    if (Close[0] > level && Close[1] <= level && !(lastBosUpLevel == level))
                    {
                        lastBosUpLevel = level;
                        bool wasDowntrend = trendState.StartsWith("Downtrend");
                        Fire(wasDowntrend ? "CHOCH" : "BOS", (wasDowntrend ? "Change of character -- " : "Break of structure -- ") +
                            "close above prior swing high " + P(level) + "." + DeltaNote(true), false);
                    }
                }
                if (swLows.Count > 0)
                {
                    double level = swLows[swLows.Count - 1].Price;
                    if (Close[0] < level && Close[1] >= level && !(lastBosDownLevel == level))
                    {
                        lastBosDownLevel = level;
                        bool wasUptrend = trendState.StartsWith("Uptrend");
                        Fire(wasUptrend ? "CHOCH" : "BOS", (wasUptrend ? "Change of character -- " : "Break of structure -- ") +
                            "close below prior swing low " + P(level) + "." + DeltaNote(false), false);
                    }
                }
            }

            // support/resistance: cluster recent swing points (both highs and lows -- a level is a level
            // regardless of which side first touched it) within SrClusterTicks of each other
            srZones.Clear();
            double clusterDist = SrClusterTicks * tick;
            foreach (var sw in swHighs.Concat(swLows).OrderBy(s => s.Bar).Skip(Math.Max(0, swHighs.Count + swLows.Count - 24)))
            {
                var zone = srZones.FirstOrDefault(z => Math.Abs(z.Price - sw.Price) <= clusterDist);
                if (zone != null) { zone.Price = (zone.Price * zone.Hits + sw.Price) / (zone.Hits + 1); zone.Hits++; }
                else srZones.Add(new SrZone { Price = sw.Price, Hits = 1 });
            }
        }

        // ------------------------------------------------------------------ candlestick patterns
        void CheckCandlePatterns()
        {
            double body0 = Math.Abs(Close[0] - Open[0]), range0 = High[0] - Low[0];
            if (range0 <= 0) return;
            double upper0 = High[0] - Math.Max(Close[0], Open[0]);
            double lower0 = Math.Min(Close[0], Open[0]) - Low[0];
            bool bull0 = Close[0] > Open[0], bear0 = Close[0] < Open[0];
            bool uptrend = trendState.StartsWith("Uptrend");
            bool downtrend = trendState.StartsWith("Downtrend");

            // Doji -- indecision
            if (body0 <= 0.10 * range0)
                Fire("DOJI", "Doji on this candle -- indecision, body is only " + Pct(body0, range0) + "% of the range.", false);

            // Marubozu -- strong momentum, almost no wicks
            if (body0 >= 0.90 * range0)
                Fire("MARUBOZU", (bull0 ? "Bullish" : "Bearish") + " marubozu -- full-bodied candle, strong " + (bull0 ? "buying" : "selling") + " momentum, almost no wick.", false);

            // Hammer / Hanging man (small body near top, long lower wick, little upper wick)
            if (body0 <= 0.35 * range0 && lower0 >= 2.0 * body0 && upper0 <= 0.3 * body0 + tick)
            {
                if (downtrend) Fire("HAMMER", "Hammer after a downtrend -- long lower wick rejecting lower prices, possible bullish reversal.", false);
                else if (uptrend) Fire("HANGMAN", "Hanging man in an uptrend -- long lower wick, watch for a bearish reversal if confirmed next bar.", false);
            }
            // Shooting star / Inverted hammer (small body near bottom, long upper wick)
            if (body0 <= 0.35 * range0 && upper0 >= 2.0 * body0 && lower0 <= 0.3 * body0 + tick)
            {
                if (uptrend) Fire("SHOOTSTAR", "Shooting star after an uptrend -- long upper wick rejecting higher prices, possible bearish reversal.", false);
                else if (downtrend) Fire("INVHAMMER", "Inverted hammer in a downtrend -- long upper wick, watch for a bullish reversal if confirmed next bar.", false);
            }

            if (CurrentBar >= 1)
            {
                double body1 = Math.Abs(Close[1] - Open[1]);
                bool bull1 = Close[1] > Open[1], bear1 = Close[1] < Open[1];
                // Engulfing
                if (bull0 && bear1 && Close[0] > Open[1] && Open[0] < Close[1] && body0 > body1)
                    Fire("ENGULF", "Bullish engulfing -- this candle's body fully covers the prior red candle.", false);
                if (bear0 && bull1 && Open[0] > Close[1] && Close[0] < Open[1] && body0 > body1)
                    Fire("ENGULF", "Bearish engulfing -- this candle's body fully covers the prior green candle.", false);

                // Piercing line / Dark cloud cover
                double mid1 = (Open[1] + Close[1]) / 2.0;
                if (bear1 && bull0 && Open[0] < Close[1] && Close[0] > mid1 && Close[0] < Open[1])
                    Fire("PIERCE", "Piercing line -- opened below the prior red candle's low and closed back above its midpoint.", false);
                if (bull1 && bear0 && Open[0] > Close[1] && Close[0] < mid1 && Close[0] > Open[1])
                    Fire("DARKCLOUD", "Dark cloud cover -- opened above the prior green candle's high and closed back below its midpoint.", false);

                // Inside bar / Outside bar
                if (High[0] < High[1] && Low[0] > Low[1])
                    Fire("INSIDE", "Inside bar -- this candle's range sits entirely inside the prior one, a coil/consolidation tell.", false);
                if (High[0] > High[1] && Low[0] < Low[1])
                    Fire("OUTSIDE", "Outside bar -- this candle's range fully engulfs the prior one's range.", false);
            }

            if (CurrentBar >= 2)
            {
                bool bull2 = Close[2] > Open[2], bear2 = Close[2] < Open[2];
                double body1 = Math.Abs(Close[1] - Open[1]), range1 = High[1] - Low[1];
                bool smallMid = range1 > 0 && body1 <= 0.35 * range1;

                // Morning star / Evening star
                if (bear2 && smallMid && bull0 && Close[0] > (Open[2] + Close[2]) / 2.0)
                    Fire("MORNSTAR", "Morning star forming -- big red, small-bodied middle candle, now a strong green closing back above its midpoint. Bullish reversal tell.", false);
                if (bull2 && smallMid && bear0 && Close[0] < (Open[2] + Close[2]) / 2.0)
                    Fire("EVESTAR", "Evening star forming -- big green, small-bodied middle candle, now a strong red closing back below its midpoint. Bearish reversal tell.", false);

                // Three white soldiers / Three black crows
                bool c2b = Close[2] > Open[2], c1b = Close[1] > Open[1], c0b = Close[0] > Open[0];
                if (c2b && c1b && c0b && Close[1] > Close[2] && Close[0] > Close[1] && Open[1] > Open[2] && Open[0] > Open[1])
                    Fire("3SOLDIERS", "Three white soldiers -- three straight rising green candles, strong bullish momentum.", false);
                bool c2r = Close[2] < Open[2], c1r = Close[1] < Open[1], c0r = Close[0] < Open[0];
                if (c2r && c1r && c0r && Close[1] < Close[2] && Close[0] < Close[1] && Open[1] < Open[2] && Open[0] < Open[1])
                    Fire("3CROWS", "Three black crows -- three straight falling red candles, strong bearish momentum.", false);
            }
        }

        // ------------------------------------------------------------------ classic chart patterns (heuristic)
        void CheckDoubleTopBottom()
        {
            if (swHighs.Count >= 2)
            {
                var h2 = swHighs[swHighs.Count - 1]; var h1 = swHighs[swHighs.Count - 2];
                double tol = h1.Price * SwingTolerancePct / 100.0;
                var between = swLows.Where(l => l.Bar > h1.Bar && l.Bar < h2.Bar).ToList();
                if (Math.Abs(h2.Price - h1.Price) <= tol && between.Count > 0 && CurrentBar - lastDoubleTopBar > 5)
                {
                    double trough = between.Min(l => l.Price);
                    double retrace = (h1.Price - trough) / Math.Max(tick, h1.Price - trough) * 100.0;
                    if (retrace >= MinRetracePct && Close[0] < trough)
                    {
                        lastDoubleTopBar = CurrentBar;
                        Fire("DBLTOP", "(heuristic) Double top -- two comparable swing highs near " + P((h1.Price + h2.Price) / 2) +
                            " with a close now breaking the neckline at " + P(trough) + ". Bearish confirmation.", true);
                    }
                }
            }
            if (swLows.Count >= 2)
            {
                var l2 = swLows[swLows.Count - 1]; var l1 = swLows[swLows.Count - 2];
                double tol = l1.Price * SwingTolerancePct / 100.0;
                var between = swHighs.Where(h => h.Bar > l1.Bar && h.Bar < l2.Bar).ToList();
                if (Math.Abs(l2.Price - l1.Price) <= tol && between.Count > 0 && CurrentBar - lastDoubleBotBar > 5)
                {
                    double peak = between.Max(h => h.Price);
                    if (Close[0] > peak)
                    {
                        lastDoubleBotBar = CurrentBar;
                        Fire("DBLBOT", "(heuristic) Double bottom -- two comparable swing lows near " + P((l1.Price + l2.Price) / 2) +
                            " with a close now breaking the neckline at " + P(peak) + ". Bullish confirmation.", true);
                    }
                }
            }
        }

        void CheckHeadShoulders()
        {
            // Head & shoulders: 3 swing highs, middle clearly the highest, outer two roughly symmetric;
            // neckline = line through the two intervening swing lows; confirmed on a close below it.
            if (swHighs.Count >= 3 && swLows.Count >= 2 && CurrentBar - lastHsBar > 8)
            {
                var hs = swHighs.Skip(swHighs.Count - 3).ToList();
                double lShoulder = hs[0].Price, head = hs[1].Price, rShoulder = hs[2].Price;
                double shoulderTol = Math.Max(lShoulder, rShoulder) * SwingTolerancePct * 2 / 100.0;
                if (head > lShoulder && head > rShoulder && Math.Abs(lShoulder - rShoulder) <= shoulderTol)
                {
                    var neck = swLows.Where(l => l.Bar > hs[0].Bar && l.Bar < hs[2].Bar).OrderBy(l => l.Bar).ToList();
                    if (neck.Count >= 1)
                    {
                        double neckPrice = neck.Count >= 2 ? (neck[0].Price + neck[neck.Count - 1].Price) / 2.0 : neck[0].Price;
                        if (Close[0] < neckPrice)
                        {
                            lastHsBar = CurrentBar;
                            Fire("HNS", "(heuristic) Head & shoulders -- left shoulder " + P(lShoulder) + ", head " + P(head) +
                                ", right shoulder " + P(rShoulder) + ", now closing below the neckline near " + P(neckPrice) + ". Bearish confirmation.", true);
                        }
                    }
                }
            }
            // Inverse head & shoulders on swing lows
            if (swLows.Count >= 3 && swHighs.Count >= 2 && CurrentBar - lastIhsBar > 8)
            {
                var ls = swLows.Skip(swLows.Count - 3).ToList();
                double lShoulder = ls[0].Price, head = ls[1].Price, rShoulder = ls[2].Price;
                double shoulderTol = Math.Max(lShoulder, rShoulder) * SwingTolerancePct * 2 / 100.0;
                if (head < lShoulder && head < rShoulder && Math.Abs(lShoulder - rShoulder) <= shoulderTol)
                {
                    var neck = swHighs.Where(h => h.Bar > ls[0].Bar && h.Bar < ls[2].Bar).OrderBy(h => h.Bar).ToList();
                    if (neck.Count >= 1)
                    {
                        double neckPrice = neck.Count >= 2 ? (neck[0].Price + neck[neck.Count - 1].Price) / 2.0 : neck[0].Price;
                        if (Close[0] > neckPrice)
                        {
                            lastIhsBar = CurrentBar;
                            Fire("IHNS", "(heuristic) Inverse head & shoulders -- left shoulder " + P(lShoulder) + ", head " + P(head) +
                                ", right shoulder " + P(rShoulder) + ", now closing above the neckline near " + P(neckPrice) + ". Bullish confirmation.", true);
                        }
                    }
                }
            }
        }

        void CheckTriangleWedge()
        {
            if (swHighs.Count < 4 || swLows.Count < 4) return;

            var hs = swHighs.Skip(swHighs.Count - 4).ToList();
            var ls = swLows.Skip(swLows.Count - 4).ToList();
            double hSlope = Slope(hs.Select(s => (double)s.Bar).ToArray(), hs.Select(s => s.Price).ToArray());
            double lSlope = Slope(ls.Select(s => (double)s.Bar).ToArray(), ls.Select(s => s.Price).ToArray());
            double flatBand = hs.Average(s => s.Price) * (SwingTolerancePct / 100.0) / Math.Max(1, (hs[hs.Count - 1].Bar - hs[0].Bar));

            bool hFlat = Math.Abs(hSlope) <= flatBand, lFlat = Math.Abs(lSlope) <= flatBand;
            bool hDown = hSlope < -flatBand, hUp = hSlope > flatBand;
            bool lDown = lSlope < -flatBand, lUp = lSlope > flatBand;

            double hAtCur = hs[0].Price + hSlope * (CurrentBar - hs[0].Bar);
            double lAtCur = ls[0].Price + lSlope * (CurrentBar - ls[0].Bar);

            if (hDown && lUp && CurrentBar - lastTriBar > 6)
            {
                lastTriBar = CurrentBar;
                if (Close[0] > hAtCur) Fire("TRIANGLE", "(heuristic) Symmetrical triangle -- upper and lower swing lines converging, breaking out ABOVE the upper line now.", true);
                else if (Close[0] < lAtCur) Fire("TRIANGLE", "(heuristic) Symmetrical triangle -- upper and lower swing lines converging, breaking out BELOW the lower line now.", true);
                else Fire("TRIANGLE", "(heuristic) Symmetrical triangle forming -- highs falling, lows rising, squeezing toward a breakout.", true);
            }
            else if (hFlat && lUp && CurrentBar - lastTriBar > 6)
            {
                lastTriBar = CurrentBar;
                if (Close[0] > hAtCur) Fire("TRIANGLE", "(heuristic) Ascending triangle -- flat resistance near " + P(hAtCur) + ", rising swing lows, now breaking ABOVE resistance.", true);
                else Fire("TRIANGLE", "(heuristic) Ascending triangle forming -- flat resistance near " + P(hAtCur) + " with rising swing lows underneath.", true);
            }
            else if (hDown && lFlat && CurrentBar - lastTriBar > 6)
            {
                lastTriBar = CurrentBar;
                if (Close[0] < lAtCur) Fire("TRIANGLE", "(heuristic) Descending triangle -- flat support near " + P(lAtCur) + ", falling swing highs, now breaking BELOW support.", true);
                else Fire("TRIANGLE", "(heuristic) Descending triangle forming -- falling swing highs pressing down on flat support near " + P(lAtCur) + ".", true);
            }
            else if (hUp && lUp && CurrentBar - lastWedgeBar > 6)
            {
                lastWedgeBar = CurrentBar;
                if (Close[0] < lAtCur) Fire("WEDGE", "(heuristic) Rising wedge -- both swing lines climbing together, now breaking DOWN out of it. Often bearish.", true);
                else Fire("WEDGE", "(heuristic) Rising wedge forming -- both highs and lows climbing, but the range is narrowing.", true);
            }
            else if (hDown && lDown && CurrentBar - lastWedgeBar > 6)
            {
                lastWedgeBar = CurrentBar;
                if (Close[0] > hAtCur) Fire("WEDGE", "(heuristic) Falling wedge -- both swing lines dropping together, now breaking UP out of it. Often bullish.", true);
                else Fire("WEDGE", "(heuristic) Falling wedge forming -- both highs and lows dropping, but the range is narrowing.", true);
            }
        }

        void CheckFlagPennant()
        {
            if (CurrentBar < 20 || CurrentBar - lastFlagBar <= 10) return;
            double a = atr[0]; if (a <= 0) return;
            int poleBars = 6;
            // "pole": a strong, mostly one-directional move over the last poleBars bars
            double poleMove = Close[0] - Close[poleBars];
            if (Math.Abs(poleMove) < PoleAtrMultiple * a) return;
            bool poleUp = poleMove > 0;

            // consolidation channel: look at the last few bars before "now" for a tight range vs ATR
            int chanBars = Math.Min(10, CurrentBar - poleBars - 1);
            if (chanBars < 4) return;
            double chanHigh = double.MinValue, chanLow = double.MaxValue;
            for (int i = 0; i < chanBars; i++) { chanHigh = Math.Max(chanHigh, High[i]); chanLow = Math.Min(chanLow, Low[i]); }
            double chanRange = chanHigh - chanLow;
            if (chanRange > FlagMaxAtrMultiple * a) return;

            lastFlagBar = CurrentBar;
            if (poleUp && Close[0] > chanHigh)
                Fire("FLAG", "(heuristic) Bull flag/pennant -- sharp move up, then a tight sideways channel, now breaking ABOVE the channel at " + P(chanHigh) + ". Continuation tell.", true);
            else if (!poleUp && Close[0] < chanLow)
                Fire("FLAG", "(heuristic) Bear flag/pennant -- sharp move down, then a tight sideways channel, now breaking BELOW the channel at " + P(chanLow) + ". Continuation tell.", true);
            else
                Fire("FLAG", "(heuristic) " + (poleUp ? "Bull" : "Bear") + " flag/pennant forming -- a sharp " + (poleUp ? "up" : "down") +
                    " move followed by a tight consolidation channel (" + P(chanLow) + " - " + P(chanHigh) + ").", true);
        }

        void CheckCupAndHandle()
        {
            int lb = CupLookbackBars;
            if (CurrentBar < lb + 10 || CurrentBar - lastCupBar <= 15) return;

            // left rim: the highest close in the first third of the lookback window
            int thirdW = lb / 3;
            double leftRim = double.MinValue; int leftRimBar = -1;
            for (int i = lb; i >= lb - thirdW; i--) { if (High[i] > leftRim) { leftRim = High[i]; leftRimBar = i; } }

            // cup low: the lowest low between the left rim and roughly 2/3 through the window (the rounded bottom)
            double cupLow = double.MaxValue;
            for (int i = leftRimBar - 1; i >= lb / 3; i--) cupLow = Math.Min(cupLow, Low[i]);
            double cupDepth = leftRim - cupLow;
            if (cupDepth <= 0) return;

            // right rim: price must recover back to within CupRimTolerance of the left rim, inside the remaining window
            double rimTol = leftRim * (SwingTolerancePct * 2) / 100.0;
            double rightRim = double.MinValue; int rightRimBar = -1;
            for (int i = lb / 3 - 1; i >= 8; i--) { if (High[i] > rightRim) { rightRim = High[i]; rightRimBar = i; } }
            if (rightRim < leftRim - rimTol) return; // never made it back up -- no cup

            // handle: a shallow pullback after the right rim, shallower than the cup itself
            double handleLow = double.MaxValue;
            for (int i = rightRimBar - 1; i >= 0; i--) handleLow = Math.Min(handleLow, Low[i]);
            double handleDepth = rightRim - handleLow;
            if (handleDepth <= 0 || handleDepth > cupDepth * (HandleMaxRetracePct / 100.0) * 2) return;

            // confirmation: close breaking back above the rim
            double rimLevel = Math.Max(leftRim, rightRim);
            if (Close[0] > rimLevel)
            {
                lastCupBar = CurrentBar;
                Fire("CUPHANDLE", "(heuristic) Possible cup & handle -- rounded recovery back to " + P(rimLevel) +
                    " after a " + P(cupDepth) + "-point cup, shallow handle pullback, now breaking out above the rim. Bullish continuation tell -- this one is the fuzziest call this panel makes, treat it as a lead not a signal.", true);
            }
        }

        // ------------------------------------------------------------------ volume features
        void CheckVolumeSignals()
        {
            if (volAvg == null || volAvg.Count < 1) return;
            double avgVol = volAvg[0];
            if (avgVol <= 0) return;
            double relVol = Volume[0] / avgVol;

            // Volume climax/exhaustion -- an unusually large bar landing right at a fresh swing extreme
            bool atFreshHigh = swHighs.Count > 0 && swHighs[swHighs.Count - 1].Bar == CurrentBar;
            bool atFreshLow = swLows.Count > 0 && swLows[swLows.Count - 1].Bar == CurrentBar;
            if (relVol >= VolClimaxMultiple && (atFreshHigh || atFreshLow) && CurrentBar - lastVolClimaxBar > 5)
            {
                lastVolClimaxBar = CurrentBar;
                Fire("VOLCLIMAX", "Volume climax -- " + relVol.ToString("0.0", CultureInfo.InvariantCulture) + "x average volume right at a fresh swing " +
                    (atFreshHigh ? "high" : "low") + ". Possible exhaustion/reversal.", true);
            }

            // Volume-price divergence -- a new swing extreme made on noticeably lighter volume than the prior one
            if (swHighs.Count >= 2 && swHighs[swHighs.Count - 1].Bar == CurrentBar)
            {
                var cur = swHighs[swHighs.Count - 1]; var prev = swHighs[swHighs.Count - 2];
                if (cur.Price > prev.Price && prev.Vol > 0 && cur.Vol < prev.Vol * 0.7 && CurrentBar - lastVolDivBar > 5)
                {
                    lastVolDivBar = CurrentBar;
                    Fire("VOLDIV", "Bearish volume divergence -- price made a higher high but on lighter volume than the prior swing high. Momentum may be fading.", true);
                }
            }
            if (swLows.Count >= 2 && swLows[swLows.Count - 1].Bar == CurrentBar)
            {
                var cur = swLows[swLows.Count - 1]; var prev = swLows[swLows.Count - 2];
                if (cur.Price < prev.Price && prev.Vol > 0 && cur.Vol < prev.Vol * 0.7 && CurrentBar - lastVolDivBar > 5)
                {
                    lastVolDivBar = CurrentBar;
                    Fire("VOLDIV", "Bullish volume divergence -- price made a lower low but on lighter volume than the prior swing low. Selling pressure may be fading.", true);
                }
            }

            // Low-volume pullback -- moves against the trend on well-below-average volume (healthy, not a reversal)
            bool pullbackDown = trendState.StartsWith("Uptrend") && Close[0] < Open[0];
            bool pullbackUp = trendState.StartsWith("Downtrend") && Close[0] > Open[0];
            if ((pullbackDown || pullbackUp) && relVol <= LowVolPullbackMultiple && CurrentBar - lastLowVolPbBar > 5)
            {
                lastLowVolPbBar = CurrentBar;
                Fire("LOWVOLPB", "Low-volume pullback -- only " + relVol.ToString("0.0", CultureInfo.InvariantCulture) +
                    "x average volume against the trend. Reads as healthy continuation, not a reversal.", true);
            }
        }

        // ------------------------------------------------------------------ liquidity sweep / stop-run
        void CheckLiquiditySweep()
        {
            if (CurrentBar - lastSweepBar <= 5) return;
            double buf = 2 * tick;
            if (swHighs.Count > 0)
            {
                var h = swHighs[swHighs.Count - 1];
                if (h.Bar < CurrentBar && High[0] > h.Price + buf && Close[0] < h.Price)
                {
                    lastSweepBar = CurrentBar;
                    Fire("SWEEP", "Liquidity sweep -- wicked above the prior swing high " + P(h.Price) + " then closed back below it. Classic stop-run/trap; watch for a reversal.", true);
                    return;
                }
            }
            if (swLows.Count > 0)
            {
                var l = swLows[swLows.Count - 1];
                if (l.Bar < CurrentBar && Low[0] < l.Price - buf && Close[0] > l.Price)
                {
                    lastSweepBar = CurrentBar;
                    Fire("SWEEP", "Liquidity sweep -- wicked below the prior swing low " + P(l.Price) + " then closed back above it. Classic stop-run/trap; watch for a reversal.", true);
                }
            }
        }

        // ------------------------------------------------------------------ order flow call-outs (absorption / traps / delta divergence)
        // All of this needs real tick-by-tick buy/sell classification, so it's gated on haveLiveDelta --
        // nothing fires here on pure historical bars, same as the rest of the order-flow feature.
        void CheckOrderFlowSignals()
        {
            if (volAvg == null || volAvg.Count < 1) return;
            double avgVol = volAvg[0];
            if (avgVol <= 0) return;
            double deltaThresh = avgVol * OrderFlowDeltaMultiple;
            if (deltaThresh <= 0) return;

            // Absorption -- a big one-sided delta that barely moved price. Someone is likely absorbing the
            // aggressive flow with resting size at this level instead of letting it push price through.
            double body = Math.Abs(Close[0] - Open[0]);
            double a = atr != null ? atr[0] : 0;
            if (a > 0 && Math.Abs(lastBarDelta) >= deltaThresh && body <= 0.3 * a && CurrentBar - lastAbsorbBar > 5)
            {
                lastAbsorbBar = CurrentBar;
                Fire("ABSORB", "Absorption -- big net " + (lastBarDelta > 0 ? "buying" : "selling") + " (delta " + FmtDelta(lastBarDelta) +
                    ") but price barely moved. Someone may be absorbing the flow at this level.", true);
            }

            // Trap -- a fresh swing extreme made AGAINST what the order flow actually supports (new high on
            // net selling, new low on net buying). Classic sign the breakout crowd is on the wrong side.
            bool atFreshHigh = swHighs.Count > 0 && swHighs[swHighs.Count - 1].Bar == CurrentBar;
            bool atFreshLow = swLows.Count > 0 && swLows[swLows.Count - 1].Bar == CurrentBar;
            if (atFreshHigh && lastBarDelta <= -deltaThresh && CurrentBar - lastTrapBar > 5)
            {
                lastTrapBar = CurrentBar;
                Fire("TRAP", "Possible bull trap -- fresh swing high made on net selling (delta " + FmtDelta(lastBarDelta) + "). Buyers chasing this high may be trapped.", true);
            }
            else if (atFreshLow && lastBarDelta >= deltaThresh && CurrentBar - lastTrapBar > 5)
            {
                lastTrapBar = CurrentBar;
                Fire("TRAP", "Possible bear trap -- fresh swing low made on net buying (delta " + FmtDelta(lastBarDelta) + "). Sellers chasing this low may be trapped.", true);
            }

            // Delta divergence -- a new swing extreme that isn't backed by cumulative delta moving the same
            // way (price vs. the flow actually behind it), as distinct from the volume-based divergence above.
            if (swHighs.Count >= 2 && swHighs[swHighs.Count - 1].Bar == CurrentBar)
            {
                var cur = swHighs[swHighs.Count - 1]; var prev = swHighs[swHighs.Count - 2];
                if (cur.Price > prev.Price && cur.Delta < prev.Delta && CurrentBar - lastDeltaDivBar > 5)
                {
                    lastDeltaDivBar = CurrentBar;
                    Fire("DELTADIV", "Bearish delta divergence -- price made a higher high but cumulative delta made a lower high. Real buying pressure isn't confirming the move.", true);
                }
            }
            if (swLows.Count >= 2 && swLows[swLows.Count - 1].Bar == CurrentBar)
            {
                var cur = swLows[swLows.Count - 1]; var prev = swLows[swLows.Count - 2];
                if (cur.Price < prev.Price && cur.Delta > prev.Delta && CurrentBar - lastDeltaDivBar > 5)
                {
                    lastDeltaDivBar = CurrentBar;
                    Fire("DELTADIV", "Bullish delta divergence -- price made a lower low but cumulative delta made a higher low. Real selling pressure isn't confirming the move.", true);
                }
            }
        }

        // ------------------------------------------------------------------ VWAP + bands (session-anchored, drawn as real plots)
        void UpdateVwap()
        {
            if (!UseVwap) { Values[0][0] = double.NaN; Values[1][0] = double.NaN; Values[2][0] = double.NaN; Values[3][0] = double.NaN; Values[4][0] = double.NaN; return; }

            if (sessionIterator == null) sessionIterator = new SessionIterator(Bars);
            if (sessionIterator.IsNewSession(Time[0], false))
            {
                sessionIterator.GetNextSession(Time[0], true);
                cumPV = 0; cumVol = 0; cumPV2 = 0;
            }

            double typical = (High[0] + Low[0] + Close[0]) / 3.0;
            double vol = Math.Max(0, Volume[0]);
            cumPV += typical * vol;
            cumVol += vol;
            cumPV2 += typical * typical * vol;

            if (cumVol > 0)
            {
                vwapVal = cumPV / cumVol;
                double variance = Math.Max(0, cumPV2 / cumVol - vwapVal * vwapVal);
                double sd = Math.Sqrt(variance);
                vwapDevUp1 = vwapVal + sd; vwapDevDn1 = vwapVal - sd;
                vwapDevUp2 = vwapVal + 2 * sd; vwapDevDn2 = vwapVal - 2 * sd;

                Values[0][0] = vwapVal;
                Values[1][0] = vwapDevUp1;
                Values[2][0] = vwapDevDn1;
                Values[3][0] = vwapDevUp2;
                Values[4][0] = vwapDevDn2;
            }
            else
            {
                Values[0][0] = double.NaN; Values[1][0] = double.NaN; Values[2][0] = double.NaN; Values[3][0] = double.NaN; Values[4][0] = double.NaN;
            }
        }

        // ------------------------------------------------------------------ higher-timeframe trend alignment
        // Lightweight on purpose -- just a momentum read on the HTF series' own closes (now vs. 5 HTF bars
        // back), not a full swing-structure replica. Reading Closes[1]/Highs[1]/Lows[1] from the primary
        // series' own OnBarUpdate always reflects that series' most recently CLOSED bar, which is exactly
        // what we want for a "what's the bigger picture doing" panel line.
        void UpdateHtfTrend()
        {
            if (!UseHtfAlignment) return;
            if (BarsArray.Length < 2 || CurrentBars.Length < 2 || CurrentBars[1] < 6) { htfTrendState = "warming up..."; return; }

            double now = Closes[1][0];
            double then = Closes[1][5];
            double threshold = Math.Max(tick * 4, Math.Abs(now) * 0.0005);

            if (now > then + threshold) htfTrendState = "Bullish bias";
            else if (now < then - threshold) htfTrendState = "Bearish bias";
            else htfTrendState = "Range / mixed";
        }

        // ------------------------------------------------------------------ volatility regime
        void UpdateVolRegime()
        {
            if (!UseVolatilityRegime || atrBaseline == null || atrBaseline.Count < 1) { volRegimeState = "warming up..."; return; }
            double baseline = atrBaseline[0];
            if (baseline <= 0) { volRegimeState = "warming up..."; return; }
            double ratio = atr[0] / baseline;

            if (ratio >= 1.4) volRegimeState = "Expanding (" + ratio.ToString("0.00", CultureInfo.InvariantCulture) + "x normal) -- wider stops, bigger moves";
            else if (ratio <= 0.7) volRegimeState = "Contracting (" + ratio.ToString("0.00", CultureInfo.InvariantCulture) + "x normal) -- quiet tape, moves may be smaller/false";
            else volRegimeState = "Normal (" + ratio.ToString("0.00", CultureInfo.InvariantCulture) + "x)";
        }

        // ------------------------------------------------------------------ order flow helpers
        string FmtDelta(double d)
        {
            string sign = d > 0 ? "+" : "";
            return sign + Math.Round(d).ToString("#,0", CultureInfo.InvariantCulture);
        }

        string DeltaNote(bool bullishBreak)
        {
            if (!UseOrderFlow || !haveLiveDelta) return "";
            bool agrees = bullishBreak ? lastBarDelta > 0 : lastBarDelta < 0;
            return agrees
                ? " Delta confirms (bar delta " + FmtDelta(lastBarDelta) + ")."
                : " Delta diverges (bar delta " + FmtDelta(lastBarDelta) + ") -- may lack real buying/selling behind it, watch for a fakeout.";
        }

        // raw trade/quote prints -- fires on live or tick-replay data regardless of Calculate mode. Classified
        // by the quote rule (at/through the last known ask = buy, at/through the last known bid = sell),
        // falling back to the tick rule (vs. the prior trade price) when a print doesn't cleanly sit at or
        // through either side.
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (!UseOrderFlow) return;

            if (e.MarketDataType == MarketDataType.Bid) { lastBid = e.Price; return; }
            if (e.MarketDataType == MarketDataType.Ask) { lastAsk = e.Price; return; }
            if (e.MarketDataType != MarketDataType.Last) return;

            double price = e.Price;
            double vol = e.Volume;
            bool buy;
            if (!double.IsNaN(lastAsk) && price >= lastAsk) buy = true;
            else if (!double.IsNaN(lastBid) && price <= lastBid) buy = false;
            else buy = double.IsNaN(lastTradePrice) || price >= lastTradePrice;

            lastTradePrice = price;

            if (buy) { barBuyVol += vol; cumDelta += vol; }
            else { barSellVol += vol; cumDelta -= vol; }

            haveLiveDelta = true;
        }

        // ------------------------------------------------------------------ per-bar panel summary refresh
        // Unlike Fire(), which only updates the panel when a specific pattern call fires, these lines (volume,
        // VWAP, order flow, HTF, volatility regime) refresh every bar regardless, so the panel never shows
        // stale readings between call-outs.
        void RefreshSummaryPanel()
        {
            if (win == null) return;

            double rv = (volAvg != null && volAvg.Count > 0 && volAvg[0] > 0) ? Volume[0] / volAvg[0] : double.NaN;
            string volTxt = double.IsNaN(rv) ? "Volume: warming up..." : "Volume: " + rv.ToString("0.0", CultureInfo.InvariantCulture) + "x average";
            string vwapTxt = cumVol > 0
                ? "VWAP: " + P(vwapVal) + " (" + (Close[0] > vwapVal ? "price above" : Close[0] < vwapVal ? "price below" : "at VWAP") + ")"
                : "VWAP: warming up...";
            string flowTxt = haveLiveDelta
                ? "Order flow: bar delta " + FmtDelta(lastBarDelta) + " | cum (since load) " + FmtDelta(cumDelta)
                : "Order flow: waiting for live ticks...";
            string htfTxt = "HTF (" + HtfMinutes + "m): " + htfTrendState;
            string regimeTxt = "Volatility: " + volRegimeState;

            bool useVol = UseVolumeFeatures, useVwapF = UseVwap, useFlow = UseOrderFlow, useHtf = UseHtfAlignment, useRegime = UseVolatilityRegime;

            win.Dispatcher.InvokeAsync(() =>
            {
                if (useVol && volLine != null) volLine.Text = volTxt;
                if (useVwapF && vwapLine != null) vwapLine.Text = vwapTxt;
                if (useFlow && flowLine != null) flowLine.Text = flowTxt;
                if (useHtf && htfLine != null) htfLine.Text = htfTxt;
                if (useRegime && volRegimeLine != null) volRegimeLine.Text = regimeTxt;
            });
        }

        static double Slope(double[] x, double[] y)
        {
            int n = x.Length; double sx = x.Sum(), sy = y.Sum(), sxy = 0, sxx = 0;
            for (int i = 0; i < n; i++) { sxy += x[i] * y[i]; sxx += x[i] * x[i]; }
            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < 1e-9) return 0;
            return (n * sxy - sx * sy) / denom;
        }

        string P(double px) { return Instrument.MasterInstrument.RoundToTickSize(px).ToString("0.##", CultureInfo.InvariantCulture); }
        string Pct(double a, double b) { return b <= 0 ? "0" : Math.Round(a / b * 100.0).ToString(CultureInfo.InvariantCulture); }

        // ------------------------------------------------------------------ call-out feed + chart tags
        void Fire(string tag, string text, bool lowConfidence)
        {
            lastPatternText = text;
            string stamp = Use24HourTime
                ? Time[0].ToString("HH:mm", CultureInfo.InvariantCulture)
                : Time[0].ToString("h:mm tt", CultureInfo.InvariantCulture);
            var brush = lowConfidence ? Amber : (tag == "BOS" || tag == "CHOCH" || tag == "STRUCTURE") ? Accent :
                        (text.IndexOf("ullish", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("buying", StringComparison.OrdinalIgnoreCase) >= 0) ? Green :
                        (text.IndexOf("earish", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("selling", StringComparison.OrdinalIgnoreCase) >= 0) ? Red : Text;

            if (ShowChartTags)
            {
                string drawTag = "sma_" + tag + "_" + CurrentBar;
                bool above = brush == Red || tag == "SHOOTSTAR" || tag == "HNS" || tag == "DBLTOP" || tag == "EVESTAR" || tag == "3CROWS";

                // several patterns can fire on the same bar -- stack their chart tags vertically
                // (one slot per prior tag on this bar, same side) instead of drawing them all at
                // the same price offset, where they'd just pile up on top of each other.
                if (tagStackBar != CurrentBar) { tagStackBar = CurrentBar; tagAboveCount = 0; tagBelowCount = 0; }
                int stackIndex = above ? tagAboveCount++ : tagBelowCount++;
                double offset = (6 + stackIndex * 14) * tick;
                double y = above ? High[0] + offset : Low[0] - offset;

                Draw.Text(this, drawTag, false, tag, 0, y, 0, brush, new SimpleFont("Arial", 10), TextAlignment.Center, null, null, 0);
            }

            if (win == null) return;

            // snapshot everything we need from the series/state now, on this (calculation) thread --
            // OnBarUpdate can advance to a new bar before the dispatched action below actually runs on
            // the UI thread, so touching Close[]/trendState/srZones from inside that lambda would risk
            // reading a bar ahead of what this particular call-out was actually about.
            string curTrend = trendState;
            bool haveBos = !double.IsNaN(lastBosUpLevel) || !double.IsNaN(lastBosDownLevel);
            double closeNow = Close[0];
            var nearRes = srZones.Where(z => z.Price > closeNow).OrderBy(z => z.Price).FirstOrDefault();
            var nearSup = srZones.Where(z => z.Price < closeNow).OrderByDescending(z => z.Price).FirstOrDefault();
            string resTxt = nearRes != null ? P(nearRes.Price) : "--";
            string supTxt = nearSup != null ? P(nearSup.Price) : "--";

            win.Dispatcher.InvokeAsync(() =>
            {
                trendLine.Text = "Trend: " + curTrend;
                structureLine.Text = "Last structure call: " + (haveBos ? "see feed below" : "none yet");
                patternLine.Text = "Last call-out: " + text;
                srLine.Text = "S/R: support " + supTxt + " | resistance " + resTxt;

                var line = new TextBlock
                {
                    Text = stamp + "  [" + tag + "]  " + text,
                    Foreground = brush,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(6, 3, 6, 3)
                };
                feedStack.Children.Insert(0, line);
                feedCount++;
                while (feedStack.Children.Count > FeedMaxLines) feedStack.Children.RemoveAt(feedStack.Children.Count - 1);
                feedScroll.ScrollToTop();
            });
        }

        // ------------------------------------------------------------------ floating panel UI
        void BuildPanel()
        {
            var root = new Border
            {
                Background = Bg,
                BorderBrush = Edge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Width = 340
            };
            var outer = new StackPanel();
            root.Child = outer;

            var header = new Border { Background = Card, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(6, 6, 0, 0), Cursor = Cursors.SizeAll };
            var headerText = new TextBlock { Text = "SNEAKY MARKET ADVISOR", Foreground = Text, FontWeight = FontWeights.Bold, FontSize = 12 };
            header.Child = headerText;
            header.MouseLeftButtonDown += Header_MouseDown;
            outer.Children.Add(header);

            var summary = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
            trendLine = NewLine(); structureLine = NewLine(); patternLine = NewLine(); srLine = NewLine();
            trendLine.Text = "Trend: warming up...";
            structureLine.Text = "Last structure call: none yet";
            patternLine.Text = "Last call-out: none yet";
            srLine.Text = "S/R: support -- | resistance --";
            summary.Children.Add(trendLine); summary.Children.Add(structureLine); summary.Children.Add(patternLine); summary.Children.Add(srLine);

            // these only get a line in the panel if their feature is actually turned on -- RefreshSummaryPanel
            // mirrors the same Use* checks so a disabled feature's (null) TextBlock is never touched.
            if (UseVolumeFeatures) { volLine = NewLine(); volLine.Text = "Volume: warming up..."; summary.Children.Add(volLine); }
            if (UseVwap) { vwapLine = NewLine(); vwapLine.Text = "VWAP: warming up..."; summary.Children.Add(vwapLine); }
            if (UseOrderFlow) { flowLine = NewLine(); flowLine.Text = "Order flow: waiting for live ticks..."; summary.Children.Add(flowLine); }
            if (UseHtfAlignment) { htfLine = NewLine(); htfLine.Text = "HTF (" + HtfMinutes + "m): warming up..."; summary.Children.Add(htfLine); }
            if (UseVolatilityRegime) { volRegimeLine = NewLine(); volRegimeLine.Text = "Volatility: warming up..."; summary.Children.Add(volRegimeLine); }

            outer.Children.Add(summary);

            outer.Children.Add(new Border { Height = 1, Background = Edge, Margin = new Thickness(0, 2, 0, 2) });

            var feedLabel = new TextBlock { Text = "CALL-OUT FEED", Foreground = Muted, FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(8, 4, 8, 2) };
            outer.Children.Add(feedLabel);

            feedStack = new StackPanel();
            feedScroll = new ScrollViewer { MaxHeight = 260, Content = feedStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            outer.Children.Add(feedScroll);

            win = new Window
            {
                Title = "Sneaky Market Advisor",
                Content = root,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = false,
                Left = 40,
                Top = 40,
                Owner = Window.GetWindow(ChartControl)
            };
            win.Show();
        }

        TextBlock NewLine() { return new TextBlock { Foreground = Text, FontSize = 12, Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap }; }

        void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Window.DragMove() hands the drag off to the OS's own window-move loop, which tracks
            // the cursor 1:1. The previous hand-rolled version computed the delta via
            // e.GetPosition(null), which is relative to this same floating window -- so as the
            // window moved, the coordinate space the delta was measured against moved with it,
            // producing the laggy/jumpy drag that was reported.
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { win.DragMove(); } catch { }
            }
        }

        // ------------------------------------------------------------------ properties
        [NinjaScriptProperty, Range(1, 20), Display(Name = "Swing strength (bars each side)", GroupName = "1. Structure", Order = 0)]
        public int SwingStrength { get; set; }
        [NinjaScriptProperty, Range(1, 200), Display(Name = "S/R cluster distance (ticks)", GroupName = "1. Structure", Order = 1)]
        public int SrClusterTicks { get; set; }
        [NinjaScriptProperty, Display(Name = "Use candlestick patterns", GroupName = "2. Patterns", Order = 0)]
        public bool UseCandlePatterns { get; set; }
        [NinjaScriptProperty, Display(Name = "Use market structure (trend/BOS/CHOCH)", GroupName = "2. Patterns", Order = 1)]
        public bool UseMarketStructure { get; set; }
        [NinjaScriptProperty, Display(Name = "Use classic chart patterns", GroupName = "2. Patterns", Order = 2)]
        public bool UseClassicPatterns { get; set; }
        [NinjaScriptProperty, Display(Name = "  Double top / bottom", GroupName = "2. Patterns", Order = 3)]
        public bool UseDoubleTopBottom { get; set; }
        [NinjaScriptProperty, Display(Name = "  Head & shoulders / inverse", GroupName = "2. Patterns", Order = 4)]
        public bool UseHeadShoulders { get; set; }
        [NinjaScriptProperty, Display(Name = "  Triangle / wedge", GroupName = "2. Patterns", Order = 5)]
        public bool UseTriangleWedge { get; set; }
        [NinjaScriptProperty, Display(Name = "  Flag / pennant", GroupName = "2. Patterns", Order = 6)]
        public bool UseFlagPennant { get; set; }
        [NinjaScriptProperty, Display(Name = "  Cup & handle (fuzziest)", GroupName = "2. Patterns", Order = 7)]
        public bool UseCupHandle { get; set; }
        [NinjaScriptProperty, Range(0.01, 5.0), Display(Name = "Swing tolerance (%)", GroupName = "3. Tolerances", Order = 0)]
        public double SwingTolerancePct { get; set; }
        [NinjaScriptProperty, Range(1, 100), Display(Name = "Min retrace for double top/bottom (%)", GroupName = "3. Tolerances", Order = 1)]
        public double MinRetracePct { get; set; }
        [NinjaScriptProperty, Range(1, 100), Display(Name = "ATR period", GroupName = "3. Tolerances", Order = 2)]
        public int AtrPeriod { get; set; }
        [NinjaScriptProperty, Range(0.1, 20.0), Display(Name = "Flag pole size (x ATR)", GroupName = "3. Tolerances", Order = 3)]
        public double PoleAtrMultiple { get; set; }
        [NinjaScriptProperty, Range(0.1, 20.0), Display(Name = "Flag channel max width (x ATR)", GroupName = "3. Tolerances", Order = 4)]
        public double FlagMaxAtrMultiple { get; set; }
        [NinjaScriptProperty, Range(30, 500), Display(Name = "Cup lookback (bars)", GroupName = "3. Tolerances", Order = 5)]
        public int CupLookbackBars { get; set; }
        [NinjaScriptProperty, Range(5, 200), Display(Name = "Cup min bars", GroupName = "3. Tolerances", Order = 6)]
        public int CupMinBars { get; set; }
        [NinjaScriptProperty, Range(1, 100), Display(Name = "Handle max retrace (% of cup depth)", GroupName = "3. Tolerances", Order = 7)]
        public double HandleMaxRetracePct { get; set; }
        [NinjaScriptProperty, Display(Name = "Show floating panel", GroupName = "4. Display", Order = 0)]
        public bool ShowPanel { get; set; }
        [NinjaScriptProperty, Display(Name = "Show tags on chart", GroupName = "4. Display", Order = 1)]
        public bool ShowChartTags { get; set; }
        [NinjaScriptProperty, Range(10, 1000), Display(Name = "Feed max lines", GroupName = "4. Display", Order = 2)]
        public int FeedMaxLines { get; set; }
        [NinjaScriptProperty, Display(Name = "Use 24-hour time in feed (off = 12-hour AM/PM)", GroupName = "4. Display", Order = 3)]
        public bool Use24HourTime { get; set; }

        [NinjaScriptProperty, Display(Name = "Use volume features (climax/divergence/low-vol pullback)", GroupName = "5. Volume & Order Flow", Order = 0)]
        public bool UseVolumeFeatures { get; set; }
        [NinjaScriptProperty, Range(5, 200), Display(Name = "Volume average period", GroupName = "5. Volume & Order Flow", Order = 1)]
        public int VolumeAvgPeriod { get; set; }
        [NinjaScriptProperty, Range(1.2, 10.0), Display(Name = "Volume climax multiple (x average)", GroupName = "5. Volume & Order Flow", Order = 2)]
        public double VolClimaxMultiple { get; set; }
        [NinjaScriptProperty, Range(0.1, 1.0), Display(Name = "Low-volume pullback multiple (x average)", GroupName = "5. Volume & Order Flow", Order = 3)]
        public double LowVolPullbackMultiple { get; set; }
        [NinjaScriptProperty, Display(Name = "Use order flow (true bid/ask delta, live only)", GroupName = "5. Volume & Order Flow", Order = 4)]
        public bool UseOrderFlow { get; set; }
        [NinjaScriptProperty, Range(0.05, 2.0), Display(Name = "  Order flow delta threshold (x average volume)", GroupName = "5. Volume & Order Flow", Order = 5)]
        public double OrderFlowDeltaMultiple { get; set; }

        [NinjaScriptProperty, Display(Name = "Use VWAP + bands", GroupName = "6. VWAP / HTF / Volatility", Order = 0)]
        public bool UseVwap { get; set; }
        [NinjaScriptProperty, Display(Name = "Use liquidity sweep / stop-run detection", GroupName = "6. VWAP / HTF / Volatility", Order = 1)]
        public bool UseLiquiditySweep { get; set; }
        [NinjaScriptProperty, Display(Name = "Use higher-timeframe trend alignment", GroupName = "6. VWAP / HTF / Volatility", Order = 2)]
        public bool UseHtfAlignment { get; set; }
        [NinjaScriptProperty, Range(2, 240), Display(Name = "  HTF minutes", GroupName = "6. VWAP / HTF / Volatility", Order = 3)]
        public int HtfMinutes { get; set; }
        [NinjaScriptProperty, Display(Name = "Use volatility regime (ATR expansion/contraction)", GroupName = "6. VWAP / HTF / Volatility", Order = 4)]
        public bool UseVolatilityRegime { get; set; }
        [NinjaScriptProperty, Range(10, 300), Display(Name = "  Volatility regime lookback (bars)", GroupName = "6. VWAP / HTF / Volatility", Order = 5)]
        public int VolRegimeLookback { get; set; }
    }
}
