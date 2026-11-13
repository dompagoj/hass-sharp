import { expect, it } from 'vitest'
import { nodeCatalog, type Graph, type GraphNode } from '../src/visual/contract'
import { chipValue, compatibleFrames, flowTrace, inputSource, nextFrame, parseFrame, traceModel, valueText } from '../src/visual/trace/traceModel'
import type { RunTrace, TraceEvent } from '../src/types'

const lit = (type: string, value: unknown) => ({ kind: 'literal', type, value })
const node = (id: string, kind: string, config: Record<string, unknown> = {}, label?: string): GraphNode => ({ id, kind, config, ...(label ? { label } : {}), position: { x: 0, y: 0 } })
const graph = (nodes: GraphNode[], edges: Graph['edges']): Graph => ({ version: 5, name: 'Sample', nodes, edges })
const trace = (events: Partial<TraceEvent>[], extra: Partial<RunTrace> = {}): RunTrace => {
  const list = events.map((event, i) => ({ seq: i + 1, frame: '', t: i * 10, ms: 1, status: 'ok', ...event }) as TraceEvent)
  const summary: RunTrace['summary'] = {}
  for (const event of list) {
    const row = (summary[event.node] ??= { count: 0, failed: 0, last: 'ok' })
    row.count++; row.last = event.status
    if (event.status === 'failed') row.failed++
  }
  return { format: 1, runId: 'run-1', rootNodeId: 't', startedUtc: '2026-11-12T18:00:00Z', complete: true, truncated: false, dropped: 0, trigger: {}, events: list, summary, ...extra }
}

it('follows a linear run: every step reached once, every wire travelled, values by port', () => {
  const g = graph(
    [node('t', 'trigger', { type: 'entity', entity: 'binary_sensor.hall' }), node('n', 'not', { value: lit('boolean', true) }), node('log', 'log', { level: 'Info' })],
    [{ id: 'e1', source: 't', target: 'n' }, { id: 'e2', source: 'n', target: 'log' }, { id: 'd1', kind: 'data', source: 'n', sourcePort: 'value', target: 'log', targetPort: 'value' }],
  )
  const model = traceModel(trace([
    { node: 't', port: 'next' },
    { node: 'n', port: 'next', inputs: { value: { type: 'boolean', v: true } }, outputs: { value: { type: 'boolean', v: false } } },
    { node: 'log', port: 'next', inputs: { value: { type: 'boolean', v: false, src: { node: 'n', port: 'value' } } } },
  ]), g, nodeCatalog)
  expect([...model.edges].sort()).toEqual(['d1', 'e1', 'e2'])
  const flow = flowTrace(model, g, '')
  expect(flow.nodes).toEqual({ t: { status: 'ok', runs: 1, ms: 1 }, n: { status: 'ok', runs: 1, ms: 1 }, log: { status: 'ok', runs: 1, ms: 1 } })
  expect(flow.value!('n', 'value', 'in')).toEqual({ type: 'boolean', v: 'Yes', trunc: undefined, src: undefined })
  expect(flow.value!('n', 'value', 'out')?.v).toBe('No')
  // A port the step did not record has no chip.
  expect(flow.value!('log', 'other', 'in')).toBeUndefined()
  expect(inputSource(g, g.nodes[2], 'value', model.value('log', 'value', 'in', '')!, nodeCatalog)).toBe('From Not · Result')
  expect(inputSource(g, g.nodes[1], 'value', model.value('n', 'value', 'in', '')!, nodeCatalog)).toBe('Value')
})

it('marks only the branch a condition took, and leaves the other side unreached', () => {
  const g = graph(
    [node('t', 'trigger'), node('if', 'if', { value: { kind: 'variable', name: 'home' } }), node('yes', 'log'), node('no', 'log')],
    [{ id: 'e1', source: 't', target: 'if' }, { id: 'yes', source: 'if', target: 'yes', port: 'true' }, { id: 'no', source: 'if', target: 'no', port: 'false' }],
  )
  const model = traceModel(trace([{ node: 't', port: 'next' }, { node: 'if', port: 'false', inputs: { value: { type: 'boolean', v: false } } }, { node: 'no' }]), g, nodeCatalog)
  expect([...model.edges].sort()).toEqual(['e1', 'no'])
  expect(Object.keys(flowTrace(model, g, '').nodes).sort()).toEqual(['if', 'no', 't'])
  expect(model.reached('yes')).toBe(false)
  expect(inputSource(g, g.nodes[1], 'value', model.value('if', 'value', 'in', '')!, nodeCatalog)).toBe('Variable home')
})

it('follows a recovered error through the error output, and a condition\'s "next" wire as its true side', () => {
  const g = graph(
    [node('t', 'trigger'), node('c', 'condition', { state: 'on' }), node('svc', 'service', { service: 'light.turn_on', fields: {} }), node('fix', 'log'), node('after', 'log')],
    [{ id: 'e1', source: 't', target: 'c' }, { id: 'e2', source: 'c', target: 'svc' }, { id: 'ok', source: 'svc', target: 'after' }, { id: 'err', source: 'svc', target: 'fix', port: 'error' }],
  )
  const model = traceModel(trace([
    { node: 't', port: 'next' }, { node: 'c', port: 'true' },
    { node: 'svc', status: 'error', port: 'error', error: { type: 'InvalidOperationException', message: 'Light is unavailable' } }, { node: 'fix', port: 'next' },
  ]), g, nodeCatalog)
  expect([...model.edges].sort()).toEqual(['e1', 'e2', 'err'])
  expect(flowTrace(model, g, '').nodes.svc.status).toBe('error')
})

it('reads loop iterations as frames: 1-based words, values of the chosen iteration, the loop item from its readers', () => {
  const g = graph(
    [node('t', 'trigger'), node('each', 'foreach', { itemType: 'string' }), node('say', 'log'), node('end', 'log')],
    [{ id: 'e1', source: 't', target: 'each' }, { id: 'body', source: 'each', target: 'say', port: 'body' }, { id: 'done', source: 'each', target: 'end' },
      { id: 'item', kind: 'data', source: 'each', sourcePort: 'item', target: 'say', targetPort: 'value' }],
  )
  const rooms = ['Hall', 'Kitchen', 'Bath']
  const model = traceModel(trace([
    { node: 't', port: 'next' },
    { node: 'each', port: 'next', t: 10, ms: 30, inputs: { items: { type: 'array', v: rooms } } },
    ...rooms.map((room, i) => ({ node: 'say', frame: `each#${i}:body`, port: 'next', ms: i + 1, inputs: { value: { type: 'string', v: room, src: { node: 'each', port: 'item' } } } })),
    { node: 'end', port: 'next' },
  ]), g, nodeCatalog)
  expect(model.edges.has('body')).toBe(true)
  expect(model.edges.has('done')).toBe(true)
  expect(model.edges.has('item')).toBe(true)
  expect(model.frameLabel('each#1:body')).toBe('Iteration 2 of 3')
  expect(model.frameLabel('each#0:condition')).toBe('Iteration 1 of 3 · Condition')
  expect(model.frameLabel('')).toBe('')
  // Top level: the latest iteration; an iteration: its own values; steps outside the loop keep theirs.
  expect(model.value('say', 'value', 'in', '')?.v).toBe('Bath')
  expect(model.value('say', 'value', 'in', 'each#1:body')?.v).toBe('Kitchen')
  expect(model.value('each', 'item', 'out', 'each#0:body')?.v).toBe('Hall')
  expect(model.value('each', 'items', 'in', 'each#0:body')?.v).toEqual(rooms)
  const flow = flowTrace(model, g, 'each#0:body')
  expect(flow.nodes.say).toEqual({ status: 'ok', runs: 3, ms: 1 })
  expect(flow.value!('each', 'items', 'in')?.v).toBe('List · 3 items')
  // Picking a step outside the loop keeps the iteration in view; picking another iteration moves to it.
  expect(nextFrame('each#1:body', '')).toBe('each#1:body')
  expect(nextFrame('each#1:body', 'each#2:body')).toBe('each#2:body')
  expect(compatibleFrames('each#1:body', 'each#2:body')).toBe(false)
  expect(compatibleFrames('each#1:body/inner#0:body', 'each#1:body')).toBe(true)
  expect(parseFrame('p:branch:a/each#2:body')).toEqual([
    { kind: 'branch', raw: 'p:branch:a', node: 'p', branch: 'a' }, { kind: 'loop', raw: 'each#2:body', node: 'each', iteration: 2, part: 'body' },
  ])
})

it('names parallel branches and shows every branch side by side', () => {
  const g = graph(
    [node('t', 'trigger'), node('p', 'parallel', { branches: ['lights', '3f2a9c1e-77aa-4b1c-9e0f-0123456789ab'] }), node('a', 'log'), node('b', 'log')],
    [{ id: 'e1', source: 't', target: 'p' }, { id: 'ba', source: 'p', target: 'a', port: 'branch:lights' }, { id: 'bb', source: 'p', target: 'b', port: 'branch:3f2a9c1e-77aa-4b1c-9e0f-0123456789ab' }],
  )
  const model = traceModel(trace([
    { node: 't', port: 'next' }, { node: 'p', port: 'next' },
    { node: 'a', frame: 'p:branch:lights', inputs: { value: { type: 'string', v: 'on' } } },
    { node: 'b', frame: 'p:branch:3f2a9c1e-77aa-4b1c-9e0f-0123456789ab', inputs: { value: { type: 'string', v: 'off' } } },
  ]), g, nodeCatalog)
  expect([...model.edges].sort()).toEqual(['ba', 'bb', 'e1'])
  expect(model.frameLabel('p:branch:lights')).toBe('Branch Lights')
  expect(model.frameLabel('p:branch:3f2a9c1e-77aa-4b1c-9e0f-0123456789ab')).toBe('Branch 2')
  // Looking at one branch keeps the other's values on its cards.
  expect(model.value('b', 'value', 'in', 'p:branch:lights')?.v).toBe('off')
})

it('keeps the summary\'s counts and statuses for runs whose events were dropped', () => {
  const g = graph([node('t', 'trigger'), node('loop', 'repeat'), node('step', 'log')], [{ id: 'e1', source: 't', target: 'loop' }, { id: 'body', source: 'loop', target: 'step', port: 'body' }])
  const recorded = trace([
    { node: 't', port: 'next' }, { node: 'loop', status: 'running', inputs: { count: { type: 'number', v: 12 } } },
    { node: 'step', frame: 'loop#0:body', port: 'next', trunc: true }, { node: 'step', frame: 'loop#1:body', port: 'next' },
  ], { truncated: true, dropped: 30, complete: false })
  recorded.summary.step = { count: 12, failed: 1, last: 'failed' }
  const model = traceModel(recorded, g, nodeCatalog)
  expect(model.runs('step')).toBe(12)
  // The repeat count gives the total even though later iterations were not kept.
  expect(model.frameLabel('loop#1:body')).toBe('Iteration 2 of 12')
  expect(flowTrace(model, g, '').nodes.loop).toEqual({ status: 'running', runs: 1, ms: 1 })
  expect(model.event(3)?.trunc).toBe(true)
  // A step the summary counts with no kept event is still reached.
  recorded.summary.later = { count: 2, failed: 0, last: 'ok' }
  const withLater = traceModel(recorded, graph([...g.nodes, node('later', 'log')], g.edges), nodeCatalog)
  expect(withLater.reached('later')).toBe(true)
  expect(flowTrace(withLater, graph([...g.nodes, node('later', 'log')], g.edges), '').nodes.later).toEqual({ status: 'ok', runs: 2 })
})

it('says what a value is in plain words, and never shows a placeholder as data', () => {
  expect(valueText({ type: 'string', v: '***', redacted: true })).toBe('Hidden')
  expect(valueText({ type: 'object', v: null, trunc: true })).toBe('Not recorded (too deep)')
  expect(valueText({ type: 'object', v: null })).toBe('No value')
  expect(valueText({ type: 'object', trunc: true })).toBe('Too large to record')
  expect(valueText({ type: 'global::System.Linq.Enumerable', v: 'global::System.Linq.Enumerable.SelectIterator', trunc: true })).toBe('Could not be recorded')
  expect(valueText({ type: 'object', v: { a: 1, b: 2, c: 3, d: 4 } })).toBe('Object · 4 fields')
  expect(valueText({ type: 'array', v: Array.from({ length: 12 }) })).toBe('List · 12 items')
  expect(valueText({ type: 'array', v: [1] })).toBe('List · 1 item')
  expect(valueText({ type: 'string', v: '' })).toBe('Empty text')
  expect(valueText({ type: 'number', v: 21.5 })).toBe('21.5')
  expect(chipValue({ type: 'string', v: 'abc', trunc: true })).toMatchObject({ v: 'abc', trunc: true })
  expect(chipValue({ type: 'string', v: '***', redacted: true }).trunc).toBeUndefined()
  expect(chipValue({ type: 'object', v: null, trunc: true }).trunc).toBeUndefined()
})
