import { For, Show } from 'solid-js'

type Container = Record<string, unknown> | unknown[]
const isContainer = (value: unknown): value is Container => value !== null && typeof value === 'object'
const entries = (value: Container): [string, unknown][] => Array.isArray(value) ? value.map((item, i) => [String(i), item]) : Object.entries(value)
const kind = (value: unknown) => value === null ? 'null' : typeof value
const scalar = (value: unknown) => value === undefined ? 'undefined' : JSON.stringify(value) ?? String(value)
const count = (value: Container) => Array.isArray(value)
  ? `${value.length} ${value.length === 1 ? 'item' : 'items'}`
  : `${Object.keys(value).length} ${Object.keys(value).length === 1 ? 'key' : 'keys'}`

/** Read-only, collapsible view of JSON values. Nested objects and arrays start collapsed. */
export const JsonTree = (props: { value: unknown; label?: string }) =>
  <div class="hs-json-tree" role="group" aria-label={props.label ?? 'Raw values'}>
    <Show when={isContainer(props.value)} fallback={<Scalar value={props.value} />}>
      <Members value={props.value as Container} />
    </Show>
  </div>

const Scalar = (props: { value: unknown }) => <span class={`hs-json-${kind(props.value)}`}>{scalar(props.value)}</span>

const Members = (props: { value: Container }) =>
  <Show when={entries(props.value).length} fallback={<span class="hs-muted">{Array.isArray(props.value) ? 'Empty list' : 'Empty object'}</span>}>
    <ul><For each={entries(props.value)}>{([name, value]) => <li><Member name={name} value={value} /></li>}</For></ul>
  </Show>

const Member = (props: { name: string; value: unknown }) =>
  <Show when={isContainer(props.value)} fallback={<span class="hs-json-leaf"><span class="hs-json-key">{props.name}</span> <Scalar value={props.value} /></span>}>
    <details>
      <summary><span class="hs-json-key">{props.name}</span> <span class="hs-muted">{count(props.value as Container)}</span></summary>
      <Members value={props.value as Container} />
    </details>
  </Show>
