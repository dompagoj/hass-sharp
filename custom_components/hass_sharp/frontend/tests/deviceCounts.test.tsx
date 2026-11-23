import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { Show } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { attentionOf, createDeviceCounts, nodeAttention } from '../src/entities/deviceCounts'
import type { DeviceCounts } from '../src/entities/deviceUi'

// The "waiting" counts of the device pages: one shared query, read every 10 seconds while the page is shown, that
// keeps the counts of its last good read when one fails.

let dispose: (() => void) | undefined
let client: QueryClient | undefined
beforeEach(() => { vi.useFakeTimers() })
afterEach(() => {
  dispose?.(); dispose = undefined
  client?.clear(); client = undefined
  document.body.innerHTML = ''
  vi.useRealTimers()
  Reflect.deleteProperty(document, 'visibilityState')
})

const ok = (result: unknown) => ({ version: 1, ok: true, result })
const counts = (attention: DeviceCounts['attention']): DeviceCounts => ({ attention, stored: {} })

/** A page that shows the door definition's count, over a backend whose `counts` answers what `answer` gives. */
function mount(answer: () => unknown) {
  const requests: unknown[] = []
  const callWS = async (message: any) => {
    requests.push(structuredClone(message))
    return answer()
  }
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const host = document.createElement('div')
  document.body.append(host)
  const Page = () => {
    const shown = createDeviceCounts(() => ({ callWS }))
    return <Show when={attentionOf(shown(), 'door')}>{count => <output>{count()}</output>}</Show>
  }
  dispose = render(() => <QueryClientProvider client={client!}><Page /></QueryClientProvider>, host)
  return { host, requests, shown: () => host.querySelector('output')?.textContent }
}
const settle = () => vi.advanceTimersByTimeAsync(0)
const visibility = (state: 'hidden' | 'visible') => {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state })
  document.dispatchEvent(new Event('visibilitychange'))
}

it('requests counts every 10 seconds while visible', async () => {
  const page = mount(() => ok(counts({ door: { faces: 2 } })))
  await settle()
  expect(page.requests).toEqual([{ type: 'hass_sharp/device_ui', request: { operation: 'counts' } }])
  expect(page.shown()).toBe('2')
  await vi.advanceTimersByTimeAsync(9_999)
  expect(page.requests).toHaveLength(1)
  await vi.advanceTimersByTimeAsync(1)
  expect(page.requests).toHaveLength(2)
  await vi.advanceTimersByTimeAsync(10_000)
  expect(page.requests).toHaveLength(3)
})

it('requests no counts while the page is hidden, reads them at once when it shows again, and none once left', async () => {
  const page = mount(() => ok(counts({})))
  await settle()
  expect(page.requests).toHaveLength(1)
  visibility('hidden')
  await vi.advanceTimersByTimeAsync(60_000)
  expect(page.requests).toHaveLength(1)
  visibility('visible')
  await settle()
  expect(page.requests).toHaveLength(2)
  await vi.advanceTimersByTimeAsync(10_000)
  expect(page.requests).toHaveLength(3)
  dispose!(); dispose = undefined
  await vi.advanceTimersByTimeAsync(60_000)
  expect(page.requests).toHaveLength(3)
})

it('shows nothing for a first counts request that fails, and keeps the last good counts through a later failure', async () => {
  let answer: () => unknown = () => { throw new Error('Connection lost') }
  const page = mount(() => answer())
  await settle()
  expect(page.shown()).toBeUndefined()
  expect(page.host.textContent).toBe('')
  answer = () => ok(counts({ door: { faces: 1, plates: 2 } }))
  await vi.advanceTimersByTimeAsync(10_000)
  expect(page.shown()).toBe('3')
  // A refusal after counts were shown: they stay, with no error, and are read again at the next interval.
  answer = () => ({ version: 1, ok: false, error: { code: 'internal', detail: 'Something broke' } })
  await vi.advanceTimersByTimeAsync(10_000)
  expect(page.requests).toHaveLength(3)
  expect(page.shown()).toBe('3')
  expect(page.host.textContent).toBe('3')
  answer = () => { throw new Error('Connection lost') }
  await vi.advanceTimersByTimeAsync(10_000)
  expect(page.requests).toHaveLength(4)
  expect(page.shown()).toBe('3')
  answer = () => ok(counts({ door: { faces: 4 } }))
  await vi.advanceTimersByTimeAsync(10_000)
  expect(page.requests).toHaveLength(5)
  expect(page.shown()).toBe('4')
})

it('counts a definition\'s nodes, and nothing for a definition or node it does not list', () => {
  const shown = counts({ door: { faces: 2, plates: 3 }, garage: { car: 1 } })
  expect(attentionOf(shown, 'door')).toBe(5)
  expect(attentionOf(shown, 'garage')).toBe(1)
  expect(attentionOf(shown, 'porch')).toBe(0)
  expect(attentionOf(undefined, 'door')).toBe(0)
  expect(nodeAttention(shown, 'door', 'plates')).toBe(3)
  expect(nodeAttention(shown, 'door', 'car')).toBe(0)
  expect(nodeAttention(shown, 'porch', 'faces')).toBe(0)
  expect(nodeAttention(undefined, 'door', 'faces')).toBe(0)
  // Ids are any text: never an object's own members.
  expect(attentionOf(shown, 'constructor')).toBe(0)
  expect(attentionOf(shown, '__proto__')).toBe(0)
  expect(nodeAttention(shown, 'door', 'toString')).toBe(0)
})
