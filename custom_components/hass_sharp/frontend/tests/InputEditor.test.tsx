import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import servicesFixture from '../../../../tests/fixtures/visual-services.json'
import { HassCtx } from '../src/context'
import { dataInputs, nodeCatalog, type GraphNode, type InputSpec, type Operand, type ServicesSection, type VisualCatalogSnapshot } from '../src/visual/contract'
import { InputEditor, type InputEditorProps } from '../src/visual/InputEditor'

const catalog: VisualCatalogSnapshot = { ...nodeCatalog, services: servicesFixture as unknown as ServicesSection }
const serviceInputs = (service: string, fields: string[] = []) => dataInputs({ id: 's', kind: 'service',
  config: { service, fields: Object.fromEntries(fields.map(f => [f, { kind: 'unset' }])), returnResponse: false } } as GraphNode, undefined, catalog)
const field = (service: string, name: string) => serviceInputs(service, [name])[`field:${name}`]
const spec = (types: string[], extra: Partial<InputSpec> = {}): InputSpec => ({ types, required: false, description: '', ...extra })
const registry = {
  entities: ['light.hall', 'light.porch', 'switch.fan'].map(entity_id => ({ entity_id, name: null, domain: entity_id.split('.')[0], area_id: null, device_id: null,
    device_class: null, supported_features: 0, unit: null, icon: null, disabled: false, state_options: null })),
  areas: [{ area_id: 'hall', name: 'Hall', floor_id: null }], devices: [], floors: [], labels: [{ label_id: 'night', name: 'Night' }],
}

let dispose: (() => void) | undefined
let client: QueryClient | undefined
afterEach(() => { dispose?.(); dispose = undefined; client?.clear(); document.body.innerHTML = '' })
type Change = { operand: Operand; coalesce?: string }
function mount(props: Partial<InputEditorProps> & { input: InputSpec }, initial?: Operand) {
  const host = document.createElement('div'); document.body.append(host)
  const [value, setValue] = createSignal<Operand | undefined>(initial)
  const changes: Change[] = []
  client = new QueryClient()
  dispose = render(() => <QueryClientProvider client={client!}><HassCtx.Provider value={{ callWS: async () => registry } as any}>
    <InputEditor variant="panel" label="Value" catalog={catalog} {...props} value={value()}
      onChange={(operand, options) => { changes.push({ operand, ...(options?.coalesce ? { coalesce: options.coalesce } : {}) }); setValue(operand) }} />
  </HassCtx.Provider></QueryClientProvider>, host)
  return { host, changes, value, setValue }
}
const q = <T extends Element = HTMLElement>(host: Element, selector: string) => { const found = host.querySelector<T>(selector); expect(found, selector).not.toBeNull(); return found! }
const type = (el: HTMLInputElement | HTMLTextAreaElement, text: string) => { el.value = text; el.dispatchEvent(new Event('input', { bubbles: true })) }
const key = (el: Element, k: string, init: KeyboardEventInit = {}) => el.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true, ...init }))
const blur = (el: Element) => el.dispatchEvent(new FocusEvent('blur'))
const menuItem = (host: Element, label: string) => [...host.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].find(b => b.textContent === label)!
const openMenu = (host: Element, label = 'Value mode') => q<HTMLButtonElement>(host, `button[aria-label="${label}"]`).click()

it('commits text once on blur, on Enter, and restores the committed text on Escape', () => {
  const { host, changes } = mount({ input: field('notify.notify', 'message'), label: 'Message' })
  const input = q<HTMLInputElement>(host, 'input[aria-label="Message"]')
  for (const text of ['H', 'He', 'Hello']) type(input, text)
  expect(changes).toEqual([])
  blur(input)
  expect(changes).toEqual([{ operand: { kind: 'literal', type: 'string', value: 'Hello' } }])
  blur(input)
  expect(changes).toHaveLength(1)
  type(input, 'Hello there'); key(input, 'Enter')
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'string', value: 'Hello there' })
  type(input, 'oops'); key(input, 'Escape')
  expect(input.value).toBe('Hello there')
  blur(input)
  expect(changes).toHaveLength(2)
  // Emptying the field clears the input.
  type(input, ''); blur(input)
  expect(changes.at(-1)!.operand).toEqual({ kind: 'unset' })
})

it('keeps an invalid number draft, explains it, and commits a number once', () => {
  const { host, changes } = mount({ input: field('hass_sharp_test.sectioned', 'level'), label: 'Level' })
  const input = q<HTMLInputElement>(host, 'input[aria-label="Level"]')
  type(input, '4x'); blur(input)
  expect(changes).toEqual([])
  expect(input.value).toBe('4x')
  expect(input.getAttribute('aria-invalid')).toBe('true')
  expect(host.querySelector(`#${input.getAttribute('aria-describedby')!.split(' ').at(-1)}`)!.textContent).toBe('Enter a finite number.')
  type(input, '4'); key(input, 'Enter')
  expect(changes).toEqual([{ operand: { kind: 'literal', type: 'number', value: 4 } }])
})

it('reports a slider drag under one coalesce key and nothing when the value is unchanged', () => {
  const { host, changes } = mount({ input: field('light.turn_on', 'brightness_pct'), label: 'Brightness' }, { kind: 'literal', type: 'number', value: 50 })
  const slider = q<HTMLInputElement>(host, 'input[type="range"]')
  expect(slider.getAttribute('aria-label')).toBe('Brightness')
  expect(slider.getAttribute('aria-valuetext')).toBe('50 %')
  const drag = (...values: number[]) => values.forEach((v, i) => { slider.value = String(v); slider.dispatchEvent(new Event(i === values.length - 1 ? 'change' : 'input', { bubbles: true })) })
  drag(60, 70, 70, 70)
  expect(changes.map(c => c.operand)).toEqual([{ kind: 'literal', type: 'number', value: 60 }, { kind: 'literal', type: 'number', value: 70 }])
  expect(changes[0].coalesce).toBeTruthy()
  expect(changes[1].coalesce).toBe(changes[0].coalesce)
  drag(80, 80)
  expect(changes).toHaveLength(3)
  expect(changes[2].coalesce).not.toBe(changes[0].coalesce)
  drag(80)
  expect(changes).toHaveLength(3)
  expect(q(host, 'output').textContent).toBe('80 %')
})

it('switches between value, variable and unset with a keyboard-operable mode menu', async () => {
  const variables = [{ name: 'flag', type: 'boolean' }, { name: 'count', type: 'number' }]
  const { host, changes } = mount({ input: spec(['boolean']), label: 'Enabled', variables }, { kind: 'literal', type: 'boolean', value: true })
  const trigger = q<HTMLButtonElement>(host, 'button[aria-label="Enabled mode"]')
  expect(trigger.getAttribute('aria-haspopup')).toBe('menu')
  key(trigger, 'ArrowDown')
  expect(trigger.getAttribute('aria-expanded')).toBe('true')
  expect([...host.querySelectorAll('[role="menuitem"]')].map(b => b.textContent)).toEqual(['Value', 'Variable…', 'Clear'])
  expect(document.activeElement).toBe(menuItem(host, 'Value'))
  key(host.querySelector('[role="menu"]')!, 'Escape')
  expect(host.querySelector('[role="menu"]')).toBeNull()

  openMenu(host, 'Enabled mode'); menuItem(host, 'Variable…').click()
  const select = q<HTMLSelectElement>(host, 'select[aria-label="Enabled variable"]')
  // Only variables assignable to the input are offered.
  expect([...select.options].filter(o => !o.disabled).map(o => o.value)).toEqual(['flag'])
  select.value = 'flag'; select.dispatchEvent(new Event('change', { bubbles: true }))
  expect(changes.at(-1)!.operand).toEqual({ kind: 'variable', name: 'flag' })
  expect(q(host, '.hs-input-editor__state').textContent).toBe('Variable flag')
  expect(host.querySelector('input[role="switch"]')).toBeNull()

  openMenu(host, 'Enabled mode'); menuItem(host, 'Clear').click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'unset' })
  expect(q<HTMLInputElement>(host, 'input[role="switch"]').checked).toBe(false)
  expect(host.textContent).toContain('Not set')

  openMenu(host, 'Enabled mode'); menuItem(host, 'Value').click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'boolean', value: false })
  expect(changes).toHaveLength(3)
  await Promise.resolve()
  expect(document.activeElement).toBe(host.querySelector('input[role="switch"]'))
  q<HTMLInputElement>(host, 'input[role="switch"]').click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'boolean', value: true })
})

it('renders the inline variant compactly with the mode menu closed', () => {
  const { host } = mount({ input: field('notify.notify', 'message'), label: 'Message', variant: 'inline' }, { kind: 'literal', type: 'string', value: 'Hi' })
  const root = q(host, '.hs-input-editor')
  expect(root.classList.contains('hs-input-editor--inline')).toBe(true)
  expect(root.getAttribute('role')).toBe('group')
  expect(root.getAttribute('aria-label')).toBe('Message')
  expect(host.querySelector('label')).toBeNull()
  expect(host.querySelector('.hs-field-hint')).toBeNull()
  expect(host.querySelector('[role="menu"]')).toBeNull()
  expect(q(host, 'button[aria-label="Message mode"]').getAttribute('aria-expanded')).toBe('false')
  expect(q<HTMLInputElement>(host, 'input[aria-label="Message"]').value).toBe('Hi')
})

it('labels the panel control and associates validation messages with it', () => {
  const { host } = mount({ input: spec(['string'], { description: 'Shown in the notification.' }), label: 'Title', errors: ['Input is required; connect a source or set a literal'] })
  const input = q<HTMLInputElement>(host, 'input[aria-label="Title"]')
  expect(q<HTMLLabelElement>(host, 'label').htmlFor).toBe(input.id)
  expect(input.getAttribute('aria-invalid')).toBe('true')
  const described = input.getAttribute('aria-describedby')!.split(' ').map(id => host.querySelector(`[id="${id}"]`)!.textContent)
  expect(described).toEqual(['Input is required; connect a source or set a literal', 'Shown in the notification.'])
})

it('validates JSON on commit and keeps the text on a parse error', () => {
  const { host, changes } = mount({ input: field('light.turn_on', 'hs_color'), label: 'HS colour' })
  const area = q<HTMLTextAreaElement>(host, 'textarea[aria-label="HS colour"]')
  type(area, '[30, 60'); blur(area)
  expect(changes).toEqual([])
  expect(area.value).toBe('[30, 60')
  expect(host.querySelector('.hs-error')!.textContent).toMatch(/^Invalid JSON/)
  // Enter adds a line in the JSON editor; Ctrl+Enter commits.
  type(area, '[30, 60]'); key(area, 'Enter')
  expect(changes).toEqual([])
  key(area, 'Enter', { ctrlKey: true })
  expect(changes).toEqual([{ operand: { kind: 'literal', type: 'array', value: [30, 60] } }])
  expect(host.querySelector('.hs-error')).toBeNull()
})

it('shows a wired input read-only and lets the mode menu replace the wire', () => {
  const { host, changes } = mount({ input: field('notify.notify', 'message'), label: 'Message', wiredFrom: 'Read entity · State' })
  expect(q(host, '.hs-input-editor__state').textContent).toBe('Connected to Read entity · State')
  expect(host.querySelector('input')).toBeNull()
  openMenu(host, 'Message mode'); menuItem(host, 'Clear').click()
  expect(changes).toEqual([{ operand: { kind: 'unset' } }])
})

it('authors an entity literal with the parametric type of a domain-filtered input', async () => {
  const { host, changes } = mount({ input: spec(['entity_id:light']), label: 'Lamp' })
  q<HTMLButtonElement>(host, 'button[aria-label="Lamp"]').click()
  await vi.waitFor(() => expect(host.querySelectorAll('dialog[open] [role="option"]')).toHaveLength(2))
  host.querySelector<HTMLButtonElement>('dialog[open] [role="option"]')!.click()
  expect(changes).toEqual([{ operand: { kind: 'literal', type: 'entity_id:light', value: 'light.hall' } }])
})

it('edits a service target as chips, keeping a single-entity string and creating the object form', async () => {
  const target = serviceInputs('light.turn_on').target
  expect(target.types).toEqual(['target', 'entity_id:light'])
  const { host, changes, setValue } = mount({ input: target, label: 'Target' }, { kind: 'literal', type: 'entity_id:light', value: 'light.hall' })
  await vi.waitFor(() => expect(host.querySelector('select[aria-label="Add device, area, floor or label to Target"]')).not.toBeNull())
  expect([...host.querySelectorAll('.hs-chip')].map(c => c.textContent)).toEqual(['Entity light.hall×'])
  q<HTMLButtonElement>(host, 'button[aria-label="Target entities"]').click()
  await vi.waitFor(() => expect(host.querySelectorAll('dialog[open] [role="option"]')).toHaveLength(2))
  const options = () => [...host.querySelectorAll<HTMLButtonElement>('dialog[open] [role="option"]')]
  options()[0].click() // deselect light.hall
  expect(changes.at(-1)!.operand).toEqual({ kind: 'unset' })
  options()[1].click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'target', value: { entity_id: ['light.porch'] } })

  setValue({ kind: 'literal', type: 'entity_id:light', value: 'light.hall' })
  const add = q<HTMLSelectElement>(host, 'select[aria-label="Add device, area, floor or label to Target"]')
  add.value = JSON.stringify(['area_id', 'hall']); add.dispatchEvent(new Event('change', { bubbles: true }))
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'target', value: { entity_id: ['light.hall'], area_id: ['hall'] } })
  q<HTMLButtonElement>(host, 'button[aria-label="Remove area Hall"]').click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'target', value: { entity_id: ['light.hall'] } })
})

it('reports toggles and selects immediately and disables every control when disabled', () => {
  const { host, changes } = mount({ input: field('hass_sharp_test.sectioned', 'speed'), label: 'Speed' })
  const select = q<HTMLSelectElement>(host, 'select[aria-label="Speed"]')
  expect([...select.options].map(o => o.textContent)).toEqual(['Choose…', 'Slow', 'fast'])
  select.value = '1'; select.dispatchEvent(new Event('change', { bubbles: true }))
  expect(changes).toEqual([{ operand: { kind: 'literal', type: 'string', value: 'fast' } }])
  dispose!()
  document.body.innerHTML = ''
  const disabled = mount({ input: field('light.turn_on', 'white'), label: 'White', disabled: true })
  expect(q<HTMLInputElement>(disabled.host, 'input[role="switch"]').disabled).toBe(true)
  expect(q<HTMLButtonElement>(disabled.host, 'button[aria-label="White mode"]').disabled).toBe(true)
})

it('includes or omits a constant field', () => {
  const { host, changes } = mount({ input: field('light.turn_on', 'white'), label: 'White' })
  const toggle = q<HTMLInputElement>(host, 'input[role="switch"]')
  expect(host.textContent).toContain('Enabled')
  toggle.click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'boolean', value: true })
  toggle.click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'unset' })
})

it('drags a colour under one coalesce key and stores [r, g, b]', () => {
  const { host, changes } = mount({ input: field('light.turn_on', 'rgb_color'), label: 'Colour' })
  const color = q<HTMLInputElement>(host, 'input[type="color"]')
  for (const [value, event] of [['#ff0000', 'input'], ['#00ff00', 'input'], ['#00ff00', 'change']]) { color.value = value; color.dispatchEvent(new Event(event, { bubbles: true })) }
  expect(changes.map(c => c.operand)).toEqual([{ kind: 'literal', type: 'array', value: [255, 0, 0] }, { kind: 'literal', type: 'array', value: [0, 255, 0] }])
  expect(new Set(changes.map(c => c.coalesce)).size).toBe(1)
})

it('edits a duration in parts and stores seconds', () => {
  const { host, changes } = mount({ input: spec(['number', 'object'], { selector: { duration: {} } }), label: 'Delay' }, { kind: 'literal', type: 'object', value: { minutes: 1, seconds: 30 } })
  expect(q<HTMLInputElement>(host, 'input[aria-label="Delay minutes"]').value).toBe('1')
  const hours = q<HTMLInputElement>(host, 'input[aria-label="Delay hours"]')
  type(hours, '1'); blur(hours)
  expect(changes).toEqual([{ operand: { kind: 'literal', type: 'number', value: 3690 } }])
})

it('shows a wire-only input without a value mode', () => {
  const { host } = mount({ input: spec(['global::HassSharp.EntityStateSnapshot']), label: 'State' })
  expect(host.textContent).toContain('Connect a')
  openMenu(host, 'State mode')
  expect([...host.querySelectorAll('[role="menuitem"]')].map(b => b.textContent)).toEqual(['Variable…', 'Clear'])
})
