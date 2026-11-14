#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
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

// FootPrint PSMC VWAP -- NinjaTrader 8 port of the TradingView script
// "FootPrint PSMC VWAP 2027 [TrinityCapital]" (Pine v6).
//   1) VWAP + 3 SD bands (+ fills), anchored VWAP, cumulative VWAP, VWAP label
//   2) Supply/Demand zones (streak + slope) with a footprint-style volume profile + POC lines.
//      The profile is built from REAL bid/ask trade data (tick replay / live) when available.
//   3) Smart-money structure: internal + swing BOS / CHoCH, HH/HL/LH/LL, EQH/EQL,
//      strong/weak high/low, premium/equilibrium/discount, prior daily/weekly/monthly H/L, alerts.
// Zone and structure logic is evaluated on CLOSED bars only (no repaint).
namespace NinjaTrader.NinjaScript.Indicators
{
    public enum PsmcResetMode { Session, Daily, Weekly, Monthly, Yearly, Cumulative, Anchor }
    public enum PsmcPriceSource { HLC3, HL2, OHLC4, Close }
    public enum PsmcZoneStyle { Wick, Body, Candle }
    public enum PsmcProfileType { Volume, Time, Raw }
    public enum PsmcStructFilter { All, BOS, CHoCH }
    public enum PsmcLabelSize { Tiny, Small, Normal }

    public class SneakyFootprintPSMCVWAP : Indicator
    {
        #region Private types
        private class Pivot
        {
            public double Cur = double.NaN;
            public double Last = double.NaN;
            public bool Crossed;
            public int Bar;
            public DateTime Time;
        }

        private class Engine
        {
            public int Len;
            public bool Internal;
            public bool Equal;
            public int Leg;
            public int Bias;
            public Pivot High = new Pivot();
            public Pivot Low = new Pivot();
        }

        private class Zone
        {
            public string Tag;
            public List<string> Tags = new List<string>();
            public double Top;
            public double Bottom;
            public int Left;
            public int Start;
            public bool Supply;
        }
        #endregion

        #region Fields
        private Series<double> sPV, sV, sPPV, aPV, aV, aPPV, cPV, cV;
        private DateTime anchor1, anchor2;

        // tick store
        private readonly object tickLock = new object();
        private Dictionary<int, double[]> curTicks = new Dictionary<int, double[]>();
        private readonly Dictionary<int, Dictionary<int, double[]>> barData = new Dictionary<int, Dictionary<int, double[]>>();
        private double lastPx;

        // closed-bar processing
        private int lastProcessed = -1;
        private int off;                       // 0 = OnBarClose, 1 = intrabar calc
        private int greenCount, redCount;
        private bool supplyActive, demandActive;
        private double cumRange, atrEma;
        private readonly List<double> atrList = new List<double>();
        private readonly List<Zone> zones = new List<Zone>();
        private int zoneCounter;

        // SMC
        private Engine eSwing, eInt, eEq;
        private ATR atr200;
        private readonly Queue<string> structTags = new Queue<string>();
        private int structCounter;
        private double trTop = double.NaN, trBottom = double.NaN;
        private DateTime trBarTime, trLastTopTime, trLastBottomTime;
        private bool trHasTime;
        private bool aIntBullBOS, aIntBearBOS, aIntBullCH, aIntBearCH;
        private bool aSwBullBOS, aSwBearBOS, aSwBullCH, aSwBearCH, aEqH, aEqL;

        // higher-timeframe series indexes
        private int idxD = -1, idxW = -1, idxM = -1;

        private SimpleFont profFont;
        private Brush grayProf;
        private bool fillsDirty = true;
        #endregion

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "VWAP bands + footprint-style S/D profile + SMC structure (port of FootPrint PSMC VWAP 2027).";
                Name = "SneakyFootprintPSMCVWAP";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = true;
                DrawOnPricePanel = true;
                IsSuspendedWhileInactive = true;
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;

                // VWAP
                ResetMode = PsmcResetMode.Session;
                AnchorDate = "2026-01-01 00:00";
                PriceSource = PsmcPriceSource.Close;
                UseRealVolume = true;
                ShowVwap = true;
                TrendColoring = true;
                ShowBands = true;
                Sd1Mult = 1.0; Sd2Mult = 2.0; Sd3Mult = 3.0;
                ShowSd1 = true; ShowSd2 = true; ShowSd3 = true;
                FillSd1 = false; FillSd2 = false;
                ShowAnchored = false;
                AnchorDate2 = "2026-06-01 00:00";
                ShowCumulative = true;
                ShowVwapLabel = true;
                VwapBull = Brushes.DarkViolet;
                VwapBear = Brushes.Gainsboro;
                FillColor = Brushes.DeepSkyBlue;

                // Supply / demand + profile
                EnableZones = true;
                ShowProfile = true;
                ShowVolumeAtLevel = false;
                ProfileType = PsmcProfileType.Volume;
                ShowPoc = false;
                PocWidth = 2;
                ZoneStyle = PsmcZoneStyle.Wick;
                MinCount = 4;
                MinAngle = 69.0;
                MaxZoneAge = 5000;
                MaxZones = 20;
                HitDeletion = true;
                ExtendZones = true;
                ZoneOpacity = 25;
                ProfileBins = 12;
                ProfileMaxBars = 50;
                ProfileFilter = 2.0;
                SupplyColor = Brushes.Gray;
                DemandColor = Brushes.DarkViolet;
                ProfileBull = Brushes.DarkViolet;
                ProfileBear = Brushes.Red;

                // SMC internal
                ShowInternal = true;
                InternalBullFilter = PsmcStructFilter.All;
                InternalBearFilter = PsmcStructFilter.All;
                InternalBullColor = Brushes.DarkViolet;
                InternalBearColor = Brushes.Red;
                InternalSize = PsmcLabelSize.Tiny;
                InternalConfluence = false;
                InternalLength = 5;

                // SMC swing
                ShowSwing = true;
                SwingBullFilter = PsmcStructFilter.All;
                SwingBearFilter = PsmcStructFilter.All;
                SwingBullColor = Brushes.DarkViolet;
                SwingBearColor = Brushes.Red;
                SwingSize = PsmcLabelSize.Small;
                ShowSwingPoints = false;
                SwingLength = 50;
                ShowStrongWeak = false;
                MaxStructLabels = 150;

                // EQH / EQL
                ShowEqual = false;
                EqualLength = 3;
                EqualThreshold = 0.1;
                EqualSize = PsmcLabelSize.Tiny;

                // Premium / discount
                ShowZones = false;
                PremiumColor = Brushes.Crimson;
                EquilibriumColor = Brushes.Gray;
                DiscountColor = Brushes.SeaGreen;

                // HTF levels
                ShowDaily = false;
                ShowWeekly = false;
                ShowMonthly = false;
                HtfColor = Brushes.RoyalBlue;

                // Alerts
                EnableAlerts = true;

                AddPlot(new Stroke(Brushes.DarkViolet, 2), PlotStyle.Line, "VWAP");           // 0
                AddPlot(new Stroke(Brushes.Gainsboro, 1), PlotStyle.Line, "SD1 Upper");       // 1
                AddPlot(new Stroke(Brushes.DarkViolet, 1), PlotStyle.Line, "SD1 Lower");      // 2
                AddPlot(new Stroke(Brushes.Silver, 1), PlotStyle.Line, "SD2 Upper");          // 3
                AddPlot(new Stroke(Brushes.MediumOrchid, 1), PlotStyle.Line, "SD2 Lower");    // 4
                AddPlot(new Stroke(Brushes.White, 1), PlotStyle.Line, "SD3 Upper");           // 5
                AddPlot(new Stroke(Brushes.Orchid, 1), PlotStyle.Line, "SD3 Lower");          // 6
                AddPlot(new Stroke(Brushes.Gold, 2), PlotStyle.Line, "Anchored VWAP");        // 7
                AddPlot(new Stroke(Brushes.DeepSkyBlue, 1), PlotStyle.Line, "Cumulative VWAP"); // 8
            }
            else if (State == State.Configure)
            {
                int k = 1;
                if (ShowDaily) { AddDataSeries(BarsPeriodType.Day, 1); idxD = k++; }
                if (ShowWeekly) { AddDataSeries(BarsPeriodType.Week, 1); idxW = k++; }
                if (ShowMonthly) { AddDataSeries(BarsPeriodType.Month, 1); idxM = k++; }
            }
            else if (State == State.DataLoaded)
            {
                sPV = new Series<double>(this); sV = new Series<double>(this); sPPV = new Series<double>(this);
                aPV = new Series<double>(this); aV = new Series<double>(this); aPPV = new Series<double>(this);
                cPV = new Series<double>(this); cV = new Series<double>(this);

                if (!DateTime.TryParse(AnchorDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out anchor1))
                    anchor1 = DateTime.MaxValue;
                if (!DateTime.TryParse(AnchorDate2, CultureInfo.InvariantCulture, DateTimeStyles.None, out anchor2))
                    anchor2 = DateTime.MaxValue;

                off = Calculate == Calculate.OnBarClose ? 0 : 1;
                eSwing = new Engine { Len = Math.Max(2, SwingLength), Internal = false };
                eInt = new Engine { Len = Math.Max(2, InternalLength), Internal = true };
                eEq = new Engine { Len = Math.Max(1, EqualLength), Equal = true };
                atr200 = ATR(200);

                profFont = new SimpleFont("Consolas", 9);
                grayProf = MakeBrush(Brushes.Gray, 0.55);
            }
        }
        #endregion

        #region Tick capture (real bid/ask classification)
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last || e.Volume <= 0)
                return;

            bool buy;
            if (e.Ask > 0 && e.Price >= e.Ask) buy = true;
            else if (e.Bid > 0 && e.Price <= e.Bid) buy = false;
            else buy = e.Price >= lastPx;
            lastPx = e.Price;

            int lvl = (int)Math.Round(e.Price / TickSize);
            lock (tickLock)
            {
                double[] v;
                if (!curTicks.TryGetValue(lvl, out v))
                {
                    v = new double[2];
                    curTicks[lvl] = v;
                }
                if (buy) v[0] += e.Volume; else v[1] += e.Volume;
            }
        }

        private void RollTicks(int key)
        {
            lock (tickLock)
            {
                if (curTicks.Count > 0)
                {
                    barData[key] = new Dictionary<int, double[]>(curTicks);
                    curTicks.Clear();
                }
            }
            barData.Remove(key - 1500);
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            CalcVwap();
            if (CurrentBar < 1)
                return;

            if (Calculate == Calculate.OnBarClose)
            {
                RollTicks(CurrentBar);
                ProcessClosedBar();
            }
            else if (IsFirstTickOfBar)
            {
                RollTicks(CurrentBar - 1);
                ProcessClosedBar();
            }

            if (IsLatest())
            {
                DrawLatest();
                if ((State == State.Realtime && IsFirstTickOfBar) || fillsDirty)
                {
                    DrawFills();
                    fillsDirty = false;
                }
            }
        }

        private bool IsLatest()
        {
            return State == State.Realtime || CurrentBar >= Count - 2;
        }
        #endregion

        #region VWAP
        private double Src()
        {
            switch (PriceSource)
            {
                case PsmcPriceSource.HLC3: return (High[0] + Low[0] + Close[0]) / 3.0;
                case PsmcPriceSource.HL2: return (High[0] + Low[0]) / 2.0;
                case PsmcPriceSource.OHLC4: return (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;
                default: return Close[0];
            }
        }

        private bool VwapReset()
        {
            if (CurrentBar == 0) return true;
            DateTime t0 = Time[0], t1 = Time[1];
            switch (ResetMode)
            {
                case PsmcResetMode.Session: return Bars.IsFirstBarOfSession;
                case PsmcResetMode.Daily: return t0.Date != t1.Date;
                case PsmcResetMode.Weekly:
                    return (t0.Date - t1.Date).Days >= 7 || (t0.Date != t1.Date && t0.DayOfWeek <= t1.DayOfWeek);
                case PsmcResetMode.Monthly: return t0.Month != t1.Month || t0.Year != t1.Year;
                case PsmcResetMode.Yearly: return t0.Year != t1.Year;
                case PsmcResetMode.Cumulative: return false;
                case PsmcResetMode.Anchor: return t0 >= anchor1 && t1 < anchor1;
            }
            return false;
        }

        private void CalcVwap()
        {
            double src = Src();
            double vol = UseRealVolume ? Volume[0] : 1.0;
            bool reset = VwapReset();
            bool first = CurrentBar == 0;

            double pPV = (first || reset) ? 0 : sPV[1];
            double pV = (first || reset) ? 0 : sV[1];
            double pPPV = (first || reset) ? 0 : sPPV[1];
            sPV[0] = pPV + src * vol;
            sV[0] = pV + vol;
            sPPV[0] = pPPV + vol * src * src;

            double qPV = 0, qV = 0, qPPV = 0;
            if (Time[0] >= anchor2)
            {
                bool started = !first && Time[1] >= anchor2;
                qPV = started ? aPV[1] : 0;
                qV = started ? aV[1] : 0;
                qPPV = started ? aPPV[1] : 0;
                qPV += src * vol; qV += vol; qPPV += vol * src * src;
            }
            aPV[0] = qPV; aV[0] = qV; aPPV[0] = qPPV;

            cPV[0] = (first ? 0 : cPV[1]) + src * vol;
            cV[0] = (first ? 0 : cV[1]) + vol;

            if (sV[0] > 0)
            {
                double vwap = sPV[0] / sV[0];
                double sd = Math.Sqrt(Math.Max(0.0, sPPV[0] / sV[0] - vwap * vwap));

                if (ShowVwap)
                {
                    Values[0][0] = vwap;
                    PlotBrushes[0][0] = TrendColoring ? (Close[0] > vwap ? VwapBull : VwapBear) : VwapBull;
                }
                if (ShowBands)
                {
                    if (ShowSd1) { Values[1][0] = vwap + sd * Sd1Mult; Values[2][0] = vwap - sd * Sd1Mult; }
                    if (ShowSd2) { Values[3][0] = vwap + sd * Sd2Mult; Values[4][0] = vwap - sd * Sd2Mult; }
                    if (ShowSd3) { Values[5][0] = vwap + sd * Sd3Mult; Values[6][0] = vwap - sd * Sd3Mult; }
                }
            }
            if (ShowAnchored && aV[0] > 0)
                Values[7][0] = aPV[0] / aV[0];
            if (ShowCumulative && cV[0] > 0)
                Values[8][0] = cPV[0] / cV[0];
        }
        #endregion

        #region Closed-bar processing
        private int BA(int absBar) { return CurrentBar - absBar; }   // absolute bar -> barsAgo

        private void ProcessClosedBar()
        {
            int idx = CurrentBar - off;               // index of the bar that just closed
            if (idx < 1 || idx <= lastProcessed)
                return;
            lastProcessed = idx;

            double o = Open[off], h = High[off], l = Low[off], c = Close[off];

            double range = h - l, body = Math.Abs(o - c);
            int emaLen = Math.Min(idx + 1, 200);
            double alpha = 2.0 / (emaLen + 1.0);
            atrEma = alpha * ((range + body) / 2.0) + (1 - alpha) * atrEma;
            double atr = atrEma / 4.0;
            while (atrList.Count < idx) atrList.Add(atr);
            atrList.Add(atr);

            double a2 = 2.0 / (idx + 2.0);
            cumRange = (1.0 - a2) * cumRange + a2 * range;

            if (EnableZones)
                UpdateZones(idx, o, h, l, c, atr);

            ResetAlertFlags();

            // 1) pivots for every engine, 2) displays -- same order as the Pine script
            UpdatePivots(eSwing, idx);
            UpdatePivots(eInt, idx);
            if (ShowEqual) UpdatePivots(eEq, idx);
            if (ShowInternal) DisplayStructure(eInt, idx);
            if (ShowSwing) DisplayStructure(eSwing, idx);
            if (ShowStrongWeak || ShowZones) UpdateTrailing(idx);

            if (EnableAlerts && State == State.Realtime)
                FireAlerts();
        }
        #endregion

        #region Supply / Demand zones
        private double Angle(int count)
        {
            if (cumRange <= 0 || CurrentBar - off - count < 0) return 0;
            double slope = (Close[off] - Close[off + count]) / cumRange;
            return Math.Abs(Math.Atan(slope) * 180.0 / Math.PI);
        }

        private void UpdateZones(int idx, double o, double h, double l, double c, double atr)
        {
            bool green = o < c;
            if (green) { supplyActive = false; redCount = 0; greenCount++; }
            else { demandActive = false; greenCount = 0; redCount++; }

            bool supply = false, demand = false;
            int back = 0;
            if (redCount >= MinCount && Angle(redCount) >= MinAngle && !supplyActive)
            {
                supplyActive = true; supply = true; back = redCount;
            }
            if (greenCount >= MinCount && Angle(greenCount) >= MinAngle && !demandActive)
            {
                demandActive = true; demand = true; back = greenCount;
            }

            for (int i = zones.Count - 1; i >= 0; i--)
            {
                Zone z = zones[i];
                int age = idx - z.Start;
                bool broke = HitDeletion && (z.Supply ? c > z.Top : c < z.Bottom);
                if (broke || age >= MaxZoneAge)
                {
                    RemoveZone(z);
                    zones.RemoveAt(i);
                }
                else
                {
                    DrawZoneBox(z);
                    ExtendPoc(z);
                }
            }

            if (!(supply || demand) || back <= 0 || idx - back < 0) return;
            bool isSupply = supply;
            int zb = idx - back;
            int ba = off + back;
            double zo = Open[ba], zh = High[ba], zl = Low[ba], zc = Close[ba];
            double zatr = zb < atrList.Count ? atrList[zb] : atr;
            double ctop = Math.Max(zo, zc), cbot = Math.Min(zo, zc);
            double top, bottom;
            if (isSupply)
            {
                switch (ZoneStyle)
                {
                    case PsmcZoneStyle.Body: top = ctop; bottom = cbot; break;
                    case PsmcZoneStyle.Candle: top = zh; bottom = zl; break;
                    default: top = zh; bottom = (zh - ctop > zatr) ? ctop : zh - zatr; break;
                }
            }
            else
            {
                switch (ZoneStyle)
                {
                    case PsmcZoneStyle.Body: top = ctop; bottom = cbot; break;
                    case PsmcZoneStyle.Candle: top = zh; bottom = zl; break;
                    default: top = (cbot - zl > zatr) ? cbot : zl + zatr; bottom = zl; break;
                }
            }

            Zone nz = new Zone
            {
                Tag = "PSMCZ" + (zoneCounter++),
                Top = top,
                Bottom = bottom,
                Left = zb,
                Start = idx,
                Supply = isSupply
            };
            nz.Tags.Add(nz.Tag);
            DrawZoneBox(nz);
            zones.Add(nz);

            if (ShowProfile || ShowPoc)
                BuildAndDrawProfile(nz, idx, back);

            while (zones.Count > MaxZones)
            {
                RemoveZone(zones[0]);
                zones.RemoveAt(0);
            }
        }

        private void DrawZoneBox(Zone z)
        {
            Brush col = z.Supply ? SupplyColor : DemandColor;
            Draw.Rectangle(this, z.Tag, false, BA(z.Left), z.Top, 0, z.Bottom, col, col, ZoneOpacity);
        }

        // POC rays are re-anchored each bar when not extended (Pine: line x2 follows bar_index + 1)
        private void ExtendPoc(Zone z)
        {
            // rays extend automatically; nothing to do (kept as a hook)
        }

        private void RemoveZone(Zone z)
        {
            foreach (string t in z.Tags)
                RemoveDrawObject(t);
        }
        #endregion

        #region Footprint profile
        // same bin rule as the Pine script: ceil((p-min)/range) - 1, clamped
        private static int BinOf(double price, double min, double rng, int bins)
        {
            int k = (int)Math.Max(1, Math.Ceiling((price - min) / rng)) - 1;
            if (k > bins - 1) k = bins - 1;
            if (k < 0) k = 0;
            return k;
        }

        private double[] Smooth(double[] src, double period)
        {
            int n = src.Length;
            if (period <= 1 || n < 2) return src;
            double[] outv = new double[n];
            for (int i = 0; i < n; i++)
            {
                double ws = 0, acc = 0;
                for (int j = 0; j < n; j++)
                {
                    double x = (j - i) / period;
                    double w = Math.Abs(x) <= 1 ? 0.75 * (1 - x * x) : 0.0;
                    ws += w;
                    acc += w * src[j];
                }
                outv[i] = ws > 0 ? acc / ws : src[i];
            }
            return outv;
        }

        private void BuildAndDrawProfile(Zone z, int idx, int width)
        {
            // Pine keeps only `period` (50) bars of data
            int dw = Math.Min(Math.Min(width * 2 - 1, ProfileMaxBars), idx + 1);
            if (dw < 1) return;
            int first = idx - dw + 1;

            double min = double.MaxValue, max = double.MinValue;
            for (int b = first; b <= idx; b++)
            {
                int ba = BA(b);
                min = Math.Min(min, Low[ba]);
                max = Math.Max(max, High[ba]);
            }
            if (max <= min) return;

            int bins = Math.Min(Math.Max(1, (int)Math.Round((max - min) / TickSize)), Math.Max(2, ProfileBins));
            double rng = (max - min) / bins;
            double[] buy = new double[bins], sell = new double[bins];

            for (int b = first; b <= idx; b++)
            {
                int ba = BA(b);
                int lo = (int)Math.Round(Low[ba] / TickSize), hi = (int)Math.Round(High[ba] / TickSize);
                int n = Math.Max(1, hi - lo + 1);
                double bShare = Close[ba] > Open[ba] ? 1.0 : (Close[ba] < Open[ba] ? 0.0 : 0.5);

                Dictionary<int, double[]> d;
                if (ProfileType == PsmcProfileType.Volume && barData.TryGetValue(b, out d) && d.Count > 0)
                {
                    foreach (KeyValuePair<int, double[]> kv in d)
                    {
                        int k = BinOf(kv.Key * TickSize, min, rng, bins);
                        buy[k] += kv.Value[0];
                        sell[k] += kv.Value[1];
                    }
                }
                else
                {
                    // no tick data (or Time/Raw profile): spread the bar's weight across its range
                    double weight = ProfileType == PsmcProfileType.Volume ? Volume[ba] : 1.0;
                    double share = weight / n;
                    for (int t = lo; t <= hi; t++)
                    {
                        int k = BinOf(t * TickSize, min, rng, bins);
                        buy[k] += share * bShare;
                        sell[k] += share * (1.0 - bShare);
                    }
                }
            }

            double[] sb = Smooth(buy, ProfileFilter);
            double[] ss = Smooth(sell, ProfileFilter);
            double[] sum = new double[bins];
            double total = 0, minV = double.MaxValue, maxV = double.MinValue;
            int poc = 0, pocBull = 0, pocBear = 0;
            for (int i = 0; i < bins; i++)
            {
                sum[i] = sb[i] + ss[i];
                total += sum[i];
                if (sum[i] > maxV) { maxV = sum[i]; poc = i; }
                if (sum[i] < minV) minV = sum[i];
                if (sb[i] > sb[pocBull]) pocBull = i;
                if (ss[i] > ss[pocBear]) pocBear = i;
            }
            if (total <= 0) return;

            int leftBarsAgo = BA(z.Left);

            if (ShowPoc)
            {
                double bp = min + (pocBull + 0.5) * rng;
                double sp = min + (pocBear + 0.5) * rng;
                string t1 = z.Tag + "_pocB", t2 = z.Tag + "_pocS";
                z.Tags.Add(t1); z.Tags.Add(t2);
                if (ExtendZones)
                {
                    Draw.Ray(this, t1, false, leftBarsAgo, bp, 0, bp, ProfileBull, DashStyleHelper.Solid, PocWidth);
                    Draw.Ray(this, t2, false, leftBarsAgo, sp, 0, sp, ProfileBear, DashStyleHelper.Solid, PocWidth);
                }
                else
                {
                    Draw.Line(this, t1, false, leftBarsAgo, bp, -2, bp, ProfileBull, DashStyleHelper.Solid, PocWidth);
                    Draw.Line(this, t2, false, leftBarsAgo, sp, -2, sp, ProfileBear, DashStyleHelper.Solid, PocWidth);
                }
            }

            if (!ShowProfile) return;

            // 70% value area around the POC (identical to the Pine routine, including its final top_idx += 1)
            int vaTop = poc, vaBot = poc;
            double acc = sum[poc];
            for (int s = 0; s < bins; s++)
            {
                if (vaTop < bins - 1) { vaTop++; acc += sum[vaTop]; }
                if (vaBot > 0) { vaBot--; acc += sum[vaBot]; }
                if (acc / total * 100.0 >= 70.0) break;
            }
            vaTop += 1;

            double vr = Math.Max(maxV - minV, 1e-10);
            for (int i = 0; i < bins; i++)
            {
                double center = min + rng * (i + 0.5);
                bool bullish = sb[i] >= ss[i];
                int norm = (int)Math.Round(1 + ((sum[i] - minV) * 10) / vr);
                norm = Math.Max(1, Math.Min(10, norm));
                string blocks = new string('█', norm);
                string txt = blocks;
                if (ShowVolumeAtLevel)
                    txt = bullish ? blocks + " " + Math.Round(sum[i]) : Math.Round(sum[i]) + " " + blocks;

                bool inVA = !(i < vaBot || i > vaTop);
                Brush baseB = bullish ? ProfileBull : ProfileBear;
                Brush col = inVA ? MakeBrush(baseB, 0.25 + 0.04 * norm) : grayProf;

                string tag = z.Tag + "_p" + i;
                z.Tags.Add(tag);
                Draw.Text(this, tag, false, txt, leftBarsAgo, center, 0, col, profFont,
                    bullish ? TextAlignment.Left : TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);
            }
        }

        private static Brush MakeBrush(Brush b, double opacity)
        {
            SolidColorBrush sb = b as SolidColorBrush;
            Color c = sb != null ? sb.Color : Colors.Gray;
            SolidColorBrush nb = new SolidColorBrush(c);
            nb.Opacity = Math.Max(0.05, Math.Min(1.0, opacity));
            nb.Freeze();
            return nb;
        }
        #endregion

        #region SMC structure
        private SimpleFont FontFor(PsmcLabelSize s)
        {
            switch (s)
            {
                case PsmcLabelSize.Tiny: return new SimpleFont("Arial", 8);
                case PsmcLabelSize.Small: return new SimpleFont("Arial", 10);
                default: return new SimpleFont("Arial", 12);
            }
        }

        private void ResetAlertFlags()
        {
            aIntBullBOS = aIntBearBOS = aIntBullCH = aIntBearCH = false;
            aSwBullBOS = aSwBearBOS = aSwBullCH = aSwBearCH = false;
            aEqH = aEqL = false;
        }

        private void UpdatePivots(Engine e, int idx)
        {
            if (idx < e.Len + 1) return;

            double hLen = High[off + e.Len], lLen = Low[off + e.Len];
            double hh = double.MinValue, ll = double.MaxValue;
            for (int k = 0; k < e.Len; k++)
            {
                hh = Math.Max(hh, High[off + k]);
                ll = Math.Min(ll, Low[off + k]);
            }
            int prev = e.Leg;
            if (hLen > hh) e.Leg = 0;          // bearish leg
            else if (lLen < ll) e.Leg = 1;     // bullish leg

            int change = e.Leg - prev;
            if (change == 0) return;

            DateTime pivTime = Time[off + e.Len];
            int pivBar = idx - e.Len;

            if (change == 1)                    // pivot LOW
            {
                Pivot p = e.Low;
                if (e.Equal && !double.IsNaN(p.Cur) && Math.Abs(p.Cur - lLen) < EqualThreshold * atr200[off])
                    DrawEqual(p, lLen, pivBar, idx, false);
                p.Last = p.Cur; p.Cur = lLen; p.Crossed = false; p.Bar = pivBar; p.Time = pivTime;
                if (!e.Equal && !e.Internal)
                {
                    trBottom = lLen; trBarTime = pivTime; trLastBottomTime = pivTime; trHasTime = true;
                    if (ShowSwingPoints && !double.IsNaN(p.Last))
                        DrawSwingPoint(pivBar, lLen, p.Cur < p.Last ? "LL" : "HL", SwingBullColor, false);
                }
            }
            else if (change == -1)              // pivot HIGH
            {
                Pivot p = e.High;
                if (e.Equal && !double.IsNaN(p.Cur) && Math.Abs(p.Cur - hLen) < EqualThreshold * atr200[off])
                    DrawEqual(p, hLen, pivBar, idx, true);
                p.Last = p.Cur; p.Cur = hLen; p.Crossed = false; p.Bar = pivBar; p.Time = pivTime;
                if (!e.Equal && !e.Internal)
                {
                    trTop = hLen; trBarTime = pivTime; trLastTopTime = pivTime; trHasTime = true;
                    if (ShowSwingPoints && !double.IsNaN(p.Last))
                        DrawSwingPoint(pivBar, hLen, p.Cur > p.Last ? "HH" : "LH", SwingBearColor, true);
                }
            }
        }

        private static bool Allowed(PsmcStructFilter f, string tag)
        {
            return f == PsmcStructFilter.All || (f == PsmcStructFilter.BOS && tag == "BOS") || (f == PsmcStructFilter.CHoCH && tag == "CHoCH");
        }

        private void DisplayStructure(Engine e, int idx)
        {
            if (idx < e.Len + 1) return;

            bool bullishBar = true, bearishBar = true;
            if (InternalConfluence)
            {
                double ho = High[off], lo = Low[off], oo = Open[off], cc = Close[off];
                // verbatim from the Pine source (including min(close, open - low))
                bullishBar = ho - Math.Max(cc, oo) > Math.Min(cc, oo - lo);
                bearishBar = ho - Math.Max(cc, oo) < Math.Min(cc, oo - lo);
            }

            double c = Close[off], cp = Close[off + 1];

            Pivot ph = e.High;
            bool extraBull = e.Internal ? (eInt.High.Cur != eSwing.High.Cur && bullishBar) : true;
            if (!double.IsNaN(ph.Cur) && c > ph.Cur && cp <= ph.Cur && !ph.Crossed && extraBull)
            {
                string tag = e.Bias == -1 ? "CHoCH" : "BOS";
                if (e.Internal) { aIntBullCH = tag == "CHoCH"; aIntBullBOS = tag == "BOS"; }
                else { aSwBullCH = tag == "CHoCH"; aSwBullBOS = tag == "BOS"; }
                ph.Crossed = true;
                e.Bias = 1;
                if (Allowed(e.Internal ? InternalBullFilter : SwingBullFilter, tag))
                    DrawStructure(ph, idx, tag, e.Internal ? InternalBullColor : SwingBullColor, e.Internal, true);
            }

            Pivot pl = e.Low;
            bool extraBear = e.Internal ? (eInt.Low.Cur != eSwing.Low.Cur && bearishBar) : true;
            if (!double.IsNaN(pl.Cur) && c < pl.Cur && cp >= pl.Cur && !pl.Crossed && extraBear)
            {
                string tag = e.Bias == 1 ? "CHoCH" : "BOS";
                if (e.Internal) { aIntBearCH = tag == "CHoCH"; aIntBearBOS = tag == "BOS"; }
                else { aSwBearCH = tag == "CHoCH"; aSwBearBOS = tag == "BOS"; }
                pl.Crossed = true;
                e.Bias = -1;
                if (Allowed(e.Internal ? InternalBearFilter : SwingBearFilter, tag))
                    DrawStructure(pl, idx, tag, e.Internal ? InternalBearColor : SwingBearColor, e.Internal, false);
            }
        }

        private void DrawStructure(Pivot p, int idx, string text, Brush col, bool internalStruct, bool bullish)
        {
            string id = "PSMCS" + (structCounter++);
            Draw.Line(this, id + "l", false, BA(p.Bar), p.Cur, off, p.Cur, col,
                internalStruct ? DashStyleHelper.Dash : DashStyleHelper.Solid, 1);
            int midBar = (int)Math.Round(0.5 * (p.Bar + idx));
            Draw.Text(this, id + "t", false, text, BA(midBar), p.Cur, bullish ? -8 : 8, col,
                FontFor(internalStruct ? InternalSize : SwingSize),
                TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            TrackTags(id + "l", id + "t");
        }

        private void DrawSwingPoint(int bar, double price, string text, Brush col, bool isHigh)
        {
            string id = "PSMCP" + (structCounter++);
            Draw.Text(this, id, false, text, BA(bar), price, isHigh ? -8 : 8, col, FontFor(SwingSize),
                TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
            TrackTags(id, null);
        }

        private void DrawEqual(Pivot old, double level, int newBar, int idx, bool isHigh)
        {
            string id = "PSMCE" + (structCounter++);
            Brush col = isHigh ? SwingBearColor : SwingBullColor;
            if (ShowEqual)
            {
                Draw.Line(this, id + "l", false, BA(old.Bar), old.Cur, BA(newBar), level, col, DashStyleHelper.Dot, 1);
                int mid = (int)Math.Round(0.5 * (old.Bar + newBar));
                Draw.Text(this, id + "t", false, isHigh ? "EQH" : "EQL", BA(mid), level, isHigh ? -8 : 8, col,
                    FontFor(EqualSize), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                TrackTags(id + "l", id + "t");
            }
            if (isHigh) aEqH = true; else aEqL = true;
        }

        private void TrackTags(string a, string b)
        {
            structTags.Enqueue(a);
            if (b != null) structTags.Enqueue(b);
            while (structTags.Count > MaxStructLabels * 2)
                RemoveDrawObject(structTags.Dequeue());
        }

        private void UpdateTrailing(int idx)
        {
            double h = High[off], l = Low[off];
            DateTime t = Time[off];
            if (double.IsNaN(trTop)) { trTop = h; trLastTopTime = t; }
            else
            {
                trTop = Math.Max(h, trTop);
                if (trTop == h) trLastTopTime = t;
            }
            if (double.IsNaN(trBottom)) { trBottom = l; trLastBottomTime = t; }
            else
            {
                trBottom = Math.Min(l, trBottom);
                if (trBottom == l) trLastBottomTime = t;
            }
            if (!trHasTime) { trBarTime = t; trHasTime = true; }
        }

        private void FireAlerts()
        {
            string snd = NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav";
            if (aIntBullBOS) Fire("SMC Internal Bullish BOS", snd);
            if (aIntBullCH) Fire("SMC Internal Bullish CHoCH", snd);
            if (aIntBearBOS) Fire("SMC Internal Bearish BOS", snd);
            if (aIntBearCH) Fire("SMC Internal Bearish CHoCH", snd);
            if (aSwBullBOS) Fire("SMC Bullish BOS", snd);
            if (aSwBullCH) Fire("SMC Bullish CHoCH", snd);
            if (aSwBearBOS) Fire("SMC Bearish BOS", snd);
            if (aSwBearCH) Fire("SMC Bearish CHoCH", snd);
            if (aEqH) Fire("SMC Equal High", snd);
            if (aEqL) Fire("SMC Equal Low", snd);
        }

        private void Fire(string msg, string snd)
        {
            Alert("PSMC_" + msg + "_" + CurrentBar, Priority.Medium, msg + " formed", snd, 1, Brushes.Black, Brushes.White);
        }
        #endregion

        #region Latest-bar drawings (labels, levels, zones, fills)
        private void DrawLatest()
        {
            DateTime now = Time[0];

            // "VWAP" label at the last bar
            if (ShowVwapLabel && ShowVwap && sV[0] > 0)
            {
                Brush c = TrendColoring ? (Close[0] > Values[0][0] ? VwapBull : VwapBear) : VwapBull;
                Draw.Text(this, "PSMC_vwaplbl", false, "VWAP", 0, Values[0][0], 0, c, new SimpleFont("Arial", 9),
                    TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);
            }

            // strong / weak high & low
            if (ShowStrongWeak && !double.IsNaN(trTop) && !double.IsNaN(trBottom))
            {
                Draw.Line(this, "PSMC_swH", false, trLastTopTime, trTop, now, trTop, SwingBearColor, DashStyleHelper.Dot, 1);
                Draw.Line(this, "PSMC_swL", false, trLastBottomTime, trBottom, now, trBottom, SwingBullColor, DashStyleHelper.Dot, 1);
                Draw.Text(this, "PSMC_swHt", false, eSwing.Bias == -1 ? "Strong High" : "Weak High", 0, trTop, -8,
                    SwingBearColor, new SimpleFont("Arial", 8), TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);
                Draw.Text(this, "PSMC_swLt", false, eSwing.Bias == 1 ? "Strong Low" : "Weak Low", 0, trBottom, 8,
                    SwingBullColor, new SimpleFont("Arial", 8), TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);
            }

            // premium / equilibrium / discount
            if (ShowZones && !double.IsNaN(trTop) && !double.IsNaN(trBottom) && trTop > trBottom && trHasTime)
            {
                double r = trTop - trBottom;
                double eq = (trTop + trBottom) / 2.0;
                Draw.Rectangle(this, "PSMC_prem", false, trBarTime, trTop, now, trTop - r * 0.05, PremiumColor, PremiumColor, 10);
                Draw.Rectangle(this, "PSMC_eq", false, trBarTime, eq + r * 0.025, now, eq - r * 0.025, EquilibriumColor, EquilibriumColor, 10);
                Draw.Rectangle(this, "PSMC_disc", false, trBarTime, trBottom + r * 0.05, now, trBottom, DiscountColor, DiscountColor, 10);
            }

            // prior daily / weekly / monthly high-low
            DrawHtf(idxD, "D");
            DrawHtf(idxW, "W");
            DrawHtf(idxM, "M");
        }

        private void DrawHtf(int k, string tf)
        {
            if (k < 1 || BarsArray[k].Count < 3 || CurrentBars[k] < 2) return;
            double hi = Highs[k][1], lo = Lows[k][1];
            DateTime t0 = Times[k][2];
            DateTime now = Time[0];
            Draw.Line(this, "PSMC_" + tf + "H", false, t0, hi, now, hi, HtfColor, DashStyleHelper.Dot, 1);
            Draw.Line(this, "PSMC_" + tf + "L", false, t0, lo, now, lo, HtfColor, DashStyleHelper.Dot, 1);
            SimpleFont f = new SimpleFont("Arial", 8);
            Draw.Text(this, "PSMC_" + tf + "Ht", false, "P" + tf + "H", 0, hi, 0, HtfColor, f, TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);
            Draw.Text(this, "PSMC_" + tf + "Lt", false, "P" + tf + "L", 0, lo, 0, HtfColor, f, TextAlignment.Right, Brushes.Transparent, Brushes.Transparent, 0);
        }

        private void DrawFills()
        {
            if (!ShowBands || CurrentBar < 2) return;
            int span = Math.Min(CurrentBar, 5000);
            if (ShowVwap && ShowSd1 && FillSd1)
            {
                Draw.Region(this, "PSMC_f1u", span, 0, Values[0], Values[1], Brushes.Transparent, FillColor, 8);
                Draw.Region(this, "PSMC_f1d", span, 0, Values[0], Values[2], Brushes.Transparent, FillColor, 8);
            }
            if (ShowSd1 && ShowSd2 && FillSd2)
            {
                Draw.Region(this, "PSMC_f2u", span, 0, Values[1], Values[3], Brushes.Transparent, FillColor, 4);
                Draw.Region(this, "PSMC_f2d", span, 0, Values[2], Values[4], Brushes.Transparent, FillColor, 4);
            }
        }
        #endregion

        #region Properties
        // ---- VWAP
        [NinjaScriptProperty]
        [Display(Name = "Reset mode", GroupName = "1. VWAP", Order = 0)]
        public PsmcResetMode ResetMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchor date (yyyy-MM-dd HH:mm) for Anchor mode", GroupName = "1. VWAP", Order = 1)]
        public string AnchorDate { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Price source", GroupName = "1. VWAP", Order = 2)]
        public PsmcPriceSource PriceSource { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use real volume (off = 1 per bar)", GroupName = "1. VWAP", Order = 3)]
        public bool UseRealVolume { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show VWAP", GroupName = "1. VWAP", Order = 4)]
        public bool ShowVwap { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Color by trend", GroupName = "1. VWAP", Order = 5)]
        public bool TrendColoring { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show SD bands", GroupName = "1. VWAP", Order = 6)]
        public bool ShowBands { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 5.0)]
        [Display(Name = "SD1 multiplier", GroupName = "1. VWAP", Order = 7)]
        public double Sd1Mult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 5.0)]
        [Display(Name = "SD2 multiplier", GroupName = "1. VWAP", Order = 8)]
        public double Sd2Mult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 5.0)]
        [Display(Name = "SD3 multiplier", GroupName = "1. VWAP", Order = 9)]
        public double Sd3Mult { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show SD1", GroupName = "1. VWAP", Order = 10)]
        public bool ShowSd1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show SD2", GroupName = "1. VWAP", Order = 11)]
        public bool ShowSd2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show SD3", GroupName = "1. VWAP", Order = 12)]
        public bool ShowSd3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fill SD1 band", GroupName = "1. VWAP", Order = 13)]
        public bool FillSd1 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Fill SD1-SD2 band", GroupName = "1. VWAP", Order = 14)]
        public bool FillSd2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show anchored VWAP", GroupName = "1. VWAP", Order = 15)]
        public bool ShowAnchored { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Anchored VWAP date (yyyy-MM-dd HH:mm)", GroupName = "1. VWAP", Order = 16)]
        public string AnchorDate2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show cumulative VWAP", GroupName = "1. VWAP", Order = 17)]
        public bool ShowCumulative { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show VWAP price label", GroupName = "1. VWAP", Order = 18)]
        public bool ShowVwapLabel { get; set; }

        [XmlIgnore]
        [Display(Name = "VWAP color (above)", GroupName = "1. VWAP", Order = 19)]
        public Brush VwapBull { get; set; }
        [Browsable(false)]
        public string VwapBullSerialize { get { return Serialize.BrushToString(VwapBull); } set { VwapBull = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "VWAP color (below)", GroupName = "1. VWAP", Order = 20)]
        public Brush VwapBear { get; set; }
        [Browsable(false)]
        public string VwapBearSerialize { get { return Serialize.BrushToString(VwapBear); } set { VwapBear = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Band fill color", GroupName = "1. VWAP", Order = 21)]
        public Brush FillColor { get; set; }
        [Browsable(false)]
        public string FillColorSerialize { get { return Serialize.BrushToString(FillColor); } set { FillColor = Serialize.StringToBrush(value); } }

        // ---- Zones / profile
        [NinjaScriptProperty]
        [Display(Name = "Enable supply/demand zones", GroupName = "2. Zones & Profile", Order = 0)]
        public bool EnableZones { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show footprint profile blocks", GroupName = "2. Zones & Profile", Order = 1)]
        public bool ShowProfile { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show volume at level", GroupName = "2. Zones & Profile", Order = 2)]
        public bool ShowVolumeAtLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Profile type (Volume = real bid/ask; Time/Raw = bar-range)", GroupName = "2. Zones & Profile", Order = 3)]
        public PsmcProfileType ProfileType { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show POC lines", GroupName = "2. Zones & Profile", Order = 4)]
        public bool ShowPoc { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "POC line width", GroupName = "2. Zones & Profile", Order = 5)]
        public int PocWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Zone style", GroupName = "2. Zones & Profile", Order = 6)]
        public PsmcZoneStyle ZoneStyle { get; set; }

        [NinjaScriptProperty]
        [Range(2, 30)]
        [Display(Name = "Min same-color candles", GroupName = "2. Zones & Profile", Order = 7)]
        public int MinCount { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 89.0)]
        [Display(Name = "Min angle (deg)", GroupName = "2. Zones & Profile", Order = 8)]
        public double MinAngle { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Max zone age (bars)", GroupName = "2. Zones & Profile", Order = 9)]
        public int MaxZoneAge { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Max active zones", GroupName = "2. Zones & Profile", Order = 10)]
        public int MaxZones { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Delete zone when price closes through it", GroupName = "2. Zones & Profile", Order = 11)]
        public bool HitDeletion { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Extend POC lines right", GroupName = "2. Zones & Profile", Order = 12)]
        public bool ExtendZones { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Zone box opacity % (TradingView draws it behind candles; lower = see price)", GroupName = "2. Zones & Profile", Order = 13)]
        public int ZoneOpacity { get; set; }

        [NinjaScriptProperty]
        [Range(2, 40)]
        [Display(Name = "Profile price bins", GroupName = "2. Zones & Profile", Order = 14)]
        public int ProfileBins { get; set; }

        [NinjaScriptProperty]
        [Range(5, 500)]
        [Display(Name = "Profile max bars", GroupName = "2. Zones & Profile", Order = 15)]
        public int ProfileMaxBars { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10.0)]
        [Display(Name = "Profile smoothing", GroupName = "2. Zones & Profile", Order = 16)]
        public double ProfileFilter { get; set; }

        [XmlIgnore]
        [Display(Name = "Supply zone color", GroupName = "2. Zones & Profile", Order = 17)]
        public Brush SupplyColor { get; set; }
        [Browsable(false)]
        public string SupplyColorSerialize { get { return Serialize.BrushToString(SupplyColor); } set { SupplyColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Demand zone color", GroupName = "2. Zones & Profile", Order = 18)]
        public Brush DemandColor { get; set; }
        [Browsable(false)]
        public string DemandColorSerialize { get { return Serialize.BrushToString(DemandColor); } set { DemandColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Bullish profile / POC color", GroupName = "2. Zones & Profile", Order = 19)]
        public Brush ProfileBull { get; set; }
        [Browsable(false)]
        public string ProfileBullSerialize { get { return Serialize.BrushToString(ProfileBull); } set { ProfileBull = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Bearish profile / POC color", GroupName = "2. Zones & Profile", Order = 20)]
        public Brush ProfileBear { get; set; }
        [Browsable(false)]
        public string ProfileBearSerialize { get { return Serialize.BrushToString(ProfileBear); } set { ProfileBear = Serialize.StringToBrush(value); } }

        // ---- SMC internal
        [NinjaScriptProperty]
        [Display(Name = "Show internal structure", GroupName = "3. SMC Internal", Order = 0)]
        public bool ShowInternal { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bullish structure", GroupName = "3. SMC Internal", Order = 1)]
        public PsmcStructFilter InternalBullFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bearish structure", GroupName = "3. SMC Internal", Order = 2)]
        public PsmcStructFilter InternalBearFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Label size", GroupName = "3. SMC Internal", Order = 3)]
        public PsmcLabelSize InternalSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Confluence filter", GroupName = "3. SMC Internal", Order = 4)]
        public bool InternalConfluence { get; set; }

        [NinjaScriptProperty]
        [Range(2, 50)]
        [Display(Name = "Internal length", GroupName = "3. SMC Internal", Order = 5)]
        public int InternalLength { get; set; }

        [XmlIgnore]
        [Display(Name = "Bullish color", GroupName = "3. SMC Internal", Order = 6)]
        public Brush InternalBullColor { get; set; }
        [Browsable(false)]
        public string InternalBullColorSerialize { get { return Serialize.BrushToString(InternalBullColor); } set { InternalBullColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Bearish color", GroupName = "3. SMC Internal", Order = 7)]
        public Brush InternalBearColor { get; set; }
        [Browsable(false)]
        public string InternalBearColorSerialize { get { return Serialize.BrushToString(InternalBearColor); } set { InternalBearColor = Serialize.StringToBrush(value); } }

        // ---- SMC swing
        [NinjaScriptProperty]
        [Display(Name = "Show swing structure", GroupName = "4. SMC Swing", Order = 0)]
        public bool ShowSwing { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bullish structure", GroupName = "4. SMC Swing", Order = 1)]
        public PsmcStructFilter SwingBullFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bearish structure", GroupName = "4. SMC Swing", Order = 2)]
        public PsmcStructFilter SwingBearFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Label size", GroupName = "4. SMC Swing", Order = 3)]
        public PsmcLabelSize SwingSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show HH / HL / LH / LL", GroupName = "4. SMC Swing", Order = 4)]
        public bool ShowSwingPoints { get; set; }

        [NinjaScriptProperty]
        [Range(10, 200)]
        [Display(Name = "Swing length", GroupName = "4. SMC Swing", Order = 5)]
        public int SwingLength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show strong / weak high-low", GroupName = "4. SMC Swing", Order = 6)]
        public bool ShowStrongWeak { get; set; }

        [NinjaScriptProperty]
        [Range(10, 1000)]
        [Display(Name = "Max structure labels kept", GroupName = "4. SMC Swing", Order = 7)]
        public int MaxStructLabels { get; set; }

        [XmlIgnore]
        [Display(Name = "Bullish color", GroupName = "4. SMC Swing", Order = 8)]
        public Brush SwingBullColor { get; set; }
        [Browsable(false)]
        public string SwingBullColorSerialize { get { return Serialize.BrushToString(SwingBullColor); } set { SwingBullColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Bearish color", GroupName = "4. SMC Swing", Order = 9)]
        public Brush SwingBearColor { get; set; }
        [Browsable(false)]
        public string SwingBearColorSerialize { get { return Serialize.BrushToString(SwingBearColor); } set { SwingBearColor = Serialize.StringToBrush(value); } }

        // ---- EQH / EQL
        [NinjaScriptProperty]
        [Display(Name = "Show equal high / low", GroupName = "5. SMC EQH / EQL", Order = 0)]
        public bool ShowEqual { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Bars confirmation", GroupName = "5. SMC EQH / EQL", Order = 1)]
        public int EqualLength { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 0.5)]
        [Display(Name = "Threshold", GroupName = "5. SMC EQH / EQL", Order = 2)]
        public double EqualThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Label size", GroupName = "5. SMC EQH / EQL", Order = 3)]
        public PsmcLabelSize EqualSize { get; set; }

        // ---- Premium / discount
        [NinjaScriptProperty]
        [Display(Name = "Show premium / equilibrium / discount", GroupName = "6. SMC Premium / Discount", Order = 0)]
        public bool ShowZones { get; set; }

        [XmlIgnore]
        [Display(Name = "Premium", GroupName = "6. SMC Premium / Discount", Order = 1)]
        public Brush PremiumColor { get; set; }
        [Browsable(false)]
        public string PremiumColorSerialize { get { return Serialize.BrushToString(PremiumColor); } set { PremiumColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Equilibrium", GroupName = "6. SMC Premium / Discount", Order = 2)]
        public Brush EquilibriumColor { get; set; }
        [Browsable(false)]
        public string EquilibriumColorSerialize { get { return Serialize.BrushToString(EquilibriumColor); } set { EquilibriumColor = Serialize.StringToBrush(value); } }

        [XmlIgnore]
        [Display(Name = "Discount", GroupName = "6. SMC Premium / Discount", Order = 3)]
        public Brush DiscountColor { get; set; }
        [Browsable(false)]
        public string DiscountColorSerialize { get { return Serialize.BrushToString(DiscountColor); } set { DiscountColor = Serialize.StringToBrush(value); } }

        // ---- MTF levels
        [NinjaScriptProperty]
        [Display(Name = "Prior daily high / low", GroupName = "7. SMC MTF Levels", Order = 0)]
        public bool ShowDaily { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Prior weekly high / low", GroupName = "7. SMC MTF Levels", Order = 1)]
        public bool ShowWeekly { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Prior monthly high / low", GroupName = "7. SMC MTF Levels", Order = 2)]
        public bool ShowMonthly { get; set; }

        [XmlIgnore]
        [Display(Name = "Level color", GroupName = "7. SMC MTF Levels", Order = 3)]
        public Brush HtfColor { get; set; }
        [Browsable(false)]
        public string HtfColorSerialize { get { return Serialize.BrushToString(HtfColor); } set { HtfColor = Serialize.StringToBrush(value); } }

        // ---- Alerts
        [NinjaScriptProperty]
        [Display(Name = "Enable NinjaTrader alerts (live bars)", GroupName = "8. Alerts", Order = 0)]
        public bool EnableAlerts { get; set; }
        #endregion
    }
}
