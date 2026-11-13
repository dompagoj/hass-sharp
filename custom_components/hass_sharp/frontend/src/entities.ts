import { useQuery } from '@tanstack/solid-query'
import { useHass } from './context'

export interface EntityInfo {
  entity_id: string
  name: string | null
  domain: string
  area_id: string | null
  device_id: string | null
  device_class: string | null
  supported_features: number | null
  unit: string | null
  icon: string | null
  disabled: boolean
  state_options: string[] | null
}
export interface EntityRegistry {
  entities: EntityInfo[]
  areas: { area_id: string; name: string; floor_id: string | null }[]
  devices: { id: string; name: string | null; area_id: string | null }[]
  floors: { floor_id: string; name: string }[]
  labels: { label_id: string; name: string }[]
}
export const entityQueryKey = ['hass-entities'] as const
export function useEntities() {
  const hass = useHass()
  return useQuery(() => ({
    queryKey: entityQueryKey,
    queryFn: async () => (await hass.callWS<EntityRegistry>({ type: 'hass_sharp/get_entities' })).entities,
    staleTime: Infinity,
    retry: false,
  }))
}
