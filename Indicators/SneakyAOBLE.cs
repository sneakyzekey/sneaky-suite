#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using D2D = SharpDX.Direct2D1;
using DW = SharpDX.DirectWrite;
#endregion

// SneakyAOBLE - NinjaTrader 8 port of "Apex Order Block & Liquidity Engine [AOBLE]" (Pine v6, Michael_Fx_Trader).
// Everything (zones, lines, labels, dashboard) is stored as data and painted in OnRender, so there are no
// thousands of Draw objects and loading history stays fast.
// Runs on closed bars (the Pine default "Confirmed-Bar Logic"); the intrabar mode and the developing preview
// are not ported. Pine's ta.pivothigh/low tie rules and volume come from the exchange feed, so a few zones
// can differ slightly from TradingView.
namespace NinjaTrader.NinjaScript
{
    public enum AobleTheme { Dark, Light, Neon, Minimal }
    public enum AobleLabelMode { ZoneStart, RightEdge, Hidden }
    public enum AobleTextSize { Tiny, Small, Normal, Large }
    public enum AobleLineStyle { Solid, Dashed, Dotted }
    public enum AobleBoundary { FullRange, Body, Hybrid }
    public enum AobleObSource { Swing, Internal, Both }
    public enum AobleInval { CandleClose, Wick }
    public enum AobleMitigated { KeepFaded, Remove }
    public enum AobleTolMode { Atr, Ticks }
    public enum AobleFvgFill { Fade, Remove }
    public enum AobleStructSrc { Swing, Internal }
    public enum AobleTfType { Minute, Day, Week }
    public enum AobleEntry { ConfirmationClose, OrderBlockRetest, FvgRetest, UserPrice }
    public enum AobleStop { BeyondSwing, BeyondOrderBlock, Atr }
    public enum AobleTp { RiskReward, StructureLiquidity }
    public enum AobleTrigger { SweepOrObTouch, SweepOnly, ObTouchOnly }
    public enum AobleConf { MssOrDisp, MssOnly }
    public enum AobleDashPos { TopRight, TopLeft, BottomRight, BottomLeft }
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakyAOBLE : Indicator
    {
        // ------------------------------------------------------------------ model
        private class Zone
        {
            public double Top, Bot; public int Left, Origin, Dir, State, Kind, Born, End = -1; public bool Retested, Ghost; public string Text; public int Slot;
        }
        private class Liq
        {
            public double Price; public int Touches, LastBar, FirstBar, End = -1, Slot; public bool IsBSL, Shown, Swept, Htf, Faded;
        }
        private class Seg { public int X0, X1; public double Price; public bool Bull, Internal; public string Text; }
        private class Mark { public int Bar; public double Price; public string Text; public bool Above, Bsl; }
        private class Struct { public int Trend; public double HPrice = double.NaN, LPrice = double.NaN; public int HBar, LBar; public bool HOpen, LOpen; }
        private class Setup
        {
            public int Dir, State, Bar0, TpHit, CloseBar = -1; public double Entry, Sl; public double[] Tps = new double[5];
            public string EntryTag = "", SlTag = ""; public string[] TpTag = new string[] { "", "", "", "", "" };
        }
        private class HtfState
        {
            public bool On; public AobleTfType Type; public int Val; public int Bip; public double Atr = double.NaN, SumTr, LH = double.NaN, LL = double.NaN; public bool OH, OL; public string Label;
            public long SeenBT, SeenST, SeenBsl, SeenSsl;
        }

        // event slots (set on the bar where they happen)
        private const int E_BULL_OB = 0, E_BEAR_OB = 1, E_BULL_BR = 2, E_BEAR_BR = 3, E_BULL_BOS = 4, E_BEAR_BOS = 5, E_BULL_MSS = 6, E_BEAR_MSS = 7,
            E_BSL_SWEEP = 8, E_SSL_SWEEP = 9, E_BULL_FVG = 10, E_BEAR_FVG = 11, E_BUY = 12, E_SELL = 13, E_ENTRY = 14, E_SL = 15, E_TP = 16,
            E_BULL_TOUCH = 17, E_BEAR_TOUCH = 18, E_NEW_BSL = 19, E_NEW_SSL = 20, NEV = 21;
        private static readonly string[] EvName = { "New bullish order block", "New bearish order block", "Bullish breaker confirmed", "Bearish breaker confirmed",
            "Bullish BOS", "Bearish BOS", "Bullish MSS / CHoCH", "Bearish MSS / CHoCH", "Buy-side liquidity traded through", "Sell-side liquidity traded through",
            "Bullish FVG created", "Bearish FVG created", "Confirmed BUY setup", "Confirmed SELL setup", "Pending entry filled", "Stop loss reached", "Target reached",
            "Price entered bullish OB", "Price entered bearish OB", "New buy-side liquidity pool", "New sell-side liquidity pool" };

        private List<Zone> obs = new List<Zone>(), brs = new List<Zone>(), fvgs = new List<Zone>(), mtfZ = new List<Zone>(), ghosts = new List<Zone>();
        private List<Liq> liqs = new List<Liq>(), sweptLiq = new List<Liq>();
        private List<Seg> segs = new List<Seg>();
        private List<Mark> marks = new List<Mark>();
        private List<Setup> setups = new List<Setup>();
        private Setup cur;
        private Struct sSwing = new Struct(), sInt = new Struct();
        private int[] ev = new int[NEV];
        private int sweepBsl = -1, sweepSsl = -1, trigBullOb = -1, trigBearOb = -1, trigBullFvg = -1, trigBearFvg = -1;
        private double brkLogBar = -1, brkLogPx = double.NaN;
        private string lastStruct = "None";
        private int usedBullTrig = -1, usedBearTrig = -1, lastSetupBar = -100000;
        private double atrV = double.NaN, atrSum; private int atrCount;
        private double[] relVolHist = new double[3];
        private double volMa, relVol, relVolMax3; private bool volAvail, volSpike;
        private HtfState[] htf = new HtfState[5];
        private List<int> htfBip = new List<int>();           // BarsInProgress -> slot (index 0 = BarsInProgress 1)
        private StreamWriter rec;
        private Series<double>[] outS;

        // signal series for strategies / signal panels
        [Browsable(false), XmlIgnore] public Series<double> SetupSignal { get { Update(); return outS[0]; } }   // +1 buy setup, -1 sell setup (bar where it was confirmed)
        [Browsable(false), XmlIgnore] public Series<double> SetupEntry { get { Update(); return outS[1]; } }
        [Browsable(false), XmlIgnore] public Series<double> SetupStop { get { Update(); return outS[2]; } }
        [Browsable(false), XmlIgnore] public Series<double> SetupTarget1 { get { Update(); return outS[3]; } }
        [Browsable(false), XmlIgnore] public Series<double> SetupTarget2 { get { Update(); return outS[4]; } }
        [Browsable(false), XmlIgnore] public Series<double> SetupTarget3 { get { Update(); return outS[5]; } }
        [Browsable(false), XmlIgnore] public Series<double> SetupAtr { get { Update(); return outS[6]; } }

        #region Properties
        [Range(1, 200)]
        [Display(Name = "ATR length", GroupName = "1. General", Order = 0)]
        public int AtrLen { get; set; }

        [Display(Name = "Zone label placement", GroupName = "1. General", Order = 1)]
        public AobleLabelMode LblMode { get; set; }

        [Display(Name = "Zone label text size", GroupName = "1. General", Order = 2)]
        public AobleTextSize ZoneTxtSize { get; set; }

        [Range(0, 60)]
        [Display(Name = "Right-edge label offset (bars; needs chart right margin)", GroupName = "1. General", Order = 3)]
        public int RightPad { get; set; }

        [Range(0, 120)]
        [Display(Name = "Setup marker distance from bar (pixels)", GroupName = "1. General", Order = 4)]
        public int MarkerPx { get; set; }

        [Display(Name = "Display BOS / CHoCH / MSS", GroupName = "2. Market Structure", Order = 5)]
        public bool ShowStructure { get; set; }

        [Display(Name = "Show swing structure", GroupName = "2. Market Structure", Order = 6)]
        public bool ShowSwingStruct { get; set; }

        [Range(2, 50)]
        [Display(Name = "Swing pivot length", GroupName = "2. Market Structure", Order = 7)]
        public int SwingLen { get; set; }

        [Display(Name = "Swing line style", GroupName = "2. Market Structure", Order = 8)]
        public AobleLineStyle SwingStyle { get; set; }

        [Display(Name = "Swing label size", GroupName = "2. Market Structure", Order = 9)]
        public AobleTextSize SwingSize { get; set; }

        [Display(Name = "Show internal structure", GroupName = "2. Market Structure", Order = 10)]
        public bool ShowIntStruct { get; set; }

        [Range(1, 50)]
        [Display(Name = "Internal pivot length", GroupName = "2. Market Structure", Order = 11)]
        public int IntLen { get; set; }

        [Display(Name = "Internal line style", GroupName = "2. Market Structure", Order = 12)]
        public AobleLineStyle IntStyle { get; set; }

        [Display(Name = "Internal label size", GroupName = "2. Market Structure", Order = 13)]
        public AobleTextSize IntSize { get; set; }

        [Range(0.1, 10.0)]
        [Display(Name = "MSS / setup displacement (ATR multiple)", GroupName = "2. Market Structure", Order = 14)]
        public double DispMult { get; set; }

        [Display(Name = "Display bullish order blocks", GroupName = "3. Bullish Order Blocks", Order = 15)]
        public bool ShowBullOB { get; set; }

        [Range(1, 50)]
        [Display(Name = "Maximum bullish order blocks", GroupName = "3. Bullish Order Blocks", Order = 16)]
        public int MaxBullOB { get; set; }

        [Display(Name = "Bullish boundary mode", GroupName = "3. Bullish Order Blocks", Order = 17)]
        public AobleBoundary BullBoundary { get; set; }

        [Range(0, 100)]
        [Display(Name = "Bullish hybrid wick inclusion (%)", GroupName = "3. Bullish Order Blocks", Order = 18)]
        public int BullHybrid { get; set; }

        [Range(0.1, 10.0)]
        [Display(Name = "Minimum bullish displacement (ATR multiple)", GroupName = "3. Bullish Order Blocks", Order = 19)]
        public double BullDisp { get; set; }

        [Display(Name = "Order block source (both directions)", GroupName = "3. Bullish Order Blocks", Order = 20)]
        public AobleObSource ObSource { get; set; }

        [Range(1, 40)]
        [Display(Name = "Origin candle search bars (both directions)", GroupName = "3. Bullish Order Blocks", Order = 21)]
        public int ObScan { get; set; }

        [Display(Name = "Use candle cluster (both directions)", GroupName = "3. Bullish Order Blocks", Order = 22)]
        public bool UseCluster { get; set; }

        [Range(2, 6)]
        [Display(Name = "Maximum cluster candles", GroupName = "3. Bullish Order Blocks", Order = 23)]
        public int ClusterMax { get; set; }

        [Display(Name = "Display bearish order blocks", GroupName = "4. Bearish Order Blocks", Order = 24)]
        public bool ShowBearOB { get; set; }

        [Range(1, 50)]
        [Display(Name = "Maximum bearish order blocks", GroupName = "4. Bearish Order Blocks", Order = 25)]
        public int MaxBearOB { get; set; }

        [Display(Name = "Bearish boundary mode", GroupName = "4. Bearish Order Blocks", Order = 26)]
        public AobleBoundary BearBoundary { get; set; }

        [Range(0, 100)]
        [Display(Name = "Bearish hybrid wick inclusion (%)", GroupName = "4. Bearish Order Blocks", Order = 27)]
        public int BearHybrid { get; set; }

        [Range(0.1, 10.0)]
        [Display(Name = "Minimum bearish displacement (ATR multiple)", GroupName = "4. Bearish Order Blocks", Order = 28)]
        public double BearDisp { get; set; }

        [Display(Name = "Display breaker blocks", GroupName = "5. Breaker Blocks", Order = 29)]
        public bool ShowBreakers { get; set; }

        [Display(Name = "Invalidation rule", GroupName = "5. Breaker Blocks", Order = 30)]
        public AobleInval BrkInvalid { get; set; }

        [Range(0.0, 5.0)]
        [Display(Name = "Minimum breakout strength (ATR multiple)", GroupName = "5. Breaker Blocks", Order = 31)]
        public double BrkStrength { get; set; }

        [Range(1, 30)]
        [Display(Name = "Maximum breakers per direction", GroupName = "5. Breaker Blocks", Order = 32)]
        public int MaxBreakers { get; set; }

        [Display(Name = "Highlight breaker retests", GroupName = "5. Breaker Blocks", Order = 33)]
        public bool BrkRetest { get; set; }

        [Display(Name = "Keep broken order block history", GroupName = "5. Breaker Blocks", Order = 34)]
        public bool KeepGhost { get; set; }

        [Range(20, 100000)]
        [Display(Name = "Breaker expiry (bars)", GroupName = "5. Breaker Blocks", Order = 35)]
        public int BrkExpire { get; set; }

        [Display(Name = "Fully mitigated order blocks", GroupName = "5. Breaker Blocks", Order = 36)]
        public AobleMitigated ObMitMode { get; set; }

        [Display(Name = "Display liquidity", GroupName = "6. Liquidity", Order = 37)]
        public bool ShowLiq { get; set; }

        [Range(2, 50)]
        [Display(Name = "Liquidity pivot length", GroupName = "6. Liquidity", Order = 38)]
        public int LiqLen { get; set; }

        [Display(Name = "Equal level tolerance mode", GroupName = "6. Liquidity", Order = 39)]
        public AobleTolMode LiqTolMode { get; set; }

        [Range(0.0, 5.0)]
        [Display(Name = "Tolerance (ATR multiple)", GroupName = "6. Liquidity", Order = 40)]
        public double LiqTolAtr { get; set; }

        [Range(0, 10000)]
        [Display(Name = "Tolerance (ticks)", GroupName = "6. Liquidity", Order = 41)]
        public int LiqTolTicks { get; set; }

        [Range(1, 6)]
        [Display(Name = "Minimum touches", GroupName = "6. Liquidity", Order = 42)]
        public int LiqMinTouches { get; set; }

        [Range(1, 500)]
        [Display(Name = "Minimum separation between swings (bars)", GroupName = "6. Liquidity", Order = 43)]
        public int LiqMinSep { get; set; }

        [Range(2, 60)]
        [Display(Name = "Maximum active liquidity levels", GroupName = "6. Liquidity", Order = 44)]
        public int MaxLiq { get; set; }

        [Display(Name = "Show sweep labels", GroupName = "6. Liquidity", Order = 45)]
        public bool ShowSweepLbl { get; set; }

        [Display(Name = "Delete consumed levels", GroupName = "6. Liquidity", Order = 46)]
        public bool DelConsumed { get; set; }

        [Range(1, 4)]
        [Display(Name = "Liquidity line width", GroupName = "6. Liquidity", Order = 47)]
        public int LiqWidth { get; set; }

        [Display(Name = "Liquidity line style", GroupName = "6. Liquidity", Order = 48)]
        public AobleLineStyle LiqStyle { get; set; }

        [Display(Name = "Display fair value gaps", GroupName = "7. Fair Value Gaps", Order = 49)]
        public bool ShowFvg { get; set; }

        [Display(Name = "Minimum gap mode", GroupName = "7. Fair Value Gaps", Order = 50)]
        public AobleTolMode FvgMode { get; set; }

        [Range(0.0, 10.0)]
        [Display(Name = "Minimum gap (ATR multiple)", GroupName = "7. Fair Value Gaps", Order = 51)]
        public double FvgMinAtr { get; set; }

        [Range(0, 10000)]
        [Display(Name = "Minimum gap (ticks)", GroupName = "7. Fair Value Gaps", Order = 52)]
        public int FvgMinTicks { get; set; }

        [Range(1, 50)]
        [Display(Name = "Maximum FVGs per direction", GroupName = "7. Fair Value Gaps", Order = 53)]
        public int MaxFvg { get; set; }

        [Display(Name = "Show midpoint (consequent encroachment)", GroupName = "7. Fair Value Gaps", Order = 54)]
        public bool FvgMid { get; set; }

        [Display(Name = "When fully filled", GroupName = "7. Fair Value Gaps", Order = 55)]
        public AobleFvgFill FvgOnFill { get; set; }

        [Display(Name = "Show FVG labels", GroupName = "7. Fair Value Gaps", Order = 56)]
        public bool FvgLabel { get; set; }

        [Display(Name = "Display premium / discount", GroupName = "8. Premium and Discount", Order = 57)]
        public bool ShowPD { get; set; }

        [Display(Name = "Dealing range source", GroupName = "8. Premium and Discount", Order = 58)]
        public AobleStructSrc PdSource { get; set; }

        [Display(Name = "Show range high / low / equilibrium lines", GroupName = "8. Premium and Discount", Order = 59)]
        public bool ShowRangeHL { get; set; }

        [Range(50, 99)]
        [Display(Name = "Zone transparency", GroupName = "8. Premium and Discount", Order = 60)]
        public int PdTransp { get; set; }

        [Display(Name = "Enable multi-timeframe confluence", GroupName = "9. Multi-Timeframe", Order = 61)]
        public bool ShowMTF { get; set; }

        [Display(Name = "Show higher-timeframe order blocks", GroupName = "9. Multi-Timeframe", Order = 62)]
        public bool MtfOB { get; set; }

        [Display(Name = "Show higher-timeframe liquidity", GroupName = "9. Multi-Timeframe", Order = 63)]
        public bool MtfLiq { get; set; }

        [Range(1, 40)]
        [Display(Name = "Maximum higher-timeframe objects (each type)", GroupName = "9. Multi-Timeframe", Order = 64)]
        public int MaxMTF { get; set; }

        [Display(Name = "Timeframe 1 on", GroupName = "9. Multi-Timeframe", Order = 65)]
        public bool Tf1On { get; set; }

        [Display(Name = "Timeframe 1 type", GroupName = "9. Multi-Timeframe", Order = 66)]
        public AobleTfType Tf1Type { get; set; }

        [Range(1, 10000)]
        [Display(Name = "Timeframe 1 value", GroupName = "9. Multi-Timeframe", Order = 67)]
        public int Tf1Val { get; set; }

        [Display(Name = "Timeframe 2 on", GroupName = "9. Multi-Timeframe", Order = 68)]
        public bool Tf2On { get; set; }

        [Display(Name = "Timeframe 2 type", GroupName = "9. Multi-Timeframe", Order = 69)]
        public AobleTfType Tf2Type { get; set; }

        [Range(1, 10000)]
        [Display(Name = "Timeframe 2 value", GroupName = "9. Multi-Timeframe", Order = 70)]
        public int Tf2Val { get; set; }

        [Display(Name = "Timeframe 3 on", GroupName = "9. Multi-Timeframe", Order = 71)]
        public bool Tf3On { get; set; }

        [Display(Name = "Timeframe 3 type", GroupName = "9. Multi-Timeframe", Order = 72)]
        public AobleTfType Tf3Type { get; set; }

        [Range(1, 10000)]
        [Display(Name = "Timeframe 3 value", GroupName = "9. Multi-Timeframe", Order = 73)]
        public int Tf3Val { get; set; }

        [Display(Name = "Timeframe 4 on", GroupName = "9. Multi-Timeframe", Order = 74)]
        public bool Tf4On { get; set; }

        [Display(Name = "Timeframe 4 type", GroupName = "9. Multi-Timeframe", Order = 75)]
        public AobleTfType Tf4Type { get; set; }

        [Range(1, 10000)]
        [Display(Name = "Timeframe 4 value", GroupName = "9. Multi-Timeframe", Order = 76)]
        public int Tf4Val { get; set; }

        [Display(Name = "Timeframe 5 on", GroupName = "9. Multi-Timeframe", Order = 77)]
        public bool Tf5On { get; set; }

        [Display(Name = "Timeframe 5 type", GroupName = "9. Multi-Timeframe", Order = 78)]
        public AobleTfType Tf5Type { get; set; }

        [Range(1, 10000)]
        [Display(Name = "Timeframe 5 value", GroupName = "9. Multi-Timeframe", Order = 79)]
        public int Tf5Val { get; set; }

        [Display(Name = "Enable trade planning", GroupName = "10. Entry and Exit Planning", Order = 80)]
        public bool ShowSetups { get; set; }

        [Display(Name = "Entry method", GroupName = "10. Entry and Exit Planning", Order = 81)]
        public AobleEntry EntryMethod { get; set; }

        [Range(0.0, 1000000.0)]
        [Display(Name = "User entry price", GroupName = "10. Entry and Exit Planning", Order = 82)]
        public double UserEntry { get; set; }

        [Display(Name = "Stop loss mode", GroupName = "10. Entry and Exit Planning", Order = 83)]
        public AobleStop SlMode { get; set; }

        [Range(0.0, 10.0)]
        [Display(Name = "Stop safety buffer (ATR multiple)", GroupName = "10. Entry and Exit Planning", Order = 84)]
        public double SlBuf { get; set; }

        [Range(0.2, 20.0)]
        [Display(Name = "ATR stop distance (ATR multiple)", GroupName = "10. Entry and Exit Planning", Order = 85)]
        public double SlAtrMult { get; set; }

        [Range(1.0, 50.0)]
        [Display(Name = "Maximum risk (ATR multiple)", GroupName = "10. Entry and Exit Planning", Order = 86)]
        public double MaxRiskAtr { get; set; }

        [Display(Name = "Target mode", GroupName = "10. Entry and Exit Planning", Order = 87)]
        public AobleTp TpMode { get; set; }

        [Range(0.1, 50.0)]
        [Display(Name = "Target 1 (R multiple)", GroupName = "10. Entry and Exit Planning", Order = 88)]
        public double Rr1 { get; set; }

        [Range(0.1, 50.0)]
        [Display(Name = "Target 2 (R multiple)", GroupName = "10. Entry and Exit Planning", Order = 89)]
        public double Rr2 { get; set; }

        [Range(0.1, 50.0)]
        [Display(Name = "Target 3 (R multiple)", GroupName = "10. Entry and Exit Planning", Order = 90)]
        public double Rr3 { get; set; }

        [Range(0.1, 50.0)]
        [Display(Name = "Target 4 (R multiple)", GroupName = "10. Entry and Exit Planning", Order = 91)]
        public double Rr4 { get; set; }

        [Range(0.1, 50.0)]
        [Display(Name = "Target 5 (R multiple)", GroupName = "10. Entry and Exit Planning", Order = 92)]
        public double Rr5 { get; set; }

        [Display(Name = "Setup trigger", GroupName = "10. Entry and Exit Planning", Order = 93)]
        public AobleTrigger TrigMode { get; set; }

        [Display(Name = "Setup confirmation", GroupName = "10. Entry and Exit Planning", Order = 94)]
        public AobleConf ConfMode { get; set; }

        [Display(Name = "Require recent FVG", GroupName = "10. Entry and Exit Planning", Order = 95)]
        public bool ReqFvg { get; set; }

        [Display(Name = "Longs in discount / shorts in premium only", GroupName = "10. Entry and Exit Planning", Order = 96)]
        public bool PdFilter { get; set; }

        [Range(1, 500)]
        [Display(Name = "Trigger-to-confirmation window (bars)", GroupName = "10. Entry and Exit Planning", Order = 97)]
        public int SetupWin { get; set; }

        [Range(0, 500)]
        [Display(Name = "Cooldown bars between setups", GroupName = "10. Entry and Exit Planning", Order = 98)]
        public int Cooldown { get; set; }

        [Range(1, 1000)]
        [Display(Name = "Pending entry expiry (bars)", GroupName = "10. Entry and Exit Planning", Order = 99)]
        public int PendExpire { get; set; }

        [Display(Name = "Dynamic update while pending", GroupName = "10. Entry and Exit Planning", Order = 100)]
        public bool DynSetup { get; set; }

        [Range(1, 10)]
        [Display(Name = "Setups kept on chart", GroupName = "10. Entry and Exit Planning", Order = 101)]
        public int MaxSetups { get; set; }

        [Display(Name = "Show setup marker label", GroupName = "10. Entry and Exit Planning", Order = 102)]
        public bool SetupMarker { get; set; }

        [Range(2, 500)]
        [Display(Name = "Volume lookback", GroupName = "11. Volume Confirmation", Order = 103)]
        public int VolLen { get; set; }

        [Range(0.5, 20.0)]
        [Display(Name = "Relative volume threshold", GroupName = "11. Volume Confirmation", Order = 104)]
        public double VolThr { get; set; }

        [Display(Name = "Require volume spike for order blocks", GroupName = "11. Volume Confirmation", Order = 105)]
        public bool VolObFilter { get; set; }

        [Display(Name = "Require volume spike for MSS", GroupName = "11. Volume Confirmation", Order = 106)]
        public bool VolMssFilter { get; set; }

        [Display(Name = "Require volume spike for setups", GroupName = "11. Volume Confirmation", Order = 107)]
        public bool VolSetupFilter { get; set; }

        [Display(Name = "Display dashboard", GroupName = "12. Dashboard", Order = 108)]
        public bool ShowDash { get; set; }

        [Display(Name = "Position", GroupName = "12. Dashboard", Order = 109)]
        public AobleDashPos DashPos { get; set; }

        [Display(Name = "Text size", GroupName = "12. Dashboard", Order = 110)]
        public AobleTextSize DashSize { get; set; }

        [Display(Name = "Compact mode", GroupName = "12. Dashboard", Order = 111)]
        public bool DashCompact { get; set; }

        [Display(Name = "Theme", GroupName = "13. Visual Theme", Order = 112)]
        public AobleTheme Theme { get; set; }

        [Range(40, 98)]
        [Display(Name = "Zone fill transparency", GroupName = "13. Visual Theme", Order = 113)]
        public int FillTransp { get; set; }

        [Display(Name = "Show zone borders", GroupName = "13. Visual Theme", Order = 114)]
        public bool ShowBorder { get; set; }

        [Range(1, 4)]
        [Display(Name = "Line width", GroupName = "13. Visual Theme", Order = 115)]
        public int LineW { get; set; }

        [Display(Name = "Show zone midpoint lines (order blocks / breakers)", GroupName = "13. Visual Theme", Order = 116)]
        public bool ShowMid { get; set; }

        [Display(Name = "Layered glow effect", GroupName = "13. Visual Theme", Order = 117)]
        public bool GlowOn { get; set; }

        [Range(60, 99)]
        [Display(Name = "Glow transparency", GroupName = "13. Visual Theme", Order = 118)]
        public int GlowTransp { get; set; }

        [Display(Name = "Raise 'price entered zone' events", GroupName = "14. Alerts", Order = 119)]
        public bool AlertOnTouch { get; set; }

        [Display(Name = "Fire NinjaTrader alerts (real time only)", GroupName = "14. Alerts", Order = 120)]
        public bool AlertsOn { get; set; }

        [Display(Name = "Display recent zones only", GroupName = "15. Performance", Order = 121)]
        public bool RecentOnly { get; set; }

        [Display(Name = "Render debug", Description = "Yellow diagnostic line at the top-left of the chart, painted before every early return in OnRender.", GroupName = "99. Diagnostics", Order = 0)]
        public bool RenderDebug { get; set; }

        [Range(100, 1000000)]
        [Display(Name = "Recent window (bars)", GroupName = "15. Performance", Order = 122)]
        public int RecentBars { get; set; }

        [Range(0, 60)]
        [Display(Name = "Maximum faded / swept history objects", GroupName = "15. Performance", Order = 123)]
        public int MaxGhosts { get; set; }

        [Display(Name = "Record confirmed setups to CSV (SneakyExport folder)", GroupName = "16. Recorder", Order = 124)]
        public bool Record { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Sneaky AOBLE";
                Description = "Apex Order Block & Liquidity Engine port: structure, order blocks, breakers, liquidity, FVG, premium/discount, MTF, trade planner.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                IsAutoScale = false;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;   // every zone/line/label this indicator shows lives only in its own
                                                     // in-memory lists and is painted by its own OnRender -- there are
                                                     // no persistent Draw.* objects behind it. With this left true,
                                                     // NinjaTrader stops calling OnBarUpdate/OnRender while the chart
                                                     // isn't the focused window (e.g. while a Properties dialog has
                                                     // focus), so the overlays go blank until something forces a
                                                     // repaint, then vanish again the next time the chart loses focus.
                                                     // Same fix already applied to SneakyIctFxProIfvgSystem_v1_0_0.cs.
                AtrLen = 14;
                LblMode = AobleLabelMode.ZoneStart;
                ZoneTxtSize = AobleTextSize.Small;
                RightPad = 6;
                MarkerPx = 16;
                ShowStructure = true;
                ShowSwingStruct = true;
                SwingLen = 5;
                SwingStyle = AobleLineStyle.Solid;
                SwingSize = AobleTextSize.Small;
                ShowIntStruct = true;
                IntLen = 3;
                IntStyle = AobleLineStyle.Dashed;
                IntSize = AobleTextSize.Tiny;
                DispMult = 1.0;
                ShowBullOB = true;
                MaxBullOB = 10;
                BullBoundary = AobleBoundary.FullRange;
                BullHybrid = 50;
                BullDisp = 1.0;
                ObSource = AobleObSource.Swing;
                ObScan = 10;
                UseCluster = false;
                ClusterMax = 3;
                ShowBearOB = true;
                MaxBearOB = 10;
                BearBoundary = AobleBoundary.FullRange;
                BearHybrid = 50;
                BearDisp = 1.0;
                ShowBreakers = true;
                BrkInvalid = AobleInval.CandleClose;
                BrkStrength = 0.1;
                MaxBreakers = 6;
                BrkRetest = true;
                KeepGhost = true;
                BrkExpire = 400;
                ObMitMode = AobleMitigated.KeepFaded;
                ShowLiq = true;
                LiqLen = 5;
                LiqTolMode = AobleTolMode.Atr;
                LiqTolAtr = 0.15;
                LiqTolTicks = 30;
                LiqMinTouches = 2;
                LiqMinSep = 5;
                MaxLiq = 15;
                ShowSweepLbl = true;
                DelConsumed = false;
                LiqWidth = 1;
                LiqStyle = AobleLineStyle.Dashed;
                ShowFvg = true;
                FvgMode = AobleTolMode.Atr;
                FvgMinAtr = 0.2;
                FvgMinTicks = 20;
                MaxFvg = 10;
                FvgMid = true;
                FvgOnFill = AobleFvgFill.Fade;
                FvgLabel = false;
                ShowPD = true;
                PdSource = AobleStructSrc.Swing;
                ShowRangeHL = true;
                PdTransp = 92;
                ShowMTF = false;
                MtfOB = true;
                MtfLiq = true;
                MaxMTF = 10;
                Tf1On = false;
                Tf1Type = AobleTfType.Minute;
                Tf1Val = 15;
                Tf2On = true;
                Tf2Type = AobleTfType.Minute;
                Tf2Val = 60;
                Tf3On = true;
                Tf3Type = AobleTfType.Minute;
                Tf3Val = 240;
                Tf4On = true;
                Tf4Type = AobleTfType.Day;
                Tf4Val = 1;
                Tf5On = false;
                Tf5Type = AobleTfType.Week;
                Tf5Val = 1;
                ShowSetups = true;
                EntryMethod = AobleEntry.ConfirmationClose;
                UserEntry = 0.0;
                SlMode = AobleStop.BeyondSwing;
                SlBuf = 0.2;
                SlAtrMult = 1.5;
                MaxRiskAtr = 6.0;
                TpMode = AobleTp.RiskReward;
                Rr1 = 1.0;
                Rr2 = 2.0;
                Rr3 = 3.0;
                Rr4 = 4.0;
                Rr5 = 5.0;
                TrigMode = AobleTrigger.SweepOrObTouch;
                ConfMode = AobleConf.MssOrDisp;
                ReqFvg = false;
                PdFilter = false;
                SetupWin = 20;
                Cooldown = 10;
                PendExpire = 30;
                DynSetup = false;
                MaxSetups = 3;
                SetupMarker = true;
                VolLen = 20;
                VolThr = 1.5;
                VolObFilter = false;
                VolMssFilter = false;
                VolSetupFilter = false;
                ShowDash = true;
                DashPos = AobleDashPos.TopRight;
                DashSize = AobleTextSize.Small;
                DashCompact = false;
                Theme = AobleTheme.Dark;
                FillTransp = 85;
                ShowBorder = true;
                LineW = 1;
                ShowMid = false;
                GlowOn = false;
                GlowTransp = 90;
                AlertOnTouch = true;
                AlertsOn = false;
                RecentOnly = false;
                RenderDebug = true;
                RecentBars = 1500;
                MaxGhosts = 15;
                Record = true;
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Setup Signal");      // 0  +1 buy setup / -1 sell setup
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "MSS Signal");        // 1  +1 bullish MSS/CHoCH, -1 bearish
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "BOS Signal");        // 2  +1 bullish BOS, -1 bearish BOS
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Sweep Signal");      // 3  +1 sell-side swept & rejected (bullish), -1 buy-side swept & rejected
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "OB Touch Signal");   // 4  +1 price entered bullish OB, -1 bearish OB
                AddPlot(new Stroke(Brushes.DeepSkyBlue, 2), PlotStyle.TriangleUp, "Setup Long");  // 5  marker
                AddPlot(new Stroke(Brushes.Magenta, 2), PlotStyle.TriangleDown, "Setup Short");   // 6  marker
            }
            else if (State == State.Configure)
            {
                htfBip.Clear();
                if (ShowMTF)
                {
                    bool[] on = { Tf1On, Tf2On, Tf3On, Tf4On, Tf5On };
                    AobleTfType[] ty = { Tf1Type, Tf2Type, Tf3Type, Tf4Type, Tf5Type };
                    int[] vl = { Tf1Val, Tf2Val, Tf3Val, Tf4Val, Tf5Val };
                    for (int i = 0; i < 5; i++)
                    {
                        if (!on[i]) continue;
                        AddDataSeries(ty[i] == AobleTfType.Minute ? BarsPeriodType.Minute : ty[i] == AobleTfType.Day ? BarsPeriodType.Day : BarsPeriodType.Week, vl[i]);
                        htfBip.Add(i);
                    }
                }
            }
            else if (State == State.DataLoaded)
            {
                outS = new Series<double>[7];
                for (int i = 0; i < 7; i++) outS[i] = new Series<double>(this);
                bool[] on = { Tf1On, Tf2On, Tf3On, Tf4On, Tf5On };
                AobleTfType[] ty = { Tf1Type, Tf2Type, Tf3Type, Tf4Type, Tf5Type };
                int[] vl = { Tf1Val, Tf2Val, Tf3Val, Tf4Val, Tf5Val };
                for (int i = 0; i < 5; i++)
                {
                    htf[i] = new HtfState();
                    htf[i].On = ShowMTF && on[i]; htf[i].Type = ty[i]; htf[i].Val = vl[i]; htf[i].Label = TfLabel(ty[i], vl[i]);
                    htf[i].Bip = htfBip.IndexOf(i) + 1;
                }
                if (Record)
                {
                    try
                    {
                        string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SneakyExport");
                        Directory.CreateDirectory(dir);
                        string name = "aoble_setups_" + Instrument.FullName.Replace(" ", "_") + "_" + BarsPeriod.Value + ".csv";
                        rec = new StreamWriter(new FileStream(Path.Combine(dir, name), FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                        rec.AutoFlush = true;
                        rec.WriteLine("time,type,dir,entry,sl,tp1,tp2,tp3,tp4,tp5,atr,info");
                    }
                    catch { rec = null; }
                }
            }
            else if (State == State.Terminated)
            {
                try { if (rec != null) { rec.Dispose(); rec = null; } } catch { }
                DisposeBrushes();
            }
        }

        private static string TfLabel(AobleTfType t, int v)
        {
            if (t == AobleTfType.Day) return "D" + v;
            if (t == AobleTfType.Week) return "W" + v;
            if (v < 60) return "M" + v;
            if (v % 60 == 0) return "H" + (v / 60);
            return v + "m";
        }

        // ------------------------------------------------------------------ helpers
        private static bool Na(double v) { return double.IsNaN(v); }
        private static double Nz(double v) { return double.IsNaN(v) ? 0 : v; }
        private double PivHigh(int L)
        {
            if (CurrentBar < 2 * L) return double.NaN;
            double v = High[L];
            for (int k = 0; k < L; k++) if (High[k] >= v) return double.NaN;
            for (int k = L + 1; k <= 2 * L; k++) if (High[k] > v) return double.NaN;
            return v;
        }
        private double PivLow(int L)
        {
            if (CurrentBar < 2 * L) return double.NaN;
            double v = Low[L];
            for (int k = 0; k < L; k++) if (Low[k] <= v) return double.NaN;
            for (int k = L + 1; k <= 2 * L; k++) if (Low[k] < v) return double.NaN;
            return v;
        }
        private double Hl2() { return (High[0] + Low[0]) / 2.0; }

        private void Trim(List<Zone> arr, int dir, int maxN)
        {
            int cnt = 0;
            foreach (Zone z in arr) if (dir == 0 || z.Dir == dir) cnt++;
            while (cnt > maxN)
            {
                int idx = -1;
                for (int i = 0; i < arr.Count; i++) if (dir == 0 || arr[i].Dir == dir) { idx = i; break; }
                if (idx < 0) break;
                arr.RemoveAt(idx); cnt--;
            }
        }

        // ------------------------------------------------------------------ order blocks
        private bool FindOB(bool bull, int maxK, double wFrac, out double zt, out double zb, out int originIdx, out int leftIdx)
        {
            zt = zb = double.NaN; originIdx = leftIdx = 0;
            bool found = false; int k0 = 1; int lim = Math.Max(maxK, 1);
            for (int k = 1; k <= lim && k <= CurrentBar; k++)
            {
                bool opp = bull ? Close[k] < Open[k] : Close[k] > Open[k];
                if (opp) { found = true; k0 = k; break; }
            }
            if (!found) return false;
            double hi = High[k0], lo = Low[k0];
            double bTop = bull ? Open[k0] : Close[k0];
            double bBot = bull ? Close[k0] : Open[k0];
            int kEarliest = k0;
            if (UseCluster)
            {
                int j = k0 + 1, cnt = 1;
                while (cnt < ClusterMax && j <= CurrentBar)
                {
                    bool opp2 = bull ? Close[j] < Open[j] : Close[j] > Open[j];
                    if (!opp2) break;
                    hi = Math.Max(hi, High[j]); lo = Math.Min(lo, Low[j]);
                    bTop = Math.Max(bTop, bull ? Open[j] : Close[j]);
                    bBot = Math.Min(bBot, bull ? Close[j] : Open[j]);
                    kEarliest = j; j++; cnt++;
                }
            }
            zt = bTop + (hi - bTop) * wFrac;
            zb = bBot - (bBot - lo) * wFrac;
            originIdx = CurrentBar - k0;
            leftIdx = CurrentBar - kEarliest;
            return true;
        }

        private void AddOB(bool bull, double t, double b, int idx, int left)
        {
            int d = bull ? 1 : -1;
            double tt = t, bb = b;
            if (tt - bb < TickSize) tt = bb + TickSize;
            bool dup = false;
            foreach (Zone z in obs)
            {
                if (z.Dir != d || dup) continue;
                if (z.Origin == idx) dup = true;
                else if (z.State < 2)
                {
                    double ov = Math.Min(z.Top, tt) - Math.Max(z.Bot, bb);
                    double h = Math.Min(z.Top - z.Bot, tt - bb);
                    if (ov > 0.5 * h) dup = true;
                }
            }
            if (dup) return;
            Zone nz = new Zone { Top = tt, Bot = bb, Left = left, Origin = idx, Dir = d, State = 0, Kind = 0, Born = CurrentBar, Text = bull ? "BULLISH ORDER BLOCK" : "BEARISH ORDER BLOCK" };
            obs.Add(nz);
            Trim(obs, d, bull ? MaxBullOB : MaxBearOB);
            ev[bull ? E_BULL_OB : E_BEAR_OB] = 1;
        }

        private double Body3(bool bull)
        {
            double m = double.MinValue;
            for (int k = 0; k < 3 && k <= CurrentBar; k++) m = Math.Max(m, bull ? Close[k] - Open[k] : Open[k] - Close[k]);
            return m;
        }

        private void TryOB(bool bull, int pivBar, bool obAllowed)
        {
            bool en = bull ? ShowBullOB : ShowBearOB;
            if (!en || !obAllowed || Na(atrV)) return;
            double body3 = Body3(bull);
            double dmul = bull ? BullDisp : BearDisp;
            bool volOk = !VolObFilter || !volAvail || relVolMax3 >= VolThr;
            if (body3 >= dmul * atrV && volOk)
            {
                double w = bull ? WFrac(BullBoundary, BullHybrid) : WFrac(BearBoundary, BearHybrid);
                int span = Math.Max(CurrentBar - pivBar, 1);
                double zt, zb; int oIdx, oLeft;
                if (FindOB(bull, Math.Min(ObScan, span), w, out zt, out zb, out oIdx, out oLeft)) AddOB(bull, zt, zb, oIdx, oLeft);
            }
        }
        private static double WFrac(AobleBoundary b, int hybrid) { return b == AobleBoundary.FullRange ? 1.0 : b == AobleBoundary.Body ? 0.0 : hybrid / 100.0; }

        private void BreakOB(Zone z)
        {
            bool wasBull = z.Dir == 1;
            if (KeepGhost && MaxGhosts > 0)
            {
                z.End = CurrentBar; z.Ghost = true; z.Text = "";
                ghosts.Add(z);
                while (ghosts.Count > MaxGhosts) ghosts.RemoveAt(0);
            }
            if (ShowBreakers)
            {
                int nd = wasBull ? -1 : 1;
                Zone bz = new Zone { Top = z.Top, Bot = z.Bot, Left = CurrentBar, Origin = z.Origin, Dir = nd, State = 0, Kind = 1, Born = CurrentBar, Text = wasBull ? "BEARISH BREAKER BLOCK" : "BULLISH BREAKER BLOCK" };
                brs.Add(bz);
                Trim(brs, nd, MaxBreakers);
                ev[wasBull ? E_BEAR_BR : E_BULL_BR] = 1;
            }
        }

        private void ManageOB()
        {
            for (int i = obs.Count - 1; i >= 0; i--)
            {
                Zone z = obs[i]; bool rm = false;
                if (RecentOnly && CurrentBar - z.Origin > RecentBars) rm = true;
                else if (CurrentBar > z.Born)
                {
                    bool bull = z.Dir == 1;
                    bool entered = bull ? Low[0] <= z.Top : High[0] >= z.Bot;
                    bool tagged = bull ? Low[0] <= z.Bot : High[0] >= z.Top;
                    double refPx = BrkInvalid == AobleInval.Wick ? (bull ? Low[0] : High[0]) : Close[0];
                    double strength = BrkStrength * Nz(atrV);
                    bool inv = bull ? refPx < z.Bot - strength : refPx > z.Top + strength;
                    if (inv) { BreakOB(z); rm = true; }
                    else
                    {
                        if (z.State == 0 && entered)
                        {
                            z.State = 1;
                            if (bull) { ev[E_BULL_TOUCH] = AlertOnTouch ? 1 : 0; trigBullOb = CurrentBar; }
                            else { ev[E_BEAR_TOUCH] = AlertOnTouch ? 1 : 0; trigBearOb = CurrentBar; }
                        }
                        if (z.State < 2 && tagged)
                        {
                            z.State = 2;
                            if (ObMitMode == AobleMitigated.Remove) rm = true;
                        }
                    }
                }
                if (rm) obs.RemoveAt(i);
            }
        }

        private void ManageBR()
        {
            for (int i = brs.Count - 1; i >= 0; i--)
            {
                Zone z = brs[i]; bool rm = false;
                if ((RecentOnly && CurrentBar - z.Origin > RecentBars) || CurrentBar - z.Born > BrkExpire) rm = true;
                else if (CurrentBar > z.Born)
                {
                    bool bear = z.Dir == -1;
                    double refPx = BrkInvalid == AobleInval.Wick ? (bear ? High[0] : Low[0]) : Close[0];
                    double strength = BrkStrength * Nz(atrV);
                    bool dead = bear ? refPx > z.Top + strength : refPx < z.Bot - strength;
                    if (dead) rm = true;
                    else if (BrkRetest && !z.Retested)
                    {
                        bool retest = bear ? High[0] >= z.Bot : Low[0] <= z.Top;
                        if (retest) { z.Retested = true; z.Text = (bear ? "BEARISH BREAKER BLOCK" : "BULLISH BREAKER BLOCK") + " (RETEST)"; }
                    }
                }
                if (rm) brs.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------------ market structure
        private void DoStruct(Struct s, double ph, double pl, bool isInt)
        {
            int pLen = isInt ? IntLen : SwingLen;
            if (!Na(ph)) { s.HPrice = ph; s.HBar = CurrentBar - pLen; s.HOpen = true; }
            if (!Na(pl)) { s.LPrice = pl; s.LBar = CurrentBar - pLen; s.LOpen = true; }
            bool showIt = ShowStructure && (isInt ? ShowIntStruct : ShowSwingStruct);
            bool obAllowed = ObSource == AobleObSource.Both || (ObSource == AobleObSource.Swing && !isInt) || (ObSource == AobleObSource.Internal && isInt);
            bool volMss = !VolMssFilter || !volAvail || relVolMax3 >= VolThr;

            if (s.HOpen && Close[0] > s.HPrice)
            {
                bool rev = s.Trend == -1;
                bool strong = !Na(atrV) && Body3(true) >= DispMult * atrV && volMss;
                string nm = rev ? (strong ? "BULLISH MSS" : "BULLISH CHoCH") : "BOS";
                double lvl = s.HPrice; int x0 = s.HBar;
                s.HOpen = false; s.Trend = 1;
                bool dup = isInt && brkLogBar == CurrentBar && Math.Abs(brkLogPx - lvl) < TickSize * 2;
                if (!dup)
                {
                    ev[rev ? E_BULL_MSS : E_BULL_BOS] = 1;
                    lastStruct = nm + " @ " + lvl.ToString("0.00", CultureInfo.InvariantCulture);
                    if (!isInt) { brkLogBar = CurrentBar; brkLogPx = lvl; }
                    if (showIt) AddSeg(x0, lvl, true, isInt, nm);
                }
                TryOB(true, x0, obAllowed);
            }
            if (s.LOpen && Close[0] < s.LPrice)
            {
                bool rev = s.Trend == 1;
                bool strong = !Na(atrV) && Body3(false) >= DispMult * atrV && volMss;
                string nm = rev ? (strong ? "BEARISH MSS" : "BEARISH CHoCH") : "BOS";
                double lvl = s.LPrice; int x0 = s.LBar;
                s.LOpen = false; s.Trend = -1;
                bool dup = isInt && brkLogBar == CurrentBar && Math.Abs(brkLogPx - lvl) < TickSize * 2;
                if (!dup)
                {
                    ev[rev ? E_BEAR_MSS : E_BEAR_BOS] = 1;
                    lastStruct = nm + " @ " + lvl.ToString("0.00", CultureInfo.InvariantCulture);
                    if (!isInt) { brkLogBar = CurrentBar; brkLogPx = lvl; }
                    if (showIt) AddSeg(x0, lvl, false, isInt, nm);
                }
                TryOB(false, x0, obAllowed);
            }
        }
        private void AddSeg(int x0, double lvl, bool bull, bool isInt, string nm)
        {
            segs.Add(new Seg { X0 = x0, X1 = CurrentBar, Price = lvl, Bull = bull, Internal = isInt, Text = nm });
            if (segs.Count > 700) segs.RemoveAt(0);
        }

        // ------------------------------------------------------------------ liquidity
        private void TrimLiq()
        {
            int localShown = 0, htfCnt = 0;
            foreach (Liq l in liqs) { if (l.Htf) htfCnt++; else if (l.Shown) localShown++; }
            while (localShown > MaxLiq)
            {
                int idx = -1;
                for (int i = 0; i < liqs.Count; i++) if (!liqs[i].Htf && liqs[i].Shown) { idx = i; break; }
                if (idx < 0) break;
                liqs.RemoveAt(idx); localShown--;
            }
            while (htfCnt > MaxMTF * 2)
            {
                int idx = -1;
                for (int i = 0; i < liqs.Count; i++) if (liqs[i].Htf) { idx = i; break; }
                if (idx < 0) break;
                liqs.RemoveAt(idx); htfCnt--;
            }
        }

        private void LiqPivot(double pv, bool isBSL)
        {
            int pbar = CurrentBar - LiqLen;
            double tol = LiqTolMode == AobleTolMode.Atr ? LiqTolAtr * Nz(atrV) : LiqTolTicks * TickSize;
            Liq hit = null;
            foreach (Liq l in liqs)
                if (hit == null && !l.Htf && l.IsBSL == isBSL && !l.Swept && Math.Abs(l.Price - pv) <= tol) hit = l;
            if (hit == null)
            {
                hit = new Liq { Price = pv, Touches = 1, LastBar = pbar, FirstBar = pbar, IsBSL = isBSL };
                liqs.Add(hit);
            }
            else if (pbar - hit.LastBar >= LiqMinSep)
            {
                hit.Touches++; hit.LastBar = pbar;
                hit.Price = isBSL ? Math.Max(hit.Price, pv) : Math.Min(hit.Price, pv);
            }
            if (hit.Touches >= LiqMinTouches && !hit.Shown)
            {
                hit.Shown = true;
                ev[isBSL ? E_NEW_BSL : E_NEW_SSL] = 1;
            }
            TrimLiq();
        }

        private void ManageLiq()
        {
            for (int i = liqs.Count - 1; i >= 0; i--)
            {
                Liq l = liqs[i]; bool rm = false;
                if (!l.Shown) { if (CurrentBar - l.LastBar > 500) rm = true; }
                else if (RecentOnly && !l.Htf && CurrentBar - l.FirstBar > RecentBars) rm = true;
                else if (!l.Swept && CurrentBar > (l.Htf ? l.LastBar : l.FirstBar))
                {
                    bool crossed = l.IsBSL ? High[0] > l.Price : Low[0] < l.Price;
                    if (crossed)
                    {
                        l.Swept = true;
                        bool rej = l.IsBSL ? Close[0] <= l.Price : Close[0] >= l.Price;
                        if (!l.Htf)
                        {
                            ev[l.IsBSL ? E_BSL_SWEEP : E_SSL_SWEEP] = 1;
                            if (rej) { if (l.IsBSL) sweepBsl = CurrentBar; else sweepSsl = CurrentBar; }
                            if (ShowSweepLbl && ShowLiq)
                            {
                                marks.Add(new Mark { Bar = CurrentBar, Price = l.IsBSL ? High[0] : Low[0], Above = l.IsBSL, Bsl = l.IsBSL, Text = (l.IsBSL ? "BSL " : "SSL ") + (rej ? "SWEEP" : "TAKEN") });
                                if (marks.Count > 400) marks.RemoveAt(0);
                            }
                        }
                        if (!(DelConsumed || l.Htf))
                        {
                            l.End = CurrentBar; l.Faded = true;
                            sweptLiq.Add(l);
                            while (sweptLiq.Count > MaxGhosts) sweptLiq.RemoveAt(0);
                        }
                        rm = true;
                    }
                }
                if (rm) liqs.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------------ fair value gaps
        private void AddFVG(bool bull)
        {
            int d = bull ? 1 : -1;
            double t = bull ? Low[0] : Low[2];
            double b = bull ? High[2] : High[0];
            Zone zn = new Zone { Top = t, Bot = b, Left = CurrentBar - 2, Origin = CurrentBar - 2, Dir = d, State = 0, Kind = 2, Born = CurrentBar, Text = FvgLabel ? (bull ? "BULLISH FVG" : "BEARISH FVG") : "" };
            fvgs.Add(zn);
            Trim(fvgs, d, MaxFvg);
            ev[bull ? E_BULL_FVG : E_BEAR_FVG] = 1;
            if (bull) trigBullFvg = CurrentBar; else trigBearFvg = CurrentBar;
        }

        private void ManageFVG()
        {
            for (int i = fvgs.Count - 1; i >= 0; i--)
            {
                Zone z = fvgs[i]; bool rm = RecentOnly && CurrentBar - z.Origin > RecentBars;
                if (!rm && CurrentBar > z.Born && z.State < 2)
                {
                    bool filled;
                    if (z.Dir == 1)
                    {
                        if (Low[0] < z.Top) { z.Top = Math.Max(Low[0], z.Bot); if (z.State == 0) z.State = 1; }
                        filled = Low[0] <= z.Bot;
                    }
                    else
                    {
                        if (High[0] > z.Bot) { z.Bot = Math.Min(High[0], z.Top); if (z.State == 0) z.State = 1; }
                        filled = High[0] >= z.Top;
                    }
                    if (filled)
                    {
                        z.State = 2;
                        if (FvgOnFill == AobleFvgFill.Remove) rm = true; else z.End = CurrentBar;
                    }
                }
                if (rm) fvgs.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------------ higher timeframes
        private void HtfBar(int slot)
        {
            HtfState h = htf[slot];
            if (CurrentBars[0] < 1) return;
            int bip = h.Bip; int cb = CurrentBars[bip];
            double hi = Highs[bip][0], lo = Lows[bip][0], cl = Closes[bip][0];
            double tr = cb == 0 ? hi - lo : Math.Max(hi - lo, Math.Max(Math.Abs(hi - Closes[bip][1]), Math.Abs(lo - Closes[bip][1])));
            if (cb < AtrLen - 1) h.SumTr += tr;
            else if (cb == AtrLen - 1) { h.SumTr += tr; h.Atr = h.SumTr / AtrLen; }
            else h.Atr = (h.Atr * (AtrLen - 1) + tr) / AtrLen;

            double ph = double.NaN, pl = double.NaN; int L = SwingLen;
            if (cb >= 2 * L)
            {
                double v = Highs[bip][L]; bool ok = true;
                for (int k = 0; k < L && ok; k++) if (Highs[bip][k] >= v) ok = false;
                for (int k = L + 1; k <= 2 * L && ok; k++) if (Highs[bip][k] > v) ok = false;
                if (ok) ph = v;
                double w = Lows[bip][L]; ok = true;
                for (int k = 0; k < L && ok; k++) if (Lows[bip][k] <= w) ok = false;
                for (int k = L + 1; k <= 2 * L && ok; k++) if (Lows[bip][k] < w) ok = false;
                if (ok) pl = w;
            }
            double up3 = double.MinValue, dn3 = double.MinValue;
            for (int k = 0; k < 3 && k <= cb; k++) { up3 = Math.Max(up3, Closes[bip][k] - Opens[bip][k]); dn3 = Math.Max(dn3, Opens[bip][k] - Closes[bip][k]); }
            if (!Na(ph)) { h.LH = ph; h.OH = true; }
            if (!Na(pl)) { h.LL = pl; h.OL = true; }

            if (MtfLiq)
            {
                if (!Na(ph)) AddMtfLiq(h, true, ph, Times[bip][L], 0);
                if (!Na(pl)) AddMtfLiq(h, false, pl, Times[bip][L], 1);
            }
            if (h.OH && cl > h.LH)
            {
                h.OH = false;
                if (!Na(h.Atr) && up3 >= DispMult * h.Atr)
                    for (int k = 1; k <= 12 && k <= cb; k++)
                        if (Closes[bip][k] < Opens[bip][k]) { if (MtfOB) AddMtfZone(h, 1, Highs[bip][k], Lows[bip][k], Times[bip][k]); break; }
            }
            if (h.OL && cl < h.LL)
            {
                h.OL = false;
                if (!Na(h.Atr) && dn3 >= DispMult * h.Atr)
                    for (int k = 1; k <= 12 && k <= cb; k++)
                        if (Closes[bip][k] > Opens[bip][k]) { if (MtfOB) AddMtfZone(h, -1, Highs[bip][k], Lows[bip][k], Times[bip][k]); break; }
            }
        }
        private int PrimaryBarAt(DateTime t)
        {
            int idx = Bars.GetBar(t);
            if (idx < 0) idx = 0;
            return Math.Min(idx, CurrentBars[0]);
        }
        private void AddMtfZone(HtfState h, int dir, double t, double b, DateTime tm)
        {
            long key = tm.Ticks;
            if (dir == 1) { if (h.SeenBT == key) return; h.SeenBT = key; } else { if (h.SeenST == key) return; h.SeenST = key; }
            int left = PrimaryBarAt(tm);
            mtfZ.Add(new Zone { Top = t, Bot = b, Left = left, Origin = CurrentBars[0], Dir = dir, State = 0, Kind = 3, Born = CurrentBars[0], Slot = Array.IndexOf(htf, h),
                Text = h.Label + (dir == 1 ? " BULLISH OB" : " BEARISH OB") });
            Trim(mtfZ, 0, MaxMTF * 2);
        }
        private void AddMtfLiq(HtfState h, bool isBSL, double p, DateTime tm, int which)
        {
            long key = tm.Ticks;
            if (isBSL) { if (h.SeenBsl == key) return; h.SeenBsl = key; } else { if (h.SeenSsl == key) return; h.SeenSsl = key; }
            int left = PrimaryBarAt(tm);
            liqs.Add(new Liq { Price = p, Touches = 1, LastBar = CurrentBars[0], FirstBar = left, IsBSL = isBSL, Shown = true, Htf = true, Slot = Array.IndexOf(htf, h) });
            TrimLiq();
        }
        private void ManageMtfZ()
        {
            for (int i = mtfZ.Count - 1; i >= 0; i--)
            {
                Zone z = mtfZ[i];
                if (CurrentBar > z.Born && ((z.Dir == 1 && Close[0] < z.Bot) || (z.Dir == -1 && Close[0] > z.Top))) mtfZ.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------------ trade planner
        private void NearZone(List<Zone> arr, int dir, double refPx, out double top, out double bot)
        {
            top = bot = double.NaN; double bestD = 1e18;
            foreach (Zone z in arr)
            {
                if (z.Dir != dir || z.State >= 2) continue;
                double d = 1e18;
                if (dir == 1) { if (z.Bot < refPx) d = z.Top <= refPx ? refPx - z.Top : 0.0; }
                else { if (z.Top > refPx) d = z.Bot >= refPx ? z.Bot - refPx : 0.0; }
                if (d < bestD) { bestD = d; top = z.Top; bot = z.Bot; }
            }
        }

        private double[] Targets(int dir, double entry, double risk)
        {
            List<double> outL = new List<double>();
            bool lng = dir == 1;
            if (TpMode == AobleTp.StructureLiquidity)
            {
                List<double> c = new List<double>();
                foreach (Liq l in liqs)
                {
                    if (l.Htf || !l.Shown || l.Swept) continue;
                    if (lng && l.IsBSL && l.Price > entry) c.Add(l.Price);
                    else if (!lng && !l.IsBSL && l.Price < entry) c.Add(l.Price);
                }
                foreach (Zone z in obs)
                {
                    if (z.State >= 2) continue;
                    if (lng && z.Dir == -1 && z.Bot > entry) c.Add(z.Bot);
                    else if (!lng && z.Dir == 1 && z.Top < entry) c.Add(z.Top);
                }
                double sw = lng ? sSwing.HPrice : sSwing.LPrice;
                if (!Na(sw) && (lng ? sw > entry : sw < entry)) c.Add(sw);
                c.Sort(); if (!lng) c.Reverse();
                double lastV = entry;
                foreach (double v in c)
                {
                    if (outL.Count >= 5) break;
                    bool ok = lng ? v >= lastV + 0.5 * risk : v <= lastV - 0.5 * risk;
                    if (ok) { outL.Add(v); lastV = v; }
                }
            }
            double[] rrs = { Rr1, Rr2, Rr3, Rr4, Rr5 };
            while (outL.Count < 5)
            {
                int i = outL.Count;
                double last = i > 0 ? outL[i - 1] : entry;
                double cand = lng ? entry + risk * rrs[i] : entry - risk * rrs[i];
                double px = lng ? Math.Max(cand, last + 0.5 * risk) : Math.Min(cand, last - 0.5 * risk);
                outL.Add(px);
            }
            return outL.ToArray();
        }

        private Setup Plan(int dir)
        {
            bool lng = dir == 1;
            double a = Na(atrV) ? 1.0 : atrV;
            double c = Close[0];
            double obT, obB, fvT, fvB;
            NearZone(obs, dir, c, out obT, out obB);
            NearZone(fvgs, dir, c, out fvT, out fvB);
            double entry = c;
            if (EntryMethod == AobleEntry.OrderBlockRetest && !Na(obT)) entry = lng ? Math.Min(obT, c) : Math.Max(obB, c);
            else if (EntryMethod == AobleEntry.FvgRetest && !Na(fvT)) { double fm = (fvT + fvB) / 2; entry = lng ? Math.Min(fm, c) : Math.Max(fm, c); }
            else if (EntryMethod == AobleEntry.UserPrice && UserEntry > 0) entry = lng ? Math.Min(UserEntry, c) : Math.Max(UserEntry, c);
            double bas = double.NaN;
            if (SlMode == AobleStop.BeyondSwing)
            {
                double sw = lng ? sSwing.LPrice : sSwing.HPrice;
                if (!Na(sw) && (lng ? sw < entry : sw > entry)) bas = sw;
            }
            else if (SlMode == AobleStop.BeyondOrderBlock)
            {
                double zb = lng ? obB : obT;
                if (!Na(zb) && (lng ? zb < entry : zb > entry)) bas = zb;
            }
            double sl = lng ? entry - SlAtrMult * a : entry + SlAtrMult * a;
            if (!Na(bas))
            {
                double sb = lng ? bas - SlBuf * a : bas + SlBuf * a;
                if (Math.Abs(entry - sb) <= MaxRiskAtr * a) sl = sb;
            }
            double risk = Math.Abs(entry - sl);
            if (risk < 0.3 * a) { risk = 0.3 * a; sl = lng ? entry - risk : entry + risk; }
            Setup s = new Setup { Dir = dir, State = 0, Bar0 = CurrentBar, Entry = entry, Sl = sl, TpHit = 0 };
            s.Tps = Targets(dir, entry, risk);
            return s;
        }

        private void TrackSetup(Setup s)
        {
            bool lng = s.Dir == 1;
            if (CurrentBar <= s.Bar0) return;
            if (s.State == 0)
            {
                bool filled = lng ? Low[0] <= s.Entry : High[0] >= s.Entry;
                bool dead = lng ? Close[0] < s.Sl : Close[0] > s.Sl;
                if (dead || CurrentBar - s.Bar0 > PendExpire)
                {
                    s.State = 2; s.EntryTag = " [CANCELLED]"; s.CloseBar = CurrentBar; RecClose(s, "CANCELLED");
                }
                else if (filled)
                {
                    s.State = 1; ev[E_ENTRY] = 1; s.EntryTag = " [FILLED]";
                }
                else if (DynSetup)
                {
                    Setup np = Plan(s.Dir);
                    s.Entry = np.Entry; s.Sl = np.Sl; s.Tps = np.Tps;
                }
            }
            if (s.State == 1)
            {
                bool slHit = lng ? Low[0] <= s.Sl : High[0] >= s.Sl;
                if (slHit)
                {
                    s.State = 2; ev[E_SL] = 1; s.SlTag = " [HIT]"; s.CloseBar = CurrentBar; RecClose(s, "SL");
                }
                else
                {
                    while (s.TpHit < 5 && (lng ? High[0] >= s.Tps[s.TpHit] : Low[0] <= s.Tps[s.TpHit]))
                    {
                        s.TpTag[s.TpHit] = " [HIT]"; s.TpHit++; ev[E_TP] = 1;
                    }
                    if (s.TpHit >= 5) { s.State = 2; s.CloseBar = CurrentBar; RecClose(s, "TP5"); }
                }
            }
        }

        private void RecOpen(Setup s, string info)
        {
            if (rec == null || State != State.Historical && State != State.Realtime) return;
            try
            {
                rec.WriteLine(string.Join(",", new string[] { Time[0].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "open", s.Dir.ToString(),
                    F(s.Entry), F(s.Sl), F(s.Tps[0]), F(s.Tps[1]), F(s.Tps[2]), F(s.Tps[3]), F(s.Tps[4]), F(Nz(atrV)), info }));
            }
            catch { }
        }
        private void RecClose(Setup s, string result)
        {
            if (rec == null) return;
            try
            {
                rec.WriteLine(string.Join(",", new string[] { Time[0].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "close", s.Dir.ToString(),
                    F(s.Entry), F(s.Sl), "", "", "", "", "", F(Nz(atrV)), result + "_tp" + s.TpHit }));
            }
            catch { }
        }
        private static string F(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

        private void PlanSetups()
        {
            if (!ShowSetups || Na(atrV)) return;
            if (cur != null && cur.State < 2) TrackSetup(cur);
            bool coolOk = CurrentBar - lastSetupBar >= Cooldown;
            bool free = cur == null || cur.State == 2;
            if (!free || !coolOk) return;
            double eqMid = (!Na(sSwing.HPrice) && !Na(sSwing.LPrice)) ? (sSwing.HPrice + sSwing.LPrice) / 2 : double.NaN;
            bool volOkS = !VolSetupFilter || !volAvail || relVolMax3 >= VolThr;

            int tbL = -1;
            if (TrigMode != AobleTrigger.ObTouchOnly && sweepSsl >= 0 && CurrentBar - sweepSsl <= SetupWin) tbL = Math.Max(tbL, sweepSsl);
            if (TrigMode != AobleTrigger.SweepOnly && trigBullOb >= 0 && CurrentBar - trigBullOb <= SetupWin) tbL = Math.Max(tbL, trigBullOb);
            bool bullDispNow = Close[0] > Open[0] && (Close[0] - Open[0]) >= DispMult * atrV;
            bool confL = ev[E_BULL_MSS] == 1 || (ConfMode == AobleConf.MssOrDisp && bullDispNow);
            bool fvgL = !ReqFvg || (trigBullFvg >= 0 && CurrentBar - trigBullFvg <= SetupWin);
            bool pdL = !PdFilter || Na(eqMid) || Close[0] < eqMid;
            if (tbL > usedBullTrig && tbL >= 0 && confL && fvgL && pdL && volOkS)
            {
                Setup s = Plan(1);
                s.State = s.Entry == Close[0] ? 1 : 0;
                NewSetup(s, E_BUY);
                usedBullTrig = tbL;
                RecOpen(s, (tbL == sweepSsl ? "sweep" : "obtouch") + "_" + (ev[E_BULL_MSS] == 1 ? "mss" : "disp"));
                return;
            }
            int tbS = -1;
            if (TrigMode != AobleTrigger.ObTouchOnly && sweepBsl >= 0 && CurrentBar - sweepBsl <= SetupWin) tbS = Math.Max(tbS, sweepBsl);
            if (TrigMode != AobleTrigger.SweepOnly && trigBearOb >= 0 && CurrentBar - trigBearOb <= SetupWin) tbS = Math.Max(tbS, trigBearOb);
            bool bearDispNow = Close[0] < Open[0] && (Open[0] - Close[0]) >= DispMult * atrV;
            bool confS = ev[E_BEAR_MSS] == 1 || (ConfMode == AobleConf.MssOrDisp && bearDispNow);
            bool fvgS = !ReqFvg || (trigBearFvg >= 0 && CurrentBar - trigBearFvg <= SetupWin);
            bool pdS = !PdFilter || Na(eqMid) || Close[0] > eqMid;
            if (tbS > usedBearTrig && tbS >= 0 && confS && fvgS && pdS && volOkS)
            {
                Setup s = Plan(-1);
                s.State = s.Entry == Close[0] ? 1 : 0;
                NewSetup(s, E_SELL);
                usedBearTrig = tbS;
                RecOpen(s, (tbS == sweepBsl ? "sweep" : "obtouch") + "_" + (ev[E_BEAR_MSS] == 1 ? "mss" : "disp"));
            }
        }
        private void NewSetup(Setup s, int evSlot)
        {
            setups.Add(s);
            while (setups.Count > MaxSetups) setups.RemoveAt(0);
            cur = s; lastSetupBar = CurrentBar; ev[evSlot] = 1;
        }

        // ------------------------------------------------------------------ main loop
        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
            {
                for (int i = 0; i < 5; i++) if (htf[i] != null && htf[i].On && htf[i].Bip == BarsInProgress) lock (sync) { HtfBar(i); }
                return;
            }
            lock (sync) { OnBarUpdateLocked(); }
        }

        private void OnBarUpdateLocked()
        {
            for (int k = 0; k < NEV; k++) ev[k] = 0;
            for (int k = 0; k < 5; k++) Values[k][0] = 0;
            for (int k = 0; k < 7; k++) outS[k][0] = 0;

            // ATR (RMA of true range) and volume
            double tr = CurrentBar == 0 ? High[0] - Low[0] : Math.Max(High[0] - Low[0], Math.Max(Math.Abs(High[0] - Close[1]), Math.Abs(Low[0] - Close[1])));
            if (CurrentBar < AtrLen - 1) atrSum += tr;
            else if (CurrentBar == AtrLen - 1) { atrSum += tr; atrV = atrSum / AtrLen; }
            else atrV = (atrV * (AtrLen - 1) + tr) / AtrLen;

            volMa = double.NaN;
            if (CurrentBar >= VolLen - 1) { double sv = 0; for (int k = 0; k < VolLen; k++) sv += Volume[k]; volMa = sv / VolLen; }
            volAvail = Nz(volMa) > 0;
            relVol = volAvail ? Volume[0] / volMa : double.NaN;
            relVolHist[2] = relVolHist[1]; relVolHist[1] = relVolHist[0]; relVolHist[0] = Nz(relVol);
            relVolMax3 = Math.Max(relVolHist[0], Math.Max(relVolHist[1], relVolHist[2]));
            volSpike = volAvail && Nz(relVol) >= VolThr;

            double phS = PivHigh(SwingLen), plS = PivLow(SwingLen), phI = PivHigh(IntLen), plI = PivLow(IntLen);
            double phL = PivHigh(LiqLen), plL = PivLow(LiqLen);

            ManageOB(); ManageBR(); ManageFVG(); ManageMtfZ();
            DoStruct(sSwing, phS, plS, false);
            DoStruct(sInt, phI, plI, true);
            if (!Na(phL)) LiqPivot(phL, true);
            if (!Na(plL)) LiqPivot(plL, false);
            ManageLiq();
            if (ShowFvg && CurrentBar >= 2)
            {
                double minSz = FvgMode == AobleTolMode.Atr ? FvgMinAtr * Nz(atrV) : FvgMinTicks * TickSize;
                if (Low[0] > High[2] && (Low[0] - High[2]) >= minSz) AddFVG(true);
                if (High[0] < Low[2] && (Low[2] - High[0]) >= minSz) AddFVG(false);
            }
            PlanSetups();

            // signals
            if (ev[E_BUY] == 1) { Values[0][0] = 1; outS[0][0] = 1; }
            else if (ev[E_SELL] == 1) { Values[0][0] = -1; outS[0][0] = -1; }
            if (ev[E_BULL_MSS] == 1) Values[1][0] = 1; else if (ev[E_BEAR_MSS] == 1) Values[1][0] = -1;
            if (ev[E_BULL_BOS] == 1) Values[2][0] = 1; else if (ev[E_BEAR_BOS] == 1) Values[2][0] = -1;
            if (ev[E_SSL_SWEEP] == 1 && sweepSsl == CurrentBar) Values[3][0] = 1;
            else if (ev[E_BSL_SWEEP] == 1 && sweepBsl == CurrentBar) Values[3][0] = -1;
            if (ev[E_BULL_TOUCH] == 1) Values[4][0] = 1; else if (ev[E_BEAR_TOUCH] == 1) Values[4][0] = -1;
            if ((ev[E_BUY] == 1 || ev[E_SELL] == 1) && cur != null)
            {
                outS[1][0] = cur.Entry; outS[2][0] = cur.Sl; outS[3][0] = cur.Tps[0]; outS[4][0] = cur.Tps[1]; outS[5][0] = cur.Tps[2]; outS[6][0] = Nz(atrV);
                double off = 0.5 * Nz(atrV);
                if (ev[E_BUY] == 1) Values[5][0] = Low[0] - off; else Values[6][0] = High[0] + off;
            }

            if (AlertsOn && State == State.Realtime)
            {
                for (int k = 0; k < NEV; k++)
                    if (ev[k] == 1)
                        Alert("AOBLE" + k + "_" + CurrentBar, Priority.Medium, "AOBLE: " + EvName[k] + " on " + Instrument.FullName, NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Black, Brushes.Yellow);
            }
        }

        // ------------------------------------------------------------------ rendering
        private readonly object sync = new object();
        private Dictionary<long, D2D.SolidColorBrush> brushCache = new Dictionary<long, D2D.SolidColorBrush>();
        private Dictionary<int, DW.TextFormat> fmtCache = new Dictionary<int, DW.TextFormat>();
        private D2D.StrokeStyle stDash, stDot;
        private int rnCount;
        private int rLast;
        private float rFrom, rX0, rSp, pL, pR, pT, pB;
        private ChartControl rCtl; private ChartScale rScale;

        // palette order: 0 bullOB 1 bearOB 2 bullBr 3 bearBr 4 BSL 5 SSL 6 bullStruct 7 bearStruct 8 bullFVG 9 bearFVG 10 equilibrium 11 entry 12 SL 13 TP 14 text
        private static readonly int[][] Pal = new int[][] {
            new int[] { 0x00E676, 0xFF1744, 0x00BFA5, 0xC2185B, 0xFF5252, 0xFF9800, 0x00E676, 0xFF1744, 0x26C6DA, 0xFF9100, 0xFFD600, 0xFFFFFF, 0xFF1744, 0x00E5A0, 0xFFFFFF },
            new int[] { 0x00963F, 0xD50000, 0x00796B, 0x880E4F, 0xE53935, 0xEF6C00, 0x00A152, 0xD50000, 0x00ACC1, 0xEF6C00, 0xF9A825, 0x1A1A1A, 0xD50000, 0x00796B, 0x111111 },
            new int[] { 0x39FF14, 0xFF0055, 0x00FFD0, 0xFF2DAA, 0xFF4D6D, 0xFFB300, 0x00FF9C, 0xFF3D71, 0x00E5FF, 0xFF6D00, 0xFFEA00, 0xFFFFFF, 0xFF3D71, 0x00FFB2, 0xFFFFFF },
            new int[] { 0x5C9E7A, 0xB5655F, 0x4F9E96, 0x9E5873, 0xA06060, 0xB08A55, 0x6FAF8A, 0xB5655F, 0x7FB8C0, 0xC09A6A, 0xC8B560, 0x8A8A8A, 0xB5655F, 0x5FA58F, 0xD0D0D0 } };
        private int C(int idx) { return Pal[(int)Theme][idx]; }
        private static readonly int[] DashBg = { 0x0B0E14, 0xFFFFFF, 0x05010F, 0x2B2F36 };

        private void DisposeBrushes()
        {
            foreach (D2D.SolidColorBrush b in brushCache.Values) { try { b.Dispose(); } catch { } }
            brushCache.Clear();
            foreach (DW.TextFormat f in fmtCache.Values) { try { f.Dispose(); } catch { } }
            fmtCache.Clear();
            try { if (stDash != null) stDash.Dispose(); } catch { }
            try { if (stDot != null) stDot.Dispose(); } catch { }
            stDash = stDot = null;
        }
        public override void OnRenderTargetChanged() { lock (sync) DisposeBrushes(); }

        private D2D.SolidColorBrush Br(int rgb, float a)
        {
            long key = ((long)rgb << 8) | (long)Math.Max(0, Math.Min(255, (int)(a * 255)));
            D2D.SolidColorBrush b;
            if (!brushCache.TryGetValue(key, out b))
            {
                b = new D2D.SolidColorBrush(RenderTarget, new SharpDX.Color4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a));
                brushCache[key] = b;
            }
            return b;
        }
        private static float FontPx(AobleTextSize s) { return s == AobleTextSize.Tiny ? 8f : s == AobleTextSize.Small ? 10f : s == AobleTextSize.Normal ? 12f : 14f; }
        private DW.TextFormat Fmt(AobleTextSize s, bool bold)
        {
            int key = (int)s * 2 + (bold ? 1 : 0);
            DW.TextFormat f;
            if (!fmtCache.TryGetValue(key, out f))
            {
                DW.Factory dw = new DW.Factory();
                f = new DW.TextFormat(dw, "Arial", bold ? DW.FontWeight.Bold : DW.FontWeight.Normal, DW.FontStyle.Normal, FontPx(s));
                f.WordWrapping = DW.WordWrapping.NoWrap;
                dw.Dispose();
                fmtCache[key] = f;
            }
            return f;
        }
        private float X(int bar) { return rX0 + (bar - rFrom) * rSp; }
        private float Y(double price) { return rScale.GetYByValue(price); }

        private void FillR(float x1, float y1, float x2, float y2, int rgb, float a)
        {
            if (x2 < x1) { float t = x1; x1 = x2; x2 = t; }
            if (y2 < y1) { float t = y1; y1 = y2; y2 = t; }
            RenderTarget.FillRectangle(new SharpDX.RectangleF(x1, y1, Math.Max(1f, x2 - x1), Math.Max(1f, y2 - y1)), Br(rgb, a));
        }
        private void BoxR(float x1, float y1, float x2, float y2, int rgb, float a, float w, int style)
        {
            if (x2 < x1) { float t = x1; x1 = x2; x2 = t; }
            if (y2 < y1) { float t = y1; y1 = y2; y2 = t; }
            D2D.SolidColorBrush b = Br(rgb, a);
            if (style == 0) RenderTarget.DrawRectangle(new SharpDX.RectangleF(x1, y1, Math.Max(1f, x2 - x1), Math.Max(1f, y2 - y1)), b, w);
            else
            {
                Ln(x1, y1, x2, y1, rgb, a, w, style); Ln(x1, y2, x2, y2, rgb, a, w, style);
                Ln(x1, y1, x1, y2, rgb, a, w, style); Ln(x2, y1, x2, y2, rgb, a, w, style);
            }
        }
        // style: 0 solid, 1 dashed, 2 dotted
        private void Ln(float x1, float y1, float x2, float y2, int rgb, float a, float w, int style)
        {
            D2D.SolidColorBrush b = Br(rgb, a);
            if (style == 0) { RenderTarget.DrawLine(new SharpDX.Vector2(x1, y1), new SharpDX.Vector2(x2, y2), b, w); return; }
            if (stDash == null)
            {
                stDash = new D2D.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, new D2D.StrokeStyleProperties { DashStyle = D2D.DashStyle.Dash });
                stDot = new D2D.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, new D2D.StrokeStyleProperties { DashStyle = D2D.DashStyle.Dot });
            }
            RenderTarget.DrawLine(new SharpDX.Vector2(x1, y1), new SharpDX.Vector2(x2, y2), b, w, style == 1 ? stDash : stDot);
        }
        private float TxtW(string s, AobleTextSize sz, bool bold) { return s.Length * FontPx(sz) * (bold ? 0.62f : 0.56f) + 2f; }
        private void Txt(string s, float x, float y, AobleTextSize sz, int rgb, float a, bool bold)
        {
            if (string.IsNullOrEmpty(s)) return;
            RenderTarget.DrawText(s, Fmt(sz, bold), new SharpDX.RectangleF(x, y, 600f, FontPx(sz) + 6f), Br(rgb, a));
        }
        private static int LS(AobleLineStyle s) { return s == AobleLineStyle.Solid ? 0 : s == AobleLineStyle.Dashed ? 1 : 2; }
        private static float Al(int transp) { return Math.Max(0f, Math.Min(1f, (100 - transp) / 100f)); }

        private int ZoneRgb(Zone z)
        {
            if (z.Ghost) return 0x808080;
            switch (z.Kind) { case 0: return C(z.Dir == 1 ? 0 : 1); case 1: return C(z.Dir == 1 ? 2 : 3); case 2: return C(z.Dir == 1 ? 8 : 9); default: return C(z.Dir == 1 ? 0 : 1); }
        }

        private void DrawZone(Zone z, bool rightEdgeMode)
        {
            float xl = X(z.Left);
            float xr = z.End >= 0 ? X(z.End) : pR;
            if (xr < pL || xl > pR) return;
            float y1 = Y(z.Top), y2 = Y(z.Bot);
            if (Math.Max(y1, y2) < pT || Math.Min(y1, y2) > pB) return;
            int rgb = ZoneRgb(z);
            int ft = FillTransp; int bt = 0;
            if (z.Kind == 3) ft = Math.Min(FillTransp + 6, 98);
            if (z.State == 1) ft = Math.Min(ft + 4, 98); else if (z.State == 2) { ft = 94; bt = 55; }
            if (z.Ghost) { ft = 94; bt = 60; }
            if (z.Retested && z.Kind == 1) ft = Math.Max(FillTransp - 25, 40);
            if (GlowOn && z.Kind != 3 && !z.Ghost) BoxR(xl, y1, xr, y2, rgb, Al(GlowTransp), LineW + 4, 0);
            FillR(xl, y1, xr, y2, rgb, Al(ft));
            int bstyle = (z.State == 2 || z.Ghost) ? 2 : (z.Kind == 3 ? (z.Slot == 0 ? 2 : z.Slot == 2 ? 0 : 1) : 0);
            BoxR(xl, y1, xr, y2, rgb, ShowBorder ? Al(bt) : 0f, LineW + (z.Retested ? 1 : 0), bstyle);
            if (ShowMid && (z.Kind == 0 || z.Kind == 1) && !z.Ghost) { float ym = (y1 + y2) / 2; Ln(xl, ym, xr, ym, rgb, 0.7f, 1f, 2); }
            if (z.Kind == 2 && FvgMid) { float ym = (y1 + y2) / 2; Ln(xl, ym, xr, ym, rgb, z.State == 2 ? 0.2f : 0.7f, 1f, 2); }
            if (!string.IsNullOrEmpty(z.Text) && LblMode != AobleLabelMode.Hidden)
            {
                float yt = Math.Min(y1, y2), h = Math.Abs(y2 - y1);
                float ty = yt + (h - FontPx(ZoneTxtSize)) / 2f - 2f;
                if (LblMode == AobleLabelMode.ZoneStart) Txt(z.Text, Math.Max(xl, pL) + 3f, ty, ZoneTxtSize, C(14), 1f, false);
                else if (z.End < 0) { float tx = Math.Min(X(rLast + RightPad), pR - TxtW(z.Text, ZoneTxtSize, false)); Txt(z.Text, tx, ty, ZoneTxtSize, rgb, 1f, false); }
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (Bars == null || ChartBars == null || RenderTarget == null || chartControl == null || chartScale == null) return;
            base.OnRender(chartControl, chartScale);
            int from = Math.Max(ChartBars.FromIndex, 0), to = ChartBars.ToIndex;
            // ── RENDER DIAGNOSTIC ──────────────────────────────────────────────────────────────────
            // Painted before every early return below, so it survives whatever is blanking the chart.
            // If this line is VISIBLE while the zones are gone -> OnRender runs and the problem is the
            // guards/coordinates/lists it reports. If this line is ALSO GONE -> OnRender is not being
            // called at all and the cause is outside this method. Turn "Render debug" off once solved.
            if (RenderDebug)
            {
                try
                {
                    string dbg = "AOBLE r" + (++rnCount) + " from=" + from + " to=" + to
                               + " cb=" + CurrentBar + " bip=" + BarsInProgress + " last=" + (Bars.Count - 1)
                               + " ob=" + obs.Count + " br=" + brs.Count + " fvg=" + fvgs.Count
                               + " liq=" + liqs.Count + " seg=" + segs.Count + " mtf=" + mtfZ.Count;
                    RenderTarget.DrawText(dbg, Fmt(AobleTextSize.Small, true),
                        new SharpDX.RectangleF(ChartPanel.X + 6f, ChartPanel.Y + 26f, 900f, 18f), Br(0xFFEA00, 1f));
                }
                catch { }
            }
            if (to <= from) return;
            lock (sync)
            {
                rCtl = chartControl; rScale = chartScale;
                rFrom = from; rX0 = chartControl.GetXByBarIndex(ChartBars, from);
                rSp = (chartControl.GetXByBarIndex(ChartBars, to) - rX0) / (float)(to - from);
                if (rSp < 0.5f) rSp = 0.5f;
                pL = ChartPanel.X; pR = ChartPanel.X + ChartPanel.W; pT = ChartPanel.Y; pB = ChartPanel.Y + ChartPanel.H;
                // THE BLANKING BUG: CurrentBar is a bar-update-context property, not a render-safe one --
                // it returns CurrentBars[BarsInProgress] for whichever series was last processed. With the
                // MTF slots on, this indicator adds 60m / 240m / Day / Week series, so in OnRender CurrentBar
                // was often the WEEK series' bar count (0, 1, 2 on a short history). "last < 2" then returned
                // before a single zone was painted, which is why the whole overlay vanished while the
                // indicator stayed loaded and threw nothing. The primary series is the only correct source
                // here, and rLast is reused by DrawZone instead of CurrentBar for the same reason.
                rLast = Bars.Count - 1;
                int last = rLast;
                if (last < 2) return;
                float xrEdge = Math.Min(X(last + RightPad), pR - 4f);

                // premium / discount
                if (ShowPD)
                {
                    Struct ps = PdSource == AobleStructSrc.Swing ? sSwing : sInt;
                    if (!Na(ps.HPrice) && !Na(ps.LPrice) && ps.HPrice > ps.LPrice)
                    {
                        double hi = ps.HPrice, lo = ps.LPrice, eq = (hi + lo) / 2; int x0b = Math.Min(ps.HBar, ps.LBar);
                        float xl = X(x0b), yh = Y(hi), ye = Y(eq), yl = Y(lo);
                        FillR(xl, yh, pR, ye, C(1), Al(PdTransp));
                        FillR(xl, ye, pR, yl, C(0), Al(PdTransp));
                        Txt("PREMIUM", Math.Max(xl, pL) + 4f, yh + 2f, ZoneTxtSize, C(1), 0.6f, false);
                        Txt("DISCOUNT", Math.Max(xl, pL) + 4f, yl - FontPx(ZoneTxtSize) - 6f, ZoneTxtSize, C(0), 0.6f, false);
                        if (ShowRangeHL)
                        {
                            Ln(xl, yh, pR, yh, C(14), 0.4f, 1f, 2); Ln(xl, ye, pR, ye, C(10), 1f, 1f, 1); Ln(xl, yl, pR, yl, C(14), 0.4f, 1f, 2);
                            Txt("RANGE HIGH (100%)", Math.Min(xrEdge, pR - 110f), yh - FontPx(ZoneTxtSize) - 4f, ZoneTxtSize, C(14), 0.8f, false);
                            Txt("EQUILIBRIUM (50%)", Math.Min(xrEdge, pR - 110f), ye - FontPx(ZoneTxtSize) - 4f, ZoneTxtSize, C(10), 1f, false);
                            Txt("RANGE LOW (0%)", Math.Min(xrEdge, pR - 110f), yl - FontPx(ZoneTxtSize) - 4f, ZoneTxtSize, C(14), 0.8f, false);
                        }
                    }
                }

                foreach (Zone z in fvgs) DrawZone(z, false);
                foreach (Zone z in ghosts) DrawZone(z, false);
                foreach (Zone z in obs) DrawZone(z, false);
                foreach (Zone z in brs) DrawZone(z, false);
                foreach (Zone z in mtfZ) DrawZone(z, false);

                // liquidity
                if (ShowLiq)
                {
                    foreach (Liq l in sweptLiq)
                    {
                        float xa = X(l.FirstBar), xb = X(l.End);
                        if (xb < pL || xa > pR) continue;
                        Ln(xa, Y(l.Price), xb, Y(l.Price), l.IsBSL ? C(4) : C(5), 0.35f, LiqWidth, 2);
                    }
                    foreach (Liq l in liqs)
                    {
                        if (!l.Shown) continue;
                        float xa = X(l.FirstBar); float yy = Y(l.Price);
                        if (yy < pT || yy > pB) continue;
                        int rgb = l.IsBSL ? C(4) : C(5);
                        int st = l.Htf ? (l.Slot == 0 ? 2 : l.Slot == 2 ? 0 : 1) : LS(LiqStyle);
                        Ln(xa, yy, pR, yy, rgb, 1f, LiqWidth, st);
                        if (LblMode != AobleLabelMode.Hidden || true)
                        {
                            string tx = l.Htf ? htf[l.Slot].Label + (l.IsBSL ? " BUY-SIDE LIQUIDITY" : " SELL-SIDE LIQUIDITY")
                                : (l.IsBSL ? "BUY-SIDE LIQUIDITY" : "SELL-SIDE LIQUIDITY") + (l.Touches > 1 ? " (EQ x" + l.Touches + ")" : "");
                            Txt(tx, Math.Min(xrEdge, pR - TxtW(tx, ZoneTxtSize, false)), yy - FontPx(ZoneTxtSize) - 4f, ZoneTxtSize, rgb, 1f, false);
                        }
                    }
                }

                // structure lines + labels
                foreach (Seg sg in segs)
                {
                    float xa = X(sg.X0), xb = X(sg.X1);
                    if (xb < pL || xa > pR) continue;
                    float yy = Y(sg.Price);
                    int rgb = sg.Bull ? C(6) : C(7);
                    Ln(xa, yy, xb, yy, rgb, 1f, LineW, LS(sg.Internal ? IntStyle : SwingStyle));
                    AobleTextSize sz = sg.Internal ? IntSize : SwingSize;
                    float tx = (xa + xb) / 2f - TxtW(sg.Text, sz, false) / 2f;
                    Txt(sg.Text, tx, sg.Bull ? yy - FontPx(sz) - 5f : yy + 1f, sz, rgb, 1f, false);
                }

                // sweep labels
                foreach (Mark m in marks)
                {
                    float xx = X(m.Bar);
                    if (xx < pL || xx > pR) continue;
                    float yy = Y(m.Price);
                    int rgb = m.Bsl ? C(4) : C(5);
                    float tx = xx - TxtW(m.Text, ZoneTxtSize, false) / 2f;
                    Txt(m.Text, tx, m.Above ? yy - FontPx(ZoneTxtSize) - 8f : yy + 6f, ZoneTxtSize, rgb, 1f, false);
                }

                // setups
                foreach (Setup s in setups)
                {
                    float xa = X(s.Bar0);
                    float xb = s.CloseBar >= 0 ? X(s.CloseBar) : pR;
                    bool open = s.CloseBar < 0;
                    for (int i = 0; i < 7; i++)
                    {
                        double px = i == 0 ? s.Entry : i == 1 ? s.Sl : s.Tps[i - 2];
                        int rgb = i == 0 ? C(11) : i == 1 ? C(12) : C(13);
                        float yy = Y(px);
                        if (xb >= pL && xa <= pR) Ln(xa, yy, xb, yy, rgb, 1f, LineW, (i == 0 && s.State == 0) ? 1 : 0);
                        string tag = i == 0 ? s.EntryTag : i == 1 ? s.SlTag : s.TpTag[i - 2];
                        string nm = i == 0 ? (s.Dir == 1 ? "BUY ENTRY " : "SELL ENTRY ") : i == 1 ? "STOP LOSS " : "TARGET " + (i - 1) + " ";
                        string tx = nm + (Math.Round(px / TickSize) * TickSize).ToString("0.00", CultureInfo.InvariantCulture) + tag;
                        float tx0 = open ? Math.Min(xrEdge, pR - TxtW(tx, ZoneTxtSize, false)) : xb + 3f;
                        if (tx0 + 20 < pL || tx0 > pR) continue;
                        Txt(tx, tx0, yy - FontPx(ZoneTxtSize) - 4f, ZoneTxtSize, rgb, open ? 1f : 0.6f, false);
                    }
                    if (SetupMarker && xa >= pL - 40 && xa <= pR)
                    {
                        string lab = s.Dir == 1 ? "BUY SETUP" : "SELL SETUP";
                        float w = TxtW(lab, AobleTextSize.Small, true) + 10f, h = 17f;
                        int bi = Math.Min(s.Bar0, Bars.Count - 1);
                        float yy = s.Dir == 1 ? Y(Bars.GetLow(bi)) + MarkerPx : Y(Bars.GetHigh(bi)) - MarkerPx - h;
                        int rgb = s.Dir == 1 ? C(6) : C(7);
                        FillR(xa - w / 2f, yy, xa + w / 2f, yy + h, rgb, 0.8f);
                        Txt(lab, xa - w / 2f + 5f, yy + 2f, AobleTextSize.Small, 0xFFFFFF, 1f, true);
                    }
                }

                if (ShowDash) DrawDash(last);
            }
        }

        private string NearOb(int dir, double close)
        {
            double bt = double.NaN, bb = double.NaN, bestD = 1e18;
            foreach (Zone z in obs)
            {
                if (z.Dir != dir || z.State >= 2) continue;
                double d = close > z.Top ? close - z.Top : close < z.Bot ? z.Bot - close : 0.0;
                if (d < bestD) { bestD = d; bt = z.Top; bb = z.Bot; }
            }
            return Na(bt) ? "-" : bb.ToString("0.00", CultureInfo.InvariantCulture) + " - " + bt.ToString("0.00", CultureInfo.InvariantCulture);
        }
        private string NearLiq(bool bsl, double close)
        {
            double best = double.NaN;
            foreach (Liq l in liqs)
            {
                if (l.Htf || !l.Shown || l.Swept || l.IsBSL != bsl) continue;
                if (bsl ? l.Price > close : l.Price < close)
                    if (Na(best) || (bsl ? l.Price < best : l.Price > best)) best = l.Price;
            }
            return Na(best) ? "-" : best.ToString("0.00", CultureInfo.InvariantCulture);
        }
        private static string TrendTxt(int t) { return t == 1 ? "BULLISH" : t == -1 ? "BEARISH" : "NEUTRAL"; }

        private void DrawDash(int last)
        {
            double close = Bars.GetClose(last);
            string setTxt = "None"; int setCol = C(14);
            if (cur != null && cur.State < 2)
            {
                setTxt = (cur.Dir == 1 ? "BUY" : "SELL") + (cur.State == 0 ? " pending @ " : " active @ ") + cur.Entry.ToString("0.00", CultureInfo.InvariantCulture) + " | TP " + cur.TpHit + "/5";
                setCol = cur.Dir == 1 ? C(6) : C(7);
            }
            string volTxt = volAvail ? Nz(relVol).ToString("0.##", CultureInfo.InvariantCulture) + "x" + (volSpike ? " (SPIKE)" : "") : "N/A (no volume data)";
            string mtfTxt = "Off";
            if (ShowMTF) { mtfTxt = ""; for (int i = 0; i < 5; i++) if (htf[i] != null && htf[i].On) mtfTxt += htf[i].Label + " "; mtfTxt = mtfTxt.Trim(); if (mtfTxt == "") mtfTxt = "None"; }
            List<string[]> rows = new List<string[]>(); List<int> cols = new List<int>();
            rows.Add(new string[] { "AOBLE", Instrument.MasterInstrument.Name + " " + BarsPeriod.Value }); cols.Add(C(14));
            rows.Add(new string[] { "Structural Bias", TrendTxt(sSwing.Trend) }); cols.Add(sSwing.Trend == 1 ? C(6) : sSwing.Trend == -1 ? C(7) : C(14));
            if (!DashCompact) { rows.Add(new string[] { "Internal Structure", TrendTxt(sInt.Trend) }); cols.Add(sInt.Trend == 1 ? C(6) : sInt.Trend == -1 ? C(7) : C(14)); }
            rows.Add(new string[] { "Last BOS / MSS", lastStruct }); cols.Add(C(14));
            if (!DashCompact)
            {
                rows.Add(new string[] { "Nearest Bullish OB", NearOb(1, close) }); cols.Add(C(0));
                rows.Add(new string[] { "Nearest Bearish OB", NearOb(-1, close) }); cols.Add(C(1));
                rows.Add(new string[] { "Nearest Buy-Side Liq.", NearLiq(true, close) }); cols.Add(C(4));
                rows.Add(new string[] { "Nearest Sell-Side Liq.", NearLiq(false, close) }); cols.Add(C(5));
            }
            rows.Add(new string[] { "Active Setup", setTxt }); cols.Add(setCol);
            rows.Add(new string[] { "Relative Volume", volTxt }); cols.Add(C(14));
            if (!DashCompact) { rows.Add(new string[] { "HTF Confluence", mtfTxt }); cols.Add(C(14)); }

            float fs = FontPx(DashSize), cellH = fs + 7f;
            float c0 = 0, c1 = 0;
            foreach (string[] r in rows) { c0 = Math.Max(c0, TxtW(r[0], DashSize, false)); c1 = Math.Max(c1, TxtW(r[1], DashSize, false)); }
            c0 += 12f; c1 += 12f;
            float w = c0 + c1, h = cellH * rows.Count;
            float ox = (DashPos == AobleDashPos.TopLeft || DashPos == AobleDashPos.BottomLeft) ? pL + 8f : pR - w - 8f;
            float oy = (DashPos == AobleDashPos.TopLeft || DashPos == AobleDashPos.TopRight) ? pT + 8f : pB - h - 8f;
            int bg = DashBg[(int)Theme]; float bga = Theme == AobleTheme.Light ? 0.9f : 0.85f;
            FillR(ox, oy, ox + w, oy + h, bg, bga);
            BoxR(ox, oy, ox + w, oy + h, 0x808080, 0.5f, 1f, 0);
            for (int i = 0; i < rows.Count; i++)
            {
                float yy = oy + i * cellH;
                if (i > 0) Ln(ox, yy, ox + w, yy, 0x808080, 0.25f, 1f, 0);
                Txt(rows[i][0], ox + 6f, yy + 2f, DashSize, C(14), 1f, i == 0);
                Txt(rows[i][1], ox + c0 + 6f, yy + 2f, DashSize, cols[i], 1f, false);
            }
        }
    }
}
