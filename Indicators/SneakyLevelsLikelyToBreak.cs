// SneakyLevelsLikelyToBreak  -  NinjaTrader 8 indicator
// Port of "Levels Likely to Break" by Zeiierman (TradingView, Pine v6).
// Original: (c) Zeiierman, licensed CC BY-NC-SA 4.0 (https://creativecommons.org/licenses/by-nc-sa/4.0/).
// This port is a derivative work: keep this credit, share alike, non-commercial use only.
//
// What it does (same rules as the Pine script):
//  * Finds swing highs / lows (Pivot Length bars each side). Two same-side pivots within Match % of each other,
//    at least Min Confirm Gap bars apart, form a level (zone depth = Zone ATR x ATR14).
//  * A pressure score (0-100%) per level is updated every bar from proximity, approach, delta proxy, absorption,
//    compression and fresh touches. Higher = thicker, more opaque level.
//  * A level breaks on a wick (or close) beyond the level + Break ATR x ATR. Breakout triangles are drawn on the break bar.
//  * Broken levels can stay as faded history; the first retest of a broken level gets a diamond.
// Repaint note: every decision is taken on a closed bar and never changes afterwards. Levels are drawn back at the pivot
// bar (like the original), although they are only known ~Pivot Length bars after the swing.
// Plots (index): 0 UpperBreak (+1 on the break bar), 1 LowerBreak (-1 on the break bar), 6 BreakSignal (+1 / -1 / 0), 2 NearestResistance, 3 NearestSupport (price, 0 = none),
// 4 ResistancePressure, 5 SupportPressure (0-100). UpperBreak / LowerBreak are also exposed as properties for strategies.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public enum ZlbBreakMode { Wick, Close }

    public class SneakyLevelsLikelyToBreak : Indicator
    {
        private const int SeedMax = 120, LiveMax = 40, DeadMax = 50, MarkMax = 20 /* Pine: 80 labels, 4 per marker */, VolLen = 20, AbsRad = 2, RemoveTouchAfterBreakBars = 20;

        private class Seed { public double p; public DateTime t; public int b; public double vq, dq, aq, dl; }
        private class Mk   { public int id; public DateTime t; public double p; public bool upper, core, alive = true; }
        private class Lvl
        {
            public int id; public double p, depth, quality, pressure, attackRv, attackDelta, seedPx, pivPx;
            public int confirms, touches, bornBar, breakBar = -1, lastTouchBar = int.MinValue;
            public bool upper, inZone, retested, broken;
            public DateTime anchorTime, seedTime, confirmTime, breakTime, retestTime;
            public Mk lastTouch;
        }

        private ATR atr; private SMA avgVol;
                private readonly List<Seed> upSeeds = new List<Seed>(), dnSeeds = new List<Seed>();
        private readonly List<Lvl> live = new List<Lvl>(), dead = new List<Lvl>();
        private readonly List<Mk> marks = new List<Mk>();
        private HashSet<string> prevTags = new HashSet<string>();
        private int lvlId, markId; private bool loggedOnce;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Levels Likely to Break (port of Zeiierman's TradingView indicator).";
                Name = "SneakyLevelsLikelyToBreak";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true; DisplayInDataBox = true; DrawOnPricePanel = true; IsAutoScale = false; PaintPriceMarkers = false;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite; IsSuspendedWhileInactive = true;

                PivotLength = 20; MatchPct = 0.90; MinConfirmGap = 2; DeleteAfterBars = 600;
                ZoneAtr = 1.12; PressureSensitivity = 1.00;
                UpperBrush = new SolidColorBrush(Color.FromRgb(24, 206, 92)); UpperBrush.Freeze();
                LowerBrush = new SolidColorBrush(Color.FromRgb(255, 61, 70)); LowerBrush.Freeze();
                CoreBrush  = new SolidColorBrush(Color.FromRgb(255, 205, 64)); CoreBrush.Freeze();
                ShowRings = true; ShowTouchMarkers = true; ShowPairTrace = true; KeepHistory = true; ShowRetests = true;
                LevelLineStyle = DashStyleHelper.Solid; LevelLineWidth = 0;
                PairTraceBrush = new SolidColorBrush(Color.FromRgb(255, 205, 64)); PairTraceBrush.Freeze();
                PairTraceStyle = DashStyleHelper.Dot; PairTraceWidth = 1;
                BreakMode = ZlbBreakMode.Wick; BreakAtr = 0.05; ShowTriangles = true; AlertOnBreak = false;

                // Plots for 3rd-party automation tools (all hidden by default; recolor in the indicator's Plots list if you want to see them).
                AddPlot(Brushes.Transparent, "UpperBreak");        // 0: 1 on the bar a resistance level breaks, else 0
                AddPlot(Brushes.Transparent, "LowerBreak");        // 1: -1 on the bar a support level breaks, else 0
                AddPlot(Brushes.Transparent, "NearestResistance"); // 2: price of the nearest live upper level (0 = none)
                AddPlot(Brushes.Transparent, "NearestSupport");    // 3: price of the nearest live lower level (0 = none)
                AddPlot(Brushes.Transparent, "ResistancePressure");// 4: break pressure 0-100 of that upper level (0 = none)
                AddPlot(Brushes.Transparent, "SupportPressure");   // 5: break pressure 0-100 of that lower level (0 = none)
                AddPlot(Brushes.Transparent, "BreakSignal");       // 6: +1 green triangle, -1 red triangle, 0 none (0 if both on one bar)
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(14); avgVol = SMA(Volume, VolLen);
                upSeeds.Clear(); dnSeeds.Clear(); live.Clear(); dead.Clear(); marks.Clear(); prevTags = new HashSet<string>();
                lvlId = 0; markId = 0;
            }
        }

        // ---------------------------------------------------------------- helpers
        private static double Clip(double x) { return Math.Max(0.0, Math.Min(1.0, x)); }
        private Brush Col(bool upper) { return upper ? UpperBrush : LowerBrush; }
        private static Brush Tr(Brush b, int transparency)
        {
            SolidColorBrush sc = b as SolidColorBrush; Color c = sc != null ? sc.Color : Colors.Gray;
            transparency = Math.Max(0, Math.Min(100, transparency));
            SolidColorBrush r = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255.0 * (100 - transparency) / 100.0), c.R, c.G, c.B));
            r.Freeze(); return r;
        }
        private static int LineAlpha(double p) { return p >= 0.82 ? 0 : p >= 0.62 ? 12 : p >= 0.42 ? 28 : 46; }
        private static int BaseWidth(double p) { return p >= 0.82 ? 4 : p >= 0.62 ? 3 : p >= 0.42 ? 2 : 1; }
        private int MainWidth(double p) { return LevelLineWidth > 0 ? LevelLineWidth : BaseWidth(p); }
        private static int OuterAlpha(double p) { return (int)Math.Round(96.0 - 12.0 * p); }
        private static int InnerAlpha(double p) { return (int)Math.Round(91.0 - 18.0 * p); }
        private static void ZoneBounds(bool upper, double p, double d, out double top, out double bot) { top = upper ? p : p + d; bot = upper ? p - d : p; }
        private static void InnerBounds(bool upper, double p, double d, out double top, out double bot) { top = upper ? p : p + d * 0.45; bot = upper ? p - d * 0.45 : p; }
        private static int Rings(double score) { return score >= 82 ? 4 : score >= 66 ? 3 : score >= 50 ? 2 : 1; }

        private TimeSpan BarSpan()
        {
            if (BarsPeriod != null && BarsPeriod.BarsPeriodType == BarsPeriodType.Minute) return TimeSpan.FromMinutes(BarsPeriod.Value);
            if (CurrentBar > 0) return Time[0] - Time[1];
            return TimeSpan.FromMinutes(1);
        }

        // ---------------------------------------------------------------- pivots
        // Pine ta.pivothigh/low(piv, piv): the bar piv back must beat every older bar strictly and every newer bar non-strictly
        // (exact-tie handling may differ from TradingView).
        private bool IsPivotHigh(int n)
        {
            double v = High[n];
            for (int k = 1; k <= n; k++) { if (High[n + k] >= v) return false; if (High[n - k] > v) return false; }
            return true;
        }
        private bool IsPivotLow(int n)
        {
            double v = Low[n];
            for (int k = 1; k <= n; k++) { if (Low[n + k] <= v) return false; if (Low[n - k] < v) return false; }
            return true;
        }

        private void PivotFeatures(int off, out double vq, out double dqAbs, out double aq, out double dl)
        {
            double pv = Math.Max(Volume[off], 0.0);
            double vb = Math.Max(avgVol[off], 1.0);
            vq = Clip((pv / vb - 0.8) / 1.7);
            int rad = Math.Min(AbsRad, off);
            double dNum = 0, dDen = 0; aq = 0;
            for (int n = 0; n <= rad * 2; n++)
            {
                int o = off - rad + n;
                double range = Math.Max(High[o] - Low[o], TickSize);
                double v = Math.Max(Volume[o], 0.0);
                double pos = Clip((Close[o] - Low[o]) / range);
                dNum += v * (pos * 2.0 - 1.0); dDen += v;
                double bs = Math.Max(avgVol[o], 1.0);
                double rv = v / bs;
                double body = Math.Abs(Close[o] - Open[o]) / range;
                double absQ = Clip((rv - 1.0) / 1.0) * Clip((0.45 - body) / 0.45);
                aq = Math.Max(aq, absQ);
            }
            dl = dDen > 0 ? dNum / dDen : 0.0;
            dqAbs = Math.Abs(dl);
        }

        // ---------------------------------------------------------------- level lifecycle
        private void PruneSeeds(bool upper, List<Seed> a)
        {
            for (int i = a.Count - 1; i >= 0; i--)
            {
                Seed s = a[i]; double tol = s.p * MatchPct * 0.01;
                bool invalid = upper ? High[0] > s.p + tol : Low[0] < s.p - tol;
                if (invalid) a.RemoveAt(i);
            }
        }

        private int FindLive(bool upper, double p)
        {
            int id = -1; double best = 1e20;
            for (int i = 0; i < live.Count; i++)
            {
                Lvl lv = live[i];
                if (lv.upper != upper) continue;
                double d = Math.Abs(p - lv.p), tol = lv.p * MatchPct * 0.01;
                if (d <= tol && d < best) { best = d; id = i; if (d == 0.0) break; }
            }
            return id;
        }

        private void AddMark(DateTime t, double p, bool upper, bool core, Lvl lv)
        {
            Mk m = new Mk { id = ++markId, t = t, p = p, upper = upper, core = core };
            marks.Add(m);
            while (marks.Count > MarkMax) { marks[0].alive = false; marks.RemoveAt(0); }
            lv.lastTouch = m;
        }

        private void DeleteLvl(Lvl lv) { if (lv.lastTouch != null) lv.lastTouch.alive = false; }

        private bool MakeLvl(bool upper, double anchor, DateTime anchorTime, DateTime confirmTime, DateTime seedTime, double seedPx, double pivPx,
                             double atrP, double matchQ, double vq, double dq, double aq, double formDelta)
        {
            double quality = Clip(0.42 * matchQ + 0.20 * vq + 0.18 * dq + 0.20 * aq);
            double pressure = Clip(0.28 + 0.58 * quality);
            double depth = Math.Max(atrP * ZoneAtr, TickSize * 2.0);
            Lvl lv = new Lvl
            {
                id = ++lvlId, p = anchor, depth = depth, quality = quality, pressure = pressure, attackRv = 1.0,
                attackDelta = upper ? formDelta : -formDelta, confirms = 2, touches = 0, bornBar = CurrentBar, upper = upper,
                anchorTime = anchorTime, seedTime = seedTime, confirmTime = confirmTime, seedPx = seedPx, pivPx = pivPx
            };
            live.Add(lv);
            while (live.Count > LiveMax) { Lvl o = live[0]; live.RemoveAt(0); DeleteLvl(o); }
            return true;
        }

        private bool Reconfirm(int id, double p, DateTime pivTime, int pivBar, double vq, double dq, double aq, double dl)
        {
            Lvl lv = live[id];
            double tol = Math.Max(lv.p * MatchPct * 0.01, TickSize);
            double closeQ = Clip(1.0 - Math.Abs(p - lv.p) / tol);
            double featureQ = Clip(0.40 * closeQ + 0.20 * vq + 0.18 * dq + 0.22 * aq);
            lv.quality = Clip(lv.quality * 0.76 + featureQ * 0.24);
            lv.pressure = Clip(lv.pressure + 0.05 + 0.07 * closeQ);
            lv.attackDelta = lv.attackDelta * 0.75 + (lv.upper ? dl : -dl) * 0.25;
            lv.confirms++;
            if (ShowTouchMarkers) { AddMark(pivTime, lv.p, lv.upper, true, lv); lv.lastTouchBar = pivBar; }
            return true;
        }

        private void RegisterLvl(bool upper, double p, DateTime pivTime, int pivBar, double atrP, double vq, double dq, double aq, double dl)
        {
            List<Seed> seeds = upper ? upSeeds : dnSeeds;
            int liveId = FindLive(upper, p);
            if (liveId >= 0) { Reconfirm(liveId, p, pivTime, pivBar, vq, dq, aq, dl); return; }
            int pick = -1; double best = 1e20;
            for (int i = 0; i < seeds.Count; i++)
            {
                Seed s = seeds[i]; double tol = s.p * MatchPct * 0.01, dist = Math.Abs(p - s.p);
                if (dist <= tol && pivBar - s.b >= MinConfirmGap && dist < best) { best = dist; pick = i; if (dist == 0.0) break; }
            }
            if (pick == -1)
            {
                seeds.Add(new Seed { p = p, t = pivTime, b = pivBar, vq = vq, dq = dq, aq = aq, dl = dl });
                while (seeds.Count > SeedMax) seeds.RemoveAt(0);
            }
            else
            {
                Seed s = seeds[pick];
                double tol = Math.Max(s.p * MatchPct * 0.01, TickSize);
                double matchQ = Clip(1.0 - Math.Abs(p - s.p) / tol);
                double anchor = upper ? Math.Max(s.p, p) : Math.Min(s.p, p);
                DateTime anchorTime = upper ? (p > s.p ? pivTime : s.t) : (p < s.p ? pivTime : s.t);
                MakeLvl(upper, anchor, anchorTime, pivTime, s.t, s.p, p, atrP, matchQ, (s.vq + vq) * 0.5, (s.dq + dq) * 0.5, Math.Max(s.aq, aq), (s.dl + dl) * 0.5);
                seeds.RemoveAt(pick);
            }
        }

        private bool IsBreak(Lvl lv, double atrNow)
        {
            double pad = Math.Max(atrNow * BreakAtr, 0.0);
            if (BreakMode == ZlbBreakMode.Close) return lv.upper ? Close[0] > lv.p + pad : Close[0] < lv.p - pad;
            return lv.upper ? High[0] > lv.p + pad : Low[0] < lv.p - pad;
        }

        private void UpdatePressure(Lvl lv, double atrSafe, double relVol, double barDelta, double absorption, double compression,
                                    double upApproach, double dnApproach, double upDelta, double dnDelta)
        {
            double zTop, zBot; ZoneBounds(lv.upper, lv.p, lv.depth, out zTop, out zBot);
            bool inside = High[0] >= zBot && Low[0] <= zTop;
            bool fresh = inside && !lv.inZone && CurrentBar > lv.bornBar;
            double proximity = Clip(1.0 - Math.Abs(Close[0] - lv.p) / (atrSafe * 1.50));
            double approach = lv.upper ? upApproach : dnApproach;
            double deltaPressure = lv.upper ? upDelta : dnDelta;
            double load = proximity * (0.30 * approach + 0.26 * deltaPressure + 0.22 * absorption + 0.10 * compression) + (fresh ? 0.12 : 0.0);
            double decay = proximity < 0.15 ? 0.990 : 0.997;
            lv.pressure = Clip(lv.pressure * decay + load * 0.10 * PressureSensitivity);
            lv.inZone = inside;
            if (proximity > 0.35)
            {
                lv.attackRv = lv.attackRv * 0.85 + relVol * 0.15;
                double signed = lv.upper ? barDelta : -barDelta;
                lv.attackDelta = lv.attackDelta * 0.85 + signed * 0.15;
            }
            if (fresh)
            {
                lv.touches++;
                if (ShowTouchMarkers) { AddMark(Time[0], lv.p, lv.upper, relVol >= 1.45, lv); lv.lastTouchBar = CurrentBar; }
            }
        }

        private bool BreakLvl(int id)
        {
            Lvl lv = live[id];
            bool recentTouch = lv.lastTouchBar != int.MinValue && CurrentBar - lv.lastTouchBar <= RemoveTouchAfterBreakBars;
            if (recentTouch && lv.lastTouch != null) { lv.lastTouch.alive = false; lv.lastTouch = null; }
            lv.broken = true; lv.breakBar = CurrentBar; lv.breakTime = Time[0];
            live.RemoveAt(id);
            if (KeepHistory) { dead.Add(lv); while (dead.Count > DeadMax) { Lvl o = dead[0]; dead.RemoveAt(0); DeleteLvl(o); } }
            else DeleteLvl(lv);
            return lv.upper;
        }

        private void ManageLive(out bool upBreak, out bool dnBreak)
        {
            upBreak = false; dnBreak = false;
            if (live.Count == 0) return;
            double atrNow = atr[0];
            double atrSafe = Math.Max(atrNow, TickSize);
            double volBase = Math.Max(avgVol[0], 1.0);
            double relVol = Math.Max(Volume[0], 0.0) / volBase;
            double barRange = Math.Max(High[0] - Low[0], TickSize);
            double closePos = Clip((Close[0] - Low[0]) / barRange);
            double barDelta = closePos * 2.0 - 1.0;
            double bodyFrac = Math.Abs(Close[0] - Open[0]) / barRange;
            double absorption = Clip((relVol - 0.9) / 1.2) * Clip((0.55 - bodyFrac) / 0.55);
            double compression = Clip(1.0 - barRange / atrSafe);
            double upApproach = Clip(Math.Max(Close[0] - Close[1], 0.0) / atrSafe);
            double dnApproach = Clip(Math.Max(Close[1] - Close[0], 0.0) / atrSafe);
            double upDelta = Clip(Math.Max(barDelta, 0.0) * relVol / 2.0);
            double dnDelta = Clip(Math.Max(-barDelta, 0.0) * relVol / 2.0);
            for (int i = live.Count - 1; i >= 0; i--)
            {
                Lvl lv = live[i];
                if (IsBreak(lv, atrNow)) { bool wasUpper = BreakLvl(i); upBreak = upBreak || wasUpper; dnBreak = dnBreak || !wasUpper; }
                else if (CurrentBar - lv.bornBar >= DeleteAfterBars) { live.RemoveAt(i); DeleteLvl(lv); }
                else UpdatePressure(lv, atrSafe, relVol, barDelta, absorption, compression, upApproach, dnApproach, upDelta, dnDelta);
            }
        }

        private void ManageRetests()
        {
            if (!(KeepHistory && ShowRetests)) return;
            for (int i = 0; i < dead.Count; i++)
            {
                Lvl lv = dead[i];
                if (lv.retested || CurrentBar <= lv.breakBar) continue;
                bool retest = lv.upper ? (Low[0] <= lv.p && Close[0] > lv.p) : (High[0] >= lv.p && Close[0] < lv.p);
                if (retest) { lv.retested = true; lv.retestTime = Time[0]; }
            }
        }

        // ---------------------------------------------------------------- main
        protected override void OnBarUpdate()
        {
            for (int pi = 0; pi < 7; pi++) Values[pi][0] = 0;
            if (CurrentBar < 2 * PivotLength + VolLen + 5) return;

            PruneSeeds(true, upSeeds); PruneSeeds(false, dnSeeds);
            bool upBreak, dnBreak;
            ManageLive(out upBreak, out dnBreak);
            ManageRetests();

            bool evUp = IsPivotHigh(PivotLength), evDn = IsPivotLow(PivotLength);
            if (evUp || evDn)
            {
                double vq, dqa, aq, dl;
                PivotFeatures(PivotLength, out vq, out dqa, out aq, out dl);
                double atrP = Math.Max(atr[PivotLength] > 0 ? atr[PivotLength] : atr[0], TickSize);
                if (evUp) RegisterLvl(true, High[PivotLength], Time[PivotLength], CurrentBar - PivotLength, atrP, vq, dqa, aq, dl);
                if (evDn) RegisterLvl(false, Low[PivotLength], Time[PivotLength], CurrentBar - PivotLength, atrP, vq, dqa, aq, dl);
            }

            if (upBreak) Values[0][0] = 1;
            if (dnBreak) Values[1][0] = -1;
            Values[6][0] = (upBreak && !dnBreak) ? 1 : (dnBreak && !upBreak) ? -1 : 0;
            Lvl nu = null, nd = null; double bu = double.MaxValue, bd = double.MaxValue;
            for (int i = 0; i < live.Count; i++)
            {
                Lvl lv = live[i]; double d = Math.Abs(lv.p - Close[0]);
                if (lv.upper) { if (d < bu) { bu = d; nu = lv; } } else { if (d < bd) { bd = d; nd = lv; } }
            }
            if (nu != null) { Values[2][0] = nu.p; Values[4][0] = Math.Round(nu.pressure * 100.0, 1); }
            if (nd != null) { Values[3][0] = nd.p; Values[5][0] = Math.Round(nd.pressure * 100.0, 1); }
            if (ShowTriangles)
            {
                if (upBreak) Draw.TriangleUp(this, "zlbU" + CurrentBar, false, 0, MIN(Low, 5)[0] - TickSize, UpperBrush);
                if (dnBreak) Draw.TriangleDown(this, "zlbD" + CurrentBar, false, 0, MAX(High, 5)[0] + TickSize, LowerBrush);
            }
            if (AlertOnBreak && State == State.Realtime && (upBreak || dnBreak))
                Alert("zlbBrk" + CurrentBar, Priority.Medium, (upBreak ? "Upper" : "Lower") + " zone broken", NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav", 10, Brushes.Black, Brushes.White);

            // Levels are drawn once, on the last bar (history is processed without drawing), then refreshed every new bar.
            if (CurrentBar >= Count - 2 || State == State.Realtime)
            {
                try { RedrawAll(); }
                catch (Exception ex) { Print("SneakyLevelsLikelyToBreak redraw error: " + ex.Message + " | " + ex.StackTrace); }
            }
        }

        // ---------------------------------------------------------------- drawing
        private void DrawLvl(Lvl lv, DateTime right, HashSet<string> cur)
        {
            Brush c = Col(lv.upper); string t = "zlb" + lv.id;
            double zTop, zBot, iTop, iBot; ZoneBounds(lv.upper, lv.p, lv.depth, out zTop, out zBot); InnerBounds(lv.upper, lv.p, lv.depth, out iTop, out iBot);
            Brush outerLine, lineBrush, dotBrush; int outerOp, innerOp, width; DashStyleHelper style;
            if (!lv.broken)
            {
                outerLine = Tr(c, Math.Min(82, LineAlpha(lv.pressure) + 30)); outerOp = 100 - OuterAlpha(lv.pressure); innerOp = 100 - InnerAlpha(lv.pressure);
                lineBrush = Tr(c, LineAlpha(lv.pressure)); width = MainWidth(lv.pressure); style = LevelLineStyle;
                dotBrush = Tr(CoreBrush, lv.pressure * 100.0 >= 66 ? 0 : 22);
            }
            else
            {
                outerLine = Tr(c, 78); outerOp = 5; innerOp = 8; lineBrush = Tr(c, 55); width = 2; style = DashStyleHelper.Dot; dotBrush = Tr(CoreBrush, 52);
            }
            Draw.Rectangle(this, t + "_o", false, lv.anchorTime, zTop, right, zBot, outerLine, c, outerOp); cur.Add(t + "_o");
            Draw.Rectangle(this, t + "_i", false, lv.anchorTime, iTop, right, iBot, Brushes.Transparent, c, innerOp); cur.Add(t + "_i");
            Draw.Line(this, t + "_a", false, lv.anchorTime, lv.p, right, lv.p, lineBrush, style, width); cur.Add(t + "_a");
            if (ShowPairTrace)
            {
                Draw.Line(this, t + "_p", false, lv.seedTime, lv.seedPx, lv.confirmTime, lv.pivPx, lv.broken ? Tr(PairTraceBrush, 75) : PairTraceBrush, PairTraceStyle, PairTraceWidth);
                cur.Add(t + "_p");
            }
            if (ShowRings) { Draw.Dot(this, t + "_r", false, lv.anchorTime, lv.p, dotBrush); cur.Add(t + "_r"); }
            if (lv.retested) { Draw.Diamond(this, t + "_rt", false, lv.retestTime, lv.p, c); cur.Add(t + "_rt"); }
        }

        private void RedrawAll()
        {
            HashSet<string> cur = new HashSet<string>();
            DateTime right = Time[0].Add(BarSpan() + BarSpan());
            for (int i = 0; i < live.Count; i++) DrawLvl(live[i], right, cur);
            if (KeepHistory) for (int i = 0; i < dead.Count; i++) DrawLvl(dead[i], dead[i].breakTime, cur);
            for (int i = 0; i < marks.Count; i++)
            {
                Mk m = marks[i]; if (!m.alive) continue;
                string tag = "zlbm" + m.id;
                Draw.Diamond(this, tag, false, m.t, m.p, m.core ? CoreBrush : Col(m.upper)); cur.Add(tag);
            }
            foreach (string tag in prevTags) if (!cur.Contains(tag)) RemoveDrawObject(tag);
            prevTags = cur;
            if (!loggedOnce) { loggedOnce = true; Print("SneakyLevelsLikelyToBreak: live=" + live.Count + " broken=" + dead.Count + " markers=" + marks.Count + " drawn objects=" + cur.Count); }
        }

        // ---------------------------------------------------------------- strategy hooks
        [Browsable(false), XmlIgnore] public Series<double> UpperBreak { get { Update(); return Values[0]; } }
        [Browsable(false), XmlIgnore] public Series<double> LowerBreak { get { Update(); return Values[1]; } }

        #region Properties
        [NinjaScriptProperty, Range(1, 100)] [Display(Name = "Pivot length", GroupName = "1. Detection", Order = 0)] public int PivotLength { get; set; }
        [NinjaScriptProperty, Range(0.01, 100.0)] [Display(Name = "Match %", GroupName = "1. Detection", Order = 1)] public double MatchPct { get; set; }
        [NinjaScriptProperty, Range(1, 1000)] [Display(Name = "Min confirm gap (bars)", GroupName = "1. Detection", Order = 2)] public int MinConfirmGap { get; set; }
        [NinjaScriptProperty, Range(1, 100000)] [Display(Name = "Delete after (bars)", GroupName = "1. Detection", Order = 3)] public int DeleteAfterBars { get; set; }

        [NinjaScriptProperty, Range(0.01, 20.0)] [Display(Name = "Zone ATR", GroupName = "2. Pressure engine", Order = 0)] public double ZoneAtr { get; set; }
        [NinjaScriptProperty, Range(0.25, 2.0)] [Display(Name = "Pressure sensitivity", GroupName = "2. Pressure engine", Order = 1)] public double PressureSensitivity { get; set; }

        [NinjaScriptProperty] [Display(Name = "Score dots", GroupName = "3. Visuals", Order = 0)] public bool ShowRings { get; set; }
        [NinjaScriptProperty] [Display(Name = "Touch markers", GroupName = "3. Visuals", Order = 1)] public bool ShowTouchMarkers { get; set; }
        [NinjaScriptProperty] [Display(Name = "Pivot pair trace", GroupName = "3. Visuals", Order = 2)] public bool ShowPairTrace { get; set; }
        [NinjaScriptProperty] [Display(Name = "Broken history", GroupName = "3. Visuals", Order = 3)] public bool KeepHistory { get; set; }
        [NinjaScriptProperty] [Display(Name = "Retests", GroupName = "3. Visuals", Order = 4)] public bool ShowRetests { get; set; }
        [NinjaScriptProperty] [Display(Name = "Level line style", GroupName = "3. Visuals", Order = 5)] public DashStyleHelper LevelLineStyle { get; set; }
        [NinjaScriptProperty, Range(0, 5)] [Display(Name = "Level width (0 = by pressure)", GroupName = "3. Visuals", Order = 6)] public int LevelLineWidth { get; set; }
        [NinjaScriptProperty] [Display(Name = "Pair trace style", GroupName = "3. Visuals", Order = 7)] public DashStyleHelper PairTraceStyle { get; set; }
        [NinjaScriptProperty, Range(1, 5)] [Display(Name = "Pair trace width", GroupName = "3. Visuals", Order = 8)] public int PairTraceWidth { get; set; }

        [XmlIgnore] [Display(Name = "Upper (resistance) color", GroupName = "4. Colors", Order = 0)] public Brush UpperBrush { get; set; }
        [Browsable(false)] public string UpperBrushSerialize { get { return Serialize.BrushToString(UpperBrush); } set { UpperBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Lower (support) color", GroupName = "4. Colors", Order = 1)] public Brush LowerBrush { get; set; }
        [Browsable(false)] public string LowerBrushSerialize { get { return Serialize.BrushToString(LowerBrush); } set { LowerBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Score core color", GroupName = "4. Colors", Order = 2)] public Brush CoreBrush { get; set; }
        [Browsable(false)] public string CoreBrushSerialize { get { return Serialize.BrushToString(CoreBrush); } set { CoreBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore] [Display(Name = "Pair trace color", GroupName = "4. Colors", Order = 3)] public Brush PairTraceBrush { get; set; }
        [Browsable(false)] public string PairTraceBrushSerialize { get { return Serialize.BrushToString(PairTraceBrush); } set { PairTraceBrush = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty] [Display(Name = "Break trigger", GroupName = "5. Breakouts", Order = 0)] public ZlbBreakMode BreakMode { get; set; }
        [NinjaScriptProperty, Range(0.0, 10.0)] [Display(Name = "Break ATR buffer", GroupName = "5. Breakouts", Order = 1)] public double BreakAtr { get; set; }
        [NinjaScriptProperty] [Display(Name = "Breakout triangles", GroupName = "5. Breakouts", Order = 2)] public bool ShowTriangles { get; set; }
        [NinjaScriptProperty] [Display(Name = "Alert on break", GroupName = "5. Breakouts", Order = 3)] public bool AlertOnBreak { get; set; }
        #endregion
    }
}
