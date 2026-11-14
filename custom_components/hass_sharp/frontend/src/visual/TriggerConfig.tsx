import { createSignal, createUniqueId, For, onCleanup, onMount, Show } from 'solid-js'
import { useHass } from '../context'
import { dataOutputs, isAssignable, nodeCatalog, type GraphNode, type InputSpec, type JsonValue, type VisualCatalogSnapshot } from './contract'
import { nodeInputs, removeEdge, updateNode, type VisualGraph } from './graph'
import type { VisualDiagnostic } from './api'
import { domainWords } from './typeLabels'
import { fitsWidget, stateField, widgetFor, WidgetControl, type Widget } from './widgets'
import { Glyph } from './Glyph'
import { parseNumber } from './widgets/number'

type Config = GraphNode['config']
type Change = (config: Config, options?: { coalesce?: string }) => void
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)

/** The trigger types people choose from: HassSharp's own and the common Home Assistant platforms. */
interface Choice { id: string; name: string; explanation: string; icon: readonly string[] }
const clock = ['M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z', 'M12 7.5V12l3 2']
const calendar = ['M4 6h16v14H4z', 'M4 10h16', 'M8 3v4', 'M16 3v4']
export const triggerChoices: Choice[] = [
  { id: 'entity', name: 'Entity changes', explanation: 'Runs whenever the entity’s state changes.', icon: ['M4 12a8 8 0 0 1 14-5.3', 'M18 3v4h-4', 'M20 12a8 8 0 0 1-14 5.3', 'M6 21v-4h4'] },
  { id: 'state', name: 'State', explanation: 'Runs when an entity becomes a state, optionally for a while.', icon: ['M8 7h8a5 5 0 0 1 0 10H8A5 5 0 0 1 8 7z', 'M16 9.5a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5z'] },
  { id: 'numeric_state', name: 'Numeric state', explanation: 'Runs when a number rises above or drops below a limit.', icon: ['M4 19h16', 'M4 15l4-4 4 3 7-7', 'M15 7h4v4'] },
  { id: 'time', name: 'Time', explanation: 'Runs at a time of day, or the time a helper holds.', icon: clock },
  { id: 'daily', name: 'Every day', explanation: 'Runs every day at a set time in a set time zone.', icon: calendar },
  { id: 'once', name: 'Once', explanation: 'Runs one time, at a set date and time.', icon: [...calendar, 'M12 14.5v.5'] },
  { id: 'time_pattern', name: 'Time pattern', explanation: 'Runs every so many hours, minutes or seconds.', icon: ['M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z', 'M12 3v3', 'M12 18v3', 'M3 12h3', 'M18 12h3'] },
  { id: 'sun', name: 'Sunrise or sunset', explanation: 'Runs at sunrise or sunset, with an optional offset.', icon: ['M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8z', 'M12 2v2', 'M12 20v2', 'M2 12h2', 'M20 12h2', 'M4.9 4.9l1.4 1.4', 'M17.7 17.7l1.4 1.4', 'M4.9 19.1l1.4-1.4', 'M17.7 6.3l1.4-1.4'] },
  { id: 'zone', name: 'Zone', explanation: 'Runs when someone enters or leaves a zone.', icon: ['M12 21s-7-6.2-7-11a7 7 0 0 1 14 0c0 4.8-7 11-7 11z', 'M12 7.5a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5z'] },
  { id: 'event', name: 'Event', explanation: 'Runs when an event fires in Home Assistant.', icon: ['M3 12h4l2-6 4 12 2-6h6'] },
  { id: 'startup', name: 'Home Assistant starts', explanation: 'Runs once each time Home Assistant starts.', icon: ['M12 3v8', 'M6.3 6.3a8 8 0 1 0 11.4 0'] },
  { id: 'manual', name: 'Only manually', explanation: 'Runs only when started with Run now or by another automation.', icon: ['M7 4.5v15l12-7.5z'] },
  { id: 'other', name: 'Other trigger', explanation: 'Any other trigger an integration in Home Assistant offers.', icon: ['M9 3v5', 'M15 3v5', 'M6 8h12v3a6 6 0 0 1-12 0z', 'M12 17v4'] },
]
const native = ['entity', 'daily', 'once', 'manual', 'startup']
const platforms = ['state', 'numeric_state', 'time', 'time_pattern', 'sun', 'zone', 'event']

/**
 * Where each trigger type keeps its settings, by what they mean: `config:<key>` in the node's config,
 * `trigger:<key>` in the Home Assistant declaration. Two types share a setting only under the same meaning, so a
 * sun trigger's `event` (sunrise) never becomes a zone trigger's `event` (enter).
 */
const typeFields: Record<string, Record<string, string>> = {
  entity: { entity: 'config:entity' },
  daily: { timeOfDay: 'config:time', timeZone: 'config:timeZone' },
  once: { dateTime: 'config:at' },
  manual: {},
  startup: {},
  state: { entity: 'trigger:entity_id', attribute: 'trigger:attribute', from: 'trigger:from', to: 'trigger:to', for: 'trigger:for' },
  numeric_state: { entity: 'trigger:entity_id', attribute: 'trigger:attribute', above: 'trigger:above', below: 'trigger:below', for: 'trigger:for' },
  time: { timeOfDay: 'trigger:at' },
  time_pattern: { hours: 'trigger:hours', minutes: 'trigger:minutes', seconds: 'trigger:seconds' },
  sun: { sunEvent: 'trigger:event', offset: 'trigger:offset' },
  zone: { who: 'trigger:entity_id', zone: 'trigger:zone', zoneEvent: 'trigger:event' },
  event: { eventType: 'trigger:event_type', eventData: 'trigger:event_data' },
  other: {},
}
/** The config keys that belong to one type or another; every other key (mode, manual, startup, …) is the trigger's own. */
const typeKeys = ['type', 'entity', 'time', 'timeZone', 'at', 'trigger']
/** Options every Home Assistant trigger has, whatever its platform. */
const commonOptions = ['id', 'enabled', 'variables']
const entityId = (value: unknown): value is string => typeof value === 'string' && /^[a-z0-9_]+\.[a-z0-9_]+$/.test(value)
/** A time of day as `HH:MM` or `HH:MM:SS`, in range. */
const timeOfDayText = (value: unknown, seconds: 'any' | 'zero') => {
  const m = typeof value === 'string' ? /^(\d\d):(\d\d)(?::(\d\d))?$/.exec(value) : null
  return !!m && Number(m[1]) < 24 && Number(m[2]) < 60 && (m[3] === undefined || (seconds === 'any' ? Number(m[3]) < 60 : m[3] === '00'))
}
/**
 * A setting moved to another type, as that type's own form could have written it; undefined when it could not.
 * "Every day" holds a time as `HH:MM` (the backend reads nothing else); "Entity changes" one entity id; Home
 * Assistant's entity fields one id as text or several as a list; an attribute is text.
 */
function carried(meaning: string, location: string, value: unknown): unknown {
  switch (meaning) {
    case 'entity': {
      const ids = Array.isArray(value) ? value : [value]
      if (!ids.length || !ids.every(entityId)) return undefined
      if (location === 'config:entity') return ids.length === 1 ? ids[0] : undefined
      return ids.length === 1 ? ids[0] : ids
    }
    case 'timeOfDay':
      if (location === 'config:time') return timeOfDayText(value, 'zero') ? timeOfDay(value as string) : undefined
      return timeOfDayText(value, 'any') ? value : undefined
    case 'attribute': return typeof value === 'string' ? value : undefined
    // The rest moves between forms that edit it with the same widget (a duration between the two state triggers).
    default: return value
  }
}

/** The platform a Home Assistant trigger declaration names; `state` when it names none. */
const declaredPlatform = (declaration: Record<string, unknown>) => String(declaration.trigger ?? declaration.platform ?? 'state')
/** The key a declaration names its platform with: the older `platform`, or `trigger`. */
const platformKeyOf = (declaration: Record<string, unknown>) =>
  Object.hasOwn(declaration, 'platform') && !Object.hasOwn(declaration, 'trigger') ? 'platform' : 'trigger'

/** The choice a trigger config stands for: a HassSharp type, a known Home Assistant platform, or "other". */
export function triggerChoice(config: Config): string {
  const type = typeof config.type === 'string' ? config.type : 'entity'
  if (type !== 'ha') return type
  const platform = declaredPlatform(record(config.trigger) ? config.trigger : {})
  return platforms.includes(platform) ? platform : 'other'
}

/**
 * The config of a trigger changed to another type: the new type's defaults, the trigger's own settings (run mode,
 * Run now, startup), and only the settings both types have. Choosing the current type changes nothing.
 */
export function retypeTrigger(config: Config, id: string): Config {
  const from = triggerChoice(config)
  if (from === id) return config
  const declaration = record(config.trigger) ? config.trigger : {}
  const read = (location: string) => {
    const [where, key] = location.split(':')
    return where === 'config' ? config[key] : declaration[key]
  }
  const next: Config = Object.fromEntries(Object.entries(config).filter(([key]) => !typeKeys.includes(key)))
  const trigger: Record<string, unknown> = {}
  if (native.includes(id)) next.type = id
  else {
    next.type = 'ha'
    trigger[platformKeyOf(declaration)] = id === 'other' ? '' : id
    for (const key of commonOptions) if (Object.hasOwn(declaration, key)) trigger[key] = declaration[key]
    if (id === 'sun') trigger.event = 'sunrise'
    if (id === 'zone') trigger.event = 'enter'
  }
  const target = typeFields[id] ?? {}
  for (const [meaning, location] of Object.entries(typeFields[from] ?? {})) {
    const to = target[meaning]
    const value = to ? carried(meaning, to, read(location)) : undefined
    if (value === undefined || value === null || value === '') continue
    const [where, key] = to.split(':')
    if (where === 'config') next[key] = value
    else trigger[key] = value
  }
  if (next.type === 'ha') next.trigger = trigger
  return next
}
/**
 * A trigger node changed to another type (`retypeTrigger`, `retypedLabel`), as one graph change: the wires from outputs
 * the new type does not have, or whose new type no longer fits where they go, are removed (the inputs they fed are left
 * unset, as a disconnect leaves them) and named in the notice, as Apply does for a Custom C# node.
 */
export function retypeTriggerNode(graph: VisualGraph, nodeId: string, config: Config, label: string | undefined, catalog: VisualCatalogSnapshot = nodeCatalog): { graph: VisualGraph; notice?: string } {
  const node = graph.nodes.find(n => n.id === nodeId)
  if (!node) return { graph }
  let next = updateNode(graph, nodeId, label === node.label ? { config } : { config, label })
  const before = dataOutputs(node, catalog), after = dataOutputs(next.nodes.find(n => n.id === nodeId)!, catalog)
  const missing: string[] = [], misfit: string[] = []
  for (const edge of graph.edges) {
    if (edge.kind !== 'data' || edge.source !== nodeId) continue
    const output = Object.hasOwn(after, edge.sourcePort) ? after[edge.sourcePort] : undefined
    const target = next.nodes.find(n => n.id === edge.target)
    const input = target ? nodeInputs(next, target, catalog)[edge.targetPort] : undefined
    const name = (Object.hasOwn(before, edge.sourcePort) ? before[edge.sourcePort].label : undefined) ?? edge.sourcePort
    if (!output) missing.push(name)
    else if (input && !input.types.some(type => isAssignable(output.type, type, catalog))) misfit.push(name)
    else continue
    next = removeEdge(next, edge.id, catalog)
  }
  const list = (names: string[]) => [...new Set(names)].join(', ')
  const count = (n: number) => `${n} ${n === 1 ? 'connection' : 'connections'}`
  const notes = [
    ...(missing.length ? [`Removed ${count(missing.length)} the new trigger does not have: ${list(missing)}`] : []),
    ...(misfit.length ? [`Removed ${count(misfit.length)} that no longer ${misfit.length === 1 ? 'fits' : 'fit'}: ${list(misfit)}`] : []),
  ]
  return { graph: next, notice: notes.length ? `${notes.join('. ')}.` : undefined }
}
/**
 * The label after a type change: a label the editor wrote (the old type's name) becomes the new type's name; a label
 * the person typed, or no label, stays. A typed label that is exactly the old type's name cannot be told apart from a
 * written one, and follows the type too.
 */
export function retypedLabel(label: string | undefined, from: string, to: string): string | undefined {
  const old = triggerChoices.find(c => c.id === from)?.name
  return label !== undefined && label === old ? triggerChoices.find(c => c.id === to)?.name ?? label : label
}
const runModes = [
  ['Single', 'Ignore it', 'Keep the current run; the new trigger is skipped.'],
  ['Restart', 'Start over', 'Stop the current run and start a new one.'],
  ['Queued', 'Run afterwards', 'Let the current run finish, then run again.'],
  ['Parallel', 'Run alongside', 'Start another run at the same time.'],
] as const

/** A value edited with a shared widget, stored as a plain value (trigger settings are not operands). */
function ValueField(props: {
  label: string
  hint?: string
  widget: Widget
  value: unknown
  required?: boolean
  inputId?: string
  errors?: string[]
  context?: { targetEntityIds?: string[] }
  onChange(value: JsonValue | undefined): void
}) {
  const id = createUniqueId(), hintId = `${id}-hint`, errorsId = `${id}-errors`
  const value = () => (props.value === undefined || props.value === null ? undefined : (props.value as JsonValue))
  // A stored value of another shape stays editable as JSON instead of being misread by the widget.
  const widget = (): Widget => (fitsWidget(props.widget, value()) ? props.widget : { kind: 'json', multiple: false })
  const errors = () => props.errors ?? []
  const describedBy = () => [errors().length && errorsId, props.hint && hintId].filter(Boolean).join(' ') || undefined
  return (
    <div class="hs-insp-field" data-input-id={props.inputId} aria-invalid={errors().length > 0 || undefined}>
      <label class="hs-insp-field__label" for={id}>{props.label}</label>
      <WidgetControl widget={widget()} value={value()} commit={v => props.onChange(v)} id={id} label={props.label} describedBy={describedBy()}
        invalid={errors().length > 0} disabled={false} compact={false} required={!!props.required} context={props.context} />
      <Show when={props.hint}><small id={hintId} class="hs-field-hint">{props.hint}</small></Show>
      <Show when={errors().length}><ul id={errorsId} class="hs-input-editor__errors"><For each={errors()}>{m => <li>{m}</li>}</For></ul></Show>
    </div>
  )
}
const entityWidget = (multiple: boolean, domains: string[] = []): Widget => ({ kind: 'entity', multiple, domains })
const text: Widget = { kind: 'text', multiple: false }
/** One entity id stays a string; several are a list. */
const entityValue = (value: JsonValue | undefined) => (Array.isArray(value) && value.length === 1 ? value[0] : value)
/** "07:30:00" is stored as Home Assistant and HassSharp write a time of day without seconds. */
const timeOfDay = (value: JsonValue | undefined) => (typeof value === 'string' && /^\d\d:\d\d:00$/.test(value) ? value.slice(0, 5) : value)
/** A number stays a number; anything else (an entity holding the limit) is kept as text. */
const numberOrText = (value: JsonValue | undefined) => {
  const number = typeof value === 'string' ? parseNumber(value) : undefined
  return number !== undefined && Number.isFinite(number) ? number : value
}
const timeZones = (() => {
  try {
    return (Intl as unknown as { supportedValuesOf?(key: string): string[] }).supportedValuesOf?.('timeZone') ?? []
  } catch {
    return []
  }
})()

/**
 * The main section of a trigger: "When" (a visible choice of trigger types, each with an icon and a one-line
 * explanation), the form of the chosen type built on the shared widgets, and what happens when it triggers again
 * while running. Changing the type starts the new type from its defaults (see `retypeTrigger`).
 */
export function TriggerConfig(props: {
  node: GraphNode
  onChange: Change
  /** A new type: its config and the node's label, as one change. */
  onRetype?(config: Config, label: string | undefined): void
  diagnostics?: VisualDiagnostic[]
}) {
  const hass = useHass()
  const [discovered, setDiscovered] = createSignal<Record<string, unknown>>({})
  const [discoveryError, setDiscoveryError] = createSignal('')
  const [choosing, setChoosing] = createSignal(false)
  let unsubscribe: (() => void) | undefined, disposed = false
  onMount(async () => {
    if (!hass?.connection?.subscribeMessage) return
    try {
      const stop = await hass.connection.subscribeMessage<Record<string, unknown>>(
        event => setDiscovered(current => ({ ...current, ...event })),
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
  const errors = (key: string) => props.diagnostics?.filter(d => d.nodeId === props.node.id && d.severity === 3 && (d.inputId === key || d.portId === key)).map(d => d.message) ?? []
  const type = () => (typeof config().type === 'string' ? (config().type as string) : 'entity')
  const declaration = () => (record(config().trigger) ? (config().trigger as Record<string, unknown>) : {})
  const platformKey = () => platformKeyOf(declaration())
  const platform = () => declaredPlatform(declaration())
  const choice = () => triggerChoice(config())
  const current = () => triggerChoices.find(c => c.id === choice()) ?? triggerChoices[0]
  const set = (key: string, value: unknown, coalesce?: string) => {
    const next = { ...config() }
    if (value === undefined) delete next[key]
    else next[key] = value
    props.onChange(next, coalesce ? { coalesce } : undefined)
  }
  /** One key of the Home Assistant declaration; unknown keys are kept. */
  const setNative = (key: string, value: unknown) => {
    const next: Record<string, unknown> = { ...(Object.hasOwn(declaration(), platformKey()) ? {} : { trigger: platform() }), ...declaration() }
    if (value === undefined || value === '') delete next[key]
    else next[key] = value
    props.onChange({ ...config(), trigger: next })
  }
  const setOption = (key: string, value: unknown) => {
    const options = record(declaration().options) ? { ...(declaration().options as Record<string, unknown>) } : {}
    if (value === undefined || value === '') delete options[key]
    else options[key] = value
    setNative('options', Object.keys(options).length ? options : undefined)
  }
  // The type list is a radio group: arrow keys, Home and End move through it without choosing; Enter, Space or a
  // click chooses; Escape closes it unchanged. Focus then returns to "Change".
  let changeButton!: HTMLButtonElement
  let choices: HTMLDivElement | undefined
  const [pending, setPending] = createSignal('')
  const radio = (id: string) => choices?.querySelector<HTMLInputElement>(`input[value="${id}"]`)
  const openChoices = () => {
    setPending(choice())
    setChoosing(true)
    queueMicrotask(() => radio(pending())?.focus())
  }
  const closeChoices = () => {
    setChoosing(false)
    queueMicrotask(() => changeButton.focus())
  }
  const pick = (id: string) => {
    if (!choosing()) return
    choose(id)
    queueMicrotask(() => changeButton.focus())
  }
  const choicesKey = (event: KeyboardEvent) => {
    const ids = triggerChoices.map(c => c.id)
    const at = Math.max(0, ids.indexOf(pending()))
    const move = { ArrowDown: at + 1, ArrowRight: at + 1, ArrowUp: at - 1, ArrowLeft: at - 1, Home: 0, End: ids.length - 1 }[event.key]
    if (move !== undefined) {
      event.preventDefault()
      const next = ids[(move + ids.length) % ids.length]
      setPending(next)
      radio(next)?.focus()
    } else if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      pick(pending())
    } else if (event.key === 'Escape') {
      event.preventDefault()
      event.stopPropagation()
      closeChoices()
    }
  }
  const choose = (id: string) => {
    setChoosing(false)
    if (id === choice()) return
    const next = retypeTrigger(config(), id)
    const label = retypedLabel(props.node.label, choice(), id)
    // The config and the label change in one step, so one Undo restores both.
    if (props.onRetype) props.onRetype(next, label)
    else props.onChange(next)
  }
  const value = (key: string) => declaration()[key]
  const field = (key: string, label: string, widget: Widget, extra: { hint?: string; required?: boolean; map?: (v: JsonValue | undefined) => unknown; context?: { targetEntityIds?: string[] } } = {}) => (
    <ValueField label={label} hint={extra.hint} widget={widget} value={value(key)} required={extra.required} context={extra.context}
      onChange={v => setNative(key, extra.map ? extra.map(v) : v)} />
  )
  const entityIds = () => { const ids = value('entity_id'); return (Array.isArray(ids) ? ids : [ids]).filter((id): id is string => typeof id === 'string') }
  const description = () => { const d = discovered()[platform()]; return record(d) ? d : undefined }
  const schemaFields = () => {
    const fields = description()?.fields
    return record(fields) ? Object.entries(fields).filter(([, f]) => record(f)) as [string, Record<string, unknown>][] : []
  }
  const options = () => (record(declaration().options) ? (declaration().options as Record<string, unknown>) : {})
  const fieldSpec = (name: string, f: Record<string, unknown>): InputSpec => ({
    types: ['string', 'number', 'boolean', 'object', 'array'], required: f.required === true, description: typeof f.description === 'string' ? f.description : '',
    ...(record(f.selector) ? { selector: f.selector } : {}), label: typeof f.name === 'string' ? f.name : domainWords(name),
  })

  return (
    <div class="hs-insp-trigger">
      <div class="hs-when">
        <span class="hs-insp-field__label" id={`${props.node.id}-when`}>When</span>
        <div class="hs-when__current">
          <span class="hs-when__icon"><Glyph paths={current().icon} /></span>
          <span class="hs-when__text"><strong>{current().name}</strong><small>{current().explanation}</small></span>
          <button ref={changeButton} type="button" class="hs-button hs-button--ghost hs-when__change" aria-expanded={choosing()} aria-label="Change trigger type"
            onClick={() => (choosing() ? closeChoices() : openChoices())}>{choosing() ? 'Close' : 'Change'}</button>
        </div>
        <Show when={choosing()}>
          <div ref={choices} class="hs-when__choices" role="radiogroup" aria-label="Trigger type" onKeyDown={choicesKey}>
            <For each={triggerChoices}>
              {item => (
                <label class="hs-when__choice" classList={{ 'hs-when__choice--current': item.id === choice() }} onClick={() => pick(item.id)}>
                  <input type="radio" name={`${props.node.id}-trigger-type`} value={item.id} checked={item.id === pending()} onChange={() => setPending(item.id)} />
                  <span class="hs-when__icon"><Glyph paths={item.icon} /></span>
                  <span class="hs-when__text"><strong>{item.name}</strong><small>{item.explanation}</small></span>
                </label>
              )}
            </For>
          </div>
        </Show>
      </div>

      <Show when={type() === 'entity'}>
        <ValueField label="Entity" inputId="entity" widget={entityWidget(false)} value={config().entity} required errors={errors('entity')}
          onChange={v => set('entity', typeof v === 'string' ? v : undefined)} />
      </Show>
      <Show when={type() === 'daily'}>
        <ValueField label="Time of day" inputId="time" widget={{ kind: 'time', multiple: false, step: 60 }} value={config().time} required errors={errors('time')}
          onChange={v => set('time', timeOfDay(v))} />
        <ValueField label="Time zone" inputId="timeZone" widget={{ ...text, options: timeZones.map(zone => ({ value: zone, label: zone })) }} value={config().timeZone}
          required errors={errors('timeZone')} hint="For example Europe/Zagreb or UTC." onChange={v => set('timeZone', v)} />
      </Show>
      <Show when={type() === 'once'}>
        <ValueField label="Date and time" inputId="at" widget={text} value={config().at} required errors={errors('at')}
          hint="With its offset from UTC, for example 2026-12-01T07:30:00+01:00." onChange={v => set('at', v)} />
      </Show>
      <Show when={type() === 'ha'}>
        <div class="hs-insp-trigger__form" data-input-id="trigger" aria-invalid={errors('trigger').length > 0 || undefined}>
          <Show when={choice() === 'other'}>
            <ValueField label="Trigger platform" widget={{ ...text, options: Object.keys(discovered()).sort().map(id => ({ value: id, label: id })) }}
              value={platform() || undefined} required hint="Any trigger platform installed in Home Assistant."
              onChange={v => setNative(platformKey(), typeof v === 'string' ? v : '')} />
            <Show when={record(description()?.target)}>
              <ValueField label="Target" widget={{ kind: 'target', multiple: false, filter: { target: description()!.target } }} value={value('target')}
                onChange={v => setNative('target', v)} />
            </Show>
            <For each={schemaFields()}>
              {([name, f]) => {
                const spec = fieldSpec(name, f)
                return <ValueField label={spec.label!} hint={spec.description || undefined} required={spec.required} value={options()[name]}
                  widget={widgetFor(spec)} onChange={v => setOption(name, v)} />
              }}
            </For>
            <Show when={discoveryError()}>
              <p class="hs-insp-note" role="note">Home Assistant did not list its trigger platforms ({discoveryError()}). Enter the platform and edit the trigger as JSON under Advanced.</p>
            </Show>
          </Show>
          <Show when={choice() === 'state'}>
            {field('entity_id', 'Entity', entityWidget(true), { required: true, map: entityValue })}
            {field('attribute', 'Attribute', text, { hint: 'Leave empty to watch the state itself.' })}
            {field('from', 'From', stateField, { context: { targetEntityIds: entityIds() }, hint: 'Leave empty for any state.' })}
            {field('to', 'To', stateField, { context: { targetEntityIds: entityIds() }, hint: 'Leave empty for any state.' })}
            {field('for', 'For', { kind: 'duration', multiple: false, objectForm: true }, { hint: 'How long the new state must hold first.' })}
          </Show>
          <Show when={choice() === 'numeric_state'}>
            {field('entity_id', 'Entity', entityWidget(true), { required: true, map: entityValue })}
            {field('attribute', 'Attribute', text, { hint: 'Leave empty to compare the state itself.' })}
            {field('above', 'Above', text, { map: numberOrText, hint: 'A number, or an entity holding one.' })}
            {field('below', 'Below', text, { map: numberOrText, hint: 'A number, or an entity holding one.' })}
            {field('for', 'For', { kind: 'duration', multiple: false, objectForm: true }, { hint: 'How long the value must stay there first.' })}
          </Show>
          <Show when={choice() === 'time'}>
            {field('at', 'At', typeof value('at') === 'string' && /^\d/.test(value('at') as string) || value('at') === undefined ? { kind: 'time', multiple: false } : entityWidget(true, ['input_datetime', 'sensor']), {
              required: true, map: v => (Array.isArray(v) ? entityValue(v) : v), hint: 'A time of day, or a date-time helper or timestamp sensor.' })}
          </Show>
          <Show when={choice() === 'time_pattern'}>
            {field('hours', 'Hours', text, { hint: 'A number, or /2 for every 2 hours.' })}
            {field('minutes', 'Minutes', text, { hint: 'A number, or /5 for every 5 minutes.' })}
            {field('seconds', 'Seconds', text, { hint: 'A number, or /30 for every 30 seconds.' })}
          </Show>
          <Show when={choice() === 'sun'}>
            {field('event', 'Sun event', { kind: 'select', multiple: false, options: [{ value: 'sunrise', label: 'Sunrise' }, { value: 'sunset', label: 'Sunset' }] }, { required: true })}
            {field('offset', 'Offset', text, { hint: 'Earlier or later, for example -00:30:00 for half an hour before.' })}
          </Show>
          <Show when={choice() === 'zone'}>
            {field('entity_id', 'Who', entityWidget(true, ['person', 'device_tracker']), { required: true, map: entityValue })}
            {field('zone', 'Zone', entityWidget(false, ['zone']), { required: true })}
            {field('event', 'When they', { kind: 'select', multiple: false, options: [{ value: 'enter', label: 'Enter' }, { value: 'leave', label: 'Leave' }] }, { required: true })}
          </Show>
          <Show when={choice() === 'event'}>
            {field('event_type', 'Event type', text, { required: true })}
            {field('event_data', 'Event data', { kind: 'json', multiple: false, structured: true }, { hint: 'Only events whose data contains these values.' })}
          </Show>
          <Show when={errors('trigger').length}>
            <ul class="hs-input-editor__errors"><For each={errors('trigger')}>{m => <li>{m}</li>}</For></ul>
          </Show>
        </div>
      </Show>
      <Show when={type() === 'manual'}><p class="hs-muted">Use Run now, or start it from another automation.</p></Show>
      <Show when={type() === 'startup'}><p class="hs-muted">Runs once each time Home Assistant starts.</p></Show>

      <fieldset class="hs-insp-modes">
        <legend class="hs-insp-field__label">If it triggers again while running</legend>
        <For each={runModes}>
          {([mode, name, explanation]) => (
            <label class="hs-insp-mode">
              <input type="radio" name={`${props.node.id}-mode`} value={mode} checked={(config().mode ?? 'Single') === mode} onChange={() => set('mode', mode)} />
              <span><strong>{name}</strong><small>{explanation}</small></span>
            </label>
          )}
        </For>
      </fieldset>
    </div>
  )
}

/**
 * The rarely needed trigger settings, shown in the node's Advanced section: manual runs, startup, C# subscription
 * expressions and, for Home Assistant triggers, the whole declaration as JSON.
 */
export function TriggerAdvanced(props: { node: GraphNode; onChange: Change }) {
  const config = () => props.node.config
  const set = (key: string, value: unknown) => {
    const next = { ...config() }
    if (value === undefined || value === '') delete next[key]
    else next[key] = value
    props.onChange(next)
  }
  const id = createUniqueId()
  return (
    <>
      <label class="hs-insp-toggle">
        <input type="checkbox" role="switch" class="hs-switch" checked={config().manual !== false} onChange={e => set('manual', e.currentTarget.checked)} />
        <span><strong>Can be started with Run now</strong><small class="hs-field-hint">Turn off to hide it from Run now.</small></span>
      </label>
      <div class="hs-insp-field">
        <label class="hs-insp-field__label" for={`${id}-startup`}>Run when Home Assistant starts</label>
        <select id={`${id}-startup`} class="hs-input"
          onChange={e => set('startup', e.currentTarget.value === '' ? undefined : e.currentTarget.value === 'true')}>
          <option value="" selected={config().startup == null}>Only for a startup trigger</option>
          <option value="true" selected={config().startup === true}>Yes, also at startup</option>
          <option value="false" selected={config().startup === false}>No, never at startup</option>
        </select>
      </div>
      <Show when={config().type === 'ha'}>
        <ValueField label="Trigger as JSON" widget={{ kind: 'json', multiple: false, structured: true }} value={config().trigger}
          hint="Home Assistant checks this declaration; keys the form does not show are kept." onChange={v => set('trigger', v)} />
      </Show>
    </>
  )
}
