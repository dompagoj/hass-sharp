import { createSignal, onCleanup, type Accessor } from 'solid-js'
import { useQuery, type QueryClient } from '@tanstack/solid-query'
import type { HomeAssistant } from '../types'
import { deviceUiApi, type DeviceCounts, type DevicePageData } from './deviceUi'

/** How often the counts are read again while the page is shown. */
const countsMs = 10_000

export const deviceCountsQueryKey = ['hass-sharp', 'device-counts'] as const

/**
 * The cards waiting for the person on the device pages of every running definition, as one query the whole page shares:
 * read when the page opens and every 10 s while the browser shows it, never while it is hidden, and no more once the
 * page is left. Undefined before the first answer, and while the last read failed: it shows nothing then, and is read
 * again at the next interval.
 */
export function createDeviceCounts(hass: Accessor<Pick<HomeAssistant, 'callWS'>>): Accessor<DeviceCounts | undefined> {
  const [shown, setShown] = createSignal(document.visibilityState !== 'hidden')
  const visibility = () => setShown(document.visibilityState !== 'hidden')
  document.addEventListener('visibilitychange', visibility)
  onCleanup(() => document.removeEventListener('visibilitychange', visibility))
  const query = useQuery(() => ({
    queryKey: [...deviceCountsQueryKey],
    queryFn: () => deviceUiApi(hass()).counts(),
    enabled: shown(),
    staleTime: 0,
    refetchInterval: countsMs,
    refetchIntervalInBackground: false,
    retry: false,
  }))
  return () => (query.isError ? undefined : query.data)
}

/** A definition's counts by node key, if it is listed (a definition id is any text: never an object's own member). */
const nodesOf = (counts: DeviceCounts | undefined, definitionId: string) =>
  counts && Object.hasOwn(counts.attention, definitionId) ? counts.attention[definitionId] : undefined

/** The cards waiting for the person on all of a definition's pages; 0 for one that is not running. */
export const attentionOf = (counts: DeviceCounts | undefined, definitionId: string): number =>
  Object.values(nodesOf(counts, definitionId) ?? {}).reduce((sum, count) => sum + count, 0)

/** The cards waiting for the person on the page of a definition's node, by the node's key; 0 for a node with none. */
export const nodeAttention = (counts: DeviceCounts | undefined, definitionId: string, nodeKey: string): number => {
  const nodes = nodesOf(counts, definitionId)
  return nodes && Object.hasOwn(nodes, nodeKey) ? nodes[nodeKey] : 0
}

/**
 * Puts what the Device tab's `read` answered of a definition into the shared counts, so they follow it at once rather
 * than at their next read: the attention of each of its pages, or none for a definition that is not running (`pages`
 * undefined). Nothing changes before the counts are first read, nor while their last read failed.
 */
export function noteDeviceRead(queryClient: QueryClient, definitionId: string, pages: readonly DevicePageData[] | undefined) {
  if (queryClient.getQueryState([...deviceCountsQueryKey])?.status !== 'success') return
  const waiting = (pages ?? []).filter(page => page.attention > 0).map(page => [page.nodeKey, page.attention] as const)
  queryClient.setQueryData<DeviceCounts>([...deviceCountsQueryKey], counts => {
    if (!counts) return counts
    const others = Object.entries(counts.attention).filter(([id]) => id !== definitionId)
    return { ...counts, attention: Object.fromEntries(waiting.length ? [...others, [definitionId, Object.fromEntries(waiting)]] : others) }
  })
}

/** Makes the shared counts stale, so the page reads them again now (after a press, which may change them). */
export const countsChanged = (queryClient: QueryClient) => void queryClient.invalidateQueries({ queryKey: [...deviceCountsQueryKey] })
