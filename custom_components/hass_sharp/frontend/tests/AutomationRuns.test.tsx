import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { createSignal } from 'solid-js'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { AutomationRuns } from '../src/components/AutomationRuns'

let dispose: () => void
let client: QueryClient
afterEach(() => { dispose?.(); client?.clear(); document.body.innerHTML = '' })
const record = (overrides = {}) => ({
  runId: 'run-1', automationId: 'automation-1', revision: 'revision-1', action: 'Home.Lights::Execute',
  cause: 'Manual', details: null, plannedUtc: null, receivedUtc: '2026-11-12T01:00:00Z',
  startUtc: '2026-11-12T01:00:01Z', endUtc: '2026-11-12T01:00:02Z', durationMs: 1000,
  status: 'Succeeded', error: null, ...overrides,
})
function setup(callWS: (message: any) => Promise<any>, admin = true) {
  const root = document.createElement('div'); document.body.append(root)
  client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const [id, setId] = createSignal('automation-1')
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{ callWS, user: { is_admin: admin } } as any}>
    <AutomationRuns automationId={id()} classes={[{ name: 'Home.Lights', methods: ['Execute', 'Other'] }, { name: 'Home.Heating', methods: ['Heat'] }]} />
  </HassCtx.Provider></QueryClientProvider>, root)
  return { root, setId }
}
const button = (root: HTMLElement, text: string) => [...root.querySelectorAll('button')].find(b => b.textContent?.includes(text))!

it('uses the saved identity and selected class/action; accepted dispatch is not success', async () => {
  let accept!: (value: any) => void
  const calls: any[] = []
  const { root } = setup(async message => {
    calls.push(message)
    if (message.type === 'hass_sharp/run_now') return new Promise(resolve => { accept = resolve })
    return { version: 1, offset: 0, limit: 20, runs: [] }
  })
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
  const select = root.querySelector('select')!
  select.selectedIndex = 2; select.dispatchEvent(new Event('change', { bubbles: true }))
  button(root, 'Run now').click()
  await vi.waitFor(() => expect(button(root, 'Requesting')).toBeDefined())
  expect(button(root, 'Requesting').disabled).toBe(true)
  expect(calls.at(-1)).toEqual({ type: 'hass_sharp/run_now', automationId: 'automation-1', className: 'Home.Heating', method: 'Heat' })
  accept({ version: 1, runId: 'run-accepted' })
  await vi.waitFor(() => expect(root.textContent).toContain('Request accepted'))
  expect(root.textContent).toContain('run-accepted')
  expect(root.textContent).not.toContain('Succeeded')
  expect(root.textContent).toContain('last saved version')
})

it('shows history loading, paginates without claiming a total, and returns to newest', async () => {
  const calls: any[] = []
  let read!: (value: any) => void
  const { root } = setup(async message => {
    calls.push(message)
    if (message.offset === 0 && calls.length === 1) return new Promise(resolve => { read = resolve })
    return { version: 1, offset: message.offset, limit: 20, runs: message.offset ? [] : [record()] }
  })
  expect(root.textContent).toContain('Loading run history')
  read({ version: 1, offset: 0, limit: 20, runs: Array.from({ length: 20 }, (_, i) => record({ runId: `run-${i}` })) })
  await vi.waitFor(() => expect(button(root, 'Older').disabled).toBe(false))
  button(root, 'Older').click()
  await vi.waitFor(() => expect(root.textContent).toContain('No more runs'))
  expect(calls.at(-1)).toEqual({ type: 'hass_sharp/run_history', automationId: 'automation-1', offset: 20, limit: 20 })
  button(root, 'Newest').click()
  await vi.waitFor(() => expect(root.querySelector('time')).not.toBeNull())
})

it('renders all statuses, causes, timing and bounded plain-text error details', async () => {
  const statuses = ['Pending', 'Running', 'Succeeded', 'Skipped', 'Cancelled', 'Failed', 'Interrupted']
  const { root } = setup(async () => ({ version: 1, offset: 0, limit: 20, runs: statuses.map((status, i) => record({
    runId: `run-${i}`, status, cause: i === 0 ? 'Entity' : i === 1 ? 'Schedule' : 'Manual',
    details: i === 0 ? 'sensor.kitchen' : null, plannedUtc: '2026-11-12T00:59:00Z',
    error: status === 'Failed' ? '<script>device failed</script>\nline two' : null,
  })) }))
  await vi.waitFor(() => expect(root.textContent).toContain('Interrupted'))
  for (const status of statuses) expect(root.textContent).toContain(status)
  expect(root.textContent).toContain('sensor.kitchen')
  expect(root.textContent).toContain('Time')
  expect(root.textContent).toContain('Manual')
  expect(root.textContent).toContain('1 s')
  expect(root.textContent).toContain('<script>device failed</script>')
  expect(root.querySelector('script')).toBeNull()
  expect(root.textContent).toContain('Planned')
  expect(root.textContent).toContain('Received')
  expect(root.textContent).toContain('Started')
  expect(root.textContent).toContain('Ended')
  expect(root.querySelector('time')?.dateTime).toBe('2026-11-12T01:00:00Z')
})

it('reports dispatch rejection and history failure separately and can retry reads', async () => {
  let failing = true
  const { root } = setup(async message => {
    if (message.type === 'hass_sharp/run_now') throw { code: 'run_failed', message: 'Manual invocation disabled' }
    if (failing) throw { code: 'history_failed', message: 'History unavailable' }
    return { version: 1, offset: 0, limit: 20, runs: [] }
  })
  await vi.waitFor(() => expect(root.textContent).toContain('History unavailable'))
  button(root, 'Run now').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Manual invocation disabled'))
  expect(root.textContent).not.toContain('Request accepted')
  failing = false; button(root, 'Try again').click()
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
})

it('refreshes actual results after accepted dispatch and clears selection feedback for a new automation', async () => {
  let ran = false
  const { root, setId } = setup(async message => {
    if (message.type === 'hass_sharp/run_now') { ran = true; return { version: 1, runId: 'manual-result' } }
    return { version: 1, offset: message.offset, limit: 20, runs: ran && message.automationId === 'automation-1' ? [record({ runId: 'manual-result', status: 'Skipped' })] : [] }
  })
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
  button(root, 'Run now').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Skipped'))
  setId('automation-2')
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
  expect(root.textContent).not.toContain('manual-result')
})

it('does not call administrator-only commands for non-admin users', async () => {
  const callWS = vi.fn(async () => ({ version: 1, offset: 0, limit: 20, runs: [] }))
  const { root } = setup(callWS, false)
  await vi.waitFor(() => expect(root.textContent).toContain('Administrator'))
  expect(callWS).not.toHaveBeenCalled()
  expect(root.querySelector('button')).toBeNull()
})

it('polls an active run until its real terminal result arrives', async () => {
  vi.useFakeTimers()
  let reads = 0
  try {
    const { root } = setup(async () => ({ version: 1, offset: 0, limit: 20,
      runs: [record({ status: ++reads === 1 ? 'Running' : 'Failed', error: reads === 1 ? null : 'Action threw' })],
    }))
    await vi.advanceTimersByTimeAsync(1)
    expect(root.textContent).toContain('Running')
    await vi.advanceTimersByTimeAsync(2000)
    expect(root.textContent).toContain('Failed')
    expect(root.textContent).toContain('Action threw')
    const stoppedAt = reads
    await vi.advanceTimersByTimeAsync(6000)
    expect(reads).toBe(stoppedAt)
  } finally { vi.useRealTimers() }
})

it('does not let a late history response from another automation replace the current identity', async () => {
  let oldResponse!: (value: any) => void
  const { root, setId } = setup(async message => message.automationId === 'automation-1'
    ? new Promise(resolve => { oldResponse = resolve })
    : { version: 1, offset: 0, limit: 20, runs: [] })
  setId('automation-2')
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
  oldResponse({ version: 1, offset: 0, limit: 20, runs: [record({ error: 'Wrong automation details' })] })
  await Promise.resolve()
  expect(root.textContent).not.toContain('Wrong automation details')
})

it('returns from an older page to the accepted manual result', async () => {
  let dispatched = false
  const calls: any[] = []
  const { root } = setup(async message => {
    calls.push(message)
    if (message.type === 'hass_sharp/run_now') { dispatched = true; return { version: 1, runId: 'latest-manual' } }
    return { version: 1, offset: message.offset, limit: 20, runs: dispatched ? [record({ runId: 'latest-manual', status: 'Cancelled' })] : Array.from({ length: 20 }, (_, i) => record({ runId: `page-${message.offset}-${i}` })) }
  })
  await vi.waitFor(() => expect(button(root, 'Older').disabled).toBe(false))
  button(root, 'Older').click()
  await vi.waitFor(() => expect(calls.at(-1)?.offset).toBe(20))
  button(root, 'Run now').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Cancelled'))
  expect(calls.at(-1)).toEqual({ type: 'hass_sharp/run_history', automationId: 'automation-1', offset: 0, limit: 20 })
  expect(button(root, 'Newest').disabled).toBe(true)
})
