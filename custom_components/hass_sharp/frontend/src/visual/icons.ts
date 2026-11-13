import { nodeCatalog, type VisualCatalogSnapshot } from './contract'
import { stableHue } from './typeLabels'

/**
 * Inline SVG icons for node categories, drawn on a 24×24 grid as strokes (`fill="none"`, `stroke="currentColor"`).
 * No icon font and no `ha-icon`: they render the same in Home Assistant, tests and the preview.
 */
export const icons = {
  trigger: ['M13 2 4 14h7l-1 8 9-12h-7l1-8z'],
  hass: ['M3 11 12 3.5l9 7.5', 'M5.5 9.5V20h4.5v-5.5h4V20h4.5V9.5'],
  flow: ['M4 7h11', 'M12 3.5 15.5 7 12 10.5', 'M20 17H9', 'M12 13.5 8.5 17l3.5 3.5'],
  logic: ['M6 3v18', 'M6 8c0 4 12 1 12 6v7', 'M14 17l4 4 4-4'],
  data: ['M4 6.5C4 4.6 7.6 3 12 3s8 1.6 8 3.5S16.4 10 12 10 4 8.4 4 6.5z', 'M4 6.5v11C4 19.4 7.6 21 12 21s8-1.6 8-3.5v-11', 'M4 12c0 1.9 3.6 3.5 8 3.5s8-1.6 8-3.5'],
  code: ['M8.5 7 3.5 12l5 5', 'M15.5 7l5 5-5 5', 'M13.5 4.5l-3 15'],
  library: ['M12 2.8 20 7.3v9.4l-8 4.5-8-4.5V7.3z', 'M4 7.3l8 4.5 8-4.5', 'M12 11.8v9.4'],
  /** Error badge: a circle with a cross (warnings use the triangle, so the two never differ by colour alone). */
  problem: ['M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z', 'M9 9l6 6', 'M15 9l-6 6'],
  /** Warning badge: a triangle with an exclamation mark. */
  warning: ['M12 3.5 21.5 20h-19z', 'M12 10v4.5', 'M12 17.2v.3'],
  /** Trace statuses (run view): the icon beside the status word. */
  traceDone: ['M5 12.5l4.5 4.5L19 7.5'],
  traceRecovered: ['M4 12a8 8 0 1 0 2.4-5.7', 'M4 4v4.5h4.5', 'M9 12.5l2 2 4-4'],
  traceFailed: ['M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z', 'M9 9l6 6', 'M15 9l-6 6'],
  traceRunning: ['M12 3a9 9 0 1 1-9 9', 'M12 7.5V12l3 2'],
  traceCancelled: ['M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z', 'M5.6 5.6l12.8 12.8'],
  traceStopped: ['M7 7h10v10H7z'],
  traceUnreached: ['M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z', 'M8 12h8'],
} as const satisfies Record<string, readonly string[]>
export type IconName = keyof typeof icons

/** How a node kind looks: its category key, colour (a CSS value from the tokens) and icon. */
export interface NodeIdentity {
  /** `trigger` for trigger nodes (they must look different from actions), else the lower-case catalog category. */
  key: string
  /** The catalog category, for people ("Hass", "Flow", …), or "Trigger". */
  label: string
  color: string
  icon: readonly string[]
}

const known: Record<string, IconName> = { hass: 'hass', flow: 'flow', logic: 'logic', data: 'data', code: 'code' }
/**
 * Category identity of a node kind. The built-in categories have their own colour token; any other category (a
 * library's) gets a stable hue derived from its name and the library icon.
 */
export function nodeIdentity(kind: string, catalog: VisualCatalogSnapshot = nodeCatalog): NodeIdentity {
  if (kind === 'trigger') return { key: 'trigger', label: 'Trigger', color: 'var(--hs-cat-trigger)', icon: icons.trigger }
  const category = Object.hasOwn(catalog.nodes, kind) ? catalog.nodes[kind].category : 'Library'
  const key = category.toLowerCase()
  if (Object.hasOwn(known, key)) return { key, label: category, color: `var(--hs-cat-${key})`, icon: icons[known[key]] }
  return {
    key: 'library',
    label: category,
    color: `color-mix(in oklab, oklch(60% 0.14 ${stableHue(category)}) var(--hs-hue-mix), var(--hs-text))`,
    icon: icons.library,
  }
}
