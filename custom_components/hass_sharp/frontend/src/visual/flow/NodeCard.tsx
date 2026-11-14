import { createEffect, createMemo, createSignal, createUniqueId, For, on, onCleanup, onMount, Show, type JSX } from 'solid-js'
import { delegateEvents } from 'solid-js/web'
import { useUpdateNodeInternals, type NodeProps } from '@dschz/solid-flow'
import { inputValue, type Operand } from '../contract'
import { InputEditor } from '../InputEditor'
import { icons } from '../icons'
import { displayValue, valueSummary, widgetFor } from '../widgets'
import { Pin } from './Pin'
import { durationLabel, traceStatusLabel, traceText, useFlow, type InputView, type PinView, type RowView, type TraceStatus } from './flowModel'

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const Icon = (props: { paths: readonly string[]; class?: string }) => (
  <svg class={props.class ?? 'hs-card__glyph'} viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
    <For each={props.paths}>{d => <path d={d} />}</For>
  </svg>
)
/** How long a released press with no click keeps a row open (iOS sends the click up to ~350 ms after release). */
const NO_CLICK_MS = 700
const clip = (text: string, max = 48) => (text.length > max ? `${text.slice(0, max - 1)}…` : text)
/** The icon beside each trace status word. */
const statusIcon: Record<TraceStatus, readonly string[]> = {
  ok: icons.traceDone, error: icons.traceRecovered, failed: icons.traceFailed, running: icons.traceRunning, cancelled: icons.traceCancelled, stopped: icons.traceStopped,
}

/**
 * Keeps keys typed into a card's editor inside it: no node move, canvas shortcut, delete or Home Assistant
 * shortcut sees them. Solid delegates keyboard handlers to an ancestor (the editor root, or the document), so this
 * boundary runs the editor's own handlers itself first.
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

type Box = { left: number; right: number }
/**
 * Where a card's problem tag goes so it stays readable inside the canvas: beside the card on the right, else on the
 * left, else (a narrow canvas, or a card wider than the room left) below the card. `gap` and `width` are in screen
 * pixels, so a zoomed canvas is measured as drawn.
 */
export function tipSide(card: Box, width: number, gap: number, bounds: Box): 'right' | 'left' | 'below' {
  if (card.right + gap + width <= bounds.right) return 'right'
  if (card.left - gap - width >= bounds.left) return 'left'
  return 'below'
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
  const statusText = () => (trace() ? traceText(reached(), view()?.pure) : '')

  const InputCell = (cell: { input: InputView }) => {
    let root!: HTMLDivElement
    const input = () => cell.input
    // An empty value stored as null is shown as not set, never as the text "null".
    const literal = () => {
      const value = operand(input().port)
      return value?.kind === 'literal' && value.value !== null ? value.value : undefined
    }
    const configured = () => {
      return literal() === undefined ? '' : valueSummary(widgetFor(input().spec, flow.catalog()), literal(), flow.names())
    }
    const traced = () => trace()?.value?.(id, input().port, 'in')
    const canEdit = () => !flow.readOnly() && flow.editable()
    // An unset optional input shows its label only; "Set <label>" opens its editor in the row until left unset.
    const [expanded, setExpanded] = createSignal(false)
    const muted = () => input().mode === 'muted'
    const showEditor = () => canEdit() && (input().mode === 'value' || (muted() && expanded()))
    const expand = () => {
      // Queued before the button is replaced, so the editor has focus by the time `leave` looks.
      queueMicrotask(() => root.querySelector<HTMLElement>('.hs-card-editor :is(input, select, textarea, button)')?.focus())
      setExpanded(true)
    }
    // Focus inside the editor keeps it open even when the input becomes unset under it ("Clear" in its menu).
    const enter = (event: FocusEvent) => {
      clearTimeout(pendingLeave)
      if ((event.target as Element).closest('.hs-card-editor')) setExpanded(true)
    }
    // Focus left the row: an input still unset folds back to its label (one with a value keeps its editor anyway),
    // unless its editor holds a draft it refused, which stays open with its error.
    let pendingLeave: ReturnType<typeof setTimeout> | undefined
    onCleanup(() => clearTimeout(pendingLeave))
    // Presses that went down inside the row and have not ended. Safari, the iOS web view and Firefox on macOS do not
    // focus a clicked button: focus leaves for the card (or nowhere) before the click opens the menu, so the row must
    // not fold under a press. On iOS a tap's click (and the mouse events that move focus) come in a later task, up to
    // about 350 ms after `pointerup`, and a second tap can start before the first one's click: clicks end released
    // presses in order. A press ends after its click has been delivered (plus a macrotask), at once when cancelled, or
    // `NO_CLICK_MS` after a release that makes no click (outside the row). When the last one ends, the row folds if
    // focus is no longer in it (a press on its label or padding moves focus to the card).
    type Press = { pointer: number; released: boolean; clicked: boolean; timer?: ReturnType<typeof setTimeout> }
    let presses: Press[] = []
    const doc = () => root.ownerDocument
    const listen = (on: boolean) => {
      const method = on ? doc().addEventListener : doc().removeEventListener
      method.call(doc(), 'pointerup', released as EventListener, true)
      method.call(doc(), 'pointercancel', cancelled as EventListener, true)
      method.call(doc(), 'click', clicked, true)
    }
    const finish = (done: Press) => {
      clearTimeout(done.timer)
      presses = presses.filter(press => press !== done)
      if (presses.length) return
      listen(false)
      fold((root.getRootNode() as Document | ShadowRoot).activeElement)
    }
    /** The press this pointer event belongs to: the oldest one of that pointer still down. */
    const held = (event: PointerEvent) => presses.find(press => !press.released && (event.pointerId === undefined || press.pointer === event.pointerId))
    function released(event: PointerEvent) {
      const press = held(event)
      if (!press) return
      press.released = true
      press.timer = setTimeout(() => finish(press), NO_CLICK_MS)
    }
    function cancelled(event: PointerEvent) {
      const press = held(event)
      if (press) finish(press)
    }
    function clicked() {
      const press = presses.find(p => p.released && !p.clicked)
      if (!press) return
      press.clicked = true
      clearTimeout(press.timer)
      // After this click has been delivered (its handlers run first).
      press.timer = setTimeout(() => finish(press))
    }
    const press = (event: PointerEvent) => {
      // addEventListener ignores a listener that is already there.
      listen(true)
      presses.push({ pointer: event.pointerId ?? 0, released: false, clicked: false })
    }
    onMount(() => root.addEventListener('pointerdown', press, true))
    onCleanup(() => {
      for (const p of presses) clearTimeout(p.timer)
      presses = []
      root.removeEventListener('pointerdown', press, true)
      listen(false)
    })
    const fold = (active: Element | null | undefined) => {
      if (!expanded() || presses.length || (active && root.contains(active))) return
      if (root.querySelector('.hs-card-editor [aria-invalid="true"]')) return
      setExpanded(false)
    }
    const leave = (event: FocusEvent) => {
      if ((event.target as Element).classList.contains('hs-pin-set')) return
      clearTimeout(pendingLeave)
      const next = event.relatedTarget as Element | null
      if (next) return fold(next)
      // Some browsers report no next element (a click, or focus still on its way): look once focus has settled.
      pendingLeave = setTimeout(() => fold((root.getRootNode() as Document | ShadowRoot).activeElement))
    }
    /** Read-only value chips. Traced values when the run recorded one; else what is configured; never a placeholder for a connected input. */
    const shown = (): { text: string; title?: string; muted?: boolean } | undefined => {
      if (showEditor()) return undefined
      const value = traced()
      if (value) return { text: `${clip(displayValue(value.v as never) || '—')}${value.trunc ? ' …' : ''}`, title: input().source, muted: value.muted }
      if (input().mode === 'connected' || input().mode === 'wire') return undefined
      if (configured()) return { text: clip(configured()) }
      // A required input without a value says so (outside a run); an optional one shows its label only.
      return input().mode === 'value' && !trace() ? { text: 'Not set', muted: true } : undefined
    }
    return (
      <div
        ref={root}
        class="hs-pin-cell hs-pin-cell--in"
        classList={{ [`hs-pin-cell--${input().mode}`]: true, 'hs-pin-cell--expanded': muted() && expanded() }}
        data-input-id={input().port}
        on:focusin={enter}
        on:focusout={leave}
      >
        <div class="hs-pin-line">
          <Pin nodeId={id} pin={input().pin} errors={pinErrors(input().pin, input().port)} describedBy={errorsId} />
          <Show
            when={canEdit() && muted() && !expanded()}
            fallback={
              <span class="hs-pin-label">
                {input().pin.label}
                <Show when={input().spec.required && input().mode !== 'connected'}>
                  <span class="hs-required" aria-hidden="true" title="Required">*</span>
                </Show>
              </span>
            }
          >
            <button
              type="button"
              class="hs-pin-label hs-pin-set nodrag nopan"
              aria-label={`Set ${input().pin.label}`}
              title={`Set ${input().pin.label}`}
              on:click={(event: MouseEvent) => {
                event.stopPropagation()
                expand()
              }}
              on:keydown={(event: KeyboardEvent) => {
                // The canvas takes Enter and Space for node selection; this button keeps them.
                if (event.key !== 'Enter' && event.key !== ' ') return
                event.preventDefault()
                event.stopPropagation()
                expand()
              }}
            >
              {input().pin.label}
            </button>
          </Show>
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
              catalog={flow.types()}
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
              <ValueChip text={`${clip(displayValue(value().v as never) || '—', 32)}${value().trunc ? ' …' : ''}`} muted={value().muted} />
            </div>
          )}
        </Show>
      </div>
    )
  }
  // The problem tag shows on hover and focus; it is placed then, against the canvas edges (the side panel's included).
  let tip: HTMLDivElement | undefined
  const [side, setSide] = createSignal<'right' | 'left' | 'below'>('right')
  const placeTip = (event: Event) => {
    const card = event.currentTarget as HTMLElement
    requestAnimationFrame(() => {
      const canvas = card.closest('.solid-flow') ?? card.closest('.hs-flow')
      if (!tip || !canvas || !tip.isConnected) return
      // Measured as drawn on the right (the widest it gets), at the canvas zoom; nothing is painted in between.
      setSide('right')
      const box = card.getBoundingClientRect(), bounds = canvas.getBoundingClientRect()
      const zoom = card.offsetWidth ? box.width / card.offsetWidth : 1
      const width = Math.min(tip.scrollWidth || 0, 320) * zoom
      if (!width) return
      setSide(tipSide(box, width, 12 * zoom, bounds))
    })
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
            'hs-card--pure': current().pure,
            'hs-card--unreachable': current().unreachable && !trace(),
            'hs-card--readonly': flow.readOnly(),
            'hs-card--traced': !!trace(),
            'hs-card--unreached': !!trace() && !reached(),
            [`hs-card--status-${reached()?.status}`]: !!reached(),
            'hs-card--focus': !!trace() && trace()!.focusNodeId === id,
          }}
          style={{ '--hs-cat': current().identity.color }}
          data-category={current().identity.key}
          aria-describedby={current().problems.length || trace() ? errorsId : undefined}
          on:pointerenter={placeTip}
          on:focusin={placeTip}
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
            <Show when={trace()}>
              {/* Status icon and word, never colour alone; the card's accessible name carries the same text. */}
              <span class="hs-card__trace" classList={{ [`hs-card__trace--${reached()?.status ?? 'unreached'}`]: true }} title={statusText()}>
                <Icon paths={reached() ? statusIcon[reached()!.status] : icons.traceUnreached} class="hs-card__trace-icon" />
                <span class="hs-card__trace-word">{reached() ? traceStatusLabel[reached()!.status] : current().pure ? 'Not used' : 'Not reached'}</span>
                <Show when={(reached()?.runs ?? 0) > 1}>
                  <span class="hs-card__runs">×{reached()!.runs}</span>
                </Show>
                <Show when={reached()?.ms !== undefined}>
                  <span class="hs-card__ms">{durationLabel(reached()!.ms!)}</span>
                </Show>
              </span>
            </Show>
            <Show when={flow.hasCode(id)}>
              <button
                type="button"
                class="hs-card__code nodrag nopan"
                aria-label={`${flow.readOnly() ? 'View' : 'Edit'} code of ${current().title}`}
                title={flow.readOnly() ? 'View code' : 'Edit code'}
                on:click={(event: MouseEvent) => {
                  event.stopPropagation()
                  flow.editCode(id)
                }}
                on:dblclick={(event: MouseEvent) => event.stopPropagation()}
                on:keydown={(event: KeyboardEvent) => {
                  // The canvas takes Enter and Space for node selection; this button keeps them.
                  if (event.key !== 'Enter' && event.key !== ' ') return
                  event.preventDefault()
                  event.stopPropagation()
                  flow.editCode(id)
                }}
              >
                <Icon paths={icons.code} class="hs-card__code-icon" />
              </button>
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
                <Icon paths={current().errors ? icons.problem : icons.warning} class="hs-card__badge-icon" />
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
            <div role="tooltip" id={errorsId} ref={tip} class={`hs-node-errors hs-node-errors--${side()}`}>
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
