import { For } from 'solid-js'

/** A 24×24 stroke icon drawn from path data, such as the `icons.ts` drawings. */
export const Glyph = (props: { paths: readonly string[]; class?: string }) => (
  <svg class={props.class ?? 'hs-insp-glyph'} viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
    <For each={props.paths}>{d => <path d={d} />}</For>
  </svg>
)
