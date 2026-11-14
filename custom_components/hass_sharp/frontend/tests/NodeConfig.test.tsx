import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { Inspector } from '../src/visual/Inspector'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { QuickAdd } from '../src/visual/QuickAdd'
import { addNode, catalogCategories, catalogPalette, createGraph, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import { connectData, nodeCatalog, validateGraph, type ServicesSection, type VisualCatalogSnapshot } from '../src/visual/contract'
import type { VisualDiagnostic } from '../src/visual/api'
import { fieldOffered, filterFacts, targetEntities } from '../src/visual/ServiceFields'
import { HassCtx } from '../src/context'
import services from '../../../../tests/fixtures/visual-services.json'

let dispose: (() => void) | undefined
let client: QueryClient | undefined
afterEach(async () => {
  dispose?.()
  dispose = undefined
  client?.clear()
  vi.restoreAllMocks()
  document.body.innerHTML = ''
  await new Promise(resolve => setTimeout(resolve, 0))
})
const catalog = { ...nodeCatalog, services: services as unknown as ServicesSection } as VisualCatalogSnapshot
const entity = (entity_id: string, name: string, state_options: string[] | null = null) => ({
  entity_id, name, domain: entity_id.split('.')[0], area_id: null, device_id: null, device_class: null, supported_features: null, unit: null, icon: null, disabled: false, state_options,
})
const registry = {
  entities: [entity('light.hall', 'Hall'), entity('climate.living', 'Living room', ['off', 'heat', 'cool', 'unavailable']), entity('switch.fan', 'Fan')],
  areas: [], devices: [], floors: [], labels: [],
}
const host = () => {
  const el = document.createElement('div')
  el.className = 'hs-app'
  document.body.append(el)
  return el
}
const lit = (type: string, value: unknown) => ({ kind: 'literal' as const, type, value })
const button = (root: Element, label: string) =>
  [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === label || b.getAttribute('aria-label') === label)
const row = (root: Element, id: string) => [...root.querySelectorAll<HTMLElement>('.hs-inspector [data-input-id]')].find(el => el.dataset.inputId === id)
function type(field: HTMLInputElement | HTMLTextAreaElement, value: string) {
  field.value = value
  field.dispatchEvent(new Event('input', { bubbles: true }))
  field.dispatchEvent(new FocusEvent('blur'))
}

/** The inspector over a graph history (and the canvas beside it when asked), as the editor wires them. */
function mount(graph: VisualGraph, id: string | undefined, options: { canvas?: boolean; diagnostics?: VisualDiagnostic[]; catalog?: VisualCatalogSnapshot; hass?: Record<string, unknown>; edge?: string; onEditCode?: (id: string) => void } = {}) {
  const el = host()
  const history = createGraphHistory(graph)
  const [selected, setSelected] = createSignal(id)
  const [selection, setSelection] = createSignal<{ ids: string[]; edge?: string }>({ ids: id ? [id] : [], edge: options.edge })
  const current = options.catalog ?? catalog
  client = new QueryClient()
  const hass = { callWS: async (message: { type: string }) => (message.type === 'hass_sharp/get_entities' ? structuredClone(registry) : {}), ...options.hass }
  dispose = render(
    () => (
      <QueryClientProvider client={client!}>
        <HassCtx.Provider value={hass as any}>
          <div class="hs-visual-workspace">
            {options.canvas && (
              <FlowAdapter graph={history.graph()} catalog={current} selectedId={selected()} onSelect={setSelected}
                onSelectionChange={(ids, edge) => setSelection({ ids, edge })} onChange={history.commit} />
            )}
            <Inspector graph={history.graph()} catalog={current} diagnostics={options.diagnostics ?? []} commit={history.commit}
              selected={history.graph().nodes.find(n => n.id === selected())} selectedIds={selection().ids} selectedEdgeId={selection().edge}
              onSelect={next => { setSelected(next); setSelection({ ids: next ? [next] : [] }) }} onEditCode={options.onEditCode} />
          </div>
        </HassCtx.Provider>
      </QueryClientProvider>
    ),
    el,
  )
  const node = (nodeId = selected()!) => history.graph().nodes.find(n => n.id === nodeId)!
  return { el, history, node, selected, setSelected }
}
/** Picks a service in the inspector's service picker by its id. */
async function pickService(el: Element, id: string) {
  el.querySelector<HTMLButtonElement>('.hs-inspector [data-input-id="service"] button[aria-label="Service"]')!.click()
  const search = el.querySelector<HTMLInputElement>('[aria-label="Search services"]')!
  search.value = id
  search.dispatchEvent(new Event('input', { bubbles: true }))
  const option = await vi.waitFor(() => {
    const found = [...el.querySelectorAll<HTMLElement>('[role="option"]')].find(o => o.textContent?.includes(id))
    expect(found, id).toBeDefined()
    return found!
  })
  option.click()
}
/** The options "+ Add field" offers, by visible label. */
function addFieldOptions(el: Element) {
  const field = el.querySelector<HTMLInputElement>('.hs-inspector input[aria-label="Add field"]')
  if (!field) return []
  field.dispatchEvent(new FocusEvent('focus'))
  field.value = ''
  field.dispatchEvent(new Event('input', { bubbles: true }))
  const labels = [...el.querySelectorAll('[aria-label="Add field options"] [role="option"]')].map(o => o.textContent ?? '')
  field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
  return labels
}
function addField(el: Element, label: string, key = '') {
  const field = el.querySelector<HTMLInputElement>('.hs-inspector input[aria-label="Add field"]')!
  field.value = label
  field.dispatchEvent(new Event('input', { bubbles: true }))
  const option = [...el.querySelectorAll<HTMLElement>('[aria-label="Add field options"] [role="option"]')].find(o => o.textContent?.startsWith(label) && o.textContent.includes(key))!
  expect(option, label).toBeDefined()
  option.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }))
  option.click()
}

it('picking light.turn_on gives a lights-only target picker and "+ Add field" offering exactly its fields', async () => {
  const graph = updateNode(createGraph('Service'), 'service-1', { config: { service: 'notify.notify', fields: {} } })
  const { el, node } = mount(graph, 'service-1')
  await pickService(el, 'light.turn_on')
  expect(node().config.service).toBe('light.turn_on')
  // The target picker offers lights only.
  const target = row(el, 'target')!
  expect(target).toBeDefined()
  target.querySelector<HTMLButtonElement>('button[aria-label="Target entities"]')!.click()
  await vi.waitFor(() => expect(el.querySelectorAll('[aria-label="Target entities results"] [role="option"]').length).toBeGreaterThan(0))
  const offered = [...el.querySelectorAll('[aria-label="Target entities results"] [role="option"]')].map(o => o.textContent ?? '')
  expect(offered.some(text => text.includes('light.hall'))).toBe(true)
  expect(offered.some(text => text.includes('climate.living') || text.includes('switch.fan'))).toBe(false)
  el.querySelector('dialog[open]')?.dispatchEvent(new Event('cancel', { cancelable: true }))
  // "+ Add field" offers exactly light.turn_on's fields (none is stored yet), named for people.
  const fields = Object.keys((services as any).items['light.turn_on'].fields)
  const offeredFields = addFieldOptions(el)
  expect(offeredFields).toHaveLength(fields.length)
  expect(offeredFields.some(text => text.startsWith('Brightness'))).toBe(true)
  addField(el, 'Brightness', 'brightness_pct')
  expect(Object.keys(node().config.fields as object)).toEqual(['brightness_pct'])
  expect(addFieldOptions(el)).toHaveLength(fields.length - 1)
  // An optional field can be removed again.
  button(row(el, 'field:brightness_pct')!, 'Remove Brightness')!.click()
  expect(node().config.fields).toEqual({})
})

it('shows climate.set_hvac_mode\'s hvac_mode as a select of the chosen entity\'s state options', async () => {
  const graph = updateNode(createGraph('Climate'), 'service-1', {
    config: { service: 'climate.set_hvac_mode', target: lit('target', { entity_id: ['climate.living'] }), fields: { hvac_mode: { kind: 'unset' } } },
  })
  const { el, node } = mount(graph, 'service-1')
  const select = await vi.waitFor(() => {
    const found = row(el, 'field:hvac_mode')?.querySelector<HTMLSelectElement>('select')
    expect(found).toBeTruthy()
    return found!
  })
  const values = [...select.options].map(o => o.textContent).filter(text => text && !text.endsWith('…') && text !== 'Not set')
  // State options read in words; the raw value is what is stored.
  expect(values).toEqual(['Off', 'Heat', 'Cool'])
  select.value = [...select.options].find(o => o.textContent === 'Heat')!.value
  select.dispatchEvent(new Event('change', { bubbles: true }))
  expect((node().config.fields as any).hvac_mode).toMatchObject({ kind: 'literal', value: 'heat' })
})

it('adds required fields automatically and prunes fields the new service does not have', async () => {
  const graph = updateNode(createGraph('Prune'), 'service-1', {
    config: { service: 'light.turn_on', target: lit('target', { entity_id: 'light.hall' }), fields: { brightness_pct: lit('number', 40), transition: lit('number', 2) } },
  })
  const { el, node } = mount(graph, 'service-1')
  expect(row(el, 'field:brightness_pct')).toBeDefined()
  await pickService(el, 'persistent_notification.create')
  // light.turn_on's fields are gone; the required message is there without asking.
  expect(Object.keys(node().config.fields as object)).toEqual(['message'])
  expect(row(el, 'field:brightness_pct')).toBeUndefined()
  const message = row(el, 'field:message')!
  expect(message).toBeDefined()
  expect(button(message, 'Remove Message')).toBeUndefined()
  // A required field has no "Not set" placeholder: its editor is there.
  expect(message.querySelector('input,textarea')).not.toBeNull()
})

it('keeps typing in a required field the step does not store yet: its first edit keeps the same control and focus', async () => {
  // Saved before message became required: the row is there, not stored.
  const graph = updateNode(createGraph('Late required'), 'service-1', { config: { service: 'persistent_notification.create', fields: {} } })
  const { el, node } = mount(graph, 'service-1')
  const input = row(el, 'field:message')!.querySelector<HTMLInputElement>('input')!
  expect(node().config.fields).toEqual({})
  input.focus()
  input.value = 'Hello'
  input.dispatchEvent(new Event('input', { bubbles: true }))
  input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect((node().config.fields as any).message).toEqual(lit('string', 'Hello'))
  await Promise.resolve()
  // The same control, still focused: typing goes on without another click.
  expect(row(el, 'field:message')!.querySelector('input')).toBe(input)
  expect(input.isConnected).toBe(true)
  expect(document.activeElement).toBe(input)
  input.value = 'Hello there'
  input.dispatchEvent(new Event('input', { bubbles: true }))
  input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect((node().config.fields as any).message).toEqual(lit('string', 'Hello there'))
  expect(el.querySelectorAll('.hs-inspector [data-input-id="field:message"]')).toHaveLength(1)
})

it('offers in "+ Add field" only the fields whose filter the chosen entities meet, as Home Assistant does', async () => {
  const fields = Object.keys((services as any).items['light.turn_on'].fields)
  const offered = (el: Element) => addFieldOptions(el).map(text => fields.find(f => text.endsWith(f))!).sort()
  // An on/off light without transitions: only the fields with no filter (and a stored one stays, whatever its filter).
  const states = { 'light.hall': { attributes: { supported_color_modes: ['onoff'], supported_features: 0 } }, 'light.desk': { attributes: { supported_color_modes: ['brightness'], supported_features: 32 } } }
  const lightOn = (target: unknown, stored: Record<string, unknown> = {}) => updateNode(createGraph('Filters'), 'service-1', { config: { service: 'light.turn_on', target, fields: stored } })
  let { el, node } = mount(lightOn(lit('target', { entity_id: ['light.hall'] }), { color_name: lit('string', 'red') }), 'service-1', { hass: { states } })
  expect(offered(el)).toEqual(['profile'])
  expect(row(el, 'field:color_name')).toBeDefined()
  expect(Object.keys(node().config.fields as object)).toEqual(['color_name'])
  dispose!(); document.body.innerHTML = ''
  // A dimmable light with transitions as well: any target entity meeting a filter is enough.
  ;({ el } = mount(lightOn(lit('target', { entity_id: ['light.hall', 'light.desk'] })), 'service-1', { hass: { states } }))
  expect(offered(el)).toEqual(['brightness', 'brightness_pct', 'brightness_step', 'brightness_step_pct', 'profile', 'transition'])
  dispose!(); document.body.innerHTML = ''
  // No target yet, or one given by area: every field.
  for (const target of [{ kind: 'unset' }, lit('target', { area_id: 'hall' })]) {
    ;({ el } = mount(lightOn(target), 'service-1', { hass: { states } }))
    expect(offered(el)).toHaveLength(fields.length)
    dispose!(); document.body.innerHTML = ''
  }
})

it('decides a field filter from supported features and attributes, and offers a field when it cannot tell', () => {
  const facts = (id: string) => ({ 'fan.a': { features: 16 }, 'fan.b': { features: 1, attributes: { preset_modes: ['auto'] } }, 'fan.c': { features: 1, attributes: { preset_modes: ['eco'] } } } as Record<string, any>)[id]
  const filter = { supportedFeatures: [16], attribute: { preset_modes: ['auto', 'sleep'] } }
  expect(fieldOffered(filter, ['fan.a'], facts)).toBe(true)
  expect(fieldOffered(filter, ['fan.b'], facts)).toBe(true)
  expect(fieldOffered(filter, ['fan.c'], facts)).toBe(false)
  expect(fieldOffered({ supportedFeatures: [16] }, ['fan.c'], facts)).toBe(false)
  // Nothing known of the entity, no entities known, or no filter: offered.
  expect(fieldOffered(filter, ['fan.unknown'], facts)).toBe(true)
  expect(fieldOffered(filter, undefined, facts)).toBe(true)
  expect(fieldOffered(null, ['fan.c'], facts)).toBe(true)
  expect(targetEntities(lit('target', 'fan.a'), false)).toEqual(['fan.a'])
  expect(targetEntities(lit('target', { entity_id: ['fan.a'], area_id: [] }), false)).toEqual(['fan.a'])
  expect(targetEntities(lit('target', { entity_id: ['fan.a'], device_id: 'd1' }), false)).toBeUndefined()
  expect(targetEntities(lit('target', { entity_id: ['fan.a'] }), true)).toBeUndefined()
  expect(targetEntities({ kind: 'unset' }, false)).toBeUndefined()
})

it('reads a missing supported_features as 0 for an entity whose state is known, as Home Assistant does', () => {
  const states = { 'fan.a': { attributes: { friendly_name: 'Fan' } }, 'fan.b': { attributes: { supported_features: 16 } } }
  const entities = [{ entity_id: 'fan.c', supported_features: 8 }]
  expect(filterFacts('fan.a', states, entities)).toEqual({ features: 0, attributes: { friendly_name: 'Fan' } })
  expect(filterFacts('fan.b', states, entities)?.features).toBe(16)
  // No state: the registry's features; neither: nothing known.
  expect(filterFacts('fan.c', states, entities)).toEqual({ features: 8 })
  expect(filterFacts('fan.d', states, entities)).toBeUndefined()
  // So a known entity without the feature does not get the field; an unknown one does.
  const facts = (id: string) => filterFacts(id, states, entities)
  expect(fieldOffered({ supportedFeatures: [16] }, ['fan.a'], facts)).toBe(false)
  expect(fieldOffered({ supportedFeatures: [16] }, ['fan.d'], facts)).toBe(true)
})

it('keeps an unknown service\'s stored fields editable as plain values and says the service is not available', () => {
  const graph = updateNode(createGraph('Gone'), 'service-1', { config: { service: 'gone.away', fields: { level: lit('number', 3) } } })
  const { el, node } = mount(graph, 'service-1')
  expect(el.querySelector('.hs-inspector')!.textContent).toContain('gone.away is not available in this Home Assistant')
  const field = row(el, 'field:level')!.querySelector<HTMLInputElement>('input')!
  expect(field.value).toBe('3')
  type(field, '5')
  expect((node().config.fields as any).level).toEqual(lit('number', 5))
})

it('stores a domain-filtered entity with a type the validator accepts', async () => {
  const graph = updateNode(createGraph('Filtered'), 'service-1', { config: { service: 'hass_sharp_test.selectors', fields: {} } })
  const { el, node, history } = mount(graph, 'service-1')
  addField(el, 'Light')
  const picker = row(el, 'field:light')!.querySelector<HTMLButtonElement>('button[aria-haspopup="dialog"]')!
  picker.click()
  const option = await vi.waitFor(() => {
    const found = [...el.querySelectorAll<HTMLElement>('[role="option"]')].find(o => o.textContent?.includes('light.hall'))
    expect(found).toBeDefined()
    return found!
  })
  option.click()
  const stored = (node().config.fields as any).light
  expect(stored).toMatchObject({ kind: 'literal', value: 'light.hall' })
  expect(validateGraph(history.graph(), catalog).filter(d => d.nodeId === 'service-1')).toEqual([])
})

const responseSwitch = (el: Element) =>
  [...el.querySelectorAll<HTMLLabelElement>('.hs-inspector .hs-insp-toggle')].find(l => l.textContent?.includes('Return response'))?.querySelector('input')

it('"Return response": locked on for a service that only answers, a switch for an optional one, absent otherwise', async () => {
  const graph = updateNode(createGraph('Response'), 'service-1', { config: { service: 'light.turn_on', target: lit('target', { entity_id: 'light.hall' }), fields: {} } })
  const { el, node } = mount(graph, 'service-1')
  const toggle = () => responseSwitch(el)
  expect(toggle()).toBeUndefined()
  await pickService(el, 'todo.get_items')
  expect(toggle()!.checked).toBe(true)
  expect(toggle()!.disabled).toBe(true)
  expect(node().config.returnResponse).toBe(true)
  await pickService(el, 'hass_sharp_test.respond_optionally')
  expect(toggle()!.disabled).toBe(false)
  const before = toggle()!.checked
  toggle()!.click()
  expect(node().config.returnResponse).toBe(!before)
  expect(toggle()!.checked).toBe(!before)
})

it('"Return response" of a service that only answers, stored off, shows off and can be turned on', () => {
  const graph = updateNode(createGraph('Response'), 'service-1', { config: { service: 'todo.get_items', target: lit('target', { entity_id: 'todo.list' }), fields: {} } })
  // The validator's own message, as both validators word it.
  const message = 'Get to-do list items (todo.get_items) only returns a response; turn on Return response'
  expect(validateGraph(graph, catalog).map(d => d.message)).toContain(message)
  const diagnostics: VisualDiagnostic[] = [{ nodeId: 'service-1', message, severity: 3, inputId: 'returnResponse' } as VisualDiagnostic]
  const { el, node } = mount(graph, 'service-1', { diagnostics })
  const toggle = responseSwitch(el)!
  expect(node().config.returnResponse).toBeUndefined()
  expect(toggle.checked).toBe(false)
  expect(toggle.disabled).toBe(false)
  // The validator's message is under the switch, and its callout focuses it.
  expect(row(el, 'returnResponse')!.textContent).toContain(message)
  // Its hint says the switch must be on, not that the Response output is always there.
  expect(row(el, 'returnResponse')!.textContent).toContain('Return response must be on for this service.')
  expect(row(el, 'returnResponse')!.textContent).not.toContain('always there')
  button(el.querySelector('.hs-insp-problems')!, message)!.click()
  expect(document.activeElement).toBe(toggle)
  toggle.click()
  expect(node().config.returnResponse).toBe(true)
  expect(responseSwitch(el)).toBe(toggle)
  expect(row(el, 'returnResponse')!.textContent).toContain('always there')
  expect(toggle.checked).toBe(true)
  expect(toggle.disabled).toBe(true)
})

it('shows messages about the service and its fields under the Service button and "+ Add field", and focuses them', () => {
  const graph = updateNode(createGraph('Messages'), 'service-1', { config: { service: 'light.turn_on', target: lit('target', { entity_id: 'light.hall' }), fields: {} } })
  const diagnostics = [
    { nodeId: 'service-1', message: 'Service problem', severity: 2, inputId: 'service' },
    { nodeId: 'service-1', message: 'Fields problem', severity: 3, inputId: 'fields' },
  ] as VisualDiagnostic[]
  const { el } = mount(graph, 'service-1', { diagnostics })
  expect(row(el, 'service')!.querySelector('.hs-input-editor__errors')!.textContent).toContain('Service problem')
  expect(row(el, 'fields')!.querySelector('.hs-input-editor__errors')!.textContent).toContain('Fields problem')
  const problems = el.querySelector('.hs-insp-problems')!
  button(problems, 'Service problem')!.click()
  expect(document.activeElement).toBe(row(el, 'service')!.querySelector('button[aria-label="Service"]'))
  button(problems, 'Fields problem')!.click()
  expect(document.activeElement).toBe(row(el, 'fields')!.querySelector('input[aria-label="Add field"]'))
})

it('keeps values edited in the panel and on the node card in sync', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  const graph = addNode(createGraph('Sync'), 'delay', 'wait')
  const { el, node } = mount(graph, 'wait', { canvas: true })
  const panel = () => el.querySelector<HTMLInputElement>('.hs-inspector input[aria-label="Seconds"]')!
  const card = () => el.querySelector<HTMLInputElement>('[data-id="wait"] .hs-card-editor input[aria-label="Seconds"]')!
  type(panel(), '75')
  expect(node().config.seconds).toEqual(lit('number', 75))
  await vi.waitFor(() => expect(card().value).toBe('75'))
  card().value = '30'
  card().dispatchEvent(new Event('input', { bubbles: true }))
  card().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(node().config.seconds).toEqual(lit('number', 30))
  await vi.waitFor(() => expect(panel().value).toBe('30'))
})

/** No control of the inspector makes, changes or removes a wire. */
function expectNoWireControls(el: Element) {
  const inspector = el.querySelector('.hs-inspector')!
  const names = [...inspector.querySelectorAll('button, select, [role="combobox"], [role="option"]')].map(c => `${c.getAttribute('aria-label') ?? ''} ${c.textContent ?? ''}`)
  for (const name of names) expect(name).not.toMatch(/connect|disconnect|unlink|destination|\bsource\b/i)
  expect(inspector.textContent).not.toMatch(/Next destination|Execution|Connect /)
}
it('has no control that connects or disconnects, for a node with execution branches or with data inputs', () => {
  let graph = addNode(createGraph('Wires'), 'constant', 'temperature')
  graph = updateNode(graph, 'temperature', { config: { type: 'number', value: lit('number', 20) } })
  graph = addNode(graph, 'compare', 'compare')
  graph = connectData(graph, 'temperature', 'value', 'compare', 'left')
  const { el, setSelected } = mount(graph, 'condition-1')
  expect(el.querySelector('.hs-inspector .hs-insp-node')).not.toBeNull()
  expectNoWireControls(el)
  setSelected('compare')
  expect(row(el, 'left')).toBeDefined()
  expectNoWireControls(el)
})

it('shows a connected input as a "From" pill without an editor, and brings the editor back with the stored value when the wire is deleted on the canvas', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  let graph = addNode(createGraph('Pill'), 'constant', 'temperature')
  graph = updateNode(graph, 'temperature', { config: { type: 'number', value: lit('number', 20) } })
  graph = addNode(graph, 'compare', 'compare')
  graph = connectData(graph, 'temperature', 'value', 'compare', 'left')
  const { el, history, selected } = mount(graph, 'compare', { canvas: true })
  const left = () => row(el, 'left')!
  const pill = left().querySelector<HTMLButtonElement>('.hs-insp-pill')!
  expect(pill.textContent).toBe('From Constant · Value')
  expect(left().querySelector('input,select,textarea')).toBeNull()
  // The pill selects its source.
  pill.click()
  expect(selected()).toBe('temperature')
  // Back on the compare step, delete the wire on the canvas.
  el.querySelector('[data-id="compare"]')!.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
  await vi.waitFor(() => expect(selected()).toBe('compare'))
  const wire = history.graph().edges.find(e => e.kind === 'data' && e.target === 'compare')!
  const edge = await vi.waitFor(() => {
    const found = [...el.querySelectorAll<HTMLElement>(".solid-flow__edge")].find(e => e.getAttribute("data-id") === wire.id)
    expect(found).toBeDefined()
    return found!
  })
  edge.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
  edge.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true, cancelable: true }))
  expect(history.graph().edges.some(e => e.id === wire.id)).toBe(false)
  el.querySelector('[data-id="compare"]')!.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
  // The editor is back, showing what the step now stores (removing a wire leaves its input unset).
  expect(history.graph().nodes.find(n => n.id === 'compare')!.config.left).toEqual({ kind: 'unset' })
  const editor = await vi.waitFor(() => { const found = left()?.querySelector<HTMLInputElement>('input'); expect(found).toBeTruthy(); return found! })
  expect(editor.value).toBe('')
  type(editor, '7')
  expect(history.graph().nodes.find(n => n.id === 'compare')!.config.left).toMatchObject({ kind: 'literal', value: '7' })
  expect(left().querySelector('.hs-insp-pill')).toBeNull()
})

it('shows problems as callouts that focus their field, and names types in words with a fallback for unresolvable ones', async () => {
  const graph = addNode(createGraph('Problems'), 'compare', 'compare')
  const diagnostics: VisualDiagnostic[] = [{ nodeId: 'compare', inputId: 'right', message: 'Second value is required', severity: 3, startLine: null, startColumn: null }]
  const { el } = mount(graph, 'compare', { diagnostics })
  const callout = button(el.querySelector('.hs-insp-problems')!, 'Second value is required')!
  expect(callout).toBeDefined()
  callout.click()
  expect(row(el, 'right')!.contains(document.activeElement)).toBe(true)
  // Validator messages appear as they are under their field.
  expect(row(el, 'right')!.textContent).toContain('Second value is required')
  const chips = [...el.querySelectorAll('.hs-inspector .hs-type-chip')].map(c => c.textContent)
  expect(chips.length).toBeGreaterThan(0)
  for (const chip of chips) expect(chip).not.toMatch(/Boolean|String|System\.|global::/)
})

it('falls back to the declared types when an input\'s type cannot be resolved', () => {
  const odd = structuredClone(nodeCatalog) as VisualCatalogSnapshot
  odd.nodes.odd_step = { ...structuredClone(odd.nodes.delay), label: 'Odd step', inputs: { mystery: { types: ['unknown', 'number'], required: false, description: '' } } } as any
  const graph = addNode(createGraph('Odd'), 'odd_step', 'odd', odd)
  const { el } = mount(graph, 'odd', { catalog: odd })
  const chip = row(el, 'mystery')?.querySelector('.hs-type-chip')
  expect(chip?.querySelector('.hs-type-chip__name')?.textContent).toBe('Number')
  // Only the type it can name has a C# type: none is guessed for the unknown one.
  expect(chip?.querySelector('.hs-csharp-type')?.textContent).toBe('double')
})

it('names each C# type of an input that takes several, and the ? of a nullable one', () => {
  const odd = structuredClone(nodeCatalog) as VisualCatalogSnapshot
  odd.types['global::System.Int32?'] = { description: 'int?', label: 'int?', nullable: true, literalKind: 'integer', category: 'Number' }
  odd.nodes.odd_step = { ...structuredClone(odd.nodes.delay), label: 'Odd step', inputs: {
    either: { types: ['number', 'string', 'entity_id'], required: false, description: '' },
    maybe: { types: ['global::System.Int32?'], required: false, description: '' },
  } } as any
  const graph = addNode(createGraph('Odd'), 'odd_step', 'odd', odd)
  const { el } = mount(graph, 'odd', { catalog: odd })
  const either = row(el, 'either')!.querySelector('.hs-type-chip')!
  expect(either.querySelector('.hs-csharp-type')?.textContent).toBe('double or string')
  expect(either.textContent).toContain(', C# types double or string')
  expect(row(el, 'maybe')!.querySelector('.hs-csharp-type')?.textContent).toBe('int?')
})

it('edits the title, offers Duplicate, Copy node ID and Delete, and never shows the node id', async () => {
  const writeText = vi.fn(async () => {})
  Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
  const { el, history, node } = mount(addNode(createGraph('Head'), 'delay', 'wait-1234'), 'wait-1234')
  const title = el.querySelector<HTMLInputElement>('.hs-inspector input[aria-label="Node label"]')!
  expect(title.placeholder).toBe('Delay')
  title.value = 'Warm up'
  title.dispatchEvent(new Event('input', { bubbles: true }))
  expect(node().label).toBe('Warm up')
  expect(el.querySelector('.hs-inspector')!.textContent).not.toContain('wait-1234')
  button(el, 'Node actions')!.click()
  button(el, 'Copy node ID')!.click()
  expect(writeText).toHaveBeenCalledWith('wait-1234')
  const count = history.graph().nodes.length
  button(el, 'Node actions')!.click()
  button(el, 'Duplicate')!.click()
  expect(history.graph().nodes).toHaveLength(count + 1)
  button(el, 'Node actions')!.click()
  button(el, 'Delete')!.click()
  expect(history.graph().nodes).toHaveLength(count)
})

it('shows a Custom C# step\'s pins, title and description from its signature, and an Edit code button instead of its source', () => {
  const source = 'class CustomNode { [Node("Brightness")] public static int Run(int level) => level; }'
  const signature = {
    title: 'Brightness', description: 'Scales a level.', pure: false,
    inputs: { level: { types: ['number'], required: true, label: 'Level', description: 'How bright.', nullable: false, hasDefaultValue: false } },
    outputs: { result: { type: 'number', label: 'Result', description: 'The scaled level.' } },
    types: {},
  }
  const graph = updateNode(addNode(createGraph('Code'), 'custom', 'code'), 'code', { config: { source, signature, arguments: { level: lit('number', 3) } } })
  const onEditCode = vi.fn()
  const { el } = mount(graph, 'code', { onEditCode })
  const inspector = el.querySelector('.hs-inspector')!
  expect(inspector.querySelector<HTMLInputElement>('input[aria-label="Node label"]')!.placeholder).toBe('Brightness')
  expect(inspector.querySelector('.hs-insp-desc')!.textContent).toBe('Scales a level.')
  expect(inspector.querySelector('pre.hs-insp-source')).toBeNull()
  expect(inspector.textContent).not.toContain('Editing arrives')
  expect(inspector.textContent).not.toContain(source)
  button(inspector, 'Edit code')!.click()
  expect(onEditCode).toHaveBeenCalledWith('code')
  expect(row(el, 'level')!.textContent).toContain('Level')
  for (const removed of ['Usings', 'Helper members', 'Expose to C#', 'Shared C#'])
    expect(inspector.textContent, removed).not.toContain(removed)
  expect(inspector.querySelector('textarea[aria-label="C# code"]')).toBeNull()
})

it('shows the selection count with Duplicate, Tidy selection and Delete, and a selected wire read only', () => {
  const graph = createGraph('Many')
  const el = host()
  const history = createGraphHistory(graph)
  const [ids, setIds] = createSignal<string[]>(['condition-1', 'service-1'])
  const [edge, setEdge] = createSignal<string>()
  client = new QueryClient()
  dispose = render(() => (
    <QueryClientProvider client={client!}>
      <HassCtx.Provider value={{ callWS: async () => registry } as any}>
        <Inspector graph={history.graph()} catalog={catalog} diagnostics={[]} commit={history.commit} selectedIds={ids()} selectedEdgeId={edge()} onSelect={() => {}} />
      </HassCtx.Provider>
    </QueryClientProvider>
  ), el)
  expect(el.textContent).toContain('2 steps selected')
  button(el, 'Duplicate')!.click()
  expect(history.graph().nodes).toHaveLength(5)
  button(el, 'Delete')!.click()
  expect(history.graph().nodes.map(n => n.id).filter(id => id === 'condition-1' || id === 'service-1')).toEqual([])
  history.undo(); history.undo()
  setIds([])
  setEdge(history.graph().edges.find(e => e.source === 'condition-1')!.id)
  const wire = el.querySelector('.hs-insp-wire')!
  expect(wire.textContent).toContain('State condition')
  expect(wire.textContent).toContain('If true')
  expect(wire.textContent).toContain('Call service')
  expect(wire.querySelector('select, input')).toBeNull()
  expectNoWireControls(el)
  setEdge(undefined)
  // Nothing selected: the triggers as summary lines and the next step.
  expect(el.querySelector('.hs-insp-overview')!.textContent).toContain('Select a step to edit it.')
  expect(el.querySelectorAll('.hs-insp-trigger-line')).toHaveLength(1)
})

it('categorizes every catalog node and groups and filters the quick-add palette', () => {
  const root = host()
  let chosen = ''
  dispose = render(() => <QuickAdd x={0} y={0} title="Add a step" allowed={() => true} onClose={() => {}} onChoose={kind => (chosen = kind)} />, root)
  expect(new Set(catalogPalette().map(item => item.category))).toEqual(new Set(catalogCategories()))
  const groups = [...root.querySelectorAll('[role="group"]')]
  // Headings use the plain category names ("Home Assistant", not the catalog's "Hass").
  const plain: Record<string, string> = { Hass: 'Home Assistant' }
  expect(groups.map(group => group.querySelector('.hs-quick-add__category')!.textContent))
    .toEqual(catalogCategories().filter(c => catalogPalette().some(n => n.category === c)).map(c => plain[c] ?? c))
  expect(groups[0].querySelector('.hs-quick-add__category')!.textContent).toBe('Home Assistant')
  const logic = groups.find(group => group.querySelector('.hs-quick-add__category')!.textContent === 'Logic')!
  expect(logic.querySelectorAll('[role="option"]')).toHaveLength(catalogPalette().filter(n => n.category === 'Logic').length)
  const search = root.querySelector<HTMLInputElement>('[aria-label="Search steps"]')!
  // A step is also found by its kind id, the short name people may know it by.
  for (const [query, label] of [['xor', 'Exclusive or'], ['foreach', 'For each']]) {
    search.value = query
    search.dispatchEvent(new Event('input', { bubbles: true }))
    const options = [...root.querySelectorAll('[role="option"]')]
    expect(options).toHaveLength(1)
    expect(options[0].textContent).toContain(label)
  }
  // And by the other names people know it by, the catalog's keywords.
  for (const [query, label] of [['constant', 'Constant'], ['set value', 'Constant'], ['literal', 'Constant'], ['greater', 'Compare'], ['property', 'Get field']]) {
    search.value = query
    search.dispatchEvent(new Event('input', { bubbles: true }))
    expect([...root.querySelectorAll('[role="option"]')].map(o => o.textContent).join(' | '), query).toContain(label)
  }
  search.value = 'Exclusive or'
  search.dispatchEvent(new Event('input', { bubbles: true }))
  expect(root.querySelectorAll('[role="option"]')).toHaveLength(1)
  search.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  expect(chosen).toBe('xor')
  search.value = 'no such step anywhere'
  search.dispatchEvent(new Event('input', { bubbles: true }))
  expect(root.textContent).toContain('No step matches that search.')
})

// ── The panel survives edits of the selected step (fix round 1, item 1) ──────────────────────────────

const inspector = (el: Element) => el.querySelector('.hs-inspector')!
const titleOf = (el: Element) => inspector(el).querySelector<HTMLInputElement>('input[aria-label="Node label"]')!
const typeKeys = (field: HTMLInputElement, text: string) => {
  for (const ch of text) {
    field.value += ch
    field.dispatchEvent(new Event('input', { bubbles: true }))
  }
}
/** Waits until focus is on `target`, so a test fails on focus that fell to the page. */
const focused = (target: () => Element | null | undefined) => vi.waitFor(() => {
  expect(document.activeElement).not.toBe(document.body)
  expect(document.activeElement).toBe(target())
})

it('keeps the same title input focused while typing a whole word, keeps Advanced open, and makes one undo step', () => {
  const { el, history, node } = mount(addNode(createGraph('Typing'), 'delay', 'wait'), 'wait')
  const title = titleOf(el)
  const before = node().label
  title.value = ''
  const advanced = inspector(el).querySelector<HTMLDetailsElement>('.hs-insp-advanced')!
  advanced.open = true
  title.focus()
  typeKeys(title, 'Wa')
  expect(node().label).toBe('Wa')
  expect(titleOf(el)).toBe(title)
  expect(document.activeElement).toBe(title)
  expect(inspector(el).querySelector('.hs-insp-advanced')).toBe(advanced)
  expect(advanced.open).toBe(true)
  typeKeys(title, 'rm up')
  expect(node().label).toBe('Warm up')
  expect(document.activeElement).toBe(title)
  // The whole word is one undo step.
  history.undo()
  expect(node().label).toBe(before)
})

it('lets a slider be dragged through several input events on one element', async () => {
  const graph = updateNode(createGraph('Slider'), 'service-1', {
    config: { service: 'light.turn_on', target: lit('target', { entity_id: 'light.hall' }), fields: { brightness_pct: lit('number', 10) } },
  })
  const { el, history, node } = mount(graph, 'service-1')
  const slider = row(el, 'field:brightness_pct')!.querySelector<HTMLInputElement>('input[type="range"]')!
  expect(slider).not.toBeNull()
  slider.focus()
  for (const value of [20, 35, 50]) {
    slider.value = String(value)
    slider.dispatchEvent(new Event('input', { bubbles: true }))
    expect((node().config.fields as any).brightness_pct).toMatchObject({ kind: 'literal', value })
    expect(row(el, 'field:brightness_pct')!.querySelector('input[type="range"]')).toBe(slider)
    expect(document.activeElement).toBe(slider)
  }
  slider.dispatchEvent(new Event('change', { bubbles: true }))
  expect((node().config.fields as any).brightness_pct).toMatchObject({ value: 50 })
  history.undo()
  expect((node().config.fields as any).brightness_pct).toMatchObject({ value: 10 })
})

it('keeps focus on a select after changing it, and keeps config keys it does not know', () => {
  const graph = updateNode(addNode(createGraph('Select'), 'compare', 'compare'), 'compare', {})
  const withExtra = { ...graph, nodes: graph.nodes.map(n => (n.id === 'compare' ? { ...n, config: { ...n.config, mystery: { kept: true } } } : n)) }
  const { el, node } = mount(withExtra, 'compare')
  const select = row(el, 'operator')!.querySelector<HTMLSelectElement>('select')!
  select.focus()
  select.value = 'ne'
  select.dispatchEvent(new Event('change', { bubbles: true }))
  expect(node().config.operator).toBe('ne')
  expect(row(el, 'operator')!.querySelector('select')).toBe(select)
  expect(document.activeElement).toBe(select)
  expect(node().config.mystery).toEqual({ kept: true })
})

it('subscribes to the trigger platforms once across edits of the trigger', async () => {
  const subscribeMessage = vi.fn(async () => () => {})
  const { el, node } = mount(createGraph('Subscribe'), 'trigger-1', { hass: { connection: { subscribeMessage } } })
  await vi.waitFor(() => expect(subscribeMessage).toHaveBeenCalledTimes(1))
  const title = titleOf(el)
  title.value = ''
  title.focus()
  typeKeys(title, 'Go')
  button(inspector(el), 'Start over')?.click()
  typeKeys(title, '!')
  expect(node().label).toBe('Go!')
  await new Promise(resolve => setTimeout(resolve, 0))
  expect(subscribeMessage).toHaveBeenCalledTimes(1)
})

// ── Focus never falls to the page (item 4) ───────────────────────────────────────────────────────────

const fieldRows = (el: Element) => [...inspector(el).querySelectorAll<HTMLElement>('.hs-insp-input[data-input-id^="field:"]')]

it('after removing a service field, focuses the next row', async () => {
  const graph = updateNode(createGraph('Next'), 'service-1', {
    config: { service: 'light.turn_on', target: lit('target', { entity_id: 'light.hall' }), fields: { brightness_pct: lit('number', 10), transition: lit('number', 2) } },
  })
  const { el } = mount(graph, 'service-1')
  const [first, second] = fieldRows(el)
  first.querySelector<HTMLButtonElement>('.hs-insp-remove')!.click()
  await vi.waitFor(() => expect(second.contains(document.activeElement)).toBe(true))
})

it('after removing the last service field, focuses the previous row', async () => {
  const graph = updateNode(createGraph('Previous'), 'service-1', {
    config: { service: 'light.turn_on', target: lit('target', { entity_id: 'light.hall' }), fields: { brightness_pct: lit('number', 10), transition: lit('number', 2) } },
  })
  const { el } = mount(graph, 'service-1')
  const [first, second] = fieldRows(el)
  second.querySelector<HTMLButtonElement>('.hs-insp-remove')!.click()
  await vi.waitFor(() => expect(first.contains(document.activeElement)).toBe(true))
})

it('after removing the only service field, focuses "+ Add field"', async () => {
  const graph = updateNode(createGraph('Add'), 'service-1', { config: { service: 'hass_sharp_test.odd_texts', fields: { odd_label: lit('string', 'x') } } })
  const { el } = mount(graph, 'service-1')
  fieldRows(el)[0].querySelector<HTMLButtonElement>('.hs-insp-remove')!.click()
  await focused(() => inspector(el).querySelector('input[aria-label="Add field"]'))
})

it('after removing a field with no row or "+ Add field" left, focuses the title', async () => {
  // A service with no target and no fields of its own, holding a field it does not know.
  const graph = updateNode(createGraph('Title'), 'service-1', { config: { service: 'hass_sharp_test.respond_optionally', fields: { stray: lit('string', 'x') } } })
  const { el } = mount(graph, 'service-1')
  fieldRows(el)[0].querySelector<HTMLButtonElement>('.hs-insp-remove')!.click()
  await focused(() => titleOf(el))
})

it('after removing a branch, focuses the next branch, else the add button', async () => {
  const graph = updateNode(addNode(createGraph('Branches'), 'parallel', 'split'), 'split', { config: { branches: ['a', 'b', 'c'] } })
  const { el, node } = mount(graph, 'split')
  const list = () => [...row(el, 'branches')!.querySelectorAll('li')]
  const second = list()[1]
  list()[0].querySelector<HTMLButtonElement>('.hs-insp-remove')!.click()
  expect(node().config.branches).toEqual(['b', 'c'])
  await focused(() => second.querySelector('.hs-insp-remove'))
  // The one branch that would be left cannot be removed, so after removing the last, focus goes to the add button.
  list()[1].querySelector<HTMLButtonElement>('.hs-insp-remove')!.click()
  expect(node().config.branches).toEqual(['b'])
  await focused(() => button(row(el, 'branches')!, '+ Add branch'))
})

it('after removing the only object field, focuses the field name input', async () => {
  const graph = updateNode(addNode(createGraph('Object'), 'object_create', 'obj'), 'obj', { config: { fields: { name: { kind: 'unset' } } } })
  const { el, node } = mount(graph, 'obj')
  button(row(el, 'fields')!, 'Remove field name')!.click()
  expect(node().config.fields).toEqual({})
  await focused(() => row(el, 'fields')!.querySelector('input[aria-label="Add a field named"]'))
})

it('after removing the last array item, focuses the item before it, else the add button', async () => {
  const graph = updateNode(addNode(createGraph('Array'), 'array_create', 'arr'), 'arr', { config: { items: [lit('number', 1), lit('number', 2)] } })
  const { el, node } = mount(graph, 'arr')
  button(row(el, 'items')!, 'Remove last')!.click()
  expect(node().config.items).toHaveLength(1)
  await vi.waitFor(() => expect(row(el, 'item:0')!.contains(document.activeElement)).toBe(true))
  button(row(el, 'items')!, 'Remove last')!.click()
  await focused(() => button(row(el, 'items')!, '+ Add item'))
})

it('after removing a loop declaration, focuses the next one, else the name input', async () => {
  const graph = updateNode(addNode(createGraph('Loop'), 'foreach', 'loop'), 'loop', {
    config: { itemType: 'number', carry: [{ name: 'total', type: 'number', initial: lit('number', 0) }, { name: 'count', type: 'number', initial: lit('number', 0) }] },
  })
  const { el, node } = mount(graph, 'loop')
  const section = () => row(el, 'carry')!
  const rows = () => [...section().querySelectorAll<HTMLElement>('.hs-insp-declaration')]
  const second = rows()[1]
  button(section(), 'Remove total')!.click()
  expect((node().config.carry as any[]).map(c => c.name)).toEqual(['count'])
  expect(rows()[0]).toBe(second)
  await vi.waitFor(() => expect(second.contains(document.activeElement)).toBe(true))
  button(section(), 'Remove count')!.click()
  await focused(() => section().querySelector('input[aria-label="New kept between iterations name"]'))
})

it('shows two loop declarations of the same name as two rows, each edited and removed by itself', () => {
  // Possible in a graph written through the API; the validator reports the duplicate.
  const graph = updateNode(addNode(createGraph('Loop'), 'foreach', 'loop'), 'loop', {
    config: { itemType: 'number', carry: [{ name: 'total', type: 'number', initial: lit('number', 1) }, { name: 'total', type: 'number', initial: lit('number', 2) }] },
  })
  const { el, node } = mount(graph, 'loop')
  const rows = () => [...row(el, 'carry')!.querySelectorAll<HTMLElement>('.hs-insp-declaration')]
  expect(rows()).toHaveLength(2)
  type(rows()[1].querySelector<HTMLInputElement>('input')!, '7')
  expect((node().config.carry as any[]).map(c => c.initial.value)).toEqual([1, 7])
  rows()[0].querySelector<HTMLButtonElement>('button[aria-label="Remove total"]')!.click()
  expect((node().config.carry as any[]).map(c => [c.name, c.initial.value])).toEqual([['total', 7]])
  expect(rows()).toHaveLength(1)
})

it('keeps a loop declaration\'s row and focus while its starting value is edited', () => {
  const graph = updateNode(addNode(createGraph('Loop'), 'foreach', 'loop'), 'loop', {
    config: { itemType: 'number', carry: [{ name: 'total', type: 'number', initial: lit('number', 0) }] },
  })
  const { el, node } = mount(graph, 'loop')
  const declaration = row(el, 'carry')!.querySelector('.hs-insp-declaration')!
  const field = declaration.querySelector<HTMLInputElement>('input')!
  field.focus()
  type(field, '5')
  expect((node().config.carry as any[])[0].initial).toMatchObject({ value: 5 })
  expect(row(el, 'carry')!.querySelector('.hs-insp-declaration')).toBe(declaration)
})

it('after Delete in the "⋯" menu, focuses the canvas; after Duplicate, the copy\'s title', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  const { el, history, selected } = mount(addNode(createGraph('Menu'), 'delay', 'wait'), 'wait', { canvas: true })
  button(el, 'Node actions')!.click()
  button(el, 'Duplicate')!.click()
  const copy = history.graph().nodes.at(-1)!.id
  expect(copy).not.toBe('wait')
  await vi.waitFor(() => expect(selected()).toBe(copy))
  await focused(() => [...inspector(el).querySelectorAll<HTMLElement>('.hs-insp-node')].find(n => n.dataset.nodeId === copy)?.querySelector('input[aria-label="Node label"]'))
  button(el, 'Node actions')!.click()
  button(el, 'Delete')!.click()
  expect(history.graph().nodes.some(n => n.id === copy)).toBe(false)
  await focused(() => el.querySelector('.hs-flow'))
})

it('after Delete in the several-steps view, focuses the canvas', async () => {
  const el = host()
  const history = createGraphHistory(createGraph('Many'))
  client = new QueryClient()
  dispose = render(() => (
    <QueryClientProvider client={client!}>
      <HassCtx.Provider value={{ callWS: async () => registry } as any}>
        <div class="hs-visual-workspace">
          <div class="hs-flow" tabindex="-1" aria-label="Automation canvas" />
          <Inspector graph={history.graph()} catalog={catalog} diagnostics={[]} commit={history.commit} selectedIds={['condition-1', 'service-1']} onSelect={() => {}} />
        </div>
      </HassCtx.Provider>
    </QueryClientProvider>
  ), el)
  button(el, 'Delete')!.click()
  await focused(() => el.querySelector('.hs-flow'))
})

it('a "From …" pill and a wire end select their step and focus its title', async () => {
  let graph = addNode(createGraph('Pills'), 'constant', 'temperature')
  graph = updateNode(graph, 'temperature', { config: { type: 'number', value: lit('number', 20) } })
  graph = addNode(graph, 'compare', 'compare')
  graph = connectData(graph, 'temperature', 'value', 'compare', 'left')
  const { el, selected } = mount(graph, 'compare')
  row(el, 'left')!.querySelector<HTMLButtonElement>('.hs-insp-pill')!.click()
  expect(selected()).toBe('temperature')
  await focused(() => titleOf(el))
  expect(inspector(el).querySelector<HTMLElement>('.hs-insp-node')!.dataset.nodeId).toBe('temperature')
  dispose?.()
  document.body.innerHTML = ''
  const wire = graph.edges.find(e => e.kind === 'data' && e.target === 'compare')!
  const second = mount(graph, undefined, { edge: wire.id })
  const ends = second.el.querySelectorAll<HTMLButtonElement>('.hs-insp-wire__end')
  ends[1].click()
  expect(second.selected()).toBe('compare')
  await focused(() => titleOf(second.el))
})

it('closing the sheet focuses the selected step\'s card and leaves its peek bar, which opens it again', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  const { el } = mount(addNode(createGraph('Sheet'), 'delay', 'wait'), 'wait', { canvas: true })
  const aside = inspector(el)
  const card = () => [...el.querySelectorAll<HTMLElement>('.solid-flow__node')].find(n => n.getAttribute('data-id') === 'wait')
  await vi.waitFor(() => expect(card()).toBeDefined())
  expect(aside.getAttribute('data-open')).toBe('true')
  button(aside, 'Close panel')!.click()
  expect(aside.getAttribute('data-open')).toBe('false')
  await focused(card)
  // Keys and clicks on the card leave it closed: the editor page decides when it opens (Enter on the card itself).
  card()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  card()!.click()
  expect(aside.getAttribute('data-open')).toBe('false')
  // Nor do keys and clicks in an input of the card's own editor (carried item A: no listener reopens the panel).
  const inline = card()!.querySelector<HTMLInputElement>('.hs-card-editor input')!
  expect(inline).not.toBeNull()
  for (const key of [' ', 'Enter']) inline.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
  inline.click()
  await Promise.resolve()
  expect(aside.getAttribute('data-open')).toBe('false')
  expect(aside.getAttribute('data-peek')).toBe('true')
  button(aside, 'Edit Delay')!.click()
  expect(aside.getAttribute('data-open')).toBe('true')
  expect(aside.getAttribute('data-peek')).toBeNull()
})

// ── Smaller items (items 7 and 10) ───────────────────────────────────────────────────────────────────

it('groups "+ Add field" by section, with Advanced last', () => {
  const graph = updateNode(createGraph('Sections'), 'service-1', { config: { service: 'hass_sharp_test.sectioned', fields: { level: lit('number', 1) } } })
  const { el } = mount(graph, 'service-1')
  const field = inspector(el).querySelector<HTMLInputElement>('input[aria-label="Add field"]')!
  field.dispatchEvent(new FocusEvent('focus'))
  field.value = ''
  field.dispatchEvent(new Event('input', { bubbles: true }))
  const groups = [...el.querySelectorAll('[aria-label="Add field options"] .hs-search-group')].map(g => g.textContent)
  expect(groups).toEqual(['Extras', 'Advanced'])
})

it('shows the entity picker for a required entity input', () => {
  const { el } = mount(createGraph('Entity'), 'condition-1')
  const entity = row(el, 'entity')!
  expect(entity).toBeDefined()
  expect(entity.querySelector('button[aria-haspopup="dialog"]')).not.toBeNull()
  expect(entity.textContent).not.toContain('Not set')
})

it('describes a setting by its hint only when the hint is shown, and does not make outputs tab stops', () => {
  const { el } = mount(addNode(createGraph('Hints'), 'compare', 'compare'), 'compare')
  for (const control of inspector(el).querySelectorAll('[aria-describedby]')) {
    for (const id of control.getAttribute('aria-describedby')!.split(' ')) expect(document.getElementById(id), id).not.toBeNull()
  }
  expect(inspector(el).querySelectorAll('.hs-insp-output[tabindex]')).toHaveLength(0)
})

it('reads a setting\'s decimal comma as a point, and refuses a whole number written with a fraction or two separators', () => {
  const graph = addNode(createGraph('Loops'), 'repeat', 'loop')
  const { el, node } = mount(graph, 'loop')
  const field = () => row(el, 'maxIterations')!.querySelector<HTMLInputElement>('input')!
  // The field's own message, not its aria-invalid (whose "false" would pass for one).
  const error = () => row(el, 'maxIterations')!.querySelector('.hs-error')?.textContent
  type(field(), '12,0')
  expect(node().config.maxIterations).toBe(12)
  type(field(), '7')
  expect(node().config.maxIterations).toBe(7)
  expect(error()).toBeUndefined()
  for (const text of ['1,5', '1,2,3', '1.2,3']) {
    type(field(), text)
    expect(node().config.maxIterations, text).toBe(7)
    expect(error(), text).toBe('Enter a whole number.')
  }
  // A comma before exactly three digits, with the number format unknown: ambiguous, and refused with the reason.
  type(field(), '1,000')
  expect(node().config.maxIterations).toBe(7)
  expect(error()).toBe('Enter a whole number. Use a point for decimals.')
})

it('refuses a setting\'s comma where the person\'s number format writes a point, and takes it where it writes a comma', () => {
  const graph = addNode(createGraph('Loops'), 'repeat', 'loop')
  let mounted = mount(graph, 'loop', { hass: { locale: { language: 'en', number_format: 'comma_decimal' } } })
  const field = () => row(mounted.el, 'maxIterations')!.querySelector<HTMLInputElement>('input')!
  const error = () => row(mounted.el, 'maxIterations')!.querySelector('.hs-error')?.textContent
  type(field(), '1,000')
  expect(mounted.node().config.maxIterations).not.toBe(1)
  expect(error()).toBe('Enter a whole number. Use a point for decimals.')
  dispose?.(); document.body.innerHTML = ''
  mounted = mount(graph, 'loop', { hass: { locale: { language: 'en', number_format: 'decimal_comma' } } })
  type(field(), '12,0')
  expect(mounted.node().config.maxIterations).toBe(12)
  expect(error()).toBeUndefined()
})
