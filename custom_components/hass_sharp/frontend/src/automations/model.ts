import type { GraphOutline, HomeAutomation, RecentRun } from '../visual/api'
import type { Graph } from '../visual/contract'
import { nodeSummary, type SummaryNames } from '../visual/nodeSummary'
import type { RunStatus } from '../types'

/**
 * The outline as a graph, for the editor's own helpers (trigger summaries, Run now): nodes without config keep only
 * their kind and label; edges are the execution wires.
 */
export function outlineGraph(outline: GraphOutline): Graph {
  const nodes = outline.nodes.map((node, index) => ({
    id: node.id ?? `#${index}`, kind: node.kind, config: node.config ?? {}, position: { x: node.x, y: node.y },
    ...(node.label ? { label: node.label } : {}),
  }))
  const edges = outline.edges.flatMap(([from, to], index) =>
    nodes[from] && nodes[to] ? [{ id: `#e${index}`, source: nodes[from].id, target: nodes[to].id }] : [])
  return { version: 5, name: outline.name, nodes, edges }
}

/** What starts the automation, one line per trigger, as the editor's overview says it ("Starts when"). */
export const triggerSummaries = (graph: Graph, names?: SummaryNames) =>
  graph.nodes.filter(node => node.kind === 'trigger').map(node => nodeSummary(node, undefined, names, graph))

/** The colour family of a run status, as the run history draws it. */
export type RunTone = 'succeeded' | 'failed' | 'cancelled' | 'stopped' | 'skipped' | 'running'
export const runTone = (status: RunStatus): RunTone =>
  status === 'Succeeded' ? 'succeeded'
    : status === 'Failed' || status === 'Interrupted' ? 'failed'
      : status === 'Cancelled' ? 'cancelled'
        : status === 'Stopped' ? 'stopped'
          : status === 'Skipped' ? 'skipped'
            : 'running'
const order: RunTone[] = ['succeeded', 'failed', 'cancelled', 'stopped', 'skipped', 'running']
/** The run dots in words: "Last 10 runs: 8 succeeded, 2 failed"; "Last run: succeeded". */
export function runsAlternative(runs: readonly RecentRun[]): string {
  if (!runs.length) return 'No runs yet'
  const counts = new Map<RunTone, number>()
  for (const run of runs) counts.set(runTone(run.status), (counts.get(runTone(run.status)) ?? 0) + 1)
  // The tone names are the words.
  const parts = order.filter(tone => counts.has(tone)).map(tone => `${counts.get(tone)} ${tone}`)
  if (runs.length === 1) return `Last run: ${runTone(runs[0].status)}`
  return `Last ${runs.length} runs: ${parts.join(', ')}`
}

const relativeFormat = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
const localDay = (time: number) => { const day = new Date(time); day.setHours(0, 0, 0, 0); return day.getTime() }
/** "just now", "5 minutes ago", "3 hours ago", "yesterday", "4 days ago", then a date. Days are calendar days here,
 * so "yesterday" means the day before today, however many hours ago that was. */
export function relativeTime(iso: string, now: number): string {
  const then = Date.parse(iso)
  const seconds = (then - now) / 1000
  if (Number.isNaN(seconds)) return iso
  if (seconds > -45) return 'just now'
  if (-seconds < 60 * 45) return relativeFormat.format(Math.round(seconds / 60), 'minute')
  // Rounded, so a day with a daylight saving change still counts as one.
  const days = Math.round((localDay(now) - localDay(then)) / 86_400_000)
  if (-seconds < 3600 * 22 || days <= 0) return relativeFormat.format(Math.round(seconds / 3600), 'hour')
  if (days < 26) return relativeFormat.format(-days, 'day')
  return new Date(iso).toLocaleDateString()
}
export const durationText = (ms: number) =>
  ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toLocaleString(undefined, { maximumFractionDigits: 1 })} s`

const live = (run: RecentRun | undefined) => run?.status === 'Pending' || run?.status === 'Running'
export const isRunning = (item: HomeAutomation) => live(item.recentRuns.at(-1))
/** The last-run line: "Running now", "Off", "Ran 5 minutes ago · 32 ms" or "Has not run yet". */
export function lastRunText(item: HomeAutomation, now: number): string {
  const last = item.recentRuns.at(-1)
  if (live(last)) return last?.startUtc ? `Running since ${relativeTime(last.startUtc, now)}` : 'Starting now'
  if (!item.enabled) return 'Off'
  const when = last?.startUtc ?? item.lastRunUtc
  if (!when) return 'Has not run yet'
  return `Ran ${relativeTime(when, now)}${last?.durationMs != null ? ` · ${durationText(last.durationMs)}` : ''}`
}

const plural = (count: number, word: string) => `${count} ${word}${count === 1 ? '' : 's'}`
const lastFailed = (item: HomeAutomation) => runTone(item.recentRuns.at(-1)?.status ?? 'Succeeded') === 'failed'
/** Something wrong, in words: the saved version is not active, or the most recent run failed; undefined when all is well. */
export function healthText(item: HomeAutomation): string | undefined {
  if (!item.currentRevisionActive) return item.problemCount ? `Not active: ${plural(item.problemCount, 'problem')}` : 'Not active'
  if (lastFailed(item)) return 'Last run failed'
}
/** Not active, or its most recent run failed: whatever the card names in its health line. */
export const needsAttention = (item: HomeAutomation) => !!healthText(item)

export const filters = [
  { id: 'all', label: 'All' },
  { id: 'attention', label: 'Needs attention' },
  { id: 'today', label: 'Ran today' },
  { id: 'never', label: 'Never ran' },
  { id: 'off', label: 'Off' },
] as const
export type Filter = (typeof filters)[number]['id']
export const sorts = [
  { id: 'run', label: 'Recently run' },
  { id: 'name', label: 'Name' },
  { id: 'changed', label: 'Recently changed' },
] as const
export type Sort = (typeof sorts)[number]['id']

export function passes(item: HomeAutomation, filter: Filter): boolean {
  switch (filter) {
    case 'attention': return needsAttention(item)
    case 'today': return item.runsToday > 0
    case 'never': return !item.lastRunUtc && !item.recentRuns.length
    case 'off': return !item.enabled
    default: return true
  }
}
/** Search by name and by what starts it, ignoring case. */
export const matchesSearch = (term: string, name: string, summaries: readonly string[]) => {
  const needle = term.trim().toLocaleLowerCase()
  return !needle || name.toLocaleLowerCase().includes(needle) || summaries.some(line => line.toLocaleLowerCase().includes(needle))
}
const time = (iso: string | null | undefined) => (iso ? Date.parse(iso) || 0 : 0)
const byName = (a: HomeAutomation, b: HomeAutomation) => a.outline.name.localeCompare(b.outline.name, undefined, { sensitivity: 'base' }) || a.id.localeCompare(b.id)
/** Recently run and recently changed put the newest first, automations without a time last; ties go by name. */
export function sortAutomations(items: readonly HomeAutomation[], sort: Sort): HomeAutomation[] {
  const key = sort === 'run' ? (item: HomeAutomation) => time(item.lastRunUtc) : sort === 'changed' ? (item: HomeAutomation) => time(item.savedUtc) : undefined
  return [...items].sort((a, b) => (key ? key(b) - key(a) : 0) || byName(a, b))
}

/**
 * "6 automations · 1 off · 14 runs today · 1 failed in the last day": every automation, how many of them are off, and
 * the runs of all of them (Run now of one that is off counts too). Zero parts after the first are left out.
 */
export function homeSummary(items: readonly HomeAutomation[]): string {
  const off = items.filter(item => !item.enabled).length
  const today = items.reduce((sum, item) => sum + item.runsToday, 0)
  const failed = items.reduce((sum, item) => sum + item.failedLastDay, 0)
  return [
    items.length && plural(items.length, 'automation'),
    off && `${off} off`,
    today && `${plural(today, 'run')} today`,
    failed && `${failed} failed in the last day`,
  ].filter(Boolean).join(' · ')
}

/**
 * How often the page reads the list again: every 5 s while a run that started in the last 2 minutes is going (most
 * runs end by then), every 30 s while only older runs are going (a long Delay), and not at all when nothing runs.
 */
export function pollInterval(items: readonly HomeAutomation[], now: number): number | false {
  const going = items.flatMap(item => item.recentRuns.filter(run => live(run)))
  if (!going.length) return false
  return going.some(run => !run.startUtc || now - Date.parse(run.startUtc) < 120_000) ? 5_000 : 30_000
}

/** The start of the viewer's day, for "runs today". */
export const startOfToday = (now = new Date()) => new Date(now.getFullYear(), now.getMonth(), now.getDate()).toISOString()

const PREFERENCES = 'hass-sharp:automations-view'
export interface ViewPreferences { filter: Filter; sort: Sort }
const defaults: ViewPreferences = { filter: 'all', sort: 'run' }
/** The filter and sort this browser used last; the defaults when storage is unavailable or holds something else. */
export function loadPreferences(): ViewPreferences {
  try {
    const saved = JSON.parse(localStorage.getItem(PREFERENCES) ?? 'null') as Partial<ViewPreferences> | null
    return {
      filter: filters.some(f => f.id === saved?.filter) ? saved!.filter! : defaults.filter,
      sort: sorts.some(s => s.id === saved?.sort) ? saved!.sort! : defaults.sort,
    }
  } catch {
    return { ...defaults }
  }
}
export function savePreferences(preferences: ViewPreferences) {
  try {
    localStorage.setItem(PREFERENCES, JSON.stringify(preferences))
  } catch {
    // Private windows and blocked storage: the choice lasts for this visit only.
  }
}
