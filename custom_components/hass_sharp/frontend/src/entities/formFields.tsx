import { createEffect, createUniqueId, For, Show, type JSX } from 'solid-js'
import { focusControl } from '../visual/InputRow'
import { DraftField, describedBy } from '../visual/widgets/common'

/**
 * The definition form's fields, laid out as the inspector's settings are: a label above the control, a hint and the
 * field's problems below it. A text field commits once, on blur or Enter (one undo step), as the inspector's do.
 */

/** Focuses the first field of the row of `list` whose `data-<member>` is `key` (its head while it is closed). */
export function focusRow(list: HTMLElement | undefined, member: 'settingKey', key: string) {
  const row = [...(list?.children ?? [])].find(el => el instanceof HTMLElement && el.dataset[member] === key) as HTMLElement | undefined
  if (row) focusControl(row.querySelector<HTMLElement>('.hs-def-row__body') ?? row)
}

/** A field's problems, under it. */
export function FieldErrors(props: { id?: string; list: string[] }) {
  return (
    <Show when={props.list.length}>
      <ul id={props.id} class="hs-def-errors">
        <For each={props.list}>{message => <li class="hs-error">{message}</li>}</For>
      </ul>
    </Show>
  )
}

export function TextField(props: {
  name: string
  label: string
  text: string
  hint?: string
  placeholder?: string
  type?: string
  inputmode?: JSX.HTMLAttributes<HTMLInputElement>['inputmode']
  multiline?: boolean
  required?: boolean
  errors?: string[]
  /** The new text; a message keeps it in the field and says why it is not taken. */
  commit(text: string): string | undefined | void
}) {
  const id = createUniqueId(), hintId = `${id}-hint`, errorsId = `${id}-errors`
  const errors = () => props.errors ?? []
  return (
    <div class="hs-insp-field hs-def-field" data-field={props.name} aria-invalid={errors().length > 0 || undefined}>
      <label class="hs-insp-field__label" for={id}>{props.label}</label>
      <DraftField id={id} label={props.label} text={props.text} placeholder={props.placeholder} type={props.type} inputmode={props.inputmode}
        multiline={props.multiline} shortcutHint={props.multiline} invalid={errors().length > 0} required={props.required}
        describedBy={describedBy(!!props.hint && hintId, errors().length > 0 && errorsId)} commit={text => props.commit(text) || undefined} />
      <Show when={props.hint}><small id={hintId} class="hs-field-hint">{props.hint}</small></Show>
      <FieldErrors id={errorsId} list={errors()} />
    </div>
  )
}

/** A text that cannot be changed here, focusable and selectable, with why. */
export function ReadOnlyField(props: { name: string; label: string; text: string; hint?: string }) {
  const id = createUniqueId(), hintId = `${id}-hint`
  return (
    <div class="hs-insp-field hs-def-field" data-field={props.name}>
      <label class="hs-insp-field__label" for={id}>{props.label}</label>
      <input id={id} class="hs-input" readOnly value={props.text} aria-describedby={props.hint ? hintId : undefined} />
      <Show when={props.hint}><small id={hintId} class="hs-field-hint">{props.hint}</small></Show>
    </div>
  )
}

export interface Choice { value: string; label: string }
export function SelectField(props: { name: string; label: string; value: string; choices: Choice[]; hint?: string; errors?: string[]; disabled?: boolean; onChange(value: string): void }) {
  const id = createUniqueId(), hintId = `${id}-hint`, errorsId = `${id}-errors`
  const errors = () => props.errors ?? []
  let select!: HTMLSelectElement
  // Choices that come later (a list read after the form) are new options: the one chosen stays chosen.
  createEffect(() => { props.choices; if (select.value !== props.value) select.value = props.value })
  return (
    <div class="hs-insp-field hs-def-field" data-field={props.name} aria-invalid={errors().length > 0 || undefined}>
      <label class="hs-insp-field__label" for={id}>{props.label}</label>
      <select ref={select} id={id} class="hs-input" disabled={props.disabled} aria-invalid={errors().length > 0 || undefined}
        aria-describedby={describedBy(!!props.hint && hintId, errors().length > 0 && errorsId)}
        onChange={event => { if (event.currentTarget.value !== props.value) props.onChange(event.currentTarget.value) }}>
        <For each={props.choices}>{choice => <option value={choice.value} selected={choice.value === props.value}>{choice.label}</option>}</For>
      </select>
      <Show when={props.hint}><small id={hintId} class="hs-field-hint">{props.hint}</small></Show>
      <FieldErrors id={errorsId} list={errors()} />
    </div>
  )
}

/** An on/off switch with its label beside it. */
export function SwitchField(props: { name: string; label: string; checked: boolean; hint?: string; disabled?: boolean; errors?: string[]; onChange(checked: boolean): void }) {
  const id = createUniqueId(), hintId = `${id}-hint`, errorsId = `${id}-errors`
  const errors = () => props.errors ?? []
  return (
    <div class="hs-insp-field hs-def-field" data-field={props.name} aria-invalid={errors().length > 0 || undefined}>
      <div class="hs-insp-field__row">
        <label class="hs-insp-field__label" for={id}>{props.label}</label>
        <input id={id} type="checkbox" role="switch" class="hs-switch" checked={props.checked} disabled={props.disabled}
          aria-describedby={describedBy(!!props.hint && hintId, errors().length > 0 && errorsId)}
          onChange={event => { const wanted = event.currentTarget.checked; event.currentTarget.checked = props.checked; props.onChange(wanted) }} />
      </div>
      <Show when={props.hint}><small id={hintId} class="hs-field-hint">{props.hint}</small></Show>
      <FieldErrors id={errorsId} list={errors()} />
    </div>
  )
}
