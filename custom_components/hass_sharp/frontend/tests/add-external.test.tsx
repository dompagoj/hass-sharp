import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { HassCtx } from '../src/context'
import { AddExternalDialog } from '../src/external/AddExternalDialog'
import { externalApi, type ExternalImportResult, type ExternalInspection, type ExternalItem } from '../src/external/api'

// The Add external dialog over a fake `hass_sharp/external`: `answer` gives each request's envelope (or a promise of
// it, to hold the reply), and every request is recorded.
const sha = '0123456789abcdef0123456789abcdef01234567'
const warning = 'Node libraries and entity definitions run C# inside Home Assistant with full access to it: its devices, its data, its files and your network. Import only from repositories you trust.'
const limit = 'GitHub refused the request: it allows 60 requests an hour without signing in; try again later'
const ok = (result: unknown) => ({ version: 1, ok: true, result })
const refused = (code: string, detail: string) => ({ version: 1, ok: false, error: { code, detail } })
const item = (kind: ExternalItem['kind'], name: string, path: string, fields: Partial<ExternalItem> = {}): ExternalItem =>
  ({ kind, name, description: `${name} described.`, path, existing: [], problem: null, ...fields })
/** hass-sharp-vision's three items, none stored yet. */
const vision = (change: (items: ExternalItem[]) => void = () => {}): ExternalInspection => {
  const items = [item('library', 'Vision.cs', 'nodes/Vision.cs'), item('library', 'PeopleDetector.cs', 'nodes/PeopleDetector.cs'),
    item('entity', 'Front door people', 'nodes/front_door_people.json')]
  change(items)
  return { owner: 'o', repo: 'r', ref: null, sha, items }
}
const outcome = (kind: ExternalItem['kind'], name: string, path: string, outcome: string, fields: object = {}) =>
  ({ kind, name, path, outcome, id: null, enabled: null, message: null, ...fields })
const imported = (items: object[]): ExternalImportResult => ({ owner: 'o', repo: 'r', sha, items, libraries: null } as ExternalImportResult)

let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); dispose = undefined; document.body.innerHTML = '' })

function mount(answer: (request: any) => unknown) {
  const root = document.createElement('div'); root.className = 'hs-app'; document.body.append(root)
  const requests: any[] = []
  const callWS = async (message: any) => {
    expect(message.type).toBe('hass_sharp/external')
    requests.push(structuredClone(message.request))
    return answer(message.request)
  }
  const [open, setOpen] = createSignal(true)
  const onImported = vi.fn()
  dispose = render(() => <HassCtx.Provider value={{ callWS, user: { is_admin: true } } as any}>
    <AddExternalDialog open={open()} onClose={() => setOpen(false)} onImported={onImported} />
  </HassCtx.Provider>, root)
  return { root, requests, open, onImported }
}
const dialog = () => document.querySelector<HTMLDialogElement>('dialog')!
const body = () => dialog().querySelector('.hs-external')!
const button = (text: string) => {
  const found = [...dialog().querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text)
  expect(found, text).toBeDefined()
  return found!
}
const field = () => [...dialog().querySelectorAll('label')].find(l => l.textContent === 'Repository URL')?.control as HTMLInputElement | undefined
const type = (input: HTMLInputElement, value: string) => { input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })) }
const checkbox = (name: string) => {
  const found = [...dialog().querySelectorAll<HTMLInputElement>('input[type="checkbox"]')].find(box => box.labels?.[0]?.textContent?.trim() === name)
  expect(found, name).toBeDefined()
  return found!
}
const choice = (name: string) => dialog().querySelector<HTMLSelectElement>(`select[aria-label="How to import ${name}"]`)
/** The option a select shows (Happy DOM's `selectedIndex` misses an option selected as it is inserted). */
const shown = (select: HTMLSelectElement) => [...select.options].find(o => o.selected)?.textContent
const choose = (select: HTMLSelectElement, label: string) => {
  const option = [...select.options].find(o => o.textContent === label)
  expect(option, label).toBeDefined()
  select.value = option!.value; select.dispatchEvent(new Event('change', { bubbles: true }))
}
const lines = () => [...dialog().querySelectorAll('[aria-label="Import result"] li')].map(li => li.textContent)
/** A group of the list, by its heading. */
const group = (name: string) => [...dialog().querySelectorAll('section')].find(section => section.querySelector('h3')?.textContent === name)
const settle = () => vi.waitFor(() => expect(body().getAttribute('aria-busy')).not.toBe('true'))
async function inspect(url = 'https://github.com/o/r') {
  type(field()!, url)
  button('Inspect').click()
  await vi.waitFor(() => expect(dialog().querySelector('section h3')).not.toBeNull())
}

it('inspects, picks and imports', async () => {
  const { requests, onImported } = mount(request => request.operation === 'inspect' ? ok(vision()) : ok(imported([
    outcome('library', 'Vision.cs', 'nodes/Vision.cs', 'imported', { id: 'vision' }),
    outcome('library', 'PeopleDetector.cs', 'nodes/PeopleDetector.cs', 'imported', { id: 'people_detector' }),
    outcome('entity', 'Front door people', 'nodes/front_door_people.json', 'imported', { id: 'front_door_people', enabled: false })])))
  expect(dialog().open).toBe(true)
  expect(document.getElementById(dialog().getAttribute('aria-labelledby')!)?.textContent).toBe('Add external')
  expect(dialog().textContent).toContain('Paste the address of a public GitHub repository with a hass_sharp.toml at its root, such as https://github.com/owner/repo or https://github.com/owner/repo/tree/main.')
  expect(dialog().querySelector('[role="note"]')?.textContent).toBe(warning)
  expect(button('Inspect').disabled).toBe(true)
  await inspect()
  expect(requests).toEqual([{ operation: 'inspect', url: 'https://github.com/o/r' }])
  // The repository and its commit head the list; each group by its heading.
  expect(dialog().querySelector('.hs-external__repo')?.textContent).toBe('o/r 0123456')
  expect(dialog().textContent).not.toContain(sha)
  expect([...dialog().querySelectorAll('h3')].map(h => h.textContent)).toEqual(expect.arrayContaining(['Node libraries', 'Entity definitions']))
  expect(dialog().querySelector('[role="note"]')?.textContent).toBe(warning)
  expect(dialog().textContent).toContain('Vision.cs described.')
  const links = [...dialog().querySelectorAll<HTMLAnchorElement>('a')].filter(a => a.textContent === 'View on GitHub')
  expect(links.map(a => a.getAttribute('href'))).toEqual([`https://github.com/o/r/blob/${sha}/nodes/Vision.cs`,
    `https://github.com/o/r/blob/${sha}/nodes/PeopleDetector.cs`, `https://github.com/o/r/blob/${sha}/nodes/front_door_people.json`])
  expect(links.every(a => a.target === '_blank' && a.rel === 'noreferrer')).toBe(true)
  // Nothing stored of those names: each is picked, with no choice to make.
  expect(['Vision.cs', 'PeopleDetector.cs', 'Front door people'].map(name => checkbox(name).checked)).toEqual([true, true, true])
  // Inspect is gone with its step: focus moves to the first item.
  await vi.waitFor(() => expect(document.activeElement).toBe(checkbox('Vision.cs')))
  expect(dialog().querySelector('select')).toBeNull()
  button('Import').click()
  await vi.waitFor(() => expect(lines()).toHaveLength(3))
  expect(requests[1]).toEqual({ operation: 'import', owner: 'o', repo: 'r', sha, picks: [
    { kind: 'library', path: 'nodes/Vision.cs', mode: 'new' }, { kind: 'library', path: 'nodes/PeopleDetector.cs', mode: 'new' },
    { kind: 'entity', path: 'nodes/front_door_people.json', mode: 'new' }] })
  expect(lines()).toEqual(['Vision.cs: imported', 'PeopleDetector.cs: imported', 'Front door people: imported, turned off'])
  expect(onImported).toHaveBeenCalledTimes(1)
  expect(onImported.mock.calls[0][0].items).toHaveLength(3)
  expect(dialog().querySelector('[role="note"]')?.textContent).toBe(warning)
  expect(dialog().querySelector('.hs-dialog-footer')!.textContent).toBe('Close')
  await vi.waitFor(() => expect(document.activeElement).toBe(button('Close')))
  button('Close').click()
  await vi.waitFor(() => expect(document.querySelector('dialog')?.open ?? false).toBe(false))
})

it('submits the address with Enter, and names a ref it was given', async () => {
  const { requests } = mount(() => ok({ ...vision(), ref: 'main' }))
  type(field()!, 'https://github.com/o/r/tree/main')
  field()!.form!.requestSubmit()
  await vi.waitFor(() => expect(requests).toEqual([{ operation: 'inspect', url: 'https://github.com/o/r/tree/main' }]))
  await vi.waitFor(() => expect(dialog().querySelector('.hs-external__repo')?.textContent).toBe('o/r main · 0123456'))
})

it('existing items start skipped', async () => {
  const stored = [{ id: 'front_door_people', revision: 3, name: 'Front door people' }]
  const { requests } = mount(request => request.operation === 'inspect' ? ok(vision(items => { items[2].existing = stored }))
    : ok(imported([outcome('entity', 'Front door people', 'nodes/front_door_people.json', request.picks[0].mode === 'new' ? 'imported' : 'overwritten', { id: 'front_door_people_2', enabled: false })])))
  await inspect()
  expect(checkbox('Front door people').checked).toBe(false)
  expect(choice('Front door people')).toBeNull()
  checkbox('Vision.cs').click(); checkbox('PeopleDetector.cs').click()
  expect(button('Import').disabled).toBe(true)
  checkbox('Front door people').click()
  expect(button('Import').disabled).toBe(false)
  const select = choice('Front door people')!
  expect([...select.options].map(o => o.textContent)).toEqual(['Overwrite Front door people (front_door_people)', 'Add as new'])
  expect(shown(select)).toBe('Overwrite Front door people (front_door_people)')
  choose(select, 'Add as new')
  expect(shown(select)).toBe('Add as new')
  button('Import').click()
  await vi.waitFor(() => expect(lines()).toEqual(['Front door people: imported, turned off']))
  expect(requests[1].picks).toEqual([{ kind: 'entity', path: 'nodes/front_door_people.json', mode: 'new' }])
})

it('overwrites an existing entity definition at the revision inspect answered', async () => {
  const stored = [{ id: 'front_door_people', revision: 3, name: 'Front door people' }]
  const { requests } = mount(request => request.operation === 'inspect' ? ok(vision(items => { items[2].existing = stored }))
    : ok(imported([outcome('entity', 'Front door people', 'nodes/front_door_people.json', 'overwritten', { id: 'front_door_people', enabled: true })])))
  await inspect()
  checkbox('Vision.cs').click(); checkbox('PeopleDetector.cs').click(); checkbox('Front door people').click()
  choose(choice('Front door people')!, 'Add as new')
  choose(choice('Front door people')!, 'Overwrite Front door people (front_door_people)')
  button('Import').click()
  await vi.waitFor(() => expect(lines()).toEqual(['Front door people: overwritten']))
  expect(requests[1].picks).toEqual([{ kind: 'entity', path: 'nodes/front_door_people.json', mode: 'overwrite', target: 'front_door_people', expectedRevision: 3 }])
})

it('a library offers no second copy', async () => {
  const stored = [{ id: 'a', revision: 1, name: 'Vision.cs' }, { id: 'b', revision: 4, name: 'Vision.cs' }]
  const { requests } = mount(request => request.operation === 'inspect' ? ok(vision(items => { items[0].existing = stored }))
    : ok(imported([outcome('library', 'Vision.cs', 'nodes/Vision.cs', 'overwritten', { id: 'b' }), outcome('library', 'PeopleDetector.cs', 'nodes/PeopleDetector.cs', 'imported', { id: 'people_detector' }),
      outcome('entity', 'Front door people', 'nodes/front_door_people.json', 'imported', { id: 'front_door_people', enabled: false })])))
  await inspect()
  expect(checkbox('Vision.cs').checked).toBe(false)
  checkbox('Vision.cs').click()
  const select = choice('Vision.cs')!
  expect([...select.options].map(o => o.textContent)).toEqual(['Overwrite Vision.cs (a)', 'Overwrite Vision.cs (b)'])
  choose(select, 'Overwrite Vision.cs (b)')
  button('Import').click()
  await vi.waitFor(() => expect(lines()).toHaveLength(3))
  // A library's target is sent only with an overwrite: a new one takes none.
  expect(requests[1].picks).toEqual([{ kind: 'library', path: 'nodes/Vision.cs', mode: 'overwrite', target: 'b', expectedRevision: 4 },
    { kind: 'library', path: 'nodes/PeopleDetector.cs', mode: 'new' }, { kind: 'entity', path: 'nodes/front_door_people.json', mode: 'new' }])
  expect(lines()[0]).toBe('Vision.cs: overwritten')
})

it('an item with a problem cannot be picked', async () => {
  const problem = 'nodes/front_door_people.json is not in the repository at 0123456'
  const { requests } = mount(request => request.operation === 'inspect' ? ok(vision(items => { items[2].problem = problem }))
    : ok(imported([outcome('library', 'Vision.cs', 'nodes/Vision.cs', 'imported'), outcome('library', 'PeopleDetector.cs', 'nodes/PeopleDetector.cs', 'imported')])))
  await inspect()
  const box = checkbox('Front door people')
  expect(box.disabled).toBe(true)
  expect(box.checked).toBe(false)
  expect(dialog().textContent).toContain(problem)
  expect(document.getElementById(box.getAttribute('aria-describedby')!.split(' ').at(-1)!)?.textContent).toBe(problem)
  button('Import').click()
  await vi.waitFor(() => expect(lines()).toHaveLength(2))
  expect(requests[1].picks.map((pick: any) => pick.path)).toEqual(['nodes/Vision.cs', 'nodes/PeopleDetector.cs'])
})

it('shows only the groups that have items', async () => {
  mount(() => ok(vision(items => items.splice(0, 2))))
  await inspect()
  expect(group('Node libraries')).toBeUndefined()
  expect([...dialog().querySelectorAll('h3')].map(h => h.textContent)).toEqual(['Entity definitions'])
  expect(group('Entity definitions')!.getAttribute('aria-labelledby')).toBe(group('Entity definitions')!.querySelector('h3')!.id)
})

it('a refused library shows on every line', async () => {
  const compiler = 'PeopleDetector.cs 3:5: The type or namespace name \'VisionDetector\' could not be found'
  const { onImported } = mount(request => request.operation === 'inspect' ? ok(vision()) : ok(imported([
    outcome('library', 'Vision.cs', 'nodes/Vision.cs', 'failed', { message: compiler }),
    outcome('library', 'PeopleDetector.cs', 'nodes/PeopleDetector.cs', 'failed', { message: compiler }),
    outcome('entity', 'Front door people', 'nodes/front_door_people.json', 'failed', { message: 'Not imported, because the node libraries were not' })])))
  await inspect()
  button('Import').click()
  await vi.waitFor(() => expect(lines()).toHaveLength(3))
  expect(lines()).toEqual([`Vision.cs: not imported — ${compiler}`, `PeopleDetector.cs: not imported — ${compiler}`,
    'Front door people: not imported — Not imported, because the node libraries were not'])
  expect(onImported).toHaveBeenCalledTimes(1)
})

it('cannot close while fetching, and recovers from a refusal', async () => {
  let release!: (answer: unknown) => void
  const { open, requests } = mount(() => new Promise(go => { release = go }))
  type(field()!, 'https://github.com/o/r')
  button('Inspect').click()
  await vi.waitFor(() => expect(requests).toHaveLength(1))
  expect(body().getAttribute('aria-busy')).toBe('true')
  dialog().dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  button('Cancel').click()
  dialog().querySelector<HTMLButtonElement>('button[aria-label="Close dialog"]')!.click()
  await Promise.resolve()
  expect(open()).toBe(true)
  release(refused('fetch', limit))
  await settle()
  expect(dialog().querySelector('[role="alert"]')?.textContent).toBe(limit)
  expect(field()?.value).toBe('https://github.com/o/r')
  await vi.waitFor(() => expect(document.activeElement).toBe(field()))
  expect(dialog().querySelector('[role="note"]')?.textContent).toBe(warning)
  button('Cancel').click()
  expect(open()).toBe(false)
})

it('a refused import shows its sentence and keeps the picks', async () => {
  const conflict = 'nodes/Vision.cs: target must be the id of the stored library file named Vision.cs: a'
  let import_!: (answer: unknown) => void
  const { open, onImported } = mount(request => request.operation === 'inspect' ? ok(vision()) : new Promise(go => { import_ = go }))
  await inspect()
  checkbox('PeopleDetector.cs').click()
  button('Import').click()
  await vi.waitFor(() => expect(body().getAttribute('aria-busy')).toBe('true'))
  dialog().dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  expect(open()).toBe(true)
  import_(refused('invalid_request', conflict))
  await settle()
  expect(dialog().querySelector('[role="alert"]')?.textContent).toBe(conflict)
  expect(checkbox('Vision.cs').checked).toBe(true)
  expect(checkbox('PeopleDetector.cs').checked).toBe(false)
  expect(onImported).not.toHaveBeenCalled()
  button('Cancel').click()
  expect(open()).toBe(false)
})

it('reads Home Assistant’s own refusal as its message', async () => {
  const api = externalApi({ callWS: () => Promise.reject({ code: 'unauthorized', message: 'Unauthorized' }) } as any)
  const error = await api.inspect('https://github.com/o/r').catch(e => e)
  expect(error).toBeInstanceOf(Error)
  expect(error.message).toBe('Unauthorized')
  await expect(externalApi({ callWS: () => Promise.reject(3) } as any).inspect('x')).rejects.toThrow('The connection to Home Assistant was lost.')
  const envelope = externalApi({ callWS: async () => refused('not_found', 'GitHub has no public repository o/r') } as any)
  await expect(envelope.inspect('https://github.com/o/r')).rejects.toThrow('GitHub has no public repository o/r')
})

it('encodes each part of a path in its GitHub link', async () => {
  mount(() => ok(vision(items => { items[0].path = 'nodes/a b#1.cs' })))
  await inspect()
  const link = [...dialog().querySelectorAll<HTMLAnchorElement>('a')].find(a => a.textContent === 'View on GitHub')!
  expect(link.getAttribute('href')).toBe(`https://github.com/o/r/blob/${sha}/nodes/a%20b%231.cs`)
})
