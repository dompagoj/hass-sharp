import { For, Show, createUniqueId, createSignal, onMount, onCleanup } from 'solid-js'
import { EntityAutocomplete } from '../components/EntityAutocomplete'
import { Field } from '../components/Controls'
import type { GraphNode } from './graph'
import { useHass } from '../context'
import { JsonField } from './ValueOperand'

type Config = GraphNode['config']

function Select(props: { label: string; value: string; options: [string, string][]; onChange: (value: string) => void }) {
  const id = createUniqueId()
  return (
    <div class="hs-field">
      <label for={id}>{props.label}</label>
      <select
        id={id}
        aria-label={props.label}
        class="hs-input"
        value={props.value}
        onChange={event => props.onChange(event.currentTarget.value)}
      >
        <For each={props.options}>{([value, label]) => <option value={value}>{label}</option>}</For>
      </select>
    </div>
  )
}

/** Type changes preserve inactive fields and extensions so editing never discards saved C#. */
export function TriggerConfig(props: { node: GraphNode; onChange: (config: Config) => void }) {
  const hass = useHass()
  const [platforms, setPlatforms] = createSignal<Record<string, unknown>>({})
  const [discoveryError, setDiscoveryError] = createSignal('')
  let unsubscribe: (() => void) | undefined,
    disposed = false
  onMount(async () => {
    if (!hass.connection?.subscribeMessage) return
    try {
      const stop = await hass.connection.subscribeMessage<Record<string, unknown>>(
        event => setPlatforms(current => ({ ...current, ...event })),
        { type: 'trigger_platforms/subscribe' },
      )
      if (disposed) stop()
      else unsubscribe = stop
    } catch (error) {
      if (!disposed) setDiscoveryError(error instanceof Error ? error.message : String(error))
    }
  })
  onCleanup(() => {
    disposed = true
    unsubscribe?.()
  })
  const config = () => props.node.config
  const native = () =>
    config().trigger && typeof config().trigger === 'object' && !Array.isArray(config().trigger)
      ? (config().trigger as Record<string, unknown>)
      : {}
  const platform = () => String(native().trigger ?? native().platform ?? 'state')
  const aliases = ['state', 'numeric_state', 'event', 'time', 'time_pattern', 'sun', 'zone']
  const nativeSet = (key: string, value: unknown) => {
    const next: Record<string, unknown> = {
      ...(native().trigger === undefined && native().platform === undefined ? { trigger: platform() } : {}),
      ...native(),
    }
    if (value === '' && !['trigger', 'platform'].includes(key)) delete next[key]
    else next[key] = value
    props.onChange({ ...config(), trigger: next })
  }
  const platformOptions = () =>
    [...new Set([...aliases, ...Object.keys(platforms()), platform()])].map(id => [id, id.replaceAll('_', ' ')] as [string, string])
  const nativeField = (key: string, label: string, kind = 'string') => (
    <Field
      label={label}
      aria-label={label}
      value={String(native()[key] ?? '')}
      onInput={event =>
        nativeSet(
          key,
          kind === 'number' && event.currentTarget.value.trim() !== '' && Number.isFinite(Number(event.currentTarget.value))
            ? Number(event.currentTarget.value)
            : event.currentTarget.value,
        )
      }
    />
  )

  const type = () => String(config().type ?? 'entity')
  const set = (key: string, value: string | boolean) =>
    props.onChange({
      ...config(),
      [key]: value,
      ...(key === 'type' && value === 'ha' && config().trigger === undefined ? { trigger: { trigger: 'state' } } : {}),
    })
  const optional = (key: string, value: string | boolean) => {
    const next = { ...config() }
    if (value === '') delete next[key]
    else next[key] = value
    props.onChange(next)
  }
  return (
    <>
      <Field
        label="Trigger ID"
        aria-label="Trigger ID"
        value={props.node.id}
        readOnly
        hint="Stable action identity. Changing the label keeps this ID."
      />
      <Select
        label="Trigger type"
        value={type()}
        options={[
          ['ha', 'Home Assistant'],
          ['entity', 'Entity changes'],
          ['daily', 'Daily'],
          ['once', 'Once'],
          ['manual', 'Manual'],
          ['startup', 'Startup'],
        ]}
        onChange={value => set('type', value)}
      />
      <Show when={type() === 'entity'}>
        <div data-input-id="entity" tabindex="-1">
          <EntityAutocomplete value={String(config().entity ?? '')} onChange={value => set('entity', value)} />
        </div>
      </Show>
      <Show when={type() === 'ha'}>
        <Select
          label="Native trigger platform"
          value={platform()}
          options={platformOptions()}
          onChange={value => {
            const next = { ...native() }
            if (Object.hasOwn(next, 'platform') && !Object.hasOwn(next, 'trigger')) next.platform = value
            else next.trigger = value
            if (next.event === undefined && ['sun', 'zone'].includes(value)) next.event = value === 'sun' ? 'sunrise' : 'enter'
            props.onChange({ ...config(), trigger: next })
          }}
        />
        <Field
          label="Platform identifier"
          aria-label="Platform identifier"
          value={platform()}
          hint="Any installed platform is supported, including platforms without a description."
          onInput={event =>
            nativeSet(
              Object.hasOwn(native(), 'platform') && !Object.hasOwn(native(), 'trigger') ? 'platform' : 'trigger',
              event.currentTarget.value,
            )
          }
        />
        <Show when={['state', 'numeric_state', 'zone'].includes(platform())}>{nativeField('entity_id', 'Native entity ID')}</Show>
        <Show when={platform() === 'state'}>
          {nativeField('from', 'From state')}
          {nativeField('to', 'To state')}
          {nativeField('attribute', 'Attribute')}
          {nativeField('for', 'For duration')}
        </Show>
        <Show when={platform() === 'numeric_state'}>
          {nativeField('above', 'Above', 'number')}
          {nativeField('below', 'Below', 'number')}
          {nativeField('attribute', 'Attribute')}
          {nativeField('for', 'For duration')}
        </Show>
        <Show when={platform() === 'event'}>
          {nativeField('event_type', 'Event type')}
          <JsonField label="Event data" value={native().event_data ?? {}} onChange={value => nativeSet('event_data', value)} />
        </Show>
        <Show when={platform() === 'time'}>{nativeField('at', 'At time')}</Show>
        <Show when={platform() === 'time_pattern'}>
          {nativeField('hours', 'Hours')}
          {nativeField('minutes', 'Minutes')}
          {nativeField('seconds', 'Seconds')}
        </Show>
        <Show when={platform() === 'sun'}>
          <Select
            label="Sun event"
            value={String(native().event ?? 'sunrise')}
            options={[
              ['sunrise', 'Sunrise'],
              ['sunset', 'Sunset'],
            ]}
            onChange={value => nativeSet('event', value)}
          />
          {nativeField('offset', 'Offset')}
        </Show>
        <Show when={platform() === 'zone'}>
          {nativeField('zone', 'Zone')}
          <Select
            label="Zone event"
            value={String(native().event ?? 'enter')}
            options={[
              ['enter', 'Enter'],
              ['leave', 'Leave'],
            ]}
            onChange={value => nativeSet('event', value)}
          />
        </Show>
        <div data-input-id="trigger" tabindex="-1">
          <JsonField
            label="Native trigger JSON"
            value={config().trigger ?? { trigger: platform() }}
            onChange={value => props.onChange({ ...config(), trigger: value })}
            hint="Home Assistant validates this declaration. Unknown fields are preserved."
          />
        </div>
        <Show when={discoveryError()}>
          <p class="hs-muted">Platform discovery unavailable: {discoveryError()}. Enter a platform identifier and JSON.</p>
        </Show>
      </Show>
      <Show when={type() === 'daily'}>
        <Field
          data-input-id="time"
          label="Daily time"
          aria-label="Daily time"
          type="time"
          value={String(config().time ?? '')}
          onInput={event => set('time', event.currentTarget.value)}
        />
        <Field
          data-input-id="timeZone"
          label="Time zone"
          aria-label="Time zone"
          placeholder="Europe/Zagreb"
          value={String(config().timeZone ?? '')}
          onInput={event => set('timeZone', event.currentTarget.value)}
          hint="Explicit system time zone, for example Europe/Zagreb or UTC."
        />
      </Show>
      <Show when={type() === 'once'}>
        <Field
          data-input-id="at"
          label="Once timestamp"
          aria-label="Once timestamp"
          placeholder="2026-12-01T07:30:00+01:00"
          value={String(config().at ?? '')}
          onInput={event => set('at', event.currentTarget.value)}
          hint="ISO timestamp with an explicit offset or Z."
        />
      </Show>
      <Select
        label="Concurrency mode"
        value={String(config().mode ?? 'Single')}
        options={[
          ['Single', 'Single'],
          ['Restart', 'Restart'],
          ['Queued', 'Queued'],
          ['Parallel', 'Parallel'],
        ]}
        onChange={value => set('mode', value)}
      />
      <p class="hs-muted">
        Concurrency applies to whole invocations. Restart cancels the previous invocation; Parallel allows overlapping runs.
      </p>
      <Select
        label="Startup override"
        value={config().startup == null ? '' : String(config().startup)}
        options={[
          ['', 'Follow trigger type'],
          ['true', 'Run at startup'],
          ['false', 'Do not run at startup'],
        ]}
        onChange={value => optional('startup', value === '' ? '' : value === 'true')}
      />
      <Select
        label="Allow manual runs"
        value={String(config().manual ?? true)}
        options={[
          ['true', 'Allowed'],
          ['false', 'Disabled'],
        ]}
        onChange={value => set('manual', value === 'true')}
      />
      <details>
        <summary>Advanced C# subscriptions</summary>
        <div class="hs-stack">
          <p class="hs-muted">
            Expressions override subscriptions and schedules. Entity, daily and once types still require their normal fields. Use Manual for
            fully custom subscriptions.
          </p>
          <For each={['entitiesExpression', 'schedulesExpression'] as const}>
            {key => {
              const label = key === 'entitiesExpression' ? 'Entities expression' : 'Schedules expression'
              const id = createUniqueId()
              return (
                <div class="hs-field">
                  <label for={id}>{label}</label>
                  <textarea
                    id={id}
                    aria-label={label}
                    class="hs-input hs-code-input"
                    rows="3"
                    value={String(config()[key] ?? '')}
                    onInput={event => optional(key, event.currentTarget.value)}
                  />
                </div>
              )
            }}
          </For>
        </div>
      </details>
    </>
  )
}
