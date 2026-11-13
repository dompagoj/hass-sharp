import { Match, Switch } from 'solid-js'
import { unwrap } from 'solid-js/store'
import { useNavigate, useParams } from '@solidjs/router'
import { useQuery } from '@tanstack/solid-query'
import { Button } from '../components/Controls'
import { useHass } from '../context'
import { visualApi, visualError } from '../visual/api'
import { TracePage } from '../visual/trace/TracePage'

/** While a run is pending or running, its trace is read again this often. */
export const LIVE_POLL_MS = 2000

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
    // Each poll replaces the response whole (no store reconciliation): the page gets plain objects, as sent.
    reconcile: false,
    refetchInterval: query => (query.state.status !== 'error' && query.state.data?.live ? LIVE_POLL_MS : false),
  }))
  // The automation as it is now, only to say when this run used an earlier version.
  const current = useQuery(() => ({
    queryKey: ['visual-trace-document', params.id],
    queryFn: () => api.read(params.id!),
    enabled: admin() && !!params.id,
    staleTime: 30_000,
  }))
  // Solid Query keeps results in a store; the graph and catalog are read (and copied by the validator) as plain data.
  const data = () => (trace.data ? unwrap(trace.data) : undefined)
  const editorHref = () => `/visual/${encodeURIComponent(params.id ?? '')}`
  const back = () => (history.length > 1 ? navigate(-1) : navigate(editorHref()))
  return (
    // A run already shown stays on the page if a later poll fails.
    <Switch>
      <Match when={!admin()}>
        <p class="hs-notice">Administrator access is required to view run traces.</p>
      </Match>
      <Match when={data()}>
        {response => <TracePage response={response()} currentRevision={current.data?.revision} editorHref={editorHref()} onBack={back} />}
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
