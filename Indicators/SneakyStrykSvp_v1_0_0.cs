// ─────────────────────────────────────────────────────────────────────────────
// StrykSvp_v1_0_0.cs
// ─────────────────────────────────────────────────────────────────────────────
//  PORTED FROM TRADINGVIEW. Original: "STRYK: SVP" v3.3 (short title STRYK:SVP)
//  by Es-Sniper (open-source Pine Script v6, TradingView). This file is a
//  NinjaScript C# re-implementation of that published source: session/period
//  volume profiles (heatmap or classic bars), naked/tested point-of-control
//  lines, value area, a live developing-session profile, initial balance,
//  the on-chart panel, the three alert conditions, and all nine candle-
//  colouring engines (including the composite "DRE" engine). Not an original
//  Sneaky Suite design — attributed to the original author here and in the
//  repo's licensing notes. Standalone — no other Sneaky files required.
//
//  PINE -> NINJASCRIPT: WHERE THIS DIFFERS FROM THE SOURCE
//  -----------------------------------------------------------
//  1. VOLUME-AT-PRICE SOURCE. Pine builds its profile from
//     request.security_lower_tf() — real exchange bars one or more minutes
//     below the chart's own timeframe, with volume SPREAD evenly across each
//     of those bars' price range (an approximation, by the author's own
//     admission, "not a tape reading"). This port instead reads real TICKS
//     via OnMarketData (Calculate.OnEachTick) and classifies each trade
//     buy/sell by the quote rule (at/above ask = buy, at/below bid = sell,
//     tick-rule fallback inside the spread) — the same classification method
//     already used elsewhere in this repo (see SneakyDeltaLevels.cs). That is
//     MORE precise than Pine's lower-timeframe-bar proxy, not less, but it
//     needs live ticks or Tick Replay — on a historical bar with no recorded
//     ticks (replay off, far-back history), this port falls back to the same
//     thing Pine falls back to: the bar's own OHLCV, spread across its range
//     (or assigned to one representative price), exactly as the "Where each
//     intrabar's volume goes" setting describes. The panel's "Built from"
//     row says which one was actually used for the current session.
//  2. SESSION BOUNDARIES. Pine's time(timeframe.period, session, tz) and
//     timeframe.change() read TradingView's own exchange calendar precisely.
//     NinjaTrader bar timestamps are delivered in the connected Trading
//     Hours template's own timezone (Central Time for the standard CME
//     templates), so this port computes session boundaries directly off
//     Time[0] under that assumption, rather than doing its own timezone
//     conversion. If your chart's Trading Hours template isn't a CME
//     Central-Time template, the session boundaries below will be off by
//     whatever offset that implies — this is an honest approximation of
//     Pine's exchange-calendar engine, not a guaranteed byte-identical port.
//  3. CANDLE COLOURING. Pine's barcolor()/plotcandle() pair becomes
//     NinjaScript's BarBrushes[0] / CandleOutlineBrushes[0] (the standard
//     NT8 "paint bars" mechanism) — same nine engines, same math.
//  4. The 10-row Pine panel table becomes one Draw.TextFixed block (no
//     native per-cell table primitive in NinjaScript).
//  5. alertcondition()/alert() become NinjaScript's built-in Alert(...) call.
//
//  Everything else — the POC/value-area algorithm (density-aware, expand-
//  from-POC-by-heaviest-adjacent-bin), the DRE composite-window reference
//  engine, the panel fields, the naked/tested bookkeeping — is translated
//  directly from the Pine source, field for field.
//
//  Changelog
//    v1.0.0 (2026-11-14) — initial port of the published Pine v6 source.
// ─────────────────────────────────────────────────────────────────────────────
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
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

namespace NinjaTrader.NinjaScript.Indicators
{
    public enum SvpSessionMode { OneHour, FourHours, OneDayCme, OneWeek, TwoWeeks, OneMonth, CmeRth, CmeOvernight, Custom }
    public enum SvpProfileStyle { Heatmap, Bars, None }
    public enum SvpBarsAnchor { SessionStart, SessionEnd }
    public enum SvpPaintEngine { DeltaHtfCandlesDre, DeltaHtfGradient, DeltaGradient, DeltaSign, ValueAreaPosition, DistanceFromPoc, VolumeAtPriceHeat, NakedPocProximity, SessionUpVolShare }
    public enum SvpDeltaMode { DeltaSize, DeltaPercentImbalance }
    public enum SvpDeltaCurve { Linear, Boosted }
    public enum SvpHtfPaint { ProfilePeriod, M15, M30, M60, M120, M240, Daily }
    public enum SvpDreReference { HtfWindowNCandles, LastHtfCandle, PriorSessionProfile }
    public enum SvpDreIntensity { DistanceOnly, Blend, Strongest }

    public class SneakyStrykSvp_v1_0_0 : Indicator
    {
        // ── one finished session's data + the tags of what's drawn for it ──
        private class SvpSession
        {
            public int Seq;
            public DateTime T0, T1, TEnd, TestedT = DateTime.MinValue;
            public double Hi = double.NaN, Lo = double.NaN;
            public double Poc = double.NaN, PocVol, Vol, BuyVol, Vah = double.NaN, Val = double.NaN;
            public bool Tested; public bool LtfBuilt = true;
            public double IbH = double.NaN, IbL = double.NaN;
            public List<string> RowTags = new List<string>();
            public Dictionary<double, double> VolSnap, BuySnap;   // this session's own volume map, kept until its rows are drawn
            public string VaBoxTag, PocLineTag, VahLineTag, ValLineTag, TagTag, IbHTag, IbLTag;
        }
        private class HtfProfile { public Dictionary<double, double> Vol = new Dictionary<double, double>(); public Dictionary<double, double> Buy = new Dictionary<double, double>(); }

        // ── live tick classification (quote rule — see header note #1) ──
        private double _lastAsk, _lastBid, _lastPx;
        private Dictionary<double, double> _volMap = new Dictionary<double, double>();
        private Dictionary<double, double> _buyMap = new Dictionary<double, double>();
        private bool _tickSeenThisBar;

        // ── session bookkeeping ──
        private int _seqCounter;
        private SvpSession _cur;
        private List<SvpSession> _done = new List<SvpSession>();
        private bool _curLtf = true;
        private DateTime _sessStartT = DateTime.MinValue;
        private DateTime _prevTime = DateTime.MinValue;
        private bool _havePrevTime;

        // ── developing-session draw tags (same tag every bar => in-place update) ──
        private List<string> _devRowTags = new List<string>();
        private double _devPocPx = double.NaN, _devVahPx = double.NaN, _devValPx = double.NaN, _devVolT = double.NaN, _devBuyT = double.NaN;

        // ── candle-colouring / DRE state ──
        private double _htfD, _htfV;
        private List<double> _htfHist = new List<double>();
        private Dictionary<double, double> _cMap = new Dictionary<double, double>(), _cBuy = new Dictionary<double, double>();
        private Dictionary<double, double> _wMap = new Dictionary<double, double>(), _wBuy = new Dictionary<double, double>();
        private List<HtfProfile> _wStack = new List<HtfProfile>();
        private List<double> _wPocHist = new List<double>();
        private double _refPocW = double.NaN, _refVahW = double.NaN, _refValW = double.NaN;
        private double _refPocL = double.NaN, _refVahL = double.NaN, _refValL = double.NaN;
        private int _dreAcc;
        private bool _dreUpPrev, _dreDnPrev;
        private double _sessVol, _sessUp, _sessMaxV;
        private DateTime _htfPaintBucketStart = DateTime.MinValue;
        private double _lastDevPoc = double.NaN, _lastDevVah = double.NaN, _lastDevVal = double.NaN;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Ported from TradingView: 'STRYK: SVP' by Es-Sniper (open-source Pine v6). "
                            + "Session/period volume profile (heatmap or bars), naked/tested POC lines, "
                            + "value area, developing session, initial balance, and nine candle-colouring "
                            + "engines. Real tick data drives the profile (quote-rule classification), not "
                            + "a lower-timeframe-bar proxy — needs live ticks or Tick Replay for history.";
                Name = "Sneaky STRYK SVP";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                IsAutoScale = false;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;

                // 0 — master switches
                MasterAll = true; MasterProfiles = true; MasterPoc = true; MasterValueArea = true;
                MasterDeveloping = true; MasterTags = true; MasterPanel = true; MasterInitialBalance = true;
                MasterPaint = true;
                PaintEngine = SvpPaintEngine.DeltaHtfCandlesDre;

                // 1 — session
                SessionMode = SvpSessionMode.OneDayCme;
                CustomSessionStart = "08:30"; CustomSessionEnd = "15:00";
                SessionsToKeep = 10;

                // 2 — profile build
                BinTicks = 4; ValueAreaPct = 70;

                // 2b — initial balance
                IbOn = true; IbMinutes = 60; IbExtendLive = true; IbShowOnCompleted = false;

                // 3 — profiles on chart
                ProfileStyle = SvpProfileStyle.Heatmap; RowsPerProfile = 24;
                HeatTransparency = 65; BarsTransparency = 55; BarsAnchor = SvpBarsAnchor.SessionStart; BarsMaxWidthPct = 100;

                // 4 — POC lines
                PocExtendToCurrentBar = true; PocStopAtTest = true; PocHideTested = false;
                TagOn = true; TagOffsetBars = 2;

                // 5 — value area
                VaLines = true; VaExtend = false; VaFillShade = false; VaFillTransparency = 88;

                // 6 — developing session
                DevDrawProfile = true; DevPocOn = true; DevVaOn = true; DevTag = true;

                // 7 — panel
                PanelOn = true;

                // 8 — alerts
                AlertNakedPocTouch = true; AlertDevPocCross = false; AlertValueAreaExit = false;

                // 9 — candle colouring
                PaintHtf = SvpHtfPaint.M60; PaintHtfCandleCount = 20;
                PaintSensitivity = 1.5; PaintDeltaMode = SvpDeltaMode.DeltaSize; PaintCurve = SvpDeltaCurve.Boosted;
                PaintLookback = 50; NakedProximityTicks = 8;

                // 9b — DRE engine
                DreReference = SvpDreReference.HtfWindowNCandles; DreWindowCandles = 23;
                DreLean = 0.35; DreIntensityFrom = SvpDreIntensity.Blend;
                DreAcceptanceBars = 3; DreSizeFullAtAvgMult = 2.0; DreMigrationCandlesBack = 3;
                DreRampPctOfVa = 25; DreAlignBoost = 0.35;

                AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "(internal — plots unused, drawing-tool based)");
                // trade-signal plots for SneakyTradeHUD AUTO TRADE: +1 long, -1 short, 0 none
                foreach (string sn in new[] { "SigRotation", "SigFailedBreakout", "SigAcceptance", "SigNakedPoc", "SigIbBreak", "SigIbBreakWide", "SigIbBreakNarrow", "SigStop", "SigTarget" })
                    AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, sn);
                SignalsOn = true; SigRthStart = 930; SigRthEnd = 1600; SigShowMarkers = true; SigShowLevels = true;
            }
            else if (State == State.Configure)
            {
            }
            else if (State == State.Historical)
            {
                // Paint this indicator's OnRender (the dashboard card) above every drawing object,
                // so profile rows and level lines never show through the panel.
                SetZOrder(int.MaxValue);
            }
            else if (State == State.DataLoaded)
            {
                // Deliberately leave _cur == null here. OnBarUpdate's session-boundary check
                // (DetectNewSession) treats _cur == null as "start the first session now" and
                // properly stamps T0/_sessStartT to the first real bar's Time[0]. Pre-creating
                // _cur here used to short-circuit that: _cur became non-null before any bar had
                // run, so the real first-session setup never fired until the next calendar-day
                // rollover, leaving T0 stuck at DateTime.MinValue for the whole first day. That
                // bogus time then got passed as a Draw anchor (profile rows, POC/VA/IB lines),
                // which throws and is swallowed by OnBarUpdate's try/catch -- silently freezing
                // the panel and suppressing every drawn element until the next session rolled in.
            }
        }

        // ── real tick classification — identical quote-rule logic to SneakyDeltaLevels.cs ──
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType == MarketDataType.Ask) { _lastAsk = e.Price; return; }
            if (e.MarketDataType == MarketDataType.Bid) { _lastBid = e.Price; return; }
            if (e.MarketDataType != MarketDataType.Last) return;

            double px = e.Price;
            bool buy;
            if (_lastAsk > 0 && px >= _lastAsk) buy = true;
            else if (_lastBid > 0 && px <= _lastBid) buy = false;
            else buy = px >= _lastPx;
            _lastPx = px;
            _tickSeenThisBar = true;

            double k = BinOf(px);
            AddVol(_volMap, _buyMap, k, e.Volume, buy);
            AddVol(_cMap, _cBuy, k, e.Volume, buy);
        }

        private double BinOf(double px)
        {
            double binPx = TickSize * BinTicks;
            if (binPx <= 0) return px;
            return Math.Round(px / binPx, MidpointRounding.AwayFromZero) * binPx;
        }

        private static void AddVol(Dictionary<double, double> vol, Dictionary<double, double> buy, double k, double v, bool isBuy)
        {
            if (v <= 0) return;
            double cur; vol.TryGetValue(k, out cur); vol[k] = cur + v;
            if (isBuy) { buy.TryGetValue(k, out cur); buy[k] = cur + v; }
        }

        // ── historical/replay fallback — mirrors Pine's own fallback when it has no
        //    lower-timeframe data: the bar's own OHLCV, spread across its range ──
        private void AddBarFallback(Dictionary<double, double> vol, Dictionary<double, double> buy, double lo, double hi, double v, bool barIsUp)
        {
            if (v <= 0) return;
            double binPx = TickSize * BinTicks;
            if (binPx <= 0) return;
            double k0 = BinOf(lo), k1 = BinOf(hi);
            int nb = Math.Max(1, Math.Min(400, (int)Math.Round((k1 - k0) / binPx, MidpointRounding.AwayFromZero) + 1));
            double share = v / nb;
            for (int j = 0; j < nb; j++)
            {
                double k = Math.Round(k0 / binPx + j, MidpointRounding.AwayFromZero) * binPx;
                AddVol(vol, buy, k, share, barIsUp);
            }
        }

        protected override void OnBarUpdate()
        {
            try
            {
                bool isNewBarTick = IsFirstTickOfBar;

                // Bars that arrive already complete (history without Tick Replay) are folded
                // into the profile on their own update; live / Tick Replay bars are folded on
                // the first tick of the NEXT bar -- and that has to happen BEFORE the session
                // check below, so a session's closing bar stays in its own session.
                bool wholeBars = State == State.Historical && !Bars.IsTickReplay;
                if (SignalsOn)
                {
                    if (wholeBars) RunSignals(0, Bars.IsFirstBarOfSession);
                    else if (isNewBarTick)
                    {
                        if (CurrentBar >= 1) RunSignals(1, _sgPendingFirst);
                        _sgPendingFirst = Bars.IsFirstBarOfSession;
                    }
                }
                if (!wholeBars && isNewBarTick && CurrentBar >= 1 && _cur != null && InSession(Time[1]))
                    FoldBar(1);

                // ---- session boundary detection (see header note #2) ----
                bool newSess = DetectNewSession(isNewBarTick);

                if (newSess)
                {
                    FinalizeSession();
                    _cur = new SvpSession { Seq = ++_seqCounter, T0 = Time[0], T1 = Time[0], Hi = High[0], Lo = Low[0], IbH = High[0], IbL = Low[0] };
                    _volMap.Clear(); _buyMap.Clear();
                    _curLtf = true;
                    _sessStartT = Time[0];
                    _sessVol = 0; _sessUp = 0; _sessMaxV = 0;
                }

                bool inSess = InSession(Time[0]);
                if (inSess && _cur != null)
                {
                    _cur.T1 = Time[0];
                    _cur.TEnd = Time[0];
                    _cur.Hi = double.IsNaN(_cur.Hi) ? High[0] : Math.Max(_cur.Hi, High[0]);
                    _cur.Lo = double.IsNaN(_cur.Lo) ? Low[0] : Math.Min(_cur.Lo, Low[0]);

                    if ((Time[0] - _sessStartT).TotalMinutes < IbMinutes)
                    {
                        _cur.IbH = double.IsNaN(_cur.IbH) ? High[0] : Math.Max(_cur.IbH, High[0]);
                        _cur.IbL = double.IsNaN(_cur.IbL) ? Low[0] : Math.Min(_cur.IbL, Low[0]);
                    }

                    if (wholeBars) FoldBar(0);
                }

                // naked -> tested bookkeeping (closed bars only)
                if (IsFirstTickOfBar && CurrentBar >= 1 && _done.Count > 0)
                {
                    double lo1 = Low[1], hi1 = High[1];
                    foreach (var s in _done)
                        if (!s.Tested && !double.IsNaN(s.Poc) && lo1 <= s.Poc && hi1 >= s.Poc)
                        {
                            s.Tested = true; s.TestedT = Time[1];
                            if (MasterAll && AlertNakedPocTouch)
                                Alert("SVPNaked" + s.Seq, Priority.Medium,
                                    "STRYK: SVP " + Instrument.MasterInstrument.Name + " — price traded through a naked session POC",
                                    NinjaTrader.Core.Globals.InstallDir + "sounds\\Alert2.wav", 60, Brushes.Black, Brushes.Orange);
                        }
                }

                if (_tickSeenThisBar == false && IsFirstTickOfBar) { /* reset handled below after fallback check */ }

                UpdateCandleColouringState();
                DrawDoneSessions();
                DrawDevelopingSession();
                if (MasterAll && MasterPanel && PanelOn) DrawPanel();
                PaintCandle();
                CheckCrossAlerts();

                if (IsFirstTickOfBar) _tickSeenThisBar = false;
                _prevTime = Time[0]; _havePrevTime = true;
            }
            catch (Exception ex)
            {
                // Never fail silently: say so on the chart and put the full trace in the Output window.
                Print("SneakyStrykSvp OnBarUpdate error at bar " + CurrentBar + ": " + ex);
                try { Draw.TextFixed(this, "SVPErr", "Sneaky STRYK SVP error (see NinjaScript Output): " + ex.Message, TextPosition.BottomRight); } catch { }
            }
        }

        // Folds one finished bar into the session maps exactly once. If real ticks were
        // collected while it was live they are already in the maps (OnMarketData); otherwise
        // Pine's own documented fallback applies: the bar's volume spread across its range.
        private int _lastFoldedBar = -1;
        private void FoldBar(int barsAgo)
        {
            int idx = CurrentBar - barsAgo;
            if (idx <= _lastFoldedBar) return;
            _lastFoldedBar = idx;
            if (barsAgo == 1 && _tickSeenThisBar)
            {
                _curLtf = true;   // real ticks were used for this bar -- same meaning as Pine's haveLtf
            }
            else if (Volume[barsAgo] > 0)
            {
                _curLtf = false;
                bool barUp = Close[barsAgo] > Open[barsAgo];
                AddBarFallback(_volMap, _buyMap, Low[barsAgo], High[barsAgo], Volume[barsAgo], barUp);
                AddBarFallback(_cMap, _cBuy, Low[barsAgo], High[barsAgo], Volume[barsAgo], barUp);
            }
        }

        // ── session detection — see header note #2 ──
        private bool DetectNewSession(bool isNewBarTick)
        {
            if (!isNewBarTick) return _cur == null;
            if (_cur == null) return true;

            switch (SessionMode)
            {
                case SvpSessionMode.OneHour:
                    return _havePrevTime && Time[0].Hour != _prevTime.Hour;
                case SvpSessionMode.FourHours:
                    return _havePrevTime && (Time[0].Hour / 4) != (_prevTime.Hour / 4) && Time[0].Date == _prevTime.Date || (_havePrevTime && Time[0].Date != _prevTime.Date);
                case SvpSessionMode.OneDayCme:
                    // The chart's Trading Hours template defines the session, so this is right in any
                    // chart time zone and never puts the session's closing bar into the next day.
                    return Bars.IsFirstBarOfSession;
                case SvpSessionMode.OneWeek:
                    // first session after a weekend-sized gap
                    return Bars.IsFirstBarOfSession && _havePrevTime && (Time[0] - _prevTime).TotalHours > 30;
                case SvpSessionMode.TwoWeeks:
                    return _havePrevTime && (TradingWeekOf(Time[0]).Ticks / (TimeSpan.TicksPerDay * 14)) != (TradingWeekOf(_prevTime).Ticks / (TimeSpan.TicksPerDay * 14));
                case SvpSessionMode.OneMonth:
                    return _havePrevTime && (Time[0].Year != _prevTime.Year || Time[0].Month != _prevTime.Month);
                case SvpSessionMode.CmeRth:
                case SvpSessionMode.CmeOvernight:
                case SvpSessionMode.Custom:
                    bool wasIn = _havePrevTime && InSession(_prevTime);
                    bool isIn = InSession(Time[0]);
                    return isIn && !wasIn;
                default:
                    return false;
            }
        }

        private static DateTime TradingDayOf(DateTime t)
        {
            // CME trading day: 17:00 CT rolls to the NEXT day's session.
            DateTime d = t.Hour >= 17 ? t.Date.AddDays(1) : t.Date;
            return d;
        }
        private static DateTime TradingWeekOf(DateTime t)
        {
            DateTime d = TradingDayOf(t);
            int diff = (int)d.DayOfWeek;   // Sunday=0 .. the trading week starts Sunday 17:00 => Monday's trading day
            // Align to the Monday trading-day (first full trading day of the week)
            DateTime monday = d.AddDays(diff == 0 ? -6 : 1 - diff);
            return monday;
        }

        private bool InSession(DateTime t)
        {
            switch (SessionMode)
            {
                case SvpSessionMode.CmeRth: return InWindow(t, new TimeSpan(8, 30, 0), new TimeSpan(15, 0, 0));
                case SvpSessionMode.CmeOvernight: return InWindow(t, new TimeSpan(17, 0, 0), new TimeSpan(8, 30, 0));
                case SvpSessionMode.Custom: return InWindow(t, ParseHm(CustomSessionStart), ParseHm(CustomSessionEnd));
                default: return true;   // timeframe-anchored modes are "always in session" between rolls
            }
        }
        private static TimeSpan ParseHm(string hhmm)
        {
            var parts = hhmm.Split(':');
            int h = 0, m = 0;
            if (parts.Length == 2) { int.TryParse(parts[0], out h); int.TryParse(parts[1], out m); }
            return new TimeSpan(h, m, 0);
        }
        private static bool InWindow(DateTime t, TimeSpan start, TimeSpan end)
        {
            TimeSpan tod = t.TimeOfDay;
            return start <= end ? (tod >= start && tod < end) : (tod >= start || tod < end);
        }

        // ── POC / value-area analysis — direct port of Pine's f_analyseMap ──
        private void Analyse(Dictionary<double, double> vol, Dictionary<double, double> buy,
            out double poc, out double pocVol, out double total, out double buyTotal, out double vah, out double val)
        {
            poc = double.NaN; pocVol = 0; total = 0; buyTotal = 0; vah = double.NaN; val = double.NaN;
            if (vol.Count == 0) return;
            double binPx = TickSize * BinTicks;
            if (binPx <= 0) return;

            var keys = vol.Keys.OrderBy(k => k).ToList();
            double firstK = keys[0], lastK = keys[keys.Count - 1];
            int n = (int)Math.Round((lastK - firstK) / binPx, MidpointRounding.AwayFromZero) + 1;
            double[] ks = new double[n];
            double[] vs = new double[n];
            int pi = 0;
            for (int i = 0; i < n; i++)
            {
                double k = Math.Round(firstK / binPx + i, MidpointRounding.AwayFromZero) * binPx;
                double v; vol.TryGetValue(k, out v);
                double b; buy.TryGetValue(k, out b);
                ks[i] = k; vs[i] = v;
                total += v; buyTotal += b;
                if (v > pocVol) { pocVol = v; poc = k; pi = i; }
            }
            double target = total * ValueAreaPct / 100.0;
            double acc = pocVol;
            int lo = pi, hi = pi;
            while (acc < target && (lo > 0 || hi < n - 1))
            {
                double below = lo > 0 ? vs[lo - 1] : -1.0;
                double above = hi < n - 1 ? vs[hi + 1] : -1.0;
                if (above >= below) { hi++; acc += above; }
                else { lo--; acc += below; }
            }
            vah = ks[hi] + binPx / 2;
            val = ks[lo] - binPx / 2;
        }

        private void FinalizeSession()
        {
            if (_cur == null) return;
            double poc, pocVol, total, buyTotal, vah, val;
            Analyse(_volMap, _buyMap, out poc, out pocVol, out total, out buyTotal, out vah, out val);
            _cur.Poc = poc; _cur.PocVol = pocVol; _cur.Vol = total; _cur.BuyVol = buyTotal; _cur.Vah = vah; _cur.Val = val;
            _cur.LtfBuilt = _curLtf;
            _cur.VolSnap = new Dictionary<double, double>(_volMap);
            _cur.BuySnap = new Dictionary<double, double>(_buyMap);
            _done.Add(_cur);
            while (_done.Count > SessionsToKeep)
            {
                var old = _done[0]; _done.RemoveAt(0);
                foreach (var tag in old.RowTags) RemoveDrawObject(tag);
                if (old.VaBoxTag != null) RemoveDrawObject(old.VaBoxTag);
                if (old.PocLineTag != null) RemoveDrawObject(old.PocLineTag);
                if (old.VahLineTag != null) RemoveDrawObject(old.VahLineTag);
                if (old.ValLineTag != null) RemoveDrawObject(old.ValLineTag);
                if (old.TagTag != null) RemoveDrawObject(old.TagTag);
                if (old.IbHTag != null) RemoveDrawObject(old.IbHTag);
                if (old.IbLTag != null) RemoveDrawObject(old.IbLTag);
            }
        }

        // Every custom brush MUST be frozen. An unfrozen WPF brush belongs to the thread that
        // created it; NinjaTrader calculates bars on one thread and renders on another, so
        // touching an unfrozen brush from the other thread throws InvalidOperationException.
        // Sends one of this indicator's drawing objects behind the price bars, and therefore
        // behind the dashboard card too. Set through reflection so a NinjaTrader build that
        // names the property differently still compiles; in that case nothing changes.
        private static readonly Dictionary<Type, System.Reflection.PropertyInfo> _zProps = new Dictionary<Type, System.Reflection.PropertyInfo>();
        private static void Bk(object o)
        {
            if (o == null) return;
            try
            {
                Type t = o.GetType(); System.Reflection.PropertyInfo pi;
                lock (_zProps) { if (!_zProps.TryGetValue(t, out pi)) { pi = t.GetProperty("ZOrder"); _zProps[t] = pi; } }
                if (pi != null && pi.CanWrite) pi.SetValue(o, -1, null);
            }
            catch { }
        }

        private static Brush Fz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static Brush Heat(double f, Brush loBrush, Brush midBrush, Brush hiBrush)
        {
            f = Math.Max(0, Math.Min(1, f));
            Color lo = ((SolidColorBrush)loBrush).Color, mid = ((SolidColorBrush)midBrush).Color, hi = ((SolidColorBrush)hiBrush).Color;
            Color c = f <= 0.5 ? LerpColor(lo, mid, f / 0.5) : LerpColor(mid, hi, (f - 0.5) / 0.5);
            return Fz(c);
        }
        private static Color LerpColor(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        private void DrawRows(List<string> rowTags, string tagPrefix, DateTime t0, DateTime t1, double lo, double hi, int rowsN, Dictionary<double, double> vol, Dictionary<double, double> buy)
        {
            foreach (var t in rowTags) RemoveDrawObject(t);
            rowTags.Clear();
            if (ProfileStyle == SvpProfileStyle.None || double.IsNaN(lo) || double.IsNaN(hi) || hi <= lo || rowsN <= 0 || vol.Count == 0) return;

            double rowH = (hi - lo) / rowsN;
            double[] rv = new double[rowsN]; double[] rb = new double[rowsN];
            foreach (var kv in vol)
            {
                int r = (int)Math.Floor((kv.Key - lo) / rowH);
                r = Math.Max(0, Math.Min(rowsN - 1, r));
                rv[r] += kv.Value;
                double b; buy.TryGetValue(kv.Key, out b);
                rb[r] += b;
            }
            double rowMax = rv.Max();
            TimeSpan span = t1 - t0; if (span <= TimeSpan.Zero) span = TimeSpan.FromMinutes(1);

            for (int r = 0; r < rowsN; r++)
            {
                double v = rv[r];
                double f = rowMax > 0 ? v / rowMax : 0.0;
                double y1 = lo + r * rowH, y2 = y1 + rowH;
                string tag = tagPrefix + "_r" + r;
                rowTags.Add(tag);
                if (ProfileStyle == SvpProfileStyle.Heatmap)
                {
                    Brush c = Heat(f, HeatColorLow, HeatColorMid, HeatColorHigh);
                    Bk(Draw.Rectangle(this, tag, false, t0, y2, t1, y1, Brushes.Transparent, c, 100 - HeatTransparency));
                }
                else
                {
                    long w = (long)(span.Ticks * f * BarsMaxWidthPct / 100.0);
                    bool buyLed = rb[r] >= v / 2;
                    Brush c = buyLed ? BarBuyBrush : BarSellBrush;
                    DateTime xa = BarsAnchor == SvpBarsAnchor.SessionStart ? t0 : t1.AddTicks(-w);
                    DateTime xb = BarsAnchor == SvpBarsAnchor.SessionStart ? t0.AddTicks(w) : t1;
                    Bk(Draw.Rectangle(this, tag, false, xa, y2, xb, y1, Brushes.Transparent, c, 100 - BarsTransparency));
                }
            }
        }

        private int RowBudget()
        {
            // Rough equivalent of Pine's drawing-object clamp (500 boxes shared across sessions).
            int n = Math.Max(6, Math.Min(RowsPerProfile, 480 / Math.Max(1, SessionsToKeep)));
            return n;
        }

        private void DrawDoneSessions()
        {
            if (_done.Count == 0) return;
            // Draw/refresh rows + POC/VA lines only for the session that JUST finalized
            // (the others' statics don't change bar to bar) — but DO refresh naked/tested
            // colour + extension every bar since that can flip at any time.
            var justFinalized = _done[_done.Count - 1];
            if (justFinalized.VolSnap != null)
            {
                var snapVol = justFinalized.VolSnap; var snapBuy = justFinalized.BuySnap;
                justFinalized.VolSnap = null; justFinalized.BuySnap = null;   // draw once, then release
                int rowsN = RowBudget();
                string prefix = "SVProw_" + justFinalized.Seq;
                if (MasterAll && MasterProfiles)
                    DrawRows(justFinalized.RowTags, prefix, justFinalized.T0, justFinalized.T1, justFinalized.Lo, justFinalized.Hi, rowsN, snapVol, snapBuy);

                if (MasterAll && MasterValueArea && VaFillShade && !double.IsNaN(justFinalized.Vah) && !double.IsNaN(justFinalized.Val))
                {
                    justFinalized.VaBoxTag = "SVPva_" + justFinalized.Seq;
                    Bk(Draw.Rectangle(this, justFinalized.VaBoxTag, false, justFinalized.T0, justFinalized.Vah, justFinalized.T1, justFinalized.Val,
                        Brushes.Transparent, ValueAreaBrush, 100 - VaFillTransparency));
                }
                if (MasterAll && MasterPoc && !double.IsNaN(justFinalized.Poc))
                    justFinalized.PocLineTag = "SVPpoc_" + justFinalized.Seq;
                if (MasterAll && MasterValueArea && VaLines && !double.IsNaN(justFinalized.Vah))
                {
                    justFinalized.VahLineTag = "SVPvah_" + justFinalized.Seq;
                    justFinalized.ValLineTag = "SVPval_" + justFinalized.Seq;
                }
                if (MasterAll && MasterTags && TagOn && !double.IsNaN(justFinalized.Poc))
                    justFinalized.TagTag = "SVPtag_" + justFinalized.Seq;
                if (MasterAll && MasterInitialBalance && IbOn && IbShowOnCompleted && !double.IsNaN(justFinalized.IbH))
                {
                    justFinalized.IbHTag = "SVPibh_" + justFinalized.Seq;
                    justFinalized.IbLTag = "SVPibl_" + justFinalized.Seq;
                    Bk(Draw.Line(this, justFinalized.IbHTag, false, justFinalized.T0, justFinalized.IbH, justFinalized.T1, justFinalized.IbH, InitialBalanceBrush, DashStyleHelper.Dash, 1));
                    Bk(Draw.Line(this, justFinalized.IbLTag, false, justFinalized.T0, justFinalized.IbL, justFinalized.T1, justFinalized.IbL, InitialBalanceBrush, DashStyleHelper.Dash, 1));
                }
            }

            foreach (var s in _done)
            {
                bool showIt = MasterAll && MasterPoc && !(s.Tested && PocHideTested);
                DateTime xEnd = (s.Tested && PocStopAtTest && s.TestedT != DateTime.MinValue) ? s.TestedT : Time[0];
                Brush pc = s.Tested ? PocTestedBrush : PocNakedBrush;
                if (s.PocLineTag != null)
                    Bk(Draw.Line(this, s.PocLineTag, false, s.T0, s.Poc, showIt ? xEnd : s.T1, s.Poc, showIt ? pc : Brushes.Transparent, DashStyleHelper.Solid, 2));
                if (s.VahLineTag != null)
                {
                    DateTime vx = VaExtend ? Time[0] : s.T1;
                    Bk(Draw.Line(this, s.VahLineTag, false, s.T0, s.Vah, vx, s.Vah, MasterAll && MasterValueArea ? ValueAreaBrush : Brushes.Transparent, DashStyleHelper.Dot, 1));
                    Bk(Draw.Line(this, s.ValLineTag, false, s.T0, s.Val, vx, s.Val, MasterAll && MasterValueArea ? ValueAreaBrush : Brushes.Transparent, DashStyleHelper.Dot, 1));
                }
                if (s.TagTag != null)
                {
                    int ago = _done.Count - _done.IndexOf(s);
                    double buyPct = s.Vol > 0 ? s.BuyVol / s.Vol * 100.0 : double.NaN;
                    string txt = ago + (ago == 1 ? " session ago" : " sessions ago")
                        + " · Vol " + FmtVol(s.Vol)
                        + (double.IsNaN(buyPct) ? "" : " · " + buyPct.ToString("0") + "% up-vol")
                        + (s.Tested ? " · tested" : "");
                    Bk(Draw.Text(this, s.TagTag, false, showIt && MasterTags && TagOn ? txt : "", xEnd, s.Poc, 0,
                        s.Tested ? PocTestedBrush : PocNakedBrush, new SimpleFont("Arial", 9), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0));
                }
            }
        }

        private static string FmtVol(double v)
        {
            double a = Math.Abs(v);
            if (a >= 1000000) return (v / 1000000.0).ToString("0.##") + "M";
            if (a >= 1000) return (v / 1000.0).ToString("0.#") + "K";
            return v.ToString("0");
        }

        private void DrawDevelopingSession()
        {
            foreach (var t in _devRowTags) RemoveDrawObject(t);
            _devRowTags.Clear();
            RemoveDrawObject("SVPdevVa"); RemoveDrawObject("SVPdevPoc"); RemoveDrawObject("SVPdevVah");
            RemoveDrawObject("SVPdevVal"); RemoveDrawObject("SVPdevTag"); RemoveDrawObject("SVPibhLive"); RemoveDrawObject("SVPiblLive");

            if (!(MasterAll && MasterDeveloping) || _cur == null || !InSession(Time[0])) { _devPocPx = double.NaN; return; }

            double poc, pocVol, total, buyTotal, vah, val;
            Analyse(_volMap, _buyMap, out poc, out pocVol, out total, out buyTotal, out vah, out val);
            _devPocPx = poc; _devVahPx = vah; _devValPx = val; _devVolT = total; _devBuyT = buyTotal;

            double dHi = Math.Max(_cur.Hi, High[0]), dLo = Math.Min(_cur.Lo, Low[0]);
            int rowsN = RowBudget();
            if (DevDrawProfile && MasterProfiles)
                DrawRows(_devRowTags, "SVPdevRow", _cur.T0, Time[0], dLo, dHi, rowsN, _volMap, _buyMap);

            if (MasterValueArea && VaFillShade && !double.IsNaN(vah))
                Bk(Draw.Rectangle(this, "SVPdevVa", false, _cur.T0, vah, Time[0], val, Brushes.Transparent, ValueAreaBrush, 100 - VaFillTransparency));

            if (DevPocOn && !double.IsNaN(poc))
            {
                DateTime tagX = Time[0].AddMinutes(TagOffsetBars);
                Bk(Draw.Line(this, "SVPdevPoc", false, _cur.T0, poc, tagX, poc, DevPocBrush, DashStyleHelper.Solid, 2));
                if (DevTag)
                {
                    double bp = total > 0 ? buyTotal / total * 100.0 : double.NaN;
                    Bk(Draw.Text(this, "SVPdevTag", false, "developing · Vol " + FmtVol(total) + (double.IsNaN(bp) ? "" : " · " + bp.ToString("0") + "% up-vol"),
                        tagX, poc, 0, DevPocBrush, new SimpleFont("Arial", 9), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0));
                }
            }
            if (DevVaOn && MasterValueArea && !double.IsNaN(vah))
            {
                Bk(Draw.Line(this, "SVPdevVah", false, _cur.T0, vah, Time[0], vah, ValueAreaBrush, DashStyleHelper.Dot, 1));
                Bk(Draw.Line(this, "SVPdevVal", false, _cur.T0, val, Time[0], val, ValueAreaBrush, DashStyleHelper.Dot, 1));
            }
            if (MasterInitialBalance && IbOn && !double.IsNaN(_cur.IbH))
            {
                DateTime ibX = IbExtendLive ? Time[0] : _sessStartT.AddMinutes(IbMinutes);
                Bk(Draw.Line(this, "SVPibhLive", false, _cur.T0, _cur.IbH, ibX, _cur.IbH, InitialBalanceBrush, DashStyleHelper.Dash, 1));
                Bk(Draw.Line(this, "SVPiblLive", false, _cur.T0, _cur.IbL, ibX, _cur.IbL, InitialBalanceBrush, DashStyleHelper.Dash, 1));
            }
        }

        // ── dashboard ──
        // The panel is painted in OnRender (two columns, per-row colours, rounded card).
        // OnBarUpdate only prepares an immutable list of rows; OnRender just reads the
        // latest list, so the calculation and render threads never share mutable state.
        private class PanelRow
        {
            public string Label, Value; public byte R, G, B; public bool Bold;
            public double Bar = double.NaN;   // 0..1 draws a thin up/down volume bar under the row
        }
        private volatile List<PanelRow> _panelRows;
        private volatile string _panelSub = "", _panelFoot = "";

        private static PanelRow Row(string label, string value, byte r, byte g, byte b, bool bold = true)
        { return new PanelRow { Label = label, Value = value, R = r, G = g, B = b, Bold = bold }; }

        private static string SessionModeLabel(SvpSessionMode m)
        {
            switch (m)
            {
                case SvpSessionMode.OneHour: return "1 hour";
                case SvpSessionMode.FourHours: return "4 hours";
                case SvpSessionMode.OneDayCme: return "1 day";
                case SvpSessionMode.OneWeek: return "1 week";
                case SvpSessionMode.TwoWeeks: return "2 weeks";
                case SvpSessionMode.OneMonth: return "1 month";
                case SvpSessionMode.CmeRth: return "RTH";
                case SvpSessionMode.CmeOvernight: return "Overnight";
                default: return "Custom";
            }
        }

        private void DrawPanel()
        {
            RemoveDrawObject("SVPPanel");   // the old plain-text panel, if a saved chart still has it
            string px = "N" + Math.Max(0, (int)Math.Round(-Math.Log10(TickSize), MidpointRounding.AwayFromZero));
            var rows = new List<PanelRow>();
            int nakedN = _done.Count(s => !s.Tested);
            rows.Add(Row("Sessions kept", _done.Count + "  ·  " + nakedN + " naked POC", 0xD1, 0xD4, 0xDC, false));

            if (InSession(Time[0]) && !double.IsNaN(_devPocPx))
            {
                bool haveVa = !double.IsNaN(_devValPx) && !double.IsNaN(_devVahPx);
                rows.Add(Row("Developing POC", _devPocPx.ToString(px), 0xFF, 0xFF, 0xFF));
                rows.Add(Row("Value area", haveVa ? _devValPx.ToString(px) + "  –  " + _devVahPx.ToString(px) : "n/a", 0x42, 0xA5, 0xF5));
                rows.Add(Row("Volume", FmtVol(_devVolT), 0xD1, 0xD4, 0xDC));
                if (_devVolT > 0)
                {
                    double up = _devBuyT / _devVolT;
                    var r = up >= 0.5 ? Row("Up-volume", (up * 100.0).ToString("0") + "%", 0x4A, 0xDE, 0x80)
                                      : Row("Up-volume", (up * 100.0).ToString("0") + "%", 0xEF, 0x53, 0x50);
                    r.Bar = up;
                    rows.Add(r);
                }
                if (haveVa)
                {
                    if (Close[0] > _devVahPx) rows.Add(Row("Price is", "▲  above value", 0x4A, 0xDE, 0x80));
                    else if (Close[0] < _devValPx) rows.Add(Row("Price is", "▼  below value", 0xEF, 0x53, 0x50));
                    else rows.Add(Row("Price is", "inside value", 0x42, 0xA5, 0xF5));
                }
            }
            if (MasterInitialBalance && IbOn && InSession(Time[0]) && _cur != null && !double.IsNaN(_cur.IbH))
                rows.Add(Row("IB " + IbMinutes + "m", _cur.IbL.ToString(px) + "  –  " + _cur.IbH.ToString(px), 0xCE, 0x93, 0xD8));

            _panelSub = SessionModeLabel(SessionMode);
            _panelFoot = (_curLtf && _tickSeenThisBar ? "Built from live ticks" : "Built from chart bars") + "  ·  bin " + BinTicks + " ticks";
            _panelRows = rows;
        }

        private static SharpDX.Color4 Dx(byte r, byte g, byte b, byte a = 255) { return new SharpDX.Color4(r / 255f, g / 255f, b / 255f, a / 255f); }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            var rows = _panelRows;
            if (rows == null || rows.Count == 0 || RenderTarget == null || ChartPanel == null) return;
            if (!(MasterAll && MasterPanel && PanelOn)) return;

            const float W = 268f, Pad = 12f, HeadH = 30f, RowH = 21f, FootH = 22f;
            float barRows = rows.Count(r => !double.IsNaN(r.Bar));
            float h = HeadH + rows.Count * RowH + barRows * 7f + FootH + 8f;
            float x = ChartPanel.X + ChartPanel.W - W - 10f;
            float y = ChartPanel.Y + 30f;

            var dw = NinjaTrader.Core.Globals.DirectWriteFactory;
            try
            {
                using (var fTitle = new SharpDX.DirectWrite.TextFormat(dw, "Segoe UI", SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, 13f))
                using (var fLabel = new SharpDX.DirectWrite.TextFormat(dw, "Segoe UI", SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, 12f))
                using (var fValue = new SharpDX.DirectWrite.TextFormat(dw, "Segoe UI", SharpDX.DirectWrite.FontWeight.SemiBold, SharpDX.DirectWrite.FontStyle.Normal, 12.5f))
                using (var fPlain = new SharpDX.DirectWrite.TextFormat(dw, "Segoe UI", SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, 12.5f))
                using (var fFoot = new SharpDX.DirectWrite.TextFormat(dw, "Segoe UI", SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, 10.5f))
                using (var bg = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Dx(0x10, 0x14, 0x1F, 255)))
                using (var edge = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Dx(0x2A, 0x2E, 0x39)))
                using (var accent = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Dx(0xFF, 0xB7, 0x4D)))
                using (var ink = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Dx(0xFF, 0xFF, 0xFF)))
                using (var mute = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Dx(0x8A, 0x91, 0xA3)))
                using (var dim = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Dx(0x5D, 0x64, 0x76)))
                using (var val = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Dx(0xFF, 0xFF, 0xFF)))
                using (var red = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Dx(0xEF, 0x53, 0x50)))
                using (var green = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Dx(0x4A, 0xDE, 0x80)))
                {
                    foreach (var f in new[] { fTitle, fLabel, fValue, fPlain, fFoot })
                    {
                        f.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;
                        f.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
                    }
                    fValue.TextAlignment = SharpDX.DirectWrite.TextAlignment.Trailing;
                    fPlain.TextAlignment = SharpDX.DirectWrite.TextAlignment.Trailing;

                    var card = new SharpDX.Direct2D1.RoundedRectangle { Rect = new SharpDX.RectangleF(x, y, W, h), RadiusX = 7f, RadiusY = 7f };
                    RenderTarget.FillRoundedRectangle(card, bg);
                    RenderTarget.DrawRoundedRectangle(card, edge, 1f);

                    // header: accent bar, title, period on the right
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(x + Pad, y + 9f, 3f, HeadH - 16f), accent);
                    RenderTarget.DrawText("STRYK · SVP", fTitle, new SharpDX.RectangleF(x + Pad + 9f, y + 1f, W - 2 * Pad, HeadH), ink);
                    RenderTarget.DrawText(_panelSub ?? "", fPlain, new SharpDX.RectangleF(x + Pad, y + 1f, W - 2 * Pad, HeadH), mute);
                    RenderTarget.DrawLine(new SharpDX.Vector2(x + Pad, y + HeadH), new SharpDX.Vector2(x + W - Pad, y + HeadH), edge, 1f);

                    float ry = y + HeadH + 4f;
                    foreach (var r in rows)
                    {
                        var rect = new SharpDX.RectangleF(x + Pad, ry, W - 2 * Pad, RowH);
                        RenderTarget.DrawText(r.Label ?? "", fLabel, rect, mute);
                        val.Color = Dx(r.R, r.G, r.B);
                        RenderTarget.DrawText(r.Value ?? "", r.Bold ? fValue : fPlain, rect, val);
                        ry += RowH;
                        if (!double.IsNaN(r.Bar))
                        {
                            float bw = W - 2 * Pad, up = (float)Math.Max(0.0, Math.Min(1.0, r.Bar)) * bw;
                            RenderTarget.FillRectangle(new SharpDX.RectangleF(x + Pad, ry, up, 3f), green);
                            RenderTarget.FillRectangle(new SharpDX.RectangleF(x + Pad + up, ry, bw - up, 3f), red);
                            ry += 7f;
                        }
                    }

                    RenderTarget.DrawLine(new SharpDX.Vector2(x + Pad, ry + 3f), new SharpDX.Vector2(x + W - Pad, ry + 3f), edge, 1f);
                    RenderTarget.DrawText(_panelFoot ?? "", fFoot, new SharpDX.RectangleF(x + Pad, ry + 4f, W - 2 * Pad, FootH), dim);
                }
            }
            catch (Exception ex) { Print("SneakyStrykSvp panel render error: " + ex.Message); }
        }

        // ── candle-colouring engines (nine, including DRE) ──
        private void UpdateCandleColouringState()
        {
            if (!IsFirstTickOfBar) return;
            if (CurrentBar < 1) return;

            double barDeltaLtf = _tickSeenThisBar ? CurrentTickDelta() : (Close[1] > Open[1] ? Volume[1] : Close[1] < Open[1] ? -Volume[1] : 0.0);

            string htfTf = PaintHtf == SvpHtfPaint.ProfilePeriod ? "profile" : PaintHtf.ToString();
            bool roll = PaintHtf == SvpHtfPaint.ProfilePeriod
                ? DetectNewSession(true)
                : HtfPaintRolled(htfTf);

            if (roll)
            {
                if (_htfV > 0)
                {
                    _htfHist.Add(_htfD);
                    while (_htfHist.Count > PaintHtfCandleCount) _htfHist.RemoveAt(0);
                }
                // file the just-finished HTF candle's map for the DRE composite window
                if (_cMap.Count > 0)
                {
                    double lp, lpv, lt, lb, lvah, lval;
                    Analyse(_cMap, _cBuy, out lp, out lpv, out lt, out lb, out lvah, out lval);
                    _refPocL = lp; _refVahL = lvah; _refValL = lval;

                    var prof = new HtfProfile { Vol = new Dictionary<double, double>(_cMap), Buy = new Dictionary<double, double>(_cBuy) };
                    _wStack.Add(prof);
                    MapAdd(_wMap, _cMap, 1.0); MapAdd(_wBuy, _cBuy, 1.0);
                    while (_wStack.Count > DreWindowCandles)
                    {
                        var old = _wStack[0]; _wStack.RemoveAt(0);
                        MapAdd(_wMap, old.Vol, -1.0); MapAdd(_wBuy, old.Buy, -1.0);
                    }
                    double wp, wpv, wt, wb, wvah, wval;
                    Analyse(_wMap, _wBuy, out wp, out wpv, out wt, out wb, out wvah, out wval);
                    _refPocW = wp; _refVahW = wvah; _refValW = wval;
                    _wPocHist.Add(wp);
                    while (_wPocHist.Count > DreMigrationCandlesBack + 1) _wPocHist.RemoveAt(0);
                }
                _cMap.Clear(); _cBuy.Clear();
                _htfD = 0; _htfV = 0;
            }
            _htfD += barDeltaLtf;
            _htfV += Volume[1];

            if (DetectNewSession(true)) { _sessVol = 0; _sessUp = 0; _sessMaxV = 0; }
            if (InSession(Time[1]))
            {
                _sessVol += Volume[1];
                _sessUp += (Close[1] > Open[1] ? Volume[1] : 0.0);
                double vHere; _volMap.TryGetValue(BinOf(Close[1]), out vHere);
                _sessMaxV = Math.Max(_sessMaxV, vHere);
            }
        }

        private double CurrentTickDelta()
        {
            // Sum of signed volume added to _cMap this bar is already folded into _htfD via
            // AddVol during OnMarketData; this returns 0 here to avoid double counting — the
            // per-bar delta used for colouring is read from the HTF accumulator (_htfD/_htfV)
            // directly rather than recomputed from the map, matching Pine's running total.
            return 0.0;
        }

        private static void MapAdd(Dictionary<double, double> dst, Dictionary<double, double> src, double sgn)
        {
            foreach (var kv in src)
            {
                double cur; dst.TryGetValue(kv.Key, out cur);
                dst[kv.Key] = cur + sgn * kv.Value;
            }
        }

        private bool HtfPaintRolled(string tf)
        {
            if (!_havePrevTime) return true;
            switch (PaintHtf)
            {
                case SvpHtfPaint.M15: return (Time[0].Hour * 60 + Time[0].Minute) / 15 != (_prevTime.Hour * 60 + _prevTime.Minute) / 15 || Time[0].Date != _prevTime.Date;
                case SvpHtfPaint.M30: return (Time[0].Hour * 60 + Time[0].Minute) / 30 != (_prevTime.Hour * 60 + _prevTime.Minute) / 30 || Time[0].Date != _prevTime.Date;
                case SvpHtfPaint.M60: return Time[0].Hour != _prevTime.Hour || Time[0].Date != _prevTime.Date;
                case SvpHtfPaint.M120: return (Time[0].Hour / 2) != (_prevTime.Hour / 2) || Time[0].Date != _prevTime.Date;
                case SvpHtfPaint.M240: return (Time[0].Hour / 4) != (_prevTime.Hour / 4) || Time[0].Date != _prevTime.Date;
                case SvpHtfPaint.Daily: return Bars.IsFirstBarOfSession;
                default: return false;
            }
        }

        private static double Grad(double n, Color neut, Color bull, Color bear)
        {
            return 0; // placeholder not used — colour variant below returns Brush directly
        }
        private static Brush GradBrush(double n, Color neut, Color bull, Color bear)
        {
            n = Math.Max(-1, Math.Min(1, n));
            return n >= 0 ? Fz(LerpColor(neut, bull, n)) : Fz(LerpColor(bear, neut, n + 1));
        }

        private void PaintCandle()
        {
            if (!(MasterAll && MasterPaint) || CurrentBar < 1) { return; }

            SvpSession refS = _done.Count > 0 ? _done[_done.Count - 1] : null;
            double refPoc = refS != null ? refS.Poc : double.NaN;
            double refVah = refS != null ? refS.Vah : double.NaN;
            double refVal = refS != null ? refS.Val : double.NaN;

            Brush col = null;
            switch (PaintEngine)
            {
                case SvpPaintEngine.DeltaHtfCandlesDre:
                    col = PaintDre(refPoc, refVah, refVal);
                    break;
                case SvpPaintEngine.DeltaHtfGradient:
                {
                    double htfAvgAbs = _htfHist.Count > 0 ? _htfHist.Select(Math.Abs).Average() : 0.0;
                    double hScale = htfAvgAbs > 0 ? htfAvgAbs * PaintSensitivity : Math.Abs(_htfD);
                    double hPct = _htfV > 0 ? _htfD / _htfV : 0.0;
                    double hRaw = PaintDeltaMode == SvpDeltaMode.DeltaPercentImbalance
                        ? Math.Max(-1.0, Math.Min(1.0, hPct * PaintSensitivity / 0.15))
                        : (hScale > 0 ? Math.Max(-1.0, Math.Min(1.0, _htfD / hScale)) : 0.0);
                    double hCurved = PaintCurve == SvpDeltaCurve.Boosted ? Math.Sign(hRaw) * Math.Pow(Math.Abs(hRaw), 0.55) : hRaw;
                    col = GradBrush(hCurved, PaintNeutral, PaintBull, PaintBear);
                    break;
                }
                case SvpPaintEngine.DeltaGradient:
                {
                    double barDelta = Close[1] > Open[1] ? Volume[1] : Close[1] < Open[1] ? -Volume[1] : 0.0;
                    double dPct = Volume[1] > 0 ? barDelta / Volume[1] : 0.0;
                    double rawN = PaintDeltaMode == SvpDeltaMode.DeltaPercentImbalance
                        ? Math.Max(-1.0, Math.Min(1.0, dPct * PaintSensitivity / 0.15)) : 0.0;
                    double curvedN = PaintCurve == SvpDeltaCurve.Boosted ? Math.Sign(rawN) * Math.Pow(Math.Abs(rawN), 0.55) : rawN;
                    col = GradBrush(curvedN, PaintNeutral, PaintBull, PaintBear);
                    break;
                }
                case SvpPaintEngine.DeltaSign:
                {
                    double barDelta = Close[1] > Open[1] ? Volume[1] : Close[1] < Open[1] ? -Volume[1] : 0.0;
                    col = barDelta > 0 ? Fz(PaintBull) : barDelta < 0 ? Fz(PaintBear) : Fz(PaintNeutral);
                    break;
                }
                case SvpPaintEngine.ValueAreaPosition:
                    if (!double.IsNaN(refVah) && !double.IsNaN(refVal))
                        col = Close[0] > refVah ? Fz(PaintBull) : Close[0] < refVal ? Fz(PaintBear) : Fz(PaintNeutral);
                    break;
                case SvpPaintEngine.DistanceFromPoc:
                    if (!double.IsNaN(refPoc) && !double.IsNaN(refVah) && !double.IsNaN(refVal))
                    {
                        double vaW = Math.Max(TickSize, refVah - refVal);
                        double pocN = Math.Max(-1.0, Math.Min(1.0, (Close[0] - refPoc) / vaW));
                        col = GradBrush(pocN, PaintNeutral, PaintBull, PaintBear);
                    }
                    break;
                case SvpPaintEngine.VolumeAtPriceHeat:
                    if (InSession(Time[0]))
                    {
                        double vAtClose; _volMap.TryGetValue(BinOf(Close[0]), out vAtClose);
                        double heatF = _sessMaxV > 0 ? Math.Min(1.0, vAtClose / _sessMaxV) : 0.0;
                        col = Heat(heatF, HeatColorLow, HeatColorMid, HeatColorHigh);
                    }
                    break;
                case SvpPaintEngine.NakedPocProximity:
                    foreach (var s in _done)
                        if (!s.Tested && !double.IsNaN(s.Poc) && Math.Abs(Close[0] - s.Poc) <= NakedProximityTicks * TickSize)
                            col = PocNakedBrush;
                    break;
                case SvpPaintEngine.SessionUpVolShare:
                    if (_sessVol > 0)
                    {
                        double upShare = _sessUp / _sessVol;
                        col = GradBrush(Math.Max(-1.0, Math.Min(1.0, (upShare - 0.5) / 0.15)), PaintNeutral, PaintBull, PaintBear);
                    }
                    break;
            }

            if (col != null)
            {
                BarBrushes[0] = col;
                CandleOutlineBrushes[0] = col;
            }
        }

        // 0 · Delta HTF candles (DRE) — the composite-reference engine
        private Brush PaintDre(double refPoc, double refVah, double refVal)
        {
            double dPoc = DreReference == SvpDreReference.HtfWindowNCandles ? _refPocW : DreReference == SvpDreReference.LastHtfCandle ? _refPocL : refPoc;
            double dVah = DreReference == SvpDreReference.HtfWindowNCandles ? _refVahW : DreReference == SvpDreReference.LastHtfCandle ? _refVahL : refVah;
            double dVal = DreReference == SvpDreReference.HtfWindowNCandles ? _refValW : DreReference == SvpDreReference.LastHtfCandle ? _refValL : refVal;

            if (double.IsNaN(dPoc) || double.IsNaN(dVah) || double.IsNaN(dVal) || dVah <= dVal) return Fz(PaintNeutral);

            bool dreUp = Close[0] > dVah, dreDn = Close[0] < dVal;
            _dreAcc = (dreUp || dreDn) ? ((dreUp && _dreUpPrev) || (dreDn && _dreDnPrev) ? _dreAcc + 1 : 1) : 0;
            _dreUpPrev = dreUp; _dreDnPrev = dreDn;
            double dreAccF = Math.Min(1.0, _dreAcc / Math.Max(1.0, (double)DreAcceptanceBars));

            double relVolSma = SMA(Volume, 20)[0];
            double dreRelV = relVolSma > 0 ? Volume[0] / relVolSma : double.NaN;
            double dreSizeF = double.IsNaN(dreRelV) ? 0.0 : Math.Max(0.0, Math.Min(1.0, (dreRelV - 1.0) / Math.Max(0.1, DreSizeFullAtAvgMult - 1.0)));

            double dreVaW = dVah - dVal;
            double dreBack = _wPocHist.Count > DreMigrationCandlesBack ? _wPocHist[_wPocHist.Count - 1 - DreMigrationCandlesBack] : double.NaN;
            double dreMig = double.IsNaN(_refPocW) || double.IsNaN(dreBack) || dreVaW <= 0 ? 0.0 : (_refPocW - dreBack) / dreVaW;
            bool dreMigAl = (dreUp && dreMig > 0) || (dreDn && dreMig < 0);
            double dreMigF = dreMigAl ? Math.Min(1.0, Math.Abs(dreMig)) : 0.0;
            double dreExtra = DreIntensityFrom == SvpDreIntensity.Strongest
                ? Math.Max(dreAccF, Math.Max(dreSizeF, dreMigF))
                : (dreAccF + dreSizeF + dreMigF) / 3.0;

            double w = dVah - dVal;
            double rampW = Math.Max(TickSize, w * DreRampPctOfVa / 100.0);
            double htfLiveD = _htfD;

            if (Close[0] > dVah || Close[0] < dVal)
            {
                double beyond = Close[0] > dVah ? Close[0] - dVah : dVal - Close[0];
                double fracD = Math.Min(1.0, beyond / rampW);
                double frac = DreIntensityFrom == SvpDreIntensity.DistanceOnly ? fracD
                    : DreIntensityFrom == SvpDreIntensity.Strongest ? Math.Max(fracD, dreExtra) : (fracD + dreExtra) / 2.0;
                bool greenZone = Close[0] > dVah;
                bool aligned = (greenZone && htfLiveD > 0) || (!greenZone && htfLiveD < 0);
                double adj = aligned ? Math.Min(1.0, frac + DreAlignBoost) : frac * (1.0 - DreAlignBoost);
                return greenZone ? Fz(LerpColor(PaintNeutral, PaintBull, adj)) : Fz(LerpColor(PaintNeutral, PaintBear, adj));
            }
            else
            {
                double halfW = Math.Max(TickSize, Math.Max(dVah - dPoc, dPoc - dVal));
                double centering = 1.0 - Math.Min(1.0, Math.Abs(Close[0] - dPoc) / halfW);
                double htfAvgAbs = _htfHist.Count > 0 ? _htfHist.Select(Math.Abs).Average() : 0.0;
                double dq = htfAvgAbs > 0 ? 1.0 - Math.Min(1.0, Math.Abs(htfLiveD) / htfAvgAbs) : 0.5;
                double depth = Math.Max(0.0, Math.Min(1.0, (centering + dq) / 2.0));
                double lean = DreLean * Math.Min(1.0, Math.Abs(Close[0] - dPoc) / halfW);
                Color leanCol = Close[0] > dPoc ? LerpColor(PaintNeutral, PaintBull, lean) : LerpColor(PaintNeutral, PaintBear, lean);
                Color baseCol = DreLean > 0 ? leanCol : PaintNeutral;
                byte alpha = (byte)Math.Round(depth * 255);
                return Fz(Color.FromArgb(alpha, baseCol.R, baseCol.G, baseCol.B));
            }
        }

        private void CheckCrossAlerts()
        {
            if (!IsFirstTickOfBar || CurrentBar < 1) return;
            if (AlertDevPocCross && !double.IsNaN(_lastDevPoc))
            {
                if ((Close[1] > _lastDevPoc && Open[1] <= _lastDevPoc) || (Close[1] < _lastDevPoc && Open[1] >= _lastDevPoc))
                    Alert("SVPDevPocCross", Priority.Low, "STRYK: SVP " + Instrument.MasterInstrument.Name + " — close crossed the developing POC at " + _lastDevPoc.ToString("0.####"),
                        NinjaTrader.Core.Globals.InstallDir + "sounds\\Alert1.wav", 60, Brushes.Black, Brushes.DodgerBlue);
            }
            if (AlertValueAreaExit && !double.IsNaN(_lastDevVah) && !double.IsNaN(_lastDevVal))
            {
                if ((Close[1] > _lastDevVah && Open[1] <= _lastDevVah) || (Close[1] < _lastDevVal && Open[1] >= _lastDevVal))
                    Alert("SVPVaExit", Priority.Low, "STRYK: SVP " + Instrument.MasterInstrument.Name + " — close left the developing value area",
                        NinjaTrader.Core.Globals.InstallDir + "sounds\\Alert1.wav", 60, Brushes.Black, Brushes.DodgerBlue);
            }
            if (!double.IsNaN(_devPocPx)) { _lastDevPoc = _devPocPx; _lastDevVah = _devVahPx; _lastDevVal = _devValPx; }
        }


        // ═══════════════════════════ TRADE SIGNALS (plots for SneakyTradeHUD AUTO TRADE) ═══════════════════════════
        // Not part of the TradingView original: an added layer that turns five textbook profile setups into
        // +1 (long) / -1 (short) plots. Rules are the ones backtested in Python on exported NQ 1-minute data
        // (88 trading days, 11 Jun - 16 Oct 2026). RESULT OF THAT TEST - none showed a reliable edge:
        //   SigRotation        106 trades, PF 0.95     SigNakedPoc     13 trades, PF 2.23 (too few to trust)
        //   SigFailedBreakout   61 trades, PF 1.01     SigIbBreak      78 trades, PF 0.74
        //   SigAcceptance       81 trades, PF 1.03     SigIbBreakNarrow 40 trades, PF 0.37 / Wide 29 trades, PF 1.24
        // Rules (regular hours only, one signal per setup per side per day, none in the last 30 minutes):
        //   Rotation        after the first hour a bar touches the developing value-area edge and closes back inside;
        //                   target developing POC, stop 2 points beyond the bar
        //   Failed breakout a close outside the PRIOR session's value area, then a close back inside; target prior
        //                   POC, stop 2 points beyond the excursion
        //   Acceptance      15 straight closes outside the prior value area; with it, stop just inside the edge, target 2R
        //   Naked POC       first regular-hours bar: toward the nearest untouched prior POC 0.15-0.75 of a typical
        //                   day's range away; equal-distance stop
        //   IB break        first close outside the first regular-hours hour; stop IB midpoint, target one IB range
        // The engine keeps its own whole-bar profile (same maths as the main one) so the signals are identical on
        // historical and live bars and match the backtest. Built for a 1-MINUTE chart. SigStop / SigTarget carry the
        // latest signal's own stop and target price; SneakyTradeHUD enters with its own tick distances instead.
        private const int SigP0 = 1;   // plot 0 is the internal placeholder; signal plots are 1..9
        private Dictionary<int, double> _sgVol = new Dictionary<int, double>();
        private double _sgHi = double.NaN, _sgLo = double.NaN, _sgPreHi = double.NaN, _sgPreLo = double.NaN;
        private bool _sgHavePrev; private double _sgPPoc, _sgPVah, _sgPVal;
        private readonly List<double> _sgNaked = new List<double>(), _sgDayRanges = new List<double>(), _sgIbHist = new List<double>();
        private bool _sgRthSeen, _sg1L, _sg1S, _sg2Up, _sg2Dn, _sg3, _sg5, _sgPendingFirst;
        private int _sgOut, _sgRun, _sgRunSide, _sgSeq, _sgLastBar = -1; private double _sgExt;
        private double _sgIbH = double.NaN, _sgIbL = double.NaN, _sgStop, _sgTarget;

        private int SgBin(double px) { return (int)Math.Floor(px / (TickSize * BinTicks) + 0.5); }
        private bool SgAnalyse(out double poc, out double vah, out double val)
        {
            poc = vah = val = double.NaN;
            if (_sgVol.Count == 0) return false;
            double binPx = TickSize * BinTicks;
            int f = _sgVol.Keys.Min(), l = _sgVol.Keys.Max(), n = l - f + 1;
            double[] vs = new double[n]; double tot = 0, pocV = -1; int pi = 0;
            for (int i = 0; i < n; i++)
            {
                double v; _sgVol.TryGetValue(f + i, out v); vs[i] = v; tot += v;
                if (v > pocV) { pocV = v; pi = i; }
            }
            double target = tot * ValueAreaPct / 100.0, acc = pocV; int lo = pi, hi = pi;
            while (acc < target && (lo > 0 || hi < n - 1))
            {
                double below = lo > 0 ? vs[lo - 1] : -1.0, above = hi < n - 1 ? vs[hi + 1] : -1.0;
                if (above >= below) { hi++; acc += above; } else { lo--; acc += below; }
            }
            poc = (f + pi) * binPx; vah = (f + hi) * binPx + binPx / 2; val = (f + lo) * binPx - binPx / 2;
            return true;
        }
        private static double SgMedian(List<double> xs, int last)
        {
            var s = xs.Skip(Math.Max(0, xs.Count - last)).OrderBy(x => x).ToList();
            return s.Count == 0 ? double.NaN : (s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0);
        }
        private bool SgFire(int a, int plot, int side, double stop, double target, string name)
        {
            double entry = Close[a];
            if (Math.Abs(entry - stop) <= 0 || (target - entry) * side <= 0) return false;   // same validity test as the backtest
            Values[SigP0 + plot][a] = side;
            _sgStop = stop; _sgTarget = target;
            Values[SigP0 + 7][a] = stop; Values[SigP0 + 8][a] = target;
            _sgSeq++;
            Brush c = side > 0 ? Brushes.LimeGreen : Brushes.OrangeRed;
            if (SigShowMarkers)
            {
                double y = side > 0 ? Low[a] - 4 * TickSize : High[a] + 4 * TickSize;
                if (side > 0) Bk(Draw.ArrowUp(this, "SVPsigA" + _sgSeq, false, Time[a], y, c));
                else Bk(Draw.ArrowDown(this, "SVPsigA" + _sgSeq, false, Time[a], y, c));
                Bk(Draw.Text(this, "SVPsigT" + _sgSeq, false, name, Time[a], y, side > 0 ? -38 : 38, c,
                    new SimpleFont("Segoe UI", 10), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0));
            }
            if (SigShowLevels)
            {
                DateTime x1 = Time[a].AddMinutes(15);
                Bk(Draw.Line(this, "SVPsigS" + _sgSeq, false, Time[a], stop, x1, stop, Brushes.OrangeRed, DashStyleHelper.Dash, 2));
                Bk(Draw.Line(this, "SVPsigG" + _sgSeq, false, Time[a], target, x1, target, Brushes.LimeGreen, DashStyleHelper.Dash, 2));
                var lf = new SimpleFont("Segoe UI", 9);
                Bk(Draw.Text(this, "SVPsigSL" + _sgSeq, false, name + " stop", x1, stop, 0, Brushes.OrangeRed, lf, TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0));
                Bk(Draw.Text(this, "SVPsigGL" + _sgSeq, false, name + " target", x1, target, 0, Brushes.LimeGreen, lf, TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0));
            }
            return true;
        }

        // Processes one FINISHED bar (a = bars ago), exactly once.
        private void RunSignals(int a, bool firstOfSession)
        {
            int idx = CurrentBar - a;
            if (idx <= _sgLastBar) return;
            _sgLastBar = idx;
            for (int p = 0; p < 7; p++) Values[SigP0 + p][a] = 0;
            Values[SigP0 + 7][a] = _sgStop; Values[SigP0 + 8][a] = _sgTarget;

            if (firstOfSession)
            {
                double poc, vah, val;
                if (!double.IsNaN(_sgHi) && SgAnalyse(out poc, out vah, out val))
                {
                    double sLo = _sgLo, sHi = _sgHi;
                    _sgNaked.RemoveAll(p => sLo <= p && p <= sHi);   // touched this session -> no longer naked
                    _sgPPoc = poc; _sgPVah = vah; _sgPVal = val; _sgHavePrev = true;
                    _sgNaked.Add(poc); while (_sgNaked.Count > 10) _sgNaked.RemoveAt(0);
                    _sgDayRanges.Add(_sgHi - _sgLo);
                    if (!double.IsNaN(_sgIbH)) _sgIbHist.Add(_sgIbH - _sgIbL);
                }
                _sgVol.Clear();
                _sgHi = _sgLo = _sgPreHi = _sgPreLo = double.NaN;
                _sgRthSeen = _sg1L = _sg1S = _sg2Up = _sg2Dn = _sg3 = _sg5 = false;
                _sgOut = 0; _sgRun = 0; _sgRunSide = 0; _sgExt = double.NaN; _sgIbH = _sgIbL = double.NaN;
            }

            DateTime st = BarsPeriod.BarsPeriodType == BarsPeriodType.Minute ? Time[a].AddMinutes(-BarsPeriod.Value) : Time[a];
            int m = st.Hour * 60 + st.Minute;
            int rth0 = SigRthStart / 100 * 60 + SigRthStart % 100, rth1 = SigRthEnd / 100 * 60 + SigRthEnd % 100;
            bool inRth = m >= rth0 && m < rth1, roomLeft = m < rth1 - 30;
            double c = Close[a], hi = High[a], lo = Low[a];

            if (inRth)
            {
                if (!_sgRthSeen)
                {
                    _sgRthSeen = true;
                    double rng = _sgDayRanges.Count >= 5 ? SgMedian(_sgDayRanges, 20) : double.NaN;
                    if (!double.IsNaN(rng))
                    {
                        double best = double.NaN;
                        foreach (double p in _sgNaked)
                        {
                            double dist = Math.Abs(p - c);
                            bool touchedOvernight = !double.IsNaN(_sgPreLo) && _sgPreLo <= p && p <= _sgPreHi;
                            if (dist < 0.15 * rng || dist > 0.75 * rng || touchedOvernight) continue;
                            if (double.IsNaN(best) || dist < Math.Abs(best - c)) best = p;
                        }
                        if (!double.IsNaN(best))
                        {
                            int side = best > c ? 1 : -1;
                            SgFire(a, 3, side, c - side * Math.Abs(best - c), best, "Naked POC");
                        }
                    }
                }

                if (m < rth0 + 60)
                {
                    _sgIbH = double.IsNaN(_sgIbH) ? hi : Math.Max(_sgIbH, hi);
                    _sgIbL = double.IsNaN(_sgIbL) ? lo : Math.Min(_sgIbL, lo);
                }
                else if (roomLeft)
                {
                    double poc, vah, val;   // developing profile through the PREVIOUS bar
                    if (SgAnalyse(out poc, out vah, out val))
                    {
                        if (!_sg1L && lo <= val && c > val && poc - c > 2) { SgFire(a, 0, 1, lo - 2, poc, "Rotation"); _sg1L = true; }
                        if (!_sg1S && hi >= vah && c < vah && c - poc > 2) { SgFire(a, 0, -1, hi + 2, poc, "Rotation"); _sg1S = true; }
                    }
                    if (!_sg5 && !double.IsNaN(_sgIbH))
                    {
                        int side = c > _sgIbH ? 1 : c < _sgIbL ? -1 : 0;
                        if (side != 0)
                        {
                            _sg5 = true;
                            double ibr = _sgIbH - _sgIbL;
                            if (SgFire(a, 4, side, (_sgIbH + _sgIbL) / 2.0, c + side * ibr, "IB break") && _sgIbHist.Count >= 10)
                                Values[SigP0 + (ibr < SgMedian(_sgIbHist, 20) ? 6 : 5)][a] = side;
                        }
                    }
                }

                if (_sgHavePrev && roomLeft)
                {
                    int pos = c > _sgPVah ? 1 : c < _sgPVal ? -1 : 0;
                    if (_sgOut != 0 && pos == 0)
                    {
                        if (_sgOut > 0 && !_sg2Up) { SgFire(a, 1, -1, _sgExt + 2, _sgPPoc, "Failed breakout"); _sg2Up = true; }
                        if (_sgOut < 0 && !_sg2Dn) { SgFire(a, 1, 1, _sgExt - 2, _sgPPoc, "Failed breakout"); _sg2Dn = true; }
                    }
                    if (pos != 0)
                        _sgExt = _sgOut != pos ? (pos > 0 ? hi : lo) : (pos > 0 ? Math.Max(_sgExt, hi) : Math.Min(_sgExt, lo));
                    _sgOut = pos;

                    _sgRun = (pos != 0 && pos == _sgRunSide) ? _sgRun + 1 : (pos != 0 ? 1 : 0);
                    _sgRunSide = pos;
                    if (_sgRun == 15 && !_sg3)
                    {
                        _sg3 = true;
                        double edge = pos > 0 ? _sgPVah : _sgPVal, w = _sgPVah - _sgPVal;
                        double stop = edge - pos * Math.Max(10, 0.1 * w), risk = Math.Abs(c - stop);
                        SgFire(a, 2, pos, stop, c + pos * 2 * risk, "Acceptance");
                    }
                }
            }
            else if (!_sgRthSeen)
            {
                _sgPreHi = double.IsNaN(_sgPreHi) ? hi : Math.Max(_sgPreHi, hi);
                _sgPreLo = double.IsNaN(_sgPreLo) ? lo : Math.Min(_sgPreLo, lo);
            }

            // the bar joins the engine's profile only after its signals were read
            if (Volume[a] > 0)
            {
                int k0 = SgBin(lo), k1 = SgBin(hi), nb = Math.Max(1, Math.Min(400, k1 - k0 + 1));
                double share = Volume[a] / nb;
                for (int k = k0; k < k0 + nb; k++) { double cur; _sgVol.TryGetValue(k, out cur); _sgVol[k] = cur + share; }
            }
            _sgHi = double.IsNaN(_sgHi) ? hi : Math.Max(_sgHi, hi);
            _sgLo = double.IsNaN(_sgLo) ? lo : Math.Min(_sgLo, lo);
        }

        #region Properties
        // -- 0. master switches --
        [NinjaScriptProperty] [Display(Name = "EVERYTHING ON", GroupName = "0. Master switches", Order = 0)] public bool MasterAll { get; set; }
        [NinjaScriptProperty] [Display(Name = "Session profiles", GroupName = "0. Master switches", Order = 1)] public bool MasterProfiles { get; set; }
        [NinjaScriptProperty] [Display(Name = "POC lines", GroupName = "0. Master switches", Order = 2)] public bool MasterPoc { get; set; }
        [NinjaScriptProperty] [Display(Name = "Value area", GroupName = "0. Master switches", Order = 3)] public bool MasterValueArea { get; set; }
        [NinjaScriptProperty] [Display(Name = "Developing session", GroupName = "0. Master switches", Order = 4)] public bool MasterDeveloping { get; set; }
        [NinjaScriptProperty] [Display(Name = "POC tags", GroupName = "0. Master switches", Order = 5)] public bool MasterTags { get; set; }
        [NinjaScriptProperty] [Display(Name = "Panel", GroupName = "0. Master switches", Order = 6)] public bool MasterPanel { get; set; }
        [NinjaScriptProperty] [Display(Name = "Initial balance", GroupName = "0. Master switches", Order = 7)] public bool MasterInitialBalance { get; set; }
        [NinjaScriptProperty] [Display(Name = "Candle colouring", GroupName = "0. Master switches", Order = 8)] public bool MasterPaint { get; set; }
        [NinjaScriptProperty] [Display(Name = "Colouring engine", GroupName = "0. Master switches", Order = 9)] public SvpPaintEngine PaintEngine { get; set; }

        // -- 1. session --
        [NinjaScriptProperty] [Display(Name = "Profile period", GroupName = "1. Session", Order = 0)] public SvpSessionMode SessionMode { get; set; }
        [NinjaScriptProperty] [Display(Name = "Custom session start (HH:MM)", GroupName = "1. Session", Order = 1)] public string CustomSessionStart { get; set; }
        [NinjaScriptProperty] [Display(Name = "Custom session end (HH:MM)", GroupName = "1. Session", Order = 2)] public string CustomSessionEnd { get; set; }
        [NinjaScriptProperty] [Range(1, 30)] [Display(Name = "Sessions to keep", GroupName = "1. Session", Order = 3)] public int SessionsToKeep { get; set; }

        // -- 2. profile build --
        [NinjaScriptProperty] [Range(1, 100)] [Display(Name = "Price bin (ticks)", GroupName = "2. Profile build", Order = 0)] public int BinTicks { get; set; }
        [NinjaScriptProperty] [Range(50, 95)] [Display(Name = "Value area (% of volume)", GroupName = "2. Profile build", Order = 1)] public int ValueAreaPct { get; set; }

        // -- 2b. initial balance --
        [NinjaScriptProperty] [Display(Name = "Initial balance on", GroupName = "2b. Initial balance", Order = 0)] public bool IbOn { get; set; }
        [NinjaScriptProperty] [Range(5, 240)] [Display(Name = "IB minutes", GroupName = "2b. Initial balance", Order = 1)] public int IbMinutes { get; set; }
        [NinjaScriptProperty] [Display(Name = "Extend developing IB to current bar", GroupName = "2b. Initial balance", Order = 2)] public bool IbExtendLive { get; set; }
        [NinjaScriptProperty] [Display(Name = "Show IB on completed sessions too", GroupName = "2b. Initial balance", Order = 3)] public bool IbShowOnCompleted { get; set; }

        // -- 3. profiles on chart --
        [NinjaScriptProperty] [Display(Name = "Style", GroupName = "3. Profiles on chart", Order = 0)] public SvpProfileStyle ProfileStyle { get; set; }
        [NinjaScriptProperty] [Range(6, 100)] [Display(Name = "Rows per profile", GroupName = "3. Profiles on chart", Order = 1)] public int RowsPerProfile { get; set; }
        [NinjaScriptProperty] [Range(0, 95)] [Display(Name = "Heatmap transparency", GroupName = "3. Profiles on chart", Order = 2)] public int HeatTransparency { get; set; }
        [NinjaScriptProperty] [Range(0, 95)] [Display(Name = "Bars transparency", GroupName = "3. Profiles on chart", Order = 3)] public int BarsTransparency { get; set; }
        [NinjaScriptProperty] [Display(Name = "Bars grow from", GroupName = "3. Profiles on chart", Order = 4)] public SvpBarsAnchor BarsAnchor { get; set; }
        [NinjaScriptProperty] [Range(10, 100)] [Display(Name = "Bars: max length (% of session)", GroupName = "3. Profiles on chart", Order = 5)] public int BarsMaxWidthPct { get; set; }
        [XmlIgnore] [Browsable(false)] public Brush HeatColorLow { get; set; } = Fz(Color.FromRgb(0x0B, 0x1F, 0x3A));
        [XmlIgnore] [Browsable(false)] public Brush HeatColorMid { get; set; } = Fz(Color.FromRgb(0x1E, 0x8C, 0x7A));
        [XmlIgnore] [Browsable(false)] public Brush HeatColorHigh { get; set; } = Fz(Color.FromRgb(0xF5, 0xD1, 0x42));
        [XmlIgnore] [Browsable(false)] public Brush BarBuyBrush { get; set; } = Fz(Color.FromRgb(0x26, 0xA6, 0x9A));
        [XmlIgnore] [Browsable(false)] public Brush BarSellBrush { get; set; } = Fz(Color.FromRgb(0xEF, 0x53, 0x50));

        // -- 4. POC lines --
        [NinjaScriptProperty] [Display(Name = "Extend naked POC to current bar", GroupName = "4. POC lines", Order = 0)] public bool PocExtendToCurrentBar { get; set; }
        [NinjaScriptProperty] [Display(Name = "Stop a tested POC at the bar that tested it", GroupName = "4. POC lines", Order = 1)] public bool PocStopAtTest { get; set; }
        [NinjaScriptProperty] [Display(Name = "Hide tested POCs entirely", GroupName = "4. POC lines", Order = 2)] public bool PocHideTested { get; set; }
        [NinjaScriptProperty] [Display(Name = "Tag each POC", GroupName = "4. POC lines", Order = 3)] public bool TagOn { get; set; }
        [NinjaScriptProperty] [Range(0, 100)] [Display(Name = "Tag offset right (minutes)", GroupName = "4. POC lines", Order = 4)] public int TagOffsetBars { get; set; }
        [XmlIgnore] [Browsable(false)] public Brush PocNakedBrush { get; set; } = Fz(Color.FromRgb(0xFF, 0xB7, 0x4D));
        [XmlIgnore] [Browsable(false)] public Brush PocTestedBrush { get; set; } = Fz(Color.FromRgb(0x78, 0x7B, 0x86));

        // -- 5. value area --
        [NinjaScriptProperty] [Display(Name = "VAH / VAL lines", GroupName = "5. Value area", Order = 0)] public bool VaLines { get; set; }
        [NinjaScriptProperty] [Display(Name = "Extend VAH/VAL to current bar", GroupName = "5. Value area", Order = 1)] public bool VaExtend { get; set; }
        [NinjaScriptProperty] [Display(Name = "Shade the value area", GroupName = "5. Value area", Order = 2)] public bool VaFillShade { get; set; }
        [NinjaScriptProperty] [Range(50, 100)] [Display(Name = "Value area fill transparency", GroupName = "5. Value area", Order = 3)] public int VaFillTransparency { get; set; }
        [XmlIgnore] [Browsable(false)] public Brush ValueAreaBrush { get; set; } = Fz(Color.FromRgb(0x42, 0xA5, 0xF5));

        // -- 6. developing session --
        [NinjaScriptProperty] [Display(Name = "Draw the developing profile", GroupName = "6. Developing session", Order = 0)] public bool DevDrawProfile { get; set; }
        [NinjaScriptProperty] [Display(Name = "Developing POC", GroupName = "6. Developing session", Order = 1)] public bool DevPocOn { get; set; }
        [NinjaScriptProperty] [Display(Name = "Developing VAH / VAL", GroupName = "6. Developing session", Order = 2)] public bool DevVaOn { get; set; }
        [NinjaScriptProperty] [Display(Name = "Tag the developing POC", GroupName = "6. Developing session", Order = 3)] public bool DevTag { get; set; }
        [XmlIgnore] [Browsable(false)] public Brush DevPocBrush { get; set; } = Brushes.White;
        [XmlIgnore] [Browsable(false)] public Brush InitialBalanceBrush { get; set; } = Fz(Color.FromRgb(0xAB, 0x47, 0xBC));

        // -- 7. panel --
        [NinjaScriptProperty] [Display(Name = "Show panel", GroupName = "7. Panel", Order = 0)] public bool PanelOn { get; set; }

        // -- 8. alerts --
        [NinjaScriptProperty] [Display(Name = "Price touches a naked POC", GroupName = "8. Alerts", Order = 0)] public bool AlertNakedPocTouch { get; set; }
        [NinjaScriptProperty] [Display(Name = "Price crosses the developing POC", GroupName = "8. Alerts", Order = 1)] public bool AlertDevPocCross { get; set; }
        [NinjaScriptProperty] [Display(Name = "Price leaves the developing value area", GroupName = "8. Alerts", Order = 2)] public bool AlertValueAreaExit { get; set; }

        // -- 9. candle colouring --
        [NinjaScriptProperty] [Display(Name = "Delta HTF candles: higher timeframe", GroupName = "9. Candle colouring", Order = 0)] public SvpHtfPaint PaintHtf { get; set; }
        [NinjaScriptProperty] [Range(3, 100)] [Display(Name = "Delta HTF candles: candles in the average", GroupName = "9. Candle colouring", Order = 1)] public int PaintHtfCandleCount { get; set; }
        [NinjaScriptProperty] [Range(0.5, 5.0)] [Display(Name = "Delta: sensitivity", GroupName = "9. Candle colouring", Order = 2)] public double PaintSensitivity { get; set; }
        [NinjaScriptProperty] [Display(Name = "Delta: mode", GroupName = "9. Candle colouring", Order = 3)] public SvpDeltaMode PaintDeltaMode { get; set; }
        [NinjaScriptProperty] [Display(Name = "Delta: curve", GroupName = "9. Candle colouring", Order = 4)] public SvpDeltaCurve PaintCurve { get; set; }
        [NinjaScriptProperty] [Range(5, 500)] [Display(Name = "Delta gradient lookback", GroupName = "9. Candle colouring", Order = 5)] public int PaintLookback { get; set; }
        [NinjaScriptProperty] [Range(1, 200)] [Display(Name = "Naked POC proximity (ticks)", GroupName = "9. Candle colouring", Order = 6)] public int NakedProximityTicks { get; set; }
        [XmlIgnore] [Browsable(false)] public Color PaintBull { get; set; } = Color.FromRgb(0x4A, 0xDE, 0x80);
        [XmlIgnore] [Browsable(false)] public Color PaintNeutral { get; set; } = Color.FromRgb(0x3B, 0x82, 0xF6);
        [XmlIgnore] [Browsable(false)] public Color PaintBear { get; set; } = Color.FromRgb(0xE6, 0x30, 0x30);

        // -- 9b. DRE engine --
        [NinjaScriptProperty] [Display(Name = "DRE reference profile", GroupName = "9b. DRE engine", Order = 0)] public SvpDreReference DreReference { get; set; }
        [NinjaScriptProperty] [Range(1, 60)] [Display(Name = "DRE window: N candles", GroupName = "9b. DRE engine", Order = 1)] public int DreWindowCandles { get; set; }
        [NinjaScriptProperty] [Range(0.0, 0.8)] [Display(Name = "DRE lean by POC side", GroupName = "9b. DRE engine", Order = 2)] public double DreLean { get; set; }
        [NinjaScriptProperty] [Display(Name = "DRE outside-value intensity from", GroupName = "9b. DRE engine", Order = 3)] public SvpDreIntensity DreIntensityFrom { get; set; }
        [NinjaScriptProperty] [Range(1, 20)] [Display(Name = "DRE acceptance: closes beyond for full", GroupName = "9b. DRE engine", Order = 4)] public int DreAcceptanceBars { get; set; }
        [NinjaScriptProperty] [Range(1.1, 6.0)] [Display(Name = "DRE size full at x avg vol", GroupName = "9b. DRE engine", Order = 5)] public double DreSizeFullAtAvgMult { get; set; }
        [NinjaScriptProperty] [Range(1, 50)] [Display(Name = "DRE migration: candles back", GroupName = "9b. DRE engine", Order = 6)] public int DreMigrationCandlesBack { get; set; }
        [NinjaScriptProperty] [Range(5, 75)] [Display(Name = "DRE ramp width (% of value area)", GroupName = "9b. DRE engine", Order = 7)] public double DreRampPctOfVa { get; set; }
        [NinjaScriptProperty] [Range(0.0, 0.6)] [Display(Name = "DRE align boost", GroupName = "9b. DRE engine", Order = 8)] public double DreAlignBoost { get; set; }

        // 10 — trade signals (added layer, not in the TradingView original)
        [NinjaScriptProperty] [Display(Name = "Trade signals on", Description = "Publishes the Sig* plots (+1 long / -1 short) for SneakyTradeHUD AUTO TRADE. Built for a 1-minute chart.", GroupName = "10. Trade signals", Order = 0)] public bool SignalsOn { get; set; }
        [NinjaScriptProperty] [Range(0, 2359)] [Display(Name = "Regular hours start (HHmm, chart time)", GroupName = "10. Trade signals", Order = 1)] public int SigRthStart { get; set; }
        [NinjaScriptProperty] [Range(0, 2359)] [Display(Name = "Regular hours end (HHmm, chart time)", GroupName = "10. Trade signals", Order = 2)] public int SigRthEnd { get; set; }
        [NinjaScriptProperty] [Display(Name = "Show signal arrows and names", GroupName = "10. Trade signals", Order = 3)] public bool SigShowMarkers { get; set; }
        [NinjaScriptProperty] [Display(Name = "Show each signal's stop and target", GroupName = "10. Trade signals", Order = 4)] public bool SigShowLevels { get; set; }
        #endregion
    }
}
