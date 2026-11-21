#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// ═══════════════════════════════════════════════════════════════════════════
// PORTED FROM TRADINGVIEW: "Squeeze Engine PRO [CandleChasers]" by CandleChasers
// (open-source, Pine Script v6). Indicator and signal research tool — NOT a
// backtested strategy. Default settings are starting points, NOT optimized or
// guaranteed. The "virtual trade" lifecycle below is a visual/alerting aid
// only; it does not place real orders (use a Strategy for that).
//
// PINE -> NINJASCRIPT NOTES (every place this port intentionally differs from
// the literal Pine source):
//  1. Pine's string timeframe ("240", "D", ...) has no direct NinjaScript
//     equivalent for a secondary series. The higher-timeframe trend filter is
//     ported using an NT8 secondary minute-based series (AddDataSeries), with
//     "Automatic HTF" computed as (chart minutes x style multiplier) exactly
//     like Pine's automaticTF, and "Manual HTF" expressed directly in minutes
//     instead of a Pine timeframe string. This requires a minute-based chart;
//     if the chart is not minute-based (e.g. Day/Range/Tick/Volume bars), the
//     HTF filter is automatically disabled with a one-time Print warning,
//     mirroring Pine's own chart.is_standard guard.
//  2. The HTF close/EMA are read one bar further back on the secondary series
//     (offset 1 on BarsInProgress==1) to stay safely non-repainting, mirroring
//     Pine's close[1] read inside request.security(..., lookahead_on).
//  3. ADX/DI+/DI- are computed with a hand-written standard Wilder smoothing
//     (rather than trusting an assumed NinjaScript built-in's exact plot
//     names), so the math is fully auditable here.
//  4. Pine's linreg-based "LazyBear" PRO/Early momentum (credit: LazyBear,
//     original author of the supplied SQZMOM_LB momentum code; used here
//     under the project's personal-use, non-commercial terms) is ported using
//     NinjaScript's built-in LinReg() method against a private (close-anchor)
//     series — the math is identical, just fed through NinjaScript's own
//     linear-regression engine instead of re-deriving slope/intercept by hand.
//  5. Pine's three `force_overlay=true` virtual entry/stop/target plots have
//     no equivalent in NinjaScript for a separate-pane indicator (a plot
//     cannot jump to the price panel). Those three levels are instead shown
//     in the "TRADE MAP" dashboard row and remain available in the Data Box
//     via three (deliberately invisible) plots, matching Pine's
//     display.data_window-only plots.
//  6. Pine's `bgcolor()` compression background has no per-bar panel
//     background equivalent for a NinjaScript oscillator; "Show background"
//     instead deepens the compression-level dot's opacity while a squeeze is
//     active.
//  7. Pine's 11-row `table` dashboard is ported as a single multi-line
//     Draw.TextFixed block (NinjaScript has no native per-cell table
//     primitive), following this repo's established convention.
//  8. Pine's shape.labelup/labeldown BUY/SELL markers are ported as
//     Draw.Text flag labels; Pine's shape.xcross EXIT markers are ported as
//     Draw.Text "X" markers (NinjaScript has no native X-cross marker).
// ═══════════════════════════════════════════════════════════════════════════
// Declared in the global namespace (not inside NinjaTrader.NinjaScript.Indicators)
// so NinjaTrader's auto-generated Strategies/MarketAnalyzerColumns accessor
// wrappers can resolve these types unqualified -- same convention already used
// by SneakyTradeHUD, SneakyInsaneToOsc_v1_0_0, SneakyWix9ineRanges and
// TONebula elsewhere in this repo, to avoid CS0246 in the generated code region.
public enum CcTradeStyle { Scalping, DayTrading, SwingTrading }
public enum CcSignalMode { EarlierSignals, Balanced, StrongerConfirmation }
public enum CcEngineMode { Classic, Pro, Early }

// Exact dashboard display text for the three enums above, matching the Pine
// source's own input.string option labels verbatim (tradeStyle/signalMode/
// engineMode options on lines 10-12) -- the bare enum .ToString() ("DayTrading",
// "Pro") does not match Pine's dashboard text ("Day Trading", "CandleChasers PRO").
public static class CcDisplay
{
    public static string Of(CcTradeStyle v)
    {
        switch (v)
        {
            case CcTradeStyle.DayTrading: return "Day Trading";
            case CcTradeStyle.SwingTrading: return "Swing Trading";
            default: return "Scalping";
        }
    }
    public static string Of(CcSignalMode v)
    {
        switch (v)
        {
            case CcSignalMode.Balanced: return "Balanced";
            case CcSignalMode.StrongerConfirmation: return "Stronger Confirmation";
            default: return "Earlier Signals";
        }
    }
    public static string Of(CcEngineMode v)
    {
        switch (v)
        {
            case CcEngineMode.Pro: return "CandleChasers PRO";
            case CcEngineMode.Early: return "CandleChasers Early";
            default: return "CandleChasers Classic";
        }
    }
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakySqueezeEngineProCc_v1_0_0 : Indicator
    {
        // ───────────────────────── persistent state ─────────────────────────
        private Series<double> srcMinusAnchor, rawMomentumSeries, velocitySeries;
        private Series<double> smTR, smPlusDM, smMinusDM, adxSeries;

        private bool prevInSqueeze, htfSupported;
        private int episode, squeezeDuration, lastSqueezeDuration, lastReleaseBar = -1;
        private int htfMinutes, chartMinutes;
        private bool htfWarned;

        private double priorLowPrice = double.NaN, priorLowMom = double.NaN; private int priorLowBar = -1;
        private double priorHighPrice = double.NaN, priorHighMom = double.NaN; private int priorHighBar = -1;

        // virtual trade lifecycle
        private int direction; private double entryPx = double.NaN, stopPx = double.NaN, targetPx = double.NaN, extremePx = double.NaN;
        private int lastEntryBar = -1, lastExitBar = -1, tradedEpisode = -1;
        private string lastExitReason = "NONE";

        // Custom-rendered dashboard table (drawn in OnRender, in THIS
        // indicator's own panel) -- cached each bar so OnRender has no
        // OnBarUpdate-only state to recompute every paint.
        private readonly string dbTitleLeft = "CANDLECHASERS", dbTitleRight = "SQZ ENGINE PRO";
        private readonly List<Tuple<string, string, Brush>> dashboardRows = new List<Tuple<string, string, Brush>>();

        [NinjaScriptProperty]
        [Display(Name = "Trading style", GroupName = "01. CandleChasers Presets", Order = 0)]
        public CcTradeStyle TradeStyle { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Signal personality", GroupName = "01. CandleChasers Presets", Order = 1)]
        public CcSignalMode SignalMode { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Signal engine", GroupName = "01. CandleChasers Presets", Order = 2)]
        public CcEngineMode EngineMode { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Classic: allow opposite-side rising/falling momentum", GroupName = "01. CandleChasers Presets", Order = 3)]
        public bool ClassicSlope { get; set; }
        [NinjaScriptProperty, Range(0, 500)]
        [Display(Name = "Bars to wait after an exit", GroupName = "01. CandleChasers Presets", Order = 4)]
        public int CooldownBars { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Override preset lengths", GroupName = "01. CandleChasers Presets", Order = 5)]
        public bool CustomLengths { get; set; }
        [NinjaScriptProperty, Range(5, 500)]
        [Display(Name = "Custom BB length", GroupName = "01. CandleChasers Presets", Order = 6)]
        public int UserBBLen { get; set; }
        [NinjaScriptProperty, Range(5, 500)]
        [Display(Name = "Custom KC length", GroupName = "01. CandleChasers Presets", Order = 7)]
        public int UserKCLen { get; set; }
        [NinjaScriptProperty, Range(0.1, double.MaxValue)]
        [Display(Name = "BB multiplier", GroupName = "01. CandleChasers Presets", Order = 8)]
        public double UserBBMult { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use confirmed higher-timeframe trend", GroupName = "02. Signal Filters", Order = 0)]
        public bool UseHTF { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Automatic higher timeframe", GroupName = "02. Signal Filters", Order = 1)]
        public bool AutoHTF { get; set; }
        [NinjaScriptProperty, Range(1, 100000)]
        [Display(Name = "Manual higher timeframe (minutes)", Description = "Translated from Pine's timeframe string (e.g. \"240\" -> 240 minutes).", GroupName = "02. Signal Filters", Order = 2)]
        public int ManualHTFMinutes { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Use ADX / DI filter", GroupName = "02. Signal Filters", Order = 3)]
        public bool UseADX { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Use relative volume filter (auto-skip absent volume)", GroupName = "02. Signal Filters", Order = 4)]
        public bool UseVol { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Require momentum on trade's side of zero", GroupName = "02. Signal Filters", Order = 5)]
        public bool RequireZero { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Allow momentum entries without recent squeeze", GroupName = "02. Signal Filters", Order = 6)]
        public bool AllowNoSqueeze { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "KC uses true range", GroupName = "03. Squeeze Engine", Order = 0)]
        public bool UseTR { get; set; }
        [NinjaScriptProperty, Range(0.1, double.MaxValue)]
        [Display(Name = "Wide KC (loose squeeze)", GroupName = "03. Squeeze Engine", Order = 1)]
        public double WideKC { get; set; }
        [NinjaScriptProperty, Range(0.1, double.MaxValue)]
        [Display(Name = "Normal KC (medium squeeze)", GroupName = "03. Squeeze Engine", Order = 2)]
        public double NormalKC { get; set; }
        [NinjaScriptProperty, Range(0.1, double.MaxValue)]
        [Display(Name = "Tight KC (deep squeeze)", GroupName = "03. Squeeze Engine", Order = 3)]
        public double TightKC { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Normalize histogram by ATR (display only)", GroupName = "03. Squeeze Engine", Order = 4)]
        public bool NormalizeMomentum { get; set; }
        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Acceleration smoothing", GroupName = "03. Squeeze Engine", Order = 5)]
        public int AccelSmoothing { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display divergence markers", GroupName = "04. Confirmed Pivot Divergences", Order = 0)]
        public bool ShowDivergences { get; set; }
        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Pivot left bars", GroupName = "04. Confirmed Pivot Divergences", Order = 1)]
        public int PivotLeft { get; set; }
        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "Pivot confirmation bars", GroupName = "04. Confirmed Pivot Divergences", Order = 2)]
        public int PivotRight { get; set; }
        [NinjaScriptProperty, Range(5, 500)]
        [Display(Name = "Maximum bars between pivots", GroupName = "04. Confirmed Pivot Divergences", Order = 3)]
        public int MaxPivotDistance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display virtual trade levels", GroupName = "05. Trade Map (Indicator Simulation)", Order = 0)]
        public bool ShowTradeMap { get; set; }
        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "ATR period", GroupName = "05. Trade Map (Indicator Simulation)", Order = 1)]
        public int AtrPeriod { get; set; }
        [NinjaScriptProperty, Range(0.1, double.MaxValue)]
        [Display(Name = "Initial stop ATR", GroupName = "05. Trade Map (Indicator Simulation)", Order = 2)]
        public double AtrStopMult { get; set; }
        [NinjaScriptProperty, Range(0.1, double.MaxValue)]
        [Display(Name = "Target reward:risk", GroupName = "05. Trade Map (Indicator Simulation)", Order = 3)]
        public double RewardMult { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Enable ATR trailing stop", GroupName = "05. Trade Map (Indicator Simulation)", Order = 4)]
        public bool TrailEnabled { get; set; }
        [NinjaScriptProperty, Range(0.1, double.MaxValue)]
        [Display(Name = "Trail ATR from favorable extreme", GroupName = "05. Trade Map (Indicator Simulation)", Order = 5)]
        public double TrailATR { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Exit on two bars of momentum fade", GroupName = "05. Trade Map (Indicator Simulation)", Order = 6)]
        public bool ExitOnFade { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show exit markers", GroupName = "06. CandleChasers Visuals", Order = 0)]
        public bool ShowExits { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show virtual entry/stop/target in dashboard", GroupName = "06. CandleChasers Visuals", Order = 1)]
        public bool ShowPriceMap { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show dashboard", GroupName = "06. CandleChasers Visuals", Order = 2)]
        public bool ShowDashboard { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show early setup markers", GroupName = "06. CandleChasers Visuals", Order = 3)]
        public bool ShowEarly { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show confirmed BUY / SELL markers", GroupName = "06. CandleChasers Visuals", Order = 4)]
        public bool ShowSignals { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Subtle compression background", GroupName = "06. CandleChasers Visuals", Order = 5)]
        public bool ShowBackground { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Sneaky Squeeze Engine PRO CC";
                Description = "Ported from TradingView 'Squeeze Engine PRO [CandleChasers]' — BB/KC squeeze, LazyBear-style momentum, HTF/ADX/volume confluence, pivot divergences and a virtual trade-lifecycle dashboard. Research tool, not a backtested strategy.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = false;
                IsSuspendedWhileInactive = false;

                TradeStyle = CcTradeStyle.DayTrading;
                SignalMode = CcSignalMode.Balanced;
                EngineMode = CcEngineMode.Pro;
                ClassicSlope = false;
                CooldownBars = 3;
                CustomLengths = false;
                UserBBLen = 20; UserKCLen = 20; UserBBMult = 2.0;

                UseHTF = true; AutoHTF = true; ManualHTFMinutes = 240;
                UseADX = true; UseVol = true; RequireZero = true; AllowNoSqueeze = false;

                UseTR = true; WideKC = 2.0; NormalKC = 1.5; TightKC = 1.0;
                NormalizeMomentum = false; AccelSmoothing = 3;

                ShowDivergences = true; PivotLeft = 4; PivotRight = 4; MaxPivotDistance = 80;

                ShowTradeMap = true; AtrPeriod = 14; AtrStopMult = 1.8; RewardMult = 2.0;
                TrailEnabled = true; TrailATR = 2.0; ExitOnFade = true;

                ShowExits = true; ShowPriceMap = false; ShowDashboard = true; ShowEarly = true;
                ShowSignals = true; ShowBackground = false;

                AddPlot(new Stroke(Brushes.Gray, 3), PlotStyle.Bar, "CandleChasers Momentum");     // 0
                AddPlot(new Stroke(Brushes.Gray, 3), PlotStyle.Dot, "Compression Level");           // 1 (Pine plot.style_circles -> Dot)
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Long confluence score");   // 2 data-window only
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Short confluence score");  // 3
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Relative volume");         // 4
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "ADX");                     // 5
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Squeeze duration");        // 6
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Virtual stop (price)");    // 7
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Virtual target (price)");  // 8
            }
            else if (State == State.Configure)
            {
                chartMinutes = BarsPeriod.BarsPeriodType == BarsPeriodType.Minute ? BarsPeriod.Value : 0;
                htfSupported = chartMinutes > 0 && UseHTF;
                if (htfSupported)
                {
                    int mult = TradeStyle == CcTradeStyle.SwingTrading ? 5 : TradeStyle == CcTradeStyle.Scalping ? 12 : 8;
                    int autoMinutes = Math.Max(1, (int)Math.Round(Math.Min(chartMinutes * mult, 525600) / 1.0));
                    htfMinutes = AutoHTF ? Math.Max(autoMinutes, chartMinutes + 1) : ManualHTFMinutes;
                    if (htfMinutes <= chartMinutes) htfSupported = false;
                    if (htfSupported) AddDataSeries(BarsPeriodType.Minute, htfMinutes);
                }
            }
            else if (State == State.DataLoaded)
            {
                srcMinusAnchor = new Series<double>(this);
                rawMomentumSeries = new Series<double>(this);
                velocitySeries = new Series<double>(this);
                smTR = new Series<double>(this);
                smPlusDM = new Series<double>(this);
                smMinusDM = new Series<double>(this);
                adxSeries = new Series<double>(this);

                if (UseHTF && !htfSupported && !htfWarned)
                {
                    htfWarned = true;
                    Print(Name + ": HTF trend filter needs a minute-based chart with a manual/automatic HTF above the chart timeframe; disabling it for this chart.");
                }
                if (EngineMode != CcEngineMode.Classic && !(WideKC > NormalKC && NormalKC > TightKC))
                    Print(Name + ": KC widths should satisfy Wide > Normal > Tight — squeeze levels may not behave as expected.");
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;   // secondary (HTF) series only feeds reads below; no separate work needed here
            bool classicMode = EngineMode == CcEngineMode.Classic;
            int presetLen = TradeStyle == CcTradeStyle.Scalping ? 14 : TradeStyle == CcTradeStyle.DayTrading ? 20 : 24;
            int bbLen = classicMode ? 20 : (CustomLengths ? UserBBLen : presetLen);
            int kcLen = classicMode ? 20 : (CustomLengths ? UserKCLen : presetLen);
            int trendLen = TradeStyle == CcTradeStyle.Scalping ? 34 : TradeStyle == CcTradeStyle.DayTrading ? 50 : 89;
            int freshBars = TradeStyle == CcTradeStyle.Scalping ? 3 : TradeStyle == CcTradeStyle.DayTrading ? 5 : 7;
            int minSqueezeBars = SignalMode == CcSignalMode.EarlierSignals ? 2 : SignalMode == CcSignalMode.Balanced ? 3 : 5;
            int scoreThreshold = SignalMode == CcSignalMode.EarlierSignals ? 60 : SignalMode == CcSignalMode.Balanced ? 75 : 85;
            double adxMin = SignalMode == CcSignalMode.EarlierSignals ? 15.0 : SignalMode == CcSignalMode.Balanced ? 18.0 : 22.0;
            double rvolMin = SignalMode == CcSignalMode.EarlierSignals ? 0.9 : SignalMode == CcSignalMode.Balanced ? 1.15 : 1.4;

            if (CurrentBar < Math.Max(Math.Max(bbLen, kcLen), Math.Max(trendLen, AtrPeriod)) + 2) return;

            double src = Close[0];
            double bbBasis = SMA(bbLen)[0];
            double bbDev = (classicMode ? 2.0 : UserBBMult) * StdDev(bbLen)[0];
            double upperBB = bbBasis + bbDev;
            double lowerBB = bbBasis - bbDev;
            double kcBasis = SMA(kcLen)[0];
            // KC average range (true range or high-low range) computed by hand rather than assuming an
            // SMA(ISeries<double>, int) overload exists against a TrueRange() indicator object.
            double kcAvgRange = AvgTrueRangeOrRange(kcLen, UseTR);

            double effWide = classicMode ? 2.0 : WideKC;
            double effNormal = classicMode ? 1.5 : NormalKC;
            double effTight = classicMode ? 1.0 : TightKC;
            bool sqzLoose = lowerBB > kcBasis - kcAvgRange * effWide && upperBB < kcBasis + kcAvgRange * effWide;
            bool sqzMedium = lowerBB > kcBasis - kcAvgRange * effNormal && upperBB < kcBasis + kcAvgRange * effNormal;
            bool sqzDeep = lowerBB > kcBasis - kcAvgRange * effTight && upperBB < kcBasis + kcAvgRange * effTight;
            int squeezeLevel = sqzDeep ? 3 : sqzMedium ? 2 : sqzLoose ? 1 : 0;
            bool inSqueeze = classicMode ? sqzMedium : squeezeLevel > 0;
            bool release = !inSqueeze && prevInSqueeze;

            if (inSqueeze && !prevInSqueeze) episode += 1;
            if (release) lastSqueezeDuration = squeezeDuration;             // old (pre-update) duration
            squeezeDuration = inSqueeze ? squeezeDuration + 1 : 0;
            if (release) lastReleaseBar = CurrentBar;
            int sinceRelease = lastReleaseBar < 0 ? int.MaxValue : CurrentBar - lastReleaseBar;
            bool freshRelease = !inSqueeze && sinceRelease <= freshBars && lastSqueezeDuration >= minSqueezeBars;

            // ── LazyBear-style PRO/Early momentum (credit: LazyBear) vs Classic 12-bar momentum ──
            double midPrice = (MAX(High, kcLen)[0] + MIN(Low, kcLen)[0]) / 2.0;
            double anchor = (midPrice + SMA(kcLen)[0]) / 2.0;
            srcMinusAnchor[0] = src - anchor;
            double lbMomentum = LinReg(srcMinusAnchor, kcLen)[0];
            double rawMomentum = classicMode ? Close[0] - Close[12] : lbMomentum;
            rawMomentumSeries[0] = rawMomentum;
            double atr = ATR(AtrPeriod)[0];
            double momentum = NormalizeMomentum ? rawMomentum / Math.Max(atr, TickSize) : rawMomentum;
            double rawMomentumPrev = rawMomentumSeries[1];
            double rawMomentumPrev2 = CurrentBar >= 2 ? rawMomentumSeries[2] : rawMomentumPrev;
            double velocity = rawMomentum - rawMomentumPrev;
            velocitySeries[0] = velocity;
            double acceleration = EMA(velocitySeries, AccelSmoothing)[0];
            bool rising = rawMomentum > rawMomentumPrev;
            bool falling = rawMomentum < rawMomentumPrev;
            bool bullMomentum = rising && acceleration > 0;
            bool bearMomentum = falling && acceleration < 0;

            // ── higher timeframe trend (confirmed only) ──
            double localEma = EMA(trendLen)[0];
            bool htfActive = UseHTF && !classicMode && htfSupported;
            double htfClose = double.NaN, htfEma = double.NaN;
            if (htfActive && BarsArray.Length > 1 && CurrentBars.Length > 1 && CurrentBars[1] >= trendLen + 1)
            {
                htfClose = Closes[1][1];
                htfEma = EMA(Closes[1], trendLen)[1];
            }
            else htfActive = false;
            bool bullTrend = Close[0] > localEma && (!htfActive || htfClose > htfEma);
            bool bearTrend = Close[0] < localEma && (!htfActive || htfClose < htfEma);

            // ── ADX / DI (hand-rolled Wilder smoothing) ──
            UpdateDmi(AtrPeriod > 0 ? 14 : 14);
            double diPlus = smTR[0] > 0 ? 100.0 * smPlusDM[0] / smTR[0] : 0.0;
            double diMinus = smTR[0] > 0 ? 100.0 * smMinusDM[0] / smTR[0] : 0.0;
            double adx = adxSeries[0];
            bool adxBull = adx >= adxMin && diPlus > diMinus;
            bool adxBear = adx >= adxMin && diMinus > diPlus;

            double avgVol = SMA(Volume, 20)[0];
            bool hasVolume = avgVol > 0;
            double rvol = hasVolume ? Volume[0] / avgVol : double.NaN;
            bool volPass = !UseVol || !hasVolume || rvol >= rvolMin;
            bool adxBullPass = !UseADX || adxBull;
            bool adxBearPass = !UseADX || adxBear;

            // ── confluence score ──
            int squeezePoints = freshRelease ? (lastSqueezeDuration >= minSqueezeBars * 2 ? 25 : 18) : inSqueeze ? (squeezeLevel == 3 ? 25 : squeezeLevel == 2 ? 18 : 10) : 0;
            bool scoreSqueezeActive = !(AllowNoSqueeze && !inSqueeze && !freshRelease);
            bool scoreVolumeActive = UseVol && hasVolume;
            int volumePoints = scoreVolumeActive ? (rvol >= rvolMin ? 15 : rvol >= rvolMin * 0.8 ? 7 : 0) : 0;
            int scoreMaximum = 45 + (scoreSqueezeActive ? 25 : 0) + (scoreVolumeActive ? 15 : 0) + (UseADX ? 15 : 0);
            int longPoints = (scoreSqueezeActive ? squeezePoints : 0) + (bullTrend ? 25 : 0) + (bullMomentum ? 20 : rising ? 10 : 0) + volumePoints + (UseADX && adxBull ? 15 : 0);
            int shortPoints = (scoreSqueezeActive ? squeezePoints : 0) + (bearTrend ? 25 : 0) + (bearMomentum ? 20 : falling ? 10 : 0) + volumePoints + (UseADX && adxBear ? 15 : 0);
            int longScore = scoreMaximum > 0 ? (int)Math.Round(100.0 * longPoints / scoreMaximum) : 0;
            int shortScore = scoreMaximum > 0 ? (int)Math.Round(100.0 * shortPoints / scoreMaximum) : 0;

            // ── entry engine ──
            bool earlyMode = EngineMode == CcEngineMode.Early || SignalMode == CcSignalMode.EarlierSignals;
            bool strictMode = SignalMode == CcSignalMode.StrongerConfirmation;
            bool candleOK = true;   // Calculate.OnBarClose already guarantees a closed bar
            bool setupEligible = AllowNoSqueeze || freshRelease || (earlyMode && inSqueeze && squeezeDuration >= minSqueezeBars);
            bool zeroLong = !RequireZero || rawMomentum > 0;
            bool zeroShort = !RequireZero || rawMomentum < 0;
            bool longImpulse = earlyMode ? rising : strictMode ? (bullMomentum && rawMomentum > rawMomentumPrev && rawMomentumPrev > rawMomentumPrev2) : bullMomentum;
            bool shortImpulse = earlyMode ? falling : strictMode ? (bearMomentum && rawMomentum < rawMomentumPrev && rawMomentumPrev < rawMomentumPrev2) : bearMomentum;
            bool longReady = candleOK && setupEligible && zeroLong && bullTrend && volPass && adxBullPass && longImpulse && longScore >= scoreThreshold;
            bool shortReady = candleOK && setupEligible && zeroShort && bearTrend && volPass && adxBearPass && shortImpulse && shortScore >= scoreThreshold;
            if (classicMode)
            {
                longReady = candleOK && release && (ClassicSlope ? rising : rawMomentum > 0);
                shortReady = candleOK && release && (ClassicSlope ? falling : rawMomentum < 0);
            }
            bool longReadyPrev = LongReadyPrev;
            bool shortReadyPrev = ShortReadyPrev;
            bool earlyLong = candleOK && inSqueeze && squeezeDuration >= minSqueezeBars && rising && rawMomentum > rawMomentumPrev2 && bullTrend;
            bool earlyShort = candleOK && inSqueeze && squeezeDuration >= minSqueezeBars && falling && rawMomentum < rawMomentumPrev2 && bearTrend;
            bool earlyLongSignal = earlyLong && !EarlyLongPrev;
            bool earlyShortSignal = earlyShort && !EarlyShortPrev;
            EarlyLongPrev = earlyLong; EarlyShortPrev = earlyShort;

            // ── pivot-confirmed divergence ──
            bool bullDiv = false, bearDiv = false;
            if (CurrentBar >= PivotLeft + PivotRight)
            {
                if (IsPivotLow(PivotLeft, PivotRight))
                {
                    int pivotBar = CurrentBar - PivotRight;
                    double pLow = Low[PivotRight];
                    double pMom = rawMomentumSeries[PivotRight];
                    if (priorLowBar >= 0 && pivotBar - priorLowBar <= MaxPivotDistance && pLow < priorLowPrice && pMom > priorLowMom)
                        bullDiv = true;
                    priorLowPrice = pLow; priorLowMom = pMom; priorLowBar = pivotBar;
                }
                if (IsPivotHigh(PivotLeft, PivotRight))
                {
                    int pivotBar = CurrentBar - PivotRight;
                    double pHigh = High[PivotRight];
                    double pMom = rawMomentumSeries[PivotRight];
                    if (priorHighBar >= 0 && pivotBar - priorHighBar <= MaxPivotDistance && pHigh > priorHighPrice && pMom < priorHighMom)
                        bearDiv = true;
                    priorHighPrice = pHigh; priorHighMom = pMom; priorHighBar = pivotBar;
                }
            }

            // ── virtual trade lifecycle: STANDBY -> CHARGING -> ARMED -> IN TRADE -> COOLDOWN ──
            bool exitLong = false, exitShort = false, buy = false, sell = false, exited = false;
            if (direction != 0 && CurrentBar > lastEntryBar)
            {
                bool hitStop = direction == 1 ? Low[0] <= stopPx : High[0] >= stopPx;
                bool hitTarget = !classicMode && (direction == 1 ? High[0] >= targetPx : Low[0] <= targetPx);
                bool momentumFade = direction == 1 ? (classicMode ? falling : falling && FallingPrev) : (classicMode ? rising : rising && RisingPrev);
                bool shouldExit = hitStop || hitTarget || ((classicMode || ExitOnFade) && momentumFade);
                if (shouldExit)
                {
                    exitLong = direction == 1; exitShort = direction == -1; exited = true;
                    lastExitReason = hitStop && hitTarget ? "STOP + TARGET: ORDER UNKNOWN" : hitStop ? "STOP" : hitTarget ? "TARGET" : "MOMENTUM FADE";
                    lastExitBar = CurrentBar;
                    direction = 0; entryPx = double.NaN; stopPx = double.NaN; targetPx = double.NaN; extremePx = double.NaN;
                }
                else
                {
                    extremePx = direction == 1 ? Math.Max(extremePx, High[0]) : Math.Min(extremePx, Low[0]);
                    if (TrailEnabled && !classicMode)
                        stopPx = direction == 1 ? Math.Max(stopPx, extremePx - atr * TrailATR) : Math.Min(stopPx, extremePx + atr * TrailATR);
                }
            }
            RisingPrev = rising; FallingPrev = falling;
            bool cooling = lastExitBar >= 0 && CurrentBar - lastExitBar <= CooldownBars;
            bool episodeAvailable = episode > 0 && tradedEpisode != episode;
            bool noSqzEntry = AllowNoSqueeze && !inSqueeze && !freshRelease && !classicMode;
            bool canEnter = candleOK && direction == 0 && !exited && !cooling && !double.IsNaN(atr) && atr > 0;
            buy = canEnter && longReady && (noSqzEntry ? !longReadyPrev : episodeAvailable);
            sell = canEnter && shortReady && !buy && (noSqzEntry ? !shortReadyPrev : episodeAvailable);
            if (buy || sell)
            {
                direction = buy ? 1 : -1;
                entryPx = Close[0];
                double riskDistance = Math.Max(atr * (classicMode ? 2.0 : AtrStopMult), TickSize);
                stopPx = buy ? entryPx - riskDistance : entryPx + riskDistance;
                targetPx = classicMode ? double.NaN : (buy ? entryPx + riskDistance * RewardMult : entryPx - riskDistance * RewardMult);
                extremePx = Close[0];
                lastEntryBar = CurrentBar;
                if (!noSqzEntry) tradedEpisode = episode;
            }
            LongReadyPrev = longReady; ShortReadyPrev = shortReady;
            string lifecycle = direction != 0 ? (direction == 1 ? "IN TRADE: LONG" : "IN TRADE: SHORT") : cooling ? "COOLDOWN" : inSqueeze ? "CHARGING" : (freshRelease && episodeAvailable) ? "ARMED" : "STANDBY";

            // ── visuals ──
            Brush bullBright = Brushes.MediumSpringGreen, bullDim = Brushes.DarkGreen, bearBright = Brushes.OrangeRed, bearDim = Brushes.DarkRed;
            Brush histColor = momentum >= 0 ? (rising ? bullBright : bullDim) : (falling ? bearBright : bearDim);
            bool deep = squeezeLevel == 3, med = squeezeLevel == 2, loose = squeezeLevel == 1;
            Brush dotColor = classicMode
                ? (inSqueeze ? Brushes.Orange : Brushes.SlateGray)
                : deep ? Brushes.MediumPurple : med ? Brushes.Orange : loose ? Brushes.Khaki : Brushes.SlateGray;
            if (ShowBackground && inSqueeze) dotColor = deep ? Brushes.Purple : med ? Brushes.DarkOrange : Brushes.Gold;   // deepen while active, approximating bgcolor()

            Values[0][0] = momentum; PlotBrushes[0][0] = histColor;
            Values[1][0] = 0; PlotBrushes[1][0] = dotColor;
            Values[2][0] = longScore; Values[3][0] = shortScore; Values[4][0] = hasVolume ? rvol : 0.0; Values[5][0] = adx; Values[6][0] = squeezeDuration;
            Values[7][0] = ShowTradeMap ? stopPx : double.NaN; Values[8][0] = ShowTradeMap ? targetPx : double.NaN;

            // These markers render on the PRICE panel (Draw.* with an
            // isAutoScale owner draws into the panel's own price axis), so
            // their Y must be a real price, not a momentum/oscillator value.
            // The previous version anchored them to the histogram's own
            // rolling high/low (roughly -80..+80), which is a different
            // scale than NQ's ~26000 price -- NinjaTrader's autoscale then
            // had to stretch the price axis to fit both, squashing the
            // candles into a sliver and the markers down near zero.
            // Anchor to the bar's own High/Low plus an ATR-scaled offset
            // instead, so everything sits just off the actual bars:
            // release=diamond+"R" (below bar), early=circle+"E"
            // (below=bull, above=bear), buy/sell=labelup/labeldown
            // "CC BUY"/"CC SELL" (below/above), divergence=triangleup/down+
            // "DIV" (below/above), exit=xcross+"EXIT" (below/above).
            double markerOffset = Math.Max(atr * 0.6, TickSize * 4);
            double bottomY = Low[0] - markerOffset;
            double topY = High[0] + markerOffset;

            if (release)
            {
                Draw.Diamond(this, "CcRel" + CurrentBar, true, 0, bottomY, Brushes.Aqua);
                Draw.Text(this, "CcRelTxt" + CurrentBar, true, "R", 0, bottomY, -14, Brushes.Aqua, new SimpleFont("Arial", 8), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (ShowEarly && earlyLongSignal)
            {
                Draw.Dot(this, "CcEBuDot" + CurrentBar, true, 0, bottomY, bullBright);
                Draw.Text(this, "CcEBu" + CurrentBar, true, "E", 0, bottomY, 14, bullBright, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (ShowEarly && earlyShortSignal)
            {
                Draw.Dot(this, "CcEBeDot" + CurrentBar, true, 0, topY, bearBright);
                Draw.Text(this, "CcEBe" + CurrentBar, true, "E", 0, topY, 14, bearBright, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (ShowSignals && buy) Draw.Text(this, "CcBuy" + CurrentBar, true, "CC BUY", 0, bottomY, -28, Brushes.Black, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, bullBright, bullBright, 0);
            if (ShowSignals && sell) Draw.Text(this, "CcSell" + CurrentBar, true, "CC SELL", 0, topY, 28, Brushes.White, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Center, bearBright, bearBright, 0);
            if (ShowDivergences && bullDiv)
            {
                Draw.TriangleUp(this, "CcDivBu" + CurrentBar, true, 0, bottomY, Brushes.Lime);
                Draw.Text(this, "CcDivBuTxt" + CurrentBar, true, "DIV", 0, bottomY, -14, Brushes.Lime, new SimpleFont("Arial", 8), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (ShowDivergences && bearDiv)
            {
                Draw.TriangleDown(this, "CcDivBe" + CurrentBar, true, 0, topY, Brushes.Red);
                Draw.Text(this, "CcDivBeTxt" + CurrentBar, true, "DIV", 0, topY, 14, Brushes.Red, new SimpleFont("Arial", 8), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (ShowExits && exitLong) Draw.Text(this, "CcXLong" + CurrentBar, true, "EXIT", 0, bottomY, -42, Brushes.Yellow, new SimpleFont("Arial", 8), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            if (ShowExits && exitShort) Draw.Text(this, "CcXShort" + CurrentBar, true, "EXIT", 0, topY, 42, Brushes.Yellow, new SimpleFont("Arial", 8), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);

            dashboardRows.Clear();
            if (ShowDashboard)
            {
                string sqzText = classicMode ? (inSqueeze ? "CLASSIC SQUEEZE" : "OPEN") : (deep ? "DEEP" : med ? "MEDIUM" : loose ? "LOOSE" : freshRelease ? "RELEASED" : "OPEN");
                string trendText = bullTrend ? "BULL" : bearTrend ? "BEAR" : "MIXED";
                string momText = bullMomentum ? "ACCEL UP" : bearMomentum ? "ACCEL DOWN" : rising ? "UP / SLOW" : "DOWN / SLOW";
                string tradeMap = !ShowTradeMap ? "OFF" : direction == 0 ? "FLAT" : (direction == 1 ? "LONG " : "SHORT ") + "S:" + stopPx.ToString("0.00") + " T:" + targetPx.ToString("0.00");
                string htfLbl = htfActive ? (htfMinutes + "m") : "local";

                Brush trendColor = bullTrend ? bullBright : bearTrend ? bearBright : Brushes.Silver;
                Brush momColor = (bullMomentum || rising) ? bullBright : bearBright;
                Brush statusColor = direction == 1 ? bullBright : direction == -1 ? bearBright
                    : cooling ? Brushes.Gray : inSqueeze ? Brushes.Orange
                    : (freshRelease && episodeAvailable) ? Brushes.Aqua : Brushes.Silver;
                Brush tradeMapColor = direction == 1 ? bullBright : direction == -1 ? bearBright : Brushes.Silver;

                dashboardRows.Add(Tuple.Create("STYLE/MODE", CcDisplay.Of(TradeStyle) + " / " + CcDisplay.Of(SignalMode), (Brush)Brushes.White));
                dashboardRows.Add(Tuple.Create("SQUEEZE", sqzText + " | " + (inSqueeze ? squeezeDuration : lastSqueezeDuration) + " bars", dotColor));
                dashboardRows.Add(Tuple.Create("TREND (" + htfLbl + ")", trendText, trendColor));
                dashboardRows.Add(Tuple.Create("MOMENTUM", momText, momColor));
                dashboardRows.Add(Tuple.Create("ADX/RVOL", adx.ToString("0.0") + " / " + (hasVolume ? rvol.ToString("0.00") : "N/A"), (Brush)Brushes.Silver));
                dashboardRows.Add(Tuple.Create("LONG/SHORT", classicMode ? "NOT USED" : longScore + " / " + shortScore, (Brush)Brushes.Silver));
                dashboardRows.Add(Tuple.Create("STATUS", lifecycle, statusColor));
                dashboardRows.Add(Tuple.Create("TRADE MAP", tradeMap, tradeMapColor));
                dashboardRows.Add(Tuple.Create("ENGINE", CcDisplay.Of(EngineMode), (Brush)Brushes.DeepSkyBlue));
                dashboardRows.Add(Tuple.Create("LAST EXIT", lastExitReason, (Brush)Brushes.Yellow));
            }

            if (buy) Alert("CcBuy", Priority.Medium, "CandleChasers Squeeze Engine PRO BUY on " + Instrument.FullName + " " + BarsPeriod + " at " + Close[0], NinjaTrader.Core.Globals.InstallDir + "\\sounds\\Alert1.wav", 15, Brushes.Black, bullBright);
            if (sell) Alert("CcSell", Priority.Medium, "CandleChasers Squeeze Engine PRO SELL on " + Instrument.FullName + " " + BarsPeriod + " at " + Close[0], NinjaTrader.Core.Globals.InstallDir + "\\sounds\\Alert1.wav", 15, Brushes.Black, bearBright);
            if (release) Alert("CcRel", Priority.Low, "CandleChasers Squeeze RELEASE on " + Instrument.FullName + " " + BarsPeriod, NinjaTrader.Core.Globals.InstallDir + "\\sounds\\Alert1.wav", 15, Brushes.Black, Brushes.Aqua);
            if (earlyLongSignal) Alert("CcEBu", Priority.Low, "CandleChasers EARLY BULL setup on " + Instrument.FullName + " " + BarsPeriod, NinjaTrader.Core.Globals.InstallDir + "\\sounds\\Alert1.wav", 15, Brushes.Black, bullBright);
            if (earlyShortSignal) Alert("CcEBe", Priority.Low, "CandleChasers EARLY BEAR setup on " + Instrument.FullName + " " + BarsPeriod, NinjaTrader.Core.Globals.InstallDir + "\\sounds\\Alert1.wav", 15, Brushes.Black, bearBright);
            if (bullDiv) Alert("CcDivBu", Priority.Low, "CandleChasers confirmed BULL divergence on " + Instrument.FullName + " " + BarsPeriod, NinjaTrader.Core.Globals.InstallDir + "\\sounds\\Alert1.wav", 15, Brushes.Black, Brushes.Lime);
            if (bearDiv) Alert("CcDivBe", Priority.Low, "CandleChasers confirmed BEAR divergence on " + Instrument.FullName + " " + BarsPeriod, NinjaTrader.Core.Globals.InstallDir + "\\sounds\\Alert1.wav", 15, Brushes.Black, Brushes.Red);
            if (exitLong || exitShort) Alert("CcExit", Priority.Low, "CandleChasers virtual trade exit on " + Instrument.FullName + " " + BarsPeriod, NinjaTrader.Core.Globals.InstallDir + "\\sounds\\Alert1.wav", 15, Brushes.Black, Brushes.Yellow);

            prevInSqueeze = inSqueeze;
        }

        // Custom-rendered dashboard table. Draw.TextFixed can only paint one
        // solid-color text blob, and (like every other Draw.* call from this
        // non-overlay indicator) it lands on the PRICE panel, not this
        // indicator's own panel -- neither matches the original Pine
        // dashboard, which is a bordered table of individually-colored rows
        // sitting inside the indicator's own pane. OnRender draws directly
        // into whichever panel hosts this indicator, so it naturally lands
        // here instead, with per-row colors Draw.TextFixed can't provide.
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (!ShowDashboard || dashboardRows.Count == 0 || RenderTarget == null)
                return;

            const float padding = 8f, rowHeight = 15f, titleHeight = 18f;
            const float labelColWidth = 100f, valueColWidth = 110f;
            float boxWidth = labelColWidth + valueColWidth + padding * 2f;
            float boxHeight = titleHeight + dashboardRows.Count * rowHeight + padding * 2f;
            float left = ChartPanel.X + ChartPanel.W - boxWidth - 6f;
            float top = ChartPanel.Y + 6f;
            var boxRect = new SharpDX.RectangleF(left, top, boxWidth, boxHeight);

            SharpDX.Direct2D1.Brush dxBg = new SolidColorBrush(Colors.Black) { Opacity = 0.6 }.ToDxBrush(RenderTarget);
            SharpDX.Direct2D1.Brush dxBorder = Brushes.DimGray.ToDxBrush(RenderTarget);
            RenderTarget.FillRectangle(boxRect, dxBg);
            RenderTarget.DrawRectangle(boxRect, dxBorder, 1f);
            dxBg.Dispose();
            dxBorder.Dispose();

            var titleFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", 11f);
            var labelFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", 10f);
            var valueFormat = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", 10f);
            valueFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Trailing;

            SharpDX.Direct2D1.Brush dxTitleLeft = Brushes.Silver.ToDxBrush(RenderTarget);
            SharpDX.Direct2D1.Brush dxTitleRight = Brushes.DeepSkyBlue.ToDxBrush(RenderTarget);
            float titleLeftWidth = boxWidth * 0.52f;
            RenderTarget.DrawText(dbTitleLeft, titleFormat, new SharpDX.RectangleF(left + padding, top + padding - 3f, titleLeftWidth, titleHeight), dxTitleLeft);
            RenderTarget.DrawText(dbTitleRight, titleFormat, new SharpDX.RectangleF(left + padding + titleLeftWidth, top + padding - 3f, boxWidth - padding * 2f - titleLeftWidth, titleHeight), dxTitleRight);
            dxTitleLeft.Dispose();
            dxTitleRight.Dispose();

            SharpDX.Direct2D1.Brush dxLabel = Brushes.LightGray.ToDxBrush(RenderTarget);
            float rowY = top + padding + titleHeight;
            for (int i = 0; i < dashboardRows.Count; i++)
            {
                if (i % 2 == 1)
                {
                    SharpDX.Direct2D1.Brush dxStripe = new SolidColorBrush(Colors.White) { Opacity = 0.04 }.ToDxBrush(RenderTarget);
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(left + 1f, rowY, boxWidth - 2f, rowHeight), dxStripe);
                    dxStripe.Dispose();
                }
                Tuple<string, string, Brush> row = dashboardRows[i];
                RenderTarget.DrawText(row.Item1, labelFormat, new SharpDX.RectangleF(left + padding, rowY, labelColWidth, rowHeight), dxLabel);
                SharpDX.Direct2D1.Brush dxValue = row.Item3.ToDxBrush(RenderTarget);
                RenderTarget.DrawText(row.Item2, valueFormat, new SharpDX.RectangleF(left + padding + labelColWidth, rowY, valueColWidth, rowHeight), dxValue);
                dxValue.Dispose();
                rowY += rowHeight;
            }
            dxLabel.Dispose();
            titleFormat.Dispose();
            labelFormat.Dispose();
            valueFormat.Dispose();
        }

        // ───────────────────────── helpers ─────────────────────────
        private bool LongReadyPrev, ShortReadyPrev, EarlyLongPrev, EarlyShortPrev, RisingPrev, FallingPrev;

        private double AvgTrueRangeOrRange(int len, bool useTR)
        {
            double sum = 0;
            for (int i = 0; i < len && i <= CurrentBar; i++)
                sum += useTR ? TrueRangeAt(i) : (High[i] - Low[i]);
            return sum / Math.Max(1, Math.Min(len, CurrentBar + 1));
        }
        private double TrueRangeAt(int barsAgo)
        {
            if (CurrentBar - barsAgo < 1) return High[barsAgo] - Low[barsAgo];
            return Math.Max(High[barsAgo] - Low[barsAgo], Math.Max(Math.Abs(High[barsAgo] - Close[barsAgo + 1]), Math.Abs(Low[barsAgo] - Close[barsAgo + 1])));
        }

        private void UpdateDmi(int period)
        {
            double tr = TrueRangeAt(0);
            double upMove = CurrentBar >= 1 ? High[0] - High[1] : 0;
            double downMove = CurrentBar >= 1 ? Low[1] - Low[0] : 0;
            double plusDM = (upMove > downMove && upMove > 0) ? upMove : 0;
            double minusDM = (downMove > upMove && downMove > 0) ? downMove : 0;
            if (CurrentBar < period)
            {
                smTR[0] = smTR.Count > 1 ? smTR[1] + tr : tr;
                smPlusDM[0] = smPlusDM.Count > 1 ? smPlusDM[1] + plusDM : plusDM;
                smMinusDM[0] = smMinusDM.Count > 1 ? smMinusDM[1] + minusDM : minusDM;
                adxSeries[0] = 0;
                return;
            }
            smTR[0] = smTR[1] - smTR[1] / period + tr;
            smPlusDM[0] = smPlusDM[1] - smPlusDM[1] / period + plusDM;
            smMinusDM[0] = smMinusDM[1] - smMinusDM[1] / period + minusDM;
            double diP = smTR[0] > 0 ? 100.0 * smPlusDM[0] / smTR[0] : 0.0;
            double diM = smTR[0] > 0 ? 100.0 * smMinusDM[0] / smTR[0] : 0.0;
            double dx = (diP + diM) > 0 ? 100.0 * Math.Abs(diP - diM) / (diP + diM) : 0.0;
            adxSeries[0] = CurrentBar < period * 2 ? dx : (adxSeries[1] * (period - 1) + dx) / period;
        }

        private bool IsPivotLow(int left, int right)
        {
            double v = Low[right];
            for (int i = 1; i <= left; i++) if (Low[right + i] <= v) return false;
            for (int i = 1; i <= right; i++) if (Low[right - i] <= v) return false;
            return true;
        }
        private bool IsPivotHigh(int left, int right)
        {
            double v = High[right];
            for (int i = 1; i <= left; i++) if (High[right + i] >= v) return false;
            for (int i = 1; i <= right; i++) if (High[right - i] >= v) return false;
            return true;
        }

        #region Properties (hidden plot accessors)
        [Browsable(false), System.Xml.Serialization.XmlIgnore]
        public Series<double> LongScore { get { return Values[2]; } }
        [Browsable(false), System.Xml.Serialization.XmlIgnore]
        public Series<double> ShortScore { get { return Values[3]; } }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SneakySqueezeEngineProCc_v1_0_0[] cacheSneakySqueezeEngineProCc_v1_0_0;
		public SneakySqueezeEngineProCc_v1_0_0 SneakySqueezeEngineProCc_v1_0_0(CcTradeStyle tradeStyle, CcSignalMode signalMode, CcEngineMode engineMode, bool classicSlope, int cooldownBars, bool customLengths, int userBBLen, int userKCLen, double userBBMult, bool useHTF, bool autoHTF, int manualHTFMinutes, bool useADX, bool useVol, bool requireZero, bool allowNoSqueeze, bool useTR, double wideKC, double normalKC, double tightKC, bool normalizeMomentum, int accelSmoothing, bool showDivergences, int pivotLeft, int pivotRight, int maxPivotDistance, bool showTradeMap, int atrPeriod, double atrStopMult, double rewardMult, bool trailEnabled, double trailATR, bool exitOnFade, bool showExits, bool showPriceMap, bool showDashboard, bool showEarly, bool showSignals, bool showBackground)
		{
			return SneakySqueezeEngineProCc_v1_0_0(Input, tradeStyle, signalMode, engineMode, classicSlope, cooldownBars, customLengths, userBBLen, userKCLen, userBBMult, useHTF, autoHTF, manualHTFMinutes, useADX, useVol, requireZero, allowNoSqueeze, useTR, wideKC, normalKC, tightKC, normalizeMomentum, accelSmoothing, showDivergences, pivotLeft, pivotRight, maxPivotDistance, showTradeMap, atrPeriod, atrStopMult, rewardMult, trailEnabled, trailATR, exitOnFade, showExits, showPriceMap, showDashboard, showEarly, showSignals, showBackground);
		}

		public SneakySqueezeEngineProCc_v1_0_0 SneakySqueezeEngineProCc_v1_0_0(ISeries<double> input, CcTradeStyle tradeStyle, CcSignalMode signalMode, CcEngineMode engineMode, bool classicSlope, int cooldownBars, bool customLengths, int userBBLen, int userKCLen, double userBBMult, bool useHTF, bool autoHTF, int manualHTFMinutes, bool useADX, bool useVol, bool requireZero, bool allowNoSqueeze, bool useTR, double wideKC, double normalKC, double tightKC, bool normalizeMomentum, int accelSmoothing, bool showDivergences, int pivotLeft, int pivotRight, int maxPivotDistance, bool showTradeMap, int atrPeriod, double atrStopMult, double rewardMult, bool trailEnabled, double trailATR, bool exitOnFade, bool showExits, bool showPriceMap, bool showDashboard, bool showEarly, bool showSignals, bool showBackground)
		{
			if (cacheSneakySqueezeEngineProCc_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakySqueezeEngineProCc_v1_0_0.Length; idx++)
					if (cacheSneakySqueezeEngineProCc_v1_0_0[idx] != null && cacheSneakySqueezeEngineProCc_v1_0_0[idx].TradeStyle == tradeStyle && cacheSneakySqueezeEngineProCc_v1_0_0[idx].SignalMode == signalMode && cacheSneakySqueezeEngineProCc_v1_0_0[idx].EngineMode == engineMode && cacheSneakySqueezeEngineProCc_v1_0_0[idx].ClassicSlope == classicSlope && cacheSneakySqueezeEngineProCc_v1_0_0[idx].CooldownBars == cooldownBars && cacheSneakySqueezeEngineProCc_v1_0_0[idx].CustomLengths == customLengths && cacheSneakySqueezeEngineProCc_v1_0_0[idx].UserBBLen == userBBLen && cacheSneakySqueezeEngineProCc_v1_0_0[idx].UserKCLen == userKCLen && cacheSneakySqueezeEngineProCc_v1_0_0[idx].UserBBMult == userBBMult && cacheSneakySqueezeEngineProCc_v1_0_0[idx].UseHTF == useHTF && cacheSneakySqueezeEngineProCc_v1_0_0[idx].AutoHTF == autoHTF && cacheSneakySqueezeEngineProCc_v1_0_0[idx].ManualHTFMinutes == manualHTFMinutes && cacheSneakySqueezeEngineProCc_v1_0_0[idx].UseADX == useADX && cacheSneakySqueezeEngineProCc_v1_0_0[idx].UseVol == useVol && cacheSneakySqueezeEngineProCc_v1_0_0[idx].RequireZero == requireZero && cacheSneakySqueezeEngineProCc_v1_0_0[idx].AllowNoSqueeze == allowNoSqueeze && cacheSneakySqueezeEngineProCc_v1_0_0[idx].UseTR == useTR && cacheSneakySqueezeEngineProCc_v1_0_0[idx].WideKC == wideKC && cacheSneakySqueezeEngineProCc_v1_0_0[idx].NormalKC == normalKC && cacheSneakySqueezeEngineProCc_v1_0_0[idx].TightKC == tightKC && cacheSneakySqueezeEngineProCc_v1_0_0[idx].NormalizeMomentum == normalizeMomentum && cacheSneakySqueezeEngineProCc_v1_0_0[idx].AccelSmoothing == accelSmoothing && cacheSneakySqueezeEngineProCc_v1_0_0[idx].ShowDivergences == showDivergences && cacheSneakySqueezeEngineProCc_v1_0_0[idx].PivotLeft == pivotLeft && cacheSneakySqueezeEngineProCc_v1_0_0[idx].PivotRight == pivotRight && cacheSneakySqueezeEngineProCc_v1_0_0[idx].MaxPivotDistance == maxPivotDistance && cacheSneakySqueezeEngineProCc_v1_0_0[idx].ShowTradeMap == showTradeMap && cacheSneakySqueezeEngineProCc_v1_0_0[idx].AtrPeriod == atrPeriod && cacheSneakySqueezeEngineProCc_v1_0_0[idx].AtrStopMult == atrStopMult && cacheSneakySqueezeEngineProCc_v1_0_0[idx].RewardMult == rewardMult && cacheSneakySqueezeEngineProCc_v1_0_0[idx].TrailEnabled == trailEnabled && cacheSneakySqueezeEngineProCc_v1_0_0[idx].TrailATR == trailATR && cacheSneakySqueezeEngineProCc_v1_0_0[idx].ExitOnFade == exitOnFade && cacheSneakySqueezeEngineProCc_v1_0_0[idx].ShowExits == showExits && cacheSneakySqueezeEngineProCc_v1_0_0[idx].ShowPriceMap == showPriceMap && cacheSneakySqueezeEngineProCc_v1_0_0[idx].ShowDashboard == showDashboard && cacheSneakySqueezeEngineProCc_v1_0_0[idx].ShowEarly == showEarly && cacheSneakySqueezeEngineProCc_v1_0_0[idx].ShowSignals == showSignals && cacheSneakySqueezeEngineProCc_v1_0_0[idx].ShowBackground == showBackground && cacheSneakySqueezeEngineProCc_v1_0_0[idx].EqualsInput(input))
						return cacheSneakySqueezeEngineProCc_v1_0_0[idx];
			return CacheIndicator<SneakySqueezeEngineProCc_v1_0_0>(new SneakySqueezeEngineProCc_v1_0_0(){ TradeStyle = tradeStyle, SignalMode = signalMode, EngineMode = engineMode, ClassicSlope = classicSlope, CooldownBars = cooldownBars, CustomLengths = customLengths, UserBBLen = userBBLen, UserKCLen = userKCLen, UserBBMult = userBBMult, UseHTF = useHTF, AutoHTF = autoHTF, ManualHTFMinutes = manualHTFMinutes, UseADX = useADX, UseVol = useVol, RequireZero = requireZero, AllowNoSqueeze = allowNoSqueeze, UseTR = useTR, WideKC = wideKC, NormalKC = normalKC, TightKC = tightKC, NormalizeMomentum = normalizeMomentum, AccelSmoothing = accelSmoothing, ShowDivergences = showDivergences, PivotLeft = pivotLeft, PivotRight = pivotRight, MaxPivotDistance = maxPivotDistance, ShowTradeMap = showTradeMap, AtrPeriod = atrPeriod, AtrStopMult = atrStopMult, RewardMult = rewardMult, TrailEnabled = trailEnabled, TrailATR = trailATR, ExitOnFade = exitOnFade, ShowExits = showExits, ShowPriceMap = showPriceMap, ShowDashboard = showDashboard, ShowEarly = showEarly, ShowSignals = showSignals, ShowBackground = showBackground }, input, ref cacheSneakySqueezeEngineProCc_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SneakySqueezeEngineProCc_v1_0_0 SneakySqueezeEngineProCc_v1_0_0(CcTradeStyle tradeStyle, CcSignalMode signalMode, CcEngineMode engineMode, bool classicSlope, int cooldownBars, bool customLengths, int userBBLen, int userKCLen, double userBBMult, bool useHTF, bool autoHTF, int manualHTFMinutes, bool useADX, bool useVol, bool requireZero, bool allowNoSqueeze, bool useTR, double wideKC, double normalKC, double tightKC, bool normalizeMomentum, int accelSmoothing, bool showDivergences, int pivotLeft, int pivotRight, int maxPivotDistance, bool showTradeMap, int atrPeriod, double atrStopMult, double rewardMult, bool trailEnabled, double trailATR, bool exitOnFade, bool showExits, bool showPriceMap, bool showDashboard, bool showEarly, bool showSignals, bool showBackground)
		{
			return indicator.SneakySqueezeEngineProCc_v1_0_0(Input, tradeStyle, signalMode, engineMode, classicSlope, cooldownBars, customLengths, userBBLen, userKCLen, userBBMult, useHTF, autoHTF, manualHTFMinutes, useADX, useVol, requireZero, allowNoSqueeze, useTR, wideKC, normalKC, tightKC, normalizeMomentum, accelSmoothing, showDivergences, pivotLeft, pivotRight, maxPivotDistance, showTradeMap, atrPeriod, atrStopMult, rewardMult, trailEnabled, trailATR, exitOnFade, showExits, showPriceMap, showDashboard, showEarly, showSignals, showBackground);
		}

		public Indicators.SneakySqueezeEngineProCc_v1_0_0 SneakySqueezeEngineProCc_v1_0_0(ISeries<double> input , CcTradeStyle tradeStyle, CcSignalMode signalMode, CcEngineMode engineMode, bool classicSlope, int cooldownBars, bool customLengths, int userBBLen, int userKCLen, double userBBMult, bool useHTF, bool autoHTF, int manualHTFMinutes, bool useADX, bool useVol, bool requireZero, bool allowNoSqueeze, bool useTR, double wideKC, double normalKC, double tightKC, bool normalizeMomentum, int accelSmoothing, bool showDivergences, int pivotLeft, int pivotRight, int maxPivotDistance, bool showTradeMap, int atrPeriod, double atrStopMult, double rewardMult, bool trailEnabled, double trailATR, bool exitOnFade, bool showExits, bool showPriceMap, bool showDashboard, bool showEarly, bool showSignals, bool showBackground)
		{
			return indicator.SneakySqueezeEngineProCc_v1_0_0(input, tradeStyle, signalMode, engineMode, classicSlope, cooldownBars, customLengths, userBBLen, userKCLen, userBBMult, useHTF, autoHTF, manualHTFMinutes, useADX, useVol, requireZero, allowNoSqueeze, useTR, wideKC, normalKC, tightKC, normalizeMomentum, accelSmoothing, showDivergences, pivotLeft, pivotRight, maxPivotDistance, showTradeMap, atrPeriod, atrStopMult, rewardMult, trailEnabled, trailATR, exitOnFade, showExits, showPriceMap, showDashboard, showEarly, showSignals, showBackground);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SneakySqueezeEngineProCc_v1_0_0 SneakySqueezeEngineProCc_v1_0_0(CcTradeStyle tradeStyle, CcSignalMode signalMode, CcEngineMode engineMode, bool classicSlope, int cooldownBars, bool customLengths, int userBBLen, int userKCLen, double userBBMult, bool useHTF, bool autoHTF, int manualHTFMinutes, bool useADX, bool useVol, bool requireZero, bool allowNoSqueeze, bool useTR, double wideKC, double normalKC, double tightKC, bool normalizeMomentum, int accelSmoothing, bool showDivergences, int pivotLeft, int pivotRight, int maxPivotDistance, bool showTradeMap, int atrPeriod, double atrStopMult, double rewardMult, bool trailEnabled, double trailATR, bool exitOnFade, bool showExits, bool showPriceMap, bool showDashboard, bool showEarly, bool showSignals, bool showBackground)
		{
			return indicator.SneakySqueezeEngineProCc_v1_0_0(Input, tradeStyle, signalMode, engineMode, classicSlope, cooldownBars, customLengths, userBBLen, userKCLen, userBBMult, useHTF, autoHTF, manualHTFMinutes, useADX, useVol, requireZero, allowNoSqueeze, useTR, wideKC, normalKC, tightKC, normalizeMomentum, accelSmoothing, showDivergences, pivotLeft, pivotRight, maxPivotDistance, showTradeMap, atrPeriod, atrStopMult, rewardMult, trailEnabled, trailATR, exitOnFade, showExits, showPriceMap, showDashboard, showEarly, showSignals, showBackground);
		}

		public Indicators.SneakySqueezeEngineProCc_v1_0_0 SneakySqueezeEngineProCc_v1_0_0(ISeries<double> input , CcTradeStyle tradeStyle, CcSignalMode signalMode, CcEngineMode engineMode, bool classicSlope, int cooldownBars, bool customLengths, int userBBLen, int userKCLen, double userBBMult, bool useHTF, bool autoHTF, int manualHTFMinutes, bool useADX, bool useVol, bool requireZero, bool allowNoSqueeze, bool useTR, double wideKC, double normalKC, double tightKC, bool normalizeMomentum, int accelSmoothing, bool showDivergences, int pivotLeft, int pivotRight, int maxPivotDistance, bool showTradeMap, int atrPeriod, double atrStopMult, double rewardMult, bool trailEnabled, double trailATR, bool exitOnFade, bool showExits, bool showPriceMap, bool showDashboard, bool showEarly, bool showSignals, bool showBackground)
		{
			return indicator.SneakySqueezeEngineProCc_v1_0_0(input, tradeStyle, signalMode, engineMode, classicSlope, cooldownBars, customLengths, userBBLen, userKCLen, userBBMult, useHTF, autoHTF, manualHTFMinutes, useADX, useVol, requireZero, allowNoSqueeze, useTR, wideKC, normalKC, tightKC, normalizeMomentum, accelSmoothing, showDivergences, pivotLeft, pivotRight, maxPivotDistance, showTradeMap, atrPeriod, atrStopMult, rewardMult, trailEnabled, trailATR, exitOnFade, showExits, showPriceMap, showDashboard, showEarly, showSignals, showBackground);
		}
	}
}

#endregion
