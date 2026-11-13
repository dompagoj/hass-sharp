import { createUniqueId, createSignal, createEffect, on, For, Show } from 'solid-js'
import { EntityAutocomplete } from '../components/EntityAutocomplete'
import { Field } from '../components/Controls'
import { typeLabel, typeColor } from './graph'
import { refinesType, resolveTypeSpec, type InputSpec, type JsonValue, type Operand, type VisualCatalogSnapshot } from './contract'
export function ConfigSelect(props: {
  label: string
  value: string
  options: readonly (readonly [string, string])[]
  onChange: (value: string) => void
}) {
  const id = createUniqueId()
  return (
    <div class="hs-field">
      <label for={id}>{props.label}</label>
      <select id={id} aria-label={props.label} class="hs-input" value={props.value} onChange={e => props.onChange(e.currentTarget.value)}>
        <For each={props.options}>
          {([value, label]) => (
            <option value={value} selected={value === props.value}>
              {label}
            </option>
          )}
        </For>
      </select>
    </div>
  )
}
export function JsonField(props: { label: string; value: unknown; onChange: (value: unknown) => void; hint?: string }) {
  const id = createUniqueId()
  const text = () => (typeof props.value === 'string' ? props.value : JSON.stringify(props.value === undefined ? {} : props.value, null, 2))
  const valid = () => {
    try {
      JSON.parse(text())
      return true
    } catch {
      return false
    }
  }
  return (
    <div class="hs-field">
      <label for={id}>{props.label}</label>
      <textarea
        id={id}
        aria-label={props.label}
        class="hs-input hs-code-input"
        rows="4"
        value={text()}
        aria-invalid={!valid() || undefined}
        onInput={e => {
          const raw = e.currentTarget.value
          try {
            props.onChange(JSON.parse(raw))
          } catch {
            props.onChange(raw)
          }
        }}
      />
      <Show when={!valid()}>
        <small class="hs-error">Enter valid JSON.</small>
      </Show>
      <Show when={props.hint}>
        <small class="hs-muted">{props.hint}</small>
      </Show>
    </div>
  )
}
const defaultLiteral = (kind: string | null | undefined): JsonValue =>
  kind === 'boolean' ? false : kind === 'number' || kind === 'integer' ? 0 : kind === 'object' ? {} : kind === 'array' ? [] : ''
function NumericLiteral(props: { label: string; value: unknown; onChange: (value: JsonValue) => void }) {
  const [draft, setDraft] = createSignal(String(props.value ?? ''))
  createEffect(
    on(
      () => props.value,
      value => {
        if (Number(draft()) !== value || draft().trim() === '') setDraft(String(value ?? ''))
      },
    ),
  )
  const valid = () =>
    draft().trim() !== '' && /^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$/.test(draft()) && Number.isFinite(Number(draft()))
  return (
    <div>
      <Field
        label={props.label}
        aria-label={props.label}
        inputmode="decimal"
        value={draft()}
        aria-invalid={!valid() || undefined}
        onInput={event => {
          const raw = event.currentTarget.value
          setDraft(raw)
          props.onChange(valid() ? Number(raw) : raw)
        }}
      />
      <Show when={!valid()}>
        <small class="hs-error">Enter a finite number.</small>
      </Show>
    </div>
  )
}
export function ValueOperand(props: {
  label: string
  value: unknown
  spec: InputSpec
  variables?: string[]
  catalog: VisualCatalogSnapshot
  onChange: (value: Operand) => void
}) {
  const variableId = createUniqueId()
  const operand = () => props.value as Operand | undefined
  const mode = () => operand()?.kind ?? 'unset'
  const type = () => (operand()?.kind === 'literal' ? (operand() as Extract<Operand, { kind: 'literal' }>).type : props.spec.types[0])
  const value = () => (operand()?.kind === 'literal' ? (operand() as Extract<Operand, { kind: 'literal' }>).value : undefined)
  const types = () =>
    props.spec.types.filter(id => !!resolveTypeSpec(id, props.catalog)?.literalKind || resolveTypeSpec(id, props.catalog)?.nullable === true)
  const initialValue = (id: string): JsonValue => {
    const spec = resolveTypeSpec(id, props.catalog)!
    return !spec.literalKind && spec.nullable
      ? null
      : spec.literalKind === 'enum'
        ? (spec.enumValues?.[0] ?? '')
        : defaultLiteral(spec.literalKind)
  }
  const spec = () => resolveTypeSpec(type(), props.catalog)
  const change = (value: JsonValue) => props.onChange({ kind: 'literal', type: type(), value })
  return (
    <div class="hs-operand" style={{ '--hs-value-color': typeColor(type(), props.catalog) }}>
      <ConfigSelect
        label={`${props.label} source`}
        value={mode()}
        options={[['unset', 'Unset'], ...(types().length ? [['literal', 'Literal'] as const] : []), ['variable', 'Named variable']]}
        onChange={kind =>
          props.onChange(
            kind === 'variable'
              ? { kind: 'variable', name: '' }
              : kind === 'literal'
                ? { kind: 'literal', type: types()[0], value: initialValue(types()[0]) }
                : { kind: 'unset' },
          )
        }
      />
      <Show when={mode() === 'unset'}>
        <p class="hs-muted">
          Unset.{' '}
          {types().length ? 'Set a literal or connect a typed output.' : 'Wire-only type: connect a factory or an existing typed output.'}
        </p>
      </Show>
      <Show when={mode() === 'variable'}>
        <Field
          label={`${props.label} variable`}
          aria-label={`${props.label} variable`}
          value={(operand() as { name?: string })?.name ?? ''}
          list={variableId}
          onInput={e => props.onChange({ kind: 'variable', name: e.currentTarget.value })}
        />
        <datalist id={variableId}>
          <For each={props.variables ?? []}>{name => <option value={name} />}</For>
        </datalist>
      </Show>
      <Show when={mode() === 'literal'}>
        <Show when={types().length > 1}>
          <ConfigSelect
            label={`${props.label} literal type`}
            value={type()}
            options={types().map(id => [id, typeLabel(id, props.catalog)] as const)}
            onChange={id => props.onChange({ kind: 'literal', type: id, value: initialValue(id) })}
          />
        </Show>
        <Show when={spec()?.nullable}>
          <button type="button" class="hs-button hs-button--ghost" onClick={() => change(null)}>
            Set null
          </button>
        </Show>
        <Show when={value() === null}>
          <p class="hs-muted">Explicit null</p>
          <Show when={spec()?.literalKind}>
            <button type="button" class="hs-button hs-button--ghost" onClick={() => change(initialValue(type()))}>
              Set a value
            </button>
          </Show>
        </Show>
        <Show when={value() !== null}>
          <Show when={spec()?.literalKind === 'boolean'}>
            <ConfigSelect
              label={props.label}
              value={String(value())}
              options={[
                ['true', 'True'],
                ['false', 'False'],
              ]}
              onChange={v => change(v === 'true')}
            />
          </Show>
          <Show when={spec()?.literalKind === 'enum'}>
            <ConfigSelect
              label={props.label}
              value={String(value())}
              options={(spec()?.enumValues ?? []).map(v => [String(v), String(v)] as const)}
              onChange={v => change(spec()?.enumValues?.find(item => String(item) === v) ?? v)}
            />
          </Show>

          <Show when={['number', 'integer'].includes(spec()?.literalKind ?? '')}>
            <NumericLiteral label={props.label} value={value()} onChange={change} />
          </Show>
          <Show when={spec()?.literalKind === 'string'}>
            <Show
              when={refinesType(type(), 'entity_id', props.catalog)}
              fallback={
                <Field
                  label={props.label}
                  aria-label={props.label}
                  value={String(value() ?? '')}
                  onInput={e => change(e.currentTarget.value)}
                />
              }
            >
              <EntityAutocomplete value={String(value() ?? '')} onChange={change} />
            </Show>
          </Show>
          <Show when={['object', 'array'].includes(spec()?.literalKind ?? '')}>
            <JsonField label={props.label} value={value()} onChange={v => change(v as JsonValue)} />
          </Show>
        </Show>
      </Show>
    </div>
  )
}
