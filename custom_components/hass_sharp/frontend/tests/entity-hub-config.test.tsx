import { afterEach, describe, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { NodeConfig } from '../src/visual/NodeConfig'
import { Inspector } from '../src/visual/Inspector'
import { nodeCatalog, validateGraph } from '../src/visual/contract'
import { connectNodes, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import { nodeTitle } from '../src/visual/flow/flowModel'
import type { VisualDiagnostic } from '../src/visual/api'
import type { SavedEntities } from '../src/visual/adapter'
import { keyProblem } from '../src/entities/definitionEdits'
import { HassCtx } from '../src/context'
import { add, empty, lit } from './visual-fixture'

// An Entity node's right panel: its name in the header, then its key, type, settings, options, run history and entity
// id in its own section; its state values are the generic Inputs.
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

const lamp = (config: Record<string, unknown> = {}) => add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp', ...config })

/** Types into a field as a person does: the text, then leaving the field (a draft field commits on blur). */
function type(field: HTMLInputElement | HTMLTextAreaElement, value: string) {
  field.value = value
  field.dispatchEvent(new Event('input', { bubbles: true }))
  field.dispatchEvent(new FocusEvent('blur'))
}
/** The labels of the panel's fields, top to bottom. */
const labels = (root: Element) => [...root.querySelectorAll('.hs-insp-field__label')].map(label => label.textContent)
/** The control a field's label names. */
const control = <T extends HTMLElement = HTMLInputElement>(root: Element, label: string) => {
  const found = [...root.querySelectorAll<HTMLLabelElement>('label.hs-insp-field__label')].find(l => l.textContent === label)
  expect(found, label).toBeDefined()
  return root.querySelector<T>(`[id="${found!.htmlFor}"]`)!
}
const row = (root: Element, id: string) => [...root.querySelectorAll<HTMLElement>('[data-input-id]')].find(el => el.dataset.inputId === id)

/** NodeConfig of the node `id` over a graph history, as the inspector wires it. */
function mount(graph: VisualGraph, id: string, options: { saved?: SavedEntities; diagnostics?: VisualDiagnostic[] } = {}) {
  const root = document.createElement('div')
  root.className = 'hs-app'
  document.body.append(root)
  const history = createGraphHistory(graph)
  const notices: string[] = []
  client = new QueryClient()
  dispose = render(() => (
    <QueryClientProvider client={client!}>
      <HassCtx.Provider value={{ callWS: async () => ({ entities: [], areas: [], devices: [], floors: [], labels: [] }) } as never}>
        <NodeConfig node={history.graph().nodes.find(n => n.id === id)!} graph={history.graph()} catalog={nodeCatalog} diagnostics={options.diagnostics ?? []}
          savedEntities={options.saved} onChange={(config, opts) => history.commit(updateNode(history.graph(), id, { config }), opts)}
          onGraphChange={history.commit} onNotice={text => notices.push(text)} />
      </HassCtx.Provider>
    </QueryClientProvider>
  ), root)
  const changed = () => history.graph().nodes.find(n => n.id === id)!
  return { root, history, changed, notices }
}

it('the header names the entity, and the card follows', () => {
  const { root, changed, history } = mount(lamp(), 'l')
  const name = root.querySelector<HTMLInputElement>('input[aria-label="Entity name"]')!
  expect(name).not.toBeNull()
  expect(root.querySelector('input[aria-label="Node label"]')).toBeNull()
  expect(name.value).toBe('Lamp')
  expect(name.placeholder).toBe('Not set')
  expect(name.dataset.inputId).toBe('name')
  expect(root.querySelector('.hs-insp-kind')!.textContent).toBe('Entity')
  type(name, 'Hall lamp')
  expect(changed().config.name).toBe('Hall lamp')
  expect(changed().label ?? '').toBe('')
  expect(nodeTitle(changed(), nodeCatalog)).toBe('Hall lamp')
  expect(nodeTitle({ ...changed(), config: { ...changed().config, name: '' } }, nodeCatalog)).toBe('lamp')
  // Cleared, the name is left out (never null), and the node is named by its key.
  type(name, '')
  expect(changed().config).not.toHaveProperty('name')
  expect(nodeTitle(changed(), nodeCatalog)).toBe('lamp')
  expect(validateGraph(history.graph()).filter(d => d.code === 'entity.name')).toHaveLength(1)
  expect(validateGraph(history.graph()).filter(d => d.severity === 3).map(d => d.code)).toEqual(['entity.name'])
})

it('the section has the entity\'s fields and its type\'s options, and no generic field repeats them', () => {
  const { root } = mount(lamp(), 'l')
  expect(labels(root)).toEqual(expect.arrayContaining(['Key', 'Type', 'Device class', 'Icon', 'Category', 'Enabled by default', 'Can be dimmed']))
  // In the order of the list on the right panel, then the type's options.
  const order = ['Key', 'Type', 'Device class', 'Icon', 'Category', 'Enabled by default', 'Can be dimmed']
  expect(labels(root).filter(label => order.includes(label!))).toEqual(order)
  expect(root.querySelectorAll('[data-input-id="key"]').length).toBe(1)
  for (const id of ['domain', 'deviceClass', 'icon', 'entityCategory', 'enabledByDefault', 'option:dimmable']) expect(root.querySelectorAll(`[data-input-id="${id}"]`).length, id).toBe(1)
  expect(root.textContent).not.toContain('State values')
  // Neither the generic Options nor Run history field: the section has them.
  expect(labels(root)).not.toContain('Options')
  expect(labels(root)).not.toContain('Run history')
  expect(labels(root)).not.toContain('Name')
  // The copy of the right panel's entity rows.
  expect(control(root, 'Device class').placeholder).toBe('None')
  expect(row(root, 'deviceClass')!.textContent).toContain('A Home Assistant device class, for example motion.')
  expect(row(root, 'icon')!.textContent).toContain('A Material Design icon, such as mdi:motion-sensor.')
  expect(row(root, 'entityCategory')!.textContent).toContain("Config and diagnostic entities are listed apart on the device's page.")
  expect(row(root, 'enabledByDefault')!.textContent).toContain('Off: Home Assistant adds it turned off, to be turned on in its settings.')
  expect(row(root, 'domain')!.textContent).toContain('Steps that use the entity follow its type.')
  expect(control<HTMLSelectElement>(root, 'Type').value).toBe('light')
  expect(control<HTMLSelectElement>(root, 'Category').value).toBe('')
  expect(control(root, 'Enabled by default').checked).toBe(true)
})

it('the fields write the node\'s config, and clearing one leaves its key out rather than null', () => {
  const { root, changed, history } = mount(lamp({ icon: 'mdi:lamp', deviceClass: 'light', entityCategory: 'config' }), 'l')
  expect(control(root, 'Icon').value).toBe('mdi:lamp')
  expect(control<HTMLSelectElement>(root, 'Category').value).toBe('config')
  type(control(root, 'Icon'), '')
  type(control(root, 'Device class'), '  ')
  const category = control<HTMLSelectElement>(root, 'Category')
  category.value = ''
  category.dispatchEvent(new Event('change', { bubbles: true }))
  for (const key of ['icon', 'deviceClass', 'entityCategory']) expect(changed().config, key).not.toHaveProperty(key)
  expect(Object.values(changed().config)).not.toContain(null)
  expect(Object.values(changed().config)).not.toContain(undefined)
  expect(validateGraph(history.graph()).filter(d => d.severity === 3)).toEqual([])
  // Set again, trimmed.
  type(control(root, 'Icon'), ' mdi:lightbulb ')
  expect(changed().config.icon).toBe('mdi:lightbulb')
  category.value = 'diagnostic'
  category.dispatchEvent(new Event('change', { bubbles: true }))
  expect(changed().config.entityCategory).toBe('diagnostic')
  // Off is stored; on again is the default, left out.
  control(root, 'Enabled by default').click()
  expect(changed().config.enabledByDefault).toBe(false)
  control(root, 'Enabled by default').click()
  expect(changed().config).not.toHaveProperty('enabledByDefault')
  // An option: on is stored under options; off is left out.
  control(root, 'Can be dimmed').click()
  expect(changed().config.options).toEqual({ dimmable: true })
  control(root, 'Can be dimmed').click()
  expect(changed().config.options).toEqual({})
  // Type sets the type.
  const domain = control<HTMLSelectElement>(root, 'Type')
  expect([...domain.options].map(o => o.textContent)).toEqual(expect.arrayContaining(['Light', 'Switch', 'Binary sensor', 'Camera']))
  domain.value = 'switch'
  domain.dispatchEvent(new Event('change', { bubbles: true }))
  expect(changed().config.domain).toBe('switch')
  expect(validateGraph(history.graph()).filter(d => d.severity === 3)).toEqual([])
})

it('choosing a type in the section shows the notice, and one undo brings everything back', () => {
  let graph = lamp({ options: { dimmable: true }, arguments: { is_on: lit('boolean', true), brightness: lit('number', 128) } })
  graph = add(graph, 'set_entity_state', 'set', { entity: 'lamp', is_on: lit('boolean', true), brightness: lit('number', 10) })
  graph = connectNodes(graph, 'l', 'set', 'command:turn_on')
  const { root, changed, history, notices } = mount(graph, 'l')
  const domain = control<HTMLSelectElement>(root, 'Type')
  domain.value = 'binary_sensor'
  domain.dispatchEvent(new Event('change', { bubbles: true }))
  expect(changed().config).toMatchObject({ domain: 'binary_sensor' })
  expect(changed().config.options).toEqual({})
  expect(changed().config.arguments).toEqual({ is_on: lit('boolean', true) })
  expect(history.graph().edges.some(e => e.source === 'l')).toBe(false)
  expect(history.graph().nodes.find(n => n.id === 'set')!.config).not.toHaveProperty('brightness')
  expect(notices).toEqual(['Removed 1 connection that no longer fits: Turn on. Cleared 2 values that no longer fit: Brightness.'])
  // The section now has the new type's fields: no Can be dimmed.
  expect(labels(root)).not.toContain('Can be dimmed')
  expect(validateGraph(history.graph()).some(d => d.message.startsWith('Unknown input'))).toBe(false)
  // One change, so one undo step.
  history.undo()
  expect(history.graph()).toBe(graph)
  // A switch has Turn on too: its wire stays, and the notice names only the values that went.
  domain.value = 'switch'
  domain.dispatchEvent(new Event('change', { bubbles: true }))
  expect(changed().config.domain).toBe('switch')
  expect(history.graph().edges.some(e => e.source === 'l' && e.kind !== 'data' && e.port === 'command:turn_on')).toBe(true)
  expect(notices).toHaveLength(2)
  expect(notices[1]).toBe('Cleared 2 values that no longer fit: Brightness.')
  // A type that drops nothing says nothing.
  domain.value = 'light'
  domain.dispatchEvent(new Event('change', { bubbles: true }))
  expect(changed().config.domain).toBe('light')
  expect(notices).toHaveLength(2)
})

it('the name is trimmed when the field is left, not while it is typed', () => {
  const { root, changed, history } = mount(lamp(), 'l')
  const name = root.querySelector<HTMLInputElement>('input[aria-label="Entity name"]')!
  // A space typed between words stays while typing.
  name.value = 'Hall '
  name.dispatchEvent(new Event('input', { bubbles: true }))
  expect(changed().config.name).toBe('Hall ')
  name.value = ' Hall lamp '
  name.dispatchEvent(new Event('input', { bubbles: true }))
  expect(changed().config.name).toBe(' Hall lamp ')
  name.dispatchEvent(new FocusEvent('blur'))
  expect(changed().config.name).toBe('Hall lamp')
  expect(name.value).toBe('Hall lamp')
  // Enter commits it too.
  name.value = 'Desk  '
  name.dispatchEvent(new Event('input', { bubbles: true }))
  name.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  expect(changed().config.name).toBe('Desk')
  // Only spaces is no name: left out, never blank.
  type(name, '   ')
  expect(changed().config).not.toHaveProperty('name')
  expect(name.value).toBe('')
  expect(validateGraph(history.graph()).filter(d => d.severity === 3).map(d => d.code)).toEqual(['entity.name'])
})

it('before the first save the key can be edited, and the steps that use it follow', () => {
  const graph = add(lamp(), 'set_entity_state', 'set', { entity: 'lamp', is_on: lit('boolean', true) })
  const { root, history, changed } = mount(graph, 'l')
  const key = control(root, 'Key')
  expect(key.readOnly).toBe(false)
  type(key, 'hall')
  expect(changed().config.key).toBe('hall')
  expect(history.graph().nodes.find(n => n.id === 'set')!.config.entity).toBe('hall')
})

it('a duplicated saved Entity node is a new entity: its own key, which can still be edited', () => {
  const root = document.createElement('div')
  root.className = 'hs-app'
  document.body.append(root)
  const [graph, setGraph] = createSignal(add(lamp(), 'set_entity_state', 'set', { entity: 'lamp', is_on: lit('boolean', true) }))
  const [selected, setSelected] = createSignal('l')
  client = new QueryClient()
  dispose = render(() => (
    <QueryClientProvider client={client!}>
      <HassCtx.Provider value={{ callWS: async () => ({ entities: [], areas: [], devices: [], floors: [], labels: [] }) } as never}>
        <Inspector graph={graph()} catalog={nodeCatalog} diagnostics={[]} commit={setGraph} docked onSelect={id => id && setSelected(id)}
          selected={graph().nodes.find(n => n.id === selected())} selectedIds={[selected()]} savedEntities={{ keys: ['lamp'], entityIds: { lamp: 'light.lamp' } }} />
      </HassCtx.Provider>
    </QueryClientProvider>
  ), root)
  expect(control(root, 'Key').readOnly).toBe(true)
  expect(root.querySelector('.hs-entity-node__id')!.textContent).toContain('light.lamp')
  root.querySelector<HTMLButtonElement>('button[aria-label="Node actions"]')!.click()
  ;[...root.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].find(b => b.textContent === 'Duplicate')!.click()
  const copy = graph().nodes.at(-1)!
  expect(copy.kind).toBe('entity')
  expect(selected()).toBe(copy.id)
  expect(copy.config.key).toBe('lamp_2')
  // The copy is not among the saved entities: its key is editable, and it has no entity id yet.
  const key = control(root, 'Key')
  expect(key.readOnly).toBe(false)
  expect(key.value).toBe('lamp_2')
  expect(root.querySelector('.hs-entity-node__id')).toBeNull()
  // The step outside the copy still names the original.
  expect(graph().nodes.find(n => n.id === 'set')!.config.entity).toBe('lamp')
  expect(validateGraph(graph()).some(d => d.code === 'definition.duplicateEntity')).toBe(false)
})

it('a saved key cannot change, and an unsaved one renames the steps that use it', () => {
  const saved = mount(add(lamp(), 'set_entity_state', 'set', { entity: 'lamp', is_on: lit('boolean', true) }), 'l', { saved: { keys: ['lamp'], entityIds: {} } })
  const fixed = control(saved.root, 'Key')
  expect(fixed.readOnly).toBe(true)
  expect(fixed.value).toBe('lamp')
  expect(row(saved.root, 'key')!.textContent).toContain("The key is part of the entity's identity in Home Assistant and cannot change.")
  dispose?.()
  document.body.innerHTML = ''

  let graph = add(lamp(), 'set_entity_state', 'set', { entity: 'lamp', is_on: lit('boolean', true) })
  graph = add(graph, 'entity', 'd', { key: 'desk', domain: 'switch', name: 'Desk' })
  const { root, history, changed } = mount(graph, 'l', { saved: { keys: ['other'], entityIds: {} } })
  const key = control(root, 'Key')
  expect(key.readOnly).toBe(false)
  expect(row(root, 'key')!.textContent).toContain('Steps that use the entity follow it. It cannot change once saved.')
  type(key, 'hall')
  expect(changed().config.key).toBe('hall')
  expect(history.graph().nodes.find(n => n.id === 'set')!.config.entity).toBe('hall')
  expect(history.graph().nodes.find(n => n.id === 'd')!.config.key).toBe('desk')
  // A key another node has: keyProblem's sentence, and nothing changes.
  const before = history.graph()
  type(key, 'desk')
  expect(row(root, 'key')!.textContent).toContain(keyProblem('desk', ['desk'])!)
  expect(history.graph()).toBe(before)
  // So does a key that is not one.
  type(key, 'Hall Lamp')
  expect(row(root, 'key')!.textContent).toContain(keyProblem('Hall Lamp', [])!)
  expect(history.graph()).toBe(before)
})

it('the state values are inputs that can be set', () => {
  const { root, changed } = mount(lamp(), 'l')
  const inputs = root.querySelector('section[aria-label="Inputs"]')!
  expect(inputs).not.toBeNull()
  expect([...inputs.querySelectorAll<HTMLElement>('[data-input-id]')].map(el => el.dataset.inputId)).toEqual(['is_on', 'brightness'])
  expect(row(inputs, 'is_on')!.textContent).toContain('On')
  expect(row(inputs, 'brightness')!.textContent).toContain('Brightness')
  // Optional on the node: each starts not set, and Set value gives it its editor.
  inputs.querySelector<HTMLButtonElement>('button[aria-label="Set On"]')!.click()
  const toggle = row(root, 'is_on')!.querySelector<HTMLInputElement>('input[role="switch"], input[type="checkbox"]')!
  expect(toggle).not.toBeNull()
  if (!toggle.checked) toggle.click()
  expect((changed().config.arguments as Record<string, unknown>).is_on).toEqual(lit('boolean', true))
  expect(changed().config).not.toHaveProperty('is_on')
})

describe('run history', () => {
  const switches = (root: Element) => [...root.querySelectorAll<HTMLElement>('section[aria-label="Run history"] label')].map(l => l.textContent)
  it('run history switches follow the type and write only what differs from the default', () => {
    const { root, changed } = mount(lamp(), 'l')
    expect(root.querySelector('section[aria-label="Run history"] h3')!.textContent).toBe('Run history')
    expect(switches(root)).toEqual(['Record state updates', 'Record commands'])
    expect(control(root, 'Record state updates').checked).toBe(false)
    expect(control(root, 'Record commands').checked).toBe(true)
    control(root, 'Record commands').click()
    expect(changed().config.history).toEqual({ commands: false })
    control(root, 'Record commands').click()
    expect(changed().config.history).toEqual({})
    control(root, 'Record state updates').click()
    expect(changed().config.history).toEqual({ state: true })
    control(root, 'Record state updates').click()
    expect(changed().config.history).toEqual({})
  })
  it('a camera records only its frames', () => {
    const { root, changed } = mount(add(empty(), 'entity', 'c', { key: 'cam', domain: 'camera', name: 'Cam' }), 'c')
    expect(switches(root)).toEqual(['Record frames'])
    expect(control(root, 'Record frames').checked).toBe(false)
    control(root, 'Record frames').click()
    expect(changed().config.history).toEqual({ frame: true })
    control(root, 'Record frames').click()
    expect(changed().config.history).toEqual({})
  })
  it('a button, which has no state, records its commands only', () => {
    const { root } = mount(add(empty(), 'entity', 'b', { key: 'bell', domain: 'button', name: 'Bell' }), 'b')
    expect(switches(root)).toEqual(['Record commands'])
  })
})

it('a problem on a field focuses that field', async () => {
  const problem = (nodeId: string, inputId: string, message: string): VisualDiagnostic =>
    ({ nodeId, inputId, code: null, message, severity: 3, startLine: null, startColumn: null })
  const named = mount(lamp(), 'l', { diagnostics: [{ ...problem('l', 'name', 'Name this entity: it has no device, so Home Assistant shows it by its own name'), code: 'entity.name' }] })
  const goTo = (root: Element, message: string) => [...root.querySelectorAll<HTMLButtonElement>('.hs-insp-problems button')].find(b => b.textContent?.includes(message))!
  goTo(named.root, 'Name this entity').click()
  expect(document.activeElement).toBe(named.root.querySelector('input[aria-label="Entity name"]'))
  // Still in the tab order.
  expect(named.root.querySelector<HTMLInputElement>('input[aria-label="Entity name"]')!.tabIndex).toBe(0)
  dispose?.()
  document.body.innerHTML = ''

  const select = mount(add(empty(), 'entity', 's', { key: 'mode', domain: 'select', name: 'Mode' }), 's', { diagnostics: [problem('s', 'option:options', 'Mode needs options: a select cannot work without it')] })
  goTo(select.root, 'Mode needs options').click()
  const options = control<HTMLTextAreaElement>(select.root, 'Options')
  expect(document.activeElement).toBe(options)
  // The problem is also said under the field.
  expect(row(select.root, 'option:options')!.textContent).toContain('Mode needs options: a select cannot work without it')
  dispose?.()
  document.body.innerHTML = ''

  const keyed = mount(lamp(), 'l', { diagnostics: [problem('l', 'key', 'The key lamp is already used by Desk: give this entity its own')] })
  goTo(keyed.root, 'already used').click()
  expect(document.activeElement).toBe(control(keyed.root, 'Key'))
})

it('the entity id line shows once Home Assistant has the entity', () => {
  const none = mount(lamp(), 'l', { saved: { keys: ['lamp'], entityIds: {} } })
  expect(none.root.querySelector('button[aria-label^="Show "]')).toBeNull()
  dispose?.()
  document.body.innerHTML = ''

  const { root } = mount(lamp(), 'l', { saved: { keys: ['lamp'], entityIds: { lamp: 'light.lamp' } } })
  const line = root.querySelector('code')!
  expect(line.textContent).toBe('light.lamp')
  const shown: unknown[] = []
  document.addEventListener('hass-more-info', event => shown.push((event as CustomEvent).detail))
  const show = root.querySelector<HTMLButtonElement>('button[aria-label="Show light.lamp in Home Assistant"]')!
  expect(show.textContent).toBe('Show')
  show.click()
  expect(shown).toEqual([{ entityId: 'light.lamp' }])
})

it('the inspector hands the panel what Home Assistant has of the saved entities', () => {
  const root = document.createElement('div')
  root.className = 'hs-app'
  document.body.append(root)
  const graph = lamp()
  client = new QueryClient()
  dispose = render(() => (
    <QueryClientProvider client={client!}>
      <HassCtx.Provider value={{ callWS: async () => ({}) } as never}>
        <Inspector graph={graph} catalog={nodeCatalog} diagnostics={[]} commit={() => {}} docked selected={graph.nodes[0]} selectedIds={['l']}
          savedEntities={{ keys: ['lamp'], entityIds: { lamp: 'light.lamp' } }} />
      </HassCtx.Provider>
    </QueryClientProvider>
  ), root)
  expect(control(root, 'Key').readOnly).toBe(true)
  expect(root.querySelector('code')!.textContent).toBe('light.lamp')
})
