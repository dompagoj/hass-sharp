import { createEffect, createSignal, createUniqueId, For, Index, on, Show, type JSX } from 'solid-js'
import type { JsonValue } from '../contract'
import { Chips, DraftField, dragSession, textPlaceholder, type WidgetProps } from './common'
import { displayValue, jsonEqual } from './json'
import { durationParts, durationSeconds, durationValue, valueKind, type ValueKind, type Widget, type WidgetKind, type WidgetOption } from './model'

const numberText = /^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/
/** Parse a typed number; undefined for empty text, NaN for anything that is not a finite number. */
const parseNumber = (text: string) => text.trim() === '' ? undefined : numberText.test(text.trim()) && Number.isFinite(Number(text)) ? Number(text) : Number.NaN
const items = (value: JsonValue | undefined): JsonValue[] => Array.isArray(value) ? value : value === undefined ? [] : [value]
/** The decimal keypad has no minus sign on some phones; offer it only when values cannot be negative. */
const decimalMode = (min: number | undefined) => min !== undefined && min >= 0 ? 'decimal' as const : undefined

/** A yes/no stored as text ("true", "False", any case), as a Constant's value from before its Type set the value's type. */
const yesNo = (text: string) => /^(true|false)$/i.test(text.trim()) ? text.trim().toLowerCase() === 'true' : undefined
export function ToggleWidget(props: WidgetProps) {
  // A stored value that is not yes/no (see `stale`) is shown as typed, to be typed again as true or false: the switch
  // could only show it as No, and flipping it twice to store it would be two changes.
  return <Show when={props.stale} fallback={<span class="hs-widget-row">
    <input id={props.id} type="checkbox" role="switch" class="hs-switch" aria-label={props.label} checked={props.value === true}
      aria-describedby={props.describedBy} aria-invalid={props.invalid || undefined} disabled={props.disabled}
      onChange={event => props.commit(event.currentTarget.checked)} />
    <span class="hs-muted">{props.value === undefined ? 'Not set' : props.value === true ? 'Yes' : 'No'}</span>
  </span>}>
    <span class="hs-widget-row">
      <DraftField id={props.id} label={props.label} text={displayValue(props.value)} placeholder="true or false" describedBy={props.describedBy} invalid={props.invalid}
        disabled={props.disabled} force initialError={yesNo(displayValue(props.value)) === undefined ? 'Enter true or false.' : undefined}
        commit={text => {
          const value = yesNo(text)
          if (value === undefined) return 'Enter true or false.'
          props.commit(value)
        }} />
    </span>
  </Show>
}

/** A constant selector: the only allowed value is either included or the input is left not set. */
export function ConstantWidget(props: WidgetProps) {
  const option = () => props.widget.options?.[0]
  return <span class="hs-widget-row">
    <input id={props.id} type="checkbox" role="switch" class="hs-switch" aria-label={props.label} checked={option() !== undefined && jsonEqual(props.value, option()!.value)}
      aria-describedby={props.describedBy} aria-invalid={props.invalid || undefined} disabled={props.disabled || !option()}
      onChange={event => props.commit(event.currentTarget.checked ? option()!.value : undefined)} />
    <span class="hs-muted">{option()?.label}</span>
  </span>
}

export function NumberWidget(props: WidgetProps) {
  const unit = () => props.widget.unit
  const value = () => typeof props.value === 'number' ? props.value : undefined
  const shown = () => value() === undefined ? 'Not set' : `${value()}${unit() ? ' ' + unit() : ''}`
  const session = dragSession(() => props.id, (v, o) => props.commit(v, o))
  return <Show when={props.widget.slider} fallback={<span class="hs-widget-row">
    <DraftField id={props.id} label={props.label} inputmode={decimalMode(props.widget.min)} text={value() === undefined ? displayValue(props.value) : String(value())}
      placeholder="Not set" describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled} force={props.stale}
      initialError={props.stale && Number.isNaN(parseNumber(displayValue(props.value))) ? 'Enter a number.' : undefined}
      commit={text => {
        const parsed = parseNumber(text)
        if (Number.isNaN(parsed)) return 'Enter a number.'
        props.commit(parsed)
      }} />
    <Show when={unit()}><span class="hs-unit">{unit()}</span></Show>
  </span>}>
    <span class="hs-widget-row hs-slider">
      <input id={props.id} type="range" class="hs-range" aria-label={props.label} min={props.widget.min} max={props.widget.max} step={props.widget.step ?? 'any'}
        value={value() ?? props.widget.min} aria-valuetext={shown()}
        aria-describedby={props.describedBy} aria-invalid={props.invalid || undefined} disabled={props.disabled}
        onInput={event => session.move(Number(event.currentTarget.value))} onChange={event => session.end(Number(event.currentTarget.value))} />
      <output class="hs-slider-value" for={props.id}>{shown()}</output>
    </span>
  </Show>
}

/** An emptied field: the empty text when the input is required (clearing a text is a real action), else not set. */
const emptyText = (props: WidgetProps, text: string) => text === '' && !props.required ? undefined : text

/** Text; a `custom_value` select offers its options as suggestions. Several values become chips. */
export function TextWidget(props: WidgetProps) {
  const listId = createUniqueId()
  const suggestions = () => props.widget.options?.length ? listId : undefined
  return <>
    <Show when={props.widget.multiple} fallback={
      <DraftField id={props.id} label={props.label} text={displayValue(props.value)} multiline={props.widget.multiline} list={suggestions()}
        placeholder={textPlaceholder(props.value)} shortcutHint={!props.compact} describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled}
        force={props.stale} commit={text => { props.commit(emptyText(props, text)) }} />}>
      <ListWidget {...props} add={add => <DraftField id={props.id} label={`Add to ${props.label}`} text="" placeholder="Add…" clearOnCommit list={suggestions()} describedBy={props.describedBy}
        invalid={props.invalid} disabled={props.disabled} commit={text => { if (text.trim()) add(text) }} />} />
    </Show>
    <Show when={suggestions()}><datalist id={listId}><For each={props.widget.options}>{o => <option value={displayValue(o.value)}>{o.label}</option>}</For></datalist></Show>
  </>
}

/** A list value: the current items as chips and an `add` control (which carries `props.id`). */
export function ListWidget(props: WidgetProps & { add(add: (item: JsonValue) => void): JSX.Element; labelFor?: (item: JsonValue) => string }) {
  const list = () => items(props.value)
  const add = (item: JsonValue) => { if (!list().some(v => jsonEqual(v, item))) props.commit([...list(), item]) }
  const remove = (key: string) => { const next = list().filter((_, i) => String(i) !== key); props.commit(next.length ? next : undefined) }
  return <div class="hs-widget-list">
    <Chips label={props.label} disabled={props.disabled} onRemove={remove} fallbackFocusId={props.id}
      items={list().map((item, i) => ({ key: String(i), label: props.labelFor?.(item) ?? displayValue(item), title: displayValue(item) }))} />
    {props.add(add)}
  </div>
}

/** Options as a native select. The current value stays visible even when it is not an option. */
export function OptionSelect(props: {
  id?: string; label: string; options: WidgetOption[]; value: JsonValue | undefined; onSelect(value: JsonValue): void
  placeholder?: string; describedBy?: string; invalid?: boolean; disabled?: boolean
}) {
  const index = () => props.value === undefined ? -1 : props.options.findIndex(o => jsonEqual(o.value, props.value))
  return <select id={props.id} class="hs-input" aria-label={props.label} aria-describedby={props.describedBy} aria-invalid={props.invalid || undefined} disabled={props.disabled}
    onChange={event => {
      const v = event.currentTarget.value
      if (v !== '' && v !== 'current') props.onSelect(props.options[Number(v)].value)
    }}>
    <Show when={props.value === undefined}><option value="" selected disabled>{props.placeholder ?? 'Choose…'}</option></Show>
    <Show when={props.value !== undefined && index() < 0}><option value="current" selected>{displayValue(props.value!)}</option></Show>
    <Index each={props.options}>{(option, i) => <option value={String(i)} selected={i === index()}>{option().label}</option>}</Index>
  </select>
}

export function SelectWidget(props: WidgetProps) {
  const options = () => props.widget.options ?? []
  return <Show when={props.widget.multiple} fallback={<OptionSelect id={props.id} label={props.label} options={options()} value={props.value}
    onSelect={v => props.commit(v)} describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled} />}>
    <ListWidget {...props} labelFor={item => options().find(o => jsonEqual(o.value, item))?.label ?? displayValue(item)}
      add={add => <OptionSelect id={props.id} label={`Add to ${props.label}`} placeholder="Add…" value={undefined} describedBy={props.describedBy}
        options={options().filter(o => !items(props.value).some(v => jsonEqual(v, o.value)))} onSelect={add} invalid={props.invalid} disabled={props.disabled} />} />
  </Show>
}

/** Hours, minutes and seconds (and days); one change on blur or Enter of a box, in the shape already stored. */
export function DurationWidget(props: WidgetProps) {
  const seconds = () => durationSeconds(props.value)
  const units = () => [...(props.widget.days ? ['days'] as const : []), 'hours', 'minutes', 'seconds'] as const
  const sizes = { days: 86400, hours: 3600, minutes: 60, seconds: 1 }
  const parts = () => durationParts(props.widget, seconds() ?? 0)
  return <span class="hs-duration" role="group" aria-label={props.label}>
    <For each={units()}>{(unit, i) => <label class="hs-duration-part">
      <DraftField id={i() === 0 ? props.id : undefined} label={`${props.label} ${unit}`} inputmode="decimal" text={seconds() === undefined ? '' : String(parts()[unit])}
        placeholder={i() === 0 && seconds() === undefined ? 'Not set' : undefined} describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled}
        commit={text => {
          const parsed = parseNumber(text)
          if (Number.isNaN(parsed) || (parsed ?? 0) < 0) return 'Enter 0 or more.'
          const next = { ...parts(), [unit]: parsed ?? 0 }
          props.commit(durationValue(props.widget, props.value, units().reduce((sum, u) => sum + next[u] * sizes[u], 0)))
        }} />
      <span class="hs-unit" aria-hidden="true">{props.compact ? unit[0] : unit}</span>
    </label>}</For>
  </span>
}

const dateTime = /^(\d{4}-\d{2}-\d{2})([T ])(\d{2}:\d{2}(?::\d{2})?)(\.\d+)?(Z|[+-]\d{2}:?\d{2})?$/
/**
 * Time, date and date-time pickers; each reports one change on blur or Enter. A stored date-time keeps its
 * separator and offset (shown as a suffix in the panel); new values are written as Home Assistant does, with a space.
 */
export function DateTimeWidget(props: WidgetProps) {
  const type = () => props.widget.kind === 'datetime' ? 'datetime-local' : props.widget.kind
  const stored = () => props.widget.kind === 'datetime' && typeof props.value === 'string' ? dateTime.exec(props.value) : null
  const text = () => { const m = stored(); return m ? `${m[1]}T${m[3]}` : displayValue(props.value) }
  const suffix = () => stored()?.[5] ?? ''
  return <span class="hs-widget-row">
    <DraftField id={props.id} label={props.label} type={type()} step={props.widget.kind === 'date' ? undefined : '1'} text={text()} placeholder={textPlaceholder(props.value)}
      describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled}
      commit={value => {
        if (value === '') { props.commit(undefined); return }
        if (props.widget.kind !== 'datetime') { props.commit(value); return }
        const m = /^(\d{4}-\d{2}-\d{2})[T ](.+)$/.exec(value)
        props.commit(m ? `${m[1]}${stored()?.[2] ?? ' '}${m[2]}${suffix()}` : value)
      }} />
    <Show when={suffix() && !props.compact}><span class="hs-unit" title="Time zone offset kept from the stored value">{suffix() === 'Z' ? 'UTC' : suffix()}</span></Show>
  </span>
}

const hex = (value: JsonValue | undefined) => Array.isArray(value) && value.length === 3 && value.every(v => typeof v === 'number')
  ? '#' + value.map(v => Math.max(0, Math.min(255, Math.round(v as number))).toString(16).padStart(2, '0')).join('') : undefined
const rgb = (text: string): JsonValue => [1, 3, 5].map(i => parseInt(text.slice(i, i + 2), 16))
/** An RGB colour as `[r, g, b]`; dragging in the colour picker reports changes under one coalesce key. */
export function ColorWidget(props: WidgetProps) {
  const session = dragSession(() => props.id, (v, o) => props.commit(v, o))
  return <span class="hs-widget-row">
    <input id={props.id} type="color" class="hs-color" aria-label={props.label} value={hex(props.value) ?? '#ffffff'}
      aria-describedby={props.describedBy} aria-invalid={props.invalid || undefined} disabled={props.disabled}
      onInput={event => session.move(rgb(event.currentTarget.value))} onChange={event => session.end(rgb(event.currentTarget.value))} />
    <span class="hs-muted">{props.value === undefined ? 'Not set' : Array.isArray(props.value) ? props.value.join(', ') : displayValue(props.value)}</span>
  </span>
}

/** Editor for lists and objects. Parse errors are shown and the text is kept; `structured` also refuses a single value. */
export function JsonWidget(props: WidgetProps) {
  return <DraftField id={props.id} label={props.label} multiline class="hs-code-input" text={props.value === undefined ? '' : JSON.stringify(props.value, null, 2)}
    placeholder="Not set" shortcutHint={!props.compact} describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled}
    commit={text => {
      if (text.trim() === '') { props.commit(undefined); return }
      let value: JsonValue
      try { value = JSON.parse(text) as JsonValue }
      catch (error) { return `Not valid JSON: ${error instanceof Error ? error.message : String(error)}` }
      if (props.widget.structured && (value === null || typeof value !== 'object')) return 'Enter a list [ … ] or an object { … }.'
      props.commit(value)
    }} />
}

const kindLabels: Record<ValueKind, string> = { text: 'Text', number: 'Number', boolean: 'Yes/No', json: 'List or object' }
const widgetKinds: Record<ValueKind, WidgetKind> = { text: 'text', number: 'number', boolean: 'toggle', json: 'json' }
/** The value read as `kind` when that reading is unambiguous ("1" and 1, "true" and true); otherwise undefined. */
function convert(value: JsonValue | undefined, kind: ValueKind): JsonValue | undefined {
  if (value === undefined || valueKind(value) === kind) return value
  if (kind === 'text' && (typeof value === 'number' || typeof value === 'boolean')) return String(value)
  if (kind === 'number' && typeof value === 'string' && numberText.test(value.trim())) return Number(value)
  if (kind === 'boolean' && (value === 'true' || value === 'false')) return value === 'true'
  return undefined
}
/**
 * A value of one of several simple kinds. The kind switch, not the text, decides the type ("01" stays text); it
 * starts from the stored value's kind, else the widget's default. Only "List or object" uses the JSON editor.
 */
export function MixedWidget(props: WidgetProps) {
  const kinds = () => props.widget.kinds ?? ['text']
  const initial = () => valueKind(props.value) ?? props.widget.defaultKind ?? kinds()[0]
  const [kind, setKind] = createSignal<ValueKind>(initial())
  createEffect(on(() => props.value, value => { const k = valueKind(value); if (k) setKind(k) }, { defer: true }))
  const commit = (value: JsonValue | undefined, options?: { coalesce?: string }) =>
    props.commit(value, value === undefined ? options : { ...options, kind: kind() })
  // What a kind starts from when the value does not convert: the widget then shows exactly what is stored.
  const start = (next: ValueKind): JsonValue | undefined => {
    const { min, max } = props.widget
    if (next === 'boolean') return false
    if (next === 'number') return min !== undefined && min > 0 ? min : max !== undefined && max < 0 ? max : 0
    return undefined
  }
  const switchTo = (next: ValueKind) => {
    setKind(next)
    if (props.value === undefined) return
    const value = convert(props.value, next) ?? start(next)
    if (value === undefined) props.commit(undefined)
    else if (!jsonEqual(value, props.value)) props.commit(value, { kind: next })
  }
  const shown = () => valueKind(props.value) === kind() ? props.value : undefined
  const child = (): WidgetProps => {
    const { min, max, step, unit } = props.widget, k = kind()
    const widget: Widget = k === 'number' ? { kind: 'number', multiple: false, min, max, step, unit } : { kind: widgetKinds[k], multiple: false, structured: k === 'json' }
    return { ...props, value: shown(), commit, widget }
  }
  return <span class="hs-mixed" classList={{ 'hs-mixed--block': kind() === 'json' }}>
    <Show when={kinds().length > 1}>
      <select class="hs-input hs-kind-switch" aria-label={`${props.label} kind`} disabled={props.disabled} onChange={event => switchTo(event.currentTarget.value as ValueKind)}>
        <For each={kinds()}>{k => <option value={k} selected={k === kind()}>{kindLabels[k]}</option>}</For>
      </select>
    </Show>
    <span class="hs-mixed-value">
      <Show when={kind() === 'text'}><TextWidget {...child()} /></Show>
      <Show when={kind() === 'number'}><NumberWidget {...child()} /></Show>
      <Show when={kind() === 'boolean'}><ToggleWidget {...child()} /></Show>
      <Show when={kind() === 'json'}><JsonWidget {...child()} /></Show>
    </span>
  </span>
}
