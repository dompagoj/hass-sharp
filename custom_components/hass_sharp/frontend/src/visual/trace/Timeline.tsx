import { createEffect, Index, on, Show } from 'solid-js'
import { Glyph } from '../Glyph'
import { isPure, type Graph, type VisualCatalogSnapshot } from '../contract'
import { durationLabel, nodeTitle, traceStatusLabel, type TraceStatus } from '../flow/flowModel'
import { icons, nodeIdentity } from '../icons'
import type { TraceEvent } from '../../types'
import type { TraceModel } from './traceModel'
import { stepSummary } from '../nodeSummary'
import { useLoadedRegistry } from '../../registry'

/** The icon beside each step status (the word is always there too). */
export const statusIcon: Record<TraceStatus, readonly string[]> = {
  ok: icons.traceDone, error: icons.traceRecovered, failed: icons.traceFailed, running: icons.traceRunning, cancelled: icons.traceCancelled, stopped: icons.traceStopped,
}
/** "+1.2 s" from the run's start. */
export const offsetLabel = (ms: number) => `+${durationLabel(ms) || '0 ms'}`

/**
 * Every recorded run of a step, in the order they started: status, step, iteration or branch, when it started and
 * how long it took. A step is named by its title and what its card says about it (the service a Call service step
 * calls), so runs of steps of one kind read apart. A value node worked out for a step is a lesser row labelled
 * "Value", just before that step's row. Choosing one selects its step (and that run); the selected step's runs are
 * marked. One row is in the Tab order; the arrow keys, Home and End move between rows.
 */
export function Timeline(props: {
  model: TraceModel
  graph: Graph
  catalog: VisualCatalogSnapshot
  selectedNodeId?: string
  selectedSeq?: number
  live: boolean
  /** The last read of the live run failed. */
  paused?: boolean
  onSelect(event: TraceEvent): void
}) {
  let list: HTMLOListElement | undefined
  const rows = () => [...(list?.querySelectorAll<HTMLButtonElement>('.hs-trace-step') ?? [])]
  const node = (id: string) => props.graph.nodes.find(n => n.id === id)
  // Registry names, once loaded, for the steps' summaries (as on their cards).
  const names = useLoadedRegistry()
  const current = () => {
    const events = props.model.events
    const index = events.findIndex(e => e.seq === props.selectedSeq)
    return index >= 0 ? index : 0
  }
  // A step chosen elsewhere (the canvas, the panel) scrolls its run into view here, without taking focus.
  createEffect(
    on(
      () => props.selectedSeq,
      seq => {
        if (seq === undefined) return
        queueMicrotask(() => rows().find(row => row.dataset.seq === String(seq))?.scrollIntoView?.({ block: 'nearest' }))
      },
      { defer: true },
    ),
  )
  const keys = (event: KeyboardEvent) => {
    const all = rows()
    const at = all.indexOf(event.target as HTMLButtonElement)
    if (at < 0) return
    const next = { ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: all.length - 1 }[event.key]
    if (next === undefined) return
    event.preventDefault()
    all[Math.max(0, Math.min(next, all.length - 1))]?.focus()
  }
  const truncated = () => props.model.trace.truncated
  const dropped = () => props.model.trace.dropped
  return (
    <div class="hs-trace-timeline">
      <Show when={props.model.events.length} fallback={<p class="hs-drawer__empty">{props.live ? 'Waiting for the first step…' : 'No steps were recorded for this run.'}</p>}>
        {/* Rows are kept by position: a live run only adds rows and updates a running one in place. */}
        <ol ref={list} class="hs-trace-timeline__list" aria-label="Steps in the order they ran" on:keydown={keys}>
          <Index each={props.model.events}>
            {(event, index) => {
              const step = () => node(event().node)
              const frame = () => props.model.frameLabel(event().frame)
              const status = () => event().status
              const selected = () => event().seq === props.selectedSeq
              const value = () => !!step() && isPure(step()!, props.catalog)
              const summary = () => { const n = step(); return n ? stepSummary(n, props.catalog, names(), props.graph) : undefined }
              const detail = () => [value() && 'Value', summary(), frame(), event().trunc && 'Some values were too large to record'].filter(Boolean).join(' · ')
              return (
                <li>
                  <button
                    type="button"
                    class="hs-trace-step"
                    classList={{ 'hs-trace-step--selected': selected(), 'hs-trace-step--node': event().node === props.selectedNodeId, 'hs-trace-step--value': value(), [`hs-trace-step--${status()}`]: true }}
                    style={{ '--hs-cat': step() ? nodeIdentity(step()!.kind, props.catalog).color : undefined }}
                    data-seq={event().seq}
                    aria-current={selected() ? 'true' : undefined}
                    tabindex={index === current() ? 0 : -1}
                    onClick={() => props.onSelect(event())}
                  >
                    <span class={`hs-trace-step__status hs-trace-status--${status()}`}>
                      <Glyph paths={statusIcon[status()]} />
                      <span class="hs-trace-step__word">{traceStatusLabel[status()]}</span>
                    </span>
                    <span class="hs-trace-step__main">
                      <span class="hs-trace-step__title">{nodeTitle(step(), props.catalog, 'A removed step')}</span>
                      <Show when={detail()}>
                        <span class="hs-trace-step__detail" title={detail()}>{detail()}</span>
                      </Show>
                    </span>
                    <span class="hs-trace-step__time">
                      <span>{offsetLabel(event().t)}</span>
                      <span class="hs-trace-step__ms">{event().ms === undefined ? 'Running' : durationLabel(event().ms!)}</span>
                    </span>
                  </button>
                </li>
              )
            }}
          </Index>
        </ol>
      </Show>
      <Show when={truncated()}>
        <p class="hs-trace-timeline__end" role="note">
          <Glyph paths={icons.warning} />
          <span>
            The record stops here: {dropped() ? `${dropped().toLocaleString()} more ${dropped() === 1 ? 'step was' : 'steps were'} not recorded` : 'the rest of the run was not recorded'}.
          </span>
        </p>
      </Show>
      <Show when={props.live}>
        <p class="hs-trace-timeline__live" role="status">{props.paused ? 'Still running · updates paused' : 'Still running · updates every 2 seconds'}</p>
      </Show>
    </div>
  )
}
