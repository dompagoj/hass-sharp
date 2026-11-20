import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { A, MemoryRouter, Route, createMemoryHistory, useLocation } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { readFileSync } from 'node:fs'
import { HassCtx } from '../src/context'
import { EntitiesListView } from '../src/views/EntitiesListView'
import { definitionPollInterval, noteStarting } from '../src/entities/DefinitionCard'
import { entityTexts } from '../src/entities/adapter'
import { definitionsQueryKey, entityTypesQueryKey } from '../src/entities/api'
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
 * `refuse` makes Home Assistant refuse every entity request with that `{code, message}`; `failTypes` fails `types`.
 */
function setup(options: { admin?: boolean; prepare?: (fixture: Fixture) => void; hold?: string[]; refuse?: { code: string; message: string }; failTypes?: boolean } = {}) {
  const root = document.createElement('div'); root.className = 'hs-app'; document.body.append(root)
  const fixture = visualFixture({ entityTypes: [cameraType] })
  options.prepare?.(fixture)
  const held: (() => void)[] = []
  const release = () => held.splice(0).forEach(go => go())
  const control = { refuse: options.refuse, failTypes: options.failTypes ?? false }
  const callWS = async (message: any) => {
    if (message.type === 'hass_sharp/entities' && control.refuse) throw control.refuse
    if (message.request?.operation === 'types' && control.failTypes) throw { code: 'entity_request_failed', message: 'Entity request failed; check integration logs' }
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

/** A graph definition's logic: an Entity node for each of `entities` (its config the entity), and no other step. */
const graph = (name: string, entities: Record<string, unknown>[] = []) => ({ kind: 'graph' as const, graph: { version: 5, name, edges: [],
  nodes: entities.map((entity, i) => ({ id: `entity-${i + 1}`, kind: 'entity', config: entity, position: { x: 60, y: 200 * i } })) } })
/** The plug: a device of four entities, each an Entity node, with a secret and a plain setting. */
const plug: DefinitionBody = {
  name: 'Plug', device: { manufacturer: 'Acme', model: 'P1' },
  settings: [{ key: 'token', type: 'string', label: 'Token', secret: true, value: 'hunter2' }, { key: 'room', type: 'string', label: 'Room', value: 'plainly-visible-value' }],
  logic: graph('Plug', [{ key: 'power', domain: 'sensor', name: 'Power' }, { key: 'relay', domain: 'switch', name: 'Relay' }, { key: 'online', domain: 'binary_sensor' },
    { key: 'boost', domain: 'switch', name: 'Boost', enabledByDefault: false }]),
}
const camera: DefinitionBody = {
  name: 'Driveway', settings: [{ key: 'Host', value: '10.0.0.9' }, { key: 'Password', value: 'camera-secret' }], logic: { kind: 'class', type: cameraType.id },
}
const porch: DefinitionBody = { name: 'Porch light', settings: [], logic: graph('Porch light', [{ key: 'light', domain: 'switch', name: 'Porch' }]) }
const registered = (uniqueId: string, entityId: string, name: string | null, disabled = false): RegisteredEntity =>
  ({ uniqueId, entityId, domain: entityId.split('.')[0], name, disabled })
const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString()
const run = (status: RecentRun['status'], minutes = 5): RecentRun => ({ runId: `${status}-${minutes}`, status, startUtc: minutesAgo(minutes), durationMs: 20 })

/** A running graph device, a failed class definition (entities from the registry) and a graph definition that is off. */
function three(fixture: Fixture) {
  const entities = fixture.entities
  entities.seed('plug', plug)
  // Running, with one entity Home Assistant has disabled (it is not enabled by default).
  entities.registry.set('plug', [registered('plug:boost', 'switch.plug_boost', 'Boost', true), registered('plug:online', 'binary_sensor.plug_online', null),
    registered('plug:power', 'sensor.plug_power', 'Power'), registered('plug:relay', 'switch.plug_relay', 'Relay')])
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
const loaded = (root: Element) => vi.waitFor(() => expect(root.querySelector('.hs-home-grid[aria-label="Entity definitions"], .hs-home-empty, [aria-label="Entity definitions that cannot be read"]')).not.toBeNull())
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
  // The running definition's entities, with their Home Assistant ids; no state yet. Enabled ones first, each group by
  // name; the one disabled in Home Assistant says so.
  expect(rows(plugCard)).toEqual([
    ['online', 'Binary sensor', 'binary_sensor.plug_online', '—'],
    ['Power', 'Sensor', 'sensor.plug_power', '—'],
    ['Relay', 'Switch', 'switch.plug_relay', '—'],
    ['Boost', 'Switch', 'switch.plug_boost', 'Disabled in Home Assistant'],
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
  // Enabled first; a registry entry without a name goes by its key.
  expect(rows(cameraCard)).toEqual([
    ['Driveway', 'Camera', 'camera.driveway', '—'],
    ['faces', 'Sensor', 'sensor.driveway_faces', 'Disabled in Home Assistant'],
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
  const many: DefinitionBody = { name: 'Weather station', settings: [],
    logic: graph('Weather station', Array.from({ length: 9 }, (_, i) => ({ key: `reading_${i + 1}`, domain: 'sensor', name: `Reading ${i + 1}` }))) }
  const { root } = setup({ prepare: f => {
    f.entities.controls.activation = 'notLoaded'
    f.entities.controls.activationError = 'The library cams does not compile'
    f.entities.seed('broken', { ...camera, name: 'Broken camera' })
    f.entities.controls.activationError = 'Not active'
    f.entities.seed('idle', { ...porch, name: 'Idle porch' })
    f.entities.controls.activation = 'running'
    f.entities.seed('gone', { name: 'Old class', settings: [], logic: { kind: 'class', type: 'library:old:NodeLib.Gone' } }, { liveEntities: [], status: 'setup' })
    f.entities.seed('stale', porch, { currentRevisionActive: false, activationError: 'Entity key light is used twice' })
    f.entities.seed('older', { ...porch, name: 'Older porch' }, { currentRevisionActive: false, activationError: null })
    f.entities.seed('station', many)
  } })
  await loaded(root)
  // Enabled and it could not be prepared: failed, why, and no entities until it has started once.
  const broken = card(root, 'broken')
  expect(status(broken)).toBe('Failed')
  expect(error(broken)).toBe('The library cams does not compile')
  expect(broken.classList.contains('hs-home-card--attention')).toBe(true)
  expect(broken.querySelector('.hs-ent-card__none')?.textContent).toBe('Its entities appear after it has started once.')
  expect(broken.querySelector('.hs-ent-row')).toBeNull()
  // On and not loaded for no recorded reason, which the backend answers off with "Not active": Not running alone.
  const idle = card(root, 'idle')
  expect(status(idle)).toBe('Not running')
  expect(error(idle)).toBeNull()
  expect(idle.textContent).not.toContain('Not active')
  // Starting, from a class no longer offered.
  const gone = card(root, 'gone')
  expect(status(gone)).toBe('Starting')
  await vi.waitFor(() => expect(gone.querySelector('.hs-ent-card__logic')?.textContent).toBe('Class not found'))
  expect(card(root, 'broken').querySelector('.hs-ent-card__logic')?.textContent).toBe('Driveway camera')
  // Running an older revision: said, with the reason when there is one.
  const stale = card(root, 'stale')
  expect(status(stale)).toBe('Running')
  expect(stale.querySelector('.hs-ent-card__stale')?.textContent).toBe('Changes not applied: Entity key light is used twice')
  // A warning, tinted as one.
  expect(stale.classList.contains('hs-home-card--attention')).toBe(false)
  expect(stale.classList.contains('hs-ent-card--warning')).toBe(true)
  expect(card(root, 'station').classList.contains('hs-ent-card--warning')).toBe(false)
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
  expect(subscriptions[0].message.entity_ids).not.toContain('switch.plug_boost')
  subscriptions[0].push({ a: {
    'sensor.plug_power': { s: '42', a: { unit_of_measurement: 'W' } }, 'switch.plug_relay': { s: 'on', a: {} },
    'camera.driveway': { s: 'unavailable', a: {} }, 'switch.porch': { s: 'unknown', a: {} },
  } })
  // A number is shown with its unit.
  await vi.waitFor(() => expect(rows(card(root, 'plug')).map(r => r[3])).toEqual(['—', '42 W', 'on', 'Disabled in Home Assistant']))
  expect(rows(card(root, 'camera')).map(r => r[3])).toEqual(['Unavailable', 'Disabled in Home Assistant'])
  expect(rows(card(root, 'porch')).map(r => r[3])).toEqual(['Unknown'])
  subscriptions[0].push({ c: { 'sensor.plug_power': { '+': { s: '43.5', lu: 2 } }, 'switch.plug_relay': { '+': { a: { icon: 'mdi:power' } } } } })
  await vi.waitFor(() => expect(rows(card(root, 'plug')).map(r => r[3])).toEqual(['—', '43.5 W', 'on', 'Disabled in Home Assistant']))
  // A change of unit is followed; no unit after a state that is not a number, nor after Unavailable or Unknown.
  subscriptions[0].push({ c: { 'sensor.plug_power': { '+': { s: '0.04', a: { unit_of_measurement: 'kW' } } } } })
  await vi.waitFor(() => expect(rows(card(root, 'plug'))[1][3]).toBe('0.04 kW'))
  subscriptions[0].push({ c: { 'sensor.plug_power': { '+': { s: 'high' } } } })
  await vi.waitFor(() => expect(rows(card(root, 'plug'))[1][3]).toBe('high'))
  subscriptions[0].push({ c: { 'sensor.plug_power': { '+': { s: 'unknown' } } } })
  await vi.waitFor(() => expect(rows(card(root, 'plug'))[1][3]).toBe('Unknown'))
  subscriptions[0].push({ c: { 'sensor.plug_power': { '+': { s: 'unavailable' } } } })
  await vi.waitFor(() => expect(rows(card(root, 'plug'))[1][3]).toBe('Unavailable'))
  // A unit Home Assistant takes away is gone.
  subscriptions[0].push({ c: { 'sensor.plug_power': { '+': { s: '12' }, '-': { a: ['unit_of_measurement'] } } } })
  await vi.waitFor(() => expect(rows(card(root, 'plug'))[1][3]).toBe('12'))
  subscriptions[0].push({ r: ['sensor.plug_power'] })
  await vi.waitFor(() => expect(rows(card(root, 'plug'))[1][3]).toBe('—'))

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

it('shows each type\'s state in words: a light\'s brightness, a cover\'s position, a number\'s unit, a password as dots, a button\'s last press', async () => {
  const things: DefinitionBody = { name: 'Things', settings: [], logic: graph('Things', [
    { key: 'lamp', domain: 'light', name: 'A lamp', options: { dimmable: true } }, { key: 'blind', domain: 'cover', name: 'B blind', options: { positionable: true } },
    { key: 'level', domain: 'number', name: 'C level' }, { key: 'mode', domain: 'select', name: 'D mode', options: { options: ['Eco', 'Comfort'] } },
    { key: 'code', domain: 'text', name: 'E code' }, { key: 'bell', domain: 'button', name: 'F bell' }]) }
  const { root, subscriptions } = setup({ prepare: f => f.entities.seed('things', things) })
  await loaded(root)
  await vi.waitFor(() => expect(subscriptions).toHaveLength(1))
  const states = () => rows(card(root, 'things')).map(r => r[3])
  subscriptions[0].push({ a: {
    'light.things_lamp': { s: 'on', a: { brightness: 153, friendly_name: 'A lamp' } },
    'cover.things_blind': { s: 'open', a: { current_position: 40 } },
    'number.things_level': { s: '21.5', a: { unit_of_measurement: '°C', min: 0, max: 100 } },
    'select.things_mode': { s: 'Eco', a: { options: ['Eco', 'Comfort'] } },
    'text.things_code': { s: '1234', a: { mode: 'password' } },
    'button.things_bell': { s: minutesAgo(5), a: {} },
  } })
  await vi.waitFor(() => expect(states()).toEqual(['On · 60 %', 'Open · 40 %', '21.5 °C', 'Eco', '••••••••', 'Pressed 5 minutes ago']))
  expect(card(root, 'things').textContent).not.toContain('1234')
  // The dots are not read out as eight bullets: a screen reader hears "Hidden".
  const code = card(root, 'things').querySelectorAll('.hs-ent-row__state')[4]
  expect(code.querySelector('[aria-hidden="true"]')?.textContent).toBe('••••••••')
  expect(code.querySelector('.hs-visually-hidden')?.textContent).toBe('Hidden')
  // Nor is its title a row of dots: it has none.
  expect(code.hasAttribute('title')).toBe(false)
  // A light that cannot dim, or is off; a cover closed or moving; a text shown as it is; a button never pressed.
  subscriptions[0].push({ c: {
    'light.things_lamp': { '-': { a: ['brightness'] } }, 'cover.things_blind': { '+': { s: 'closing' } },
    'text.things_code': { '+': { s: 'hello', a: { mode: 'text' } } }, 'button.things_bell': { '+': { s: 'unknown' } },
  } })
  await vi.waitFor(() => expect(states()).toEqual(['On', 'Closing', '21.5 °C', 'Eco', 'hello', 'Never pressed']))
  // A press long ago is a date: "Pressed on 3 November 2026"; a long state is clamped, its whole text in the title.
  const long = 'y'.repeat(255)
  const pressed = new Date(Date.now() - 40 * 86_400_000).toISOString()
  subscriptions[0].push({ c: { 'button.things_bell': { '+': { s: pressed } }, 'text.things_code': { '+': { s: long } } } })
  const date = new Date(pressed).toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })
  await vi.waitFor(() => expect(states().slice(4)).toEqual([long, `Pressed on ${date}`]))
  expect(date).toMatch(/[A-Za-z]{3,}/)
  expect(card(root, 'things').querySelectorAll('.hs-ent-row__state')[4].getAttribute('title')).toBe(long)
  subscriptions[0].push({ c: { 'light.things_lamp': { '+': { s: 'off', a: { brightness: null } } }, 'cover.things_blind': { '+': { s: 'closed', a: { current_position: 0 } } } } })
  await vi.waitFor(() => expect(states().slice(0, 2)).toEqual(['Off', 'Closed']))
  // The light again at full brightness; Unavailable and Unknown as for any entity.
  subscriptions[0].push({ c: { 'light.things_lamp': { '+': { s: 'on', a: { brightness: 255 } } }, 'number.things_level': { '+': { s: 'unknown' } }, 'select.things_mode': { '+': { s: 'unavailable' } } } })
  await vi.waitFor(() => expect(states().slice(0, 4)).toEqual(['On · 100 %', 'Closed', 'Unknown', 'Unavailable']))
})

it('shows a camera as Ready or Unavailable, never its access token, its picture\'s address or any other attribute', async () => {
  const doorbell: DefinitionBody = { name: 'Doorbell', settings: [], logic: graph('Doorbell', [{ key: 'front', domain: 'camera', name: 'Front door' }]) }
  const logged = (['log', 'info', 'warn', 'error', 'debug'] as const).map(method => vi.spyOn(console, method))
  const { root, subscriptions } = setup({ prepare: f => f.entities.seed('doorbell', doorbell) })
  await loaded(root)
  await vi.waitFor(() => expect(subscriptions).toHaveLength(1))
  const token = 'f3c1d9-access-token', picture = `/api/camera_proxy/camera.doorbell_front?token=${token}`
  subscriptions[0].push({ a: { 'camera.doorbell_front': { s: 'idle', a: { access_token: token, entity_picture: picture, friendly_name: 'Front door', brand: 'HassSharp', supported_features: 0 } } } })
  await vi.waitFor(() => expect(rows(card(root, 'doorbell'))).toEqual([['Front door', 'Camera', 'camera.doorbell_front', 'Ready']]))
  const never = () => {
    for (const value of [token, picture, 'camera_proxy', 'HassSharp']) expect(root.innerHTML, value).not.toContain(value)
    for (const spy of logged) expect(JSON.stringify(spy.mock.calls)).not.toContain(token)
  }
  never()
  // A new token, as Home Assistant hands one out every few minutes: still nothing of it.
  subscriptions[0].push({ c: { 'camera.doorbell_front': { '+': { a: { access_token: `${token}-2`, entity_picture: `${picture}-2` } } } } })
  await new Promise(resolve => setTimeout(resolve, 0))
  never()
  subscriptions[0].push({ c: { 'camera.doorbell_front': { '+': { s: 'unavailable' } } } })
  await vi.waitFor(() => expect(rows(card(root, 'doorbell'))[0][3]).toBe('Unavailable'))
  never()
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

it('deletes after the confirmation, moving focus to the next card, and recovers from a refusal without a reload', async () => {
  const { root, fixture } = setup({ prepare: three })
  await loaded(root)
  menu(root, 'Plug', 'Delete')
  const dialog = await vi.waitFor(() => { const found = root.querySelector('dialog[open]'); expect(found).not.toBeNull(); return found! })
  expect(dialog.textContent).toContain('Delete this entity definition?')
  expect(dialog.textContent).toContain('Plug and its entities are removed from Home Assistant, with their device if it has one. This cannot be undone.')
  button(dialog, 'Cancel').click()
  await vi.waitFor(() => expect(root.querySelector('dialog[open]')).toBeNull())
  expect(requests(fixture, 'delete')).toEqual([])

  // Changed behind the page (a library save rebuilds definitions): said in plain words, the list read again, and a
  // retry deletes the revision the list now has.
  fixture.entities.documents.get('plug')!.revision = 4
  menu(root, 'Plug', 'Delete')
  let reads = requests(fixture, 'list').length
  button(root.querySelector('dialog[open]')!, 'Delete').click()
  const alert = () => root.querySelector('dialog[open] [role="alert"]')
  await vi.waitFor(() => expect(alert()?.textContent).toContain('This entity definition changed since the list was loaded. Reload the list, then delete it.'))
  expect(alert()?.textContent).not.toContain('document')
  expect(button(alert()!, 'Reload the list')).toBeDefined()
  expect(fixture.entities.documents.has('plug')).toBe(true)
  await vi.waitFor(() => expect(requests(fixture, 'list').length).toBeGreaterThan(reads))
  await vi.waitFor(() => expect(button(root.querySelector('dialog[open]')!, 'Delete').disabled).toBe(false))
  button(root.querySelector('dialog[open]')!, 'Delete').click()
  await vi.waitFor(() => expect(card(root, 'plug')).toBeNull())
  expect(requests(fixture, 'delete').map(r => r.expectedRevision)).toEqual([1, 4])
  expect(fixture.entities.documents.has('plug')).toBe(false)
  expect(root.querySelector('dialog[open]')).toBeNull()
  await vi.waitFor(() => expect(document.activeElement).toBe(card(root, 'porch').querySelector('.hs-home-card__link')))

  // "Reload the list" closes the dialog and reads the list again; Delete then takes the new revision.
  fixture.entities.documents.get('porch')!.revision = 9
  menu(root, 'Porch light', 'Delete')
  button(root.querySelector('dialog[open]')!, 'Delete').click()
  await vi.waitFor(() => expect(alert()?.textContent).toContain('Reload the list, then delete it.'))
  reads = requests(fixture, 'list').length
  button(alert()!, 'Reload the list').click()
  await vi.waitFor(() => expect(root.querySelector('dialog[open]')).toBeNull())
  await vi.waitFor(() => expect(requests(fixture, 'list').length).toBeGreaterThan(reads))
  await vi.waitFor(() => expect(client.getQueryData<{ definitions: HomeDefinition[] }>([...definitionsQueryKey])?.definitions.find(d => d.id === 'porch')?.revision).toBe(9))
  menu(root, 'Porch light', 'Delete')
  button(root.querySelector('dialog[open]')!, 'Delete').click()
  await vi.waitFor(() => expect(card(root, 'porch')).toBeNull())
  expect(requests(fixture, 'delete').at(-1)).toEqual({ operation: 'delete', id: 'porch', expectedRevision: 9 })

  // Already deleted elsewhere: the dialog closes, and the list, read again, no longer has it.
  fixture.entities.documents.delete('camera')
  menu(root, 'Driveway', 'Delete')
  reads = requests(fixture, 'list').length
  button(root.querySelector('dialog[open]')!, 'Delete').click()
  await vi.waitFor(() => expect(root.querySelector('dialog[open]')).toBeNull())
  expect(requests(fixture, 'delete').at(-1)).toMatchObject({ id: 'camera' })
  await vi.waitFor(() => expect(requests(fixture, 'list').length).toBeGreaterThan(reads))
  await vi.waitFor(() => expect(card(root, 'camera')).toBeNull())
})

it('keeps Reload the list out of reach until the list read after a conflict answers, and keeps any other refusal in the dialog', async () => {
  const holding: string[] = []
  const { root, fixture, release } = setup({ prepare: three, hold: holding })
  await loaded(root)
  fixture.entities.documents.get('plug')!.revision = 4
  menu(root, 'Plug', 'Delete')
  // The list read that follows the refusal is held: the delete is still settling, so Reload the list cannot be used.
  holding.push('list')
  button(root.querySelector('dialog[open]')!, 'Delete').click()
  const alert = () => root.querySelector('dialog[open] [role="alert"]')
  await vi.waitFor(() => expect(alert()?.textContent).toContain('Reload the list, then delete it.'))
  expect(button(alert()!, 'Reload the list').disabled).toBe(true)
  holding.splice(0)
  await vi.waitFor(() => { release(); expect(button(alert()!, 'Reload the list').disabled).toBe(false) })
  button(alert()!, 'Reload the list').click()
  await vi.waitFor(() => expect(root.querySelector('dialog[open]')).toBeNull())

  // Another refusal: its message stays in the dialog, which stays open.
  const answer = fixture.callWS
  fixture.callWS = async (message: any) => (message.request?.operation === 'delete'
    ? { version: 1, ok: false, error: { code: 'invalid_request', detail: 'The registry is busy' } } : answer(message))
  menu(root, 'Porch light', 'Delete')
  button(root.querySelector('dialog[open]')!, 'Delete').click()
  await vi.waitFor(() => expect(alert()?.textContent).toBe('Could not delete it: The registry is busy'))
  expect(root.querySelector('dialog[open]')).not.toBeNull()
  expect(card(root, 'porch')).not.toBeNull()
})

const earlier = 'saved by an earlier development build; delete it and build it again'

it('lists a definition file that cannot be read with why, and deletes it at revision 0 after the confirmation', async () => {
  const { root, fixture } = setup({ prepare: f => { f.entities.unreadable.set('hall', earlier); f.entities.registry.set('hall', [registered('hall:motion', 'binary_sensor.hall', 'Hall')]) } })
  await loaded(root)
  // Not "none yet": the page shows it, by its id and why, with Delete; it cannot be opened or turned on.
  expect(root.querySelector('.hs-home-empty')).toBeNull()
  const list = root.querySelector<HTMLElement>('[aria-label="Entity definitions that cannot be read"]')!
  const row = list.querySelector<HTMLElement>('[data-unreadable="hall"]')!
  expect(row.querySelector('.hs-ent-unreadable__id')?.textContent).toBe('hall')
  expect(row.querySelector('.hs-ent-unreadable__reason')?.textContent).toBe(earlier)
  expect(row.querySelector('a, [role="switch"], input')).toBeNull()
  button(row, 'Delete hall').click()
  const dialog = await vi.waitFor(() => { const found = root.querySelector('dialog[open]'); expect(found).not.toBeNull(); return found! })
  expect(dialog.textContent).toContain('hall and its entities are removed from Home Assistant')
  button(dialog, 'Delete').click()
  await vi.waitFor(() => expect(root.querySelector('dialog[open]')).toBeNull())
  expect(requests(fixture, 'delete')).toEqual([{ operation: 'delete', id: 'hall', expectedRevision: 0 }])
  expect(fixture.entities.unreadable.has('hall')).toBe(false)
  // The row goes, and with nothing left the page says there is none yet.
  await vi.waitFor(() => expect(root.querySelector('[data-unreadable]')).toBeNull())
  expect(root.querySelector('.hs-home-empty h2')?.textContent).toBe('No entity definitions yet')
})

it('shows the files that cannot be read again at once on returning to the page, however long it was open', async () => {
  const holding: string[] = []
  const { root, history, release } = setup({ hold: holding, prepare: f => { f.entities.unreadable.set('hall', earlier) } })
  await loaded(root)
  expect(root.querySelector('[data-unreadable="hall"]')).not.toBeNull()
  // What the cache collects after its time while the page is open: every entry nothing on the page observes.
  for (const query of client.getQueryCache().getAll()) if (!query.getObserversCount()) client.getQueryCache().remove(query)
  history.set({ value: '/elsewhere' })
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Elsewhere'))
  // Back, with the list read again held: the page shows what the cache has, the file included and no empty state.
  holding.push('list')
  history.set({ value: '/entities' })
  await vi.waitFor(() => expect(root.querySelector('#entities-title')).not.toBeNull())
  expect(root.querySelector('[data-unreadable="hall"]')).not.toBeNull()
  expect(root.querySelector('.hs-home-empty')).toBeNull()
  holding.splice(0)
  release()
})

it('after deleting a file that cannot be read, focus goes to the next such file, else the previous one', async () => {
  const { root } = setup({ prepare: f => { three(f); f.entities.unreadable.set('alpha', earlier); f.entities.unreadable.set('beta', earlier); f.entities.unreadable.set('gamma', earlier) } })
  await loaded(root)
  await vi.waitFor(() => expect(root.querySelectorAll('[data-unreadable]')).toHaveLength(3))
  const remove = async (id: string) => {
    button(root.querySelector(`[data-unreadable="${id}"]`)!, `Delete ${id}`).click()
    const dialog = await vi.waitFor(() => { const found = root.querySelector('dialog[open]'); expect(found).not.toBeNull(); return found! })
    button(dialog, 'Delete').click()
    await vi.waitFor(() => expect(root.querySelector(`[data-unreadable="${id}"]`)).toBeNull())
  }
  await remove('beta')
  await vi.waitFor(() => expect(document.activeElement?.getAttribute('aria-label')).toBe('Delete gamma'))
  await remove('gamma')
  await vi.waitFor(() => expect(document.activeElement?.getAttribute('aria-label')).toBe('Delete alpha'))
  await remove('alpha')
  await vi.waitFor(() => expect(document.activeElement?.textContent).toContain('New entity definition'))
})

it('offers no Delete for a file a delete cannot remove, and says to remove it by hand', async () => {
  const { root } = setup({ prepare: f => { f.entities.unreadable.set('Hall', 'Invalid definition ID'); f.entities.unreadable.set('broken', 'Invalid entity definition envelope') } })
  await loaded(root)
  const hall = root.querySelector<HTMLElement>('[data-unreadable="Hall"]')!
  expect(hall.querySelector('.hs-ent-unreadable__reason')?.textContent).toBe('Invalid definition ID')
  expect(hall.querySelector('button')).toBeNull()
  expect(hall.textContent).toContain('Remove this file from Home Assistant\'s .storage/hass_sharp folder by hand.')
  // One whose content is the problem can be deleted, as before.
  const broken = root.querySelector<HTMLElement>('[data-unreadable="broken"]')!
  expect(broken.querySelector('button[aria-label="Delete broken"]')).not.toBeNull()
  expect(broken.textContent).not.toContain('by hand')
})

it('lists a file that cannot be read beside the cards of the readable definitions', async () => {
  const { root } = setup({ prepare: f => { three(f); f.entities.unreadable.set('broken', 'Invalid entity definition envelope') } })
  await loaded(root)
  await vi.waitFor(() => expect(cards(root)).toHaveLength(3))
  expect(root.querySelector('[data-unreadable="broken"] .hs-ent-unreadable__reason')?.textContent).toBe('Invalid entity definition envelope')
  expect(cards(root)).not.toContain('broken')
})

it('opens a definition from its menu', async () => {
  const { root, history } = setup({ prepare: three })
  await loaded(root)
  menu(root, 'Driveway', 'Open')
  await vi.waitFor(() => expect(history.get()).toBe('/entities/camera'))
})

it('names a class it could not look up, and reads the classes and the list again after a node library is saved', async () => {
  const { root, fixture, control } = setup({ prepare: three, failTypes: true })
  await loaded(root)
  const logic = () => card(root, 'camera').querySelector('.hs-ent-card__logic')
  await vi.waitFor(() => expect(card(root, 'camera').querySelector('.hs-ent-card__hint')?.textContent).toBe('Could not load the class’s name.'))
  expect(logic()?.textContent).toBe('Class')
  expect(logic()?.getAttribute('title')).toBe('Could not load the class’s name.')
  // The page still works.
  expect(status(card(root, 'camera'))).toBe('Failed')
  control.failTypes = false
  const before = { list: requests(fixture, 'list').length, types: fixture.calls.filter(c => c.request?.operation === 'types').length }
  window.dispatchEvent(new Event('hass-sharp-libraries-changed'))
  await vi.waitFor(() => expect(logic()?.textContent).toBe('Driveway camera'))
  expect(card(root, 'camera').querySelector('.hs-ent-card__hint')).toBeNull()
  expect(logic()?.hasAttribute('title')).toBe(false)
  expect(requests(fixture, 'list').length).toBeGreaterThan(before.list)
  expect(requests(fixture, 'types').length).toBeGreaterThan(0)
  expect(client.getQueryState([...entityTypesQueryKey])?.status).toBe('success')
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
  expect(button(dialog, 'Continue').disabled).toBe(true)
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
  button(again, 'Continue').click()
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
  button(dialog, 'Continue').click()
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
  // Starting for more than 2 minutes (since the page first saw it starting): every 30 s, as for an old run.
  const since = new Map<string, number>()
  noteStarting(since, [item({ status: 'setup' })], at - 3 * 60_000)
  expect(definitionPollInterval([item({ status: 'setup' })], at, since)).toBe(30_000)
  expect(definitionPollInterval([item({ status: 'setup' })], at - 90_000, since)).toBe(5000)
  noteStarting(since, [item({ status: 'setup' })], at)
  expect(since.get('x')).toBe(at - 3 * 60_000)
  expect(definitionPollInterval([item({ status: 'setup', recentRuns: [{ runId: '1', status: 'Running', startUtc: new Date(at - 60_000).toISOString(), durationMs: null }] })], at, since)).toBe(5000)
  // Once it has started, it is forgotten: starting again counts from then.
  noteStarting(since, [item({})], at)
  expect(since.has('x')).toBe(false)
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
  // A long state is clamped to three lines in both layouts.
  expect(css).toMatch(/\.hs-app \.hs-ent-row__state \{[^}]*-webkit-line-clamp: 3;[^}]*overflow: hidden/)
  expect(css).not.toMatch(/@media \(max-width: 700px\) \{[^@]*\.hs-app \.hs-ent-row__state \{[^}]*(display|-webkit-line-clamp|overflow):/)
  // Every rule of the section is scoped to the panel and takes its colours from the tokens.
  const plain = css.replace(/\/\*[\s\S]*?\*\//g, '').replace(/@media[^{]*\{/g, '')
  for (const rule of plain.split('}').map(r => r.trim()).filter(Boolean))
    expect(rule.split('{')[0].split(',').every(selector => selector.trim().startsWith('.hs-app ')), rule).toBe(true)
  expect(css).not.toMatch(/#[0-9a-f]{3,8}\b|rgb\(|hsl\(/i)
  for (const selector of ['.hs-home-grid', '.hs-home-card', '.hs-home-card__title', '.hs-ent-row', '.hs-ent-row__id'])
    expect(root.querySelector(selector), selector).not.toBeNull()
})
