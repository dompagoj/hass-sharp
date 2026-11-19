import { readFileSync } from 'node:fs'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { Inspector } from '../src/visual/Inspector'
import { addGraphNode, connectData, nodeCatalog, validateGraph, withFirstStateValue, type VisualCatalogSnapshot } from '../src/visual/contract'
import { graphTypes } from '../src/visual/connectionIndex'
import { connectNodes, createGraph, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import { icons, nodeIdentity } from '../src/visual/icons'
import { chooseEntity, refitNotice, retypeNode } from '../src/entities/NodePickers'
import { HassCtx } from '../src/context'
import { add, empty, lit } from './visual-fixture'

// The steps of an entity definition name its entities (each an Entity node), their commands and its settings by key;
// the inspector offers them by name, and a key the definition no longer has stays shown, marked missing. The tests of
// When commanded, When a frame is needed and State themselves stay until Task 12 removes those steps.
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
  nodes.reduce(entityNode, { ...empty([], 'Plug'), definition: { entities: [], settings } as never })
/** The graph with one step added and its config replaced by `config`. */
const step = (graph: VisualGraph, kind: string, id: string, config: Record<string, unknown>) =>
  updateNode(addGraphNode(graph, kind, id, nodeCatalog), id, { config })
/** The config of the step `id`. */
const configOf = (graph: VisualGraph, id: string) => graph.nodes.find(n => n.id === id)!.config

const registryEntity = (entity_id: string, name: string, state_options: string[] | null = null) => ({
  entity_id, name, domain: entity_id.split('.')[0], area_id: null, device_id: null, device_class: null, supported_features: null, unit: null, icon: null, disabled: false, state_options,
})
const registry = { entities: [registryEntity('climate.living', 'Living room', ['off', 'heat', 'cool']), registryEntity('binary_sensor.motion', 'Motion'), registryEntity('camera.front_door', 'Front door')], areas: [], devices: [], floors: [], labels: [] }

/** The inspector of one node over a graph history, as the editor wires them. */
function mount(graph: VisualGraph, id: string, catalog = nodeCatalog) {
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
        <Inspector graph={history.graph()} catalog={catalog} diagnostics={[]} commit={history.commit} onNotice={text => notices.push(text)}
          selected={history.graph().nodes.find(n => n.id === selected())} selectedIds={[id]} />
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

  it('lists only the entities that take commands for When commanded (the old step, until Task 12)', async () => {
    const { el } = mount(step(entityGraph(), 'entity_command', 'cmd', {}), 'cmd')
    expect(texts(await open(el, 'entity'))).toEqual(['Relay · Switch'])
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

  it('says when the definition has no entities yet', async () => {
    const { el } = mount(step(entityGraph({ nodes: [] }), 'set_entity_state', 'state', {}), 'state')
    expect(field(el, 'entity').textContent).toContain('This definition has no entities yet. Add one under Entities, shown when no step is selected.')
    expect(box(el, 'entity').disabled).toBe(true)
  })

  it('says where to give an entity a type that takes commands when none does (When commanded, until Task 12)', async () => {
    const { el } = mount(step(entityGraph({ nodes: [{ key: 'power', domain: 'sensor', name: 'Power' }] }), 'entity_command', 'cmd', {}), 'cmd')
    expect(field(el, 'entity').textContent).toContain("None of this definition's entities takes commands. Give one a type that does, such as a switch, under Entities, shown when no step is selected.")
    expect(field(el, 'command').textContent).toContain('Give an entity a type that takes commands first.')
  })

  it('marks a type with no state for Set state', async () => {
    const graph = entityGraph({ nodes: [{ key: 'bell', domain: 'button', name: 'Bell' }, { key: 'relay', domain: 'switch', name: 'Relay' }] })
    const { el } = mount(step(graph, 'set_entity_state', 'set', { entity: 'bell' }), 'set')
    expect(texts(await open(el, 'entity'))).toEqual(['Bell · Button · has no state', 'Relay · Switch'])
    expect(el.querySelector('.hs-inspector section[aria-label="Inputs"]')).toBeNull()
  })
})

describe('the camera pickers of Show frame and When a frame is needed', () => {
  // `camera_frame` is the old When a frame is needed, whose picker is tested here until Task 12 removes it.
  const cameraKinds = ['show_frame', 'camera_frame']
  const cameras = () => entityGraph({ nodes: [...plug, { key: 'front', domain: 'camera', name: 'Front door' }, { key: 'yard', domain: 'camera' }] })

  it('offer only the definition\'s cameras, by name, and store the chosen key', async () => {
    for (const kind of cameraKinds) {
      const { el, node } = mount(step(cameras(), kind, 'cam', {}), 'cam')
      expect(field(el, 'entity').querySelector('label')?.textContent, kind).toBe('Camera')
      expect(texts(await open(el, 'entity')), kind).toEqual(['Front door · Camera', 'yard · Camera'])
      await choose(el, 'entity', 'yard')
      expect(node().config.entity, kind).toBe('yard')
      // Only one picker for the camera: the generic text field of its config is not drawn too.
      expect(el.querySelectorAll('.hs-inspector [data-input-id="entity"]'), kind).toHaveLength(1)
      dispose?.(); dispose = undefined; document.body.innerHTML = ''
    }
  })

  it('say to add a camera first when the definition has none, and are disabled', async () => {
    for (const graph of [entityGraph(), entityGraph({ nodes: [] })])
      for (const kind of cameraKinds) {
        const { el } = mount(step(graph, kind, 'cam', {}), 'cam')
        expect(field(el, 'entity').querySelector('.hs-field-hint')?.textContent, kind).toBe('Add a camera to this definition first.')
        expect(box(el, 'entity').disabled, kind).toBe(true)
        dispose?.(); dispose = undefined; document.body.innerHTML = ''
      }
  })

  it('show an entity that is not a camera as chosen, marked so', async () => {
    const { el } = mount(step(cameras(), 'show_frame', 'cam', { entity: 'relay' }), 'cam')
    expect(box(el, 'entity').value).toBe('Relay')
    expect(texts(await open(el, 'entity'))).toEqual(['Relay · Switch · not a camera', 'Front door · Camera', 'yard · Camera'])
  })

  it('name the definition\'s only camera in a new step, and none when it has several or none', () => {
    const added = (graph: VisualGraph, kind: string) => addGraphNode(graph, kind, 'new', nodeCatalog).nodes.at(-1)!.config
    const one = entityGraph({ nodes: [...plug, { key: 'front', domain: 'camera', name: 'Front door' }] })
    for (const kind of cameraKinds) {
      expect(added(one, kind).entity, kind).toBe('front')
      expect(added(cameras(), kind).entity, kind).toBeUndefined()
      // A definition whose only entity is not a camera: none named.
      expect(added(entityGraph({ nodes: [{ key: 'power', domain: 'sensor', name: 'Power' }] }), kind).entity, kind).toBeUndefined()
    }
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

describe('the command picker of When commanded (the old step, until Task 12)', () => {
  it('waits for an entity, then offers its commands by label and follows the entity', async () => {
    const { el, node } = mount(step(entityGraph(), 'entity_command', 'cmd', {}), 'cmd')
    expect(box(el, 'command').disabled).toBe(true)
    expect(field(el, 'command').textContent).toContain('Choose an entity first.')
    await choose(el, 'entity', 'Relay')
    // Ruling R12: the first command of the entity's type once it has an entity.
    expect(node().config).toMatchObject({ entity: 'relay', command: 'turn_on' })
    await vi.waitFor(() => expect(box(el, 'command').disabled).toBe(false))
    expect(box(el, 'command').getAttribute('aria-label')).toBe('Command')
    expect(box(el, 'command').value).toBe('Turn on')
    expect(texts(await open(el, 'command'))).toEqual(['Turn on', 'Turn off'])
    await choose(el, 'command', 'Turn off')
    expect(node().config.command).toBe('turn_off')
  })

  it('keeps a command the new entity also has, and replaces one it does not', () => {
    let graph = entityGraph({ nodes: [...plug, { key: 'fan', domain: 'switch', name: 'Fan' }] })
    graph = step(graph, 'entity_command', 'cmd', { entity: 'relay', command: 'turn_off' })
    graph = chooseEntity(graph, 'cmd', 'fan', nodeCatalog).graph
    expect(configOf(graph, 'cmd')).toMatchObject({ entity: 'fan', command: 'turn_off' })
    graph = updateNode(graph, 'cmd', { config: { ...configOf(graph, 'cmd'), command: 'toggle' } })
    expect(configOf(chooseEntity(graph, 'cmd', 'relay', nodeCatalog).graph, 'cmd')).toMatchObject({ entity: 'relay', command: 'turn_on' })
  })

  it('names a type and its commands as the catalog labels them, else in words', async () => {
    // A type the catalog adds: its label and its commands' come from its row, none from a table of the panel's own.
    const catalog = { ...nodeCatalog, entityDomains: { ...nodeCatalog.entityDomains,
      fan: { label: 'Ceiling fan', state: {}, commands: { turn_on: { label: 'Spin up' }, set_speed: {} }, options: {} },
      air_purifier: { state: {}, commands: { boost: {} }, options: {} } } } as unknown as VisualCatalogSnapshot
    const graph = entityGraph({ nodes: [{ key: 'blades', domain: 'fan', name: 'Blades' }, { key: 'purifier', domain: 'air_purifier', name: 'Purifier' }] })
    const { el } = mount(step(graph, 'entity_command', 'cmd', { entity: 'blades', command: 'turn_on' }), 'cmd', catalog)
    expect(texts(await open(el, 'entity'))).toEqual(['Blades · Ceiling fan', 'Purifier · Air purifier'])
    await vi.waitFor(() => expect(box(el, 'command').value).toBe('Spin up'))
    expect(texts(await open(el, 'command'))).toEqual(['Spin up', 'Set speed'])
  })
})

describe('the outputs of When commanded follow its command (the old step, until Task 12)', () => {
  // A lamp's Turn on gives its brightness, wired into a Set state; Turn off, a switch's commands and a number's give none
  // of that name.
  const lamp = () => {
    let graph = entityGraph({ nodes: [...plug, { key: 'lamp', domain: 'light', name: 'Lamp', options: { dimmable: true } }] })
    graph = step(graph, 'entity_command', 'cmd', { entity: 'lamp', command: 'turn_on' })
    graph = step(graph, 'set_entity_state', 'set', { entity: 'lamp' })
    return { ...graph, edges: [...graph.edges, { id: 'wire', kind: 'data' as const, source: 'cmd', sourcePort: 'brightness', target: 'set', targetPort: 'brightness' }] }
  }
  const wires = (graph: VisualGraph) => graph.edges.filter(e => e.kind === 'data').map(e => `${e.source}.${e.sourcePort}`)
  const notice = 'Removed 1 connection from an output the new command does not have: Brightness.'

  it('removes the wires of an output the command chosen does not have, in the same edit, and says so', async () => {
    const { el, history, node, notices } = mount(lamp(), 'cmd')
    await vi.waitFor(() => expect(box(el, 'command').value).toBe('Turn on'))
    await choose(el, 'command', 'Turn off')
    expect(node().config.command).toBe('turn_off')
    expect(wires(history.graph())).toEqual([])
    expect(notices).toEqual([notice])
    // One undo step brings back both.
    history.undo()
    expect([node().config.command, wires(history.graph())]).toEqual(['turn_on', ['cmd.brightness']])
  })

  it('removes them when the entity is retyped or another entity is chosen, and keeps a wire the new command has too', () => {
    const retyped = retypeNode(lamp(), 'lamp', 'switch', nodeCatalog)
    expect(configOf(retyped.graph, 'cmd')).toMatchObject({ entity: 'lamp', command: 'turn_on' })
    expect(wires(retyped.graph)).toEqual([])
    expect(refitNotice(retyped.removed)).toContain(notice.slice(0, -1))
    const chosen = chooseEntity(lamp(), 'cmd', 'relay', nodeCatalog)
    expect(wires(chosen.graph)).toEqual([])
    expect(chosen.notice).toBe(notice)
    // Another light's Turn on gives a brightness too: its wire stays.
    const lamps = entityNode(lamp(), { key: 'bulb', domain: 'light', name: 'Bulb', options: { dimmable: true } })
    const kept = chooseEntity(lamps, 'cmd', 'bulb', nodeCatalog)
    expect(wires(kept.graph)).toEqual(['cmd.brightness'])
    expect(kept.notice).toBeUndefined()
  })

  it('counts the connections it removes', () => {
    let twice = lamp()
    twice = step(twice, 'set_entity_state', 'again', { entity: 'lamp' })
    twice = { ...twice, edges: [...twice.edges, { id: 'wire2', kind: 'data' as const, source: 'cmd', sourcePort: 'brightness', target: 'again', targetPort: 'brightness' }] }
    expect(chooseEntity(twice, 'cmd', 'relay', nodeCatalog).notice).toBe('Removed 2 connections from outputs the new command does not have: Brightness.')
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
  it('says a State\'s optional values are optional (the old step, until Task 12)', () => {
    const { el } = mount(step(things, 'entity_state', 'state', { entity: 'lamp' }), 'state')
    expect(row(el, 'is_on').unset).toBe(false)
    expect(row(el, 'brightness')).toEqual({ unset: true, hint: 'Optional.' })
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
  it('names the only entity it can name, and that entity\'s first command (When commanded and State, until Task 12)', () => {
    expect(added(entityGraph(), 'entity_command')).toMatchObject({ entity: 'relay', command: 'turn_on' })
    expect(added(entityGraph({ nodes: [{ key: 'power', domain: 'sensor', name: 'Power' }] }), 'entity_state').entity).toBe('power')
  })
  it('names nothing when it could name several, or none', () => {
    for (const kind of ['set_entity_state', 'set_entity_available']) {
      expect(added(entityGraph(), kind).entity, kind).toBeUndefined()
      expect(added(entityGraph({ nodes: [] }), kind).entity, kind).toBeUndefined()
    }
    expect(added(entityGraph(), 'setting').key).toBeUndefined()
  })
  it('names nothing when it could name several, or none (When commanded and State, until Task 12)', () => {
    expect(added(entityGraph(), 'entity_state').entity).toBeUndefined()
    const sensors = entityGraph({ nodes: [{ key: 'power', domain: 'sensor', name: 'Power' }] })
    expect(added(sensors, 'entity_command')).not.toHaveProperty('entity')
    expect(added(sensors, 'entity_command')).not.toHaveProperty('command')
    expect(added(createGraph('Automation'), 'entity_state')).not.toHaveProperty('entity')
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
})
