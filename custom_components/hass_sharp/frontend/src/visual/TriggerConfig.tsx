import { For, Show, createUniqueId } from 'solid-js'
import { Field } from '../components/Controls'
import type { GraphNode } from './graph'

type Config = GraphNode['config']

function Select(props: { label: string; value: string; options: [string, string][]; onChange: (value: string) => void }) {
  const id = createUniqueId()
  return <div class="hs-field"><label for={id}>{props.label}</label><select id={id} aria-label={props.label} class="hs-input" value={props.value} onChange={event => props.onChange(event.currentTarget.value)}>
    <For each={props.options}>{([value, label]) => <option value={value}>{label}</option>}</For>
  </select></div>
}

/** Type changes preserve inactive fields and extensions so editing never discards saved C#. */
export function TriggerConfig(props: { node: GraphNode; version: 1 | 2; onChange: (config: Config) => void }) {
  const config = () => props.node.config
  const type = () => String(config().type ?? 'entity')
  const set = (key: string, value: string | boolean) => props.onChange({ ...config(), [key]: value })
  const optional = (key: string, value: string | boolean) => {
    const next = { ...config() }
    if (value === '') delete next[key]; else next[key] = value
    props.onChange(next)
  }
  return <>
    <Field label="Trigger ID" aria-label="Trigger ID" value={props.node.id} readOnly hint="Stable action identity. Changing the label keeps this ID." />
    <Select label="Trigger type" value={type()} options={[
      ['entity', 'Entity changes'], ['daily', 'Daily'], ['once', 'Once'], ['manual', 'Manual'], ['startup', 'Startup'],
    ]} onChange={value => set('type', value)} />
    <Show when={type() === 'entity'}><Field label="Entity ID" aria-label="Entity ID" value={String(config().entity ?? '')} onInput={event => set('entity', event.currentTarget.value)} /></Show>
    <Show when={type() === 'daily'}>
      <Field label="Daily time" aria-label="Daily time" type="time" value={String(config().time ?? '')} onInput={event => set('time', event.currentTarget.value)} />
      <Field label="Time zone" aria-label="Time zone" placeholder="Europe/Zagreb" value={String(config().timeZone ?? '')} onInput={event => set('timeZone', event.currentTarget.value)} hint="Explicit system time zone, for example Europe/Zagreb or UTC." />
    </Show>
    <Show when={type() === 'once'}><Field label="Once timestamp" aria-label="Once timestamp" placeholder="2026-12-01T07:30:00+01:00" value={String(config().at ?? '')} onInput={event => set('at', event.currentTarget.value)} hint="ISO timestamp with an explicit offset or Z." /></Show>
    <Select label="Concurrency mode" value={String(config().mode ?? 'Single')} options={[
      ['Single', 'Single'], ['Restart', 'Restart'], ['Queued', 'Queued'], ['Parallel', 'Parallel'],
    ]} onChange={value => set('mode', value)} />
    <p class="hs-muted">Concurrency applies to whole invocations. Restart cancels the previous invocation; Parallel allows overlapping runs.</p>
    <Show when={props.version === 2}>
      <Select label="Startup override" value={config().startup == null ? '' : String(config().startup)} options={[
        ['', 'Follow trigger type'], ['true', 'Run at startup'], ['false', 'Do not run at startup'],
      ]} onChange={value => optional('startup', value === '' ? '' : value === 'true')} />
      <Select label="Allow manual runs" value={String(config().manual ?? true)} options={[
        ['true', 'Allowed'], ['false', 'Disabled'],
      ]} onChange={value => set('manual', value === 'true')} />
      <details><summary>Advanced C# subscriptions</summary><div class="hs-stack">
        <p class="hs-muted">Expressions override subscriptions and schedules. Entity, daily and once types still require their normal fields. Use Manual for fully custom subscriptions.</p>
        <For each={['entitiesExpression', 'schedulesExpression'] as const}>{key => {
          const label = key === 'entitiesExpression' ? 'Entities expression' : 'Schedules expression'
          const id = createUniqueId()
          return <div class="hs-field"><label for={id}>{label}</label><textarea id={id} aria-label={label} class="hs-input hs-code-input" rows="3" value={String(config()[key] ?? '')} onInput={event => optional(key, event.currentTarget.value)} /></div>
        }}</For>
      </div></details>
    </Show>
  </>
}
