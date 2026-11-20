import { readFileSync } from 'node:fs'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { Inspector } from '../src/visual/Inspector'
import { addGraphNode, connectData, nodeCatalog, validateGraph, withFirstStateValue, type VisualCatalogSnapshot } from '../src/visual/contract'
import { graphTypes } from '../src/visual/connectionIndex'
import { connectNodes, createGraph, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import { copySteps, pasteSteps } from '../src/visual/clipboard'
import { icons, nodeIdentity } from '../src/visual/icons'
import { chooseEntity } from '../src/entities/NodePickers'
import { refitNotice, retypeNode } from '../src/entities/refit'
import { HassCtx } from '../src/context'
import type { SavedEntities } from '../src/visual/adapter'
import type { VisualDiagnostic } from '../src/visual/api'
import { add, empty, lit } from './visual-fixture'

// The steps of an entity definition name its entities (each an Entity node) and its settings by key; the inspector
// offers them by name, and a key the definition no longer has stays shown, marked missing.
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

/** An Entity node's entity: its key (also the node's id), type and name; no name when `name` is left out. */
type EntityNode = { key: string; domain: string; name?: string; options?: Record<string, unknown> }
const plug: EntityNode[] = [{ key: 'relay', domain: 'switch', name: 'Relay' }, { key: 'power', domain: 'sensor', name: 'Power' }, { key: 'online', domain: 'binary_sensor', name: 'Online' }]
const plugSettings = () => [{ key: 'host', label: 'Address', type: 'string', value: '10.0.0.7' }, { key: 'password', label: 'Password', type: 'string', secret: true, value: null, hasValue: true }]
/** The graph with an Entity node `entity.key` for the entity, named only when `entity` names it. */
const entityNode = (graph: VisualGraph, entity: EntityNode) => {
  const added = add(graph, 'entity', entity.key, entity)
  if (entity.name !== undefined) return added
  const { name: _typeLabel, ...config } = added.nodes.at(-1)!.config
  return updateNode(added, entity.key, { config })
}
/** A plug's definition: an Entity node for each of `nodes` (its relay, power and online), and its settings. */
const entityGraph = ({ nodes = plug, settings = plugSettings() }: { nodes?: EntityNode[]; settings?: Record<string, unknown>[] } = {}): VisualGraph =>
  nodes.reduce(entityNode, { ...empty('Plug'), definition: { settings } as never })
/** The graph with one step added and its config replaced by `config`. */
const step = (graph: VisualGraph, kind: string, id: string, config: Record<string, unknown>) =>
  updateNode(addGraphNode(graph, kind, id, nodeCatalog), id, { config })
/** The config of the step `id`. */
const configOf = (graph: VisualGraph, id: string) => graph.nodes.find(n => n.id === id)!.config

const registryEntity = (entity_id: string, name: string, state_options: string[] | null = null) => ({
  entity_id, name, domain: entity_id.split('.')[0], area_id: null, device_id: null, device_class: null, supported_features: null, unit: null, icon: null, disabled: false, state_options,
})
const registry = {
  entities: [registryEntity('climate.living', 'Living room', ['off', 'heat', 'cool']), registryEntity('binary_sensor.motion', 'Motion'), registryEntity('camera.front_door', 'Front door'),
    // The relay of the saved definition below, on the device Home Assistant made for it.
    { ...registryEntity('switch.hall_relay', 'Relay'), device_id: 'own' }],
  areas: [], floors: [], labels: [],
  devices: [{ id: 'router', name: 'Router', area_id: null }, { id: 'own', name: 'Hall plug', area_id: null }],
}

/** The inspector of one node over a graph history, as the editor wires them. */
function mount(graph: VisualGraph, id: string, catalog = nodeCatalog, options: { saved?: SavedEntities; diagnostics?: VisualDiagnostic[] } = {}) {
  const el = document.createElement('div')
  el.className = 'hs-app'
  document.body.append(el)
  const history = createGraphHistory(graph)
  const [selected] = createSignal(id)
  const notices: string[] = []
  client = new QueryClient()
  const hass = { callWS: async (message: { type: string }) => (message.type === 'hass_sharp/get_entities' ? structuredClone(registry) : {}) }
  dispose = render(() => (
    <QueryClientProvider client={client!}>
      <HassCtx.Provider value={hass as any}>
        <Inspector graph={history.graph()} catalog={catalog} diagnostics={options.diagnostics ?? []} commit={history.commit} onNotice={text => notices.push(text)}
          selected={history.graph().nodes.find(n => n.id === selected())} selectedIds={[id]} savedEntities={options.saved} />
      </HassCtx.Provider>
    </QueryClientProvider>
  ), el)
  const node = () => history.graph().nodes.find(n => n.id === id)!
  return { el, history, node, notices }
}
const field = (el: Element, key: string) => el.querySelector<HTMLElement>(`.hs-inspector [data-input-id="${key}"]`)!
const box = (el: Element, key: string) => field(el, key).querySelector<HTMLInputElement>('input[role="combobox"]')!
/** Opens a picker and answers its options, as "label detail". */
async function open(el: Element, key: string) {
  box(el, key).click()
  await vi.waitFor(() => expect(field(el, key).querySelector('[role="listbox"]')).not.toBeNull())
  return [...field(el, key).querySelectorAll<HTMLElement>('[role="option"]')]
}
const texts = (options: HTMLElement[]) => options.map(o => [...o.children].map(c => c.textContent).join(' · '))
const choose = async (el: Element, key: string, label: string) => (await open(el, key)).find(o => o.firstElementChild?.textContent === label)!.click()

describe('the entity picker', () => {
  it('offers every entity of the definition by name, with its type, and stores the chosen key', async () => {
    const { el, node } = mount(step(entityGraph(), 'set_entity_available', 'avail', { available: lit('boolean', true) }), 'avail')
    expect(box(el, 'entity').getAttribute('aria-label')).toBe('Entity')
    expect(field(el, 'entity').querySelector('label')?.textContent).toBe('Entity')
    expect(texts(await open(el, 'entity'))).toEqual(['Relay · Switch', 'Power · Sensor', 'Online · Binary sensor'])
    await choose(el, 'entity', 'Power')
    expect(node().config.entity).toBe('power')
    await vi.waitFor(() => expect(box(el, 'entity').value).toBe('Power'))
  })

  it('names an entity without a name by its key', async () => {
    const graph = entityGraph({ nodes: [{ key: 'relay', domain: 'switch' }, { key: 'power', domain: 'sensor', name: 'Power' }] })
    const { el } = mount(step(graph, 'set_entity_state', 'state', { entity: 'relay' }), 'state')
    expect(box(el, 'entity').value).toBe('relay')
    expect(texts(await open(el, 'entity'))).toEqual(['relay · Switch', 'Power · Sensor'])
  })

  it('is chosen with the keyboard: arrow keys move, Enter chooses', async () => {
    const { el, node } = mount(step(entityGraph(), 'set_entity_available', 'state', {}), 'state')
    const input = box(el, 'entity')
    input.focus()
    for (const key of ['ArrowDown', 'ArrowDown', 'ArrowDown', 'Enter']) input.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
    expect(node().config.entity).toBe('online')
  })

  it('shows a key the definition no longer has as the selected entity, marked missing', async () => {
    const { el, node } = mount(step(entityGraph(), 'set_entity_state', 'set', { entity: 'heater' }), 'set')
    expect(box(el, 'entity').value).toBe('Missing entity: heater')
    expect(box(el, 'entity').getAttribute('aria-invalid')).toBe('true')
    const options = await open(el, 'entity')
    expect(texts(options)[0]).toBe('Missing entity: heater')
    expect(options[0].classList.contains('hs-search-current')).toBe(true)
    expect(node().config.entity).toBe('heater')
  })

  it('says to add an Entity node when the definition has none', async () => {
    const { el } = mount(step(entityGraph({ nodes: [] }), 'set_entity_state', 'state', {}), 'state')
    expect(field(el, 'entity').textContent).toContain('Add an Entity node from the step library first.')
    expect(box(el, 'entity').disabled).toBe(true)
  })

  it('marks a type with no state for Set state', async () => {
    const graph = entityGraph({ nodes: [{ key: 'bell', domain: 'button', name: 'Bell' }, { key: 'relay', domain: 'switch', name: 'Relay' }] })
    const { el } = mount(step(graph, 'set_entity_state', 'set', { entity: 'bell' }), 'set')
    expect(texts(await open(el, 'entity'))).toEqual(['Bell · Button · has no state', 'Relay · Switch'])
    expect(el.querySelector('.hs-inspector section[aria-label="Inputs"]')).toBeNull()
  })
})

describe('the camera picker of Show frame', () => {
  const cameras = () => entityGraph({ nodes: [...plug, { key: 'front', domain: 'camera', name: 'Front door' }, { key: 'yard', domain: 'camera' }] })

  it('offers only the definition\'s cameras, by name, and stores the chosen key', async () => {
    const { el, node } = mount(step(cameras(), 'show_frame', 'cam', {}), 'cam')
    expect(field(el, 'entity').querySelector('label')?.textContent).toBe('Camera')
    expect(texts(await open(el, 'entity'))).toEqual(['Front door · Camera', 'yard · Camera'])
    await choose(el, 'entity', 'yard')
    expect(node().config.entity).toBe('yard')
    // Only one picker for the camera: the generic text field of its config is not drawn too.
    expect(el.querySelectorAll('.hs-inspector [data-input-id="entity"]')).toHaveLength(1)
  })

  it('says to add a camera first when the definition has none, and is disabled', async () => {
    for (const graph of [entityGraph(), entityGraph({ nodes: [] })]) {
      const { el } = mount(step(graph, 'show_frame', 'cam', {}), 'cam')
      expect(field(el, 'entity').querySelector('.hs-field-hint')?.textContent).toBe('Add a camera to this definition first.')
      expect(box(el, 'entity').disabled).toBe(true)
      dispose?.(); dispose = undefined; document.body.innerHTML = ''
    }
  })

  it('shows an entity that is not a camera as chosen, marked so', async () => {
    const { el } = mount(step(cameras(), 'show_frame', 'cam', { entity: 'relay' }), 'cam')
    expect(box(el, 'entity').value).toBe('Relay')
    expect(texts(await open(el, 'entity'))).toEqual(['Relay · Switch · not a camera', 'Front door · Camera', 'yard · Camera'])
  })

  it('names the definition\'s only camera in a new step, and none when it has several or none', () => {
    const added = (graph: VisualGraph) => addGraphNode(graph, 'show_frame', 'new', nodeCatalog).nodes.at(-1)!.config
    const one = entityGraph({ nodes: [...plug, { key: 'front', domain: 'camera', name: 'Front door' }] })
    expect(added(one).entity).toBe('front')
    expect(added(cameras()).entity).toBeUndefined()
    // A definition whose only entity is not a camera: none named.
    expect(added(entityGraph({ nodes: [{ key: 'power', domain: 'sensor', name: 'Power' }] })).entity).toBeUndefined()
  })
})

describe('retyping an Entity node to or from Camera', () => {
  it('removes the values, wires and options a camera does not take, and a camera\'s seconds between frames from another type', () => {
    let graph = entityGraph({ nodes: [{ key: 'relay', domain: 'switch', name: 'Relay' }, { key: 'level', domain: 'number', name: 'Level', options: { min: 1, max: 30 } }] })
    graph = step(graph, 'set_entity_state', 'set', { entity: 'relay', is_on: lit('boolean', true) })
    graph = step(graph, 'set_entity_state', 'value', { entity: 'level' })
    graph = connectNodes(graph, 'level', 'value', 'command:set_value')
    graph = connectData(graph, 'level', 'command:set_value:value', 'value', 'value', graphTypes(graph, nodeCatalog))
    const relay = retypeNode(graph, 'relay', 'camera', nodeCatalog)
    expect(configOf(relay.graph, 'set')).toEqual({ entity: 'relay' })
    expect(refitNotice(relay.removed)).toBe('Cleared a value that no longer fits: On.')
    const level = retypeNode(graph, 'level', 'camera', nodeCatalog)
    expect(configOf(level.graph, 'level')).toMatchObject({ key: 'level', domain: 'camera', name: 'Level' })
    // A number's minimum and maximum go: a camera takes neither.
    expect(configOf(level.graph, 'level').options).toEqual({})
    // Its Set value command and that command's value go, with the wires from them.
    expect(level.graph.edges.filter(e => e.source === 'level')).toEqual([])
    expect(refitNotice(level.removed)).toContain('Value')
    // Back from Camera: its seconds between frames go, as a number has none.
    const camera = entityGraph({ nodes: [{ key: 'front', domain: 'camera', name: 'Front', options: { frame_interval: 2 } }] })
    const front = configOf(retypeNode(camera, 'front', 'number', nodeCatalog).graph, 'front')
    expect(front).toMatchObject({ key: 'front', domain: 'number', name: 'Front' })
    expect(front.options).toEqual({})
  })
})

describe('Take a picture', () => {
  it('offers only Home Assistant\'s cameras for its camera', async () => {
    const { el } = mount(step(entityGraph(), 'camera_snapshot', 'snap', {}), 'snap')
    field(el, 'entity').querySelector<HTMLButtonElement>('.hs-picker-field')!.click()
    const options = () => [...document.querySelectorAll<HTMLElement>('dialog[open] [role="option"]')]
    await vi.waitFor(() => expect(options().length).toBeGreaterThan(0))
    expect(options().map(o => o.textContent)).toEqual([expect.stringContaining('camera.front_door')])
  })
})

describe('the setting picker', () => {
  it('offers the settings by label with their type, never their values', async () => {
    const { el, node } = mount(step(entityGraph(), 'setting', 'host', {}), 'host')
    expect(box(el, 'key').getAttribute('aria-label')).toBe('Setting')
    const options = await open(el, 'key')
    expect(texts(options)).toEqual(['Address · Text', 'Password · Text'])
    await choose(el, 'key', 'Address')
    expect(node().config.key).toBe('host')
    await vi.waitFor(() => expect(box(el, 'key').value).toBe('Address'))
    expect(el.querySelector('.hs-inspector')!.textContent).not.toContain('10.0.0.7')
  })

  it('shows a key the definition no longer has as missing, and says when there is none', async () => {
    const { el } = mount(step(entityGraph(), 'setting', 'host', { key: 'port' }), 'host')
    expect(box(el, 'key').value).toBe('Missing setting: port')
    dispose?.()
    document.body.innerHTML = ''
    const empty = mount(step(entityGraph({ settings: [] }), 'setting', 'host', {}), 'host')
    expect(field(empty.el, 'key').textContent).toContain('This definition has no settings yet.')
  })
})

describe('inputs follow the entity', () => {
  /** Set state of the relay, its On typed in or wired from Has state. */
  const relaySet = (wired: boolean) => {
    let graph = step(entityGraph(), 'set_entity_state', 'set', { entity: 'relay', ...(wired ? {} : { is_on: lit('boolean', true) }) })
    if (wired) {
      graph = addGraphNode(graph, 'entity_is', 'is', nodeCatalog)
      graph = connectData(graph, 'is', 'value', 'set', 'is_on', nodeCatalog)
    }
    return graph
  }

  it('shows the inputs of the chosen entity\'s type', async () => {
    const { el } = mount(relaySet(false), 'set')
    expect(field(el, 'is_on')).not.toBeNull()
    await choose(el, 'entity', 'Power')
    await vi.waitFor(() => expect(el.querySelector('.hs-inspector [data-input-id="value"]')).not.toBeNull())
    expect(el.querySelector('.hs-inspector [data-input-id="is_on"]')).toBeNull()
  })

  it('removes the wires and values of inputs the new type does not have, and says so', async () => {
    const wired = chooseEntity(relaySet(true), 'set', 'power', nodeCatalog)
    expect(wired.graph.edges).toEqual([])
    expect(wired.notice).toBe('Removed 1 connection that no longer fits: On.')
    const typed = chooseEntity(relaySet(false), 'set', 'power', nodeCatalog)
    expect(configOf(typed.graph, 'set')).toEqual({ entity: 'power' })
    expect(typed.notice).toBe('Cleared a value that no longer fits: On.')
    // Through the panel: the notice reaches the canvas.
    const { el, node, notices } = mount(relaySet(true), 'set')
    await choose(el, 'entity', 'Power')
    expect(node().config.entity).toBe('power')
    expect(notices).toEqual(['Removed 1 connection that no longer fits: On.'])
  })

  it('clears a value kept hidden while its entity was missing, once another entity is chosen', () => {
    const graph = step(entityGraph(), 'set_entity_state', 'set', { entity: 'heater', is_on: lit('boolean', true), level: lit('number', 3) })
    const chosen = chooseEntity(graph, 'set', 'power', nodeCatalog)
    expect(configOf(chosen.graph, 'set')).toEqual({ entity: 'power' })
    expect(chosen.notice).toBe('Cleared 2 values that no longer fit: Is on, Level.')
    // One the new type takes stays.
    expect(configOf(chooseEntity(graph, 'set', 'online', nodeCatalog).graph, 'set')).toEqual({ entity: 'online', is_on: lit('boolean', true) })
  })

  it('keeps the wires and values an entity of another type also takes', () => {
    const wired = chooseEntity(relaySet(true), 'set', 'online', nodeCatalog)
    expect(wired.graph.edges.map(e => e.id)).toEqual(relaySet(true).edges.map(e => e.id))
    expect(wired.notice).toBeUndefined()
    const typed = chooseEntity(relaySet(false), 'set', 'online', nodeCatalog)
    expect(configOf(typed.graph, 'set')).toEqual({ entity: 'online', is_on: lit('boolean', true) })
  })
})

describe('the state values of Set state', () => {
  // What is drawn is what is saved (ruling R13): a value with no value is never drawn as a switch.
  const things = entityGraph({ nodes: [{ key: 'relay', domain: 'switch', name: 'Relay' }, { key: 'lamp', domain: 'light', name: 'Lamp' },
    { key: 'blind', domain: 'cover', name: 'Blind' }, { key: 'power', domain: 'sensor', name: 'Power' }, { key: 'level', domain: 'number', name: 'Level' },
    { key: 'bell', domain: 'button', name: 'Bell' }] })
  const kept = 'Leave empty to keep it as it is.'
  const row = (el: Element, port: string) => ({
    unset: !!field(el, port).querySelector('.hs-insp-unset'),
    hint: field(el, port).querySelector('.hs-field-hint')?.textContent ?? null,
  })
  const toggle = (el: Element, port: string) => field(el, port).querySelector<HTMLInputElement>('input[role="switch"]')
  /** A new Set state, its entity then chosen in the picker. */
  const chosen = async (key: string) => {
    const mounted = mount(step(things, 'set_entity_state', 'set', {}), 'set')
    await choose(mounted.el, 'entity', key)
    return mounted
  }
  it('opens On when the only value it set was cleared, then a switch or a light is chosen', () => {
    const unset = { kind: 'unset' }
    // Its On cleared, then a light: On again, as for a step that set nothing.
    const lamp = chooseEntity(step(things, 'set_entity_state', 'set', { entity: 'relay', is_on: unset }), 'set', 'lamp', nodeCatalog)
    expect(configOf(lamp.graph, 'set')).toEqual({ entity: 'lamp', is_on: lit('boolean', true) })
    // A sensor's value cleared, then a switch.
    const relay = chooseEntity(step(things, 'set_entity_state', 'set', { entity: 'power', value: unset }), 'set', 'relay', nodeCatalog)
    expect(configOf(relay.graph, 'set')).toEqual({ entity: 'relay', is_on: lit('boolean', true) })
    expect(withFirstStateValue('set_entity_state', { entity: 'lamp', is_on: unset }, graphTypes(things, nodeCatalog))).toEqual({ entity: 'lamp', is_on: lit('boolean', true) })
  })

  it('opens a new switch step On, as saved, which one click turns Off', async () => {
    const { el, node, history } = await chosen('Relay')
    expect(node().config).toEqual({ entity: 'relay', is_on: lit('boolean', true) })
    expect(toggle(el, 'is_on')?.checked).toBe(true)
    toggle(el, 'is_on')!.click()
    expect(node().config.is_on).toEqual(lit('boolean', false))
    // Choosing the entity and its On were one change.
    history.undo()
    history.undo()
    expect(node().config).toEqual({})
  })
  it('opens a step added with a switch or light as the only entity On too', () => {
    const one = entityGraph({ nodes: [{ key: 'lamp', domain: 'light', name: 'Lamp' }] })
    expect(addGraphNode(one, 'set_entity_state', 'set', nodeCatalog).nodes.at(-1)!.config).toMatchObject({ entity: 'lamp', is_on: lit('boolean', true) })
    // The light's own Entity node sets no value until one is given: its state values are its inputs.
    expect(configOf(one, 'lamp').arguments).toEqual({})
    expect(addGraphNode(one, 'set_entity_available', 'avail', nodeCatalog).nodes.at(-1)!.config).not.toHaveProperty('is_on')
  })
  it('undoes the entity and its value together', async () => {
    const { node, history } = await chosen('Lamp')
    expect(node().config).toEqual({ entity: 'lamp', is_on: lit('boolean', true) })
    history.undo()
    expect(node().config).toEqual({})
  })
  it('opens a new light step On, its brightness not set and kept when left so', async () => {
    const { el } = await chosen('Lamp')
    expect(toggle(el, 'is_on')?.checked).toBe(true)
    expect(row(el, 'brightness')).toEqual({ unset: true, hint: kept })
  })
  it('changes nothing else when the entity of a step that sets values changes, and leaves a button alone', async () => {
    const { node } = mount(step(things, 'set_entity_state', 'set', { entity: 'lamp', brightness: lit('number', 40) }), 'set')
    expect(configOf(chooseEntity(step(things, 'set_entity_state', 'set', { entity: 'lamp', brightness: lit('number', 40) }), 'set', 'relay', nodeCatalog).graph, 'set'))
      .toEqual({ entity: 'relay' })
    expect(node().config.is_on).toBeUndefined()
    expect(configOf(chooseEntity(step(things, 'set_entity_state', 'set', {}), 'set', 'bell', nodeCatalog).graph, 'set')).toEqual({ entity: 'bell' })
    // A step that sets no state value is given none.
    expect(configOf(chooseEntity(step(things, 'set_entity_available', 'avail', {}), 'avail', 'relay', nodeCatalog).graph, 'avail')).toEqual({ entity: 'relay' })
  })
  it('shows a saved light step that sets only its brightness with On not set, kept, and not as a switch', () => {
    const { el } = mount(step(things, 'set_entity_state', 'set', { entity: 'lamp', brightness: lit('number', 40) }), 'set')
    expect(row(el, 'is_on')).toEqual({ unset: true, hint: kept })
    expect(toggle(el, 'is_on')).toBeNull()
  })
  it('shows a saved cover step that sets only its position with Closed not set, and the same for one that sets nothing', () => {
    const cover = mount(step(things, 'set_entity_state', 'set', { entity: 'blind', position: lit('number', 40) }), 'set')
    expect(row(cover.el, 'is_closed')).toEqual({ unset: true, hint: kept })
    expect(toggle(cover.el, 'is_closed')).toBeNull()
    dispose?.()
    document.body.innerHTML = ''
    const bare = mount(step(things, 'set_entity_state', 'set', { entity: 'relay' }), 'set')
    expect(row(bare.el, 'is_on').unset).toBe(true)
    expect(toggle(bare.el, 'is_on')).toBeNull()
  })
  it('sets a boolean to true with Set value, so the switch it then shows is what is saved, and ties the hint to that button', () => {
    const { el, node } = mount(step(things, 'set_entity_state', 'set', { entity: 'blind', position: lit('number', 40) }), 'set')
    const button = field(el, 'is_closed').querySelector<HTMLButtonElement>('.hs-insp-unset button')!
    const hint = field(el, 'is_closed').querySelector<HTMLElement>('.hs-field-hint')!
    expect(button.getAttribute('aria-describedby')).toBe(hint.id)
    button.click()
    expect(node().config.is_closed).toEqual(lit('boolean', true))
    expect(toggle(el, 'is_closed')?.checked).toBe(true)
  })
  it('opens a new sensor or number step with its field ready and empty', async () => {
    for (const name of ['Power', 'Level']) {
      const { el, node, history } = await chosen(name)
      expect(node().config, name).toEqual({ entity: name.toLowerCase() })
      expect(row(el, 'value').unset, name).toBe(false)
      expect(field(el, 'value').querySelector<HTMLInputElement>('input:not([role])')?.value, name).toBe('')
      // Nothing set yet: the validator says so until the value is filled.
      const problems = () => validateGraph(history.graph(), nodeCatalog).filter(d => d.nodeId === 'set').map(d => d.message)
      expect(problems().some(message => message.endsWith('needs at least one value')), name).toBe(true)
      history.commit(updateNode(history.graph(), 'set', { config: { ...node().config, value: lit('number', 4) } }))
      expect(problems().some(message => message.endsWith('needs at least one value')), name).toBe(false)
      dispose?.()
      document.body.innerHTML = ''
    }
  })
  it('shows the data of an Entity node\'s commands as its outputs', () => {
    const { el } = mount(things, 'lamp')
    expect([...el.querySelectorAll('.hs-insp-output__name')].map(o => o.textContent)).toEqual(['Brightness'])
    dispose?.()
    document.body.innerHTML = ''
    // A switch's commands carry no data: no outputs.
    expect(mount(things, 'relay').el.querySelector('.hs-insp-output__name')).toBeNull()
  })
})

describe('a step added to an entity definition', () => {
  const added = (graph: VisualGraph, kind: string) => addGraphNode(graph, kind, 'new', nodeCatalog).nodes.at(-1)!.config
  it('names the only entity it can name', () => {
    const one = entityGraph({ nodes: [{ key: 'power', domain: 'sensor', name: 'Power' }] })
    for (const kind of ['set_entity_state', 'set_entity_available']) expect(added(one, kind).entity, kind).toBe('power')
  })
  it('names nothing when it could name several, or none', () => {
    for (const kind of ['set_entity_state', 'set_entity_available']) {
      expect(added(entityGraph(), kind).entity, kind).toBeUndefined()
      expect(added(entityGraph({ nodes: [] }), kind).entity, kind).toBeUndefined()
    }
    expect(added(entityGraph(), 'setting').key).toBeUndefined()
  })
  it('names the only setting', () => {
    expect(added(entityGraph({ settings: [{ key: 'host', label: 'Address', type: 'string' }] }), 'setting').key).toBe('host')
  })
  it('leaves Has state, Current state and every automation step as before', () => {
    const one = entityGraph({ nodes: [{ key: 'power', domain: 'sensor', name: 'Power' }] })
    expect(added(one, 'entity_is')).toEqual(added(createGraph('Automation'), 'entity_is'))
    expect(added(one, 'entity_current')).toEqual(added(createGraph('Automation'), 'entity_current'))
    expect(added(createGraph('Automation'), 'set_entity_state')).not.toHaveProperty('entity')
  })
})

it('offers the known states of the chosen Home Assistant entity for Has state, as a State condition does', async () => {
  const graph = step(entityGraph(), 'entity_is', 'is', { entity: lit('entity_id', 'climate.living'), state: lit('string', 'heat') })
  const { el, node } = mount(graph, 'is')
  const select = () => field(el, 'state').querySelector<HTMLSelectElement>('select')
  await vi.waitFor(() => expect([...select()!.options].filter(o => !o.disabled).map(o => o.textContent)).toEqual(['Off', 'Heat', 'Cool']))
  select()!.value = [...select()!.options].find(o => o.textContent === 'Cool')!.value
  select()!.dispatchEvent(new Event('change', { bubbles: true }))
  expect(node().config.state).toEqual(lit('string', 'cool'))
})

it('keeps the state as text for an entity whose states are not known', async () => {
  const graph = step(createGraph('Automation'), 'entity_is', 'other', { entity: lit('entity_id', 'binary_sensor.motion'), state: lit('string', 'on') })
  const { el } = mount(graph, 'other')
  await vi.waitFor(() => expect(field(el, 'state').querySelector<HTMLInputElement>('input')?.value).toBe('on'))
  expect(field(el, 'state').querySelector('select')).toBeNull()
})

describe('devices in the right panel', () => {
  /**
   * A relay, and four Device nodes in this order: Hall plug (key plug), Hub (key hub), a second Hall plug (key twin)
   * and one with neither a name nor a key (spare).
   */
  const devices = () => {
    let graph = entityGraph({ nodes: [{ key: 'relay', domain: 'switch', name: 'Relay' }] })
    graph = add(graph, 'device', 'plug', { key: 'plug', name: 'Hall plug' })
    graph = add(graph, 'device', 'hub', { key: 'hub', name: 'Hub' })
    graph = add(graph, 'device', 'twin', { key: 'twin', name: 'Hall plug' })
    return updateNode(add(graph, 'device', 'spare'), 'spare', { config: {} })
  }
  const select = (el: Element, key: string) => field(el, key).querySelector<HTMLSelectElement>('select')!
  const pick = (control: HTMLSelectElement, value: string) => {
    control.value = value
    control.dispatchEvent(new Event('change', { bubbles: true }))
  }
  const pickLabel = (control: HTMLSelectElement, label: string) => pick(control, [...control.options].find(o => o.textContent === label)!.value)
  /** The control a field's label names, in the inspector. */
  const control = <T extends HTMLElement = HTMLInputElement>(el: Element, label: string) => {
    const found = [...el.querySelectorAll<HTMLLabelElement>('.hs-inspector label.hs-insp-field__label')].find(l => l.textContent === label)
    expect(found, label).toBeDefined()
    return el.querySelector<T>(`[id="${found!.htmlFor}"]`)!
  }
  const typeInto = (input: HTMLInputElement, value: string) => {
    input.value = value
    input.dispatchEvent(new Event('input', { bubbles: true }))
    input.dispatchEvent(new FocusEvent('blur'))
  }
  const wires = (graph: VisualGraph) => graph.edges.filter(e => e.kind === 'data' && e.target === 'relay' && e.targetPort === 'device')

  it('the Device select of an Entity node wires and unwires it', async () => {
    const { el, history } = mount(devices(), 'relay')
    expect(field(el, 'device').querySelector('label')!.textContent).toBe('Device')
    // Every Device node by its name, else its key, else Device: two of one name are both offered.
    expect([...select(el, 'device').options].map(o => o.textContent)).toEqual(['None', 'Hall plug', 'Hub', 'Hall plug', 'Device'])
    expect(select(el, 'device').value).toBe('')
    expect(field(el, 'device').querySelector('.hs-field-hint')!.textContent).toBe("An entity on a device is listed on that device's page. None: it stands alone.")
    // The Device pin is not among the generic inputs, so nothing offers to type a device in.
    const inputs = el.querySelector('.hs-inspector section[aria-label="Inputs"]')!
    expect([...inputs.querySelectorAll<HTMLElement>('[data-input-id]')].map(row => row.dataset.inputId)).toEqual(['is_on'])
    expect(el.querySelectorAll('.hs-inspector [data-input-id="device"]')).toHaveLength(1)

    pick(select(el, 'device'), 'plug')
    expect(wires(history.graph())).toEqual([{ id: expect.any(String), kind: 'data', source: 'plug', sourcePort: 'entities', target: 'relay', targetPort: 'device' }])
    await vi.waitFor(() => expect(select(el, 'device').value).toBe('plug'))
    // The other Hall plug, chosen by its node: the wire is replaced, in one undo step.
    pick(select(el, 'device'), 'twin')
    expect(wires(history.graph()).map(e => e.source)).toEqual(['twin'])
    history.undo()
    expect(wires(history.graph()).map(e => e.source)).toEqual(['plug'])
    await vi.waitFor(() => expect(select(el, 'device').value).toBe('plug'))
    // None: the wire goes, in one undo step.
    pickLabel(select(el, 'device'), 'None')
    expect(wires(history.graph())).toEqual([])
    await vi.waitFor(() => expect(select(el, 'device').value).toBe(''))
    history.undo()
    expect(wires(history.graph()).map(e => e.source)).toEqual(['plug'])
    history.undo()
    expect(wires(history.graph())).toEqual([])
    // A device with no key can still be wired to.
    pickLabel(select(el, 'device'), 'Device')
    expect(wires(history.graph()).map(e => e.source)).toEqual(['spare'])
  })

  it('the Device select offers a Device node and not a C# device, which adds its entities in its code', async () => {
    const graph = add(add(devices(), 'csharp_device', 'code', { key: 'code', name: 'Code plug' }), 'device', 'desk', { key: 'desk', name: 'Desk' })
    const { el } = mount(graph, 'relay')
    expect([...select(el, 'device').options].map(o => o.textContent)).toEqual(['None', 'Hall plug', 'Hub', 'Hall plug', 'Device', 'Desk'])
  })

  it('a Device node\'s fields edit its config', async () => {
    const { el, node, history } = mount(devices(), 'plug')
    const labels = [...el.querySelectorAll('.hs-inspector .hs-insp-main label.hs-insp-field__label')].map(label => label.textContent)
    expect(labels).toEqual(['Key', 'Manufacturer', 'Model', 'Software version', 'Hardware version', 'Configuration URL', 'Suggested area', 'Part of'])
    // Its name is the header's; no generic field repeats any of them.
    expect(el.querySelector<HTMLInputElement>('.hs-inspector input[aria-label="Device name"]')!.value).toBe('Hall plug')
    for (const id of ['key', 'manufacturer', 'model', 'swVersion', 'hwVersion', 'configurationUrl', 'suggestedArea', 'via'])
      expect(el.querySelectorAll(`.hs-inspector [data-input-id="${id}"]`), id).toHaveLength(1)
    // The labels, placeholders and hints a class definition's device form has too.
    expect(control(el, 'Model').placeholder).toBe('Not set')
    expect(control(el, 'Configuration URL').type).toBe('url')
    expect(field(el, 'configurationUrl').textContent).toContain("A page of the device's own, linked from its page in Home Assistant.")
    expect(field(el, 'suggestedArea').textContent).toContain('The area Home Assistant puts the device in when it first adds it.')
    expect(field(el, 'via').textContent).toContain('The device it is connected through, such as a hub.')

    typeInto(control(el, 'Model'), 'P1')
    expect(node().config.model).toBe('P1')
    typeInto(control(el, 'Configuration URL'), ' http://10.0.0.7 ')
    expect(node().config.configurationUrl).toBe('http://10.0.0.7')
    // Cleared, a field is left out, never stored blank.
    typeInto(control(el, 'Model'), '')
    expect(node().config).not.toHaveProperty('model')
    typeInto(control(el, 'Manufacturer'), '   ')
    expect(node().config).not.toHaveProperty('manufacturer')
    expect(Object.values(node().config)).not.toContain('')
    expect(validateGraph(history.graph(), nodeCatalog).filter(d => d.nodeId === 'plug' && d.severity === 3)).toEqual([])
  })

  it('Part of another device of this definition', async () => {
    // The saved relay is on the device Home Assistant made for this definition: it is not offered as a parent.
    const saved = { keys: ['relay'], entityIds: { relay: 'switch.hall_relay' }, deviceKeys: [] }
    const { el, node, history } = mount(devices(), 'plug', nodeCatalog, { saved })
    const partOf = () => select(el, 'via')
    expect([...partOf().options].map(o => o.textContent)).toEqual(['Nothing', 'Another device of this definition', 'A Home Assistant device…'])
    expect(partOf().value).toBe('')
    // Chosen, the other devices are listed; until one is picked the device is still part of nothing.
    pickLabel(partOf(), 'Another device of this definition')
    const local = () => el.querySelector<HTMLElement>('.hs-inspector [data-field="viaLocal"]')
    await vi.waitFor(() => expect(local()).not.toBeNull())
    expect(node().config).not.toHaveProperty('via')
    expect(local()!.querySelector('.hs-field-hint')!.textContent).toBe('Part of nothing until you pick a device.')
    const box = () => local()!.querySelector<HTMLInputElement>('input[role="combobox"]')!
    expect(box().getAttribute('aria-label')).toBe('Device of this definition')
    box().click()
    await vi.waitFor(() => expect(local()!.querySelector('[role="listbox"]')).not.toBeNull())
    // By name, with the key that tells two of one name apart; never the device itself, nor one with no key.
    const options = [...local()!.querySelectorAll<HTMLElement>('[role="option"]')]
    expect(texts(options)).toEqual(['Hall plug · twin', 'Hub · hub'])
    options.find(o => o.firstElementChild?.textContent === 'Hub')!.click()
    expect(node().config.via).toEqual({ device: 'hub' })
    await vi.waitFor(() => expect(box().value).toBe('Hub'))
    expect(partOf().selectedOptions[0].textContent).toBe('Another device of this definition')
    expect(local()!.querySelector('.hs-field-hint')).toBeNull()

    // A Home Assistant device, as a class definition's device form offers one: without the device of this definition's entities.
    pickLabel(partOf(), 'A Home Assistant device…')
    const ha = () => el.querySelector<HTMLElement>('.hs-inspector [data-field="viaDevice"]')
    await vi.waitFor(() => expect(ha()).not.toBeNull())
    expect(local()).toBeNull()
    expect(ha()!.querySelector('.hs-field-hint')!.textContent).toBe('Still part of Hub until you pick a device.')
    expect(node().config.via).toEqual({ device: 'hub' })
    ha()!.querySelector<HTMLInputElement>('input[role="combobox"]')!.click()
    await vi.waitFor(() => expect(ha()!.querySelectorAll('[role="option"]').length).toBeGreaterThan(0))
    const devicesOffered = [...ha()!.querySelectorAll<HTMLElement>('[role="option"]')]
    expect(devicesOffered.map(o => o.firstElementChild?.textContent)).toEqual(['Router'])
    devicesOffered[0].click()
    expect(node().config.via).toEqual({ deviceId: 'router' })
    // Back to another device of this definition: still part of the Home Assistant device until one is picked.
    pickLabel(partOf(), 'Another device of this definition')
    await vi.waitFor(() => expect(local()).not.toBeNull())
    expect(local()!.querySelector('.hs-field-hint')!.textContent).toBe('Still part of Router until you pick a device.')
    // Nothing: Part of is left out.
    pickLabel(partOf(), 'Nothing')
    expect(node().config).not.toHaveProperty('via')
    history.undo()
    expect(node().config.via).toEqual({ deviceId: 'router' })
  })

  it('Part of offers another device of this definition only when there is one to pick, or one is named', async () => {
    const partOf = (el: Element) => [...select(el, 'via').options].map(o => o.textContent)
    // The only other Device node has no key: it cannot be picked, so the choice is not offered.
    const lone = () => add(entityGraph({ nodes: [{ key: 'relay', domain: 'switch', name: 'Relay' }] }), 'device', 'plug', { key: 'plug', name: 'Hall plug' })
    const spare = (graph: VisualGraph) => updateNode(add(graph, 'device', 'spare'), 'spare', { config: {} })
    let { el } = mount(spare(lone()), 'plug')
    expect(partOf(el)).toEqual(['Nothing', 'A Home Assistant device…'])
    dispose?.(); document.body.innerHTML = ''
    // Alone in the definition, the same.
    ;({ el } = mount(lone(), 'plug'))
    expect(partOf(el)).toEqual(['Nothing', 'A Home Assistant device…'])
    dispose?.(); document.body.innerHTML = ''
    // Part of a device the definition does not have: the choice is offered and chosen, so the missing one is seen and can be cleared.
    const missing = updateNode(spare(lone()), 'plug', { config: { key: 'plug', name: 'Hall plug', via: { device: 'hub' } } })
    const mounted = mount(missing, 'plug')
    el = mounted.el
    expect(partOf(el)).toEqual(['Nothing', 'Another device of this definition', 'A Home Assistant device…'])
    expect(select(el, 'via').selectedOptions[0].textContent).toBe('Another device of this definition')
    const local = el.querySelector<HTMLElement>('.hs-inspector [data-field="viaLocal"]')!
    expect(local.querySelector<HTMLInputElement>('input[role="combobox"]')!.value).toBe('Missing device: hub')
    pick(select(el, 'via'), '')
    expect(mounted.node().config).not.toHaveProperty('via')
    await vi.waitFor(() => expect(partOf(el)).toEqual(['Nothing', 'A Home Assistant device…']))
  })

  it('a saved device\'s key is read-only', () => {
    const { el } = mount(devices(), 'plug', nodeCatalog, { saved: { keys: [], entityIds: {}, deviceKeys: ['plug'] } })
    const key = control(el, 'Key')
    expect(key.readOnly).toBe(true)
    expect(key.value).toBe('plug')
    expect(field(el, 'key').textContent).toContain("The key is part of the device's identity in Home Assistant and cannot change.")
    dispose?.()
    document.body.innerHTML = ''

    // Not saved yet: it can change, and a device that is part of it follows.
    const graph = updateNode(devices(), 'twin', { config: { key: 'twin', name: 'Hall plug', via: { device: 'hub' } } })
    const unsaved = mount(graph, 'hub', nodeCatalog, { saved: { keys: [], entityIds: {}, deviceKeys: ['plug'] } })
    const editable = control(unsaved.el, 'Key')
    expect(editable.readOnly).toBe(false)
    expect(field(unsaved.el, 'key').textContent).toContain('Devices that are part of it follow it. It cannot change once saved.')
    typeInto(editable, 'bridge')
    expect(unsaved.node().config.key).toBe('bridge')
    expect(configOf(unsaved.history.graph(), 'twin').via).toEqual({ device: 'bridge' })
    // A key another device has, or one that is not a key: refused, and nothing changes.
    const before = unsaved.history.graph()
    typeInto(control(unsaved.el, 'Key'), 'plug')
    expect(field(unsaved.el, 'key').textContent).toContain('Another one already has this key; give each a key of its own.')
    typeInto(control(unsaved.el, 'Key'), 'Hall Plug')
    expect(unsaved.history.graph()).toBe(before)
  })

  it('a pasted copy of a saved device has a key of its own, which can change', () => {
    const graph = devices()
    const pasted = pasteSteps(graph, copySteps(graph, ['plug'])!, nodeCatalog, { offset: { x: 40, y: 40 } })
    if ('error' in pasted) throw new Error(pasted.error)
    const copy = pasted.ids[0]
    expect(configOf(pasted.graph, copy).key).toBe('plug_2')
    const { el, node } = mount(pasted.graph, copy, nodeCatalog, { saved: { keys: [], entityIds: {}, deviceKeys: ['plug'] } })
    const key = control(el, 'Key')
    expect(key.value).toBe('plug_2')
    expect(key.readOnly).toBe(false)
    expect(field(el, 'key').textContent).not.toContain('cannot change.')
    typeInto(key, 'desk_plug')
    expect(node().config.key).toBe('desk_plug')
  })

  it('a Device node\'s problems go to their field', () => {
    const problem = (inputId: string | undefined, code: string, message: string, severity = 3): VisualDiagnostic =>
      ({ nodeId: 'plug', ...(inputId ? { inputId } : {}), code, message, severity, startLine: null, startColumn: null })
    const diagnostics = [
      problem('key', 'definition.deviceKey', 'Give this device a key, such as plug'),
      problem('name', 'device.name', 'Name this device: Home Assistant shows it by this name'),
      problem('via', 'device.via', 'Part of must be nothing, a Home Assistant device or another device of this definition'),
      problem('configurationUrl', 'device.configurationUrl', 'The configuration URL of Hall plug must start with http://, https:// or homeassistant://, followed by an address'),
      problem(undefined, 'device.empty', "Hall plug has no entities: connect its Entities pin to an entity's Device pin", 2),
    ]
    const { el } = mount(devices(), 'plug', nodeCatalog, { diagnostics })
    const goTo = (message: string) => [...el.querySelectorAll<HTMLButtonElement>('.hs-insp-problems button')].find(b => b.textContent?.includes(message))
    goTo('Give this device a key')!.click()
    expect(document.activeElement).toBe(control(el, 'Key'))
    goTo('Name this device')!.click()
    expect(document.activeElement).toBe(el.querySelector('.hs-inspector input[aria-label="Device name"]'))
    goTo('Part of must be')!.click()
    expect(document.activeElement).toBe(select(el, 'via'))
    goTo('The configuration URL')!.click()
    expect(document.activeElement).toBe(control(el, 'Configuration URL'))
    // Each is said under its field too.
    expect(field(el, 'configurationUrl').textContent).toContain('must start with http://')
    expect(field(el, 'via').textContent).toContain('Part of must be nothing')
    // No field for a device with no entities: listed, with nothing to go to.
    const empty = [...el.querySelectorAll('.hs-insp-problem')].find(item => item.textContent?.includes('has no entities'))!
    expect(empty.querySelector('button.hs-insp-problem__body')).toBeNull()
  })
})

describe('the Entity category', () => {
  it('has an icon and colour of its own', () => {
    for (const kind of ['entity', 'entity_setup', 'set_entity_state', 'set_entity_available', 'show_frame', 'setting'])
      expect(nodeIdentity(kind, nodeCatalog), kind).toEqual({ key: 'entity', label: 'Entity', color: 'var(--hs-cat-entity)', icon: icons.entity })
  })
  it('defines the colour beside the other categories, for both colour schemes, and draws it on the minimap', () => {
    const tokens = readFileSync('src/styles/tokens.css', 'utf8')
    // A mid-tone hue mixed toward the text colour by --hs-hue-mix, which the dark scheme lowers, as each category's.
    expect(tokens).toMatch(/--hs-cat-code: [^;]+;\n\s*--hs-cat-entity: color-mix\(in oklab, #[0-9a-f]{6} var\(--hs-hue-mix\), var\(--hs-text, #202535\)\);/)
    expect(readFileSync('src/styles/flow.css', 'utf8')).toContain('.hs-app .hs-flow .hs-minimap-node--entity { fill: var(--hs-cat-entity); }')
  })
  it('gives the Device pin a hue of its own, not one a value pin or a category has', () => {
    const tokens = readFileSync('src/styles/tokens.css', 'utf8')
    const hex = (name: string) => tokens.match(new RegExp(`--hs-${name}: (?:color-mix\\(in oklab, )?(#[0-9a-f]{6})`))?.[1]
    const others = [...tokens.matchAll(/--hs-((?:port|cat)-[a-z-]+): (?:color-mix\(in oklab, )?#[0-9a-f]{6}/g)].map(m => m[1]).filter(name => name !== 'port-device')
    expect(others.length).toBeGreaterThan(10)
    // Far from each: Data's teal was next to the first colour chosen.
    const rgb = (value: string) => [1, 3, 5].map(i => parseInt(value.slice(i, i + 2), 16))
    const device = rgb(hex('port-device')!)
    for (const name of others) expect(Math.hypot(...rgb(hex(name)!).map((c, i) => c - device[i])), name).toBeGreaterThan(60)
  })
})

it('opens a step of a kind this version does not have, such as the old State, and names it as unknown', () => {
  // A graph saved with a State step, which an Entity node replaced: the step is still drawn and selected, and says why.
  const graph = { ...entityGraph(), nodes: [...entityGraph().nodes, { id: 'old', kind: 'entity_state', config: { entity: 'relay', is_on: lit('boolean', true) } }] }
  const { el } = mount(graph, 'old')
  expect(el.querySelector('.hs-insp-node')?.getAttribute('data-node-id')).toBe('old')
  expect(el.querySelector('.hs-insp-desc')?.textContent).toBe('This kind of step is not available. Its settings are kept.')
  expect(el.querySelector('.hs-insp-json')?.textContent).toContain('"kind": "entity_state"')
  // No entity picker is offered for it: the step library's pickers are for the kinds this version has.
  expect(el.querySelector('.hs-inspector [data-input-id="entity"]')).toBeNull()
  expect(validateGraph(graph, nodeCatalog).filter(d => d.nodeId === 'old').map(d => d.code)).toContain('node.unknownKind')
})
