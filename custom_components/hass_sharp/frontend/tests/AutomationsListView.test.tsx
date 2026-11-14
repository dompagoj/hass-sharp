import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { MemoryRouter, Route, createMemoryHistory } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { VisualAutomationView } from '../src/views/VisualAutomationView'
import { visualFixture, type FixtureRuns } from './visual-fixture'
import { addNode, createGraph, updateNode } from '../src/visual/graph'
import { AutomationsListView } from '../src/views/AutomationsListView'
import type { RecentRun } from '../src/visual/api'
import { readFileSync } from 'node:fs'

let dispose: (() => void) | undefined
let client: QueryClient
beforeEach(() => localStorage.clear())
afterEach(() => { dispose?.(); client.clear(); document.body.innerHTML = ''; vi.restoreAllMocks() })

type Fixture = ReturnType<typeof visualFixture>
/** The list page over the in-memory API; `hold` delays the replies to the given operations until released. */
function setup(options: { admin?: boolean; refresh?: () => Promise<unknown>; prepare?: (fixture: Fixture) => void; hold?: string[] } = {}) {
  const root = document.createElement('div'); document.body.append(root)
  const fixture = visualFixture()
  options.prepare?.(fixture)
  const held: (() => void)[] = []
  const release = () => held.splice(0).forEach(go => go())
  // The list reads saved visual documents only; any REST call would be a leftover of hand-written automations.
  const callApi = vi.fn(async () => { throw new Error('unexpected REST call') })
  const callWS = async (message: any) => {
    if (message.type === 'hass_sharp/reload_entities' && options.refresh) return options.refresh()
    if (message.type === 'hass_sharp/get_entities') return { entities: [{ entity_id: 'binary_sensor.motion', name: 'Hallway motion' }, { entity_id: 'binary_sensor.door', name: 'Front door' }] }
    if (options.hold?.includes(message.request?.operation)) await new Promise<void>(go => held.push(go))
    return fixture.callWS(message)
  }
  const hass = { ...fixture, user: { is_admin: options.admin ?? true }, callWS, callApi }
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const history = createMemoryHistory(); history.set({ value: '/' })
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={hass as any}>
    <MemoryRouter history={history}><Route path="/" component={AutomationsListView} /><Route path="/visual/new" component={VisualAutomationView} /><Route path="/visual/:id" component={() => <h1 data-opened>Opened</h1>} /></MemoryRouter>
  </HassCtx.Provider></QueryClientProvider>, root)
  return { root, fixture, callApi, history, release }
}

const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString()
const run = (status: RecentRun['status'], minutes = 5, durationMs: number | null = 32): RecentRun => ({ runId: `${status}-${minutes}-${Math.random()}`, status, startUtc: minutesAgo(minutes), durationMs })
/** A saved automation: the default graph (an entity trigger on binary_sensor.motion), with more triggers when asked. */
function saved(fixture: Fixture, id: string, name: string, options: { triggers?: Record<string, unknown>[]; enabled?: boolean; active?: boolean; runs?: FixtureRuns; savedUtc?: string; manual?: boolean } = {}) {
  let graph = createGraph(name)
  if (options.manual === false) graph = updateNode(graph, 'trigger-1', { config: { ...graph.nodes[0].config, manual: false } })
  for (const [i, config] of (options.triggers ?? []).entries()) {
    graph = addNode(graph, 'trigger', `trigger-${i + 2}`)
    graph = updateNode(graph, `trigger-${i + 2}`, { config, position: { x: 60, y: 300 + i * 200 } })
  }
  fixture.documents.set(id, { version: 1, id, revision: 7, graph, source: 'server', sourceMap: [], automationId: `visual:${id}`,
    currentRevisionActive: options.active ?? true, enabled: options.enabled, savedUtc: options.savedUtc ?? null })
  if (options.runs) fixture.runs.set(id, options.runs)
}
const card = (root: Element, id: string) => root.querySelector<HTMLElement>(`[data-automation="${id}"]`)!
const cards = (root: Element) => [...root.querySelectorAll<HTMLElement>('[data-automation]')].map(c => c.dataset.automation)
const button = (root: Element, text: string) => [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text || b.getAttribute('aria-label') === text)!
const menu = (root: Element, name: string, item: string) => {
  button(root, `Actions for ${name}`).click()
  const choice = [...root.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].find(b => b.textContent === item)
  expect(choice, item).toBeDefined()
  choice!.click()
}
const type = (input: HTMLInputElement, value: string) => { input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })) }
const loaded = (root: Element) => vi.waitFor(() => expect(root.querySelector('.hs-home-grid[aria-label="Automations"], .hs-home-empty')).not.toBeNull())
const calls = (fixture: Fixture, operation: string) => fixture.calls.filter(c => c.request?.operation === operation).map(c => c.request)

it('shows each automation: name, what starts it with registry names, a picture of its graph, its runs and when it last ran', async () => {
  const runs = [...Array.from({ length: 8 }, (_, i) => run('Succeeded', 60 - i)), run('Failed', 20), run('Succeeded', 5, 32)]
  const { root, callApi } = setup({ prepare: f => {
    saved(f, 'hall', 'Hall lights', { runs: { recentRuns: runs, runsToday: 10, lastRunUtc: minutesAgo(5) } })
    saved(f, 'many', 'Many triggers', { triggers: [{ type: 'daily', time: '07:00' }, { type: 'entity', entity: 'binary_sensor.door' }, { type: 'startup' }] })
  } })
  await loaded(root)
  await vi.waitFor(() => expect(card(root, 'hall').textContent).toContain('When Hallway motion changes'))
  const hall = card(root, 'hall')
  const link = hall.querySelector<HTMLAnchorElement>('a.hs-home-card__link')!
  expect(link.getAttribute('href')).toBe('/visual/hall')
  expect(link.textContent).toBe('Hall lights')
  expect(root.querySelector(`#${link.getAttribute('aria-describedby')}`)?.textContent).toContain('When Hallway motion changes')
  // One rectangle per node, the execution wires as lines; decoration only.
  const picture = hall.querySelector('svg.hs-thumb')!
  expect(picture.getAttribute('aria-hidden')).toBe('true')
  expect(picture.querySelectorAll('rect')).toHaveLength(3)
  expect(picture.querySelectorAll('path')).toHaveLength(2)
  const dots = hall.querySelector('.hs-run-dots')!
  expect(dots.getAttribute('role')).toBe('img')
  expect(dots.getAttribute('aria-label')).toBe('Last 10 runs: 9 succeeded, 1 failed')
  expect([...dots.children].map(d => d.className.replace('hs-run-dot hs-run-dot--', '')).slice(-2)).toEqual(['failed', 'succeeded'])
  expect(hall.querySelector('.hs-home-card__last')?.textContent).toBe('Ran 5 minutes ago · 32 ms')
  expect(hall.querySelector('.hs-home-card__health')).toBeNull()
  // Several triggers: the first two, then how many more.
  const many = card(root, 'many')
  expect([...many.querySelectorAll('.hs-home-card__starts li')].map(li => li.textContent)).toEqual(['When Hallway motion changes', 'Every day at 07:00', '+2 more'])
  expect(many.querySelector('.hs-home-card__last')?.textContent).toBe('Has not run yet')
  expect(many.querySelector('.hs-run-dots')).toBeNull()
  expect(callApi).not.toHaveBeenCalled()
})

it('says when the saved version is not active, prominently, and shows a run in progress as running', async () => {
  const { root, fixture } = setup({ prepare: f => {
    saved(f, 'broken', 'Broken', { active: false, runs: { problemCount: 2 } })
    saved(f, 'plain', 'Not loaded', { active: false })
    saved(f, 'busy', 'Busy', { runs: { recentRuns: [run('Succeeded', 9), run('Running', 1, null)], runsToday: 2, lastRunUtc: minutesAgo(1) } })
  } })
  await loaded(root)
  const broken = card(root, 'broken')
  expect(broken.classList.contains('hs-home-card--attention')).toBe(true)
  expect(broken.querySelector('.hs-home-card__health')?.textContent).toBe('Not active: 2 problems')
  expect(card(root, 'plain').querySelector('.hs-home-card__health')?.textContent).toBe('Not active')
  const runNow = broken.querySelector<HTMLButtonElement>('.hs-topbar__run')!
  expect(runNow.disabled).toBe(true)
  expect(runNow.title).toBe('The saved version is not active')
  const busy = card(root, 'busy')
  expect(busy.classList.contains('hs-home-card--attention')).toBe(false)
  expect(busy.querySelector('.hs-home-card__last')?.textContent).toBe('Running since 1 minute ago')
  expect(busy.querySelector('.hs-run-dot:last-child')?.className).toContain('hs-run-dot--running')
  expect(busy.querySelector('.hs-run-dots')?.getAttribute('aria-label')).toBe('Last 2 runs: 1 succeeded, 1 running')
  // A run in progress keeps the list fresh.
  const before = calls(fixture, 'list').length
  await vi.waitFor(() => expect(calls(fixture, 'list').length).toBeGreaterThan(before), { timeout: 4000 })
})

it('runs the one manual trigger, offers a menu for several, and explains when none allows it', async () => {
  const { root, fixture } = setup({ prepare: f => {
    saved(f, 'one', 'One trigger')
    saved(f, 'two', 'Two triggers', { triggers: [{ type: 'manual', manual: true }] })
    saved(f, 'none', 'No manual', { manual: false })
  } })
  await loaded(root)
  card(root, 'one').querySelector<HTMLButtonElement>('.hs-topbar__run')!.click()
  await vi.waitFor(() => expect(fixture.calls.filter(c => c.type === 'hass_sharp/run_now')).toHaveLength(1))
  expect(fixture.calls.find(c => c.type === 'hass_sharp/run_now')).toMatchObject({ automationId: 'visual:one', className: 'VisualAutomation' })
  await vi.waitFor(() => expect(root.querySelector('.hs-home-notice[role="status"]')?.textContent).toBe('Run of One trigger started.'))
  const two = card(root, 'two').querySelector<HTMLButtonElement>('.hs-topbar__run')!
  expect(two.getAttribute('aria-haspopup')).toBe('menu')
  two.click()
  const items = card(root, 'two').querySelectorAll('[role="menuitem"]')
  expect(items).toHaveLength(2)
  expect(document.activeElement).toBe(items[0])
  const none = card(root, 'none').querySelector<HTMLButtonElement>('.hs-topbar__run')!
  expect(none.disabled).toBe(true)
  expect(none.title).toBe('No trigger allows manual runs')
  // Neither the button nor its menu navigates.
  expect(root.querySelector('[data-opened]')).toBeNull()
})

it('renames from the menu, keeping focus on the card', async () => {
  const { root, fixture } = setup({ prepare: f => saved(f, 'hall', 'Hall lights') })
  await loaded(root)
  menu(root, 'Hall lights', 'Rename')
  const field = await vi.waitFor(() => { const input = root.querySelector<HTMLInputElement>('dialog input[name="rename"]'); expect(input?.value).toBe('Hall lights'); return input! })
  type(field, 'Hallway lights')
  button(root, 'Rename').click()
  await vi.waitFor(() => expect(card(root, 'hall').querySelector('.hs-home-card__link')?.textContent).toBe('Hallway lights'))
  expect(calls(fixture, 'save')[0]).toMatchObject({ id: 'hall', expectedRevision: 7, graph: { name: 'Hallway lights' } })
  await vi.waitFor(() => expect(document.activeElement).toBe(button(root, 'Actions for Hallway lights')))
  // A failed rename says why and keeps the dialog open.
  fixture.controls.conflict = true
  menu(root, 'Hallway lights', 'Rename')
  type(await vi.waitFor(() => root.querySelector<HTMLInputElement>('dialog input[name="rename"]')!), 'Again')
  button(root, 'Rename').click()
  await vi.waitFor(() => expect(root.querySelector('dialog [role="alert"]')?.textContent).toContain('Could not rename: Changed elsewhere'))
})

it('duplicates as a new automation and moves focus to the copy; views runs on the editor’s Runs tab', async () => {
  const { root, fixture, history } = setup({ prepare: f => saved(f, 'hall', 'Hall lights') })
  await loaded(root)
  menu(root, 'Hall lights', 'Duplicate')
  await vi.waitFor(() => expect(cards(root)).toHaveLength(2))
  const save = calls(fixture, 'save')[0]
  expect(save).toMatchObject({ expectedRevision: 0, graph: { name: 'Hall lights (copy)' } })
  expect(save.id).toMatch(/^visual-[0-9a-f-]{36}$/)
  await vi.waitFor(() => expect(document.activeElement).toBe(card(root, save.id).querySelector('.hs-home-card__link')))
  expect(root.querySelector('.hs-home-notice')?.textContent).toBe('Duplicated as “Hall lights (copy)”. It is on.')
  menu(root, 'Hall lights', 'View runs')
  await vi.waitFor(() => expect(history.get()).toBe('/visual/hall?drawer=runs'))
})

it('deletes with the confirmation and its listed revision, keeps a conflict for review, and moves focus to the next card', async () => {
  const { root, fixture, callApi } = setup({ prepare: f => { saved(f, 'a', 'Alpha'); saved(f, 'b', 'Beta') } })
  await loaded(root)
  localStorage.setItem('hass-sharp:automations-view', JSON.stringify({ filter: 'all', sort: 'name' }))
  fixture.controls.deleteConflict = true
  menu(root, 'Alpha', 'Delete')
  const confirm = () => [...root.querySelectorAll<HTMLButtonElement>('dialog button')].find(b => b.textContent?.trim() === 'Delete')!
  confirm().click()
  await vi.waitFor(() => expect(root.querySelector('dialog [role="alert"]')?.textContent).toContain('Changed elsewhere'))
  expect(fixture.documents.has('a')).toBe(true)
  expect(calls(fixture, 'delete')[0]).toEqual({ operation: 'delete', id: 'a', expectedRevision: 7 })
  fixture.controls.deleteConflict = false
  confirm().click()
  await vi.waitFor(() => expect(card(root, 'a')).toBeNull())
  await vi.waitFor(() => expect(document.activeElement).toBe(card(root, 'b').querySelector('.hs-home-card__link')))
  // The last one: focus goes to New automation.
  menu(root, 'Beta', 'Delete')
  confirm().click()
  await vi.waitFor(() => expect(root.querySelector('.hs-home-empty')).not.toBeNull())
  await vi.waitFor(() => expect(document.activeElement?.classList.contains('hs-home-new')).toBe(true))
  expect(callApi).not.toHaveBeenCalled()
})

it('searches by name and by what starts it, filters, sorts, and remembers the filter and sort in this browser', async () => {
  const prepare = (f: Fixture) => {
    saved(f, 'door', 'Porch light', { triggers: [{ type: 'entity', entity: 'binary_sensor.door' }], runs: { recentRuns: [run('Failed', 2)], runsToday: 1, failedLastDay: 1, lastRunUtc: minutesAgo(2) }, savedUtc: '2026-11-01T00:00:00Z' })
    saved(f, 'heat', 'Warm house', { runs: { recentRuns: [run('Succeeded', 600)], runsToday: 0, lastRunUtc: minutesAgo(3000) }, savedUtc: '2026-11-10T00:00:00Z' })
    saved(f, 'never', 'Bedtime', { enabled: false })
  }
  const first = setup({ prepare })
  const root = first.root
  await loaded(root)
  expect(cards(root)).toEqual(['door', 'heat', 'never'])
  const search = root.querySelector<HTMLInputElement>('input[type="search"]')!
  type(search, 'front door')
  expect(cards(root)).toEqual(['door'])
  type(search, 'warm')
  expect(cards(root)).toEqual(['heat'])
  type(search, 'nothing like it')
  expect(root.querySelector('.hs-home-none h2')?.textContent).toBe('No automations match “nothing like it”')
  button(root, 'Clear').click()
  expect(search.value).toBe('')
  expect(cards(root)).toHaveLength(3)
  const chip = (label: string) => [...root.querySelectorAll<HTMLButtonElement>('.hs-home-filter')].find(b => b.textContent?.startsWith(label))!
  expect(chip('Needs attention').textContent).toBe('Needs attention1')
  chip('Needs attention').click()
  expect(chip('Needs attention').getAttribute('aria-pressed')).toBe('true')
  expect(cards(root)).toEqual(['door'])
  chip('Ran today').click(); expect(cards(root)).toEqual(['door'])
  chip('Never ran').click(); expect(cards(root)).toEqual(['never'])
  chip('Off').click(); expect(cards(root)).toEqual(['never'])
  chip('All').click()
  const sort = root.querySelector<HTMLSelectElement>('.hs-home-sort select')!
  sort.value = 'name'; sort.dispatchEvent(new Event('change', { bubbles: true }))
  expect(cards(root)).toEqual(['never', 'door', 'heat'])
  sort.value = 'changed'; sort.dispatchEvent(new Event('change', { bubbles: true }))
  expect(cards(root)).toEqual(['heat', 'door', 'never'])
  chip('Never ran').click()
  expect(JSON.parse(localStorage.getItem('hass-sharp:automations-view')!)).toEqual({ filter: 'never', sort: 'changed' })
  dispose?.(); document.body.innerHTML = ''
  const second = setup({ prepare })
  await loaded(second.root)
  expect(second.root.querySelector<HTMLSelectElement>('.hs-home-sort select')!.value).toBe('changed')
  expect(cards(second.root)).toEqual(['never'])
})

it('works when this browser’s storage is unavailable', async () => {
  vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked') })
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked') })
  const { root } = setup({ prepare: f => saved(f, 'hall', 'Hall lights') })
  await loaded(root)
  ;[...root.querySelectorAll<HTMLButtonElement>('.hs-home-filter')].find(b => b.textContent?.startsWith('Off'))!.click()
  expect(root.querySelector('.hs-home-none h2')?.textContent).toBe('No automation is off')
})

it('shows skeleton cards while loading, an explained empty page, and a failure with Try again', async () => {
  const pending = setup({ hold: ['list'] })
  expect(pending.root.querySelectorAll('.hs-home-card--skeleton')).toHaveLength(3)
  expect(pending.root.querySelector('[role="status"]')?.textContent).toBe('Loading automations…')
  pending.release()
  await vi.waitFor(() => expect(pending.root.querySelector('.hs-home-empty')).not.toBeNull())
  const empty = pending.root.querySelector('.hs-home-empty')!
  expect(empty.textContent).toContain('An automation is a small graph')
  expect(empty.querySelectorAll('svg rect')).toHaveLength(4)
  expect(pending.root.querySelector('.hs-home-tools')).toBeNull()
  expect(button(empty, 'New automation')).toBeDefined()
  dispose?.(); document.body.innerHTML = ''
  const failing = setup({ prepare: f => { f.controls.homeError = true; saved(f, 'hall', 'Hall lights') } })
  await vi.waitFor(() => expect(failing.root.querySelector('[role="alert"]')?.textContent).toContain('Automations offline'))
  failing.fixture.controls.homeError = false
  button(failing.root, 'Try again').click()
  await vi.waitFor(() => expect(card(failing.root, 'hall')).not.toBeNull())
})

it('turns an automation off and on from its card at once, rolls back with a message when that fails, and never navigates', async () => {
  const { root, fixture, release } = setup({ hold: ['set_enabled'], prepare: f => {
    saved(f, 'hall', 'Hall lights', { runs: { recentRuns: [run('Succeeded', 5)], runsToday: 3, failedLastDay: 1, lastRunUtc: minutesAgo(5) } })
    saved(f, 'door', 'Porch light', { runs: { runsToday: 2, lastRunUtc: minutesAgo(9), recentRuns: [run('Succeeded', 9)] } })
  } })
  await loaded(root)
  expect(root.querySelector('.hs-home-summary')?.textContent).toBe('2 automations · 5 runs today · 1 failed in the last day')
  const toggle = () => root.querySelector<HTMLInputElement>('input[role="switch"][aria-label="Hall lights on"]')!
  expect(toggle().checked).toBe(true)
  toggle().click()
  // Shown before the reply: the switch, the Off chip, "Off" for the last run, the dots kept, the summary.
  await vi.waitFor(() => expect(toggle().checked).toBe(false))
  const hall = card(root, 'hall')
  expect(hall.classList.contains('hs-home-card--off')).toBe(true)
  expect(hall.querySelector('.hs-home-chip')?.textContent).toBe('Off')
  expect(hall.querySelector('.hs-home-card__last')?.textContent).toBe('Off')
  expect(hall.querySelector('.hs-run-dots')).not.toBeNull()
  expect(root.querySelector('.hs-home-summary')?.textContent).toBe('1 automation · 2 runs today · 1 off')
  release()
  await vi.waitFor(() => expect(fixture.documents.get('hall')!.enabled).toBe(false))
  expect(calls(fixture, 'set_enabled')).toEqual([{ operation: 'set_enabled', id: 'hall', enabled: false }])
  expect(toggle().checked).toBe(false)
  // A failure puts it back and says so.
  fixture.controls.setEnabledError = 'storage'
  toggle().click()
  await vi.waitFor(() => expect(toggle().checked).toBe(true))
  release()
  await vi.waitFor(() => expect(root.querySelector('[role="alert"]')?.textContent).toBe('Could not turn Hall lights on: Fixture refused the switch'))
  expect(toggle().checked).toBe(false)
  expect(root.querySelector('[data-opened]')).toBeNull()
  const offChip = [...root.querySelectorAll<HTMLButtonElement>('.hs-home-filter')].find(b => b.textContent?.startsWith('Off'))!
  offChip.click()
  expect(cards(root)).toEqual(['hall'])
})

it('keeps Refresh entity IDs in the header’s overflow menu, reporting progress and failure', async () => {
  let finish!: (value: unknown) => void
  const refresh = vi.fn().mockImplementationOnce(() => new Promise(resolve => { finish = resolve })).mockRejectedValueOnce(new Error('offline'))
  const { root } = setup({ refresh })
  await loaded(root)
  client.setQueryData(['hass-entities'], { entities: [{ entity_id: 'light.old' }], areas: [], devices: [], floors: [], labels: [] })
  expect([...root.querySelectorAll('button')].some(b => b.textContent === 'Refresh entity IDs')).toBe(false)
  const pick = () => { button(root, 'More actions').click(); [...root.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].find(b => b.textContent?.startsWith('Refresh'))!.click() }
  pick(); pick()
  expect(refresh).toHaveBeenCalledTimes(1)
  expect(root.textContent).toContain('Refreshing entity IDs')
  finish({ success: true })
  await vi.waitFor(() => expect(root.textContent).toContain('Entity IDs refreshed'))
  // The registry is read again (the page's summaries use it), so new names appear.
  await vi.waitFor(() => expect((client.getQueryData(['hass-entities']) as any)?.entities[0].entity_id).toBe('binary_sensor.motion'))
  pick()
  await vi.waitFor(() => expect(root.textContent).toContain('Could not refresh entity IDs'))
})

it('hides the overflow menu and the card actions from non-administrators', async () => {
  const { root } = setup({ admin: false, prepare: f => saved(f, 'hall', 'Hall lights') })
  await loaded(root)
  expect(root.querySelector('[aria-label="More actions"]')).toBeNull()
  expect(card(root, 'hall').querySelector('.hs-home-card__actions')).toBeNull()
})

it('starts a named draft in the editor from New automation, without any request', async () => {
  const { root, fixture, callApi } = setup()
  await loaded(root)
  button(root.querySelector('.hs-home-heading')!, 'New automation').click()
  await Promise.resolve()
  const name = root.querySelector<HTMLInputElement>('input[name="name"]')!
  expect(document.activeElement).toBe(name)
  type(name, 'Morning lights')
  root.querySelector('dialog form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  await vi.waitFor(() => expect(root.querySelector('[aria-label="Visual automation editor"] h1')?.textContent).toBe('Morning lights'))
  expect(callApi).not.toHaveBeenCalled()
  expect(fixture.calls.filter(c => c.request?.operation === 'save' || c.request?.operation === 'delete')).toEqual([])
})

it('lays out for 360 px: one column of cards that never exceed the screen, long names wrap', async () => {
  const { root } = setup({ prepare: f => saved(f, 'long', 'A very long automation name to check wrapping on a small screen without any spaces_in_it_at_all_whatsoever') })
  await loaded(root)
  // Happy DOM has no layout: check the rules the layout keys on, and that the page uses those classes.
  const css = readFileSync('src/styles/automations.css', 'utf8')
  expect(css).toMatch(/\.hs-home-grid \{[^}]*grid-template-columns: repeat\(auto-fill, minmax\(min\(100%, 320px\), 1fr\)\)/)
  expect(css).toMatch(/\.hs-home-card \{[^}]*min-width: 0/)
  expect(css).toMatch(/\.hs-home-card__title \{[^}]*overflow-wrap: anywhere/)
  expect(css).toMatch(/\.hs-home-card__starts li \{[^}]*overflow-wrap: anywhere/)
  expect(css).toMatch(/@media \(max-width: 700px\) \{[^@]*\.hs-home-search \{ flex-basis: 100%; max-width: none; \}/)
  for (const selector of ['.hs-home-grid', '.hs-home-card', '.hs-home-card__title', '.hs-home-card__starts li', '.hs-home-search', '.hs-home-tools'])
    expect(root.querySelector(selector), selector).not.toBeNull()
})
