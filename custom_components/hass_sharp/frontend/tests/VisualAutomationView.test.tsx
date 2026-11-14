import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { A, MemoryRouter, Route, createMemoryHistory } from '@solidjs/router'
import { VisualAutomationView } from '../src/views/VisualAutomationView'
import { HassCtx } from '../src/context'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { visualFixture } from './visual-fixture'
import { createGraph } from '../src/visual/graph'
let dispose: (() => void) | undefined
let client: QueryClient
afterEach(() => { dispose?.(); client?.clear(); document.body.innerHTML = ''; vi.unstubAllGlobals() })
function setup(saved = false, minimal = false, path?: string) {
  const fixture = visualFixture()
  if (saved) fixture.documents.set('motion', { version: 1, id: 'motion', revision: 4, graph: createGraph('Saved motion'), source: 'saved', sourceMap: [], automationId: 'visual:motion' })
  if (minimal) fixture.documents.get('motion')!.graph.nodes = fixture.documents.get('motion')!.graph.nodes.map(({ label, position, size, ...node }) => node as any)
  const root = document.createElement('div'); document.body.append(root)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const history = createMemoryHistory(); history.set({ value: path ?? (saved ? '/visual/motion' : '/visual/new') })
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{ ...fixture, user: { is_admin: true } } as any}>
    <MemoryRouter history={history} root={props => <><A href="/elsewhere">Leave</A><A href="/visual/new?name=Fresh">New visual</A>{props.children}</>}>
      <Route path="/visual/:id" component={VisualAutomationView} />
      <Route path="/elsewhere" component={() => <h1>Elsewhere</h1>} />
      <Route path="/automations" component={() => <h1>Automations</h1>} />
    </MemoryRouter></HassCtx.Provider></QueryClientProvider>, root)
  return { root, fixture, history }
}
const button = (root: Element, text: string) => [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text || b.getAttribute('aria-label') === text)!
const edit = (root: Element, text: string) => { const input = root.querySelector<HTMLInputElement>('[aria-label="Node label"]')!; input.value = text; input.dispatchEvent(new Event('input', { bubbles: true })) }
const chip = (root: Element) => root.querySelector('.hs-save-chip__text')!
/** The save chip's explanation (its tooltip and hidden text). */
const chipTip = (root: Element) => root.querySelector('.hs-save-chip')!.getAttribute('title')
/** Waits for the save state "Saved" at `revision` (the revision is in the chip's tooltip, not its text). */
const saved = async (root: Element, revision: number) => vi.waitFor(() => {
  expect(chip(root).textContent).toBe('Saved')
  expect(chipTip(root)).toBe(`Saved revision ${revision}`)
})
/** Picks an item of the top bar's "⋯" menu. */
const more = (root: Element, item: string) => {
  button(root, 'More actions').click()
  const choice = [...root.querySelectorAll<HTMLButtonElement>('[role="menu"] [role="menuitem"]')].find(b => b.textContent === item)
  expect(choice, item).toBeDefined()
  choice!.click()
}
/** Opens a tab of the bottom drawer. */
const tab = (root: Element, name: string) => {
  const found = [...root.querySelectorAll<HTMLButtonElement>('.hs-drawer [role="tab"]')].find(b => b.textContent?.startsWith(name))!
  if (found.getAttribute('aria-selected') !== 'true') found.click()
}
it('creates, edits and saves separate drafts on HTTP without crypto.randomUUID', async () => {
  // HTTP LAN origins expose getRandomValues but not randomUUID.
  vi.stubGlobal('crypto', { getRandomValues: crypto.getRandomValues.bind(crypto) })
  const { root, fixture } = setup()
  button(root, 'Add step').click(); button(root, 'Create Trigger').click();chooseTrigger(root, 'manual')
  button(root, 'Add step').click(); button(root, 'Create Trigger').click();chooseTrigger(root, 'manual')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await saved(root, 1)
  const first = [...fixture.documents.values()][0]
  const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/
  expect(first.id.slice('visual-'.length)).toMatch(uuid)
  expect(first.graph.nodes).toHaveLength(5)
  for (const node of first.graph.nodes.slice(3)) expect(node.id).toMatch(uuid)
  expect(new Set(first.graph.nodes.map(node => node.id)).size).toBe(5)

  root.querySelector<HTMLAnchorElement>('a[href="/visual/new?name=Fresh"]')!.click()
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Fresh'))
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(fixture.documents.size).toBe(2))
  const second = [...fixture.documents.values()][1]
  expect(second.id).not.toBe(first.id)
  expect(second.id.slice('visual-'.length)).toMatch(uuid)
  expect(first.graph.nodes).toHaveLength(5)
  expect(second.graph.nodes).toHaveLength(3)
})
it('leaves an unchanged draft without a warning', async () => {
  const { root } = setup()
  root.querySelector<HTMLAnchorElement>('a')!.click(); await Promise.resolve()
  expect(root.textContent).toContain('Elsewhere')
})
it('protects only changed drafts and allows explicit discard', async () => {
  const { root } = setup()
  edit(root, 'Changed')
  root.querySelector<HTMLAnchorElement>('a')!.click(); await Promise.resolve()
  expect(root.querySelector('dialog[open]')?.textContent).toContain('Unsaved changes')
  button(root, 'Keep editing').click()
  root.querySelector<HTMLAnchorElement>('a')!.click()
  button(root, 'Discard changes').click(); await Promise.resolve()
  expect(root.textContent).toContain('Elsewhere')
})
it('saves server generated source, opens persisted identity and exposes run/history', async () => {
  const { root, fixture } = setup()
  edit(root, 'Motion trigger')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await saved(root, 1)
  expect(fixture.documents.size).toBe(1)
  const doc = [...fixture.documents.values()][0]
  expect(doc.graph.nodes[0].label).toBe('Motion trigger')
  expect(doc.source).toContain('generated on server')
  // The Runs tab of the drawer shows the history; Run now in the top bar starts the saved trigger.
  expect(root.querySelector('[aria-label="Run history"]')).toBeNull()
  tab(root, 'Runs')
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
  const runNow = root.querySelector<HTMLButtonElement>('.hs-topbar__run')!
  expect(runNow.textContent?.trim()).toBe('Run now')
  runNow.click()
  await vi.waitFor(() => expect(root.querySelector('.hs-topbar__notice')?.textContent).toBe('Run started. Its result is under Runs.'))
  expect(fixture.calls).toContainEqual({ type: 'hass_sharp/run_now', automationId: `visual:${doc.id}`, className: 'VisualAutomation', method: 'Run_747269676765722D31' })
  // The history asks again, so the new run shows.
  await vi.waitFor(() => expect(fixture.calls.filter(c => c.type === 'hass_sharp/run_history').length).toBeGreaterThan(1))
  root.querySelector<HTMLAnchorElement>('a')!.click(); await Promise.resolve()
  expect(root.textContent).toContain('Elsewhere')
})
it('keeps a conflicted local draft until the person chooses what to keep, and can still reload and discard it', async () => {
  const { root, fixture } = setup()
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await saved(root, 1)
  fixture.controls.conflict = true
  edit(root, 'My unsaved change')
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Changed elsewhere'))
  expect((root.querySelector('[aria-label="Node label"]') as HTMLInputElement).value).toBe('My unsaved change')
  // The choices, and no advice to export first.
  const dialog = () => root.querySelector('dialog[open]')
  await vi.waitFor(() => expect(dialog()?.textContent).toContain('This automation changed elsewhere'))
  expect(dialog()!.textContent).not.toContain('Export')
  expect(root.querySelector('.hs-visual-alert')!.textContent).not.toContain('Export')
  for (const choice of ['Keep my version', 'Save as a copy', 'Discard my changes']) expect(button(dialog()!, choice), choice).toBeDefined()
  // Closed, the choices come back from the alert and from Save.
  button(dialog()!, 'Close dialog').click()
  expect(dialog()).toBeNull()
  button(root, 'Save').click()
  expect(dialog()?.textContent).toContain('This automation changed elsewhere')
  button(dialog()!, 'Close dialog').click()
  button(root, 'Choose what to keep…').click()
  expect(dialog()?.textContent).toContain('This automation changed elsewhere')
  button(dialog()!, 'Close dialog').click()
  more(root, 'Reload saved graph')
  expect(root.querySelector('dialog[open]')?.textContent).toContain('Discard local changes')
  button(root, 'Reload and discard').click()
  await vi.waitFor(() => expect((root.querySelector('[aria-label="Node label"]') as HTMLInputElement).value).toBe('Entity changes'))
})
it('Keep my version saves the changes over the revision saved elsewhere', async () => {
  const { root, fixture } = setup(true)
  await saved(root, 4)
  edit(root, 'Mine')
  // Saved elsewhere meanwhile (a node library save rebuilds it).
  const stored = fixture.documents.get('motion')!
  fixture.documents.set('motion', { ...stored, revision: 6 })
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  const dialog = () => root.querySelector('dialog[open]')
  await vi.waitFor(() => expect(dialog()?.textContent).toContain('This automation changed elsewhere'))
  button(dialog()!, 'Keep my version').click()
  await saved(root, 7)
  expect(fixture.documents.get('motion')!.graph.nodes[0].label).toBe('Mine')
  expect(root.querySelector('[role="alert"]')).toBeNull()
})
it('Save as a copy saves the changes as a new automation, turned off, and opens it', async () => {
  const { root, fixture, history } = setup(true)
  await saved(root, 4)
  edit(root, 'Mine')
  fixture.controls.conflict = true
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  const dialog = () => root.querySelector('dialog[open]')
  await vi.waitFor(() => expect(dialog()?.textContent).toContain('This automation changed elsewhere'))
  fixture.controls.conflict = false
  button(dialog()!, 'Save as a copy').click()
  await vi.waitFor(() => expect(fixture.documents.size).toBe(2))
  const copy = [...fixture.documents.values()].find(d => d.id !== 'motion')!
  expect(copy).toMatchObject({ revision: 1, enabled: false })
  expect(copy.graph.name).toBe('Saved motion (copy)')
  expect(copy.graph.nodes[0].label).toBe('Mine')
  expect(fixture.documents.get('motion')!.graph.nodes[0].label).not.toBe('Mine')
  await vi.waitFor(() => expect(history.get()).toBe(`/visual/${copy.id}`))
  await saved(root, 1)
  expect(root.querySelector('h1')?.textContent).toBe('Saved motion (copy)')
})
it('saves on the first click right after an edit, while its check is still running', async () => {
  const { root, fixture } = setup(true)
  await saved(root, 4)
  edit(root, 'Saved at once')
  // The click lands before the check of the edit answers: Save is available and says what it waits on.
  expect(button(root, 'Save').disabled).toBe(false)
  expect(button(root, 'Save').title).toBe('Checking…')
  button(root, 'Save').click()
  await saved(root, 5)
  expect(fixture.documents.get('motion')!.graph.nodes[0].label).toBe('Saved at once')
})
it('says when the graph could not be checked, still saves, and checks again when the page comes back', async () => {
  const { root, fixture } = setup(true)
  await saved(root, 4)
  fixture.controls.previewError = true
  edit(root, 'Unchecked')
  await vi.waitFor(() => expect(button(root, 'Save').title).toBe('Could not check the graph'))
  expect(document.getElementById(button(root, 'Save').getAttribute('aria-describedby')!)?.textContent).toBe('Could not check the graph')
  expect(button(root, 'Save').disabled).toBe(false)
  fixture.controls.previewError = false
  window.dispatchEvent(new Event('focus'))
  await vi.waitFor(() => expect(button(root, 'Save').hasAttribute('title')).toBe(false))
  button(root, 'Save').click()
  await saved(root, 5)
})
it('offers the unsaved changes of a panel torn down without asking, and restores them as one undo step', async () => {
  const first = setup(true)
  await saved(first.root, 4)
  edit(first.root, 'Left unsaved')
  // Home Assistant's sidebar removes the panel: no router guard, no beforeunload.
  dispose!(); dispose = undefined; document.body.innerHTML = ''
  const { root } = setup(true)
  const dialog = () => root.querySelector('dialog[open]')
  await vi.waitFor(() => expect(dialog()?.textContent).toContain('Restore unsaved changes from'))
  expect(chip(root).textContent).toBe('Saved')
  button(dialog()!, 'Restore').click()
  expect(dialog()).toBeNull()
  await vi.waitFor(() => expect((root.querySelector('[aria-label="Node label"]') as HTMLInputElement).value).toBe('Left unsaved'))
  expect(chip(root).textContent).toBe('Unsaved changes')
  button(root, 'Undo').click()
  await vi.waitFor(() => expect((root.querySelector('[aria-label="Node label"]') as HTMLInputElement).value).toBe('Entity changes'))
  expect(chip(root).textContent).toBe('Saved')
})
it('offers no restore after leaving with nothing unsaved, or after discarding the changes on the way out', async () => {
  const first = setup(true)
  await saved(first.root, 4)
  dispose!(); dispose = undefined; document.body.innerHTML = ''
  const second = setup(true)
  await saved(second.root, 4)
  expect(second.root.querySelector('dialog[open]')).toBeNull()
  edit(second.root, 'Thrown away')
  second.root.querySelector<HTMLAnchorElement>('a')!.click(); await Promise.resolve()
  button(second.root, 'Discard changes').click()
  await vi.waitFor(() => expect(second.root.textContent).toContain('Elsewhere'))
  dispose!(); dispose = undefined; document.body.innerHTML = ''
  const third = setup(true)
  await saved(third.root, 4)
  await new Promise(resolve => setTimeout(resolve, 20))
  expect(third.root.querySelector('dialog[open]')).toBeNull()
})
it('keeps the switch as the server took it when a save that the server took first answers after it', async () => {
  const { root, fixture } = setup(true)
  await saved(root, 4)
  const toggle = () => root.querySelector<HTMLInputElement>('.hs-topbar__enabled input[role="switch"]')!
  await vi.waitFor(() => expect(toggle()).not.toBeNull())
  let takeSwitch!: () => void, answerSave!: () => void
  fixture.controls.holdEnabled = new Promise<void>(resolve => { takeSwitch = resolve })
  fixture.controls.holdSaveReply = new Promise<void>(resolve => { answerSave = resolve })
  // Off is pressed; before the server takes it, a save goes in and is taken first (the automation still on).
  toggle().click()
  expect(toggle().checked).toBe(false)
  edit(root, 'Saved during the switch')
  button(root, 'Save').click()
  await vi.waitFor(() => expect(fixture.documents.get('motion')!.revision).toBe(5))
  takeSwitch()
  await vi.waitFor(() => expect(fixture.documents.get('motion')!.enabled).toBe(false))
  answerSave()
  await saved(root, 5)
  // The save's answer says on; the server holds off, and so does the switch.
  expect(toggle().checked).toBe(false)
})
it('cannot be switched while a save is on its way, so a save pressed first never races the switch', async () => {
  const { root, fixture } = setup(true)
  await saved(root, 4)
  const toggle = () => root.querySelector<HTMLInputElement>('.hs-topbar__enabled input[role="switch"]')!
  await vi.waitFor(() => expect(toggle()).not.toBeNull())
  let answerSave!: () => void
  fixture.controls.holdSaveReply = new Promise<void>(resolve => { answerSave = resolve })
  edit(root, 'Saved before the switch')
  button(root, 'Save').click()
  await vi.waitFor(() => expect(fixture.documents.get('motion')!.revision).toBe(5))
  // The switch waits for the save's answer.
  expect(toggle().disabled).toBe(true)
  answerSave()
  await saved(root, 5)
  toggle().click()
  await vi.waitFor(() => expect(fixture.documents.get('motion')!.enabled).toBe(false))
  expect(toggle().checked).toBe(false)
})
it('shows persistence succeeded when runtime activation fails', async () => {
  const { root, fixture } = setup(); fixture.controls.active = false
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Saved, but not activated'))
  expect(root.textContent).toContain('Compilation failed during activation')
  root.querySelector<HTMLAnchorElement>('a')!.click(); await Promise.resolve()
  expect(root.textContent).toContain('Elsewhere')
})
it('maps diagnostics onto nodes and source lines and blocks invalid saves', async () => {
  const { root, fixture } = setup(); fixture.controls.invalid = true
  await vi.waitFor(() => expect(root.textContent).toContain('Invalid entity'))
  expect(button(root, 'Save').disabled).toBe(true)
  expect(root.querySelector('[data-id="trigger-1"] .hs-flow-card--error')).not.toBeNull()
  expect(chip(root).textContent).toBe('1 problem')
  // The Problems tab lists it under its step; the message selects the step and marks its generated line.
  const problems = root.querySelector('.hs-problems')!
  expect(problems.querySelector('.hs-problems__step')?.textContent).toBe('Entity changes')
  button(problems, 'Invalid entity').click()
  expect(root.querySelector('[data-source-line="3"]')?.getAttribute('aria-current')).toBe('true')
  expect(root.querySelector('[data-id="trigger-1"]')?.classList.contains('selected')).toBe(true)
  button(problems, 'Show line 3 of the generated C#').click()
  expect(root.querySelector('.hs-drawer [role="tab"][aria-selected="true"]')?.textContent).toBe('Generated C#')
})
it('asks before the back button leaves unsaved changes, and leaves an unchanged draft at once', async () => {
  const { root } = setup()
  edit(root, 'Changed')
  button(root, 'Back to automations').click(); await Promise.resolve()
  expect(root.querySelector('dialog[open]')?.textContent).toContain('Leave and discard your unsaved changes?')
  button(root, 'Keep editing').click()
  expect(root.querySelector('[aria-label="Visual automation editor"]')).not.toBeNull()
  button(root, 'Back to automations').click(); await Promise.resolve()
  button(root, 'Discard changes').click()
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Automations'))
  // A fresh, unchanged draft goes back without asking.
  root.querySelector<HTMLAnchorElement>('a[href="/visual/new?name=Fresh"]')!.click()
  await vi.waitFor(() => expect(button(root, 'Back to automations')).toBeDefined())
  button(root, 'Back to automations').click()
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Automations'))
  expect(root.querySelector('dialog[open]')).toBeNull()
})
it('reads and renames a saved graph without changing its automation identity', async () => {
  const { root, fixture } = setup(true)
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Saved motion'))
  const name = root.querySelector<HTMLInputElement>('[aria-label="Automation name"]')!
  name.value = 'Renamed motion'; name.dispatchEvent(new Event('input', { bubbles: true }))
  // The name is committed when the field is left (or on Enter).
  name.dispatchEvent(new FocusEvent('blur'))
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await saved(root, 5)
  expect(fixture.documents.get('motion')?.graph.name).toBe('Renamed motion')
  expect(fixture.documents.get('motion')?.automationId).toBe('visual:motion')
  expect(fixture.calls.filter(c => c.request?.operation === 'save').at(-1).request.expectedRevision).toBe(4)
})

it('marks Save unavailable while there is nothing to save, keeps it focusable and tied to why; an edit enables it, and undoing the edit marks it again', async () => {
  const { root, fixture } = setup(true)
  await vi.waitFor(() => expect(chip(root).textContent).toBe('Saved'))
  const save = () => button(root, 'Save')
  const saves = () => fixture.calls.filter(c => c.request?.operation === 'save').length
  const unavailable = () => save().getAttribute('aria-disabled') === 'true'
  expect(unavailable()).toBe(true)
  expect(save().title).toBe('Nothing to save: every change is saved')
  // Not natively disabled: the keyboard reaches it, and the reason is its description.
  expect(save().disabled).toBe(false)
  save().focus()
  expect(document.activeElement).toBe(save())
  const reason = document.getElementById(save().getAttribute('aria-describedby')!)
  expect(reason?.textContent).toBe('Nothing to save: every change is saved')
  // Clicking it anyway (or anything that would save) does nothing.
  save().click()
  await Promise.resolve()
  expect(saves()).toBe(0)
  const name = root.querySelector<HTMLInputElement>('[aria-label="Automation name"]')!
  name.value = 'Renamed motion'; name.dispatchEvent(new Event('input', { bubbles: true })); name.dispatchEvent(new FocusEvent('blur'))
  await vi.waitFor(() => expect(unavailable() || save().disabled).toBe(false))
  // Available at once; while the check of the edit runs, that is its title and its description, as a reason is.
  expect(save().title).toBe('Checking…')
  expect(document.getElementById(save().getAttribute('aria-describedby')!)?.textContent).toBe('Checking…')
  await vi.waitFor(() => expect(save().hasAttribute('title')).toBe(false))
  expect(save().hasAttribute('aria-describedby')).toBe(false)
  button(root, 'Undo').click()
  await vi.waitFor(() => expect(unavailable()).toBe(true))
  name.value = 'Renamed motion'; name.dispatchEvent(new Event('input', { bubbles: true })); name.dispatchEvent(new FocusEvent('blur'))
  await vi.waitFor(() => expect(unavailable() || save().disabled).toBe(false))
  save().click()
  await saved(root, 5)
  expect(saves()).toBe(1)
  expect(unavailable()).toBe(true)
  expect(save().title).toBe('Nothing to save: every change is saved')
})
it('keeps Save enabled for a saved version that is not active, so saving it again can activate it', async () => {
  const { root, fixture } = setup(true)
  fixture.documents.get('motion')!.currentRevisionActive = false
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Saved motion'))
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  await vi.waitFor(() => expect(button(root, 'Save').hasAttribute('title')).toBe(false))
  button(root, 'Save').click()
  await vi.waitFor(() => expect(fixture.calls.filter(c => c.request?.operation === 'save')).toHaveLength(1))
})

it('creates a fresh draft when navigating from a saved visual graph to new', async () => {
  const { root } = setup(true)
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Saved motion'))
  root.querySelector<HTMLAnchorElement>('a[href="/visual/new?name=Fresh"]')!.click()
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Fresh'))
  expect(chip(root).textContent).toBe('Not saved')
  tab(root, 'Runs')
  expect(root.querySelector('[aria-label="Run history"]')).toBeNull()
  expect(root.textContent).toContain('Save this automation to see its runs here')
  expect(button(root, 'Undo').disabled).toBe(true)
})

it('opens valid API graphs without optional canvas metadata and preserves their identity', async () => {
  const { root, fixture } = setup(true, true)
  await vi.waitFor(() => expect(root.querySelector('[data-id="trigger-1"]')).not.toBeNull())
  await vi.waitFor(()=>expect(root.querySelector('[data-id="trigger-1"] .hs-flow-card strong')?.textContent).toBe('Trigger'))
  // Opening it is not a change: nothing to save until something is edited.
  await vi.waitFor(() => expect(chip(root).textContent).toBe('Saved'))
  expect(button(root, 'Save').getAttribute('aria-disabled')).toBe('true')
  const name = root.querySelector<HTMLInputElement>('[aria-label="Automation name"]')!
  name.value = 'Renamed motion'; name.dispatchEvent(new Event('input', { bubbles: true })); name.dispatchEvent(new FocusEvent('blur'))
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await saved(root, 5)
  expect(fixture.documents.get('motion')?.graph.nodes[0]).toMatchObject({ id: 'trigger-1', config: { entity: 'binary_sensor.motion' } })
})


type Field = HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement
// By accessible name: an aria-label, or a <label for> (the inspector labels its fields).
const control = (root: Element, label: string) => {
  const named = root.querySelector<Field>(`[aria-label="${label}"]`)
  const tag = [...root.querySelectorAll<HTMLLabelElement>('label[for]')].find(l => l.textContent?.trim() === label)
  return (named ?? (tag ? root.querySelector<Field>(`[id="${tag.htmlFor}"]`) : null))!
}
function setControl(root: Element, label: string, value: string) {
  const field = control(root, label)
  expect(field, label).not.toBeNull()
  field.value = value
  field.dispatchEvent(new Event(field.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true }))
  // Inspector text fields commit on blur.
  field.dispatchEvent(new FocusEvent('blur'))
}
/** Picks a trigger type from the inspector's visible "When" choices. */
function chooseTrigger(root: Element, type: string) {
  const inspector = root.querySelector('.hs-inspector')!
  if (!inspector.querySelector('[role="radiogroup"][aria-label="Trigger type"]')) button(inspector, 'Change trigger type').click()
  const radio = inspector.querySelector<HTMLInputElement>(`[aria-label="Trigger type"] input[value="${type}"]`)
  expect(radio, type).not.toBeNull()
  radio!.click()
}
const chooseMode = (root: Element, mode: string) => root.querySelector<HTMLInputElement>(`.hs-inspector .hs-insp-modes input[value="${mode}"]`)!.click()
const press = (target: Element) => target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
function json(root: Element) {
  button(root, 'Graph JSON').click()
  return JSON.parse(root.querySelector('[aria-label="Graph JSON"]')!.textContent!)
}

it('authors two triggers, saves/reloads them, then dispatches either saved action', async () => {
  const { root, fixture } = setup()
  // The page has no C# sections of its own (a Custom C# step holds the shared C#).
  expect(root.querySelector('[aria-label="C# imports"], [aria-label="C# members"], [aria-label="Graph C# sections"]')).toBeNull()
  chooseTrigger(root, 'daily')
  setControl(root, 'Time of day', '07:30')
  setControl(root, 'Time zone', 'Europe/Zagreb')
  chooseMode(root, 'Restart')
  button(root, 'Add step').click(); button(root, 'Create Trigger').click()
  setControl(root, 'Node label', 'Manual evening')
  chooseTrigger(root, 'manual')
  // Wires are made on the canvas (here from the keyboard): Enter on the new trigger's Next pin, then on the service.
  const added = json(root).nodes.at(-1).id
  press(root.querySelector(`[data-id="${added}"] [data-handleid="next"]`)!)
  press(root.querySelector('[data-id="service-1"] [data-handleid="in"]')!)
  const draft = json(root)
  const second = draft.nodes.at(-1)
  expect(draft).toMatchObject({ version: 5 })
  expect(draft.nodes[0]).toMatchObject({ id: 'trigger-1', config: { type: 'daily', time: '07:30', timeZone: 'Europe/Zagreb', mode: 'Restart' } })
  expect(second).toMatchObject({ kind: 'trigger', config: { type: 'manual' } })
  expect(draft.edges).toContainEqual({ id: `${second.id}->service-1`, source: second.id, target: 'service-1' })
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  button(root, 'Save').click()
  await saved(root, 1)
  const stored = [...fixture.documents.values()][0]
  expect(stored.graph).toEqual(draft)
  setControl(root, 'Node label', 'unsaved label')
  more(root, 'Reload saved graph'); button(root, 'Reload and discard').click()
  await vi.waitFor(() => expect(json(root)).toEqual(draft))
  // With two triggers that allow manual runs, Run now opens a menu of them (label and summary).
  const runNow = root.querySelector<HTMLButtonElement>('.hs-topbar__run')!
  expect(runNow.getAttribute('aria-haspopup')).toBe('menu')
  const items = () => [...root.querySelectorAll<HTMLButtonElement>('[role="menu"][aria-label="Run which trigger"] [role="menuitem"]')]
  for (const index of [0, 1]) {
    runNow.click()
    // The first trigger's written label followed its new type.
    expect(items().map(item => item.querySelector('strong')!.textContent)).toEqual(['Every day', 'Manual evening'])
    expect(items()[0].querySelector('small')!.textContent).toContain('07:30')
    expect(document.activeElement).toBe(items()[0])
    items()[index].click()
    expect(items()).toHaveLength(0)
    await vi.waitFor(() => expect(root.querySelector('.hs-topbar__notice')?.textContent).toMatch(/^Run of (Every day|Manual evening) started/))
    await vi.waitFor(() => expect(runNow.disabled).toBe(false))
  }
  // The Runs tab shows history only.
  tab(root, 'Runs')
  await vi.waitFor(() => expect(root.querySelector('[aria-label="Run history"]')).not.toBeNull())
  expect(root.querySelector('.hs-drawer [aria-label="Run automation"]')).toBeNull()
  const methods = fixture.calls.filter(c => c.type === 'hass_sharp/run_now').map(c => c.method)
  expect(methods).toContain('Run_747269676765722D31')
  // UUIDs are ASCII; independently derive the request expectation from the persisted ID.
  expect(methods).toContain('Run_' + Array.from(second.id as string, c => c.charCodeAt(0).toString(16).toUpperCase()).join(''))
  // Unsaved trigger changes must not change the list of runnable saved actions.
  button(root, 'Add step').click(); button(root, 'Create Trigger').click()
  runNow.click()
  expect(items()).toHaveLength(2)
  expect(runNow.title).toBe('Runs the last saved version; unsaved changes are not included')
})

it('edits all trigger kinds and boolean overrides without changing its ID, and offers no trigger expressions', () => {
  const { root } = setup()
  for (const type of ['once', 'startup', 'manual', 'entity', 'daily']) {
    chooseTrigger(root, type)
    if (type === 'once') setControl(root, 'Date and time', '2026-12-01T07:30:00+01:00')
    if (type === 'entity') {
      root.querySelector<HTMLButtonElement>('.hs-inspector [aria-label="Entity"]')!.click()
      // Cards on the canvas have their own entity pickers; this edits the trigger in the inspector.
      const inspector = root.querySelector('.hs-inspector')!
      setControl(inspector, 'Search entities', 'sensor.temperature')
      button(inspector, 'Use ID: sensor.temperature').click()
    }
  }
  for (const mode of ['Single', 'Restart', 'Queued', 'Parallel']) {
    chooseMode(root, mode)
    expect(json(root).nodes[0].config.mode).toBe(mode)
  }
  setControl(root, 'Run when Home Assistant starts', 'true')
  const manualRuns = [...root.querySelectorAll('.hs-inspector label')].find(l => l.textContent?.includes('Can be started with Run now'))!
  manualRuns.querySelector('input')!.click()
  expect(root.querySelector('.hs-inspector [aria-label="Entities expression"], .hs-inspector [aria-label="Schedules expression"]')).toBeNull()
  // Each type change started from the new type's defaults: the earlier types' settings are gone.
  expect(json(root).nodes[0]).toMatchObject({ id: 'trigger-1', config: { type: 'daily', startup: true, manual: false, mode: 'Parallel' } })
  expect(Object.keys(json(root).nodes[0].config).sort()).toEqual(['manual', 'mode', 'startup', 'type'])
  expect('entitiesExpression' in json(root).nodes[0].config).toBe(false)
})

it('rejects old saved graph versions without offering upgrade or overwriting the document',async()=>{
 const {root,fixture}=setup(true);fixture.documents.get('motion')!.graph.version=1 as any
 await vi.waitFor(()=>expect(root.textContent).toContain('Unsupported graph version 1; only version 5 is supported'))
 expect(button(root,'Save').disabled).toBe(true)
 expect(root.textContent).not.toContain('Upgrade to')
 expect(fixture.documents.get('motion')!.revision).toBe(4)
})

it('lists node library problems under Node libraries, shows their line, and marks every range of the selected trigger', async () => {
  const { root, fixture } = setup()
  // Replace only the external preview response; the editor/adapter/session stay real.
  fixture.controls.preview = {
    valid: false, source: '// header\nclass VisualAutomation {\n int x = nope;\n void Configure() {}\n void Run_61() {}\n}',
    sourceMap: [{ nodeId: '$library:rooms', startLine: 3, endLine: 3 },
      { nodeId: 'trigger-1', startLine: 4, endLine: 4 }, { nodeId: 'trigger-1', startLine: 5, endLine: 5 }],
    diagnostics: [{ nodeId: '$library:rooms', message: 'Unknown member', severity: 3, startLine: 3, startColumn: 10 }],
  }
  await vi.waitFor(() => expect(root.querySelector('.hs-problems')?.textContent).toContain('Unknown member'))
  const group = root.querySelector('.hs-problems__group')!
  expect(group.querySelector('.hs-problems__step')?.textContent).toBe('Node libraries')
  button(group, 'Unknown member').click()
  expect(root.querySelector('.hs-drawer [role="tab"][aria-selected="true"]')?.textContent).toBe('Generated C#')
  expect(root.querySelector('[data-source-line="3"]')?.getAttribute('aria-current')).toBe('true')
  // A line of a node library belongs to no step.
  button(root, 'Select source line 4').click()
  button(root, 'Select source line 3').click()
  expect(root.querySelector('[data-source-line="3"]')?.getAttribute('aria-current')).toBe('true')
  await vi.waitFor(() => expect(root.querySelector('[data-id="trigger-1"]')?.classList.contains('selected')).toBe(false))
  root.querySelector('[data-id="trigger-1"]')!.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
  expect(root.querySelector('[data-source-line="4"]')?.getAttribute('aria-current')).toBe('true')
  expect(root.querySelector('[data-source-line="5"]')?.getAttribute('aria-current')).toBe('true')
})

it('renders a save-time validation rejection and a structural diagnostic without node or line', async () => {
  const { root, fixture } = setup()
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  fixture.controls.invalid = true
  button(root, 'Save').click()
  await vi.waitFor(() => expect(root.querySelector('[role="alert"]')?.textContent).toContain('Fix the graph errors before saving.'))
  const problem = root.querySelector('.hs-problems .hs-problem')!
  expect(problem.textContent).toBe('Invalid entityLine 3')
  expect(problem.querySelector('[role="img"]')?.getAttribute('aria-label')).toBe('Error')
  expect(button(root, 'Save').disabled).toBe(true)
  expect(chipTip(root)).toBe('Not saved yet')
  fixture.controls.preview = { valid: false, source: '', sourceMap: [], diagnostics: [{ nodeId: null, message: 'Only one outgoing edge per port is supported', severity: 3, startLine: null, startColumn: null }] }
  edit(root, 'Changed')
  await vi.waitFor(() => expect(root.querySelector('.hs-problems')?.textContent).toContain('Only one outgoing edge per port is supported'))
  expect([...root.querySelectorAll('.hs-problems__step')].map(h => h.textContent)).toContain('This automation')
})

it('opens on the Runs tab when it is reached from a run (?drawer=runs), and stays closed once closed', async () => {
  const { root, history } = setup(true, false, '/visual/motion?drawer=runs')
  await saved(root, 4)
  // The address loses the parameter once the tab is open, so a reload does not reopen it.
  expect(history.get()).toBe('/visual/motion')
  const runs = () => [...root.querySelectorAll<HTMLButtonElement>('.hs-drawer [role="tab"]')].find(b => b.textContent?.startsWith('Runs'))!
  expect(root.querySelector('.hs-drawer')!.classList).toContain('hs-drawer--open')
  expect(runs().getAttribute('aria-selected')).toBe('true')
  runs().click()
  expect(root.querySelector('.hs-drawer')!.classList).not.toContain('hs-drawer--open')
  await new Promise(resolve => setTimeout(resolve, 0))
  expect(root.querySelector('.hs-drawer')!.classList).not.toContain('hs-drawer--open')
})

it('removes only the drawer parameter, keeping the others and the hash, and replaces the history entry', async () => {
  const { root, history } = setup(true, false, '/visual/motion?from=list&drawer=runs&x=1#top')
  await saved(root, 4)
  await vi.waitFor(() => expect(history.get()).toBe('/visual/motion?from=list&x=1#top'))
  expect(root.querySelector('.hs-drawer')!.classList).toContain('hs-drawer--open')
  // Replaced, not pushed: Back leaves the page instead of returning to the address with the parameter.
  history.back()
  expect(history.get()).toBe('/')
})

it('opens with the drawer closed otherwise', async () => {
  const { root } = setup(true)
  await saved(root, 4)
  expect(root.querySelector('.hs-drawer')!.classList).not.toContain('hs-drawer--open')
})
it('turns a saved automation off and on from the top bar without a change to save, and rolls back a refused switch', async () => {
  const { root, fixture } = setup(true)
  await saved(root, 4)
  const toggle = () => root.querySelector<HTMLInputElement>('.hs-topbar__enabled input[role="switch"]')!
  await vi.waitFor(() => expect(toggle()).not.toBeNull())
  expect(toggle().getAttribute('aria-label')).toBe('Saved motion on')
  expect(toggle().checked).toBe(true)
  // Nothing selected: the side panel shows the overview.
  const node = root.querySelector('.solid-flow__node')!
  node.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  const overview = () => root.querySelector('.hs-insp-overview')?.textContent ?? ''
  await vi.waitFor(() => expect(root.querySelector('.hs-insp-overview')).not.toBeNull())
  expect(overview()).not.toContain('Off:')
  const undo = button(root, 'Undo')
  const undoBefore = undo?.disabled
  const runNow = () => root.querySelector<HTMLButtonElement>('.hs-topbar__run')!
  const runBefore = runNow().disabled

  toggle().click()
  // Optimistic: the switch and the overview say off at once, before the backend answers.
  expect(toggle().checked).toBe(false)
  await vi.waitFor(() => expect(overview()).toContain('Off: nothing starts this automation by itself. Run now still works.'))
  await vi.waitFor(() => expect(fixture.documents.get('motion')!.enabled).toBe(false))
  expect(fixture.calls).toContainEqual(expect.objectContaining({ request: expect.objectContaining({ operation: 'set_enabled', id: 'motion', enabled: false }) }))
  // It is not an edit: still saved at the same revision, nothing to undo, and Run now is still offered.
  expect(chip(root).textContent).toBe('Saved')
  expect(chipTip(root)).toBe('Saved revision 4')
  if (undo) expect(undo.disabled).toBe(undoBefore)
  expect(runNow().disabled).toBe(runBefore)

  // Refused: the switch goes back and says why.
  fixture.controls.setEnabledError = 'forbidden'
  toggle().click()
  expect(toggle().checked).toBe(true)
  await vi.waitFor(() => expect(root.querySelector('.hs-topbar__notice')?.textContent).toContain('Could not turn it on'))
  expect(toggle().checked).toBe(false)
  expect(overview()).toContain('Off:')
  expect(fixture.documents.get('motion')!.enabled).toBe(false)

  fixture.controls.setEnabledError = undefined
  toggle().click()
  await vi.waitFor(() => expect(fixture.documents.get('motion')!.enabled).toBe(true))
  expect(toggle().checked).toBe(true)
  await vi.waitFor(() => expect(overview()).not.toContain('Off:'))
  expect(chip(root).textContent).toBe('Saved')
})

it('shows a backend warning (an unknown entity) as a warning on the card, in the panel and in Problems, and still saves', async () => {
  const { root, fixture } = setup(true)
  await saved(root, 4)
  fixture.controls.preview = {
    valid: true, source: 'class A {}', sourceMap: [],
    diagnostics: [{ nodeId: 'trigger-1', inputId: 'entity', code: 'entity.unknown', message: 'Home Assistant has no entity binary_sensor.motion', severity: 2, startLine: null, startColumn: null }],
  } as any
  edit(root, 'Motion')
  await vi.waitFor(() => expect(root.querySelector('.hs-problems')?.textContent).toContain('Home Assistant has no entity binary_sensor.motion'))
  // Problems: a warning, not an error.
  const problem = root.querySelector('.hs-problems .hs-problem')!
  expect(problem.classList).toContain('hs-problem--warning')
  expect(problem.querySelector('[role="img"]')?.getAttribute('aria-label')).toBe('Warning')
  // The card: the amber badge, no red outline, the pin not marked invalid.
  const card = root.querySelector('.solid-flow__node[data-id="trigger-1"] .hs-card')!
  expect(card.classList).toContain('hs-card--warning')
  expect(card.classList).not.toContain('hs-flow-card--error')
  expect(card.querySelector('.hs-card__badge')!.classList).toContain('hs-card__badge--warning')
  // The panel: a warning callout; the entity field is not invalid.
  const callout = root.querySelector('.hs-inspector .hs-insp-problem')!
  expect(callout.classList).toContain('hs-insp-problem--warning')
  expect(root.querySelector('.hs-inspector [data-input-id="entity"]')!.getAttribute('aria-invalid')).toBeNull()
  // Save is not blocked; the save chip names no problem.
  await vi.waitFor(() => expect(button(root, 'Save').disabled).toBe(false))
  expect(chip(root).textContent).toBe('Unsaved changes')
  button(root, 'Save').click()
  await saved(root, 5)
  expect(root.querySelector('.hs-problems')?.textContent).toContain('Home Assistant has no entity binary_sensor.motion')
})
