import type { JsonValue } from '../contract'
import { displayValue, optionWords } from './json'

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
