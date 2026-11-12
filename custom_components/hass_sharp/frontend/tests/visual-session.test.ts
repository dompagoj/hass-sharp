import { afterEach, expect, it, vi } from 'vitest'
import { createRoot } from 'solid-js'
import { createGraph, updateNode } from '../src/visual/graph'
import { createVisualSession } from '../src/visual/session'
import { visualApi, VisualApiError } from '../src/visual/api'

let dispose: () => void
const graph = () => createGraph('Motion')
const preview = (source = 'server source') => ({ valid: true, source, sourceMap: [{ nodeId: 'trigger-1', startLine: 1, endLine: 3 }], diagnostics: [] })
const document = (revision = 2) => ({ version: 1, id: 'motion', revision, graph: graph(), source: 'saved source', sourceMap: [], automationId: 'visual:motion' })
const ok = (result: unknown) => ({ version: 1, ok: true, result })
const flush = async () => { await Promise.resolve(); await Promise.resolve() }
function setup(handler: (request: any) => Promise<any>) {
  const calls: any[] = []
  const hass = { callWS: async (message: any) => { calls.push(message); return handler(message.request) } } as any
  const session = createRoot(d => { dispose = d; return createVisualSession(hass, graph()) })
  return { session, calls, hass }
}
afterEach(() => { dispose?.(); vi.useRealTimers() })

it('unwraps application errors and never sends caller source to the visual API', async () => {
  const hass = { callWS: async () => ({ version: 1, ok: false, error: { code: 'conflict', detail: 'Changed elsewhere' } }) } as any
  await expect(visualApi(hass).save('motion', graph(), 2)).rejects.toMatchObject({ code: 'conflict', message: 'Changed elsewhere' })
})
it('debounces edits, clears stale code immediately and ignores out-of-order previews', async () => {
  vi.useFakeTimers()
  const resolvers: ((value: any) => void)[] = []
  const { session, calls } = setup(() => new Promise(resolve => resolvers.push(resolve)))
  await flush(); await vi.advanceTimersByTimeAsync(350)
  session.history.commit(updateNode(session.history.graph(), 'trigger-1', { label: 'New label' }))
  await vi.advanceTimersByTimeAsync(100)
  session.history.commit(updateNode(session.history.graph(), 'trigger-1', { label: 'Newest label' }))
  await vi.advanceTimersByTimeAsync(350)
  expect(calls).toHaveLength(2)
  resolvers[1](ok(preview('new source'))); await flush()
  resolvers[0](ok(preview('old source'))); await flush()
  expect(session.preview()?.source).toBe('new source')
  session.history.commit(updateNode(session.history.graph(), 'trigger-1', { label: 'Changed again' }))
  expect(session.preview()).toBeUndefined()
  expect(session.canSave()).toBe(false)
})
it('loads saved graph/source together, tracks dirty by content, and keeps server revision on conflict', async () => {
  const { session, calls } = setup(async req => req.operation === 'read' ? ok(document()) : req.operation === 'save'
    ? { version: 1, ok: false, error: { code: 'conflict', detail: 'Changed elsewhere' } } : ok(preview()))
  await session.load('motion')
  expect(session.document()?.source).toBe('saved source')
  expect(session.dirty()).toBe(false)
  session.history.commit(updateNode(session.history.graph(), 'trigger-1', { label: 'Edited' }))
  expect(session.dirty()).toBe(true)
  session.history.undo(); expect(session.dirty()).toBe(false)
  session.history.redo()
  await vi.waitFor(() => expect(session.canSave()).toBe(true))
  await session.save()
  expect(session.conflict()).toBe(true)
  expect(session.history.graph().nodes[0].label).toBe('Edited')
  expect(session.document()?.revision).toBe(2)
  expect(calls.find(c => c.request.operation === 'save').request).toMatchObject({ id: 'motion', expectedRevision: 2 })
  expect(calls.find(c => c.request.operation === 'save').request).not.toHaveProperty('source')
  await session.load('motion')
  expect(session.dirty()).toBe(false)
  expect(session.history.canUndo()).toBe(false)
})
it('treats activation failure as persisted and does not lose edits made during save', async () => {
  let finish!: (value: any) => void
  const { session } = setup(async req => req.operation === 'save' ? await new Promise(resolve => { finish = resolve }) : ok(preview()))
  await vi.waitFor(() => expect(session.canSave()).toBe(true))
  const saving = session.save()
  session.history.commit(updateNode(session.history.graph(), 'trigger-1', { label: 'Edit during save' }))
  finish(ok({ document: document(1), active: false, activationError: 'Runtime unavailable' })); await saving
  expect(session.activationError()).toBe('Runtime unavailable')
  expect(session.document()?.revision).toBe(1)
  expect(session.dirty()).toBe(true)
  expect(session.history.graph().nodes[0].label).toBe('Edit during save')
})
it('blocks save on errors, permits warnings, and recovers from a rejected preview', async () => {
  let response: any = new Error('Offline')
  const { session } = setup(async () => { if (response instanceof Error) throw response; return ok(response) })
  await vi.waitFor(() => expect(session.previewError()).toContain('Offline'))
  expect(session.canSave()).toBe(false)
  response = { ...preview(), valid: false, diagnostics: [{ nodeId: 'trigger-1', message: 'Bad graph', severity: 3, startLine: null, startColumn: null }] }
  session.retryPreview()
  await vi.waitFor(() => expect(session.preview()?.valid).toBe(false))
  expect(session.canSave()).toBe(false)
  response = { ...preview(), diagnostics: [{ nodeId: null, message: 'Warning', severity: 2, startLine: null, startColumn: null }] }
  session.retryPreview()
  await vi.waitFor(() => expect(session.canSave()).toBe(true))
  expect(new VisualApiError('validation', response).detail).toBe(response)
})
it('retains the draft and exposes a read failure instead of offering to overwrite a missing document', async () => {
  const { session } = setup(async req => req.operation === 'read' ? { version: 1, ok: false, error: { code: 'not_found', detail: 'Gone' } } : ok(preview()))
  await session.load('motion')
  expect(session.error()).toContain('Gone')
  await vi.waitFor(() => expect(session.preview()).toBeDefined())
  expect(session.canSave()).toBe(false)
})
it('invalidates pending reads when returning to the previously open identity', async () => {
  let finishB!: (value: any) => void
  const { session } = setup(async req => req.operation === 'read' && req.id === 'other'
    ? await new Promise(resolve => { finishB = resolve }) : req.operation === 'read' ? ok(document()) : ok(preview()))
  await session.load('motion')
  const loadingB = session.load('other')
  expect(session.document()).toBeUndefined()
  await session.load('motion')
  finishB(ok({ ...document(), id: 'other', automationId: 'visual:other' })); await loadingB
  expect(session.document()?.id).toBe('motion')
})
it('does not attach an old save result to a new draft', async () => {
  let finish!: (value: any) => void
  const { session } = setup(async req => req.operation === 'save' ? await new Promise(resolve => { finish = resolve }) : ok(preview()))
  await vi.waitFor(() => expect(session.canSave()).toBe(true))
  const saving = session.save()
  session.newDraft(createGraph('Different draft'))
  finish(ok({ document: document(1), active: true, activationError: null })); await saving
  expect(session.document()).toBeUndefined()
  expect(session.history.graph().name).toBe('Different draft')
})
