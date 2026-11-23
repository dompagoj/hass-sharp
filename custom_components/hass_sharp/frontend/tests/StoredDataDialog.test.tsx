import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { HassCtx } from '../src/context'
import { StoredDataDialog } from '../src/entities/StoredDataDialog'
import type { SharedStore } from '../src/entities/deviceUi'

// The Stored data dialog over a fake `hass_sharp/device_ui`: `answer` gives each request's envelope (or a promise of it,
// to hold the reply), and every request is recorded.
const ok = (result: unknown) => ({ version: 1, ok: true, result })
const refused = (code: string, detail: string) => ({ version: 1, ok: false, error: { code, detail } })
const people: SharedStore = { name: 'hass-sharp-vision.people', bytes: 1536, users: ['driveway', 'gone'] }
const plates: SharedStore = { name: 'plates', bytes: 1, users: [] }
const note = 'Data a single device keeps for itself is deleted with its entity definition, or when its node is removed and the definition is saved.'

let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); dispose = undefined; document.body.innerHTML = '' })

/** The dialog, closed until `show()`, over a backend whose `stores` answers `stores()` and other requests `answer`. */
function mount(stores: () => SharedStore[] | Promise<unknown> | unknown, answer: (request: any) => unknown = () => ok({ deleted: true })) {
  const root = document.createElement('div'); root.className = 'hs-app'; document.body.append(root)
  const requests: any[] = []
  const callWS = async (message: any) => {
    expect(message.type).toBe('hass_sharp/device_ui')
    requests.push(structuredClone(message.request))
    if (message.request.operation !== 'stores') return answer(message.request)
    const listed = await stores()
    return Array.isArray(listed) ? ok({ shared: listed }) : listed
  }
  const [open, setOpen] = createSignal(false)
  const names: Record<string, string> = { driveway: 'Driveway camera', porch: 'Porch camera' }
  dispose = render(() => <HassCtx.Provider value={{ callWS, user: { is_admin: true } } as any}>
    <StoredDataDialog open={open()} definitionName={id => (Object.hasOwn(names, id) ? names[id] : undefined)} onClose={() => setOpen(false)} />
  </HassCtx.Provider>, root)
  return { root, requests, open, show: () => setOpen(true) }
}
const dialogs = () => [...document.querySelectorAll<HTMLDialogElement>('dialog[open]')]
const dialog = () => dialogs()[0]
const confirm = () => dialogs()[1]
const titleOf = (found: HTMLDialogElement) => document.getElementById(found.getAttribute('aria-labelledby')!)?.textContent
const button = (within: Element, text: string) => {
  const found = [...within.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text || b.getAttribute('aria-label') === text)
  expect(found, text).toBeDefined()
  return found!
}
const rows = () => [...dialog().querySelectorAll('li')].map(row => [...row.querySelectorAll('.hs-stored__name, .hs-stored__size, .hs-stored__users')].map(part => part.textContent))

it('lists the shared stores with size and users', async () => {
  const page = mount(() => [people, plates, { name: '<b>big</b>', bytes: 5 * 1024 * 1024 + 300 * 1024, users: ['porch'] }])
  // Nothing is read while it is closed.
  await new Promise(resolve => setTimeout(resolve, 0))
  expect(page.requests).toEqual([])
  page.show()
  await vi.waitFor(() => expect(rows()).toHaveLength(3))
  expect(titleOf(dialog())).toBe('Stored data')
  expect(page.requests).toEqual([{ operation: 'stores' }])
  // A definition the page does not list is named by its id; names are text, never markup.
  expect(rows()).toEqual([
    ['hass-sharp-vision.people', '1.5 KiB', 'Used by Driveway camera and gone'],
    ['plates', '1 byte', 'Not in use'],
    ['<b>big</b>', '5.3 MiB', 'Used by Porch camera'],
  ])
  expect(dialog().querySelector('b')).toBeNull()
  expect(button(dialog(), 'Delete hass-sharp-vision.people')).toBeDefined()
  expect(dialog().textContent).toContain(note)
})

it('says when no device has stored shared data', async () => {
  const page = mount(() => [])
  page.show()
  await vi.waitFor(() => expect(dialog().textContent).toContain('No device has stored shared data.'))
  expect(dialog().querySelector('li')).toBeNull()
  expect(dialog().textContent).toContain(note)
})

it('shows a list that cannot be read as one line, with Try again', async () => {
  let fail = true
  const page = mount(() => (fail ? refused('storage', 'The shared stores could not be read.') : [plates]))
  page.show()
  await vi.waitFor(() => expect(dialog().querySelector('[role="alert"]')?.textContent).toContain('The shared stores could not be read.'))
  fail = false
  button(dialog(), 'Try again').click()
  await vi.waitFor(() => expect(rows()).toEqual([['plates', '1 byte', 'Not in use']]))
  expect(dialog().querySelector('[role="alert"]')).toBeNull()
  expect(page.requests).toEqual([{ operation: 'stores' }, { operation: 'stores' }])
})

it('asks, deletes and reloads', async () => {
  let listed = [people, plates]
  let release!: () => void
  const page = mount(() => listed, request => new Promise(resolve => { release = () => { listed = [people]; resolve(ok({ deleted: true })) } }))
  page.show()
  await vi.waitFor(() => expect(rows()).toHaveLength(2))
  button(dialog(), 'Delete plates').click()
  await vi.waitFor(() => expect(confirm()).toBeDefined())
  expect(titleOf(confirm())).toBe('Delete plates?')
  expect(confirm().textContent).toContain('Everything devices stored in it is removed and cannot be brought back.')
  button(confirm(), 'Delete').click()
  // While the delete is out, its buttons wait.
  await vi.waitFor(() => expect(page.requests.at(-1)).toEqual({ operation: 'delete_store', name: 'plates' }))
  expect(button(confirm(), 'Cancel').disabled).toBe(true)
  expect(button(confirm(), 'Delete').disabled).toBe(true)
  release()
  await vi.waitFor(() => expect(rows()).toEqual([['hass-sharp-vision.people', '1.5 KiB', 'Used by Driveway camera and gone']]))
  expect(dialogs()).toHaveLength(1)
  expect(page.requests).toEqual([{ operation: 'stores' }, { operation: 'delete_store', name: 'plates' }, { operation: 'stores' }])
})

it('takes a store already gone as deleted, and reads the list again', async () => {
  let listed = [people, plates]
  const page = mount(() => listed, () => { listed = [people]; return refused('not_found', 'There is no shared store plates.') })
  page.show()
  await vi.waitFor(() => expect(rows()).toHaveLength(2))
  button(dialog(), 'Delete plates').click()
  await vi.waitFor(() => expect(confirm()).toBeDefined())
  button(confirm(), 'Delete').click()
  await vi.waitFor(() => expect(rows()).toHaveLength(1))
  expect(dialogs()).toHaveLength(1)
  expect(dialog().querySelector('[role="alert"]')).toBeNull()
})

it('cancel deletes nothing', async () => {
  const page = mount(() => [people, plates])
  page.show()
  await vi.waitFor(() => expect(rows()).toHaveLength(2))
  button(dialog(), 'Delete plates').click()
  await vi.waitFor(() => expect(confirm()).toBeDefined())
  button(confirm(), 'Cancel').click()
  expect(dialogs()).toHaveLength(1)
  expect(rows()).toHaveLength(2)
  expect(page.requests).toEqual([{ operation: 'stores' }])
})

it('shows an in-use refusal', async () => {
  const page = mount(() => [people], () => refused('in_use', 'Driveway camera uses hass-sharp-vision.people; turn it off first.'))
  page.show()
  await vi.waitFor(() => expect(rows()).toHaveLength(1))
  button(dialog(), 'Delete hass-sharp-vision.people').click()
  await vi.waitFor(() => expect(confirm()).toBeDefined())
  button(confirm(), 'Delete').click()
  await vi.waitFor(() => expect(dialog().querySelector('[role="alert"]')?.textContent).toBe('Driveway camera uses hass-sharp-vision.people; turn it off first.'))
  expect(dialogs()).toHaveLength(1)
  expect(rows()).toHaveLength(1)
  expect(page.requests).toEqual([{ operation: 'stores' }, { operation: 'delete_store', name: 'hass-sharp-vision.people' }])
})
