import { createEffect, createMemo, createSignal, createUniqueId, For, Match, on, onCleanup, Show, Switch, untrack, type JSX } from 'solid-js'
import { A, useNavigate } from '@solidjs/router'
import { useQuery, useQueryClient } from '@tanstack/solid-query'
import { EnabledSwitch } from '../automations/EnabledSwitch'
import { Button, Icon } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { LeaveGuard } from '../components/LeaveGuard'
import { useHass } from '../context'
import type { VisualDiagnostic } from '../visual/api'
import { nodeCatalog, type JsonValue } from '../visual/contract'
import { Glyph } from '../visual/Glyph'
import { icons } from '../visual/icons'
import { libraryHref } from '../visual/library-api'
import { saveChip } from '../visual/TopBar'
import { describedBy } from '../visual/widgets/common'
import { entityTexts } from './adapter'
import { definitionsQueryKey, entityApi, entityError, entityTypesQueryKey } from './api'
import { createClassSession, maskedValue, type ClassSession } from './classSession'
import { cardEntities, definitionStatus } from './DefinitionCard'
import { DeleteDefinition } from './DeleteDefinition'
import { problemRow } from './definitionEdits'
import { deviceMemory, DeviceSection } from './DeviceForm'
import { FieldErrors } from './formFields'
import type { DefinitionSetting, EntityDocument, EntityType, EntityTypeSetting } from './model'
import { entityTypeLabel } from './NodePickers'
import { SecretValue } from './SettingRows'

/** `library:<source id>:<class>`: the node library that declared a class, and the class's own name. */
const typeParts = (id: string) => {
  const match = /^library:([^:]+):(.+)$/.exec(id)
  return match ? { sourceId: match[1], name: match[2].split('.').at(-1) ?? match[2] } : { sourceId: undefined, name: id }
}
/** What an `int` setting takes anyway: a range this wide is not worth a hint. */
const intRange = (setting: EntityTypeSetting) => (setting.minimum ?? -Infinity) <= -2147483648 && (setting.maximum ?? Infinity) >= 2147483647
const kindLabel = (type: EntityType) => (type.kind === 'device' ? 'Device' : 'Single entity')

/**
 * The page of an entity definition built from a class in a node library (decision 8): its name, its class (chosen
 * for a new definition; for a saved one fixed, unless the class is gone), the class's settings, the device details,
 * the entities it made, its status, and Save, on/off and Delete. No canvas, no runs, no generated C#.
 *
 * `document`: the saved definition as read (absent for a new one, which `name` and `type` start from). `onSaved`
 * hears of each save; the first gives the definition its id.
 */
export function ClassDefinitionEditor(props: { document?: EntityDocument; name?: string; type?: string; onSaved(document: EntityDocument): void }) {
  const hass = useHass()
  const api = entityApi(hass)
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const types = useQuery(() => ({ queryKey: [...entityTypesQueryKey], queryFn: () => api.types(), staleTime: 0 }))
  const typeList = () => (types.isSuccess ? types.data.types : undefined)
  const session = createClassSession(hass, untrack(() => ({ document: props.document, name: props.name })), {
    types: typeList,
    onChanged: () => void queryClient.invalidateQueries({ queryKey: [...definitionsQueryKey] }),
  })
  const body = session.body
  const saved = session.document

  // The class the address names, once the classes are known.
  let preset = !!untrack(() => props.type) && !untrack(saved)
  createEffect(() => {
    const list = typeList()
    if (!preset || !list) return
    preset = false
    const wanted = list.find(t => t.id === untrack(() => props.type))
    if (wanted) session.preset(wanted)
  })

  // A node library saved: the classes may be new, and the definition rebuilt (a new revision, a new status).
  const librariesChanged = () => {
    void queryClient.invalidateQueries({ queryKey: [...entityTypesQueryKey] })
    void session.refresh()
  }
  window.addEventListener('hass-sharp-libraries-changed', librariesChanged)
  onCleanup(() => window.removeEventListener('hass-sharp-libraries-changed', librariesChanged))

  /** The saved definition's class is not one the node libraries offer now: another may be chosen. */
  const missing = createMemo(() => {
    const document = saved(), list = typeList()
    if (!document || !list || document.logic.kind !== 'class') return false
    const stored = document.logic.type
    return !list.some(t => t.id === stored)
  })
  const type = session.type
  const declared = (key: string) => !!type()?.settings.some(s => s.key === key)
  /** Saved values of settings the chosen class does not declare. */
  const unused = createMemo(() => (type() ? body().settings.filter(s => !declared(s.key)) : []))

  // The server's problems: a setting's on its field (by key), the rest at the top.
  const problems = createMemo(() => {
    const fields = new Map<string, string[]>(), others: VisualDiagnostic[] = []
    for (const diagnostic of session.diagnostics()) {
      if (diagnostic.nodeId || diagnostic.severity <= 0) continue
      // The page explains a class that is gone itself.
      if (diagnostic.code === 'definition.unknownType' && (missing() || !body().type)) continue
      const row = problemRow(diagnostic), key = diagnostic.inputId ?? ''
      // On the field of a setting the class declares, or on the row of a value it no longer uses.
      if (row === `setting:${key}` && (declared(key) || body().settings.some(s => s.key === key))) fields.set(key, [...(fields.get(key) ?? []), diagnostic.message])
      else others.push(diagnostic)
    }
    return { fields, others }
  })
  const errorCount = () => session.diagnostics().filter(d => d.severity >= 3).length

  const busy = () => session.saving() || session.switching()
  /** Why Save does nothing, if it does nothing. */
  const saveReason = () => {
    if (session.conflict()) return entityTexts.conflictSaveTitle
    if (types.isError) return 'The classes could not be loaded'
    if (!typeList()) return 'Loading the classes…'
    if (!type()) return missing() ? 'Choose another class first: this one is not available' : 'Choose a class first'
    if (saved() && !session.dirty()) return 'Nothing to save: every change is saved'
  }
  const [conflictOpen, setConflictOpen] = createSignal(false)
  createEffect(on(session.conflict, conflicted => setConflictOpen(conflicted)))
  const saved$ = (document: EntityDocument | undefined) => { if (document) props.onSaved(document) }
  const save = () => {
    if (session.conflict()) return setConflictOpen(true)
    if (saveReason()) return
    void session.save().then(saved$)
  }

  const status = createMemo(() => { const document = saved(); return document ? definitionStatus(document) : undefined })
  const entities = createMemo(() => { const document = saved(); return document ? cardEntities(document) : [] })
  const memory = deviceMemory()
  const showInHomeAssistant = (event: MouseEvent, entityId: string) =>
    (event.currentTarget as HTMLElement).dispatchEvent(new CustomEvent('hass-more-info', { detail: { entityId }, bubbles: true, composed: true }))
  const pickerName = createUniqueId()

  return (
    <section class="hs-cdef" aria-label="Entity definition">
      <ClassTopBar name={body().name} onRename={session.setName} onBack={() => navigate('/entities')} busy={busy()}
        save={{ saving: session.saving(), dirty: session.dirty(), errors: errorCount(), revision: saved()?.revision }}
        saveReason={saveReason()} onSave={save}
        enabled={
          <Show when={saved()}>
            {document => (
              <EnabledSwitch class="hs-topbar__enabled" name={body().name} enabled={document().enabled} disabled={session.saving()} busy={session.switching()}
                titles={{ on: entityTexts.enabledOn, off: entityTexts.enabledOff }} onChange={wanted => void session.setEnabled(wanted)} />
            )}
          </Show>
        } />
      <div class="hs-cdef__body" inert={session.saving()} aria-busy={session.saving() || undefined}>
        <Show when={session.notice()}>
          {notice => <p class="hs-notice hs-cdef-notice" classList={{ 'hs-notice--danger': !!notice().error }} role={notice().error ? 'alert' : 'status'}>{notice().text}</p>}
        </Show>
        <Show when={session.error()}>
          <div class="hs-notice hs-notice--danger hs-cdef-notice" role="alert">
            {session.error()}
            <Show when={session.conflict()}>
              <p>Your changes are still here. <Button onClick={() => setConflictOpen(true)}>Choose what to keep…</Button></p>
            </Show>
          </div>
        </Show>
        <Show when={problems().others.length}>
          <ul class="hs-def-problems" aria-label="Problems of this entity definition">
            <For each={problems().others}>
              {problem => (
                <li classList={{ 'hs-def-problems__item': true, 'hs-def-problems__item--warning': problem.severity < 3 }}>
                  <Glyph paths={problem.severity >= 3 ? icons.problem : icons.warning} />
                  <span>{problem.message}</span>
                </li>
              )}
            </For>
          </ul>
        </Show>
        <Show when={status()}>
          {current => (
            <div class="hs-cdef-status">
              <p class={`hs-ent-card__status hs-ent-card__status--${current().tone}`} data-definition-status role="status">{current().label}</p>
              <Show when={current().error}>
                {error => <p class="hs-cdef-status__line hs-cdef-status__line--error"><Glyph paths={icons.problem} /><span>{error()}</span></p>}
              </Show>
              <Show when={current().stale}>
                {stale => <p class="hs-cdef-status__line hs-cdef-status__line--stale"><Glyph paths={icons.warning} /><span>{stale()}</span></p>}
              </Show>
            </div>
          )}
        </Show>

        <section class="hs-def-section hs-cdef-class" aria-label="Class">
          <h2 class="hs-insp-heading">Class</h2>
          <Switch>
            <Match when={types.isError}>
              <div class="hs-notice hs-notice--danger hs-cdef-notice" role="alert">
                <p>Could not load the classes: {entityError(types.error)}</p>
                <Button onClick={() => void types.refetch()}>Try again</Button>
              </div>
            </Match>
            <Match when={!typeList()}>
              <p class="hs-muted" role="status">Loading the classes…</p>
            </Match>
            <Match when={saved() && !missing() && type()}>
              {chosen => <ChosenClass type={chosen()} />}
            </Match>
            <Match when={!missing() && typeList()?.length === 0}>
              <p>No classes yet. Write a class that derives from SharpDevice or SharpEntity in a node library, and it appears here.</p>
              <p><A href="/libraries">Open the node library</A></p>
            </Match>
            <Match when={true}>
              <Show when={missing() && saved()}>
                {document => <MissingClass document={document()} />}
              </Show>
              <Show when={typeList()?.length} fallback={<p class="hs-muted">No other class is offered by the node libraries now.</p>}>
                <fieldset class="hs-ent-choices hs-cdef-picker">
                  <legend>{missing() ? 'Choose another class' : 'Choose a class'}</legend>
                  <For each={typeList()}>
                    {option => (
                      <label class="hs-ent-choice">
                        <input type="radio" name={pickerName} value={option.id} checked={body().type === option.id}
                          onChange={event => { if (event.currentTarget.checked) session.setType(option) }} />
                        <span class="hs-ent-choice__text">
                          <strong>{option.title}</strong>
                          <Show when={option.description}><small>{option.description}</small></Show>
                          <small>{kindLabel(option)}</small>
                        </span>
                      </label>
                    )}
                  </For>
                </fieldset>
                <Show when={type()}>{chosen => <p><A href={libraryHref(chosen().sourceId)}>Open the class in the node library</A></p>}</Show>
              </Show>
            </Match>
          </Switch>
        </section>

        <Show when={type()}>
          {chosen => <>
            <SettingsSection type={chosen()} session={session} problems={problems().fields} unused={unused()} />
            <Show when={chosen().kind === 'device'} fallback={
              <DeviceSection device={body().device} onChange={session.setDevice} memory={memory} savedId={saved()?.id} savedDevice={saved()?.device}
                toggle={{ label: 'Group this entity as a device', hint: 'Home Assistant shows it on a device page of its own, named after this entity definition.' }} />
            }>
              <DeviceSection device={body().device} onChange={session.setDevice} fixed={{ hint: 'Leave a field empty to use what the class sets.', placeholder: 'Set by the class' }} />
            </Show>
          </>}
        </Show>

        <section class="hs-def-section" aria-label="Entities">
          <h2 class="hs-insp-heading">Entities</h2>
          <Show when={entities().length} fallback={<p class="hs-muted">Its entities appear after it has started once.</p>}>
            <ul class="hs-def-rows">
              <For each={entities()}>
                {entity => (
                  <li class="hs-def-row">
                    <div class="hs-cdef-entity">
                      <span class="hs-def-row__icon" aria-hidden="true"><Glyph paths={icons.entity} /></span>
                      <span class="hs-def-row__title">
                        <strong>{entity.name}</strong>
                        <small>{entityTypeLabel(nodeCatalog, entity.domain)}{entity.disabled ? ' · Disabled in Home Assistant' : ''}</small>
                      </span>
                    </div>
                    <Show when={entity.entityId}>
                      {entityId => (
                        <p class="hs-def-row__id">
                          <code>{entityId()}</code>
                          <button type="button" class="hs-button hs-button--ghost hs-def-row__show" aria-label={`Show ${entityId()} in Home Assistant`}
                            onClick={event => showInHomeAssistant(event, entityId())}>Show</button>
                        </p>
                      )}
                    </Show>
                  </li>
                )}
              </For>
            </ul>
          </Show>
        </section>

        <Show when={saved()}>
          {document => (
            <DeleteDefinition document={document()} name={body().name} blocked={session.dirty() || busy() || session.conflict()}
              hint={session.dirty() ? 'Save your changes before deleting it.' : undefined} onError={text => session.showNotice({ text, error: true })} />
          )}
        </Show>
      </div>

      <Dialog open={session.conflict() && conflictOpen()} title={entityTexts.conflictTitle} width="medium" onClose={() => setConflictOpen(false)}
        footer={<>
          <Button variant="danger" disabled={busy()} onClick={() => { setConflictOpen(false); void session.reload() }}>Reload</Button>
          <Button variant="primary" disabled={busy()} onClick={() => { setConflictOpen(false); void session.saveAnyway().then(saved$) }}>Save anyway</Button>
        </>}>
        <p>{entityTexts.conflictElsewhere}</p>
        <p><strong>Save anyway</strong> saves your changes over the saved one. <strong>Reload</strong> discards your changes and opens the saved version.</p>
      </Dialog>
      <LeaveGuard dirty={session.dirty()} />
    </section>
  )
}

/**
 * The page's top bar, in the editor's (TopBar's) look: back, the name as the title field, the save state, the on/off
 * switch and Save. A name left blank goes back to the one it had when the field was entered; so does Escape.
 */
function ClassTopBar(props: {
  name: string
  onRename(name: string): void
  onBack(): void
  busy: boolean
  save: { saving: boolean; dirty: boolean; errors: number; revision?: number }
  /** Why Save does nothing: Save is then marked unavailable, and says why. */
  saveReason?: string
  onSave(): void
  enabled?: JSX.Element
}) {
  let before = ''
  const chip = () => saveChip(props.save)
  const explanationId = createUniqueId(), reasonId = createUniqueId()
  const restore = (input: HTMLInputElement) => { input.value = before; if (before !== props.name) props.onRename(before) }
  return (
    <header class="hs-topbar">
      <button type="button" class="hs-topbar__icon" aria-label={entityTexts.back} title={entityTexts.back} onClick={() => props.onBack()}>
        <Icon name="arrow" />
      </button>
      <h1 class="hs-visually-hidden">{props.name}</h1>
      <div class="hs-topbar__title">
        <input class="hs-topbar__name" aria-label={entityTexts.nameLabel} title={entityTexts.nameLabel} spellcheck={false} autocomplete="off"
          enterkeyhint="done" maxLength={120} value={props.name} disabled={props.busy}
          onFocus={() => (before = props.name)}
          onInput={event => { const text = event.currentTarget.value; if (text.trim() && text !== props.name) props.onRename(text) }}
          onKeyDown={event => {
            if (event.key === 'Enter') { event.preventDefault(); event.currentTarget.blur() }
            else if (event.key === 'Escape' && event.currentTarget.value !== before) { event.preventDefault(); event.stopPropagation(); restore(event.currentTarget) }
          }}
          onBlur={event => { if (!event.currentTarget.value.trim()) restore(event.currentTarget) }} />
      </div>
      <span class={`hs-save-chip hs-save-chip--${chip().tone}`} tabindex="0" title={chip().explanation} aria-describedby={explanationId}>
        <span class="hs-save-chip__text" role="status">{chip().text}</span>
        <span class="hs-save-chip__short" aria-hidden="true">{chip().short}</span>
        <span id={explanationId} hidden>{chip().explanation}</span>
      </span>
      <div class="hs-topbar__actions">
        <div class="hs-topbar__run-group">{props.enabled}</div>
        {/* Unavailable with a reason: marked, not disabled, so it stays in the tab order with its reason, as the editor's is. */}
        <Button variant="primary" class="hs-topbar__save" busy={props.save.saving} aria-disabled={props.saveReason ? 'true' : undefined}
          title={props.saveReason} aria-describedby={props.saveReason ? reasonId : undefined} onClick={() => props.onSave()}>
          Save
        </Button>
        <Show when={props.saveReason}><span id={reasonId} class="hs-visually-hidden">{props.saveReason}</span></Show>
      </div>
    </header>
  )
}

/** A saved definition's class: what it is, and a way to its code. */
function ChosenClass(props: { type: EntityType }) {
  return (
    <div class="hs-cdef-chosen">
      <p><strong>{props.type.title}</strong> <span class="hs-muted">· {kindLabel(props.type)}</span></p>
      <Show when={props.type.description}><p class="hs-muted">{props.type.description}</p></Show>
      <p><A href={libraryHref(props.type.sourceId)}>Open the class in the node library</A></p>
    </div>
  )
}

/** The saved definition's class is gone: why, where it was, and which settings have values (by key only). */
function MissingClass(props: { document: EntityDocument }) {
  const parts = () => typeParts(props.document.logic.kind === 'class' ? props.document.logic.type : '')
  return (
    <div class="hs-cdef-missing" role="note">
      <p><strong>This entity definition's class is not available: it was removed, or its node library does not compile.</strong></p>
      <p>
        It was built from <code>{parts().name}</code>.{' '}
        <A href={parts().sourceId ? libraryHref(parts().sourceId!) : '/libraries'}>Open the node library</A>
      </p>
      <Show when={props.document.settings.length}>
        <p>Its saved settings, kept as they are:</p>
        <ul class="hs-cdef-keys" aria-label="Saved settings">
          <For each={props.document.settings}>{setting => <li><code>{setting.key}</code></li>}</For>
        </ul>
      </Show>
    </div>
  )
}

/** The class's settings, one field each in the class's order, and the saved values of settings it no longer has. */
function SettingsSection(props: { type: EntityType; session: ClassSession; problems: Map<string, string[]>; unused: DefinitionSetting[] }) {
  return (
    <section class="hs-def-section" aria-label="Settings">
      <h2 class="hs-insp-heading">Settings</h2>
      <Show when={props.type.settings.length} fallback={<p class="hs-muted">This class has no settings.</p>}>
        <div class="hs-cdef-fields">
          <For each={props.type.settings}>
            {setting => <SettingField setting={setting} session={props.session} errors={props.problems.get(setting.key) ?? []} />}
          </For>
        </div>
      </Show>
      <Show when={props.unused.length}>
        <section class="hs-cdef-unused" aria-label="No longer used by this class">
          <h3>No longer used by this class</h3>
          <p class="hs-field-hint">Values saved for settings this class does not have. Remove them to save.</p>
          <ul class="hs-def-rows">
            <For each={props.unused}>
              {setting => (
                <li class="hs-def-row hs-cdef-unused__row">
                  <div class="hs-cdef-unused__head">
                    <code>{setting.key}</code>
                    <Button variant="danger" aria-label={`Remove setting ${setting.key}`} onClick={() => props.session.removeSetting(setting.key)}>Remove</Button>
                  </div>
                  <FieldErrors list={props.problems.get(setting.key) ?? []} />
                </li>
              )}
            </For>
          </ul>
        </section>
      </Show>
    </section>
  )
}

/**
 * One setting of the class. Empty, it keeps the class's default (`value: null`). A secret, or a value the page was not
 * sent, is the definition form's secret field. The server checks the values; its problems are shown under the field.
 */
function SettingField(props: { setting: EntityTypeSetting; session: ClassSession; errors: string[] }) {
  const id = createUniqueId(), hintId = `${id}-hint`, errorsId = `${id}-errors`
  const key = () => props.setting.key
  const label = () => props.setting.label || key()
  const stored = () => props.session.setting(key())
  const value = () => stored()?.value
  const set = (next: JsonValue | null) => props.session.setValue(key(), next)
  const range = () => {
    const { minimum, maximum } = props.setting
    if (props.setting.type !== 'integer' || minimum === undefined || maximum === undefined || intRange(props.setting)) return
    return `Between ${minimum} and ${maximum}.`
  }
  const hint = () => [range(), props.setting.description].filter(Boolean).join(' ')
  const invalid = () => props.errors.length > 0
  const describe = () => describedBy(!!hint() && hintId, invalid() && errorsId)
  const text = () => (typeof value() === 'string' ? value() as string : typeof value() === 'number' || typeof value() === 'boolean' ? String(value()) : '')
  const number = (typed: string): JsonValue | null => {
    if (!typed.trim()) return null
    const parsed = Number(typed)
    // The server says what a value that is not a number should be.
    return Number.isFinite(parsed) ? parsed : typed
  }
  const select = (choices: { value: string; label: string }[], current: string, onChange: (value: string) => void) => (
    <select id={id} class="hs-input" aria-invalid={invalid() || undefined} aria-describedby={describe()}
      onChange={event => onChange(event.currentTarget.value)}>
      <For each={choices}>{choice => <option value={choice.value} selected={choice.value === current}>{choice.label}</option>}</For>
    </select>
  )
  const secretType = () => (props.setting.type === 'integer' || props.setting.type === 'number' ? 'number' : props.setting.type === 'boolean' ? 'boolean' : 'string')
  return (
    <Show when={!props.setting.secret && !maskedValue(stored())} fallback={
      <SecretValue type={secretType()} setting={stored() ?? { key: key(), value: null }} name={`setting:${key()}`} label={label()}
        note={props.setting.description ?? ''} errors={props.errors}
        onChange={typed => set(typed)} onClear={() => props.session.clearValue(key())} />
    }>
      <div class="hs-insp-field hs-def-field" data-field={`setting:${key()}`} aria-invalid={invalid() || undefined}>
        <label class="hs-insp-field__label" for={id}>{label()}</label>
        <Switch fallback={
          <input id={id} class="hs-input" type="text" placeholder="Class default" value={text()} aria-invalid={invalid() || undefined} aria-describedby={describe()}
            onInput={event => set(event.currentTarget.value === '' ? null : event.currentTarget.value)} />
        }>
          <Match when={props.setting.type === 'integer' || props.setting.type === 'number'}>
            <input id={id} class="hs-input" type="number" inputmode={props.setting.type === 'integer' ? 'numeric' : 'decimal'}
              step={props.setting.type === 'integer' ? '1' : 'any'} min={props.setting.minimum} max={props.setting.maximum}
              placeholder="Class default" value={text()} aria-invalid={invalid() || undefined} aria-describedby={describe()}
              onInput={event => set(number(event.currentTarget.value))} />
          </Match>
          <Match when={props.setting.type === 'boolean'}>
            {select([{ value: '', label: "Use the class's default" }, { value: 'true', label: 'True' }, { value: 'false', label: 'False' }],
              value() === true ? 'true' : value() === false ? 'false' : '', choice => set(choice === '' ? null : choice === 'true'))}
          </Match>
          <Match when={props.setting.type === 'enum'}>
            {select([
              { value: '', label: "Use the class's default" },
              // A saved value the class no longer offers stays in view, so the field says what is saved.
              ...(typeof value() === 'string' && value() !== '' && !props.setting.options.includes(value() as string) ? [{ value: value() as string, label: value() as string }] : []),
              ...props.setting.options.map(option => ({ value: option, label: option })),
            ], typeof value() === 'string' ? value() as string : '', choice => set(choice === '' ? null : choice))}
          </Match>
        </Switch>
        <Show when={hint()}><small id={hintId} class="hs-field-hint">{hint()}</small></Show>
        <FieldErrors id={errorsId} list={props.errors} />
      </div>
    </Show>
  )
}
