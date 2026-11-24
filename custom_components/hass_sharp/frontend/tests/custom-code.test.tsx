import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { readFileSync, existsSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
import { completions, definitions, editors, formats, hovers, markers, resetHost, signatures } from './fake-monaco'
import { add, empty, visualFixture } from './visual-fixture'
import { run } from './run-fixture'
import { EntityDefinitionView } from '../src/views/EntityDefinitionView'
import { createMemoryHistory, MemoryRouter, Route } from '@solidjs/router'
import { RunTraceView } from '../src/views/RunTraceView'
import { HassCtx } from '../src/context'
import { VisualEditor } from '../src/visual/VisualEditor'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { CustomCodeEditor } from '../src/visual/CustomCodeEditor'
import { applyClassCode, applyCustomCode, exampleSource } from '../src/visual/customCode'
import { addNode, connectNodes, createGraph, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import { connectData, customSignature, dataOutputs, type ClassSetting, type ClassSignature, type CustomSignature, type VisualCatalogSnapshot } from '../src/visual/contract'

vi.mock('monaco-editor', async () => (await import('./fake-monaco')).host)
vi.mock('monaco-vim', () => ({ initVimMode: () => ({ dispose() {} }) }))

const catalog = rich.catalog as unknown as VisualCatalogSnapshot
const S = 'global::System.String', I = 'global::System.Int32', L = 'global::System.Int64', D = 'global::System.Double'
const lit = (type: string, value: unknown) => ({ kind: 'literal' as const, type, value })
const template = exampleSource('custom', catalog)
const signature = (inputs: Record<string, string>, outputs: Record<string, string>, pure = false, title = 'Rename this node'): CustomSignature => ({
  title, description: '', pure, types: {},
  inputs: Object.fromEntries(Object.entries(inputs).map(([id, type]) => [id, { types: [type], required: true, label: id[0].toUpperCase() + id.slice(1), description: '', nullable: false, hasDefaultValue: false }])),
  outputs: Object.fromEntries(Object.entries(outputs).map(([id, type]) => [id, { type, label: id[0].toUpperCase() + id.slice(1), description: '' }])),
})

let dispose: (() => void) | undefined
beforeEach(() => resetHost())
afterEach(async () => {
  dispose?.(); dispose = undefined
  vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals()
  document.body.innerHTML = ''
  await new Promise(resolve => setTimeout(resolve, 0))
})
const root = () => { const el = document.createElement('div'); el.className = 'hs-app'; document.body.append(el); return el }
const button = (scope: ParentNode, label: string) =>
  [...scope.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === label || b.getAttribute('aria-label') === label)

/** A saved automation whose Custom C# node `code` (the template) is wired: Constant → text, result → Log. */
function sample(): VisualGraph {
  let g = createGraph('Code')
  g = addNode(g, 'custom', 'code', catalog)
  g = addNode(g, 'constant', 'words', catalog)
  g = updateNode(g, 'words', { config: { ...g.nodes.find(n => n.id === 'words')!.config, type: 'string', value: lit('string', 'hello') } })
  g = addNode(g, 'log', 'note', catalog)
  g = connectNodes(connectNodes(g, 'service-1', 'code', undefined, catalog), 'code', 'note', undefined, catalog)
  g = connectData(g, 'words', 'value', 'code', 'text', catalog)
  return connectData(g, 'code', 'result', 'note', 'value', catalog)
}

/** The editor page over a saved automation, as the panel mounts it, with the fixture backend (and a callWS in front of it). */
function mount(graph = sample(), options: { callWS?: (message: any) => unknown } = {}) {
  const host = root(), fixture = visualFixture({ catalog })
  fixture.documents.set('saved', { version: 1, id: 'saved', revision: 1, graph, source: '// fixture', sourceMap: [], automationId: 'visual:saved', currentRevisionActive: true, catalogGeneration: 'saved-fixture' })
  const callWS = vi.fn((message: any) => options.callWS?.(message) ?? fixture.callWS(message))
  const dirty: boolean[] = []
  dispose = render(() => <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <HassCtx.Provider value={{ ...fixture, callWS, user: { is_admin: true } } as any}>
      <VisualEditor name="Code" documentId="saved" onDirtyChange={value => dirty.push(value)} />
    </HassCtx.Provider></QueryClientProvider>, host)
  return { host, fixture, callWS, dirty }
}
const card = (host: Element, id: string) => host.querySelector<HTMLElement>(`.solid-flow__node[data-id="${id}"]`)!
const dialog = () => document.querySelector<HTMLDialogElement>('dialog.hs-code-dialog')
const state = () => dialog()!.querySelector('.hs-code-state')!.textContent
const apply = () => button(dialog()!, 'Apply')!
const previews = (callWS: ReturnType<typeof vi.fn>) => callWS.mock.calls.map(([m]) => m).filter(m => m.request?.operation === 'custom_preview')
const graphOf = (host: Element) => { button(host, 'Graph JSON')!.click(); return JSON.parse(host.querySelector('pre[aria-label="Graph JSON"]')!.textContent!) as VisualGraph }
/** Opens the saved automation and the code editor of `code` from its card. */
async function open(options: Parameters<typeof mount>[1] = {}) {
  const page = mount(sample(), options)
  await vi.waitFor(() => expect(card(page.host, 'code')).toBeTruthy())
  button(card(page.host, 'code'), 'Edit code of Rename this node')!.click()
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  await vi.waitFor(() => expect(state()).toBe('No problems'))
  return { ...page, editor: editors[0] }
}

it('opens the code editor from the panel button, the card control, a double-click and E on the card; nothing opens by itself', async () => {
  const { host } = mount()
  await vi.waitFor(() => expect(card(host, 'code')).toBeTruthy())
  expect(dialog()).toBeNull()
  const close = async () => { button(dialog()!, 'Cancel')!.click(); await vi.waitFor(() => expect(dialog()).toBeNull()) }
  // The card control does not start a drag, and selects the step.
  const control = button(card(host, 'code'), 'Edit code of Rename this node')!
  expect(control.classList.contains('nodrag')).toBe(true)
  control.click()
  await vi.waitFor(() => expect(dialog()?.open).toBe(true))
  expect(dialog()!.querySelector('h2')!.textContent).toBe('Rename this node')
  await close()
  // The panel's button (the step is selected now).
  button(host.querySelector('aside[aria-label="Node inspector"]')!, 'Edit code')!.click()
  await vi.waitFor(() => expect(dialog()).not.toBeNull())
  await close()
  card(host, 'code').querySelector('.hs-card__title')!.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))
  await vi.waitFor(() => expect(dialog()).not.toBeNull())
  await close()
  card(host, 'code').dispatchEvent(new KeyboardEvent('keydown', { key: 'e', bubbles: true, cancelable: true }))
  await vi.waitFor(() => expect(dialog()).not.toBeNull())
  await close()
  // Other cards have no code control, and a double-click on them opens nothing.
  expect(button(card(host, 'note'), 'Edit code of Log')).toBeUndefined()
  card(host, 'note').dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))
  await Promise.resolve()
  expect(dialog()).toBeNull()
})

it('a new Custom C# node shows the template\'s pins and title at once', () => {
  const graph = addNode(createGraph('New'), 'custom', 'fresh', catalog)
  const node = graph.nodes.find(n => n.id === 'fresh')!
  expect(node.label).toBeUndefined()
  const pins = customSignature(node, catalog)!
  expect(pins.title).toBe('Rename this node')
  expect(Object.keys(pins.inputs)).toEqual(['text'])
  expect(Object.keys(pins.outputs)).toEqual(['result'])
})

it('checks once, about 400 ms after the last keystroke, with the node\'s id; ignores a stale reply; shows markers, problems and the pins', async () => {
  const replies: { message: any; resolve: (value: unknown) => void }[] = []
  let hold = false
  const { editor, callWS, fixture } = await open({
    callWS: message => (hold && message.request?.operation === 'custom_preview' ? new Promise(resolve => replies.push({ message, resolve })) : undefined),
  })
  expect(previews(callWS)).toHaveLength(1)
  expect(previews(callWS)[0].request).toMatchObject({ nodeId: 'code', source: template })
  expect(dialog()!.querySelector('[aria-label="Pins"]')!.textContent).toContain('Text')
  vi.useFakeTimers()
  hold = true
  const two = template.replace('string text', 'string text, int count')
  editor.change(template.replace('string text', 'string text, int'))
  await vi.advanceTimersByTimeAsync(200)
  editor.change(two)
  await vi.advanceTimersByTimeAsync(399)
  expect(previews(callWS)).toHaveLength(1)
  expect(state()).toBe('Checking…')
  await vi.advanceTimersByTimeAsync(1)
  expect(previews(callWS)).toHaveLength(2)
  expect(previews(callWS)[1].request).toMatchObject({ nodeId: 'code', source: two })
  // The reply comes after another edit: it is stale and changes nothing.
  const broken = two.replace('return text;', 'return text !!;')
  editor.change(broken)
  replies[0].resolve(await fixture.callWS(replies[0].message))
  await vi.advanceTimersByTimeAsync(0)
  expect(state()).toBe('Checking…')
  expect(dialog()!.querySelector('[aria-label="Pins"]')!.textContent).not.toContain('Count')
  await vi.advanceTimersByTimeAsync(400)
  replies[1].resolve(await fixture.callWS(replies[1].message))
  await vi.advanceTimersByTimeAsync(0)
  expect(state()).toBe('1 problem')
  expect(apply().disabled).toBe(true)
  const marker = markers.get('hass-sharp-custom:///code.cs')!
  expect(marker).toEqual([expect.objectContaining({ startLineNumber: 9, message: "Unexpected '!!' (fixture)", severity: 8 })])
  const problem = dialog()!.querySelector<HTMLButtonElement>('[aria-label="Problems"] button.hs-code-problem')!
  expect(problem.textContent).toContain('Line 9, column 21')
  problem.click()
  expect(editor.selection).toMatchObject({ startLineNumber: 9, startColumn: 21 })
  // The pins of the last code that compiled stay on view, marked as such.
  expect(dialog()!.querySelector('[aria-label="Pins"]')!.textContent).toContain('From the last code that compiled')
  // Fixed: the pins follow the code.
  hold = false
  editor.change(two)
  await vi.advanceTimersByTimeAsync(400)
  await vi.advanceTimersByTimeAsync(0)
  expect(state()).toBe('No problems')
  const pins = dialog()!.querySelector('[aria-label="Pins"]')!
  expect([...pins.querySelectorAll('.hs-code-pin__name')].map(el => el.textContent)).toEqual(['Text', 'Count', 'Result'])
  expect(pins.textContent).toContain('Step: runs in the step order.')
  expect(apply().disabled).toBe(false)
})

it('applies the code and its pins as one undo step, keeping the wires and values that still fit and naming the rest; Undo restores all', async () => {
  const { host, editor } = await open()
  const before = graphOf(host)
  // Checking: Apply waits.
  // `text` goes and `result` becomes two named outputs: both of their wires go; the step order wires stay.
  const next = template.replace('string RenameThisNode(string text)', '(double Total, int Count) RenameThisNode(int count)').replace('return text;', 'return (count, count);')
  editor.change(next)
  expect(apply().disabled).toBe(true)
  await vi.waitFor(() => expect(state()).toBe('No problems'))
  vi.useFakeTimers({ shouldAdvanceTime: true })
  apply().click()
  await vi.waitFor(() => expect(dialog()).toBeNull())
  const notice = () => host.querySelector('.hs-canvas-notice')?.textContent
  expect(notice()).toBe('Removed 2 connections that no longer fit: Text, Result.')
  // It stays until dismissed or the next change: the dialog closing has just moved focus away.
  await vi.advanceTimersByTimeAsync(30_000)
  expect(notice()).toBe('Removed 2 connections that no longer fit: Text, Result.')
  const after = graphOf(host)
  const node = after.nodes.find(n => n.id === 'code')!
  expect(node.config.source).toBe(next)
  expect(Object.keys((node.config.signature as CustomSignature).inputs)).toEqual(['count'])
  expect(Object.keys((node.config.signature as CustomSignature).outputs)).toEqual(['total', 'count'])
  expect(after.edges.filter(e => e.kind === 'data' && (e.source === 'code' || e.target === 'code'))).toEqual([])
  // Its step order wires stay; its label is left alone.
  expect(after.edges.filter(e => e.kind !== 'data' && (e.source === 'code' || e.target === 'code'))).toHaveLength(2)
  expect(node.label).toBeUndefined()
  button(host, 'Undo')!.click()
  expect(graphOf(host)).toEqual(before)
  expect(notice()).toBeUndefined()
})

it('keeps values and wires of pins that still exist and fit, and clears the rest (one graph, with the notice)', () => {
  let graph = sample()
  graph = updateNode(graph, 'code', { label: 'Mine', config: { ...graph.nodes.find(n => n.id === 'code')!.config, arguments: { text: { kind: 'unset' }, level: lit('number', 3), gone: lit('string', 'x') } } })
  // `level` (a number) no longer fits a text pin; `text` and `result` keep their wires.
  const kept = applyCustomCode(graph, 'code', 'new code', signature({ text: S, level: S }, { result: S }), catalog)
  const node = kept.graph.nodes.find(n => n.id === 'code')!
  expect(node.label).toBe('Mine')
  expect(node.config.arguments).toEqual({ text: { kind: 'unset' } })
  expect(kept.graph.edges.filter(e => e.kind === 'data')).toHaveLength(2)
  expect(kept.notice).toBe('Cleared 2 values that no longer fit: level, gone.')
  // A Constant's text does not go into a Double pin, and the old `result` is gone.
  const changed = applyCustomCode(graph, 'code', 'new code', signature({ text: D }, { total: D }), catalog)
  expect(changed.graph.edges.filter(e => e.kind === 'data').map(e => e.id)).toEqual([])
  expect(changed.notice).toBe('Removed 2 connections that no longer fit: Text, Result. Cleared 2 values that no longer fit: level, gone.')
  expect(dataOutputs(changed.graph.nodes.find(n => n.id === 'code')!, catalog)).toHaveProperty('total')
})

it('takes a step that becomes a value node out of the step order, joining the steps before and after it', () => {
  const graph = sample()
  const pure = applyCustomCode(graph, 'code', 'code', signature({ text: S }, { result: S }, true), catalog)
  const order = (g: VisualGraph) => g.edges.filter(e => e.kind !== 'data').map(e => `${e.source}->${e.target}`)
  expect(order(pure.graph).filter(e => e.includes('code'))).toEqual([])
  // Call service → code → Log becomes Call service → Log: the run still reaches Log.
  expect(order(pure.graph)).toContain('service-1->note')
  expect(pure.graph.edges.filter(e => e.kind === 'data')).toHaveLength(2)
  expect(pure.notice).toBe('Rename this node is now a value node; Log now follows Call service.')
  const step = applyCustomCode(pure.graph, 'code', 'code', signature({ text: S }, { result: S }), catalog)
  expect(step.notice).toBe('It is now a step: connect it into the step order.')
  // One undo step restores code, pins and wires together.
  const history = createGraphHistory(graph)
  history.commit(pure.graph)
  history.undo()
  expect(history.graph()).toEqual(graph)
})

it('tells same-named steps apart in what a new value node\'s notice says', () => {
  // Log → code → Log: the two Logs read alike, so each is named with its id.
  let graph = addNode(sample(), 'log', 'first', catalog)
  graph = { ...graph, edges: graph.edges.filter(e => !(e.kind !== 'data' && e.target === 'code')) }
  graph = connectNodes(connectNodes(graph, 'service-1', 'first', undefined, catalog), 'first', 'code', undefined, catalog)
  const pure = applyCustomCode(graph, 'code', 'code', signature({ text: S }, { result: S }, true), catalog)
  expect(pure.notice).toBe('Rename this node is now a value node; Log · note now follows Log · first.')
  // Two same-named steps no longer reached are both named.
  let split = addNode(sample(), 'log', 'other', catalog)
  split = connectNodes(split, 'code', 'other', 'error', catalog)
  expect(applyCustomCode(split, 'code', 'code', signature({ text: S }, { result: S }, true), catalog).notice)
    .toBe('It is now a value node: removed its 3 step order wires; Log · note and Log · other are no longer reached.')
})

it('names the steps a new value node no longer leads to when it cannot join them', () => {
  // Its error path goes on to a second Log: two wires out, so nothing is joined.
  let graph = addNode(sample(), 'log', 'oops', catalog)
  graph = updateNode(graph, 'oops', { label: 'Report' })
  graph = connectNodes(graph, 'code', 'oops', 'error', catalog)
  const pure = applyCustomCode(graph, 'code', 'code', signature({ text: S }, { result: S }, true), catalog)
  expect(pure.graph.edges.filter(e => e.kind !== 'data' && (e.source === 'code' || e.target === 'code'))).toEqual([])
  expect(pure.graph.edges.some(e => e.kind !== 'data' && e.source === 'service-1')).toBe(false)
  expect(pure.notice).toBe('It is now a value node: removed its 3 step order wires; Log and Report are no longer reached.')
  // Nothing before it: what followed it is no longer reached.
  const first = { ...sample(), edges: sample().edges.filter(e => !(e.kind !== 'data' && e.target === 'code')) }
  expect(applyCustomCode(first, 'code', 'code', signature({ text: S }, { result: S }, true), catalog).notice).toBe('It is now a value node: removed its 1 step order wire; Log is no longer reached.')
})

/** C#'s long, which only a node's own code brings (as its signature's types do). */
const withLong = (pins: CustomSignature): CustomSignature => ({ ...pins, types: { [L]: { ...structuredClone(catalog.types[I]), label: 'long', description: 'long', min: Number.MIN_SAFE_INTEGER, max: Number.MAX_SAFE_INTEGER, assignableTo: [] } as never } })
/** `code` and a second Custom C# node `sink`, whose code takes a number `n` (Int64). */
function numbers(output: string): VisualGraph {
  let graph = addNode(sample(), 'custom', 'sink', catalog)
  const config = (id: string) => graph.nodes.find(n => n.id === id)!.config
  graph = updateNode(graph, 'code', { config: { ...config('code'), signature: signature({ text: S }, { result: output }) } })
  graph = updateNode(graph, 'sink', { config: { ...config('sink'), signature: withLong(signature({ n: L }, {}, false, 'Sink')) } })
  // Log's value takes anything: only the code's own number wire is in question.
  graph = { ...graph, edges: [...graph.edges, { id: 'code.result->sink.n', kind: 'data', source: 'code', sourcePort: 'result', target: 'sink', targetPort: 'n' }] }
  return graph
}

it('decides that a wire still fits as the validators do: an int into a long stays, text into a long goes', () => {
  // A wire into the node: a long pin takes an int output.
  let graph = numbers(I)
  graph = updateNode(graph, 'sink', { config: { ...graph.nodes.find(n => n.id === 'sink')!.config, signature: withLong(signature({ n: L }, { result: I }, false, 'Sink')) } })
  graph = { ...graph, edges: [...graph.edges, { id: 'sink.result->code.count', kind: 'data', source: 'sink', sourcePort: 'result', target: 'code', targetPort: 'count' }] }
  const into = applyCustomCode(graph, 'code', 'code', withLong(signature({ text: S, count: L }, { result: I })), catalog)
  expect(into.graph.edges.map(e => e.id)).toContain('sink.result->code.count')
  // An output whose type changed: int → long into a long pin still fits.
  const wider = applyCustomCode(numbers(I), 'code', 'code', withLong(signature({ text: S }, { result: L })), catalog)
  expect(wider.graph.edges.map(e => e.id)).toContain('code.result->sink.n')
  expect(wider.notice).toBeUndefined()
  // Text into a long pin does not.
  const text = applyCustomCode(numbers(I), 'code', 'code', signature({ text: S }, { result: S }), catalog)
  expect(text.graph.edges.map(e => e.id)).not.toContain('code.result->sink.n')
  expect(text.notice).toBe('Removed 1 connection that no longer fits: Result.')
})

it('keeps a typed-in number only where it fits the new pin: 3 into an int, not 3.5', () => {
  let graph = sample()
  graph = updateNode(graph, 'code', { config: { ...graph.nodes.find(n => n.id === 'code')!.config, arguments: { whole: lit('number', 3), half: lit('number', 3.5) } } })
  const result = applyCustomCode(graph, 'code', 'code', signature({ text: S, whole: I, half: I }, { result: S }), catalog)
  expect(result.graph.nodes.find(n => n.id === 'code')!.config.arguments).toEqual({ whole: lit('number', 3) })
  expect(result.notice).toBe('Cleared a value that no longer fits: half.')
})

it('asks before discarding changes, counts open unapplied changes as unsaved, and closes at once without changes', async () => {
  const { host, editor, dirty } = await open()
  // Escape without changes closes.
  dialog()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  await vi.waitFor(() => expect(dialog()).toBeNull())
  button(card(host, 'code'), 'Edit code of Rename this node')!.click()
  await vi.waitFor(() => expect(editors).toHaveLength(2))
  const second = editors[1]
  expect(editor.disposed).toBe(true)
  second.change(`${template}\n// more`)
  expect(dirty.at(-1)).toBe(true)
  const closing = new Event('beforeunload', { cancelable: true })
  window.dispatchEvent(closing)
  expect(closing.defaultPrevented).toBe(true)
  // Cancel asks; Keep editing keeps the text.
  button(dialog()!, 'Cancel')!.click()
  const ask = () => [...dialog()!.querySelectorAll('dialog')].find(d => d.open && d.textContent?.includes('Discard your changes to the code?'))
  await vi.waitFor(() => expect(ask()).toBeTruthy())
  button(ask()!, 'Keep editing')!.click()
  await vi.waitFor(() => expect(ask()).toBeUndefined())
  expect(second.model.getValue()).toBe(`${template}\n// more`)
  // Escape asks too; Discard closes and leaves the node as it was.
  dialog()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  await vi.waitFor(() => expect(ask()).toBeTruthy())
  button(ask()!, 'Discard')!.click()
  await vi.waitFor(() => expect(dialog()).toBeNull())
  expect(dirty.at(-1)).toBe(false)
  expect(graphOf(host).nodes.find(n => n.id === 'code')!.config.source).toBe(template)
  // An Escape that Monaco used (its suggestion list) is not Cancel.
  button(card(host, 'code'), 'Edit code of Rename this node')!.click()
  await vi.waitFor(() => expect(dialog()).not.toBeNull())
  const used = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })
  used.preventDefault()
  dialog()!.dispatchEvent(used)
  await Promise.resolve()
  expect(dialog()).not.toBeNull()
})

it('keeps the text through a failed check, says why with Try again, and keeps Apply disabled', async () => {
  let offline = false
  const { editor, callWS } = await open({ callWS: message => (offline && message.request?.operation === 'custom_preview' ? Promise.reject({ code: 3, message: 'Connection lost' }) : undefined) })
  offline = true
  editor.change(`${template}\n// edited`)
  await vi.waitFor(() => expect(dialog()!.querySelector('[role="alert"]')?.textContent).toContain('Could not check the code: Connection lost'))
  expect(state()).toBe('Not checked')
  expect(apply().disabled).toBe(true)
  expect(editor.model.getValue()).toBe(`${template}\n// edited`)
  offline = false
  const count = previews(callWS).length
  button(dialog()!, 'Try again')!.click()
  await vi.waitFor(() => expect(state()).toBe('No problems'))
  expect(previews(callWS)).toHaveLength(count + 1)
  expect(dialog()!.querySelector('[role="alert"]')).toBeNull()
  expect(apply().disabled).toBe(false)
})

it('sends completion and hover to the library language service with this node\'s id and its unsaved code', async () => {
  const { editor, callWS } = await open()
  editor.change(template.replace('return text;', 'return text.Le'))
  const noCancellation = { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) }
  const position = { lineNumber: 9, column: 23 }
  await completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
  await hovers[0].provideHover(editor.model, position, noCancellation)
  const language = callWS.mock.calls.map(([m]) => m.request).filter(r => r?.operation === 'completion' || r?.operation === 'hover')
  expect(language).toHaveLength(2)
  for (const request of language) expect(request).toMatchObject({ custom: true, nodeId: 'code', source: editor.model.getValue(), position: editor.model.getOffsetAt(position) })
  expect(typeof language[0].workspaceVersion).toBe('string')
})

it('sends signature help, definition and format with this node\'s id; goes to definitions only in its own code', async () => {
  const { editor, callWS } = await open({
    // A definition in the node's code and one in a library file, which the editor has no model to show.
    callWS: message => message.request?.operation === 'definition' ? {
      version: 1, ok: true, result: { id: 'code', workspaceVersion: message.request.workspaceVersion, generation: '', saved: false, data: [
        { id: '$custom', range: { start: 0, length: 6, startLine: 1, startColumn: 1, endLine: 1, endColumn: 7 } },
        { id: 'helpers', range: { start: 0, length: 6, startLine: 4, startColumn: 1, endLine: 4, endColumn: 7 } },
      ] },
    } : undefined,
  })
  const code = template.replace('return text;', 'return Make(text, ').replace('{\n    /// <summary>', '{\n    static string Make(string label, int count) => label;\n    /// <summary>')
  editor.change(code)
  const noCancellation = { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) }
  const position = editor.model.getPositionAt(code.indexOf('Make(text, ') + 'Make(text, '.length)
  const help = await signatures[0].provideSignatureHelp(editor.model, position, noCancellation, {})
  expect(help.value.signatures[0].label).toContain('Make')
  const found = await definitions[0].provideDefinition(editor.model, position, noCancellation)
  expect(found).toEqual([{ uri: editor.model.uri, range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 7 } }])
  const edits = await formats[0].provideDocumentFormattingEdits(editor.model, {}, noCancellation)
  expect(edits).toHaveLength(1)
  const requests = callWS.mock.calls.map(([m]) => m.request).filter(r => ['signature', 'definition', 'format'].includes(r?.operation))
  expect(requests.map(r => r.operation)).toEqual(['signature', 'definition', 'format'])
  for (const request of requests) expect(request).toMatchObject({ custom: true, nodeId: 'code', source: code })
  expect(requests[0].position).toBe(editor.model.getOffsetAt(position))
})

it('resets to the example from its menu as an edit of the code', async () => {
  const { editor } = await open()
  editor.change('// nothing')
  button(dialog()!, 'Code actions')!.click()
  ;[...dialog()!.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].find(b => b.textContent === 'Reset to example')!.click()
  expect(editor.model.getValue()).toBe(template)
})

it('shows the code read only from a run\'s canvas ("View code"), never checks it, and has only Close', async () => {
  const host = root()
  const graph = sample()
  const fixture = visualFixture({ catalog })
  const callWS = vi.fn(fixture.callWS)
  let opened: string | undefined
  dispose = render(() => <QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={{ ...fixture, callWS } as any}>
    <FlowAdapter graph={graph} catalog={catalog} readOnly onSelect={() => {}} onEditCode={id => { opened = id }} />
    <CustomCodeEditor node={graph.nodes.find(n => n.id === 'code')!} catalog={catalog} readOnly onClose={() => {}} />
  </HassCtx.Provider></QueryClientProvider>, host)
  await vi.waitFor(() => expect(card(host, 'code')).toBeTruthy())
  button(card(host, 'code'), 'View code of Rename this node')!.click()
  expect(opened).toBe('code')
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  expect(editors[0].options.readOnly).toBe(true)
  expect(state()).toBe('Read only')
  expect(button(dialog()!, 'Apply')).toBeUndefined()
  expect(button(dialog()!, 'Close')).toBeTruthy()
  await new Promise(resolve => setTimeout(resolve, 450))
  expect(previews(callWS)).toEqual([])
})

it('opens the help with the facts of writing a node', async () => {
  await open()
  button(dialog()!, 'How to write a node')!.click()
  const help = [...dialog()!.querySelectorAll('dialog')].find(d => d.open)!
  for (const fact of ['[Port("Label")]', 'Pure = true', 'CancellationToken', 'NodeContext', 'State(entityId)', 'CallService(service, target, data, returnResponse)', 'Log(message, level)', '<summary>', '(double Average, double Max)',
    // The typed services (Task 25b), as docs/node-libraries.md describes them.
    'Read a state with context.State("sensor.x").', 'await context.Services.Light.TurnOn(light, brightnessPct: 30)', 'the methods follow this Home Assistant\'s own services',
    '["light.a", "light.b"]', 'ServiceTarget.Areas("kitchen")', 'For a service id known only at run time, use context.CallService("domain.service", target, data).',
    'If a service goes away, code that calls it stops compiling at its next compile; running automations keep running.'])
    expect(help.textContent, fact).toContain(fact)
})

/** Static imports from `file` within src, recursively (type-only imports are erased; `import()` is not static). */
function staticGraph(entry: string) {
  const seen = new Set<string>(), bare = new Set<string>()
  const visit = (file: string) => {
    if (seen.has(file)) return
    seen.add(file)
    const text = readFileSync(file, 'utf8')
    for (const match of text.matchAll(/^\s*(?:import|export)\s+(?!type\b)(?:[^'"]*?\sfrom\s+)?['"]([^'"]+)['"]/gm)) {
      const spec = match[1]
      if (!spec.startsWith('.')) { bare.add(spec); continue }
      const base = resolve(dirname(file), spec)
      const target = ['', '.ts', '.tsx', '/index.ts', '/index.tsx'].map(ext => base + ext).find(path => existsSync(path) && !path.endsWith('/') && /\.(tsx?)$/.test(path))
      if (target) visit(target)
    }
  }
  visit(resolve(entry))
  return { files: [...seen], bare: [...bare] }
}

it('keeps Monaco out of the panel\'s first chunk: the code editor loads it with a dynamic import', () => {
  const graph = staticGraph('src/hass-sharp.ts')
  expect(graph.files.some(file => file.endsWith('visual/VisualEditor.tsx'))).toBe(true)
  expect(graph.files.some(file => file.endsWith('visual/CustomCodeEditor.tsx'))).toBe(true)
  expect(graph.bare.filter(spec => spec.startsWith('monaco'))).toEqual([])
  expect(graph.files.filter(file => file.includes('components/Editor/'))).toEqual([])
  expect(readFileSync('src/visual/CustomCodeEditor.tsx', 'utf8')).toContain("import('../components/Editor/CustomCode')")
  expect(readFileSync('src/App.tsx', 'utf8')).toContain("import('./views/NodeLibraryView')")
})

it('Ctrl/⌘+S anywhere in the dialog applies when Apply is enabled, and never reaches the browser\'s Save page', async () => {
  const { host, editor } = await open()
  const save = (target: Element, meta = false) => {
    const event = new KeyboardEvent('keydown', { key: 's', ctrlKey: !meta, metaKey: meta, bubbles: true, cancelable: true })
    target.dispatchEvent(event)
    return event
  }
  editor.change(`${template}\n// saved`)
  // While it is checked, there is nothing to apply; the browser still never sees the key.
  expect(save(button(dialog()!, 'How to write a node')!).defaultPrevented).toBe(true)
  expect(dialog()).not.toBeNull()
  await vi.waitFor(() => expect(state()).toBe('No problems'))
  // From inside the help, which stops other keys at its own dialog.
  button(dialog()!, 'How to write a node')!.click()
  const help = [...dialog()!.querySelectorAll('dialog')].find(d => d.open)!
  expect(save(button(help, 'Close dialog')!, true).defaultPrevented).toBe(true)
  await vi.waitFor(() => expect(dialog()).toBeNull())
  expect(graphOf(host).nodes.find(n => n.id === 'code')!.config.source).toBe(`${template}\n// saved`)
})

it('names the dialog as the node\'s code, and announces a change of result, not each check', async () => {
  const { editor } = await open()
  expect(dialog()!.getAttribute('aria-label')).toBe('Code of Rename this node')
  const live = [...dialog()!.querySelectorAll('[role="status"], [aria-live]')]
  expect(live).toHaveLength(1)
  expect(live[0].textContent).toBe('No problems')
  editor.change(template.replace('return text;', 'return text !!;'))
  expect(state()).toBe('Checking…')
  expect(live[0].textContent).toBe('No problems')
  await vi.waitFor(() => expect(state()).toBe('1 problem'))
  expect(live[0].textContent).toBe('1 problem')
  editor.change(template.replace('return text;', 'return text !! ;'))
  await vi.waitFor(() => expect(state()).toBe('1 problem'))
  expect(live[0].textContent).toBe('1 problem')
})

it('closes the editor when its step goes away under it, and offers code with unapplied changes to copy', async () => {
  const writeText = vi.fn(async (_text: string) => {})
  Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
  const { host, fixture, editor, dirty } = await open()
  const saved = fixture.documents.get('saved')!
  const without = { ...saved.graph, nodes: saved.graph.nodes.filter(n => n.id !== 'code'), edges: saved.graph.edges.filter(e => e.source !== 'code' && e.target !== 'code') }
  const reload = async (graph: VisualGraph, revision: number) => {
    fixture.documents.set('saved', { ...saved, revision, graph })
    button(host, 'More actions')!.click()
    ;[...host.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].find(b => b.textContent === 'Reload saved graph')!.click()
    await vi.waitFor(() => expect(graphOf(host).nodes.some(n => n.id === 'code')).toBe(graph.nodes.some(n => n.id === 'code')))
  }
  const left = () => [...document.querySelectorAll('dialog')].find(d => d.open && d.textContent?.includes('This step no longer exists'))
  // No changes: it just closes.
  await reload(without, 2)
  await vi.waitFor(() => expect(dialog()).toBeNull())
  expect(left()).toBeUndefined()
  await reload(saved.graph, 3)
  // The step is back, but its editor does not open by itself.
  await new Promise(resolve => setTimeout(resolve, 0))
  expect(dialog()).toBeNull()
  button(card(host, 'code'), 'Edit code of Rename this node')!.click()
  await vi.waitFor(() => expect(editors).toHaveLength(2))
  expect(editor.disposed).toBe(true)
  editors[1].change(`${template}\n// mine`)
  expect(dirty.at(-1)).toBe(true)
  await reload(without, 4)
  await vi.waitFor(() => expect(left()).toBeTruthy())
  expect(dialog()).toBeNull()
  expect(dirty.at(-1)).toBe(false)
  expect(left()!.querySelector('textarea')!.value).toBe(`${template}\n// mine`)
  button(left()!, 'Copy the code')!.click()
  await vi.waitFor(() => expect(button(left()!, 'Copied')).toBeTruthy())
  expect(writeText).toHaveBeenCalledWith(`${template}\n// mine`)
  button(left()!, 'Close')!.click()
  await vi.waitFor(() => expect(left()).toBeUndefined())
  // The page can open another step's code: nothing is left set.
  await reload(saved.graph, 5)
  await new Promise(resolve => setTimeout(resolve, 0))
  expect(dialog()).toBeNull()
  button(card(host, 'code'), 'Edit code of Rename this node')!.click()
  await vi.waitFor(() => expect(dialog()).not.toBeNull())
})

it('ignores a check that answers after Cancel, and starts none once the dialog has closed', async () => {
  const replies: { message: any; resolve: (value: unknown) => void }[] = []
  let hold = false
  const { host, editor, callWS, fixture } = await open({
    callWS: message => (hold && message.request?.operation === 'custom_preview' ? new Promise(resolve => replies.push({ message, resolve })) : undefined),
  })
  vi.useFakeTimers()
  hold = true
  // A check under way when the dialog closes.
  editor.change(template.replace('return text;', 'return text !!;'))
  await vi.advanceTimersByTimeAsync(400)
  expect(replies).toHaveLength(1)
  dialog()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  button([...dialog()!.querySelectorAll('dialog')].find(d => d.open)!, 'Discard')!.click()
  await vi.advanceTimersByTimeAsync(0)
  expect(dialog()).toBeNull()
  const before = markers.get('hass-sharp-custom:///code.cs')
  replies[0].resolve(await fixture.callWS(replies[0].message))
  await vi.advanceTimersByTimeAsync(0)
  expect(markers.get('hass-sharp-custom:///code.cs')).toBe(before)
  // An edit just before closing: its timer does not fire a check afterwards.
  button(card(host, 'code'), 'Edit code of Rename this node')!.click()
  await vi.advanceTimersByTimeAsync(0)
  const second = editors.at(-1)!
  await vi.advanceTimersByTimeAsync(400)
  const count = callWS.mock.calls.length
  second.change(`${template}\n// pending`)
  button(dialog()!, 'Cancel')!.click()
  button([...dialog()!.querySelectorAll('dialog')].find(d => d.open)!, 'Discard')!.click()
  await vi.advanceTimersByTimeAsync(1000)
  expect(dialog()).toBeNull()
  expect(callWS.mock.calls.slice(count).filter(([m]) => m.request?.operation === 'custom_preview')).toEqual([])
})

it('Escape in the help, the confirmation or the menu closes only that layer', async () => {
  const { editor } = await open()
  const escape = (target: Element) => target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  const inner = () => [...dialog()!.querySelectorAll('dialog')].find(d => d.open)
  button(dialog()!, 'How to write a node')!.click()
  escape(button(inner()!, 'Close dialog')!)
  await vi.waitFor(() => expect(inner()).toBeUndefined())
  expect(dialog()).not.toBeNull()
  editor.change(`${template}\n// mine`)
  button(dialog()!, 'Cancel')!.click()
  await vi.waitFor(() => expect(inner()?.textContent).toContain('Discard your changes to the code?'))
  escape(button(inner()!, 'Keep editing')!)
  await vi.waitFor(() => expect(inner()).toBeUndefined())
  expect(dialog()).not.toBeNull()
  button(dialog()!, 'Code actions')!.click()
  const item = dialog()!.querySelector<HTMLElement>('[role="menuitem"]')!
  escape(item)
  await vi.waitFor(() => expect(dialog()!.querySelector('[role="menuitem"]')).toBeNull())
  expect(dialog()).not.toBeNull()
  expect(inner()).toBeUndefined()
})

it('E opens the code only on its own: not with Ctrl, ⌘ or Alt, nor while typing on the card', async () => {
  // Without the wire into Text, the card has a field to type it in.
  const graph = sample()
  const { host } = mount({ ...graph, edges: graph.edges.filter(e => !(e.kind === 'data' && e.target === 'code')) })
  await vi.waitFor(() => expect(card(host, 'code')).toBeTruthy())
  const press = (target: Element, init: KeyboardEventInit = {}) => target.dispatchEvent(new KeyboardEvent('keydown', { key: 'e', bubbles: true, cancelable: true, ...init }))
  for (const init of [{ ctrlKey: true }, { metaKey: true }, { altKey: true }]) press(card(host, 'code'), init)
  const field = card(host, 'code').querySelector<HTMLElement>('input, textarea')!
  expect(field).toBeTruthy()
  press(field)
  await new Promise(resolve => setTimeout(resolve, 0))
  expect(dialog()).toBeNull()
  press(card(host, 'code'))
  await vi.waitFor(() => expect(dialog()).not.toBeNull())
})

it('opens a run\'s code read only, as the run had it, from the trace page', async () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
  const ran = sample()
  const snapshotCode = `${template}\n// as it ran`
  const graph = updateNode(ran, 'code', { config: { ...ran.nodes.find(n => n.id === 'code')!.config, source: snapshotCode } })
  const history = createMemoryHistory()
  history.set({ value: '/visual/saved/runs/run-1' })
  const callWS = vi.fn(async (message: any) => {
    if (message.type === 'hass_sharp/run_trace') return {
      version: 1, live: false, run: run({ runId: 'run-1', visualId: 'saved', visualRevision: 1 }),
      snapshot: { revision: 'rev', visualId: 'saved', visualRevision: 1, graph, catalog },
      trace: { format: 1, runId: 'run-1', rootNodeId: 'trigger-1', startedUtc: '2026-11-12T01:00:01Z', complete: true, truncated: false, dropped: 0, trigger: {}, events: [], summary: {} },
    }
    if (message.type === 'hass_sharp/visual' && message.request.operation === 'read') return { version: 1, ok: true, result: { id: 'saved', revision: 2, graph: sample() } }
    return visualFixture({ catalog }).callWS(message)
  })
  const host = root()
  dispose = render(() => <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <HassCtx.Provider value={{ callWS, user: { is_admin: true } } as any}>
      <MemoryRouter history={history}><Route path="/visual/:id/runs/:runId" component={RunTraceView} /></MemoryRouter>
    </HassCtx.Provider></QueryClientProvider>, host)
  await vi.waitFor(() => expect(card(host, 'code')).toBeTruthy())
  button(card(host, 'code'), 'View code of Rename this node')!.click()
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  expect(editors[0].options.readOnly).toBe(true)
  expect(editors[0].model.getValue()).toBe(snapshotCode)
  expect(state()).toBe('Read only')
  expect(button(dialog()!, 'Apply')).toBeUndefined()
  button(dialog()!, 'Close')!.click()
  await vi.waitFor(() => expect(dialog()).toBeNull())
})

it('keeps unapplied code in the draft of a panel torn down, and opens it in the code editor when the draft is restored', async () => {
  const { editor } = await open()
  editor.change(`${template}\n// typed, not applied`)
  // The panel goes away without asking (the sidebar): the unapplied code is kept with the draft.
  dispose?.(); dispose = undefined
  document.body.innerHTML = ''
  const kept = JSON.parse(localStorage.getItem('hass-sharp:draft:saved')!)
  expect(kept.code).toEqual({ nodeId: 'code', text: `${template}\n// typed, not applied` })
  resetHost()
  const { host, dirty } = mount()
  const restore = () => [...document.querySelectorAll<HTMLDialogElement>('dialog')].find(d => d.open && d.textContent?.includes('Restore unsaved changes'))
  await vi.waitFor(() => expect(restore()).toBeTruthy())
  button(restore()!, 'Restore')!.click()
  // The code editor opens on its node with the unapplied code, still to be applied.
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  expect(editors[0].model.getValue()).toBe(`${template}\n// typed, not applied`)
  await vi.waitFor(() => expect(dirty.at(-1)).toBe(true))
  expect(graphOf(host).nodes.find(n => n.id === 'code')!.config.source).toBe(template)
})

it('words the help\'s note on services for an entity definition when the code is one of its steps', async () => {
  const fixture = visualFixture({ catalog })
  const node = sample().nodes.find(n => n.id === 'code')!
  dispose = render(() => <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <HassCtx.Provider value={{ ...fixture, user: { is_admin: true } } as any}>
      <CustomCodeEditor node={node} catalog={catalog} document="entity" onApply={() => {}} onClose={() => {}} />
    </HassCtx.Provider></QueryClientProvider>, root())
  await vi.waitFor(() => expect(dialog()?.open).toBe(true))
  button(dialog()!, 'How to write a node')!.click()
  const help = [...dialog()!.querySelectorAll('dialog')].find(d => d.open)!
  expect(help.textContent).toContain('If a service goes away, code that calls it stops compiling at its next compile; what is already running keeps running.')
  expect(help.textContent).not.toContain('automation')
})

// ---- C# entity and C# device nodes: their code is one class, and its [Setting] properties are the node's settings.

const setting = (key: string, type: ClassSetting['type'] = 'string', more: Partial<ClassSetting> = {}): ClassSetting => ({ key, label: key, description: '', type, secret: false, ...more })
const classSig = (settings: ClassSetting[], domain: string | null = null, title = 'Plug'): ClassSignature => ({ title, description: '', domain, settings })
/** An entity definition's graph with its settings `host` (text) and `level` (a number), and a Setting node of each. */
function definitionGraph(): VisualGraph {
  let graph: VisualGraph = { ...empty('Plug'), definition: { settings: [{ key: 'host', label: 'Host', type: 'string', value: '10.0.0.7' }, { key: 'level', label: 'Level', type: 'number', value: 3 }] } as never }
  graph = add(graph, 'setting', 'host', { key: 'host' }, { x: 0, y: 100 })
  return add(graph, 'setting', 'level', { key: 'level' }, { x: 0, y: 300 })
}
const argumentsOf = (graph: VisualGraph, id: string) => graph.nodes.find(n => n.id === id)!.config.arguments
const wiresInto = (graph: VisualGraph, id: string) => graph.edges.flatMap(e => (e.kind === 'data' && e.target === id ? [`${e.source}.${e.sourcePort}->${e.targetPort}`] : []))

it('applyClassCode keeps each typed value its setting still takes, and clears the rest by their labels', () => {
  const before = [setting('Address'), setting('Port', 'integer'), setting('Whole', 'number'), setting('Ratio', 'number'), setting('Mode', 'enum', { options: ['eco', 'comfort'] }),
    setting('Mode2', 'enum', { label: 'Second mode', options: ['eco', 'comfort'] }), setting('Flag', 'boolean'), setting('Gone', 'string', { label: 'Old setting' }), setting('Unset'), setting('Token')]
  const graph = add(definitionGraph(), 'csharp_device', 'plug', { key: 'plug', name: 'Plug', signature: classSig(before), arguments: {
    Address: lit('string', 'http://a'), Port: lit('number', 3), Whole: lit('number', 3), Ratio: lit('number', 2.5), Mode: lit('string', 'eco'), Mode2: lit('string', 'comfort'),
    Flag: lit('boolean', true), Gone: lit('string', 'x'), Unset: { kind: 'unset' }, Token: lit('string', 'abc') } })
  const after = [setting('Address'), setting('Port', 'number'), setting('Whole', 'integer'), setting('Ratio', 'integer'), setting('Mode', 'enum', { options: ['eco', 'away'] }),
    setting('Mode2', 'enum', { label: 'Second mode', options: ['eco', 'away'] }), setting('Flag'), setting('Token', 'string', { secret: true })]
  const result = applyClassCode(graph, 'plug', 'new code', classSig(after), catalog)
  // Text into text, an integer into a number and a whole number into an integer, a text the enum still lists.
  expect(argumentsOf(result.graph, 'plug')).toEqual({ Address: lit('string', 'http://a'), Port: lit('number', 3), Whole: lit('number', 3), Mode: lit('string', 'eco') })
  // A fraction into an integer, a text the enum no longer lists, true into text, a setting gone, a setting now secret;
  // the value of a setting gone that held none is dropped without a word.
  expect(result.notices).toEqual(['Cleared 5 values that no longer fit: Ratio, Second mode, Flag, Old setting, Token'])
  const node = result.graph.nodes.find(n => n.id === 'plug')!
  expect(node.config.source).toBe('new code')
  expect(node.config.signature).toEqual(classSig(after))
  // Everything else of the node is as it was.
  expect(node.config).toMatchObject({ key: 'plug', name: 'Plug' })
})

it('applyClassCode removes the wires into settings that are gone or no longer take the Setting, and keeps one into a setting now secret', () => {
  let graph = add(definitionGraph(), 'device', 'hall', { key: 'hall', name: 'Hall' })
  graph = add(graph, 'csharp_entity', 'thermo', { key: 'thermo', signature: classSig([setting('Address'), setting('Token'), setting('Count'), setting('Gone', 'string', { label: 'Old address' }), setting('Level', 'number')], 'sensor', 'Thermometer') })
  graph = connectData(graph, 'hall', 'entities', 'thermo', 'device')
  for (const port of ['Address', 'Token', 'Count', 'Gone']) graph = connectData(graph, 'host', 'value', 'thermo', port)
  graph = connectData(graph, 'level', 'value', 'thermo', 'Level')
  expect(wiresInto(graph, 'thermo')).toHaveLength(6)
  const after = classSig([setting('Address'), setting('Token', 'string', { secret: true }), setting('Count', 'integer'), setting('Level', 'integer')], 'sensor', 'Thermometer')
  const result = applyClassCode(graph, 'thermo', 'code', after, catalog)
  // The device wire and the Setting wires that still fit stay: a secret is given by a Setting, a number Setting goes into an integer.
  expect(wiresInto(result.graph, 'thermo')).toEqual(['hall.entities->device', 'host.value->Address', 'host.value->Token', 'level.value->Level'])
  expect(result.notices).toEqual(['Removed 2 connections that no longer fit: Count, Old address'])
  // Both notices together, in the Custom step's order.
  const typed = updateNode(graph, 'thermo', { config: { ...graph.nodes.find(n => n.id === 'thermo')!.config, arguments: { Count: lit('string', 'x') } } })
  const both = applyClassCode({ ...typed, edges: typed.edges.filter(e => !(e.kind === 'data' && e.targetPort === 'Count')) }, 'thermo', 'code', after, catalog)
  expect(both.notices).toEqual(['Removed 1 connection that no longer fits: Old address', 'Cleared a value that no longer fits: Count'])
})

it('applyClassCode needs no refit for a changed type, and changes nothing for a signature its node cannot have', () => {
  let graph = add(definitionGraph(), 'csharp_entity', 'thermo', { key: 'thermo', signature: classSig([setting('Address'), setting('Host')], 'sensor', 'Thermometer'), arguments: { Address: lit('string', 'http://a') } })
  graph = connectData(graph, 'host', 'value', 'thermo', 'Host')
  // A sensor becomes a switch: no step names it, so its values and wires stay.
  const retyped = applyClassCode(graph, 'thermo', 'switch code', classSig([setting('Address'), setting('Host')], 'switch', 'Relay'), catalog)
  expect(retyped.notices).toEqual([])
  expect(argumentsOf(retyped.graph, 'thermo')).toMatchObject({ Address: lit('string', 'http://a') })
  expect(argumentsOf(retyped.graph, 'thermo')).toEqual(argumentsOf(graph, 'thermo'))
  expect(wiresInto(retyped.graph, 'thermo')).toEqual(['host.value->Host'])
  expect(retyped.graph.nodes.find(n => n.id === 'thermo')!.config.signature).toMatchObject({ domain: 'switch', title: 'Relay' })
  // The signature written is the node's own copy.
  const given = classSig([setting('Address')], 'switch', 'Relay')
  const own = applyClassCode(graph, 'thermo', 'code', given, catalog)
  given.settings[0].label = 'Changed'
  expect((own.graph.nodes.find(n => n.id === 'thermo')!.config.signature as ClassSignature).settings[0].label).toBe('Address')
  // A setting keyed device (the C# entity's own pin), a signature not as the compiler writes it, a node without a class.
  for (const refused of [classSig([setting('device')], 'sensor'), { title: 5 } as never])
    expect(applyClassCode(graph, 'thermo', 'code', refused, catalog)).toEqual({ graph, notices: [] })
  expect(applyClassCode(graph, 'host', 'code', classSig([]), catalog)).toEqual({ graph, notices: [] })
  // A C# device has no device pin: a setting of that key is its own.
  const device = add(definitionGraph(), 'csharp_device', 'plug', { key: 'plug', name: 'Plug' })
  expect(applyClassCode(device, 'plug', 'code', classSig([setting('device')]), catalog).graph.nodes.at(-1)!.config.signature).toEqual(classSig([setting('device')]))
})

it('applyClassCode keeps an entity id its entity setting still takes, typed as the setting\'s pin, and clears one of another domain or a text', () => {
  const lamp = (more: Partial<ClassSetting> = {}) => setting('Lamp', 'entity', { domain: 'light', ...more })
  const relay = (settings: ClassSetting[], value: unknown) =>
    add(definitionGraph(), 'csharp_entity', 'relay', { key: 'relay', signature: classSig(settings, 'switch', 'Relay'), arguments: { Lamp: value } })
  const apply = (settings: ClassSetting[], value: unknown, after: ClassSetting[]) => applyClassCode(relay(settings, value), 'relay', 'code', classSig(after, 'switch', 'Relay'), catalog)
  const kitchen = lit('entity_id:light', 'light.kitchen')
  // A property retyped from string to LightEntity: the address typed in is no light's id, and is cleared by its label.
  const retyped = apply([setting('Lamp', 'string', { label: 'Lamp address' })], lit('string', '192.168.1.20'), [lamp()])
  expect(argumentsOf(retyped.graph, 'relay')).toEqual({})
  expect(retyped.notices).toEqual(['Cleared a value that no longer fits: Lamp address'])
  // A text that is a light's id fits the LightEntity, and is typed as one.
  const id = apply([setting('Lamp')], lit('string', 'light.porch'), [lamp()])
  expect(argumentsOf(id.graph, 'relay')).toEqual({ Lamp: lit('entity_id:light', 'light.porch') })
  expect(id.notices).toEqual([])
  // From LightEntity to SwitchEntity: a light is no switch.
  const other = apply([lamp()], kitchen, [setting('Lamp', 'entity', { domain: 'switch' })])
  expect(argumentsOf(other.graph, 'relay')).toEqual({})
  expect(other.notices).toEqual(['Cleared a value that no longer fits: Lamp'])
  // From LightEntity to Entity: any entity fits, and the value is typed as any entity's id.
  const plain = apply([lamp()], kitchen, [setting('Lamp', 'entity')])
  expect(argumentsOf(plain.graph, 'relay')).toEqual({ Lamp: lit('entity_id', 'light.kitchen') })
  expect(plain.notices).toEqual([])
  // From Entity back to LightEntity, a light's id stays and is typed as a light's again.
  expect(argumentsOf(apply([setting('Lamp', 'entity')], lit('entity_id', 'light.kitchen'), [lamp()]).graph, 'relay')).toEqual({ Lamp: kitchen })
  // The same LightEntity, now required: the value stays as it was.
  const required = apply([lamp()], kitchen, [lamp({ required: true })])
  expect(argumentsOf(required.graph, 'relay')).toEqual({ Lamp: kitchen })
  expect(required.notices).toEqual([])
  // A value that is no entity id's shape fits no entity setting.
  for (const value of [lit('entity_id:light', 'light'), lit('entity_id:light', 'Light.Kitchen'), lit('entity_id:light', 3)])
    expect(apply([lamp()], value, [setting('Lamp', 'entity')]).notices, String(value.value)).toEqual(['Cleared a value that no longer fits: Lamp'])
  // A value typed as an entity's id fits no text setting, as before.
  expect(apply([lamp()], kitchen, [setting('Lamp')]).notices).toEqual(['Cleared a value that no longer fits: Lamp'])
})

it('applyClassCode removes the wire from a text Setting into a setting that became an entity', () => {
  let graph = add(definitionGraph(), 'csharp_entity', 'relay', { key: 'relay', signature: classSig([setting('Lamp')], 'switch', 'Relay') })
  graph = connectData(graph, 'host', 'value', 'relay', 'Lamp')
  expect(wiresInto(graph, 'relay')).toEqual(['host.value->Lamp'])
  const result = applyClassCode(graph, 'relay', 'code', classSig([setting('Lamp', 'entity', { domain: 'light', required: true })], 'switch', 'Relay'), catalog)
  expect(wiresInto(result.graph, 'relay')).toEqual([])
  expect(result.notices).toEqual(['Removed 1 connection that no longer fits: Lamp'])
})

/** A C# device's code: Plug, with the settings Address and Token. */
const plugCode = (token = '[Setting("Token")]') => `/// <summary>A plug.</summary>
public class Plug : SharpDevice
{
    [Setting("Address")]
    public string Address { get; set; } = "";

    ${token}
    public string Token { get; set; } = "";
}`
/** The saved entity definition `plug`: its C# device `plug` (Plug), its Address wired from the Setting `host`, its Token typed. */
function plugDefinition(): VisualGraph {
  const graph = add(definitionGraph(), 'csharp_device', 'plug', { key: 'plug', name: 'Plug', via: null, source: plugCode(),
    signature: classSig([setting('Address'), setting('Token')]), arguments: { Token: lit('string', 'abc') } }, { x: 400, y: 100 })
  return connectData(graph, 'host', 'value', 'plug', 'Address')
}
/** The editor page over a saved entity definition `plug` holding `graph`, as the panel mounts it. */
function mountDefinition(graph: VisualGraph, options: { callWS?: (message: any) => unknown } = {}) {
  const host = root(), fixture = visualFixture({ catalog })
  const { definition, ...logic } = graph
  fixture.entities.seed('plug', { name: 'Plug', settings: (definition as unknown as { settings: never[] }).settings, logic: { kind: 'graph', graph: logic as never } })
  const callWS = vi.fn((message: any) => options.callWS?.(message) ?? fixture.callWS(message))
  const hass = { ...fixture, callWS, user: { is_admin: true } }
  const history = createMemoryHistory()
  history.set({ value: '/entities/plug' })
  dispose = render(() => <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <HassCtx.Provider value={hass as any}>
      <MemoryRouter history={history}><Route path="/entities/:id" component={EntityDefinitionView} /></MemoryRouter>
    </HassCtx.Provider></QueryClientProvider>, host)
  // Unsaved changes, as the page shows them.
  const dirty = () => host.querySelector('.hs-save-chip__text')?.textContent !== 'Saved'
  return { host, fixture, callWS, dirty }
}
const kindLine = () => dialog()!.querySelector('.hs-code-bar__kind')!.textContent
const settingNames = () => [...dialog()!.querySelectorAll('[aria-label="Settings"] .hs-code-pin__name')].map(el => el.textContent)

it('opens a C# device\'s code from its card and its panel, headed by its kind, with its settings, checked as a C# device', async () => {
  const { host, callWS } = mountDefinition(plugDefinition())
  await vi.waitFor(() => expect(card(host, 'plug')).toBeTruthy())
  button(card(host, 'plug'), 'Edit code of Plug')!.click()
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  expect(kindLine()).toBe('C# device code')
  expect(dialog()!.querySelector('h2')!.textContent).toBe('Plug')
  expect(dialog()!.querySelector('[aria-label="Pins"]')).toBeNull()
  expect(dialog()!.querySelector('[aria-label="Settings"] h3')!.textContent).toBe('Settings')
  expect(settingNames()).toEqual(['Address', 'Token'])
  await vi.waitFor(() => expect(state()).toBe('No problems'))
  expect(previews(callWS).at(-1).request).toEqual({ operation: 'custom_preview', nodeId: 'plug', source: plugCode(), kind: 'csharp_device' })
  expect(apply().disabled).toBe(false)
  // A secret is said so; a code without settings says how to make one.
  editors[0].change(plugCode('[Setting("Token", Secret = true)]'))
  await vi.waitFor(() => expect(state()).toBe('No problems'))
  expect([...dialog()!.querySelectorAll('[aria-label="Settings"] .hs-code-pin')].map(el => el.textContent)).toEqual([expect.not.stringContaining('Secret'), expect.stringContaining('Secret')])
  editors[0].change('public class Plug : SharpDevice { }')
  await vi.waitFor(() => expect(settingNames()).toEqual([]))
  expect(dialog()!.querySelector('[aria-label="Settings"]')!.textContent).toContain('This class has no settings. Mark a property with [Setting] to make it an input.')
  // Reset to example is the C# device's own template.
  button(dialog()!, 'Code actions')!.click()
  ;[...dialog()!.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')].find(b => b.textContent === 'Reset to example')!.click()
  expect(editors[0].model.getValue()).toBe(exampleSource('csharp_device', catalog))
  button(dialog()!, 'Cancel')!.click()
  button([...dialog()!.querySelectorAll('dialog')].find(d => d.open)!, 'Discard')!.click()
  await vi.waitFor(() => expect(dialog()).toBeNull())
  // The panel's button (the node is selected now), and E on the card.
  button(host.querySelector('aside[aria-label="Node inspector"]')!, 'Edit code')!.click()
  await vi.waitFor(() => expect(dialog()).not.toBeNull())
  button(dialog()!, 'Cancel')!.click()
  await vi.waitFor(() => expect(dialog()).toBeNull())
  card(host, 'plug').dispatchEvent(new KeyboardEvent('keydown', { key: 'e', bubbles: true, cancelable: true }))
  await vi.waitFor(() => expect(dialog()).not.toBeNull())
  button(dialog()!, 'Cancel')!.click()
  await vi.waitFor(() => expect(dialog()).toBeNull())
  // A Setting node has no code.
  expect(card(host, 'host').querySelector('.hs-card__code')).toBeNull()
})

it('names an entity setting\'s type in the code editor\'s Settings: Entity, with its domain when it has one', async () => {
  // The check gives a class with a LightEntity, an Entity and a string setting.
  const signature = classSig([setting('Lamp', 'entity', { domain: 'light', required: true }), setting('Source', 'entity'), setting('Address')])
  const { host } = mountDefinition(plugDefinition(), { callWS: message => message.request?.operation === 'custom_preview'
    ? Promise.resolve({ version: 1, ok: true, result: { valid: true, diagnostics: [], signature } }) : undefined })
  await vi.waitFor(() => expect(card(host, 'plug')).toBeTruthy())
  button(card(host, 'plug'), 'Edit code of Plug')!.click()
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  editors[0].change(`${plugCode()}\n`)
  await vi.waitFor(() => expect(settingNames()).toEqual(['Lamp', 'Source', 'Address']))
  const meta = () => [...dialog()!.querySelectorAll('[aria-label="Settings"] .hs-code-pin__meta')].map(el => el.textContent)
  expect(meta()).toEqual(['Entity (light) · Required', 'Entity', 'Text'])
})

it('says in the code editor\'s Settings which settings are required, as it says which are secret', async () => {
  const signature = classSig([setting('Host', 'string', { required: true }), setting('Token', 'string', { secret: true, required: true }), setting('Address'), setting('Port', 'integer')])
  const { host } = mountDefinition(plugDefinition(), { callWS: message => message.request?.operation === 'custom_preview'
    ? Promise.resolve({ version: 1, ok: true, result: { valid: true, diagnostics: [], signature } }) : undefined })
  await vi.waitFor(() => expect(card(host, 'plug')).toBeTruthy())
  button(card(host, 'plug'), 'Edit code of Plug')!.click()
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  editors[0].change(`${plugCode()}\n`)
  await vi.waitFor(() => expect(settingNames()).toEqual(['Host', 'Token', 'Address', 'Port']))
  const meta = () => [...dialog()!.querySelectorAll('[aria-label="Settings"] .hs-code-pin__meta')].map(el => el.textContent)
  expect(meta()).toEqual(['Text · Required', 'Text · Secret · Required', 'Text', 'Whole number'])
})

it('applies a C# device\'s code as one undo step: the wire into a setting it dropped and the value typed into one now secret go, with both notices', async () => {
  const { host } = mountDefinition(plugDefinition())
  await vi.waitFor(() => expect(card(host, 'plug')).toBeTruthy())
  const before = graphOf(host)
  button(card(host, 'plug'), 'Edit code of Plug')!.click()
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  const next = plugCode('[Setting("Token", Secret = true)]').replace('[Setting("Address")]\n    public string Address { get; set; } = "";\n\n    ', '')
  editors[0].change(next)
  await vi.waitFor(() => expect(settingNames()).toEqual(['Token']))
  await vi.waitFor(() => expect(state()).toBe('No problems'))
  vi.useFakeTimers({ shouldAdvanceTime: true })
  apply().click()
  await vi.waitFor(() => expect(dialog()).toBeNull())
  const notice = () => host.querySelector('.hs-canvas-notice')?.textContent
  expect(notice()).toBe('Removed 1 connection that no longer fits: Address. Cleared a value that no longer fits: Token.')
  await vi.advanceTimersByTimeAsync(30_000)
  expect(notice()).toBe('Removed 1 connection that no longer fits: Address. Cleared a value that no longer fits: Token.')
  const after = graphOf(host)
  const node = after.nodes.find(n => n.id === 'plug')!
  expect(node.config.source).toBe(next)
  expect(node.config.signature).toEqual({ title: 'Plug', description: 'A plug.', domain: null, settings: [setting('Token', 'string', { secret: true })] })
  expect(node.config.arguments).toEqual({})
  expect(wiresInto(after, 'plug')).toEqual([])
  expect(node.config).toMatchObject({ key: 'plug', name: 'Plug' })
  button(host, 'Undo')!.click()
  expect(graphOf(host)).toEqual(before)
})

it('offers Apply for a C# entity only for a class it can be: a setting called device is refused, with the reason', async () => {
  const graph = add(definitionGraph(), 'csharp_entity', 'thermo', { key: 'thermo', source: 'public class Thermometer : SharpSensor { }', signature: classSig([], 'sensor', 'Thermometer') }, { x: 400, y: 100 })
  const { host, callWS } = mountDefinition(graph)
  await vi.waitFor(() => expect(card(host, 'thermo')).toBeTruthy())
  button(card(host, 'thermo'), 'Edit code of Thermometer')!.click()
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  expect(kindLine()).toBe('C# entity code')
  await vi.waitFor(() => expect(state()).toBe('No problems'))
  expect(previews(callWS).at(-1).request.kind).toBe('csharp_entity')
  // The check (the fixture's, which does not say it) passes; the node cannot have the setting.
  const device = 'public class Thermometer : SharpSensor\n{\n    [Setting("Device")]\n    public string device { get; set; } = "";\n}'
  editors[0].change(device)
  await vi.waitFor(() => expect(state()).toBe('1 problem'))
  expect(apply().disabled).toBe(true)
  const reason = 'A setting cannot be called device: that is the pin the entity\'s device connects to'
  const problems = () => [...dialog()!.querySelectorAll('[aria-label="Problems"] .hs-code-problem__message')].map(el => el.textContent)
  expect(problems()).toEqual([reason])
  // Said once when the server says it too.
  const said = { sourceId: 'thermo', startLine: 3, startColumn: 5, endLine: 3, endColumn: 20, severity: 3, message: reason }
  callWS.mockImplementation((message: any) => message.request?.operation === 'custom_preview'
    ? Promise.resolve({ version: 1, ok: true, result: { valid: false, diagnostics: [said], signature: classSig([setting('device')], 'sensor', 'Thermometer') } })
    : visualFixture({ catalog }).callWS(message))
  editors[0].change(`${device}\n`)
  await vi.waitFor(() => expect(dialog()!.querySelector('.hs-code-problem__where')?.textContent).toBe('Line 3, column 5'))
  expect(problems()).toEqual([reason])
  expect(state()).toBe('1 problem')
  expect(apply().disabled).toBe(true)
  // The help is about one class: its base, its settings and its life, and no Add for an entity.
  button(dialog()!, 'How to write a C# entity')!.click()
  const help = [...dialog()!.querySelectorAll('dialog')].find(d => d.open)!
  for (const fact of ['one public class', 'SharpSwitch', '[Setting("Label")]', 'Secret = true', 'Setup()', 'Start()', 'Stop()', 'Stopping']) expect(help.textContent, fact).toContain(fact)
  expect(help.textContent).not.toContain('Add(')
  expect(help.textContent).not.toContain('[Node')
})

it('words the help of a C# device\'s code around SharpDevice and the entities it adds', async () => {
  const node = plugDefinition().nodes.find(n => n.id === 'plug')!
  const fixture = visualFixture({ catalog })
  dispose = render(() => <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <HassCtx.Provider value={{ ...fixture, user: { is_admin: true } } as any}>
      <CustomCodeEditor node={node} catalog={catalog} document="entity" onApply={() => {}} onClose={() => {}} />
    </HassCtx.Provider></QueryClientProvider>, root())
  await vi.waitFor(() => expect(dialog()?.open).toBe(true))
  button(dialog()!, 'How to write a C# device')!.click()
  const help = [...dialog()!.querySelectorAll('dialog')].find(d => d.open)!
  expect(help.textContent).toContain('Writing a C# device')
  for (const fact of ['SharpDevice', 'Add(', '[Setting("Label")]', 'Setup()', 'Start()', 'Stop()']) expect(help.textContent, fact).toContain(fact)
  expect(help.textContent).not.toContain('[Node')
  // A setting may be an entity, which takes no Setting, and a string or entity one may be required; a class follows an
  // entity with Track and calls services through Services as well as CallService.
  for (const fact of ['LightEntity', 'takes no Setting', 'is required', 'Track(entity, ...)', 'Services.<Domain>.<Service>(...)', 'CallService(']) expect(help.textContent, fact).toContain(fact)
  expect(help.textContent).not.toContain('or an enum.')
  expect(help.textContent).not.toContain('A setting given nothing keeps')
})

it('keeps a C# device\'s unapplied code in the draft by its kind, opens it again on Restore, and never in a node of another kind', async () => {
  const first = mountDefinition(plugDefinition())
  await vi.waitFor(() => expect(card(first.host, 'plug')).toBeTruthy())
  button(card(first.host, 'plug'), 'Edit code of Plug')!.click()
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  editors[0].change(`${plugCode()}\n// typed, not applied`)
  dispose?.(); dispose = undefined
  document.body.innerHTML = ''
  const key = Object.keys(localStorage).find(k => k.startsWith('hass-sharp:draft:') && k.endsWith('plug'))!
  const kept = JSON.parse(localStorage.getItem(key)!)
  expect(kept.code).toEqual({ nodeId: 'plug', text: `${plugCode()}\n// typed, not applied`, kind: 'csharp_device' })
  const restore = () => [...document.querySelectorAll<HTMLDialogElement>('dialog')].find(d => d.open && d.textContent?.includes('Restore unsaved changes'))
  resetHost()
  const second = mountDefinition(plugDefinition())
  await vi.waitFor(() => expect(restore()).toBeTruthy())
  button(restore()!, 'Restore')!.click()
  await vi.waitFor(() => expect(editors).toHaveLength(1))
  expect(kindLine()).toBe('C# device code')
  expect(editors[0].model.getValue()).toBe(`${plugCode()}\n// typed, not applied`)
  // It can be applied once checked. The page's chip counts the graph's changes only, and the graph is still the saved one.
  await vi.waitFor(() => expect(apply().disabled).toBe(false))
  expect(second.dirty()).toBe(false)
  dispose?.(); dispose = undefined
  document.body.innerHTML = ''
  // The same draft as a Custom C# step's (no kind): the node of that id is a C# device, so the code is only offered to copy.
  localStorage.setItem(key, JSON.stringify({ ...kept, code: { nodeId: 'plug', text: kept.code.text } }))
  resetHost()
  mountDefinition(plugDefinition())
  await vi.waitFor(() => expect(restore()).toBeTruthy())
  button(restore()!, 'Restore')!.click()
  const left = () => [...document.querySelectorAll('dialog')].find(d => d.open && d.textContent?.includes('no longer exists'))
  await vi.waitFor(() => expect(left()).toBeTruthy())
  expect(left()!.querySelector('textarea')!.value).toBe(`${plugCode()}\n// typed, not applied`)
  expect(dialog()).toBeNull()
  expect(editors).toHaveLength(0)
})

it('checks a Custom C# step\'s code as a step, saying so', async () => {
  const { callWS } = await open()
  expect(previews(callWS)[0].request).toEqual({ operation: 'custom_preview', nodeId: 'code', source: template, kind: 'custom' })
})
