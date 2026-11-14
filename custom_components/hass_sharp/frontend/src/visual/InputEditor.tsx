import { createMemo, createUniqueId, For, Show } from 'solid-js'
import { Menu, type MenuItem } from '../components/Menu'
import { nodeCatalog, type InputSpec, type JsonValue, type Operand, type VisualCatalogSnapshot } from './contract'
import { typeLabel } from './typeLabels'
import { commitLiteral, fitsWidget, initialValue, inlineWidget, jsonEqual, keepsNull, literalFor, valueKind, namesRegistryItems, registryNames, valueSummary, WidgetControl, widgetFor, type CommitOptions, type Widget } from './widgets'
import { useRegistry } from '../registry'

export interface InputEditorProps {
  /** The resolved input spec (`dataInputs`), including C2 constraints and the raw selector. */
  input: InputSpec
  /** The stored operand; undefined is the same as not set. */
  value: Operand | undefined
  /**
   * One call per committed change. Drags of a slider or colour picker pass the same `coalesce` key for every
   * change of that drag, so the parent can merge them into one undo step.
   */
  onChange(operand: Operand, options?: { coalesce?: string }): void
  /**
   * `inline`: compact, for a node card (no visible label or hint); a widget that is not `inlineWidget` shows a
   * summary of its value instead. `panel`: labelled, with the description.
   */
  variant: 'inline' | 'panel'
  /** Accessible name of every control, and the visible label in the panel. */
  label: string
  disabled?: boolean
  /** `targetEntityIds`: the entities the node acts on, for the state widget's options. */
  context?: { targetEntityIds?: string[] }
  /** Validation messages for this input; shown and associated with the control. */
  errors?: string[]
  /** Set while the input is connected: the source shown read-only (for example "Read entity · State"). */
  wiredFrom?: string
  catalog?: VisualCatalogSnapshot
  /**
   * A stored value of another type than the input's (a Constant's value from before its Type set the value's type) is
   * shown in the input's own number, text or yes/no editor, as text, and committing it stores it as the input's type
   * (a yes/no from the text true or false, any case).
   */
  retype?: boolean
}

const jsonWidget: Widget = { kind: 'json', multiple: false }
const same = (a: Operand, b: Operand) => a.kind === b.kind && (a.kind !== 'literal' || (b.kind === 'literal' && a.type === b.type && jsonEqual(a.value, b.value)))

/** The inline summary of a value; registry ids are named once the registry has loaded. */
function Summary(props: { widget: Widget; value: JsonValue | undefined }) {
  const Line = (p: { text: string }) => <span class="hs-input-editor__summary" classList={{ 'hs-input-editor__summary--empty': props.value === undefined }} title={p.text}>{p.text}</span>
  const Named = () => { const registry = useRegistry(); return <Line text={valueSummary(props.widget, props.value, registryNames(registry.data))} /> }
  return <Show when={namesRegistryItems(props.widget)} fallback={<Line text={valueSummary(props.widget, props.value)} />}><Named /></Show>
}

/** Edits one operand of one input with the widget its type and selector call for, plus a Value / Clear menu. */
export function InputEditor(props: InputEditorProps) {
  const id = createUniqueId(), labelId = `${id}-label`, hintId = `${id}-hint`, errorsId = `${id}-errors`
  let root!: HTMLDivElement
  const catalog = () => props.catalog ?? nodeCatalog
  const operand = (): Operand => props.value ?? { kind: 'unset' }
  // A literal null (how an empty optional value is often stored) is edited and summarised as not set, never as "null";
  // in the JSON editor it is a value someone wrote, and stays one.
  const literal = () => { const o = operand(); return o.kind === 'literal' && (o.value !== null || keepsNull(widget())) ? o.value : undefined }
  const widget = createMemo(() => widgetFor(props.input, catalog()))
  // A Constant's stored text or number of another kind than its Type is shown in the Type's own editor (see `retype`).
  const stale = () => {
    const value = literal(), kind = widget().kind, stored = operand()
    return !!props.retype && (kind === 'number' || kind === 'text' || kind === 'toggle') && !widget().multiple && (typeof value === 'string' || typeof value === 'number')
      && (!fitsWidget(widget(), value) || (stored.kind === 'literal' && !props.input.types.includes(stored.type)))
  }
  // A stored value of another shape stays editable as JSON instead of being misread by the widget.
  const shown = () => fitsWidget(widget(), literal()) || stale() ? widget() : jsonWidget
  const view = () => props.wiredFrom !== undefined ? 'wired' : 'value'
  // A list or object typed into a kind switch is as large as the JSON editor.
  const light = () => inlineWidget(shown()) && !(shown().kind === 'mixed' && valueKind(literal()) === 'json')
  const summary = () => props.variant === 'inline' && view() === 'value' && widget().kind !== 'none' && !light()
  // The id of the control carrying the label and descriptions (the widget); without one the group carries them.
  const control = () => view() === 'value' && widget().kind !== 'none' && !summary() ? id : undefined
  const errors = () => props.errors ?? []
  const hint = () => props.variant === 'panel' ? props.input.description : ''
  const describedBy = () => [errors().length && errorsId, hint() && hintId].filter(Boolean).join(' ') || undefined
  const typeNames = () => [...new Set(props.input.types.map(t => typeLabel(t, catalog())))].join(' or ')

  const emit = (next: Operand, options?: { coalesce?: string }) => {
    if (props.disabled) return
    // While connected, any choice is news to the parent: it decides whether to disconnect.
    if (props.wiredFrom === undefined && same(next, operand())) return
    props.onChange(next, options?.coalesce ? { coalesce: options.coalesce } : undefined)
  }
  const commit = (value: JsonValue | undefined, options?: CommitOptions) =>
    emit(value === undefined ? { kind: 'unset' } : commitLiteral(props.input, value, options, catalog()), options)
  const focus = (target: string) => queueMicrotask(() => root.querySelector<HTMLElement>(`[id="${target}"]`)?.focus())
  const chooseValue = () => {
    const current = operand()
    // A value kept under a connection comes back; otherwise start from the input's default.
    if (current.kind === 'literal') { if (props.wiredFrom !== undefined) emit(current) }
    else {
      const start = initialValue(widget(), props.input, catalog())
      emit(start === undefined ? { kind: 'unset' } : literalFor(props.input, start, catalog()))
    }
    focus(id)
  }
  const items = (): MenuItem[] => [
    ...(widget().kind === 'none' ? [] : [{ label: 'Value', onSelect: chooseValue }]),
    { label: 'Clear', onSelect: () => emit({ kind: 'unset' }) },
  ]
  const menu = () => <Menu label={`${props.label} mode`} items={items()} disabled={props.disabled} />

  const content = () => <>
    <Show when={view() === 'value'}>
      <Show when={widget().kind !== 'none'} fallback={<span class="hs-input-editor__state hs-muted">
        {props.retype ? (props.variant === 'inline' ? 'Choose another Type' : 'No value can be typed in for this Type: choose another.') : props.variant === 'inline' ? 'Connect an output' : `Connect an output of type ${typeNames()}.`}
      </span>}>
        <Show when={!summary()} fallback={<Summary widget={shown()} value={literal()} />}>
          <WidgetControl widget={shown()} value={literal()} commit={commit} id={id} label={props.label} describedBy={describedBy()} required={props.input.required}
            invalid={errors().length > 0} disabled={!!props.disabled} compact={props.variant === 'inline'} context={props.context} stale={stale()} />
        </Show>
      </Show>
    </Show>
    <Show when={view() === 'wired'}>
      <span class="hs-input-editor__state">Connected to {props.wiredFrom}</span>
    </Show>
  </>

  return <div ref={root} class={`hs-input-editor hs-input-editor--${props.variant}`} role="group" data-mode={view()}
    aria-labelledby={props.variant === 'panel' ? labelId : undefined} aria-label={props.variant === 'inline' ? props.label : undefined}
    aria-describedby={control() ? undefined : describedBy()}>
    <Show when={props.variant === 'panel'}>
      <div class="hs-input-editor__head">
        <label id={labelId} for={control()}>{props.label}</label>
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
