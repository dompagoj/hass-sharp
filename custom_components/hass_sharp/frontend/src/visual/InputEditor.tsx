import { createMemo, createSignal, createUniqueId, For, Show } from 'solid-js'
import { Menu, type MenuItem } from '../components/Menu'
import { isAssignable, nodeCatalog, type InputSpec, type JsonValue, type Operand, type VisualCatalogSnapshot } from './contract'
import { typeColor, typeLabel } from './graph'
import { fitsWidget, initialValue, literalFor, WidgetControl, widgetFor, type Widget } from './widgets'

/** A named variable visible to the input (D7), with the type of its producer. */
export interface EditorVariable { name: string; type: string }
export interface InputEditorProps {
  /** The resolved input spec (`dataInputs`), including C2 constraints and the raw selector. */
  input: InputSpec
  /** The stored operand; undefined is the same as unset. */
  value: Operand | undefined
  /**
   * One call per committed change. Drags of a slider or colour picker pass the same `coalesce` key for every
   * change of that drag, so the parent can merge them into one undo step.
   */
  onChange(operand: Operand, options?: { coalesce?: string }): void
  /** `inline`: compact, for a node card (no visible label or hint). `panel`: labelled, with the description. */
  variant: 'inline' | 'panel'
  /** Accessible name of every control, and the visible label in the panel. */
  label: string
  disabled?: boolean
  /** Variables visible to this input, for "Variable…"; only those assignable to the input are offered. */
  variables?: EditorVariable[]
  /** `targetEntityIds`: the entities the node acts on, for the state widget's options. */
  context?: { targetEntityIds?: string[] }
  /** Validation messages for this input; shown and associated with the control. */
  errors?: string[]
  /** Set while the input is wired: the source shown read-only (for example "Read entity · State"). */
  wiredFrom?: string
  catalog?: VisualCatalogSnapshot
}

const jsonWidget: Widget = { kind: 'json', multiple: false }
const same = (a: Operand, b: Operand) => JSON.stringify([a.kind, 'type' in a ? a.type : null, 'value' in a ? a.value : null, 'name' in a ? a.name : null])
  === JSON.stringify([b.kind, 'type' in b ? b.type : null, 'value' in b ? b.value : null, 'name' in b ? b.name : null])

/** Edits one operand of one input with the widget its type and selector call for, plus a Value / Variable… / Clear menu. */
export function InputEditor(props: InputEditorProps) {
  const id = createUniqueId(), labelId = `${id}-label`, hintId = `${id}-hint`, errorsId = `${id}-errors`, variableId = `${id}-variable`
  let root!: HTMLDivElement
  const catalog = () => props.catalog ?? nodeCatalog
  const operand = (): Operand => props.value ?? { kind: 'unset' }
  const literal = () => { const o = operand(); return o.kind === 'literal' ? o.value : undefined }
  const widget = createMemo(() => widgetFor(props.input, catalog()))
  // A stored value of another shape stays editable as JSON instead of being misread by the widget.
  const shown = () => fitsWidget(widget(), literal()) ? widget() : jsonWidget
  const [picking, setPicking] = createSignal(false)
  const view = () => picking() ? 'variable' : props.wiredFrom !== undefined ? 'wired' : operand().kind === 'variable' ? 'variable' : 'value'
  const variableName = () => { const o = operand(); return o.kind === 'variable' ? o.name : '' }
  const variables = () => (props.variables ?? []).filter(v => props.input.types.some(t => isAssignable(v.type, t, catalog())))
  const errors = () => props.errors ?? []
  const hint = () => props.variant === 'panel' ? props.input.description : ''
  const describedBy = () => [errors().length && errorsId, hint() && hintId].filter(Boolean).join(' ') || undefined
  const color = () => { const o = operand(); return typeColor(o.kind === 'literal' ? o.type : props.input.types[0] ?? 'object', catalog()) }

  const emit = (next: Operand, options?: { coalesce?: string }) => {
    if (props.disabled) return
    // While wired, any choice is news to the parent: it decides whether to disconnect.
    if (props.wiredFrom === undefined && same(next, operand())) return
    props.onChange(next, options)
  }
  const commit = (value: JsonValue | undefined, options?: { coalesce?: string }) =>
    emit(value === undefined ? { kind: 'unset' } : literalFor(props.input, value, catalog()), options)
  const focus = (target: string) => queueMicrotask(() => root.querySelector<HTMLElement>(`[id="${target}"]`)?.focus())
  const chooseValue = () => {
    setPicking(false)
    if (operand().kind !== 'literal' || props.wiredFrom !== undefined) {
      const start = initialValue(widget(), props.input)
      emit(start === undefined ? { kind: 'unset' } : literalFor(props.input, start, catalog()))
    }
    focus(id)
  }
  const items = (): MenuItem[] => [
    ...(widget().kind === 'none' ? [] : [{ label: 'Value', onSelect: chooseValue }]),
    { label: 'Variable…', onSelect: () => { setPicking(true); focus(variableId) } },
    { label: 'Clear', onSelect: () => { setPicking(false); emit({ kind: 'unset' }) } },
  ]
  const menu = () => <Menu label={`${props.label} mode`} items={items()} disabled={props.disabled} />

  const content = () => <>
    <Show when={view() === 'value'}>
      <Show when={widget().kind !== 'none'} fallback={<span class="hs-input-editor__state hs-muted">
        {props.variant === 'inline' ? 'Connect an output' : `Connect a ${props.input.types.map(t => typeLabel(t, catalog())).join(' or ')} output.`}
      </span>}>
        <WidgetControl widget={shown()} value={literal()} commit={commit} id={id} label={props.label} describedBy={describedBy()}
          invalid={errors().length > 0} disabled={!!props.disabled} compact={props.variant === 'inline'} context={props.context} />
      </Show>
    </Show>
    <Show when={view() === 'wired'}>
      <span class="hs-input-editor__state">Connected to {props.wiredFrom}</span>
    </Show>
    <Show when={view() === 'variable'}>
      <Show when={picking() || !variableName()} fallback={<span class="hs-input-editor__state">Variable <code>{variableName()}</code></span>}>
        <Show when={variables().length} fallback={<span id={variableId} tabindex="-1" class="hs-input-editor__state hs-muted">No matching variable is visible here.</span>}>
          <select id={variableId} class="hs-input" aria-label={`${props.label} variable`} aria-describedby={describedBy()} disabled={props.disabled}
            onKeyDown={event => { if (event.key === 'Escape' && picking()) { event.preventDefault(); event.stopPropagation(); setPicking(false) } }}
            onChange={event => { const name = event.currentTarget.value; setPicking(false); if (name) emit({ kind: 'variable', name }) }}>
            <option value="" selected={!variables().some(v => v.name === variableName())} disabled>Choose a variable…</option>
            <For each={variables()}>{v => <option value={v.name} selected={v.name === variableName()}>{v.name} ({typeLabel(v.type, catalog())})</option>}</For>
          </select>
        </Show>
        <Show when={picking()}><button type="button" class="hs-button hs-button--ghost" onClick={() => setPicking(false)}>Cancel</button></Show>
      </Show>
    </Show>
  </>

  return <div ref={root} class={`hs-input-editor hs-input-editor--${props.variant}`} role="group" data-mode={view()}
    aria-labelledby={props.variant === 'panel' ? labelId : undefined} aria-label={props.variant === 'inline' ? props.label : undefined}
    aria-describedby={describedBy()} style={{ '--hs-value-color': color() }}>
    <Show when={props.variant === 'panel'}>
      <div class="hs-input-editor__head">
        <label id={labelId} for={view() === 'value' && widget().kind !== 'none' ? id : undefined}>{props.label}</label>
        {menu()}
      </div>
    </Show>
    <div class="hs-input-editor__body">
      {content()}
      <Show when={props.variant === 'inline'}>{menu()}</Show>
    </div>
    <Show when={hint()}><small id={hintId} class="hs-field-hint">{hint()}</small></Show>
    <Show when={errors().length}>
      <ul id={errorsId} class="hs-input-editor__errors"><For each={errors()}>{message => <li>{message}</li>}</For></ul>
    </Show>
  </div>
}
