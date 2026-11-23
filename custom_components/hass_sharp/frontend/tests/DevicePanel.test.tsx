import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { HassCtx } from '../src/context'
import { DevicePanel } from '../src/entities/DevicePanel'
import type { DeviceActResult, DeviceCard, DeviceField, DevicePageData } from '../src/entities/deviceUi'

// The Device tab's panel against an in-memory `hass_sharp/device_ui` backend: what it draws, how it polls, what it
// keeps of what the person typed, and what a press sends.

let dispose: (() => void) | undefined
beforeEach(() => { vi.useFakeTimers() })
afterEach(() => {
  dispose?.(); dispose = undefined
  document.body.innerHTML = ''
  vi.useRealTimers()
  Reflect.deleteProperty(document, 'visibilityState')
})

const text = (key: string, label: string, value = '', extra: Partial<DeviceField> = {}): DeviceField => ({ key, label, type: 'text', value, required: false, ...extra })
const faceCard = (id: string, extra: Partial<DeviceCard> = {}): DeviceCard => ({
  id, image: null, title: `Face ${id}`, detail: 'Seen 14 times',
  fields: [text('name', 'Name'), text('note', 'Note', 'first seen at the door')],
  actions: [{ id: 'save', label: 'Save', style: 'primary', confirm: null }, { id: 'dismiss', label: 'Dismiss', style: 'default', confirm: null }],
  ...extra,
})
const doorPage = (cards: DeviceCard[] = [faceCard('f1'), faceCard('f2')]): DevicePageData => ({
  nodeKey: 'faces', title: 'Front door people', attention: cards.length,
  lists: [
    { id: 'unknown', title: 'Unknown faces', attention: true, empty: 'Nobody new.', cards },
    { id: 'people', title: 'People', attention: false, empty: 'Nobody is known yet.', cards: [] },
  ],
})

/** The in-memory device page API: answers as `DeviceUiRequest` does, and keeps every request. */
function backend() {
  const state = {
    running: true,
    revision: 'r1',
    pages: [doorPage()] as DevicePageData[],
    images: {} as Record<string, string>,
    act: (_request: any): DeviceActResult => ({ status: 'done', message: null }),
    /** An `act` waits for this before it answers. */
    hold: undefined as Promise<unknown> | undefined,
    /** A `read` fails with this as Home Assistant's refusal. */
    readError: undefined as unknown,
  }
  const requests: any[] = []
  const ok = (result: unknown) => ({ version: 1, ok: true, result })
  const callWS = async (message: any) => {
    expect(message.type).toBe('hass_sharp/device_ui')
    const request = message.request
    requests.push(structuredClone(request))
    if (request.operation === 'read') {
      if (state.readError) throw state.readError
      if (!state.running) return ok({ running: false })
      if (request.knownRevision === state.revision) return ok({ running: true, revision: state.revision, unchanged: true })
      return ok({ running: true, revision: state.revision, pages: structuredClone(state.pages) })
    }
    if (request.operation === 'image') {
      const jpeg = state.images[request.hash]
      return jpeg ? ok({ jpeg }) : { version: 1, ok: false, error: { code: 'not_found', detail: 'The device’s page shows no such image' } }
    }
    if (request.operation === 'act') {
      if (state.hold) await state.hold
      if (!state.running) return { version: 1, ok: false, error: { code: 'not_running', detail: 'The entity definition is not running' } }
      return ok(state.act(request))
    }
    throw new Error(`Unexpected request ${JSON.stringify(message)}`)
  }
  /** A new revision of the pages, as the device draws it. */
  const draw = (pages: DevicePageData[]) => { state.pages = pages; state.revision = `r${Number(state.revision.slice(1)) + 1}` }
  return { state, requests, callWS, draw, reads: () => requests.filter(r => r.operation === 'read') }
}
type Backend = ReturnType<typeof backend>

function mount(device: Backend = backend(), options: { definitionId?: string } = { definitionId: 'door' }) {
  const host = document.createElement('div')
  host.className = 'hs-app'
  document.body.append(host)
  const [definitionId, setDefinitionId] = createSignal(options.definitionId)
  const [visible, setVisible] = createSignal(true)
  dispose = render(() => (
    <HassCtx.Provider value={{ callWS: device.callWS } as any}>
      <DevicePanel definitionId={definitionId()} visible={visible()} />
    </HassCtx.Provider>
  ), host)
  return { host, device, setVisible, setDefinitionId }
}

/** Lets the answers in flight arrive. */
const settle = () => vi.advanceTimersByTimeAsync(0)
/** The next poll, two seconds on, and its answer. */
const poll = () => vi.advanceTimersByTimeAsync(2000)
const card = (host: Element, list: string, id: string) => host.querySelector<HTMLElement>(`[data-list-id="${list}"] [data-card-id="${id}"]`)
const input = (within: Element, label: string) => within.querySelector<HTMLInputElement>(`input[aria-label="${label}"]`)!
const button = (within: Element, name: string) => {
  const found = [...within.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === name)
  expect(found, name).toBeDefined()
  return found!
}
const type = (field: HTMLInputElement, value: string) => { field.value = value; field.dispatchEvent(new Event('input', { bubbles: true })) }
const blur = (field: HTMLElement) => field.dispatchEvent(new FocusEvent('blur'))
const acts = (device: Backend) => device.requests.filter(r => r.operation === 'act')

it('draws lists, cards, fields and actions', async () => {
  const device = backend()
  device.state.pages = [{
    ...doorPage([faceCard('f1', {
      fields: [
        text('name', 'Name', 'Ivan', { suggestions: ['Ivan', 'Ana'] }),
        { key: 'role', label: 'Role', type: 'select', value: 'family', options: ['guest', 'family'], required: false },
        { key: 'age', label: 'Age', type: 'number', value: 41, minimum: 0, maximum: 120, required: false },
        { key: 'notify', label: 'Notify', type: 'toggle', value: true, required: false },
      ],
      actions: [{ id: 'rename', label: 'Rename', style: 'default', confirm: null }, { id: 'delete', label: 'Delete', style: 'danger', confirm: 'Forget Ivan?' }],
    })]),
  }]
  const { host } = mount(device)
  await settle()
  expect(host.querySelector('h3')!.textContent).toBe('Front door people')
  expect([...host.querySelectorAll('h4')].map(h => h.textContent)).toEqual(['Unknown faces (1)', 'People (0)'])
  // A list without cards says its `empty` text.
  expect(host.querySelector('[data-list-id="people"]')!.textContent).toContain('Nobody is known yet.')
  const f1 = card(host, 'unknown', 'f1')!
  expect(f1.textContent).toContain('Face f1')
  expect(f1.textContent).toContain('Seen 14 times')
  expect(input(f1, 'Name').value).toBe('Ivan')
  expect([...f1.querySelectorAll('datalist option')].map(o => o.getAttribute('value'))).toEqual(['Ivan', 'Ana'])
  const role = f1.querySelector<HTMLSelectElement>('[data-field="role"] select')!
  expect(role.value).toBe('family')
  expect([...role.options].map(o => o.textContent)).toContain('guest')
  expect(input(f1, 'Age').value).toBe('41')
  expect(f1.querySelector<HTMLInputElement>('[data-field="notify"] input[role="switch"]')!.checked).toBe(true)
  expect(button(f1, 'Rename').className).toContain('hs-button--secondary')
  expect(button(f1, 'Delete').className).toContain('hs-button--danger')
  expect(f1.querySelector('img')).toBeNull()
})

it('says why it shows no pages: not saved, not running, or nothing drawn', async () => {
  const unsaved = mount(backend(), { definitionId: undefined })
  await settle()
  expect(unsaved.host.textContent).toBe('Save this entity definition to see its device pages.')
  expect(unsaved.device.requests).toEqual([])
  dispose!(); document.body.innerHTML = ''

  const off = backend()
  off.state.running = false
  const shown = mount(off)
  await settle()
  expect(shown.host.textContent).toBe('This entity definition is not running. Its device pages show while it is on.')
  dispose!(); document.body.innerHTML = ''

  const blank = backend()
  blank.state.pages = []
  const none = mount(blank)
  await settle()
  expect(none.host.textContent).toBe('None of its C# devices has drawn a page.')
})

it('polls every 2 seconds with the known revision and stops while hidden', async () => {
  const { host, device, setVisible } = mount()
  await settle()
  expect(device.reads()).toEqual([{ operation: 'read', definitionId: 'door' }])
  const before = card(host, 'unknown', 'f1')
  await vi.advanceTimersByTimeAsync(1999)
  expect(device.reads()).toHaveLength(1)
  await vi.advanceTimersByTimeAsync(1)
  expect(device.reads()).toHaveLength(2)
  expect(device.reads()[1]).toEqual({ operation: 'read', definitionId: 'door', knownRevision: 'r1' })
  // Unchanged: the same card, nothing redrawn.
  expect(card(host, 'unknown', 'f1')).toBe(before)

  // The tab hidden: no reads, however long.
  setVisible(false)
  await vi.advanceTimersByTimeAsync(10_000)
  expect(device.reads()).toHaveLength(2)
  setVisible(true)
  await settle()
  expect(device.reads()).toHaveLength(3)

  // The browser's page hidden: none either, and a read at once when it shows again.
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' })
  document.dispatchEvent(new Event('visibilitychange'))
  await vi.advanceTimersByTimeAsync(10_000)
  expect(device.reads()).toHaveLength(3)
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' })
  document.dispatchEvent(new Event('visibilitychange'))
  await settle()
  expect(device.reads()).toHaveLength(4)
  await poll()
  expect(device.reads()).toHaveLength(5)

  // Disposed: nothing more.
  dispose!(); dispose = undefined
  await vi.advanceTimersByTimeAsync(10_000)
  expect(device.reads()).toHaveLength(5)
})

it('shows a failed poll as one line and goes on polling', async () => {
  const { host, device } = mount()
  await settle()
  device.state.readError = { code: 'unknown_error', message: 'The device page request failed.' }
  await poll()
  expect(host.querySelector('[role="alert"]')!.textContent).toBe('The device page request failed.')
  // What it showed stays.
  expect(card(host, 'unknown', 'f1')).not.toBeNull()
  device.state.readError = undefined
  device.draw([doorPage([faceCard('f3')])])
  await poll()
  expect(host.querySelector('[role="alert"]')).toBeNull()
  expect(card(host, 'unknown', 'f3')).not.toBeNull()
})

it('ignores an answer for an earlier definition', async () => {
  const device = backend()
  let release!: () => void
  const gate = new Promise<void>(resolve => (release = resolve))
  const inner = device.callWS
  device.callWS = async (message: any) => {
    if (message.request.definitionId === 'door') await gate
    if (message.request.definitionId === 'garage') return { version: 1, ok: true, result: { running: true, revision: 'g1', pages: [] } }
    return inner(message)
  }
  const { host, setDefinitionId } = mount(device)
  await settle()
  setDefinitionId('garage')
  await settle()
  expect(host.textContent).toBe('None of its C# devices has drawn a page.')
  release()
  await settle()
  expect(host.textContent).toBe('None of its C# devices has drawn a page.')
})

it('keeps typed text through a refresh', async () => {
  const { host, device } = mount()
  await settle()
  const name = input(card(host, 'unknown', 'f1')!, 'Name')
  name.focus()
  type(name, 'Iva')
  name.setSelectionRange(1, 1)
  // The device redraws the card with another note.
  device.draw([doorPage([faceCard('f1', { fields: [text('name', 'Name'), text('note', 'Note', 'seen again')] }), faceCard('f2')])])
  await poll()
  const shown = card(host, 'unknown', 'f1')!
  expect(input(shown, 'Name')).toBe(name)
  expect(name.value).toBe('Iva')
  expect(document.activeElement).toBe(name)
  expect(name.selectionStart).toBe(1)
  expect(input(shown, 'Note').value).toBe('seen again')
  // A server value for the field typed into does not win over what was typed.
  device.draw([doorPage([faceCard('f1', { fields: [text('name', 'Name', 'Unknown'), text('note', 'Note', 'seen again')] }), faceCard('f2')])])
  await poll()
  expect(name.value).toBe('Iva')
  // The card goes; once a card of that id comes back, what was typed is gone.
  device.draw([doorPage([faceCard('f2')])])
  await poll()
  expect(card(host, 'unknown', 'f1')).toBeNull()
  device.draw([doorPage([faceCard('f1'), faceCard('f2')])])
  await poll()
  expect(input(card(host, 'unknown', 'f1')!, 'Name').value).toBe('')
})

it('keeps a number being typed through a refresh that changes it, and takes it once typed', async () => {
  const device = backend()
  const counted = (count: number | null) => doorPage([faceCard('f1', { fields: [{ key: 'count', label: 'Count', type: 'number', value: count, required: false }] })])
  device.state.pages = [counted(3)]
  const { host } = mount(device)
  await settle()
  const count = input(card(host, 'unknown', 'f1')!, 'Count')
  expect(count.value).toBe('3')
  count.focus()
  type(count, '12')
  device.draw([counted(4)])
  await poll()
  expect(count.value).toBe('12')
  blur(count)
  device.draw([counted(5)])
  await poll()
  expect(count.value).toBe('12')
  button(card(host, 'unknown', 'f1')!, 'Save').click()
  await settle()
  expect(acts(device)[0].values).toEqual({ count: 12 })
})

it('sends a press with the card\'s values and refreshes', async () => {
  const device = backend()
  device.state.pages = [doorPage([faceCard('f1', {
    fields: [
      text('name', 'Name'),
      { key: 'role', label: 'Role', type: 'select', value: null, options: ['guest', 'family'], required: false },
      { key: 'age', label: 'Age', type: 'number', value: null, required: false },
      { key: 'score', label: 'Score', type: 'number', value: null, required: false },
      { key: 'notify', label: 'Notify', type: 'toggle', value: true, required: false },
    ],
  }), faceCard('f2')])]
  const { host } = mount(device)
  await settle()
  const f1 = card(host, 'unknown', 'f1')!
  type(input(f1, 'Name'), 'Ivan')
  const role = f1.querySelector<HTMLSelectElement>('[data-field="role"] select')!
  role.value = 'family'
  role.dispatchEvent(new Event('change', { bubbles: true }))
  const age = input(f1, 'Age')
  type(age, '41')
  blur(age)
  const notify = f1.querySelector<HTMLInputElement>('[data-field="notify"] input[role="switch"]')!
  notify.click()
  await settle()
  expect(notify.checked).toBe(false)
  const before = device.requests.length
  button(f1, 'Save').click()
  await settle()
  expect(device.requests.slice(before)).toEqual([
    { operation: 'act', definitionId: 'door', nodeKey: 'faces', list: 'unknown', card: 'f1', action: 'save', values: { name: 'Ivan', role: 'family', age: 41, score: null, notify: false } },
    { operation: 'read', definitionId: 'door', knownRevision: 'r1' },
  ])
  // Done: what was typed into the card is reset to what the device shows.
  expect(input(f1, 'Name').value).toBe('')
  expect(input(f1, 'Age').value).toBe('')
})

it('asks before a confirm action, and sends nothing when the dialog is cancelled', async () => {
  const device = backend()
  device.state.pages = [doorPage([faceCard('f1', { actions: [{ id: 'forget', label: 'Forget', style: 'danger', confirm: 'Forget this face and its 14 samples?' }] })])]
  const { host } = mount(device)
  await settle()
  button(card(host, 'unknown', 'f1')!, 'Forget').click()
  await settle()
  const dialog = () => document.querySelector<HTMLDialogElement>('dialog[open]')
  expect(dialog()!.textContent).toContain('Forget this face and its 14 samples?')
  button(dialog()!, 'Cancel').click()
  await settle()
  expect(dialog()).toBeNull()
  expect(acts(device)).toEqual([])
  button(card(host, 'unknown', 'f1')!, 'Forget').click()
  await settle()
  button(dialog()!, 'Forget').click()
  await settle()
  expect(dialog()).toBeNull()
  expect(acts(device)).toEqual([{ operation: 'act', definitionId: 'door', nodeKey: 'faces', list: 'unknown', card: 'f1', action: 'forget', values: { name: '', note: 'first seen at the door' } }])
})

it('shows a refusal under the card', async () => {
  const device = backend()
  device.state.act = () => ({ status: 'refused', message: 'Ivan is already someone else.' })
  const { host } = mount(device)
  await settle()
  button(card(host, 'unknown', 'f1')!, 'Save').click()
  await settle()
  expect(card(host, 'unknown', 'f1')!.textContent).toContain('Ivan is already someone else.')
  expect(card(host, 'unknown', 'f2')!.textContent).not.toContain('Ivan is already someone else.')
  // It stays through a refresh, until the card's next press.
  await poll()
  expect(card(host, 'unknown', 'f1')!.textContent).toContain('Ivan is already someone else.')
  device.state.act = () => ({ status: 'failed', message: 'The camera did not answer.' })
  button(card(host, 'unknown', 'f1')!, 'Save').click()
  await settle()
  expect(card(host, 'unknown', 'f1')!.textContent).not.toContain('Ivan is already someone else.')
  expect(card(host, 'unknown', 'f1')!.textContent).toContain('The camera did not answer.')
  device.state.act = () => ({ status: 'changed' })
  button(card(host, 'unknown', 'f1')!, 'Save').click()
  await settle()
  expect(card(host, 'unknown', 'f1')!.textContent).toContain('This changed meanwhile; the page was refreshed.')
  expect(card(host, 'unknown', 'f1')!.textContent).not.toContain('The camera did not answer.')
  // A definition that stopped meanwhile: the panel says it is not running.
  device.state.running = false
  button(card(host, 'unknown', 'f1')!, 'Save').click()
  await settle()
  expect(host.textContent).toBe('This entity definition is not running. Its device pages show while it is on.')
})

it('disables the card\'s buttons while a press is out', async () => {
  const device = backend()
  let release!: () => void
  device.state.hold = new Promise<void>(resolve => (release = resolve))
  const { host } = mount(device)
  await settle()
  const f1 = card(host, 'unknown', 'f1')!, f2 = card(host, 'unknown', 'f2')!
  const actions = (shown: Element) => [...shown.querySelectorAll<HTMLButtonElement>('.hs-device-card__actions button')]
  button(f1, 'Save').click()
  await settle()
  expect(actions(f1).map(b => b.disabled)).toEqual([true, true])
  expect(actions(f2).map(b => b.disabled)).toEqual([false, false])
  // A poll meanwhile leaves them disabled.
  await poll()
  expect(actions(card(host, 'unknown', 'f1')!).map(b => b.disabled)).toEqual([true, true])
  release()
  await settle()
  expect(actions(f1).map(b => b.disabled)).toEqual([false, false])
})

it('stops a press with an empty required field', async () => {
  const device = backend()
  device.state.pages = [doorPage([faceCard('f1', { fields: [text('name', 'Name', '', { required: true }), text('note', 'Note')] })])]
  const { host } = mount(device)
  await settle()
  const f1 = card(host, 'unknown', 'f1')!
  type(input(f1, 'Name'), '   ')
  button(f1, 'Save').click()
  await settle()
  expect(acts(device)).toEqual([])
  const field = f1.querySelector('[data-field="name"]')!
  expect(field.getAttribute('aria-invalid')).toBe('true')
  expect(field.textContent).toContain('Name is required.')
  // Filled in, it is sent, and the mark goes.
  type(input(f1, 'Name'), 'Ana')
  await settle()
  expect(field.getAttribute('aria-invalid')).toBeNull()
  button(f1, 'Save').click()
  await settle()
  expect(acts(device)).toHaveLength(1)
  expect(acts(device)[0].values).toEqual({ name: 'Ana', note: '' })
})

it('fetches an image once per hash', async () => {
  const device = backend()
  device.state.images = { h1: 'AAAA' }
  const pictured = () => doorPage([faceCard('f1', { image: 'h1' }), faceCard('f2', { image: 'h1' }), faceCard('f3', { image: 'gone' })])
  device.state.pages = [pictured()]
  const { host } = mount(device)
  await settle()
  await settle()
  const images = () => device.requests.filter(r => r.operation === 'image')
  expect(images()).toEqual([
    { operation: 'image', definitionId: 'door', nodeKey: 'faces', hash: 'h1' },
    { operation: 'image', definitionId: 'door', nodeKey: 'faces', hash: 'gone' },
  ])
  expect(card(host, 'unknown', 'f1')!.querySelector('img')!.getAttribute('src')).toBe('data:image/jpeg;base64,AAAA')
  expect(card(host, 'unknown', 'f2')!.querySelector('img')!.getAttribute('src')).toBe('data:image/jpeg;base64,AAAA')
  // An image the card no longer shows: the card without a picture.
  expect(card(host, 'unknown', 'f3')!.querySelector('img')).toBeNull()
  // New revisions that show the same hashes fetch nothing again.
  device.draw([pictured()])
  await poll()
  device.draw([pictured()])
  await poll()
  expect(images()).toHaveLength(2)
  // A hash no card shows is dropped: shown again, it is fetched again.
  device.draw([doorPage([faceCard('f1')])])
  await poll()
  device.draw([doorPage([faceCard('f1', { image: 'h1' })])])
  await poll()
  await settle()
  expect(images().filter(r => r.hash === 'h1')).toHaveLength(2)
})

it('renders markup-like text as text', async () => {
  const device = backend()
  const markup = '<img src=x onerror=alert(1)>'
  const long = 'A'.repeat(500)
  device.state.pages = [{ ...doorPage([faceCard('f1', { title: markup, detail: markup }), faceCard('f2', { title: long })]), title: markup }]
  const { host } = mount(device)
  await settle()
  expect(host.querySelector('img')).toBeNull()
  expect(host.querySelector('h3')!.textContent).toBe(markup)
  expect(card(host, 'unknown', 'f1')!.querySelector('.hs-device-card__title')!.textContent).toBe(markup)
  expect(card(host, 'unknown', 'f1')!.querySelector('.hs-device-card__detail')!.textContent).toBe(markup)
  expect(card(host, 'unknown', 'f2')!.querySelector('.hs-device-card__title')!.textContent).toBe(long)
})

it('takes any field key the device chose, an object member\'s name too', async () => {
  const device = backend()
  device.state.pages = [doorPage([faceCard('f1', { fields: [text('constructor', 'Builder', 'Ana'), text('__proto__', 'Origin', 'door')] })])]
  const { host } = mount(device)
  await settle()
  const f1 = card(host, 'unknown', 'f1')!
  expect(input(f1, 'Builder').value).toBe('Ana')
  type(input(f1, 'Origin'), 'gate')
  button(f1, 'Save').click()
  await settle()
  expect(acts(device)[0].values).toEqual(JSON.parse('{"constructor":"Ana","__proto__":"gate"}'))
  expect(Object.keys(acts(device)[0].values)).toEqual(['constructor', '__proto__'])
})
