import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { NodeConfig } from '../src/visual/NodeConfig'
import { addNode, connectNodes, createGraph, createGraphHistory, catalogPalette, updateNode } from '../src/visual/graph'
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
  let graph = addNode(createGraph('Typed'), 'set_value', 'temperature')
  graph = updateNode(graph, 'temperature', { config: { type: 'number', value: { kind: 'literal', type: 'number', value: 20 } } })
  graph = addNode(graph, 'if', 'branch')
  graph = connectNodes(graph, 'service-1', 'temperature')
  graph = connectNodes(graph, 'temperature', 'branch')
  return graph
}
it('shows separate typed inputs/outputs and required condition semantics, without an appearance section', () => {
  const el = host()
  const graph = fixture()
  const node = graph.nodes.find(n => n.id === 'branch')!
  dispose = render(() => <NodeConfig graph={graph} node={node} catalog={nodeCatalog} onChange={() => {}} onGraphChange={() => {}} />, el)
  expect(el.textContent).toContain('Condition · Boolean · Required')
  expect(el.textContent).toContain('True selects the True execution branch')
  expect([...el.querySelectorAll('h3')].map(n => n.textContent)).toEqual(expect.arrayContaining(['Inputs', 'Outputs']))
  expect(el.querySelector('[aria-label="Appearance"]')).toBeNull()
})
it('offers explicit null for nullable wire-only CLR types', () => {
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
    () => (
      <NodeConfig graph={graph} node={graph.nodes.at(-1)!} catalog={catalog} onChange={next => (config = next)} onGraphChange={() => {}} />
    ),
    el,
  )
  const mode = el.querySelector<HTMLSelectElement>('[aria-label="Result source"]')!
  expect([...mode.options].map(o => o.value)).toContain('literal')
  mode.value = 'literal'
  mode.dispatchEvent(new Event('change', { bubbles: true }))
  expect(config.arguments).toEqual({ result: { kind: 'literal', type: 'global::Example.Result?', value: null } })
})

import { connectData, dataOutputs } from '../src/visual/contract'
import { createSignal } from 'solid-js'
it('highlights inspector connection mismatch with the same exact typed explanation', () => {
  const el = host(),
    graph = fixture()
  let changed = false
  dispose = render(
    () => (
      <NodeConfig
        graph={graph}
        node={graph.nodes.find(n => n.id === 'branch')!}
        catalog={nodeCatalog}
        onChange={() => {}}
        onGraphChange={() => {
          changed = true
        }}
      />
    ),
    el,
  )
  const select = el.querySelector<HTMLSelectElement>('[aria-label="Condition connection"]')!
  select.value = JSON.stringify(['temperature', 'value'])
  select.dispatchEvent(new Event('change', { bubbles: true }))
  ;[...el.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent === 'Connect Condition')!.click()
  expect(changed).toBe(false)
  expect(el.querySelector('[data-input-id="value"]')?.getAttribute('aria-invalid')).toBe('true')
  expect(el.textContent).toContain('Condition requires Boolean; Value outputs Number')
})

import { VisualEditor } from '../src/visual/VisualEditor'
import { HassCtx } from '../src/context'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { visualFixture } from './visual-fixture'
import richCatalogFixture from '../../../../tests/fixtures/visual-rich-catalog.json'
import type { Graph } from '../src/visual/contract'
it('clears a rejected inspector connection when a later literal edit resolves its context', () => {
  const el = host(),
    [graph, setGraph] = createSignal(fixture())
  dispose = render(
    () => (
      <NodeConfig
        graph={graph()}
        node={graph().nodes.find(n => n.id === 'branch')!}
        catalog={nodeCatalog}
        onChange={config => setGraph(updateNode(graph(), 'branch', { config }))}
        onGraphChange={setGraph}
      />
    ),
    el,
  )
  const connection = el.querySelector<HTMLSelectElement>('[aria-label="Condition connection"]')!
  connection.value = JSON.stringify(['temperature', 'value'])
  connection.dispatchEvent(new Event('change', { bubbles: true }))
  ;[...el.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent === 'Connect Condition')!.click()
  expect(el.textContent).toContain('Condition requires Boolean; Value outputs Number')
  const value = el.querySelector<HTMLSelectElement>('[aria-label="Condition"]')!
  value.value = 'false'
  value.dispatchEvent(new Event('change', { bubbles: true }))
  expect(el.textContent).not.toContain('Condition requires Boolean; Value outputs Number')
  expect(el.querySelector('[data-input-id="value"]')?.getAttribute('aria-invalid')).toBeNull()
})
