import { createMemo, For, Show } from 'solid-js'
import { EntityAutocomplete } from '../../components/EntityAutocomplete'
import { useEntities, type EntityInfo } from '../../entities'
import { entityFilter, useRegistry, type Registry } from '../../registry'
import type { JsonValue } from '../contract'
import { Chips, type WidgetProps } from './common'
import { SelectWidget, TextWidget } from './basic'
import type { Widget, WidgetOption } from './model'

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const strings = (value: unknown) => (Array.isArray(value) ? value : value === undefined || value === null ? [] : [value]).filter((v): v is string => typeof v === 'string' && v !== '')
/** Entities an entity or target widget offers: the input's `entity_id:<d>` domains and its selector filter. */
export function entityPredicate(widget: Widget): (entity: EntityInfo) => boolean {
  const byFilter = entityFilter(widget.filter), domains = widget.domains ?? []
  return entity => (!domains.length || domains.includes(entity.domain)) && byFilter(entity)
}

export function EntityWidget(props: WidgetProps) {
  const accepts = createMemo(() => entityPredicate(props.widget))
  return <Show when={props.widget.multiple} fallback={<EntityAutocomplete id={props.id} label={props.label} hideLabel filter={accepts()}
    value={typeof props.value === 'string' ? props.value : ''} onChange={id => props.commit(id || undefined)}
    describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled} />}>
    <EntityAutocomplete id={props.id} label={props.label} hideLabel filter={accepts()} multiple values={strings(props.value)}
      onChangeValues={ids => props.commit(ids.length ? ids : undefined)} describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled} />
  </Show>
}

/** Contract C3 "state": the union of `state_options` of the target entities; free text without any. */
export function StateWidget(props: WidgetProps) {
  const entities = useEntities()
  const options = createMemo((): WidgetOption[] => {
    if (props.widget.stateAttribute) return []
    const ids = new Set([...(props.context?.targetEntityIds ?? []), ...(props.widget.stateEntities ?? [])])
    const hidden = new Set(props.widget.hideStates ?? []), found: string[] = []
    for (const entity of entities.data ?? []) if (ids.has(entity.entity_id))
      for (const state of entity.state_options ?? []) if (!hidden.has(state) && !found.includes(state)) found.push(state)
    return found.map(value => ({ value, label: value }))
  })
  return <Show when={options().length} fallback={<TextWidget {...props} widget={{ ...props.widget, kind: 'text' }} />}>
    <SelectWidget {...props} widget={{ ...props.widget, kind: 'select', options: options() }} />
  </Show>
}

function registryOptions(registry: Registry | undefined, kind: string): WidgetOption[] {
  if (!registry) return []
  switch (kind) {
    case 'device': return registry.devices.map(d => ({ value: d.id, label: d.name ?? d.id }))
    case 'area': return registry.areas.map(a => ({ value: a.area_id, label: a.name }))
    case 'floor': return registry.floors.map(f => ({ value: f.floor_id, label: f.name }))
    case 'label': return registry.labels.map(l => ({ value: l.label_id, label: l.name }))
    default: return []
  }
}
/** Device, area, floor and label pickers over the shared registry. */
export function RegistryWidget(props: WidgetProps) {
  const registry = useRegistry()
  return <SelectWidget {...props} widget={{ ...props.widget, options: registryOptions(registry.data, props.widget.kind) }} />
}

const targetKeys = [['entity_id', 'Entity'], ['device_id', 'Device'], ['area_id', 'Area'], ['floor_id', 'Floor'], ['label_id', 'Label']] as const
type TargetKey = typeof targetKeys[number][0]
type TargetIds = Record<TargetKey, string[]>
/** The ids of a target literal: a single entity id string or `{entity_id, device_id, area_id, floor_id, label_id}`. */
export function targetIds(value: JsonValue | undefined): TargetIds {
  const source = typeof value === 'string' ? { entity_id: value } : record(value) ? value : {}
  return Object.fromEntries(targetKeys.map(([key]) => [key, strings(source[key])])) as TargetIds
}
/**
 * The target literal for edited ids. A target that was a plain entity string stays one while it holds exactly one
 * entity and nothing else; new targets use the object form. A key that held one string keeps that shape.
 */
export function targetValue(previous: JsonValue | undefined, ids: TargetIds): JsonValue | undefined {
  const count = targetKeys.reduce((sum, [key]) => sum + ids[key].length, 0)
  if (!count) return undefined
  if (typeof previous === 'string' && count === 1 && ids.entity_id.length === 1) return ids.entity_id[0]
  const base = record(previous) ? previous as Record<string, JsonValue> : {}
  const next: Record<string, JsonValue> = Object.fromEntries(Object.entries(base).filter(([key]) => !targetKeys.some(([k]) => k === key)))
  for (const [key] of targetKeys) if (ids[key].length) next[key] = typeof base[key] === 'string' && ids[key].length === 1 ? ids[key][0] : ids[key]
  return next
}
/** Entity, device, area, floor and label chips; entities are filtered by the input's domains or selector. */
export function TargetWidget(props: WidgetProps) {
  const registry = useRegistry()
  const accepts = createMemo(() => entityPredicate(props.widget))
  const ids = () => targetIds(props.value)
  const set = (key: TargetKey, list: string[]) => props.commit(targetValue(props.value, { ...ids(), [key]: list }))
  const name = (key: TargetKey, id: string) => {
    const r = registry.data
    const found = key === 'entity_id' ? r?.entities.find(e => e.entity_id === id)?.name : key === 'device_id' ? r?.device(id)?.name
      : key === 'area_id' ? r?.area(id)?.name : key === 'floor_id' ? r?.floor(id)?.name : r?.label(id)?.name
    return found || id
  }
  const chips = () => targetKeys.flatMap(([key, prefix]) => ids()[key].map(id => ({ key: JSON.stringify([key, id]), label: name(key, id), title: id, prefix })))
  const groups = () => targetKeys.slice(1).map(([key, prefix]) => ({
    key, label: `${prefix}s`,
    options: registryOptions(registry.data, prefix.toLowerCase()).filter(o => !ids()[key].includes(o.value as string)),
  })).filter(group => group.options.length)
  return <div class="hs-target" role="group" aria-label={props.label} aria-describedby={props.describedBy}>
    <Chips label={`${props.label} items`} items={chips()} disabled={props.disabled}
      onRemove={chip => { const [key, id] = JSON.parse(chip) as [TargetKey, string]; set(key, ids()[key].filter(v => v !== id)) }} />
    <div class="hs-target-add">
      <EntityAutocomplete id={props.id} label={`${props.label} entities`} hideLabel buttonText={props.compact ? 'Entities…' : 'Choose entities…'} filter={accepts()} multiple values={ids().entity_id}
        onChangeValues={list => set('entity_id', list)} invalid={props.invalid} disabled={props.disabled} describedBy={props.describedBy} />
      <Show when={groups().length}>
        <select class="hs-input" aria-label={`Add device, area, floor or label to ${props.label}`} disabled={props.disabled}
          onChange={event => {
            const raw = event.currentTarget.value
            event.currentTarget.value = ''
            if (!raw) return
            const [key, id] = JSON.parse(raw) as [TargetKey, string]
            set(key, [...ids()[key], id])
          }}>
          <option value="" selected>{props.compact ? 'Add…' : 'Add device, area, floor or label…'}</option>
          <For each={groups()}>{group => <optgroup label={group.label}>
            <For each={group.options}>{option => <option value={JSON.stringify([group.key, option.value])}>{option.label}</option>}</For>
          </optgroup>}</For>
        </select>
      </Show>
    </div>
  </div>
}
