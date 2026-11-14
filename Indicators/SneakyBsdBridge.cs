#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.Gui;
#endregion

// SneakyBsdBridge
// Reads the PUBLIC plot values of other indicators already on the same chart (e.g. the BSD indicators, whose plots hold
// PRICES or n/a) and republishes them as standard signals for automation tools:
//     Bridge Long = +1 / 0     Bridge Short = -1 / 0     Bridge Signal = +1 long, -1 short, 0 none
// A source plot counts as "signal" when it has a valid, non-zero value on the bar. It does not modify or inspect any
// protected code. Put this indicator BELOW (added after) the source indicators so they have calculated first.
namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakyBsdBridge : Indicator
    {
        [Display(Name = "Source A: indicator name contains", GroupName = "1 Source A", Order = 0, Description = "Text found in the source indicator's name on the chart, e.g. 'Zone Entry Signals'.")]
        public string SourceA { get; set; }
        [Range(-1, 50)]
        [Display(Name = "A: LONG plot index (-1 = off)", GroupName = "1 Source A", Order = 1, Description = "Index = position in the Data Box list, starting at 0. Zone Entry Signals: TradeSignalLong = 5.")]
        public int ALong { get; set; }
        [Range(-1, 50)]
        [Display(Name = "A: SHORT plot index (-1 = off)", GroupName = "1 Source A", Order = 2, Description = "Zone Entry Signals: TradeSignalShort = 4.")]
        public int AShort { get; set; }

        [Display(Name = "Source B: indicator name contains", GroupName = "2 Source B", Order = 0, Description = "Optional second source, e.g. 'OrderFlow Zones'. Leave empty to disable.")]
        public string SourceB { get; set; }
        [Range(-1, 50)]
        [Display(Name = "B: LONG plot index (-1 = off)", GroupName = "2 Source B", Order = 1, Description = "OrderFlow Zones: LongEntry = 2.")]
        public int BLong { get; set; }
        [Range(-1, 50)]
        [Display(Name = "B: SHORT plot index (-1 = off)", GroupName = "2 Source B", Order = 2, Description = "OrderFlow Zones: ShortEntry = 3.")]
        public int BShort { get; set; }

        [Range(0, 10)]
        [Display(Name = "Late-signal bars (0 = same bar only)", GroupName = "3 Timing", Order = 0, Description = "Some indicators fill a signal plot a bar or two AFTER the bar it belongs to. If >0, a signal found on any of the last N bars is reported ONCE on the current bar.")]
        public int LateBars { get; set; }
        [Display(Name = "Print debug to NinjaScript Output", GroupName = "3 Timing", Order = 1, Description = "Prints, for the first 40 bars where Source A has any plot value, every plot index and value.")]
        public bool Debug { get; set; }

        private System.Collections.Generic.HashSet<long> seen = new System.Collections.Generic.HashSet<long>();
        private int dbgCount;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Sneaky BSD Bridge";
                Description = "Republishes other indicators' plot signals as standard +1 / -1 plots for automation tools.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true; IsAutoScale = false; DisplayInDataBox = true; PaintPriceMarkers = false; IsSuspendedWhileInactive = true;
                SourceA = "Zone Entry Signals"; ALong = 5; AShort = 4;
                SourceB = ""; BLong = 2; BShort = 3; LateBars = 2; Debug = true;
                AddPlot(new Stroke(System.Windows.Media.Brushes.Gray, 1), PlotStyle.Line, "Bridge Signal");
                AddPlot(new Stroke(System.Windows.Media.Brushes.Lime, 1), PlotStyle.Line, "Bridge Long");
                AddPlot(new Stroke(System.Windows.Media.Brushes.Red, 1), PlotStyle.Line, "Bridge Short");
                AddPlot(new Stroke(System.Windows.Media.Brushes.DodgerBlue, 1), PlotStyle.Line, "Bridge Found");
            }
        }

        // true if the plot has a value on the bar 'ago' bars back AND that (source,idx,bar) has not been reported before
        private bool Fired(IndicatorBase src, int srcId, int idx, int maxAgo)
        {
            if (src == null || idx < 0 || src.Values == null || idx >= src.Values.Length) return false;
            for (int k = 0; k <= maxAgo && k <= CurrentBar; k++)
            {
                try
                {
                    if (!src.Values[idx].IsValidDataPoint(k)) continue;
                    double v = src.Values[idx][k];
                    if (double.IsNaN(v) || v == 0) continue;
                    long key = ((long)srcId * 1000 + idx) * 10000000L + (CurrentBar - k);
                    if (seen.Add(key)) return true;
                }
                catch { }
            }
            return false;
        }

        private void DebugDump(IndicatorBase src)
        {
            if (!Debug || src == null || dbgCount >= 40 || src.Values == null) return;
            string line = ""; bool any = false;
            for (int i = 0; i < src.Values.Length; i++)
            {
                try
                {
                    bool ok = src.Values[i].IsValidDataPoint(0);
                    double v = ok ? src.Values[i][0] : double.NaN;
                    if (ok && !double.IsNaN(v) && v != 0) any = true;
                    line += i + ":" + (ok ? v.ToString("0.##") : "n/a") + " ";
                }
                catch { line += i + ":err "; }
            }
            if (any) { dbgCount++; Print("BsdBridge " + Time[0] + " plots(" + src.Values.Length + ") " + line); }
        }

        private static string Norm(string t) { return (t ?? "").Replace(" ", "").Replace("_", "").ToLowerInvariant(); }

        private bool listed;
        private IndicatorBase Find(string text)
        {
            if (string.IsNullOrEmpty(text) || ChartControl == null || ChartControl.Indicators == null) return null;
            string key = Norm(text);
            if (!listed)
            {
                listed = true;
                try
                {
                    for (int i = 0; i < ChartControl.Indicators.Count; i++)
                    {
                        IndicatorBase q = ChartControl.Indicators[i];
                        if (q != null) Print("BsdBridge sees indicator: Name='" + q.Name + "' Display='" + q.DisplayName + "' Type=" + q.GetType().Name + " plots=" + (q.Values == null ? 0 : q.Values.Length));
                    }
                }
                catch { }
            }
            for (int i = 0; i < ChartControl.Indicators.Count; i++)
            {
                IndicatorBase ind = ChartControl.Indicators[i];
                if (ind == null || ReferenceEquals(ind, this)) continue;
                if (Norm(ind.Name).Contains(key) || Norm(ind.DisplayName).Contains(key) || Norm(ind.GetType().Name).Contains(key)) return ind;
            }
            return null;
        }

        protected override void OnBarUpdate()
        {
            bool lg = false, sh = false;
            IndicatorBase a = Find(SourceA);
            if (a != null) { DebugDump(a); lg |= Fired(a, 1, ALong, LateBars); sh |= Fired(a, 1, AShort, LateBars); }
            IndicatorBase b = Find(SourceB);
            if (b != null) { lg |= Fired(b, 2, BLong, LateBars); sh |= Fired(b, 2, BShort, LateBars); }
            Values[3][0] = (a != null ? 1 : 0) + (b != null ? 2 : 0);   // diagnostic: 1 = source A found, 2 = source B found
            Values[1][0] = lg ? 1 : 0;
            Values[2][0] = sh ? -1 : 0;
            Values[0][0] = (lg && sh) ? 0 : (lg ? 1 : (sh ? -1 : 0));
        }
    }
}
