import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { NodeLibraryEditor, NodeLibraryView } from '../src/views/NodeLibraryView'
import { createMemoryHistory, MemoryRouter, Route } from '@solidjs/router'
import { editors, resetHost, definitions, completions, openers } from './fake-monaco'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
vi.mock('monaco-editor', async () => (await import('./fake-monaco')).host)
vi.mock('monaco-vim', () => ({ initVimMode: () => ({ dispose() {} }) }))
let dispose: () => void
let host: HTMLDivElement
let requests: any[]
/** Answers `hass_sharp/external`'s requests, which are recorded in `requests` too. */
let external: (request: any) => unknown = () => { throw new Error('No external answer') }
const documents = () => [{ version: 1, id: 'shared', name: 'Shared.cs', source: rich.source, revision: 1 }, { version: 1, id: 'helper', name: 'Helper.cs', source: 'public class Helper {}', revision: 1 }]
function mount(invalid = false, response?: (request: any, result: any) => any) {
  requests = []
  const callWS = async (message: any) => {
    const r = message.request; requests.push(r)
    if (message.type === 'hass_sharp/external') return external(r)
    const result = r.operation === 'list' ? { documents: documents(), generation: 'g1' }
      : r.operation === 'catalog' ? { catalog: rich.catalog, generation: 'g1', saved: true, valid: true, diagnostics: [] }
      : r.operation === 'save' ? { saved: true, document: { ...documents()[0], id: r.id, name: r.name, source: r.source, revision: 2 }, generation: 'g2', catalog: rich.catalog, automations: [] }
      : { id: r.id, workspaceVersion: r.workspaceVersion, generation: r.generation, sourceGeneration: `source-${r.workspaceVersion}`, saved: false,
          data: r.operation === 'preview' ? { valid: !invalid, catalog: rich.catalog, diagnostics: invalid ? [{ sourceId: 'helper', startLine: 1, startColumn: 3, endLine: 1, endColumn: 8, severity: 3, message: 'Helper error' }] : [] }
          : r.operation === 'format' ? r.source : r.operation === 'definition' ? [{ id: 'helper', range: { start: 13, length: 6, startLine: 1, startColumn: 14, endLine: 1, endColumn: 20 } }]
          : r.operation === 'completion' ? { items: [{ displayText: 'Reading', displayTextPrefix: '', displayTextSuffix: '', filterText: 'Reading', tags: ['Class'], textEdit: { start: 1, length: 0, newText: 'Reading' }, additionalTextEdits: [] }], incomplete: false } : [] }
    const answer = await response?.(r, result)
    // A response may give the whole envelope, to answer with an error.
    return answer?.envelope ?? { version: 1, ok: true, result: answer ?? result }
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
  // What the intro says holds for both kinds of card: steps run through execution connections, value nodes when read.
  expect(host.textContent).toContain('A step node runs when an execution connection reaches it; a value node runs when a step reads its output.')
  expect(host.textContent).not.toContain('Methods run only through execution connections')
  expect(requests.every(r => !['run', 'execute'].includes(r.operation))).toBe(true)
  // The entity refresh command was removed with typed entities: no button, no request.
  expect(host.textContent).not.toContain('Refresh entity IDs')
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
it('keeps a completion list in flight while typing goes on, though each edit advances the library workspace', async () => {
  // Typing on after the position asked about advances the workspace (and the file's source in it); the list is still for
  // the code before that position, and Monaco filters it by what was typed meanwhile.
  mount(); await vi.advanceTimersByTimeAsync(900)
  const editor = editors[0]
  const typed = editor.model.getValue()
  const result = completions[0].provideCompletionItems(editor.model, { lineNumber: 1, column: 2 }, {}, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) })
  editor.change(typed.slice(0, 1) + 'R' + typed.slice(1))
  await vi.advanceTimersByTimeAsync(150)
  expect((await result).suggestions.map((s: any) => s.label)).toEqual(['Reading'])
  expect(editor.triggers).toEqual([])
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
it('previews a pure library node as a value node, without step-order pins', async () => {
  const pure = structuredClone(rich.catalog) as any
  pure.nodes['library:shared:global%3A%3ANodeLib.Samples.Describe'].pure = true
  mount(false, (r, result) => r.operation === 'preview' || r.operation === 'catalog' ? (r.operation === 'catalog' ? { ...result, catalog: pure } : { ...result, data: { ...result.data, catalog: pure } }) : result)
  await vi.advanceTimersByTimeAsync(900)
  const card = (label: string) => [...host.querySelectorAll('[aria-label="Node previews"] article')].find(a => a.querySelector('strong')?.textContent === label)!
  expect(card('Describe').textContent).not.toContain('Execution'); expect(card('Describe').textContent).toContain('Value node')
  expect(card('Consume').textContent).toContain('Execution')
})
it('names each automation a library save re-activated by its name, not its id', async () => {
  const automations = [{ id: 'visual-1f2e', saved: true, document: { id: 'visual-1f2e', revision: 3, graph: { version: 5, name: 'Porch lights', nodes: [], edges: [] } }, active: true, activationError: null },
    { id: 'visual-9a8b', saved: true, document: { id: 'visual-9a8b', revision: 2, graph: { version: 5, name: 'Night mode', nodes: [], edges: [] } }, active: false, activationError: 'boom' }]
  mount(false, (r, result) => r.operation === 'save' ? { ...result, automations } : result); await vi.advanceTimersByTimeAsync(900)
  editors[0].change('public class Updated {}'); await vi.advanceTimersByTimeAsync(900)
  host.querySelector<HTMLButtonElement>('[data-save]')!.click(); await vi.advanceTimersByTimeAsync(0)
  const text = host.querySelector('[aria-label="Dependent automations"]')!.textContent!
  expect(text).toContain('Porch lights: Saved and active'); expect(text).toContain('Night mode: Saved; activation failed: boom'); expect(text).not.toContain('visual-1f2e')
})
it('names the automation a refused library save would break, and drops the previous save’s results', async () => {
  const activations = [{ id: 'visual-1f2e', saved: true, document: { id: 'visual-1f2e', revision: 3, graph: { version: 5, name: 'Porch lights', nodes: [], edges: [] } }, active: true, activationError: null }]
  let refuse = false
  const refusal = { version: 1, ok: false, error: { code: 'validation', detail: { diagnostics: [], automations: [{ id: 'visual-9a8b', name: 'Night mode', diagnostics: [{ nodeId: 'up', inputId: 'lamp', step: 'Turn on lamp', message: 'Lamp is required', severity: 3 }] }], invalid: [] } } }
  mount(false, (r, result) => r.operation !== 'save' ? result : refuse ? { envelope: refusal } : { ...result, automations: activations })
  await vi.advanceTimersByTimeAsync(900)
  editors[0].change('public class Updated {}'); await vi.advanceTimersByTimeAsync(900)
  host.querySelector<HTMLButtonElement>('[data-save]')!.click(); await vi.advanceTimersByTimeAsync(0)
  expect(host.textContent).toContain('Porch lights: Saved and active')
  refuse = true
  editors[0].change('public class Renamed {}'); await vi.advanceTimersByTimeAsync(900)
  host.querySelector<HTMLButtonElement>('[data-save]')!.click(); await vi.advanceTimersByTimeAsync(0)
  expect(host.querySelector('[role="alert"]')?.textContent).toContain('Night mode')
  // The step by its title, as the editor names it, never by its id or the input's.
  expect(host.querySelector('[aria-label="Library workspace diagnostics"]')!.textContent).toContain('Error · Night mode · Turn on lamp: Lamp is required')
  expect(host.querySelector('[aria-label="Library workspace diagnostics"]')!.textContent).not.toContain('up · lamp')
  expect(host.textContent).not.toContain('Porch lights: Saved and active')
})
it('drops the previous save’s results when a delete starts, and names what a refused delete would break as a delete', async () => {
  const activations = [{ id: 'visual-1f2e', saved: true, document: { id: 'visual-1f2e', revision: 3, graph: { version: 5, name: 'Porch lights', nodes: [], edges: [] } }, active: true, activationError: null }]
  const refusal = { version: 1, ok: false, error: { code: 'validation', detail: { diagnostics: [], automations: [{ id: 'visual-9a8b', name: 'Night mode', diagnostics: [] }], invalid: [] } } }
  mount(false, (r, result) => r.operation === 'delete' ? { envelope: refusal } : r.operation === 'save' ? { ...result, automations: activations } : result)
  await vi.advanceTimersByTimeAsync(900)
  editors[0].change('public class Updated {}'); await vi.advanceTimersByTimeAsync(900)
  host.querySelector<HTMLButtonElement>('[data-save]')!.click(); await vi.advanceTimersByTimeAsync(0)
  expect(host.textContent).toContain('Porch lights: Saved and active')
  button('Delete file').click()
  ;[...document.querySelectorAll<HTMLButtonElement>('dialog[open] button')].find(b => b.textContent === 'Delete file')!.click()
  await vi.advanceTimersByTimeAsync(0)
  expect(requests.some(r => r.operation === 'delete')).toBe(true)
  const alert = host.querySelector('[role="alert"]')?.textContent ?? ''
  expect(alert).toContain('Deleting this library would break Night mode')
  expect(alert).not.toContain('Saving')
  expect(host.textContent).not.toContain('Porch lights: Saved and active')
})
it('says which automations a library save left as they were, because they already had errors', async () => {
  const invalid = [{ id: 'visual-77aa', name: 'Old heating', running: false, message: 'Not rebuilt, because it has errors; it is not running', diagnostics: [] }]
  mount(false, (r, result) => r.operation === 'save' ? { ...result, invalid } : result); await vi.advanceTimersByTimeAsync(900)
  editors[0].change('public class Updated {}'); await vi.advanceTimersByTimeAsync(900)
  host.querySelector<HTMLButtonElement>('[data-save]')!.click(); await vi.advanceTimersByTimeAsync(0)
  expect(host.querySelector('[aria-label="Dependent automations"]')!.textContent).toContain('Old heating: Not rebuilt, because it has errors; it is not running')
})

// "Open in library" from a type's definition: `?file=<id>&line=<n>` opens that file at that line once, then leaves the
// address (replaced, not a new history entry), so a reload does not jump back there.
function mountAt(path: string) {
  requests = []
  const callWS = async (message: any) => {
    const r = message.request; requests.push(r)
    const result = r.operation === 'list' ? { documents: documents(), generation: 'g1' }
      : r.operation === 'catalog' ? { catalog: rich.catalog, generation: 'g1', saved: true, valid: true, diagnostics: [] }
      : { id: r.id, workspaceVersion: r.workspaceVersion, generation: r.generation, sourceGeneration: `source-${r.workspaceVersion}`, saved: false, data: r.operation === 'preview' ? { valid: true, catalog: rich.catalog, diagnostics: [] } : [] }
    return { version: 1, ok: true, result }
  }
  const history = createMemoryHistory()
  history.set({ value: '/automations' }); history.set({ value: path })
  dispose = render(() => <QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={{ callWS, user: { is_admin: true } } as any}>
    <MemoryRouter history={history}><Route path="/libraries" component={NodeLibraryView} /><Route path="/automations" component={() => <p>Automations</p>} /></MemoryRouter>
  </HassCtx.Provider></QueryClientProvider>, host)
  return history
}
it('opens the file and line an address names, once, and then clears them from the address', async () => {
  const history = mountAt('/libraries?file=helper&line=1')
  await vi.advanceTimersByTimeAsync(900)
  expect(editors[0].getModel().getValue()).toBe('public class Helper {}')
  expect(editors[0].selection).toMatchObject({ startLineNumber: 1, endLineNumber: 1 })
  expect(history.get()).toBe('/libraries')
  // Replaced: Back goes to the page before, not to the address with the file.
  history.back()
  expect(history.get()).toBe('/automations')
})
it('says when the file an address names is not there, and clears the address', async () => {
  const history = mountAt('/libraries?file=gone&line=3')
  await vi.advanceTimersByTimeAsync(900)
  expect(host.querySelector('.hs-notice')?.textContent).toBe('The library file to open is not there; it may have been renamed or deleted.')
  expect(editors[0].getModel().getValue()).toBe(rich.source)
  expect(history.get()).toBe('/libraries')
})

it('counts what a refused save would break as an error, and lists errors first, marked as the others are', async () => {
  const warnings = Array.from({ length: 3 }, (_, i) => ({ sourceId: 'shared', startLine: i + 1, startColumn: 1, endLine: i + 1, endColumn: 2, severity: 2, message: `Missing XML comment ${i}` }))
  const refusal = { version: 1, ok: false, error: { code: 'validation', detail: { diagnostics: warnings, automations: [{ id: 'visual-15ee', name: 'Smoke Select property', diagnostics: [
    { nodeId: 'd26fa2a6-6669-46f5-ab5e-242c40ccd69d', inputId: 'property', step: 'Select property', message: 'User has no property UserName; choose another', severity: 3 },
    { nodeId: null, message: 'The graph has no trigger', severity: 3 }] }], invalid: [] } } }
  mount(false, (r, result) => r.operation === 'save' ? { envelope: refusal } : result); await vi.advanceTimersByTimeAsync(900)
  editors[0].change('public class Renamed {}'); await vi.advanceTimersByTimeAsync(900)
  host.querySelector<HTMLButtonElement>('[data-save]')!.click(); await vi.advanceTimersByTimeAsync(0)
  const section = host.querySelector('[aria-label="Library workspace diagnostics"]')!
  expect(section.querySelector('p')!.textContent).toBe('2 errors · 3 warnings')
  const entries = [...section.querySelectorAll('li')].map(li => li.textContent)
  expect(entries).toHaveLength(5)
  expect(entries.slice(0, 2)).toEqual(['Error · Smoke Select property · Select property: User has no property UserName; choose another', 'Error · Smoke Select property: The graph has no trigger'])
  expect(entries.slice(2).every(text => text!.startsWith('Warning · Shared.cs'))).toBe(true)
  expect(section.textContent).not.toContain('d26fa2a6')
})

it('counts one error and one warning in the workspace header in the singular', async () => {
  const refusal = { version: 1, ok: false, error: { code: 'validation', detail: {
    diagnostics: [{ sourceId: 'shared', startLine: 1, startColumn: 1, endLine: 1, endColumn: 2, severity: 2, message: 'Missing XML comment' }],
    automations: [{ id: 'visual-15ee', name: 'Smoke Select property', diagnostics: [{ nodeId: null, message: 'The graph has no trigger', severity: 3 }] }], invalid: [] } } }
  mount(false, (r, result) => r.operation === 'save' ? { envelope: refusal } : result); await vi.advanceTimersByTimeAsync(900)
  editors[0].change('public class Renamed {}'); await vi.advanceTimersByTimeAsync(900)
  host.querySelector<HTMLButtonElement>('[data-save]')!.click(); await vi.advanceTimersByTimeAsync(0)
  expect(host.querySelector('[aria-label="Library workspace diagnostics"] p')!.textContent).toBe('1 error · 1 warning')
})

// What a save did to the entity definitions that use the libraries, beside the automations, each linked to its page.
const definitionAnswers = {
  definitions: [
    { id: 'plug', saved: true, active: true, activationError: null, document: { version: 1, id: 'plug', name: 'Plug', revision: 3, settings: [{ key: 'password', type: 'string', secret: true, value: null, hasValue: true }] } },
    { id: 'lamp', saved: true, active: false, activationError: 'Lamp could not start', enabled: true, document: { version: 1, id: 'lamp', name: 'Lamp', revision: 2, settings: [] } },
  ],
  invalidDefinitions: [{ id: 'old_sensor', name: 'Old sensor', running: true, diagnostics: [], message: 'Not rebuilt, because the definition has errors: it is still running the previous version, with the previous library code' }],
}
function mountRouted(response: (request: any) => any) {
  requests = []
  const callWS = async (message: any) => {
    const r = message.request; requests.push(r)
    const answer = response(r)
    if (answer) return answer.envelope ?? { version: 1, ok: true, result: answer }
    const result = r.operation === 'list' ? { documents: documents(), generation: 'g1' }
      : r.operation === 'catalog' ? { catalog: rich.catalog, generation: 'g1', saved: true, valid: true, diagnostics: [] }
      : { id: r.id, workspaceVersion: r.workspaceVersion, generation: r.generation, sourceGeneration: `source-${r.workspaceVersion}`, saved: false, data: r.operation === 'preview' ? { valid: true, catalog: rich.catalog, diagnostics: [] } : [] }
    return { version: 1, ok: true, result }
  }
  const history = createMemoryHistory()
  history.set({ value: '/libraries' })
  dispose = render(() => <QueryClientProvider client={new QueryClient()}><HassCtx.Provider value={{ callWS, user: { is_admin: true } } as any}>
    <MemoryRouter history={history}><Route path="/libraries" component={NodeLibraryView} /><Route path="/entities/:id" component={() => <p class="definition-page">Definition</p>} /></MemoryRouter>
  </HassCtx.Provider></QueryClientProvider>, host)
  return history
}
const saveEdit = async () => {
  editors[0].change('public class Updated {}'); await vi.advanceTimersByTimeAsync(900)
  host.querySelector<HTMLButtonElement>('[data-save]')!.click(); await vi.advanceTimersByTimeAsync(0)
}
it('says how many entity definitions a library save rebuilt, and names the ones that failed or were left as they were, each linked', async () => {
  const history = mountRouted(r => r.operation === 'save' ? { saved: true, document: { ...documents()[0], source: r.source, revision: 2 }, generation: 'g2', catalog: rich.catalog, automations: [], invalid: [], ...definitionAnswers } : undefined)
  await vi.advanceTimersByTimeAsync(900)
  await saveEdit()
  const section = host.querySelector('[aria-label="Dependent entity definitions"]')!
  expect(section).not.toBeNull()
  const lines = [...section.querySelectorAll('p')].map(p => p.textContent)
  expect(lines).toEqual([
    'Rebuilt 2 entity definitions',
    'Lamp: Saved; activation failed: Lamp could not start',
    'Old sensor: Not rebuilt, because the definition has errors: it is still running the previous version, with the previous library code',
  ])
  expect([...section.querySelectorAll('a')].map(a => [a.textContent, a.getAttribute('href')])).toEqual([['Lamp', '/entities/lamp'], ['Old sensor', '/entities/old_sensor']])
  // No automation was rebuilt: nothing is said of automations.
  expect(host.querySelector('[aria-label="Dependent automations"]')).toBeNull()
  section.querySelector<HTMLAnchorElement>('a')!.click()
  await vi.advanceTimersByTimeAsync(0)
  expect(history.get()).toBe('/entities/lamp')
})
it('counts one entity definition rebuilt in the singular', async () => {
  mountRouted(r => r.operation === 'save' ? { saved: true, document: { ...documents()[0], source: r.source, revision: 2 }, generation: 'g2', catalog: rich.catalog, automations: [], definitions: definitionAnswers.definitions.slice(0, 1), invalidDefinitions: [] } : undefined)
  await vi.advanceTimersByTimeAsync(900)
  await saveEdit()
  expect(host.querySelector('[aria-label="Dependent entity definitions"]')!.textContent).toBe('Rebuilt 1 entity definition')
})
it('says that a library change touches the entity definitions too, and words a definition that did not start with no reason plainly', async () => {
  const quiet = { ...definitionAnswers.definitions[1], activationError: null }
  mountRouted(r => r.operation === 'save' ? { saved: true, document: { ...documents()[0], source: r.source, revision: 2 }, generation: 'g2', catalog: rich.catalog, automations: [], definitions: [quiet], invalidDefinitions: [] } : undefined)
  await vi.advanceTimersByTimeAsync(900)
  expect(host.querySelector('.hs-library-persistence')!.textContent).toBe('Saving checks every saved visual automation and entity definition, and rebuilds each at its next revision. '
    + 'Those that are on run the rebuilt version. Running invocations keep their prepared source snapshot. Unsaved files are used together for previews; save each file to publish it.')
  button('Delete file').click()
  await vi.advanceTimersByTimeAsync(0)
  expect(document.querySelector('dialog[open]')!.textContent).toContain('Delete this file and discard its draft. Saved automations and entity definitions that use it must still compile without it, or it is not deleted.')
  ;[...document.querySelectorAll<HTMLButtonElement>('dialog[open] button')].find(b => b.textContent === 'Keep editing')!.click()
  await saveEdit()
  const lines = [...host.querySelector('[aria-label="Dependent entity definitions"]')!.querySelectorAll('p')].map(p => p.textContent)
  expect(lines).toEqual(['Rebuilt 1 entity definition', 'Lamp: Saved, but it is not running'])
  expect(host.textContent).not.toContain('current revision is not active')
})
it('counts the rebuilt entity definitions that are off, and says nothing else of them', async () => {
  const off = (id: string, name: string) => ({ id, saved: true, active: false, activationError: null, enabled: false, document: { version: 1, id, name, revision: 4, settings: [] } })
  mountRouted(r => r.operation === 'save' ? { saved: true, document: { ...documents()[0], source: r.source, revision: 2 }, generation: 'g2', catalog: rich.catalog, automations: [],
    definitions: [...definitionAnswers.definitions, off('heater', 'Heater'), off('fan', 'Fan')], invalidDefinitions: [] } : undefined)
  await vi.advanceTimersByTimeAsync(900)
  await saveEdit()
  const lines = [...host.querySelector('[aria-label="Dependent entity definitions"]')!.querySelectorAll('p')].map(p => p.textContent)
  expect(lines).toEqual(['Rebuilt 4 entity definitions (2 are off)', 'Lamp: Saved; activation failed: Lamp could not start'])
})
it('counts one rebuilt entity definition that is off in the singular', async () => {
  const heater = { id: 'heater', saved: true, active: false, activationError: null, enabled: false, document: { version: 1, id: 'heater', name: 'Heater', revision: 4, settings: [] } }
  mountRouted(r => r.operation === 'save' ? { saved: true, document: { ...documents()[0], source: r.source, revision: 2 }, generation: 'g2', catalog: rich.catalog, automations: [], definitions: [heater], invalidDefinitions: [] } : undefined)
  await vi.advanceTimersByTimeAsync(900)
  await saveEdit()
  expect(host.querySelector('[aria-label="Dependent entity definitions"]')!.textContent).toBe('Rebuilt 1 entity definition (1 is off)')
})
it('lists the entity definitions a refused save would break under the refusal, linked, and counts their problems', async () => {
  const refusal = { version: 1, ok: false, error: { code: 'validation', detail: { diagnostics: [], automations: [], invalid: [],
    definitions: [{ id: 'plug', name: 'Plug', diagnostics: [{ nodeId: 'on', inputId: null, message: 'Relay has no command toggle', severity: 3 }] }] } } }
  mountRouted(r => r.operation === 'save' ? { envelope: refusal } : undefined)
  await vi.advanceTimersByTimeAsync(900)
  await saveEdit()
  expect(host.querySelector('[role="alert"]')!.textContent).toContain('Saving would break Plug. Fix the library or that entity definition first')
  const blocked = host.querySelector('[aria-label="Entity definitions this would break"]')!
  expect(blocked.textContent).toBe('Plug: Relay has no command toggle')
  expect(blocked.querySelector('a')!.getAttribute('href')).toBe('/entities/plug')
  const diagnostics = host.querySelector('[aria-label="Library workspace diagnostics"]')!
  expect(diagnostics.querySelector('p')!.textContent).toBe('1 error · 0 warnings')
  expect([...diagnostics.querySelectorAll('li')].map(li => li.textContent)).toEqual(['Error · Plug: Relay has no command toggle'])
})
it('names the entity definitions without links where the page has no router', async () => {
  mount(false, (r, result) => r.operation === 'save' ? { ...result, ...definitionAnswers } : result); await vi.advanceTimersByTimeAsync(900)
  await saveEdit()
  const section = host.querySelector('[aria-label="Dependent entity definitions"]')!
  expect(section.textContent).toContain('Lamp: Saved; activation failed: Lamp could not start')
  expect(section.querySelector('a')).toBeNull()
  expect(host.innerHTML).not.toContain('hasValue')
})

it('imports from a GitHub repository with Add external, and marks a draft whose file the import overwrote', async () => {
  const sha = '0123456789abcdef0123456789abcdef01234567'
  let overwritten = false
  external = r => ({ version: 1, ok: true, result: r.operation === 'inspect'
    ? { owner: 'o', repo: 'r', ref: null, sha, items: [{ kind: 'library', name: 'Shared.cs', description: 'Shared nodes.', path: 'Shared.cs', existing: [{ id: 'shared', revision: 1, name: 'Shared.cs' }], problem: null }] }
    : (overwritten = true, { owner: 'o', repo: 'r', sha, items: [{ kind: 'library', name: 'Shared.cs', path: 'Shared.cs', outcome: 'overwritten', id: 'shared', enabled: null, message: null }],
      libraries: { saved: true, documents: [], generation: 'g2', catalog: rich.catalog, automations: [] } }) })
  // Once imported, the list has the overwritten file at its next revision.
  mount(false, (r, result) => r.operation === 'list' && overwritten
    ? { ...result, generation: 'g2', documents: result.documents.map((d: any) => d.id === 'shared' ? { ...d, revision: 2, source: 'public class Imported {}' } : d) } : result)
  await vi.advanceTimersByTimeAsync(900)
  editors[0].change('draft main'); await vi.advanceTimersByTimeAsync(900)
  button('Add external').click(); await vi.advanceTimersByTimeAsync(0)
  const dialog = [...host.querySelectorAll('dialog')].find(d => d.open)!
  expect(dialog.querySelector('h2')?.textContent).toBe('Add external')
  const url = [...dialog.querySelectorAll('label')].find(l => l.textContent === 'Repository URL')!.control as HTMLInputElement
  url.value = 'https://github.com/o/r'; url.dispatchEvent(new Event('input', { bubbles: true }))
  button('Inspect').click(); await vi.advanceTimersByTimeAsync(0)
  const box = [...dialog.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')].find(b => b.labels?.[0]?.textContent?.trim() === 'Shared.cs')!
  expect(box.checked).toBe(false)
  box.click(); await vi.advanceTimersByTimeAsync(0)
  const lists = requests.filter(r => r.operation === 'list').length
  button('Import').click(); await vi.advanceTimersByTimeAsync(900)
  expect(requests.find(r => r.operation === 'import')?.picks).toEqual([{ kind: 'library', path: 'Shared.cs', mode: 'overwrite', target: 'shared', expectedRevision: 1 }])
  expect(dialog.textContent).toContain('Shared.cs: overwritten')
  expect(requests.filter(r => r.operation === 'list').length).toBeGreaterThan(lists)
  // As after a save made elsewhere: the draft is kept, and its file is marked.
  const file = [...host.querySelectorAll('[aria-label="Library files"] button')].find(b => b.querySelector('span')?.textContent === 'Shared.cs')!
  expect(file.querySelector('[aria-label="Revision conflict"]')).not.toBeNull()
  expect(editors[0].getModel().getValue()).toBe('draft main')
})
