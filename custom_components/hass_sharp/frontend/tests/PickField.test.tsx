import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { DevicePanel } from '../src/entities/DevicePanel'
import type { DeviceActResult, DeviceCard, DeviceField, DeviceOption, DeviceOptionSet, DevicePageData } from '../src/entities/deviceUi'

// A card's pick field on the Device tab, against an in-memory `hass_sharp/device_ui` backend: the button on the card,
// its dialog (search, keyboard, the form of a new one), the pictures it asks for, what survives a refresh, and what a
// press sends.

let dispose: (() => void) | undefined
let client: QueryClient | undefined
beforeEach(() => { vi.useFakeTimers() })
afterEach(() => {
  dispose?.(); dispose = undefined
  client?.clear(); client = undefined
  document.body.innerHTML = ''
  vi.useRealTimers()
})

const person = (id: string, title: string, detail: string | null = null, image: string | null = null): DeviceOption => ({ id, image, title, detail })
const people = (): DeviceOptionSet => ({
  id: 'people',
  items: [person('p1', 'Ivan Horvat', '41 samples', 'h1'), person('p2', 'Ana Horvat', '12 samples', 'h2'), person('p3', 'Marko Kovač', 'Seen last WEEK')],
})
/** The new person's form: a required first name, a last name and an age from 0 to 120. */
const newPerson = { title: 'New person', fields: [
  { key: 'first_name', label: 'First name', type: 'text', value: null, required: true },
  { key: 'last_name', label: 'Last name', type: 'text', value: null, required: false },
  { key: 'age', label: 'Age', type: 'number', value: null, minimum: 0, maximum: 120, required: false },
] } satisfies DeviceField['add']
const pick = (extra: Partial<DeviceField> = {}): DeviceField => ({ key: 'person', label: 'Person', type: 'pick', value: null, set: 'people', required: false, ...extra })
const faceCard = (id: string, field: DeviceField = pick(), extra: Partial<DeviceCard> = {}): DeviceCard => ({
  id, image: null, title: `Face ${id}`, detail: null, fields: [field],
  actions: [{ id: 'save', label: 'Save', style: 'primary', confirm: null, validate: true }, { id: 'dismiss', label: 'Dismiss', style: 'default', confirm: null, validate: false }],
  ...extra,
})
const doorPage = (cards: DeviceCard[] = [faceCard('f1')], options: DeviceOptionSet[] = [people()]): DevicePageData => ({
  nodeKey: 'faces', title: 'Front door people', attention: cards.length,
  lists: [{ id: 'unknown', title: 'Unknown faces', attention: true, empty: 'Nobody new.', cards }],
  options,
})

/** The in-memory device page API: answers as `DeviceUiRequest` does, and keeps every request. */
function backend() {
  const state = {
    revision: 'r1',
    pages: [doorPage()] as DevicePageData[],
    images: {} as Record<string, string>,
    act: (_request: any): DeviceActResult => ({ status: 'done', message: null }),
  }
  const requests: any[] = []
  const ok = (result: unknown) => ({ version: 1, ok: true, result })
  const callWS = async (message: any) => {
    const request = message.request
    requests.push(structuredClone(request))
    if (request.operation === 'read') {
      if (request.knownRevision === state.revision) return ok({ running: true, revision: state.revision, unchanged: true })
      return ok({ running: true, revision: state.revision, pages: structuredClone(state.pages) })
    }
    if (request.operation === 'image') {
      const jpeg = state.images[request.hash]
      return jpeg ? ok({ jpeg }) : { version: 1, ok: false, error: { code: 'not_found', detail: 'The device’s page shows no such image' } }
    }
    if (request.operation === 'act') return ok(state.act(request))
    if (request.operation === 'counts') return ok({ attention: {}, stored: {} })
    throw new Error(`Unexpected request ${JSON.stringify(message)}`)
  }
  const draw = (pages: DevicePageData[]) => { state.pages = pages; state.revision = `r${Number(state.revision.slice(1)) + 1}` }
  return { state, requests, callWS, draw }
}
type Backend = ReturnType<typeof backend>

function mount(device: Backend) {
  const host = document.createElement('div')
  host.className = 'hs-app'
  document.body.append(host)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  dispose = render(() => (
    <QueryClientProvider client={client!}><HassCtx.Provider value={{ callWS: device.callWS } as any}>
      <DevicePanel definitionId="door" visible />
    </HassCtx.Provider></QueryClientProvider>
  ), host)
  return host
}

const settle = () => vi.advanceTimersByTimeAsync(0)
const poll = () => vi.advanceTimersByTimeAsync(2000)
const card = (host: Element, id: string) => host.querySelector<HTMLElement>(`[data-card-id="${id}"]`)!
const field = (within: Element, key = 'person') => within.querySelector<HTMLElement>(`[data-field="${key}"]`)!
const pickButton = (within: Element, key = 'person') => field(within, key).querySelector<HTMLButtonElement>('button')!
const button = (within: Element, name: string) => {
  const found = [...within.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === name)
  expect(found, name).toBeDefined()
  return found!
}
const dialog = () => document.querySelector<HTMLDialogElement>('dialog[open]')
const search = () => dialog()!.querySelector<HTMLInputElement>('input[role="combobox"]')!
const listbox = () => dialog()!.querySelector<HTMLElement>('[role="listbox"]')!
const rows = () => [...listbox().querySelectorAll<HTMLElement>('[role="option"]')]
const titles = () => rows().map(row => row.querySelector('strong')?.textContent ?? row.textContent!.trim())
const type = (input: HTMLInputElement, value: string) => { input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })) }
const key = (element: Element, value: string) => element.dispatchEvent(new KeyboardEvent('keydown', { key: value, bubbles: true, cancelable: true }))
const blur = (element: HTMLElement) => element.dispatchEvent(new FocusEvent('blur'))
const acts = (device: Backend) => device.requests.filter(r => r.operation === 'act')
const imageRequests = (device: Backend) => device.requests.filter(r => r.operation === 'image').map(r => r.hash)
/** Opens the dialog of the card's pick field, as a click does, and lets its search take the focus. */
const open = async (within: Element) => { pickButton(within).click(); await settle() }

it('draws the field as a button under its label: the chosen option, or Choose…', async () => {
  const device = backend()
  device.state.images = { h1: 'AAAA' }
  device.state.pages = [doorPage([faceCard('f1', pick({ value: 'p1' })), faceCard('f2', pick({ required: true }))])]
  const host = mount(device)
  await settle()
  await settle()
  const chosen = pickButton(card(host, 'f1'))
  expect(chosen.type).toBe('button')
  expect(chosen.getAttribute('aria-haspopup')).toBe('dialog')
  // Its name is the label and then what it shows.
  const [labelId, ownId] = chosen.getAttribute('aria-labelledby')!.split(' ')
  expect(document.getElementById(labelId)!.textContent).toBe('Person')
  expect(ownId).toBe(chosen.id)
  expect(chosen.textContent).toContain('Ivan Horvat')
  expect(chosen.textContent).toContain('41 samples')
  const picture = chosen.querySelector('img')!
  expect(picture.getAttribute('src')).toBe('data:image/jpeg;base64,AAAA')
  expect(picture.getAttribute('alt')).toBe('')
  // Nothing chosen; a required one marked as the card's other fields are.
  const f2 = card(host, 'f2')
  expect(pickButton(f2).textContent).toContain('Choose…')
  const hint = field(f2).querySelector('.hs-field-hint')!
  expect(hint.textContent).toBe('Required.')
  expect(pickButton(f2).getAttribute('aria-describedby')?.split(' ')).toContain(hint.id)
  expect(field(card(host, 'f1')).querySelector('.hs-field-hint')).toBeNull()
})

it('reads a page without options, as an older backend draws it, as none', async () => {
  const device = backend()
  const page = doorPage([faceCard('f1', pick({ value: 'p1' }))])
  delete (page as Partial<DevicePageData>).options
  device.state.pages = [page]
  const host = mount(device)
  await settle()
  expect(pickButton(card(host, 'f1')).textContent).toContain('Choose…')
  await open(card(host, 'f1'))
  expect(rows()).toEqual([])
  expect(dialog()!.textContent).toContain('No match')
})

it('stops a validating press with nothing chosen, marks the field and moves the focus to it', async () => {
  const device = backend()
  device.state.pages = [doorPage([faceCard('f1', pick({ required: true }))])]
  const host = mount(device)
  await settle()
  const f1 = card(host, 'f1')
  button(f1, 'Save').click()
  await settle()
  expect(acts(device)).toEqual([])
  expect(field(f1).getAttribute('aria-invalid')).toBe('true')
  expect(field(f1).textContent).toContain('Person is required.')
  expect(document.activeElement).toBe(pickButton(f1))
  // Chosen, the mark goes and the press is sent.
  await open(f1)
  rows()[1].click()
  await settle()
  expect(field(f1).getAttribute('aria-invalid')).toBeNull()
  button(f1, 'Save').click()
  await settle()
  expect(acts(device).map(r => r.values)).toEqual([{ person: { picked: 'p2' } }])
})

it('opens a dialog titled with the label, its search focused, and filters by title and detail without regard to case', async () => {
  const device = backend()
  device.state.pages = [doorPage([faceCard('f1', pick({ value: 'p1', except: ['p2'] }))])]
  const host = mount(device)
  await settle()
  await open(card(host, 'f1'))
  const shown = dialog()!
  expect(document.getElementById(shown.getAttribute('aria-labelledby')!)!.textContent).toBe('Person')
  expect(document.activeElement).toBe(search())
  // The option of `except` is not offered.
  expect(titles()).toEqual(['Ivan Horvat', 'Marko Kovač'])
  type(search(), 'HORV')
  expect(titles()).toEqual(['Ivan Horvat'])
  type(search(), 'last week')
  expect(titles()).toEqual(['Marko Kovač'])
  type(search(), 'nobody')
  expect(rows()).toEqual([])
  expect(shown.textContent).toContain('No match')
})

it('renders at most 50 rows, with a line for the rest', async () => {
  const device = backend()
  const many: DeviceOptionSet = { id: 'people', items: Array.from({ length: 120 }, (_, i) => person(`p${i}`, `Person ${i}`)) }
  device.state.pages = [doorPage([faceCard('f1')], [many])]
  const host = mount(device)
  await settle()
  await open(card(host, 'f1'))
  expect(rows()).toHaveLength(50)
  expect(dialog()!.textContent).toContain('70 more: type to narrow')
  type(search(), 'person 1')
  // Person 1, 10–19 and 100–119.
  expect(rows()).toHaveLength(31)
  expect(dialog()!.textContent).not.toContain('more: type to narrow')
})

it('chooses with the arrow keys and Enter, or a click, and gives the focus back to the field', async () => {
  const device = backend()
  device.state.pages = [doorPage([faceCard('f1', pick({ value: 'p1' }))])]
  const host = mount(device)
  await settle()
  const f1 = card(host, 'f1')
  await open(f1)
  const input = search()
  // The chosen option is active, and marked.
  const active = () => document.getElementById(input.getAttribute('aria-activedescendant')!)
  expect(active()).toBe(rows()[0])
  expect(rows()[0].textContent).toContain('Chosen')
  expect(rows()[1].textContent).not.toContain('Chosen')
  key(input, 'ArrowDown')
  expect(active()).toBe(rows()[1])
  expect(rows()[1].getAttribute('aria-selected')).toBe('true')
  expect(rows()[0].getAttribute('aria-selected')).toBe('false')
  key(input, 'ArrowDown')
  key(input, 'ArrowDown')
  // Past the last, back to the first.
  expect(active()).toBe(rows()[0])
  key(input, 'ArrowUp')
  expect(active()).toBe(rows()[2])
  key(input, 'Enter')
  await settle()
  expect(dialog()).toBeNull()
  expect(pickButton(f1).textContent).toContain('Marko Kovač')
  expect(document.activeElement).toBe(pickButton(f1))
  // A click chooses too.
  await open(f1)
  expect(active()?.textContent).toContain('Marko Kovač')
  rows()[1].click()
  await settle()
  expect(dialog()).toBeNull()
  expect(pickButton(f1).textContent).toContain('Ana Horvat')
  expect(document.activeElement).toBe(pickButton(f1))
})

it('closes on Escape or Cancel with no change, and closes nothing behind it', async () => {
  const device = backend()
  device.state.pages = [doorPage([faceCard('f1', pick({ value: 'p1' }))])]
  const host = mount(device)
  await settle()
  const behind = vi.fn()
  host.addEventListener('keydown', behind)
  document.addEventListener('keydown', behind)
  const f1 = card(host, 'f1')
  await open(f1)
  key(search(), 'ArrowDown')
  key(search(), 'Escape')
  await settle()
  expect(dialog()).toBeNull()
  expect(behind).not.toHaveBeenCalled()
  expect(pickButton(f1).textContent).toContain('Ivan Horvat')
  expect(document.activeElement).toBe(pickButton(f1))
  await open(f1)
  type(search(), 'ana')
  button(dialog()!, 'Cancel').click()
  await settle()
  expect(dialog()).toBeNull()
  expect(pickButton(f1).textContent).toContain('Ivan Horvat')
  expect(document.activeElement).toBe(pickButton(f1))
  document.removeEventListener('keydown', behind)
  // Opened again: the search starts empty.
  await open(f1)
  expect(search().value).toBe('')
  expect(rows()).toHaveLength(3)
})

it('fills in the form of a new one: what was typed first, Use checks it, Back returns to the list', async () => {
  const device = backend()
  device.state.pages = [doorPage([faceCard('f1', pick({ required: true, add: newPerson }))])]
  const host = mount(device)
  await settle()
  const f1 = card(host, 'f1')
  await open(f1)
  type(search(), 'Mar')
  // The form's row is the last, and the arrow keys reach it.
  const add = rows().at(-1)!
  expect(add.textContent).toContain('+ New person')
  key(search(), 'ArrowUp')
  expect(search().getAttribute('aria-activedescendant')).toBe(add.id)
  key(search(), 'Enter')
  await settle()
  const shown = dialog()!
  const input = (label: string) => shown.querySelector<HTMLInputElement>(`input[aria-label="${label}"]`)!
  expect(shown.querySelector('[role="listbox"]')).toBeNull()
  expect(input('First name').value).toBe('Mar')
  expect(document.activeElement).toBe(input('First name'))
  // Back: the list again, with what was typed.
  button(shown, 'Back').click()
  await settle()
  expect(search().value).toBe('Mar')
  expect(document.activeElement).toBe(search())
  rows().at(-1)!.click()
  await settle()
  // Use checks the required fields and the number ranges as a card's.
  type(input('First name'), ' ')
  type(input('Age'), '130')
  blur(input('Age'))
  button(shown, 'Use').click()
  await settle()
  expect(dialog()).toBe(shown)
  expect(shown.querySelector('[data-field="first_name"]')!.getAttribute('aria-invalid')).toBe('true')
  expect(shown.textContent).toContain('First name is required.')
  expect(shown.textContent).toContain('Age must be at most 120.')
  expect(document.activeElement).toBe(input('First name'))
  type(input('First name'), 'Marko')
  type(input('Last name'), 'Novak')
  type(input('Age'), '30')
  blur(input('Age'))
  button(shown, 'Use').click()
  await settle()
  expect(dialog()).toBeNull()
  expect(pickButton(f1).textContent).toContain('New')
  expect(pickButton(f1).textContent).toContain('Marko Novak')
  expect(document.activeElement).toBe(pickButton(f1))
  button(f1, 'Save').click()
  await settle()
  expect(acts(device).map(r => r.values)).toEqual([{ person: { new: { first_name: 'Marko', last_name: 'Novak', age: 30 } } }])
})

it('sends the option picked, or null for none; an action that does not validate sends nothing; done clears the choice', async () => {
  const device = backend()
  device.state.pages = [doorPage([faceCard('f1', pick({ value: 'p1' })), faceCard('f2')])]
  const host = mount(device)
  await settle()
  const f1 = card(host, 'f1'), f2 = card(host, 'f2')
  // Nothing chosen: null, sent and not left out.
  button(f2, 'Save').click()
  await settle()
  expect(acts(device)[0].values).toEqual({ person: null })
  expect('person' in acts(device)[0].values).toBe(true)
  await open(f1)
  rows()[2].click()
  await settle()
  button(f1, 'Dismiss').click()
  await settle()
  expect(acts(device)[1].values).toEqual({})
  // Done: the choice is reset to what the device drew.
  expect(pickButton(f1).textContent).toContain('Ivan Horvat')
  await open(f1)
  rows()[2].click()
  await settle()
  button(f1, 'Save').click()
  await settle()
  expect(acts(device)[2].values).toEqual({ person: { picked: 'p3' } })
  expect(pickButton(f1).textContent).toContain('Ivan Horvat')
  // Not done: the choice stays.
  device.state.act = () => ({ status: 'refused', message: 'Not now.' })
  await open(f1)
  rows()[1].click()
  await settle()
  button(f1, 'Save').click()
  await settle()
  expect(acts(device)[3].values).toEqual({ person: { picked: 'p2' } })
  expect(pickButton(f1).textContent).toContain('Ana Horvat')
})

it('asks for the chosen options\' pictures and, while a dialog is open, the shown rows\', each once', async () => {
  const device = backend()
  const many: DeviceOptionSet = { id: 'people', items: Array.from({ length: 500 }, (_, i) => person(`p${i}`, `Person ${i}`, null, `h${i}`)) }
  device.state.images = Object.fromEntries(many.items.map(option => [option.image!, `IMG${option.id.slice(1)}`]))
  const cards = () => [faceCard('f1', pick({ value: 'p1' })), faceCard('f2', pick({ value: 'p1' })), faceCard('f3', pick({ value: 'p300' })), faceCard('f4')]
  device.state.pages = [doorPage(cards(), [many])]
  const host = mount(device)
  await settle()
  await settle()
  expect(imageRequests(device).sort()).toEqual(['h1', 'h300'])
  expect(pickButton(card(host, 'f2')).querySelector('img')!.getAttribute('src')).toBe('data:image/jpeg;base64,IMG1')
  // The rows a dialog shows: 50, the chosen ones' not again.
  await open(card(host, 'f4'))
  await settle()
  expect(imageRequests(device)).toHaveLength(2 + 49)
  expect(new Set(imageRequests(device)).size).toBe(51)
  expect(rows()[3].querySelector('img')!.getAttribute('src')).toBe('data:image/jpeg;base64,IMG3')
  expect([...dialog()!.querySelectorAll('img')].every(img => img.getAttribute('alt') === '')).toBe(true)
  // Narrowed and widened again: only the rows not shown before.
  type(search(), 'person 49')
  await settle()
  expect(imageRequests(device)).toHaveLength(51 + 10)
  type(search(), '')
  await settle()
  expect(imageRequests(device)).toHaveLength(61)
  // A refresh asks for nothing again.
  device.draw([doorPage(cards(), [many])])
  await poll()
  await settle()
  expect(imageRequests(device)).toHaveLength(61)
  // Chosen: its picture stays; the others the dialog showed are let go, and asked for again when shown again.
  type(search(), 'person 250')
  await settle()
  expect(imageRequests(device)).toHaveLength(62)
  rows()[0].click()
  await settle()
  expect(imageRequests(device)).toHaveLength(62)
  expect(pickButton(card(host, 'f4')).querySelector('img')!.getAttribute('src')).toBe('data:image/jpeg;base64,IMG250')
  await open(card(host, 'f4'))
  await settle()
  expect(imageRequests(device).filter(hash => hash === 'h7')).toHaveLength(2)
  expect(imageRequests(device).filter(hash => hash === 'h1' || hash === 'h300' || hash === 'h250')).toHaveLength(3)
})

it('keeps the choice, the open dialog, its typed text and its scroll through a refresh', async () => {
  const device = backend()
  device.state.pages = [doorPage([faceCard('f1', pick({ value: 'p1' }))])]
  const host = mount(device)
  await settle()
  const f1 = card(host, 'f1')
  await open(f1)
  rows()[1].click()
  await settle()
  device.draw([doorPage([faceCard('f1', pick({ value: 'p1' }))])])
  await poll()
  expect(pickButton(f1).textContent).toContain('Ana Horvat')
  await open(f1)
  const shown = dialog()!, input = search(), list = listbox(), row = rows()[0]
  type(input, 'horvat')
  list.scrollTop = 40
  key(input, 'ArrowDown')
  const activeId = input.getAttribute('aria-activedescendant')
  device.draw([doorPage([faceCard('f1', pick({ value: 'p1' }))])])
  await poll()
  expect(dialog()).toBe(shown)
  expect(search()).toBe(input)
  expect(input.value).toBe('horvat')
  expect(listbox()).toBe(list)
  expect(list.scrollTop).toBe(40)
  expect(rows()[0]).toBe(row)
  expect(input.getAttribute('aria-activedescendant')).toBe(activeId)
})

it('drops an option that goes from the open list, and a chosen one that goes from the field', async () => {
  const device = backend()
  device.state.pages = [doorPage([faceCard('f1', pick({ required: true }))])]
  const host = mount(device)
  await settle()
  const f1 = card(host, 'f1')
  await open(f1)
  rows()[2].click()
  await settle()
  expect(pickButton(f1).textContent).toContain('Marko Kovač')
  await open(f1)
  const without = (...gone: string[]): DeviceOptionSet => ({ id: 'people', items: people().items.filter(option => !gone.includes(option.id)) })
  device.draw([doorPage([faceCard('f1', pick({ required: true }))], [without('p2')])])
  await poll()
  expect(titles()).toEqual(['Ivan Horvat', 'Marko Kovač'])
  key(search(), 'Escape')
  await settle()
  // The chosen one goes: nothing chosen, and a press of the required field stops here.
  device.draw([doorPage([faceCard('f1', pick({ required: true }))], [without('p2', 'p3')])])
  await poll()
  expect(pickButton(f1).textContent).toContain('Choose…')
  button(f1, 'Save').click()
  await settle()
  expect(acts(device)).toEqual([])
  expect(field(f1).textContent).toContain('Person is required.')
})

it('gives the search, the list and its rows their roles', async () => {
  const device = backend()
  device.state.pages = [doorPage([faceCard('f1', pick({ add: newPerson }))])]
  const host = mount(device)
  await settle()
  await open(card(host, 'f1'))
  const input = search()
  expect(input.getAttribute('aria-autocomplete')).toBe('list')
  expect(input.getAttribute('aria-expanded')).toBe('true')
  expect(input.getAttribute('aria-controls')).toBe(listbox().id)
  expect(listbox().getAttribute('aria-label')).toBe('Person options')
  expect(rows().map(row => row.id).every(Boolean)).toBe(true)
  expect(rows()).toHaveLength(4)
})
