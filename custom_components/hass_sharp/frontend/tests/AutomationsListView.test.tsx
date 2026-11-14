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
function setup(admin = true, refresh?: () => Promise<unknown>) {
  const root = document.createElement('div'); document.body.append(root)
  const fixture = visualFixture()
  // The list reads saved visual documents only; any REST call would be a leftover of hand-written automations.
  const callApi = vi.fn(async () => { throw new Error('unexpected REST call') })
  const hass = { ...fixture, user: { is_admin: admin }, callWS: (message: any) => message.type === 'hass_sharp/reload_entities' && refresh ? refresh() : fixture.callWS(message), callApi }
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={hass as any}>
    <MemoryRouter><Route path="/" component={AutomationsListView} /><Route path="/visual/new" component={VisualAutomationView} /><Route path="/visual/:id" component={() => <h1 data-opened>Opened</h1>} /></MemoryRouter>
  </HassCtx.Provider></QueryClientProvider>, root)
  return { root, fixture, callApi }
}
const press = (element: Element, key: string) => element.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
async function withSaved(fixture: ReturnType<typeof visualFixture>, ...names: [string, string][]) {
  await vi.waitFor(() => expect(client.getQueryState(['visual-automations'])?.status).toBe('success'))
  for (const [id, name] of names) fixture.documents.set(id, { version: 1, id, revision: 7, graph: createGraph(name), source: 'server', sourceMap: [], automationId: `visual:${id}` })
  await client.invalidateQueries({ queryKey: ['visual-automations'] })
}

it('lists saved visual documents and filters them by name', async () => {
  const { root, fixture, callApi } = setup()
  await withSaved(fixture, ['motion', 'Hall motion'], ['heating', 'Warm house'])
  await vi.waitFor(() => expect(root.querySelector('a[href="/visual/motion"]')?.textContent).toContain('Hall motion'))
  expect(root.querySelector('a[href="/visual/heating"]')?.textContent).toContain('Warm house')
  const search = root.querySelector<HTMLInputElement>('input[type="search"]')!
  search.value = 'hall'; search.dispatchEvent(new Event('input', { bubbles: true }))
  expect(root.textContent).toContain('Hall motion')
  expect(root.textContent).not.toContain('Warm house')
  search.value = 'missing'; search.dispatchEvent(new Event('input', { bubbles: true }))
  expect(root.querySelector('[role="status"]')?.textContent).toContain('No automations found')
  expect(callApi).not.toHaveBeenCalled()
})
it('opens row actions by keyboard, restores focus on escape, and edits in the visual editor', async () => {
  const { root, fixture } = setup()
  await withSaved(fixture, ['motion', 'Hall motion'])
  await vi.waitFor(() => expect(root.textContent).toContain('Hall motion'))
  const trigger = root.querySelector<HTMLButtonElement>('button[aria-label="Actions for Hall motion"]')!
  expect(trigger).not.toBeNull()
  trigger.focus(); press(trigger, 'ArrowDown')
  const items = root.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')
  expect(items).toHaveLength(2)
  expect(document.activeElement).toBe(items[0])
  press(items[0], 'ArrowUp'); expect(document.activeElement).toBe(items[1])
  press(items[1], 'Escape'); expect(document.activeElement).toBe(trigger)
  trigger.click()
  ;[...root.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].find(b => b.textContent === 'Edit / rename')!.click()
  await vi.waitFor(() => expect(root.querySelector('[data-opened]')).not.toBeNull())
})
it('starts a named visual draft from a name alone, without any request', async () => {
  const { root, fixture, callApi } = setup()
  await vi.waitFor(() => expect(client.getQueryState(['visual-automations'])?.status).toBe('success'))
  ;[...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.includes('New automation'))!.click()
  await Promise.resolve()
  expect(root.querySelector('input[type="radio"]')).toBeNull()
  const name = root.querySelector<HTMLInputElement>('input[name="name"]')!
  expect(document.activeElement).toBe(name)
  name.value = 'Morning lights'; name.dispatchEvent(new Event('input', { bubbles: true }))
  root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  await vi.waitFor(() => expect(root.querySelector('[aria-label="Visual automation editor"] h1')?.textContent).toBe('Morning lights'))
  expect(callApi).not.toHaveBeenCalled()
  // A draft is not saved until the editor saves it.
  expect(fixture.calls.filter((c: any) => c.request?.operation === 'save' || c.request?.operation === 'delete')).toEqual([])
})

it('deletes visual documents with their listed revision and preserves a conflict for review', async () => {
  const { root, fixture, callApi } = setup()
  await withSaved(fixture, ['motion', 'Hall motion'])
  await vi.waitFor(() => expect(root.textContent).toContain('Hall motion'))
  fixture.controls.deleteConflict = true
  root.querySelector<HTMLButtonElement>('[aria-label="Actions for Hall motion"]')!.click()
  ;[...root.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].find(b => b.textContent === 'Delete')!.click()
  const deleteButton = () => [...root.querySelectorAll<HTMLButtonElement>('dialog button')].find(b => b.textContent?.trim() === 'Delete')!
  deleteButton().click()
  await vi.waitFor(() => expect(root.querySelector('dialog [role="alert"]')?.textContent).toContain('Changed elsewhere'))
  expect(fixture.documents.has('motion')).toBe(true)
  expect(callApi).not.toHaveBeenCalled()
  expect(fixture.calls.find(c => c.request?.operation === 'delete').request).toEqual({ operation: 'delete', id: 'motion', expectedRevision: 7 })
  fixture.controls.deleteConflict = false
  deleteButton().click()
  await vi.waitFor(() => expect(root.querySelector('a[href="/visual/motion"]')).toBeNull())
})

it('refreshes symbols once, reports progress and invalidates entity suggestions', async () => {
  let finish!: (value: unknown) => void
  const refresh = vi.fn(() => new Promise(resolve => { finish = resolve }))
  const { root } = setup(true, refresh)
  client.setQueryData(['hass-entities'], { entities: [{ entity_id: 'light.old' }], areas: [], devices: [], floors: [], labels: [] })
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
  const { root } = setup(true, refresh)
  const button = [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent === 'Refresh entity IDs')!
  expect(button).toBeDefined(); button.click()
  await vi.waitFor(() => expect(root.textContent).toContain('Could not refresh entity IDs'))
  expect(button.disabled).toBe(false); button.click()
  await vi.waitFor(() => expect(root.textContent).toContain('Entity IDs refreshed'))
})
it('hides entity refresh from non-administrators', () => {
  const { root } = setup(false)
  expect(root.textContent).not.toContain('Refresh entity IDs')
})
