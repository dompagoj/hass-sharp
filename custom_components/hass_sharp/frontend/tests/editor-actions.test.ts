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
function setup(callWS = vi.fn(async () => [] as any), onSave = vi.fn(async () => {})) {
  const editor = new FakeEditor({ value: 'Lis' })
  let actions!: ReturnType<typeof useEditorActions>
  createRoot(dispose => { cleanup = dispose; actions = useEditorActions({ callWS } as any); actions.register(editor as any, onSave) })
  return { editor, actions, onSave, callWS }
}
beforeEach(() => { resetHost(); vi.useFakeTimers() })
afterEach(() => { cleanup?.(); vi.useRealTimers() })
async function flush() { await vi.advanceTimersByTimeAsync(600) }

describe('Monaco requests', () => {
  it('inserts the Roslyn edit instead of the decorated display label', async () => {
    const { editor } = setup(vi.fn(async (m: any) => m.type.endsWith('get_completions') ? [completion] : []))
    const request = completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
    await flush()
    const result = await request
    expect(result.suggestions[0].label).toBe('List<>')
    expect(result.suggestions[0].insertText).toBe('List')
    expect(result.suggestions[0].range).toEqual({ startLineNumber: 1, endLineNumber: 1, startColumn: 1, endColumn: 4 })
  })
  it('preserves method completion kind zero', async () => {
    const { editor } = setup(vi.fn(async (m: any) => m.type.endsWith('get_completions') ? [{ ...completion, tags: ['Method'] }] : []))
    const result = await completions[0].provideCompletionItems(editor.model, position, {}, noCancellation)
    expect(result.suggestions[0].kind).toBe(0)
  })
  it('maps primary and additional Roslyn edits against the same multiline source', async () => {
    const item = { ...completion, textEdit: { start: 5, length: 3, newText: 'List' }, additionalTextEdits: [{ start: 0, length: 0, newText: 'using Example;\n' }] }
    const { editor } = setup(vi.fn(async (m: any) => m.type.endsWith('get_completions') ? [item] : []))
    editor.change('// x\nLis')
    const result = await completions[0].provideCompletionItems(editor.model, { lineNumber: 2, column: 4 }, {}, noCancellation)
    expect(result.suggestions[0].range).toEqual({ startLineNumber: 2, startColumn: 1, endLineNumber: 2, endColumn: 4 })
    expect(result.suggestions[0].additionalTextEdits).toEqual([{ range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 }, text: 'using Example;\n' }])
  })
  it('settles superseded completion requests and ignores their late responses', async () => {
    const response = deferred<any>()
    const { editor } = setup(vi.fn(async (m: any) => m.type.endsWith('get_completions') ? response.promise : []))
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
    const { editor } = setup(vi.fn(async (m: any) => m.type.endsWith('get_completions') ? response.promise : []))
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
    const { editor } = setup(vi.fn(async (m: any) => m.type.endsWith('get_hover') ? hover.promise : diagnostics.promise))
    const result = hovers[0].provideHover(editor.model, position, noCancellation)
    editor.change('valid')
    diagnostics.resolve([diagnostic]); hover.resolve('old symbol')
    await vi.advanceTimersByTimeAsync(0)
    expect(await result).toBeNull()
    expect(markers.get(editor.model.uri.toString()) ?? []).toEqual([])
  })
  it('checks the version when consuming an already delivered completion', async () => {
    const response = deferred<any>()
    const { editor } = setup(vi.fn((m: any) => m.type.endsWith('get_completions') ? response.promise : Promise.resolve([])))
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
    const { editor, actions } = setup(vi.fn(async (m: any) => m.type.endsWith('format_source') ? 'formatted' : []), vi.fn(async (model: any) => { saved.push(model.getValue()) }))
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
    const { editor, actions, onSave, callWS } = setup(vi.fn(async (m: any) => m.type.endsWith('format_source') ? format.promise : []))
    await flush()
    const saving = actions.save()
    await vi.advanceTimersByTimeAsync(0)
    expect(callWS.mock.calls.some(([m]) => m.type.endsWith('format_source'))).toBe(true)
    editor.change('newer edit')
    format.resolve('stale format'); await saving
    expect(editor.model.getValue()).toBe('newer edit')
    expect(onSave).not.toHaveBeenCalled()
  })
  it('does not apply formatting delivered just before a model edit', async () => {
    const format = deferred<any>()
    const { editor, actions, onSave } = setup(vi.fn((m: any) => m.type.endsWith('format_source') ? format.promise : Promise.resolve([])))
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
    const { editor, actions, onSave, callWS } = setup(vi.fn(async (m: any) => m.type.endsWith('format_source') ? format.promise : []))
    await flush()
    markers.set('other-model', [{ severity: 8 }])
    markers.set(editor.model.uri.toString(), [{ severity: 4 }])
    const first = actions.save(); const second = actions.save()
    format.resolve('formatted'); await Promise.all([first, second])
    expect(onSave).toHaveBeenCalledTimes(1)
    expect(callWS.mock.calls.filter(([m]) => m.type.endsWith('format_source'))).toHaveLength(1)
  })
  it('validates the formatted revision before persisting', async () => {
    const { actions, onSave } = setup(vi.fn(async (m: any) => m.type.endsWith('format_source') ? 'broken' : m.source === 'broken' ? [diagnostic] : []))
    await flush(); await actions.save()
    expect(onSave).not.toHaveBeenCalled()
  })
})
