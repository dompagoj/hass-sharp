import { Show, createUniqueId, For } from 'solid-js'
import { EntityAutocomplete } from '../components/EntityAutocomplete'
import { ServicePicker } from '../components/ServicePicker'
import { Field } from '../components/Controls'
import { useHass } from '../context'
import { ConfigSelect, ValueOperand, valueTypes } from './ValueOperand'
import { outputType, type GraphNode, type NodeConfigData, type Operand, type ValueType } from './graph'

/** Explicit fields keep wire keys out of the editing experience. */
export function NodeConfig(props: { node: GraphNode; nodes: GraphNode[]; onChange: (config: NodeConfigData) => void }) {
  const hass = useHass()
  const cfg = () => props.node.config
  const kind = () => props.node.kind
  const str = (key: string, fallback = '') => String(cfg()[key] ?? fallback)
  const set = (key: string, value: NodeConfigData[string]) => props.onChange({ ...cfg(), [key]: value })
  const has = (...kinds: string[]) => kinds.includes(kind())
  const input = (key: string, label: string, type = 'text', hint?: string) => <Field label={label} aria-label={label} type={type} value={str(key)} hint={hint} onInput={event => set(key, event.currentTarget.value)} />
  const operand = (key: string, label: string, type?: ValueType) => <ValueOperand label={label} value={cfg()[key] as Operand} type={type} nodes={props.nodes} onChange={value => set(key, value)} />
  const leftType = () => { const left = cfg().left as Operand; return left?.kind === 'literal' ? left.type : outputType(props.nodes.find(n => n.config.output === left?.name) ?? props.node) }
  const comparisonType = () => kind() === 'wait_until' ? str('type') : leftType()
  const attributeId = createUniqueId()
  const attributes = () => Object.keys(hass?.states?.[str('entity')]?.attributes ?? {}).sort()
  return <>
    <Show when={has('condition','service','read_entity','wait_until')}><EntityAutocomplete value={str('entity')} optional={kind() === 'service'} onChange={value => set('entity', value)} /></Show>
    <Show when={kind() === 'condition'}>{input('state', 'Required state')}</Show>
    <Show when={kind() === 'service'}>
      <ServicePicker value={str('service')} onChange={value => set('service', value)} />
      <Field label="Service data (JSON object)" aria-label="Service data (JSON object)" value={str('data','{}')} onInput={event => set('data',event.currentTarget.value)} hint={'Example: {"brightness": 128}. Target entity overrides entity_id.'} />
      {input('response', 'Response name', 'text', 'Optional. Custom C# can read responses["name"].')}
    </Show>
    <Show when={has('read_entity','wait_until')}>
      <ConfigSelect label="Entity field" value={str('field')} options={[["state","State"],["attribute","Attribute"]]} onChange={value => set('field',value)} />
      <Show when={str('field') === 'attribute'}><Field label="Attribute" aria-label="Attribute" list={attributeId} value={str('attribute')} onInput={event => set('attribute',event.currentTarget.value)} hint="Choose a live attribute or enter an unavailable attribute’s exact name. Missing attributes produce an error." /><datalist id={attributeId}><For each={attributes()}>{name => <option value={name} />}</For></datalist></Show>
    </Show>
    <Show when={has('read_entity','set_value','wait_until')}><ConfigSelect label="Result type" value={str('type')} options={valueTypes} onChange={value => set('type',value)} /><p class="hs-muted">Explicit conversion: booleans accept true/false, on/off and 1/0. Numbers use a decimal point and must be finite.</p></Show>
    <Show when={!!outputType(props.node)}>{input('output', 'Output value name', 'text', 'Letters, numbers and underscores; start with a letter or underscore. Reuse a name only with the same type.')}</Show>
    <Show when={has('if','not')}>{operand('value','Condition','boolean')}</Show>
    <Show when={has('and','or','xor')}>{operand('left','First input','boolean')}{operand('right','Second input','boolean')}</Show>
    <Show when={kind() === 'compare'}>{operand('left','Left operand')}</Show>
    <Show when={has('compare','wait_until')}>
      <ConfigSelect label="Comparison" value={str('operator')} options={[
        ['eq','Equals'],['ne','Does not equal'],
        ...(comparisonType() === 'number' ? [['lt','Less than'],['le','Less than or equal'],['gt','Greater than'],['ge','Greater than or equal']] as [string,string][] : []),
      ]} onChange={value => set('operator',value)} />
      {operand('right', 'Right operand', comparisonType() as ValueType)}
    </Show>
    <Show when={kind() === 'set_value'}>{operand('value','Value to assign')}</Show>
    <Show when={kind() === 'log'}><ConfigSelect label="Log level" value={str('level')} options={['Debug','Info','Warn','Error'].map(level => [level,level])} onChange={value => set('level',value)} />{operand('value','Message')}</Show>
    <Show when={kind() === 'delay'}>{input('seconds','Seconds','number','Cancellable pause before Next.')}</Show>
    <Show when={kind() === 'repeat'}>{input('count','Repeat count','number','Whole number from 0 to 1,000.')}<p class="hs-muted">Connect Body to separate steps. An unconnected output ends an iteration. Connect Next to the steps after all iterations. Body nodes cannot connect to Next’s path or be shared outside this repeat. Up to 8 nested repeats; 100,000 steps per invocation.</p></Show>
    <Show when={kind() === 'wait_until'}>{input('timeout','Timeout seconds','number','Required: 0.01–86,400 seconds. Timeout follows On error or fails the invocation.')}{input('interval','Poll interval seconds','number','0.01–3,600 seconds. Reads live entity data on every poll; cancellation ends immediately.')}</Show>
    <Show when={kind() === 'stop'}><p>Stop ends the whole invocation, even inside a Repeat body.</p></Show>
    <Show when={kind() === 'custom'}><div class="hs-field"><label for="hs-custom-code">Custom C# section</label><textarea id="hs-custom-code" aria-label="Custom C# section" class="hs-input hs-code-input" rows="6" value={str('code')} onInput={event => set('code',event.currentTarget.value)} /></div></Show>
  </>
}
