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
afterEach(() => { dispose?.(); client?.clear(); document.body.innerHTML = '' })
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
  expect(fixture.calls).toContainEqual({ type: 'hass_sharp/run_now', automationId: `visual:${doc.id}`, className: 'VisualAutomation', method: 'Run' })
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
