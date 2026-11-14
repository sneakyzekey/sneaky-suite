// SneakyReversion: NinjaTrader 8 version of the "High-Conviction Reversion" idea (HA direction + Bollinger / value-area reversion).
// This is an independent implementation from the public description, NOT NINZA's indicators.
// Signals (+1 long / -1 short / 0 none), written on the first tick of the bar AFTER the signal bar (Calculate = OnEachTick):
//   0 PullbackSignal    Setup 1A: touch of the outer Bollinger band and close back inside, in the Heikin Ashi direction
//   1 MidRetestSignal   Setup 1B: pushed through the middle band within N bars, then a bar closes back across it, HA direction
//   2 ValueAreaSignal   Setup 2: left the rolling value area (VAL/VAH) within N bars, then closes back through the POC, HA direction
//   3 HaDirection       Heikin Ashi colour of the last closed bar (+1/-1)
//   4-9 lines: BbMiddle, BbUpper, BbLower, Poc, Vah, Val (plotted on the bar they describe)
//   10 AnySignal        first non-zero of PullbackSignal, ValueAreaSignal, MidRetestSignal (same priority as the arrows)
// NOTE: backtests on NQ 1-minute and 2000-tick data showed NO edge for these rules. Forward-test on SIM only.
#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
// Streaming core for SneakyReversion. One Add() per CLOSED bar.
internal struct RvOut
{
	public int Dir, S1A, S1B, S2;
	public double Mid, Up, Lo, Poc, Vah, Val;
}

internal class SneakyReversionCore
{
	public int BbLen = 20, RetestWin = 5, ProfWin = 240, SeenWin = 10;
	public double BbMult = 2.0, BinSize = 1.0, VaPct = 0.70;
	public bool UseHa = true;

	private readonly System.Collections.Generic.List<double> o = new System.Collections.Generic.List<double>();
	private readonly System.Collections.Generic.List<double> h = new System.Collections.Generic.List<double>();
	private readonly System.Collections.Generic.List<double> l = new System.Collections.Generic.List<double>();
	private readonly System.Collections.Generic.List<double> c = new System.Collections.Generic.List<double>();
	private readonly System.Collections.Generic.List<double> v = new System.Collections.Generic.List<double>();
	private readonly System.Collections.Generic.List<double> ma = new System.Collections.Generic.List<double>();
	private readonly System.Collections.Generic.List<long> bin = new System.Collections.Generic.List<long>();
	private readonly System.Collections.Generic.List<double> poc = new System.Collections.Generic.List<double>();
	private readonly System.Collections.Generic.List<double> vah = new System.Collections.Generic.List<double>();
	private readonly System.Collections.Generic.List<double> val = new System.Collections.Generic.List<double>();
	private readonly System.Collections.Generic.Dictionary<long, double> hist = new System.Collections.Generic.Dictionary<long, double>();
	private double haOpenPrev, haClosePrev;


	public RvOut Add(double O, double H, double L, double C, double V)
	{
		RvOut r = new RvOut();
		o.Add(O); h.Add(H); l.Add(L); c.Add(C); v.Add(V);
		int i = c.Count - 1;

		// ---- Heikin Ashi colour (bar i) ----
		double hc = (O + H + L + C) / 4.0, ho;
		if (i == 0) ho = (O + C) / 2.0; else ho = (haOpenPrev + haClosePrev) / 2.0;
		haOpenPrev = ho; haClosePrev = hc;
		int col = hc > ho ? 1 : (hc < ho ? -1 : 0);
		r.Dir = col;

		// ---- Bollinger ----
		double m = double.NaN, sd = double.NaN, up = double.NaN, lo = double.NaN;
		if (i >= BbLen - 1)
		{
			double s = 0; for (int j = i - BbLen + 1; j <= i; j++) s += c[j];
			m = s / BbLen;
			double q = 0; for (int j = i - BbLen + 1; j <= i; j++) { double d = c[j] - m; q += d * d; }
			sd = System.Math.Sqrt(q / BbLen);
			up = m + BbMult * sd; lo = m - BbMult * sd;
		}
		ma.Add(m);
		bool ok = i >= BbLen - 1;
		r.Mid = m; r.Up = up; r.Lo = lo;

		// Setup 1A: pullback to band in HA direction
		if (ok)
		{
			bool touchL = L <= lo && C > lo, touchU = H >= up && C < up;
			if (touchL && col > 0) r.S1A = 1;
			if (touchU && col < 0) r.S1A = -1;
		}

		// Setup 1B: pushed through middle band within RetestWin bars, then bar closes back across it
		if (ok && i >= 1 && i >= RetestWin)
		{
			bool above = false, below = false;
			for (int j = i - RetestWin; j < i; j++)
			{
				if (!double.IsNaN(ma[j]))
				{
					if (c[j] > ma[j]) above = true;
					if (c[j] < ma[j]) below = true;
				}
			}
			double cp = c[i - 1], mp = ma[i - 1];
			if (!double.IsNaN(mp))
			{
				if (col < 0 && above && C < m && cp >= mp && C < O) r.S1B = -1;
				if (col > 0 && below && C > m && cp <= mp && C > O) r.S1B = 1;
			}
		}

		// ---- Rolling volume profile (hlc3 binned) ----
		long b = (long)System.Math.Floor(((H + L + C) / 3.0) / BinSize);
		bin.Add(b);
		double cur; hist.TryGetValue(b, out cur); hist[b] = cur + V;
		if (i >= ProfWin)
		{
			long bo = bin[i - ProfWin]; double x; hist.TryGetValue(bo, out x); hist[bo] = x - v[i - ProfWin];
		}
		double pc = double.NaN, vh = double.NaN, vl = double.NaN;
		if (i >= ProfWin - 1)
		{
			long a = long.MaxValue, z = long.MinValue;
			for (int j = i - ProfWin + 1; j <= i; j++) { if (bin[j] < a) a = bin[j]; if (bin[j] > z) z = bin[j]; }
			int len = (int)(z - a + 1);
			double[] seg = new double[len]; double tot = 0;
			for (int k = 0; k < len; k++) { double x; hist.TryGetValue(a + k, out x); seg[k] = x; tot += x; }
			if (tot > 0)
			{
				int p = 0; double best = seg[0];
				for (int k = 1; k < len; k++) if (seg[k] > best) { best = seg[k]; p = k; }
				double sum = seg[p]; int Lk = p, Rk = p;
				while (sum < VaPct * tot && (Lk > 0 || Rk < len - 1))
				{
					double upv = Rk < len - 1 ? seg[Rk + 1] : -1, dnv = Lk > 0 ? seg[Lk - 1] : -1;
					if (upv >= dnv) { Rk++; sum += seg[Rk]; } else { Lk--; sum += seg[Lk]; }
				}
				pc = (p + a + 0.5) * BinSize; vl = (Lk + a) * BinSize; vh = (Rk + a + 1) * BinSize;
			}
		}
		poc.Add(pc); vah.Add(vh); val.Add(vl);
		r.Poc = pc; r.Vah = vh; r.Val = vl;

		// Setup 2: left the value area (VAL/VAH) within SeenWin bars, then closes back through the POC
		if (!double.IsNaN(pc) && i >= SeenWin && i >= 1)
		{
			bool wasBelow = false, wasAbove = false;
			for (int j = i - SeenWin; j < i; j++)
			{
				if (!double.IsNaN(poc[j]))
				{
					if (c[j] < val[j]) wasBelow = true;
					if (c[j] > vah[j]) wasAbove = true;
				}
			}
			double cp = c[i - 1], pp = poc[i - 1];
			if (!double.IsNaN(pp))
			{
				if (wasBelow && cp <= pp && C > pc && (col > 0 || !UseHa)) r.S2 = 1;
				if (wasAbove && cp >= pp && C < pc && (col < 0 || !UseHa)) r.S2 = -1;
			}
		}
		return r;
	}
}


	public class SneakyReversion : Indicator
	{
		private SneakyReversionCore core;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description		= "HA-direction Bollinger / value-area reversion signals (+1/-1 plots). Independent implementation.";
				Name			= "SneakyReversion";
				Calculate		= Calculate.OnEachTick;
				IsOverlay		= true;
				DisplayInDataBox = true;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = true;
				ArePlotsConfigurable = true;
				IsAutoScale		= false;
				BbLength		= 20;
				BbMult			= 2.0;
				RetestWindow	= 5;
				ProfileBars		= 240;
				BinSizePoints	= 1.0;
				ValueAreaPct	= 70.0;
				SeenWindow		= 10;
				RequireHa		= true;
				ShowArrows		= true;
				ShowBands		= true;
				ShowProfile		= true;
				ShowLabels		= true;
				MarkerOffsetTicks = 4;
				AddPlot(System.Windows.Media.Brushes.DodgerBlue, "PullbackSignal");
				AddPlot(System.Windows.Media.Brushes.Transparent, "MidRetestSignal");
				AddPlot(System.Windows.Media.Brushes.Transparent, "ValueAreaSignal");
				AddPlot(System.Windows.Media.Brushes.Transparent, "HaDirection");
				AddPlot(System.Windows.Media.Brushes.Gold, "BbMiddle");
				AddPlot(System.Windows.Media.Brushes.SlateGray, "BbUpper");
				AddPlot(System.Windows.Media.Brushes.SlateGray, "BbLower");
				AddPlot(System.Windows.Media.Brushes.OrangeRed, "Poc");
				AddPlot(System.Windows.Media.Brushes.MediumPurple, "Vah");
				AddPlot(System.Windows.Media.Brushes.MediumPurple, "Val");
				AddPlot(System.Windows.Media.Brushes.Transparent, "AnySignal");
			}
			else if (State == State.DataLoaded)
			{
				core = new SneakyReversionCore
				{
					BbLen = BbLength, BbMult = BbMult, RetestWin = RetestWindow, ProfWin = ProfileBars,
					BinSize = BinSizePoints, VaPct = ValueAreaPct / 100.0, SeenWin = SeenWindow, UseHa = RequireHa
				};
			}
		}

		protected override void OnBarUpdate()
		{
			if (core == null)
				return;

			bool onClose = Calculate == Calculate.OnBarClose;
			if (!onClose && !IsFirstTickOfBar)
				return;
			int ago = onClose ? 0 : 1;
			if (CurrentBar < ago)
				return;

			RvOut r = core.Add(Open[ago], High[ago], Low[ago], Close[ago], Volume[ago]);

			Values[0][0] = r.S1A;
			Values[1][0] = r.S1B;
			Values[2][0] = r.S2;
			Values[3][0] = r.Dir;
			int any = r.S1A != 0 ? r.S1A : (r.S2 != 0 ? r.S2 : r.S1B);
			Values[10][0] = any;

			if (ShowBands)
			{
				if (!double.IsNaN(r.Mid)) { Values[4][ago] = r.Mid; Values[5][ago] = r.Up; Values[6][ago] = r.Lo; }
			}
			if (ShowProfile)
			{
				if (!double.IsNaN(r.Poc)) { Values[7][ago] = r.Poc; Values[8][ago] = r.Vah; Values[9][ago] = r.Val; }
			}

			if (ShowLabels && CurrentBar == Count - 1)
			{
				if (ShowBands && !double.IsNaN(r.Mid))
				{
					DrawLabel("RvLblMid", "BB Mid", r.Mid, 4, ago);
					DrawLabel("RvLblUp", "BB Upper", r.Up, 5, ago);
					DrawLabel("RvLblLo", "BB Lower", r.Lo, 6, ago);
				}
				if (ShowProfile && !double.IsNaN(r.Poc))
				{
					DrawLabel("RvLblPoc", "POC", r.Poc, 7, ago);
					DrawLabel("RvLblVah", "VAH", r.Vah, 8, ago);
					DrawLabel("RvLblVal", "VAL", r.Val, 9, ago);
				}
			}

			if (ShowArrows)
			{
				int sig = any;
				if (sig != 0)
				{
					double off = MarkerOffsetTicks * TickSize;
					if (sig > 0)
						Draw.ArrowUp(this, "RvL" + CurrentBar, false, ago, Low[ago] - off, System.Windows.Media.Brushes.DodgerBlue);
					else
						Draw.ArrowDown(this, "RvS" + CurrentBar, false, ago, High[ago] + off, System.Windows.Media.Brushes.OrangeRed);
				}
			}
		}

		private void DrawLabel(string tag, string name, double price, int plotIndex, int ago)
		{
			Draw.Text(this, tag, false, "  " + name + " " + Instrument.MasterInstrument.FormatPrice(price), ago, price, 0,
				Plots[plotIndex].Brush, new NinjaTrader.Gui.Tools.SimpleFont("Arial", 10), System.Windows.TextAlignment.Left,
				System.Windows.Media.Brushes.Transparent, System.Windows.Media.Brushes.Transparent, 0);
		}

		#region Properties

		[Display(Name = "Show Signal Arrows", Description = "Arrow on any Setup 1A / 1B / 2 signal.", Order = 0, GroupName = "00. Display")]
		public bool ShowArrows { get; set; }

		[Display(Name = "Show Bollinger Lines", Order = 1, GroupName = "00. Display")]
		public bool ShowBands { get; set; }

		[Display(Name = "Show POC / VAH / VAL Lines", Order = 2, GroupName = "00. Display")]
		public bool ShowProfile { get; set; }

		[Display(Name = "Show Line Labels (BB / POC / VAH / VAL)", Description = "Name and price at the right edge, in each line's plot colour.", Order = 3, GroupName = "00. Display")]
		public bool ShowLabels { get; set; }

		[Display(Name = "Marker Offset (ticks)", Order = 4, GroupName = "00. Display")]
		[Range(0, 100)]
		public int MarkerOffsetTicks { get; set; }

		[Display(Name = "Bollinger Length", Order = 0, GroupName = "01. Bollinger")]
		[Range(2, 500)]
		public int BbLength { get; set; }

		[Display(Name = "Bollinger Std Dev", Order = 1, GroupName = "01. Bollinger")]
		[Range(0.1, 10)]
		public double BbMult { get; set; }

		[Display(Name = "Setup 1B: Middle-Band Push Window (bars)", Order = 2, GroupName = "01. Bollinger")]
		[Range(1, 200)]
		public int RetestWindow { get; set; }

		[Display(Name = "Profile Window (bars)", Order = 0, GroupName = "02. Value Area")]
		[Range(10, 5000)]
		public int ProfileBars { get; set; }

		[Display(Name = "Profile Bin Size (points)", Order = 1, GroupName = "02. Value Area")]
		[Range(0.25, 100)]
		public double BinSizePoints { get; set; }

		[Display(Name = "Value Area %", Order = 2, GroupName = "02. Value Area")]
		[Range(10, 99)]
		public double ValueAreaPct { get; set; }

		[Display(Name = "Setup 2: Outside-Value-Area Window (bars)", Order = 3, GroupName = "02. Value Area")]
		[Range(1, 500)]
		public int SeenWindow { get; set; }

		[Display(Name = "Require Heikin Ashi Direction", Description = "Take signals only in the Heikin Ashi colour direction.", Order = 0, GroupName = "03. Filter")]
		public bool RequireHa { get; set; }

		#endregion

		#region Plot series (for automation tools)

		[Browsable(false)] [XmlIgnore] public Series<double> PullbackSignal { get { Update(); return Values[0]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> MidRetestSignal { get { Update(); return Values[1]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> ValueAreaSignal { get { Update(); return Values[2]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> HaDirection { get { Update(); return Values[3]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> BbMiddle { get { Update(); return Values[4]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> BbUpper { get { Update(); return Values[5]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> BbLower { get { Update(); return Values[6]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Poc { get { Update(); return Values[7]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Vah { get { Update(); return Values[8]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> AnySignal { get { Update(); return Values[10]; } }
		[Browsable(false)] [XmlIgnore] public Series<double> Val { get { Update(); return Values[9]; } }

		#endregion
	}
}
