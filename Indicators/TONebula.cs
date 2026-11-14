// TONebula — NinjaTrader 8 port of TraderOracle's "Nebula v3" (TradingView, build 29)
// Original: https://github.com/TraderOracle/TradingView  (Mozilla Public License 2.0)
// Original concept & Pine code: TraderOracle. Port keeps the original logic; see notes at the bottom.
//
// Calculation: always OnBarClose (every Nebula signal is bar-close confirmed in the original).
// Candles are recolored with NT's own bar brushes — no need to hide the chart's candles.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using DX  = SharpDX;
using D2D = SharpDX.Direct2D1;
using DW  = SharpDX.DirectWrite;
#endregion

#region Global enums
// Shows enum values in the property grid with readable names ("Pinky and the Brain", "Heikin Ashi Trend").
// Workspaces still save the plain enum name, so nothing breaks on reload.
public class TONebulaEnumConverter : EnumConverter
{
	public TONebulaEnumConverter(Type type) : base(type) { }

	private static string Label(Type t, string name)
	{
		System.Reflection.FieldInfo fi = t.GetField(name);
		if (fi != null)
		{
			object[] attrs = fi.GetCustomAttributes(typeof(DescriptionAttribute), false);
			if (attrs.Length > 0) return ((DescriptionAttribute)attrs[0]).Description;
		}
		var sb = new System.Text.StringBuilder();
		for (int i = 0; i < name.Length; i++)
		{
			if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
			sb.Append(name[i]);
		}
		return sb.ToString();
	}

	public override object ConvertTo(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object value, Type destinationType)
	{
		if (destinationType == typeof(string) && value != null && value.GetType() == EnumType)
			return Label(EnumType, value.ToString());
		return base.ConvertTo(context, culture, value, destinationType);
	}

	public override object ConvertFrom(ITypeDescriptorContext context, System.Globalization.CultureInfo culture, object value)
	{
		string s = value as string;
		if (s != null)
		{
			foreach (string name in Enum.GetNames(EnumType))
				if (name == s || Label(EnumType, name) == s) return Enum.Parse(EnumType, name);
		}
		return base.ConvertFrom(context, culture, value);
	}
}

[TypeConverter(typeof(TONebulaEnumConverter))]
public enum TONebulaCandleMode
{
	None, Vector, Waddah, Squeeze, VolumeDelta, TrendAgreement, TidalWave, SignalConfluence,
	[Description("RSI Heat")] RsiHeat, KernelSlope, RelativeVolume, [Description("ADX Strength")] AdxStrength, HeikinAshiTrend, StretchFromMean, Supertrend
}

[TypeConverter(typeof(TONebulaEnumConverter))]
public enum TONebulaTheme
{
	Standard, [Description("Pinky and the Brain")] PinkyAndTheBrain, ColorBlind, MellowYellow, Neon, Cyberpunk, Aurora, Ocean, Sunset, Nord,
	Dracula, TokyoNight, Royal, [Description("Ice and Fire")] IceAndFire, GreenLantern, Superman, WonderWoman, Hulk, Aquaman, [Description("RedTail")] RedTail
}

[TypeConverter(typeof(TONebulaEnumConverter))]
public enum TONebulaCloudType { None, Simple, RelativeStrength, MoneyFlow, CommodityChannel }

[TypeConverter(typeof(TONebulaEnumConverter))]
public enum TONebulaCloudPalette
{
	[Description("Theme (match Color theme)")] Theme,
	[Description("Aqua / Coral")] AquaCoral, [Description("Emerald / Crimson")] EmeraldCrimson, [Description("Sky / Orchid")] SkyOrchid,
	[Description("Lime / Tangerine")] LimeTangerine, [Description("Gold / Violet")] GoldViolet, [Description("Teal / Magenta")] TealMagenta,
	[Description("Mint / Rose")] MintRose, [Description("Cobalt / Amber")] CobaltAmber, [Description("Jade / Ruby")] JadeRuby,
	[Description("Turquoise / Tomato")] TurquoiseTomato, [Description("Periwinkle / Peach")] PeriwinklePeach, [Description("Ice / Lava")] IceLava,
	[Description("RedTail")] RedTail
}

[TypeConverter(typeof(TONebulaEnumConverter))]
public enum TONebulaDashPosition { TopRight, TopCenter, TopLeft, MiddleRight, MiddleLeft, BottomRight, BottomCenter, BottomLeft }

public enum TONebulaTextSize { Tiny, Small, Normal, Large }

public enum TONebulaLineStyle { Solid, Dotted, Dashed }
#endregion

namespace NinjaTrader.NinjaScript.Indicators.TraderOracle
{
	public class TONebula : Indicator
	{
		#region Constants (from the Pine source)
		private const int vSqueeze = 4, vTramp = 4, vBands = 2, vLuxRev = 3, vEarlyRev = 2, vDeadRev = 2, vShark = 2;
		private const int UP = 1, DN = 2;                 // waveState values (0 = na)

		private const int iColorSmooth = 3;
		private const double stFactor = 3.0;
		private const int stPeriod = 10;
		private const double adxSqueeze = 0;
		private const double iVolDeltaTop = 300;

		private const int Weighting = 10, MA_Length = 6, ADX_Length = 2;
		private const int adxlen = 14, dilen = 14;

		private const int watchSignalLookback = 35, rsiLength = 32, rsiBasisLength = 32, priceBasisLength = 20;
		private const double rsiMultiplier = 2.0, priceInnerMultiplier = 2.0, atrMult = 1.5;
		private const int atrPeriod = 30, maPeriod = 10, rsiMaLength = 24;

		private const int rlength = 12, DER_avg = 5, vlookbk = 20;
		private const int wlengthBB = 20;
		private const double wmultBB = 2.5;

		private const double kH = 21.0, kR = 8.0;
		private const int kX0 = 15;

		private const int alphaLength = 20, gammaLength = 20;

		private const double iBBThreshold = 0.0015, RSIThreshold = 25, RSIDown = 72;
		private const int rsiLengthInput = 14, lengthBB = 20;
		private const double multBB = 2.0;

		private const int sqLen = 20;
		private const double multKC = 1.5;
		#endregion

		#region Pine-style running calculators
		// ta.ema: seeded with an SMA of the first `len` valid values; an na input restarts the seed (as in Pine)
		private sealed class PEma
		{
			private readonly int len; private readonly double alpha;
			private readonly Queue<double> seed = new Queue<double>();
			public double Value = double.NaN;
			public PEma(int l, bool wilder = false) { len = Math.Max(1, l); alpha = wilder ? 1.0 / len : 2.0 / (len + 1); }
			public double Next(double x)
			{
				if (double.IsNaN(x) || double.IsInfinity(x)) { Value = double.NaN; seed.Clear(); return Value; }
				if (double.IsNaN(Value))
				{
					seed.Enqueue(x);
					if (seed.Count > len) seed.Dequeue();
					if (seed.Count == len) { double s = 0; foreach (double q in seed) s += q; Value = s / len; seed.Clear(); }
					return Value;
				}
				Value = alpha * x + (1 - alpha) * Value;
				return Value;
			}
		}

		private sealed class SessRange
		{
			public double hi = double.NaN, lo = double.NaN;
			public int x1 = -1, xEnd = -1;
			public bool forming, done, hiBrk, loBrk;
		}

		private enum MKind { Glyph, Badge, Box, Triangle }

		private sealed class Marker
		{
			public int Bar; public bool Below; public string Text; public Color Col; public Color TextCol;
			public float Size; public string Tip; public MKind Kind; public double Price = double.NaN;
		}

		private sealed class ImbLine { public int Bar; public double Price, Fill; public int Dir; public Color Col; }
		private sealed class DivLine { public int X1, X2; public double Y1, Y2; public Color Col; }
		private sealed class SqzZone { public int Start, End; }
		private sealed class HoverItem { public DX.RectangleF Rect; public string Tip; }
		#endregion

		#region Fields
		private readonly object sync = new object();

		// colors
		private Color colorBigGreen, colorBigRed, cloudUp, cloudDn, strongSellRed;
		private static readonly Color cRed_ = Hex(0xFF5252), cPurple = Hex(0x9C27B0), cYellow = Hex(0xFFEB3B),
			cOrange = Hex(0xFF9800), cGray = Hex(0x787B86), cBlue = Hex(0x2962FF), cFuchsia = Hex(0xE040FB);
		private static readonly Color violetVectorColor = Hex(0xE040FB);
		private static readonly Color blueVectorColor = Color.FromRgb(83, 144, 249);
		private Color regularCandleUpColor, regularCandleDownColor;

		// calculators
		private PEma e9, e21, cvdBuy, cvdSell, trRmaDi, pRma, mRma, adxRma, rsiUpM, rsiDnM, rsiUpU, rsiDnU,
			rsiUpD, rsiDnD, eFast, eSlow, eT1, eE1, atr30, atr10, eRsiHeat, eKSlope, eRelVol, eAdxS, eZ;

		// series
		private Series<double> sEma9, sEma21, sYhat, sRsiM, sRsiU, sRsiDiv, sMfiUp, sMfiDn, sVarMA, sMacd, sRange,
			sSqSrc, sVal, sVolSpread, sVolAvgL, sVola, sCplus, sCminus, sDemNz, sSupNz, sBc, sW1, sW2, sLL,
			sUpperBB, sLowerBB, sUpperAtr, sLowerAtr, sRsiBasis, sUpperRsi, sLowerRsi, sRsiMa, sDhT, sDlT,
			sUpClose, sDownClose, sUpperBBsq, sLowerBBsq, sFan, sMg, sCloudArgb, sMin50, sMax50;
		private Series<bool> sDeadRev, sLuxRev, sTramp, sSqz, sEarlyRev, sShark, sBands, sTrampDn, sTrampUp,
			sWeGoUp, sWeGoDown, sUpwards, sDownwards, sBrightGreen, sBrightRed, sPlotBuy, sPlotSell;

		// public outputs
		private Series<double> oSignal, oAdd, oTP, oWave, oConf, oCloud;

		// state
		private int bSC, sSC;
		private double upperTVal = double.NaN, lowerTVal = double.NaN, prevUpperTVal = double.NaN, prevLowerTVal = double.NaN;
		private double upCloseV = double.NaN, downCloseV = double.NaN;
		private double sPDIp, sMDIp, STRp, ADXip = double.NaN, VarMAp, mgP = double.NaN;
		private double plusFix = double.NaN, minusFix = double.NaN;
		private int cGreen, cRed; private bool prevPos, prevNeg;
		private readonly Queue<bool> buyWatch = new Queue<bool>(), sellWatch = new Queue<bool>();
		private double cFix = double.NaN, boFix = double.NaN, bcFix = double.NaN;
		private double[] kernelW; private double kernelWSum;
		private int lastPlBar = -1, lastPhBar = -1;
		private double prevPlRsi = double.NaN, prevPlLow = double.NaN, prevPhRsi = double.NaN, prevPhHigh = double.NaN;
		private int prevPlBar = -1, prevPhBar = -1;
		private int waveState;
		private double hemaP = double.NaN, bHP = double.NaN;
		private double haOpenP = double.NaN, haCloseP = double.NaN;
		private double stUpperP = double.NaN, stLowerP = double.NaN, stP = double.NaN;
		private bool prevSqzOn; private SqzZone curZone;

		// session state
		private TimeZoneInfo nyTz;
		private int orS, orE, lonS, lonE, asiaS, asiaE, rthS, rthE;
		private readonly SessRange rOR = new SessRange(), rLon = new SessRange(), rAsia = new SessRange();
		private bool pInOR, pInLon, pInAsia, pInRTH;
		private double ycVal = double.NaN; private int ycX = -1;
		private int orBrkDir, orBrkBar = -1, orBrkThisBar; private DateTime orBrkTime;
		private double orPostHi = double.NaN, orPostLo = double.NaN, orAvg = double.NaN;
		private readonly List<double> orSizes = new List<double>();

		// drawings
		private readonly Dictionary<int, List<Marker>> markers = new Dictionary<int, List<Marker>>();
		private readonly List<ImbLine> imbLines = new List<ImbLine>();
		private readonly List<DivLine> divLines = new List<DivLine>();
		private readonly List<SqzZone> sqzZones = new List<SqzZone>();

		// dashboard snapshot
		private bool dFanUp; private double dAdx = double.NaN, dRsi = double.NaN, dDiP, dDiM; private int dConf;
		private string lvlUpTxt = "—", lvlDnTxt = "—";

		// rendering
		private readonly Dictionary<uint, SolidColorBrush> wpfBrushes = new Dictionary<uint, SolidColorBrush>();
		private readonly Dictionary<uint, D2D.SolidColorBrush> dxBrushes = new Dictionary<uint, D2D.SolidColorBrush>();
		private readonly Dictionary<string, DW.TextFormat> fmts = new Dictionary<string, DW.TextFormat>();
		private D2D.StrokeStyle dotStyle, dashStyle;
		private readonly List<HoverItem> hoverItems = new List<HoverItem>();
		private string hoverTip; private DX.Vector2 hoverPt; private bool mouseHooked;
		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "TraderOracle's Nebula v3 — candle coloring, trend cloud, Tidal Wave signals, add-ons, take-profit confluence, RSI divergence, session levels and dashboard.";
				Name = "TONebula";
				Calculate = Calculate.OnBarClose;
				IsOverlay = true;
				DisplayInDataBox = true;
				DrawOnPricePanel = true;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = false;   // keep calculating when the chart tab is hidden (automation)
				ScaleJustification = ScaleJustification.Right;
				BarsRequiredToPlot = 0;

				CandleMode = TONebulaCandleMode.Waddah;
				ColorTheme = TONebulaTheme.Standard;
				CloudType = TONebulaCloudType.Simple;
				CloudPalette = TONebulaCloudPalette.Theme;
				ShowDashboard = true;
				ShowSqueezeZones = true;
				ShowDivergences = true;
				ShowSessionLevels = true;
				ShowHema = false;
				ShowPlus = false;
				ShowBigPlus = true;
				Show921 = false;
				ShowVolumeImbalances = false;
				DrawBehindBars = true;

				DashPosition = TONebulaDashPosition.TopRight;
				DashTextSize = TONebulaTextSize.Small;
				DashShowLevels = true;
				DashTransparency = 20;
				DashBarBlocks = 10;

				SqueezeZoneBrush = new SolidColorBrush(cBlue);
				SqueezeZoneTransparency = 85;
				SqueezeMinLength = 3;
				ShowSqueezeRelease = false;

				DrawDivLines = false;
				DivTransparency = 50;
				DivRsiLength = 14;
				PivotLeft = 5;
				PivotRight = 5;
				DivRangeMin = 5;
				DivRangeMax = 60;

				LevelOR = true;   SessionOR = "0930-1030";
				LevelLondon = true; SessionLondon = "0300-0800";
				LevelAsia = true; SessionAsia = "1800-0300";
				LevelYC = true;   SessionRTH = "0930-1600";
				LevelExtensions = true;
				LevelShadeForming = true;
				LevelMarkBreakout = true;
				LevelMergeTicks = 8;
				LevelTagOffset = 4;
				LevelTransparency = 45;

				ImbalanceExtend = 50;
				ImbalanceWidth = 3;
				ImbalanceStyle = TONebulaLineStyle.Dotted;

				TakePartialThreshold = 5;
				TakeAllThreshold = 7;
				UseQuadratic = false;
				SimpleCloudTransparency = 80;
				WeakTrendTransparency = 80;
				StrongTrendTransparency = 50;

				WaeSensitivity = 150;
				WaeFastLength = 20;
				WaeSlowLength = 40;
				WaeChannelLength = 20;
				WaeBBMult = 2.0;

				SqueezeTolerance = 2;
				AdxSqueezeThreshold = 21;
				ApplyRsi2575 = false;

				EnableAlerts = false;
				AlertSound = "Alert2.wav";

				AddPlot(new Stroke(new SolidColorBrush(Color.FromArgb(204, 0xE0, 0x40, 0xFB)), 1), PlotStyle.Line, "HEMA");
				// Automation plots (Predator X etc.): transparent, excluded from autoscale. +1 = bullish, -1 = bearish, 0 = nothing.
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SigBuySell");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SigStrong");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SigBasic");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SigUltimate");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SigAddLight");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SigAddStrong");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SigTakeProfitAll");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SigTakeProfitPartial");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SigDivergence");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SigEmaCross");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SigORBreak");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "SigSqueezeRelease");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "StateWave");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "StateCloud");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "StateConfluence");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "StateSqueeze");
			}
			else if (State == State.Configure)
			{
				Calculate = Calculate.OnBarClose;
			}
			else if (State == State.DataLoaded)
			{
				appliedTheme = null; appliedPalette = null;
				EnsureColors();
				InitState();

				sEma9 = S(); sEma21 = S(); sYhat = S(); sRsiM = S(); sRsiU = S(); sRsiDiv = S(); sMfiUp = S(); sMfiDn = S();
				sVarMA = S(); sMacd = S(); sRange = S(); sSqSrc = S(); sVal = S(); sVolSpread = S(); sVolAvgL = S();
				sVola = S(); sCplus = S(); sCminus = S(); sDemNz = S(); sSupNz = S(); sBc = S(); sW1 = S(); sW2 = S(); sLL = S();
				sUpperBB = S(); sLowerBB = S(); sUpperAtr = S(); sLowerAtr = S(); sRsiBasis = S(); sUpperRsi = S(); sLowerRsi = S();
				sRsiMa = S(); sDhT = S(); sDlT = S(); sUpClose = S(); sDownClose = S(); sMin50 = S(); sMax50 = S();
				sUpperBBsq = new Series<double>(this, MaximumBarsLookBack.Infinite);
				sLowerBBsq = new Series<double>(this, MaximumBarsLookBack.Infinite);
				sFan = new Series<double>(this, MaximumBarsLookBack.Infinite);
				sMg = new Series<double>(this, MaximumBarsLookBack.Infinite);
				sCloudArgb = new Series<double>(this, MaximumBarsLookBack.Infinite);

				sDeadRev = B(); sLuxRev = B(); sTramp = B(); sSqz = B(); sEarlyRev = B(); sShark = B(); sBands = B();
				sTrampDn = B(); sTrampUp = B(); sWeGoUp = B(); sWeGoDown = B(); sUpwards = B(); sDownwards = B();
				sBrightGreen = B(); sBrightRed = B(); sPlotBuy = B(); sPlotSell = B();

				oSignal = S(); oAdd = S(); oTP = S(); oWave = S(); oConf = S(); oCloud = S();

				try { nyTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); } catch { nyTz = TimeZoneInfo.Local; }
				ParseSession(SessionOR, out orS, out orE);
				ParseSession(SessionLondon, out lonS, out lonE);
				ParseSession(SessionAsia, out asiaS, out asiaE);
				ParseSession(SessionRTH, out rthS, out rthE);
			}
			else if (State == State.Historical)
			{
				if (DrawBehindBars) SetZOrder(-1);
				if (ChartControl != null && !mouseHooked)
				{
					ChartControl.Dispatcher.InvokeAsync(() =>
					{
						if (ChartPanel != null && !mouseHooked) { ChartPanel.MouseMove += OnPanelMouseMove; ChartPanel.MouseLeave += OnPanelMouseLeave; mouseHooked = true; }
					});
				}
			}
			else if (State == State.Terminated)
			{
				if (mouseHooked && ChartControl != null)
				{
					ChartControl.Dispatcher.InvokeAsync(() =>
					{
						if (ChartPanel != null) { ChartPanel.MouseMove -= OnPanelMouseMove; ChartPanel.MouseLeave -= OnPanelMouseLeave; }
					});
					mouseHooked = false;
				}
				DisposeDx();
				foreach (var f in fmts.Values) f.Dispose();
				fmts.Clear();
			}
		}

		private Series<double> S() { return new Series<double>(this); }
		private Series<bool> B() { return new Series<bool>(this); }

		private void InitState()
		{
			e9 = new PEma(9); e21 = new PEma(21); cvdBuy = new PEma(14); cvdSell = new PEma(14);
			trRmaDi = new PEma(dilen, true); pRma = new PEma(dilen, true); mRma = new PEma(dilen, true); adxRma = new PEma(adxlen, true);
			rsiUpM = new PEma(rsiLengthInput, true); rsiDnM = new PEma(rsiLengthInput, true);
			rsiUpU = new PEma(rsiLength, true); rsiDnU = new PEma(rsiLength, true);
			rsiUpD = new PEma(DivRsiLength, true); rsiDnD = new PEma(DivRsiLength, true);
			eFast = new PEma(WaeFastLength); eSlow = new PEma(WaeSlowLength); eT1 = new PEma(iColorSmooth); eE1 = new PEma(iColorSmooth);
			atr30 = new PEma(atrPeriod, true); atr10 = new PEma(stPeriod, true);
			eRsiHeat = new PEma(iColorSmooth); eKSlope = new PEma(iColorSmooth); eRelVol = new PEma(iColorSmooth);
			eAdxS = new PEma(iColorSmooth); eZ = new PEma(iColorSmooth);

			kernelW = new double[kX0 + 2]; kernelWSum = 0;
			for (int i = 0; i <= kX0 + 1; i++)
			{
				double w = Math.Pow(1 + (i * i) / (kH * kH * 2 * kR), -kR);
				kernelW[i] = w; kernelWSum += w;
			}

			bSC = sSC = 0; cGreen = cRed = 0; prevPos = prevNeg = false; waveState = 0;
			upperTVal = lowerTVal = prevUpperTVal = prevLowerTVal = upCloseV = downCloseV = double.NaN;
			buyWatch.Clear(); sellWatch.Clear();
			markers.Clear(); imbLines.Clear(); divLines.Clear(); sqzZones.Clear(); orSizes.Clear();
			curZone = null; prevSqzOn = false;
		}
		// Theme colors are resolved from the live property values, re-checked on every bar and every render,
		// so the palette always follows whatever ColorTheme / CloudPalette the running instance actually holds.
		private TONebulaTheme? appliedTheme;
		private TONebulaCloudPalette? appliedPalette;
		private bool EnsureColors()
		{
			if (appliedTheme.HasValue && appliedTheme.Value == ColorTheme && appliedPalette.HasValue && appliedPalette.Value == CloudPalette)
				return false;
			InitColors();
			appliedTheme = ColorTheme;
			appliedPalette = CloudPalette;
			return true;
		}

		private void InitColors()
		{
			switch (ColorTheme)
			{
				case TONebulaTheme.PinkyAndTheBrain: colorBigGreen = Hex(0x00f5f1); colorBigRed = Hex(0xfc03f8); break;
				case TONebulaTheme.ColorBlind:       colorBigGreen = Hex(0x03fcf4); colorBigRed = Hex(0xfca903); break;
				case TONebulaTheme.MellowYellow:     colorBigGreen = Hex(0x35defc); colorBigRed = Hex(0xfcf11c); break;
				case TONebulaTheme.Neon:             colorBigGreen = Hex(0x39ff14); colorBigRed = Hex(0xff073a); break;
				case TONebulaTheme.Cyberpunk:        colorBigGreen = Hex(0x00f0ff); colorBigRed = Hex(0xff2a6d); break;
				case TONebulaTheme.Aurora:           colorBigGreen = Hex(0x64ffda); colorBigRed = Hex(0xb388ff); break;
				case TONebulaTheme.Ocean:            colorBigGreen = Hex(0x4dd0e1); colorBigRed = Hex(0xff8a65); break;
				case TONebulaTheme.Sunset:           colorBigGreen = Hex(0xffc857); colorBigRed = Hex(0xe84855); break;
				case TONebulaTheme.Nord:             colorBigGreen = Hex(0xa3be8c); colorBigRed = Hex(0xbf616a); break;
				case TONebulaTheme.Dracula:          colorBigGreen = Hex(0x50fa7b); colorBigRed = Hex(0xff79c6); break;
				case TONebulaTheme.TokyoNight:       colorBigGreen = Hex(0x7aa2f7); colorBigRed = Hex(0xf7768e); break;
				case TONebulaTheme.Royal:            colorBigGreen = Hex(0xffd700); colorBigRed = Hex(0x9d4edd); break;
				case TONebulaTheme.IceAndFire:       colorBigGreen = Hex(0x00bfff); colorBigRed = Hex(0xff4500); break;
				case TONebulaTheme.GreenLantern:     colorBigGreen = Hex(0x00e04b); colorBigRed = Hex(0xffd600); break;
				case TONebulaTheme.Superman:         colorBigGreen = Hex(0x1e88e5); colorBigRed = Hex(0xe53935); break;
				case TONebulaTheme.WonderWoman:      colorBigGreen = Hex(0xffc400); colorBigRed = Hex(0xd50000); break;
				case TONebulaTheme.Hulk:             colorBigGreen = Hex(0x76ff03); colorBigRed = Hex(0xaa00ff); break;
				case TONebulaTheme.Aquaman:          colorBigGreen = Hex(0x1de9b6); colorBigRed = Hex(0xff9100); break;
				case TONebulaTheme.RedTail:          colorBigGreen = Hex(0x4682b4); colorBigRed = Hex(0x555555); break;
				default:                             colorBigGreen = Hex(0x00ff00); colorBigRed = Hex(0xff0000); break;
			}
			switch (CloudPalette)
			{
				case TONebulaCloudPalette.AquaCoral:       cloudUp = Hex(0x00d4c8); cloudDn = Hex(0xff6f61); break;
				case TONebulaCloudPalette.EmeraldCrimson:  cloudUp = Hex(0x2ecc71); cloudDn = Hex(0xe74c3c); break;
				case TONebulaCloudPalette.SkyOrchid:       cloudUp = Hex(0x4fc3f7); cloudDn = Hex(0xba68c8); break;
				case TONebulaCloudPalette.LimeTangerine:   cloudUp = Hex(0xc6ff00); cloudDn = Hex(0xff9100); break;
				case TONebulaCloudPalette.GoldViolet:      cloudUp = Hex(0xffd54f); cloudDn = Hex(0x7c4dff); break;
				case TONebulaCloudPalette.TealMagenta:     cloudUp = Hex(0x1de9b6); cloudDn = Hex(0xf50057); break;
				case TONebulaCloudPalette.MintRose:        cloudUp = Hex(0x69f0ae); cloudDn = Hex(0xff80ab); break;
				case TONebulaCloudPalette.CobaltAmber:     cloudUp = Hex(0x2979ff); cloudDn = Hex(0xffab00); break;
				case TONebulaCloudPalette.JadeRuby:        cloudUp = Hex(0x00a86b); cloudDn = Hex(0xe0115f); break;
				case TONebulaCloudPalette.TurquoiseTomato: cloudUp = Hex(0x40e0d0); cloudDn = Hex(0xff6347); break;
				case TONebulaCloudPalette.PeriwinklePeach: cloudUp = Hex(0x8c9eff); cloudDn = Hex(0xffab91); break;
				case TONebulaCloudPalette.IceLava:         cloudUp = Hex(0x80deea); cloudDn = Hex(0xff3d00); break;
				case TONebulaCloudPalette.RedTail:         cloudUp = Hex(0x4682b4); cloudDn = Hex(0x555555); break;
				default:                                   cloudUp = colorBigGreen; cloudDn = colorBigRed; break;
			}
			strongSellRed = Color.FromRgb((byte)(colorBigRed.R * 0.6), (byte)(colorBigRed.G * 0.6), (byte)(colorBigRed.B * 0.6));
			regularCandleUpColor = T(Hex(0x02a433), 99);
			regularCandleDownColor = T(Hex(0xa10101), 99);
		}

		protected override void OnBarUpdate()
		{
			lock (sync)
			{
				EnsureColors();
				if (CurrentBar == 0)
					Print("TONebula [" + Instrument.FullName + " " + TfLabel() + "]: theme=" + ColorTheme + ", cloud colors=" + CloudPalette + ", bull=" + colorBigGreen + ", bear=" + colorBigRed + ", candle mode=" + CandleMode);
				Compute();
			}
		}

		private void Compute()
		{
			int cb = CurrentBar;
			double o = Open[0], h = High[0], l = Low[0], c = Close[0], v = Volume[0];
			bool isRed = c < o, isGreen = c > o;
			double c1 = cb > 0 ? Close[1] : double.NaN;
			double chg = cb > 0 ? c - c1 : double.NaN;

			// ---------- EMAs ----------
			sEma9[0] = e9.Next(c);
			sEma21[0] = e21.Next(c);

			// ---------- ADX (dirmov) ----------
			double dmUp = cb > 0 ? h - High[1] : double.NaN;
			double dmDown = cb > 0 ? -(l - Low[1]) : double.NaN;
			double plusDM = double.IsNaN(dmUp) ? double.NaN : (dmUp > dmDown && dmUp > 0 ? dmUp : 0);
			double minusDM = double.IsNaN(dmDown) ? double.NaN : (dmDown > dmUp && dmDown > 0 ? dmDown : 0);
			double trDi = cb > 0 ? Math.Max(h - l, Math.Max(Math.Abs(h - c1), Math.Abs(l - c1))) : double.NaN;
			double trr = trRmaDi.Next(trDi);
			double pr = pRma.Next(plusDM), mr = mRma.Next(minusDM);
			double pl = 100 * pr / trr, mn = 100 * mr / trr;
			if (!double.IsNaN(pl) && !double.IsInfinity(pl)) plusFix = pl;
			if (!double.IsNaN(mn) && !double.IsInfinity(mn)) minusFix = mn;
			double diPlus = plusFix, diMinus = minusFix;
			double diSum = diPlus + diMinus;
			double adxVal = 100 * adxRma.Next(Math.Abs(diPlus - diMinus) / (diSum == 0 ? 1 : diSum));
			bool sigabove19 = adxVal > adxSqueeze;

			// ---------- estimated volume delta ----------
			double upperWick = c > o ? h - c : h - o;
			double lowerWick = c > o ? o - l : c - l;
			double spread = h - l;
			double pUW = spread > 0 ? upperWick / spread : 0, pLW = spread > 0 ? lowerWick / spread : 0;
			double pBody = spread > 0 ? (spread - (upperWick + lowerWick)) / spread : 0;
			double wickShare = (pUW + pLW) / 2;
			double buyingVol = c > o ? (pBody + wickShare) * v : wickShare * v;
			double sellingVol = c < o ? (pBody + wickShare) * v : wickShare * v;
			double cvd = cvdBuy.Next(buyingVol) - cvdSell.Next(sellingVol);
			Color cCVD = cvd > 0 ? Grad(Math.Abs(cvd), 0, iVolDeltaTop, T(colorBigGreen, 70), colorBigGreen)
				: cvd < 0 ? Grad(Math.Abs(cvd), 0, iVolDeltaTop, T(colorBigRed, 70), colorBigRed) : Colors.White;

			// ---------- engulfing reversal at 50-bar extreme (deadRev) ----------
			sMin50[0] = Lowest(Low, 50);
			sMax50[0] = Highest(High, 50);
			double ll3 = Lowest(Low, 3), hh3 = Highest(High, 3);
			bool buyDSR = false, sellDSR = false;
			if (cb >= 3)
			{
				buyDSR = Close[1] < Open[1] && isGreen && c > Open[1] && (ll3 < sMin50[1] || ll3 < sMin50[2] || ll3 < sMin50[3]);
				sellDSR = Close[1] > Open[1] && isRed && c < Open[1] && (hh3 > sMax50[1] || hh3 > sMax50[2] || hh3 > sMax50[3]);
			}
			bool deadRev = buyDSR || sellDSR;
			sDeadRev[0] = deadRev;

			// ---------- 9-count exhaustion (luxRev) ----------
			int pbSC = bSC, psSC = sSC;
			if (cb >= 4 && c < Close[4]) { bSC = bSC == 9 ? 1 : bSC + 1; sSC = 0; }
			else { sSC = sSC == 9 ? 1 : sSC + 1; bSC = 0; }
			bool luxRev = bSC == 9 || (pbSC == 8 && sSC == 1) || sSC == 9 || (psSC == 8 && bSC == 1);
			sLuxRev[0] = luxRev;

			// ---------- fractals (for early reversal) ----------
			sDhT[0] = cb >= 2 ? Math.Sign(h - High[1]) + Math.Sign(High[1] - High[2]) : double.NaN;
			sDlT[0] = cb >= 2 ? Math.Sign(l - Low[1]) + Math.Sign(Low[1] - Low[2]) : double.NaN;
			bool bullf = cb >= 4 && sDhT[0] == -2 && sDhT[2] == 2 && High[2] == Highest(High, 5);
			bool bearf = cb >= 4 && sDlT[0] == 2 && sDlT[2] == -2 && Low[2] == Lowest(Low, 5);
			if (bullf) upperTVal = High[2];
			if (cb > 0 && c > upperTVal && c1 <= prevUpperTVal) upCloseV = c;
			if (bearf) lowerTVal = Low[2];
			if (cb > 0 && c < lowerTVal && c1 >= prevLowerTVal) downCloseV = c;
			prevUpperTVal = upperTVal; prevLowerTVal = lowerTVal;
			sUpClose[0] = upCloseV; sDownClose[0] = downCloseV;

			// ---------- RSI(14) / MFI / CCI ----------
			double upM = rsiUpM.Next(double.IsNaN(chg) ? double.NaN : Math.Max(chg, 0));
			double downM = rsiDnM.Next(double.IsNaN(chg) ? double.NaN : -Math.Min(chg, 0));
			double rsiM = RsiFrom(upM, downM);
			sRsiM[0] = rsiM;

			double tp = Typical[0];
			double tpChg = cb > 0 ? tp - Typical[1] : double.NaN;
			sMfiUp[0] = v * (tpChg <= 0 ? 0 : tp);
			sMfiDn[0] = v * (tpChg >= 0 ? 0 : tp);
			double mfUp = Sum(sMfiUp, 14), mfDn = Sum(sMfiDn, 14);
			double mfi = 100.0 - (100.0 / (1.0 + mfUp / mfDn));
			double tpSma = Sma(Typical, 20), tpDev = MeanDev(Typical, 20);
			double cci = (tp - tpSma) / (0.015 * tpDev);

			bool bCCI = CloudType == TONebulaCloudType.CommodityChannel;
			bool bOsc = CloudType == TONebulaCloudType.RelativeStrength || CloudType == TONebulaCloudType.MoneyFlow || bCCI;
			double iSource = CloudType == TONebulaCloudType.RelativeStrength ? rsiM : CloudType == TONebulaCloudType.MoneyFlow ? mfi : bCCI ? cci : 0.0;
			double MIN_CLOUD = bOsc ? 20 : 0;
			double MID_CLOUD = bCCI ? -100 : bOsc ? 50 : 0;
			double MAX_CLOUD = bCCI ? 100 : bOsc ? 80 : 0;

			// ---------- Fantail VMA ----------
			double Bulls1 = cb > 0 ? 0.5 * (Math.Abs(h - High[1]) + (h - High[1])) : double.NaN;
			double Bears1 = cb > 0 ? 0.5 * (Math.Abs(Low[1] - l) + (Low[1] - l)) : double.NaN;
			double Bears = Bulls1 >= Bears1 ? 0 : Bears1;
			double Bulls = Bulls1 <= Bears1 ? 0 : Bulls1;
			double TRf = cb > 0 ? Math.Max(h - l, h - c1) : h - l;
			double sPDI = 0, sMDI = 0, STR = h - l;
			if (cb > 0)
			{
				sPDI = (Weighting * sPDIp + Bulls) / (Weighting + 1);
				sMDI = (Weighting * sMDIp + Bears) / (Weighting + 1);
				STR = (Weighting * STRp + TRf) / (Weighting + 1);
			}
			double PDI = STR > 0 ? sPDI / STR : 0;
			double MDI = STR > 0 ? sMDI / STR : 0;
			double DX_ = (PDI + MDI) > 0 ? Math.Abs(PDI - MDI) / (PDI + MDI) : 0;
			double ADXi = 0;
			if (cb > 0) ADXi = (Weighting * ADXip + DX_) / (Weighting + 1);
			double ADXmin = cb > 0 ? Math.Min(ADXi, ADXip) : double.NaN;   // ta.lowest(ADX, 2)
			double ADXmax = cb > 0 ? Math.Max(ADXi, ADXip) : double.NaN;
			double Diff = ADXmax - ADXmin;
			double Const = Diff > 0 ? (ADXi - ADXmin) / Diff : 0;
			double VarMA = cb > 0 ? ((2 - Const) * VarMAp + Const * c) / 2 : c;
			sVarMA[0] = VarMA;
			double FanVMA = Sma(sVarMA, MA_Length);
			sPDIp = sPDI; sMDIp = sMDI; STRp = STR; ADXip = ADXi; VarMAp = VarMA;

			// ---------- McGinley Dynamic ----------
			double mg;
			if (double.IsNaN(mgP)) mg = cb >= 13 ? Sma(Close, 14) : double.NaN;
			else mg = mgP + (c - mgP) / (14 * Math.Pow(c / mgP, 4));
			mgP = mg;
			sFan[0] = FanVMA; sMg[0] = mg;

			// ---------- WAE ----------
			double basisWAE = Sma(Close, WaeChannelLength);
			double devWAE = WaeBBMult * Stdev(Close, WaeChannelLength);
			double e1 = (basisWAE + devWAE) - (basisWAE - devWAE);
			double macd = eFast.Next(c) - eSlow.Next(c);
			sMacd[0] = macd;
			double t1 = cb > 0 ? (macd - sMacd[1]) * WaeSensitivity : double.NaN;
			double t1s = eT1.Next(t1), e1s = eE1.Next(e1);
			double waeRatio = e1s > 0 ? t1s / e1s : 0.0;
			if (double.IsNaN(waeRatio)) waeRatio = 0;
			double waeStrength = Math.Abs(waeRatio);
			Color waeBase = waeRatio >= 0 ? colorBigGreen : colorBigRed;
			Color cBodyColor = Grad(waeStrength, 0, 2, T(waeBase, 90), waeBase);
			Color cWickColor = Grad(waeStrength, 0, 1, T(waeBase, 50), waeBase);

			// ---------- Bollinger (20, 2) / trampoline ----------
			double basisBB = Sma(Close, lengthBB);
			double sd20 = Stdev(Close, lengthBB);
			double devBB = multBB * sd20;
			double upperBB = basisBB + devBB, lowerBB = basisBB - devBB;
			double bbw = (upperBB - lowerBB) / basisBB;
			sUpperBB[0] = upperBB; sLowerBB[0] = lowerBB;

			bool trampDn = isRed && rsiM <= RSIThreshold && c < lowerBB && bbw > iBBThreshold;
			bool trampUp = isGreen && rsiM >= RSIDown && c > upperBB && bbw > iBBThreshold;
			sTrampDn[0] = trampDn; sTrampUp[0] = trampUp;
			bool weGoUp = cb >= 5 && isGreen && (sTrampDn[1] || sTrampDn[2] || sTrampDn[3] || sTrampDn[4] || sTrampDn[5]) && h > High[1];
			bool weGoDown = cb >= 5 && isRed && (sTrampUp[1] || sTrampUp[2] || sTrampUp[3] || sTrampUp[4] || sTrampUp[5]) && l < Low[1];
			sWeGoUp[0] = weGoUp; sWeGoDown[0] = weGoDown;
			bool upThrust = weGoUp && cb >= 4 && !sWeGoUp[1] && !sWeGoUp[2] && !sWeGoUp[3] && !sWeGoUp[4];
			bool downThrust = weGoDown && cb >= 4 && !sWeGoDown[1] && !sWeGoDown[2] && !sWeGoDown[3] && !sWeGoDown[4];
			bool tramp = upThrust || downThrust;
			sTramp[0] = tramp;

			// ---------- TTM squeeze ----------
			double sqDev = multKC * sd20;
			double upperBBsq = basisBB + sqDev, lowerBBsq = basisBB - sqDev;
			sRange[0] = h - l;
			double rangema = Sma(sRange, sqLen);
			double upperKC = basisBB + rangema * multKC, lowerKC = basisBB - rangema * multKC;
			bool sqzOn = lowerBBsq > lowerKC && upperBBsq < upperKC;
			sUpperBBsq[0] = upperBBsq; sLowerBBsq[0] = lowerBBsq;

			double avg1 = (Highest(High, sqLen) + Lowest(Low, sqLen)) / 2;
			double avg2 = (avg1 + basisBB) / 2;
			sSqSrc[0] = c - avg2;
			double val = LinReg(sSqSrc, sqLen);
			sVal[0] = val;
			double valPrev = cb > 0 && !double.IsNaN(sVal[1]) ? sVal[1] : 0;

			bool pos = false, neg = false;
			if (val < valPrev && val < 5 && !sqzOn) cRed++;
			if (val > valPrev && val > 5 && !sqzOn) cGreen++;
			if (val > valPrev && cRed > SqueezeTolerance && val < 5 && !prevPos && sigabove19) { cRed = 0; pos = true; }
			if (val < valPrev && cGreen > SqueezeTolerance && val > 5 && !prevNeg && sigabove19) { cGreen = 0; neg = true; }
			prevPos = pos; prevNeg = neg;
			bool sqz = pos || neg;
			sSqz[0] = sqz;

			Color cSQColor = Colors.White, cSQBorder = Colors.White;
			if (val > 0)
			{
				if (val > valPrev) { cSQColor = Grad(Math.Abs(val), 0, 30, T(colorBigGreen, 50), colorBigGreen); cSQBorder = colorBigGreen; }
				else if (val < valPrev) { cSQColor = T(colorBigGreen, 70); cSQBorder = Colors.Transparent; }
			}
			else
			{
				if (val < valPrev) { cSQColor = Grad(Math.Abs(val), 0, 30, T(colorBigRed, 50), colorBigRed); cSQBorder = colorBigRed; }
				else if (val > valPrev) { cSQColor = T(colorBigRed, 50); cSQBorder = Colors.Transparent; }
			}

			// ---------- PVSRA vector candles ----------
			double averageVolume = Sma(Volume, 10);
			sVolSpread[0] = v * (h - l);
			double highestVolumeSpread = Highest(sVolSpread, 10);
			bool climax = v >= averageVolume * 2 || sVolSpread[0] >= highestVolumeSpread;
			bool highVol = v >= averageVolume * 1.5;
			bool pvBull = c > o;
			Color pvsraColor = climax ? (pvBull ? colorBigGreen : colorBigRed)
				: highVol ? (pvBull ? blueVectorColor : violetVectorColor)
				: (pvBull ? regularCandleUpColor : regularCandleDownColor);
			bool pvGreen = climax && pvBull, pvRed = climax && !pvBull;

			bool bGreenSignal = pvGreen && cb >= 3 && (c == sUpClose[0] || Close[1] == sUpClose[1] || Close[2] == sUpClose[2] || Close[3] == sUpClose[3]);
			bool bRedSignal = pvRed && cb >= 3 && (c == sDownClose[0] || Close[1] == sDownClose[1] || Close[2] == sDownClose[2] || Close[3] == sDownClose[3]);
			bool earlyRev = bGreenSignal || bRedSignal;
			sEarlyRev[0] = earlyRev;

			// ---------- Shark ----------
			double basis5 = Sma(sRsiM, 30), dev5 = 2.0 * Stdev(sRsiM, 30);
			bool bBelow25 = !ApplyRsi2575 || rsiM < 26;
			bool bAbove75 = !ApplyRsi2575 || rsiM > 74;
			bool shark = (rsiM < basis5 - dev5 && bBelow25) || (rsiM > basis5 + dev5 && bAbove75);
			sShark[0] = shark;

			// ---------- Ultimate Buy / Sell ----------
			double trAtr = cb > 0 ? Math.Max(h - l, Math.Max(Math.Abs(h - c1), Math.Abs(l - c1))) : h - l;
			double atrValue = atr30.Next(trAtr);
			double atrMaValue = Wma(Close, maPeriod);
			sUpperAtr[0] = atrMaValue + atrValue * atrMult;
			sLowerAtr[0] = atrMaValue - atrValue * atrMult;

			double upU = rsiUpU.Next(double.IsNaN(chg) ? double.NaN : Math.Max(chg, 0));
			double downU = rsiDnU.Next(double.IsNaN(chg) ? double.NaN : -Math.Min(chg, 0));
			sRsiU[0] = RsiFrom(upU, downU);
			sRsiBasis[0] = Wma(sRsiU, rsiBasisLength);
			double rsiDeviation = Stdev(sRsiU, rsiBasisLength);
			sUpperRsi[0] = sRsiBasis[0] + rsiMultiplier * rsiDeviation;
			sLowerRsi[0] = sRsiBasis[0] - rsiMultiplier * rsiDeviation;
			sRsiMa[0] = Wma(sRsiU, rsiMaLength);

			bool priceCrossOverInner = XOver(Close, sLowerBB);
			bool priceCrossUnderInner = XUnder(Close, sUpperBB);
			bool rsiCrossOverLower = XOver(sRsiU, sLowerRsi);
			bool rsiCrossUnderUpper = XUnder(sRsiU, sUpperRsi);
			bool rsiCrossOverBasis = XOver(sRsiU, sRsiBasis);
			bool rsiCrossUnderBasis = XUnder(sRsiU, sRsiBasis);
			bool rsiCrossOverMa = XOver(sRsiU, sRsiMa);
			bool rsiCrossUnderMa = XUnder(sRsiU, sRsiMa);
			bool rsiCrossUnder75 = XUnderK(sRsiU, 75);
			bool rsiCrossOver25 = XOverK(sRsiU, 25);
			bool highUnderAtrLower = XUnder(High, sLowerAtr);
			bool lowOverAtrUpper = XOver(Low, sUpperAtr);

			bool buyWatched = priceCrossOverInner || rsiCrossOverLower || rsiCrossOver25 || highUnderAtrLower;
			bool sellWatched = priceCrossUnderInner || rsiCrossUnderUpper || rsiCrossUnder75 || lowOverAtrUpper;
			buyWatch.Enqueue(buyWatched); sellWatch.Enqueue(sellWatched);
			while (buyWatch.Count > watchSignalLookback) buyWatch.Dequeue();
			while (sellWatch.Count > watchSignalLookback) sellWatch.Dequeue();
			bool buySignals = buyWatch.Contains(true) && (rsiCrossOverBasis || rsiCrossOver25 || rsiCrossOverMa);
			bool sellSignals = sellWatch.Contains(true) && (rsiCrossUnderBasis || rsiCrossUnder75 || rsiCrossUnderMa);
			bool plotBuy = buySignals && !buyWatched;
			bool plotSell = !plotBuy && sellSignals && !sellWatched;
			if (plotBuy || plotSell) { buyWatch.Clear(); sellWatch.Clear(); }
			sPlotBuy[0] = plotBuy; sPlotSell[0] = plotSell;

			// ---------- Vodka Shot ----------
			double volAvgL = Sma(Volume, 70);
			sVolAvgL[0] = volAvgL;
			double volDev = (volAvgL + 1.618034 * Stdev(sVolAvgL, 70)) / volAvgL * 11 / 100;
			double volRel = v / volAvgL;
			bool volSpike = volRel * .145898 > volDev;

			double hv = Highest(Volume, vlookbk), lv = Lowest(Volume, vlookbk);
			double vola = (hv - lv) > 0 ? (v - lv) / (hv - lv) : double.NaN;
			sVola[0] = vola;
			double R = (Highest(High, 2) - Lowest(Low, 2)) / 2;
			double rsr = R > 0 && !double.IsNaN(chg) ? Math.Max(Math.Min(chg / R, 1), -1) : double.NaN;
			double cRaw = rsr * vola;
			if (!double.IsNaN(cRaw)) cFix = cRaw;
			sCplus[0] = double.IsNaN(cFix) ? double.NaN : Math.Max(cFix, 0);
			sCminus[0] = double.IsNaN(cFix) ? double.NaN : -Math.Min(cFix, 0);
			double avgVola = Wma(sVola, rlength);
			double dem = avgVola > 0 ? Wma(sCplus, rlength) / avgVola : double.NaN;
			double sup = avgVola > 0 ? Wma(sCminus, rlength) / avgVola : double.NaN;
			sSupNz[0] = double.IsNaN(sup) ? 0 : sup;
			sDemNz[0] = double.IsNaN(dem) ? 0 : dem;
			double boRaw = 100 * Wma(sSupNz, DER_avg), bcRaw = 100 * Wma(sDemNz, DER_avg);
			if (!double.IsNaN(boRaw)) boFix = boRaw;
			if (!double.IsNaN(bcRaw)) bcFix = bcRaw;
			double bo = boFix, bc = bcFix;
			sBc[0] = bc;
			bool rising = cb > 0 && bc - sBc[1] > 0;
			bool vaderUp = bc > bo && rising;
			bool vaderDn = bc < bo && !rising;

			sW1[0] = Wma(Close, 7); sW2[0] = Wma(sW1, 7); sLL[0] = Wma(sW2, 7);
			double LL = sLL[0], LLp = cb > 0 ? sLL[1] : double.NaN;
			bool upUTruthy = !double.IsNaN(upU) && upU != 0;
			bool upwards = LL > LLp && vaderUp && upUTruthy && isGreen && volSpike;
			bool downwards = LL < LLp && vaderDn && isRed && volSpike;
			sUpwards[0] = upwards; sDownwards[0] = downwards;
			bool pBuyVodka = upwards && cb >= 4 && !sUpwards[1] && !sUpwards[2] && !sUpwards[3] && !sUpwards[4];
			// NOTE: faithful to the original, which checks upwards[2..4] (not downwards) for the sell side
			bool pSellVodka = downwards && cb >= 4 && !sDownwards[1] && !sUpwards[2] && !sUpwards[3] && !sUpwards[4];

			// ---------- Bollinger wick rejection (2.5 SD) ----------
			double wdev = wmultBB * Stdev(Close, wlengthBB);
			double wupper = basisBB + wdev, wlower = basisBB - wdev;
			bool bands = (l <= wlower && c >= wlower && isRed) || (h >= wupper && c < wupper && isGreen);
			sBands[0] = bands;

			// ---------- Rational quadratic kernel ----------
			double yhat1 = double.NaN;
			if (cb >= kX0 + 1)
			{
				double ks = 0;
				for (int i = 0; i <= kX0 + 1; i++) ks += Close[i] * kernelW[i];
				yhat1 = ks / kernelWSum;
			}
			sYhat[0] = yhat1;

			// ---------- RSI divergence ----------
			double upD = rsiUpD.Next(double.IsNaN(chg) ? double.NaN : Math.Max(chg, 0));
			double dnD = rsiDnD.Next(double.IsNaN(chg) ? double.NaN : -Math.Min(chg, 0));
			sRsiDiv[0] = RsiFrom(upD, dnD);
			bool plFound = PivotLow(sRsiDiv, PivotLeft, PivotRight);
			bool phFound = PivotHigh(sRsiDiv, PivotLeft, PivotRight);
			bool plInRange = lastPlBar >= 0 && InRange(cb - 1 - lastPlBar);
			bool phInRange = lastPhBar >= 0 && InRange(cb - 1 - lastPhBar);
			double rsiPiv = cb >= PivotRight ? sRsiDiv[PivotRight] : double.NaN;
			double lowPiv = cb >= PivotRight ? Low[PivotRight] : double.NaN;
			double highPiv = cb >= PivotRight ? High[PivotRight] : double.NaN;
			int pivBar = cb - PivotRight;
			bool rawBullDiv = plFound && plInRange && rsiPiv > prevPlRsi && lowPiv < prevPlLow;
			bool rawBearDiv = phFound && phInRange && rsiPiv < prevPhRsi && highPiv > prevPhHigh;
			bool bullDiv = ShowDivergences && plFound && plInRange && rsiPiv > prevPlRsi && lowPiv < prevPlLow;
			bool bearDiv = ShowDivergences && phFound && phInRange && rsiPiv < prevPhRsi && highPiv > prevPhHigh;
			double divPrevPlRsi = prevPlRsi, divPrevPhRsi = prevPhRsi;
			Color divBull = T(colorBigGreen, DivTransparency), divBear = T(colorBigRed, DivTransparency);
			if (DrawDivLines)
			{
				if (bullDiv) divLines.Add(new DivLine { X1 = prevPlBar, Y1 = prevPlLow, X2 = pivBar, Y2 = lowPiv, Col = divBull });
				if (bearDiv) divLines.Add(new DivLine { X1 = prevPhBar, Y1 = prevPhHigh, X2 = pivBar, Y2 = highPiv, Col = divBear });
			}
			if (plFound) { prevPlRsi = rsiPiv; prevPlLow = lowPiv; prevPlBar = pivBar; lastPlBar = cb; }
			if (phFound) { prevPhRsi = rsiPiv; prevPhHigh = highPiv; prevPhBar = pivBar; lastPhBar = cb; }

			// ---------- take-profit confluence ----------
			int iTP = 0;
			if (cb >= 1)
			{
				iTP = (deadRev || sDeadRev[1] ? vDeadRev : 0) + (luxRev || sLuxRev[1] ? vLuxRev : 0) + (tramp || sTramp[1] ? vTramp : 0)
					+ (sqz || sSqz[1] ? vSqueeze : 0) + (earlyRev || sEarlyRev[1] ? vEarlyRev : 0) + (shark || sShark[1] ? vShark : 0)
					+ (bands || sBands[1] ? vBands : 0);
			}

			// ---------- Tidal Wave ----------
			for (int i = imbLines.Count - 1; i >= 0; i--)
			{
				ImbLine il = imbLines[i];
				bool filled = il.Dir == 1 ? l <= il.Fill : h >= il.Fill;
				if (filled) imbLines.RemoveAt(i);
			}

			bool noOverlapRed = false, noOverlapGreen = false, brightGreen = false, brightRed = false, gapGreen = false, gapRed = false;
			int wsPrev = waveState;
			int maxLb = Math.Min(200, cb);
			if (isGreen)
			{
				for (int i = 1; i <= maxLb; i++)
				{
					if (sBrightRed[i]) break;
					else if (waveState == UP && Close[i] < Open[i]) break;
					else if (waveState == DN && o >= Close[i] && Close[i] > Open[i])
					{ noOverlapGreen = true; brightGreen = true; waveState = UP; break; }
				}
				if (cb >= 1 && o >= c1 && Close[1] > Open[1])
				{
					if (ShowVolumeImbalances && o > c1) AddImbalance(cb, o, c1, 1, T(colorBigGreen, 40));
					waveState = UP; brightGreen = true; gapGreen = true;
				}
			}
			if (isRed)
			{
				for (int i = 1; i <= maxLb; i++)
				{
					if (sBrightGreen[i]) break;
					else if (waveState == DN && Close[i] > Open[i]) break;
					else if (waveState == UP && o <= Close[i] && Close[i] < Open[i])
					{ noOverlapRed = true; brightRed = true; waveState = DN; break; }
				}
				if (cb >= 1 && Close[1] < Open[1] && o < c1)
				{
					if (ShowVolumeImbalances) AddImbalance(cb, o, c1, -1, T(colorBigRed, 40));
					waveState = DN; brightRed = true; gapRed = true;
				}
			}
			sBrightGreen[0] = brightGreen; sBrightRed[0] = brightRed;

			bool bBigBuy1 = plotBuy || (cb >= 3 && (sPlotBuy[1] || sPlotBuy[2] || sPlotBuy[3]));
			bool bBigSell1 = plotSell || (cb >= 3 && (sPlotSell[1] || sPlotSell[2] || sPlotSell[3]));

			// ---------- HEMA ----------
			double alpha = 2.0 / (alphaLength + 1), gamma = 2.0 / (gammaLength + 1);
			double hemaPrevNz = double.IsNaN(hemaP) ? 0 : hemaP;
			double hema = (1 - alpha) * (hemaPrevNz + (double.IsNaN(bHP) ? c : bHP)) + alpha * c;
			double bH = (1 - gamma) * (double.IsNaN(bHP) ? 0 : bHP) + gamma * (hema - hemaPrevNz);
			hemaP = hema; bHP = bH;
			if (ShowHema) Values[0][0] = hema; else Values[0].Reset();

			// ---------- 9/21 cross ----------
			Series<double> crossSrc = UseQuadratic ? sYhat : sEma21;
			bool rawCrossUp = XOver(sEma9, crossSrc), rawCrossDown = XUnder(sEma9, crossSrc);
			bool bSkullUp = Show921 && rawCrossUp;
			bool bSkullDown = Show921 && rawCrossDown;

			// ---------- Cloud ----------
			bool fanUp = FanVMA > mg;
			Color cColor;
			if (CloudType == TONebulaCloudType.Simple) cColor = T(fanUp ? cloudUp : cloudDn, SimpleCloudTransparency);
			else if (fanUp) cColor = Grad(iSource, MID_CLOUD, MAX_CLOUD, T(cloudUp, WeakTrendTransparency), T(cloudUp, StrongTrendTransparency));
			else cColor = Grad(iSource, MIN_CLOUD, MID_CLOUD, T(cloudDn, WeakTrendTransparency), T(cloudDn, StrongTrendTransparency));
			sCloudArgb[0] = Argb(cColor);

			// ---------- signals ----------
			bool waveBuy = noOverlapGreen && wsPrev == DN;
			bool waveSell = noOverlapRed && wsPrev == UP;
			bool buyBasic = !bBigBuy1 && waveBuy;
			bool sellBasic = !bBigSell1 && waveSell;
			bool buySuper = bBigBuy1 && waveBuy;
			bool sellSuper = bBigSell1 && waveSell;
			bool plusBuy = gapGreen && wsPrev == UP;
			bool plusSell = gapRed && wsPrev == DN;
			bool vodkaBuy = pBuyVodka && wsPrev == UP;
			bool vodkaSell = pSellVodka && wsPrev == DN;
			bool tpAll = iTP >= TakeAllThreshold;
			bool tpSome = iTP >= TakePartialThreshold;

			// ---------- candle colors ----------
			bool bullBar = c >= o;
			Color trendBase = fanUp ? colorBigGreen : colorBigRed;
			Color waveBase = waveState == UP ? colorBigGreen : waveState == DN ? colorBigRed : cGray;
			bool waveSignalBar = buyBasic || sellBasic || buySuper || sellSuper || plusBuy || plusSell || vodkaBuy || vodkaSell;

			double rsiHeat = eRsiHeat.Next(rsiM);
			double kDiff = cb > 0 ? (yhat1 - sYhat[1]) / atrValue : double.NaN;
			double kSlope = eKSlope.Next(double.IsNaN(kDiff) || double.IsInfinity(kDiff) ? 0 : kDiff);
			double relVolS = eRelVol.Next(double.IsNaN(volRel) || double.IsInfinity(volRel) ? 1 : volRel);
			double adxS = eAdxS.Next(adxVal);

			double haClose = (o + h + l + c) / 4;
			double haOpen = double.IsNaN(haOpenP) ? (o + c) / 2 : (haOpenP + haCloseP) / 2;
			haOpenP = haOpen; haCloseP = haClose;
			double haBodyR = (haClose - haOpen) / atrValue;
			double haBody = double.IsNaN(haBodyR) || double.IsInfinity(haBodyR) ? 0 : haBodyR;

			double zRaw = (c - basisBB) / sd20;
			double zScore = eZ.Next(double.IsNaN(zRaw) || double.IsInfinity(zRaw) ? 0 : zRaw);

			bool stUp = SupertrendUp(h, l, c, c1, trAtr);

			Color body, wick;
			switch (CandleMode)
			{
				case TONebulaCandleMode.Vector: body = wick = pvsraColor; break;
				case TONebulaCandleMode.VolumeDelta: body = wick = cCVD; break;
				case TONebulaCandleMode.Waddah: body = cBodyColor; wick = cWickColor; break;
				case TONebulaCandleMode.Squeeze: body = cSQColor; wick = cSQColor; break;
				case TONebulaCandleMode.TrendAgreement: body = wick = (fanUp == bullBar) ? trendBase : T(trendBase, 75); break;
				case TONebulaCandleMode.TidalWave: body = wick = waveSignalBar ? waveBase : T(waveBase, 55); break;
				case TONebulaCandleMode.SignalConfluence: body = wick = DirGrad(bullBar, iTP, 0, TakeAllThreshold); break;
				case TONebulaCandleMode.RsiHeat:
					body = wick = rsiHeat >= 50 ? Grad(rsiHeat, 50, 80, T(colorBigGreen, 85), colorBigGreen) : Grad(rsiHeat, 20, 50, colorBigRed, T(colorBigRed, 85));
					break;
				case TONebulaCandleMode.KernelSlope: body = wick = DirGrad(kSlope >= 0, Math.Abs(kSlope), 0, 0.3); break;
				case TONebulaCandleMode.RelativeVolume: body = wick = DirGrad(bullBar, relVolS, 0.5, 2.5); break;
				case TONebulaCandleMode.AdxStrength: body = wick = Grad(adxS, 15, 40, T(cGray, 50), diPlus >= diMinus ? colorBigGreen : colorBigRed); break;
				case TONebulaCandleMode.HeikinAshiTrend: body = wick = DirGrad(haBody >= 0, Math.Abs(haBody), 0, 0.6); break;
				case TONebulaCandleMode.StretchFromMean: body = wick = DirGrad(zScore >= 0, Math.Abs(zScore), 0, 2.5); break;
				case TONebulaCandleMode.Supertrend: body = wick = T(stUp ? colorBigGreen : colorBigRed, stUp == bullBar ? 0 : 50); break;
				default: body = wick = Colors.Transparent; break;
			}
			if (CandleMode != TONebulaCandleMode.None)
			{
				BarBrush = WpfBrush(body);
				CandleOutlineBrush = WpfBrush(wick);
			}

			// ---------- markers ----------
			string waveTxt = waveState == UP ? "Up" : waveState == DN ? "Down" : "none";
			string ctx = "\n\nCloud: " + (fanUp ? "Up" : "Down") + "  |  Wave: " + waveTxt + "\nRSI " + F0(rsiM) + "  |  ADX " + F0(adxVal)
				+ "  |  Confluence " + iTP + "/" + TakeAllThreshold;

			if (buyBasic) AddMarker(cb, true, "•", colorBigGreen, 13, MKind.Glyph, "BUY\nTidal Wave: new up-wave (green bar opening above a prior green close)" + ctx);
			if (sellBasic) AddMarker(cb, false, "•", colorBigRed, 13, MKind.Glyph, "SELL\nTidal Wave: new down-wave (red bar opening below a prior red close)" + ctx);
			if (buySuper) AddMarker(cb, true, "", colorBigGreen, 13, MKind.Badge, "STRONG BUY  ·  Confluence " + iTP + "\nTidal Wave: new up-wave\nUltimate Buy fired within the last 4 bars" + ctx);
			if (sellSuper) AddMarker(cb, false, "", strongSellRed, 13, MKind.Badge, "STRONG SELL  ·  Confluence " + iTP + "\nTidal Wave: new down-wave\nUltimate Sell fired within the last 4 bars" + ctx);
			if (ShowPlus && plusBuy) AddMarker(cb, true, "+", colorBigGreen, 13, MKind.Glyph, "ADD LONG\nVolume imbalance: opened above the prior green close during an up-wave" + ctx);
			if (ShowPlus && plusSell) AddMarker(cb, false, "+", colorBigRed, 13, MKind.Glyph, "ADD SHORT\nVolume imbalance: opened below the prior red close during a down-wave" + ctx);
			if (ShowBigPlus && vodkaBuy) AddMarker(cb, true, "✚", colorBigGreen, 13, MKind.Glyph, "ADD LONG (STRONG)\nVodka Shot: rising triple WMA, bullish energy ratio, volume spike, up-wave" + ctx);
			if (ShowBigPlus && vodkaSell) AddMarker(cb, false, "✚", colorBigRed, 13, MKind.Glyph, "ADD SHORT (STRONG)\nVodka Shot: falling triple WMA, bearish energy ratio, volume spike, down-wave" + ctx);

			bool sTpAll = tpAll && waveState != 0;
			bool sTpSome = tpSome && !tpAll && waveState != 0;
			if (sTpAll || sTpSome)
			{
				var why = new List<string>();
				if (deadRev || sDeadRev[1]) why.Add("Engulfing reversal at a 50-bar extreme");
				if (luxRev || sLuxRev[1]) why.Add("9-count exhaustion");
				if (tramp || sTramp[1]) why.Add("Trampoline");
				if (sqz || sSqz[1]) why.Add("Squeeze momentum turn");
				if (earlyRev || sEarlyRev[1]) why.Add("Early reversal (fractal break on a vector bar)");
				if (shark || sShark[1]) why.Add("Shark (RSI outside its bands)");
				if (bands || sBands[1]) why.Add("Bollinger wick rejection");
				string title = sTpAll ? "TAKE PROFIT ✔ (all)" : "TAKE PROFIT ✓ (partial)";
				AddMarker(cb, waveState == DN, sTpAll ? "✔" : "✓", sTpAll ? cRed_ : cPurple, 19, MKind.Glyph,
					title + "\nReversal signals:\n• " + string.Join("\n• ", why) + ctx);
			}

			if (bullDiv)
			{
				Color tc = DivText(colorBigGreen, DivTransparency);
				AddMarker(pivBar, true, "Div", divBull, 10, MKind.Box, "BULLISH RSI DIVERGENCE\nPrice made a lower low, RSI made a higher low\nRSI " + F0(divPrevPlRsi) + " → " + F0(rsiPiv) + ctx, tc);
			}
			if (bearDiv)
			{
				Color tc = DivText(colorBigRed, DivTransparency);
				AddMarker(pivBar, false, "Div", divBear, 10, MKind.Box, "BEARISH RSI DIVERGENCE\nPrice made a higher high, RSI made a lower high\nRSI " + F0(divPrevPhRsi) + " → " + F0(rsiPiv) + ctx, tc);
			}
			if (bSkullUp) AddMarker(cb, true, "", T(cYellow, 20), 8, MKind.Triangle, null);
			if (bSkullDown) AddMarker(cb, false, "", T(cYellow, 20), 8, MKind.Triangle, null);

			// ---------- squeeze zones ----------
			int sqzRelease = 0;
			if (sqzOn)
			{
				if (!prevSqzOn) { curZone = new SqzZone { Start = cb, End = cb }; sqzZones.Add(curZone); }
				else if (curZone != null) curZone.End = cb;
			}
			else if (prevSqzOn && curZone != null)
			{
				int zLen = curZone.End - curZone.Start + 1;
				if (zLen >= SqueezeMinLength) sqzRelease = val > 0 ? 1 : -1;
				if (ShowSqueezeZones && ShowSqueezeRelease && zLen >= SqueezeMinLength)
				{
					bool relUp = val > 0;
					var m = AddMarker(cb, !relUp, relUp ? "⇡" : "⇣", relUp ? colorBigGreen : colorBigRed, 26, MKind.Glyph,
						"Squeeze released after " + zLen + " bars. Momentum broke " + (relUp ? "up." : "down."));
					m.Price = relUp ? sUpperBBsq[1] : sLowerBBsq[1];
				}
				curZone = null;
			}
			prevSqzOn = sqzOn;

			// ---------- session levels ----------
			orBrkThisBar = 0;
			UpdateSessions(cb, h, l, c);

			// ---------- outputs / snapshot ----------
			oSignal[0] = buySuper ? 2 : buyBasic ? 1 : sellSuper ? -2 : sellBasic ? -1 : 0;
			oAdd[0] = vodkaBuy ? 2 : plusBuy ? 1 : vodkaSell ? -2 : plusSell ? -1 : 0;
			oTP[0] = sTpAll ? 2 : sTpSome ? 1 : 0;
			oWave[0] = waveState == UP ? 1 : waveState == DN ? -1 : 0;
			oConf[0] = iTP;
			oCloud[0] = fanUp ? 1 : -1;

			// ---------- automation plots (independent of the visibility toggles) ----------
			Values[1][0]  = (buyBasic || buySuper) ? 1 : (sellBasic || sellSuper) ? -1 : 0;
			Values[2][0]  = buySuper ? 1 : sellSuper ? -1 : 0;
			Values[3][0]  = buyBasic ? 1 : sellBasic ? -1 : 0;
			Values[4][0]  = plotBuy ? 1 : plotSell ? -1 : 0;
			Values[5][0]  = plusBuy ? 1 : plusSell ? -1 : 0;
			Values[6][0]  = vodkaBuy ? 1 : vodkaSell ? -1 : 0;
			// take-profit = reversal warning: -1 in an up-wave (top risk), +1 in a down-wave (bottom risk)
			Values[7][0]  = sTpAll ? (waveState == UP ? -1 : 1) : 0;
			Values[8][0]  = sTpSome ? (waveState == UP ? -1 : 1) : 0;
			Values[9][0]  = rawBullDiv ? 1 : rawBearDiv ? -1 : 0;
			Values[10][0] = rawCrossUp ? 1 : rawCrossDown ? -1 : 0;
			Values[11][0] = orBrkThisBar;
			Values[12][0] = sqzRelease;
			Values[13][0] = waveState == UP ? 1 : waveState == DN ? -1 : 0;
			Values[14][0] = fanUp ? 1 : -1;
			Values[15][0] = iTP;
			Values[16][0] = sqzOn ? 1 : 0;
			dFanUp = fanUp; dAdx = adxVal; dRsi = rsiM; dDiP = diPlus; dDiM = diMinus; dConf = iTP;

			// ---------- alert ----------
			if (EnableAlerts && State == State.Realtime)
			{
				var sigs = new List<string>();
				if (buySuper) sigs.Add("Strong Buy (confluence " + iTP + ")");
				if (sellSuper) sigs.Add("Strong Sell (confluence " + iTP + ")");
				if (buyBasic) sigs.Add("Buy");
				if (sellBasic) sigs.Add("Sell");
				if (ShowBigPlus && vodkaBuy) sigs.Add("Add Long (Strong)");
				if (ShowBigPlus && vodkaSell) sigs.Add("Add Short (Strong)");
				if (ShowPlus && plusBuy) sigs.Add("Add Long");
				if (ShowPlus && plusSell) sigs.Add("Add Short");
				if (plotBuy) sigs.Add("Ultimate Buy");
				if (plotSell) sigs.Add("Ultimate Sell");
				if (bullDiv) sigs.Add("Bullish Divergence");
				if (bearDiv) sigs.Add("Bearish Divergence");
				if (sTpAll) sigs.Add("Take Profit ✔");
				if (sTpSome) sigs.Add("Take Profit ✓");
				if (bSkullUp) sigs.Add("9/21 Cross Up");
				if (bSkullDown) sigs.Add("9/21 Cross Down");
				if (sigs.Count > 0)
				{
					string msg = "Nebula: " + Instrument.MasterInstrument.Name + " " + TfLabel() + " | " + string.Join(", ", sigs) + " @ " + Instrument.MasterInstrument.FormatPrice(c);
					Alert("TONebula" + cb, Priority.Medium, msg, NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + AlertSound, 0, Brushes.Black, Brushes.White);
				}
			}
		}

		#region Supertrend (ta.supertrend)
		private bool stDirUp = true; private int stDir = 1;
		private bool SupertrendUp(double h, double l, double c, double c1, double tr)
		{
			double atrPrev = atr10.Value;
			double atr = atr10.Next(tr);
			double src = (h + l) / 2;
			double upper = src + stFactor * atr, lower = src - stFactor * atr;
			double prevLower = double.IsNaN(stLowerP) ? 0 : stLowerP;
			double prevUpper = double.IsNaN(stUpperP) ? 0 : stUpperP;
			lower = (lower > prevLower || c1 < prevLower) ? lower : prevLower;
			upper = (upper < prevUpper || c1 > prevUpper) ? upper : prevUpper;
			if (double.IsNaN(atrPrev)) stDir = 1;
			else if (stP == prevUpper) stDir = c > upper ? -1 : 1;
			else stDir = c < lower ? 1 : -1;
			stP = stDir == -1 ? lower : upper;
			stLowerP = lower; stUpperP = upper;
			stDirUp = stDir < 0;
			return stDirUp;
		}
		#endregion

		#region Session levels
		private void UpdateSessions(int cb, double h, double l, double c)
		{
			DateTime t = BarOpenTime();
			DateTime et;
			try { et = TimeZoneInfo.ConvertTime(t, NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, nyTz); } catch { et = t; }
			int hm = et.Hour * 60 + et.Minute;
			bool inOR = InSess(hm, orS, orE), inLon = InSess(hm, lonS, lonE), inAsia = InSess(hm, asiaS, asiaE), inRTH = InSess(hm, rthS, rthE);

			if (Bars.IsFirstBarOfSession)
			{
				if (!rOR.forming) rOR.done = false;
				if (!rLon.forming) rLon.done = false;
				if (!rAsia.forming) rAsia.done = false;
			}

			TrackRange(rOR, inOR, pInOR, cb, h, l, c);
			TrackRange(rLon, inLon, pInLon, cb, h, l, c);
			TrackRange(rAsia, inAsia, pInAsia, cb, h, l, c);

			if (pInRTH && !inRTH && cb > 0) { ycVal = Close[1]; ycX = cb; }

			if (inOR && !pInOR) { orBrkDir = 0; orBrkBar = -1; orPostHi = double.NaN; orPostLo = double.NaN; }
			if (pInOR && !inOR)
			{
				orAvg = orSizes.Count > 0 ? orSizes.Average() : double.NaN;
				orSizes.Add(rOR.hi - rOR.lo);
				if (orSizes.Count > 20) orSizes.RemoveAt(0);
			}
			if (rOR.done)
			{
				orPostHi = double.IsNaN(orPostHi) ? h : Math.Max(orPostHi, h);
				orPostLo = double.IsNaN(orPostLo) ? l : Math.Min(orPostLo, l);
				if (orBrkDir == 0)
				{
					if (c > rOR.hi) { orBrkDir = 1; orBrkBar = cb; orBrkTime = et; orBrkThisBar = 1; }
					else if (c < rOR.lo) { orBrkDir = -1; orBrkBar = cb; orBrkTime = et; orBrkThisBar = -1; }
				}
			}
			pInOR = inOR; pInLon = inLon; pInAsia = inAsia; pInRTH = inRTH;
		}

		private void TrackRange(SessRange sr, bool inNow, bool inBefore, int cb, double h, double l, double c)
		{
			if (inNow && !inBefore)
			{
				sr.hi = h; sr.lo = l; sr.x1 = cb; sr.xEnd = cb;
				sr.forming = true; sr.done = false; sr.hiBrk = false; sr.loBrk = false;
			}
			else if (inNow && sr.forming)
			{
				sr.hi = Math.Max(sr.hi, h); sr.lo = Math.Min(sr.lo, l); sr.xEnd = cb;
			}
			else if (!inNow && sr.forming)
			{
				sr.forming = false; sr.done = true;
			}
			if (sr.done)
			{
				if (c > sr.hi) sr.hiBrk = true;
				if (c < sr.lo) sr.loBrk = true;
			}
		}

		private DateTime BarOpenTime()
		{
			DateTime t = Time[0];
			switch (BarsPeriod.BarsPeriodType)
			{
				case BarsPeriodType.Minute: return t.AddMinutes(-BarsPeriod.Value);
				case BarsPeriodType.Second: return t.AddSeconds(-BarsPeriod.Value);
				default: return t;
			}
		}

		private static bool InSess(int hm, int s, int e)
		{
			if (s < 0) return false;
			return s < e ? (hm >= s && hm < e) : (hm >= s || hm < e);
		}

		private static void ParseSession(string txt, out int s, out int e)
		{
			s = e = -1;
			if (string.IsNullOrWhiteSpace(txt)) return;
			string[] p = txt.Trim().Split('-');
			if (p.Length != 2) return;
			int a, b;
			if (!int.TryParse(p[0].Trim(), out a) || !int.TryParse(p[1].Trim(), out b)) return;
			s = (a / 100) * 60 + a % 100;
			e = (b / 100) * 60 + b % 100;
		}
		#endregion

		#region Math helpers (Pine semantics: na if not enough bars or any na in the window)
		private double Sma(ISeries<double> s, int len)
		{
			if (len < 1 || CurrentBar < len - 1) return double.NaN;
			double sum = 0;
			for (int i = 0; i < len; i++) { double x = s[i]; if (double.IsNaN(x)) return double.NaN; sum += x; }
			return sum / len;
		}
		private double Sum(ISeries<double> s, int len)
		{
			if (len < 1 || CurrentBar < len - 1) return double.NaN;
			double sum = 0;
			for (int i = 0; i < len; i++) { double x = s[i]; if (double.IsNaN(x)) return double.NaN; sum += x; }
			return sum;
		}
		private double Stdev(ISeries<double> s, int len)
		{
			double m = Sma(s, len);
			if (double.IsNaN(m)) return double.NaN;
			double ss = 0;
			for (int i = 0; i < len; i++) { double d = s[i] - m; ss += d * d; }
			return Math.Sqrt(ss / len);
		}
		private double MeanDev(ISeries<double> s, int len)
		{
			double m = Sma(s, len);
			if (double.IsNaN(m)) return double.NaN;
			double sum = 0;
			for (int i = 0; i < len; i++) sum += Math.Abs(s[i] - m);
			return sum / len;
		}
		private double Wma(ISeries<double> s, int len)
		{
			if (len < 1 || CurrentBar < len - 1) return double.NaN;
			double sum = 0, norm = 0;
			for (int i = 0; i < len; i++)
			{
				double x = s[i]; if (double.IsNaN(x)) return double.NaN;
				double w = len - i; sum += x * w; norm += w;
			}
			return sum / norm;
		}
		private double Highest(ISeries<double> s, int len)
		{
			if (len < 1 || CurrentBar < len - 1) return double.NaN;
			double m = double.MinValue;
			for (int i = 0; i < len; i++) { double x = s[i]; if (double.IsNaN(x)) return double.NaN; if (x > m) m = x; }
			return m;
		}
		private double Lowest(ISeries<double> s, int len)
		{
			if (len < 1 || CurrentBar < len - 1) return double.NaN;
			double m = double.MaxValue;
			for (int i = 0; i < len; i++) { double x = s[i]; if (double.IsNaN(x)) return double.NaN; if (x < m) m = x; }
			return m;
		}
		private double LinReg(ISeries<double> s, int len)
		{
			if (len < 2 || CurrentBar < len - 1) return double.NaN;
			double sx = 0, sy = 0, sxx = 0, sxy = 0;
			for (int i = 0; i < len; i++)
			{
				double y = s[i]; if (double.IsNaN(y)) return double.NaN;
				double x = len - 1 - i;
				sx += x; sy += y; sxx += x * x; sxy += x * y;
			}
			double slope = (len * sxy - sx * sy) / (len * sxx - sx * sx);
			double intercept = (sy - slope * sx) / len;
			return intercept + slope * (len - 1);
		}
		private bool XOver(ISeries<double> a, ISeries<double> b)
		{
			return CurrentBar >= 1 && a[0] > b[0] && a[1] <= b[1];
		}
		private bool XUnder(ISeries<double> a, ISeries<double> b)
		{
			return CurrentBar >= 1 && a[0] < b[0] && a[1] >= b[1];
		}
		private bool XOverK(ISeries<double> a, double k) { return CurrentBar >= 1 && a[0] > k && a[1] <= k; }
		private bool XUnderK(ISeries<double> a, double k) { return CurrentBar >= 1 && a[0] < k && a[1] >= k; }

		private static double RsiFrom(double up, double dn)
		{
			if (double.IsNaN(up) || double.IsNaN(dn)) return double.NaN;
			return dn == 0 ? 100 : up == 0 ? 0 : 100 - (100 / (1 + up / dn));
		}

		private bool PivotLow(Series<double> s, int L, int R)
		{
			if (CurrentBar < L + R) return false;
			double p = s[R]; if (double.IsNaN(p)) return false;
			for (int k = 1; k <= L; k++) { double x = s[R + k]; if (double.IsNaN(x) || x <= p) return false; }
			for (int k = 1; k <= R; k++) { double x = s[R - k]; if (double.IsNaN(x) || x < p) return false; }
			return true;
		}
		private bool PivotHigh(Series<double> s, int L, int R)
		{
			if (CurrentBar < L + R) return false;
			double p = s[R]; if (double.IsNaN(p)) return false;
			for (int k = 1; k <= L; k++) { double x = s[R + k]; if (double.IsNaN(x) || x >= p) return false; }
			for (int k = 1; k <= R; k++) { double x = s[R - k]; if (double.IsNaN(x) || x > p) return false; }
			return true;
		}
		private bool InRange(int bars) { return DivRangeMin <= bars && bars <= DivRangeMax; }

		private static string F0(double x) { return double.IsNaN(x) ? "–" : Math.Round(x).ToString("0", CultureInfo.InvariantCulture); }
		#endregion

		#region Color helpers
		private static Color Hex(uint rgb) { return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); }
		private static Color T(Color c, double transp)
		{
			transp = Math.Max(0, Math.Min(100, transp));
			return Color.FromArgb((byte)Math.Round(255 * (100 - transp) / 100.0), c.R, c.G, c.B);
		}
		private static double Transp(Color c) { return 100 - c.A / 2.55; }
		private static Color Grad(double v, double lo, double hi, Color a, Color b)
		{
			if (double.IsNaN(v)) return Colors.Transparent;
			double t = hi == lo ? 1 : (v - lo) / (hi - lo);
			t = Math.Max(0, Math.Min(1, t));
			return Color.FromArgb((byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
				(byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
		}
		private Color DirGrad(bool up, double strength, double lo, double hi)
		{
			Color b = up ? colorBigGreen : colorBigRed;
			return Grad(strength, lo, hi, T(b, 85), b);
		}
		private static double Lum(Color c) { return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B; }
		private Color DivText(Color b, int transp)
		{
			double a = (100 - transp) / 100.0;
			return a * Lum(b) + (1 - a) * Lum(ChartBg()) > 140 ? Colors.Black : Colors.White;
		}
		private static uint Argb(Color c) { return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B; }
		private static Color FromArgb(uint k) { return Color.FromArgb((byte)(k >> 24), (byte)(k >> 16), (byte)(k >> 8), (byte)k); }

		private Brush WpfBrush(Color c)
		{
			uint k = Argb(c);
			SolidColorBrush b;
			if (!wpfBrushes.TryGetValue(k, out b)) { b = new SolidColorBrush(c); b.Freeze(); wpfBrushes[k] = b; }
			return b;
		}
		private Color ChartFg()
		{
			var b = ChartControl != null ? ChartControl.Properties.ChartText as SolidColorBrush : null;
			return b != null ? b.Color : Colors.WhiteSmoke;
		}
		private Color ChartBg()
		{
			var b = ChartControl != null ? ChartControl.Properties.ChartBackground as SolidColorBrush : null;
			return b != null ? b.Color : Color.FromRgb(20, 20, 24);
		}
		#endregion

		#region Drawing registries
		private Marker AddMarker(int bar, bool below, string text, Color col, float size, MKind kind, string tip, Color? textCol = null)
		{
			var m = new Marker { Bar = bar, Below = below, Text = text, Col = col, Size = size, Kind = kind, Tip = tip, TextCol = textCol ?? Colors.White };
			List<Marker> list;
			if (!markers.TryGetValue(bar, out list)) { list = new List<Marker>(); markers[bar] = list; }
			list.Add(m);
			return m;
		}

		private void AddImbalance(int bar, double price, double fill, int dir, Color col)
		{
			imbLines.Add(new ImbLine { Bar = bar, Price = price, Fill = fill, Dir = dir, Col = col });
			if (imbLines.Count > 400) imbLines.RemoveAt(0);
		}

		private string TfLabel()
		{
			int v = BarsPeriod.Value;
			switch (BarsPeriod.BarsPeriodType)
			{
				case BarsPeriodType.Tick: return v + "T";
				case BarsPeriodType.Second: return v + "s";
				case BarsPeriodType.Minute: return v < 60 ? v + "m" : (v % 60 == 0 ? (v / 60) + "h" : v + "m");
				case BarsPeriodType.Range: return v + "R";
				case BarsPeriodType.Volume: return v + "V";
				case BarsPeriodType.Day: return "D";
				case BarsPeriodType.Week: return "W";
				default: return BarsPeriod.ToString();
			}
		}
		#endregion

		#region Rendering
		// Automation plots hold +1/-1 values; keep them out of the price scale. Only the HEMA line counts.
		public override void OnCalculateMinMax()
		{
			MinValue = double.MaxValue;
			MaxValue = double.MinValue;
			if (!ShowHema || ChartBars == null) return;
			for (int i = ChartBars.FromIndex; i <= ChartBars.ToIndex; i++)
			{
				if (!Values[0].IsValidDataPointAt(i)) continue;
				double v = Values[0].GetValueAt(i);
				if (v < MinValue) MinValue = v;
				if (v > MaxValue) MaxValue = v;
			}
		}

		public override void OnRenderTargetChanged()
		{
			DisposeDx();
		}

		private void DisposeDx()
		{
			foreach (var b in dxBrushes.Values) if (b != null && !b.IsDisposed) b.Dispose();
			dxBrushes.Clear();
			if (dotStyle != null) { dotStyle.Dispose(); dotStyle = null; }
			if (dashStyle != null) { dashStyle.Dispose(); dashStyle = null; }
		}

		private D2D.SolidColorBrush Dx(Color c)
		{
			uint k = Argb(c);
			D2D.SolidColorBrush b;
			if (!dxBrushes.TryGetValue(k, out b) || b == null || b.IsDisposed)
			{
				b = new D2D.SolidColorBrush(RenderTarget, new DX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f));
				dxBrushes[k] = b;
			}
			return b;
		}

		private D2D.StrokeStyle Style(TONebulaLineStyle st)
		{
			if (st == TONebulaLineStyle.Solid) return null;
			if (st == TONebulaLineStyle.Dotted)
			{
				if (dotStyle == null)
					dotStyle = new D2D.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, new D2D.StrokeStyleProperties { DashStyle = D2D.DashStyle.Dot, DashCap = D2D.CapStyle.Round });
				return dotStyle;
			}
			if (dashStyle == null)
				dashStyle = new D2D.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, new D2D.StrokeStyleProperties { DashStyle = D2D.DashStyle.Dash });
			return dashStyle;
		}

		private DW.TextFormat Fmt(string family, float size, bool bold)
		{
			string key = family + "|" + size.ToString(CultureInfo.InvariantCulture) + "|" + bold;
			DW.TextFormat f;
			if (!fmts.TryGetValue(key, out f))
			{
				f = new DW.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, family, bold ? DW.FontWeight.Bold : DW.FontWeight.Normal, DW.FontStyle.Normal, size);
				f.WordWrapping = DW.WordWrapping.NoWrap;
				fmts[key] = f;
			}
			return f;
		}

		private DX.Vector2 Measure(string s, DW.TextFormat f)
		{
			using (var tl = new DW.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, s ?? "", f, 4000, 4000))
				return new DX.Vector2(tl.Metrics.WidthIncludingTrailingWhitespace, tl.Metrics.Height);
		}

		private void Text(string s, DW.TextFormat f, float x, float y, Color c)
		{
			using (var tl = new DW.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, s ?? "", f, 4000, 4000))
				RenderTarget.DrawTextLayout(new DX.Vector2(x, y), tl, Dx(c), D2D.DrawTextOptions.NoSnap);
		}

		private float XAt(ChartControl cc, int idx)
		{
			int lastIdx = ChartBars.Bars.Count - 1;
			if (idx < 0) idx = 0;
			if (idx <= lastIdx) return cc.GetXByBarIndex(ChartBars, idx);
			return cc.GetXByBarIndex(ChartBars, lastIdx) + (float)((idx - lastIdx) * cc.Properties.BarDistance);
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (ChartBars == null || RenderTarget == null || IsInHitTest) return;
			base.OnRender(chartControl, chartScale);

			lock (sync)
			{
				if (CurrentBar < 2) return;
				var oldAA = RenderTarget.AntialiasMode;
				RenderTarget.AntialiasMode = D2D.AntialiasMode.PerPrimitive;
				hoverItems.Clear();
				try
				{
					int from = Math.Max(1, ChartBars.FromIndex), to = Math.Min(ChartBars.ToIndex, CurrentBar);
					if (CloudType != TONebulaCloudType.None) RenderCloud(chartControl, chartScale, from, to);
					if (ShowSqueezeZones) RenderSqueeze(chartControl, chartScale, from, to);
					if (ShowVolumeImbalances) RenderImbalances(chartControl, chartScale, to);
					if (DrawDivLines) RenderDivLines(chartControl, chartScale, from, to);
					if (ShowSessionLevels) RenderLevels(chartControl, chartScale);
					RenderMarkers(chartControl, chartScale, ChartBars.FromIndex, ChartBars.ToIndex);
					if (ShowDashboard) RenderDashboard(chartControl);
					RenderTooltip(chartControl);
				}
				finally { RenderTarget.AntialiasMode = oldAA; }
			}
		}

		private void RenderCloud(ChartControl cc, ChartScale cs, int from, int to)
		{
			for (int i = from; i <= to; i++)
			{
				double f0 = sFan.GetValueAt(i - 1), m0 = sMg.GetValueAt(i - 1), f1 = sFan.GetValueAt(i), m1 = sMg.GetValueAt(i);
				if (double.IsNaN(f0) || double.IsNaN(m0) || double.IsNaN(f1) || double.IsNaN(m1)) continue;
				Color col = FromArgb((uint)sCloudArgb.GetValueAt(i));
				if (col.A == 0) continue;
				float x0 = cc.GetXByBarIndex(ChartBars, i - 1), x1 = cc.GetXByBarIndex(ChartBars, i);
				using (var g = new D2D.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
				{
					using (var sink = g.Open())
					{
						sink.BeginFigure(new DX.Vector2(x0, cs.GetYByValue(f0)), D2D.FigureBegin.Filled);
						sink.AddLine(new DX.Vector2(x1, cs.GetYByValue(f1)));
						sink.AddLine(new DX.Vector2(x1, cs.GetYByValue(m1)));
						sink.AddLine(new DX.Vector2(x0, cs.GetYByValue(m0)));
						sink.EndFigure(D2D.FigureEnd.Closed);
						sink.Close();
					}
					RenderTarget.FillGeometry(g, Dx(col));
				}
			}
		}

		private void RenderSqueeze(ChartControl cc, ChartScale cs, int from, int to)
		{
			SolidColorBrush zoneBrush = SqueezeZoneBrush as SolidColorBrush;
			Color fill = T(zoneBrush != null ? zoneBrush.Color : cBlue, SqueezeZoneTransparency);
			Color edge = T(fill, Math.Max(0, SqueezeZoneTransparency - 45));
			foreach (var z in sqzZones)
			{
				if (z.End - z.Start + 1 < SqueezeMinLength) continue;
				if (z.End < from - 1 || z.Start > to + 1) continue;
				var pts = new List<DX.Vector2>();
				for (int i = z.Start; i <= z.End; i++) pts.Add(new DX.Vector2(cc.GetXByBarIndex(ChartBars, i), cs.GetYByValue(sUpperBBsq.GetValueAt(i))));
				for (int i = z.End; i >= z.Start; i--) pts.Add(new DX.Vector2(cc.GetXByBarIndex(ChartBars, i), cs.GetYByValue(sLowerBBsq.GetValueAt(i))));
				using (var g = SmoothClosed(pts))
				{
					RenderTarget.FillGeometry(g, Dx(fill));
					RenderTarget.DrawGeometry(g, Dx(edge), 1f);
				}
			}
		}

		// Closed Catmull-Rom spline -> cubic Beziers (Pine's curved polyline)
		private D2D.PathGeometry SmoothClosed(List<DX.Vector2> p)
		{
			var g = new D2D.PathGeometry(NinjaTrader.Core.Globals.D2DFactory);
			using (var sink = g.Open())
			{
				int n = p.Count;
				sink.BeginFigure(p[0], D2D.FigureBegin.Filled);
				for (int i = 0; i < n; i++)
				{
					DX.Vector2 p0 = p[(i - 1 + n) % n], p1 = p[i], p2 = p[(i + 1) % n], p3 = p[(i + 2) % n];
					var c1 = new DX.Vector2(p1.X + (p2.X - p0.X) / 6f, p1.Y + (p2.Y - p0.Y) / 6f);
					var c2 = new DX.Vector2(p2.X - (p3.X - p1.X) / 6f, p2.Y - (p3.Y - p1.Y) / 6f);
					sink.AddBezier(new D2D.BezierSegment { Point1 = c1, Point2 = c2, Point3 = p2 });
				}
				sink.EndFigure(D2D.FigureEnd.Closed);
				sink.Close();
			}
			return g;
		}

		private void RenderImbalances(ChartControl cc, ChartScale cs, int to)
		{
			var st = Style(ImbalanceStyle);
			foreach (var il in imbLines)
			{
				if (il.Bar > to) continue;
				float x0 = XAt(cc, il.Bar), x1 = XAt(cc, il.Bar + ImbalanceExtend);
				float y = cs.GetYByValue(il.Price);
				if (st != null) RenderTarget.DrawLine(new DX.Vector2(x0, y), new DX.Vector2(x1, y), Dx(il.Col), ImbalanceWidth, st);
				else RenderTarget.DrawLine(new DX.Vector2(x0, y), new DX.Vector2(x1, y), Dx(il.Col), ImbalanceWidth);
			}
		}

		private void RenderDivLines(ChartControl cc, ChartScale cs, int from, int to)
		{
			foreach (var d in divLines)
			{
				if (d.X2 < from || d.X1 > to) continue;
				RenderTarget.DrawLine(new DX.Vector2(XAt(cc, d.X1), cs.GetYByValue(d.Y1)), new DX.Vector2(XAt(cc, d.X2), cs.GetYByValue(d.Y2)), Dx(d.Col), 1f);
			}
		}

		private void RenderMarkers(ChartControl cc, ChartScale cs, int from, int to)
		{
			const float gap = 4f;
			for (int i = from; i <= to; i++)
			{
				List<Marker> list;
				if (!markers.TryGetValue(i, out list)) continue;
				float x = cc.GetXByBarIndex(ChartBars, i);
				float yLow = cs.GetYByValue(ChartBars.Bars.GetLow(i)), yHigh = cs.GetYByValue(ChartBars.Bars.GetHigh(i));
				float stackBelow = 0, stackAbove = 0;
				foreach (var m in list)
				{
					float hgt = DrawMarker(cc, cs, m, x, m.Below ? yLow + gap + stackBelow : yHigh - gap - stackAbove);
					if (double.IsNaN(m.Price)) { if (m.Below) stackBelow += hgt + 2; else stackAbove += hgt + 2; }
				}
			}

			// opening range breakout marker (today only)
			if (ShowSessionLevels && LevelOR && LevelMarkBreakout && rOR.done && orBrkDir != 0 && orBrkBar >= from && orBrkBar <= to)
			{
				var m = new Marker { Bar = orBrkBar, Below = orBrkDir == 1, Text = orBrkDir == 1 ? "▲" : "▼", Col = orBrkDir == 1 ? colorBigGreen : colorBigRed,
					Size = 12, Kind = MKind.Glyph, Tip = "Opening range breakout " + (orBrkDir == 1 ? "up" : "down") + " at " + orBrkTime.ToString("HH:mm") };
				float x = cc.GetXByBarIndex(ChartBars, orBrkBar);
				float y = m.Below ? cs.GetYByValue(ChartBars.Bars.GetLow(orBrkBar)) + gap : cs.GetYByValue(ChartBars.Bars.GetHigh(orBrkBar)) - gap;
				DrawMarker(cc, cs, m, x, y);
			}
		}

		// returns the marker's height; y is the edge nearest the bar
		private float DrawMarker(ChartControl cc, ChartScale cs, Marker m, float x, float y)
		{
			bool anchored = !double.IsNaN(m.Price);
			if (anchored) y = cs.GetYByValue(m.Price);
			DX.RectangleF r;
			switch (m.Kind)
			{
				case MKind.Glyph:
				{
					var f = Fmt("Segoe UI Symbol", m.Size, true);
					var sz = Measure(m.Text, f);
					float top = anchored ? y - sz.Y / 2 : (m.Below ? y : y - sz.Y);
					Text(m.Text, f, x - sz.X / 2, top, m.Col);
					r = new DX.RectangleF(x - sz.X / 2, top, sz.X, sz.Y);
					break;
				}
				case MKind.Badge:
				{
					float w = 14, hb = 9, pt = 5;
					using (var g = new D2D.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
					{
						using (var sink = g.Open())
						{
							if (m.Below)
							{
								sink.BeginFigure(new DX.Vector2(x, y), D2D.FigureBegin.Filled);
								sink.AddLine(new DX.Vector2(x + pt, y + pt));
								sink.AddLine(new DX.Vector2(x + w / 2, y + pt));
								sink.AddLine(new DX.Vector2(x + w / 2, y + pt + hb));
								sink.AddLine(new DX.Vector2(x - w / 2, y + pt + hb));
								sink.AddLine(new DX.Vector2(x - w / 2, y + pt));
								sink.AddLine(new DX.Vector2(x - pt, y + pt));
							}
							else
							{
								sink.BeginFigure(new DX.Vector2(x, y), D2D.FigureBegin.Filled);
								sink.AddLine(new DX.Vector2(x + pt, y - pt));
								sink.AddLine(new DX.Vector2(x + w / 2, y - pt));
								sink.AddLine(new DX.Vector2(x + w / 2, y - pt - hb));
								sink.AddLine(new DX.Vector2(x - w / 2, y - pt - hb));
								sink.AddLine(new DX.Vector2(x - w / 2, y - pt));
								sink.AddLine(new DX.Vector2(x - pt, y - pt));
							}
							sink.EndFigure(D2D.FigureEnd.Closed);
							sink.Close();
						}
						RenderTarget.FillGeometry(g, Dx(m.Col));
					}
					r = m.Below ? new DX.RectangleF(x - w / 2, y, w, pt + hb) : new DX.RectangleF(x - w / 2, y - pt - hb, w, pt + hb);
					break;
				}
				case MKind.Box:
				{
					var f = Fmt("Segoe UI", m.Size, true);
					var sz = Measure(m.Text, f);
					float pad = 3, pt = 4, w = sz.X + pad * 2, hb = sz.Y + 1;
					float top = m.Below ? y + pt : y - pt - hb;
					using (var g = new D2D.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
					{
						using (var sink = g.Open())
						{
							float edgeY = m.Below ? top : top + hb;
							sink.BeginFigure(new DX.Vector2(x, y), D2D.FigureBegin.Filled);
							sink.AddLine(new DX.Vector2(x + pt, edgeY));
							sink.AddLine(new DX.Vector2(x + w / 2, edgeY));
							sink.AddLine(new DX.Vector2(x + w / 2, m.Below ? top + hb : top));
							sink.AddLine(new DX.Vector2(x - w / 2, m.Below ? top + hb : top));
							sink.AddLine(new DX.Vector2(x - w / 2, edgeY));
							sink.AddLine(new DX.Vector2(x - pt, edgeY));
							sink.EndFigure(D2D.FigureEnd.Closed);
							sink.Close();
						}
						RenderTarget.FillGeometry(g, Dx(m.Col));
					}
					Text(m.Text, f, x - sz.X / 2, top, m.TextCol);
					r = new DX.RectangleF(x - w / 2, m.Below ? y : top, w, pt + hb);
					break;
				}
				default: // Triangle
				{
					float s = m.Size;
					using (var g = new D2D.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
					{
						using (var sink = g.Open())
						{
							if (m.Below)
							{
								sink.BeginFigure(new DX.Vector2(x, y), D2D.FigureBegin.Filled);
								sink.AddLine(new DX.Vector2(x + s / 2, y + s));
								sink.AddLine(new DX.Vector2(x - s / 2, y + s));
							}
							else
							{
								sink.BeginFigure(new DX.Vector2(x, y), D2D.FigureBegin.Filled);
								sink.AddLine(new DX.Vector2(x + s / 2, y - s));
								sink.AddLine(new DX.Vector2(x - s / 2, y - s));
							}
							sink.EndFigure(D2D.FigureEnd.Closed);
							sink.Close();
						}
						RenderTarget.FillGeometry(g, Dx(m.Col));
					}
					r = m.Below ? new DX.RectangleF(x - s / 2, y, s, s) : new DX.RectangleF(x - s / 2, y - s, s, s);
					break;
				}
			}
			if (!string.IsNullOrEmpty(m.Tip)) hoverItems.Add(new HoverItem { Rect = r, Tip = m.Tip });
			return r.Height;
		}

		private sealed class LvlItem { public double Price; public string Name; public Color Col; public int X1; public TONebulaLineStyle St; }

		private void RenderLevels(ChartControl cc, ChartScale cs)
		{
			int last = ChartBars.Bars.Count - 1;
			Color fg = ChartFg();
			var items = new List<LvlItem>();
			Func<bool, bool, Color> lvlColor = (brk, isHigh) => T(isHigh ? colorBigGreen : colorBigRed, brk ? 10 : LevelTransparency);

			Action<SessRange, string, TONebulaLineStyle> addRange = (sr, nm, st) =>
			{
				if (sr.forming)
				{
					if (LevelShadeForming && sr.x1 >= 0)
					{
						float x0 = XAt(cc, sr.x1), x1 = XAt(cc, last);
						float y0 = cs.GetYByValue(sr.hi), y1 = cs.GetYByValue(sr.lo);
						var rect = new DX.RectangleF(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
						RenderTarget.FillRectangle(rect, Dx(T(fg, 94)));
						RenderTarget.DrawRectangle(rect, Dx(T(fg, 75)), 1f, Style(TONebulaLineStyle.Dotted));
					}
				}
				else if (sr.done)
				{
					items.Add(new LvlItem { Price = sr.hi, Name = nm + "H", Col = lvlColor(sr.hiBrk, true), X1 = sr.xEnd, St = st });
					items.Add(new LvlItem { Price = sr.lo, Name = nm + "L", Col = lvlColor(sr.loBrk, false), X1 = sr.xEnd, St = st });
				}
			};

			if (LevelAsia) addRange(rAsia, "Asia", TONebulaLineStyle.Dotted);
			if (LevelLondon) addRange(rLon, "Lon", TONebulaLineStyle.Dashed);
			if (LevelOR)
			{
				addRange(rOR, "OR", TONebulaLineStyle.Solid);
				if (rOR.done && LevelExtensions && orBrkDir != 0)
				{
					double rng = rOR.hi - rOR.lo;
					double ext1 = orBrkDir == 1 ? rOR.hi + 0.5 * rng : rOR.lo - 0.5 * rng;
					double ext2 = orBrkDir == 1 ? rOR.hi + rng : rOR.lo - rng;
					Color ec = T(orBrkDir == 1 ? colorBigGreen : colorBigRed, 50);
					items.Add(new LvlItem { Price = ext1, Name = "OR 1.5×", Col = ec, X1 = last - 6, St = TONebulaLineStyle.Dotted });
					bool hit = orBrkDir == 1 ? orPostHi >= ext1 : orPostLo <= ext1;
					if (hit) items.Add(new LvlItem { Price = ext2, Name = "OR 2×", Col = ec, X1 = last - 6, St = TONebulaLineStyle.Dotted });
				}
			}
			if (LevelYC && !double.IsNaN(ycVal)) items.Add(new LvlItem { Price = ycVal, Name = "Close", Col = T(fg, LevelTransparency), X1 = ycX, St = TONebulaLineStyle.Dotted });

			lvlUpTxt = "—"; lvlDnTxt = "—";
			if (items.Count == 0) return;

			float xEnd = XAt(cc, last + LevelTagOffset);
			foreach (var it in items)
			{
				if (double.IsNaN(it.Price)) continue;
				float y = cs.GetYByValue(it.Price);
				var st = Style(it.St);
				var p0 = new DX.Vector2(XAt(cc, it.X1), y); var p1 = new DX.Vector2(xEnd, y);
				if (st != null) RenderTarget.DrawLine(p0, p1, Dx(it.Col), 1f, st); else RenderTarget.DrawLine(p0, p1, Dx(it.Col), 1f);
			}

			// tags (merged when close together)
			var sorted = items.Where(q => !double.IsNaN(q.Price)).OrderBy(q => q.Price).ToList();
			double tol = LevelMergeTicks * TickSize;
			var f = Fmt("Segoe UI", 11f, false);
			int i = 0;
			while (i < sorted.Count)
			{
				var first = sorted[i];
				var txt = new StringBuilder(first.Name);
				var tip = new StringBuilder(first.Name + "  " + Instrument.MasterInstrument.FormatPrice(first.Price));
				int j = i + 1;
				while (j < sorted.Count && sorted[j].Price - first.Price <= tol)
				{
					txt.Append(" · ").Append(sorted[j].Name);
					tip.Append("\n").Append(sorted[j].Name).Append("  ").Append(Instrument.MasterInstrument.FormatPrice(sorted[j].Price));
					j++;
				}
				string s = txt.ToString();
				var sz = Measure(s, f);
				float y = cs.GetYByValue(first.Price);
				float pt = 5, pad = 4, hb = sz.Y + 2, w = sz.X + pad * 2;
				using (var g = new D2D.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
				{
					using (var sink = g.Open())
					{
						sink.BeginFigure(new DX.Vector2(xEnd, y), D2D.FigureBegin.Filled);
						sink.AddLine(new DX.Vector2(xEnd + pt, y - hb / 2));
						sink.AddLine(new DX.Vector2(xEnd + pt + w, y - hb / 2));
						sink.AddLine(new DX.Vector2(xEnd + pt + w, y + hb / 2));
						sink.AddLine(new DX.Vector2(xEnd + pt, y + hb / 2));
						sink.EndFigure(D2D.FigureEnd.Closed);
						sink.Close();
					}
					RenderTarget.FillGeometry(g, Dx(T(first.Col, 82)));
				}
				Text(s, f, xEnd + pt + pad, y - sz.Y / 2, T(first.Col, 0));
				hoverItems.Add(new HoverItem { Rect = new DX.RectangleF(xEnd, y - hb / 2, pt + w, hb), Tip = tip.ToString() });
				i = j;
			}

			// nearest above / below (live price)
			double px = ChartBars.Bars.GetClose(last);
			double bestUp = double.NaN, bestDn = double.NaN;
			foreach (var it in sorted)
			{
				double d = it.Price - px;
				if (d > 0 && (double.IsNaN(bestUp) || d < bestUp)) { bestUp = d; lvlUpTxt = it.Name + "  +" + Instrument.MasterInstrument.FormatPrice(d); }
				if (d < 0 && (double.IsNaN(bestDn) || -d < bestDn)) { bestDn = -d; lvlDnTxt = it.Name + "  -" + Instrument.MasterInstrument.FormatPrice(-d); }
			}
		}

		private sealed class DashRow { public string Key, Val, Tip; public Color ValCol, BarCol; public double Pct = -1; public bool Head; }

		private Color BarColor(double pct)
		{
			return pct < 50 ? Grad(pct, 0, 50, colorBigRed, cGray) : Grad(pct, 50, 100, cGray, colorBigGreen);
		}

		private void RenderDashboard(ChartControl cc)
		{
			Color fg = ChartFg(), bg = ChartBg();
			Color dashBg = T(bg, DashTransparency);
			Color headBg = T(fg, Math.Min(100, 88 + DashTransparency / 10.0));
			Color keyCol = T(fg, 35);
			float fs = DashTextSize == TONebulaTextSize.Tiny ? 10f : DashTextSize == TONebulaTextSize.Normal ? 14f : DashTextSize == TONebulaTextSize.Large ? 17f : 12f;
			var f = Fmt("Segoe UI", fs, false);
			var fb = Fmt("Segoe UI", fs, true);

			var rows = new List<DashRow>();
			rows.Add(new DashRow { Key = "NEBULA", Val = Instrument.MasterInstrument.Name + " · " + TfLabel(), Head = true });
			rows.Add(new DashRow { Key = "Theme", Val = appliedTheme.HasValue ? appliedTheme.Value.ToString() : "—", ValCol = colorBigGreen,
				Tip = "Color theme this chart is running: " + ColorTheme + "\nCloud colors: " + CloudPalette });
			rows.Add(new DashRow { Key = "Trend (cloud)", Val = dFanUp ? "▲ Up" : "▼ Down", ValCol = dFanUp ? colorBigGreen : colorBigRed });
			rows.Add(new DashRow { Key = "Wave", Val = waveState == 0 ? "—" : waveState == UP ? "▲ Up" : "▼ Down", ValCol = waveState == UP ? colorBigGreen : waveState == DN ? colorBigRed : fg });

			double adxPct = double.IsNaN(dAdx) ? 0 : Math.Min(100.0, dAdx * 2);
			Color adxCol = BarColor(50 + (dDiP >= dDiM ? 1 : -1) * adxPct / 2);
			rows.Add(new DashRow { Key = "ADX", Val = F0(dAdx), Pct = adxPct, BarCol = adxCol, ValCol = adxCol,
				Tip = "ADX " + F0(dAdx) + (dAdx >= 25 ? " (strong trend)" : dAdx >= 20 ? " (trend building)" : " (choppy)") + ".\nBar is full at 50. Leans green when buyers lead (+DI), red when sellers lead (-DI)." });
			double rsiV = double.IsNaN(dRsi) ? 50 : dRsi;
			rows.Add(new DashRow { Key = "RSI", Val = F0(dRsi), Pct = rsiV, BarCol = BarColor(rsiV), ValCol = BarColor(rsiV),
				Tip = "RSI " + F0(dRsi) + (dRsi >= 70 ? " (overbought)" : dRsi <= 30 ? " (oversold)" : "") + ".\nGray at 50, redder toward 0, greener toward 100." });
			double confPct = Math.Min(100.0, 100.0 * dConf / Math.Max(1, TakeAllThreshold));
			Color confTarget = waveState == UP ? colorBigRed : waveState == DN ? colorBigGreen : cOrange;
			Color confCol = Grad(confPct, 0, 100, cGray, confTarget);
			rows.Add(new DashRow { Key = "Confluence", Val = dConf + "/" + TakeAllThreshold, Pct = confPct, BarCol = confCol, ValCol = confCol,
				Tip = dConf + " of " + TakeAllThreshold + " reversal points needed for Take Profit ✔.\nFills toward red in an up-wave (top risk), green in a down-wave (bottom risk)." });

			if (DashShowLevels && ShowSessionLevels)
			{
				rows.Add(new DashRow { Key = "SESSION LEVELS", Val = "", Head = true });
				double orSize = rOR.hi - rOR.lo;
				string orTxt = rOR.done ? Instrument.MasterInstrument.FormatPrice(orSize) + " pts" + (double.IsNaN(orAvg) || orAvg == 0 ? "" : " · " + (orSize / orAvg).ToString("0.0", CultureInfo.InvariantCulture) + "× avg")
					: rOR.forming ? "Forming…" : "—";
				rows.Add(new DashRow { Key = "Opening range", Val = orTxt, ValCol = fg });
				string brk = !rOR.done ? "—" : orBrkDir == 1 ? "▲ Up at " + orBrkTime.ToString("HH:mm") : orBrkDir == -1 ? "▼ Down at " + orBrkTime.ToString("HH:mm") : "Inside";
				rows.Add(new DashRow { Key = "OR break", Val = brk, ValCol = !rOR.done ? fg : orBrkDir == 1 ? colorBigGreen : orBrkDir == -1 ? colorBigRed : fg });
				rows.Add(new DashRow { Key = "Nearest above", Val = lvlUpTxt, ValCol = fg });
				rows.Add(new DashRow { Key = "Nearest below", Val = lvlDnTxt, ValCol = fg });
			}

			float pad = 7, colGap = 18, rowH = fs * 1.5f;
			float blockW = fs * 0.5f, blockH = fs * 0.72f, blockGap = 1.5f;
			float barW = DashBarBlocks * (blockW + blockGap);
			float keyW = 0, valW = 0;
			foreach (var r in rows)
			{
				keyW = Math.Max(keyW, Measure(r.Key, r.Head ? fb : f).X);
				float vw = Measure(r.Val, f).X + (r.Pct >= 0 ? barW + 6 : 0);
				valW = Math.Max(valW, vw);
			}
			float W = pad + keyW + colGap + valW + pad, H = rows.Count * rowH + 2;

			float px = ChartPanel.X, py = ChartPanel.Y, pw = ChartPanel.W, ph = ChartPanel.H, mgn = 8;
			float x, y;
			switch (DashPosition)
			{
				case TONebulaDashPosition.TopLeft: x = px + mgn; y = py + mgn; break;
				case TONebulaDashPosition.TopCenter: x = px + (pw - W) / 2; y = py + mgn; break;
				case TONebulaDashPosition.MiddleRight: x = px + pw - W - mgn; y = py + (ph - H) / 2; break;
				case TONebulaDashPosition.MiddleLeft: x = px + mgn; y = py + (ph - H) / 2; break;
				case TONebulaDashPosition.BottomRight: x = px + pw - W - mgn; y = py + ph - H - mgn; break;
				case TONebulaDashPosition.BottomCenter: x = px + (pw - W) / 2; y = py + ph - H - mgn; break;
				case TONebulaDashPosition.BottomLeft: x = px + mgn; y = py + ph - H - mgn; break;
				default: x = px + pw - W - mgn; y = py + mgn; break;
			}

			var panel = new DX.RectangleF(x, y, W, H);
			RenderTarget.FillRectangle(panel, Dx(dashBg));
			float ry = y + 1;
			foreach (var r in rows)
			{
				var rowRect = new DX.RectangleF(x, ry, W, rowH);
				if (r.Head) RenderTarget.FillRectangle(rowRect, Dx(headBg));
				float ty = ry + (rowH - fs * 1.33f) / 2;
				Text(r.Key, r.Head ? fb : f, x + pad, ty, r.Head ? fg : keyCol);
				var vs = Measure(r.Val, f);
				float vx = x + W - pad - vs.X;
				Text(r.Val, f, vx, ty, r.Head ? keyCol : r.ValCol);
				if (r.Pct >= 0)
				{
					int full = (int)Math.Round(r.Pct / 100.0 * DashBarBlocks);
					float bx = vx - 6 - barW;
					float by = ry + (rowH - blockH) / 2;
					for (int b = 0; b < DashBarBlocks; b++)
					{
						var br = new DX.RectangleF(bx + b * (blockW + blockGap), by, blockW, blockH);
						RenderTarget.FillRectangle(br, Dx(b < full ? r.BarCol : T(fg, 85)));
					}
				}
				if (!string.IsNullOrEmpty(r.Tip)) hoverItems.Add(new HoverItem { Rect = rowRect, Tip = r.Tip });
				ry += rowH;
			}
			RenderTarget.DrawRectangle(panel, Dx(T(fg, 80)), 1f);
		}

		private void RenderTooltip(ChartControl cc)
		{
			if (string.IsNullOrEmpty(hoverTip)) return;
			var f = Fmt("Segoe UI", 11.5f, false);
			var sz = Measure(hoverTip, f);
			float pad = 7;
			float w = sz.X + pad * 2, h = sz.Y + pad * 2;
			float x = hoverPt.X + 14, y = hoverPt.Y + 14;
			if (x + w > ChartPanel.X + ChartPanel.W) x = hoverPt.X - w - 10;
			if (y + h > ChartPanel.Y + ChartPanel.H) y = hoverPt.Y - h - 10;
			var rect = new DX.RectangleF(x, y, w, h);
			Color bg = ChartBg(), fg = ChartFg();
			RenderTarget.FillRoundedRectangle(new D2D.RoundedRectangle { Rect = rect, RadiusX = 4, RadiusY = 4 }, Dx(T(bg, 4)));
			RenderTarget.DrawRoundedRectangle(new D2D.RoundedRectangle { Rect = rect, RadiusX = 4, RadiusY = 4 }, Dx(T(fg, 60)), 1f);
			Text(hoverTip, f, x + pad, y + pad, fg);
		}

		private void OnPanelMouseMove(object sender, MouseEventArgs e)
		{
			if (ChartControl == null || ChartPanel == null) return;
			Point p = e.GetPosition(ChartPanel);
			float mx = (float)ChartingExtensions.ConvertToHorizontalPixels(p.X, ChartControl.PresentationSource) + ChartPanel.X;
			float my = (float)ChartingExtensions.ConvertToVerticalPixels(p.Y, ChartControl.PresentationSource) + ChartPanel.Y;
			string tip = null;
			lock (sync)
			{
				for (int i = hoverItems.Count - 1; i >= 0; i--)
				{
					var r = hoverItems[i].Rect;
					if (mx >= r.Left - 2 && mx <= r.Right + 2 && my >= r.Top - 2 && my <= r.Bottom + 2) { tip = hoverItems[i].Tip; break; }
				}
			}
			bool changed = tip != hoverTip || (tip != null && (Math.Abs(mx - hoverPt.X) > 3 || Math.Abs(my - hoverPt.Y) > 3));
			hoverPt = new DX.Vector2(mx, my);
			if (changed) { hoverTip = tip; ChartControl.InvalidateVisual(); }
		}

		private void OnPanelMouseLeave(object sender, MouseEventArgs e)
		{
			if (hoverTip != null) { hoverTip = null; if (ChartControl != null) ChartControl.InvalidateVisual(); }
		}
		#endregion

		#region Properties
		// ---- 01. Visible Settings ----
		[Display(Name = "Candle coloring", Order = 1, GroupName = "01. Visible Settings")]
		public TONebulaCandleMode CandleMode { get; set; }

		[Display(Name = "Color theme (candles, signals, levels)", Order = 2, GroupName = "01. Visible Settings")]
		public TONebulaTheme ColorTheme { get; set; }

		[Display(Name = "Cloud type", Order = 3, GroupName = "01. Visible Settings")]
		public TONebulaCloudType CloudType { get; set; }

		[Display(Name = "Cloud colors (cloud only)", Order = 4, GroupName = "01. Visible Settings")]
		public TONebulaCloudPalette CloudPalette { get; set; }

		[Display(Name = "Show dashboard", Order = 5, GroupName = "01. Visible Settings")]
		public bool ShowDashboard { get; set; }

		[Display(Name = "Show squeeze zones", Order = 6, GroupName = "01. Visible Settings")]
		public bool ShowSqueezeZones { get; set; }

		[Display(Name = "Show RSI divergences", Order = 7, GroupName = "01. Visible Settings")]
		public bool ShowDivergences { get; set; }

		[Display(Name = "Show session levels", Order = 8, GroupName = "01. Visible Settings")]
		public bool ShowSessionLevels { get; set; }

		[Display(Name = "Show HEMA line", Order = 9, GroupName = "01. Visible Settings")]
		public bool ShowHema { get; set; }

		[Display(Name = "Show plus sign to add", Order = 10, GroupName = "01. Visible Settings")]
		public bool ShowPlus { get; set; }

		[Display(Name = "Show bigger plus sign (Vodka Shot)", Order = 11, GroupName = "01. Visible Settings")]
		public bool ShowBigPlus { get; set; }

		[Display(Name = "Show 9/21 EMA cross", Order = 12, GroupName = "01. Visible Settings")]
		public bool Show921 { get; set; }

		[Display(Name = "Show volume imbalances", Order = 13, GroupName = "01. Visible Settings")]
		public bool ShowVolumeImbalances { get; set; }

		[Display(Name = "Draw behind bars", Description = "Cloud, zones and markers render under the candles. Turn off if you want the dashboard drawn over candles.", Order = 14, GroupName = "01. Visible Settings")]
		public bool DrawBehindBars { get; set; }

		// ---- 02. Dashboard ----
		[Display(Name = "Position", Order = 1, GroupName = "02. Dashboard")]
		public TONebulaDashPosition DashPosition { get; set; }

		[Display(Name = "Text size", Order = 2, GroupName = "02. Dashboard")]
		public TONebulaTextSize DashTextSize { get; set; }

		[Display(Name = "Show session levels in dashboard", Order = 3, GroupName = "02. Dashboard")]
		public bool DashShowLevels { get; set; }

		[Range(0, 100)]
		[Display(Name = "Background transparency", Order = 4, GroupName = "02. Dashboard")]
		public int DashTransparency { get; set; }

		[Range(5, 20)]
		[Display(Name = "Bar length (blocks)", Order = 5, GroupName = "02. Dashboard")]
		public int DashBarBlocks { get; set; }

		// ---- 03. Squeeze Zones ----
		[XmlIgnore]
		[Display(Name = "Squeeze zone color", Order = 1, GroupName = "03. Squeeze Zones")]
		public Brush SqueezeZoneBrush { get; set; }

		[Browsable(false)]
		public string SqueezeZoneBrushSerializable
		{
			get { return Serialize.BrushToString(SqueezeZoneBrush); }
			set { SqueezeZoneBrush = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name = "Squeeze zone transparency", Order = 2, GroupName = "03. Squeeze Zones")]
		public int SqueezeZoneTransparency { get; set; }

		[Range(1, int.MaxValue)]
		[Display(Name = "Minimum squeeze length (bars)", Order = 3, GroupName = "03. Squeeze Zones")]
		public int SqueezeMinLength { get; set; }

		[Display(Name = "Show squeeze release arrows", Order = 4, GroupName = "03. Squeeze Zones")]
		public bool ShowSqueezeRelease { get; set; }

		// ---- 04. RSI Divergence ----
		[Display(Name = "Draw divergence lines on price", Order = 1, GroupName = "04. RSI Divergence")]
		public bool DrawDivLines { get; set; }

		[Range(0, 100)]
		[Display(Name = "Divergence transparency", Order = 2, GroupName = "04. RSI Divergence")]
		public int DivTransparency { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RSI length", Order = 3, GroupName = "04. RSI Divergence")]
		public int DivRsiLength { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Pivot lookback left", Order = 4, GroupName = "04. RSI Divergence")]
		public int PivotLeft { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Pivot lookback right", Order = 5, GroupName = "04. RSI Divergence")]
		public int PivotRight { get; set; }

		[Range(1, int.MaxValue)]
		[Display(Name = "Min bars between pivots", Order = 6, GroupName = "04. RSI Divergence")]
		public int DivRangeMin { get; set; }

		[Range(2, int.MaxValue)]
		[Display(Name = "Max bars between pivots", Order = 7, GroupName = "04. RSI Divergence")]
		public int DivRangeMax { get; set; }

		// ---- 05. Session Levels (New York time) ----
		[Display(Name = "Opening range", Order = 1, GroupName = "05. Session Levels (NY time)")]
		public bool LevelOR { get; set; }

		[Display(Name = "Opening range session", Description = "HHmm-HHmm, New York time", Order = 2, GroupName = "05. Session Levels (NY time)")]
		public string SessionOR { get; set; }

		[Display(Name = "London", Order = 3, GroupName = "05. Session Levels (NY time)")]
		public bool LevelLondon { get; set; }

		[Display(Name = "London session", Order = 4, GroupName = "05. Session Levels (NY time)")]
		public string SessionLondon { get; set; }

		[Display(Name = "Asia", Order = 5, GroupName = "05. Session Levels (NY time)")]
		public bool LevelAsia { get; set; }

		[Display(Name = "Asia session", Order = 6, GroupName = "05. Session Levels (NY time)")]
		public string SessionAsia { get; set; }

		[Display(Name = "Yesterday's close", Order = 7, GroupName = "05. Session Levels (NY time)")]
		public bool LevelYC { get; set; }

		[Display(Name = "Regular session", Description = "Its last close is used as yesterday's close", Order = 8, GroupName = "05. Session Levels (NY time)")]
		public string SessionRTH { get; set; }

		[Display(Name = "Opening range extensions (1.5× / 2×)", Order = 9, GroupName = "05. Session Levels (NY time)")]
		public bool LevelExtensions { get; set; }

		[Display(Name = "Shade ranges while they form", Order = 10, GroupName = "05. Session Levels (NY time)")]
		public bool LevelShadeForming { get; set; }

		[Display(Name = "Mark opening range breakout", Order = 11, GroupName = "05. Session Levels (NY time)")]
		public bool LevelMarkBreakout { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "Merge tags within (ticks)", Order = 12, GroupName = "05. Session Levels (NY time)")]
		public int LevelMergeTicks { get; set; }

		[Range(1, 50)]
		[Display(Name = "Tag distance from last bar", Order = 13, GroupName = "05. Session Levels (NY time)")]
		public int LevelTagOffset { get; set; }

		[Range(0, 100)]
		[Display(Name = "Level transparency", Order = 14, GroupName = "05. Session Levels (NY time)")]
		public int LevelTransparency { get; set; }

		// ---- 06. Tidal Wave ----
		[Range(10, 500)]
		[Display(Name = "Imbalance line length (bars)", Order = 1, GroupName = "06. Tidal Wave")]
		public int ImbalanceExtend { get; set; }

		[Range(1, 10)]
		[Display(Name = "Imbalance line width", Order = 2, GroupName = "06. Tidal Wave")]
		public int ImbalanceWidth { get; set; }

		[Display(Name = "Imbalance line style", Order = 3, GroupName = "06. Tidal Wave")]
		public TONebulaLineStyle ImbalanceStyle { get; set; }

		// ---- 07. Advanced ----
		[NinjaScriptProperty]
		[Range(2, 50)]
		[Display(Name = "Minimum signals for take partial profit", Order = 1, GroupName = "07. Advanced")]
		public int TakePartialThreshold { get; set; }

		[NinjaScriptProperty]
		[Range(3, 50)]
		[Display(Name = "Minimum signals for take ALL profit", Order = 2, GroupName = "07. Advanced")]
		public int TakeAllThreshold { get; set; }

		[Display(Name = "Use quadratic equation for 9/21 cross", Order = 3, GroupName = "07. Advanced")]
		public bool UseQuadratic { get; set; }

		[Range(0, 100)]
		[Display(Name = "Simple cloud transparency", Order = 4, GroupName = "07. Advanced")]
		public int SimpleCloudTransparency { get; set; }

		[Range(0, 100)]
		[Display(Name = "Weak trend transparency", Order = 5, GroupName = "07. Advanced")]
		public int WeakTrendTransparency { get; set; }

		[Range(0, 100)]
		[Display(Name = "Strong trend transparency", Order = 6, GroupName = "07. Advanced")]
		public int StrongTrendTransparency { get; set; }

		// ---- 08. WAE ----
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Sensitivity", Order = 1, GroupName = "08. WAE (Waddah candles)")]
		public int WaeSensitivity { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Fast EMA length", Order = 2, GroupName = "08. WAE (Waddah candles)")]
		public int WaeFastLength { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Slow EMA length", Order = 3, GroupName = "08. WAE (Waddah candles)")]
		public int WaeSlowLength { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "BB channel length", Order = 4, GroupName = "08. WAE (Waddah candles)")]
		public int WaeChannelLength { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "BB stdev multiplier", Order = 5, GroupName = "08. WAE (Waddah candles)")]
		public double WaeBBMult { get; set; }

		// ---- 09. Relaxing ----
		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Squeeze tolerance (lower = more sensitive)", Order = 1, GroupName = "09. Relaxing Settings")]
		public int SqueezeTolerance { get; set; }

		[Range(0, int.MaxValue)]
		[Display(Name = "ADX threshold for TTM squeeze (no effect, as in original)", Order = 2, GroupName = "09. Relaxing Settings")]
		public int AdxSqueezeThreshold { get; set; }

		// ---- 10. Shark ----
		[NinjaScriptProperty]
		[Display(Name = "Apply 25/75 RSI rule", Order = 1, GroupName = "10. Shark Settings")]
		public bool ApplyRsi2575 { get; set; }

		// ---- 11. Alerts ----
		[Display(Name = "Enable 'any Nebula signal' alert", Order = 1, GroupName = "11. Alerts")]
		public bool EnableAlerts { get; set; }

		[Display(Name = "Alert sound file", Description = "File in NinjaTrader's sounds folder", Order = 2, GroupName = "11. Alerts")]
		public string AlertSound { get; set; }

		// ---- outputs for strategies / Market Analyzer ----
		[Browsable(false), XmlIgnore] public Series<double> Hema { get { return Values[0]; } }
		/// <summary>+2 strong buy, +1 buy, -1 sell, -2 strong sell, 0 none</summary>
		[Browsable(false), XmlIgnore] public Series<double> Signal { get { Update(); return oSignal; } }
		/// <summary>+2 Vodka Shot long, +1 plus long, -1 plus short, -2 Vodka Shot short</summary>
		[Browsable(false), XmlIgnore] public Series<double> AddSignal { get { Update(); return oAdd; } }
		/// <summary>2 take all profit, 1 take partial profit, 0 none</summary>
		[Browsable(false), XmlIgnore] public Series<double> TakeProfit { get { Update(); return oTP; } }
		/// <summary>1 up-wave, -1 down-wave, 0 none yet</summary>
		[Browsable(false), XmlIgnore] public Series<double> Wave { get { Update(); return oWave; } }
		/// <summary>take-profit confluence score</summary>
		[Browsable(false), XmlIgnore] public Series<double> Confluence { get { Update(); return oConf; } }
		/// <summary>1 cloud up, -1 cloud down</summary>
		[Browsable(false), XmlIgnore] public Series<double> CloudTrend { get { Update(); return oCloud; } }

		// ---- automation plots ----
		/// <summary>Any Tidal Wave buy/sell (basic or strong): +1 buy, -1 sell</summary>
		[Browsable(false), XmlIgnore] public Series<double> SigBuySell { get { return Values[1]; } }
		/// <summary>Strong Buy/Sell: +1 / -1</summary>
		[Browsable(false), XmlIgnore] public Series<double> SigStrong { get { return Values[2]; } }
		/// <summary>Basic Buy/Sell (no recent Ultimate signal): +1 / -1</summary>
		[Browsable(false), XmlIgnore] public Series<double> SigBasic { get { return Values[3]; } }
		/// <summary>Ultimate Buy/Sell engine: +1 / -1</summary>
		[Browsable(false), XmlIgnore] public Series<double> SigUltimate { get { return Values[4]; } }
		/// <summary>Add (+) volume imbalance: +1 long, -1 short</summary>
		[Browsable(false), XmlIgnore] public Series<double> SigAddLight { get { return Values[5]; } }
		/// <summary>Add (Vodka Shot): +1 long, -1 short</summary>
		[Browsable(false), XmlIgnore] public Series<double> SigAddStrong { get { return Values[6]; } }
		/// <summary>Take ALL profit: -1 in an up-wave (top risk), +1 in a down-wave (bottom risk)</summary>
		[Browsable(false), XmlIgnore] public Series<double> SigTakeProfitAll { get { return Values[7]; } }
		/// <summary>Take partial profit (below the ALL threshold): -1 up-wave, +1 down-wave</summary>
		[Browsable(false), XmlIgnore] public Series<double> SigTakeProfitPartial { get { return Values[8]; } }
		/// <summary>RSI divergence, on the confirmation bar (PivotRight bars after the swing): +1 bull, -1 bear</summary>
		[Browsable(false), XmlIgnore] public Series<double> SigDivergence { get { return Values[9]; } }
		/// <summary>9/21 EMA (or kernel) cross: +1 up, -1 down</summary>
		[Browsable(false), XmlIgnore] public Series<double> SigEmaCross { get { return Values[10]; } }
		/// <summary>Opening range breakout bar: +1 up, -1 down</summary>
		[Browsable(false), XmlIgnore] public Series<double> SigORBreak { get { return Values[11]; } }
		/// <summary>Squeeze ended (min length met): +1 momentum up, -1 down</summary>
		[Browsable(false), XmlIgnore] public Series<double> SigSqueezeRelease { get { return Values[12]; } }
		/// <summary>Tidal Wave state: +1 up, -1 down, 0 none yet</summary>
		[Browsable(false), XmlIgnore] public Series<double> StateWave { get { return Values[13]; } }
		/// <summary>Cloud trend: +1 up, -1 down</summary>
		[Browsable(false), XmlIgnore] public Series<double> StateCloud { get { return Values[14]; } }
		/// <summary>Take-profit confluence score</summary>
		[Browsable(false), XmlIgnore] public Series<double> StateConfluence { get { return Values[15]; } }
		/// <summary>Squeeze on: 1, off: 0</summary>
		[Browsable(false), XmlIgnore] public Series<double> StateSqueeze { get { return Values[16]; } }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private TraderOracle.TONebula[] cacheTONebula;
		public TraderOracle.TONebula TONebula(int divRsiLength, int pivotLeft, int pivotRight, int takePartialThreshold, int takeAllThreshold, int waeSensitivity, int waeFastLength, int waeSlowLength, int waeChannelLength, double waeBBMult, int squeezeTolerance, bool applyRsi2575)
		{
			return TONebula(Input, divRsiLength, pivotLeft, pivotRight, takePartialThreshold, takeAllThreshold, waeSensitivity, waeFastLength, waeSlowLength, waeChannelLength, waeBBMult, squeezeTolerance, applyRsi2575);
		}

		public TraderOracle.TONebula TONebula(ISeries<double> input, int divRsiLength, int pivotLeft, int pivotRight, int takePartialThreshold, int takeAllThreshold, int waeSensitivity, int waeFastLength, int waeSlowLength, int waeChannelLength, double waeBBMult, int squeezeTolerance, bool applyRsi2575)
		{
			if (cacheTONebula != null)
				for (int idx = 0; idx < cacheTONebula.Length; idx++)
					if (cacheTONebula[idx] != null && cacheTONebula[idx].DivRsiLength == divRsiLength && cacheTONebula[idx].PivotLeft == pivotLeft && cacheTONebula[idx].PivotRight == pivotRight && cacheTONebula[idx].TakePartialThreshold == takePartialThreshold && cacheTONebula[idx].TakeAllThreshold == takeAllThreshold && cacheTONebula[idx].WaeSensitivity == waeSensitivity && cacheTONebula[idx].WaeFastLength == waeFastLength && cacheTONebula[idx].WaeSlowLength == waeSlowLength && cacheTONebula[idx].WaeChannelLength == waeChannelLength && cacheTONebula[idx].WaeBBMult == waeBBMult && cacheTONebula[idx].SqueezeTolerance == squeezeTolerance && cacheTONebula[idx].ApplyRsi2575 == applyRsi2575 && cacheTONebula[idx].EqualsInput(input))
						return cacheTONebula[idx];
			return CacheIndicator<TraderOracle.TONebula>(new TraderOracle.TONebula(){ DivRsiLength = divRsiLength, PivotLeft = pivotLeft, PivotRight = pivotRight, TakePartialThreshold = takePartialThreshold, TakeAllThreshold = takeAllThreshold, WaeSensitivity = waeSensitivity, WaeFastLength = waeFastLength, WaeSlowLength = waeSlowLength, WaeChannelLength = waeChannelLength, WaeBBMult = waeBBMult, SqueezeTolerance = squeezeTolerance, ApplyRsi2575 = applyRsi2575 }, input, ref cacheTONebula);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.TraderOracle.TONebula TONebula(int divRsiLength, int pivotLeft, int pivotRight, int takePartialThreshold, int takeAllThreshold, int waeSensitivity, int waeFastLength, int waeSlowLength, int waeChannelLength, double waeBBMult, int squeezeTolerance, bool applyRsi2575)
		{
			return indicator.TONebula(Input, divRsiLength, pivotLeft, pivotRight, takePartialThreshold, takeAllThreshold, waeSensitivity, waeFastLength, waeSlowLength, waeChannelLength, waeBBMult, squeezeTolerance, applyRsi2575);
		}

		public Indicators.TraderOracle.TONebula TONebula(ISeries<double> input , int divRsiLength, int pivotLeft, int pivotRight, int takePartialThreshold, int takeAllThreshold, int waeSensitivity, int waeFastLength, int waeSlowLength, int waeChannelLength, double waeBBMult, int squeezeTolerance, bool applyRsi2575)
		{
			return indicator.TONebula(input, divRsiLength, pivotLeft, pivotRight, takePartialThreshold, takeAllThreshold, waeSensitivity, waeFastLength, waeSlowLength, waeChannelLength, waeBBMult, squeezeTolerance, applyRsi2575);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.TraderOracle.TONebula TONebula(int divRsiLength, int pivotLeft, int pivotRight, int takePartialThreshold, int takeAllThreshold, int waeSensitivity, int waeFastLength, int waeSlowLength, int waeChannelLength, double waeBBMult, int squeezeTolerance, bool applyRsi2575)
		{
			return indicator.TONebula(Input, divRsiLength, pivotLeft, pivotRight, takePartialThreshold, takeAllThreshold, waeSensitivity, waeFastLength, waeSlowLength, waeChannelLength, waeBBMult, squeezeTolerance, applyRsi2575);
		}

		public Indicators.TraderOracle.TONebula TONebula(ISeries<double> input , int divRsiLength, int pivotLeft, int pivotRight, int takePartialThreshold, int takeAllThreshold, int waeSensitivity, int waeFastLength, int waeSlowLength, int waeChannelLength, double waeBBMult, int squeezeTolerance, bool applyRsi2575)
		{
			return indicator.TONebula(input, divRsiLength, pivotLeft, pivotRight, takePartialThreshold, takeAllThreshold, waeSensitivity, waeFastLength, waeSlowLength, waeChannelLength, waeBBMult, squeezeTolerance, applyRsi2575);
		}
	}
}

#endregion
