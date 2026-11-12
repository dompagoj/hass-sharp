import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { MemoryRouter, Route } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { VisualAutomationView } from '../src/views/VisualAutomationView'
import { AutomationsListView } from '../src/views/AutomationsListView'

let dispose: (() => void) | undefined
let client: QueryClient
afterEach(() => { dispose?.(); client.clear(); document.body.innerHTML = '' })
const scripts = [{ fileName: 'Heating.cs', classes: [{ name: 'Home.Heating', methods: ['Start'] }] }, { fileName: 'Lights.cs', classes: [] }]
function setup(failWrites = false) {
  const root = document.createElement('div'); document.body.append(root)
  const calls: { method: string; path: string; data: unknown }[] = []
  const hass = { callApi: async (method: string, path: string, data: unknown) => {
    calls.push({ method, path, data })
    if (method === 'GET') return scripts
    if (failWrites) throw { body: { error: 'Name already exists' } }
    return { fileName: 'Warm house.cs' }
  } }
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={hass as any}>
    <MemoryRouter><Route path="/" component={AutomationsListView} /><Route path="/visual/new" component={VisualAutomationView} /></MemoryRouter>
  </HassCtx.Provider></QueryClientProvider>, root)
  return { root, calls }
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
