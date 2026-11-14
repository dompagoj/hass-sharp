import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { NodeConfig } from '../src/visual/NodeConfig'
import { addNode, connectNodes, createGraph, updateNode } from '../src/visual/graph'
import { nodeCatalog, type VisualCatalogSnapshot } from '../src/visual/contract'
let dispose: (() => void) | undefined
afterEach(() => {
  dispose?.()
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})
const host = () => {
  const el = document.createElement('div')
  document.body.append(el)
  return el
}
const fixture = () => {
  let graph = addNode(createGraph('Typed'), 'constant', 'temperature')
  graph = updateNode(graph, 'temperature', { config: { type: 'number', value: { kind: 'literal', type: 'number', value: 20 } } })
  graph = addNode(graph, 'if', 'branch')
  graph = connectNodes(graph, 'service-1', 'temperature')
  graph = connectNodes(graph, 'temperature', 'branch')
  return graph
}
it('shows typed inputs with friendly type chips and outputs, without an appearance section', () => {
  const el = host()
  const graph = fixture()
  dispose = render(() => (
    <>
      <NodeConfig graph={graph} node={graph.nodes.find(n => n.id === 'branch')!} catalog={nodeCatalog} onChange={() => {}} onGraphChange={() => {}} />
      <NodeConfig graph={graph} node={graph.nodes.find(n => n.id === 'temperature')!} catalog={nodeCatalog} onChange={() => {}} onGraphChange={() => {}} />
    </>
  ), el)
  const [branch, temperature] = [...el.querySelectorAll('.hs-insp-node')]
  const condition = branch.querySelector<HTMLElement>('.hs-insp-input[data-input-id="value"]')!
  // Label, type chip and the input's description; the chip names the type in words.
  expect(condition.textContent).toContain('Condition')
  expect(condition.querySelector('.hs-type-chip')!.textContent).toBe('Yes/No')
  expect(condition.textContent).toContain('Yes continues on If true, no continues on If false.')
  expect([...branch.querySelectorAll('h3')].map(n => n.textContent)).toContain('Inputs')
  expect([...temperature.querySelectorAll('h3')].map(n => n.textContent)).toContain('Outputs')
  const output = temperature.querySelector('.hs-insp-output')!
  expect(output.querySelector('.hs-insp-output__type')!.textContent).toBe('Number')
  expect(output.getAttribute('aria-label')).toContain('Number')
  expect(el.querySelector('[aria-label="Appearance"]')).toBeNull()
})
it('offers "Leave empty" for nullable wire-only CLR types', () => {
  const el = host(),
    catalog = structuredClone(nodeCatalog)
  catalog.types['global::Example.Result?'] = {
    label: 'Example.Result?',
    description: 'Nullable result',
    nullable: true,
    category: 'Object',
    literalKind: null,
    assignableTo: ['global::Example.Result?'],
  }
  catalog.nodes['library:nullable'] = {
    label: 'Nullable consumer',
    description: 'Consumes a CLR result',
    category: 'Library',
    minGraphVersion: 5,
    config: { arguments: { type: 'object', default: {} } },
    argumentStorage: 'arguments',
    inputs: { result: { label: 'Result', types: ['global::Example.Result?'], description: 'Nullable result', required: true } },
    outputs: {},
    execution: ['next'],
  }
  const graph = addNode(createGraph('Nullable'), 'library:nullable', 'consumer', catalog)
  let config = graph.nodes.at(-1)!.config
  dispose = render(
    () => <NodeConfig graph={graph} node={graph.nodes.at(-1)!} catalog={catalog} onChange={next => (config = next)} onGraphChange={() => {}} />,
    el,
  )
  const result = el.querySelector('.hs-insp-input[data-input-id="result"]')!
  const none = [...result.querySelectorAll('label')].find(l => l.textContent?.includes('Leave empty'))!.querySelector('input')!
  expect(none.checked).toBe(false)
  none.click()
  expect(config.arguments).toEqual({ result: { kind: 'literal', type: 'global::Example.Result?', value: null } })
  // The chip names the type for people, never by its CLR name.
  expect(result.querySelector('.hs-type-chip')!.textContent).not.toContain('global::')
})

import { createSignal } from 'solid-js'
import type { VisualDiagnostic } from '../src/visual/api'
it('shows a validator message as it is under its field, and clears it when the problem is gone', () => {
  const el = host(),
    graph = fixture()
  const message = 'Condition requires Yes/No; Value outputs Number'
  const [diagnostics, setDiagnostics] = createSignal<VisualDiagnostic[]>([{ nodeId: 'branch', inputId: 'value', message, severity: 3, startLine: null, startColumn: null }])
  dispose = render(
    () => <NodeConfig graph={graph} node={graph.nodes.find(n => n.id === 'branch')!} catalog={nodeCatalog} diagnostics={diagnostics()} onChange={() => {}} onGraphChange={() => {}} />,
    el,
  )
  const row = el.querySelector('.hs-insp-input[data-input-id="value"]')!
  expect(row.getAttribute('aria-invalid')).toBe('true')
  expect(row.querySelector('.hs-input-editor__errors')!.textContent).toBe(message)
  // The problem is also a callout at the top that focuses the field.
  const callout = el.querySelector<HTMLButtonElement>('.hs-insp-problems button')!
  expect(callout.textContent).toBe(message)
  setDiagnostics([])
  expect(el.querySelector('.hs-insp-input[data-input-id="value"]')!.getAttribute('aria-invalid')).toBeNull()
  expect(el.textContent).not.toContain(message)
})

import richCatalogFixture from '../../../../tests/fixtures/visual-rich-catalog.json'
import type { Graph } from '../src/visual/contract'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
it('names source-derived CLR types in words on every chip and output', () => {
  const el = host(),
    catalog = richCatalogFixture.catalog as unknown as VisualCatalogSnapshot
  const graph = structuredClone(richCatalogFixture.cases[0].graph) as Graph
  const client = new QueryClient()
  dispose = render(() => (
    <QueryClientProvider client={client}>
      <HassCtx.Provider value={{ callWS: async () => ({ entities: [], areas: [], devices: [], floors: [], labels: [] }) } as any}>
        {graph.nodes.map(node => <NodeConfig graph={graph} node={node} catalog={catalog} onChange={() => {}} onGraphChange={() => {}} />)}
      </HassCtx.Provider>
    </QueryClientProvider>
  ), el)
  const shown = [...el.querySelectorAll('.hs-type-chip, .hs-insp-output__type')].map(c => c.textContent ?? '')
  expect(shown.length).toBeGreaterThan(0)
  for (const text of shown) expect(text).not.toMatch(/global::|System\.|EntityStateSnapshot|Boolean|String/)
})
