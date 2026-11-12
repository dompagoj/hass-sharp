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
function setup(saved = false, minimal = false) {
  const fixture = visualFixture()
  if (saved) fixture.documents.set('motion', { version: 1, id: 'motion', revision: 4, graph: createGraph('Saved motion'), source: 'saved', sourceMap: [], automationId: 'visual:motion' })
  if (minimal) fixture.documents.get('motion')!.graph.nodes = fixture.documents.get('motion')!.graph.nodes.map(({ label, position, size, ...node }) => node as any)
  const root = document.createElement('div'); document.body.append(root)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const history = createMemoryHistory(); history.set({ value: saved ? '/visual/motion' : '/visual/new' })
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={{ ...fixture, user: { is_admin: true } } as any}>
    <MemoryRouter history={history} root={props => <><A href="/elsewhere">Leave</A><A href="/visual/new?name=Fresh">New visual</A>{props.children}</>}>
      <Route path="/visual/:id" component={VisualAutomationView} />
      <Route path="/elsewhere" component={() => <h1>Elsewhere</h1>} />
    </MemoryRouter></HassCtx.Provider></QueryClientProvider>, root)
  return { root, fixture }
}
const button = (root: Element, text: string) => [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === text || b.getAttribute('aria-label') === text)!
const edit = (root: Element, text: string) => { const input = root.querySelector<HTMLInputElement>('[aria-label="Node label"]')!; input.value = text; input.dispatchEvent(new Event('input', { bubbles: true })) }
it('creates, edits and saves separate drafts on HTTP without crypto.randomUUID', async () => {
  // HTTP LAN origins expose getRandomValues but not randomUUID.
  vi.stubGlobal('crypto', { getRandomValues: crypto.getRandomValues.bind(crypto) })
  const { root, fixture } = setup()
  button(root, 'Add Wait').click()
  button(root, 'Add Wait').click()
  await vi.waitFor(() => expect(button(root, 'Save graph').disabled).toBe(false))
  button(root, 'Save graph').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Saved · revision 1'))
  const first = [...fixture.documents.values()][0]
  const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/
  expect(first.id.slice('visual-'.length)).toMatch(uuid)
  expect(first.graph.nodes).toHaveLength(5)
  for (const node of first.graph.nodes.slice(3)) expect(node.id).toMatch(uuid)
  expect(new Set(first.graph.nodes.map(node => node.id)).size).toBe(5)

  root.querySelector<HTMLAnchorElement>('a[href="/visual/new?name=Fresh"]')!.click()
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Fresh'))
  await vi.waitFor(() => expect(button(root, 'Save graph').disabled).toBe(false))
  button(root, 'Save graph').click()
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
  await vi.waitFor(() => expect(button(root, 'Save graph').disabled).toBe(false))
  button(root, 'Save graph').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Saved · revision 1'))
  expect(fixture.documents.size).toBe(1)
  const doc = [...fixture.documents.values()][0]
  expect(doc.graph.nodes[0].label).toBe('Motion trigger')
  expect(doc.source).toContain('generated on server')
  await vi.waitFor(() => expect(root.textContent).toContain('No runs yet'))
  button(root, 'Run now').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Request accepted'))
  expect(fixture.calls).toContainEqual({ type: 'hass_sharp/run_now', automationId: `visual:${doc.id}`, className: 'VisualAutomation', method: 'Run_747269676765722D31' })
  root.querySelector<HTMLAnchorElement>('a')!.click(); await Promise.resolve()
  expect(root.textContent).toContain('Elsewhere')
})
it('keeps a conflicted local draft until the user explicitly reloads it', async () => {
  const { root, fixture } = setup()
  await vi.waitFor(() => expect(button(root, 'Save graph').disabled).toBe(false))
  button(root, 'Save graph').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Saved · revision 1'))
  fixture.controls.conflict = true
  edit(root, 'My unsaved change')
  await vi.waitFor(() => expect(button(root, 'Save graph').disabled).toBe(false))
  button(root, 'Save graph').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Changed elsewhere'))
  expect((root.querySelector('[aria-label="Node label"]') as HTMLInputElement).value).toBe('My unsaved change')
  button(root, 'Reload saved graph').click()
  expect(root.querySelector('dialog[open]')?.textContent).toContain('Discard local changes')
  button(root, 'Reload and discard').click()
  await vi.waitFor(() => expect((root.querySelector('[aria-label="Node label"]') as HTMLInputElement).value).toBe('Entity changes'))
})
it('shows persistence succeeded when runtime activation fails', async () => {
  const { root, fixture } = setup(); fixture.controls.active = false
  await vi.waitFor(() => expect(button(root, 'Save graph').disabled).toBe(false))
  button(root, 'Save graph').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Saved, but not activated'))
  expect(root.textContent).toContain('Compilation failed during activation')
  root.querySelector<HTMLAnchorElement>('a')!.click(); await Promise.resolve()
  expect(root.textContent).toContain('Elsewhere')
})
it('maps diagnostics onto nodes and source lines and blocks invalid saves', async () => {
  const { root, fixture } = setup(); fixture.controls.invalid = true
  await vi.waitFor(() => expect(root.textContent).toContain('Invalid entity'))
  expect(button(root, 'Save graph').disabled).toBe(true)
  expect(root.querySelector('[data-id="trigger-1"]')?.textContent).toContain('error')
  button(root, 'Invalid entity · line 3').click()
  expect(root.querySelector('[data-source-line="3"]')?.getAttribute('aria-current')).toBe('true')
  expect(root.querySelector('[data-id="trigger-1"]')?.classList.contains('selected')).toBe(true)
})
it('reads and renames a saved graph without changing its automation identity', async () => {
  const { root, fixture } = setup(true)
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Saved motion'))
  const name = root.querySelector<HTMLInputElement>('[aria-label="Automation name"]')!
  name.value = 'Renamed motion'; name.dispatchEvent(new Event('input', { bubbles: true }))
  await vi.waitFor(() => expect(button(root, 'Save graph').disabled).toBe(false))
  button(root, 'Save graph').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Saved · revision 5'))
  expect(fixture.documents.get('motion')?.graph.name).toBe('Renamed motion')
  expect(fixture.documents.get('motion')?.automationId).toBe('visual:motion')
  expect(fixture.calls.filter(c => c.request?.operation === 'save').at(-1).request.expectedRevision).toBe(4)
})

it('creates a fresh draft when navigating from a saved visual graph to new', async () => {
  const { root } = setup(true)
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Saved motion'))
  root.querySelector<HTMLAnchorElement>('a[href="/visual/new?name=Fresh"]')!.click()
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Fresh'))
  expect(root.textContent).toContain('Not saved')
  expect(root.querySelector('[aria-label="Run history"]')).toBeNull()
  expect(button(root, 'Undo').disabled).toBe(true)
})

it('opens valid API graphs without optional canvas metadata and preserves their identity', async () => {
  const { root, fixture } = setup(true, true)
  await vi.waitFor(() => expect(root.querySelector('[data-id="trigger-1"]')).not.toBeNull())
  expect(root.querySelector('[data-id="trigger-1"]')?.textContent).toContain('trigger')
  await vi.waitFor(() => expect(button(root, 'Save graph').disabled).toBe(false))
  button(root, 'Save graph').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Saved · revision 5'))
  expect(fixture.documents.get('motion')?.graph.nodes[0]).toMatchObject({ id: 'trigger-1', config: { entity: 'binary_sensor.motion' } })
})


const control = (root: Element, label: string) => root.querySelector<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(`[aria-label="${label}"]`)!
function setControl(root: Element, label: string, value: string) {
  const field = control(root, label)
  expect(field, label).not.toBeNull()
  field.value = value
  field.dispatchEvent(new Event(field.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true }))
}
function json(root: Element) {
  button(root, 'Graph JSON').click()
  return JSON.parse(root.querySelector('[aria-label="Graph JSON"]')!.textContent!)
}

it('authors two v2 triggers and C# sections, saves/reloads them, then dispatches either saved action', async () => {
  const { root, fixture } = setup()
  setControl(root, 'C# imports', 'using System.Globalization;')
  setControl(root, 'C# members', 'private string Prefix() => "Heating";')
  setControl(root, 'Trigger type', 'daily')
  setControl(root, 'Daily time', '07:30')
  setControl(root, 'Time zone', 'Europe/Zagreb')
  setControl(root, 'Concurrency mode', 'Restart')
  button(root, 'Add Entity changes').click()
  setControl(root, 'Node label', 'Manual evening')
  setControl(root, 'Trigger type', 'manual')
  setControl(root, 'Connect to', 'service-1')
  button(root, 'Connect').click()
  const draft = json(root)
  const second = draft.nodes.at(-1)
  expect(draft).toMatchObject({ version: 2, imports: 'using System.Globalization;', members: 'private string Prefix() => "Heating";' })
  expect(draft.nodes[0]).toMatchObject({ id: 'trigger-1', config: { type: 'daily', time: '07:30', timeZone: 'Europe/Zagreb', mode: 'Restart' } })
  expect(second).toMatchObject({ kind: 'trigger', config: { type: 'manual' } })
  expect(draft.edges).toContainEqual({ id: `${second.id}->service-1`, source: second.id, target: 'service-1' })
  await vi.waitFor(() => expect(button(root, 'Save graph').disabled).toBe(false))
  button(root, 'Save graph').click()
  await vi.waitFor(() => expect(root.textContent).toContain('Saved · revision 1'))
  const saved = [...fixture.documents.values()][0]
  expect(saved.graph).toEqual(draft)
  setControl(root, 'C# members', 'unsaved member')
  button(root, 'Reload saved graph').click(); button(root, 'Reload and discard').click()
  await vi.waitFor(() => expect(control(root, 'C# members').value).toBe('private string Prefix() => "Heating";'))
  expect(json(root)).toEqual(draft)
  const actions = root.querySelector<HTMLSelectElement>('.hs-run-action select')!
  expect(actions.options).toHaveLength(2)
  for (const option of Array.from(actions.options)) {
    actions.value = option.value; actions.dispatchEvent(new Event('change', { bubbles: true }))
    button(root, 'Run now').click()
    await vi.waitFor(() => expect(button(root, 'Run now').disabled).toBe(false))
  }
  const methods = fixture.calls.filter(c => c.type === 'hass_sharp/run_now').map(c => c.method)
  expect(methods).toContain('Run_747269676765722D31')
  // UUIDs are ASCII; independently derive the request expectation from the persisted ID.
  expect(methods).toContain('Run_' + Array.from(second.id as string, c => c.charCodeAt(0).toString(16).toUpperCase()).join(''))
  // Unsaved trigger changes must not change the list of runnable saved actions.
  button(root, 'Add Entity changes').click()
  expect(root.querySelectorAll('.hs-run-action option')).toHaveLength(2)
})

it('edits all trigger kinds, boolean overrides and expressions without changing its ID or custom code', () => {
  const { root } = setup()
  for (const type of ['once', 'startup', 'manual', 'entity', 'daily']) {
    setControl(root, 'Trigger type', type)
    if (type === 'once') setControl(root, 'Once timestamp', '2026-12-01T07:30:00+01:00')
    if (type === 'entity') setControl(root, 'Entity ID', 'sensor.temperature')
  }
  for (const mode of ['Single', 'Restart', 'Queued', 'Parallel']) {
    setControl(root, 'Concurrency mode', mode)
    expect(json(root).nodes[0].config.mode).toBe(mode)
  }
  setControl(root, 'Startup override', 'true')
  setControl(root, 'Allow manual runs', 'false')
  setControl(root, 'Entities expression', '["sensor.temperature"]')
  setControl(root, 'Schedules expression', '[]')
  expect(json(root).nodes[0]).toMatchObject({ id: 'trigger-1', config: {
    type: 'daily', startup: true, manual: false, entitiesExpression: '["sensor.temperature"]', schedulesExpression: '[]',
    at: '2026-12-01T07:30:00+01:00', entity: 'sensor.temperature',
  } })
  button(root, 'Add Custom C#').click()
  setControl(root, 'Custom C# section', 'await Task.Delay(10, CancellationToken);')
  expect(json(root).nodes.at(-1).config.code).toBe('await Task.Delay(10, CancellationToken);')
})

it('keeps v1 editing and Run, and upgrades only when explicitly chosen', async () => {
  const { root, fixture } = setup(true)
  fixture.documents.get('motion')!.graph.version = 1
  await vi.waitFor(() => expect(root.querySelector('h1')?.textContent).toBe('Saved motion'))
  expect(control(root, 'C# imports')).toBeNull()
  expect(button(root, 'Add Entity changes').disabled).toBe(true)
  edit(root, 'Legacy trigger')
  expect(json(root).version).toBe(1)
  button(root, 'Run now').click()
  await vi.waitFor(() => expect(fixture.calls).toContainEqual({ type: 'hass_sharp/run_now', automationId: 'visual:motion', className: 'VisualAutomation', method: 'Run' }))
  button(root, 'Upgrade to graph v2').click()
  expect(json(root).version).toBe(2)
  expect(json(root).nodes[0].id).toBe('trigger-1')
  expect(button(root, 'Add Entity changes').disabled).toBe(false)
  // The saved v1 action remains available until the upgrade is saved.
  expect(root.querySelector('.hs-run-action option')?.getAttribute('value')).toBe('VisualAutomation::Run')
})

it('locates import/member diagnostics and all ranges for the same trigger from backend mappings', async () => {
  const { root, fixture } = setup()
  // Replace only the external preview response; the editor/adapter/session stay real.
  fixture.controls.preview = {
    valid: false, source: 'using Missing;\nclass VisualAutomation {\n int x = nope;\n void Configure() {}\n void Run_61() {}\n}',
    sourceMap: [{ nodeId: '$imports', startLine: 1, endLine: 1 }, { nodeId: '$members', startLine: 3, endLine: 3 },
      { nodeId: 'trigger-1', startLine: 4, endLine: 4 }, { nodeId: 'trigger-1', startLine: 5, endLine: 5 }],
    diagnostics: [{ nodeId: '$members', message: 'Unknown member', severity: 3, startLine: 3, startColumn: 10 }],
  }
  await vi.waitFor(() => expect(root.textContent).toContain('Unknown member'))
  button(root, 'Unknown member · line 3').click()
  expect(control(root, 'C# members').getAttribute('aria-invalid')).toBe('true')
  await vi.waitFor(() => expect(document.activeElement).toBe(control(root, 'C# members')))
  expect(root.querySelector('[data-source-line="3"]')?.getAttribute('aria-current')).toBe('true')
  button(root, 'Select source line 1').click()
  await vi.waitFor(() => expect(document.activeElement).toBe(control(root, 'C# imports')))
  root.querySelector('[data-id="trigger-1"]')!.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
  expect(root.querySelector('[data-source-line="4"]')?.getAttribute('aria-current')).toBe('true')
  expect(root.querySelector('[data-source-line="5"]')?.getAttribute('aria-current')).toBe('true')
})

it('renders a save-time validation rejection and a structural diagnostic without node or line', async () => {
  const { root, fixture } = setup()
  await vi.waitFor(() => expect(button(root, 'Save graph').disabled).toBe(false))
  fixture.controls.invalid = true
  button(root, 'Save graph').click()
  await vi.waitFor(() => expect(root.querySelector('[role="alert"]')?.textContent).toContain('Fix the graph errors before saving.'))
  expect(root.querySelector('[aria-label="Graph diagnostics"]')?.textContent).toContain('Error: Invalid entity · line 3')
  expect(button(root, 'Save graph').disabled).toBe(true)
  expect(root.textContent).toContain('Not saved')
  fixture.controls.preview = { valid: false, source: '', sourceMap: [], diagnostics: [{ nodeId: null, message: 'Only one outgoing edge per port is supported', severity: 3, startLine: null, startColumn: null }] }
  edit(root, 'Changed')
  await vi.waitFor(() => expect(root.querySelector('[aria-label="Graph diagnostics"]')?.textContent).toBe('Error: Only one outgoing edge per port is supported'))
})
