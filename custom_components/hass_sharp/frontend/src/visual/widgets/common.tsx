import { createEffect, createSignal, createUniqueId, For, on, Show, type JSX } from 'solid-js'
import type { JsonValue } from '../contract'
import type { Widget } from './model'

/** What every widget receives from the input editor. `commit(undefined)` clears the input. */
export interface WidgetProps {
  widget: Widget
  value: JsonValue | undefined
  commit(value: JsonValue | undefined, options?: { coalesce?: string }): void
  /** Put on the primary control, so the editor's label points at it. */
  id: string
  label: string
  describedBy?: string
  invalid: boolean
  disabled: boolean
  compact: boolean
  context?: { targetEntityIds?: string[] }
}

export const describedBy = (...ids: (string | false | undefined)[]) => ids.filter(Boolean).join(' ') || undefined

/**
 * A text control that reports one change on blur or Enter (Ctrl/Cmd+Enter when multiline) and restores the last
 * committed text on Escape. `commit` returns an error message to keep the draft and show it.
 */
export function DraftField(props: {
  id?: string
  label: string
  text: string
  commit(text: string): string | undefined
  multiline?: boolean
  type?: string
  step?: string
  inputmode?: JSX.HTMLAttributes<HTMLInputElement>['inputmode']
  list?: string
  placeholder?: string
  class?: string
  describedBy?: string
  invalid?: boolean
  disabled?: boolean
  /** Empty the field after each accepted commit (an "add item" field). */
  clearOnCommit?: boolean
}) {
  const [draft, setDraft] = createSignal(props.text)
  const [error, setError] = createSignal<string>()
  const fallbackId = createUniqueId()
  const errorId = () => `${props.id ?? fallbackId}-local-error`
  // A new committed value (from this field or elsewhere) replaces the draft.
  createEffect(on(() => props.text, text => { setDraft(text); setError() }, { defer: true }))
  const finish = () => {
    if (draft() === props.text) { setError(); return }
    const message = props.commit(draft())
    setError(message)
    if (!message && props.clearOnCommit) setDraft(props.text)
  }
  const keydown = (event: KeyboardEvent) => {
    if (event.key === 'Enter' && (!props.multiline || event.ctrlKey || event.metaKey)) { event.preventDefault(); finish() }
    if (event.key === 'Escape' && (draft() !== props.text || error())) {
      event.preventDefault(); event.stopPropagation()
      setDraft(props.text); setError()
    }
  }
  const shared = () => ({
    id: props.id, class: `hs-input ${props.class ?? ''}`, 'aria-label': props.label, placeholder: props.placeholder, disabled: props.disabled,
    'aria-invalid': props.invalid || !!error() || undefined, 'aria-describedby': describedBy(props.describedBy, !!error() && errorId()),
    onInput: (event: InputEvent & { currentTarget: HTMLInputElement | HTMLTextAreaElement }) => setDraft(event.currentTarget.value),
    onBlur: finish, onKeyDown: keydown,
  })
  return <>
    <Show when={props.multiline} fallback={<input {...shared()} type={props.type ?? 'text'} step={props.step} inputmode={props.inputmode} list={props.list} value={draft()} />}>
      <textarea {...shared()} rows="4" value={draft()} />
    </Show>
    <Show when={error()}><small id={errorId()} class="hs-error">{error()}</small></Show>
  </>
}

/**
 * Commits of one pointer drag or keyboard step share a coalesce key, so the parent can merge them into one undo
 * step; a repeated value within the session is not reported again.
 */
export function dragSession(id: () => string, commit: WidgetProps['commit']) {
  let key: string | undefined, last: string | undefined, count = 0
  return {
    move(value: JsonValue) {
      key ??= `${id()}:drag:${++count}`
      const text = JSON.stringify(value)
      if (text !== last) { last = text; commit(value, { coalesce: key }) }
    },
    end(value: JsonValue) { this.move(value); key = undefined; last = undefined },
  }
}

export interface Chip { key: string; label: string; title?: string; /** A category shown before the label, such as "Area". */ prefix?: string }
/** Removable items of a list value. */
export function Chips(props: { label: string; items: Chip[]; onRemove(key: string): void; disabled?: boolean }) {
  return <Show when={props.items.length}>
    <ul class="hs-chips" aria-label={props.label}>
      <For each={props.items}>{item => <li class="hs-chip" title={item.title ?? item.key}>
        <span><Show when={item.prefix}><small>{item.prefix} </small></Show>{item.label}</span>
        <button type="button" class="hs-chip-remove" aria-label={`Remove ${item.prefix ? item.prefix.toLowerCase() + ' ' : ''}${item.label}`} disabled={props.disabled} onClick={() => props.onRemove(item.key)}>×</button>
      </li>}</For>
    </ul>
  </Show>
}
