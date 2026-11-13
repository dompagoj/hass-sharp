import { nodeCatalog, refinesType, resolveTypeSpec, rootType, validLiteral, type InputSpec, type JsonValue, type Operand, type VisualCatalogSnapshot } from '../contract'

/** Contract C3: the control an input's literal is edited with. `none` is a wire-only input (no literal type). */
export type WidgetKind =
  | 'toggle' | 'number' | 'text' | 'select' | 'constant' | 'entity' | 'state' | 'device' | 'area' | 'floor' | 'label'
  | 'target' | 'duration' | 'time' | 'date' | 'datetime' | 'color' | 'json' | 'none'
export interface WidgetOption { value: JsonValue; label: string }
export interface Widget {
  kind: WidgetKind
  /** The literal is a list of items (selector `multiple`). */
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
  /** state: entities named by the selector itself, states it hides, and the attribute it reads instead of the state. */
  stateEntities?: string[]
  hideStates?: string[]
  stateAttribute?: string
  /** duration: a days field. */
  days?: boolean
}

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const num = (value: unknown) => typeof value === 'number' && Number.isFinite(value) ? value : undefined
const str = (value: unknown) => typeof value === 'string' && value !== '' ? value : undefined
const strings = (value: unknown) => (Array.isArray(value) ? value : value === undefined ? [] : [value]).filter((v): v is string => typeof v === 'string' && v !== '')
/** Human text of a JSON value: strings as they are, anything else as JSON. */
export const displayValue = (value: JsonValue | undefined) => value === undefined ? '' : typeof value === 'string' ? value : JSON.stringify(value)
export const jsonEqual = (a: unknown, b: unknown): boolean => a === b || (a !== null && b !== null && typeof a === 'object' && typeof b === 'object' && JSON.stringify(a) === JSON.stringify(b))

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
        const mired = config.unit === 'mired'
        return { kind: 'number', multiple: false, slider: true, step: input.step ?? 1, unit: mired ? 'mired' : 'K',
          min: input.min ?? num(config.min) ?? num(config.min_mireds) ?? (mired ? 153 : 2000),
          max: input.max ?? num(config.max) ?? num(config.max_mireds) ?? (mired ? 500 : 6500) }
      }
      case 'text': return { kind: 'text', multiple, multiline: config.multiline === true }
      case 'select': return config.custom_value === true
        ? { kind: 'text', multiple, options: options({ ...input, enum: undefined }, config) }
        : { kind: 'select', multiple, options: options(input, config) }
      case 'entity': return { kind: 'entity', multiple, domains: entityDomains(input.types, catalog), filter: input.selector }
      case 'state': return { kind: 'state', multiple, stateEntities: strings(config.entity_id), hideStates: strings(config.hide_states), ...(str(config.attribute) ? { stateAttribute: str(config.attribute) } : {}) }
      case 'device': case 'area': case 'floor': case 'label': return { kind: name, multiple }
      case 'target': return { kind: 'target', multiple: false, domains: entityDomains(input.types, catalog), filter: input.selector }
      case 'duration': return { kind: 'duration', multiple: false, days: config.enable_day === true }
      case 'time': case 'date': case 'datetime': return { kind: name, multiple: false }
      case 'color_rgb': return { kind: 'color', multiple: false }
      case 'constant': return { kind: 'constant', multiple: false, options: options(input, { options: [{ value: config.value, label: config.label }] }).slice(0, 1) }
      default: return { kind: 'json', multiple: false }
    }
  }
  if (input.enum?.length) return { kind: 'select', multiple: false, options: options(input, undefined) }
  const literal = input.types.filter(t => resolveTypeSpec(t, catalog)?.literalKind)
  if (!literal.length) return { kind: 'none', multiple: false }
  // A target, optionally also accepting a bare entity id (the service `target` input); `$any` stays JSON.
  if (literal.some(t => refinesType(t, 'target', catalog)) && literal.every(t => refinesType(t, 'target', catalog) || refinesType(t, 'entity_id', catalog))) return { kind: 'target', multiple: false, domains: entityDomains(input.types, catalog) }
  const kinds = new Set(literal.map(t => resolveTypeSpec(t, catalog)!.literalKind))
  if (kinds.size !== 1) return { kind: 'json', multiple: false }
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

/** The value a newly chosen "Value" mode starts with, or undefined when the user must enter one first. */
export function initialValue(widget: Widget, input: InputSpec): JsonValue | undefined {
  const fallback = input.default
  if (record(fallback) && fallback.kind === 'literal' && fallback.value !== undefined) return structuredClone(fallback.value as JsonValue)
  switch (widget.kind) {
    case 'toggle': return false
    case 'number': return Math.min(Math.max(0, widget.min ?? 0), widget.max ?? Number.POSITIVE_INFINITY)
    case 'select': case 'constant': return widget.multiple ? undefined : widget.options?.[0]?.value
    case 'color': return [255, 255, 255]
    case 'duration': return 0
    default: return undefined
  }
}

/** Whether a stored literal value has the shape the widget edits; anything else is shown in the JSON editor. */
export function fitsWidget(widget: Widget, value: JsonValue | undefined): boolean {
  if (value === undefined || widget.kind === 'json' || widget.kind === 'select' || widget.kind === 'constant') return true
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
