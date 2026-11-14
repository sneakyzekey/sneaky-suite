#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
#endregion

// Squeeze counter: Bollinger Bands inside Keltner Channels = squeeze ON. Counts how many bars it lasts,
// plots TTM-style momentum, and stamps a +1/-1 "fire" signal on the first bar after the squeeze releases.
// NOTE: on my test of 193 fires on 5-minute NQ (Sep 29 - Oct 30 2026) this had NO measurable edge.
// It is a context/analysis tool, not a standalone entry signal. See README.
namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakySqueezeCounter : Indicator
    {
        private Series<double> tr, mom, cnt, fire, fireLen;
        private int run;

        [NinjaScriptProperty, Range(5, 200)]
        [Display(Name = "Length", GroupName = "Parameters", Order = 0)]
        public int Len { get; set; }
        [NinjaScriptProperty, Range(0.5, 5)]
        [Display(Name = "Bollinger std-dev multiplier", GroupName = "Parameters", Order = 1)]
        public double BbMult { get; set; }
        [NinjaScriptProperty, Range(0.5, 5)]
        [Display(Name = "Keltner ATR multiplier", GroupName = "Parameters", Order = 2)]
        public double KcMult { get; set; }
        [NinjaScriptProperty, Range(1, 100)]
        [Display(Name = "Minimum squeeze bars for a signal", GroupName = "Parameters", Order = 3)]
        public int MinBars { get; set; }

        // for strategies / signal panels
        [Browsable(false), System.Xml.Serialization.XmlIgnore]
        public Series<double> FireSignal { get { Update(); return fire; } }      // +1 fire up, -1 fire down, on the first bar after release
        [Browsable(false), System.Xml.Serialization.XmlIgnore]
        public Series<double> SqueezeBars { get { Update(); return fireLen; } }   // length of the squeeze that just fired
        [Browsable(false), System.Xml.Serialization.XmlIgnore]
        public Series<double> Momentum { get { Update(); return mom; } }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Sneaky Squeeze Counter";
                Description = "Bollinger-in-Keltner squeeze with a bar counter, momentum histogram and fire signal.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = false;
                Len = 20; BbMult = 2.0; KcMult = 1.5; MinBars = 1;
                AddPlot(new Stroke(Brushes.DodgerBlue, 3), PlotStyle.Bar, "Momentum");   // 0
                AddPlot(new Stroke(Brushes.Gray, 3), PlotStyle.Dot, "Squeeze");          // 1  dot on zero line: red = on, green = off
                AddPlot(new Stroke(Brushes.Orange, 2), PlotStyle.Line, "Squeeze Bars");  // 2  running count
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Fire Signal"); // 3 +1 / -1 for signal panels
            }
            else if (State == State.DataLoaded)
            {
                tr = new Series<double>(this); mom = new Series<double>(this); cnt = new Series<double>(this);
                fire = new Series<double>(this); fireLen = new Series<double>(this);
                run = 0;
            }
        }

        protected override void OnBarUpdate()
        {
            tr[0] = CurrentBar == 0 ? High[0] - Low[0]
                  : Math.Max(High[0] - Low[0], Math.Max(Math.Abs(High[0] - Close[1]), Math.Abs(Low[0] - Close[1])));
            fire[0] = 0; fireLen[0] = 0;
            Values[3][0] = 0;
            if (CurrentBar < Len) { mom[0] = 0; cnt[0] = 0; return; }

            double sum = 0, trs = 0, hh = double.MinValue, ll = double.MaxValue;
            for (int i = 0; i < Len; i++) { sum += Close[i]; trs += tr[i]; if (High[i] > hh) hh = High[i]; if (Low[i] < ll) ll = Low[i]; }
            double m = sum / Len, atr = trs / Len, v = 0;
            for (int i = 0; i < Len; i++) v += (Close[i] - m) * (Close[i] - m);
            double bb = BbMult * Math.Sqrt(v / Len), kc = KcMult * atr;
            bool on = bb < kc;

            // momentum = linear-regression endpoint of (close - mean(donchian mid, SMA)) over Len bars
            double bas = ((hh + ll) / 2.0 + m) / 2.0;
            double n = Len, xm = (n - 1) / 2.0, ym = 0;
            double[] y = new double[Len];
            for (int i = 0; i < Len; i++) { y[i] = Close[Len - 1 - i] - bas; ym += y[i]; }
            ym /= n;
            double sxx = 0, sxy = 0;
            for (int i = 0; i < Len; i++) { sxx += (i - xm) * (i - xm); sxy += (i - xm) * (y[i] - ym); }
            double slope = sxy / sxx;
            double mo = ym + slope * ((n - 1) - xm);
            mom[0] = mo;

            Values[0][0] = mo;
            PlotBrushes[0][0] = mo >= 0 ? (mo >= mom[1] ? Brushes.Cyan : Brushes.DodgerBlue) : (mo <= mom[1] ? Brushes.Red : Brushes.Maroon);
            Values[1][0] = 0;
            PlotBrushes[1][0] = on ? Brushes.Red : Brushes.LimeGreen;

            if (on) run++;
            else
            {
                if (run >= MinBars && run > 0 && mo != 0)
                {
                    fire[0] = mo > 0 ? 1 : -1; fireLen[0] = run;
                    Values[3][0] = fire[0];
                }
                run = 0;
            }
            cnt[0] = run;
            Values[2][0] = run;
        }
    }
}
