import { createSignal } from 'solid-js'
import type { QueryClient } from '@tanstack/solid-query'
import type { AutomationRun, HomeAssistant, RunAcceptedResponse } from '../types'

/** One manual-run action of a saved automation: a class and one of its methods. */
export interface RunAction { className: string; method: string }

/** A run request's error as one sentence for the page. */
export const runErrorMessage = (error: unknown) => {
  const e = error as { message?: string; body?: { error?: string }; error?: string }
  return e?.body?.error ?? e?.message ?? e?.error ?? 'Check your connection and try again.'
}

// The last accepted run of each automation, wherever it was started (the editor's top bar or the Runs list), so the
// Runs list keeps polling until that run has finished.
const [accepted, setAccepted] = createSignal<ReadonlyMap<string, { runId: string; at: number }>>(new Map())
export const acceptedRun = (automationId: string) => accepted().get(automationId)
export const forgetAcceptedRun = (automationId: string) =>
  setAccepted(current => {
    if (!current.has(automationId)) return current
    const next = new Map(current)
    next.delete(automationId)
    return next
  })

const active = (run: AutomationRun) => run.status === 'Pending' || run.status === 'Running'
const QUICK_MS = 60_000

/**
 * How often a page showing the newest runs of an automation reads them again (a TanStack `refetchInterval`), shared by
 * the Runs list and the editor's "Last run": every 2 s while a run accepted in the last minute has not ended (it may
 * not be recorded yet) or the newest unfinished run started under a minute ago, then every 15 s while a run is still
 * Pending or Running, and not at all once none is. Polling also stops when no page shows the history any more.
 */
export function runHistoryInterval(automationId: string, runs: readonly AutomationRun[], now = Date.now()): number | false {
  const request = acceptedRun(automationId)
  if (request && now - request.at < QUICK_MS && !runs.some(run => run.runId === request.runId && !active(run))) return 2000
  const running = runs.filter(active)
  if (!running.length) return false
  const newest = Math.max(...running.map(run => Date.parse(run.startUtc ?? run.receivedUtc) || 0))
  return now - newest < QUICK_MS ? 2000 : 15_000
}

/**
 * Asks Home Assistant to run one action of the saved automation now (`hass_sharp/run_now`). On acceptance the run is
 * remembered for polling and the automation's run history is refetched. Rejects with the request's error.
 */
export async function dispatchRun(
  hass: Pick<HomeAssistant, 'callWS'>,
  automationId: string,
  action: RunAction,
  queryClient?: QueryClient,
): Promise<RunAcceptedResponse> {
  forgetAcceptedRun(automationId)
  const response = await hass.callWS<RunAcceptedResponse>({
    type: 'hass_sharp/run_now', automationId, className: action.className, method: action.method,
  })
  setAccepted(current => new Map(current).set(automationId, { runId: response.runId, at: Date.now() }))
  void queryClient?.invalidateQueries({ queryKey: ['run-history', automationId] })
  return response
}
