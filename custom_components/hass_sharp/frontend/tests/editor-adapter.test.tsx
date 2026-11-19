import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { MemoryRouter, Route, createMemoryHistory } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { Dialog } from '../src/components/Dialog'
import { EditorView } from '../src/views/VisualAutomationView'
import type { EditorAdapter, EditorDocument } from '../src/visual/adapter'
import type { VisualPreview } from '../src/visual/api'
import { graphDocument } from '../src/visual/contract'
import type { VisualGraph } from '../src/visual/graph'
import { visualFixture } from './visual-fixture'
import { automationAdapter } from '../src/visual/automationAdapter'
import { entityAdapter } from '../src/entities/adapter'

// The editor driven by a second kind of document: a stub adapter with its own backend, ids, routes and words. Nothing
// of the automation's may show (its words, its routes) and no automation request may be sent.

/** A widget: the editor's document plus a member of its own, handed back to the stub's overview. */
type Widget = EditorDocument & { spins: number }
class WidgetError extends Error {
  constructor(public code: string, public detail: unknown) { super(typeof detail === 'string' ? detail : `Widget request failed (${code}).`) }
}
const forbidden = ['automation', 'Automation', '/visual/', '/automations']
const clean = (text: string) => forbidden.filter(word => text.includes(word))

/** The `n`th gear: an Entity node, a binary sensor. */
const gear = (n: number) => ({ id: `gear_${n}`, kind: 'entity', config: { key: `gear_${n}`, domain: 'binary_sensor', name: `Gear ${n}` }, position: { x: 60, y: 200 * n } })
/**
 * A widget graph: an entity graph (it carries a definition) with gears as its Entity nodes, and a secret setting. Its
 * definition lists no entity, as an editor graph's does while its entities are its Entity nodes'.
 */
function widgetGraph(name: string, gears = 1, token: string | null = 'hunter2'): VisualGraph {
  return {
    version: 5, name, nodes: Array.from({ length: gears }, (_, i) => gear(i + 1)), edges: [],
    definition: {
      entities: [],
      settings: [{ key: 'token', type: 'string', value: token, secret: true } as { key: string; type: string }],
    },
  }
}
const gears = (graph: VisualGraph) => graph.nodes.filter(node => node.kind === 'entity').length
const addGear = (graph: VisualGraph): VisualGraph => ({ ...graph, nodes: [...graph.nodes, gear(gears(graph) + 1)] })
/** What the stub's drafts keep: the graph without its secret values. */
const withoutSecrets = (graph: VisualGraph): VisualGraph => ({
  ...graph, definition: { ...graph.definition!, settings: graph.definition!.settings.map(setting => ({ ...setting, value: null })) },
})

/** The stub's in-memory backend, with revisions as a real store keeps them. */
function widgetBackend() {
  const documents = new Map<string, Widget>()
  const calls: unknown[][] = []
  const store = (id: string, graph: VisualGraph, revision: number, enabled = true): Widget => {
    const doc: Widget = { id, revision, graph: structuredClone(graph), source: `// widget ${id}`, sourceMap: [], automationId: `widget:${id}`, enabled,
      active: enabled, currentRevisionActive: true, activationError: null, spins: revision * 10 }
    documents.set(id, doc)
    return structuredClone(doc)
  }
  const problems: VisualPreview['diagnostics'] = []
  const adapter: EditorAdapter<Widget> = {
    document: 'entity',
    read: async id => {
      calls.push(['read', id])
      const doc = documents.get(id)
      if (!doc) throw new WidgetError('not_found', 'Widget not found')
      return structuredClone(doc)
    },
    preview: async graph => {
      calls.push(['preview', graph.name])
      return { valid: true, source: '// widget source', sourceMap: [], diagnostics: structuredClone(problems) }
    },
    save: async (id, graph, expectedRevision, options) => {
      calls.push(['save', id, structuredClone(graph), expectedRevision, options])
      const current = documents.get(id)
      if ((current?.revision ?? 0) !== expectedRevision) throw new WidgetError('conflict', 'Revision conflict; reload the current widget')
      const document = store(id, graph, expectedRevision + 1, current ? current.enabled : options?.enabled ?? true)
      return { document, active: true, activationError: null }
    },
    setEnabled: async (id, enabled, options) => {
      calls.push(['setEnabled', id, enabled, !!options?.queryClient])
      documents.get(id)!.enabled = enabled
      return enabled
    },
    apiError: error => (error instanceof WidgetError ? error : undefined),
    // Taken ids are known only by asking: the id comes later than the call.
    newId: async graph => {
      await new Promise(resolve => setTimeout(resolve, 5))
      const base = graph.name.toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_|_$/g, '')
      let id = base, n = 1
      while (documents.has(id)) id = `${base}_${++n}`
      calls.push(['newId', graph.name, id])
      return id
    },
    starter: name => widgetGraph(name),
    draft: withoutSecrets,
    listHref: '/widgets',
    editorHref: id => `/widgets/${id}`,
    runHref: (id, runId) => `/widgets/${id}/runs/${runId}`,
    queryKeys: [['widget-list'], ['widget-home', 'all']],
    texts: {
      editorLabel: 'Widget editor', canvasLabel: 'Widget canvas', untitled: 'Untitled widget', nameLabel: 'Widget name', back: 'Back to widgets',
      overviewLabel: 'Widget overview', problemsHeading: 'This widget', runsEmpty: 'Save this widget to see its spins here.',
      exportFileName: 'widget.graph.json', enabledOn: 'On: the widget turns', enabledOff: 'Off: the widget stands still', conflictMessage: 'This widget changed elsewhere while you had unsaved widget changes here.',
      conflictSaveTitle: 'The saved widget changed elsewhere: choose what to keep', conflictTitle: 'This widget changed elsewhere',
      conflictElsewhere: 'Someone saved this widget again while you were changing it.',
      conflictDraft: time => `Your widget changes from ${time} came before the widget was saved again.`, copyNoun: 'a new widget',
    },
    runGroup: context => (
      <button type="button" class="widget-spin" disabled={context.busy} onClick={() => context.notice({ text: `Spun ${context.document?.id}` })}>
        Spin {context.document?.id ?? 'nothing'}
      </button>
    ),
    overview: context => (
      <div class="widget-overview">
        <p class="widget-gears">Gears: {gears(context.graph())}</p>
        <p class="widget-saved">{context.saved ? `Saved with ${context.document?.spins} spins` : 'Never saved'}</p>
        <p class="widget-on">{context.enabled === undefined ? 'No switch yet' : context.enabled ? 'Spinning' : 'Still'}</p>
        <p class="widget-problems">{context.diagnostics.length} notes · {Object.keys(context.catalog.nodes).length > 0 ? 'catalog' : 'no catalog'}</p>
        <button type="button" onClick={() => context.commit(addGear(context.graph()))}>Add gear</button>
      </div>
    ),
    help: props => <Dialog open={props.open} title="Widget help" width="large" onClose={props.onClose}><p>Widgets turn gears.</p></Dialog>,
  }
  return { adapter, documents, calls, store, problems }
}

let dispose: (() => void) | undefined
let client: QueryClient | undefined
afterEach(() => { dispose?.(); dispose = undefined; client?.clear(); document.body.innerHTML = ''; vi.restoreAllMocks(); vi.unstubAllGlobals() })

/** Mounts the widget editor page at `path`, on the shared catalog; any automation request is recorded and refused. */
function setup(path: string, backend = widgetBackend()) {
  const fixture = visualFixture()
  const automationCalls: unknown[] = []
  const callWS = async (message: any) => {
    if (message.type === 'hass_sharp/visual' || message.type === 'hass_sharp/run_now') {
      automationCalls.push(message)
      throw new Error('an automation request')
    }
    if (message.type === 'hass_sharp/run_history') {
      const runs = message.automationId === 'widget:spinner'
        ? [{ runId: 'run-1', automationId: 'widget:spinner', revision: 'r', action: 'Spin', actionLabel: 'Spin', triggerNodeId: null, visualId: 'spinner',
          visualRevision: 2, cause: 'Manual', trigger: null, plannedUtc: null, receivedUtc: '2026-11-18T10:00:00Z', startUtc: '2026-11-18T10:00:00Z',
          endUtc: '2026-11-18T10:00:01Z', durationMs: 1000, status: 'Succeeded', error: null, hasTrace: true }]
        : []
      return { version: 2, offset: message.offset ?? 0, limit: message.limit ?? 20, runs }
    }
    return fixture.callWS(message)
  }
  const host = document.createElement('div'); host.className = 'hs-app'; document.body.append(host)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const invalidated = vi.spyOn(client, 'invalidateQueries')
  const history = createMemoryHistory(); history.set({ value: path })
  const page = () => <EditorView adapter={backend.adapter} />
  dispose = render(() => <QueryClientProvider client={client!}><HassCtx.Provider value={{ ...fixture, callWS, user: { id: 'u1', is_admin: true } } as any}>
    <MemoryRouter history={history}>
      <Route path="/widgets/:id" component={page} />
      <Route path="/widgets" component={() => <h1>Widgets</h1>} />
    </MemoryRouter></HassCtx.Provider></QueryClientProvider>, host)
  return { host, backend, history, automationCalls, invalidated }
}
const button = (root: Element, text: string) => {
  const found = [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text || b.getAttribute('aria-label') === text)
  expect(found, text).toBeDefined()
  return found!
}
const chip = (root: Element) => root.querySelector('.hs-save-chip__text')!.textContent
const saved = (root: Element, revision: number) => vi.waitFor(() => {
  expect(chip(root)).toBe('Saved')
  expect(root.querySelector('.hs-save-chip')!.getAttribute('title')).toBe(`Saved revision ${revision}`)
})
const nameField = (root: Element) => root.querySelector<HTMLInputElement>('input[aria-label="Widget name"]')
const gearsShown = (root: Element) => root.querySelector('[aria-label="Widget overview"] .widget-gears')?.textContent
const dialog = (root: Element) => root.querySelector<HTMLDialogElement>('dialog[open]')
const tab = (root: Element, name: string) => [...root.querySelectorAll<HTMLButtonElement>('.hs-drawer [role="tab"]')].find(b => b.textContent?.startsWith(name))!
const more = (root: Element, item: string) => {
  button(root, 'More actions').click()
  const choice = [...root.querySelectorAll<HTMLButtonElement>('[role="menu"] [role="menuitem"]')].find(b => b.textContent === item)
  expect(choice, item).toBeDefined()
  choice!.click()
}
/** Everything rendered, attributes included, says nothing of automations. */
const expectClean = (root: Element) => {
  // The step library's descriptions are the node catalog's (the shared catalog's Trigger says "Start the automation…"),
  // not the editor's words.
  const copy = root.cloneNode(true) as Element
  copy.querySelectorAll('.hs-lib__text small').forEach(description => description.remove())
  expect(clean(copy.innerHTML)).toEqual([])
}
/** A saved widget open at `/widgets/spinner`, revision 2. */
async function openSpinner(backend = widgetBackend()) {
  backend.store('spinner', widgetGraph('Spinner'), 2)
  const page = setup('/widgets/spinner', backend)
  await saved(page.host, 2)
  await vi.waitFor(() => expect(gearsShown(page.host)).toBe('Gears: 1'))
  return page
}

it('starts from the adapter: its kind of graph, words, overview, run group and help', async () => {
  const backend = widgetBackend()
  expect(graphDocument(backend.adapter.starter('x'))).toBe(backend.adapter.document)
  backend.problems.push({ nodeId: null, message: 'Gear 1 needs oil', severity: 2, startLine: null, startColumn: null })
  const { host, automationCalls } = await openSpinner(backend)
  expect(host.querySelector('[aria-label="Widget editor"]')).not.toBeNull()
  expect(host.querySelector('[aria-label="Widget canvas"]')).not.toBeNull()
  expect(nameField(host)!.value).toBe('Spinner')
  expect(button(host, 'Back to widgets')).toBeDefined()
  const overview = host.querySelector('[aria-label="Widget overview"]')!
  expect(overview.querySelector('.widget-saved')!.textContent).toBe('Saved with 20 spins')
  expect(overview.querySelector('.widget-on')!.textContent).toBe('Spinning')
  await vi.waitFor(() => expect(overview.querySelector('.widget-problems')!.textContent).toBe('1 notes · catalog'))
  // The run group: the stub's control beside the on/off switch, with its context.
  button(host, 'Spin spinner').click()
  await vi.waitFor(() => expect(host.querySelector('.hs-topbar__notice')?.textContent).toBe('Spun spinner'))
  // Problems about no step are the widget's own.
  tab(host, 'Problems').click()
  await vi.waitFor(() => expect([...host.querySelectorAll('.hs-problems__step')].map(h => h.textContent)).toContain('This widget'))
  // The help is the stub's.
  more(host, 'Help')
  expect(dialog(host)!.textContent).toContain('Widget help')
  expect(dialog(host)!.textContent).toContain('Widgets turn gears.')
  button(dialog(host)!, 'Close dialog').click()
  // Export graph names the file as the adapter says.
  const named: string[] = [], files: Blob[] = []
  vi.spyOn(URL, 'createObjectURL').mockImplementation(file => { files.push(file as Blob); return 'blob:widget' })
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {})
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { named.push(this.download) })
  more(host, 'Export graph')
  expect(named).toEqual(['widget.graph.json'])
  // The file holds the graph as the adapter's draft gives it: no secret value.
  const exported = JSON.parse(await files[0]!.text())
  expect(exported).toEqual(withoutSecrets(widgetGraph('Spinner')))
  expectClean(host)
  expect(automationCalls).toEqual([])
})

it('edits through the overview\'s context with undo, saves through the adapter and makes its queries stale', async () => {
  const { host, backend, invalidated, automationCalls } = await openSpinner()
  // Opening reads it, and the first catalog answer checks it again.
  expect(backend.calls.filter(call => call[0] === 'read').map(call => call[1])).toEqual(['spinner', 'spinner'])
  button(host, 'Add gear').click()
  expect(gearsShown(host)).toBe('Gears: 2')
  expect(chip(host)).toBe('Unsaved changes')
  button(host, 'Undo').click()
  expect(gearsShown(host)).toBe('Gears: 1')
  expect(chip(host)).toBe('Saved')
  button(host, 'Redo').click()
  expect(gearsShown(host)).toBe('Gears: 2')
  // The draft leaves the secret out; the graph being edited keeps it.
  await vi.waitFor(() => expect(localStorage.getItem('hass-sharp:draft:u1/entity:spinner')).not.toBeNull(), { timeout: 2000 })
  expect(localStorage.getItem('hass-sharp:draft:u1/entity:spinner')).not.toContain('hunter2')
  await vi.waitFor(() => expect(button(host, 'Save').disabled).toBe(false))
  button(host, 'Save').click()
  await saved(host, 3)
  const save = backend.calls.find(call => call[0] === 'save')!
  expect(save[1]).toBe('spinner')
  expect(gears(save[2] as VisualGraph)).toBe(2)
  expect(save[3]).toBe(2)
  expect((save[2] as VisualGraph).definition!.settings).toEqual([{ key: 'token', type: 'string', value: 'hunter2', secret: true }])
  expect(invalidated.mock.calls.map(([filters]) => filters?.queryKey)).toEqual([['widget-list'], ['widget-home', 'all']])
  // The saved document is the one the overview is given.
  expect(host.querySelector('.widget-saved')!.textContent).toBe('Saved with 30 spins')
  expectClean(host)
  expect(automationCalls).toEqual([])
})

it('gives a new widget the adapter\'s id, which may come later, and moves to its own address', async () => {
  const backend = widgetBackend()
  backend.store('fresh_widget', widgetGraph('Fresh widget'), 1)
  const { host, history, automationCalls } = setup('/widgets/new?name=Fresh widget', backend)
  await vi.waitFor(() => expect(nameField(host)!.value).toBe('Fresh widget'))
  expect(host.querySelector('.widget-saved')!.textContent).toBe('Never saved')
  expect(host.querySelector('.widget-on')!.textContent).toBe('No switch yet')
  tab(host, 'Runs').click()
  await vi.waitFor(() => expect(host.textContent).toContain('Save this widget to see its spins here.'))
  await vi.waitFor(() => expect(button(host, 'Save').disabled).toBe(false))
  button(host, 'Save').click()
  await saved(host, 1)
  expect(backend.calls.find(call => call[0] === 'newId')).toEqual(['newId', 'Fresh widget', 'fresh_widget_2'])
  const save = backend.calls.find(call => call[0] === 'save')!
  expect([save[1], save[3], save[4]]).toEqual(['fresh_widget_2', 0, undefined])
  await vi.waitFor(() => expect(history.get()).toBe('/widgets/fresh_widget_2'))
  expect(host.querySelector('.widget-saved')!.textContent).toBe('Saved with 10 spins')
  // An untitled widget takes the adapter's name.
  dispose!(); dispose = undefined; document.body.innerHTML = ''
  const untitled = setup('/widgets/new', backend)
  await vi.waitFor(() => expect(nameField(untitled.host)!.value).toBe('Untitled widget'))
  expectClean(untitled.host)
  expect(untitled.automationCalls).toEqual([])
})

/** The open widget with a local change, saved again elsewhere meanwhile: Save meets the conflict and its dialog opens. */
async function conflicted() {
  const page = await openSpinner()
  button(page.host, 'Add gear').click()
  page.backend.store('spinner', widgetGraph('Spinner elsewhere', 3), 3)
  await vi.waitFor(() => expect(button(page.host, 'Save').disabled).toBe(false))
  button(page.host, 'Save').click()
  await vi.waitFor(() => expect(dialog(page.host)?.textContent).toContain('This widget changed elsewhere'))
  const text = dialog(page.host)!.textContent!
  expect(text).toContain('Someone saved this widget again while you were changing it.')
  expect(text).toContain('Save as a copy saves them as a new widget, turned off')
  expect(button(page.host, 'Save').getAttribute('title')).toBe('The saved widget changed elsewhere: choose what to keep')
  expectClean(page.host)
  return page
}

it('keeps my version over a widget saved elsewhere: it reads the latest through the adapter and saves over it', async () => {
  const { host, backend, automationCalls } = await conflicted()
  button(dialog(host)!, 'Keep my version').click()
  await saved(host, 4)
  expect(backend.calls.filter(call => call[0] === 'read').map(call => call[1])).toEqual(['spinner', 'spinner', 'spinner'])
  const save = backend.calls.filter(call => call[0] === 'save').at(-1)!
  expect([save[1], gears(save[2] as VisualGraph), save[3]]).toEqual(['spinner', 2, 3])
  expect(gearsShown(host)).toBe('Gears: 2')
  expectClean(host)
  expect(automationCalls).toEqual([])
})

it('saves a conflicting change as a copy with the adapter\'s id, turned off, and moves to it', async () => {
  const { host, backend, history, automationCalls } = await conflicted()
  button(dialog(host)!, 'Save as a copy').click()
  await saved(host, 1)
  expect(backend.calls.find(call => call[0] === 'newId')).toEqual(['newId', 'Spinner (copy)', 'spinner_copy'])
  const save = backend.calls.filter(call => call[0] === 'save').at(-1)!
  expect([save[1], (save[2] as VisualGraph).name, gears(save[2] as VisualGraph), save[3], save[4]]).toEqual(['spinner_copy', 'Spinner (copy)', 2, 0, { enabled: false }])
  await vi.waitFor(() => expect(history.get()).toBe('/widgets/spinner_copy'))
  expect(nameField(host)!.value).toBe('Spinner (copy)')
  expect(backend.documents.get('spinner')!.revision).toBe(3)
  expectClean(host)
  expect(automationCalls).toEqual([])
})

it('discards a conflicting change: the latest widget opens', async () => {
  const { host, automationCalls } = await conflicted()
  button(dialog(host)!, 'Discard my changes').click()
  await saved(host, 3)
  expect(nameField(host)!.value).toBe('Spinner elsewhere')
  expect(gearsShown(host)).toBe('Gears: 3')
  expectClean(host)
  expect(automationCalls).toEqual([])
})

it('keeps a draft as the adapter\'s draft gives it, under its own kind\'s key, and restores it', async () => {
  const backend = widgetBackend()
  const first = await openSpinner(backend)
  button(first.host, 'Add gear').click()
  const key = 'hass-sharp:draft:u1/entity:spinner'
  await vi.waitFor(() => expect(localStorage.getItem(key)).not.toBeNull(), { timeout: 2000 })
  // An automation with the same id would use the key without the kind: it is left alone.
  expect(localStorage.getItem('hass-sharp:draft:u1/spinner')).toBeNull()
  const stored = JSON.parse(localStorage.getItem(key)!)
  expect(stored.graph).toEqual(withoutSecrets(addGear(widgetGraph('Spinner'))))
  expect(stored.revision).toBe(2)
  expect(localStorage.getItem(key)).not.toContain('hunter2')
  // The panel goes away without asking; opening the widget again offers the draft.
  dispose!(); dispose = undefined; document.body.innerHTML = ''
  const { host, automationCalls } = setup('/widgets/spinner', backend)
  await vi.waitFor(() => expect(dialog(host)?.textContent).toContain('Restore unsaved changes from'))
  button(dialog(host)!, 'Restore').click()
  await vi.waitFor(() => expect(gearsShown(host)).toBe('Gears: 2'))
  expect(chip(host)).toBe('Unsaved changes')
  button(host, 'Undo').click()
  expect(gearsShown(host)).toBe('Gears: 1')
  expectClean(host)
  expect(automationCalls).toEqual([])
})

it('meets a draft made on an older revision as a conflict, in the adapter\'s words', async () => {
  const backend = widgetBackend()
  backend.store('spinner', widgetGraph('Spinner'), 2)
  localStorage.setItem('hass-sharp:draft:u1/entity:spinner', JSON.stringify({ graph: withoutSecrets(widgetGraph('Spinner', 4)), revision: 1, name: 'Spinner', at: Date.now() }))
  const { host, automationCalls } = setup('/widgets/spinner', backend)
  await vi.waitFor(() => expect(dialog(host)?.textContent).toContain('This widget changed elsewhere'))
  expect(dialog(host)!.textContent).toMatch(/Your widget changes from .+ came before the widget was saved again\./)
  expect(host.querySelector('.hs-visual-alert[role="alert"]')!.textContent).toContain('This widget changed elsewhere while you had unsaved widget changes here.')
  expect(gearsShown(host)).toBe('Gears: 4')
  expectClean(host)
  expect(automationCalls).toEqual([])
})

it('turns the widget off through the adapter, and links its runs to the adapter\'s run pages', async () => {
  const { host, backend, automationCalls } = await openSpinner()
  const toggle = host.querySelector<HTMLInputElement>('input[role="switch"][aria-label="Spinner on"]')!
  const tip = () => toggle.closest('label')!.getAttribute('title')
  expect(tip()).toBe('On: the widget turns')
  toggle.checked = false
  toggle.dispatchEvent(new Event('change', { bubbles: true }))
  await vi.waitFor(() => expect(backend.calls).toContainEqual(['setEnabled', 'spinner', false, true]))
  await vi.waitFor(() => expect(host.querySelector('.widget-on')!.textContent).toBe('Still'))
  expect(tip()).toBe('Off: the widget stands still')
  tab(host, 'Runs').click()
  await vi.waitFor(() => expect(host.querySelector('a.hs-run-trace')).not.toBeNull())
  expect(host.querySelector('a.hs-run-trace')!.getAttribute('href')).toBe('/widgets/spinner/runs/run-1')
  expectClean(host)
  expect(automationCalls).toEqual([])
})

it('encodes the id in the editor link of the automation and the entity definition, as in their run links', () => {
  const hass = { callWS: async () => ({}) }
  expect(automationAdapter(hass).editorHref('visual-a b/c?d')).toBe('/visual/visual-a%20b%2Fc%3Fd')
  expect(automationAdapter(hass).editorHref('visual-0f3c')).toBe('/visual/visual-0f3c')
  expect(entityAdapter(hass).editorHref('a b/c?d')).toBe('/entities/a%20b%2Fc%3Fd')
  expect(automationAdapter(hass).runHref('visual-a b', 'r/1')).toBe('/visual/visual-a%20b/runs/r%2F1')
})

it('the entity definition\'s adapter answers its saved entities\' keys and the entity ids Home Assistant gave them', () => {
  const adapter = entityAdapter({ callWS: async () => ({}) })
  const context = (document: unknown) => ({ document }) as unknown as Parameters<NonNullable<typeof adapter.savedEntities>>[0]
  expect(adapter.savedEntities!(context(undefined))).toBeUndefined()
  const saved = { id: 'desk', entities: [{ key: 'lamp', domain: 'light' }, { key: 'fan', domain: 'switch' }], registeredEntities: [] }
  // Running: its live entities; one Home Assistant has no id for yet is left out.
  expect(adapter.savedEntities!(context({ ...saved, liveEntities: [{ key: 'lamp', entityId: 'light.desk_lamp' }, { key: 'fan', entityId: null }] })))
    .toEqual({ keys: ['lamp', 'fan'], entityIds: { lamp: 'light.desk_lamp' } })
  // Off or failed: the entity registry's, by unique id.
  expect(adapter.savedEntities!(context({ ...saved, liveEntities: [], registeredEntities: [{ uniqueId: 'desk:fan', entityId: 'switch.desk_fan' }, { uniqueId: 'other:lamp', entityId: 'light.other' }] })))
    .toEqual({ keys: ['lamp', 'fan'], entityIds: { fan: 'switch.desk_fan' } })
  // The automation editor has no entities.
  expect(automationAdapter({ callWS: async () => ({}) }).savedEntities).toBeUndefined()
})
