import { createSignal, onCleanup, Show, type JSX } from 'solid-js'
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
 * The name is a draft while it is edited: Enter or leaving the field commits it as one undo step; Escape puts back
 * the name it had and leaves no history; an empty name is not kept.
 */
export function TopBar(props: {
  name: string
  onRename(name: string): void
  onBack?: () => void
  save: SaveState
  busy: boolean
  canUndo: boolean
  canRedo: boolean
  onUndo(): void
  onRedo(): void
  canSave: boolean
  onSave(): void
  /** The Run now button (admins only). */
  run?: JSX.Element
  notice?: RunNotice
  fullscreen: boolean
  onExitFullscreen(): void
  menu: MenuItem[]
}) {
  const [draft, setDraft] = createSignal<string>()
  let before = ''
  /** An edit starts from the name the automation has now. */
  const start = () => {
    if (draft() !== undefined) return
    before = props.name
    setDraft(props.name)
  }
  const commit = () => {
    const text = draft()
    if (text === undefined) return
    setDraft(undefined)
    const name = text.trim() ? text : before
    if (name !== props.name) props.onRename(name)
  }
  const chip = () => saveChip(props.save)
  return (
    <>
      <header class="hs-topbar">
        <Show when={props.onBack}>
          <button type="button" class="hs-topbar__icon hs-fs-hide" aria-label="Back to automations" title="Back to automations" onClick={() => props.onBack?.()}>
            <Icon name="arrow" />
          </button>
        </Show>
        <h1 class="hs-visually-hidden">{props.name}</h1>
        <div class="hs-topbar__title">
          <input
            class="hs-topbar__name"
            aria-label="Automation name"
            title="Automation name"
            spellcheck={false}
            autocomplete="off"
            enterkeyhint="done"
            value={draft() ?? props.name}
            disabled={props.busy}
            onFocus={() => start()}
            onInput={event => {
              const text = event.currentTarget.value
              start()
              setDraft(text)
            }}
            onKeyDown={event => {
              if (event.key === 'Enter') {
                event.preventDefault()
                commit()
                event.currentTarget.blur()
              } else if (event.key === 'Escape' && draft() !== undefined && draft() !== before) {
                event.preventDefault()
                setDraft(before)
                event.currentTarget.value = before
              }
            }}
            onBlur={commit}
          />
        </div>
        <span class={`hs-save-chip hs-save-chip--${chip().tone}`} role="status" tabindex="0" title={chip().explanation}>
          <span class="hs-save-chip__text" data-short={chip().short}>{chip().text}</span>
          <span class="hs-visually-hidden">. {chip().explanation}</span>
        </span>
        <div class="hs-topbar__actions">
          <ToolButton label="Undo" shortcut="Control+Z" icon={historyIcons.undo} disabled={!props.canUndo} onClick={props.onUndo} />
          <ToolButton label="Redo" shortcut="Control+Y" icon={historyIcons.redo} disabled={!props.canRedo} onClick={props.onRedo} />
          <Show when={props.run}>
            <div class="hs-fs-hide">{props.run}</div>
          </Show>
          <Button variant="primary" class="hs-topbar__save" busy={props.save.saving} disabled={!props.canSave} onClick={() => props.onSave()}>
            Save
          </Button>
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
