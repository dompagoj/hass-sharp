import type { GraphDefinition, JsonValue } from '../visual/contract'
import type { VisualDiagnostic } from '../visual/api'
import type { VisualGraph } from '../visual/graph'
import { definitionEntityKinds, entityKey, entityKeyPattern, renameEntityKey, type DefinitionDevice, type DefinitionEntity, type DefinitionSetting, type EditorDefinition, type EntityDocument } from './model'

/**
 * The definition form's changes to an editor graph's `definition` (its entities, settings and device), each a new
 * graph: one undo step when committed. Nothing here changes its argument.
 */

/** The rule an entity's key follows, as the form says it. */
export const keyRule = 'Lower-case letters, digits and underscores, starting with a letter; at most 40 characters.'
/** The rule a setting's key follows, the server's (ruling R15): not blank once trimmed, and not another setting's. */
export const settingKeyRule = "Any text that is not blank and not another setting's key."
const takenKey = 'Another one already has this key; give each a key of its own.'

/** The definition the graph carries; empty lists and no device when it has none. */
export function definitionOf(graph: VisualGraph): EditorDefinition {
  const definition = (graph.definition ?? {}) as unknown as Partial<EditorDefinition>
  return { entities: definition.entities ?? [], settings: definition.settings ?? [], device: definition.device ?? null }
}
const withDefinition = (graph: VisualGraph, change: Partial<EditorDefinition>): VisualGraph =>
  ({ ...graph, definition: { ...definitionOf(graph), ...(graph.definition ?? {}), ...change } as unknown as GraphDefinition })

/** `base`, else `base_2`, `base_3`… past the keys `taken`. */
export function freeKey(base: string, taken: string[]): string {
  if (!taken.includes(base)) return base
  for (let n = 2; ; n++) if (!taken.includes(`${base}_${n}`)) return `${base}_${n}`
}

/** Why `text` cannot be the new key of an entity among `others`; undefined when it can. */
export function keyProblem(text: string, others: string[]): string | undefined {
  if (!entityKeyPattern.test(text)) return keyRule
  if (others.includes(text)) return takenKey
}

/** Why `text`, trimmed, cannot be the new key of a setting among `others` (ruling R15); undefined when it can. */
export function settingKeyProblem(text: string, others: string[]): string | undefined {
  const key = text.trim()
  if (!key) return 'Give the setting a key.'
  if (others.includes(key)) return takenKey
}

/**
 * A secret whose saved value is not here (masked; nothing typed in its place): its key cannot be renamed (ruling R16),
 * as the server keeps a saved value by the key it was saved under, and it cannot be made a plain setting.
 */
export const maskedSecret = (setting: DefinitionSetting) =>
  setting.secret === true && setting.hasValue === true && (setting.value === null || setting.value === undefined)

// ---- Entities

/** The steps that name the definition's entity `key`. */
export const stepsUsingEntity = (graph: VisualGraph, key: string) =>
  graph.nodes.filter(node => definitionEntityKinds.includes(node.kind) && node.config?.entity === key)

/** A new entity of the type `domain` with a free key, at the end. */
export function addEntity(graph: VisualGraph, domain: string): { graph: VisualGraph; key: string } {
  const entities = definitionOf(graph).entities
  const key = freeKey('entity', entities.map(e => e.key))
  return { graph: withDefinition(graph, { entities: [...entities, { key, domain }] }), key }
}

/** The definition without its entity `key`; the steps that name it stay, for the validator to name. */
export const removeEntity = (graph: VisualGraph, key: string): VisualGraph =>
  withDefinition(graph, { entities: definitionOf(graph).entities.filter(e => e.key !== key) })

/**
 * The entity `key` with `changes` made: a member given `undefined` is left out (a name, icon or device class cleared,
 * no category, enabled by default as when absent).
 */
export function updateEntity(graph: VisualGraph, key: string, changes: Partial<Record<keyof DefinitionEntity, unknown>>): VisualGraph {
  return withDefinition(graph, {
    entities: definitionOf(graph).entities.map(entity => {
      if (entity.key !== key) return entity
      const next: DefinitionEntity = { ...entity }
      for (const [member, value] of Object.entries(changes)) {
        if (value === undefined) delete next[member]
        else next[member] = value
      }
      return next
    }),
  })
}

/** The entity `key` with its option `option` set, or left out when `value` is undefined (and no `options` when none are left). */
export function setEntityOption(graph: VisualGraph, key: string, option: string, value: unknown): VisualGraph {
  const entity = definitionOf(graph).entities.find(e => e.key === key)
  if (!entity) return graph
  const options: Record<string, unknown> = { ...(entity.options ?? {}) }
  if (value === undefined) delete options[option]
  else options[option] = value
  return updateEntity(graph, key, { options: Object.keys(options).length ? options : undefined })
}

/**
 * The definition renamed to `name` before its first save: its first entity follows the name (key and name) while it
 * still has what the name gave it, so the person who never touched it gets an entity named after the definition.
 */
export function renameDefinition(graph: VisualGraph, name: string, saved: boolean): VisualGraph {
  const renamed = { ...graph, name }
  const first = definitionOf(graph).entities[0]
  if (saved || !first || first.key !== entityKey(graph.name) || (first.name ?? '') !== graph.name) return renamed
  const keyed = renameEntityKey(renamed, first.key, entityKey(name))
  return updateEntity(keyed, definitionOf(keyed).entities[0].key, { name: name || undefined })
}

// ---- Settings

/** The Setting steps that read the setting `key`. */
export const stepsUsingSetting = (graph: VisualGraph, key: string) => graph.nodes.filter(node => node.kind === 'setting' && node.config?.key === key)

/** A new text setting with a free key and no value, at the end. */
export function addSetting(graph: VisualGraph): { graph: VisualGraph; key: string } {
  const settings = definitionOf(graph).settings
  const key = freeKey('setting', settings.map(s => s.key))
  return { graph: withDefinition(graph, { settings: [...settings, { key, type: 'string', value: null }] }), key }
}

export const removeSetting = (graph: VisualGraph, key: string): VisualGraph =>
  withDefinition(graph, { settings: definitionOf(graph).settings.filter(s => s.key !== key) })

/** The setting `key` with `changes` made; a member given `undefined` is left out. */
export function updateSetting(graph: VisualGraph, key: string, changes: Partial<Record<keyof DefinitionSetting, unknown>>): VisualGraph {
  return withDefinition(graph, {
    settings: definitionOf(graph).settings.map(setting => {
      if (setting.key !== key) return setting
      const next = { ...setting } as Record<string, unknown>
      for (const [member, value] of Object.entries(changes)) {
        if (value === undefined) delete next[member]
        else next[member] = value
      }
      return next as unknown as DefinitionSetting
    }),
  })
}

/**
 * The setting renamed to `to` (trimmed), with every Setting step that reads it; the same graph when that is not a free
 * key, or the setting is a masked secret (ruling R16).
 */
export function renameSetting(graph: VisualGraph, from: string, to: string): VisualGraph {
  const settings = definitionOf(graph).settings
  const setting = settings.find(s => s.key === from)
  to = to.trim()
  if (!setting || from === to || maskedSecret(setting) || settingKeyProblem(to, settings.filter(s => s.key !== from).map(s => s.key))) return graph
  const renamed = updateSetting(graph, from, { key: to })
  return { ...renamed, nodes: renamed.nodes.map(node => (node.kind === 'setting' && node.config?.key === from ? { ...node, config: { ...node.config, key: to } } : node)) }
}

/** A setting's value as the type `type` takes it: a text, number or true/false that fits stays; anything else is no value. */
export function valueFor(type: string, value: JsonValue | undefined): JsonValue {
  if (value === null || value === undefined) return null
  if (type === 'string') return typeof value === 'string' ? value : typeof value === 'number' || typeof value === 'boolean' ? String(value) : null
  if (type === 'number') {
    const number = typeof value === 'number' ? value : typeof value === 'string' && value.trim() ? Number(value) : NaN
    return Number.isFinite(number) ? number : null
  }
  if (type === 'boolean') return typeof value === 'boolean' ? value : value === 'true' ? true : value === 'false' ? false : null
  return null
}

/**
 * A setting's type changed: its value converted when it can be (else none). A masked secret has no value here to
 * convert: it is cleared, and says so (`hasValue: false`). `removed`: a value the setting had is gone.
 */
export function retypeSetting(graph: VisualGraph, key: string, type: string): { graph: VisualGraph; removed: boolean } {
  const setting = definitionOf(graph).settings.find(s => s.key === key)
  if (!setting || (setting.type ?? 'string') === type) return { graph, removed: false }
  const masked = maskedSecret(setting)
  const value = valueFor(type, setting.value)
  const had = setting.value !== null && setting.value !== undefined
  return { graph: updateSetting(graph, key, { type, value, ...(masked ? { hasValue: false } : {}) }), removed: masked || (had && value === null) }
}

/**
 * The graph the editor goes on with once it was saved as `document`: each secret setting now holds no value here and
 * says whether one is saved, as a read of the saved definition would give it (ruling R9). Other members as sent.
 */
export function savedSecrets(sent: VisualGraph, document: Pick<EntityDocument, 'settings'>): VisualGraph {
  const settings = definitionOf(sent).settings
  if (!settings.some(s => s.secret)) return sent
  return withDefinition(sent, {
    settings: settings.map(setting => {
      if (!setting.secret) return setting
      const stored = document.settings.find(s => s.key === setting.key)
      return { ...setting, value: null, hasValue: stored?.hasValue === true || (stored !== undefined && stored.value !== null && stored.value !== undefined) }
    }),
  })
}

/**
 * What Save as a copy leaves out, said once it is saved: the copy is a new definition, so a secret that was not typed
 * here has no saved value in it. Undefined when there is none.
 */
export function copyNotice(copied: VisualGraph): string | undefined {
  const names = definitionOf(copied).settings.filter(maskedSecret).map(s => (typeof s.label === 'string' && s.label.trim()) || s.key)
  if (!names.length) return
  const list = names.length === 1 ? names[0] : `${names.slice(0, -1).join(', ')} and ${names.at(-1)}`
  return `The copy has no saved value for ${list}. Type ${names.length === 1 ? 'it' : 'them'} again.`
}

// ---- Device

/** The device's details with `member` set, or left out when `value` is empty; the device's parent (`via`) stays as it is. */
export function updateDevice(graph: VisualGraph, member: keyof DefinitionDevice, value: string): VisualGraph {
  const device: DefinitionDevice = { ...(definitionOf(graph).device ?? {}) }
  if (value.trim()) (device as Record<string, unknown>)[member] = value
  else delete device[member]
  return withDefinition(graph, { device })
}
export const setDevice = (graph: VisualGraph, device: DefinitionDevice | null): VisualGraph => withDefinition(graph, { device })

// ---- Problems

const entityCodes = /^definition\.(entity(Key|Field|Domain|Option)|duplicateEntity)$/
const settingCodes = /^definition\.(setting(Key|Type)|duplicateSetting|unknownSetting)$/
/** The form's row a problem about no step is about: `entity:<key>` or `setting:<key>`, by its code and `inputId`. */
export function problemRow(diagnostic: VisualDiagnostic): string | undefined {
  const key = diagnostic.inputId, code = diagnostic.code ?? ''
  if (diagnostic.nodeId || !key) return
  if (entityCodes.test(code)) return `entity:${key}`
  if (settingCodes.test(code)) return `setting:${key}`
}
/**
 * The problems about no step, by where the form shows them: an entity's or a setting's (by its key, `inputId`), and
 * the rest (the definition's own, and an entity or setting the form has no row for).
 */
export function definitionProblems(diagnostics: VisualDiagnostic[], definition: EditorDefinition) {
  const entities = new Map<string, VisualDiagnostic[]>(), settings = new Map<string, VisualDiagnostic[]>(), others: VisualDiagnostic[] = []
  for (const diagnostic of diagnostics) {
    if (diagnostic.nodeId || diagnostic.severity <= 0) continue
    const row = problemRow(diagnostic), key = diagnostic.inputId ?? ''
    const into = row?.startsWith('entity:') && definition.entities.some(e => e.key === key) ? entities
      : row?.startsWith('setting:') && definition.settings.some(s => s.key === key) ? settings : undefined
    if (into) into.set(key, [...(into.get(key) ?? []), diagnostic])
    else others.push(diagnostic)
  }
  return { entities, settings, others }
}
