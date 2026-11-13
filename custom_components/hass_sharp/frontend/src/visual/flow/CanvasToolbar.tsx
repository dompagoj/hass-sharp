import { For, Show, type JSX } from 'solid-js'
import { Panel } from '@dschz/solid-flow'

/** Toolbar and wire icons, drawn on a 24×24 grid as strokes like the category icons. */
export const toolIcons = {
  add: ['M12 5v14', 'M5 12h14'],
  zoomIn: ['M10.5 4a6.5 6.5 0 1 0 0 13a6.5 6.5 0 1 0 0-13z', 'M20 20l-4.8-4.8', 'M10.5 7.5v6', 'M7.5 10.5h6'],
  zoomOut: ['M10.5 4a6.5 6.5 0 1 0 0 13a6.5 6.5 0 1 0 0-13z', 'M20 20l-4.8-4.8', 'M7.5 10.5h6'],
  fit: ['M4 9V4h5', 'M15 4h5v5', 'M20 15v5h-5', 'M9 20H4v-5', 'M9 9h6v6H9z'],
  tidy: ['M3 5h6v5H3z', 'M15 4h6v5h-6z', 'M15 15h6v5h-6z', 'M9 7.5h3', 'M12 6.5v11a1 1 0 0 0 1 1h2', 'M12 6.5h3'],
  snap: ['M4 4h16v16H4z', 'M4 9.3h16', 'M4 14.7h16', 'M9.3 4v16', 'M14.7 4v16'],
  minimap: ['M3 5h18v14H3z', 'M12.5 11.5h6v5h-6z'],
  remove: ['M6 6l12 12', 'M18 6 6 18'],
} as const satisfies Record<string, readonly string[]>

export const ToolIcon = (props: { paths: readonly string[] }) => (
  <svg class="hs-tool__glyph" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
    <For each={props.paths}>{d => <path d={d} />}</For>
  </svg>
)

/** One icon button: its name is its accessible name and its tooltip (with the shortcut, when it has one). */
export function ToolButton(props: { label: string; shortcut?: string; icon: readonly string[]; pressed?: boolean; disabled?: boolean; onClick: () => void }) {
  return (
    <button
      type="button"
      class="hs-tool"
      aria-label={props.label}
      aria-pressed={props.pressed === undefined ? undefined : props.pressed}
      aria-keyshortcuts={props.shortcut}
      data-tip={props.shortcut ? `${props.label} (${props.shortcut})` : props.label}
      disabled={props.disabled}
      on:click={() => props.onClick()}
    >
      <ToolIcon paths={props.icon} />
    </button>
  )
}

export interface CanvasToolbarProps {
  readOnly: boolean
  snap: boolean
  minimap: boolean
  onAdd(): void
  onZoomIn(): void
  onZoomOut(): void
  onFit(): void
  onTidy(): void
  onSnap(): void
  onMinimap(): void
  /** Extra buttons at the end (the editor page's "Full screen"); `ToolButton` matches their look. */
  children?: JSX.Element
}

/**
 * The canvas's floating toolbar: add a step, zoom, fit, tidy, snap to grid, minimap, and a slot at the end. Editing
 * tools (add, tidy, snap) are left out of a read-only canvas; zoom, fit and the minimap stay.
 */
export function CanvasToolbar(props: CanvasToolbarProps) {
  return (
    <Panel position="bottom-left" class="hs-canvas-toolbar nodrag nopan" role="group" aria-label="Canvas tools">
      <Show when={!props.readOnly}>
        <ToolButton label="Add a step" shortcut="A" icon={toolIcons.add} onClick={props.onAdd} />
        <span class="hs-canvas-toolbar__rule" aria-hidden="true" />
      </Show>
      <ToolButton label="Zoom in" icon={toolIcons.zoomIn} onClick={props.onZoomIn} />
      <ToolButton label="Zoom out" icon={toolIcons.zoomOut} onClick={props.onZoomOut} />
      <ToolButton label="Fit to view" icon={toolIcons.fit} onClick={props.onFit} />
      <Show when={!props.readOnly}>
        <ToolButton label="Tidy layout" icon={toolIcons.tidy} onClick={props.onTidy} />
        <ToolButton label="Snap to grid" icon={toolIcons.snap} pressed={props.snap} onClick={props.onSnap} />
      </Show>
      <ToolButton label="Minimap" icon={toolIcons.minimap} pressed={props.minimap} onClick={props.onMinimap} />
      {props.children}
    </Panel>
  )
}
