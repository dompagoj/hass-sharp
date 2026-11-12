import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { createMemoryHistory, MemoryRouter, Route } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { editors, resetHost } from './fake-monaco'
import { HassCtx } from '../src/context'
import { AutomationView } from '../src/views/AutomationView'

vi.mock('monaco-editor', async () => (await import('./fake-monaco')).host)
vi.mock('monaco-vim', () => ({ initVimMode: () => ({ dispose() {} }) }))
let dispose: (() => void) | undefined
let client: QueryClient
beforeEach(() => { resetHost(); localStorage.clear() })
afterEach(() => { dispose?.(); client.clear(); document.body.innerHTML = '' })
function setup(failRefresh = false, savedMethods?: string[]) {
  const root = document.createElement('div'); document.body.append(root)
  const history = createMemoryHistory()
  history.set({ value: '/automations/A.cs' })
  client = new QueryClient({ defaultOptions: { queries: { staleTime: Infinity, retry: false } } })
  client.setQueryData(['automations', 'A.cs'], { fileName: 'A.cs', source: 'source A' })
  client.setQueryData(['automations', 'B.cs'], { fileName: 'B.cs', source: 'source B' })
  const scripts = [
    { fileName: 'A.cs', automationId: 'stable-A', revision: 'rev-A', classes: [{ name: 'Home.AutomationA', methods: ['Execute'] }] },
    { fileName: 'B.cs', automationId: 'stable-B', revision: 'rev-B', classes: [] },
  ]
  client.setQueryData(['automations'], scripts)
  const hass = {
    user: { is_admin: true },
    callApi: async (method: string, path: string) => {
      if (path === 'hass-sharp/automations') return structuredClone(scripts)
      if (failRefresh) throw new Error('Disconnected')
      if (method === 'POST' && savedMethods) scripts[0].classes[0].methods = savedMethods
      return { fileName: 'A.cs', source: 'refreshed A' }
    },
    callWS: async (message: any) => message.type === 'hass_sharp/run_history' ? { version: 1, offset: 0, limit: 20, runs: [] } : message.type.endsWith('format_source') ? message.source : [],
  }
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={hass as any}>
    <MemoryRouter history={history}><Route path="/automations/:id" component={AutomationView} /></MemoryRouter>
  </HassCtx.Provider></QueryClientProvider>, root)
  return { root, history }
}
it('opens the correct model when navigating directly between cached files', async () => {
  const { history } = setup()
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  editors[0].change('unsaved A')
  history.set({ value: '/automations/B.cs' })
  await vi.waitFor(() => expect(editors.at(-1)?.model.getValue()).toBe('source B'))
  expect(editors[0].disposed).toBe(true)
})
it('keeps the same file model and its unsaved changes during background refetch', async () => {
  setup()
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  editors[0].change('unsaved A')
  await client.invalidateQueries({ queryKey: ['automations', 'A.cs'] })
  expect(editors).toHaveLength(1)
  expect(editors[0].model.getValue()).toBe('unsaved A')
})

it('keeps unsaved code visible if a background refresh fails', async () => {
  const { root } = setup(true)
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  editors[0].change('unsaved A')
  await client.invalidateQueries({ queryKey: ['automations', 'A.cs'] })
  expect(root.querySelector('[role="alert"]')).not.toBeNull()
  expect(editors[0].disposed).toBe(false)
  expect(editors[0].model.getValue()).toBe('unsaved A')
})

it('opens run controls using backend metadata and keeps unsaved source across history navigation', async () => {
  const { root } = setup()
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  editors[0].change('unsaved source')
  const runTab = root.querySelector<HTMLButtonElement>('[role="tab"][id$="runs"]')
  expect(runTab).not.toBeNull()
  runTab!.click()
  await vi.waitFor(() => expect(root.textContent).toContain('Home.AutomationA'))
  root.querySelector<HTMLButtonElement>('[role="tab"][id$="source"]')!.click()
  expect(editors).toHaveLength(1)
  expect(editors[0].model.getValue()).toBe('unsaved source')
  expect(editors[0].disposed).toBe(false)
})

it('refreshes declared actions after saving without replacing the source model', async () => {
  const { root } = setup(false, ['NewSavedAction'])
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  root.querySelector<HTMLButtonElement>('[role="tab"][id$="runs"]')!.click()
  await vi.waitFor(() => expect(root.querySelector('select')?.textContent).toContain('Execute'))
  root.querySelector<HTMLButtonElement>('[role="tab"][id$="source"]')!.click()
  editors[0].change('new saved source')
  await vi.waitFor(() => expect(root.querySelector<HTMLButtonElement>('[data-save]')!.disabled).toBe(false))
  root.querySelector<HTMLButtonElement>('[data-save]')!.click()
  await vi.waitFor(() => expect(root.querySelector('select')?.textContent).toContain('NewSavedAction'))
  expect(editors).toHaveLength(1)
  expect(editors[0].model.getValue()).toBe('new saved source')
})
