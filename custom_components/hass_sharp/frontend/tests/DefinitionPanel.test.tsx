import { afterEach, describe, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { DefinitionPanel, type DefinitionLiveEntity } from '../src/entities/DefinitionPanel'
import { definitionBody, draftGraph, editorGraph, newGraphDefinition, type DefinitionBody, type EditorDefinition } from '../src/entities/model'
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

function mount(graph: VisualGraph, options: { saved?: boolean; diagnostics?: VisualDiagnostic[]; liveEntities?: DefinitionLiveEntity[] } = {}) {
  const el = document.createElement('div')
  el.className = 'hs-app'
  document.body.append(el)
  const history = createGraphHistory(graph)
  const [diagnostics, setDiagnostics] = createSignal(options.diagnostics ?? [])
  const notices: string[] = []
  dispose = render(() => (
    <DefinitionPanel graph={history.graph()} catalog={nodeCatalog} diagnostics={diagnostics()} saved={!!options.saved} commit={history.commit}
      liveEntities={options.liveEntities} onNotice={text => notices.push(text)} />
  ), el)
  return { el, history, notices, setDiagnostics, graph: () => history.graph() }
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
