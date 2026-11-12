import { createMemo, createSignal, createUniqueId, For, Show } from 'solid-js'
import { useQuery, useMutation } from '@tanstack/solid-query'
import { useHass } from '../context'
import type { AutomationRun, RunAcceptedResponse, RunHistoryResponse, UserScriptClassDto } from '../types'
import { Button, Status } from './Controls'

const PAGE_SIZE = 20
const active = (run: AutomationRun) => run.status === 'Pending' || run.status === 'Running'
const errorMessage = (error: unknown) => {
  const e = error as { message?: string; body?: { error?: string }; error?: string }
  return e?.body?.error ?? e?.message ?? e?.error ?? 'Check your connection and try again.'
}
const duration = (ms: number | null) => ms === null ? '—' : ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toLocaleString(undefined, { maximumFractionDigits: 2 })} s`
const cause = (value: string) => value === 'Schedule' ? 'Time' : value
const Timestamp = (props: { value: string | null }) => <Show when={props.value} fallback="—">{value =>
  <time dateTime={value()} title={value()}>{new Date(value()).toLocaleString()}</time>
}</Show>

export const AutomationRuns = (props: { automationId: string; classes: UserScriptClassDto[] }) => {
  const hass = useHass()
  // Identity, rather than filename, owns pagination and dispatch feedback.
  return <Show when={hass.user?.is_admin} fallback={<p class="hs-notice">Administrator access is required to run automations and view history.</p>}>
    <Show when={props.automationId} keyed>{id => <RunPanel automationId={id} classes={props.classes} />}</Show>
  </Show>
}

const RunPanel = (props: { automationId: string; classes: UserScriptClassDto[] }) => {
  const hass = useHass()
  const selectId = createUniqueId()
  const [offset, setOffset] = createSignal(0)
  const [selection, setSelection] = createSignal('')
  const [accepted, setAccepted] = createSignal<{ runId: string; at: number }>()
  const actions = createMemo(() => props.classes.flatMap(klass => klass.methods.map(method => ({
    key: `${klass.name}::${method}`, className: klass.name, method,
  }))))
  const selected = () => actions().find(action => action.key === selection()) ?? actions()[0]
  const history = useQuery(() => ({
    queryKey: ['run-history', props.automationId, offset()],
    queryFn: () => hass.callWS<RunHistoryResponse>({ type: 'hass_sharp/run_history', automationId: props.automationId, offset: offset(), limit: PAGE_SIZE }),
    staleTime: 0,
    refetchInterval: query => {
      if (offset() || query.state.status === 'error') return false
      const runs = query.state.data?.runs ?? []
      const request = accepted()
      const awaitingResult = request && Date.now() - request.at < 60_000 && !runs.some(run => run.runId === request.runId && !active(run))
      return runs.some(active) || awaitingResult ? 2000 : false
    },
  }))
  const dispatch = useMutation(() => ({
    mutationFn: (action: { className: string; method: string }) => hass.callWS<RunAcceptedResponse>({
      type: 'hass_sharp/run_now', automationId: props.automationId, className: action.className, method: action.method,
    }),
    onSuccess: response => {
      setAccepted({ runId: response.runId, at: Date.now() })
      // Return to newest records so an accepted attempt is never hidden on an older page.
      if (offset()) setOffset(0)
      else void history.refetch()
    },
  }))
  const runNow = () => {
    const action = selected()
    if (!action || dispatch.isPending) return
    setAccepted(undefined)
    dispatch.mutate(action)
  }
  return <div class="hs-runs">
    <section class="hs-run-controls" aria-label="Run automation">
      <div><h2>Run now</h2><p class="hs-muted">Runs the last saved version. Unsaved editor changes are not included.</p></div>
      <Show when={actions().length} fallback={<p class="hs-muted">No declared actions. Add an AutomationAction in Configure and save to run it.</p>}>
        <div class="hs-run-action"><label for={selectId}>Action</label>
          <select id={selectId} class="hs-input" value={selected()?.key} disabled={dispatch.isPending} onChange={event => setSelection(event.currentTarget.value)}>
            <For each={actions()}>{action => <option value={action.key}>{action.className} · {action.method}</option>}</For>
          </select>
          <Button variant="primary" busy={dispatch.isPending} onClick={runNow}>{dispatch.isPending ? 'Requesting…' : 'Run now'}</Button>
        </div>
        <p class="hs-muted hs-run-help">The action must allow manual runs. Success means the C# action completed; it does not confirm a device effect.</p>
      </Show>
      <Show when={accepted()}>{request => <p class="hs-run-feedback" role="status">Request accepted · <code>{request().runId}</code>. Check its status below for the result.</p>}</Show>
      <Show when={dispatch.isError}><p class="hs-error" role="alert">Run request failed: {errorMessage(dispatch.error)}</p></Show>
    </section>
    <section aria-label="Run history" class="hs-run-history">
      <header class="hs-history-heading"><div><h2>Run history</h2><p class="hs-muted">Newest first · times in your local time zone. Pages may shift when new runs arrive.</p></div>
        <Button busy={history.isFetching} onClick={() => void history.refetch()}>Refresh</Button></header>
      <Show when={history.isPending}><div class="hs-empty" role="status">Loading run history…</div></Show>
      <Show when={history.isError}><div class="hs-notice hs-notice--danger" role="alert"><h2>Run history couldn’t load</h2>
        <p>{errorMessage(history.error)}</p><Button onClick={() => void history.refetch()}>Try again</Button>
      </div></Show>
      <Show when={history.isSuccess && !history.data.runs.length}><div class="hs-empty" role="status">
        <h2>{offset() ? 'No more runs' : 'No runs yet'}</h2><p>{offset() ? 'Return to the newest runs or the previous page.' : 'Run an action or wait for its next trigger.'}</p>
      </div></Show>
      <ul class="hs-run-list" aria-label="Recorded runs" aria-busy={history.isFetching}>
        <For each={history.data?.runs}>{run => <li class="hs-run-card">
          <div class="hs-run-summary"><strong>{run.action}</strong><Status tone={run.status === 'Failed' || run.status === 'Interrupted' ? 'danger' : run.status === 'Succeeded' ? 'success' : 'neutral'}>{run.status}</Status></div>
          <div class="hs-run-meta"><span>{cause(run.cause)}</span><Timestamp value={run.receivedUtc} /><span>{duration(run.durationMs)}</span></div>
          <Show when={run.details}><p class="hs-run-cause">{run.details}</p></Show>
          <details><summary>Run details</summary><dl class="hs-run-details">
            <div><dt>Run ID</dt><dd><code>{run.runId}</code></dd></div>
            <div><dt>Revision</dt><dd><code>{run.revision}</code></dd></div>
            <div><dt>Planned</dt><dd><Timestamp value={run.plannedUtc} /></dd></div>
            <div><dt>Received</dt><dd><Timestamp value={run.receivedUtc} /></dd></div>
            <div><dt>Started</dt><dd><Timestamp value={run.startUtc} /></dd></div>
            <div><dt>Ended</dt><dd><Timestamp value={run.endUtc} /></dd></div>
            <div><dt>Duration</dt><dd>{duration(run.durationMs)}</dd></div>
          </dl></details>
          <Show when={run.error}><pre class="hs-run-error" aria-label="Run error">{run.error}</pre></Show>
        </li>}</For>
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
