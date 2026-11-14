import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { createMemoryHistory, MemoryRouter, Route } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
import { editors, resetHost } from './fake-monaco'
import { visualFixture } from './visual-fixture'
import { run } from './run-fixture'
import { HassCtx } from '../src/context'
import { NodeConfig } from '../src/visual/NodeConfig'
import { VisualEditor } from '../src/visual/VisualEditor'
import { RunTraceView } from '../src/views/RunTraceView'
import { TypeDefinitions } from '../src/visual/typeDefinition'
import { LibraryApiError, libraryApi, type TypeDefinition } from '../src/visual/library-api'
import { nodeCatalog, type VisualCatalogSnapshot } from '../src/visual/contract'
import { addNode, createGraph, type VisualGraph } from '../src/visual/graph'
import type { RunTrace, RunTraceResponse, TraceEvent } from '../src/types'

vi.mock('monaco-editor', async () => (await import('./fake-monaco')).host)
vi.mock('monaco-vim', () => ({ initVimMode: () => ({ dispose() {} }) }))
// Counts imports of the code view (and so of Monaco): none until a definition is first opened.
const codeView = vi.hoisted(() => ({ imported: 0 }))
vi.mock('../src/components/Editor/CodeView', async importOriginal => { codeView.imported++; return importOriginal() })

// The C# types in the panel are links to their definitions (Task 31): a library, Custom C# or HassSharp type opens a
// dialog with its code, read only; C#'s own types are plain text.
const catalog = rich.catalog as unknown as VisualCatalogSnapshot
const library = { id: rich.sourceId, name: 'Typed sample.cs', revision: rich.revision, source: rich.source }
/** Trigger → Make (a Custom C# node whose code declares the record Own) → Consume (a library step: IReading, int, string?, string, Mode). */
const graph = () => structuredClone(rich.cases.find(c => c.name === 'custom: a record the code declares into the library interface it implements')!.graph) as unknown as VisualGraph
const own = 'global::NodeLib.Custom_make.Own'
const reading: TypeDefinition = { name: 'IReading', kind: 'source', text: 'public interface IReading { }', origin: { library: 'shared', file: 'Typed sample.cs' }, line: 1 }

let dispose: (() => void) | undefined
beforeEach(() => resetHost())
afterEach(async () => {
  dispose?.(); dispose = undefined
  vi.useRealTimers(); vi.restoreAllMocks()
  document.body.innerHTML = ''
  await new Promise(resolve => setTimeout(resolve, 0))
})
const host = () => { const el = document.createElement('div'); el.className = 'hs-app'; document.body.append(el); return el }
const button = (scope: ParentNode, label: string) =>
  [...scope.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === label || b.getAttribute('aria-label') === label)
const link = (scope: ParentNode, name: string) => scope.querySelector<HTMLButtonElement>(`button.hs-csharp-link[aria-label="Show the C# definition of ${name}"]`)
const row = (scope: ParentNode, id: string) => scope.querySelector<HTMLElement>(`.hs-insp-input[data-input-id="${id}"]`)!
/** The definition's dialog, while open. */
const shown = () => [...document.querySelectorAll<HTMLDialogElement>('dialog.hs-dialog')].find(d => d.open && d.querySelector('.hs-typedef'))
/** The read-only code view's text, once Monaco has loaded. */
const code = () => editors.at(-1)?.model.getValue()

/** The panel of one step, with links when `links` is given. */
function panel(id: string, links?: Partial<Parameters<typeof TypeDefinitions>[0]>) {
  const el = host(), g = graph(), node = g.nodes.find(n => n.id === id)!
  const config = () => <NodeConfig graph={g} node={node} catalog={catalog} onChange={() => {}} onGraphChange={() => {}} />
  dispose = render(() => links
    ? <TypeDefinitions load={async () => reading} graph={() => g} catalog={() => catalog} {...links}>{config()}</TypeDefinitions>
    : config(), el)
  return el
}

// First in the file: the code view's module is imported once per file, by the first dialog opened.
it('loads Monaco only when a definition is first opened, not with the page or the panel', async () => {
  const el = panel('consume', {})
  await new Promise(resolve => setTimeout(resolve, 20))
  expect(link(row(el, 'reading'), 'NodeLib.IReading')).not.toBeNull()
  expect(codeView.imported, 'the panel with its links imports no code view').toBe(0)
  link(row(el, 'reading'), 'NodeLib.IReading')!.click()
  await vi.waitFor(() => expect(code()).toBe('public interface IReading { }'))
  expect(codeView.imported).toBe(1)
})

it('links the library, Custom C# and HassSharp parts of a pin\'s C# type, and leaves C#\'s own types as plain text', () => {
  const el = panel('consume', {})
  expect(link(row(el, 'reading'), 'NodeLib.IReading')).not.toBeNull()
  expect(link(row(el, 'mode'), 'NodeLib.Mode')).not.toBeNull()
  // int, string? and string are C#'s own: the same text, no link.
  for (const [id, text] of [['type', 'int'], ['output', 'string?'], ['service', 'string']]) {
    expect(row(el, id).querySelector('.hs-csharp-type')!.textContent, id).toBe(text)
    expect(row(el, id).querySelector('.hs-csharp-link'), id).toBeNull()
  }
  // A link reads as the type's text, inside the chip's code, and is a real button.
  const chip = row(el, 'reading').querySelector('.hs-type-chip')!
  expect(chip.textContent).toBe('Reading, C# type NodeLib.IReading')
  expect(link(chip, 'NodeLib.IReading')!.type).toBe('button')
  dispose!(); document.body.innerHTML = ''

  // An output of a type a Custom C# node's code declares: written as the code names it.
  const make = panel('make', {})
  const output = make.querySelector('.hs-insp-output')!
  expect(output.querySelector('.hs-csharp-type')!.textContent).toBe('Own')
  expect(link(output, 'Own')).not.toBeNull()
  dispose!(); document.body.innerHTML = ''

  // Outside an editor page or a trace (no definitions to open), the same text with no link.
  const plain = panel('consume')
  expect(row(plain, 'reading').querySelector('.hs-csharp-type')!.textContent).toBe('NodeLib.IReading')
  expect(plain.querySelector('.hs-csharp-link')).toBeNull()
})

it('never offers a definition for C#\'s own types: a True/False and a text pin are plain text', () => {
  const el = host()
  let g = addNode(createGraph('Plain'), 'if', 'branch')
  g = addNode(g, 'constant', 'value')
  dispose = render(() => <TypeDefinitions load={async () => reading} graph={() => g} catalog={() => nodeCatalog}>
    <NodeConfig graph={g} node={g.nodes.find(n => n.id === 'branch')!} catalog={nodeCatalog} onChange={() => {}} onGraphChange={() => {}} />
    <NodeConfig graph={g} node={g.nodes.find(n => n.id === 'value')!} catalog={nodeCatalog} onChange={() => {}} onGraphChange={() => {}} />
  </TypeDefinitions>, el)
  expect([...el.querySelectorAll('.hs-csharp-type')].map(c => c.textContent)).toEqual(['bool', 'string', 'string'])
  expect(el.querySelector('.hs-csharp-link')).toBeNull()
})

it('opens the definition the backend answers, with where it is declared, read only, and gives focus back to the link', async () => {
  const load = vi.fn(async () => reading), opened = vi.fn()
  const el = panel('consume', { load, onOpenLibrary: opened })
  const target = link(row(el, 'reading'), 'NodeLib.IReading')!
  target.focus(); target.click()
  // A library type's id goes alone: no node's code with it.
  expect(load).toHaveBeenCalledWith('global::NodeLib.IReading', undefined)
  await vi.waitFor(() => expect(code()).toBe('public interface IReading { }'))
  const dialog = shown()!
  expect(dialog.querySelector('h2')!.textContent).toBe('IReading')
  expect(dialog.querySelector('.hs-typedef__origin')!.textContent).toBe('Declared in the node library Typed sample.cs.')
  expect(editors.at(-1)!.options).toMatchObject({ readOnly: true, domReadOnly: true, ariaLabel: 'C# definition of IReading' })
  expect(dialog.querySelector('.hs-code-view')).not.toBeNull()
  // Escape closes it and the link has focus again.
  dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
  await vi.waitFor(() => expect(shown()).toBeUndefined())
  expect(document.activeElement).toBe(target)
  // Open in library goes to the file, at the declaration's line.
  target.click()
  await vi.waitFor(() => expect(button(shown()!, 'Open in library')).toBeDefined())
  expect(button(shown()!, 'Edit code')).toBeUndefined()
  button(shown()!, 'Open in library')!.click()
  expect(opened).toHaveBeenCalledWith('shared', 1)
  await vi.waitFor(() => expect(shown()).toBeUndefined())
})

it('says it is loading, then shows the backend\'s refusal in the dialog and tries again on request', async () => {
  let fail = true
  let release!: () => void
  const load = vi.fn(() => new Promise<TypeDefinition>((resolve, reject) => {
    release = () => fail ? reject(new LibraryApiError('not_found', 'There is no type NodeLib.Mode; it may have been renamed or removed')) : resolve({ ...reading, name: 'Mode', text: 'public enum Mode { Off, On }' })
  }))
  const el = panel('consume', { load })
  link(row(el, 'mode'), 'NodeLib.Mode')!.click()
  await vi.waitFor(() => expect(shown()).toBeDefined())
  // Until the answer, the title is the link's text and the body says it is loading.
  expect(shown()!.querySelector('h2')!.textContent).toBe('NodeLib.Mode')
  expect(shown()!.querySelector('[role="status"]')!.textContent).toBe('Loading the definition…')
  release()
  await vi.waitFor(() => expect(shown()!.querySelector('[role="alert"]')).not.toBeNull())
  expect(shown()!.querySelector('[role="alert"]')!.textContent).toContain('Could not show the definition: There is no type NodeLib.Mode; it may have been renamed or removed')
  expect(button(shown()!, 'Open in library')).toBeUndefined()
  fail = false
  button(shown()!, 'Try again')!.click()
  release()
  await vi.waitFor(() => expect(code()).toBe('public enum Mode { Off, On }'))
  expect(shown()!.querySelector('[role="alert"]')).toBeNull()
  expect(load).toHaveBeenCalledTimes(2)
})

it('drops a slow answer that arrives after Close, or after another type was opened', async () => {
  const fixture = visualFixture({ catalog, libraries: [library] })
  let release!: () => void
  fixture.controls.holdTypeDefinition = new Promise<void>(resolve => { release = resolve })
  const el = panel('consume', { load: (type, code) => libraryApi(fixture).typeDefinition(type, code) })
  // IReading is asked for and its answer held; the dialog is closed before it arrives.
  link(row(el, 'reading'), 'NodeLib.IReading')!.click()
  await vi.waitFor(() => expect(shown()?.querySelector('[role="status"]')).not.toBeNull())
  button(shown()!, 'Close')!.click()
  await vi.waitFor(() => expect(shown()).toBeUndefined())
  // Mode is opened and answers at once; then IReading's late answer arrives.
  fixture.controls.holdTypeDefinition = undefined
  link(row(el, 'mode'), 'NodeLib.Mode')!.click()
  await vi.waitFor(() => expect(code()).toBe('public enum Mode { Off, On }'))
  release()
  await new Promise(resolve => setTimeout(resolve, 20))
  expect(fixture.calls.filter(m => m.request?.operation === 'type_definition').map(m => m.request.type)).toEqual(['global::NodeLib.IReading', 'global::NodeLib.Mode'])
  expect(shown()!.querySelector('h2')!.textContent).toBe('Mode')
  expect(code()).toBe('public enum Mode { Off, On }')
  expect(shown()!.textContent).not.toContain('IReading')
  // An answer that arrives after Close opens nothing.
  fixture.controls.holdTypeDefinition = new Promise<void>(resolve => { release = resolve })
  button(shown()!, 'Close')!.click()
  await vi.waitFor(() => expect(shown()).toBeUndefined())
  link(row(el, 'reading'), 'NodeLib.IReading')!.click()
  await vi.waitFor(() => expect(shown()).toBeDefined())
  button(shown()!, 'Close')!.click()
  release()
  await new Promise(resolve => setTimeout(resolve, 20))
  expect(shown()).toBeUndefined()
})

it('asks for a type a Custom C# node declares with that node\'s code, and offers Edit code on the editor page', async () => {
  const el = host(), fixture = visualFixture({ catalog, libraries: [library] })
  fixture.documents.set('saved', { version: 1, id: 'saved', revision: 1, graph: graph(), source: '// fixture', sourceMap: [], automationId: 'visual:saved', currentRevisionActive: true, catalogGeneration: 'saved-fixture' })
  const opened = vi.fn()
  dispose = render(() => <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <HassCtx.Provider value={{ ...fixture, user: { is_admin: true } } as any}>
      <VisualEditor name="Code" documentId="saved" onOpenLibrary={opened} />
    </HassCtx.Provider></QueryClientProvider>, el)
  const card = (id: string) => el.querySelector<HTMLElement>(`.solid-flow__node[data-id="${id}"]`)
  await vi.waitFor(() => expect(card('make')).not.toBeNull())
  card('make')!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  const inspector = () => el.querySelector<HTMLElement>('aside[aria-label="Node inspector"]')!
  await vi.waitFor(() => expect(link(inspector(), 'Own')).not.toBeNull())
  link(inspector(), 'Own')!.click()
  await vi.waitFor(() => expect(code()).toBe('public sealed record Own(string State) : IReading;'))
  const source = graph().nodes.find(n => n.id === 'make')!.config.source
  const asked = fixture.calls.filter(m => m.request?.operation === 'type_definition')
  expect(asked.at(-1)!.request).toEqual({ operation: 'type_definition', type: own, nodeId: 'make', source })
  expect(shown()!.querySelector('h2')!.textContent).toBe('Own')
  expect(shown()!.querySelector('.hs-typedef__origin')!.textContent).toBe('Declared in the code of the step Make.')
  // Edit code closes the definition and opens the node's code editor.
  button(shown()!, 'Edit code')!.click()
  await vi.waitFor(() => expect(document.querySelector<HTMLDialogElement>('dialog.hs-code-dialog')?.open).toBe(true))
  expect(shown()).toBeUndefined()
})

/** The run's events: Make gave an Own; Consume took it and the mode On. */
function trace(): RunTraceResponse {
  const list: TraceEvent[] = [
    { seq: 1, node: 't', frame: '', t: 0, ms: 1, status: 'ok', port: 'next' },
    { seq: 2, node: 'make', frame: '', t: 1, ms: 2, status: 'ok', port: 'next', outputs: { result: { type: own, v: { State: 'on' } } } },
    { seq: 3, node: 'consume', frame: '', t: 3, ms: 1, status: 'ok', port: 'next', inputs: { reading: { type: own, v: { State: 'on' }, src: { node: 'make', port: 'result' } }, mode: { type: 'global::NodeLib.Mode', v: 1 } } },
  ]
  const summary: RunTrace['summary'] = Object.fromEntries(list.map(e => [e.node, { count: 1, failed: 0, last: 'ok' as const }]))
  return {
    version: 1, live: false,
    run: run({ runId: 'run-1', visualId: 'typed', visualRevision: 2 }),
    snapshot: { revision: 'rev', visualId: 'typed', visualRevision: 2, graph: graph(), catalog },
    trace: { format: 1, runId: 'run-1', rootNodeId: 't', startedUtc: '2026-11-12T01:00:01Z', complete: true, truncated: false, dropped: 0, trigger: { platform: 'manual' }, events: list, summary },
  }
}

it('offers the definitions read only in a run\'s trace: a library type as it is now, a Custom C# type from the run\'s code', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  const el = host(), fixture = visualFixture({ catalog, libraries: [library] })
  // The node's code in the stored run, which the editor's copy may no longer match.
  const ran = trace()
  const callWS = vi.fn(async (message: any) => {
    if (message.type === 'hass_sharp/run_trace') return structuredClone(ran)
    if (message.type === 'hass_sharp/visual' && message.request.operation === 'read') return { version: 1, ok: true, result: { id: 'typed', revision: 2, graph: graph() } }
    if (message.type === 'hass_sharp/node_library' && message.request.operation === 'type_definition') return fixture.callWS(message)
    return []
  })
  const history = createMemoryHistory()
  history.set({ value: '/visual/typed/runs/run-1' })
  dispose = render(() => <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <HassCtx.Provider value={{ callWS, user: { is_admin: true } } as any}>
      <MemoryRouter history={history}>
        <Route path="/visual/:id/runs/:runId" component={RunTraceView} />
        <Route path="/libraries" component={() => <p class="library-page">Libraries</p>} />
      </MemoryRouter>
    </HassCtx.Provider></QueryClientProvider>, el)
  const card = (id: string) => el.querySelector<HTMLElement>(`.solid-flow__node[data-id="${id}"]`)
  const values = () => el.querySelector<HTMLElement>('[aria-label="Step values"]')!
  const port = (id: string) => values().querySelector<HTMLElement>(`.hs-trace-port[data-port="${id}"]`)!
  await vi.waitFor(() => expect(card('consume')).not.toBeNull())
  card('consume')!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  await vi.waitFor(() => expect(port('mode')).not.toBeNull())

  // A Custom C# type: from the run's own code, with no Edit code (a run is read only).
  link(port('reading'), 'Own')!.click()
  await vi.waitFor(() => expect(code()).toBe('public sealed record Own(string State) : IReading;'))
  const asked = () => callWS.mock.calls.map(([m]) => m).filter(m => m.request?.operation === 'type_definition')
  expect(asked().at(-1)!.request).toMatchObject({ type: own, nodeId: 'make', source: ran.snapshot.graph.nodes.find(n => n.id === 'make')!.config.source })
  expect(shown()!.textContent).toContain('From the code this run used.')
  expect(button(shown()!, 'Edit code')).toBeUndefined()
  expect(editors.at(-1)!.options.readOnly).toBe(true)
  button(shown()!, 'Close')!.click()
  await vi.waitFor(() => expect(shown()).toBeUndefined())

  // A library type: the library as it is now, which may have changed since the run.
  link(port('mode'), 'NodeLib.Mode')!.click()
  await vi.waitFor(() => expect(code()).toBe('public enum Mode { Off, On }'))
  expect(asked().at(-1)!.request).toEqual({ operation: 'type_definition', type: 'global::NodeLib.Mode' })
  expect(shown()!.querySelector('.hs-typedef__origin')!.textContent).toBe('Declared in the node library Typed sample.cs.')
  expect(shown()!.textContent).toContain('This is the node library as it is now; it may have changed since this run.')
  button(shown()!, 'Open in library')!.click()
  await vi.waitFor(() => expect(el.querySelector('.library-page')).not.toBeNull())
  expect(history.get()).toBe('/libraries?file=shared&line=3')
})
