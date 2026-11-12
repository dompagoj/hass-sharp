import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { EntityAutocomplete } from '../src/components/EntityAutocomplete'

let dispose: () => void
let client: QueryClient
afterEach(() => { dispose?.(); client?.clear(); document.body.innerHTML = '' })
function setup() {
  const host = document.createElement('div'); document.body.append(host)
  let entities = [{ entity_id: 'light.hall', friendly_name: 'Hall Lamp' }, { entity_id: 'sensor.offline', friendly_name: 'Offline Meter' }]
  let fail = false
  const callWS = vi.fn(async () => { if (fail) throw new Error('offline'); return { entities } })
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const [value, setValue] = createSignal('manual.initial')
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{ callWS } as any}>
    <EntityAutocomplete value={value()} onChange={setValue} />
  </HassCtx.Provider></QueryClientProvider>, host)
  const input = host.querySelector<HTMLInputElement>('input')!
  const type = (text: string) => { input.value = text; input.dispatchEvent(new Event('input', { bubbles: true })) }
  const key = (key: string) => input.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
  return { host, input, type, key, value, callWS, setEntities: (next: typeof entities) => entities = next, fail: () => fail = true, recover: () => fail = false }
}
it('loads live IDs, searches friendly names case-insensitively and selects exact IDs by keyboard', async () => {
  const s = setup()
  await vi.waitFor(() => expect(s.callWS).toHaveBeenCalledWith({ type: 'hass_sharp/get_entities' }))
  s.input.focus(); s.type('OFFLINE')
  await vi.waitFor(() => expect(s.host.querySelectorAll('[role="option"]')).toHaveLength(1))
  expect(s.host.textContent).toContain('Offline Meter')
  s.key('ArrowDown'); s.key('Enter')
  expect(s.value()).toBe('sensor.offline')
  expect(s.input.getAttribute('aria-expanded')).toBe('false')
})
it('keeps manual IDs through empty results, dismissal, failures and retry', async () => {
  const s = setup(); s.input.focus(); s.type('manual.custom')
  await vi.waitFor(() => expect(s.host.textContent).toContain('No matching entities'))
  s.key('Escape'); expect(s.value()).toBe('manual.custom')
  s.input.blur(); s.fail(); s.input.focus()
  await vi.waitFor(() => expect(s.host.querySelector('[role="alert"]')).not.toBeNull())
  expect(s.value()).toBe('manual.custom')
  s.recover(); s.host.querySelector<HTMLButtonElement>('button')!.click()
  await vi.waitFor(() => expect(s.host.querySelector('[role="alert"]')).toBeNull())
  expect(s.value()).toBe('manual.custom')
})
it('refreshes when reopened and after cache invalidation, without replacing typed values', async () => {
  const s = setup(); s.input.focus(); s.type('')
  await vi.waitFor(() => expect(s.host.querySelectorAll('[role="option"]')).toHaveLength(2))
  s.key('Escape'); s.input.blur()
  s.setEntities([{ entity_id: 'light.new', friendly_name: 'New lamp' }]); s.input.focus()
  await vi.waitFor(() => expect(s.host.textContent).toContain('light.new'))
  s.setEntities([{ entity_id: 'light.refreshed', friendly_name: 'Refreshed' }])
  await client.invalidateQueries({ queryKey: ['hass-entities'] })
  await vi.waitFor(() => expect(s.host.textContent).toContain('light.refreshed'))
  expect(s.value()).toBe('')
  document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }))
  expect(s.input.getAttribute('aria-expanded')).toBe('false')
})
