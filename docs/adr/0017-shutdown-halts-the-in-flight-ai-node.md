# A shutdown halts the in-flight AI node rather than letting it finish

When the host is asked to stop, ILD parks every run it is driving at its current
AI node — the same `WaitingHuman` + `IsHalted` park the Halt button produces,
stamped `HaltReason.Shutdown` — waits for the driving loops to unwind, and
resumes exactly those runs against the same agent session on the next start. The
alternative shape, letting the in-flight node run to completion and stopping at
the node boundary, was rejected because an AI node has no bound worth waiting
for: to the engine it is one step (ADR-0007), but to the adapter it is a CLI
that can think for many minutes. No supervisor grace period we could ask an
operator to configure — 30s by Kubernetes default — would reliably cover it, so
"let it finish" degrades to the hard kill it was meant to avoid, just later.
Halting at a known point costs only the current step's partial output and is
bounded by a timeout we choose.

## Considered options

**Let the in-flight node finish, then stop at the node boundary.** Rejected
above: unbounded in the only dimension that matters. It also fails in the worst
direction — the runs it abandons mid-step are the long ones, which are the runs
with the most work to lose.

**Auto-resume every halted run on startup.** The tempting simplification, since
a shutdown park and a human Halt produce byte-identical rows today. It was
rejected because it quietly deletes the Halt button: a person halts a run to
look at it, and any restart — a deploy, a crash, an image bump — would hand it
back to the agent before they returned. That is why the halt carries a _reason_
rather than the resume paths guessing from shape. `HaltReason` is nullable and
null means human, so every row written before this existed reads as a human halt
and is left alone, which is the safe direction for the ambiguous case.

**Snapshot the agent's state ourselves instead of parking.** Not available to
us: session capture belongs to the adapter, and per ADR-0007 AI execution is
delegated to the CLI wholesale. Resuming against `CurrentAiSessionId` uses the
adapter's own session, which is the only durable representation of that context
ILD has.

## Consequences

- The drain **waits** for the driving loops, and that wait is the feature rather
  than a courtesy. The park is written before the agent process is killed, but
  the interrupted-node bookkeeping happens as the loop unwinds; a process that
  exited first would leave the half-written state this removes. Hence the budget
  nesting — drain < host shutdown timeout < supervisor grace period — documented
  in [configuration.md](../configuration.md#graceful-shutdown).
- A shutdown park makes **no work-item transition**. Leaving the item `Running`
  on the server is what lets the startup reconciler recognise the run as still
  ours; moving it to `HumanFeedback` would both ask a person for something and
  make the item look reclaimable.
- Runs on non-AI nodes are cancelled and left `Running` for ordinary crash
  recovery. A Cmd or Condition node is cheap to redo and not worth a park a
  human might have to clear.
- Resume is attempted from **three** places — the recovery manager, the remote
  startup reconciler and the stuck-run watchdog. The watchdog is not redundancy
  for its own sake: startup reconciliation is skipped wholesale when the
  work-item server is unreachable, which is what happens when one deploy rolls
  both containers, and it swallows its own failures. Without a backstop the
  tidiest possible shutdown could park a run forever — strictly worse than the
  hard kill.
- `RecoveryPolicy` still decides. `Cancel` and `NeedsReview` are an operator's
  explicit statement about what a restart does to their runs, and "the restart
  was a tidy one" is not grounds to overrule them.
- The frontend gates its steer dialog on `IsHalted` alone, so a shutdown-parked
  run briefly offers a steer box between startup and auto-resume. Harmless —
  steering it just resumes with the note — but exposing `HaltReason` in the run
  DTO and labelling it is the honest follow-up. **Done** with the throttle park
  below: both run endpoints now project `haltReason`, and the steer window reads
  it rather than guessing from a node's prose.

## Addendum: a throttled AI node parks here too

An AI provider interrupting the node — a usage or session limit, HTTP 429, an
overloaded provider, a dropped stream — now parks the run through this same halt
rather than routing it onto the `on_failure` edge. It is the same row shape
(`WaitingHuman` + `IsHalted`, resumed by `ResumeFromHaltAsync` against
`CurrentAiSessionId`), stamped `HaltReason.Throttled` and carrying the
`AI Provider Throttled` feedback reason, and it reuses the engine's existing
outcome switch rather than building a second park beside it. It is deliberately
**not** routed through `HaltRunAsync`: that cancels the run's CTS to kill a live
agent, and a throttled adapter has already exited. Genuine failures — auth, a
missing model, a validation error, a crashed process, an exhausted context
window — still take `on_failure`, unchanged. Context exhaustion is a genuine
failure on purpose: resuming the same session walks back into the same wall, so
parking would only relocate the dead end.

The steer window labels the park and offers the provider's own words, keyed off
the run's `haltReason` rather than off its node rows: a human's Halt also ends
its node `Interrupted` **with** an error ("Run cancelled"), so the rows cannot
tell the two parks apart, while the field that records who parked the run can.

`Throttled` behaves like `Human`, not like `Shutdown`. Blanket auto-resume was
reconsidered here — bounded retries on a backoff schedule, swept by a background
service — and declined again, for the reason above and one more: the reset time
is stated in the provider's own words ("resets 9:40am (UTC)"), and a schedule
built by parsing that would be ILD guessing at a contract no provider offers. So
the park shows the adapter's raw output and a person decides when to click
Resume. That is the accepted cost: a throttled run needs one human Resume per
interruption. Nothing here needs a migration — `HaltReason` is an existing int
column.

## Amendment: that retry exists now, off by default, and does read the stated reset

The bounded retry declined above is available as the `throttle.autoResume` app
setting. The reasoning stands where it mattered most: the setting is **off** by
default, so the shipped behaviour is unchanged for anyone who does not ask for
this. `ThrottledRunResumeSweeper` resumes an eligible park through the ordinary
`ResumeFromHaltAsync`, and a provider that is still throttled interrupts the
node again, re-parking the run for the next attempt. Delays double from ten
minutes and stop after five automatic resumes,
which spans the shape of a session window without spinning against a limit that
is not going to lift. What remains an operator's judgement is whether a wasted
round-trip costs them less than a run sitting idle overnight; the default
answers that the same way this ADR did.

**The reset time is read after all**, which is the part of the decision above
that did not survive contact with the feature: when the provider has already
said "resets 7:10pm (UTC)", firing before then spends one of five attempts on a
refusal we were told to expect. `AiFailureClassifier.TryParseResetAt` reads it
at park time onto `LoopRun.ThrottleResetAt` (one migration), and the sweeper
treats it as a **floor** under the backoff, never as a trigger — nothing fires
because the stated time arrived, only later than it. So the objection stands in
the form it was actually about: ILD still does not build a timetable out of
provider prose, and a notice it cannot read leaves the retry ladder to schedule
alone, exactly as before.

What keeps that from becoming the guesswork this ADR declined is refusing the
ambiguous readings. Only a time the provider stamped UTC is taken, plus an
unambiguous "try again in 20 minutes"; a bare "resets 3pm" is left alone,
because the zone it means is the reader's and guessing west idles a run for
hours it never had to wait. Anything further out than twelve hours is dropped
too — at that distance a misread twelve-hour clock is likelier than a real
window. Every unread case costs one early attempt, which the ladder already
absorbs; the read cases are the ones where waiting was free.

Both the delay and the bound are read off automatic resumes spent **since a
human last touched the run**, not since the current interruption — the same
window the traversal budget is measured over, deliberately, because they are one
question asked twice. A run that auto-resumes twice, works for an hour and is
then interrupted afresh waits longer and has fewer tries left than a run nobody
has helped yet. That is the intended reading: an unattended run being stopped
for the fifth time is a run whose provider is rationing it, and the answer to
that is a person rather than a sixth retry. One human Resume restores both
budgets.

Two guards keep it from eroding decisions taken elsewhere. Only
`HaltReason.Throttled` is eligible, so a human's Halt, a `Shutdown` park and a
`MaxAiTraversals` park are untouched. And an automatic resume is explicitly not
a human touch: it is the one caller of `ResumeFromHaltAsync` that does **not**
refill the AI traversal budget (ADR-0018), because a run left to itself must not
buy a fresh budget every time its provider hiccups. The count of consecutive
automatic resumes lives on the run (`LoopRun.ThrottleAutoResumeCount`, one
migration) and is refilled by exactly what refills that budget.
