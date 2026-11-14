import { For, Show } from 'solid-js'
import type { RecentRun } from '../visual/api'
import { runsAlternative, runTone } from './model'

/**
 * The most recent runs as a row of dots, oldest first and newest last, in the run history's colours and shapes. One
 * text alternative for the row ("Last 10 runs: 8 succeeded, 2 failed"); the dots themselves are not announced.
 */
export function RunDots(props: { runs: readonly RecentRun[] }) {
  return (
    <Show when={props.runs.length}>
      <span class="hs-run-dots" role="img" aria-label={runsAlternative(props.runs)} title={runsAlternative(props.runs)}>
        <For each={props.runs}>{run => <span class={`hs-run-dot hs-run-dot--${runTone(run.status)}`} aria-hidden="true" />}</For>
      </span>
    </Show>
  )
}
