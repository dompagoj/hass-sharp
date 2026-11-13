import { nodeCatalog, refinesType, resolveTypeSpec, rootType, validLiteral, type InputSpec, type JsonValue, type Operand, type VisualCatalogSnapshot } from '../contract'
import { displayValue, jsonEqual, record, strings } from './json'

/**
 * Contract C3: the control an input's value is edited with. `mixed` is a value of one of several simple kinds
 * chosen with a kind switch; `none` is an input that can only be connected.
 */
export type WidgetKind =
  | 'toggle' | 'number' | 'text' | 'select' | 'constant' | 'entity' | 'state' | 'device' | 'area' | 'floor' | 'label'
  | 'target' | 'duration' | 'time' | 'date' | 'datetime' | 'color' | 'mixed' | 'json' | 'none'
/** The kinds a `mixed` widget switches between: Text, Number, Yes/No, List or object. */
export type ValueKind = 'text' | 'number' | 'boolean' | 'json'
export interface WidgetOption { value: JsonValue; label: string }
export interface Widget {
  kind: WidgetKind
  /** The value is a list of items (selector `multiple`). */
  multiple: boolean
  /** number: a slider (with min and max) instead of a box. */
  slider?: boolean
  min?: number
  max?: number
  /** A widget increment only; never a validation rule (C3). */
  step?: number
  unit?: string
  /** text: several lines. */
  multiline?: boolean
  /** select / constant: the allowed values. text: suggestions for a `custom_value` select. */
  options?: WidgetOption[]
  /** entity / target: allowed entity domains from `entity_id:<d>` types; empty allows every domain. */
  domains?: string[]
  /** entity / target: the raw selector, for `entityFilter`. */
  filter?: unknown
  /** target: the input also accepts a bare entity id, so a single-entity target may stay a string. */
  entityString?: boolean
  /** state: entities named by the selector itself, states it hides, and the attribute it reads instead of the state. */
  stateEntities?: string[]
  hideStates?: string[]
  stateAttribute?: string
  /** duration: a days field. `objectForm`: a value that was not set is written as `{hours, minutes, seconds}`. */
  days?: boolean
  objectForm?: boolean
  /** mixed: the kinds the input accepts, and the kind a new value starts with. */
  kinds?: ValueKind[]
  defaultKind?: ValueKind
  /** json: only a list or an object is accepted (the "List or object" kind of a mixed value). */
  structured?: boolean
}

const num = (value: unknown) => typeof value === 'number' && Number.isFinite(value) ? value : undefined
const str = (value: unknown) => typeof value === 'string' && value !== '' ? value : undefined

/** The single selector type and its options, or undefined without a usable selector. */
function selectorOf(input: InputSpec): [string, Record<string, unknown>] | undefined {
  const keys = record(input.selector) ? Object.keys(input.selector) : []
  if (keys.length !== 1) return
  const config = input.selector![keys[0]]
  return [keys[0], record(config) ? config : {}]
}
/** Domains of the input's `entity_id:<d>` types; empty when plain `entity_id` (any domain) is accepted. */
export function entityDomains(types: string[], catalog: VisualCatalogSnapshot = nodeCatalog): string[] {
  const entity = types.filter(t => refinesType(t, 'entity_id', catalog))
  if (!entity.length || entity.includes('entity_id')) return []
  return [...new Set(entity.map(t => t.slice(t.indexOf(':') + 1)))]
}
function options(input: InputSpec, config: Record<string, unknown> | undefined): WidgetOption[] {
  if (Array.isArray(input.enum)) return input.enum.map((value, i) => ({ value, label: input.enumLabels?.[i] ?? displayValue(value) }))
  return (Array.isArray(config?.options) ? config.options : []).flatMap((option): WidgetOption[] => {
    if (record(option)) return option.value === undefined ? [] : [{ value: option.value as JsonValue, label: str(option.label) ?? displayValue(option.value as JsonValue) }]
    return [{ value: option as JsonValue, label: displayValue(option as JsonValue) }]
  })
}
function numberWidget(input: InputSpec, config: Record<string, unknown> | undefined, slider: boolean): Widget {
  const min = input.min ?? num(config?.min), max = input.max ?? num(config?.max)
  const widget: Widget = { kind: 'number', multiple: false, slider: slider && min !== undefined && max !== undefined }
  const step = input.step ?? num(config?.step) ?? (input.integer ? 1 : undefined)
  const unit = input.unit ?? str(config?.unit_of_measurement)
  return Object.assign(widget, min !== undefined && { min }, max !== undefined && { max }, step !== undefined && { step }, unit !== undefined && { unit })
}
const kindOrder: ValueKind[] = ['text', 'number', 'boolean', 'json']
/** The value kind of a type's literals, from its root (`entity_id` is text, `target` is an object). */
export function typeKind(type: string, catalog: VisualCatalogSnapshot = nodeCatalog): ValueKind | undefined {
  switch (resolveTypeSpec(rootType(type, catalog), catalog)?.literalKind) {
    case 'string': return 'text'
    case 'number': case 'integer': return 'number'
    case 'boolean': return 'boolean'
    case 'object': case 'array': return 'json'
    default: return undefined
  }
}
/** The kind of a stored value. */
export const valueKind = (value: JsonValue | undefined): ValueKind | undefined =>
  value === undefined || value === null ? undefined : typeof value === 'string' ? 'text' : typeof value === 'number' ? 'number' : typeof value === 'boolean' ? 'boolean' : 'json'
/** Several accepted kinds get a kind switch; only objects and lists get the plain JSON editor. */
function mixed(input: InputSpec, catalog: VisualCatalogSnapshot, defaultKind?: ValueKind): Widget {
  const kinds = kindOrder.filter(kind => input.types.some(t => typeKind(t, catalog) === kind))
  if (!kinds.length) return { kind: 'none', multiple: false }
  if (kinds.length === 1 && kinds[0] === 'json') return { kind: 'json', multiple: false }
  // The number kind keeps the input's range, step and unit.
  const { min, max, step, unit } = numberWidget(input, undefined, false)
  return Object.assign({ kind: 'mixed', multiple: false, kinds, defaultKind: defaultKind && kinds.includes(defaultKind) ? defaultKind : kinds.includes('text') ? 'text' : kinds[0] } as Widget,
    min !== undefined && { min }, max !== undefined && { max }, step !== undefined && { step }, unit !== undefined && { unit })
}
const acceptsKind = (input: InputSpec, kind: ValueKind, catalog: VisualCatalogSnapshot) => input.types.some(t => typeKind(t, catalog) === kind)

/**
 * Contract C3: the widget for an input, from its Home Assistant `selector` hint first, then its constraints
 * (`enum`), then the literal kinds of its accepted types. Types are resolved through `catalog` (C1).
 */
export function widgetFor(input: InputSpec, catalog: VisualCatalogSnapshot = nodeCatalog): Widget {
  const selector = selectorOf(input)
  if (selector) {
    const [name, config] = selector, multiple = config.multiple === true
    switch (name) {
      case 'boolean': return { kind: 'toggle', multiple: false }
      case 'number': return numberWidget(input, config, config.mode !== 'box')
      case 'color_temp': {
        // Home Assistant's colour temperature selector defaults to mired.
        const mired = config.unit !== 'kelvin'
        return { kind: 'number', multiple: false, slider: true, step: input.step ?? 1, unit: mired ? 'mired' : 'K',
          min: input.min ?? num(config.min) ?? (mired ? num(config.min_mireds) ?? 153 : 2000),
          max: input.max ?? num(config.max) ?? (mired ? num(config.max_mireds) ?? 500 : 6500) }
      }
      case 'text': return { kind: 'text', multiple, multiline: config.multiline === true }
      case 'template': return { kind: 'text', multiple: false, multiline: true }
      case 'select': return config.custom_value === true
        ? { kind: 'text', multiple, options: options({ ...input, enum: undefined }, config) }
        : { kind: 'select', multiple, options: options(input, config) }
      case 'entity': return { kind: 'entity', multiple, domains: entityDomains(input.types, catalog), filter: input.selector }
      case 'state': return { kind: 'state', multiple, stateEntities: strings(config.entity_id), hideStates: strings(config.hide_states), ...(str(config.attribute) ? { stateAttribute: str(config.attribute) } : {}) }
      case 'device': case 'area': case 'floor': case 'label': return { kind: name, multiple }
      case 'target': return { kind: 'target', multiple: false, domains: entityDomains(input.types, catalog), filter: input.selector, entityString: input.types.some(t => refinesType(t, 'entity_id', catalog)) }
      case 'duration': return { kind: 'duration', multiple: false, days: config.enable_day === true, objectForm: acceptsKind(input, 'json', catalog) }
      case 'time': case 'date': case 'datetime': return { kind: name, multiple: false }
      case 'color_rgb': return { kind: 'color', multiple: false }
      case 'constant': return { kind: 'constant', multiple: false, options: options(input, { options: [{ value: config.value, label: config.label }] }).slice(0, 1) }
      default: return mixed(input, catalog, 'json')
    }
  }
  if (input.enum?.length) return { kind: 'select', multiple: false, options: options(input, undefined) }
  const literal = input.types.filter(t => resolveTypeSpec(t, catalog)?.literalKind)
  if (!literal.length) return { kind: 'none', multiple: false }
  // A target, optionally also accepting a bare entity id (the service `target` input); `$any` is a mixed value.
  if (literal.some(t => refinesType(t, 'target', catalog)) && literal.every(t => refinesType(t, 'target', catalog) || refinesType(t, 'entity_id', catalog)))
    return { kind: 'target', multiple: false, domains: entityDomains(input.types, catalog), entityString: literal.some(t => refinesType(t, 'entity_id', catalog)) }
  const kinds = new Set(literal.map(t => resolveTypeSpec(t, catalog)!.literalKind))
  if (kinds.size !== 1) return mixed(input, catalog)
  switch ([...kinds][0]) {
    case 'boolean': return { kind: 'toggle', multiple: false }
    case 'number': case 'integer': return numberWidget(input, undefined, false)
    case 'string': return literal.every(t => refinesType(t, 'entity_id', catalog))
      ? { kind: 'entity', multiple: false, domains: entityDomains(literal, catalog) }
      : { kind: 'text', multiple: false }
    case 'enum': {
      const values = literal.length === 1 ? resolveTypeSpec(literal[0], catalog)!.enumValues ?? [] : []
      return values.length ? { kind: 'select', multiple: false, options: values.map(value => ({ value, label: displayValue(value) })) } : { kind: 'json', multiple: false }
    }
    default: return { kind: 'json', multiple: false }
  }
}

/** Whether a widget is light enough for a node card; anything else is summarised there and edited in the panel. */
export function inlineWidget(widget: Widget): boolean {
  switch (widget.kind) {
    case 'toggle': case 'constant': case 'number': case 'time': case 'date': case 'datetime': case 'mixed': case 'none': return true
    case 'text': return !widget.multiline && !widget.multiple
    case 'select': case 'state': case 'entity': case 'device': case 'area': case 'floor': case 'label': return !widget.multiple
    case 'duration': return !widget.days
    default: return false
  }
}

/**
 * The literal operand for an entered value: the first accepted type the value is a valid literal of, so a
 * domain-filtered input (`entity_id:light`) gets that exact type. Only when no type accepts the value does it
 * fall back to the first type of the right shape, so the validator can explain what is wrong.
 */
export function literalFor(input: InputSpec, value: JsonValue, catalog: VisualCatalogSnapshot = nodeCatalog): Extract<Operand, { kind: 'literal' }> {
  const types = input.types
  const type = types.find(t => validLiteral(t, value, catalog))
    ?? types.find(t => validLiteral(rootType(t, catalog), value, catalog))
    ?? types.find(t => resolveTypeSpec(t, catalog)?.literalKind)
    ?? types[0] ?? 'string'
  return { kind: 'literal', type, value }
}
/** The literal of a `mixed` widget: its type comes from the chosen kind, never from the text (so "01" stays text). */
export function mixedLiteral(input: InputSpec, kind: ValueKind, value: JsonValue, catalog: VisualCatalogSnapshot = nodeCatalog) {
  return literalFor({ ...input, types: input.types.filter(t => typeKind(t, catalog) === kind) }, value, catalog)
}
/** Whether a value passes the input's accepted types and its C2 literal constraints. */
export function validForInput(input: InputSpec, value: JsonValue, catalog: VisualCatalogSnapshot = nodeCatalog): boolean {
  const literal = literalFor(input, value, catalog)
  if (!validLiteral(literal.type, value, catalog)) return false
  if (typeof value === 'number' && ((input.min !== undefined && value < input.min) || (input.max !== undefined && value > input.max) || (input.integer && !Number.isInteger(value)))) return false
  if (input.enum && !input.enum.some(allowed => jsonEqual(allowed, value))) return false
  if (input.pattern && typeof value === 'string' && !new RegExp(input.pattern).test(value)) return false
  return !(Array.isArray(value) && ((input.minItems !== undefined && value.length < input.minItems) || (input.maxItems !== undefined && value.length > input.maxItems)))
}

const round = (n: number) => Math.round(n * 1000) / 1000
/** Seconds of a duration value: a number of seconds or Home Assistant's `{days, hours, minutes, seconds, milliseconds}`. */
export function durationSeconds(value: JsonValue | undefined): number | undefined {
  if (typeof value === 'number') return value
  if (!record(value)) return
  const part = (key: string) => typeof value[key] === 'number' ? value[key] as number : 0
  return part('days') * 86400 + part('hours') * 3600 + part('minutes') * 60 + part('seconds') + part('milliseconds') / 1000
}
/** The duration's parts as shown: days (when enabled), hours, minutes and seconds; seconds keep at most 3 decimals. */
export function durationParts(widget: Widget, seconds: number): Record<'days' | 'hours' | 'minutes' | 'seconds', number> {
  let rest = round(seconds)
  const days = widget.days ? Math.floor(rest / 86400) : 0; rest -= days * 86400
  const hours = Math.floor(rest / 3600); rest -= hours * 3600
  const minutes = Math.floor(rest / 60)
  return { days, hours, minutes, seconds: round(rest - minutes * 60) }
}
/**
 * A duration in the stored shape: a stored number stays seconds, a stored object stays `{hours, minutes, seconds}`
 * (with `days` only when enabled). A new value uses the object form, as Home Assistant's own selector writes it,
 * when the input accepts an object.
 */
export function durationValue(widget: Widget, previous: JsonValue | undefined, seconds: number): JsonValue {
  const object = record(previous) || (previous === undefined && widget.objectForm === true)
  if (!object) return round(seconds)
  const { days, ...rest } = durationParts(widget, seconds)
  return widget.days ? { days, ...rest } : rest
}

/** The value a newly chosen "Value" mode starts with, or undefined when the user must enter one first. */
export function initialValue(widget: Widget, input: InputSpec, catalog: VisualCatalogSnapshot = nodeCatalog): JsonValue | undefined {
  // A default is an operand for catalog inputs and service fields alike; its value is used when the input accepts it.
  const fallback = input.default
  if (record(fallback) && fallback.kind === 'literal' && fallback.value !== undefined && validForInput(input, fallback.value as JsonValue, catalog)) return structuredClone(fallback.value as JsonValue)
  switch (widget.kind) {
    case 'toggle': return false
    case 'number': return Math.min(Math.max(0, widget.min ?? 0), widget.max ?? Number.POSITIVE_INFINITY)
    case 'select': case 'constant': return widget.multiple ? undefined : widget.options?.[0]?.value
    case 'color': return [255, 255, 255]
    case 'duration': return durationValue(widget, undefined, 0)
    default: return undefined
  }
}

/** Whether a stored literal value has the shape the widget edits; anything else is shown as a mixed or JSON value. */
export function fitsWidget(widget: Widget, value: JsonValue | undefined): boolean {
  if (value === undefined || widget.kind === 'json' || widget.kind === 'mixed' || widget.kind === 'select' || widget.kind === 'constant') return true
  const single = (v: JsonValue): boolean => {
    switch (widget.kind) {
      case 'toggle': return typeof v === 'boolean'
      case 'number': return typeof v === 'number'
      case 'duration': return typeof v === 'number' || record(v)
      case 'target': return typeof v === 'string' || record(v)
      case 'color': return Array.isArray(v) && v.length === 3 && v.every(c => typeof c === 'number')
      default: return typeof v === 'string'
    }
  }
  return widget.multiple && Array.isArray(value) ? value.every(single) : single(value)
}

/** The operand a widget's commit becomes: typed by the kind switch when it gave one, else by `literalFor`. */
export function commitLiteral(input: InputSpec, value: JsonValue, options?: { kind?: ValueKind }, catalog: VisualCatalogSnapshot = nodeCatalog) {
  return options?.kind ? mixedLiteral(input, options.kind, value, catalog) : literalFor(input, value, catalog)
}
