import { useQuery } from '@tanstack/solid-query'
import { createMemo, Show } from 'solid-js'
import { useHass } from '../../context'
import type { HomeAssistant } from '../../types'
import type { JsonValue } from '../contract'
import { ListWidget, TextWidget } from './basic'
import type { WidgetProps } from './common'
import { strings } from './json'
import type { LookupKind, Widget } from './model'
import { SearchSelect, type SearchOption } from './SearchSelect'

type Hass = Pick<HomeAssistant, 'callWS'> & Partial<Pick<HomeAssistant, 'themes' | 'translationMetadata' | 'language'>>
interface ConfigEntry { entry_id: string; domain: string; title: string; state?: string }
interface Agent { id: string; name: string }

/** A language code in words ("German", or "Deutsch" in its own language); the code itself when the browser has no name for it. */
export function languageName(code: string, inLanguage?: string): string {
  try { return new Intl.DisplayNames([inLanguage || 'en'], { type: 'language' }).of(code) ?? code }
  catch { return code }
}

const byLabel = (a: SearchOption, b: SearchOption) => a.label.localeCompare(b.label)

/** The choices of a Home Assistant lookup selector, read from Home Assistant itself. */
export async function lookupOptions(hass: Hass, widget: Widget): Promise<SearchOption[]> {
  switch (widget.kind as LookupKind) {
    case 'config_entry': {
      const entries = await hass.callWS<ConfigEntry[]>({ type: 'config_entries/get', ...(widget.integration ? { domain: widget.integration } : {}) })
      return entries.map(entry => ({
        value: entry.entry_id, label: entry.title || entry.domain,
        detail: [widget.integration ? undefined : entry.domain, entry.state && entry.state !== 'loaded' ? entry.state.replaceAll('_', ' ') : undefined].filter(Boolean).join(' · ') || undefined,
      })).sort(byLabel)
    }
    case 'theme': {
      const themes = hass.themes?.themes ?? (await hass.callWS<{ themes?: Record<string, unknown> }>({ type: 'frontend/get_themes' })).themes ?? {}
      const own = Object.keys(themes).map(name => ({ value: name, label: name })).sort(byLabel)
      return widget.includeDefault ? [{ value: 'default', label: 'Home Assistant default' }, ...own] : own
    }
    case 'conversation_agent': {
      const reply = await hass.callWS<{ agents?: Agent[] }>({ type: 'conversation/agent/list', ...(widget.agentLanguage ? { language: widget.agentLanguage } : {}) })
      return (reply.agents ?? []).map(agent => ({ value: agent.id, label: agent.name || agent.id, detail: agent.name ? agent.id : undefined }))
    }
    case 'language': {
      const codes = widget.languages ?? Object.keys(hass.translationMetadata?.translations ?? {})
      const options = codes.map(code => ({ value: code, label: languageName(code, widget.nativeName ? code : hass.language), detail: code }))
      return widget.noSort ? options : options.sort(byLabel)
    }
  }
}

/**
 * Config entry, theme, conversation agent and language selectors: a searchable choice of what Home Assistant has. A
 * stored id that is not offered (an entry removed since) stays shown as it is. Without Home Assistant, or when the
 * lookup fails or finds nothing, the id can be typed instead.
 */
export function LookupWidget(props: WidgetProps) {
  const hass = useHass() as Hass | undefined
  const query = useQuery(() => ({
    queryKey: ['hass-lookup', props.widget.kind, props.widget.integration ?? props.widget.agentLanguage ?? '', props.widget.includeDefault ?? false,
      (props.widget.languages ?? []).join(','), props.widget.nativeName ?? false, hass?.language ?? ''],
    queryFn: () => lookupOptions(hass!, props.widget),
    enabled: !!hass,
    staleTime: 60_000,
    retry: false,
  }))
  const options = createMemo(() => query.isSuccess ? query.data : [])
  const typed = () => !hass || query.isError || (query.isSuccess && !options().length)
  const name = (id: JsonValue) => options().find(o => o.value === id)?.label ?? String(id)
  const placeholder = () => query.isLoading ? 'Loading…' : 'Not set'
  return <Show when={!typed()} fallback={<TextWidget {...props} widget={{ ...props.widget, kind: 'text' }} />}>
    <Show when={props.widget.multiple} fallback={<SearchSelect id={props.id} label={props.label} options={options()}
      value={typeof props.value === 'string' ? props.value : undefined} onSelect={id => props.commit(id)} placeholder={placeholder()}
      describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled} />}>
      <ListWidget {...props} labelFor={name} add={add => <SearchSelect id={props.id} label={`Add to ${props.label}`} clearOnSelect placeholder="Add…"
        options={options().filter(o => !strings(props.value).includes(o.value))} onSelect={add}
        describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled} />} />
    </Show>
  </Show>
}
