import { createSignal, For, Show } from 'solid-js'

type Container = Record<string, unknown> | unknown[]
/** Members shown per level before an "N more" row; each press of it shows this many more. */
const PAGE = 100
const isContainer = (value: unknown): value is Container => value !== null && typeof value === 'object'
const entries = (value: Container): [string, unknown][] => Array.isArray(value) ? value.map((item, i) => [String(i), item]) : Object.entries(value)
const kind = (value: unknown) => value === null ? 'null' : typeof value
const scalar = (value: unknown) => value === undefined ? 'undefined' : JSON.stringify(value) ?? String(value)
const count = (value: Container) => Array.isArray(value)
  ? `${value.length} ${value.length === 1 ? 'item' : 'items'}`
  : `${Object.keys(value).length} ${Object.keys(value).length === 1 ? 'key' : 'keys'}`

/**
 * Read-only, collapsible view of JSON values. Nested objects and arrays start collapsed and are built when first
 * opened; each level shows at most 100 members at a time.
 */
export const JsonTree = (props: { value: unknown; label?: string }) =>
  <div class="hs-json-tree" role="group" aria-label={props.label ?? 'Raw values'}>
    <Show when={isContainer(props.value)} fallback={<Scalar value={props.value} />}>
      <Members value={props.value as Container} />
    </Show>
  </div>

const Scalar = (props: { value: unknown }) => <span class={`hs-json-${kind(props.value)}`}>{scalar(props.value)}</span>

const Members = (props: { value: Container }) => {
  const [shown, setShown] = createSignal(PAGE)
  const all = () => entries(props.value)
  const rest = () => all().length - shown()
  return <Show when={all().length} fallback={<span class="hs-muted">{Array.isArray(props.value) ? 'Empty list' : 'Empty object'}</span>}>
    <ul>
      <For each={all().slice(0, shown())}>{([name, value]) => <li><Member name={name} value={value} /></li>}</For>
      <Show when={rest() > 0}>
        <li class="hs-json-more"><button type="button" class="hs-json-more__button" onClick={() => setShown(n => n + PAGE)}>{rest()} more</button></li>
      </Show>
    </ul>
  </Show>
}

const Member = (props: { name: string; value: unknown }) => {
  // Built on first open, then kept, so what was opened inside stays open.
  const [opened, setOpened] = createSignal(false)
  return <Show when={isContainer(props.value)} fallback={<span class="hs-json-leaf"><span class="hs-json-key">{props.name}</span> <Scalar value={props.value} /></span>}>
    <details onToggle={event => { if (event.currentTarget.open) setOpened(true) }}>
      <summary><span class="hs-json-key">{props.name}</span> <span class="hs-muted">{count(props.value as Container)}</span></summary>
      <Show when={opened()}><Members value={props.value as Container} /></Show>
    </details>
  </Show>
}
