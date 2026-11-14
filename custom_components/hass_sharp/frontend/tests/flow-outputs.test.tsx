import { afterEach, expect, it } from 'vitest'
import { render } from 'solid-js/web'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
import fixtureCases from '../../../../tests/fixtures/visual-graph-validation.json'
import * as contract from '../src/visual/contract'
import { addConnected } from '../src/visual/flow/autoConnect'
import { buildEdgeView, buildNodeView, execLabel } from '../src/visual/flow/flowModel'
import type { VisualGraph } from '../src/visual/graph'
import { LibraryPreview } from '../src/visual/LibraryPreview'
import { traceModel } from '../src/visual/trace/traceModel'
import type { RunTrace, TraceEvent } from '../src/types'

// Flows: a node method's [Port(..., FlowControl = true)] parameters, as the compiler writes them into the catalog (a
// library node) or a signature (a Custom C# node). Each flow is a scope with its own row, the value it carries right
// under it, then Done and On error.
const richCatalog = rich.catalog as unknown as contract.VisualCatalogSnapshot
const richCase = (name: string) => structuredClone(rich.cases.find(c => c.name === name)!.graph) as unknown as VisualGraph
const validationCase = (name: string) => structuredClone((fixtureCases as { name: string; graph: unknown }[]).find(c => c.name === name)!.graph) as VisualGraph
const classified = () => richCase("flow: a flow's value is read on that flow")

let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); dispose = undefined; document.body.innerHTML = '' })

it('a library node with flows has a row per flow, its value under it, then Done and On error', () => {
  const graph = classified()
  const node = graph.nodes.find(n => n.id === 'classify')!
  expect(contract.executionPorts(node, richCatalog)).toEqual(['cold', 'ok', 'next', 'error'])
  expect(contract.scopePorts(node, richCatalog)).toEqual(['cold', 'ok'])
  const view = buildNodeView(node, { graph, catalog: richCatalog, diagnostics: [] })
  const right = view.rows.map(r => r.output).filter(p => p !== undefined)
  expect(right.map(p => [p.id, p.kind, p.label])).toEqual([['cold', 'exec', 'Cold'], ['data:cold', 'data', 'Cold'], ['ok', 'exec', 'Comfortable'], ['next', 'exec', 'Done']])
  expect(view.next, 'a node with flows has no header pin: Done is a row').toBeUndefined()
  expect(view.footer?.id).toBe('error')
  expect(right[0].name).toBe('Cold execution output, connected')
  expect(execLabel(node, 'ok', richCatalog)).toBe('Comfortable')
  const wire = graph.edges.find(e => e.kind !== 'data' && e.source === 'classify')!
  expect(buildEdgeView(wire, graph, richCatalog)!.label).toBe('Execution wire from Classify · Cold to Log')
})

it('a Custom C# node takes its flows from its signature', () => {
  const graph = validationCase("custom node: its signature's flows are its execution ports, each a scope")
  const node = graph.nodes.find(n => n.id === 'x')!
  expect(contract.executionPorts(node)).toEqual(['body', 'next', 'error'])
  expect(contract.scopePorts(node)).toEqual(['body'])
  expect(contract.validateGraph(graph)).toEqual([])
  const view = buildNodeView(node, { graph, catalog: contract.withGraphTypes(contract.nodeCatalog, graph), diagnostics: [] })
  expect(view.rows.map(r => r.output?.id).filter(Boolean)).toEqual(['body', 'data:body', 'next'])
  expect(execLabel(node, 'next', contract.nodeCatalog)).toBe('Done')
})

it("For each's item and index sit under Body, as a flow's value does", () => {
  const graph: VisualGraph = { version: 5, name: 'Loop', nodes: [
    { id: 't', kind: 'trigger', config: { type: 'manual' }, position: { x: 0, y: 0 } },
    { id: 'f', kind: 'foreach', config: { items: { kind: 'literal', type: 'array', value: [] }, itemType: 'number' }, position: { x: 0, y: 0 } },
  ], edges: [{ id: 'e', source: 't', target: 'f' }] }
  const view = buildNodeView(graph.nodes[1], { graph, catalog: contract.nodeCatalog, diagnostics: [] })
  expect(view.rows.map(r => r.output?.id).filter(Boolean)).toEqual(['body', 'data:item', 'data:index', 'next'])
})

it('auto-connect puts a step reading a flow value on that flow', () => {
  const graph = classified()
  const without: VisualGraph = { ...graph, nodes: graph.nodes.filter(n => n.id !== 'cold'), edges: graph.edges.filter(e => e.source !== 'cold' && e.target !== 'cold') }
  const result = addConnected(without, 'log', { x: 400, y: 0 }, { nodeId: 'classify', handle: 'data:cold', type: 'source' }, richCatalog, 'added')
  expect(result, 'the step is placed and wired').toBeDefined()
  expect(result!.graph.edges.some(e => e.kind === 'data' && e.source === 'classify' && e.sourcePort === 'cold' && e.target === 'added')).toBe(true)
  expect(result!.graph.edges.some(e => e.kind !== 'data' && e.source === 'classify' && e.port === 'cold' && e.target === 'added'), JSON.stringify(result!.graph.edges)).toBe(true)
  expect(contract.validateGraph(result!.graph, richCatalog).filter(d => d.severity === 3)).toEqual([])
})

it('the library preview names flows by their labels', () => {
  const el = document.body.appendChild(document.createElement('div'))
  dispose = render(() => <LibraryPreview catalog={richCatalog} sourceId="shared" pending={false} />, el)
  const card = [...el.querySelectorAll('.hs-library-preview')].find(c => c.textContent?.includes('Classify'))!
  expect(card.querySelector('.hs-library-execution')!.textContent).toContain('Cold · Comfortable · Done · On error')
})

it('a trace names a flow call by its flow, and a call travels its wire', () => {
  const graph = classified()
  const events: Partial<TraceEvent>[] = [{ node: 't', port: 'next' }, { node: 'classify', port: 'next' }, { node: 'cold', frame: 'classify#0:cold' }]
  const list = events.map((event, i) => ({ seq: i + 1, frame: '', t: i * 10, ms: 1, status: 'ok', ...event }) as TraceEvent)
  const trace: RunTrace = { format: 1, runId: 'run-1', rootNodeId: 't', startedUtc: '2026-11-12T18:00:00Z', complete: true, truncated: false, dropped: 0, trigger: {}, events: list,
    summary: { t: { count: 1, failed: 0, last: 'ok' }, classify: { count: 1, failed: 0, last: 'ok' }, cold: { count: 1, failed: 0, last: 'ok' } } }
  const model = traceModel(trace, graph, richCatalog)
  expect(model.frameLabel('classify#0:cold')).toBe('Cold · Call 1')
  const wire = graph.edges.find(e => e.kind !== 'data' && e.source === 'classify')!
  expect(model.edgesIn('').has(wire.id)).toBe(true)
})
