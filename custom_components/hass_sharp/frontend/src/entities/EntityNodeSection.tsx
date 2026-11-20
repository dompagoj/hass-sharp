import { For, Match, Show, Switch, type JSX } from 'solid-js'
import { connectData, dataOutputs, declaredDevices, declaredDomain, deviceNodeOf, disconnectData, entityKeyHolders, entityNodeName, entityTypeLabel, words, type ConfigSpec } from '../visual/contract'
import { keyProblem, keyRule, readNumber } from './definitionEdits'
import { ReadOnlyField, SelectField, SwitchField, TextField, type Choice } from './formFields'
import { renameEntityKey } from './model'
import type { StepPickerContext } from './NodePickers'
import { refitNotice, retypeNode } from './refit'

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const text = (value: unknown) => (typeof value === 'string' ? value : '')
/** Under a key Home Assistant has. */
const savedKeyHint = "The key is part of the entity's identity in Home Assistant and cannot change."
/** An entity's category: none, or where its device's page lists it apart. */
const categories: Choice[] = [{ value: '', label: 'None' }, { value: 'config', label: 'Config' }, { value: 'diagnostic', label: 'Diagnostic' }]
/** The flows of an entity whose runs can be recorded, by `config.history` member: its switch, and whether it is on by default. */
const flows = [
  { flow: 'state', label: 'Record state updates', on: false },
  { flow: 'commands', label: 'Record commands', on: true },
  { flow: 'frame', label: 'Record frames', on: false },
] as const

/** A field of the section, under the config key that a problem names (`data-input-id`), so the problem's row focuses it. */
const At = (props: { id: string; children: JSX.Element }) => <div data-input-id={props.id}>{props.children}</div>

/**
 * The right panel's section of an Entity node: its key (read only once saved), type, the device it is on (its Device
 * pin, wired from here), device class, icon, category, whether it is enabled by default, each option of its type, which
 * of its flows record their runs, and once Home Assistant has the entity, its entity id. Its name is the inspector's
 * header and its state values its inputs. A field cleared is left out of the node's config, never stored as null.
 */
export function EntityNodeSection(props: { ctx: StepPickerContext }) {
  const ctx = props.ctx
  const config = () => ctx.node().config
  const key = () => text(config().key)
  const domain = () => text(config().domain)
  const row = () => declaredDomain(ctx.node(), ctx.catalog())
  const set = (field: string, value: unknown) => {
    const next = { ...config() }
    if (value === undefined) delete next[field]
    else next[field] = value
    ctx.onChange(next)
  }
  // The key no longer changes once Home Assistant has it.
  const fixed = () => key() !== '' && !!ctx.saved?.()?.keys.includes(key())
  // Every other node's entity key, a C# entity's too: they share one namespace.
  const others = () => entityKeyHolders(ctx.graph(), ctx.catalog()).filter(d => d.nodeId !== ctx.node().id).map(d => text(d.key))
  // A new type, as one change: what no longer fits it goes, and the notice says what.
  const retype = (value: string) => {
    const { graph, removed } = retypeNode(ctx.graph(), ctx.node().id, value, ctx.catalog())
    ctx.onGraphChange(graph)
    const notice = refitNotice(removed)
    if (notice) ctx.onNotice?.(notice)
  }
  // The device it is on: the Device node wired to its Device pin. Each Device node is offered by its name (else its key,
  // else Device) and chosen by its node, so two of one name are told apart by their place in the list. Only a device with
  // an Entities pin is offered: a C# device adds its entities in its code, and none can be wired to it.
  const device = () => deviceNodeOf(ctx.graph(), ctx.node().id, ctx.catalog()) ?? ''
  const deviceChoices = (): Choice[] => [
    { value: '', label: 'None' },
    ...declaredDevices(ctx.graph(), ctx.catalog()).flatMap(d => {
      const node = ctx.graph().nodes.find(n => n.id === d.nodeId)
      return node && Object.hasOwn(dataOutputs(node, ctx.catalog()), 'entities') ? [{ value: d.nodeId, label: entityNodeName(node, ctx.catalog()) || 'Device' }] : []
    }),
  ]
  // One change: a wire from another device is replaced, None removes it.
  const wire = (nodeId: string) => {
    try {
      ctx.onGraphChange(nodeId ? connectData(ctx.graph(), nodeId, 'entities', ctx.node().id, 'device', ctx.catalog()) : disconnectData(ctx.graph(), ctx.node().id, 'device', ctx.catalog()))
    } catch (error) {
      // Refused: the wire would cause a problem, which the notice names; nothing changes.
      ctx.onNotice?.(error instanceof Error ? error.message : String(error))
    }
  }
  const types = () => Object.keys(ctx.catalog().entityDomains ?? {})
  const typeChoices = () => [
    ...(types().includes(domain()) ? [] : [{ value: domain(), label: entityTypeLabel(ctx.catalog(), domain()) }]),
    ...types().map(type => ({ value: type, label: entityTypeLabel(ctx.catalog(), type) })),
  ]
  const optionKeys = () => Object.keys(row()?.options ?? {})
  const options = () => (record(config().options) ? config().options as Record<string, unknown> : {})
  const setOption = (option: string, value: unknown) => {
    const next = { ...options() }
    if (value === undefined) delete next[option]
    else next[option] = value
    set('options', next)
  }
  // Only the flows its type has: state values, commands, frames.
  const recordable = () => {
    const type = row()
    if (!type) return []
    const has = { state: Object.keys(type.state ?? {}).length > 0, commands: Object.keys(type.commands ?? {}).length > 0, frame: record(type.frame) }
    return flows.filter(flow => has[flow.flow])
  }
  const history = () => (record(config().history) ? config().history as Record<string, unknown> : {})
  const records = (flow: (typeof flows)[number]) => { const value = history()[flow.flow]; return typeof value === 'boolean' ? value : flow.on }
  // A flow set back to its default is left out.
  const setRecords = (flow: (typeof flows)[number], on: boolean) => {
    const next = { ...history() }
    if (on === flow.on) delete next[flow.flow]
    else next[flow.flow] = on
    set('history', next)
  }
  const entityId = () => { const ids = ctx.saved?.()?.entityIds; return ids && Object.hasOwn(ids, key()) ? ids[key()] : undefined }
  const showInHomeAssistant = (event: MouseEvent, entityId: string) =>
    (event.currentTarget as HTMLElement).dispatchEvent(new CustomEvent('hass-more-info', { detail: { entityId }, bubbles: true, composed: true }))
  return (
    <>
      <At id="key">
        <Show when={!fixed()} fallback={<ReadOnlyField name="key" label="Key" text={key()} hint={savedKeyHint} />}>
          <TextField name="key" label="Key" text={key()} hint={`${keyRule} Steps that use the entity follow it. It cannot change once saved.`} errors={ctx.errorsFor('key')}
            commit={value => {
              if (value === key()) return
              const problem = keyProblem(value, others())
              if (problem) return problem
              ctx.onGraphChange(renameEntityKey(ctx.graph(), key(), value, ctx.node().id))
            }} />
        </Show>
      </At>
      <At id="domain">
        <SelectField name="domain" label="Type" value={domain()} choices={typeChoices()} errors={ctx.errorsFor('domain')} onChange={retype}
          hint="Steps that use the entity follow its type." />
      </At>
      <At id="device">
        <SelectField name="device" label="Device" value={device()} choices={deviceChoices()} errors={ctx.errorsFor('device')} onChange={wire}
          hint="An entity on a device is listed on that device's page. None: it stands alone." />
      </At>
      <At id="deviceClass">
        <TextField name="deviceClass" label="Device class" text={text(config().deviceClass)} placeholder="None" errors={ctx.errorsFor('deviceClass')}
          hint="A Home Assistant device class, for example motion." commit={value => set('deviceClass', value.trim() ? value.trim() : undefined)} />
      </At>
      <At id="icon">
        <TextField name="icon" label="Icon" text={text(config().icon)} placeholder="None" errors={ctx.errorsFor('icon')}
          hint="A Material Design icon, such as mdi:motion-sensor." commit={value => set('icon', value.trim() ? value.trim() : undefined)} />
      </At>
      <At id="entityCategory">
        <SelectField name="entityCategory" label="Category" value={text(config().entityCategory)} choices={categories} errors={ctx.errorsFor('entityCategory')}
          hint="Config and diagnostic entities are listed apart on the device's page." onChange={value => set('entityCategory', value || undefined)} />
      </At>
      <At id="enabledByDefault">
        <SwitchField name="enabledByDefault" label="Enabled by default" checked={config().enabledByDefault !== false} errors={ctx.errorsFor('enabledByDefault')}
          hint="Off: Home Assistant adds it turned off, to be turned on in its settings." onChange={on => set('enabledByDefault', on ? undefined : false)} />
      </At>
      {/* By key: a field stays (and keeps focus) while another option changes. */}
      <For each={optionKeys()}>
        {option => (
          <At id={`option:${option}`}>
            <OptionField option={option} rule={row()?.options?.[option] ?? { type: 'string' }} value={options()[option]} errors={ctx.errorsFor(`option:${option}`)}
              onChange={value => setOption(option, value)} />
          </At>
        )}
      </For>
      <Show when={recordable().length}>
        <section class="hs-insp-section" aria-label="Run history" data-input-id="history">
          <h3 class="hs-insp-heading">Run history</h3>
          <For each={recordable()}>
            {flow => <SwitchField name={`history:${flow.flow}`} label={flow.label} checked={records(flow)} onChange={on => setRecords(flow, on)} />}
          </For>
        </section>
      </Show>
      <Show when={entityId()}>
        {id => (
          <p class="hs-def-row__id hs-entity-node__id">
            <code>{id()}</code>
            <button type="button" class="hs-button hs-button--ghost hs-def-row__show" aria-label={`Show ${id()} in Home Assistant`}
              onClick={event => showInHomeAssistant(event, id())}>Show</button>
          </p>
        )}
      </Show>
    </>
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
        <TextField name={name()} label={label()} hint={hint()} placeholder={placeholder()} required={required()} errors={props.errors}
          text={typeof props.value === 'number' ? String(props.value) : text(props.value)}
          commit={value => { const read = readNumber(value); props.onChange(read === null ? undefined : read ?? value) }} />
      </Match>
      <Match when={props.rule.type === 'boolean'}>
        <SwitchField name={name()} label={label()} hint={hint()} checked={props.value === true} errors={props.errors} onChange={on => props.onChange(on || undefined)} />
      </Match>
    </Switch>
  )
}
