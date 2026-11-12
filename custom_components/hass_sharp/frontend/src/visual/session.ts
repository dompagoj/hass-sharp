import { batch, createEffect, createMemo, createSignal, onCleanup } from 'solid-js'
import type { HomeAssistant } from '../types'
import { createGraphHistory, serializeGraph, type VisualGraph } from './graph'
import { visualApi, VisualApiError, visualError, type VisualDocument, type VisualPreview } from './api'
import { createUuid } from './id'

/** Owns graph/source pairs and their revisions. Never generates or persists source locally. */
export function createVisualSession(hass: Pick<HomeAssistant, 'callWS'>, initial: VisualGraph) {
  const api = visualApi(hass)
  const history = createGraphHistory(initial)
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
  let id = `visual-${createUuid()}`
  let generation = 0, loadGeneration = 0, identityGeneration = 0, disposed = false
  onCleanup(() => { disposed = true; generation++; loadGeneration++ })
  createEffect(() => {
    const graph = history.graph()
    previewAttempt()
    const current = ++generation
    batch(() => { setPreview(undefined); setPreviewError(undefined) })
    const timer = setTimeout(async () => {
      try {
        const result = await api.preview(graph)
        if (!disposed && current === generation) setPreview(result)
      } catch (err) {
        if (!disposed && current === generation) setPreviewError(visualError(err))
      }
    }, 300)
    onCleanup(() => { clearTimeout(timer); if (current === generation) generation++ })
  })
  const canSave = () => !loading() && !saving() && !readFailed() && !conflict() && !!preview()?.valid && !preview()?.diagnostics.some(d => d.severity === 3)
  const newDraft = (graph: VisualGraph) => {
    identityGeneration++; loadGeneration++
    id = `visual-${createUuid()}`
    batch(() => {
      history.reset(graph); setDocument(undefined); setBaseline(serializeGraph(graph))
      setError(undefined); setConflict(false); setReadFailed(false); setActivationError(undefined)
      setLoading(false); setSaving(false)
    })
  }
  const load = async (documentId: string) => {
    identityGeneration++; setSaving(false)
    const current = ++loadGeneration
    batch(() => { setLoading(true); setDocument(undefined); setError(undefined) })
    try {
      const saved = await api.read(documentId)
      if (disposed || current !== loadGeneration) return
      batch(() => {
        history.reset(saved.graph); setDocument(saved); setBaseline(serializeGraph(saved.graph))
        setConflict(false); setReadFailed(false); setActivationError(undefined)
      })
    } catch (err) {
      if (!disposed && current === loadGeneration) { setError(visualError(err)); setReadFailed(true) }
    } finally { if (!disposed && current === loadGeneration) setLoading(false) }
  }
  const save = async () => {
    if (!canSave()) return
    const snapshot = history.graph()
    const current = identityGeneration
    setSaving(true); setError(undefined)
    try {
      const result = await api.save(document()?.id ?? id, snapshot, document()?.revision ?? 0)
      if (disposed || current !== identityGeneration) return
      batch(() => {
        setDocument(result.document); setBaseline(serializeGraph(snapshot))
        setActivationError(result.active ? undefined : result.activationError ?? 'Runtime activation failed. Edit and save again, or reload the HassSharp integration.')
      })
      return result
    } catch (err) {
      if (disposed || current !== identityGeneration) return
      setError(visualError(err))
      if (err instanceof VisualApiError) {
        if (err.code === 'conflict') setConflict(true)
        if (err.code === 'validation' && serializeGraph(history.graph()) === serializeGraph(snapshot)) setPreview(err.detail as VisualPreview)
      }
    } finally { if (!disposed && current === identityGeneration) setSaving(false) }
  }
  return { history, document, dirty, preview, previewError, error, conflict, activationError, loading, saving, canSave, load, save, newDraft,
    retryPreview: () => setPreviewAttempt(value => value + 1) }
}
