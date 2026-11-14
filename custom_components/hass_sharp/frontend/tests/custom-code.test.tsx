import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { readFileSync, existsSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
import { completions, definitions, editors, formats, hovers, markers, resetHost, signatures } from './fake-monaco'
import { visualFixture } from './visual-fixture'
import { run } from './run-fixture'
import { createMemoryHistory, MemoryRouter, Route } from '@solidjs/router'
import { RunTraceView } from '../src/views/RunTraceView'
import { HassCtx } from '../src/context'
import { VisualEditor } from '../src/visual/VisualEditor'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { CustomCodeEditor } from '../src/visual/CustomCodeEditor'
import { applyCustomCode, exampleSource } from '../src/visual/customCode'
import { addNode, connectNodes, createGraph, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import { connectData, customSignature, dataOutputs, type CustomSignature, type VisualCatalogSnapshot } from '../src/visual/contract'

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
  for (const fact of ['[Port("Label")]', 'Pure = true', 'CancellationToken', 'NodeContext', 'State(entityId)', 'CallService(service, target, data, returnResponse)', 'Log(message, level)', '<summary>', '(double Average, double Max)'])
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
