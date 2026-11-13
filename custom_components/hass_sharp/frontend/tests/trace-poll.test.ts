import { expect, it } from 'vitest'
import { nodeCatalog } from '../src/visual/contract'
import { createGraph } from '../src/visual/graph'
import { sharePoll } from '../src/visual/trace/poll'
import type { RunTraceResponse, TraceEvent } from '../src/types'
import { run } from './run-fixture'

const event = (seq: number, extra: Partial<TraceEvent> = {}): TraceEvent => ({ seq, node: `n${seq}`, frame: '', t: seq, ms: 1, status: 'ok', ...extra })
/** A reply as the backend sends it: new objects every time. */
const reply = (events: TraceEvent[], options: { revision?: string; live?: boolean } = {}): RunTraceResponse => JSON.parse(JSON.stringify({
  version: 1, live: options.live ?? true, run: run({ runId: 'run-1', status: 'Running' }),
  snapshot: { revision: options.revision ?? 'rev-1', visualId: 'heating', visualRevision: 2, graph: createGraph('Hall'), catalog: nodeCatalog },
  trace: { format: 1, runId: 'run-1', rootNodeId: 'n1', startedUtc: '2026-11-12T18:00:00Z', complete: false, truncated: false, dropped: 0, trigger: { platform: 'manual' }, events, summary: {} },
}))

it('keeps the previous reply whole when a poll brings nothing new', () => {
  const first = reply([event(1), event(2)])
  expect(sharePoll(first, reply([event(1), event(2)]))).toBe(first)
  expect(sharePoll(undefined, first)).toBe(first)
})

it('shares the snapshot while its revision is the same, and every event that did not change', () => {
  const first = reply([event(1), event(2, { status: 'running', ms: undefined })])
  const next = sharePoll(first, reply([event(1), event(2), event(3)]))
  expect(next).not.toBe(first)
  expect(next.snapshot).toBe(first.snapshot)
  expect(next.trace!.trigger).toBe(first.trace!.trigger)
  expect(next.trace!.events[0]).toBe(first.trace!.events[0])
  // The running event finished: its new content is used, as is the new event.
  expect(next.trace!.events[1]).not.toBe(first.trace!.events[1])
  expect(next.trace!.events[1].status).toBe('ok')
  expect(next.trace!.events.map(e => e.seq)).toEqual([1, 2, 3])
  // Another revision of the snapshot (the backend stored a different graph) is taken as sent.
  expect(sharePoll(next, reply([event(1), event(2), event(3)], { revision: 'rev-2' })).snapshot).not.toBe(first.snapshot)
  // The end of the run: the same events, now not live.
  const done = sharePoll(next, reply([event(1), event(2), event(3)], { live: false }))
  expect(done.live).toBe(false)
  expect(done.trace).toBe(next.trace)
})
