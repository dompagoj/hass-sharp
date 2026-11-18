import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { A, MemoryRouter, Route, createMemoryHistory } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { EntityDefinitionView } from '../src/views/EntityDefinitionView'
import { newGraphDefinition, type DefinitionBody } from '../src/entities/model'
import { visualFixture } from './visual-fixture'
import { icons } from '../src/visual/icons'

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
/** The page at `path`, over a new in-memory backend (or `fixture`, to open the page again on what it saved). */
function setup(path: string, prepare?: (fixture: Fixture) => void, fixture: Fixture = visualFixture()) {
  prepare?.(fixture)
  const callWS = (message: any) => (message.type === 'hass_sharp/get_entities' ? Promise.resolve(structuredClone(registry)) : fixture.callWS(message))
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

/** The phase 2 example of the derived motion sensor (code map, section 3). */
const motionExample: DefinitionBody = {
  name: 'Any motion', device: null, settings: [],
  entities: [{ key: 'any_motion', domain: 'binary_sensor', name: 'Any motion' }],
  logic: { kind: 'graph', graph: { version: 5, name: 'Any motion',
    nodes: [
      { id: 'a', kind: 'entity_is', config: { entity: { kind: 'literal', type: 'entity_id', value: 'binary_sensor.motion_a' }, state: { kind: 'literal', type: 'string', value: 'on' } }, label: 'Has state ▸ motion A' },
      { id: 'b', kind: 'entity_is', config: { entity: { kind: 'literal', type: 'entity_id', value: 'binary_sensor.motion_b' }, state: { kind: 'literal', type: 'string', value: 'on' } }, label: 'Has state ▸ motion B' },
      { id: 'or', kind: 'or', config: { left: { kind: 'unset' }, right: { kind: 'unset' } } },
      { id: 's', kind: 'entity_state', config: { entity: 'any_motion' }, label: 'State ▸ any motion' }],
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
  // The definition's name names its first entity until that entity is edited.
  rename(root, 'Any motion')
  const ids: string[] = []
  for (const [entity, text] of [['binary_sensor.motion_a', 'Has state ▸ motion A'], ['binary_sensor.motion_b', 'Has state ▸ motion B']]) {
    button(root, 'Add Has state').click()
    ids.push(lastStep(root))
    await pickEntity(root, entity)
    label(root, text)
  }
  button(root, 'Add Or').click()
  ids.push(lastStep(root))
  label(root, '')
  // The definition's only entity is chosen for a new State (ruling R12).
  button(root, 'Add State').click()
  ids.push(lastStep(root))
  label(root, 'State ▸ any motion')
  wire(root, ids[0], 'value', ids[2], 'left')
  wire(root, ids[1], 'value', ids[2], 'right')
  wire(root, ids[2], 'value', ids[3], 'is_on')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(1))
  const sent = saves(fixture)[0]
  // The id comes from the name (ruling R4); a new definition replaces revision 0.
  expect(sent).toMatchObject({ id: 'any_motion', expectedRevision: 0 })
  // As it goes over the websocket (JSON: a cleared label is no member), with the page's ids and positions.
  const body = normalised(JSON.parse(JSON.stringify(sent.definition)), ['a', 'b', 'or', 's'])
  // The page writes two things the hand-written example leaves out, each meaning what leaving it out means: the
  // State's run history at its default (off, as the server takes it), and the wired input as `unset` (as the example's
  // Or has its two).
  const state = body.logic.graph.nodes[3]
  expect(state.config).toEqual({ entity: 'any_motion', trackHistory: false, is_on: { kind: 'unset' } })
  state.config = { entity: 'any_motion' }
  expect(body).toEqual(motionExample)
  await vi.waitFor(() => expect(history.get()).toBe('/entities/any_motion'))
  expect(fixture.entities.documents.get('any_motion')!.entities).toEqual(motionExample.entities)
  expectClean(root)

  // Opened again, it shows what was saved, with its entity's id in Home Assistant and its key fixed.
  const sentGraph = JSON.parse(JSON.stringify(sent.definition.logic.graph))
  dispose!(); document.body.innerHTML = ''
  const again = setup('/entities/any_motion', undefined, fixture)
  await vi.waitFor(() => expect(again.root.querySelector('.hs-save-chip__text')?.textContent).toBe('Saved'))
  expect(nameInput(again.root).value).toBe('Any motion')
  const { definition, ...logic } = json(again.root)
  expect(definition).toEqual({ entities: motionExample.entities, settings: [], device: null })
  expect(logic.nodes.map((n: any) => n.id)).toEqual(sentGraph.nodes.map((n: any) => n.id))
  expect(logic.edges).toEqual(sentGraph.edges)
  const row = expand(entityRow(again.root, 'any_motion'))
  expect(row.querySelector('.hs-def-row__id code')!.textContent).toBe('binary_sensor.any_motion_any_motion')
  expect(control(row, 'key').readOnly).toBe(true)
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
/** Picks one of the definition's entities (or a command) in the selected step's picker, by its label. */
const pickOption = async (root: Element, field: string, text: string) => {
  const box = inspector(root).querySelector<HTMLInputElement>(`[data-input-id="${field}"] input[role="combobox"]`)!
  box.click()
  await vi.waitFor(() => expect(inspector(root).querySelector(`[data-input-id="${field}"] [role="listbox"]`)).not.toBeNull())
  ;[...inspector(root).querySelectorAll<HTMLElement>(`[data-input-id="${field}"] [role="option"]`)].find(o => o.firstElementChild?.textContent === text)!.click()
}

it('builds a polled plug: a time-pattern trigger sets its power, two When commanded flows answer its relay, with a host and a secret password', async () => {
  const { root, fixture } = setup('/entities/new?name=Plug')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  // The starter's entity, named after the definition, becomes the relay.
  const first = expand(entityRow(root, 'plug'))
  type(control(first, 'name'), 'Relay')
  type(control(first, 'key'), 'relay')
  const relay = entityRow(root, 'relay')
  choose(control(relay, 'domain'), 'switch')
  type(control(relay, 'icon'), 'mdi:power-socket')
  button(root, 'Add entity').click()
  let row = entityRow(root, 'entity')
  type(control(row, 'key'), 'power')
  row = entityRow(root, 'power')
  choose(control(row, 'domain'), 'sensor')
  type(control(row, 'name'), 'Power')
  type(control(row, 'deviceClass'), 'power')
  type(control(row, 'option:unit'), 'W')
  choose(control(row, 'option:stateClass'), 'measurement')
  button(root, 'Add entity').click()
  row = entityRow(root, 'entity')
  type(control(row, 'key'), 'online')
  row = entityRow(root, 'online')
  type(control(row, 'name'), 'Online')
  type(control(row, 'deviceClass'), 'connectivity')
  choose(control(row, 'entityCategory'), 'diagnostic')
  const device = root.querySelector<HTMLElement>('[aria-label="Device"]')!
  control(device, 'device').click()
  type(control(device, 'manufacturer'), 'Acme')
  type(control(device, 'model'), 'P1')
  button(root, 'Add setting').click()
  row = settingRow(root, 'setting')
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
  button(root, 'Add Trigger').click()
  const poll = lastStep(root)
  button(inspector(root), 'Change trigger type').click()
  ;[...inspector(root).querySelectorAll<HTMLElement>('.hs-when__choice')].find(c => c.textContent?.includes('Time pattern'))!.click()
  type(inspector(root).querySelector<HTMLInputElement>('input[aria-label="Minutes"]')!, '/10')
  button(root, 'Add Set state').click()
  const setPower = lastStep(root)
  await pickOption(root, 'entity', 'Power')
  choose(inspector(root).querySelector<HTMLSelectElement>('[data-input-id="value"] select[aria-label="Value kind"]')!, 'number')
  type(inspector(root).querySelector<HTMLInputElement>('[data-input-id="value"] input[aria-label="Value"]')!, '42')
  flow(root, poll, setPower)
  // Turning the relay on and off: a When commanded each, the relay pre-chosen (the only entity that takes commands).
  const commands: string[] = []
  for (const command of ['Turn on', 'Turn off']) {
    button(root, 'Add When commanded').click()
    const when = lastStep(root)
    commands.push(when)
    if (command === 'Turn off') await pickOption(root, 'command', 'Turn off')
    button(root, 'Add Set state').click()
    const set = lastStep(root)
    await pickOption(root, 'entity', 'Relay')
    const on = inspector(root).querySelector<HTMLInputElement>('[data-input-id="is_on"] input[role="switch"]')!
    on.click()
    if (command === 'Turn off') inspector(root).querySelector<HTMLInputElement>('[data-input-id="is_on"] input[role="switch"]')!.click()
    flow(root, when, set)
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
  expect(sent.definition.entities).toEqual([
    { key: 'relay', domain: 'switch', name: 'Relay', icon: 'mdi:power-socket' },
    { key: 'power', domain: 'sensor', name: 'Power', deviceClass: 'power', options: { unit: 'W', stateClass: 'measurement' } },
    { key: 'online', domain: 'binary_sensor', name: 'Online', deviceClass: 'connectivity', entityCategory: 'diagnostic' },
  ])
  expect(sent.definition.device).toEqual({ manufacturer: 'Acme', model: 'P1' })
  // The typed secret goes to Home Assistant in the save, and nowhere else.
  expect(sent.definition.settings).toEqual([
    { key: 'host', type: 'string', value: '10.0.0.7', label: 'Host' },
    { key: 'password', type: 'string', value: 'hunter2', label: 'Password', secret: true },
  ])
  const graph = sent.definition.logic.graph
  expect(graph.definition).toBeUndefined()
  expect(graph.nodes.map((n: any) => [n.kind, n.config])).toEqual([
    ['trigger', { mode: 'Single', manual: true, trackHistory: false, type: 'ha', trigger: { trigger: 'time_pattern', minutes: '/10' } }],
    ['set_entity_state', { entity: 'power', value: { kind: 'literal', type: 'number', value: 42 } }],
    ['entity_command', { trackHistory: true, entity: 'relay', command: 'turn_on' }],
    ['set_entity_state', { entity: 'relay', is_on: { kind: 'literal', type: 'boolean', value: true } }],
    ['entity_command', { trackHistory: true, entity: 'relay', command: 'turn_off' }],
    ['set_entity_state', { entity: 'relay', is_on: { kind: 'literal', type: 'boolean', value: false } }],
  ])
  const ids = graph.nodes.map((n: any) => n.id)
  expect(graph.edges.map((e: any) => [e.kind ?? 'execution', ids.indexOf(e.source), ids.indexOf(e.target)])).toEqual([['execution', 0, 1], ['execution', 2, 3], ['execution', 4, 5]])
  await vi.waitFor(() => expect(root.querySelector('.hs-save-chip__text')!.textContent).toBe('Saved'))
  // Saved: the store has the secret; the page holds it no longer, and says it is saved.
  expect(fixture.entities.documents.get('plug')!.settings[1].value).toBe('hunter2')
  deselect(root)
  expect(control(expand(settingRow(root, 'password')), 'value').placeholder).toBe('Saved; type to replace')
  expect(root.innerHTML).not.toContain('hunter2')
  expect(JSON.stringify(json(root))).not.toContain('hunter2')
  // Each entity now shows the entity id Home Assistant gave it.
  await vi.waitFor(() => expect(entityRow(root, 'power').querySelector('.hs-def-row__id code')?.textContent).toBe('sensor.plug_power'))
})

/** The plug as phase 2 stores it, with a secret password, saved at revision 1 and running. */
const plugBody = (): DefinitionBody => ({
  name: 'Plug',
  device: { manufacturer: 'Acme', model: 'P1' },
  settings: [{ key: 'host', label: 'Host', type: 'string', value: '10.0.0.7' }, { key: 'password', label: 'Password', type: 'string', secret: true, value: 'stored-secret' }],
  entities: [{ key: 'relay', domain: 'switch', name: 'Relay' }, { key: 'power', domain: 'sensor', name: 'Power', options: { unit: 'W' } }],
  logic: { kind: 'graph', graph: { version: 5, name: 'Plug', nodes: [{ id: 'on', kind: 'entity_command', config: { entity: 'relay', command: 'turn_on' }, position: { x: 60, y: 100 } }], edges: [] } },
})
const openPlug = async (prepare?: (fixture: Fixture) => void, fixture?: Fixture) => {
  const page = setup('/entities/plug', f => { if (!f.entities.documents.has('plug')) f.entities.seed('plug', plugBody()); prepare?.(f) }, fixture)
  await vi.waitFor(() => expect(page.root.querySelector('.hs-save-chip__text')?.textContent).toBe('Saved'))
  return page
}
const chipTip = (root: Element) => root.querySelector('.hs-save-chip')!.getAttribute('title')

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
  // Its entities are still known from Home Assistant's registry.
  expect(entityRow(root, 'relay').querySelector('.hs-def-row__id code')!.textContent).toBe('switch.plug_relay')
})

it('deletes a saved definition after a confirmation that says its entities leave Home Assistant, then shows the entities page', async () => {
  const { root, fixture, history } = await openPlug()
  const remove = button(inspector(root), 'Delete entity definition')
  remove.click()
  const dialog = document.querySelector<HTMLDialogElement>('dialog[open]')!
  expect(dialog.textContent).toContain('Plug and its entities are removed from Home Assistant, with their device if it has one. This cannot be undone.')
  button(dialog, 'Delete').click()
  await vi.waitFor(() => expect(history.get()).toBe('/entities'))
  expect(fixture.entities.requests.at(-1)).toEqual({ operation: 'delete', id: 'plug', expectedRevision: 1 })
  expect(fixture.entities.documents.has('plug')).toBe(false)
  expect(root.querySelector('h1')!.textContent).toBe('Entities')
})

it('offers no delete while there are unsaved changes, and none for a definition never saved', async () => {
  const { root } = await openPlug()
  type(control(expand(entityRow(root, 'relay')), 'name'), 'Main relay')
  expect(button(inspector(root), 'Delete entity definition').disabled).toBe(true)
  expect(inspector(root).textContent).toContain('Save or undo your changes before deleting it.')
  dispose!(); document.body.innerHTML = ''
  const fresh = setup('/entities/new')
  await vi.waitFor(() => expect(button(fresh.root, 'Save').disabled).toBe(false))
  expect([...inspector(fresh.root).querySelectorAll('button')].some(b => b.textContent === 'Delete entity definition')).toBe(false)
})

it('meets a revision saved elsewhere with the conflict choices, in the entity definition\'s words, and keeps the person\'s version', async () => {
  const { root, fixture } = await openPlug()
  type(control(expand(entityRow(root, 'relay')), 'name'), 'Main relay')
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
  expect(last.definition.entities[0].name).toBe('Main relay')
  // The secret no one typed is kept.
  expect(last.definition.settings[1]).toEqual({ key: 'password', label: 'Password', type: 'string', secret: true, keep: true })
  expect(fixture.entities.documents.get('plug')!.settings[1].value).toBe('stored-secret')
})

it('restores a draft without a typed secret, keeping the saved one, and never writes a secret to the draft', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const first = await openPlug()
  const fixture = first.fixture
  expect(first.root.innerHTML).not.toContain('stored-secret')
  type(control(expand(entityRow(first.root, 'relay')), 'name'), 'Main relay')
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
  expect(control(expand(entityRow(again.root, 'relay')), 'name').value).toBe('Main relay')
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
  type(control(expand(entityRow(root, 'any_motion')), 'name'), 'Motion anywhere')
  expect(json(root).definition.entities[0].name).toBe('Motion anywhere')
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

it('starts a new definition named from the address, with one binary sensor named after it and the editor in its words', async () => {
  const { root, fixture } = setup('/entities/new?logic=graph&name=Hall motion')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  expect(nameInput(root).value).toBe('Hall motion')
  expect(json(root).definition).toEqual({ entities: [{ key: 'hall_motion', domain: 'binary_sensor', name: 'Hall motion' }], settings: [], device: null })
  expect(root.querySelector('section[aria-label="Entity definition editor"]')).not.toBeNull()
  expect(button(root, 'Back to entities')).toBeDefined()
  // Renamed after the first entity was edited, the entity keeps its own name.
  type(control(expand(entityRow(root, 'hall_motion')), 'name'), 'Hall')
  rename(root, 'Hallway motion')
  expect(json(root).definition.entities[0]).toEqual({ key: 'hall_motion', domain: 'binary_sensor', name: 'Hall' })
  // Taken ids are asked for at the first save.
  fixture.entities.seed('hallway_motion', newGraphDefinition('Hallway motion'))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture)[0]).toMatchObject({ id: 'hallway_motion_2', expectedRevision: 0 }))
  expect(fixture.entities.requests.filter(r => r.operation === 'list')).toHaveLength(1)
  expect(fixture.calls.some((call: any) => call.type === 'hass_sharp/visual' || call.type === 'hass_sharp/run_now')).toBe(false)
  more(root, 'Help')
  expect(document.querySelector('dialog[open]')!.textContent).toContain('What an entity definition is')
  expectClean(root)
})

const more = (root: Element, item: string) => {
  button(root, 'More actions').click()
  ;[...root.querySelectorAll<HTMLButtonElement>('[role="menu"] [role="menuitem"]')].find(b => b.textContent === item)!.click()
}

/** The phase 2 example of the device definition (code map, section 3; `tests/Runtime/EntityGraphTests.cs:511-535`). */
const plugExample: DefinitionBody = {
  name: 'Plug', device: { manufacturer: 'Acme', model: 'P1', suggestedArea: 'Kitchen' }, settings: [],
  entities: [
    { key: 'power', domain: 'sensor', name: 'Power', deviceClass: 'power', options: { unit: 'W', stateClass: 'measurement' } },
    { key: 'online', domain: 'binary_sensor', deviceClass: 'connectivity', entityCategory: 'diagnostic' },
    { key: 'relay', domain: 'switch', name: 'Relay', icon: 'mdi:power-socket', enabledByDefault: false }],
  logic: { kind: 'graph', graph: { version: 5, name: 'Plug',
    nodes: [
      { id: 'p', kind: 'entity_state', config: { value: { kind: 'literal', type: 'number', value: 42 }, entity: 'power' } },
      { id: 'o', kind: 'entity_state', config: { is_on: { kind: 'literal', type: 'boolean', value: true }, entity: 'online' } },
      { id: 'on', kind: 'entity_command', config: { entity: 'relay', command: 'turn_on' } },
      { id: 'set_on', kind: 'set_entity_state', config: { is_on: { kind: 'literal', type: 'boolean', value: true }, entity: 'relay' } },
      { id: 'off', kind: 'entity_command', config: { entity: 'relay', command: 'turn_off' } },
      { id: 'set_off', kind: 'set_entity_state', config: { is_on: { kind: 'literal', type: 'boolean', value: false }, entity: 'relay' } }],
    edges: [
      { id: 'on:next:set_on', source: 'on', target: 'set_on', port: 'next' },
      { id: 'off:next:set_off', source: 'off', target: 'set_off', port: 'next' }] } },
}

it('builds the phase 2 plug through the page, every member of it, and saves that example', async () => {
  const { root, fixture } = setup('/entities/new?name=Plug')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  // The starter's entity, named after the definition, becomes the power sensor.
  let row = expand(entityRow(root, 'plug'))
  type(control(row, 'key'), 'power')
  row = entityRow(root, 'power')
  type(control(row, 'name'), 'Power')
  choose(control(row, 'domain'), 'sensor')
  type(control(row, 'deviceClass'), 'power')
  type(control(row, 'option:unit'), 'W')
  choose(control(row, 'option:stateClass'), 'measurement')
  // A binary sensor without a name of its own.
  button(root, 'Add entity').click()
  type(control(entityRow(root, 'entity'), 'key'), 'online')
  row = entityRow(root, 'online')
  type(control(row, 'deviceClass'), 'connectivity')
  choose(control(row, 'entityCategory'), 'diagnostic')
  // The relay, which Home Assistant adds turned off.
  button(root, 'Add entity').click()
  type(control(entityRow(root, 'entity'), 'key'), 'relay')
  row = entityRow(root, 'relay')
  choose(control(row, 'domain'), 'switch')
  type(control(row, 'name'), 'Relay')
  type(control(row, 'icon'), 'mdi:power-socket')
  control(row, 'enabledByDefault').click()
  const device = root.querySelector<HTMLElement>('[aria-label="Device"]')!
  control(device, 'device').click()
  type(control(device, 'manufacturer'), 'Acme')
  type(control(device, 'model'), 'P1')
  type(control(device, 'suggestedArea'), 'Kitchen')

  // Each step is added with its title as its label; the example's steps have none, so the label is cleared.
  const ids: string[] = []
  // The power's State: 42 W.
  button(root, 'Add State').click()
  ids.push(lastStep(root))
  label(root, '')
  await pickOption(root, 'entity', 'Power')
  choose(inspector(root).querySelector<HTMLSelectElement>('[data-input-id="value"] select[aria-label="Value kind"]')!, 'number')
  type(inspector(root).querySelector<HTMLInputElement>('[data-input-id="value"] input[aria-label="Value"]')!, '42')
  // The online State: on.
  button(root, 'Add State').click()
  ids.push(lastStep(root))
  label(root, '')
  await pickOption(root, 'entity', 'online')
  inspector(root).querySelector<HTMLInputElement>('[data-input-id="is_on"] input[role="switch"]')!.click()
  // Turn on and Turn off, each setting the relay (the only entity that takes commands, so chosen for them).
  for (const [command, on] of [['Turn on', true], ['Turn off', false]] as const) {
    deselect(root)
    button(root, 'Add When commanded').click()
    const when = lastStep(root)
    ids.push(when)
    label(root, '')
    if (command === 'Turn off') await pickOption(root, 'command', 'Turn off')
    button(root, 'Add Set state').click()
    const set = lastStep(root)
    ids.push(set)
    label(root, '')
    await pickOption(root, 'entity', 'Relay')
    const toggle = () => inspector(root).querySelector<HTMLInputElement>('[data-input-id="is_on"] input[role="switch"]')!
    toggle().click()
    if (!on) toggle().click()
    flow(root, when, set)
  }
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(1))
  const sent = JSON.parse(JSON.stringify(saves(fixture)[0]))
  expect(sent).toMatchObject({ id: 'plug', expectedRevision: 0 })
  const body = normalised(sent.definition, ['p', 'o', 'on', 'set_on', 'off', 'set_off'])
  // Normalised as the motion example's: the page's step ids, positions and wire ids replaced by the example's. Beyond
  // that the page writes each step's run history at its catalog default (a State off, When commanded on), which the
  // hand-written example leaves to that default; and a flow wire from Next leaves its port out, which the graph format
  // reads as `next`, where the example names it.
  const nodes = body.logic.graph.nodes
  expect(nodes.map((n: any) => n.config.trackHistory)).toEqual([false, false, true, undefined, true, undefined])
  for (const node of nodes) delete node.config.trackHistory
  body.logic.graph.edges = body.logic.graph.edges.map(({ id: _id, ...edge }: any) => {
    expect(Object.keys(edge)).toEqual(['source', 'target'])
    return { id: `${edge.source}:next:${edge.target}`, ...edge, port: 'next' }
  })
  expect(body).toEqual(plugExample)
})

it('keeps the key of an entity added to a saved definition open until the save that stores it', async () => {
  const { root, fixture } = await openPlug()
  button(inspector(root), 'Add entity').click()
  const added = entityRow(root, 'entity')
  expect(control(added, 'key').readOnly).toBe(false)
  type(control(added, 'key'), 'meter')
  expect(control(entityRow(root, 'meter'), 'key').readOnly).toBe(false)
  // The saved ones stay fixed meanwhile.
  expect(control(expand(entityRow(root, 'relay')), 'key').readOnly).toBe(true)
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(chipTip(root)).toBe('Saved revision 2'))
  expect(saves(fixture).at(-1).definition.entities.map((e: any) => e.key)).toEqual(['relay', 'power', 'meter'])
  expect(control(entityRow(root, 'meter'), 'key').readOnly).toBe(true)
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
  press(root.querySelector('.solid-flow__node[data-id="on"]')!)
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
  type(control(expand(entityRow(root, 'relay')), 'name'), 'Main relay')
  type(control(expand(settingRow(root, 'token')), 'value'), 'typed-token')
  button(await conflictWith(root, fixture), 'Save as a copy').click()
  await vi.waitFor(() => expect(history.get()).toBe('/entities/plug_copy'))
  const copy = saves(fixture).at(-1)
  expect(copy).toMatchObject({ id: 'plug_copy', expectedRevision: 0, enabled: false })
  expect(copy.definition.name).toBe('Plug (copy)')
  expect(copy.definition.settings.find((s: any) => s.key === 'token').value).toBe('typed-token')
  await vi.waitFor(() => expect(root.textContent).toContain('The copy has no saved value for Password and API key. Type them again.'))
  expect(fixture.entities.documents.get('plug')!.name).toBe('Plug (elsewhere)')
  deselect(root)
  expect(control(expand(settingRow(root, 'password')), 'value').placeholder).toBe('No value')
  expect(root.innerHTML).not.toMatch(/stored-|typed-token/)
})

it('says so when the copy lacks a single secret', async () => {
  const { root, fixture } = await openPlug()
  type(control(expand(entityRow(root, 'relay')), 'name'), 'Main relay')
  button(await conflictWith(root, fixture), 'Save as a copy').click()
  await vi.waitFor(() => expect(root.textContent).toContain('The copy has no saved value for Password. Type it again.'))
})

it('discards the changes after a conflict, opening the definition as saved elsewhere', async () => {
  const { root, fixture } = await openPlug()
  type(control(expand(entityRow(root, 'relay')), 'name'), 'Main relay')
  button(await conflictWith(root, fixture), 'Discard my changes').click()
  await vi.waitFor(() => expect(nameInput(root).value).toBe('Plug (elsewhere)'))
  expect(chipTip(root)).toBe('Saved revision 2')
  expect(json(root).definition.entities[0].name).toBe('Relay')
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
  type(control(expand(entityRow(root, 'relay')), 'name'), 'Main relay')
  fixture.entities.documents.set('plug', { ...fixture.entities.documents.get('plug')!, revision: 3 })
  fixture.controls.generation = 'g3'
  window.dispatchEvent(new Event('hass-sharp-libraries-changed'))
  await vi.waitFor(() => expect(root.textContent).toContain('This entity definition changed elsewhere while you had unsaved changes here.'))
  expect(json(root).definition.entities[0].name).toBe('Main relay')
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
  type(control(expand(entityRow(root, 'relay')), 'name'), 'Main relay')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  fixture.entities.controls.diagnostics = [{ nodeId: null, inputId: 'host', portId: null, code: 'definition.settingType', severity: 3, message: 'Setting host is not a text', startLine: null, startColumn: null } as any]
  button(root, 'Save').click()
  await vi.waitFor(() => expect(settingRow(root, 'host').textContent).toContain('Setting host is not a text'))
  expect(settingRow(root, 'host').querySelector('.hs-def-row__head')!.getAttribute('aria-expanded')).toBe('true')
  expect(chipTip(root)).not.toBe('Saved revision 2')
  // Closed by the person, then found from the Problems tab: the form shows, the row opens, focus is in it.
  settingRow(root, 'host').querySelector<HTMLButtonElement>('.hs-def-row__head')!.click()
  press(root.querySelector('.solid-flow__node[data-id="on"]')!)
  await vi.waitFor(() => expect(inspector(root).querySelector('.hs-insp-node')).not.toBeNull())
  ;[...root.querySelectorAll<HTMLButtonElement>('.hs-drawer [role="tab"]')].find(b => b.textContent?.startsWith('Problems'))!.click()
  ;[...root.querySelectorAll<HTMLButtonElement>('.hs-problem__message')].find(b => b.textContent === 'Setting host is not a text')!.click()
  await vi.waitFor(() => expect(document.activeElement).toBe(control(settingRow(root, 'host'), 'key')))
  expect(inspector(root).querySelector('.hs-insp-node')).toBeNull()
})
