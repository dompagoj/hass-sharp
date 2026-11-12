import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createRoot } from 'solid-js'
import { createLibrarySession } from '../src/visual/library-session'
import { libraryApi } from '../src/visual/library-api'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
const doc = (id = 'shared', source = rich.source, revision = 1) => ({ version: 1 as const, id, name: `${id}.cs`, source, revision })
const preview = (r: any) => ({ ...r, saved: false, sourceGeneration: `draft-${r.workspaceVersion}`, data: { valid: true, catalog: rich.catalog, diagnostics: [] } })
let dispose: () => void
let session: ReturnType<typeof createLibrarySession>
function setup(handler: (r: any) => any = () => undefined) {
  const calls: any[] = []
  const callWS = vi.fn(async (message: any) => {
    const r = message.request; calls.push(r)
    const result = await handler(r) ?? (r.operation === 'list' ? { generation: 'g1', documents: [doc(), doc('other', 'public class Other {}')] }
      : r.operation === 'catalog' ? { generation: 'g1', saved: true, valid: true, diagnostics: [], catalog: rich.catalog }
      : r.operation === 'read' ? doc(r.id) : r.operation === 'preview' ? preview(r) : undefined)
    return result?.ok === false ? result : { version: 1, ok: true, result }
  })
  createRoot(d => { dispose = d; session = createLibrarySession(libraryApi({ callWS } as any)) })
  return { calls, callWS }
}
async function flush(ms = 350) { await vi.advanceTimersByTimeAsync(ms) }
beforeEach(() => vi.useFakeTimers())
afterEach(() => { dispose?.(); vi.useRealTimers() })
it('debounces all-file drafts and preserves edits while navigating files', async () => {
  const { calls } = setup(); await flush()
  session.edit('draft main'); session.select('other'); session.edit('draft helper'); session.select('shared')
  expect(session.selected()?.source).toBe('draft main'); expect(session.preview()).toBeUndefined()
  const count = calls.filter(c => c.operation === 'preview').length
  await flush(299); expect(calls.filter(c => c.operation === 'preview')).toHaveLength(count)
  await flush(2)
  expect(calls.at(-1)).toMatchObject({ operation: 'preview', id: 'shared', source: 'draft main', sources: expect.arrayContaining([{ id: 'other', source: 'draft helper' }]) })
  expect(session.preview()?.valid).toBe(true)
})
it('rejects old previews when another file changes', async () => {
  let resolve!: (r: any) => void; let old: any
  setup(r => { if (r.operation === 'preview' && !old) { old = r; return new Promise(done => { resolve = done }) } }); await flush()
  session.select('other'); session.edit('changed helper'); session.select('shared'); await flush()
  const current = session.preview()
  resolve({ ...preview(old), data: { valid: false, diagnostics: [{ message: 'obsolete', severity: 3 }], catalog: rich.catalog } }); await flush(0)
  expect(session.preview()).toBe(current); expect(session.diagnostics()).toEqual([])
})
it('retains drafts on conflicts and reports saved activation failures', async () => {
  let conflict = true
  setup(r => r.operation === 'save' ? conflict ? { ok: false, error: { code: 'conflict', detail: 'Revision conflict; reload' } }
    : { saved: true, document: doc(r.id, r.source, 2), generation: 'g2', catalog: rich.catalog, automations: [{ id: 'graph', saved: true, active: false, activationError: 'activation failed', document: {} }] } : undefined)
  await flush(); session.edit('draft'); await flush(); await session.save()
  expect(session.conflict()).toBe(true); expect(session.selected()?.source).toBe('draft'); expect(session.selected()?.revision).toBe(1)
  conflict = false; await session.reload('shared'); session.edit('valid draft'); await flush()
  let changed = 0; const listener = () => changed++; window.addEventListener('hass-sharp-libraries-changed', listener)
  await session.save(); window.removeEventListener('hass-sharp-libraries-changed', listener)
  expect(session.selected()?.revision).toBe(2); expect(session.dirty()).toBe(false)
  expect(session.activations()[0].activationError).toBe('activation failed'); expect(changed).toBe(1)
})
it('creates stable files, renames with revision checks, and deletes through the API', async () => {
  const { calls } = setup(r => r.operation === 'save' ? { saved: true, document: { ...doc(r.id, r.source, 1), name: r.name }, generation: 'g2', catalog: rich.catalog, automations: [] }
    : r.operation === 'delete' ? { saved: true, deleted: true, document: null, generation: 'g3', catalog: rich.catalog, automations: [] } : undefined)
  await flush(); session.create('New.cs'); const id = session.selected()!.id
  session.rename('Renamed.cs'); await flush(); await session.save()
  expect(calls.find(c => c.operation === 'save')).toMatchObject({ id, name: 'Renamed.cs', expectedRevision: 0 })
  await session.remove()
  expect(calls.find(c => c.operation === 'delete')).toMatchObject({ id, expectedRevision: 1 })
  expect(session.files().some(f => f.id === id)).toBe(false)
})
it('blocks save until a valid current preview and preserves edits during persistence', async () => {
  let resolve!: (r: any) => void; let submitted: any
  const { calls } = setup(r => r.operation === 'save' ? new Promise(done => { submitted = r; resolve = done }) : undefined)
  await flush(); session.edit('first'); expect(session.canSave()).toBe(false); await session.save(); expect(calls.some(c => c.operation === 'save')).toBe(false)
  await flush(); const saving = session.save(); await flush(0); session.edit('second')
  resolve({ saved: true, document: doc('shared', submitted.source, 2), generation: 'g2', catalog: rich.catalog, automations: [] }); await saving
  expect(session.selected()?.source).toBe('second'); expect(session.selected()?.revision).toBe(2); expect(session.dirty()).toBe(true)
})
it('surfaces a changed saved revision on focus without overwriting drafts and invalidates symbol refresh', async () => {
  let revision = 1
  const { calls } = setup(r => r.operation === 'list' ? { documents: [doc('shared', rich.source, revision)], generation: `g${revision}` } : undefined)
  await flush(); session.edit('keep draft'); await flush(); const version = session.workspaceVersion()
  revision = 2; window.dispatchEvent(new Event('focus')); await flush()
  expect(session.selected()?.source).toBe('keep draft'); expect(session.selected()?.revision).toBe(1)
  expect(session.conflict()).toBe(true); expect(session.canSave()).toBe(false); expect(session.workspaceVersion()).not.toBe(version)
  const before = session.workspaceVersion(); window.dispatchEvent(new Event('hass-sharp-libraries-changed')); expect(session.workspaceVersion()).not.toBe(before)
  dispose(); const count = calls.length; window.dispatchEvent(new Event('focus')); window.dispatchEvent(new Event('hass-sharp-libraries-changed')); await flush()
  expect(calls).toHaveLength(count)
})
it('rejects reply identity mismatches, blocks current errors, and allows warnings', async () => {
  let severity = 3
  setup(r => r.operation === 'preview' ? { ...preview(r), data: { ...preview(r).data, valid: severity !== 3, diagnostics: [{ sourceId: 'shared', message: 'problem', severity }] } } : undefined)
  await flush(); expect(session.canSave()).toBe(false)
  severity = 2; session.edit('warning source'); await flush(); expect(session.canSave()).toBe(true)
  const c = session.context()!
  expect(session.accepts({ ...preview(c), id: 'foreign' }, c)).toBe(false)
  expect(session.accepts({ ...preview(c), generation: 'foreign' }, c)).toBe(false)
  expect(session.accepts({ ...preview(c), workspaceVersion: 'foreign' }, c)).toBe(false)
  expect(session.accepts({ ...preview(c), sourceGeneration: 'foreign' }, c)).toBe(false)
})
it('keeps edits made during deletion as a new unsaved document', async () => {
  let resolve!: (r: any) => void
  setup(r => r.operation === 'delete' ? new Promise(done => { resolve = done }) : undefined)
  await flush(); const removing = session.remove(); session.edit('new work after delete started')
  resolve({ saved: true, deleted: true, document: null, generation: 'g2', catalog: rich.catalog, automations: [] }); await removing
  expect(session.selected()?.source).toBe('new work after delete started'); expect(session.selected()?.revision).toBe(0); expect(session.dirty()).toBe(true)
})
it('defers a refresh requested during save so an old list cannot roll back the committed revision', async () => {
  let resolve!: (r: any) => void; let revision = 1
  const { calls } = setup(r => r.operation === 'save' ? new Promise(done => { resolve = done })
    : r.operation === 'list' ? { documents: [doc('shared', revision === 1 ? rich.source : 'submitted', revision)], generation: `g${revision}` } : undefined)
  await flush(); session.edit('submitted'); await flush(); const saving = session.save()
  await session.refresh()
  expect(calls.filter(c => c.operation === 'list')).toHaveLength(1)
  revision = 2; resolve({ saved: true, document: doc('shared', 'submitted', 2), generation: 'g2', catalog: rich.catalog, automations: [] }); await saving; await flush(0)
  expect(calls.filter(c => c.operation === 'list')).toHaveLength(2)
  expect(session.selected()?.revision).toBe(2); expect(session.generation()).toBe('g2')
})
