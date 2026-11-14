import { createMemo, createUniqueId, For, Show } from 'solid-js'
import { A } from '@solidjs/router'
import { Menu } from '../components/Menu'
import type { HomeAutomation } from '../visual/api'
import { nodeCatalog } from '../visual/contract'
import { Glyph } from '../visual/Glyph'
import { icons } from '../visual/icons'
import type { SummaryNames } from '../visual/nodeSummary'
import { RunNowButton, type RunNotice } from '../visual/RunNow'
import { EnabledSwitch } from './EnabledSwitch'
import { GraphThumbnail } from './GraphThumbnail'
import { healthText, isRunning, lastRunText, outlineGraph, triggerSummaries } from './model'
import { RunDots } from './RunDots'

export interface CardActions {
  onToggle(enabled: boolean): void
  onRename(): void
  onDuplicate(): void
  onViewRuns(): void
  onDelete(): void
  onRunNotice(notice: RunNotice): void
}

/**
 * One automation on the home page. The whole card is the link to its editor (the name is the link's text; what starts
 * it, its health and its runs describe it); the switch, Run now and the "⋯" menu sit above the link.
 */
export function AutomationCard(props: CardActions & {
  item: HomeAutomation
  names?: SummaryNames
  now: number
  admin: boolean
  busy?: boolean
}) {
  const describedBy = createUniqueId()
  const name = () => props.item.outline.name
  const graph = createMemo(() => outlineGraph(props.item.outline))
  const summaries = createMemo(() => triggerSummaries(graph(), props.names))
  const health = () => healthText(props.item)
  const document = createMemo(() => ({ graph: graph(), automationId: props.item.automationId }))
  return (
    <li
      class="hs-home-card"
      classList={{ 'hs-home-card--off': !props.item.enabled, 'hs-home-card--attention': !!health(), 'hs-home-card--running': isRunning(props.item) }}
      data-automation={props.item.id}
    >
      <div class="hs-home-card__picture">
        <GraphThumbnail outline={props.item.outline} />
        <Show when={!props.item.enabled}>
          <span class="hs-home-chip hs-home-chip--off">Off</span>
        </Show>
      </div>
      <div class="hs-home-card__body">
        <h2 class="hs-home-card__title">
          <A class="hs-home-card__link" href={`/visual/${encodeURIComponent(props.item.id)}`} aria-describedby={describedBy}>{name()}</A>
        </h2>
        <div id={describedBy} class="hs-home-card__about">
          <Show when={summaries().length} fallback={<p class="hs-home-card__starts hs-muted">No trigger yet</p>}>
            <ul class="hs-home-card__starts">
              <For each={summaries().slice(0, 2)}>
                {line => (
                  <li>
                    <Glyph paths={icons.trigger} class="hs-home-card__glyph" />
                    <span>{line}</span>
                  </li>
                )}
              </For>
              <Show when={summaries().length > 2}>
                <li class="hs-home-card__more">+{summaries().length - 2} more</li>
              </Show>
            </ul>
          </Show>
          <Show when={health()}>
            <p class="hs-home-card__health">
              <Glyph paths={icons.problem} class="hs-home-card__glyph" />
              <span>{health()}</span>
            </p>
          </Show>
          <p class="hs-home-card__runs">
            <RunDots runs={props.item.recentRuns} />
            <span class="hs-home-card__last">{lastRunText(props.item, props.now)}</span>
          </p>
        </div>
      </div>
      <Show when={props.admin}>
        <div class="hs-home-card__actions">
          <EnabledSwitch name={name()} enabled={props.item.enabled} disabled={props.busy} onChange={props.onToggle} />
          <RunNowButton
            document={document()}
            catalog={nodeCatalog}
            names={props.names}
            unavailable={props.item.currentRevisionActive ? undefined : 'The saved version is not active'}
            dirty={false}
            busy={props.busy}
            onNotice={props.onRunNotice}
          />
          <Menu
            label={`Actions for ${name()}`}
            disabled={props.busy}
            items={[
              { label: 'Rename', onSelect: props.onRename },
              { label: 'Duplicate', onSelect: props.onDuplicate },
              { label: 'View runs', onSelect: props.onViewRuns },
              { label: 'Delete', danger: true, onSelect: props.onDelete },
            ]}
          />
        </div>
      </Show>
    </li>
  )
}
