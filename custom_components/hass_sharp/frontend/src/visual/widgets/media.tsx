import { createResource, createSignal, createUniqueId, For, Show } from 'solid-js'
import { Dialog } from '../../components/Dialog'
import { Button } from '../../components/Controls'
import { useHass } from '../../context'
import type { HomeAssistant } from '../../types'
import type { JsonValue } from '../contract'
import { Chips, DraftField, type WidgetProps } from './common'
import { record } from './json'

/** One item of Home Assistant's media browser (`media_player/browse_media`, `media_source/browse_media`). */
export interface BrowseMedia {
  title: string
  media_class?: string
  media_content_id: string
  media_content_type: string
  children_media_class?: string | null
  can_play: boolean
  can_expand: boolean
  thumbnail?: string | null
  children?: BrowseMedia[]
}
type MediaValue = Record<string, JsonValue>

const text = (value: unknown) => typeof value === 'string' ? value : ''
const metadata = (value: JsonValue | undefined) => record(value) && record(value.metadata) ? value.metadata : {}
/** How a chosen media item reads: its title from the browser, else its content id. */
export function mediaTitle(value: JsonValue | undefined): string {
  if (!record(value)) return ''
  return text(metadata(value).title) || text(value.media_content_id)
}
/** Whether a MIME type is one of `accept`'s (`audio/*` takes `audio/mpeg`); without `accept`, every type is. */
export function acceptsMedia(accept: string[] | undefined, type: string): boolean {
  if (!accept?.length) return true
  return accept.some(pattern => pattern.endsWith('/*') ? type.startsWith(pattern.slice(0, -1)) : pattern === type)
}
/** What a field stores for a picked item, in the shape Home Assistant's own media picker writes. */
export function mediaValue(item: BrowseMedia, entityId?: string): MediaValue {
  const meta: MediaValue = { title: item.title }
  if (item.thumbnail) meta.thumbnail = item.thumbnail
  if (item.media_class) meta.media_class = item.media_class
  if (item.children_media_class) meta.children_media_class = item.children_media_class
  return { ...(entityId ? { entity_id: entityId } : {}), media_content_id: item.media_content_id, media_content_type: item.media_content_type, metadata: meta }
}

// Signed thumbnail paths, shared by every thumbnail shown; a signature is asked for again before it runs out.
const signed = new Map<string, { url: Promise<string | undefined>; until: number }>()
const signedFor = 600, reuseFor = 300_000
/**
 * The address a thumbnail can be loaded from. Home Assistant answers a path of its own (`/api/brands/…`,
 * `/api/media_source/…`) only when it is signed, as its own media browser signs it; any other address is used as it is.
 */
export function thumbnailUrl(hass: Pick<HomeAssistant, 'callWS'> | undefined, path: string, now = Date.now()): Promise<string | undefined> {
  if (!path.startsWith('/') || path.startsWith('//') || /[?&]authSig=/.test(path)) return Promise.resolve(path)
  if (!hass) return Promise.resolve(undefined)
  const known = signed.get(path)
  if (known && known.until > now) return known.url
  // A refusal is not kept: the next thumbnail asks again.
  const url = hass.callWS<{ path: string }>({ type: 'auth/sign_path', path, expires: signedFor })
    .then(reply => reply.path, () => { signed.delete(path); return undefined })
  signed.set(path, { url, until: now + reuseFor })
  return url
}
/** A media item's thumbnail, or an empty square while it has none (or it cannot be loaded). */
function Thumbnail(props: { src?: string | null }) {
  const hass = useHass() as Pick<HomeAssistant, 'callWS'> | undefined
  const [url] = createResource(() => props.src || undefined, src => thumbnailUrl(hass, src))
  const [failed, setFailed] = createSignal<string>()
  return <Show when={url() && failed() !== url()} fallback={<span class="hs-media-thumb" aria-hidden="true" />}>
    <img class="hs-media-thumb" src={url()!} alt="" loading="lazy" onError={() => setFailed(url())} />
  </Show>
}

interface Level { id?: string; type?: string; title: string }
/**
 * Home Assistant's media browser in a dialog. With `entityId`, the library of that media player; otherwise (or
 * after the player cannot be browsed) Home Assistant's media sources. Folders open; playable items are picked.
 */
export function MediaBrowser(props: {
  hass: Pick<HomeAssistant, 'callWS'>
  entityId?: string
  accept?: string[]
  title: string
  onPick(item: BrowseMedia, entityId: string | undefined): void
  onClose(): void
}) {
  const [player, setPlayer] = createSignal(props.entityId)
  const [path, setPath] = createSignal<Level[]>([{ title: 'Media' }])
  const here = () => path().at(-1)!
  const [page] = createResource(() => ({ player: player(), level: here() }), ({ player, level }) => props.hass.callWS<BrowseMedia>(player
    ? { type: 'media_player/browse_media', entity_id: player, ...(level.id !== undefined ? { media_content_id: level.id, media_content_type: level.type ?? '' } : {}) }
    : { type: 'media_source/browse_media', ...(level.id !== undefined ? { media_content_id: level.id } : {}) }))
  const children = () => (page()?.children ?? []).filter(child => child.can_expand || (child.can_play && acceptsMedia(props.accept, child.media_content_type)))
  const open = (item: BrowseMedia) => setPath(levels => [...levels, { id: item.media_content_id, type: item.media_content_type, title: item.title }])
  const pick = (item: BrowseMedia) => props.onPick(item, player())
  const sources = () => { setPlayer(undefined); setPath([{ title: 'Media' }]) }
  // Home Assistant rejects with `{code, message}`, which Solid wraps in an Error as its cause.
  const message = (error: unknown): string => {
    const cause = error instanceof Error ? error.cause : undefined
    if (record(cause) && typeof cause.message === 'string') return cause.message
    return error instanceof Error ? error.message : record(error) && typeof error.message === 'string' ? error.message : String(error)
  }
  return <Dialog open title={props.title} width="large" onClose={() => props.onClose()}
    footer={<Button onClick={() => props.onClose()}>Cancel</Button>}>
    <div class="hs-media-browser">
      <nav class="hs-media-browser__path" aria-label="Media folders">
        <Show when={path().length > 1}>
          <Button variant="ghost" onClick={() => setPath(levels => levels.slice(0, -1))}>Back</Button>
        </Show>
        <span>{[player() ? 'Player library' : 'Media sources', ...path().slice(1).map(level => level.title)].join(' › ')}</span>
        <Show when={player()}><Button variant="ghost" onClick={sources}>Browse media sources</Button></Show>
      </nav>
      <Show when={!page.error} fallback={<p class="hs-error" role="alert">Could not browse media: {message(page.error)}</p>}>
        <Show when={!page.loading} fallback={<p class="hs-muted" role="status">Loading…</p>}>
          <Show when={page()?.can_play && path().length > 1 && acceptsMedia(props.accept, page()!.media_content_type)}>
            <Button variant="primary" onClick={() => pick(page()!)}>Choose “{page()!.title}”</Button>
          </Show>
          <Show when={children().length} fallback={<p class="hs-muted" role="status">Nothing to choose here.</p>}>
            <ul class="hs-media-browser__items">
              <For each={children()}>{item => <li class="hs-media-item">
                <Thumbnail src={item.thumbnail} />
                <span class="hs-media-item__title">{item.title}<Show when={item.media_class}><small>{item.media_class}</small></Show></span>
                <Show when={item.can_expand}><Button variant="ghost" aria-label={`Open ${item.title}`} onClick={() => open(item)}>Open</Button></Show>
                <Show when={item.can_play && acceptsMedia(props.accept, item.media_content_type)}>
                  <Button aria-label={`Choose ${item.title}`} onClick={() => pick(item)}>Choose</Button>
                </Show>
              </li>}</For>
            </ul>
          </Show>
        </Show>
      </Show>
    </div>
  </Dialog>
}

/**
 * Home Assistant's media selector: the chosen item, picked in the media browser of the media player the step acts on
 * (or in Home Assistant's media sources), or entered by content ID and type.
 */
export function MediaWidget(props: WidgetProps) {
  const hass = useHass() as Pick<HomeAssistant, 'callWS'> | undefined
  const [browsing, setBrowsing] = createSignal(false)
  const manualId = createUniqueId()
  // The player browsed: the one entity the step acts on, unless the selector asks for media of given types.
  const player = () => props.widget.accept?.length ? undefined : props.context?.targetEntityIds?.length === 1 ? props.context.targetEntityIds[0] : undefined
  const list = () => Array.isArray(props.value) ? props.value.filter(record) as MediaValue[] : []
  const single = () => record(props.value) ? props.value : undefined
  const pick = (item: BrowseMedia, entityId: string | undefined) => {
    setBrowsing(false)
    const value = mediaValue(item, entityId)
    props.commit(props.widget.multiple ? [...list(), value] : value)
  }
  const setPart = (key: 'media_content_id' | 'media_content_type', input: string) => {
    const current = single() ?? {}
    const next: MediaValue = { ...current, [key]: input.trim() }
    // A changed item is no longer the one the browser named.
    if (next.media_content_id !== current.media_content_id || next.media_content_type !== current.media_content_type) delete next.metadata
    if (!next.media_content_id && !next.media_content_type) { props.commit(undefined); return }
    next.media_content_id ??= ''
    next.media_content_type ??= ''
    props.commit(next)
  }
  const thumbnail = () => text(metadata(single()).thumbnail)
  return <div class="hs-media">
    <Show when={props.widget.multiple} fallback={
      <div class="hs-media-value">
        <Show when={thumbnail()}><Thumbnail src={thumbnail()} /></Show>
        <span class="hs-media-value__title" classList={{ 'hs-muted': !single() }}>
          {single() ? mediaTitle(single()) || 'Untitled' : 'Not set'}
          <Show when={single() && text(single()!.media_content_type)}><small>{text(single()!.media_content_type)}</small></Show>
        </span>
      </div>}>
      <Chips label={props.label} disabled={props.disabled} fallbackFocusId={props.id}
        items={list().map((item, i) => ({ key: String(i), label: mediaTitle(item) || 'Untitled', title: text(item.media_content_id) }))}
        onRemove={key => { const next = list().filter((_, i) => String(i) !== key); props.commit(next.length ? next : undefined) }} />
    </Show>
    <div class="hs-media-actions">
      <Show when={hass}>
        <Button id={props.id} aria-describedby={props.describedBy} disabled={props.disabled} onClick={() => setBrowsing(true)}>
          {props.widget.multiple ? 'Add media…' : single() ? 'Change media…' : 'Choose media…'}
        </Button>
      </Show>
      <Show when={!props.widget.multiple && single() && !props.required}>
        <Button variant="ghost" disabled={props.disabled} onClick={() => props.commit(undefined)}>Clear</Button>
      </Show>
    </div>
    <Show when={!props.widget.multiple}>
      <details class="hs-media-manual" open={!hass || undefined}>
        <summary>Enter manually</summary>
        <label class="hs-media-manual__field" for={hass ? manualId : props.id}>Content ID</label>
        <DraftField id={hass ? manualId : props.id} label="Content ID" text={text(single()?.media_content_id)} placeholder="Such as media-source://… or a URL"
          describedBy={props.describedBy} invalid={props.invalid} disabled={props.disabled} commit={value => { setPart('media_content_id', value); return undefined }} />
        <label class="hs-media-manual__field" for={`${manualId}-type`}>Content type</label>
        <DraftField id={`${manualId}-type`} label="Content type" text={text(single()?.media_content_type)} placeholder="Such as music, video or audio/mpeg"
          disabled={props.disabled} commit={value => { setPart('media_content_type', value); return undefined }} />
      </details>
    </Show>
    <Show when={browsing() && hass}>
      <MediaBrowser hass={hass!} entityId={player()} accept={props.widget.accept} title={`Choose ${props.label}`} onPick={pick} onClose={() => setBrowsing(false)} />
    </Show>
  </div>
}
