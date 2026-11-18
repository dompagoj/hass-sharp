import { For, Show } from 'solid-js'
import type { RunTrigger } from '../types'
import type { EntityDomainSpec, Graph, VisualCatalogSnapshot } from '../visual/contract'

/**
 * What names an entity definition's command runs: its entities (each by key, with its name and type) and the commands
 * of each type (the catalog's `entityDomains`). Without it a command run reads by the entity's key and the raw command.
 */
export interface TriggerNames {
  entities?: readonly { key: string; name?: string | null; domain?: string | null }[]
  domains?: Record<string, EntityDomainSpec>
}
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
/** The names of a graph's definition (an entity definition's graph), with the catalog's entity types; none for an automation's. */
export function definitionNames(graph: Graph | undefined, catalog: VisualCatalogSnapshot | undefined): TriggerNames | undefined {
  const definition = graph?.definition as unknown
  if (!record(definition) || !Array.isArray(definition.entities)) return
  const entities = definition.entities.filter(record).filter((entity): entity is { key: string } => typeof entity.key === 'string')
  return { entities, domains: catalog?.entityDomains }
}
/** "Main relay: Turn on": the entity by its name (else its key), the command by its type's label (else in words). */
const commanded = (trigger: RunTrigger, names?: TriggerNames) => {
  const key = trigger.entity ?? '', command = trigger.command ?? ''
  const entity = names?.entities?.find(e => e.key === key)
  const commands = entity?.domain ? names?.domains?.[entity.domain]?.commands : undefined
  const label = commands && Object.hasOwn(commands, command) && typeof commands[command]?.label === 'string' ? commands[command].label : ''
  return `${entity?.name || key}: ${label || command.replace(/_/g, ' ')}`
}

const capitalise = (text: string) => text.charAt(0).toUpperCase() + text.slice(1)
const localTime = (iso: string) => new Date(iso).toLocaleString()
// A serialiser may write absent fields as null; treat both the same.
const change = (trigger: RunTrigger) => trigger.from == null && trigger.to == null
  ? trigger.entityId!
  : `${trigger.entityId}: ${trigger.from ?? '—'} → ${trigger.to ?? '—'}`

/** One line in human words: what fired the run. */
export function summarizeTrigger(trigger: RunTrigger, names?: TriggerNames): string {
  switch (trigger.kind) {
    case 'entity': return trigger.entityId ? change(trigger) : 'Entity changed'
    case 'ha':
      if (trigger.entityId) return change(trigger)
      if (trigger.eventType) return `Event ${trigger.eventType}`
      if (trigger.description) return capitalise(trigger.description)
      return trigger.platform ? `${capitalise(trigger.platform)} trigger` : 'Home Assistant trigger'
    case 'schedule': return trigger.planned ? `Scheduled for ${localTime(trigger.planned)}` : 'Scheduled run'
    case 'manual': return 'Started manually'
    case 'startup': return 'Started with Home Assistant'
    case 'command': return trigger.entity || trigger.command ? commanded(trigger, names) : 'Entity commanded'
    case 'setup': return 'Definition started'
    default: return trigger.description ? capitalise(trigger.description) : 'Trigger'
  }
}

const fields: [keyof RunTrigger, string][] = [
  ['platform', 'Platform'], ['entityId', 'Entity'], ['from', 'From'], ['to', 'To'], ['eventType', 'Event type'],
  ['scheduleId', 'Schedule'], ['planned', 'Planned'], ['id', 'Trigger ID'], ['description', 'Description'],
  ['entity', 'Entity'], ['command', 'Command'],
]

const shown = (trigger: RunTrigger, key: keyof RunTrigger): boolean => trigger[key] != null && trigger[key] !== ''
  // The description is already the headline when nothing more specific was recorded.
  && (key !== 'description' || shown(trigger, 'entityId') || shown(trigger, 'eventType'))

/** Structured trigger summary of a run, without the raw payload. */
export const TriggerSummary = (props: { trigger: RunTrigger | null | undefined; names?: TriggerNames }) =>
  <Show when={props.trigger} fallback={<p class="hs-muted">No trigger summary was recorded for this run.</p>}>{trigger => <div class="hs-trigger-summary">
    <p class="hs-trigger-headline">{summarizeTrigger(trigger(), props.names)}</p>
    <dl class="hs-run-fields">
      <For each={fields.filter(([key]) => shown(trigger(), key))}>{([key, label]) => <div>
        <dt>{label}</dt>
        <dd>{key === 'planned'
          ? <time dateTime={trigger().planned} title={trigger().planned}>{localTime(trigger().planned!)}</time>
          : key === 'entityId' || key === 'scheduleId' || key === 'eventType' || key === 'entity' || key === 'command' ? <code>{trigger()[key]}</code> : trigger()[key]}</dd>
      </div>}</For>
    </dl>
  </div>}</Show>
