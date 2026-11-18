import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { A, MemoryRouter, Route, createMemoryHistory, useLocation } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { readFileSync } from 'node:fs'
import { HassCtx } from '../src/context'
import { EntitiesListView } from '../src/views/EntitiesListView'
import { definitionPollInterval } from '../src/entities/DefinitionCard'
import { entityTexts } from '../src/entities/adapter'
import { definitionsQueryKey } from '../src/entities/api'
import type { DefinitionBody, HomeDefinition, RegisteredEntity } from '../src/entities/model'
import type { RecentRun } from '../src/visual/api'
import { visualFixture, type FixtureEntityType } from './visual-fixture'

// The Entities section's list page, over the in-memory entity API, with Home Assistant's `subscribe_entities` faked.
let dispose: (() => void) | undefined
let client: QueryClient
beforeEach(() => localStorage.clear())
afterEach(() => { dispose?.(); dispose = undefined; client?.clear(); document.body.innerHTML = ''; vi.restoreAllMocks() })

type Fixture = ReturnType<typeof visualFixture>
type Subscription = { message: any; push(event: unknown): void; unsubscribe: ReturnType<typeof vi.fn> }

const cameraType: FixtureEntityType = {
  id: 'library:cams:NodeLib.Camera', sourceId: 'cams', typeExpression: 'global::NodeLib.Camera', kind: 'device', domain: null,
  title: 'Driveway camera', description: 'A camera with face detection.',
  settings: [
    { key: 'Host', label: 'Host', description: null, type: 'string', secret: false, options: [] },
    { key: 'Password', label: 'Password', description: null, type: 'string', secret: true, options: [] },
  ],
  entities: [{ key: 'stream', domain: 'camera', name: 'Driveway' }],
}

/**
 * The list at `/entities` over a new in-memory backend. `hold` delays the replies to these operations until released;
 * `refuse` makes Home Assistant refuse every entity request with that `{code, message}`.
 */
function setup(options: { admin?: boolean; prepare?: (fixture: Fixture) => void; hold?: string[]; refuse?: { code: string; message: string } } = {}) {
  const root = document.createElement('div'); root.className = 'hs-app'; document.body.append(root)
  const fixture = visualFixture({ entityTypes: [cameraType] })
  options.prepare?.(fixture)
  const held: (() => void)[] = []
  const release = () => held.splice(0).forEach(go => go())
  const control = { refuse: options.refuse }
  const callWS = async (message: any) => {
    if (message.type === 'hass_sharp/entities' && control.refuse) throw control.refuse
    if (options.hold?.includes(message.request?.operation)) await new Promise<void>(go => held.push(go))
    return fixture.callWS(message)
  }
  const subscriptions: Subscription[] = []
  const subscribeMessage = vi.fn(async (callback: (event: unknown) => void, message: unknown) => {
    const subscription: Subscription = { message, push: callback, unsubscribe: vi.fn(async () => {}) }
    subscriptions.push(subscription)
    return subscription.unsubscribe
  })
  const hass = { ...fixture, user: { is_admin: options.admin ?? true }, callWS, connection: { subscribeMessage } }
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const history = createMemoryHistory(); history.set({ value: '/entities' })
  const Opened = () => { const location = useLocation(); return <h1 data-opened>{location.pathname}{location.search}</h1> }
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={hass as any}>
    <MemoryRouter history={history} root={props => <><A href="/elsewhere">Leave</A>{props.children}</>}>
      <Route path="/entities" component={EntitiesListView} />
      <Route path="/entities/:id" component={Opened} />
      <Route path="/elsewhere" component={() => <h1>Elsewhere</h1>} />
    </MemoryRouter>
  </HassCtx.Provider></QueryClientProvider>, root)
  return { root, fixture, history, release, subscriptions, subscribeMessage, control }
}

const graph = (name: string) => ({ kind: 'graph' as const, graph: { version: 5, name, nodes: [], edges: [] } })
/** The plug: a device of three entities built with nodes, with a secret and a plain setting. */
const plug: DefinitionBody = {
  name: 'Plug', device: { manufacturer: 'Acme', model: 'P1' },
  settings: [{ key: 'token', type: 'string', label: 'Token', secret: true, value: 'hunter2' }, { key: 'room', type: 'string', label: 'Room', value: 'plainly-visible-value' }],
  entities: [{ key: 'power', domain: 'sensor', name: 'Power' }, { key: 'relay', domain: 'switch', name: 'Relay' }, { key: 'online', domain: 'binary_sensor' }],
  logic: graph('Plug'),
}
const camera: DefinitionBody = {
  name: 'Driveway', settings: [{ key: 'Host', value: '10.0.0.9' }, { key: 'Password', value: 'camera-secret' }], logic: { kind: 'class', type: cameraType.id },
}
const porch: DefinitionBody = { name: 'Porch light', settings: [], entities: [{ key: 'light', domain: 'switch', name: 'Porch' }], logic: graph('Porch light') }
const registered = (uniqueId: string, entityId: string, name: string | null, disabled = false): RegisteredEntity =>
  ({ uniqueId, entityId, domain: entityId.split('.')[0], name, disabled })
const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString()
const run = (status: RecentRun['status'], minutes = 5): RecentRun => ({ runId: `${status}-${minutes}`, status, startUtc: minutesAgo(minutes), durationMs: 20 })

/** A running graph device, a failed class definition (entities from the registry) and a graph definition that is off. */
function three(fixture: Fixture) {
  const entities = fixture.entities
  entities.seed('plug', plug)
  entities.runs.set('plug', { recentRuns: [run('Succeeded', 30), run('Failed', 10), run('Succeeded', 2)], runsToday: 3, failedLastDay: 1, lastRunUtc: minutesAgo(2) })
  entities.controls.activation = 'failed'
  entities.controls.activationError = 'Could not reach the camera'
  entities.seed('camera', camera, { liveEntities: [] })
  entities.registry.set('camera', [registered('camera:stream', 'camera.driveway', 'Driveway'), registered('camera:faces', 'sensor.driveway_faces', null, true)])
  entities.controls.activation = 'running'
  entities.seed('porch', porch, { enabled: false })
  entities.registry.set('porch', [registered('porch:light', 'switch.porch', 'Porch')])
}

const card = (root: Element, id: string) => root.querySelector<HTMLElement>(`[data-definition="${id}"]`)!
const cards = (root: Element) => [...root.querySelectorAll<HTMLElement>('[data-definition]')].map(c => c.dataset.definition)
const rows = (element: Element) => [...element.querySelectorAll('.hs-ent-row')].map(row => [
  row.querySelector('.hs-ent-row__name')?.textContent, row.querySelector('.hs-ent-row__type')?.textContent,
  row.querySelector('.hs-ent-row__id')?.textContent ?? null,
  // No state yet is a dash on screen, "No state" for screen readers.
  (row.querySelector('.hs-ent-row__state [aria-hidden="true"]') ?? row.querySelector('.hs-ent-row__state'))?.textContent,
])
const status = (element: Element) => element.querySelector('.hs-ent-card__status')?.textContent
const error = (element: Element) => element.querySelector('.hs-ent-card__error')?.textContent ?? null
const button = (root: Element, text: string) => {
  const found = [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text || b.getAttribute('aria-label') === text)
  expect(found, text).toBeDefined()
  return found!
}
const menu = (root: Element, name: string, item: string) => {
  button(root, `Actions for ${name}`).click()
  const choice = [...root.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].find(b => b.textContent === item)
  expect(choice, item).toBeDefined()
  choice!.click()
}
const type = (input: HTMLInputElement, value: string) => { input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })) }
const loaded = (root: Element) => vi.waitFor(() => expect(root.querySelector('.hs-home-grid[aria-label="Entity definitions"], .hs-home-empty')).not.toBeNull())
const requests = (fixture: Fixture, operation: string) => fixture.entities.requests.filter(r => r.operation === operation)

it('shows each definition: its name, how it works, its device, its status, its entities and, built with nodes, its runs', async () => {
  const { root, fixture } = setup({ prepare: three })
  await loaded(root)
  // By name; one list request with the viewer's day, one types request for the whole page.
  expect(cards(root)).toEqual(['camera', 'plug', 'porch'])
  expect(requests(fixture, 'list')[0]).toMatchObject({ operation: 'list', view: 'home' })
  expect(typeof requests(fixture, 'list')[0].todayUtc).toBe('string')
  await vi.waitFor(() => expect(card(root, 'camera').querySelector('.hs-ent-card__logic')?.textContent).toBe('Driveway camera'))
  expect(requests(fixture, 'types')).toHaveLength(1)

  const plugCard = card(root, 'plug')
  const link = plugCard.querySelector<HTMLAnchorElement>('a.hs-home-card__link')!
  expect(link.getAttribute('href')).toBe('/entities/plug')
  expect(link.textContent).toBe('Plug')
  expect(root.querySelector(`#${link.getAttribute('aria-describedby')}`)?.textContent).toContain('Running')
  expect(plugCard.querySelector('.hs-ent-card__logic')?.textContent).toBe('Nodes')
  expect(plugCard.querySelector('.hs-ent-card__device')?.textContent).toBe('Device')
  expect(status(plugCard)).toBe('Running')
  expect(error(plugCard)).toBeNull()
  // The running definition's entities, with their Home Assistant ids; no state yet.
  expect(rows(plugCard)).toEqual([
    ['Power', 'Sensor', 'sensor.plug_power', '—'],
    ['Relay', 'Switch', 'switch.plug_relay', '—'],
    ['online', 'Binary sensor', 'binary_sensor.plug_online', '—'],
  ])
  expect(plugCard.querySelector('ul.hs-ent-card__entities')?.getAttribute('aria-label')).toBe('Entities of Plug')
  const dots = plugCard.querySelector('.hs-run-dots')!
  expect(dots.getAttribute('aria-label')).toBe('Last 3 runs: 2 succeeded, 1 failed')
  expect(plugCard.querySelector('.hs-ent-card__runs-today')?.textContent).toBe('3 runs today')
  expect(plugCard.querySelector('.hs-ent-card__runs-today a, .hs-run-dots a')).toBeNull()

  // A class definition whose start failed: its entities exist (from the registry) and are unavailable.
  const cameraCard = card(root, 'camera')
  expect(cameraCard.classList.contains('hs-home-card--attention')).toBe(true)
  expect(status(cameraCard)).toBe('Failed')
  expect(error(cameraCard)).toBe('Could not reach the camera')
  expect(cameraCard.querySelector('.hs-ent-card__device')).toBeNull()
  // In the registry's order (by unique id).
  expect(rows(cameraCard)).toEqual([
    ['sensor.driveway_faces', 'Sensor', 'sensor.driveway_faces', 'Disabled in Home Assistant'],
    ['Driveway', 'Camera', 'camera.driveway', '—'],
  ])
  // A class records no runs.
  expect(cameraCard.querySelector('.hs-run-dots, .hs-ent-card__runs-today')).toBeNull()

  // Off: no error, the entities the registry keeps, its runs today (none).
  const porchCard = card(root, 'porch')
  expect(porchCard.classList.contains('hs-home-card--off')).toBe(true)
  expect(status(porchCard)).toBe('Off')
  expect(error(porchCard)).toBeNull()
  expect(rows(porchCard)).toEqual([['Porch', 'Switch', 'switch.porch', '—']])
  expect(porchCard.querySelector('.hs-ent-card__runs-today')?.textContent).toBe('0 runs today')

  expect(root.querySelector('.hs-home-summary')?.textContent).toBe('3 entity definitions · 1 off · 3 runs today')
  // Setting values never reach the page, secret or not.
  for (const value of ['hunter2', 'plainly-visible-value', 'camera-secret', '10.0.0.9']) expect(root.textContent).not.toContain(value)
})

it('says why a definition is not running, starting, or running an older revision, and caps a long list of entities', async () => {
  const many: DefinitionBody = { name: 'Weather station', settings: [], logic: graph('Weather station'),
    entities: Array.from({ length: 9 }, (_, i) => ({ key: `reading_${i + 1}`, domain: 'sensor', name: `Reading ${i + 1}` })) }
  const { root } = setup({ prepare: f => {
    f.entities.controls.activation = 'notLoaded'
    f.entities.controls.activationError = 'The library cams does not compile'
    f.entities.seed('broken', { ...camera, name: 'Broken camera' })
    f.entities.controls.activation = 'running'
    f.entities.seed('gone', { name: 'Old class', settings: [], logic: { kind: 'class', type: 'library:old:NodeLib.Gone' } }, { liveEntities: [], status: 'setup' })
    f.entities.seed('stale', porch, { currentRevisionActive: false, activationError: 'Entity key light is used twice' })
    f.entities.seed('older', { ...porch, name: 'Older porch' }, { currentRevisionActive: false, activationError: null })
    f.entities.seed('station', many)
  } })
  await loaded(root)
  // Enabled and not loaded: why, and no entities until it has started once.
  const broken = card(root, 'broken')
  expect(status(broken)).toBe('Not running')
  expect(error(broken)).toBe('The library cams does not compile')
  expect(broken.classList.contains('hs-home-card--attention')).toBe(true)
  expect(broken.querySelector('.hs-ent-card__none')?.textContent).toBe('Its entities appear after it has started once.')
  expect(broken.querySelector('.hs-ent-row')).toBeNull()
  // Starting, from a class no longer offered.
  const gone = card(root, 'gone')
  expect(status(gone)).toBe('Starting')
  await vi.waitFor(() => expect(gone.querySelector('.hs-ent-card__logic')?.textContent).toBe('Class not found'))
  expect(card(root, 'broken').querySelector('.hs-ent-card__logic')?.textContent).toBe('Driveway camera')
  // Running an older revision: said, with the reason when there is one.
  const stale = card(root, 'stale')
  expect(status(stale)).toBe('Running')
  expect(stale.querySelector('.hs-ent-card__stale')?.textContent).toBe('Changes not applied: Entity key light is used twice')
  expect(stale.classList.contains('hs-home-card--attention')).toBe(true)
  expect(card(root, 'older').querySelector('.hs-ent-card__stale')?.textContent).toBe('Changes not applied')
  expect(card(root, 'station').querySelector('.hs-ent-card__stale')).toBeNull()
  // Nine entities: six, then how many more.
  const station = card(root, 'station')
  expect(rows(station).map(r => r[0])).toEqual(['Reading 1', 'Reading 2', 'Reading 3', 'Reading 4', 'Reading 5', 'Reading 6'])
  expect(station.querySelector('.hs-ent-card__more')?.textContent).toBe('and 3 more')
})

it('shows live states through one subscription, resubscribes only when the entity ids change, and unsubscribes on leaving', async () => {
  const { root, subscriptions, subscribeMessage, fixture } = setup({ prepare: three })
  await loaded(root)
  await vi.waitFor(() => expect(subscribeMessage).toHaveBeenCalledTimes(1))
  // Every card's entities but the one disabled in Home Assistant, sorted.
  expect(subscriptions[0].message).toEqual({ type: 'subscribe_entities', entity_ids: ['binary_sensor.plug_online', 'camera.driveway', 'sensor.plug_power', 'switch.plug_relay', 'switch.porch'] })
  subscriptions[0].push({ a: {
    'sensor.plug_power': { s: '42', a: { unit_of_measurement: 'W' } }, 'switch.plug_relay': { s: 'on', a: {} },
    'camera.driveway': { s: 'unavailable', a: {} }, 'switch.porch': { s: 'unknown', a: {} },
  } })
  await vi.waitFor(() => expect(rows(card(root, 'plug')).map(r => r[3])).toEqual(['42', 'on', '—']))
  expect(rows(card(root, 'camera')).map(r => r[3])).toEqual(['Disabled in Home Assistant', 'Unavailable'])
  expect(rows(card(root, 'porch')).map(r => r[3])).toEqual(['Unknown'])
  subscriptions[0].push({ c: { 'sensor.plug_power': { '+': { s: '43.5', lu: 2 } }, 'switch.plug_relay': { '+': { a: { icon: 'mdi:power' } } } } })
  await vi.waitFor(() => expect(rows(card(root, 'plug')).map(r => r[3])).toEqual(['43.5', 'on', '—']))
  subscriptions[0].push({ r: ['sensor.plug_power'] })
  await vi.waitFor(() => expect(rows(card(root, 'plug'))[0][3]).toBe('—'))

  // A refetch with the same entities keeps the subscription.
  await client.refetchQueries({ queryKey: [...definitionsQueryKey] })
  expect(requests(fixture, 'list').length).toBeGreaterThan(1)
  expect(subscribeMessage).toHaveBeenCalledTimes(1)
  // Turned on, the porch light runs and has its own entity id: a new subscription for the new set.
  root.querySelector<HTMLInputElement>('input[role="switch"][aria-label="Porch light on"]')!.click()
  await vi.waitFor(() => expect(subscribeMessage).toHaveBeenCalledTimes(2))
  expect(subscriptions[0].unsubscribe).toHaveBeenCalledTimes(1)
  expect(subscriptions[1].message.entity_ids).toContain('switch.porch_light')
  expect(subscriptions[1].message.entity_ids).not.toContain('switch.porch')
  // Leaving the page closes it.
  root.querySelector<HTMLAnchorElement>('a[href="/elsewhere"]')!.click()
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Elsewhere'))
  expect(subscriptions[1].unsubscribe).toHaveBeenCalledTimes(1)
  expect(subscribeMessage).toHaveBeenCalledTimes(2)
})

it('turns a definition off and on from its card, with the entity wording on the switch, and rolls back a failure', async () => {
  const { root, fixture, release } = setup({ hold: ['set_enabled'], prepare: three })
  await loaded(root)
  const toggle = () => root.querySelector<HTMLInputElement>('input[role="switch"][aria-label="Plug on"]')!
  expect(toggle().checked).toBe(true)
  expect(toggle().closest('label')?.title).toBe(entityTexts.enabledOn)
  toggle().click()
  // Shown before the reply.
  await vi.waitFor(() => expect(status(card(root, 'plug'))).toBe('Off'))
  expect(toggle().checked).toBe(false)
  expect(toggle().getAttribute('aria-busy')).toBe('true')
  expect(card(root, 'plug').classList.contains('hs-home-card--off')).toBe(true)
  expect(toggle().closest('label')?.title).toBe(entityTexts.enabledOff)
  await vi.waitFor(() => { release(); expect(fixture.entities.documents.get('plug')!.enabled).toBe(false) })
  expect(requests(fixture, 'set_enabled')).toEqual([{ operation: 'set_enabled', id: 'plug', enabled: false }])
  await vi.waitFor(() => expect(toggle().hasAttribute('aria-busy')).toBe(false))
  expect(status(card(root, 'plug'))).toBe('Off')
  // On again: starting at once, running once the list is read again.
  toggle().click()
  await vi.waitFor(() => expect(status(card(root, 'plug'))).toBe('Starting'))
  await vi.waitFor(() => { release(); expect(status(card(root, 'plug'))).toBe('Running') })
  expect(toggle().checked).toBe(true)
  expect(root.querySelector('[data-opened]')).toBeNull()
  // A refused switch puts it back and says so.
  fixture.entities.documents.delete('porch')
  root.querySelector<HTMLInputElement>('input[role="switch"][aria-label="Porch light on"]')!.click()
  await vi.waitFor(() => { release(); expect(root.querySelector('.hs-home-notice[role="alert"]')?.textContent).toBe('Could not turn Porch light on: Entity definition not found') })
  expect(root.querySelector<HTMLInputElement>('input[role="switch"][aria-label="Porch light on"]')!.checked).toBe(false)
  expect(status(card(root, 'porch'))).toBe('Off')
})

it('opens a definition from its menu and deletes one after the confirmation, moving focus to the next card', async () => {
  const { root, fixture, history } = setup({ prepare: three })
  await loaded(root)
  menu(root, 'Plug', 'Delete')
  const dialog = await vi.waitFor(() => { const found = root.querySelector('dialog[open]'); expect(found).not.toBeNull(); return found! })
  expect(dialog.textContent).toContain('Delete this entity definition?')
  expect(dialog.textContent).toContain('Plug and its entities are removed from Home Assistant, with their device if it has one. This cannot be undone.')
  button(dialog, 'Cancel').click()
  await vi.waitFor(() => expect(root.querySelector('dialog[open]')).toBeNull())
  expect(requests(fixture, 'delete')).toEqual([])
  // A refusal stays in the dialog.
  fixture.entities.documents.get('plug')!.revision = 4
  menu(root, 'Plug', 'Delete')
  button(root.querySelector('dialog[open]')!, 'Delete').click()
  await vi.waitFor(() => expect(root.querySelector('dialog[open] [role="alert"]')?.textContent).toContain('Revision conflict; reload the current document'))
  expect(fixture.entities.documents.has('plug')).toBe(true)
  button(root.querySelector('dialog[open]')!, 'Cancel').click()
  await client.refetchQueries({ queryKey: [...definitionsQueryKey] })
  menu(root, 'Plug', 'Delete')
  button(root.querySelector('dialog[open]')!, 'Delete').click()
  await vi.waitFor(() => expect(card(root, 'plug')).toBeNull())
  expect(requests(fixture, 'delete').at(-1)).toEqual({ operation: 'delete', id: 'plug', expectedRevision: 4 })
  expect(fixture.entities.documents.has('plug')).toBe(false)
  await vi.waitFor(() => expect(document.activeElement).toBe(card(root, 'porch').querySelector('.hs-home-card__link')))
  menu(root, 'Driveway', 'Open')
  await vi.waitFor(() => expect(history.get()).toBe('/entities/camera'))
})

it('starts a new entity definition, named, built with nodes or from a class', async () => {
  const { root, history } = setup({ prepare: three })
  await loaded(root)
  button(root.querySelector('.hs-home-heading')!, 'New entity definition').click()
  const dialog = await vi.waitFor(() => { const found = root.querySelector<HTMLDialogElement>('dialog[open]'); expect(found).not.toBeNull(); return found! })
  const name = dialog.querySelector<HTMLInputElement>('input[name="name"]')!
  await vi.waitFor(() => expect(document.activeElement).toBe(name))
  const choices = [...dialog.querySelectorAll<HTMLInputElement>('input[type="radio"]')]
  expect(choices.map(c => c.closest('label')?.querySelector('strong')?.textContent)).toEqual(['Build with nodes', 'Use a class from the node library'])
  expect(choices.map(c => c.closest('label')?.querySelector('small')?.textContent)).toEqual([
    'Define entities and their behaviour with steps.', 'Create entities from a C# class in your node library, with its settings.'])
  expect(choices[0].checked).toBe(true)
  expect(button(dialog, 'Open draft').disabled).toBe(true)
  type(name, 'Garden sensors')
  dialog.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  await vi.waitFor(() => expect(history.get()).toBe('/entities/new?logic=graph&name=Garden%20sensors'))
  expect(root.querySelector('[data-opened]')?.textContent).toBe('/entities/new?logic=graph&name=Garden%20sensors')
  dispose?.(); document.body.innerHTML = ''
  const second = setup({ prepare: three })
  await loaded(second.root)
  button(second.root.querySelector('.hs-home-heading')!, 'New entity definition').click()
  const again = await vi.waitFor(() => second.root.querySelector<HTMLDialogElement>('dialog[open]')!)
  // Cancel forgets the name.
  type(again.querySelector<HTMLInputElement>('input[name="name"]')!, 'Forgotten')
  button(again, 'Cancel').click()
  button(second.root.querySelector('.hs-home-heading')!, 'New entity definition').click()
  await vi.waitFor(() => expect(again.open).toBe(true))
  expect(again.querySelector<HTMLInputElement>('input[name="name"]')!.value).toBe('')
  again.querySelectorAll<HTMLInputElement>('input[type="radio"]')[1].click()
  type(again.querySelector<HTMLInputElement>('input[name="name"]')!, 'Front camera')
  button(again, 'Open draft').click()
  await vi.waitFor(() => expect(second.history.get()).toBe('/entities/new?logic=class&name=Front%20camera'))
})

it('explains the section when there is none yet, with both ways to start', async () => {
  const { root, history } = setup()
  await loaded(root)
  const empty = root.querySelector('.hs-home-empty')!
  expect(empty.querySelector('h2')?.textContent).toBe('No entity definitions yet')
  expect(empty.textContent).toContain('new entities and devices in Home Assistant')
  const start = [...empty.querySelectorAll<HTMLButtonElement>('button.hs-ent-start')]
  expect(start.map(b => b.querySelector('strong')?.textContent)).toEqual(['Build with nodes', 'Use a class from the node library'])
  start[1].click()
  const dialog = await vi.waitFor(() => root.querySelector<HTMLDialogElement>('dialog[open]')!)
  expect(dialog.querySelectorAll<HTMLInputElement>('input[type="radio"]')[1].checked).toBe(true)
  type(dialog.querySelector<HTMLInputElement>('input[name="name"]')!, 'Doorbell')
  button(dialog, 'Open draft').click()
  await vi.waitFor(() => expect(history.get()).toBe('/entities/new?logic=class&name=Doorbell'))
})

it('shows skeleton cards while loading and a failure with Try again; says when the user is no administrator or HassSharp is not running', async () => {
  const pending = setup({ hold: ['list'] })
  expect(pending.root.querySelectorAll('.hs-home-card--skeleton')).toHaveLength(3)
  expect(pending.root.querySelector('[role="status"]')?.textContent).toBe('Loading entity definitions…')
  pending.release()
  await loaded(pending.root)
  dispose?.(); document.body.innerHTML = ''

  const notReady = 'HassSharp is not running: wait for it to finish starting, or turn the integration on under Settings → Devices & services'
  const failing = setup({ prepare: three, refuse: { code: 'not_ready', message: notReady } })
  await vi.waitFor(() => expect(failing.root.querySelector('[role="alert"] h2')?.textContent).toBe('Entities couldn’t load'))
  expect(failing.root.querySelector('[role="alert"]')?.textContent).toContain(notReady)
  expect(failing.subscribeMessage).not.toHaveBeenCalled()
  failing.control.refuse = undefined
  button(failing.root, 'Try again').click()
  await vi.waitFor(() => expect(card(failing.root, 'plug')).not.toBeNull())
  dispose?.(); document.body.innerHTML = ''

  const user = setup({ admin: false, prepare: three, refuse: { code: 'unauthorized', message: 'Administrator required' } })
  await vi.waitFor(() => expect(user.root.querySelector('[role="alert"]')?.textContent).toContain('Administrator required'))
  user.control.refuse = undefined
  button(user.root, 'Try again').click()
  // Shown a list anyway, a user who is no administrator gets no switch or menu.
  await vi.waitFor(() => expect(card(user.root, 'plug')).not.toBeNull())
  expect(card(user.root, 'plug').querySelector('.hs-home-card__actions')).toBeNull()
})

it('reads the list again every 5 s while a definition is starting or a run just started, never when all is settled', () => {
  const at = Date.parse('2026-11-18T12:00:00Z')
  const item = (fields: Partial<HomeDefinition>) => ({ id: 'x', enabled: true, status: 'running', recentRuns: [], ...fields } as HomeDefinition)
  expect(definitionPollInterval([], at)).toBe(false)
  expect(definitionPollInterval([item({})], at)).toBe(false)
  expect(definitionPollInterval([item({ status: 'setup' })], at)).toBe(5000)
  expect(definitionPollInterval([item({ status: 'setup', enabled: false })], at)).toBe(false)
  expect(definitionPollInterval([item({ recentRuns: [{ runId: '1', status: 'Running', startUtc: new Date(at - 60_000).toISOString(), durationMs: null }] })], at)).toBe(5000)
  expect(definitionPollInterval([item({ recentRuns: [{ runId: '1', status: 'Running', startUtc: new Date(at - 3_600_000).toISOString(), durationMs: null }] })], at)).toBe(30_000)
})

it('lays out for 360 px: one column of cards, entity rows that wrap, choices that stack', async () => {
  const { root } = setup({ prepare: f => { three(f); f.entities.seed('long', { ...porch, name: 'A very long entity definition name without_any_spaces_in_it_at_all_whatsoever_to_wrap' }) } })
  await loaded(root)
  // Happy DOM has no layout: check the rules the layout keys on, and that the page uses those classes.
  const css = readFileSync('src/styles/entities.css', 'utf8')
  expect(css).toMatch(/\.hs-app \.hs-ent-row \{[^}]*min-width: 0/)
  expect(css).toMatch(/\.hs-app \.hs-ent-row__id \{[^}]*overflow-wrap: anywhere/)
  expect(css).toMatch(/@media \(max-width: 700px\) \{[^@]*\.hs-app \.hs-ent-row \{[^}]*grid-template-columns: minmax\(0, 1fr\)/)
  expect(css).toMatch(/@media \(max-width: 700px\) \{[^@]*\.hs-app \.hs-ent-starts \{[^}]*grid-template-columns: minmax\(0, 1fr\)/)
  // Every rule of the section is scoped to the panel and takes its colours from the tokens.
  const plain = css.replace(/\/\*[\s\S]*?\*\//g, '').replace(/@media[^{]*\{/g, '')
  for (const rule of plain.split('}').map(r => r.trim()).filter(Boolean))
    expect(rule.split('{')[0].split(',').every(selector => selector.trim().startsWith('.hs-app ')), rule).toBe(true)
  expect(css).not.toMatch(/#[0-9a-f]{3,8}\b|rgb\(|hsl\(/i)
  for (const selector of ['.hs-home-grid', '.hs-home-card', '.hs-home-card__title', '.hs-ent-row', '.hs-ent-row__id'])
    expect(root.querySelector(selector), selector).not.toBeNull()
})
