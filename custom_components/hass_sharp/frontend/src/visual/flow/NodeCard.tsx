import { createEffect, createMemo, createUniqueId, For, on, onCleanup, Show, type JSX } from 'solid-js'
import { delegateEvents } from 'solid-js/web'
import { useUpdateNodeInternals, type NodeProps } from '@dschz/solid-flow'
import { inputValue, type Operand } from '../contract'
import { InputEditor } from '../InputEditor'
import { icons } from '../icons'
import { displayValue, valueSummary, widgetFor } from '../widgets'
import { Pin } from './Pin'
import { durationLabel, traceStatusLabel, useFlow, type InputView, type PinView, type RowView } from './flowModel'

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const Icon = (props: { paths: readonly string[]; class?: string }) => (
  <svg class={props.class ?? 'hs-card__glyph'} viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
    <For each={props.paths}>{d => <path d={d} />}</For>
  </svg>
)
const clip = (text: string, max = 48) => (text.length > max ? `${text.slice(0, max - 1)}…` : text)

/**
 * Keeps keys typed into a card's editor inside it: no node move, canvas shortcut, delete or Home Assistant
 * shortcut sees them. Solid delegates keyboard handlers to the document, so this boundary runs them itself first.
 */
function EditorBoundary(props: { children: JSX.Element }) {
  const stop = (event: Event) => event.stopPropagation()
  return (
    <div
      class="hs-card-editor nodrag nopan nowheel"
      ref={element => delegateEvents(['keydown', 'keyup'], element as unknown as Document)}
      on:keydown={stop}
      on:keyup={stop}
      on:keypress={stop}
    >
      {props.children}
    </div>
  )
}

/** A traced or configured value, read-only. */
const ValueChip = (props: { text: string; title?: string; muted?: boolean }) => (
  <span class="hs-value-chip" classList={{ 'hs-value-chip--muted': props.muted }} title={props.title ?? props.text}>
    {props.text}
  </span>
)

/** The node card: a category-tinted header with the execution pins, then rows of input and output pins. */
export function NodeCard(props: NodeProps) {
  const flow = useFlow()
  const refresh = useUpdateNodeInternals()
  const id = props.id
  const errorsId = createUniqueId()
  const view = createMemo(() => flow.view(id))
  let alive = true
  onCleanup(() => (alive = false))
  // Pins move when rows change; measure the handles again after the DOM has updated.
  createEffect(
    on(view, () =>
      queueMicrotask(() => {
        if (alive) refresh(id)
      }),
    ),
  )
  const trace = () => flow.trace()
  const reached = () => trace()?.nodes[id]
  const pinErrors = (pin: PinView, port: string) =>
    (view()?.problems ?? []).filter(p => p.severity === 3 && (p.inputId === port || p.portId === port || p.inputId === pin.id)).map(p => p.message)
  const operand = (port: string): Operand | undefined => {
    const node = flow.graphNode(id)
    const value = node ? inputValue(node, port, flow.catalog()) : undefined
    return record(value) && typeof value.kind === 'string' ? (value as Operand) : undefined
  }
  const statusText = () => {
    const status = reached()
    if (!trace()) return ''
    if (!status) return 'Not reached'
    return [traceStatusLabel[status.status], status.runs > 1 && `${status.runs} times`, status.ms !== undefined && durationLabel(status.ms)].filter(Boolean).join(', ')
  }

  const InputCell = (cell: { input: InputView }) => {
    const input = () => cell.input
    const literal = () => {
      const value = operand(input().port)
      return value?.kind === 'literal' ? value.value : undefined
    }
    const configured = () => {
      const value = operand(input().port)
      if (value?.kind === 'variable') return `Variable ${value.name}`
      return literal() === undefined ? '' : valueSummary(widgetFor(input().spec, flow.catalog()), literal())
    }
    const traced = () => trace()?.value?.(id, input().port, 'in')
    const chip = () => {
      if (trace()) {
        const value = traced()
        if (value) return { text: `${clip(displayValue(value.v as never) || '—')}${value.trunc ? ' …' : ''}`, title: input().source }
        return reached() ? { text: 'No value', muted: true } : undefined
      }
      if (input().mode === 'connected') return undefined
      return configured() ? { text: clip(configured()) } : undefined
    }
    const showEditor = () => !flow.readOnly() && flow.editable() && (input().mode === 'value' || input().mode === 'muted')
    const shown = () => {
      if (showEditor()) return undefined
      const value = chip()
      if (value || input().mode !== 'value') return value
      return { text: configured() || 'Not set', muted: !configured() }
    }
    return (
      <div class="hs-pin-cell hs-pin-cell--in" classList={{ [`hs-pin-cell--${input().mode}`]: true }} data-input-id={input().port}>
        <div class="hs-pin-line">
          <Pin nodeId={id} pin={input().pin} errors={pinErrors(input().pin, input().port)} describedBy={errorsId} />
          <span class="hs-pin-label">
            {input().pin.label}
            <Show when={input().spec.required && input().mode !== 'connected'}>
              <span class="hs-required" aria-hidden="true" title="Required">*</span>
            </Show>
          </span>
          <Show when={input().mode === 'connected'}>
            <span class="hs-source-chip" style={{ '--hs-pin-color': input().pin.color }} title={`Connected to ${input().source}`}>
              {input().source}
            </span>
          </Show>
        </div>
        <Show when={showEditor()}>
          <EditorBoundary>
            <InputEditor
              variant="inline"
              input={input().spec}
              value={operand(input().port)}
              label={input().pin.label}
              catalog={flow.catalog()}
              variables={flow.variables()}
              context={{ targetEntityIds: view()?.targetEntityIds ?? [] }}
              errors={input().errors}
              onChange={(next, options) => flow.setInput(id, input().port, next, options)}
            />
          </EditorBoundary>
        </Show>
        <Show when={shown()}>
          {value => <div class="hs-pin-value"><ValueChip text={value().text} title={value().title} muted={value().muted} /></div>}
        </Show>
      </div>
    )
  }
  const OutputCell = (cell: { pin: PinView }) => {
    const port = () => (cell.pin.kind === 'data' ? cell.pin.id.slice(5) : cell.pin.id)
    const traced = () => (cell.pin.kind === 'data' ? trace()?.value?.(id, port(), 'out') : undefined)
    return (
      <div class="hs-pin-cell hs-pin-cell--out" classList={{ 'hs-pin-cell--exec': cell.pin.kind === 'exec' }} data-output-id={port()}>
        <div class="hs-pin-line">
          <span class="hs-pin-label">{cell.pin.label}</span>
          <Pin nodeId={id} pin={cell.pin} errors={pinErrors(cell.pin, port())} describedBy={errorsId} />
        </div>
        <Show when={traced()}>
          {value => (
            <div class="hs-pin-value">
              <ValueChip text={`${clip(displayValue(value().v as never) || '—', 32)}${value().trunc ? ' …' : ''}`} />
            </div>
          )}
        </Show>
      </div>
    )
  }
  const rowIds = createMemo(() => view()?.rows.map(row => row.id) ?? [], undefined, {
    equals: (a, b) => a.length === b.length && a.every((value, i) => value === b[i]),
  })
  const row = (rowId: string): RowView | undefined => view()?.rows.find(r => r.id === rowId)

  return (
    <Show when={view()}>
      {current => (
        <article
          class="hs-flow-card hs-card"
          classList={{
            'hs-flow-card--error': current().errors > 0,
            'hs-card--warning': current().errors === 0 && current().warnings > 0,
            'hs-card--trigger': current().kind === 'trigger',
            'hs-card--unknown': !current().known,
            'hs-card--unreachable': current().unreachable && !trace(),
            'hs-card--readonly': flow.readOnly(),
            'hs-card--traced': !!trace(),
            'hs-card--unreached': !!trace() && !reached(),
            [`hs-card--status-${reached()?.status}`]: !!reached(),
            'hs-card--focus': !!trace() && trace()!.focusNodeId === id,
          }}
          style={{ '--hs-cat': current().identity.color }}
          data-category={current().identity.key}
          aria-describedby={current().problems.length ? errorsId : undefined}
        >
          <header class="hs-card__head">
            <Show when={current().execIn}>{pin => <Pin nodeId={id} pin={pin()} errors={pinErrors(pin(), 'in')} describedBy={errorsId} />}</Show>
            <span class="hs-card__icon" title={current().identity.label}>
              <Icon paths={current().identity.icon} />
            </span>
            <div class="hs-card__titles">
              <strong class="hs-card__title">{current().title}</strong>
              <span class="hs-card__summary" title={current().summary}>
                {current().summary}
              </span>
            </div>
            <Show when={trace() && reached()}>
              {status => (
                <span class="hs-card__trace" title={statusText()}>
                  <Show when={status().runs > 1}>
                    <span class="hs-card__runs">×{status().runs}</span>
                  </Show>
                  <Show when={status().ms !== undefined}>
                    <span>{durationLabel(status().ms!)}</span>
                  </Show>
                </span>
              )}
            </Show>
            <Show when={current().problems.length}>
              <button
                type="button"
                class="hs-card__badge nodrag nopan"
                classList={{ 'hs-card__badge--warning': current().errors === 0 }}
                aria-label={`${current().problems.length} ${current().errors ? 'problem' : 'warning'}${current().problems.length === 1 ? '' : 's'}: show in the panel`}
                title={current().problems.map(p => p.message).join('\n')}
                on:click={(event: MouseEvent) => {
                  event.stopPropagation()
                  flow.locateProblem(id)
                }}
              >
                <Icon paths={icons.problem} class="hs-card__badge-icon" />
                {current().problems.length}
              </button>
            </Show>
            <Show when={current().next}>{pin => <Pin nodeId={id} pin={pin()} errors={pinErrors(pin(), pin().id)} describedBy={errorsId} />}</Show>
          </header>
          <Show when={rowIds().length || current().footer}>
            <div class="hs-card__body">
              <For each={rowIds()}>
                {rowId => (
                  <div class="hs-card__row">
                    <Show when={row(rowId)?.input} fallback={<span />}>
                      {input => <InputCell input={input()} />}
                    </Show>
                    <Show when={row(rowId)?.output}>{pin => <OutputCell pin={pin()} />}</Show>
                  </div>
                )}
              </For>
              <Show when={current().footer}>
                {pin => (
                  <div class="hs-card__row hs-card__row--footer">
                    <span />
                    <OutputCell pin={pin()} />
                  </div>
                )}
              </Show>
            </div>
          </Show>
          <Show when={current().problems.length || trace()}>
            <div role="tooltip" id={errorsId} class="hs-node-errors">
              <Show when={trace()}>
                <p>{statusText()}</p>
              </Show>
              <For each={current().problems}>{problem => <p>{problem.message}</p>}</For>
            </div>
          </Show>
        </article>
      )}
    </Show>
  )
}
