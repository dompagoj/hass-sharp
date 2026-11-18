import { batch, createEffect, createMemo, createSignal, on, onCleanup, untrack } from 'solid-js'
import type { HomeAssistant } from '../types'
import type { VisualDiagnostic } from '../visual/api'
import type { JsonValue } from '../visual/contract'
import { entityTexts } from './adapter'
import { EntityApiError, entityApi, entityError, type EntityPreview } from './api'
import { definitionPollInterval, noteStarting } from './DefinitionCard'
import { definitionId, maskedSetting, type DefinitionBody, type DefinitionDevice, type DefinitionSetting, type EntityDocument, type EntityType, type HomeDefinition } from './model'

/**
 * What the class definition's page edits: its name, its class (a `types` id; empty while none is chosen), the values
 * of its settings (as `read` answers them: a masked one `value: null` with `hasValue`) and its device details.
 */
export interface ClassBody {
  name: string
  type: string
  settings: DefinitionSetting[]
  device: DefinitionDevice | null
}

const none = (value: unknown) => value === null || value === undefined

/**
 * The device a request sends, its empty details left out. A single entity's is the switch's: on, a device even with no
 * details (named after the definition); off, none. A device class always has its own, which these details override:
 * with none of them, none is sent. A class not known (gone) keeps what is stored.
 */
function deviceToSend(device: DefinitionDevice | null, kind: EntityType['kind'] | undefined): DefinitionDevice | null {
  if (!device) return null
  const kept = Object.fromEntries(Object.entries(device).filter(([, value]) => !none(value) && value !== '')) as DefinitionDevice
  return kind === 'device' && !Object.keys(kept).length ? null : kept
}

/** The body a document stands for, as the page edits it. */
const bodyOf = (document: EntityDocument): ClassBody => ({
  name: document.name,
  type: document.logic.kind === 'class' ? document.logic.type ?? '' : '',
  settings: structuredClone(document.settings ?? []),
  device: structuredClone(document.device ?? null),
})

/**
 * The definition a request sends: each setting the page has, a masked one nobody changed as `keep` (ruling R9), an
 * empty one as `value: null` (the class's default); a device without empty details, as the class's `kind` takes it.
 */
export function classRequest(body: ClassBody, kind?: EntityType['kind']): DefinitionBody {
  return {
    name: body.name,
    device: deviceToSend(body.device, kind),
    settings: body.settings.map(setting => (maskedSetting(setting) ? { key: setting.key, keep: true } : { key: setting.key, value: setting.value ?? null })),
    logic: { kind: 'class', type: body.type },
  }
}

/** What counts as a change: an empty value that was never saved is the same as no value at all. */
const comparable = (body: ClassBody, kind: EntityType['kind'] | undefined) => JSON.stringify({
  ...classRequest(body, kind),
  settings: body.settings.filter(s => !none(s.value) || s.hasValue === true).map(s => (maskedSetting(s) ? { key: s.key, keep: true } : { key: s.key, value: s.value })),
})

/** The members of a read that say how the definition runs, which a re-read takes even over unsaved changes. */
const runFields = ['enabled', 'active', 'currentRevisionActive', 'activationError', 'status', 'liveEntities', 'registeredEntities'] as const
const withRun = (document: EntityDocument, latest: EntityDocument): EntityDocument =>
  ({ ...document, ...Object.fromEntries(runFields.map(field => [field, latest[field]])) })

/** A number field whose text does not read: its setting, its label, and what to enter ("enter a whole number"). */
export interface UnreadableNumber { key: string; label: string; message: string; reason: string }

/** How a refused request is told: a conflict (which offers to reload or to save anyway), or a sentence. */
export interface ClassNotice { text: string; error?: boolean }

/**
 * The state of a class definition's page: the definition as last read or saved (`document`), what the page edits
 * (`body`), whether that differs from it (`dirty`), the server's problems (a debounced `preview` while editing, a
 * refused save's), and Save, turning on and off, conflicts and re-reads. Nothing is written to the browser's storage:
 * a class definition keeps no drafts.
 *
 * `types` are the classes the node libraries declare (undefined while not known). `onChanged` hears of every change
 * the server took (a save, a switch), for the lists to read again.
 */
export function createClassSession(
  hass: Pick<HomeAssistant, 'callWS'>,
  initial: { document?: EntityDocument; name?: string; type?: string },
  options: { types(): readonly EntityType[] | undefined; onChanged?(): void },
) {
  const api = entityApi(hass)
  const [document, setDocument] = createSignal<EntityDocument | undefined>(initial.document)
  const start: ClassBody = initial.document ? bodyOf(initial.document) : { name: initial.name ?? entityTexts.untitled, type: '', settings: [], device: null }
  const [body, setBody] = createSignal<ClassBody>(start)
  // What was last read or saved, compared as the class (once known) takes it.
  const [baseline, setBaseline] = createSignal<ClassBody>(start)
  // A new definition is named after its class until a name is typed (or the address gave one).
  let nameFromClass = !initial.document && !initial.name
  const [diagnostics, setDiagnostics] = createSignal<VisualDiagnostic[]>([])
  const [error, setError] = createSignal<string>()
  const [conflict, setConflict] = createSignal(false)
  const [saving, setSaving] = createSignal(false)
  const [switching, setSwitching] = createSignal(false)
  const [notice, setNotice] = createSignal<ClassNotice>()
  // Number fields whose text does not read as a number of their type, in the order they became so: nothing is saved
  // while there is one.
  const [unreadable, setUnreadable] = createSignal<UnreadableNumber[]>([])
  // Counts the reloads that discarded the page's changes: a field then shows what is saved, whatever was typed in it.
  const [reverted, setReverted] = createSignal(0)

  const type = createMemo(() => { const id = body().type; return id ? options.types()?.find(t => t.id === id) : undefined })
  const kind = () => type()?.kind
  const request = () => classRequest(body(), kind())
  const dirty = createMemo(() => comparable(body(), kind()) !== comparable(baseline(), kind()))

  /** Takes a read or saved definition as the one the page edits. */
  const accept = (latest: EntityDocument) => batch(() => {
    const next = bodyOf(latest)
    setDocument(latest)
    setBody(next)
    setBaseline(next)
    setConflict(false)
    setError(undefined)
  })

  const change = (update: (body: ClassBody) => ClassBody) => {
    setBody(update)
    // A refused save's message goes with the next change; a conflict stays until it is settled.
    if (!conflict()) setError(undefined)
  }
  const setting = (key: string) => body().settings.find(s => s.key === key)
  const putSetting = (key: string, update: (setting: DefinitionSetting | undefined) => DefinitionSetting) =>
    change(b => setting(key) ? { ...b, settings: b.settings.map(s => (s.key === key ? update(s) : s)) } : { ...b, settings: [...b.settings, update(undefined)] })

  // ---- The server's check, 300 ms after the last change, of a definition with a class.
  let previewGeneration = 0
  let previewTimer: ReturnType<typeof setTimeout> | undefined
  // The classes read again (a node library saved) are checked against too.
  const typesKey = () => JSON.stringify(options.types() ?? null)
  createEffect(on(() => [comparable(body(), kind()), typesKey()] as const, () => {
    clearTimeout(previewTimer)
    const current = ++previewGeneration
    if (!body().type) return
    previewTimer = setTimeout(() => void (async () => {
      let answer: EntityPreview | undefined
      try { answer = await api.preview(untrack(request)) } catch { /* A save checks again; nothing to show here. */ }
      if (current !== previewGeneration) return
      if (answer) setDiagnostics(answer.diagnostics ?? [])
    })(), 300)
  }))
  onCleanup(() => { clearTimeout(previewTimer); previewGeneration++ })

  const newId = async (name: string) => definitionId(name, (await api.list()).map(d => d.id))

  const refused = (failure: unknown) => {
    if (failure instanceof EntityApiError && failure.code === 'conflict' && document()) {
      setConflict(true)
      setError(entityTexts.conflictMessage)
      return
    }
    if (failure instanceof EntityApiError && failure.code === 'validation') {
      const detail = failure.detail as Partial<EntityPreview> | undefined
      previewGeneration++
      setDiagnostics(detail?.diagnostics ?? [])
      setError(failure.message)
      return
    }
    setError(entityError(failure))
  }

  /** Saves what the page has at `revision` (the saved one's, 0 for a new definition); a new one gets its id from its name. */
  const send = async (revision: number) => {
    const sent = request()
    const saved = document()
    let id = saved?.id ?? await newId(sent.name)
    let answer
    try {
      answer = await api.save(id, sent, revision)
    } catch (failure) {
      // A new definition's id taken meanwhile: the next free one, once.
      if (saved || !(failure instanceof EntityApiError) || failure.code !== 'conflict') throw failure
      const next = await newId(sent.name)
      if (next === id) throw failure
      id = next
      answer = await api.save(id, sent, 0)
    }
    accept(answer.document)
    setDiagnostics(answer.diagnostics ?? [])
    options.onChanged?.()
    return answer.document
  }
  const saveWith = async (revision: () => Promise<number>) => {
    if (saving() || unreadable().length) return
    setSaving(true)
    setError(undefined)
    try {
      return await send(await revision())
    } catch (failure) {
      refused(failure)
    } finally {
      setSaving(false)
    }
  }

  /**
   * Re-reads the saved definition: all of it with no unsaved changes. Under unsaved changes, a definition stored as
   * the page loaded it (a node library save rebuilds it at the next revision) is taken with its new revision, so Save
   * is no conflict; one changed elsewhere keeps the revision the page opened, and Save offers the choices.
   */
  const refresh = async () => {
    const saved = document()
    if (!saved) return
    let latest: EntityDocument
    try { latest = await api.read(saved.id) } catch { return }
    const current = document()
    if (current?.id !== saved.id) return
    if (!dirty() && !conflict() && !saving()) accept(latest)
    else if (!conflict() && comparable(bodyOf(latest), kind()) === comparable(bodyOf(current), kind())) setDocument(latest)
    else setDocument(withRun(current, latest))
  }

  // ---- While it is starting, read again as the entities page does: every 5 s for 2 minutes, then every 30 s.
  const since = new Map<string, number>()
  let pollTimer: ReturnType<typeof setTimeout> | undefined
  createEffect(on(() => { const d = document(); return d ? `${d.id}:${d.revision}:${d.enabled}:${d.status}` : '' }, () => {
    clearTimeout(pollTimer)
    const d = document()
    if (!d) return
    const listed = [{ ...d, recentRuns: [], runsToday: 0, failedLastDay: 0, lastRunUtc: null } as unknown as HomeDefinition]
    const now = Date.now()
    noteStarting(since, listed, now)
    if (!(d.enabled && d.status === 'setup')) return
    const wait = definitionPollInterval(listed, now, since)
    if (wait !== false) pollTimer = setTimeout(() => void refresh(), wait)
  }))
  onCleanup(() => clearTimeout(pollTimer))

  /**
   * Chooses the class (a new definition's, or another for one whose class is gone). The values saved for settings it
   * declares are kept; values typed here for settings it does not declare go, and saved ones stay until removed.
   */
  const setType = (chosen: EntityType) => change(b => ({
    ...b,
    name: nameFromClass ? chosen.title : b.name,
    type: chosen.id,
    settings: b.settings.filter(s => s.hasValue === true || chosen.settings.some(d => d.key === s.key)),
  }))

  return {
    document, body, dirty, type, diagnostics, error, conflict, saving, switching, notice, unreadable, reverted,
    setting,
    /** A number field's text reads (no `problem`) or does not. */
    markUnreadable(key: string, problem: Omit<UnreadableNumber, 'key'> | undefined) {
      setUnreadable(list => {
        const rest = list.filter(entry => entry.key !== key)
        return problem ? [...rest, { key, ...problem }] : rest.length === list.length ? list : rest
      })
    },
    setName(name: string) { nameFromClass = false; change(b => ({ ...b, name })) },
    setType,
    /** The class a new definition's address names, chosen before anything is edited: not a change of its own. */
    preset(chosen: EntityType) {
      setType(chosen)
      setBaseline(body())
    },
    /** A setting's value; null for the class's default. A masked value keeps `hasValue` (what is saved) as it is. */
    setValue(key: string, value: JsonValue | null) { putSetting(key, current => ({ ...(current ?? { key }), value })) },
    /** A masked value cleared: no value is kept (the class's default). */
    clearValue(key: string) { putSetting(key, current => ({ ...(current ?? { key }), value: null, hasValue: false })) },
    removeSetting(key: string) { change(b => ({ ...b, settings: b.settings.filter(s => s.key !== key) })) },
    setDevice(device: DefinitionDevice | null) { change(b => ({ ...b, device })) },
    save: () => saveWith(async () => document()?.revision ?? 0),
    /** After a conflict: mine over the revision saved since. */
    saveAnyway: () => saveWith(async () => (await api.read(document()!.id)).revision),
    /** After a conflict: the saved one, mine discarded. */
    async reload() {
      const saved = document()
      if (!saved) return
      try { accept(await api.read(saved.id)); setReverted(n => n + 1) } catch (failure) { setError(entityError(failure)) }
    },
    refresh,
    async setEnabled(enabled: boolean) {
      const saved = document()
      if (!saved || switching()) return
      setSwitching(true)
      setNotice(undefined)
      try {
        const answer = await api.setEnabled(saved.id, enabled)
        setDocument(current => (current ? { ...current, enabled: answer.enabled, active: answer.active, activationError: answer.activationError } : current))
        options.onChanged?.()
        await refresh()
      } catch (failure) {
        setNotice({ text: `Could not turn ${body().name} ${enabled ? 'on' : 'off'}: ${entityError(failure)}`, error: true })
      } finally {
        setSwitching(false)
      }
    },
    showNotice: setNotice,
  }
}
export type ClassSession = ReturnType<typeof createClassSession>
