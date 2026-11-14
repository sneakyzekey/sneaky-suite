#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.ComponentModel.DataAnnotations;
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

// SneakyTrendlinesHoldRate -- NinjaTrader 8 port of the TradingView script
// "Trendlines with Breakouts, Touches & Hold Rate [LunqFX]" (Pine v6).
//  * auto trendlines through confirmed swing highs / lows (best-touch pair wins)
//  * lines die when price closes through them; touches counted; breaks and retests marked
//  * scoreboard: how often a touch held, a break followed through, and a break was retested
//  * optional neon / glow candles
// Everything is decided on CLOSED bars (no repaint).
namespace NinjaTrader.NinjaScript.Indicators
{
    public enum TlLabelSize { Small, Normal, Large, Huge }
    public enum TlTextSize { Small, Normal, Large }
    public enum TlPanelPos { BottomCenter, TopCenter, BottomRight, TopRight }

    public class SneakyTrendlinesHoldRate : Indicator
    {
        #region Types
        private class TL
        {
            public int Id;
            public int Side;            // 1 resistance, -1 support
            public double P1;
            public int B1;
            public double Slope;
            public int Touches;
            public int LastTouch;
            public bool Drawn;
        }

        private class Ev
        {
            public int Kind;            // 1 touch, 2 break
            public int Side;
            public double P1;
            public int B1;
            public double Slope;
            public int Bar;
            public bool Retested, Judged;
            // recorder fields
            public int LineId, Touches, RetestBar = -1, OutcomeBar = -1;
            public DateTime Time, AnchorTime;
            public double Atr, LineVal, EvClose, Mfe, Mae;
            public string Outcome = "";
        }
        #endregion

        #region Fields
        private int off;
        private int lastProcessed = -1;
        private ATR atrI;
        private readonly List<double> hiP = new List<double>();
        private readonly List<int> hiB = new List<int>();
        private readonly List<double> loP = new List<double>();
        private readonly List<int> loB = new List<int>();
        private readonly List<TL> alive = new List<TL>();
        private readonly List<Ev> events = new List<Ev>();
        private readonly Queue<string> deadTags = new Queue<string>();
        private int idCounter, labelCounter;

        private int tHeld, tFail, bFoll, bFail, bRet, bN, touchAtBreak;
        private bool evNew, evBrkUp, evBrkDn, evTouch;

        // theme colours
        private Brush SUP, RES, GREYB, TXT, MUTE, PANEL, ROW, INK;

        // scoreboard strings (written on the data thread, read in OnRender)
        private string[] tileLbl = new string[6], tileBig = new string[6], tileCap = new string[6];
        private Brush[] tileAccent = new Brush[6], tileBigCol = new Brush[6];
        private bool scoreReady;
        private StreamWriter rec;
        private Series<double> sigBreak, sigRetest, sigTouch, sigBrkLvl, sigRetLvl, sigTchLvl, sigAtr;
        private int recId;
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Auto trendlines with breakouts, touches, retests and a hold-rate scoreboard (port of LunqFX).";
                Name = "SneakyTrendlinesHoldRate";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                IsAutoScale = false;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                IsSuspendedWhileInactive = true;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;

                PivLen = 8; MaxPer = 3; LookPiv = 6;
                TolAtr = 0.25; BrkAtr = 0.10; MinLen = 10; MinTch = 3;
                MoveAtr = 1.0; EvalBar = 20;
                ShowGlow = true; ExtR = 15; KeepBrk = 2;
                ShowEv = true; ShowTch = true; LblSize = TlLabelSize.Large;
                ShowTbl = true; TblPos = TlPanelPos.BottomCenter; TblSize = TlTextSize.Normal;
                AlBrk = true; AlNew = false; AlTch = false;
                NeonCandles = true; GlowCandles = true;
                LightTheme = false;
                DrawBarsBack = 3000;
                LabelDist = 30;
                RecordEvents = false;
                ShowBreakSignals = true;
                ShowRetestSignals = true;
                ShowTouchSignals = false;
                MarkerOffset = 1.0;
                AddPlot(new Stroke(Brushes.DeepSkyBlue, 2), PlotStyle.TriangleUp, "Break Up");        // 0  long breakout
                AddPlot(new Stroke(Brushes.Magenta, 2), PlotStyle.TriangleDown, "Break Down");        // 1  short breakout
                AddPlot(new Stroke(Brushes.DeepSkyBlue, 2), PlotStyle.Square, "Retest Long");         // 2  retest of broken resistance
                AddPlot(new Stroke(Brushes.Magenta, 2), PlotStyle.Square, "Retest Short");            // 3  retest of broken support
                AddPlot(new Stroke(Brushes.DeepSkyBlue, 2), PlotStyle.Dot, "Touch Long");             // 4  touch of support
                AddPlot(new Stroke(Brushes.Magenta, 2), PlotStyle.Dot, "Touch Short");                // 5  touch of resistance
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Break Signal");   // 6  +1 / -1 for signal panels
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Retest Signal");  // 7
                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "Touch Signal");   // 8
                RecordFolder = "SneakyExport";
            }
            else if (State == State.DataLoaded)
            {
                atrI = ATR(14);
                sigBreak = new Series<double>(this); sigRetest = new Series<double>(this); sigTouch = new Series<double>(this);
                sigBrkLvl = new Series<double>(this); sigRetLvl = new Series<double>(this); sigTchLvl = new Series<double>(this);
                sigAtr = new Series<double>(this);
                off = Calculate == Calculate.OnBarClose ? 0 : 1;

                if (LightTheme)
                {
                    SUP = Frozen(0x02, 0x84, 0xC7); RES = Frozen(0xA2, 0x1C, 0xAF); GREYB = Frozen(0x8A, 0x8F, 0x9C);
                    TXT = Frozen(0x1A, 0x1D, 0x24); MUTE = Frozen(0x5E, 0x64, 0x72); PANEL = Frozen(0xFF, 0xFF, 0xFF); ROW = Frozen(0xEE, 0xF1, 0xF6);
                }
                else
                {
                    SUP = Frozen(0x38, 0xBD, 0xF8); RES = Frozen(0xD9, 0x46, 0xEF); GREYB = Frozen(0x6B, 0x70, 0x80);
                    TXT = Frozen(0xE8, 0xEA, 0xF0); MUTE = Frozen(0x8F, 0x96, 0xA6); PANEL = Frozen(0x0A, 0x0C, 0x12); ROW = Frozen(0x14, 0x18, 0x26);
                }
                INK = Frozen(0x0A, 0x0C, 0x12);
                OpenRecorder();
            }
            else if (State == State.Terminated)
            {
                CloseRecorder();
            }
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            SolidColorBrush br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        private static Brush Alpha(Brush b, double opacity)
        {
            SolidColorBrush sb = b as SolidColorBrush;
            SolidColorBrush nb = new SolidColorBrush(sb != null ? sb.Color : Colors.Gray);
            nb.Opacity = Math.Max(0.0, Math.Min(1.0, opacity));
            nb.Freeze();
            return nb;
        }
        #endregion

        #region Event recorder (CSV for the Python backtest)
        private void OpenRecorder()
        {
            if (!RecordEvents) return;
            try
            {
                string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, RecordFolder);
                Directory.CreateDirectory(dir);
                string name = "tl_events_" + Instrument.FullName.Replace(" ", "_") + "_" + BarsPeriod.Value + "_" + BarsPeriod.BarsPeriodType + ".csv";
                foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
                rec = new StreamWriter(new FileStream(Path.Combine(dir, name), FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                rec.WriteLine("event_id,kind,side,event_time,event_bar,line_id,anchor_time,anchor_price,slope_per_bar,line_value,touches,atr,event_close,outcome,outcome_bar,bars_to_outcome,retested,retest_bar,mfe_atr,mae_atr");
                rec.Flush();
            }
            catch (Exception ex)
            {
                Print("SneakyTrendlinesHoldRate recorder: " + ex.Message);
                rec = null;
            }
        }

        private void CloseRecorder()
        {
            try { if (rec != null) { rec.Flush(); rec.Close(); } } catch { }
            rec = null;
        }

        private static string F(double v) { return v.ToString("0.#####", CultureInfo.InvariantCulture); }
        private static string T(DateTime t) { return t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }

        // one row per resolved event (touch / break), plus one per new line
        private void WriteEvent(Ev e)
        {
            if (rec == null) return;
            try
            {
                int btw = e.OutcomeBar >= 0 ? e.OutcomeBar - e.Bar : -1;
                rec.WriteLine(string.Join(",", new string[] {
                    (recId++).ToString(CultureInfo.InvariantCulture),
                    e.Kind == 1 ? "touch" : e.Kind == 2 ? "break" : "line_new",
                    e.Side == 1 ? "resistance" : "support",
                    T(e.Time), e.Bar.ToString(CultureInfo.InvariantCulture), e.LineId.ToString(CultureInfo.InvariantCulture),
                    T(e.AnchorTime), F(e.P1), F(e.Slope), F(e.LineVal), e.Touches.ToString(CultureInfo.InvariantCulture),
                    F(e.Atr), F(e.EvClose), e.Outcome, e.OutcomeBar.ToString(CultureInfo.InvariantCulture),
                    btw.ToString(CultureInfo.InvariantCulture), e.Retested ? "1" : "0", e.RetestBar.ToString(CultureInfo.InvariantCulture),
                    F(e.Mfe), F(e.Mae) }));
                rec.Flush();
            }
            catch { }
        }
        #endregion

        #region Helpers
        private static double LineAt(double p1, int b1, double slope, int x) { return p1 + slope * (x - b1); }
        private int BA(int absBar) { return CurrentBar - absBar; }
        private Brush ColOf(int side) { return side == 1 ? RES : SUP; }

        private bool CanDraw() { return State == State.Realtime || CurrentBar >= Count - DrawBarsBack; }
        private bool IsLatest() { return State == State.Realtime || CurrentBar >= Count - 2; }

        private SimpleFont LabelFont()
        {
            switch (LblSize)
            {
                case TlLabelSize.Small: return new SimpleFont("Arial", 9);
                case TlLabelSize.Normal: return new SimpleFont("Arial", 11);
                case TlLabelSize.Large: return new SimpleFont("Arial", 13);
                default: return new SimpleFont("Arial", 16);
            }
        }

        private SimpleFont RetestFont()
        {
            switch (LblSize)
            {
                case TlLabelSize.Small: return new SimpleFont("Arial", 8);
                case TlLabelSize.Normal: return new SimpleFont("Arial", 9);
                case TlLabelSize.Large: return new SimpleFont("Arial", 11);
                default: return new SimpleFont("Arial", 13);
            }
        }

        private static string Pct(int a, int b)
        {
            return b > 0 ? ((int)Math.Round(a * 100.0 / b)) + "%" : "—";
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 2)
                return;

            // candle colouring (price-bar brushes)
            if (NeonCandles)
            {
                Brush edge = Close[0] >= Open[0] ? SUP : RES;
                if (GlowCandles)
                {
                    BarBrush = Brushes.Transparent;
                    CandleOutlineBrush = Brushes.Transparent;
                }
                else
                {
                    BarBrush = Alpha(edge, 0.76);
                    CandleOutlineBrush = edge;
                }
            }

            if (Calculate == Calculate.OnBarClose)
                ProcessClosedBar();
            else if (IsFirstTickOfBar)
                ProcessClosedBar();

            if (IsLatest())
            {
                DrawAlive();
                UpdateScoreboard();
            }
        }
        #endregion

        #region Closed-bar logic
        private void ProcessClosedBar()
        {
            int idx = CurrentBar - off;
            if (idx < 1 || idx <= lastProcessed) return;
            lastProcessed = idx;

            evNew = evBrkUp = evBrkDn = evTouch = false;
            sigBreak[off] = 0; sigRetest[off] = 0; sigTouch[off] = 0;
            Values[6][off] = 0; Values[7][off] = 0; Values[8][off] = 0;
            sigBrkLvl[off] = 0; sigRetLvl[off] = 0; sigTchLvl[off] = 0; sigAtr[off] = 0;

            double atr = atrI[off];
            if (atr <= 0) return;
            double h = High[off], l = Low[off], c = Close[off];

            // ---- pivots (confirmed PivLen bars later)
            if (idx >= 2 * PivLen)
            {
                double ph = double.NaN, pl = double.NaN;
                double hp = High[off + PivLen], lp = Low[off + PivLen];
                bool isH = true, isL = true;
                for (int k = 0; k <= 2 * PivLen; k++)
                {
                    if (k == PivLen) continue;
                    double hk = High[off + k], lk = Low[off + k];
                    if (k < PivLen) { if (hk >= hp) isH = false; if (lk <= lp) isL = false; }       // newer side: strict
                    else { if (hk > hp) isH = false; if (lk < lp) isL = false; }                      // older side: ties allowed
                }
                if (isH) ph = hp;
                if (isL) pl = lp;
                int pB = idx - PivLen;

                for (int si = 0; si < 2; si++)
                {
                    int side = si == 0 ? 1 : -1;
                    bool isPiv = side == 1 ? !double.IsNaN(ph) : !double.IsNaN(pl);
                    if (!isPiv) continue;
                    double p2 = side == 1 ? ph : pl;

                    int bestT; double bestP, bestS; int bestB;
                    TryLine(side, p2, pB, idx, atr, out bestT, out bestP, out bestB, out bestS);
                    if (bestT >= MinTch && !SameAs(side, bestP, bestB, bestS, bestT, idx, atr))
                    {
                        alive.Add(new TL { Id = idCounter++, Side = side, P1 = bestP, B1 = bestB, Slope = bestS, Touches = bestT, LastTouch = idx });
                        evNew = true;
                        if (rec != null)
                        {
                            Ev ln = new Ev { Kind = 3, Side = side, P1 = bestP, B1 = bestB, Slope = bestS, Bar = idx, LineId = alive[alive.Count - 1].Id,
                                Touches = bestT, Time = Time[off], AnchorTime = Time[CurrentBar - bestB], Atr = atr,
                                LineVal = LineAt(bestP, bestB, bestS, idx), EvClose = c, Outcome = "new", OutcomeBar = idx };
                            WriteEvent(ln);
                        }
                        int cnt = 0;
                        foreach (TL t in alive) if (t.Side == side) cnt++;
                        if (cnt > MaxPer)
                        {
                            for (int i = 0; i < alive.Count; i++)
                            {
                                if (alive[i].Side == side)
                                {
                                    RemoveLineDrawings(alive[i]);
                                    alive.RemoveAt(i);
                                    break;
                                }
                            }
                        }
                    }

                    if (side == 1)
                    {
                        hiP.Add(p2); hiB.Add(pB);
                        if (hiP.Count > 30) { hiP.RemoveAt(0); hiB.RemoveAt(0); }
                    }
                    else
                    {
                        loP.Add(p2); loB.Add(pB);
                        if (loP.Count > 30) { loP.RemoveAt(0); loB.RemoveAt(0); }
                    }
                }
            }

            // ---- live lines: touches and breaks
            double tol = TolAtr * atr;
            int ii = 0;
            while (ii < alive.Count)
            {
                TL t = alive[ii];
                double v = LineAt(t.P1, t.B1, t.Slope, idx);
                bool broke = t.Side == 1 ? c > v + BrkAtr * atr : c < v - BrkAtr * atr;
                if (broke)
                {
                    bN++;
                    touchAtBreak += t.Touches;
                    events.Add(new Ev { Kind = 2, Side = t.Side, P1 = t.P1, B1 = t.B1, Slope = t.Slope, Bar = idx,
                        LineId = t.Id, Touches = t.Touches, Time = Time[off], AnchorTime = Time[CurrentBar - t.B1], Atr = atr, LineVal = v, EvClose = c });
                    if (t.Side == 1) evBrkUp = true; else evBrkDn = true;
                    sigBreak[off] = t.Side == 1 ? 1 : -1;
                    Values[6][off] = sigBreak[off];
                    sigBrkLvl[off] = v;
                    sigAtr[off] = atr;
                    if (ShowBreakSignals)
                    {
                        if (t.Side == 1) Values[0][off] = l - MarkerOffset * atr;
                        else Values[1][off] = h + MarkerOffset * atr;
                    }

                    if (ShowEv && CanDraw())
                    {
                        string tag = "TLB" + (labelCounter++);
                        Brush col = ColOf(t.Side);
                        Draw.Text(this, tag, false, t.Side == 1 ? "▲ BREAK" : "▼ BREAK", off, t.Side == 1 ? MIN(Low, 8)[off] : MAX(High, 8)[off],
                            t.Side == 1 ? LabelDist : -LabelDist, t.Side == 1 ? Brushes.White : INK, LabelFont(), TextAlignment.Center,
                            col, col, 90);
                    }
                    KillLine(t, idx);
                    alive.RemoveAt(ii);
                }
                else
                {
                    bool touched = t.Side == 1 ? h >= v - tol : l <= v + tol;
                    if (touched && idx - t.LastTouch > PivLen)
                    {
                        t.Touches++;
                        t.LastTouch = idx;
                        evTouch = true;
                        sigTouch[off] = t.Side == 1 ? -1 : 1;
                        Values[8][off] = sigTouch[off];      // touch of resistance = short, touch of support = long
                        sigTchLvl[off] = v;
                        sigAtr[off] = atr;
                        if (ShowTouchSignals)
                        {
                            if (t.Side == -1) Values[4][off] = l - (MarkerOffset + 1.4) * atr;
                            else Values[5][off] = h + (MarkerOffset + 1.4) * atr;
                        }
                        events.Add(new Ev { Kind = 1, Side = t.Side, P1 = t.P1, B1 = t.B1, Slope = t.Slope, Bar = idx,
                            LineId = t.Id, Touches = t.Touches, Time = Time[off], AnchorTime = Time[CurrentBar - t.B1], Atr = atr, LineVal = v, EvClose = c });
                    }
                    ii++;
                }
            }

            // ---- judge touches and breaks
            int j = 0;
            while (j < events.Count)
            {
                Ev e = events[j];
                double v = LineAt(e.P1, e.B1, e.Slope, idx);
                int age = idx - e.Bar;
                bool done = false;
                {
                    double fav, adv;
                    if (e.Kind == 1) { fav = e.Side == 1 ? v - l : h - v; adv = e.Side == 1 ? h - v : v - l; }
                    else { fav = e.Side == 1 ? h - v : v - l; adv = e.Side == 1 ? v - l : h - v; }
                    if (e.Atr > 0) { e.Mfe = Math.Max(e.Mfe, fav / e.Atr); e.Mae = Math.Max(e.Mae, adv / e.Atr); }
                }
                if (e.Kind == 1)
                {
                    bool away = e.Side == 1 ? v - l >= MoveAtr * atr : h - v >= MoveAtr * atr;
                    bool through = e.Side == 1 ? c > v + BrkAtr * atr : c < v - BrkAtr * atr;
                    if (through) { tFail++; done = true; e.Outcome = "failed"; }
                    else if (away) { tHeld++; done = true; e.Outcome = "held"; }
                    else if (age > EvalBar) { done = true; e.Outcome = "expired"; }
                }
                else
                {
                    bool beyond = e.Side == 1 ? h - v >= MoveAtr * atr : v - l >= MoveAtr * atr;
                    bool back = e.Side == 1 ? c < v - BrkAtr * atr : c > v + BrkAtr * atr;
                    bool near = e.Side == 1 ? l <= v + tol : h >= v - tol;
                    if (!e.Judged)
                    {
                        if (back) { bFail++; e.Judged = true; e.Outcome = "failed"; e.OutcomeBar = idx; }
                        else if (beyond && age > 0) { bFoll++; e.Judged = true; e.Outcome = "followed"; e.OutcomeBar = idx; }
                    }
                    if (age > 1 && near && !back && !e.Retested)
                    {
                        e.Retested = true;
                        e.RetestBar = idx;
                        sigRetest[off] = e.Side == 1 ? 1 : -1;
                        Values[7][off] = sigRetest[off];     // retest of a broken resistance = long, of broken support = short
                        sigRetLvl[off] = v;
                        sigAtr[off] = atr;
                        if (ShowRetestSignals)
                        {
                            if (e.Side == 1) Values[2][off] = l - (MarkerOffset + 0.7) * atr;
                            else Values[3][off] = h + (MarkerOffset + 0.7) * atr;
                        }
                        bRet++;
                        if (ShowEv && CanDraw())
                        {
                            string tag = "TLR" + (labelCounter++);
                            Draw.Text(this, tag, false, "RETEST", off, e.Side == 1 ? MIN(Low, 8)[off] : MAX(High, 8)[off], e.Side == 1 ? (int)(LabelDist * 2.6) : -(int)(LabelDist * 2.6),
                                ColOf(e.Side), RetestFont(), TextAlignment.Center, ColOf(e.Side), PANEL, 80);
                        }
                    }
                    if (back || age > EvalBar)
                    {
                        if (!e.Judged) { bFail++; e.Outcome = "expired"; e.OutcomeBar = idx; }
                        done = true;
                    }
                }
                if (done)
                {
                    if (e.Kind == 1) e.OutcomeBar = idx;
                    WriteEvent(e);
                    events.RemoveAt(j);
                }
                else j++;
            }

            // ---- alerts (live bars only)
            if (State == State.Realtime)
            {
                string snd = NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav";
                string tk = Instrument.MasterInstrument.Name;
                if (AlBrk && evBrkUp) Alert("TL_up_" + CurrentBar, Priority.High, tk + " broke above a descending trendline", snd, 1, Brushes.Black, Brushes.White);
                if (AlBrk && evBrkDn) Alert("TL_dn_" + CurrentBar, Priority.High, tk + " broke below an ascending trendline", snd, 1, Brushes.Black, Brushes.White);
                if (AlNew && evNew) Alert("TL_new_" + CurrentBar, Priority.Medium, tk + " new trendline drawn", snd, 1, Brushes.Black, Brushes.White);
                if (AlTch && evTouch) Alert("TL_tch_" + CurrentBar, Priority.Medium, tk + " touched a trendline", snd, 1, Brushes.Black, Brushes.White);
            }
        }

        // each new swing is paired with earlier swings on the same side; the pair with the most touches wins
        private void TryLine(int side, double p2, int b2, int idx, double atr,
            out int bestT, out double bestP, out int bestB, out double bestS)
        {
            List<double> ps = side == 1 ? hiP : loP;
            List<int> bs = side == 1 ? hiB : loB;
            int n = ps.Count;
            bestT = 0; bestP = 0; bestB = 0; bestS = 0;
            double tol = TolAtr * atr;
            if (n == 0) return;

            for (int k = n - 1; k >= Math.Max(0, n - LookPiv); k--)
            {
                double p1 = ps[k];
                int b1 = bs[k];
                bool okDir = side == 1 ? p2 < p1 : p2 > p1;
                if (!(okDir && b2 - b1 >= MinLen && idx - b1 < 480)) continue;

                double s = (p2 - p1) / (b2 - b1);
                bool valid = true;
                for (int x = b1 + 1; x <= idx; x++)
                {
                    double v = LineAt(p1, b1, s, x);
                    double cx = Close[CurrentBar - x];
                    if (side == 1 ? cx > v + tol : cx < v - tol) { valid = false; break; }
                }
                if (!valid) continue;

                int t = 2;
                for (int j = 0; j < n; j++)
                {
                    int bj = bs[j];
                    if (bj > b1 && bj < b2 && Math.Abs(ps[j] - LineAt(p1, b1, s, bj)) <= tol)
                        t++;
                }
                if (t > bestT) { bestT = t; bestP = p1; bestB = b1; bestS = s; }
            }
        }

        private bool SameAs(int side, double p1, int b1, double s, int touches, int idx, double atr)
        {
            bool dup = false;
            foreach (TL t in alive)
            {
                if (t.Side != side) continue;
                double gap = Math.Abs(LineAt(t.P1, t.B1, t.Slope, idx) - LineAt(p1, b1, s, idx));
                bool similar = Math.Abs(t.Slope - s) <= Math.Max(Math.Abs(t.Slope), Math.Abs(s)) * 0.35;
                if (gap <= TolAtr * atr || (similar && gap <= 0.75 * atr)) dup = true;
                if (t.B1 == b1 && t.Touches >= touches) dup = true;
            }
            return dup;
        }

        private void RemoveLineDrawings(TL t)
        {
            RemoveDrawObject("TLC" + t.Id);
            RemoveDrawObject("TLG" + t.Id);
            RemoveDrawObject("TLT" + t.Id);
        }

        private void KillLine(TL t, int at)
        {
            RemoveDrawObject("TLG" + t.Id);
            RemoveDrawObject("TLT" + t.Id);
            if (CanDraw())
            {
                string tag = "TLC" + t.Id;
                double y2 = LineAt(t.P1, t.B1, t.Slope, at);
                Draw.Line(this, tag, false, BA(t.B1), t.P1, off, y2, Alpha(ColOf(t.Side), 0.55), DashStyleHelper.Dash, 2);
                deadTags.Enqueue(tag);
                while (deadTags.Count > KeepBrk)
                    RemoveDrawObject(deadTags.Dequeue());
            }
            else
            {
                RemoveDrawObject("TLC" + t.Id);
            }
        }
        #endregion

        #region Drawing of live lines
        private void DrawAlive()
        {
            foreach (TL t in alive)
            {
                int x2 = -ExtR;                                   // barsAgo (negative = future)
                double y2 = LineAt(t.P1, t.B1, t.Slope, CurrentBar + ExtR);
                Brush col = ColOf(t.Side);
                if (ShowGlow)
                    Draw.Line(this, "TLG" + t.Id, false, BA(t.B1), t.P1, x2, y2, Alpha(col, 0.18), DashStyleHelper.Solid, 7);
                Draw.Line(this, "TLC" + t.Id, false, BA(t.B1), t.P1, x2, y2, col, DashStyleHelper.Solid, 2);
                if (ShowTch)
                    Draw.Text(this, "TLT" + t.Id, false, t.Touches + " touches", 0, LineAt(t.P1, t.B1, t.Slope, CurrentBar), t.Side == 1 ? 16 : -16, col, RetestFont(), TextAlignment.Right,
                        PANEL, PANEL, 80);
                t.Drawn = true;
            }
        }
        #endregion

        #region Scoreboard data
        private void UpdateScoreboard()
        {
            double atr = atrI[0];
            if (atr <= 0) return;
            double close = Close[0];
            int nR = 0, nS = 0, bestRT = 0, bestST = 0;
            double bestR = double.NaN, bestS = double.NaN;
            foreach (TL t in alive)
            {
                double v = LineAt(t.P1, t.B1, t.Slope, CurrentBar);
                if (t.Side == 1)
                {
                    nR++;
                    if (double.IsNaN(bestR) || Math.Abs(v - close) < Math.Abs(bestR - close)) { bestR = v; bestRT = t.Touches; }
                }
                else
                {
                    nS++;
                    if (double.IsNaN(bestS) || Math.Abs(v - close) < Math.Abs(bestS - close)) { bestS = v; bestST = t.Touches; }
                }
            }

            tileAccent[0] = SUP; tileLbl[0] = "TRENDLINES"; tileBig[0] = "▼" + nR + "  ▲" + nS; tileBigCol[0] = TXT; tileCap[0] = "live lines";
            tileAccent[1] = RES; tileLbl[1] = "RESISTANCE";
            tileBig[1] = double.IsNaN(bestR) ? "—" : "+" + (Math.Abs(bestR - close) / atr).ToString("0.0") + " ATR"; tileBigCol[1] = RES;
            tileCap[1] = double.IsNaN(bestR) ? "no live line" : bestRT + " touches";
            tileAccent[2] = SUP; tileLbl[2] = "SUPPORT";
            tileBig[2] = double.IsNaN(bestS) ? "—" : "−" + (Math.Abs(close - bestS) / atr).ToString("0.0") + " ATR"; tileBigCol[2] = SUP;
            tileCap[2] = double.IsNaN(bestS) ? "no live line" : bestST + " touches";
            tileAccent[3] = TXT; tileLbl[3] = "TOUCH HELD"; tileBig[3] = Pct(tHeld, tHeld + tFail); tileBigCol[3] = TXT; tileCap[3] = "of " + (tHeld + tFail) + " touches";
            tileAccent[4] = TXT; tileLbl[4] = "BREAK FOLLOWED"; tileBig[4] = Pct(bFoll, bFoll + bFail); tileBigCol[4] = TXT; tileCap[4] = "of " + bN + " breaks";
            tileAccent[5] = TXT; tileLbl[5] = "RETESTED"; tileBig[5] = Pct(bRet, bN); tileBigCol[5] = TXT;
            tileCap[5] = "avg " + (bN > 0 ? (touchAtBreak * 1.0 / bN).ToString("0.0") : "—") + " touches / break";
            scoreReady = true;
        }
        #endregion

        #region Rendering (glow candles + scoreboard)
        private SharpDX.Direct2D1.Brush Dx(Brush b, float opacity)
        {
            SharpDX.Direct2D1.Brush d = b.ToDxBrush(RenderTarget);
            d.Opacity = opacity;
            return d;
        }

        private void Text(string text, float x, float y, float w, float h, float size, Brush col)
        {
            using (SharpDX.Direct2D1.Brush tb = Dx(col, 1f))
            using (SharpDX.DirectWrite.TextFormat fmt = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
                SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, size))
            using (SharpDX.DirectWrite.TextLayout lay = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, text, fmt, w, h))
            {
                lay.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, y), lay, tb, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
            }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (Bars == null || chartControl == null || ChartBars == null || RenderTarget == null)
                return;

            // ---- glow candles
            if (NeonCandles && GlowCandles)
            {
                Brush bg = chartControl.Properties.ChartBackground ?? Brushes.Black;
                float bw = (float)Math.Max(2.0, chartControl.BarWidth * 2.0);
                using (SharpDX.Direct2D1.Brush mask = Dx(bg, 1f))
                {
                    for (int i = ChartBars.FromIndex; i <= ChartBars.ToIndex; i++)
                    {
                        double o = Bars.GetOpen(i), h = Bars.GetHigh(i), l = Bars.GetLow(i), c = Bars.GetClose(i);
                        float x = chartControl.GetXByBarIndex(ChartBars, i);
                        float yH = chartScale.GetYByValue(h), yL = chartScale.GetYByValue(l);
                        float yO = chartScale.GetYByValue(o), yC = chartScale.GetYByValue(c);
                        Brush edge = c >= o ? SUP : RES;

                        using (SharpDX.Direct2D1.Brush wick = Dx(edge, 0.9f))
                            RenderTarget.DrawLine(new SharpDX.Vector2(x, yH), new SharpDX.Vector2(x, yL), wick, 1f);

                        float left = x - bw / 2f;
                        float top = Math.Min(yO, yC), hgt = Math.Max(1f, Math.Abs(yC - yO));
                        RenderTarget.FillRectangle(new SharpDX.RectangleF(left, top, bw, hgt), mask);

                        // four bands brightening toward the close
                        float[] op = { 0.22f, 0.42f, 0.64f, 0.92f };
                        float q = (yC - yO) / 4f;
                        for (int b = 0; b < 4; b++)
                        {
                            float y1 = yO + q * b, y2 = yO + q * (b + 1);
                            using (SharpDX.Direct2D1.Brush band = Dx(edge, op[b]))
                                RenderTarget.FillRectangle(new SharpDX.RectangleF(left, Math.Min(y1, y2), bw, Math.Max(1f, Math.Abs(y2 - y1))), band);
                        }
                        using (SharpDX.Direct2D1.Brush outline = Dx(edge, 0.7f))
                            RenderTarget.DrawRectangle(new SharpDX.RectangleF(left, top, bw, hgt), outline, 1f);
                    }
                }
            }

            // ---- scoreboard
            if (!ShowTbl || !scoreReady) return;

            float k = TblSize == TlTextSize.Small ? 0.85f : TblSize == TlTextSize.Large ? 1.2f : 1f;
            float tw = 128f * k, stripH = 5f, lblH = 16f * k, bigH = 28f * k, capH = 16f * k;
            float totalW = tw * 6f, totalH = stripH + lblH + bigH + capH;
            float margin = 10f;
            float px, py;
            if (TblPos == TlPanelPos.BottomCenter) { px = ChartPanel.X + (ChartPanel.W - totalW) / 2f; py = ChartPanel.Y + ChartPanel.H - totalH - margin; }
            else if (TblPos == TlPanelPos.TopCenter) { px = ChartPanel.X + (ChartPanel.W - totalW) / 2f; py = ChartPanel.Y + margin; }
            else if (TblPos == TlPanelPos.BottomRight) { px = ChartPanel.X + ChartPanel.W - totalW - margin; py = ChartPanel.Y + ChartPanel.H - totalH - margin; }
            else { px = ChartPanel.X + ChartPanel.W - totalW - margin; py = ChartPanel.Y + margin; }

            using (SharpDX.Direct2D1.Brush back = Dx(PANEL, 0.92f))
                RenderTarget.FillRectangle(new SharpDX.RectangleF(px - 4, py - 4, totalW + 8, totalH + 8), back);

            for (int i = 0; i < 6; i++)
            {
                float x = px + i * tw;
                float w = tw - 4f;
                using (SharpDX.Direct2D1.Brush acc = Dx(tileAccent[i], 1f))
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(x, py, w, stripH), acc);
                using (SharpDX.Direct2D1.Brush row = Dx(ROW, 1f))
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(x, py + stripH, w, lblH + bigH + capH), row);
                Text(tileLbl[i], x, py + stripH + 1f, w, lblH, 10f * k, MUTE);
                Text(tileBig[i], x, py + stripH + lblH, w, bigH, 17f * k, tileBigCol[i]);
                Text(tileCap[i], x, py + stripH + lblH + bigH, w, capH, 9.5f * k, MUTE);
            }
        }
        #endregion

        #region Properties
        // ---- signal series for strategies (not shown as plots)
        [Browsable(false)] [XmlIgnore]
        public Series<double> BreakSignal { get { Update(); return sigBreak; } }       // +1 break up, -1 break down
        [Browsable(false)] [XmlIgnore]
        public Series<double> RetestSignal { get { Update(); return sigRetest; } }     // +1 retest of broken resistance (long), -1 retest of broken support (short)
        [Browsable(false)] [XmlIgnore]
        public Series<double> TouchSignal { get { Update(); return sigTouch; } }       // +1 touch of support (long), -1 touch of resistance (short)
        [Browsable(false)] [XmlIgnore]
        public Series<double> BreakLevel { get { Update(); return sigBrkLvl; } }       // line price at the break bar
        [Browsable(false)] [XmlIgnore]
        public Series<double> RetestLevel { get { Update(); return sigRetLvl; } }
        [Browsable(false)] [XmlIgnore]
        public Series<double> TouchLevel { get { Update(); return sigTchLvl; } }
        [Browsable(false)] [XmlIgnore]
        public Series<double> SignalAtr { get { Update(); return sigAtr; } }           // ATR(14) on the signal bar

        [NinjaScriptProperty]
        [Range(2, 50)]
        [Display(Name = "Swing length", Description = "Bars on each side that confirm a swing. Higher = fewer, longer trendlines.", GroupName = "1. Trendlines", Order = 0)]
        public int PivLen { get; set; }

        [NinjaScriptProperty]
        [Range(1, 6)]
        [Display(Name = "Active lines per side", GroupName = "1. Trendlines", Order = 1)]
        public int MaxPer { get; set; }

        [NinjaScriptProperty]
        [Range(2, 12)]
        [Display(Name = "Swings searched for an anchor", GroupName = "1. Trendlines", Order = 2)]
        public int LookPiv { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 2.0)]
        [Display(Name = "Touch tolerance (x ATR)", GroupName = "1. Trendlines", Order = 3)]
        public double TolAtr { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "Break buffer (x ATR)", GroupName = "1. Trendlines", Order = 4)]
        public double BrkAtr { get; set; }

        [NinjaScriptProperty]
        [Range(3, 200)]
        [Display(Name = "Min bars between anchors", GroupName = "1. Trendlines", Order = 5)]
        public int MinLen { get; set; }

        [NinjaScriptProperty]
        [Range(2, 6)]
        [Display(Name = "Min touches to draw a line", GroupName = "1. Trendlines", Order = 6)]
        public int MinTch { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 5.0)]
        [Display(Name = "Touch holds / break follows through after (x ATR)", GroupName = "2. Measurement", Order = 0)]
        public double MoveAtr { get; set; }

        [NinjaScriptProperty]
        [Range(5, 100)]
        [Display(Name = "Bars to judge a touch or a break", GroupName = "2. Measurement", Order = 1)]
        public int EvalBar { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Line glow", GroupName = "3. Drawing", Order = 0)]
        public bool ShowGlow { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Extend lines right (bars)", GroupName = "3. Drawing", Order = 1)]
        public int ExtR { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Broken lines kept", GroupName = "3. Drawing", Order = 2)]
        public int KeepBrk { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Break and retest labels", GroupName = "3. Drawing", Order = 3)]
        public bool ShowEv { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Touch count on each line", GroupName = "3. Drawing", Order = 4)]
        public bool ShowTch { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Label size", GroupName = "3. Drawing", Order = 5)]
        public TlLabelSize LblSize { get; set; }

        [NinjaScriptProperty]
        [Range(5, 150)]
        [Display(Name = "Label distance from the bar (pixels; retest labels sit further out)", GroupName = "3. Drawing", Order = 7)]
        public int LabelDist { get; set; }

        [NinjaScriptProperty]
        [Range(100, 100000)]
        [Display(Name = "Create break/retest labels only on last N bars (load speed)", GroupName = "3. Drawing", Order = 6)]
        public int DrawBarsBack { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show panel", GroupName = "4. Panel", Order = 0)]
        public bool ShowTbl { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Position", GroupName = "4. Panel", Order = 1)]
        public TlPanelPos TblPos { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Text size", GroupName = "4. Panel", Order = 2)]
        public TlTextSize TblSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: trendline break", GroupName = "5. Alerts", Order = 0)]
        public bool AlBrk { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: new trendline", GroupName = "5. Alerts", Order = 1)]
        public bool AlNew { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert: trendline touch", GroupName = "5. Alerts", Order = 2)]
        public bool AlTch { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Neon candles", GroupName = "6. Visuals", Order = 0)]
        public bool NeonCandles { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Glow candles", GroupName = "6. Visuals", Order = 1)]
        public bool GlowCandles { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show break arrows", GroupName = "7. Signals", Order = 0)]
        public bool ShowBreakSignals { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show retest squares", GroupName = "7. Signals", Order = 1)]
        public bool ShowRetestSignals { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show touch dots", GroupName = "7. Signals", Order = 2)]
        public bool ShowTouchSignals { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name = "Marker offset from the bar (x ATR)", GroupName = "7. Signals", Order = 3)]
        public double MarkerOffset { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Record every touch / break / retest to CSV", GroupName = "8. Recorder", Order = 0)]
        public bool RecordEvents { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Folder (under Documents\\NinjaTrader 8)", GroupName = "8. Recorder", Order = 1)]
        public string RecordFolder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Light theme (use on a light chart background)", GroupName = "6. Visuals", Order = 2)]
        public bool LightTheme { get; set; }
        #endregion
    }
}
