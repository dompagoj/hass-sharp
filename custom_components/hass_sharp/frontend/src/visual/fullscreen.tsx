import { createEffect, createSignal, on, onCleanup } from 'solid-js'
import { ToolButton } from './flow/CanvasToolbar'

/** Drawn on a 24×24 grid as strokes, like the canvas toolbar's icons. */
export const fullscreenIcons = {
  enter: ['M4 9V4h5', 'M15 4h5v5', 'M20 15v5h-5', 'M9 20H4v-5'],
  exit: ['M9 4v5H4', 'M20 9h-5V4', 'M15 20v-5h5', 'M4 15h5v5'],
} as const

export type FullscreenMode = 'off' | 'native' | 'maximised'
type Root = Document | ShadowRoot

/**
 * Full screen for one element (an editor's root): the browser's Fullscreen API when it is there and allowed, else a
 * CSS "maximised" mode the caller styles from `maximised()` (fixed to the viewport, above Home Assistant's chrome;
 * iPhone Safari and some embedded webviews have no Fullscreen API for elements). `active()` follows the browser:
 * leaving full screen with its own control or Escape turns it off. Nothing is remembered across page loads.
 */
export function createFullscreen(element: () => HTMLElement | undefined) {
  const [mode, setMode] = createSignal<FullscreenMode>('off')
  // The element may sit in Home Assistant's shadow roots; its own root names it, the document only names the host.
  const root = (el: HTMLElement): Root => el.getRootNode() as Root
  const current = () => {
    const el = element()
    return el ? root(el).fullscreenElement === el || el.ownerDocument.fullscreenElement === el : false
  }
  const changed = () => {
    if (current()) setMode('native')
    else if (mode() === 'native') setMode('off')
  }
  document.addEventListener('fullscreenchange', changed)
  onCleanup(() => {
    document.removeEventListener('fullscreenchange', changed)
    if (current()) void document.exitFullscreen?.().catch(() => {})
  })
  const enter = async () => {
    const el = element()
    if (!el || mode() !== 'off') return
    if (typeof el.requestFullscreen === 'function' && el.ownerDocument.fullscreenEnabled !== false) {
      try {
        await el.requestFullscreen({ navigationUI: 'hide' })
        setMode(current() ? 'native' : 'maximised')
        return
      } catch {
        // Refused (no user gesture, a sandboxed frame, a permissions policy): maximise instead.
      }
    }
    setMode('maximised')
  }
  const exit = async () => {
    const was = mode()
    setMode('off')
    if (was === 'native' && current()) await element()!.ownerDocument.exitFullscreen().catch(() => {})
  }
  return {
    mode,
    active: () => mode() !== 'off',
    maximised: () => mode() === 'maximised',
    enter,
    exit,
    toggle: () => (mode() === 'off' ? enter() : exit()),
  }
}
export type Fullscreen = ReturnType<typeof createFullscreen>

/**
 * Full screen for a page (the editor, a run's trace): entering remembers what had focus and calls `onEnter` first (to
 * fold away what full screen hides); `F` toggles and Escape leaves the maximised fallback (through {@link
 * FullscreenPage.keydown}, called from the page root's key handler). Focus never falls to the page: entering moves it
 * from what folded away to the bar's "Exit full screen", and leaving moves it from there back to what opened full
 * screen, else to `fallback()`.
 */
export function createFullscreenPage(options: {
  root: () => HTMLElement
  fullscreen: Fullscreen
  onEnter?(): void
  /** After leaving, before focus moves. */
  onExit?(): void
  /** Where focus goes when what opened full screen is gone (the canvas). */
  fallback(): HTMLElement | null | undefined
}) {
  const { fullscreen } = options
  let opener: HTMLElement | null = null
  const activeElement = () => (options.root().getRootNode() as Document | ShadowRoot).activeElement as HTMLElement | null
  const refocus = (element: HTMLElement | null) =>
    element?.isConnected && element !== options.root().ownerDocument.body ? element.focus({ preventScroll: true }) : options.fallback()?.focus({ preventScroll: true })
  const enter = () => {
    opener = activeElement()
    options.onEnter?.()
    void fullscreen.enter()
  }
  const toggle = () => (fullscreen.active() ? void fullscreen.exit() : enter())
  createEffect(
    on(
      fullscreen.active,
      active => {
        const root = options.root(), focused = activeElement()
        const lost = !focused || focused === root.ownerDocument.body
        if (active) {
          // What had focus may be folded away (the "⋯" menu, the library, the drawer): the bar's exit button takes it.
          if (lost || focused.closest('.hs-fs-hide, .hs-lib__panel, .hs-drawer__panel')) queueMicrotask(() => root.querySelector<HTMLElement>('.hs-topbar__exit')?.focus())
          return
        }
        options.onExit?.()
        // The exit button goes away with full screen: back to what opened it, else the fallback.
        if (lost || focused.closest('.hs-topbar__exit')) queueMicrotask(() => refocus(opener))
      },
      { defer: true },
    ),
  )
  /** `F` toggles full screen (true: the key was used); Escape leaves the maximised fallback. */
  const keydown = (event: KeyboardEvent): boolean => {
    const key = event.key.length === 1 ? event.key.toLowerCase() : event.key
    if (!event.ctrlKey && !event.metaKey && !event.altKey && key === 'f') {
      event.preventDefault()
      if (!event.repeat) toggle()
      return true
    }
    // Escape leaves the maximised fallback only when nothing on the way (a menu, the drawer, the canvas) used it.
    // The browser's own full screen leaves on Escape by itself.
    if (event.key === 'Escape' && fullscreen.maximised()) setTimeout(() => !event.defaultPrevented && fullscreen.maximised() && void fullscreen.exit())
    return false
  }
  return { enter, toggle, keydown }
}
export type FullscreenPage = ReturnType<typeof createFullscreenPage>

/** The canvas toolbar's "Full screen" toggle (shortcut F), pressed while full screen is on. */
export function FullscreenButton(props: { fullscreen: Fullscreen; onToggle?: () => void }) {
  return (
    <ToolButton
      label={props.fullscreen.active() ? 'Exit full screen' : 'Full screen'}
      shortcut="F"
      icon={props.fullscreen.active() ? fullscreenIcons.exit : fullscreenIcons.enter}
      pressed={props.fullscreen.active()}
      onClick={() => (props.onToggle ? props.onToggle() : void props.fullscreen.toggle())}
    />
  )
}
