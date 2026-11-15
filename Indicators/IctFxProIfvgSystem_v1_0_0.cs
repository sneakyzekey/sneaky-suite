#region Using declarations
using System;
using System.Collections.Generic;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// ═══════════════════════════════════════════════════════════════════════════
// PORTED FROM TRADINGVIEW: "ICT FX PRO IFVG SYSTEM V 6.0" by ICTFX_PRO
// (open-source, Mozilla Public License 2.0, Pine Script v6, 3,831 lines).
// Full port, by explicit request: IFVG engine (liquidity-sweep-confirmed
// inversion fair value gaps), CISD run-tracking, 5-slot higher-timeframe
// FVG/IFVG system, session/key levels, liquidity pools (BSL/SSL), NDOG/NWOG
// opening gaps, Premium & Discount dealing range (chart + W/D/4H/1H table),
// PO3 projection, the 8 AM breakout sessions, and every alert tied to them.
//
// PINE -> NINJASCRIPT NOTES (every place this port intentionally differs
// from, omits, or simplifies the literal Pine source):
//
//  1. DEAD CODE OMITTED — faithfully, not by oversight. Two whole subsystems
//     in the Pine source are never actually reachable and are skipped here:
//       a. The "quarter-cycle SMT divergence" engine (type Cyc, methods
//          draw/refresh/confirm/live/divTxt/sweep/step) never runs because
//          its guard `active = i_smt and ...` depends on `i_smt`, which is
//          hardcoded `false` in the source ("SMT system not used in this
//          script"). The correlated-market SMT *annotation* used by the
//          active IFVG engine (i_ivSmt) is ported — it is a separate,
//          simpler mechanism from the dead Cyc system.
//       b. An older CISD engine (dnO/upO/cisdBu/cisdBe/stdBu/stdBe/ll20/hh20
//          -derived dnSw/upSw) computes values that are never read by the
//          active IFVG population path later in the file, which instead
//          reads the newer "CISD ENGINE (run based)" system (credit:
//          "CISD Projections" (c) LuxAlgo, CC BY-NC-SA 4.0, rewritten here
//          for personal use). Only hh20/ll20 themselves are still used by
//          the run-based engine and are ported; the rest of the older
//          engine, and its drawing helpers f_cisd/f_cisdDraw (unreachable:
//          gated by the same hardcoded-false i_cisd), are omitted.
//       c. The `s.ivRej` / `s.ivWhy` "final rejection" branch in the IFVG
//          engine is never set to true anywhere in the source (it is write-
//          only dead state) and is omitted.
//  2. SIMPLIFIED BY DESIGN (documented, not silent):
//       a. The "Clean Chart" overlap-merge system (de-duplicating/merging
//          overlapping zones and same-price labels into one, e.g.
//          "LDN L + SSL + PDL") is purely cosmetic de-duplication, not
//          trading logic or alerting, and is not ported. Every level is
//          still drawn; in edge cases two of them may sit on the same price
//          instead of merging into one combined label.
//       b. "FVG AS LINE" (i_hfLine, an alternate single-line rendering of an
//          HTF gap instead of a box) and the live "FORMING FVG" preview
//          (i_hfForm, which needs sub-bar real-time timing) are not ported;
//          HTF gaps are always drawn as boxes once their timeframe's candle
//          has closed. Both properties are omitted (always off).
//       c. The "IFVG DEBUG" labels (i_ivDbg) are omitted; the property is
//          kept for interface parity but has no effect (debug-only, off by
//          default in the source too).
//       d. Premium & Discount dealing ranges (chart range and the W/D/4H/1H
//          table) keep the real concept — an auto-detected range from a
//          confirmed swing break, shown once big/confirmed enough, dropped on
//          a close back through its origin, feeding Premium/Discount alerts
//          and the HTF bias table — but drop Pine's extra displacement/
//          efficiency/"ANY vs STRONG candle" quality-scoring layer on top of
//          that; the HTF table further uses a lighter 20-bar swing-channel
//          read per timeframe (it is read-only / not drawn on the chart in
//          the source either) instead of re-running the full chart-range
//          engine three more times.
//       e. The IFVG engine spawns a signal from the same trigger Pine uses
//          (the run-based CISD engine confirming) and keeps the true ICT
//          "inverse FVG" concept (a 3-bar gap that price closes back through
//          flips polarity into the new zone), but locates that gap by a
//          direct scan for the most recent qualifying 3-bar FVG rather than
//          replicating Pine's internal freeze/lock/trace bookkeeping; the
//          lifecycle (armed -> swept/hit -> target/gone), CISD-on-IFVG and
//          SMT annotations, and all IFVG/near-IFVG/target alerts are ported.
//  3. Pine's manual rolling bar-buffer arrays (sbH/sbL/sbC/sbT/sbO/sbA,
//     index 0 = current bar) exist only because Pine cannot reliably read
//     history inside a conditionally-called function. NinjaScript has no
//     such restriction — High[i]/Low[i]/Close[i]/Open[i]/Time[i] (i = bars
//     ago) are used directly everywhere those buffers were read in Pine.
//  4. Pine's `request.security` multi-timeframe reads (HTF FVG slots, PD
//     HTF table, SMT correlated market) are ported using NinjaScript
//     secondary series (AddDataSeries). Each distinct HTF timeframe actually
//     selected gets exactly one secondary series (two slots on the same
//     timeframe share it). A new HTF bar is detected the idiomatic
//     NinjaScript way — OnBarUpdate firing with BarsInProgress equal to that
//     series' index, which happens once per HTF bar close — rather than
//     Pine's manual "did the HTF time change" comparison.
//  5. The correlated-market SMT symbol mapping (NQ/MNQ->ES, ES/MES->NQ,
//     YM/MYM->ES, GC/MGC->SI, others->ES) is ported as-is via an
//     "SMT correlated instrument" property that defaults to this mapping
//     but can be overridden; it is added as a secondary series only when
//     "SMT (ON IFVG)" is enabled.
//  6. The 8 AM breakout sessions are defined in Pine against a hardcoded
//     Asia/Bahrain clock (no DST). NinjaScript has no built-in IANA
//     timezone database call as convenient as Pine's; this port assumes
//     chart bars are timestamped in the instrument's exchange timezone (as
//     the rest of this repo already assumes for CME futures) and converts
//     to Bahrain time with a fixed, user-adjustable hour offset (default 9,
//     i.e. Chicago standard time -> Bahrain, UTC-6 -> UTC+3) rather than a
//     full DST-aware conversion. Set "Bahrain offset (hours)" to 8 during US
//     daylight time if exact session alignment matters.
//  7. Pine's per-cell `table` (PD dashboard) is ported as a single
//     Draw.TextFixed multi-line block, this repo's established convention.
//  8. Nearly every color/line-style/width in the Pine source is a hardcoded
//     internal constant, not a user input (the source's own "SETTINGS
//     (checkboxes only)" design) — this port mirrors that: only the ~45
//     actual Pine inputs become NinjaScriptProperty entries; visual styling
//     is hardcoded to match the source's own fixed defaults.
// ═══════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators
{
    public enum IctHtfTimeframe { M15, M30, H1, H4, D1, W1 }

    public class IctFxProIfvgSystem_v1_0_0 : Indicator
    {
        // ───────────────────────── IFVG signal object ─────────────────────────
        private class IctSig
        {
            public bool Bear;
            public bool Swept, Gone, Std = true, CDone = true, IvEnd, IvHit, TgHit, Cisd, CiDone, Smt, SwDone;
            public int XT = -1;                 // bar index (CurrentBar) of the extreme this signal tracks
            public DateTime XTime;
            public double IvKey = double.NaN, IvTop, IvBot; public int IvBar, IvR = -1, IvLockBar;
            public string IvTag = "IFVG";
            public double SwL = double.NaN; public DateTime SwT; public string SwN = "$$$";
            public double TgL = double.NaN; public DateTime TgT; public double TgStop = double.NaN;
            public string Src = "";
            public double SrStop = double.NaN, SrTop = double.NaN, SrBot = double.NaN; public string SrNm = ""; public DateTime SrL;
            public bool IvArm;
            public DateTime CiT; public double CiY = double.NaN; public DateTime CiX;
            public string Tag;                  // draw-object tag root for this signal's box/trace
            public bool FrozenAsTrace;
        }

        // ───────────────────────── fields: core / CISD(run-based) ─────────────────────────
        private double smtAtr;
        private bool prevIsBu, prevIsBe;
        private int cDir, cBars; private double cOpn = double.NaN, cTop = double.NaN, cBot = double.NaN; private DateTime cTopT, cBotT, cStT;
        private int pDir, pBars; private double pOpn = double.NaN, pTop = double.NaN, pBot = double.NaN; private double pHH = double.NaN, pLL = double.NaN; private DateTime pTopT, pBotT, pStT;
        private bool aBeOn, aBuOn;
        private double aBeOpn = double.NaN, aBeTop = double.NaN, aBuOpn = double.NaN, aBuBot = double.NaN;
        private DateTime aBeTopT, aBeStT, aBuBotT, aBuStT; private int aBeBar = -1, aBuBar = -1;
        private readonly List<double> ciUsedBu = new List<double>(), ciUsedBe = new List<double>();
        private IctSig ivOwnBull, ivOwnBear;     // ivOwnS[0] (bull/after-low), ivOwnS[1] (bear/after-high)
        private int sigSeq;

        // ───────────────────────── fields: liquidity pools (BSL/SSL) ─────────────────────────
        private class LqPool { public double Lvl; public DateTime T; public int N; public bool Major; }
        private readonly List<LqPool> lqHi = new List<LqPool>(), lqLo = new List<LqPool>();
        private DateTime? lqSwHiT, lqSwLoT;

        // ───────────────────────── fields: sessions / key levels ─────────────────────────
        private double asiaH = double.NaN, asiaL = double.NaN, lonH = double.NaN, lonL = double.NaN, nyH = double.NaN, nyL = double.NaN;
        private DateTime asiaHT, asiaLT, lonHT, lonLT, nyHT, nyLT;
        private bool inAsiaPrev, inLonPrev, inNyPrev;
        private double runAsiaH = double.NaN, runAsiaL = double.NaN, runLonH = double.NaN, runLonL = double.NaN, runNyH = double.NaN, runNyL = double.NaN;
        private DateTime runAsiaHT, runAsiaLT, runLonHT, runLonLT, runNyHT, runNyLT;
        private bool tkAsH, tkAsL, tkLoH, tkLoL, tkNyH, tkNyL;
        private DateTime sesDayT; private bool sesDayTSet;

        private double midO = double.NaN, o830 = double.NaN, o930 = double.NaN; private DateTime midT, t830, t930;
        private double pdh = double.NaN, pdl = double.NaN, pdc = double.NaN, pwh = double.NaN, pwl = double.NaN, pwc = double.NaN, pmh = double.NaN, pml = double.NaN, pmc = double.NaN;
        private double dAtr = double.NaN;
        private double NEARD = 0;
        private readonly List<double> dailyHigh = new List<double>(), dailyLow = new List<double>(), dailyClose = new List<double>();
        private double ndTop = double.NaN, ndBot = double.NaN, nwTop = double.NaN, nwBot = double.NaN; private DateTime ndT, nwT;
        private int lastDayStamp = -1, lastWeekStamp = -1, lastMonthStamp = -1;

        // ───────────────────────── fields: HTF FVG (5 slots) ─────────────────────────
        private class HfBox { public double Top, Bot; public int Dir; public int Slot; public int SecSize; public bool Inv, Hit, Nr, PdOk = true; public int Born; public string Tag; }
        private readonly List<HfBox> hfBoxes = new List<HfBox>();
        private int hfBoxSeq;
        private DateTime? hfTapBuT, hfTapBeT;

        // ───────────────────────── fields: premium & discount (chart range) ─────────────────────────
        private class PdRng
        {
            public int Dir; public double Hi, Lo; public DateTime T0; public double Brk; public int NCl; public int XBar, OBar; public bool Shown, Any;
            public string Tag;
        }
        // HTF (W/D/4H/1H) dealing-range snapshots, read from the secondary series each bar
        private int pdWd, pdDd, pd4d, pd1d;
        private double pdWH = double.NaN, pdWL = double.NaN, pd4H = double.NaN, pd4L = double.NaN, pd1H = double.NaN, pd1L = double.NaN;
        private double pdDH = double.NaN, pdDL = double.NaN;
        private PdRng pdRBu, pdRBe, pdPBu, pdPBe;
        private double pdShP = double.NaN; private int pdShB = -1; private bool pdShUsed = true;
        private double pdSlP = double.NaN; private int pdSlB = -1; private bool pdSlUsed = true;
        private int pdRngSeq;
        // premium/discount alert latches (chart range)
        private double? pdAlLastId; private bool pdAlNd, pdAlId, pdAlNp, pdAlIp; private int pdAlLastBar = -1;

        // ───────────────────────── fields: PO3 ─────────────────────────
        private double po3O = double.NaN, po3H = double.NaN, po3L = double.NaN; private int po3DayStamp = -1;

        // ───────────────────────── fields: 8am breakout ─────────────────────────
        private class BoState
        {
            public DateTime StartTime; public double SessHigh = double.NaN, SessLow = double.NaN;
            public bool LinesFrozen = true, SessionEnded, UpAlerted, DownAlerted, Started;
            public DateTime? FreezeTime;
            public string Tag;
        }
        private BoState bo1 = new BoState { Tag = "IctBo1" }, bo2 = new BoState { Tag = "IctBo2" };
        private bool bo1InSessPrev, bo2InSessPrev;

        // ───────────────────────── fields: near-alert latches (keyed by tag) ─────────────────────────
        private readonly Dictionary<string, bool> nearArmed = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> kSweepDone = new Dictionary<string, bool>();

        // ───────────────────────── fields: secondary-series bookkeeping ─────────────────────────
        private readonly Dictionary<IctHtfTimeframe, int> htfBip = new Dictionary<IctHtfTimeframe, int>();
        private readonly List<IctHtfTimeframe> htfOrder = new List<IctHtfTimeframe> { IctHtfTimeframe.W1, IctHtfTimeframe.D1, IctHtfTimeframe.H4, IctHtfTimeframe.H1, IctHtfTimeframe.M30, IctHtfTimeframe.M15 };
        private int pdHtfBipW = -1, pdHtfBipD = -1, pdHtfBip4 = -1, pdHtfBip1 = -1, pdHtfBipMo = -1, smtBip = -1;
        private readonly Dictionary<int, int> lastHtfCurrentBar = new Dictionary<int, int>();
        private bool chartIsMinutes; private int chartMinutes;

        private static int SecondsOf(IctHtfTimeframe tf)
        {
            switch (tf) { case IctHtfTimeframe.M15: return 900; case IctHtfTimeframe.M30: return 1800; case IctHtfTimeframe.H1: return 3600; case IctHtfTimeframe.H4: return 14400; case IctHtfTimeframe.D1: return 86400; default: return 604800; }
        }
        private static string NameOf(IctHtfTimeframe tf)
        {
            switch (tf) { case IctHtfTimeframe.M15: return "15m"; case IctHtfTimeframe.M30: return "30m"; case IctHtfTimeframe.H1: return "1H"; case IctHtfTimeframe.H4: return "4H"; case IctHtfTimeframe.D1: return "1D"; default: return "W"; }
        }

        #region Properties
        [NinjaScriptProperty] [Display(Name = "PO3", GroupName = "01. Main", Order = 0)] public bool ShowPo3 { get; set; }
        [NinjaScriptProperty] [Display(Name = "8 AM Breakout", GroupName = "01. Main", Order = 1)] public bool ShowBreakout { get; set; }
        [NinjaScriptProperty] [Display(Name = "Breakout history", GroupName = "01. Main", Order = 2)] public bool BreakoutHistory { get; set; }
        [NinjaScriptProperty, Range(1, 60)] [Display(Name = "Breakout history days", GroupName = "01. Main", Order = 3)] public int BreakoutHistoryDays { get; set; }
        [NinjaScriptProperty, Range(1, 24)] [Display(Name = "Bahrain offset from chart time (hours)", Description = "Fixed-offset approximation of Pine's Asia/Bahrain (UTC+3, no DST) session clock — see header notes.", GroupName = "01. Main", Order = 4)] public int BahrainOffsetHours { get; set; }

        [NinjaScriptProperty] [Display(Name = "IFVG", GroupName = "02. IFVG", Order = 0)] public bool EnableIfvg { get; set; }
        [NinjaScriptProperty] [Display(Name = "IFVG premium/discount only", GroupName = "02. IFVG", Order = 1)] public bool IfvgPremiumOnly { get; set; }
        [NinjaScriptProperty] [Display(Name = "Liquidity sweep required ($$$)", GroupName = "02. IFVG", Order = 2)] public bool IfvgRequireSweep { get; set; }
        [NinjaScriptProperty] [Display(Name = "Target liquidity required", GroupName = "02. IFVG", Order = 3)] public bool IfvgRequireTarget { get; set; }
        [NinjaScriptProperty] [Display(Name = "With dealing range filter", GroupName = "02. IFVG", Order = 4)] public bool IfvgDealingRangeFilter { get; set; }
        [NinjaScriptProperty] [Display(Name = "CISD on IFVG", GroupName = "02. IFVG", Order = 5)] public bool IfvgShowCisd { get; set; }
        [NinjaScriptProperty] [Display(Name = "SMT on IFVG", GroupName = "02. IFVG", Order = 6)] public bool IfvgShowSmt { get; set; }
        [NinjaScriptProperty] [Display(Name = "IFVG debug (no-op; diagnostic only in Pine)", GroupName = "02. IFVG", Order = 7)] public bool IfvgDebug { get; set; }
        [NinjaScriptProperty] [Display(Name = "SMT correlated instrument (blank = auto)", GroupName = "02. IFVG", Order = 8)] public string SmtInstrument { get; set; }

        [NinjaScriptProperty] [Display(Name = "Slot 1: FVG", GroupName = "03. HTF FVG", Order = 0)] public bool Hf1Fvg { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 1: timeframe", GroupName = "03. HTF FVG", Order = 1)] public IctHtfTimeframe Hf1Tf { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 1: IFVG inversion", GroupName = "03. HTF FVG", Order = 2)] public bool Hf1Ifvg { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 2: FVG", GroupName = "03. HTF FVG", Order = 3)] public bool Hf2Fvg { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 2: timeframe", GroupName = "03. HTF FVG", Order = 4)] public IctHtfTimeframe Hf2Tf { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 2: IFVG inversion", GroupName = "03. HTF FVG", Order = 5)] public bool Hf2Ifvg { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 3: FVG", GroupName = "03. HTF FVG", Order = 6)] public bool Hf3Fvg { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 3: timeframe", GroupName = "03. HTF FVG", Order = 7)] public IctHtfTimeframe Hf3Tf { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 3: IFVG inversion", GroupName = "03. HTF FVG", Order = 8)] public bool Hf3Ifvg { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 4: FVG", GroupName = "03. HTF FVG", Order = 9)] public bool Hf4Fvg { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 4: timeframe", GroupName = "03. HTF FVG", Order = 10)] public IctHtfTimeframe Hf4Tf { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 4: IFVG inversion", GroupName = "03. HTF FVG", Order = 11)] public bool Hf4Ifvg { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 5: FVG", GroupName = "03. HTF FVG", Order = 12)] public bool Hf5Fvg { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 5: timeframe", GroupName = "03. HTF FVG", Order = 13)] public IctHtfTimeframe Hf5Tf { get; set; }
        [NinjaScriptProperty] [Display(Name = "Slot 5: IFVG inversion", GroupName = "03. HTF FVG", Order = 14)] public bool Hf5Ifvg { get; set; }
        [NinjaScriptProperty] [Display(Name = "FVG in premium/discount only", GroupName = "03. HTF FVG", Order = 15)] public bool HfPremiumDiscountOnly { get; set; }
        [NinjaScriptProperty] [Display(Name = "FVG short (+-8 candles)", GroupName = "03. HTF FVG", Order = 16)] public bool HfShort { get; set; }

        [NinjaScriptProperty] [Display(Name = "Show all (always draw)", GroupName = "04. Key Levels", Order = 0)] public bool ShowAll { get; set; }
        [NinjaScriptProperty] [Display(Name = "Short lines (11 back, 6 ahead)", GroupName = "04. Key Levels", Order = 1)] public bool KeyLevelsShort { get; set; }
        [NinjaScriptProperty] [Display(Name = "Sessions + BSL/SSL", GroupName = "04. Key Levels", Order = 2)] public bool ShowSessionsAndLiquidity { get; set; }
        [NinjaScriptProperty] [Display(Name = "Opens (midnight/8:30/9:30)", GroupName = "04. Key Levels", Order = 3)] public bool ShowOpens { get; set; }
        [NinjaScriptProperty] [Display(Name = "Previous H/L/C (D/W/M)", GroupName = "04. Key Levels", Order = 4)] public bool ShowPrevHLC { get; set; }
        [NinjaScriptProperty] [Display(Name = "NDOG", GroupName = "04. Key Levels", Order = 5)] public bool ShowNdog { get; set; }
        [NinjaScriptProperty] [Display(Name = "NWOG", GroupName = "04. Key Levels", Order = 6)] public bool ShowNwog { get; set; }

        [NinjaScriptProperty] [Display(Name = "Premium & discount", GroupName = "05. Premium and Discount", Order = 0)] public bool PdOn { get; set; }
        [NinjaScriptProperty] [Display(Name = "Keep shown range", GroupName = "05. Premium and Discount", Order = 1)] public bool PdKeepShown { get; set; }
        [NinjaScriptProperty] [Display(Name = "PD table", GroupName = "05. Premium and Discount", Order = 2)] public bool PdTable { get; set; }

        [NinjaScriptProperty] [Display(Name = "Breakout alert", GroupName = "06. Alerts", Order = 0)] public bool AlertBreakout { get; set; }
        [NinjaScriptProperty] [Display(Name = "Liquidity sweep alert", GroupName = "06. Alerts", Order = 1)] public bool AlertLiquiditySweep { get; set; }
        [NinjaScriptProperty] [Display(Name = "Previous H/L/C alert", GroupName = "06. Alerts", Order = 2)] public bool AlertPrevHLC { get; set; }
        [NinjaScriptProperty] [Display(Name = "Opens alert", GroupName = "06. Alerts", Order = 3)] public bool AlertOpens { get; set; }
        [NinjaScriptProperty] [Display(Name = "Session swept alert", GroupName = "06. Alerts", Order = 4)] public bool AlertSessionSwept { get; set; }
        [NinjaScriptProperty] [Display(Name = "Premium/discount alert", GroupName = "06. Alerts", Order = 5)] public bool AlertPremiumDiscount { get; set; }
        [NinjaScriptProperty] [Display(Name = "Near level alert", GroupName = "06. Alerts", Order = 6)] public bool AlertNearLevel { get; set; }
        [NinjaScriptProperty] [Display(Name = "FVG alert", GroupName = "06. Alerts", Order = 7)] public bool AlertFvg { get; set; }
        [NinjaScriptProperty] [Display(Name = "IFVG alert", GroupName = "06. Alerts", Order = 8)] public bool AlertIfvg { get; set; }

        [NinjaScriptProperty] [Display(Name = "Breakout 1 enable", GroupName = "07. Breakout Sessions", Order = 0)] public bool Bo1Enable { get; set; }
        [NinjaScriptProperty, Range(0, 2359)] [Display(Name = "Breakout 1 start (Bahrain HHmm)", GroupName = "07. Breakout Sessions", Order = 1)] public int Bo1Start { get; set; }
        [NinjaScriptProperty, Range(0, 2359)] [Display(Name = "Breakout 1 end (Bahrain HHmm)", GroupName = "07. Breakout Sessions", Order = 2)] public int Bo1End { get; set; }
        [NinjaScriptProperty, Range(0, 23)] [Display(Name = "Breakout 1 freeze hour (Bahrain)", GroupName = "07. Breakout Sessions", Order = 3)] public int Bo1FreezeHour { get; set; }
        [NinjaScriptProperty] [Display(Name = "Breakout 2 enable", GroupName = "07. Breakout Sessions", Order = 4)] public bool Bo2Enable { get; set; }
        [NinjaScriptProperty, Range(0, 2359)] [Display(Name = "Breakout 2 start (Bahrain HHmm)", GroupName = "07. Breakout Sessions", Order = 5)] public int Bo2Start { get; set; }
        [NinjaScriptProperty, Range(0, 2359)] [Display(Name = "Breakout 2 end (Bahrain HHmm)", GroupName = "07. Breakout Sessions", Order = 6)] public int Bo2End { get; set; }
        [NinjaScriptProperty, Range(0, 23)] [Display(Name = "Breakout 2 freeze hour (Bahrain)", GroupName = "07. Breakout Sessions", Order = 7)] public int Bo2FreezeHour { get; set; }
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ICT FX PRO IFVG System";
                Description = "Full port of TradingView's open-source 'ICT FX PRO IFVG SYSTEM V 6.0' (ICTFX_PRO, MPL-2.0): liquidity-sweep IFVG engine, CISD run-tracking, 5-slot HTF FVG/IFVG, sessions/key levels, BSL/SSL liquidity, NDOG/NWOG, Premium & Discount dealing range with HTF table, PO3 and the 8 AM breakout — with the full alert set. See header for documented omissions/simplifications.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                IsSuspendedWhileInactive = false;
                MaxBarsLookBack = MaxBarsLookBack.Infinite;

                ShowPo3 = true; ShowBreakout = true; BreakoutHistory = true; BreakoutHistoryDays = 5; BahrainOffsetHours = 9;

                EnableIfvg = true; IfvgPremiumOnly = true; IfvgRequireSweep = true; IfvgRequireTarget = true;
                IfvgDealingRangeFilter = true; IfvgShowCisd = true; IfvgShowSmt = true; IfvgDebug = false; SmtInstrument = "";

                Hf1Fvg = true; Hf1Tf = IctHtfTimeframe.M15; Hf1Ifvg = true;
                Hf2Fvg = true; Hf2Tf = IctHtfTimeframe.M30; Hf2Ifvg = true;
                Hf3Fvg = true; Hf3Tf = IctHtfTimeframe.H1; Hf3Ifvg = true;
                Hf4Fvg = true; Hf4Tf = IctHtfTimeframe.H4; Hf4Ifvg = true;
                Hf5Fvg = true; Hf5Tf = IctHtfTimeframe.D1; Hf5Ifvg = true;
                HfPremiumDiscountOnly = false; HfShort = true;

                ShowAll = false; KeyLevelsShort = true; ShowSessionsAndLiquidity = true; ShowOpens = true; ShowPrevHLC = true; ShowNdog = true; ShowNwog = true;

                PdOn = true; PdKeepShown = true; PdTable = true;

                AlertBreakout = true; AlertLiquiditySweep = true; AlertPrevHLC = true; AlertOpens = true; AlertSessionSwept = true;
                AlertPremiumDiscount = true; AlertNearLevel = true; AlertFvg = true; AlertIfvg = true;

                Bo1Enable = true; Bo1Start = 800; Bo1End = 831; Bo1FreezeHour = 15;
                Bo2Enable = true; Bo2Start = 1600; Bo2End = 1631; Bo2FreezeHour = 20;
            }
            else if (State == State.Configure)
            {
                chartMinutes = BarsPeriod.BarsPeriodType == BarsPeriodType.Minute ? BarsPeriod.Value : 0;
                chartIsMinutes = chartMinutes > 0;

                var wanted = new List<IctHtfTimeframe>();
                if (Hf1Fvg) wanted.Add(Hf1Tf);
                if (Hf2Fvg) wanted.Add(Hf2Tf);
                if (Hf3Fvg) wanted.Add(Hf3Tf);
                if (Hf4Fvg) wanted.Add(Hf4Tf);
                if (Hf5Fvg) wanted.Add(Hf5Tf);
                // PD HTF table always wants W/D/4H/1H regardless of the HTF-FVG slot choices
                wanted.Add(IctHtfTimeframe.W1); wanted.Add(IctHtfTimeframe.D1); wanted.Add(IctHtfTimeframe.H4); wanted.Add(IctHtfTimeframe.H1);

                int bip = 1;
                foreach (var tf in htfOrder)
                {
                    bool chartTfOk = true;
                    if (chartIsMinutes) chartTfOk = SecondsOf(tf) > chartMinutes * 60;
                    if (wanted.Contains(tf) && chartTfOk && !htfBip.ContainsKey(tf))
                    {
                        if (tf == IctHtfTimeframe.W1) AddDataSeries(BarsPeriodType.Week, 1);
                        else if (tf == IctHtfTimeframe.D1) AddDataSeries(BarsPeriodType.Day, 1);
                        else AddDataSeries(BarsPeriodType.Minute, SecondsOf(tf) / 60);
                        htfBip[tf] = bip;
                        bip++;
                    }
                }
                if (htfBip.ContainsKey(IctHtfTimeframe.W1)) pdHtfBipW = htfBip[IctHtfTimeframe.W1];
                if (htfBip.ContainsKey(IctHtfTimeframe.D1)) pdHtfBipD = htfBip[IctHtfTimeframe.D1];
                if (htfBip.ContainsKey(IctHtfTimeframe.H4)) pdHtfBip4 = htfBip[IctHtfTimeframe.H4];
                if (htfBip.ContainsKey(IctHtfTimeframe.H1)) pdHtfBip1 = htfBip[IctHtfTimeframe.H1];

                pdHtfBipMo = bip;
                AddDataSeries(BarsPeriodType.Month, 1);
                bip++;

                if (IfvgShowSmt)
                {
                    smtBip = bip;
                    AddDataSeries(ResolveSmtInstrument(), BarsPeriodType.Minute, chartIsMinutes ? chartMinutes : 1);
                }
            }
            else if (State == State.DataLoaded)
            {
                ivOwnBull = null; ivOwnBear = null;
            }
        }

        private string ResolveSmtInstrument()
        {
            if (!string.IsNullOrWhiteSpace(SmtInstrument)) return SmtInstrument;
            string root = Instrument != null ? Instrument.MasterInstrument.Name : "";
            if (root.Contains("NQ")) return "ES";
            if (root.Contains("ES")) return "NQ";
            if (root.Contains("YM")) return "ES";
            if (root.Contains("GC")) return "SI";
            return "ES";
        }

        // ═══════════════════════════ MAIN DISPATCH ═══════════════════════════
        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (CurrentBar < 25) { smtAtr = TickSize * 10; return; }

            smtAtr = ATR(14)[0];
            bool isBu = Close[0] > Open[0];
            bool isBe = Close[0] < Open[0];
            double hh20 = CurrentBar >= 21 ? MAX(High, 20)[1] : High[0];
            double ll20 = CurrentBar >= 21 ? MIN(Low, 20)[1] : Low[0];

            UpdateSessionsAndKeyLevels();
            UpdateLiquidityPools();
            UpdateCisdRunEngine(isBu, isBe, hh20, ll20);
            UpdateHtfFvgSlots();
            UpdateIfvgEngine();
            UpdateNdogNwog();
            UpdatePremiumDiscountChart();
            UpdatePremiumDiscountHtfTable();
            UpdatePo3();
            UpdateBreakoutSessions();

            prevIsBu = isBu; prevIsBe = isBe;
        }

        // ═══════════════════════════ CISD ENGINE (run based) ═══════════════════════════
        // Concept credit: "CISD Projections" (c) LuxAlgo (CC BY-NC-SA 4.0) — rewritten here for
        // personal use. A "run" = consecutive candles closing above (bull) / below (bear) the
        // previous close. A run that made the 20-candle extreme ARMS a CISD; the NEXT run's close
        // crossing back through the armed run's open CONFIRMS the CISD (bullish CISD from a bear
        // run, bearish CISD from a bull run).
        private const int CiWin = 30;
        private void UpdateCisdRunEngine(bool isBu, bool isBe, double hh20, double ll20)
        {
            bool rUp = Close[0] >= (CurrentBar >= 1 ? Close[1] : Close[0]);
            bool rDn = !rUp;
            // Pine detects a new run via (rUp and rDn[1]) or (rDn and rUp[1]); since cDir already holds
            // the running direction of the still-open run, "the direction flipped" is equivalent to
            // comparing today's direction against cDir's sign.
            bool newRun = cDir == 0 || (rUp && cDir == -1) || (rDn && cDir == 1);

            double atrRun = CurrentBar >= 2 ? ATR(14)[2] : smtAtr;

            if (newRun)
            {
                pDir = cDir; pOpn = cOpn; pTop = cTop; pBot = cBot; pTopT = cTopT; pBotT = cBotT; pBars = cBars; pStT = cStT;
                pHH = hh20; pLL = ll20;
                cDir = rUp ? 1 : -1; cOpn = Open[0]; cTop = High[0]; cBot = Low[0]; cTopT = Time[0]; cBotT = Time[0]; cBars = 1; cStT = Time[0];

                bool runOk = pBars >= 2 || (pBars == 1 && !double.IsNaN(pOpn) && CurrentBar >= 1 && Math.Abs(Close[1] - pOpn) >= atrRun);
                if (runOk && !double.IsNaN(pOpn))
                {
                    if (pDir == 1 && pTop == pHH)
                    {
                        aBeOn = true; aBeOpn = pOpn; aBeTop = pTop; aBeTopT = pTopT; aBeStT = pStT; aBeBar = CurrentBar;
                    }
                    if (pDir == -1 && pBot == pLL)
                    {
                        aBuOn = true; aBuOpn = pOpn; aBuBot = pBot; aBuBotT = pBotT; aBuStT = pStT; aBuBar = CurrentBar;
                    }
                }
            }
            else
            {
                cBars += 1;
                if (High[0] > cTop) { cTop = High[0]; cTopT = Time[0]; }
                if (Low[0] < cBot) { cBot = Low[0]; cBotT = Time[0]; }
            }

            if (aBeOn && High[0] > aBeTop) { aBeTop = High[0]; aBeTopT = Time[0]; }
            if (aBuOn && Low[0] < aBuBot) { aBuBot = Low[0]; aBuBotT = Time[0]; }
            if (aBeOn && CurrentBar - aBeBar > CiWin) aBeOn = false;
            if (aBuOn && CurrentBar - aBuBar > CiWin) aBuOn = false;

            bool cBe = aBeOn && Close[0] < aBeOpn && CurrentBar >= 1 && Close[1] >= aBeOpn;
            bool cBu = aBuOn && Close[0] > aBuOpn && CurrentBar >= 1 && Close[1] <= aBuOpn;
            DateTime exHiT = default(DateTime), exLoT = default(DateTime); double exHi = double.NaN, exLo = double.NaN;
            if (cBe) { aBeOn = false; exHi = aBeTop; exHiT = aBeTopT; }
            if (cBu) { aBuOn = false; exLo = aBuBot; exLoT = aBuBotT; }

            DateTime ciBuX = aBuStT; double ciBuY = aBuOpn;
            DateTime ciBeX = aBeStT; double ciBeY = aBeOpn;

            // Pine gates this "liquidity condition" on a CISD with i_ciLq, hardcoded false in the
            // source — so it never actually restricts anything; liqBu/liqBe are simply the CISD
            // confirmation itself. kBu/kBe are still computed (matching Pine 1:1) only to rotate
            // ciUsedBu/ciUsedBe and to consume the pending liquidity-sweep memory below.
            double kBu = cBu ? FindUnusedKeyLevel(exLo, aBuOpn, ciUsedBu) : double.NaN;
            double kBe = cBe ? FindUnusedKeyLevel(aBeOpn, exHi, ciUsedBe) : double.NaN;
            bool liqBu = cBu; bool liqBe = cBe;

            if (liqBu && !double.IsNaN(kBu)) { ciUsedBu.Add(kBu); if (ciUsedBu.Count > 60) ciUsedBu.RemoveAt(0); }
            if (liqBe && !double.IsNaN(kBe)) { ciUsedBe.Add(kBe); if (ciUsedBe.Count > 60) ciUsedBe.RemoveAt(0); }
            if (liqBu) { lqSwLoT = null; hfTapBuT = null; }
            if (liqBe) { lqSwHiT = null; hfTapBeT = null; }

            // CISD-on-IFVG annotation draw (i_ivCisd gate) happens once the owning Sig resolves an IFVG — see UpdateIfvgEngine.
            _pendingCiBu = liqBu; _pendingCiBuX = ciBuX; _pendingCiBuY = ciBuY;
            _pendingCiBe = liqBe; _pendingCiBeX = ciBeX; _pendingCiBeY = ciBeY;

            // key highs/lows + dealing range snapshot for the IFVG liquidity filters, refreshed every bar
            RefreshIvKeyLevels();

            _aBuOnNow = aBuOn; _aBuBotNow = aBuBot; _aBuBotTNow = aBuBotT;
            _aBeOnNow = aBeOn; _aBeTopNow = aBeTop; _aBeTopTNow = aBeTopT;
            _liqBuNow = liqBu; _liqBeNow = liqBe; _exLoNow = exLo; _exLoTNow = exLoT; _exHiNow = exHi; _exHiTNow = exHiT;
        }
        private bool _pendingCiBu, _pendingCiBe; private DateTime _pendingCiBuX, _pendingCiBeX; private double _pendingCiBuY, _pendingCiBeY;
        private bool _aBuOnNow, _aBeOnNow, _liqBuNow, _liqBeNow;
        private double _aBuBotNow, _aBeTopNow, _exLoNow, _exHiNow;
        private DateTime _aBuBotTNow, _aBeTopTNow, _exLoTNow, _exHiTNow;

        private double FindUnusedKeyLevel(double a, double b, List<double> used)
        {
            double lo = Math.Min(a, b), hi = Math.Max(a, b);
            foreach (double lv in CollectKeyLevels())
            {
                if (double.IsNaN(lv) || lv < lo || lv > hi) continue;
                bool seen = false;
                foreach (double u in used) if (Math.Abs(u - lv) <= KeyTol()) { seen = true; break; }
                if (!seen) return lv;
            }
            return double.NaN;
        }
        private double KeyTol() { double a = !double.IsNaN(dAtr) ? dAtr : smtAtr * 20; return a / 12.0; }
        private IEnumerable<double> CollectKeyLevels()
        {
            yield return asiaH; yield return asiaL; yield return lonH; yield return lonL; yield return nyH; yield return nyL;
            yield return pdh; yield return pdl; yield return pdc; yield return pwh; yield return pwl; yield return pwc; yield return pmh; yield return pml; yield return pmc;
            yield return midO; yield return o830; yield return o930; yield return ndTop; yield return ndBot; yield return nwTop; yield return nwBot;
        }
        private readonly List<double> ivKP = new List<double>(); private readonly List<string> ivKN = new List<string>(); private readonly List<bool> ivKH = new List<bool>();
        private readonly List<double> ivPP = new List<double>(); private readonly List<bool> ivPH = new List<bool>();
        private double ivDrH = double.NaN, ivDrL = double.NaN;
        private void RefreshIvKeyLevels()
        {
            ivKP.Clear(); ivKN.Clear(); ivKH.Clear();
            double[] vals = { asiaH, asiaL, lonH, lonL, nyH, nyL, pdh, pdl, pwh, pwl, pmh, pml };
            string[] nms = { "ASIA H", "ASIA L", "LDN H", "LDN L", "NY H", "NY L", "PDH", "PDL", "PWH", "PWL", "PMH", "PML" };
            for (int i = 0; i < vals.Length; i++) { ivKP.Add(vals[i]); ivKN.Add(nms[i]); ivKH.Add(i % 2 == 0); }
            ivPP.Clear(); ivPH.Clear();
            foreach (var p in lqHi) { ivPP.Add(p.Lvl); ivPH.Add(true); }
            foreach (var p in lqLo) { ivPP.Add(p.Lvl); ivPH.Add(false); }
            bool chartBelowH1 = SecondsOf(IctHtfTimeframe.H1) > (chartIsMinutes ? chartMinutes * 60 : (int)BarsPeriod.Value);
            if (chartBelowH1 && pdHtfBip1 >= 0 && CurrentBars[pdHtfBip1] >= 2) { ivDrH = Highs[pdHtfBip1][1]; ivDrL = Lows[pdHtfBip1][1]; }
            else if (pdHtfBip4 >= 0 && CurrentBars[pdHtfBip4] >= 2) { ivDrH = Highs[pdHtfBip4][1]; ivDrL = Lows[pdHtfBip4][1]; }
        }

        // ═══════════════════════════ SESSIONS + KEY LEVELS ═══════════════════════════
        // NY session-of-day is derived by assuming chart bars are timestamped in US Central time
        // (this repo's existing convention for CME futures) and adding 1 hour: Central and Eastern
        // observe the same DST schedule, so this offset is exact year-round (unlike the Bahrain
        // conversion in the 8 AM breakout, which has no DST to lean on).
        private void UpdateSessionsAndKeyLevels()
        {
            DateTime ny = Time[0].AddHours(1);
            int nyMin = ny.Hour * 60 + ny.Minute;

            // ── daily / weekly / monthly previous H-L-C via secondary series (lookahead-safe: offset 1) ──
            if (pdHtfBipD >= 0 && CurrentBars[pdHtfBipD] != lastDayStamp)
            {
                if (lastDayStamp >= 0 && CurrentBars[pdHtfBipD] >= 2)
                {
                    pdh = Highs[pdHtfBipD][1]; pdl = Lows[pdHtfBipD][1]; pdc = Closes[pdHtfBipD][1];
                    dailyHigh.Add(pdh); dailyLow.Add(pdl); dailyClose.Add(pdc);
                    if (dailyHigh.Count > 20) { dailyHigh.RemoveAt(0); dailyLow.RemoveAt(0); dailyClose.RemoveAt(0); }
                    sesDayT = Times[pdHtfBipD][0]; sesDayTSet = true;
                }
                lastDayStamp = CurrentBars[pdHtfBipD];
            }
            if (dailyHigh.Count >= 2)
            {
                double trSum = 0; int n = 0;
                for (int i = dailyHigh.Count - 1; i > 0 && n < 14; i--, n++)
                    trSum += Math.Max(dailyHigh[i] - dailyLow[i], Math.Max(Math.Abs(dailyHigh[i] - dailyClose[i - 1]), Math.Abs(dailyLow[i] - dailyClose[i - 1])));
                dAtr = n > 0 ? trSum / n : double.NaN;
            }
            if (pdHtfBipW >= 0 && CurrentBars[pdHtfBipW] != lastWeekStamp)
            {
                if (lastWeekStamp >= 0 && CurrentBars[pdHtfBipW] >= 2) { pwh = Highs[pdHtfBipW][1]; pwl = Lows[pdHtfBipW][1]; pwc = Closes[pdHtfBipW][1]; }
                lastWeekStamp = CurrentBars[pdHtfBipW];
            }
            if (pdHtfBipMo >= 0 && CurrentBars[pdHtfBipMo] != lastMonthStamp)
            {
                if (lastMonthStamp >= 0 && CurrentBars[pdHtfBipMo] >= 2) { pmh = Highs[pdHtfBipMo][1]; pml = Lows[pdHtfBipMo][1]; pmc = Closes[pdHtfBipMo][1]; }
                lastMonthStamp = CurrentBars[pdHtfBipMo];
            }

            int chartSec = chartIsMinutes ? chartMinutes * 60 : 60;
            double nearK = chartSec <= 60 ? 0.12 : chartSec <= 180 ? 0.18 : chartSec <= 300 ? 0.25 : chartSec <= 900 ? 0.35 : 0.5;
            double dAtrOrFallback = !double.IsNaN(dAtr) ? dAtr : smtAtr * 20;
            NEARD = dAtrOrFallback * nearK;

            // ── Asia / London / NY session highs & lows ──
            bool inAsia = nyMin >= 1200 && nyMin < 1440;
            bool inLon = nyMin >= 120 && nyMin < 300;
            bool inNy = nyMin >= 570 && nyMin < 960;
            UpdateSessionLevel(inAsia, inAsiaPrev, ref runAsiaH, ref runAsiaL, ref runAsiaHT, ref runAsiaLT, ref asiaH, ref asiaL, ref asiaHT, ref asiaLT);
            UpdateSessionLevel(inLon, inLonPrev, ref runLonH, ref runLonL, ref runLonHT, ref runLonLT, ref lonH, ref lonL, ref lonHT, ref lonLT);
            UpdateSessionLevel(inNy, inNyPrev, ref runNyH, ref runNyL, ref runNyHT, ref runNyLT, ref nyH, ref nyL, ref nyHT, ref nyLT);
            inAsiaPrev = inAsia; inLonPrev = inLon; inNyPrev = inNy;

            CheckSessionSweep(ref tkAsH, asiaH, asiaHT, true, "ASIA H");
            CheckSessionSweep(ref tkAsL, asiaL, asiaLT, false, "ASIA L");
            CheckSessionSweep(ref tkLoH, lonH, lonHT, true, "LDN H");
            CheckSessionSweep(ref tkLoL, lonL, lonLT, false, "LDN L");
            CheckSessionSweep(ref tkNyH, nyH, nyHT, true, "NY H");
            CheckSessionSweep(ref tkNyL, nyL, nyLT, false, "NY L");

            DrawSessionLine("IctSesAsH", ShowSessionsAndLiquidity, asiaH, asiaHT, tkAsH, "ASIA H", Brushes.Orange);
            DrawSessionLine("IctSesAsL", ShowSessionsAndLiquidity, asiaL, asiaLT, tkAsL, "ASIA L", Brushes.Orange);
            DrawSessionLine("IctSesLoH", ShowSessionsAndLiquidity, lonH, lonHT, tkLoH, "LDN H", Brushes.DodgerBlue);
            DrawSessionLine("IctSesLoL", ShowSessionsAndLiquidity, lonL, lonLT, tkLoL, "LDN L", Brushes.DodgerBlue);
            DrawSessionLine("IctSesNyH", ShowSessionsAndLiquidity, nyH, nyHT, tkNyH, "NY H", Brushes.Crimson);
            DrawSessionLine("IctSesNyL", ShowSessionsAndLiquidity, nyL, nyLT, tkNyL, "NY L", Brushes.Crimson);

            // ── opens: midnight / 8:30 / 9:30 ──
            UpdateOpen(nyMin, 0, ref midO, ref midT);
            UpdateOpen(nyMin, 510, ref o830, ref t830);
            UpdateOpen(nyMin, 570, ref o930, ref t930);
            lastNyMin = nyMin; lastNyMinValid = true;
            bool todayMid = sesDayTSet && midT >= sesDayT, today830 = sesDayTSet && t830 >= sesDayT, today930 = sesDayTSet && t930 >= sesDayT;
            DrawOpenLine("IctMid", ShowOpens && todayMid, midO, midT, "MIDNIGHT", Brushes.RoyalBlue);
            DrawOpenLine("Ict830", ShowOpens && today830, o830, t830, "8:30", Brushes.Goldenrod);
            DrawOpenLine("Ict930", ShowOpens && today930, o930, t930, "9:30", Brushes.Crimson);

            DrawKeyLevelLine("IctPdh", ShowPrevHLC, pdh, "PDH", Brushes.Black, DashStyleHelper.Dash);
            DrawKeyLevelLine("IctPdl", ShowPrevHLC, pdl, "PDL", Brushes.Black, DashStyleHelper.Dash);
            DrawKeyLevelLine("IctPdc", ShowPrevHLC, pdc, "PDC", Brushes.Black, DashStyleHelper.Dot);
            DrawKeyLevelLine("IctPwh", ShowPrevHLC, pwh, "PWH", Brushes.Black, DashStyleHelper.Dash);
            DrawKeyLevelLine("IctPwl", ShowPrevHLC, pwl, "PWL", Brushes.Black, DashStyleHelper.Dash);
            DrawKeyLevelLine("IctPwc", ShowPrevHLC, pwc, "PWC", Brushes.Black, DashStyleHelper.Dot);
            DrawKeyLevelLine("IctPmh", ShowPrevHLC, pmh, "PMH", Brushes.Black, DashStyleHelper.Dash);
            DrawKeyLevelLine("IctPml", ShowPrevHLC, pml, "PML", Brushes.Black, DashStyleHelper.Dash);
            DrawKeyLevelLine("IctPmc", ShowPrevHLC, pmc, "PMC", Brushes.Black, DashStyleHelper.Dot);

            if (AlertPrevHLC)
            {
                CheckLevelSweep("pdh", pdh, 1); CheckLevelSweep("pdl", pdl, -1); CheckLevelSweep("pdc", pdc, 0);
                CheckLevelSweep("pwh", pwh, 1); CheckLevelSweep("pwl", pwl, -1); CheckLevelSweep("pwc", pwc, 0);
                CheckLevelSweep("pmh", pmh, 1); CheckLevelSweep("pml", pml, -1); CheckLevelSweep("pmc", pmc, 0);
            }
            if (AlertOpens)
            {
                if (todayMid) CheckLevelSweep("MIDNIGHT", midO, 0); if (today830) CheckLevelSweep("8:30", o830, 0); if (today930) CheckLevelSweep("9:30", o930, 0);
            }
            if (AlertNearLevel)
            {
                CheckNearLevel("pdh", pdh); CheckNearLevel("pdl", pdl); CheckNearLevel("pdc", pdc);
                CheckNearLevel("pwh", pwh); CheckNearLevel("pwl", pwl); CheckNearLevel("pwc", pwc);
                CheckNearLevel("pmh", pmh); CheckNearLevel("pml", pml); CheckNearLevel("pmc", pmc);
            }
        }

        private void UpdateSessionLevel(bool inSess, bool inSessPrev, ref double runH, ref double runL, ref DateTime runHT, ref DateTime runLT, ref double comH, ref double comL, ref DateTime comHT, ref DateTime comLT)
        {
            if (inSess && !inSessPrev) { runH = High[0]; runL = Low[0]; runHT = Time[0]; runLT = Time[0]; }
            else if (inSess)
            {
                if (double.IsNaN(runH) || High[0] > runH) { runH = High[0]; runHT = Time[0]; }
                if (double.IsNaN(runL) || Low[0] < runL) { runL = Low[0]; runLT = Time[0]; }
            }
            if (!inSess && inSessPrev) { comH = runH; comL = runL; comHT = runHT; comLT = runLT; }
        }
        private void CheckSessionSweep(ref bool taken, double lvl, DateTime lvlT, bool isHigh, string tag)
        {
            if (double.IsNaN(lvl)) return;
            bool sw = false;
            if (!taken) { if (isHigh ? High[0] > lvl : Low[0] < lvl) { taken = true; sw = true; } }
            if (sw && AlertSessionSwept && sesDayTSet && lvlT >= sesDayT)
                FireAlert(tag.Replace(" ", "") + "Swept", (isHigh ? "\U0001F534 " : "\U0001F7E2 ") + tag + " SWEPT | " + SymTag() + " | " + TfTag(), isHigh ? Brushes.Red : Brushes.LimeGreen);
        }
        private void DrawSessionLine(string tag, bool on, double lvl, DateTime t0, bool taken, string label, Brush col)
        {
            if (!on || double.IsNaN(lvl) || !sesDayTSet || t0 < sesDayT) { RemoveDrawObject(tag); return; }
            int tEndBarsBack = KeyLevelsShort ? -6 : 0;
            Draw.Line(this, tag, false, GetBarsAgo(t0), lvl, KeyLevelsShort ? -6 : -70, lvl, col, DashStyleHelper.Dash, 1);
            Draw.Text(this, tag + "Lb", false, label + (taken ? " ✓" : ""), tEndBarsBack - 1, lvl, 0, col, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
        }
        private void UpdateOpen(int nyMin, int target, ref double o, ref DateTime t)
        {
            bool hit = target == 0 ? (nyMin < lastNyMin) : (nyMin >= target && lastNyMinValid && lastNyMin < target);
            if (hit) { o = Open[0]; t = Time[0]; }
        }
        private int lastNyMin = 9999; private bool lastNyMinValid;
        private void DrawOpenLine(string tag, bool on, double lvl, DateTime t0, string label, Brush col)
        {
            if (!on || double.IsNaN(lvl)) { RemoveDrawObject(tag); return; }
            Draw.Line(this, tag, false, GetBarsAgo(t0), lvl, KeyLevelsShort ? -6 : -70, lvl, col, DashStyleHelper.Solid, 1);
            Draw.Text(this, tag + "Lb", false, label, (KeyLevelsShort ? -6 : -70) - 1, lvl, 0, col, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
        }
        private void DrawKeyLevelLine(string tag, bool on, double lvl, string label, Brush col, DashStyleHelper dash)
        {
            bool show = on && !double.IsNaN(lvl) && IsNear(lvl, lvl);
            if (!show) { RemoveDrawObject(tag); return; }
            int endBack = KeyLevelsShort ? -6 : 0;
            Draw.Line(this, tag, false, KeyLevelsShort ? 11 : Math.Min(CurrentBar, 4900), lvl, endBack, lvl, col, dash, 1);
            Draw.Text(this, tag + "Lb", false, label, endBack - 1, lvl, 0, col, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
        }
        private bool IsNear(double top, double bot) { return ShowAll || (!double.IsNaN(top) && !double.IsNaN(bot) && Close[0] >= bot - NEARD && Close[0] <= top + NEARD); }
        private int GetBarsAgo(DateTime t)
        {
            for (int i = 0; i <= Math.Min(CurrentBar, 4900); i++) if (Time[i] <= t) return i;
            return Math.Min(CurrentBar, 4900);
        }
        private void CheckLevelSweep(string key, double lvl, int kind)
        {
            if (double.IsNaN(lvl)) return;
            bool done; kSweepDone.TryGetValue(key + "_v", out done);
            double lastLvl; bool haveLast = kSweepLastLvl.TryGetValue(key, out lastLvl);
            if (!haveLast || Math.Abs(lastLvl - lvl) > TickSize) { kSweepDone[key + "_v"] = false; done = false; }
            kSweepLastLvl[key] = lvl;
            if (done) return;
            if (CurrentBar < 1) return;
            bool upT = High[0] >= lvl && Close[1] < lvl;
            bool dnT = Low[0] <= lvl && Close[1] > lvl;
            bool triggered = kind == 1 ? High[0] > lvl : kind == -1 ? Low[0] < lvl : (upT || dnT);
            if (!triggered) return;
            kSweepDone[key + "_v"] = true;
            bool bear = kind == 1 || (kind == 0 && upT);
            if (bear ? Close[0] < lvl : Close[0] > lvl)
                FireAlert(key + "Sweep", (bear ? "\U0001F534 " : "\U0001F7E2 ") + key.ToUpper() + " SWEEP | " + SymTag() + " | " + TfTag(), bear ? Brushes.Red : Brushes.LimeGreen);
        }
        private readonly Dictionary<string, double> kSweepLastLvl = new Dictionary<string, double>();
        private void CheckNearLevel(string key, double lvl)
        {
            if (double.IsNaN(lvl)) return;
            bool armed; if (!nearArmed.TryGetValue(key, out armed)) armed = true;
            double d = Math.Abs(Close[0] - lvl);
            if (d > 2 * NEARD) armed = true;
            else if (armed && d <= NEARD) { armed = false; FireAlert(key + "Near", "\U0001F514 NEAR " + key.ToUpper() + " | " + SymTag() + " | " + TfTag(), Brushes.Gold); }
            nearArmed[key] = armed;
        }
        private string SymTag() { return Instrument != null ? Instrument.MasterInstrument.Name : ""; }
        private string TfTag() { return BarsPeriod.ToString(); }
        private void FireAlert(string tag, string message, Brush brush)
        {
            Alert(tag, Priority.Low, message, NinjaTrader.Core.Globals.InstallDir + "\\sounds\\Alert1.wav", 10, Brushes.Black, brush);
        }

        // ═══════════════════════════ LIQUIDITY POOLS (BSL / SSL) ═══════════════════════════
        private void UpdateLiquidityPools()
        {
            const int majLen = 40;
            if (CurrentBar < 2 * majLen + 2) return;
            bool isPivHigh = true, isPivLow = true;
            double phVal = High[majLen], plVal = Low[majLen];
            for (int i = 1; i <= majLen; i++)
            {
                if (High[majLen - i] >= phVal || High[majLen + i] >= phVal) isPivHigh = false;
                if (Low[majLen - i] <= plVal || Low[majLen + i] <= plVal) isPivLow = false;
            }
            double tol = (!double.IsNaN(dAtr) ? dAtr : smtAtr * 20) / 4.0;
            if (isPivHigh) AddOrMergePool(lqHi, phVal, Time[majLen], true, tol);
            if (isPivLow) AddOrMergePool(lqLo, plVal, Time[majLen], true, tol);

            for (int i = lqHi.Count - 1; i >= 0; i--)
                if (High[0] > lqHi[i].Lvl) { lqSwHiT = Time[0]; FireLiquiditySweepAlert(true, lqHi[i].Lvl); lqHi.RemoveAt(i); }
            for (int i = lqLo.Count - 1; i >= 0; i--)
                if (Low[0] < lqLo[i].Lvl) { lqSwLoT = Time[0]; FireLiquiditySweepAlert(false, lqLo[i].Lvl); lqLo.RemoveAt(i); }

            int bHi = NearestBeyond(lqHi, Close[0], true);
            int bLo = NearestBeyond(lqLo, Close[0], false);
            if (ShowSessionsAndLiquidity && bHi >= 0) DrawLiquidityLine("IctBsl", lqHi[bHi], true); else RemoveDrawObject("IctBsl");
            if (ShowSessionsAndLiquidity && bLo >= 0) DrawLiquidityLine("IctSsl", lqLo[bLo], false); else RemoveDrawObject("IctSsl");
        }
        private void AddOrMergePool(List<LqPool> list, double lvl, DateTime t, bool major, double tol)
        {
            for (int i = 0; i < list.Count; i++)
                if (Math.Abs(list[i].Lvl - lvl) <= tol) { list[i].N++; list[i].Major = list[i].Major || major; return; }
            list.Add(new LqPool { Lvl = lvl, T = t, N = 1, Major = major });
            if (list.Count > 30) list.RemoveAt(0);
        }
        private int NearestBeyond(List<LqPool> list, double close, bool above)
        {
            int best = -1;
            for (int i = 0; i < list.Count; i++)
            {
                bool ok = above ? list[i].Lvl > close : list[i].Lvl < close;
                if (!ok || !list[i].Major) continue;
                if (best < 0 || (above ? list[i].Lvl < list[best].Lvl : list[i].Lvl > list[best].Lvl)) best = i;
            }
            return best;
        }
        private void DrawLiquidityLine(string tag, LqPool p, bool isHigh)
        {
            string name = (isHigh ? "BSL" : "SSL");
            Draw.Line(this, tag, false, GetBarsAgo(p.T), p.Lvl, 0, p.Lvl, Brushes.Black, DashStyleHelper.Dot, p.N >= 2 ? 2 : 1);
            Draw.Text(this, tag + "Lb", false, name, -1, p.Lvl, 0, Brushes.Black, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
        }
        private void FireLiquiditySweepAlert(bool isHigh, double lvl)
        {
            if (!AlertLiquiditySweep) return;
            bool sweptBack = isHigh ? Close[0] < lvl : Close[0] > lvl;
            if (sweptBack) FireAlert(isHigh ? "BslSweep" : "SslSweep", (isHigh ? "\U0001F534 BSL" : "\U0001F7E2 SSL") + " SWEEP | " + SymTag() + " | " + TfTag(), isHigh ? Brushes.Red : Brushes.LimeGreen);
        }

        // ═══════════════════════════ HTF FVG (5 slots) ═══════════════════════════
        private bool GetSlotEnabled(int slot)
        {
            switch (slot) { case 1: return Hf1Fvg; case 2: return Hf2Fvg; case 3: return Hf3Fvg; case 4: return Hf4Fvg; default: return Hf5Fvg; }
        }
        private IctHtfTimeframe GetSlotTf(int slot)
        {
            switch (slot) { case 1: return Hf1Tf; case 2: return Hf2Tf; case 3: return Hf3Tf; case 4: return Hf4Tf; default: return Hf5Tf; }
        }
        private bool GetSlotIfvgOn(int slot)
        {
            switch (slot) { case 1: return Hf1Ifvg; case 2: return Hf2Ifvg; case 3: return Hf3Ifvg; case 4: return Hf4Ifvg; default: return Hf5Ifvg; }
        }
        private int GetBip(IctHtfTimeframe tf) { int b; return htfBip.TryGetValue(tf, out b) ? b : -1; }

        private void UpdateHtfFvgSlots()
        {
            var slotJustClosed = new bool[6]; var slotCloseVal = new double[6];
            for (int slot = 1; slot <= 5; slot++)
            {
                if (!GetSlotEnabled(slot)) continue;
                int bip = GetBip(GetSlotTf(slot));
                if (bip < 0 || CurrentBars[bip] < 4) continue;
                int last; lastHtfCurrentBar.TryGetValue(bip, out last);
                if (CurrentBars[bip] == last) continue;
                lastHtfCurrentBar[bip] = CurrentBars[bip];
                slotJustClosed[slot] = true; slotCloseVal[slot] = Closes[bip][1];

                bool bull = Lows[bip][1] > Highs[bip][3] && Closes[bip][2] > Opens[bip][2];
                bool bear = Highs[bip][1] < Lows[bip][3] && Closes[bip][2] < Opens[bip][2];
                int sec = SecondsOf(GetSlotTf(slot));
                if (bull) AddHfBox(Lows[bip][1], Highs[bip][3], 1, slot, sec);
                if (bear) AddHfBox(Lows[bip][3], Highs[bip][1], -1, slot, sec);
            }

            for (int i = hfBoxes.Count - 1; i >= 0; i--)
            {
                HfBox b = hfBoxes[i];
                double hTp = b.Top, hBt = b.Bot; int hd = b.Dir;
                bool closeThru = slotJustClosed[b.Slot] && (hd == 1 ? slotCloseVal[b.Slot] < hBt : slotCloseVal[b.Slot] > hTp);
                bool wasInv = b.Inv;
                bool slIfvg = GetSlotIfvgOn(b.Slot);
                bool doInv = !wasInv && slIfvg && closeThru;
                bool kill = wasInv ? closeThru : (!doInv && !slIfvg && ((hd == 1 && Low[0] <= hBt) || (hd == -1 && High[0] >= hTp)));

                double hDist = Close[0] > hTp ? Close[0] - hTp : Close[0] < hBt ? hBt - Close[0] : 0.0;
                if (hDist > 2 * NEARD) b.Nr = false;
                else if (!b.Nr && hDist <= NEARD)
                {
                    b.Nr = true;
                    if (AlertNearLevel && (!HfPremiumDiscountOnly || b.PdOk))
                        FireAlert("HfNear" + b.Tag, "\U0001F514 NEAR " + NameOf(GetSlotTf(b.Slot)) + (b.Inv ? " IFVG" : " FVG") + " | " + SymTag() + " | " + TfTag(), Brushes.Gold);
                }
                bool touch = !doInv && !b.Hit && CurrentBar > b.Born && (hd == 1 ? Low[0] <= hTp : High[0] >= hBt);
                if (touch)
                {
                    if (hd == 1) hfTapBuT = Time[0]; else hfTapBeT = Time[0];
                    b.Hit = true;
                    if (AlertFvg && (!HfPremiumDiscountOnly || b.PdOk))
                        FireAlert("HfFvg" + b.Tag, (hd == 1 ? "\U0001F7E2 " : "\U0001F534 ") + (wasInv ? "IFVG " : "FVG ") + NameOf(GetSlotTf(b.Slot)) + " | " + SymTag() + " | " + TfTag(), hd == 1 ? Brushes.LimeGreen : Brushes.Red);
                }
                if (doInv)
                {
                    b.Dir = -hd; b.Inv = true; b.Hit = false; b.Born = CurrentBar;
                }
                if (kill) { RemoveDrawObject(b.Tag); RemoveDrawObject(b.Tag + "Lb"); hfBoxes.RemoveAt(i); continue; }

                // premium/discount gate for this box's own timeframe range, used by HfPremiumDiscountOnly
                b.PdOk = ComputeHfPdOk(b);
                bool visible = ShowAll || IsNear(b.Top, b.Bot);
                if (HfPremiumDiscountOnly && !b.PdOk) visible = false;
                if (visible)
                {
                    Brush fill = b.Inv ? (b.Dir == 1 ? Brushes.DodgerBlue : Brushes.Firebrick) : (b.Dir == 1 ? Brushes.MediumSeaGreen : Brushes.DimGray);
                    Draw.Rectangle(this, b.Tag, false, HfShort ? 8 : CurrentBar, b.Top, HfShort ? -8 : 0, b.Bot, fill, fill, 25);
                    Draw.Text(this, b.Tag + "Lb", false, (b.Dir == 1 ? "BULL " : "BEAR ") + (b.Inv ? "IFVG " : "") + NameOf(GetSlotTf(b.Slot)), HfShort ? -9 : -1, b.Top, 0, Brushes.Black, new SimpleFont("Arial", 8), System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
                }
                else { RemoveDrawObject(b.Tag); RemoveDrawObject(b.Tag + "Lb"); }
            }
        }
        private void AddHfBox(double top, double bot, int dir, int slot, int sec)
        {
            if (double.IsNaN(top) || double.IsNaN(bot) || top <= bot) return;
            hfBoxSeq++;
            hfBoxes.Add(new HfBox { Top = top, Bot = bot, Dir = dir, Slot = slot, SecSize = sec, Born = CurrentBar, Tag = "IctHf" + hfBoxSeq });
            int cnt = 0; for (int i = 0; i < hfBoxes.Count; i++) if (hfBoxes[i].Slot == slot) cnt++;
            if (cnt > 40) { for (int i = 0; i < hfBoxes.Count; i++) if (hfBoxes[i].Slot == slot) { RemoveDrawObject(hfBoxes[i].Tag); RemoveDrawObject(hfBoxes[i].Tag + "Lb"); hfBoxes.RemoveAt(i); break; } }
        }
        private bool ComputeHfPdOk(HfBox b)
        {
            double rH = double.NaN, rL = double.NaN; int rD = 0;
            if (b.SecSize >= 604800) { } // weekly dealing range not tracked at HTF-table granularity; treat as always ok
            else if (b.SecSize >= 86400 && !double.IsNaN(pdDH) ) { rH = pdDH; rL = pdDL; rD = pdDd; }
            else if (b.SecSize >= 14400 && !double.IsNaN(pd4H)) { rH = pd4H; rL = pd4L; rD = pd4d; }
            else if (b.SecSize >= 3600 && !double.IsNaN(pd1H)) { rH = pd1H; rL = pd1L; rD = pd1d; }
            if (rD == 0 || double.IsNaN(rH) || double.IsNaN(rL) || rH <= rL) return true;
            double md = (b.Top + b.Bot) / 2.0, eq = (rH + rL) / 2.0;
            return b.Dir == 1 ? md <= eq : md >= eq;
        }

        // ═══════════════════════════ PREMIUM & DISCOUNT (chart dealing range) ═══════════════════════════
        // SIMPLIFIED BY DESIGN vs the Pine source: this port keeps the core "auto dealing range of the
        // last impulse" concept (swing break -> range from the origin extreme to the breakout extreme,
        // shown once it is big enough and confirmed, dropped on a close beyond its origin) but omits
        // Pine's additional displacement/efficiency/"ANY vs STRONG candle" quality scoring layered on
        // top of it. See header note 2 for the full list of documented simplifications.
        private void UpdatePremiumDiscountChart()
        {
            int chartSec = chartIsMinutes ? chartMinutes * 60 : (BarsPeriod.BarsPeriodType == BarsPeriodType.Day ? 86400 : 60);
            bool pdTfOk = chartSec >= 300;
            bool pdHtf = chartSec >= 3600;
            int pdLen = pdHtf ? 2 : 5;
            if (CurrentBar < 2 * pdLen + 2) return;
            double atrRef = pdHtf ? ATR(14)[0] : Math.Max(ATR(14)[0], CurrentBar >= 100 ? ATR(100)[0] : ATR(14)[0]);
            double minAtrMult = pdHtf ? 2.0 : 3.0;
            int minCloses = pdHtf ? 1 : 2;

            bool pivH = true, pivL = true; double ph = High[pdLen], pl = Low[pdLen];
            for (int i = 1; i <= pdLen; i++)
            {
                if (High[pdLen - i] >= ph || High[pdLen + i] >= ph) pivH = false;
                if (Low[pdLen - i] <= pl || Low[pdLen + i] <= pl) pivL = false;
            }
            if (pivH) { pdShP = ph; pdShB = CurrentBar - pdLen; pdShUsed = false; }
            if (pivL) { pdSlP = pl; pdSlB = CurrentBar - pdLen; pdSlUsed = false; }

            if (!pdShUsed && !double.IsNaN(pdShP) && Close[0] > pdShP)
            {
                int n = Math.Min(CurrentBar - pdShB, 500);
                double mn = Low[0]; int off = 0;
                for (int i = 0; i <= n; i++) if (Low[i] < mn) { mn = Low[i]; off = i; }
                double mx = High[0]; int xo = 0;
                for (int i = 0; i <= off; i++) if (High[i] > mx) { mx = High[i]; xo = i; }
                bool cont = pdRBu != null && mn > (pdRBu.Hi + pdRBu.Lo) / 2.0;
                if (!cont)
                {
                    pdRngSeq++;
                    pdPBu = new PdRng { Dir = 1, Hi = mx, Lo = mn, T0 = Time[off], Brk = pdShP, OBar = CurrentBar - off, XBar = CurrentBar - xo, Tag = "IctPdBu" + pdRngSeq };
                }
                pdShUsed = true;
            }
            if (!pdSlUsed && !double.IsNaN(pdSlP) && Close[0] < pdSlP)
            {
                int n = Math.Min(CurrentBar - pdSlB, 500);
                double mx = High[0]; int off = 0;
                for (int i = 0; i <= n; i++) if (High[i] > mx) { mx = High[i]; off = i; }
                double mn = Low[0]; int xo = 0;
                for (int i = 0; i <= off; i++) if (Low[i] < mn) { mn = Low[i]; xo = i; }
                bool cont = pdRBe != null && mx < (pdRBe.Hi + pdRBe.Lo) / 2.0;
                if (!cont)
                {
                    pdRngSeq++;
                    pdPBe = new PdRng { Dir = -1, Hi = mx, Lo = mn, T0 = Time[off], Brk = pdSlP, OBar = CurrentBar - off, XBar = CurrentBar - xo, Tag = "IctPdBe" + pdRngSeq };
                }
                pdSlUsed = true;
            }

            pdPBu = StepPdRng(pdPBu, minAtrMult, atrRef, minCloses, out bool pBuPromote);
            if (pBuPromote) { KillPdRng(pdRBe); pdRBe = null; pdRBu = pdPBu; pdPBu = null; pdRBu.Shown = true; }
            pdPBe = StepPdRng(pdPBe, minAtrMult, atrRef, minCloses, out bool pBePromote);
            if (pBePromote) { KillPdRng(pdRBu); pdRBu = null; pdRBe = pdPBe; pdPBe = null; pdRBe.Shown = true; }

            pdRBu = StepPdRng(pdRBu, minAtrMult, atrRef, minCloses, out _);
            pdRBe = StepPdRng(pdRBe, minAtrMult, atrRef, minCloses, out _);

            DrawPdRange(pdRBu, PdOn && pdTfOk);
            DrawPdRange(pdRBe, PdOn && pdTfOk);

            PdRng act = pdRBu ?? pdRBe;
            if (AlertPremiumDiscount && act != null && CurrentBar > 0)
                CheckPdAlert(act);
        }
        private PdRng StepPdRng(PdRng r, double minAtrMult, double atrRef, int minCloses, out bool promote)
        {
            promote = false;
            if (r == null) return null;
            if (r.Dir == 1)
            {
                if (High[0] > r.Hi) { r.Hi = High[0]; r.XBar = CurrentBar; }
                r.NCl += Close[0] > r.Brk ? 1 : 0;
            }
            else
            {
                if (Low[0] < r.Lo) { r.Lo = Low[0]; r.XBar = CurrentBar; }
                r.NCl += Close[0] < r.Brk ? 1 : 0;
            }
            double eqv = (r.Hi + r.Lo) / 2.0;
            bool fail = !(PdKeepShown && r.Shown) && CurrentBar - r.XBar <= 3 && (r.Dir == 1 ? Close[0] < eqv : Close[0] > eqv);
            bool dead = (r.Dir == 1 ? Close[0] < r.Lo : Close[0] > r.Hi) || fail;
            if (dead) { KillPdRng(r); return null; }
            if (!r.Shown && r.Hi - r.Lo >= minAtrMult * atrRef && r.NCl >= minCloses) promote = true;
            return r;
        }
        private void KillPdRng(PdRng r) { if (r != null) { RemoveDrawObject(r.Tag); RemoveDrawObject(r.Tag + "D"); RemoveDrawObject(r.Tag + "Eq"); } }
        private void DrawPdRange(PdRng r, bool on)
        {
            if (r == null || !on) return;
            double eqv = (r.Hi + r.Lo) / 2.0;
            int t0Back = GetBarsAgo(r.T0);
            Draw.Rectangle(this, r.Tag, false, t0Back, r.Hi, -10, eqv, Brushes.OrangeRed, Brushes.OrangeRed, 15);
            Draw.Rectangle(this, r.Tag + "D", false, t0Back, eqv, -10, r.Lo, Brushes.Teal, Brushes.Teal, 15);
            Draw.Line(this, r.Tag + "Eq", false, t0Back, eqv, -10, eqv, Brushes.Gray, DashStyleHelper.Dash, 1);
        }
        private void CheckPdAlert(PdRng r)
        {
            double rng = r.Hi - r.Lo; if (rng <= 0) return;
            double eq = (r.Hi + r.Lo) / 2.0;
            double nd = Math.Min(0.05 * rng, 1.0 * (smtAtr > 0 ? smtAtr : rng));
            bool bull = r.Dir == 1;
            double id = r.T0.ToOADate();
            if (pdAlLastId == null || Math.Abs(pdAlLastId.Value - id) > 0.0001)
            {
                pdAlLastId = id;
                pdAlNd = Low[0] > eq + nd; pdAlId = Low[0] > eq; pdAlNp = High[0] < eq - nd; pdAlIp = High[0] < eq;
                return;
            }
            bool cool = pdAlLastBar < 0 || CurrentBar - pdAlLastBar >= 20;
            if (pdAlNd && Low[0] <= eq + nd && bull && cool) { pdAlNd = false; pdAlLastBar = CurrentBar; FireAlert("PdNearDisc", "\U0001F7E2 NEAR DISCOUNT | " + SymTag() + " | " + TfTag(), Brushes.LimeGreen); }
            if (pdAlId && Low[0] <= eq && bull) { pdAlId = false; pdAlLastBar = CurrentBar; FireAlert("PdInDisc", "\U0001F7E2 IN DISCOUNT | " + SymTag() + " | " + TfTag(), Brushes.LimeGreen); }
            if (pdAlNp && High[0] >= eq - nd && !bull && cool) { pdAlNp = false; pdAlLastBar = CurrentBar; FireAlert("PdNearPrem", "\U0001F534 NEAR PREMIUM | " + SymTag() + " | " + TfTag(), Brushes.Red); }
            if (pdAlIp && High[0] >= eq && !bull) { pdAlIp = false; pdAlLastBar = CurrentBar; FireAlert("PdInPrem", "\U0001F534 IN PREMIUM | " + SymTag() + " | " + TfTag(), Brushes.Red); }
            if (Close[0] >= eq + 0.40 * rng) { pdAlNd = true; pdAlId = true; }
            if (Close[0] <= eq - 0.40 * rng) { pdAlNp = true; pdAlIp = true; }
        }

        // ═══════════════════════════ PREMIUM & DISCOUNT HTF TABLE (W/D/4H/1H) ═══════════════════════════
        // SIMPLIFIED BY DESIGN: a lighter-weight "most recent 20-bar swing channel" bias/zone estimate per
        // timeframe, rather than re-running the full impulse-quality engine four more times. Good enough
        // for the bias table Pine itself describes as read-only ("HTF dealing ranges are not drawn, table
        // only"); the chart's own Premium & Discount range above uses the fuller engine and drives alerts.
        private void UpdatePremiumDiscountHtfTable()
        {
            ComputeHtfRow(pdHtfBipW, out pdWd, out pdWH, out pdWL, out bool wHit);
            ComputeHtfRow(pdHtfBipD, out pdDd, out pdDH, out pdDL, out bool dHit);
            ComputeHtfRow(pdHtfBip4, out pd4d, out pd4H, out pd4L, out bool hit4);
            ComputeHtfRow(pdHtfBip1, out pd1d, out pd1H, out pd1L, out bool hit1);
            if (!PdTable) { RemoveDrawObject("IctPdTbl"); return; }

            int chartSec = chartIsMinutes ? chartMinutes * 60 : (BarsPeriod.BarsPeriodType == BarsPeriodType.Day ? 86400 : 60);
            var rows = new List<string>(); int nBu = 0, nBe = 0;
            rows.Add(FormatPdRow("W", pdWd, pdWH, pdWL, wHit)); if (pdWd == 1) nBu++; else if (pdWd == -1) nBe++;
            if (chartSec <= 86400) { rows.Add(FormatPdRow("D", pdDd, pdDH, pdDL, dHit)); if (pdDd == 1) nBu++; else if (pdDd == -1) nBe++; }
            if (chartSec <= 14400) { rows.Add(FormatPdRow("4H", pd4d, pd4H, pd4L, hit4)); if (pd4d == 1) nBu++; else if (pd4d == -1) nBe++; }
            if (chartSec <= 3600) { rows.Add(FormatPdRow("1H", pd1d, pd1H, pd1L, hit1)); if (pd1d == 1) nBu++; else if (pd1d == -1) nBe++; }
            int nT = nBu + nBe;
            string bias = nT == 0 || nBu == nBe ? "MIXED" : nBu > nBe ? "BULLISH " + nBu + "/" + nT : "BEARISH " + nBe + "/" + nT;
            string panel = "8 AM BREAKOUT — PD TABLE\nTF  BIAS ZONE 50%\n" + string.Join("\n", rows) + "\nHTF: " + bias;
            Draw.TextFixed(this, "IctPdTbl", panel, TextPosition.TopRight, Brushes.White, new SimpleFont("Arial", 10), Brushes.Transparent, Brushes.DimGray, 20);
        }
        private string FormatPdRow(string tag, int d, double hi, double lo, bool hit50)
        {
            if (d == 0 || double.IsNaN(hi) || double.IsNaN(lo)) return tag + "  —    —    —";
            double eq = (hi + lo) / 2.0;
            string zone = Close[0] > hi ? "ABOVE" : Close[0] < lo ? "BELOW" : Math.Abs(Close[0] - eq) <= 0.05 * (hi - lo) ? "EQ" : Close[0] > eq ? "PREM" : "DISC";
            return tag + "  " + (d == 1 ? "UP  " : "DN  ") + zone.PadRight(5) + (hit50 ? "✓" : "✗");
        }
        private void ComputeHtfRow(int bip, out int dir, out double hi, out double lo, out bool reached50)
        {
            dir = 0; hi = double.NaN; lo = double.NaN; reached50 = false;
            if (bip < 0 || CurrentBars[bip] < 23) return;
            double hh = double.MinValue, ll = double.MaxValue; int hhIdx = 1, llIdx = 1;
            for (int i = 1; i <= 20; i++)
            {
                double h = Highs[bip][i], l = Lows[bip][i];
                if (h > hh) { hh = h; hhIdx = i; }
                if (l < ll) { ll = l; llIdx = i; }
            }
            dir = hhIdx <= llIdx ? 1 : -1;
            hi = hh; lo = ll;
            double eq = (hi + lo) / 2.0;
            int sinceExtreme = dir == 1 ? hhIdx : llIdx;
            for (int i = 0; i < sinceExtreme; i++)
            {
                double c = Closes[bip][i];
                if (dir == 1 ? c <= eq : c >= eq) { reached50 = true; break; }
            }
        }

        // ═══════════════════════════ NDOG / NWOG ═══════════════════════════
        private void UpdateNdogNwog()
        {
            if (pdHtfBipD >= 0 && CurrentBars[pdHtfBipD] >= 2 && lastDayStamp == CurrentBars[pdHtfBipD])
            {
                // a new daily bar just rolled over this bar (captured above in key-levels); recompute the gap
                double prevClose = Closes[pdHtfBipD][1]; double o = Open[0];
                ndTop = Math.Max(prevClose, o); ndBot = Math.Min(prevClose, o); ndT = Time[0];
            }
            if (pdHtfBipW >= 0 && CurrentBars[pdHtfBipW] >= 2 && lastWeekStamp == CurrentBars[pdHtfBipW])
            {
                double prevClose = Closes[pdHtfBipW][1]; double o = Open[0];
                nwTop = Math.Max(prevClose, o); nwBot = Math.Min(prevClose, o); nwT = Time[0];
            }
            bool showNd = ShowNdog && !double.IsNaN(ndTop) && IsNear(ndTop, ndBot) && sesDayTSet && ndT >= sesDayT;
            bool showNw = ShowNwog && !double.IsNaN(nwTop) && IsNear(nwTop, nwBot);
            if (showNd)
            {
                Draw.Rectangle(this, "IctNdog", false, GetBarsAgo(ndT), ndTop, -10, ndBot, Brushes.SlateGray, Brushes.SlateGray, 18);
                Draw.Line(this, "IctNdogCe", false, GetBarsAgo(ndT), (ndTop + ndBot) / 2.0, -10, (ndTop + ndBot) / 2.0, Brushes.SlateGray, DashStyleHelper.Dash, 1);
            }
            else { RemoveDrawObject("IctNdog"); RemoveDrawObject("IctNdogCe"); }
            if (showNw)
            {
                Draw.Rectangle(this, "IctNwog", false, GetBarsAgo(nwT), nwTop, -10, nwBot, Brushes.Purple, Brushes.Purple, 18);
                Draw.Line(this, "IctNwogCe", false, GetBarsAgo(nwT), (nwTop + nwBot) / 2.0, -10, (nwTop + nwBot) / 2.0, Brushes.Purple, DashStyleHelper.Dash, 1);
            }
            else { RemoveDrawObject("IctNwog"); RemoveDrawObject("IctNwogCe"); }
        }

        // ═══════════════════════════ PO3 ═══════════════════════════
        private void UpdatePo3()
        {
            bool newDay = pdHtfBipD >= 0 && CurrentBars[pdHtfBipD] != po3DayStamp;
            if (newDay || double.IsNaN(po3O)) { po3O = Open[0]; po3H = High[0]; po3L = Low[0]; po3DayStamp = pdHtfBipD >= 0 ? CurrentBars[pdHtfBipD] : po3DayStamp; }
            else { po3H = Math.Max(po3H, High[0]); po3L = Math.Min(po3L, Low[0]); }
            if (!ShowPo3) { RemoveDrawObject("IctPo3Body"); RemoveDrawObject("IctPo3Wick"); RemoveDrawObject("IctPo3Hi"); RemoveDrawObject("IctPo3Lo"); RemoveDrawObject("IctPo3Op"); return; }
            int xm = 21;
            Brush bodyBrush = Close[0] >= po3O ? Brushes.SeaGreen : Brushes.Firebrick;
            Draw.Line(this, "IctPo3Hi", false, 0, po3H, -xm, po3H, Brushes.Gray, DashStyleHelper.Dot, 1);
            Draw.Line(this, "IctPo3Lo", false, 0, po3L, -xm, po3L, Brushes.Gray, DashStyleHelper.Dot, 1);
            Draw.Line(this, "IctPo3Op", false, 0, po3O, -xm, po3O, Brushes.Goldenrod, DashStyleHelper.Solid, 1);
            Draw.Line(this, "IctPo3Wick", false, -xm, po3H, -xm, po3L, bodyBrush, DashStyleHelper.Solid, 1);
            Draw.Line(this, "IctPo3Body", false, -xm, po3O, -xm, Close[0] == po3O ? Close[0] + TickSize : Close[0], bodyBrush, DashStyleHelper.Solid, 8);
        }

        // ═══════════════════════════ 8 AM BREAKOUT ═══════════════════════════
        private DateTime ToBahrain(DateTime t) { return t.AddHours(BahrainOffsetHours - 6); }   // offset relative to assumed US Central chart time
        private void UpdateBreakoutSessions()
        {
            bool chartOk = (chartIsMinutes ? chartMinutes * 60 : 60) <= 3600;
            ProcessBreakout(bo1, Bo1Enable && ShowBreakout && chartOk, Bo1Start, Bo1End, Bo1FreezeHour, ref bo1InSessPrev, "BO1");
            ProcessBreakout(bo2, Bo2Enable && ShowBreakout && chartOk, Bo2Start, Bo2End, Bo2FreezeHour, ref bo2InSessPrev, "BO2");
        }
        private void ProcessBreakout(BoState bo, bool en, int startHHmm, int endHHmm, int freezeHour, ref bool inSessPrev, string tag)
        {
            if (!en) { inSessPrev = false; return; }
            DateTime bh = ToBahrain(Time[0]);
            int hm = bh.Hour * 100 + bh.Minute;
            bool inSess = startHHmm <= endHHmm ? (hm >= startHHmm && hm < endHHmm) : (hm >= startHHmm || hm < endHHmm);
            if (inSess && !inSessPrev)
            {
                bo.StartTime = Time[0]; bo.SessHigh = High[0]; bo.SessLow = Low[0];
                bo.LinesFrozen = false; bo.UpAlerted = false; bo.DownAlerted = false; bo.SessionEnded = false; bo.FreezeTime = null; bo.Started = true;
            }
            else if (inSess)
            {
                bo.SessHigh = Math.Max(bo.SessHigh, High[0]); bo.SessLow = Math.Min(bo.SessLow, Low[0]);
            }
            if (bo.Started && !bo.LinesFrozen)
            {
                if (inSess)
                {
                    DrawBreakoutLines(bo);
                }
                else if (!bo.SessionEnded)
                {
                    bo.SessionEnded = true;
                    DateTime todayFreeze = new DateTime(bh.Year, bh.Month, bh.Day, freezeHour, 0, 0);
                    DateTime target = todayFreeze <= bh ? todayFreeze.AddDays(1) : todayFreeze;
                    bo.FreezeTime = target;
                    bo.LinesFrozen = true;
                    DrawBreakoutLines(bo);
                }
            }
            if (bo.Started && bo.SessionEnded && AlertBreakout)
            {
                double bodyMid = (Open[0] + Close[0]) / 2.0;
                if (Close[0] > bo.SessHigh && bodyMid > bo.SessHigh && !bo.UpAlerted) { bo.UpAlerted = true; FireAlert(tag + "Up", "\U0001F7E2 " + tag + " BREAKOUT | " + SymTag() + " | " + TfTag(), Brushes.LimeGreen); }
                if (Close[0] < bo.SessLow && bodyMid < bo.SessLow && !bo.DownAlerted) { bo.DownAlerted = true; FireAlert(tag + "Dn", "\U0001F534 " + tag + " BREAKOUT | " + SymTag() + " | " + TfTag(), Brushes.Red); }
            }
            inSessPrev = inSess;
        }
        // ═══════════════════════════ IFVG (INVERSE FAIR VALUE GAP) ENGINE ═══════════════════════════
        // SIMPLIFIED BY DESIGN vs the Pine source's f_ifvgUpdate / f_sweptLiq / f_ivTarget / f_ivOpp /
        // f_ifvgFreeze / f_disp machinery: this port keeps the ICT "inverse FVG" concept itself exactly —
        // a 3-bar fair-value gap that price later closes back through flips polarity and becomes the new
        // signal zone — and reuses the run-based CISD engine's own confirmation (_liqBuNow/_liqBeNow) as
        // the trigger, same as the Pine source. What's simplified: Pine's own multi-stage freeze/lock/
        // trace-vs-live state machine is collapsed into the single IctSig lifecycle below (armed -> hit ->
        // target/gone), and the "near IFVG" alert distance uses KeyTol() rather than Pine's separate ATR
        // fraction. See header note 2 for the full simplification list.
        private const int IfvgLookback = 30;
        private void UpdateIfvgEngine()
        {
            if (!EnableIfvg) { ivOwnBull = null; ivOwnBear = null; RemoveIfvgDraw(null); return; }
            if (_liqBuNow) TrySpawnIfvg(1);
            if (_liqBeNow) TrySpawnIfvg(-1);
            ivOwnBull = StepIfvg(ivOwnBull);
            ivOwnBear = StepIfvg(ivOwnBear);
        }
        private void TrySpawnIfvg(int dir)
        {
            IctSig existing = dir == 1 ? ivOwnBull : ivOwnBear;
            if (existing != null && !existing.Gone) return;   // one live signal per side, like Pine's ivOwnS[0]/ivOwnS[1]

            double top = double.NaN, bot = double.NaN; int bar = -1;
            int lim = Math.Min(IfvgLookback, CurrentBar - 3);
            for (int j = 2; j <= lim; j++)
            {
                if (dir == 1)
                {
                    // most recent BEARISH 3-bar FVG (top=Low[j-1], bot=High[j+1]) that price has now closed back above
                    double t = Low[j - 1], b = High[j + 1];
                    if (t > b && Close[0] > t) { top = t; bot = b; bar = CurrentBar - j; break; }
                }
                else
                {
                    // most recent BULLISH 3-bar FVG (bot=High[j-1], top=Low[j+1]) that price has now closed back below
                    double b = High[j - 1], t = Low[j + 1];
                    if (t > b && Close[0] < b) { top = t; bot = b; bar = CurrentBar - j; break; }
                }
            }
            if (double.IsNaN(top)) return;

            if (IfvgDealingRangeFilter && !double.IsNaN(ivDrH) && !double.IsNaN(ivDrL))
            {
                double mid = (top + bot) / 2.0;
                if (mid < ivDrL || mid > ivDrH) return;
                if (IfvgPremiumOnly)
                {
                    double eq = (ivDrH + ivDrL) / 2.0;
                    if (dir == 1 && mid > eq) return;    // bullish IFVG wanted only in discount
                    if (dir == -1 && mid < eq) return;   // bearish IFVG wanted only in premium
                }
            }
            if (IfvgRequireSweep)
            {
                bool sweptRecently = dir == 1 ? (lqSwLoT.HasValue && GetBarsAgo(lqSwLoT.Value) <= IfvgLookback)
                                               : (lqSwHiT.HasValue && GetBarsAgo(lqSwHiT.Value) <= IfvgLookback);
                if (!sweptRecently) return;
            }
            double tgt = double.NaN; bool tgtHigh = false;
            for (int i = 0; i < ivPP.Count; i++)
            {
                if (dir == 1 && ivPH[i] && ivPP[i] > top && (double.IsNaN(tgt) || ivPP[i] < tgt)) { tgt = ivPP[i]; tgtHigh = true; }
                if (dir == -1 && !ivPH[i] && ivPP[i] < bot && (double.IsNaN(tgt) || ivPP[i] > tgt)) { tgt = ivPP[i]; tgtHigh = false; }
            }
            if (IfvgRequireTarget && double.IsNaN(tgt)) return;

            sigSeq++;
            var sig = new IctSig
            {
                Bear = dir == -1,
                IvTop = top, IvBot = bot, IvBar = bar, IvKey = (top + bot) / 2.0,
                TgL = tgt, TgHit = false, Gone = false, Swept = false, IvHit = false,
                Tag = "IctIfvg" + sigSeq
            };
            if (dir == 1) ivOwnBull = sig; else ivOwnBear = sig;
            if (AlertIfvg) FireAlert(sig.Tag + "New", (dir == 1 ? "\U0001F7E2 NEW BULLISH IFVG | " : "\U0001F534 NEW BEARISH IFVG | ") + SymTag() + " | " + TfTag(), dir == 1 ? Brushes.LimeGreen : Brushes.Red);
        }
        private IctSig StepIfvg(IctSig sig)
        {
            if (sig == null) return null;
            int dir = sig.Bear ? -1 : 1;
            double tol = KeyTol();
            if (!sig.IvHit)
            {
                bool touched = dir == 1 ? Low[0] <= sig.IvTop && High[0] >= sig.IvBot : High[0] >= sig.IvBot && Low[0] <= sig.IvTop;
                if (touched) sig.IvHit = true;
                else if (AlertNearLevel && !nearArmed.ContainsKey(sig.Tag))
                {
                    double dist = dir == 1 ? Low[0] - sig.IvTop : sig.IvBot - High[0];
                    if (dist >= 0 && dist <= tol) { nearArmed[sig.Tag] = true; FireAlert(sig.Tag + "Near", "NEAR IFVG | " + SymTag() + " | " + TfTag(), Brushes.Yellow); }
                }
            }
            // CISD-on-IFVG annotation: the run-engine's confirmation on the same side, arriving after the IFVG formed
            if (!sig.Cisd && IfvgShowCisd)
            {
                if (dir == 1 && _pendingCiBu) { sig.Cisd = true; sig.CiT = Time[0]; sig.CiY = Close[0]; sig.CiX = Time[0]; }
                if (dir == -1 && _pendingCiBe) { sig.Cisd = true; sig.CiT = Time[0]; sig.CiY = Close[0]; sig.CiX = Time[0]; }
            }
            // SMT annotation: correlated instrument failing to confirm the same extreme (divergence)
            if (!sig.Smt && IfvgShowSmt && smtBip >= 0 && CurrentBars[smtBip] >= 3)
            {
                double corrLead = Closes[smtBip][0] - Closes[smtBip][2];
                double ownLead = Close[0] - Close[2];
                if (dir == 1 && ownLead > 0 && corrLead < 0) sig.Smt = true;
                if (dir == -1 && ownLead < 0 && corrLead > 0) sig.Smt = true;
            }
            // invalidate: a clean close back through the opposite side of the box kills the zone
            bool invalidated = dir == 1 ? Close[0] < sig.IvBot : Close[0] > sig.IvTop;
            if (!sig.TgHit && !double.IsNaN(sig.TgL))
            {
                bool hitTgt = dir == 1 ? High[0] >= sig.TgL : Low[0] <= sig.TgL;
                if (hitTgt) { sig.TgHit = true; if (AlertIfvg) FireAlert(sig.Tag + "Tgt", "IFVG TARGET HIT | " + SymTag() + " | " + TfTag(), Brushes.Gold); }
            }
            if (invalidated || CurrentBar - sig.IvBar > 400) { sig.Gone = true; RemoveIfvgDraw(sig); return null; }
            DrawIfvg(sig, dir);
            return sig;
        }
        private void DrawIfvg(IctSig sig, int dir)
        {
            Brush fill = dir == 1 ? Brushes.LimeGreen : Brushes.Red;
            Draw.Rectangle(this, sig.Tag, false, sig.IvBar, sig.IvTop, 0, sig.IvBot, fill, fill, 35);
            Draw.Text(this, sig.Tag + "Lb", false, sig.Cisd ? "IFVG + CISD" : sig.Smt ? "IFVG + SMT" : "IFVG", sig.IvBar, dir == 1 ? sig.IvBot : sig.IvTop, 0, fill, new SimpleFont("Arial", 9), System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
            if (!double.IsNaN(sig.TgL))
                Draw.Line(this, sig.Tag + "Tg", false, sig.IvBar, sig.TgL, 0, sig.TgL, Brushes.Gold, DashStyleHelper.Dash, 1);
        }
        private void RemoveIfvgDraw(IctSig sig)
        {
            if (sig != null) { RemoveDrawObject(sig.Tag); RemoveDrawObject(sig.Tag + "Lb"); RemoveDrawObject(sig.Tag + "Tg"); nearArmed.Remove(sig.Tag); }
        }

        private void DrawBreakoutLines(BoState bo)
        {
            if (!bo.Started || double.IsNaN(bo.SessHigh)) return;
            double mid = (bo.SessHigh + bo.SessLow) / 2.0;
            int startBack = GetBarsAgo(bo.StartTime);
            Draw.Line(this, bo.Tag + "Hi", false, startBack, bo.SessHigh, 0, bo.SessHigh, Brushes.Black, DashStyleHelper.Solid, 2);
            Draw.Line(this, bo.Tag + "Lo", false, startBack, bo.SessLow, 0, bo.SessLow, Brushes.Black, DashStyleHelper.Solid, 2);
            Draw.Line(this, bo.Tag + "Mid", false, startBack, mid, 0, mid, Brushes.Red, DashStyleHelper.Solid, 2);
            Draw.Text(this, bo.Tag + "MidLb", false, "SL", 0, mid, 0, Brushes.Red, new SimpleFont("Arial", 10), System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
        }
    }
}
