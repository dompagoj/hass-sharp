import { createEffect, createSignal, createUniqueId, For, on, Show, type JSX } from 'solid-js'
import type { JsonValue } from '../contract'
import type { ValueKind, Widget } from './model'

export interface CommitOptions {
  /** Changes of one drag share this key, so the parent can merge them into one undo step. */
  coalesce?: string
  /** The kind chosen with the kind switch; the type then comes from it rather than from the value. */
  kind?: ValueKind
}
/** What every widget receives from the input editor. `commit(undefined)` leaves the input not set. */
export interface WidgetProps {
  widget: Widget
  value: JsonValue | undefined
  commit(value: JsonValue | undefined, options?: CommitOptions): void
  /** Put on the primary control, so the editor's label points at it. */
  id: string
  label: string
  describedBy?: string
  invalid: boolean
  disabled: boolean
  compact: boolean
  required: boolean
  context?: { targetEntityIds?: string[] }
}

export const describedBy = (...ids: (string | false | undefined)[]) => ids.filter(Boolean).join(' ') || undefined

/**
 * A text control that reports one change on blur or Enter (Ctrl/Cmd+Enter when multiline) and restores the last
 * committed text on Escape. `commit` returns an error message to keep the draft and show it. A draft is reported
 * once even when the parent applies the change later.
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
  /** Show the "Ctrl+Enter to apply" hint of a multiline field. */
  shortcutHint?: boolean
}) {
  const [draft, setDraft] = createSignal(props.text)
  const [error, setError] = createSignal<string>()
  const ownId = createUniqueId()
  const errorId = () => `${props.id ?? ownId}-local-error`, hintId = () => `${props.id ?? ownId}-shortcut`
  // The last draft handed to the parent, until the parent's new text arrives.
  let reported: string | undefined
  createEffect(on(() => props.text, text => { setDraft(text); setError(); reported = undefined }, { defer: true }))
  const finish = () => {
    if (draft() === props.text) { setError(); return }
    if (draft() === reported) return
    const message = props.commit(draft())
    setError(message)
    if (message) return
    if (props.clearOnCommit) setDraft(props.text)
    else reported = draft()
  }
  const keydown = (event: KeyboardEvent) => {
    if (event.key === 'Enter' && !event.isComposing && (!props.multiline || event.ctrlKey || event.metaKey)) { event.preventDefault(); finish() }
    if (event.key === 'Escape' && (draft() !== props.text || error())) {
      event.preventDefault(); event.stopPropagation()
      setDraft(props.text); setError(); reported = undefined
    }
  }
  const hint = () => props.multiline && props.shortcutHint
  const shared = () => ({
    id: props.id, class: `hs-input ${props.class ?? ''}`, 'aria-label': props.label, placeholder: props.placeholder, disabled: props.disabled,
    'aria-invalid': props.invalid || !!error() || undefined,
    'aria-describedby': describedBy(props.describedBy, !!error() && errorId(), hint() && hintId()),
    onInput: (event: InputEvent & { currentTarget: HTMLInputElement | HTMLTextAreaElement }) => setDraft(event.currentTarget.value),
    onBlur: finish, onKeyDown: keydown,
  })
  return <>
    <Show when={props.multiline} fallback={<input {...shared()} type={props.type ?? 'text'} step={props.step} inputmode={props.inputmode} list={props.list} value={draft()} />}>
      <textarea {...shared()} rows="4" aria-keyshortcuts="Control+Enter Meta+Enter" value={draft()} />
    </Show>
    <Show when={hint()}><small id={hintId()} class="hs-field-hint">Ctrl+Enter to apply</small></Show>
    <Show when={error()}><small id={errorId()} class="hs-error">{error()}</small></Show>
  </>
}

/** The placeholder of a text value: "Not set" without a value, "Empty text" for an empty string. */
export const textPlaceholder = (value: JsonValue | undefined) => value === '' ? 'Empty text' : value === undefined ? 'Not set' : undefined

/**
 * Commits of one pointer drag share a coalesce key, so the parent can merge them into one undo step; a repeated
 * value within the drag is not reported again.
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
/**
 * Removable items of a list value. After a removal, focus moves to the next chip's remove button, else the
 * previous one, else the control with id `fallbackFocusId` (the add control).
 */
export function Chips(props: { label: string; items: Chip[]; onRemove(key: string): void; disabled?: boolean; fallbackFocusId?: string }) {
  let host!: HTMLDivElement
  const remove = (key: string, index: number) => {
    props.onRemove(key)
    queueMicrotask(() => {
      const buttons = Array.from(host.querySelectorAll<HTMLButtonElement>('.hs-chip-remove'))
      const next = buttons[index] ?? buttons[index - 1]
      if (next) { next.focus(); return }
      const root = host.getRootNode() as Document | ShadowRoot
      if (props.fallbackFocusId) root.getElementById(props.fallbackFocusId)?.focus()
    })
  }
  return <div ref={host} class="hs-chips-host">
    <Show when={props.items.length}>
      <ul class="hs-chips" aria-label={props.label}>
        <For each={props.items}>{(item, index) => <li class="hs-chip" title={item.title ?? item.key}>
          <span><Show when={item.prefix}><small>{item.prefix} </small></Show>{item.label}</span>
          <button type="button" class="hs-chip-remove" aria-label={`Remove ${item.prefix ? item.prefix.toLowerCase() + ' ' : ''}${item.label}`} disabled={props.disabled}
            onClick={() => remove(item.key, index())}>×</button>
        </li>}</For>
      </ul>
    </Show>
  </div>
}
