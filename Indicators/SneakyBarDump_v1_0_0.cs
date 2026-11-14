// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
// ═════════════════════════════════════════════════════════════════════════════
//  SentinelBarDump — the EQUIVALENCE GATE's ground truth
//  File: SneakyBarDump_v1_0_0.cs   ·   Version v1.2.0   ·   Schema bars.2 / ticks.1   ·   namespace …Indicators.Sneaky
// ─────────────────────────────────────────────────────────────────────────────
//  WHAT THIS IS
//    A transcript of what NinjaTrader's bar construction ACTUALLY produced: every completed bar's
//    time + OHLC + volume, unthrottled, written to JSONL. It exists to answer exactly one question —
//    **does the offline harness build the same bars NT does?** Load it on a chart, and the file it
//    writes is the answer key.
//
//    v1.2.0 adds an OPT-IN raw tick recorder alongside the bar dumper (default OFF — set "Record
//    ticks" true in Properties to turn it on). It exists for a different reason than the bar file:
//    if a signal/gate's settings change, the fires it produces afterwards land at different times,
//    and the sensor-triggered tick sidecars (SneakyExcursionRecorder/SneakyCandidateRecorder) only
//    ever capture ticks around a fire that already happened — there is no continuous tick history to
//    backtest a NEW setting against. This gives that continuous history, the same way the bar dump
//    already does for bars.
//
//  WHY IT HAD TO BE BUILT (2026-07-26)
//    The harness's first equivalence target is SentinelTide, and the plan was to compare against the
//    `[Sentinel:Tide]` lines in sentinel.log. That log cannot do the job, and the reason is worth
//    recording so nobody tries again:
//      • it is throttled to one line per 10 WALL-seconds, so a rebuild logs ~8% of bars — and not a
//        random 8%, since bar time advances thousands of times faster than wall time;
//      • it is REALTIME-GATED, so a static historical chart is silent;
//      • it carries NO BAR TIMESTAMP, only the wall-clock instant of the log write;
//      • `_lastLog` is per-instance, so several bars-type instances interleave and the `N bars`
//        session ordinal jumps between them (measured: 34 lines total, ordinals 1 / 429 / 58, with
//        four byte-identical lines 0.24s apart).
//    A sample with no join key is not ground truth. This writes every bar, with its own time.
//
//  ⚠ DELIBERATE EXCEPTION: NO REALTIME GATE
//    Every recorder in this suite gates on `State == State.Realtime`, because a seam has no as-of
//    semantics and a replayed verdict stamped onto an old bar is lookahead contamination. That
//    reasoning does not apply here and applying it anyway would destroy the tool: a bar transcript
//    contains no labels, no seam reads and nothing forward-looking — it records only what a bar
//    ALREADY IS at its own close. Historical bars are precisely what the gate needs to compare.
//    Every row carries `rt` (true = built live, false = built during the historical rebuild) so the
//    Lab can split them if it ever matters. This also fixes the defect flagged in FLOWBARS §3b:
//    "G1 must be checkable on a static chart."
//
//  BAR-TYPE AGNOSTIC ON PURPOSE
//    It reads Time/Open/High/Low/Close/Volume, so it works unchanged on Tide, TBars, TbarsCount,
//    Flux, Drift, Lattice, Effort — and on stock NT bar types. Every future harness equivalence
//    gate uses this same file; it is not a Tide-specific probe.
//
//  NOT A SENSOR — the …State publish protocol does NOT apply
//    Design system §9 item 6 requires every new signal/regime/bias/context indicator to publish a
//    `…State` seam and be wired into the Council. This is a diagnostic exporter: it has no opinion
//    about direction, nothing to fuse, and no consumer inside the platform. Publishing a seam would
//    add a publisher to the very seam-store whose failure modes this tool exists to investigate.
//
//  HOW TO USE IT
//    1. Add it to the chart whose bar type you want to reproduce. It starts writing immediately —
//       the historical rebuild alone gives a full answer key, no replay needed.
//    2. The file lands in Sentinel\Harness\bars\<stamp>__<inst>__<bartag>.jsonl. Line 1 is a HEADER
//       object (schema, instrument, bar tag, tick size, bars-period values, versions) so the file is
//       self-describing and the Lab never has to guess the tick size or the period.
//    3. Diff it against the harness with Lab\harness\equivalence.py.
//    4. To also record ticks, turn on "Record ticks" in Properties. A second, separate file lands in
//       Sentinel\Harness\ticks\<stamp>__<inst>__<bartag>__<disambig>.jsonl, sharing the bar file's
//       stamp/disambig so the two can be matched up by filename. Each row is `{"t":iso,"px":price,
//       "vol":volume}` for one trade print (MarketDataType.Last).
//       ⚠ REALTIME/REPLAY ONLY — THIS DOES NOT BACKFILL. Unlike the bar dump (which gets a full
//       historical rebuild for free via OnBarUpdate), NinjaTrader only fires OnMarketData for
//       realtime and replay data, never for historical bars. Turning "Record ticks" on today starts
//       a tick history from today; it cannot retroactively produce tick data for any earlier date.
//       If you need ticks for a session that already happened, they only exist if this was already
//       on (or a fire-triggered sidecar recorder was) at the time.
//
//  CHANGELOG
//    v1.2.0 (2026-08-23) — added the opt-in tick recorder described above. New `RecordTicks`
//           [NinjaScriptProperty] (default false) and non-property `FlushEveryTicks` (default 2000,
//           mirrors `FlushEveryBars`'s "no codegen churn" reasoning). New SCHEMA ticks.1, written to
//           a SEPARATE file (Harness\ticks\, not mixed into the bars.2 file) so existing bars.2
//           consumers (bars/ntdump.py, Lab\harness\equivalence.py) are unaffected. Bar file schema
//           (bars.2) is unchanged by this release.
//    v1.1.0 (2026-08-17) — SCHEMA bars.2, ported from main: the header now carries
//           `resetOnNewTradingDay` (NinjaTrader's `Bars.IsResetOnNewTradingDay`, the Data Series
//           "Break at EOD", the Python port's `reset_on_new_session`). It changes where EVERY bar
//           boundary falls, so without it the two columns cannot honestly be compared — it was a
//           *stated precondition* of the bar-type parity gate rather than a compared field, which
//           means a settings mismatch would have surfaced as a bar disagreement and been debugged
//           as a porting bug.
//           ⛔ It cannot be set instead of recorded: the property is READ-ONLY and appears in
//           neither `BarsProperties`, `BarsPeriod`, nor any chart template. Recording it is the
//           only defence there is. 1 = on, 0 = off, **-1 = UNKNOWN** (not the same claim as off —
//           the gate must treat -1 as unknown, never as "EOD was off").
//           ⚠ Deliberately edited IN PLACE rather than forked to `_v1_1_0`: the type name is the
//           indicator's serialization identity, and a rename would orphan it in every chart
//           template and workspace that already names it. The version consumers key on is `schema`.
//    v1.0.0 (2026-07-26) — initial. Every-bar JSONL transcript, self-describing header, buffered
//           writes with periodic flush, no realtime gate (see above), glass card + label remover.
// ═════════════════════════════════════════════════════════════════════════════

using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns.Sneaky;

namespace NinjaTrader.NinjaScript.Indicators.Sneaky
{
    public class SneakyBarDump_v1_0_0 : Indicator, ISneakyScrollCard
    {
        private const string SchemaVer     = "bars.2";
        private const string TickSchemaVer = "ticks.1";
        private const string DumpVer       = "1.2.0";

        private string        _logPath;
        private StringBuilder _buf;
        private int           _pending;
        private long          _written;
        private bool          _writerDead;
        private bool          _writeFailed;
        private string        _lastErr;

        // tick recorder state (opt-in, see RecordTicks) — a separate file, separate buffer, separate
        // failure state from the bar dump above so one can be broken without silently taking the
        // other down with it.
        private string        _ticksLogPath;
        private StringBuilder _ticksBuf;
        private int           _ticksPending;
        private long          _ticksWritten;
        private bool          _ticksWriterDead;
        private bool          _ticksWriteFailed;
        private string        _ticksLastErr;

        // card state
        private SneakySkin.Painter _sp;
        private bool     _liveBars;
        private DateTime _lastBarTime;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "Sneaky Bar Dump v1.2.0";
                Description = "Writes EVERY completed bar (time + OHLC + volume) to JSONL as the answer key for the "
                            + "offline harness equivalence gate. Bar-type agnostic, unthrottled, historical bars "
                            + "included on purpose. No orders, no seam, no opinion. Writes Sentinel\\Harness\\bars\\. "
                            + "Optionally (Record ticks) also writes a continuous raw-tick transcript to "
                            + "Sentinel\\Harness\\ticks\\ — realtime/replay only, does not backfill historical ticks.";
                IsOverlay                = true;
                Calculate                = Calculate.OnBarClose;   // one call per COMPLETED bar — the forming bar is not a bar
                DrawOnPricePanel         = true;
                IsSuspendedWhileInactive = false;
                ShowInfo                 = true;
                CardCorner               = SneakyCardCorner.TopRight;
                FlushEveryBars           = 500;
                RecordTicks              = false;
                FlushEveryTicks          = 2000;
                ShowIndicatorLabel       = false;
            }
            else if (State == State.DataLoaded)
            {
                if (!ShowIndicatorLabel) Name = string.Empty;

                _buf = new StringBuilder(1 << 16);
                string stamp = DateTime.Now.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
                // BUGFIX (2026-08-22, P2 finding: filename collision): stamp is SECOND-precision, so two chart
                // instances of this dumper finishing setup within the same wall-clock second (common at
                // workspace load, when several charts init near-simultaneously) computed the IDENTICAL path
                // and could clobber/interleave each other's writes. A per-instance GUID suffix is used rather
                // than just widening the stamp to millisecond precision because workspace load can start
                // multiple charts within the same millisecond too -- only a per-instance random token is robust.
                // Both the bar file and (if enabled) the tick file share this same stamp+disambig so the two
                // can be matched up by filename alone.
                string disambig = Guid.NewGuid().ToString("N").Substring(0, 6);

                try
                {
                    string dir = Path.Combine(SneakyCore.SettingsDir, "Harness", "bars");
                    Directory.CreateDirectory(dir);
                    _logPath = Path.Combine(dir, stamp + "__" + InstName() + "__" + BarTag() + "__" + disambig + ".jsonl");
                    WriteHeader();
                }
                catch (Exception ex)
                {
                    _logPath = null; _writerDead = true; _lastErr = ex.Message;
                    try
                    {
                        SneakyCore.Log("BarDump", "WRITER DEAD — could not open Sentinel\\Harness\\bars; NOTHING will "
                            + "be recorded this session (" + ex.Message + "). The card shows NO REC.");
                    }
                    catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.OnStateChange", _sx); }
                }

                if (RecordTicks)
                {
                    _ticksBuf = new StringBuilder(1 << 16);
                    try
                    {
                        string tdir = Path.Combine(SneakyCore.SettingsDir, "Harness", "ticks");
                        Directory.CreateDirectory(tdir);
                        _ticksLogPath = Path.Combine(tdir, stamp + "__" + InstName() + "__" + BarTag() + "__" + disambig + ".jsonl");
                        WriteTicksHeader();
                    }
                    catch (Exception ex)
                    {
                        _ticksLogPath = null; _ticksWriterDead = true; _ticksLastErr = ex.Message;
                        try
                        {
                            SneakyCore.Log("BarDump", "TICK WRITER DEAD — could not open Sentinel\\Harness\\ticks; "
                                + "no ticks will be recorded this session (" + ex.Message + "). The bar dump above is unaffected.");
                        }
                        catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.OnStateChange", _sx); }
                    }
                }
            }
            else if (State == State.Terminated)
            {
                Flush();
                FlushTicks();
                if (_sp != null)
                {
                    try { _sp.Dispose(); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.OnStateChange", _sx); }
                    _sp = null;
                }
                try { SneakySkin.CardLayout.Release(this); } catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.OnStateChange", _sx); }
            }
        }

        /// <summary>Line 1 = everything a reader needs to interpret the rows without guessing.
        /// The tick size in particular: heights and bodies are meaningless in ticks without it, and
        /// a Lab-side default is exactly the kind of silent assumption this project keeps paying for.</summary>
        private void WriteHeader()
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"hdr\":1")
              .Append(",\"schema\":").Append(Q(SchemaVer))
              .Append(",\"dumpVer\":").Append(Q(DumpVer))
              .Append(",\"coreVer\":").Append(Q(SafeCoreVer()))
              .Append(",\"inst\":").Append(Q(InstName()))
              .Append(",\"bartype\":").Append(Q(BarTag()))
              .Append(",\"barLabel\":").Append(Q(SafeFriendly()))
              .Append(",\"tickSize\":").Append(F(SafeTickSize()))
              .Append(",\"pointValue\":").Append(F(SafePointValue()))
              .Append(",\"periodType\":").Append(F(SafePeriodTypeId()))
              .Append(",\"periodValue\":").Append(F(SafePeriodValue()))
              .Append(",\"periodValue2\":").Append(F(SafePeriodValue2()))
              .Append(",\"baseValue\":").Append(F(SafeBaseValue()))
              .Append(",\"tradingHours\":").Append(Q(SafeTradingHours()))
              // Schema bars.2 — see SafeResetOnNewTradingDay(). 1 = on, 0 = off, -1 = UNKNOWN.
              .Append(",\"resetOnNewTradingDay\":").Append(F(SafeResetOnNewTradingDay()))
              .Append(",\"openedUtc\":").Append(Q(Iso(DateTime.UtcNow)))
              .Append("}");
            Append(sb.ToString());
        }

        /// <summary>Same self-describing-header idea as WriteHeader(), scaled down: a tick row has
        /// no period/schema ambiguity to resolve, so this only needs enough to join back to the bar
        /// file (inst, bartype) and to interpret price (tickSize).</summary>
        private void WriteTicksHeader()
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"hdr\":1")
              .Append(",\"schema\":").Append(Q(TickSchemaVer))
              .Append(",\"dumpVer\":").Append(Q(DumpVer))
              .Append(",\"coreVer\":").Append(Q(SafeCoreVer()))
              .Append(",\"inst\":").Append(Q(InstName()))
              .Append(",\"bartype\":").Append(Q(BarTag()))
              .Append(",\"tickSize\":").Append(F(SafeTickSize()))
              .Append(",\"openedUtc\":").Append(Q(Iso(DateTime.UtcNow)))
              .Append("}");
            AppendTick(sb.ToString());
        }

        protected override void OnBarUpdate()
        {
            if (_writerDead || CurrentBar < 0) return;

            _liveBars = State == State.Realtime;
            _lastBarTime = Time[0];

            // One row per completed bar. `t` is the bar's OWN close time in UTC -- the join key the
            // Tide log never had. Prices are emitted raw (already on the tick grid); the Lab derives
            // height/body from them and the header's tickSize rather than trusting a second source.
            var sb = new StringBuilder(160);
            sb.Append("{\"i\":").Append(CurrentBar.ToString(CultureInfo.InvariantCulture))
              .Append(",\"t\":").Append(Q(Iso(Time[0])))
              .Append(",\"o\":").Append(F(Open[0]))
              .Append(",\"h\":").Append(F(High[0]))
              .Append(",\"l\":").Append(F(Low[0]))
              .Append(",\"c\":").Append(F(Close[0]))
              .Append(",\"v\":").Append(F(Volume[0]))
              .Append(",\"rt\":").Append(_liveBars ? "true" : "false")
              .Append(",\"newSession\":").Append(SafeFirstOfSession() ? "true" : "false")
              .Append("}");
            Append(sb.ToString());
        }

        // ── opt-in tick recorder ──────────────────────────────────────────────────────────────
        // ⚠ Fires realtime/replay only (never historical) — same NT8 platform constraint
        // SneakyExcursionRecorder's own OnMarketData relies on, and the reason this cannot backfill
        // a tick history for a date that already happened. Same data thread as OnBarUpdate, so no
        // lock is needed around the shared buffer/counters (mirrors ExcursionRecorder's reasoning).
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (!RecordTicks || _ticksWriterDead || _ticksLogPath == null) return;
            if (e.MarketDataType != MarketDataType.Last) return;

            var sb = new StringBuilder(96);
            sb.Append("{\"t\":").Append(Q(Iso(e.Time)))
              .Append(",\"px\":").Append(F(e.Price))
              .Append(",\"vol\":").Append(F(e.Volume))
              .Append("}");
            AppendTick(sb.ToString());
        }

        private void Append(string json)
        {
            if (_logPath == null || _buf == null) return;
            _buf.Append(json).Append(Environment.NewLine);
            _written++;
            _pending++;
            // Buffered because a historical rebuild can produce six figures of bars and one file
            // append per bar would put disk latency inside the bar path. Flushed often enough that
            // a crash costs a few hundred rows, not the run.
            if (_pending >= Math.Max(1, FlushEveryBars)) Flush();
        }

        private void Flush()
        {
            if (_logPath == null || _buf == null || _buf.Length == 0) return;
            try
            {
                File.AppendAllText(_logPath, _buf.ToString());
                _buf.Length = 0;
                _pending = 0;
                _writeFailed = false;
            }
            catch (Exception ex)
            {
                // Say so on the card rather than silently producing a short file -- a truncated
                // answer key that looks complete is worse than no answer key.
                _writeFailed = true;
                _lastErr = ex.Message;
                SneakyCore.Swallow("SneakyBarDump.Flush", ex);
            }
        }

        private void AppendTick(string json)
        {
            if (_ticksLogPath == null || _ticksBuf == null) return;
            _ticksBuf.Append(json).Append(Environment.NewLine);
            _ticksWritten++;
            _ticksPending++;
            // Ticks arrive far more often than bars, so this buffers on its own threshold
            // (FlushEveryTicks) rather than sharing the bar dump's FlushEveryBars cadence.
            if (_ticksPending >= Math.Max(1, FlushEveryTicks)) FlushTicks();
        }

        private void FlushTicks()
        {
            if (_ticksLogPath == null || _ticksBuf == null || _ticksBuf.Length == 0) return;
            try
            {
                File.AppendAllText(_ticksLogPath, _ticksBuf.ToString());
                _ticksBuf.Length = 0;
                _ticksPending = 0;
                _ticksWriteFailed = false;
            }
            catch (Exception ex)
            {
                _ticksWriteFailed = true;
                _ticksLastErr = ex.Message;
                SneakyCore.Swallow("SneakyBarDump.FlushTicks", ex);
            }
        }

        // ── the Sneaky glass card ─────────────────────────────────────────────────────────────
        // 2026-08-07 — converted from SneakySkin.CardLayout.Place() (single, non-scrolling
        // placement — what the original real-repo source actually used) to the shared
        // PlaceScroll()/ISneakyScrollCard rotation every other Sneaky card participates in.
        // Not a bug I introduced — it's how the upstream file was built — but Sneaky's own
        // convention is that every card takes its turn in the shared rotation rather than
        // claiming a fixed spot, so converting it to match.
        protected override void OnRender(NinjaTrader.Gui.Chart.ChartControl chartControl, NinjaTrader.Gui.Chart.ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (!ShowInfo || RenderTarget == null || ChartPanel == null) return;
            try
            {
                if (_sp == null) _sp = new SneakySkin.Painter();
                _sp.Begin(RenderTarget);
                RenderCard();
                _sp.End();
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.OnRender", _sx); }
        }

        public void ConductorRender(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (!ShowInfo || rt == null) return;
            try { if (_sp == null) _sp = new SneakySkin.Painter(); _sp.Begin(rt); RenderCard(); }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.ConductorRender", _sx); }
            finally { try { _sp.End(); } catch { } }
        }

        private void RenderCard()
        {
            const float cw = 258f, ch = 104f;
            SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
            var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
                ChartPanel.X, ChartPanel.Y, ChartPanel.W, ChartPanel.H, cw, ch);
            if (slot.Width <= 0f) return;

            bool dead = _writerDead || _writeFailed;
            bool live = !dead;
            var edge = dead ? SneakySkin.CDown : (_written > 0 ? SneakySkin.CAccent : SneakySkin.CLine);
            var r = _sp.Card(slot.X, slot.Y, cw, ch, edge);

            _sp.Dot(r.Left + 5f, r.Top + 8f, live ? SneakySkin.CAccent : SneakySkin.CMute, live);
            _sp.Text("SNEAKY BAR DUMP", r.Left + 16f, r.Top, r.Width - 66f, 16f, SneakySkin.CInk, 11f, true);
            string st = dead ? "NO REC" : (_liveBars ? "LIVE" : "REBUILD");
            var stCol = dead ? SneakySkin.CDown : (_liveBars ? SneakySkin.CAccent : SneakySkin.CInk2);
            _sp.Pill(st, r.Right, r.Top - 1f, stCol);

            var lead = SharpDX.DirectWrite.TextAlignment.Leading;

            _sp.Text("BARS WRITTEN", r.Left, r.Top + 24f, 130f, 12f, SneakySkin.CMute, 9f, true);
            _sp.Text(_written.ToString("N0", CultureInfo.InvariantCulture),
                r.Left, r.Top + 32f, 130f, 28f, dead ? SneakySkin.CDown : SneakySkin.CAccent, 24f);

            _sp.Text("BUFFERED", r.Left + 140f, r.Top + 24f, 110f, 12f, SneakySkin.CMute, 9f, true);
            _sp.Text(_pending.ToString(CultureInfo.InvariantCulture),
                r.Left + 140f, r.Top + 33f, 110f, 20f, SneakySkin.CInk2, 15f, true);

            if (dead)
            {
                _sp.Text(_lastErr == null ? "write failed" : Trunc(_lastErr, 46),
                    r.Left, r.Top + 64f, r.Width, 14f, SneakySkin.CDown, 9.5f, false, lead, true);
            }
            else
            {
                _sp.Text("last bar  " + _lastBarTime.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    r.Left, r.Top + 64f, r.Width, 14f, SneakySkin.CInk2, 9.5f, false, lead, true);
            }

            string footer = InstName() + " · " + SafeFriendly();
            if (RecordTicks)
            {
                footer += "  ·  ticks " + _ticksWritten.ToString("N0", CultureInfo.InvariantCulture)
                        + (_ticksWriterDead || _ticksWriteFailed ? " (NO REC)" : "");
            }
            _sp.Text(footer,
                r.Left, r.Top + 80f, r.Width, 12f, SneakySkin.CMute, 9f, false, lead, true);
        }

        // ── small safe accessors (every one of these can throw on a half-built chart) ───────────
        private string InstName()
        {
            try { return Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : "unknown"; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.InstName", _sx); return "unknown"; }
        }

        private string BarTag()
        {
            try
            {
                string t = SneakyCore.BarTag(BarsPeriod);
                string ln = SneakyCore.LaneOf(ChartControl);
                return string.IsNullOrEmpty(ln) ? t : t + "@" + ln;
            }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.BarTag", _sx); return "unknown"; }
        }

        private string SafeFriendly()
        {
            try { return SneakyCore.FriendlyBartag(BarTag()); }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.Friendly", _sx); return BarTag(); }
        }
        private string SafeCoreVer()
        {
            try { return SneakyCore.Version; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.CoreVer", _sx); return "?"; }
        }
        private double SafeTickSize()
        {
            try { return Instrument.MasterInstrument.TickSize; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.TickSize", _sx); return double.NaN; }
        }
        private double SafePointValue()
        {
            try { return Instrument.MasterInstrument.PointValue; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.PointValue", _sx); return double.NaN; }
        }
        private double SafePeriodTypeId()
        {
            try { return (int)BarsPeriod.BarsPeriodType; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.PeriodType", _sx); return double.NaN; }
        }
        private double SafePeriodValue()
        {
            try { return BarsPeriod.Value; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.PeriodValue", _sx); return double.NaN; }
        }
        private double SafePeriodValue2()
        {
            try { return BarsPeriod.Value2; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.PeriodValue2", _sx); return double.NaN; }
        }
        private double SafeBaseValue()
        {
            try { return BarsPeriod.BaseBarsPeriodValue; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.BaseValue", _sx); return double.NaN; }
        }
        private string SafeTradingHours()
        {
            try { return Bars != null && Bars.TradingHours != null ? Bars.TradingHours.Name : "?"; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.TradingHours", _sx); return "?"; }
        }
        // ⭐⭐ "BREAK AT EOD", AND WHY IT MUST BE RECORDED RATHER THAN ASSUMED.
        // This is NinjaTrader's `Bars.IsResetOnNewTradingDay`, and the Python port's
        // `reset_on_new_session`. It changes where EVERY bar boundary falls, so the two columns
        // cannot be compared without it. ⇒ A settings mismatch would surface as a BAR DISAGREEMENT
        // and be debugged as a porting bug.
        // ⛔ It cannot simply be set to a known value either: the property is READ-ONLY, and it
        //    appears nowhere in `BarsProperties`, in `BarsPeriod`, or in any chart template.
        //    **Recording it is the only defence available.**
        // Emitted as a nullable-ish flag: -1 means "could not read", which is NOT the same claim as
        // false, and the gate must treat it as unknown rather than as "EOD off".
        private double SafeResetOnNewTradingDay()
        {
            try { return Bars != null ? (Bars.IsResetOnNewTradingDay ? 1 : 0) : -1; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.ResetOnNewTradingDay", _sx); return -1; }
        }
        private bool SafeFirstOfSession()
        {
            try { return Bars.IsFirstBarOfSession; }
            catch (Exception _sx) { SneakyCore.Swallow("SneakyBarDump.FirstOfSession", _sx); return false; }
        }

        private static string Trunc(string s, int n) { return s == null ? "" : (s.Length <= n ? s : s.Substring(0, n)); }
        private static string F(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return Math.Round(v, 6).ToString(CultureInfo.InvariantCulture);
        }
        private static string Iso(DateTime dt) { return dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
        private static string Q(string s)
        {
            if (s == null) return "null";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Show info card", Description = "Draw the Sentinel glass-card readout (off = pure headless dumper).", GroupName = "Bar Dump", Order = 1)]
        public bool ShowInfo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Card corner", Description = "Which chart corner the card docks to. Cards in the same corner auto-stack (never overlap).", GroupName = "Bar Dump", Order = 2)]
        public SneakyCardCorner CardCorner { get; set; }

        // NOT a [NinjaScriptProperty] — serializes to the workspace + shows in F6 but stays OUT of the
        // generated constructor (no codegen churn).
        [Display(Name = "Flush every N bars", Description = "Write the buffer to disk every N bars. Lower = less lost on a crash, more disk churn during a rebuild.", GroupName = "Bar Dump", Order = 3)]
        public int FlushEveryBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Record ticks", Description = "Also write a continuous raw-tick transcript to Sentinel\\Harness\\ticks\\. Default OFF. REALTIME/REPLAY ONLY — does not backfill historical ticks (NT8 never fires OnMarketData for historical data).", GroupName = "Bar Dump", Order = 4)]
        public bool RecordTicks { get; set; }

        // NOT a [NinjaScriptProperty] — same "no codegen churn" reasoning as FlushEveryBars above.
        [Display(Name = "Flush every N ticks", Description = "Write the tick buffer to disk every N ticks. Ticks arrive far more often than bars, so this has its own (larger) cadence.", GroupName = "Bar Dump", Order = 5)]
        public int FlushEveryTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show indicator label", Description = "Show NinjaTrader's chart name label. Sentinel default = OFF (clean chart); turn on to restore it.", GroupName = "Sneaky", Order = 100)]
        public bool ShowIndicatorLabel { get; set; }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.SneakyBarDump_v1_0_0[] cacheSneakyBarDump_v1_0_0;
		public Sneaky.SneakyBarDump_v1_0_0 SneakyBarDump_v1_0_0(bool showInfo, SneakyCardCorner cardCorner, bool recordTicks, bool showIndicatorLabel)
		{
			return SneakyBarDump_v1_0_0(Input, showInfo, cardCorner, recordTicks, showIndicatorLabel);
		}

		public Sneaky.SneakyBarDump_v1_0_0 SneakyBarDump_v1_0_0(ISeries<double> input, bool showInfo, SneakyCardCorner cardCorner, bool recordTicks, bool showIndicatorLabel)
		{
			if (cacheSneakyBarDump_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyBarDump_v1_0_0.Length; idx++)
					if (cacheSneakyBarDump_v1_0_0[idx] != null && cacheSneakyBarDump_v1_0_0[idx].ShowInfo == showInfo && cacheSneakyBarDump_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyBarDump_v1_0_0[idx].RecordTicks == recordTicks && cacheSneakyBarDump_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyBarDump_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyBarDump_v1_0_0[idx];
			return CacheIndicator<Sneaky.SneakyBarDump_v1_0_0>(new Sneaky.SneakyBarDump_v1_0_0(){ ShowInfo = showInfo, CardCorner = cardCorner, RecordTicks = recordTicks, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyBarDump_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.SneakyBarDump_v1_0_0 SneakyBarDump_v1_0_0(bool showInfo, SneakyCardCorner cardCorner, bool recordTicks, bool showIndicatorLabel)
		{
			return indicator.SneakyBarDump_v1_0_0(Input, showInfo, cardCorner, recordTicks, showIndicatorLabel);
		}

		public Indicators.Sneaky.SneakyBarDump_v1_0_0 SneakyBarDump_v1_0_0(ISeries<double> input , bool showInfo, SneakyCardCorner cardCorner, bool recordTicks, bool showIndicatorLabel)
		{
			return indicator.SneakyBarDump_v1_0_0(input, showInfo, cardCorner, recordTicks, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.SneakyBarDump_v1_0_0 SneakyBarDump_v1_0_0(bool showInfo, SneakyCardCorner cardCorner, bool recordTicks, bool showIndicatorLabel)
		{
			return indicator.SneakyBarDump_v1_0_0(Input, showInfo, cardCorner, recordTicks, showIndicatorLabel);
		}

		public Indicators.Sneaky.SneakyBarDump_v1_0_0 SneakyBarDump_v1_0_0(ISeries<double> input , bool showInfo, SneakyCardCorner cardCorner, bool recordTicks, bool showIndicatorLabel)
		{
			return indicator.SneakyBarDump_v1_0_0(input, showInfo, cardCorner, recordTicks, showIndicatorLabel);
		}
	}
}

#endregion
