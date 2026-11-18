import { afterEach, describe, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { DefinitionPanel, deviceMemory, type DefinitionLiveEntity, type DeviceMemory } from '../src/entities/DefinitionPanel'
import { definitionBody, draftGraph, editorGraph, newGraphDefinition, type DefinitionBody, type DefinitionDevice, type EditorDefinition } from '../src/entities/model'
import type { VisualDiagnostic } from '../src/visual/api'
import { addGraphNode, connectData, nodeCatalog, validateGraph } from '../src/visual/contract'
import { createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'

// The definition form edits `graph.definition` through `commit`, one undo step per change, as the inspector's fields
// change a step: the entities, the device and the settings of an entity definition built from steps.
let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); dispose = undefined; document.body.innerHTML = '' })

const lit = (type: string, value: unknown) => ({ kind: 'literal' as const, type, value })
const plug = (): DefinitionBody => ({
  name: 'Plug',
  device: { manufacturer: 'Acme', model: 'P1', via: { definition: 'hub', deviceId: null } },
  settings: [
    { key: 'host', label: 'Address', type: 'string', value: '10.0.0.7' },
    { key: 'password', label: 'Password', type: 'string', secret: true, value: null, hasValue: true },
  ],
  entities: [{ key: 'relay', domain: 'switch', name: 'Relay' }, { key: 'power', domain: 'sensor', name: 'Power', options: { unit: 'W' } }],
  logic: { kind: 'graph', graph: { version: 5, name: 'Plug', nodes: [], edges: [] } },
})
const definition = (graph: VisualGraph) => graph.definition as unknown as EditorDefinition

/**
 * The form on `graph`. `saved`: the saved definition's entity keys (`true`: those of `graph`), which are fixed;
 * `device`: what the editor remembers of a device turned off, and the saved document's device.
 */
function mount(graph: VisualGraph, options: {
  saved?: boolean | string[]; diagnostics?: VisualDiagnostic[]; liveEntities?: DefinitionLiveEntity[]
  memory?: DeviceMemory; savedDevice?: DefinitionDevice | null
} = {}) {
  const el = document.createElement('div')
  el.className = 'hs-app'
  document.body.append(el)
  const history = createGraphHistory(graph)
  const [diagnostics, setDiagnostics] = createSignal(options.diagnostics ?? [])
  const [located, setLocated] = createSignal<{ target: string }>()
  const savedKeys = options.saved === true ? definition(graph).entities.map(e => e.key) : options.saved || []
  const notices: string[] = []
  dispose = render(() => (
    <DefinitionPanel graph={history.graph()} catalog={nodeCatalog} diagnostics={diagnostics()} savedKeys={savedKeys} commit={history.commit}
      liveEntities={options.liveEntities} onNotice={text => notices.push(text)} deviceMemory={options.memory} savedDevice={options.savedDevice}
      located={located()} />
  ), el)
  return { el, history, notices, setDiagnostics, setLocated, graph: () => history.graph() }
}
const button = (root: Element, name: string) => {
  const found = [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === name || b.getAttribute('aria-label') === name)
  expect(found, name).toBeDefined()
  return found!
}
const entityRow = (el: Element, key: string) => el.querySelector<HTMLElement>(`[data-entity-key="${key}"]`)!
const settingRow = (el: Element, key: string) => el.querySelector<HTMLElement>(`[data-setting-key="${key}"]`)!
/** Opens a row (its head is a disclosure button). */
const expand = (row: HTMLElement) => {
  const head = row.querySelector<HTMLButtonElement>('.hs-def-row__head')!
  if (head.getAttribute('aria-expanded') !== 'true') head.click()
  return row
}
const control = (root: Element, field: string) => root.querySelector<HTMLInputElement & HTMLSelectElement>(`[data-field="${field}"] :is(input, select, textarea)`)!
/** Types into a text field and leaves it with Enter, as a person does: the field commits once. */
const type = (input: HTMLInputElement | HTMLTextAreaElement, text: string) => {
  input.value = text
  input.dispatchEvent(new Event('input', { bubbles: true }))
  input.dispatchEvent(new FocusEvent('blur'))
}
const choose = (select: HTMLSelectElement, value: string) => { select.value = value; select.dispatchEvent(new Event('change', { bubbles: true })) }
const toggle = (input: HTMLInputElement) => input.click()
const dialog = () => document.querySelector<HTMLDialogElement>('dialog[open]')

describe('the entities', () => {
  it('edits every field of an entity, each change one undo step', () => {
    const { el, history, graph } = mount(editorGraph(plug()))
    const row = expand(entityRow(el, 'relay'))
    expect(row.querySelector('.hs-def-row__head')!.textContent).toContain('Relay')
    expect(row.querySelector('.hs-def-row__head')!.textContent).toContain('Switch · relay')
    type(control(row, 'name'), 'Main relay')
    expect(definition(graph()).entities[0].name).toBe('Main relay')
    type(control(row, 'deviceClass'), 'outlet')
    type(control(row, 'icon'), 'mdi:power-socket')
    choose(control(row, 'entityCategory'), 'config')
    toggle(control(row, 'enabledByDefault'))
    expect(definition(graph()).entities[0]).toEqual({ key: 'relay', domain: 'switch', name: 'Main relay', deviceClass: 'outlet', icon: 'mdi:power-socket', entityCategory: 'config', enabledByDefault: false })
    // Cleared fields leave their members out, as an absent member means none (enabled by default: on).
    toggle(control(row, 'enabledByDefault'))
    choose(control(row, 'entityCategory'), '')
    type(control(row, 'icon'), '')
    expect(definition(graph()).entities[0]).toEqual({ key: 'relay', domain: 'switch', name: 'Main relay', deviceClass: 'outlet' })
    // Each change was one step.
    history.undo(); history.undo(); history.undo()
    expect(definition(graph()).entities[0]).toEqual({ key: 'relay', domain: 'switch', name: 'Main relay', deviceClass: 'outlet', icon: 'mdi:power-socket', entityCategory: 'config', enabledByDefault: false })
    for (let i = 0; i < 5; i++) history.undo()
    expect(definition(graph()).entities[0]).toEqual({ key: 'relay', domain: 'switch', name: 'Relay' })
    expect(history.canUndo()).toBe(false)
  })

  it('offers the options of the entity\'s type: a sensor\'s unit, state class and list of options', () => {
    const { el, graph, history } = mount(editorGraph(plug()))
    const row = expand(entityRow(el, 'power'))
    expect(control(row, 'option:unit').value).toBe('W')
    expect([...control(row, 'option:stateClass').options].map(o => o.textContent)).toEqual(['None', 'Measurement', 'Total', 'Total increasing'])
    choose(control(row, 'option:stateClass'), 'measurement')
    type(control(row, 'option:options'), 'low\n high\n\n')
    expect(definition(graph()).entities[1].options).toEqual({ unit: 'W', stateClass: 'measurement', options: ['low', 'high'] })
    type(control(row, 'option:unit'), '')
    choose(control(row, 'option:stateClass'), '')
    type(control(row, 'option:options'), '')
    expect(definition(graph()).entities[1]).toEqual({ key: 'power', domain: 'sensor', name: 'Power' })
    history.undo()
    expect(definition(graph()).entities[1].options).toEqual({ options: ['low', 'high'] })
    // A switch takes no options.
    expect(expand(entityRow(el, 'relay')).querySelector('[data-field^="option:"]')).toBeNull()
  })

  it('changes an entity\'s type with its steps in one undo step, saying which wires no longer fit', () => {
    let graph = editorGraph(plug())
    graph = updateNode(addGraphNode(graph, 'entity_is', 'is', nodeCatalog), 'is', { config: { entity: lit('entity_id', 'binary_sensor.door'), state: lit('string', 'on') } })
    graph = updateNode(addGraphNode(graph, 'set_entity_state', 'set', nodeCatalog), 'set', { config: { entity: 'relay' } })
    graph = connectData(graph, 'is', 'value', 'set', 'is_on', nodeCatalog)
    const { el, notices, history, graph: current } = mount(graph)
    const row = expand(entityRow(el, 'relay'))
    choose(control(row, 'domain'), 'sensor')
    expect(definition(current()).entities[0]).toEqual({ key: 'relay', domain: 'sensor', name: 'Relay' })
    expect(current().edges).toEqual([])
    expect(notices).toEqual(['Removed 1 connection that no longer fits: On.'])
    history.undo()
    expect(definition(current()).entities[0].domain).toBe('switch')
    expect(current().edges).toHaveLength(1)
  })

  it('renames the steps\' references with the key before the first save, and keeps a key that is not one', () => {
    let graph = editorGraph(newGraphDefinition('Any motion'))
    graph = updateNode(addGraphNode(graph, 'entity_state', 's', nodeCatalog), 's', { config: { entity: 'any_motion' } })
    const { el, graph: current, history } = mount(graph)
    const row = expand(entityRow(el, 'any_motion'))
    const key = control(row, 'key')
    type(key, 'Motion!')
    // The text stays in the field with the rule; the graph is as it was.
    expect(key.value).toBe('Motion!')
    expect(row.textContent).toContain('Lower-case letters, digits and underscores, starting with a letter; at most 40 characters.')
    expect(definition(current()).entities[0].key).toBe('any_motion')
    type(key, 'motion')
    expect(definition(current()).entities[0].key).toBe('motion')
    expect(current().nodes[0].config.entity).toBe('motion')
    // The row stays open under its new key.
    expect(entityRow(el, 'motion').querySelector('[data-field="key"]')).not.toBeNull()
    history.undo()
    expect(current().nodes[0].config.entity).toBe('any_motion')
  })

  it('shows a saved entity\'s key read only, and its Home Assistant entity id', () => {
    const { el } = mount(editorGraph(plug()), { saved: true, liveEntities: [{ key: 'relay', entityId: 'switch.plug_relay' }] })
    const row = expand(entityRow(el, 'relay'))
    const key = control(row, 'key')
    expect(key.readOnly).toBe(true)
    expect(key.value).toBe('relay')
    expect(row.textContent).toContain("The key is part of the entity's identity in Home Assistant and cannot change.")
    expect(row.querySelector('.hs-def-row__id code')!.textContent).toBe('switch.plug_relay')
    const opened: unknown[] = []
    el.addEventListener('hass-more-info', event => opened.push((event as CustomEvent).detail))
    button(row, 'Show switch.plug_relay in Home Assistant').click()
    expect(opened).toEqual([{ entityId: 'switch.plug_relay' }])
    expect(entityRow(el, 'power').querySelector('.hs-def-row__id')).toBeNull()
  })

  it('keeps the key of an entity added to a saved definition open until it is saved, and fixes a key the saved definition has', () => {
    const { el, graph } = mount(editorGraph(plug()), { saved: true })
    button(el, 'Add entity').click()
    const added = entityRow(el, 'entity')
    expect(control(added, 'key').readOnly).toBe(false)
    type(control(added, 'key'), 'meter')
    expect(control(entityRow(el, 'meter'), 'key').readOnly).toBe(false)
    expect(entityRow(el, 'meter').textContent).toContain('It cannot change once saved.')
    // Power removed and added back under its key is the same entity in Home Assistant: its key is fixed again.
    button(expand(entityRow(el, 'power')), 'Remove entity Power').click()
    expect(entityRow(el, 'power')).toBeNull()
    button(el, 'Add entity').click()
    type(control(entityRow(el, 'entity'), 'key'), 'power')
    const back = entityRow(el, 'power')
    expect(control(back, 'key').readOnly).toBe(true)
    expect(back.textContent).toContain("The key is part of the entity's identity in Home Assistant and cannot change.")
    expect(definition(graph()).entities.map(e => e.key)).toEqual(['relay', 'meter', 'power'])
  })

  it('trims an entity\'s name, and names its body as the head\'s only while it is shown', () => {
    const { el, graph } = mount(editorGraph(plug()))
    const row = entityRow(el, 'relay')
    const head = row.querySelector('.hs-def-row__head')!
    expect(head.hasAttribute('aria-controls')).toBe(false)
    expand(row)
    expect(document.getElementById(head.getAttribute('aria-controls')!)).toBe(row.querySelector('.hs-def-row__body'))
    type(control(row, 'name'), '  Main relay ')
    expect(definition(graph()).entities[0].name).toBe('Main relay')
    type(control(row, 'name'), '   ')
    expect(definition(graph()).entities[0].name).toBeUndefined()
  })

  it('opens and focuses the row of a problem chosen in the Problems tab', async () => {
    const diagnostics = [{ nodeId: null, message: 'Entity key relay is wrong', severity: 3, startLine: null, startColumn: null, inputId: 'relay', portId: null, code: 'definition.entityKey' }]
    const { el, setLocated } = mount(editorGraph(plug()), { diagnostics })
    const row = entityRow(el, 'relay')
    // Opened for its problem, then closed by the person.
    row.querySelector<HTMLButtonElement>('.hs-def-row__head')!.click()
    expect(row.querySelector('.hs-def-row__body')).toBeNull()
    setLocated({ target: 'entity:relay' })
    expect(row.querySelector('.hs-def-row__body')).not.toBeNull()
    await vi.waitFor(() => expect(document.activeElement).toBe(control(row, 'name')))
    // A setting's problem, the same way.
    expect(settingRow(el, 'host').querySelector('.hs-def-row__body')).toBeNull()
    setLocated({ target: 'setting:host' })
    await vi.waitFor(() => expect(document.activeElement).toBe(control(settingRow(el, 'host'), 'key')))
  })

  it('adds an entity with a free key and opens it; removing one that steps use asks first and leaves a problem naming it', async () => {
    let graph = editorGraph(plug())
    graph = updateNode(addGraphNode(graph, 'entity_command', 'on', nodeCatalog), 'on', { config: { entity: 'relay', command: 'turn_on' } })
    const { el, graph: current, history } = mount(graph)
    button(el, 'Add entity').click()
    expect(definition(current()).entities.map(e => e.key)).toEqual(['relay', 'power', 'entity'])
    expect(entityRow(el, 'entity').querySelector('[data-field="name"]')).not.toBeNull()
    button(el, 'Add entity').click()
    expect(definition(current()).entities.at(-1)).toEqual({ key: 'entity_2', domain: 'binary_sensor' })
    // An entity no step uses goes at once.
    button(entityRow(el, 'entity_2'), 'Remove entity entity_2').click()
    expect(dialog()).toBeNull()
    expect(definition(current()).entities).toHaveLength(3)
    button(expand(entityRow(el, 'relay')), 'Remove entity Relay').click()
    expect(dialog()!.textContent).toContain('1 step uses this entity. It will show a problem until you change or remove it.')
    button(dialog()!, 'Remove').click()
    expect(definition(current()).entities.map(e => e.key)).toEqual(['power', 'entity'])
    expect(validateGraph(current(), nodeCatalog).map(d => d.message).join('\n')).toContain('relay')
    history.undo()
    expect(definition(current()).entities.map(e => e.key)).toEqual(['relay', 'power', 'entity'])
  })

  it('asks before removing the last entity, which the definition needs', () => {
    const { el, graph } = mount(editorGraph(newGraphDefinition('Any motion')))
    button(expand(entityRow(el, 'any_motion')), 'Remove entity Any motion').click()
    expect(dialog()!.textContent).toContain('It is the only entity')
    button(dialog()!, 'Remove').click()
    expect(definition(graph()).entities).toEqual([])
    expect(el.textContent).toContain('This entity definition has no entities yet.')
  })

  it('shows a problem about an entity or a setting on its row, opened, and the definition\'s own above the form', async () => {
    const problem = (code: string, message: string, inputId: string | null): VisualDiagnostic => ({ nodeId: null, inputId, code, message, severity: 3, startLine: null, startColumn: null })
    const { el, setDiagnostics } = mount(editorGraph(plug()))
    expect(entityRow(el, 'power').querySelector('.hs-def-row__head')!.getAttribute('aria-expanded')).toBe('false')
    setDiagnostics([
      problem('definition.entityOption', 'Option unit of entity power takes a text', 'power'),
      problem('definition.settingType', 'Address takes a text', 'host'),
      problem('definition.name', 'Give the definition a name of up to 120 characters', null),
    ])
    const power = entityRow(el, 'power')
    expect(power.querySelector('.hs-def-row__head')!.getAttribute('aria-expanded')).toBe('true')
    expect(power.querySelector('.hs-def-row__mark')!.getAttribute('aria-label')).toBe('1 problem')
    expect(power.querySelector('.hs-def-row__body')!.textContent).toContain('Option unit of entity power takes a text')
    expect(settingRow(el, 'host').textContent).toContain('Address takes a text')
    const top = el.querySelector('[aria-label="Problems of this entity definition"]')!
    expect(top.textContent).toBe('Give the definition a name of up to 120 characters')
    expect(entityRow(el, 'relay').textContent).not.toContain('takes a text')
  })
})

describe('the device', () => {
  it('groups the entities as a device with its details, and keeps its parent when turned off and on', () => {
    const { el, graph, history } = mount(editorGraph(plug()))
    const on = control(el.querySelector('[aria-label="Device"]')!, 'device')
    expect(on.checked).toBe(true)
    expect(on.getAttribute('aria-describedby')).toBeTruthy()
    const device = el.querySelector('[aria-label="Device"]')!
    type(control(device, 'swVersion'), '1.2')
    type(control(device, 'suggestedArea'), 'Kitchen')
    type(control(device, 'model'), '')
    expect(definition(graph()).device).toEqual({ manufacturer: 'Acme', swVersion: '1.2', suggestedArea: 'Kitchen', via: { definition: 'hub', deviceId: null } })
    toggle(on)
    expect(definition(graph()).device).toBeNull()
    expect(device.querySelector('[data-field="manufacturer"]')).toBeNull()
    toggle(on)
    expect(definition(graph()).device).toEqual({ manufacturer: 'Acme', swVersion: '1.2', suggestedArea: 'Kitchen', via: { definition: 'hub', deviceId: null } })
    history.undo(); history.undo()
    expect(definition(graph()).device?.model).toBeUndefined()
    history.undo()
    expect(definition(graph()).device?.model).toBe('P1')
  })

  it('remembers the device turned off for the editor, past the form being shown again, and else turns the saved one back on', () => {
    const memory = deviceMemory()
    const first = mount(editorGraph(plug()), { memory })
    toggle(control(first.el.querySelector('[aria-label="Device"]')!, 'device'))
    const off = first.graph()
    expect(definition(off).device).toBeNull()
    // A step selected: the form goes; nothing selected: it is back, on the same graph.
    dispose!(); document.body.innerHTML = ''
    const again = mount(off, { memory })
    toggle(control(again.el.querySelector('[aria-label="Device"]')!, 'device'))
    expect(definition(again.graph()).device).toEqual({ manufacturer: 'Acme', model: 'P1', via: { definition: 'hub', deviceId: null } })
    // Nothing remembered (a draft restored with the device off): the saved document's device.
    dispose!(); document.body.innerHTML = ''
    const saved: DefinitionDevice = { manufacturer: 'Acme', model: 'P2', via: { definition: 'hub', deviceId: null } }
    const restored = mount(off, { memory: deviceMemory(), savedDevice: saved })
    toggle(control(restored.el.querySelector('[aria-label="Device"]')!, 'device'))
    expect(definition(restored.graph()).device).toEqual(saved)
  })

  it('starts a device of a definition that had none with no details', () => {
    const { el, graph } = mount(editorGraph(newGraphDefinition('Any motion')))
    const device = el.querySelector('[aria-label="Device"]')!
    expect(device.querySelector('[data-field="manufacturer"]')).toBeNull()
    toggle(control(device, 'device'))
    expect(definition(graph()).device).toEqual({})
    for (const field of ['manufacturer', 'model', 'swVersion', 'hwVersion', 'configurationUrl', 'suggestedArea']) expect(control(device, field), field).not.toBeNull()
    type(control(device, 'manufacturer'), ' Acme ')
    expect(definition(graph()).device).toEqual({ manufacturer: 'Acme' })
  })
})

describe('the settings', () => {
  it('adds a setting, edits its key, label, type and value, and renames the steps that read it', () => {
    let graph = editorGraph(plug())
    graph = updateNode(addGraphNode(graph, 'setting', 'read', nodeCatalog), 'read', { config: { key: 'host' } })
    const { el, graph: current, history } = mount(graph)
    const host = expand(settingRow(el, 'host'))
    expect(control(host, 'value').value).toBe('10.0.0.7')
    type(control(host, 'key'), 'address')
    expect(current().nodes[0].config.key).toBe('address')
    const row = settingRow(el, 'address')
    type(control(row, 'label'), 'Host name')
    type(control(row, 'value'), 'plug.local')
    expect(definition(current()).settings[0]).toEqual({ key: 'address', label: 'Host name', type: 'string', value: 'plug.local' })
    history.undo(); history.undo(); history.undo()
    expect(definition(current()).settings[0]).toEqual({ key: 'host', label: 'Address', type: 'string', value: '10.0.0.7' })
    expect(current().nodes[0].config.key).toBe('host')
    button(el, 'Add setting').click()
    const added = settingRow(el, 'setting')
    choose(control(added, 'type'), 'number')
    type(control(added, 'value'), 'four')
    expect(added.textContent).toContain('Enter a number.')
    type(control(added, 'value'), '4.5')
    choose(control(added, 'type'), 'boolean')
    expect(definition(current()).settings.at(-1)).toEqual({ key: 'setting', type: 'boolean', value: null })
    choose(control(added, 'value'), 'true')
    expect(definition(current()).settings.at(-1)).toEqual({ key: 'setting', type: 'boolean', value: true })
  })

  it('takes any key that is not blank and not another setting\'s, trimmed, and trims the label (ruling R15)', () => {
    const { el, graph } = mount(editorGraph(plug()))
    const host = expand(settingRow(el, 'host'))
    expect(host.querySelector('[data-field="key"]')!.textContent).toContain('Any text that is not blank and not another setting\'s key. Setting steps that read it follow it.')
    type(control(host, 'key'), '  Host name ')
    expect(definition(graph()).settings[0].key).toBe('Host name')
    const row = settingRow(el, 'Host name')
    type(control(row, 'key'), '   ')
    expect(row.textContent).toContain('Give the setting a key.')
    type(control(row, 'key'), 'password')
    expect(row.textContent).toContain('Another one already has this key; give each a key of its own.')
    expect(definition(graph()).settings.map(s => s.key)).toEqual(['Host name', 'password'])
    type(control(row, 'label'), '  Address  ')
    expect(definition(graph()).settings[0].label).toBe('Address')
  })

  it('says when a new type removed a setting\'s value, and not when the value fits it', () => {
    const { el, graph, notices } = mount(editorGraph(plug()))
    const host = expand(settingRow(el, 'host'))
    choose(control(host, 'type'), 'number')
    expect(definition(graph()).settings[0].value).toBeNull()
    expect(notices).toEqual(['The value was removed because it does not fit the new type.'])
    type(control(host, 'value'), '4')
    choose(control(host, 'type'), 'string')
    expect(definition(graph()).settings[0].value).toBe('4')
    expect(notices).toHaveLength(1)
    // A saved secret's value is not here to convert: a new type removes it.
    const password = expand(settingRow(el, 'password'))
    choose(control(password, 'type'), 'number')
    expect(definition(graph()).settings[1]).toMatchObject({ type: 'number', value: null, hasValue: false })
    expect(notices).toHaveLength(2)
  })

  it('renames a saved secret only once its value is typed again or cleared (ruling R16)', () => {
    let graph = editorGraph(plug())
    graph = updateNode(addGraphNode(graph, 'setting', 'read', nodeCatalog), 'read', { config: { key: 'password' } })
    const { el, graph: current } = mount(graph, { saved: true })
    const row = expand(settingRow(el, 'password'))
    expect(control(row, 'key').readOnly).toBe(true)
    expect(row.querySelector('[data-field="key"]')!.textContent).toContain('To rename this setting, type its value again first.')
    type(control(row, 'value'), 'hunter2')
    type(control(row, 'key'), 'secret')
    expect(definition(current()).settings[1]).toMatchObject({ key: 'secret', value: 'hunter2' })
    expect(current().nodes[0].config.key).toBe('secret')
    expect(el.innerHTML).not.toContain('hunter2')
    dispose!(); document.body.innerHTML = ''
    // Cleared, it has no saved value to lose.
    const other = mount(editorGraph(plug()), { saved: true })
    const cleared = expand(settingRow(other.el, 'password'))
    button(cleared, 'Clear value').click()
    expect(control(cleared, 'key').readOnly).toBe(false)
    type(control(cleared, 'key'), 'token')
    expect(definition(other.graph()).settings[1]).toMatchObject({ key: 'token', value: null, hasValue: false })
  })

  it('asks before removing a setting a step reads', () => {
    let graph = editorGraph(plug())
    graph = updateNode(addGraphNode(graph, 'setting', 'read', nodeCatalog), 'read', { config: { key: 'host' } })
    const { el, graph: current } = mount(graph)
    button(expand(settingRow(el, 'host')), 'Remove setting Address').click()
    expect(dialog()!.textContent).toContain('1 step uses this setting. It will show a problem until you change or remove it.')
    button(dialog()!, 'Cancel').click()
    expect(definition(current()).settings).toHaveLength(2)
    button(settingRow(el, 'host'), 'Remove setting Address').click()
    button(dialog()!, 'Remove').click()
    expect(definition(current()).settings.map(s => s.key)).toEqual(['password'])
  })

  it('never shows a secret, and sends it kept when untouched, the typed value when replaced, and no value when cleared', () => {
    const { el, graph, history } = mount(editorGraph(plug()))
    const row = expand(settingRow(el, 'password'))
    const input = control(row, 'value')
    expect(input.type).toBe('password')
    expect(input.value).toBe('')
    expect(input.placeholder).toBe('Saved; type to replace')
    // Secret: on, and it cannot be turned off while its saved value is not here.
    expect(control(row, 'secret').checked).toBe(true)
    expect(control(row, 'secret').disabled).toBe(true)
    // Untouched: kept. Leaving the empty field changes nothing.
    input.dispatchEvent(new FocusEvent('blur'))
    expect(history.canUndo()).toBe(false)
    const sent = (current: VisualGraph) => definitionBody(current).settings!.find(s => s.key === 'password')
    expect(sent(graph())).toEqual({ key: 'password', label: 'Password', type: 'string', secret: true, keep: true })
    // Replaced: the typed value is sent, and the field empties again.
    type(input, 'hunter2')
    expect(input.value).toBe('')
    expect(input.placeholder).toBe('New value set; type to replace')
    expect(sent(graph())).toEqual({ key: 'password', label: 'Password', type: 'string', secret: true, value: 'hunter2' })
    expect(el.innerHTML).not.toContain('hunter2')
    expect(JSON.stringify(draftGraph(graph()))).not.toContain('hunter2')
    // Cleared: no value.
    button(row, 'Clear value').click()
    expect(sent(graph())).toEqual({ key: 'password', label: 'Password', type: 'string', secret: true, value: null })
    expect(input.placeholder).toBe('No value')
    expect(row.querySelector('button[aria-label="Clear value"]')).toBeNull()
    history.undo(); history.undo()
    expect(sent(graph())).toEqual({ key: 'password', label: 'Password', type: 'string', secret: true, keep: true })
  })

  it('makes a plain setting a secret, whose value is then no longer shown', () => {
    const { el, graph } = mount(editorGraph(plug()))
    const row = expand(settingRow(el, 'host'))
    toggle(control(row, 'secret'))
    expect(definition(graph()).settings[0]).toEqual({ key: 'host', label: 'Address', type: 'string', value: '10.0.0.7', secret: true })
    expect(control(row, 'value').type).toBe('password')
    expect(control(row, 'value').value).toBe('')
    expect(el.innerHTML).not.toContain('10.0.0.7')
  })
})

it('names every control', () => {
  const { el } = mount(editorGraph(plug()), { saved: true })
  for (const row of el.querySelectorAll<HTMLElement>('.hs-def-row')) expand(row)
  for (const control of el.querySelectorAll<HTMLElement>('input, select, textarea, button')) {
    const id = control.id
    const named = control.getAttribute('aria-label') || control.textContent?.trim() || (id && el.querySelector(`label[for="${id}"]`)?.textContent?.trim())
    expect(named, control.outerHTML).toBeTruthy()
  }
  vi.restoreAllMocks()
})
