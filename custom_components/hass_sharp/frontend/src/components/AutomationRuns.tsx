import { batch, createEffect, createSignal, createUniqueId, For, on, onCleanup, Show, useContext } from 'solid-js'
import { A, RouterContext } from '@solidjs/router'
import { useQuery } from '@tanstack/solid-query'
import { useHass } from '../context'
import type { AutomationRun, RunHistoryResponse, RunStatus } from '../types'
import { acceptedRun, forgetAcceptedRun, runErrorMessage as errorMessage } from './runDispatch'
import { Button, Icon } from './Controls'
import { JsonTree } from './JsonTree'
import { summarizeTrigger, TriggerSummary } from './TriggerSummary'
import { relativeTime } from '../automations/model'

const PAGE_SIZE = 20
const STATUSES: RunStatus[] = ['Failed', 'Cancelled', 'Interrupted', 'Stopped', 'Skipped', 'Succeeded', 'Running', 'Pending']
const CAUSES: Record<string, string> = {
  HomeAssistantTrigger: 'Home Assistant trigger', Entity: 'Entity change', Schedule: 'Schedule', Manual: 'Manual run', Startup: 'Startup',
}
const active = (run: AutomationRun) => run.status === 'Pending' || run.status === 'Running'
const duration = (ms: number | null) => ms === null ? '—' : ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toLocaleString(undefined, { maximumFractionDigits: 2 })} s`
// Rows recorded before run history v2 carry only the method name and no trigger summary.
const runLabel = (run: AutomationRun) => run.actionLabel ?? run.action
const causeLabel = (run: AutomationRun) => CAUSES[run.cause] ?? run.cause
const reason = (run: AutomationRun) => run.status !== 'Succeeded' && run.error ? run.error.split('\n', 1)[0] : undefined
const Timestamp = (props: { value: string | null }) => <Show when={props.value} fallback="—">{value =>
  <time dateTime={value()} title={value()}>{new Date(value()).toLocaleString()}</time>
}</Show>

/** The run history of one saved automation. Runs are started elsewhere (the visual editor's top bar). */
interface RunsProps {
  automationId: string
}
export const AutomationRuns = (props: RunsProps) => {
  const hass = useHass()
  // Identity, rather than filename, owns pagination and dispatch feedback.
  return <Show when={hass.user?.is_admin} fallback={<p class="hs-notice">Administrator access is required to view run history.</p>}>
    <Show when={props.automationId} keyed>{id => <RunPanel automationId={id} />}</Show>
  </Show>
}

const RunPanel = (props: RunsProps) => {
  const hass = useHass()
  const filterId = createUniqueId()
  const [offset, setOffset] = createSignal(0)
  const [statusFilter, setStatusFilter] = createSignal<RunStatus | ''>('')
  // Shared with every place that starts a run of this automation, so a run started elsewhere is polled here too.
  const accepted = () => acceptedRun(props.automationId)
  // Leaving the page forgets it: a run accepted before is not awaited when the list opens again.
  onCleanup(() => forgetAcceptedRun(props.automationId))
  // Expansion is keyed by run ID so a row stays open while polling replaces its record.
  const [expanded, setExpanded] = createSignal<ReadonlySet<string>>(new Set())
  const [now, setNow] = createSignal(Date.now())
  const clock = setInterval(() => setNow(Date.now()), 30_000)
  onCleanup(() => clearInterval(clock))
  const toggle = (runId: string) => setExpanded(current => {
    const next = new Set(current)
    if (!next.delete(runId)) next.add(runId)
    return next
  })
  const history = useQuery(() => ({
    queryKey: ['run-history', props.automationId, offset(), statusFilter()],
    queryFn: () => hass.callWS<RunHistoryResponse>({
      type: 'hass_sharp/run_history', automationId: props.automationId, offset: offset(), limit: PAGE_SIZE,
      ...(statusFilter() ? { status: [statusFilter()] } : {}),
    }),
    staleTime: 0,
    refetchInterval: query => {
      if (offset() || query.state.status === 'error') return false
      const runs = query.state.data?.runs ?? []
      const request = accepted()
      const awaitingResult = request && Date.now() - request.at < 60_000 && !runs.some(run => run.runId === request.runId && !active(run))
      return runs.some(active) || awaitingResult ? 2000 : false
    },
  }))
  // Each newly accepted run, wherever it was started (the editor's top bar), returns the list to the newest,
  // unfiltered records so it is never hidden, and polls for its result.
  createEffect(on(() => accepted()?.runId, runId => {
    if (!runId) return
    if (offset() || statusFilter()) batch(() => { setOffset(0); setStatusFilter('') })
    else void history.refetch()
  }, { defer: true }))
  const filter = (value: RunStatus | '') => batch(() => { setStatusFilter(value); setOffset(0) })
  return <div class="hs-runs">
    <section aria-label="Run history" class="hs-run-history">
      <header class="hs-history-heading"><div><h2>Run history</h2><p class="hs-muted">Newest first · times in your local time zone. Pages may shift when new runs arrive.</p></div>
        <div class="hs-history-tools">
          <label for={filterId}>Status</label>
          <select id={filterId} class="hs-input" value={statusFilter()} onChange={event => filter(event.currentTarget.value as RunStatus | '')}>
            <option value="">All statuses</option>
            <For each={STATUSES}>{status => <option value={status}>{status}</option>}</For>
          </select>
          <Button busy={history.isFetching} onClick={() => void history.refetch()}>Refresh</Button>
        </div></header>
      <Show when={history.isPending}><div class="hs-empty" role="status">Loading run history…</div></Show>
      <Show when={history.isError}><div class="hs-notice hs-notice--danger" role="alert"><h2>Run history couldn’t load</h2>
        <p>{errorMessage(history.error)}</p><Button onClick={() => void history.refetch()}>Try again</Button>
      </div></Show>
      <Show when={history.isSuccess && !history.data.runs.length}><div class="hs-empty" role="status">
        <h2>{offset() ? 'No more runs' : statusFilter() ? `No ${statusFilter().toLowerCase()} runs` : 'No runs yet'}</h2>
        <p>{offset() ? 'Return to the newest runs or the previous page.' : statusFilter() ? 'Choose another status to see other runs.' : 'Run it now or wait for its next trigger.'}</p>
      </div></Show>
      <ul class="hs-run-list" aria-label="Recorded runs" aria-busy={history.isFetching}>
        <For each={history.data?.runs}>{run => <RunRow run={run} now={now()} open={expanded().has(run.runId)} onToggle={() => toggle(run.runId)} />}</For>
      </ul>
      <nav class="hs-history-pagination" aria-label="Run history pages">
        <Button disabled={!offset() || history.isFetching} onClick={() => setOffset(0)}>Newest</Button>
        <Button disabled={!offset() || history.isFetching} onClick={() => setOffset(Math.max(0, offset() - PAGE_SIZE))}>Newer</Button>
        <span class="hs-muted">{history.data?.runs.length ? `${offset() + 1}–${offset() + history.data.runs.length}` : '—'}</span>
        <Button disabled={history.isFetching || !history.isSuccess || history.data.runs.length < PAGE_SIZE} onClick={() => setOffset(offset() + PAGE_SIZE)}>Older</Button>
      </nav>
    </section>
  </div>
}


const RunRow = (props: { run: AutomationRun; now: number; open: boolean; onToggle: () => void }) => {
  // The run list can also stand alone, outside the app's routes: a link to a run needs the router.
  const routed = !!useContext(RouterContext)
  const bodyId = createUniqueId()
  const summary = () => props.run.trigger ? summarizeTrigger(props.run.trigger) : undefined
  return <li class={`hs-run-row hs-run-row--${props.run.status.toLowerCase()}`}>
    <div class="hs-run-line">
      <button type="button" class="hs-run-toggle" aria-expanded={props.open} aria-controls={props.open ? bodyId : undefined} onClick={() => props.onToggle()}>
        <span class={`hs-run-status hs-run-status--${props.run.status.toLowerCase()}`}>{props.run.status}</span>
        <span class="hs-run-main">
          <span class="hs-run-label">{runLabel(props.run)}</span>
          <span class="hs-run-cause">{causeLabel(props.run)}<Show when={summary()}> · <span class="hs-run-trigger">{summary()}</span></Show></span>
          <Show when={reason(props.run)}><span class="hs-run-reason">{reason(props.run)}</span></Show>
        </span>
        <span class="hs-run-when">
          <time dateTime={props.run.receivedUtc} title={new Date(props.run.receivedUtc).toLocaleString()}>{relativeTime(props.run.receivedUtc, props.now)}</time>
          <span class="hs-run-duration">{duration(props.run.durationMs)}</span>
        </span>
        <Icon name="chevron" />
      </button>
      {/* Any run of a visual automation opens on its graph; without a trace it has no recorded values. */}
      <Show when={routed && props.run.visualId}>{visualId =>
        <A class="hs-run-trace" href={`/visual/${encodeURIComponent(visualId())}/runs/${encodeURIComponent(props.run.runId)}`}>{props.run.hasTrace ? 'View trace' : 'View run'}</A>
      }</Show>
    </div>
    <Show when={props.open}><div id={bodyId} class="hs-run-body">
      <Show when={props.run.error}>{error => <section>
        <h3>{props.run.status === 'Cancelled' ? 'Cancel reason' : 'Error'}</h3>
        <pre class={`hs-run-error ${props.run.status === 'Cancelled' ? 'hs-run-error--quiet' : ''}`}>{error()}</pre>
      </section>}</Show>
      <section><h3>Trigger</h3><TriggerSummary trigger={props.run.trigger} /></section>
      <section><h3>Run</h3><dl class="hs-run-fields">
        <div><dt>Run ID</dt><dd class="hs-run-id"><code>{props.run.runId}</code><CopyButton value={props.run.runId} /></dd></div>
        <Show when={props.run.visualRevision != null}><div><dt>Graph revision</dt><dd>{props.run.visualRevision}</dd></div></Show>
        <div><dt>Automation revision</dt><dd><code>{props.run.revision}</code></dd></div>
        <div><dt>Planned</dt><dd><Timestamp value={props.run.plannedUtc} /></dd></div>
        <div><dt>Received</dt><dd><Timestamp value={props.run.receivedUtc} /></dd></div>
        <div><dt>Started</dt><dd><Timestamp value={props.run.startUtc} /></dd></div>
        <div><dt>Ended</dt><dd><Timestamp value={props.run.endUtc} /></dd></div>
        <div><dt>Duration</dt><dd>{duration(props.run.durationMs)}</dd></div>
      </dl></section>
      <details class="hs-run-raw"><summary>Raw values</summary><JsonTree value={props.run} label="Raw run record" /></details>
    </div></Show>
  </li>
}

const CopyButton = (props: { value: string }) => {
  const [state, setState] = createSignal<'idle' | 'copied' | 'failed'>('idle')
  let reset: ReturnType<typeof setTimeout> | undefined
  onCleanup(() => clearTimeout(reset))
  const copy = async () => {
    try { await navigator.clipboard.writeText(props.value); setState('copied') } catch { setState('failed') }
    clearTimeout(reset)
    reset = setTimeout(() => setState('idle'), 2000)
  }
  // The Clipboard API exists only in secure contexts; the ID stays selectable without it.
  return <Show when={typeof navigator.clipboard?.writeText === 'function'}>
    <Button variant="ghost" class="hs-run-copy" aria-label={state() === 'copied' ? 'Run ID copied' : 'Copy run ID'} onClick={() => void copy()}>
      {state() === 'copied' ? 'Copied' : state() === 'failed' ? 'Copy failed' : 'Copy'}
    </Button>
  </Show>
}
