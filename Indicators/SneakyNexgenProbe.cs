#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
#endregion

// SneakyNexgenProbe
// Passive recorder. Put it on the SAME chart as the Nexgen indicators (add it LAST so they have calculated first).
// For every bar it writes the value of every plot of every matching indicator already on the chart, plus the bar itself,
// to CSV files in <NinjaTrader user data>\SneakyExport. It also writes each matching indicator's current settings once.
// It only reads values the indicators already publish; it never touches any indicator's code and draws nothing.
//
//   nexgen_probe_<instrument>_<period>_plots.csv     time,bar,indicator,plot,value      (long format, one row per valid plot value)
//   nexgen_probe_<instrument>_<period>_bars.csv      time,bar,open,high,low,close,volume
//   nexgen_probe_<instrument>_<period>_settings.csv  indicator,property,value
namespace NinjaTrader.NinjaScript.Indicators
{
    public class SneakyNexgenProbe : Indicator
    {
        [Display(Name = "Indicator name contains (comma list)", GroupName = "1 Sources", Order = 0, Description = "An indicator on this chart is recorded if its name, display name or type name contains any of these words.")]
        public string NameFilter { get; set; }
        [Display(Name = "Record ALL other indicators", GroupName = "1 Sources", Order = 1, Description = "Ignore the filter and record every other indicator on the chart.")]
        public bool RecordAll { get; set; }
        [Range(100, 2000000)]
        [Display(Name = "Max bars to record", GroupName = "2 Output", Order = 0, Description = "Only the most recent N bars are written (older historical bars are skipped).")]
        public int MaxBars { get; set; }
        [Display(Name = "Print what it finds to NinjaScript Output", GroupName = "2 Output", Order = 1)]
        public bool Verbose { get; set; }

        private List<IndicatorBase> src = new List<IndicatorBase>();
        private StreamWriter plotsW, barsW, setW;
        private bool opened, settingsDone, listed;
        private int lastCount = -1, lastRescan = -1, rows;
        private string stem;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Sneaky Nexgen Probe";
                Description = "Records the plot values of the Nexgen indicators on this chart to CSV, for comparing against independent code.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true; IsAutoScale = false; DisplayInDataBox = false; PaintPriceMarkers = false; IsSuspendedWhileInactive = false;
                NameFilter = "T3,Nexgen,MadMan,SYNTH,Fib,Dvg,MarketFlow,NextRange,Macd";
                RecordAll = false; MaxBars = 30000; Verbose = true;
            }
            else if (State == State.Terminated)
            {
                try { if (plotsW != null) plotsW.Flush(); } catch { }
                try { if (barsW != null) barsW.Flush(); } catch { }
                try { if (setW != null) setW.Flush(); } catch { }
                try { if (plotsW != null) plotsW.Dispose(); if (barsW != null) barsW.Dispose(); if (setW != null) setW.Dispose(); } catch { }
                plotsW = barsW = setW = null;
            }
        }

        private static string Norm(string t) { return (t ?? "").Replace(" ", "").Replace("_", "").ToLowerInvariant(); }

        private bool Matches(IndicatorBase q)
        {
            if (q == null || ReferenceEquals(q, this)) return false;
            if (q is SneakyNexgenProbe) return false;
            if (RecordAll) return true;
            if (string.IsNullOrEmpty(NameFilter)) return false;
            string a = Norm(q.Name), b = Norm(q.DisplayName), c = Norm(q.GetType().Name);
            foreach (string w in NameFilter.Split(','))
            {
                string k = Norm(w);
                if (k.Length == 0) continue;
                if (a.Contains(k) || b.Contains(k) || c.Contains(k)) return true;
            }
            return false;
        }

        private void Rescan()
        {
            List<IndicatorBase> found = new List<IndicatorBase>();
            try
            {
                if (ChartControl == null || ChartControl.Indicators == null) return;
                int n = ChartControl.Indicators.Count;
                for (int i = 0; i < n; i++)
                {
                    IndicatorBase q = null;
                    try { q = ChartControl.Indicators[i]; } catch { }
                    if (Matches(q)) found.Add(q);
                }
                if (found.Count > 0 || src.Count == 0) src = found;
                lastCount = n;
                if (Verbose && (!listed || found.Count != lastRescan))
                {
                    listed = true; lastRescan = found.Count;
                    Print("NexgenProbe: " + found.Count + " indicator(s) recorded:");
                    for (int i = 0; i < found.Count; i++)
                        Print("   [" + i + "] Name='" + found[i].Name + "' Type=" + found[i].GetType().Name + " plots=" + (found[i].Values == null ? 0 : found[i].Values.Length));
                }
            }
            catch (Exception ex) { if (Verbose) Print("NexgenProbe rescan: " + ex.Message); }
        }

        private void OpenFiles()
        {
            try
            {
                string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SneakyExport");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                stem = Path.Combine(dir, "nexgen_probe_" + Instrument.MasterInstrument.Name + "_" + BarsPeriod.BarsPeriodType.ToString() + BarsPeriod.Value.ToString(CultureInfo.InvariantCulture));
                plotsW = new StreamWriter(new FileStream(stem + "_plots.csv", FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                barsW = new StreamWriter(new FileStream(stem + "_bars.csv", FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                plotsW.WriteLine("time,bar,indicator,plot,value");
                barsW.WriteLine("time,bar,open,high,low,close,volume");
                opened = true;
            }
            catch (Exception ex) { Print("NexgenProbe could not open files: " + ex.Message); }
        }

        private void WriteSettings()
        {
            try
            {
                setW = new StreamWriter(new FileStream(stem + "_settings.csv", FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                setW.WriteLine("indicator,property,value");
                for (int i = 0; i < src.Count; i++)
                {
                    IndicatorBase q = src[i];
                    string key = Key(q, i);
                    foreach (PropertyInfo p in q.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        try
                        {
                            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                            Type t = p.PropertyType;
                            if (!(t.IsPrimitive || t == typeof(string) || t.IsEnum || t == typeof(decimal))) continue;
                            object v = p.GetValue(q, null);
                            string s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                            setW.WriteLine(Csv(key) + "," + Csv(p.Name) + "," + Csv(s));
                        }
                        catch { }
                    }
                }
                setW.Flush();
                settingsDone = true;
            }
            catch (Exception ex) { Print("NexgenProbe settings: " + ex.Message); settingsDone = true; }
        }

        private static string Csv(string s) { s = s ?? ""; return (s.IndexOfAny(new char[] { ',', '"', '\n', '\r' }) >= 0) ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }
        private static string Key(IndicatorBase q, int i) { return i + ":" + (string.IsNullOrWhiteSpace(q.Name) ? q.GetType().Name : q.Name.Trim()); }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1) return;
            if (State == State.Historical && Count - 2 - CurrentBar >= MaxBars) return;   // only the newest MaxBars bars
            if (!opened) OpenFiles();
            if (!opened) return;
            if (src.Count == 0 || CurrentBar % 200 == 0) Rescan();
            if (src.Count == 0) return;
            if (!settingsDone) WriteSettings();

            string ts = Time[0].ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string bar = CurrentBar.ToString(CultureInfo.InvariantCulture);
            try
            {
                barsW.WriteLine(ts + "," + bar + "," + Open[0].ToString("R", CultureInfo.InvariantCulture) + "," + High[0].ToString("R", CultureInfo.InvariantCulture) + "," +
                    Low[0].ToString("R", CultureInfo.InvariantCulture) + "," + Close[0].ToString("R", CultureInfo.InvariantCulture) + "," + Volume[0].ToString("R", CultureInfo.InvariantCulture));
                for (int i = 0; i < src.Count; i++)
                {
                    IndicatorBase q = src[i];
                    if (q.Values == null) continue;
                    string key = Key(q, i);
                    for (int p = 0; p < q.Values.Length; p++)
                    {
                        try
                        {
                            if (!q.Values[p].IsValidDataPoint(0)) continue;
                            double v = q.Values[p][0];
                            if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                            string pn = (q.Plots != null && p < q.Plots.Length && q.Plots[p] != null) ? q.Plots[p].Name : ("plot" + p);
                            plotsW.WriteLine(ts + "," + bar + "," + Csv(key) + "," + Csv(pn) + "," + v.ToString("R", CultureInfo.InvariantCulture));
                            rows++;
                        }
                        catch { }
                    }
                }
                if (CurrentBar % 50 == 0) { plotsW.Flush(); barsW.Flush(); }
            }
            catch (Exception ex) { if (Verbose) Print("NexgenProbe write: " + ex.Message); }
        }
    }
}
