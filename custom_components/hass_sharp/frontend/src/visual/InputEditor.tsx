import { createMemo, createSignal, createUniqueId, For, Show } from 'solid-js'
import { Menu, type MenuItem } from '../components/Menu'
import { isAssignable, nodeCatalog, type InputSpec, type JsonValue, type Operand, type VisualCatalogSnapshot } from './contract'
import { typeLabel } from './typeLabels'
import { commitLiteral, fitsWidget, initialValue, inlineWidget, jsonEqual, literalFor, valueKind, namesRegistryItems, registryNames, valueSummary, WidgetControl, widgetFor, type CommitOptions, type Widget } from './widgets'
import { useRegistry } from '../registry'

/** A named variable visible to the input (D7), with the type of its producer. */
export interface EditorVariable { name: string; type: string }
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
  /** Variables visible to this input, for "Variable…"; only those assignable to the input are offered. */
  variables?: EditorVariable[]
  /** `targetEntityIds`: the entities the node acts on, for the state widget's options. */
  context?: { targetEntityIds?: string[] }
  /** Validation messages for this input; shown and associated with the control. */
  errors?: string[]
  /** Set while the input is connected: the source shown read-only (for example "Read entity · State"). */
  wiredFrom?: string
  catalog?: VisualCatalogSnapshot
}

const jsonWidget: Widget = { kind: 'json', multiple: false }
const same = (a: Operand, b: Operand) => a.kind === b.kind && (a.kind !== 'literal' || (b.kind === 'literal' && a.type === b.type && jsonEqual(a.value, b.value)))
  && (a.kind !== 'variable' || (b.kind === 'variable' && a.name === b.name))

/** The inline summary of a value; registry ids are named once the registry has loaded. */
function Summary(props: { widget: Widget; value: JsonValue | undefined }) {
  const Line = (p: { text: string }) => <span class="hs-input-editor__summary" title={p.text}>{p.text}</span>
  const Named = () => { const registry = useRegistry(); return <Line text={valueSummary(props.widget, props.value, registryNames(registry.data))} /> }
  return <Show when={namesRegistryItems(props.widget)} fallback={<Line text={valueSummary(props.widget, props.value)} />}><Named /></Show>
}

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
  // A list or object typed into a kind switch is as large as the JSON editor.
  const light = () => inlineWidget(shown()) && !(shown().kind === 'mixed' && valueKind(literal()) === 'json')
  const summary = () => props.variant === 'inline' && view() === 'value' && widget().kind !== 'none' && !light()
  const variableName = () => { const o = operand(); return o.kind === 'variable' ? o.name : '' }
  const variables = () => (props.variables ?? []).filter(v => props.input.types.some(t => isAssignable(v.type, t, catalog())))
  const choosingVariable = () => view() === 'variable' && (picking() || !variableName()) && variables().length > 0
  // The id of the control carrying the label and descriptions (the widget or the variable select); without one the group carries them.
  const control = () => view() === 'value' && widget().kind !== 'none' && !summary() ? id : choosingVariable() ? variableId : undefined
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
    setPicking(false)
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
    { label: 'Variable…', onSelect: () => { setPicking(true); focus(variableId) } },
    { label: 'Clear', onSelect: () => { setPicking(false); emit({ kind: 'unset' }) } },
  ]
  const menu = () => <Menu label={`${props.label} mode`} items={items()} disabled={props.disabled} />
  // Leaving the variable chooser returns focus to the mode menu it was opened from.
  const stopPicking = () => { setPicking(false); queueMicrotask(() => root.querySelector<HTMLElement>('button[aria-haspopup="menu"]')?.focus()) }
  const cancelPicking = (event: KeyboardEvent) => { if (event.key === 'Escape' && picking()) { event.preventDefault(); event.stopPropagation(); stopPicking() } }

  const content = () => <>
    <Show when={view() === 'value'}>
      <Show when={widget().kind !== 'none'} fallback={<span class="hs-input-editor__state hs-muted">
        {props.variant === 'inline' ? 'Connect an output' : `Connect an output of type ${typeNames()}.`}
      </span>}>
        <Show when={!summary()} fallback={<Summary widget={shown()} value={literal()} />}>
          <WidgetControl widget={shown()} value={literal()} commit={commit} id={id} label={props.label} describedBy={describedBy()} required={props.input.required}
            invalid={errors().length > 0} disabled={!!props.disabled} compact={props.variant === 'inline'} context={props.context} />
        </Show>
      </Show>
    </Show>
    <Show when={view() === 'wired'}>
      <span class="hs-input-editor__state">Connected to {props.wiredFrom}</span>
    </Show>
    <Show when={view() === 'variable'}>
      <Show when={picking() || !variableName()} fallback={<span class="hs-input-editor__state">Variable <code>{variableName()}</code></span>}>
        <Show when={variables().length} fallback={<span id={variableId} tabindex="-1" class="hs-input-editor__state hs-muted" onKeyDown={cancelPicking}>No matching variable is visible here.</span>}>
          <select id={variableId} class="hs-input" aria-label={`${props.label} variable`} aria-describedby={describedBy()} disabled={props.disabled}
            onKeyDown={cancelPicking} onChange={event => { const name = event.currentTarget.value; setPicking(false); if (name) emit({ kind: 'variable', name }) }}>
            <option value="" selected={!variables().some(v => v.name === variableName())} disabled>Choose a variable…</option>
            <For each={variables()}>{v => <option value={v.name} selected={v.name === variableName()}>{v.name} ({typeLabel(v.type, catalog())})</option>}</For>
          </select>
        </Show>
        <Show when={picking()}><button type="button" class="hs-button hs-button--ghost" onClick={stopPicking}>Cancel</button></Show>
      </Show>
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
