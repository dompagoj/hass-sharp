import type { JsonValue } from '../contract'
import { displayValue, optionWords } from './json'
import type { Widget, WidgetOption } from './model'

/** An entity as far as its states go (a registry entity carries more). */
export interface StateEntity {
  readonly entity_id: string
  readonly state_options?: readonly string[] | null
}

/**
 * Contract C3 "state": the union of the `state_options` of these entities, in registry order, without the hidden
 * states. What the state picker offers, and what a card or the trace view reads a state with.
 */
export function stateOptions(entities: readonly StateEntity[] | undefined, ids: Iterable<string>, hidden: readonly string[] = []): string[] {
  const wanted = new Set(ids), skip = new Set(hidden), found: string[] = []
  if (!wanted.size) return found
  for (const entity of entities ?? []) if (wanted.has(entity.entity_id))
    for (const state of entity.state_options ?? []) if (!skip.has(state) && !found.includes(state)) found.push(state)
  return found
}

/**
 * How a state reads where it is not being picked: with its entity's states known, the label the picker gives it (or,
 * for a state the entity does not have, the value as stored); with none known, the value in words when it looks like
 * an identifier ("heat_cool" → "Heat cool"). The stored value is never changed.
 */
export const stateWords = (value: JsonValue, states: readonly string[]): string =>
  states.length && !(typeof value === 'string' && states.includes(value)) ? displayValue(value) : optionWords(value)

/**
 * The state picker of a setting that is not a service field (a State condition's state): the panel offers its states
 * with this widget, and a card's summary reads them by the same rule (also for `stateSuggestions`, which offers the same states).
 */
export const stateField: Widget = { kind: 'state', multiple: false }

/**
 * A state that may be left empty or be one the entity does not list (`unavailable`): free text that suggests the
 * known states (a state trigger's from and to).
 */
export const stateSuggestions: Widget = { ...stateField, freeText: true }

/** The states a state widget offers for these entities: the rule of its picker, its card chip and its trace chip. */
export function stateWidgetOptions(widget: Widget, ids: readonly string[], entities: readonly StateEntity[] | undefined): WidgetOption[] {
  if (widget.stateAttribute) return []
  // Choose the source before hiding states: hiding all known registry states must not revive fallback choices.
  const known = stateOptions(entities, [...ids, ...(widget.stateEntities ?? [])])
  const options = known.length ? known.map(value => ({ value, label: optionWords(value) })) : widget.fallbackStateOptions ?? []
  const hidden = new Set(widget.hideStates), seen = new Set<string>()
  return options.filter(option => {
    if (typeof option.value !== 'string' || hidden.has(option.value) || seen.has(option.value)) return false
    seen.add(option.value)
    return true
  })
}

export const widgetStates = (widget: Widget, ids: readonly string[], entities: readonly StateEntity[] | undefined): string[] =>
  stateWidgetOptions(widget, ids, entities).map(option => option.value as string)

/** How a state reads where it is not being picked, for a state widget and the entities its step acts on. */
export function stateLabel(value: JsonValue, widget: Widget, ids: readonly string[], entities: readonly StateEntity[] | undefined): string {
  const options = stateWidgetOptions(widget, ids, entities)
  return options.find(option => option.value === value)?.label ?? stateWords(value, options.map(option => option.value as string))
}
