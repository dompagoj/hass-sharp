import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { A, MemoryRouter, Route, createMemoryHistory } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { EntityDefinitionView } from '../src/views/EntityDefinitionView'
import { EntityRunTraceView } from '../src/views/RunTraceView'
import { nodeCatalog } from '../src/visual/contract'
import { newGraphDefinition, type DefinitionBody } from '../src/entities/model'
import { visualFixture } from './visual-fixture'
import { icons } from '../src/visual/icons'
import { dropStep, libraryItem } from './library'

// The entity definition editor in the panel, against the in-memory entity API: a definition is built, saved, read
// back, switched, deleted and recovered entirely through the page.

let dispose: (() => void) | undefined
let client: QueryClient | undefined
afterEach(async () => {
  dispose?.(); dispose = undefined
  client?.clear()
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
  vi.useRealTimers()
  await new Promise(resolve => setTimeout(resolve, 0))
})

const registryEntity = (entity_id: string, name: string) => ({
  entity_id, name, domain: entity_id.split('.')[0], area_id: null, device_id: null, device_class: null, supported_features: null, unit: null, icon: null, disabled: false, state_options: null,
})
const registry = { entities: [registryEntity('binary_sensor.motion_a', 'Motion A'), registryEntity('binary_sensor.motion_b', 'Motion B')], areas: [], devices: [], floors: [], labels: [] }

type Fixture = ReturnType<typeof visualFixture>
/**
 * The page at `path`, over a new in-memory backend (or `fixture`, to open the page again on what it saved), with
 * Home Assistant's registry `entities` (default: two motion sensors and no devices).
 */
function setup(path: string, prepare?: (fixture: Fixture) => void, fixture: Fixture = visualFixture(), entities: unknown = registry) {
  prepare?.(fixture)
  const callWS = (message: any) => (message.type === 'hass_sharp/get_entities' ? Promise.resolve(structuredClone(entities)) : fixture.callWS(message))
  const hass = { ...fixture, callWS, user: { is_admin: true, id: 'u1' } }
  const root = document.createElement('div')
  root.className = 'hs-app'
  document.body.append(root)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const history = createMemoryHistory()
  history.set({ value: path })
  dispose = render(() => (
    <QueryClientProvider client={client!}><HassCtx.Provider value={hass as any}>
      <MemoryRouter history={history} root={props => <><A href="/elsewhere">Leave</A>{props.children}</>}>
        <Route path="/entities/:id" component={EntityDefinitionView} />
        <Route path="/entities" component={() => <h1>Entities</h1>} />
        <Route path="/elsewhere" component={() => <h1>Elsewhere</h1>} />
      </MemoryRouter>
    </HassCtx.Provider></QueryClientProvider>
  ), root)
  return { root, fixture, history, hass }
}

const button = (root: Element, name: string) => {
  const found = [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === name || b.getAttribute('aria-label') === name)
  expect(found, name).toBeDefined()
  return found!
}
const press = (target: Element, key = 'Enter') => target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
const inspector = (root: Element) => root.querySelector<HTMLElement>('aside[aria-label="Node inspector"]')!
const nameInput = (root: Element) => root.querySelector<HTMLInputElement>('input[aria-label="Entity definition name"]')!
const rename = (root: Element, name: string) => { const input = nameInput(root); input.value = name; input.dispatchEvent(new Event('input', { bubbles: true })) }
const label = (root: Element, text: string) => {
  const input = inspector(root).querySelector<HTMLInputElement>('input[aria-label="Node label"]')!
  input.value = text
  input.dispatchEvent(new Event('input', { bubbles: true }))
}
const json = (root: Element) => { button(root, 'Graph JSON').click(); return JSON.parse(root.querySelector('pre[aria-label="Graph JSON"]')!.textContent!) }
/** The id of the step added last. */
const lastStep = (root: Element) => json(root).nodes.at(-1).id as string
const pin = (root: Element, id: string, side: 'source' | 'target', handle: string) => root.querySelector<HTMLElement>(`[data-id="${id}"] .${side}[data-handleid="${handle}"]`)!
/** Connects a value output to an input from the keyboard, as the canvas offers it. */
const wire = (root: Element, from: string, output: string, to: string, input: string) => {
  press(pin(root, from, 'source', `data:${output}`))
  press(pin(root, to, 'target', `data:${input}`))
}
/** Connects a step's flow output to another step, from the keyboard. */
const flow = (root: Element, from: string, to: string, port = 'next') => {
  press(root.querySelector<HTMLElement>(`[data-id="${from}"] [data-handleid="${port}"]`)!)
  press(root.querySelector<HTMLElement>(`[data-id="${to}"] [data-handleid="in"]`)!)
}
/** Picks a Home Assistant entity in the selected step's entity input. */
const pickEntity = async (root: Element, entityId: string) => {
  inspector(root).querySelector<HTMLButtonElement>('[data-input-id="entity"] .hs-picker-field')!.click()
  const options = () => [...root.querySelectorAll<HTMLElement>('dialog[open] [role="option"]')]
  await vi.waitFor(() => expect(options().some(o => o.textContent?.includes(entityId))).toBe(true))
  options().find(o => o.textContent?.includes(entityId))!.click()
}
/** Escape on the selected step's card: nothing is selected, and the inspector shows the definition form. */
const deselect = (root: Element) => {
  const id = inspector(root).querySelector('.hs-insp-node')?.getAttribute('data-node-id')
  if (id) press(root.querySelector(`.solid-flow__node[data-id="${id}"]`)!, 'Escape')
  expect(inspector(root).querySelector('[aria-label="Entities"]')).not.toBeNull()
}
const saves = (fixture: ReturnType<typeof visualFixture>) => fixture.entities.requests.filter(r => r.operation === 'save')

/** Everything rendered, attributes included, says nothing of automations (the step library's catalog descriptions aside). */
const expectClean = (root: Element) => {
  const copy = root.cloneNode(true) as Element
  copy.querySelectorAll('.hs-lib__text small, .hs-insp-desc').forEach(description => description.remove())
  expect(['automation', 'Automation', '/visual/', '/automations'].filter(word => copy.innerHTML.includes(word))).toEqual([])
}

/** The phase 2 example of the derived motion sensor (code map, section 3), its entity the starter's Entity node. */
const motionExample: DefinitionBody = {
  name: 'Any motion', device: null, settings: [],
  logic: { kind: 'graph', graph: { version: 5, name: 'Any motion',
    nodes: [
      { id: 's', kind: 'entity', config: { key: 'any_motion', domain: 'binary_sensor', name: 'Any motion', enabledByDefault: true, options: {}, history: {}, arguments: { is_on: { kind: 'unset' } } } },
      { id: 'a', kind: 'entity_is', config: { entity: { kind: 'literal', type: 'entity_id', value: 'binary_sensor.motion_a' }, state: { kind: 'literal', type: 'string', value: 'on' } }, label: 'Has state ▸ motion A' },
      { id: 'b', kind: 'entity_is', config: { entity: { kind: 'literal', type: 'entity_id', value: 'binary_sensor.motion_b' }, state: { kind: 'literal', type: 'string', value: 'on' } }, label: 'Has state ▸ motion B' },
      { id: 'or', kind: 'or', config: { left: { kind: 'unset' }, right: { kind: 'unset' } } }],
    edges: [
      { id: 'a.value:or.left', kind: 'data', source: 'a', target: 'or', sourcePort: 'value', targetPort: 'left' },
      { id: 'b.value:or.right', kind: 'data', source: 'b', target: 'or', sourcePort: 'value', targetPort: 'right' },
      { id: 'or.value:s.is_on', kind: 'data', source: 'or', target: 's', sourcePort: 'value', targetPort: 'is_on' }] } },
}
/**
 * A sent definition with its steps' generated ids replaced by `ids` (in the order the steps were added) and their
 * positions left out: the only parts of the request the page makes up itself.
 */
function normalised(definition: any, ids: string[]) {
  const graph = definition.logic.graph
  const rename = new Map<string, string>(graph.nodes.map((node: any, i: number) => [node.id, ids[i]]))
  const id = (old: string) => rename.get(old) ?? old
  const nodes = graph.nodes.map(({ position: _position, size: _size, ...node }: any) => ({ ...node, id: id(node.id) }))
  const edges = graph.edges.map((edge: any) => ({ ...edge, id: `${id(edge.source)}.${edge.sourcePort}:${id(edge.target)}.${edge.targetPort}`, source: id(edge.source), target: id(edge.target) }))
  return { ...definition, logic: { ...definition.logic, graph: { ...graph, nodes, edges } } }
}

it('builds the derived motion sensor from a new definition through the page, and saves the phase 2 example', async () => {
  const { root, fixture, history } = setup('/entities/new')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  // The definition's name names its starter Entity node's entity until that entity is edited.
  rename(root, 'Any motion')
  const ids: string[] = [json(root).nodes[0].id]
  for (const [entity, text] of [['binary_sensor.motion_a', 'Has state ▸ motion A'], ['binary_sensor.motion_b', 'Has state ▸ motion B']]) {
    dropStep(root, 'Has state')
    ids.push(lastStep(root))
    await pickEntity(root, entity)
    label(root, text)
  }
  dropStep(root, 'Or')
  ids.push(lastStep(root))
  label(root, '')
  // The Or gives the Entity node its state.
  wire(root, ids[1], 'value', ids[3], 'left')
  wire(root, ids[2], 'value', ids[3], 'right')
  wire(root, ids[3], 'value', ids[0], 'is_on')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(1))
  const sent = saves(fixture)[0]
  // The id comes from the name (ruling R4); a new definition replaces revision 0.
  expect(sent).toMatchObject({ id: 'any_motion', expectedRevision: 0 })
  // As it goes over the websocket (JSON: a cleared label is no member), with the page's ids and positions, and no
  // entities list: the entity is the Entity node's.
  const body = normalised(JSON.parse(JSON.stringify(sent.definition)), ['s', 'a', 'b', 'or'])
  expect(body).not.toHaveProperty('entities')
  expect(body).toEqual(motionExample)
  await vi.waitFor(() => expect(history.get()).toBe('/entities/any_motion'))
  // The store derives the entity from the node.
  expect(fixture.entities.documents.get('any_motion')!.entities).toEqual([{ key: 'any_motion', domain: 'binary_sensor', name: 'Any motion', enabledByDefault: true, options: {} }])
  expectClean(root)

  // Opened again, it shows what was saved, with its entity's id in Home Assistant and its key fixed.
  const sentGraph = JSON.parse(JSON.stringify(sent.definition.logic.graph))
  dispose!(); document.body.innerHTML = ''
  const again = setup('/entities/any_motion', undefined, fixture)
  await vi.waitFor(() => expect(again.root.querySelector('.hs-save-chip__text')?.textContent).toBe('Saved'))
  expect(nameInput(again.root).value).toBe('Any motion')
  const { definition, ...logic } = json(again.root)
  // The editor lists no entity: the node declares it.
  expect(definition).toEqual({ entities: [], settings: [], device: null })
  expect(logic.nodes.map((n: any) => n.id)).toEqual(sentGraph.nodes.map((n: any) => n.id))
  expect(logic.edges).toEqual(sentGraph.edges)
  expect(again.root.querySelector('[aria-label="Entities"] [data-entity-key]')).toBeNull()
  // Saved, the node's key is read-only in its section, and the section shows its entity's id in Home Assistant.
  again.root.querySelector<HTMLElement>(`.solid-flow__node[data-id="${ids[0]}"]`)!.click()
  await vi.waitFor(() => expect(inspector(again.root).querySelector('.hs-entity-node__id code')?.textContent).toBe('binary_sensor.any_motion_any_motion'))
  expect(control(inspector(again.root), 'key').readOnly).toBe(true)
  expect(control(inspector(again.root), 'key').value).toBe('any_motion')
  expect(again.root.querySelectorAll('.solid-flow__node')).toHaveLength(4)
  expectClean(again.root)
})

const entityRow = (root: Element, key: string) => root.querySelector<HTMLElement>(`[data-entity-key="${key}"]`)!
const settingRow = (root: Element, key: string) => root.querySelector<HTMLElement>(`[data-setting-key="${key}"]`)!
const expand = (row: HTMLElement) => {
  const head = row.querySelector<HTMLButtonElement>('.hs-def-row__head')!
  if (head.getAttribute('aria-expanded') !== 'true') head.click()
  return row
}
const control = (root: Element, field: string) => root.querySelector<HTMLInputElement & HTMLSelectElement>(`[data-field="${field}"] :is(input, select, textarea)`)!
const type = (input: HTMLInputElement | HTMLTextAreaElement, text: string) => {
  input.value = text
  input.dispatchEvent(new Event('input', { bubbles: true }))
  input.dispatchEvent(new FocusEvent('blur'))
}
const choose = (select: HTMLSelectElement, value: string) => { select.value = value; select.dispatchEvent(new Event('change', { bubbles: true })) }
/** Selects the step `id` on the canvas, so the inspector shows it. */
const selectNode = async (root: Element, id: string) => {
  root.querySelector<HTMLElement>(`.solid-flow__node[data-id="${id}"]`)!.click()
  await vi.waitFor(() => expect(inspector(root).querySelector('.hs-insp-node')?.getAttribute('data-node-id')).toBe(id))
}
/** Names the selected Entity node's entity, in the inspector's header. */
const entityName = (root: Element, name: string) => type(inspector(root).querySelector<HTMLInputElement>('input[aria-label="Entity name"]')!, name)
/** Adds an Entity node from the step library; it is selected. Its id. */
const addEntityNode = (root: Element) => { dropStep(root, 'Entity'); return lastStep(root) }
/** Picks one of the definition's entities (or a command) in the selected step's picker, by its label. */
const pickOption = async (root: Element, field: string, text: string) => {
  const box = inspector(root).querySelector<HTMLInputElement>(`[data-input-id="${field}"] input[role="combobox"]`)!
  box.click()
  await vi.waitFor(() => expect(inspector(root).querySelector(`[data-input-id="${field}"] [role="listbox"]`)).not.toBeNull())
  ;[...inspector(root).querySelectorAll<HTMLElement>(`[data-input-id="${field}"] [role="option"]`)].find(o => o.firstElementChild?.textContent === text)!.click()
}

it('builds a polled plug: a time-pattern trigger sets its power, its relay\'s two commands answer it, with a host and a secret password', async () => {
  const { root, fixture } = setup('/entities/new?name=Plug')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  // The starter's Entity node, named after the definition, becomes the relay.
  const relay = json(root).nodes[0].id as string
  await selectNode(root, relay)
  entityName(root, 'Relay')
  type(control(inspector(root), 'key'), 'relay')
  choose(control(inspector(root), 'domain'), 'switch')
  type(control(inspector(root), 'icon'), 'mdi:power-socket')
  const power = addEntityNode(root)
  entityName(root, 'Power')
  type(control(inspector(root), 'key'), 'power')
  choose(control(inspector(root), 'domain'), 'sensor')
  type(control(inspector(root), 'deviceClass'), 'power')
  type(control(inspector(root), 'option:unit'), 'W')
  choose(control(inspector(root), 'option:stateClass'), 'measurement')
  addEntityNode(root)
  entityName(root, 'Online')
  type(control(inspector(root), 'key'), 'online')
  type(control(inspector(root), 'deviceClass'), 'connectivity')
  choose(control(inspector(root), 'entityCategory'), 'diagnostic')
  deselect(root)
  const device = root.querySelector<HTMLElement>('[aria-label="Device"]')!
  control(device, 'device').click()
  type(control(device, 'manufacturer'), 'Acme')
  type(control(device, 'model'), 'P1')
  button(root, 'Add setting').click()
  let row = settingRow(root, 'setting')
  type(control(row, 'key'), 'host')
  row = settingRow(root, 'host')
  type(control(row, 'label'), 'Host')
  type(control(row, 'value'), '10.0.0.7')
  button(root, 'Add setting').click()
  row = settingRow(root, 'setting')
  type(control(row, 'key'), 'password')
  row = settingRow(root, 'password')
  type(control(row, 'label'), 'Password')
  control(row, 'secret').click()
  type(control(row, 'value'), 'hunter2')

  // A time pattern polls; it sets the power.
  dropStep(root, 'Trigger')
  const poll = lastStep(root)
  button(inspector(root), 'Change trigger type').click()
  ;[...inspector(root).querySelectorAll<HTMLElement>('.hs-when__choice')].find(c => c.textContent?.includes('Time pattern'))!.click()
  type(inspector(root).querySelector<HTMLInputElement>('input[aria-label="Minutes"]')!, '/10')
  dropStep(root, 'Set state')
  const setPower = lastStep(root)
  // Its only value is ready to fill: a Set state of an entity with one state value is for that value.
  await pickOption(root, 'entity', 'Power')
  choose(inspector(root).querySelector<HTMLSelectElement>('[data-input-id="value"] select[aria-label="Value kind"]')!, 'number')
  type(inspector(root).querySelector<HTMLInputElement>('[data-input-id="value"] input[aria-label="Value"]')!, '42')
  flow(root, poll, setPower)
  // Turning the relay on and off: a Set state on each of its two commands.
  for (const command of ['turn_on', 'turn_off']) {
    dropStep(root, 'Set state')
    const set = lastStep(root)
    await pickOption(root, 'entity', 'Relay')
    // A new Set state of a switch opens On, as it is saved; Off is one click.
    expect(inspector(root).querySelector<HTMLInputElement>('[data-input-id="is_on"] input[role="switch"]')!.checked).toBe(true)
    if (command === 'turn_off') inspector(root).querySelector<HTMLInputElement>('[data-input-id="is_on"] input[role="switch"]')!.click()
    flow(root, relay, set, `command:${command}`)
  }
  // The new definition's draft is kept as it is edited, with the device and without the typed secret.
  await vi.waitFor(() => expect(Object.values(localStorage).find(draft => draft.includes('"Acme"'))).toBeDefined(), { timeout: 3000 })
  expect(Object.values(localStorage).find(draft => draft.includes('"Acme"'))).toContain('"password"')
  expect(Object.values(localStorage).join('')).not.toContain('hunter2')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(1))
  const sent = JSON.parse(JSON.stringify(saves(fixture)[0]))
  expect(sent).toMatchObject({ id: 'plug', expectedRevision: 0 })
  expect(sent.definition.name).toBe('Plug')
  // The entities are the Entity nodes': the definition lists none.
  expect(sent.definition).not.toHaveProperty('entities')
  expect(sent.definition.device).toEqual({ manufacturer: 'Acme', model: 'P1' })
  // The typed secret goes to Home Assistant in the save, and nowhere else.
  expect(sent.definition.settings).toEqual([
    { key: 'host', type: 'string', value: '10.0.0.7', label: 'Host' },
    { key: 'password', type: 'string', value: 'hunter2', label: 'Password', secret: true },
  ])
  const graph = sent.definition.logic.graph
  expect(graph.definition).toBeUndefined()
  expect(graph.nodes.map((n: any) => [n.kind, n.config])).toEqual([
    ['entity', { key: 'relay', domain: 'switch', name: 'Relay', icon: 'mdi:power-socket', enabledByDefault: true, options: {}, history: {}, arguments: {} }],
    ['entity', { key: 'power', domain: 'sensor', name: 'Power', deviceClass: 'power', enabledByDefault: true, options: { unit: 'W', stateClass: 'measurement' }, history: {}, arguments: {} }],
    ['entity', { key: 'online', domain: 'binary_sensor', name: 'Online', deviceClass: 'connectivity', entityCategory: 'diagnostic', enabledByDefault: true, options: {}, history: {}, arguments: {} }],
    ['trigger', { mode: 'Single', manual: true, trackHistory: false, type: 'ha', trigger: { trigger: 'time_pattern', minutes: '/10' } }],
    ['set_entity_state', { entity: 'power', value: { kind: 'literal', type: 'number', value: 42 } }],
    ['set_entity_state', { entity: 'relay', is_on: { kind: 'literal', type: 'boolean', value: true } }],
    ['set_entity_state', { entity: 'relay', is_on: { kind: 'literal', type: 'boolean', value: false } }],
  ])
  const ids = graph.nodes.map((n: any) => n.id)
  expect(graph.edges.map((e: any) => [e.kind ?? 'execution', ids.indexOf(e.source), e.port ?? 'next', ids.indexOf(e.target)]))
    .toEqual([['execution', 3, 'next', 4], ['execution', 0, 'command:turn_on', 5], ['execution', 0, 'command:turn_off', 6]])
  await vi.waitFor(() => expect(root.querySelector('.hs-save-chip__text')!.textContent).toBe('Saved'))
  // Saved: the store has the secret; the page holds it no longer, and says it is saved.
  expect(fixture.entities.documents.get('plug')!.settings[1].value).toBe('hunter2')
  deselect(root)
  expect(control(expand(settingRow(root, 'password')), 'value').placeholder).toBe('Saved; type to replace')
  expect(root.innerHTML).not.toContain('hunter2')
  expect(JSON.stringify(json(root))).not.toContain('hunter2')
  // Each Entity node now shows the entity id Home Assistant gave it, and its key no longer changes.
  await selectNode(root, power)
  await vi.waitFor(() => expect(inspector(root).querySelector('.hs-entity-node__id code')?.textContent).toBe('sensor.plug_power'))
  expect(control(inspector(root), 'key').readOnly).toBe(true)
})

/** The plug, saved at revision 1 and running: a secret password, and a relay and a power sensor, each an Entity node. */
const plugBody = (): DefinitionBody => ({
  name: 'Plug',
  device: { manufacturer: 'Acme', model: 'P1' },
  settings: [{ key: 'host', label: 'Host', type: 'string', value: '10.0.0.7' }, { key: 'password', label: 'Password', type: 'string', secret: true, value: 'stored-secret' }],
  logic: { kind: 'graph', graph: { version: 5, name: 'Plug', edges: [], nodes: [
    { id: 'relay', kind: 'entity', config: { key: 'relay', domain: 'switch', name: 'Relay' }, position: { x: 60, y: 100 } },
    { id: 'power', kind: 'entity', config: { key: 'power', domain: 'sensor', name: 'Power', options: { unit: 'W' } }, position: { x: 60, y: 500 } }] } },
})
const openPlug = async (prepare?: (fixture: Fixture) => void, fixture?: Fixture, entities?: unknown) => {
  const page = setup('/entities/plug', f => { if (!f.entities.documents.has('plug')) f.entities.seed('plug', plugBody()); prepare?.(f) }, fixture, entities)
  await vi.waitFor(() => expect(page.root.querySelector('.hs-save-chip__text')?.textContent).toBe('Saved'))
  return page
}
const chipTip = (root: Element) => root.querySelector('.hs-save-chip')!.getAttribute('title')
/** Renames the plug's relay on its Entity node, then shows the definition's form again: an unsaved change. */
const renameRelay = async (root: Element, name: string) => {
  await selectNode(root, 'relay')
  entityName(root, name)
  deselect(root)
}
/** The name the relay's Entity node gives it, in the graph the page holds. */
const relayName = (root: Element) => json(root).nodes.find((n: any) => n.id === 'relay').config.name

it('turns a saved definition off and on with its switch, which says what that does, and makes the lists stale', async () => {
  const { root, fixture } = await openPlug()
  client!.setQueryData(['entity-definitions'], [])
  const toggle = root.querySelector<HTMLInputElement>('input[aria-label="Plug on"]')!
  expect(toggle.checked).toBe(true)
  expect(toggle.closest('label')!.title).toBe('On: its entities are in Home Assistant and its steps run')
  expect(inspector(root).querySelector('.hs-insp-off')).toBeNull()
  toggle.click()
  await vi.waitFor(() => expect(fixture.entities.requests.at(-1)).toEqual({ operation: 'set_enabled', id: 'plug', enabled: false }))
  await vi.waitFor(() => expect(toggle.checked).toBe(false))
  expect(toggle.closest('label')!.title).toBe('Off: its entities are unavailable in Home Assistant and none of its steps run')
  expect(inspector(root).querySelector('.hs-insp-off')!.textContent).toBe('Off: its entities are unavailable in Home Assistant and none of its steps run.')
  await vi.waitFor(() => expect(client!.getQueryState(['entity-definitions'])!.isInvalidated).toBe(true))
  // Off is not a failed start: no "not activated" alert.
  expect(root.textContent).not.toContain('Saved, but not activated')
  toggle.click()
  await vi.waitFor(() => expect(toggle.checked).toBe(true))
  expect(fixture.entities.documents.get('plug')!.enabled).toBe(true)
  expectClean(root)
})

it('opens a definition that is off without an activation alert, saying it is off', async () => {
  const { root } = await openPlug(fixture => fixture.entities.seed('plug', plugBody(), { enabled: false }))
  expect(root.querySelector<HTMLInputElement>('input[aria-label="Plug on"]')!.checked).toBe(false)
  expect(root.textContent).not.toContain('Saved, but not activated')
  expect(inspector(root).querySelector('.hs-insp-off')).not.toBeNull()
  // Off, its saved revision is not loaded, which is no reason to save it again: saving it would not start it.
  expect(root.querySelector('.hs-save-chip__text')!.textContent).toBe('Saved')
  expect(button(root, 'Save').getAttribute('aria-disabled')).toBe('true')
  expect(button(root, 'Save').title).toBe('Nothing to save: every change is saved')
  // Its entities are still known from Home Assistant's registry.
  await selectNode(root, 'relay')
  await vi.waitFor(() => expect(inspector(root).querySelector('.hs-entity-node__id code')?.textContent).toBe('switch.plug_relay'))
})

it('saves a definition that is off without saying its activation failed: a save does not start it', async () => {
  const { root, fixture } = await openPlug(fixture => fixture.entities.seed('plug', plugBody(), { enabled: false }))
  await renameRelay(root, 'Main relay')
  await vi.waitFor(() => expect(button(root, 'Save').getAttribute('aria-disabled')).toBeNull())
  button(root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(1))
  await vi.waitFor(() => expect(root.querySelector('.hs-save-chip__text')?.textContent).toBe('Saved'))
  expect(root.textContent).not.toContain('Saved, but not activated')
  expect(root.textContent).not.toContain('Runtime activation failed')
  // Still shown as off.
  expect(root.querySelector<HTMLInputElement>('input[aria-label="Plug on"]')!.checked).toBe(false)
  expect(inspector(root).querySelector('.hs-insp-off')).not.toBeNull()
  expect(fixture.entities.documents.get('plug')!.enabled).toBe(false)
})

it('says a save of a definition that is on did not activate it, with the reason or without one', async () => {
  const { root, fixture } = await openPlug()
  fixture.entities.controls.activation = 'notLoaded'
  fixture.entities.controls.activationError = 'Downloading firmware.bin failed'
  rename(root, 'Socket')
  await vi.waitFor(() => expect(button(root, 'Save').getAttribute('aria-disabled')).toBeNull())
  button(root, 'Save').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Saved, but not activated'))
  expect(root.textContent).toContain('Downloading firmware.bin failed')
  // An answer that says it is not active, and not why.
  const inner = fixture.callWS
  fixture.callWS = async (message: any) => {
    const answer: any = await inner(message)
    if (message.request?.operation === 'save' && answer?.ok) answer.result.activationError = null
    return answer
  }
  rename(root, 'Plug')
  await vi.waitFor(() => expect(button(root, 'Save').getAttribute('aria-disabled')).toBeNull())
  button(root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(2))
  await vi.waitFor(() => expect(root.textContent).toContain('Runtime activation failed. Edit and save again, or reload the HassSharp integration.'))
  expect(root.textContent).toContain('Saved, but not activated')
})

it('deletes a saved definition after a confirmation that says its entities leave Home Assistant, then shows the entities page', async () => {
  const { root, fixture, history } = await openPlug()
  const remove = button(inspector(root), 'Delete entity definition')
  remove.click()
  const dialog = document.querySelector<HTMLDialogElement>('dialog[open]')!
  expect(dialog.textContent).toContain('Plug and its entities are removed from Home Assistant, with their device if it has one. This cannot be undone.')
  button(dialog, 'Delete').click()
  await vi.waitFor(() => expect(history.get()).toBe('/entities'))
  // The lists read again after it (Part of's among them).
  expect(fixture.entities.requests.filter(r => r.operation === 'delete')).toEqual([{ operation: 'delete', id: 'plug', expectedRevision: 1 }])
  expect(fixture.entities.documents.has('plug')).toBe(false)
  expect(root.querySelector('h1')!.textContent).toBe('Entities')
})

it('offers no delete while there are unsaved changes, and none for a definition never saved', async () => {
  const { root } = await openPlug()
  await renameRelay(root, 'Main relay')
  expect(button(inspector(root), 'Delete entity definition').disabled).toBe(true)
  expect(inspector(root).textContent).toContain('Save or undo your changes before deleting it.')
  dispose!(); document.body.innerHTML = ''
  const fresh = setup('/entities/new')
  await vi.waitFor(() => expect(button(fresh.root, 'Save').disabled).toBe(false))
  expect([...inspector(fresh.root).querySelectorAll('button')].some(b => b.textContent === 'Delete entity definition')).toBe(false)
})

it('meets a revision saved elsewhere with the conflict choices, in the entity definition\'s words, and keeps the person\'s version', async () => {
  const { root, fixture } = await openPlug()
  await renameRelay(root, 'Main relay')
  // Saved again elsewhere meanwhile.
  const stored = fixture.entities.documents.get('plug')!
  fixture.entities.documents.set('plug', { ...stored, revision: 2, name: 'Plug (elsewhere)' })
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(document.querySelector('dialog[open]')?.textContent).toContain('This entity definition changed elsewhere'))
  const dialog = document.querySelector<HTMLDialogElement>('dialog[open]')!
  expect(dialog.textContent).toContain('Saving a node library does this to every entity definition that uses it.')
  expect(dialog.textContent).toContain('saves them as a new entity definition, turned off')
  expect(root.textContent).toContain('The saved entity definition changed elsewhere: choose what to keep')
  button(dialog, 'Keep my version').click()
  await vi.waitFor(() => expect(chipTip(root)).toBe('Saved revision 3'))
  const last = saves(fixture).at(-1)
  expect(last).toMatchObject({ id: 'plug', expectedRevision: 2 })
  expect(last.definition).not.toHaveProperty('entities')
  expect(last.definition.logic.graph.nodes.find((n: any) => n.id === 'relay').config.name).toBe('Main relay')
  // The secret no one typed is kept.
  expect(last.definition.settings[1]).toEqual({ key: 'password', label: 'Password', type: 'string', secret: true, keep: true })
  expect(fixture.entities.documents.get('plug')!.settings[1].value).toBe('stored-secret')
})

it('restores a draft without a typed secret, keeping the saved one, and never writes a secret to the draft', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const first = await openPlug()
  const fixture = first.fixture
  expect(first.root.innerHTML).not.toContain('stored-secret')
  await renameRelay(first.root, 'Main relay')
  type(control(expand(settingRow(first.root, 'password')), 'value'), 'typed-secret')
  await vi.advanceTimersByTimeAsync(600)
  const key = 'hass-sharp:draft:u1/entity:plug'
  await vi.waitFor(() => expect(localStorage.getItem(key)).toContain('Main relay'))
  expect(localStorage.getItem(key)).not.toContain('typed-secret')
  expect(localStorage.getItem(key)).not.toContain('stored-secret')
  // The Graph JSON view and Export graph show the graph as the draft keeps it.
  expect(JSON.stringify(json(first.root))).not.toContain('typed-secret')
  const blobs: Blob[] = []
  vi.stubGlobal('URL', Object.assign(Object.create(URL), { createObjectURL: (blob: Blob) => { blobs.push(blob); return 'blob:graph' }, revokeObjectURL() {} }))
  const anchorClick = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
  button(first.root, 'More actions').click()
  ;[...first.root.querySelectorAll<HTMLButtonElement>('[role="menu"] [role="menuitem"]')].find(b => b.textContent === 'Export graph')!.click()
  expect(anchorClick).toHaveBeenCalled()
  const exported = await blobs[0].text()
  expect(exported).toContain('Main relay')
  expect(exported).not.toContain('typed-secret')
  anchorClick.mockRestore()
  vi.unstubAllGlobals()
  // The page goes away with its changes unsaved.
  dispose!(); dispose = undefined; document.body.innerHTML = ''
  expect(localStorage.getItem(key)).not.toContain('typed-secret')
  const again = await openPlug(undefined, fixture)
  await vi.waitFor(() => expect(document.querySelector('dialog[open]')?.textContent).toContain('Restore unsaved changes'))
  button(document.querySelector('dialog[open]')!, 'Restore').click()
  expect(relayName(again.root)).toBe('Main relay')
  expect(control(expand(settingRow(again.root, 'password')), 'value').placeholder).toBe('Saved; type to replace')
  await vi.waitFor(() => expect(button(again.root, 'Save').disabled).toBe(false))
  button(again.root, 'Save').click()
  await vi.waitFor(() => expect(chipTip(again.root)).toBe('Saved revision 2'))
  expect(saves(fixture).at(-1).definition.settings[1]).toEqual({ key: 'password', label: 'Password', type: 'string', secret: true, keep: true })
  expect(fixture.entities.documents.get('plug')!.settings[1].value).toBe('stored-secret')
  expect(again.root.innerHTML).not.toContain('stored-secret')
})

it('opens the definition form as the inspector\'s sheet on a narrow screen, from a bar of its own', async () => {
  vi.stubGlobal('matchMedia', (query: string) => ({ matches: query.includes('max-width'), media: query, addEventListener() {}, removeEventListener() {} }))
  const { root } = setup('/entities/new?name=Any motion')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  const sheet = inspector(root)
  expect(sheet.dataset.open).toBe('false')
  expect(sheet.dataset.peek).toBe('true')
  const peek = button(root, 'Edit Entities, device and settings')
  // Its icon is the adapter's: an entity definition's.
  expect(peek.querySelector('.hs-insp-peek__icon')!.innerHTML).toContain(`d="${icons.entity[0]}"`)
  expect(peek.getAttribute('aria-expanded')).toBe('false')
  peek.click()
  await vi.waitFor(() => expect(sheet.dataset.open).toBe('true'))
  expect(sheet.dataset.peek).toBeUndefined()
  expect(sheet.querySelector('[aria-label="Entities"]')).not.toBeNull()
  // Focus moves into the sheet; Close puts it back as its bar.
  await vi.waitFor(() => expect(sheet.contains(document.activeElement)).toBe(true))
  // The definition's form is in it, a setting's row among it.
  button(sheet, 'Add setting').click()
  type(control(settingRow(root, 'setting'), 'key'), 'host')
  expect(json(root).definition.settings.map((s: any) => s.key)).toEqual(['host'])
  button(sheet, 'Close panel').click()
  expect(sheet.dataset.open).toBe('false')
  expect(sheet.dataset.peek).toBe('true')
})

it('hands a definition built from a class, saved or new, to its own page', async () => {
  const { root } = setup('/entities/plug1', fixture => fixture.entities.seed('plug1', { name: 'Plug one', settings: [{ key: 'Host', value: '10.0.0.1' }], logic: { kind: 'class', type: 'library:shelly:NodeLib.FakePlug' } }))
  await vi.waitFor(() => expect(root.querySelector('section.hs-cdef[aria-label="Entity definition"]')).not.toBeNull())
  expect(root.querySelector('h1')!.textContent).toBe('Plug one')
  expect(nameInput(root).value).toBe('Plug one')
  expect(root.querySelector('section[aria-label="Entity definition editor"]')).toBeNull()
  dispose!(); document.body.innerHTML = ''
  // A new one: the class page at once, which asks only for the classes.
  const fresh = setup('/entities/new?logic=class&name=Shelly')
  expect(fresh.root.querySelector('section.hs-cdef[aria-label="Entity definition"]')).not.toBeNull()
  expect(fresh.root.querySelector('h1')!.textContent).toBe('Shelly')
  await vi.waitFor(() => expect(fresh.fixture.entities.requests).toEqual([{ operation: 'types' }]))
  expect(fresh.root.querySelector('section[aria-label="Entity definition editor"]')).toBeNull()
})

it('starts a new definition named from the address, with an Entity node, a binary sensor named after it, and the editor in its words', async () => {
  const { root, fixture } = setup('/entities/new?logic=graph&name=Hall motion')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  expect(nameInput(root).value).toBe('Hall motion')
  const graph = json(root)
  expect(graph.definition).toEqual({ entities: [], settings: [], device: null })
  expect(graph.nodes.map((n: any) => [n.kind, n.config.key, n.config.domain, n.config.name])).toEqual([['entity', 'hall_motion', 'binary_sensor', 'Hall motion']])
  expect(root.querySelector('section[aria-label="Entity definition editor"]')).not.toBeNull()
  expect(button(root, 'Back to entities')).toBeDefined()
  // Its node follows the definition's name until it is saved.
  rename(root, 'Hall motions')
  expect(json(root).nodes[0].config).toMatchObject({ key: 'hall_motions', name: 'Hall motions' })
  rename(root, 'Hall motion')
  // Renamed after the entity was named on its node, the entity keeps its own name.
  root.querySelector<HTMLElement>(`.solid-flow__node[data-id="${graph.nodes[0].id}"]`)!.click()
  await vi.waitFor(() => expect(inspector(root).querySelector('input[aria-label="Entity name"]')).not.toBeNull())
  const name = inspector(root).querySelector<HTMLInputElement>('input[aria-label="Entity name"]')!
  name.value = 'Hall'
  name.dispatchEvent(new Event('input', { bubbles: true }))
  rename(root, 'Hallway motion')
  expect(json(root).nodes[0].config).toMatchObject({ key: 'hall_motion', domain: 'binary_sensor', name: 'Hall' })
  // Taken ids are asked for at the first save.
  fixture.entities.seed('hallway_motion', newGraphDefinition('Hallway motion'))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture)[0]).toMatchObject({ id: 'hallway_motion_2', expectedRevision: 0 }))
  expect(fixture.entities.requests.filter(r => r.operation === 'list')).toHaveLength(1)
  expect(fixture.calls.some((call: any) => call.type === 'hass_sharp/visual' || call.type === 'hass_sharp/run_now')).toBe(false)
  more(root, 'Help')
  expect(document.querySelector('dialog[open]')!.textContent).toContain('What an entity definition is')
  // What a secret is kept out of, and where it can still show.
  expect(document.querySelector('dialog[open]')!.textContent).toContain('once saved, its value is never shown again. It is left out of this browser\'s drafts and of Export graph. '
    + 'Run traces, the Log step and the service logs show it as hidden. A true or false worked out from it is shown. '
    + 'A step that returns what it was given, such as a custom step or a service response, can still show it.')
  // The ten types, how a camera shows a frame, and what HTTP requests, downloads and restored state do.
  const help = document.querySelector('dialog[open]')!.textContent!
  // Which entities a State follows (Visual/StateDependencies.cs): those chosen in the steps it reads, not any value wired in.
  expect(help).toContain('A State step gives an entity its state: wire values into it, and it is worked out again whenever an entity chosen in a step it reads, such as Has state or Current state, changes (not one read by a step such as Read entity).')
  expect(help).not.toContain('whenever one of them changes')
  // A State's runs are recorded when its run history is turned on, as a trigger's and a frame's are; a command's and a
  // start's unless it is turned off.
  expect(help).toContain('When commanded and When started record their runs, listed under Runs, unless their run history is turned off; '
    + 'a trigger, a State or a When a frame is needed records them only when it is turned on, and a trigger also when it is started with Run now.')
  expect(help).not.toContain('a State never does')
  // Off, the entities it made stay unavailable; one saved off has made none yet.
  expect(help).toContain('Turned off, the definition\'s entities stay in Home Assistant as unavailable; deleting it removes them. '
    + 'A definition saved off, such as a copy, creates its entities the first time it is turned on.')
  expect(help).toContain('each a binary sensor, a sensor, a switch, a button, a number, a select, a text, a light, a cover or a camera')
  expect(help).toContain('A camera shows the picture its last Show frame step gave it, such as one taken with Take a picture and drawn on. '
    + 'With a When a frame is needed step, its steps make a new picture when Home Assistant asks for one, which it does only while something is watching the camera or asks for its picture: nothing runs otherwise.')
  for (const fact of ['http:// or https://', 'checks the server\'s certificate', '10 seconds', '4 MiB', 'fails on an error status unless you turn that off',
    'one to a different host fails the step when it has headers, so they are never sent there', 'Keep the response out of run history', 'SHA-256 checksum, of the file as the server sends it',
    '.storage/hass_sharp/files/', 'is not downloaded again', 'come back with the state they had after Home Assistant restarts or the definition is saved',
    'keeps its last one, or comes on at full brightness the first time', 'A button has no state'])
    expect(help, fact).toContain(fact)
  expectClean(root)
})

const more = (root: Element, item: string) => {
  button(root, 'More actions').click()
  ;[...root.querySelectorAll<HTMLButtonElement>('[role="menu"] [role="menuitem"]')].find(b => b.textContent === item)!.click()
}

/** An Entity node of the example: its entity and state values, the rest at what a new node has. */
const exampleNode = (id: string, config: Record<string, unknown>) =>
  ({ id, kind: 'entity', config: { enabledByDefault: true, options: {}, history: {}, arguments: {}, ...config } })
/**
 * The phase 2 example of the device definition (code map, section 3; `tests/Runtime/EntityGraphTests.cs`, A device
 * definition groups its entities), its three entities Entity nodes: the power's and the online's state given on the
 * node, the relay's two commands each setting it.
 */
const plugExample: DefinitionBody = {
  name: 'Plug', device: { manufacturer: 'Acme', model: 'P1', suggestedArea: 'Kitchen' }, settings: [],
  logic: { kind: 'graph', graph: { version: 5, name: 'Plug',
    nodes: [
      exampleNode('p', { key: 'power', domain: 'sensor', name: 'Power', deviceClass: 'power', options: { unit: 'W', stateClass: 'measurement' }, arguments: { value: { kind: 'literal', type: 'number', value: 42 } } }),
      exampleNode('o', { key: 'online', domain: 'binary_sensor', deviceClass: 'connectivity', entityCategory: 'diagnostic', arguments: { is_on: { kind: 'literal', type: 'boolean', value: true } } }),
      exampleNode('r', { key: 'relay', domain: 'switch', name: 'Relay', icon: 'mdi:power-socket', enabledByDefault: false }),
      { id: 'set_on', kind: 'set_entity_state', config: { is_on: { kind: 'literal', type: 'boolean', value: true }, entity: 'relay' } },
      { id: 'set_off', kind: 'set_entity_state', config: { is_on: { kind: 'literal', type: 'boolean', value: false }, entity: 'relay' } }],
    edges: [
      { id: 'r:command:turn_on:set_on', source: 'r', target: 'set_on', port: 'command:turn_on' },
      { id: 'r:command:turn_off:set_off', source: 'r', target: 'set_off', port: 'command:turn_off' }] } },
}
/** Gives the selected step's state value `id` a value: Set value, then its kind and, for a number, the number. */
const setValue = (root: Element, id: string, kind?: string, value?: string) => {
  inspector(root).querySelector<HTMLButtonElement>(`[data-input-id="${id}"] .hs-insp-unset button`)!.click()
  if (kind) choose(inspector(root).querySelector<HTMLSelectElement>(`[data-input-id="${id}"] select[aria-label="Value kind"]`)!, kind)
  if (value !== undefined) type(inspector(root).querySelector<HTMLInputElement>(`[data-input-id="${id}"] input[aria-label="Value"]`)!, value)
}

it('builds the phase 2 plug through the page, every member of it, and saves that example', async () => {
  const { root, fixture } = setup('/entities/new?name=Plug')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  // The starter's Entity node, named after the definition, becomes the power sensor: 42 W.
  const ids: string[] = [json(root).nodes[0].id]
  await selectNode(root, ids[0])
  type(control(inspector(root), 'key'), 'power')
  entityName(root, 'Power')
  choose(control(inspector(root), 'domain'), 'sensor')
  type(control(inspector(root), 'deviceClass'), 'power')
  type(control(inspector(root), 'option:unit'), 'W')
  choose(control(inspector(root), 'option:stateClass'), 'measurement')
  setValue(root, 'value', 'number', '42')
  // A binary sensor without a name of its own, on.
  ids.push(addEntityNode(root))
  entityName(root, '')
  type(control(inspector(root), 'key'), 'online')
  type(control(inspector(root), 'deviceClass'), 'connectivity')
  choose(control(inspector(root), 'entityCategory'), 'diagnostic')
  setValue(root, 'is_on')
  const on = () => inspector(root).querySelector<HTMLInputElement>('[data-input-id="is_on"] input[role="switch"]')!
  if (!on().checked) on().click()
  // The relay, which Home Assistant adds turned off.
  const relay = addEntityNode(root)
  ids.push(relay)
  type(control(inspector(root), 'key'), 'relay')
  choose(control(inspector(root), 'domain'), 'switch')
  entityName(root, 'Relay')
  type(control(inspector(root), 'icon'), 'mdi:power-socket')
  control(inspector(root), 'enabledByDefault').click()
  deselect(root)
  const device = root.querySelector<HTMLElement>('[aria-label="Device"]')!
  control(device, 'device').click()
  type(control(device, 'manufacturer'), 'Acme')
  type(control(device, 'model'), 'P1')
  type(control(device, 'suggestedArea'), 'Kitchen')

  // Each step is added with its title as its label; the example's steps have none, so the label is cleared.
  // Turn on and Turn off, each setting the relay.
  for (const [command, on] of [['turn_on', true], ['turn_off', false]] as const) {
    deselect(root)
    dropStep(root, 'Set state')
    const set = lastStep(root)
    ids.push(set)
    label(root, '')
    await pickOption(root, 'entity', 'Relay')
    const toggle = () => inspector(root).querySelector<HTMLInputElement>('[data-input-id="is_on"] input[role="switch"]')!
    expect(toggle().checked).toBe(true)
    if (!on) toggle().click()
    flow(root, relay, set, `command:${command}`)
  }
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(1))
  const sent = JSON.parse(JSON.stringify(saves(fixture)[0]))
  expect(sent).toMatchObject({ id: 'plug', expectedRevision: 0 })
  // No entities list: every entity is an Entity node's.
  expect(sent.definition).not.toHaveProperty('entities')
  const body = normalised(sent.definition, ['p', 'o', 'r', 'set_on', 'set_off'])
  // Normalised as the motion example's: the page's step ids, positions and wire ids replaced by the example's.
  body.logic.graph.edges = body.logic.graph.edges.map(({ id: _id, ...edge }: any) => ({ id: `${edge.source}:${edge.port}:${edge.target}`, ...edge }))
  expect(body).toEqual(plugExample)
})

it('keeps the key of an Entity node added to a saved definition open until the save that stores it', async () => {
  const { root, fixture } = await openPlug()
  const added = addEntityNode(root)
  entityName(root, 'Meter')
  expect(control(inspector(root), 'key').readOnly).toBe(false)
  type(control(inspector(root), 'key'), 'meter')
  expect(control(inspector(root), 'key').readOnly).toBe(false)
  // The saved ones stay fixed meanwhile.
  await selectNode(root, 'relay')
  expect(control(inspector(root), 'key').readOnly).toBe(true)
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(chipTip(root)).toBe('Saved revision 2'))
  const sent = saves(fixture).at(-1).definition
  expect(sent).not.toHaveProperty('entities')
  expect(sent.logic.graph.nodes.filter((n: any) => n.kind === 'entity').map((n: any) => n.config.key)).toEqual(['relay', 'power', 'meter'])
  // The store's entities are the nodes'; the new one's key is fixed now.
  expect(fixture.entities.documents.get('plug')!.entities!.map(e => e.key)).toEqual(['relay', 'power', 'meter'])
  await selectNode(root, added)
  await vi.waitFor(() => expect(control(inspector(root), 'key').readOnly).toBe(true))
})

it('saves a definition named New under another id than the new one\'s address (ruling R17)', async () => {
  const { root, fixture, history } = setup('/entities/new?name=New')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(history.get()).toBe('/entities/new_2'))
  expect(saves(fixture)[0]).toMatchObject({ id: 'new_2', expectedRevision: 0 })
  await vi.waitFor(() => expect(chipTip(root)).toBe('Saved revision 1'))
  expect(nameInput(root).value).toBe('New')
})

it('gives a new definition the next free id when its id was taken elsewhere before its first save went through', async () => {
  const { root, fixture, history } = setup('/entities/new?name=Hall motion')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  const answer = fixture.entities.callWS
  fixture.entities.callWS = (message: any) => {
    // Another page saves "Hall motion" first, between this page's choice of the id and its save.
    if (message.request?.operation === 'save' && message.request.id === 'hall_motion' && !fixture.entities.documents.has('hall_motion'))
      fixture.entities.seed('hall_motion', newGraphDefinition('Hall motion'))
    return answer(message)
  }
  button(root, 'Save').click()
  await vi.waitFor(() => expect(history.get()).toBe('/entities/hall_motion_2'))
  expect(saves(fixture).map(s => [s.id, s.expectedRevision])).toEqual([['hall_motion', 0], ['hall_motion_2', 0]])
  expect(document.querySelector('dialog[open]')).toBeNull()
  await vi.waitFor(() => expect(chipTip(root)).toBe('Saved revision 1'))
  expect(fixture.entities.documents.get('hall_motion')!.revision).toBe(1)
})

it('turns the device back on as it was, its parent too, after a step was selected in between', async () => {
  const body = plugBody()
  body.device = { manufacturer: 'Acme', model: 'P1', via: { definition: 'hub', deviceId: null } }
  const { root } = await openPlug(fixture => fixture.entities.seed('plug', body))
  // As read: the store answers every member of the device, those not set as null.
  const before = json(root).definition.device
  expect(before).toMatchObject({ manufacturer: 'Acme', model: 'P1', via: { definition: 'hub', deviceId: null } })
  control(inspector(root).querySelector('[aria-label="Device"]')!, 'device').click()
  expect(json(root).definition.device).toBeNull()
  // A step selected (the form goes), then nothing (it is back).
  press(root.querySelector('.solid-flow__node[data-id="relay"]')!)
  await vi.waitFor(() => expect(inspector(root).querySelector('.hs-insp-node')).not.toBeNull())
  deselect(root)
  control(inspector(root).querySelector('[aria-label="Device"]')!, 'device').click()
  expect(json(root).definition.device).toEqual(before)
})

/** The plug with three secrets saved: Password, Token and API key. */
const securePlug = () => {
  const body = plugBody()
  body.settings = [...body.settings, { key: 'token', label: 'Token', type: 'string', secret: true, value: 'stored-token' }, { key: 'api_key', label: 'API key', type: 'string', secret: true, value: 'stored-key' }]
  return body
}
const conflictWith = async (root: Element, fixture: Fixture) => {
  const stored = fixture.entities.documents.get('plug')!
  fixture.entities.documents.set('plug', { ...stored, revision: 2, name: 'Plug (elsewhere)' })
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(document.querySelector('dialog[open]')?.textContent).toContain('This entity definition changed elsewhere'))
  return document.querySelector<HTMLDialogElement>('dialog[open]')!
}

it('saves a copy turned off after a conflict, and names the secrets the copy has no value for', async () => {
  const { root, fixture, history } = await openPlug(fixture => fixture.entities.seed('plug', securePlug()))
  await renameRelay(root, 'Main relay')
  type(control(expand(settingRow(root, 'token')), 'value'), 'typed-token')
  button(await conflictWith(root, fixture), 'Save as a copy').click()
  await vi.waitFor(() => expect(history.get()).toBe('/entities/plug_copy'))
  const copy = saves(fixture).at(-1)
  expect(copy).toMatchObject({ id: 'plug_copy', expectedRevision: 0, enabled: false })
  expect(copy.definition.name).toBe('Plug (copy)')
  expect(copy.definition.settings.find((s: any) => s.key === 'token').value).toBe('typed-token')
  await vi.waitFor(() => expect(root.textContent).toContain('The copy has no saved value for Password and API key. Type them again.'))
  // The copy is off, as it was saved: not a failed activation.
  expect(root.textContent).not.toContain('Saved, but not activated')
  expect(fixture.entities.documents.get('plug_copy')!.enabled).toBe(false)
  expect(fixture.entities.documents.get('plug')!.name).toBe('Plug (elsewhere)')
  deselect(root)
  expect(control(expand(settingRow(root, 'password')), 'value').placeholder).toBe('No value')
  expect(root.innerHTML).not.toMatch(/stored-|typed-token/)
})

it('says so when the copy lacks a single secret', async () => {
  const { root, fixture } = await openPlug()
  await renameRelay(root, 'Main relay')
  button(await conflictWith(root, fixture), 'Save as a copy').click()
  await vi.waitFor(() => expect(root.textContent).toContain('The copy has no saved value for Password. Type it again.'))
})

it('discards the changes after a conflict, opening the definition as saved elsewhere', async () => {
  const { root, fixture } = await openPlug()
  await renameRelay(root, 'Main relay')
  button(await conflictWith(root, fixture), 'Discard my changes').click()
  await vi.waitFor(() => expect(nameInput(root).value).toBe('Plug (elsewhere)'))
  expect(chipTip(root)).toBe('Saved revision 2')
  expect(relayName(root)).toBe('Relay')
  expect(saves(fixture)).toHaveLength(1)
})

it('reads the definition again when node libraries change: the newer revision when nothing is unsaved, the conflict otherwise', async () => {
  const { root, fixture } = await openPlug()
  const stored = fixture.entities.documents.get('plug')!
  fixture.entities.documents.set('plug', { ...stored, revision: 2, name: 'Plug renamed' })
  fixture.controls.generation = 'g2'
  window.dispatchEvent(new Event('hass-sharp-libraries-changed'))
  await vi.waitFor(() => expect(nameInput(root).value).toBe('Plug renamed'))
  expect(chipTip(root)).toBe('Saved revision 2')
  await renameRelay(root, 'Main relay')
  fixture.entities.documents.set('plug', { ...fixture.entities.documents.get('plug')!, revision: 3 })
  fixture.controls.generation = 'g3'
  window.dispatchEvent(new Event('hass-sharp-libraries-changed'))
  await vi.waitFor(() => expect(root.textContent).toContain('This entity definition changed elsewhere while you had unsaved changes here.'))
  expect(relayName(root)).toBe('Main relay')
})

it('keeps the definition when its delete is refused because it changed elsewhere, and says why', async () => {
  const { root, fixture, history } = await openPlug()
  const stored = fixture.entities.documents.get('plug')!
  fixture.entities.documents.set('plug', { ...stored, revision: 2 })
  button(inspector(root), 'Delete entity definition').click()
  button(document.querySelector('dialog[open]')!, 'Delete').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Could not delete it:'))
  expect(fixture.entities.requests.at(-1)).toEqual({ operation: 'delete', id: 'plug', expectedRevision: 1 })
  expect(fixture.entities.documents.has('plug')).toBe(true)
  expect(history.get()).toBe('/entities/plug')
  expect(document.querySelector('dialog[open]')).toBeNull()
})

it('shows a save refused with a setting\'s problem on its row, and the Problems tab finds that row', async () => {
  const { root, fixture } = await openPlug()
  await renameRelay(root, 'Main relay')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  fixture.entities.controls.diagnostics = [{ nodeId: null, inputId: 'host', portId: null, code: 'definition.settingType', severity: 3, message: 'Setting host is not a text', startLine: null, startColumn: null } as any]
  button(root, 'Save').click()
  await vi.waitFor(() => expect(settingRow(root, 'host').textContent).toContain('Setting host is not a text'))
  expect(settingRow(root, 'host').querySelector('.hs-def-row__head')!.getAttribute('aria-expanded')).toBe('true')
  expect(chipTip(root)).not.toBe('Saved revision 2')
  // Closed by the person, then found from the Problems tab: the form shows, the row opens, focus is in it.
  settingRow(root, 'host').querySelector<HTMLButtonElement>('.hs-def-row__head')!.click()
  press(root.querySelector('.solid-flow__node[data-id="relay"]')!)
  await vi.waitFor(() => expect(inspector(root).querySelector('.hs-insp-node')).not.toBeNull())
  ;[...root.querySelectorAll<HTMLButtonElement>('.hs-drawer [role="tab"]')].find(b => b.textContent?.startsWith('Problems'))!.click()
  ;[...root.querySelectorAll<HTMLButtonElement>('.hs-problem__message')].find(b => b.textContent === 'Setting host is not a text')!.click()
  await vi.waitFor(() => expect(document.activeElement).toBe(control(settingRow(root, 'host'), 'key')))
  expect(inspector(root).querySelector('.hs-insp-node')).toBeNull()
})

it('a Problems-tab choice about the definition ends a step\'s description and shows its row', async () => {
  const { root, fixture } = await openPlug()
  await renameRelay(root, 'Main relay')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  fixture.entities.controls.diagnostics = [{ nodeId: null, inputId: 'host', portId: null, code: 'definition.settingType', severity: 3, message: 'Setting host is not a text', startLine: null, startColumn: null } as any]
  button(root, 'Save').click()
  await vi.waitFor(() => expect(settingRow(root, 'host').textContent).toContain('Setting host is not a text'))
  settingRow(root, 'host').querySelector<HTMLButtonElement>('.hs-def-row__head')!.click()
  libraryItem(root, 'Delay').click()
  expect(inspector(root).querySelector('[aria-label="Step details"]')).not.toBeNull()
  ;[...root.querySelectorAll<HTMLButtonElement>('.hs-drawer [role="tab"]')].find(b => b.textContent?.startsWith('Problems'))!.click()
  ;[...root.querySelectorAll<HTMLButtonElement>('.hs-problem__message')].find(b => b.textContent === 'Setting host is not a text')!.click()
  await vi.waitFor(() => expect(inspector(root).querySelector('[aria-label="Step details"]')).toBeNull())
  expect(libraryItem(root, 'Delay').getAttribute('aria-pressed')).toBe('false')
  await vi.waitFor(() => expect(document.activeElement).toBe(control(settingRow(root, 'host'), 'key')))
  expect(settingRow(root, 'host').querySelector('.hs-def-row__head')!.getAttribute('aria-expanded')).toBe('true')
})

// ---- The Runs tab: the definition's runs, in its words, each linked to its run's page under /entities.
const runRecord = (overrides: Record<string, unknown>) => ({
  runId: 'run-1', automationId: 'entity:plug', revision: 'rev-1', action: 'VisualAutomation::On', actionLabel: 'Relay: Turn on', triggerNodeId: 'relay',
  visualId: 'plug', visualRevision: 1, cause: 'Command', trigger: { kind: 'command', entity: 'relay', command: 'turn_on' }, plannedUtc: null,
  receivedUtc: '2026-11-12T01:00:00Z', startUtc: '2026-11-12T01:00:01Z', endUtc: '2026-11-12T01:00:02Z', durationMs: 1000, status: 'Succeeded', error: null, hasTrace: true, ...overrides,
})
const plugRuns = () => [
  runRecord({ runId: 'run-2' }),
  runRecord({ runId: 'run-1', cause: 'Setup', trigger: { kind: 'setup' }, actionLabel: 'When started', triggerNodeId: 'start' }),
]
/** The plug's page with its run history answered from `runs` (by its run history key), and its runs' pages routed. */
async function openPlugRuns(runs: () => unknown[], path = '/entities/plug?drawer=runs') {
  const fixture = visualFixture()
  fixture.entities.seed('plug', plugBody())
  const inner = fixture.callWS
  const history_: any[] = []
  const callWS = async (message: any) => {
    if (message.type === 'hass_sharp/get_entities') return structuredClone(registry)
    if (message.type === 'hass_sharp/run_history') { history_.push(message); return { version: 2, offset: 0, limit: 20, runs: message.automationId === 'entity:plug' ? runs() : [] } }
    if (message.type === 'hass_sharp/run_trace') {
      const found = runs().find((r: any) => r.runId === message.runId) as any
      if (!found) throw { code: 'not_found', message: `Run ${message.runId} was not found.` }
      const stored = fixture.entities.documents.get('plug')!
      return { version: 1, live: false, run: found, trace: null,
        snapshot: { revision: 'rev-1', visualId: 'plug', visualRevision: 1, catalog: nodeCatalog,
          // Its entities are its Entity nodes': the definition composed in has its settings by key and type only.
          graph: { ...structuredClone(stored.logic.graph), definition: { settings: stored.settings.map(s => ({ key: s.key, type: s.type })) } } } }
    }
    return inner(message)
  }
  const hass = { ...fixture, callWS, user: { is_admin: true, id: 'u1' } }
  const root = document.createElement('div')
  root.className = 'hs-app'
  document.body.append(root)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const history = createMemoryHistory()
  history.set({ value: path })
  dispose = render(() => (
    <QueryClientProvider client={client!}><HassCtx.Provider value={hass as any}>
      <MemoryRouter history={history}>
        <Route path="/entities/:id" component={EntityDefinitionView} />
        <Route path="/entities/:id/runs/:runId" component={EntityRunTraceView} />
        <Route path="/entities" component={() => <h1>Entities</h1>} />
      </MemoryRouter>
    </HassCtx.Provider></QueryClientProvider>
  ), root)
  await vi.waitFor(() => expect(root.querySelector('.hs-save-chip__text')?.textContent).toBe('Saved'))
  return { root, fixture, history, requests: history_ }
}
const card = (root: Element, id: string) => root.querySelector<HTMLElement>(`.solid-flow__node[data-id="${id}"]`)!
const runRows = (root: Element) => [...root.querySelectorAll<HTMLLIElement>('.hs-run-row')]

it('lists the definition\'s runs in its Runs tab: its causes, its entities and commands by name, and its own words', async () => {
  const { root, requests } = await openPlugRuns(plugRuns)
  await vi.waitFor(() => expect(runRows(root)).toHaveLength(2))
  expect(requests[0]).toMatchObject({ type: 'hass_sharp/run_history', automationId: 'entity:plug' })
  expect(runRows(root)[0].querySelector('.hs-run-cause')!.textContent).toBe('Command · Relay: Turn on')
  expect(runRows(root)[1].querySelector('.hs-run-cause')!.textContent).toBe('Started · Definition started')
  expect(runRows(root).map(row => row.querySelector('a.hs-run-trace')!.getAttribute('href'))).toEqual(['/entities/plug/runs/run-2', '/entities/plug/runs/run-1'])
  expectClean(root)
  // A run's details; its raw record keeps the backend's own field names (automationId) and is left out of the check.
  runRows(root)[0].querySelector<HTMLButtonElement>('.hs-run-toggle')!.click()
  await vi.waitFor(() => expect(runRows(root)[0].querySelector('.hs-run-body')).not.toBeNull())
  expect([...runRows(root)[0].querySelectorAll('dt')].map(dt => dt.textContent)).toContain('Revision')
  expect(runRows(root)[0].querySelector('.hs-trigger-headline')!.textContent).toBe('Relay: Turn on')
  runRows(root)[0].querySelector('.hs-run-raw')!.remove()
  expectClean(root)
})

it('says when a saved definition\'s runs appear, in its Runs tab with none yet', async () => {
  const { root } = await openPlugRuns(() => [])
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
  expect(root.textContent).toContain('Runs appear here when one of its entities is commanded, when it starts, or when a trigger with run history fires.')
  expectClean(root)
})

it('opens a run of the definition on its run page and comes back to its Runs tab, with no secret on the way, typed or stored', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const { root, history } = await openPlugRuns(plugRuns)
  // A replacement password typed, not saved: the page is left without it.
  type(control(expand(settingRow(root, 'password')), 'value'), 'typed-secret')
  await vi.advanceTimersByTimeAsync(600)
  await vi.waitFor(() => expect(runRows(root)).toHaveLength(2))
  runRows(root)[0].querySelector<HTMLAnchorElement>('a.hs-run-trace')!.click()
  await vi.waitFor(() => expect(document.querySelector('dialog[open]')?.textContent).toContain('Unsaved changes'))
  button(document.querySelector('dialog[open]')!, 'Discard changes').click()
  await vi.waitFor(() => expect(history.get()).toBe('/entities/plug/runs/run-2'))
  await vi.waitFor(() => expect(root.querySelector('a.hs-trace-open')).not.toBeNull())
  expect(root.querySelector('a.hs-trace-open')!.getAttribute('href')).toBe('/entities/plug')
  expect(card(root, 'relay').textContent).toContain('Relay')
  for (const secret of ['typed-secret', 'stored-secret']) expect(document.body.innerHTML).not.toContain(secret)
  root.querySelector<HTMLButtonElement>('[aria-label="Back to the entity definition’s runs"]')!.click()
  await vi.waitFor(() => expect(runRows(root)).toHaveLength(2))
  expect(history.get()).toBe('/entities/plug')
  for (const secret of ['typed-secret', 'stored-secret']) expect(document.body.innerHTML).not.toContain(secret)
})

/** Holds every `read` of the entity API until `release` is called. */
function holdReads(fixture: Fixture) {
  const inner = fixture.entities.callWS
  let release!: () => void
  const gate = new Promise<void>(resolve => (release = resolve))
  fixture.entities.callWS = async (message: any) => {
    if (message.request?.operation === 'read') await gate
    return inner(message)
  }
  return () => release()
}
const editorShown = (root: Element) => !!root.querySelector('section[aria-label="Entity definition editor"], [aria-label^="Entity definition canvas"]')
const catalogAsked = (fixture: Fixture) => fixture.calls.some(c => c.type === 'hass_sharp/node_library' && c.request?.operation === 'catalog')
const reads = (fixture: Fixture) => fixture.entities.requests.filter(r => r.operation === 'read').length

it('opens a definition built from a class without the graph editor, even while it is being read', async () => {
  let release!: () => void
  const { root, fixture } = setup('/entities/plug1', f => {
    f.entities.seed('plug1', { name: 'Plug one', settings: [{ key: 'Host', value: '10.0.0.1' }], logic: { kind: 'class', type: 'library:shelly:NodeLib.FakePlug' } })
    release = holdReads(f)
  })
  await vi.waitFor(() => expect(root.querySelector('[role="status"]')?.textContent).toBe('Loading saved entity definition…'))
  expect(editorShown(root)).toBe(false)
  expect(root.textContent).not.toContain('Loading saved graph')
  expect(button(root, 'Back to entities')).toBeDefined()
  await new Promise(resolve => setTimeout(resolve, 20))
  expect(catalogAsked(fixture)).toBe(false)
  release()
  await vi.waitFor(() => expect(root.querySelector('section.hs-cdef[aria-label="Entity definition"]')).not.toBeNull())
  expect(nameInput(root).value).toBe('Plug one')
  expect(editorShown(root)).toBe(false)
  expect(root.textContent).not.toContain('Loading saved graph')
  await new Promise(resolve => setTimeout(resolve, 20))
  expect(catalogAsked(fixture)).toBe(false)
  expect(reads(fixture)).toBe(1)
})

it('opens a definition built from steps in the graph editor, read once', async () => {
  let release!: () => void
  const { root, fixture } = setup('/entities/plug', f => { f.entities.seed('plug', plugBody()); release = holdReads(f) })
  await vi.waitFor(() => expect(root.querySelector('[role="status"]')?.textContent).toBe('Loading saved entity definition…'))
  expect(editorShown(root)).toBe(false)
  release()
  await vi.waitFor(() => expect(root.querySelector('.hs-save-chip__text')?.textContent).toBe('Saved'))
  expect(root.querySelector('section[aria-label="Entity definition editor"]')).not.toBeNull()
  expect(nameInput(root).value).toBe('Plug')
  expect(root.querySelector('section.hs-cdef')).toBeNull()
  // The editor is handed the definition the page read: its own reads are only the check after the node catalog
  // answers, as before the page read first.
  expect(reads(fixture)).toBe(2)
  expect(fixture.calls.findIndex(c => c.type === 'hass_sharp/entities' && c.request.operation === 'read')).toBeLessThan(fixture.calls.findIndex(c => c.request?.operation === 'catalog'))
  // Saved again, the editor's own read and save work as before.
  rename(root, 'Plug two')
  button(root, 'Save').click()
  await vi.waitFor(() => expect(fixture.entities.documents.get('plug')!.revision).toBe(2))
  expect(fixture.entities.documents.get('plug')!.name).toBe('Plug two')
})

it('says a definition that is not stored was not found, and one that could not be read why', async () => {
  const { root, history } = setup('/entities/nothing')
  await vi.waitFor(() => expect(root.querySelector('[role="alert"]')?.textContent).toContain('This entity definition was not found. It may have been deleted.'))
  expect(editorShown(root)).toBe(false)
  expect(root.querySelector('section.hs-cdef[aria-label="Entity definition"]')).toBeNull()
  button(root, 'Back to entities').click()
  await vi.waitFor(() => expect(history.get()).toBe('/entities'))
  dispose!(); document.body.innerHTML = ''
  let fail = true
  const again = setup('/entities/plug', f => {
    f.entities.seed('plug', plugBody())
    const inner = f.entities.callWS
    f.entities.callWS = async (message: any) => {
      if (message.request?.operation === 'read' && fail) return { version: 1, ok: false, error: { code: 'internal', detail: 'The store is busy' } }
      return inner(message)
    }
  })
  await vi.waitFor(() => expect(again.root.querySelector('[role="alert"]')?.textContent).toContain('Could not open the entity definition: The store is busy'))
  expect(editorShown(again.root)).toBe(false)
  fail = false
  button(again.root, 'Try again').click()
  await vi.waitFor(() => expect(again.root.querySelector('.hs-save-chip__text')?.textContent).toBe('Saved'))
  expect(nameInput(again.root).value).toBe('Plug')
})

it('mounts a new class definition\'s page once', async () => {
  // Each page made is a section of its own.
  const mounted = new Set<Element>()
  const observer = new MutationObserver(records => {
    for (const record of records) for (const node of record.addedNodes)
      if (node instanceof Element) for (const e of [node, ...node.querySelectorAll('*')]) if (e.matches('section.hs-cdef[aria-label="Entity definition"]')) mounted.add(e)
  })
  observer.observe(document.body, { childList: true, subtree: true })
  const { root, fixture } = setup('/entities/new?logic=class&name=Shelly')
  await vi.waitFor(() => expect(fixture.entities.requests).toEqual([{ operation: 'types' }]))
  await new Promise(resolve => setTimeout(resolve, 20))
  observer.disconnect()
  expect(root.querySelectorAll('section.hs-cdef[aria-label="Entity definition"]')).toHaveLength(1)
  expect(mounted.size).toBe(1)
})

// ---- The six types beyond the first three: their options in the form, their steps, and what the server refuses.
const undo = (root: Element) =>
  root.querySelector('section[aria-label="Entity definition editor"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true, cancelable: true }))
/** Adds an entity with the key `key` of the type `domain`, its row open. */
const addTyped = (root: Element, key: string, domain: string) => {
  button(root, 'Add entity').click()
  type(control(entityRow(root, 'entity'), 'key'), key)
  choose(control(entityRow(root, key), 'domain'), domain)
  return entityRow(root, key)
}
const fieldOf = (row: Element, name: string) => row.querySelector<HTMLElement>(`[data-field="${name}"]`)!
/**
 * For the tests of the Entities list's rows, which Task 11 moves to the Entity node's section with the list's end: the
 * new definition's starter Entity node deleted, and its entity listed instead (a binary sensor keyed and named as
 * the starter was), its row open.
 */
const listStarter = async (root: Element, key: string, name: string) => {
  await selectNode(root, json(root).nodes[0].id)
  button(inspector(root), 'Node actions').click()
  ;[...root.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].find(b => b.textContent === 'Delete')!.click()
  await vi.waitFor(() => expect(json(root).nodes).toEqual([]))
  deselect(root)
  button(root, 'Add entity').click()
  type(control(entityRow(root, 'entity'), 'key'), key)
  type(control(entityRow(root, key), 'name'), name)
  return expand(entityRow(root, key))
}

it('offers all ten types, and saves a button, number, select, text, light and cover with their options typed', async () => {
  const { root, fixture } = setup('/entities/new?name=Things')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  const first = await listStarter(root, 'things', 'Things')
  expect([...control(first, 'domain').options].map(o => o.textContent)).toEqual(['Binary sensor', 'Sensor', 'Switch', 'Button', 'Number', 'Select', 'Text', 'Light', 'Cover', 'Camera'])
  type(control(first, 'key'), 'bell')
  choose(control(entityRow(root, 'bell'), 'domain'), 'button')
  // A number's options: a field each, read as numbers; Shown as a choice; every option has a hint.
  let row = addTyped(root, 'level', 'number')
  expect([...row.querySelectorAll('[data-field^="option:"]')].map(f => [f.querySelector('label')!.textContent, f.querySelector('.hs-field-hint')?.textContent])).toEqual([
    ['Minimum', 'The smallest value it takes; 0 when left empty.'], ['Maximum', 'The largest value it takes; 100 when left empty.'],
    ['Step', 'How far apart the values Home Assistant offers are, above 0; 1 when left empty.'],
    ['Shown as', 'Whether Home Assistant shows a box or a slider; it chooses when left empty.'], ['Unit', 'The unit of its value, for example °C or %.']])
  // No decimal keypad: a minimum can be negative, and some phones' decimal keypad has no minus sign.
  expect(control(row, 'option:min').hasAttribute('inputmode')).toBe(false)
  type(control(row, 'option:min'), '-20')
  expect(json(root).definition.entities.find((e: any) => e.key === 'level').options).toMatchObject({ min: -20 })
  type(control(row, 'option:min'), '5')
  type(control(row, 'option:max'), ' 40 ')
  type(control(row, 'option:step'), '0.5')
  choose(control(row, 'option:mode'), 'slider')
  type(control(row, 'option:unit'), '%')
  row = addTyped(root, 'mode', 'select')
  type(control(row, 'option:options'), 'Eco\n\n Comfort \n')
  row = addTyped(root, 'label', 'text')
  type(control(row, 'option:min'), '1')
  type(control(row, 'option:max'), '20')
  type(control(row, 'option:pattern'), '^[a-z]+$')
  choose(control(row, 'option:mode'), 'password')
  // A light that can dim and a cover with a position: switches.
  row = addTyped(root, 'lamp', 'light')
  expect(control(row, 'option:dimmable').getAttribute('role')).toBe('switch')
  control(row, 'option:dimmable').click()
  row = addTyped(root, 'blind', 'cover')
  control(row, 'option:positionable').click()
  control(row, 'option:positionable').click()
  control(row, 'option:positionable').click()
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(1))
  expect(saves(fixture)[0].definition.entities).toEqual([
    { key: 'bell', domain: 'button', name: 'Things' },
    { key: 'level', domain: 'number', options: { min: 5, max: 40, step: 0.5, mode: 'slider', unit: '%' } },
    { key: 'mode', domain: 'select', options: { options: ['Eco', 'Comfort'] } },
    { key: 'label', domain: 'text', options: { min: 1, max: 20, pattern: '^[a-z]+$', mode: 'password' } },
    { key: 'lamp', domain: 'light', options: { dimmable: true } },
    { key: 'blind', domain: 'cover', options: { positionable: true } },
  ])
  await vi.waitFor(() => expect(chipTip(root)).toBe('Saved revision 1'))
  // Turned off again, an option is left out rather than saved false.
  control(entityRow(root, 'lamp'), 'option:dimmable').click()
  expect(json(root).definition.entities.find((e: any) => e.key === 'lamp')).toEqual({ key: 'lamp', domain: 'light' })
  expectClean(root)
})

it('blocks Save while a required option is empty or a number option is not a number, and says so on the field', async () => {
  const { root, fixture } = setup('/entities/new?name=Mode')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  const row = await listStarter(root, 'mode', 'Mode')
  choose(control(row, 'domain'), 'select')
  // Required: said by the field, and Save waits for it.
  const options = () => fieldOf(entityRow(root, 'mode'), 'option:options')
  expect([...options().querySelectorAll('.hs-field-hint')].map(h => h.textContent)).toEqual(['Ctrl+Enter to apply', 'One per line: every option it offers. Required.'])
  expect(control(row, 'option:options').getAttribute('aria-required')).toBe('true')
  const required = 'Mode needs options: a select cannot work without it'
  // Not in red the moment the type is chosen: Save waits, the Problems tab has it, and the field says it once left.
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(true))
  await new Promise(resolve => setTimeout(resolve, 20))
  expect(options().textContent).not.toContain(required)
  expect(control(row, 'option:options').hasAttribute('aria-invalid')).toBe(false)
  expect(json(root).definition.entities[0]).not.toHaveProperty('options')
  button(root, 'Save').click()
  await new Promise(resolve => setTimeout(resolve, 20))
  expect(saves(fixture)).toEqual([])
  // Chosen in the Problems tab, it shows on its field.
  ;[...root.querySelectorAll<HTMLButtonElement>('.hs-drawer [role="tab"]')].find(b => b.textContent?.startsWith('Problems'))!.click()
  await vi.waitFor(() => expect([...root.querySelectorAll('.hs-problem__message')].map(b => b.textContent)).toContain(required))
  ;[...root.querySelectorAll<HTMLButtonElement>('.hs-problem__message')].find(b => b.textContent === required)!.click()
  await vi.waitFor(() => expect(options().textContent).toContain(required))

  type(control(row, 'option:options'), 'Eco\nComfort')
  await vi.waitFor(() => expect(options().textContent).not.toContain(required))
  // A number option that does not read: kept as typed, said on its field, and Save waits; unsaved work all the while.
  const level = addTyped(root, 'level', 'number')
  type(control(level, 'option:min'), '5,5')
  const message = 'Minimum of level takes a number'
  await vi.waitFor(() => expect(fieldOf(entityRow(root, 'level'), 'option:min').textContent).toContain(message))
  expect(control(entityRow(root, 'level'), 'option:min').value).toBe('5,5')
  expect(control(entityRow(root, 'level'), 'option:min').getAttribute('aria-invalid')).toBe('true')
  expect(button(root, 'Save').disabled).toBe(true)
  // Kept while a step is selected, and when the form shows again (the step's adding then undone).
  dropStep(root, 'State')
  deselect(root)
  expect(control(expand(entityRow(root, 'level')), 'option:min').value).toBe('5,5')
  undo(root)
  expect(json(root).nodes).toEqual([])
  type(control(entityRow(root, 'level'), 'option:min'), '5.5')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  expect(fieldOf(entityRow(root, 'level'), 'option:min').textContent).not.toContain(message)
  button(root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(1))
  expect(saves(fixture)[0].definition.entities).toEqual([
    { key: 'mode', domain: 'select', name: 'Mode', options: { options: ['Eco', 'Comfort'] } }, { key: 'level', domain: 'number', options: { min: 5.5 } }])
})

it('says a required option is missing on its field once the field is left without one', async () => {
  const { root } = setup('/entities/new?name=Mode')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  choose(control(await listStarter(root, 'mode', 'Mode'), 'domain'), 'select')
  const options = () => fieldOf(entityRow(root, 'mode'), 'option:options')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(true))
  await new Promise(resolve => setTimeout(resolve, 20))
  expect(options().querySelector('.hs-error')).toBeNull()
  control(entityRow(root, 'mode'), 'option:options').dispatchEvent(new FocusEvent('focusout', { bubbles: true }))
  await vi.waitFor(() => expect(options().querySelector('.hs-error')?.textContent).toBe('Mode needs options: a select cannot work without it'))
  expect(control(entityRow(root, 'mode'), 'option:options').getAttribute('aria-invalid')).toBe('true')
})

/** Whether the element has the focus of the page it is on. */
const focused = (element: Element) => (element.getRootNode() as Document | ShadowRoot).activeElement === element

it('keeps focus in an option field while another option changes, a switch is turned, or a choice is made', async () => {
  const { root } = setup('/entities/new?name=Level')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  choose(control(await listStarter(root, 'level', 'Level'), 'domain'), 'number')
  const max = control(entityRow(root, 'level'), 'option:max')
  max.focus()
  // Minimum commits (its blur comes as focus has moved on to Maximum).
  const min = control(entityRow(root, 'level'), 'option:min')
  min.value = '5'
  min.dispatchEvent(new Event('input', { bubbles: true }))
  min.dispatchEvent(new FocusEvent('blur'))
  expect(control(entityRow(root, 'level'), 'option:max')).toBe(max)
  expect(focused(max)).toBe(true)
  expect(json(root).definition.entities[0].options).toEqual({ min: 5 })
  // A choice in a select keeps focus on it.
  const mode = control(entityRow(root, 'level'), 'option:mode')
  mode.focus()
  choose(mode, 'slider')
  expect(control(entityRow(root, 'level'), 'option:mode')).toBe(mode)
  expect(focused(mode)).toBe(true)
  expect(json(root).definition.entities[0].options).toEqual({ min: 5, mode: 'slider' })
  // Space on a switch turns it, and focus stays on it.
  choose(control(entityRow(root, 'level'), 'domain'), 'light')
  const dimmable = control(entityRow(root, 'level'), 'option:dimmable')
  dimmable.focus()
  dimmable.dispatchEvent(new KeyboardEvent('keydown', { key: ' ', code: 'Space', bubbles: true }))
  dimmable.click()
  dimmable.dispatchEvent(new KeyboardEvent('keyup', { key: ' ', code: 'Space', bubbles: true }))
  expect(control(entityRow(root, 'level'), 'option:dimmable')).toBe(dimmable)
  expect(focused(dimmable)).toBe(true)
  expect(json(root).definition.entities[0].options).toEqual({ dimmable: true })
})

it('drops the options a new type does not have, or whose value it does not take, in one undo step', async () => {
  const { root } = setup('/entities/new?name=Level')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  const row = await listStarter(root, 'level', 'Level')
  choose(control(row, 'domain'), 'number')
  type(control(row, 'option:min'), '2')
  type(control(row, 'option:unit'), '%')
  choose(control(row, 'option:mode'), 'slider')
  // A text takes a min and a mode, but not a slider; no unit.
  choose(control(entityRow(root, 'level'), 'domain'), 'text')
  expect(json(root).definition.entities[0]).toEqual({ key: 'level', domain: 'text', name: 'Level', options: { min: 2 } })
  expect(control(entityRow(root, 'level'), 'option:min').value).toBe('2')
  expect(control(entityRow(root, 'level'), 'option:mode').value).toBe('')
  undo(root)
  expect(json(root).definition.entities[0]).toEqual({ key: 'level', domain: 'number', name: 'Level', options: { min: 2, unit: '%', mode: 'slider' } })
  // A sensor's list of options becomes a select's.
  choose(control(entityRow(root, 'level'), 'domain'), 'sensor')
  type(control(entityRow(root, 'level'), 'option:options'), 'low\nhigh')
  choose(control(entityRow(root, 'level'), 'domain'), 'select')
  expect(json(root).definition.entities[0].options).toEqual({ options: ['low', 'high'] })
})

it('shows what the server refuses of an option under that option, and keeps Save waiting for it', async () => {
  const { root } = setup('/entities/new?name=Things')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  const row = await listStarter(root, 'things', 'Things')
  choose(control(row, 'domain'), 'text')
  type(control(row, 'option:min'), '30')
  type(control(row, 'option:max'), '300')
  type(control(entityRow(root, 'things'), 'option:pattern'), '[a-')
  const max = 'Longest length of Things takes 255 or less, as Home Assistant keeps at most 255 characters, not 300'
  await vi.waitFor(() => expect(fieldOf(entityRow(root, 'things'), 'option:max').textContent).toContain(max))
  expect(fieldOf(entityRow(root, 'things'), 'option:pattern').textContent).toContain('Pattern of Things is not a regular expression')
  // Not known before the server's check answers; once it has, Save waits for them as for any error.
  expect(button(root, 'Save').disabled).toBe(true)
  expect(entityRow(root, 'things').querySelector('.hs-def-row__body > .hs-def-errors')).toBeNull()
  // A min not below the max is the min's.
  type(control(entityRow(root, 'things'), 'option:max'), '20')
  type(control(entityRow(root, 'things'), 'option:pattern'), '')
  const order = 'Shortest length of Things is 30, which is not below its longest length of 20'
  await vi.waitFor(() => expect(fieldOf(entityRow(root, 'things'), 'option:min').textContent).toContain(order))
  expect(fieldOf(entityRow(root, 'things'), 'option:max').textContent).not.toContain(order)
  // A number's step of 0; a sensor's empty list cannot be typed (an empty field leaves the option out).
  choose(control(entityRow(root, 'things'), 'domain'), 'number')
  type(control(entityRow(root, 'things'), 'option:max'), '')
  type(control(entityRow(root, 'things'), 'option:min'), '')
  type(control(entityRow(root, 'things'), 'option:step'), '0')
  await vi.waitFor(() => expect(fieldOf(entityRow(root, 'things'), 'option:step').textContent).toContain('Step of Things takes a number above 0, not 0'))
})

it('gives a camera its seconds between frames, with its hint, says on the field when it is out of range, and keeps focus there', async () => {
  // The previews the server has answered, so that what it says is counted only once it has said it.
  const answered: any[] = []
  const { root, fixture } = setup('/entities/new?name=Front door', fixture => {
    const answer = fixture.entities.callWS
    fixture.entities.callWS = async (message: any) => {
      const result = await answer(message)
      if (message.request?.operation === 'preview') answered.push(message.request)
      return result
    }
  })
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  choose(control(await listStarter(root, 'front_door', 'Front door'), 'domain'), 'camera')
  const row = () => entityRow(root, 'front_door')
  expect([...row().querySelectorAll('[data-field^="option:"]')].map(f => [f.querySelector('label')!.textContent, f.querySelector('.hs-field-hint')?.textContent])).toEqual([
    ['Seconds between frames', 'How often Home Assistant asks for a new picture while someone is watching; 1 when left empty.']])
  // Below its range: said on its field at once, in the server's words, and Save waits; focus stays in the field.
  const field = control(row(), 'option:frame_interval')
  field.focus()
  field.value = '0.2'
  field.dispatchEvent(new Event('input', { bubbles: true }))
  press(field)
  const below = 'Seconds between frames of Front door takes a number from 0.5 to 60, not 0.2'
  await vi.waitFor(() => expect(fieldOf(row(), 'option:frame_interval').querySelector('.hs-error')?.textContent).toBe(below))
  expect(control(row(), 'option:frame_interval')).toBe(field)
  expect(focused(field)).toBe(true)
  expect(field.getAttribute('aria-invalid')).toBe('true')
  expect(button(root, 'Save').disabled).toBe(true)
  // Said once, though the server says it too: counted once the server has answered a preview of 0.2, and the view has taken its answer.
  await vi.waitFor(() => expect(answered.some(request => JSON.stringify(request).includes('"frame_interval":0.2'))).toBe(true))
  await new Promise(resolve => setTimeout(resolve, 0))
  expect([...root.querySelectorAll('.hs-error')].filter(e => e.textContent === below)).toHaveLength(1)
  type(control(row(), 'option:frame_interval'), '61')
  await vi.waitFor(() => expect(fieldOf(row(), 'option:frame_interval').textContent).toContain('Seconds between frames of Front door takes a number from 0.5 to 60, not 61'))
  // At either end of its range it is taken.
  type(control(row(), 'option:frame_interval'), '0.5')
  await vi.waitFor(() => expect(fieldOf(row(), 'option:frame_interval').querySelector('.hs-error')).toBeNull())
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(1))
  expect(saves(fixture)[0].definition.entities).toEqual([{ key: 'front_door', domain: 'camera', name: 'Front door', options: { frame_interval: 0.5 } }])
})

it('retypes an entity to and from Camera in one undo step, removing what no longer fits', async () => {
  const { root } = setup('/entities/new?name=Front')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  choose(control(await listStarter(root, 'front', 'Front'), 'domain'), 'number')
  type(control(entityRow(root, 'front'), 'option:min'), '2')
  // A number's options go: a camera has none of them.
  choose(control(entityRow(root, 'front'), 'domain'), 'camera')
  expect(json(root).definition.entities[0]).toEqual({ key: 'front', domain: 'camera', name: 'Front' })
  type(control(entityRow(root, 'front'), 'option:frame_interval'), '2')
  expect(json(root).definition.entities[0].options).toEqual({ frame_interval: 2 })
  // A camera's seconds between frames go too when it becomes a switch; one undo step brings it back.
  choose(control(entityRow(root, 'front'), 'domain'), 'switch')
  expect(json(root).definition.entities[0]).toEqual({ key: 'front', domain: 'switch', name: 'Front' })
  expect(entityRow(root, 'front').querySelector('[data-field="option:frame_interval"]')).toBeNull()
  undo(root)
  expect(json(root).definition.entities[0]).toEqual({ key: 'front', domain: 'camera', name: 'Front', options: { frame_interval: 2 } })
  expect(control(entityRow(root, 'front'), 'option:frame_interval').value).toBe('2')
})

it('wires a light\'s Turn on into its Set state, brightness and all, and a number\'s Set value into its own', async () => {
  const { root, fixture } = setup('/entities/new?name=Lamp')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  // The starter's Entity node becomes a light that can dim; a second one is a number.
  const lamp = json(root).nodes[0].id as string
  await selectNode(root, lamp)
  choose(control(inspector(root), 'domain'), 'light')
  control(inspector(root), 'option:dimmable').click()
  const set = addEntityNode(root)
  entityName(root, 'Level')
  type(control(inspector(root), 'key'), 'level')
  choose(control(inspector(root), 'domain'), 'number')
  // A number's Set value: its value an output of the node, into Set state's Value.
  expect([...inspector(root).querySelectorAll('.hs-insp-output__name')].map(o => o.textContent)).toEqual(['Value'])
  deselect(root)
  dropStep(root, 'Set state')
  const setLevel = lastStep(root)
  await pickOption(root, 'entity', 'Level')
  // One state value: ready to fill, as it is all the step sets.
  expect(inspector(root).querySelector('[data-input-id="value"] .hs-insp-unset')).toBeNull()
  wire(root, set, 'command:set_value:value', setLevel, 'value')
  flow(root, set, setLevel, 'command:set_value')
  // The light's Turn on: its brightness an output, into Set state's optional Brightness; On set when the light was chosen.
  await selectNode(root, lamp)
  expect([...inspector(root).querySelectorAll('.hs-insp-output__name')].map(o => o.textContent)).toEqual(['Brightness'])
  const on = lamp
  deselect(root)
  dropStep(root, 'Set state')
  const setOn = lastStep(root)
  await pickOption(root, 'entity', 'Lamp')
  expect(inspector(root).querySelector('[data-input-id="is_on"] .hs-insp-unset')).toBeNull()
  const brightness = inspector(root).querySelector('[data-input-id="brightness"]')!
  expect(brightness.querySelector('.hs-insp-unset')!.textContent).toContain('Not set')
  expect(brightness.textContent).toContain('Leave empty to keep it as it is.')
  expect(inspector(root).querySelector<HTMLInputElement>('[data-input-id="is_on"] input[role="switch"]')!.checked).toBe(true)
  wire(root, on, 'command:turn_on:brightness', setOn, 'brightness')
  flow(root, on, setOn, 'command:turn_on')
  // Turn off sets On alone.
  deselect(root)
  dropStep(root, 'Set state')
  const setOff = lastStep(root)
  await pickOption(root, 'entity', 'Lamp')
  const toggle = () => inspector(root).querySelector<HTMLInputElement>('[data-input-id="is_on"] input[role="switch"]')!
  toggle().click()
  expect(toggle().checked).toBe(false)
  flow(root, lamp, setOff, 'command:turn_off')
  const graph = json(root)
  expect(graph.edges.filter((e: any) => e.kind === 'data').map((e: any) => [e.source, e.sourcePort, e.target, e.targetPort])).toEqual([
    [set, 'command:set_value:value', setLevel, 'value'], [on, 'command:turn_on:brightness', setOn, 'brightness']])
  expect(graph.edges.filter((e: any) => e.kind !== 'data').map((e: any) => [e.source, e.port, e.target])).toEqual([
    [set, 'command:set_value', setLevel], [lamp, 'command:turn_on', setOn], [lamp, 'command:turn_off', setOff]])
  expect(graph.nodes.find((n: any) => n.id === setOn).config).toMatchObject({ entity: 'lamp', is_on: { kind: 'literal', type: 'boolean', value: true } })
  expect(graph.nodes.find((n: any) => n.id === setOff).config).toMatchObject({ entity: 'lamp', is_on: { kind: 'literal', type: 'boolean', value: false } })
  expect(graph.nodes.find((n: any) => n.id === setOff).config).not.toHaveProperty('brightness')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(chipTip(root)).toBe('Saved revision 1'))
  expect(saves(fixture)).toHaveLength(1)
  expect(saves(fixture)[0].definition).not.toHaveProperty('entities')
})

it('shows a problem that has no row of the form at the top of the form, from a step selected', async () => {
  const { root, fixture } = await openPlug()
  fixture.entities.controls.diagnostics = [{ nodeId: null, inputId: null, portId: null, code: 'definition.other', severity: 3, message: 'The definition cannot be made', startLine: null, startColumn: null } as any]
  await renameRelay(root, 'Main relay')
  await vi.waitFor(() => expect(inspector(root).querySelector('[aria-label="Problems of this entity definition"]')?.textContent).toContain('The definition cannot be made'))
  press(root.querySelector('.solid-flow__node[data-id="relay"]')!)
  await vi.waitFor(() => expect(inspector(root).querySelector('.hs-insp-node')).not.toBeNull())
  ;[...root.querySelectorAll<HTMLButtonElement>('.hs-drawer [role="tab"]')].find(b => b.textContent?.startsWith('Problems'))!.click()
  ;[...root.querySelectorAll<HTMLButtonElement>('.hs-problem__message')].find(b => b.textContent === 'The definition cannot be made')!.click()
  await vi.waitFor(() => expect(document.activeElement).toBe(inspector(root).querySelector('[aria-label="Problems of this entity definition"]')))
  expect(inspector(root).querySelector('.hs-insp-node')).toBeNull()
})

it('shows the reason a definition that is on could not start, beside Failed', async () => {
  const { root } = await openPlug(fixture => {
    fixture.entities.controls.activation = 'notLoaded'
    fixture.entities.controls.activationError = 'Downloading firmware.bin failed: its checksum does not match'
    fixture.entities.seed('plug', plugBody())
  })
  await vi.waitFor(() => expect(root.querySelector('[role="alert"]')?.textContent).toContain('Downloading firmware.bin failed: its checksum does not match'))
})

it('adds HTTP request and Download file to an entity definition, with forms that read as their settings say', async () => {
  const { root } = setup('/entities/new?name=Plug')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  dropStep(root, 'HTTP request')
  const field = (id: string) => inspector(root).querySelector<HTMLElement>(`[data-input-id="${id}"]`)!
  const fields = () => [...inspector(root).querySelectorAll<HTMLElement>('[data-input-id]')].map(f => f.dataset.inputId)
  expect(fields()).toEqual(['method', 'responseType', 'timeout', 'failOnStatus', 'hideResponse', 'url', 'headers', 'body'])
  expect([...field('method').querySelectorAll('option')].map(o => o.textContent)).toEqual(['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD'])
  expect([...field('responseType').querySelectorAll('option')].map(o => o.textContent)).toEqual(['Text', 'JSON object', 'JSON list'])
  // The timeout a number of seconds, 10 unless changed; the two choices switches.
  expect(field('timeout').querySelector('input')!.value).toBe('10')
  expect(field('timeout').querySelector('input')!.inputMode).toBe('decimal')
  expect(field('timeout').textContent).toContain('How many seconds the whole answer may take, from 1 to 300')
  expect([field('failOnStatus'), field('hideResponse')].map(f => [f.querySelector('label')!.textContent, f.querySelector<HTMLInputElement>('input[role="switch"]')!.checked]))
    .toEqual([['Fail on an error status', true], ['Keep the response out of run history', false]])
  expect(['url', 'headers', 'body'].map(id => field(id).textContent?.includes(id === 'url' ? 'URL' : id === 'headers' ? 'Headers' : 'Body'))).toEqual([true, true, true])
  expect([...inspector(root).querySelectorAll('.hs-insp-output__name')].map(o => o.textContent)).toEqual(['Status', 'Body', 'Headers'])
  deselect(root)
  dropStep(root, 'Download file')
  expect(fields()).toEqual(['name', 'url', 'sha256'])
  expect(field('sha256').textContent).toContain('SHA-256 checksum')
  expect([...inspector(root).querySelectorAll('.hs-insp-output__name')].map(o => o.textContent)).toEqual(['Path'])
})

// ---- Run now: a Trigger step of the saved definition, started from the top bar as an automation's trigger is.
/** The plug, polled by a Trigger step `poll` (and `others` more steps). */
const pollingPlug = (...others: any[]): DefinitionBody => {
  const body = plugBody()
  if (body.logic.kind === 'graph')
    body.logic.graph.nodes.push({ id: 'poll', kind: 'trigger', label: 'Poll', config: { type: 'ha', trigger: { trigger: 'time_pattern', seconds: '/10' } }, position: { x: 60, y: 300 } }, ...others)
  return body
}
const runNow = (root: Element) => root.querySelector<HTMLButtonElement>('.hs-topbar__run')
const topNotice = (root: Element) => root.querySelector('.hs-topbar__notice')

it("runs a Trigger step of the saved, running definition from Run now, and its run shows under Runs as an automation's does", async () => {
  const { root, fixture } = await openPlug(f => f.entities.seed('plug', pollingPlug()))
  const run = runNow(root)!
  expect(run.textContent?.trim()).toBe('Run now')
  expect(run.disabled).toBe(false)
  expect(run.title).toBe('Run the saved entity definition’s Trigger step now')
  expect(run.getAttribute('aria-haspopup')).toBeNull()
  const histories = () => fixture.calls.filter((c: any) => c.type === 'hass_sharp/run_history').length
  await vi.waitFor(() => expect(histories()).toBeGreaterThan(0))
  const before = histories()
  // While its request is in flight it takes no second press and keeps the focus.
  const inner = fixture.callWS
  let release!: () => void
  fixture.callWS = async (message: any) => {
    if (message.request?.operation === 'run') await new Promise<void>(resolve => { release = resolve })
    return inner(message)
  }
  run.focus()
  run.click()
  await vi.waitFor(() => expect(run.getAttribute('aria-disabled')).toBe('true'))
  expect(run.disabled).toBe(false)
  expect(document.activeElement).toBe(run)
  run.click()
  fixture.callWS = inner
  release()
  await vi.waitFor(() => expect(topNotice(root)?.textContent).toBe('Run started. Its result is under Runs.'))
  expect(fixture.entities.requests.filter(r => r.operation === 'run')).toEqual([{ operation: 'run', id: 'plug', triggerId: 'poll' }])
  expect(fixture.entities.controls.runs).toEqual([{ id: 'plug', triggerId: 'poll' }])
  expect(fixture.calls.some((c: any) => c.type === 'hass_sharp/run_now' || c.type === 'hass_sharp/visual')).toBe(false)
  expect(run.getAttribute('aria-disabled')).toBeNull()
  // The run history is asked for again, so the new run shows.
  await vi.waitFor(() => expect(histories()).toBeGreaterThan(before))
  expectClean(root)
  // Refused, it says why in the server's words.
  fixture.entities.documents.get('plug')!.enabled = false
  run.click()
  await vi.waitFor(() => expect(topNotice(root)?.textContent).toBe('Run request failed: The entity definition is off'))
  expect(topNotice(root)!.getAttribute('role')).toBe('alert')
})

it('offers a menu of the Trigger steps that can be started with Run now, named and summarised as on their cards', async () => {
  const { root, fixture } = await openPlug(f => f.entities.seed('plug', pollingPlug(
    { id: 'nightly', kind: 'trigger', label: 'Nightly', config: { type: 'daily', time: '03:00', timeZone: 'UTC' }, position: { x: 60, y: 500 } },
    { id: 'quiet', kind: 'trigger', label: 'Quiet', config: { type: 'manual', manual: false }, position: { x: 60, y: 700 } })))
  const run = runNow(root)!
  expect(run.getAttribute('aria-haspopup')).toBe('menu')
  expect(run.title).toBe('Choose a Trigger step to run')
  run.click()
  const items = () => [...root.querySelectorAll<HTMLButtonElement>('[role="menu"][aria-label="Run which Trigger step"] [role="menuitem"]')]
  expect(items().map(item => item.querySelector('strong')!.textContent)).toEqual(['Poll', 'Nightly'])
  expect(items()[1].querySelector('small')!.textContent).toContain('03:00')
  expect(document.activeElement).toBe(items()[0])
  items()[1].click()
  await vi.waitFor(() => expect(topNotice(root)?.textContent).toBe('Run of Nightly started. Its result is under Runs.'))
  expect(fixture.entities.controls.runs).toEqual([{ id: 'plug', triggerId: 'nightly' }])
  expectClean(root)
})

it('offers Run now only while the saved definition runs, without unsaved changes, and says why not in its own words', async () => {
  // No Trigger step: nothing to run, no Run now.
  let page = await openPlug()
  expect(runNow(page.root)).toBeNull()
  dispose!(); document.body.innerHTML = ''
  // Never saved.
  page = setup('/entities/new?name=Poller')
  await vi.waitFor(() => expect(button(page.root, 'Save').disabled).toBe(false))
  dropStep(page.root, 'Trigger')
  await vi.waitFor(() => expect(runNow(page.root)).not.toBeNull())
  expect(runNow(page.root)!.disabled).toBe(true)
  expect(runNow(page.root)!.title).toBe('Save the entity definition to run this step')
  dispose!(); document.body.innerHTML = ''
  // Saved, running: unsaved changes take it away until they are undone.
  page = await openPlug(f => f.entities.seed('plug', pollingPlug()))
  const { root, fixture } = page
  const run = runNow(root)!
  rename(root, 'Socket')
  await vi.waitFor(() => expect(run.disabled).toBe(true))
  expect(run.title).toBe('Save the entity definition to run this step')
  run.click()
  undo(root)
  await vi.waitFor(() => expect(run.disabled).toBe(false))
  // Off: it does not run (unlike an automation that is off).
  const toggle = root.querySelector<HTMLInputElement>('input[aria-label="Plug on"]')!
  toggle.click()
  await vi.waitFor(() => expect(run.title).toBe('Turn the entity definition on to run this step'))
  expect(run.disabled).toBe(true)
  await vi.waitFor(() => expect(fixture.entities.documents.get('plug')!.enabled).toBe(false))
  await new Promise(resolve => setTimeout(resolve, 0))
  // On again, it is read again to learn when it runs.
  const reads = fixture.entities.requests.filter(r => r.operation === 'read').length
  fixture.entities.controls.activation = 'setup'
  toggle.click()
  await vi.waitFor(() => expect(fixture.entities.requests.filter(r => r.operation === 'read').length).toBeGreaterThan(reads))
  await vi.waitFor(() => expect(run.title).toBe('The entity definition is still starting'))
  expect(run.disabled).toBe(true)
  fixture.entities.documents.set('plug', { ...fixture.entities.documents.get('plug')!, status: 'running' })
  await vi.waitFor(() => expect(run.disabled).toBe(false), { timeout: 4000 })
  expect(fixture.entities.controls.runs).toEqual([])
  expectClean(root)
  dispose!(); document.body.innerHTML = ''
  // Failed to start, and a Trigger step that cannot be started with Run now.
  page = await openPlug(f => { f.entities.controls.activation = 'failed'; f.entities.seed('plug', pollingPlug()) })
  expect(runNow(page.root)!.title).toBe('The entity definition is not running')
  expect(runNow(page.root)!.disabled).toBe(true)
  dispose!(); document.body.innerHTML = ''
  const quiet = pollingPlug()
  if (quiet.logic.kind === 'graph') quiet.logic.graph.nodes.at(-1)!.config.manual = false
  page = await openPlug(f => f.entities.seed('plug', quiet))
  expect(runNow(page.root)!.title).toBe('No Trigger step can be started with Run now')
  expect(runNow(page.root)!.disabled).toBe(true)
})

it('describes the Trigger step for a definition in the step library and the inspector, and says Run now is recorded', async () => {
  const { root } = await openPlug(f => f.entities.seed('plug', pollingPlug()))
  const described = 'Run the steps after it when something happens, at a set time or on an interval.'
  expect(root.querySelector('.hs-lib__item[aria-label="Trigger"] small')!.textContent).toBe(described)
  press(card(root, 'poll'))
  await vi.waitFor(() => expect(inspector(root).querySelector('.hs-insp-desc')?.textContent).toBe(described))
  const history = [...inspector(root).querySelectorAll<HTMLLabelElement>('label')].find(l => l.querySelector('strong')?.textContent === 'Track in run history')!
  expect(history.querySelector('.hs-field-hint')!.textContent).toBe('Record automatic runs and their traces. Run now is always recorded.')
  more(root, 'Help')
  const help = document.querySelector('dialog[open]')!.textContent!
  expect(help).toContain('Run now in the top bar starts a Trigger step by hand while the saved definition is on and running')
  expect(help).toContain('Part of')
})

// ---- Part of: the device a definition's device is connected through.
const device = (id: string, name: string, area_id: string | null = null) => ({ id, name, area_id })
/** The registry with a device for the plug's relay, and two Home Assistant devices, one of them in an area. */
const devicesRegistry = () => ({
  ...registry,
  entities: [...registry.entities, { ...registryEntity('switch.plug_relay', 'Relay'), device_id: 'plug-device' as string | null }],
  areas: [{ area_id: 'loft', name: 'Loft', floor_id: null }],
  devices: [device('zigbee', 'Zigbee coordinator'), device('plug-device', 'Plug'), device('attic', 'Attic router', 'loft')],
})
const partOf = (root: Element) => control(root.querySelector('[aria-label="Device"]')!, 'via') as HTMLSelectElement
const shownChoices = (select: HTMLSelectElement) => [...select.options].map(o => o.textContent)

it('makes the device part of another definition that is a device, or of a Home Assistant device, and shows it again when opened', async () => {
  const seeds = (f: Fixture) => {
    f.entities.seed('hub', { ...newGraphDefinition('Hub'), device: { manufacturer: 'Acme' } })
    f.entities.seed('lonely', newGraphDefinition('Lonely sensor'))
    f.entities.seed('bridge', { ...newGraphDefinition('Bridge'), device: {} })
  }
  const { root, fixture } = await openPlug(seeds, undefined, devicesRegistry())
  await vi.waitFor(() => expect(shownChoices(partOf(root))).toEqual(['Nothing', 'Bridge', 'Hub', 'A Home Assistant device…']))
  expect(partOf(root).value).toBe('')
  choose(partOf(root), 'definition:hub')
  expect(json(root).definition.device).toMatchObject({ manufacturer: 'Acme', model: 'P1', via: { definition: 'hub' } })
  expect(json(root).definition.device.via).toEqual({ definition: 'hub' })
  // A device definition saved since the list was read (well within its 30 s) is offered once a save makes the lists stale.
  fixture.entities.seed('router', { ...newGraphDefinition('Router'), device: {} })
  button(root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture).at(-1)!.definition.device).toMatchObject({ manufacturer: 'Acme', model: 'P1', via: { definition: 'hub' } }))
  await vi.waitFor(() => expect(root.querySelector('.hs-save-chip__text')?.textContent).toBe('Saved'))
  await vi.waitFor(() => expect(shownChoices(partOf(root))).toEqual(['Nothing', 'Bridge', 'Hub', 'Router', 'A Home Assistant device…']))
  dispose!(); document.body.innerHTML = ''
  const again = await openPlug(undefined, fixture, devicesRegistry())
  await vi.waitFor(() => expect(partOf(again.root).value).toBe('definition:hub'))
  expect(partOf(again.root).selectedOptions[0].textContent).toBe('Hub')
  // A Home Assistant device: the device picker, by name, without the plug's own device. Until one is picked the
  // device is still part of Hub, and the form says so; the focus stays on Part of, and Tab goes on to the picker.
  partOf(again.root).focus()
  choose(partOf(again.root), 'device')
  const picker = () => again.root.querySelector<HTMLInputElement>('[aria-label="Device"] input[role="combobox"][aria-label="Home Assistant device"]')!
  await vi.waitFor(() => expect(picker()).not.toBeNull())
  expect(document.activeElement).toBe(partOf(again.root))
  const focusable = [...again.root.querySelectorAll<HTMLElement>('[aria-label="Device"] :is(input, select, textarea, button)')]
  expect(focusable[focusable.indexOf(partOf(again.root)) + 1]).toBe(picker())
  expect(json(again.root).definition.device.via).toEqual({ definition: 'hub' })
  expect(again.root.querySelector('[aria-label="Device"] [data-field="viaDevice"] .hs-field-hint')!.textContent).toBe('Still part of Hub until you pick a device.')
  picker().click()
  const options = () => [...again.root.querySelectorAll<HTMLElement>('[aria-label="Device"] [role="option"]')]
  expect(options().map(o => o.querySelector('span')!.textContent)).toEqual(['Attic router', 'Zigbee coordinator'])
  // Each with its area, as the device picker of a step shows it.
  expect(options().map(o => o.querySelector('small')?.textContent ?? null)).toEqual(['Loft', null])
  options()[1].click()
  expect(json(again.root).definition.device.via).toEqual({ deviceId: 'zigbee' })
  expect(partOf(again.root).value).toBe('device')
  expect(again.root.querySelector('[aria-label="Device"] [data-field="viaDevice"] .hs-field-hint')).toBeNull()
  button(again.root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture).at(-1)!.definition.device.via).toEqual({ deviceId: 'zigbee' }))
  await vi.waitFor(() => expect(again.root.querySelector('.hs-save-chip__text')?.textContent).toBe('Saved'))
  dispose!(); document.body.innerHTML = ''
  const third = await openPlug(undefined, fixture, devicesRegistry())
  await vi.waitFor(() => expect(partOf(third.root).value).toBe('device'))
  await vi.waitFor(() => expect(third.root.querySelector<HTMLInputElement>('[aria-label="Device"] input[aria-label="Home Assistant device"]')!.value).toBe('Zigbee coordinator'))
  // Nothing: the device is part of nothing, the other details as they were.
  choose(partOf(third.root), '')
  expect(json(third.root).definition.device).toMatchObject({ manufacturer: 'Acme', model: 'P1' })
  expect(json(third.root).definition.device).not.toHaveProperty('via')
  expect(third.root.querySelector('[aria-label="Device"] input[aria-label="Home Assistant device"]')).toBeNull()
  expectClean(third.root)
})

it('shows a parent definition that no longer exists as such, which can be cleared', async () => {
  const body = plugBody()
  body.device = { manufacturer: 'Acme', via: { definition: 'gone', deviceId: null } }
  const { root } = await openPlug(f => f.entities.seed('plug', body), undefined, devicesRegistry())
  await vi.waitFor(() => expect(partOf(root).selectedOptions[0]?.textContent).toBe('A definition that no longer exists'))
  expect(partOf(root).value).toBe('definition:gone')
  choose(partOf(root), '')
  expect(json(root).definition.device).toMatchObject({ manufacturer: 'Acme' })
  expect(json(root).definition.device).not.toHaveProperty('via')
  expect(shownChoices(partOf(root))).toEqual(['Nothing', 'A Home Assistant device…'])
  undo(root)
  expect(json(root).definition.device.via).toEqual({ definition: 'gone', deviceId: null })
})

it('stops reading a definition that does not start, then says it is not running, and a save reads it again', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const { root, fixture } = await openPlug(f => { f.entities.controls.activation = 'setup'; f.entities.seed('plug', pollingPlug()) })
  const run = runNow(root)!
  const reads = () => fixture.entities.requests.filter(r => r.operation === 'read').length
  await vi.waitFor(() => expect(run.title).toBe('The entity definition is still starting'))
  const first = reads()
  for (let i = 0; i < 20; i++) await vi.advanceTimersByTimeAsync(2000)
  // At most 15 reads, about 30 s: then what the last read said, never "still starting".
  expect(reads() - first).toBeLessThanOrEqual(15)
  expect(reads() - first).toBeGreaterThanOrEqual(10)
  await vi.waitFor(() => expect(run.title).toBe('The entity definition is not running'))
  const after = reads()
  await vi.advanceTimersByTimeAsync(10_000)
  expect(reads()).toBe(after)
  // A save is a new answer: it is read again until it runs.
  fixture.entities.controls.activation = 'running'
  rename(root, 'Socket')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(run.disabled).toBe(false))
  expect(run.title).toBe('Run the saved entity definition’s Trigger step now')
})

it('stops waiting when every read fails, and says the definition is not running', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const { root, fixture } = await openPlug(f => { f.entities.controls.activation = 'setup'; f.entities.seed('plug', pollingPlug()) })
  const inner = fixture.callWS
  let failed = 0
  fixture.callWS = async (message: any) => {
    if (message.request?.operation === 'read') { failed++; throw new Error('offline') }
    return inner(message)
  }
  const run = runNow(root)!
  for (let i = 0; i < 20; i++) await vi.advanceTimersByTimeAsync(2000)
  expect(failed).toBeLessThanOrEqual(16)
  await vi.waitFor(() => expect(run.title).toBe('The entity definition is not running'))
})

it('reads the definition again when it was switched elsewhere, so its switch says what the server says', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const { root, fixture } = await openPlug(f => f.entities.seed('plug', pollingPlug()))
  const toggle = () => root.querySelector<HTMLInputElement>('input[aria-label="Plug on"]')!
  toggle().click()
  await vi.waitFor(() => expect(fixture.entities.documents.get('plug')!.enabled).toBe(false))
  await vi.advanceTimersByTimeAsync(0)
  toggle().click()
  await vi.waitFor(() => expect(fixture.entities.documents.get('plug')!.enabled).toBe(true))
  // Turned off again elsewhere (by an assistant, say) before this page's read.
  fixture.entities.documents.set('plug', { ...fixture.entities.documents.get('plug')!, enabled: false, status: 'off', active: false, currentRevisionActive: false, activationError: 'Not active', liveEntities: [] })
  for (let i = 0; i < 20; i++) await vi.advanceTimersByTimeAsync(2000)
  await vi.waitFor(() => expect(toggle().checked).toBe(false))
  await vi.waitFor(() => expect(runNow(root)!.title).toBe('Turn the entity definition on to run this step'))
  // Saved again elsewhere: read again at once.
  dispose!(); document.body.innerHTML = ''
  const page = await openPlug(f => f.entities.seed('plug', pollingPlug()))
  const plug = () => page.fixture.entities.documents.get('plug')!
  page.root.querySelector<HTMLInputElement>('input[aria-label="Plug on"]')!.click()
  await vi.waitFor(() => expect(plug().enabled).toBe(false))
  await vi.advanceTimersByTimeAsync(0)
  page.fixture.entities.documents.set('plug', { ...plug(), revision: 2 })
  page.root.querySelector<HTMLInputElement>('input[aria-label="Plug on"]')!.click()
  await vi.advanceTimersByTimeAsync(2000)
  await vi.waitFor(() => expect(page.root.querySelector('.hs-save-chip')!.getAttribute('title')).toBe('Saved revision 2'))
  expect(page.root.querySelector<HTMLInputElement>('input[aria-label="Plug on"]')!.checked).toBe(true)
})

it("names a parent as it is called, until a device is picked, and a parent that is gone as such", async () => {
  const pending = (root: Element) => root.querySelector('[aria-label="Device"] [data-field="viaDevice"] .hs-field-hint')?.textContent
  const body = plugBody()
  body.device = { via: { definition: 'room' } }
  const { root } = await openPlug(f => { f.entities.seed('room', { ...newGraphDefinition('A Room Hub'), device: {} }); f.entities.seed('plug', body) }, undefined, devicesRegistry())
  await vi.waitFor(() => expect(partOf(root).selectedOptions[0]?.textContent).toBe('A Room Hub'))
  choose(partOf(root), 'device')
  await vi.waitFor(() => expect(pending(root)).toBe('Still part of A Room Hub until you pick a device.'))
  dispose!(); document.body.innerHTML = ''
  const gone = plugBody()
  gone.device = { via: { definition: 'gone' } }
  const again = await openPlug(f => f.entities.seed('plug', gone), undefined, devicesRegistry())
  await vi.waitFor(() => expect(partOf(again.root).selectedOptions[0]?.textContent).toBe('A definition that no longer exists'))
  choose(partOf(again.root), 'device')
  await vi.waitFor(() => expect(pending(again.root)).toBe('Still part of a definition that no longer exists until you pick a device.'))
})

it('reads a definition again each time it is switched on, after an earlier switch used up its reads', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const { root, fixture } = await openPlug(f => f.entities.seed('plug', pollingPlug()))
  const toggle = () => root.querySelector<HTMLInputElement>('input[aria-label="Plug on"]')!
  const stored = () => fixture.entities.documents.get('plug')!
  const reads = () => fixture.entities.requests.filter(r => r.operation === 'read').length
  const cycle = async () => {
    toggle().click()
    await vi.waitFor(() => expect(stored().enabled).toBe(false))
    await vi.advanceTimersByTimeAsync(0)
    toggle().click()
    await vi.waitFor(() => expect(stored().enabled).toBe(true))
    await vi.advanceTimersByTimeAsync(0)
  }
  // Switched off and on, it does not start: its reads are used up, and it is not running.
  fixture.entities.controls.activation = 'setup'
  await cycle()
  for (let i = 0; i < 20; i++) await vi.advanceTimersByTimeAsync(2000)
  await vi.waitFor(() => expect(runNow(root)!.title).toBe('The entity definition is not running'))
  // Switched off and on again to retry: read again until it starts.
  await cycle()
  const before = reads()
  await vi.advanceTimersByTimeAsync(2000)
  fixture.entities.documents.set('plug', { ...stored(), status: 'running', active: true, currentRevisionActive: true, activationError: null })
  for (let i = 0; i < 3; i++) await vi.advanceTimersByTimeAsync(2000)
  expect(reads() - before).toBeGreaterThan(1)
  await vi.waitFor(() => expect(runNow(root)!.disabled).toBe(false))
})
