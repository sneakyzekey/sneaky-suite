#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// SneakyDynamicSwingVwap
// NinjaTrader 8 port of "Dynamic Swing Anchored VWAP" by Zeiierman (TradingView, Pine v6).
// Original work (c) Zeiierman, licensed CC BY-NC-SA 4.0
//   https://creativecommons.org/licenses/by-nc-sa/4.0/
// This port is an adaptation and is therefore distributed under the same license:
// attribution to Zeiierman required, NON-COMMERCIAL use only, share-alike.
//
// What it does (same as the original)
//   * Swing points: a bar that makes the highest high (lowest low) of the last "Swing Period" bars is a new
//     high-event (low-event). Direction is +1 when the latest event was a high, -1 when it was a low.
//   * When the direction flips, the previous swing extreme in the new direction becomes the anchor
//     (pivot low when turning up, pivot high when turning down), a label (HH / LH / HL / LL, compared with the
//     previous swing) is placed on that pivot, and an exponentially weighted VWAP is recomputed from the pivot
//     bar to the current bar, then extended bar by bar.
//   * Adaptive Price Tracking = half-life (in bars) of that EWMA; optionally scaled by the ATR(50)/avg ratio.
//   * Uptrend VWAP is drawn in the "uptrend" colour (red by default) and downtrend VWAP in the "downtrend"
//     colour (lime by default). This matches how the TradingView script actually colours the lines, even
//     though its tooltips describe the opposite.
//   * Volume is the chart's volume. On instruments or charts with no volume the VWAP is undefined (blank).
//
// Notes
//   * Keep Calculate on "On bar close" (default). Labels and the first part of each VWAP segment are placed
//     on past bars when a flip is detected, same as the original.
//   * Automation plots (invisible, written every bar after warm-up; see README):
//       BuySignal  +1 on the bar the direction flips up,   SellSignal -1 on the bar it flips down
//                  (both are N/A / unset on every other bar, so signal scanners see them as sparse signals;
//                   code that reads them gets 0 on those bars)
//       Direction  +1 / -1 current direction
//       VwapPrice  value of the active VWAP on this bar (0 if none)
//       AnchorPrice price of the pivot the active VWAP is anchored to (0 if none)
//       HH / HL / LH / LL  one plot per swing label: +1 / +1 / -1 / -1 on the bar that label is created,
//                  N/A on every other bar. The label's chart object is tagged dsv_HH<bar>, dsv_HL<bar>, ... so
//                  scanners can pick each label type up separately too.
//     A flip is only confirmed when price makes a new Swing-Period extreme, so these are trend-change
//     signals that arrive late by design.

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakyDynamicSwingVwap : Indicator
    {
        private const int AtrLen = 50;

        private Series<double> hlc3S, trS, atrS, atrAvgS, aptS, dirS, phS, plS;

        private double ph, pl, prev, p, vol, anchorPrice;
        private int    phL, plL;

        // ------------------------------------------------------------------ lifecycle
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Swing-anchored VWAP with adaptive tracking (port of Zeiierman).";
                Name                     = "SneakyDynamicSwingVwap";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = true;
                IsAutoScale              = false;   // invisible automation plots must not stretch the price scale
                DisplayInDataBox         = true;
                DrawOnPricePanel         = true;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = true;
                MaximumBarsLookBack      = MaximumBarsLookBack.Infinite;

                SwingPeriod   = 50;
                BaseApt       = 20;
                UseAdapt      = false;
                VolBias       = 10.0;

                LowPivotBrush  = Rgb(0x00, 0xE6, 0x76);   // labels under swing lows (HL / LL)
                HighPivotBrush = Rgb(0xF2, 0x36, 0x45);   // labels over swing highs (HH / LH)
                VwapUpBrush    = Rgb(0x00, 0xE6, 0x76);   // uptrend VWAP (after a buy flip) = green
                VwapDownBrush  = Rgb(0xF2, 0x36, 0x45);   // downtrend VWAP (after a sell flip) = red
                LineWidth      = 2;

                ShowLabels     = true;
                LabelsOnSignalBar = false;
                LabelDistance  = 16;
                LabelSize      = 9;

                PopupAlerts    = false;
                SoundFile      = "Alert2.wav";

                AddPlot(new Stroke(Brushes.Lime, 2),        PlotStyle.Line, "VwapUp");         // 0: uptrend VWAP segments
                AddPlot(new Stroke(Brushes.Red, 2),         PlotStyle.Line, "VwapDown");       // 1: downtrend VWAP segments
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "BuySignal");      // 2: +1 on the flip-up bar, N/A (unset) on every other bar
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SellSignal");     // 3: -1 on the flip-down bar, N/A (unset) on every other bar
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Direction");      // 4: +1 / -1
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "VwapPrice");      // 5: active VWAP price, else 0
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "AnchorPrice");    // 6: pivot price of the active VWAP, else 0
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "HH");             // 7: +1 on the bar a "HH" label is created, else N/A
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "HL");             // 8: +1 on the bar a "HL" label is created, else N/A
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "LH");             // 9: -1 on the bar a "LH" label is created, else N/A
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "LL");             // 10: -1 on the bar a "LL" label is created, else N/A
            }
            else if (State == State.Configure)
            {
                Plots[0].Brush = VwapUpBrush;   Plots[0].Width = LineWidth;
                Plots[1].Brush = VwapDownBrush; Plots[1].Width = LineWidth;
            }
            else if (State == State.DataLoaded)
            {
                hlc3S = S(); trS = S(); atrS = S(); atrAvgS = S(); aptS = S(); dirS = S(); phS = S(); plS = S();
                ph = double.NaN; pl = double.NaN; phL = 0; plL = 0; prev = double.NaN;
                p = double.NaN; vol = double.NaN; anchorPrice = double.NaN;
            }
        }

        private Series<double> S() { return new Series<double>(this, MaximumBarsLookBack.Infinite); }

        private static Brush Rgb(byte r, byte g, byte b)
        {
            Brush br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        private static double AlphaFromApt(double apt)
        {
            double decay = Math.Exp(-Math.Log(2.0) / Math.Max(1.0, apt));
            return 1.0 - decay;
        }

        // ------------------------------------------------------------------ main
        protected override void OnBarUpdate()
        {
            int b = CurrentBar;
            for (int k = 2; k <= 3; k++) Values[k].Reset();   // signal plots stay unset (N/A) except on their signal bar
            for (int k = 7; k <= 10; k++) Values[k].Reset();
            for (int k = 4; k <= 6; k++) Values[k][0] = 0;

            hlc3S[0] = (High[0] + Low[0] + Close[0]) / 3.0;

            // ---- ATR(50) (Wilder, Pine ta.atr) and its RMA (Pine ta.rma of a series that starts as na)
            trS[0] = b == 0 ? High[0] - Low[0]
                            : Math.Max(High[0] - Low[0], Math.Max(Math.Abs(High[0] - Close[1]), Math.Abs(Low[0] - Close[1])));
            double atr = double.NaN;
            if (b == AtrLen - 1)
            {
                double s = 0; for (int i = 0; i < AtrLen; i++) s += trS[i];
                atr = s / AtrLen;
            }
            else if (b >= AtrLen) atr = (atrS[1] * (AtrLen - 1) + trS[0]) / AtrLen;
            atrS[0] = atr;

            double atrAvg = double.NaN;
            if (b == 2 * AtrLen - 2)
            {
                double s = 0; for (int i = 0; i < AtrLen; i++) s += atrS[i];
                atrAvg = s / AtrLen;
            }
            else if (b >= 2 * AtrLen - 1) atrAvg = (atrAvgS[1] * (AtrLen - 1) + atr) / AtrLen;
            atrAvgS[0] = atrAvg;

            double ratio = atrAvg > 0 ? atr / atrAvg : 1.0;
            double aptRaw = UseAdapt ? BaseApt / Math.Pow(ratio, VolBias) : BaseApt;
            double aptClamped = Math.Max(5.0, Math.Min(300.0, aptRaw));
            aptS[0] = Math.Round(aptClamped, MidpointRounding.AwayFromZero);

            // ---- swing events: current bar is the highest high / lowest low of the last SwingPeriod bars
            if (b >= SwingPeriod - 1)
            {
                bool isHigh = true, isLow = true;
                for (int i = 1; i < SwingPeriod; i++)
                {
                    if (High[i] > High[0]) isHigh = false;
                    if (Low[i]  < Low[0])  isLow  = false;
                }
                if (isHigh) { ph = High[0]; phL = b; }
                if (isLow)  { pl = Low[0];  plL = b; }
            }
            phS[0] = ph; plS[0] = pl;

            int dir = phL > plL ? 1 : -1;
            dirS[0] = dir;

            if (b == 0)
            {
                // Pine: var p = hlc3 * volume; var vol = volume; first bar goes through the "else" branch
                p = hlc3S[0] * Volume[0];
                vol = Volume[0];
                double v0 = vol > 0 ? p / vol : double.NaN;
                if (double.IsNaN(v0)) Values[1].Reset(); else Values[1][0] = v0;
                Values[0].Reset();
                Values[4][0] = dir;
                return;
            }

            int dirPrev = (int)dirS[1];
            bool flip = dir != dirPrev;

            Values[0].Reset(); Values[1].Reset();
            double vwapNow;

            if (flip)
            {
                int    x = dir > 0 ? plL : phL;
                double y = dir > 0 ? pl  : ph;

                string txt = "";
                if      (dir > 0 && pl < prev) txt = "LL";
                else if (dir > 0 && pl > prev) txt = "HL";
                else if (dir < 0 && ph < prev) txt = "LH";
                else if (dir < 0 && ph > prev) txt = "HH";

                if (txt == "HH") Values[7][0] = 1;
                if (txt == "HL") Values[8][0] = 1;
                if (txt == "LH") Values[9][0] = -1;
                if (txt == "LL") Values[10][0] = -1;

                if (ShowLabels && txt.Length > 0)
                {
                    Brush col = (txt == "HL" || txt == "LL") ? LowPivotBrush : HighPivotBrush;   // by pivot side: swing-low labels (HL/LL, buy side) green, swing-high labels (HH/LH, sell side) red
                    // default: label sits on the swing bar (same as TradingView, appears late); option: on the confirmation bar
                    int    lblAgo = LabelsOnSignalBar ? 0 : b - x;
                    double lblY   = LabelsOnSignalBar ? (dir > 0 ? Low[0] : High[0]) : y;
                    Draw.Text(this, "dsv_" + txt + b, false, txt, lblAgo, lblY, dir > 0 ? LabelDistance : -LabelDistance,
                        Brushes.White, new SimpleFont("Arial", LabelSize) { Bold = true },
                        System.Windows.TextAlignment.Center, col, col, 80);
                }

                prev = dir > 0 ? phS[1] : plS[1];
                anchorPrice = y;

                int barsBack = b - x;
                p = y * Volume[barsBack];
                vol = Volume[barsBack];

                // recompute the anchored VWAP from the pivot bar to now (oldest bar first)
                Series<double> target = dir < 0 ? Values[1] : Values[0];
                vwapNow = double.NaN;
                for (int i = barsBack; i >= 0; i--)
                {
                    double alpha = AlphaFromApt(aptS[i]);
                    double pxv = hlc3S[i] * Volume[i];
                    double vi  = Volume[i];
                    p   = (1.0 - alpha) * p   + alpha * pxv;
                    vol = (1.0 - alpha) * vol + alpha * vi;
                    double vp = vol > 0 ? p / vol : double.NaN;
                    target[i] = vp;                 // NaN = no point
                    if (i == 0) vwapNow = vp;
                }

                if (PopupAlerts && State == State.Realtime)
                {
                    string snd = NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + SoundFile;
                    Alert("dsv_" + b, Priority.High, Instrument.FullName + (dir > 0 ? " swing VWAP direction: UP" : " swing VWAP direction: DOWN"),
                        snd, 10, Brushes.Black, dir > 0 ? Brushes.LimeGreen : Brushes.OrangeRed);
                }
            }
            else
            {
                double alpha = AlphaFromApt(aptS[0]);
                double pxv = hlc3S[0] * Volume[0];
                p   = (1.0 - alpha) * p   + alpha * pxv;
                vol = (1.0 - alpha) * vol + alpha * Volume[0];
                vwapNow = vol > 0 ? p / vol : double.NaN;
                Series<double> target = dir > 0 ? Values[0] : Values[1];
                if (double.IsNaN(vwapNow)) target.Reset(); else target[0] = vwapNow;
            }

            // ---- automation plots
            if (flip && dir > 0) Values[2][0] = 1;
            if (flip && dir < 0) Values[3][0] = -1;
            Values[4][0] = dir;
            Values[5][0] = double.IsNaN(vwapNow) ? 0 : vwapNow;
            Values[6][0] = double.IsNaN(anchorPrice) ? 0 : anchorPrice;
        }

        // ------------------------------------------------------------------ outputs for strategies / automation tools
        [Browsable(false), XmlIgnore] public Series<double> VwapUp      { get { Update(); return Values[0]; } }
        [Browsable(false), XmlIgnore] public Series<double> VwapDown    { get { Update(); return Values[1]; } }
        [Browsable(false), XmlIgnore] public Series<double> BuySignal   { get { Update(); return Values[2]; } }
        [Browsable(false), XmlIgnore] public Series<double> SellSignal  { get { Update(); return Values[3]; } }
        [Browsable(false), XmlIgnore] public Series<double> Direction   { get { Update(); return Values[4]; } }
        [Browsable(false), XmlIgnore] public Series<double> VwapPrice   { get { Update(); return Values[5]; } }
        [Browsable(false), XmlIgnore] public Series<double> AnchorPrice { get { Update(); return Values[6]; } }
        [Browsable(false), XmlIgnore] public Series<double> HH          { get { Update(); return Values[7]; } }
        [Browsable(false), XmlIgnore] public Series<double> HL          { get { Update(); return Values[8]; } }
        [Browsable(false), XmlIgnore] public Series<double> LH          { get { Update(); return Values[9]; } }
        [Browsable(false), XmlIgnore] public Series<double> LL          { get { Update(); return Values[10]; } }

        #region Properties
        [NinjaScriptProperty, Range(2, 1000)]
        [Display(Name = "Swing period", GroupName = "1. Swing points", Order = 0, Description = "Bars used to detect swing highs and lows. Larger = bigger, slower swings.")]
        public int SwingPeriod { get; set; }

        [NinjaScriptProperty, Range(1.0, 300.0)]
        [Display(Name = "Adaptive price tracking", GroupName = "1. Swing points", Order = 1, Description = "Half-life (bars) of the VWAP's exponential weighting. Lower = tighter to price.")]
        public double BaseApt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Adapt APT by ATR ratio", GroupName = "1. Swing points", Order = 2, Description = "Shorten the tracking period in high volatility, lengthen it in low volatility.")]
        public bool UseAdapt { get; set; }

        [NinjaScriptProperty, Range(0.1, 100.0)]
        [Display(Name = "Volatility bias", GroupName = "1. Swing points", Order = 3, Description = "How strongly volatility changes the tracking speed when adaptation is on.")]
        public double VolBias { get; set; }

        [XmlIgnore][Display(Name = "Label under swing lows (HL / LL)", GroupName = "2. Style", Order = 0)]
        public Brush LowPivotBrush { get; set; }
        [Browsable(false)] public string LowPivotBrushSerialize { get { return Serialize.BrushToString(LowPivotBrush); } set { LowPivotBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "Label over swing highs (HH / LH)", GroupName = "2. Style", Order = 1)]
        public Brush HighPivotBrush { get; set; }
        [Browsable(false)] public string HighPivotBrushSerialize { get { return Serialize.BrushToString(HighPivotBrush); } set { HighPivotBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "VWAP line, uptrend", GroupName = "2. Style", Order = 2)]
        public Brush VwapUpBrush { get; set; }
        [Browsable(false)] public string VwapUpBrushSerialize { get { return Serialize.BrushToString(VwapUpBrush); } set { VwapUpBrush = Serialize.StringToBrush(value); } }

        [XmlIgnore][Display(Name = "VWAP line, downtrend", GroupName = "2. Style", Order = 3)]
        public Brush VwapDownBrush { get; set; }
        [Browsable(false)] public string VwapDownBrushSerialize { get { return Serialize.BrushToString(VwapDownBrush); } set { VwapDownBrush = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty, Range(1, 10)]
        [Display(Name = "Line width", GroupName = "2. Style", Order = 4)]
        public int LineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show swing labels", GroupName = "3. Labels", Order = 0)]
        public bool ShowLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw labels on confirmation bar (no repaint)", GroupName = "3. Labels", Order = 5, Description = "Off: label sits on the swing bar, which can be up to Swing period bars back (as in the original). On: label is drawn on the bar where the flip is confirmed, so it never appears in the past.")]
        public bool LabelsOnSignalBar { get; set; }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Label distance from pivot (px)", GroupName = "3. Labels", Order = 1)]
        public int LabelDistance { get; set; }

        [NinjaScriptProperty, Range(6, 30)]
        [Display(Name = "Label text size (pt)", GroupName = "3. Labels", Order = 2)]
        public int LabelSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert on direction flips", GroupName = "4. Alerts", Order = 0)]
        public bool PopupAlerts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert sound file", GroupName = "4. Alerts", Order = 1)]
        public string SoundFile { get; set; }
        #endregion
    }
}
