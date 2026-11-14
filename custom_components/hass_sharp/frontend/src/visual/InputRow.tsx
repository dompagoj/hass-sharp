import { createSignal, For, Show } from 'solid-js'
import { InputEditor } from './InputEditor'
import { Glyph } from './Glyph'
import type { InputView } from './flow/flowModel'
import { inputValue, resolveTypeSpec, setInput, words, type GraphNode, type InputSpec, type Operand, type VisualCatalogSnapshot } from './contract'
import { typeColor, typeIsList, typeLabel } from './typeLabels'
import { csharpTypes } from './csharpType'

type Options = { coalesce?: string }
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
export const closeIcon = ['M6 6l12 12', 'M18 6 6 18']

/** Focuses the control of an inspector row: the one its label names, else its first field, else a button. */
export function focusControl(row: HTMLElement) {
  const labelled = row.querySelector<HTMLLabelElement>('label[for]')
  const target = (labelled?.htmlFor ? row.querySelector<HTMLElement>(`[id="${labelled.htmlFor}"]`) : null)
    ?? row.querySelector<HTMLElement>('input:not([type="hidden"]):not(:disabled),select:not(:disabled),textarea:not(:disabled)')
    ?? row.querySelector<HTMLElement>('button:not([aria-haspopup="menu"]):not(:disabled)')
    ?? row.querySelector<HTMLElement>('button:not(:disabled)')
  if (!target) return false
  target.focus()
  target.scrollIntoView?.({ block: 'nearest' })
  return row.ownerDocument.activeElement === target
}

/** Focuses the control of the row of `port` (an input id, `service`, `fields`, …) under `root`. */
export function focusRow(root: ParentNode | undefined, port: string) {
  const row = root && [...root.querySelectorAll<HTMLElement>('[data-input-id]')].find(el => el.dataset.inputId === port)
  if (!row) return false
  if (!focusControl(row)) { row.tabIndex = -1; row.focus() }
  return true
}

/**
 * Call before removing `row` (one of its siblings matching `rows`); call the result after the removal. Focus then
 * goes to the next row, else the previous one, else `fallback`, so it never falls to the page.
 */
export function keepFocus(row: Element | null | undefined, rows: string, fallback: () => HTMLElement | null | undefined) {
  const peers = row?.parentElement ? [...row.parentElement.children].filter(el => el.matches(rows)) : []
  const index = row ? peers.indexOf(row) : -1
  const candidates = [peers[index + 1], index > 0 ? peers[index - 1] : undefined].filter((el): el is HTMLElement => el instanceof HTMLElement)
  return () => queueMicrotask(() => {
    for (const el of candidates) if (el.isConnected && focusControl(el)) return
    fallback()?.focus()
  })
}

/** The friendly type of an input: its resolved type, else its declared types; never an unresolvable id. */
export function inputTypeChip(view: InputView | undefined, spec: InputSpec | undefined, catalog: VisualCatalogSnapshot) {
  const id = view?.pin.typeId
  if (id && resolveTypeSpec(id, catalog)) return { text: typeLabel(id, catalog), color: typeColor(id, catalog), list: typeIsList(id, catalog) }
  const types = (spec?.types ?? []).filter(t => resolveTypeSpec(t, catalog))
  const names = [...new Set(types.map(t => typeLabel(t, catalog)))]
  if (names.length === 1) return { text: names[0], color: typeColor(types[0], catalog), list: typeIsList(types[0], catalog) }
  return { text: names.length && names.length <= 3 ? names.join(' or ') : 'Any value', color: 'var(--hs-muted)', list: false }
}

/**
 * The friendly name of a type and, in the panel's input and output rows, the C# type a node method declares for it
 * (`csharp`, see {@link csharpType}), read as one text: "True/False, C# type bool".
 */
export const TypeChip = (props: { text: string; color: string; list?: boolean; csharp?: string[] }) => {
  const csharp = () => props.csharp?.join(' or ') ?? ''
  return (
    <span class="hs-type-chip" style={{ '--hs-chip-color': props.color }} title={csharp() ? `Type: ${props.text} · C#: ${csharp()}` : `Type: ${props.text}`}>
      <span class="hs-type-chip__dot" classList={{ 'hs-type-chip__dot--list': !!props.list }} aria-hidden="true" />
      <span class="hs-type-chip__name">{props.text}</span>
      <Show when={csharp()}>
        <CSharpType text={csharp()} plural={(props.csharp?.length ?? 0) > 1} />
      </Show>
    </span>
  )
}
/** A C# type in code style, read after the friendly name as ", C# type bool". */
export const CSharpType = (props: { text: string; plural?: boolean }) => (
  <><span class="hs-visually-hidden">, C# {props.plural ? 'types' : 'type'} </span><code class="hs-csharp-type">{props.text}</code></>
)

/** The C# types of an input: of its resolved type, else of each type it accepts (as {@link inputTypeChip}). */
export function inputCSharpTypes(view: InputView | undefined, spec: InputSpec | undefined, catalog: VisualCatalogSnapshot) {
  const id = view?.pin.typeId
  if (id && resolveTypeSpec(id, catalog)) return csharpTypes([id], catalog)
  return csharpTypes((spec?.types ?? []).filter(t => resolveTypeSpec(t, catalog)), catalog)
}

/** Validator messages, shown as they are under their field. */
export const Errors = (props: { list: string[]; id?: string }) => (
  <Show when={props.list.length}><ul id={props.id} class="hs-input-editor__errors"><For each={props.list}>{m => <li>{m}</li>}</For></ul></Show>
)

/** What the inspector's input row needs beyond the port: overrides for rows of service fields. */
export interface FieldRow {
  label?: string
  description?: string
  /** The spec to edit with, for a field that is not stored yet or a target filtered by the service. */
  spec?: InputSpec
  /** The value of a field that is not stored yet, and where its first change goes. */
  value?: Operand
  onChange?(operand: Operand, options?: Options): void
  onRemove?(): void
  removeLabel?: string
  /** Why the field cannot be removed (it is connected); undefined when it can. */
  removeBlocked?: string
}

/** The node an input row belongs to, read through accessors so the row survives edits of that node. */
export interface RowContext {
  node(): GraphNode
  catalog(): VisualCatalogSnapshot
  input(port: string): InputView | undefined
  targetEntityIds(): string[]
  onChange(config: GraphNode['config'], options?: Options): void
  /** Select another node (a connected input's source). */
  onSelect?(id?: string): void
  focus(port: string): void
  /** Where focus goes when a removed row has no neighbour. */
  fallback(): HTMLElement | null | undefined
}

/** One input: label and type chip, then its editor, a "From …" pill when connected, or "Not set". */
export function InputRow(row: { port: string; ctx: RowContext } & FieldRow) {
  const ctx = row.ctx
  const input = () => ctx.input(row.port)
  const inputSpec = () => row.spec ?? input()?.spec
  const label = () => row.label ?? input()?.pin.label ?? words.portLabel(undefined, row.port)
  const operand = () => row.value ?? (inputValue(ctx.node(), row.port, ctx.catalog()) as Operand | undefined)
  const wired = () => input()?.mode === 'connected'
  const [setting, setSetting] = createSignal(false)
  // A service's target and the fields added on purpose always show their editor.
  const muted = () => !wired() && !setting() && input()?.mode === 'muted' && !(ctx.node().kind === 'service' && (row.port === 'target' || row.port.startsWith('field:')))
  const errors = () => input()?.errors ?? []
  const chip = () => inputTypeChip(input(), inputSpec(), ctx.catalog())
  const editSpec = (): InputSpec | undefined => { const s = inputSpec(); return s && row.description !== undefined ? { ...s, description: row.description } : s }
  // A wire-only type that accepts null (a nullable C# type) can still be left empty without a wire.
  const nullable = () => input()?.mode === 'wire' && !!inputSpec()?.types.length && inputSpec()!.types.every(t => resolveTypeSpec(t, ctx.catalog())?.nullable === true)
  const isNull = () => { const o = operand(); return o?.kind === 'literal' && o.value === null }
  const change = (next: Operand, options?: Options) => {
    if (row.onChange) return row.onChange(next, options)
    const node = structuredClone(ctx.node())
    if (row.port.startsWith('field:') && !record(node.config.fields)) node.config.fields = {}
    setInput(node, row.port, next, ctx.catalog())
    ctx.onChange(node.config, options)
  }
  return (
    <div class="hs-insp-input" data-input-id={row.port} aria-invalid={errors().length > 0 || undefined}>
      <Show when={!wired() && !muted() && editSpec()} fallback={<span class="hs-insp-input__label">{label()}</span>}>
        {current => (
          <InputEditor variant="panel" input={current()} value={operand()} label={label()} catalog={ctx.catalog()}
            context={{ targetEntityIds: ctx.targetEntityIds() }} errors={errors()} onChange={change}
            retype={ctx.node().kind === 'constant' && row.port === 'value'} />
        )}
      </Show>
      <TypeChip {...chip()} csharp={inputCSharpTypes(input(), inputSpec(), ctx.catalog())} />
      <Show when={row.onRemove}>
        {remove => (
          <button type="button" class="hs-button hs-button--ghost hs-insp-remove" aria-label={row.removeLabel ?? `Remove ${label()}`}
            title={row.removeBlocked ?? row.removeLabel} disabled={!!row.removeBlocked}
            onClick={event => {
              const restore = keepFocus(event.currentTarget.closest('.hs-insp-input'), '.hs-insp-input', ctx.fallback)
              remove()()
              restore()
            }}>
            <Glyph paths={closeIcon} />
          </button>
        )}
      </Show>
      <Show when={wired() && input()}>
        {current => (
          <>
            <button type="button" class="hs-insp-pill" style={{ '--hs-chip-color': chip().color }}
              aria-label={`${label()}, ${chip().text}, from ${current().source}. Select it on the canvas`}
              title={`Select ${current().source?.split(' · ')[0]} on the canvas`} onClick={() => ctx.onSelect?.(current().sourceNode)}>
              <span class="hs-type-chip__dot hs-type-chip__dot--filled" aria-hidden="true" />
              <span>From {current().source}</span>
            </button>
            <Errors list={errors()} />
          </>
        )}
      </Show>
      <Show when={!wired() && nullable()}>
        <label class="hs-insp-toggle hs-insp-null">
          <input type="checkbox" role="switch" class="hs-switch" checked={isNull()}
            onChange={e => change(e.currentTarget.checked ? { kind: 'literal', type: inputSpec()!.types[0], value: null } : { kind: 'unset' })} />
          <span><strong>Leave empty</strong><small class="hs-field-hint">Use no value instead of connecting one.</small></span>
        </label>
      </Show>
      <Show when={muted()}>
        <div class="hs-insp-unset">
          <span class="hs-muted">Not set</span>
          <button type="button" class="hs-button hs-button--ghost" aria-label={`Set ${label()}`}
            onClick={() => { setSetting(true); ctx.focus(row.port) }}>Set value</button>
        </div>
        <Show when={inputSpec()?.description}><small class="hs-field-hint">{inputSpec()!.description}</small></Show>
        <Errors list={errors()} />
      </Show>
    </div>
  )
}
