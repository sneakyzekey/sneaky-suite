#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class LinRegUTBotReversalStrategy : Strategy
    {
        private ATR atr;
        private UTBotAlertsNT8 utBotSignalIndicator;

        private Series<double> utSource;
        private Series<double> utTrailingStop;

        private Series<double> priceOpen;
        private Series<double> priceHigh;
        private Series<double> priceLow;
        private Series<double> priceClose;

        private LinReg linRegOpen;
        private LinReg linRegHigh;
        private LinReg linRegLow;
        private LinReg linRegClose;
        private SMA signalSma;
        private EMA signalEma;

        private Grid controlPanel;
        private Grid chartHostGrid;
        private TextBlock panelTitle;
        private TextBlock panelInfo;
        private TextBlock panelSignal;
        private TranslateTransform panelDragTransform;
        private Point panelDragStart;
        private bool panelDragging;
        private Button autoButton, longButton, shortButton;
        private Button manualLongButton, manualShortButton;
        private Button autoBreakEvenButton, breakEvenButton, breakEvenPlusButton;
        private Button atrTrailButton, slDownButton, slUpButton, tpDownButton, tpUpButton, closeButton;
        private Button histTradesButton;

        private volatile bool closeRequested;
        private volatile bool breakEvenRequested;
        private volatile bool breakEvenPlusRequested;
        private volatile bool riskUpdateRequested;
        private double manualStopPrice;
        private double manualTargetPrice;
        private bool autoBreakEvenTriggered;
        private bool atrTrailActivated;
        private MarketPosition trackedPosition = MarketPosition.Flat;
        private HorizontalLine movableStopLine;
        private const string MovableStopLineTag = "LinRegUTMovableStopLine";
        private int lastSignal;
        private string lastFilterState = "WAIT";
        private MarketPosition pendingReversal = MarketPosition.Flat;
        private int pendingUtDirection;
        private double currentLinRegSignalLine;

        // ---- Order Flow Confluence (optional, no Level 2 depth required) ----
        // Self-contained CVD (cumulative volume delta) regression, computed the same way as
        // SneakyOrderFlowDashboard_v1_0_0's own "3c CVD Regression" section (same quote-rule +
        // tick-rule trade classification, same rolling-window OLS fit) -- deliberately duplicated
        // here rather than instantiating the dashboard indicator from this strategy, since the
        // dashboard's constructor takes dozens of positional parameters that change across its own
        // patches, which would make this strategy fragile to unrelated dashboard edits (the same
        // "no dependency on another file's exact shape" lesson as the v1.0.0 patch 5 VWAP fix on
        // that file). Needs only Level 1 trade prints + bid/ask, so it works identically whether or
        // not the feed provides real Level 2 depth.
        private readonly object cfLock = new object();
        private double cfCumulativeDelta;
        private double cfLastTradePrice = double.NaN;
        private string cfLastTradeSide;
        private List<double> cfHCvd = new List<double>();
        private double cfSlope, cfR2;

        // Diagnostic counters for the entry-stop clearance check (MinStopClearanceTicks) added to
        // ReverseOrEnterLong/ReverseOrEnterShort. A valid UT+LinReg signal can, in rare cases, arrive with
        // the LinReg stop line too close to the live market to satisfy the clearance margin -- these let
        // the user tell "no signal fired" apart from "a signal fired but was blocked here" without guessing.
        private long clearanceBlockedCount;
        private string lastClearanceBlockText = "";

        // ---- Trade markers (2026-09-18) ----
        // Replicates NinjaTrader's own native fill labels ("Long"/"Short"/"ReverseToShort"/
        // "Profit target"/"Stop loss", etc., at the actual fill price) via our own Draw calls, so we
        // can scope them to the current session only -- NinjaTrader's native "Plot Executions" chart
        // setting has no such filter, it's all-history-or-nothing (see Data Series -> Trades on the
        // chart, or Ctrl+E, for the native all-or-nothing toggle). currentSessionBegin tracks the
        // start of the most recent session seen so far; every fill is logged once (regardless of
        // current visibility) so flipping ShowHistoricalTradeMarkers back on, or a new session
        // starting, can redraw/hide the right set without losing history.
        private DateTime currentSessionBegin = DateTime.MinValue;
        private int tradeMarkerSeq;
        private class TradeMarkerRecord
        {
            public int barIndex;
            public double price;
            public string orderName;
            public bool isBuy;
            public bool isLongSide;
            public DateTime time;
            public string tag;
            public bool visible;
        }
        private List<TradeMarkerRecord> tradeMarkerLog = new List<TradeMarkerRecord>();

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                         = "UT Bot reversal strategy filtered by Humble Linear Regression candles.";
                Name                                = "LinRegUTBotReversalStrategy";
                Calculate                           = Calculate.OnBarClose;
                EntriesPerDirection                 = 1;
                EntryHandling                       = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy        = true;
                ExitOnSessionCloseSeconds           = 30;
                IsInstantiatedOnEachOptimizationIteration = true;
                StartBehavior                       = StartBehavior.WaitUntilFlat;
                RealtimeErrorHandling               = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling                  = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade                 = 20;

                Quantity                            = 1;
                KeyValue                            = 2.0;
                AtrPeriod                           = 1;
                UseHeikinAshiSource                 = false;
                LinearRegressionLength              = 11;
                SignalLength                        = 11;
                UseSmaSignal                        = true;
                UseLinePriceFilter                  = false;
                ShowTradeMarkers                    = true;
                HideNativeCandles                   = false;

                EnableAutomation                    = true;
                EnableLongs                         = true;
                EnableShorts                        = true;
                ShowControlPanel                    = true;
                PanelWidth                          = 360;
                PanelRightOffset                    = 10;
                PanelTopOffset                      = 35;

                UseProfitTarget                     = false;
                ProfitTargetTicks                   = 80;
                RiskAdjustTicks                     = 4;
                UseFixedStopTicks                   = false;
                StopLossTicks                       = 40;
                ShowHistoricalTradeMarkers           = false;
                AutoBreakEven                       = true;
                AutoBreakEvenTriggerTicks           = 20;
                BreakEvenPlusTicks                  = 5;
                UseAtrTrailStop                     = true;
                MinStopClearanceTicks               = 4;

                UseOrderFlowConfluence              = false;
                OrderFlowLookbackBars               = 200;
                OrderFlowMinR2                       = 0.30;
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(AtrPeriod);
                // Read the UT Bot indicator's exported Signal series directly.
                // Internal visual alerts are disabled to avoid duplicate labels/sounds.
                utBotSignalIndicator = UTBotAlertsNT8(KeyValue, AtrPeriod,
                    UseHeikinAshiSource, false, false, false);

                utSource       = new Series<double>(this, MaximumBarsLookBack.Infinite);
                utTrailingStop = new Series<double>(this, MaximumBarsLookBack.Infinite);

                priceOpen  = new Series<double>(this, MaximumBarsLookBack.Infinite);
                priceHigh  = new Series<double>(this, MaximumBarsLookBack.Infinite);
                priceLow   = new Series<double>(this, MaximumBarsLookBack.Infinite);
                priceClose = new Series<double>(this, MaximumBarsLookBack.Infinite);

                linRegOpen  = LinReg(priceOpen, LinearRegressionLength);
                linRegHigh  = LinReg(priceHigh, LinearRegressionLength);
                linRegLow   = LinReg(priceLow, LinearRegressionLength);
                linRegClose = LinReg(priceClose, LinearRegressionLength);

                signalSma = SMA(linRegClose, SignalLength);
                signalEma = EMA(linRegClose, SignalLength);
            }
            else if (State == State.Historical)
            {
                if (ShowControlPanel && ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(() => AddControlPanel());
            }
            else if (State == State.Terminated)
            {
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(() => RemoveControlPanel());
            }
        }

        protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
        {
            if (State != State.Realtime)
                return;

            if (UseOrderFlowConfluence && marketDataUpdate.MarketDataType == MarketDataType.Last)
                UpdateOrderFlowCvd(marketDataUpdate.Price, marketDataUpdate.Bid, marketDataUpdate.Ask, marketDataUpdate.Volume);

            ProcessPanelRequests();
            UpdateControlPanel();
        }

        // Quote-rule + tick-rule-fallback trade classification (Lee & Ready, 1991) -- identical
        // formula to SneakyOrderFlowDashboard_v1_0_0's ClassifyTrade. MUST be called with cfLock held.
        private string ClassifyTrade(double price, double bid, double ask)
        {
            if (ask > 0 && price >= ask) return "BUY";
            if (bid > 0 && price <= bid) return "SELL";
            if (!double.IsNaN(cfLastTradePrice))
            {
                if (price > cfLastTradePrice) return "BUY";
                if (price < cfLastTradePrice) return "SELL";
                if (cfLastTradeSide != null) return cfLastTradeSide;
            }
            return "BUY";
        }

        // Runs on the real-time tape thread (same thread OnMarketData always runs on) -- cfLock
        // guards cfCumulativeDelta since UpdateOrderFlowConfluenceBarLogic() below reads it from
        // the separate bar-processing thread.
        private void UpdateOrderFlowCvd(double price, double bid, double ask, long volume)
        {
            lock (cfLock)
            {
                string side = ClassifyTrade(price, bid, ask);
                cfLastTradePrice = price;
                cfLastTradeSide  = side;
                cfCumulativeDelta += (side == "BUY" ? volume : -volume);
            }
        }

        // Runs once per bar close (bar-processing thread). Snapshots the tape-thread-accumulated
        // cfCumulativeDelta, appends it to the rolling window, and fits an OLS regression of CVD
        // against sequential bar index -- same math as the dashboard's CVD Regression section.
        private void UpdateOrderFlowConfluenceBarLogic()
        {
            if (!UseOrderFlowConfluence)
                return;

            double cvdNow;
            lock (cfLock) { cvdNow = cfCumulativeDelta; }

            cfHCvd.Add(cvdNow);
            while (cfHCvd.Count > OrderFlowLookbackBars) cfHCvd.RemoveAt(0);

            int n = cfHCvd.Count;
            double slope = 0, r2 = 0;
            if (n >= 2)
            {
                double sx = 0, sy = 0;
                for (int i = 0; i < n; i++) { sx += i; sy += cfHCvd[i]; }
                double mx = sx / n, my = sy / n;
                double vx = 0, cov = 0;
                for (int i = 0; i < n; i++)
                {
                    double dx = i - mx, dy = cfHCvd[i] - my;
                    vx += dx * dx; cov += dx * dy;
                }
                slope = vx > 0 ? cov / vx : 0;
                double intercept = my - slope * mx;

                double ssTot = 0, ssRes = 0;
                for (int i = 0; i < n; i++)
                {
                    double resid = cfHCvd[i] - (intercept + slope * i);
                    ssRes += resid * resid;
                    double diffFromMean = cfHCvd[i] - my;
                    ssTot += diffFromMean * diffFromMean;
                }
                r2 = ssTot > 0 ? 1.0 - (ssRes / ssTot) : 0;
            }

            cfSlope = slope;
            cfR2 = r2;
        }

        // The actual confluence gate. Returns true (never blocks) unless the CVD regression has a
        // FULL window AND a confident fit (R² >= OrderFlowMinR2) AND that confident slope points the
        // opposite way from the requested trade direction. This is deliberate: a not-yet-full window
        // or a low-R² slope is noise/insufficient data, not real disagreement, and should never be
        // the reason a signal gets blocked -- including for users whose feed has no Level 2 depth,
        // since this gate never needs depth in the first place (only Level 1 trade prints).
        private bool OrderFlowAllows(int direction)
        {
            if (!UseOrderFlowConfluence) return true;
            if (cfHCvd.Count < OrderFlowLookbackBars) return true;
            if (cfR2 < OrderFlowMinR2) return true;
            int slopeDir = Math.Sign(cfSlope);
            if (slopeDir == 0) return true;
            return slopeDir == direction;
        }

        private string OrderFlowStatusText()
        {
            if (!UseOrderFlowConfluence) return "OFF";
            if (cfHCvd.Count < OrderFlowLookbackBars) return "WARMING " + cfHCvd.Count + "/" + OrderFlowLookbackBars;
            if (cfR2 < OrderFlowMinR2) return "NEUTRAL (R² " + cfR2.ToString("0.00") + ")";
            return (cfSlope > 0 ? "BULL" : cfSlope < 0 ? "BEAR" : "NEUTRAL") + " (R² " + cfR2.ToString("0.00") + ")";
        }

        protected override void OnBarUpdate()
        {
            ProcessPanelRequests();

            // Bar-update fallback: if an execution callback was delayed by the
            // playback/account adapter, complete the queued reversal here.
            if (Position.MarketPosition == MarketPosition.Flat
                && pendingReversal != MarketPosition.Flat)
            {
                MarketPosition next = pendingReversal;
                pendingReversal = MarketPosition.Flat;
                if (next == MarketPosition.Long)
                    ReverseOrEnterLong();
                else
                    ReverseOrEnterShort();
            }

            if (HideNativeCandles)
            {
                BarBrush = Brushes.Transparent;
                CandleOutlineBrush = Brushes.Transparent;
            }

            if (UseOrderFlowConfluence && Bars.IsFirstBarOfSession)
            {
                lock (cfLock) { cfCumulativeDelta = 0; }
                cfHCvd.Clear();
            }
            if (Bars.IsFirstBarOfSession)
            {
                clearanceBlockedCount = 0;
                lastClearanceBlockText = "";
                currentSessionBegin = Time[0];
                RefreshTradeMarkerVisibility();
            }
            UpdateOrderFlowConfluenceBarLogic();

            int minimumBars = Math.Max(AtrPeriod, LinearRegressionLength + SignalLength - 1);
            if (CurrentBar < minimumBars)
            {
                UpdateSourceSeries();
                UpdateUtTrailingStop();
                return;
            }

            UpdateSourceSeries();
            UpdateUtTrailingStop();

            // UTBotAlertsNT8 exports Buy as +1 and Sell as -1. Reading that
            // series guarantees the strategy uses the same alert calculation.
            if (utBotSignalIndicator != null)
                utBotSignalIndicator.Update();
            int utAlertSignal = utBotSignalIndicator == null
                ? 0
                : (int)Math.Round(utBotSignalIndicator.Signal[0]);

            // Safety fallback uses the same internal crossover calculation.
            if (utAlertSignal == 0)
            {
                if (utSource[0] > utTrailingStop[0] && utSource[1] <= utTrailingStop[1])
                    utAlertSignal = 1;
                else if (utSource[0] < utTrailingStop[0] && utSource[1] >= utTrailingStop[1])
                    utAlertSignal = -1;
            }

            // Keep the latest UT direction armed until LinReg confirms it.
            // A new opposite UT alert replaces the pending direction.
            if (utAlertSignal != 0)
                pendingUtDirection = utAlertSignal;

            double linRegCandleClose = linRegClose[0];
            double signalLine = UseSmaSignal ? signalSma[0] : signalEma[0];
            currentLinRegSignalLine = Instrument.MasterInstrument.RoundToTickSize(signalLine);

            bool linRegLongOk  = pendingUtDirection == 1 && linRegCandleClose > signalLine;
            bool linRegShortOk = pendingUtDirection == -1 && linRegCandleClose < signalLine;

            // Optional extra gate (off by default): requires the RAW last price, not just the
            // smoothed LinReg candle close above, to also be on the correct side of the line.
            bool linePriceLongOk  = !UseLinePriceFilter || Close[0] > currentLinRegSignalLine;
            bool linePriceShortOk = !UseLinePriceFilter || Close[0] < currentLinRegSignalLine;

            bool validLong  = linRegLongOk && linePriceLongOk && OrderFlowAllows(1);
            bool validShort = linRegShortOk && linePriceShortOk && OrderFlowAllows(-1);

            lastSignal = validLong ? 1 : validShort ? -1 : 0;
            lastFilterState = pendingUtDirection == 1
                ? (validLong ? "LONG PASS" : !linRegLongOk ? "UT BUY WAIT" : !linePriceLongOk ? "LINE BLOCK" : "OF BLOCK")
                : pendingUtDirection == -1
                    ? (validShort ? "SHORT PASS" : !linRegShortOk ? "UT SELL WAIT" : !linePriceShortOk ? "LINE BLOCK" : "OF BLOCK")
                    : "WAIT";

            if (EnableAutomation && EnableLongs && validLong && Position.MarketPosition != MarketPosition.Long)
            {
                pendingUtDirection = 0;
                RequestLongEntry();
            }
            else if (EnableAutomation && EnableShorts && validShort && Position.MarketPosition != MarketPosition.Short)
            {
                pendingUtDirection = 0;
                RequestShortEntry();
            }
            else if ((validLong && Position.MarketPosition == MarketPosition.Long)
                || (validShort && Position.MarketPosition == MarketPosition.Short))
            {
                pendingUtDirection = 0;
            }

            ProcessPanelRequests();
            UpdateControlPanel();
        }

        private void UpdateSourceSeries()
        {
            priceOpen[0]  = Open[0];
            priceHigh[0]  = High[0];
            priceLow[0]   = Low[0];
            priceClose[0] = Close[0];

            // Pine's Heikin Ashi close is (open + high + low + close) / 4.
            // Its ATR remains calculated from the regular chart bars.
            utSource[0] = UseHeikinAshiSource
                ? (Open[0] + High[0] + Low[0] + Close[0]) / 4.0
                : Close[0];
        }

        private void UpdateUtTrailingStop()
        {
            double loss = KeyValue * atr[0];
            double previousStop = CurrentBar > 0 ? utTrailingStop[1] : 0.0;
            double previousSource = CurrentBar > 0 ? utSource[1] : utSource[0];

            if (utSource[0] > previousStop && previousSource > previousStop)
                utTrailingStop[0] = Math.Max(previousStop, utSource[0] - loss);
            else if (utSource[0] < previousStop && previousSource < previousStop)
                utTrailingStop[0] = Math.Min(previousStop, utSource[0] + loss);
            else
                utTrailingStop[0] = utSource[0] > previousStop
                    ? utSource[0] - loss
                    : utSource[0] + loss;
        }

        // 2026-09-10 FIX: added the same MinStopClearanceTicks safety margin used by
        // MoveStopToBreakEven/the LinReg trailing stop/ReadMovableStopLine (see their own comments
        // for the original incident) -- this entry path had NO clearance at all, just a bare
        // "stop is on the correct side of a snapshotted market price" check. A sell-stop (long
        // entry) or buy-stop (short entry) placed with only a single tick (or zero) of room can be
        // outrun between this check and the moment NinjaTrader/the sim engine actually processes the
        // new-position stop order, at which point it's REJECTED OUTRIGHT as structurally invalid --
        // not a "modify," an initial placement -- ("Buy stop or buy stop limit orders can't be
        // placed below the market" / the sell-stop equivalent above it). Under this strategy's
        // RealtimeErrorHandling (StopCancelClose) that rejection flattens the position and
        // TERMINATES THE WHOLE STRATEGY, exactly like the earlier break-even/trailing incidents.
        // Skipping the entry this bar when clearance isn't there does mean the armed UT+LinReg
        // signal is dropped rather than retried (pendingUtDirection was already zeroed by the
        // caller before invoking this) -- same trade-off already accepted for the pre-existing
        // "invalid at entry time" abort case this replaces, just with a wider, safer margin.
        // 2026-09-18: added UseFixedStopTicks. When on, the initial stop is a fixed tick distance
        // from the entry price (same style as ProfitTargetTicks) instead of the LinReg signal line.
        // The wrong-side-of-market check below only applies to the LinReg-line stop -- a fixed-tick
        // stop measured off the live market price is always on the correct side by construction, so
        // that branch is skipped entirely when this mode is on.
        private void ReverseOrEnterLong()
        {
            double marketPrice = State == State.Realtime ? GetCurrentAsk() : Close[0];
            if (marketPrice <= 0) marketPrice = Close[0];

            double stopPrice;
            if (UseFixedStopTicks)
            {
                stopPrice = Instrument.MasterInstrument.RoundToTickSize(marketPrice - StopLossTicks * TickSize);
            }
            else
            {
                if (currentLinRegSignalLine <= 0 || currentLinRegSignalLine >= marketPrice)
                    return;   // stop is on the wrong side entirely -- not a clearance issue, nothing to log
                stopPrice = currentLinRegSignalLine;
            }

            double clearance = MinStopClearanceTicks * TickSize;
            if (stopPrice >= marketPrice - clearance)
            {
                // Right side, just not enough room -- see the 2026-09-10 fix comment above this
                // method for why this exists. Counted separately from a flat "no signal" so a
                // missed entry can be told apart from "the strategy simply had nothing to trade."
                clearanceBlockedCount++;
                lastClearanceBlockText = "LONG @ " + marketPrice.ToString("0.00") + " stop " + stopPrice.ToString("0.00");
                return;
            }

            manualStopPrice = stopPrice;
            manualTargetPrice = 0;
            SetStopLoss("Long", CalculationMode.Price, manualStopPrice, false);
            if (UseProfitTarget)
                SetProfitTarget("Long", CalculationMode.Ticks, ProfitTargetTicks);
            EnterLong(Quantity, "Long");
            // Entry marker is now drawn from OnExecutionUpdate/LogTradeMarker at the real fill
            // price, the same path exits and bracket fills go through -- see 2026-09-18 note above.
        }

        private void ReverseOrEnterShort()
        {
            double marketPrice = State == State.Realtime ? GetCurrentBid() : Close[0];
            if (marketPrice <= 0) marketPrice = Close[0];

            double stopPrice;
            if (UseFixedStopTicks)
            {
                stopPrice = Instrument.MasterInstrument.RoundToTickSize(marketPrice + StopLossTicks * TickSize);
            }
            else
            {
                if (currentLinRegSignalLine <= marketPrice)
                    return;   // stop is on the wrong side entirely -- not a clearance issue, nothing to log
                stopPrice = currentLinRegSignalLine;
            }

            double clearance = MinStopClearanceTicks * TickSize;
            if (stopPrice <= marketPrice + clearance)
            {
                clearanceBlockedCount++;
                lastClearanceBlockText = "SHORT @ " + marketPrice.ToString("0.00") + " stop " + stopPrice.ToString("0.00");
                return;
            }

            manualStopPrice = stopPrice;
            manualTargetPrice = 0;
            SetStopLoss("Short", CalculationMode.Price, manualStopPrice, false);
            if (UseProfitTarget)
                SetProfitTarget("Short", CalculationMode.Ticks, ProfitTargetTicks);
            EnterShort(Quantity, "Short");
            // Entry marker is now drawn from OnExecutionUpdate/LogTradeMarker at the real fill
            // price, the same path exits and bracket fills go through -- see 2026-09-18 note above.
        }

        private void RequestLongEntry()
        {
            if (Position.MarketPosition == MarketPosition.Short)
            {
                pendingReversal = MarketPosition.Long;
                ExitShort("ReverseToLong", "Short");
            }
            else if (Position.MarketPosition == MarketPosition.Flat)
            {
                pendingReversal = MarketPosition.Flat;
                ReverseOrEnterLong();
            }
        }

        private void RequestShortEntry()
        {
            if (Position.MarketPosition == MarketPosition.Long)
            {
                pendingReversal = MarketPosition.Short;
                ExitLong("ReverseToShort", "Long");
            }
            else if (Position.MarketPosition == MarketPosition.Flat)
            {
                pendingReversal = MarketPosition.Flat;
                ReverseOrEnterShort();
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
            int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null || execution.Order.OrderState != OrderState.Filled)
                return;

            LogTradeMarker(execution, price, time);

            if ((execution.Order.Name == "Long" || execution.Order.Name == "Short")
                && marketPosition != MarketPosition.Flat)
            {
                SyncMovableStopLine(manualStopPrice);
            }

            // The execution callback's marketPosition argument is not the
            // strategy's resulting position. Key the re-entry from the exact
            // filled reversal-exit order instead.
            if (execution.Order.Name == "ReverseToLong"
                && pendingReversal == MarketPosition.Long)
            {
                pendingReversal = MarketPosition.Flat;
                ReverseOrEnterLong();
            }
            else if (execution.Order.Name == "ReverseToShort"
                && pendingReversal == MarketPosition.Short)
            {
                pendingReversal = MarketPosition.Flat;
                ReverseOrEnterShort();
            }
        }

        private string ActiveEntrySignal()
        {
            return Position.MarketPosition == MarketPosition.Long ? "Long" : "Short";
        }

        // Logs every filled order (entries, reversal exits, manual closes, and bracket Profit
        // Target / Stop Loss fills all come through here with NinjaTrader's own order names) and
        // draws or hides it immediately per the current ShowHistoricalTradeMarkers setting.
        private void LogTradeMarker(Execution execution, double price, DateTime time)
        {
            if (!ShowTradeMarkers)
                return;

            bool isBuy = execution.Order.OrderAction == OrderAction.Buy
                || execution.Order.OrderAction == OrderAction.BuyToCover;

            // Color follows which SIDE the fill belongs to, not the raw buy/sell direction of this
            // particular order: Buy (long entry) and Sell (long exit -- profit target/stop loss on a
            // long) are both the long trade, so both stay green; SellShort (short entry) and
            // BuyToCover (short exit) are both the short trade, so both stay red. Without this, a
            // short's own profit-target/stop-loss fill (a BuyToCover) would render green just because
            // buying closed it, which reads as if it were a separate long trade.
            bool isLongSide = execution.Order.OrderAction == OrderAction.Buy
                || execution.Order.OrderAction == OrderAction.Sell;

            TradeMarkerRecord rec = new TradeMarkerRecord
            {
                barIndex = CurrentBar,
                price = price,
                orderName = execution.Order.Name,
                isBuy = isBuy,
                isLongSide = isLongSide,
                time = time,
                tag = "TradeMarker" + (tradeMarkerSeq++) + "_" + CurrentBar,
                visible = false
            };
            tradeMarkerLog.Add(rec);
            ApplyMarkerVisibility(rec);
        }

        // Draws (or removes) one marker's Draw.Text object to match whether it should currently be
        // visible -- current-session fills always qualify; older ones only when
        // ShowHistoricalTradeMarkers is on. Safe to call repeatedly; it's a no-op once the drawn
        // state already matches.
        private void ApplyMarkerVisibility(TradeMarkerRecord rec)
        {
            bool shouldShow = ShowTradeMarkers && (ShowHistoricalTradeMarkers || rec.time >= currentSessionBegin);

            if (shouldShow && !rec.visible)
            {
                // 2026-09-18: switched to the boxed/pill Draw.Text overload (background + outline) --
                // plain floating text was hard to pick out against candles/other overlays. This is the
                // same visual language NinjaTrader's own native order-line pills use (e.g. the "Buy
                // STP"/"Buy LMT" tags next to working orders), just applied to fill markers instead.
                int barsAgo = Math.Max(0, CurrentBar - rec.barIndex);
                // 2026-09-18: stack markers that land on the same bar and the same side (a reversal's
                // exit fill and its immediate re-entry fill are almost always inches apart in price and
                // on the same bar) instead of letting their boxes render on top of each other -- each
                // already-visible marker sharing this bar+side pushes the next one further out.
                int stackIndex = tradeMarkerLog.Count(other => other != rec && other.visible
                    && other.barIndex == rec.barIndex && other.isBuy == rec.isBuy);
                double offset = (8 + stackIndex * 7) * TickSize;
                double y = rec.isBuy ? rec.price - offset : rec.price + offset;
                Brush areaBrush = rec.isLongSide ? Brushes.DarkGreen : Brushes.DarkRed;
                Brush outlineBrush = rec.isLongSide ? Brushes.LimeGreen : Brushes.Red;
                NinjaTrader.Gui.Tools.SimpleFont font = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 11) { Bold = true };
                Draw.Text(this, rec.tag, false, rec.orderName + " " + rec.price.ToString("0.00"),
                    barsAgo, y, 0, Brushes.White, font, TextAlignment.Center, outlineBrush, areaBrush, 90);
                rec.visible = true;
            }
            else if (!shouldShow && rec.visible)
            {
                RemoveDrawObject(rec.tag);
                rec.visible = false;
            }
        }

        // Re-evaluates every logged fill against the current ShowHistoricalTradeMarkers setting and
        // session cutoff. Called when a new session begins (yesterday's fills just aged out of
        // "current") and when the panel button or property toggles the setting live.
        private void RefreshTradeMarkerVisibility()
        {
            foreach (TradeMarkerRecord rec in tradeMarkerLog)
                ApplyMarkerVisibility(rec);
        }

        private void ProcessPanelRequests()
        {
            if (closeRequested)
            {
                closeRequested = false;
                ClosePosition();
            }

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                breakEvenRequested = false;
                breakEvenPlusRequested = false;
                riskUpdateRequested = false;
                manualStopPrice = 0;
                manualTargetPrice = 0;
                autoBreakEvenTriggered = false;
                atrTrailActivated = false;
                trackedPosition = MarketPosition.Flat;
                RemoveMovableStopLine();
                return;
            }

            if (trackedPosition != Position.MarketPosition)
            {
                trackedPosition = Position.MarketPosition;
                autoBreakEvenTriggered = false;
                atrTrailActivated = false;
            }

            string entrySignal = ActiveEntrySignal();
            EnsureMovableStopLine();
            ReadMovableStopLine(entrySignal);

            double markPrice = State == State.Realtime
                ? (Position.MarketPosition == MarketPosition.Long ? GetCurrentBid() : GetCurrentAsk())
                : Close[0];
            if (markPrice <= 0)
                markPrice = Close[0];

            if (AutoBreakEven && !autoBreakEvenTriggered && AutoBreakEvenTriggerTicks > 0)
            {
                double profitTicks = Position.MarketPosition == MarketPosition.Long
                    ? (markPrice - Position.AveragePrice) / TickSize
                    : (Position.AveragePrice - markPrice) / TickSize;
                if (profitTicks >= AutoBreakEvenTriggerTicks)
                {
                    // Only latch "triggered" once the stop actually moved -- see MoveStopToBreakEven's
                    // own comment for why this can legitimately be skipped and must retry next tick.
                    if (MoveStopToBreakEven(entrySignal, BreakEvenPlusTicks, markPrice))
                        autoBreakEvenTriggered = true;
                }
            }

            if (breakEvenRequested || breakEvenPlusRequested)
            {
                int plusTicks = breakEvenPlusRequested ? BreakEvenPlusTicks : 0;
                breakEvenRequested = false;
                breakEvenPlusRequested = false;
                if (MoveStopToBreakEven(entrySignal, plusTicks, markPrice))
                    autoBreakEvenTriggered = true;
            }

            // LinReg signal-line trailing stop. It begins with the entry and
            // follows the signal line on completed bars, but never loosens.
            //
            // 2026-09-09 FIX: added MinStopClearanceTicks as a safety margin on `valid`. Without it,
            // this only required the candidate be a single tick on the correct side of markPrice at
            // the instant this ran -- on a fast/violent bar (a big spike through several levels
            // within the same bar), price can move past that candidate between this check and the
            // moment NinjaTrader's own order-modify pipeline actually processes the request, so a
            // stop that WAS valid when submitted arrives as "Stop price can't be changed below/above
            // the market." Under RealtimeErrorHandling.StopCancelClose (this strategy's setting) that
            // single rejected modify flattens the position and TERMINATES THE WHOLE STRATEGY -- exactly
            // what happened (see the "Unable to change order" / BuyToCover StopMarket dialog this fix
            // was written for). A clearance buffer doesn't eliminate the race (an arbitrarily large gap
            // can still outrun any fixed buffer), but it removes the razor's-edge case that made it
            // trivial to trigger, without changing RealtimeErrorHandling itself -- that's a separate,
            // deliberate choice (auto-flatten-and-stop vs. keep running through order errors) left for
            // the user to make, not something to change silently as a side effect of this fix.
            // Fixed-tick stops are intentionally left alone here -- the LinReg trail is a different
            // stop-management style (follow the regression line) and would otherwise silently
            // replace the fixed distance the user asked for the moment a bar closes.
            if (UseAtrTrailStop && !UseFixedStopTicks && currentLinRegSignalLine > 0)
            {
                atrTrailActivated = true;
                double candidate = currentLinRegSignalLine;
                double clearance = MinStopClearanceTicks * TickSize;
                bool valid = Position.MarketPosition == MarketPosition.Long
                    ? candidate < markPrice - clearance
                    : candidate > markPrice + clearance;
                bool tighter = Position.MarketPosition == MarketPosition.Long
                    ? manualStopPrice <= 0 || candidate > manualStopPrice + TickSize * 0.5
                    : manualStopPrice <= 0 || candidate < manualStopPrice - TickSize * 0.5;

                if (valid && tighter)
                {
                    manualStopPrice = candidate;
                    SetStopLoss(entrySignal, CalculationMode.Price, manualStopPrice, false);
                    SyncMovableStopLine(manualStopPrice);
                }
            }

            if (riskUpdateRequested)
            {
                riskUpdateRequested = false;
                if (manualStopPrice > 0)
                {
                    SetStopLoss(entrySignal, CalculationMode.Price, manualStopPrice, false);
                    SyncMovableStopLine(manualStopPrice);
                }
                if (UseProfitTarget && manualTargetPrice > 0)
                    SetProfitTarget(entrySignal, CalculationMode.Price, manualTargetPrice);
            }
        }

        // Returns whether the stop was actually moved. Previously this submitted unconditionally --
        // with NO check against the live market at all -- so on a fast reversal the just-computed
        // breakeven price could already be on the wrong side of the current market by the time this
        // ran, and NinjaTrader rejects that modify outright ("Stop price can't be changed below/above
        // the market"). Under RealtimeErrorHandling.StopCancelClose (this strategy's setting) a single
        // rejected modify flattens the position and TERMINATES THE WHOLE STRATEGY -- see the 2026-09-09
        // fix note on the LinReg trailing-stop block above for the incident this was written for.
        // Returning false (instead of always latching "triggered") lets the caller retry next tick
        // once the market has settled, rather than silently giving up on break-even for the trade.
        private bool MoveStopToBreakEven(string entrySignal, int plusTicks, double markPrice)
        {
            double candidate = Position.AveragePrice
                + (Position.MarketPosition == MarketPosition.Long ? 1 : -1) * plusTicks * TickSize;
            candidate = Instrument.MasterInstrument.RoundToTickSize(candidate);

            double clearance = MinStopClearanceTicks * TickSize;
            bool valid = Position.MarketPosition == MarketPosition.Long
                ? candidate < markPrice - clearance
                : candidate > markPrice + clearance;
            if (!valid)
                return false;

            manualStopPrice = candidate;
            SetStopLoss(entrySignal, CalculationMode.Price, manualStopPrice, false);
            SyncMovableStopLine(manualStopPrice);
            return true;
        }

        private void ClosePosition()
        {
            pendingReversal = MarketPosition.Flat;
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong("ClosePosition", "Long");
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort("ClosePosition", "Short");
        }

        private void SubmitManualMarketOrder(MarketPosition direction)
        {
            if (State != State.Realtime || CurrentBar < 1)
                return;

            if (direction == MarketPosition.Long && Position.MarketPosition != MarketPosition.Long)
                RequestLongEntry();
            else if (direction == MarketPosition.Short && Position.MarketPosition != MarketPosition.Short)
                RequestShortEntry();
        }

        private void EnsureMovableStopLine()
        {
            if (Position.MarketPosition == MarketPosition.Flat || manualStopPrice <= 0 || movableStopLine != null)
                return;
            movableStopLine = Draw.HorizontalLine(this, MovableStopLineTag, manualStopPrice, Brushes.Gold);
            if (movableStopLine != null) movableStopLine.IsLocked = false;
        }

        private void ReadMovableStopLine(string entrySignal)
        {
            if (movableStopLine == null || Position.MarketPosition == MarketPosition.Flat)
                return;
            double dragged = Instrument.MasterInstrument.RoundToTickSize(movableStopLine.StartAnchor.Price);
            if (dragged <= 0 || Math.Abs(dragged - manualStopPrice) < TickSize * 0.5)
                return;
            double market = Position.MarketPosition == MarketPosition.Long ? GetCurrentBid() : GetCurrentAsk();
            if (market <= 0) market = Close[0];
            // Same MinStopClearanceTicks safety margin as the auto stop-management paths above --
            // a manual drag is lower-risk (a deliberate one-off action, not a hot per-tick loop) but
            // the underlying race (market moves between this check and NinjaTrader actually
            // processing the modify) is identical, so it gets the same guard for consistency.
            double clearance = MinStopClearanceTicks * TickSize;
            bool valid = Position.MarketPosition == MarketPosition.Long ? dragged < market - clearance : dragged > market + clearance;
            if (!valid) { SyncMovableStopLine(manualStopPrice); return; }
            manualStopPrice = dragged;
            SetStopLoss(entrySignal, CalculationMode.Price, manualStopPrice, false);
        }

        private void SyncMovableStopLine(double price)
        {
            if (price <= 0 || Position.MarketPosition == MarketPosition.Flat) return;
            movableStopLine = Draw.HorizontalLine(this, MovableStopLineTag, price, Brushes.Gold);
            if (movableStopLine != null) movableStopLine.IsLocked = false;
        }

        private void RemoveMovableStopLine()
        {
            if (movableStopLine == null) return;
            RemoveDrawObject(MovableStopLineTag);
            movableStopLine = null;
        }

        private void AddControlPanel()
        {
            if (controlPanel != null || ChartPanel == null) return;
            controlPanel = new Grid
            {
                Width = Math.Max(360, PanelWidth),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, PanelTopOffset, PanelRightOffset, 0),
                Background = new SolidColorBrush(Color.FromArgb(235, 5, 20, 18))
            };
            for (int i = 0; i < 12; i++) controlPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < 12; i++) controlPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            panelTitle = MakeText("LINREG + UT AUTO   ↔ DRAG", 14, FontWeights.Bold, Brushes.LimeGreen);
            panelTitle.Cursor = Cursors.SizeAll;
            panelTitle.ToolTip = "Drag to move panel";
            PlaceElement(panelTitle, 0, 0, 12);
            panelInfo = MakeText("Waiting for realtime data...", 12, FontWeights.Normal, Brushes.White);
            panelInfo.TextWrapping = TextWrapping.Wrap;
            PlaceElement(panelInfo, 1, 0, 12);
            panelSignal = MakeText("Signal —   |   LinReg —", 12, FontWeights.Bold, Brushes.White);
            PlaceElement(panelSignal, 2, 0, 12);

            autoButton = MakeButton("AUTO: ON"); longButton = MakeButton("LONG: ON"); shortButton = MakeButton("SHORT: ON");
            PlaceElement(autoButton, 3, 0, 4); PlaceElement(longButton, 3, 4, 4); PlaceElement(shortButton, 3, 8, 4);
            manualLongButton = MakeButton("MANUAL LONG MKT"); manualShortButton = MakeButton("MANUAL SHORT MKT");
            PlaceElement(manualLongButton, 4, 0, 6); PlaceElement(manualShortButton, 4, 6, 6);
            autoBreakEvenButton = MakeButton("AUTO BE: ARMED"); breakEvenButton = MakeButton("MOVE SL TO BE"); breakEvenPlusButton = MakeButton("BE+ 5T");
            PlaceElement(autoBreakEvenButton, 5, 0, 4); PlaceElement(breakEvenButton, 5, 4, 4); PlaceElement(breakEvenPlusButton, 5, 8, 4);
            atrTrailButton = MakeButton("LINREG TRAIL: ON"); PlaceElement(atrTrailButton, 6, 0, 12);
            slDownButton = MakeButton("SL ▼"); slUpButton = MakeButton("SL ▲"); tpDownButton = MakeButton("TP ▼"); tpUpButton = MakeButton("TP ▲");
            PlaceElement(slDownButton, 7, 0, 3); PlaceElement(slUpButton, 7, 3, 3); PlaceElement(tpDownButton, 7, 6, 3); PlaceElement(tpUpButton, 7, 9, 3);
            histTradesButton = MakeButton("HIST TRADES: OFF"); PlaceElement(histTradesButton, 8, 0, 12);
            closeButton = MakeButton("CLOSE POSITION"); PlaceElement(closeButton, 9, 0, 12);

            autoButton.Click += AutoButton_Click; longButton.Click += LongButton_Click; shortButton.Click += ShortButton_Click;
            manualLongButton.Click += ManualLongButton_Click; manualShortButton.Click += ManualShortButton_Click;
            autoBreakEvenButton.Click += AutoBreakEvenButton_Click; breakEvenButton.Click += BreakEvenButton_Click; breakEvenPlusButton.Click += BreakEvenPlusButton_Click;
            atrTrailButton.Click += AtrTrailButton_Click; slDownButton.Click += SlDownButton_Click; slUpButton.Click += SlUpButton_Click;
            tpDownButton.Click += TpDownButton_Click; tpUpButton.Click += TpUpButton_Click; closeButton.Click += CloseButton_Click;
            histTradesButton.Click += HistTradesButton_Click;
            panelTitle.MouseLeftButtonDown += PanelTitle_MouseLeftButtonDown; panelTitle.MouseMove += PanelTitle_MouseMove; panelTitle.MouseLeftButtonUp += PanelTitle_MouseLeftButtonUp;

            panelDragTransform = new TranslateTransform(); controlPanel.RenderTransform = panelDragTransform;
            chartHostGrid = FindChartHostGrid();
            if (chartHostGrid == null) { controlPanel = null; return; }
            chartHostGrid.Children.Add(controlPanel);
            UpdateButtonStates();
        }

        private void RemoveControlPanel()
        {
            if (controlPanel == null) return;
            autoButton.Click -= AutoButton_Click; longButton.Click -= LongButton_Click; shortButton.Click -= ShortButton_Click;
            manualLongButton.Click -= ManualLongButton_Click; manualShortButton.Click -= ManualShortButton_Click;
            autoBreakEvenButton.Click -= AutoBreakEvenButton_Click; breakEvenButton.Click -= BreakEvenButton_Click; breakEvenPlusButton.Click -= BreakEvenPlusButton_Click;
            atrTrailButton.Click -= AtrTrailButton_Click; slDownButton.Click -= SlDownButton_Click; slUpButton.Click -= SlUpButton_Click;
            tpDownButton.Click -= TpDownButton_Click; tpUpButton.Click -= TpUpButton_Click; closeButton.Click -= CloseButton_Click;
            histTradesButton.Click -= HistTradesButton_Click;
            panelTitle.MouseLeftButtonDown -= PanelTitle_MouseLeftButtonDown; panelTitle.MouseMove -= PanelTitle_MouseMove; panelTitle.MouseLeftButtonUp -= PanelTitle_MouseLeftButtonUp;
            if (chartHostGrid != null) chartHostGrid.Children.Remove(controlPanel);
            controlPanel = null; chartHostGrid = null; RemoveMovableStopLine();
        }

        private Grid FindChartHostGrid()
        {
            if (ChartControl == null) return null;
            Grid direct = ChartControl.Parent as Grid;
            if (direct != null) return direct;
            DependencyObject current = VisualTreeHelper.GetParent(ChartControl);
            while (current != null)
            {
                Grid grid = current as Grid;
                if (grid != null) return grid;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private TextBlock MakeText(string text, double size, FontWeight weight, Brush brush)
        {
            return new TextBlock { Text = text, FontSize = size, FontWeight = weight, Foreground = brush, Margin = new Thickness(7, 4, 7, 3) };
        }

        private Button MakeButton(string text)
        {
            return new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(3, 5, 3, 5), Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(18, 58, 51)), BorderBrush = Brushes.SeaGreen, BorderThickness = new Thickness(1),
                FontWeight = FontWeights.Bold, FontSize = 10, HorizontalContentAlignment = HorizontalAlignment.Center };
        }

        private void PlaceElement(UIElement element, int row, int column, int span)
        {
            Grid.SetRow(element, row); Grid.SetColumn(element, column); Grid.SetColumnSpan(element, span); controlPanel.Children.Add(element);
        }

        private void PanelTitle_MouseLeftButtonDown(object s, MouseButtonEventArgs e) { panelDragging = true; panelDragStart = e.GetPosition(chartHostGrid ?? controlPanel); panelTitle.CaptureMouse(); e.Handled = true; }
        private void PanelTitle_MouseMove(object s, MouseEventArgs e) { if (!panelDragging) return; Point p = e.GetPosition(chartHostGrid ?? controlPanel); Vector d = p - panelDragStart; panelDragTransform.X += d.X; panelDragTransform.Y += d.Y; panelDragStart = p; e.Handled = true; }
        private void PanelTitle_MouseLeftButtonUp(object s, MouseButtonEventArgs e) { if (!panelDragging) return; panelDragging = false; panelTitle.ReleaseMouseCapture(); e.Handled = true; }

        private void AutoButton_Click(object s, RoutedEventArgs e) { EnableAutomation = !EnableAutomation; UpdateButtonStates(); }
        private void LongButton_Click(object s, RoutedEventArgs e) { EnableLongs = !EnableLongs; UpdateButtonStates(); }
        private void ShortButton_Click(object s, RoutedEventArgs e) { EnableShorts = !EnableShorts; UpdateButtonStates(); }
        private void ManualLongButton_Click(object s, RoutedEventArgs e) { TriggerCustomEvent(o => SubmitManualMarketOrder(MarketPosition.Long), null); }
        private void ManualShortButton_Click(object s, RoutedEventArgs e) { TriggerCustomEvent(o => SubmitManualMarketOrder(MarketPosition.Short), null); }
        private void AutoBreakEvenButton_Click(object s, RoutedEventArgs e) { AutoBreakEven = !AutoBreakEven; if (!AutoBreakEven) autoBreakEvenTriggered = false; UpdateButtonStates(); }
        private void BreakEvenButton_Click(object s, RoutedEventArgs e) { breakEvenRequested = true; }
        private void BreakEvenPlusButton_Click(object s, RoutedEventArgs e) { breakEvenPlusRequested = true; }
        private void AtrTrailButton_Click(object s, RoutedEventArgs e) { UseAtrTrailStop = !UseAtrTrailStop; if (!UseAtrTrailStop) atrTrailActivated = false; UpdateButtonStates(); }
        private void CloseButton_Click(object s, RoutedEventArgs e) { closeRequested = true; }
        private void SlDownButton_Click(object s, RoutedEventArgs e) { AdjustStop(-RiskAdjustTicks); }
        private void SlUpButton_Click(object s, RoutedEventArgs e) { AdjustStop(RiskAdjustTicks); }
        private void TpDownButton_Click(object s, RoutedEventArgs e) { AdjustTarget(-RiskAdjustTicks); }
        private void TpUpButton_Click(object s, RoutedEventArgs e) { AdjustTarget(RiskAdjustTicks); }
        private void HistTradesButton_Click(object s, RoutedEventArgs e) { ShowHistoricalTradeMarkers = !ShowHistoricalTradeMarkers; RefreshTradeMarkerVisibility(); UpdateButtonStates(); }

        private void AdjustStop(int ticks)
        {
            if (Position.MarketPosition == MarketPosition.Flat || manualStopPrice <= 0) return;
            manualStopPrice += ticks * TickSize; riskUpdateRequested = true;
        }

        private void AdjustTarget(int ticks)
        {
            if (!UseProfitTarget || Position.MarketPosition == MarketPosition.Flat) return;
            if (manualTargetPrice <= 0) manualTargetPrice = Position.MarketPosition == MarketPosition.Long
                ? Position.AveragePrice + ProfitTargetTicks * TickSize : Position.AveragePrice - ProfitTargetTicks * TickSize;
            manualTargetPrice += ticks * TickSize; riskUpdateRequested = true;
        }

        private void UpdateButtonStates()
        {
            if (controlPanel == null) return;
            autoButton.Content = EnableAutomation ? "AUTO: ON" : "AUTO: OFF"; autoButton.Background = EnableAutomation ? Brushes.DarkGreen : Brushes.DimGray;
            longButton.Content = EnableLongs ? "LONG: ON" : "LONG: OFF"; longButton.Background = EnableLongs ? Brushes.DarkGreen : Brushes.DimGray;
            shortButton.Content = EnableShorts ? "SHORT: ON" : "SHORT: OFF"; shortButton.Background = EnableShorts ? Brushes.DarkRed : Brushes.DimGray;
            autoBreakEvenButton.Content = "AUTO BE: " + (!AutoBreakEven ? "OFF" : autoBreakEvenTriggered ? "TRIGGERED" : "ARMED");
            autoBreakEvenButton.Background = AutoBreakEven ? Brushes.DarkGreen : Brushes.DimGray;
            breakEvenPlusButton.Content = "BE+ " + BreakEvenPlusTicks + "T";
            atrTrailButton.Content = "LINREG TRAIL: " + (!UseAtrTrailStop ? "OFF" : atrTrailActivated ? "ACTIVE" : "ARMED");
            atrTrailButton.Background = UseAtrTrailStop ? Brushes.DarkGreen : Brushes.DimGray;
            manualLongButton.Background = Brushes.DarkGreen; manualShortButton.Background = Brushes.DarkRed; closeButton.Background = Brushes.DarkRed;
            histTradesButton.Content = "HIST TRADES: " + (ShowHistoricalTradeMarkers ? "ON" : "OFF");
            histTradesButton.Background = ShowHistoricalTradeMarkers ? Brushes.DarkGreen : Brushes.DimGray;
        }

        private void UpdateControlPanel()
        {
            if (!ShowControlPanel || ChartControl == null || controlPanel == null) return;
            NinjaTrader.Cbi.Position accountPosition = null;
            if (Account != null && Instrument != null)
                accountPosition = Account.Positions.FirstOrDefault(p => p.Instrument != null && p.Instrument.FullName == Instrument.FullName);
            MarketPosition mp = accountPosition == null ? MarketPosition.Flat : accountPosition.MarketPosition;
            int qty = accountPosition == null ? 0 : accountPosition.Quantity;
            double entry = accountPosition == null ? 0 : accountPosition.AveragePrice;
            double upnl = 0;
            if (accountPosition != null && mp != MarketPosition.Flat)
            {
                double mark = mp == MarketPosition.Long ? GetCurrentBid() : GetCurrentAsk();
                if (mark <= 0 && CurrentBar >= 0) mark = Close[0];
                upnl = accountPosition.GetUnrealizedProfitLoss(PerformanceUnit.Currency, mark);
            }
            double rpnl = Account != null && State == State.Realtime ? Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar) : SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            string info = "Instr   " + (Instrument == null ? "" : Instrument.MasterInstrument.Name) + "     Qty " + qty + "\n" +
                "Pos     " + mp + "\nEntry   " + (entry > 0 ? entry.ToString("0.00") : "-") + "\n" +
                "Stop    " + (manualStopPrice > 0 ? manualStopPrice.ToString("0.00") : "-") + "     SL " + (UseFixedStopTicks ? StopLossTicks + "t FIXED" : "LINREG") + "  [DRAG]\n" +
                "Target  " + (!UseProfitTarget ? "OFF" : manualTargetPrice > 0 ? manualTargetPrice.ToString("0.00") : ProfitTargetTicks + "t") + "\n" +
                "AutoBE  " + (!AutoBreakEven ? "OFF" : autoBreakEvenTriggered ? "TRIGGERED" : "ARMED @ +" + AutoBreakEvenTriggerTicks + "t") + "\n" +
                "LinReg  " + (!UseAtrTrailStop ? "TRAIL OFF" : atrTrailActivated ? "TRAIL ACTIVE" : "TRAIL ARMED") + "\n" +
                (clearanceBlockedCount > 0 ? "Blocked " + clearanceBlockedCount + "  (last: " + lastClearanceBlockText + ")\n" : "") +
                "UNREALIZED PNL  " + upnl.ToString("C2") + "     REALIZED PNL  " + rpnl.ToString("C2");
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                if (panelInfo != null) panelInfo.Text = info;
                if (panelSignal != null)
                {
                    panelSignal.Text = "Signal " + (lastSignal > 0 ? "LONG" : lastSignal < 0 ? "SHORT" : "WAIT") + "   |   LinReg " + lastFilterState
                        + (UseOrderFlowConfluence ? "   |   OF " + OrderFlowStatusText() : "");
                    panelSignal.Foreground = lastSignal > 0 ? Brushes.LimeGreen : lastSignal < 0 ? Brushes.OrangeRed : Brushes.White;
                }
                UpdateButtonStates();
            });
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Quantity", GroupName = "Order", Order = 0)]
        public int Quantity { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, double.MaxValue)]
        [Display(Name = "UT Key Value", GroupName = "UT Bot", Order = 1)]
        public double KeyValue { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "UT ATR Period", GroupName = "UT Bot", Order = 2)]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UT Heikin Ashi Source", GroupName = "UT Bot", Order = 3)]
        public bool UseHeikinAshiSource { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Linear Regression Length", GroupName = "LinReg", Order = 4)]
        public int LinearRegressionLength { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Signal Smoothing", GroupName = "LinReg", Order = 5)]
        public int SignalLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Simple MA Signal Line", GroupName = "LinReg", Order = 6)]
        public bool UseSmaSignal { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Line As Trend Filter", Description = "2026-09-16 -- optional, off by default. "
            + "When on, adds ONE more requirement before any long/short is allowed: the actual last traded "
            + "price (Close[0]), not the smoothed LinReg candle close, must also be on the correct side of "
            + "the LinReg signal line -- above it for longs, below it for shorts. This is an ADDITIONAL gate "
            + "layered on top of the existing UT Bot + LinReg confirmation this strategy already requires "
            + "(see linRegLongOk/linRegShortOk) -- it does not replace that logic, and a signal can still be "
            + "blocked by UT/LinReg or Order Flow Confluence even when this passes. Panel's LinReg status "
            + "shows \"LINE BLOCK\" when this is the reason a pending signal isn't firing.",
            GroupName = "LinReg", Order = 8)]
        public bool UseLinePriceFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Trade Markers", GroupName = "Visual", Order = 7)]
        public bool ShowTradeMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Historical Trade Markers", Description = "2026-09-18 -- off by default. "
            + "When off, trade markers (drawn the same way NinjaTrader's own native fill labels look -- "
            + "order name + fill price, colored by buy/sell) only show for fills from the CURRENT "
            + "session; older fills stay logged but hidden. Turn on to show the full history again -- "
            + "nothing is lost, this only controls what's currently drawn. Also toggleable live from "
            + "the panel's \"HIST TRADES\" button without recompiling. Independent of NinjaTrader's own "
            + "native \"Plot Executions\" chart setting (Data Series -> Trades, or Ctrl+E), which this "
            + "does not affect -- turn that off separately if you don't want NinjaTrader's own markers "
            + "layered underneath these.", GroupName = "Visual", Order = 8)]
        public bool ShowHistoricalTradeMarkers { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hide Native Candles", Description = "Makes the chart's NinjaTrader candles transparent so the LinReg candles can be viewed alone.", GroupName = "Visual", Order = 9)]
        public bool HideNativeCandles { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Automation", GroupName = "Panel / Trading", Order = 10)]
        public bool EnableAutomation { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Longs", GroupName = "Panel / Trading", Order = 11)]
        public bool EnableLongs { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Shorts", GroupName = "Panel / Trading", Order = 12)]
        public bool EnableShorts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Control Panel", GroupName = "Panel / Trading", Order = 13)]
        public bool ShowControlPanel { get; set; }

        [NinjaScriptProperty]
        [Range(220, 500)]
        [Display(Name = "Panel Width", GroupName = "Panel / Trading", Order = 14)]
        public int PanelWidth { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Panel Right Offset", GroupName = "Panel / Trading", Order = 15)]
        public int PanelRightOffset { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Panel Top Offset", GroupName = "Panel / Trading", Order = 16)]
        public int PanelTopOffset { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Fixed Stop Ticks", Description = "2026-09-18 -- optional, off by default. "
            + "When on, a new entry's stop loss is placed a fixed number of ticks (Stop Loss Ticks, "
            + "below) from the entry price, the same way Profit Target Ticks works, instead of using "
            + "the current LinReg signal line price. While a position is open, \"Use LinReg Signal-Line "
            + "Trail\" has no effect when this is on -- the fixed stop stays put (aside from Auto Break "
            + "Even / manual SL▼▲ panel adjustments) rather than being replaced by the trailing LinReg "
            + "line.", GroupName = "Risk Management", Order = 18)]
        public bool UseFixedStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Stop Loss Ticks", Description = "Fixed stop-loss distance in ticks from the "
            + "entry price. Only used when \"Use Fixed Stop Ticks\" is on.", GroupName = "Risk Management", Order = 19)]
        public int StopLossTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Profit Target", GroupName = "Risk Management", Order = 20)]
        public bool UseProfitTarget { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10000)]
        [Display(Name = "Profit Target Ticks", GroupName = "Risk Management", Order = 21)]
        public int ProfitTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "SL/TP Adjust Ticks", GroupName = "Risk Management", Order = 22)]
        public int RiskAdjustTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Auto Break Even", GroupName = "Risk Management", Order = 23)]
        public bool AutoBreakEven { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10000)]
        [Display(Name = "Auto BE Trigger Ticks", GroupName = "Risk Management", Order = 24)]
        public int AutoBreakEvenTriggerTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Break Even Plus Ticks", GroupName = "Risk Management", Order = 25)]
        public int BreakEvenPlusTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use LinReg Signal-Line Trail", GroupName = "Risk Management", Order = 26)]
        public bool UseAtrTrailStop { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Min Stop Clearance Ticks",
            Description = "Safety margin: a stop-loss modify (auto break-even, LinReg trail, or dragging "
                + "the stop line) is only submitted when the new price clears the live market by at least "
                + "this many ticks. Without it, a fast/violent bar can move price past a just-computed stop "
                + "between this check and NinjaTrader actually processing the modify, which gets rejected "
                + "as 'Stop price can't be changed below/above the market' -- and under this strategy's "
                + "RealtimeErrorHandling (StopCancelClose), that single rejection flattens the position and "
                + "terminates the whole strategy. This buffer reduces how often that race can happen; it "
                + "cannot eliminate it outright, since an arbitrarily large gap can still outrun any fixed "
                + "margin.", GroupName = "Risk Management", Order = 27)]
        public int MinStopClearanceTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Order Flow Confluence",
            Description = "When on, a UT Bot + LinReg signal must also NOT be confidently contradicted by CVD "
                + "(cumulative volume delta, from Level 1 trade prints and quotes -- no Level 2 market depth "
                + "required) before it's taken. A CVD read that's still warming up, or whose regression fit "
                + "(R²) hasn't cleared the threshold below, is treated as noise/insufficient data and never "
                + "blocks a trade -- only a confident CVD slope pointing the opposite way blocks it. Off by "
                + "default: this changes real trade timing, so it's opt-in.", GroupName = "Order Flow Confluence", Order = 30)]
        public bool UseOrderFlowConfluence { get; set; }

        [NinjaScriptProperty]
        [Range(20, int.MaxValue)]
        [Display(Name = "CVD Lookback (bars)", Description = "Rolling window (in bars) the CVD regression fits over.", GroupName = "Order Flow Confluence", Order = 31)]
        public int OrderFlowLookbackBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "Min R² (fit-quality gate)",
            Description = "The CVD regression's R² must clear this before its direction can block a trade -- "
                + "below it, the slope is noise, not a real disagreement.", GroupName = "Order Flow Confluence", Order = 32)]
        public double OrderFlowMinR2 { get; set; }
        #endregion
    }
}
