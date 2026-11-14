import type { JsonValue } from '../contract'
import { displayValue, jsonEqual, record, strings } from './json'
import { durationParts, durationSeconds, type Widget } from './model'
import { targetIds } from './pickers'

/** The registry kinds whose ids a summary names. */
export type NamedKind = 'entity' | 'device' | 'area' | 'floor' | 'label'
/** A person-facing name for a registry id; undefined falls back to the id. */
export type NameResolver = (kind: NamedKind, id: string) => string | undefined

const count = (n: number, one: string, many = one + 's') => `${n} ${n === 1 ? one : many}`
const clip = (text: string, max = 40) => text.length > max ? text.slice(0, max - 1) + '…' : text
/** Lists read "30, 60"; objects read "Object, 2 fields"; a list item that is an object or a list reads the same way, never raw JSON. */
const nested = (item: JsonValue) => Array.isArray(item) ? `List, ${count(item.length, 'item')}` : record(item) ? `Object, ${count(Object.keys(item).length, 'field')}` : displayValue(item)
function plain(value: JsonValue): string {
  if (Array.isArray(value)) return value.map(nested).join(', ')
  if (record(value)) return `Object, ${count(Object.keys(value).length, 'field')}`
  return displayValue(value)
}
/**
 * How a chosen option reads, wherever it is shown (the card, the panel, the trace view): the widget's own option label
 * (the schema's label, else the value in words). A value that is not one of the options reads as it is stored, the way
 * the validator names it. One helper, so the three always agree.
 */
export function optionLabel(widget: Widget, value: JsonValue): string {
  return widget.options?.find(o => jsonEqual(o.value, value))?.label ?? displayValue(value)
}
/** A widget in which null is a value someone wrote (the JSON editor), not an empty input. */
export const keepsNull = (widget: Widget) => widget.kind === 'json'
/** Whether a summary of this widget names registry items (and so wants a `NameResolver`). */
export const namesRegistryItems = (widget: Widget) => ['target', 'entity', 'device', 'area', 'floor', 'label'].includes(widget.kind)

/**
 * A one-line description of a value, shown on a node card for a widget that is too large to edit there.
 * Registry ids are named through `names` (entity friendly names, device, area, floor and label names), else shown as ids.
 */
export function valueSummary(widget: Widget, value: JsonValue | null | undefined, names?: NameResolver): string {
  // null is how an empty optional value is often stored: it reads as not set, never as "null"; in a JSON value it is a
  // value (written on purpose) and reads as one.
  if (value === undefined || (value === null && !keepsNull(widget))) return 'Not set'
  if (value === '') return 'Empty text'
  // Entity ids read well enough without a name; device, area, floor and label ids may be GUIDs, so an unnamed one is
  // only counted ("1 device"), never shown (design principle 2).
  const named = (kind: NamedKind, list: string[]) => {
    if (kind === 'entity') return list.map(id => names?.(kind, id) || id)
    const known = list.map(id => names?.(kind, id)).filter((n): n is string => !!n)
    const unnamed = list.length - known.length
    return unnamed ? [...known, count(unnamed, kind)] : known
  }
  switch (widget.kind) {
    case 'target': {
      const ids = targetIds(value)
      const total = ids.entity_id.length + ids.device_id.length + ids.area_id.length + ids.floor_id.length + ids.label_id.length
      if (!total) return 'No target'
      if (total <= 3) return clip([...named('entity', ids.entity_id), ...named('device', ids.device_id), ...named('area', ids.area_id),
        ...named('floor', ids.floor_id), ...named('label', ids.label_id)].join(', '))
      return [count(ids.entity_id.length, 'entity', 'entities'), count(ids.device_id.length, 'device'), count(ids.area_id.length, 'area'),
        count(ids.floor_id.length, 'floor'), count(ids.label_id.length, 'label')].filter(text => !text.startsWith('0 ')).join(', ')
    }
    case 'entity': case 'device': case 'area': case 'floor': case 'label': {
      const ids = strings(value)
      return clip(ids.length ? named(widget.kind as NamedKind, ids).join(', ') : plain(value))
    }
    case 'select': case 'state': {
      const label = (item: JsonValue) => optionLabel(widget, item)
      return clip(Array.isArray(value) ? value.map(label).join(', ') : label(value))
    }
    case 'duration': {
      const parts = durationParts(widget, durationSeconds(value) ?? 0)
      const text = ([['days', 'd'], ['hours', 'h'], ['minutes', 'min'], ['seconds', 's']] as const).filter(([k]) => parts[k]).map(([k, u]) => `${parts[k]} ${u}`).join(' ')
      return text || '0 s'
    }
    case 'color': return Array.isArray(value) ? `RGB ${value.join(', ')}` : clip(plain(value))
    case 'text': {
      if (Array.isArray(value)) return clip(plain(value))
      const text = displayValue(value)
      return clip(text.split('\n')[0] + (text.includes('\n') ? ' …' : ''))
    }
    default: return clip(plain(value))
  }
}
