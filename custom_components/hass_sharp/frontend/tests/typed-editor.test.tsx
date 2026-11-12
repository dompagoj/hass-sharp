import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { NodeConfig } from '../src/visual/NodeConfig'
import { addNode, connectNodes, createGraph, createGraphHistory, catalogPalette, updateNode } from '../src/visual/graph'
import { nodeCatalog, type VisualCatalogSnapshot } from '../src/visual/graphV4'
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
it('creates only v4 graphs and derives custom palette entries from a document catalog', () => {
  expect(createGraph('New').version).toBe(4)
  const catalog: VisualCatalogSnapshot = structuredClone(nodeCatalog)
  catalog.nodes['custom:factory'] = {
    label: 'Factory',
    category: 'Library',
    description: 'Create an object',
    minGraphVersion: 4,
    config: {},
    inputs: {},
    outputs: {},
    execution: ['next'],
  }
  expect(catalogPalette(catalog).find(item => item.kind === 'custom:factory')?.label).toBe('Factory')
  expect(catalogPalette(nodeCatalog).some(item => item.kind === 'custom:factory')).toBe(false)
})
it('shows separate typed inputs/outputs, required condition semantics and collapsed appearance', () => {
  const el = host()
  const graph = fixture()
  const node = graph.nodes.find(n => n.id === 'branch')!
  dispose = render(() => <NodeConfig graph={graph} node={node} catalog={nodeCatalog} onChange={() => {}} onGraphChange={() => {}} />, el)
  expect(el.textContent).toContain('Condition · Boolean · Required')
  expect(el.textContent).toContain('True selects the True execution branch')
  expect([...el.querySelectorAll('h3')].map(n => n.textContent)).toEqual(expect.arrayContaining(['Inputs', 'Outputs']))
  expect(el.querySelector<HTMLDetailsElement>('[aria-label="Appearance"]')?.open).toBe(false)
})
it('renders typed labeled handles and rejects incompatible keyboard connection with exact target error', async () => {
  const el = host()
  const history = createGraphHistory(fixture())
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={() => {}} onChange={history.commit} />, el)
  const source = el.querySelector<HTMLElement>('[data-id="temperature"] .source[data-handleid="data:value"]')!
  const target = el.querySelector<HTMLElement>('[data-id="branch"] [data-handleid="data:value"]')!
  expect(source?.getAttribute('aria-label')).toContain('Number')
  expect(target?.getAttribute('aria-label')).toContain('Boolean')
  for (const handle of [source, target])
    handle.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(history.graph().edges.filter(e => e.kind === 'data')).toHaveLength(0)
  expect(el.textContent).toContain('Condition requires Boolean; Value outputs Number')
  expect(target.getAttribute('aria-invalid')).toBe('true')
})
it('removes only an Alt-primary-clicked native edge and restores it in one undo/redo', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  const el = host()
  const history = createGraphHistory(fixture())
  const original = structuredClone(history.graph())
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={() => {}} onChange={history.commit} />, el)
  await vi.waitFor(() => expect(el.querySelector('.solid-flow__edge')).not.toBeNull())
  const edge = el.querySelector('.solid-flow__edge')!
  const id = edge.getAttribute('data-id')!
  edge.dispatchEvent(new MouseEvent('click', { button: 0, bubbles: true, cancelable: true }))
  expect(history.canUndo()).toBe(false)
  edge.dispatchEvent(new MouseEvent('click', { button: 0, altKey: true, bubbles: true, cancelable: true }))
  expect(history.graph().edges.map(e => e.id)).toEqual(original.edges.filter(e => e.id !== id).map(e => e.id))
  expect(el.querySelector('[aria-label="Search steps"]')).toBeNull()
  history.undo()
  expect(history.graph()).toEqual(original)
  expect(history.canUndo()).toBe(false)
  history.redo()
  expect(history.graph().edges).toHaveLength(original.edges.length - 1)
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
    minGraphVersion: 4,
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

import { connectData, dataOutputs } from '../src/visual/graphV4'
import { createSignal } from 'solid-js'
it('colors connected and literal input rows from their actual types rather than the first accepted union type', () => {
  const el = host()
  const graph = fixture()
  dispose = render(() => <FlowAdapter graph={graph} onSelect={() => {}} onChange={() => {}} />, el)
  const input = el.querySelector<HTMLElement>('[data-id="temperature"] .target[data-handleid="data:value"]')!
  expect(input.style.background).toBe('var(--hs-port-number)')
})
it.each(['mouse', 'touch'])('connects real typed handles with %s, clears literals, and keeps full undo/redo snapshots', async device => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  const el = host()
  let graph = fixture()
  graph = updateNode(graph, 'temperature', { config: { type: 'boolean', value: { kind: 'literal', type: 'boolean', value: true } } })
  const history = createGraphHistory(graph)
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={() => {}} onChange={history.commit} />, el)
  await vi.waitFor(() => expect(el.querySelector('.solid-flow__edge')).not.toBeNull())
  const source = el.querySelector<HTMLElement>('[data-id="temperature"] .source[data-handleid="data:value"]')!,
    target = el.querySelector<HTMLElement>('[data-id="branch"] .target[data-handleid="data:value"]')!
  vi.spyOn(document, 'elementFromPoint').mockReturnValue(target)
  source.dispatchEvent(
    new PointerEvent('pointerdown', {
      bubbles: true,
      button: 0,
      pointerType: device === 'touch' ? 'touch' : 'mouse',
      clientX: 10,
      clientY: 10,
    }),
  )
  if (device === 'touch') {
    const finger = { identifier: 1, target: source, clientX: 200, clientY: 200 } as Touch
    document.dispatchEvent(new TouchEvent('touchmove', { bubbles: true, touches: [finger], changedTouches: [finger] }))
    document.dispatchEvent(new TouchEvent('touchend', { bubbles: true, touches: [], changedTouches: [finger] }))
  } else {
    document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 200, clientY: 200 }))
    document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: 200, clientY: 200 }))
  }
  expect(history.graph().edges.at(-1)).toMatchObject({
    kind: 'data',
    source: 'temperature',
    sourcePort: 'value',
    target: 'branch',
    targetPort: 'value',
  })
  expect(history.graph().nodes.find(n => n.id === 'branch')!.config.value).toEqual({ kind: 'unset' })
  const connected = history.graph()
  history.undo()
  expect(history.graph()).toEqual(graph)
  expect(history.canUndo()).toBe(false)
  history.redo()
  expect(history.graph()).toEqual(connected)
})
it('rejects a real incompatible data drag before edge creation and highlights the exact input', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  const el = host(),
    [graph, setGraph] = createSignal(fixture())
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, el)
  await vi.waitFor(() => expect(el.querySelector('.solid-flow__edge')).not.toBeNull())
  const source = el.querySelector<HTMLElement>('[data-id="temperature"] .source[data-handleid="data:value"]')!,
    target = el.querySelector<HTMLElement>('[data-id="branch"] .target[data-handleid="data:value"]')!
  vi.spyOn(document, 'elementFromPoint').mockReturnValue(target)
  source.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 10, clientY: 10 }))
  document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 200, clientY: 200 }))
  document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: 200, clientY: 200 }))
  expect(graph().edges.some(e => e.kind === 'data')).toBe(false)
  expect(target.getAttribute('aria-invalid')).toBe('true')
  expect(target.getAttribute('title')).toBe('Condition requires Boolean; Value outputs Number')
  expect(el.querySelector('[aria-label="Search steps"]')).toBeNull()
})
it('uses the same category token on all five typed handles, edges, inspector rows and String entity refinements', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  const el = host()
  let graph = createGraph('Colors')
  for (const type of ['boolean', 'number', 'string', 'object', 'array', 'entity_id']) {
    graph = addNode(graph, 'set_value', `source-${type}`)
    graph = addNode(graph, 'log', `target-${type}`)
    graph = updateNode(graph, `source-${type}`, {
      config: {
        type,
        value: {
          kind: 'literal',
          type,
          value:
            type === 'boolean'
              ? true
              : type === 'number'
                ? 1
                : type === 'object'
                  ? {}
                  : type === 'array'
                    ? []
                    : type === 'entity_id'
                      ? 'light.hall'
                      : 'hello',
        },
      },
    })
    const previous =
      type === 'boolean'
        ? 'service-1'
        : `target-${['boolean', 'number', 'string', 'object', 'array', 'entity_id'][['boolean', 'number', 'string', 'object', 'array', 'entity_id'].indexOf(type) - 1]}`
    graph = connectNodes(graph, previous, `source-${type}`)
    graph = connectNodes(graph, `source-${type}`, `target-${type}`)
    graph = connectData(graph, `source-${type}`, 'value', `target-${type}`, 'value')
  }
  dispose = render(
    () => (
      <>
        <FlowAdapter graph={graph} onSelect={() => {}} onChange={() => {}} />
        <NodeConfig
          graph={graph}
          node={graph.nodes.find(n => n.id === 'target-number')!}
          catalog={nodeCatalog}
          onChange={() => {}}
          onGraphChange={() => {}}
        />
      </>
    ),
    el,
  )
  await vi.waitFor(() => expect(el.querySelectorAll('.solid-flow__edge')).toHaveLength(graph.edges.length))
  for (const type of ['boolean', 'number', 'string', 'object', 'array', 'entity_id']) {
    const token = `var(--hs-port-${type === 'entity_id' ? 'string' : type})`
    expect(el.querySelector<HTMLElement>(`[data-id="source-${type}"] .source[data-handleid="data:value"]`)!.style.background).toBe(token)
    expect(el.querySelector<HTMLElement>(`[data-id="target-${type}"] .target[data-handleid="data:value"]`)!.style.background).toBe(token)
    const edge = graph.edges.find(e => e.kind === 'data' && e.source === `source-${type}`)!
    expect(
      [...el.querySelectorAll<SVGElement>('.solid-flow__edge')]
        .find(element => element.dataset.id === edge.id)!
        .querySelector<SVGElement>('.solid-flow__edge-path')!.style.stroke,
    ).toBe(token)
  }
  expect(el.querySelector<HTMLElement>('.hs-typed-input[data-input-id="value"]')!.style.getPropertyValue('--hs-value-color')).toBe(
    'var(--hs-port-number)',
  )
})

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

it('removes exactly the clicked data edge on Option-click, keeps its input unset and restores the entire imported graph', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  const el = host()
  let graph = fixture()
  graph = updateNode(graph, 'temperature', { config: { type: 'boolean', value: { kind: 'literal', type: 'boolean', value: true } } })
  graph = connectData(graph, 'temperature', 'value', 'branch', 'value')
  // The v4 contract accepts dormant literals beside an incoming wire. Removal must never revive one.
  graph = updateNode(graph, 'branch', { config: { value: { kind: 'literal', type: 'boolean', value: false } } })
  const history = createGraphHistory(graph)
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={() => {}} onChange={history.commit} />, el)
  const id = graph.edges.find(e => e.kind === 'data')!.id
  await vi.waitFor(() => expect([...el.querySelectorAll<SVGElement>('.solid-flow__edge')].some(e => e.dataset.id === id)).toBe(true))
  const edge = [...el.querySelectorAll<SVGElement>('.solid-flow__edge')].find(e => e.dataset.id === id)!
  edge.querySelector('path')!.dispatchEvent(new MouseEvent('click', { button: 1, altKey: true, bubbles: true }))
  expect(history.canUndo()).toBe(false)
  edge.querySelector('path')!.dispatchEvent(new MouseEvent('click', { button: 0, bubbles: true }))
  expect(history.canUndo()).toBe(false)
  edge.querySelector('path')!.dispatchEvent(new MouseEvent('click', { button: 0, altKey: true, bubbles: true, cancelable: true }))
  expect(history.graph().edges).toEqual(graph.edges.filter(e => e.id !== id))
  expect(history.graph().nodes.find(n => n.id === 'branch')!.config.value).toEqual({ kind: 'unset' })
  const removed = history.graph()
  history.undo()
  expect(history.graph()).toEqual(graph)
  expect(history.canUndo()).toBe(false)
  history.redo()
  expect(history.graph()).toEqual(removed)
})

import { VisualEditor } from '../src/visual/VisualEditor'
import { HassCtx } from '../src/context'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { visualFixture } from './visual-fixture'
it('keeps local/backend errors beneath the canvas and focuses exact typed input independently of source navigation', async () => {
  const el = host(),
    fixtureBackend = visualFixture()
  const graph = updateNode(updateNode(fixture(), 'branch', { config: { value: { kind: 'unset' } } }), 'trigger-1', {
    config: { type: 'ha', trigger: { trigger: 'state' } },
  })
  fixtureBackend.documents.set('errors', {
    version: 1,
    id: 'errors',
    revision: 1,
    graph,
    source: 'saved',
    sourceMap: [],
    automationId: 'visual:errors',
    currentRevisionActive: true,
  })
  fixtureBackend.controls.preview = {
    valid: false,
    source: 'class VisualAutomation {}',
    sourceMap: [{ nodeId: 'branch', startLine: 1, endLine: 1 }],
    diagnostics: [
      {
        nodeId: 'trigger-1',
        inputId: 'trigger',
        portId: 'trigger',
        message: 'Home Assistant: entity_id is required',
        severity: 3,
        startLine: null,
        startColumn: null,
      },
    ],
  }
  const client = new QueryClient()
  dispose = render(
    () => (
      <QueryClientProvider client={client}>
        <HassCtx.Provider value={fixtureBackend as any}>
          <VisualEditor name="Errors" documentId="errors" />
        </HassCtx.Provider>
      </QueryClientProvider>
    ),
    el,
  )
  await vi.waitFor(() =>
    expect(el.querySelector('[aria-label="Graph diagnostics"]')?.textContent).toContain('Home Assistant: entity_id is required'),
  )
  const list = el.querySelector('[aria-label="Graph diagnostics"]')!
  expect(list.previousElementSibling?.getAttribute('aria-label')).toBe('Automation canvas')
  ;[...list.querySelectorAll<HTMLButtonElement>('button')]
    .find(button => button.textContent === 'Home Assistant: entity_id is required')!
    .click()
  await Promise.resolve()
  expect(document.activeElement).toBe(el.querySelector('[aria-label="Native trigger JSON"]'))
  const node = el.querySelector<HTMLElement>('[data-id="branch"]')!,
    port = node.querySelector<HTMLElement>('.target[data-handleid="data:value"]')!
  expect(node.querySelector('.hs-flow-card--error')).not.toBeNull()
  expect(port.getAttribute('title')).toContain('Input is required')
  const tooltip = node.querySelector('[role="tooltip"]')!
  expect(tooltip.textContent).toContain('Input is required; connect a source or set a literal')
  expect(port.getAttribute('aria-describedby')).toBe(tooltip.id)
  node.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
  port.focus()
  expect(node.querySelector('.hs-flow-card--error')).not.toBeNull()
  ;[...list.querySelectorAll<HTMLButtonElement>('button')]
    .find(b => b.textContent === 'Input is required; connect a source or set a literal')!
    .click()
  await Promise.resolve()
  const input = el.querySelector<HTMLSelectElement>('[aria-label="Condition source"]')!
  expect(document.activeElement).toBe(input)
  expect(node.classList.contains('selected')).toBe(true)
  fixtureBackend.controls.preview = { valid: true, source: 'current', sourceMap: [], diagnostics: [] }
  input.value = 'literal'
  input.dispatchEvent(new Event('change', { bubbles: true }))
  expect(list.textContent).not.toContain('Input is required')
  expect(list.textContent).not.toContain('Home Assistant: entity_id is required')
  expect(node.querySelector('.hs-flow-card--error')).toBeNull()
  client.clear()
})

it('preserves full branch/carry/export identifiers through native handles and keyboard connections', () => {
  const el = host()
  let graph = addNode(addNode(addNode(createGraph('Scopes'), 'repeat', 'loop'), 'loop_result', 'result'), 'stop', 'after')
  graph = updateNode(graph, 'loop', {
    config: {
      count: { kind: 'literal', type: 'number', value: 3 },
      carry: [{ name: 'total', type: 'number', initial: { kind: 'literal', type: 'number', value: 0 } }],
      exports: [{ name: 'answer', type: 'number', initial: { kind: 'literal', type: 'number', value: 0 } }],
    },
  })
  graph = connectNodes(graph, 'service-1', 'loop')
  graph = connectNodes(graph, 'loop', 'result', 'body')
  graph = connectNodes(graph, 'loop', 'after', 'next')
  const history = createGraphHistory(graph)
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={() => {}} onChange={history.commit} />, el)
  const handle = (id: string, port: string, side: string) =>
    el.querySelector<HTMLElement>(`[data-id="${id}"] .${side}[data-handleid="${port}"]`)!
  expect(handle('loop', 'data:export:answer', 'source')?.getAttribute('aria-label')).toContain('answer')
  for (const target of [handle('loop', 'data:carry:total', 'source'), handle('result', 'data:carry:total', 'target')])
    target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(history.graph().edges.at(-1)).toMatchObject({ kind: 'data', sourcePort: 'carry:total', targetPort: 'carry:total' })
  history.undo()
  expect(history.graph()).toEqual(graph)
  dispose()
  dispose = undefined
  el.innerHTML = ''
  let parallel = addNode(addNode(createGraph('Branches'), 'parallel', 'parallel'), 'stop', 'branch-stop')
  parallel = updateNode(parallel, 'parallel', { config: { branches: ['stable-id'] } })
  parallel = connectNodes(parallel, 'service-1', 'parallel')
  const branchHistory = createGraphHistory(parallel)
  dispose = render(() => <FlowAdapter graph={branchHistory.graph()} onSelect={() => {}} onChange={branchHistory.commit} />, el)
  for (const target of [handle('parallel', 'branch:stable-id', 'source'), handle('branch-stop', 'in', 'target')])
    target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(branchHistory.graph().edges.at(-1)).toMatchObject({ source: 'parallel', target: 'branch-stop', port: 'branch:stable-id' })
})

import richCatalogFixture from '../../../../tests/fixtures/visual-rich-catalog.json'
import type { GraphV4 } from '../src/visual/graphV4'
it('preserves declared input labels when source-derived custom CLR Boolean outputs connect to unlabeled built-in nodes', () => {
  const el = host(),
    catalog = richCatalogFixture.catalog as unknown as VisualCatalogSnapshot
  let graph = structuredClone(richCatalogFixture.cases[0].graph) as GraphV4
  const predicateKind = Object.entries(catalog.nodes).find(([, spec]) => spec.label === 'Is on')![0]
  graph = addNode(graph, predicateKind, 'predicate', catalog)
  graph = addNode(graph, 'if', 'if', catalog)
  graph = connectNodes(graph, 'consume', 'predicate', undefined, catalog)
  graph = connectNodes(graph, 'predicate', 'if', undefined, catalog)
  graph = connectData(graph, 'describe', 'result', 'predicate', 'reading', catalog)
  graph = connectData(graph, 'predicate', 'result', 'if', 'value', catalog)
  graph = { ...graph, nodes: graph.nodes.map(({ label, ...node }) => node) }
  dispose = render(
    () => (
      <>
        <FlowAdapter catalog={catalog} graph={graph} onSelect={() => {}} onChange={() => {}} />
        <NodeConfig
          node={graph.nodes.find(n => n.id === 'describe')!}
          graph={graph}
          catalog={catalog}
          onChange={() => {}}
          onGraphChange={() => {}}
        />
        <NodeConfig
          node={graph.nodes.find(n => n.id === 'if')!}
          graph={graph}
          catalog={catalog}
          onChange={() => {}}
          onGraphChange={() => {}}
        />
      </>
    ),
    el,
  )
  expect(el.querySelector('[data-id="describe"] .hs-flow-card strong')?.textContent).toBe('Describe')
  expect(el.querySelector('[data-id="r"] .hs-flow-card strong')?.textContent).toBe('Read entity')
  expect(el.querySelector('[data-id="if"]')?.getAttribute('aria-label')).toContain('If')
  expect(el.textContent).toContain('Condition · Boolean · Required')
  expect(el.querySelector('[data-id="if"] .target[data-handleid="data:value"]')?.getAttribute('aria-label')).toBe(
    'Condition · Boolean input',
  )
  expect(el.querySelector('[data-id="predicate"] .source[data-handleid="data:result"]')?.getAttribute('aria-label')).toBe(
    'Result · bool output',
  )
  expect(el.textContent).toContain('From Is on')
  expect(el.textContent).toContain('From Read entity')
  expect(el.textContent).toContain('To Consume')
  expect(el.querySelector('[data-id="describe"] .hs-flow-card')?.textContent).not.toContain('library:')
  expect(catalog.types.number.label).toBe('Number')
  expect(catalog.types['global::System.Double'].label).toBe('double')
})
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
