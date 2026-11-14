import { afterEach, describe, expect, it, vi } from 'vitest'
import { nodeCatalog, type ServicesSection } from '../src/visual/contract'
import servicesFixture from '../../../../tests/fixtures/visual-services.json'
import { visualFixture } from './visual-fixture'
import { createRoot } from 'solid-js'
import { createGraph, removeEdge, updateNode } from '../src/visual/graph'
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
  // Save does not wait for the check: the server checks a save itself.
  expect(session.canSave()).toBe(true)
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
  // A check that could not run is no known error: Save stays available (the server checks it).
  expect(session.canSave()).toBe(true)
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

it('lists a step-level problem once when the server reports it with a null input', async () => {
  // The server serialises inputId and portId as null; local diagnostics leave them out.
  const unreachable = 'Call service is not connected into the flow; connect a step to its execution input'
  const { session } = setup(async () => ok({ ...preview(), valid: false, diagnostics: [
    { nodeId: 'service-1', message: unreachable, severity: 3, startLine: null, startColumn: null, inputId: null, portId: null },
    { nodeId: 'service-1', message: 'Only the server knows this', severity: 3, startLine: null, startColumn: null, inputId: null, portId: null },
  ] }))
  const start = session.history.graph()
  session.history.commit(removeEdge(start, start.edges.find(e => e.kind !== 'data' && e.target === 'service-1')!.id))
  await vi.waitFor(() => expect(session.diagnostics().some(d => d.message === 'Only the server knows this')).toBe(true))
  expect(session.diagnostics().filter(d => d.message === unreachable)).toHaveLength(1)
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
 expect(session.error()).toContain('This automation changed elsewhere')
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
  expect(session.error()).toContain('This automation changed elsewhere')
  expect(session.document()?.revision).toBe(2)
  expect(session.history.graph()).toEqual(local)
  expect(session.canSave()).toBe(false)
})

describe('services section (C4)', () => {
  const section = (generation: string) => ({ ...structuredClone(servicesFixture), servicesGeneration: generation }) as unknown as ServicesSection
  function servicesSetup() {
    const state = { generation: 'one' as string | undefined, current: section('one'), fail: false, requests: 0 }
    const hass = { callWS: async (message: any) => {
      if (message.type !== 'hass_sharp/node_library') return ok(preview())
      if (message.request.operation === 'services') {
        state.requests++
        if (state.fail) throw new Error('Services offline')
        return ok(state.current)
      }
      return ok({ catalog: nodeCatalog, generation: 'fixture', ...(state.generation ? { servicesGeneration: state.generation } : {}), saved: true, valid: true, diagnostics: [] })
    } } as any
    const session = createRoot(d => { dispose = d; return createVisualSession(hass, graph()) })
    return { session, state }
  }
  it('attaches the section as catalog.services and refetches only when servicesGeneration changes', async () => {
    const { session, state } = servicesSetup()
    await vi.waitFor(() => expect(session.catalog().services?.servicesGeneration).toBe('one'))
    const first = session.catalog()
    expect(first.nodes).toBe(nodeCatalog.nodes)
    expect(nodeCatalog.services).toBeUndefined()
    await session.refreshCatalog(); await session.refreshCatalog()
    expect(state.requests).toBe(1)
    expect(session.catalog()).toBe(first)
    state.generation = 'two'; state.current = section('two')
    await session.refreshCatalog()
    expect(state.requests).toBe(2)
    expect(session.catalog().services?.servicesGeneration).toBe('two')
    expect(session.catalog()).not.toBe(first)
    await session.refreshCatalog()
    expect(state.requests).toBe(2)
  })
  it('keeps the catalog usable without services when the request fails, and retries on the next refresh', async () => {
    const { session, state } = servicesSetup()
    state.fail = true
    await session.refreshCatalog()
    await vi.waitFor(() => expect(state.requests).toBeGreaterThan(0))
    expect(session.catalog().services).toBeUndefined()
    expect(session.catalogError()).toBeUndefined()
    expect(session.diagnostics().filter(d => d.severity === 3)).toEqual([])
    state.fail = false
    await session.refreshCatalog()
    expect(session.catalog().services?.servicesGeneration).toBe('one')
    // Service checks now run: an unknown service is reported as a warning.
    session.history.commit(updateNode(session.history.graph(), 'service-1', { config: { ...session.history.graph().nodes[2].config, service: 'light.unknown_service' } }))
    expect(session.diagnostics()).toEqual(expect.arrayContaining([expect.objectContaining({ nodeId: 'service-1', severity: 2 })]))
  })
  it('does not request services from a catalog that reports no servicesGeneration', async () => {
    const { session, state } = servicesSetup()
    state.generation = undefined
    await session.refreshCatalog()
    expect(state.requests).toBeLessThanOrEqual(1)
    const before = state.requests
    await session.refreshCatalog()
    expect(state.requests).toBe(before)
  })
})

it('reads run traces from hass_sharp/run_trace and serves list results without source', async () => {
  const response = { version: 1, run: { runId: 'r1' }, live: false, snapshot: null, trace: null }
  const callWS = vi.fn(async () => response)
  expect(await visualApi({ callWS } as any).runTrace('r1')).toBe(response)
  expect(callWS).toHaveBeenCalledWith({ type: 'hass_sharp/run_trace', runId: 'r1' })
  const backend = visualFixture()
  backend.documents.set('motion', document() as any)
  const listed = await visualApi(backend as any).list()
  expect(listed[0]).toMatchObject({ id: 'motion', revision: 2 })
  expect(listed[0]).not.toHaveProperty('source')
  expect(listed[0]).not.toHaveProperty('sourceMap')
  expect((await visualApi(backend as any).read('motion')).source).toBe('saved source')
})

describe('saving right after an edit', () => {
  it('saves before the edit\'s check has answered, and shows what the server refuses', async () => {
    vi.useFakeTimers()
    let refuse = false
    const refused = { ...preview(), valid: false, diagnostics: [{ nodeId: 'trigger-1', inputId: null, message: 'Only the server refuses this', severity: 3, startLine: null, startColumn: null }] }
    const { session, calls } = setup(async req => req.operation === 'save'
      ? refuse ? { version: 1, ok: false, error: { code: 'validation', detail: refused } } : ok({ document: { ...document(1), graph: req.graph }, active: true, activationError: null })
      : ok(preview()))
    await flush(); await vi.advanceTimersByTimeAsync(350)
    // The edit a field commits on blur, just before the click on Save lands: its check has not run yet.
    session.history.commit(updateNode(session.history.graph(), 'trigger-1', { label: 'Typed and saved at once' }))
    expect(session.preview()).toBeUndefined()
    expect(session.previewPending()).toBe(true)
    expect(session.canSave()).toBe(true)
    await session.save()
    const saves = () => calls.filter(c => c.request.operation === 'save')
    expect(saves()).toHaveLength(1)
    expect(saves()[0].request.graph.nodes[0].label).toBe('Typed and saved at once')
    expect(session.dirty()).toBe(false)
    // A refused save shows the server's problems, and Save is unavailable for that known error.
    refuse = true
    session.history.commit(updateNode(session.history.graph(), 'trigger-1', { label: 'Refused' }))
    await session.save()
    expect(session.diagnostics().some(d => d.message === 'Only the server refuses this')).toBe(true)
    expect(session.canSave()).toBe(false)
  })
})

describe('a revision conflict', () => {
  /** A backend whose saved document's revision and graph can change behind the editor's back. */
  function conflictSetup(options: { conflictOnSave?: boolean } = {}) {
    const state = { generation: 'one', revision: 2, graph: graph(), conflictOnSave: options.conflictOnSave ?? false }
    const calls: any[] = []
    const hass = { callWS: async (message: any) => {
      if (message.type === 'hass_sharp/node_library') return ok({ catalog: nodeCatalog, generation: state.generation, saved: true, valid: true, diagnostics: [] })
      const req = message.request
      calls.push(req)
      if (req.operation === 'read') return ok({ ...document(state.revision), graph: structuredClone(state.graph), catalogGeneration: state.generation })
      if (req.operation === 'save') {
        if (req.id === 'motion' && (state.conflictOnSave || req.expectedRevision !== state.revision)) return { version: 1, ok: false, error: { code: 'conflict', detail: 'Changed elsewhere' } }
        if (req.id === 'motion') { state.revision++; state.graph = req.graph }
        return ok({ document: { ...document(req.expectedRevision + 1), id: req.id, automationId: `visual:${req.id}`, graph: req.graph, ...(req.enabled === false ? { enabled: false } : {}) }, active: true, activationError: null })
      }
      return ok(preview())
    } } as any
    const session = createRoot(d => { dispose = d; return createVisualSession(hass, graph()) })
    return { session, state, calls, saves: () => calls.filter(c => c.operation === 'save') }
  }
  /** The node library is saved elsewhere: it rebuilds the automation, which gets a new revision. */
  const librarySaved = (state: { generation: string; revision: number; graph: any }) => {
    state.generation = 'two'; state.revision++
    state.graph = updateNode(state.graph, 'service-1', { label: 'Rebuilt by the library' })
  }

  it('reloads the open automation quietly when the library changed it and nothing is unsaved', async () => {
    const { session, state } = conflictSetup()
    await flush(); await session.load('motion'); await flush()
    librarySaved(state)
    await session.refreshCatalog()
    expect(session.conflict()).toBe(false)
    expect(session.error()).toBeUndefined()
    expect(session.document()?.revision).toBe(3)
    expect(session.history.graph().nodes.find(n => n.id === 'service-1')?.label).toBe('Rebuilt by the library')
    expect(session.dirty()).toBe(false)
  })

  it('with unsaved changes, Keep my version saves the local graph against the latest revision', async () => {
    const { session, state, saves } = conflictSetup()
    await flush(); await session.load('motion'); await flush()
    const local = updateNode(session.history.graph(), 'trigger-1', { label: 'Mine' })
    session.history.commit(local)
    librarySaved(state)
    await session.refreshCatalog()
    expect(session.conflict()).toBe(true)
    expect(session.history.graph()).toEqual(local)
    await session.keepMine()
    expect(saves().at(-1)).toMatchObject({ id: 'motion', expectedRevision: 3 })
    expect(saves().at(-1).graph.nodes[0].label).toBe('Mine')
    expect(session.conflict()).toBe(false)
    expect(session.error()).toBeUndefined()
    expect(session.document()?.revision).toBe(4)
    expect(session.dirty()).toBe(false)
  })

  it('a save answered with a conflict offers the same choices; Save as a copy keeps both, the copy turned off', async () => {
    const { session, saves } = conflictSetup({ conflictOnSave: true })
    await flush(); await session.load('motion'); await flush()
    session.history.commit(updateNode(session.history.graph(), 'trigger-1', { label: 'Mine' }))
    await session.save()
    expect(session.conflict()).toBe(true)
    expect(session.history.graph().nodes[0].label).toBe('Mine')
    await session.saveCopy()
    const copy = saves().at(-1)
    expect(copy.id).not.toBe('motion')
    expect(copy).toMatchObject({ expectedRevision: 0, enabled: false })
    expect(copy.graph.name).toBe('Motion (copy)')
    expect(copy.graph.nodes[0].label).toBe('Mine')
    expect(session.conflict()).toBe(false)
    expect(session.document()?.id).toBe(copy.id)
    expect(session.dirty()).toBe(false)
    // The rename is one step that can be undone like any other.
    expect(session.history.canUndo()).toBe(true)
  })

  it('Discard my changes opens the latest saved revision', async () => {
    const { session, state } = conflictSetup()
    await flush(); await session.load('motion'); await flush()
    session.history.commit(updateNode(session.history.graph(), 'trigger-1', { label: 'Mine' }))
    librarySaved(state)
    await session.refreshCatalog()
    expect(session.conflict()).toBe(true)
    await session.discardChanges()
    expect(session.conflict()).toBe(false)
    expect(session.document()?.revision).toBe(3)
    expect(session.history.graph().nodes[0].label).not.toBe('Mine')
    expect(session.dirty()).toBe(false)
  })
})

describe('drafts of unsaved changes', () => {
  const key = 'hass-sharp:draft:motion'
  const stored = () => { const text = localStorage.getItem(key); return text ? JSON.parse(text) : undefined }
  function draftSetup() {
    const state = { revision: 2 }
    const hass = { callWS: async (message: any) => {
      if (message.type === 'hass_sharp/node_library') return ok({ catalog: nodeCatalog, generation: 'one', saved: true, valid: true, diagnostics: [] })
      const req = message.request
      if (req.operation === 'read') return ok(document(state.revision))
      if (req.operation === 'save') { state.revision++; return ok({ document: { ...document(state.revision), graph: req.graph }, active: true, activationError: null }) }
      return ok(preview())
    } } as any
    const open = () => {
      let stop!: () => void
      const session = createRoot(d => { stop = d; dispose = d; return createVisualSession(hass, graph()) })
      return { session, stop }
    }
    return { state, open }
  }

  it('keeps a draft while there are unsaved changes, and removes it on save', async () => {
    vi.useFakeTimers()
    const { open } = draftSetup()
    const { session } = open()
    await flush(); await session.load('motion'); await flush()
    session.history.commit(updateNode(session.history.graph(), 'trigger-1', { label: 'Not saved yet' }))
    expect(stored()).toBeUndefined()
    await vi.advanceTimersByTimeAsync(1000)
    expect(stored()).toMatchObject({ revision: 2, name: 'Motion' })
    expect(stored().graph.nodes[0].label).toBe('Not saved yet')
    expect(typeof stored().at).toBe('number')
    await session.save()
    await vi.advanceTimersByTimeAsync(1000)
    expect(stored()).toBeUndefined()
  })

  it('offers a draft when the automation is opened again, and restores it as one undo step', async () => {
    const { open } = draftSetup()
    const first = open()
    await flush(); await first.session.load('motion'); await flush()
    first.session.history.commit(updateNode(first.session.history.graph(), 'trigger-1', { label: 'Survives the panel' }))
    // The panel is torn down before the draft's delay is over: what is pending is written then.
    first.stop()
    expect(stored().graph.nodes[0].label).toBe('Survives the panel')
    const { session } = open()
    await flush(); await session.load('motion'); await flush()
    expect(session.restoreOffer()?.at).toBe(stored().at)
    expect(session.dirty()).toBe(false)
    session.restoreDraft()
    expect(session.restoreOffer()).toBeUndefined()
    expect(session.history.graph().nodes[0].label).toBe('Survives the panel')
    expect(session.dirty()).toBe(true)
    session.history.undo()
    expect(session.history.graph().nodes[0].label).not.toBe('Survives the panel')
    expect(session.dirty()).toBe(false)
  })

  it('offers nothing for an automation left with no unsaved changes, and Discard removes an offered draft', async () => {
    const { open } = draftSetup()
    const clean = open()
    await flush(); await clean.session.load('motion'); await flush()
    clean.stop()
    expect(stored()).toBeUndefined()
    const second = open()
    await flush(); await second.session.load('motion'); await flush()
    expect(second.session.restoreOffer()).toBeUndefined()
    second.session.history.commit(updateNode(second.session.history.graph(), 'trigger-1', { label: 'Changed' }))
    second.stop()
    const third = open()
    await flush(); await third.session.load('motion'); await flush()
    expect(third.session.restoreOffer()).toBeDefined()
    third.session.dismissDraft()
    expect(third.session.restoreOffer()).toBeUndefined()
    expect(stored()).toBeUndefined()
  })

  it('offers a draft of an older revision through the conflict choices', async () => {
    const { state, open } = draftSetup()
    const first = open()
    await flush(); await first.session.load('motion'); await flush()
    first.session.history.commit(updateNode(first.session.history.graph(), 'trigger-1', { label: 'Made on revision 2' }))
    first.stop()
    state.revision = 3
    const { session, stop } = open()
    await flush(); await session.load('motion'); await flush()
    expect(session.restoreOffer()).toBeUndefined()
    expect(session.conflict()).toBe(true)
    expect(session.conflictDraft()).toBe(stored().at)
    expect(session.history.graph().nodes[0].label).toBe('Made on revision 2')
    // Kept on the revision it was made on, so it is never offered as a plain restore of revision 3.
    session.history.commit(updateNode(session.history.graph(), 'trigger-1', { label: 'Made on revision 2, again' }))
    stop()
    expect(stored()).toMatchObject({ revision: 2 })
    expect(stored().graph.nodes[0].label).toBe('Made on revision 2, again')
  })

  it('removes drafts older than 14 days when the editor starts, and ignores what is not a draft', () => {
    const day = 24 * 60 * 60 * 1000
    const draft = (at: number) => JSON.stringify({ graph: graph(), revision: 1, name: 'Motion', at })
    localStorage.setItem('hass-sharp:draft:old', draft(Date.now() - 15 * day))
    localStorage.setItem('hass-sharp:draft:recent', draft(Date.now() - 13 * day))
    localStorage.setItem('hass-sharp:draft:broken', '{not json')
    localStorage.setItem('someone-else', 'kept')
    const { open } = draftSetup()
    open()
    expect(localStorage.getItem('hass-sharp:draft:old')).toBeNull()
    expect(localStorage.getItem('hass-sharp:draft:recent')).not.toBeNull()
    expect(localStorage.getItem('hass-sharp:draft:broken')).toBeNull()
    expect(localStorage.getItem('someone-else')).toBe('kept')
  })

  it('works without storage', async () => {
    const getItem = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('denied') })
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('denied') })
    try {
      const { open } = draftSetup()
      const { session, stop } = open()
      await flush(); await session.load('motion'); await flush()
      session.history.commit(updateNode(session.history.graph(), 'trigger-1', { label: 'No storage' }))
      expect(() => stop()).not.toThrow()
      expect(session.history.graph().nodes[0].label).toBe('No storage')
    } finally { getItem.mockRestore(); setItem.mockRestore() }
  })
})

describe('drafts and conflicts: closing pass', () => {
  const stored = (key: string) => { const text = localStorage.getItem(`hass-sharp:draft:${key}`); return text ? JSON.parse(text) : undefined }
  const draftKeys = () => Object.keys(localStorage).filter(k => k.startsWith('hass-sharp:draft:')).map(k => k.slice('hass-sharp:draft:'.length)).sort()
  /** A server holding `motion` at `state.revision`; `user` is the Home Assistant user of the panel. */
  function server(user?: string, options: { readFails?: boolean } = {}) {
    const state = { revision: 2, graph: graph(), readFails: options.readFails ?? false }
    const hass = { ...(user ? { user: { id: user } } : {}), callWS: async (message: any) => {
      if (message.type === 'hass_sharp/node_library') return ok({ catalog: nodeCatalog, generation: `g${state.revision}`, saved: true, valid: true, diagnostics: [] })
      const req = message.request
      if (req.operation === 'read') {
        if (state.readFails) return { version: 1, ok: false, error: { code: 'unavailable', detail: 'Try again' } }
        return ok({ ...document(state.revision), graph: structuredClone(state.graph) })
      }
      if (req.operation === 'save') {
        if (req.id === 'motion' && req.expectedRevision !== state.revision) return { version: 1, ok: false, error: { code: 'conflict', detail: 'Changed elsewhere' } }
        if (req.id === 'motion') { state.revision++; state.graph = req.graph }
        return ok({ document: { ...document(req.expectedRevision + 1), id: req.id, graph: req.graph }, active: true, activationError: null })
      }
      return ok(preview())
    } } as any
    const open = (code?: () => { nodeId: string; text: string } | undefined) => {
      let stop!: () => void
      const session = createRoot(d => { stop = d; dispose = d; return createVisualSession(hass, graph(), { code }) })
      return { session, stop }
    }
    return { state, open }
  }
  const edit = (session: ReturnType<typeof createVisualSession>, label: string) => session.history.commit(updateNode(session.history.graph(), 'trigger-1', { label }))

  it('keeps drafts per Home Assistant user: another person in this browser is not offered them', async () => {
    const ann = server('ann')
    const first = ann.open()
    await flush(); await first.session.load('motion'); await flush()
    edit(first.session, 'Ann\'s change')
    first.stop()
    expect(draftKeys()).toEqual(['ann/motion'])
    const bob = server('bob').open()
    await flush(); await bob.session.load('motion'); await flush()
    expect(bob.session.restoreOffer()).toBeUndefined()
    bob.stop()
    const again = ann.open()
    await flush(); await again.session.load('motion'); await flush()
    expect(again.session.restoreOffer()?.graph.nodes[0].label).toBe('Ann\'s change')
  })

  it('writes a dirty session\'s draft when it is torn down, keeps the time of changes that did not change, and removes only its own', async () => {
    vi.useFakeTimers()
    const { open } = server()
    const first = open()
    await flush(); await first.session.load('motion'); await flush()
    edit(first.session, 'Made first')
    await vi.advanceTimersByTimeAsync(1000)
    const at = stored('motion').at
    // Torn down with no write waiting: the changes are written all the same, with the time they were made.
    await vi.advanceTimersByTimeAsync(60_000)
    localStorage.removeItem('hass-sharp:draft:motion')
    first.stop()
    expect(stored('motion').graph.nodes[0].label).toBe('Made first')
    const written = stored('motion').at
    // Opened again and restored, the same changes keep that time.
    const second = open()
    await flush(); await second.session.load('motion'); await flush()
    second.session.restoreDraft()
    await vi.advanceTimersByTimeAsync(60_000)
    second.stop()
    expect(stored('motion').at).toBe(written)
    expect(written).toBeGreaterThanOrEqual(at)
    // A clean session of the same automation (another tab) leaves the draft it did not write alone.
    const clean = open()
    await flush(); await clean.session.load('motion'); await flush()
    await vi.advanceTimersByTimeAsync(1000)
    clean.stop()
    expect(stored('motion')).toBeDefined()
  })

  it('gives each new automation its own draft, and offers the newest one a new automation left', async () => {
    vi.useFakeTimers()
    const { open } = server('ann')
    const first = open()
    first.session.newDraft(createGraph('New automation'))
    edit(first.session, 'First new')
    await vi.advanceTimersByTimeAsync(1000)
    const second = open()
    second.session.newDraft(createGraph('New automation'))
    // Nothing was left yet: the first is still being edited.
    expect(second.session.restoreOffer()).toBeUndefined()
    edit(second.session, 'Second new')
    await vi.advanceTimersByTimeAsync(1000)
    const keys = draftKeys()
    expect(keys).toHaveLength(2)
    for (const key of keys) expect(key).toMatch(/^ann\/new:/)
    first.stop(); second.stop()
    const third = open()
    third.session.newDraft(createGraph('New automation'))
    expect(third.session.restoreOffer()?.graph.nodes[0].label).toBe('Second new')
    third.session.restoreDraft()
    // Taken over: written under the key it came from, not a third one.
    edit(third.session, 'Second new, continued')
    await vi.advanceTimersByTimeAsync(1000)
    expect(draftKeys()).toHaveLength(2)
  })

  it('sends a draft offered for a revision that was saved again meanwhile through the conflict choices', async () => {
    const { state, open } = server()
    const first = open()
    await flush(); await first.session.load('motion'); await flush()
    edit(first.session, 'Made on revision 2')
    first.stop()
    const { session } = open()
    await flush(); await session.load('motion'); await flush()
    expect(session.restoreOffer()).toBeDefined()
    // Saved elsewhere while the offer is open: the page comes back and reloads quietly (nothing is unsaved).
    state.revision = 3
    state.graph = updateNode(state.graph, 'trigger-1', { label: 'Revision 3' })
    await session.refreshCatalog()
    expect(session.restoreOffer()).toBeUndefined()
    expect(session.document()?.revision).toBe(3)
    expect(session.conflict()).toBe(true)
    expect(session.conflictDraft()).toBeDefined()
    expect(session.history.graph().nodes[0].label).toBe('Made on revision 2')
  })

  it('says Keep my version did not save a graph with problems yet, and keeps its draft on the latest revision', async () => {
    vi.useFakeTimers()
    const { state, open } = server()
    const first = open()
    await flush(); await first.session.load('motion'); await flush()
    edit(first.session, 'Mine')
    first.stop()
    state.revision = 3
    const { session } = open()
    await flush(); await session.load('motion'); await flush()
    expect(session.conflict()).toBe(true)
    // A problem only this graph has: a Compare with nothing to compare.
    session.history.commit({ ...session.history.graph(), nodes: [...session.history.graph().nodes, { id: 'cmp', kind: 'compare', config: {}, position: { x: 0, y: 0 } } as any] })
    expect(session.diagnostics().some(d => d.severity === 3)).toBe(true)
    await session.keepMine()
    expect(state.revision).toBe(3)
    expect(session.conflict()).toBe(false)
    expect(session.error()).toBe('Not saved yet: fix the problems, then Save.')
    expect(session.document()?.revision).toBe(3)
    expect(stored('motion')).toMatchObject({ revision: 3 })
  })

  it('keeps unapplied code in the draft and gives it back when the draft is restored', async () => {
    vi.useFakeTimers()
    const { open } = server()
    let code: { nodeId: string; text: string } | undefined
    const first = open(() => code)
    await flush(); await first.session.load('motion'); await flush()
    code = { nodeId: 'trigger-1', text: 'typed, not applied' }
    first.stop()
    expect(stored('motion').code).toEqual(code)
    code = undefined
    const { session } = open(() => code)
    await flush(); await session.load('motion'); await flush()
    expect(session.restoreOffer()?.code).toEqual({ nodeId: 'trigger-1', text: 'typed, not applied' })
    session.restoreDraft()
    expect(session.takeRestoredCode()).toEqual({ nodeId: 'trigger-1', text: 'typed, not applied' })
    expect(session.takeRestoredCode()).toBeUndefined()
  })

  it('Discard after a read that failed reloads the automation that was asked for', async () => {
    const { state, open } = server(undefined, { readFails: true })
    const { session } = open()
    await flush(); await session.load('motion'); await flush()
    expect(session.document()).toBeUndefined()
    state.readFails = false
    await session.discardChanges()
    expect(session.document()?.id).toBe('motion')
  })
})
