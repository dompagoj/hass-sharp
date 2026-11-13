import { createMemo, createSignal, For, Show, type JSX } from 'solid-js'

type Container = Record<string, unknown> | unknown[]
/**
 * Shows a member that is not an object or a list in place of its JSON text, or returns undefined to keep the JSON
 * text (the trace view uses it for the recorder's placeholders).
 */
export type JsonLeaf = (name: string, value: unknown) => JSX.Element | undefined
/** Members shown per level before an "N more" row; each press of it shows this many more. */
const PAGE = 100
const isContainer = (value: unknown): value is Container => value !== null && typeof value === 'object'
const names = (value: Container): string[] => (Array.isArray(value) ? value.map((_, i) => String(i)) : Object.keys(value))
const member = (value: Container, name: string): unknown => (Array.isArray(value) ? value[Number(name)] : value[name])
const kind = (value: unknown) => value === null ? 'null' : typeof value
const scalar = (value: unknown) => value === undefined ? 'undefined' : JSON.stringify(value) ?? String(value)
const count = (value: Container) => Array.isArray(value)
  ? `${value.length} ${value.length === 1 ? 'item' : 'items'}`
  : `${Object.keys(value).length} ${Object.keys(value).length === 1 ? 'field' : 'fields'}`

/**
 * Read-only, collapsible view of JSON values. Nested objects and arrays start collapsed and are built when first
 * opened; each level shows at most 100 members at a time. Members are kept by name, so a new value with the same
 * shape (a live run read again) keeps what was opened.
 */
export const JsonTree = (props: { value: unknown; label?: string; leaf?: JsonLeaf }) =>
  <div class="hs-json-tree" role="group" aria-label={props.label ?? 'Raw values'}>
    <Show when={isContainer(props.value)} fallback={<Scalar value={props.value} />}>
      <Members value={props.value as Container} leaf={props.leaf} />
    </Show>
  </div>

const Scalar = (props: { value: unknown }) => <span class={`hs-json-${kind(props.value)}`}>{scalar(props.value)}</span>

const Members = (props: { value: Container; leaf?: JsonLeaf }) => {
  let list: HTMLUListElement | undefined
  const [shown, setShown] = createSignal(PAGE)
  const all = createMemo(() => names(props.value))
  const rest = () => all().length - shown()
  const more = () => {
    const first = shown()
    setShown(n => n + PAGE)
    // The last press takes its button away: focus goes to the first member it showed.
    if (rest() <= 0) {
      const item = list?.children[first] as HTMLElement | undefined
      const target = item?.querySelector<HTMLElement>('summary') ?? item
      if (target && target === item) target.tabIndex = -1
      target?.focus()
    }
  }
  return <Show when={all().length} fallback={<span class="hs-muted">{Array.isArray(props.value) ? 'Empty list' : 'Empty object'}</span>}>
    <ul ref={list}>
      <For each={all().slice(0, shown())}>{name => <li><Member name={name} value={member(props.value, name)} leaf={props.leaf} /></li>}</For>
      <Show when={rest() > 0}>
        <li class="hs-json-more"><button type="button" class="hs-json-more__button" onClick={more}>{rest()} more</button></li>
      </Show>
    </ul>
  </Show>
}

const Member = (props: { name: string; value: unknown; leaf?: JsonLeaf }) => {
  // Built on first open, then kept, so what was opened inside stays open.
  const [opened, setOpened] = createSignal(false)
  return <Show when={isContainer(props.value)} fallback={
    <span class="hs-json-leaf"><span class="hs-json-key">{props.name}</span> {props.leaf?.(props.name, props.value) ?? <Scalar value={props.value} />}</span>
  }>
    <details onToggle={event => { if (event.currentTarget.open) setOpened(true) }}>
      <summary><span class="hs-json-key">{props.name}</span> <span class="hs-muted">{count(props.value as Container)}</span></summary>
      <Show when={opened()}><Members value={props.value as Container} leaf={props.leaf} /></Show>
    </details>
  </Show>
}
