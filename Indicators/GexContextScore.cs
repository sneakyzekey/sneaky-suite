#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// GexContextScore -- ORIGINAL indicator, v1 (2026-09-26). Ports the reference tool's own
// OFK_NQ_ContextScore.cs (github.com/Kza56/OFK_Atas_GEX, 392 lines, read in full before this
// port): a -100..+100 directional score gauge computed purely from the SAME CSV
// GexLevels_v1_0_0.cs already reads (GEX walls, gamma flip, gamma zone, DEX magnets, skew,
// term structure, 0DTE pin, VIX regime, IVR) -- no new data source needed.
//
// A SEPARATE, self-contained indicator (own CSV read/parse, own panel) rather than sharing
// state with GexLevels_v1_0_0.cs -- deliberately mirrors the reference tool's own design
// (OFK_NQ_GEX_Levels and OFK_NQ_ContextScore are two independent ATAS indicators that both
// read the same full_levels_NQ.json on their own schedule, never handing data to each other
// directly). Add both to the same chart; this one renders in its own sub-panel below price
// (IsOverlay = false), same as any oscillator.
//
// WHAT'S DIFFERENT FROM THE REFERENCE (documented, not silently dropped):
//   - MACRO BLACKOUT BLOCKING FILTER: NOT PORTED. The reference blocks the score to 0 when a
//     macro event (FOMC/CPI/NFP) is in blackout or under 30 min out, using econ_calendar.py's
//     Forex-Factory-derived blackout status embedded in its JSON. This project's macro-event
//     data (macro_calendar_NQ.json) is read ONLY by generate_briefing_nq.py (Python side) --
//     fetch_gex_nq.py's CSV never carries it to the C# side, so there's nothing to block on
//     here. Left out rather than faked, same policy as every other "not reproduced" item in
//     this project (see GexLevels_v1_0_0.cs's own module comment). If this gets added later,
//     it would need a new "# macro_..." CSV token from fetch_gex_nq.py first.
//   - "DATA QUALITY = error/partial" FILTERS: NOT PORTED (as literal fields) -- this project's
//     CSV has no data_quality token. Substituted with this project's own existing staleness
//     concept instead (CSV older than StaleHoursThreshold -> blocked, same "STALE" idea
//     GexLevels_v1_0_0.cs's status line already uses), which is the closest equivalent this
//     pipeline actually has evidence for.
//   - 0DTE PIN AFTERNOON WINDOW: the reference checks UTC hour 18-21 (~14:00-16:00 ET). This
//     project already assumes the machine's local clock IS Eastern time (same assumption
//     generate_briefing_nq.py's session_phase() makes), so this checks local hour 14-16
//     directly instead of a UTC offset -- same window, simpler expression, one assumption
//     instead of two.
//
// Everything else (wall-position gradient, gamma-flip gradient, gamma-zone bucket, DEX D+/D-
// pull gradient, skew/term penalties, VIX-extreme block + VIX-elevated/IVR>90 attenuators,
// the -100..100 clamp and BULLISH HIGH/BULLISH/NEUTRAL/BEARISH/BEARISH HIGH tagging) matches
// the reference tool's own thresholds and formula exactly -- see ComputeScore below, which is
// a line-for-line port of the reference's own ComputeScore.

namespace NinjaTrader.NinjaScript.Indicators
{
    public class GexContextScore : Indicator
    {
        // ── Parsed-from-CSV context (Intraday-bucket levels + the '#'-comment scalar
        // tokens) -- independent read from the same file GexLevels_v1_0_0.cs reads, see the
        // module comment above for why this isn't shared cross-indicator.
        private struct Ctx
        {
            public bool Loaded;
            public double CallWallIntraday, PutWallIntraday, GammaFlipIntraday;
            public double CTransIntraday, PTransIntraday, DexPlusIntraday, DexMinusIntraday;
            public double PinStrike0DTE;
            public double? Skew25d, TermSlopeVp, Ivr, Vix;
            public string VixRegime; // "LOW"/"NORMAL"/"ELEVATED"/"EXTREME"/null
            public DateTime WriteTimeUtc;
        }

        private System.Timers.Timer refreshTimer;
        private readonly object ctxLock = new object();
        private Ctx pendingCtx;
        private volatile bool ctxDirty;
        private Ctx activeCtx;
        private DateTime lastFileWriteUtc = DateTime.MinValue;
        private bool lastFileWriteKnown;

        private int lastScore;
        private string lastReason = "";
        private string lastTag = "NEUTRAL";

        private static readonly SimpleFont scoreFont = new SimpleFont("Arial", 20) { Bold = true };
        private static readonly SimpleFont reasonFont = new SimpleFont("Arial", 10);

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "GexContextScore";
                Description = "Directional context score (-100..+100) derived from GexLevels_NQ.csv -- GEX wall position, gamma flip, gamma zone, DEX magnets, skew/term, 0DTE pin, with VIX-based blocking/attenuating filters. See file header for what's NOT ported from the reference tool (macro blackout).";
                Calculate = Calculate.OnBarClose;
                IsOverlay = false; // own sub-panel, like any oscillator
                DisplayInDataBox = false;
                IsSuspendedWhileInactive = true;

                GexFilePath = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexLevels_NQ.csv");
                RefreshCheckSeconds = 120;
                StaleHoursThreshold = 20;
                EnableBlockingFilters = true;
                ShowStatusText = true;

                BullishStrongBrush = Brushes.Lime;
                BullishMildBrush = Brushes.MediumSeaGreen;
                NeutralBrush = Brushes.Gray;
                BearishMildBrush = Brushes.Orange;
                BearishStrongBrush = Brushes.Red;

                AddPlot(new Stroke(Brushes.Gray, 4), PlotStyle.Bar, "Score");
                AddLine(Brushes.DimGray, 0, "Zero");
            }
            else if (State == State.DataLoaded)
            {
                lastFileWriteUtc = DateTime.MinValue;
                lastFileWriteKnown = false;
                lastScore = 0;
                lastReason = "";
                lastTag = "NEUTRAL";
            }
            else if (State == State.Historical)
            {
                refreshTimer = new System.Timers.Timer(Math.Max(5, RefreshCheckSeconds) * 1000.0);
                refreshTimer.Elapsed += (s, e) => CheckAndLoadFile();
                refreshTimer.AutoReset = true;
                refreshTimer.Start();
                CheckAndLoadFile();
            }
            else if (State == State.Terminated)
            {
                if (refreshTimer != null)
                {
                    refreshTimer.Stop();
                    refreshTimer.Dispose();
                    refreshTimer = null;
                }
            }
        }

        // ── File polling (timer thread) -- same lock + dirty-flag hand-off pattern as
        // GexLevels_v1_0_0.cs's own CheckAndLoadFile, kept independent per the module comment.
        private void CheckAndLoadFile()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(GexFilePath) || !File.Exists(GexFilePath))
                    return;

                DateTime writeUtc = File.GetLastWriteTimeUtc(GexFilePath);
                if (lastFileWriteKnown && writeUtc == lastFileWriteUtc)
                    return;

                Ctx ctx = ParseContext(GexFilePath);
                ctx.WriteTimeUtc = writeUtc;

                lock (ctxLock) { pendingCtx = ctx; }
                ctxDirty = true;
                lastFileWriteUtc = writeUtc;
                lastFileWriteKnown = true;
            }
            catch (Exception ex)
            {
                Print("GexContextScore file read/parse failed: " + ex.Message);
            }
        }

        /// <summary>Reads only what ComputeScore needs: the Intraday-bucket wall/flip/
        /// transition/DEX levels, the 0DTE Pin Strike, and the scalar '#'-comment tokens
        /// (skew_25d/term_slope_vp/ivr/vix/vix_regime). Same file format GexLevels_v1_0_0.cs
        /// parses in full -- this is a narrower, independent read of the same rows.</summary>
        private Ctx ParseContext(string path)
        {
            var ctx = new Ctx { Loaded = true };
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("#"))
                {
                    string skewTok = ExtractToken(line, "skew_25d=");
                    if (skewTok != null) ctx.Skew25d = ParseNullableDouble(skewTok);
                    string termTok = ExtractToken(line, "term_slope_vp=");
                    if (termTok != null) ctx.TermSlopeVp = ParseNullableDouble(termTok);
                    string ivrTok = ExtractToken(line, "ivr=");
                    if (ivrTok != null) ctx.Ivr = ParseNullableDouble(ivrTok);
                    string vixTok = ExtractToken(line, "vix=");
                    if (vixTok != null) ctx.Vix = ParseNullableDouble(vixTok);
                    string vixRegimeTok = ExtractToken(line, "vix_regime=");
                    if (vixRegimeTok != null) ctx.VixRegime = vixRegimeTok.ToUpperInvariant();
                    continue;
                }

                if (line.StartsWith("Price,", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] parts = line.Split(',');
                if (parts.Length < 4)
                    continue;

                double price;
                if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out price))
                    continue;
                string type = parts[2].Trim();
                string group = parts[3].Trim();

                if (group == "Intraday")
                {
                    switch (type)
                    {
                        case "CallWall": ctx.CallWallIntraday = price; break;
                        case "PutWall": ctx.PutWallIntraday = price; break;
                        case "ZeroGamma": ctx.GammaFlipIntraday = price; break;
                        case "CTrans": ctx.CTransIntraday = price; break;
                        case "PTrans": ctx.PTransIntraday = price; break;
                        case "DexPlus": ctx.DexPlusIntraday = price; break;
                        case "DexMinus": ctx.DexMinusIntraday = price; break;
                    }
                }
                else if (group == "ZeroDTE" && type == "PinStrike")
                {
                    ctx.PinStrike0DTE = price;
                }
            }
            return ctx;
        }

        private static double? ParseNullableDouble(string token)
        {
            double v;
            if (token != null && token != "n/a"
                && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v;
            return null;
        }

        private static string ExtractToken(string line, string key)
        {
            int idx = line.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0)
                return null;
            int start = idx + key.Length;
            int end = line.IndexOf(' ', start);
            string token = end < 0 ? line.Substring(start) : line.Substring(start, end - start);
            return token.Trim();
        }

        protected override void OnBarUpdate()
        {
            if (ctxDirty)
            {
                lock (ctxLock) { activeCtx = pendingCtx; }
                ctxDirty = false;
            }

            if (CurrentBar < 0)
                return;

            // Score is a "right now" read against today's single CSV snapshot -- computing it
            // against an old historical bar's Close would silently mix today's GEX levels with
            // a stale price, same reasoning the reference tool documents for its own
            // live-bar-only restriction. State.Realtime is NinjaTrader's own standard signal
            // that historical backfill is done and this bar is genuinely live -- simpler and
            // more robust here than replicating the reference's own bar-index bookkeeping
            // (ATAS's OnCalculate model recalculates every tick including on historical bars,
            // which NinjaTrader's Calculate.OnBarClose does not, so the two aren't a direct
            // match anyway).
            if (State != State.Realtime)
                return;

            var (score, reason, tag) = ComputeScore(Close[0], activeCtx);
            lastScore = score;
            lastReason = reason;
            lastTag = tag;

            Values[0][0] = score;
            // PlotBrushes is indexed by bars-ago, exactly like Values ([0] = current/live bar).
            PlotBrushes[0][0] = BrushForScore(score);

            if (ShowStatusText)
                DrawScoreText();
        }

        /// <summary>Line-for-line port of the reference tool's own ComputeScore -- see the
        /// module comment for the three documented deviations (no macro blackout filter, a
        /// staleness block instead of data_quality=error/partial, local-time instead of UTC
        /// for the 0DTE-pin afternoon window).</summary>
        private (int score, string reason, string tag) ComputeScore(double spot, Ctx ctx)
        {
            if (!ctx.Loaded)
                return (0, "No data", "DATA");

            if (EnableBlockingFilters)
            {
                if (ctx.VixRegime == "EXTREME")
                    return (0, "VIX EXTREME (" + (ctx.Vix.HasValue ? ctx.Vix.Value.ToString("F1", CultureInfo.InvariantCulture) : "?") + ") -- STOP", "BLOCKED");
                if (lastFileWriteKnown && (DateTime.UtcNow - ctx.WriteTimeUtc).TotalHours > StaleHoursThreshold)
                    return (0, "STALE data -- check Refresh / Task Scheduler", "BLOCKED");
            }

            int score = 0;
            var reasons = new List<string>();

            double cw = ctx.CallWallIntraday;
            double pw = ctx.PutWallIntraday;
            bool hasRange = cw > pw && pw > 0;
            double range = hasRange ? cw - pw : 0;

            // 1. Wall position (+/-30), gradient in the PutWall-CallWall range
            if (hasRange)
            {
                double ratio = (spot - pw) / range;
                int wallScore;
                if (ratio < 0)
                {
                    wallScore = -30;
                    reasons.Add("PW broke");
                }
                else if (ratio > 1)
                {
                    wallScore = 30;
                    reasons.Add("CW broke");
                }
                else
                {
                    wallScore = (int)Math.Round((0.5 - ratio) * 60);
                    if (wallScore >= 15) reasons.Add("PW zone");
                    else if (wallScore <= -15) reasons.Add("CW zone");
                }
                score += wallScore;
            }

            // 2. Gamma Flip (+/-15), gradient, 0 at the flip itself
            if (ctx.GammaFlipIntraday > 0 && hasRange)
            {
                double normalized = (spot - ctx.GammaFlipIntraday) / range;
                normalized = Math.Max(-0.5, Math.Min(0.5, normalized));
                int gfScore = (int)Math.Round(normalized * 30);
                score += gfScore;
                if (gfScore >= 5) reasons.Add("GF+");
                else if (gfScore <= -5) reasons.Add("GF-");
            }

            // 3. Gamma zone (+/-20), bucketed by cTrans/pTrans/walls
            if (hasRange)
            {
                double ct = ctx.CTransIntraday, pt = ctx.PTransIntraday;
                if (spot > cw) { score += 20; reasons.Add("squeeze+"); }
                else if (ct > 0 && spot >= ct) { score += 5; reasons.Add("pos-gamma"); }
                else if (pt > 0 && spot >= pt) { /* transition zone: 0 */ }
                else if (spot >= pw) { score -= 5; reasons.Add("neg-gamma"); }
                else { score -= 20; reasons.Add("squeeze-"); }
            }

            // 4. DEX D+/D- (+/-15), gradient within 25% of the range
            if (hasRange)
            {
                double dexZone = range * 0.25;
                if (ctx.DexPlusIntraday > 0)
                {
                    double dist = ctx.DexPlusIntraday - spot;
                    if (dist > 0 && dist < dexZone)
                    {
                        int dScore = (int)Math.Round((1 - dist / dexZone) * 15);
                        score += dScore;
                        if (dScore >= 5) reasons.Add("D+ pull");
                    }
                }
                if (ctx.DexMinusIntraday > 0)
                {
                    double dist = spot - ctx.DexMinusIntraday;
                    if (dist > 0 && dist < dexZone)
                    {
                        int dScore = (int)Math.Round((1 - dist / dexZone) * 15);
                        score -= dScore;
                        if (dScore >= 5) reasons.Add("D- pull");
                    }
                }
            }

            // 5. Skew 25d (up to -10)
            if (ctx.Skew25d.HasValue && ctx.Skew25d.Value > 0.05) { score -= 10; reasons.Add("skew>5vp"); }
            else if (ctx.Skew25d.HasValue && ctx.Skew25d.Value > 0.03) { score -= 5; }

            // 6. Term IV backwardation (-5)
            if (ctx.TermSlopeVp.HasValue && ctx.TermSlopeVp.Value > 0.01) { score -= 5; reasons.Add("term-back"); }

            // 7. 0DTE Pin afternoon (+/-5) -- 14:00-16:00 local (assumed ET, see module comment)
            int localHour = DateTime.Now.Hour;
            if (localHour >= 14 && localHour <= 16 && ctx.PinStrike0DTE > 0 && hasRange)
            {
                double pinZone = range * 0.10;
                double pinDist = Math.Abs(spot - ctx.PinStrike0DTE);
                if (pinDist < pinZone)
                {
                    int sign = spot > ctx.PinStrike0DTE ? -1 : 1;
                    score += sign * 5;
                    reasons.Add("0DTE pin");
                }
            }

            // Attenuators
            double mult = 1.0;
            if (ctx.VixRegime == "ELEVATED") mult *= 0.6;
            if (ctx.Ivr.HasValue && ctx.Ivr.Value > 90) mult *= 0.5;
            score = (int)Math.Round(score * mult);
            score = Math.Max(-100, Math.Min(100, score));

            string tag = score >= 70 ? "BULLISH HIGH"
                       : score >= 30 ? "BULLISH"
                       : score > -30 ? "NEUTRAL"
                       : score > -70 ? "BEARISH"
                       : "BEARISH HIGH";

            string reason = reasons.Count > 0 ? string.Join(" | ", reasons) : "neutral";
            return (score, reason, tag);
        }

        private Brush BrushForScore(int score)
        {
            if (score >= 70) return BullishStrongBrush;
            if (score >= 30) return BullishMildBrush;
            if (score > -30) return NeutralBrush;
            if (score > -70) return BearishMildBrush;
            return BearishStrongBrush;
        }

        private void DrawScoreText()
        {
            try
            {
                string text = (lastScore > 0 ? "+" : "") + lastScore + "  [" + lastTag + "]"
                              + (lastReason.Length > 0 ? "\n" + lastReason : "");
                // BottomLeft used to collide with GexLevels_v1_0_0's own status block --
                // both indicators anchor to a fixed corner, and when they share a panel that
                // meant this text and GexLevels' "GEX levels as of / NORMAL / Skew / IVR / ..."
                // block landed on the exact same pixels, stacking unreadably. BottomRight
                // keeps this score readable regardless of panel layout. Opacity bumped to
                // match GexLevels' own fix (60 -> 92) so it isn't see-through either.
                Draw.TextFixed(this, "GexContextScoreText", text, TextPosition.BottomRight,
                    BrushForScore(lastScore), scoreFont, Brushes.Transparent, Brushes.Black, 92);
            }
            catch (Exception ex)
            {
                Print("GexContextScore DrawScoreText failed: " + ex.Message);
            }
        }

        public override string DisplayName
        {
            get { return "GexContextScore"; }
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "GEX CSV File Path", GroupName = "1 Source", Order = 1, Description = "Same CSV GexLevels_v1_0_0 reads -- keep these two pointed at the same file.")]
        public string GexFilePath { get; set; }

        [NinjaScriptProperty]
        [Range(5, 3600)]
        [Display(Name = "Refresh Check (seconds)", GroupName = "1 Source", Order = 2)]
        public int RefreshCheckSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 168)]
        [Display(Name = "Stale After (hours)", GroupName = "1 Source", Order = 3, Description = "Score is blocked to 0 when the CSV is older than this -- see the STALE blocking filter.")]
        public int StaleHoursThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable blocking filters", Description = "Forces score to 0 on VIX EXTREME or stale data. Macro-blackout blocking is NOT implemented -- see file header comment.", GroupName = "2 Score", Order = 1)]
        public bool EnableBlockingFilters { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show score text", GroupName = "2 Score", Order = 2)]
        public bool ShowStatusText { get; set; }

        [XmlIgnore]
        [Display(Name = "Bullish HIGH (>=70) Color", GroupName = "3 Colors", Order = 1)]
        public Brush BullishStrongBrush { get; set; }
        [Browsable(false)]
        public string BullishStrongBrushSerializable
        {
            get { return Serialize.BrushToString(BullishStrongBrush); }
            set { BullishStrongBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bullish (30-69) Color", GroupName = "3 Colors", Order = 2)]
        public Brush BullishMildBrush { get; set; }
        [Browsable(false)]
        public string BullishMildBrushSerializable
        {
            get { return Serialize.BrushToString(BullishMildBrush); }
            set { BullishMildBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Neutral (-29..29) Color", GroupName = "3 Colors", Order = 3)]
        public Brush NeutralBrush { get; set; }
        [Browsable(false)]
        public string NeutralBrushSerializable
        {
            get { return Serialize.BrushToString(NeutralBrush); }
            set { NeutralBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bearish (-69..-30) Color", GroupName = "3 Colors", Order = 4)]
        public Brush BearishMildBrush { get; set; }
        [Browsable(false)]
        public string BearishMildBrushSerializable
        {
            get { return Serialize.BrushToString(BearishMildBrush); }
            set { BearishMildBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bearish HIGH (<=-70) Color", GroupName = "3 Colors", Order = 5)]
        public Brush BearishStrongBrush { get; set; }
        [Browsable(false)]
        public string BearishStrongBrushSerializable
        {
            get { return Serialize.BrushToString(BearishStrongBrush); }
            set { BearishStrongBrush = Serialize.StringToBrush(value); }
        }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private GexContextScore[] cacheGexContextScore;
		public GexContextScore GexContextScore(string gexFilePath, int refreshCheckSeconds, int staleHoursThreshold, bool enableBlockingFilters, bool showStatusText)
		{
			return GexContextScore(Input, gexFilePath, refreshCheckSeconds, staleHoursThreshold, enableBlockingFilters, showStatusText);
		}

		public GexContextScore GexContextScore(ISeries<double> input, string gexFilePath, int refreshCheckSeconds, int staleHoursThreshold, bool enableBlockingFilters, bool showStatusText)
		{
			if (cacheGexContextScore != null)
				for (int idx = 0; idx < cacheGexContextScore.Length; idx++)
					if (cacheGexContextScore[idx] != null && cacheGexContextScore[idx].GexFilePath == gexFilePath && cacheGexContextScore[idx].RefreshCheckSeconds == refreshCheckSeconds && cacheGexContextScore[idx].StaleHoursThreshold == staleHoursThreshold && cacheGexContextScore[idx].EnableBlockingFilters == enableBlockingFilters && cacheGexContextScore[idx].ShowStatusText == showStatusText && cacheGexContextScore[idx].EqualsInput(input))
						return cacheGexContextScore[idx];
			return CacheIndicator<GexContextScore>(new GexContextScore(){ GexFilePath = gexFilePath, RefreshCheckSeconds = refreshCheckSeconds, StaleHoursThreshold = staleHoursThreshold, EnableBlockingFilters = enableBlockingFilters, ShowStatusText = showStatusText }, input, ref cacheGexContextScore);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.GexContextScore GexContextScore(string gexFilePath, int refreshCheckSeconds, int staleHoursThreshold, bool enableBlockingFilters, bool showStatusText)
		{
			return indicator.GexContextScore(Input, gexFilePath, refreshCheckSeconds, staleHoursThreshold, enableBlockingFilters, showStatusText);
		}

		public Indicators.GexContextScore GexContextScore(ISeries<double> input , string gexFilePath, int refreshCheckSeconds, int staleHoursThreshold, bool enableBlockingFilters, bool showStatusText)
		{
			return indicator.GexContextScore(input, gexFilePath, refreshCheckSeconds, staleHoursThreshold, enableBlockingFilters, showStatusText);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.GexContextScore GexContextScore(string gexFilePath, int refreshCheckSeconds, int staleHoursThreshold, bool enableBlockingFilters, bool showStatusText)
		{
			return indicator.GexContextScore(Input, gexFilePath, refreshCheckSeconds, staleHoursThreshold, enableBlockingFilters, showStatusText);
		}

		public Indicators.GexContextScore GexContextScore(ISeries<double> input , string gexFilePath, int refreshCheckSeconds, int staleHoursThreshold, bool enableBlockingFilters, bool showStatusText)
		{
			return indicator.GexContextScore(input, gexFilePath, refreshCheckSeconds, staleHoursThreshold, enableBlockingFilters, showStatusText);
		}
	}
}

#endregion
