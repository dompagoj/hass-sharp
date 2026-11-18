import { createEffect, createSignal, createUniqueId, Index, on, Show } from 'solid-js'
import { Button } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { settingTypes, type JsonValue, type VisualCatalogSnapshot } from '../visual/contract'
import type { VisualDiagnostic } from '../visual/api'
import { Glyph } from '../visual/Glyph'
import type { VisualGraph } from '../visual/graph'
import { icons } from '../visual/icons'
import { typeLabel } from '../visual/typeLabels'
import { describedBy } from '../visual/widgets/common'
import { addSetting, definitionOf, maskedSecret, removeSetting, renameSetting, retypeSetting, settingKeyProblem, settingKeyRule, stepsUsingSetting, updateSetting, valueFor } from './definitionEdits'
import { usedBy } from './EntityRows'
import { FieldErrors, focusRow, ReadOnlyField, SelectField, SwitchField, TextField } from './formFields'
import type { DefinitionSetting } from './model'

const text = (value: unknown) => (typeof value === 'string' ? value : '')
const none = (value: unknown) => value === null || value === undefined
/** What a new type that removed a setting's value says. */
export const valueRemoved = 'The value was removed because it does not fit the new type.'

export interface SettingRowsProps {
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  /** The problems about each setting (by key) that no step has. */
  problems: Map<string, VisualDiagnostic[]>
  /** A problem chosen in the Problems tab: the row it names (`setting:<key>`) opens, and focus goes into it. */
  located?: { target: string }
  commit(graph: VisualGraph): void
  /** Says what a change also did (a new type removed the value). */
  onNotice?(text: string): void
}

/**
 * The Settings section: values the steps read with a Setting step (a host, a password). A row per setting; opened,
 * its key, label, type, whether it is a secret, and its value. A secret's value is never shown (ruling R9).
 */
export function SettingRows(props: SettingRowsProps) {
  const settings = () => definitionOf(props.graph).settings
  const [open, setOpen] = createSignal<ReadonlySet<string>>(new Set())
  const toggle = (key: string, wanted = !open().has(key)) => setOpen(current => { const next = new Set(current); if (wanted) next.add(key); else next.delete(key); return next })
  createEffect(on(() => [...props.problems.keys()].join(' '), () => { for (const key of props.problems.keys()) if (!open().has(key)) toggle(key, true) }))
  let list: HTMLUListElement | undefined
  createEffect(on(() => props.located, located => {
    const key = located?.target.startsWith('setting:') ? located.target.slice('setting:'.length) : undefined
    if (key === undefined || !settings().some(s => s.key === key)) return
    toggle(key, true)
    queueMicrotask(() => focusRow(list, 'settingKey', key))
  }))
  const [removing, setRemoving] = createSignal<string>()
  const removingUses = () => { const key = removing(); return key ? stepsUsingSetting(props.graph, key).length : 0 }
  const remove = (key: string) => {
    if (stepsUsingSetting(props.graph, key).length) return setRemoving(key)
    props.commit(removeSetting(props.graph, key))
  }
  const add = () => {
    const made = addSetting(props.graph)
    props.commit(made.graph)
    toggle(made.key, true)
  }
  return (
    <section class="hs-def-section" aria-label="Settings">
      <h2 class="hs-insp-heading">Settings</h2>
      <Show when={settings().length} fallback={<p class="hs-muted">No settings. A setting is a value its steps read with a Setting step, such as an address or a password.</p>}>
        <ul class="hs-def-rows" aria-label="Settings" ref={list}>
          <Index each={settings()}>
            {setting => (
              <SettingRow setting={setting()} rows={props} open={open().has(setting().key)} onToggle={() => toggle(setting().key)}
                onRenamed={(from, to) => setOpen(current => { const next = new Set(current); if (next.delete(from)) next.add(to); return next })}
                onRemove={() => remove(setting().key)} />
            )}
          </Index>
        </ul>
      </Show>
      <div class="hs-def-actions"><Button onClick={add}>Add setting</Button></div>
      <Dialog open={!!removing()} title="Remove this setting?" width="small" onClose={() => setRemoving(undefined)} footer={<>
        <Button onClick={() => setRemoving(undefined)}>Cancel</Button>
        <Button variant="danger" onClick={() => { const key = removing()!; setRemoving(undefined); props.commit(removeSetting(props.graph, key)) }}>Remove</Button>
      </>}>
        <p>{usedBy(removingUses(), 'setting')}</p>
      </Dialog>
    </section>
  )
}

function SettingRow(props: { setting: DefinitionSetting; rows: SettingRowsProps; open: boolean; onToggle(): void; onRenamed(from: string, to: string): void; onRemove(): void }) {
  const id = createUniqueId()
  const rows = props.rows
  const key = () => props.setting.key
  const type = () => text(props.setting.type) || 'string'
  const problems = () => (rows.problems.get(key()) ?? []).map(d => d.message)
  const set = (changes: Partial<Record<keyof DefinitionSetting, unknown>>) => rows.commit(updateSetting(rows.graph, key(), changes))
  const others = () => definitionOf(rows.graph).settings.filter(s => s.key !== key()).map(s => s.key)
  /** A secret whose saved value is not here (masked): it keeps its key, and stays a secret, until its value is typed or cleared. */
  const masked = () => maskedSecret(props.setting)
  const retype = (type: string) => {
    const result = retypeSetting(rows.graph, key(), type)
    rows.commit(result.graph)
    if (result.removed) rows.onNotice?.(valueRemoved)
  }
  const types = () => [...(settingTypes.includes(type()) ? [] : [type()]), ...settingTypes].map(t => ({ value: t, label: typeLabel(t, rows.catalog) }))
  return (
    <li class="hs-def-row" data-setting-key={key()} classList={{ 'hs-def-row--problem': problems().length > 0 }}>
      <button type="button" class="hs-def-row__head" aria-expanded={props.open} aria-controls={props.open ? `${id}-body` : undefined} onClick={() => props.onToggle()}>
        <span class="hs-def-row__icon" aria-hidden="true"><Glyph paths={icons.data} /></span>
        <span class="hs-def-row__title">
          <strong>{text(props.setting.label) || key()}</strong>
          <small>{typeLabel(type(), rows.catalog)}{props.setting.secret ? ' · Secret' : ''} · {key()}</small>
        </span>
        <Show when={problems().length}>
          {count => <span class="hs-def-row__mark" role="img" aria-label={`${count()} ${count() === 1 ? 'problem' : 'problems'}`}><Glyph paths={icons.problem} /></span>}
        </Show>
      </button>
      <Show when={props.open}>
        <div id={`${id}-body`} class="hs-def-row__body">
          <FieldErrors list={problems()} />
          {/* The server keeps a saved secret by its key: renamed untouched, it would be lost (ruling R16). */}
          <Show when={!masked()} fallback={<ReadOnlyField name="key" label="Key" text={key()} hint="To rename this setting, type its value again first." />}>
            <TextField name="key" label="Key" text={key()} hint={`${settingKeyRule} Setting steps that read it follow it.`}
              commit={value => {
                const to = value.trim()
                if (to === key()) return
                const problem = settingKeyProblem(to, others())
                if (problem) return problem
                const from = key()
                rows.commit(renameSetting(rows.graph, from, to))
                props.onRenamed(from, to)
              }} />
          </Show>
          <TextField name="label" label="Label" text={text(props.setting.label)} placeholder="Not set" hint="How the Setting step names it."
            commit={value => set({ label: value.trim() || undefined })} />
          <SelectField name="type" label="Type" value={type()} choices={types()} onChange={retype} />
          <SwitchField name="secret" label="Secret" checked={props.setting.secret === true} disabled={masked()}
            hint={masked() ? 'Clear its saved value first to make it a plain setting.' : 'A secret, such as a password, is never shown again once saved.'}
            onChange={secret => set({ secret: secret ? true : undefined })} />
          <Show when={props.setting.secret} fallback={<PlainValue type={type()} value={props.setting.value} onChange={value => set({ value })} />}>
            <SecretValue type={type()} setting={props.setting} onChange={value => set({ value })} onClear={() => set({ value: null, hasValue: false })} />
          </Show>
          <div class="hs-def-actions">
            <Button variant="danger" aria-label={`Remove setting ${text(props.setting.label) || key()}`} onClick={() => props.onRemove()}>Remove</Button>
          </div>
        </div>
      </Show>
    </li>
  )
}

/** A value typed as text, as `type` reads it; a message when it does not read so. */
function parsed(type: string, typed: string): { value: JsonValue } | { error: string } {
  if (type === 'number') {
    const value = valueFor('number', typed.trim().replace(',', '.'))
    return value === null ? { error: 'Enter a number.' } : { value }
  }
  if (type === 'boolean') {
    const value = valueFor('boolean', typed.trim().toLowerCase())
    return value === null ? { error: 'Enter true or false.' } : { value }
  }
  return { value: typed }
}

/** A setting's value that is not a secret: a text, a number, or true or false; empty is no value. */
function PlainValue(props: { type: string; value: JsonValue | undefined; onChange(value: JsonValue): void }) {
  return (
    <Show when={props.type !== 'boolean'} fallback={
      <SelectField name="value" label="Value" value={props.value === true ? 'true' : props.value === false ? 'false' : ''}
        choices={[{ value: '', label: 'No value' }, { value: 'true', label: 'True' }, { value: 'false', label: 'False' }]}
        onChange={value => props.onChange(value === '' ? null : value === 'true')} />
    }>
      <TextField name="value" label="Value" text={none(props.value) ? '' : String(props.value)} placeholder="No value"
        inputmode={props.type === 'number' ? 'decimal' : undefined}
        commit={typed => {
          if (typed === '') return props.onChange(null)
          const result = parsed(props.type, typed)
          if ('error' in result) return result.error
          props.onChange(result.value)
        }} />
    </Show>
  )
}

/**
 * A secret's value. Never shown: the field is a password field that starts empty and empties again once its text is
 * taken (on blur or Enter); its placeholder says whether a value is saved or a new one was typed. Untouched it changes
 * nothing; "Clear value" leaves the setting without a value.
 *
 * `label` names the field (default "Value"), `note` follows the state in its hint (default: about drafts and
 * downloads; empty for none), and `errors` are the server's problems with it, shown under it.
 */
export function SecretValue(props: {
  type: string; setting: DefinitionSetting; onChange(value: JsonValue): void; onClear(): void
  name?: string; label?: string; note?: string; errors?: string[]
}) {
  const id = createUniqueId(), hintId = `${id}-hint`, errorId = `${id}-error`, errorsId = `${id}-errors`
  const [error, setError] = createSignal<string>()
  const errors = () => props.errors ?? []
  const note = () => props.note ?? "A secret is kept out of this browser's drafts and downloads."
  const typedBefore = () => !none(props.setting.value)
  const saved = () => props.setting.hasValue === true
  const state = () => (typedBefore() ? 'New value set; type to replace' : saved() ? 'Saved; type to replace' : 'No value')
  const take = (input: HTMLInputElement) => {
    const typed = input.value
    if (typed === '') return setError(undefined)
    const result = parsed(props.type, typed)
    if ('error' in result) return setError(result.error)
    setError(undefined)
    input.value = ''
    props.onChange(result.value)
  }
  return (
    <div class="hs-insp-field hs-def-field" data-field={props.name ?? 'value'} aria-invalid={!!error() || errors().length > 0 || undefined}>
      <label class="hs-insp-field__label" for={id}>{props.label ?? 'Value'}</label>
      <div class="hs-def-secret">
        <input id={id} class="hs-input" type="password" autocomplete="new-password" placeholder={state()} aria-invalid={!!error() || errors().length > 0 || undefined}
          aria-describedby={describedBy(hintId, !!error() && errorId, errors().length > 0 && errorsId)}
          onBlur={event => take(event.currentTarget)}
          onKeyDown={event => {
            if (event.key === 'Enter' && !event.isComposing) { event.preventDefault(); take(event.currentTarget) }
            if (event.key === 'Escape' && event.currentTarget.value) { event.preventDefault(); event.stopPropagation(); event.currentTarget.value = ''; setError(undefined) }
          }} />
        <Show when={saved() || typedBefore()}>
          <Button aria-label={props.label ? `Clear ${props.label}` : 'Clear value'} onClick={() => props.onClear()}>Clear</Button>
        </Show>
      </div>
      <small id={hintId} class="hs-field-hint">{state()}.{note() ? ` ${note()}` : ''}</small>
      <Show when={error()}><small id={errorId} class="hs-error">{error()}</small></Show>
      <FieldErrors id={errorsId} list={errors()} />
    </div>
  )
}
