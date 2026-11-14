import type { QueryClient } from '@tanstack/solid-query'
import type { HomeAssistant } from '../types'
import { visualApi, type HomeAutomation } from '../visual/api'

/** The automations page's list query; the editor invalidates `['visual-automations']` after a save, which covers it. */
export const homeQueryKey = ['visual-automations', 'home'] as const

/** Shows `enabled` for automation `id` in the cached list, at once (an optimistic update or its rollback). */
export function showEnabled(queryClient: QueryClient | undefined, id: string, enabled: boolean) {
  queryClient?.setQueryData<HomeAutomation[]>([...homeQueryKey], list => list?.map(item => (item.id === id ? { ...item, enabled } : item)))
}

/**
 * Turns an automation on or off. The cached list shows the new state before the request finishes and the old one
 * again if it fails; the returned promise rejects with the request's error then.
 */
export async function setAutomationEnabled(hass: Pick<HomeAssistant, 'callWS'>, queryClient: QueryClient | undefined, id: string, enabled: boolean) {
  // A list request already on its way must not put the old state back.
  await queryClient?.cancelQueries({ queryKey: [...homeQueryKey] })
  showEnabled(queryClient, id, enabled)
  try {
    const result = await visualApi(hass).setEnabled(id, enabled)
    showEnabled(queryClient, id, result.enabled)
    return result.enabled
  } catch (error) {
    showEnabled(queryClient, id, !enabled)
    throw error
  }
}
