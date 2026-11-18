import { Match, Switch } from 'solid-js'
import { unwrap } from 'solid-js/store'
import { useNavigate, useParams } from '@solidjs/router'
import { useQuery } from '@tanstack/solid-query'
import { Button } from '../components/Controls'
import { useHass } from '../context'
import { classDefinitionCode, entityAdapter } from '../entities/adapter'
import type { RunTraceResponse } from '../types'
import { automationAdapter, type EditorAdapter } from '../visual/adapter'
import { visualApi, visualError } from '../visual/api'
import { libraryHref } from '../visual/library-api'
import { sharePoll } from '../visual/trace/poll'
import { TracePage, type TraceTexts } from '../visual/trace/TracePage'

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
 *
 * With another adapter (an entity definition's, {@link EntityRunTraceView}) the document is read, and the editor and
 * list linked, through it, in its words; `ownRunsOnly` then shows a run only on its own document's page: a run of
 * another document, a document that no longer exists and one that cannot be read as the adapter's (an entity definition
 * built from a class) each say so instead, and a document that cannot be read just now offers Try again (one read before,
 * which showed the run is its own, keeps the run on the page). An
 * automation's page follows the run's automation, as it always has.
 */
export function RunTraceView(props: { adapter?: EditorAdapter; ownRunsOnly?: boolean } = {}) {
  const hass = useHass()
  const params = useParams()
  const navigate = useNavigate()
  const api = visualApi(hass)
  const adapter = props.adapter ?? (automationAdapter(hass) as unknown as EditorAdapter)
  const texts = adapter.texts
  const own = !!props.ownRunsOnly
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
  // The page's own document, or else (an automation's page) the run's.
  const visualId = () => (own ? params.id : data()?.run.visualId ?? params.id) ?? ''
  // The document as it is now, to say when this run used an earlier version: an automation's read once the run says
  // which, one that shows only its own runs read at once, to know whether the run is its own.
  const current = useQuery(() => ({
    queryKey: [adapter.document === 'automation' ? 'visual-trace-document' : `${adapter.document}-trace-document`, visualId()],
    queryFn: () => adapter.read(visualId()),
    enabled: admin() && (own || !!data()) && !!visualId(),
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
  const editorHref = () => adapter.editorHref(visualId())
  // Back is always the document's Runs tab: the history could lead out of the panel.
  const back = () => navigate(`${editorHref()}?drawer=runs`)
  /**
   * Why a page that shows only its own runs does not show this one: the document is gone, cannot be read as this
   * page's (its own words), could not be read now (`retry`), or the run is another's. The run is known to be its own
   * only once the document is read.
   */
  const refused = (): { text: string; gone?: boolean; retry?: boolean } | undefined => {
    if (!own) return
    const run = data()?.run, document = current.data
    if (current.isError) {
      const code = adapter.apiError(current.error)?.code
      if (code === 'not_found') return { text: texts.traceDeleted ?? 'This automation no longer exists.', gone: true }
      // An entity definition built from a class, which has no recorded runs: the read's own words.
      if (code === classDefinitionCode) return { text: visualError(current.error) }
      // Any other failure of a read again: the document read before still says the run is its own, so it stays.
      if (run && document && run.visualId === params.id && run.automationId === document.automationId) return
      // Otherwise whether the run is its own is not known, so it is not shown until a read succeeds.
      return { text: visualError(current.error), retry: true }
    }
    if (run && (run.visualId !== params.id || (document && run.automationId !== document.automationId)))
      return { text: texts.traceForeign ?? 'This run is not one of this automation’s runs.' }
  }
  const traceTexts = (): Partial<TraceTexts> => ({
    back: texts.traceBack, untitled: texts.traceUntitled, earlier: texts.traceEarlier, missing: texts.traceMissing, noRecord: texts.traceNoRecord, canvas: texts.canvasLabel,
  })
  // Unset texts take the automation's (spreading an undefined would replace them).
  const definedTexts = () => Object.fromEntries(Object.entries(traceTexts()).filter(([, value]) => value !== undefined)) as Partial<TraceTexts>
  return (
    // A run already shown stays on the page if a later read fails.
    <Switch>
      <Match when={!admin()}>
        <p class="hs-notice">Administrator access is required to view run traces.</p>
      </Match>
      <Match when={refused()}>
        {reason => (
          <div class="hs-notice hs-notice--danger" role="alert">
            <h2>This run couldn’t load</h2>
            <p>{reason().text}</p>
            <p>{reason().gone
              ? <Button onClick={() => navigate(adapter.listHref)}>{texts.back}</Button>
              : <>
                {reason().retry && <><Button onClick={() => void current.refetch()}>Try again</Button>{' '}</>}
                <Button onClick={() => navigate(editorHref())}>Open in editor</Button>
              </>}</p>
          </div>
        )}
      </Match>
      {/* A page that shows only its own runs waits for the document, which says whether the run is one of them. */}
      <Match when={own && data() && current.isPending}>
        <div class="hs-empty" role="status">Loading the run…</div>
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
            onOpenLibrary={(id, line) => navigate(libraryHref(id, line))}
            texts={definedTexts()}
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

/**
 * `/entities/:id/runs/:runId`: one run of an entity definition built from steps, read through the entity API, linked
 * back to its editor (`/entities/<id>`) and worded for it; only the definition's own runs are shown.
 */
export function EntityRunTraceView() {
  return <RunTraceView adapter={entityAdapter(useHass()) as unknown as EditorAdapter} ownRunsOnly />
}
