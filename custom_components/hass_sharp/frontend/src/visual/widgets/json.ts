import type { JsonValue } from '../contract'

/** The shared value helpers of the widgets and the input editor. */
export const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
/** A string, or the strings of a list; empty strings and other items are dropped. */
export const strings = (value: unknown): string[] =>
  (Array.isArray(value) ? value : value === undefined || value === null ? [] : [value]).filter((v): v is string => typeof v === 'string' && v !== '')
/** Structural JSON equality; object key order does not matter (as in the validator). */
export const jsonEqual = (a: unknown, b: unknown): boolean =>
  a === b || (Array.isArray(a) && Array.isArray(b) ? a.length === b.length && a.every((v, i) => jsonEqual(v, b[i])) :
    record(a) && record(b) && Object.keys(a).length === Object.keys(b).length && Object.keys(a).every(k => Object.hasOwn(b, k) && jsonEqual(a[k], b[k])))
/** Human text of a JSON value: strings as they are, anything else as JSON. */
export const displayValue = (value: JsonValue | undefined) => value === undefined ? '' : typeof value === 'string' ? value : JSON.stringify(value)
