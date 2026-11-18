import { createMemo, createUniqueId, For, Show } from 'solid-js'
import { A } from '@solidjs/router'
import { Menu } from '../components/Menu'
import { EnabledSwitch } from '../automations/EnabledSwitch'
import { pollInterval } from '../automations/model'
import { RunDots } from '../automations/RunDots'
import { plural } from '../utils'
import { entityTypeLabel, nodeCatalog } from '../visual/contract'
import { Glyph } from '../visual/Glyph'
import { icons } from '../visual/icons'
import { entityTexts } from './adapter'
import type { LiveStates } from './liveState'
import type { HomeDefinition } from './model'

/** The switch's tooltips: the editor's, so the two say the same. */
const switchTitles = { on: entityTexts.enabledOn, off: entityTexts.enabledOff }
/** What the backend says of a definition with nothing loaded and no error of its own. */
const notActive = 'Not active'
/** Beside "Class" when the classes could not be read. */
const classNameFailed = 'Could not load the class’s name.'
/** How many of a definition's entities its card lists before "and N more". */
export const shownEntities = 6

export type DefinitionTone = 'running' | 'starting' | 'failed' | 'stopped' | 'off'
/**
 * A definition's status in words: Off (turned off: no error), Running, Starting, Failed (it could not be prepared, or
 * its start threw and its entities exist and are unavailable) or Not running (on, and not loaded for no reason the
 * backend recorded), with the reason when there is one; and "Changes not applied" while an older revision runs.
 */
export function definitionStatus(item: Pick<HomeDefinition, 'enabled' | 'activationError' | 'status' | 'currentRevisionActive'>): { label: string; tone: DefinitionTone; error?: string; stale?: string } {
  if (!item.enabled) return { label: 'Off', tone: 'off' }
  const error = item.activationError && item.activationError !== notActive ? item.activationError : undefined
  if (item.status === 'failed') return { label: 'Failed', tone: 'failed', error }
  if (item.status === 'off') return { label: 'Not running', tone: 'stopped', error }
  if (item.status === 'setup') return { label: 'Starting', tone: 'starting' }
  if (item.currentRevisionActive) return { label: 'Running', tone: 'running' }
  return { label: 'Running', tone: 'running', stale: error ? `Changes not applied: ${error}` : 'Changes not applied' }
}
/** The card's tint: a problem (on and not running), a warning (an older revision runs), or none. */
const tint = (status: ReturnType<typeof definitionStatus>) =>
  status.tone === 'failed' || status.tone === 'stopped' ? 'attention' : status.stale ? 'warning' : undefined

/** An entity of a definition as its card lists it. */
export interface CardEntity { key: string; name: string; domain: string; entityId: string | null; disabled: boolean }
const collator = new Intl.Collator(undefined, { sensitivity: 'base', numeric: true })
/**
 * A definition's entities: those it registered while running (or failed), else Home Assistant's entity registry's,
 * which keeps them while it is off or not loaded; a class definition that never started has neither. Whether Home
 * Assistant has one disabled always comes from the registry. Each is named by its name (its own, else the one the
 * definition gives it), else its key, else its entity id. Enabled ones come first, each group by name.
 */
export function cardEntities(item: Pick<HomeDefinition, 'id' | 'entities' | 'liveEntities' | 'registeredEntities'>): CardEntity[] {
  const declared = new Map((item.entities ?? []).map(entity => [entity.key, entity.name]))
  const registry = new Map(item.registeredEntities.map(entry => [entry.uniqueId, entry]))
  const entity = (key: string, name: string | null | undefined, domain: string, entityId: string | null, disabled: boolean): CardEntity =>
    ({ key, name: name || declared.get(key) || key || entityId || '', domain, entityId, disabled })
  const prefix = `${item.id}:`
  const listed = item.liveEntities.length
    ? item.liveEntities.map(e => entity(e.key, e.name, e.domain, e.entityId, registry.get(e.uniqueId)?.disabled === true))
    : item.registeredEntities.map(e => {
      const key = e.uniqueId.startsWith(prefix) ? e.uniqueId.slice(prefix.length) : e.uniqueId.slice(e.uniqueId.indexOf(':') + 1)
      return entity(key, e.name, e.domain, e.entityId, e.disabled)
    })
  return listed.sort((a, b) => Number(a.disabled) - Number(b.disabled) || collator.compare(a.name, b.name))
}
/** The entity ids whose states a card shows: its listed entities that Home Assistant has enabled. */
export const watchedEntityIds = (item: HomeDefinition) =>
  cardEntities(item).slice(0, shownEntities).flatMap(e => (e.entityId && !e.disabled ? [e.entityId] : []))

/** An entity's state on its card: Home Assistant's text (a number with its unit), Unavailable, Unknown, or none yet. */
function stateText(entity: CardEntity, states: LiveStates): { text: string; none?: boolean; quiet?: boolean } {
  if (entity.disabled) return { text: 'Disabled in Home Assistant', quiet: true }
  const state = entity.entityId ? states[entity.entityId] : undefined
  if (!state) return { text: '—', none: true }
  if (state.state === 'unavailable') return { text: 'Unavailable', quiet: true }
  if (state.state === 'unknown') return { text: 'Unknown', quiet: true }
  const number = state.state.trim() !== '' && Number.isFinite(Number(state.state))
  return { text: number && state.unit ? `${state.state} ${state.unit}` : state.state }
}

const starting = (item: HomeDefinition) => item.enabled && item.status === 'setup'
/**
 * Keeps in `since` when the page first saw each definition starting (`now` for one new to it), and forgets those no
 * longer starting.
 */
export function noteStarting(since: Map<string, number>, items: readonly HomeDefinition[], now: number) {
  const ids = new Set(items.filter(starting).map(item => item.id))
  for (const id of since.keys()) if (!ids.has(id)) since.delete(id)
  for (const id of ids) if (!since.has(id)) since.set(id, now)
}
/**
 * How often the entities page reads the list again: as the automations page does for runs, and in the same way while a
 * definition that is on is starting: every 5 s for the first 2 minutes since the page saw it starting (`since`, from
 * {@link noteStarting}; one not in it counts from `now`), then every 30 s.
 */
export function definitionPollInterval(items: readonly HomeDefinition[], now: number, since: ReadonlyMap<string, number> = new Map()): number | false {
  const runs = pollInterval(items, now)
  const waiting = items.filter(starting)
  if (!waiting.length) return runs
  const start = waiting.some(item => now - (since.get(item.id) ?? now) < 120_000) ? 5_000 : 30_000
  return runs === false ? start : Math.min(runs, start)
}

/** "3 entity definitions · 1 off · 4 runs today": zero parts after the first are left out. */
export function definitionsSummary(items: readonly HomeDefinition[]): string {
  const off = items.filter(item => !item.enabled).length
  const today = items.reduce((sum, item) => sum + item.runsToday, 0)
  return [items.length && plural(items.length, 'entity definition'), off && `${off} off`, today && `${plural(today, 'run')} today`].filter(Boolean).join(' · ')
}

/**
 * One entity definition on the entities page. The whole card is the link to its editor (the name is the link's text;
 * how it works and its status describe it); the switch and the "⋯" menu sit above the link. Setting values are never
 * shown.
 */
export function DefinitionCard(props: {
  item: HomeDefinition
  /** A class definition's class: its title, null when no node library offers it, undefined while not known. */
  classTitle?: string | null
  /** The classes could not be read: a class definition says so beside "Class". */
  classFailed?: boolean
  states: LiveStates
  admin: boolean
  /** Deleting it: its actions wait. */
  busy?: boolean
  /** Its own switch request is in flight: the switch ignores presses but keeps focus. */
  switching?: boolean
  onToggle(enabled: boolean): void
  onOpen(): void
  onDelete(): void
}) {
  const describedBy = createUniqueId()
  const name = () => props.item.name
  const status = createMemo(() => definitionStatus(props.item))
  const entities = createMemo(() => cardEntities(props.item))
  const graph = () => props.item.logic.kind === 'graph'
  const logic = () => (graph() ? 'Nodes' : props.classTitle === null ? 'Class not found' : props.classTitle ?? 'Class')
  const unnamed = () => !graph() && props.classTitle === undefined && !!props.classFailed
  return (
    <li
      class="hs-home-card hs-ent-card"
      classList={{ 'hs-home-card--off': !props.item.enabled, 'hs-home-card--attention': tint(status()) === 'attention', 'hs-ent-card--warning': tint(status()) === 'warning' }}
      data-definition={props.item.id}
    >
      <div class="hs-home-card__body">
        <h2 class="hs-home-card__title">
          <A class="hs-home-card__link" href={`/entities/${encodeURIComponent(props.item.id)}`} aria-describedby={describedBy}>{name()}</A>
        </h2>
        <div id={describedBy} class="hs-home-card__about">
          <p class="hs-ent-card__kind">
            <span class="hs-ent-card__logic" classList={{ 'hs-ent-card__logic--missing': !graph() && props.classTitle === null }}
              title={unnamed() ? classNameFailed : undefined}>{logic()}</span>
            <Show when={unnamed()}><small class="hs-ent-card__hint">{classNameFailed}</small></Show>
            <Show when={props.item.device}><span class="hs-home-chip hs-ent-card__device">Device</span></Show>
          </p>
          <p class={`hs-ent-card__status hs-ent-card__status--${status().tone}`}>{status().label}</p>
          <Show when={status().error}>
            {error => (
              <p class="hs-home-card__health">
                <Glyph paths={icons.problem} class="hs-home-card__glyph" />
                <span class="hs-ent-card__error">{error()}</span>
              </p>
            )}
          </Show>
          <Show when={status().stale}>
            {stale => (
              <p class="hs-home-card__health">
                <Glyph paths={icons.warning} class="hs-home-card__glyph" />
                <span class="hs-ent-card__stale">{stale()}</span>
              </p>
            )}
          </Show>
        </div>
        <Show when={entities().length} fallback={<p class="hs-ent-card__none hs-muted">Its entities appear after it has started once.</p>}>
          <ul class="hs-ent-card__entities" aria-label={`Entities of ${name()}`}>
            <For each={entities().slice(0, shownEntities)}>
              {entity => {
                const state = () => stateText(entity, props.states)
                return (
                  <li class="hs-ent-row">
                    <span class="hs-ent-row__name">{entity.name}</span>
                    <span class="hs-ent-row__state" classList={{ 'hs-ent-row__state--quiet': !!state().quiet || !!state().none }}>
                      <Show when={state().none} fallback={state().text}>
                        <span aria-hidden="true">{state().text}</span><span class="hs-visually-hidden">No state</span>
                      </Show>
                    </span>
                    <span class="hs-ent-row__meta">
                      <span class="hs-ent-row__type">{entityTypeLabel(nodeCatalog, entity.domain)}</span>
                      <Show when={entity.entityId}>{id => <code class="hs-ent-row__id">{id()}</code>}</Show>
                    </span>
                  </li>
                )
              }}
            </For>
          </ul>
          <Show when={entities().length > shownEntities}>
            <p class="hs-ent-card__more">and {entities().length - shownEntities} more</p>
          </Show>
        </Show>
        <Show when={graph()}>
          <p class="hs-home-card__runs">
            <RunDots runs={props.item.recentRuns} />
            <span class="hs-ent-card__runs-today">{`${plural(props.item.runsToday, 'run')} today`}</span>
          </p>
        </Show>
      </div>
      <Show when={props.admin}>
        <div class="hs-home-card__actions">
          <EnabledSwitch name={name()} enabled={props.item.enabled} disabled={props.busy} busy={props.switching} titles={switchTitles} onChange={props.onToggle} />
          <Menu
            label={`Actions for ${name()}`}
            disabled={props.busy}
            items={[
              { label: 'Open', onSelect: props.onOpen },
              { label: 'Delete', danger: true, onSelect: props.onDelete },
            ]}
          />
        </div>
      </Show>
    </li>
  )
}
