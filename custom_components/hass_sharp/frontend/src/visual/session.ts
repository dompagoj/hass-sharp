import { batch, createEffect, createMemo, createSignal, onCleanup } from 'solid-js'
import type { HomeAssistant } from '../types'
import { createGraphHistory, serializeGraph, type VisualGraph } from './graph'
import { visualApi, visualError, type VisualDocument, type VisualPreview } from './api'
import { nodeCatalog, type ServicesSection, type VisualCatalogSnapshot } from './contract'
import { connectionIndex } from './connectionIndex'
import { danglingWireIds } from './danglingWire'
import { createUuid } from './id'
import { draftSpace, listDrafts, pruneDrafts, readDraft, removeDraft, writeDraft, type VisualDraft } from './drafts'
import { automationAdapter, type EditorAdapter, type EditorDocument, type EditorSaveResult } from './adapter'

/** How long the editor waits after a change before keeping its draft. */
const draftDelay = 500
/** Keep my version could not save the local graph yet: it has problems the person fixes first. */
export const notSavedYet = 'Not saved yet: fix the problems, then Save.'
/** The draft keys of the sessions open in this page: a new document is never offered one still being edited here. */
const liveKeys = new Set<string>()
/** Unapplied code of a Custom C# node's code editor. */
export type UnappliedCode = { nodeId: string; text: string }

/**
 * A new document's first save: `save` under the id `newId` gives, and when that id was taken elsewhere since it was
 * chosen (`isConflict`; an id from the name, as an entity definition's is), once more under the id `newId` gives then.
 * A second conflict, a fresh id that is the taken one again (the first conflict) and any other failure are thrown. An
 * id given at once (not a promise) is saved under in the same turn, as an automation's save always was.
 */
export async function firstSave<T>(newId: () => string | Promise<string>, save: (id: string) => Promise<T>, isConflict: (e: unknown) => boolean): Promise<{ id: string; saved: T }> {
  const made = newId(), id = typeof made === 'string' ? made : await made
  try {
    return { id, saved: await save(id) }
  } catch (err) {
    if (!isConflict(err)) throw err
    const next = await newId()
    if (next === id) throw err
    return { id: next, saved: await save(next) }
  }
}

/**
 * Owns graph/source pairs and their revisions. Never generates or persists source locally. The document is read,
 * checked, saved and named through `adapter` (default: an automation's, {@link automationAdapter}); the node catalog
 * and services are the node library's, whatever the document.
 *
 * `unsaved` says whether the editor has changes beyond the graph (a code editor's unapplied text): a saved revision
 * that changed elsewhere is then never reloaded quietly.
 *
 * While there are unsaved changes (the graph, or `code`: a code editor's unapplied text) a draft of them is kept in this
 * browser ({@link VisualDraft}) under the document and the Home Assistant user, once the open document is known
 * (`load`, `openNew`, `newDraft`; a new document's key is its own editing session's), as the adapter's `draft` gives
 * the graph (an entity definition leaves its secret values out). A session removes only a draft
 * it wrote or took over, on save, reload, discard or when its changes are undone; a dirty one writes its draft when it
 * is torn down. Opening a document with a draft made on its saved revision offers it (`restoreOffer`; a new
 * document, the newest left by a new document of its kind); a draft made on an older revision is shown as a conflict, with the
 * same choices as one met while editing (`keepMine`, `saveCopy`, `discardChanges`). A draft's unapplied code comes back
 * through `restoredCode`, for the code editor.
 */
export function createVisualSession<D extends EditorDocument = VisualDocument>(
  hass: Pick<HomeAssistant, 'callWS'>,
  initial: VisualGraph,
  options: { unsaved?: () => boolean; code?: () => UnappliedCode | undefined; adapter?: EditorAdapter<D> } = {},
) {
  const api = visualApi(hass)
  const adapter = options.adapter ?? (automationAdapter(hass) as unknown as EditorAdapter<D>)
  const conflictMessage = adapter.texts.conflictMessage
  /** The graph as a draft keeps it. */
  const toDraft = (graph: VisualGraph) => (adapter.draft ? adapter.draft(graph) : graph)
  const history = createGraphHistory(initial)
  const [libraryCatalog, setCatalog] = createSignal<VisualCatalogSnapshot>(nodeCatalog)
  // C4: the services section never enters the catalog root; it is attached to the snapshot the editor validates with.
  const [services, setServices] = createSignal<ServicesSection>()
  const catalog = createMemo<VisualCatalogSnapshot>(() => {
    const base = libraryCatalog(), section = services()
    return section ? { ...base, services: section } : base
  })
  const [catalogGeneration, setCatalogGeneration] = createSignal<string>()
  const [catalogReady, setCatalogReady] = createSignal(false)
  const [catalogError, setCatalogError] = createSignal<string>()
  const [document, setDocument] = createSignal<D>()
  const [baseline, setBaseline] = createSignal(serializeGraph(initial))
  const [preview, setPreview] = createSignal<VisualPreview>()
  const [previewError, setPreviewError] = createSignal<string>()
  const [error, setError] = createSignal<string>()
  const [conflict, setConflict] = createSignal(false)
  /** When the conflict is a draft made on an older revision: when that draft was written. */
  const [conflictDraft, setConflictDraft] = createSignal<number>()
  const [restoreOffer, setRestoreOffer] = createSignal<VisualDraft>()
  /** Unapplied code of a draft taken back, for the code editor to open with (`takeRestoredCode`). */
  const [restoredCode, setRestoredCode] = createSignal<UnappliedCode>()
  const [activationError, setActivationError] = createSignal<string>()
  const [loading, setLoading] = createSignal(false)
  const [saving, setSaving] = createSignal(false)
  const [readFailed, setReadFailed] = createSignal(false)
  const [previewAttempt, setPreviewAttempt] = createSignal(0)
  const dirty = createMemo(() => serializeGraph(history.graph()) !== baseline())
  const unsaved = () => dirty() || !!options.unsaved?.()
  const diagnostics = createMemo(() => {
    // The same validation the connection index of this graph snapshot already ran.
    const graph = history.graph(), analysis = connectionIndex(graph, catalog()).analysis
    // A problem about a wire whose pin is missing names that wire, so it can be removed from the problem.
    const wires = danglingWireIds(graph, analysis)
    const local = [
      ...analysis.diagnostics.map((d, i) => ({ ...d, startLine: null, startColumn: null, ...(wires[i] ? { edgeId: wires[i]!.id, edgeIndex: wires[i]!.index } : {}) })),
      ...(adapter.check?.(graph, catalog()) ?? []),
    ]
    const backend = preview()?.diagnostics ?? []
    // The server sends a missing input as null; local diagnostics leave it out.
    return [
      ...local,
      ...backend.filter(d => !local.some(other => other.nodeId === d.nodeId && (other.inputId ?? null) === (d.inputId ?? null) && other.message === d.message)),
    ]
  })
  /** A new document's id, once its first save has asked the adapter for one (a failed save tries it again). */
  let id: string | undefined
  let generation = 0,
    loadGeneration = 0,
    identityGeneration = 0,
    disposed = false
  let catalogRequest = 0
  // A catalog reply does not prove the opened document revision was checked.
  let checkedDocument: { id: string; revision: number; generation: string } | undefined
  let servicesRequest = 0,
    servicesPending: string | undefined
  /**
   * Fetch the services section when the catalog reports a generation other than the attached one. A failure keeps
   * the catalog usable without services (validators skip service checks); the next catalog refresh retries.
   */
  const refreshServices = async (wanted: string | undefined) => {
    if (wanted === undefined || services()?.servicesGeneration === wanted || servicesPending === wanted) return
    const request = ++servicesRequest
    servicesPending = wanted
    try {
      const section = await api.services()
      if (disposed || request !== servicesRequest) return
      if (section && typeof section.servicesGeneration === 'string' && section.items && typeof section.items === 'object') setServices(section)
    } catch {
      // Keep the previous section (or none); retried on the next catalog refresh.
    } finally {
      if (request === servicesRequest) servicesPending = undefined
    }
  }
  const refreshCatalog = async () => {
    const request = ++catalogRequest
    const opened = document()
    try {
      const result = await api.catalog()
      if (disposed || request !== catalogRequest) return
      if (!result.catalog || result.catalog.graphVersion !== nodeCatalog.graphVersion || !result.valid) throw new Error('The saved node catalog is invalid')
      batch(() => {
        if (result.generation !== catalogGeneration()) {
          setCatalog(result.catalog)
          setCatalogGeneration(result.generation)
        }
        setCatalogReady(true)
        setCatalogError(undefined)
      })
      const servicesRefresh = refreshServices(result.servicesGeneration)
      if (
        opened &&
        (checkedDocument?.id !== opened.id ||
          checkedDocument.revision !== opened.revision ||
          checkedDocument.generation !== result.generation)
      ) {
        const latest = await adapter.read(opened.id)
        if (!disposed && request === catalogRequest && document()?.id === opened.id && document()?.revision === opened.revision) {
          checkedDocument = { id: opened.id, revision: opened.revision, generation: result.generation }
          if (latest.revision !== opened.revision) {
            // Nothing unsaved: show the new revision. Unsaved changes: the person chooses what to keep.
            if (!unsaved() && latest.graph?.version === nodeCatalog.graphVersion) {
              // A draft offered for the revision that was open is now a draft of an older one: the conflict choices.
              const offer = restoreOffer()
              batch(() => {
                showSaved(latest)
                setError(undefined)
                if (offer) {
                  setRestoreOffer(undefined)
                  adoptConflict(offer)
                }
              })
              checkedDocument = { id: latest.id, revision: latest.revision, generation: result.generation }
            } else
              batch(() => {
                setConflict(true)
                setError(conflictMessage)
              })
          }
        }
      }
      await servicesRefresh
    } catch (err) {
      if (!disposed && request === catalogRequest) {
        setCatalogReady(false)
        setCatalogError(visualError(err))
      }
    }
  }
  void refreshCatalog()
  const catalogChanged = () => {
    void refreshCatalog()
  }
  window.addEventListener('hass-sharp-libraries-changed', catalogChanged)
  window.addEventListener('focus', catalogChanged)
  onCleanup(() => {
    window.removeEventListener('hass-sharp-libraries-changed', catalogChanged)
    window.removeEventListener('focus', catalogChanged)
  })
  onCleanup(() => {
    disposed = true
    generation++
    loadGeneration++
    catalogRequest++
    servicesRequest++
  })
  createEffect(() => {
    const graph = history.graph()
    previewAttempt()
    catalog()
    const current = ++generation
    batch(() => {
      setPreview(undefined)
      setPreviewError(undefined)
    })
    const timer = setTimeout(async () => {
      try {
        const result = await adapter.preview(graph)
        if (!disposed && current === generation) setPreview(result)
      } catch (err) {
        if (!disposed && current === generation) setPreviewError(visualError(err))
      }
    }, 300)
    onCleanup(() => {
      clearTimeout(timer)
      if (current === generation) generation++
    })
  })
  /** The check of the current graph has not answered yet. */
  const previewPending = () => !preview() && !previewError()
  // Save does not wait for the check of the latest edit (the click that ends an edit would find Save disabled), nor
  // need one that could not run: the server checks every save and answers with its problems. Only a known error stops it.
  const canSave = () =>
    catalogReady() &&
    !diagnostics().some(d => d.severity === 3) &&
    !loading() &&
    !saving() &&
    !readFailed() &&
    !conflict() &&
    preview()?.valid !== false &&
    !preview()?.diagnostics.some(d => d.severity === 3)

  // ---- Drafts of unsaved changes (see the doc comment above).
  /** The Home Assistant user, so that another person in this browser is never offered these changes. */
  const user = () => {
    try {
      const id = (hass as Partial<HomeAssistant>).user?.id
      return typeof id === 'string' && id ? id : undefined
    } catch {
      return undefined
    }
  }
  const space = draftSpace(adapter.document)
  const scoped = (key: string) => { const who = user(); return who ? `${who}/${space}${key}` : `${space}${key}` }
  /** Where this document's draft is kept; none until the open document is known. */
  let draftKey: string | undefined
  /** The key of the draft offered for restoring (a new document's may be another editing session's). */
  let offerKey: string | undefined
  /** The revision the local changes were made on, when it is not the open document's (a draft of an older one). */
  let draftBase: number | undefined
  /** The person discarded the changes (leaving without saving): nothing is kept until the next document opens. */
  let draftDiscarded = false
  /** This session wrote the draft under `draftKey`, or took it over: only then does it remove it on its own. */
  let ownsDraft = false
  let draftTimer: ReturnType<typeof setTimeout> | undefined
  pruneDrafts()
  /** Moves this session to `key` (or none) in the page's live keys. */
  const holdKey = (key: string | undefined) => {
    if (draftKey) liveKeys.delete(draftKey)
    draftKey = key
    if (key) liveKeys.add(key)
  }
  onCleanup(() => { if (draftKey) liveKeys.delete(draftKey) })
  const changed = () => dirty() || !!options.code?.()
  const keepDraft = () => {
    clearTimeout(draftTimer)
    draftTimer = undefined
    if (!draftKey || draftDiscarded || restoreOffer() || loading()) return
    if (!changed()) {
      if (ownsDraft) removeDraft(draftKey)
      ownsDraft = false
      return
    }
    const graph = toDraft(history.graph()), code = options.code?.()
    if (writeDraft(draftKey, { graph, revision: draftBase ?? document()?.revision ?? 0, name: graph.name, at: Date.now(), ...(code ? { code } : {}) })) ownsDraft = true
  }
  createEffect(() => {
    history.graph()
    const waiting = loading() || !!restoreOffer()
    if (!changed() || waiting) {
      clearTimeout(draftTimer)
      draftTimer = undefined
      if (!changed() && !waiting && draftKey && !draftDiscarded && ownsDraft) {
        removeDraft(draftKey)
        ownsDraft = false
      }
      return
    }
    clearTimeout(draftTimer)
    draftTimer = setTimeout(keepDraft, draftDelay)
  })
  /** The page or the panel goes away: unsaved changes are written now. */
  const flushDraft = () => { if (draftTimer !== undefined || changed()) keepDraft() }
  window.addEventListener('pagehide', flushDraft)
  onCleanup(() => {
    window.removeEventListener('pagehide', flushDraft)
    flushDraft()
  })
  /** A draft of an older revision: its changes are shown, and the person chooses what to keep. */
  const adoptConflict = (draft: VisualDraft) => {
    batch(() => {
      history.commit(draft.graph)
      draftBase = draft.revision
      ownsDraft = true
      setConflict(true)
      setConflictDraft(draft.at)
      setError(conflictMessage)
      if (draft.code) setRestoredCode(draft.code)
    })
  }
  /** Starts keeping drafts under `key`; a draft already there is offered (same revision) or shown as a conflict. */
  const openDraft = (key: string, saved?: D) => {
    holdKey(key)
    offerKey = undefined
    draftBase = undefined
    draftDiscarded = false
    ownsDraft = false
    setRestoredCode(undefined)
    const draft = readDraft(key)
    if (!draft) return
    const revision = saved?.revision ?? 0
    if (!draft.code && serializeGraph(draft.graph) === serializeGraph(toDraft(saved?.graph ?? history.graph()))) return removeDraft(key)
    if (draft.revision !== revision) return adoptConflict(draft)
    offerKey = key
    setRestoreOffer(draft)
  }
  /** A new document: its own key, and the newest draft a new document of its kind left before is offered. */
  const openNewDraft = () => {
    openDraft(scoped(`new:${createUuid()}`))
    const left = listDrafts(scoped('new:')).find(d => !liveKeys.has(d.key))
    if (!left) return
    offerKey = left.key
    setRestoreOffer(left.draft)
  }
  /** Shows a saved revision as the clean state of the editor. */
  const showSaved = (saved: D) => {
    history.reset(saved.graph)
    setDocument(() => saved)
    setBaseline(serializeGraph(saved.graph))
    setConflict(false)
    setConflictDraft(undefined)
    setReadFailed(false)
    setActivationError(saved.activationError ?? undefined)
    draftBase = undefined
  }
  const newDraft = (graph: VisualGraph) => {
    identityGeneration++
    loadGeneration++
    id = undefined
    batch(() => {
      history.reset(graph)
      setDocument(undefined)
      setBaseline(serializeGraph(graph))
      setError(undefined)
      setConflict(false)
      setConflictDraft(undefined)
      setRestoreOffer(undefined)
      setReadFailed(false)
      setActivationError(undefined)
      setLoading(false)
      setSaving(false)
      openNewDraft()
    })
  }
  /** The document last asked for: Discard reloads it even when its read failed. */
  let requested: string | undefined
  const load = async (documentId: string) => {
    requested = documentId
    identityGeneration++
    setSaving(false)
    const current = ++loadGeneration
    batch(() => {
      setLoading(true)
      setDocument(undefined)
      setError(undefined)
      setRestoreOffer(undefined)
    })
    try {
      const saved = await adapter.read(documentId)
      if (disposed || current !== loadGeneration) return
      if (saved.graph.version !== nodeCatalog.graphVersion)
        throw new Error(`Unsupported graph version ${saved.graph.version}; only version ${nodeCatalog.graphVersion} is supported`)
      batch(() => {
        showSaved(saved)
        openDraft(scoped(saved.id), saved)
      })
      void refreshCatalog()
    } catch (err) {
      if (!disposed && current === loadGeneration) {
        setError(visualError(err))
        setReadFailed(true)
      }
    } finally {
      if (!disposed && current === loadGeneration) setLoading(false)
    }
  }
  const save = async () => {
    if (!canSave()) return
    const snapshot = history.graph()
    const current = identityGeneration
    setSaving(true)
    setError(undefined)
    try {
      const opened = document()
      let result: EditorSaveResult<D>
      if (opened) result = await adapter.save(opened.id, snapshot, opened.revision)
      else {
        // A new document: the id an earlier first save chose, else the one `newId` gives, kept for the next try. An
        // automation's ids are random, so it never meets the retry. A page that moved on meanwhile saves nothing (the
        // catch below says nothing of it).
        let chosen = id
        const take = (next: string) => {
          if (disposed || current !== identityGeneration) throw new Error('The editor moved on')
          return (id = next)
        }
        const ask = () => {
          const next = chosen ?? adapter.newId(snapshot)
          chosen = undefined
          return typeof next === 'string' ? take(next) : next.then(take)
        }
        result = (await firstSave(ask, target => adapter.save(target, snapshot, 0), err => adapter.apiError(err)?.code === 'conflict')).saved
      }
      if (disposed || current !== identityGeneration) return
      saved(result, snapshot)
      return result
    } catch (err) {
      if (disposed || current !== identityGeneration) return
      refused(err, snapshot)
    } finally {
      if (!disposed && current === identityGeneration) setSaving(false)
    }
  }
  const saved = (result: EditorSaveResult<D>, snapshot: VisualGraph) => {
    const before = draftKey
    // What the editor goes on with: the graph sent, unless the adapter says what it now stands for.
    const kept = adapter.savedGraph ? adapter.savedGraph(snapshot, result.document) : snapshot
    batch(() => {
      if (kept !== snapshot && serializeGraph(history.graph()) === serializeGraph(snapshot)) history.replace(kept)
      setDocument(() => result.document)
      setBaseline(serializeGraph(kept))
      setConflict(false)
      setConflictDraft(undefined)
      setActivationError(
        result.active
          ? undefined
          : (result.activationError ?? 'Runtime activation failed. Edit and save again, or reload the HassSharp integration.'),
      )
      draftBase = undefined
      // A new document's draft moves to its id once it has one.
      const key = scoped(result.document.id)
      if (before !== key) {
        if (before && ownsDraft) removeDraft(before)
        holdKey(key)
        ownsDraft = false
      }
    })
  }
  const refused = (err: unknown, snapshot: VisualGraph) => {
    setError(visualError(err))
    const failure = adapter.apiError(err)
    if (!failure) return
    if (failure.code === 'conflict') {
      // Saved elsewhere first. Nothing unsaved here (an inactive revision saved again): show the saved one.
      const doc = document()
      if (!unsaved() && doc) void load(doc.id)
      else setConflict(true)
    }
    if (failure.code === 'validation' && serializeGraph(history.graph()) === serializeGraph(snapshot)) setPreview(failure.detail as VisualPreview)
  }
  /** Keep my version: save the local graph over the latest saved revision (the server checks it as any save). */
  const keepMine = async () => {
    const doc = document()
    if (!doc || saving() || loading()) return
    const current = identityGeneration
    setSaving(true)
    setError(undefined)
    let latest: D
    try {
      latest = await adapter.read(doc.id)
    } catch (err) {
      if (!disposed && current === identityGeneration) {
        setError(visualError(err))
        setSaving(false)
      }
      return
    }
    if (disposed || current !== identityGeneration) return
    batch(() => {
      setDocument(() => latest)
      setBaseline(serializeGraph(latest.graph))
      setConflict(false)
      setConflictDraft(undefined)
      setActivationError(latest.activationError ?? undefined)
      setSaving(false)
      draftBase = undefined
    })
    // The local graph is now the unsaved change to the latest revision, and its draft says so. One with problems is
    // not saved yet: they are fixed first, then saved.
    if (!dirty()) return
    keepDraft()
    if (!canSave()) {
      setError(notSavedYet)
      return
    }
    return save()
  }
  /** Save as a copy: the local graph becomes a new document, turned off, and the editor goes on with it. */
  const saveCopy = async () => {
    if (saving() || loading()) return
    const current = identityGeneration
    const local = history.graph()
    const copy: VisualGraph = { ...local, name: `${local.name} (copy)` }
    setSaving(true)
    setError(undefined)
    try {
      const made = adapter.newId(copy)
      const copyId = typeof made === 'string' ? made : await made
      if (disposed || current !== identityGeneration) return
      // Turned off, so it does not answer the original's triggers a second time.
      const result = await adapter.save(copyId, copy, 0, { enabled: false })
      if (disposed || current !== identityGeneration) return
      // A load of the original still on its way is no longer wanted.
      loadGeneration++
      id = copyId
      batch(() => {
        history.commit(copy)
        saved(result, copy)
      })
      return result
    } catch (err) {
      if (disposed || current !== identityGeneration) return
      setError(visualError(err))
      const failure = adapter.apiError(err)
      if (failure?.code === 'validation') setPreview(failure.detail as VisualPreview)
    } finally {
      if (!disposed && current === identityGeneration) setSaving(false)
    }
  }
  /** Discard my changes (or Reload saved graph): the draft goes, and the latest saved revision opens. */
  const discardChanges = async () => {
    const target = document()?.id ?? requested
    if (draftKey) removeDraft(draftKey)
    ownsDraft = false
    // Unapplied code a draft brought back stays for the code editor, as code typed there does.
    const code = restoredCode()
    if (target) await load(target)
    if (code && !disposed && document()?.id === target) setRestoredCode(code)
  }
  return {
    history,
    unsaved,
    catalog,
    catalogGeneration,
    catalogError,
    refreshCatalog,
    diagnostics,
    document,
    dirty,
    preview,
    previewPending,
    previewError,
    error,
    conflict,
    conflictDraft,
    keepMine,
    saveCopy,
    discardChanges,
    restoreOffer,
    /** Restores the offered draft as one undo step; its unapplied code comes back through `restoredCode`. */
    restoreDraft: () => {
      const draft = restoreOffer()
      if (!draft) return
      batch(() => {
        // A new document takes over the draft another editing session left.
        if (offerKey && offerKey !== draftKey) holdKey(offerKey)
        ownsDraft = true
        setRestoreOffer(undefined)
        history.commit(draft.graph)
        if (draft.code) setRestoredCode(draft.code)
      })
    },
    /** Discards the offered draft. */
    dismissDraft: () => {
      setRestoreOffer(undefined)
      if (offerKey) removeDraft(offerKey)
      offerKey = undefined
    },
    restoredCode,
    /** The restored unapplied code, once: the code editor opens with it. */
    takeRestoredCode: () => {
      const code = restoredCode()
      setRestoredCode(undefined)
      return code
    },
    /** Leaving without saving: the draft is removed and none is kept until another document opens. */
    discardDraft: () => {
      clearTimeout(draftTimer)
      draftTimer = undefined
      draftDiscarded = true
      if (draftKey) removeDraft(draftKey)
    },
    /** A new document (no saved revision yet): starts keeping its draft, and offers one already kept. */
    openNew: () => {
      if (!document() && !draftKey) openNewDraft()
    },
    activationError,
    loading,
    saving,
    canSave,
    load,
    save,
    newDraft,
    retryPreview: () => setPreviewAttempt(value => value + 1),
  }
}
