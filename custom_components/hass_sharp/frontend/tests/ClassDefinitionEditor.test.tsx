import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { A, MemoryRouter, Route, createMemoryHistory } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { EntityDefinitionView } from '../src/views/EntityDefinitionView'
import type { DefinitionBody } from '../src/entities/model'
import { visualFixture, type FixtureEntityType } from './visual-fixture'

// An entity definition built from a class in a node library, on its own page, against the in-memory entity API: a
// class is chosen, its settings filled in, the definition saved, read back, switched, refused and recovered.
let dispose: (() => void) | undefined
let client: QueryClient | undefined
afterEach(async () => {
  dispose?.(); dispose = undefined
  client?.clear()
  document.body.innerHTML = ''
  vi.restoreAllMocks()
  vi.useRealTimers()
  await new Promise(resolve => setTimeout(resolve, 0))
})

const plugType: FixtureEntityType = {
  id: 'library:shelly:NodeLib.ShellyPlug', sourceId: 'shelly', typeExpression: 'global::NodeLib.ShellyPlug', kind: 'device', domain: null,
  title: 'Shelly plug', description: 'A Shelly Plug S on the local network.',
  settings: [
    { key: 'Host', label: 'Host', description: 'The address of the plug.', type: 'string', secret: false, options: [] },
    { key: 'Interval', label: 'Poll interval', description: null, type: 'integer', secret: false, options: [], minimum: 1, maximum: 3600 },
    { key: 'Password', label: 'Password', description: null, type: 'string', secret: true, options: [] },
    { key: 'Mode', label: 'Mode', description: null, type: 'enum', secret: false, options: ['Local', 'Cloud'] },
    { key: 'Led', label: 'LED', description: null, type: 'boolean', secret: false, options: [] },
    { key: 'Offset', label: 'Offset', description: null, type: 'number', secret: false, options: [] },
  ],
  entities: [{ key: 'relay', domain: 'switch', name: 'Relay' }, { key: 'power', domain: 'sensor', name: 'Power' }],
}
const thermometerType: FixtureEntityType = {
  id: 'library:shelly:NodeLib.Thermometer', sourceId: 'shelly', typeExpression: 'global::NodeLib.Thermometer', kind: 'entity', domain: 'sensor',
  title: 'Thermometer', description: null,
  settings: [{ key: 'Host', label: 'Host', description: null, type: 'string', secret: false, options: [] }],
  entities: [{ key: 'temperature', domain: 'sensor', name: 'Temperature' }],
}
const plug = (settings: DefinitionBody['settings'], name = 'Plug', type = plugType.id): DefinitionBody => ({ name, settings, logic: { kind: 'class', type } })

type Fixture = ReturnType<typeof visualFixture>
function setup(path: string, prepare?: (fixture: Fixture) => void, fixture: Fixture = visualFixture({ entityTypes: [plugType, thermometerType] })) {
  prepare?.(fixture)
  const callWS = (message: any) => (message.type === 'hass_sharp/get_entities' ? Promise.resolve({ entities: [], areas: [], devices: [], floors: [], labels: [] }) : fixture.callWS(message))
  const hass = { ...fixture, callWS, user: { is_admin: true, id: 'u1' } }
  const root = document.createElement('div')
  root.className = 'hs-app'
  document.body.append(root)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const history = createMemoryHistory()
  history.set({ value: path })
  dispose = render(() => (
    <QueryClientProvider client={client!}><HassCtx.Provider value={hass as any}>
      <MemoryRouter history={history} root={props => <><A href="/elsewhere">Leave</A>{props.children}</>}>
        <Route path="/entities/:id" component={EntityDefinitionView} />
        <Route path="/entities" component={() => <h1>Entities</h1>} />
        <Route path="/libraries" component={() => <h1>Node library</h1>} />
        <Route path="/elsewhere" component={() => <h1>Elsewhere</h1>} />
      </MemoryRouter>
    </HassCtx.Provider></QueryClientProvider>
  ), root)
  return { root, fixture, history }
}

const page = (root: Element) => root.querySelector<HTMLElement>('section.hs-cdef[aria-label="Entity definition"]')
const ready = async (root: Element) => { await vi.waitFor(() => expect(page(root)).not.toBeNull()); return page(root)! }
const button = (root: Element, name: string) => {
  const found = [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === name || b.getAttribute('aria-label') === name)
  expect(found, name).toBeDefined()
  return found!
}
const link = (root: Element, name: string) => {
  const found = [...root.querySelectorAll<HTMLAnchorElement>('a')].find(a => a.textContent?.trim() === name)
  expect(found, name).toBeDefined()
  return found!
}
/** The control a visible label names. */
const control = <T extends HTMLElement = HTMLInputElement>(root: Element, label: string) => {
  const found = [...root.querySelectorAll('label')].find(l => l.textContent?.trim() === label)
  expect(found, label).toBeDefined()
  return document.getElementById(found!.htmlFor) as unknown as T
}
const type = (input: HTMLInputElement, text: string) => { input.value = text; input.dispatchEvent(new Event('input', { bubbles: true })) }
const choose = (select: HTMLSelectElement, value: string) => { select.value = value; select.dispatchEvent(new Event('change', { bubbles: true })) }
/** Types into a field that takes its text on blur (a secret, a device detail). */
const enter = (input: HTMLInputElement, text: string) => { input.value = text; input.dispatchEvent(new Event('input', { bubbles: true })); input.dispatchEvent(new FocusEvent('blur')) }
const nameInput = (root: Element) => root.querySelector<HTMLInputElement>('input[aria-label="Entity definition name"]')!
const pickClass = (root: Element, id: string) => root.querySelector<HTMLInputElement>(`input[type="radio"][value="${id}"]`)!
const save = (root: Element) => button(root, 'Save')
const saves = (fixture: Fixture) => fixture.entities.requests.filter(r => r.operation === 'save')
const status = (root: Element) => root.querySelector('[data-definition-status]')?.textContent?.trim()
const chip = (root: Element) => root.querySelector('.hs-save-chip__text')?.textContent

it('creates an entity definition from a class with two settings, and shows its entities once it runs', async () => {
  const { root, fixture, history } = setup('/entities/new?logic=class')
  const view = await ready(root)
  // The class comes first: each with its title, description and whether it is a device.
  await vi.waitFor(() => expect(pickClass(root, plugType.id)).not.toBeNull())
  const choices = view.querySelector('fieldset')!
  expect(choices.querySelector('legend')!.textContent).toBe('Choose a class')
  expect(choices.textContent).toContain('A Shelly Plug S on the local network.')
  expect(pickClass(root, plugType.id).closest('label')!.textContent).toContain('Device')
  expect(pickClass(root, thermometerType.id).closest('label')!.textContent).toContain('Single entity')
  expect(save(root).getAttribute('aria-disabled')).toBe('true')
  pickClass(root, plugType.id).click()
  // Named after the class; one field per setting in the class's order, labelled by the class.
  await vi.waitFor(() => expect(nameInput(root).value).toBe('Shelly plug'))
  const settings = view.querySelector('section[aria-label="Settings"]')!
  expect([...settings.querySelectorAll('label')].map(l => l.textContent?.trim())).toEqual(['Host', 'Poll interval', 'Password', 'Mode', 'LED', 'Offset'])
  expect(settings.textContent).toContain('The address of the plug.')
  expect(control(root, 'Host').placeholder).toBe('Class default')
  const interval = control(root, 'Poll interval')
  expect([interval.type, interval.min, interval.max]).toEqual(['number', '1', '3600'])
  expect(settings.textContent).toContain('Between 1 and 3600.')
  expect([...control<HTMLSelectElement>(root, 'Mode').options].map(o => o.textContent)).toEqual(["Use the class's default", 'Local', 'Cloud'])
  type(control(root, 'Host'), '10.0.0.1')
  type(interval, '30')
  expect(chip(root)).toContain('Unsaved changes')
  save(root).click()
  await vi.waitFor(() => expect(history.get()).toBe('/entities/shelly_plug'))
  // Only what was filled in is sent; the rest keeps the class's defaults. New, it is saved on.
  expect(saves(fixture)).toEqual([{ operation: 'save', id: 'shelly_plug', expectedRevision: 0, definition: {
    name: 'Shelly plug', device: null, settings: [{ key: 'Host', value: '10.0.0.1' }, { key: 'Interval', value: 30 }], logic: { kind: 'class', type: plugType.id },
  } }])
  expect(fixture.entities.documents.get('shelly_plug')!.settings).toEqual([{ key: 'Host', value: '10.0.0.1' }, { key: 'Interval', value: 30 }])
  await vi.waitFor(() => expect(status(root)).toBe('Running'))
  expect(chip(root)).toBe('Saved')
  // The class is fixed now, with a way to its code; the entities are listed with their Home Assistant ids.
  expect(pickClass(root, plugType.id)).toBeNull()
  expect(link(root, 'Open the class in the node library').getAttribute('href')).toBe('/libraries?file=shelly')
  const entities = page(root)!.querySelector('section[aria-label="Entities"]')!
  expect([...entities.querySelectorAll('li')].map(li => li.querySelector('strong')!.textContent)).toEqual(['Power', 'Relay'])
  expect([...entities.querySelectorAll('code')].map(c => c.textContent)).toEqual(['sensor.shelly_plug_power', 'switch.shelly_plug_relay'])
  const shown = vi.fn()
  root.addEventListener('hass-more-info', event => shown((event as CustomEvent).detail))
  button(entities, 'Show switch.shelly_plug_relay in Home Assistant').click()
  expect(shown).toHaveBeenCalledWith({ entityId: 'switch.shelly_plug_relay' })
  expect(control(root, 'Host').value).toBe('10.0.0.1')
  expect(control(root, 'Poll interval').value).toBe('30')
})

it('creates a second plug from the same class with its own settings, and both are listed with their own entities', async () => {
  const fixture = visualFixture({ entityTypes: [plugType, thermometerType] })
  fixture.entities.seed('kitchen_plug', plug([{ key: 'Host', value: '10.0.0.1' }], 'Kitchen plug'))
  const { root, history } = setup(`/entities/new?logic=class&type=${encodeURIComponent(plugType.id)}&name=Desk plug`, undefined, fixture)
  await ready(root)
  // The class from the address is chosen; the name from the address stays.
  await vi.waitFor(() => expect(control(root, 'Host')).not.toBeNull())
  expect(nameInput(root).value).toBe('Desk plug')
  type(control(root, 'Host'), '10.0.0.2')
  choose(control<HTMLSelectElement>(root, 'Mode'), 'Cloud')
  save(root).click()
  await vi.waitFor(() => expect(history.get()).toBe('/entities/desk_plug'))
  expect(fixture.entities.documents.get('kitchen_plug')!.settings).toEqual([{ key: 'Host', value: '10.0.0.1' }])
  expect(fixture.entities.documents.get('desk_plug')!.settings).toEqual([{ key: 'Host', value: '10.0.0.2' }, { key: 'Mode', value: 'Cloud' }])
  const listed = await fixture.callWS({ type: 'hass_sharp/entities', request: { operation: 'list' } }) as any
  expect(listed.result.map((d: any) => [d.id, d.logic.type, d.liveEntities.map((e: any) => e.entityId)])).toEqual([
    ['desk_plug', plugType.id, ['switch.desk_plug_relay', 'sensor.desk_plug_power']],
    ['kitchen_plug', plugType.id, ['switch.kitchen_plug_relay', 'sensor.kitchen_plug_power']],
  ])
})

it('keeps the class default for a field left empty, and says so', async () => {
  const { root, fixture } = setup('/entities/plug', f => f.entities.seed('plug', plug([{ key: 'Host', value: '10.0.0.1' }, { key: 'Interval', value: 30 }, { key: 'Led', value: true }])))
  await ready(root)
  await vi.waitFor(() => expect(control(root, 'Poll interval').value).toBe('30'))
  expect(control<HTMLSelectElement>(root, 'LED').value).toBe('true')
  expect(control(root, 'Poll interval').placeholder).toBe('Class default')
  type(control(root, 'Poll interval'), '')
  choose(control<HTMLSelectElement>(root, 'LED'), '')
  save(root).click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(1))
  expect(saves(fixture)[0].definition.settings).toEqual([{ key: 'Host', value: '10.0.0.1' }, { key: 'Interval', value: null }, { key: 'Led', value: null }])
  await vi.waitFor(() => expect(fixture.entities.documents.get('plug')!.revision).toBe(2))
  expect(fixture.entities.documents.get('plug')!.settings.map(s => s.value)).toEqual(['10.0.0.1', null, null])
  await vi.waitFor(() => expect(chip(root)).toBe('Saved'))
  expect(control(root, 'Poll interval').value).toBe('')
})

it("shows the server's message on the field of an integer out of its range, while editing and when the save is refused", async () => {
  const { root, fixture } = setup('/entities/new?logic=class', f => { f.entities.controls.classChecks = true })
  await ready(root)
  await vi.waitFor(() => expect(pickClass(root, plugType.id)).not.toBeNull())
  pickClass(root, plugType.id).click()
  await vi.waitFor(() => expect(control(root, 'Poll interval')).not.toBeNull())
  type(control(root, 'Poll interval'), '5000')
  const message = 'Poll interval takes a whole number from 1 to 3600'
  await vi.waitFor(() => expect(control(root, 'Poll interval').closest('[data-field]')!.textContent).toContain(message))
  expect(control(root, 'Poll interval').getAttribute('aria-invalid')).toBe('true')
  expect(fixture.entities.requests.some(r => r.operation === 'preview' && r.definition.settings.some((s: any) => s.key === 'Interval' && s.value === 5000))).toBe(true)
  save(root).click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(1))
  await vi.waitFor(() => expect(root.querySelector('[role="alert"]')?.textContent).toContain('Fix the problems before saving.'))
  expect(control(root, 'Poll interval').closest('[data-field]')!.textContent).toContain(message)
  expect(fixture.entities.documents.size).toBe(0)
  type(control(root, 'Poll interval'), '60')
  await vi.waitFor(() => expect(control(root, 'Poll interval').closest('[data-field]')!.textContent).not.toContain(message))
  expect(control(root, 'Poll interval').getAttribute('aria-invalid')).toBeNull()
  save(root).click()
  await vi.waitFor(() => expect(fixture.entities.documents.get('shelly_plug')?.settings).toEqual([{ key: 'Interval', value: 60 }]))
})

it('never shows a secret, sends it once, keeps it on later saves, and writes nothing to storage', async () => {
  const written = vi.spyOn(Storage.prototype, 'setItem')
  const { root, fixture, history } = setup('/entities/new?logic=class')
  await ready(root)
  await vi.waitFor(() => expect(pickClass(root, plugType.id)).not.toBeNull())
  pickClass(root, plugType.id).click()
  await vi.waitFor(() => expect(control(root, 'Password')).not.toBeNull())
  const password = control(root, 'Password')
  expect(password.type).toBe('password')
  enter(password, 'hunter2')
  expect(password.value).toBe('')
  expect(password.placeholder).toBe('New value set; type to replace')
  expect(root.innerHTML).not.toContain('hunter2')
  save(root).click()
  await vi.waitFor(() => expect(history.get()).toBe('/entities/shelly_plug'))
  await vi.waitFor(() => expect(control(root, 'Password').placeholder).toBe('Saved; type to replace'))
  expect(saves(fixture)[0].definition.settings).toEqual([{ key: 'Password', value: 'hunter2' }])
  const sentBefore = fixture.entities.requests.length
  // Saved again with another change: the secret is kept, not sent.
  type(control(root, 'Host'), '10.0.0.3')
  save(root).click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(2))
  expect(saves(fixture)[1].definition.settings).toEqual([{ key: 'Password', keep: true }, { key: 'Host', value: '10.0.0.3' }])
  await vi.waitFor(() => expect(fixture.entities.documents.get('shelly_plug')!.revision).toBe(2))
  expect(fixture.entities.documents.get('shelly_plug')!.settings).toEqual([{ key: 'Password', value: 'hunter2' }, { key: 'Host', value: '10.0.0.3' }])
  expect(JSON.stringify(fixture.entities.requests.slice(sentBefore))).not.toContain('hunter2')
  // Only the save and the checks of the typed value carried it; the id was found without it.
  expect(fixture.entities.requests.filter(r => JSON.stringify(r).includes('hunter2')).every(r => r.operation === 'preview' || r.operation === 'save')).toBe(true)
  expect(root.innerHTML).not.toContain('hunter2')
  // Clear: no value is kept.
  button(root, 'Clear Password').click()
  save(root).click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(3))
  expect(saves(fixture)[2].definition.settings[0]).toEqual({ key: 'Password', value: null })
  expect(written).not.toHaveBeenCalled()
  expect(localStorage.length).toBe(0)
})

it('reads a saved secret back masked and keeps it when the definition is saved unchanged but renamed', async () => {
  const written = vi.spyOn(Storage.prototype, 'setItem')
  const { root, fixture } = setup('/entities/plug', f => f.entities.seed('plug', plug([{ key: 'Host', value: '10.0.0.1' }, { key: 'Password', value: 'stored-secret' }])))
  await ready(root)
  await vi.waitFor(() => expect(control(root, 'Password').placeholder).toBe('Saved; type to replace'))
  expect(root.innerHTML).not.toContain('stored-secret')
  type(nameInput(root), 'Plug in the hall')
  save(root).click()
  await vi.waitFor(() => expect(fixture.entities.documents.get('plug')!.revision).toBe(2))
  expect(saves(fixture)[0].definition).toEqual({ name: 'Plug in the hall', device: null, settings: [{ key: 'Host', value: '10.0.0.1' }, { key: 'Password', keep: true }], logic: { kind: 'class', type: plugType.id } })
  expect(fixture.entities.documents.get('plug')!.settings).toEqual([{ key: 'Host', value: '10.0.0.1' }, { key: 'Password', value: 'stored-secret' }])
  // Neither this page nor the editor that read it first keeps a draft.
  dispose!(); dispose = undefined
  expect(written).not.toHaveBeenCalled()
  expect(localStorage.length).toBe(0)
})

it('explains a class that is gone, lists the saved settings by key only, and lets another class be chosen', async () => {
  const { root, fixture } = setup('/entities/old', f => {
    f.entities.controls.classChecks = true
    f.entities.seed('old', plug([{ key: 'Host', value: '10.0.0.9' }, { key: 'Password', value: 'pw-secret' }, { key: 'Token', value: 'tok-secret' }], 'Old plug', 'library:old:NodeLib.Gone'))
  })
  const view = await ready(root)
  await vi.waitFor(() => expect(view.textContent).toContain("This entity definition's class is not available: it was removed, or its node library does not compile."))
  expect(link(root, 'Open the node library').getAttribute('href')).toBe('/libraries?file=old')
  expect([...view.querySelectorAll('ul[aria-label="Saved settings"] li')].map(li => li.textContent)).toEqual(['Host', 'Password', 'Token'])
  expect(root.innerHTML).not.toContain('10.0.0.9')
  expect(root.innerHTML).not.toContain('pw-secret')
  expect(root.innerHTML).not.toContain('tok-secret')
  expect(view.querySelector('fieldset legend')!.textContent).toBe('Choose another class')
  expect(save(root).getAttribute('aria-disabled')).toBe('true')
  save(root).click()
  expect(saves(fixture)).toHaveLength(0)
  // Another class: the stored values of the settings it declares are kept, the others are no longer used.
  pickClass(root, plugType.id).click()
  await vi.waitFor(() => expect(control(root, 'Host').placeholder).toBe('Saved; type to replace'))
  expect(control(root, 'Password').placeholder).toBe('Saved; type to replace')
  const unused = view.querySelector('section[aria-label="No longer used by this class"]')!
  expect(unused.textContent).toContain('Token')
  expect(save(root).getAttribute('aria-disabled')).toBeNull()
  save(root).click()
  // The server refuses a setting the class does not have: shown on its row.
  await vi.waitFor(() => expect(unused.textContent).toContain('Shelly plug has no setting Token'))
  expect(saves(fixture)[0].definition.settings).toEqual([{ key: 'Host', keep: true }, { key: 'Password', keep: true }, { key: 'Token', keep: true }])
  button(root, 'Remove setting Token').click()
  expect(view.querySelector('section[aria-label="No longer used by this class"]')).toBeNull()
  save(root).click()
  await vi.waitFor(() => expect(fixture.entities.documents.get('old')!.revision).toBe(2))
  expect(fixture.entities.documents.get('old')!.logic).toMatchObject({ kind: 'class', type: plugType.id })
  expect(fixture.entities.documents.get('old')!.settings).toEqual([{ key: 'Host', value: '10.0.0.9' }, { key: 'Password', value: 'pw-secret' }])
  // A class it knows: the plain value shows again, the secret stays hidden, and the class is fixed.
  await vi.waitFor(() => expect(control(root, 'Host').value).toBe('10.0.0.9'))
  expect(root.innerHTML).not.toContain('pw-secret')
  expect(pickClass(root, plugType.id)).toBeNull()
  expect(view.textContent).not.toContain('is not available')
})

it("says at the top that a save waits for the node libraries to compile", async () => {
  const { root, fixture } = setup('/entities/plug', f => {
    f.entities.seed('plug', plug([{ key: 'Host', value: '10.0.0.1' }]))
    f.entities.controls.libraryErrors = true
  })
  await ready(root)
  // Every setting comes back masked while the libraries do not compile.
  await vi.waitFor(() => expect(control(root, 'Host').placeholder).toBe('Saved; type to replace'))
  expect(root.innerHTML).not.toContain('10.0.0.1')
  type(nameInput(root), 'Plug two')
  save(root).click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(1))
  await vi.waitFor(() => expect(root.querySelector('ul[aria-label="Problems of this entity definition"]')?.textContent).toContain('The node libraries have errors; fix them first'))
  expect(fixture.entities.documents.get('plug')!.revision).toBe(1)
})

it('lists the values of settings the class no longer has by key, sends them back until they are removed', async () => {
  const { root, fixture } = setup('/entities/plug', f => f.entities.seed('plug', plug([{ key: 'Host', value: '10.0.0.1' }, { key: 'Old', value: 'stray' }])))
  const view = await ready(root)
  await vi.waitFor(() => expect(view.querySelector('section[aria-label="No longer used by this class"]')).not.toBeNull())
  const unused = view.querySelector('section[aria-label="No longer used by this class"]')!
  expect([...unused.querySelectorAll('code')].map(c => c.textContent)).toEqual(['Old'])
  expect(root.innerHTML).not.toContain('stray')
  type(control(root, 'Host'), '10.0.0.2')
  save(root).click()
  await vi.waitFor(() => expect(fixture.entities.documents.get('plug')!.revision).toBe(2))
  expect(saves(fixture)[0].definition.settings).toEqual([{ key: 'Host', value: '10.0.0.2' }, { key: 'Old', keep: true }])
  expect(fixture.entities.documents.get('plug')!.settings).toEqual([{ key: 'Host', value: '10.0.0.2' }, { key: 'Old', value: 'stray' }])
  await vi.waitFor(() => expect(chip(root)).toBe('Saved'))
  button(root, 'Remove setting Old').click()
  expect(chip(root)).toContain('Unsaved changes')
  save(root).click()
  await vi.waitFor(() => expect(fixture.entities.documents.get('plug')!.revision).toBe(3))
  expect(saves(fixture)[1].definition.settings).toEqual([{ key: 'Host', value: '10.0.0.2' }])
})

it('offers to reload or to save anyway when the definition changed elsewhere', async () => {
  const { root, fixture } = setup('/entities/plug', f => f.entities.seed('plug', plug([{ key: 'Host', value: '10.0.0.1' }, { key: 'Password', value: 'stored-secret' }])))
  await ready(root)
  await vi.waitFor(() => expect(control(root, 'Host').value).toBe('10.0.0.1'))
  const elsewhere = (host: string, revision: number) => fixture.callWS({ type: 'hass_sharp/entities', request: { operation: 'save', id: 'plug', expectedRevision: revision, definition: plug([{ key: 'Host', value: host }, { key: 'Password', keep: true }]) } })
  await elsewhere('theirs', 1)
  type(control(root, 'Host'), 'mine')
  save(root).click()
  const dialog = () => document.querySelector<HTMLElement>('dialog[open]')
  await vi.waitFor(() => expect(dialog()?.textContent).toContain('This entity definition changed elsewhere'))
  expect(dialog()!.textContent).not.toContain('document')
  button(dialog()!, 'Save anyway').click()
  await vi.waitFor(() => expect(fixture.entities.documents.get('plug')!.revision).toBe(3))
  expect(saves(fixture).at(-1)).toMatchObject({ expectedRevision: 2, definition: { settings: [{ key: 'Host', value: 'mine' }, { key: 'Password', keep: true }] } })
  expect(fixture.entities.documents.get('plug')!.settings).toEqual([{ key: 'Host', value: 'mine' }, { key: 'Password', value: 'stored-secret' }])
  await vi.waitFor(() => expect(chip(root)).toBe('Saved'))
  // Again, and this time the stored one is kept: mine goes.
  await elsewhere('theirs again', 3)
  type(control(root, 'Host'), 'mine again')
  save(root).click()
  await vi.waitFor(() => expect(dialog()?.textContent).toContain('This entity definition changed elsewhere'))
  button(dialog()!, 'Reload').click()
  await vi.waitFor(() => expect(control(root, 'Host').value).toBe('theirs again'))
  expect(chip(root)).toBe('Saved')
  expect(fixture.entities.documents.get('plug')!.revision).toBe(4)
})

it('asks before leaving unsaved changes', async () => {
  const { root, fixture } = setup('/entities/plug', f => f.entities.seed('plug', plug([{ key: 'Host', value: '10.0.0.1' }])))
  await ready(root)
  await vi.waitFor(() => expect(control(root, 'Host').value).toBe('10.0.0.1'))
  type(control(root, 'Host'), '10.0.0.7')
  link(root, 'Leave').click()
  const dialog = () => document.querySelector<HTMLElement>('dialog[open]')
  await vi.waitFor(() => expect(dialog()?.textContent).toContain('Leave and discard your unsaved changes?'))
  button(dialog()!, 'Keep editing').click()
  expect(page(root)).not.toBeNull()
  expect(control(root, 'Host').value).toBe('10.0.0.7')
  link(root, 'Leave').click()
  await vi.waitFor(() => expect(dialog()).not.toBeNull())
  button(dialog()!, 'Discard changes').click()
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Elsewhere'))
  expect(fixture.entities.documents.get('plug')!.settings).toEqual([{ key: 'Host', value: '10.0.0.1' }])
})

it("shows the save's status, and reads it again while the definition is starting", async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  const { root, fixture } = setup('/entities/new?logic=class', f => { f.entities.controls.activation = 'failed' })
  await ready(root)
  await vi.waitFor(() => expect(pickClass(root, plugType.id)).not.toBeNull())
  pickClass(root, plugType.id).click()
  await vi.waitFor(() => expect(nameInput(root).value).toBe('Shelly plug'))
  save(root).click()
  await vi.waitFor(() => expect(status(root)).toBe('Failed'))
  expect(page(root)!.textContent).toContain('Fixture start failed')
  dispose!(); dispose = undefined; document.body.innerHTML = ''
  // Starting: read again until it runs.
  fixture.entities.controls.activation = 'running'
  const again = setup('/entities/shelly_plug', f => { f.entities.documents.get('shelly_plug')!.status = 'setup' }, fixture)
  await ready(again.root)
  await vi.waitFor(() => expect(status(again.root)).toBe('Starting'))
  const reads = () => fixture.entities.requests.filter(r => r.operation === 'read').length
  const before = reads()
  fixture.entities.documents.get('shelly_plug')!.status = 'running'
  await vi.advanceTimersByTimeAsync(5_100)
  await vi.waitFor(() => expect(status(again.root)).toBe('Running'))
  expect(reads()).toBeGreaterThan(before)
})

it('reads the classes and the definition again after a node library is saved, keeping unsaved changes', async () => {
  const { root, fixture } = setup('/entities/plug', f => f.entities.seed('plug', plug([{ key: 'Host', value: '10.0.0.1' }])))
  await ready(root)
  await vi.waitFor(() => expect(control(root, 'Host').value).toBe('10.0.0.1'))
  const libraryChanged = () => window.dispatchEvent(new Event('hass-sharp-libraries-changed'))
  const elsewhere = (host: string, revision: number) => fixture.callWS({ type: 'hass_sharp/entities', request: { operation: 'save', id: 'plug', expectedRevision: revision, definition: plug([{ key: 'Host', value: host }]) } })
  // Nothing unsaved: the new revision and the class's new setting.
  await elsewhere('10.0.0.5', 1)
  fixture.entities.types[0].settings.push({ key: 'Channel', label: 'Channel', description: null, type: 'integer', secret: false, options: [] })
  libraryChanged()
  await vi.waitFor(() => expect(control(root, 'Host').value).toBe('10.0.0.5'))
  await vi.waitFor(() => expect(control(root, 'Channel')).not.toBeNull())
  expect(chip(root)).toBe('Saved')
  // Unsaved changes stay; the classes and the status are new.
  type(control(root, 'Host'), 'mine')
  fixture.entities.controls.activation = 'failed'
  await elsewhere('theirs', 2)
  fixture.entities.types[0].settings[0].label = 'Address'
  libraryChanged()
  await vi.waitFor(() => expect(status(root)).toBe('Failed'))
  await vi.waitFor(() => expect(control(root, 'Address').value).toBe('mine'))
  expect(chip(root)).toContain('Unsaved changes')
})

it('turns the definition off and deletes it from its page', async () => {
  const { root, fixture, history } = setup('/entities/plug', f => f.entities.seed('plug', plug([{ key: 'Host', value: '10.0.0.1' }])))
  await ready(root)
  await vi.waitFor(() => expect(status(root)).toBe('Running'))
  const toggle = root.querySelector<HTMLInputElement>('input[aria-label="Plug on"]')!
  expect(toggle.closest('label')!.title).toBe('On: its entities are in Home Assistant and its steps run')
  toggle.click()
  await vi.waitFor(() => expect(status(root)).toBe('Off'))
  expect(fixture.entities.requests).toContainEqual({ operation: 'set_enabled', id: 'plug', enabled: false })
  expect(fixture.entities.documents.get('plug')!.enabled).toBe(false)
  button(root, 'Delete entity definition').click()
  const dialog = () => document.querySelector<HTMLElement>('dialog[open]')
  await vi.waitFor(() => expect(dialog()?.textContent).toContain('Plug and its entities are removed from Home Assistant'))
  button(dialog()!, 'Delete').click()
  await vi.waitFor(() => expect(history.get()).toBe('/entities'))
  expect(fixture.entities.documents.size).toBe(0)
})

it("gives a single entity a device only when asked, and overrides a device class's details", async () => {
  const { root, fixture } = setup('/entities/new?logic=class')
  const view = await ready(root)
  await vi.waitFor(() => expect(pickClass(root, thermometerType.id)).not.toBeNull())
  // A single entity: a device only when asked for.
  pickClass(root, thermometerType.id).click()
  await vi.waitFor(() => expect(view.querySelector('section[aria-label="Device"] input[role="switch"]')).not.toBeNull())
  const single = view.querySelector('section[aria-label="Device"]')!
  expect(single.querySelector('label')!.textContent).toBe('Group this entity as a device')
  expect(single.querySelector<HTMLInputElement>('input[role="switch"]')!.checked).toBe(false)
  single.querySelector<HTMLInputElement>('input[role="switch"]')!.click()
  await vi.waitFor(() => expect(control(root, 'Model')).not.toBeNull())
  enter(control(root, 'Model'), 'T1')
  // A device: its fields override what the class sets, with no switch.
  pickClass(root, plugType.id).click()
  await vi.waitFor(() => expect(view.querySelector('section[aria-label="Device"] input[role="switch"]')).toBeNull())
  const device = view.querySelector('section[aria-label="Device"]')!
  expect(device.textContent).toContain('Leave a field empty to use what the class sets.')
  expect(control(root, 'Manufacturer').placeholder).toBe('Set by the class')
  expect(control(root, 'Model').value).toBe('T1')
  enter(control(root, 'Manufacturer'), 'Acme')
  save(root).click()
  await vi.waitFor(() => expect(saves(fixture)).toHaveLength(1))
  expect(saves(fixture)[0].definition).toEqual({ name: 'Shelly plug', device: { model: 'T1', manufacturer: 'Acme' }, settings: [], logic: { kind: 'class', type: plugType.id } })
  await vi.waitFor(() => expect(fixture.entities.documents.get('shelly_plug')).toBeDefined())
})

it('explains where classes come from when the node libraries have none', async () => {
  const { root } = setup('/entities/new?logic=class', undefined, visualFixture())
  const view = await ready(root)
  await vi.waitFor(() => expect(view.textContent).toContain('No classes yet. Write a class that derives from SharpDevice or SharpEntity in a node library, and it appears here.'))
  expect(link(root, 'Open the node library').getAttribute('href')).toBe('/libraries')
  expect(view.querySelector('section[aria-label="Settings"]')).toBeNull()
})
