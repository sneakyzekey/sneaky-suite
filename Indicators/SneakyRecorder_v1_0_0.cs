// ─────────────────────────────────────────────────────────────────────────────
// This Source Code Form is subject to the terms of the Mozilla Public License,
// v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.
//
// Copyright (c) 2026 silentsudo-io and the Sentinel Suite contributors.
// ─────────────────────────────────────────────────────────────────────────────
//
// ═════════════════════════════════════════════════════════════════════════════
//  SNEAKY RECORDER v1.0.0 — the thing that lets every other number be checked.
//
//  WHY THIS EXISTS
//  This suite now carries ~20 Council voters, three modulators, a Confluence
//  cascade and an orthogonal qualifier. Almost every weight and threshold in it
//  is REASONED, NOT MEASURED — including all of mine. There is currently no
//  mechanism that could ever tell us which of those guesses is wrong.
//
//  This is that mechanism. It writes one row per observation: the full fused
//  state at decision time, then what price actually did afterwards at fixed
//  horizons. With enough rows you can answer the questions that are unanswerable
//  today:
//      • does conviction 60% actually beat conviction 30%?
//      • does Eye agreeing with the Council beat the Council alone?
//      • which voters carry predictive weight and which are noise?
//      • does the dry-up damp help, or does it only cost trades?
//
//  ── IT RECORDS, IT DOES NOT JUDGE ───────────────────────────────────────────
//  Deliberately dumb. It computes no edge, filters nothing, and has no opinion
//  about what a good row looks like. Every horizon outcome is raw MFE/MAE/close
//  in ticks, unsigned by any thesis. If this tool decided what counted as a
//  win, it would bake in the same assumptions it exists to test.
//
//  ── FORWARD OUTCOMES ARE WRITTEN LATE, ON PURPOSE ───────────────────────────
//  A row cannot be complete at the moment it is observed — the outcome has not
//  happened yet. So rows are held open in memory and flushed once their longest
//  horizon has elapsed. Consequence worth knowing: the tail of any session is
//  UNFLUSHED and will be lost if NinjaTrader is killed. FlushIncompleteOnStop
//  writes what is available at Terminated, marked incomplete, so partial data is
//  labelled rather than silently missing.
//
//  ── HOUSE CONVENTIONS FOLLOWED ──────────────────────────────────────────────
//  Same shape as CompressionBase's excursion log: JSONL, one object per line,
//  under <SettingsDir>\Recorder\, filename {stamp}__{inst}__{bartag}.jsonl.
//  Invariant culture on every number, ISO-8601 timestamps, nulls where a sensor
//  was absent (never 0 — "absent" and "zero" are different facts and conflating
//  them would corrupt the analysis).
//
//  ── 2026-09-15 — EYE PER-SOURCE BREAKDOWN ───────────────────────────────────
//  eyeDir/eyeScore only ever captured Eye's FUSED verdict, so no recorded row
//  could say which of CVD-DIR / CVD-DIVERG / LIQ actually drove it — needed to
//  test whether CVD-DIVERG (which can only ever match CVD-DIR's sign per
//  SneakyEye's own 2026-09-15 header) is what's behind the "Eye agrees" edge
//  inversion found twice in grading. New columns eyeCvdDir/eyeCvdDiverg/
//  eyeCvdTick/eyeLiqBlock/eyeEvidence, same honest-null convention, gated on
//  HaveEye exactly like eyeDir/eyeScore already were.
//
//  Places no orders. Reads seams only.
// ═════════════════════════════════════════════════════════════════════════════

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns.Sneaky;
// 2026-08-19 — REQUIRED for NinjaScript's own auto-generated wrapper region (the
// "#region NinjaScript generated code" block it appends below on compile) to resolve
// SneakyRecordMode. That generated code lives in namespace NinjaTrader.NinjaScript.Indicators
// (a PARENT of this file's own NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors namespace),
// and C# doesn't search into child namespaces automatically — only SneakyCardCorner happened
// to resolve there because AddOns.Sneaky (above) is already usinged; nothing previously made
// this file's own namespace visible to the generated block living one level up. A using
// placed here (outside any namespace block) applies file-wide, including to that generated
// region appended later in the same file — which is what actually fixes "The type or
// namespace name 'SneakyRecordMode' could not be found" at compile time.
using NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.Sneaky.Sensors
{
	public class SneakyRecorder_v1_0_0 : Indicator, ISneakyScrollCard
	{
		private const string SchemaVer = "rec-1";

		private sealed class Row
		{
			public int      Bar;
			public DateTime Time;
			public double   Price;

			// fused state at observation time
			public int      CouncilBias;
			public double   CouncilConv, CouncilSize, CouncilNet, CouncilDeclared, CouncilActive;
			public int      Voters, Agree, Against;
			public bool     CouncilVetoed;
			public string   CouncilVeto;
			public string   CnlInstance;   // v2026-08-04 — Council's InstanceTag, so multiple colliding
											// instances on one scope can be told apart after the fact
			public string   Scope;         // v2026-08-04 — the FULL resolved scope this row's seams were
											// actually consulted under (e.g. "NQ.4v1x1@D"), not just the
											// instance tag. CnlInstance always reads correctly straight off
											// Council's own property regardless of lane state, so it can't
											// by itself reveal a lane-registration gap (see AssertLane in
											// Council/Confluence, 2026-08-04) — during a gap, every seam this
											// row consults (GetTrendState/GetAdxState/etc.) was read from the
											// BARE, unlaned scope, which a DIFFERENT still-unlaned chart could
											// simultaneously be writing to. A row where Scope doesn't end in
											// "@" + CnlInstance was recorded during exactly that window and
											// its sensor fields (trendDir, lvlSide, mtfBias, ...) are suspect
											// even though CnlInstance itself is correct. Cheap to add: `sc` is
											// already recomputed fresh every bar just below.
			public string   CouncilReasons;

			public bool     HaveEye;
			public int      EyeDir;
			public double   EyeScore;
			// 2026-09-15 -- per-source breakdown, added alongside SneakyCore v1.46.0's EyeVerdict
			// extension specifically to test whether CVD-DIVERG (which can only ever match CVD-DIR's
			// sign, never oppose it) is what's driving the "Eye agrees" edge inversion found twice in
			// grading. Same honest-null convention as every other sensor here: only meaningful when
			// HaveEye is true, same as EyeDir/EyeScore above.
			public int      EyeCvdDir, EyeCvdDivergence, EyeLiqBlock;
			public bool     EyeCvdTickBacked;
			public double   EyeEvidence;

			// orthogonal sources
			public bool     HavePrs, HaveImkt, HavePart, HaveCvb;
			public int      PrsDir, ImktLean, CvbBias;
			public double   PrsStrength, ImktScore, Rvol, VolZ, CvbConv;
			public bool     PrsTick, Climax, DryUp;

			// price-derived context
			public bool     HaveClock;
			public int      Phase, SinceOpen, ToClose, Dow;
			public bool     InSession, InKill;

			public bool     HaveTrend, HaveAdx, HaveRegime;
			public int      TrendDir, Regime;
			public bool     AdxStrong;

			public bool     HaveCnf;
			public int      CnfBias, CnfSignal;
			public bool     CnfFired;
			public string   CnfInstance;   // v2026-08-06 — see SneakyCore.ConfluenceState.InstanceTag comment
			public bool     CnfRegimeOk, CnfAdxOk, CnfLiqOk, CnfVolOk, CnfZscoreOk, CnfCvbOk, CnfOptOk;
			public bool     CnfImktOk, CnfEyeOk, CnfFlowOk, CnfPartOk, CnfStructOk, CnfClockOk;
			public bool     CnfLevelOk, CnfMtfOk, CnfProfOk, CnfChopOk;
			public bool     CnfProfOverextOk;   // 2026-08-05 — see SneakyCore.ConfluenceState.ProfOverextOk comment
			public bool     CnfFluxOk;          // 2026-08-06 — see SneakyCore.ConfluenceState.FluxOk comment
			public bool     CnfTfusOk;          // 2026-08-06 — see SneakyCore.ConfluenceState.TfusOk comment
			public bool     CnfArchOk;          // 2026-08-07 — see SneakyCore.ConfluenceState.ArchOk comment
			public bool     CnfNewsOk;          // 2026-08-08 — see SneakyCore.ConfluenceState.NewsOk comment
			public bool     CnfTriggerLong, CnfTriggerShort;
			public string   CnfBlockedBy, CnfTriggerLabel;

			// v2026-08-02 — SneakyLocation/SneakyMtf/SneakyProfile publishers, same "Have"-gated
			// pattern as every other orthogonal source above (absent sensor = null, never 0).
			public bool     HaveLvl;
			public int      LvlSide;
			public string   LvlNearestName;
			public double   LvlDistTicks, LvlDistAtr;

			public bool     HaveMtf;
			public int      MtfBias, MtfAlignedCount, MtfTfCount;
			public double   MtfAlignment;
			public bool     MtfAllAgree;

			public bool     HaveProf;
			public int      ProfLocation, ProfSignal;
			public double   ProfDistPocTicks;
			public bool     ProfNearHvn, ProfNearLvn;

			public bool     HaveCnlEyeVote;
			public int      CnlEyeVote;
			public int      CnlPreVetoBias;
			public bool     CnlKillWindowActive;
			public bool     CnlBreadthDamped, CnlSqueezeDamped, CnlDryUpDamped, CnlInValueDamped;
			public bool     CnlProfOverextDamped;   // 2026-08-06 — see SneakyCore.CouncilState.ProfOverextDamped comment
			// v2026-08-02 — the raw per-voter decision vector (ML spec §2.1's whole point: FIT the
			// weights, don't just grade the output). Council already copies these into a fresh dict
			// per update (see SetCouncilState's own doc comment), so holding a reference here is safe.
			public Dictionary<string,int>    CnlVotes;
			public Dictionary<string,double> CnlVoteW;

			// forward outcomes, filled later
			public Dictionary<int, double> MfeTicks = new Dictionary<int, double>();
			public Dictionary<int, double> MaeTicks = new Dictionary<int, double>();
			public Dictionary<int, double> ClsTicks = new Dictionary<int, double>();
			public double RunHi = double.MinValue, RunLo = double.MaxValue;
			public bool   Complete;

			// v2026-08-04 — triple-barrier labeling: was price ever driven BarrierTicks away
			// from entry, and which direction, before this row's horizon ran out? BarrierHit
			// stays 0 (never crossed) until either barrier fires; BarrierBars records HOW MANY
			// BARS it took. A row that completes with BarrierHit still 0 is CENSORED — the
			// honest answer is "time ran out before either barrier fired," never silently
			// relabeled as a win, a loss, or a flat outcome.
			public int  BarrierHit;        // 0 = never crossed, +1 = upper hit first, -1 = lower
			public int  BarrierBars = -1;  // bars until BarrierHit fired; stays -1 while still 0
			public bool Censored;          // true iff BarrierHit was still 0 at write time
			// BUGFIX (2026-08-22, P1 finding #9): true when a single bar's range crossed BOTH
			// barriers at once (wide/gap bar) — order-of-touch is unknowable from OHLC alone.
			// Mirrors SneakyExcursionRecorder_v2_0_0.cs / SneakyCandidateRecorder_v1_0_0.cs's ftAmbig.
			public bool FtAmbig;
		}

		private readonly List<Row> _open = new List<Row>();
		private int[] _horizons = new int[0];
		private string _logPath;
		private int _written, _held;
		private SneakySkin.Painter _sp;
		private DateTime _lastRenderExLog = DateTime.MinValue;
		// 2026-08-19 — A2 parity with SneakyExcursionRecorder_v2_0_0's own fix: before this, a failed
		// write (or a failed corpus-dir setup) here was invisible outside a Print line in the Output
		// tab — the on-chart card kept showing a healthy-looking stale "N rows written" with no sign
		// new rows were being lost. _writerDead = the one-time corpus-dir setup itself failed (nothing
		// will ever be recorded this instance). _writeFailed = an individual write threw AFTER setup
		// succeeded (e.g. a locked file / permissions flip mid-session). Both are latched (logged once,
		// not per-row) and surfaced on the card as a "NO REC" pill so a silent drain can't hide.
		private bool _writerDead, _writeFailed;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Writes the full fused state (Council, Eye, Confluence sources, orthogonal axes) plus "
					+ "forward MFE/MAE/close at fixed horizons to JSONL, so the suite's weights and thresholds can be "
					+ "GRADED instead of guessed. Records only — no edge, no filtering, no opinion about what a good "
					+ "row looks like, because a recorder that judged would bake in the assumptions it exists to test. "
					+ "Rows flush once their longest horizon elapses. Output: <SettingsDir>\\Recorder\\. Places no orders.";
				Name                     = "Sneaky Recorder v1.0.0";
				Calculate                = Calculate.OnBarClose;
				IsOverlay                = true;
				DisplayInDataBox         = false;
				PaintPriceMarkers        = false;
				IsSuspendedWhileInactive = false;
				IsAutoScale              = false;
				ShowIndicatorLabel       = false;

				RecordMode        = SneakyRecordMode.EveryBar;
				HorizonBars       = "5,10,20,60";
				BarrierTicks      = 40.0;
				// 2026-08-19 — was 5.0. Every other seam-reader in the suite (ExcursionRecorder's
				// CouncilStaleSec, GetBrickState, GetConfluenceState, ...) uses a 90s staleness window.
				// A 5s window is far tighter than most publishers' own poll cadence, so a seam read
				// (esp. GetCouncilState) can come back stale/null even when the publisher is perfectly
				// healthy — and when that happens to cnl specifically, CnlVotes never gets set on the
				// row, which makes sneaky_to_lab.py's translator silently DROP the row entirely
				// ("no_votes"). Raised to 90s to match the suite convention and stop that quiet loss.
				StaleSec          = 90.0;
				RecordHistorical  = false;
				FlushIncompleteOnStop = true;

				ShowCard   = true;
				CardCorner = SneakyCardCorner.TopLeft;
			}
			else if (State == State.DataLoaded)
			{
				var hs = new List<int>();
				foreach (var part in (HorizonBars ?? "").Split(','))
				{
					int v;
					if (int.TryParse(part.Trim(), out v) && v > 0) hs.Add(v);
				}
				hs.Sort();
				_horizons = hs.ToArray();
				if (!ShowIndicatorLabel) Name = string.Empty;
			}
			else if (State == State.Terminated)
			{
				if (FlushIncompleteOnStop) FlushAll(true);
				if (_sp != null) { try { _sp.Dispose(); } catch { } _sp = null; }
				try { SneakySkin.CardLayout.Release(this); } catch { }
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 2 || _horizons.Length == 0) return;
			if (!RecordHistorical && State == State.Historical) { AdvanceOpen(); return; }

			AdvanceOpen();

			// ── read every seam once ──
			string sc = null; try { sc = SneakyCore.ScopeOf(Instrument, BarsPeriod, ChartControl); } catch { }
			string inst = null; try { inst = Instrument.MasterInstrument.Name; } catch { }

			SneakyCore.CouncilState      cnl  = null; SneakyCore.EyeVerdict        eye  = null;
			SneakyCore.PressureState     prs  = null; SneakyCore.IntermarketState  imk  = null;
			SneakyCore.ParticipationState par = null; SneakyCore.ConvictionState   cvb  = null;
			SneakyCore.TrendState        trd  = null; SneakyCore.AdxState          adx  = null;
			SneakyCore.RegimeState       reg  = null; SneakyCore.ClockState        clk  = null;
			SneakyCore.ConfluenceState   cnf  = null;
			SneakyCore.LevelState        lv   = null; SneakyCore.MtfState          mtf  = null;
			SneakyCore.ProfileState      prof = null;

			try { cnl = SneakyCore.GetCouncilState(sc, StaleSec); }        catch { }
			try { eye = SneakyCore.GetEyeVerdict(sc, StaleSec); }          catch { }
			try { prs = SneakyCore.GetPressureState(sc, StaleSec); }       catch { }
			try { par = SneakyCore.GetParticipationState(sc, StaleSec); }  catch { }
			try { cvb = SneakyCore.GetConvictionState(sc, StaleSec); }     catch { }
			try { trd = SneakyCore.GetTrendState(sc, StaleSec); }          catch { }
			try { adx = SneakyCore.GetAdxState(sc, StaleSec); }            catch { }
			try { reg = SneakyCore.GetRegimeState(sc, StaleSec); }         catch { }
			try { if (inst != null) imk = SneakyCore.GetIntermarketState(inst, StaleSec); } catch { }
			// Clock is INSTRUMENT-keyed. Recording phase on every row is what turns
			// "midday is bad" from a folk belief into a question the data can answer.
			try { if (inst != null) clk = SneakyCore.GetClockState(inst, StaleSec); } catch { }
			// Confluence is SCOPE-keyed (bar-type-dependent), same reasoning as Participation.
			try { cnf = SneakyCore.GetConfluenceState(sc, StaleSec); }     catch { }
			// Location/Mtf/Profile are all SCOPE-keyed too — same reasoning as Confluence.
			try { lv   = SneakyCore.GetLevelState(sc, StaleSec); }         catch { }
			try { mtf  = SneakyCore.GetMtfState(sc, StaleSec); }           catch { }
			try { prof = SneakyCore.GetProfileState(sc, StaleSec); }       catch { }

			// ── should this bar be recorded? ──
			bool take;
			switch (RecordMode)
			{
				case SneakyRecordMode.CouncilActionable:
					take = cnl != null && cnl.Bias != 0 && !cnl.Vetoed && cnl.SizeMult > 0.0; break;
				case SneakyRecordMode.CouncilNonFlat:
					take = cnl != null && cnl.Bias != 0; break;
				case SneakyRecordMode.ConfluenceFired:
					take = cnf != null && cnf.Fired; break;
				default:
					take = true; break;
			}
			if (!take) return;

			var r = new Row { Bar = CurrentBar, Time = Time[0], Price = Close[0], Scope = sc };

			if (cnl != null)
			{
				r.CouncilBias = cnl.Bias; r.CouncilConv = cnl.Conviction; r.CouncilSize = cnl.SizeMult;
				r.Voters = cnl.Voters; r.Agree = cnl.Agree; r.Against = cnl.Disagree;
				r.CouncilVetoed = cnl.Vetoed; r.CouncilVeto = cnl.VetoReason; r.CouncilReasons = cnl.Reasons;
				r.CnlInstance = cnl.InstanceTag;
				try { r.CouncilNet = cnl.NetScore; r.CouncilActive = cnl.ActiveW; } catch { }
				// EYE only appears in Votes at all when Council's 'Include Eye As Voter' is on —
				// absence here means the experiment is off, not that Eye abstained this bar.
				try
				{
					int ev;
					if (cnl.Votes != null && cnl.Votes.TryGetValue("EYE", out ev))
					{ r.HaveCnlEyeVote = true; r.CnlEyeVote = ev; }
				}
				catch { }
				// PreVetoBias/KillWindowActive need SneakyCore v1.39.0+ on the Council publisher —
				// an older Council still compiles and runs, these just default to 0/false (same
				// convention as every other optional trailing param in this suite).
				try { r.CnlPreVetoBias = cnl.PreVetoBias; r.CnlKillWindowActive = cnl.KillWindowActive; } catch { }
				try { r.CnlBreadthDamped = cnl.BreadthDamped; r.CnlSqueezeDamped = cnl.SqueezeDamped;
					  r.CnlDryUpDamped = cnl.DryUpDamped; r.CnlInValueDamped = cnl.InValueDamped; } catch { }
				try { r.CnlProfOverextDamped = cnl.ProfOverextDamped; } catch { }
				try { r.CnlVotes = cnl.Votes; r.CnlVoteW = cnl.VoteW; } catch { }
			}
			if (eye != null) { r.HaveEye = true; r.EyeDir = eye.Direction; r.EyeScore = eye.Score;
				r.EyeCvdDir = eye.CvdDir; r.EyeCvdDivergence = eye.CvdDivergence;
				r.EyeCvdTickBacked = eye.CvdTickBacked; r.EyeLiqBlock = eye.LiqBlock; r.EyeEvidence = eye.Evidence; }
			if (prs != null) { r.HavePrs = true; r.PrsDir = prs.Dir; r.PrsStrength = prs.Strength; r.PrsTick = prs.TickBacked; }
			if (imk != null) { r.HaveImkt = true; r.ImktLean = imk.Lean; r.ImktScore = imk.Score; }
			if (par != null) { r.HavePart = true; r.Rvol = par.Rvol; r.VolZ = par.VolZ; r.Climax = par.Climax; r.DryUp = par.DryUp; }
			if (cvb != null) { r.HaveCvb = true; r.CvbBias = cvb.Bias; r.CvbConv = cvb.Conviction; }
			if (clk != null) { r.HaveClock = true; r.Phase = clk.Phase; r.SinceOpen = clk.MinsSinceOpen;
							   r.ToClose = clk.MinsToClose; r.Dow = clk.DayOfWeek;
							   r.InSession = clk.InSession; r.InKill = clk.InKillWindow; }
			if (trd != null) { r.HaveTrend = true; r.TrendDir = trd.Direction; }
			if (adx != null) { r.HaveAdx = true; r.AdxStrong = adx.Strong; }
			if (cnf != null)
			{
				r.HaveCnf = true; r.CnfBias = cnf.Bias; r.CnfSignal = cnf.Signal; r.CnfFired = cnf.Fired;
			r.CnfInstance = cnf.InstanceTag;
				r.CnfRegimeOk = cnf.RegimeOk; r.CnfAdxOk = cnf.AdxOk; r.CnfLiqOk = cnf.LiqOk;
				r.CnfVolOk = cnf.VolOk; r.CnfZscoreOk = cnf.ZscoreOk; r.CnfCvbOk = cnf.CvbOk; r.CnfOptOk = cnf.OptOk;
				r.CnfImktOk = cnf.ImktOk; r.CnfEyeOk = cnf.EyeOk; r.CnfFlowOk = cnf.FlowOk;
				r.CnfPartOk = cnf.PartOk; r.CnfStructOk = cnf.StructOk; r.CnfClockOk = cnf.ClockOk;
				r.CnfLevelOk = cnf.LevelOk; r.CnfMtfOk = cnf.MtfOk; r.CnfProfOk = cnf.ProfOk; r.CnfChopOk = cnf.ChopOk;
			r.CnfProfOverextOk = cnf.ProfOverextOk;
			r.CnfFluxOk = cnf.FluxOk;
			r.CnfTfusOk = cnf.TfusOk;
			r.CnfArchOk = cnf.ArchOk;
			r.CnfNewsOk = cnf.NewsOk;
				r.CnfTriggerLong = cnf.TriggerLong; r.CnfTriggerShort = cnf.TriggerShort;
				r.CnfBlockedBy = cnf.BlockedBy; r.CnfTriggerLabel = cnf.TriggerLabel;
			}
			if (reg != null) { r.HaveRegime = true; r.Regime = reg.Regime; }
			if (lv != null)   { r.HaveLvl = true; r.LvlSide = lv.VwapSide; r.LvlNearestName = lv.NearestName;
								 r.LvlDistTicks = lv.NearestDistTicks; r.LvlDistAtr = lv.NearestDistAtr; }
			if (mtf != null)  { r.HaveMtf = true; r.MtfBias = mtf.Bias; r.MtfAlignment = mtf.AlignmentScore;
								 r.MtfAlignedCount = mtf.AlignedCount; r.MtfTfCount = mtf.TfCount; r.MtfAllAgree = mtf.AllAgree; }
			if (prof != null) { r.HaveProf = true; r.ProfLocation = prof.Location; r.ProfSignal = prof.Signal;
								 r.ProfDistPocTicks = prof.DistPocTicks; r.ProfNearHvn = prof.NearHVN; r.ProfNearLvn = prof.NearLVN; }

			_open.Add(r);
			_held = _open.Count;
		}

		/// <summary>Roll forward every open row; flush the ones whose longest horizon has elapsed.</summary>
		private void AdvanceOpen()
		{
			if (_open.Count == 0) return;
			int longest = _horizons[_horizons.Length - 1];
			double hi = High[0], lo = Low[0], cl = Close[0], tick = TickSize > 0 ? TickSize : 0.25;

			for (int i = _open.Count - 1; i >= 0; i--)
			{
				var r = _open[i];
				int age = CurrentBar - r.Bar;
				if (age <= 0) continue;

				if (hi > r.RunHi) r.RunHi = hi;
				if (lo < r.RunLo) r.RunLo = lo;

				// triple-barrier check — first crossing only, whichever side fires first.
				// Checked EVERY bar (not just at horizon checkpoints), same as RunHi/RunLo,
				// so BarrierBars is a genuine bar-count-to-first-crossing, not an approximation
				// snapped to the nearest recorded horizon.
				if (r.BarrierHit == 0 && !r.FtAmbig && tick > 0)
				{
					// BUGFIX (2026-08-22, P1 finding #9): a wide/gap bar can cross BOTH barriers within
					// the same bar; OHLC alone can't tell which was touched first, so the old if/else-if
					// silently fell through to BarrierHit=+1 (upper-hit-first) every time — a confident
					// guess on exactly the high-volatility bars that matter most for training. Matches
					// SneakyExcursionRecorder_v2_0_0.cs (~line 1039, same-bar FtFavBar==FtAdvBar) and
					// SneakyCandidateRecorder_v1_0_0.cs (~line 612): leave BarrierHit at its "unresolved"
					// sentinel (0) and raise FtAmbig instead of guessing a direction.
					bool barUp = (hi - r.Price) / tick >= BarrierTicks;
					bool barDn = (r.Price - lo) / tick >= BarrierTicks;
					if (barUp && barDn) { r.FtAmbig = true; r.BarrierBars = age; }
					else if (barUp) { r.BarrierHit = 1; r.BarrierBars = age; }
					else if (barDn) { r.BarrierHit = -1; r.BarrierBars = age; }
				}

				foreach (int h in _horizons)
				{
					if (age != h) continue;
					// Raw and UNSIGNED by any thesis: excursion above and below entry, plus
					// where it closed. Sign them at analysis time against whatever direction
					// hypothesis you are testing — that keeps the data reusable.
					r.MfeTicks[h] = (r.RunHi - r.Price) / tick;
					r.MaeTicks[h] = (r.RunLo - r.Price) / tick;
					r.ClsTicks[h] = (cl - r.Price) / tick;
				}

				// BUGFIX (2026-08-22, P1 finding #9): BarrierHit==0 no longer means "never crossed" on
				// its own now that an ambiguous same-bar hit also leaves it at 0 — guard with !FtAmbig
				// so an ambiguous row (a barrier DID fire) isn't mislabeled Censored ("time ran out").
				if (age >= longest) { r.Complete = true; r.Censored = (r.BarrierHit == 0 && !r.FtAmbig); Write(r); _open.RemoveAt(i); }
			}
			_held = _open.Count;
		}

		private void FlushAll(bool markIncomplete)
		{
			for (int i = _open.Count - 1; i >= 0; i--)
			{
				var r = _open[i];
				if (markIncomplete) r.Complete = false;
				r.Censored = (r.BarrierHit == 0 && !r.FtAmbig);   // BUGFIX (2026-08-22, P1 finding #9): see AdvanceOpen
				Write(r);
				_open.RemoveAt(i);
			}
			_held = 0;
		}

		// ── JSON writers, invariant culture throughout ──
		private static void S(StringBuilder b, string k, string v)
		{ b.Append('"').Append(k).Append("\":"); if (v == null) b.Append("null"); else b.Append('"').Append(v.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); }
		private static void N(StringBuilder b, string k, double v)
		{ b.Append('"').Append(k).Append("\":").Append(v.ToString("0.######", CultureInfo.InvariantCulture)); }
		private static void I(StringBuilder b, string k, int v)
		{ b.Append('"').Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture)); }

		// v2026-08-02 — the raw decision vector as nested JSON: {"TREND":{"dir":1,"w":1.0}, ...}.
		// null (not {}) when Council never published a vector at all (pre-1.24.0 publisher), so a
		// training script can tell "no vector" from "vector was empty" — the same distinction the
		// ML spec draws for an absent Votes key vs. an explicit 0.
		private static void VotesObj(StringBuilder b, string k, Dictionary<string,int> votes, Dictionary<string,double> voteW)
		{
			b.Append('"').Append(k).Append("\":");
			if (votes == null) { b.Append("null"); return; }
			b.Append('{');
			bool first = true;
			foreach (var kv in votes)
			{
				if (!first) b.Append(',');
				first = false;
				double w = 1.0;
				if (voteW != null) voteW.TryGetValue(kv.Key, out w);
				b.Append('"').Append(kv.Key.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\":{\"dir\":")
				 .Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append(",\"w\":")
				 .Append(w.ToString("0.######", CultureInfo.InvariantCulture)).Append('}');
			}
			b.Append('}');
		}
		private static void B(StringBuilder b, string k, bool v)
		{ b.Append('"').Append(k).Append("\":").Append(v ? "true" : "false"); }
		private static void Null(StringBuilder b, string k)
		{ b.Append('"').Append(k).Append("\":null"); }
		private static void C(StringBuilder b) { b.Append(','); }
		private static string Safe(string s)
		{ if (string.IsNullOrEmpty(s)) return "x"; var o = new StringBuilder(); foreach (char c in s) o.Append(char.IsLetterOrDigit(c) ? c : '-'); return o.ToString(); }

		// A2 — a failed write is surfaced ONCE (not per-row spam), same discipline as
		// SneakyExcursionRecorder_v2_0_0.NoteWriteFail. The card flags it via the "NO REC" pill.
		private void NoteWriteFail(Exception ex)
		{
			if (_writeFailed) return;
			_writeFailed = true;
			try { Print("[SneakyRecorder] WRITE FAILED — rows are being LOST (" + ex.Message + "). Fix the sink; the card flags it."); } catch { }
		}

		// A2 — the corpus-dir setup itself failed (bad path, permissions, disk full at first write).
		// Distinct from NoteWriteFail: this is a ONE-SHOT failure that can never self-heal within the
		// session (the same setup only ever runs once, on the first Write() call), so it latches
		// _writerDead permanently rather than retrying/spamming on every subsequent bar's Write() call.
		private void NoteWriterDead(Exception ex)
		{
			try { Print("[SneakyRecorder] WRITER DEAD — could not open the corpus dir; NOTHING will be recorded this session (" + ex.Message + "). The card shows NO REC."); } catch { }
		}

		private void Write(Row r)
		{
			if (_writerDead) return;   // corpus dir failed to open earlier this session — don't keep retrying/spamming

			if (_logPath == null)
			{
				try
				{
					string dir = Path.Combine(SneakyCore.SettingsDir, "Recorder", SchemaVer);
					Directory.CreateDirectory(dir);
					string stamp = DateTime.Now.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
					string inst = "x"; try { inst = Instrument.MasterInstrument.Name; } catch { }
					// BUGFIX (2026-08-22, P2 finding: filename collision): stamp is SECOND-precision, so two chart
					// instances of this recorder finishing setup within the same wall-clock second (common at
					// workspace load, when several charts init near-simultaneously) computed the IDENTICAL path
					// and could clobber/interleave each other's writes. A per-instance GUID suffix is used rather
					// than just widening the stamp to millisecond precision because workspace load can start
					// multiple charts within the same millisecond too -- only a per-instance random token is robust.
					string disambig = Guid.NewGuid().ToString("N").Substring(0, 6);
					_logPath = Path.Combine(dir, stamp + "__" + Safe(inst) + "__" + Safe(SneakyCore.BarTag(BarsPeriod)) + "__" + disambig + ".jsonl");
				}
				catch (Exception ex)
				{
					_writerDead = true;
					NoteWriterDead(ex);
					return;   // can't set up the sink at all — nothing downstream can be written
				}
			}

			try
			{
				var b = new StringBuilder(1024);
				b.Append('{');
				S(b, "schema", SchemaVer);   C(b); S(b, "kind", "observation"); C(b);
				S(b, "inst", Instrument != null && Instrument.MasterInstrument != null ? Instrument.MasterInstrument.Name : null); C(b);
				S(b, "bartype", SneakyCore.BarTag(BarsPeriod)); C(b);
				S(b, "time", r.Time.ToString("o", CultureInfo.InvariantCulture)); C(b);
				I(b, "bar", r.Bar);          C(b); N(b, "price", r.Price);      C(b);
				B(b, "complete", r.Complete); C(b);
				I(b, "barrierHit", r.BarrierHit); C(b);
				if (r.BarrierBars >= 0) { I(b, "barrierBars", r.BarrierBars); } else { Null(b, "barrierBars"); }
				C(b);
				B(b, "censored", r.Censored); C(b);
				// BUGFIX (2026-08-22, P1 finding #9): same-bar-both-barriers flag, matching the ftAmbig
				// field SneakyExcursionRecorder_v2_0_0.cs / SneakyCandidateRecorder_v1_0_0.cs already emit.
				B(b, "ftAmbig", r.FtAmbig); C(b);

				I(b, "cnlBias", r.CouncilBias);   C(b); N(b, "cnlConv", r.CouncilConv);  C(b);
				N(b, "cnlSize", r.CouncilSize);   C(b); N(b, "cnlNet", r.CouncilNet);    C(b);
				N(b, "cnlActive", r.CouncilActive); C(b);
				I(b, "voters", r.Voters);         C(b); I(b, "agree", r.Agree);          C(b);
				I(b, "against", r.Against);       C(b); B(b, "cnlVetoed", r.CouncilVetoed); C(b);
				S(b, "cnlVeto", r.CouncilVeto);   C(b); S(b, "cnlReasons", r.CouncilReasons); C(b);
				S(b, "cnlInstance", r.CnlInstance); C(b);
				// v2026-08-04 — see the Row.Scope field comment: lets a post-hoc check tell a row
				// recorded during a lane-registration gap (Scope doesn't end "@" + cnlInstance) apart
				// from a clean one, even though cnlInstance itself reads correctly either way.
				S(b, "scope", r.Scope); C(b);
				if (r.HaveCnlEyeVote) I(b, "cnlEyeVote", r.CnlEyeVote); else Null(b, "cnlEyeVote"); C(b);
				I(b, "cnlPreVetoBias", r.CnlPreVetoBias); C(b);
				B(b, "cnlKillWindowActive", r.CnlKillWindowActive); C(b);
				B(b, "cnlBreadthDamped", r.CnlBreadthDamped); C(b);
				B(b, "cnlSqueezeDamped", r.CnlSqueezeDamped); C(b);
				B(b, "cnlDryUpDamped", r.CnlDryUpDamped); C(b);
				B(b, "cnlInValueDamped", r.CnlInValueDamped); C(b);
				B(b, "cnlProfOverextDamped", r.CnlProfOverextDamped); C(b);
				VotesObj(b, "cnlVotes", r.CnlVotes, r.CnlVoteW); C(b);

				// null, never 0, when a sensor was absent — "absent" and "zero" are
				// different facts and conflating them would corrupt the analysis.
				if (r.HaveEye)  { I(b, "eyeDir", r.EyeDir); C(b); N(b, "eyeScore", r.EyeScore); }
				else            { Null(b, "eyeDir"); C(b); Null(b, "eyeScore"); }
				C(b);
				// 2026-09-15 -- per-source breakdown (see EyeVerdict.CvdDir/CvdDivergence/CvdTickBacked/
				// LiqBlock/Evidence in SneakyCore v1.46.0). Same honest-null gate as eyeDir/eyeScore above
				// -- these are only meaningful when HaveEye is true, never a standalone sensor presence.
				if (r.HaveEye)  { I(b, "eyeCvdDir", r.EyeCvdDir); C(b); I(b, "eyeCvdDiverg", r.EyeCvdDivergence); C(b);
					B(b, "eyeCvdTick", r.EyeCvdTickBacked); C(b); I(b, "eyeLiqBlock", r.EyeLiqBlock); C(b);
					N(b, "eyeEvidence", r.EyeEvidence); }
				else            { Null(b, "eyeCvdDir"); C(b); Null(b, "eyeCvdDiverg"); C(b);
					Null(b, "eyeCvdTick"); C(b); Null(b, "eyeLiqBlock"); C(b); Null(b, "eyeEvidence"); }
				C(b);
				if (r.HavePrs)  { I(b, "prsDir", r.PrsDir); C(b); N(b, "prsStr", r.PrsStrength); C(b); B(b, "prsTick", r.PrsTick); }
				else            { Null(b, "prsDir"); C(b); Null(b, "prsStr"); C(b); Null(b, "prsTick"); }
				C(b);
				if (r.HaveImkt) { I(b, "imktLean", r.ImktLean); C(b); N(b, "imktScore", r.ImktScore); }
				else            { Null(b, "imktLean"); C(b); Null(b, "imktScore"); }
				C(b);
				if (r.HavePart) { N(b, "rvol", r.Rvol); C(b); N(b, "volZ", r.VolZ); C(b); B(b, "climax", r.Climax); C(b); B(b, "dryUp", r.DryUp); }
				else            { Null(b, "rvol"); C(b); Null(b, "volZ"); C(b); Null(b, "climax"); C(b); Null(b, "dryUp"); }
				C(b);
				if (r.HaveCvb)  { I(b, "cvbBias", r.CvbBias); C(b); N(b, "cvbConv", r.CvbConv); }
				else            { Null(b, "cvbBias"); C(b); Null(b, "cvbConv"); }
				C(b);
				if (r.HaveClock) { I(b, "phase", r.Phase); C(b); I(b, "sinceOpen", r.SinceOpen); C(b);
								   I(b, "toClose", r.ToClose); C(b); I(b, "dow", r.Dow); C(b);
								   B(b, "inSession", r.InSession); C(b); B(b, "inKill", r.InKill); }
				else             { Null(b, "phase"); C(b); Null(b, "sinceOpen"); C(b); Null(b, "toClose"); C(b);
								   Null(b, "dow"); C(b); Null(b, "inSession"); C(b); Null(b, "inKill"); }
				C(b);
				if (r.HaveTrend) I(b, "trendDir", r.TrendDir); else Null(b, "trendDir"); C(b);
				if (r.HaveAdx)   B(b, "adxStrong", r.AdxStrong); else Null(b, "adxStrong"); C(b);
				if (r.HaveRegime) I(b, "regime", r.Regime); else Null(b, "regime"); C(b);

				if (r.HaveCnf)
				{
					I(b, "cnfBias", r.CnfBias);         C(b); I(b, "cnfSignal", r.CnfSignal);   C(b);
					B(b, "cnfFired", r.CnfFired);       C(b);
					S(b, "cnfInstance", r.CnfInstance); C(b);
					B(b, "cnfRegimeOk", r.CnfRegimeOk); C(b); B(b, "cnfAdxOk", r.CnfAdxOk);      C(b);
					B(b, "cnfLiqOk", r.CnfLiqOk);       C(b); B(b, "cnfVolOk", r.CnfVolOk);      C(b);
					B(b, "cnfZscoreOk", r.CnfZscoreOk); C(b); B(b, "cnfCvbOk", r.CnfCvbOk);      C(b);
					B(b, "cnfOptOk", r.CnfOptOk);       C(b);
					B(b, "cnfImktOk", r.CnfImktOk);     C(b); B(b, "cnfEyeOk", r.CnfEyeOk);      C(b);
					B(b, "cnfFlowOk", r.CnfFlowOk);     C(b); B(b, "cnfPartOk", r.CnfPartOk);    C(b);
					B(b, "cnfStructOk", r.CnfStructOk); C(b); B(b, "cnfClockOk", r.CnfClockOk);  C(b);
					B(b, "cnfLevelOk", r.CnfLevelOk);   C(b); B(b, "cnfMtfOk", r.CnfMtfOk);       C(b); B(b, "cnfProfOk", r.CnfProfOk); C(b); B(b, "cnfChopOk", r.CnfChopOk); C(b);
				B(b, "cnfProfOverextOk", r.CnfProfOverextOk); C(b);
				B(b, "cnfFluxOk", r.CnfFluxOk); C(b);
				B(b, "cnfTfusOk", r.CnfTfusOk); C(b);
				B(b, "cnfArchOk", r.CnfArchOk); C(b);
				B(b, "cnfNewsOk", r.CnfNewsOk); C(b);
					B(b, "cnfTriggerLong", r.CnfTriggerLong);   C(b); B(b, "cnfTriggerShort", r.CnfTriggerShort); C(b);
					S(b, "cnfBlockedBy", r.CnfBlockedBy);       C(b); S(b, "cnfTriggerLabel", r.CnfTriggerLabel);
				}
				else
				{
					Null(b, "cnfBias"); C(b); Null(b, "cnfSignal"); C(b); Null(b, "cnfFired"); C(b);
					Null(b, "cnfInstance"); C(b);
					Null(b, "cnfRegimeOk"); C(b); Null(b, "cnfAdxOk"); C(b); Null(b, "cnfLiqOk"); C(b);
					Null(b, "cnfVolOk"); C(b); Null(b, "cnfZscoreOk"); C(b); Null(b, "cnfCvbOk"); C(b);
					Null(b, "cnfOptOk"); C(b);
					Null(b, "cnfImktOk"); C(b); Null(b, "cnfEyeOk"); C(b); Null(b, "cnfFlowOk"); C(b);
					Null(b, "cnfPartOk"); C(b); Null(b, "cnfStructOk"); C(b); Null(b, "cnfClockOk"); C(b);
					Null(b, "cnfLevelOk"); C(b); Null(b, "cnfMtfOk"); C(b); Null(b, "cnfProfOk"); C(b); Null(b, "cnfChopOk"); C(b);
				Null(b, "cnfProfOverextOk"); C(b);
				Null(b, "cnfFluxOk"); C(b);
				Null(b, "cnfTfusOk"); C(b);
				Null(b, "cnfArchOk"); C(b);
				Null(b, "cnfNewsOk"); C(b);
					Null(b, "cnfTriggerLong"); C(b); Null(b, "cnfTriggerShort"); C(b);
					Null(b, "cnfBlockedBy"); C(b); Null(b, "cnfTriggerLabel");
				}
				C(b);

				// v2026-08-02 — SneakyLocation/SneakyMtf/SneakyProfile, same Have-gated null convention.
				if (r.HaveLvl) { I(b, "lvlSide", r.LvlSide); C(b); S(b, "lvlNearest", r.LvlNearestName); C(b);
								  N(b, "lvlDistTicks", r.LvlDistTicks); C(b); N(b, "lvlDistAtr", r.LvlDistAtr); }
				else            { Null(b, "lvlSide"); C(b); Null(b, "lvlNearest"); C(b);
								  Null(b, "lvlDistTicks"); C(b); Null(b, "lvlDistAtr"); }
				C(b);
				if (r.HaveMtf) { I(b, "mtfBias", r.MtfBias); C(b); N(b, "mtfAlign", r.MtfAlignment); C(b);
								  I(b, "mtfAlignedCount", r.MtfAlignedCount); C(b); I(b, "mtfTfCount", r.MtfTfCount); C(b);
								  B(b, "mtfAllAgree", r.MtfAllAgree); }
				else            { Null(b, "mtfBias"); C(b); Null(b, "mtfAlign"); C(b);
								  Null(b, "mtfAlignedCount"); C(b); Null(b, "mtfTfCount"); C(b); Null(b, "mtfAllAgree"); }
				C(b);
				if (r.HaveProf) { I(b, "profLoc", r.ProfLocation); C(b); I(b, "profSignal", r.ProfSignal); C(b);
								  N(b, "profDistPocTicks", r.ProfDistPocTicks); C(b);
								  B(b, "profNearHvn", r.ProfNearHvn); C(b); B(b, "profNearLvn", r.ProfNearLvn); }
				else            { Null(b, "profLoc"); C(b); Null(b, "profSignal"); C(b);
								  Null(b, "profDistPocTicks"); C(b); Null(b, "profNearHvn"); C(b); Null(b, "profNearLvn"); }
				C(b);

				b.Append("\"fwd\":{");
				bool first = true;
				foreach (int h in _horizons)
				{
					if (!first) C(b); first = false;
					b.Append('"').Append("h").Append(h.ToString(CultureInfo.InvariantCulture)).Append("\":{");
					if (r.MfeTicks.ContainsKey(h))
					{
						N(b, "mfe", r.MfeTicks[h]); C(b);
						N(b, "mae", r.MaeTicks[h]); C(b);
						N(b, "cls", r.ClsTicks[h]);
					}
					else { Null(b, "mfe"); C(b); Null(b, "mae"); C(b); Null(b, "cls"); }
					b.Append('}');
				}
				b.Append("}}");

				// FileShare.ReadWrite so the file can be tailed/analysed while it is open.
				using (var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
				using (var sw = new StreamWriter(fs, Encoding.UTF8))
					sw.WriteLine(b.ToString());

				_written++;
			}
			catch (Exception ex) { NoteWriteFail(ex); }
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || ChartPanel == null) return;
			if (_sp == null) _sp = new SneakySkin.Painter();
			_sp.Begin(RenderTarget);
			try { if (ShowCard) RenderCard(); }
			catch (Exception ex)
			{
				if ((DateTime.UtcNow - _lastRenderExLog).TotalMilliseconds > 15000)
				{ _lastRenderExLog = DateTime.UtcNow; try { Print("[SneakyRecorder] card threw: " + ex.Message); } catch { } }
			}
			try { _sp.End(); } catch { }
		}
        public void ConductorRender(SharpDX.Direct2D1.RenderTarget rt)
        {
            if (!(ShowCard)) return;
            if (rt == null) return;
            try
            {
                if (_sp == null) _sp = new SneakySkin.Painter();
                _sp.Begin(rt);
                RenderCard();
            }
            catch { }
            finally { try { _sp.End(); } catch { } }
        }


		private void RenderCard()
		{
			const float cw = 250f, ch = 150f;
			SneakySkin.CardLayout.EnsureScrollHook(ChartControl);
			var slot = SneakySkin.CardLayout.PlaceScroll(this, ChartControl, _sp,
				ChartControl.ChartPanels[0].X, ChartControl.ChartPanels[0].Y, ChartControl.ChartPanels[0].W, ChartControl.ChartPanels[0].H, cw, ch);
			if (slot.Width <= 0f) return;   // not the currently-selected card in the rotation
			// A2 — writer failure is now VISIBLE on the card, not just a Print line in the Output tab.
			bool dead = _writerDead || _writeFailed;
			var col = dead ? SneakySkin.CDown : (_written > 0 ? SneakySkin.CUp : SneakySkin.CMute);
			var r = _sp.Card(slot.X, slot.Y, cw, ch, col);

			_sp.Dot(r.Left + 5f, r.Top + 8f, dead ? SneakySkin.CDown : col, _written > 0 && !dead);
			_sp.Text("RECORDER", r.Left + 16f, r.Top, r.Width - 66f, 16f, SneakySkin.CInk, 11f, true);
			string st = dead ? "NO REC" : (_held > 0 ? "REC" : "IDLE");
			var stCol = dead ? SneakySkin.CDown : (_held > 0 ? SneakySkin.CAccent : SneakySkin.CMute);
			_sp.Pill(st, r.Right, r.Top - 1f, stCol);

			_sp.Text(_written.ToString("#,0") + " rows written", r.Left, r.Top + 24f, r.Width, 20f, col, 14f, true);
			_sp.Text(_held + " held open (awaiting outcome)", r.Left, r.Top + 50f, r.Width, 14f, SneakySkin.CInk2, 10f);
			_sp.Text("mode: " + RecordMode + "   horizons: " + HorizonBars, r.Left, r.Top + 68f, r.Width, 14f, SneakySkin.CMute, 9f);

			if (dead)
				_sp.Text("WRITER DEAD — rows are being LOST (see Output tab)", r.Left, r.Top + 88f, r.Width, 14f, SneakySkin.CDown, 9f, true);
			else if (!RecordHistorical && State == State.Historical)
				_sp.Text("historical bars skipped (see setting)", r.Left, r.Top + 88f, r.Width, 14f, SneakySkin.CWarn, 9f, true);
			else if (_held > 0)
				_sp.Text("tail rows flush as horizons elapse", r.Left, r.Top + 88f, r.Width, 14f, SneakySkin.CMute, 9f);

			_sp.Text(_logPath == null ? "no file yet" : Path.GetFileName(_logPath),
				r.Left, r.Top + 110f, r.Width, 14f, SneakySkin.CMute, 7.5f);
		}

		#region Properties

		[NinjaScriptProperty]
		[Display(Name = "Record Mode", Description = "EveryBar gives the fullest dataset and the only unbiased base rate — you cannot measure whether high conviction beats low if you only ever record high. The Council modes are for smaller targeted studies.", GroupName = "1. Recording", Order = 0)]
		public SneakyRecordMode RecordMode { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Horizon Bars (comma list)", Description = "Forward outcome horizons in bars, e.g. 5,10,20,60. A row flushes once its LONGEST horizon elapses.", GroupName = "1. Recording", Order = 1)]
		public string HorizonBars { get; set; }

		[NinjaScriptProperty] [Range(1.0, 100000.0)]
		[Display(Name = "Barrier Width (ticks)", Description = "Triple-barrier labeling: how far price must move from entry, in either direction, to count as a barrier hit. A FIXED tick width, not ATR-scaled — simple and transparent, but doesn't adapt to volatility on its own; widen it yourself on a fast instrument or a coarser bar type.", GroupName = "1. Recording", Order = 5)]
		public double BarrierTicks { get; set; }

		[NinjaScriptProperty] [Range(0.5, 300.0)]
		[Display(Name = "Seam Staleness (sec)", Description = "How old a seam read (Council, Confluence, Eye, ...) may be before it's treated as absent. Default 90s, matching the convention every other Sneaky recorder uses. A too-tight window silently nulls out sensors that are actually fine — and for cnl specifically, a stale read means no vote vector on the row, which the Lab's sneaky_to_lab.py translator then drops entirely as \"no_votes\".", GroupName = "1. Recording", Order = 2)]
		public double StaleSec { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Record Historical Bars", Description = "OFF by default. Historical rows are NOT comparable to live ones — several sensors behave differently or do not publish at all on historical bars (Drift's CVB is realtime-only; pressure falls back to an OHLC proxy). Mixing them silently corrupts any comparison.", GroupName = "1. Recording", Order = 3)]
		public bool RecordHistorical { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Flush Incomplete On Stop", Description = "Write still-open rows at shutdown, flagged complete:false. Without this the tail of every session is lost; with it, partial data is labelled rather than silently missing.", GroupName = "1. Recording", Order = 4)]
		public bool FlushIncompleteOnStop { get; set; }

		[NinjaScriptProperty] [Display(Name = "Show Card", GroupName = "2. Display", Order = 0)] public bool ShowCard { get; set; }
		[NinjaScriptProperty] [Display(Name = "Card Corner", GroupName = "2. Display", Order = 1)] public SneakyCardCorner CardCorner { get; set; }
		[NinjaScriptProperty] [Display(Name = "Show Indicator Label", GroupName = "2. Display", Order = 2)] public bool ShowIndicatorLabel { get; set; }

		#endregion
	}

	public enum SneakyRecordMode
	{
		EveryBar,
		CouncilNonFlat,
		CouncilActionable,
		ConfluenceFired
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Sneaky.Sensors.SneakyRecorder_v1_0_0[] cacheSneakyRecorder_v1_0_0;
		public Sneaky.Sensors.SneakyRecorder_v1_0_0 SneakyRecorder_v1_0_0(SneakyRecordMode recordMode, string horizonBars, double barrierTicks, double staleSec, bool recordHistorical, bool flushIncompleteOnStop, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return SneakyRecorder_v1_0_0(Input, recordMode, horizonBars, barrierTicks, staleSec, recordHistorical, flushIncompleteOnStop, showCard, cardCorner, showIndicatorLabel);
		}

		public Sneaky.Sensors.SneakyRecorder_v1_0_0 SneakyRecorder_v1_0_0(ISeries<double> input, SneakyRecordMode recordMode, string horizonBars, double barrierTicks, double staleSec, bool recordHistorical, bool flushIncompleteOnStop, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			if (cacheSneakyRecorder_v1_0_0 != null)
				for (int idx = 0; idx < cacheSneakyRecorder_v1_0_0.Length; idx++)
					if (cacheSneakyRecorder_v1_0_0[idx] != null && cacheSneakyRecorder_v1_0_0[idx].RecordMode == recordMode && cacheSneakyRecorder_v1_0_0[idx].HorizonBars == horizonBars && cacheSneakyRecorder_v1_0_0[idx].BarrierTicks == barrierTicks && cacheSneakyRecorder_v1_0_0[idx].StaleSec == staleSec && cacheSneakyRecorder_v1_0_0[idx].RecordHistorical == recordHistorical && cacheSneakyRecorder_v1_0_0[idx].FlushIncompleteOnStop == flushIncompleteOnStop && cacheSneakyRecorder_v1_0_0[idx].ShowCard == showCard && cacheSneakyRecorder_v1_0_0[idx].CardCorner == cardCorner && cacheSneakyRecorder_v1_0_0[idx].ShowIndicatorLabel == showIndicatorLabel && cacheSneakyRecorder_v1_0_0[idx].EqualsInput(input))
						return cacheSneakyRecorder_v1_0_0[idx];
			return CacheIndicator<Sneaky.Sensors.SneakyRecorder_v1_0_0>(new Sneaky.Sensors.SneakyRecorder_v1_0_0(){ RecordMode = recordMode, HorizonBars = horizonBars, BarrierTicks = barrierTicks, StaleSec = staleSec, RecordHistorical = recordHistorical, FlushIncompleteOnStop = flushIncompleteOnStop, ShowCard = showCard, CardCorner = cardCorner, ShowIndicatorLabel = showIndicatorLabel }, input, ref cacheSneakyRecorder_v1_0_0);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Sneaky.Sensors.SneakyRecorder_v1_0_0 SneakyRecorder_v1_0_0(SneakyRecordMode recordMode, string horizonBars, double barrierTicks, double staleSec, bool recordHistorical, bool flushIncompleteOnStop, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyRecorder_v1_0_0(Input, recordMode, horizonBars, barrierTicks, staleSec, recordHistorical, flushIncompleteOnStop, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyRecorder_v1_0_0 SneakyRecorder_v1_0_0(ISeries<double> input , SneakyRecordMode recordMode, string horizonBars, double barrierTicks, double staleSec, bool recordHistorical, bool flushIncompleteOnStop, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyRecorder_v1_0_0(input, recordMode, horizonBars, barrierTicks, staleSec, recordHistorical, flushIncompleteOnStop, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Sneaky.Sensors.SneakyRecorder_v1_0_0 SneakyRecorder_v1_0_0(SneakyRecordMode recordMode, string horizonBars, double barrierTicks, double staleSec, bool recordHistorical, bool flushIncompleteOnStop, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyRecorder_v1_0_0(Input, recordMode, horizonBars, barrierTicks, staleSec, recordHistorical, flushIncompleteOnStop, showCard, cardCorner, showIndicatorLabel);
		}

		public Indicators.Sneaky.Sensors.SneakyRecorder_v1_0_0 SneakyRecorder_v1_0_0(ISeries<double> input , SneakyRecordMode recordMode, string horizonBars, double barrierTicks, double staleSec, bool recordHistorical, bool flushIncompleteOnStop, bool showCard, SneakyCardCorner cardCorner, bool showIndicatorLabel)
		{
			return indicator.SneakyRecorder_v1_0_0(input, recordMode, horizonBars, barrierTicks, staleSec, recordHistorical, flushIncompleteOnStop, showCard, cardCorner, showIndicatorLabel);
		}
	}
}

#endregion
