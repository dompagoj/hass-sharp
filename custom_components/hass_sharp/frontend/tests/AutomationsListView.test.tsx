import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { MemoryRouter, Route } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { VisualAutomationView } from '../src/views/VisualAutomationView'
import { visualFixture } from './visual-fixture'
import { createGraph } from '../src/visual/graph'
import { AutomationsListView } from '../src/views/AutomationsListView'

let dispose: (() => void) | undefined
let client: QueryClient
afterEach(() => { dispose?.(); client.clear(); document.body.innerHTML = '' })
const scripts = [{ fileName: 'Heating.cs', classes: [{ name: 'Home.Heating', methods: ['Start'] }] }, { fileName: 'Lights.cs', classes: [] }]
function setup(failWrites = false, admin = true, refresh?: () => Promise<unknown>) {
  const root = document.createElement('div'); document.body.append(root)
  const calls: { method: string; path: string; data: unknown }[] = []
  const fixture = visualFixture()
  const hass = { ...fixture, user: { is_admin: admin }, callWS: (message: any) => message.type === 'hass_sharp/reload_entities' && refresh ? refresh() : fixture.callWS(message), callApi: async (method: string, path: string, data: unknown) => {
    calls.push({ method, path, data })
    if (method === 'GET') return scripts
    if (failWrites) throw { body: { error: 'Name already exists' } }
    return { fileName: 'Warm house.cs' }
  } }
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={hass as any}>
    <MemoryRouter><Route path="/" component={AutomationsListView} /><Route path="/visual/new" component={VisualAutomationView} /></MemoryRouter>
  </HassCtx.Provider></QueryClientProvider>, root)
  return { root, calls, fixture }
}
const press = (element: Element, key: string) => element.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
it('filters by file and class, and keeps empty-class files reachable', async () => {
  const { root } = setup()
  await vi.waitFor(() => expect(root.textContent).toContain('Lights.cs'))
  expect(root.querySelector('a[href="/automations/Lights.cs"]')).not.toBeNull()
  const search = root.querySelector<HTMLInputElement>('input[type="search"]')!
  expect(search).not.toBeNull()
  search.value = 'home.heating'; search.dispatchEvent(new Event('input', { bubbles: true }))
  expect(root.textContent).toContain('Heating.cs')
  expect(root.textContent).not.toContain('Lights.cs')
  search.value = 'missing'; search.dispatchEvent(new Event('input', { bubbles: true }))
  expect(root.querySelector('[role="status"]')?.textContent).toContain('No automations')
})
it('opens row actions by keyboard, renames with form submit, and restores focus on cancel', async () => {
  const { root, calls } = setup(true)
  await vi.waitFor(() => expect(root.textContent).toContain('Heating.cs'))
  const trigger = root.querySelector<HTMLButtonElement>('button[aria-label="Actions for Heating.cs"]')!
  expect(trigger).not.toBeNull()
  trigger.focus(); press(trigger, 'ArrowDown')
  const items = root.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')
  expect(items).toHaveLength(2)
  expect(document.activeElement).toBe(items[0])
  press(items[0], 'ArrowUp'); expect(document.activeElement).toBe(items[1])
  press(items[1], 'Escape'); expect(document.activeElement).toBe(trigger)
  trigger.click()
  root.querySelector<HTMLButtonElement>('[role="menuitem"]')!.click()
  await Promise.resolve()
  const input = root.querySelector<HTMLInputElement>('input[name="name"]')!
  expect(input.value).toBe('Heating.cs')
  expect(document.activeElement).toBe(input)
  input.value = 'Warm house'; input.dispatchEvent(new Event('input', { bubbles: true }))
  root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  await vi.waitFor(() => expect(root.querySelector('[role="alert"]')?.textContent).toContain('Name already exists'))
  expect(calls).toContainEqual({ method: 'PUT', path: 'hass-sharp/automations/Heating.cs', data: { name: 'Warm house' } })
  press(input, 'Escape'); await Promise.resolve()
  expect(document.activeElement).toBe(trigger)
})
it('starts a named Visual draft without posting a C# automation', async () => {
  const { root, calls } = setup()
  await vi.waitFor(() => expect(root.textContent).toContain('Heating.cs'))
  ;[...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.includes('New automation'))!.click()
  await Promise.resolve()
  const visual = root.querySelector<HTMLInputElement>('input[value="visual"]')!
  expect(visual).not.toBeNull()
  visual.click()
  const name = root.querySelector<HTMLInputElement>('input[name="name"]')!
  name.value = 'Morning lights'; name.dispatchEvent(new Event('input', { bubbles: true }))
  root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  await Promise.resolve()
  expect(calls.filter(call => call.method !== 'GET')).toEqual([])
  expect(root.querySelector('[aria-label="Visual automation editor"] h1')?.textContent).toBe('Morning lights')
})
it('keeps C# creation on the existing backend endpoint', async () => {
  const { root, calls } = setup()
  await vi.waitFor(() => expect(root.textContent).toContain('Heating.cs'))
  ;[...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.includes('New automation'))!.click()
  await Promise.resolve()
  const name = root.querySelector<HTMLInputElement>('input[name="name"]')!
  name.value = 'Warm house'; name.dispatchEvent(new Event('input', { bubbles: true }))
  root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  await vi.waitFor(() => expect(calls).toContainEqual({ method: 'POST', path: 'hass-sharp/automations', data: { name: 'Warm house' } }))
})

it('merges saved visual documents with C# files and filters by graph name', async () => {
  const { root, fixture } = setup()
  await vi.waitFor(() => expect(client.getQueryState(['visual-automations'])?.status).toBe('success'))
  fixture.documents.set('motion', { version: 1, id: 'motion', revision: 7, graph: createGraph('Hall motion'), source: 'server', sourceMap: [], automationId: 'visual:motion' })
  await client.invalidateQueries({ queryKey: ['visual-automations'] })
  await vi.waitFor(() => expect(root.querySelector('a[href="/visual/motion"]')?.textContent).toContain('Hall motion'))
  expect(root.textContent).toContain('Lights.cs')
  const search = root.querySelector<HTMLInputElement>('input[type="search"]')!
  search.value = 'hall'; search.dispatchEvent(new Event('input', { bubbles: true }))
  expect(root.textContent).toContain('Hall motion')
  expect(root.textContent).not.toContain('Heating.cs')
})
it('deletes visual documents with their listed revision and preserves a conflict for review', async () => {
  const { root, calls, fixture } = setup()
  await vi.waitFor(() => expect(client.getQueryState(['visual-automations'])?.status).toBe('success'))
  fixture.documents.set('motion', { version: 1, id: 'motion', revision: 7, graph: createGraph('Hall motion'), source: 'server', sourceMap: [], automationId: 'visual:motion' })
  await client.invalidateQueries({ queryKey: ['visual-automations'] })
  await vi.waitFor(() => expect(root.textContent).toContain('Hall motion'))
  fixture.controls.deleteConflict = true
  root.querySelector<HTMLButtonElement>('[aria-label="Actions for Hall motion"]')!.click()
  ;[...root.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].find(b => b.textContent === 'Delete')!.click()
  const deleteButton = () => [...root.querySelectorAll<HTMLButtonElement>('dialog button')].find(b => b.textContent?.trim() === 'Delete')!
  deleteButton().click()
  await vi.waitFor(() => expect(root.querySelector('dialog [role="alert"]')?.textContent).toContain('Changed elsewhere'))
  expect(fixture.documents.has('motion')).toBe(true)
  expect(calls.filter(c => c.method === 'DELETE')).toEqual([])
  expect(fixture.calls.find(c => c.request?.operation === 'delete').request).toEqual({ operation: 'delete', id: 'motion', expectedRevision: 7 })
  fixture.controls.deleteConflict = false
  deleteButton().click()
  await vi.waitFor(() => expect(root.querySelector('a[href="/visual/motion"]')).toBeNull())
})

it('refreshes symbols once, reports progress and invalidates entity suggestions', async () => {
  let finish!: (value: unknown) => void
  const refresh = vi.fn(() => new Promise(resolve => { finish = resolve }))
  const { root } = setup(false, true, refresh)
  client.setQueryData(['hass-entities'], [{ entity_id: 'light.old' }])
  const button = [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent === 'Refresh entity IDs')!
  expect(button).toBeDefined(); button.click(); button.click()
  expect(button.disabled).toBe(true); expect(refresh).toHaveBeenCalledTimes(1)
  expect(root.textContent).toContain('Refreshing entity IDs')
  finish({ success: true })
  await vi.waitFor(() => expect(root.textContent).toContain('Entity IDs refreshed'))
  expect(client.getQueryState(['hass-entities'])?.isInvalidated).toBe(true)
  expect(button.disabled).toBe(false)
})
it('reports refresh failure and allows retry', async () => {
  const refresh = vi.fn().mockRejectedValueOnce(new Error('offline')).mockResolvedValue({ success: true })
  const { root } = setup(false, true, refresh)
  const button = [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent === 'Refresh entity IDs')!
  expect(button).toBeDefined(); button.click()
  await vi.waitFor(() => expect(root.textContent).toContain('Could not refresh entity IDs'))
  expect(button.disabled).toBe(false); button.click()
  await vi.waitFor(() => expect(root.textContent).toContain('Entity IDs refreshed'))
})
it('hides entity refresh from non-administrators', () => {
  const { root } = setup(false, false)
  expect(root.textContent).not.toContain('Refresh entity IDs')
})
