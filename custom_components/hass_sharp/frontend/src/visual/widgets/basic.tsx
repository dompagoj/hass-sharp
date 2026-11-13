import { createUniqueId, For, Index, Show, type JSX } from 'solid-js'
import type { JsonValue } from '../contract'
import { Chips, DraftField, dragSession, type WidgetProps } from './common'
import { displayValue, jsonEqual, type WidgetOption } from './model'

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const numberText = /^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/
/** Parse a typed number; undefined for empty text, NaN for anything that is not a finite number. */
const parseNumber = (text: string) => text.trim() === '' ? undefined : numberText.test(text.trim()) && Number.isFinite(Number(text)) ? Number(text) : Number.NaN
const items = (value: JsonValue | undefined): JsonValue[] => Array.isArray(value) ? value : value === undefined ? [] : [value]

export function ToggleWidget(props: WidgetProps) {
  return <span class="hs-widget-row">
    <input id={props.id} type="checkbox" role="switch" class="hs-switch" aria-label={props.label} checked={props.value === true}
      aria-describedby={props.describedBy} aria-invalid={props.invalid || undefined} disabled={props.disabled}
      onChange={event => props.commit(event.currentTarget.checked)} />
    <Show when={props.value === undefined}><span class="hs-muted">Not set</span></Show>
  </span>
}

/** A constant selector: the only allowed value is either included or the input is left unset. */
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
  const session = dragSession(() => props.id, (v, o) => props.commit(v, o))
  return <Show when={props.widget.slider} fallback={<span class="hs-widget-row">
    <DraftField id={props.id} label={props.label} inputmode="decimal" text={value() === undefined ? displayValue(props.value) : String(value())}
      placeholder={props.compact ? undefined : 'Not set'} describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled}
      commit={text => {
        const parsed = parseNumber(text)
        if (Number.isNaN(parsed)) return 'Enter a finite number.'
        props.commit(parsed)
      }} />
    <Show when={unit()}><span class="hs-unit">{unit()}</span></Show>
  </span>}>
    <span class="hs-widget-row hs-slider">
      <input id={props.id} type="range" class="hs-range" aria-label={props.label} min={props.widget.min} max={props.widget.max} step={props.widget.step ?? 'any'}
        value={value() ?? props.widget.min} aria-valuetext={value() === undefined ? 'Not set' : `${value()}${unit() ? ' ' + unit() : ''}`}
        aria-describedby={props.describedBy} aria-invalid={props.invalid || undefined} disabled={props.disabled}
        onInput={event => session.move(Number(event.currentTarget.value))} onChange={event => session.end(Number(event.currentTarget.value))} />
      <output class="hs-slider-value" for={props.id}>{value() === undefined ? 'Not set' : `${value()}${unit() ? ' ' + unit() : ''}`}</output>
    </span>
  </Show>
}

/** Text; a `custom_value` select offers its options as suggestions. Several values become chips. */
export function TextWidget(props: WidgetProps) {
  const listId = createUniqueId()
  const suggestions = () => props.widget.options?.length ? listId : undefined
  return <>
    <Show when={props.widget.multiple} fallback={
      <DraftField id={props.id} label={props.label} text={displayValue(props.value)} multiline={props.widget.multiline} list={suggestions()}
        describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled}
        commit={text => { props.commit(text === '' ? undefined : text) }} />}>
      <ListWidget {...props} add={add => <DraftField id={props.id} label={`Add to ${props.label}`} text="" clearOnCommit list={suggestions()} describedBy={props.describedBy}
        invalid={props.invalid} disabled={props.disabled} commit={text => { if (text.trim()) add(text) }} />} />
    </Show>
    <Show when={suggestions()}><datalist id={listId}><For each={props.widget.options}>{o => <option value={displayValue(o.value)}>{o.label}</option>}</For></datalist></Show>
  </>
}

/** A list value: the current items as chips and an `add` control. */
export function ListWidget(props: WidgetProps & { add(add: (item: JsonValue) => void): JSX.Element; labelFor?: (item: JsonValue) => string }) {
  const list = () => items(props.value)
  const add = (item: JsonValue) => { if (!list().some(v => jsonEqual(v, item))) props.commit([...list(), item]) }
  const remove = (key: string) => { const next = list().filter((_, i) => String(i) !== key); props.commit(next.length ? next : undefined) }
  return <div class="hs-widget-list">
    <Chips label={props.label} disabled={props.disabled} onRemove={remove}
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
    <Show when={props.value !== undefined && index() < 0}><option value="current" selected>{displayValue(props.value)}</option></Show>
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

/** Seconds of a duration literal: a number of seconds or Home Assistant's `{days, hours, minutes, seconds, milliseconds}`. */
export function durationSeconds(value: JsonValue | undefined): number | undefined {
  if (typeof value === 'number') return value
  if (!record(value)) return
  const part = (key: string) => typeof value[key] === 'number' ? value[key] as number : 0
  return part('days') * 86400 + part('hours') * 3600 + part('minutes') * 60 + part('seconds') + part('milliseconds') / 1000
}
/** Hours, minutes and seconds (and days); one change on blur or Enter of a box, always as a number of seconds. */
export function DurationWidget(props: WidgetProps) {
  const seconds = () => durationSeconds(props.value)
  const units = () => [...(props.widget.days ? [['days', 86400]] as const : []), ['hours', 3600], ['minutes', 60], ['seconds', 1]] as const
  const parts = () => {
    let rest = seconds() ?? 0
    return units().map(([, size]) => { const whole = size === 1 ? rest : Math.floor(rest / size); rest -= whole * size; return whole })
  }
  return <span class="hs-duration" role="group" aria-label={props.label} aria-describedby={props.describedBy}>
    <For each={units()}>{([unit], i) => <label class="hs-duration-part">
      <DraftField id={i() === 0 ? props.id : undefined} label={`${props.label} ${unit}`} inputmode="decimal" text={seconds() === undefined ? '' : String(parts()[i()])}
        invalid={props.invalid} disabled={props.disabled}
        commit={text => {
          const parsed = parseNumber(text)
          if (Number.isNaN(parsed) || (parsed ?? 0) < 0) return 'Enter a positive number.'
          const next = parts().map((p, j) => j === i() ? parsed ?? 0 : p)
          props.commit(units().reduce((sum, [, size], j) => sum + next[j] * size, 0))
        }} />
      <span class="hs-unit" aria-hidden="true">{props.compact ? unit[0] : unit}</span>
    </label>}</For>
  </span>
}

/** Time, date and date-time pickers; each reports one change on blur or Enter. */
export function DateTimeWidget(props: WidgetProps) {
  const type = () => props.widget.kind === 'datetime' ? 'datetime-local' : props.widget.kind
  // datetime-local shows `YYYY-MM-DDTHH:MM[:SS]` without an offset; Home Assistant also writes a space.
  const text = () => {
    const raw = displayValue(props.value)
    return props.widget.kind === 'datetime' ? raw.replace(' ', 'T').replace(/(\.\d+)?(Z|[+-]\d{2}:?\d{2})$/, '') : raw
  }
  return <DraftField id={props.id} label={props.label} type={type()} step={props.widget.kind === 'date' ? undefined : '1'} text={text()}
    describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled} commit={value => { props.commit(value === '' ? undefined : value) }} />
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

/** Fallback editor for any JSON value. Parse errors are shown and the text is kept. */
export function JsonWidget(props: WidgetProps) {
  return <DraftField id={props.id} label={props.label} multiline class="hs-code-input" text={props.value === undefined ? '' : JSON.stringify(props.value, null, 2)}
    describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled}
    commit={text => {
      if (text.trim() === '') { props.commit(undefined); return }
      try { props.commit(JSON.parse(text) as JsonValue) }
      catch (error) { return `Invalid JSON: ${error instanceof Error ? error.message : String(error)}` }
    }} />
}
