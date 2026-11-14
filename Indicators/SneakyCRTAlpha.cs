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

// SneakyCRTAlpha -- Candle Range Theory (CRT) toolkit. ORIGINAL implementation written from the public CRT concept
// (higher-timeframe reference candle -> liquidity sweep / FVG / return / break entries) plus common ICT-style key levels.
// Not affiliated with any commercial indicator. UNPROVEN: no backtest edge has been demonstrated. Use on sim first.
//
// Higher-timeframe (HTF) candles are built from the chart's own bars (no extra data series), so it works on any chart
// timeframe smaller than the HTF. HTF buckets are anchored at "HTF anchor hour" (18 = CME futures day open, chart time).
//
// Signals (hidden plots for auto-traders: 0 Signal +1/-1, 1 Type 1=Sweep 2=FVG 3=Return 4=Break, 2 Stop, 3 Target):
//   SWEEP : ref candle bearish -> 2nd candle wicks below ref low but closes back above it -> entry zone = lower part of the
//           ref range; BUY on a bullish reaction candle inside the zone. Target = ref high.  (mirror for sells)
//   FVG   : while the 2nd candle is forming, bullish FVGs inside the ref range are tracked; BUY when price retraces into one
//           and reacts bullish (no sweep needed).  (bearish ref -> buys, bullish ref -> sells)
//   RETURN: 2nd candle makes a strong move beyond the ref extreme (closes beyond it). Entry zone = the 2nd candle's range;
//           signal after price retraces by RetracePct of that candle and reacts back in the move's direction.
//   BREAK : 2nd candle closes beyond the ref extreme in the ref's direction; signal on the next lower-timeframe BOS/CHoCH
//           in that direction.
namespace NinjaTrader.NinjaScript.Indicators
{
	public class CrtCandle { public int S, E; public double O, H, L, C; public DateTime T; public long Id; public bool Done; }
	public class CrtSetup { public int Kind, Dir, Born, Life; public double Lo, Hi, Level, Ext, Crh, Crl; public bool Fired, Dead; public int FirstBar; }
	public class CrtFvg { public int Dir, Bar; public double Top, Bot; public bool Dead, Fired; }
	public class CrtZone { public int Dir, Bar; public double Top, Bot; public bool Dead; }
	public class CrtMark { public int Id, Bar, Dir, Kind; public double Y; }
	public class CrtEq { public int Id, A, B; public double Y; public bool High; }
	public class CrtSt { public int Id, A, B; public double Y; public bool Bull, Choch; }
	public class CrtLevel { public string Name; public double V = double.NaN; public int Bar = -1; public bool Broken; public int Clr; }

	public class SneakyCRTAlpha : Indicator
	{
		private List<CrtCandle> htf = new List<CrtCandle>();
		private CrtCandle cur;
		private List<CrtSetup> setups = new List<CrtSetup>();
		private List<CrtFvg> fvgs = new List<CrtFvg>();
		private List<CrtZone> obs = new List<CrtZone>();
		private int sigCount;
		private List<CrtMark> marks = new List<CrtMark>(); private List<CrtEq> eqs = new List<CrtEq>(); private List<CrtSt> sts = new List<CrtSt>(); private int idGen;
		// ---- dashboard state ----
		private const int NUI = 21;
		private static readonly string[] UiName = { "Sweep", "Return", "Break", "FVG", "Sweep Zone", "Return Zone", "FVG Zone", "CRH / CRL", "HTF Range", "BOS / CHoCH", "PDH / PDL", "PWH / PWL", "PMH / PML", "Asia", "London", "New York", "Daily Open", "EQH / EQL", "Order Block", "Round Number", "" };
		private bool[] ui = new bool[NUI]; private volatile bool uiDirty; private bool uiCollapsed; private float uiX = -1f, uiY = 40f;
		private List<float[]> chipRects = new List<float[]>(); private float[] titleRect = new float[4], arrowRect = new float[4], allRect = new float[4], noneRect = new float[4];
		private System.Windows.FrameworkElement dragCtl; private bool dragging; private System.Windows.Point dStart; private float dX0, dY0; private float pW, pH;
		private string UiPath() { return System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SneakyCrtUi_" + (Instrument != null ? Instrument.MasterInstrument.Name : "X") + ".txt"); }
		private void LoadUi()
		{
			for (int i = 0; i < NUI; i++) ui[i] = true;
			try
			{
				string f = UiPath(); if (!System.IO.File.Exists(f)) return; string[] a = System.IO.File.ReadAllText(f).Trim().Split('|'); if (a.Length < 4) return;
				float.TryParse(a[0], NumberStyles.Float, CultureInfo.InvariantCulture, out uiX); float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out uiY); uiCollapsed = a[2] == "1";
				for (int i = 0; i < NUI - 1 && i < a[3].Length; i++) ui[i] = a[3][i] == '1';
			}
			catch { }
		}
		private void SaveUi()
		{
			try { var sb = new System.Text.StringBuilder(); for (int i = 0; i < NUI - 1; i++) sb.Append(ui[i] ? '1' : '0'); System.IO.File.WriteAllText(UiPath(), uiX.ToString(CultureInfo.InvariantCulture) + "|" + uiY.ToString(CultureInfo.InvariantCulture) + "|" + (uiCollapsed ? "1" : "0") + "|" + sb); } catch { }
		}
		private volatile CrtCandle[] snap = new CrtCandle[0];
		private double atr; private int atrN;
		// structure
		private double lastPH = double.NaN, lastPL = double.NaN; private int lastPHbar = -1, lastPLbar = -1; private int trend;
		private bool phBroken, plBroken; private int structEvt; // +1 bull BOS, +2 bull CHoCH, -1 bear BOS, -2 bear CHoCH on this bar
		// key levels
		private long dayId = long.MinValue, weekId = long.MinValue, monId = long.MinValue;
		private double dH, dL, wH, wL, mH, mL;
		private CrtLevel pdh = new CrtLevel { Name = "PDH" }, pdl = new CrtLevel { Name = "PDL" }, pwh = new CrtLevel { Name = "PWH" }, pwl = new CrtLevel { Name = "PWL" }, pmh = new CrtLevel { Name = "PMH" }, pml = new CrtLevel { Name = "PML" };
		private CrtLevel dOpen = new CrtLevel { Name = "DO" };
		private CrtLevel[] sessLv = new CrtLevel[6];
		private double[] sH = new double[3], sL = new double[3]; private bool[] sIn = new bool[3]; private int[] sStart = new int[3];
		private List<double> pivH = new List<double>(), pivL = new List<double>(); private List<int> pivHb = new List<int>(), pivLb = new List<int>();
		private int eqCount; private SimpleFont font, fontSm;
		private bool dayOpenSet; private long dayOpenDayId = long.MinValue;
		private static readonly string[] SessName = { "Asia", "London", "NY" };

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Candle Range Theory toolkit: HTF reference candle, Sweep / FVG / Return / Break signals, entry zones, key levels. UNPROVEN edge.";
				Name = "SneakyCRTAlpha"; Calculate = Calculate.OnBarClose; IsOverlay = true; DisplayInDataBox = true; DrawOnPricePanel = true; PaintPriceMarkers = false; IsSuspendedWhileInactive = false;
				HtfMinutes = 60; AnchorHour = 18; ShowHtfCandles = true; ShowOlc = true; ShowHalves = true; ColorPaths = true; ExtendBars = 12;
				UseSweep = true; UseFvg = true; UseReturn = true; UseBreak = true; ZonePct = 50; RetracePct = 50; SetupLifeHtf = 2; StopBufTicks = 4; BreakMode = CrtBreakMode.Either; BreakFirstOnly = true; PivotStrength = 2;
				ShowPdhPdl = true; ShowPwhPwl = true; ShowPmhPml = true; ShowSessions = true; ShadeSessions = false; ShowDailyOpen = true; ShowEq = true; ShowOb = true; ShowRound = true; ShowStructure = true; ShowDashboard = true;
				AsiaStart = 20; AsiaEnd = 0; LondonStart = 2; LondonEnd = 5; NyStart = 9; NyEnd = 16; DailyOpenHour = 0; EqTolTicks = 6; ObAtrMult = 1.5; MaxOb = 4; RoundStep = 100; RoundCount = 3;
				FontSize = 10; AlertOnSignals = true; AlertSound = "Alert1.wav";
				AddPlot(Brushes.Transparent, "Signal"); AddPlot(Brushes.Transparent, "Type"); AddPlot(Brushes.Transparent, "Stop"); AddPlot(Brushes.Transparent, "Target");
			}
			else if (State == State.DataLoaded)
			{
				htf.Clear(); setups.Clear(); fvgs.Clear(); obs.Clear(); cur = null; sigCount = 0; snap = new CrtCandle[0];
				for (int i = 0; i < 6; i++) sessLv[i] = new CrtLevel { Name = SessName[i / 2] + (i % 2 == 0 ? " H" : " L") };
				font = new SimpleFont("Arial", FontSize); fontSm = new SimpleFont("Arial", Math.Max(7, FontSize - 2));
				marks.Clear(); eqs.Clear(); sts.Clear(); LoadUi(); uiDirty = true;
				if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => AttachUi());
			}
			else if (State == State.Terminated)
			{
				try { if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => DetachUi()); else DetachUi(); } catch { }
			}
		}

		// ===================== helpers =====================
		private long BucketId(DateTime t)
		{
			DateTime epoch = new DateTime(2000, 1, 1, AnchorHour, 0, 0);
			double mins = (t - epoch).TotalMinutes; return (long)Math.Floor(mins / Math.Max(1, HtfMinutes));
		}
		private int Ago(int bar) { return CurrentBar - bar; }
		private bool Last { get { return State == State.Realtime || CurrentBar >= Count - 1; } }
		private bool InHours(DateTime t, int a, int b) { int h = t.Hour; return a <= b ? (h >= a && h < b) : (h >= a || h < b); }
		private static Brush Fz(byte a, byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromArgb(a, r, g, b)); br.Freeze(); return br; }
		private Brush BullC { get { return Brushes.Lime; } }
		private Brush BearC { get { return Brushes.HotPink; } }

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 5) return;
			Values[0][0] = 0; Values[1][0] = 0; Values[2][0] = 0; Values[3][0] = 0;
			double tr = Math.Max(High[0] - Low[0], Math.Max(Math.Abs(High[0] - Close[1]), Math.Abs(Low[0] - Close[1])));
			atr = atrN < 14 ? (atr * atrN + tr) / (atrN + 1) : (atr * 13 + tr) / 14; atrN++;

			lastClose = Close[0]; lastBarIdx = CurrentBar; needBuild = true;
			try { UpdateStructure(); } catch (Exception ex) { dbg = "UpdateStructure " + ex.GetType().Name + ": " + ex.Message; }
			try { UpdateHtf(); } catch (Exception ex) { dbg = "UpdateHtf " + ex.GetType().Name + ": " + ex.Message; }
			try { if (ShowOb) UpdateOb(); } catch (Exception ex) { dbg = "UpdateOb " + ex.GetType().Name + ": " + ex.Message; }
			try { UpdateFvgs(); } catch (Exception ex) { dbg = "UpdateFvgs " + ex.GetType().Name + ": " + ex.Message; }
			try { RunSetups(); } catch (Exception ex) { dbg = "RunSetups " + ex.GetType().Name + ": " + ex.Message; }
			try { UpdateLevels(); } catch (Exception ex) { dbg = "LVL " + ex.GetType().Name + ": " + ex.Message + " @" + (ex.StackTrace ?? "").Split('\n')[0].Trim(); }
			try { if (uiDirty) ApplyUi(); } catch (Exception ex) { dbg = "UI " + ex.GetType().Name + ": " + ex.Message; }
			
		}

		protected override void OnMarketData(NinjaTrader.Data.MarketDataEventArgs e)
		{
			if (!uiDirty || State != State.Realtime || CurrentBar < 5) return;
			try { ApplyUi(); DrawAll(); } catch { }
		}

		private void ApplyUi()
		{
			uiDirty = false;
			foreach (var m in marks) { RemoveMark(m); if (ui[m.Kind - 1]) DrawMark(m); }
			foreach (var q in eqs) { RemoveEq(q); if (ui[17] && ShowEq) DrawEq(q); }
			foreach (var r in sts) { RemoveSt(r); if (ShowStructure && ui[9]) DrawSt(r); }
		}

		// ===================== HTF candle aggregation =====================
		private void UpdateHtf()
		{
			long id = BucketId(Time[0]);
			if (cur == null || id != cur.Id)
			{
				if (cur != null) { cur.Done = true; OnHtfClosed(); }
				cur = new CrtCandle { S = CurrentBar, E = CurrentBar, O = Open[0], H = High[0], L = Low[0], C = Close[0], T = Time[0], Id = id };
				htf.Add(cur); if (htf.Count > 60) htf.RemoveAt(0);
			}
			else { cur.E = CurrentBar; cur.H = Math.Max(cur.H, High[0]); cur.L = Math.Min(cur.L, Low[0]); cur.C = Close[0]; }
			int n = htf.Count; int k = Math.Min(3, n); var a = new CrtCandle[k];
			for (int i = 0; i < k; i++) { var c = htf[n - k + i]; a[i] = new CrtCandle { S = c.S, E = c.E, O = c.O, H = c.H, L = c.L, C = c.C, T = c.T, Id = c.Id, Done = c.Done }; }
			snap = a;
		}

		private void OnHtfClosed()
		{
			int n = htf.Count; if (n < 2) return;
			CrtCandle c2 = htf[n - 1]; CrtCandle rf = n >= 2 ? htf[n - 2] : null;
			// path colouring of the just-closed candle
			if (ColorPaths && c2 != null) PaintPath(c2, false);
			if (rf == null) return;
			bool rBear = rf.C < rf.O, rBull = rf.C > rf.O; double crh = rf.H, crl = rf.L, rng = crh - crl; if (rng <= 0) return;
			double buf = StopBufTicks * TickSize;
			// ---- sweep ----
			if (UseSweep && ui[0])
			{
				if (rBear && c2.L < crl && c2.C > crl && c2.C < crh)
				{
					if (ColorPaths) PaintPath(c2, true);
					setups.Add(new CrtSetup { Kind = 1, Dir = 1, Born = CurrentBar, Life = SetupLifeHtf, Lo = crl, Hi = crl + rng * ZonePct / 100.0, Ext = c2.L - buf, Crh = crh, Crl = crl, Level = crh, FirstBar = rf.S });
				}
				else if (rBull && c2.H > crh && c2.C < crh && c2.C > crl)
				{
					if (ColorPaths) PaintPath(c2, true);
					setups.Add(new CrtSetup { Kind = 1, Dir = -1, Born = CurrentBar, Life = SetupLifeHtf, Hi = crh, Lo = crh - rng * ZonePct / 100.0, Ext = c2.H + buf, Crh = crh, Crl = crl, Level = crl, FirstBar = rf.S });
				}
			}
			// ---- return / break ----
			double r2 = c2.H - c2.L;
			if (UseReturn && ui[1] && r2 > 0)
			{
				if (rBear && c2.C > c2.O && c2.C > crh)
					setups.Add(new CrtSetup { Kind = 3, Dir = 1, Born = CurrentBar, Life = SetupLifeHtf, Lo = c2.L, Hi = c2.H, Level = c2.H - r2 * RetracePct / 100.0, Ext = c2.L - buf, Crh = crh, Crl = crl, FirstBar = c2.S });
				else if (rBull && c2.C < c2.O && c2.C < crl)
					setups.Add(new CrtSetup { Kind = 3, Dir = -1, Born = CurrentBar, Life = SetupLifeHtf, Lo = c2.L, Hi = c2.H, Level = c2.L + r2 * RetracePct / 100.0, Ext = c2.H + buf, Crh = crh, Crl = crl, FirstBar = c2.S });
			}
			if (UseBreak && ui[2] && r2 > 0)
			{
				if (rBull && c2.C > c2.O && c2.C > crh)
					setups.Add(new CrtSetup { Kind = 4, Dir = 1, Born = CurrentBar, Life = SetupLifeHtf, Lo = crh, Hi = c2.H, Ext = c2.L - buf, Crh = crh, Crl = crl, FirstBar = c2.S });
				else if (rBear && c2.C < c2.O && c2.C < crl)
					setups.Add(new CrtSetup { Kind = 4, Dir = -1, Born = CurrentBar, Life = SetupLifeHtf, Lo = c2.L, Hi = crl, Ext = c2.H + buf, Crh = crh, Crl = crl, FirstBar = c2.S });
			}
			if (setups.Count > 40) setups.RemoveRange(0, setups.Count - 40);
		}

		private void PaintPath(CrtCandle c, bool sweepYellow)
		{
			for (int i = c.S; i <= c.E; i++)
			{
				int ago = CurrentBar - i; if (ago < 0 || ago > CurrentBar) continue;
				Brush b = sweepYellow ? Brushes.Yellow : (Close[ago] >= Open[ago] ? Brushes.LimeGreen : Brushes.HotPink);
				BarBrushes[ago] = b; CandleOutlineBrushes[ago] = b;
			}
		}

		// ===================== LTF structure (pivots / BOS / CHoCH) =====================
		private void UpdateStructure()
		{
			structEvt = 0; int p = PivotStrength; if (CurrentBar < 2 * p + 2) return;
			int k = p; // candidate pivot at barsAgo = p
			bool ph = true, pl = true;
			for (int j = 1; j <= p; j++) { if (!(High[k] > High[k - j])) ph = false; if (!(High[k] > High[k + j])) ph = false; if (!(Low[k] < Low[k - j])) pl = false; if (!(Low[k] < Low[k + j])) pl = false; }
			if (ph) { lastPH = High[k]; lastPHbar = CurrentBar - k; phBroken = false; AddPiv(true, High[k], lastPHbar); }
			if (pl) { lastPL = Low[k]; lastPLbar = CurrentBar - k; plBroken = false; AddPiv(false, Low[k], lastPLbar); }
			if (!double.IsNaN(lastPH) && !phBroken && Close[0] > lastPH) { phBroken = true; structEvt = trend >= 0 ? 1 : 2; trend = 1; AddSt(lastPHbar, lastPH, true, structEvt == 2); }
			if (!double.IsNaN(lastPL) && !plBroken && Close[0] < lastPL) { plBroken = true; structEvt = trend <= 0 ? -1 : -2; trend = -1; AddSt(lastPLbar, lastPL, false, structEvt == -2); }
		}

		private void AddSt(int a, double y, bool bull, bool choch)
		{
			var r = new CrtSt { Id = idGen++, A = a, B = CurrentBar, Y = y, Bull = bull, Choch = choch }; sts.Add(r);
			if (sts.Count > 40) { RemoveSt(sts[0]); sts.RemoveAt(0); }
			if (ShowStructure && ui[9]) DrawSt(r);
		}
		private void DrawSt(CrtSt r)
		{
			int a = CurrentBar - r.A, b = CurrentBar - r.B; if (a < 0 || b < 0 || a > CurrentBar) return; string tag = "crtSt" + r.Id; Brush br = r.Bull ? Brushes.DodgerBlue : Brushes.Orange;
			Draw.Line(this, tag, false, a, r.Y, b, r.Y, br, r.Choch ? DashStyleHelper.Dash : DashStyleHelper.Solid, 1);
			Draw.Text(this, tag + "t", false, r.Choch ? "CHoCH" : "BOS", (a + b) / 2, r.Y, r.Bull ? -9 : 9, br, fontSm, System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
		}
		private void RemoveSt(CrtSt r) { RemoveDrawObject("crtSt" + r.Id); RemoveDrawObject("crtSt" + r.Id + "t"); }
		private void DrawEq(CrtEq q)
		{
			int a = CurrentBar - q.A, b = CurrentBar - q.B; if (a < 0 || b < 0 || a > CurrentBar) return; string tag = "crtEq" + q.Id;
			Draw.Line(this, tag, false, a, q.Y, b, q.Y, Brushes.Gold, DashStyleHelper.Dot, 1);
			Draw.Text(this, tag + "t", false, q.High ? "EQH" : "EQL", b, q.Y, q.High ? -10 : 10, Brushes.Gold, fontSm, System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
		}
		private void RemoveEq(CrtEq q) { RemoveDrawObject("crtEq" + q.Id); RemoveDrawObject("crtEq" + q.Id + "t"); }
		private void AddPiv(bool high, double v, int bar)
		{
			if (!ShowEq) return;
			var pv = high ? pivH : pivL; var pb = high ? pivHb : pivLb; double tol = EqTolTicks * TickSize;
			for (int i = pv.Count - 1; i >= 0 && i >= pv.Count - 12; i--)
			{
				if (Math.Abs(pv[i] - v) <= tol && bar - pb[i] > 2 && bar - pb[i] < 300)
				{
					var q = new CrtEq { Id = idGen++, A = pb[i], B = bar, Y = (pv[i] + v) / 2.0, High = high }; eqs.Add(q);
					if (eqs.Count > 24) { RemoveEq(eqs[0]); eqs.RemoveAt(0); }
					if (ui[17]) DrawEq(q);
					break;
				}
			}
			pv.Add(v); pb.Add(bar); if (pv.Count > 30) { pv.RemoveAt(0); pb.RemoveAt(0); }
		}

		// ===================== order blocks =====================
		private void UpdateOb()
		{
			// mitigate
			foreach (var z in obs) if (!z.Dead) { if (z.Dir > 0 && Close[0] < z.Bot) z.Dead = true; else if (z.Dir < 0 && Close[0] > z.Top) z.Dead = true; }
			obs.RemoveAll(z => z.Dead && CurrentBar - z.Bar > 400);
			double rg = High[0] - Low[0]; if (atr <= 0 || rg < ObAtrMult * atr) return;
			double body = Math.Abs(Close[0] - Open[0]); if (body < 0.6 * rg) return;
			bool up = Close[0] > Open[0];
			for (int j = 1; j <= 5 && j <= CurrentBar; j++)
			{
				bool opp = up ? Close[j] < Open[j] : Close[j] > Open[j];
				if (opp)
				{
					if (up && Close[0] <= High[j]) break; if (!up && Close[0] >= Low[j]) break;
					obs.Add(new CrtZone { Dir = up ? 1 : -1, Bar = CurrentBar - j, Top = High[j], Bot = Low[j] });
					int live = 0; for (int i = obs.Count - 1; i >= 0; i--) if (!obs[i].Dead && ++live > MaxOb) obs[i].Dead = true;
					break;
				}
			}
		}

		// ===================== FVG tracking =====================
		private CrtCandle RefCandle() { int n = htf.Count; return n >= 2 ? htf[n - 2] : null; }
		private void UpdateFvgs()
		{
			if (!UseFvg || !ui[3] || cur == null) return; CrtCandle rf = RefCandle(); if (rf == null) return;
			foreach (var f in fvgs) if (!f.Dead) { if (f.Dir > 0 && Close[0] < f.Bot) f.Dead = true; else if (f.Dir < 0 && Close[0] > f.Top) f.Dead = true; }
			fvgs.RemoveAll(f => f.Dead && CurrentBar - f.Bar > 200);
			bool rBear = rf.C < rf.O, rBull = rf.C > rf.O; if (!rBear && !rBull) return;
			// react
			foreach (var f in fvgs)
			{
				if (f.Dead || f.Fired || CurrentBar - f.Bar < 2) continue;
				if (f.Dir > 0 && Low[0] <= f.Top && Close[0] > Open[0] && Close[0] >= f.Bot) { f.Fired = true; Fire(1, 2, f.Bot - StopBufTicks * TickSize, rf.H, "FVG"); }
				else if (f.Dir < 0 && High[0] >= f.Bot && Close[0] < Open[0] && Close[0] <= f.Top) { f.Fired = true; Fire(-1, 2, f.Top + StopBufTicks * TickSize, rf.L, "FVG"); }
			}
			if (CurrentBar < 3 || cur.S > CurrentBar) return;
			// new gap formed by bars [2],[1],[0] (middle bar = [1]); only while 2nd candle is forming
			if (rBear && Low[0] > High[2] && Close[1] > Open[1] && High[2] >= rf.L && Low[0] <= rf.H)
				fvgs.Add(new CrtFvg { Dir = 1, Bar = CurrentBar, Top = Low[0], Bot = High[2] });
			else if (rBull && High[0] < Low[2] && Close[1] < Open[1] && Low[2] <= rf.H && High[0] >= rf.L)
				fvgs.Add(new CrtFvg { Dir = -1, Bar = CurrentBar, Top = Low[2], Bot = High[0] });
		}

		// ===================== setups =====================
		private void RunSetups()
		{
			int hn = htf.Count;
			for (int i = 0; i < setups.Count; i++)
			{
				var s = setups[i]; if (s.Dead || s.Fired) continue;
				// life: expires after Life HTF candles from the closing candle
				if (cur != null && hn >= 2)
				{
					int born = 0; for (int j = hn - 1; j >= 0; j--) if (htf[j].S <= s.Born) { born = j; break; }
					if (hn - 1 - born > s.Life) { s.Dead = true; continue; }
				}
				if (s.Dir > 0 && Close[0] < s.Ext) { s.Dead = true; continue; }
				if (s.Dir < 0 && Close[0] > s.Ext) { s.Dead = true; continue; }
				if (CurrentBar <= s.Born) continue;
				bool bullBar = Close[0] > Open[0], bearBar = Close[0] < Open[0];
				if (s.Kind == 1)
				{
					if (s.Dir > 0 && Low[0] <= s.Hi && Close[0] >= s.Lo && bullBar) { s.Fired = true; Fire(1, 1, s.Ext, s.Level, "SWEEP"); }
					else if (s.Dir < 0 && High[0] >= s.Lo && Close[0] <= s.Hi && bearBar) { s.Fired = true; Fire(-1, 1, s.Ext, s.Level, "SWEEP"); }
				}
				else if (s.Kind == 3)
				{
					if (s.Dir > 0 && Low[0] <= s.Level && bullBar && Close[0] >= s.Lo) { s.Fired = true; Fire(1, 3, s.Ext, Close[0] + 2 * (Close[0] - s.Ext), "RETURN"); }
					else if (s.Dir < 0 && High[0] >= s.Level && bearBar && Close[0] <= s.Hi) { s.Fired = true; Fire(-1, 3, s.Ext, Close[0] - 2 * (s.Ext - Close[0]), "RETURN"); }
				}
				else if (s.Kind == 4)
				{
					bool ok = BreakMode == CrtBreakMode.Either ? structEvt != 0 : (BreakMode == CrtBreakMode.BOS ? Math.Abs(structEvt) == 1 : Math.Abs(structEvt) == 2);
					if (ok && s.Dir > 0 && structEvt > 0) { if (BreakFirstOnly) s.Fired = true; Fire(1, 4, s.Ext, Close[0] + 2 * (Close[0] - s.Ext), "BREAK"); }
					else if (ok && s.Dir < 0 && structEvt < 0) { if (BreakFirstOnly) s.Fired = true; Fire(-1, 4, s.Ext, Close[0] - 2 * (s.Ext - Close[0]), "BREAK"); }
				}
			}
			setups.RemoveAll(x => (x.Dead || x.Fired) && CurrentBar - x.Born > 600);
		}

		private static readonly string[] KindName = { "", "SWEEP", "FVG", "RETURN", "BREAK" };
		private void DrawMark(CrtMark m)
		{
			int ago = CurrentBar - m.Bar; if (ago < 0 || ago > CurrentBar) return; string tag = "crtM" + m.Id; bool b = m.Dir > 0; string txt = KindName[m.Kind] + (b ? " BUY" : " SELL");
			if (b) Draw.TriangleUp(this, tag, false, ago, m.Y, BullC); else Draw.TriangleDown(this, tag, false, ago, m.Y, BearC);
			Draw.Text(this, tag + "t", false, txt, ago, m.Y, b ? 22 : -22, b ? BullC : BearC, font, System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
		}
		private void RemoveMark(CrtMark m) { RemoveDrawObject("crtM" + m.Id); RemoveDrawObject("crtM" + m.Id + "t"); }
		private void Fire(int dir, int kind, double stop, double target, string name)
		{
			if (Values[0][0] != 0) return; // one signal per bar
			Values[0][0] = dir; Values[1][0] = kind; Values[2][0] = stop; Values[3][0] = target;
			bool b = dir > 0; string txt = name + (b ? " BUY" : " SELL");
			var mk = new CrtMark { Id = idGen++, Bar = CurrentBar, Dir = dir, Kind = kind, Y = b ? Low[0] - 3 * TickSize : High[0] + 3 * TickSize }; marks.Add(mk);
			if (marks.Count > 300) { RemoveMark(marks[0]); marks.RemoveAt(0); }
			if (ui[kind - 1]) DrawMark(mk);
			if (AlertOnSignals && State == State.Realtime) Alert("crt" + CurrentBar, Priority.Medium, "CRT " + txt + " " + Instrument.FullName, NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + AlertSound, 10, Brushes.Black, Brushes.Yellow);
		}

		// ===================== key levels =====================
		private void UpdateLevels()
		{
			DateTime t = Time[0]; DateTime ts = t.AddHours(24 - AnchorHour);
			long d = ts.Date.Ticks / TimeSpan.TicksPerDay; long w = (long)Math.Floor((ts.Date - new DateTime(2000, 1, 3)).TotalDays / 7.0); long m = ts.Year * 12L + ts.Month;
			if (d != dayId) { if (dayId != long.MinValue) { pdh.V = dH; pdl.V = dL; pdh.Bar = pdl.Bar = CurrentBar; pdh.Broken = pdl.Broken = false; } dayId = d; dH = High[0]; dL = Low[0]; } else { dH = Math.Max(dH, High[0]); dL = Math.Min(dL, Low[0]); }
			if (w != weekId) { if (weekId != long.MinValue) { pwh.V = wH; pwl.V = wL; pwh.Bar = pwl.Bar = CurrentBar; pwh.Broken = pwl.Broken = false; } weekId = w; wH = High[0]; wL = Low[0]; } else { wH = Math.Max(wH, High[0]); wL = Math.Min(wL, Low[0]); }
			if (m != monId) { if (monId != long.MinValue) { pmh.V = mH; pml.V = mL; pmh.Bar = pml.Bar = CurrentBar; pmh.Broken = pml.Broken = false; } monId = m; mH = High[0]; mL = Low[0]; } else { mH = Math.Max(mH, High[0]); mL = Math.Min(mL, Low[0]); }
			foreach (var lv in new[] { pdh, pwh, pmh }) if (!double.IsNaN(lv.V) && High[0] > lv.V) lv.Broken = true;
			foreach (var lv in new[] { pdl, pwl, pml }) if (!double.IsNaN(lv.V) && Low[0] < lv.V) lv.Broken = true;
			// daily open (first bar at/after DailyOpenHour each calendar day)
			long cd = t.Date.Ticks / TimeSpan.TicksPerDay;
			if (cd != dayOpenDayId && t.Hour >= DailyOpenHour) { dayOpenDayId = cd; dOpen.V = Open[0]; dOpen.Bar = CurrentBar; }
			// sessions
			int[] a = { AsiaStart, LondonStart, NyStart }, e = { AsiaEnd, LondonEnd, NyEnd };
			for (int i = 0; i < 3; i++)
			{
				bool inS = InHours(t, a[i], e[i]);
				if (inS) { if (!sIn[i]) { sIn[i] = true; sH[i] = High[0]; sL[i] = Low[0]; sStart[i] = CurrentBar; } else { sH[i] = Math.Max(sH[i], High[0]); sL[i] = Math.Min(sL[i], Low[0]); } }
				else if (sIn[i]) { sIn[i] = false; sessLv[2 * i].V = sH[i]; sessLv[2 * i + 1].V = sL[i]; sessLv[2 * i].Bar = sessLv[2 * i + 1].Bar = sStart[i]; sessLv[2 * i].Broken = sessLv[2 * i + 1].Broken = false; }
			}
			for (int i = 0; i < 3; i++) { if (!double.IsNaN(sessLv[2 * i].V) && High[0] > sessLv[2 * i].V && !sIn[i]) sessLv[2 * i].Broken = true; if (!double.IsNaN(sessLv[2 * i + 1].V) && Low[0] < sessLv[2 * i + 1].V && !sIn[i]) sessLv[2 * i + 1].Broken = true; }
			if (ShadeSessions)
			{
				if (sIn[0]) BackBrush = Fz(18, 120, 120, 255); else if (sIn[1]) BackBrush = Fz(18, 255, 170, 0); else if (sIn[2]) BackBrush = Fz(18, 0, 200, 120);
			}
		}

		// ===================== layers (lines / zones): built on the engine thread, painted in OnRender =====================
		public class RItem { public bool Box; public double Y1, Y2; public int A, B, Lay, Rgb; public float Alpha = 1f; public bool Dash; public string Text; public bool Above; }
		private volatile RItem[] items = new RItem[0]; private string dbg = ""; private double lastClose; private int lastBarIdx = -1; private volatile bool needBuild = true; private int paintCount, buildCount;
		private List<RItem> bld;
		private void L(int lay, double y, int a, int rgb, bool dash, string text, bool above = true)
		{
			if (double.IsNaN(y) || a < 0) return; bld.Add(new RItem { Lay = lay, Y1 = y, A = Math.Max(0, a), B = CurrentBar + ExtendBars, Rgb = rgb, Dash = dash, Text = text, Above = above });
		}
		private void Bx2(int lay, double top, double bot, int a, int rgb, float alpha, string text)
		{
			if (double.IsNaN(top) || double.IsNaN(bot) || a < 0) return; bld.Add(new RItem { Box = true, Lay = lay, Y1 = top, Y2 = bot, A = Math.Max(0, a), B = CurrentBar + ExtendBars, Rgb = rgb, Alpha = alpha, Text = text });
		}
		private void LevelItem(int lay, CrtLevel lv, int rgb)
		{
			if (double.IsNaN(lv.V) || lv.Bar < 0) return; L(lay, lv.V, lv.Bar, rgb, lv.Broken, lv.Name + (lv.Broken ? "*" : ""));
		}
		private void BuildRender()
		{
			bld = new List<RItem>();
			if (ShowPdhPdl) { LevelItem(10, pdh, 0x00BFFF); LevelItem(10, pdl, 0x00BFFF); }
			if (ShowPwhPwl) { LevelItem(11, pwh, 0x9370DB); LevelItem(11, pwl, 0x9370DB); }
			if (ShowPmhPml) { LevelItem(12, pmh, 0xDA70D6); LevelItem(12, pml, 0xDA70D6); }
			if (ShowDailyOpen) LevelItem(16, dOpen, 0xC0C0C0);
			if (ShowSessions) { int[] sc = { 0x6A5ACD, 0xFFA500, 0x3CB371 }; for (int i = 0; i < 3; i++) { LevelItem(13 + i, sessLv[2 * i], sc[i]); LevelItem(13 + i, sessLv[2 * i + 1], sc[i]); } }
			if (ShowRound && RoundStep > 0 && lastBarIdx >= 0)
			{
				double step = RoundStep, b0 = Math.Floor(lastClose / step) * step;
				for (int i = -RoundCount + 1; i <= RoundCount; i++) { double y = b0 + i * step; L(19, y, Math.Max(0, lastBarIdx - 150), 0x808080, true, y.ToString("0")); }
			}
			if (cur != null && ShowOlc)
			{
				double mid = (cur.H + cur.L) / 2.0;
				L(8, cur.O, cur.S, 0xFFFFFF, true, "O"); L(8, cur.H, cur.S, 0xD3D3D3, false, "H"); L(8, cur.L, cur.S, 0xD3D3D3, false, "L"); L(8, cur.C, cur.S, cur.C >= cur.O ? 0x32CD32 : 0xFF69B4, true, "C");
				if (ShowHalves) { Bx2(8, cur.H, mid, cur.S, 0xFF5078, 0.10f, null); Bx2(8, mid, cur.L, cur.S, 0x28C878, 0.10f, null); }
			}
			CrtCandle rf = RefCandle();
			if (rf != null) { L(7, rf.H, rf.S, 0xFFFF00, true, "CRH"); L(7, rf.L, rf.S, 0xFFFF00, true, "CRL", false); }
			int zi = 0;
			for (int i = setups.Count - 1; i >= 0 && zi < 6; i--)
			{
				var st = setups[i]; if (st.Dead || st.Fired || st.Kind == 4) continue; bool b = st.Dir > 0;
				Bx2(st.Kind == 1 ? 4 : 5, st.Hi, st.Lo, st.Born, b ? 0x1EC85A : 0xFF4690, 0.22f, (st.Kind == 1 ? "SWEEP" : "RETURN") + (b ? " BUY zone" : " SELL zone")); zi++;
			}
			int fi = 0;
			for (int i = fvgs.Count - 1; i >= 0 && fi < 6; i--)
			{
				var f = fvgs[i]; if (f.Dead || f.Fired) continue; bool b = f.Dir > 0; Bx2(6, f.Top, f.Bot, f.Bar, b ? 0x14A078 : 0xC83C50, 0.22f, "FVG"); fi++;
			}
			int oi = 0;
			if (ShowOb) for (int i = obs.Count - 1; i >= 0 && oi < MaxOb; i--) { var z = obs[i]; if (z.Dead) continue; bool b = z.Dir > 0; Bx2(18, z.Top, z.Bot, z.Bar, b ? 0x1E78FF : 0xFF5A28, 0.22f, b ? "Bull OB" : "Bear OB"); oi++; }
			items = bld.ToArray(); buildCount++;
		}
		private void DrawAll() { try { BuildRender(); } catch (Exception ex) { dbg = "BUILD " + ex.GetType().Name + ": " + ex.Message; Print("SneakyCRTAlpha BuildRender: " + ex); } }

		private void PaintLayers(ChartControl cc, ChartScale cs)
		{
			var arr = items; if (arr == null || arr.Length == 0 || ChartBars == null) return;
			var cache = new Dictionary<int, SharpDX.Direct2D1.SolidColorBrush>(); var tf = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", Math.Max(8f, FontSize - 1f));
			Func<int, float, SharpDX.Direct2D1.SolidColorBrush> br = delegate (int rgb, float al) { int key = rgb ^ ((int)(al * 100) << 24); SharpDX.Direct2D1.SolidColorBrush b; if (!cache.TryGetValue(key, out b)) { b = Bx(rgb, al); cache[key] = b; } return b; };
			try
			{
				int last = Math.Max(1, Count - 1); float xl = cc.GetXByBarIndex(ChartBars, last), xp = cc.GetXByBarIndex(ChartBars, last - 1); float sp = Math.Max(1f, xl - xp);
				Func<int, float> X = delegate (int i) { return i <= last ? cc.GetXByBarIndex(ChartBars, Math.Max(0, i)) : xl + (i - last) * sp; };
				float left = ChartPanel.X, right = ChartPanel.X + ChartPanel.W, top = ChartPanel.Y, bot = ChartPanel.Y + ChartPanel.H;
				foreach (var it in arr)
				{
					if (it.Lay < 0 || it.Lay >= NUI || !ui[it.Lay]) continue;
					float x1 = Math.Max(left, X(it.A)), x2 = Math.Min(right, X(it.B)); if (x2 <= x1) continue;
					if (it.Box)
					{
						float ya = cs.GetYByValue(it.Y1), yb = cs.GetYByValue(it.Y2); float t = Math.Min(ya, yb), h = Math.Abs(yb - ya); if (t > bot || t + h < top) continue;
						RenderTarget.FillRectangle(new SharpDX.RectangleF(x1, t, x2 - x1, Math.Max(1f, h)), br(it.Rgb, it.Alpha));
						if (it.Text != null) { RenderTarget.DrawRectangle(new SharpDX.RectangleF(x1, t, x2 - x1, Math.Max(1f, h)), br(it.Rgb, 0.8f), 1f); RenderTarget.DrawText(it.Text, tf, new SharpDX.RectangleF(x1 + 3f, t, 200f, 16f), br(0xE8EEF9, 1f)); }
					}
					else
					{
						float y = cs.GetYByValue(it.Y1); if (y < top || y > bot) continue; var lb = br(it.Rgb, 0.9f);
						if (!it.Dash) RenderTarget.DrawLine(new SharpDX.Vector2(x1, y), new SharpDX.Vector2(x2, y), lb, 1f);
						else for (float x = x1; x < x2; x += 8f) RenderTarget.DrawLine(new SharpDX.Vector2(x, y), new SharpDX.Vector2(Math.Min(x + 4f, x2), y), lb, 1f);
						if (it.Text != null) RenderTarget.DrawText(it.Text, tf, new SharpDX.RectangleF(Math.Max(x1, x2 - 60f), it.Above ? y - 15f : y + 1f, 70f, 16f), lb);
					}
				}
			}
			catch (Exception ex) { dbg = ex.GetType().Name + ": " + ex.Message; }
			finally { foreach (var kv in cache) { try { kv.Value.Dispose(); } catch { } } try { tf.Dispose(); } catch { } }
		}

		// ===================== 3 HTF candles at the right edge =====================
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || ChartPanel == null || ChartBars == null) return;
			if (needBuild || buildCount == 0) { needBuild = false; DrawAll(); }
			PaintLayers(chartControl, chartScale);
			DrawPanel();
			if (!ShowHtfCandles || !ui[8]) return;
			var s = snap; if (s == null || s.Length == 0) return;
			var dx = new List<SharpDX.Direct2D1.SolidColorBrush>();
			try
			{
				float x0 = ChartPanel.X + ChartPanel.W - 150f, w = 20f, gap = 14f;
				var up = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.1f, 0.8f, 0.5f, 0.9f)); dx.Add(up);
				var dn = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1f, 0.35f, 0.55f, 0.9f)); dx.Add(dn);
				var wk = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.8f, 0.8f, 0.8f, 0.9f)); dx.Add(wk);
				for (int i = 0; i < s.Length; i++)
				{
					var c = s[i]; float x = x0 + i * (w + gap);
					float yo = chartScale.GetYByValue(c.O), yc = chartScale.GetYByValue(c.C), yh = chartScale.GetYByValue(c.H), yl = chartScale.GetYByValue(c.L);
					bool bull = c.C >= c.O; var b = bull ? up : dn;
					RenderTarget.DrawLine(new SharpDX.Vector2(x + w / 2, yh), new SharpDX.Vector2(x + w / 2, yl), wk, 1f);
					float top = Math.Min(yo, yc), h = Math.Max(2f, Math.Abs(yc - yo));
					RenderTarget.FillRectangle(new SharpDX.RectangleF(x, top, w, h), b);
				}
			}
			catch { }
			finally { foreach (var b in dx) b.Dispose(); }
		}


		// ===================== on-chart dashboard (click to toggle, drag the title) =====================
		private SharpDX.Direct2D1.SolidColorBrush Bx(int rgb, float a) { return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a)); }
		private void TxtD(string t, SharpDX.DirectWrite.TextFormat f, float x, float y, float w, float h, SharpDX.Direct2D1.SolidColorBrush b) { RenderTarget.DrawText(t, f, new SharpDX.RectangleF(x, y, w, h), b); }
		private static readonly string[] Sec = { "SIGNALS", "TRADE ZONES", "STRUCTURE", "KEY LEVEL" };
		private static readonly int[][] SecIdx = { new[] { 0, 2, 1, 3 }.Length == 4 ? new[] { 0, 1, 2, 3 } : null, new[] { 4, 5, 6, 7, 8 }, new[] { 9 }, new[] { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 } };
		private void DrawPanel()
		{
			if (!ShowDashboard || ChartPanel == null) return;
			var tf = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", 11f);
			var tfs = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", 9f);
			var tfb = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", 13f);
			var bgB = Bx(0x131C2E, 0.95f); var brdB = Bx(0x2A3A5C, 1f); var txtB = Bx(0xE8EEF9, 1f); var dimB = Bx(0x8A97B2, 1f); var orgB = Bx(0xFFA21F, 1f); var onB = Bx(0x2AD0A0, 1f); var offB = Bx(0x5B6785, 1f); var chipB = Bx(0x1F2B44, 1f); var chipOn = Bx(0x2B3D63, 1f);
			try
			{
				float W = 296f, pad = 14f, chipH = 24f, gap = 6f, rowGap = 8f;
				chipRects.Clear();
				float x0 = uiX < 0 ? ChartPanel.X + ChartPanel.W - W - 90f : ChartPanel.X + uiX; float y0 = ChartPanel.Y + uiY;
				// layout pass
				float y = y0 + 40f; var layout = new List<object[]>();
				if (!uiCollapsed)
				{
					for (int sIdx = 0; sIdx < 4; sIdx++)
					{
						layout.Add(new object[] { "sec", sIdx, y }); y += 20f; float x = x0 + pad;
						foreach (int id in SecIdx[sIdx])
						{
							float cw = UiName[id].Length * 6.3f + 30f;
							if (x + cw > x0 + W - pad + 0.5f) { x = x0 + pad; y += chipH + gap; }
							layout.Add(new object[] { "chip", id, x, y, cw }); x += cw + gap;
						}
						y += chipH + rowGap + 6f;
					}
					y += 16f;
				}
				pW = W; pH = uiCollapsed ? 36f : y - y0;
				RenderTarget.FillRectangle(new SharpDX.RectangleF(x0, y0, pW, pH), bgB); RenderTarget.DrawRectangle(new SharpDX.RectangleF(x0, y0, pW, pH), brdB, 1f);
				RenderTarget.FillRectangle(new SharpDX.RectangleF(x0 + 10f, y0 + 10f, 3f, 18f), orgB);
				TxtD("CRT", tfb, x0 + 20f, y0 + 6f, 40f, 24f, txtB); TxtD("Dashboard", tf, x0 + 54f, y0 + 9f, 100f, 20f, dimB); TxtD(uiCollapsed ? "\u25B8" : "\u25BE", tf, x0 + W - 26f, y0 + 8f, 20f, 20f, dimB);
				titleRect = new[] { x0, y0, W - 34f, 36f }; arrowRect = new[] { x0 + W - 34f, y0, 34f, 36f };
				allRect = new float[4]; noneRect = new float[4];
				if (!uiCollapsed)
				{
					foreach (var it in layout)
					{
						string kind = (string)it[0];
						if (kind == "sec")
						{
							int sIdx = (int)it[1]; float yy = (float)it[2];
							RenderTarget.DrawLine(new SharpDX.Vector2(x0 + 8f, yy - 2f), new SharpDX.Vector2(x0 + W - 8f, yy - 2f), brdB, 1f);
							TxtD(Sec[sIdx], tfs, x0 + pad, yy + 2f, 120f, 14f, dimB);
							if (sIdx == 3)
							{
								TxtD("All", tfs, x0 + W - 70f, yy + 2f, 24f, 14f, orgB); TxtD("None", tfs, x0 + W - 42f, yy + 2f, 34f, 14f, orgB);
								allRect = new[] { x0 + W - 74f, yy, 28f, 16f }; noneRect = new[] { x0 + W - 44f, yy, 38f, 16f };
							}
						}
						else
						{
							int id = (int)it[1]; float cx = (float)it[2], cy = (float)it[3], cw = (float)it[4]; bool on = ui[id];
							RenderTarget.FillRectangle(new SharpDX.RectangleF(cx, cy, cw, chipH), on ? chipOn : chipB); RenderTarget.DrawRectangle(new SharpDX.RectangleF(cx, cy, cw, chipH), brdB, 1f);
							RenderTarget.FillRectangle(new SharpDX.RectangleF(cx + 8f, cy + chipH / 2f - 4f, 8f, 8f), on ? onB : offB);
							TxtD(UiName[id], tf, cx + 22f, cy + 3f, cw - 24f, chipH, txtB);
							chipRects.Add(new[] { cx, cy, cw, chipH, id });
						}
					}
					TxtD("Click to toggle \u00B7 drag the title to move  [v6 L" + (items == null ? -1 : items.Length) + " B" + buildCount + " " + dbg + "]", tfs, x0 + 12f, y0 + pH - 22f, W - 16f, 14f, dimB);
				}
			}
			catch { }
			finally { foreach (var d in new IDisposable[] { bgB, brdB, txtB, dimB, orgB, onB, offB, chipB, chipOn, tf, tfs, tfb }) { try { d.Dispose(); } catch { } } }
		}
		private double DevScale()
		{
			try { var src = System.Windows.PresentationSource.FromVisual(dragCtl); if (src != null && src.CompositionTarget != null) return src.CompositionTarget.TransformToDevice.M11; } catch { }
			return 1.0;
		}
		private void AttachUi()
		{
			try
			{
				if (ChartControl == null || dragCtl != null) return; dragCtl = ChartControl;
				dragCtl.PreviewMouseLeftButtonDown += UiDown; dragCtl.PreviewMouseMove += UiMove; dragCtl.PreviewMouseLeftButtonUp += UiUp;
			}
			catch { }
		}
		private void DetachUi()
		{
			try
			{
				if (dragCtl == null) return;
				dragCtl.PreviewMouseLeftButtonDown -= UiDown; dragCtl.PreviewMouseMove -= UiMove; dragCtl.PreviewMouseLeftButtonUp -= UiUp; dragCtl = null;
			}
			catch { }
		}
		private static bool In(float[] r, double x, double y) { return r != null && r[2] > 0 && x >= r[0] && x <= r[0] + r[2] && y >= r[1] && y <= r[1] + r[3]; }
		private void Redraw()
		{
			try { if (dragCtl != null) dragCtl.InvalidateVisual(); } catch { }
			try { ForceRefresh(); } catch { }
		}
		private void ApplyUiNow()
		{
			try { TriggerCustomEvent(o => { try { if (uiDirty) ApplyUi(); ForceRefresh(); } catch { } }, null); } catch { }
		}
		private void UiDown(object sd, System.Windows.Input.MouseButtonEventArgs e)
		{
			try
			{
				if (dragCtl == null || !ShowDashboard) return; double sc = DevScale(); var p = e.GetPosition(dragCtl); double mx = p.X * sc, my = p.Y * sc;
				if (In(arrowRect, mx, my)) { uiCollapsed = !uiCollapsed; SaveUi(); e.Handled = true; Redraw(); return; }
				if (!uiCollapsed)
				{
					if (In(allRect, mx, my) || In(noneRect, mx, my)) { bool v = In(allRect, mx, my); for (int i = 10; i <= 19; i++) ui[i] = v; uiDirty = true; SaveUi(); e.Handled = true; Redraw(); ApplyUiNow(); return; }
					foreach (var r in chipRects) if (In(r, mx, my)) { int id = (int)r[4]; ui[id] = !ui[id]; uiDirty = true; SaveUi(); e.Handled = true; Redraw(); ApplyUiNow(); return; }
				}
				if (In(titleRect, mx, my)) { dragging = true; dStart = new System.Windows.Point(mx, my); dX0 = uiX < 0 ? (float)(titleRect[0] - ChartPanel.X) : uiX; dY0 = uiY; dragCtl.CaptureMouse(); e.Handled = true; }
			}
			catch { }
		}
		private void UiMove(object sd, System.Windows.Input.MouseEventArgs e)
		{
			try
			{
				if (!dragging || dragCtl == null) return; double sc = DevScale(); var p = e.GetPosition(dragCtl);
				uiX = Math.Max(0f, dX0 + (float)(p.X * sc - dStart.X)); uiY = Math.Max(0f, dY0 + (float)(p.Y * sc - dStart.Y)); e.Handled = true; Redraw();
			}
			catch { }
		}
		private void UiUp(object sd, System.Windows.Input.MouseButtonEventArgs e)
		{
			try { if (!dragging) return; dragging = false; dragCtl.ReleaseMouseCapture(); SaveUi(); e.Handled = true; }
			catch { }
		}

		#region Properties
		[Range(2, 1440)] [Display(Name = "HTF minutes", Order = 1, GroupName = "1. Higher timeframe", Description = "Reference timeframe built from chart bars; must be larger than the chart timeframe. 15, 30, 60, 240...")] public int HtfMinutes { get; set; }
		[Range(0, 23)] [Display(Name = "HTF anchor hour (chart time)", Order = 2, GroupName = "1. Higher timeframe", Description = "18 = CME futures day start")] public int AnchorHour { get; set; }
		[Display(Name = "Show 3 HTF candles at right edge", Order = 3, GroupName = "1. Higher timeframe")] public bool ShowHtfCandles { get; set; }
		[Display(Name = "Show HTF open/high/low/close lines", Order = 4, GroupName = "1. Higher timeframe")] public bool ShowOlc { get; set; }
		[Display(Name = "Shade upper / lower half", Order = 5, GroupName = "1. Higher timeframe")] public bool ShowHalves { get; set; }
		[Display(Name = "Colour candle paths (green/pink ref, yellow sweep)", Order = 6, GroupName = "1. Higher timeframe")] public bool ColorPaths { get; set; }
		[Range(1, 200)] [Display(Name = "Extend lines/zones (bars)", Order = 7, GroupName = "1. Higher timeframe")] public int ExtendBars { get; set; }
		[Display(Name = "Sweep signal", Order = 1, GroupName = "2. Signals")] public bool UseSweep { get; set; }
		[Display(Name = "FVG signal", Order = 2, GroupName = "2. Signals")] public bool UseFvg { get; set; }
		[Display(Name = "Return signal", Order = 3, GroupName = "2. Signals")] public bool UseReturn { get; set; }
		[Display(Name = "Break signal", Order = 4, GroupName = "2. Signals")] public bool UseBreak { get; set; }
		[Range(10, 100)] [Display(Name = "Sweep entry zone % of ref range", Order = 5, GroupName = "2. Signals")] public int ZonePct { get; set; }
		[Range(10, 90)] [Display(Name = "Return min retrace %", Order = 6, GroupName = "2. Signals")] public int RetracePct { get; set; }
		[Range(1, 6)] [Display(Name = "Setup life (HTF candles)", Order = 7, GroupName = "2. Signals")] public int SetupLifeHtf { get; set; }
		[Range(0, 50)] [Display(Name = "Stop buffer (ticks)", Order = 8, GroupName = "2. Signals")] public int StopBufTicks { get; set; }
		[Display(Name = "Break confirmation", Order = 9, GroupName = "2. Signals")] public CrtBreakMode BreakMode { get; set; }
		[Display(Name = "Break: first signal only", Order = 10, GroupName = "2. Signals")] public bool BreakFirstOnly { get; set; }
		[Range(1, 6)] [Display(Name = "Swing pivot strength (bars)", Order = 11, GroupName = "2. Signals")] public int PivotStrength { get; set; }
		[Display(Name = "PDH / PDL", Order = 1, GroupName = "3. Key levels")] public bool ShowPdhPdl { get; set; }
		[Display(Name = "PWH / PWL", Order = 2, GroupName = "3. Key levels")] public bool ShowPwhPwl { get; set; }
		[Display(Name = "PMH / PML", Order = 3, GroupName = "3. Key levels")] public bool ShowPmhPml { get; set; }
		[Display(Name = "Asia / London / NY ranges", Order = 4, GroupName = "3. Key levels")] public bool ShowSessions { get; set; }
		[Display(Name = "Shade active session background", Order = 5, GroupName = "3. Key levels")] public bool ShadeSessions { get; set; }
		[Display(Name = "Daily open", Order = 6, GroupName = "3. Key levels")] public bool ShowDailyOpen { get; set; }
		[Display(Name = "Equal highs / lows", Order = 7, GroupName = "3. Key levels")] public bool ShowEq { get; set; }
		[Display(Name = "Order blocks", Order = 8, GroupName = "3. Key levels")] public bool ShowOb { get; set; }
		[Display(Name = "Round numbers", Order = 9, GroupName = "3. Key levels")] public bool ShowRound { get; set; }
		[Display(Name = "BOS / CHoCH marks", Order = 10, GroupName = "3. Key levels")] public bool ShowStructure { get; set; }
		[Display(Name = "Show dashboard panel", Order = 11, GroupName = "3. Key levels")] public bool ShowDashboard { get; set; }
		[Range(0, 23)] [Display(Name = "Asia start hour", Order = 1, GroupName = "4. Session hours (chart time)")] public int AsiaStart { get; set; }
		[Range(0, 23)] [Display(Name = "Asia end hour", Order = 2, GroupName = "4. Session hours (chart time)")] public int AsiaEnd { get; set; }
		[Range(0, 23)] [Display(Name = "London start hour", Order = 3, GroupName = "4. Session hours (chart time)")] public int LondonStart { get; set; }
		[Range(0, 23)] [Display(Name = "London end hour", Order = 4, GroupName = "4. Session hours (chart time)")] public int LondonEnd { get; set; }
		[Range(0, 23)] [Display(Name = "NY start hour", Order = 5, GroupName = "4. Session hours (chart time)")] public int NyStart { get; set; }
		[Range(0, 23)] [Display(Name = "NY end hour", Order = 6, GroupName = "4. Session hours (chart time)")] public int NyEnd { get; set; }
		[Range(0, 23)] [Display(Name = "Daily open hour", Order = 7, GroupName = "4. Session hours (chart time)")] public int DailyOpenHour { get; set; }
		[Range(1, 100)] [Display(Name = "EQH/EQL tolerance (ticks)", Order = 1, GroupName = "5. Detail")] public int EqTolTicks { get; set; }
		[Range(0.5, 5.0)] [Display(Name = "Order block min impulse (x ATR)", Order = 2, GroupName = "5. Detail")] public double ObAtrMult { get; set; }
		[Range(1, 8)] [Display(Name = "Max order blocks shown", Order = 3, GroupName = "5. Detail")] public int MaxOb { get; set; }
		[Range(1, 5000)] [Display(Name = "Round number step (points)", Order = 4, GroupName = "5. Detail")] public double RoundStep { get; set; }
		[Range(1, 6)] [Display(Name = "Round numbers each side", Order = 5, GroupName = "5. Detail")] public int RoundCount { get; set; }
		[Range(6, 30)] [Display(Name = "Font size", Order = 6, GroupName = "5. Detail")] public int FontSize { get; set; }
		[Display(Name = "Alert on signals", Order = 1, GroupName = "6. Alerts")] public bool AlertOnSignals { get; set; }
		[Display(Name = "Alert sound file (NinjaTrader\\sounds)", Order = 2, GroupName = "6. Alerts")] public string AlertSound { get; set; }
		#endregion
	}

	public enum CrtBreakMode { Either, BOS, CHoCH }
}
