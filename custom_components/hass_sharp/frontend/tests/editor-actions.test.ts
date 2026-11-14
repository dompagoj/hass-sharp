import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRoot } from 'solid-js'
import { FakeEditor, FakeModel, completions, hovers, markers, resetHost } from './fake-monaco'
import { useEditorActions } from '../src/components/Editor/editor-actions'

vi.mock('monaco-editor', async () => (await import('./fake-monaco')).host)
const position = { lineNumber: 1, column: 4 }
const noCancellation = { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) }
const diagnostic = { startLine: 1, startColumn: 1, endLine: 1, endColumn: 2, message: 'bad code', severity: 3 }
const completion = { displayText: 'List', displayTextPrefix: '', displayTextSuffix: '<>', filterText: 'List', tags: ['Class'], textEdit: { start: 0, length: 3, newText: 'List' }, additionalTextEdits: [] }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r }); return { promise, resolve } }
let cleanup: () => void
// The library transport: handlers answer a request by its operation; this wraps the answer in the reply envelope.
function setup(handler: (request: any) => any = vi.fn(async () => [] as any), onSave = vi.fn(async () => {})) {
  const context = () => ({ id: 'main', source: 'Lis', workspaceVersion: '1', generation: 'saved', sources: [] })
  const callWS = vi.fn(async (message: any) => {
    const r = message.request
    let data = await handler(r)
    if (r.operation === 'preview') data = { valid: !data.some((d: any) => d.severity === 3), diagnostics: data.map((d: any) => ({ ...d, sourceId: 'main' })) }
    if (r.operation === 'hover' && typeof data === 'string') data = { text: data }
    return { version: 1, ok: true, result: { id: r.id, workspaceVersion: r.workspaceVersion, generation: r.generation, sourceGeneration: 'source-1', saved: false, data } }
  })
  const editor = new FakeEditor({ value: 'Lis' })
  let actions!: ReturnType<typeof useEditorActions>
  createRoot(dispose => { cleanup = dispose; actions = useEditorActions({ callWS } as any, { context }); actions.register(editor as any, onSave) })
  return { editor, actions, onSave, callWS }
}
beforeEach(() => { resetHost(); vi.useFakeTimers() })
afterEach(() => { cleanup?.(); vi.useRealTimers() })
async function flush() { await vi.advanceTimersByTimeAsync(600) }

describe('Monaco requests', () => {
  it('inserts the Roslyn edit instead of the decorated display label', async () => {
    const { editor } = setup(vi.fn(async (m: any) => m.operation === 'completion' ? [completion] : []))
    const request = completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
    await flush()
    const result = await request
    expect(result.suggestions[0].label).toBe('List<>')
    expect(result.suggestions[0].insertText).toBe('List')
    expect(result.suggestions[0].range).toEqual({ startLineNumber: 1, endLineNumber: 1, startColumn: 1, endColumn: 4 })
  })
  it('preserves method completion kind zero', async () => {
    const { editor } = setup(vi.fn(async (m: any) => m.operation === 'completion' ? [{ ...completion, tags: ['Method'] }] : []))
    const result = await completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
    expect(result.suggestions[0].kind).toBe(0)
  })
  it('maps primary and additional Roslyn edits against the same multiline source', async () => {
    const item = { ...completion, textEdit: { start: 5, length: 3, newText: 'List' }, additionalTextEdits: [{ start: 0, length: 0, newText: 'using Example;\n' }] }
    const { editor } = setup(vi.fn(async (m: any) => m.operation === 'completion' ? [item] : []))
    editor.change('// x\nLis')
    const result = await completions[0].provideCompletionItems(editor.model, { lineNumber: 2, column: 4 }, {}, noCancellation)
    expect(result.suggestions[0].range).toEqual({ startLineNumber: 2, startColumn: 1, endLineNumber: 2, endColumn: 4 })
    expect(result.suggestions[0].additionalTextEdits).toEqual([{ range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 }, text: 'using Example;\n' }])
  })
  it('settles superseded completion requests and ignores their late responses', async () => {
    const response = deferred<any>()
    const { editor } = setup(vi.fn(async (m: any) => m.operation === 'completion' ? response.promise : []))
    const first = completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
    await vi.advanceTimersByTimeAsync(160)
    editor.change('new')
    const second = completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
    response.resolve([completion])
    await flush()
    expect((await first).suggestions).toEqual([])
    expect((await second).suggestions).toHaveLength(1)
  })
  it('cancels an in-flight completion without waiting for the server', async () => {
    const response = deferred<any>()
    const { editor } = setup(vi.fn(async (m: any) => m.operation === 'completion' ? response.promise : []))
    let cancel!: () => void
    const token = { isCancellationRequested: false, onCancellationRequested: (fn: () => void) => { cancel = fn; return { dispose() {} } } }
    const result = completions[0].provideCompletionItems(editor.model, position, {}, token)
    await vi.advanceTimersByTimeAsync(160)
    let settled = false
    Promise.resolve(result).then(() => { settled = true })
    cancel()
    await vi.advanceTimersByTimeAsync(0)
    expect(settled).toBe(true)
    expect((await result).suggestions).toEqual([])
  })
  it('ignores stale diagnostics and hover after editing', async () => {
    const diagnostics = deferred<any>(); const hover = deferred<any>()
    const { editor } = setup(vi.fn(async (m: any) => m.operation === 'hover' ? hover.promise : diagnostics.promise))
    const result = hovers[0].provideHover(editor.model, position, noCancellation)
    editor.change('valid')
    diagnostics.resolve([diagnostic]); hover.resolve('old symbol')
    await vi.advanceTimersByTimeAsync(0)
    expect(await result).toBeNull()
    expect(markers.get(editor.model.uri.toString()) ?? []).toEqual([])
  })
  it('checks the version when consuming an already delivered completion', async () => {
    const response = deferred<any>()
    const { editor } = setup(vi.fn((m: any) => m.operation === 'completion' ? response.promise : Promise.resolve([])))
    const result = completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
    response.promise.then(() => editor.change('newer'))
    response.resolve([completion])
    expect((await result).suggestions).toEqual([])
  })
  it('does not answer requests for another editor model', async () => {
    setup()
    const foreign = new FakeModel('abc')
    expect(await completions[0].provideCompletionItems(foreign, position, {}, noCancellation)).toBeNull()
    expect(await hovers[0].provideHover(foreign, position, noCancellation)).toBeNull()
  })
  it('disposes providers, subscriptions and pending requests', async () => {
    const { editor } = setup()
    const result = completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
    cleanup()
    await flush()
    expect((await result).suggestions).toEqual([])
    expect(completions).toHaveLength(0); expect(hovers).toHaveLength(0)
    expect(editor.listeners.size).toBe(0)
    expect(editor.actions.size).toBe(0)
    expect(markers.get(editor.model.uri.toString()) ?? []).toEqual([])
  })
})
describe('Save', () => {
  it('uses one command to format, validate and persist for button and Ctrl/Cmd+S', async () => {
    const saved: string[] = []
    const { editor, actions } = setup(vi.fn(async (m: any) => m.operation === 'format' ? 'formatted' : []), vi.fn(async (model: any) => { saved.push(model.getValue()) }))
    await flush()
    await actions.save()
    expect(saved).toEqual(['formatted'])
    const action = [...editor.actions.values()].find(a => a.keybindings?.includes(2048 | 49))
    expect(action).toBeDefined()
    editor.change('again'); await flush()
    await action.run()
    expect(saved).toEqual(['formatted', 'formatted'])
  })
  it('does not overwrite or save edits made while formatting', async () => {
    const format = deferred<any>()
    const { editor, actions, onSave, callWS } = setup(vi.fn(async (m: any) => m.operation === 'format' ? format.promise : []))
    await flush()
    const saving = actions.save()
    await vi.advanceTimersByTimeAsync(0)
    expect(callWS.mock.calls.some(([m]) => m.request.operation === 'format')).toBe(true)
    editor.change('newer edit')
    format.resolve('stale format'); await saving
    expect(editor.model.getValue()).toBe('newer edit')
    expect(onSave).not.toHaveBeenCalled()
  })
  it('does not apply formatting delivered just before a model edit', async () => {
    const format = deferred<any>()
    const { editor, actions, onSave } = setup(vi.fn((m: any) => m.operation === 'format' ? format.promise : Promise.resolve([])))
    await flush()
    const saving = actions.save()
    await vi.advanceTimersByTimeAsync(0)
    format.promise.then(() => editor.change('newer'))
    format.resolve('stale format')
    await saving
    expect(editor.model.getValue()).toBe('newer')
    expect(onSave).not.toHaveBeenCalled()
  })
  it('blocks only current-model errors and coalesces duplicate saves', async () => {
    const format = deferred<any>()
    const { editor, actions, onSave, callWS } = setup(vi.fn(async (m: any) => m.operation === 'format' ? format.promise : []))
    await flush()
    markers.set('other-model', [{ severity: 8 }])
    markers.set(editor.model.uri.toString(), [{ severity: 4 }])
    const first = actions.save(); const second = actions.save()
    format.resolve('formatted'); await Promise.all([first, second])
    expect(onSave).toHaveBeenCalledTimes(1)
    expect(callWS.mock.calls.filter(([m]) => m.request.operation === 'format')).toHaveLength(1)
  })
  it('validates the formatted revision before persisting', async () => {
    const { actions, onSave } = setup(vi.fn(async (m: any) => m.operation === 'format' ? 'broken' : m.source === 'broken' ? [diagnostic] : []))
    await flush(); await actions.save()
    expect(onSave).not.toHaveBeenCalled()
  })
})

describe('Editor feedback and typing recovery', () => {
  it('exposes diagnostics, selects exact ranges, and clears stale results on edits', async () => {
    const { editor, actions } = setup(vi.fn(async () => [diagnostic]))
    await flush()
    expect(actions.diagnostics()).toEqual([{ ...diagnostic, sourceId: 'main' }])
    actions.goToDiagnostic(actions.diagnostics()[0])
    expect(editor.selection).toEqual({ startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 2 })
    editor.change('fixed')
    expect(actions.diagnostics()).toEqual([])
    expect(markers.get(editor.model.uri.toString())).toEqual([])
    editor.setModel(new FakeModel('other'))
    expect(actions.diagnostics()).toEqual([])
  })
  it('surfaces validation transport failure and recovers after another edit', async () => {
    const logged = vi.spyOn(console, 'error').mockImplementation(() => {})
    let failed = true
    const { editor, actions } = setup(vi.fn(async () => { if (failed) throw new Error('offline'); return [] }))
    await flush()
    expect(actions.validationError()).toContain('offline')
    expect(actions.saveDisabled()).toBe(true)
    failed = false
    editor.change('recovered'); await flush()
    expect(actions.validationError()).toBeNull()
    expect(actions.saveDisabled()).toBe(false)
    expect(logged).toHaveBeenCalledWith('Failed to get diagnostics', expect.any(Error))
    logged.mockRestore()
  })
  it('reopens suggestions after typing cancels an in-flight request', async () => {
    const response = deferred<any>()
    const { editor } = setup(vi.fn(async (m: any) => m.operation === 'completion' ? response.promise : []))
    const first = completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
    editor.change('List')
    editor.move({ lineNumber: 1, column: 5 }, 'keyboard', 0)
    await vi.advanceTimersByTimeAsync(150)
    expect(editor.triggers).toContain('editor.action.triggerSuggest')
    response.resolve([completion])
    expect((await first).suggestions).toEqual([])
  })
  it('marks server-filtered lists incomplete and shows namespaces as modules', async () => {
    const { editor } = setup(vi.fn(async (m: any) => m.operation === 'completion' ? [{ ...completion, tags: ['Namespace'] }] : []))
    const result = await completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
    expect(result.incomplete).toBe(true)
    expect(result.suggestions[0].kind).toBe(8)
  })
})

it('does not reopen suggestions after switching models or disposing the editor', async () => {
  const response = deferred<any>()
  const { editor } = setup(vi.fn(async (m: any) => m.operation === 'completion' ? response.promise : []))
  void completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
  editor.change('List')
  editor.setModel(new FakeModel('another'))
  await vi.advanceTimersByTimeAsync(150)
  expect(editor.triggers).toEqual([])
  void completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
  editor.change('newer')
  cleanup()
  await vi.advanceTimersByTimeAsync(150)
  expect(editor.triggers).toEqual([])
  response.resolve([])
})

it.each(['escape', 'cursor'])('does not reopen dismissed suggestions after %s', async kind => {
  const response = deferred<any>()
  const { editor } = setup(vi.fn(async (m: any) => m.operation === 'completion' ? response.promise : []))
  void completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
  editor.change('List')
  if (kind === 'escape') editor.key(9)
  else editor.move({ lineNumber: 1, column: 2 })
  await vi.advanceTimersByTimeAsync(150)
  expect(editor.triggers).toEqual([])
  response.resolve([])
})

describe('Library workspace language services', () => {
  function librarySetup(handler: (r: any) => any) {
    let workspaceVersion = '1'
    let sourceGeneration: string | undefined
    const context = () => ({ id: 'main', source: 'Lis', workspaceVersion, generation: 'saved', sources: [{ id: 'helper', source: 'unsaved helper' }] })
    const callWS = vi.fn(async (message: any) => {
      const r = message.request
      return { version: 1, ok: true, result: { id: r.id, workspaceVersion: r.workspaceVersion, generation: r.generation, sourceGeneration: `source-${r.workspaceVersion}`, saved: false, data: await handler(r) } }
    })
    const editor = new FakeEditor({ value: 'Lis' })
    let actions!: ReturnType<typeof useEditorActions>
    createRoot(dispose => { cleanup = dispose; actions = useEditorActions({ callWS } as any, { context, sourceGeneration: () => sourceGeneration }); actions.register(editor as any, vi.fn()) })
    return { editor, actions, callWS, setSourceGeneration: (value: string) => { sourceGeneration = value }, changeWorkspace: () => { workspaceVersion = '2'; actions.invalidateContext() } }
  }
  it('uses actual library transport for member edits, nullable signatures and cross-file definitions', async () => {
    const { signatures, definitions, formats } = await import('./fake-monaco')
    const { editor, callWS } = librarySetup(r => r.operation === 'completion' ? [completion] : r.operation === 'signature' ? { signatures: [{ label: 'Make(string? label, int count = 1)', documentation: '<summary>Factory</summary>', parameters: ['string? label', 'int count = 1'] }], activeSignature: 0, activeParameter: 1 }
      : r.operation === 'definition' ? [{ id: 'helper', range: { start: 2, length: 4, startLine: 1, startColumn: 3, endLine: 1, endColumn: 7 } }] : r.operation === 'format' ? 'formatted' : { diagnostics: [], valid: true })
    const result = await completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
    expect(result.suggestions[0].insertText).toBe('List')
    expect(callWS.mock.calls.at(-1)![0]).toMatchObject({ type: 'hass_sharp/node_library', request: { operation: 'completion', id: 'main', sources: [{ id: 'helper', source: 'unsaved helper' }] } })
    const signature = await signatures[0].provideSignatureHelp(editor.model, position, noCancellation, {})
    expect(signature.value.signatures[0].documentation.value).toBe('Factory'); expect(signature.value.activeParameter).toBe(1); expect(signature.value.signatures[0].parameters[1].label).toBe('int count = 1')
    expect(definitions).toHaveLength(1); expect(formats).toHaveLength(1)
  })
  it('keeps the names that references in signature documentation point at', async () => {
    const { signatures } = await import('./fake-monaco')
    const documentation = '<summary>Turn on light.</summary><param name="target">An entity id, or a <see cref="T:global::HassSharp.ServiceTarget"/> for areas; <see langword="null"/> for none. See <paramref name="target"/>.</param>'
    const { editor } = librarySetup(r => r.operation === 'signature' ? { signatures: [{ label: 'TurnOn(ServiceTarget? target)', documentation, parameters: ['ServiceTarget? target'] }], activeSignature: 0, activeParameter: 0 } : { diagnostics: [], valid: true })
    const signature = await signatures[0].provideSignatureHelp(editor.model, position, noCancellation, {})
    expect(signature.value.signatures[0].documentation.value).toBe('Turn on light.\n\ntarget: An entity id, or a ServiceTarget for areas; null for none. See target.')
  })
  it('maps source-based hover ranges to Monaco without exposing XML signature markup', async () => {
    const { editor } = librarySetup(r => r.operation === 'hover' ? { text: 'string State', range: { start: 0, length: 3, startLine: 1, startColumn: 1, endLine: 1, endColumn: 4 } } : { diagnostics: [], valid: true })
    const hover = await hovers[0].provideHover(editor.model, position, noCancellation)
    expect(hover.range).toMatchObject({ startLineNumber: 1, startColumn: 1, endColumn: 4 })
    expect(hover.contents[0].value).toBe('string State')
  })
  it('rejects a response whose source fingerprint differs from the current preview', async () => {
    const { editor, setSourceGeneration } = librarySetup(r => r.operation === 'completion' ? [completion] : { diagnostics: [], valid: true })
    setSourceGeneration('current-preview')
    const result = await completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
    expect(result.suggestions).toEqual([])
  })
  it('settles every provider when other-file context changes without changing selected model version', async () => {
    const { signatures, definitions, formats } = await import('./fake-monaco')
    const reply = deferred<any>()
    const { editor, changeWorkspace } = librarySetup(() => reply.promise)
    const requests = [completions[0].provideCompletionItems(editor.model, position, {}, noCancellation), hovers[0].provideHover(editor.model, position, noCancellation), signatures[0].provideSignatureHelp(editor.model, position, noCancellation, {}), definitions[0].provideDefinition(editor.model, position, noCancellation), formats[0].provideDocumentFormattingEdits(editor.model, {}, noCancellation)]
    changeWorkspace()
    expect(await Promise.all(requests)).toEqual([{ suggestions: [] }, null, null, null, []])
    reply.resolve([])
    cleanup(); expect(signatures).toHaveLength(0); expect(definitions).toHaveLength(0); expect(formats).toHaveLength(0)
  })
})
