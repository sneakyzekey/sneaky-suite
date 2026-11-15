#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows;
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
// SneakySvpSignals_v1_0_0 — volume-profile trade signals as plots, for
// SneakyTradeHUD's AUTO TRADE (or any tool that reads indicator plots).
//
// Original Sneaky Suite work. It is the on-chart version of the five textbook
// session-volume-profile setups that were backtested in Python on exported NQ
// 1-minute data (88 trading days, 11 Jun - 16 Oct 2026). The rules here are the
// same rules, bar for bar. Standalone: it builds its own session profile and
// does not need SneakyStrykSvp on the chart (the two agree on POC / value area
// when both use the same bin size).
//
// WHAT THE BACKTEST FOUND (read this before automating anything):
//   1 Rotation inside value      106 trades, PF 0.95  — no edge
//   2 Failed breakout             61 trades, PF 1.01  — no edge
//   3 Acceptance outside value    81 trades, PF 1.03  — no edge
//   4 Naked POC magnet            13 trades, PF 2.23  — too few trades to trust
//   5 IB break (all)              78 trades, PF 0.74  — lost
//     IB break, narrow IB         40 trades, PF 0.37  — lost clearly
//     IB break, wide IB           29 trades, PF 1.24  — not significant
// These plots exist so the setups can be watched, forward-tested and filtered.
// They are not a tested profitable system.
//
// PLOTS (each is +1 on a LONG signal bar, -1 on a SHORT signal bar, 0 otherwise)
//   Rotation, FailedBreakout, Acceptance, NakedPoc, IbBreak, IbBreakWide,
//   IbBreakNarrow            — pick these in the HUD ("equals 1" / "equals -1")
//   SignalStop, SignalTarget — the setup's own stop and target PRICE for the
//                              most recent signal (0 until the first signal)
//
// THE RULES (regular hours only; one signal per setup per side per day)
//   1 Rotation        after the first hour, a bar touches the developing value
//                     area edge and closes back inside. Target developing POC,
//                     stop 2 points beyond the bar.
//   2 Failed breakout a close outside the PRIOR session's value area, then a
//                     close back inside. Target prior POC, stop 2 points beyond
//                     the excursion's extreme.
//   3 Acceptance      15 straight closes outside the prior value area. Trade
//                     with it. Stop just inside the edge (10% of the value
//                     area's width, at least 10 points), target 2R.
//   4 Naked POC       on the first regular-hours bar, trade toward the nearest
//                     prior POC that is still untouched and sits 0.15-0.75 of a
//                     typical day's range away. Equal-distance stop.
//   5 IB break        first close outside the first hour's range. Stop at the
//                     IB midpoint, target one IB range. "Wide" / "narrow" compare
//                     this IB with the median of the last 20.
//
// NOTES
//   * Built and tested for a 1-MINUTE chart. Other bar sizes change every rule.
//   * Regular-hours times are CHART time (defaults 9:30-16:00 suit a chart in
//     US Eastern time). The session itself comes from the Trading Hours template.
//   * SneakyTradeHUD enters with ITS OWN fixed stop / target ticks. It does not
//     read SignalStop / SignalTarget, so a HUD trade will not have the setup's
//     stop and target unless you set comparable distances in the HUD.
// ═══════════════════════════════════════════════════════════════════════════
namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakySvpSignals_v1_0_0 : Indicator
    {
        // session profile, keyed by bin index (price / bin size, halves rounded up like Pine)
        private Dictionary<int, double> vol = new Dictionary<int, double>();
        private double binPx;
        private double sessHi = double.NaN, sessLo = double.NaN, preHi = double.NaN, preLo = double.NaN;
        private bool havePrev; private double pPoc, pVah, pVal;
        private readonly List<double> naked = new List<double>();
        private readonly List<double> dayRanges = new List<double>();
        private readonly List<double> ibHist = new List<double>();

        // per-day state
        private bool rthSeen, done1L, done1S, done2Up, done2Dn, done3, done5;
        private int outSide, runLen, runSide; private double ext;
        private double ibH = double.NaN, ibL = double.NaN;
        private double lastStop, lastTarget;
        private int sigSeq;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Sneaky SVP Signals";
                Description = "Five session-volume-profile setups as +1 / -1 signal plots for SneakyTradeHUD's AUTO TRADE. "
                            + "Backtested on NQ 1-minute data: none showed a reliable edge. See the notes at the top of the file.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                IsAutoScale = false;
                DisplayInDataBox = true;
                PaintPriceMarkers = false;
                DrawOnPricePanel = true;

                BinTicks = 4; ValueAreaPct = 70;
                RthStart = 930; RthEnd = 1600; IbMinutes = 60;
                ShowMarkers = true; ShowLevels = true;

                AddPlot(Brushes.Transparent, "Rotation");
                AddPlot(Brushes.Transparent, "FailedBreakout");
                AddPlot(Brushes.Transparent, "Acceptance");
                AddPlot(Brushes.Transparent, "NakedPoc");
                AddPlot(Brushes.Transparent, "IbBreak");
                AddPlot(Brushes.Transparent, "IbBreakWide");
                AddPlot(Brushes.Transparent, "IbBreakNarrow");
                AddPlot(Brushes.Transparent, "SignalStop");
                AddPlot(Brushes.Transparent, "SignalTarget");
            }
            else if (State == State.DataLoaded)
            {
                binPx = TickSize * BinTicks;
            }
        }

        private int BinOf(double px) { return (int)Math.Floor(px / binPx + 0.5); }
        private void Spread(double lo, double hi, double v)
        {
            if (v <= 0) return;
            int k0 = BinOf(lo), k1 = BinOf(hi);
            int nb = Math.Max(1, Math.Min(400, k1 - k0 + 1));
            double share = v / nb;
            for (int k = k0; k < k0 + nb; k++) { double c; vol.TryGetValue(k, out c); vol[k] = c + share; }
        }
        // same POC / value-area maths as SneakyStrykSvp (and the Pine original)
        private bool Analyse(out double poc, out double vah, out double val)
        {
            poc = vah = val = double.NaN;
            if (vol.Count == 0) return false;
            int f = vol.Keys.Min(), l = vol.Keys.Max(), n = l - f + 1;
            double[] vs = new double[n]; double tot = 0, pocV = -1; int pi = 0;
            for (int i = 0; i < n; i++)
            {
                double v; vol.TryGetValue(f + i, out v); vs[i] = v; tot += v;
                if (v > pocV) { pocV = v; pi = i; }
            }
            double target = tot * ValueAreaPct / 100.0, acc = pocV; int lo = pi, hi = pi;
            while (acc < target && (lo > 0 || hi < n - 1))
            {
                double below = lo > 0 ? vs[lo - 1] : -1.0, above = hi < n - 1 ? vs[hi + 1] : -1.0;
                if (above >= below) { hi++; acc += above; } else { lo--; acc += below; }
            }
            poc = (f + pi) * binPx; vah = (f + hi) * binPx + binPx / 2; val = (f + lo) * binPx - binPx / 2;
            return true;
        }
        private static double Median(List<double> xs, int last)
        {
            var s = xs.Skip(Math.Max(0, xs.Count - last)).OrderBy(x => x).ToList();
            return s.Count == 0 ? double.NaN : (s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0);
        }

        private void Fire(int plot, int side, double stop, double target, string name)
        {
            double entry = Close[0];
            if (Math.Abs(entry - stop) <= 0 || (target - entry) * side <= 0) return;   // same validity test as the backtest
            Values[plot][0] = side;
            lastStop = stop; lastTarget = target;
            Values[7][0] = stop; Values[8][0] = target;
            sigSeq++;
            Brush c = side > 0 ? Brushes.LimeGreen : Brushes.OrangeRed;
            if (ShowMarkers)
            {
                if (side > 0) Draw.ArrowUp(this, "SvpSigA" + sigSeq, false, 0, Low[0] - 4 * TickSize, c);
                else Draw.ArrowDown(this, "SvpSigA" + sigSeq, false, 0, High[0] + 4 * TickSize, c);
                Draw.Text(this, "SvpSigT" + sigSeq, false, name, 0, side > 0 ? Low[0] - 4 * TickSize : High[0] + 4 * TickSize,
                    side > 0 ? -22 : 22, c, new SimpleFont("Segoe UI", 10), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            }
            if (ShowLevels)
            {
                DateTime x0 = Time[0], x1 = Time[0].AddMinutes(15);
                Draw.Line(this, "SvpSigS" + sigSeq, false, x0, stop, x1, stop, Brushes.OrangeRed, DashStyleHelper.Dash, 1);
                Draw.Line(this, "SvpSigG" + sigSeq, false, x0, target, x1, target, Brushes.LimeGreen, DashStyleHelper.Dash, 1);
            }
        }

        protected override void OnBarUpdate()
        {
            for (int p = 0; p < 7; p++) Values[p][0] = 0;
            Values[7][0] = lastStop; Values[8][0] = lastTarget;

            // ── session roll: finish the old session's profile, start a new one ──
            if (Bars.IsFirstBarOfSession)
            {
                double poc, vah, val;
                if (!double.IsNaN(sessHi) && Analyse(out poc, out vah, out val))
                {
                    naked.RemoveAll(p => sessLo <= p && p <= sessHi);   // touched this session -> no longer naked
                    pPoc = poc; pVah = vah; pVal = val; havePrev = true;
                    naked.Add(poc); while (naked.Count > 10) naked.RemoveAt(0);
                    dayRanges.Add(sessHi - sessLo);
                    if (!double.IsNaN(ibH)) ibHist.Add(ibH - ibL);
                }
                vol.Clear();
                sessHi = sessLo = preHi = preLo = double.NaN;
                rthSeen = done1L = done1S = done2Up = done2Dn = done3 = done5 = false;
                outSide = 0; runLen = 0; runSide = 0; ext = double.NaN; ibH = ibL = double.NaN;
            }

            DateTime st = BarsPeriod.BarsPeriodType == BarsPeriodType.Minute ? Time[0].AddMinutes(-BarsPeriod.Value) : Time[0];
            int m = st.Hour * 60 + st.Minute;
            int rth0 = RthStart / 100 * 60 + RthStart % 100, rth1 = RthEnd / 100 * 60 + RthEnd % 100;
            bool inRth = m >= rth0 && m < rth1;
            bool roomLeft = m < rth1 - 30;    // no new entries in the last 30 minutes
            double c = Close[0];

            if (inRth)
            {
                // ── 4: naked POC, on the first regular-hours bar ──
                if (!rthSeen)
                {
                    rthSeen = true;
                    double rng = dayRanges.Count >= 5 ? Median(dayRanges, 20) : double.NaN;
                    if (!double.IsNaN(rng))
                    {
                        double best = double.NaN;
                        foreach (double p in naked)
                        {
                            double dist = Math.Abs(p - c);
                            bool touchedOvernight = !double.IsNaN(preLo) && preLo <= p && p <= preHi;
                            if (dist < 0.15 * rng || dist > 0.75 * rng || touchedOvernight) continue;
                            if (double.IsNaN(best) || dist < Math.Abs(best - c)) best = p;
                        }
                        if (!double.IsNaN(best))
                        {
                            int side = best > c ? 1 : -1;
                            Fire(3, side, c - side * Math.Abs(best - c), best, "Naked POC");
                        }
                    }
                }

                if (m < rth0 + IbMinutes)
                {
                    ibH = double.IsNaN(ibH) ? High[0] : Math.Max(ibH, High[0]);
                    ibL = double.IsNaN(ibL) ? Low[0] : Math.Min(ibL, Low[0]);
                }
                else if (roomLeft)
                {
                    // ── 1: rotation at the developing value-area edge (profile through the PREVIOUS bar) ──
                    double poc, vah, val;
                    if (Analyse(out poc, out vah, out val))
                    {
                        if (!done1L && Low[0] <= val && c > val && poc - c > 2) { Fire(0, 1, Low[0] - 2, poc, "Rotation"); done1L = true; }
                        if (!done1S && High[0] >= vah && c < vah && c - poc > 2) { Fire(0, -1, High[0] + 2, poc, "Rotation"); done1S = true; }
                    }
                    // ── 5: initial-balance break ──
                    if (!done5 && !double.IsNaN(ibH))
                    {
                        int side = c > ibH ? 1 : c < ibL ? -1 : 0;
                        if (side != 0)
                        {
                            done5 = true;
                            double ibr = ibH - ibL, stop = (ibH + ibL) / 2.0, tgt = c + side * ibr;
                            Fire(4, side, stop, tgt, "IB break");
                            if (ibHist.Count >= 10 && Values[4][0] != 0)
                                Values[ibr < Median(ibHist, 20) ? 6 : 5][0] = side;
                        }
                    }
                }

                // ── 2 and 3: against the prior session's value area ──
                if (havePrev && roomLeft)
                {
                    int pos = c > pVah ? 1 : c < pVal ? -1 : 0;
                    if (outSide != 0 && pos == 0)
                    {
                        if (outSide > 0 && !done2Up) { Fire(1, -1, ext + 2, pPoc, "Failed breakout"); done2Up = true; }
                        if (outSide < 0 && !done2Dn) { Fire(1, 1, ext - 2, pPoc, "Failed breakout"); done2Dn = true; }
                    }
                    if (pos != 0)
                        ext = outSide != pos ? (pos > 0 ? High[0] : Low[0]) : (pos > 0 ? Math.Max(ext, High[0]) : Math.Min(ext, Low[0]));
                    outSide = pos;

                    runLen = (pos != 0 && pos == runSide) ? runLen + 1 : (pos != 0 ? 1 : 0);
                    runSide = pos;
                    if (runLen == 15 && !done3)
                    {
                        done3 = true;
                        double edge = pos > 0 ? pVah : pVal, w = pVah - pVal;
                        double stop = edge - pos * Math.Max(10, 0.1 * w), risk = Math.Abs(c - stop);
                        Fire(2, pos, stop, c + pos * 2 * risk, "Acceptance");
                    }
                }
            }
            else if (!rthSeen)
            {
                preHi = double.IsNaN(preHi) ? High[0] : Math.Max(preHi, High[0]);
                preLo = double.IsNaN(preLo) ? Low[0] : Math.Min(preLo, Low[0]);
            }

            // this bar joins the profile only after the signals above have been read
            Spread(Low[0], High[0], Volume[0]);
            sessHi = double.IsNaN(sessHi) ? High[0] : Math.Max(sessHi, High[0]);
            sessLo = double.IsNaN(sessLo) ? Low[0] : Math.Min(sessLo, Low[0]);
        }

        #region Properties
        [NinjaScriptProperty] [Range(1, 100)] [Display(Name = "Price bin (ticks)", GroupName = "1. Profile", Order = 0)] public int BinTicks { get; set; }
        [NinjaScriptProperty] [Range(50, 95)] [Display(Name = "Value area (% of volume)", GroupName = "1. Profile", Order = 1)] public int ValueAreaPct { get; set; }
        [NinjaScriptProperty] [Range(0, 2359)] [Display(Name = "Regular hours start (HHmm, chart time)", GroupName = "2. Trading window", Order = 0)] public int RthStart { get; set; }
        [NinjaScriptProperty] [Range(0, 2359)] [Display(Name = "Regular hours end (HHmm, chart time)", GroupName = "2. Trading window", Order = 1)] public int RthEnd { get; set; }
        [NinjaScriptProperty] [Range(5, 240)] [Display(Name = "Initial balance (minutes)", GroupName = "2. Trading window", Order = 2)] public int IbMinutes { get; set; }
        [NinjaScriptProperty] [Display(Name = "Show signal arrows and names", GroupName = "3. Display", Order = 0)] public bool ShowMarkers { get; set; }
        [NinjaScriptProperty] [Display(Name = "Show each signal's stop and target", GroupName = "3. Display", Order = 1)] public bool ShowLevels { get; set; }

        [Browsable(false)] [XmlIgnore] public Series<double> Rotation { get { return Values[0]; } }
        [Browsable(false)] [XmlIgnore] public Series<double> FailedBreakout { get { return Values[1]; } }
        [Browsable(false)] [XmlIgnore] public Series<double> Acceptance { get { return Values[2]; } }
        [Browsable(false)] [XmlIgnore] public Series<double> NakedPoc { get { return Values[3]; } }
        [Browsable(false)] [XmlIgnore] public Series<double> IbBreak { get { return Values[4]; } }
        [Browsable(false)] [XmlIgnore] public Series<double> IbBreakWide { get { return Values[5]; } }
        [Browsable(false)] [XmlIgnore] public Series<double> IbBreakNarrow { get { return Values[6]; } }
        [Browsable(false)] [XmlIgnore] public Series<double> SignalStop { get { return Values[7]; } }
        [Browsable(false)] [XmlIgnore] public Series<double> SignalTarget { get { return Values[8]; } }
        #endregion
    }
}
