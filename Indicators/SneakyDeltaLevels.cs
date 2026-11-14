#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// Delta levels ("liquidity memory"): for every closed bar, find the single price level with the largest
// buy-minus-sell volume imbalance (real tick data: trades classified against bid/ask). Strong ones are
// kept as horizontal "ghost" levels until price closes through them or they age out. A bar that reaches a
// level and closes back on the right side stamps a touch signal and (optionally) logs it to CSV so it can
// be backtested. Needs live ticks or Tick Replay for history; bar-only data cannot produce delta.
namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakyDeltaLevels : Indicator
    {
        private class Lvl { public int Id, Bar; public double Price, Delta, Vol; public DateTime T; }

        private Dictionary<double, double> curBuy = new Dictionary<double, double>();
        private Dictionary<double, double> curSell = new Dictionary<double, double>();
        private List<Lvl> levels = new List<Lvl>();
        private double lastAsk, lastBid, lastPx;
        private int idCounter, lastDone = -1;
        private Series<double> tr, sig;
        private StreamWriter rec;

        [NinjaScriptProperty, Range(1, 100000)]
        [Display(Name = "Min level delta (contracts)", GroupName = "1. Levels", Order = 0)]
        public int MinDelta { get; set; }
        [NinjaScriptProperty, Range(1, 50)]
        [Display(Name = "Max levels kept", GroupName = "1. Levels", Order = 1)]
        public int MaxLevels { get; set; }
        [NinjaScriptProperty, Range(10, 5000)]
        [Display(Name = "Max level age (bars)", GroupName = "1. Levels", Order = 2)]
        public int MaxAge { get; set; }
        [NinjaScriptProperty, Range(0, 40)]
        [Display(Name = "Touch tolerance (ticks)", GroupName = "1. Levels", Order = 3)]
        public int TouchTicks { get; set; }
        [NinjaScriptProperty, Range(0, 40)]
        [Display(Name = "Break tolerance (ticks past level, on close)", GroupName = "1. Levels", Order = 4)]
        public int BreakTicks { get; set; }
        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "Extend lines right (bars)", GroupName = "2. Drawing", Order = 0)]
        public int ExtR { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Show labels", GroupName = "2. Drawing", Order = 1)]
        public bool ShowLabels { get; set; }
        [NinjaScriptProperty]
        [Display(Name = "Record touches to CSV", GroupName = "3. Recorder", Order = 0)]
        public bool Record { get; set; }

        [Browsable(false), System.Xml.Serialization.XmlIgnore]
        public Series<double> TouchSignal { get { Update(); return sig; } }   // +1 touch of buy-delta level (support), -1 sell-delta level (resistance)

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Sneaky Delta Levels";
                Description = "Price levels with the biggest per-bar buy/sell imbalance, kept as ghost levels, with touch signals.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                IsAutoScale = false;
                MinDelta = 150; MaxLevels = 8; MaxAge = 600; TouchTicks = 2; BreakTicks = 4;
                ExtR = 10; ShowLabels = true; Record = true;
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Touch Signal");           // 0  +1 / -1
                AddPlot(new Stroke(Brushes.DeepSkyBlue, 2), PlotStyle.TriangleUp, "Touch Long");       // 1
                AddPlot(new Stroke(Brushes.Magenta, 2), PlotStyle.TriangleDown, "Touch Short");        // 2
            }
            else if (State == State.DataLoaded)
            {
                tr = new Series<double>(this); sig = new Series<double>(this);
                if (Record)
                {
                    try
                    {
                        string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SneakyExport");
                        Directory.CreateDirectory(dir);
                        string name = "delta_touches_" + Instrument.FullName.Replace(" ", "_") + ".csv";
                        rec = new StreamWriter(new FileStream(Path.Combine(dir, name), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                        rec.AutoFlush = true;
                        if (rec.BaseStream.Length == 0) rec.WriteLine("touch_time,touch_bar,side,level,delta,level_volume,level_time,age_bars,close,atr");
                    }
                    catch { rec = null; }
                }
            }
            else if (State == State.Terminated)
            {
                try { if (rec != null) { rec.Dispose(); rec = null; } } catch { }
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType == MarketDataType.Ask) { lastAsk = e.Price; return; }
            if (e.MarketDataType == MarketDataType.Bid) { lastBid = e.Price; return; }
            if (e.MarketDataType != MarketDataType.Last) return;
            double px = e.Price; bool buy;
            if (lastAsk > 0 && px >= lastAsk) buy = true;
            else if (lastBid > 0 && px <= lastBid) buy = false;
            else buy = px >= lastPx;                      // tick rule fallback inside the spread
            lastPx = px;
            Dictionary<double, double> d = buy ? curBuy : curSell;
            double cur; d.TryGetValue(px, out cur); d[px] = cur + e.Volume;
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1) return;
            if (CurrentBar > 0)
                tr[0] = Math.Max(High[0] - Low[0], Math.Max(Math.Abs(High[0] - Close[1]), Math.Abs(Low[0] - Close[1])));
            if (!IsFirstTickOfBar || lastDone == CurrentBar) return;
            lastDone = CurrentBar;
            int off = 1;                                   // the bar that just closed

            // 1) retire levels the closed bar broke through, and old ones
            double tk = TickSize;
            for (int i = levels.Count - 1; i >= 0; i--)
            {
                Lvl L = levels[i];
                bool broken = L.Delta > 0 ? Close[off] < L.Price - BreakTicks * tk : Close[off] > L.Price + BreakTicks * tk;
                if (broken || CurrentBar - L.Bar > MaxAge) { RemoveDrawObject("DLL" + L.Id); RemoveDrawObject("DLT" + L.Id); levels.RemoveAt(i); }
            }

            // 2) touches of existing levels by the closed bar
            sig[off] = 0; Values[0][off] = 0;
            double atr = 0; int n = Math.Min(20, CurrentBar);
            for (int i = 1; i <= n; i++) atr += tr[i]; if (n > 0) atr /= n;
            foreach (Lvl L in levels)
            {
                if (L.Bar >= CurrentBar - off) continue;            // not a level created by this very bar
                double tol = TouchTicks * tk; int side = 0;
                if (L.Delta > 0 && Low[off] <= L.Price + tol && Close[off] > L.Price) side = 1;
                else if (L.Delta < 0 && High[off] >= L.Price - tol && Close[off] < L.Price) side = -1;
                if (side == 0) continue;
                sig[off] = side; Values[0][off] = side;
                if (side == 1) Values[1][off] = Low[off] - 2 * tk; else Values[2][off] = High[off] + 2 * tk;
                if (rec != null)
                {
                    try
                    {
                        rec.WriteLine(string.Join(",", new string[] {
                            Time[off].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), (CurrentBar - off).ToString(), side.ToString(),
                            L.Price.ToString("0.00", CultureInfo.InvariantCulture), L.Delta.ToString("0", CultureInfo.InvariantCulture),
                            L.Vol.ToString("0", CultureInfo.InvariantCulture), L.T.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                            (CurrentBar - off - L.Bar).ToString(), Close[off].ToString("0.00", CultureInfo.InvariantCulture), atr.ToString("0.00", CultureInfo.InvariantCulture) }));
                    }
                    catch { }
                }
                break;                                              // one touch per bar
            }

            // 3) new level from the closed bar: price with the largest |buy - sell|
            double bestP = 0, bestD = 0, bestV = 0;
            HashSet<double> prices = new HashSet<double>(curBuy.Keys);
            foreach (double p in curSell.Keys) prices.Add(p);
            foreach (double p in prices)
            {
                double b, s; curBuy.TryGetValue(p, out b); curSell.TryGetValue(p, out s);
                double dl = b - s;
                if (Math.Abs(dl) > Math.Abs(bestD)) { bestD = dl; bestP = p; bestV = b + s; }
            }
            curBuy.Clear(); curSell.Clear();
            if (Math.Abs(bestD) >= MinDelta)
            {
                Lvl nl = new Lvl(); nl.Id = idCounter++; nl.Bar = CurrentBar - off; nl.Price = bestP; nl.Delta = bestD; nl.Vol = bestV; nl.T = Time[off];
                levels.Add(nl);
                if (levels.Count > MaxLevels)
                {
                    int w = 0; for (int i = 1; i < levels.Count; i++) if (Math.Abs(levels[i].Delta) < Math.Abs(levels[w].Delta)) w = i;
                    RemoveDrawObject("DLL" + levels[w].Id); RemoveDrawObject("DLT" + levels[w].Id); levels.RemoveAt(w);
                }
            }

            // 4) redraw ghost lines
            foreach (Lvl L in levels)
            {
                Brush col = L.Delta > 0 ? Brushes.DeepSkyBlue : Brushes.Magenta;
                Draw.Line(this, "DLL" + L.Id, false, CurrentBar - L.Bar, L.Price, -ExtR, L.Price, col, DashStyleHelper.Dash, 1);
                if (ShowLabels)
                    Draw.Text(this, "DLT" + L.Id, false, (L.Delta > 0 ? "+" : "") + L.Delta.ToString("0"), -ExtR, L.Price, 0, col,
                        new SimpleFont("Arial", 10), System.Windows.TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
            }
        }
    }
}
