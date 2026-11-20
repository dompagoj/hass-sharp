import { afterEach, describe, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { DefinitionPanel, deviceMemory, type DefinitionLiveEntity, type DeviceMemory } from '../src/entities/DefinitionPanel'
import { definitionBody, draftGraph, editorGraph, newGraphDefinition, type DefinitionBody, type DefinitionDevice, type EditorDefinition } from '../src/entities/model'
import type { VisualDiagnostic } from '../src/visual/api'
import { addGraphNode, nodeCatalog } from '../src/visual/contract'
import { createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'

// The definition form edits `graph.definition` through `commit`, one undo step per change, as the inspector's fields
// change a step: the device and the settings of an entity definition built from steps, whose entities are its Entity
// nodes (edited in their own section, tests/entity-node-config.test.tsx).
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
  logic: { kind: 'graph', graph: { version: 5, name: 'Plug', nodes: [
    { id: 'relay', kind: 'entity', config: { key: 'relay', domain: 'switch', name: 'Relay' } },
    { id: 'power', kind: 'entity', config: { key: 'power', domain: 'sensor', name: 'Power', options: { unit: 'W' } } },
  ], edges: [] } },
})
const definition = (graph: VisualGraph) => graph.definition as unknown as EditorDefinition

/** The form on `graph`. `device`: what the editor remembers of a device turned off, and the saved document's device. */
function mount(graph: VisualGraph, options: {
  diagnostics?: VisualDiagnostic[]; liveEntities?: DefinitionLiveEntity[]
  memory?: DeviceMemory; savedDevice?: DefinitionDevice | null
} = {}) {
  const el = document.createElement('div')
  el.className = 'hs-app'
  document.body.append(el)
  const history = createGraphHistory(graph)
  const [diagnostics, setDiagnostics] = createSignal(options.diagnostics ?? [])
  const [located, setLocated] = createSignal<{ target: string }>()
  const notices: string[] = []
  dispose = render(() => (
    <DefinitionPanel graph={history.graph()} catalog={nodeCatalog} diagnostics={diagnostics()} commit={history.commit}
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
const problem = (code: string, message: string, inputId: string | null, nodeId: string | null = null): VisualDiagnostic =>
  ({ nodeId, inputId, portId: null, code, message, severity: 3, startLine: null, startColumn: null })

describe('the overview', () => {
  it('has the device and the settings, and no Entities section: its entities are its Entity nodes', () => {
    const { el } = mount(editorGraph(plug()))
    const headings = [...el.querySelectorAll('h2, h3, h4, legend, [role="heading"]')].map(h => h.textContent?.trim())
    expect(el.querySelector('[aria-label="Device"]')).not.toBeNull()
    expect(el.querySelector('[aria-label="Settings"]')).not.toBeNull()
    expect(el.querySelector('[aria-label="Entities"]')).toBeNull()
    expect(headings).not.toContain('Entities')
    expect([...el.querySelectorAll('button')].map(b => b.textContent?.trim())).not.toContain('Add entity')
    expect(el.querySelector('[data-entity-key]')).toBeNull()
    expect(el.textContent).not.toContain('Relay')
    // A definition with no Entity node has no entities to show either: the problem saying so is above the form.
    dispose!(); document.body.innerHTML = ''
    const bare = mount({ ...editorGraph(plug()), nodes: [] }, {
      diagnostics: [problem('definition.entitiesRequired', 'Add an Entity node: an entity definition creates the entities on its canvas, and has none', null)],
    }).el
    expect(bare.querySelector('[aria-label="Problems of this entity definition"]')!.textContent).toBe('Add an Entity node: an entity definition creates the entities on its canvas, and has none')
    expect(bare.querySelector('[aria-label="Entities"]')).toBeNull()
  })

  it('opens and focuses the row of a setting problem chosen in the Problems tab, and the list for one no row shows', async () => {
    const { el, setLocated } = mount(editorGraph(plug()), { diagnostics: [problem('definition.name', 'Give the definition a name of up to 120 characters', null)] })
    expect(settingRow(el, 'host').querySelector('.hs-def-row__body')).toBeNull()
    setLocated({ target: 'setting:host' })
    await vi.waitFor(() => expect(document.activeElement).toBe(control(settingRow(el, 'host'), 'key')))
    setLocated({ target: 'problems' })
    await vi.waitFor(() => expect(document.activeElement).toBe(el.querySelector('[aria-label="Problems of this entity definition"]')))
  })

  it('shows a problem about a setting on its row and the definition\'s own above the form, and none that is on a node', () => {
    const { el, setDiagnostics } = mount(editorGraph(plug()))
    setDiagnostics([
      problem('definition.settingType', 'Address takes a text', 'host'),
      problem('definition.name', 'Give the definition a name of up to 120 characters', null),
      // An Entity node's problem is the node's: the canvas and its section show it, not the form.
      { ...problem('definition.entityOption', 'Unit of Power takes a text', 'option:unit', 'power') },
      { ...problem('entity.name', 'Give the entity a name', 'name', 'relay') },
    ])
    expect(settingRow(el, 'host').textContent).toContain('Address takes a text')
    const top = el.querySelector('[aria-label="Problems of this entity definition"]')!
    expect(top.textContent).toBe('Give the definition a name of up to 120 characters')
    expect(el.textContent).not.toContain('Unit of Power takes a text')
    expect(el.textContent).not.toContain('Give the entity a name')
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
    expect(current().nodes.find(n => n.id === 'read')!.config.key).toBe('address')
    const row = settingRow(el, 'address')
    type(control(row, 'label'), 'Host name')
    type(control(row, 'value'), 'plug.local')
    expect(definition(current()).settings[0]).toEqual({ key: 'address', label: 'Host name', type: 'string', value: 'plug.local' })
    history.undo(); history.undo(); history.undo()
    expect(definition(current()).settings[0]).toEqual({ key: 'host', label: 'Address', type: 'string', value: '10.0.0.7' })
    expect(current().nodes.find(n => n.id === 'read')!.config.key).toBe('host')
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

  it('keeps the row open, with the same fields and the focus where it was, when a setting\'s key is renamed', () => {
    const { el, graph } = mount(editorGraph(plug()))
    const row = expand(settingRow(el, 'host'))
    const key = control(row, 'key'), label = control(row, 'label')
    // Taken with Enter: the focus stays in the key's field.
    key.focus()
    key.value = 'address'
    key.dispatchEvent(new Event('input', { bubbles: true }))
    key.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    expect(definition(graph()).settings[0].key).toBe('address')
    expect(control(settingRow(el, 'address'), 'key')).toBe(key)
    expect(document.activeElement).toBe(key)
    // Taken as the focus moves on to the next field: that field keeps it.
    key.value = 'hostname'
    key.dispatchEvent(new Event('input', { bubbles: true }))
    label.focus()
    expect(definition(graph()).settings[0].key).toBe('hostname')
    expect(label.isConnected).toBe(true)
    expect(control(settingRow(el, 'hostname'), 'label')).toBe(label)
    expect(document.activeElement).toBe(label)
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
    const { el, graph: current } = mount(graph)
    const row = expand(settingRow(el, 'password'))
    expect(control(row, 'key').readOnly).toBe(true)
    expect(row.querySelector('[data-field="key"]')!.textContent).toContain('To rename this setting, type its value again first.')
    type(control(row, 'value'), 'hunter2')
    type(control(row, 'key'), 'secret')
    expect(definition(current()).settings[1]).toMatchObject({ key: 'secret', value: 'hunter2' })
    expect(current().nodes.find(n => n.id === 'read')!.config.key).toBe('secret')
    expect(el.innerHTML).not.toContain('hunter2')
    dispose!(); document.body.innerHTML = ''
    // Cleared, it has no saved value to lose.
    const other = mount(editorGraph(plug()))
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
  const { el } = mount(editorGraph(plug()))
  for (const row of el.querySelectorAll<HTMLElement>('.hs-def-row')) expand(row)
  for (const control of el.querySelectorAll<HTMLElement>('input, select, textarea, button')) {
    const id = control.id
    const named = control.getAttribute('aria-label') || control.textContent?.trim() || (id && el.querySelector(`label[for="${id}"]`)?.textContent?.trim())
    expect(named, control.outerHTML).toBeTruthy()
  }
  vi.restoreAllMocks()
})
