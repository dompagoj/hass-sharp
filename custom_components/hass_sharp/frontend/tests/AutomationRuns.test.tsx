import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { createSignal } from 'solid-js'
import { createMemoryHistory, MemoryRouter, Route } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { AutomationRuns } from '../src/components/AutomationRuns'
import { dispatchRun, forgetAcceptedRun, runHistoryInterval } from '../src/components/runDispatch'
import { definitionNames, summarizeTrigger } from '../src/components/TriggerSummary'
import { flowMethod, nodeCatalog, type Graph, type VisualCatalogSnapshot } from '../src/visual/contract'
import { page, run as record, triggers, v1Run } from './run-fixture'
import { dateText, lastRunText, relativeTime } from '../src/automations/model'
import type { HomeAutomation } from '../src/visual/api'

let dispose: () => void
let client: QueryClient
afterEach(() => { dispose?.(); client?.clear(); document.body.innerHTML = '' })
function setup(callWS: (message: any) => Promise<any>, admin = true) {
  const root = document.createElement('div'); document.body.append(root)
  client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const [id, setId] = createSignal('automation-1')
  const history = createMemoryHistory()
  history.set({ value: '/visual/heating' })
  const Panel = () => <AutomationRuns automationId={id()} />
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{ callWS, user: { is_admin: admin } } as any}>
    <MemoryRouter history={history}><Route path="*" component={Panel} /></MemoryRouter>
  </HassCtx.Provider></QueryClientProvider>, root)
  // Runs start elsewhere (the visual editor's top bar); this is the same shared dispatch.
  const run = () => dispatchRun({ callWS } as any, id(), { className: 'VisualAutomation', method: 'Run_74' }, client)
  return { root, setId, run }
}
const button = (root: HTMLElement, text: string) => [...root.querySelectorAll('button')].find(b => b.textContent?.includes(text))!
const listText = (root: HTMLElement) => root.querySelector('.hs-run-list')?.textContent ?? ''
const rows = (root: HTMLElement) => [...root.querySelectorAll<HTMLLIElement>('.hs-run-row')]
const toggle = (row: HTMLElement) => row.querySelector<HTMLButtonElement>('.hs-run-toggle')!
const statusSelect = (root: HTMLElement) => [...root.querySelectorAll('select')].find(select => select.textContent?.includes('All statuses'))!

it('polls for a run accepted elsewhere without treating acceptance as success', async () => {
  let accept!: (value: any) => void
  const calls: any[] = []
  const { root, run } = setup(async message => {
    calls.push(message)
    if (message.type === 'hass_sharp/run_now') return new Promise(resolve => { accept = resolve })
    return page([])
  })
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
  expect(root.querySelector('select')!.textContent).toContain('All statuses')
  const dispatched = run()
  expect(calls.at(-1)).toEqual({ type: 'hass_sharp/run_now', automationId: 'automation-1', className: 'VisualAutomation', method: 'Run_74' })
  const reads = calls.length
  accept({ version: 1, runId: 'run-accepted' }); await dispatched
  await vi.waitFor(() => expect(calls.length).toBeGreaterThan(reads))
  expect(calls.at(-1)).toMatchObject({ type: 'hass_sharp/run_history', automationId: 'automation-1', offset: 0 })
  expect(listText(root)).not.toContain('Succeeded')
  expect(root.textContent).toContain('No runs yet')
})

it('shows history loading, paginates without claiming a total, and returns to newest', async () => {
  const calls: any[] = []
  let read!: (value: any) => void
  const { root } = setup(async message => {
    calls.push(message)
    if (message.offset === 0 && calls.length === 1) return new Promise(resolve => { read = resolve })
    return page(message.offset ? [] : [record()], message.offset)
  })
  expect(root.textContent).toContain('Loading run history')
  read(page(Array.from({ length: 20 }, (_, i) => record({ runId: `run-${i}` }))))
  await vi.waitFor(() => expect(button(root, 'Older').disabled).toBe(false))
  button(root, 'Older').click()
  await vi.waitFor(() => expect(root.textContent).toContain('No more runs'))
  expect(calls.at(-1)).toEqual({ type: 'hass_sharp/run_history', automationId: 'automation-1', offset: 20, limit: 20 })
  button(root, 'Newest').click()
  await vi.waitFor(() => expect(root.querySelector('time')).not.toBeNull())
})

it('renders one compact row per run with label, cause, trigger, relative time and duration', async () => {
  vi.useFakeTimers({ now: new Date('2026-11-12T01:05:00Z'), toFake: ['Date'] })
  try {
    const { root } = setup(async () => page([record({ status: 'Cancelled', error: 'Restarted by a newer trigger' })]))
    await vi.waitFor(() => expect(rows(root)).toHaveLength(1))
    const row = rows(root)[0]
    expect(row.querySelector('.hs-run-label')!.textContent).toBe('VTRV stays in heat mode for 30 minutes')
    expect(row.textContent).not.toContain('VisualAutomation::Run_')
    expect(row.querySelector('.hs-run-cause')!.textContent).toBe('Home Assistant trigger · climate.outside_bathroom_vtrv: off → heat')
    expect(row.querySelector('.hs-run-status')!.textContent).toBe('Cancelled')
    expect(row.querySelector('.hs-run-reason')!.textContent).toBe('Restarted by a newer trigger')
    const time = row.querySelector('time')!
    expect(time.textContent).toBe('5 minutes ago')
    expect(time.dateTime).toBe('2026-11-12T01:00:00Z')
    expect(time.title).toBe(new Date('2026-11-12T01:00:00Z').toLocaleString())
    expect(row.querySelector('.hs-run-duration')!.textContent).toBe('1 s')
    // Details are on demand: no raw JSON or metadata until the row is expanded.
    expect(row.querySelector('.hs-run-body')).toBeNull()
    expect(row.textContent).not.toContain('run-1')
  } finally { vi.useRealTimers() }
})

it('says a run long ago on its date: "Ran on 3 November 2026 · 32 ms", "Running since 3 November 2026"', () => {
  const now = new Date(2026, 11, 20, 9, 0).getTime(), then = new Date(2026, 10, 3, 9, 0).toISOString()
  const automation = (status: 'Succeeded' | 'Running') => ({
    id: 'a', revision: 1, automationId: 'a', enabled: true, currentRevisionActive: true, outline: { triggers: [], nodes: [], edges: [] },
    recentRuns: [{ runId: 'r', status, startUtc: then, durationMs: status === 'Running' ? null : 32 }], runsToday: 0, failedLastDay: 0, lastRunUtc: then,
  }) as unknown as HomeAutomation
  expect(lastRunText(automation('Succeeded'), now)).toBe(`Ran on ${dateText(then)} · 32 ms`)
  expect(lastRunText(automation('Running'), now)).toBe(`Running since ${dateText(then)}`)
})

it('says yesterday only for the calendar day before today, in the run list too', async () => {
  // Local times, so the test holds in any time zone.
  const now = new Date(2026, 10, 12, 9, 0).getTime()
  const at = (day: number, hour: number, minute = 0) => new Date(2026, 10, day, hour, minute).toISOString()
  expect(relativeTime(at(12, 8, 30), now)).toBe('30 minutes ago')
  expect(relativeTime(at(12, 1), now)).toBe('8 hours ago')
  expect(relativeTime(at(11, 20), now)).toBe('13 hours ago')
  // 23 hours ago is yesterday morning; 30 hours ago (03:00 two days back) is not yesterday.
  expect(relativeTime(at(11, 10), now)).toBe('yesterday')
  expect(relativeTime(at(11, 0, 30), now)).toBe('yesterday')
  expect(relativeTime(at(10, 3), now)).toBe('2 days ago')
  // Late in the evening, 23 hours ago is still today.
  expect(relativeTime(new Date(2026, 10, 12, 0, 30).toISOString(), new Date(2026, 10, 12, 23, 30).getTime())).toBe('23 hours ago')
  vi.useFakeTimers({ now, toFake: ['Date'] })
  try {
    const { root } = setup(async () => page([record({ receivedUtc: at(10, 3) })]))
    await vi.waitFor(() => expect(rows(root)).toHaveLength(1))
    expect(rows(root)[0].querySelector('time')!.textContent).toBe('2 days ago')
  } finally { vi.useRealTimers() }
})

it('counts calendar days across a daylight saving change (a 23- or 25-hour day)', () => {
  const zone = process.env.TZ
  process.env.TZ = 'Europe/Zagreb'
  try {
    // The zone took effect: winter and summer offsets differ.
    expect(new Date(2026, 0, 1).getTimezoneOffset()).not.toBe(new Date(2026, 6, 1).getTimezoneOffset())
    // Spring forward (29 March 2026): the day before is 23 hours long.
    const spring = new Date(2026, 2, 30, 10, 0).getTime()
    expect(relativeTime(new Date(2026, 2, 29, 10, 0).toISOString(), spring)).toBe('yesterday')
    expect(relativeTime(new Date(2026, 2, 28, 23, 30).toISOString(), spring)).toBe('2 days ago')
    expect(relativeTime(new Date(2026, 2, 29, 0, 10).toISOString(), new Date(2026, 2, 29, 23, 50).getTime())).toBe('23 hours ago')
    // Fall back (25 October 2026): the day before is 25 hours long.
    const autumn = new Date(2026, 9, 26, 0, 5).getTime()
    expect(relativeTime(new Date(2026, 9, 25, 0, 10).toISOString(), autumn)).toBe('yesterday')
    expect(relativeTime(new Date(2026, 9, 24, 23, 50).toISOString(), autumn)).toBe('2 days ago')
  } finally {
    if (zone === undefined) delete process.env.TZ
    else process.env.TZ = zone
  }
})

it('expands a row with a real button to show the reason, trigger summary, metadata and raw values', async () => {
  const writeText = vi.fn(async () => {})
  Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
  try {
    const { root } = setup(async () => page([record({ status: 'Cancelled', error: 'Restarted by a newer trigger', plannedUtc: '2026-11-12T00:59:00Z' })]))
    await vi.waitFor(() => expect(rows(root)).toHaveLength(1))
    const control = toggle(rows(root)[0])
    expect(control.tagName).toBe('BUTTON')
    expect(control.getAttribute('aria-expanded')).toBe('false')
    control.click()
    await vi.waitFor(() => expect(control.getAttribute('aria-expanded')).toBe('true'))
    const body = rows(root)[0].querySelector<HTMLElement>('.hs-run-body')!
    expect(control.getAttribute('aria-controls')).toBe(body.id)
    expect(body.textContent).toContain('Cancel reason')
    expect(body.querySelector('pre')!.textContent).toBe('Restarted by a newer trigger')
    expect(body.querySelector('.hs-trigger-headline')!.textContent).toBe('climate.outside_bathroom_vtrv: off → heat')
    for (const text of ['Platform', 'state', 'Entity', 'From', 'To', 'Run ID', 'run-1', 'Graph revision', '7', 'Automation revision', 'revision-1', 'Planned', 'Received', 'Started', 'Ended', 'Duration'])
      expect(body.textContent).toContain(text)
    const raw = body.querySelector<HTMLDetailsElement>('.hs-run-raw')!
    expect(raw.open).toBe(false)
    expect(raw.querySelector('.hs-json-tree')!.textContent).toContain('"VTRV stays in heat mode for 30 minutes"')
    button(body, 'Copy').click()
    await vi.waitFor(() => expect(button(body, 'Copied')).toBeDefined())
    expect(writeText).toHaveBeenCalledWith('run-1')
    control.click()
    await vi.waitFor(() => expect(rows(root)[0].querySelector('.hs-run-body')).toBeNull())
    expect(control.getAttribute('aria-expanded')).toBe('false')
  } finally { delete (navigator as any).clipboard }
})

it('keeps a row expanded while polling replaces its record', async () => {
  vi.useFakeTimers()
  // Seconds after the record's start: a run this recent is read again every 2 s.
  vi.setSystemTime('2026-11-12T01:00:05Z')
  let reads = 0
  try {
    const { root } = setup(async () => page([record({ status: ++reads === 1 ? 'Running' : 'Succeeded', endUtc: reads === 1 ? null : '2026-11-12T01:00:02Z' })]))
    await vi.advanceTimersByTimeAsync(1)
    toggle(rows(root)[0]).click()
    await vi.advanceTimersByTimeAsync(2000)
    expect(rows(root)[0].querySelector('.hs-run-status')!.textContent).toBe('Succeeded')
    expect(rows(root)[0].querySelector('.hs-run-body')).not.toBeNull()
  } finally { vi.useRealTimers() }
})

it('summarises every trigger kind in human words', () => {
  expect(summarizeTrigger(triggers.haState)).toBe('climate.outside_bathroom_vtrv: off → heat')
  expect(summarizeTrigger(triggers.haEvent)).toBe('Event doorbell_pressed')
  expect(summarizeTrigger(triggers.haTime)).toBe('Time')
  expect(summarizeTrigger({ kind: 'ha', platform: 'sun' })).toBe('Sun trigger')
  expect(summarizeTrigger({ kind: 'ha' })).toBe('Home Assistant trigger')
  expect(summarizeTrigger(triggers.entity)).toBe('binary_sensor.hall_motion: off → on')
  expect(summarizeTrigger({ kind: 'entity', entityId: 'light.hall', to: 'on' })).toBe('light.hall: — → on')
  expect(summarizeTrigger({ kind: 'entity', entityId: 'light.hall' })).toBe('light.hall')
  expect(summarizeTrigger({ kind: 'entity', entityId: 'light.hall', from: null, to: null } as any)).toBe('light.hall')
  expect(summarizeTrigger(triggers.schedule)).toBe(`Scheduled for ${new Date('2026-11-12T06:30:00Z').toLocaleString()}`)
  expect(summarizeTrigger({ kind: 'schedule', scheduleId: 'daily-0' })).toBe('Scheduled run')
  expect(summarizeTrigger(triggers.manual)).toBe('Started manually')
  expect(summarizeTrigger(triggers.startup)).toBe('Started with Home Assistant')
})

it('labels every cause and shows every status as text', async () => {
  const statuses = ['Pending', 'Running', 'Succeeded', 'Skipped', 'Cancelled', 'Failed', 'Interrupted', 'Stopped'] as const
  const kinds = [
    ['HomeAssistantTrigger', triggers.haState, 'Home Assistant trigger'], ['Entity', triggers.entity, 'Entity change'],
    ['Schedule', triggers.schedule, 'Schedule'], ['Manual', triggers.manual, 'Manual run'], ['Startup', triggers.startup, 'Startup'],
  ] as const
  const { root } = setup(async () => page(statuses.map((status, i) => {
    const [cause, trigger] = kinds[i % kinds.length]
    return record({ runId: `run-${i}`, status, cause, trigger, error: status === 'Failed' ? '<script>device failed</script>\nline two' : null })
  })))
  await vi.waitFor(() => expect(rows(root)).toHaveLength(statuses.length))
  rows(root).forEach((row, i) => {
    expect(row.querySelector('.hs-run-status')!.textContent).toBe(statuses[i])
    expect(row.querySelector('.hs-run-status')!.className).toContain(`hs-run-status--${statuses[i].toLowerCase()}`)
    expect(row.querySelector('.hs-run-cause')!.textContent).toContain(kinds[i % kinds.length][2])
  })
  const failed = rows(root)[5]
  expect(failed.querySelector('.hs-run-reason')!.textContent).toBe('<script>device failed</script>')
  toggle(failed).click()
  await vi.waitFor(() => expect(failed.querySelector('pre')?.textContent).toBe('<script>device failed</script>\nline two'))
  expect(failed.textContent).toContain('Error')
  expect(root.querySelector('script')).toBeNull()
})

it('sends the server-side status filter and resets to the first page', async () => {
  const calls: any[] = []
  const { root } = setup(async message => {
    calls.push(message)
    if (message.status) return page([record({ runId: 'failed-1', status: 'Failed', error: 'Boom' })], message.offset)
    return page(Array.from({ length: 20 }, (_, i) => record({ runId: `run-${i}` })), message.offset)
  })
  await vi.waitFor(() => expect(button(root, 'Older').disabled).toBe(false))
  button(root, 'Older').click()
  await vi.waitFor(() => expect(calls.at(-1)?.offset).toBe(20))
  const select = statusSelect(root)
  select.value = 'Failed'; select.dispatchEvent(new Event('change', { bubbles: true }))
  await vi.waitFor(() => expect(rows(root)).toHaveLength(1))
  expect(calls.at(-1)).toEqual({ type: 'hass_sharp/run_history', automationId: 'automation-1', offset: 0, limit: 20, status: ['Failed'] })
  select.value = ''; select.dispatchEvent(new Event('change', { bubbles: true }))
  await vi.waitFor(() => expect(rows(root)).toHaveLength(20))
  expect(calls.at(-1)).toEqual({ type: 'hass_sharp/run_history', automationId: 'automation-1', offset: 0, limit: 20 })
})

it('explains an empty filtered page and clears the filter after a manual dispatch', async () => {
  const calls: any[] = []
  const { root, run } = setup(async message => {
    calls.push(message)
    if (message.type === 'hass_sharp/run_now') return { version: 1, runId: 'manual-1' }
    return page(message.status ? [] : [record({ runId: 'manual-1', status: 'Succeeded' })])
  })
  await vi.waitFor(() => expect(rows(root)).toHaveLength(1))
  const select = statusSelect(root)
  select.value = 'Stopped'; select.dispatchEvent(new Event('change', { bubbles: true }))
  await vi.waitFor(() => expect(root.textContent).toContain('No stopped runs'))
  await run()
  await vi.waitFor(() => expect(rows(root)).toHaveLength(1))
  expect(statusSelect(root).value).toBe('')
  expect(calls.at(-1)).toEqual({ type: 'hass_sharp/run_history', automationId: 'automation-1', offset: 0, limit: 20 })
})

it('links every run of a visual automation to its trace view, named by whether values were recorded', async () => {
  const { root } = setup(async () => page([
    record({ runId: 'with trace', visualId: 'heating/1', hasTrace: true }),
    record({ runId: 'no-trace', visualId: 'heating/1', hasTrace: false }),
    record({ runId: 'code', visualId: null, hasTrace: false }),
  ]))
  await vi.waitFor(() => expect(rows(root)).toHaveLength(3))
  const link = rows(root)[0].querySelector<HTMLAnchorElement>('a.hs-run-trace')!
  expect(link.textContent).toBe('View trace')
  expect(link.getAttribute('href')).toBe('/visual/heating%2F1/runs/with%20trace')
  const graphOnly = rows(root)[1].querySelector<HTMLAnchorElement>('a.hs-run-trace')!
  expect(graphOnly.textContent).toBe('View run')
  expect(graphOnly.getAttribute('href')).toBe('/visual/heating%2F1/runs/no-trace')
  expect(rows(root)[2].querySelector('a')).toBeNull()
})

it('still renders v1-shaped rows from the current backend', async () => {
  const { root } = setup(async () => ({ version: 1, offset: 0, limit: 20, runs: [v1Run({ status: 'Cancelled', error: 'Invocation cancelled' })] }))
  await vi.waitFor(() => expect(rows(root)).toHaveLength(1))
  const row = rows(root)[0]
  expect(row.querySelector('.hs-run-label')!.textContent).toBe('Home.Lights::Execute')
  expect(row.querySelector('.hs-run-cause')!.textContent).toBe('Manual run')
  expect(row.querySelector('.hs-run-reason')!.textContent).toBe('Invocation cancelled')
  expect(row.querySelector('a')).toBeNull()
  // The v1 raw payload string is not dumped into the list.
  expect(row.textContent).not.toContain('"platform"')
  toggle(row).click()
  await vi.waitFor(() => expect(row.textContent).toContain('No trigger summary was recorded'))
  expect(row.textContent).not.toContain('Graph revision')
})

it('reports a history failure and can retry reads', async () => {
  let failing = true
  const { root } = setup(async () => {
    if (failing) throw { code: 'history_failed', message: 'History unavailable' }
    return page([])
  })
  await vi.waitFor(() => expect(root.textContent).toContain('History unavailable'))
  failing = false; button(root, 'Try again').click()
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
})

it('refreshes actual results after accepted dispatch and forgets them for a new automation', async () => {
  let ran = false
  const { root, setId, run } = setup(async message => {
    if (message.type === 'hass_sharp/run_now') { ran = true; return { version: 1, runId: 'manual-result' } }
    return page(ran && message.automationId === 'automation-1' ? [record({ runId: 'manual-result', status: 'Skipped' })] : [], message.offset)
  })
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
  await run()
  await vi.waitFor(() => expect(listText(root)).toContain('Skipped'))
  setId('automation-2')
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
  expect(root.textContent).not.toContain('manual-result')
})

it('does not call administrator-only commands for non-admin users', async () => {
  const callWS = vi.fn(async () => page([]))
  const { root } = setup(callWS, false)
  await vi.waitFor(() => expect(root.textContent).toContain('Administrator'))
  expect(callWS).not.toHaveBeenCalled()
  expect(root.querySelector('button')).toBeNull()
})

it('polls an active run until its real terminal result arrives', async () => {
  vi.useFakeTimers()
  // Seconds after the record's start: a run this recent is read again every 2 s.
  vi.setSystemTime('2026-11-12T01:00:05Z')
  let reads = 0
  try {
    const { root } = setup(async () => page([record({ status: ++reads === 1 ? 'Running' : 'Failed', error: reads === 1 ? null : 'Action threw' })]))
    await vi.advanceTimersByTimeAsync(1)
    expect(listText(root)).toContain('Running')
    await vi.advanceTimersByTimeAsync(2000)
    expect(listText(root)).toContain('Failed')
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
    : page([]))
  setId('automation-2')
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
  oldResponse(page([record({ error: 'Wrong automation details', status: 'Failed' })]))
  await Promise.resolve()
  expect(root.textContent).not.toContain('Wrong automation details')
})

it('returns from an older page to the accepted manual result', async () => {
  let dispatched = false
  const calls: any[] = []
  const { root, run } = setup(async message => {
    calls.push(message)
    if (message.type === 'hass_sharp/run_now') { dispatched = true; return { version: 1, runId: 'latest-manual' } }
    return page(dispatched ? [record({ runId: 'latest-manual', status: 'Cancelled' })] : Array.from({ length: 20 }, (_, i) => record({ runId: `page-${message.offset}-${i}` })), message.offset)
  })
  await vi.waitFor(() => expect(button(root, 'Older').disabled).toBe(false))
  button(root, 'Older').click()
  await vi.waitFor(() => expect(calls.at(-1)?.offset).toBe(20))
  await run()
  await vi.waitFor(() => expect(listText(root)).toContain('Cancelled'))
  expect(calls.at(-1)).toEqual({ type: 'hass_sharp/run_history', automationId: 'automation-1', offset: 0, limit: 20 })
  expect(button(root, 'Newest').disabled).toBe(true)
})

it('reads the newest runs again every 2 s for a minute, then every 15 s while one has not ended, and not once none is', async () => {
  const started = Date.parse('2026-11-12T01:00:01Z')
  const running = record({ status: 'Running', endUtc: null, durationMs: null })
  expect(runHistoryInterval('automation-1', [running], started + 59_000)).toBe(2000)
  expect(runHistoryInterval('automation-1', [running], started + 61_000)).toBe(15_000)
  expect(runHistoryInterval('automation-1', [record({ status: 'Pending', startUtc: null, endUtc: null, durationMs: null })], started + 30 * 60_000)).toBe(15_000)
  expect(runHistoryInterval('automation-1', [record(), record({ runId: 'run-0', status: 'Failed' })], started)).toBe(false)
  expect(runHistoryInterval('automation-1', [], started)).toBe(false)
  // An accepted run is awaited for a minute even before it is recorded, and no longer once it has ended.
  const callWS = vi.fn(async () => ({ version: 1, runId: 'run-9' }))
  await dispatchRun({ callWS } as any, 'automation-1', { className: 'VisualAutomation', method: 'Run_74' })
  try {
    expect(runHistoryInterval('automation-1', [])).toBe(2000)
    expect(runHistoryInterval('automation-1', [record({ runId: 'run-9' })])).toBe(false)
    expect(runHistoryInterval('automation-1', [], Date.now() + 61_000)).toBe(false)
    expect(runHistoryInterval('automation-2', [])).toBe(false)
  } finally { forgetAcceptedRun('automation-1') }
})

// An entity definition's runs: a command to one of its entities, or the definition starting.
const commandTrigger = { kind: 'command', entity: 'relay', command: 'turn_on' } as const
const setupTrigger = { kind: 'setup' } as const
/** The run list as an entity definition's editor shows it: in its words, naming its entities and their commands. */
function setupDefinition(callWS: (message: any) => Promise<any>, props: Partial<Parameters<typeof AutomationRuns>[0]> = {}) {
  const root = document.createElement('div'); document.body.append(root)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const history = createMemoryHistory()
  history.set({ value: '/entities/plug' })
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{ callWS, user: { is_admin: true } } as any}>
    <MemoryRouter history={history}><Route path="*" component={() => <AutomationRuns automationId="entity:plug" {...props} />} /></MemoryRouter>
  </HassCtx.Provider></QueryClientProvider>, root)
  return { root }
}
const at = { x: 0, y: 0 }
/** An entity definition's graph of the Entity nodes `hubs`, each `[id, key, domain, name?]`, and the steps `more`. */
const hubsGraph = (name: string, hubs: [string, string, string, string?][], more: Graph['nodes'] = [], edges: Graph['edges'] = []) => ({
  version: 5, name, edges,
  nodes: [...hubs.map(([id, key, domain, named]) => ({ id, kind: 'entity', config: { key, domain, ...(named === undefined ? {} : { name: named }) }, position: at })), ...more],
  definition: { entities: [], settings: [] },
}) as unknown as Graph
/** The plug's names, from its Entity nodes: a relay named Main relay, and a power sensor with no name. */
const plugNames = definitionNames(hubsGraph('Plug', [['relay', 'relay', 'switch', 'Main relay'], ['power', 'power', 'sensor']]), nodeCatalog)!

it('summarises a command run by its entity and command, named when the definition is known, and a setup run', () => {
  // Without the definition: the key, and the command with its underscores as spaces.
  expect(summarizeTrigger(commandTrigger)).toBe('relay: turn on')
  expect(summarizeTrigger(setupTrigger)).toBe('Definition started')
  // With it: the entity's name, and the command's label from its type.
  expect(summarizeTrigger(commandTrigger, plugNames)).toBe('Main relay: Turn on')
  // An entity with no name of its own, or of a type whose commands are not described, falls back as before.
  expect(summarizeTrigger({ kind: 'command', entity: 'power', command: 'reset_energy' }, plugNames)).toBe('power: reset energy')
  expect(summarizeTrigger({ kind: 'command', entity: 'gone', command: 'turn_off' }, plugNames)).toBe('gone: turn off')
})

it('names a command run\'s command by the catalog\'s label of its type, else in words', () => {
  const catalog = { ...nodeCatalog, entityDomains: { ...nodeCatalog.entityDomains, fan: { label: 'Fan', state: {}, options: {}, commands: { turn_on: { label: 'Spin up' }, set_speed: {} } } } } as unknown as VisualCatalogSnapshot
  const fan = definitionNames(hubsGraph('Fan', [['blades', 'blades', 'fan', 'Blades']]), catalog)!
  expect(summarizeTrigger({ kind: 'command', entity: 'blades', command: 'turn_on' }, fan)).toBe('Blades: Spin up')
  expect(summarizeTrigger({ kind: 'command', entity: 'blades', command: 'set_speed' }, fan)).toBe('Blades: Set speed')
})

it('labels a definition\'s Command and Setup causes, and words its empty state and revision for the definition', async () => {
  let runs: unknown[] = []
  const { root } = setupDefinition(async () => page(runs), {
    emptyHint: 'Runs appear here when one of its entities is commanded, when it starts, or when a trigger with run history fires.',
    revisionLabel: 'Revision', triggerNames: plugNames,
    runHref: run => `/entities/${run.visualId}/runs/${run.runId}`,
  })
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
  expect(root.textContent).toContain('Runs appear here when one of its entities is commanded, when it starts, or when a trigger with run history fires.')
  expect(root.textContent).not.toContain('Run it now')
  dispose(); document.body.innerHTML = ''
  runs = [
    record({ runId: 'run-2', automationId: 'entity:plug', visualId: 'plug', cause: 'Command', trigger: commandTrigger, actionLabel: 'Main relay: Turn on' }),
    record({ runId: 'run-1', automationId: 'entity:plug', visualId: 'plug', cause: 'Setup', trigger: setupTrigger, actionLabel: 'When started' }),
  ]
  const again = setupDefinition(async () => page(runs), { revisionLabel: 'Revision', triggerNames: plugNames, runHref: run => `/entities/${run.visualId}/runs/${run.runId}` })
  await vi.waitFor(() => expect(rows(again.root)).toHaveLength(2))
  expect(rows(again.root)[0].querySelector('.hs-run-cause')!.textContent).toBe('Command · Main relay: Turn on')
  expect(rows(again.root)[1].querySelector('.hs-run-cause')!.textContent).toBe('Started · Definition started')
  expect(rows(again.root)[0].querySelector('a.hs-run-trace')!.getAttribute('href')).toBe('/entities/plug/runs/run-2')
  toggle(rows(again.root)[0]).click()
  await vi.waitFor(() => expect(rows(again.root)[0].querySelector('.hs-run-body')).not.toBeNull())
  const body = rows(again.root)[0].querySelector('.hs-run-body')!
  expect(body.querySelector('.hs-trigger-headline')!.textContent).toBe('Main relay: Turn on')
  expect([...body.querySelectorAll('dt')].map(dt => dt.textContent)).toContain('Revision')
  expect(body.textContent).not.toContain('Automation revision')
  // The trigger's own fields: the entity's key and the command, as recorded.
  expect(body.querySelector('.hs-trigger-summary')!.textContent).toContain('relay')
  expect(body.querySelector('.hs-trigger-summary')!.textContent).toContain('turn_on')
})

it('labels the two causes in an automation\'s list too, without the definition\'s names', async () => {
  const { root } = setup(async () => page([
    record({ runId: 'run-2', cause: 'Command', trigger: commandTrigger }), record({ runId: 'run-1', cause: 'Setup', trigger: setupTrigger }),
  ]))
  await vi.waitFor(() => expect(rows(root)).toHaveLength(2))
  expect(rows(root)[0].querySelector('.hs-run-cause')!.textContent).toBe('Command · relay: turn on')
  expect(rows(root)[1].querySelector('.hs-run-cause')!.textContent).toBe('Started · Definition started')
})

it('labels a frame run by its camera, named when the definition is known', async () => {
  const frameTrigger = { kind: 'frame', entity: 'front' } as const
  const cameraNames = definitionNames(hubsGraph('Doorbell', [['front', 'front', 'camera', 'Front door']]), nodeCatalog)!
  expect(summarizeTrigger(frameTrigger)).toBe('front')
  expect(summarizeTrigger(frameTrigger, cameraNames)).toBe('Front door')
  expect(summarizeTrigger({ kind: 'frame' })).toBe('Frame needed')
  const { root } = setupDefinition(async () => page([record({ runId: 'run-1', automationId: 'entity:plug', visualId: 'plug', cause: 'Frame', trigger: frameTrigger })]), { triggerNames: cameraNames })
  await vi.waitFor(() => expect(rows(root)).toHaveLength(1))
  expect(rows(root)[0].querySelector('.hs-run-cause')!.textContent).toBe('Frame needed · Front door')
})

// A root step of an entity definition with no method of its own runs under the method name generated from its id; a
// flow of an Entity node under the one generated from its id and pin.
const lensGraph = hubsGraph('Lens', [['cam', 'front', 'camera', 'Front door'], ['plug', 'relay', 'switch', 'Relay']], [
  { id: 'setup', kind: 'entity_setup', config: {}, position: at },
  { id: 'tick', kind: 'trigger', label: 'Every minute', config: { type: 'ha', trigger: { trigger: 'time_pattern', minutes: '*' } }, position: at },
  { id: 'log', kind: 'log', config: { value: { kind: 'unset' } }, position: at },
])

it('titles an entity definition\'s run by its root step when it carries that step\'s generated method name, and keeps any other name', async () => {
  const [cam, plug] = lensGraph.nodes
  const runs = [
    record({ runId: 'run-5', automationId: 'entity:lens', visualId: 'lens', cause: 'Frame', trigger: { kind: 'frame', entity: 'front' }, actionLabel: flowMethod(cam, 'frame') }),
    record({ runId: 'run-4', automationId: 'entity:lens', visualId: 'lens', cause: 'Setup', trigger: setupTrigger, actionLabel: 'Run_7365747570' }),
    record({ runId: 'run-3', automationId: 'entity:lens', visualId: 'lens', cause: 'Command', trigger: commandTrigger, actionLabel: flowMethod(plug, 'command:turn_on') }),
    record({ runId: 'run-6', automationId: 'entity:lens', visualId: 'lens', cause: 'Trigger', trigger: { kind: 'ha', platform: 'time_pattern' }, actionLabel: 'Run_7469636B' }),
    // A root no longer in the graph, and a step that is no root: as recorded.
    record({ runId: 'run-2', automationId: 'entity:lens', visualId: 'lens', cause: 'Setup', trigger: setupTrigger, actionLabel: 'Run_676F6E65' }),
    record({ runId: 'run-1', automationId: 'entity:lens', visualId: 'lens', cause: 'Setup', trigger: setupTrigger, actionLabel: 'Run_6C6F67' }),
  ]
  const { root } = setupDefinition(async () => page(runs), { triggerNames: definitionNames(lensGraph, nodeCatalog) })
  await vi.waitFor(() => expect(rows(root)).toHaveLength(6))
  // A root without a label is titled by its kind (When started); one with a label by its label (Every minute); an
  // Entity node's flow by its entity and the flow.
  expect(rows(root).map(row => row.querySelector('.hs-run-label')!.textContent)).toEqual(['Front door: When a frame is needed', 'When started', 'Relay: Turn on', 'Every minute', 'Run_676F6E65', 'Run_6C6F67'])
})

it('titles a run of an Entity node\'s flow by the entity and the flow when it carries the flow\'s method name', async () => {
  const hubGraph = { version: 5, name: 'Lamp', edges: [],
    nodes: [{ id: 'l', kind: 'entity', config: { key: 'lamp', domain: 'light', name: 'Lamp' }, position: at }],
    definition: { entities: [], settings: [] },
  } as unknown as Graph
  const runs = [
    // Run_6C_ and the hex of command:turn_on, then of state (flowMethod).
    record({ runId: 'run-2', automationId: 'entity:lamp', visualId: 'lamp', cause: 'Command', trigger: { kind: 'command', entity: 'lamp', command: 'turn_on' }, actionLabel: 'Run_6C_636F6D6D616E643A7475726E5F6F6E' }),
    record({ runId: 'run-1', automationId: 'entity:lamp', visualId: 'lamp', cause: 'Startup', trigger: { kind: 'startup' }, actionLabel: 'Run_6C_7374617465' }),
  ]
  const { root } = setupDefinition(async () => page(runs), { triggerNames: definitionNames(hubGraph, nodeCatalog) })
  await vi.waitFor(() => expect(rows(root)).toHaveLength(2))
  expect(rows(root).map(row => row.querySelector('.hs-run-label')!.textContent)).toEqual(['Lamp: Turn on', 'Lamp: State'])
})

it('keeps an automation\'s run names as recorded: its graph gives no names', async () => {
  const automation = { version: 5, name: 'Heating', edges: [], nodes: [{ id: 't1', kind: 'trigger', config: {}, position: at }] } as unknown as Graph
  expect(definitionNames(automation, nodeCatalog)).toBeUndefined()
  const { root } = setup(async () => page([record({ runId: 'run-2', actionLabel: 'Run_7431' }), record({ runId: 'run-1' })]))
  await vi.waitFor(() => expect(rows(root)).toHaveLength(2))
  expect(rows(root).map(row => row.querySelector('.hs-run-label')!.textContent)).toEqual(['Run_7431', 'VTRV stays in heat mode for 30 minutes'])
})
