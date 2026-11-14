import { expect, it } from 'vitest'
import { nodeCatalog, type Graph, type GraphNode } from '../src/visual/contract'
import { chipValue, flowTrace, framesAgree, inChosenFrame, parseFrame, traceModel, valueText } from '../src/visual/trace/traceModel'
import { inputSource, secretName } from '../src/visual/trace/traceWords'
import secretNames from '../../../../tests/fixtures/trace-secret-names.json'
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
  // The Not is a value node: worked out (without an execution port) just before the Log that reads it.
  const g = graph(
    [node('t', 'trigger', { type: 'entity', entity: 'binary_sensor.hall' }), node('n', 'not', { value: lit('boolean', true) }), node('log', 'log', { level: 'Info' })],
    [{ id: 'e1', source: 't', target: 'log' }, { id: 'd1', kind: 'data', source: 'n', sourcePort: 'value', target: 'log', targetPort: 'value' }],
  )
  const model = traceModel(trace([
    { node: 't', port: 'next' },
    { node: 'n', inputs: { value: { type: 'boolean', v: true } }, outputs: { value: { type: 'boolean', v: false } } },
    { node: 'log', port: 'next', inputs: { value: { type: 'boolean', v: false, src: { node: 'n', port: 'value' } } } },
  ]), g, nodeCatalog)
  // The value wire out of the value node is travelled: the Log read it.
  expect([...model.edges].sort()).toEqual(['d1', 'e1'])
  const flow = flowTrace(model, g, '')
  expect(flow.nodes).toEqual({ t: { status: 'ok', runs: 1, ms: 1 }, n: { status: 'ok', runs: 1, ms: 1 }, log: { status: 'ok', runs: 1, ms: 1 } })
  expect(flow.value!('n', 'value', 'in')).toEqual({ type: 'boolean', v: 'Yes', trunc: undefined, src: undefined })
  expect(flow.value!('n', 'value', 'out')?.v).toBe('No')
  // A port the step did not record has no chip.
  expect(flow.value!('log', 'other', 'in')).toBeUndefined()
  expect(inputSource(g, g.nodes[2], 'value', model.value('log', 'value', 'in', '')!, nodeCatalog)).toBe('From Not · Result')
  expect(inputSource(g, g.nodes[1], 'value', model.value('n', 'value', 'in', '')!, nodeCatalog)).toBe('Value')
})

it('leaves a value node no step read unreached, and its wire not travelled', () => {
  const g = graph(
    [node('t', 'trigger', { type: 'manual' }), node('c', 'constant', { type: 'number', value: lit('number', 1) }), node('unused', 'constant', { type: 'number', value: lit('number', 2) }),
      node('i', 'if', { value: { kind: 'unset' } }), node('cmp', 'compare', { left: { kind: 'unset' }, right: lit('number', 1), operator: 'eq' }), node('yes', 'log'), node('no', 'log')],
    [{ id: 'e1', source: 't', target: 'i' }, { id: 'e2', source: 'i', target: 'yes', port: 'true' }, { id: 'e3', source: 'i', target: 'no', port: 'false' },
      { id: 'd1', kind: 'data', source: 'c', sourcePort: 'value', target: 'cmp', targetPort: 'left' }, { id: 'd2', kind: 'data', source: 'cmp', sourcePort: 'value', target: 'i', targetPort: 'value' },
      { id: 'd3', kind: 'data', source: 'unused', sourcePort: 'value', target: 'no', targetPort: 'value' }],
  )
  const model = traceModel(trace([
    { node: 't', port: 'next' },
    { node: 'c', outputs: { value: { type: 'number', v: 1 } } },
    { node: 'cmp', inputs: { left: { type: 'number', v: 1, src: { node: 'c', port: 'value' } }, right: { type: 'number', v: 1 } }, outputs: { value: { type: 'boolean', v: true } } },
    { node: 'i', port: 'true', inputs: { value: { type: 'boolean', v: true, src: { node: 'cmp', port: 'value' } } } },
    { node: 'yes', port: 'next' },
  ]), g, nodeCatalog)
  expect([...model.edges].sort()).toEqual(['d1', 'd2', 'e1', 'e2'])
  const flow = flowTrace(model, g, '')
  expect(Object.keys(flow.nodes).sort()).toEqual(['c', 'cmp', 'i', 't', 'yes'])
  expect(flow.value!('cmp', 'value', 'out')?.v).toBe('Yes')
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
  // The whole run: every iteration counted, the last one's timing.
  expect(flowTrace(model, g, '').nodes.say).toEqual({ status: 'ok', runs: 3, ms: 3 })
  // One iteration: the loop's steps show that iteration only; the loop itself and the step after it are unchanged.
  const flow = flowTrace(model, g, 'each#0:body')
  expect(flow.nodes.say).toEqual({ status: 'ok', runs: 1, ms: 1 })
  expect(flow.nodes.end).toEqual({ status: 'ok', runs: 1, ms: 1 })
  expect(flow.value!('each', 'items', 'in')?.v).toBe('List · 3 items')
  expect(flow.value!('each', 'item', 'out')?.v).toBe('Hall')
  expect(framesAgree('each#1:body', 'each#2:body')).toBe(false)
  expect(framesAgree('each#1:body/inner#0:body', 'each#1:body')).toBe(true)
  expect(framesAgree('each#1:condition', 'each#1:body')).toBe(true)
  expect(framesAgree('', 'each#1:body')).toBe(true)
  // The chosen iteration applies to the loop's steps and the loop itself, not to steps outside it.
  expect(inChosenFrame('say', ['each#0:body'], 'each#1:body')).toBe(true)
  expect(inChosenFrame('each', [''], 'each#1:body')).toBe(true)
  expect(inChosenFrame('end', [''], 'each#1:body')).toBe(false)
  expect(inChosenFrame('say', ['each#0:body'], '')).toBe(false)
  expect(parseFrame('p:branch:a/each#2:body')).toEqual([
    { kind: 'branch', raw: 'p:branch:a', node: 'p', branch: 'a' }, { kind: 'loop', raw: 'each#2:body', node: 'each', iteration: 2, part: 'body' },
  ])
})

it('names parallel branches; the whole run shows every branch, a chosen branch only its own steps and wires', () => {
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
  expect(model.value('b', 'value', 'in', '')?.v).toBe('off')
  // One branch chosen: the other branch's step did not run in it, and its wire is not travelled in that picture.
  expect(model.value('b', 'value', 'in', 'p:branch:lights')).toBeUndefined()
  const lights = flowTrace(model, g, 'p:branch:lights')
  expect(Object.keys(lights.nodes).sort()).toEqual(['a', 'p', 't'])
  expect([...lights.edges].sort()).toEqual(['ba', 'e1'])
  expect(Object.keys(flowTrace(model, g, '').nodes).sort()).toEqual(['a', 'b', 'p', 't'])
  expect(inChosenFrame('b', ['p:branch:3f2a9c1e-77aa-4b1c-9e0f-0123456789ab'], 'p:branch:lights')).toBe(true)
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
  // A whole value is never cut for depth (only what is inside it): a cut null could not be read.
  expect(valueText({ type: 'object', v: null, trunc: true })).toBe('Could not be recorded')
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

it('counts a while loop by its bodies, names its last condition check, and counts until and nested loops', () => {
  const g = graph([node('t', 'trigger'), node('w', 'while'), node('check', 'compare'), node('step', 'log')],
    [{ id: 'e1', source: 't', target: 'w' }, { id: 'cond', source: 'w', target: 'check', port: 'condition' }, { id: 'body', source: 'w', target: 'step', port: 'body' }])
  // while: the condition before each of 4 bodies, and once more to end.
  const whiles = traceModel(trace([
    { node: 't', port: 'next' }, { node: 'w', port: 'next' },
    ...[0, 1, 2, 3].flatMap(i => [{ node: 'check', frame: `w#${i}:condition` }, { node: 'step', frame: `w#${i}:body` }]),
    { node: 'check', frame: 'w#4:condition' },
  ]), g, nodeCatalog)
  expect(whiles.frameLabel('w#0:body')).toBe('Iteration 1 of 4')
  expect(whiles.frameLabel('w#3:condition')).toBe('Iteration 4 of 4 · Condition')
  expect(whiles.frameLabel('w#4:condition')).toBe('Final check')
  // While the run goes on, or with steps missing, a body may still follow that check (or was dropped): no "Final check".
  const whileEvents = whiles.trace.events
  for (const extra of [{ complete: false }, { truncated: true, dropped: 3 }]) {
    const open = traceModel({ ...whiles.trace, events: whileEvents, ...extra }, g, nodeCatalog)
    expect(open.frameLabel('w#4:condition')).toBe('Iteration 5 of 5 · Condition')
  }
  // The final check's picture: the condition steps ran, the body did not.
  const final = flowTrace(whiles, g, 'w#4:condition')
  expect(final.nodes.check).toBeDefined()
  expect(final.nodes.step).toBeUndefined()
  expect(final.edges.has('body')).toBe(false)
  // until: the body first, then its condition, as many of each.
  const untils = traceModel(trace([
    { node: 't', port: 'next' }, { node: 'w', port: 'next' },
    ...[0, 1, 2].flatMap(i => [{ node: 'step', frame: `w#${i}:body` }, { node: 'check', frame: `w#${i}:condition` }]),
  ]), g, nodeCatalog)
  expect(untils.frameLabel('w#2:condition')).toBe('Iteration 3 of 3 · Condition')
  expect(untils.frameLabel('w#0:body')).toBe('Iteration 1 of 3')
  // Nested: the inner loop's total is per outer iteration.
  const nested = traceModel(trace([
    { node: 't', port: 'next' },
    { node: 'step', frame: 'outer#0:body/inner#0:body' }, { node: 'step', frame: 'outer#0:body/inner#1:body' },
    { node: 'step', frame: 'outer#1:body/inner#0:body' }, { node: 'step', frame: 'outer#1:body/inner#1:body' }, { node: 'step', frame: 'outer#1:body/inner#2:body' },
  ]), g, nodeCatalog)
  expect(nested.frameLabel('outer#0:body/inner#1:body')).toBe('Iteration 1 of 2 · Iteration 2 of 2')
  expect(nested.frameLabel('outer#1:body/inner#0:body')).toBe('Iteration 2 of 2 · Iteration 1 of 3')
  // Choosing an outer iteration keeps all of its inner ones; choosing an inner one keeps only that.
  expect(flowTrace(nested, g, 'outer#1:body').nodes.step.runs).toBe(3)
  expect(flowTrace(nested, g, 'outer#1:body/inner#2:body').nodes.step.runs).toBe(1)
  expect(nested.eventIn('step', 'outer#0:body')?.seq).toBe(3)
})

it('reads a port taken with nothing connected, and steps that were cancelled, stopped or are still running', () => {
  const g = graph([node('t', 'trigger'), node('if', 'if'), node('a', 'log'), node('b', 'log'), node('c', 'log')],
    [{ id: 'e1', source: 't', target: 'if' }, { id: 'yes', source: 'if', target: 'a', port: 'true' }])
  const model = traceModel(trace([
    { node: 't', port: 'next' },
    // The false side has no wire: nothing is travelled from it, and the step is still done.
    { node: 'if', port: 'false' },
    { node: 'a', status: 'cancelled', port: undefined, ms: 4 },
    { node: 'b', status: 'stopped', port: undefined },
    { node: 'c', status: 'running', port: undefined, ms: undefined },
  ]), g, nodeCatalog)
  expect([...model.edges]).toEqual(['e1'])
  const flow = flowTrace(model, g, '')
  expect(flow.nodes.if.status).toBe('ok')
  expect(flow.nodes.a).toEqual({ status: 'cancelled', runs: 1, ms: 4 })
  expect(flow.nodes.b.status).toBe('stopped')
  expect(flow.nodes.c).toEqual({ status: 'running', runs: 1 })
})

it('hides the recorder\'s secret names exactly as the C# rule does', () => {
  for (const row of secretNames.names) {
    expect(secretName(row.name), row.name).toBe(row.secret)
    // A name in a recorded tree may carry the position the recorder gave alike keys.
    if (row.name) expect(secretName(`${row.name}#3`), `${row.name}#3`).toBe(row.secret)
  }
})
