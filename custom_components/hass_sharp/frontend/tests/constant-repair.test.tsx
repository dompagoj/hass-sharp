import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { VisualEditor } from '../src/visual/VisualEditor'
import { NodeConfig } from '../src/visual/NodeConfig'
import { dataInputs, nodeCatalog, validateGraph, type Graph, type VisualCatalogSnapshot } from '../src/visual/contract'
import { InputEditor } from '../src/visual/InputEditor'
import { addNode, createGraph, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import { visualFixture } from './visual-fixture'
import { constantTypeLabels, constantTypes, ConstantType } from '../src/visual/constant'
import { typeLabel } from '../src/visual/typeLabels'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'

let dispose: (() => void) | undefined
afterEach(async () => {
  dispose?.()
  dispose = undefined
  vi.restoreAllMocks()
  document.body.innerHTML = ''
  await new Promise(resolve => setTimeout(resolve, 0))
})

const lit = (type: string, value: unknown) => ({ kind: 'literal' as const, type, value })
const root = () => { const el = document.createElement('div'); el.className = 'hs-app'; document.body.append(el); return el }
const byLabel = (scope: ParentNode, label: string) =>
  [...scope.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === label || b.getAttribute('aria-label') === label)

/** A Constant whose Type is Number (or `type`), holding a value stored before its Type set the value's type. */
function stale(value: unknown, storedAs = 'string', type = 'number'): VisualGraph {
  let g = addNode(createGraph('Repair'), 'constant', 'c', nodeCatalog)
  g = updateNode(g, 'c', { config: { type, value: lit(storedAs, value) }, position: { x: 400, y: 300 } })
  return g
}

function mount(graph: VisualGraph) {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(260)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(120)
  const host = root(), fixture = visualFixture()
  fixture.documents.set('saved', { version: 1, id: 'saved', revision: 1, graph, source: '// fixture', sourceMap: [], automationId: 'visual:saved', currentRevisionActive: true, catalogGeneration: 'saved-fixture' })
  dispose = render(() => <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <HassCtx.Provider value={{ ...fixture, user: { is_admin: true } } as any}>
      <VisualEditor name="Repair" documentId="saved" />
    </HassCtx.Provider></QueryClientProvider>, host)
  return host
}
const graphOf = (host: Element) => {
  const pre = () => host.querySelector('pre[aria-label="Graph JSON"]')
  if (!pre()) byLabel(host, 'Graph JSON')!.click()
  return JSON.parse(pre()!.textContent!) as Graph
}
const providers = (children: () => any) => <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
  <HassCtx.Provider value={{ ...visualFixture(), user: { is_admin: true } } as any}>{children()}</HassCtx.Provider></QueryClientProvider>
const configOf = (host: Element, id = 'c') => graphOf(host).nodes.find(n => n.id === id)!.config
const blur = (field: HTMLInputElement) => { field.dispatchEvent(new FocusEvent('blur')); field.dispatchEvent(new FocusEvent('focusout', { bubbles: true })) }
const cardField = (host: Element) => host.querySelector<HTMLInputElement>('[data-id="c"] .hs-card-editor input[aria-label="Value"]')!
const panelField = (host: Element) => host.querySelector<HTMLInputElement>('.hs-inspector [data-input-id="value"] input[aria-label="Value"]')!

it("shows a Constant's stored text in its Type's own editor, and stores it as the Type when committed unchanged: one undo step", async () => {
  const host = mount(stale('21'))
  await vi.waitFor(() => expect(cardField(host)).not.toBeNull())
  // The number field, holding the stored text (not an empty field, not the JSON editor).
  expect(cardField(host).value).toBe('21')
  expect(host.querySelector('[data-id="c"] .hs-json-editor, [data-id="c"] textarea')).toBeNull()
  host.querySelector<HTMLElement>('[data-id="c"] .hs-card__title')!.click()
  await vi.waitFor(() => expect(panelField(host)).not.toBeNull())
  expect(panelField(host).value).toBe('21')
  // The validator refuses the value as stored.
  expect(validateGraph(graphOf(host), nodeCatalog).some(d => d.severity === 3 && d.nodeId === 'c')).toBe(true)

  // Committing it unchanged stores it as a number; the graph then validates.
  blur(cardField(host))
  expect(configOf(host)).toEqual({ type: 'number', value: lit('number', 21) })
  expect(validateGraph(graphOf(host), nodeCatalog).filter(d => d.severity === 3)).toEqual([])
  await vi.waitFor(() => expect(cardField(host).value).toBe('21'))
  // Committing again changes nothing more.
  blur(cardField(host))
  blur(panelField(host))
  // One undo step brings back the value as it was stored.
  byLabel(host, 'Undo')!.click()
  expect(configOf(host)).toEqual({ type: 'number', value: lit('string', '21') })
  expect(byLabel(host, 'Undo')!.disabled).toBe(true)
})

it('stores the stale value from the panel too, and a number stored under another type is stored again as a number', async () => {
  const host = mount(stale(21, 'integer'))
  await vi.waitFor(() => expect(cardField(host)).not.toBeNull())
  host.querySelector<HTMLElement>('[data-id="c"] .hs-card__title')!.click()
  await vi.waitFor(() => expect(panelField(host)).not.toBeNull())
  expect(panelField(host).value).toBe('21')
  panelField(host).dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(configOf(host)).toEqual({ type: 'number', value: lit('number', 21) })
  byLabel(host, 'Undo')!.click()
  expect(configOf(host)).toEqual({ type: 'number', value: lit('integer', 21) })
})

it("shows a stored value that does not read as the Type with the field's own error, and keeps it", async () => {
  const host = mount(stale('abc'))
  await vi.waitFor(() => expect(cardField(host)).not.toBeNull())
  const field = cardField(host)
  expect(field.value).toBe('abc')
  expect(field.getAttribute('aria-invalid')).toBe('true')
  const error = () => host.querySelector('[data-id="c"] .hs-card-editor .hs-error')
  expect(error()?.textContent).toBe('Enter a number.')
  // Committing it, or pressing Escape, neither drops it nor hides why it is wrong.
  blur(field)
  field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  expect(configOf(host)).toEqual({ type: 'number', value: lit('string', 'abc') })
  expect(cardField(host).value).toBe('abc')
  expect(error()?.textContent).toBe('Enter a number.')
  expect(byLabel(host, 'Undo')!.disabled).toBe(true)
  // Typing a number repairs it, in one undo step.
  cardField(host).value = '7'
  cardField(host).dispatchEvent(new Event('input', { bubbles: true }))
  blur(cardField(host))
  expect(configOf(host)).toEqual({ type: 'number', value: lit('number', 7) })
  await vi.waitFor(() => expect(error()).toBeNull())
  byLabel(host, 'Undo')!.click()
  expect(configOf(host)).toEqual({ type: 'number', value: lit('string', 'abc') })
})

it('shows a Yes/No Constant stored as the text True as that text, stores it as yes/no in one undo step, and keeps anything else with its error', async () => {
  const host = mount(stale('True', 'string', 'boolean'))
  await vi.waitFor(() => expect(cardField(host)).not.toBeNull())
  // A text field holding the stored text, not a switch that could only show it as No.
  expect(cardField(host).value).toBe('True')
  expect(host.querySelector('[data-id="c"] .hs-card-editor input[role="switch"]')).toBeNull()
  cardField(host).dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(configOf(host)).toEqual({ type: 'boolean', value: lit('boolean', true) })
  // Stored as yes/no, the switch is back.
  await vi.waitFor(() => expect(host.querySelector<HTMLInputElement>('[data-id="c"] .hs-card-editor input[role="switch"]')?.checked).toBe(true))
  byLabel(host, 'Undo')!.click()
  expect(configOf(host)).toEqual({ type: 'boolean', value: lit('string', 'True') })
  expect(byLabel(host, 'Undo')!.disabled).toBe(true)
  dispose!(); document.body.innerHTML = ''

  const other = mount(stale('maybe', 'string', 'boolean'))
  await vi.waitFor(() => expect(cardField(other)).not.toBeNull())
  expect(cardField(other).value).toBe('maybe')
  expect(cardField(other).getAttribute('aria-invalid')).toBe('true')
  const error = () => other.querySelector('[data-id="c"] .hs-card-editor .hs-error')
  expect(error()?.textContent).toBe('Enter true or false.')
  blur(cardField(other))
  expect(configOf(other)).toEqual({ type: 'boolean', value: lit('string', 'maybe') })
  expect(error()?.textContent).toBe('Enter true or false.')
  cardField(other).value = 'FALSE'
  cardField(other).dispatchEvent(new Event('input', { bubbles: true }))
  blur(cardField(other))
  expect(configOf(other)).toEqual({ type: 'boolean', value: lit('boolean', false) })
})

it("changes a Constant's Type in the panel: the value stays when it reads as the new Type, is cleared otherwise, one undo step each", () => {
  const el = root()
  let start = addNode(createGraph('Panel'), 'constant', 'c', nodeCatalog)
  start = updateNode(start, 'c', { config: { type: 'string', value: lit('string', 'light.hall') } })
  const history = createGraphHistory(start)
  dispose = render(() => providers(() => {
    const node = () => history.graph().nodes.find(n => n.id === 'c')!
    return <NodeConfig graph={history.graph()} node={node()} catalog={nodeCatalog}
      onChange={config => history.commit(updateNode(history.graph(), 'c', { config }))} onGraphChange={graph => history.commit(graph)} />
  }), el)
  const type = () => el.querySelector<HTMLSelectElement>('[data-input-id="type"] select')!
  const config = () => history.graph().nodes.find(n => n.id === 'c')!.config
  const choose = (value: string) => { type().value = value; type().dispatchEvent(new Event('change', { bubbles: true })) }
  choose('entity_id')
  expect(config()).toEqual({ type: 'entity_id', value: lit('entity_id', 'light.hall') })
  choose('number')
  expect(config()).toEqual({ type: 'number', value: { kind: 'unset' } })
  expect(type().value).toBe('number')
  history.undo()
  expect(config()).toEqual({ type: 'entity_id', value: lit('entity_id', 'light.hall') })
  history.undo()
  expect(config()).toEqual({ type: 'string', value: lit('string', 'light.hall') })
  expect(history.canUndo()).toBe(false)
})

it("keeps a Constant's outgoing wire when its new Type no longer fits the input it feeds: an error on that input, as with Convert", () => {
  let g = addNode(createGraph('Fit'), 'constant', 'c', nodeCatalog)
  g = updateNode(g, 'c', { config: { type: 'boolean', value: lit('boolean', true) } })
  g = addNode(g, 'if', 'branch', nodeCatalog)
  g = { ...g, edges: [...g.edges, { id: 'w', kind: 'data', source: 'c', sourcePort: 'value', target: 'branch', targetPort: 'value' }] } as VisualGraph
  const wireErrors = (graph: VisualGraph) => validateGraph(graph, nodeCatalog).filter(d => d.severity === 3 && d.code !== 'node.unreachable')
  expect(wireErrors(g)).toEqual([])
  const el = root()
  const history = createGraphHistory(g)
  dispose = render(() => providers(() => {
    const node = () => history.graph().nodes.find(n => n.id === 'c')!
    return <NodeConfig graph={history.graph()} node={node()} catalog={nodeCatalog}
      onChange={config => history.commit(updateNode(history.graph(), 'c', { config }))} onGraphChange={graph => history.commit(graph)} />
  }), el)
  const type = el.querySelector<HTMLSelectElement>('[data-input-id="type"] select')!
  type.value = 'string'
  type.dispatchEvent(new Event('change', { bubbles: true }))
  const after = history.graph()
  expect(after.nodes.find(n => n.id === 'c')!.config.type).toBe('string')
  expect(after.edges.find(e => e.id === 'w')).toEqual(g.edges.find(e => e.id === 'w'))
  // The wire stays; the If's Condition carries the mismatch (the Constant's own problem is only its cleared value).
  expect(wireErrors(after).map(d => [d.nodeId, d.inputId, d.message])).toEqual([
    ['branch', 'value', 'Condition accepts Yes/No, not Text'],
    ['c', 'value', 'Value is required'],
  ])
})

it('offers a Constant only the types a value can be typed in for, and keeps showing a stored Type that is not offered', () => {
  const catalog = (rich as any).catalog
  const offered = constantTypes(catalog)
  // The built-in value types, entity ids and targets, JSON objects and lists, the CLR text, number and boolean types
  // and enums; never an interface, an abstract or record type (no literal form) and never a nullable one.
  expect(offered).toEqual(expect.arrayContaining(['boolean', 'number', 'string', 'object', 'array', 'entity_id', 'global::System.Int32', 'global::NodeLib.Mode']))
  expect(offered.every(id => !!catalog.types[id].literalKind && catalog.types[id].nullable !== true)).toBe(true)
  for (const left of ['global::System.IComparable', 'global::System.ISpanParsable<global::System.Boolean>']) expect(offered).not.toContain(left)
  expect(Object.keys(catalog.types).filter(id => catalog.types[id].nullable === true).some(id => offered.includes(id))).toBe(false)
  expect(offered.length).toBeLessThan(Object.keys(catalog.types).length / 4)

  // A Constant stored with a Type that is no longer offered: the card and the panel show it, selected, by its name.
  const stored = 'global::System.IComparable'
  let g = addNode(createGraph('Stored'), 'constant', 'c', catalog)
  g = updateNode(g, 'c', { config: { type: stored, value: { kind: 'unset' } } })
  const el = root()
  const changes: unknown[] = []
  dispose = render(() => providers(() => <>
    <ConstantType type={stored} catalog={catalog} onChange={type => changes.push(type)} />
    <NodeConfig graph={g} node={g.nodes.find(n => n.id === 'c')!} catalog={catalog} onChange={config => changes.push(config)} onGraphChange={() => {}} />
  </>), el)
  const [card, panel] = [el.querySelector<HTMLSelectElement>('select[aria-label="Type"]')!, el.querySelector<HTMLSelectElement>('[data-input-id="type"] select')!]
  for (const select of [card, panel]) {
    expect(select.selectedOptions[0].textContent).toBe(typeLabel(stored, catalog))
    expect([...select.options].map(o => o.value).filter(v => v !== '' && v !== stored)).toEqual(offered)
  }
  expect(changes).toEqual([])
})

it('names no two entries of the Type list alike, and offers a type that is the same as one of the editor\'s own once', () => {
  const catalog = (rich as any).catalog
  const offered = constantTypes(catalog), labels = constantTypeLabels(catalog)
  // C#'s bool, double, string, JsonObject and JsonArray are Yes/No, Number, Text, Object and List: offered once, as those.
  for (const same of ['global::System.Boolean', 'global::System.Double', 'global::System.String', 'global::System.Text.Json.Nodes.JsonObject', 'global::System.Text.Json.Nodes.JsonArray'])
    expect(offered, same).not.toContain(same)
  // int and float are other types that read as Number: their C# name follows. The editor's own Number keeps its label.
  expect(offered.map(id => labels.get(id))).toEqual(['Yes/No', 'Number', 'Text', 'Object', 'List', 'Entity', 'Target', 'Number (int)', 'Mode', 'Number (float)'])
  expect(new Set(labels.values()).size).toBe(offered.length)
  // A stored Type that is the same as an offered one is named apart from it too, and stays selected.
  const stored = 'global::System.Double'
  expect(constantTypeLabels(catalog, stored).get(stored)).toBe('Number (double)')
  let g = addNode(createGraph('Stored'), 'constant', 'c', catalog)
  g = updateNode(g, 'c', { config: { type: stored, value: { kind: 'literal', type: stored, value: 2 } } })
  const el = root()
  dispose = render(() => providers(() => <>
    <ConstantType type={stored} catalog={catalog} onChange={() => {}} />
    <NodeConfig graph={g} node={g.nodes.find(n => n.id === 'c')!} catalog={catalog} onChange={() => {}} onGraphChange={() => {}} />
  </>), el)
  for (const select of [el.querySelector<HTMLSelectElement>('select[aria-label="Type"]')!, el.querySelector<HTMLSelectElement>('[data-input-id="type"] select')!]) {
    expect(select.selectedOptions[0].textContent).toBe('Number (double)')
    const names = [...select.options].map(o => o.textContent)
    expect(new Set(names).size).toBe(names.length)
  }
})

it('names two offered types with the same short name apart by as much of their full name as it takes, never "Mode (Mode)"', () => {
  const base = (rich as any).catalog
  const mode = base.types['global::NodeLib.Mode']
  const enumType = (label: string, id: string) => ({ ...mode, label, description: label, assignableTo: [id, 'global::System.Enum'] })
  const ids = ['global::Lights.Node1.Mode', 'global::Lights.Node2.Mode', 'global::Heat.Node1.Mode', 'global::Fans.Outer.Mode']
  const catalog = {
    ...base,
    types: {
      ...base.types,
      // Two Custom C# nodes declaring `enum Mode`, a third under another namespace with the same node name, and the
      // library's NodeLib.Mode: all read as "Mode".
      [ids[0]]: enumType('Lights.Node1.Mode', ids[0]),
      [ids[1]]: enumType('Lights.Node2.Mode', ids[1]),
      [ids[2]]: enumType('Heat.Node1.Mode', ids[2]),
      [ids[3]]: enumType('Fans.Outer.Mode', ids[3]),
    },
  }
  const labels = constantTypeLabels(catalog)
  const modes = ['global::NodeLib.Mode', ...ids].map(id => labels.get(id))
  expect(modes).toEqual(['Mode (NodeLib.Mode)', 'Mode (Lights.Node1.Mode)', 'Mode (Node2.Mode)', 'Mode (Heat.Node1.Mode)', 'Mode (Outer.Mode)'])
  const offered = constantTypes(catalog).map(id => labels.get(id))
  expect(new Set(offered).size).toBe(offered.length)
  expect(offered.some(label => /^(.+) \(\1\)$/.test(label!))).toBe(false)
  // A label alone that is unique keeps no suffix; one shared only by C# types gets the shortest name that differs.
  const two = { ...base, types: { ...base.types, [ids[0]]: enumType('Lights.Node1.Mode', ids[0]) } }
  expect(['global::NodeLib.Mode', ids[0]].map(id => constantTypeLabels(two).get(id))).toEqual(['Mode (NodeLib.Mode)', 'Mode (Node1.Mode)'])
  const one = constantTypeLabels(base)
  expect(one.get('global::NodeLib.Mode')).toBe('Mode')
})

/** A Constant's Value input under this Type, as its card and panel edit it. */
const constantValue = (type: string, catalog: VisualCatalogSnapshot) => {
  let g = addNode(createGraph('Value'), 'constant', 'c', catalog)
  g = updateNode(g, 'c', { config: { type, value: { kind: 'unset' } } })
  return dataInputs(g.nodes.find(n => n.id === 'c')!, undefined, catalog).value
}
const editor = (type: string, value: unknown, catalog: VisualCatalogSnapshot) => {
  const changes: unknown[] = [], el = root()
  dispose = render(() => providers(() => <InputEditor variant="panel" input={constantValue(type, catalog)} value={value as any} label="Value" catalog={catalog} retype
    onChange={next => changes.push(next)} />), el)
  return { el, changes }
}

it('leaves a value its Type takes as it is (a number under a C# int, an entity id under Text): focus and leave change nothing', async () => {
  for (const [type, value, catalog] of [['global::System.Int32', lit('number', 21), (rich as any).catalog], ['string', lit('entity_id', 'light.hall'), nodeCatalog]] as const) {
    const { el, changes } = editor(type, value, catalog)
    const field = el.querySelector<HTMLInputElement>('input[aria-label="Value"]')!
    expect(field.value, type).toBe(String(value.value))
    expect(field.getAttribute('aria-invalid'), type).toBeNull()
    field.focus(); blur(field)
    field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
    expect(changes, type).toEqual([])
    dispose!(); dispose = undefined; document.body.innerHTML = ''
  }
  // In the editor: no undo step, nothing to save.
  let g = addNode(createGraph('Repair'), 'constant', 'c', nodeCatalog)
  g = updateNode(g, 'c', { config: { type: 'string', value: lit('entity_id', 'light.hall') }, position: { x: 400, y: 300 } })
  const host = mount(g)
  await vi.waitFor(() => expect(cardField(host)).not.toBeNull())
  blur(cardField(host))
  expect(configOf(host)).toEqual({ type: 'string', value: lit('entity_id', 'light.hall') })
  expect(byLabel(host, 'Undo')!.disabled).toBe(true)
})

it('stores an enum value of another type as the Type with an explicit action, since its select already shows it: one change', () => {
  const catalog = (rich as any).catalog
  const { el, changes } = editor('global::NodeLib.Mode', lit('number', 1), catalog)
  const select = el.querySelector<HTMLSelectElement>('select[aria-label="Value"]')!
  expect(select.selectedOptions[0].textContent).toBe('On')
  byLabel(el, 'Store as Mode')!.click()
  expect(changes).toEqual([lit('global::NodeLib.Mode', 1)])
  dispose!(); dispose = undefined; document.body.innerHTML = ''
  // A value of the Type has no such action.
  const valid = editor('global::NodeLib.Mode', lit('global::NodeLib.Mode', 1), catalog)
  expect(byLabel(valid.el, 'Store as Mode')).toBeUndefined()
})

it('stores the unchanged text again after Undo, in the same field', async () => {
  const host = mount(stale('21'))
  await vi.waitFor(() => expect(cardField(host)).not.toBeNull())
  const field = cardField(host)
  blur(field)
  expect(configOf(host)).toEqual({ type: 'number', value: lit('number', 21) })
  byLabel(host, 'Undo')!.click()
  expect(configOf(host)).toEqual({ type: 'number', value: lit('string', '21') })
  // The same field (its text never changed) stores it again.
  expect(cardField(host)).toBe(field)
  blur(field)
  expect(configOf(host)).toEqual({ type: 'number', value: lit('number', 21) })
})

it('keeps an empty stored text under Number with the field\'s error, until a number is typed', async () => {
  const host = mount(stale(''))
  await vi.waitFor(() => expect(cardField(host)).not.toBeNull())
  const error = () => host.querySelector('[data-id="c"] .hs-card-editor .hs-error')
  expect(error()?.textContent).toBe('Enter a number.')
  blur(cardField(host))
  cardField(host).dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(configOf(host)).toEqual({ type: 'number', value: lit('string', '') })
  expect(error()?.textContent).toBe('Enter a number.')
  expect(byLabel(host, 'Undo')!.disabled).toBe(true)
  cardField(host).value = '7'
  cardField(host).dispatchEvent(new Event('input', { bubbles: true }))
  blur(cardField(host))
  expect(configOf(host)).toEqual({ type: 'number', value: lit('number', 7) })
})
