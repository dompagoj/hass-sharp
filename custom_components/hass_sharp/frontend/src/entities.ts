import { useQuery } from '@tanstack/solid-query'
import { useHass } from './context'

export interface EntitySuggestion { entity_id: string; friendly_name?: string }
export const entityQueryKey = ['hass-entities'] as const
export function useEntities() {
  const hass = useHass()
  return useQuery(() => ({
    queryKey: entityQueryKey,
    queryFn: async () => (await hass.callWS<{ entities: EntitySuggestion[] }>({ type: 'hass_sharp/get_entities' })).entities,
    staleTime: Infinity,
    retry: false,
  }))
}
