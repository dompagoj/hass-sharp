import { For, Show, createEffect, createSignal, on, createMemo, createUniqueId } from 'solid-js'
import { Field } from '../components/Controls'
import { literal, outputType, type GraphNode, type Operand, type ValueType } from './graph'

export function ConfigSelect(props: { label: string; value: string; options: readonly (readonly [string, string])[]; onChange: (value: string) => void }) {
  const id = createUniqueId()
  return <div class="hs-field"><label for={id}>{props.label}</label><select id={id} aria-label={props.label} class="hs-input" value={props.value} onChange={event => props.onChange(event.currentTarget.value)}>
    <For each={props.options}>{([value, label]) => <option value={value} selected={props.value === value}>{label}</option>}</For>
  </select></div>
}
export const valueTypes = [['boolean', 'Boolean'], ['number', 'Number'], ['string', 'String']] as const
function NumericLiteral(props: { label: string; value: string | boolean | number; onChange: (value: number | string) => void }) {
  const [draft, setDraft] = createSignal(String(props.value))
  const valid = () => /^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/.test(draft()) && Number.isFinite(Number(draft()))
  createEffect(on(() => props.value, value => {
    // Keep intermediate formatting (e.g. -2.) while its numeric meaning is unchanged.
    if (!(typeof value === 'number' && valid() && Number(draft()) === value) && String(value) !== draft()) setDraft(String(value))
  }))
  return <Field label={props.label} aria-label={props.label} type="text" inputmode="decimal" value={draft()} aria-invalid={!valid() || undefined}
    hint={!valid() ? 'Enter a finite number, for example -2.5. This input must be completed before saving.' : undefined}
    onInput={event => {
      const text = event.currentTarget.value
      setDraft(text)
      // Incomplete text intentionally remains an invalid numeric operand in the graph,
      // so server preview blocks saving instead of silently substituting zero.
      props.onChange(valid() ? Number(text) : text)
    }} />
}
export function ValueOperand(props: { label: string; value: Operand; nodes: GraphNode[]; type?: ValueType; onChange: (operand: Operand) => void }) {
  const value = () => props.value ?? literal(props.type ?? 'string', props.type === 'boolean' ? true : props.type === 'number' ? 0 : '')
  const type = () => value().kind === 'literal' ? (value() as Extract<Operand, { kind: 'literal' }>).type : props.type ?? 'string'
  const variables = createMemo(() => [...new Map(props.nodes.filter(node => outputType(node) && (!props.type || outputType(node) === props.type)).map(node => [String(node.config.output), outputType(node)!])).entries()])
  const reset = (type: ValueType) => props.onChange(literal(type, type === 'boolean' ? true : type === 'number' ? 0 : ''))
  return <fieldset class="hs-operand"><legend>{props.label}</legend>
    <ConfigSelect label={`${props.label} source`} value={value().kind} options={[["literal", "Literal"], ["variable", "Value reference"]]} onChange={kind => kind === 'literal' ? reset(props.type ?? 'string') : props.onChange({ kind: 'variable', name: variables()[0]?.[0] ?? '' })} />
    <Show when={value().kind === 'variable'} fallback={<>
      <Show when={props.type && value().kind === 'literal' && type() !== props.type}><p class="hs-error">This input requires {props.type}. <button type="button" class="hs-button" onClick={() => reset(props.type!)}>Use {props.type} literal</button></p></Show>
      <Show when={!props.type}><ConfigSelect label={`${props.label} type`} value={type()} options={valueTypes} onChange={type => reset(type as ValueType)} /></Show>
      <Show when={type() === 'boolean'} fallback={<Show when={type() === 'number'} fallback={<Field label={`${props.label} literal`} aria-label={`${props.label} literal`} value={String((value() as any).value ?? '')} onInput={event => props.onChange(literal('string', event.currentTarget.value))} />}><NumericLiteral label={`${props.label} literal`} value={(value() as any).value} onChange={number => props.onChange(literal('number', number))} /></Show>}>
        <ConfigSelect label={`${props.label} literal`} value={String((value() as any).value)} options={[["true", "True"], ["false", "False"]]} onChange={v => props.onChange(literal('boolean', v === 'true'))} />
      </Show>
    </>}>
      <ConfigSelect label={`${props.label} reference`} value={(value() as any).name ?? ''} options={[
        ['', 'Choose a value'],
        ...variables().map(([name, type]) => [name, `${name} · ${type}`] as [string,string]),
        ...(value().kind === 'variable' && (value() as any).name && !variables().some(([name]) => name === (value() as any).name) ? [[(value() as any).name, `${(value() as any).name} (missing or incompatible)`] as [string,string]] : []),
      ]} onChange={name => props.onChange({ kind: 'variable', name })} />
      <p class="hs-muted">The producing step must run before this input. Values are local to this invocation.</p>
    </Show>
  </fieldset>
}
