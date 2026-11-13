import { createSignal, onCleanup } from 'solid-js'
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
