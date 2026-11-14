import { batch, createEffect, createMemo, createSignal, onCleanup } from 'solid-js'
import type { HomeAssistant } from '../types'
import { createGraphHistory, serializeGraph, type VisualGraph } from './graph'
import { visualApi, VisualApiError, visualError, type VisualDocument, type VisualPreview } from './api'
import { nodeCatalog, type ServicesSection, type VisualCatalogSnapshot } from './contract'
import { connectionIndex } from './connectionIndex'
import { danglingWireIds } from './danglingWire'
import { createUuid } from './id'

/** Owns graph/source pairs and their revisions. Never generates or persists source locally. */
export function createVisualSession(hass: Pick<HomeAssistant, 'callWS'>, initial: VisualGraph) {
  const api = visualApi(hass)
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
  const [document, setDocument] = createSignal<VisualDocument>()
  const [baseline, setBaseline] = createSignal(serializeGraph(initial))
  const [preview, setPreview] = createSignal<VisualPreview>()
  const [previewError, setPreviewError] = createSignal<string>()
  const [error, setError] = createSignal<string>()
  const [conflict, setConflict] = createSignal(false)
  const [activationError, setActivationError] = createSignal<string>()
  const [loading, setLoading] = createSignal(false)
  const [saving, setSaving] = createSignal(false)
  const [readFailed, setReadFailed] = createSignal(false)
  const [previewAttempt, setPreviewAttempt] = createSignal(0)
  const dirty = createMemo(() => serializeGraph(history.graph()) !== baseline())
  const diagnostics = createMemo(() => {
    // The same validation the connection index of this graph snapshot already ran.
    const graph = history.graph(), analysis = connectionIndex(graph, catalog()).analysis
    // A problem about a wire whose pin is missing names that wire, so it can be removed from the problem.
    const wires = danglingWireIds(graph, analysis)
    const local = analysis.diagnostics.map((d, i) => ({ ...d, startLine: null, startColumn: null, ...(wires[i] ? { edgeId: wires[i] } : {}) }))
    const backend = preview()?.diagnostics ?? []
    // The server sends a missing input as null; local diagnostics leave it out.
    return [
      ...local,
      ...backend.filter(d => !local.some(other => other.nodeId === d.nodeId && (other.inputId ?? null) === (d.inputId ?? null) && other.message === d.message)),
    ]
  })
  let id = `visual-${createUuid()}`
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
        const latest = await api.read(opened.id)
        if (!disposed && request === catalogRequest && document()?.id === opened.id && document()?.revision === opened.revision) {
          checkedDocument = { id: opened.id, revision: opened.revision, generation: result.generation }
          if (latest.revision !== opened.revision)
            batch(() => {
              setConflict(true)
              setError('Node library changed this saved graph. Your local graph is preserved; reload the saved revision to continue.')
            })
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
        const result = await api.preview(graph)
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
  const canSave = () =>
    catalogReady() &&
    !diagnostics().some(d => d.severity === 3) &&
    !loading() &&
    !saving() &&
    !readFailed() &&
    !conflict() &&
    !!preview()?.valid &&
    !preview()?.diagnostics.some(d => d.severity === 3)
  const newDraft = (graph: VisualGraph) => {
    identityGeneration++
    loadGeneration++
    id = `visual-${createUuid()}`
    batch(() => {
      history.reset(graph)
      setDocument(undefined)
      setBaseline(serializeGraph(graph))
      setError(undefined)
      setConflict(false)
      setReadFailed(false)
      setActivationError(undefined)
      setLoading(false)
      setSaving(false)
    })
  }
  const load = async (documentId: string) => {
    identityGeneration++
    setSaving(false)
    const current = ++loadGeneration
    batch(() => {
      setLoading(true)
      setDocument(undefined)
      setError(undefined)
    })
    try {
      const saved = await api.read(documentId)
      if (disposed || current !== loadGeneration) return
      if (saved.graph.version !== nodeCatalog.graphVersion)
        throw new Error(`Unsupported graph version ${saved.graph.version}; only version ${nodeCatalog.graphVersion} is supported`)
      batch(() => {
        history.reset(saved.graph)
        setDocument(saved)
        setBaseline(serializeGraph(saved.graph))
        setConflict(false)
        setReadFailed(false)
        setActivationError(saved.activationError ?? undefined)
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
      const result = await api.save(document()?.id ?? id, snapshot, document()?.revision ?? 0)
      if (disposed || current !== identityGeneration) return
      batch(() => {
        setDocument(result.document)
        setBaseline(serializeGraph(snapshot))
        setActivationError(
          result.active
            ? undefined
            : (result.activationError ?? 'Runtime activation failed. Edit and save again, or reload the HassSharp integration.'),
        )
      })
      return result
    } catch (err) {
      if (disposed || current !== identityGeneration) return
      setError(visualError(err))
      if (err instanceof VisualApiError) {
        if (err.code === 'conflict') setConflict(true)
        if (err.code === 'validation' && serializeGraph(history.graph()) === serializeGraph(snapshot))
          setPreview(err.detail as VisualPreview)
      }
    } finally {
      if (!disposed && current === identityGeneration) setSaving(false)
    }
  }
  return {
    history,
    catalog,
    catalogGeneration,
    catalogError,
    refreshCatalog,
    diagnostics,
    document,
    dirty,
    preview,
    previewError,
    error,
    conflict,
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
