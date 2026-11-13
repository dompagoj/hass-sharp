import type { JsonValue } from '../contract'
import { displayValue } from './json'
import { durationParts, durationSeconds, type Widget } from './model'
import { targetIds } from './pickers'

const count = (n: number, one: string, many = one + 's') => `${n} ${n === 1 ? one : many}`
const clip = (text: string, max = 40) => text.length > max ? text.slice(0, max - 1) + '…' : text
/** A one-line description of a value, shown on a node card for a widget that is too large to edit there. */
export function valueSummary(widget: Widget, value: JsonValue | undefined): string {
  if (value === undefined) return 'Not set'
  if (value === '') return 'Empty text'
  switch (widget.kind) {
    case 'target': {
      const ids = targetIds(value)
      if (ids.entity_id.length === 1 && Object.values(ids).flat().length === 1) return ids.entity_id[0]
      return [count(ids.entity_id.length, 'entity', 'entities'), count(ids.device_id.length, 'device'), count(ids.area_id.length, 'area'),
        count(ids.floor_id.length, 'floor'), count(ids.label_id.length, 'label')].filter(text => !text.startsWith('0 ')).join(', ')
    }
    case 'duration': {
      const parts = durationParts(widget, durationSeconds(value) ?? 0)
      const text = ([['days', 'd'], ['hours', 'h'], ['minutes', 'min'], ['seconds', 's']] as const).filter(([k]) => parts[k]).map(([k, u]) => `${parts[k]} ${u}`).join(' ')
      return text || '0 s'
    }
    case 'color': return Array.isArray(value) ? `RGB ${value.join(', ')}` : clip(displayValue(value))
    case 'text': return Array.isArray(value) ? clip(value.map(displayValue).join(', ')) : clip(displayValue(value).split('\n')[0] + (displayValue(value).includes('\n') ? ' …' : ''))
    default: return Array.isArray(value) && widget.multiple ? clip(value.map(displayValue).join(', ')) : clip(displayValue(value))
  }
}
