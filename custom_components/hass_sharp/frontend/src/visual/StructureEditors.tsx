import { createSignal, For, Show } from 'solid-js'
import { InputEditor } from './InputEditor'
import { closeIcon, Errors, keepFocus, TypeChip } from './InputRow'
import { Glyph } from './Glyph'
import { execLabel } from './flow/flowModel'
import { resolveTypeSpec, type GraphNode, type Operand, type VisualCatalogSnapshot } from './contract'
import type { VisualGraph } from './graph'
import { typeColor, typeLabel } from './typeLabels'
import { DraftField } from './widgets/common'

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)

/** The step a structure editor changes, read through accessors so the editor survives edits of that step. */
export interface StructureContext {
  node(): GraphNode
  graph(): VisualGraph
  catalog(): VisualCatalogSnapshot
  /** Sets (or, with `undefined`, removes) one config key. */
  set(key: string, value: unknown): void
  errorsFor(key: string): string[]
  /** Focuses the row of an input once it is shown. */
  focus(port: string): void
}
const connected = (ctx: StructureContext, port: string) =>
  ctx.graph().edges.some(e => e.kind === 'data' && e.target === ctx.node().id && e.targetPort === port)

/** The branches of a Parallel step; one that has steps attached is removed on the canvas first. */
export function Branches(props: { ctx: StructureContext }) {
  const ctx = props.ctx
  let addButton!: HTMLButtonElement
  const branches = () => { const list = ctx.node().config.branches; return Array.isArray(list) ? list.filter((b): b is string => typeof b === 'string') : [] }
  const used = (id: string) => ctx.graph().edges.some(e => e.kind !== 'data' && e.source === ctx.node().id && e.port === `branch:${id}`)
  const add = () => {
    const taken = new Set(branches())
    const id = [...'abcdefghijklmnopqrstuvwxyz'].find(letter => !taken.has(letter)) ?? `branch_${branches().length + 1}`
    ctx.set('branches', [...branches(), id])
  }
  return (
    <div class="hs-insp-field" data-input-id="branches" aria-invalid={ctx.errorsFor('branches').length > 0 || undefined}>
      <span class="hs-insp-field__label">Branches</span>
      <ul class="hs-insp-list">
        <For each={branches()}>
          {id => {
            const name = () => execLabel(ctx.node(), `branch:${id}`, ctx.catalog())
            return (
              <li>
                <span>{name()}</span>
                <button type="button" class="hs-button hs-button--ghost hs-insp-remove" aria-label={`Remove ${name()}`} disabled={used(id) || branches().length <= 1}
                  title={used(id) ? 'Remove its steps on the canvas first' : undefined}
                  onClick={event => {
                    const restore = keepFocus(event.currentTarget.closest('li'), 'li', () => addButton)
                    ctx.set('branches', branches().filter(b => b !== id))
                    restore()
                  }}>
                  <Glyph paths={closeIcon} />
                </button>
              </li>
            )
          }}
        </For>
      </ul>
      <button ref={addButton} type="button" class="hs-button hs-button--secondary hs-insp-add-button" onClick={add}>+ Add branch</button>
      <small class="hs-field-hint">Branches run at the same time; Done runs once all of them have finished.</small>
      <Errors list={ctx.errorsFor('branches')} />
    </div>
  )
}

/** The field names of a Create object step; each is an input below. */
export function ObjectFields(props: { ctx: StructureContext }) {
  const ctx = props.ctx
  let root!: HTMLDivElement
  const fields = () => { const value = ctx.node().config.fields; return record(value) ? value : {} }
  return (
    <div class="hs-insp-field" data-input-id="fields" ref={root}>
      <span class="hs-insp-field__label">Fields</span>
      <ul class="hs-insp-list">
        <For each={Object.keys(fields())}>
          {key => (
            <li>
              <code>{key}</code>
              <button type="button" class="hs-button hs-button--ghost hs-insp-remove" aria-label={`Remove field ${key}`} disabled={connected(ctx, `field:${key}`)}
                title={connected(ctx, `field:${key}`) ? 'Remove its connection on the canvas first' : undefined}
                onClick={event => {
                  const restore = keepFocus(event.currentTarget.closest('li'), 'li', () => root.querySelector<HTMLInputElement>('input[aria-label="Add a field named"]'))
                  const next = { ...fields() }
                  delete next[key]
                  ctx.set('fields', next)
                  restore()
                }}>
                <Glyph paths={closeIcon} />
              </button>
            </li>
          )}
        </For>
      </ul>
      <DraftField label="Add a field named" placeholder="+ Add field…" text="" clearOnCommit
        commit={text => {
          const name = text.trim()
          if (!name) return
          if (Object.hasOwn(fields(), name)) return 'There is already a field with this name.'
          ctx.set('fields', { ...fields(), [name]: { kind: 'unset' } })
          ctx.focus(`field:${name}`)
        }} />
    </div>
  )
}

/** The items of a Create array step; each is an input below. */
export function ArrayItems(props: { ctx: StructureContext }) {
  const ctx = props.ctx
  let addButton!: HTMLButtonElement
  const items = () => { const value = ctx.node().config.items; return Array.isArray(value) ? value : [] }
  const lastConnected = () => items().length > 0 && connected(ctx, `item:${items().length - 1}`)
  const removeLast = () => {
    const count = items().length
    ctx.set('items', items().slice(0, -1))
    // The item before the removed one, else the add button.
    if (count > 1) ctx.focus(`item:${count - 2}`)
    else queueMicrotask(() => addButton.focus())
  }
  return (
    <div class="hs-insp-field" data-input-id="items">
      <span class="hs-insp-field__label">Items</span>
      <div class="hs-insp-field__row">
        <span class="hs-muted">{items().length === 1 ? '1 item' : `${items().length} items`}</span>
        <button ref={addButton} type="button" class="hs-button hs-button--secondary hs-insp-add-button"
          onClick={() => { const count = items().length; ctx.set('items', [...items(), { kind: 'unset' }]); ctx.focus(`item:${count}`) }}>+ Add item</button>
        <button type="button" class="hs-button hs-button--ghost" disabled={!items().length || lastConnected()} title={lastConnected() ? 'Remove its connection on the canvas first' : undefined}
          onClick={removeLast}>Remove last</button>
      </div>
    </div>
  )
}

/** Values a loop keeps between iterations or makes available after it, each with its type and starting value. */
export function Declarations(props: { ctx: StructureContext; key: 'carry' | 'exports'; title: string; hint: string }) {
  const ctx = props.ctx
  let nameInput!: HTMLInputElement
  const list = () => { const value = ctx.node().config[props.key]; return Array.isArray(value) ? value.filter(record) : [] }
  // Rows are keyed by name and by position among the entries of that name: editing a starting value or removing
  // another entry keeps a row (and its focus), and two entries with the same or no name (a graph written through the
  // API) stay two rows, each edited and removed by its own position.
  const nameAt = (entry: Record<string, unknown>) => String(entry.name ?? '')
  const keys = () => list().map((entry, index, all) => `${all.slice(0, index).filter(other => nameAt(other) === nameAt(entry)).length}:${nameAt(entry)}`)
  const nameOf = (key: string) => key.slice(key.indexOf(':') + 1)
  /** The entry's current position in the list. */
  const at = (key: string) => keys().indexOf(key)
  const prefix = props.key === 'carry' ? 'carry' : 'export'
  const used = (name: string) => ctx.graph().edges.some(e => e.kind === 'data' && ((e.source === ctx.node().id && e.sourcePort === `${prefix}:${name}`) || e.targetPort === `${prefix}:${name}`))
  const literalTypes = () => Object.keys(ctx.catalog().types).filter(t => resolveTypeSpec(t, ctx.catalog())?.literalKind)
  const [name, setName] = createSignal('')
  const [type, setType] = createSignal('number')
  const [error, setError] = createSignal('')
  const update = (index: number, patch: Record<string, unknown>) =>
    ctx.set(props.key, list().map((entry, i) => (i === index ? { ...entry, ...patch } : entry)))
  const add = () => {
    const value = name().trim()
    if (!/^[A-Za-z_][A-Za-z0-9_]{0,63}$/.test(value)) return setError('Use letters, digits and underscores, not starting with a digit.')
    if (list().some(entry => entry.name === value)) return setError('That name is already used.')
    setError('')
    setName('')
    ctx.set(props.key, [...list(), { name: value, type: type(), initial: { kind: 'unset' } }])
  }
  return (
    <section class="hs-insp-section" aria-label={props.title} data-input-id={props.key}>
      <h3 class="hs-insp-heading">{props.title}</h3>
      <small class="hs-field-hint">{props.hint}</small>
      <For each={keys()}>
        {key => {
          const itemName = nameOf(key)
          const item = () => list()[at(key)]
          const itemType = () => { const t = item()?.type; return typeof t === 'string' ? t : 'object' }
          const initial = () => { const value = item()?.initial; return record(value) ? (value as Operand) : undefined }
          return (
            <div class="hs-insp-declaration">
              <div class="hs-insp-field__row">
                <code>{itemName}</code>
                <TypeChip text={typeLabel(itemType(), ctx.catalog())} color={typeColor(itemType(), ctx.catalog())} />
                <button type="button" class="hs-button hs-button--ghost hs-insp-remove" aria-label={`Remove ${itemName}`} disabled={used(itemName)}
                  title={used(itemName) ? 'Remove its connections on the canvas first' : undefined}
                  onClick={event => {
                    const restore = keepFocus(event.currentTarget.closest('.hs-insp-declaration'), '.hs-insp-declaration', () => nameInput)
                    const index = at(key)
                    ctx.set(props.key, list().filter((_, i) => i !== index))
                    restore()
                  }}>
                  <Glyph paths={closeIcon} />
                </button>
              </div>
              <InputEditor variant="panel" label={`${itemName} starts at`} catalog={ctx.catalog()}
                input={{ types: [itemType()], required: true, description: 'Its value before the first iteration.' }}
                value={initial()} onChange={next => update(at(key), { initial: next })} />
            </div>
          )
        }}
      </For>
      <div class="hs-insp-declare">
        <input ref={nameInput} class="hs-input" aria-label={`New ${props.title.toLowerCase()} name`} placeholder="Name" value={name()} onInput={e => setName(e.currentTarget.value)}
          onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); add() } }} />
        <select class="hs-input" aria-label={`New ${props.title.toLowerCase()} type`} onChange={e => setType(e.currentTarget.value)}>
          <For each={literalTypes()}>{t => <option value={t} selected={t === type()}>{typeLabel(t, ctx.catalog())}</option>}</For>
        </select>
        <button type="button" class="hs-button hs-button--secondary" onClick={add}>Add</button>
      </div>
      <Show when={error()}><small class="hs-error" role="alert">{error()}</small></Show>
    </section>
  )
}
