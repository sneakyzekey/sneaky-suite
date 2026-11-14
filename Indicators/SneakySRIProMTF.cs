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
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// SneakySRIProMTF -- NinjaTrader 8 port of the TradingView script
// "Trinity Capital SRI PRO 2027 & MTF" (Pine v5).
//   Panel 1: smoothed RSI histogram (+ fill around 50) and DSS / trigger oscillator, 20/30/50/70/80 levels.
//   Panel 2: 5-timeframe trend table, consensus, composite strength %, loss-of-strength %, signal row.
//   Signals: buy  = DSS crosses above trigger below the oversold level AND consensus up
//            sell = DSS crosses below trigger above the overbought level AND consensus down
namespace NinjaTrader.NinjaScript.Indicators
{
    public enum SriPanelPos { TopRight, TopLeft, BottomRight, BottomLeft }

    public class SneakySRIProMTF : Indicator
    {
        #region Fields
        private const int N = 6;                       // index 0 = chart series, 1..5 = MTF series
        private RSI[] rsiI = new RSI[N];
        private EMA[] rsiSI = new EMA[N];
        private MACD[] macdI = new MACD[N];
        private EMA[] emaF = new EMA[N];
        private EMA[] emaS = new EMA[N];
        private ADX[] adxI = new ADX[N];
        private ATR[] atrI = new ATR[N];

        private Series<double> stochS, stoch2, dirMag, strengthS, lossS;
        private MAX maxH, maxX;
        private MIN minL, minX;
        private EMA xPre, xRaw, xDss, xTrigRaw, xTrig;

        private readonly int[] trendTF = new int[5];
        private readonly double[] strTF = new double[5];
        private int[] tfMinutes = new int[5];

        // panel state (written in OnBarUpdate, read in OnRender)
        private volatile int curTrend, consensus;
        private double strengthPct = 50, lossPct, curStrength;
        private bool buyNow, sellNow;
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "RSI + DSS oscillator with a 5-timeframe trend / strength panel (port of Trinity Capital SRI PRO 2027 & MTF).";
                Name = "SneakySRIProMTF";
                Calculate = Calculate.OnEachTick;
                IsOverlay = false;
                DisplayInDataBox = true;
                DrawOnPricePanel = false;
                IsSuspendedWhileInactive = true;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;

                PurpleColor = Brushes.DarkViolet;
                GrayColor = Brushes.Gainsboro;
                RedColor = Brushes.Tomato;
                GoldColor = Brushes.Gold;

                RsiLen = 14; RsiSmoothLen = 6;
                Pds = 9; EmaLen = 6; TriggerLen = 2; DssSmooth = 2; TriggerSmooth = 1;
                Overbought = 80; Oversold = 20;

                Tf1 = 30; Tf2 = 15; Tf3 = 5; Tf4 = 3; Tf5 = 1;
                UseAdx = true;
                MacdFast = 12; MacdSlow = 26; MacdSignal = 9;
                PanelFastMA = 21; PanelSlowMA = 50;

                PanelPos = SriPanelPos.TopRight;
                ShowPanel = true;
                EnableAlerts = true;

                AddPlot(new Stroke(Brushes.DarkViolet, 2), PlotStyle.Bar, "RSI Histogram");   // 0
                AddPlot(new Stroke(Brushes.DarkViolet, 2), PlotStyle.Line, "DSS");             // 1
                AddPlot(new Stroke(Brushes.Gold, 1), PlotStyle.Line, "Trigger");               // 2

                AddLine(new Stroke(Brushes.Transparent, 1), 0, "Min");
                AddLine(new Stroke(Brushes.Transparent, 1), 100, "Max");
                AddLine(new Stroke(Brushes.RoyalBlue, 1), 50, "Median");
                AddLine(new Stroke(Brushes.Gray, DashStyleHelper.Dash, 1), 70, "Band Up");
                AddLine(new Stroke(Brushes.Gray, DashStyleHelper.Dash, 1), 30, "Band Down");
                AddLine(new Stroke(Brushes.DarkViolet, 1), 80, "Overbought");
                AddLine(new Stroke(Brushes.Gainsboro, 1), 20, "Oversold");
            }
            else if (State == State.Configure)
            {
                tfMinutes = new int[] { Tf1, Tf2, Tf3, Tf4, Tf5 };
                for (int i = 0; i < 5; i++)
                    AddDataSeries(BarsPeriodType.Minute, Math.Max(1, tfMinutes[i]));
            }
            else if (State == State.DataLoaded)
            {
                for (int k = 0; k < N; k++)
                {
                    rsiI[k] = RSI(Closes[k], RsiLen, 1);
                    rsiSI[k] = EMA(rsiI[k], RsiSmoothLen);
                    macdI[k] = MACD(Closes[k], MacdFast, MacdSlow, MacdSignal);
                    emaF[k] = EMA(Closes[k], PanelFastMA);
                    emaS[k] = EMA(Closes[k], PanelSlowMA);
                    adxI[k] = ADX(Closes[k], 14);
                    atrI[k] = ATR(Closes[k], 14);
                }

                stochS = new Series<double>(this);
                stoch2 = new Series<double>(this);
                dirMag = new Series<double>(this);
                strengthS = new Series<double>(this);
                lossS = new Series<double>(this);

                maxH = MAX(Highs[0], Pds);
                minL = MIN(Lows[0], Pds);
                xPre = EMA(stochS, EmaLen);
                maxX = MAX(xPre, Pds);
                minX = MIN(xPre, Pds);
                xRaw = EMA(stoch2, EmaLen);
                xDss = EMA(xRaw, DssSmooth);
                xTrigRaw = EMA(xDss, TriggerLen);
                xTrig = EMA(xTrigRaw, TriggerSmooth);
            }
        }
        #endregion

        #region Calculation helpers
        private static double Clamp(double v, double lo, double hi)
        {
            return Math.Max(lo, Math.Min(hi, v));
        }

        // returns trend (+1 / -1 / 0) and signed strength (-1..+1) for series k
        private void Calc(int k, out int trend, out double strength)
        {
            trend = 0; strength = 0;
            if (CurrentBars[k] < 2) return;

            double r = rsiI[k][0];
            double rs = rsiSI[k][0], rsPrev = rsiSI[k][1];
            double macdLine = macdI[k].Default[0], sig = macdI[k].Avg[0];
            double fast = emaF[k][0], slow = emaS[k][0];
            double adx = adxI[k][0];
            double atr = Math.Max(atrI[k][0], TickSize);

            // --- trend vote (f_trend)
            int bull = 0, bear = 0;
            if (r > 55) bull++; else if (r < 45) bear++;
            if (macdLine > sig) bull++; else if (macdLine < sig) bear++;
            if (fast > slow) bull++; else if (fast < slow) bear++;
            bool isTrending = UseAdx ? adx > 20 : true;
            if (isTrending)
                trend = bull >= 2 ? 1 : bear >= 2 ? -1 : 0;

            // --- composite strength (f_strength_signed)
            double rsiC = Clamp((rs - 50.0) / 20.0, -1.0, 1.0);
            double macdC = Clamp(((macdLine - sig) / atr) * 4.0, -1.0, 1.0);
            double maC = Clamp(((fast - slow) / atr) * 2.0, -1.0, 1.0);
            double baseDir = rsiC + macdC + maC;
            double dirSign = baseDir > 0 ? 1.0 : baseDir < 0 ? -1.0 : 0.0;
            double adxC = Clamp((adx - 20.0) / 25.0, 0.0, 1.0) * dirSign;
            double rsiMom = Clamp((rs - rsPrev) / 2.0, -1.0, 1.0);
            strength = Clamp(rsiC * 0.25 + macdC * 0.25 + maC * 0.20 + adxC * 0.20 + rsiMom * 0.10, -1.0, 1.0);
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            if (BarsInProgress >= 1)
            {
                int t; double s;
                Calc(BarsInProgress, out t, out s);
                trendTF[BarsInProgress - 1] = t;
                strTF[BarsInProgress - 1] = s;
                return;
            }

            if (CurrentBar < 1)
                return;

            // ---- RSI histogram
            double rsiSmooth = rsiSI[0][0];
            Values[0][0] = rsiSmooth;
            PlotBrushes[0][0] = rsiSmooth > 50 ? PurpleColor : GrayColor;

            // ---- DSS
            double hh = maxH[0], ll = minL[0];
            stochS[0] = hh > ll ? 100.0 * (Close[0] - ll) / (hh - ll) : stochS[1];
            double xp = xPre[0];
            double hx = maxX[0], lx = minX[0];
            stoch2[0] = hx > lx ? 100.0 * (xp - lx) / (hx - lx) : stoch2[1];
            double dss = xDss[0], trig = xTrig[0];
            Values[1][0] = dss;
            Values[2][0] = trig;
            PlotBrushes[1][0] = dss > trig ? PurpleColor : GrayColor;

            // ---- MTF / strength
            int t0; double s0;
            Calc(0, out t0, out s0);
            curTrend = t0;
            curStrength = s0;

            int score = 0;
            double sumS = 0;
            for (int i = 0; i < 5; i++) { score += trendTF[i]; sumS += strTF[i]; }
            consensus = score >= 3 ? 1 : score <= -3 ? -1 : 0;

            double mtf = sumS / 5.0;
            double fin = Clamp(mtf * 0.70 + s0 * 0.30, -1.0, 1.0);
            strengthPct = 50.0 + fin * 50.0;

            dirMag[0] = Math.Abs(fin);
            double peak = 0;
            for (int i = 0; i < 5 && i <= CurrentBar; i++)
                peak = Math.Max(peak, dirMag[i]);
            lossPct = peak > 0.05 ? Clamp((peak - dirMag[0]) / peak * 100.0, 0.0, 100.0) : 0.0;
            strengthS[0] = strengthPct;
            lossS[0] = lossPct;

            bool crossUp = dss > trig && Values[1][1] <= Values[2][1];
            bool crossDn = dss < trig && Values[1][1] >= Values[2][1];
            buyNow = crossUp && dss < Oversold && consensus == 1;
            sellNow = crossDn && dss > Overbought && consensus == -1;

            // ---- alerts: evaluate the bar that just closed, live bars only
            if (EnableAlerts && State == State.Realtime && IsFirstTickOfBar && CurrentBar > 2)
            {
                double d1 = Values[1][1], t1 = Values[2][1], d2 = Values[1][2], t2 = Values[2][2];
                bool buyPrev = d1 > t1 && d2 <= t2 && d1 < Oversold && consensus == 1;
                bool sellPrev = d1 < t1 && d2 >= t2 && d1 > Overbought && consensus == -1;
                double sp = strengthS[1], lp = lossS[1];
                bool reversal = (sp > 40.0 && sp < 60.0 && lp >= 20.0) || lp >= 35.0;
                string snd = NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav";
                if (buyPrev) Alert("SRI_buy_" + CurrentBar, Priority.High, "SRI 2027: Buy signal - oscillator and consensus aligned UP", snd, 1, Brushes.Black, Brushes.White);
                if (sellPrev) Alert("SRI_sell_" + CurrentBar, Priority.High, "SRI 2027: Sell signal - oscillator and consensus aligned DOWN", snd, 1, Brushes.Black, Brushes.White);
                if (reversal) Alert("SRI_loss_" + CurrentBar, Priority.Medium, "SRI 2027: Significant loss of strength detected. Possible slowdown/reversal. Confirm with the MTF.", snd, 1, Brushes.Black, Brushes.White);
            }
        }
        #endregion

        #region Rendering (RSI fill, 30-70 band, MTF table)
        private static string TrendText(int t) { return t == 1 ? "▲ UP" : t == -1 ? "▼ DOWN" : "— SIDEWAYS"; }

        private Brush TrendBrush(int t) { return t == 1 ? PurpleColor : t == -1 ? RedColor : GrayColor; }

        private SharpDX.Direct2D1.Brush Dx(Brush b, float opacity)
        {
            SharpDX.Direct2D1.Brush d = b.ToDxBrush(RenderTarget);
            d.Opacity = opacity;
            return d;
        }

        private void DrawRow(string text, float x, float y, float w, float h, Brush textBrush, Brush bg, float bgOpacity, bool center)
        {
            if (bg != null)
            {
                using (SharpDX.Direct2D1.Brush bgB = Dx(bg, bgOpacity))
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, w, h), bgB);
            }
            using (SharpDX.Direct2D1.Brush tb = Dx(textBrush, 1f))
            using (SharpDX.DirectWrite.TextFormat fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
                SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, 12f))
            using (SharpDX.DirectWrite.TextLayout lay = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, text, fmt, w - 8f, h))
            {
                if (center) lay.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(x + 4f, y + 1f), lay, tb, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (Bars == null || chartControl == null || ChartBars == null || RenderTarget == null)
                return;

            float panelW = ChartPanel.W, panelH = ChartPanel.H;

            // --- 30..70 band
            float yTop = chartScale.GetYByValue(70), yBot = chartScale.GetYByValue(30);
            using (SharpDX.Direct2D1.Brush bandB = Dx(PurpleColor, 0.05f))
                RenderTarget.FillRectangle(new SharpDX.RectangleF(ChartPanel.X, Math.Min(yTop, yBot), panelW, Math.Abs(yBot - yTop)), bandB);

            // --- RSI fill between 50 and the smoothed RSI
            float y50 = chartScale.GetYByValue(50);
            float bw = chartControl.Properties.BarDistance;
            using (SharpDX.Direct2D1.Brush fillUp = Dx(PurpleColor, 0.6f))
            using (SharpDX.Direct2D1.Brush fillDn = Dx(GrayColor, 0.6f))
            {
                for (int i = ChartBars.FromIndex; i <= ChartBars.ToIndex; i++)
                {
                    if (!Values[0].IsValidDataPointAt(i)) continue;
                    double v = Values[0].GetValueAt(i);
                    float x = chartControl.GetXByBarIndex(ChartBars, i);
                    float y = chartScale.GetYByValue(v);
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(x - bw / 2f, Math.Min(y, y50), bw, Math.Abs(y - y50)), v > 50 ? fillUp : fillDn);
                }
            }

            if (!ShowPanel) return;

            // --- MTF table
            string[] rows = new string[11];
            Brush[] txt = new Brush[11];
            Brush[] bgs = new Brush[11];
            float[] bgo = new float[11];

            rows[0] = "SRI OSCILLATOR PRO 2027 & MTF"; txt[0] = Brushes.White; bgs[0] = PurpleColor; bgo[0] = 0.35f;
            for (int i = 0; i < 5; i++)
            {
                rows[1 + i] = tfMinutes[i] + "  " + TrendText(trendTF[i]);
                txt[1 + i] = TrendBrush(trendTF[i]);
            }
            rows[6] = "CONSENSUS " + TrendText(consensus); txt[6] = TrendBrush(consensus); bgs[6] = PurpleColor; bgo[6] = 0.10f;
            rows[7] = "CURRENT " + TrendText(curTrend); txt[7] = TrendBrush(curTrend);

            double sp = strengthPct, lp = lossPct;
            string strengthText = sp >= 70.0 ? "BUY ONLY" : sp >= 60.0 ? "UP / STRONG" : sp > 40.0 ? "REVERSAL!" : sp >= 20.0 ? "DOWN / STRONG" : "SELL ONLY";
            Brush strengthBrush = sp >= 60.0 ? PurpleColor : sp > 40.0 ? GrayColor : RedColor;
            rows[8] = "STRENGTH " + sp.ToString("0.0") + "% | " + strengthText; txt[8] = Brushes.White; bgs[8] = strengthBrush; bgo[8] = 0.75f;

            string lossText = lp >= 35.0 ? "REVERSAL!" : lp >= 20.0 ? "STRENGTH FALLING!" : "STRENGTH STABLE";
            Brush lossBrush = lp >= 35.0 ? RedColor : lp >= 20.0 ? GoldColor : GrayColor;
            rows[9] = "LOSS " + lp.ToString("0.0") + "% | " + lossText; txt[9] = Brushes.White; bgs[9] = lossBrush; bgo[9] = 0.65f;

            string status = buyNow ? "SIGNAL: BUY" : sellNow ? "SIGNAL: SELL" : "SIGNAL: WAIT";
            Brush statusBrush = buyNow ? PurpleColor : sellNow ? RedColor : GrayColor;
            rows[10] = status; txt[10] = Brushes.White; bgs[10] = statusBrush; bgo[10] = 0.70f;

            float w = 250f, rh = 18f, h = rh * 11f;
            float margin = 8f;
            float px = (PanelPos == SriPanelPos.TopRight || PanelPos == SriPanelPos.BottomRight) ? ChartPanel.X + panelW - w - margin : ChartPanel.X + margin;
            float py = (PanelPos == SriPanelPos.TopRight || PanelPos == SriPanelPos.TopLeft) ? ChartPanel.Y + margin : ChartPanel.Y + panelH - h - margin;

            using (SharpDX.Direct2D1.Brush back = Dx(Brushes.Black, 0.85f))
                RenderTarget.FillRectangle(new SharpDX.RectangleF(px, py, w, h), back);

            for (int i = 0; i < 11; i++)
                DrawRow(rows[i], px, py + i * rh, w, rh, txt[i], bgs[i], bgo[i], i == 10);

            using (SharpDX.Direct2D1.Brush frame = Dx(PurpleColor, 0.6f))
                RenderTarget.DrawRectangle(new SharpDX.RectangleF(px, py, w, h), frame, 1f);
        }
        #endregion

        #region Properties
        [XmlIgnore]
        [Display(Name = "Positive (up) color", GroupName = "1. Colors", Order = 0)]
        public Brush PurpleColor { get; set; }
        [Browsable(false)]
        public string PurpleColorSerialize { get { return Serialize.BrushToString(PurpleColor); } set { PurpleColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Neutral color", GroupName = "1. Colors", Order = 1)]
        public Brush GrayColor { get; set; }
        [Browsable(false)]
        public string GrayColorSerialize { get { return Serialize.BrushToString(GrayColor); } set { GrayColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Negative (down) color", GroupName = "1. Colors", Order = 2)]
        public Brush RedColor { get; set; }
        [Browsable(false)]
        public string RedColorSerialize { get { return Serialize.BrushToString(RedColor); } set { RedColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Trigger color (set also in Plots)", GroupName = "1. Colors", Order = 3)]
        public Brush GoldColor { get; set; }
        [Browsable(false)]
        public string GoldColorSerialize { get { return Serialize.BrushToString(GoldColor); } set { GoldColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "RSI period", GroupName = "2. RSI & DSS", Order = 0)]
        public int RsiLen { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "RSI smoothing", GroupName = "2. RSI & DSS", Order = 1)]
        public int RsiSmoothLen { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "DSS PDS", GroupName = "2. RSI & DSS", Order = 2)]
        public int Pds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "DSS EMA", GroupName = "2. RSI & DSS", Order = 3)]
        public int EmaLen { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Trigger length", GroupName = "2. RSI & DSS", Order = 4)]
        public int TriggerLen { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "DSS smooth", GroupName = "2. RSI & DSS", Order = 5)]
        public int DssSmooth { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Trigger smooth", GroupName = "2. RSI & DSS", Order = 6)]
        public int TriggerSmooth { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Overbought", GroupName = "2. RSI & DSS", Order = 7)]
        public int Overbought { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Oversold", GroupName = "2. RSI & DSS", Order = 8)]
        public int Oversold { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1440)]
        [Display(Name = "Timeframe 1 (minutes)", GroupName = "3. MTF Panel", Order = 0)]
        public int Tf1 { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1440)]
        [Display(Name = "Timeframe 2 (minutes)", GroupName = "3. MTF Panel", Order = 1)]
        public int Tf2 { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1440)]
        [Display(Name = "Timeframe 3 (minutes)", GroupName = "3. MTF Panel", Order = 2)]
        public int Tf3 { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1440)]
        [Display(Name = "Timeframe 4 (minutes)", GroupName = "3. MTF Panel", Order = 3)]
        public int Tf4 { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1440)]
        [Display(Name = "Timeframe 5 (minutes)", GroupName = "3. MTF Panel", Order = 4)]
        public int Tf5 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use ADX trend filter (ADX > 20)", GroupName = "3. MTF Panel", Order = 5)]
        public bool UseAdx { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "MACD fast", GroupName = "3. MTF Panel", Order = 6)]
        public int MacdFast { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "MACD slow", GroupName = "3. MTF Panel", Order = 7)]
        public int MacdSlow { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "MACD signal", GroupName = "3. MTF Panel", Order = 8)]
        public int MacdSignal { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Fast MA (EMA)", GroupName = "3. MTF Panel", Order = 9)]
        public int PanelFastMA { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Slow MA (EMA)", GroupName = "3. MTF Panel", Order = 10)]
        public int PanelSlowMA { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show panel", GroupName = "4. Panel position", Order = 0)]
        public bool ShowPanel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Panel position", GroupName = "4. Panel position", Order = 1)]
        public SriPanelPos PanelPos { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable NinjaTrader alerts (live bars)", GroupName = "5. Alerts", Order = 0)]
        public bool EnableAlerts { get; set; }
        #endregion
    }
}
