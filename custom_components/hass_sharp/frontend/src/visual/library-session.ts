import { batch, createMemo, createSignal, onCleanup } from 'solid-js'
import { LibraryApiError, librariesChanged, type LibraryApi, type LibraryContext, type LibraryDocument, type LibraryPreview, type LibraryReply, type LibraryActivation, type LibraryValidationDetail } from './library-api'

export const libraryTemplate = `public static class Nodes
{
    /// <summary>Create a reusable node.</summary>
    /// <param name="text">The input text.</param>
    /// <returns>The resulting text.</returns>
    [Node("My node", Category = "Custom")]
    public static string MyNode([Port("Text")] string text) => text;
}`
interface Draft extends LibraryDocument { baseline: LibraryDocument; conflict?: boolean }
export function createLibrarySession(api: LibraryApi) {
  const [files, setFiles] = createSignal<Draft[]>([])
  const [selectedId, setSelectedId] = createSignal<string>()
  const selected = createMemo(() => files().find(f => f.id === selectedId()))
  const [generation, setGeneration] = createSignal('')
  const [workspaceVersion, setWorkspaceVersion] = createSignal('0')
  const [preview, setPreview] = createSignal<LibraryPreview>()
  const [sourceGeneration, setSourceGeneration] = createSignal<string>()
  const [error, setError] = createSignal<string>()
  const [validation, setValidation] = createSignal<LibraryValidationDetail>()
  const [loading, setLoading] = createSignal(true)
  const [previewing, setPreviewing] = createSignal(false)
  const [saving, setSaving] = createSignal(false)
  const [activations, setActivations] = createSignal<LibraryActivation[]>([])
  const dirtyFile = (f: Draft) => f.revision === 0 || f.source !== f.baseline.source || f.name !== f.baseline.name
  const dirty = createMemo(() => files().some(dirtyFile))
  const conflict = () => selected()?.conflict === true
  let disposed = false, notifying = false, refreshQueued = false, version = 0, loadRequest = 0
  let timer: ReturnType<typeof setTimeout> | undefined
  const context = (): LibraryContext | undefined => {
    const file = selected(); if (!file) return
    return { id: file.id, source: file.source, workspaceVersion: workspaceVersion(), generation: generation(), sources: files().filter(f => f.id !== file.id && dirtyFile(f)).map(({ id, source }) => ({ id, source })) }
  }
  const current = (c: LibraryContext) => !disposed && c.id === selectedId() && c.workspaceVersion === workspaceVersion() && c.generation === generation()
  const accepts = <T,>(reply: LibraryReply<T>, c: LibraryContext) => current(c) && reply.saved === false && reply.id === c.id && reply.workspaceVersion === c.workspaceVersion && reply.generation === c.generation && typeof reply.sourceGeneration === 'string' && (!sourceGeneration() || sourceGeneration() === reply.sourceGeneration)
  const runPreview = async () => {
    clearTimeout(timer)
    const c = context(); if (!c || !generation()) { setPreviewing(false); return false }
    setPreviewing(true)
    try {
      const reply = await api.preview(c)
      if (!accepts(reply, c)) return false
      batch(() => { setPreview(reply.data); setSourceGeneration(reply.sourceGeneration); setPreviewing(false) })
      return true
    } catch (e) {
      if (!current(c)) return false
      batch(() => { setError(String(e)); setPreviewing(false); if (e instanceof LibraryApiError && e.code === 'conflict') setFiles(list => list.map(f => f.id === c.id ? { ...f, conflict: true } : f)) })
      return false
    }
  }
  const invalidate = () => {
    batch(() => { setWorkspaceVersion(String(++version)); setPreview(undefined); setSourceGeneration(undefined); setValidation(undefined); setError(undefined); setPreviewing(!!selected()) })
    clearTimeout(timer); timer = setTimeout(() => void runPreview(), 300)
  }
  const refresh = async () => {
    if (saving()) { refreshQueued = true; return }
    const request = ++loadRequest
    try {
      const result = await api.list()
      if (disposed || request !== loadRequest) return
      batch(() => {
        const existing = files()
        setFiles([...result.documents.map(document => {
          const draft = existing.find(f => f.id === document.id)
          return draft && dirtyFile(draft) ? { ...draft, conflict: draft.revision !== document.revision || draft.conflict } : { ...document, baseline: document }
        }), ...existing.filter(f => !result.documents.some(d => d.id === f.id) && dirtyFile(f)).map(f => ({ ...f, conflict: f.revision > 0 || f.conflict }))])
        const changed = generation() !== result.generation
        setGeneration(result.generation)
        if (!selected()) setSelectedId(files()[0]?.id)
        setLoading(false)
        if (changed || workspaceVersion() === '0') invalidate()
      })
    } catch (e) { if (!disposed && request === loadRequest) { setError(String(e)); setLoading(false) } }
  }
  const select = (id: string) => { if (selectedId() !== id && files().some(f => f.id === id)) { setSelectedId(id); invalidate() } }
  const edit = (source: string) => { if (selected()?.source !== source) { setFiles(list => list.map(f => f.id === selectedId() ? { ...f, source } : f)); invalidate() } }
  const rename = (name: string) => { if (selected()?.name !== name) { setFiles(list => list.map(f => f.id === selectedId() ? { ...f, name } : f)); invalidate() } }
  const validName = () => !!selected()?.name.trim() && selected()!.name.length <= 120 && !/[\/\\\0\r\n]/.test(selected()!.name)
  const canSave = () => !!selected() && validName() && !conflict() && !saving() && !!preview()?.valid && !previewing() && !error() && !preview()?.diagnostics.some(d => d.severity === 3)
  const mutationError = (e: unknown, id: string) => {
    setError(e instanceof Error ? e.message : String(e))
    if (e instanceof LibraryApiError) {
      if (e.code === 'conflict') setFiles(list => list.map(f => f.id === id ? { ...f, conflict: true } : f))
      if (e.code === 'validation' && typeof e.detail === 'object') setValidation(e.detail as LibraryValidationDetail)
    }
  }
  const save = async () => {
    if (!canSave()) return
    const submitted = { ...selected()! }; ++loadRequest; setSaving(true); setError(undefined)
    try {
      const result = await api.save(submitted)
      if (disposed) return
      batch(() => {
        if (result.document) {
          const saved = result.document
          setFiles(list => list.map(f => f.id === submitted.id ? { ...f, revision: saved.revision, baseline: saved, conflict: false } : f))
        }
        setGeneration(result.generation); setActivations(result.automations); invalidate()
      })
      notifying = true
      try { librariesChanged() } finally { notifying = false }
    } catch (e) { if (!disposed) mutationError(e, submitted.id) }
    finally { if (!disposed) { setSaving(false); if (refreshQueued) { refreshQueued = false; void refresh() } } }
  }
  const validateAndSave = async () => {
    const intended = selected()
    const captured = context()
    if (!intended || !captured) return
    if (!await runPreview() || !current(captured) || selected()?.revision !== intended.revision) return
    await save()
  }
  const remove = async () => {
    const f = selected(); if (!f || saving()) return
    if (!f.revision) { setFiles(list => list.filter(d => d.id !== f.id)); setSelectedId(files()[0]?.id); invalidate(); return }
    ++loadRequest; setSaving(true); setError(undefined)
    try {
      const result = await api.delete(f.id, f.revision)
      if (disposed) return
      batch(() => {
        setFiles(list => list.flatMap(d => d.id !== f.id ? [d] : d.source !== f.source || d.name !== f.name ? [{ ...d, revision: 0, baseline: { ...d, revision: 0 }, conflict: false }] : []))
        if (!selected()) setSelectedId(files()[0]?.id)
        setGeneration(result.generation); setActivations(result.automations); invalidate()
      })
      notifying = true
      try { librariesChanged() } finally { notifying = false }
    } catch (e) { if (!disposed) mutationError(e, f.id) }
    finally { if (!disposed) { setSaving(false); if (refreshQueued) { refreshQueued = false; void refresh() } } }
  }
  const reload = async (id: string) => {
    const requestVersion = workspaceVersion()
    const isCurrent = () => !disposed && workspaceVersion() === requestVersion
    const applyReload = async (document?: LibraryDocument) => {
      ++loadRequest // Ignore older file lists that could resurrect the discarded draft.
      batch(() => {
        setFiles(list => document ? list.map(f => f.id === id ? { ...document, baseline: document } : f) : list.filter(f => f.id !== id))
        if (!selected()) setSelectedId(files()[0]?.id)
        invalidate()
      })
      await refresh()
    }
    try {
      const document = await api.read(id)
      if (!isCurrent()) return
      await applyReload(document)
    } catch (e) {
      if (!isCurrent()) return
      // Reload is the confirmed per-file discard action. Authoritative absence
      // discards that draft as well; later edits/navigation revoke the confirmation.
      if (e instanceof LibraryApiError && e.code === 'not_found') await applyReload()
      else setError(String(e))
    }
  }
  const create = (name = 'Nodes.cs') => {
    const document: LibraryDocument = { version: 1, id: `library-${crypto.randomUUID()}`, name, revision: 0, source: libraryTemplate }
    setFiles(list => [...list, { ...document, baseline: document }]); setSelectedId(document.id); invalidate()
  }
  const onChanged = () => { if (!notifying) { invalidate(); void refresh() } }
  window.addEventListener('hass-sharp-libraries-changed', onChanged)
  const onFocus = () => { if (!saving()) void refresh() }
  window.addEventListener('focus', onFocus)
  onCleanup(() => { disposed = true; ++loadRequest; clearTimeout(timer); window.removeEventListener('hass-sharp-libraries-changed', onChanged); window.removeEventListener('focus', onFocus) })
  void refresh()
  return { files, selected, selectedId, select, edit, rename, create, remove, save, validateAndSave, reload, refresh, invalidate, dirty, dirtyFile, conflict, canSave, preview, previewing, validatePreview: runPreview, diagnostics: () => validation()?.diagnostics ?? preview()?.diagnostics ?? [], validation, error, loading, saving, activations, workspaceVersion, generation, sourceGeneration, context, accepts }
}
export type LibrarySession = ReturnType<typeof createLibrarySession>
