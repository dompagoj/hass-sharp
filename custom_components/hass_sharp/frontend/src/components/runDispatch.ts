import { createSignal } from 'solid-js'
import type { QueryClient } from '@tanstack/solid-query'
import type { HomeAssistant, RunAcceptedResponse } from '../types'

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
