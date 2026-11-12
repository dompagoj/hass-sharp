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
function setup(failRefresh = false) {
  const root = document.createElement('div'); document.body.append(root)
  const history = createMemoryHistory()
  history.set({ value: '/automations/A.cs' })
  client = new QueryClient({ defaultOptions: { queries: { staleTime: Infinity, retry: false } } })
  client.setQueryData(['automations', 'A.cs'], { fileName: 'A.cs', source: 'source A' })
  client.setQueryData(['automations', 'B.cs'], { fileName: 'B.cs', source: 'source B' })
  const hass = {
    callApi: async () => { if (failRefresh) throw new Error('Disconnected'); return { fileName: 'A.cs', source: 'refreshed A' } },
    callWS: async (message: any) => message.type.endsWith('format_source') ? message.source : [],
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
