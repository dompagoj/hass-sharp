import { afterEach, expect, it, vi } from 'vitest'
import { nodeCatalog } from '../src/visual/contract'
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
  const hass = { callWS: async (message: any) => { if(message.type==='hass_sharp/node_library')return ok({catalog:nodeCatalog,generation:'fixture',saved:true,valid:true,diagnostics:[]}); calls.push(message); return handler(message.request) } } as any
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

it('reports local typed input errors immediately and rejects old graphs without replacing the draft', async () => {
 const {session}=setup(async req=>req.operation==='read'?ok({...document(),graph:{...graph(),version:3}}):ok(preview()))
 const before=session.history.graph()
 await session.load('motion')
 expect(session.error()).toContain('Unsupported graph version 3; only version 5 is supported')
 expect(session.history.graph()).toEqual(before)
 session.history.commit(updateNode(before,'condition-1',{config:{entity:{kind:'unset'},state:'on'}}))
 expect(session.diagnostics()).toEqual(expect.arrayContaining([expect.objectContaining({nodeId:'condition-1',inputId:'entity',severity:3})]))
 expect(session.canSave()).toBe(false)
})

it('refreshes document-owned catalogs and surfaces dependent graph revision conflicts',async()=>{
 let generation='one',revision=2
 const mutableCatalog=structuredClone(nodeCatalog)
 mutableCatalog.nodes['saved:custom']={label:'Saved custom',description:'Saved source method',category:'Library',minGraphVersion:5,config:{},inputs:{},outputs:{},execution:['next']}
 const hass={callWS:async(message:any)=>message.type==='hass_sharp/node_library'?ok({catalog:mutableCatalog,generation,saved:true,valid:true,diagnostics:[]}):message.request.operation==='read'?ok({...document(revision),catalogGeneration:generation}):ok(preview())} as any
 const session=createRoot(d=>{dispose=d;return createVisualSession(hass,graph())})
 await flush();await session.load('motion');await flush()
 expect(session.catalog().nodes['saved:custom']?.label).toBe('Saved custom')
 expect(nodeCatalog.nodes['saved:custom']).toBeUndefined()
 const local=updateNode(session.history.graph(),'trigger-1',{label:'Unsaved label'});session.history.commit(local)
 generation='two';revision=3
 await session.refreshCatalog()
 expect(session.conflict()).toBe(true)
 expect(session.error()).toContain('Node library changed this saved graph')
 expect(session.history.graph()).toEqual(local)
 expect(session.document()?.revision).toBe(2)
 expect(session.canSave()).toBe(false)
})

it('invalidates old previews and source maps when only the saved catalog generation changes',async()=>{
 vi.useFakeTimers();let generation='one';const pending:((value:any)=>void)[]=[]
 const hass={callWS:async(message:any)=>message.type==='hass_sharp/node_library'?ok({catalog:structuredClone(nodeCatalog),generation,saved:true,valid:true,diagnostics:[]}):new Promise(resolve=>pending.push(resolve))} as any
 const session=createRoot(d=>{dispose=d;return createVisualSession(hass,graph())})
 await flush();await vi.advanceTimersByTimeAsync(350);expect(pending).toHaveLength(1)
 const original=session.history.graph();generation='two';await session.refreshCatalog();await vi.advanceTimersByTimeAsync(350)
 expect(session.history.graph()).toBe(original);expect(session.history.canUndo()).toBe(false);expect(pending).toHaveLength(2)
 pending[1](ok({...preview('current library'),sourceMap:[{nodeId:'condition-1',startLine:9,endLine:11}]}));await flush()
 pending[0](ok({...preview('old library'),diagnostics:[{nodeId:'trigger-1',message:'Obsolete library error',severity:3}]}));await flush()
 expect(session.preview()?.source).toBe('current library');expect(session.preview()?.sourceMap[0].startLine).toBe(9)
 expect(session.diagnostics().some(d=>d.message==='Obsolete library error')).toBe(false)
})
it('ignores in-flight old catalogs and cleans up library/focus listeners',async()=>{
 const pending:((value:any)=>void)[]=[];const listeners=vi.spyOn(window,'addEventListener'),removed=vi.spyOn(window,'removeEventListener')
 const hass={callWS:async(message:any)=>message.type==='hass_sharp/node_library'?new Promise(resolve=>pending.push(resolve)):ok(preview())} as any
 const session=createRoot(d=>{dispose=d;return createVisualSession(hass,graph())})
 const fresh=session.refreshCatalog();expect(pending).toHaveLength(2)
 const latest=structuredClone(nodeCatalog);latest.nodes['library:latest']={label:'Latest saved',description:'Current',category:'Library',minGraphVersion:5,config:{},inputs:{},outputs:{},execution:['next']}
 pending[1](ok({catalog:latest,generation:'latest',saved:true,valid:true,diagnostics:[]}));await fresh
 pending[0](ok({catalog:nodeCatalog,generation:'old',saved:true,valid:true,diagnostics:[]}));await flush()
 expect(session.catalogGeneration()).toBe('latest');expect(session.catalog().nodes['library:latest']).toBeDefined()
 const registered=listeners.mock.calls.filter(([name])=>name==='hass-sharp-libraries-changed'||name==='focus')
 expect(registered).toHaveLength(2);dispose()
 for(const [name,callback] of registered)expect(removed).toHaveBeenCalledWith(name,callback)
 window.dispatchEvent(new Event('hass-sharp-libraries-changed'));window.dispatchEvent(new Event('focus'))
 expect(pending).toHaveLength(2)
 listeners.mockRestore();removed.mockRestore()
})

it('does not lose a pending rebuilt-revision check when a same-generation catalog refresh overlaps it', async () => {
  let savedGeneration = 'one', savedRevision = 2
  let finishFirstRead: ((value: unknown) => void) | undefined
  const hass = {
    callWS: async (message: any) => {
      if (message.type === 'hass_sharp/node_library')
        return ok({ catalog: structuredClone(nodeCatalog), generation: savedGeneration, saved: true, valid: true, diagnostics: [] })
      if (message.request.operation === 'read') {
        const result = ok({ ...document(savedRevision), catalogGeneration: savedGeneration })
        if (savedGeneration === 'two' && !finishFirstRead)
          return new Promise(resolve => { finishFirstRead = resolve })
        return result
      }
      return ok(preview())
    },
  } as any
  const session = createRoot(d => { dispose = d; return createVisualSession(hass, graph()) })
  await flush(); await session.load('motion'); await flush()
  const local = updateNode(session.history.graph(), 'trigger-1', { label: 'Keep my dirty graph' })
  session.history.commit(local)
  await vi.waitFor(() => expect(session.canSave()).toBe(true))
  savedGeneration = 'two'; savedRevision = 3
  const first = session.refreshCatalog()
  await flush()
  expect(session.catalogGeneration()).toBe('two')
  expect(finishFirstRead).toBeDefined()
  await session.refreshCatalog()
  finishFirstRead!(ok({ ...document(3), catalogGeneration: 'two' }))
  await first
  await vi.waitFor(() => expect(session.preview()?.valid).toBe(true))
  expect(session.conflict()).toBe(true)
  expect(session.error()).toContain('Node library changed this saved graph')
  expect(session.document()?.revision).toBe(2)
  expect(session.history.graph()).toEqual(local)
  expect(session.canSave()).toBe(false)
})
