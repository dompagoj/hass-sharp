import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { VisualEditor as RealVisualEditor } from '../src/visual/VisualEditor'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { addNode, connectNodes, createGraph, createGraphHistory, updateNode } from '../src/visual/graph'

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
const chip = (root: Element) => root.querySelector('.hs-save-chip')!
const editorRoot = (root: Element) => root.querySelector<HTMLElement>('[aria-label="Visual automation editor"]')!
const key = (target: Element, key: string, init: KeyboardEventInit = {}) => {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }); target.dispatchEvent(event); return event
}
/** Mounts the editor with its own fixture (and optionally a callWS in front of it). */
function mount(name = 'Heating', options: { callWS?: (message: any, fixture: ReturnType<typeof visualFixture>) => unknown } = {}) {
  const host = root(), fixture = visualFixture()
  const callWS = (message: any) => options.callWS?.(message, fixture) ?? fixture.callWS(message)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{ ...fixture, callWS, user: { is_admin: true } } as any}>
    <RealVisualEditor name={name} />
  </HassCtx.Provider></QueryClientProvider>, host)
  return { host, fixture }
}
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

it('renames the automation in place as one undo step, and Escape puts the name back', () => {
  const { host } = mount('Heating')
  const name = host.querySelector<HTMLInputElement>('input[aria-label="Automation name"]')!
  expect(chip(host).textContent).toBe('Not saved')
  name.focus()
  for (const value of ['Kit', 'Kitchen']) { name.value = value; name.dispatchEvent(new Event('input', { bubbles: true })) }
  expect(chip(host).textContent).toBe('Unsaved changes')
  expect(chip(host).getAttribute('title')).toBe('Not saved yet; there are unsaved changes')
  expect(host.querySelector('h1')!.textContent).toBe('Kitchen')
  expect(json(host).name).toBe('Kitchen')
  button(host, 'Undo').click()
  expect(name.value).toBe('Heating')
  button(host, 'Redo').click()
  expect(key(name, 'Escape').defaultPrevented).toBe(true)
  expect(name.value).toBe('Heating')
  expect(json(host).name).toBe('Heating')
  // An empty name is not kept.
  name.focus(); name.value = ''; name.dispatchEvent(new Event('input', { bubbles: true })); name.dispatchEvent(new FocusEvent('blur'))
  expect(json(host).name).toBe('Heating')
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
  expect(host.querySelector('.hs-drawer__panel:not([hidden])')!.textContent).toContain('Save this automation to run it')
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
  await vi.waitFor(() => expect(chip(host).textContent).toBe('1 problem'))
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
      ? { version: 2, offset: 0, limit: 20, total: 1, runs: [{ runId: 'r1', automationId: message.automationId, status: 'Failed', receivedUtc: '2026-11-12T08:00:00Z', triggerKind: 'manual', hasTrace: true }] }
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
  await vi.waitFor(() => expect(overview()?.textContent).toContain('Last run: Failed'))
  expect(overview()!.querySelector('.hs-error')!.textContent).toBe('Failed')
  await vi.waitFor(() => expect(overview()!.textContent).toContain('1 warning'))
  button(overview()!, '1 warning').click()
  expect(drawerTab(host, 'Problems').getAttribute('aria-selected')).toBe('true')
  await vi.waitFor(() => expect(document.activeElement?.textContent).toBe('This light may be off.'))
})
