import { createEffect, createSignal, createUniqueId, For, Index, Match, on, Show, Switch } from 'solid-js'
import { Button } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { entityTypeLabel, words, type ConfigSpec, type VisualCatalogSnapshot } from '../visual/contract'
import type { VisualDiagnostic } from '../visual/api'
import { Glyph } from '../visual/Glyph'
import type { VisualGraph } from '../visual/graph'
import { icons } from '../visual/icons'
import { addEntity, definitionOf, keyProblem, keyRule, problemOption, readOptionNumber, removeEntity, setEntityOption, stepsUsingEntity, updateEntity } from './definitionEdits'
import { FieldErrors, focusRow, ReadOnlyField, SelectField, SwitchField, TextField, type Choice } from './formFields'
import { renameEntityKey, type DefinitionEntity } from './model'
import { retypeEntity } from './NodePickers'

const text = (value: unknown) => (typeof value === 'string' ? value : '')
const savedKeyHint = "The key is part of the entity's identity in Home Assistant and cannot change."
const categories: Choice[] = [{ value: '', label: 'None' }, { value: 'config', label: 'Config' }, { value: 'diagnostic', label: 'Diagnostic' }]
/** "N steps use this entity…": what removing a used entity or setting leaves. */
export const usedBy = (count: number, noun: string) =>
  `${count} ${count === 1 ? 'step uses' : 'steps use'} this ${noun}. ${count === 1 ? 'It' : 'They'} will show a problem until you change or remove ${count === 1 ? 'it' : 'them'}.`

export interface RowsProps {
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  /** The problems about each entity (by key) that no step has. */
  problems: Map<string, VisualDiagnostic[]>
  /**
   * The keys of the saved definition's entities: each is part of its entity's identity in Home Assistant and no longer
   * changes. An entity added since keeps its key open until the save that first stores it.
   */
  savedKeys: readonly string[]
  /** A problem chosen in the Problems tab: the row it names (`entity:<key>`) opens, and focus goes into it. */
  located?: { target: string }
  commit(graph: VisualGraph): void
  /** The entity id Home Assistant gave each entity of the saved definition, by key. */
  entityIds: Map<string, string>
  onNotice?(text: string): void
}

/**
 * The Entities section: a row per entity (its name, type and key; opened, every field of it), "Add entity", and
 * Remove, which asks first when steps use the entity or it is the last one.
 */
export function EntityRows(props: RowsProps) {
  const entities = () => definitionOf(props.graph).entities
  const [open, setOpen] = createSignal<ReadonlySet<string>>(new Set())
  const toggle = (key: string, wanted = !open().has(key)) => setOpen(current => { const next = new Set(current); if (wanted) next.add(key); else next.delete(key); return next })
  // A row that gets a problem opens, so the problem is in view.
  createEffect(on(() => [...props.problems.keys()].join(' '), () => { for (const key of props.problems.keys()) if (!open().has(key)) toggle(key, true) }))
  let list: HTMLUListElement | undefined
  createEffect(on(() => props.located, located => {
    const key = located?.target.startsWith('entity:') ? located.target.slice('entity:'.length) : undefined
    if (key === undefined || !entities().some(e => e.key === key)) return
    toggle(key, true)
    // Once the opened row is drawn.
    queueMicrotask(() => focusRow(list, 'entityKey', key))
  }))
  const domains = () => Object.keys(props.catalog.entityDomains ?? {})
  const [removing, setRemoving] = createSignal<string>()
  const remove = (key: string) => {
    if (stepsUsingEntity(props.graph, key).length || entities().length === 1) return setRemoving(key)
    props.commit(removeEntity(props.graph, key))
  }
  const add = () => {
    const made = addEntity(props.graph, domains()[0] ?? 'binary_sensor')
    props.commit(made.graph)
    toggle(made.key, true)
  }
  const removingUses = () => { const key = removing(); return key ? stepsUsingEntity(props.graph, key).length : 0 }
  return (
    <section class="hs-def-section" aria-label="Entities">
      <h2 class="hs-insp-heading">Entities</h2>
      <Show when={entities().length} fallback={<p class="hs-muted">This entity definition has no entities yet.</p>}>
        <ul class="hs-def-rows" aria-label="Entities" ref={list}>
          <Index each={entities()}>
            {entity => (
              <EntityRow entity={entity()} rows={props} open={open().has(entity().key)} onToggle={() => toggle(entity().key)}
                onRenamed={(from, to) => setOpen(current => { const next = new Set(current); if (next.delete(from)) next.add(to); return next })}
                onRemove={() => remove(entity().key)} types={domains()} />
            )}
          </Index>
        </ul>
      </Show>
      <div class="hs-def-actions"><Button onClick={add}>Add entity</Button></div>
      <Dialog open={!!removing()} title="Remove this entity?" width="small" onClose={() => setRemoving(undefined)} footer={<>
        <Button onClick={() => setRemoving(undefined)}>Cancel</Button>
        <Button variant="danger" onClick={() => { const key = removing()!; setRemoving(undefined); props.commit(removeEntity(props.graph, key)) }}>Remove</Button>
      </>}>
        <Show when={removingUses()}><p>{usedBy(removingUses(), 'entity')}</p></Show>
        <Show when={entities().length === 1}><p>It is the only entity: the entity definition needs one before it can be saved.</p></Show>
      </Dialog>
    </section>
  )
}

function EntityRow(props: {
  entity: DefinitionEntity
  rows: RowsProps
  open: boolean
  types: string[]
  onToggle(): void
  onRenamed(from: string, to: string): void
  onRemove(): void
}) {
  const id = createUniqueId()
  const rows = props.rows
  const key = () => props.entity.key
  // An option's problem goes under that option's field; the rest at the top of the row.
  const messages = () => (rows.problems.get(key()) ?? []).map(d => d.message)
  const optionKeys = () => Object.keys(spec()?.options ?? {})
  const onOption = (message: string) => { const option = problemOption(message); return option !== undefined && optionKeys().includes(option) ? option : undefined }
  const problems = messages
  const rowProblems = () => messages().filter(message => onOption(message) === undefined)
  const optionErrors = (option: string) => messages().filter(message => onOption(message) === option)
  const typeLabel = () => entityTypeLabel(rows.catalog, props.entity.domain)
  const spec = () => { const domains = rows.catalog.entityDomains; return domains && Object.hasOwn(domains, props.entity.domain) ? domains[props.entity.domain] : undefined }
  const set = (changes: Partial<Record<keyof DefinitionEntity, unknown>>) => rows.commit(updateEntity(rows.graph, key(), changes))
  const others = () => definitionOf(rows.graph).entities.filter(e => e.key !== key()).map(e => e.key)
  const entityId = () => rows.entityIds.get(key())
  const typeChoices = () => [
    ...(props.types.includes(props.entity.domain) ? [] : [{ value: props.entity.domain, label: entityTypeLabel(rows.catalog, props.entity.domain) }]),
    ...props.types.map(domain => ({ value: domain, label: entityTypeLabel(rows.catalog, domain) })),
  ]
  const retype = (domain: string) => {
    const result = retypeEntity(rows.graph, key(), domain, rows.catalog)
    rows.commit(result.graph)
    if (result.notice) rows.onNotice?.(result.notice)
  }
  const showInHomeAssistant = (event: MouseEvent, entityId: string) =>
    (event.currentTarget as HTMLElement).dispatchEvent(new CustomEvent('hass-more-info', { detail: { entityId }, bubbles: true, composed: true }))
  return (
    <li class="hs-def-row" data-entity-key={key()} classList={{ 'hs-def-row--problem': problems().length > 0 }}>
      <button type="button" class="hs-def-row__head" aria-expanded={props.open} aria-controls={props.open ? `${id}-body` : undefined} onClick={() => props.onToggle()}>
        <span class="hs-def-row__icon" aria-hidden="true"><Glyph paths={icons.entity} /></span>
        <span class="hs-def-row__title">
          <strong>{text(props.entity.name) || key()}</strong>
          <small>{typeLabel()} · {key()}</small>
        </span>
        <Show when={problems().length}>
          {count => <span class="hs-def-row__mark" role="img" aria-label={`${count()} ${count() === 1 ? 'problem' : 'problems'}`}><Glyph paths={icons.problem} /></span>}
        </Show>
      </button>
      <Show when={entityId()}>
        {entityId => (
          <p class="hs-def-row__id">
            <code>{entityId()}</code>
            <button type="button" class="hs-button hs-button--ghost hs-def-row__show" aria-label={`Show ${entityId()} in Home Assistant`}
              onClick={event => showInHomeAssistant(event, entityId())}>Show</button>
          </p>
        )}
      </Show>
      <Show when={props.open}>
        <div id={`${id}-body`} class="hs-def-row__body">
          <FieldErrors list={rowProblems()} />
          <TextField name="name" label="Name" text={text(props.entity.name)} placeholder="Not set"
            hint="The name Home Assistant shows for it." commit={value => set({ name: value.trim() || undefined })} />
          <Show when={!rows.savedKeys.includes(key())} fallback={<ReadOnlyField name="key" label="Key" text={key()} hint={savedKeyHint} />}>
            <TextField name="key" label="Key" text={key()} hint={`${keyRule} Steps that use the entity follow it. It cannot change once saved.`}
              commit={value => {
                if (value === key()) return
                const problem = keyProblem(value, others())
                if (problem) return problem
                const from = key()
                rows.commit(renameEntityKey(rows.graph, from, value))
                props.onRenamed(from, value)
              }} />
          </Show>
          <SelectField name="domain" label="Type" value={props.entity.domain} choices={typeChoices()} onChange={retype}
            hint="Steps that use the entity follow its type." />
          <TextField name="deviceClass" label="Device class" text={text(props.entity.deviceClass)} placeholder="None"
            hint="A Home Assistant device class, for example motion." commit={value => set({ deviceClass: value.trim() ? value.trim() : undefined })} />
          <TextField name="icon" label="Icon" text={text(props.entity.icon)} placeholder="None"
            hint="A Material Design icon, such as mdi:motion-sensor." commit={value => set({ icon: value.trim() ? value.trim() : undefined })} />
          <SelectField name="entityCategory" label="Category" value={text(props.entity.entityCategory)} choices={categories}
            hint="Config and diagnostic entities are listed apart on the device's page." onChange={value => set({ entityCategory: value || undefined })} />
          <SwitchField name="enabledByDefault" label="Enabled by default" checked={props.entity.enabledByDefault !== false}
            hint="Off: Home Assistant adds it turned off, to be turned on in its settings." onChange={on => set({ enabledByDefault: on ? undefined : false })} />
          <For each={Object.entries(spec()?.options ?? {})}>
            {([option, rule]) => <OptionField option={option} rule={rule} value={props.entity.options?.[option]} errors={optionErrors(option)}
              onChange={value => rows.commit(setEntityOption(rows.graph, key(), option, value))} />}
          </For>
          <div class="hs-def-actions">
            <Button variant="danger" aria-label={`Remove entity ${text(props.entity.name) || key()}`} onClick={() => props.onRemove()}>Remove</Button>
          </div>
        </div>
      </Show>
    </li>
  )
}

/**
 * One option the entity's type takes (`entityDomains[…].options`), by its rule: a list of texts (one per line), a
 * choice of its values, a number, a switch, or a text; its hint is the catalog's, and one the type needs says so. A
 * number is read as the class page's number fields read theirs: a text that is not one is kept as typed (the form
 * then says so on the field, and Save waits). Empty, an option is left out (the type's default); so is a switch off.
 */
function OptionField(props: { option: string; rule: ConfigSpec; value: unknown; errors: string[]; onChange(value: unknown): void }) {
  const name = () => `option:${props.option}`
  const label = () => text(props.rule.label) || words.key(props.option)
  const required = () => props.rule.required === true
  const hint = () => [text(props.rule.description), required() ? 'Required.' : ''].filter(Boolean).join(' ') || undefined
  const placeholder = () => (required() ? 'Required' : 'None')
  const allowed = () => (Array.isArray(props.rule.enum) ? props.rule.enum.map(String) : undefined)
  return (
    <Switch fallback={
      <Show when={allowed()} fallback={
        <TextField name={name()} label={label()} hint={hint()} placeholder={placeholder()} required={required()} errors={props.errors} text={text(props.value)}
          commit={value => props.onChange(value.trim() ? value.trim() : undefined)} />
      }>
        {values => <SelectField name={name()} label={label()} hint={hint()} value={text(props.value)} errors={props.errors}
          choices={[{ value: '', label: required() ? 'Choose one' : 'None' }, ...values().map(value => ({ value, label: words.key(value) }))]} onChange={value => props.onChange(value || undefined)} />}
      </Show>
    }>
      <Match when={props.rule.type === 'array'}>
        <TextField name={name()} label={label()} hint={hint()} multiline placeholder={placeholder()} required={required()} errors={props.errors}
          text={Array.isArray(props.value) ? props.value.map(String).join('\n') : ''}
          commit={value => { const items = value.split('\n').map(item => item.trim()).filter(Boolean); props.onChange(items.length ? items : undefined) }} />
      </Match>
      <Match when={props.rule.type === 'number'}>
        <TextField name={name()} label={label()} hint={hint()} placeholder={placeholder()} required={required()} errors={props.errors} inputmode="decimal"
          text={typeof props.value === 'number' ? String(props.value) : text(props.value)}
          commit={value => { const trimmed = value.trim(); props.onChange(trimmed === '' ? undefined : readOptionNumber(trimmed) ?? value) }} />
      </Match>
      <Match when={props.rule.type === 'boolean'}>
        <SwitchField name={name()} label={label()} hint={hint()} checked={props.value === true} errors={props.errors} onChange={on => props.onChange(on || undefined)} />
      </Match>
    </Switch>
  )
}
