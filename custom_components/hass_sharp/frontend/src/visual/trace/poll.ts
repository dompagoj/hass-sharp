import type { RunTraceResponse, TraceEvent } from '../../types'

const same = (a: unknown, b: unknown) => a === b || JSON.stringify(a) === JSON.stringify(b)

/**
 * A live run's next reply, sharing every part that did not change with the one before: the snapshot (the graph and
 * catalog the canvas builds its cards from) while its revision is the same, each event whose seq and content are the
 * same, the trigger data and the run record. What the page has opened (tree levels, the chosen run, the cards and the
 * viewport) then survives each poll. Returns `previous` itself when nothing changed.
 */
export function sharePoll<T extends RunTraceResponse | undefined>(previous: RunTraceResponse | undefined, next: T): T {
  // Nothing to share with (the first reply, or another run's query starting with no data yet), or another run.
  if (!previous || !next || previous.run.runId !== next.run.runId) return next
  const snapshot =
    previous.snapshot && next.snapshot && previous.snapshot.revision === next.snapshot.revision && previous.snapshot.visualId === next.snapshot.visualId
      ? previous.snapshot
      : next.snapshot
  let trace = next.trace
  if (previous.trace && next.trace) {
    const before = new Map<number, TraceEvent>(previous.trace.events.map(event => [event.seq, event]))
    const events = next.trace.events.map(event => {
      const old = before.get(event.seq)
      return old && same(old, event) ? old : event
    })
    const { events: _old, trigger: oldTrigger, summary: oldSummary, ...oldRest } = previous.trace
    const { events: _new, trigger, summary, ...rest } = next.trace
    const unchanged = events.length === previous.trace.events.length && events.every((event, i) => event === previous.trace!.events[i])
    trace =
      unchanged && same(oldRest, rest) && same(oldSummary, summary) && same(oldTrigger, trigger)
        ? previous.trace
        : { ...next.trace, events, trigger: same(oldTrigger, trigger) ? oldTrigger : trigger }
  }
  const run = same(previous.run, next.run) ? previous.run : next.run
  if (snapshot === previous.snapshot && trace === previous.trace && run === previous.run && previous.live === next.live && previous.version === next.version) return previous as T
  return { ...next, snapshot, trace, run }
}
