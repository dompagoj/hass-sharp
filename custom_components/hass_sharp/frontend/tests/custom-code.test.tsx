import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { readFileSync, existsSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
import { completions, editors, hovers, markers, resetHost } from './fake-monaco'
import { fixtureCustomPreview, visualFixture } from './visual-fixture'
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
const S = 'global::System.String', I = 'global::System.Int32', D = 'global::System.Double'
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

it('a new Custom C# node shows the template\'s pins and title at once, and the fixture derives the same pins from the template', () => {
  const graph = addNode(createGraph('New'), 'custom', 'fresh', catalog)
  const node = graph.nodes.find(n => n.id === 'fresh')!
  expect(node.label).toBeUndefined()
  const pins = customSignature(node, catalog)!
  expect(pins.title).toBe('Rename this node')
  expect(Object.keys(pins.inputs)).toEqual(['text'])
  expect(Object.keys(pins.outputs)).toEqual(['result'])
  const derived = fixtureCustomPreview('fresh', template)
  expect(derived.valid).toBe(true)
  expect(derived.signature).toMatchObject({ title: pins.title, pure: false, inputs: { text: { types: [S], required: true } }, outputs: { result: { type: S } } })
  expect(fixtureCustomPreview('fresh', `${template}\n!!`)).toMatchObject({ valid: false, diagnostics: [{ startLine: 12, startColumn: 1, severity: 3 }] })
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
  apply().click()
  await vi.waitFor(() => expect(dialog()).toBeNull())
  expect(host.querySelector('.hs-canvas-notice')!.textContent).toBe('Removed 2 connections that no longer fit: Text, Result.')
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

it('removes the step order wires of a node that becomes a value node, says so, and says when it becomes a step again', () => {
  const graph = sample()
  const pure = applyCustomCode(graph, 'code', 'code', signature({ text: S }, { result: S }, true), catalog)
  expect(pure.graph.edges.filter(e => e.kind !== 'data' && (e.source === 'code' || e.target === 'code'))).toEqual([])
  expect(pure.graph.edges.filter(e => e.kind === 'data')).toHaveLength(2)
  expect(pure.notice).toBe('It is now a value node: removed its 2 step order wires.')
  const step = applyCustomCode(pure.graph, 'code', 'code', signature({ text: S }, { result: S }), catalog)
  expect(step.notice).toBe('It is now a step: connect it into the step order.')
  // One undo step restores code, pins and wires together.
  const history = createGraphHistory(graph)
  history.commit(pure.graph)
  history.undo()
  expect(history.graph()).toEqual(graph)
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
