import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import servicesFixture from '../../../../tests/fixtures/visual-services.json'
import { HassCtx } from '../src/context'
import { dataInputs, nodeCatalog, type GraphNode, type InputSpec, type Operand, type ServicesSection, type VisualCatalogSnapshot } from '../src/visual/contract'
import { InputEditor, type InputEditorProps } from '../src/visual/InputEditor'
import { valueSummary } from '../src/visual/widgets'

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
/** A controlled editor; `delay` applies each change later, as an asynchronous parent would. */
function mount(props: Partial<InputEditorProps> & { input: InputSpec }, initial?: Operand, delay?: number) {
  const host = document.createElement('div'); document.body.append(host)
  const [value, setValue] = createSignal<Operand | undefined>(initial)
  const changes: Change[] = []
  client = new QueryClient()
  dispose = render(() => <QueryClientProvider client={client!}><HassCtx.Provider value={{ callWS: async () => registry } as any}>
    <InputEditor variant="panel" label="Value" catalog={catalog} {...props} value={value()}
      onChange={(operand, options) => {
        changes.push({ operand, ...(options?.coalesce ? { coalesce: options.coalesce } : {}) })
        if (delay === undefined) setValue(operand); else setTimeout(() => setValue(operand), delay)
      }} />
  </HassCtx.Provider></QueryClientProvider>, host)
  return { host, changes, value, setValue }
}
const remount = (...args: Parameters<typeof mount>) => { dispose?.(); document.body.innerHTML = ''; return mount(...args) }
const q = <T extends Element = HTMLElement>(host: Element, selector: string) => { const found = host.querySelector<T>(selector); expect(found, selector).not.toBeNull(); return found! }
const type = (el: HTMLInputElement | HTMLTextAreaElement, text: string) => { el.value = text; el.dispatchEvent(new Event('input', { bubbles: true })) }
const key = (el: Element, k: string, init: KeyboardEventInit = {}) => el.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true, ...init }))
const blur = (el: Element) => el.dispatchEvent(new FocusEvent('blur'))
const menuItem = (host: Element, label: string) => [...host.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].find(b => b.textContent === label)!
const openMenu = (host: Element, label = 'Value mode') => q<HTMLButtonElement>(host, `button[aria-label="${label}"]`).click()
const described = (host: Element, el: Element) => (el.getAttribute('aria-describedby') ?? '').split(' ').filter(Boolean).map(id => host.querySelector(`[id="${id}"]`)!.textContent)
const chose = (select: HTMLSelectElement, value: string) => { select.value = value; select.dispatchEvent(new Event('change', { bubbles: true })) }

it('commits text once on blur, on Enter, and restores the committed text on Escape', () => {
  const { host, changes } = mount({ input: field('notify.notify', 'title'), label: 'Title' })
  const input = q<HTMLInputElement>(host, 'input[aria-label="Title"]')
  expect(input.placeholder).toBe('Not set')
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
  // Emptying an optional field leaves it not set.
  type(input, ''); blur(input)
  expect(changes.at(-1)!.operand).toEqual({ kind: 'unset' })
})

it('commits the empty text for a required field and shows it apart from not set', () => {
  const { host, changes } = mount({ input: field('notify.notify', 'message'), label: 'Message' }, { kind: 'literal', type: 'string', value: 'Hi' })
  const input = q<HTMLInputElement>(host, 'input[aria-label="Message"]')
  type(input, ''); blur(input)
  expect(changes).toEqual([{ operand: { kind: 'literal', type: 'string', value: '' } }])
  expect(input.placeholder).toBe('Empty text')
  openMenu(host, 'Message mode'); menuItem(host, 'Clear').click()
  expect(q<HTMLInputElement>(host, 'input[aria-label="Message"]').placeholder).toBe('Not set')
  const note = remount({ input: field('hass_sharp_test.sectioned', 'note'), label: 'Note' })
  expect(q<HTMLTextAreaElement>(note.host, 'textarea[aria-label="Note"]').placeholder).toBe('Not set')
})

it('reports a draft once when the parent applies it later, and ignores Enter while composing', async () => {
  const { host, changes } = mount({ input: field('notify.notify', 'title'), label: 'Title' }, undefined, 5)
  const input = q<HTMLInputElement>(host, 'input[aria-label="Title"]')
  type(input, 'こんにちは'); key(input, 'Enter', { isComposing: true })
  expect(changes).toEqual([])
  key(input, 'Enter'); blur(input)
  expect(changes).toHaveLength(1)
  await new Promise(resolve => setTimeout(resolve, 10))
  blur(input)
  expect(changes).toHaveLength(1)
  type(input, 'next'); blur(input)
  expect(changes).toHaveLength(2)
})

it('keeps an invalid number draft, explains it, and commits a number once', () => {
  const { host, changes } = mount({ input: field('hass_sharp_test.sectioned', 'level'), label: 'Level' })
  const input = q<HTMLInputElement>(host, 'input[aria-label="Level"]')
  expect(input.getAttribute('inputmode')).toBe('decimal')
  type(input, '4x'); blur(input)
  expect(changes).toEqual([])
  expect(input.value).toBe('4x')
  expect(input.getAttribute('aria-invalid')).toBe('true')
  expect(described(host, input).at(-1)).toBe('Enter a number.')
  type(input, '4'); key(input, 'Enter')
  expect(changes).toEqual([{ operand: { kind: 'literal', type: 'number', value: 4 } }])
  // A field that may be negative does not get the decimal keypad, which lacks a minus sign on some phones.
  const step = remount({ input: spec(['number'], { min: -10 }), label: 'Step' })
  expect(q(step.host, 'input[aria-label="Step"]').hasAttribute('inputmode')).toBe(false)
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

it('drags a colour under one coalesce key per drag and stores [r, g, b]', () => {
  const { host, changes } = mount({ input: field('light.turn_on', 'rgb_color'), label: 'Colour' })
  const color = q<HTMLInputElement>(host, 'input[type="color"]')
  const drag = (steps: [string, string][]) => { for (const [value, event] of steps) { color.value = value; color.dispatchEvent(new Event(event, { bubbles: true })) } }
  drag([['#ff0000', 'input'], ['#00ff00', 'input'], ['#00ff00', 'change']])
  expect(changes.map(c => c.operand)).toEqual([{ kind: 'literal', type: 'array', value: [255, 0, 0] }, { kind: 'literal', type: 'array', value: [0, 255, 0] }])
  expect(new Set(changes.map(c => c.coalesce)).size).toBe(1)
  drag([['#0000ff', 'input'], ['#0000ff', 'change']])
  expect(changes).toHaveLength(3)
  expect(changes[2].coalesce).toBeTruthy()
  expect(changes[2].coalesce).not.toBe(changes[0].coalesce)
})

it('switches between value and not set with a keyboard-operable mode menu', async () => {
  const { host, changes } = mount({ input: spec(['boolean']), label: 'Enabled' }, { kind: 'literal', type: 'boolean', value: true })
  const trigger = q<HTMLButtonElement>(host, 'button[aria-label="Enabled mode"]')
  expect(trigger.getAttribute('aria-haspopup')).toBe('menu')
  key(trigger, 'ArrowDown')
  expect(trigger.getAttribute('aria-expanded')).toBe('true')
  expect([...host.querySelectorAll('[role="menuitem"]')].map(b => b.textContent)).toEqual(['Value', 'Clear'])
  expect(document.activeElement).toBe(menuItem(host, 'Value'))
  key(host.querySelector('[role="menu"]')!, 'Escape')
  expect(host.querySelector('[role="menu"]')).toBeNull()

  openMenu(host, 'Enabled mode'); menuItem(host, 'Clear').click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'unset' })
  expect(q<HTMLInputElement>(host, 'input[role="switch"]').checked).toBe(false)
  expect(host.textContent).toContain('Not set')

  openMenu(host, 'Enabled mode'); menuItem(host, 'Value').click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'boolean', value: false })
  expect(changes).toHaveLength(2)
  await Promise.resolve()
  expect(document.activeElement).toBe(host.querySelector('input[role="switch"]'))
  q<HTMLInputElement>(host, 'input[role="switch"]').click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'boolean', value: true })
})

it('brings back the value kept under a connection when Value is chosen', () => {
  const { host, changes } = mount({ input: field('hass_sharp_test.sectioned', 'level'), label: 'Level', wiredFrom: 'Counter · Value' }, { kind: 'literal', type: 'number', value: 7 })
  openMenu(host, 'Level mode'); menuItem(host, 'Value').click()
  expect(changes).toEqual([{ operand: { kind: 'literal', type: 'number', value: 7 } }])
  // Without a kept value, the service field's default (3) is the start.
  const fresh = remount({ input: { ...field('hass_sharp_test.sectioned', 'level'), default: { kind: 'literal', type: 'number', value: 3 } }, label: 'Level', wiredFrom: 'Counter · Value' })
  openMenu(fresh.host, 'Level mode'); menuItem(fresh.host, 'Value').click()
  expect(fresh.changes).toEqual([{ operand: { kind: 'literal', type: 'number', value: 3 } }])
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

it('summarises a value too large for a node card in the inline variant, naming registry items', async () => {
  const { host } = mount({ input: serviceInputs('light.turn_on').target, label: 'Target', variant: 'inline' },
    { kind: 'literal', type: 'target', value: { entity_id: ['light.hall', 'light.porch'], area_id: ['hall'] } })
  // Entity ids stand in until the registry has loaded (an area is only counted); then the area reads by its name.
  expect(q(host, '.hs-input-editor__summary').textContent).toBe('light.hall, light.porch, 1 area')
  await vi.waitFor(() => expect(q(host, '.hs-input-editor__summary').textContent).toBe('light.hall, light.porch, Hall'))
  expect(q(host, '.hs-input-editor__summary').title).toBe('light.hall, light.porch, Hall')
  expect(host.querySelector('.hs-chip')).toBeNull()
  const many = remount({ input: serviceInputs('light.turn_on').target, label: 'Target', variant: 'inline' },
    { kind: 'literal', type: 'target', value: { entity_id: ['light.hall', 'light.porch', 'switch.fan'], area_id: ['hall'] } })
  expect(q(many.host, '.hs-input-editor__summary').textContent).toBe('3 entities, 1 area')
  const json = remount({ input: field('light.turn_on', 'hs_color'), label: 'HS colour', variant: 'inline' }, { kind: 'literal', type: 'array', value: [30, 60] })
  expect(q(json.host, '.hs-input-editor__summary').textContent).toBe('30, 60')
})

it('labels the panel control and associates validation messages with it once', () => {
  const { host } = mount({ input: spec(['string'], { description: 'Shown in the notification.' }), label: 'Title', errors: ['Input is required'] })
  const input = q<HTMLInputElement>(host, 'input[aria-label="Title"]')
  expect(q<HTMLLabelElement>(host, 'label').htmlFor).toBe(input.id)
  expect(input.getAttribute('aria-invalid')).toBe('true')
  expect(described(host, input)).toEqual(['Input is required', 'Shown in the notification.'])
  expect(q(host, '.hs-input-editor').hasAttribute('aria-describedby')).toBe(false)
  // Without a control (connected), the group carries the messages.
  const wired = remount({ input: spec(['string']), label: 'Title', errors: ['Input is required'], wiredFrom: 'A · B' })
  expect(described(wired.host, q(wired.host, '.hs-input-editor'))).toEqual(['Input is required'])
})

it('validates JSON on commit, keeps the text on a parse error and shows the shortcut', () => {
  const { host, changes } = mount({ input: spec(['object', 'array']), label: 'Data' })
  const area = q<HTMLTextAreaElement>(host, 'textarea[aria-label="Data"]')
  expect(area.placeholder).toBe('Not set')
  expect(area.getAttribute('aria-keyshortcuts')).toBe('Control+Enter Meta+Enter')
  expect(described(host, area)).toContain('Ctrl+Enter to apply')
  type(area, '[30, 60'); blur(area)
  expect(changes).toEqual([])
  expect(area.value).toBe('[30, 60')
  expect(host.querySelector('.hs-error')!.textContent).toMatch(/^Not valid JSON/)
  // Enter adds a line in the JSON editor; Ctrl+Enter commits.
  type(area, '[30, 60]'); key(area, 'Enter')
  expect(changes).toEqual([])
  key(area, 'Enter', { ctrlKey: true })
  expect(changes).toEqual([{ operand: { kind: 'literal', type: 'array', value: [30, 60] } }])
  expect(host.querySelector('.hs-error')).toBeNull()
})

it('types a value of several kinds by the kind switch', () => {
  const { host, changes } = mount({ input: spec(Object.keys(nodeCatalog.types)), label: 'Value' })
  const kind = q<HTMLSelectElement>(host, 'select[aria-label="Value kind"]')
  expect([...kind.options].map(o => o.textContent)).toEqual(['Text', 'Number', 'Yes/No', 'List or object'])
  expect(kind.value).toBe('text')
  const text = q<HTMLInputElement>(host, 'input[aria-label="Value"]')
  type(text, '01'); blur(text)
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'string', value: '01' })
  chose(kind, 'number')
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'number', value: 1 })
  expect(q<HTMLInputElement>(host, 'input[aria-label="Value"]').value).toBe('1')
  chose(q<HTMLSelectElement>(host, 'select[aria-label="Value kind"]'), 'boolean')
  // 1 is not a yes/no value: the switch starts from No, and that is what is stored.
  expect(changes).toHaveLength(3)
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'boolean', value: false })
  expect(q<HTMLInputElement>(host, 'input[role="switch"]').checked).toBe(false)
  q<HTMLInputElement>(host, 'input[role="switch"]').click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'boolean', value: true })
  chose(q<HTMLSelectElement>(host, 'select[aria-label="Value kind"]'), 'json')
  // Nothing converts a yes/no value to a list or object, so the value is cleared rather than kept out of sight.
  expect(changes.at(-1)!.operand).toEqual({ kind: 'unset' })
  const area = q<HTMLTextAreaElement>(host, 'textarea[aria-label="Value"]')
  expect(area.value).toBe('')
  type(area, '5'); blur(area)
  expect(q(host, '.hs-error').textContent).toBe('Enter a list [ … ] or an object { … }.')
  expect(changes.at(-1)!.operand).toEqual({ kind: 'unset' })
  type(area, '{"a": 1}'); blur(area)
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'object', value: { a: 1 } })
  chose(q<HTMLSelectElement>(host, 'select[aria-label="Value kind"]'), 'text')
  expect(changes.at(-1)!.operand).toEqual({ kind: 'unset' })
  expect(q<HTMLInputElement>(host, 'input[aria-label="Value"]').placeholder).toBe('Not set')
  // A stored value sets the switch.
  const stored = remount({ input: spec(['string', 'number']), label: 'Value' }, { kind: 'literal', type: 'number', value: 5 })
  expect(q<HTMLSelectElement>(stored.host, 'select[aria-label="Value kind"]').value).toBe('number')
})

it('keeps the range and unit of a number typed by the kind switch', () => {
  const { host, changes } = mount({ input: spec(['string', 'number'], { min: 10, max: 20, unit: '%' }), label: 'Level' },
    { kind: 'literal', type: 'string', value: 'high' })
  chose(q<HTMLSelectElement>(host, 'select[aria-label="Level kind"]'), 'number')
  // "high" is not a number: the number starts inside the range.
  expect(changes).toEqual([{ operand: { kind: 'literal', type: 'number', value: 10 } }])
  const input = q<HTMLInputElement>(host, 'input[aria-label="Level"]')
  expect(input.value).toBe('10')
  expect(input.getAttribute('inputmode')).toBe('decimal')
  expect(q(host, '.hs-unit').textContent).toBe('%')
})

it('shows a connected input read-only and lets the mode menu replace the connection', () => {
  const { host, changes } = mount({ input: field('notify.notify', 'message'), label: 'Message', wiredFrom: 'Read entity · State' })
  expect(q(host, '.hs-input-editor__state').textContent).toBe('Connected to Read entity · State')
  expect(host.querySelector('input')).toBeNull()
  openMenu(host, 'Message mode'); menuItem(host, 'Clear').click()
  expect(changes).toEqual([{ operand: { kind: 'unset' } }])
})

it('authors an entity value with the parametric type of a domain-filtered input', async () => {
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
  await vi.waitFor(() => expect(host.querySelector('input[aria-label="Add device, area, floor or label to Target"]')).not.toBeNull())
  expect([...host.querySelectorAll('.hs-chip')].map(c => c.textContent)).toEqual(['Entity light.hall×'])
  q<HTMLButtonElement>(host, 'button[aria-label="Target entities"]').click()
  await vi.waitFor(() => expect(host.querySelectorAll('dialog[open] [role="option"]')).toHaveLength(2))
  const options = () => [...host.querySelectorAll<HTMLButtonElement>('dialog[open] [role="option"]')]
  options()[0].click() // deselect light.hall
  expect(changes.at(-1)!.operand).toEqual({ kind: 'unset' })
  options()[1].click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'target', value: { entity_id: ['light.porch'] } })
  options()[0].click(); options()[1].click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'target', value: { entity_id: ['light.hall'] } })

  setValue({ kind: 'literal', type: 'entity_id:light', value: 'light.hall' })
  const add = q<HTMLInputElement>(host, 'input[aria-label="Add device, area, floor or label to Target"]')
  type(add, 'hall')
  expect([...host.querySelectorAll('.hs-search-group')].map(g => g.textContent)).toEqual(['Areas'])
  key(add, 'Enter')
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'target', value: { entity_id: ['light.hall'], area_id: ['hall'] } })
  q<HTMLButtonElement>(host, 'button[aria-label="Remove area Hall"]').click()
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'target', value: { entity_id: ['light.hall'] } })
})

it('converts a stored string to the object form when the input accepts only a target', async () => {
  const { host, changes } = mount({ input: spec(['target']), label: 'Target' }, { kind: 'literal', type: 'target', value: 'light.hall' })
  await vi.waitFor(() => expect(host.querySelector('input[aria-label="Add device, area, floor or label to Target"]')).not.toBeNull())
  const add = q<HTMLInputElement>(host, 'input[aria-label="Add device, area, floor or label to Target"]')
  type(add, 'night'); key(add, 'Enter')
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'target', value: { entity_id: ['light.hall'], label_id: ['night'] } })
})

it('moves focus to the next chip, then the previous one, then the add control after a removal', async () => {
  const { host } = mount({ input: spec(['array'], { selector: { text: { multiple: true } } }), label: 'Tags' }, { kind: 'literal', type: 'array', value: ['a', 'b', 'c'] })
  const remove = (label: string) => q<HTMLButtonElement>(host, `button[aria-label="Remove ${label}"]`).click()
  remove('a'); await Promise.resolve()
  expect(document.activeElement?.getAttribute('aria-label')).toBe('Remove b')
  remove('c'); await Promise.resolve()
  expect(document.activeElement?.getAttribute('aria-label')).toBe('Remove b')
  remove('b'); await Promise.resolve()
  expect(document.activeElement).toBe(host.querySelector('input[aria-label="Add to Tags"]'))
})

it('moves chip focus once a parent that applies changes later has removed the chip', async () => {
  const { host, value } = mount({ input: spec(['array'], { selector: { text: { multiple: true } } }), label: 'Tags' },
    { kind: 'literal', type: 'array', value: ['a', 'b', 'c'] }, 5)
  const remove = (label: string) => q<HTMLButtonElement>(host, `button[aria-label="Remove ${label}"]`).click()
  remove('a')
  await Promise.resolve()
  // The chip is still there until the parent applies the change.
  expect(host.querySelector('button[aria-label="Remove a"]')).not.toBeNull()
  await vi.waitFor(() => expect(document.activeElement?.getAttribute('aria-label')).toBe('Remove b'))
  expect(value()).toEqual({ kind: 'literal', type: 'array', value: ['b', 'c'] })
  remove('c')
  await vi.waitFor(() => expect(host.querySelector('button[aria-label="Remove c"]')).toBeNull())
  expect(document.activeElement?.getAttribute('aria-label')).toBe('Remove b')
  remove('b')
  await vi.waitFor(() => expect(document.activeElement).toBe(host.querySelector('input[aria-label="Add to Tags"]')))
})

it('shows the apply shortcut as this platform writes it', () => {
  const platform = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(navigator), 'platform')
  try {
    Object.defineProperty(navigator, 'platform', { value: 'MacIntel', configurable: true })
    const { host } = mount({ input: spec(['object', 'array']), label: 'Data' })
    expect(q(host, '.hs-field-hint').textContent).toBe('⌘+Enter to apply')
  } finally { delete (navigator as { platform?: string }).platform }
  expect(Object.getOwnPropertyDescriptor(Object.getPrototypeOf(navigator), 'platform')).toEqual(platform)
  const other = remount({ input: spec(['object', 'array']), label: 'Data' })
  expect(q(other.host, '.hs-field-hint').textContent).toBe('Ctrl+Enter to apply')
})

it('keeps the stored duration shape and writes new values in the object form', () => {
  const input = spec(['number', 'object'], { selector: { duration: {} } })
  const object = mount({ input, label: 'Delay' }, { kind: 'literal', type: 'object', value: { minutes: 1, seconds: 30 } })
  expect(q<HTMLInputElement>(object.host, 'input[aria-label="Delay minutes"]').value).toBe('1')
  const hours = q<HTMLInputElement>(object.host, 'input[aria-label="Delay hours"]')
  type(hours, '1'); blur(hours)
  expect(object.changes).toEqual([{ operand: { kind: 'literal', type: 'object', value: { hours: 1, minutes: 1, seconds: 30 } } }])
  type(hours, '-1'); blur(hours)
  expect(described(object.host, hours).at(-1)).toBe('Enter 0 or more.')

  const number = remount({ input, label: 'Delay' }, { kind: 'literal', type: 'number', value: 90 })
  const minutes = q<HTMLInputElement>(number.host, 'input[aria-label="Delay minutes"]')
  type(minutes, '2'); blur(minutes)
  expect(number.changes).toEqual([{ operand: { kind: 'literal', type: 'number', value: 150 } }])

  const fresh = remount({ input, label: 'Delay', errors: ['Bad delay'] })
  const seconds = q<HTMLInputElement>(fresh.host, 'input[aria-label="Delay seconds"]')
  // Validation messages reach every box, not only the group.
  expect(described(fresh.host, seconds)).toEqual(['Bad delay'])
  type(seconds, '0.3'); blur(seconds)
  expect(fresh.changes).toEqual([{ operand: { kind: 'literal', type: 'object', value: { hours: 0, minutes: 0, seconds: 0.3 } } }])
  expect(q<HTMLInputElement>(fresh.host, 'input[aria-label="Delay seconds"]').value).toBe('0.3')

  const onlySeconds = remount({ input: spec(['number'], { selector: { duration: {} } }), label: 'Delay' })
  const m = q<HTMLInputElement>(onlySeconds.host, 'input[aria-label="Delay minutes"]')
  type(m, '1'); blur(m)
  expect(onlySeconds.changes).toEqual([{ operand: { kind: 'literal', type: 'number', value: 60 } }])
})

it('keeps a stored date-time offset and separator, and writes new values without one', () => {
  const input = spec(['string'], { selector: { datetime: {} } })
  const utc = mount({ input, label: 'At' }, { kind: 'literal', type: 'string', value: '2026-11-13T10:00:00Z' })
  const field = q<HTMLInputElement>(utc.host, 'input[aria-label="At"]')
  // Browsers may shorten ":00" seconds in the picker's value.
  expect(field.value).toMatch(/^2026-11-13T10:00(:00)?$/)
  expect(utc.host.querySelector('.hs-unit')!.textContent).toBe('UTC')
  type(field, '2026-11-13T11:30:15'); blur(field)
  expect(utc.changes).toEqual([{ operand: { kind: 'literal', type: 'string', value: '2026-11-13T11:30:15Z' } }])

  const spaced = remount({ input, label: 'At' }, { kind: 'literal', type: 'string', value: '2026-11-13 10:00:00+02:00' })
  const second = q<HTMLInputElement>(spaced.host, 'input[aria-label="At"]')
  type(second, '2026-11-14T10:00'); blur(second)
  expect(spaced.changes).toEqual([{ operand: { kind: 'literal', type: 'string', value: '2026-11-14 10:00+02:00' } }])

  const fresh = remount({ input, label: 'At' })
  const third = q<HTMLInputElement>(fresh.host, 'input[aria-label="At"]')
  expect(third.placeholder).toBe('Not set')
  type(third, '2026-11-13T09:15'); blur(third)
  expect(fresh.changes).toEqual([{ operand: { kind: 'literal', type: 'string', value: '2026-11-13 09:15' } }])
  type(q<HTMLInputElement>(fresh.host, 'input[aria-label="At"]'), ''); blur(q(fresh.host, 'input[aria-label="At"]'))
  expect(fresh.changes.at(-1)!.operand).toEqual({ kind: 'unset' })
})

it('reports toggles and selects immediately', () => {
  const { host, changes } = mount({ input: field('hass_sharp_test.sectioned', 'speed'), label: 'Speed' })
  const select = q<HTMLSelectElement>(host, 'select[aria-label="Speed"]')
  expect([...select.options].map(o => o.textContent)).toEqual(['Choose…', 'Slow', 'Fast'])
  chose(select, '1')
  expect(changes).toEqual([{ operand: { kind: 'literal', type: 'string', value: 'fast' } }])
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

const everyKind: [string, InputSpec, Operand?][] = [
  ['toggle', spec(['boolean'])], ['slider', field('light.turn_on', 'brightness_pct')], ['number', spec(['number'])], ['text', spec(['string'])],
  ['multiline', field('hass_sharp_test.sectioned', 'note')], ['text list', spec(['array'], { selector: { text: { multiple: true } } }), { kind: 'literal', type: 'array', value: ['a'] }],
  ['select', field('hass_sharp_test.sectioned', 'speed')], ['select list', spec(['array'], { selector: { select: { multiple: true, options: ['a', 'b'] } } }), { kind: 'literal', type: 'array', value: ['a'] }],
  ['constant', field('light.turn_on', 'white')], ['entity', spec(['entity_id'])], ['entity list', spec(['entity_id', 'array'], { selector: { entity: { multiple: true } } })],
  ['state', field('climate.set_hvac_mode', 'hvac_mode')], ['area', spec(['string'], { selector: { area: {} } })], ['label list', spec(['string', 'array'], { selector: { label: { multiple: true } } }), { kind: 'literal', type: 'array', value: ['night'] }],
  ['target', serviceInputs('light.turn_on').target, { kind: 'literal', type: 'target', value: { entity_id: ['light.hall'] } }],
  ['duration', spec(['number', 'object'], { selector: { duration: { enable_day: true } } })], ['time', spec(['string'], { selector: { time: {} } })],
  ['date', spec(['string'], { selector: { date: {} } })], ['datetime', spec(['string'], { selector: { datetime: {} } })], ['colour', field('light.turn_on', 'rgb_color')],
  ['mixed', spec(['string', 'number'])], ['json', spec(['object'])],
]
it.each(everyKind)('disables every control of the %s widget', async (_, input, initial) => {
  const { host } = mount({ input, label: 'Field', disabled: true }, initial)
  await vi.waitFor(() => expect(host.querySelector('.hs-input-editor__body input, .hs-input-editor__body select, .hs-input-editor__body textarea, .hs-input-editor__body button')).not.toBeNull())
  // A picker's closed dialog cannot be reached; only the controls on the page count.
  const controls = [...host.querySelectorAll<HTMLInputElement>('input, select, textarea, button')].filter(c => !c.closest('dialog'))
  expect(controls.filter(c => !c.disabled).map(c => c.outerHTML.slice(0, 80))).toEqual([])
})

const banned = /operand|literal|unset|payload/i
it.each(everyKind)('shows no implementation words in the %s widget', async (_, input, initial) => {
  const { host } = mount({ input, label: 'Field' }, initial)
  await Promise.resolve()
  openMenu(host, 'Field mode')
  const attributes = [...host.querySelectorAll('*')].flatMap(el => ['aria-label', 'placeholder', 'title', 'aria-valuetext'].map(a => el.getAttribute(a) ?? ''))
  expect([host.textContent, ...attributes].filter(text => banned.test(text!))).toEqual([])
})

it('shows an input that can only be connected without a value mode', () => {
  const { host } = mount({ input: spec(['global::HassSharp.EntityStateSnapshot']), label: 'State' })
  expect(q(host, '.hs-input-editor__state').textContent).toBe('Connect an output of type Entity state.')
  openMenu(host, 'State mode')
  expect([...host.querySelectorAll('[role="menuitem"]')].map(b => b.textContent)).toEqual(['Clear'])
})

it('edits and summarises a value stored as null as not set, never as the text null', () => {
  // A target on a card is summarised: "Not set", muted.
  const target = mount({ input: { types: ['target', 'entity_id'], required: false, description: '' }, variant: 'inline', label: 'Target' }, { kind: 'literal', type: 'target', value: null } as any)
  const summary = target.host.querySelector('.hs-input-editor__summary')!
  expect(summary.textContent).toBe('Not set')
  expect(summary.classList).toContain('hs-input-editor__summary--empty')
  expect(target.host.textContent).not.toContain('null')
  // In the panel a text value is an empty box, not a JSON editor holding "null".
  const text = remount({ input: { types: ['string'], required: false, description: '' }, label: 'Note' }, { kind: 'literal', type: 'string', value: null } as any)
  expect(text.host.querySelector('textarea.hs-code-input')).toBeNull()
  expect(q<HTMLInputElement>(text.host, 'input[aria-label="Note"]').value).toBe('')
})

it('keeps a literal null in a JSON value visible as null: it is a value there, not an empty input', () => {
  const panel = mount({ input: { types: ['object'], required: false, description: '' }, label: 'Data' }, { kind: 'literal', type: 'object', value: null } as any)
  expect(q<HTMLTextAreaElement>(panel.host, 'textarea').value).toBe('null')
  const inline = remount({ input: { types: ['object'], required: false, description: '' }, variant: 'inline', label: 'Data' }, { kind: 'literal', type: 'object', value: null } as any)
  const summary = inline.host.querySelector('.hs-input-editor__summary')
  expect(summary?.textContent ?? q<HTMLTextAreaElement>(inline.host, 'textarea').value).toBe('null')
  expect(summary?.classList.contains('hs-input-editor__summary--empty') ?? false).toBe(false)
  expect(valueSummary({ kind: 'json', multiple: false }, null)).toBe('null')
  expect(valueSummary({ kind: 'text', multiple: false }, null)).toBe('Not set')
})

it('reads a decimal comma as the decimal point when the number has no point, and refuses more than one separator', () => {
  const { host, changes } = mount({ input: field('hass_sharp_test.sectioned', 'level'), label: 'Level' })
  const input = q<HTMLInputElement>(host, 'input[aria-label="Level"]')
  type(input, '21,5'); blur(input)
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'number', value: 21.5 })
  type(input, '1.5'); blur(input)
  expect(changes.at(-1)!.operand).toEqual({ kind: 'literal', type: 'number', value: 1.5 })
  const count = changes.length
  for (const text of ['1,2,3', '1.2,3']) {
    type(input, text); blur(input)
    expect(changes, text).toHaveLength(count)
    expect(described(host, input).at(-1)).toBe('Enter a number.')
  }
  // A field that may be negative takes a comma too.
  const step = remount({ input: spec(['number'], { min: -10 }), label: 'Step' })
  const box = q<HTMLInputElement>(step.host, 'input[aria-label="Step"]')
  type(box, '-1,5'); blur(box)
  expect(step.changes).toEqual([{ operand: { kind: 'literal', type: 'number', value: -1.5 } }])
  // So does a part of a duration.
  const delay = remount({ input: spec(['number', 'object'], { selector: { duration: {} } }), label: 'Delay' })
  const seconds = q<HTMLInputElement>(delay.host, 'input[aria-label="Delay seconds"]')
  type(seconds, '2,5'); blur(seconds)
  expect(delay.changes).toEqual([{ operand: { kind: 'literal', type: 'object', value: { hours: 0, minutes: 0, seconds: 2.5 } } }])
})
