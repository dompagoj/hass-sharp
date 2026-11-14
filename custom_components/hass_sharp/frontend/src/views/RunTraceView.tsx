import { Match, Switch } from 'solid-js'
import { unwrap } from 'solid-js/store'
import { useNavigate, useParams } from '@solidjs/router'
import { useQuery } from '@tanstack/solid-query'
import { Button } from '../components/Controls'
import { useHass } from '../context'
import type { RunTraceResponse } from '../types'
import { visualApi, visualError } from '../visual/api'
import { sharePoll } from '../visual/trace/poll'
import { TracePage } from '../visual/trace/TracePage'

/** While a run is pending or running, its trace is read again this often. */
export const LIVE_POLL_MS = 2000
/** After a read of a live run got no answer, the next one waits this long. */
export const RETRY_POLL_MS = 5000

/**
 * A read that got no answer (the connection dropped, Home Assistant restarting) rather than an error reply from the
 * integration: the websocket library rejects those with a numeric code, the integration with a word.
 */
export const unanswered = (error: unknown) => typeof (error as { code?: unknown } | null)?.code !== 'string'

/**
 * `/visual/:id/runs/:runId`: one run of a visual automation, read-only (contract C9 `run_trace`). The graph shown is
 * the one stored with the run, not the automation as it is now.
 */
export function RunTraceView() {
  const hass = useHass()
  const params = useParams()
  const navigate = useNavigate()
  const api = visualApi(hass)
  const admin = () => !!hass.user?.is_admin
  const trace = useQuery(() => ({
    queryKey: ['run-trace', params.runId],
    queryFn: () => api.runTrace(params.runId!),
    enabled: admin() && !!params.runId,
    staleTime: 0,
    // Each reply is kept as plain objects (no store reconciliation), sharing what did not change with the one before.
    // Solid Query also calls this with no data (another run's query starting): that passes through untouched.
    reconcile: (previous: RunTraceResponse | undefined, next: RunTraceResponse | undefined) =>
      previous === undefined || next === undefined ? next : sharePoll(unwrap(previous), next),
    // A live run is read again until it ends. A read with no answer (the last good reply stays) is tried again, less
    // often; an error reply is not, until the person asks.
    refetchInterval: query => {
      if (!query.state.data?.live) return false
      if (query.state.status !== 'error') return LIVE_POLL_MS
      return unanswered(query.state.error) ? RETRY_POLL_MS : false
    },
  }))
  // Solid Query keeps results in a store; the graph and catalog are read (and copied by the validator) as plain data.
  const data = () => (trace.data ? unwrap(trace.data) : undefined)
  const visualId = () => data()?.run.visualId ?? params.id ?? ''
  // The automation as it is now, only to say when this run used an earlier version: read once the run says which.
  const current = useQuery(() => ({
    queryKey: ['visual-trace-document', visualId()],
    queryFn: () => api.read(visualId()),
    enabled: admin() && !!data() && !!visualId(),
    staleTime: 30_000,
  }))
  // The services as they are now, to show service fields with: a run's stored snapshot may have no service schema (C4).
  // TracePage uses them only when it has none.
  const services = useQuery(() => ({
    queryKey: ['visual-services'],
    queryFn: () => api.services(),
    enabled: admin() && !!data(),
    staleTime: 60_000,
  }))
  const editorHref = () => `/visual/${encodeURIComponent(visualId())}`
  // Back is always the automation's Runs tab: the history could lead out of the panel.
  const back = () => navigate(`${editorHref()}?drawer=runs`)
  return (
    // A run already shown stays on the page if a later read fails.
    <Switch>
      <Match when={!admin()}>
        <p class="hs-notice">Administrator access is required to view run traces.</p>
      </Match>
      <Match when={data()}>
        {response => (
          <TracePage
            response={response()}
            currentRevision={current.data?.revision}
            editorHref={editorHref()}
            onBack={back}
            paused={trace.isError && response().live ? (unanswered(trace.error) ? 'could not reach Home Assistant' : visualError(trace.error)) : undefined}
            retrying={trace.isError && trace.isFetching}
            onRetry={() => void trace.refetch()}
            services={services.data ? unwrap(services.data) : undefined}
          />
        )}
      </Match>
      <Match when={trace.isPending}>
        <div class="hs-empty" role="status">Loading the run…</div>
      </Match>
      <Match when={trace.isError}>
        <div class="hs-notice hs-notice--danger" role="alert">
          <h2>This run couldn’t load</h2>
          <p>{visualError(trace.error)}</p>
          <p><Button onClick={() => void trace.refetch()}>Try again</Button> <Button onClick={() => navigate(editorHref())}>Open in editor</Button></p>
        </div>
      </Match>
    </Switch>
  )
}
