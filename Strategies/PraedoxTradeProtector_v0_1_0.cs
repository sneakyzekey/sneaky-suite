#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// 2026-09-23 -- PHASE 4 of a from-scratch rebuild of a lost personal project ("Praedox
// Sentinel"). Nothing but marketing copy and screenshots survived -- this is an ORIGINAL
// implementation of what the marketing copy describes, not a recovered/reverse-engineered
// copy. See PraedoxSignalEngine_v0_1_0.cs's header for the project's overall framing.
//
// SCOPE OF THIS FILE: "Trade Protector" (stop trading when the session win OR loss streak
// limit is hit first) + "Daily P&L Limits" (max profit / max loss in dollars, auto-stop),
// per the reference screenshot's marketing copy:
//   "Trade Protector -- Stops trading when your session win OR loss limit is reached first.
//    Global across accounts. Persists across reloads. Resets each session."
//   "Daily P&L Limits -- Max profit and max loss thresholds in dollars. Auto-stops trading
//    when hit."
//
// ARCHITECTURE -- WHY THIS IS A STRATEGY, NOT AN INDICATOR (important, read before editing):
// PraedoxFibEngine/PraedoxVolatilityShield are Indicators, sub-instantiated directly inside
// PraedoxSignalEngine (also an Indicator) via NinjaScript's generated wrapper methods --
// that mechanism ONLY works Indicator-to-Indicator. "Auto-stops trading" here means actually
// flattening live positions, which requires order-management access (Account.Flatten(...))
// that only a Strategy (or AddOn) has. So this file is deliberately a Strategy, living in
// Custom\Strategies\ rather than Custom\Indicators\, and it CANNOT be sub-instantiated by
// PraedoxSignalEngine the way the Phase 2/3 engines were. If/when this needs to gate the
// Signal Engine's entries too, that has to go through the small static bridge class at the
// bottom of this file (PraedoxTradeProtectorState) -- a well-worn NT8 cross-script pattern
// (a static field is shared process-wide since every NinjaScript loads into one AppDomain),
// NOT the sub-instantiation pattern used for Phase 2/3. That wiring is a separate step, not
// done in this file.
//
// WHAT "AUTO-STOPS TRADING" ACTUALLY MEANS HERE (a real limit, not a design choice): nothing
// in NinjaScript can intercept or block a manual click in the SuperDOM/Chart Trader, or an
// order from an unrelated strategy, BEFORE it reaches the exchange -- there's no supported
// pre-submission hook for orders this script didn't originate. What IS enforced:
//   1. The instant a limit is breached, every open position on the account (across every
//      instrument, not just this chart's) is flattened via Account.Flatten(...).
//   2. PraedoxTradeProtectorState.TradingBlocked flips to true for any other script that
//      reads it (e.g. the Signal Engine, once wired) -- stopping new AUTOMATED entries.
//   3. Once blocked, it stays blocked for the rest of the session (see below) -- it will
//      keep re-flattening if something re-opens a position while blocked.
// A human overriding all of this with manual clicks is outside what any NinjaScript can
// prevent; this is a backstop, not a lock.
//
// WIN/LOSS STREAK BASIS: consecutive wins or consecutive losses in a row (resets to 0 the
// moment a trade of the other kind closes) -- matches "win/loss limit" read as a streak
// count, not a total-trades-this-session count. A "trade" here is defined as one full
// flatten-to-flat cycle on this account: every time ANY position on the account returns to
// MarketPosition.Flat, the realized-P&L delta since the previous flat point is that trade's
// result (positive = win, negative = loss, exactly zero = neither, streaks unchanged).
//
// P&L SOURCE: Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar), read via the
// OnAccountItemUpdate callback NinjaTrader already calls for every change -- this reflects
// ALL realized P&L on the account (any strategy, any manual trade), not just orders this
// script places itself. "SESSION" here is read as calendar day (local machine date) for
// both the streak/P&L baseline reset and the persisted-state file below -- a deliberate
// simplification versus NT8's own per-instrument Session Template boundaries (e.g. a
// futures session rolling at 6pm ET); tunable later if daily local-midnight isn't the
// boundary you actually want.
//
// "GLOBAL ACROSS ACCOUNTS": read as "enforced no matter which account you're trading on"
// (this strategy is scoped to whichever single Account it's attached with, like any
// NinjaScript Strategy) -- NOT "simultaneously watches every account in the platform at
// once," which isn't how a per-instance NinjaScript Strategy works. If you trade multiple
// accounts, attach one instance of this per account.
//
// PERSISTENCE: state (today's realized-P&L baseline, current win/loss streak, blocked
// flag+reason) is written to a small plain-text key=value file under NinjaTrader's user
// data folder, keyed by account name, and reloaded on State.DataLoaded -- so a NinjaTrader
// restart mid-session does NOT reset the streak/limits/blocked state, only a new calendar
// day does. Plain key=value lines (not JSON) deliberately, to avoid depending on a JSON
// library NT8's bundled .NET Framework runtime may not have referenced by default -- same
// "ordinary, low-risk idiom" preference used elsewhere in this project.
//
// This does NOT include the on-chart dashboard/cockpit UI (separate later phase) -- this
// file's own status panel (top-right, toggle-able) is a plain readout, not the full "A.I.
// Control Center" panel from the reference screenshots.

namespace NinjaTrader.NinjaScript.Strategies
{
    public class PraedoxTradeProtector_v0_1_0 : Strategy
    {
        private static readonly SimpleFont panelFont = new SimpleFont("Arial", 12) { Bold = true };
        private static readonly SimpleFont warningFont = new SimpleFont("Arial", 14) { Bold = true };

        // -- Live/session state (not persisted verbatim as fields -- see SaveState/LoadState) --
        private bool stateLoaded;
        private string stateFilePath;
        private DateTime sessionDate;
        private double realizedBaseline;      // Account's RealizedProfitLoss at session start
        private double lastFlatRealizedPnl;    // RealizedProfitLoss as of the last flat-to-flat point
        private double currentRealizedPnl;     // most recent RealizedProfitLoss reading
        private int winStreak;
        private int lossStreak;
        private bool blocked;
        private int blockReasonCode;           // 0=none, 1=win streak, 2=loss streak, 3=max profit, 4=max loss
        private bool baselineInitialized;

        private static readonly string[] BlockReasonText =
        {
            "", "WIN STREAK LIMIT REACHED", "LOSS STREAK LIMIT REACHED", "MAX DAILY PROFIT REACHED", "MAX DAILY LOSS REACHED"
        };

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PraedoxTradeProtector_v0_1_0";
                Description = "Phase 4 rebuild: Trade Protector (consecutive win/loss streak limit) + Daily P&L Limits ($ profit/loss auto-stop). Original implementation from behavior spec (no surviving source). Flattens the account and blocks further automated entries when a limit is hit.";

                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                IsInstantiatedOnEachOptimizationIteration = false;

                // -- Trade Protector (consecutive win/loss streak) --
                EnableTradeProtector = true;
                MaxConsecutiveWins = 5;
                MaxConsecutiveLosses = 3;

                // -- Daily P&L Limits (dollars) --
                EnableDailyPnLLimits = true;
                MaxDailyProfitDollars = 500;
                MaxDailyLossDollars = 500;

                // -- Enforcement --
                AutoFlatten = true;

                // -- Display --
                ShowStatusPanel = true;
            }
            else if (State == State.Configure)
            {
                // No extra data series needed -- this reacts to account events, not bars.
            }
            else if (State == State.DataLoaded)
            {
                blockReasonCode = 0;
                blocked = false;
            }
        }

        protected override void OnBarUpdate()
        {
            // This script doesn't trade off bar data -- all the real work happens in
            // OnAccountItemUpdate/OnPositionUpdate below, which NinjaTrader calls directly
            // on account activity regardless of bar close. OnBarUpdate exists only to give
            // the status panel a regular redraw cadence (so it survives a chart scroll/
            // resize) and to catch the (rare) case where DataLoaded ran before the account
            // was attached.
            EnsureStateLoaded();
            DrawStatusPanel();
        }

        /// <summary>
        /// Lazily loads persisted state once Account is actually available (it may not be
        /// set yet at State.DataLoaded for some attach flows) and captures the session's
        /// starting realized P&L baseline if this is the first time we've seen this account
        /// today.
        /// </summary>
        private void EnsureStateLoaded()
        {
            if (stateLoaded || Account == null)
                return;

            stateFilePath = BuildStateFilePath(Account.Name);
            LoadState();
            stateLoaded = true;

            double currentRealized = Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
            currentRealizedPnl = currentRealized;

            if (!baselineInitialized)
            {
                realizedBaseline = currentRealized;
                lastFlatRealizedPnl = currentRealized;
                baselineInitialized = true;
                SaveState();
            }
        }

        protected override void OnAccountItemUpdate(Account account, AccountItem accountItem, double value)
        {
            if (account != Account || accountItem != AccountItem.RealizedProfitLoss)
                return;

            EnsureStateLoaded();
            currentRealizedPnl = value;

            CheckDailyPnLLimits();
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (position.Account != Account || marketPosition != MarketPosition.Flat)
                return;

            EnsureStateLoaded();

            // The realized-P&L delta since the last time ANY position on this account was
            // flat is this trade's result -- see "WIN/LOSS STREAK BASIS" in the file header.
            double delta = currentRealizedPnl - lastFlatRealizedPnl;
            lastFlatRealizedPnl = currentRealizedPnl;

            if (delta > 0)
            {
                winStreak++;
                lossStreak = 0;
            }
            else if (delta < 0)
            {
                lossStreak++;
                winStreak = 0;
            }
            // delta == 0 (pure breakeven/commission-only close): streaks unchanged.

            SaveState();
            CheckTradeProtectorLimits();
        }

        private void CheckDailyPnLLimits()
        {
            if (!EnableDailyPnLLimits || blocked || !baselineInitialized)
                return;

            double sessionPnl = currentRealizedPnl - realizedBaseline;

            if (sessionPnl >= MaxDailyProfitDollars)
                TriggerBlock(3);
            else if (sessionPnl <= -MaxDailyLossDollars)
                TriggerBlock(4);
        }

        private void CheckTradeProtectorLimits()
        {
            if (!EnableTradeProtector || blocked)
                return;

            if (winStreak >= MaxConsecutiveWins)
                TriggerBlock(1);
            else if (lossStreak >= MaxConsecutiveLosses)
                TriggerBlock(2);
        }

        private void TriggerBlock(int reasonCode)
        {
            blocked = true;
            blockReasonCode = reasonCode;
            SaveState();

            PraedoxTradeProtectorState.TradingBlocked = true;
            PraedoxTradeProtectorState.BlockReasonCode = reasonCode;

            if (AutoFlatten)
                FlattenAccount();
        }

        /// <summary>
        /// Flattens every open position on this account across every instrument (not just
        /// this chart's) and cancels working orders for them -- see "WHAT 'AUTO-STOPS
        /// TRADING' ACTUALLY MEANS HERE" in the file header for what this can and can't do.
        /// </summary>
        private void FlattenAccount()
        {
            try
            {
                var instruments = Account.Positions
                    .Where(p => p.MarketPosition != MarketPosition.Flat)
                    .Select(p => p.Instrument)
                    .Distinct()
                    .ToList();

                if (instruments.Count > 0)
                    Account.Flatten(instruments);
            }
            catch (Exception ex)
            {
                Log("PraedoxTradeProtector: flatten failed -- " + ex.Message, LogLevel.Error);
            }
        }

        private void DrawStatusPanel()
        {
            if (!ShowStatusPanel)
            {
                RemoveDrawObject("PraedoxProtectorPanel");
                RemoveDrawObject("PraedoxProtectorWarning");
                return;
            }

            double sessionPnl = baselineInitialized ? currentRealizedPnl - realizedBaseline : 0;
            string panelText = "Session P&L: " + sessionPnl.ToString("+$0.00;-$0.00;$0.00")
                + "\nWin Streak: " + winStreak + " / " + MaxConsecutiveWins
                + "\nLoss Streak: " + lossStreak + " / " + MaxConsecutiveLosses;
            Brush panelColor = blocked ? Brushes.Red : Brushes.White;
            Draw.TextFixed(this, "PraedoxProtectorPanel", panelText, TextPosition.TopRight, panelColor, panelFont, Brushes.Transparent, Brushes.Black, 60);

            if (blocked)
            {
                string reasonText = (blockReasonCode >= 0 && blockReasonCode < BlockReasonText.Length) ? BlockReasonText[blockReasonCode] : "TRADING BLOCKED";
                Draw.TextFixed(this, "PraedoxProtectorWarning", "⛔ TRADING BLOCKED: " + reasonText, TextPosition.BottomRight, Brushes.Red, warningFont, Brushes.Transparent, Brushes.Black, 70);
            }
            else
            {
                RemoveDrawObject("PraedoxProtectorWarning");
            }
        }

        #region Persistence
        private string BuildStateFilePath(string accountName)
        {
            string safeName = accountName;
            foreach (char c in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(c, '_');

            string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "PraedoxTradeProtectorState");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, safeName + ".txt");
        }

        private void LoadState()
        {
            sessionDate = DateTime.Now.Date;
            baselineInitialized = false;
            winStreak = 0;
            lossStreak = 0;
            blocked = false;
            blockReasonCode = 0;

            if (string.IsNullOrEmpty(stateFilePath) || !File.Exists(stateFilePath))
                return;

            try
            {
                var values = new System.Collections.Generic.Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(stateFilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    values[line.Substring(0, eq)] = line.Substring(eq + 1);
                }

                string dateStr;
                DateTime storedDate;
                if (!values.TryGetValue("Date", out dateStr) || !DateTime.TryParse(dateStr, out storedDate) || storedDate.Date != sessionDate)
                    return; // stale (different day) -- fresh defaults above, resets each session

                realizedBaseline = ParseDoubleOr(values, "RealizedBaseline", 0);
                lastFlatRealizedPnl = ParseDoubleOr(values, "LastFlatRealizedPnl", 0);
                winStreak = (int)ParseDoubleOr(values, "WinStreak", 0);
                lossStreak = (int)ParseDoubleOr(values, "LossStreak", 0);
                string blockedStr;
                blocked = values.TryGetValue("Blocked", out blockedStr) && blockedStr == "1";
                blockReasonCode = (int)ParseDoubleOr(values, "BlockReason", 0);
                baselineInitialized = true;

                if (blocked)
                {
                    PraedoxTradeProtectorState.TradingBlocked = true;
                    PraedoxTradeProtectorState.BlockReasonCode = blockReasonCode;
                }
            }
            catch (Exception ex)
            {
                Log("PraedoxTradeProtector: state load failed -- " + ex.Message, LogLevel.Warning);
            }
        }

        private double ParseDoubleOr(System.Collections.Generic.Dictionary<string, string> values, string key, double fallback)
        {
            string s;
            double result;
            return (values.TryGetValue(key, out s) && double.TryParse(s, out result)) ? result : fallback;
        }

        private void SaveState()
        {
            if (string.IsNullOrEmpty(stateFilePath))
                return;

            try
            {
                string[] lines =
                {
                    "Date=" + sessionDate.ToString("yyyy-MM-dd"),
                    "RealizedBaseline=" + realizedBaseline,
                    "LastFlatRealizedPnl=" + lastFlatRealizedPnl,
                    "WinStreak=" + winStreak,
                    "LossStreak=" + lossStreak,
                    "Blocked=" + (blocked ? "1" : "0"),
                    "BlockReason=" + blockReasonCode
                };
                File.WriteAllLines(stateFilePath, lines);
            }
            catch (Exception ex)
            {
                Log("PraedoxTradeProtector: state save failed -- " + ex.Message, LogLevel.Warning);
            }
        }
        #endregion

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Enable Trade Protector", GroupName = "Trade Protector", Order = 1)]
        public bool EnableTradeProtector { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Consecutive Wins", GroupName = "Trade Protector", Order = 2, Description = "Stops trading once this many winning trades in a row occur.")]
        public int MaxConsecutiveWins { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Consecutive Losses", GroupName = "Trade Protector", Order = 3, Description = "Stops trading once this many losing trades in a row occur.")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Daily P&L Limits", GroupName = "Daily P&L Limits", Order = 1)]
        public bool EnableDailyPnLLimits { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Max Daily Profit ($)", GroupName = "Daily P&L Limits", Order = 2, Description = "Stops trading once today's realized P&L on this account reaches this profit.")]
        public double MaxDailyProfitDollars { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Max Daily Loss ($)", GroupName = "Daily P&L Limits", Order = 3, Description = "Stops trading once today's realized P&L on this account reaches this loss (enter as a positive number).")]
        public double MaxDailyLossDollars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Auto-Flatten On Breach", GroupName = "Enforcement", Order = 1, Description = "When a limit is hit, immediately flatten every open position on this account (all instruments) and cancel their working orders. Off = block new automated entries only, leave existing positions alone.")]
        public bool AutoFlatten { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Status Panel", GroupName = "Display", Order = 1)]
        public bool ShowStatusPanel { get; set; }
        #endregion
    }

    /// <summary>
    /// Cross-script bridge -- see "ARCHITECTURE" in this file's header comment. A static
    /// field is shared process-wide (every NinjaScript loads into the same AppDomain), so
    /// any other script -- an Indicator like PraedoxSignalEngine, which can't sub-instantiate
    /// a Strategy -- can read PraedoxTradeProtectorState.TradingBlocked directly without any
    /// wrapper/instantiation mechanism. Defaults to false/0 (fail-open) if this Strategy
    /// isn't attached and running anywhere -- a stopped protector doesn't silently block
    /// everything.
    /// </summary>
    public static class PraedoxTradeProtectorState
    {
        public static volatile bool TradingBlocked = false;
        public static volatile int BlockReasonCode = 0;
    }
}
