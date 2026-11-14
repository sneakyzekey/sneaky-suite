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

// SneakyKineticPanel
// An original on-chart "cockpit" panel. Every number on it comes from an open, documented formula (see README):
//   M1..H1 tiles : (close - EMA) / (StrengthScale * ATR) on each timeframe, as a signed percent (capped at +-100)
//   POWER        : ADX of the chart series (0..100)
//   ORDERFLOW    : buy vs sell volume over a rolling window, classified from live ticks (bid/ask rule, tick rule fallback)
//   VOL          : average volume of the last 3 completed bars vs the last 30
//   FAST MOM     : (close - close[3]) / ATR, in ATR multiples
//   RSI          : RSI of the chart series
//   VIX / DXY    : optional context readouts (need the instruments in your data feed)
//   BIAS SCORE   : weighted blend of the timeframe tiles, order flow and RSI, -100..+100.
//                  It is a SCORE, not a probability. Test it before trusting it (the CSV recorder is for that).
namespace NinjaTrader.NinjaScript.Indicators
{
    public enum KineticCorner { TopRight, TopLeft, BottomRight, BottomLeft }

    public class SneakyKineticPanel : Indicator
    {
        private const int NTF = 6;
        private static readonly string[] TfName = { "M1", "M3", "M5", "M15", "M30", "H1" };
        private static readonly int[] TfMin = { 1, 3, 5, 15, 30, 60 };
        private static readonly double[] TfW = { 1.0, 1.0, 1.5, 2.0, 2.0, 2.5 };

        private readonly object sync = new object();

        // indicators
        private EMA[] emaTF = new EMA[NTF];
        private ATR[] atrTF = new ATR[NTF];
        private ADX adxP;
        private RSI rsiP;
        private ATR atrP;
        private SMA volFast, volSlow;

        // live state (guarded by sync)
        private double[] strength = new double[NTF];
        private bool[] strengthOk = new bool[NTF];
        private double power, rsiVal, fastMom, volRatio, vixVal, dxyVal, dxyChg;
        private bool powerOk, rsiOk, fastOk, volOk, vixOk, dxyOk;
        private double bias, mtfScore, flowScore, rsiScore;
        private int signal, rawSignal, rawLast, pulseBar = -1, pulseDir; private bool armed = true;
        private double flowBuyPct; private bool flowOk;

        // Radar link: SneakyGexRadar on the same instrument writes RadarStatus_<root>.txt
        private DateTime lastRadarRead = DateTime.MinValue;
        private int radarOpen, radarDir, radarScore; private bool radarOk;
        private string RadarRoot()
        {
            string r = Instrument.MasterInstrument.Name.ToUpperInvariant();
            if (r == "MNQ") return "NQ"; if (r == "MES") return "ES"; if (r == "MYM") return "YM";
            return r;
        }
        // SYNC = share of available timeframe tiles pointing the same way as the bias (written for the Radar status line)
        private int syncPct = 0; private DateTime lastSyncWrite = DateTime.MinValue;
        private void CalcSync(double[] st, bool[] ok, double bi)
        {
            int n = 0, agree = 0, up = 0;
            for (int i = 0; i < NTF; i++) { if (!ok[i]) continue; n++; if (st[i] >= 0) up++; }
            if (n == 0) { syncPct = 0; return; }
            agree = bi >= 0 ? up : n - up;
            if (Math.Abs(bi) < 0.5) agree = Math.Max(up, n - up);
            syncPct = (int)Math.Round(100.0 * agree / n);
            if (DateTime.Now - lastSyncWrite < TimeSpan.FromSeconds(2)) return;
            lastSyncWrite = DateTime.Now;
            try { File.WriteAllText(Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GexSync_" + RadarRoot() + ".txt"), syncPct + "|" + bi.ToString("F0", CultureInfo.InvariantCulture)); } catch { }
        }
        private void ReadRadar()
        {
            try
            {
                if (DateTime.Now - lastRadarRead < TimeSpan.FromSeconds(2)) return;
                lastRadarRead = DateTime.Now;
                string f = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "RadarStatus_" + RadarRoot() + ".txt");
                if (!File.Exists(f)) { radarOk = false; return; }
                string[] a = File.ReadAllText(f).Split('|');
                if (a.Length < 6) { radarOk = false; return; }
                DateTime ts = DateTime.Parse(a[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
                radarOk = (DateTime.UtcNow - ts) < TimeSpan.FromMinutes(3);
                radarOpen = int.Parse(a[3]); radarDir = int.Parse(a[4]); radarScore = int.Parse(a[5]);
            }
            catch { radarOk = false; }
        }

        // order flow window
        private struct FlowEv { public DateTime T; public double Buy; public double Sell; }
        private Queue<FlowEv> flowQ = new Queue<FlowEv>();
        private double flowBuy, flowSell;
        private double lastAsk, lastBid, lastPx;

        private int vixIdx = -1, dxyIdx = -1;
        private StreamWriter rec;
        private string recPath = "";
        private string[] lastRow;

        public Series<double> BiasSeries { get { return Values[0]; } }
        public Series<double> SignalSeries { get { return Values[1]; } }

        // ------------------------------------------------------------------ properties
        [Display(Name = "Corner", GroupName = "Panel", Order = 0)]
        public KineticCorner Corner { get; set; }
        [Range(0, 2000)]
        [Display(Name = "Offset X (px)", GroupName = "Panel", Order = 1)]
        public int OffsetX { get; set; }
        [Range(0, 2000)]
        [Display(Name = "Offset Y (px)", GroupName = "Panel", Order = 2)]
        public int OffsetY { get; set; }
        [Range(0.5, 2.0)]
        [Display(Name = "Panel scale", GroupName = "Panel", Order = 3)]
        public double PanelScale { get; set; }

        [Range(2, 200)]
        [Display(Name = "EMA length (tiles)", GroupName = "Formulas", Order = 0)]
        public int EmaLen { get; set; }
        [Range(2, 200)]
        [Display(Name = "ATR length", GroupName = "Formulas", Order = 1)]
        public int AtrLen { get; set; }
        [Range(0.25, 10.0)]
        [Display(Name = "Tile scale (ATRs = 100%)", GroupName = "Formulas", Order = 2, Description = "Distance from the EMA, in ATRs, that counts as 100% strength.")]
        public double StrengthScale { get; set; }
        [Range(2, 100)]
        [Display(Name = "ADX length (power)", GroupName = "Formulas", Order = 3)]
        public int AdxLen { get; set; }
        [Range(2, 100)]
        [Display(Name = "RSI length", GroupName = "Formulas", Order = 4)]
        public int RsiLen { get; set; }
        [Range(10, 3600)]
        [Display(Name = "Order flow window (sec)", GroupName = "Formulas", Order = 5)]
        public int FlowWindowSec { get; set; }

        [Range(0, 100)]
        [Display(Name = "Bias weight: timeframes", GroupName = "Bias score", Order = 0)]
        public int WMtf { get; set; }
        [Range(0, 100)]
        [Display(Name = "Bias weight: order flow", GroupName = "Bias score", Order = 1)]
        public int WFlow { get; set; }
        [Range(0, 100)]
        [Display(Name = "Bias weight: RSI", GroupName = "Bias score", Order = 2)]
        public int WRsi { get; set; }
        [Range(1, 100)]
        [Display(Name = "Signal threshold (+-)", GroupName = "Bias score", Order = 3, Description = "Signal plot is +1 at/above this bias, -1 at/below its negative, else 0.")]
        public int SignalThreshold { get; set; }
        [Display(Name = "Signal pulse (fire once per cross)", GroupName = "Bias score", Order = 4, Description = "ON: the Signal plot is +1/-1 only on the bar where the bias first crosses the threshold, then 0 until it has dropped back inside the threshold at a bar close. OFF: the old level behaviour - +1/-1 for as long as the bias stays beyond the threshold, which makes an auto-trader re-enter every time it is flat. The recorder CSV keeps the level value.")]
        public bool SignalPulse { get; set; }

        [Display(Name = "Show VIX / DXY", GroupName = "Context", Order = 0, Description = "Needs both instruments in your data feed. Off by default so the indicator loads everywhere.")]
        public bool ShowVixDxy { get; set; }
        [Display(Name = "VIX instrument", GroupName = "Context", Order = 1)]
        public string VixName { get; set; }
        [Display(Name = "DXY instrument", GroupName = "Context", Order = 2)]
        public string DxyName { get; set; }

        [Display(Name = "Record CSV (for backtests)", GroupName = "Recorder", Order = 0)]
        public bool LogCsv { get; set; }

        // ------------------------------------------------------------------ lifecycle
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Sneaky Kinetic Panel";
                Description = "On-chart cockpit: multi-timeframe strength tiles, power, order flow, RSI and an open-formula bias score with signal plots.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                ZOrder = 10000;   // render above GEX level labels/lines
                IsAutoScale = false;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;
                Corner = KineticCorner.TopRight;
                OffsetX = 70; OffsetY = 40;
                PanelScale = 1.0;
                EmaLen = 20; AtrLen = 14; StrengthScale = 2.0; AdxLen = 14; RsiLen = 14; FlowWindowSec = 300;
                WMtf = 60; WFlow = 30; WRsi = 10; SignalThreshold = 40; SignalPulse = true;
                ShowVixDxy = false; VixName = "^VIX"; DxyName = "DX 12-26";
                LogCsv = true;
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Bias Score");
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Signal");
            }
            else if (State == State.Configure)
            {
                for (int i = 0; i < NTF; i++) AddDataSeries(BarsPeriodType.Minute, TfMin[i]);
                if (ShowVixDxy)
                {
                    AddDataSeries(VixName, BarsPeriodType.Minute, 1); vixIdx = NTF + 1;
                    AddDataSeries(DxyName, BarsPeriodType.Minute, 1); dxyIdx = NTF + 2;
                }
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => AttachDrag());
            }
            else if (State == State.DataLoaded)
            {
                for (int i = 0; i < NTF; i++)
                {
                    emaTF[i] = EMA(Closes[i + 1], EmaLen);
                    atrTF[i] = ATR(Closes[i + 1], AtrLen);
                }
                adxP = ADX(Close, AdxLen);
                rsiP = RSI(Close, RsiLen, 1);
                atrP = ATR(Close, AtrLen);
                volFast = SMA(Volume, 3);
                volSlow = SMA(Volume, 30);
                lastRow = null;
            }
            else if (State == State.Terminated)
            {
                try { if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => DetachDrag()); else DetachDrag(); } catch { }
                try { if (rec != null) { rec.Flush(); rec.Dispose(); } } catch { }
                rec = null;
                lock (sync) DisposeGfx();
            }
        }

        // ------------------------------------------------------------------ order flow (live ticks)
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0) return;
            if (e.MarketDataType == MarketDataType.Ask) { lastAsk = e.Price; return; }
            if (e.MarketDataType == MarketDataType.Bid) { lastBid = e.Price; return; }
            if (e.MarketDataType != MarketDataType.Last) return;

            double buy = 0, sell = 0, v = e.Volume;
            if (lastAsk > 0 && e.Price >= lastAsk) buy = v;
            else if (lastBid > 0 && e.Price <= lastBid) sell = v;
            else if (lastPx > 0 && e.Price > lastPx) buy = v;
            else if (lastPx > 0 && e.Price < lastPx) sell = v;
            else { buy = v * 0.5; sell = v * 0.5; }
            lastPx = e.Price;

            lock (sync)
            {
                FlowEv ev = new FlowEv(); ev.T = e.Time; ev.Buy = buy; ev.Sell = sell;
                flowQ.Enqueue(ev); flowBuy += buy; flowSell += sell;
                PruneFlow(e.Time);
            }
        }

        private void PruneFlow(DateTime now)
        {
            DateTime cut = now.AddSeconds(-FlowWindowSec);
            while (flowQ.Count > 0 && flowQ.Peek().T < cut)
            {
                FlowEv o = flowQ.Dequeue();
                flowBuy -= o.Buy; flowSell -= o.Sell;
            }
            if (flowQ.Count == 0) { flowBuy = 0; flowSell = 0; }
        }

        // ------------------------------------------------------------------ calculations
        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

        protected override void OnBarUpdate()
        {
            int bip = BarsInProgress;
            if (bip >= 1 && bip <= NTF)
            {
                int i = bip - 1;
                if (CurrentBars[bip] < Math.Max(EmaLen, AtrLen) + 2) return;
                double atr = atrTF[i][0];
                if (atr <= 0) return;
                double s = Clamp((Closes[bip][0] - emaTF[i][0]) / (StrengthScale * atr) * 100.0, -100, 100);
                lock (sync) { strength[i] = s; strengthOk[i] = true; }
                return;
            }
            if (bip == vixIdx && vixIdx > 0) { lock (sync) { vixVal = Closes[bip][0]; vixOk = true; } return; }
            if (bip == dxyIdx && dxyIdx > 0)
            {
                double chg = 0;
                if (CurrentBars[bip] > 61 && Closes[bip][60] > 0) chg = (Closes[bip][0] / Closes[bip][60] - 1.0) * 100.0;
                lock (sync) { dxyVal = Closes[bip][0]; dxyChg = chg; dxyOk = true; }
                return;
            }
            if (bip != 0) return;
            if (CurrentBar < Math.Max(30, Math.Max(AdxLen, RsiLen) + 2)) return;

            if (IsFirstTickOfBar && CurrentBar > 1 && LogCsv) WriteRow();

            lock (sync)
            {
                PruneFlow(Time[0]);
                power = adxP[0]; powerOk = true;
                rsiVal = rsiP[0]; rsiOk = true;
                double a = atrP[0];
                if (a > 0) { fastMom = (Close[0] - Close[3]) / a; fastOk = true; }
                double vs = volSlow[1];
                if (vs > 0) { volRatio = volFast[1] / vs; volOk = true; }

                // timeframe score (weighted mean of available tiles)
                double sw = 0, sv = 0;
                for (int i = 0; i < NTF; i++) if (strengthOk[i]) { sw += TfW[i]; sv += TfW[i] * strength[i]; }
                mtfScore = sw > 0 ? sv / sw : 0;

                double tot = flowBuy + flowSell;
                flowOk = tot > 0;
                flowBuyPct = flowOk ? 100.0 * flowBuy / tot : 50.0;
                flowScore = flowOk ? (flowBuyPct - 50.0) * 2.0 : 0;

                rsiScore = Clamp((rsiVal - 50.0) * 2.0, -100, 100);

                double w1 = WMtf, w2 = flowOk ? WFlow : 0, w3 = WRsi;
                double wt = w1 + w2 + w3;
                bias = wt > 0 ? (w1 * mtfScore + w2 * flowScore + w3 * rsiScore) / wt : 0;
                rawSignal = bias >= SignalThreshold ? 1 : (bias <= -SignalThreshold ? -1 : 0);
                if (SignalPulse)
                {
                    if (IsFirstTickOfBar && rawLast == 0) armed = true;      // previous bar closed inside the threshold: ready for a new cross
                    if (rawSignal != 0 && armed) { armed = false; pulseBar = CurrentBar; pulseDir = rawSignal; }
                    signal = (pulseBar == CurrentBar && rawSignal == pulseDir) ? pulseDir : 0;
                }
                else signal = rawSignal;
                rawLast = rawSignal;

                Values[0][0] = bias;
                Values[1][0] = signal;

                lastRow = BuildRow(Time[0], Close[0]);
            }
        }

        private string[] BuildRow(DateTime t, double c)
        {
            List<string> r = new List<string>();
            r.Add(t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            r.Add(c.ToString("F2", CultureInfo.InvariantCulture));
            for (int i = 0; i < NTF; i++) r.Add(strengthOk[i] ? strength[i].ToString("F1", CultureInfo.InvariantCulture) : "");
            r.Add(powerOk ? power.ToString("F1", CultureInfo.InvariantCulture) : "");
            r.Add(flowOk ? flowBuyPct.ToString("F1", CultureInfo.InvariantCulture) : "");
            r.Add(rsiOk ? rsiVal.ToString("F1", CultureInfo.InvariantCulture) : "");
            r.Add(fastOk ? fastMom.ToString("F2", CultureInfo.InvariantCulture) : "");
            r.Add(bias.ToString("F1", CultureInfo.InvariantCulture));
            r.Add(rawSignal.ToString(CultureInfo.InvariantCulture));
            r.Add(vixOk ? vixVal.ToString("F2", CultureInfo.InvariantCulture) : "");
            r.Add(dxyOk ? dxyVal.ToString("F2", CultureInfo.InvariantCulture) : "");
            return r.ToArray();
        }

        // snapshot from the last tick of the bar that just closed -> one row per bar
        private void WriteRow()
        {
            try
            {
                string[] row;
                lock (sync) row = lastRow;
                if (row == null) return;
                if (rec == null)
                {
                    string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SneakyExport");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string nm = Instrument.MasterInstrument.Name + "_" + BarsPeriod.BarsPeriodType.ToString() + BarsPeriod.Value.ToString(CultureInfo.InvariantCulture);
                    recPath = Path.Combine(dir, "kinetic_" + nm + ".csv");
                    bool exists = File.Exists(recPath);
                    rec = new StreamWriter(new FileStream(recPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                    if (!exists) rec.WriteLine("time,close,m1,m3,m5,m15,m30,h1,power,flow_buy_pct,rsi,fast_mom,bias,signal,vix,dxy");
                }
                rec.WriteLine(string.Join(",", row));
                rec.Flush();
            }
            catch { }
        }

        // ------------------------------------------------------------------ rendering
        private Dictionary<long, D2D.SolidColorBrush> brushCache = new Dictionary<long, D2D.SolidColorBrush>();
        private Dictionary<int, DW.TextFormat> fmtCache = new Dictionary<int, DW.TextFormat>();

        private void DisposeGfx()
        {
            foreach (D2D.SolidColorBrush b in brushCache.Values) { try { b.Dispose(); } catch { } }
            brushCache.Clear();
            foreach (DW.TextFormat f in fmtCache.Values) { try { f.Dispose(); } catch { } }
            fmtCache.Clear();
        }
        public override void OnRenderTargetChanged() { lock (sync) DisposeGfx(); }

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
        private DW.TextFormat Fmt(float px, bool bold, bool center)
        {
            int key = ((int)(px * 10)) * 4 + (bold ? 1 : 0) + (center ? 2 : 0);
            DW.TextFormat f;
            if (!fmtCache.TryGetValue(key, out f))
            {
                DW.Factory dw = new DW.Factory();
                f = new DW.TextFormat(dw, "Arial", bold ? DW.FontWeight.Bold : DW.FontWeight.Normal, DW.FontStyle.Normal, px);
                f.WordWrapping = DW.WordWrapping.NoWrap;
                if (center) f.TextAlignment = DW.TextAlignment.Center;
                dw.Dispose();
                fmtCache[key] = f;
            }
            return f;
        }

        private void Fill(float x, float y, float w, float h, int rgb, float a)
        {
            RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)), Br(rgb, a));
        }
        private void Frame(float x, float y, float w, float h, int rgb, float a, float t)
        {
            RenderTarget.DrawRectangle(new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)), Br(rgb, a), t);
        }
        private void Txt(string s, float x, float y, float w, float px, int rgb, bool bold, bool center)
        {
            RenderTarget.DrawText(s, Fmt(px, bold, center), new SharpDX.RectangleF(x, y, w, px + 6f), Br(rgb, 1f));
        }

        private const int CBg = 0x0A0F1A, CBorder = 0x00E5D0, CGreen = 0x00E676, CRed = 0xFF3355, CAmber = 0xFFB300, CYellow = 0xFFD400, CCyan = 0x00E5FF, CGrey = 0x8A93A6, CWhite = 0xF2F5FA, COrange = 0xFF9100;

        private void RFill(float x, float y, float w, float h, float r, int rgb, float a)
        {
            D2D.RoundedRectangle rr = new D2D.RoundedRectangle();
            rr.Rect = new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)); rr.RadiusX = r; rr.RadiusY = r;
            RenderTarget.FillRoundedRectangle(rr, Br(rgb, a));
        }
        private void RFrame(float x, float y, float w, float h, float r, int rgb, float a, float t)
        {
            D2D.RoundedRectangle rr = new D2D.RoundedRectangle();
            rr.Rect = new SharpDX.RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h)); rr.RadiusX = r; rr.RadiusY = r;
            RenderTarget.DrawRoundedRectangle(rr, Br(rgb, a), t);
        }


        // ---- drag-to-move (grab the header strip) ----
        private System.Windows.FrameworkElement dragCtl;
        private bool dragging, invPending;
        private float pRx, pRy, pRw, pRh;
        private System.Windows.Point dStart; private int dOffX, dOffY;
        private double DevScale()
        {
            try { var src = System.Windows.PresentationSource.FromVisual(dragCtl); if (src != null && src.CompositionTarget != null) return src.CompositionTarget.TransformToDevice.M11; } catch { }
            return 1.0;
        }
        private void AttachDrag()
        {
            try
            {
                if (ChartControl == null || dragCtl != null) return;
                dragCtl = ChartControl;
                dragCtl.PreviewMouseLeftButtonDown += DragDown;
                dragCtl.PreviewMouseMove += DragMove;
                dragCtl.PreviewMouseLeftButtonUp += DragUp;
            }
            catch { }
        }
        private void DetachDrag()
        {
            try
            {
                if (dragCtl == null) return;
                dragCtl.PreviewMouseLeftButtonDown -= DragDown;
                dragCtl.PreviewMouseMove -= DragMove;
                dragCtl.PreviewMouseLeftButtonUp -= DragUp;
                dragCtl = null;
            }
            catch { }
        }
        private void DragDown(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (dragCtl == null || pRw <= 0f) return;
                double sc = DevScale();
                System.Windows.Point p = e.GetPosition(dragCtl);
                double mx = p.X * sc, my = p.Y * sc;
                if (mx < pRx || mx > pRx + pRw || my < pRy || my > pRy + pRh) return;
                dragging = true; dStart = new System.Windows.Point(mx, my); dOffX = OffsetX; dOffY = OffsetY;
                dragCtl.CaptureMouse();
                e.Handled = true;
            }
            catch { }
        }
        private void DragMove(object s, System.Windows.Input.MouseEventArgs e)
        {
            try
            {
                if (!dragging || dragCtl == null) return;
                double sc = DevScale();
                System.Windows.Point p = e.GetPosition(dragCtl);
                double dx = p.X * sc - dStart.X, dy = p.Y * sc - dStart.Y;
                bool left = Corner == KineticCorner.TopLeft || Corner == KineticCorner.BottomLeft, top = Corner == KineticCorner.TopLeft || Corner == KineticCorner.TopRight;
                OffsetX = (int)Math.Max(0, Math.Min(2000, dOffX + (left ? dx : -dx)));
                OffsetY = (int)Math.Max(0, Math.Min(2000, dOffY + (top ? dy : -dy)));
                if (!invPending)
                {
                    invPending = true;   // coalesce: at most one chart redraw per render frame
                    dragCtl.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
                    {
                        invPending = false;
                        try { if (dragCtl != null) dragCtl.InvalidateVisual(); } catch { }
                    }));
                }
                e.Handled = true;
            }
            catch { }
        }
        private void DragUp(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (!dragging) return;
                dragging = false;
                dragCtl.ReleaseMouseCapture();
                e.Handled = true;
            }
            catch { }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null || ChartPanel == null || chartControl == null) return;
            base.OnRender(chartControl, chartScale);
            ReadRadar();

            double[] s = new double[NTF]; bool[] sOk = new bool[NTF];
            double pw, rs, fm, vr, bi, ms, fs, rsc, vx, dx, dxc, fb;
            bool pwOk, rsOk, fmOk, vrOk, flOk, vxOk, dxOk; int sg;
            lock (sync)
            {
                Array.Copy(strength, s, NTF); Array.Copy(strengthOk, sOk, NTF);
                pw = power; pwOk = powerOk; rs = rsiVal; rsOk = rsiOk; fm = fastMom; fmOk = fastOk; vr = volRatio; vrOk = volOk;
                bi = bias; ms = mtfScore; fs = flowScore; rsc = rsiScore; vx = vixVal; vxOk = vixOk; dx = dxyVal; dxc = dxyChg; dxOk = dxyOk;
                fb = flowBuyPct; flOk = flowOk; sg = rawSignal;
            }

            CalcSync(s, sOk, bi);
            float k = (float)PanelScale;
            float W = 410f * k, pad = 12f * k, f1 = 12f * k, f2 = 10.5f * k;
            float H = 300f * k;
            float px, py;
            float L = ChartPanel.X, T = ChartPanel.Y, R = ChartPanel.X + ChartPanel.W, B = ChartPanel.Y + ChartPanel.H;
            if (Corner == KineticCorner.TopRight || Corner == KineticCorner.BottomRight) px = R - W - OffsetX; else px = L + OffsetX;
            if (Corner == KineticCorner.TopRight || Corner == KineticCorner.TopLeft) py = T + OffsetY; else py = B - H - OffsetY;
            if (px < L) px = L; if (py < T) py = T;

            pRx = px; pRy = py; pRw = W; pRh = 34f * k;
            int dirC = bi >= SignalThreshold ? CGreen : (bi <= -SignalThreshold ? CRed : CAmber);
            lock (sync)
            {
                RFill(px, py, W, H, 10f * k, CBg, 0.94f);
                RFrame(px, py, W, H, 10f * k, CBorder, 1f, 1.6f);

                float x = px + pad, y = py + 9f * k, iw = W - pad * 2;

                // title + bias tag
                Txt("\u26A1 " + Instrument.MasterInstrument.Name + " KINETIC COCKPIT [SYNC " + syncPct + "%]", x, y + 4f * k, iw - 156f * k, 11f * k, CGreen, true, false);
                string tag = "[BIAS] " + (bi >= SignalThreshold ? "LONG " : (bi <= -SignalThreshold ? "SHORT " : "NEUTRAL ")) + (bi >= 0 ? "+" : "") + bi.ToString("F0", CultureInfo.InvariantCulture);
                float tagW = 150f * k;
                RFill(px + W - pad - tagW, y, tagW, 24f * k, 5f * k, dirC, 0.12f);
                RFrame(px + W - pad - tagW, y, tagW, 24f * k, 5f * k, dirC, 1f, 1.3f);
                Txt(tag, px + W - pad - tagW, y + 5f * k, tagW, f2, dirC, true, true);
                y += 30f * k;

                // radar badge (reads the Sneaky GEX Radar on this instrument)
                {
                    string rb; int rbC;
                    if (!radarOk) { rb = "[RADAR] off - add Sneaky GEX Radar to this chart"; rbC = CGrey; }
                    else if (radarOpen > 0) { rb = "[RADAR] " + radarScore + "% STOP-HUNT " + (radarDir > 0 ? "LONG" : "SHORT") + (radarOpen > 1 ? "  (+" + (radarOpen - 1) + ")" : ""); rbC = radarDir > 0 ? CGreen : CRed; }
                    else { rb = "[RADAR] scanning - no sweep yet"; rbC = CAmber; }
                    RFill(x, y, iw, 18f * k, 4f * k, rbC, radarOk && radarOpen > 0 ? 0.14f : 0.07f);
                    RFrame(x, y, iw, 18f * k, 4f * k, rbC, 0.9f, 1.1f);
                    Txt(rb, x, y + 3f * k, iw, f2, rbC, true, true);
                }
                y += 26f * k;

                // timeframe tiles
                float gap = 6f * k, tw = (iw - gap * (NTF - 1)) / NTF, th = 40f * k;
                for (int i = 0; i < NTF; i++)
                {
                    float tx = x + i * (tw + gap);
                    bool flat0 = sOk[i] && Math.Round(s[i]) == 0;   // rounds to 0% -> neutral, not a red/green arrow
                    int c = !sOk[i] ? CGrey : (flat0 ? CYellow : (s[i] >= 0 ? CGreen : CRed));
                    RFill(tx, y, tw, th, 5f * k, c, 0.10f);
                    RFrame(tx, y, tw, th, 5f * k, c, 1f, 1.4f);
                    Txt(TfName[i], tx, y + 4f * k, tw, f2, CWhite, false, true);
                    string v = !sOk[i] ? "--" : (flat0 ? "\u2014 0%" : ((s[i] >= 0 ? "\u25B2 " : "\u25BC ") + Math.Abs(s[i]).ToString("F0", CultureInfo.InvariantCulture) + "%"));
                    Txt(v, tx, y + 21f * k, tw, f2, c, true, true);
                }
                y += th + 10f * k;

                // power (10 bright segments)
                Txt("POWER: " + (pwOk ? pw.ToString("F0", CultureInfo.InvariantCulture) + "%" : "--"), x, y, iw, f2, CYellow, true, false);
                y += 16f * k;
                Segs(x, y, iw * 0.34f, 7f * k, 10, pwOk ? (int)Math.Round(Clamp(pw, 0, 100) / 10.0) : 0, CYellow, 3f * k);
                y += 14f * k;

                // order flow line + segmented bar
                string press = !flOk ? "--" : (fb >= 55 ? "BUY \u25B2" : (fb <= 45 ? "SELL \u25BC" : "BALANCED"));
                string vol = !vrOk ? "--" : (vr >= 1.3 ? "HIGH" : (vr <= 0.7 ? "LOW" : "NORMAL"));
                int pressC = !flOk ? CGrey : (fb >= 55 ? CGreen : (fb <= 45 ? CRed : CAmber));
                string flowTxt = flOk
                    ? ("ORDERFLOW:  " + (100 - fb).ToString("F0", CultureInfo.InvariantCulture) + "% SELL vs " + fb.ToString("F0", CultureInfo.InvariantCulture) + "% BUY  |  PRESSURE: " + press + "  |  VOL: " + vol)
                    : ("ORDERFLOW:  no live ticks  |  VOL: " + vol);
                Txt(flowTxt, x, y, iw, f2, flOk ? CWhite : CGrey, true, false);
                y += 16f * k;
                {
                    int tot = 44, sellN = flOk ? (int)Math.Round((100 - fb) / 100.0 * tot) : 0;
                    float sgap = 2f * k, sw = (iw - sgap * (tot - 1)) / tot;
                    for (int i = 0; i < tot; i++)
                        Fill(x + i * (sw + sgap), y, sw, 7f * k, !flOk ? CGrey : (i < sellN ? CRed : CGreen), !flOk ? 0.25f : 1f);
                }
                y += 14f * k;

                // fast momentum + bar
                string fmT = !fmOk ? "--" : (Math.Abs(fm).ToString("F1", CultureInfo.InvariantCulture) + "x " + (fm >= 0 ? "\u25B2" : "\u25BC") + (Math.Abs(fm) >= 1.0 ? "  (MOMENTUM ACTIVE)" : "  (WEAK)"));
                int fmC = !fmOk ? CGrey : (Math.Abs(fm) >= 1.0 ? (fm > 0 ? CGreen : CRed) : CGrey);
                Txt("FAST MOMENTUM:  " + fmT, x, y, iw, f2, fmC, true, false);
                y += 16f * k;
                {
                    int tot = 44, on = fmOk ? (int)Math.Round(Clamp(Math.Abs(fm) / 3.0, 0, 1) * tot) : 0;
                    float sgap = 2f * k, sw = (iw - sgap * (tot - 1)) / tot;
                    for (int i = 0; i < tot; i++)
                        Fill(x + i * (sw + sgap), y, sw, 7f * k, i < on ? fmC : CGrey, i < on ? 1f : 0.2f);
                }
                y += 15f * k;

                // RSI (left) + VIX / DXY (right)
                string rsT = !rsOk ? "--" : (rs.ToString("F1", CultureInfo.InvariantCulture) + (rs >= 70 ? " (OVERBOUGHT)" : (rs <= 30 ? " (OVERSOLD)" : " (NEUTRAL)")));
                Txt("RSI: " + rsT, x, y, iw * 0.45f, f2, CGrey, false, false);
                if (ShowVixDxy)
                {
                    float rx = x + iw * 0.47f;
                    string vlab = !vxOk ? "n/a" : (vx < 15 ? "LOW" : (vx < 20 ? "NORMAL" : (vx < 30 ? "ELEVATED" : "PANIC")));
                    int vlev = !vxOk ? 0 : (vx < 15 ? 1 : (vx < 20 ? 2 : 3));
                    int vc = !vxOk ? CGrey : (vx < 15 ? CGreen : (vx < 20 ? COrange : CRed));
                    Txt("VIX (FEAR):  \u25CF " + (vxOk ? vx.ToString("F1", CultureInfo.InvariantCulture) : "--") + " (" + vlab + ")", rx, y, iw * 0.53f, f2, vc, true, false);
                    Segs(x + iw - 30f * k, y + 3f * k, 30f * k, 8f * k, 3, vlev, vc, 2f * k);
                    string dlab = !dxOk ? "n/a" : (dxc > 0.15 ? "STRONG \u25B2" : (dxc < -0.15 ? "WEAK \u25BC" : "NEUTRAL \u2014"));
                    Txt("DXY (DOLLAR):  \u25CF " + dlab, rx, y + 15f * k, iw * 0.53f, f2, CGrey, true, false);
                }
                y += 34f * k;

                // bias score (the honest name for the "forecast" line)
                Txt("BIAS SCORE: " + (bi >= 0 ? "+" : "") + bi.ToString("F0", CultureInfo.InvariantCulture), x, y, iw * 0.55f, 16f * k, dirC, true, false);
                Txt("tf " + ms.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " | flow " + (flOk ? fs.ToString("+0;-0;0", CultureInfo.InvariantCulture) : "--") + " | rsi " + rsc.ToString("+0;-0;0", CultureInfo.InvariantCulture),
                    x + iw * 0.52f, y + 5f * k, iw * 0.48f, 9.5f * k, CGrey, false, false);
                y += 26f * k;

                // forecast: bias mapped through a logistic curve, nudged by an open Radar setup.
                // This is a readable re-scaling of the bias score, NOT a backtested probability.
                {
                    double z = bi / 30.0 + (radarOk && radarOpen > 0 ? radarDir * 0.4 : 0.0);
                    double pUp = 100.0 / (1.0 + Math.Exp(-z));
                    bool up = pUp >= 50.0;
                    double pc = up ? pUp : 100.0 - pUp;
                    int fc = pc < 55 ? CAmber : (up ? CGreen : CRed);
                    Txt("FORECAST: " + pc.ToString("F0", CultureInfo.InvariantCulture) + "% " + (pc < 55 ? "FLAT" : (up ? "UP \u25B2" : "DOWN \u25BC")), x, y, iw * 0.6f, 16f * k, fc, true, false);
                    Txt("bias-based, not backtested", x + iw * 0.6f, y + 5f * k, iw * 0.4f, 9.5f * k, CGrey, false, false);
                }
            }
        }

        private void Segs(float x, float y, float w, float h, int total, int on, int rgb, float gap)
        {
            float sw = (w - gap * (total - 1)) / total;
            for (int i = 0; i < total; i++) Fill(x + i * (sw + gap), y, sw, h, rgb, i < on ? 1f : 0.16f);
        }
    }
}
