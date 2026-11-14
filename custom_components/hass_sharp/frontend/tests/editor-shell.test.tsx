import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { VisualEditor as RealVisualEditor } from '../src/visual/VisualEditor'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { addNode, connectNodes, createGraph, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import { connectData, nodeCatalog } from '../src/visual/contract'
import { soon } from '../src/visual/soon'
import { manualTriggers, RunNowButton } from '../src/visual/RunNow'
import { acceptedRun } from '../src/components/runDispatch'

import { HassCtx } from '../src/context'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { visualFixture } from './visual-fixture'
const VisualEditor = (props: { name: string }) => <QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={ { ...visualFixture(), user: { is_admin: true } } as any}><RealVisualEditor {...props} /></HassCtx.Provider></QueryClientProvider>

let dispose: (() => void) | undefined
afterEach(async () => {
  dispose?.(); dispose = undefined; vi.restoreAllMocks(); vi.unstubAllGlobals(); document.body.innerHTML = ''
  // D3 clears its click suppression on the next macrotask after a drag.
  await new Promise(resolve => setTimeout(resolve, 0))
})
function root() { const root = document.createElement('div'); root.className = 'hs-app'; document.body.append(root); return root }
function button(root: Element, label: string) {
  const result = [...root.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent?.trim() === label || button.getAttribute('aria-label') === label)
  expect(result, label).toBeDefined(); return result!
}
function input(root: Element, label: string, value: string) {
  const control = root.querySelector<HTMLInputElement>(`[aria-label="${label}"]`) ?? root.querySelector<HTMLInputElement>(`#${[...root.querySelectorAll('label')].find(l => l.textContent === label)?.htmlFor}`)
  expect(control, label).not.toBeNull(); control!.value = value; control!.dispatchEvent(new Event('change', { bubbles: true })); control!.dispatchEvent(new Event('input', { bubbles: true }))
}

it('uses keyboard history only outside text controls, and keeps JSON available', () => {
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  button(host, 'Add step').click(); button(host, 'Create Delay').click()
  const label = host.querySelector<HTMLInputElement>('input[aria-label="Node label"]')!
  label.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true }))
  expect(label.value).toBe('Delay')
  host.querySelector('[aria-label="Visual automation editor"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true, cancelable: true }))
  expect(button(host, 'Redo').disabled).toBe(false)
  button(host, 'Graph JSON').click()
  expect(host.querySelector('pre[aria-label="Graph JSON"]')?.textContent).toContain('"version": 5')
})
it('updates the draft on input before blur and warns before the page is closed', () => {
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  const label = host.querySelector<HTMLInputElement>('input[aria-label="Node label"]')!
  label.value = 'Door opens'; label.dispatchEvent(new Event('input', { bubbles: true }))
  button(host, 'Graph JSON').click()
  expect(host.querySelector('pre')?.textContent).toContain('Door opens')
  const closing = new Event('beforeunload', { cancelable: true })
  window.dispatchEvent(closing)
  expect(closing.defaultPrevented).toBe(true)
})
it('keeps canvas keyboard shortcuts inside the Home Assistant panel', () => {
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  const shortcut = vi.fn(); window.addEventListener('keydown', shortcut)
  try {
    host.querySelector('[data-id="trigger-1"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'c', bubbles: true }))
    expect(shortcut).not.toHaveBeenCalled()
  } finally { window.removeEventListener('keydown', shortcut) }
})
it('blocks Home Assistant native ancestor shortcuts while keeping node keyboard movement', () => {
  const host = root(); const ancestor = vi.fn(); host.addEventListener('keydown', ancestor)
  dispose = render(() => <VisualEditor name="Heating" />, host)
  const node = host.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  node.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }))
  node.dispatchEvent(new KeyboardEvent('keyup', { key: 'ArrowRight', bubbles: true }))
  expect(ancestor).not.toHaveBeenCalled()
  button(host, 'Graph JSON').click()
  expect(JSON.parse(host.querySelector('pre')!.textContent!).nodes[0].position.x).toBeGreaterThan(60)
})
/** Picks an item of the top bar's "⋯" menu. */
function more(root: Element, item: string) {
  button(root, 'More actions').click()
  const choice = [...root.querySelectorAll<HTMLButtonElement>('[role="menu"] [role="menuitem"]')].find(b => b.textContent === item)
  expect(choice, item).toBeDefined(); choice!.click()
}
const drawerTab = (root: Element, name: string) => [...root.querySelectorAll<HTMLButtonElement>('.hs-drawer [role="tab"]')].find(b => b.textContent?.startsWith(name))!
const chip = (root: Element) => root.querySelector('.hs-save-chip__text')!
/** The save chip's explanation (its tooltip and hidden text). */
const chipTip = (root: Element) => root.querySelector('.hs-save-chip')!.getAttribute('title')
const editorRoot = (root: Element) => root.querySelector<HTMLElement>('[aria-label="Visual automation editor"]')!
const key = (target: Element, key: string, init: KeyboardEventInit = {}) => {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }); target.dispatchEvent(event); return event
}
/** Mounts the editor with its own fixture (and optionally a callWS in front of it). */
function mount(name = 'Heating', options: { callWS?: (message: any, fixture: ReturnType<typeof visualFixture>) => unknown; saved?: VisualGraph } = {}) {
  const host = root(), fixture = visualFixture()
  if (options.saved) fixture.documents.set('saved', {
    version: 1, id: 'saved', revision: 1, graph: options.saved, source: '// fixture', sourceMap: [], automationId: 'visual:saved', currentRevisionActive: true, catalogGeneration: 'saved-fixture',
  })
  const callWS = (message: any) => options.callWS?.(message, fixture) ?? fixture.callWS(message)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{ ...fixture, callWS, user: { is_admin: true } } as any}>
    <RealVisualEditor name={name} documentId={options.saved ? 'saved' : undefined} />
  </HassCtx.Provider></QueryClientProvider>, host)
  return { host, fixture, client }
}
/** A viewport whose width can cross 900px during a test. */
function resizableScreen(narrow: boolean) {
  const lists: { matches: boolean; listeners: Set<() => void> }[] = []
  vi.stubGlobal('matchMedia', (query: string) => {
    const list = { matches: query.includes('max-width') && narrow, media: query, listeners: new Set<() => void>(),
      addEventListener(_: string, listener: () => void) { this.listeners.add(listener) }, removeEventListener(_: string, listener: () => void) { this.listeners.delete(listener) } }
    if (query.includes('max-width')) lists.push(list)
    return list
  })
  return (next: boolean) => { for (const list of lists) { list.matches = next; list.listeners.forEach(listener => listener()) } }
}
const inspectorOf = (host: Element) => host.querySelector<HTMLElement>('aside[aria-label="Node inspector"]')!
const titleInput = (host: Element, id: string) => inspectorOf(host).querySelector<HTMLInputElement>(`.hs-insp-node[data-node-id="${id}"] input[aria-label="Node label"]`)
const settle = () => new Promise(resolve => setTimeout(resolve, 0))
/** A viewport narrower than 900 px, where the inspector is a two-stage sheet. */
const narrowScreen = () => vi.stubGlobal('matchMedia', (query: string) => ({
  matches: query.includes('max-width'), media: query, addEventListener() {}, removeEventListener() {},
}))
const json = (host: Element) => { button(host, 'Graph JSON').click(); return JSON.parse(host.querySelector('pre[aria-label="Graph JSON"]')!.textContent!) }
const focusNode = (host: Element, id: string) => host.querySelector(`[data-id="${id}"]`)!.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
function dragEvent(type: string, data: DataTransfer, clientX = 0, clientY = 0) {
  const event = new MouseEvent(type, { bubbles: true, cancelable: true, clientX, clientY })
  Object.defineProperty(event, 'dataTransfer', { value: data })
  return event
}

it('opens visual help from the "⋯" menu without changing the graph, and gives focus back to the menu', async () => {
  const host = root(); dispose = render(() => <VisualEditor name="Help" />, host)
  expect(host.querySelector('button[aria-label="Visual automation help"]')).toBeNull()
  more(host, 'Help')
  expect(host.querySelector('dialog[open]')?.textContent).toContain('Build and connect steps')
  expect(host.querySelector('dialog[open]')?.textContent).not.toContain('Advanced C#')
  expect(button(host, 'Undo').disabled).toBe(true)
  button(host.querySelector('dialog[open]')!, 'Close dialog').click()
  expect(host.querySelector('dialog[open]')).toBeNull()
  expect(document.activeElement).toBe(button(host, 'More actions'))
})

it('fetches current entity suggestions when reopening an editor with a fresh cache', async () => {
  const host = root()
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  // The shared registry query (src/registry.ts) caches the whole get_entities reply.
  client.setQueryData(['hass-entities'], { entities: [{ entity_id: 'sensor.old' }], areas: [], devices: [], floors: [], labels: [] })
  const fixture = visualFixture()
  const callWS = vi.fn((message: any) => message.type === 'hass_sharp/get_entities'
    ? Promise.resolve({ entities: [{ entity_id: 'sensor.new' }] }) : fixture.callWS(message))
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{ ...fixture, callWS } as any}>
    <RealVisualEditor name="Reopened" />
  </HassCtx.Provider></QueryClientProvider>, host)
  await vi.waitFor(() => expect((client.getQueryData(['hass-entities']) as any)?.entities).toEqual([{ entity_id: 'sensor.new' }]))
  expect(callWS).toHaveBeenCalledWith({ type: 'hass_sharp/get_entities' })
})
import { Menu } from '../src/components/Menu'
it('runs an inspector "⋯" menu with arrows and Escape inside the editor while no key typed there reaches the document', async () => {
  const host = root()
  dispose = render(() => <VisualEditor name="Heating" />, host)
  const documentKeys = vi.fn(); document.addEventListener('keydown', documentKeys)
  // The inspector's own "⋯" menus come with its typed rows; mount the same Menu component into it.
  const inspector = host.querySelector('aside[aria-label="Node inspector"]')!
  const chosen = vi.fn()
  const slot = inspector.appendChild(document.createElement('div'))
  const disposeMenu = render(() => <Menu label="Value mode" items={[{ label: 'Value', onSelect: chosen }, { label: 'Variable…', onSelect: chosen }, { label: 'Clear', onSelect: chosen }]} />, slot)
  try {
    const trigger = inspector.querySelector<HTMLButtonElement>('button[aria-label="Value mode"]')!
    trigger.focus()
    trigger.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }))
    expect(trigger.getAttribute('aria-expanded')).toBe('true')
    const items = [...inspector.querySelectorAll<HTMLElement>('[role="menu"] [role="menuitem"]')]
    expect(document.activeElement).toBe(items[0])
    items[0].dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }))
    expect(document.activeElement).toBe(items[1])
    items[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(trigger.getAttribute('aria-expanded')).toBe('false')
    expect(document.activeElement).toBe(trigger)
    expect(chosen).not.toHaveBeenCalled()
    const label = host.querySelector<HTMLInputElement>('input[aria-label="Node label"]')!
    label.dispatchEvent(new KeyboardEvent('keydown', { key: 'x', bubbles: true, cancelable: true }))
    label.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true, cancelable: true }))
    expect(documentKeys).not.toHaveBeenCalled()
    expect(json(host).nodes).toHaveLength(3)
  } finally { disposeMenu(); document.removeEventListener('keydown', documentKeys) }
})

it('renames the automation as it is typed: one undo step per visit, Escape and an empty name restore it', () => {
  const { host } = mount('Heating')
  const name = host.querySelector<HTMLInputElement>('input[aria-label="Automation name"]')!
  const type = (value: string) => { name.value = value; name.dispatchEvent(new Event('input', { bubbles: true })) }
  const leave = () => { name.blur(); name.dispatchEvent(new FocusEvent('blur')) }
  const closing = () => { const event = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(event); return event.defaultPrevented }
  expect(chip(host).textContent).toBe('Not saved')
  // One typed character, still in the field: the graph, the guard and the chip follow it.
  name.focus(); type('Heatingx')
  expect(document.activeElement).toBe(name)
  expect(chip(host).textContent).toBe('Unsaved changes')
  expect(chipTip(host)).toBe('Not saved yet; there are unsaved changes')
  expect(closing()).toBe(true)
  expect(host.querySelector('h1')!.textContent).toBe('Heatingx')
  // Escape undoes the visit: nothing changed, nothing to undo.
  expect(key(name, 'Escape').defaultPrevented).toBe(true)
  expect(name.value).toBe('Heating')
  expect(chip(host).textContent).toBe('Not saved')
  expect(closing()).toBe(false)
  expect(button(host, 'Undo').disabled).toBe(true)
  leave()
  // A visit is one undo step however many keys it took; Enter leaves the field.
  name.focus(); type('Kit'); type('Kitchen')
  expect(json(host).name).toBe('Kitchen')
  expect(key(name, 'Enter').defaultPrevented).toBe(true)
  expect(document.activeElement).not.toBe(name)
  leave()
  name.focus(); type('Kitchen 2'); leave()
  expect(json(host).name).toBe('Kitchen 2')
  button(host, 'Undo').click()
  expect(name.value).toBe('Kitchen')
  button(host, 'Undo').click()
  expect(name.value).toBe('Heating')
  expect(button(host, 'Undo').disabled).toBe(true)
  button(host, 'Redo').click()
  expect(name.value).toBe('Kitchen')
  // A name left blank goes back to the one the visit started from, with no new undo step.
  name.focus(); type('Hall'); type('  '); leave()
  expect(name.value).toBe('Kitchen')
  expect(json(host).name).toBe('Kitchen')
  button(host, 'Undo').click()
  expect(json(host).name).toBe('Heating')
  expect(button(host, 'Undo').disabled).toBe(true)
})

it('keeps the drawer closed on load, switches its tabs without losing their state, and closes on Escape', async () => {
  const { host } = mount()
  const drawer = host.querySelector('.hs-drawer')!
  expect(drawer.classList.contains('hs-drawer--open')).toBe(false)
  expect([...host.querySelectorAll('.hs-drawer [role="tab"]')].map(t => t.getAttribute('aria-selected'))).toEqual(['false', 'false', 'false'])
  expect([...host.querySelectorAll<HTMLElement>('.hs-drawer__panel')].every(panel => panel.hidden)).toBe(true)
  drawerTab(host, 'Generated C#').click()
  expect(drawer.classList.contains('hs-drawer--open')).toBe(true)
  button(host, 'Graph JSON').click()
  drawerTab(host, 'Runs').click()
  expect(drawerTab(host, 'Runs').getAttribute('aria-selected')).toBe('true')
  expect(host.querySelector('.hs-drawer__panel:not([hidden])')!.textContent).toContain('Save this automation to see its runs here')
  drawerTab(host, 'Generated C#').click()
  expect(button(host, 'Graph JSON').getAttribute('aria-pressed')).toBe('true')
  // Arrow keys move between tabs.
  key(drawerTab(host, 'Generated C#'), 'ArrowLeft')
  expect(drawerTab(host, 'Problems').getAttribute('aria-selected')).toBe('true')
  expect(document.activeElement).toBe(drawerTab(host, 'Problems'))
  const panel = host.querySelector<HTMLElement>('.hs-drawer__panel:not([hidden])')!
  expect(panel.textContent).toContain('No problems found.')
  key(panel, 'Escape')
  expect(drawer.classList.contains('hs-drawer--open')).toBe(false)
  expect(document.activeElement).toBe(drawerTab(host, 'Problems'))
  // The toggle opens it again at the last tab; clicking the open tab closes it.
  button(host, 'Show details').click()
  expect(drawerTab(host, 'Problems').getAttribute('aria-selected')).toBe('true')
  drawerTab(host, 'Problems').click()
  expect(drawer.classList.contains('hs-drawer--open')).toBe(false)
})

it('selects the step of a problem and focuses the field it is about', async () => {
  const { host, fixture } = mount()
  fixture.controls.preview = {
    valid: false, source: 'class A\n{\n  Run();\n}', sourceMap: [{ nodeId: 'service-1', startLine: 3, endLine: 3 }],
    diagnostics: [{ nodeId: 'service-1', inputId: 'target', message: 'Choose what to turn on.', severity: 3, startLine: 3, startColumn: 3 }],
  } as any
  host.querySelector<HTMLInputElement>('input[aria-label="Node label"]')!.value = 'Motion'
  host.querySelector<HTMLInputElement>('input[aria-label="Node label"]')!.dispatchEvent(new Event('input', { bubbles: true }))
  // Problems and unsaved changes are both shown.
  await vi.waitFor(() => expect(chip(host).textContent).toBe('1 problem · Unsaved changes'))
  expect(host.querySelector('.hs-save-chip')!.getAttribute('tabindex')).toBe('0')
  // One live region with the state; the explanation is the chip's description, the short words are not read.
  const saveChip = host.querySelector<HTMLElement>('.hs-save-chip')!
  expect(saveChip.getAttribute('role')).toBeNull()
  expect(saveChip.querySelectorAll('[role="status"]').length).toBe(1)
  expect(saveChip.querySelector('[role="status"]')).toBe(chip(host))
  expect(document.getElementById(saveChip.getAttribute('aria-describedby')!)!.textContent).toBe('Not saved yet; there are unsaved changes')
  expect(saveChip.querySelector('.hs-save-chip__short')!.getAttribute('aria-hidden')).toBe('true')
  expect(saveChip.querySelector('.hs-save-chip__short')!.textContent).toBe('1 problem · Unsaved')
  expect(drawerTab(host, 'Problems').textContent).toBe('Problems1')
  drawerTab(host, 'Problems').click()
  const message = button(host, 'Choose what to turn on.')
  expect(message.closest('.hs-problems__group')!.querySelector('h3')!.textContent).toBe('Call service')
  message.click()
  const inspector = host.querySelector('aside[aria-label="Node inspector"]')!
  await vi.waitFor(() => expect(inspector.querySelector('[data-input-id="target"]')!.contains(document.activeElement)).toBe(true))
  expect(document.activeElement!.getAttribute('aria-label')).toBe('Target entities')
  expect(host.querySelector('.solid-flow__node[data-id="service-1"]')!.classList.contains('selected')).toBe(true)
  button(host, 'Show line 3 of the generated C#').click()
  expect(drawerTab(host, 'Generated C#').getAttribute('aria-selected')).toBe('true')
  expect(host.querySelector('[data-source-line="3"]')!.getAttribute('aria-current')).toBe('true')
})

it('filters the step library by name, kind id or description, and a click adds the step as one undo step', async () => {
  const { host } = mount()
  const search = host.querySelector<HTMLInputElement>('input[aria-label="Search the step library"]')!
  search.value = 'xor'; search.dispatchEvent(new Event('input', { bubbles: true }))
  const labels = () => [...host.querySelectorAll('.hs-lib__item')].map(item => item.getAttribute('aria-label'))
  expect(labels().length).toBeGreaterThan(0)
  // "xor" is the kind id of Exclusive or.
  expect(labels()).toEqual(['Add Exclusive or'])
  // Keywords: Constant is also found as "set value" and "literal", the names people may know it by.
  for (const query of ['constant', 'set value', 'literal']) {
    search.value = query; search.dispatchEvent(new Event('input', { bubbles: true }))
    expect(labels(), query).toContain('Add Constant')
  }
  search.value = 'no step is called this'; search.dispatchEvent(new Event('input', { bubbles: true }))
  expect(host.querySelector('.hs-lib')!.textContent).toContain('No step matches that search.')
  search.value = 'delay'; search.dispatchEvent(new Event('input', { bubbles: true }))
  expect(labels()).toContain('Add Delay')
  expect(host.querySelector('.hs-lib__item')!.getAttribute('draggable')).toBe('true')
  button(host, 'Add Delay').click()
  const graph = json(host)
  expect(graph.nodes).toHaveLength(4)
  expect(graph.nodes[3].kind).toBe('delay')
  expect(host.querySelector<HTMLInputElement>('input[aria-label="Node label"]')!.value).toBe('Delay')
  button(host, 'Undo').click()
  expect(json(host).nodes).toHaveLength(3)
  // Collapsing to the rail keeps focus on the library.
  button(host, 'Hide step library').click()
  expect(host.querySelector('.hs-lib__panel')).toBeNull()
  await vi.waitFor(() => expect(document.activeElement).toBe(button(host, 'Show step library')))
  button(host, 'Show step library').click()
  await vi.waitFor(() => expect(document.activeElement).toBe(host.querySelector('input[aria-label="Search the step library"]')))
})

it('goes full screen with the browser API, folds the library and drawer away, and follows the browser leaving it', async () => {
  let element: Element | null = null
  const change = () => document.dispatchEvent(new Event('fullscreenchange'))
  Object.defineProperty(document, 'fullscreenElement', { configurable: true, get: () => element })
  const request = vi.fn(function (this: Element) { element = this; change(); return Promise.resolve() })
  const exit = vi.fn(() => { element = null; change(); return Promise.resolve() })
  const original = HTMLElement.prototype.requestFullscreen
  HTMLElement.prototype.requestFullscreen = request as any
  Object.defineProperty(document, 'exitFullscreen', { configurable: true, value: exit })
  try {
    const { host } = mount()
    const page = editorRoot(host)
    drawerTab(host, 'Generated C#').click()
    const toggle = host.querySelector<HTMLButtonElement>('.hs-flow button[aria-label="Full screen"]')!
    expect(toggle.getAttribute('aria-pressed')).toBe('false')
    toggle.focus(); toggle.click()
    await vi.waitFor(() => expect(page.classList.contains('hs-visual--fullscreen')).toBe(true))
    expect(request).toHaveBeenCalledOnce()
    expect(page.classList.contains('hs-visual--maximised')).toBe(false)
    expect(toggle.getAttribute('aria-pressed')).toBe('true')
    expect(toggle.getAttribute('aria-label')).toBe('Exit full screen')
    expect(host.querySelector('.hs-lib__panel')).toBeNull()
    expect(host.querySelector('.hs-drawer--open')).toBeNull()
    expect(host.querySelector('.hs-topbar__exit')).not.toBeNull()
    // The browser leaves full screen by itself (its own Escape or control).
    element = null; change()
    expect(page.classList.contains('hs-visual--fullscreen')).toBe(false)
    expect(host.querySelector('.hs-lib__panel')).not.toBeNull()
    expect(host.querySelector('.hs-topbar__exit')).toBeNull()
    // The bar's exit button leaves it too, and focus goes back to what opened it.
    toggle.focus(); toggle.click()
    await vi.waitFor(() => expect(host.querySelector('.hs-topbar__exit')).not.toBeNull())
    button(host, 'Exit full screen').click()
    await vi.waitFor(() => expect(page.classList.contains('hs-visual--fullscreen')).toBe(false))
    expect(exit).toHaveBeenCalled()
    await vi.waitFor(() => expect(document.activeElement).toBe(toggle))
  } finally {
    HTMLElement.prototype.requestFullscreen = original
    delete (document as any).fullscreenElement
    delete (document as any).exitFullscreen
  }
})

it('maximises the page where the Fullscreen API is missing; F toggles it and Escape leaves it when nothing else uses the key', async () => {
  const original = HTMLElement.prototype.requestFullscreen
  ;(HTMLElement.prototype as any).requestFullscreen = undefined
  try {
    const { host } = mount()
    const page = editorRoot(host)
    const card = host.querySelector<HTMLElement>('.solid-flow__node[data-id="trigger-1"]')!
    key(card, 'f')
    await vi.waitFor(() => expect(page.classList.contains('hs-visual--maximised')).toBe(true))
    expect(page.classList.contains('hs-visual--fullscreen')).toBe(true)
    // F typed in a field is text.
    const label = host.querySelector<HTMLInputElement>('input[aria-label="Node label"]')
    key(label!, 'f'); expect(page.classList.contains('hs-visual--maximised')).toBe(true)
    // Escape on a selected card deselects it first.
    key(card, 'Escape')
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(page.classList.contains('hs-visual--maximised')).toBe(true)
    key(host.querySelector('.hs-flow')!, 'Escape')
    await vi.waitFor(() => expect(page.classList.contains('hs-visual--maximised')).toBe(false))
    key(card, 'f')
    await vi.waitFor(() => expect(page.classList.contains('hs-visual--maximised')).toBe(true))
    key(card, 'F')
    await vi.waitFor(() => expect(page.classList.contains('hs-visual--fullscreen')).toBe(false))
  } finally { HTMLElement.prototype.requestFullscreen = original }
})

it('shows a narrow screen\'s inspector as a peek bar that Enter on the card or a tap opens, and Close returns to it', async () => {
  narrowScreen()
  const { host } = mount()
  const sheet = host.querySelector<HTMLElement>('aside[aria-label="Node inspector"]')!
  // Never open on load.
  expect(sheet.dataset.open).toBe('false')
  expect(sheet.dataset.peek).toBe('true')
  const peek = button(host, 'Edit Entity changes')
  expect(peek.getAttribute('aria-expanded')).toBe('false')
  // Clicks and keys inside the panel or on a card's own controls do not reopen it (item A).
  const card = host.querySelector<HTMLElement>('.solid-flow__node[data-id="trigger-1"]')!
  card.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  expect(sheet.dataset.open).toBe('false')
  key(card, ' ')
  await Promise.resolve()
  expect(sheet.dataset.open).toBe('false')
  key(card, 'Enter')
  await vi.waitFor(() => expect(sheet.dataset.open).toBe('true'))
  expect(sheet.dataset.peek).toBeUndefined()
  await vi.waitFor(() => expect(document.activeElement).toBe(host.querySelector('input[aria-label="Node label"]')))
  button(sheet, 'Close panel').click()
  expect(sheet.dataset.open).toBe('false')
  await vi.waitFor(() => expect(document.activeElement).toBe(card))
  // A click in the closed sheet's area or on the card does not reopen it.
  sheet.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true })); sheet.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  card.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  expect(sheet.dataset.open).toBe('false')
  button(host, 'Edit Entity changes').click()
  await vi.waitFor(() => expect(sheet.dataset.open).toBe('true'))
  await vi.waitFor(() => expect(document.activeElement).toBe(host.querySelector('input[aria-label="Node label"]')))
  // Nothing selected: no bar at all.
  key(card, 'Escape')
  expect(host.querySelector('.hs-insp-peek')).toBeNull()
  expect(sheet.dataset.open).toBe('false')
})

it('duplicates from the panel where the canvas duplicate places the copy', () => {
  const { host } = mount()
  focusNode(host, 'service-1')
  key(host.querySelector('.solid-flow__node[data-id="service-1"]')!, 'd', { ctrlKey: true })
  const canvasCopy = json(host).nodes[3]
  expect(canvasCopy.kind).toBe('service')
  button(host, 'Undo').click()
  expect(json(host).nodes).toHaveLength(3)
  focusNode(host, 'service-1')
  button(host.querySelector('aside[aria-label="Node inspector"]')!, 'Node actions').click()
  const item = [...host.querySelectorAll<HTMLElement>('[role="menu"] [role="menuitem"]')].find(b => b.textContent === 'Duplicate')!
  item.click()
  const panelCopy = json(host).nodes[3]
  expect(panelCopy.position).toEqual(canvasCopy.position)
  expect(panelCopy.config).toEqual(canvasCopy.config)
})

it('shows the last run and the problem count in the overview, and the count opens Problems', async () => {
  const { host, fixture } = mount('Runs', {
    callWS: message => message.type === 'hass_sharp/run_history'
      ? { version: 2, offset: 0, limit: 20, total: 1, runs: [{ runId: 'r1', automationId: message.automationId, revision: 'r', action: 'VisualAutomation::Run_x', actionLabel: 'Entity changes', triggerNodeId: 'trigger-1', visualId: 'saved', visualRevision: 1, cause: 'Manual', trigger: { kind: 'manual' }, plannedUtc: null, receivedUtc: '2026-11-12T08:00:00Z', startUtc: null, endUtc: null, durationMs: null, status: 'Failed', error: 'Boom', hasTrace: true }] }
      : undefined,
  })
  await vi.waitFor(() => expect(button(host, 'Save').disabled).toBe(false))
  button(host, 'Save').click()
  await vi.waitFor(() => expect(chip(host).textContent).toBe('Saved'))
  fixture.controls.preview = {
    valid: true, source: 'class A {}', sourceMap: [],
    diagnostics: [{ nodeId: 'service-1', message: 'This light may be off.', severity: 2 }],
  } as any
  const label = host.querySelector<HTMLInputElement>('input[aria-label="Node label"]')!
  label.value = 'Motion'; label.dispatchEvent(new Event('input', { bubbles: true }))
  key(host.querySelector('.solid-flow__node[data-id="trigger-1"]')!, 'Escape')
  const overview = () => host.querySelector('[aria-label="Automation overview"]')
  // Beside the canvas (900px and wider, not full screen) the panel is shown with nothing selected: the overview.
  expect(editorRoot(host).classList.contains('hs-visual--floating')).toBe(false)
  expect(inspectorOf(host).dataset.open).toBe('true')
  expect(inspectorOf(host).dataset.peek).toBeUndefined()
  await vi.waitFor(() => expect(overview()?.textContent).toContain('Last run: Failed'))
  expect(overview()!.querySelector('.hs-error')!.textContent).toBe('Failed')
  await vi.waitFor(() => expect(overview()!.textContent).toContain('1 warning'))
  button(overview()!, '1 warning').click()
  expect(drawerTab(host, 'Problems').getAttribute('aria-selected')).toBe('true')
  await vi.waitFor(() => expect(document.activeElement?.textContent).toBe('This light may be off.'))
})

it('keeps the side panel through a resize across 900px, and comes back narrow as the peek bar', async () => {
  const resize = resizableScreen(true)
  const { host } = mount()
  const panel = inspectorOf(host)
  expect(editorRoot(host).classList.contains('hs-visual--floating')).toBe(true)
  expect(panel.dataset.peek).toBe('true')
  // Narrow to wide with a step selected: the side panel with that step.
  resize(false)
  expect(editorRoot(host).classList.contains('hs-visual--floating')).toBe(false)
  expect(panel.dataset.open).toBe('true')
  expect(panel.dataset.peek).toBeUndefined()
  expect(titleInput(host, 'trigger-1')).not.toBeNull()
  // Wide to narrow: the peek bar, never the expanded sheet.
  resize(true)
  expect(panel.dataset.open).toBe('false')
  expect(panel.dataset.peek).toBe('true')
  // Nothing selected, wide: the overview is shown.
  resize(false)
  key(host.querySelector('.solid-flow__node[data-id="trigger-1"]')!, 'Escape')
  expect(panel.dataset.open).toBe('true')
  expect(panel.querySelector('[aria-label="Automation overview"]')).not.toBeNull()
})

it('keeps a narrow screen\'s sheet open for a selection made inside it, and focuses the new step\'s title', async () => {
  narrowScreen()
  const saved = connectData(createGraph('Pills'), 'trigger-1', 'entity_id', 'condition-1', 'entity')
  const { host } = mount('Pills', { saved })
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__node[data-id="condition-1"]')).not.toBeNull())
  const panel = inspectorOf(host)
  focusNode(host, 'condition-1')
  button(host, 'Edit State condition').click()
  await vi.waitFor(() => expect(panel.dataset.open).toBe('true'))
  // The "From …" pill selects the step it comes from.
  const pill = panel.querySelector<HTMLButtonElement>('.hs-insp-pill')!
  expect(pill.textContent).toContain('Entity changes')
  pill.focus(); pill.click()
  await vi.waitFor(() => expect(document.activeElement).toBe(titleInput(host, 'trigger-1')))
  expect(panel.dataset.open).toBe('true')
  await settle()
  expect(document.activeElement).toBe(titleInput(host, 'trigger-1'))
  // The panel's Duplicate selects the copy and keeps the sheet open on it.
  button(panel, 'Node actions').click()
  ;[...panel.querySelectorAll<HTMLElement>('[role="menu"] [role="menuitem"]')].find(b => b.textContent === 'Duplicate')!.click()
  const copy = json(host).nodes.at(-1).id
  expect(copy).not.toBe('trigger-1')
  await vi.waitFor(() => expect(document.activeElement).toBe(titleInput(host, copy)))
  expect(panel.dataset.open).toBe('true')
  await settle()
  expect(document.activeElement).toBe(titleInput(host, copy))
  expect(document.activeElement).not.toBe(document.body)
  // A selection made on the canvas folds it back to the peek bar.
  focusNode(host, 'service-1')
  expect(panel.dataset.open).toBe('false')
  expect(panel.dataset.peek).toBe('true')
})

it('leaves a narrow screen\'s sheet closed for keys and clicks in a card\'s own inputs; Enter on the card opens it (item A)', async () => {
  narrowScreen()
  const { host } = mount()
  button(host, 'Add Delay').click()
  const id = json(host).nodes.at(-1).id
  const panel = inspectorOf(host)
  expect(panel.dataset.open).toBe('false')
  const card = host.querySelector<HTMLElement>(`.solid-flow__node[data-id="${id}"]`)!
  const input = card.querySelector<HTMLInputElement>('.hs-card-editor input')!
  expect(input).not.toBeNull()
  key(input, ' '); key(input, 'Enter'); input.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  await settle()
  expect(panel.dataset.open).toBe('false')
  key(card, 'Enter')
  await vi.waitFor(() => expect(panel.dataset.open).toBe('true'))
  await vi.waitFor(() => expect(document.activeElement).toBe(titleInput(host, id)))
})

it('runs the one trigger that allows manual runs from the top bar, and the Runs tab (history only) polls for its result', async () => {
  let accepted: string | undefined, reads = 0
  const { host } = mount('Run', {
    callWS: message => {
      if (message.type === 'hass_sharp/run_now') { accepted = 'run-1'; return Promise.resolve({ version: 1, runId: 'run-1' }) }
      if (message.type !== 'hass_sharp/run_history') return undefined
      if (accepted) reads++
      // The accepted run is not recorded yet on the first read after it: the list keeps polling for it anyway.
      const runs = accepted && reads > 1 ? [{ runId: accepted, automationId: message.automationId, revision: 'r', action: 'a', actionLabel: 'Entity changes', triggerNodeId: 'trigger-1', visualId: 'x', visualRevision: 1, cause: 'Manual', trigger: { kind: 'manual' }, plannedUtc: null, receivedUtc: new Date().toISOString(), startUtc: null, endUtc: null, durationMs: null, status: reads > 3 ? 'Succeeded' : 'Running', error: null, hasTrace: false }] : []
      return Promise.resolve({ version: 2, offset: 0, limit: 20, runs })
    },
  })
  const runNow = host.querySelector<HTMLButtonElement>('.hs-topbar__run')!
  expect(runNow.disabled).toBe(true)
  expect(runNow.title).toBe('Save the automation to run it')
  await vi.waitFor(() => expect(button(host, 'Save').disabled).toBe(false))
  button(host, 'Save').click()
  await vi.waitFor(() => expect(runNow.disabled).toBe(false))
  expect(runNow.title).toBe('Run the saved automation now')
  expect(runNow.getAttribute('aria-haspopup')).toBeNull()
  drawerTab(host, 'Runs').click()
  expect(host.querySelector('.hs-drawer [aria-label="Run automation"], .hs-run-action')).toBeNull()
  runNow.click()
  await vi.waitFor(() => expect(host.querySelector('.hs-topbar__notice')?.textContent).toBe('Run started. Its result is under Runs.'))
  await vi.waitFor(() => expect(host.querySelector('.hs-run-list')?.textContent).toContain('Running'), { timeout: 5000 })
  await vi.waitFor(() => expect(host.querySelector('.hs-run-list')?.textContent).toContain('Succeeded'), { timeout: 8000 })
}, 15000)

it('returns the Runs tab to the newest unfiltered page and polls after a run started from the top bar', async () => {
  const run = (runId: string, status: string) => ({ runId, automationId: 'visual:saved', revision: 'r', action: 'a', actionLabel: 'Entity changes', triggerNodeId: 'trigger-1', visualId: 'x', visualRevision: 1, cause: 'Manual', trigger: { kind: 'manual' }, plannedUtc: null, receivedUtc: new Date().toISOString(), startUtc: null, endUtc: null, durationMs: null, status, error: null, hasTrace: false })
  let accepted = false
  const reads: { offset: number; status?: string[] }[] = []
  const { host } = mount('Run', {
    saved: createGraph('Run'),
    callWS: message => {
      if (message.type === 'hass_sharp/run_now') { accepted = true; return Promise.resolve({ version: 1, runId: 'run-new' }) }
      if (message.type !== 'hass_sharp/run_history') return undefined
      reads.push({ offset: message.offset, status: message.status })
      const runs = message.offset ? [] : accepted && !message.status ? [run('run-new', 'Running')] : Array.from({ length: 20 }, (_, i) => run(`old-${i}`, 'Failed'))
      return Promise.resolve({ version: 2, offset: message.offset, limit: 20, runs })
    },
  })
  const runNow = host.querySelector<HTMLButtonElement>('.hs-topbar__run')!
  await vi.waitFor(() => expect(runNow.disabled).toBe(false))
  drawerTab(host, 'Runs').click()
  const status = () => host.querySelector<HTMLSelectElement>('.hs-history-tools select')!
  await vi.waitFor(() => expect(button(host, 'Older').disabled).toBe(false))
  status().value = 'Failed'; status().dispatchEvent(new Event('change', { bubbles: true }))
  await vi.waitFor(() => expect(button(host, 'Older').disabled).toBe(false))
  button(host, 'Older').click()
  await vi.waitFor(() => expect(reads.at(-1)).toEqual({ offset: 20, status: ['Failed'] }))
  runNow.click()
  await vi.waitFor(() => expect(host.querySelector('.hs-run-list')?.textContent).toContain('Running'))
  expect(status().value).toBe('')
  expect(button(host, 'Newest').disabled).toBe(true)
  // Still running: the first page is read again on the poll.
  const before = reads.filter(read => !read.offset && !read.status).length
  await vi.waitFor(() => expect(reads.filter(read => !read.offset && !read.status).length).toBeGreaterThan(before), { timeout: 5000 })
}, 10000)

it('forgets a run it started once the page leaves that automation for a new draft', async () => {
  const graph = createGraph('Run')
  const saved = { version: 1, id: 'a', revision: 1, graph, source: '', sourceMap: [], automationId: 'visual:a', currentRevisionActive: true, catalogGeneration: 'g' } as any
  const [document_, setDocument] = createSignal<any>(saved)
  const host = root()
  const hass = { callWS: () => Promise.resolve({ version: 1, runId: 'run-a' }) }
  dispose = render(() => <QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={hass as any}>
    <RunNowButton document={document_()} catalog={nodeCatalog} dirty={false} onNotice={() => {}} />
  </HassCtx.Provider></QueryClientProvider>, host)
  host.querySelector<HTMLButtonElement>('.hs-topbar__run')!.click()
  await vi.waitFor(() => expect(acceptedRun('visual:a')?.runId).toBe('run-a'))
  setDocument(undefined)
  expect(acceptedRun('visual:a')).toBeUndefined()
})

it('names trigger entities by their registry name in the overview and the Run now menu, as the cards do', async () => {
  const saved = createGraph('Named')
  const { host } = mount('Named', {
    saved,
    callWS: message => message.type === 'hass_sharp/get_entities'
      ? Promise.resolve({ entities: [{ entity_id: 'binary_sensor.motion', name: 'Hall motion' }] }) : undefined,
  })
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__node[data-id="trigger-1"]')).not.toBeNull())
  key(host.querySelector('.solid-flow__node[data-id="trigger-1"]')!, 'Escape')
  await vi.waitFor(() => expect(host.querySelector('.hs-insp-trigger-line')?.textContent).toBe('When Hall motion changes'))
  const names = { entities: [{ entity_id: 'binary_sensor.motion', name: 'Hall motion' }] }
  const document = { version: 1, id: 'named', revision: 1, graph: saved, source: '', sourceMap: [], automationId: 'visual:named' } as any
  expect(manualTriggers(document, nodeCatalog, names).map(t => t.summary)).toEqual(['When Hall motion changes'])
})

it('disables Run now when no trigger allows manual runs, and says why', async () => {
  const saved = updateNode(createGraph('Manual off'), 'trigger-1', { config: { type: 'entity', entity: 'binary_sensor.motion', manual: false, mode: 'Single' } })
  const { host } = mount('Manual off', { saved })
  const runNow = () => host.querySelector<HTMLButtonElement>('.hs-topbar__run')!
  await vi.waitFor(() => expect(runNow().title).toBe('No trigger allows manual runs'))
  expect(runNow().disabled).toBe(true)
})

it('falls back to the maximised page when the browser refuses full screen', async () => {
  const original = HTMLElement.prototype.requestFullscreen
  HTMLElement.prototype.requestFullscreen = vi.fn(() => Promise.reject(new TypeError('Permissions check failed'))) as any
  try {
    const { host } = mount()
    host.querySelector<HTMLButtonElement>('.hs-flow button[aria-label="Full screen"]')!.click()
    await vi.waitFor(() => expect(editorRoot(host).classList.contains('hs-visual--maximised')).toBe(true))
    expect(HTMLElement.prototype.requestFullscreen).toHaveBeenCalledOnce()
    // F held down toggles once.
    const card = host.querySelector('.solid-flow__node[data-id="trigger-1"]')!
    key(card, 'f', { repeat: true })
    expect(editorRoot(host).classList.contains('hs-visual--maximised')).toBe(true)
  } finally { HTMLElement.prototype.requestFullscreen = original }
})

it('cancels a keyboard connection with Escape without leaving the maximised page', async () => {
  const original = HTMLElement.prototype.requestFullscreen
  ;(HTMLElement.prototype as any).requestFullscreen = undefined
  try {
    const { host } = mount()
    key(host.querySelector('.solid-flow__node[data-id="trigger-1"]')!, 'f')
    await vi.waitFor(() => expect(editorRoot(host).classList.contains('hs-visual--maximised')).toBe(true))
    const pin = host.querySelector<HTMLElement>('[data-id="condition-1"] [data-handleid="false"]')!
    key(pin, 'Enter')
    expect(host.querySelector('.hs-flow')!.textContent).toContain('Choose a pin to connect to')
    const escape = key(pin, 'Escape')
    expect(escape.defaultPrevented).toBe(true)
    await settle(); await settle()
    expect(editorRoot(host).classList.contains('hs-visual--maximised')).toBe(true)
  } finally { HTMLElement.prototype.requestFullscreen = original }
})

it('places a step added from the library below a card at the centre of the view', () => {
  const { host } = mount()
  button(host, 'Add Delay').click()
  const first = json(host).nodes.at(-1)
  button(host, 'Add Delay').click()
  const second = json(host).nodes.at(-1)
  expect(second.id).not.toBe(first.id)
  expect(second.position.x).toBe(first.position.x)
  expect(second.position.y).toBeGreaterThan(first.position.y)
})

it('gives up a pending focus once focus has moved elsewhere (item B)', async () => {
  const host = root()
  const moved = host.appendChild(document.createElement('button'))
  const late = host.appendChild(document.createElement('input'))
  let tries = 0
  soon(() => { tries++; return false }, host)
  moved.focus()
  await settle(); await settle()
  expect(tries).toBe(0)
  // Without a move it retries until the target is there.
  soon(() => { tries++; if (tries < 2) return false; late.focus(); return true }, host)
  await vi.waitFor(() => expect(document.activeElement).toBe(late))
  expect(tries).toBe(2)
})
