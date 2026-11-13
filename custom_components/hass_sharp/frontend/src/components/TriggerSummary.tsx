import { For, Show } from 'solid-js'
import type { RunTrigger } from '../types'

const capitalise = (text: string) => text.charAt(0).toUpperCase() + text.slice(1)
const localTime = (iso: string) => new Date(iso).toLocaleString()
// A serialiser may write absent fields as null; treat both the same.
const change = (trigger: RunTrigger) => trigger.from == null && trigger.to == null
  ? trigger.entityId!
  : `${trigger.entityId}: ${trigger.from ?? '—'} → ${trigger.to ?? '—'}`

/** One line in human words: what fired the run. */
export function summarizeTrigger(trigger: RunTrigger): string {
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
    default: return trigger.description ? capitalise(trigger.description) : 'Trigger'
  }
}

const fields: [keyof RunTrigger, string][] = [
  ['platform', 'Platform'], ['entityId', 'Entity'], ['from', 'From'], ['to', 'To'], ['eventType', 'Event type'],
  ['scheduleId', 'Schedule'], ['planned', 'Planned'], ['id', 'Trigger ID'], ['description', 'Description'],
]

const shown = (trigger: RunTrigger, key: keyof RunTrigger): boolean => trigger[key] != null && trigger[key] !== ''
  // The description is already the headline when nothing more specific was recorded.
  && (key !== 'description' || shown(trigger, 'entityId') || shown(trigger, 'eventType'))

/** Structured trigger summary of a run, without the raw payload. */
export const TriggerSummary = (props: { trigger: RunTrigger | null | undefined }) =>
  <Show when={props.trigger} fallback={<p class="hs-muted">No trigger summary was recorded for this run.</p>}>{trigger => <div class="hs-trigger-summary">
    <p class="hs-trigger-headline">{summarizeTrigger(trigger())}</p>
    <dl class="hs-run-fields">
      <For each={fields.filter(([key]) => shown(trigger(), key))}>{([key, label]) => <div>
        <dt>{label}</dt>
        <dd>{key === 'planned'
          ? <time dateTime={trigger().planned} title={trigger().planned}>{localTime(trigger().planned!)}</time>
          : key === 'entityId' || key === 'scheduleId' || key === 'eventType' ? <code>{trigger()[key]}</code> : trigger()[key]}</dd>
      </div>}</For>
    </dl>
  </div>}</Show>
