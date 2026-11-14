// SneakyExportAll: dumps everything the OTHER indicators on this chart plot or draw, plus the bars, to CSV files so signals can be backtested offline.
// It does not touch or modify the other indicators; it only reads their public plot values and drawing objects.
// Output folder: Documents\NinjaTrader 8\SneakyExport\  (bars.csv, plots_<indicator>.csv, draws.csv, export_info.txt)
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	public class SneakyExportAll : Indicator
	{
		private bool dumped; private int realtimeBars; private bool errShown; private string dir;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Exports bars, every other indicator's plot values and its drawing objects (arrows, text, lines) on this chart to CSV for offline backtesting.";
				Name = "SneakyExportAll"; Calculate = Calculate.OnBarClose; IsOverlay = true; DisplayInDataBox = false; PaintPriceMarkers = false;
				IsSuspendedWhileInactive = false; IsAutoScale = false; DrawOnPricePanel = true;
				RefreshEveryBars = 30;
			}
			else if (State == State.DataLoaded)
			{
				dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SneakyExport"); dumped = false; realtimeBars = 0;
			}
		}

		protected override void OnBarUpdate()
		{
			if (State != State.Realtime || RefreshEveryBars <= 0) return;
			realtimeBars++; if (realtimeBars % RefreshEveryBars == 0) dumped = false;   // re-dump at next render
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (dumped || ChartControl == null || Bars == null || Bars.Count < 10) return;
			if (!(State == State.Realtime || CurrentBar >= Bars.Count - 2)) return;
			dumped = true;
			try { Dump(); Print("SneakyExportAll: wrote CSV files to " + dir); }
			catch (Exception ex) { if (!errShown) { errShown = true; Print("SneakyExportAll error: " + ex); } }
		}

		private static string F(double v) { return double.IsNaN(v) ? "" : v.ToString("0.#####", CultureInfo.InvariantCulture); }
		private static string Clean(string s) { return (s ?? "").Replace(",", ";").Replace("\r", " ").Replace("\n", " "); }
		private static string Safe(string s) { foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }

		private void Dump()
		{
			Directory.CreateDirectory(dir); int n = Bars.Count; var sb = new StringBuilder();
			var info = new StringBuilder();
			info.AppendLine("Instrument," + Instrument.FullName); info.AppendLine("BarsPeriod," + BarsPeriod.Value + " " + BarsPeriod.BarsPeriodType); info.AppendLine("Bars," + n);
			info.AppendLine("First," + Bars.GetTime(0).ToString("yyyy-MM-dd HH:mm:ss")); info.AppendLine("Last," + Bars.GetTime(n - 1).ToString("yyyy-MM-dd HH:mm:ss")); info.AppendLine("Exported," + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
			sb.AppendLine("time,open,high,low,close,volume");
			for (int i = 0; i < n; i++) sb.AppendLine(Bars.GetTime(i).ToString("yyyy-MM-dd HH:mm:ss") + "," + F(Bars.GetOpen(i)) + "," + F(Bars.GetHigh(i)) + "," + F(Bars.GetLow(i)) + "," + F(Bars.GetClose(i)) + "," + F(Bars.GetVolume(i)));
			File.WriteAllText(Path.Combine(dir, "bars.csv"), sb.ToString());
			var draws = new StringBuilder(); draws.AppendLine("indicator,type,tag,time,price,text,time2,price2");
			int idx = 0;
			foreach (var ind in ChartControl.Indicators)
			{
				if (ind == null || ReferenceEquals(ind, this)) continue; idx++;
				string nm = Safe(ind.Name ?? ind.GetType().Name) + "_" + idx;
				info.AppendLine("Indicator," + nm + "," + ind.GetType().FullName);
				try
				{
					int pc = ind.Values != null ? ind.Values.Length : 0; var ps = new StringBuilder(); var hdr = new StringBuilder("time");
					for (int p = 0; p < pc; p++) { string pn = (ind.Plots != null && p < ind.Plots.Length) ? ind.Plots[p].Name : "plot" + p; hdr.Append("," + Clean(pn)); }
					ps.AppendLine(hdr.ToString()); int rows = 0;
					for (int i = 0; i < n; i++)
					{
						var line = new StringBuilder(Bars.GetTime(i).ToString("yyyy-MM-dd HH:mm:ss")); bool any = false;
						for (int p = 0; p < pc; p++)
						{
							double v = double.NaN;
							try { if (ind.Values[p].IsValidDataPointAt(i)) v = ind.Values[p].GetValueAt(i); } catch { }
							if (!double.IsNaN(v) && v != 0) any = true; line.Append("," + F(v));
						}
						ps.AppendLine(line.ToString()); rows++;
					}
					File.WriteAllText(Path.Combine(dir, "plots_" + nm + ".csv"), ps.ToString()); info.AppendLine("  plots," + pc + ",rows," + rows);
				}
				catch (Exception ex) { info.AppendLine("  plots error: " + ex.Message); }
				try
				{
					int cnt = 0;
					foreach (var d in ind.DrawObjects)
					{
						if (d == null) continue; Type t = d.GetType(); string tag = Str(t, d, "Tag"); string text = Str(t, d, "DisplayText"); if (text == "") text = Str(t, d, "Text");
						object a1 = Prop(t, d, "Anchor") ?? Prop(t, d, "StartAnchor"); object a2 = Prop(t, d, "EndAnchor");
						string tm = "", pr = "", tm2 = "", pr2 = ""; Anch(a1, out tm, out pr); Anch(a2, out tm2, out pr2);
						draws.AppendLine(Clean(nm) + "," + t.Name + "," + Clean(tag) + "," + tm + "," + pr + "," + Clean(text) + "," + tm2 + "," + pr2); cnt++;
					}
					info.AppendLine("  drawObjects," + cnt);
				}
				catch (Exception ex) { info.AppendLine("  draw error: " + ex.Message); }
			}
			File.WriteAllText(Path.Combine(dir, "draws.csv"), draws.ToString()); File.WriteAllText(Path.Combine(dir, "export_info.txt"), info.ToString());
		}

		private static object Prop(Type t, object o, string name) { try { var p = t.GetProperty(name); return p != null ? p.GetValue(o, null) : null; } catch { return null; } }
		private static string Str(Type t, object o, string name) { var v = Prop(t, o, name); return v == null ? "" : v.ToString(); }
		private static void Anch(object a, out string time, out string price)
		{
			time = ""; price = ""; if (a == null) return; Type t = a.GetType();
			object tm = Prop(t, a, "Time"); object pr = Prop(t, a, "Price");
			if (tm is DateTime) time = ((DateTime)tm).ToString("yyyy-MM-dd HH:mm:ss"); if (pr is double) price = ((double)pr).ToString("0.#####", CultureInfo.InvariantCulture);
		}

		[Range(0, 1000)] [Display(Name = "Refresh export every N realtime bars (0 = once)", Order = 0, GroupName = "Export")] public int RefreshEveryBars { get; set; }
	}
}
