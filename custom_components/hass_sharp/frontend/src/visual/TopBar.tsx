import { children, createSignal, createUniqueId, onCleanup, Show, type JSX } from 'solid-js'
import { Button, Icon } from '../components/Controls'
import { Menu, type MenuItem } from '../components/Menu'
import { ToolButton } from './flow/CanvasToolbar'
import { fullscreenIcons } from './fullscreen'
import { Glyph } from './Glyph'
import type { RunNotice } from './RunNow'

const historyIcons = { undo: ['M9 14 4 9l5-5', 'M4 9h10.5a5.5 5.5 0 0 1 0 11H11'], redo: ['M15 14l5-5-5-5', 'M20 9H9.5a5.5 5.5 0 0 0 0 11H13'] }
const plural = (count: number, word: string) => `${count} ${word}${count === 1 ? '' : 's'}`

export interface SaveState {
  saving: boolean
  dirty: boolean
  /** Problems that are errors. */
  errors: number
  /** The saved revision; undefined while the automation has never been saved. */
  revision?: number
}

/** The save-state chip's words (long, and short for the compact full-screen bar), tone and explanation. */
export function saveChip(state: SaveState) {
  const explanation = `${state.revision === undefined ? 'Not saved yet' : `Saved revision ${state.revision}`}${state.dirty ? '; there are unsaved changes' : ''}`
  if (state.saving) return { text: 'Saving…', short: 'Saving…', tone: 'busy', explanation }
  const problems = state.errors ? plural(state.errors, 'problem') : ''
  if (problems || state.dirty) {
    const text = [problems, state.dirty && 'Unsaved changes'].filter(Boolean).join(' · ')
    const short = [problems, state.dirty && 'Unsaved'].filter(Boolean).join(' · ')
    return { text, short, tone: problems ? 'danger' : 'dirty', explanation }
  }
  return state.revision === undefined
    ? { text: 'Not saved', short: 'Not saved', tone: 'dirty', explanation }
    : { text: 'Saved', short: 'Saved', tone: 'saved', explanation }
}

/**
 * The editor's top bar: back, the name edited in place, the save state, Undo and Redo, Run now, Save and a "⋯"
 * menu. In full screen it is the compact floating bar (name, save state, Undo, Redo, Save, "Exit full screen").
 *
 * The name is committed as it is typed, so the unsaved-changes guard and Save follow every keystroke; one visit to the
 * field is one undo step (a coalesce key per visit). Escape undoes that step, Enter leaves the field, and a name left
 * blank goes back to the one it had.
 *
 * Read-only (the run trace view): without `onRename` the name is a heading, `status` takes the save chip's place, and
 * Undo, Redo and Save are left out when their handlers are.
 */
export function TopBar(props: {
  name: string
  /** A typed name; every call of one visit to the field carries that visit's `coalesce` key. Absent: read-only. */
  onRename?(name: string, options: { coalesce: string }): void
  onBack?: () => void
  /** The Back button's name. */
  backLabel?: string
  save?: SaveState
  /** In place of the save chip. */
  status?: JSX.Element
  busy?: boolean
  canUndo?: boolean
  canRedo?: boolean
  onUndo?(): void
  onRedo?(): void
  canSave?: boolean
  onSave?(): void
  /** The Run now button (admins only). */
  run?: JSX.Element
  /** More actions before "Exit full screen" (each hides itself in full screen when it should). */
  actions?: JSX.Element
  notice?: RunNotice
  fullscreen: boolean
  onExitFullscreen(): void
  menu: MenuItem[]
}) {
  // One visit to the name field: its coalesce key, the name it started from, and whether it has made an undo step.
  let visits = 0
  let visit: { key: string; before: string; changed: boolean } | undefined
  const start = () => (visit ??= { key: `automation-name:${++visits}`, before: props.name, changed: false })
  /** Takes back this visit's undo step, which puts back the name it started from. */
  const restore = (input: HTMLInputElement) => {
    const current = start()
    if (current.changed) props.onUndo?.()
    current.changed = false
    input.value = props.name
  }
  const chip = () => saveChip(props.save ?? { saving: false, dirty: false, errors: 0 })
  const explanationId = createUniqueId()
  const run = children(() => props.run)
  return (
    <>
      <header class="hs-topbar">
        <Show when={props.onBack}>
          <button type="button" class="hs-topbar__icon hs-fs-hide" aria-label={props.backLabel ?? 'Back to automations'} title={props.backLabel ?? 'Back to automations'} onClick={() => props.onBack?.()}>
            <Icon name="arrow" />
          </button>
        </Show>
        <Show when={props.onRename} fallback={
          <div class="hs-topbar__title">
            <h1 class="hs-topbar__heading">{props.name}</h1>
          </div>
        }>
          <h1 class="hs-visually-hidden">{props.name}</h1>
          <div class="hs-topbar__title">
            <input
              class="hs-topbar__name"
              aria-label="Automation name"
              title="Automation name"
              spellcheck={false}
              autocomplete="off"
              enterkeyhint="done"
              value={props.name}
              disabled={props.busy}
              onFocus={() => start()}
              onInput={event => {
                const text = event.currentTarget.value, current = start()
                // A blank name is not committed; leaving the field blank puts the earlier one back.
                if (!text.trim() || text === props.name) return
                props.onRename?.(text, { coalesce: current.key })
                current.changed = true
              }}
              onKeyDown={event => {
                if (event.key === 'Enter') {
                  event.preventDefault()
                  event.currentTarget.blur()
                } else if (event.key === 'Escape' && visit && (visit.changed || event.currentTarget.value !== visit.before)) {
                  event.preventDefault()
                  restore(event.currentTarget)
                }
              }}
              onBlur={event => {
                if (!event.currentTarget.value.trim()) restore(event.currentTarget)
                visit = undefined
              }}
            />
          </div>
        </Show>
        <Show when={!props.status} fallback={props.status}>
          <span class={`hs-save-chip hs-save-chip--${chip().tone}`} tabindex="0" title={chip().explanation} aria-describedby={explanationId}>
            <span class="hs-save-chip__text" role="status">{chip().text}</span>
            <span class="hs-save-chip__short" aria-hidden="true">{chip().short}</span>
            <span id={explanationId} hidden>{chip().explanation}</span>
          </span>
        </Show>
        <div class="hs-topbar__actions">
          <Show when={props.onUndo}>
            <ToolButton label="Undo" shortcut="Control+Z" icon={historyIcons.undo} disabled={!props.canUndo} onClick={() => props.onUndo?.()} />
            <ToolButton label="Redo" shortcut="Control+Y" icon={historyIcons.redo} disabled={!props.canRedo} onClick={() => props.onRedo?.()} />
          </Show>
          <Show when={run()}>
            <div class="hs-fs-hide">{run()}</div>
          </Show>
          <Show when={props.onSave}>
            <Button variant="primary" class="hs-topbar__save" busy={props.save?.saving} disabled={!props.canSave} onClick={() => props.onSave?.()}>
              Save
            </Button>
          </Show>
          {props.actions}
          <Show when={props.fullscreen}>
            <Button class="hs-topbar__exit" aria-label="Exit full screen" title="Exit full screen (F)" onClick={() => props.onExitFullscreen()}>
              <Glyph paths={fullscreenIcons.exit} class="hs-topbar__exit-icon" />
              <span class="hs-topbar__exit-text">Exit full screen</span>
            </Button>
          </Show>
          <div class="hs-fs-hide hs-topbar__more">
            <Menu label="More actions" items={props.menu} />
          </div>
        </div>
      </header>
      <Show when={props.notice}>
        {current => (
          <p class="hs-topbar__notice" classList={{ 'hs-error': !!current().error }} role={current().error ? 'alert' : 'status'}>
            {current().text}
          </p>
        )}
      </Show>
    </>
  )
}

/** A notice that goes away by itself after a while (an error stays until the next one). */
export function createNotice(duration = 6000) {
  const [notice, setNotice] = createSignal<RunNotice>()
  let timer: ReturnType<typeof setTimeout> | undefined
  onCleanup(() => clearTimeout(timer))
  const show = (next: RunNotice) => {
    clearTimeout(timer)
    setNotice(next)
    if (!next.error) timer = setTimeout(() => setNotice(undefined), duration)
  }
  return [notice, show] as const
}
