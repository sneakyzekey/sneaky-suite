#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
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

// SneakyWickFill  --  marks unfilled candle WICKS from four timeframes (5 min, 1 hour, 4 hour, daily) as zones on any chart.
//
//  A wick zone is the part of a finished candle between its body and its high (upper wick) or its low (lower wick).
//  "Wick fill" = price later trading back into that wick. A zone is dropped once price has filled it (how deep counts as
//  filled is a setting), when it gets older than the look-back, or when a newer zone replaces it (see Show).
//
//  Show:
//    NearestAboveAndBelow  per timeframe, only the closest unfilled upper wick above price and lower wick below price  (default)
//    LatestOnly            per timeframe, only the most recent unfilled upper wick and lower wick
//    AllUnfilled           every unfilled wick inside the look-back (up to "Max zones per timeframe")
//
//  Works on any chart type. Timeframes at or above the chart's own are the useful ones; the chart needs enough days loaded for
//  the higher timeframes (e.g. 20+ days for daily wicks). Zones update when a chart bar closes.
//  This marks where wicks are - it is a drawing aid, not a tested trading signal.

public enum SneakyWickShow { NearestAboveAndBelow, LatestOnly, AllUnfilled }

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakyWickFill : Indicator
    {
        private sealed class Zone
        {
            public int Tf;            // 1..4
            public bool Upper;        // true = upper wick (above the body), false = lower wick
            public double Lo, Hi;     // zone prices
            public DateTime Time;     // close time of the candle that made it
            public int Bar;           // that candle's index on its own timeframe
            public bool Filled;
            public string Tag;
        }

        private readonly List<Zone> zones = new List<Zone>();
        private readonly HashSet<string> drawn = new HashSet<string>();
        private static readonly string[] TfName = { "", "5m", "1H", "4H", "D" };
        private int seq;
        private SimpleFont labelFont;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "SneakyWickFill";
                Description = "Marks unfilled candle wicks from the 5 min, 1 hour, 4 hour and daily timeframes as zones.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                DrawOnPricePanel = true;
                IsSuspendedWhileInactive = true;

                ShowMode = SneakyWickShow.NearestAboveAndBelow;
                Show5 = true; Show60 = true; Show240 = true; ShowDay = true;
                MinTicks5 = 8; MinTicks60 = 20; MinTicks240 = 40; MinTicksDay = 80;
                Lookback5 = 60; Lookback60 = 48; Lookback240 = 30; LookbackDay = 20;
                MinWickPctOfRange = 0;
                FillPercent = 100;
                MaxZonesPerTf = 6;
                ExtendBars = 12;
                FillOpacity = 8;
                LabelSize = 10;
                MaxWickMultiple = 0;
                ShowLabels = true;
                Color5 = Brushes.Gray; Color60 = Brushes.DodgerBlue; Color240 = Brushes.Orange; ColorDay = Brushes.MediumOrchid;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 5);      // 1
                AddDataSeries(BarsPeriodType.Minute, 60);     // 2
                AddDataSeries(BarsPeriodType.Minute, 240);    // 3
                AddDataSeries(BarsPeriodType.Day, 1);         // 4
            }
        }

        private bool TfOn(int tf) { return tf == 1 ? Show5 : tf == 2 ? Show60 : tf == 3 ? Show240 : ShowDay; }
        private int TfMinTicks(int tf) { return tf == 1 ? MinTicks5 : tf == 2 ? MinTicks60 : tf == 3 ? MinTicks240 : MinTicksDay; }
        private int TfLookback(int tf) { return tf == 1 ? Lookback5 : tf == 2 ? Lookback60 : tf == 3 ? Lookback240 : LookbackDay; }
        private Brush TfBrush(int tf) { return tf == 1 ? Color5 : tf == 2 ? Color60 : tf == 3 ? Color240 : ColorDay; }

        protected override void OnBarUpdate()
        {
            try
            {
                int bip = BarsInProgress;
                if (bip >= 1 && bip <= 4) { NewCandle(bip); return; }
                if (bip != 0 || CurrentBar < 1) return;

                // fills are judged on the chart's own bars, but only by bars that closed after the wick candle did
                double hi = High[0], lo = Low[0];
                DateTime now = Time[0];
                double part = Math.Max(1, Math.Min(100, FillPercent)) / 100.0;
                for (int i = zones.Count - 1; i >= 0; i--)
                {
                    Zone z = zones[i];
                    if (now <= z.Time) continue;
                    if (z.Upper ? hi >= z.Lo + part * (z.Hi - z.Lo) : lo <= z.Hi - part * (z.Hi - z.Lo)) z.Filled = true;
                    bool old = CurrentBars[z.Tf] - z.Bar > TfLookback(z.Tf);
                    if (z.Filled || old) zones.RemoveAt(i);
                }

                // drawing every historical bar would be slow and pointless: start with the last loaded bars
                if (State == State.Historical && CurrentBar < Count - 3) return;
                Redraw(Close[0]);
            }
            catch (Exception ex) { Print("SneakyWickFill: " + ex.Message); }
        }

        // a candle just closed on timeframe 'tf' (the series values below belong to that timeframe)
        private void NewCandle(int tf)
        {
            if (!TfOn(tf) || CurrentBar < 0) return;
            double o = Open[0], h = High[0], l = Low[0], c = Close[0];
            double top = Math.Max(o, c), bot = Math.Min(o, c), range = h - l;
            if (range <= 0) return;
            double min = TfMinTicks(tf) * TickSize, pct = MinWickPctOfRange / 100.0;
            double max = MaxWickMultiple > 0 ? min * MaxWickMultiple : double.MaxValue;     // optional: skip very tall wicks (they cover the chart)
            if (h - top >= min && h - top <= max && (h - top) >= pct * range) AddZone(tf, true, top, h);
            if (bot - l >= min && bot - l <= max && (bot - l) >= pct * range) AddZone(tf, false, l, bot);
        }

        private void AddZone(int tf, bool upper, double lo, double hi)
        {
            Zone z = new Zone();
            z.Tf = tf; z.Upper = upper; z.Lo = lo; z.Hi = hi; z.Time = Time[0]; z.Bar = CurrentBar;
            z.Tag = "SWF_" + tf + (upper ? "U" : "L") + "_" + (++seq);
            zones.Add(z);
            // keep the list small: drop the oldest of this timeframe beyond a generous cap
            int n = 0;
            for (int i = zones.Count - 1; i >= 0; i--)
                if (zones[i].Tf == tf && ++n > 400) zones.RemoveAt(i);
        }

        private void Redraw(double price)
        {
            List<Zone> pick = new List<Zone>();
            for (int tf = 1; tf <= 4; tf++)
            {
                if (!TfOn(tf)) continue;
                if (ShowMode == SneakyWickShow.AllUnfilled)
                {
                    int n = 0;
                    for (int i = zones.Count - 1; i >= 0 && n < MaxZonesPerTf; i--)
                        if (zones[i].Tf == tf) { pick.Add(zones[i]); n++; }
                    continue;
                }
                Zone up = null, dn = null;
                for (int i = 0; i < zones.Count; i++)
                {
                    Zone z = zones[i];
                    if (z.Tf != tf) continue;
                    if (ShowMode == SneakyWickShow.LatestOnly)
                    {
                        if (z.Upper) { if (up == null || z.Time >= up.Time) up = z; }
                        else { if (dn == null || z.Time >= dn.Time) dn = z; }
                    }
                    else
                    {
                        // nearest upper wick whose tip is still above price / nearest lower wick whose tip is still below price
                        if (z.Upper) { if (z.Hi > price && (up == null || z.Lo < up.Lo)) up = z; }
                        else { if (z.Lo < price && (dn == null || z.Hi > dn.Hi)) dn = z; }
                    }
                }
                if (up != null) pick.Add(up);
                if (dn != null) pick.Add(dn);
            }

            HashSet<string> keep = new HashSet<string>();
            for (int i = 0; i < pick.Count; i++)
            {
                Zone z = pick[i];
                int startAgo = 0;
                try { startAgo = CurrentBar - Bars.GetBar(z.Time); } catch { startAgo = 0; }
                startAgo = Math.Max(0, Math.Min(CurrentBar, startAgo));
                Brush b = TfBrush(z.Tf);
                Draw.Rectangle(this, z.Tag, false, startAgo, z.Hi, -Math.Max(1, ExtendBars), z.Lo, b, b, Math.Max(0, Math.Min(100, FillOpacity)));
                keep.Add(z.Tag);
                if (ShowLabels)
                {
                    string txt = TfName[z.Tf] + (z.Upper ? " \u25B2 " : " \u25BC ")
                               + Instrument.MasterInstrument.FormatPrice(z.Lo) + " - " + Instrument.MasterInstrument.FormatPrice(z.Hi);
                    // plain small text with NO background box (the short Draw.Text overload uses the chart's default text
                    // template, which can be a large font on a filled box). Upper wicks are labelled just inside their top edge,
                    // lower wicks just inside their bottom edge, so nested zones don't stack their labels on one spot.
                    if (labelFont == null) labelFont = new SimpleFont("Arial", Math.Max(6, LabelSize));
                    Draw.Text(this, z.Tag + "_t", false, txt, -Math.Max(1, ExtendBars / 2), z.Upper ? z.Hi : z.Lo, z.Upper ? -(LabelSize + 2) : (LabelSize + 2),
                              b, labelFont, System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                    keep.Add(z.Tag + "_t");
                }
            }
            foreach (string tag in drawn)
                if (!keep.Contains(tag)) RemoveDrawObject(tag);
            drawn.Clear();
            foreach (string tag in keep) drawn.Add(tag);
        }

        #region Properties
        [Display(Name = "Show", Description = "NearestAboveAndBelow: per timeframe, the closest unfilled upper wick above price and lower wick below price. LatestOnly: the most recent unfilled upper and lower wick. AllUnfilled: every unfilled wick in the look-back.", GroupName = "1. Zones", Order = 0)]
        public SneakyWickShow ShowMode { get; set; }

        [Range(1, 100)]
        [Display(Name = "Filled when price covers this % of the wick", Description = "100 = price has to reach the tip of the wick. 50 = reaching the middle of the wick counts as filled. A filled zone is removed.", GroupName = "1. Zones", Order = 1)]
        public int FillPercent { get; set; }

        [Range(0, 100)]
        [Display(Name = "Wick must be at least this % of the candle's range", Description = "0 = off. Filters out small wicks on big candles.", GroupName = "1. Zones", Order = 2)]
        public int MinWickPctOfRange { get; set; }

        [Range(0, 1000)]
        [Display(Name = "Skip wicks taller than (x the min size, 0 = off)", Description = "Very tall wicks make zones that cover the whole chart. Example: min size 40 ticks and 10 here skips wicks taller than 400 ticks.", GroupName = "1. Zones", Order = 3)]
        public int MaxWickMultiple { get; set; }

        [Range(1, 50)]
        [Display(Name = "Max zones per timeframe (AllUnfilled)", GroupName = "1. Zones", Order = 4)]
        public int MaxZonesPerTf { get; set; }

        [Display(Name = "5 min wicks", GroupName = "2. 5 minute", Order = 0)]
        public bool Show5 { get; set; }
        [Range(1, 10000)]
        [Display(Name = "Min wick size (ticks)", GroupName = "2. 5 minute", Order = 1)]
        public int MinTicks5 { get; set; }
        [Range(1, 5000)]
        [Display(Name = "Look back (5 min candles)", GroupName = "2. 5 minute", Order = 2)]
        public int Lookback5 { get; set; }

        [Display(Name = "1 hour wicks", GroupName = "3. 1 hour", Order = 0)]
        public bool Show60 { get; set; }
        [Range(1, 10000)]
        [Display(Name = "Min wick size (ticks)", GroupName = "3. 1 hour", Order = 1)]
        public int MinTicks60 { get; set; }
        [Range(1, 5000)]
        [Display(Name = "Look back (1 hour candles)", GroupName = "3. 1 hour", Order = 2)]
        public int Lookback60 { get; set; }

        [Display(Name = "4 hour wicks", GroupName = "4. 4 hour", Order = 0)]
        public bool Show240 { get; set; }
        [Range(1, 10000)]
        [Display(Name = "Min wick size (ticks)", GroupName = "4. 4 hour", Order = 1)]
        public int MinTicks240 { get; set; }
        [Range(1, 5000)]
        [Display(Name = "Look back (4 hour candles)", GroupName = "4. 4 hour", Order = 2)]
        public int Lookback240 { get; set; }

        [Display(Name = "Daily wicks", GroupName = "5. Daily", Order = 0)]
        public bool ShowDay { get; set; }
        [Range(1, 10000)]
        [Display(Name = "Min wick size (ticks)", GroupName = "5. Daily", Order = 1)]
        public int MinTicksDay { get; set; }
        [Range(1, 5000)]
        [Display(Name = "Look back (daily candles)", GroupName = "5. Daily", Order = 2)]
        public int LookbackDay { get; set; }

        [Range(1, 500)]
        [Display(Name = "Extend zones right (bars)", GroupName = "6. Display", Order = 0)]
        public int ExtendBars { get; set; }
        [Range(0, 100)]
        [Display(Name = "Fill opacity (%)", Description = "0 = outline only.", GroupName = "6. Display", Order = 1)]
        public int FillOpacity { get; set; }
        [Display(Name = "Show labels", GroupName = "6. Display", Order = 2)]
        public bool ShowLabels { get; set; }
        [Range(6, 30)]
        [Display(Name = "Label size", GroupName = "6. Display", Order = 2)]
        public int LabelSize { get; set; }

        [XmlIgnore]
        [Display(Name = "5 min color", GroupName = "6. Display", Order = 3)]
        public Brush Color5 { get; set; }
        [Browsable(false)]
        public string Color5Serialize { get { return Serialize.BrushToString(Color5); } set { Color5 = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "1 hour color", GroupName = "6. Display", Order = 4)]
        public Brush Color60 { get; set; }
        [Browsable(false)]
        public string Color60Serialize { get { return Serialize.BrushToString(Color60); } set { Color60 = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "4 hour color", GroupName = "6. Display", Order = 5)]
        public Brush Color240 { get; set; }
        [Browsable(false)]
        public string Color240Serialize { get { return Serialize.BrushToString(Color240); } set { Color240 = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Daily color", GroupName = "6. Display", Order = 6)]
        public Brush ColorDay { get; set; }
        [Browsable(false)]
        public string ColorDaySerialize { get { return Serialize.BrushToString(ColorDay); } set { ColorDay = Serialize.StringToBrush(value); } }
        #endregion
    }
}
