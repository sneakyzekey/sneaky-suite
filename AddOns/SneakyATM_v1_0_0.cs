// ═════════════════════════════════════════════════════════════════════════════
//  SneakyATM — daily goal/risk sizing + pullback advisor (NT8 AddOn)
//  File: SneakyATM_v1_0_0.cs
//
//  WHAT IT DOES (v1 — recreated from marketing/demo material for a closed-source
//  third-party tool ("Maverick ATM"); no source was available, this is an original
//  implementation of the described behavior, not a port — and this tool is its own
//  thing with its own name/branding, not a copy of theirs)
//    • You set a daily profit-lock goal, a daily loss limit, and a fixed per-trade
//      risk cap for one account.
//    • Click PREVIEW LONG or PREVIEW SHORT to see four tiered one-click sizes --
//      Quick / Medium / Large / Home Run. Quick uses a tighter "scalp" stop;
//      Medium/Large/Home Run share one wider ATR-based stop with escalating
//      1R / 1.5R / 2R targets. Quantity is sized to fit your per-trade risk cap,
//      hard-capped again against whatever's left of today's loss limit -- neither
//      cap can ever be exceeded. If the nominal stop can't fit even 1 contract
//      inside the cap, the stop is tightened to fit (shown as "Stop tightened X
//      -> Y ticks"); if even a tightened stop would be unreasonably small, that
//      group of tiers is skipped rather than forced.
//    • An OBSERVATIONS advisor evaluates eight rules every preview and HARD-BLOCKS
//      entry if any fire, with one shared "Override" toggle to bypass all of them
//      per-session (matches how the real product's screenshots show one shared
//      override, not a per-rule one):
//        1) Consecutive-candle pullback -- N closed candles in a row (default 3)
//           in your preview direction, read off whatever chart has SneakyAtmLink.
//        2) Volume spike -- last closed bar's volume >= VolumeSpikeMultiple (2.0x
//           default) times a rolling 20-bar average, also from SneakyAtmLink.
//        3) Session time-of-day -- a configurable local "quiet/chop" window
//           (QuietStart/QuietEnd, default 12:00-13:00 -- a common index-futures
//           lunch lull); pure wall-clock, no chart data needed.
//        4) Distance to a prior-session level -- price within
//           PriorLevelToleranceTicks (default 10) of the prior completed
//           session's High/Low/Close, tracked by SneakyAtmLink the same way
//           NinjaTrader's own built-in PriorDayOHLC indicator does.
//        5) Pace to goal -- today's P/L has already reached PaceToGoalThresholdPct
//           (default 70%) of the profit lock but hasn't hit it yet -- purely from
//           the engine's own daily tracking, no chart data needed.
//        6) Range compression -- price has stayed inside a tight band (vs. ATR)
//           for the full 20-bar window SneakyAtmLink tracks. Direction-agnostic.
//        7) SAR-flip chop -- a hand-rolled Parabolic SAR (SneakyAtmLink again) has
//           flipped trend 3+ times in that same 20-bar window. Direction-agnostic.
//        8) Upcoming USD news -- a Medium/High-impact economic release is due within
//           NewsPreReleaseMinutes (default 15). Feed is an UNOFFICIAL, community-
//           sourced ForexFactory-style calendar (same one most third-party NT8/MT4
//           "news lockout" tools use) -- no official public API exists for this, so
//           the address could move or the format could change without notice; every
//           failure is swallowed and just leaves this rule inert rather than
//           blocking previews. Country/impact filter (USD, Medium+High) is
//           hardcoded, not yet a setting.
//      Separately, a few informational-only notes (account switched, a candle
//      burst just happened, day vs. overnight session, a qualifying news release
//      that just happened, wins-needed-to-goal) show above the preview buttons at
//      all times, even before PREVIEW LONG/SHORT is clicked -- these never block
//      anything, there's nothing to override.
//    • A separate "Override sizing" toggle + CONTRACTS stepper lets you manually
//      size a trade yourself when you want to take more than Risk/trade $ would
//      auto-size -- that per-trade cap genuinely doesn't apply once this is on.
//      What still can't be bypassed, same as every other path, is today's
//      remaining daily LOSS limit: if your chosen quantity would risk more than
//      what's left of it, the quantity (never the stop) is hard-clamped down to
//      whatever today's limit actually allows, with a "MAX OVERRIDE" banner
//      explaining why. Hovering a tier button isolates the chart down to just
//      that tier's own entry/stop/target lines; with nothing hovered, the chart
//      shows the shared entry plus every available tier's target line at once.
//    • Once filled, the bracket's stop is moved to breakeven once price reaches
//      1R in your favor.
//    • Once the daily goal or the daily risk limit is hit (or the max-trades count
//      is reached), the window locks out all entries for the rest of the trading
//      day (reset time configurable, default 5:00 PM local).
//
//  DESIGN NOTES / THINGS I (Claude) CHOSE OR APPROXIMATED, NOT A PUBLISHED SPEC
//    • "Scalp stop" for Quick is a real ATR(14) computed off a genuine 1-minute
//      series added to SneakyAtmLink (via AddDataSeries), independent of whatever
//      timeframe the chart itself is on -- not a fraction of the shared/main stop.
//      ScalpStopFraction (default 0.67) is kept only as a fallback used if that
//      1-minute series isn't available yet (e.g. right after dropping the link
//      indicator on a chart, before it has 1-2 bars of the fast series to compute
//      from) -- the UI labels the stop "1m ATR" or "scalp approx." accordingly.
//    • Stop-tightening floor = MinStopFraction (default 0.5) of the nominal stop;
//      below that a tier is skipped ("too wild") rather than tightened further.
//    • Win/loss/R scoring only counts a trade when its OWN stop or target order
//      is the one that shows Filled; a manual flatten isn't scored (logged, not
//      counted), to avoid guessing at a result that wasn't actually one of the
//      bracket's own orders filling.
//    • v1's only advisor rule is the consecutive-candle pullback warning, per
//      your scoping answer earlier -- your screenshots show others (volume spike,
//      session-time-of-day, distance to a prior level, pace-to-goal). None of
//      those are implemented here; say if you want any added.
//
//  FILES (Documents\NinjaTrader 8\SneakyATM\): config.txt, state.txt
//  OPEN IT: Control Center → New → Sneaky ATM
// ═════════════════════════════════════════════════════════════════════════════
#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Code;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;   // SneakyAtmLinkRegistry
#endregion

namespace NinjaTrader.NinjaScript.AddOns.Sneaky
{
    // ───────────────────────────── AddOn shell (menu + engine lifetime) ─────────────────────────────
    public class SneakyATM : NinjaTrader.NinjaScript.AddOnBase
    {
        private NTMenuItem menuItem;
        private NTMenuItem existingMenu;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "SneakyATM";
                Description = "Daily goal/risk tiered sizing + consecutive-candle pullback advisor (Control Center ▸ New).";
            }
            else if (State == State.Active)
            {
                SneakyAtmEngine.Start();
            }
            else if (State == State.Terminated)
            {
                SneakyAtmEngine.Stop();
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            ControlCenter cc = window as ControlCenter;
            if (cc == null || menuItem != null) return;

            existingMenu = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            if (existingMenu == null) return;

            menuItem = new NTMenuItem
            {
                Header = "Sneaky ATM",
                Style  = Application.Current.TryFindResource("MainMenuItem") as Style
            };
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
                existingMenu = null;
            }
        }

        private void OnMenuClick(object sender, RoutedEventArgs e)
        {
            NinjaTrader.Core.Globals.RandomDispatcher.InvokeAsync(new Action(() =>
            {
                var w = new SneakyAtmWindow();
                w.Show();
                w.Activate();
            }));
        }
    }

    // One parsed <event> from the economic-calendar feed (see SneakyAtmEngine.FetchNewsFeed).
    public sealed class NewsEvent
    {
        public string   Title;
        public string   Country;     // currency code as the feed spells it, e.g. "USD"
        public string   Impact;      // "High" / "Medium" / "Low" / "Holiday" / etc., as the feed spells it
        public DateTime? WhenLocal;  // null if the feed gave no parseable clock time ("All Day", "Tentative", "Holiday", ...)
    }

    // ───────────────────────────── Engine: config + daily P&L/trade tracking ─────────────────────────────
    public sealed class SneakyAtmEngine
    {
        public static SneakyAtmEngine Instance;

        private readonly object _lock = new object();

        // ── config (persisted) ──
        public string   AccountName          = "";
        public double   DailyGoal            = 1000;    // "profit lock"
        public double   DailyRiskLimit        = 500;     // "loss limit" -- positive number = max allowed loss
        public double   RiskPerTrade         = 250;     // fixed per-trade $ risk cap
        public int      MaxTradesPerDay      = 10;
        public TimeSpan ResetTime            = new TimeSpan(17, 0, 0);
        public int      ConsecutiveRun       = 3;
        public double   StopAtrMult          = 1.0;
        public double   ScalpStopFraction    = 0.67;    // Quick's stop as a fraction of the shared ATR stop
        public double   MinStopFraction      = 0.5;     // floor for auto-tightening before a tier is skipped
        public string   LastChartKey         = "";
        public string   ActiveProfile        = "Default";   // name of the last-applied/-saved named settings profile

        // ── advisor/OBSERVATIONS thresholds (persisted) ──
        public double   VolumeSpikeMultiple     = 2.0;            // warn if last closed bar's volume >= this x the rolling baseline
        public double   PriorLevelToleranceTicks = 10;            // warn if price is within this many ticks of a prior-session H/L/C
        public double   PaceToGoalThresholdPct  = 70;             // warn once today's P/L reaches this % of the profit lock (and hasn't hit it yet)
        public TimeSpan QuietStart              = new TimeSpan(12, 0, 0);   // "quiet/chop window" start, local time -- default is a common lunch lull
        public TimeSpan QuietEnd                = new TimeSpan(13, 0, 0);   // ... end, local time

        // ── informational-only notes (never block entry, no override needed) ──
        // Day/Overnight is approximated from local wall-clock time rather than the instrument's real
        // session template (no confirmed-safe NinjaScript API for that was found in this codebase) --
        // default matches US equity-index-futures regular trading hours.
        public TimeSpan DaySessionStart         = new TimeSpan(9, 30, 0);
        public TimeSpan DaySessionEnd           = new TimeSpan(16, 0, 0);

        // ── economic news calendar (persisted thresholds only -- the feed itself is never persisted
        //    to disk, just kept in memory and re-fetched) ──
        // Community-sourced, UNOFFICIAL feed -- the same one most third-party NT8/MT4 "news lockout"
        // indicators use, since ForexFactory itself offers no public, authenticated API for this.
        // It could change address, format, or disappear without notice -- every failure in
        // FetchNewsFeed is caught and just leaves the last successfully fetched list in place rather
        // than touching anything else this engine tracks. Hard-filtered to USD, Medium/High impact
        // only (per your own answer) -- index futures (ES/NQ/...) don't move on a JPY retail-sales
        // print. Country/impact filter is intentionally not exposed as a setting yet; say if you want
        // it configurable later.
        // 2026-11-25: the "cdn-nfs.faireconomy.media" address this used at first came back with a DNS
        // resolution failure on the user's machine (the hostname doesn't resolve at all, not a block)
        // -- web research found multiple independent 2023-2024 reports confirming "nfs.faireconomy.media"
        // (no "cdn-" prefix) as the current working address instead; switched to that. If THIS one also
        // fails to resolve, the feed has likely moved again and needs re-researching, not just retried.
        private const string NewsFeedUrl       = "https://nfs.faireconomy.media/ff_calendar_thisweek.xml";
        private const string NewsCountryFilter = "USD";
        public double  NewsPreReleaseMinutes  = 15;   // hard-blocks entry starting this many minutes BEFORE a qualifying release
        public double  NewsPostReleaseMinutes = 15;   // informational-only note for this many minutes AFTER one

        private readonly object      _newsLock = new object();
        private List<NewsEvent>      _newsEvents = new List<NewsEvent>();
        private System.Timers.Timer  _newsTimer;

        // ── news-feed health, for the window's "News feed: ..." status line (see BuildLayout) ──
        public DateTime LastNewsFetchUtc;     // MinValue until the first fetch completes (success OR failure)
        public DateTime LastNewsSuccessUtc;   // MinValue until the first fetch that actually succeeds
        public int      LastNewsEventCount;   // total <event> entries parsed on the last SUCCESSFUL fetch
        public string   LastNewsError;        // null when the last fetch succeeded

        // ── daily state (persisted) ──
        private string _dayKey = "";
        private readonly Dictionary<string, double> _baseline    = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int>    _tradesToday = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int>    _winsToday   = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int>    _lossesToday = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _sumRToday   = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private System.Timers.Timer _timer;

        // ───────── lifecycle ─────────
        public static void Start()
        {
            if (Instance != null) return;
            var e = new SneakyAtmEngine();
            Instance = e;
            e.LoadConfig();
            e.LoadState();
            e._timer = new System.Timers.Timer(1000) { AutoReset = true };
            e._timer.Elapsed += delegate { e.Tick(); };
            e._timer.Start();

            e._newsTimer = new System.Timers.Timer(15 * 60 * 1000) { AutoReset = true };   // re-fetch every 15 min
            e._newsTimer.Elapsed += delegate { e.FetchNewsFeed(); };
            e._newsTimer.Start();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { e.FetchNewsFeed(); });   // prime it now rather than waiting the full 15 min

            Log("Engine started.");
        }

        public static void Stop()
        {
            SneakyAtmEngine e = Instance;
            if (e == null) return;
            Instance = null;
            try { if (e._timer != null) { e._timer.Stop(); e._timer.Dispose(); } } catch { }
            try { if (e._newsTimer != null) { e._newsTimer.Stop(); e._newsTimer.Dispose(); } } catch { }
            Log("Engine stopped.");
        }

        // ───────── economic news calendar: fetch + parse + lookup ─────────
        private void FetchNewsFeed()
        {
            try
            {
                string xml;
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "SneakyATM/1.0";
                    xml = wc.DownloadString(NewsFeedUrl);
                }

                var doc = new XmlDocument();
                doc.LoadXml(xml);

                TimeZoneInfo eastern = null;
                try { eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); } catch { }

                var list = new List<NewsEvent>();
                foreach (XmlNode node in doc.GetElementsByTagName("event"))
                {
                    string title = ChildText(node, "title");
                    if (string.IsNullOrEmpty(title)) continue;

                    DateTime parsedDate;
                    TimeSpan tod;
                    DateTime? whenLocal = null;
                    if (DateTime.TryParseExact(ChildText(node, "date"), "MM-dd-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedDate) &&
                        TryParseFeedTime(ChildText(node, "time"), out tod))
                    {
                        try
                        {
                            DateTime unspecified = DateTime.SpecifyKind(parsedDate.Date + tod, DateTimeKind.Unspecified);
                            DateTime utc = eastern != null ? TimeZoneInfo.ConvertTimeToUtc(unspecified, eastern) : unspecified.ToUniversalTime();
                            whenLocal = utc.ToLocalTime();
                        }
                        catch { whenLocal = null; }
                    }

                    list.Add(new NewsEvent { Title = title, Country = ChildText(node, "country"), Impact = ChildText(node, "impact"), WhenLocal = whenLocal });
                }

                lock (_newsLock) { _newsEvents = list; }
                LastNewsFetchUtc = DateTime.UtcNow;
                LastNewsSuccessUtc = LastNewsFetchUtc;
                LastNewsEventCount = list.Count;
                LastNewsError = null;
                Log("News feed OK -- " + list.Count + " events parsed.");
            }
            catch (Exception ex)
            {
                LastNewsFetchUtc = DateTime.UtcNow;
                LastNewsError = ex.Message;
                Log("News feed fetch failed (leaving last known list in place): " + ex.Message);
            }
        }

        private static string ChildText(XmlNode eventNode, string name)
        {
            foreach (XmlNode child in eventNode.ChildNodes)
                if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                    return child.InnerText;
            return null;
        }

        // The feed spells clock time like "8:30am", "12:00pm", "10:00pm" -- or, for events with no
        // single instant, "All Day", "Tentative", "Day 1", "Holiday" etc., which this deliberately
        // fails to parse (caller then just skips that event for any time-based lookup).
        private static bool TryParseFeedTime(string timeStr, out TimeSpan tod)
        {
            tod = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(timeStr)) return false;
            string s = timeStr.Trim().ToLowerInvariant();
            bool pm = s.EndsWith("pm"); bool am = s.EndsWith("am");
            if (!pm && !am) return false;
            s = s.Substring(0, s.Length - 2).Trim();
            string[] parts = s.Split(':');
            int hour, minute = 0;
            if (parts.Length < 1 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)) return false;
            if (parts.Length > 1) int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minute);
            if (pm && hour != 12) hour += 12;
            if (am && hour == 12) hour = 0;
            if (hour < 0 || hour > 23 || minute < 0 || minute > 59) return false;
            tod = new TimeSpan(hour, minute, 0);
            return true;
        }

        private static bool IsUsdMediumOrHigh(NewsEvent ev)
        {
            if (ev == null || string.IsNullOrEmpty(ev.Country) || !string.Equals(ev.Country.Trim(), NewsCountryFilter, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrEmpty(ev.Impact)) return false;
            string imp = ev.Impact.Trim();
            return imp.IndexOf("High", StringComparison.OrdinalIgnoreCase) >= 0 || imp.IndexOf("Medium", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // nearest qualifying release in the next 'lookaheadMinutes' -- for the hard-blocking OBSERVATIONS rule
        public bool TryGetUpcomingNews(double lookaheadMinutes, out string title, out string impact, out int minutesUntil)
        {
            title = null; impact = null; minutesUntil = 0;
            DateTime now = DateTime.Now;
            List<NewsEvent> snapshot;
            lock (_newsLock) snapshot = _newsEvents;
            NewsEvent best = null; double bestMins = double.MaxValue;
            foreach (NewsEvent ev in snapshot)
            {
                if (!ev.WhenLocal.HasValue || !IsUsdMediumOrHigh(ev)) continue;
                double mins = (ev.WhenLocal.Value - now).TotalMinutes;
                if (mins >= 0 && mins <= lookaheadMinutes && mins < bestMins) { best = ev; bestMins = mins; }
            }
            if (best == null) return false;
            title = best.Title; impact = best.Impact; minutesUntil = (int)Math.Ceiling(bestMins);
            return true;
        }

        // most recent qualifying release within the last 'lookbackMinutes' -- for the informational-only note
        public bool TryGetRecentNews(double lookbackMinutes, out string title, out string impact)
        {
            title = null; impact = null;
            DateTime now = DateTime.Now;
            List<NewsEvent> snapshot;
            lock (_newsLock) snapshot = _newsEvents;
            NewsEvent best = null; double bestMins = double.MaxValue;
            foreach (NewsEvent ev in snapshot)
            {
                if (!ev.WhenLocal.HasValue || !IsUsdMediumOrHigh(ev)) continue;
                double mins = (now - ev.WhenLocal.Value).TotalMinutes;
                if (mins >= 0 && mins <= lookbackMinutes && mins < bestMins) { best = ev; bestMins = mins; }
            }
            if (best == null) return false;
            title = best.Title; impact = best.Impact;
            return true;
        }

        public static void Log(string msg)
        {
            try { Output.Process("[SneakyATM] " + msg, PrintTo.OutputTab1); } catch { }
        }

        // ───────── paths / persistence ─────────
        private static string Dir()
        {
            string d = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SneakyATM");
            try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
            return d;
        }

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
                        case "account":      AccountName = v; break;
                        case "goal":         { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) DailyGoal = d; } break;
                        case "risk":         { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) DailyRiskLimit = d; } break;
                        case "riskpertrade": { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) RiskPerTrade = d; } break;
                        case "maxtrades":    { int n; if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) MaxTradesPerDay = n; } break;
                        case "reset":        { TimeSpan t; if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out t)) ResetTime = t; } break;
                        case "run":          { int n; if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ConsecutiveRun = Math.Max(2, n); } break;
                        case "stopmult":     { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) StopAtrMult = d; } break;
                        case "scalpfrac":    { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) ScalpStopFraction = d; } break;
                        case "minfrac":      { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) MinStopFraction = d; } break;
                        case "chartkey":     LastChartKey = v; break;
                        case "volspike":     { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) VolumeSpikeMultiple = d; } break;
                        case "priorleveltol":{ double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) PriorLevelToleranceTicks = d; } break;
                        case "pacepct":      { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) PaceToGoalThresholdPct = d; } break;
                        case "quietstart":   { TimeSpan t; if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out t)) QuietStart = t; } break;
                        case "quietend":     { TimeSpan t; if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out t)) QuietEnd = t; } break;
                        case "daystart":     { TimeSpan t; if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out t)) DaySessionStart = t; } break;
                        case "dayend":       { TimeSpan t; if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out t)) DaySessionEnd = t; } break;
                        case "newspremin":   { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) NewsPreReleaseMinutes = d; } break;
                        case "newspostmin":  { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) NewsPostReleaseMinutes = d; } break;
                        case "profile":      ActiveProfile = v; break;
                    }
                }
            }
            catch (Exception ex) { Log("LoadConfig: " + ex.Message); }
        }

        public void SaveConfig()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                lock (_lock)
                {
                    sb.AppendLine("account=" + AccountName);
                    sb.AppendLine("goal=" + DailyGoal.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("risk=" + DailyRiskLimit.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("riskpertrade=" + RiskPerTrade.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("maxtrades=" + MaxTradesPerDay.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("reset=" + ResetTime.ToString(@"hh\:mm"));
                    sb.AppendLine("run=" + ConsecutiveRun.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("stopmult=" + StopAtrMult.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("scalpfrac=" + ScalpStopFraction.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("minfrac=" + MinStopFraction.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("chartkey=" + LastChartKey);
                    sb.AppendLine("volspike=" + VolumeSpikeMultiple.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("priorleveltol=" + PriorLevelToleranceTicks.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("pacepct=" + PaceToGoalThresholdPct.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("quietstart=" + QuietStart.ToString(@"hh\:mm"));
                    sb.AppendLine("quietend=" + QuietEnd.ToString(@"hh\:mm"));
                    sb.AppendLine("daystart=" + DaySessionStart.ToString(@"hh\:mm"));
                    sb.AppendLine("dayend=" + DaySessionEnd.ToString(@"hh\:mm"));
                    sb.AppendLine("newspremin=" + NewsPreReleaseMinutes.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("newspostmin=" + NewsPostReleaseMinutes.ToString(CultureInfo.InvariantCulture));
                    sb.AppendLine("profile=" + ActiveProfile);
                }
                File.WriteAllText(Path.Combine(Dir(), "config.txt"), sb.ToString());
            }
            catch (Exception ex) { Log("SaveConfig: " + ex.Message); }
        }

        // ── named settings profiles (e.g. "Scalper" vs "Day Trader") ──────────
        // Each profile is one pipe-joined line of the same settings SaveConfig/LoadConfig persist,
        // stored in its own file keyed by name, so you can save the current DAILY/ADVISOR SETTINGS
        // under a name and switch back to it later (or for a different instrument) without retyping
        // every field. Matches the real product's per-instrument named presets as closely as this
        // codebase's existing persistence style allows.
        private string CaptureSettingsLine()
        {
            return string.Join("|", new[]
            {
                DailyGoal.ToString(CultureInfo.InvariantCulture),
                DailyRiskLimit.ToString(CultureInfo.InvariantCulture),
                RiskPerTrade.ToString(CultureInfo.InvariantCulture),
                MaxTradesPerDay.ToString(CultureInfo.InvariantCulture),
                ResetTime.ToString(@"hh\:mm"),
                ConsecutiveRun.ToString(CultureInfo.InvariantCulture),
                StopAtrMult.ToString(CultureInfo.InvariantCulture),
                ScalpStopFraction.ToString(CultureInfo.InvariantCulture),
                MinStopFraction.ToString(CultureInfo.InvariantCulture),
                VolumeSpikeMultiple.ToString(CultureInfo.InvariantCulture),
                PriorLevelToleranceTicks.ToString(CultureInfo.InvariantCulture),
                PaceToGoalThresholdPct.ToString(CultureInfo.InvariantCulture),
                QuietStart.ToString(@"hh\:mm"),
                QuietEnd.ToString(@"hh\:mm"),
                DaySessionStart.ToString(@"hh\:mm"),
                DaySessionEnd.ToString(@"hh\:mm"),
                NewsPreReleaseMinutes.ToString(CultureInfo.InvariantCulture),
                NewsPostReleaseMinutes.ToString(CultureInfo.InvariantCulture),
            });
        }

        private void ApplySettingsLine(string line)
        {
            string[] parts = (line ?? "").Split('|');
            if (parts.Length < 16) return;   // 16 is the ORIGINAL field count -- keep this at 16 (not 18) so profiles
                                              // saved before NewsPreReleaseMinutes/NewsPostReleaseMinutes still apply;
                                              // those two are read conditionally below, just like every other field.
            double d; int n; TimeSpan t;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) DailyGoal = d;
            if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) DailyRiskLimit = d;
            if (double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) RiskPerTrade = d;
            if (int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) MaxTradesPerDay = n;
            if (TimeSpan.TryParse(parts[4], CultureInfo.InvariantCulture, out t)) ResetTime = t;
            if (int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) ConsecutiveRun = n;
            if (double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) StopAtrMult = d;
            if (double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) ScalpStopFraction = d;
            if (double.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) MinStopFraction = d;
            if (double.TryParse(parts[9], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) VolumeSpikeMultiple = d;
            if (double.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) PriorLevelToleranceTicks = d;
            if (double.TryParse(parts[11], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) PaceToGoalThresholdPct = d;
            if (TimeSpan.TryParse(parts[12], CultureInfo.InvariantCulture, out t)) QuietStart = t;
            if (TimeSpan.TryParse(parts[13], CultureInfo.InvariantCulture, out t)) QuietEnd = t;
            if (TimeSpan.TryParse(parts[14], CultureInfo.InvariantCulture, out t)) DaySessionStart = t;
            if (TimeSpan.TryParse(parts[15], CultureInfo.InvariantCulture, out t)) DaySessionEnd = t;
            if (parts.Length > 16 && double.TryParse(parts[16], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) NewsPreReleaseMinutes = d;
            if (parts.Length > 17 && double.TryParse(parts[17], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) NewsPostReleaseMinutes = d;
        }

        private static Dictionary<string, string> LoadProfilesRaw()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string p = Path.Combine(Dir(), "profiles.txt");
                if (!File.Exists(p)) return dict;
                foreach (string line in File.ReadAllLines(p))
                {
                    int i = line.IndexOf('|');
                    if (i <= 0) continue;
                    dict[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch (Exception ex) { Log("LoadProfiles: " + ex.Message); }
            return dict;
        }

        private static void SaveProfilesRaw(Dictionary<string, string> dict)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in dict) sb.AppendLine(kv.Key + "|" + kv.Value);
                File.WriteAllText(Path.Combine(Dir(), "profiles.txt"), sb.ToString());
            }
            catch (Exception ex) { Log("SaveProfiles: " + ex.Message); }
        }

        public List<string> ProfileNames()
        {
            var names = new List<string>(LoadProfilesRaw().Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public bool ApplyProfile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string line;
            if (!LoadProfilesRaw().TryGetValue(name, out line)) return false;
            ApplySettingsLine(line);
            ActiveProfile = name;
            SaveConfig();
            return true;
        }

        public void SaveCurrentAsProfile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var dict = LoadProfilesRaw();
            dict[name] = CaptureSettingsLine();
            SaveProfilesRaw(dict);
            ActiveProfile = name;
            SaveConfig();
        }

        public void DeleteProfile(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)) return;
            var dict = LoadProfilesRaw();
            if (dict.Remove(name))
            {
                SaveProfilesRaw(dict);
                if (string.Equals(ActiveProfile, name, StringComparison.OrdinalIgnoreCase))
                {
                    ActiveProfile = "Default";
                    SaveConfig();
                }
            }
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
                        else if (k.StartsWith("base."))   { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) _baseline[k.Substring(5)] = d; }
                        else if (k.StartsWith("trades.")) { int n;    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) _tradesToday[k.Substring(7)] = n; }
                        else if (k.StartsWith("wins."))   { int n;    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) _winsToday[k.Substring(5)] = n; }
                        else if (k.StartsWith("losses.")) { int n;    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) _lossesToday[k.Substring(7)] = n; }
                        else if (k.StartsWith("sumr."))   { double d; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) _sumRToday[k.Substring(5)] = d; }
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
                    foreach (KeyValuePair<string, int> kv in _tradesToday)
                        sb.AppendLine("trades." + kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
                    foreach (KeyValuePair<string, int> kv in _winsToday)
                        sb.AppendLine("wins." + kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
                    foreach (KeyValuePair<string, int> kv in _lossesToday)
                        sb.AppendLine("losses." + kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
                    foreach (KeyValuePair<string, double> kv in _sumRToday)
                        sb.AppendLine("sumr." + kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture));
                }
                File.WriteAllText(Path.Combine(Dir(), "state.txt"), sb.ToString());
            }
            catch (Exception ex) { Log("SaveState: " + ex.Message); }
        }

        // ───────── account helpers ─────────
        public static List<string> AccountNames()
        {
            var l = new List<string>();
            try { lock (Account.All) foreach (Account a in Account.All) if (a != null && !string.IsNullOrEmpty(a.Name)) l.Add(a.Name); }
            catch { }
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }

        public static Account FindAccount(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try { lock (Account.All) foreach (Account a in Account.All) if (a != null && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a; }
            catch { }
            return null;
        }

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

        // ───────── 1s housekeeping: day rollover, baseline ─────────
        private void Tick()
        {
            try
            {
                DateTime now = DateTime.Now;
                string day = (now - ResetTime).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                bool dirty = false;
                lock (_lock)
                {
                    if (day != _dayKey)
                    {
                        _dayKey = day;
                        _baseline.Clear();
                        _tradesToday.Clear();
                        _winsToday.Clear();
                        _lossesToday.Clear();
                        _sumRToday.Clear();
                        dirty = true;
                        Log("New trading day " + day + " — goal/risk/trade/score counters reset.");
                    }
                    string acct = AccountName;
                    if (!string.IsNullOrEmpty(acct) && !_baseline.ContainsKey(acct))
                    {
                        Account a = FindAccount(acct);
                        double pnl = a == null ? double.NaN : AcctPnl(a);
                        if (!double.IsNaN(pnl)) { _baseline[acct] = pnl; dirty = true; }
                    }
                }
                if (dirty) SaveState();
            }
            catch (Exception ex) { Log("Tick: " + ex.Message); }
        }

        // ───────── public read/write API for the window ─────────
        public double DailyPnl(string acct)
        {
            if (string.IsNullOrEmpty(acct)) return 0;
            Account a = FindAccount(acct);
            if (a == null) return 0;
            double pnl = AcctPnl(a);
            if (double.IsNaN(pnl)) return 0;
            lock (_lock)
            {
                double baseline;
                if (!_baseline.TryGetValue(acct, out baseline)) { _baseline[acct] = pnl; return 0; }
                return pnl - baseline;
            }
        }

        public int TradesToday(string acct)
        {
            if (string.IsNullOrEmpty(acct)) return 0;
            lock (_lock) { int n; return _tradesToday.TryGetValue(acct, out n) ? n : 0; }
        }

        public void RegisterTradeSubmitted(string acct)
        {
            if (string.IsNullOrEmpty(acct)) return;
            lock (_lock)
            {
                int n;
                _tradesToday.TryGetValue(acct, out n);
                _tradesToday[acct] = n + 1;
            }
            SaveState();
        }

        public void RegisterTradeResult(string acct, bool win, double rMultiple)
        {
            if (string.IsNullOrEmpty(acct)) return;
            lock (_lock)
            {
                int n;
                if (win) { _winsToday.TryGetValue(acct, out n); _winsToday[acct] = n + 1; }
                else     { _lossesToday.TryGetValue(acct, out n); _lossesToday[acct] = n + 1; }
                double r;
                _sumRToday.TryGetValue(acct, out r);
                _sumRToday[acct] = r + rMultiple;
            }
            SaveState();
        }

        public void Score(string acct, out int wins, out int losses, out double sumR)
        {
            wins = 0; losses = 0; sumR = 0;
            if (string.IsNullOrEmpty(acct)) return;
            lock (_lock)
            {
                _winsToday.TryGetValue(acct, out wins);
                _lossesToday.TryGetValue(acct, out losses);
                _sumRToday.TryGetValue(acct, out sumR);
            }
        }

        public double RemainingRiskBudget(string acct)
        {
            double lossSoFar = Math.Max(0, -DailyPnl(acct));
            return Math.Max(0, DailyRiskLimit - lossSoFar);
        }

        // returns null if not locked out, else the reason text -- NEVER overridable
        public string LockoutReason(string acct)
        {
            double pnl = DailyPnl(acct);
            if (DailyGoal > 0 && pnl >= DailyGoal) return "Daily goal reached (+" + pnl.ToString("0.00", CultureInfo.InvariantCulture) + ") — locked for the rest of the day.";
            if (DailyRiskLimit > 0 && -pnl >= DailyRiskLimit) return "Daily risk limit hit (" + pnl.ToString("0.00", CultureInfo.InvariantCulture) + ") — locked for the rest of the day.";
            if (MaxTradesPerDay > 0 && TradesToday(acct) >= MaxTradesPerDay) return "Max trades for the day reached (" + TradesToday(acct) + ") — locked for the rest of the day.";
            return null;
        }

        public TimeSpan TimeUntilReset()
        {
            DateTime now = DateTime.Now;
            DateTime next = now.Date + ResetTime;
            if (next <= now) next = next.AddDays(1);
            return next - now;
        }
    }

    // ───────────────────────────── Window ─────────────────────────────
    public class SneakyAtmWindow : NTWindow
    {
        private enum Tier { Quick, Medium, Large, HomeRun }

        private sealed class TierDef
        {
            public Tier   Id;
            public string Label;
            public double RMultiple;     // of whichever stop this tier's group ends up using
            public bool   Shared;        // true = Medium/Large/HomeRun's shared ATR stop group; false = Quick's own scalp stop
        }

        private static readonly TierDef[] Tiers = new[]
        {
            new TierDef { Id = Tier.Quick,   Label = "QUICK HIT",  RMultiple = 1.0, Shared = false },
            new TierDef { Id = Tier.Medium,  Label = "MEDIUM HIT", RMultiple = 1.0, Shared = true  },
            new TierDef { Id = Tier.Large,   Label = "LARGE HIT",  RMultiple = 1.5, Shared = true  },
            new TierDef { Id = Tier.HomeRun, Label = "HOME RUN",   RMultiple = 2.0, Shared = true  },
        };

        private sealed class Sizing
        {
            public int    Qty;
            public double StopTicks;      // the stop actually used (possibly tightened)
            public bool   Tightened;
            public bool   Skip;
            public bool   ManualClamped;  // manual sizing asked for more than the daily-loss-limit ceiling allows and got capped down
            public int    ManualRequestedQty;
        }

        private sealed class Pending
        {
            public string     AcctName;
            public Account    Acct;
            public Instrument Instr;
            public int        Dir;       // +1 long, -1 short
            public int        Qty;
            public double     StopTicks;
            public double     TargetTicks;
            public double     TierRMultiple;
            public Order      EntryOrder;
            public Order      StopOrder;
            public Order      TargetOrder;
            public double     EntryAvg;
            public DateTime   StartedUtc;
            public bool       BracketPlaced;
            public bool       BeMoved;
        }

        // theme (same palette convention as SneakyCockpit — see ApplyTheme)
        private Brush Bg = new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x17));
        private Brush Card = new SolidColorBrush(Color.FromRgb(0x11, 0x17, 0x26));
        private Brush Edge = new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x3D));
        private Brush Text = new SolidColorBrush(Color.FromRgb(0xE9, 0xEE, 0xF7));
        private Brush Ink2 = new SolidColorBrush(Color.FromRgb(0xAE, 0xBA, 0xCE));
        private Brush Muted = new SolidColorBrush(Color.FromRgb(0x6C, 0x7A, 0x92));
        private Brush Accent = new SolidColorBrush(Color.FromRgb(0x3F, 0xD1, 0xE0));
        private Brush Green = new SolidColorBrush(Color.FromRgb(0x25, 0xD0, 0x8B));
        private Brush Red = new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x6A));
        private Brush Amber = new SolidColorBrush(Color.FromRgb(0xF2, 0xB3, 0x4C));

        // tier button colors (own palette, not copied from any third-party product)
        private static readonly Brush TQuick  = new SolidColorBrush(Color.FromRgb(0x2F, 0x8F, 0xE0));
        private static readonly Brush TMedium = new SolidColorBrush(Color.FromRgb(0xD9, 0xA7, 0x2C));
        private static readonly Brush TLarge  = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));
        private static readonly Brush THome   = new SolidColorBrush(Colors.White);

        private static SolidColorBrush FB(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        // SneakySkin is an OPTIONAL shared theme class from the rest of the Sneaky Suite -- it is
        // deliberately NOT referenced by type here (and not required in this file's install zip).
        // Standalone installs (just this file + SneakyAtmLink) compile and run fine on the hardcoded
        // palette fields declared above; if SneakySkin.cs also happens to be present in the same
        // NinjaScript project (the full Sneaky Suite install), it's picked up here via reflection so
        // this window matches that shared theme instead. Never a compile-time dependency either way.
        private static bool _sneakySkinSearched;
        private static Type _sneakySkinType;
        private static Type FindSneakySkinType()
        {
            if (_sneakySkinSearched) return _sneakySkinType;
            _sneakySkinSearched = true;
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }
                    Type hit = types.FirstOrDefault(x => x.Name == "SneakySkin");
                    if (hit != null) { _sneakySkinType = hit; break; }
                }
            }
            catch { }
            return _sneakySkinType;
        }

        private static Color SkinColor(Type t, string member, Color fallback)
        {
            try
            {
                FieldInfo f = t.GetField(member, BindingFlags.Public | BindingFlags.Static);
                if (f != null && f.FieldType == typeof(Color)) return (Color)f.GetValue(null);
                PropertyInfo p = t.GetProperty(member, BindingFlags.Public | BindingFlags.Static);
                if (p != null && p.PropertyType == typeof(Color)) return (Color)p.GetValue(null, null);
            }
            catch { }
            return fallback;
        }

        private void ApplyTheme()
        {
            try
            {
                Type skin = FindSneakySkinType();
                if (skin == null) return;   // no shared theme installed -- keep our own hardcoded palette

                try
                {
                    MethodInfo refresh = skin.GetMethod("MaybeRefreshTheme", BindingFlags.Public | BindingFlags.Static);
                    if (refresh != null) refresh.Invoke(null, null);
                }
                catch { }

                Bg     = FB(SkinColor(skin, "KVoid",   ((SolidColorBrush)Bg).Color));
                Card   = FB(SkinColor(skin, "KPanel",  ((SolidColorBrush)Card).Color));
                Edge   = FB(SkinColor(skin, "KLine",   ((SolidColorBrush)Edge).Color));
                Text   = FB(SkinColor(skin, "KInk",    ((SolidColorBrush)Text).Color));
                Ink2   = FB(SkinColor(skin, "KInk2",   ((SolidColorBrush)Ink2).Color));
                Muted  = FB(SkinColor(skin, "KMute",   ((SolidColorBrush)Muted).Color));
                Accent = FB(SkinColor(skin, "KAccent", ((SolidColorBrush)Accent).Color));
                Green  = FB(SkinColor(skin, "KUp",     ((SolidColorBrush)Green).Color));
                Red    = FB(SkinColor(skin, "KDown",   ((SolidColorBrush)Red).Color));
                Amber  = FB(SkinColor(skin, "KWarn",   ((SolidColorBrush)Amber).Color));
            }
            catch { }
        }

        private ComboBox   _account, _chart, _profile;
        private TextBox    _goal, _risk, _riskPerTrade, _maxTrades, _reset, _run;
        private TextBox    _volSpike, _priorTol, _pacePct, _quietStart, _quietEnd, _dayStart, _dayEnd, _newsPre, _newsPost;
        private TextBlock  _subtitle, _infoNotes;
        private TextBlock  _pnlLine, _limitsLine, _budgetLine, _goalLine, _scoreLine, _resetLine, _lockBanner, _newsStatusLine;
        private Border     _previewLongBtn, _previewShortBtn, _cancelPreviewBtn;
        private StackPanel  _previewChoiceRow;
        private TextBlock  _dirLabel, _summaryQuick, _summaryShared, _beNote, _tightenNote, _advisorBanner, _manualRiskBanner;
        private StackPanel _previewBody;
        private Border     _advisorOverrideChk, _sizingOverrideChk;
        private TextBlock  _advisorOverrideGlyph, _sizingOverrideGlyph, _qtyVal;
        private bool       _advisorOverride;
        private bool       _sizingOverride;
        private int        _manualQty = 1;
        private int?       _previewDir;   // null = no preview, +1 long, -1 short
        private StackPanel _tierBody;
        private double     _lastQuickTargetDollar;   // Quick tier's own $ target, for the "wins needed" info note
        private bool       _lastQuickAvailable;

        // On-chart tier lines: the entry price tracks the live market every 400ms refresh while a
        // preview is open (see Refresh()), and only freezes once an order is actually in flight --
        // at that point the real bracket is built from the actual fill price anyway, not this one.
        // _tierPlanChartKey remembers which chart currently has a published plan so it can be
        // cleared if the preview is canceled or the watched chart is switched mid-preview.
        private double     _previewEntryPrice;
        private string     _tierPlanChartKey;

        // Which tier row (if any) the mouse is currently over -- while set, the chart isolates down
        // to just that tier's own stop + target line instead of showing every tier's target at once
        // (see BuildTierRows). Cleared the moment the mouse leaves that row, OR BuildTierRows itself
        // doesn't re-add this tier (preview canceled, chart switched, tier now skipped).
        private Tier? _hoveredTier;

        private string _lastAccountSeen;        // for the one-shot "Account switched (from X)" info note
        private string _accountJustSwitchedFrom; // non-null once a switch is detected; shown until the NEXT switch
        private TextBlock _tradePnlLine;   // live unrealized P/L of the CURRENT open trade, shown only while one is open

        private Pending         _pending;
        private DispatcherTimer _timer;

        public SneakyAtmWindow()
        {
            Caption = "Sneaky ATM";
            Width = 360; Height = 760;

            ApplyTheme();
            var eng = SneakyAtmEngine.Instance;
            Content = BuildLayout();
            if (eng != null)
            {
                LoadSettingsFieldsFromEngine(eng);
                if (!string.IsNullOrEmpty(eng.AccountName)) _account.Text = eng.AccountName;
            }

            RefreshAccounts();
            RefreshCharts();
            RefreshProfiles();
            UpdatePreviewUi();

            _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Closed += (s, e) => { try { _timer.Stop(); } catch { } ClearTierPlan(); };

            Refresh();
        }

        // ── layout ───────────────────────────────────────────────────────────
        private FrameworkElement BuildLayout()
        {
            var root = new ScrollViewer { Background = Bg, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new StackPanel { Margin = new Thickness(10) };
            root.Content = body;

            var title = new TextBlock { Text = "SNEAKY ATM", Foreground = Text, FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) };
            body.Children.Add(title);

            _subtitle = new TextBlock { Foreground = Ink2, FontSize = 10.5, Margin = new Thickness(0, 0, 0, 6) };
            body.Children.Add(_subtitle);

            body.Children.Add(Section("ACCOUNT"));
            _account = Combo();
            _account.SelectionChanged += (s, e) => OnAccountChanged();
            body.Children.Add(_account);

            body.Children.Add(Section("WATCHING (drop SneakyAtmLink on a chart to add it here)"));
            _chart = Combo();
            _chart.SelectionChanged += (s, e) => OnChartChanged();
            body.Children.Add(_chart);

            body.Children.Add(Section("PROFILE (named settings presets, e.g. \"Scalper\" vs \"Day Trader\")"));
            _profile = Combo();
            _profile.SelectionChanged += (s, e) => OnProfileSelected();
            body.Children.Add(_profile);
            var profBtnRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            profBtnRow.ColumnDefinitions.Add(new ColumnDefinition()); profBtnRow.ColumnDefinitions.Add(new ColumnDefinition());
            var saveProfBtn = Button("Save as profile", Accent); Grid.SetColumn(saveProfBtn, 0); saveProfBtn.Margin = new Thickness(0, 0, 4, 0);
            saveProfBtn.MouseLeftButtonUp += (s, e) => { if (SaveCurrentProfile()) FlashButtonText(saveProfBtn, "Saved ✓"); };
            var delProfBtn = Button("Delete profile", Red); Grid.SetColumn(delProfBtn, 1); delProfBtn.Margin = new Thickness(4, 0, 0, 0);
            delProfBtn.MouseLeftButtonUp += (s, e) => DeleteCurrentProfile();
            profBtnRow.Children.Add(saveProfBtn); profBtnRow.Children.Add(delProfBtn);
            body.Children.Add(profBtnRow);

            // status block
            body.Children.Add(Section("STATUS"));
            _pnlLine = StatusLine();   body.Children.Add(_pnlLine);
            _limitsLine = StatusLine(); body.Children.Add(_limitsLine);
            _budgetLine = StatusLine(); body.Children.Add(_budgetLine);
            _goalLine = StatusLine();  body.Children.Add(_goalLine);
            _scoreLine = StatusLine(); body.Children.Add(_scoreLine);
            _resetLine = StatusLine(); body.Children.Add(_resetLine);
            _newsStatusLine = StatusLine(); _newsStatusLine.FontFamily = new FontFamily("Segoe UI"); _newsStatusLine.FontSize = 10;
            body.Children.Add(_newsStatusLine);

            _lockBanner = Banner(Red);
            _lockBanner.Visibility = Visibility.Collapsed;
            body.Children.Add(_lockBanner);

            // Standing info notes -- account-switch, candle-burst, day/overnight, wins-needed. Lives
            // outside _previewBody deliberately: unlike the OBSERVATIONS advisor banner (which needs
            // a chosen LONG/SHORT direction to talk about), these are all direction-agnostic and
            // should show up even before you've picked PREVIEW LONG/SHORT.
            _infoNotes = new TextBlock { Foreground = Ink2, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            _infoNotes.Visibility = Visibility.Collapsed;
            body.Children.Add(_infoNotes);

            // config
            body.Children.Add(Section("DAILY SETTINGS"));
            var cfg = new Grid();
            for (int i = 0; i < 2; i++) cfg.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 3; i++) cfg.RowDefinitions.Add(new RowDefinition());
            _goal = ConfigField(cfg, 0, 0, "Profit lock $");
            _risk = ConfigField(cfg, 0, 1, "Loss limit $");
            _riskPerTrade = ConfigField(cfg, 1, 0, "Risk/trade $");
            _maxTrades = ConfigField(cfg, 1, 1, "Max trades/day");
            _reset = ConfigField(cfg, 2, 0, "Reset (HH:mm)");
            _run = ConfigField(cfg, 2, 1, "Pullback run (N)");
            body.Children.Add(cfg);

            body.Children.Add(Section("ADVISOR SETTINGS"));
            var adv = new Grid();
            for (int i = 0; i < 2; i++) adv.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 5; i++) adv.RowDefinitions.Add(new RowDefinition());
            _volSpike   = ConfigField(adv, 0, 0, "Volume spike x");
            _priorTol   = ConfigField(adv, 0, 1, "Prior-level tol (ticks)");
            _quietStart = ConfigField(adv, 1, 0, "Quiet start (HH:mm)");
            _quietEnd   = ConfigField(adv, 1, 1, "Quiet end (HH:mm)");
            _pacePct    = ConfigField(adv, 2, 0, "Pace-to-goal warn %");
            _dayStart   = ConfigField(adv, 2, 1, "Day session start (HH:mm)");
            _dayEnd     = ConfigField(adv, 3, 0, "Day session end (HH:mm)");
            _newsPre    = ConfigField(adv, 3, 1, "News pre-release (min)");
            _newsPost   = ConfigField(adv, 4, 0, "News post-release (min)");
            body.Children.Add(adv);

            var saveBtn = Button("Save settings", Accent);
            saveBtn.Margin = new Thickness(0, 6, 0, 10);
            saveBtn.MouseLeftButtonUp += (s, e) => { SaveSettings(); FlashButtonText(saveBtn, "Saved ✓"); };
            body.Children.Add(saveBtn);

            // preview choice
            _previewChoiceRow = new StackPanel();
            var dirRow = new Grid();
            dirRow.ColumnDefinitions.Add(new ColumnDefinition()); dirRow.ColumnDefinitions.Add(new ColumnDefinition());
            _previewLongBtn = PreviewButton("PREVIEW LONG", Green, 0, dirRow);
            _previewShortBtn = PreviewButton("PREVIEW SHORT", Red, 1, dirRow);
            _previewLongBtn.MouseLeftButtonUp += (s, e) => StartPreview(1);
            _previewShortBtn.MouseLeftButtonUp += (s, e) => StartPreview(-1);
            _previewChoiceRow.Children.Add(dirRow);
            body.Children.Add(_previewChoiceRow);

            _cancelPreviewBtn = Button("CANCEL PREVIEW", Muted);
            _cancelPreviewBtn.MouseLeftButtonUp += (s, e) => CancelPreview();
            body.Children.Add(_cancelPreviewBtn);

            // preview body (direction label, stop/risk summary, BE note, advisor, sizing controls, tiers)
            _previewBody = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            body.Children.Add(_previewBody);

            _dirLabel = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(0, 4, 0, 2) };
            _previewBody.Children.Add(_dirLabel);
            _tradePnlLine = new TextBlock { FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.SemiBold, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed };
            _previewBody.Children.Add(_tradePnlLine);
            _summaryQuick = new TextBlock { Foreground = Ink2, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
            _previewBody.Children.Add(_summaryQuick);
            _summaryShared = new TextBlock { Foreground = Ink2, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
            _previewBody.Children.Add(_summaryShared);
            _beNote = new TextBlock { Text = "BE at 1R (stop moves to entry once price reaches +1R).", Foreground = Muted, FontSize = 10, Margin = new Thickness(0, 0, 0, 2) };
            _previewBody.Children.Add(_beNote);
            _tightenNote = new TextBlock { Foreground = Amber, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            _tightenNote.Visibility = Visibility.Collapsed;
            _previewBody.Children.Add(_tightenNote);

            _previewBody.Children.Add(Section("OBSERVATIONS"));
            _advisorBanner = Banner(Amber);
            _advisorBanner.Visibility = Visibility.Collapsed;
            _previewBody.Children.Add(_advisorBanner);

            var ovRow1 = CheckRow(out _advisorOverrideChk, out _advisorOverrideGlyph, "Override OBSERVATIONS warnings for this session");
            _advisorOverrideChk.MouseLeftButtonUp += (s, e) => { _advisorOverride = !_advisorOverride; _advisorOverrideGlyph.Text = _advisorOverride ? "✓" : ""; Refresh(); };
            _previewBody.Children.Add(ovRow1);

            // manual sizing override
            _previewBody.Children.Add(Section("SIZING"));
            var sizeRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var minus = Button("−", Ink2); minus.Width = 32; Grid.SetColumn(minus, 0);
            minus.MouseLeftButtonUp += (s, e) => { _manualQty = Math.Max(1, _manualQty - 1); Refresh(); };
            sizeRow.Children.Add(minus);
            _qtyVal = new TextBlock { Text = "AUTO", Foreground = Text, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0) };
            Grid.SetColumn(_qtyVal, 1); sizeRow.Children.Add(_qtyVal);
            var plus = Button("+", Ink2); plus.Width = 32; Grid.SetColumn(plus, 2); plus.HorizontalAlignment = HorizontalAlignment.Left;
            plus.MouseLeftButtonUp += (s, e) => { _manualQty = _manualQty + 1; Refresh(); };
            sizeRow.Children.Add(plus);
            var autoBtn = Button("AUTO", Accent); Grid.SetColumn(autoBtn, 3);
            autoBtn.MouseLeftButtonUp += (s, e) => { _sizingOverride = false; UpdateSizingOverrideGlyph(); Refresh(); };
            sizeRow.Children.Add(autoBtn);
            _previewBody.Children.Add(sizeRow);

            var ovRow2 = CheckRow(out _sizingOverrideChk, out _sizingOverrideGlyph, "Override sizing — use CONTRACTS above instead of auto");
            _sizingOverrideChk.MouseLeftButtonUp += (s, e) => { _sizingOverride = !_sizingOverride; UpdateSizingOverrideGlyph(); Refresh(); };
            _previewBody.Children.Add(ovRow2);

            // Manual sizing bypasses the Risk/trade $ cap entirely (full nominal stop, whatever qty
            // you pick) -- this banner is the only warning you get that the cap isn't protecting you
            // while it's on. See ComputeSizing's manualOverride branch.
            _manualRiskBanner = Banner(Red);
            _manualRiskBanner.Visibility = Visibility.Collapsed;
            _previewBody.Children.Add(_manualRiskBanner);

            // tiers
            _previewBody.Children.Add(Section("PICK YOUR TARGET"));
            _tierBody = new StackPanel();
            _previewBody.Children.Add(_tierBody);

            var disclaimer = new TextBlock { Text = "Observations describe recent price action only. Not financial advice.",
                Foreground = Muted, FontSize = 9.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            body.Children.Add(disclaimer);

            return root;
        }

        private Border CheckRow(out Border chk, out TextBlock glyph, string label)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            Border c = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Background = Card,
                BorderBrush = Edge, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            TextBlock g = new TextBlock { Text = "", Foreground = Amber, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
            c.Child = g;
            Grid.SetColumn(c, 0); row.Children.Add(c);
            var lbl = new TextBlock { Text = label, Foreground = Ink2, FontSize = 10.5, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lbl, 1); row.Children.Add(lbl);
            chk = c; glyph = g;
            // wrap in a Border so callers can add the Grid as a single child -- just return a dummy container
            var holder = new Border { Child = row };
            return holder;
        }

        private TextBlock Section(string text)
        {
            return new TextBlock { Text = text, Foreground = Muted, FontSize = 10, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 3) };
        }

        private ComboBox Combo()
        {
            return new ComboBox { IsEditable = true, FontFamily = new FontFamily("Consolas"), FontSize = 11, Height = 24 };
        }

        private TextBox ConfigField(Grid g, int row, int col, string label)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, col == 0 ? 6 : 0, 6) };
            cell.Children.Add(new TextBlock { Text = label, Foreground = Ink2, FontSize = 10, Margin = new Thickness(0, 0, 0, 2) });
            var tb = new TextBox { Background = Card, Foreground = Text, BorderBrush = Edge, BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(4, 2, 4, 2) };
            cell.Children.Add(tb);
            Grid.SetRow(cell, row); Grid.SetColumn(cell, col);
            g.Children.Add(cell);
            return tb;
        }

        private TextBlock StatusLine()
        {
            return new TextBlock { Foreground = Text, FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 2) };
        }

        private TextBlock Banner(Brush color)
        {
            return new TextBlock { Foreground = color, FontSize = 11, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4), Background = Card };
        }

        private Border PreviewButton(string text, Brush accent, int col, Grid row)
        {
            var b = new Border { Height = 34, CornerRadius = new CornerRadius(6), Background = accent,
                Margin = new Thickness(col == 0 ? 0 : 4, 0, col == 0 ? 4 : 0, 0), Cursor = Cursors.Hand };
            b.Child = new TextBlock { Text = text, Foreground = FB(Colors.Black), FontWeight = FontWeights.Bold, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(b, col); row.Children.Add(b);
            return b;
        }

        private Border Button(string text, Brush accent)
        {
            var b = new Border { Height = 28, CornerRadius = new CornerRadius(6), Background = Card, BorderBrush = accent,
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
            b.Child = new TextBlock { Text = text, Foreground = accent, FontWeight = FontWeights.SemiBold, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return b;
        }

        // Momentarily swaps a Button()-built border's label to a confirmation message, then
        // restores its original text. "Save settings" and "Save as profile" both silently write
        // to disk with zero other visible change on success (no dropdown change, no status-line
        // update) -- this is the only outward sign either one actually did something.
        private void FlashButtonText(Border btn, string confirmText, int milliseconds = 900)
        {
            var tb = btn.Child as TextBlock;
            if (tb == null) return;
            string original = tb.Text;
            tb.Text = confirmText;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            t.Tick += (s, e) => { tb.Text = original; t.Stop(); };
            t.Start();
        }

        private void StartPreview(int dir)
        {
            _previewDir = dir;
            var link = SneakyAtmLinkRegistry.TryGet(_chart.Text);
            _previewEntryPrice = link != null ? CurrentPrice(link.Instrument) : 0;
            UpdatePreviewUi();
            Refresh();
        }

        private void CancelPreview()
        {
            ClearTierPlan();
            _previewDir = null;
            _previewEntryPrice = 0;
            UpdatePreviewUi();
            Refresh();
        }

        private void ClearTierPlan()
        {
            if (!string.IsNullOrEmpty(_tierPlanChartKey))
            {
                SneakyAtmLinkRegistry.ClearTierPlan(_tierPlanChartKey);
                _tierPlanChartKey = null;
            }
        }

        private void UpdatePreviewUi()
        {
            bool previewing = _previewDir.HasValue;
            _previewChoiceRow.Visibility = previewing ? Visibility.Collapsed : Visibility.Visible;
            _cancelPreviewBtn.Visibility = previewing ? Visibility.Visible : Visibility.Collapsed;
            _previewBody.Visibility = previewing ? Visibility.Visible : Visibility.Collapsed;
            if (previewing)
            {
                _dirLabel.Text = _previewDir.Value > 0 ? "LONG" : "SHORT";
                _dirLabel.Foreground = _previewDir.Value > 0 ? Green : Red;
            }
        }

        private void UpdateSizingOverrideGlyph()
        {
            _sizingOverrideGlyph.Text = _sizingOverride ? "✓" : "";
        }

        // ── account / chart pickers ─────────────────────────────────────────
        private void RefreshAccounts()
        {
            string cur = _account.Text;
            _account.Items.Clear();
            foreach (string n in SneakyAtmEngine.AccountNames()) _account.Items.Add(n);
            if (!string.IsNullOrEmpty(cur)) _account.Text = cur;
        }

        private void OnAccountChanged()
        {
            var eng = SneakyAtmEngine.Instance;
            if (eng == null) return;
            eng.AccountName = _account.Text ?? "";
            eng.SaveConfig();
        }

        private void RefreshCharts()
        {
            string cur = _chart.Text;
            _chart.Items.Clear();
            foreach (string k in SneakyAtmLinkRegistry.LivePublishers(300)) _chart.Items.Add(k);
            if (!string.IsNullOrEmpty(cur)) _chart.Text = cur;
            else
            {
                var eng = SneakyAtmEngine.Instance;
                if (eng != null && !string.IsNullOrEmpty(eng.LastChartKey)) _chart.Text = eng.LastChartKey;
            }
        }

        private void OnChartChanged()
        {
            // Switching the watched chart mid-preview would otherwise leave stale tier lines
            // drawn on the chart that was just un-selected, anchored to a price level that no
            // longer applies to anything -- simplest correct behavior is to cancel the preview.
            if (_previewDir.HasValue) CancelPreview();

            var eng = SneakyAtmEngine.Instance;
            if (eng == null) return;
            eng.LastChartKey = _chart.Text ?? "";
            eng.SaveConfig();
        }

        // ── named settings profiles ──────────────────────────────────────────
        private void RefreshProfiles()
        {
            var eng = SneakyAtmEngine.Instance;
            string cur = _profile.Text;
            _profile.Items.Clear();
            if (eng != null) foreach (string n in eng.ProfileNames()) _profile.Items.Add(n);
            if (!string.IsNullOrEmpty(cur)) _profile.Text = cur;
            else if (eng != null) _profile.Text = eng.ActiveProfile;
        }

        private void OnProfileSelected()
        {
            var eng = SneakyAtmEngine.Instance;
            if (eng == null) return;
            string name = _profile.Text;
            if (string.IsNullOrWhiteSpace(name)) return;
            if (eng.ApplyProfile(name))
            {
                LoadSettingsFieldsFromEngine(eng);
                Refresh();
            }
        }

        private bool SaveCurrentProfile()
        {
            var eng = SneakyAtmEngine.Instance;
            if (eng == null) return false;
            string name = (_profile.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name)) return false;
            SaveSettings();   // commit whatever's currently typed into the engine's live fields first
            eng.SaveCurrentAsProfile(name);
            RefreshProfiles();
            Refresh();
            return true;
        }

        private void DeleteCurrentProfile()
        {
            var eng = SneakyAtmEngine.Instance;
            if (eng == null) return;
            string name = (_profile.Text ?? "").Trim();
            if (string.IsNullOrEmpty(name) || string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)) return;
            eng.DeleteProfile(name);
            _profile.Text = eng.ActiveProfile;
            RefreshProfiles();
            Refresh();
        }

        private void LoadSettingsFieldsFromEngine(SneakyAtmEngine eng)
        {
            _goal.Text = eng.DailyGoal.ToString("0.##", CultureInfo.InvariantCulture);
            _risk.Text = eng.DailyRiskLimit.ToString("0.##", CultureInfo.InvariantCulture);
            _riskPerTrade.Text = eng.RiskPerTrade.ToString("0.##", CultureInfo.InvariantCulture);
            _maxTrades.Text = eng.MaxTradesPerDay.ToString(CultureInfo.InvariantCulture);
            _reset.Text = eng.ResetTime.ToString(@"hh\:mm");
            _run.Text = eng.ConsecutiveRun.ToString(CultureInfo.InvariantCulture);
            _volSpike.Text = eng.VolumeSpikeMultiple.ToString("0.##", CultureInfo.InvariantCulture);
            _priorTol.Text = eng.PriorLevelToleranceTicks.ToString("0.##", CultureInfo.InvariantCulture);
            _pacePct.Text = eng.PaceToGoalThresholdPct.ToString("0.##", CultureInfo.InvariantCulture);
            _quietStart.Text = eng.QuietStart.ToString(@"hh\:mm");
            _quietEnd.Text = eng.QuietEnd.ToString(@"hh\:mm");
            _dayStart.Text = eng.DaySessionStart.ToString(@"hh\:mm");
            _dayEnd.Text = eng.DaySessionEnd.ToString(@"hh\:mm");
            _newsPre.Text = eng.NewsPreReleaseMinutes.ToString("0.##", CultureInfo.InvariantCulture);
            _newsPost.Text = eng.NewsPostReleaseMinutes.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private void SaveSettings()
        {
            var eng = SneakyAtmEngine.Instance;
            if (eng == null) return;
            double d;
            if (double.TryParse(_goal.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) eng.DailyGoal = Math.Max(0, d);
            if (double.TryParse(_risk.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) eng.DailyRiskLimit = Math.Max(0, d);
            if (double.TryParse(_riskPerTrade.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) eng.RiskPerTrade = Math.Max(0, d);
            int n;
            if (int.TryParse(_maxTrades.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) eng.MaxTradesPerDay = Math.Max(0, n);
            TimeSpan t;
            if (TimeSpan.TryParse(_reset.Text, CultureInfo.InvariantCulture, out t)) eng.ResetTime = t;
            if (int.TryParse(_run.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) eng.ConsecutiveRun = Math.Max(2, n);
            if (double.TryParse(_volSpike.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) eng.VolumeSpikeMultiple = Math.Max(1, d);
            if (double.TryParse(_priorTol.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) eng.PriorLevelToleranceTicks = Math.Max(0, d);
            if (double.TryParse(_pacePct.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) eng.PaceToGoalThresholdPct = Math.Max(0, d);
            if (TimeSpan.TryParse(_quietStart.Text, CultureInfo.InvariantCulture, out t)) eng.QuietStart = t;
            if (TimeSpan.TryParse(_quietEnd.Text, CultureInfo.InvariantCulture, out t)) eng.QuietEnd = t;
            if (TimeSpan.TryParse(_dayStart.Text, CultureInfo.InvariantCulture, out t)) eng.DaySessionStart = t;
            if (TimeSpan.TryParse(_dayEnd.Text, CultureInfo.InvariantCulture, out t)) eng.DaySessionEnd = t;
            if (double.TryParse(_newsPre.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) eng.NewsPreReleaseMinutes = Math.Max(0, d);
            if (double.TryParse(_newsPost.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) eng.NewsPostReleaseMinutes = Math.Max(0, d);
            eng.SaveConfig();
        }

        // ── main refresh: status, advisor, tier rows ────────────────────────
        private void Refresh()
        {
            try
            {
                RefreshCharts();
                PollPending();
                UpdateTradePnlLine();

                var eng = SneakyAtmEngine.Instance;
                string acct = _account.Text;
                if (eng == null) return;

                if (!string.IsNullOrEmpty(acct) && !string.IsNullOrEmpty(_lastAccountSeen) &&
                    !string.Equals(acct, _lastAccountSeen, StringComparison.OrdinalIgnoreCase))
                    _accountJustSwitchedFrom = _lastAccountSeen;
                _lastAccountSeen = acct;

                double pnl = eng.DailyPnl(acct);
                double budget = eng.RemainingRiskBudget(acct);
                int wins, losses; double sumR;
                eng.Score(acct, out wins, out losses, out sumR);

                _pnlLine.Text = "Today's P/L: " + (pnl >= 0 ? "+" : "") + pnl.ToString("0.00", CultureInfo.InvariantCulture);
                _pnlLine.Foreground = pnl >= 0 ? Green : Red;
                _limitsLine.Text = "Loss limit: -" + eng.DailyRiskLimit.ToString("0.00", CultureInfo.InvariantCulture) +
                                    "   Risk/trade: " + eng.RiskPerTrade.ToString("0.00", CultureInfo.InvariantCulture);
                _budgetLine.Text = "You can lose " + budget.ToString("0.00", CultureInfo.InvariantCulture) + " more today.";
                double need = eng.DailyGoal - pnl;
                _goalLine.Text = need > 0
                    ? "Profit lock: +" + eng.DailyGoal.ToString("0.00", CultureInfo.InvariantCulture) + "  (need " + need.ToString("0.00", CultureInfo.InvariantCulture) + " more)"
                    : "Profit lock reached (+" + eng.DailyGoal.ToString("0.00", CultureInfo.InvariantCulture) + ").";
                _goalLine.Foreground = need > 0 ? Ink2 : Green;
                _scoreLine.Text = "Score: " + wins + "W / " + losses + "L  " + (sumR >= 0 ? "+" : "") + sumR.ToString("0.0", CultureInfo.InvariantCulture) +
                                   "R   Trades: " + eng.TradesToday(acct) + "/" + eng.MaxTradesPerDay;
                TimeSpan until = eng.TimeUntilReset();
                _resetLine.Text = "Day resets " + eng.ResetTime.ToString(@"hh\:mm") + " local (in " + (int)until.TotalHours + "h " + until.Minutes + "m)";

                if (eng.LastNewsFetchUtc == DateTime.MinValue)
                {
                    _newsStatusLine.Text = "News feed: not fetched yet...";
                    _newsStatusLine.Foreground = Muted;
                }
                else if (eng.LastNewsError != null)
                {
                    _newsStatusLine.Text = "News feed: last attempt failed (" + MinutesAgoText(eng.LastNewsFetchUtc) + ") -- " + eng.LastNewsError +
                        (eng.LastNewsSuccessUtc != DateTime.MinValue ? "  [still using " + eng.LastNewsEventCount + " events from " + MinutesAgoText(eng.LastNewsSuccessUtc) + "]" : "  [no events loaded yet]");
                    _newsStatusLine.Foreground = Amber;
                }
                else
                {
                    _newsStatusLine.Text = "News feed: OK -- " + eng.LastNewsEventCount + " events, fetched " + MinutesAgoText(eng.LastNewsSuccessUtc) + ".";
                    _newsStatusLine.Foreground = Muted;
                }

                string lockReason = eng.LockoutReason(acct);
                bool locked = lockReason != null;
                _lockBanner.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
                if (locked) _lockBanner.Text = "⛔ " + lockReason;

                var link = SneakyAtmLinkRegistry.TryGet(_chart.Text);
                UpdateInfoNotes(eng, pnl, link);

                if (!_previewDir.HasValue) { _tierBody.Children.Clear(); return; }

                var observations = new List<string>();

                // 1) consecutive-candle pullback (v1's original, and still the only rule that reads
                //    the actual candle sequence on the chart)
                int runDir = 0;
                if (link != null && link.TryGetConsecutiveRun(eng.ConsecutiveRun, out runDir) && runDir == _previewDir.Value)
                {
                    string dirTxt = runDir > 0 ? "up" : "down";
                    observations.Add(eng.ConsecutiveRun + " candles in a row " + dirTxt + " on " + link.Label +
                        " — possible pullback risk entering " + (_previewDir.Value > 0 ? "LONG" : "SHORT") + " here.");
                }

                // 2) volume spike -- last closed bar's volume vs. a rolling 20-bar baseline, published
                //    by SneakyAtmLink
                if (link != null && link.VolumeRatio >= eng.VolumeSpikeMultiple)
                {
                    observations.Add("Last closed bar's volume on " + link.Label + " is " + link.VolumeRatio.ToString("0.0", CultureInfo.InvariantCulture) +
                        "x its recent average — possible exhaustion/spike move, not necessarily a clean continuation.");
                }

                // 3) session time-of-day -- a configurable local-time "quiet/chop" window. Pure
                //    wall-clock check, doesn't need chart data at all.
                TimeSpan nowLocal = DateTime.Now.TimeOfDay;
                bool inQuiet = eng.QuietStart <= eng.QuietEnd
                    ? (nowLocal >= eng.QuietStart && nowLocal < eng.QuietEnd)
                    : (nowLocal >= eng.QuietStart || nowLocal < eng.QuietEnd);   // window wraps past midnight
                if (inQuiet)
                {
                    observations.Add("It's " + DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture) + " local — inside your quiet/chop window (" +
                        eng.QuietStart.ToString(@"hh\:mm") + "–" + eng.QuietEnd.ToString(@"hh\:mm") + "). Historically lower-quality setups in this window.");
                }

                // 4) distance to a prior session level -- published by SneakyAtmLink once it's seen a
                //    full prior session roll over. Picks whichever of high/low/close is actually
                //    nearest (not just first-match-in-order) and reports the exact tick distance and
                //    the level's price, formatted per the instrument's own tick/decimal convention.
                if (link != null && link.PriorOhlcReady && link.Instrument != null && link.TickSize > 0)
                {
                    double px = CurrentPrice(link.Instrument);
                    if (px > 0)
                    {
                        var levels = new[]
                        {
                            new { Name = "yesterday's high",  Price = link.PriorHigh },
                            new { Name = "yesterday's low",   Price = link.PriorLow },
                            new { Name = "yesterday's close", Price = link.PriorClose },
                        };
                        int bestIdx = -1; double bestDist = double.MaxValue;
                        for (int i = 0; i < levels.Length; i++)
                        {
                            double d = Math.Abs(px - levels[i].Price);
                            if (d < bestDist) { bestDist = d; bestIdx = i; }
                        }
                        double distTicks = bestIdx >= 0 ? Math.Round(bestDist / link.TickSize) : double.MaxValue;
                        if (bestIdx >= 0 && distTicks <= eng.PriorLevelToleranceTicks)
                        {
                            string priceTxt = FormatPrice(link.Instrument, levels[bestIdx].Price);
                            observations.Add("Price is " + distTicks.ToString("0", CultureInfo.InvariantCulture) +
                                " tick" + (distTicks == 1 ? "" : "s") + " from " + levels[bestIdx].Name + " (" + priceTxt +
                                ") — price can react at that level.");
                        }
                    }
                }

                // 5) pace to goal -- purely from the engine's own daily P&L tracking, no chart data
                if (eng.DailyGoal > 0 && pnl > 0 && pnl < eng.DailyGoal && pnl >= eng.DailyGoal * (eng.PaceToGoalThresholdPct / 100.0))
                {
                    observations.Add("You're already " + (pnl / eng.DailyGoal * 100.0).ToString("0", CultureInfo.InvariantCulture) +
                        "% of the way to today's profit lock — consider only your best setups from here rather than just filling the day.");
                }

                // 6) range compression -- price has been coiling inside a tight band for the whole
                //    lookback window SneakyAtmLink tracks. Doesn't care about direction; a tight range
                //    can resolve either way, so it fires for both LONG and SHORT previews alike.
                if (link != null && link.RangeTight)
                {
                    observations.Add("Price has stayed within " + link.RangeTicks.ToString("0", CultureInfo.InvariantCulture) +
                        " ticks for " + link.RangeBars.ToString("0", CultureInfo.InvariantCulture) +
                        " bars. Tight ranges like this can end with a sharp move.");
                }

                // 7) SAR-flip chop -- published by SneakyAtmLink as the number of hand-rolled Parabolic
                //    SAR trend flips inside that same lookback window. Also direction-agnostic.
                if (link != null && link.SarFlips >= 3)
                {
                    observations.Add("The SAR flipped " + link.SarFlips.ToString("0", CultureInfo.InvariantCulture) +
                        " times in the last " + link.RangeBars.ToString("0", CultureInfo.InvariantCulture) +
                        " bars. Price has been choppy.");
                }

                // 8) upcoming USD Medium/High-impact news -- direction-agnostic, hard-blocks like every
                //    other rule. If the feed hasn't been fetched yet (just started) or is unreachable,
                //    TryGetUpcomingNews simply returns false rather than failing the preview.
                {
                    string newsTitle; string newsImpact; int newsMinutes;
                    if (eng.TryGetUpcomingNews(eng.NewsPreReleaseMinutes, out newsTitle, out newsImpact, out newsMinutes))
                    {
                        observations.Add(newsTitle + " (" + newsImpact + " impact) in about " + newsMinutes +
                            " minute" + (newsMinutes == 1 ? "" : "s") + " -- price can move sharply around the release.");
                    }
                }

                bool warn = observations.Count > 0;
                _advisorBanner.Visibility = warn ? Visibility.Visible : Visibility.Collapsed;
                if (warn)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < observations.Count; i++)
                        sb.Append("⚠ ").Append(observations[i]).Append(i < observations.Count - 1 ? "\n" : "");
                    sb.Append(_advisorOverride ? "  (overridden)" : "\nEntries blocked unless overridden above.");
                    _advisorBanner.Text = sb.ToString();
                }

                bool blockedByAdvisor = warn && !_advisorOverride;
                bool entryInFlight = _pending != null;

                // Keep the preview's entry price -- and therefore every tier line, both in this
                // panel and drawn on the chart -- tracking the live market, instead of freezing at
                // whatever price was showing the instant Preview was clicked. Frozen only once an
                // order is actually in flight, since at that point the real fill price (not the
                // preview) is what the bracket gets built around anyway (see TryEnter/PollPending).
                if (!entryInFlight && link != null && link.Instrument != null)
                {
                    double live = CurrentPrice(link.Instrument);
                    if (live > 0) _previewEntryPrice = live;
                }

                string instrShort = link != null
                    ? (link.Instrument != null ? link.Instrument.FullName : link.InstrumentFullName)
                    : "No chart";
                _subtitle.Text = instrShort + " · " + eng.ActiveProfile;

                BuildTierRows(eng, acct, link, locked, blockedByAdvisor, entryInFlight);
            }
            catch (Exception ex) { SneakyAtmEngine.Log("Window.Refresh: " + ex.Message); }
        }

        private Sizing ComputeSizing(double nominalStopTicks, double dollarPerTick, double cap, bool manualOverride, int manualQty, double minStopFraction, double dailyLossCap)
        {
            var r = new Sizing();
            if (manualOverride && manualQty > 0)
            {
                // Manual sizing is explicitly "take more than the Risk/trade $ cap would auto-size" --
                // that cap doesn't apply here, by design. What CANNOT be bypassed, same as every other
                // path, is today's remaining daily LOSS limit: however big you want this one trade,
                // this account cannot be allowed to lose more than dailyLossCap today. If the full
                // nominal stop at manualQty would risk more than that, the qty is hard-clamped down
                // (never the stop -- manual sizing keeps the real stop, it only ever gives up size).
                r.ManualRequestedQty = manualQty;
                int maxAffordable = (dollarPerTick > 0 && nominalStopTicks > 0 && dailyLossCap > 0)
                    ? (int)Math.Floor(dailyLossCap / (nominalStopTicks * dollarPerTick))
                    : 0;
                r.Qty = Math.Min(manualQty, Math.Max(0, maxAffordable));
                r.ManualClamped = r.Qty < manualQty;
                r.StopTicks = nominalStopTicks; r.Tightened = false; r.Skip = r.Qty <= 0;
                return r;
            }
            double nominalRisk1 = nominalStopTicks * dollarPerTick;
            if (nominalRisk1 > 0 && nominalRisk1 <= cap)
            {
                r.Qty = Math.Max(1, (int)Math.Floor(cap / nominalRisk1));
                r.StopTicks = nominalStopTicks; r.Tightened = false; r.Skip = false;
                return r;
            }
            double minStopTicks = Math.Max(2, Math.Round(nominalStopTicks * minStopFraction));
            double fitStopTicks = dollarPerTick > 0 ? Math.Floor(cap / dollarPerTick) : 0;
            if (fitStopTicks >= minStopTicks && fitStopTicks >= 1)
            {
                r.Qty = 1; r.StopTicks = fitStopTicks; r.Tightened = true; r.Skip = false;
                return r;
            }
            r.Qty = 0; r.StopTicks = nominalStopTicks; r.Tightened = false; r.Skip = true;
            return r;
        }

        private void BuildTierRows(SneakyAtmEngine eng, string acct, SneakyAtmLinkRegistry.ChartState link,
                                    bool locked, bool blockedByAdvisor, bool entryInFlight)
        {
            _tierBody.Children.Clear();
            _tightenNote.Visibility = Visibility.Collapsed;

            if (link == null)
            {
                _tierBody.Children.Add(new TextBlock { Text = "No chart selected (or its SneakyAtmLink isn't publishing). Drop SneakyAtmLink on a chart and pick it above.",
                    Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
                _summaryQuick.Text = ""; _summaryShared.Text = "";
                ClearTierPlan();
                return;
            }

            // On-chart tier lines (see TierPlan/TierLine): entry line first, then one line per
            // tier actually available this refresh. Published to the WATCHED chart's own
            // SneakyAtmLink at the end of this method, once every row is known.
            var planLines = new List<SneakyAtmLinkRegistry.TierLine>();
            int previewDir = _previewDir.Value;
            if (_previewEntryPrice > 0)
            {
                planLines.Add(new SneakyAtmLinkRegistry.TierLine
                {
                    Label = previewDir > 0 ? "LONG ENTRY" : "SHORT ENTRY",
                    Price = _previewEntryPrice,
                    DollarTarget = 0,
                    ColorId = 0
                });
            }

            double cap = Math.Min(eng.RiskPerTrade, eng.RemainingRiskBudget(acct));
            double dollarPerTick = 1;
            try { dollarPerTick = link.Instrument.MasterInstrument.PointValue * link.Instrument.MasterInstrument.TickSize; } catch { }
            double mainStopTicks = Math.Max(1, Math.Round((link.Atr / Math.Max(1e-9, link.TickSize)) * eng.StopAtrMult));

            // Quick's "scalp stop" is a real 1-minute ATR off the chart's own SneakyAtmLink, not a
            // fraction of the main stop. AtrFast is 0 only if the companion indicator couldn't add a
            // 1-minute series for this instrument/feed (hasn't happened in testing, but kept as a
            // safety net) or hasn't warmed up yet right after being dropped on the chart -- in either
            // case we fall back to the old fraction-of-main-stop approximation rather than sizing off
            // a zero stop.
            bool   quickIsRealAtr   = link.AtrFast > 0;
            double quickNominalTicks = quickIsRealAtr
                ? Math.Max(1, Math.Round(link.AtrFast / Math.Max(1e-9, link.TickSize)))
                : Math.Max(1, Math.Round(mainStopTicks * eng.ScalpStopFraction));

            bool manual = _sizingOverride;
            double dailyLossCap = eng.RemainingRiskBudget(acct);
            Sizing quick = ComputeSizing(quickNominalTicks, dollarPerTick, cap, manual, _manualQty, eng.MinStopFraction, dailyLossCap);
            Sizing shared = ComputeSizing(mainStopTicks, dollarPerTick, cap, manual, _manualQty, eng.MinStopFraction, dailyLossCap);

            _lastQuickAvailable = !quick.Skip && quick.Qty > 0;
            _lastQuickTargetDollar = _lastQuickAvailable
                ? quick.Qty * quick.StopTicks * Tiers[0].RMultiple * dollarPerTick
                : 0;

            string instrName = link.Instrument != null ? link.Instrument.FullName : link.InstrumentFullName;
            bool clampedNow = manual && (quick.ManualClamped || shared.ManualClamped);
            _qtyVal.Text = manual ? (clampedNow ? Math.Min(quick.Qty, shared.Qty).ToString(CultureInfo.InvariantCulture) : _manualQty.ToString(CultureInfo.InvariantCulture)) : "AUTO";

            // Manual sizing intentionally bypasses the Risk/trade $ cap (see ComputeSizing's
            // manualOverride branch) -- but NOT today's remaining daily loss limit, which is hard and
            // the same for every path. If your CONTRACTS pick would risk more than what's left of
            // today's loss limit, ComputeSizing already clamped Qty down -- this banner just explains
            // why the fill you're about to get may be smaller than what you dialed in.
            if (manual && (quick.ManualClamped || shared.ManualClamped))
            {
                Sizing worst = quick.ManualClamped && (!shared.ManualClamped || quick.Qty <= shared.Qty) ? quick : shared;
                _manualRiskBanner.Text = "⚠ MAX OVERRIDE: " + worst.Qty + " contract" + (worst.Qty == 1 ? "" : "s") +
                    " (your daily loss limit) — " + worst.ManualRequestedQty + " x " + instrName +
                    " would risk more than the $" + dailyLossCap.ToString("0.##", CultureInfo.InvariantCulture) + " you have left today.";
                _manualRiskBanner.Visibility = Visibility.Visible;
            }
            else
            {
                _manualRiskBanner.Visibility = Visibility.Collapsed;
            }

            string quickStopLabel = quickIsRealAtr ? "1m ATR" : "scalp approx.";
            if (quick.Skip)
                _summaryQuick.Text = "Quick (scalp): SKIP (too wild — can't fit even a tightened stop inside your risk cap).";
            else
                _summaryQuick.Text = "Quick (scalp): " + quick.Qty + " x " + instrName + " · stop " + quick.StopTicks.ToString("0", CultureInfo.InvariantCulture) +
                                      "t (" + quickStopLabel + ") · risk $" + (quick.Qty * quick.StopTicks * dollarPerTick).ToString("0.00", CultureInfo.InvariantCulture);

            if (shared.Skip)
                _summaryShared.Text = "Medium/Large/Home Run: SKIP (too wild — can't fit even a tightened stop inside your risk cap).";
            else
                _summaryShared.Text = "Medium/Large/Home Run: " + shared.Qty + " x " + instrName + " · stop " + shared.StopTicks.ToString("0", CultureInfo.InvariantCulture) +
                                       "t (ATR) · risk $" + (shared.Qty * shared.StopTicks * dollarPerTick).ToString("0.00", CultureInfo.InvariantCulture);

            if (quick.Tightened || shared.Tightened)
            {
                var sb = new StringBuilder("⚠ Stop tightened to fit your risk: ");
                if (quick.Tightened) sb.Append("Quick " + quickNominalTicks.ToString("0") + " → " + quick.StopTicks.ToString("0") + "t. ");
                if (shared.Tightened) sb.Append("Medium/Large/HR " + mainStopTicks.ToString("0") + " → " + shared.StopTicks.ToString("0") + "t.");
                _tightenNote.Text = sb.ToString();
                _tightenNote.Visibility = Visibility.Visible;
            }

            // If the mouse is over a tier row, isolate the chart down to just that tier's own bracket
            // (entry, its stop, its target) instead of showing every available tier's target at once.
            // A hovered tier that this refresh turns out to be skipped/unavailable just falls back to
            // the "nothing hovered" view below (its own branch never fires, isHoveredTier is simply
            // never true for a skipped tier since that's gated on !skip too).
            bool anyHover = _hoveredTier.HasValue;

            foreach (TierDef td in Tiers)
            {
                Sizing sz = td.Shared ? shared : quick;
                bool skip = sz.Skip;
                double targetTicks = sz.StopTicks * td.RMultiple;
                double payout = sz.Qty * targetTicks * dollarPerTick;
                double riskDollars = sz.Qty * sz.StopTicks * dollarPerTick;

                bool disabled = locked || blockedByAdvisor || entryInFlight || skip || sz.Qty <= 0;
                bool isHoveredTier = anyHover && _hoveredTier.Value == td.Id && !skip && sz.Qty > 0;

                if (!skip && sz.Qty > 0 && _previewEntryPrice > 0 && (!anyHover || isHoveredTier))
                {
                    planLines.Add(new SneakyAtmLinkRegistry.TierLine
                    {
                        Label = td.Label,
                        Price = _previewEntryPrice + previewDir * targetTicks * link.TickSize,
                        DollarTarget = payout,
                        ColorId = td.Id == Tier.Quick ? 1 : td.Id == Tier.Medium ? 2 : td.Id == Tier.Large ? 3 : 4
                    });
                }
                if (isHoveredTier && _previewEntryPrice > 0)
                {
                    planLines.Add(new SneakyAtmLinkRegistry.TierLine
                    {
                        Label = td.Shared ? "STOP" : "QUICK HIT STOP",
                        Price = _previewEntryPrice - previewDir * sz.StopTicks * link.TickSize,
                        DollarTarget = -riskDollars,
                        ColorId = 5
                    });
                }

                Brush tierColor = td.Id == Tier.Quick ? TQuick : td.Id == Tier.Medium ? TMedium : td.Id == Tier.Large ? TLarge : THome;
                Brush tierTextColor = td.Id == Tier.HomeRun ? FB(Colors.Black) : FB(Colors.White);

                var row = new Border { Background = disabled ? Card : tierColor, BorderBrush = Edge, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10),
                    Cursor = disabled ? Cursors.Arrow : Cursors.Hand, Opacity = disabled ? 0.5 : 1.0 };
                var rs = new StackPanel();
                row.Child = rs;

                if (skip)
                {
                    rs.Children.Add(new TextBlock { Text = td.Label, Foreground = disabled ? Muted : tierTextColor, FontWeight = FontWeights.Bold, FontSize = 12 });
                    rs.Children.Add(new TextBlock { Text = "Not available right now", Foreground = Muted, FontSize = 10.5, Margin = new Thickness(0, 2, 0, 0) });
                }
                else
                {
                    rs.Children.Add(new TextBlock { Text = td.Label, Foreground = disabled ? Ink2 : tierTextColor, FontWeight = FontWeights.Bold, FontSize = 12 });
                    rs.Children.Add(new TextBlock
                    {
                        Foreground = disabled ? Muted : tierTextColor, FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
                        Text = "Target +$" + payout.ToString("0.00", CultureInfo.InvariantCulture) + "  (" + td.RMultiple.ToString("0.#", CultureInfo.InvariantCulture) +
                               "R, " + targetTicks.ToString("0", CultureInfo.InvariantCulture) + " ticks)   risk $" + riskDollars.ToString("0.00", CultureInfo.InvariantCulture)
                    });
                }

                if (!disabled)
                {
                    TierDef captured = td; Sizing szCap = sz; double tgtCap = targetTicks;
                    row.MouseLeftButtonUp += (s, e) => TryEnter(link, captured, szCap.Qty, szCap.StopTicks, tgtCap);
                    row.MouseEnter += (s, e) => { _hoveredTier = captured.Id; Refresh(); };
                    row.MouseLeave += (s, e) => { if (_hoveredTier == captured.Id) _hoveredTier = null; Refresh(); };
                }
                _tierBody.Children.Add(row);
            }

            // A hovered tier that just went skipped/disabled this refresh (advisor fired, risk budget
            // ran out, etc.) never got a MouseLeave -- clear it here so the chart falls back to the
            // "nothing hovered" view instead of isolating a tier that no longer has a bracket to show.
            if (_hoveredTier.HasValue && !planLines.Any(l => l.ColorId == 5))
                _hoveredTier = null;

            // Publish (or clear) the on-chart tier plan for whichever chart is WATCHING-selected.
            // If the entry price never got sampled (e.g. no last-trade price yet right after the
            // instrument subscribed), there's nothing meaningful to draw -- clear instead.
            string chartKey = _chart.Text;
            if (_previewEntryPrice > 0 && planLines.Count > 0 && !string.IsNullOrEmpty(chartKey))
            {
                if (!string.IsNullOrEmpty(_tierPlanChartKey) && _tierPlanChartKey != chartKey)
                    SneakyAtmLinkRegistry.ClearTierPlan(_tierPlanChartKey);
                SneakyAtmLinkRegistry.PublishTierPlan(chartKey, previewDir, planLines);
                _tierPlanChartKey = chartKey;
            }
            else
            {
                ClearTierPlan();
            }
        }

        // informational-only notes -- never block entry, no override needed. Distinct from the
        // hard-blocking OBSERVATIONS banner above.
        private void UpdateInfoNotes(SneakyAtmEngine eng, double pnl, SneakyAtmLinkRegistry.ChartState link)
        {
            var notes = new List<string>();

            if (!string.IsNullOrEmpty(_accountJustSwitchedFrom))
                notes.Add("Account switched (from " + _accountJustSwitchedFrom + ").");

            // Post-release news note -- informational only, direction-agnostic, shown even with no
            // preview direction chosen yet (matches your screenshot).
            {
                string newsTitle; string newsImpact;
                if (eng.TryGetRecentNews(eng.NewsPostReleaseMinutes, out newsTitle, out newsImpact))
                    notes.Add(newsTitle + " (" + newsImpact + " impact) was just released.");
            }

            // Candle-burst note -- same consecutive-run data as OBSERVATIONS rule #1, but reported
            // here regardless of which way (or whether) you're previewing: a run in EITHER direction
            // is just "a burst just happened," not specifically a risk to one side of a trade.
            if (link != null)
            {
                int burstDir;
                if (link.TryGetConsecutiveRun(eng.ConsecutiveRun, out burstDir) && burstDir != 0)
                {
                    notes.Add(eng.ConsecutiveRun + " candles " + (burstDir > 0 ? "up" : "down") +
                        " in a row. A pause or pullback can follow a burst like this.");
                }
            }

            // wins-needed-to-goal, sized off Quick's own $ target (mirrors the real product's
            // "At today's size, your goal needs about N QUICK HIT wins." line)
            if (_previewDir.HasValue && _lastQuickAvailable && _lastQuickTargetDollar > 0)
            {
                double remaining = eng.DailyGoal - pnl;
                if (remaining > 0)
                {
                    int winsNeeded = (int)Math.Ceiling(remaining / _lastQuickTargetDollar);
                    notes.Add("At today's size, your goal needs about " + Math.Max(1, winsNeeded) + " QUICK HIT win" +
                        (winsNeeded == 1 ? "" : "s") + ".");
                }
            }

            // Day vs. Overnight session -- approximated from a configurable local wall-clock window
            // (there's no confirmed NinjaScript API in this codebase for the instrument's actual
            // session template), same wraparound-safe comparison already used for the quiet window.
            TimeSpan nowLocal = DateTime.Now.TimeOfDay;
            bool isDaySession = eng.DaySessionStart <= eng.DaySessionEnd
                ? (nowLocal >= eng.DaySessionStart && nowLocal < eng.DaySessionEnd)
                : (nowLocal >= eng.DaySessionStart || nowLocal < eng.DaySessionEnd);
            notes.Add(isDaySession ? "Day session." : "Overnight session.");

            if (notes.Count > 0)
            {
                _infoNotes.Text = string.Join("   ", notes);
                _infoNotes.Visibility = Visibility.Visible;
            }
            else
            {
                _infoNotes.Visibility = Visibility.Collapsed;
            }
        }

        // ── entry + bracket ──────────────────────────────────────────────────
        private void TryEnter(SneakyAtmLinkRegistry.ChartState link, TierDef tier, int qty, double stopTicks, double targetTicks)
        {
            if (_pending != null || !_previewDir.HasValue) return;
            var eng = SneakyAtmEngine.Instance;
            string acct = _account.Text;
            if (eng == null || string.IsNullOrEmpty(acct) || qty <= 0) return;

            Account a = SneakyAtmEngine.FindAccount(acct);
            Instrument instr = link.Instrument;
            if (a == null || instr == null) { SneakyAtmEngine.Log("Entry aborted: account or instrument not resolved."); return; }

            int dir = _previewDir.Value;
            OrderAction act = dir > 0 ? OrderAction.Buy : OrderAction.SellShort;
            string name = "SnkAtm_" + tier.Id + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);

            try
            {
                Order o = a.CreateOrder(instr, act, OrderType.Market, OrderEntry.Automated, TimeInForce.Day,
                                         qty, 0, 0, string.Empty, name, NinjaTrader.Core.Globals.MaxDate, null);
                a.Submit(new Order[] { o });

                _pending = new Pending
                {
                    AcctName = acct, Acct = a, Instr = instr, Dir = dir, Qty = qty, StopTicks = stopTicks, TargetTicks = targetTicks,
                    TierRMultiple = tier.RMultiple, EntryOrder = o, StartedUtc = DateTime.UtcNow
                };
                eng.RegisterTradeSubmitted(acct);
                SneakyAtmEngine.Log(tier.Label + " ▶ " + a.Name + " " + act + " " + qty + " " + instr.FullName);
            }
            catch (Exception ex) { SneakyAtmEngine.Log("Entry failed: " + ex.Message); }
        }

        // polled every tick while an entry is in flight: wait for the fill, place the OCO stop+target bracket,
        // move the stop to breakeven at +1R, and score the result once flat again.
        private void PollPending()
        {
            Pending pe = _pending;
            if (pe == null) return;
            var eng = SneakyAtmEngine.Instance;

            if (pe.BracketPlaced)
            {
                double tick = 1;
                try { tick = pe.Instr.MasterInstrument.TickSize; } catch { }

                if (!pe.BeMoved)
                {
                    double cur = CurrentPrice(pe.Instr);
                    if (cur > 0)
                    {
                        double gainTicks = pe.Dir * (cur - pe.EntryAvg) / tick;
                        if (gainTicks >= pe.StopTicks)
                        {
                            try
                            {
                                pe.StopOrder.StopPriceChanged = pe.EntryAvg;
                                pe.Acct.Change(new Order[] { pe.StopOrder });
                                pe.BeMoved = true;
                                SneakyAtmEngine.Log("Moved stop to breakeven (+1R reached).");
                            }
                            catch (Exception ex) { SneakyAtmEngine.Log("BE move failed: " + ex.Message); }
                        }
                    }
                }

                Position p = FindPos(pe.Acct, pe.Instr);
                if (p == null || p.MarketPosition == MarketPosition.Flat)
                {
                    bool? win = null;
                    try
                    {
                        if (pe.TargetOrder != null && pe.TargetOrder.OrderState == OrderState.Filled) win = true;
                        else if (pe.StopOrder != null && pe.StopOrder.OrderState == OrderState.Filled) win = false;
                    }
                    catch { }
                    if (win.HasValue && eng != null)
                        eng.RegisterTradeResult(pe.AcctName, win.Value, win.Value ? pe.TierRMultiple : -1.0);
                    else
                        SneakyAtmEngine.Log("Trade closed outside its own bracket (manual flatten?) — not scored.");
                    _pending = null;
                }
                return;
            }

            if ((DateTime.UtcNow - pe.StartedUtc).TotalSeconds > 15 && pe.EntryOrder != null &&
                (pe.EntryOrder.OrderState == OrderState.Rejected || pe.EntryOrder.OrderState == OrderState.Cancelled))
            {
                SneakyAtmEngine.Log("Entry order " + pe.EntryOrder.OrderState + " — nothing to bracket.");
                _pending = null;
                return;
            }

            Position pos = FindPos(pe.Acct, pe.Instr);
            if (pos == null || pos.MarketPosition == MarketPosition.Flat) return;   // not filled yet

            double avg = pos.AveragePrice;
            double tick2 = 1;
            try { tick2 = pe.Instr.MasterInstrument.TickSize; } catch { }
            double stopPx = RoundToTick(avg - pe.Dir * pe.StopTicks * tick2, tick2);
            double tgtPx = RoundToTick(avg + pe.Dir * pe.TargetTicks * tick2, tick2);

            OrderAction exitAct = pe.Dir > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
            string oco = Guid.NewGuid().ToString("N");
            try
            {
                var send = new List<Order>();
                pe.StopOrder = pe.Acct.CreateOrder(pe.Instr, exitAct, OrderType.StopMarket, OrderEntry.Automated, TimeInForce.Gtc,
                                                    pe.Qty, 0, stopPx, oco, "SnkAtm_SL", NinjaTrader.Core.Globals.MaxDate, null);
                send.Add(pe.StopOrder);
                pe.TargetOrder = pe.Acct.CreateOrder(pe.Instr, exitAct, OrderType.Limit, OrderEntry.Automated, TimeInForce.Gtc,
                                                      pe.Qty, tgtPx, 0, oco, "SnkAtm_TP", NinjaTrader.Core.Globals.MaxDate, null);
                send.Add(pe.TargetOrder);
                pe.Acct.Submit(send.ToArray());
                pe.EntryAvg = avg;
                pe.BracketPlaced = true;
                SneakyAtmEngine.Log("Bracket placed: stop " + stopPx.ToString("0.00", CultureInfo.InvariantCulture) +
                                    ", target " + tgtPx.ToString("0.00", CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { SneakyAtmEngine.Log("Bracket failed: " + ex.Message); _pending = null; }
        }

        // Live unrealized P/L of the CURRENT open trade only (not today's cumulative total, which
        // _pnlLine already shows) -- visible the moment the bracket is actually placed (i.e. the
        // entry has filled), hidden again once flat.
        private void UpdateTradePnlLine()
        {
            Pending pe = _pending;
            if (pe == null || !pe.BracketPlaced) { _tradePnlLine.Visibility = Visibility.Collapsed; return; }

            double cur = CurrentPrice(pe.Instr);
            if (cur <= 0) { _tradePnlLine.Visibility = Visibility.Collapsed; return; }

            double tick = 1, dollarPerTick = 1;
            try
            {
                tick = pe.Instr.MasterInstrument.TickSize;
                dollarPerTick = pe.Instr.MasterInstrument.PointValue * tick;
            }
            catch { }

            double gainTicks = tick > 0 ? pe.Dir * (cur - pe.EntryAvg) / tick : 0;
            double openPnl = gainTicks * dollarPerTick * pe.Qty;

            _tradePnlLine.Text = "Open trade P/L: " + (openPnl >= 0 ? "+$" : "-$") + Math.Abs(openPnl).ToString("0.00", CultureInfo.InvariantCulture) +
                                  "  (" + (gainTicks >= 0 ? "+" : "") + gainTicks.ToString("0", CultureInfo.InvariantCulture) + "t)";
            _tradePnlLine.Foreground = openPnl >= 0 ? Green : Red;
            _tradePnlLine.Visibility = Visibility.Visible;
        }

        private static string MinutesAgoText(DateTime utc)
        {
            double mins = (DateTime.UtcNow - utc).TotalMinutes;
            if (mins < 1) return "just now";
            if (mins < 60) return (int)mins + "m ago";
            return (int)(mins / 60) + "h " + (int)(mins % 60) + "m ago";
        }

        private static double CurrentPrice(Instrument instr)
        {
            try { return instr.MarketData.Last.Price; } catch { return 0; }
        }

        private static string FormatPrice(Instrument instr, double price)
        {
            try { return instr.MasterInstrument.FormatPrice(price); } catch { return price.ToString("0.##", CultureInfo.InvariantCulture); }
        }

        private static Position FindPos(Account a, Instrument instr)
        {
            try
            {
                foreach (Position p in a.Positions.ToArray())
                    if (p != null && p.Instrument != null && p.Instrument.FullName == instr.FullName && p.MarketPosition != MarketPosition.Flat)
                        return p;
            }
            catch { }
            return null;
        }

        private static double RoundToTick(double price, double tick)
        {
            return tick <= 0 ? price : Math.Round(price / tick) * tick;
        }
    }
}
