// SneakyCotScreener - NinjaTrader 8 port of the TradingView "COT Multi-Symbol Screener" (13 symbols, 2 intervals)
// COT index = 100 x (net - lowest net) / (highest net - lowest net) over N weekly reports, net = Commercial Long - Commercial Short (CFTC Legacy report).
// Data: downloaded from the CFTC public reporting site (needs internet), cached to Documents\NinjaTrader 8\SneakyCot\. Table draws on the chart (any instrument / timeframe).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.Indicators
{
internal class CotRow { public DateTime Date; public double Net; }
internal class CotRes { public double Idx1 = double.NaN, Idx2 = double.NaN; public int X1, X2; public DateTime Release; public bool HasRel; public bool Ok; public string Status = ""; }
internal static class CotCalc
{
	static readonly Dictionary<string, string> Map = new Dictionary<string, string>
	{
		{"6A","232741"},{"AUD","232741"},{"6B","096742"},{"GBP","096742"},{"6C","090741"},{"CAD","090741"},{"6E","099741"},{"EUR","099741"},{"6J","097741"},{"JPY","097741"},{"6L","102741"},{"BRL","102741"},{"6M","095741"},{"MXN","095741"},{"6N","112741"},{"NZD","112741"},{"6S","092741"},{"CHF","092741"},{"6Z","122741"},{"ZAR","122741"},{"DX","098662"},{"BTC","133741"},{"ETH","146021"},
		{"ES","13874A"},{"MES","13874A"},{"NQ","209742"},{"MNQ","209742"},{"YM","124603"},{"MYM","124603"},{"RTY","239742"},{"M2K","239742"},{"NKD","240741"},{"VX","1170E1"},
		{"CL","067651"},{"MCL","067651"},{"NG","023651"},{"HO","022651"},{"RB","111659"},
		{"GC","088691"},{"MGC","088691"},{"SI","084691"},{"SIL","084691"},{"HG","085692"},{"MHG","085692"},{"PL","076651"},{"PA","075651"},{"ALI","191691"},{"LTH","189691"},
		{"ZC","002602"},{"C","002602"},{"ZW","001602"},{"W","001602"},{"ZS","005602"},{"S","005602"},{"ZM","026603"},{"ZL","007601"},{"ZO","004603"},{"ZR","039601"},{"KE","001612"},{"RS","135731"},
		{"KC","083731"},{"SB","080732"},{"CC","073732"},{"CT","033661"},{"OJ","040701"},{"LBR","058644"},{"LBS","058644"},
		{"LE","057642"},{"GF","061641"},{"HE","054642"},{"DC","052641"},
		{"ZT","042601"},{"ZF","044601"},{"ZN","043602"},{"ZB","020601"},{"UB","020604"},{"SR3","134741"}
	};
	public static string RootToCode(string root) { string v; return Map.TryGetValue((root ?? "").Trim().ToUpperInvariant(), out v) ? v : ""; }
	public static string Norm(string c) { string t = (c ?? "").Trim().Trim('"').ToUpperInvariant().TrimStart('0'); return t; }
	// Parse "ROOT" or "ROOT:CODE" entries
	public static void ParseSymbols(string s, List<string> roots, List<string> codes)
	{
		roots.Clear(); codes.Clear(); if (string.IsNullOrEmpty(s)) return;
		foreach (string raw in s.Split(new[] { ',', ';', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries))
		{
			string t = raw.Trim(); if (t.Length == 0) continue; int k = t.IndexOf(':'); string root = k >= 0 ? t.Substring(0, k) : t; string code = k >= 0 ? t.Substring(k + 1).Trim() : "";
			roots.Add(root.ToUpperInvariant()); codes.Add(code != "" ? code.ToUpperInvariant() : RootToCode(root));
		}
	}
	static List<string> SplitCsv(string line)
	{
		var r = new List<string>(); var sb = new StringBuilder(); bool q = false;
		foreach (char ch in line) { if (ch == '"') q = !q; else if (ch == ',' && !q) { r.Add(sb.ToString()); sb.Clear(); } else sb.Append(ch); }
		r.Add(sb.ToString()); return r;
	}
	// returns code(normalised) -> list of rows (newest first); error text on failure
	public static Dictionary<string, List<CotRow>> ParseCsv(string text, out string err)
	{
		err = ""; var res = new Dictionary<string, List<CotRow>>(); if (string.IsNullOrWhiteSpace(text)) { err = "empty response"; return res; }
		string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); var h = SplitCsv(lines[0]); int iD = -1, iC = -1, iL = -1, iS = -1;
		for (int i = 0; i < h.Count; i++)
		{
			string n = h[i].Trim().Trim('"').ToLowerInvariant();
			if (n == "report_date_as_yyyy_mm_dd" || n == "as of date in form yyyy-mm-dd") iD = i; else if (n == "cftc_contract_market_code" || n == "cftc contract market code") iC = i;
			else if (n == "comm_positions_long_all" || n == "commercial positions-long (all)") iL = i; else if (n == "comm_positions_short_all" || n == "commercial positions-short (all)") iS = i;
		}
		if (iD < 0 || iC < 0 || iL < 0 || iS < 0) { err = "unexpected columns: " + lines[0].Substring(0, Math.Min(160, lines[0].Length)); return res; }
		for (int k = 1; k < lines.Length; k++)
		{
			var f = SplitCsv(lines[k]); if (f.Count <= Math.Max(Math.Max(iD, iC), Math.Max(iL, iS))) continue; DateTime d; double lo, sh;
			string ds = f[iD].Trim().Trim('"'); if (ds.Length >= 10) ds = ds.Substring(0, 10);
			if (!DateTime.TryParseExact(ds, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) continue;
			if (!double.TryParse(f[iL].Trim().Trim('"'), NumberStyles.Any, CultureInfo.InvariantCulture, out lo) || !double.TryParse(f[iS].Trim().Trim('"'), NumberStyles.Any, CultureInfo.InvariantCulture, out sh)) continue;
			string code = Norm(f[iC]); List<CotRow> l; if (!res.TryGetValue(code, out l)) { l = new List<CotRow>(); res[code] = l; } l.Add(new CotRow { Date = d, Net = lo - sh });
		}
		foreach (var kv in res) kv.Value.Sort((a, b) => b.Date.CompareTo(a.Date));
		if (res.Count == 0) err = "no rows parsed";
		return res;
	}
	// Release = Friday 15:30 New York of the report week (report date is Tuesday)
	public static DateTime ReleaseEt(DateTime reportDate) { int add = ((int)DayOfWeek.Friday - (int)reportDate.DayOfWeek + 7) % 7; return reportDate.Date.AddDays(add).AddHours(15).AddMinutes(30); }
	static double Index(List<CotRow> h, int off, int len)
	{
		if (off + len > h.Count) return double.NaN; double mn = double.MaxValue, mx = double.MinValue;
		for (int i = off; i < off + len; i++) { mn = Math.Min(mn, h[i].Net); mx = Math.Max(mx, h[i].Net); }
		return mx != mn ? 100.0 * (h[off].Net - mn) / (mx - mn) : double.NaN;
	}
	public static CotRes Calc(List<CotRow> h, DateTime nowEt, bool hideCurrent, int len1, int len2, double bear, double bull)
	{
		var r = new CotRes(); if (h == null || h.Count == 0) { r.Status = "COT data missing"; return r; }
		int off = hideCurrent && nowEt < ReleaseEt(h[0].Date) ? 1 : 0; if (off >= h.Count) { r.Status = "COT data missing"; return r; }
		r.Idx1 = Index(h, off, len1); r.Idx2 = Index(h, off, len2); double p1 = Index(h, off + 1, len1), p2 = Index(h, off + 1, len2);
		r.X1 = r.Idx1 > bull && p1 <= bull ? 1 : r.Idx1 < bear && p1 >= bear ? -1 : 0; r.X2 = r.Idx2 > bull && p2 <= bull ? 1 : r.Idx2 < bear && p2 >= bear ? -1 : 0;
		r.Release = ReleaseEt(h[off].Date); r.HasRel = true; r.Ok = !double.IsNaN(r.Idx1) && !double.IsNaN(r.Idx2); r.Status = r.Ok ? "OK" : "COT data missing"; return r;
	}
}

	public enum CotPos { TopRight, TopLeft, BottomRight, BottomLeft, MiddleRight, MiddleLeft, TopCenter, BottomCenter, MiddleCenter }

	public class SneakyCotScreener : Indicator
	{
		private Dictionary<string, List<CotRow>> data = new Dictionary<string, List<CotRow>>(); private readonly object lk = new object(); private string status = "Loading CFTC data..."; private DateTime lastFetch = DateTime.MinValue; private volatile bool fetching; private bool errShown;
		private static readonly TimeZoneInfo Et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "COT multi-symbol screener (CFTC Legacy commercial net positions, COT index over two lookbacks).";
				Name = "SneakyCotScreener"; Calculate = Calculate.OnBarClose; IsOverlay = true; DisplayInDataBox = false; PaintPriceMarkers = false; IsSuspendedWhileInactive = true; DrawOnPricePanel = true;
				Symbols = "CL,GC,SI,HO,KE,KC,NQ,NG,ES,ZW,ZC,ZS,LE"; IncludeOptions = false; HideCurrentWeek = false; Bearish = 25; Bullish = 75; Int1 = 26; Int2 = 52; Pos = CotPos.TopRight; FontSize = 12; RefreshHours = 3; LocalCsv = ""; DebugPrint = true;
			}
			else if (State == State.DataLoaded) StartFetch();
		}

		protected override void OnBarUpdate() { if (!fetching && (DateTime.Now - lastFetch).TotalHours >= RefreshHours && (CurrentBar >= Count - 2 || State == State.Realtime)) StartFetch(); }

		private string CacheFile() { string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SneakyCot"); try { Directory.CreateDirectory(dir); } catch { } return Path.Combine(dir, IncludeOptions ? "cot_legacy_combined.csv" : "cot_legacy_futures.csv"); }

		private void StartFetch()
		{
			if (fetching) return; fetching = true; lastFetch = DateTime.Now; bool opt = IncludeOptions; string sym = Symbols, local = LocalCsv; string cache = CacheFile();
			Task.Run(() =>
			{
				try
				{
					string text = null, err = ""; var roots = new List<string>(); var codes = new List<string>(); CotCalc.ParseSymbols(sym, roots, codes);
					if (!string.IsNullOrEmpty(local) && File.Exists(local)) text = File.ReadAllText(local);
					else
					{
						try
						{
							ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; var inList = new StringBuilder(); var seen = new HashSet<string>();
							foreach (string c in codes) if (c != "" && seen.Add(c)) { if (inList.Length > 0) inList.Append(","); inList.Append("'" + c + "'"); }
							string since = DateTime.UtcNow.AddDays(-7 * 120).ToString("yyyy-MM-dd"); string ds = opt ? "jun7-fc8e" : "6dca-aqww";
							string url = "https://publicreporting.cftc.gov/resource/" + ds + ".csv?$select=" + Uri.EscapeDataString("report_date_as_yyyy_mm_dd,cftc_contract_market_code,comm_positions_long_all,comm_positions_short_all") + "&$where=" + Uri.EscapeDataString("cftc_contract_market_code in(" + inList + ") AND report_date_as_yyyy_mm_dd >= '" + since + "T00:00:00.000'") + "&$order=" + Uri.EscapeDataString("report_date_as_yyyy_mm_dd DESC") + "&$limit=50000";
							var req = (HttpWebRequest)WebRequest.Create(url); req.Timeout = 30000; req.UserAgent = "Mozilla/5.0 NinjaTrader";
							using (var rs = req.GetResponse()) using (var sr = new StreamReader(rs.GetResponseStream())) text = sr.ReadToEnd();
							try { File.WriteAllText(cache, text); } catch { }
						}
						catch (Exception ex) { err = "download failed: " + ex.Message; if (File.Exists(cache)) { text = File.ReadAllText(cache); err += " (using cached file)"; } }
					}
					if (text == null) { lock (lk) status = err != "" ? err : "no data"; }
					else
					{
						string perr; var parsed = CotCalc.ParseCsv(text, out perr);
						lock (lk) { if (perr == "") { data = parsed; status = err; } else status = "parse error: " + perr; }
						if (perr == "" && DebugPrint) { foreach (var kv in parsed) { var sb2 = new StringBuilder("COT " + kv.Key + " (" + kv.Value.Count + " reports): "); for (int q = 0; q < Math.Min(5, kv.Value.Count); q++) sb2.Append(kv.Value[q].Date.ToString("MM-dd") + "=" + kv.Value[q].Net.ToString("0", CultureInfo.InvariantCulture) + "  "); Print(sb2.ToString()); } }
					}
				}
				catch (Exception ex) { lock (lk) status = "error: " + ex.Message; }
				finally { fetching = false; try { if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => ChartControl.InvalidateVisual()); } catch { } }
			});
		}

		private static SharpDX.Color4 K(byte r, byte g, byte b) { return new SharpDX.Color4(r / 255f, g / 255f, b / 255f, 1f); }

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale); if (RenderTarget == null || ChartPanel == null) return;
			var disp = new List<IDisposable>();
			try
			{
				var roots = new List<string>(); var codes = new List<string>(); CotCalc.ParseSymbols(Symbols, roots, codes); Dictionary<string, List<CotRow>> d; string st; lock (lk) { d = data; st = status; }
				DateTime nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Et); int n = roots.Count; float fs = FontSize; float rh = fs * 2.0f, pad = 9f;
				string[] hdr = { "Symbol", "Idx " + Int1 + " W", "Idx " + Int2 + " W", "Cross " + Int1 + " W", "Cross " + Int2 + " W", "Status", "Release date" };
				var cells = new string[n + 1, 7]; var bg = new byte[n + 1, 7][]; var fg = new byte[n + 1, 7][];
				byte[] BLACK = { 0, 0, 0 }, WHITE = { 255, 255, 255 }, SILVER = { 178, 181, 190 }, GREEN = { 76, 175, 80 }, RED = { 255, 50, 70 };
				for (int c = 0; c < 7; c++) { cells[0, c] = hdr[c]; bg[0, c] = BLACK; fg[0, c] = WHITE; }
				for (int i = 0; i < n; i++)
				{
					int r = i + 1; string code = codes[i]; CotRes res = null; if (code != "") { List<CotRow> h; if (d.TryGetValue(CotCalc.Norm(code), out h)) res = CotCalc.Calc(h, nowEt, HideCurrentWeek, Int1, Int2, Bearish, Bullish); else res = new CotRes { Status = "COT data missing" }; }
					double i1 = res == null ? double.NaN : res.Idx1, i2 = res == null ? double.NaN : res.Idx2;
					bool dbull = i1 > Bullish && i2 > Bullish, dbear = i1 < Bearish && i2 < Bearish;
					Func<double, byte[]> col = x => double.IsNaN(x) ? RED : x > Bullish ? GREEN : x < Bearish ? RED : SILVER;
					cells[r, 0] = roots[i]; bg[r, 0] = dbull ? GREEN : dbear ? RED : SILVER; cells[r, 1] = double.IsNaN(i1) ? "Error/NA" : i1.ToString("0", CultureInfo.InvariantCulture); bg[r, 1] = col(i1); cells[r, 2] = double.IsNaN(i2) ? "Error/NA" : i2.ToString("0", CultureInfo.InvariantCulture); bg[r, 2] = col(i2);
					cells[r, 3] = res == null ? "" : res.X1 > 0 ? "▲" : res.X1 < 0 ? "▼" : ""; cells[r, 4] = res == null ? "" : res.X2 > 0 ? "▲" : res.X2 < 0 ? "▼" : ""; bg[r, 3] = SILVER; bg[r, 4] = SILVER;
					bool bad = code == "" || res == null || !res.Ok; cells[r, 5] = code == "" ? "Invalid root" : res.Status; bg[r, 5] = bad ? RED : GREEN; cells[r, 6] = res != null && res.HasRel ? res.Release.ToString("yyyy-MM-dd") : "n/a"; bg[r, 6] = SILVER;
					for (int c = 0; c < 7; c++) fg[r, c] = (c == 3 || c == 4) ? BLACK : BLACK;
				}
				Func<bool, SharpDX.DirectWrite.TextFormat> F = bold => { var f = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", bold ? SharpDX.DirectWrite.FontWeight.Bold : SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, fs) { ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center, TextAlignment = SharpDX.DirectWrite.TextAlignment.Center }; disp.Add(f); return f; };
				var fb = F(true); float[] w = new float[7];
				for (int r = 0; r <= n; r++) for (int c = 0; c < 7; c++) { using (var tl = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, cells[r, c] ?? "", fb, 2000f, 100f)) w[c] = Math.Max(w[c], tl.Metrics.Width + 2 * pad); }
				float tw = 0; foreach (float x in w) tw += x; float th = (n + 1) * rh + (st != "" ? rh : 0); float pw = ChartPanel.W, ph = ChartPanel.H; int p = (int)Pos; int rowp = (p == 0 || p == 1 || p == 6) ? 0 : (p == 2 || p == 3 || p == 7) ? 2 : 1; int colp = (p == 0 || p == 2 || p == 4) ? 2 : (p == 1 || p == 3 || p == 5) ? 0 : 1;
				float x0 = colp == 0 ? 10f : colp == 1 ? (pw - tw) / 2f : pw - tw - 14f, y0 = rowp == 0 ? 46f : rowp == 1 ? (ph - th) / 2f : ph - th - 44f;
				for (int r = 0; r <= n; r++)
				{
					float x = x0, y = y0 + r * rh;
					for (int c = 0; c < 7; c++)
					{
						var b = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(bg[r, c][0], bg[r, c][1], bg[r, c][2])); disp.Add(b); RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, w[c], rh), b);
						var bd = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(30, 34, 45)); disp.Add(bd); RenderTarget.DrawRectangle(new SharpDX.RectangleF(x, y, w[c], rh), bd, 1f);
						var t = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(fg[r, c][0], fg[r, c][1], fg[r, c][2])); disp.Add(t); RenderTarget.DrawText(cells[r, c] ?? "", fb, new SharpDX.RectangleF(x, y, w[c], rh), t); x += w[c];
					}
				}
				if (st != "") { var nb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, K(255, 212, 121)); disp.Add(nb); var ff = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", fs * 0.8f) { ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center }; disp.Add(ff); RenderTarget.DrawText("CFTC: " + st, ff, new SharpDX.RectangleF(x0, y0 + (n + 1) * rh, Math.Max(tw, 300f), rh), nb); }
			}
			catch (Exception ex) { if (!errShown) { errShown = true; Print("SneakyCotScreener OnRender error: " + ex); } }
			finally { foreach (var d2 in disp) d2.Dispose(); }
		}

		#region Properties
		[NinjaScriptProperty] [Display(Name = "Symbols (ROOT or ROOT:CFTCcode, comma separated)", Order = 0, GroupName = "1. Symbols")] public string Symbols { get; set; }
		[NinjaScriptProperty] [Display(Name = "Include options (futures + options report)", Order = 1, GroupName = "1. Symbols")] public bool IncludeOptions { get; set; }
		[NinjaScriptProperty] [Display(Name = "Hide current week (use report only after Friday 15:30 ET)", Order = 2, GroupName = "1. Symbols")] public bool HideCurrentWeek { get; set; }
		[NinjaScriptProperty] [Range(0, 100)] [Display(Name = "Bearish threshold", Order = 0, GroupName = "2. Thresholds")] public int Bearish { get; set; }
		[NinjaScriptProperty] [Range(0, 100)] [Display(Name = "Bullish threshold", Order = 1, GroupName = "2. Thresholds")] public int Bullish { get; set; }
		[NinjaScriptProperty] [Range(2, 104)] [Display(Name = "Interval 1 (weeks)", Order = 2, GroupName = "2. Thresholds")] public int Int1 { get; set; }
		[NinjaScriptProperty] [Range(2, 104)] [Display(Name = "Interval 2 (weeks)", Order = 3, GroupName = "2. Thresholds")] public int Int2 { get; set; }
		[NinjaScriptProperty] [Display(Name = "Table position", Order = 0, GroupName = "3. Table")] public CotPos Pos { get; set; }
		[NinjaScriptProperty] [Range(8, 28)] [Display(Name = "Font size", Order = 1, GroupName = "3. Table")] public int FontSize { get; set; }
		[NinjaScriptProperty] [Range(1, 48)] [Display(Name = "Re-download every N hours", Order = 0, GroupName = "4. Data")] public int RefreshHours { get; set; }
		[NinjaScriptProperty] [Display(Name = "Local CSV file (optional, overrides download)", Order = 1, GroupName = "4. Data")] public string LocalCsv { get; set; }
		[NinjaScriptProperty] [Display(Name = "Print last 5 nets to Output window", Order = 2, GroupName = "4. Data")] public bool DebugPrint { get; set; }
		#endregion
	}
}
