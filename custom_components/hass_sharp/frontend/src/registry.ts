import { useQuery } from '@tanstack/solid-query'
import { useHass } from './context'
import type { EntityInfo, EntityRegistry } from './entities'
import type { HomeAssistant } from './types'

export type Area = EntityRegistry['areas'][number]
export type Device = EntityRegistry['devices'][number]
export type Floor = EntityRegistry['floors'][number]
export type Label = EntityRegistry['labels'][number]
/** Contract C7 with every list present, plus lookups by id. */
export interface Registry extends EntityRegistry {
  area(id: string): Area | undefined
  device(id: string): Device | undefined
  floor(id: string): Floor | undefined
  label(id: string): Label | undefined
}

const list = <T>(value: unknown): T[] => (Array.isArray(value) ? (value as T[]) : [])
/** Normalise a `hass_sharp/get_entities` reply; missing lists become empty. */
export function createRegistry(raw: Partial<EntityRegistry> | null | undefined): Registry {
  const entities = list<EntityInfo>(raw?.entities), areas = list<Area>(raw?.areas), devices = list<Device>(raw?.devices)
  const floors = list<Floor>(raw?.floors), labels = list<Label>(raw?.labels)
  const lookup = <T>(items: T[], id: (item: T) => string) => {
    const map = new Map(items.map(item => [id(item), item]))
    return (key: string) => map.get(key)
  }
  return {
    entities, areas, devices, floors, labels,
    area: lookup(areas, a => a.area_id),
    device: lookup(devices, d => d.id),
    floor: lookup(floors, f => f.floor_id),
    label: lookup(labels, l => l.label_id),
  }
}

export const entityQueryKey = ['hass-entities'] as const
/** One `get_entities` request shared by every registry and entity consumer. */
export const registryQuery = (hass: Pick<HomeAssistant, 'callWS'>) => ({
  queryKey: entityQueryKey,
  queryFn: async () => createRegistry(await hass.callWS<EntityRegistry>({ type: 'hass_sharp/get_entities' })),
  staleTime: Infinity,
  retry: false,
})
export function useRegistry() {
  const hass = useHass()
  return useQuery(() => registryQuery(hass))
}

interface EntityFilter { domain: string[]; deviceClass: string[]; supportedFeatures: number[] }
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const values = (value: unknown) => (value === undefined || value === null ? [] : Array.isArray(value) ? value : [value])
const strings = (value: unknown) => values(value).filter((v): v is string => typeof v === 'string' && v !== '')
const filter = (raw: Record<string, unknown>): EntityFilter => ({
  domain: strings(raw.domain),
  deviceClass: strings(raw.deviceClass ?? raw.device_class),
  supportedFeatures: values(raw.supportedFeatures ?? raw.supported_features).filter((v): v is number => typeof v === 'number'),
})
/** The alternatives an input allows: a C4 target, a C4 field `filter`, or a raw Home Assistant entity/target selector. */
function filters(input: unknown): EntityFilter[] {
  if (!record(input)) return []
  if (record(input.target)) return filters(input.target)
  if (Array.isArray(input.entity)) return input.entity.filter(record).map(filter)
  if (record(input.entity)) {
    const entity = input.entity
    return entity.filter !== undefined ? values(entity.filter).filter(record).map(filter) : [filter(entity)]
  }
  return 'entity' in input || 'device' in input ? [] : [filter(input)]
}
const accepts = (entity: EntityInfo, f: EntityFilter) =>
  (!f.domain.length || f.domain.includes(entity.domain)) &&
  (!f.deviceClass.length || (entity.device_class !== null && f.deviceClass.includes(entity.device_class))) &&
  (!f.supportedFeatures.length || f.supportedFeatures.some(feature => ((entity.supported_features ?? 0) & feature) !== 0))
/**
 * A predicate over registry entities, matching Home Assistant's frontend selector filtering: an entity passes when
 * any alternative matches; within one, any listed domain and any device class. Each supported-features value matches
 * when the entity shares any of its bits (`(features & value) !== 0`), and several values are OR-ed.
 * No filter accepts all.
 */
export function entityFilter(input: unknown): (entity: EntityInfo) => boolean {
  const alternatives = filters(input)
  const unrestricted = !alternatives.length || alternatives.some(f => !f.domain.length && !f.deviceClass.length && !f.supportedFeatures.length)
  return unrestricted ? () => true : entity => alternatives.some(f => accepts(entity, f))
}
