import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { NodeLibraryEditor } from '../src/views/NodeLibraryView'
import { editors, resetHost, definitions, completions, openers } from './fake-monaco'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
vi.mock('monaco-editor', async () => (await import('./fake-monaco')).host)
vi.mock('monaco-vim', () => ({ initVimMode: () => ({ dispose() {} }) }))
let dispose: () => void
let host: HTMLDivElement
let requests: any[]
const documents = () => [{ version: 1, id: 'shared', name: 'Shared.cs', source: rich.source, revision: 1 }, { version: 1, id: 'helper', name: 'Helper.cs', source: 'public class Helper {}', revision: 1 }]
function mount(invalid = false, response?: (request: any, result: any) => any) {
  requests = []
  const callWS = async (message: any) => {
    const r = message.request; requests.push(r)
    const result = r.operation === 'list' ? { documents: documents(), generation: 'g1' }
      : r.operation === 'catalog' ? { catalog: rich.catalog, generation: 'g1', saved: true, valid: true, diagnostics: [] }
      : r.operation === 'save' ? { saved: true, document: { ...documents()[0], id: r.id, name: r.name, source: r.source, revision: 2 }, generation: 'g2', catalog: rich.catalog, automations: [] }
      : { id: r.id, workspaceVersion: r.workspaceVersion, generation: r.generation, sourceGeneration: `source-${r.workspaceVersion}`, saved: false,
          data: r.operation === 'preview' ? { valid: !invalid, catalog: rich.catalog, diagnostics: invalid ? [{ sourceId: 'helper', startLine: 1, startColumn: 3, endLine: 1, endColumn: 8, severity: 3, message: 'Helper error' }] : [] }
          : r.operation === 'format' ? r.source : r.operation === 'definition' ? [{ id: 'helper', range: { start: 13, length: 6, startLine: 1, startColumn: 14, endLine: 1, endColumn: 20 } }] : [] }
    return { version: 1, ok: true, result: await response?.(r, result) ?? result }
  }
  dispose = render(() => <QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={{ callWS, user: { is_admin: true } } as any}><NodeLibraryEditor /></HassCtx.Provider></QueryClientProvider>, host)
}
const button = (label: string) => [...host.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === label || b.querySelector('span')?.textContent === label)!
beforeEach(() => { vi.useFakeTimers(); resetHost(); host = document.createElement('div'); document.body.append(host) })
afterEach(() => { dispose?.(); host.remove(); vi.useRealTimers() })
it('shows source-derived cards, XML docs, actual CLR ports and explicit execution without running code', async () => {
  mount(); await vi.advanceTimersByTimeAsync(900)
  expect(host.textContent).toContain('Read a typed state.'); expect(host.textContent).toContain('A detached snapshot.'); expect(host.textContent).toContain('The reading.')
  expect(host.querySelector('[aria-label="Node previews"]')?.textContent).toContain('Entity state'); expect(host.querySelector('[aria-label="Node previews"]')?.textContent).toContain('Reading'); expect(host.querySelector('[aria-label="Node previews"]')?.textContent).not.toContain('EntityStateSnapshot')
  expect(host.querySelector('[aria-label="Node previews"]')?.textContent).toContain('Execution')
  expect(host.textContent).toContain('factory node'); expect(host.textContent).not.toContain('Pure')
  expect(requests.every(r => !['run', 'execute'].includes(r.operation))).toBe(true)
  const current = editors[0]; button('Full screen').click(); expect(editors[0]).toBe(current); button('Exit full screen').click(); expect(editors[0]).toBe(current)
})
it('preserves all drafts during file switching and definition navigation, focusing the target range', async () => {
  mount(); await vi.advanceTimersByTimeAsync(900)
  const editor = editors[0]; editor.change('draft main'); button('Helper.cs').click(); editor.change('draft helper'); button('Shared.cs').click()
  expect(editor.getModel().getValue()).toBe('draft main')
  const locations = await definitions[0].provideDefinition(editor.model, { lineNumber: 1, column: 2 }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) })
  expect(editor.getModel().getValue()).toBe('draft main') // Ctrl-hover resolves locations without navigating.
  expect(locations[0].uri.toString()).toBe('hass-sharp-library:///helper.cs')
  expect(await openers[0].openCodeEditor(editor, locations[0].uri, locations[0].range)).toBe(true)
  expect(editor.getModel().getValue()).toBe('draft helper'); expect(editor.selection).toMatchObject({ startColumn: 14, endColumn: 20 })
  button('Shared.cs').click(); expect(editor.getModel().getValue()).toBe('draft main')
  await completions[0].provideCompletionItems(editor.model, { lineNumber: 1, column: 2 }, {}, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) })
  expect(requests.at(-1).sources).toContainEqual({ id: 'helper', source: 'draft helper' })
})
it('navigates cross-file compiler errors and blocks saving while the workspace has errors', async () => {
  mount(true); await vi.advanceTimersByTimeAsync(900)
  const issue = [...host.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.includes('Helper error'))!
  expect(host.querySelector<HTMLButtonElement>('[data-save]')!.disabled).toBe(true)
  issue.click(); expect(editors[0].selection).toMatchObject({ startColumn: 3, endColumn: 8 })
  expect(editors[0].getModel().getValue()).toBe('public class Helper {}')
})
it('formats and saves the current file with its original revision, then displays the committed revision', async () => {
  mount(); await vi.advanceTimersByTimeAsync(900)
  editors[0].change('public class Updated {}'); await vi.advanceTimersByTimeAsync(900)
  expect(host.querySelector<HTMLButtonElement>('[data-save]')!.disabled).toBe(false)
  host.querySelector<HTMLButtonElement>('[data-save]')!.click(); await vi.advanceTimersByTimeAsync(0)
  expect(requests.find(r => r.operation === 'save')).toMatchObject({ id: 'shared', expectedRevision: 1, source: 'public class Updated {}' })
  expect(host.textContent).toContain('Revision 2'); expect(host.textContent).toContain('· Saved')
})
it('retains typing recovery when a library edit invalidates the workspace', async () => {
  mount(); await vi.advanceTimersByTimeAsync(900)
  const editor = editors[0]
  const result = completions[0].provideCompletionItems(editor.model, { lineNumber: 1, column: 2 }, {}, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) })
  editor.change('Lis'); editor.move({ lineNumber: 1, column: 4 }, 'keyboard', 0)
  await vi.advanceTimersByTimeAsync(150)
  expect(editor.triggers).toContain('editor.action.triggerSuggest')
  expect((await result).suggestions).toEqual([])
})
it('restores each file cursor and selection when switching back to its draft', async () => {
  mount(); await vi.advanceTimersByTimeAsync(900)
  const editor = editors[0]
  const original = { startLineNumber: 2, startColumn: 2, endLineNumber: 2, endColumn: 8 }
  editor.setSelection(original); button('Helper.cs').click()
  editor.setSelection({ startLineNumber: 1, startColumn: 3, endLineNumber: 1, endColumn: 9 })
  button('Shared.cs').click(); expect(editor.selection).toEqual(original)
})

it('does not save a different file after the final save preview returns across navigation', async () => {
  let savingPreviews = 0, armed = false
  let resolve!: (value: any) => void
  let delayed: any
  mount(false, (request, result) => {
    if (armed && request.operation === 'preview' && request.id === 'shared' && ++savingPreviews === 3) {
      delayed = result
      return new Promise(done => { resolve = done })
    }
  })
  await vi.advanceTimersByTimeAsync(900)
  editors[0].change('draft A'); await vi.advanceTimersByTimeAsync(900)
  armed = true
  host.querySelector<HTMLButtonElement>('[data-save]')!.click(); await vi.advanceTimersByTimeAsync(0)
  expect(delayed).toBeDefined()
  button('Helper.cs').click(); editors[0].change('draft B'); await vi.advanceTimersByTimeAsync(900)
  resolve(delayed); await vi.advanceTimersByTimeAsync(0)
  expect(requests.filter(request => request.operation === 'save')).toEqual([])
  expect(editors[0].getModel().getValue()).toBe('draft B')
  button('Shared.cs').click(); expect(editors[0].getModel().getValue()).toBe('draft A')
})
