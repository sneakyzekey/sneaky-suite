// ═════════════════════════════════════════════════════════════════════════════
//  SneakyEquityGate — SIM → LIVE equity-curve controller (NT8 AddOn)
//  File: SneakyEquityGate_v1_0_0.cs
//
//  WHAT IT DOES
//    • Watches a MASTER (SIM) account. Every completed round-trip (flat → flat, per
//      instrument) becomes one point on a trade-by-trade equity curve.
//    • Builds an EMA of that curve. LIVE routing is permitted only by this gate:
//        1. Warm-up        : needs EMA-period points before an EMA exists.
//        2. Wait cross-down: (optional, default ON) curve must first dip below the EMA.
//        3. Cross above    : curve closes back above the EMA  → LIVE ENABLED.
//        4. Cross below    : curve closes below the EMA again  → LIVE DISABLED (SIM only)
//                            until it crosses above again.
//    • While LIVE is enabled AND the add-on is ARMED, every master fill is mirrored as a
//      market order to the checked LIVE target accounts (size = fill × multiplier).
//    • Gate flips only change the decision for the NEXT trade. A trade already copied is
//      always followed through to its exit, so a live account is never trapped in a position.
//    • Daily LIVE loss limit: per target account, realized+unrealized P&L since the trading-day
//      reset. If it reaches the limit, that target stops receiving copies for the rest of the
//      day (and is flattened if "Flatten on limit" is checked).
//
//  SAFETY
//    • Starts DISARMED every time NinjaTrader starts. Nothing is sent to a live account until
//      you press ARM and confirm. Test on two SIM accounts first.
//    • This is a stand-alone engine with its own fill-mirror. Do NOT run it on the same
//      accounts as SneakyCopierService — you would double-copy.
//    • Same instrument is used on the targets (no GC→MGC style mapping).
//
//  FILES (Documents\NinjaTrader 8\SneakyEquityGate\)
//    config.txt, state.txt, trades_<master>.csv (the persisted equity curve)
//
//  OPEN IT: Control Center → New → Sneaky Equity Gate
// ═════════════════════════════════════════════════════════════════════════════
#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Code;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    // ───────────────────────────── AddOn shell (menu + lifetime) ─────────────────────────────
    public class SneakyEquityGate : AddOnBase
    {
        private NTMenuItem menuItem;
        private NTMenuItem existingMenu;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "SIM to LIVE equity-curve + EMA routing gate with daily loss limit.";
                Name        = "Sneaky Equity Gate";
            }
            else if (State == State.Active)
            {
                SneakyEquityGateEngine.Start();
            }
            else if (State == State.Terminated)
            {
                SneakyEquityGateEngine.Stop();
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            ControlCenter cc = window as ControlCenter;
            if (cc == null) return;
            existingMenu = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            if (existingMenu == null) return;

            menuItem = new NTMenuItem();
            menuItem.Header = "Sneaky Equity Gate";
            menuItem.Style  = Application.Current.TryFindResource("MainMenuItem") as Style;
            menuItem.Click += OnMenuClick;
            existingMenu.Items.Add(menuItem);
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (menuItem != null && window is ControlCenter)
            {
                if (existingMenu != null && existingMenu.Items.Contains(menuItem))
                    existingMenu.Items.Remove(menuItem);
                menuItem.Click -= OnMenuClick;
                menuItem = null;
            }
        }

        private void OnMenuClick(object sender, RoutedEventArgs e)
        {
            NinjaTrader.Core.Globals.RandomDispatcher.BeginInvoke(new Action(delegate { new SneakyEquityGateWindow().Show(); }));
        }
    }

    // ───────────────────────────── Engine ─────────────────────────────
    public enum SneakyGatePhase { Warmup, WaitCrossBelow, Disabled, Enabled }

    public class SneakyGateSnapshot
    {
        public double[] Eq;
        public double[] Ema;
        public bool[]   EnabledAt;
        public int[]    FlipIdx;
        public bool[]   FlipUp;
        public SneakyGatePhase Phase;
        public bool   Armed;
        public bool   AllKilled;
        public bool   AnyKilled;
        public string KilledList;
        public int    EmaPeriod;
        public int    Points;
        public double DailyPnl;       // sum across targets
        public double WorstBuffer;    // min over targets of (dailyPnl - limit)
        public double DailyLimit;
        public bool   HasTargets;
        public string MasterText;
        public string TargetsText;
        public DateTime Updated;
    }

    internal class SneakyMirrorReq
    {
        public Instrument Instr;
        public bool IsEntry;
        public int  Dir;          // direction of the POSITION being opened/closed (+1 long, -1 short)
        public int  Qty;          // master fill qty (absolute)
        public bool FlattenAll;   // trade complete on master → flatten follower on that instrument
        public List<string> Targets;
    }

    internal class SneakyMasterPos
    {
        public int    Qty;                      // signed
        public double Avg;
        public double Pnl;                      // accumulated for current trade (money)
        public List<string> CopyTargets = new List<string>();
    }

    public sealed class SneakyEquityGateEngine
    {
        public static SneakyEquityGateEngine Instance;
        private const string Prefix = "SnkGate_";

        private readonly object _lock = new object();

        // ── config ──
        public string       Master = "";
        public List<string> Targets = new List<string>();
        public int          EmaPeriod = 50;
        public double       DailyLimit = -500;
        public TimeSpan     ResetTime = new TimeSpan(17, 0, 0);
        public bool         RequireCrossDown = true;
        public bool         FlattenOnLimit = true;
        public double       Multiplier = 1.0;
        public bool         Armed;                          // never persisted

        // ── curve ──
        private List<double> _eq = new List<double>();
        private double[] _ema = new double[0];
        private bool[]   _enAt = new bool[0];
        private List<int>  _flipIdx = new List<int>();
        private List<bool> _flipUp = new List<bool>();
        private SneakyGatePhase _phase = SneakyGatePhase.Warmup;

        // ── master tracking ──
        private Account _masterAcct;
        private readonly Dictionary<string, SneakyMasterPos> _pos = new Dictionary<string, SneakyMasterPos>();
        private readonly HashSet<string> _seen = new HashSet<string>();

        // ── daily ──
        private string _dayKey = "";
        private readonly Dictionary<string, double> _baseline = new Dictionary<string, double>();
        private readonly Dictionary<string, double> _dailyPnl = new Dictionary<string, double>();
        private readonly HashSet<string> _killed = new HashSet<string>();

        private System.Timers.Timer _timer;
        private bool _tickBusy;

        // ───────── lifecycle ─────────
        public static void Start()
        {
            if (Instance != null) return;
            SneakyEquityGateEngine e = new SneakyEquityGateEngine();
            Instance = e;
            e.LoadConfig();
            e.LoadTrades();
            e.LoadState();
            e._timer = new System.Timers.Timer(1000);
            e._timer.Elapsed += delegate { e.Tick(); };
            e._timer.AutoReset = true;
            e._timer.Start();
            Log("Engine started (DISARMED).");
        }

        public static void Stop()
        {
            SneakyEquityGateEngine e = Instance;
            if (e == null) return;
            Instance = null;
            try { if (e._timer != null) { e._timer.Stop(); e._timer.Dispose(); } } catch { }
            e.Unsubscribe();
            Log("Engine stopped.");
        }

        public static void Log(string msg)
        {
            try { Output.Process("[SneakyEquityGate] " + msg, PrintTo.OutputTab1); } catch { }
        }

        // ───────── paths / persistence ─────────
        private static string Dir()
        {
            string d = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SneakyEquityGate");
            try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
            return d;
        }
        private static string Safe(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
        private string TradesPath() { return Path.Combine(Dir(), "trades_" + Safe(Master.Length == 0 ? "none" : Master) + ".csv"); }

        private void LoadConfig()
        {
            try
            {
                string p = Path.Combine(Dir(), "config.txt");
                if (!File.Exists(p)) return;
                foreach (string line in File.ReadAllLines(p))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim().ToLowerInvariant();
                    string v = line.Substring(i + 1).Trim();
                    switch (k)
                    {
                        case "master": Master = v; break;
                        case "targets":
                            Targets = v.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                            break;
                        case "ema": { int n; if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) EmaPeriod = Math.Max(2, n); } break;
                        case "limit": { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) DailyLimit = d; } break;
                        case "reset": { TimeSpan t; if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out t)) ResetTime = t; } break;
                        case "crossdown": RequireCrossDown = v == "1"; break;
                        case "flatten": FlattenOnLimit = v == "1"; break;
                        case "mult": { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 0) Multiplier = d; } break;
                    }
                }
            }
            catch (Exception ex) { Log("LoadConfig: " + ex.Message); }
        }

        private void SaveConfig()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("master=" + Master);
                sb.AppendLine("targets=" + string.Join("|", Targets));
                sb.AppendLine("ema=" + EmaPeriod.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("limit=" + DailyLimit.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("reset=" + ResetTime.ToString(@"hh\:mm"));
                sb.AppendLine("crossdown=" + (RequireCrossDown ? "1" : "0"));
                sb.AppendLine("flatten=" + (FlattenOnLimit ? "1" : "0"));
                sb.AppendLine("mult=" + Multiplier.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(Path.Combine(Dir(), "config.txt"), sb.ToString());
            }
            catch (Exception ex) { Log("SaveConfig: " + ex.Message); }
        }

        private void LoadTrades()
        {
            lock (_lock)
            {
                _eq.Clear();
                _eq.Add(0.0);
                try
                {
                    string p = TradesPath();
                    if (File.Exists(p))
                    {
                        double eq = 0;
                        foreach (string line in File.ReadAllLines(p))
                        {
                            string[] f = line.Split(',');
                            if (f.Length < 3) continue;
                            double pnl;
                            if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out pnl)) continue;
                            eq += pnl;
                            _eq.Add(eq);
                        }
                    }
                }
                catch (Exception ex) { Log("LoadTrades: " + ex.Message); }
                Recompute();
            }
        }

        private void AppendTradeLine(string instr, double pnl)
        {
            try
            {
                File.AppendAllText(TradesPath(),
                    DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture) + "," + instr.Replace(',', '_') + "," +
                    pnl.ToString("0.00", CultureInfo.InvariantCulture) + Environment.NewLine);
            }
            catch (Exception ex) { Log("AppendTrade: " + ex.Message); }
        }

        private void LoadState()
        {
            try
            {
                string p = Path.Combine(Dir(), "state.txt");
                if (!File.Exists(p)) return;
                lock (_lock)
                {
                    foreach (string line in File.ReadAllLines(p))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        string k = line.Substring(0, i);
                        string v = line.Substring(i + 1);
                        if (k == "day") _dayKey = v;
                        else if (k.StartsWith("base.")) { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) _baseline[k.Substring(5)] = d; }
                        else if (k.StartsWith("kill.")) _killed.Add(k.Substring(5));
                    }
                }
            }
            catch (Exception ex) { Log("LoadState: " + ex.Message); }
        }

        private void SaveState()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                lock (_lock)
                {
                    sb.AppendLine("day=" + _dayKey);
                    foreach (KeyValuePair<string, double> kv in _baseline)
                        sb.AppendLine("base." + kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
                    foreach (string k in _killed) sb.AppendLine("kill." + k + "=1");
                }
                File.WriteAllText(Path.Combine(Dir(), "state.txt"), sb.ToString());
            }
            catch (Exception ex) { Log("SaveState: " + ex.Message); }
        }

        // ───────── settings from the UI ─────────
        public void ApplySettings(string master, List<string> targets, int ema, double limit, TimeSpan reset,
                                  bool crossDown, bool flatten, double mult)
        {
            bool masterChanged;
            lock (_lock)
            {
                masterChanged = !string.Equals(Master, master, StringComparison.OrdinalIgnoreCase);
                Master = master ?? "";
                Targets = targets ?? new List<string>();
                EmaPeriod = Math.Max(2, ema);
                DailyLimit = limit;
                ResetTime = reset;
                RequireCrossDown = crossDown;
                FlattenOnLimit = flatten;
                Multiplier = mult > 0 ? mult : 1.0;
                if (masterChanged) { Armed = false; }
            }
            SaveConfig();
            if (masterChanged)
            {
                Unsubscribe();
                LoadTrades();
            }
            else
            {
                lock (_lock) { Recompute(); }
            }
            Log("Settings applied. master=" + Master + " targets=" + string.Join(",", Targets) + " ema=" + EmaPeriod);
        }

        public void SetArmed(bool armed)
        {
            lock (_lock) { Armed = armed; }
            Log(armed ? "ARMED — live routing allowed whenever the gate is enabled." : "DISARMED — nothing will be routed live.");
        }

        public void ResetCurve()
        {
            lock (_lock)
            {
                Armed = false;
                _eq.Clear(); _eq.Add(0.0);
                try { string p = TradesPath(); if (File.Exists(p)) File.Delete(p); } catch { }
                Recompute();
            }
            Log("Equity curve reset (add-on disarmed).");
        }

        public static List<string> AccountNames()
        {
            List<string> l = new List<string>();
            try
            {
                lock (Account.All)
                {
                    foreach (Account a in Account.All)
                        if (a != null && !string.IsNullOrEmpty(a.Name)) l.Add(a.Name);
                }
            }
            catch { }
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }

        private static Account FindAccount(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                lock (Account.All)
                {
                    foreach (Account a in Account.All)
                        if (a != null && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
                }
            }
            catch { }
            return null;
        }

        private static bool IsConnected(Account a)
        {
            try { return a != null && a.Connection != null && a.Connection.Status == ConnectionStatus.Connected; }
            catch { return false; }
        }

        // ───────── equity curve / EMA / gate ─────────
        // caller holds _lock
        private void Recompute()
        {
            int n = _eq.Count;
            int N = Math.Max(2, EmaPeriod);
            double[] ema = new double[n];
            bool[] en = new bool[n];
            for (int i = 0; i < n; i++) ema[i] = double.NaN;

            if (n >= N)
            {
                double sum = 0;
                for (int i = 0; i < N; i++) sum += _eq[i];
                ema[N - 1] = sum / N;
                double a = 2.0 / (N + 1);
                for (int i = N; i < n; i++) ema[i] = a * _eq[i] + (1 - a) * ema[i - 1];
            }

            List<int> fi = new List<int>();
            List<bool> fu = new List<bool>();
            bool saw = false, enabled = false;
            for (int i = N - 1; i < n; i++)
            {
                double d = _eq[i] - ema[i];
                bool above = d > 1e-9, below = d < -1e-9;
                bool prev = enabled;
                if (RequireCrossDown)
                {
                    if (!saw) { if (below) saw = true; enabled = false; }
                    else { if (above) enabled = true; else if (below) enabled = false; }
                }
                else
                {
                    if (above) enabled = true; else if (below) enabled = false;
                }
                en[i] = enabled;
                if (enabled != prev) { fi.Add(i); fu.Add(enabled); }
            }

            _ema = ema; _enAt = en; _flipIdx = fi; _flipUp = fu;
            if (n < N) _phase = SneakyGatePhase.Warmup;
            else if (RequireCrossDown && !saw) _phase = SneakyGatePhase.WaitCrossBelow;
            else _phase = enabled ? SneakyGatePhase.Enabled : SneakyGatePhase.Disabled;
        }

        // ───────── subscriptions ─────────
        private void EnsureSubscribed()
        {
            if (_masterAcct != null) return;
            string m;
            lock (_lock) { m = Master; }
            Account a = FindAccount(m);
            if (a == null) return;
            lock (_lock)
            {
                _pos.Clear();
                try
                {
                    foreach (Position p in a.Positions.ToArray())
                    {
                        if (p == null || p.MarketPosition == MarketPosition.Flat || p.Quantity == 0) continue;
                        SneakyMasterPos mp = new SneakyMasterPos();
                        mp.Qty = p.MarketPosition == MarketPosition.Long ? p.Quantity : -p.Quantity;
                        mp.Avg = p.AveragePrice;
                        _pos[p.Instrument.FullName] = mp;   // open at startup → not copied, exit not mirrored
                    }
                }
                catch { }
                _masterAcct = a;
            }
            a.ExecutionUpdate += OnMasterExecution;
            Log("Subscribed to master account " + a.Name);
        }

        private void Unsubscribe()
        {
            Account a = _masterAcct;
            _masterAcct = null;
            if (a != null) { try { a.ExecutionUpdate -= OnMasterExecution; } catch { } }
            lock (_lock) { _pos.Clear(); }
        }

        // ───────── master fills → equity curve + mirror ─────────
        private void OnMasterExecution(object sender, ExecutionEventArgs e)
        {
            try
            {
                Execution ex = e.Execution;
                if (ex == null || ex.Order == null || ex.Instrument == null || ex.Quantity <= 0) return;

                OrderAction oa = ex.Order.OrderAction;
                int sgn = (oa == OrderAction.Buy || oa == OrderAction.BuyToCover) ? 1 : -1;

                List<SneakyMirrorReq> reqs;
                lock (_lock)
                {
                    if (!_seen.Add(ex.ExecutionId)) return;
                    if (_seen.Count > 5000) _seen.Clear();
                    double comm = 0;
                    try { comm = ex.Commission; } catch { }
                    reqs = ProcessFill(ex.Instrument, sgn * ex.Quantity, ex.Price, comm);
                }
                foreach (SneakyMirrorReq r in reqs) Mirror(r);
            }
            catch (Exception ex2) { Log("OnMasterExecution: " + ex2.Message); }
        }

        // caller holds _lock
        private List<SneakyMirrorReq> ProcessFill(Instrument instr, int fill, double price, double commission)
        {
            List<SneakyMirrorReq> reqs = new List<SneakyMirrorReq>();
            string key = instr.FullName;
            SneakyMasterPos mp;
            if (!_pos.TryGetValue(key, out mp)) { mp = new SneakyMasterPos(); _pos[key] = mp; }
            double pv = 1.0;
            try { pv = instr.MasterInstrument.PointValue; } catch { }

            mp.Pnl -= Math.Abs(commission);

            int remaining = fill;
            int guard = 0;
            while (remaining != 0 && guard++ < 4)
            {
                if (mp.Qty == 0)
                {
                    // new trade: decide NOW whether this episode is copied live
                    mp.Qty = remaining; mp.Avg = price;
                    mp.CopyTargets = EligibleTargets();
                    if (mp.CopyTargets.Count > 0)
                    {
                        SneakyMirrorReq r = new SneakyMirrorReq();
                        r.Instr = instr; r.IsEntry = true; r.Dir = Math.Sign(remaining);
                        r.Qty = Math.Abs(remaining); r.Targets = new List<string>(mp.CopyTargets);
                        reqs.Add(r);
                    }
                    remaining = 0;
                }
                else if (Math.Sign(mp.Qty) == Math.Sign(remaining))
                {
                    // add to position
                    int tot = Math.Abs(mp.Qty) + Math.Abs(remaining);
                    mp.Avg = (mp.Avg * Math.Abs(mp.Qty) + price * Math.Abs(remaining)) / tot;
                    mp.Qty += remaining;
                    if (mp.CopyTargets.Count > 0)
                    {
                        SneakyMirrorReq r = new SneakyMirrorReq();
                        r.Instr = instr; r.IsEntry = true; r.Dir = Math.Sign(mp.Qty);
                        r.Qty = Math.Abs(remaining); r.Targets = new List<string>(mp.CopyTargets);
                        reqs.Add(r);
                    }
                    remaining = 0;
                }
                else
                {
                    // reduce / close / reverse
                    int posDir = Math.Sign(mp.Qty);
                    int closeQty = Math.Min(Math.Abs(remaining), Math.Abs(mp.Qty));
                    mp.Pnl += (price - mp.Avg) * posDir * closeQty * pv;
                    mp.Qty -= posDir * closeQty;
                    bool complete = mp.Qty == 0;

                    if (mp.CopyTargets.Count > 0)
                    {
                        SneakyMirrorReq r = new SneakyMirrorReq();
                        r.Instr = instr; r.IsEntry = false; r.Dir = posDir;
                        r.Qty = closeQty; r.FlattenAll = complete; r.Targets = new List<string>(mp.CopyTargets);
                        reqs.Add(r);
                    }

                    remaining -= Math.Sign(remaining) * closeQty;   // leftover opens the opposite way

                    if (complete)
                    {
                        double tradePnl = mp.Pnl;
                        mp.Pnl = 0; mp.Avg = 0; mp.CopyTargets = new List<string>();
                        _eq.Add(_eq[_eq.Count - 1] + tradePnl);
                        AppendTradeLine(key, tradePnl);
                        SneakyGatePhase before = _phase;
                        Recompute();
                        Log("Trade closed " + key + " pnl=" + tradePnl.ToString("0.00", CultureInfo.InvariantCulture)
                            + " equity=" + _eq[_eq.Count - 1].ToString("0.00", CultureInfo.InvariantCulture)
                            + " gate " + before + " -> " + _phase);
                    }
                }
            }
            return reqs;
        }

        // caller holds _lock
        private List<string> EligibleTargets()
        {
            List<string> l = new List<string>();
            if (!Armed || _phase != SneakyGatePhase.Enabled) return l;
            foreach (string t in Targets)
            {
                if (string.Equals(t, Master, StringComparison.OrdinalIgnoreCase)) continue;
                if (_killed.Contains(t)) continue;
                if (!IsConnected(FindAccount(t))) continue;
                l.Add(t);
            }
            return l;
        }

        // ───────── order placement on targets ─────────
        private static int FollowerQty(Account a, Instrument instr)
        {
            try
            {
                foreach (Position p in a.Positions.ToArray())
                {
                    if (p == null || p.Instrument == null) continue;
                    if (p.Instrument.FullName != instr.FullName) continue;
                    if (p.MarketPosition == MarketPosition.Long) return p.Quantity;
                    if (p.MarketPosition == MarketPosition.Short) return -p.Quantity;
                }
            }
            catch { }
            return 0;
        }

        private void Mirror(SneakyMirrorReq r)
        {
            double mult;
            lock (_lock) { mult = Multiplier; }
            foreach (string t in r.Targets)
            {
                try
                {
                    Account a = FindAccount(t);
                    if (a == null || !IsConnected(a)) { Log("MIRROR skipped (account missing/disconnected): " + t); continue; }
                    lock (_lock) { if (_killed.Contains(t) && r.IsEntry) continue; }

                    int qty = Math.Max(1, (int)Math.Round(r.Qty * mult));
                    OrderAction act;
                    if (r.IsEntry)
                    {
                        act = r.Dir > 0 ? OrderAction.Buy : OrderAction.SellShort;
                    }
                    else
                    {
                        int fq = FollowerQty(a, r.Instr);
                        // only close in the direction we hold; never reverse the follower
                        if (fq == 0 || Math.Sign(fq) != r.Dir) continue;
                        qty = r.FlattenAll ? Math.Abs(fq) : Math.Min(qty, Math.Abs(fq));
                        act = r.Dir > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
                    }
                    if (qty <= 0) continue;

                    string name = Prefix + Guid.NewGuid().ToString("N").Substring(0, 8);
                    Order o = a.CreateOrder(r.Instr, act, OrderType.Market, OrderEntry.Automated, TimeInForce.Day,
                                            qty, 0, 0, string.Empty, name, NinjaTrader.Core.Globals.MaxDate, null);
                    a.Submit(new Order[] { o });
                    Log("MIRROR ▶ " + a.Name + " " + act + " " + qty + " " + r.Instr.FullName);
                }
                catch (Exception ex) { Log("MIRROR failed on " + t + ": " + ex.Message); }
            }
        }

        // ───────── 1-second housekeeping: daily reset, loss limit ─────────
        private static double AcctPnl(Account a)
        {
            try
            {
                double r = a.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
                double u = a.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
                return r + u;
            }
            catch { return double.NaN; }
        }

        private void Tick()
        {
            if (_tickBusy) return;
            _tickBusy = true;
            try
            {
                EnsureSubscribed();

                DateTime now = DateTime.Now;
                string day;
                string[] targets;
                double limit; bool flatten;
                lock (_lock)
                {
                    day = (now - ResetTime).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (day != _dayKey)
                    {
                        _dayKey = day;
                        _killed.Clear();
                        _baseline.Clear();
                        _dailyPnl.Clear();
                        Log("New trading day " + day + " — baselines and daily limits reset.");
                    }
                    targets = Targets.ToArray();
                    limit = DailyLimit; flatten = FlattenOnLimit;
                }

                bool dirty = false;
                foreach (string t in targets)
                {
                    Account a = FindAccount(t);
                    if (a == null || !IsConnected(a)) continue;
                    double pnl = AcctPnl(a);
                    if (double.IsNaN(pnl)) continue;

                    bool kill = false;
                    lock (_lock)
                    {
                        if (!_baseline.ContainsKey(t)) { _baseline[t] = pnl; dirty = true; }
                        double d = pnl - _baseline[t];
                        _dailyPnl[t] = d;
                        if (limit < 0 && d <= limit && !_killed.Contains(t))
                        {
                            _killed.Add(t); kill = true; dirty = true;
                        }
                    }
                    if (kill)
                    {
                        Log("DAILY LIMIT HIT on " + t + " (" + limit.ToString("0.00", CultureInfo.InvariantCulture) + ") — no more copies today.");
                        if (flatten)
                        {
                            try
                            {
                                List<Instrument> ins = new List<Instrument>();
                                foreach (Position p in a.Positions.ToArray())
                                    if (p != null && p.Instrument != null && p.MarketPosition != MarketPosition.Flat) ins.Add(p.Instrument);
                                if (ins.Count > 0) { a.Flatten(ins); Log("Flatten sent on " + t); }
                            }
                            catch (Exception ex) { Log("Flatten failed on " + t + ": " + ex.Message); }
                        }
                    }
                }
                if (dirty) SaveState();
            }
            catch (Exception ex) { Log("Tick: " + ex.Message); }
            finally { _tickBusy = false; }
        }

        // ───────── snapshot for the UI ─────────
        public SneakyGateSnapshot GetSnapshot()
        {
            SneakyGateSnapshot s = new SneakyGateSnapshot();
            lock (_lock)
            {
                s.Eq = _eq.ToArray();
                s.Ema = (double[])_ema.Clone();
                s.EnabledAt = (bool[])_enAt.Clone();
                s.FlipIdx = _flipIdx.ToArray();
                s.FlipUp = _flipUp.ToArray();
                s.Phase = _phase;
                s.Armed = Armed;
                s.EmaPeriod = Math.Max(2, EmaPeriod);
                s.Points = _eq.Count;
                s.DailyLimit = DailyLimit;
                s.HasTargets = Targets.Count > 0;
                s.MasterText = Master.Length == 0 ? "(none)" : Master + (_masterAcct == null ? "  — not connected" : "");
                s.TargetsText = string.Join(", ", Targets);

                double sum = 0, worst = double.MaxValue; int cnt = 0, killedCnt = 0;
                List<string> kl = new List<string>();
                foreach (string t in Targets)
                {
                    double d;
                    if (_dailyPnl.TryGetValue(t, out d)) { sum += d; cnt++; worst = Math.Min(worst, d - DailyLimit); }
                    if (_killed.Contains(t)) { killedCnt++; kl.Add(t); }
                }
                s.DailyPnl = sum;
                s.WorstBuffer = cnt > 0 ? worst : double.NaN;
                s.AnyKilled = killedCnt > 0;
                s.AllKilled = Targets.Count > 0 && killedCnt == Targets.Count;
                s.KilledList = string.Join(", ", kl);
            }
            s.Updated = DateTime.Now;
            return s;
        }
    }

    // ───────────────────────────── Window ─────────────────────────────
    public class SneakyEquityGateWindow : NTWindow
    {
        private static Brush B(string hex)
        {
            Brush b = (Brush)new BrushConverter().ConvertFromString(hex);
            if (b.CanFreeze) b.Freeze();
            return b;
        }
        private static readonly Brush BgMain  = B("#0E131B");
        private static readonly Brush BgPanel = B("#141B26");
        private static readonly Brush BdPanel = B("#263041");
        private static readonly Brush FgMain  = B("#E6EAF0");
        private static readonly Brush FgDim   = B("#8B97A8");
        private static readonly Brush Green   = B("#2ECC71");
        private static readonly Brush Red     = B("#E74C3C");
        private static readonly Brush Amber   = B("#F2B134");
        private static readonly Brush Blue    = B("#2F80ED");
        private static readonly Brush Orange  = B("#F2994A");

        private ComboBox cbMaster;
        private StackPanel spTargets;
        private TextBox tbEma, tbLimit, tbReset, tbMult;
        private CheckBox cbCross, cbFlat;
        private ToggleButton btnArm;
        private Canvas chart;
        private TextBlock tStatus, tStatusSub, tPnl, tLimit, tRemain, tAddon, tPerm, tKill, tUpdate, tMaster, tTrades;
        private Border statusBorder;
        private DispatcherTimer timer;
        private SneakyGateSnapshot last;
        private bool suppressArm;

        public SneakyEquityGateWindow()
        {
            Caption = "Sneaky Equity Gate — SIM to LIVE Controller";
            Width = 980; Height = 820;
            Content = BuildUi();
            LoadFromEngine();
            Loaded += delegate { Refresh(); };
            SizeChanged += delegate { Draw(last); };
            Closed += delegate { if (timer != null) timer.Stop(); };

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(500);
            timer.Tick += delegate { Refresh(); };
            timer.Start();
        }

        // ── UI construction ──
        private static TextBlock Tb(string text, double size, Brush fg, FontWeight? w)
        {
            TextBlock t = new TextBlock();
            t.Text = text; t.FontSize = size; t.Foreground = fg;
            if (w.HasValue) t.FontWeight = w.Value;
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        private static Border MakePanel(UIElement child)
        {
            Border b = new Border();
            b.Background = BgPanel; b.BorderBrush = BdPanel; b.BorderThickness = new Thickness(1);
            b.CornerRadius = new CornerRadius(4); b.Padding = new Thickness(10); b.Margin = new Thickness(4);
            b.Child = child;
            return b;
        }

        private UIElement BuildUi()
        {
            Grid root = new Grid();
            root.Background = BgMain;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---- settings row ----
            WrapPanel top = new WrapPanel();
            top.Margin = new Thickness(6, 6, 6, 0);

            StackPanel pMaster = new StackPanel();
            pMaster.Children.Add(Tb("Master account (SIM)", 12, FgDim, null));
            cbMaster = new ComboBox(); cbMaster.Width = 190; cbMaster.Margin = new Thickness(0, 4, 0, 0);
            pMaster.Children.Add(cbMaster);
            Button btnRefresh = new Button(); btnRefresh.Content = "Refresh accounts"; btnRefresh.Margin = new Thickness(0, 6, 0, 0);
            btnRefresh.Click += delegate { LoadFromEngine(); };
            pMaster.Children.Add(btnRefresh);
            top.Children.Add(MakePanel(pMaster));

            StackPanel pTargets = new StackPanel();
            pTargets.Children.Add(Tb("Target accounts (LIVE)", 12, FgDim, null));
            ScrollViewer sv = new ScrollViewer(); sv.Height = 96; sv.Width = 200; sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            spTargets = new StackPanel();
            sv.Content = spTargets;
            pTargets.Children.Add(sv);
            top.Children.Add(MakePanel(pTargets));

            StackPanel pEma = new StackPanel();
            pEma.Children.Add(Tb("EMA period (trades)", 12, FgDim, null));
            tbEma = new TextBox(); tbEma.Width = 80; tbEma.Margin = new Thickness(0, 4, 0, 6);
            pEma.Children.Add(tbEma);
            pEma.Children.Add(Tb("Size multiplier", 12, FgDim, null));
            tbMult = new TextBox(); tbMult.Width = 80; tbMult.Margin = new Thickness(0, 4, 0, 0);
            pEma.Children.Add(tbMult);
            top.Children.Add(MakePanel(pEma));

            StackPanel pLim = new StackPanel();
            pLim.Children.Add(Tb("Daily LIVE loss limit (USD, negative)", 12, FgDim, null));
            tbLimit = new TextBox(); tbLimit.Width = 100; tbLimit.Margin = new Thickness(0, 4, 0, 6);
            pLim.Children.Add(tbLimit);
            pLim.Children.Add(Tb("Trading-day reset (HH:mm local)", 12, FgDim, null));
            tbReset = new TextBox(); tbReset.Width = 80; tbReset.Margin = new Thickness(0, 4, 0, 0);
            pLim.Children.Add(tbReset);
            top.Children.Add(MakePanel(pLim));

            StackPanel pOpt = new StackPanel();
            cbCross = new CheckBox(); cbCross.Content = "Require cross-below first"; cbCross.Foreground = FgMain;
            cbFlat = new CheckBox(); cbFlat.Content = "Flatten target on limit"; cbFlat.Foreground = FgMain; cbFlat.Margin = new Thickness(0, 6, 0, 8);
            Button btnApply = new Button(); btnApply.Content = "Apply settings"; btnApply.Padding = new Thickness(10, 4, 10, 4);
            btnApply.Click += delegate { Apply(); };
            Button btnResetCurve = new Button(); btnResetCurve.Content = "Reset equity curve"; btnResetCurve.Margin = new Thickness(0, 6, 0, 0);
            btnResetCurve.Click += delegate
            {
                if (MessageBox.Show("Clear the equity curve and start the track record from zero?\n(The add-on will also be disarmed.)",
                    "Reset equity curve", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                { SneakyEquityGateEngine.Instance.ResetCurve(); Refresh(); }
            };
            pOpt.Children.Add(cbCross); pOpt.Children.Add(cbFlat); pOpt.Children.Add(btnApply); pOpt.Children.Add(btnResetCurve);
            top.Children.Add(MakePanel(pOpt));

            StackPanel pArm = new StackPanel();
            pArm.Children.Add(Tb("LIVE routing", 12, FgDim, null));
            btnArm = new ToggleButton(); btnArm.Content = "DISARMED  (click to ARM)"; btnArm.Width = 170; btnArm.Height = 48; btnArm.Margin = new Thickness(0, 6, 0, 0);
            btnArm.FontWeight = FontWeights.Bold;
            btnArm.Click += OnArmClick;
            pArm.Children.Add(btnArm);
            top.Children.Add(MakePanel(pArm));

            Grid.SetRow(top, 0);
            root.Children.Add(top);

            // ---- chart ----
            Grid cg = new Grid();
            cg.Margin = new Thickness(4);
            tTrades = Tb("Equity curve (master SIM) — blue: equity, orange: EMA", 13, FgMain, FontWeights.SemiBold);
            tTrades.Margin = new Thickness(8, 4, 0, 0); tTrades.VerticalAlignment = VerticalAlignment.Top;
            chart = new Canvas(); chart.ClipToBounds = true; chart.Margin = new Thickness(0, 26, 0, 0);
            chart.SizeChanged += delegate { Draw(last); };
            cg.Children.Add(chart);
            cg.Children.Add(tTrades);
            Border cb = MakePanel(cg); cb.Padding = new Thickness(2);
            Grid.SetRow(cb, 1);
            root.Children.Add(cb);

            // ---- bottom panels ----
            Grid bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            StackPanel sRoute = new StackPanel();
            sRoute.Children.Add(Tb("LIVE Routing Status", 13, FgMain, FontWeights.SemiBold));
            statusBorder = new Border(); statusBorder.BorderThickness = new Thickness(2); statusBorder.CornerRadius = new CornerRadius(4);
            statusBorder.Margin = new Thickness(0, 8, 0, 0); statusBorder.Padding = new Thickness(8);
            StackPanel inner = new StackPanel();
            tStatus = Tb("—", 24, FgMain, FontWeights.Bold); tStatus.HorizontalAlignment = HorizontalAlignment.Center;
            tStatusSub = Tb("", 12, FgDim, null); tStatusSub.HorizontalAlignment = HorizontalAlignment.Center; tStatusSub.TextWrapping = TextWrapping.Wrap; tStatusSub.TextAlignment = TextAlignment.Center;
            inner.Children.Add(tStatus); inner.Children.Add(tStatusSub);
            statusBorder.Child = inner;
            sRoute.Children.Add(statusBorder);
            Border bRoute = MakePanel(sRoute); Grid.SetColumn(bRoute, 0); bottom.Children.Add(bRoute);

            StackPanel sPnl = new StackPanel();
            sPnl.Children.Add(Tb("Daily LIVE PnL", 13, FgMain, FontWeights.SemiBold));
            tPnl = Tb("—", 26, Green, FontWeights.Bold); tPnl.HorizontalAlignment = HorizontalAlignment.Center; tPnl.Margin = new Thickness(0, 6, 0, 6);
            sPnl.Children.Add(tPnl);
            tLimit = Tb("", 12, FgDim, null); tRemain = Tb("", 12, FgDim, null);
            sPnl.Children.Add(tLimit); sPnl.Children.Add(tRemain);
            Border bPnl = MakePanel(sPnl); Grid.SetColumn(bPnl, 1); bottom.Children.Add(bPnl);

            StackPanel sSys = new StackPanel();
            sSys.Children.Add(Tb("System", 13, FgMain, FontWeights.SemiBold));
            tMaster = Tb("", 12, FgDim, null); tAddon = Tb("", 12, FgDim, null); tPerm = Tb("", 12, FgDim, null); tKill = Tb("", 12, FgDim, null); tUpdate = Tb("", 12, FgDim, null);
            sSys.Children.Add(tMaster); sSys.Children.Add(tAddon); sSys.Children.Add(tPerm); sSys.Children.Add(tKill); sSys.Children.Add(tUpdate);
            Border bSys = MakePanel(sSys); Grid.SetColumn(bSys, 2); bottom.Children.Add(bSys);

            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);
            return root;
        }

        // ── settings <-> engine ──
        private void LoadFromEngine()
        {
            SneakyEquityGateEngine e = SneakyEquityGateEngine.Instance;
            if (e == null) return;
            List<string> names = SneakyEquityGateEngine.AccountNames();

            cbMaster.Items.Clear();
            foreach (string n in names) cbMaster.Items.Add(n);
            cbMaster.SelectedItem = names.FirstOrDefault(n => string.Equals(n, e.Master, StringComparison.OrdinalIgnoreCase));

            spTargets.Children.Clear();
            foreach (string n in names)
            {
                CheckBox c = new CheckBox();
                c.Content = n; c.Foreground = FgMain; c.Margin = new Thickness(0, 2, 0, 2);
                c.IsChecked = e.Targets.Any(t => string.Equals(t, n, StringComparison.OrdinalIgnoreCase));
                spTargets.Children.Add(c);
            }

            tbEma.Text = e.EmaPeriod.ToString(CultureInfo.InvariantCulture);
            tbMult.Text = e.Multiplier.ToString(CultureInfo.InvariantCulture);
            tbLimit.Text = e.DailyLimit.ToString(CultureInfo.InvariantCulture);
            tbReset.Text = e.ResetTime.ToString(@"hh\:mm");
            cbCross.IsChecked = e.RequireCrossDown;
            cbFlat.IsChecked = e.FlattenOnLimit;
        }

        private void Apply()
        {
            SneakyEquityGateEngine e = SneakyEquityGateEngine.Instance;
            if (e == null) return;
            string master = cbMaster.SelectedItem as string ?? "";
            List<string> targets = new List<string>();
            foreach (UIElement u in spTargets.Children)
            {
                CheckBox c = u as CheckBox;
                if (c != null && c.IsChecked == true && !string.Equals((string)c.Content, master, StringComparison.OrdinalIgnoreCase))
                    targets.Add((string)c.Content);
            }
            int ema; double limit, mult; TimeSpan reset;
            if (!int.TryParse(tbEma.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ema) || ema < 2) { MessageBox.Show("EMA period must be a whole number ≥ 2."); return; }
            if (!double.TryParse(tbLimit.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out limit)) { MessageBox.Show("Daily loss limit must be a number, e.g. -500."); return; }
            if (limit > 0) limit = -limit;
            if (!double.TryParse(tbMult.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out mult) || mult <= 0) { MessageBox.Show("Size multiplier must be a positive number."); return; }
            if (!TimeSpan.TryParse(tbReset.Text.Trim(), CultureInfo.InvariantCulture, out reset)) { MessageBox.Show("Reset time must look like 17:00."); return; }

            e.ApplySettings(master, targets, ema, limit, reset, cbCross.IsChecked == true, cbFlat.IsChecked == true, mult);
            Refresh();
        }

        private void OnArmClick(object sender, RoutedEventArgs ev)
        {
            if (suppressArm) return;
            SneakyEquityGateEngine e = SneakyEquityGateEngine.Instance;
            if (e == null) return;
            if (btnArm.IsChecked == true)
            {
                SneakyGateSnapshot s = e.GetSnapshot();
                string msg = "ARM live routing?\n\nWhen the equity-curve gate is ENABLED, master fills will be copied as REAL market orders to:\n\n"
                    + (s.TargetsText.Length == 0 ? "(no target accounts selected)" : s.TargetsText)
                    + "\n\nDaily loss limit: " + s.DailyLimit.ToString("0.00", CultureInfo.InvariantCulture)
                    + "\n\nHave you tested this on SIM accounts first?";
                if (MessageBox.Show(msg, "ARM LIVE ROUTING", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    e.SetArmed(true);
                else
                { suppressArm = true; btnArm.IsChecked = false; suppressArm = false; }
            }
            else e.SetArmed(false);
            Refresh();
        }

        // ── refresh / drawing ──
        private void Refresh()
        {
            SneakyEquityGateEngine e = SneakyEquityGateEngine.Instance;
            if (e == null) return;
            SneakyGateSnapshot s = e.GetSnapshot();
            last = s;

            suppressArm = true;
            btnArm.IsChecked = s.Armed;
            btnArm.Content = s.Armed ? "ARMED  (click to DISARM)" : "DISARMED  (click to ARM)";
            btnArm.Foreground = s.Armed ? Green : Amber;
            btnArm.Background = s.Armed ? B("#16351F") : B("#3A2F12");
            btnArm.BorderBrush = s.Armed ? Green : Amber;
            suppressArm = false;

            // status tile
            string st, sub; Brush col;
            if (s.AllKilled) { st = "DAILY LIMIT HIT"; sub = "All targets stopped for today. Resets at the trading-day reset time."; col = Red; }
            else if (s.Phase == SneakyGatePhase.Warmup)
            { st = "WARMING UP"; sub = "Equity points " + s.Points + " / " + s.EmaPeriod + " needed before the EMA exists. Trades go to SIM only."; col = FgDim; }
            else if (s.Phase == SneakyGatePhase.WaitCrossBelow)
            { st = "WAITING: CROSS BELOW EMA"; sub = "SIM only. Equity must dip below the EMA, then close back above it."; col = Amber; }
            else if (s.Phase == SneakyGatePhase.Disabled)
            { st = "LIVE DISABLED"; sub = "Equity is below the EMA — trading SIM only until it crosses back above."; col = Red; }
            else if (!s.Armed)
            { st = "GATE OPEN — DISARMED"; sub = "Equity is above the EMA but routing is DISARMED. Press ARM to copy to LIVE."; col = Amber; }
            else if (!s.HasTargets)
            { st = "GATE OPEN — NO TARGETS"; sub = "Select at least one LIVE target account and press Apply."; col = Amber; }
            else
            { st = "LIVE ENABLED"; sub = "Next eligible trade will be copied to LIVE."; col = Green; }
            tStatus.Text = st; tStatus.Foreground = col; tStatusSub.Text = sub;
            statusBorder.BorderBrush = col;

            // pnl panel
            if (double.IsNaN(s.WorstBuffer)) { tPnl.Text = "—"; tPnl.Foreground = FgDim; tRemain.Text = "Buffer to limit: —"; }
            else
            {
                tPnl.Text = (s.DailyPnl >= 0 ? "+" : "") + s.DailyPnl.ToString("0.00", CultureInfo.InvariantCulture);
                tPnl.Foreground = s.DailyPnl >= 0 ? Green : Red;
                tRemain.Text = "Buffer to limit (worst acct): " + s.WorstBuffer.ToString("0.00", CultureInfo.InvariantCulture);
                tRemain.Foreground = s.WorstBuffer > 0 ? Green : Red;
            }
            tLimit.Text = "Daily loss limit: " + s.DailyLimit.ToString("0.00", CultureInfo.InvariantCulture) + " per target";

            tMaster.Text = "Master: " + s.MasterText;
            tAddon.Text = "Routing: " + (s.Armed ? "ARMED" : "DISARMED");
            tPerm.Text = "LIVE permission (gate): " + (s.Phase == SneakyGatePhase.Enabled ? "Enabled" : "Disabled");
            tKill.Text = "Daily kill switch: " + (s.AnyKilled ? "TRIGGERED (" + s.KilledList + ")" : "Not triggered");
            tKill.Foreground = s.AnyKilled ? Red : FgDim;
            tUpdate.Text = "Last update: " + s.Updated.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            tTrades.Text = "Equity curve (master SIM) — blue: equity, orange: EMA(" + s.EmaPeriod + ")   |   completed trades: " + (s.Points - 1);

            Draw(s);
        }

        private void Draw(SneakyGateSnapshot s)
        {
            if (s == null || chart == null) return;
            double w = chart.ActualWidth, h = chart.ActualHeight;
            if (w < 80 || h < 60) return;
            chart.Children.Clear();

            double L = 58, R = 12, T = 8, Bm = 22;
            double pw = w - L - R, ph = h - T - Bm;
            int n = s.Eq.Length;

            double mn = 0, mx = 0;
            for (int i = 0; i < n; i++)
            {
                mn = Math.Min(mn, s.Eq[i]); mx = Math.Max(mx, s.Eq[i]);
                if (!double.IsNaN(s.Ema[i])) { mn = Math.Min(mn, s.Ema[i]); mx = Math.Max(mx, s.Ema[i]); }
            }
            if (mx - mn < 1) { mx += 1; mn -= 1; }
            double pad = (mx - mn) * 0.08; mn -= pad; mx += pad;

            Func<int, double> X = delegate (int i) { return L + (n <= 1 ? 0 : pw * i / (double)(n - 1)); };
            Func<double, double> Y = delegate (double v) { return T + ph * (1.0 - (v - mn) / (mx - mn)); };

            // grid + labels
            for (int g = 0; g <= 4; g++)
            {
                double v = mn + (mx - mn) * g / 4.0;
                double y = Y(v);
                System.Windows.Shapes.Line ln = new System.Windows.Shapes.Line();
                ln.X1 = L; ln.X2 = L + pw; ln.Y1 = y; ln.Y2 = y; ln.Stroke = BdPanel; ln.StrokeThickness = 1;
                chart.Children.Add(ln);
                TextBlock lb = Tb(v.ToString((mx - mn) >= 20 ? "N0" : "N2", CultureInfo.InvariantCulture), 11, FgDim, null);
                Canvas.SetLeft(lb, 4); Canvas.SetTop(lb, y - 8);
                chart.Children.Add(lb);
            }
            // zero line
            if (mn < 0 && mx > 0)
            {
                System.Windows.Shapes.Line z = new System.Windows.Shapes.Line();
                z.X1 = L; z.X2 = L + pw; z.Y1 = Y(0); z.Y2 = Y(0); z.Stroke = FgDim; z.StrokeThickness = 1;
                z.StrokeDashArray = new DoubleCollection(new double[] { 3, 3 });
                chart.Children.Add(z);
            }

            // enabled / disabled shading (only once the EMA exists)
            int N = s.EmaPeriod;
            if (n >= N)
            {
                int runStart = N - 1;
                for (int i = N - 1; i < n; i++)
                {
                    bool endRun = (i == n - 1) || (s.EnabledAt[i + 1] != s.EnabledAt[runStart]);
                    if (endRun)
                    {
                        double x1 = X(runStart), x2 = X(Math.Min(n - 1, i + 1));
                        if (x2 - x1 < 1) x2 = x1 + 1;
                        System.Windows.Shapes.Rectangle r = new System.Windows.Shapes.Rectangle();
                        r.Width = x2 - x1; r.Height = ph;
                        r.Fill = s.EnabledAt[runStart] ? B("#1A2ECC71") : B("#1AE74C3C");
                        Canvas.SetLeft(r, x1); Canvas.SetTop(r, T);
                        chart.Children.Add(r);
                        runStart = i + 1;
                    }
                }
                // flip markers
                for (int f = 0; f < s.FlipIdx.Length; f++)
                {
                    double x = X(s.FlipIdx[f]);
                    System.Windows.Shapes.Line v = new System.Windows.Shapes.Line();
                    v.X1 = x; v.X2 = x; v.Y1 = T; v.Y2 = T + ph;
                    v.Stroke = s.FlipUp[f] ? Green : Red; v.StrokeThickness = 1.2;
                    v.StrokeDashArray = new DoubleCollection(new double[] { 4, 3 });
                    chart.Children.Add(v);
                    TextBlock tag = Tb(s.FlipUp[f] ? "LIVE ON" : "LIVE OFF", 10, s.FlipUp[f] ? Green : Red, FontWeights.Bold);
                    Canvas.SetLeft(tag, Math.Min(x + 3, L + pw - 50)); Canvas.SetTop(tag, T + (s.FlipUp[f] ? 2 : 16));
                    chart.Children.Add(tag);
                }
            }

            // equity + ema lines
            System.Windows.Shapes.Polyline pe = new System.Windows.Shapes.Polyline();
            pe.Stroke = Blue; pe.StrokeThickness = 1.8; pe.StrokeLineJoin = PenLineJoin.Round;
            for (int i = 0; i < n; i++) pe.Points.Add(new Point(X(i), Y(s.Eq[i])));
            chart.Children.Add(pe);

            System.Windows.Shapes.Polyline pm = new System.Windows.Shapes.Polyline();
            pm.Stroke = Orange; pm.StrokeThickness = 1.8;
            bool any = false;
            for (int i = 0; i < n; i++) if (!double.IsNaN(s.Ema[i])) { pm.Points.Add(new Point(X(i), Y(s.Ema[i]))); any = true; }
            if (any) chart.Children.Add(pm);

            // x labels
            int[] marks = new int[] { 0, (n - 1) / 2, n - 1 };
            foreach (int m in marks.Distinct())
            {
                TextBlock xl = Tb(m.ToString(CultureInfo.InvariantCulture), 11, FgDim, null);
                Canvas.SetLeft(xl, X(m) - 6); Canvas.SetTop(xl, T + ph + 3);
                chart.Children.Add(xl);
            }
            TextBlock xt = Tb("Trade number", 11, FgDim, null);
            Canvas.SetLeft(xt, L + pw / 2 - 30); Canvas.SetTop(xt, T + ph + 3);
            if (n < 8) chart.Children.Add(xt);
        }
    }
}
