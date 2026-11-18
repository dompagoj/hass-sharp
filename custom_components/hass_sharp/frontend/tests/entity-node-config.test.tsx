import { readFileSync } from 'node:fs'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { Inspector } from '../src/visual/Inspector'
import { addGraphNode, connectData, nodeCatalog, type Graph } from '../src/visual/contract'
import { createGraph, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import { icons, nodeIdentity } from '../src/visual/icons'
import { chooseEntity } from '../src/entities/NodePickers'
import { HassCtx } from '../src/context'

// The steps of an entity definition name its entities, their commands and its settings by key; the inspector offers
// them by name, and a key the definition no longer has stays shown, marked missing.
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

const lit = (type: string, value: unknown) => ({ kind: 'literal' as const, type, value })
const definition = () => ({
  entities: [{ key: 'relay', domain: 'switch', name: 'Relay' }, { key: 'power', domain: 'sensor', name: 'Power' }, { key: 'online', domain: 'binary_sensor', name: 'Online' }],
  settings: [{ key: 'host', label: 'Address', type: 'string', value: '10.0.0.7' }, { key: 'password', label: 'Password', type: 'string', secret: true, value: null, hasValue: true }],
})
const entityGraph = (extra: Partial<ReturnType<typeof definition>> = {}): VisualGraph =>
  ({ version: 5, name: 'Plug', nodes: [], edges: [], definition: { ...definition(), ...extra } as unknown as Graph['definition'] })
/** The graph with one step added and its config replaced by `config`. */
const step = (graph: VisualGraph, kind: string, id: string, config: Record<string, unknown>) =>
  updateNode(addGraphNode(graph, kind, id, nodeCatalog), id, { config })

const registryEntity = (entity_id: string, name: string, state_options: string[] | null = null) => ({
  entity_id, name, domain: entity_id.split('.')[0], area_id: null, device_id: null, device_class: null, supported_features: null, unit: null, icon: null, disabled: false, state_options,
})
const registry = { entities: [registryEntity('climate.living', 'Living room', ['off', 'heat', 'cool']), registryEntity('binary_sensor.motion', 'Motion')], areas: [], devices: [], floors: [], labels: [] }

/** The inspector of one node over a graph history, as the editor wires them. */
function mount(graph: VisualGraph, id: string) {
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
        <Inspector graph={history.graph()} catalog={nodeCatalog} diagnostics={[]} commit={history.commit} onNotice={text => notices.push(text)}
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
    const graph = entityGraph({ entities: [{ key: 'relay', domain: 'switch' }, { key: 'power', domain: 'sensor', name: 'Power' }] as never })
    const { el } = mount(step(graph, 'entity_state', 'state', { entity: 'relay' }), 'state')
    expect(box(el, 'entity').value).toBe('relay')
    expect(texts(await open(el, 'entity'))).toEqual(['relay · Switch', 'Power · Sensor'])
  })

  it('is chosen with the keyboard: arrow keys move, Enter chooses', async () => {
    const { el, node } = mount(step(entityGraph(), 'entity_state', 'state', {}), 'state')
    const input = box(el, 'entity')
    input.focus()
    for (const key of ['ArrowDown', 'ArrowDown', 'ArrowDown', 'Enter']) input.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
    expect(node().config.entity).toBe('online')
  })

  it('lists only the entities that take commands for When commanded', async () => {
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
    const { el } = mount(step(entityGraph({ entities: [] }), 'entity_state', 'state', {}), 'state')
    expect(field(el, 'entity').textContent).toContain('This definition has no entities yet.')
    expect(box(el, 'entity').disabled).toBe(true)
  })
})

describe('the command picker', () => {
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
    let graph = entityGraph({ entities: [...definition().entities, { key: 'fan', domain: 'switch', name: 'Fan' }] })
    graph = step(graph, 'entity_command', 'cmd', { entity: 'relay', command: 'turn_off' })
    graph = chooseEntity(graph, 'cmd', 'fan', nodeCatalog).graph
    expect(graph.nodes[0].config).toMatchObject({ entity: 'fan', command: 'turn_off' })
    graph = updateNode(graph, 'cmd', { config: { ...graph.nodes[0].config, command: 'toggle' } })
    expect(chooseEntity(graph, 'cmd', 'relay', nodeCatalog).graph.nodes[0].config).toMatchObject({ entity: 'relay', command: 'turn_on' })
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
    expect(typed.graph.nodes[0].config).toEqual({ entity: 'power' })
    expect(typed.notice).toBe('Cleared a value that no longer fits: On.')
    // Through the panel: the notice reaches the canvas.
    const { el, node, notices } = mount(relaySet(true), 'set')
    await choose(el, 'entity', 'Power')
    expect(node().config.entity).toBe('power')
    expect(notices).toEqual(['Removed 1 connection that no longer fits: On.'])
  })

  it('keeps the wires and values an entity of another type also takes', () => {
    const wired = chooseEntity(relaySet(true), 'set', 'online', nodeCatalog)
    expect(wired.graph.edges.map(e => e.id)).toEqual(relaySet(true).edges.map(e => e.id))
    expect(wired.notice).toBeUndefined()
    const typed = chooseEntity(relaySet(false), 'set', 'online', nodeCatalog)
    expect(typed.graph.nodes[0].config).toEqual({ entity: 'online', is_on: lit('boolean', true) })
  })
})

describe('a step added to an entity definition', () => {
  const added = (graph: VisualGraph, kind: string) => addGraphNode(graph, kind, 'new', nodeCatalog).nodes.at(-1)!.config
  it('names the only entity it can name, and that entity\'s first command', () => {
    expect(added(entityGraph(), 'entity_command')).toMatchObject({ entity: 'relay', command: 'turn_on' })
    const one = entityGraph({ entities: [{ key: 'power', domain: 'sensor', name: 'Power' }] })
    for (const kind of ['entity_state', 'set_entity_state', 'set_entity_available']) expect(added(one, kind).entity, kind).toBe('power')
  })
  it('names nothing when it could name several, or none', () => {
    for (const kind of ['entity_state', 'set_entity_state', 'set_entity_available']) expect(added(entityGraph(), kind).entity, kind).toBeUndefined()
    const sensors = entityGraph({ entities: [{ key: 'power', domain: 'sensor', name: 'Power' }] })
    expect(added(sensors, 'entity_command')).not.toHaveProperty('entity')
    expect(added(sensors, 'entity_command')).not.toHaveProperty('command')
    expect(added(entityGraph(), 'setting').key).toBeUndefined()
  })
  it('names the only setting', () => {
    expect(added(entityGraph({ settings: [{ key: 'host', label: 'Address', type: 'string' }] }), 'setting').key).toBe('host')
  })
  it('leaves Has state, Current state and every automation step as before', () => {
    const one = entityGraph({ entities: [{ key: 'power', domain: 'sensor', name: 'Power' }] })
    expect(added(one, 'entity_is')).toEqual(added(createGraph('Automation'), 'entity_is'))
    expect(added(one, 'entity_current')).toEqual(added(createGraph('Automation'), 'entity_current'))
    expect(added(createGraph('Automation'), 'entity_state')).not.toHaveProperty('entity')
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
    for (const kind of ['entity_state', 'entity_command', 'entity_setup', 'set_entity_state', 'set_entity_available', 'setting'])
      expect(nodeIdentity(kind, nodeCatalog), kind).toEqual({ key: 'entity', label: 'Entity', color: 'var(--hs-cat-entity)', icon: icons.entity })
  })
  it('defines the colour beside the other categories, for both colour schemes, and draws it on the minimap', () => {
    const tokens = readFileSync('src/styles/tokens.css', 'utf8')
    // A mid-tone hue mixed toward the text colour by --hs-hue-mix, which the dark scheme lowers, as each category's.
    expect(tokens).toMatch(/--hs-cat-code: [^;]+;\n\s*--hs-cat-entity: color-mix\(in oklab, #[0-9a-f]{6} var\(--hs-hue-mix\), var\(--hs-text, #202535\)\);/)
    expect(readFileSync('src/styles/flow.css', 'utf8')).toContain('.hs-app .hs-flow .hs-minimap-node--entity { fill: var(--hs-cat-entity); }')
  })
})
