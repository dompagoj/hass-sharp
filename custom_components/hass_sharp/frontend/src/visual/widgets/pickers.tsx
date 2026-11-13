import { createMemo, Show } from 'solid-js'
import { EntityAutocomplete } from '../../components/EntityAutocomplete'
import { useEntities, type EntityInfo } from '../../entities'
import { entityFilter, useRegistry, type Registry } from '../../registry'
import type { JsonValue } from '../contract'
import { domainWords } from '../typeLabels'
import { Chips, type WidgetProps } from './common'
import { ListWidget, SelectWidget, TextWidget } from './basic'
import { record, strings } from './json'
import type { Widget, WidgetOption } from './model'
import { SearchSelect, type SearchOption } from './SearchSelect'

/** Entities an entity or target widget offers: the input's `entity_id:<d>` domains and its selector filter. */
export function entityPredicate(widget: Widget): (entity: EntityInfo) => boolean {
  const byFilter = entityFilter(widget.filter), domains = widget.domains ?? []
  return entity => (!domains.length || domains.includes(entity.domain)) && byFilter(entity)
}
/** Domains named by an entity or target selector; empty when any alternative allows every domain. */
function selectorDomains(selector: unknown): string[] {
  if (!record(selector)) return []
  if (record(selector.target)) return selectorDomains(selector.target)
  const entity = selector.entity
  const alternatives = Array.isArray(entity) ? entity : record(entity) ? (entity.filter !== undefined ? (Array.isArray(entity.filter) ? entity.filter : [entity.filter]) : [entity]) : []
  const domains = alternatives.filter(record).map(a => strings(a.domain))
  return !domains.length || domains.some(d => !d.length) ? [] : [...new Set(domains.flat())]
}
/**
 * Why a typed entity ID cannot be used: picked and typed IDs pass the same domain rule (the input's domains,
 * else the selector's). Device class and feature filters cannot be checked for an entity that is not listed.
 */
export function manualEntityError(widget: Widget): (id: string) => string | undefined {
  const domains = widget.domains?.length ? widget.domains : selectorDomains(widget.filter)
  const words = domains.map(d => domainWords(d).toLowerCase()).join(' or ')
  return id => !domains.length || domains.includes(id.slice(0, id.indexOf('.'))) ? undefined : `${id} cannot be used here: only ${words} entities fit.`
}

export function EntityWidget(props: WidgetProps) {
  const accepts = createMemo(() => entityPredicate(props.widget))
  const manual = createMemo(() => manualEntityError(props.widget))
  return <Show when={props.widget.multiple} fallback={<EntityAutocomplete id={props.id} label={props.label} hideLabel filter={accepts()} manualError={manual()}
    value={typeof props.value === 'string' ? props.value : ''} onChange={id => props.commit(id || undefined)}
    describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled} />}>
    <EntityAutocomplete id={props.id} label={props.label} hideLabel filter={accepts()} manualError={manual()} multiple values={strings(props.value)}
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

const registryKinds = { device: 'Device', area: 'Area', floor: 'Floor', label: 'Label' } as const
function registryOptions(registry: Registry | undefined, kind: string): SearchOption[] {
  if (!registry) return []
  switch (kind) {
    case 'device': return registry.devices.map(d => ({ value: d.id, label: d.name ?? d.id, detail: d.area_id ? registry.area(d.area_id)?.name : undefined }))
    case 'area': return registry.areas.map(a => ({ value: a.area_id, label: a.name, detail: a.floor_id ? registry.floor(a.floor_id)?.name : undefined }))
    case 'floor': return registry.floors.map(f => ({ value: f.floor_id, label: f.name }))
    case 'label': return registry.labels.map(l => ({ value: l.label_id, label: l.name }))
    default: return []
  }
}
/** Device, area, floor and label pickers over the shared registry, searchable as you type. */
export function RegistryWidget(props: WidgetProps) {
  const registry = useRegistry()
  const options = () => registryOptions(registry.data, props.widget.kind)
  const name = (id: JsonValue) => options().find(o => o.value === id)?.label ?? String(id)
  return <Show when={props.widget.multiple} fallback={<SearchSelect id={props.id} label={props.label} options={options()}
    value={typeof props.value === 'string' ? props.value : undefined} onSelect={id => props.commit(id)} placeholder="Not set"
    describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled} />}>
    <ListWidget {...props} labelFor={name} add={add => <SearchSelect id={props.id} label={`Add to ${props.label}`} clearOnSelect placeholder="Add…"
      options={options().filter(o => !strings(props.value).includes(o.value))} onSelect={add}
      describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled} />} />
  </Show>
}

const targetKeys = [['entity_id', 'Entity'], ['device_id', 'Device'], ['area_id', 'Area'], ['floor_id', 'Floor'], ['label_id', 'Label']] as const
type TargetKey = typeof targetKeys[number][0]
type TargetIds = Record<TargetKey, string[]>
/** The ids of a target value: a single entity id string or `{entity_id, device_id, area_id, floor_id, label_id}`. */
export function targetIds(value: JsonValue | undefined): TargetIds {
  const source = typeof value === 'string' ? { entity_id: value } : record(value) ? value : {}
  return Object.fromEntries(targetKeys.map(([key]) => [key, strings(source[key])])) as TargetIds
}
/**
 * The target value for edited ids. A plain entity string stays one while it holds exactly one entity and nothing
 * else, and only when the input accepts an entity id (`entityString`); otherwise targets use the object form.
 * A key that held one string keeps that shape; other keys of the object are kept.
 */
export function targetValue(previous: JsonValue | undefined, ids: TargetIds, entityString = true): JsonValue | undefined {
  const count = targetKeys.reduce((sum, [key]) => sum + ids[key].length, 0)
  if (!count) return undefined
  if (entityString && typeof previous === 'string' && count === 1 && ids.entity_id.length === 1) return ids.entity_id[0]
  const base = record(previous) ? previous as Record<string, JsonValue> : {}
  const next: Record<string, JsonValue> = Object.fromEntries(Object.entries(base).filter(([key]) => !targetKeys.some(([k]) => k === key)))
  for (const [key] of targetKeys) if (ids[key].length) next[key] = typeof base[key] === 'string' && ids[key].length === 1 ? ids[key][0] : ids[key]
  return next
}
/** Entity, device, area, floor and label chips; entities are filtered by the input's domains or selector. */
export function TargetWidget(props: WidgetProps) {
  const registry = useRegistry()
  const accepts = createMemo(() => entityPredicate(props.widget))
  const manual = createMemo(() => manualEntityError(props.widget))
  const ids = () => targetIds(props.value)
  const set = (key: TargetKey, list: string[]) => props.commit(targetValue(props.value, { ...ids(), [key]: list }, props.widget.entityString === true))
  const name = (key: TargetKey, id: string) => {
    const r = registry.data
    const found = key === 'entity_id' ? r?.entities.find(e => e.entity_id === id)?.name : key === 'device_id' ? r?.device(id)?.name
      : key === 'area_id' ? r?.area(id)?.name : key === 'floor_id' ? r?.floor(id)?.name : r?.label(id)?.name
    return found || id
  }
  const chips = () => targetKeys.flatMap(([key, prefix]) => ids()[key].map(id => ({ key: JSON.stringify([key, id]), label: name(key, id), title: id, prefix })))
  const others = () => (Object.keys(registryKinds) as (keyof typeof registryKinds)[]).flatMap(kind => {
    const key = `${kind}_id` as TargetKey
    return registryOptions(registry.data, kind).filter(o => !ids()[key].includes(o.value))
      .map(o => ({ ...o, value: JSON.stringify([key, o.value]), group: `${registryKinds[kind]}s` }))
  })
  return <div class="hs-target" role="group" aria-label={props.label}>
    <Chips label={`${props.label} items`} items={chips()} disabled={props.disabled} fallbackFocusId={props.id}
      onRemove={chip => { const [key, id] = JSON.parse(chip) as [TargetKey, string]; set(key, ids()[key].filter(v => v !== id)) }} />
    <div class="hs-target-add">
      <EntityAutocomplete id={props.id} label={`${props.label} entities`} hideLabel buttonText={props.compact ? 'Entities…' : 'Choose entities…'} filter={accepts()} manualError={manual()}
        multiple values={ids().entity_id} onChangeValues={list => set('entity_id', list)} invalid={props.invalid} disabled={props.disabled} describedBy={props.describedBy} />
      <Show when={others().length}>
        <SearchSelect label={`Add device, area, floor or label to ${props.label}`} clearOnSelect options={others()}
          placeholder={props.compact ? 'Add…' : 'Add device, area, floor or label…'} describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled}
          onSelect={raw => { const [key, id] = JSON.parse(raw) as [TargetKey, string]; set(key, [...ids()[key], id]) }} />
      </Show>
    </div>
  </div>
}
