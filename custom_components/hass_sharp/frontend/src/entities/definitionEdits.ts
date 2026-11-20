import { blankEnds, declaredEntities, entityTypeLabel, freeKey, words, type ConfigSpec, type Graph, type GraphDefinition, type JsonValue, type VisualCatalogSnapshot } from '../visual/contract'
import type { VisualDiagnostic } from '../visual/api'
import type { VisualGraph } from '../visual/graph'
import { definitionEntityKinds, entityKey, entityKeyPattern, maskedSetting, renameEntityKey, type DefinitionDevice, type DefinitionEntity, type DefinitionSetting, type DefinitionVia, type EditorDefinition, type EntityDocument } from './model'

/**
 * The definition form's changes to an editor graph's `definition` (its settings), each a new graph: one undo step when
 * committed. Nothing here changes its argument. Its entities and devices are its Entity and Device nodes, edited on the
 * canvas. The device helpers below are the class editor's, whose definition has a device of its own.
 */

/** The rule an entity's key follows, as the form says it. */
export const keyRule = 'Lower-case letters, digits and underscores, starting with a letter; at most 40 characters.'
/** The rule a setting's key follows, the server's (ruling R15): not blank once trimmed, and not another setting's. */
export const settingKeyRule = "Any text that is not blank and not another setting's key."
const takenKey = 'Another one already has this key; give each a key of its own.'

/** The definition the graph carries: its settings, none when it has none. */
export function definitionOf(graph: VisualGraph): EditorDefinition {
  const definition = (graph.definition ?? {}) as unknown as Partial<EditorDefinition>
  return { settings: definition.settings ?? [] }
}
/** The graph with its definition changed: its settings, and nothing else it carried (such as an earlier build's device). */
const withDefinition = (graph: VisualGraph, change: Partial<EditorDefinition>): VisualGraph =>
  ({ ...graph, definition: { ...definitionOf(graph), ...change } as unknown as GraphDefinition })

export { freeKey }

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
export const maskedSecret = (setting: DefinitionSetting) => setting.secret === true && maskedSetting(setting)

// ---- Entities

/** The steps that name the definition's entity `key`, but those of `except`. */
export const stepsUsingEntity = (graph: VisualGraph, key: string, except: ReadonlySet<string> = new Set()) =>
  graph.nodes.filter(node => definitionEntityKinds.includes(node.kind) && node.config?.entity === key && !except.has(node.id))

/**
 * What deleting the steps `ids` asks first, when among them are Entity nodes whose entities steps left behind still
 * name (a key no other Entity node gives): those steps would then show a problem. Undefined when nothing would: the
 * steps are deleted at once.
 */
export function entityRemoval(graph: Graph, ids: string[], catalog: VisualCatalogSnapshot): { title: string; body: string } | undefined {
  const deleted = new Set(ids)
  const declared = declaredEntities(graph, catalog)
  const going = declared.filter(d => deleted.has(d.nodeId))
  if (!going.length) return
  // A key a node that stays still declares leaves no step without it.
  const staying = new Set(declared.filter(d => !deleted.has(d.nodeId)).map(d => d.entity.key))
  const keys = new Set(going.map(d => d.entity.key).filter((key): key is string => typeof key === 'string' && key !== '' && !staying.has(key)))
  const counts = [...keys].map(key => stepsUsingEntity(graph, key, deleted).length)
  const steps = counts.reduce((sum, count) => sum + count, 0)
  if (!steps) return
  // "These entities" only when more than one of them is still used: one used among several deleted is "this entity".
  const several = counts.filter(count => count > 0).length > 1
  const them = several ? 'these entities' : 'this entity'
  return {
    title: several ? 'Delete these entities?' : 'Delete this entity?',
    body: steps === 1 ? `1 step uses ${them}. It will show a problem until you change or remove it.` : `${steps} steps use ${them}. They will show a problem until you change or remove them.`,
  }
}

/**
 * Whether an option's value is one its type's rule takes: a list of texts for an array, a finite number, true or false,
 * or a text; one of its values when it has an `enum`. What the server checks of each option's type (`DefinitionDocument`).
 */
export function optionFits(rule: ConfigSpec, value: unknown): boolean {
  const allowed = (item: unknown) => !Array.isArray(rule.enum) || rule.enum.includes(item)
  if (rule.type === 'array') return Array.isArray(value) && value.every(item => typeof item === 'string' && allowed(item))
  if (rule.type === 'number') return typeof value === 'number' && Number.isFinite(value)
  if (rule.type === 'boolean') return typeof value === 'boolean'
  return typeof value === 'string' && allowed(value)
}

/**
 * A number as typed: digits, a sign, a fraction and an exponent, with a point; `null` for an empty text, `undefined`
 * for one that is not a number (a comma, a unit, an unfinished number), or not a whole one when `whole`. How the class
 * page's number settings and the entity form's number options read what is typed.
 */
export function readNumber(typed: string, whole = false): number | null | undefined {
  const trimmed = typed.trim()
  if (!trimmed) return null
  if (!/^[+-]?(\d+(\.\d*)?|\.\d+)(e[+-]?\d+)?$/i.test(trimmed)) return
  const parsed = Number(trimmed)
  if (!Number.isFinite(parsed) || (whole && !Number.isSafeInteger(parsed))) return
  return parsed
}

/** A problem with an option of the entity an Entity node declares: on the node, about `option:<option>`. */
const nodeOptionProblem = (nodeId: string, option: string, message: string): VisualDiagnostic =>
  ({ nodeId, inputId: `option:${option}`, portId: null, message, severity: 3, startLine: null, startColumn: null, code: 'definition.entityOption' })
/** An option as a problem names it: its label, else its key in words, as its field is labelled. */
export const optionLabel = (option: string, rule: ConfigSpec | undefined) =>
  typeof rule?.label === 'string' && rule.label ? rule.label : words.key(option)
const hasName = (entity: { name?: unknown }) => typeof entity.name === 'string' && entity.name.replace(blankEnds, '') !== ''
const lowerFirst = (text: string) => text.charAt(0).toLowerCase() + text.slice(1)
/**
 * The range a number option's rule gives (its `min` and `max`, such as a camera's seconds between frames), in the words
 * of `DefinitionDocument.Range`, when `value` is outside it; undefined when it is within, or the rule gives no range.
 */
function outsideRange(rule: ConfigSpec, value: number): string | undefined {
  const { min, max } = rule
  return typeof min === 'number' && typeof max === 'number' && (value < min || value > max) ? `a number from ${min} to ${max}` : undefined
}

/**
 * What the editor knows of the Entity nodes' options without asking the server, in the server's words
 * (`DefinitionDocument.Options`), each problem on its node (`nodeId`) about `option:<option>`, so the server's answer
 * about the same option is not shown twice: an option the type needs left empty, a number option holding a text that
 * is not a number (kept as typed, so it can be finished), and a number outside the range its rule gives. Each blocks
 * Save; the server checks the rest. A node whose options are not an object is its Options field's.
 */
export function optionProblems(graph: VisualGraph, catalog: VisualCatalogSnapshot): VisualDiagnostic[] {
  const problems: VisualDiagnostic[] = []
  const check = (entity: Partial<DefinitionEntity>, problem: (option: string, message: string) => VisualDiagnostic) => {
    const domains = catalog.entityDomains
    const row = domains && typeof entity.domain === 'string' && Object.hasOwn(domains, entity.domain) ? domains[entity.domain] : undefined
    if (!row || typeof entity.key !== 'string' || typeof entity.domain !== 'string') return
    if (entity.options !== undefined && (entity.options === null || typeof entity.options !== 'object' || Array.isArray(entity.options))) return
    // As entityNodeName names it: its name, else its key, each trimmed, else Entity; a sentence that starts with it says
    // Entity and its key when it has no name.
    const key = entity.key.replace(blankEnds, ''), subject = key ? `Entity ${key}` : 'Entity'
    const options = entity.options ?? {}, label = entityTypeLabel(catalog, entity.domain), named = hasName(entity) ? (entity.name as string).replace(blankEnds, '') : key || 'Entity'
    for (const [option, rule] of Object.entries(row.options ?? {})) {
      const value = Object.hasOwn(options, option) ? options[option] : undefined
      const range = rule.type === 'number' && typeof value === 'number' && Number.isFinite(value) ? outsideRange(rule, value) : undefined
      if (rule.type === 'number' && typeof value === 'string') problems.push(problem(option, `${optionLabel(option, rule)} of ${named} takes a number`))
      else if (range) problems.push(problem(option, `${optionLabel(option, rule)} of ${named} takes ${range}, not ${value}`))
      else if (rule.required === true && (value === undefined || value === null))
        // A sentence starts with the entity: its name, else "Entity" and its key, which may start lower-case.
        problems.push(problem(option, `${hasName(entity) ? named : subject} needs ${lowerFirst(optionLabel(option, rule))}: ${words.article(label)} ${label.toLowerCase()} cannot work without it`))
    }
  }
  // A node without a text key is named by none, as the server names it.
  for (const { nodeId, entity } of declaredEntities(graph, catalog))
    check({ ...entity, key: typeof entity.key === 'string' ? entity.key : '' } as Partial<DefinitionEntity>, (option, message) => nodeOptionProblem(nodeId, option, message))
  return problems
}

/**
 * The definition renamed to `name` before its first save: its first Entity node (the starter's) follows the name (key
 * and name) while it still has what the name gave it, so the person who never touched it gets an entity named after
 * the definition.
 */
export function renameDefinition(graph: VisualGraph, name: string, saved: boolean): VisualGraph {
  const renamed = { ...graph, name }
  if (saved) return renamed
  // As the starter names it: no name for a blank one.
  const given = (text: string) => (text.trim() ? text : undefined)
  const follows = (entity: { key?: unknown; name?: unknown }) => entity.key === entityKey(graph.name) && entity.name === given(graph.name)
  const [starter] = declaredEntities(graph)
  if (!starter || !follows(starter.entity)) return renamed
  const { nodeId, entity } = starter
  const keyed = renameEntityKey(renamed, entity.key as string, entityKey(name), nodeId)
  return {
    ...keyed,
    nodes: keyed.nodes.map(node => {
      if (node.id !== nodeId) return node
      const { name: _name, ...config } = node.config
      return { ...node, config: { ...config, ...(given(name) ? { name } : {}) } }
    }),
  }
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

// ---- A class definition's device

/** A device's details with `member` set to `value`, or left out when it is blank; its parent (`via`) stays as it is. */
export function withDetail(device: DefinitionDevice | null, member: keyof DefinitionDevice, value: string): DefinitionDevice {
  const next: DefinitionDevice = { ...(device ?? {}) }
  if (value.trim()) (next as Record<string, unknown>)[member] = value
  else delete next[member]
  return next
}
/** The device part of `via` (`{definition}` or `{deviceId}`), or of nothing (null); its details stay as they are. */
export function withVia(device: DefinitionDevice | null, via: DefinitionVia | null): DefinitionDevice {
  const next: DefinitionDevice = { ...(device ?? {}) }
  if (via) next.via = via
  else delete next.via
  return next
}

// ---- Problems

const settingCodes = /^definition\.(setting(Key|Type)|duplicateSetting|unknownSetting)$/
/** The form's row a problem about no step is about: `setting:<key>`, by its code and `inputId`. */
export function problemRow(diagnostic: VisualDiagnostic): string | undefined {
  const key = diagnostic.inputId, code = diagnostic.code ?? ''
  if (diagnostic.nodeId || !key) return
  if (settingCodes.test(code)) return `setting:${key}`
}
/**
 * The problems about no step, by where the form shows them: a setting's (by its key, `inputId`), and the rest (the
 * definition's own, and a setting the form has no row for). A problem about an entity is on its Entity node.
 */
export function definitionProblems(diagnostics: VisualDiagnostic[], definition: EditorDefinition) {
  const settings = new Map<string, VisualDiagnostic[]>(), others: VisualDiagnostic[] = []
  for (const diagnostic of diagnostics) {
    if (diagnostic.nodeId || diagnostic.severity <= 0) continue
    const key = diagnostic.inputId ?? ''
    if (problemRow(diagnostic) && definition.settings.some(s => s.key === key)) settings.set(key, [...(settings.get(key) ?? []), diagnostic])
    else others.push(diagnostic)
  }
  return { settings, others }
}
