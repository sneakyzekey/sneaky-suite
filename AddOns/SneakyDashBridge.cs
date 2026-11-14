// SneakyDashBridge - NinjaTrader 8 AddOn: feeds the Sneaky Dashboard (local web page) and provides the pause / long-only / short-only gate.
// Every 2 s it writes  <NT data folder>\SneakyDash\state.json  (accounts, positions, working orders, per-strategy trades incl. MAE/MFE).
// It appends every execution to  executions.csv  and reads  commands.json  (written by the dashboard) for the gate.
// It places NO orders EXCEPT one manual action: the dashboard's Flatten button (cancel working orders + close positions on one account),
// and only if the file  SneakyDash\allow_flatten.txt  exists. Strategies opt in to the gate with two lines (see README):
//     if (!SneakyGate.Allow(Name, true)) return;     // before an entry long
//     if (!SneakyGate.Allow(Name, false)) return;    // before an entry short
#region Using declarations
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
	public static class SneakyGate
	{
		static readonly object lk = new object(); static Dictionary<string, bool[]> map = new Dictionary<string, bool[]>(); static DateTime loaded = DateTime.MinValue; static bool allPaused; public static readonly HashSet<string> Seen = new HashSet<string>();
		public static string Dir { get { string d = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SneakyDash"); try { Directory.CreateDirectory(d); } catch { } return d; } }
		static void Reload()
		{
			if ((DateTime.UtcNow - loaded).TotalSeconds < 1.0) return; loaded = DateTime.UtcNow;
			try
			{
				string f = Path.Combine(Dir, "commands.json"); if (!File.Exists(f)) return; string t = File.ReadAllText(f); var m = new Dictionary<string, bool[]>(); bool ap = t.Contains("\"allPaused\":true") || t.Contains("\"allPaused\": true");
				// tiny parser: "name":{"paused":b,"longOk":b,"shortOk":b}
				int i = 0; while (true) { i = t.IndexOf("\"paused\"", i); if (i < 0) break; int ns = t.LastIndexOf("\"name\"", i); if (ns < 0) { i += 8; continue; } int q1 = t.IndexOf('"', t.IndexOf(':', ns) + 1), q2 = t.IndexOf('"', q1 + 1); string name = t.Substring(q1 + 1, q2 - q1 - 1);
					Func<string, bool> flag = key => { int k = t.IndexOf("\"" + key + "\"", ns); if (k < 0) return true; int c = t.IndexOf(':', k); return t.Substring(c + 1, 6).Contains("true"); };
					m[name] = new[] { flag("paused"), flag("longOk"), flag("shortOk") }; i = q2; }
				lock (lk) { map = m; allPaused = ap; }
			}
			catch { }
		}
		// true = the strategy may open a position in that direction
		public static bool Allow(string strategyName, bool isLong)
		{
			Reload(); lock (lk) { Seen.Add(strategyName ?? ""); if (allPaused) return false; bool[] g; if (!map.TryGetValue(strategyName ?? "", out g)) return true; if (g[0]) return false; return isLong ? g[1] : g[2]; }
		}
	}

	public class SneakyDashBridge : AddOnBase
	{
		Timer timer; readonly object wl = new object(); readonly HashSet<string> hooked = new HashSet<string>(); bool busy; string dir;
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults) { Name = "SneakyDashBridge"; Description = "Feeds the Sneaky Dashboard and provides the pause/direction gate."; }
			else if (State == State.Active) { dir = SneakyGate.Dir; timer = new Timer(Tick, null, 3000, 2000); }
			else if (State == State.Terminated) { if (timer != null) { timer.Dispose(); timer = null; } foreach (Account a in Account.All.ToList()) { try { a.ExecutionUpdate -= OnExec; } catch { } } hooked.Clear(); }
		}

		static string J(string s) { if (s == null) return ""; return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " "); }
		static string N(double d) { return double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("0.####", CultureInfo.InvariantCulture); }
		static object P(object o, string name) { if (o == null) return null; try { var pi = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance); return pi == null ? null : pi.GetValue(o, null); } catch { return null; } }
		static double D(object o, string name) { object v = P(o, name); try { return v == null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; } }
		static string S(object o, string name) { object v = P(o, name); return v == null ? "" : v.ToString(); }

		void OnExec(object sender, ExecutionEventArgs e)
		{
			try
			{
				Execution x = e.Execution; if (x == null || x.Instrument == null) return; string f = Path.Combine(dir, "executions.csv"); bool head = !File.Exists(f);
				lock (wl) File.AppendAllText(f, (head ? "time,account,instrument,action,qty,price,order,commission\n" : "") + x.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "," + x.Account.Name + "," + x.Instrument.FullName + "," + x.Order.OrderAction + "," + x.Quantity + "," + N(x.Price) + "," + (x.Order != null ? x.Order.Name : "").Replace(",", ";") + "," + N(x.Commission) + "\n");
			}
			catch { }
		}

		void Tick(object st)
		{
			if (busy) return; busy = true;
			try
			{
				var sb = new StringBuilder(); sb.Append("{\"ts\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\",\"accounts\":[");
				var strat = new StringBuilder(); var hist = new StringBuilder(); var tb = new StringBuilder(); bool fh = true; bool doHist = (DateTime.UtcNow - lastHist).TotalSeconds >= 60; bool fa = true, fs = true; var occ = new Dictionary<string, int>(); HashSet<string> seen; lock (typeof(SneakyGate)) { lock (SneakyGate.Seen) seen = new HashSet<string>(SneakyGate.Seen); } List<Account> accts; lock (Account.All) accts = Account.All.ToList();
				foreach (Account a in accts)
				{
					if (!hooked.Contains(a.Name)) { try { a.ExecutionUpdate += OnExec; hooked.Add(a.Name); } catch { } }
					if (!fa) sb.Append(","); fa = false;
					sb.Append("{\"name\":\"" + J(a.Name) + "\",\"connected\":" + (a.Connection != null && a.Connection.Status == ConnectionStatus.Connected ? "true" : "false") + ",\"cash\":" + N(a.Get(AccountItem.CashValue, Currency.UsDollar)) + ",\"netLiq\":" + N(a.Get(AccountItem.NetLiquidation, Currency.UsDollar)) + ",\"realized\":" + N(a.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar)) + ",\"unrealized\":" + N(a.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar)) + ",\"positions\":[");
					bool fp = true; foreach (Position p in a.Positions.ToList()) { if (p.MarketPosition == MarketPosition.Flat) continue; if (!fp) sb.Append(","); fp = false; sb.Append("{\"instrument\":\"" + J(p.Instrument.FullName) + "\",\"side\":\"" + p.MarketPosition + "\",\"qty\":" + p.Quantity + ",\"avg\":" + N(p.AveragePrice) + ",\"open\":" + N(p.GetUnrealizedProfitLoss(PerformanceUnit.Currency, p.Instrument.MarketData != null && p.Instrument.MarketData.Last != null ? p.Instrument.MarketData.Last.Price : p.AveragePrice)) + "}"); }
					sb.Append("],\"orders\":["); bool fo = true;
					foreach (Order o in a.Orders.ToList()) { if (o.OrderState != OrderState.Working && o.OrderState != OrderState.Accepted) continue; if (!fo) sb.Append(","); fo = false; sb.Append("{\"instrument\":\"" + J(o.Instrument.FullName) + "\",\"action\":\"" + o.OrderAction + "\",\"type\":\"" + o.OrderType + "\",\"qty\":" + o.Quantity + ",\"limit\":" + N(o.LimitPrice) + ",\"stop\":" + N(o.StopPrice) + ",\"name\":\"" + J(o.Name) + "\"}"); }
					sb.Append("]}");
					// strategies on this account (reflection: tolerant of API differences)
					object col = P(a, "Strategies"); var en = col as IEnumerable;
					if (en != null)
						foreach (object s in en.Cast<object>().ToList())
						{
							try
							{
								if (!fs) strat.Append(","); fs = false; string nm = S(s, "Name"); string inst = S(P(s, "Instrument"), "FullName"); string kb = a.Name + "|" + nm + "|" + inst; int oc; occ.TryGetValue(kb, out oc); occ[kb] = oc + 1; object perf = P(s, "SystemPerformance"); object all = P(perf, "AllTrades");
								string hd = ("{\"key\":\"" + J(kb + "#" + oc) + "\",\"gated\":" + (seen.Contains(nm) ? "true" : "false") + ",\"name\":\"" + J(nm) + "\",\"account\":\"" + J(a.Name) + "\",\"instrument\":\"" + J(S(P(s, "Instrument"), "FullName")) + "\",\"state\":\"" + J(S(s, "State")) + "\",\"position\":\"" + J(S(P(s, "Position"), "MarketPosition")) + "\",\"trades\":["); tb.Length = 0;
								var tl = all as IEnumerable; bool ft = true; int cnt = 0; var list = tl == null ? new List<object>() : tl.Cast<object>().ToList(); int skip = Math.Max(0, list.Count - 2000);
								foreach (object t in list.Skip(skip))
								{
									object en0 = P(t, "Entry"), ex0 = P(t, "Exit"); if (doHist) { if (!ft) tb.Append(","); ft = false; cnt++; } else break;
									tb.Append("{\"in\":\"" + J(S(en0, "Time")) + "\",\"out\":\"" + J(S(ex0, "Time")) + "\",\"dir\":\"" + (S(en0, "MarketPosition").StartsWith("L") ? "L" : "S") + "\",\"qty\":" + N(D(t, "Quantity")) + ",\"entry\":" + N(D(en0, "Price")) + ",\"exit\":" + N(D(ex0, "Price")) + ",\"pnl\":" + N(D(t, "ProfitCurrency")) + ",\"comm\":" + N(D(t, "Commission")) + ",\"mae\":" + N(D(t, "MaeCurrency")) + ",\"mfe\":" + N(D(t, "MfeCurrency")) + "}");
								}
								strat.Append(hd + "],\"totalTrades\":" + list.Count + "}"); if (doHist) { if (!fh) hist.Append(","); fh = false; hist.Append(hd + tb + "],\"totalTrades\":" + list.Count + "}"); }
							}
							catch (Exception ex) { Print("SneakyDashBridge strategy read: " + ex.Message); }
						}
				}
				sb.Append("],\"strategies\":[" + strat + "]}");
				if (doHist) { lastHist = DateTime.UtcNow; string ht = Path.Combine(dir, "history.tmp"), hf = Path.Combine(dir, "history.json"); File.WriteAllText(ht, "{\"ts\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\",\"strategies\":[" + hist + "]}"); if (File.Exists(hf)) File.Delete(hf); File.Move(ht, hf); }
				bool fl = File.Exists(Path.Combine(dir, "allow_flatten.txt")); sb.Length = sb.Length - 1; sb.Append(",\"flattenEnabled\":" + (fl ? "true" : "false") + "}"); if (fl) DoFlatten(accts);
				string tmp = Path.Combine(dir, "state.tmp"), fin = Path.Combine(dir, "state.json"); File.WriteAllText(tmp, sb.ToString()); if (File.Exists(fin)) File.Delete(fin); File.Move(tmp, fin);
			}
			catch (Exception ex) { try { NinjaTrader.Code.Output.Process("SneakyDashBridge: " + ex.Message, PrintTo.OutputTab1); } catch { } }
			finally { busy = false; }
		}
		string lastFlatId = ""; DateTime lastHist = DateTime.MinValue;
		// Manual emergency action requested from the dashboard button: cancel working orders and close positions on ONE account.
		void DoFlatten(List<Account> accts)
		{
			try
			{
				string rf = Path.Combine(dir, "flatten_req.json"); if (!File.Exists(rf)) return; string t = File.ReadAllText(rf);
				Func<string, string> get = key => { int k = t.IndexOf("\"" + key + "\""); if (k < 0) return ""; int c = t.IndexOf(':', k) + 1; while (c < t.Length && (t[c] == ' ' || t[c] == '"')) c++; int e = c; while (e < t.Length && t[e] != '"' && t[e] != ',' && t[e] != '}') e++; return t.Substring(c, e - c); };
				string id = get("id"), acct = get("account"); if (id == "" || id == lastFlatId) return; lastFlatId = id;
				double ageS; if (!double.TryParse(get("ts"), NumberStyles.Float, CultureInfo.InvariantCulture, out ageS)) return;
				if (Math.Abs((DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - ageS) > 30) return; // stale request: ignore
				Account a = accts.FirstOrDefault(x => x.Name == acct); string res;
				if (a == null) res = "account not found"; else
				{
					var ins = new List<Instrument>(); foreach (Position p in a.Positions.ToList()) if (p.MarketPosition != MarketPosition.Flat && !ins.Contains(p.Instrument)) ins.Add(p.Instrument);
					foreach (Order o in a.Orders.ToList()) if ((o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted) && !ins.Contains(o.Instrument)) ins.Add(o.Instrument);
					if (ins.Count == 0) res = "nothing to flatten"; else { a.Flatten(ins); res = "flatten sent for " + ins.Count + " instrument(s)"; }
				}
				File.WriteAllText(Path.Combine(dir, "flatten_result.json"), "{\"id\":\"" + J(id) + "\",\"account\":\"" + J(acct) + "\",\"result\":\"" + J(res) + "\",\"time\":\"" + DateTime.Now.ToString("HH:mm:ss") + "\"}"); Print("SneakyDashBridge: " + acct + " " + res);
			}
			catch (Exception ex) { Print("SneakyDashBridge flatten: " + ex.Message); }
		}
		void Print(string m) { try { NinjaTrader.Code.Output.Process(m, PrintTo.OutputTab1); } catch { } }
	}
}
