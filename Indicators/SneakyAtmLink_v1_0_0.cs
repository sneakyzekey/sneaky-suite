#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// ─────────────────────────────────────────────────────────────────────────
//  SneakyAtmLink
//  -------------
//  A tiny, settings-free "sensor" indicator. It has no plots, no UI, and
//  nothing to configure -- drop it on whichever chart you want the
//  SneakyATM AddOn window to be able to watch, and it publishes that
//  chart's instrument, ATR(14) (plus a real 1-minute ATR), the direction
//  of its last several closed candles, a rolling volume-spike ratio, and
//  the prior completed session's High/Low/Close into a small static
//  registry (SneakyAtmLinkRegistry) -- everything SneakyATM's OBSERVATIONS
//  advisor rules (pullback run, volume spike, distance-to-prior-level) need.
//
//  Why this exists: NinjaTrader AddOns (unlike Indicators) have no direct
//  access to an arbitrary chart's ChartControl or bar data -- only an
//  Indicator sitting ON that chart does. SneakyATM is a standalone AddOn
//  window, so it can't reach into a chart on its own; this indicator is
//  the one-time "drop it on the chart" step that lets the AddOn read
//  exactly what that chart is showing, with no risk of drifting from a
//  second, independent data feed.
//
//  Registry entries are keyed by "<Instrument.FullName> — <BarsPeriod>"
//  so two charts on the same instrument but different timeframes don't
//  collide and both show up as distinct choices in the ATM's chart picker.
//  Removed from the registry automatically when the indicator unloads
//  (chart closed, indicator removed, workspace closed), so the ATM never
//  shows a chart that's no longer open.
// ─────────────────────────────────────────────────────────────────────────

namespace NinjaTrader.NinjaScript.Indicators
{
    public static class SneakyAtmLinkRegistry
    {
        public sealed class ChartState
        {
            public string     Label;              // display label, e.g. "NQ 12-26 — 1 Min"
            public string     InstrumentFullName;
            public Instrument Instrument;          // live reference, read cross-thread the same way SneakyEquityGate passes Instrument between threads
            public double     TickSize;
            public double   Atr;                 // ATR(14) on the CHART's own timeframe, in price units
            public double   AtrFast;              // ATR(14) on a real 1-minute series, in price units (0 if unavailable -- caller should fall back)
            public int[]    Dirs;                 // last N closed-candle directions, oldest first: +1 up, -1 down, 0 flat
            public double   VolumeRatio;           // last closed bar's volume / rolling 20-bar average volume (0 until enough bars seen)
            public bool     PriorOhlcReady;         // true once a full prior session has been observed
            public double   PriorHigh;              // prior completed session's high (0 if !PriorOhlcReady)
            public double   PriorLow;               // prior completed session's low  (0 if !PriorOhlcReady)
            public double   PriorClose;             // prior completed session's close (0 if !PriorOhlcReady)
            public int      RangeBars;              // how many of the most recent bars RangeTicks/RangeTight span (0 until enough bars seen)
            public int      RangeTicks;              // high-low span of the last RangeBars bars, in ticks (0 until enough bars seen)
            public bool     RangeTight;              // true once RangeTicks has stayed inside a tight band for RangeBars bars running
            public int      SarFlips;                // # of hand-rolled Parabolic SAR trend flips in the last RangeBars bars (choppiness proxy)
            public DateTime LastBarTimeLocal;      // close time of the most recent bar included
            public DateTime LastUpdateUtc;         // wall-clock time this entry was (re)published

            // true when the last 'run' candles in Dirs all share the same non-zero direction
            public bool TryGetConsecutiveRun(int run, out int direction)
            {
                direction = 0;
                if (Dirs == null || run <= 0 || Dirs.Length < run) return false;
                int dir = Dirs[Dirs.Length - 1];
                if (dir == 0) return false;
                for (int i = Dirs.Length - 2; i >= Dirs.Length - run; i--)
                    if (Dirs[i] != dir) return false;
                direction = dir;
                return true;
            }
        }

        // ── Tier plan: the PREVIEW LONG/SHORT panel's on-chart line+label lines ──
        // Published by the SneakyATM AddOn window every ~400ms while a preview is open
        // (same key as ChartState above -- "<Instrument.FullName> — <BarsPeriod>"), and
        // picked up by whichever SneakyAtmLink is sitting on that same chart so IT can
        // actually draw (an AddOn window has no chart/ChartControl of its own to draw
        // onto -- see the file header). Cleared the moment the preview is canceled, a
        // trade is taken, or a different direction/chart is picked.
        public sealed class TierLine
        {
            public string Label;         // "SHORT ENTRY", "QUICK HIT", "MEDIUM HIT", "LARGE HIT", "HOME RUN"
            public double Price;
            public double DollarTarget;  // 0 for the entry line itself (no target on that one)
            public int    ColorId;       // 0 = entry (neutral), 1 = Quick, 2 = Medium, 3 = Large, 4 = Home Run
        }

        public sealed class TierPlan
        {
            public int            Dir;    // +1 long, -1 short
            public List<TierLine> Lines;
            public long            Version; // bumped on every publish so a watcher can tell "new plan" from "same plan, redundant tick"
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, ChartState> _states = new Dictionary<string, ChartState>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, TierPlan> _tierPlans = new Dictionary<string, TierPlan>(StringComparer.OrdinalIgnoreCase);
        private static long _tierPlanVersionSeq;

        public static void Publish(string key, ChartState state)
        {
            lock (_lock) { _states[key] = state; }
        }

        public static void Remove(string key)
        {
            lock (_lock) { _states.Remove(key); }
        }

        public static void PublishTierPlan(string key, int dir, List<TierLine> lines)
        {
            lock (_lock)
            {
                _tierPlans[key] = new TierPlan { Dir = dir, Lines = lines, Version = System.Threading.Interlocked.Increment(ref _tierPlanVersionSeq) };
            }
        }

        public static void ClearTierPlan(string key)
        {
            lock (_lock) { _tierPlans.Remove(key); }
        }

        public static TierPlan TryGetTierPlan(string key)
        {
            lock (_lock)
            {
                TierPlan p;
                return _tierPlans.TryGetValue(key, out p) ? p : null;
            }
        }

        // keys of entries updated within the last 'maxAgeSec' seconds -- what the ATM's picker should offer
        public static List<string> LivePublishers(double maxAgeSec)
        {
            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;
                return _states.Where(kv => (now - kv.Value.LastUpdateUtc).TotalSeconds <= maxAgeSec)
                              .Select(kv => kv.Key)
                              .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                              .ToList();
            }
        }

        public static ChartState TryGet(string key)
        {
            lock (_lock)
            {
                ChartState s;
                return _states.TryGetValue(key, out s) ? s : null;
            }
        }
    }

    public class SneakyAtmLink : Indicator
    {
        private const int Capacity     = 20;   // candle directions kept per chart -- plenty for any "N in a row" check
        private const int FastAtrPeriod = 14;  // same period as the main ATR, just computed on a real 1-minute series
        private const int VolBaselineLen = 20; // bars of volume history kept for the rolling average baseline
        private const int RangeLookback = 20;  // bars of range-compression / SAR-flip history kept (matches the 20-bar window the advisor talks about)
        private const double RangeTightFraction = 0.6;  // "tight" means this many ATRs or fewer span the whole lookback window

        private ATR         atrI;
        private string      key;
        private List<int>   dirs;

        // Range-compression tracking: a plain rolling window of the last RangeLookback bars' highs
        // and lows. RangeTicks is just (window high - window low) in ticks; RangeTight compares that
        // span against RangeTightFraction * ATR * RangeLookback as a rough "has this been coiling"
        // read -- not a published indicator, just enough to drive the advisor's "tight range" note.
        private List<double> rangeHighs;
        private List<double> rangeLows;

        // Hand-rolled Parabolic SAR (standard Wilder formula, own small state machine -- same
        // "replicate the formula by hand" approach already used for the fast ATR above, so this file
        // doesn't take on a dependency on NinjaTrader's built-in ParabolicSAR's exact factory-method
        // signature). Only used to count trend flips over the last RangeLookback bars as a choppiness
        // proxy for the advisor's "SAR flipped N times" note -- the actual SAR price itself is never
        // published or drawn.
        private bool          sarInit;
        private bool          sarUpTrend;
        private double        sarValue;
        private double        sarEp;      // extreme point
        private double        sarAf;      // acceleration factor
        private const double  SarStep = 0.02, SarMax = 0.2;
        private List<bool>    sarUpHistory;   // true = uptrend, oldest first -- flips counted across this window

        // Volume-spike baseline: a plain rolling average of the last VolBaselineLen CLOSED bars'
        // volume, excluding the bar currently being evaluated (so a huge current bar can't inflate
        // its own baseline and mask itself).
        private List<double> volHistory;

        // Prior-session H/L/C, tracked the same way NinjaTrader's own built-in @PriorDayOHLC.cs does:
        // a Data.SessionIterator plus a running "current session" high/low/close that rolls into
        // "prior session" the moment a new trading day is detected.
        private NinjaTrader.Data.SessionIterator sessionIter;
        private DateTime    curSessionDate = NinjaTrader.Core.Globals.MinDate;
        private double      curHigh, curLow, curClose;
        private double      priorHigh, priorLow, priorClose;
        private bool        priorOhlcReady;

        // Real 1-minute ATR, hand-rolled rather than via ATR(Closes[1], period): NinjaTrader's own
        // generated ATR(ISeries<double>, period) overload only uses the 'input' series for cache
        // matching -- its OnBarUpdate() always reads High[0]/Low[0]/Close[1] off whichever bars
        // context is current, so calling it against a secondary series' Close stream would silently
        // compute the PRIMARY series' ATR, not the secondary one. This replicates NinjaTrader's own
        // @ATR.cs Wilder-smoothing formula by hand against BarsInProgress==1's own High/Low/Close.
        private bool         fastSeriesOk;
        private double       fastAtrValue;

        // Heartbeat: SneakyAtmLink only otherwise re-publishes inside OnBarUpdate() (i.e. on a new
        // closed candle). Over a weekend/holiday, or any stretch with no new bar, that means the
        // registry entry's LastUpdateUtc goes stale and SneakyATM's "LivePublishers" freshness check
        // (currently a 300-second window) silently drops this chart from its WATCHING picker -- even
        // though the indicator is still very much loaded. This timer re-publishes the last known
        // state every 20s, independent of market data, so the chart stays selectable through a market
        // closure. It only runs once Realtime (no point ticking during a historical-only backtest),
        // and is stopped/disposed on Terminated so it can't outlive the indicator/chart.
        private SneakyAtmLinkRegistry.ChartState lastState;
        private System.Timers.Timer              heartbeat;

        // Tier-plan drawing: polls the registry (populated by the SneakyATM AddOn window,
        // see SneakyAtmLinkRegistry.TierPlan above) on a UI-thread timer so Draw.* calls are
        // safe, and redraws only when the published Version actually changes -- so an idle
        // preview (AddOn republishing the identical plan every ~400ms) doesn't thrash the
        // chart's draw-object list.
        private DispatcherTimer tierTimer;
        private long            lastTierVersion = -1;
        private int             drawnTierLineCount;
        // Captured every primary-series closed bar (see OnBarUpdate). Bar-anchored Draw.Text
        // calls made from the tier-drawing timer use THIS timestamp instead of barsAgo, so they
        // no longer depend on whatever BarsInProgress happens to be parked at when the timer
        // fires (see UpdateTierDrawings for why that mattered).
        private DateTime        lastPrimaryBarTime = NinjaTrader.Core.Globals.MinDate;
        private static readonly Brush       EntryLineBrush = Brushes.Gainsboro;
        private static readonly Brush       QuickLineBrush = CreateFrozenBrush(0x2F, 0x8F, 0xE0);
        private static readonly Brush       MedLineBrush   = CreateFrozenBrush(0xD9, 0xA7, 0x2C);
        private static readonly Brush       LargeLineBrush = CreateFrozenBrush(0x8B, 0x5C, 0xF6);
        private static readonly Brush       HomeLineBrush  = Brushes.White;
        private static readonly Brush       StopLineBrush  = CreateFrozenBrush(0xFF, 0x4D, 0x6D);   // hot-pink/red, distinct from the Red (Short/Large) palette used elsewhere
        private static readonly SimpleFont  TierFont       = new SimpleFont("Segoe UI", 12) { Bold = true };

        // Brushes.Gainsboro/.White above are already frozen, thread-agnostic system brushes, but a
        // plain "new SolidColorBrush(...)" is a Freezable bound to whichever thread first touches this
        // type (not necessarily the chart's own UI dispatcher) -- Freeze() makes it immutable and
        // removes that thread affinity so Draw.* can safely use it from the chart's UI thread later.
        private static Brush CreateFrozenBrush(byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "SneakyAtmLink";
                Description = "No settings. Drop this on a chart to let the SneakyATM AddOn window read that chart's instrument, ATR and recent candle directions.";
                Calculate               = Calculate.OnBarClose;
                IsOverlay               = true;     // sits on the price panel, draws nothing
                DisplayInDataBox        = false;
                PaintPriceMarkers       = false;
                IsSuspendedWhileInactive = false;
            }
            else if (State == State.Configure)
            {
                // Secondary series: a REAL 1-minute feed for this same instrument, regardless of what
                // timeframe the chart itself is on. Wrapped in try/catch -- if some instrument/feed
                // combination can't support it, we just fall back (AtrFast stays 0, callers already
                // treat that as "unavailable").
                try
                {
                    AddDataSeries(BarsPeriodType.Minute, 1);
                    fastSeriesOk = true;
                }
                catch
                {
                    fastSeriesOk = false;
                }
            }
            else if (State == State.DataLoaded)
            {
                atrI = ATR(14);
                dirs = new List<int>(Capacity);
                volHistory = new List<double>(VolBaselineLen);
                rangeHighs = new List<double>(RangeLookback);
                rangeLows = new List<double>(RangeLookback);
                sarUpHistory = new List<bool>(RangeLookback);
                sessionIter = new NinjaTrader.Data.SessionIterator(Bars);
            }
            else if (State == State.Historical)
            {
                key = BuildKey();
            }
            else if (State == State.Realtime)
            {
                heartbeat = new System.Timers.Timer(20000) { AutoReset = true };
                heartbeat.Elapsed += (s, e) =>
                {
                    // Just touch the timestamp and re-publish the same snapshot -- no NinjaScript
                    // bar/price access here, this runs on a background timer thread, not the engine
                    // thread, so it only touches our own plain-data ChartState and the registry's
                    // own lock-protected dictionary, both of which are safe to touch off-thread.
                    var snap = lastState;
                    if (snap == null || string.IsNullOrEmpty(key)) return;
                    snap.LastUpdateUtc = DateTime.UtcNow;
                    SneakyAtmLinkRegistry.Publish(key, snap);
                };
                heartbeat.Start();

                // Separate UI-thread timer for tier-plan drawing. State.Realtime actually runs on
                // NinjaTrader's own per-instrument engine thread, NOT the chart's UI thread -- a
                // DispatcherTimer built on Dispatcher.CurrentDispatcher there silently never ticks
                // (nothing pumps that thread's message loop). ChartControl.Dispatcher is the
                // indicator's actual chart window's real UI dispatcher regardless of which thread
                // this State callback runs on, so Draw.* calls from the timer land correctly.
                Dispatcher uiDispatcher = ChartControl != null ? ChartControl.Dispatcher : Dispatcher.CurrentDispatcher;
                tierTimer = new DispatcherTimer(DispatcherPriority.Background, uiDispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
                tierTimer.Tick += (s, e) => UpdateTierDrawings();
                tierTimer.Start();
            }
            else if (State == State.Terminated)
            {
                if (heartbeat != null)
                {
                    heartbeat.Stop();
                    heartbeat.Dispose();
                    heartbeat = null;
                }
                if (tierTimer != null)
                {
                    tierTimer.Stop();
                    tierTimer = null;
                }
                ClearTierDrawings();
                if (!string.IsNullOrEmpty(key))
                {
                    SneakyAtmLinkRegistry.Remove(key);
                    SneakyAtmLinkRegistry.ClearTierPlan(key);
                }
            }
        }

        private string BuildKey()
        {
            string instr  = Instrument != null ? Instrument.FullName : "?";
            string period = BarsPeriod != null ? BarsPeriod.ToString() : "?";
            return instr + " — " + period;
        }

        protected override void OnBarUpdate()
        {
            // Once a secondary series is added, OnBarUpdate fires for BOTH series, and Close[0]/
            // Open[0]/Time[0] etc. refer to whichever series is current -- NOT always the primary
            // chart. This branch is required, not optional, once AddDataSeries is in play.
            if (BarsInProgress == 1)
            {
                UpdateFastAtr();
                return;
            }

            if (BarsInProgress != 0) return;   // belt-and-suspenders; primary is always index 0

            if (CurrentBar < 1) return;

            lastPrimaryBarTime = Time[0];

            int dir = Close[0] > Open[0] ? 1 : (Close[0] < Open[0] ? -1 : 0);
            dirs.Add(dir);
            if (dirs.Count > Capacity) dirs.RemoveAt(0);

            // Volume-spike ratio: current bar's volume vs. the average of the PRIOR bars already in
            // volHistory (computed before this bar's own volume is added in, so a huge bar can't
            // inflate the baseline it's being measured against).
            double volRatio = 0;
            if (volHistory.Count >= 5)
            {
                double sum = 0;
                for (int i = 0; i < volHistory.Count; i++) sum += volHistory[i];
                double avg = sum / volHistory.Count;
                if (avg > 0) volRatio = Volume[0] / avg;
            }
            volHistory.Add(Volume[0]);
            if (volHistory.Count > VolBaselineLen) volHistory.RemoveAt(0);

            UpdatePriorSessionOhlc();

            rangeHighs.Add(High[0]); if (rangeHighs.Count > RangeLookback) rangeHighs.RemoveAt(0);
            rangeLows.Add(Low[0]);   if (rangeLows.Count > RangeLookback) rangeLows.RemoveAt(0);
            int rangeBarsReady = Math.Min(rangeHighs.Count, RangeLookback);
            double rangeHigh = rangeHighs.Count > 0 ? rangeHighs.Max() : 0;
            double rangeLow = rangeLows.Count > 0 ? rangeLows.Min() : 0;
            double tickSz = Instrument.MasterInstrument.TickSize;
            int rangeTicks = tickSz > 0 ? (int)Math.Round((rangeHigh - rangeLow) / tickSz) : 0;
            bool rangeTight = rangeBarsReady >= RangeLookback && atrI.Value[0] > 0 &&
                               (rangeHigh - rangeLow) <= RangeTightFraction * atrI.Value[0] * RangeLookback;

            UpdateSar();
            int sarFlips = 0;
            for (int i = 1; i < sarUpHistory.Count; i++) if (sarUpHistory[i] != sarUpHistory[i - 1]) sarFlips++;

            if (string.IsNullOrEmpty(key)) key = BuildKey();

            var state = new SneakyAtmLinkRegistry.ChartState
            {
                Label               = key,
                InstrumentFullName  = Instrument.FullName,
                Instrument          = Instrument,
                TickSize            = Instrument.MasterInstrument.TickSize,
                Atr                 = atrI.Value[0],
                AtrFast             = fastSeriesOk ? fastAtrValue : 0,
                Dirs                = dirs.ToArray(),
                VolumeRatio         = volRatio,
                PriorOhlcReady      = priorOhlcReady,
                PriorHigh           = priorHigh,
                PriorLow            = priorLow,
                PriorClose          = priorClose,
                RangeBars           = rangeBarsReady,
                RangeTicks          = rangeTicks,
                RangeTight          = rangeTight,
                SarFlips            = sarFlips,
                LastBarTimeLocal    = Time[0],
                LastUpdateUtc       = DateTime.UtcNow
            };
            lastState = state;   // so the heartbeat has something to re-publish between real bar closes
            SneakyAtmLinkRegistry.Publish(key, state);
        }

        // Prior completed session's H/L/C, tracked the same way NinjaTrader's own built-in
        // @PriorDayOHLC.cs does: roll the running "current session" OHLC into "prior session" the
        // instant the SessionIterator says we've crossed into a new trading day. Only meaningful for
        // intraday bars (matches @PriorDayOHLC.cs's own IsIntraday guard).
        private void UpdatePriorSessionOhlc()
        {
            if (!Bars.BarsType.IsIntraday) return;

            DateTime tradingDay = sessionIter.GetTradingDay(Time[0]);
            if (curSessionDate != tradingDay || curHigh == 0)
            {
                if (curHigh != 0)   // we had a real "current" session to roll into "prior"
                {
                    priorHigh = curHigh; priorLow = curLow; priorClose = curClose;
                    priorOhlcReady = true;
                }
                curHigh = High[0]; curLow = Low[0]; curClose = Close[0];
                curSessionDate = tradingDay;
            }
            else
            {
                curHigh  = Math.Max(curHigh, High[0]);
                curLow   = Math.Min(curLow, Low[0]);
                curClose = Close[0];
            }
        }

        // Standard Wilder Parabolic SAR, hand-rolled (see the field comments above for why). Only
        // the uptrend/downtrend flag is kept, in a short rolling window, to count flips -- the SAR
        // price itself is never read by anything.
        private void UpdateSar()
        {
            if (!sarInit)
            {
                sarInit = true;
                sarUpTrend = Close[0] >= Open[0];
                sarValue = sarUpTrend ? Low[0] : High[0];
                sarEp = sarUpTrend ? High[0] : Low[0];
                sarAf = SarStep;
            }
            else
            {
                double newSar = sarValue + sarAf * (sarEp - sarValue);
                if (sarUpTrend)
                {
                    newSar = Math.Min(newSar, Math.Min(Low[1], Low[0]));
                    if (High[0] > sarEp) { sarEp = High[0]; sarAf = Math.Min(sarAf + SarStep, SarMax); }
                    if (Low[0] < newSar)
                    {
                        sarUpTrend = false;
                        newSar = sarEp;
                        sarEp = Low[0];
                        sarAf = SarStep;
                    }
                }
                else
                {
                    newSar = Math.Max(newSar, Math.Max(High[1], High[0]));
                    if (Low[0] < sarEp) { sarEp = Low[0]; sarAf = Math.Min(sarAf + SarStep, SarMax); }
                    if (High[0] > newSar)
                    {
                        sarUpTrend = true;
                        newSar = sarEp;
                        sarEp = High[0];
                        sarAf = SarStep;
                    }
                }
                sarValue = newSar;
            }
            sarUpHistory.Add(sarUpTrend);
            if (sarUpHistory.Count > RangeLookback) sarUpHistory.RemoveAt(0);
        }

        // Draws the SneakyATM preview's entry + QUICK/MEDIUM/LARGE/HOME RUN HIT lines directly
        // on this chart, mirroring the reference tool's on-chart behavior: dashed horizontal
        // lines at each level, labeled with the tier name, price, and $ target, that appear the
        // moment a preview forms and disappear the moment it's canceled. The AddOn window can't
        // draw on a chart itself (see file header), so it just publishes a TierPlan and this
        // indicator -- which IS sitting on the chart -- does the actual drawing.
        private void UpdateTierDrawings()
        {
            try
            {
                if (string.IsNullOrEmpty(key)) return;

                // AddDataSeries(BarsPeriodType.Minute, 1) in State.Configure adds a SECOND series
                // (BarsInProgress==1, the real 1-minute ATR feed) that isn't plotted on any chart
                // panel. BarsInProgress is a plain instance field that OnBarUpdate leaves set to
                // whichever series it last ran for -- it is NOT reset between calls. This timer
                // fires on the UI thread independent of OnBarUpdate, so by the time it runs,
                // BarsInProgress can easily be sitting at 1 (last set by the secondary series'
                // own bar close) rather than 0 (the chart's own primary series). A bar-anchored
                // Draw.Text call made while BarsInProgress==1 resolves its x-position against that
                // un-plotted secondary series -- it still constructs and gets added to the
                // drawing-objects collection (so it never throws and never returns null), it just
                // has no chart panel to actually paint onto, so it's silently invisible. This is
                // exactly why Draw.HorizontalLine (no barsAgo) and Draw.TextFixed (no barsAgo)
                // always rendered fine while every bar-anchored Draw.Text call never appeared.
                // Force it back to the primary series before any bar-anchored Draw.* call here.
                BarsInProgress = 0;
                SneakyAtmLinkRegistry.TierPlan plan = SneakyAtmLinkRegistry.TryGetTierPlan(key);

                if (plan == null || plan.Lines == null || plan.Lines.Count == 0)
                {
                    if (lastTierVersion != -1)
                    {
                        ClearTierDrawings();
                        lastTierVersion = -1;
                    }
                    return;
                }

                if (plan.Version == lastTierVersion) return;   // identical plan already on the chart

                ClearTierDrawings();
                for (int i = 0; i < plan.Lines.Count; i++)
                {
                    SneakyAtmLinkRegistry.TierLine ln = plan.Lines[i];
                    Brush brush = TierColor(ln.ColorId);
                    string tag = "atmTier" + i;

                    Draw.HorizontalLine(this, tag, ln.Price, brush, DashStyleHelper.Dash, 2);

                    string priceTxt = FormatPx(ln.Price);
                    string label = ln.DollarTarget == 0
                        ? ln.Label + "  " + priceTxt
                        : ln.Label + "  " + priceTxt + "   " + (ln.DollarTarget >= 0 ? "+$" : "-$") +
                          Math.Abs(ln.DollarTarget).ToString("0.00", CultureInfo.InvariantCulture);

                    // Solid colored tag (not bare floating text) -- matches the reference tool's
                    // QUICK/MEDIUM/LARGE/HOME RUN boxes, and is far easier to actually see on a busy
                    // chart than plain text in the tier color was.
                    //
                    // Anchored by TIMESTAMP (lastPrimaryBarTime), not barsAgo -- a barsAgo-anchored
                    // call resolves its x-position against whichever series BarsInProgress happens
                    // to be pointing at, and this timer fires on the UI thread independent of
                    // OnBarUpdate, so it can't be trusted to still be 0 (this indicator also runs a
                    // secondary 1-minute series for the fast-ATR calc). isAutoScale must be true --
                    // this filled/bordered box variant of Draw.Text doesn't paint at all with it
                    // false, even though the price axis doesn't need to expand to fit it.
                    //
                    // Entry (Gainsboro) and Home Run (White) are both light backgrounds -- white
                    // text on either is low-contrast-to-invisible (Home Run was literally
                    // white-on-white). Dark text for those two, white text for the darker/
                    // saturated Quick/Medium/Large tier colors where white reads fine.
                    Brush labelTextBrush = (ln.ColorId == 0 || ln.ColorId == 4) ? Brushes.Black : Brushes.White;
                    Draw.Text(this, tag + "lbl", true, label, lastPrimaryBarTime, ln.Price, -14, labelTextBrush, TierFont,
                        System.Windows.TextAlignment.Right, Brushes.Black, brush, 100);
                }
                drawnTierLineCount = plan.Lines.Count;
                lastTierVersion = plan.Version;
            }
            catch (Exception ex) { Print("[SneakyAtmLink] UpdateTierDrawings EXCEPTION: " + ex); }
        }

        private void ClearTierDrawings()
        {
            int n = Math.Max(drawnTierLineCount, 8);   // a little slack in case the tier count ever changes
            for (int i = 0; i < n; i++)
            {
                RemoveDrawObject("atmTier" + i);
                RemoveDrawObject("atmTier" + i + "lbl");
            }
            drawnTierLineCount = 0;
        }

        private static Brush TierColor(int id)
        {
            switch (id)
            {
                case 1: return QuickLineBrush;
                case 2: return MedLineBrush;
                case 3: return LargeLineBrush;
                case 4: return HomeLineBrush;
                case 5: return StopLineBrush;
                default: return EntryLineBrush;
            }
        }

        private string FormatPx(double price)
        {
            try { return Instrument.MasterInstrument.FormatPrice(price); } catch { return price.ToString("0.##", CultureInfo.InvariantCulture); }
        }

        // Hand-rolled Wilder ATR against the real 1-minute series (BarsInProgress == 1), using the
        // exact formula NinjaTrader's own built-in @ATR.cs uses, but reading that secondary series'
        // own High/Low/Close rather than the primary chart's.
        private void UpdateFastAtr()
        {
            double high0 = Highs[1][0];
            double low0  = Lows[1][0];

            if (CurrentBars[1] <= 0)
            {
                fastAtrValue = high0 - low0;
            }
            else
            {
                double close1    = Closes[1][1];
                double trueRange = Math.Max(Math.Abs(low0 - close1), Math.Max(high0 - low0, Math.Abs(high0 - close1)));
                int    n          = Math.Min(CurrentBars[1] + 1, FastAtrPeriod);
                fastAtrValue      = ((n - 1) * fastAtrValue + trueRange) / n;
            }
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SneakyAtmLink[] cacheSneakyAtmLink;
		public SneakyAtmLink SneakyAtmLink()
		{
			return SneakyAtmLink(Input);
		}

		public SneakyAtmLink SneakyAtmLink(ISeries<double> input)
		{
			if (cacheSneakyAtmLink != null)
				for (int idx = 0; idx < cacheSneakyAtmLink.Length; idx++)
					if (cacheSneakyAtmLink[idx] != null &&  cacheSneakyAtmLink[idx].EqualsInput(input))
						return cacheSneakyAtmLink[idx];
			return CacheIndicator<SneakyAtmLink>(new SneakyAtmLink(), input, ref cacheSneakyAtmLink);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SneakyAtmLink SneakyAtmLink()
		{
			return indicator.SneakyAtmLink(Input);
		}

		public Indicators.SneakyAtmLink SneakyAtmLink(ISeries<double> input )
		{
			return indicator.SneakyAtmLink(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SneakyAtmLink SneakyAtmLink()
		{
			return indicator.SneakyAtmLink(Input);
		}

		public Indicators.SneakyAtmLink SneakyAtmLink(ISeries<double> input )
		{
			return indicator.SneakyAtmLink(input);
		}
	}
}

#endregion
