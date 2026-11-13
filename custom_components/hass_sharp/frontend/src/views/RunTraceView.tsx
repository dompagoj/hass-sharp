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
/** After a read of a live run failed, the next one waits this long. */
export const RETRY_POLL_MS = 5000

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
    reconcile: (previous: RunTraceResponse | undefined, next: RunTraceResponse) => sharePoll(previous && unwrap(previous), next),
    // A live run is read again until it ends; a failed read (the last good reply stays) is tried again, less often.
    refetchInterval: query => (query.state.data?.live ? (query.state.status === 'error' ? RETRY_POLL_MS : LIVE_POLL_MS) : false),
  }))
  // Solid Query keeps results in a store; the graph and catalog are read (and copied by the validator) as plain data.
  const data = () => (trace.data ? unwrap(trace.data) : undefined)
  const visualId = () => data()?.run.visualId ?? params.id ?? ''
  // The automation as it is now, only to say when this run used an earlier version.
  const current = useQuery(() => ({
    queryKey: ['visual-trace-document', visualId()],
    queryFn: () => api.read(visualId()),
    enabled: admin() && !!visualId(),
    staleTime: 30_000,
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
            paused={trace.isError && !trace.isFetching && response().live}
            retrying={trace.isFetching}
            onRetry={() => void trace.refetch()}
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
