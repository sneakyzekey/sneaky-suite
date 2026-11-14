#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SDX = SharpDX;
using D2D = SharpDX.Direct2D1;
using DW  = SharpDX.DirectWrite;
#endregion

// SneakyIctMarketStructure
// NinjaTrader 8 port of "ICT Market Structure Screener" by Zeiierman (TradingView, Pine v5).
// Original work (c) Zeiierman, licensed CC BY-NC-SA 4.0
//   https://creativecommons.org/licenses/by-nc-sa/4.0/
// This port is an adaptation and is therefore distributed under the same license:
// attribution to Zeiierman required, NON-COMMERCIAL use only, share-alike.
//
// What it does (same as the original)
//   * Tracks a running range high (Up) and low (Dn). Pivot highs/lows (Period bars each side) overwrite them.
//   * A new range high above the previous one is bullish structure: CHoCH if the structure was bearish/neutral,
//     SMS the next time (only when the previous high had been unchanged for "Response" bars), then BMS for each further one.
//     A new range low below the previous one is the same thing in the bearish direction.
//   * Labels + lines on the chart, the active range (top/bottom lines + premium / mid / discount zones, 20 bars to the right)
//     or, in "Previous Range" mode, the range history as lines and filled zone bands.
//   * Screener table for other instruments (each runs its own structure on the chart's bar type), drawn on the chart.
//
// Differences from the TradingView script
//   * Symbols are NinjaTrader instrument names ("ES 12-26"), up to 8 slots, plus an option to list the chart instrument.
//     Every symbol uses its own bars (Pine re-uses the latest bar of the other symbol on each chart bar); for futures that
//     trade the same hours the result is the same.
//   * Pine's pivot tie rule is not documented. Here a bar is a pivot high when every bar to its left is lower and no bar to
//     its right is higher (the first of equal highs is the pivot). Pivot lows are the mirror image.
//   * The table is drawn with the chart renderer, so it follows the table position/size settings but not TradingView's "auto" size.
//
// Notes
//   * Keep Calculate on "On bar close" (default). The active range and the table are drawn by the chart renderer from the latest state.
//   * Automation plots (invisible; see README). Event plots are N/A (unset) on every bar except the event bar, so signal
//     scanners see them as sparse signals; code that reads them gets 0. The labels are tagged ict_BullCHoCH<bar>, ict_BullSMS<bar>,
//     ict_BullBMS<bar>, ict_BearCHoCH<bar>, ict_BearSMS<bar>, ict_BearBMS<bar> so scanners list each type separately.

namespace NinjaTrader.NinjaScript.Indicators
{
    public enum SneakyIctDisplay { ActiveRange, PreviousRange }
    public enum SneakyIctTablePos { TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight }
    public enum SneakyIctTableSize { Auto, Tiny, Small, Normal, Large, Huge }

    public class SneakyIctMarketStructure : Indicator
    {
        private sealed class Smc
        {
            public double Up, Dn, Close;
            public int Pos, IUp, IDn;
            public bool Ready;
            public int LastBar;
            public string Tick;
            public List<double> Ups = new List<double>();
            public List<double> Dns = new List<double>();
        }

        private struct Ev { public int Kind; public bool Bull; public int Center; public int X1; public double Y; }

        private const int MaxSlots = 8;
        private Smc cs;
        private List<Smc> secs = new List<Smc>();
        private List<string> secNames = new List<string>();
        private List<Ev> evs = new List<Ev>();

        private D2D.Brush dxHead, dxBull, dxBear, dxWhite, dxPrem, dxMid, dxDisc, dxTop, dxBot;

        // ------------------------------------------------------------------ lifecycle
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "ICT market structure (CHoCH / SMS / BMS) with premium & discount and a multi-symbol screener (port of Zeiierman).";
                Name                     = "SneakyIctMarketStructure";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsAutoScale              = false;   // invisible automation plots must not stretch the price scale
                DisplayInDataBox         = true;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = true;

                Period        = 20;
                UseResponse   = true;
                Response      = 7;
                DisplayMode   = SneakyIctDisplay.ActiveRange;
                ShowBullish   = true;
                ShowBearish   = true;
                BullBrush     = Rgb(0x08, 0xEC, 0x7E);
                BearBrush     = Rgb(0xFF, 0x22, 0x22);
                ShowZones     = true;
                PremiumBrush  = Argb(0x33, 0xFF, 0x22, 0x22);
                DiscountBrush = Argb(0x33, 0x08, 0xEC, 0x7E);
                MidBrush      = Argb(0x70, 0x7C, 0x80, 0x7E);
                RightExtend   = 20;
                LabelSize     = 10;
                LabelDistance = 6;

                ShowTable     = true;
                ColTicker = ColPrice = ColTrend = ColSignal = ColRange = ColPremium = ColMid = ColDiscount = true;
                TablePosition = SneakyIctTablePos.TopRight;
                TableSize     = SneakyIctTableSize.Normal;
                HeaderBrush   = Rgb(0x29, 0x62, 0xFF);
                TableBullBrush = Rgb(0x08, 0x99, 0x81);
                TableBearBrush = Rgb(0xF2, 0x36, 0x45);

                IncludeChart  = true;
                Symbol1 = "ES 12-26"; Symbol2 = "YM 12-26"; Symbol3 = "GC 12-26"; Symbol4 = "RTY 12-26";
                Symbol5 = ""; Symbol6 = ""; Symbol7 = ""; Symbol8 = "";

                PopupAlerts   = false;
                ZoneAlerts    = false;
                SoundFile     = "Alert2.wav";

                AddPlot(new Stroke(Brushes.Red, 2),         PlotStyle.Line, "RangeHighLine");   // 0: range high (Previous Range mode only)
                AddPlot(new Stroke(Brushes.Lime, 2),        PlotStyle.Line, "RangeLowLine");    // 1: range low  (Previous Range mode only)
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "BuySignal");       // 2: +1 on a bullish CHoCH bar, else N/A
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SellSignal");      // 3: -1 on a bearish CHoCH bar, else N/A
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Choch");           // 4: +1 bullish / -1 bearish CHoCH, else N/A
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Sms");             // 5: +1 / -1 SMS, else N/A
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Bms");             // 6: +1 / -1 BMS, else N/A
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Trend");           // 7: +1 bullish / -1 bearish structure, 0 before the first event
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "StructurePos");    // 8: +1 CHoCH, +2 SMS, +3.. BMS (negative for bearish)
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "RangeHigh");       // 9
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "RangeLow");        // 10
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "PremiumTop");      // 11
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "PremiumBottom");   // 12
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "MidTop");          // 13
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "MidBottom");       // 14
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "DiscountTop");     // 15
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "DiscountBottom");  // 16
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "InPremium");       // 17: 1 when close is inside the premium zone
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "InMid");           // 18
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "InDiscount");      // 19
            }
            else if (State == State.Configure)
            {
                Plots[0].Brush = BearBrush;
                Plots[1].Brush = BullBrush;

                secNames.Clear();
                string[] slots = { Symbol1, Symbol2, Symbol3, Symbol4, Symbol5, Symbol6, Symbol7, Symbol8 };
                foreach (string raw in slots)
                {
                    string nm = (raw ?? "").Trim();
                    if (nm.Length == 0) continue;
                    if (string.Equals(nm, Instrument.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (secNames.Exists(x => string.Equals(x, nm, StringComparison.OrdinalIgnoreCase))) continue;
                    try
                    {
                        Instrument ins = Instrument.GetInstrument(nm);
                        if (ins == null) { Print("SneakyIctMarketStructure: instrument not found: " + nm); continue; }
                        AddDataSeries(nm, BarsPeriod.BarsPeriodType, BarsPeriod.Value);
                        secNames.Add(nm);
                    }
                    catch (Exception ex) { Print("SneakyIctMarketStructure: could not add " + nm + ": " + ex.Message); }
                }
            }
            else if (State == State.DataLoaded)
            {
                cs = new Smc { Tick = Instrument.FullName };
                secs.Clear();
                foreach (string nm in secNames) secs.Add(new Smc { Tick = nm });
            }
            else if (State == State.Terminated)
            {
                DisposeBrushes();
            }
        }

        private static Brush Rgb(byte r, byte g, byte b)
        {
            Brush br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        private static Brush Argb(byte a, byte r, byte g, byte b)
        {
            Brush br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        // ------------------------------------------------------------------ engine (Pine SMCScreen / SmartMoneyStructure)
        private double PivotHigh(int bip)
        {
            if (CurrentBars[bip] < 2 * Period) return double.NaN;
            double c = Highs[bip][Period];
            for (int j = 1; j <= Period; j++)
            {
                if (Highs[bip][Period + j] >= c) return double.NaN;   // left side must be lower
                if (Highs[bip][Period - j] >  c) return double.NaN;   // right side may not be higher
            }
            return c;
        }

        private double PivotLow(int bip)
        {
            if (CurrentBars[bip] < 2 * Period) return double.NaN;
            double c = Lows[bip][Period];
            for (int j = 1; j <= Period; j++)
            {
                if (Lows[bip][Period + j] <= c) return double.NaN;
                if (Lows[bip][Period - j] <  c) return double.NaN;
            }
            return c;
        }

        private void Step(Smc d, double h, double l, double pvtHi, double pvtLo, int b)
        {
            if (!d.Ready) { d.Up = h; d.Dn = l; d.Ready = true; }
            d.Ups.Add(d.Up);
            d.Dns.Add(d.Dn);
            double prevUp = d.Up, prevDn = d.Dn;
            d.Up = Math.Max(prevUp, h);
            d.Dn = Math.Min(prevDn, l);
            if (!double.IsNaN(pvtHi)) d.Up = pvtHi;
            if (!double.IsNaN(pvtLo)) d.Dn = pvtLo;
            Structure(d, prevUp, prevDn, b);
        }

        private void Structure(Smc d, double rh, double rl, int b)
        {
            int n = UseResponse ? Response : Period;
            int idx = Math.Min(d.Ups.Count - 1, n);
            double refUp = d.Ups[d.Ups.Count - 1 - idx];
            double refDn = d.Dns[d.Dns.Count - 1 - idx];

            if (d.Up > rh)
            {
                int center = (int)Math.Floor((d.IUp + b) / 2.0 + 0.5);
                if (d.Pos <= 0)                         { evs.Add(new Ev { Kind = 0, Bull = true, Center = center, X1 = d.IUp, Y = rh }); d.Pos = 1; }
                else if (d.Pos == 1 && rh == refUp)     { evs.Add(new Ev { Kind = 1, Bull = true, Center = center, X1 = d.IUp, Y = rh }); d.Pos = 2; }
                else if (d.Pos > 1 && rh == refUp)      { evs.Add(new Ev { Kind = 2, Bull = true, Center = center, X1 = d.IUp, Y = rh }); d.Pos += 1; }
                d.IUp = b;
            }
            else if (d.Up < rh)
            {
                d.IUp = b - Period;
            }

            if (d.Dn < rl)
            {
                int center = (int)Math.Floor((d.IDn + b) / 2.0 + 0.5);
                if (d.Pos >= 0)                         { evs.Add(new Ev { Kind = 0, Bull = false, Center = center, X1 = d.IDn, Y = rl }); d.Pos = -1; }
                else if (d.Pos == -1 && rl == refDn)    { evs.Add(new Ev { Kind = 1, Bull = false, Center = center, X1 = d.IDn, Y = rl }); d.Pos = -2; }
                else if (d.Pos < -1 && rl == refDn)     { evs.Add(new Ev { Kind = 2, Bull = false, Center = center, X1 = d.IDn, Y = rl }); d.Pos -= 1; }
                d.IDn = b;
            }
            else if (d.Dn > rl)
            {
                d.IDn = b - Period;
            }
        }

        private static string KindName(int k) { return k == 0 ? "CHoCH" : (k == 1 ? "SMS" : "BMS"); }

        private void Levels(Smc d, out double premTop, out double premBot, out double midTop, out double midBot, out double discTop, out double discBot)
        {
            double r = d.Up - d.Dn;
            premTop = d.Up - r * 0.10;  premBot = d.Up - r * 0.25;
            discTop = d.Dn + r * 0.25;  discBot = d.Dn + r * 0.10;
            midTop  = d.Up - r * 0.45;  midBot  = d.Dn + r * 0.45;
        }

        private static string SigText(Smc d) { int a = Math.Abs(d.Pos); return a == 1 ? "CHoCH" : (a == 2 ? "SMS" : "BMS"); }

        // ------------------------------------------------------------------ main
        protected override void OnBarUpdate()
        {
            int k = BarsInProgress;
            if (k > 0)
            {
                if (k - 1 >= secs.Count) return;
                Smc s = secs[k - 1];
                evs.Clear();
                Step(s, Highs[k][0], Lows[k][0], PivotHigh(k), PivotLow(k), CurrentBars[k]);
                s.Close = Closes[k][0];
                if (PopupAlerts && State == State.Realtime)
                    foreach (Ev e in evs)
                        Notify(s.Tick + ": " + (e.Bull ? "Bullish " : "Bearish ") + KindName(e.Kind), s.Tick + KindName(e.Kind) + CurrentBars[k], e.Bull);
                evs.Clear();
                return;
            }

            int b = CurrentBar;
            for (int i = 0; i <= 6; i++) Values[i].Reset();   // sparse / mode-dependent plots default to N/A

            evs.Clear();
            Step(cs, High[0], Low[0], PivotHigh(0), PivotLow(0), b);
            cs.Close = Close[0];
            cs.LastBar = b;

            foreach (Ev e in evs)
            {
                double sign = e.Bull ? 1 : -1;
                if (e.Kind == 0) { Values[4][0] = sign; if (e.Bull) Values[2][0] = 1; else Values[3][0] = -1; }
                else if (e.Kind == 1) Values[5][0] = sign;
                else Values[6][0] = sign;

                if (e.Bull ? ShowBullish : ShowBearish)
                {
                    string side = e.Bull ? "Bull" : "Bear";
                    Brush col = e.Bull ? BullBrush : BearBrush;
                    Draw.Text(this, "ict_" + side + KindName(e.Kind) + b, false, KindName(e.Kind), b - Math.Max(0, e.Center), e.Y,
                        e.Bull ? -LabelDistance : LabelDistance, col, new SimpleFont("Arial", LabelSize),
                        System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                    Draw.Line(this, "ict_" + side + "Line" + b, false, b - Math.Max(0, e.X1), e.Y, 0, e.Y, col, DashStyleHelper.Solid, 1);
                }
                if (PopupAlerts && State == State.Realtime)
                    Notify(cs.Tick + ": " + (e.Bull ? "Bullish " : "Bearish ") + KindName(e.Kind), cs.Tick + side0(e) + KindName(e.Kind) + b, e.Bull);
            }
            evs.Clear();

            double pt, pb, mt, mb, dt, db;
            Levels(cs, out pt, out pb, out mt, out mb, out dt, out db);

            Values[7][0]  = cs.Pos > 0 ? 1 : (cs.Pos < 0 ? -1 : 0);
            Values[8][0]  = cs.Pos;
            Values[9][0]  = cs.Up;   Values[10][0] = cs.Dn;
            Values[11][0] = pt;      Values[12][0] = pb;
            Values[13][0] = mt;      Values[14][0] = mb;
            Values[15][0] = dt;      Values[16][0] = db;
            double c = Close[0];
            Values[17][0] = (c <= pt && c >= pb) ? 1 : 0;
            Values[18][0] = (c <= mt && c >= mb) ? 1 : 0;
            Values[19][0] = (c <= dt && c >= db) ? 1 : 0;

            if (DisplayMode == SneakyIctDisplay.PreviousRange)
            {
                Values[0][0] = cs.Up;
                Values[1][0] = cs.Dn;
            }

            if (ZoneAlerts && State == State.Realtime)
            {
                ZoneAlert(cs, cs.Close);
                foreach (Smc s in secs) if (s.Ready) ZoneAlert(s, s.Close);
            }
        }

        private static string side0(Ev e) { return e.Bull ? "Bull" : "Bear"; }

        private void ZoneAlert(Smc d, double price)
        {
            double pt, pb, mt, mb, dt, db;
            Levels(d, out pt, out pb, out mt, out mb, out dt, out db);
            string z = null;
            if (price <= pt && price >= pb) z = "Premium";
            else if (price <= mt && price >= mb) z = "Mid";
            else if (price <= dt && price >= db) z = "Discount";
            if (z != null) Notify(d.Tick + " in " + z + " area!", d.Tick + "zone" + CurrentBars[0], z != "Premium");
        }

        private void Notify(string msg, string id, bool bullish)
        {
            string snd = NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + SoundFile;
            Alert("ict_" + id, Priority.High, msg, snd, 10, Brushes.Black, bullish ? Brushes.LimeGreen : Brushes.OrangeRed);
        }

        // ------------------------------------------------------------------ rendering: zone bands (Previous Range) and the screener table
        public override void OnRenderTargetChanged()
        {
            DisposeBrushes();
            if (RenderTarget == null) return;
            dxHead  = HeaderBrush.ToDxBrush(RenderTarget);
            dxBull  = TableBullBrush.ToDxBrush(RenderTarget);
            dxBear  = TableBearBrush.ToDxBrush(RenderTarget);
            dxWhite = Brushes.White.ToDxBrush(RenderTarget);
            dxPrem  = PremiumBrush.ToDxBrush(RenderTarget);
            dxMid   = MidBrush.ToDxBrush(RenderTarget);
            dxDisc  = DiscountBrush.ToDxBrush(RenderTarget);
            dxTop   = BearBrush.ToDxBrush(RenderTarget);
            dxBot   = BullBrush.ToDxBrush(RenderTarget);
        }

        private static void Kill(ref D2D.Brush b) { if (b != null) { b.Dispose(); b = null; } }
        private void DisposeBrushes() { Kill(ref dxHead); Kill(ref dxBull); Kill(ref dxBear); Kill(ref dxWhite); Kill(ref dxPrem); Kill(ref dxMid); Kill(ref dxDisc); Kill(ref dxTop); Kill(ref dxBot); }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (Bars == null || ChartBars == null || RenderTarget == null || cs == null || dxHead == null) return;
            try
            {
                if (ShowZones && DisplayMode == SneakyIctDisplay.PreviousRange)
                {
                    DrawBand(chartControl, chartScale, 11, 12, dxPrem);
                    DrawBand(chartControl, chartScale, 13, 14, dxMid);
                    DrawBand(chartControl, chartScale, 15, 16, dxDisc);
                }
                if (DisplayMode == SneakyIctDisplay.ActiveRange) DrawActive(chartControl, chartScale);
                if (ShowTable) DrawTable();
            }
            catch { }
        }

        // Active range: drawn by the renderer from the latest state, so it is always current and needs no drawing objects.
        private void DrawActive(ChartControl cc, ChartScale sc)
        {
            if (!cs.Ready || dxTop == null) return;
            int last = cs.LastBar;
            float xl = cc.GetXByBarIndex(ChartBars, last);
            float xr = xl + RightExtend * cc.Properties.BarDistance;
            float xu = cc.GetXByBarIndex(ChartBars, Math.Max(0, cs.IUp));
            float xd = cc.GetXByBarIndex(ChartBars, Math.Max(0, cs.IDn));
            double pt, pb, mt, mb, dt, db;
            Levels(cs, out pt, out pb, out mt, out mb, out dt, out db);
            if (ShowZones)
            {
                float x0 = Math.Min(Math.Max(xu, xd), xr);
                Zone(x0, xr, sc, pt, pb, dxPrem);
                Zone(x0, xr, sc, mt, mb, dxMid);
                Zone(x0, xr, sc, dt, db, dxDisc);
            }
            float yu = sc.GetYByValue(cs.Up), yd = sc.GetYByValue(cs.Dn);
            RenderTarget.DrawLine(new SDX.Vector2(xu, yu), new SDX.Vector2(xr, yu), dxTop, 2f);
            RenderTarget.DrawLine(new SDX.Vector2(xd, yd), new SDX.Vector2(xr, yd), dxBot, 2f);
        }

        private void Zone(float x0, float x1, ChartScale sc, double top, double bot, D2D.Brush brush)
        {
            if (brush == null || x1 <= x0) return;
            float y0 = sc.GetYByValue(top), y1 = sc.GetYByValue(bot);
            RenderTarget.FillRectangle(new SDX.RectangleF(x0, y0, x1 - x0, y1 - y0), brush);
        }

        private bool Ok(int plot, int i)
        {
            if (i < 0 || !Values[plot].IsValidDataPointAt(i)) return false;
            double v = Values[plot].GetValueAt(i);
            return !double.IsNaN(v) && !double.IsInfinity(v) && v != 0;
        }

        private void DrawBand(ChartControl cc, ChartScale sc, int topPlot, int botPlot, D2D.Brush brush)
        {
            if (brush == null) return;
            int from = Math.Max(ChartBars.FromIndex, 0);
            int to   = Math.Min(ChartBars.ToIndex, Bars.Count - 1);
            int runStart = -1;
            for (int i = from; i <= to + 1; i++)
            {
                bool ok = i <= to && Ok(topPlot, i) && Ok(botPlot, i);
                if (ok && runStart < 0) runStart = i;
                if (!ok && runStart >= 0) { EmitBand(cc, sc, runStart, i - 1, topPlot, botPlot, brush); runStart = -1; }
            }
        }

        private void EmitBand(ChartControl cc, ChartScale sc, int s, int e, int topPlot, int botPlot, D2D.Brush brush)
        {
            if (e <= s) return;
            using (D2D.PathGeometry geo = new D2D.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
            {
                D2D.GeometrySink sink = geo.Open();
                sink.BeginFigure(new SDX.Vector2(cc.GetXByBarIndex(ChartBars, s), sc.GetYByValue(Values[topPlot].GetValueAt(s))), D2D.FigureBegin.Filled);
                for (int k = s + 1; k <= e; k++)
                    sink.AddLine(new SDX.Vector2(cc.GetXByBarIndex(ChartBars, k), sc.GetYByValue(Values[topPlot].GetValueAt(k))));
                for (int k = e; k >= s; k--)
                    sink.AddLine(new SDX.Vector2(cc.GetXByBarIndex(ChartBars, k), sc.GetYByValue(Values[botPlot].GetValueAt(k))));
                sink.EndFigure(D2D.FigureEnd.Closed);
                sink.Close();
                RenderTarget.FillGeometry(geo, brush);
            }
        }

        private static string Money(double v) { return "$" + v.ToString("#,##0.00", CultureInfo.InvariantCulture); }

        private float TableFont()
        {
            switch (TableSize)
            {
                case SneakyIctTableSize.Tiny:  return 9f;
                case SneakyIctTableSize.Small: return 10f;
                case SneakyIctTableSize.Large: return 15f;
                case SneakyIctTableSize.Huge:  return 20f;
                case SneakyIctTableSize.Auto:  return 11f;
                default: return 12f;
            }
        }

        private void DrawTable()
        {
            string[] heads = { "Ticker", "Price", "Trend", "Signal", "Range", "Premium", "Mid", "Discount" };
            bool[] on = { ColTicker, ColPrice, ColTrend, ColSignal, ColRange, ColPremium, ColMid, ColDiscount };

            List<Smc> rows = new List<Smc>();
            if (IncludeChart && cs.Ready) rows.Add(cs);
            foreach (Smc s in secs) if (s.Ready) rows.Add(s);
            List<int> cols = new List<int>();
            for (int j = 0; j < 8; j++) if (on[j]) cols.Add(j);
            if (cols.Count == 0) return;

            string[][] cells = new string[rows.Count + 1][];
            cells[0] = new string[cols.Count];
            for (int c = 0; c < cols.Count; c++) cells[0][c] = heads[cols[c]];
            for (int r = 0; r < rows.Count; r++)
            {
                Smc d = rows[r];
                double pt, pb, mt, mb, dt, db;
                Levels(d, out pt, out pb, out mt, out mb, out dt, out db);
                string[] all = { d.Tick, Money(d.Close), d.Pos > 0 ? "Bullish" : "Bearish", SigText(d),
                    Money(d.Up) + "-" + Money(d.Dn), Money(pt) + "-" + Money(pb), Money(mt) + "-" + Money(mb), Money(dt) + "-" + Money(db) };
                cells[r + 1] = new string[cols.Count];
                for (int c = 0; c < cols.Count; c++) cells[r + 1][c] = all[cols[c]];
            }

            float fs = TableFont(), padX = 7f, padY = 3f;
            using (DW.TextFormat tf = new DW.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", fs))
            {
                float[] colW = new float[cols.Count];
                float textH = 0f;
                for (int r = 0; r < cells.Length; r++)
                    for (int c = 0; c < cols.Count; c++)
                        using (DW.TextLayout tl = new DW.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, cells[r][c], tf, 2000f, 200f))
                        {
                            colW[c] = Math.Max(colW[c], tl.Metrics.Width + 2 * padX);
                            textH = Math.Max(textH, tl.Metrics.Height);
                        }
                float rowH = textH + 2 * padY;
                float totW = 0f; for (int c = 0; c < colW.Length; c++) totW += colW[c];
                float totH = rowH * cells.Length;

                float margin = 8f;
                float px = ChartPanel.X, py = ChartPanel.Y, pw = ChartPanel.W, ph = ChartPanel.H;
                int p = (int)TablePosition;
                int hx = p % 3, vy = p / 3;   // 0 left/top, 1 center/middle, 2 right/bottom
                float x0 = hx == 0 ? px + margin : (hx == 1 ? px + (pw - totW) / 2f : px + pw - totW - margin);
                float y0 = vy == 0 ? py + margin : (vy == 1 ? py + (ph - totH) / 2f : py + ph - totH - margin);

                for (int r = 0; r < cells.Length; r++)
                {
                    D2D.Brush bg = r == 0 ? dxHead : (rows[r - 1].Pos > 0 ? dxBull : dxBear);
                    float x = x0, y = y0 + r * rowH;
                    for (int c = 0; c < cols.Count; c++)
                    {
                        SDX.RectangleF rect = new SDX.RectangleF(x, y, colW[c], rowH);
                        RenderTarget.FillRectangle(rect, bg);
                        RenderTarget.DrawRectangle(rect, dxWhite, 1f);
                        using (DW.TextLayout tl = new DW.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, cells[r][c], tf, colW[c], rowH))
                            RenderTarget.DrawTextLayout(new SDX.Vector2(x + padX, y + padY), tl, dxWhite);
                        x += colW[c];
                    }
                }
            }
        }

        // ------------------------------------------------------------------ outputs for strategies / automation tools
        [Browsable(false), XmlIgnore] public Series<double> BuySignal      { get { Update(); return Values[2]; } }
        [Browsable(false), XmlIgnore] public Series<double> SellSignal     { get { Update(); return Values[3]; } }
        [Browsable(false), XmlIgnore] public Series<double> Choch          { get { Update(); return Values[4]; } }
        [Browsable(false), XmlIgnore] public Series<double> Sms            { get { Update(); return Values[5]; } }
        [Browsable(false), XmlIgnore] public Series<double> Bms            { get { Update(); return Values[6]; } }
        [Browsable(false), XmlIgnore] public Series<double> Trend          { get { Update(); return Values[7]; } }
        [Browsable(false), XmlIgnore] public Series<double> StructurePos   { get { Update(); return Values[8]; } }
        [Browsable(false), XmlIgnore] public Series<double> RangeHigh      { get { Update(); return Values[9]; } }
        [Browsable(false), XmlIgnore] public Series<double> RangeLow       { get { Update(); return Values[10]; } }
        [Browsable(false), XmlIgnore] public Series<double> PremiumTop     { get { Update(); return Values[11]; } }
        [Browsable(false), XmlIgnore] public Series<double> PremiumBottom  { get { Update(); return Values[12]; } }
        [Browsable(false), XmlIgnore] public Series<double> MidTop         { get { Update(); return Values[13]; } }
        [Browsable(false), XmlIgnore] public Series<double> MidBottom      { get { Update(); return Values[14]; } }
        [Browsable(false), XmlIgnore] public Series<double> DiscountTop    { get { Update(); return Values[15]; } }
        [Browsable(false), XmlIgnore] public Series<double> DiscountBottom { get { Update(); return Values[16]; } }
        [Browsable(false), XmlIgnore] public Series<double> InPremium      { get { Update(); return Values[17]; } }
        [Browsable(false), XmlIgnore] public Series<double> InMid          { get { Update(); return Values[18]; } }
        [Browsable(false), XmlIgnore] public Series<double> InDiscount     { get { Update(); return Values[19]; } }

        #region Properties
        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Period (pivot bars each side)", GroupName = "1. Structure", Order = 0, Description = "Larger = broader structure, slower.")]
        public int Period { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use response period", GroupName = "1. Structure", Order = 1, Description = "Off: the Period above is used for the SMS / BMS check as well.")]
        public bool UseResponse { get; set; }

        [NinjaScriptProperty, Range(1, 500)]
        [Display(Name = "Response period", GroupName = "1. Structure", Order = 2, Description = "A new high only counts as SMS / BMS when the previous high has been unchanged for this many bars.")]
        public int Response { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Structure display", GroupName = "1. Structure", Order = 3, Description = "Active range: top/bottom lines and zones for the current range. Previous range: the range history as lines and filled bands.")]
        public SneakyIctDisplay DisplayMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show bullish structure", GroupName = "2. Style", Order = 0)]
        public bool ShowBullish { get; set; }

        [XmlIgnore][Display(Name = "Bullish colour", GroupName = "2. Style", Order = 1)]
        public Brush BullBrush { get; set; }
        [Browsable(false)] public string BullBrushSerialize { get { return Serialize.BrushToString(BullBrush); } set { BullBrush = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Display(Name = "Show bearish structure", GroupName = "2. Style", Order = 2)]
        public bool ShowBearish { get; set; }

        [XmlIgnore][Display(Name = "Bearish colour", GroupName = "2. Style", Order = 3)]
        public Brush BearBrush { get; set; }
        [Browsable(false)] public string BearBrushSerialize { get { return Serialize.BrushToString(BearBrush); } set { BearBrush = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Display(Name = "Show premium / discount / mid zones", GroupName = "2. Style", Order = 4)]
        public bool ShowZones { get; set; }

        [XmlIgnore][Display(Name = "Premium zone colour", GroupName = "2. Style", Order = 5)]
        public Brush PremiumBrush { get; set; }
        [Browsable(false)] public string PremiumBrushSerialize { get { return Serialize.BrushToString(PremiumBrush); } set { PremiumBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Discount zone colour", GroupName = "2. Style", Order = 6)]
        public Brush DiscountBrush { get; set; }
        [Browsable(false)] public string DiscountBrushSerialize { get { return Serialize.BrushToString(DiscountBrush); } set { DiscountBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Mid zone colour", GroupName = "2. Style", Order = 7)]
        public Brush MidBrush { get; set; }
        [Browsable(false)] public string MidBrushSerialize { get { return Serialize.BrushToString(MidBrush); } set { MidBrush = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty, Range(0, 200)]
        [Display(Name = "Active range extends right (bars)", GroupName = "2. Style", Order = 8)]
        public int RightExtend { get; set; }

        [NinjaScriptProperty, Range(6, 30)]
        [Display(Name = "Label text size (pt)", GroupName = "2. Style", Order = 9)]
        public int LabelSize { get; set; }

        [NinjaScriptProperty, Range(0, 60)]
        [Display(Name = "Label distance from line (px)", GroupName = "2. Style", Order = 10)]
        public int LabelDistance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show table", GroupName = "3. Table", Order = 0)]
        public bool ShowTable { get; set; }

        [NinjaScriptProperty][Display(Name = "Column: Ticker",   GroupName = "3. Table", Order = 1)] public bool ColTicker   { get; set; }
        [NinjaScriptProperty][Display(Name = "Column: Price",    GroupName = "3. Table", Order = 2)] public bool ColPrice    { get; set; }
        [NinjaScriptProperty][Display(Name = "Column: Trend",    GroupName = "3. Table", Order = 3)] public bool ColTrend    { get; set; }
        [NinjaScriptProperty][Display(Name = "Column: Signal",   GroupName = "3. Table", Order = 4)] public bool ColSignal   { get; set; }
        [NinjaScriptProperty][Display(Name = "Column: Range",    GroupName = "3. Table", Order = 5)] public bool ColRange    { get; set; }
        [NinjaScriptProperty][Display(Name = "Column: Premium",  GroupName = "3. Table", Order = 6)] public bool ColPremium  { get; set; }
        [NinjaScriptProperty][Display(Name = "Column: Mid",      GroupName = "3. Table", Order = 7)] public bool ColMid      { get; set; }
        [NinjaScriptProperty][Display(Name = "Column: Discount", GroupName = "3. Table", Order = 8)] public bool ColDiscount { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Table position", GroupName = "3. Table", Order = 9)]
        public SneakyIctTablePos TablePosition { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Table size", GroupName = "3. Table", Order = 10)]
        public SneakyIctTableSize TableSize { get; set; }

        [XmlIgnore][Display(Name = "Header colour", GroupName = "3. Table", Order = 11)]
        public Brush HeaderBrush { get; set; }
        [Browsable(false)] public string HeaderBrushSerialize { get { return Serialize.BrushToString(HeaderBrush); } set { HeaderBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Bullish row colour", GroupName = "3. Table", Order = 12)]
        public Brush TableBullBrush { get; set; }
        [Browsable(false)] public string TableBullBrushSerialize { get { return Serialize.BrushToString(TableBullBrush); } set { TableBullBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Bearish row colour", GroupName = "3. Table", Order = 13)]
        public Brush TableBearBrush { get; set; }
        [Browsable(false)] public string TableBearBrushSerialize { get { return Serialize.BrushToString(TableBearBrush); } set { TableBearBrush = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Display(Name = "List the chart instrument in the table", GroupName = "4. Screener symbols", Order = 0)]
        public bool IncludeChart { get; set; }

        [NinjaScriptProperty][Display(Name = "Symbol 1 (blank = off)", GroupName = "4. Screener symbols", Order = 1, Description = "NinjaTrader instrument name, e.g. ES 12-26. Needs data for that instrument.")] public string Symbol1 { get; set; }
        [NinjaScriptProperty][Display(Name = "Symbol 2", GroupName = "4. Screener symbols", Order = 2)] public string Symbol2 { get; set; }
        [NinjaScriptProperty][Display(Name = "Symbol 3", GroupName = "4. Screener symbols", Order = 3)] public string Symbol3 { get; set; }
        [NinjaScriptProperty][Display(Name = "Symbol 4", GroupName = "4. Screener symbols", Order = 4)] public string Symbol4 { get; set; }
        [NinjaScriptProperty][Display(Name = "Symbol 5", GroupName = "4. Screener symbols", Order = 5)] public string Symbol5 { get; set; }
        [NinjaScriptProperty][Display(Name = "Symbol 6", GroupName = "4. Screener symbols", Order = 6)] public string Symbol6 { get; set; }
        [NinjaScriptProperty][Display(Name = "Symbol 7", GroupName = "4. Screener symbols", Order = 7)] public string Symbol7 { get; set; }
        [NinjaScriptProperty][Display(Name = "Symbol 8", GroupName = "4. Screener symbols", Order = 8)] public string Symbol8 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert on structure events", GroupName = "5. Alerts", Order = 0)]
        public bool PopupAlerts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert while price is in a zone (every bar)", GroupName = "5. Alerts", Order = 1)]
        public bool ZoneAlerts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert sound file", GroupName = "5. Alerts", Order = 2)]
        public string SoundFile { get; set; }
        #endregion
    }
}
