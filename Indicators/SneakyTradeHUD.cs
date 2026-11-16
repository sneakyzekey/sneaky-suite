// ═════════════════════════════════════════════════════════════════════════════
//  SneakyTradeHUD — universal on-chart trade HUD + order management (NT8 indicator)
//
//  Works on ANY chart, with ANY strategy or manual trading. It does not read the
//  strategy; it reads the ACCOUNT's position + working orders for the chart's
//  instrument, so it behaves the same for Chart Trader, ATM, managed or unmanaged
//  strategies, or another add-on's orders.
//
//  SHOWS      position, open P&L ($ / points / ticks / R-multiple), session P&L for the
//             account (realized + open), the live stop & target prices found.
//  BUTTONS    BE            stop(s) -> entry ± BE offset (only once price is past it)
//             Lock 50%      stop(s) -> halfway between entry and price
//             SL up / down  stop(s) one nudge (default 1 pt) UP / DOWN on the chart, per click
//             SL away       stop(s) one nudge further from price
//             TP closer/away  same, for the target(s)
//             Trail now     stop(s) -> the level of the selected TRAIL MODE (one shot)
//             Trail mode    click to cycle (Shift-click = back); the stepper under it sets that mode's value:
//                             FIXED       N ticks behind price
//                             ATR         ATR x multiple behind price
//                             BARS        under the low (above the high) of the last N finished bars, plus an offset
//                             CHANDELIER  ATR x multiple behind the best price since entry
//                             EMA         at the EMA, minus / plus an offset
//                             % PROFIT    locks that % of the best open profit reached
//                             STEP        every N ticks of profit moves the stop up one step (step 1 = break-even)
//                           ATR / BARS / EMA come from the bars of the chart the HUD is on and update on bar close.
//                           Auto Trail runs the same mode continuously; the stop only ever ratchets tighter.
//             + SL / + TP   create a protective stop / target if none exists (OCO-linked)
//             Close 50% / Close 1   partial close (resizes the exit orders to match)
//             Flatten       close position + cancel its orders. "Flatten:" chip picks how:
//                             NT flatten  = Account.Flatten (NinjaTrader then DISABLES strategies on it)
//                             close only  = cancel orders + market close, strategies stay enabled
//             BE offset     - / + stepper (ticks): BE and Auto BE use entry + that many ticks
//             Set SL / Set TP   stop / target at CURRENT PRICE -/+ the stepper's ticks; places one if
//                             none exists (ghost / orphan positions), otherwise moves the existing one
//             (Shift-click a - / + to step 5 ticks)
//             Reverse       cancel exits, then market the opposite way (confirms first)
//             Cancel orders cancel every working order on this instrument
//             Auto BE / Auto Trail   toggles that run on market data
//  ENTRY      Semi-auto entries with a bracket. Set Qty, Stop ticks and Target ticks on the HUD, click ARM (entries do nothing
//             until armed), then BUY MKT / SELL MKT, or BUY LMT / SELL LMT (limit at the bid / ask minus / plus the offset).
//             When the entry fills the HUD places an OCO stop + target that many ticks from the fill price, sized to what
//             actually filled (partial fills are followed). Target ticks 0 = stop only. Everything after that - BE, trail,
//             nudges, flatten - works on the bracket like on any other orders.
//             The entry buttons only OPEN a trade: with a position already on the instrument they refuse.
//             !! HUD entries belong to no strategy. A strategy running on the same account + instrument that closes positions
//             it does not own (the Sneaky GEX Radar Strategy's "Flatten orphan" does) will close a HUD trade. Use the entry
//             buttons on an account / instrument where no such strategy is trading.
//  AUTO TRADE Turns signals from OTHER indicators on this chart into bracket entries. Pick a plot for LONG and a plot for SHORT,
//             and optionally a second plot for each side (the "+ and" row), combined with ANY (either one fires) or ALL (both must
//             agree on the same bar) via the Long/Short: match ANY/ALL button.
//             from the drop-downs (every plot of every indicator on the chart is listed), pick how each is read
//             (above / below / equals a value you set with the - + buttons, or any non-zero value), then switch AUTO TRADE on. A signal is read once per finished bar, and only
//             fires on the bar where the condition BECOMES true. Entries use the ENTRY section's Qty / Stop / Target and go
//             to the account that was selected when AUTO TRADE was switched on.
//             Opposite signal while in a trade: Ignore, Close, or Reverse (click the button to choose).
//             Limits in the settings: max trades per day, a time window, and an account daily-loss stop.
//             AUTO TRADE always starts OFF when the chart loads. Live only - nothing is traded on historical bars.
//  STOP GUARD Strategies that manage their own stop (trailing / re-quoting it) would normally
//             undo a stop you moved here. With Stop guard ON the HUD remembers the stop you last
//             set and, if ANY strategy/ATM moves the stop back to a looser price, immediately puts
//             it back. If the strategy tightens past your level the HUD follows it. Works with any
//             strategy - nothing in the strategy has to change. Stops are only ever restored,
//             never loosened, except by your own SL-down click. (Dragging a stop looser on the chart
//             counts as a strategy move: use SL-down or switch the guard off for that.)
//  ACCOUNT    "Acct" chip: AUTO (Chart Trader's account if readable, else the account that
//             holds a position in this instrument) — click to cycle to a specific account.
//  DRAG       grab the title bar to move it. "–/+" collapses it.
//
//  NOTES
//    • Stops move only in the protective direction when nudged "closer" and are clamped so
//      they can never land on the wrong side of the market (avoids order rejections).
//    • Changing/cancelling orders that a MANAGED strategy owns can make that strategy
//      resync or stop itself — NinjaTrader's own behaviour. For strategy-run charts, test on
//      SIM first.
// ═════════════════════════════════════════════════════════════════════════════
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

public enum SneakyHudCorner { TopLeft, TopRight, BottomLeft, BottomRight }
public enum SneakyHudOpposite { Ignore, Close, Reverse }
public enum SneakyHudTrail { FixedDistance, Atr, BarLowHigh, Chandelier, Ema, PercentOfProfit, Step }
public enum SneakyHudAutoSide { Both, LongOnly, ShortOnly }

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakyTradeHUD : Indicator
    {
        private const string OrdTag = "SnkHud_";

        // ── live state ──
        private volatile Account acct;
        private string overrideName = "";            // "" = not picked yet (never "AUTO" any more -- see ResolveAccount)
        private bool autoPicked = false;             // true once a one-time starting guess has been made
        private double lastPx, bidPx, askPx;
        private bool autoBE, autoTrail, beDone;
        private int lastAuto;
        // ── stop guard ──
        private bool stopGuard;
        private double guardPx = double.NaN;
        private int guardDir;
        private DateTime guardLastApply = DateTime.MinValue;
        private DateTime guardWinStart = DateTime.MinValue;
        private int guardReverts;
        private Account guardSub;
        private readonly object guardLock = new object();
        private Button btnGuard;
        // ── tick steppers / flatten mode ──
        private int beTicks, slTicks = 20, tpTicks = 20;
        private bool flatKeep;
        private TextBlock lblBeT, lblSlT, lblTpT;
        private Button btnBE, btnFlatMode;
        // ── trailing modes ──
        private static readonly string[] TrailNames = { "FIXED", "ATR", "BARS", "CHANDELIER", "EMA", "% PROFIT", "STEP" };
        private int trailMode, trailTicks = 20, atrMultX10 = 20, trailBars = 3, chandMultX10 = 30, emaOffTicks = 4, trailPct = 50, stepTicks = 40;
        private ATR atrI; private EMA emaI;
        private volatile bool barsReady;
        private double atrC, emaC;                                   // cached on bar close (safe to read from any thread)
        private double[] recentLow = new double[0], recentHigh = new double[0];   // last finished bars, newest first
        private double tickHi = double.MinValue, tickLo = double.MaxValue, extPx, extKeyAvg; private int extKeyDir;
        private Button btnTrailMode; private TextBlock lblTrailV;
        // ── semi-auto entry + bracket ──
        private bool armed;
        private int entQty = 1, entSl = 40, entTp = 80, entOff = 0;
        private Button btnArm; private TextBlock lblEntQty, lblEntSl, lblEntTp, lblEntOff, lblRisk;
        private readonly object entryLock = new object();
        private Order pendEntry, brStop, brTgt;                      // the working HUD entry and the bracket placed for it
        private int pendDir, pendSl, pendTp; private bool pendActive;
        private DateTime bracketAt = DateTime.MinValue, pendFillSeen = DateTime.MinValue;
        // ── auto trade from other indicators' plots ──
        private sealed class PlotSrc { public string Key, Label; public NinjaTrader.NinjaScript.IndicatorBase Ind; public NinjaTrader.NinjaScript.StrategyBase Strat; public int Plot; }
        private List<PlotSrc> plotSrcs = new List<PlotSrc>();
        // Up to MaxAutoSlots independent conditions per side, combined with OR (any one firing is enough -- e.g. GEX
        // Radar's Breakout/WallFade/Flip/PinTrap/Chop signals are alternative triggers, not things that should ever
        // all be true at once). NinjaTrader itself has no limit on this kind of internal state (these aren't plots,
        // just picker rows) -- 10 is a practical ceiling, comfortably past the ~7 tradeable signal plots any one
        // source indicator/strategy exposes, with room to spare for a second source. The "+ Add" button reveals one
        // more row at a time up to this ceiling; the scrollable panel (see bodyScroll) keeps it usable past a few rows.
        private const int MaxAutoSlots = 10;
        private PlotSrc[] srcLong = new PlotSrc[MaxAutoSlots], srcShort = new PlotSrc[MaxAutoSlots];
        private int[] ruleLong = new int[MaxAutoSlots], ruleShort = new int[MaxAutoSlots];   // 0 = above the value, 1 = below the value, 2 = any non-zero value, 3 = equals the value
        private double[] valLong = new double[MaxAutoSlots], valShort = new double[MaxAutoSlots];
        private int longSlotCount = 1, shortSlotCount = 1;             // how many rows are currently shown per side (1..MaxAutoSlots)
        private static readonly string[] RuleNames = { "above", "below", "not zero", "equals" };
        private static readonly int[] RuleNext = { 1, 3, 0, 2 };       // click order: above -> below -> equals -> not zero
        private TextBlock[] lblValL = new TextBlock[MaxAutoSlots], lblValS = new TextBlock[MaxAutoSlots];
        private static string ValTxt(double v) { return (v > 0 ? "+" : "") + v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture); }
        private string RuleTxt(int rule, double v) { return rule == 2 ? "is not zero" : (rule == 0 ? "> " : (rule == 1 ? "< " : "= ")) + ValTxt(v); }
        // slot is 0-based (array index); the persisted properties are numbered 1..MaxAutoSlots -- these are the
        // only place that mapping needs to be written out, everything else just uses the 0-based arrays/helpers.
        private string GetSourceProp(bool isLong, int slot)
        {
            switch (slot)
            {
                case 0: return isLong ? AutoLongSource1 : AutoShortSource1;
                case 1: return isLong ? AutoLongSource2 : AutoShortSource2;
                case 2: return isLong ? AutoLongSource3 : AutoShortSource3;
                case 3: return isLong ? AutoLongSource4 : AutoShortSource4;
                case 4: return isLong ? AutoLongSource5 : AutoShortSource5;
                case 5: return isLong ? AutoLongSource6 : AutoShortSource6;
                case 6: return isLong ? AutoLongSource7 : AutoShortSource7;
                case 7: return isLong ? AutoLongSource8 : AutoShortSource8;
                case 8: return isLong ? AutoLongSource9 : AutoShortSource9;
                default: return isLong ? AutoLongSource10 : AutoShortSource10;
            }
        }
        private void SetSourceProp(bool isLong, int slot, string v)
        {
            switch (slot)
            {
                case 0: if (isLong) AutoLongSource1 = v; else AutoShortSource1 = v; break;
                case 1: if (isLong) AutoLongSource2 = v; else AutoShortSource2 = v; break;
                case 2: if (isLong) AutoLongSource3 = v; else AutoShortSource3 = v; break;
                case 3: if (isLong) AutoLongSource4 = v; else AutoShortSource4 = v; break;
                case 4: if (isLong) AutoLongSource5 = v; else AutoShortSource5 = v; break;
                case 5: if (isLong) AutoLongSource6 = v; else AutoShortSource6 = v; break;
                case 6: if (isLong) AutoLongSource7 = v; else AutoShortSource7 = v; break;
                case 7: if (isLong) AutoLongSource8 = v; else AutoShortSource8 = v; break;
                case 8: if (isLong) AutoLongSource9 = v; else AutoShortSource9 = v; break;
                default: if (isLong) AutoLongSource10 = v; else AutoShortSource10 = v; break;
            }
        }
        private int GetRuleProp(bool isLong, int slot)
        {
            switch (slot)
            {
                case 0: return isLong ? AutoLongRule1 : AutoShortRule1;
                case 1: return isLong ? AutoLongRule2 : AutoShortRule2;
                case 2: return isLong ? AutoLongRule3 : AutoShortRule3;
                case 3: return isLong ? AutoLongRule4 : AutoShortRule4;
                case 4: return isLong ? AutoLongRule5 : AutoShortRule5;
                case 5: return isLong ? AutoLongRule6 : AutoShortRule6;
                case 6: return isLong ? AutoLongRule7 : AutoShortRule7;
                case 7: return isLong ? AutoLongRule8 : AutoShortRule8;
                case 8: return isLong ? AutoLongRule9 : AutoShortRule9;
                default: return isLong ? AutoLongRule10 : AutoShortRule10;
            }
        }
        private void SetRuleProp(bool isLong, int slot, int v)
        {
            switch (slot)
            {
                case 0: if (isLong) AutoLongRule1 = v; else AutoShortRule1 = v; break;
                case 1: if (isLong) AutoLongRule2 = v; else AutoShortRule2 = v; break;
                case 2: if (isLong) AutoLongRule3 = v; else AutoShortRule3 = v; break;
                case 3: if (isLong) AutoLongRule4 = v; else AutoShortRule4 = v; break;
                case 4: if (isLong) AutoLongRule5 = v; else AutoShortRule5 = v; break;
                case 5: if (isLong) AutoLongRule6 = v; else AutoShortRule6 = v; break;
                case 6: if (isLong) AutoLongRule7 = v; else AutoShortRule7 = v; break;
                case 7: if (isLong) AutoLongRule8 = v; else AutoShortRule8 = v; break;
                case 8: if (isLong) AutoLongRule9 = v; else AutoShortRule9 = v; break;
                default: if (isLong) AutoLongRule10 = v; else AutoShortRule10 = v; break;
            }
        }
        private double GetValueProp(bool isLong, int slot)
        {
            switch (slot)
            {
                case 0: return isLong ? AutoLongValue1 : AutoShortValue1;
                case 1: return isLong ? AutoLongValue2 : AutoShortValue2;
                case 2: return isLong ? AutoLongValue3 : AutoShortValue3;
                case 3: return isLong ? AutoLongValue4 : AutoShortValue4;
                case 4: return isLong ? AutoLongValue5 : AutoShortValue5;
                case 5: return isLong ? AutoLongValue6 : AutoShortValue6;
                case 6: return isLong ? AutoLongValue7 : AutoShortValue7;
                case 7: return isLong ? AutoLongValue8 : AutoShortValue8;
                case 8: return isLong ? AutoLongValue9 : AutoShortValue9;
                default: return isLong ? AutoLongValue10 : AutoShortValue10;
            }
        }
        private void SetValueProp(bool isLong, int slot, double v)
        {
            switch (slot)
            {
                case 0: if (isLong) AutoLongValue1 = v; else AutoShortValue1 = v; break;
                case 1: if (isLong) AutoLongValue2 = v; else AutoShortValue2 = v; break;
                case 2: if (isLong) AutoLongValue3 = v; else AutoShortValue3 = v; break;
                case 3: if (isLong) AutoLongValue4 = v; else AutoShortValue4 = v; break;
                case 4: if (isLong) AutoLongValue5 = v; else AutoShortValue5 = v; break;
                case 5: if (isLong) AutoLongValue6 = v; else AutoShortValue6 = v; break;
                case 6: if (isLong) AutoLongValue7 = v; else AutoShortValue7 = v; break;
                case 7: if (isLong) AutoLongValue8 = v; else AutoShortValue8 = v; break;
                case 8: if (isLong) AutoLongValue9 = v; else AutoShortValue9 = v; break;
                default: if (isLong) AutoLongValue10 = v; else AutoShortValue10 = v; break;
            }
        }
        private volatile bool autoOn; private Account autoAcct;
        private int oppAction, autoEvalBar = -1, autoEvalDue, revDir; private DateTime autoDay = DateTime.MinValue, revUntil, closeStart;
        // auto-trade counter: per ACCOUNT, and per trading SESSION rather than per calendar day.
        // Counting globally meant trades the HUD placed on one account blocked every other account,
        // and resetting on local midnight meant the evening session (which opens at 18:00 ET) started
        // already carrying the day session's count. Key = account name, value = entries this session.
        private readonly Dictionary<string, int> autoTradesAcct = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly object tradeCntLock = new object();
        private string acctDriftNote = "";
        private DateTime autoSession = DateTime.MinValue;
        // "Require break of signal bar" bookkeeping: a signal that's waiting for a LATER bar to break its own signal bar's high/low
        private bool sigPendActive; private int sigPendDir, sigPendBar; private double sigPendHigh, sigPendLow;
        private bool closing, closeSent;
        private string autoNote = "";
        private ComboBox[] cbLong = new ComboBox[MaxAutoSlots], cbShort = new ComboBox[MaxAutoSlots];
        private Button[] btnRuleL = new Button[MaxAutoSlots], btnRuleS = new Button[MaxAutoSlots];
        private StackPanel longRows, shortRows;
        private Button btnAutoTrade, btnOpp, btnAddLong, btnRemoveLong, btnAddShort, btnRemoveShort; private TextBlock lblAutoInfo; private bool fillingCombos;
        private Button btnTradeSide, btnReqBreak, btnReqConsec; private TextBlock lblConsecCount;
        private ScrollViewer bodyScroll; private Button btnSize; private static readonly double[] SizeSteps = { 1.0, 0.85, 0.7, 0.55, 0.45, 0.35 };
        private Account pendAcct;                                    // the account the entry went to (AUTO may resolve differently later)
        private string status = "";
        private DateTime statusTime = DateTime.MinValue;

        // ── UI ──
        private Border root;
        private StackPanel body;
        private TextBlock lblPos, lblOpen, lblDetail, lblSession, lblOrders, lblStatus;
        private Button btnAcct, btnCollapse, btnAutoBE, btnAutoTrail, btnDock, btnTmpl, btnTmplDeleteRef;
        private string currentTemplateName = "";
        private DispatcherTimer uiTimer;
        private bool dragging, collapsed;
        private Point dragStart;
        private double dragOffX, dragOffY;
        // ── Chart Trader docking (experimental -- relies on an internal/undocumented NT8 UI hook;
        // falls back to the normal floating panel automatically if the hook isn't found) ──
        private Grid chartTraderGrid;
        private System.Windows.Controls.RowDefinition chartTraderRowDef;
        private bool dockedInChartTrader;

        private static Brush B(string hex)
        {
            Brush b = (Brush)new BrushConverter().ConvertFromString(hex);
            if (b.CanFreeze) b.Freeze();
            return b;
        }
        private static readonly Brush BgPanel = B("#E6101722");
        private static readonly Brush BgBtn   = B("#1B2433");
        private static readonly Brush BdBtn   = B("#34425A");
        private static readonly Brush FgMain  = B("#E6EAF0");
        private static readonly Brush FgDim   = B("#8B97A8");
        private static readonly Brush Green   = B("#2ECC71");
        private static readonly Brush Red     = B("#E74C3C");
        private static readonly Brush Amber   = B("#F2B134");

        // Re-derives every local working field from the current [NinjaScriptProperty] values.
        // Runs once at State.Historical (normal indicator startup), and again after a template is
        // loaded on a live chart, so both paths end up with exactly the same state.
        private void ResyncFromProperties()
        {
            stopGuard = StopGuardDefault;
            trailMode = (int)TrailModeDefault;
            entQty = Math.Max(1, EntryQty); entSl = Math.Max(1, EntryStopTicks); entTp = Math.Max(0, EntryTargetTicks);
            oppAction = (int)AutoOpposite;
            longSlotCount = Math.Max(1, Math.Min(MaxAutoSlots, AutoLongSlotCount == 0 ? 1 : AutoLongSlotCount));
            shortSlotCount = Math.Max(1, Math.Min(MaxAutoSlots, AutoShortSlotCount == 0 ? 1 : AutoShortSlotCount));
            for (int i = 0; i < MaxAutoSlots; i++)
            {
                ruleLong[i] = Math.Max(0, Math.Min(3, GetRuleProp(true, i))); valLong[i] = GetValueProp(true, i);
                ruleShort[i] = Math.Max(0, Math.Min(3, GetRuleProp(false, i))); valShort[i] = GetValueProp(false, i);
            }
            try { trailTicks = Math.Max(1, (int)Math.Round(TrailPoints / Tk)); stepTicks = Math.Max(1, (int)Math.Round(TrailStepPoints / Tk)); } catch { }
            atrMultX10 = Math.Max(1, (int)Math.Round(TrailAtrMult * 10)); chandMultX10 = Math.Max(1, (int)Math.Round(ChandelierMult * 10));
            trailBars = Math.Max(1, TrailBars); emaOffTicks = TrailEmaOffsetTicks; trailPct = Math.Max(5, Math.Min(95, TrailPercent));
            try { beTicks = (int)Math.Round(BeOffsetPoints / Tk); } catch { beTicks = 0; }
            slTicks = Math.Max(1, SlFromPriceTicks); tpTicks = Math.Max(1, TpFromPriceTicks);
            flatKeep = FlattenKeepsStrategies;
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Universal on-chart trade HUD: open/session P&L plus one-click order management (BE, nudge SL/TP, trail, flatten...).";
                Name        = "SneakyTradeHUD";
                Calculate   = Calculate.OnBarClose;
                IsOverlay   = true;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;

                Corner            = SneakyHudCorner.TopRight;
                OffsetX           = 12;
                OffsetY           = 40;
                NudgePoints       = 1.0;
                BeOffsetPoints    = 0.0;
                TrailPoints       = 5.0;
                DefaultStopPoints   = 10.0;
                DefaultTargetPoints = 20.0;
                AutoBeTriggerPoints = 5.0;
                AutoTrailStartPoints = 5.0;
                ConfirmReverse    = true;
                StartCollapsed    = false;
                StopGuardDefault  = true;
                SlFromPriceTicks  = 20;
                TpFromPriceTicks  = 20;
                TrailModeDefault  = SneakyHudTrail.FixedDistance;
                ShowEntry = true; EntryQty = 1; EntryStopTicks = 40; EntryTargetTicks = 80; DisarmAfterEntry = false;
                ShowAutoTrade = true;
                AutoLongSlotCount = 1; AutoShortSlotCount = 1;
                AutoLongSource1 = ""; AutoLongRule1 = 0; AutoLongValue1 = 0;
                AutoShortSource1 = ""; AutoShortRule1 = 1; AutoShortValue1 = 0;
                HudScale = 0.55; BodyMaxHeight = 560; AutoOpposite = SneakyHudOpposite.Ignore;
                AutoMaxTradesPerDay = 10; AutoSessionResetHHmm = 1800; AutoStartHHmm = 0; AutoEndHHmm = 0; AutoDailyLossLimit = 0;
                AutoTradeSide = SneakyHudAutoSide.Both; AutoRequireBreakOfSignalBar = false; AutoBreakMaxBars = 20; AutoRequireConsecutiveBars = false; AutoConsecutiveBarsCount = 2;
                TrailAtrPeriod = 14; TrailAtrMult = 2.0; TrailBars = 3; TrailBarOffsetTicks = 2; ChandelierMult = 3.0;
                TrailEmaPeriod = 21; TrailEmaOffsetTicks = 4; TrailPercent = 50; TrailStepPoints = 10.0;
                FlattenKeepsStrategies = false;
                CancelHudOrdersWhenFlat = true;
            }
            else if (State == State.DataLoaded)
            {
                atrI = ATR(Math.Max(1, TrailAtrPeriod));
                emaI = EMA(Math.Max(1, TrailEmaPeriod));
            }
            else if (State == State.Historical)
            {
                ResyncFromProperties();
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(new Action(BuildUi));
            }
            else if (State == State.Terminated)
            {
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(new Action(TearDownUi));
            }
        }

        // caches the bar-based inputs of the trailing modes once per finished bar
        protected override void OnBarUpdate()
        {
            // a bar just finished: read the chosen signal plots shortly after, once every indicator on the chart has processed it
            if (autoOn && State == State.Realtime && CurrentBar > 2)
            {
                if (Calculate == Calculate.OnBarClose) { autoEvalBar = CurrentBar; autoEvalDue = Environment.TickCount + 150; }
                else if (IsFirstTickOfBar) { autoEvalBar = CurrentBar - 1; autoEvalDue = Environment.TickCount + 150; }
            }
            try
            {
                if (atrI == null || emaI == null) return;
                if (CurrentBar < Math.Max(TrailAtrPeriod, 2)) return;
                int n = Math.Min(50, CurrentBar + 1);
                double[] lo = new double[n], hi = new double[n];
                for (int i = 0; i < n; i++) { lo[i] = Low[i]; hi[i] = High[i]; }
                recentLow = lo; recentHigh = hi;
                atrC = atrI[0]; emaC = emaI[0];
                barsReady = true;
            }
            catch { }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            try
            {
                if (e.MarketDataType == MarketDataType.Last) lastPx = e.Price;
                else if (e.MarketDataType == MarketDataType.Bid) bidPx = e.Price;
                else if (e.MarketDataType == MarketDataType.Ask) askPx = e.Price;
                else return;
                if (e.MarketDataType == MarketDataType.Last) { if (e.Price > tickHi) tickHi = e.Price; if (e.Price < tickLo) tickLo = e.Price; }
                if (autoEvalBar >= 0 && Environment.TickCount - autoEvalDue >= 0) { int eb = autoEvalBar; autoEvalBar = -1; AutoEvaluate(eb); }
                RunAutos();
            }
            catch { }
        }

        // ═════════════════ helpers ═════════════════
        private double Tk { get { return Instrument.MasterInstrument.TickSize; } }
        private double Rnd(double p) { return Instrument.MasterInstrument.RoundToTickSize(p); }

        private void SetStatus(string s)
        {
            status = s; statusTime = DateTime.Now;
            try { Print("[SneakyTradeHUD] " + s); } catch { }
        }

        private double Last()
        {
            if (lastPx > 0) return lastPx;
            try { return Instrument.MarketData.Last.Price; } catch { }
            return 0;
        }
        private double Bid() { return bidPx > 0 ? bidPx : Last(); }
        private double Ask() { return askPx > 0 ? askPx : Last(); }

        private static bool IsConnected(Account a)
        {
            try { return a != null && a.Connection != null && a.Connection.Status == ConnectionStatus.Connected; }
            catch { return false; }
        }

        private static Account FindAcct(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                lock (Account.All)
                {
                    foreach (Account a in Account.All)
                        if (a != null && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
                }
            }
            catch { }
            return null;
        }

        private Position FindPos(Account a)
        {
            try
            {
                foreach (Position p in a.Positions.ToArray())
                    if (p != null && p.Instrument != null && p.Instrument.FullName == Instrument.FullName
                        && p.MarketPosition != MarketPosition.Flat && p.Quantity > 0)
                        return p;
            }
            catch { }
            return null;
        }

        private static int Dir(Position p) { return p == null ? 0 : (p.MarketPosition == MarketPosition.Long ? 1 : p.MarketPosition == MarketPosition.Short ? -1 : 0); }

        private static bool IsLive(Order o)
        {
            return o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted
                || o.OrderState == OrderState.PartFilled || o.OrderState == OrderState.TriggerPending;
        }

        private List<Order> LiveOrders(Account a)
        {
            List<Order> l = new List<Order>();
            try
            {
                foreach (Order o in a.Orders.ToArray())
                    if (o != null && o.Instrument != null && o.Instrument.FullName == Instrument.FullName && IsLive(o)) l.Add(o);
            }
            catch { }
            return l;
        }

        private List<Order> ExitOrders(Account a, int dir, bool stops)
        {
            List<Order> l = new List<Order>();
            double px = Last();
            foreach (Order o in LiveOrders(a))
            {
                bool exitAct = dir > 0 ? (o.OrderAction == OrderAction.Sell || o.OrderAction == OrderAction.SellShort)
                                       : (o.OrderAction == OrderAction.BuyToCover || o.OrderAction == OrderAction.Buy);
                if (!exitAct) continue;
                if (stops)
                {
                    if ((o.OrderType == OrderType.StopMarket || o.OrderType == OrderType.StopLimit)
                        && (dir > 0 ? o.StopPrice < px : o.StopPrice > px)) l.Add(o);
                }
                else
                {
                    if (o.OrderType == OrderType.Limit && (dir > 0 ? o.LimitPrice > px : o.LimitPrice < px)) l.Add(o);
                }
            }
            return l;
        }

        // Finds Chart Trader's own content Grid via the same internal reflection hook that used to
        // back account resolution (removed -- see ResolveAccount). Still used for docking the HUD
        // Internal/undocumented -- can return null if Chart Trader isn't open on this chart, or if a
        // future NT8 update renames/restructures it. Callers must treat null as "not available" and
        // fall back gracefully, never assume this keeps working.
        private Grid FindChartTraderGrid()
        {
            try
            {
                NTWindow w = Window.GetWindow(ChartControl) as NTWindow;
                if (w == null) return null;
                DependencyObject d = w.FindFirst("ChartWindowChartTraderControl");
                if (d == null) return null;
                PropertyInfo pi = d.GetType().GetProperty("Content");
                if (pi == null) return null;
                return pi.GetValue(d, null) as Grid;
            }
            catch { return null; }
        }

        // Tries to embed 'root' as an extra row inside Chart Trader's panel instead of floating it on
        // the chart. Returns false (leaving 'root' untouched) on any failure so BuildUi() can fall back
        // to the normal floating placement.
        private bool TryDockInChartTrader()
        {
            try
            {
                Grid g = FindChartTraderGrid();
                if (g == null) return false;

                root.HorizontalAlignment = HorizontalAlignment.Stretch;
                root.VerticalAlignment = VerticalAlignment.Top;
                root.Margin = new Thickness(0, 4, 0, 4);
                System.Windows.Controls.Panel.SetZIndex(root, 0);

                chartTraderRowDef = new System.Windows.Controls.RowDefinition();
                chartTraderRowDef.Height = GridLength.Auto;
                g.RowDefinitions.Add(chartTraderRowDef);
                Grid.SetRow(root, g.RowDefinitions.Count - 1);
                if (g.ColumnDefinitions.Count > 1) Grid.SetColumnSpan(root, g.ColumnDefinitions.Count);
                if (!g.Children.Contains(root)) g.Children.Add(root);

                chartTraderGrid = g;
                return true;
            }
            catch { chartTraderGrid = null; chartTraderRowDef = null; return false; }
        }

        // Used only once, to guess a sensible starting account before the user has made an explicit
        // pick (see autoPicked in UpdateUi). This used to also fall back to whatever Chart Trader
        // currently resolved to, via reflection, every UI tick -- that silent "whatever the panel
        // isn't necessarily showing" value is exactly how 10 auto-trade entries once reached Sim120
        // while the HUD read FLAT (Sim100). Account selection is now always explicit: click "Acct:"
        // to cycle to the one you want. This only picks a reasonable starting point.
        private Account ResolveAccount()
        {
            try
            {
                lock (Account.All)
                {
                    foreach (Account a in Account.All)
                        if (a != null && IsConnected(a) && FindPos(a) != null) return a;
                    foreach (Account a in Account.All)
                        if (a != null && IsConnected(a)) return a;
                }
            }
            catch { }
            return null;
        }

        // ═════════════════ order actions ═════════════════
        private void ApplyStop(Account a, Order o, double newPrice) { ApplyStop(a, o, newPrice, true); }

        private void ApplyStop(Account a, Order o, double newPrice, bool remember)
        {
            double oldStop = o.StopPrice;
            guardLastApply = DateTime.UtcNow;
            if (remember)
            {
                // the level the USER asked for becomes the guarded level
                bool longExit = o.OrderAction == OrderAction.Sell || o.OrderAction == OrderAction.SellShort;
                lock (guardLock) { guardPx = newPrice; guardDir = longExit ? 1 : -1; guardReverts = 0; }
            }
            o.StopPriceChanged = newPrice;
            if (o.OrderType == OrderType.StopLimit) o.LimitPriceChanged = o.LimitPrice + (newPrice - oldStop);
            a.Change(new Order[] { o });
        }

        // ═════════════════ stop guard ═════════════════
        private void EnsureGuardSub(Account a)
        {
            if (a == guardSub) return;
            if (pendActive && guardSub == pendAcct) return;      // don't drop the entry account's order events mid-entry
            try { if (guardSub != null) guardSub.OrderUpdate -= OnGuardOrderUpdate; } catch { }
            lock (guardLock) { guardPx = double.NaN; }
            guardSub = a;
            try { if (a != null) a.OrderUpdate += OnGuardOrderUpdate; } catch { }
        }

        private void DropGuardSub()
        {
            try { if (guardSub != null) guardSub.OrderUpdate -= OnGuardOrderUpdate; } catch { }
            guardSub = null;
        }

        private void OnGuardOrderUpdate(object sender, OrderEventArgs e)
        {
            try { if (pendActive && e.Order != null && e.Order.Name == OrdTag + "Entry" && ReferenceEquals(sender, pendAcct)) EntryReconcile(); } catch { }
            GuardCheck(sender as Account, e.Order);
        }

        // also runs from the UI timer so a strategy re-quote that landed inside the settle window is still caught
        private void GuardCheck(Account a, Order o)
        {
            try
            {
                if (!stopGuard || double.IsNaN(guardPx)) return;
                if (o == null || a == null || o.Instrument == null || o.Instrument.FullName != Instrument.FullName) return;
                if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) return;
                if (o.OrderType != OrderType.StopMarket && o.OrderType != OrderType.StopLimit) return;

                Position p = FindPos(a);
                int dir = Dir(p);
                if (dir == 0 || dir != guardDir) { lock (guardLock) { guardPx = double.NaN; } return; }

                bool exitAct = dir > 0 ? (o.OrderAction == OrderAction.Sell || o.OrderAction == OrderAction.SellShort)
                                       : (o.OrderAction == OrderAction.BuyToCover || o.OrderAction == OrderAction.Buy);
                if (!exitAct) return;
                double px = Last();
                if (px <= 0 || !(dir > 0 ? o.StopPrice < px : o.StopPrice > px)) return;   // protective stops only

                double g = guardPx;
                // a strategy / ATM tightened beyond the guarded level: follow it, never fight it
                if (dir > 0 ? o.StopPrice > g + Tk / 2 : o.StopPrice < g - Tk / 2) { lock (guardLock) { guardPx = o.StopPrice; } return; }
                bool looser = dir > 0 ? o.StopPrice < g - Tk / 2 : o.StopPrice > g + Tk / 2;
                if (!looser) return;
                if ((DateTime.UtcNow - guardLastApply).TotalMilliseconds < 400) return;   // our own change still settling

                double refPx = dir > 0 ? Bid() : Ask();
                double target = Rnd(dir > 0 ? Math.Min(g, refPx - Tk) : Math.Max(g, refPx + Tk));
                if (!(dir > 0 ? target > o.StopPrice + Tk / 2 : target < o.StopPrice - Tk / 2)) return;   // market already through it

                DateTime now = DateTime.UtcNow;
                if ((now - guardWinStart).TotalSeconds > 10) { guardWinStart = now; guardReverts = 0; }
                if (++guardReverts > 25)
                {
                    stopGuard = false;
                    SetStatus("Stop guard released: a strategy keeps re-quoting its stop (25 restores in 10s). Turn guard back on only if you want the fight.");
                    return;
                }
                double was = o.StopPrice;
                ApplyStop(a, o, target, false);
                SetStatus("Guard: stop was moved to " + was.ToString("0.00", CultureInfo.InvariantCulture)
                          + " by another process -> restored " + target.ToString("0.00", CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private void ApplyLimit(Account a, Order o, double newPrice)
        {
            o.LimitPriceChanged = newPrice;
            a.Change(new Order[] { o });
        }

        private bool Ctx(out Account a, out Position p, out int dir, bool silent)
        {
            a = acct; p = null; dir = 0;
            if (a == null) { if (!silent) SetStatus("No account resolved."); return false; }
            p = FindPos(a); dir = Dir(p);
            if (p == null) { if (!silent) SetStatus("No open position on " + Instrument.MasterInstrument.Name + " (" + a.Name + ")."); return false; }
            return true;
        }

        // move every stop to 'target' only if that is an improvement (tighter) and still valid vs market
        private int MoveStopsTo(Account a, int dir, double target, bool silent, string what)
        {
            double refPx = dir > 0 ? Bid() : Ask();
            double t = dir > 0 ? Math.Min(target, refPx - Tk) : Math.Max(target, refPx + Tk);
            t = Rnd(t);
            int moved = 0;
            List<Order> stops = ExitOrders(a, dir, true);
            if (stops.Count == 0) { if (!silent) SetStatus("No working stop found. Use + SL."); return 0; }
            foreach (Order s in stops)
            {
                bool better = dir > 0 ? t > s.StopPrice + Tk / 2 : t < s.StopPrice - Tk / 2;
                if (!better) continue;
                ApplyStop(a, s, t); moved++;
            }
            if (!silent) SetStatus(moved > 0 ? what + ": " + moved + " stop(s) -> " + t.ToString("0.00", CultureInfo.InvariantCulture)
                                              : what + ": stop already at/beyond " + t.ToString("0.00", CultureInfo.InvariantCulture));
            return moved;
        }

        private void DoBE()
        {
            Account a; Position p; int dir;
            if (!Ctx(out a, out p, out dir, false)) return;
            double be = Rnd(p.AveragePrice + dir * beTicks * Tk);
            double px = dir > 0 ? Bid() : Ask();
            if (dir > 0 ? px <= be + Tk : px >= be - Tk) { SetStatus("BE: price hasn't moved past break-even yet."); return; }
            MoveStopsTo(a, dir, be, false, "BE");
        }

        private void DoLock50()
        {
            Account a; Position p; int dir;
            if (!Ctx(out a, out p, out dir, false)) return;
            double px = Last();
            double profit = (px - p.AveragePrice) * dir;
            if (profit <= Tk) { SetStatus("Lock 50%: position isn't in profit."); return; }
            MoveStopsTo(a, dir, p.AveragePrice + dir * profit * 0.5, false, "Lock 50%");
        }

        private void DoTrailNow()
        {
            Account a; Position p; int dir;
            if (!Ctx(out a, out p, out dir, false)) return;
            double tg; string err;
            if (!TrailTarget(p, dir, out tg, out err)) { SetStatus("Trail " + TrailNames[trailMode] + ": " + err); return; }
            MoveStopsTo(a, dir, tg, false, "Trail " + TrailNames[trailMode]);
        }

        // best price since this position was first seen (for Chandelier / % profit / Step). Called ~3x a second from the data thread.
        private void TrackExtreme(Position p, int dir)
        {
            double px = Last();
            if (p == null || dir == 0) { extKeyDir = 0; extPx = 0; tickHi = double.MinValue; tickLo = double.MaxValue; return; }
            if (extKeyDir != dir || Math.Abs(extKeyAvg - p.AveragePrice) > Tk / 2) { extKeyDir = dir; extKeyAvg = p.AveragePrice; extPx = px; }
            double h = tickHi, l = tickLo;
            tickHi = double.MinValue; tickLo = double.MaxValue;
            if (dir > 0) extPx = Math.Max(extPx, Math.Max(px, h == double.MinValue ? px : h));
            else extPx = Math.Min(extPx, Math.Min(px, l == double.MaxValue ? px : l));
        }

        // where the selected trail mode would put the stop right now. Uses only cached values, so the UI thread may call it.
        private bool TrailTarget(Position p, int dir, out double target, out string err)
        {
            if (!TrailRaw(p, dir, out target, out err)) return false;
            // never hand back a level at or through the market: the stop would be jammed one tick from price and fill at once
            // (happens when price is on the wrong side of the EMA, or already beyond the bar low / high)
            double mk = dir > 0 ? Bid() : Ask();
            if (dir > 0 ? target >= mk - Tk : target <= mk + Tk)
            {
                err = "level " + Rnd(target).ToString("0.00", CultureInfo.InvariantCulture) + " is at or through the market - not moved";
                return false;
            }
            return true;
        }
        private bool TrailRaw(Position p, int dir, out double target, out string err)
        {
            target = 0; err = "";
            double px = Last();
            if (px <= 0 || p == null || dir == 0) { err = "no live price yet"; return false; }
            double avg = p.AveragePrice;
            double ext = (extKeyDir == dir && Math.Abs(extKeyAvg - avg) <= Tk / 2 && extPx > 0) ? extPx : px;
            ext = dir > 0 ? Math.Max(ext, px) : Math.Min(ext, px);
            switch (trailMode)
            {
                case 1:
                    if (!barsReady || atrC <= 0) { err = "ATR not ready (needs more bars)"; return false; }
                    target = px - dir * atrC * atrMultX10 / 10.0; return true;
                case 2:
                {
                    double[] lo = recentLow, hi = recentHigh;
                    int n = Math.Min(trailBars, Math.Min(lo.Length, hi.Length));
                    if (!barsReady || n < 1) { err = "bars not ready"; return false; }
                    double v = dir > 0 ? double.MaxValue : double.MinValue;
                    for (int i = 0; i < n; i++) v = dir > 0 ? Math.Min(v, lo[i]) : Math.Max(v, hi[i]);
                    target = v - dir * TrailBarOffsetTicks * Tk; return true;
                }
                case 3:
                    if (!barsReady || atrC <= 0) { err = "ATR not ready (needs more bars)"; return false; }
                    target = ext - dir * atrC * chandMultX10 / 10.0; return true;
                case 4:
                    if (!barsReady || emaC <= 0) { err = "EMA not ready (needs more bars)"; return false; }
                    target = emaC - dir * emaOffTicks * Tk; return true;
                case 5:
                {
                    double gain = (ext - avg) * dir;
                    if (gain <= Tk) { err = "position hasn't been in profit yet"; return false; }
                    target = avg + dir * gain * trailPct / 100.0; return true;
                }
                case 6:
                {
                    double gain = (ext - avg) * dir, step = stepTicks * Tk;
                    int k = (int)Math.Floor(gain / step + 1e-9);
                    if (k < 1) { err = "first step (" + stepTicks + " ticks of profit) not reached yet"; return false; }
                    target = avg + dir * (k - 1) * step; return true;
                }
                default:
                    target = px - dir * trailTicks * Tk; return true;
            }
        }

        private int TrailParam
        {
            get { return trailMode == 1 ? atrMultX10 : trailMode == 2 ? trailBars : trailMode == 3 ? chandMultX10 : trailMode == 4 ? emaOffTicks : trailMode == 5 ? trailPct : trailMode == 6 ? stepTicks : trailTicks; }
            set
            {
                if (trailMode == 1) atrMultX10 = Math.Max(1, Math.Min(200, value));
                else if (trailMode == 2) trailBars = Math.Max(1, Math.Min(50, value));
                else if (trailMode == 3) chandMultX10 = Math.Max(1, Math.Min(200, value));
                else if (trailMode == 4) emaOffTicks = Math.Max(-200, Math.Min(400, value));
                else if (trailMode == 5) trailPct = Math.Max(5, Math.Min(95, value));
                else if (trailMode == 6) stepTicks = Math.Max(1, Math.Min(2000, value));
                else trailTicks = Math.Max(1, Math.Min(2000, value));
            }
        }
        private string TrailParamText()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            switch (trailMode)
            {
                case 1: return (atrMultX10 / 10.0).ToString("0.0", ci) + " x";
                case 2: return trailBars + (trailBars == 1 ? " bar" : " bars");
                case 3: return (chandMultX10 / 10.0).ToString("0.0", ci) + " x";
                case 4: return emaOffTicks.ToString("+0;-0;0", ci) + " t";
                case 5: return trailPct + " %";
                case 6: return stepTicks + " t";
                default: return trailTicks + " t";
            }
        }

        // 'up' = move the order UP on the chart (higher price), whatever side the position is on
        private void NudgeStop(bool up)
        {
            Account a; Position p; int dir;
            if (!Ctx(out a, out p, out dir, false)) return;
            bool closer = up ? dir > 0 : dir < 0;       // long: stop is below price, so up = closer. short: mirrored
            List<Order> stops = ExitOrders(a, dir, true);
            if (stops.Count == 0) { SetStatus("No working stop found. Use + SL."); return; }
            double refPx = dir > 0 ? Bid() : Ask();
            int moved = 0; double last = 0;
            foreach (Order s in stops)
            {
                double cand = s.StopPrice + (closer ? dir : -dir) * NudgePoints;
                if (closer) cand = dir > 0 ? Math.Min(cand, refPx - Tk) : Math.Max(cand, refPx + Tk);
                cand = Rnd(cand);
                bool changed = Math.Abs(cand - s.StopPrice) >= Tk / 2;
                if (closer && !(dir > 0 ? cand > s.StopPrice : cand < s.StopPrice)) changed = false;
                if (!changed) continue;
                ApplyStop(a, s, cand); moved++; last = cand;
            }
            SetStatus(moved > 0 ? "SL " + (up ? "up" : "down") + " (" + (closer ? "closer" : "away") + ") -> " + last.ToString("0.00", CultureInfo.InvariantCulture)
                                : "SL can't move further " + (closer ? "(already at market)." : "."));
        }

        private void NudgeTarget(bool up)
        {
            Account a; Position p; int dir;
            if (!Ctx(out a, out p, out dir, false)) return;
            bool closer = up ? dir < 0 : dir > 0;       // long: target is above price, so up = further away. short: mirrored
            List<Order> tgts = ExitOrders(a, dir, false);
            if (tgts.Count == 0) { SetStatus("No working target found. Use + TP."); return; }
            double refPx = dir > 0 ? Ask() : Bid();
            int moved = 0; double last = 0;
            foreach (Order t in tgts)
            {
                double cand = t.LimitPrice + (closer ? -dir : dir) * NudgePoints;
                if (closer) cand = dir > 0 ? Math.Max(cand, refPx + Tk) : Math.Min(cand, refPx - Tk);
                cand = Rnd(cand);
                if (Math.Abs(cand - t.LimitPrice) < Tk / 2) continue;
                if (closer && !(dir > 0 ? cand < t.LimitPrice : cand > t.LimitPrice)) continue;
                ApplyLimit(a, t, cand); moved++; last = cand;
            }
            SetStatus(moved > 0 ? "TP " + (up ? "up" : "down") + " (" + (closer ? "closer" : "away") + ") -> " + last.ToString("0.00", CultureInfo.InvariantCulture)
                                : "TP can't move further.");
        }

        private string OcoFor(List<Order> counterpart)
        {
            foreach (Order o in counterpart)
                if (!string.IsNullOrEmpty(o.Oco)) return o.Oco;
            return Guid.NewGuid().ToString("N");
        }

        private void AddStop()
        {
            Account a; Position p; int dir;
            if (!Ctx(out a, out p, out dir, false)) return;
            if (ExitOrders(a, dir, true).Count > 0) { SetStatus("A stop already exists."); return; }
            double refPx = dir > 0 ? Bid() : Ask();
            double price = p.AveragePrice - dir * DefaultStopPoints;
            price = dir > 0 ? Math.Min(price, refPx - Tk) : Math.Max(price, refPx + Tk);
            price = Rnd(price);
            string oco = OcoFor(ExitOrders(a, dir, false));
            OrderAction act = dir > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
            Order o = a.CreateOrder(Instrument, act, OrderType.StopMarket, OrderEntry.Manual, TimeInForce.Gtc,
                                    p.Quantity, 0, price, oco, OrdTag + "SL", NinjaTrader.Core.Globals.MaxDate, null);
            lock (guardLock) { guardPx = price; guardDir = dir; guardReverts = 0; }
            a.Submit(new Order[] { o });
            SetStatus("Stop placed at " + price.ToString("0.00", CultureInfo.InvariantCulture));
        }

        private void AddTarget()
        {
            Account a; Position p; int dir;
            if (!Ctx(out a, out p, out dir, false)) return;
            if (ExitOrders(a, dir, false).Count > 0) { SetStatus("A target already exists."); return; }
            double refPx = dir > 0 ? Ask() : Bid();
            double price = p.AveragePrice + dir * DefaultTargetPoints;
            price = dir > 0 ? Math.Max(price, refPx + Tk) : Math.Min(price, refPx - Tk);
            price = Rnd(price);
            string oco = OcoFor(ExitOrders(a, dir, true));
            OrderAction act = dir > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
            Order o = a.CreateOrder(Instrument, act, OrderType.Limit, OrderEntry.Manual, TimeInForce.Gtc,
                                    p.Quantity, price, 0, oco, OrdTag + "TP", NinjaTrader.Core.Globals.MaxDate, null);
            a.Submit(new Order[] { o });
            SetStatus("Target placed at " + price.ToString("0.00", CultureInfo.InvariantCulture));
        }

        // stop at CURRENT price -/+ N ticks: places one if none exists (ghost / orphan position), else moves the existing stop(s) there
        private void StopFromPrice()
        {
            Account a; Position p; int dir;
            if (!Ctx(out a, out p, out dir, false)) return;
            double refPx = dir > 0 ? Bid() : Ask();
            if (refPx <= 0) { SetStatus("Set SL: no live price yet."); return; }
            double price = Rnd(refPx - dir * Math.Max(1, slTicks) * Tk);
            string ps = price.ToString("0.00", CultureInfo.InvariantCulture);
            List<Order> stops = ExitOrders(a, dir, true);
            if (stops.Count > 0)
            {
                foreach (Order s in stops) ApplyStop(a, s, price);
                SetStatus("Set SL: " + stops.Count + " stop(s) -> " + ps + " (price " + (dir > 0 ? "-" : "+") + slTicks + "t)");
                return;
            }
            string oco = OcoFor(ExitOrders(a, dir, false));
            OrderAction act = dir > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
            Order o = a.CreateOrder(Instrument, act, OrderType.StopMarket, OrderEntry.Manual, TimeInForce.Gtc,
                                    p.Quantity, 0, price, oco, OrdTag + "SL", NinjaTrader.Core.Globals.MaxDate, null);
            lock (guardLock) { guardPx = price; guardDir = dir; guardReverts = 0; }
            a.Submit(new Order[] { o });
            SetStatus("Set SL: stop placed at " + ps + " (price " + (dir > 0 ? "-" : "+") + slTicks + "t)");
        }

        // target at CURRENT price +/- N ticks: places one if none exists, else moves the existing target(s) there
        private void TargetFromPrice()
        {
            Account a; Position p; int dir;
            if (!Ctx(out a, out p, out dir, false)) return;
            double refPx = dir > 0 ? Ask() : Bid();
            if (refPx <= 0) { SetStatus("Set TP: no live price yet."); return; }
            double price = Rnd(refPx + dir * Math.Max(1, tpTicks) * Tk);
            string ps = price.ToString("0.00", CultureInfo.InvariantCulture);
            List<Order> tgts = ExitOrders(a, dir, false);
            if (tgts.Count > 0)
            {
                foreach (Order t in tgts) ApplyLimit(a, t, price);
                SetStatus("Set TP: " + tgts.Count + " target(s) -> " + ps + " (price " + (dir > 0 ? "+" : "-") + tpTicks + "t)");
                return;
            }
            string oco = OcoFor(ExitOrders(a, dir, true));
            OrderAction act = dir > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
            Order o = a.CreateOrder(Instrument, act, OrderType.Limit, OrderEntry.Manual, TimeInForce.Gtc,
                                    p.Quantity, price, 0, oco, OrdTag + "TP", NinjaTrader.Core.Globals.MaxDate, null);
            a.Submit(new Order[] { o });
            SetStatus("Set TP: target placed at " + ps + " (price " + (dir > 0 ? "+" : "-") + tpTicks + "t)");
        }

        // ── semi-auto entry: market / limit entry, then an OCO stop + target from the fill ──
        private static bool EntryDead(Order o)
        {
            return o == null || o.OrderState == OrderState.Filled || o.OrderState == OrderState.Cancelled || o.OrderState == OrderState.Rejected;
        }

        private void DoEntry(int dir, bool limit)
        {
            Account a = acct;
            if (a == null) { SetStatus("No account resolved."); return; }
            if (!armed) { SetStatus("Entries are not armed. Click ARM first."); return; }
            if (SubmitEntry(a, dir, limit, "") && DisarmAfterEntry) armed = false;
        }

        // the entry itself (manual buttons and AUTO TRADE both end up here). Returns true when an order went out.
        private bool SubmitEntry(Account a, int dir, bool limit, string who)
        {
            if (FindPos(a) != null) { SetStatus(who + "Already in a position on " + a.Name + " - entries only open a new trade."); return false; }
            lock (entryLock)
            {
                if (pendActive && !EntryDead(pendEntry)) { SetStatus(who + "A HUD entry order is still working. Cancel it first (Cancel orders)."); return false; }
                double px = 0;
                if (limit)
                {
                    px = dir > 0 ? Bid() - entOff * Tk : Ask() + entOff * Tk;
                    if (px <= 0) { SetStatus(who + "Entry: no live bid / ask yet."); return false; }
                    px = Rnd(px);
                }
                else if (Last() <= 0) { SetStatus(who + "Entry: no live price yet."); return false; }
                OrderAction act = dir > 0 ? OrderAction.Buy : OrderAction.SellShort;
                Order o = a.CreateOrder(Instrument, act, limit ? OrderType.Limit : OrderType.Market, OrderEntry.Manual, limit ? TimeInForce.Gtc : TimeInForce.Day,
                                        Math.Max(1, entQty), px, 0, string.Empty, OrdTag + "Entry", NinjaTrader.Core.Globals.MaxDate, null);
                pendDir = dir; pendSl = Math.Max(1, entSl); pendTp = Math.Max(0, entTp);
                brStop = null; brTgt = null; pendEntry = o; pendAcct = a; pendFillSeen = DateTime.MinValue; pendActive = true;
                EnsureGuardSub(a);
                a.Submit(new Order[] { o });
                SetStatus(who + (dir > 0 ? "BUY " : "SELL ") + Math.Max(1, entQty) + (limit ? " limit @ " + px.ToString("0.00", CultureInfo.InvariantCulture) : " at market")
                          + " on " + a.Name + "  -  bracket: stop " + pendSl + "t" + (pendTp > 0 ? ", target " + pendTp + "t" : ", no target"));
                return true;
            }
        }

        // ── AUTO TRADE: other indicators' (and strategies') plots -> entries ──
        // every plot of every other indicator AND every strategy on this chart (UI thread).
        // Strategies (e.g. SneakyGexRadarStrategy) never show up in ChartControl.Indicators --
        // that collection is indicators only. A strategy that calls AddPlot in its own SetDefaults
        // (NT8 supports "strategy plots", same AddPlot/Plots/Values mechanism as indicators) only
        // shows up in ChartControl.Strategies, so it has to be scanned separately or its plots are
        // invisible to this picker no matter what day it is.
        private void ScanPlots()
        {
            List<PlotSrc> found = new List<PlotSrc>();
            try
            {
                if (ChartControl == null) return;
                Dictionary<string, int> seen = new Dictionary<string, int>();

                if (ChartControl.Indicators != null)
                {
                    foreach (NinjaTrader.NinjaScript.IndicatorBase ind in ChartControl.Indicators)
                    {
                        // only Plots needs to exist to LIST a source (it's set in SetDefaults); Values can still be null/not-yet-sized
                        // (e.g. the source indicator hasn't reached State.DataLoaded while disconnected over a weekend) - SrcTrue()
                        // already checks Values safely at read time, so it just reads as "not true yet" until the source catches up.
                        if (ind == null || ReferenceEquals(ind, this) || ind.Plots == null) continue;
                        string tn = ind.GetType().Name;
                        int inst; seen.TryGetValue(tn, out inst); seen[tn] = inst + 1;
                        string who = tn + (inst > 0 ? " #" + (inst + 1) : "");
                        for (int i = 0; i < ind.Plots.Length; i++)
                        {
                            PlotSrc ps = new PlotSrc();
                            string pn = ind.Plots[i] == null || string.IsNullOrEmpty(ind.Plots[i].Name) ? "plot" + i : ind.Plots[i].Name;
                            ps.Ind = ind; ps.Plot = i; ps.Key = who + "|" + pn; ps.Label = who + " : " + pn;
                            found.Add(ps);
                        }
                    }
                }

                if (ChartControl.Strategies != null)
                {
                    foreach (object raw in ChartControl.Strategies)
                    {
                        NinjaTrader.NinjaScript.StrategyBase strat = raw as NinjaTrader.NinjaScript.StrategyBase;
                        if (strat == null || strat.Plots == null) continue;
                        string tn = strat.GetType().Name;
                        int inst; seen.TryGetValue(tn, out inst); seen[tn] = inst + 1;
                        string who = tn + (inst > 0 ? " #" + (inst + 1) : "");
                        for (int i = 0; i < strat.Plots.Length; i++)
                        {
                            PlotSrc ps = new PlotSrc();
                            string pn = strat.Plots[i] == null || string.IsNullOrEmpty(strat.Plots[i].Name) ? "plot" + i : strat.Plots[i].Name;
                            ps.Strat = strat; ps.Plot = i; ps.Key = who + "|" + pn; ps.Label = who + " (strategy) : " + pn;
                            found.Add(ps);
                        }
                    }
                }
            }
            catch { }
            plotSrcs = found;
        }
        private PlotSrc FindSrc(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (PlotSrc ps in plotSrcs) if (ps.Key == key) return ps;
            return null;
        }
        private void FillCombos()
        {
            if (cbLong[0] == null || cbShort[0] == null) return;
            fillingCombos = true;
            try
            {
                ScanPlots();
                for (int i = 0; i < MaxAutoSlots; i++)
                {
                    srcLong[i] = FindSrc(GetSourceProp(true, i));
                    srcShort[i] = FindSrc(GetSourceProp(false, i));
                }
                for (int side = 0; side < 2; side++)
                {
                    bool isLong = side == 0;
                    ComboBox[] cbs = isLong ? cbLong : cbShort;
                    PlotSrc[] cur = isLong ? srcLong : srcShort;
                    for (int c = 0; c < MaxAutoSlots; c++)
                    {
                        if (cbs[c] == null) continue;   // row not built yet (slot not added)
                        cbs[c].Items.Clear();
                        cbs[c].Items.Add("(none)");
                        int sel = 0;
                        for (int i = 0; i < plotSrcs.Count; i++) { cbs[c].Items.Add(plotSrcs[i].Label); if (cur[c] != null && plotSrcs[i].Key == cur[c].Key) sel = i + 1; }
                        cbs[c].SelectedIndex = sel;
                    }
                }
            }
            catch { }
            fillingCombos = false;
        }
        private void BuildAutoRow(bool isLong, int slot)
        {
            StackPanel rowPanel = new StackPanel();
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock l = Lbl(11, isLong ? Green : Red, FontWeights.SemiBold);
            l.Text = isLong ? "Long" : "Short";
            l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(3, 0, 0, 0);
            Grid.SetColumn(l, 0); g.Children.Add(l);
            ComboBox cb = new ComboBox();
            cb.Height = 24; cb.FontSize = 10.5; cb.Margin = new Thickness(1.5);
            cb.ToolTip = "The plot that gives this " + (isLong ? "LONG" : "SHORT") + " signal. Lists every plot of every other indicator (and strategy) on this chart. Opening the list rescans the chart.";
            int slotCap = slot;
            cb.DropDownOpened += delegate { FillCombos(); };
            cb.SelectionChanged += delegate { ComboPicked(isLong, slotCap); };
            Grid.SetColumn(cb, 1); g.Children.Add(cb);
            rowPanel.Children.Add(g);

            Grid g2 = new Grid();
            g2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
            g2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
            TextBlock w = Lbl(10, FgDim, FontWeights.Normal); w.Text = "when"; w.VerticalAlignment = VerticalAlignment.Center; w.Margin = new Thickness(3, 0, 0, 0);
            Grid.SetColumn(w, 0); g2.Children.Add(w);
            Button rb = MkBtn("above", "How the plot is compared with the value on the right: above it, below it, equals it, or 'not zero' (any non-zero value, the number is not used). A signal fires on the bar where this BECOMES true. Click to change.",
                delegate { if (isLong) { ruleLong[slotCap] = RuleNext[ruleLong[slotCap]]; SetRuleProp(true, slotCap, ruleLong[slotCap]); } else { ruleShort[slotCap] = RuleNext[ruleShort[slotCap]]; SetRuleProp(false, slotCap, ruleShort[slotCap]); } AutoRuleChanged(); }, 0);
            Grid.SetColumn(rb, 1); g2.Children.Add(rb);
            string vt = "The value the plot is compared with, e.g. equals +1 for longs and equals -1 for shorts. Click = 1, Shift-click = 0.1. Any exact number can be typed in the indicator settings (6. Auto trade).";
            Button vm = MkBtn("−", vt, delegate { AutoValStep(isLong, slotCap, -1); }, 0);
            Grid.SetColumn(vm, 2); g2.Children.Add(vm);
            TextBlock vl = Lbl(12, FgMain, FontWeights.SemiBold); vl.HorizontalAlignment = HorizontalAlignment.Center; vl.VerticalAlignment = VerticalAlignment.Center; vl.ToolTip = vt;
            Grid.SetColumn(vl, 3); g2.Children.Add(vl);
            Button vp = MkBtn("+", vt, delegate { AutoValStep(isLong, slotCap, 1); }, 0);
            Grid.SetColumn(vp, 4); g2.Children.Add(vp);
            rowPanel.Children.Add(g2);

            if (isLong) { cbLong[slot] = cb; btnRuleL[slot] = rb; lblValL[slot] = vl; } else { cbShort[slot] = cb; btnRuleS[slot] = rb; lblValS[slot] = vl; }
            (isLong ? longRows : shortRows).Children.Add(rowPanel);
        }

        private void AddAutoSlot(bool isLong)
        {
            int count = isLong ? longSlotCount : shortSlotCount;
            if (count >= MaxAutoSlots) return;
            BuildAutoRow(isLong, count);
            count++;
            if (isLong) { longSlotCount = count; AutoLongSlotCount = count; } else { shortSlotCount = count; AutoShortSlotCount = count; }
            UpdateUi();
        }

        private void RemoveAutoSlot(bool isLong)
        {
            int count = isLong ? longSlotCount : shortSlotCount;
            if (count <= 1) return;
            int slot = count - 1;
            bool hadSrc = isLong ? srcLong[slot] != null : srcShort[slot] != null;
            if (isLong)
            {
                srcLong[slot] = null; ruleLong[slot] = 0; valLong[slot] = 0;
                SetSourceProp(true, slot, ""); SetRuleProp(true, slot, 0); SetValueProp(true, slot, 0);
                cbLong[slot] = null; btnRuleL[slot] = null; lblValL[slot] = null;
            }
            else
            {
                srcShort[slot] = null; ruleShort[slot] = 0; valShort[slot] = 0;
                SetSourceProp(false, slot, ""); SetRuleProp(false, slot, 0); SetValueProp(false, slot, 0);
                cbShort[slot] = null; btnRuleS[slot] = null; lblValS[slot] = null;
            }
            StackPanel panel = isLong ? longRows : shortRows;
            if (panel.Children.Count > 0) panel.Children.RemoveAt(panel.Children.Count - 1);
            count--;
            if (isLong) { longSlotCount = count; AutoLongSlotCount = count; } else { shortSlotCount = count; AutoShortSlotCount = count; }
            if (hadSrc && autoOn) { autoOn = false; SetStatus("AUTO TRADE switched OFF because a signal condition was removed."); }
            UpdateUi();
        }

        private void ComboPicked(bool isLong, int slot)
        {
            if (fillingCombos) return;
            try
            {
                ComboBox cb = isLong ? cbLong[slot] : cbShort[slot];
                int i = cb.SelectedIndex - 1;
                PlotSrc ps = i >= 0 && i < plotSrcs.Count ? plotSrcs[i] : null;
                string key = ps == null ? "" : ps.Key;
                if (isLong) srcLong[slot] = ps; else srcShort[slot] = ps;
                SetSourceProp(isLong, slot, key);
                if (autoOn) { autoOn = false; SetStatus("AUTO TRADE switched OFF because a signal plot was changed. Switch it on again when ready."); }
            }
            catch { }
        }
        private void AutoRuleChanged()
        {
            if (autoOn) { autoOn = false; SetStatus("AUTO TRADE switched OFF because a signal rule was changed. Switch it on again when ready."); }
            UpdateUi();
        }
        private void AutoValStep(bool isLong, int slot, int sign)
        {
            double st = ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.1 : 1.0) * sign;
            if (isLong) { valLong[slot] = Math.Round(valLong[slot] + st, 4); SetValueProp(true, slot, valLong[slot]); }
            else { valShort[slot] = Math.Round(valShort[slot] + st, 4); SetValueProp(false, slot, valShort[slot]); }
            AutoRuleChanged();
        }
        private string SideDesc(PlotSrc[] src, int[] rule, double[] val)
        {
            List<string> parts = new List<string>();
            for (int i = 0; i < src.Length; i++) if (src[i] != null) parts.Add(src[i].Label + " " + RuleTxt(rule[i], val[i]));
            if (parts.Count == 0) return "-";
            return string.Join(" OR ", parts.ToArray());
        }
        private void ToggleAuto()
        {
            if (autoOn) { autoOn = false; revDir = 0; sigPendActive = false; SetStatus("AUTO TRADE off."); return; }
            Account a = acct;
            if (a == null) { SetStatus("AUTO TRADE: no account resolved."); return; }
            if (!srcLong.Any(x => x != null) && !srcShort.Any(x => x != null)) { SetStatus("AUTO TRADE: pick a LONG and / or SHORT signal plot first."); return; }
            if (State != State.Realtime) { SetStatus("AUTO TRADE: the chart is not live yet."); return; }
            autoAcct = a; autoEvalBar = -1; revDir = 0; closing = false; autoNote = ""; sigPendActive = false;
            autoOn = true;
            SetStatus("AUTO TRADE armed on account " + a.Name + " - all auto entries go to this account until you switch AUTO off.");
            string m = "AUTO TRADE ON  -  account " + a.Name + ", qty " + entQty + ", stop " + entSl + "t, target " + (entTp > 0 ? entTp + "t" : "none")
                     + "  |  long: " + SideDesc(srcLong, ruleLong, valLong) + "  |  short: " + SideDesc(srcShort, ruleShort, valShort);
            SetStatus(m);
        }
        private static bool SrcTrue(PlotSrc ps, int rule, double val, int bar)
        {
            try
            {
                if (ps == null || bar < 0) return false;
                // source is either an Indicator or a Strategy (ScanPlots sets exactly one of these) -- same
                // Plots/Values shape either way, just read from whichever one this source actually is.
                var values = ps.Ind != null ? ps.Ind.Values : (ps.Strat != null ? ps.Strat.Values : null);
                if (values == null || ps.Plot >= values.Length) return false;
                if (!values[ps.Plot].IsValidDataPointAt(bar)) return false;
                double v = values[ps.Plot].GetValueAt(bar);
                if (double.IsNaN(v) || double.IsInfinity(v)) return false;
                return rule == 0 ? v > val : (rule == 1 ? v < val : (rule == 3 ? Math.Abs(v - val) < 1e-7 : v != 0));
            }
            catch { return false; }
        }
        // true when ANY of the side's conditions becomes true on this bar (was not already true the bar before).
        // Each added condition is an independent alternative trigger (OR) -- e.g. GEX Radar's several signal plots
        // firing at different times, any one of which should be enough; they are not meant to all align at once.
        private static bool SideTrue(PlotSrc[] src, int[] rule, double[] val, int bar)
        {
            bool haveSrc = false, nowAny = false, prevAny = false;
            for (int i = 0; i < src.Length; i++)
            {
                if (src[i] == null) continue;
                haveSrc = true;
                nowAny |= SrcTrue(src[i], rule[i], val[i], bar);
                prevAny |= SrcTrue(src[i], rule[i], val[i], bar - 1);
            }
            if (!haveSrc) return false;
            return nowAny && !prevAny;
        }
        // read the chosen plots for the bar that just finished (data thread, live only)
        private void AutoEvaluate(int bar)
        {
            if (!autoOn) return;
            Account a = autoAcct;
            if (a == null) return;

            // "Require break of signal bar": a signal that's waiting for a LATER bar to break its own signal bar's high/low.
            // Checked every bar it stays pending, even bars with no fresh signal of their own.
            if (sigPendActive)
            {
                if (bar - sigPendBar > AutoBreakMaxBars) { sigPendActive = false; AutoSay("pending " + (sigPendDir > 0 ? "LONG" : "SHORT") + " signal expired - no break within " + AutoBreakMaxBars + " bars"); }
                else
                {
                    bool brokeOut = sigPendDir > 0 ? Bars.GetHigh(bar) > sigPendHigh : Bars.GetLow(bar) < sigPendLow;
                    if (brokeOut)
                    {
                        int d = sigPendDir; sigPendActive = false;   // one shot: resolved either way
                        if (!AutoRequireConsecutiveBars || ConsecutiveBarsOkAuto(d, bar)) HandleAutoSignal(a, d);
                        else AutoSay((d > 0 ? "LONG" : "SHORT") + " break confirmed but consecutive-bar filter not met - skipped");
                    }
                }
            }

            // fires only on the bar where the condition BECOMES true, so a plot that stays at 1 does not re-enter every bar
            bool L = SideTrue(srcLong, ruleLong, valLong, bar);
            bool S = SideTrue(srcShort, ruleShort, valShort, bar);
            if (AutoTradeSide == SneakyHudAutoSide.LongOnly) S = false;
            else if (AutoTradeSide == SneakyHudAutoSide.ShortOnly) L = false;
            if (!L && !S) return;
            if (L && S) { AutoSay("long AND short signal on the same bar - ignored"); return; }
            int dir = L ? 1 : -1;

            if (AutoRequireBreakOfSignalBar)
            {
                // hold the signal; only act once a later bar actually breaks this signal bar's high/low (checked above)
                sigPendActive = true; sigPendDir = dir; sigPendHigh = Bars.GetHigh(bar); sigPendLow = Bars.GetLow(bar); sigPendBar = bar;
                AutoSay((dir > 0 ? "LONG" : "SHORT") + " signal - waiting for a break of the signal bar's " + (dir > 0 ? "high" : "low") + " before entering");
                return;
            }
            if (AutoRequireConsecutiveBars && !ConsecutiveBarsOkAuto(dir, bar)) { AutoSay((dir > 0 ? "LONG" : "SHORT") + " signal skipped - consecutive-bar filter not met"); return; }
            HandleAutoSignal(a, dir);
        }
        // true when the given absolute bar index, plus the (AutoConsecutiveBarsCount - 1) bars immediately before it, are
        // all the same color as the trade direction: Close > Open ("up bar") for a long, Close < Open ("down bar") for a short.
        private bool ConsecutiveBarsOkAuto(int dir, int bar)
        {
            int n = AutoConsecutiveBarsCount;
            if (bar < n - 1) return false;
            for (int k = 0; k < n; k++)
            {
                int b = bar - k;
                double o = Bars.GetOpen(b), c = Bars.GetClose(b);
                if (dir > 0) { if (!(c > o)) return false; }
                else { if (!(c < o)) return false; }
            }
            return true;
        }
        // The trading day a timestamp belongs to. The CME session reopens at AutoSessionResetHHmm (18:00 by
        // default), so anything at or after that time counts toward the NEXT calendar day's session.
        private DateTime SessionDayOf(DateTime now)
        {
            int hm = now.Hour * 100 + now.Minute;
            return hm >= AutoSessionResetHHmm ? now.Date.AddDays(1) : now.Date;
        }
        // Clears every account's count when the session rolls. Safe to call from either thread.
        private void RollSessionIfNeeded(DateTime now)
        {
            DateTime sd = SessionDayOf(now);
            lock (tradeCntLock)
            {
                if (sd == autoSession) return;
                autoSession = sd; autoTradesAcct.Clear(); autoDay = sd;
            }
        }
        private int TradesOn(Account a)
        {
            if (a == null) return 0;
            lock (tradeCntLock) { int n; return autoTradesAcct.TryGetValue(a.Name, out n) ? n : 0; }
        }
        private int BumpTrades(Account a)
        {
            if (a == null) return 0;
            lock (tradeCntLock)
            {
                int n; autoTradesAcct.TryGetValue(a.Name, out n); n++; autoTradesAcct[a.Name] = n; return n;
            }
        }

        // acts on a (possibly deferred) LONG/SHORT signal: reverse/close on an opposite open trade, or enter flat
        private void HandleAutoSignal(Account a, int dir)
        {
            string sig = (dir > 0 ? "LONG" : "SHORT") + " signal";
            DateTime now = DateTime.Now;
            RollSessionIfNeeded(now);
            Position p = FindPos(a);
            if (p != null)
            {
                if (Dir(p) == dir) { AutoSay(sig + " - already " + (dir > 0 ? "long" : "short") + ", nothing to do"); return; }
                if (oppAction == 0) { AutoSay(sig + " against the open trade - ignored (On opposite: IGNORE)"); return; }
                AutoSay(sig + " against the open trade - " + (oppAction == 2 ? "reversing" : "closing"));
                closing = true; closeSent = false; closeStart = DateTime.UtcNow;
                if (oppAction == 2 && AutoMayEnter(a, now, sig)) { revDir = dir; revUntil = DateTime.UtcNow.AddSeconds(10); }
                CancelHudExits(a);
                return;
            }
            if (!AutoMayEnter(a, now, sig)) return;
            if (SubmitEntry(a, dir, false, "AUTO " + sig + ": ")) { int n = BumpTrades(a); AutoSay(sig + " -> entered on " + a.Name + " (" + n + (AutoMaxTradesPerDay > 0 ? "/" + AutoMaxTradesPerDay : "") + " this session)"); }
        }
        private bool AutoMayEnter(Account a, DateTime now, string sig)
        {
            RollSessionIfNeeded(now);
            if (AutoMaxTradesPerDay > 0 && TradesOn(a) >= AutoMaxTradesPerDay) { AutoSay(sig + " skipped - " + a.Name + " hit max trades per session (" + AutoMaxTradesPerDay + ")"); return false; }
            if (AutoStartHHmm != AutoEndHHmm)
            {
                int hm = now.Hour * 100 + now.Minute;
                bool inside = AutoStartHHmm < AutoEndHHmm ? (hm >= AutoStartHHmm && hm < AutoEndHHmm) : (hm >= AutoStartHHmm || hm < AutoEndHHmm);
                if (!inside) { AutoSay(sig + " skipped - outside the time window"); return false; }
            }
            if (AutoDailyLossLimit > 0)
            {
                try
                {
                    double real = a.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
                    if (real <= -AutoDailyLossLimit) { AutoSay(sig + " skipped - account daily loss limit reached"); return false; }
                }
                catch { }
            }
            return true;
        }
        private void AutoSay(string m)
        {
            autoNote = DateTime.Now.ToString("HH:mm:ss") + "  " + m;
            SetStatus("AUTO: " + m);
        }
        private List<Order> HudExits(Account a)
        {
            List<Order> l = new List<Order>();
            foreach (Order o in LiveOrders(a)) if (o.Name == OrdTag + "SL" || o.Name == OrdTag + "TP") l.Add(o);
            return l;
        }
        private void CancelHudExits(Account a)
        {
            List<Order> l = HudExits(a);
            lock (guardLock) { guardPx = double.NaN; }
            if (l.Count > 0) a.Cancel(l.ToArray());
        }
        // closing for an opposite signal is done in two steps so the bracket stop and the market close can't both fill:
        // 1) cancel the HUD's stop / target  2) once they are gone (or after 1.5 s) close at market. Then the reverse entry, if any.
        private void AutoHousekeeping()
        {
            Account a = autoAcct;
            if (a == null) return;
            if (closing)
            {
                Position p = FindPos(a);
                if (p == null) closing = false;
                else if (!closeSent && (HudExits(a).Count == 0 || (DateTime.UtcNow - closeStart).TotalSeconds > 1.5))
                {
                    OrderAction act = Dir(p) > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
                    Order o = a.CreateOrder(Instrument, act, OrderType.Market, OrderEntry.Manual, TimeInForce.Day, p.Quantity, 0, 0, string.Empty, OrdTag + "AutoX", NinjaTrader.Core.Globals.MaxDate, null);
                    closeSent = true;
                    a.Submit(new Order[] { o });
                }
                else if ((DateTime.UtcNow - closeStart).TotalSeconds > 10) { closing = false; revDir = 0; AutoSay("close for the opposite signal did not complete - check the position"); }
            }
            if (revDir != 0 && !closing)
            {
                if (!autoOn || DateTime.UtcNow > revUntil) { revDir = 0; }
                else if (FindPos(a) == null && !pendActive)
                {
                    int d = revDir; revDir = 0;
                    if (SubmitEntry(a, d, false, "AUTO reverse: ")) { BumpTrades(a); AutoSay("reversed to " + (d > 0 ? "LONG" : "SHORT") + " on " + a.Name); }
                }
            }
        }

        // keeps the bracket in step with what the entry has filled. Called from the account's order events (immediately on a fill)
        // and from the UI timer (safety net). Uses its own order references, so two callers can't both place a bracket.
        private void EntryReconcile()
        {
            Account a = pendAcct;
            if (!pendActive || a == null) return;
            lock (entryLock)
            {
                if (!pendActive) return;
                Order pe = pendEntry;
                Position p = FindPos(a);
                int dir = Dir(p);
                if (p == null || dir != pendDir)
                {
                    // nothing filled (yet), or the trade is already over
                    if (!EntryDead(pe)) return;                                   // entry still working: keep waiting
                    if (pe != null && pe.OrderState == OrderState.Filled && brStop == null)
                    {
                        // the fill can be reported a moment before the position shows up on the account: give it 5 seconds
                        if (pendFillSeen == DateTime.MinValue) pendFillSeen = DateTime.UtcNow;
                        if ((DateTime.UtcNow - pendFillSeen).TotalSeconds < 5) return;
                        SetStatus("Entry filled but no position showed up on " + a.Name + " - NO bracket was placed. Check the account.");
                    }
                    else if (pe != null && pe.OrderState == OrderState.Rejected) SetStatus("Entry order was REJECTED.");
                    pendActive = false; pendEntry = null;
                    return;
                }
                int qty = p.Quantity;
                if (brStop == null)
                {
                    double avg = p.AveragePrice;
                    double stop = avg - dir * pendSl * Tk, mk = dir > 0 ? Bid() : Ask();
                    if (mk > 0) stop = dir > 0 ? Math.Min(stop, mk - Tk) : Math.Max(stop, mk + Tk);      // never on the wrong side of the market
                    stop = Rnd(stop);
                    string oco = Guid.NewGuid().ToString("N");
                    OrderAction act = dir > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
                    List<Order> send = new List<Order>();
                    brStop = a.CreateOrder(Instrument, act, OrderType.StopMarket, OrderEntry.Manual, TimeInForce.Gtc, qty, 0, stop, oco, OrdTag + "SL", NinjaTrader.Core.Globals.MaxDate, null);
                    send.Add(brStop);
                    string tpTxt = "no target";
                    if (pendTp > 0)
                    {
                        double tgt = avg + dir * pendTp * Tk, mk2 = dir > 0 ? Ask() : Bid();
                        if (mk2 > 0) tgt = dir > 0 ? Math.Max(tgt, mk2 + Tk) : Math.Min(tgt, mk2 - Tk);
                        tgt = Rnd(tgt);
                        brTgt = a.CreateOrder(Instrument, act, OrderType.Limit, OrderEntry.Manual, TimeInForce.Gtc, qty, tgt, 0, oco, OrdTag + "TP", NinjaTrader.Core.Globals.MaxDate, null);
                        send.Add(brTgt);
                        tpTxt = "target " + tgt.ToString("0.00", CultureInfo.InvariantCulture);
                    }
                    lock (guardLock) { guardPx = stop; guardDir = dir; guardReverts = 0; }
                    bracketAt = DateTime.UtcNow;
                    a.Submit(send.ToArray());
                    SetStatus("Filled " + qty + " @ " + avg.ToString("0.00", CultureInfo.InvariantCulture) + "  -  bracket placed: stop " + stop.ToString("0.00", CultureInfo.InvariantCulture) + ", " + tpTxt);
                }
                else
                {
                    // a later partial fill: grow the bracket to the position size
                    List<Order> ch = new List<Order>();
                    if (IsLive(brStop) && brStop.Quantity != qty) { brStop.QuantityChanged = qty; ch.Add(brStop); }
                    if (brTgt != null && IsLive(brTgt) && brTgt.Quantity != qty) { brTgt.QuantityChanged = qty; ch.Add(brTgt); }
                    if (ch.Count > 0) { a.Change(ch.ToArray()); SetStatus("Bracket resized to " + qty + "."); }
                }
                if (EntryDead(pe) && brStop.Quantity == qty) { pendActive = false; pendEntry = null; }      // fully filled and covered: done
            }
        }

        private void ClosePartial(bool half)
        {
            Account a; Position p; int dir;
            if (!Ctx(out a, out p, out dir, false)) return;
            int qty = half ? p.Quantity / 2 : 1;
            if (qty < 1 || qty >= p.Quantity) { SetStatus("Position too small for that partial close."); return; }
            int remaining = p.Quantity - qty;
            OrderAction act = dir > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
            Order o = a.CreateOrder(Instrument, act, OrderType.Market, OrderEntry.Manual, TimeInForce.Day,
                                    qty, 0, 0, string.Empty, OrdTag + "Part", NinjaTrader.Core.Globals.MaxDate, null);
            a.Submit(new Order[] { o });
            // keep protective orders from being larger than what's left
            List<Order> resize = new List<Order>();
            foreach (Order x in ExitOrders(a, dir, true).Concat(ExitOrders(a, dir, false)))
                if (x.Quantity > remaining) { x.QuantityChanged = remaining; resize.Add(x); }
            if (resize.Count > 0) a.Change(resize.ToArray());
            SetStatus("Closed " + qty + ", " + remaining + " left.");
        }

        private void DoFlatten()
        {
            Account a = acct;
            if (a == null) { SetStatus("No account resolved."); return; }
            autoBE = false; autoTrail = false;
            if (!flatKeep)
            {
                // NinjaTrader's own flatten: it also DISABLES every strategy running on this account + instrument
                a.Flatten(new List<Instrument> { Instrument });
                SetStatus("Flatten sent on " + a.Name + ". NinjaTrader switches strategies on it OFF when it flattens - set 'Flatten:' to 'close only' to keep them on.");
                return;
            }
            // close only: cancel this instrument's working orders, then market-close. Strategies are left enabled.
            List<Order> live = LiveOrders(a);
            if (live.Count > 0) a.Cancel(live.ToArray());
            lock (guardLock) { guardPx = double.NaN; }
            Position p = FindPos(a);
            if (p == null) { SetStatus(live.Count > 0 ? "No position. Cancelled " + live.Count + " order(s)." : "Nothing to flatten on " + a.Name + "."); return; }
            int dir = Dir(p);
            OrderAction act = dir > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
            Order o = a.CreateOrder(Instrument, act, OrderType.Market, OrderEntry.Manual, TimeInForce.Day,
                                    p.Quantity, 0, 0, string.Empty, OrdTag + "Flat", NinjaTrader.Core.Globals.MaxDate, null);
            a.Submit(new Order[] { o });
            SetStatus("Closed " + p.Quantity + " at market, strategies left ON. A strategy that was in this trade still thinks it is - its next exit can open a new position. Restart it if unsure.");
        }

        private void DoReverse()
        {
            Account a; Position p; int dir;
            if (!Ctx(out a, out p, out dir, false)) return;
            if (ConfirmReverse)
            {
                if (MessageBox.Show("Reverse " + (dir > 0 ? "LONG" : "SHORT") + " " + p.Quantity + " " + Instrument.MasterInstrument.Name
                    + " on " + a.Name + "?\n\nWorking orders on this instrument will be cancelled first.",
                    "Reverse position", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            List<Order> live = LiveOrders(a);
            if (live.Count > 0) a.Cancel(live.ToArray());
            OrderAction act = dir > 0 ? OrderAction.SellShort : OrderAction.Buy;
            Order o = a.CreateOrder(Instrument, act, OrderType.Market, OrderEntry.Manual, TimeInForce.Day,
                                    p.Quantity * 2, 0, 0, string.Empty, OrdTag + "Rev", NinjaTrader.Core.Globals.MaxDate, null);
            a.Submit(new Order[] { o });
            SetStatus("Reversing to " + (dir > 0 ? "SHORT" : "LONG") + " " + p.Quantity + ".");
        }

        private void CancelAllHere()
        {
            Account a = acct;
            if (a == null) { SetStatus("No account resolved."); return; }
            List<Order> live = LiveOrders(a);
            if (live.Count == 0) { SetStatus("No working orders on this instrument."); return; }
            a.Cancel(live.ToArray());
            SetStatus("Cancelled " + live.Count + " order(s).");
        }

        // ── auto BE / auto trail (market-data thread, throttled) ──
        private void RunAutos()
        {
            int now = Environment.TickCount;
            if (now - lastAuto < 300) return;
            lastAuto = now;
            if (closing || revDir != 0) { try { AutoHousekeeping(); } catch { } }
            Account a = acct;
            if (a == null) return;
            Position p = FindPos(a);
            TrackExtreme(p, Dir(p));
            if (p == null) { beDone = false; return; }
            if (!autoBE && !autoTrail) return;
            int dir = Dir(p);
            double profit = (Last() - p.AveragePrice) * dir;
            double trg; string terr;
            if (autoTrail && profit >= AutoTrailStartPoints)
            {
                if (TrailTarget(p, dir, out trg, out terr)) MoveStopsTo(a, dir, trg, true, "Trail");
            }
            else if (autoBE && !beDone && profit >= AutoBeTriggerPoints)
            {
                double be = Rnd(p.AveragePrice + dir * beTicks * Tk);
                if (MoveStopsTo(a, dir, be, true, "BE") > 0) { beDone = true; SetStatus("Auto BE: stop moved to break-even."); }
            }
        }

        // HUD-placed stops / targets ("+ SL", "+ TP", "Set SL", "Set TP") belong to no strategy, so nothing cancels them when the
        // position is closed some other way (strategy time stop, manual close...). Left working they would OPEN a new position
        // when hit. Once an account has been flat on this instrument for ~0.7 s, cancel the HUD's own leftover orders on it.
        private readonly Dictionary<string, DateTime> orphanSeen = new Dictionary<string, DateTime>();
        private void CleanupOrphans()
        {
            if (!CancelHudOrdersWhenFlat) return;
            try
            {
                List<Account> accts = new List<Account>();
                lock (Account.All) { foreach (Account x in Account.All) if (x != null) accts.Add(x); }
                foreach (Account x in accts)
                {
                    List<Order> mine = new List<Order>();
                    if (FindPos(x) == null && (DateTime.UtcNow - bracketAt).TotalSeconds > 3)      // a just-placed bracket can show up before its position does
                        foreach (Order o in LiveOrders(x))
                            if (o.Name == OrdTag + "SL" || o.Name == OrdTag + "TP") mine.Add(o);
                    if (mine.Count == 0) { orphanSeen.Remove(x.Name); continue; }
                    DateTime first;
                    if (!orphanSeen.TryGetValue(x.Name, out first)) { orphanSeen[x.Name] = DateTime.UtcNow; continue; }
                    if ((DateTime.UtcNow - first).TotalMilliseconds < 700) continue;
                    orphanSeen.Remove(x.Name);
                    x.Cancel(mine.ToArray());
                    SetStatus("Position is flat on " + x.Name + ": cancelled " + mine.Count + " leftover HUD order(s) so they can't open a new position.");
                }
            }
            catch { }
        }

        // ═════════════════ UI ═════════════════
        private Button MkBtn(string text, string tip, RoutedEventHandler h, double width)
        {
            Button b = new Button();
            b.Content = text; b.ToolTip = tip; if (width > 0) b.Width = width; b.Height = 24; b.MinWidth = 0; b.MinHeight = 0; b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Margin = new Thickness(1.5);
            b.Padding = new Thickness(1, 0, 1, 0);
            b.FontSize = 11; b.Foreground = FgMain; b.Background = BgBtn; b.BorderBrush = BdBtn;
            b.Cursor = Cursors.Hand;
            b.Click += delegate (object s, RoutedEventArgs e)
            {
                try { h(s, e); }
                catch (Exception ex) { SetStatus("Error: " + ex.Message); }
            };
            return b;
        }

        private static TextBlock Lbl(double size, Brush fg, FontWeight w)
        {
            TextBlock t = new TextBlock();
            t.FontSize = size; t.Foreground = fg; t.FontWeight = w;
            t.Margin = new Thickness(0, 1, 0, 1);
            return t;
        }

        private static TextBlock Section(string text)
        {
            TextBlock t = Lbl(10, FgDim, FontWeights.SemiBold);
            t.Text = text; t.Margin = new Thickness(2, 6, 0, 0);
            return t;
        }

        private System.Windows.Controls.Primitives.UniformGrid Row(int cols)
        {
            System.Windows.Controls.Primitives.UniformGrid g = new System.Windows.Controls.Primitives.UniformGrid();
            g.Columns = cols; return g;
        }

        // label | - | value | + | optional action button.  Shift-click - / + steps 5.
        private Grid Stepper(string label, string tip, Func<int> get, Action<int> set, int min, int max, out TextBlock val, Button action)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(action == null ? 0 : 64) });
            TextBlock l = Lbl(11, FgDim, FontWeights.Normal);
            l.Text = label; l.ToolTip = tip; l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(3, 0, 0, 0);
            Grid.SetColumn(l, 0); g.Children.Add(l);
            Button minus = MkBtn("\u2212", tip + "  (Shift-click = 5)", delegate { int st = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5 : 1; set(Math.Max(min, get() - st)); UpdateUi(); }, 0);
            Grid.SetColumn(minus, 1); g.Children.Add(minus);
            TextBlock v = Lbl(12, FgMain, FontWeights.SemiBold);
            v.HorizontalAlignment = HorizontalAlignment.Center; v.VerticalAlignment = VerticalAlignment.Center; v.ToolTip = tip;
            Grid.SetColumn(v, 2); g.Children.Add(v);
            Button plus = MkBtn("+", tip + "  (Shift-click = 5)", delegate { int st = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5 : 1; set(Math.Min(max, get() + st)); UpdateUi(); }, 0);
            Grid.SetColumn(plus, 3); g.Children.Add(plus);
            if (action != null) { Grid.SetColumn(action, 4); g.Children.Add(action); }
            val = v;
            return g;
        }

        private void BuildUi()
        {
            try
            {
                if (ChartControl == null || root != null) return;

                root = new Border();
                root.Background = BgPanel; root.BorderBrush = BdBtn; root.BorderThickness = new Thickness(1);
                root.CornerRadius = new CornerRadius(5); root.Padding = new Thickness(6);
                System.Windows.Controls.Panel.SetZIndex(root, 1000);

                StackPanel sp = new StackPanel();
                if (DockInChartTrader) sp.HorizontalAlignment = HorizontalAlignment.Stretch;
                else sp.Width = 250;
                root.Child = sp;

                // header (drag handle)
                Grid head = new Grid();
                head.Background = Brushes.Transparent; head.Cursor = Cursors.SizeAll;
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                TextBlock title = Lbl(12, FgMain, FontWeights.Bold); title.Text = "SNEAKY HUD  " + Instrument.MasterInstrument.Name;
                title.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(title, 0); head.Children.Add(title);
                btnDock = MkBtn("⧉", "Dock in Chart Trader / float on the chart. Click to switch (rebuilds the panel). Same as the 'Dock in Chart Trader' setting in the indicator's properties.", delegate { ToggleDock(); }, 26);
                Grid.SetColumn(btnDock, 1); head.Children.Add(btnDock);
                btnSize = MkBtn("100%", "Shrink / grow the whole panel. Shift-click to go the other direction. (Setting: HUD size)", delegate { CycleSize(1); }, 0);
                Grid.SetColumn(btnSize, 2); head.Children.Add(btnSize);
                btnAcct = MkBtn("Acct: ...", "Click to cycle through each connected account.", delegate { CycleAccount(); }, 0);
                Grid.SetColumn(btnAcct, 3); head.Children.Add(btnAcct);
                btnCollapse = MkBtn("–", "Collapse / expand", delegate { ToggleCollapse(); }, 26);
                Grid.SetColumn(btnCollapse, 4); head.Children.Add(btnCollapse);
                head.MouseLeftButtonDown += OnDragDown;
                head.MouseMove += OnDragMove;
                head.MouseLeftButtonUp += OnDragUp;
                sp.Children.Add(head);

                body = new StackPanel();
                bodyScroll = new ScrollViewer();
                bodyScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                bodyScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                bodyScroll.MaxHeight = Math.Max(150, BodyMaxHeight);
                bodyScroll.Content = body;
                bodyScroll.PreviewMouseWheel += OnBodyWheel;
                sp.Children.Add(bodyScroll);

                lblPos = Lbl(12, FgMain, FontWeights.SemiBold);
                lblOpen = Lbl(22, FgMain, FontWeights.Bold);
                lblDetail = Lbl(11, FgDim, FontWeights.Normal);
                lblSession = Lbl(12, FgMain, FontWeights.SemiBold); lblSession.Margin = new Thickness(0, 6, 0, 0);
                lblOrders = Lbl(11, FgDim, FontWeights.Normal);
                body.Children.Add(lblPos); body.Children.Add(lblOpen); body.Children.Add(lblDetail);
                body.Children.Add(lblSession); body.Children.Add(lblOrders);

                body.Children.Add(Section("TEMPLATE  (all settings below, per saved name)"));
                var rTmpl = Row(2);
                btnTmpl = MkBtn("Tmpl: (none)", "Cycles through your saved templates for this indicator and loads the one shown. Saved under NinjaTrader 8\\SneakyTemplates\\TradeHUD.",
                    delegate { CycleTemplate(); }, 0);
                rTmpl.Children.Add(btnTmpl);
                Button btnTmplSave = MkBtn("Save as...", "Saves every setting on this HUD (entry sizing, auto-trade signal plots, trailing mode, the account picked, etc.) under a name you choose, so it can be reloaded after NinjaTrader restarts.",
                    delegate { PromptSaveTemplate(); }, 0);
                rTmpl.Children.Add(btnTmplSave);
                body.Children.Add(rTmpl);
                var rTmpl2 = Row(1);
                Button btnTmplDelete = MkBtn("Delete template", "Deletes the template currently shown in \"Tmpl:\".", delegate { DeleteTemplate(); }, 0);
                btnTmplDeleteRef = btnTmplDelete;
                rTmpl2.Children.Add(btnTmplDelete);
                body.Children.Add(rTmpl2);

                if (ShowEntry)
                {
                    body.Children.Add(Section("ENTRY  (bracket: stop + target from the fill)"));
                    var rArm = Row(1);
                    btnArm = MkBtn("ARM entries (off)", "Safety switch. The BUY / SELL buttons below do nothing until this is on. Shows the account the order will go to.", delegate { armed = !armed; UpdateUi(); }, 0);
                    rArm.Children.Add(btnArm);
                    body.Children.Add(rArm);
                    body.Children.Add(Stepper("Qty", "Contracts for the entry.", delegate { return entQty; }, delegate (int x) { entQty = x; }, 1, 200, out lblEntQty, null));
                    body.Children.Add(Stepper("Stop ticks", "Bracket stop: this many ticks from the fill price.", delegate { return entSl; }, delegate (int x) { entSl = x; }, 1, 4000, out lblEntSl, null));
                    body.Children.Add(Stepper("Target ticks", "Bracket target: this many ticks from the fill price. 0 = no target (stop only).", delegate { return entTp; }, delegate (int x) { entTp = x; }, 0, 8000, out lblEntTp, null));
                    body.Children.Add(Stepper("Limit offset", "BUY LMT is placed this many ticks below the bid, SELL LMT this many above the ask. 0 = at the bid / ask.", delegate { return entOff; }, delegate (int x) { entOff = x; }, 0, 2000, out lblEntOff, null));
                    var rE1 = Row(2);
                    Button bBuy = MkBtn("BUY MKT", "Buy at market, then place the bracket from the fill. Needs ARM.", delegate { DoEntry(1, false); }, 0);
                    Button bSell = MkBtn("SELL MKT", "Sell short at market, then place the bracket from the fill. Needs ARM.", delegate { DoEntry(-1, false); }, 0);
                    bBuy.Foreground = Green; bBuy.FontWeight = FontWeights.Bold; bSell.Foreground = Red; bSell.FontWeight = FontWeights.Bold;
                    rE1.Children.Add(bBuy); rE1.Children.Add(bSell);
                    body.Children.Add(rE1);
                    var rE2 = Row(2);
                    Button bBuyL = MkBtn("BUY LMT", "Buy limit at the bid minus the limit offset. The bracket is placed when it fills. Cancel it with 'Cancel orders'. Needs ARM.", delegate { DoEntry(1, true); }, 0);
                    Button bSellL = MkBtn("SELL LMT", "Sell limit at the ask plus the limit offset. The bracket is placed when it fills. Cancel it with 'Cancel orders'. Needs ARM.", delegate { DoEntry(-1, true); }, 0);
                    bBuyL.Foreground = Green; bSellL.Foreground = Red;
                    rE2.Children.Add(bBuyL); rE2.Children.Add(bSellL);
                    body.Children.Add(rE2);
                    lblRisk = Lbl(10, FgDim, FontWeights.Normal); lblRisk.Margin = new Thickness(3, 2, 0, 0);
                    body.Children.Add(lblRisk);
                }

                body.Children.Add(Section("STOP"));
                var r1 = Row(3);
                btnBE = MkBtn("BE", "Move stop to break-even plus the BE offset ticks below. Only when price is past it.", delegate { DoBE(); }, 0);
                r1.Children.Add(btnBE);
                r1.Children.Add(MkBtn("Lock 50%", "Stop to half of the open profit", delegate { DoLock50(); }, 0));
                r1.Children.Add(MkBtn("Trail now", "Move the stop to the level of the trail mode selected below (one shot). Only ever tightens.", delegate { DoTrailNow(); }, 0));
                r1.Children.Add(MkBtn("SL ▲", "Move the stop UP one nudge (long: tighter, short: looser)", delegate { NudgeStop(true); }, 0));
                r1.Children.Add(MkBtn("SL ▼", "Move the stop DOWN one nudge (long: looser, short: tighter)", delegate { NudgeStop(false); }, 0));
                r1.Children.Add(MkBtn("+ SL", "Place a protective stop if none exists", delegate { AddStop(); }, 0));
                body.Children.Add(r1);
                var rTm = Row(1);
                btnTrailMode = MkBtn("Trail: FIXED", "Trail mode used by 'Trail now' and 'Auto Trail'. Click = next mode, Shift-click = previous. FIXED ticks behind price / ATR multiple behind price / BARS under the last N bar lows (over highs) / CHANDELIER ATR multiple behind the best price since entry / EMA / % PROFIT locked / STEP. ATR, BARS and EMA use this chart's bars and update on bar close.",
                    delegate { int d = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? TrailNames.Length - 1 : 1; trailMode = (trailMode + d) % TrailNames.Length; UpdateUi(); }, 0);
                rTm.Children.Add(btnTrailMode);
                body.Children.Add(rTm);
                body.Children.Add(Stepper("Trail value", "The selected trail mode's setting: ticks (FIXED, STEP), ATR multiple (ATR, CHANDELIER), number of bars (BARS), offset ticks from the EMA (EMA), percent of best profit locked (% PROFIT).",
                    delegate { return TrailParam; }, delegate (int x) { TrailParam = x; }, -4000, 4000, out lblTrailV, null));
                body.Children.Add(Stepper("BE offset", "Ticks added to break-even for BE and Auto BE (+ = locks profit, - = leaves room)",
                    delegate { return beTicks; }, delegate (int x) { beTicks = x; beDone = false; }, -200, 400, out lblBeT, null));
                body.Children.Add(Stepper("SL from price", "Stop this many ticks behind the CURRENT price. Places a stop if none exists (ghost trades), else moves the existing stop there.",
                    delegate { return slTicks; }, delegate (int x) { slTicks = x; }, 1, 2000, out lblSlT,
                    MkBtn("Set SL", "Stop at current price minus/plus the ticks shown. Places one if none exists, otherwise moves the existing stop.", delegate { StopFromPrice(); }, 0)));

                body.Children.Add(Section("TARGET"));
                var r2 = Row(3);
                r2.Children.Add(MkBtn("TP ▲", "Move the target UP one nudge (long: further away, short: closer)", delegate { NudgeTarget(true); }, 0));
                r2.Children.Add(MkBtn("TP ▼", "Move the target DOWN one nudge (long: closer, short: further away)", delegate { NudgeTarget(false); }, 0));
                r2.Children.Add(MkBtn("+ TP", "Place a target if none exists", delegate { AddTarget(); }, 0));
                body.Children.Add(r2);
                body.Children.Add(Stepper("TP from price", "Target this many ticks beyond the CURRENT price. Places a target if none exists (ghost trades), else moves the existing target there.",
                    delegate { return tpTicks; }, delegate (int x) { tpTicks = x; }, 1, 2000, out lblTpT,
                    MkBtn("Set TP", "Target at current price plus/minus the ticks shown. Places one if none exists, otherwise moves the existing target.", delegate { TargetFromPrice(); }, 0)));

                body.Children.Add(Section("POSITION"));
                var r3 = Row(3);
                r3.Children.Add(MkBtn("Close 50%", "Market-close half the position", delegate { ClosePartial(true); }, 0));
                r3.Children.Add(MkBtn("Close 1", "Market-close one contract", delegate { ClosePartial(false); }, 0));
                r3.Children.Add(MkBtn("Reverse", "Cancel orders and reverse (asks to confirm)", delegate { DoReverse(); }, 0));
                body.Children.Add(r3);
                var r3b = Row(2);
                r3b.Children.Add(MkBtn("Cancel orders", "Cancel all working orders on this instrument", delegate { CancelAllHere(); }, 0));
                Button flat = MkBtn("FLATTEN", "Close the position and cancel its orders", delegate { DoFlatten(); }, 0);
                flat.Foreground = Red; flat.FontWeight = FontWeights.Bold;
                r3b.Children.Add(flat);
                body.Children.Add(r3b);
                var r3c = Row(1);
                btnFlatMode = MkBtn("Flatten: NT flatten", "How FLATTEN works. NT flatten = NinjaTrader's own flatten, which also DISABLES strategies on this account/instrument. Close only = cancel orders + market close, strategies stay enabled (a strategy that was in the trade will be out of sync).", delegate { flatKeep = !flatKeep; }, 0);
                r3c.Children.Add(btnFlatMode);
                body.Children.Add(r3c);

                if (ShowAutoTrade)
                {
                    body.Children.Add(Section("AUTO TRADE  (signals from other indicators' plots)"));
                    var rAt = Row(1);
                    btnAutoTrade = MkBtn("AUTO TRADE: off", "Master switch. When ON, a signal on any of the plots chosen below enters at market with the ENTRY section's Qty / Stop / Target, on the account shown. Always starts OFF.", delegate { ToggleAuto(); UpdateUi(); }, 0);
                    rAt.Children.Add(btnAutoTrade);
                    body.Children.Add(rAt);

                    longRows = new StackPanel(); body.Children.Add(longRows);
                    for (int slot = 0; slot < longSlotCount; slot++) BuildAutoRow(true, slot);
                    var rAddL = Row(2);
                    btnAddLong = MkBtn("+ Add long condition", "Add another LONG plot/condition. Any one of the long conditions firing is enough to go long (OR) -- e.g. pick several of GEX Radar's signal plots separately.", delegate { AddAutoSlot(true); }, 0);
                    btnRemoveLong = MkBtn("\u2212 Remove last", "Remove the last LONG condition row.", delegate { RemoveAutoSlot(true); }, 0);
                    rAddL.Children.Add(btnAddLong); rAddL.Children.Add(btnRemoveLong);
                    body.Children.Add(rAddL);

                    shortRows = new StackPanel(); body.Children.Add(shortRows);
                    for (int slot = 0; slot < shortSlotCount; slot++) BuildAutoRow(false, slot);
                    var rAddS = Row(2);
                    btnAddShort = MkBtn("+ Add short condition", "Add another SHORT plot/condition. Any one of the short conditions firing is enough to go short (OR).", delegate { AddAutoSlot(false); }, 0);
                    btnRemoveShort = MkBtn("\u2212 Remove last", "Remove the last SHORT condition row.", delegate { RemoveAutoSlot(false); }, 0);
                    rAddS.Children.Add(btnAddShort); rAddS.Children.Add(btnRemoveShort);
                    body.Children.Add(rAddS);

                    var rOp = Row(1);
                    btnOpp = MkBtn("On opposite signal: IGNORE", "What an opposite signal does while a trade is open: IGNORE it, CLOSE the trade, or REVERSE (close, then enter the other way). Click to change.",
                        delegate { oppAction = (oppAction + 1) % 3; AutoOpposite = (SneakyHudOpposite)oppAction; UpdateUi(); }, 0);
                    rOp.Children.Add(btnOpp);
                    body.Children.Add(rOp);

                    var rTs = Row(1);
                    btnTradeSide = MkBtn("Trade side: BOTH", "Which signals AUTO TRADE is allowed to act on. BOTH (default): long and short signals both enter. LONG ONLY / SHORT ONLY: signals on the other side are ignored entirely. Click to change.",
                        delegate { AutoTradeSide = (SneakyHudAutoSide)(((int)AutoTradeSide + 1) % 3); UpdateUi(); }, 0);
                    rTs.Children.Add(btnTradeSide);
                    body.Children.Add(rTs);

                    var rRb = Row(1);
                    btnReqBreak = MkBtn("Require break of signal bar: OFF", "OFF (default): enter right on the signal bar. ON: hold the signal and only enter once a LATER bar's high (long) / low (short) trades beyond the signal bar's own high/low - price has to actually follow through. Dropped if that break doesn't happen within 'Max bars to wait for the break' (indicator properties). Click to toggle.",
                        delegate { AutoRequireBreakOfSignalBar = !AutoRequireBreakOfSignalBar; if (!AutoRequireBreakOfSignalBar) sigPendActive = false; UpdateUi(); }, 0);
                    rRb.Children.Add(btnReqBreak);
                    body.Children.Add(rRb);

                    var rRc = Row(1);
                    btnReqConsec = MkBtn("Require consecutive bars: OFF", "OFF (default): no bar-color requirement. ON: the bar an entry is about to trigger on, plus the N-1 bars before it (N set below), must all be up-closed (long) or down-closed (short) - e.g. 'up bar after up bar'. A doji breaks the run. Click to toggle.",
                        delegate { AutoRequireConsecutiveBars = !AutoRequireConsecutiveBars; UpdateUi(); }, 0);
                    rRc.Children.Add(btnReqConsec);
                    body.Children.Add(rRc);
                    body.Children.Add(Stepper("Consecutive bars needed", "How many bars N (including the entry bar) must all be the same color as the trade direction. Only applies while 'Require consecutive bars' above is ON.",
                        delegate { return AutoConsecutiveBarsCount; }, delegate (int x) { AutoConsecutiveBarsCount = x; }, 1, 20, out lblConsecCount, null));

                    lblAutoInfo = Lbl(10, FgDim, FontWeights.Normal); lblAutoInfo.Margin = new Thickness(3, 2, 0, 0); lblAutoInfo.TextWrapping = TextWrapping.Wrap; if (!DockInChartTrader) lblAutoInfo.Width = 240;
                    body.Children.Add(lblAutoInfo);
                    FillCombos();
                }

                body.Children.Add(Section("AUTO"));
                var r4 = Row(2);
                btnAutoBE = MkBtn("Auto BE: off", "When profit reaches the trigger, move stop to break-even (once per position)", delegate { autoBE = !autoBE; beDone = false; }, 0);
                btnAutoTrail = MkBtn("Auto Trail: off", "Keep ratcheting the stop with the selected trail mode once profit reaches the start distance (settings: Auto Trail starts at). Only ever tightens.", delegate { autoTrail = !autoTrail; }, 0);
                r4.Children.Add(btnAutoBE); r4.Children.Add(btnAutoTrail);
                body.Children.Add(r4);
                var r5 = Row(1);
                btnGuard = MkBtn("Stop guard: ON", "Keeps the stop you set here: if a strategy/ATM moves it back to a looser price the HUD restores it. Click to toggle.", delegate { stopGuard = !stopGuard; if (!stopGuard) lock (guardLock) { guardPx = double.NaN; } }, 0);
                r5.Children.Add(btnGuard);
                body.Children.Add(r5);

                lblStatus = Lbl(10, Amber, FontWeights.Normal);
                lblStatus.Margin = new Thickness(2, 6, 0, 0); lblStatus.TextWrapping = TextWrapping.Wrap; if (!DockInChartTrader) lblStatus.Width = 240;
                body.Children.Add(lblStatus);

                collapsed = StartCollapsed;
                bodyScroll.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                btnCollapse.Content = collapsed ? "+" : "–";

                ApplyScale();
                dockedInChartTrader = DockInChartTrader && TryDockInChartTrader();
                if (!dockedInChartTrader)
                {
                    PositionRoot();
                    UserControlCollection.Add(root);
                    if (DockInChartTrader) SetStatus("Dock in Chart Trader: not found on this chart, floating on chart instead.");
                }
                if (btnDock != null)
                {
                    btnDock.Content = dockedInChartTrader ? "⧉" : "⤢";
                    btnDock.ToolTip = (dockedInChartTrader ? "Docked in Chart Trader. " : "Floating on chart. ") + "Click to switch to " + (dockedInChartTrader ? "floating on the chart" : "docking in Chart Trader") + " (rebuilds the panel). Same as the 'Dock in Chart Trader' setting in the indicator's properties.";
                }

                uiTimer = new DispatcherTimer();
                uiTimer.Interval = TimeSpan.FromMilliseconds(300);
                uiTimer.Tick += delegate { UpdateUi(); };
                uiTimer.Start();
                UpdateUi();
            }
            catch (Exception ex) { try { Print("[SneakyTradeHUD] BuildUi: " + ex.Message); } catch { } }
        }

        private void TearDownUi()
        {
            try
            {
                if (uiTimer != null) { uiTimer.Stop(); uiTimer = null; }
                DropGuardSub();
                if (root != null)
                {
                    if (dockedInChartTrader && chartTraderGrid != null)
                    {
                        if (chartTraderGrid.Children.Contains(root)) chartTraderGrid.Children.Remove(root);
                        if (chartTraderRowDef != null && chartTraderGrid.RowDefinitions.Contains(chartTraderRowDef))
                            chartTraderGrid.RowDefinitions.Remove(chartTraderRowDef);
                    }
                    else if (UserControlCollection.Contains(root))
                    {
                        UserControlCollection.Remove(root);
                    }
                }
                chartTraderGrid = null; chartTraderRowDef = null; dockedInChartTrader = false;
                root = null;
            }
            catch { }
        }

        private void PositionRoot()
        {
            bool left = Corner == SneakyHudCorner.TopLeft || Corner == SneakyHudCorner.BottomLeft;
            bool top = Corner == SneakyHudCorner.TopLeft || Corner == SneakyHudCorner.TopRight;
            root.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            root.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            root.Margin = new Thickness(left ? OffsetX : 0, top ? OffsetY : 0, left ? 0 : OffsetX, top ? 0 : OffsetY);
        }

        private void ToggleCollapse()
        {
            collapsed = !collapsed;
            bodyScroll.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            btnCollapse.Content = collapsed ? "+" : "–";
        }
        // on-chart equivalent of the "Dock in Chart Trader" property: flips it and rebuilds the panel
        // so it actually re-docks (or floats back onto the chart) right away, no recompile needed.
        private void ToggleDock()
        {
            try
            {
                DockInChartTrader = !DockInChartTrader;
                TearDownUi();
                BuildUi();
            }
            catch (Exception ex) { try { Print("[SneakyTradeHUD] ToggleDock: " + ex.Message); } catch { } }
        }

        private void ApplyScale()
        {
            if (root == null) return;
            double k = HudScale > 0 ? HudScale : 1.0;
            root.LayoutTransform = Math.Abs(k - 1.0) < 0.001 ? null : new ScaleTransform(k, k);
            if (btnSize != null) btnSize.Content = Math.Round(k * 100) + "%";
        }
        private void CycleSize(int dir)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) dir = -dir;
            int i = 0; double best = 999;
            for (int n = 0; n < SizeSteps.Length; n++) { double d = Math.Abs(SizeSteps[n] - HudScale); if (d < best) { best = d; i = n; } }
            i = Math.Max(0, Math.Min(SizeSteps.Length - 1, i + dir));
            HudScale = SizeSteps[i];
            ApplyScale();
        }
        // mouse wheel over the HUD scrolls the panel instead of zooming the chart underneath it
        private void OnBodyWheel(object sender, MouseWheelEventArgs e)
        {
            if (bodyScroll == null) return;
            bodyScroll.ScrollToVerticalOffset(bodyScroll.VerticalOffset - e.Delta * 0.5);
            e.Handled = true;
        }

        private void OnDragDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is Button) return;
            if (dockedInChartTrader) return;   // docked in Chart Trader's own layout -- nothing to drag
            dragging = true; dragStart = e.GetPosition(ChartControl);
            dragOffX = OffsetX; dragOffY = OffsetY;
            ((UIElement)sender).CaptureMouse();
        }
        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            Point p = e.GetPosition(ChartControl);
            bool left = Corner == SneakyHudCorner.TopLeft || Corner == SneakyHudCorner.BottomLeft;
            bool top = Corner == SneakyHudCorner.TopLeft || Corner == SneakyHudCorner.TopRight;
            double dx = p.X - dragStart.X, dy = p.Y - dragStart.Y;
            OffsetX = Math.Max(0, dragOffX + (left ? dx : -dx));
            OffsetY = Math.Max(0, dragOffY + (top ? dy : -dy));
            PositionRoot();
        }
        private void OnDragUp(object sender, MouseButtonEventArgs e)
        {
            dragging = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }

        private void CycleAccount()
        {
            List<string> names = new List<string>();
            try
            {
                lock (Account.All)
                {
                    foreach (Account a in Account.All)
                        if (a != null && IsConnected(a)) names.Add(a.Name);
                }
            }
            catch { }
            if (names.Count == 0) { SetStatus("No connected accounts to pick from."); return; }
            int i = names.FindIndex(n => string.Equals(n, overrideName, StringComparison.OrdinalIgnoreCase));
            overrideName = names[(i + 1) % names.Count];
            autoPicked = true;
            acct = FindAcct(overrideName);
            SetStatus("Managing account: " + overrideName);
        }

        // ═════════════════ templates (save / load every setting under a name) ═════════════════
        // Everything the HUD exposes as a [NinjaScriptProperty] already survives a NinjaTrader chart
        // template / workspace save -- but that saves and restores an entire chart at once, which is
        // clunky when you just want to flip one indicator between a couple of named setups. This saves
        // a plain text file per name under NinjaTrader 8\SneakyTemplates\TradeHUD, so re-setting up a
        // chart after restarting NinjaTrader is a two-click "Tmpl:" cycle instead of re-picking every
        // plot, qty, stop/target and account by hand.
        private static string TemplatesDir()
        {
            string d = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SneakyTemplates", "TradeHUD");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        private static string SafeFileName(string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in name) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString().Trim();
        }

        // This HUD manages all its own settings through its own WPF panel rather than NinjaTrader's
        // Properties grid, so none of them actually carry [NinjaScriptProperty] -- that attribute
        // doesn't appear anywhere in this file. The real tell for "this is one of the HUD's own
        // settings" is [Display(...)] (the ones shown in the grid anyway, for completeness/defaults)
        // or [Browsable(false)] (the extra AutoLongSource2..10 / AutoShortSource2..10 slots, hidden
        // from the grid on purpose because there can be up to 10 of them). Filtering on
        // NinjaScriptPropertyAttribute instead matched nothing, so SaveTemplate/LoadTemplate were
        // silently saving and restoring zero properties -- only __AccountOverride ever took effect,
        // which is exactly why cycling templates looked like it did nothing to the AUTO TRADE rows.
        private PropertyInfo[] TemplateProps()
        {
            return GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                            && p.DeclaringType == typeof(SneakyTradeHUD)
                            && (p.GetCustomAttributes(typeof(DisplayAttribute), true).Length > 0
                                || p.GetCustomAttributes(typeof(BrowsableAttribute), true).Any(a => !((BrowsableAttribute)a).Browsable)))
                .ToArray();
        }

        private List<string> ListTemplates()
        {
            List<string> names = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(TemplatesDir(), "*.txt"))
                    names.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private void SaveTemplate(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                string path = Path.Combine(TemplatesDir(), SafeFileName(name) + ".txt");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# SneakyTradeHUD template \"" + name + "\" -- saved " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                sb.AppendLine("__AccountOverride=" + overrideName);
                foreach (PropertyInfo p in TemplateProps())
                {
                    object v;
                    try { v = p.GetValue(this, null); } catch { continue; }
                    string s = v == null ? "" : (v is double dv ? dv.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture));
                    sb.AppendLine(p.Name + "=" + s);
                }
                File.WriteAllText(path, sb.ToString());
                currentTemplateName = name;
                SetStatus("Saved template \"" + name + "\".");
                UpdateUi();
            }
            catch (Exception ex) { SetStatus("Save template failed: " + ex.Message); }
        }

        private void LoadTemplate(string name)
        {
            try
            {
                string path = Path.Combine(TemplatesDir(), SafeFileName(name) + ".txt");
                if (!File.Exists(path)) { SetStatus("Template \"" + name + "\" not found."); return; }
                Dictionary<string, PropertyInfo> props = TemplateProps().ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    if (string.Equals(k, "__AccountOverride", StringComparison.OrdinalIgnoreCase)) { overrideName = v; autoPicked = true; continue; }
                    PropertyInfo p;
                    if (!props.TryGetValue(k, out p)) continue;
                    try
                    {
                        if (p.PropertyType.IsEnum) p.SetValue(this, Enum.Parse(p.PropertyType, v), null);
                        else if (p.PropertyType == typeof(bool)) p.SetValue(this, bool.Parse(v), null);
                        else if (p.PropertyType == typeof(int)) p.SetValue(this, int.Parse(v, CultureInfo.InvariantCulture), null);
                        else if (p.PropertyType == typeof(double)) p.SetValue(this, double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture), null);
                        else p.SetValue(this, v, null);
                    }
                    catch { }
                }
                currentTemplateName = name;
                if (autoOn) { autoOn = false; SetStatus("AUTO TRADE switched OFF -- a template was loaded."); }
                ResyncFromProperties();
                acct = FindAcct(overrideName);
                TearDownUi();
                BuildUi();
                FillCombos();
                UpdateUi();
                SetStatus("Loaded template \"" + name + "\".");
            }
            catch (Exception ex) { SetStatus("Load template failed: " + ex.Message); }
        }

        private void DeleteTemplate()
        {
            if (currentTemplateName.Length == 0) { SetStatus("No template selected to delete."); return; }
            try
            {
                string path = Path.Combine(TemplatesDir(), SafeFileName(currentTemplateName) + ".txt");
                if (File.Exists(path)) File.Delete(path);
                SetStatus("Deleted template \"" + currentTemplateName + "\".");
                currentTemplateName = "";
                UpdateUi();
            }
            catch (Exception ex) { SetStatus("Delete template failed: " + ex.Message); }
        }

        private void CycleTemplate()
        {
            List<string> names = ListTemplates();
            if (names.Count == 0) { SetStatus("No saved templates yet. Click \"Save as...\" to create one."); return; }
            int i = names.FindIndex(n => string.Equals(n, currentTemplateName, StringComparison.OrdinalIgnoreCase));
            string next = names[(i + 1) % names.Count];
            LoadTemplate(next);
        }

        // Small modal text-input dialog -- NinjaScript indicators have no built-in prompt, so this is a
        // minimal hand-built one (OK / Cancel + a single TextBox), used only for naming a template.
        private string PromptText(string title, string label, string initial)
        {
            Window w = new Window();
            w.Title = title; w.Width = 360; w.Height = 150; w.ResizeMode = ResizeMode.NoResize;
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.Topmost = true;
            if (ChartControl != null) { try { w.Owner = Window.GetWindow(ChartControl); } catch { } }
            StackPanel sp = new StackPanel(); sp.Margin = new Thickness(12);
            TextBlock l = new TextBlock(); l.Text = label; l.Margin = new Thickness(0, 0, 0, 6);
            TextBox tb = new TextBox(); tb.Text = initial ?? ""; tb.Margin = new Thickness(0, 0, 0, 10);
            StackPanel btns = new StackPanel(); btns.Orientation = Orientation.Horizontal; btns.HorizontalAlignment = HorizontalAlignment.Right;
            Button ok = new Button(); ok.Content = "OK"; ok.Width = 70; ok.Margin = new Thickness(0, 0, 6, 0); ok.IsDefault = true;
            Button cancel = new Button(); cancel.Content = "Cancel"; cancel.Width = 70; cancel.IsCancel = true;
            bool accepted = false;
            ok.Click += delegate { accepted = true; w.Close(); };
            cancel.Click += delegate { w.Close(); };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            sp.Children.Add(l); sp.Children.Add(tb); sp.Children.Add(btns);
            w.Content = sp;
            tb.Focus(); tb.SelectAll();
            w.ShowDialog();
            return accepted ? tb.Text.Trim() : null;
        }

        private void PromptSaveTemplate()
        {
            string name = PromptText("Save SneakyTradeHUD template", "Template name:", currentTemplateName.Length > 0 ? currentTemplateName : Instrument.MasterInstrument.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            SaveTemplate(name.Trim());
        }

        private static string Money(double v)
        {
            return (v >= 0 ? "+$" : "-$") + Math.Abs(v).ToString("N2", CultureInfo.InvariantCulture);
        }

        private void UpdateUi()
        {
            try
            {
                if (root == null) return;
                // Account selection is explicit (overrideName, set only by clicking "Acct:") -- no more
                // reflection-based re-resolution against Chart Trader every tick. That reflection path
                // used to let the panel show one account while auto entries silently went to another;
                // that's exactly how 10 entries once reached Sim120 while the HUD read "FLAT (Sim100)".
                // One-time exception: if nothing has been picked yet, guess a starting account so the
                // HUD isn't blank on first load.
                if (overrideName.Length == 0 && !autoPicked)
                {
                    autoPicked = true;
                    Account guess = ResolveAccount();
                    if (guess != null) overrideName = guess.Name;
                }
                Account resolvedAcct = FindAcct(overrideName);
                // AutoEvaluate trades autoAcct, which ToggleAuto froze when AUTO was switched on. If the
                // user clicks "Acct:" to a different account while AUTO is still armed, that's reported
                // here instead of silently moving where auto entries go.
                if (autoOn && autoAcct != null)
                {
                    acctDriftNote = ReferenceEquals(resolvedAcct, autoAcct) ? ""
                        : "AUTO is trading " + autoAcct.Name + "  -  Acct is now set to "
                          + (resolvedAcct == null ? "nothing" : resolvedAcct.Name) + ". Switch AUTO off and on to move it.";
                    acct = autoAcct;
                }
                else { acctDriftNote = ""; acct = resolvedAcct; }
                Account a = acct;
                EnsureGuardSub(a);
                CleanupOrphans();
                if (a != null && stopGuard && !double.IsNaN(guardPx))
                {
                    Position gp = FindPos(a);
                    if (gp != null) foreach (Order so in ExitOrders(a, Dir(gp), true)) GuardCheck(a, so);
                }

                btnAcct.Content = "Acct: " + (overrideName.Length == 0 ? "(none)" : (a == null ? overrideName + "?" : a.Name));
                btnAcct.ToolTip = overrideName.Length == 0
                    ? "No connected accounts found. Click to retry, or connect an account and reload the indicator."
                    : "Managing: " + (a == null ? overrideName + " (not connected)" : a.Name) + " (click to cycle)";
                if (btnTmpl != null) btnTmpl.Content = "Tmpl: " + (currentTemplateName.Length == 0 ? "(none)" : currentTemplateName);
                if (btnTmplDeleteRef != null) btnTmplDeleteRef.Content = currentTemplateName.Length == 0 ? "Delete template" : "Delete \"" + currentTemplateName + "\"";
                btnAutoBE.Content = "Auto BE: " + (autoBE ? "ON" : "off");
                string bt = beTicks.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " t";
                if (lblBeT != null) lblBeT.Text = bt;
                if (lblSlT != null) lblSlT.Text = slTicks + " t";
                if (lblTpT != null) lblTpT.Text = tpTicks + " t";
                if (lblTrailV != null) lblTrailV.Text = TrailParamText();
                if (pendActive) EntryReconcile();
                if (btnAutoTrade != null)
                {
                    if (cbLong[0] != null && cbLong[0].Items.Count <= 1 && !fillingCombos) FillCombos();  // other indicators may load after the HUD
                    Account aa = autoAcct;
                    btnAutoTrade.Content = autoOn ? "AUTO TRADING ON  \u2192  " + (aa == null ? "?" : aa.Name) + "   (click to stop)" : "AUTO TRADE: off";
                    btnAutoTrade.Foreground = autoOn ? Red : FgMain; btnAutoTrade.FontWeight = autoOn ? FontWeights.Bold : FontWeights.Normal;
                    for (int slot = 0; slot < longSlotCount; slot++)
                    {
                        if (btnRuleL[slot] == null) continue;
                        btnRuleL[slot].Content = RuleNames[ruleLong[slot]];
                        lblValL[slot].Text = ruleLong[slot] == 2 ? "-" : ValTxt(valLong[slot]);
                    }
                    for (int slot = 0; slot < shortSlotCount; slot++)
                    {
                        if (btnRuleS[slot] == null) continue;
                        btnRuleS[slot].Content = RuleNames[ruleShort[slot]];
                        lblValS[slot].Text = ruleShort[slot] == 2 ? "-" : ValTxt(valShort[slot]);
                    }
                    if (btnRemoveLong != null) btnRemoveLong.Visibility = longSlotCount > 1 ? Visibility.Visible : Visibility.Collapsed;
                    if (btnAddLong != null) btnAddLong.Visibility = longSlotCount < MaxAutoSlots ? Visibility.Visible : Visibility.Collapsed;
                    if (btnRemoveShort != null) btnRemoveShort.Visibility = shortSlotCount > 1 ? Visibility.Visible : Visibility.Collapsed;
                    if (btnAddShort != null) btnAddShort.Visibility = shortSlotCount < MaxAutoSlots ? Visibility.Visible : Visibility.Collapsed;
                    btnOpp.Content = "On opposite signal: " + (oppAction == 0 ? "IGNORE" : (oppAction == 1 ? "CLOSE" : "REVERSE"));
                    if (btnTradeSide != null)
                    {
                        string tsTxt = AutoTradeSide == SneakyHudAutoSide.Both ? "BOTH" : (AutoTradeSide == SneakyHudAutoSide.LongOnly ? "LONG ONLY" : "SHORT ONLY");
                        btnTradeSide.Content = "Trade side: " + tsTxt;
                        btnTradeSide.Foreground = AutoTradeSide == SneakyHudAutoSide.Both ? FgMain : (AutoTradeSide == SneakyHudAutoSide.LongOnly ? Green : Red);
                        btnTradeSide.FontWeight = AutoTradeSide == SneakyHudAutoSide.Both ? FontWeights.Normal : FontWeights.Bold;
                    }
                    if (btnReqBreak != null)
                    {
                        btnReqBreak.Content = "Require break of signal bar: " + (AutoRequireBreakOfSignalBar ? (sigPendActive ? "ON (waiting...)" : "ON") : "OFF");
                        btnReqBreak.Foreground = AutoRequireBreakOfSignalBar ? Amber : FgMain; btnReqBreak.FontWeight = AutoRequireBreakOfSignalBar ? FontWeights.Bold : FontWeights.Normal;
                    }
                    if (btnReqConsec != null)
                    {
                        btnReqConsec.Content = "Require consecutive bars: " + (AutoRequireConsecutiveBars ? "ON" : "OFF");
                        btnReqConsec.Foreground = AutoRequireConsecutiveBars ? Amber : FgMain; btnReqConsec.FontWeight = AutoRequireConsecutiveBars ? FontWeights.Bold : FontWeights.Normal;
                    }
                    if (lblConsecCount != null) lblConsecCount.Text = AutoConsecutiveBarsCount.ToString();
                    Account accUi = acct; int cnt = TradesOn(accUi); string accNm = accUi == null ? "no account" : accUi.Name;
                    string lim = (AutoMaxTradesPerDay > 0 ? "trades this session " + cnt + "/" + AutoMaxTradesPerDay : "trades this session " + cnt) + " on " + accNm
                               + (AutoStartHHmm != AutoEndHHmm ? "   window " + AutoStartHHmm.ToString("0000") + "-" + AutoEndHHmm.ToString("0000") : "");
                    lblAutoInfo.Text = (autoOn ? lim : "off  |  " + lim) + (acctDriftNote.Length > 0 ? "\n" + acctDriftNote : "") + (autoNote.Length > 0 ? "\n" + autoNote : "");
                }
                if (btnArm != null)
                {
                    btnArm.Content = armed ? "ARMED  \u2192  " + (a == null ? "no account" : a.Name) + "   (click to disarm)" : "ARM entries (off)";
                    btnArm.Foreground = armed ? Red : FgMain;
                    btnArm.FontWeight = armed ? FontWeights.Bold : FontWeights.Normal;
                    lblEntQty.Text = entQty.ToString(CultureInfo.InvariantCulture);
                    lblEntSl.Text = entSl + " t"; lblEntTp.Text = entTp == 0 ? "none" : entTp + " t"; lblEntOff.Text = entOff + " t";
                    double tv = Instrument.MasterInstrument.PointValue * Tk * entQty;
                    lblRisk.Text = "Risk " + Money(-entSl * tv).Replace("-", "") + (entTp > 0 ? "   Target " + Money(entTp * tv).Replace("+", "") + "   R:R 1 : " + ((double)entTp / entSl).ToString("0.0", CultureInfo.InvariantCulture) : "   no target")
                                 + "   (" + (entSl * Tk).ToString("0.##", CultureInfo.InvariantCulture) + " pts stop)";
                }
                if (btnTrailMode != null)
                {
                    string lvl = "";
                    try
                    {
                        Position tp0 = a == null ? null : FindPos(a);
                        double tg0; string te0;
                        if (tp0 != null && TrailTarget(tp0, Dir(tp0), out tg0, out te0)) lvl = "  \u2192 " + Rnd(tg0).ToString("0.00", CultureInfo.InvariantCulture);
                    }
                    catch { }
                    btnTrailMode.Content = "Trail: " + TrailNames[trailMode] + lvl;
                }
                if (btnBE != null) btnBE.Content = beTicks == 0 ? "BE" : "BE " + beTicks.ToString("+0;-0", CultureInfo.InvariantCulture) + "t";
                if (btnFlatMode != null)
                {
                    btnFlatMode.Content = flatKeep ? "Flatten: close only (strategies stay ON)" : "Flatten: NT flatten (disables strategies)";
                    btnFlatMode.Foreground = flatKeep ? Green : Amber;
                }
                btnAutoBE.Foreground = autoBE ? Green : FgMain;
                btnAutoTrail.Content = "Auto Trail: " + (autoTrail ? "ON (" + TrailNames[trailMode] + ")" : "off");
                btnAutoTrail.Foreground = autoTrail ? Green : FgMain;
                double gpx = guardPx;
                btnGuard.Content = "Stop guard: " + (stopGuard ? "ON" : "off") + (stopGuard && !double.IsNaN(gpx) ? "  (SL " + gpx.ToString("0.00", CultureInfo.InvariantCulture) + ")" : "");
                btnGuard.Foreground = stopGuard ? Green : FgMain;

                if (a == null)
                {
                    lblPos.Text = "No account"; lblOpen.Text = "—"; lblOpen.Foreground = FgDim;
                    lblDetail.Text = ""; lblSession.Text = ""; lblOrders.Text = "";
                }
                else
                {
                    Position p = FindPos(a);
                    int dir = Dir(p);
                    double px = Last();
                    double pv = Instrument.MasterInstrument.PointValue;
                    List<Order> stops = new List<Order>(), tgts = new List<Order>();
                    double open = 0;

                    if (p == null)
                    {
                        lock (guardLock) { guardPx = double.NaN; }
                        lblPos.Text = "FLAT  (" + a.Name + ")";
                        lblOpen.Text = "$0.00"; lblOpen.Foreground = FgDim;
                        lblDetail.Text = "last " + (px > 0 ? px.ToString("0.00", CultureInfo.InvariantCulture) : "—");
                    }
                    else
                    {
                        double pts = (px - p.AveragePrice) * dir;
                        open = pts * p.Quantity * pv;
                        stops = ExitOrders(a, dir, true); tgts = ExitOrders(a, dir, false);
                        lblPos.Text = (dir > 0 ? "LONG " : "SHORT ") + p.Quantity + " @ " + p.AveragePrice.ToString("0.00", CultureInfo.InvariantCulture) + "  (" + a.Name + ")";
                        lblOpen.Text = Money(open);
                        lblOpen.Foreground = open > 0 ? Green : open < 0 ? Red : FgMain;

                        string r = "";
                        if (stops.Count > 0)
                        {
                            double risk = stops.Max(s => dir > 0 ? p.AveragePrice - s.StopPrice : s.StopPrice - p.AveragePrice);
                            if (risk > Tk / 2) r = "   " + (pts / risk).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "R";
                        }
                        lblDetail.Text = pts.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " pts   "
                            + (pts / Tk).ToString("+0;-0;0", CultureInfo.InvariantCulture) + " ticks" + r;
                    }

                    double real = 0, unreal = 0; bool ok = true;
                    try
                    {
                        real = a.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
                        unreal = a.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
                    }
                    catch { ok = false; }
                    if (ok)
                    {
                        double net = real + unreal;
                        lblSession.Text = "Session " + Money(net) + "\n   real " + Money(real) + "  |  open " + Money(unreal);
                        lblSession.Foreground = net > 0 ? Green : net < 0 ? Red : FgMain;
                    }
                    else { lblSession.Text = "Session: n/a"; lblSession.Foreground = FgDim; }

                    string sl = stops.Count == 0 ? "SL —" : "SL " + string.Join("/", stops.Select(s => s.StopPrice.ToString("0.00", CultureInfo.InvariantCulture)).Distinct());
                    string tp = tgts.Count == 0 ? "TP —" : "TP " + string.Join("/", tgts.Select(t => t.LimitPrice.ToString("0.00", CultureInfo.InvariantCulture)).Distinct());
                    lblOrders.Text = p == null ? "" : sl + "     " + tp;
                }

                if (status.Length > 0 && (DateTime.Now - statusTime).TotalSeconds < 12)
                    lblStatus.Text = statusTime.ToString("HH:mm:ss") + "  " + status;
                else lblStatus.Text = "";
            }
            catch { }
        }

        // ═════════════════ properties ═════════════════
        [Display(Name = "Corner", GroupName = "1. Layout", Order = 0)]
        public SneakyHudCorner Corner { get; set; }

        [Range(0, 3000)]
        [Display(Name = "Offset X (px)", GroupName = "1. Layout", Order = 1)]
        public double OffsetX { get; set; }

        [Range(0, 3000)]
        [Display(Name = "Offset Y (px)", GroupName = "1. Layout", Order = 2)]
        public double OffsetY { get; set; }

        [Display(Name = "Start collapsed", GroupName = "1. Layout", Order = 3)]
        public bool StartCollapsed { get; set; }

        [Range(0.25, 1.5)]
        [Display(Name = "HUD size (0.35 - 1.0, 0.55 = about half)", Description = "Shrinks the whole panel. Can also be cycled with the size button in the HUD header.", GroupName = "1. Layout", Order = 4)]
        public double HudScale { get; set; }

        [Range(150, 3000)]
        [Display(Name = "Visible height before scrolling (px, before scaling)", Description = "The panel's body becomes a scroll area past this height - scroll with the mouse wheel over the HUD.", GroupName = "1. Layout", Order = 5)]
        public double BodyMaxHeight { get; set; }

        [Display(Name = "Dock in Chart Trader (experimental)", Description = "Tries to embed the HUD as an extra row inside NinjaTrader's own Chart Trader panel instead of floating it on the chart. Uses an internal NinjaTrader UI hook that isn't officially documented -- NinjaTrader could change it in a future update with no warning. If it can't be found (Chart Trader isn't open on this chart, or the hook stops working after an update), the HUD automatically falls back to floating on the chart like before, and posts a status note saying so.", GroupName = "1. Layout", Order = 6)]
        public bool DockInChartTrader { get; set; }

        [Display(Name = "Stop guard on by default", Description = "Restores the stop you set with the HUD if a strategy/ATM moves it back to a looser price.", GroupName = "2. Orders", Order = 6)]
        public bool StopGuardDefault { get; set; }

        [Range(0.01, 1000)]
        [Display(Name = "Nudge size (points per click)", GroupName = "2. Orders", Order = 0)]
        public double NudgePoints { get; set; }

        [Range(-1000, 1000)]
        [Display(Name = "Break-even offset at start (points, + = in profit)", Description = "Starting value of the BE offset stepper on the HUD (shown there in ticks).", GroupName = "2. Orders", Order = 1)]
        public double BeOffsetPoints { get; set; }

        [Range(0.01, 1000)]
        [Display(Name = "Trail distance (points)", Description = "FIXED trail mode: starting distance behind price (shown on the HUD in ticks).", GroupName = "2. Orders", Order = 2)]
        public double TrailPoints { get; set; }

        [Range(0.01, 1000)]
        [Display(Name = "Default stop distance for + SL (points)", GroupName = "2. Orders", Order = 3)]
        public double DefaultStopPoints { get; set; }

        [Range(0.01, 1000)]
        [Display(Name = "Default target distance for + TP (points)", GroupName = "2. Orders", Order = 4)]
        public double DefaultTargetPoints { get; set; }

        [Display(Name = "Confirm Reverse", GroupName = "2. Orders", Order = 5)]
        public bool ConfirmReverse { get; set; }

        [Range(1, 2000)]
        [Display(Name = "Set SL: ticks from price at start", Description = "Starting value of the 'SL from price' stepper.", GroupName = "2. Orders", Order = 7)]
        public int SlFromPriceTicks { get; set; }

        [Range(1, 2000)]
        [Display(Name = "Set TP: ticks from price at start", Description = "Starting value of the 'TP from price' stepper.", GroupName = "2. Orders", Order = 8)]
        public int TpFromPriceTicks { get; set; }

        [Display(Name = "Flatten keeps strategies enabled", Description = "OFF = NinjaTrader's own flatten, which disables every strategy on the account/instrument. ON = cancel orders + market close only; strategies stay enabled but one that was in the trade will be out of sync with the account. Can also be switched on the HUD.", GroupName = "2. Orders", Order = 9)]
        public bool FlattenKeepsStrategies { get; set; }

        [Display(Name = "Cancel HUD orders when flat", Description = "Stops / targets placed by the HUD (+ SL, + TP, Set SL, Set TP) are cancelled automatically once the position is closed, so a leftover order can't open a new position. Strategy and ATM orders are never touched.", GroupName = "2. Orders", Order = 10)]
        public bool CancelHudOrdersWhenFlat { get; set; }

        [Display(Name = "Show the ENTRY section", Description = "Semi-auto entries: BUY / SELL buttons that place an OCO stop + target from the fill. Untick to keep the HUD management-only.", GroupName = "5. Entry", Order = 0)]
        public bool ShowEntry { get; set; }

        [Range(1, 200)]
        [Display(Name = "Qty at start", GroupName = "5. Entry", Order = 1)]
        public int EntryQty { get; set; }

        [Range(1, 4000)]
        [Display(Name = "Stop ticks at start", GroupName = "5. Entry", Order = 2)]
        public int EntryStopTicks { get; set; }

        [Range(0, 8000)]
        [Display(Name = "Target ticks at start (0 = none)", GroupName = "5. Entry", Order = 3)]
        public int EntryTargetTicks { get; set; }

        [Display(Name = "Disarm after each entry", Description = "ON: ARM switches itself off after every entry click, so each trade needs a deliberate re-arm.", GroupName = "5. Entry", Order = 4)]
        public bool DisarmAfterEntry { get; set; }

        [Display(Name = "Show the AUTO TRADE section", Description = "Entries from other indicators' plots. Untick to hide it.", GroupName = "6. Auto trade", Order = 0)]
        public bool ShowAutoTrade { get; set; }

        [Range(1, 10)]
        [Display(Name = "Long condition rows shown", Description = "How many LONG rows are shown on the HUD (1-10). Use the +Add / -Remove buttons on the HUD; this just reflects/restores that count.", GroupName = "6. Auto trade", Order = 1)]
        public int AutoLongSlotCount { get; set; }

        [Range(1, 10)]
        [Display(Name = "Short condition rows shown", Description = "How many SHORT rows are shown on the HUD (1-10). Use the +Add / -Remove buttons on the HUD; this just reflects/restores that count.", GroupName = "6. Auto trade", Order = 2)]
        public int AutoShortSlotCount { get; set; }

        [Display(Name = "Long signal plot 1", Description = "Set from the drop-down on the HUD (indicator name | plot name). Any one LONG row firing is enough to go long (OR).", GroupName = "6. Auto trade", Order = 3)]
        public string AutoLongSource1 { get; set; }
        [Range(0, 3)]
        [Display(Name = "Long rule 1 (0 = above, 1 = below, 2 = not zero, 3 = equals)", GroupName = "6. Auto trade", Order = 3)]
        public int AutoLongRule1 { get; set; }
        [Display(Name = "Long value 1", Description = "The number the long plot is compared with (e.g. 1).", GroupName = "6. Auto trade", Order = 3)]
        public double AutoLongValue1 { get; set; }

        [Display(Name = "Short signal plot 1", Description = "Set from the drop-down on the HUD (indicator name | plot name). Any one SHORT row firing is enough to go short (OR).", GroupName = "6. Auto trade", Order = 4)]
        public string AutoShortSource1 { get; set; }
        [Range(0, 3)]
        [Display(Name = "Short rule 1 (0 = above, 1 = below, 2 = not zero, 3 = equals)", GroupName = "6. Auto trade", Order = 4)]
        public int AutoShortRule1 { get; set; }
        [Display(Name = "Short value 1", Description = "The number the short plot is compared with (e.g. -1).", GroupName = "6. Auto trade", Order = 4)]
        public double AutoShortValue1 { get; set; }

        [Browsable(false)] public string AutoLongSource2 { get; set; }
        [Browsable(false)] public int AutoLongRule2 { get; set; }
        [Browsable(false)] public double AutoLongValue2 { get; set; }
        [Browsable(false)] public string AutoLongSource3 { get; set; }
        [Browsable(false)] public int AutoLongRule3 { get; set; }
        [Browsable(false)] public double AutoLongValue3 { get; set; }
        [Browsable(false)] public string AutoLongSource4 { get; set; }
        [Browsable(false)] public int AutoLongRule4 { get; set; }
        [Browsable(false)] public double AutoLongValue4 { get; set; }
        [Browsable(false)] public string AutoLongSource5 { get; set; }
        [Browsable(false)] public int AutoLongRule5 { get; set; }
        [Browsable(false)] public double AutoLongValue5 { get; set; }
        [Browsable(false)] public string AutoLongSource6 { get; set; }
        [Browsable(false)] public int AutoLongRule6 { get; set; }
        [Browsable(false)] public double AutoLongValue6 { get; set; }
        [Browsable(false)] public string AutoLongSource7 { get; set; }
        [Browsable(false)] public int AutoLongRule7 { get; set; }
        [Browsable(false)] public double AutoLongValue7 { get; set; }
        [Browsable(false)] public string AutoLongSource8 { get; set; }
        [Browsable(false)] public int AutoLongRule8 { get; set; }
        [Browsable(false)] public double AutoLongValue8 { get; set; }
        [Browsable(false)] public string AutoLongSource9 { get; set; }
        [Browsable(false)] public int AutoLongRule9 { get; set; }
        [Browsable(false)] public double AutoLongValue9 { get; set; }
        [Browsable(false)] public string AutoLongSource10 { get; set; }
        [Browsable(false)] public int AutoLongRule10 { get; set; }
        [Browsable(false)] public double AutoLongValue10 { get; set; }

        [Browsable(false)] public string AutoShortSource2 { get; set; }
        [Browsable(false)] public int AutoShortRule2 { get; set; }
        [Browsable(false)] public double AutoShortValue2 { get; set; }
        [Browsable(false)] public string AutoShortSource3 { get; set; }
        [Browsable(false)] public int AutoShortRule3 { get; set; }
        [Browsable(false)] public double AutoShortValue3 { get; set; }
        [Browsable(false)] public string AutoShortSource4 { get; set; }
        [Browsable(false)] public int AutoShortRule4 { get; set; }
        [Browsable(false)] public double AutoShortValue4 { get; set; }
        [Browsable(false)] public string AutoShortSource5 { get; set; }
        [Browsable(false)] public int AutoShortRule5 { get; set; }
        [Browsable(false)] public double AutoShortValue5 { get; set; }
        [Browsable(false)] public string AutoShortSource6 { get; set; }
        [Browsable(false)] public int AutoShortRule6 { get; set; }
        [Browsable(false)] public double AutoShortValue6 { get; set; }
        [Browsable(false)] public string AutoShortSource7 { get; set; }
        [Browsable(false)] public int AutoShortRule7 { get; set; }
        [Browsable(false)] public double AutoShortValue7 { get; set; }
        [Browsable(false)] public string AutoShortSource8 { get; set; }
        [Browsable(false)] public int AutoShortRule8 { get; set; }
        [Browsable(false)] public double AutoShortValue8 { get; set; }
        [Browsable(false)] public string AutoShortSource9 { get; set; }
        [Browsable(false)] public int AutoShortRule9 { get; set; }
        [Browsable(false)] public double AutoShortValue9 { get; set; }
        [Browsable(false)] public string AutoShortSource10 { get; set; }
        [Browsable(false)] public int AutoShortRule10 { get; set; }
        [Browsable(false)] public double AutoShortValue10 { get; set; }

        [Display(Name = "On opposite signal", Description = "While a trade is open: Ignore the opposite signal, Close the trade, or Reverse.", GroupName = "6. Auto trade", Order = 5)]
        public SneakyHudOpposite AutoOpposite { get; set; }

        [Range(0, 1000)]
        [Display(Name = "Max auto trades per session, per account (0 = no limit)", GroupName = "6. Auto trade", Order = 6)]
        public int AutoMaxTradesPerDay { get; set; }

        [Range(0, 2359)]
        [Display(Name = "Session reset time (HHmm, 1800 = CME reopen)", Description = "The trade counter clears at this time each day instead of at midnight, so the evening session does not inherit the day session's count. Counted separately for each account.", GroupName = "6. Auto trade", Order = 7)]
        public int AutoSessionResetHHmm { get; set; }

        [Range(0, 2359)]
        [Display(Name = "Trade from (HHmm, PC clock)", Description = "Auto entries only between From and To. Leave both at 0 for no time limit.", GroupName = "6. Auto trade", Order = 7)]
        public int AutoStartHHmm { get; set; }

        [Range(0, 2359)]
        [Display(Name = "Trade to (HHmm, PC clock)", GroupName = "6. Auto trade", Order = 8)]
        public int AutoEndHHmm { get; set; }

        [Range(0, 1000000)]
        [Display(Name = "Stop new auto entries after the account loses ($ today, 0 = off)", Description = "Uses the ACCOUNT's realized P&L for the day, so it includes trades from anything else on that account.", GroupName = "6. Auto trade", Order = 9)]
        public double AutoDailyLossLimit { get; set; }

        [Display(Name = "Trade side", Description = "Both (default): a LONG or SHORT signal either one can enter. Long only / Short only: signals on the other side are ignored entirely.", GroupName = "6. Auto trade", Order = 10)]
        public SneakyHudAutoSide AutoTradeSide { get; set; }

        [Display(Name = "Require break of signal bar", Description = "OFF (default): enter right on the signal bar, as before. ON: hold the signal and wait - only enter once a LATER bar's high (long) / low (short) trades beyond the signal bar's own high/low, i.e. price actually follows through past where the signal printed. Dropped (no trade) if the break hasn't happened within the bar limit below.", GroupName = "6. Auto trade", Order = 11)]
        public bool AutoRequireBreakOfSignalBar { get; set; }

        [Range(1, 500)]
        [Display(Name = "Max bars to wait for the break", GroupName = "6. Auto trade", Order = 12)]
        public int AutoBreakMaxBars { get; set; }

        [Display(Name = "Require N consecutive same-color bars", Description = "OFF (default): no bar-color requirement. ON: the bar an entry is about to trigger on, plus the N-1 bars before it, must all be the same color as the trade direction - up-closed bars (Close > Open) for a long, down-closed bars (Close < Open) for a short (e.g. N=2 needs 'up bar after up bar' for a long). A doji (Close == Open) breaks the run and blocks that entry. Works standalone or combined with the break-of-signal-bar filter above (checked on whichever bar the entry would otherwise fire on).", GroupName = "6. Auto trade", Order = 13)]
        public bool AutoRequireConsecutiveBars { get; set; }

        [Range(1, 20)]
        [Display(Name = "Consecutive bars needed", GroupName = "6. Auto trade", Order = 14)]
        public int AutoConsecutiveBarsCount { get; set; }

        [Display(Name = "Trail mode at start", Description = "Trail mode the HUD starts in. It can be changed on the HUD at any time.", GroupName = "4. Trailing", Order = 0)]
        public SneakyHudTrail TrailModeDefault { get; set; }

        [Range(1, 200)]
        [Display(Name = "ATR period", Description = "ATR / CHANDELIER modes. Calculated on this chart's bars.", GroupName = "4. Trailing", Order = 1)]
        public int TrailAtrPeriod { get; set; }

        [Range(0.1, 20)]
        [Display(Name = "ATR multiple at start", GroupName = "4. Trailing", Order = 2)]
        public double TrailAtrMult { get; set; }

        [Range(0.1, 20)]
        [Display(Name = "Chandelier ATR multiple at start", GroupName = "4. Trailing", Order = 3)]
        public double ChandelierMult { get; set; }

        [Range(1, 50)]
        [Display(Name = "Bars for BARS mode at start", Description = "Stop goes under the lowest low (over the highest high) of this many finished bars.", GroupName = "4. Trailing", Order = 4)]
        public int TrailBars { get; set; }

        [Range(0, 200)]
        [Display(Name = "BARS mode offset (ticks beyond the low / high)", GroupName = "4. Trailing", Order = 5)]
        public int TrailBarOffsetTicks { get; set; }

        [Range(1, 500)]
        [Display(Name = "EMA period", Description = "EMA mode. Calculated on this chart's bars.", GroupName = "4. Trailing", Order = 6)]
        public int TrailEmaPeriod { get; set; }

        [Range(-200, 400)]
        [Display(Name = "EMA offset at start (ticks beyond the EMA)", GroupName = "4. Trailing", Order = 7)]
        public int TrailEmaOffsetTicks { get; set; }

        [Range(5, 95)]
        [Display(Name = "% of best profit locked at start", GroupName = "4. Trailing", Order = 8)]
        public int TrailPercent { get; set; }

        [Range(0.25, 1000)]
        [Display(Name = "Step size at start (points)", Description = "STEP mode: each time the best profit reaches another step, the stop moves up one step. Step 1 = break-even.", GroupName = "4. Trailing", Order = 9)]
        public double TrailStepPoints { get; set; }

        [Range(0, 1000)]
        [Display(Name = "Auto BE trigger (points of profit)", GroupName = "3. Auto", Order = 0)]
        public double AutoBeTriggerPoints { get; set; }

        [Range(0, 1000)]
        [Display(Name = "Auto Trail starts at (points of profit)", GroupName = "3. Auto", Order = 1)]
        public double AutoTrailStartPoints { get; set; }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SneakyTradeHUD[] cacheSneakyTradeHUD;
		public SneakyTradeHUD SneakyTradeHUD()
		{
			return SneakyTradeHUD(Input);
		}

		public SneakyTradeHUD SneakyTradeHUD(ISeries<double> input)
		{
			if (cacheSneakyTradeHUD != null)
				for (int idx = 0; idx < cacheSneakyTradeHUD.Length; idx++)
					if (cacheSneakyTradeHUD[idx] != null &&  cacheSneakyTradeHUD[idx].EqualsInput(input))
						return cacheSneakyTradeHUD[idx];
			return CacheIndicator<SneakyTradeHUD>(new SneakyTradeHUD(), input, ref cacheSneakyTradeHUD);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SneakyTradeHUD SneakyTradeHUD()
		{
			return indicator.SneakyTradeHUD(Input);
		}

		public Indicators.SneakyTradeHUD SneakyTradeHUD(ISeries<double> input )
		{
			return indicator.SneakyTradeHUD(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SneakyTradeHUD SneakyTradeHUD()
		{
			return indicator.SneakyTradeHUD(Input);
		}

		public Indicators.SneakyTradeHUD SneakyTradeHUD(ISeries<double> input )
		{
			return indicator.SneakyTradeHUD(input);
		}
	}
}

#endregion
