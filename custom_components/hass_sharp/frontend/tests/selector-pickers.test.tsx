import { afterEach, describe, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import type { InputSpec, JsonValue } from '../src/visual/contract'
import { acceptsMedia, thumbnailUrl, fitsWidget, inlineWidget, literalFor, valueSummary, WidgetControl, widgetFor, type Widget } from '../src/visual/widgets'

// The types Home Assistant's selectors without a dedicated type get (ServiceSchemaCatalog's AnyType).
const anyType = ['object', 'array', 'string', 'number', 'boolean']
const field = (selector: Record<string, unknown>, extra: Partial<InputSpec> = {}): InputSpec => ({ types: anyType, required: false, description: '', selector, ...extra })

let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); dispose = undefined; document.body.innerHTML = '' })

/** A Home Assistant whose WebSocket commands answer from `replies`, keyed by message type. */
function mount(widget: Widget, options: { value?: JsonValue; replies?: Record<string, (msg: any) => unknown>; hass?: Record<string, unknown> | null; targets?: string[]; required?: boolean } = {}) {
  const host = document.createElement('div'); document.body.append(host)
  const client = new QueryClient()
  const commit = vi.fn()
  const callWS = vi.fn(async (msg: { type: string }) => {
    const reply = options.replies?.[msg.type]
    if (!reply) throw new Error(`unexpected ${msg.type}`)
    return reply(msg)
  })
  const hass = options.hass === null ? undefined : { callWS, language: 'en', ...options.hass }
  dispose = render(() => <QueryClientProvider client={client}><HassCtx.Provider value={hass as any}>
    <WidgetControl widget={widget} value={options.value} commit={commit} id="field" label="Field" disabled={false} compact={false} invalid={false}
      required={options.required ?? false} context={{ targetEntityIds: options.targets ?? [] }} />
  </HassCtx.Provider></QueryClientProvider>, host)
  return { host, commit, callWS }
}
const openOptions = async (host: HTMLElement) => {
  const box = host.querySelector<HTMLInputElement>('input[role="combobox"]')!
  await vi.waitFor(() => { box.click(); expect(host.querySelectorAll('[role="option"]').length).toBeGreaterThan(0) })
  return [...host.querySelectorAll<HTMLElement>('[role="option"]')]
}
const button = (host: HTMLElement, name: string) => [...host.querySelectorAll<HTMLButtonElement>('button')].find(b => (b.getAttribute('aria-label') ?? b.textContent?.trim()) === name)

describe('selectors with a lookup', () => {
  it('maps config entry, theme, conversation agent and language selectors to their pickers', () => {
    expect(widgetFor(field({ config_entry: { integration: 'telegram_bot' } }))).toEqual({ kind: 'config_entry', multiple: false, integration: 'telegram_bot' })
    expect(widgetFor(field({ config_entry: {} }))).toEqual({ kind: 'config_entry', multiple: false })
    expect(widgetFor(field({ theme: { include_default: true } }))).toEqual({ kind: 'theme', multiple: false, includeDefault: true })
    expect(widgetFor(field({ conversation_agent: { language: 'de' } }))).toEqual({ kind: 'conversation_agent', multiple: false, agentLanguage: 'de' })
    expect(widgetFor(field({ language: { languages: ['en', 'he'], native_name: true } }))).toEqual({ kind: 'language', multiple: false, nativeName: true, noSort: false, languages: ['en', 'he'] })
    for (const name of ['config_entry', 'theme', 'conversation_agent', 'language']) expect(inlineWidget(widgetFor(field({ [name]: {} })))).toBe(true)
    // An id is stored as text.
    expect(literalFor(field({ config_entry: {} }), '01JABC')).toEqual({ kind: 'literal', type: 'string', value: '01JABC' })
  })
  it('lists the config entries of the selector\'s integration and stores the entry id', async () => {
    const { host, commit, callWS } = mount(widgetFor(field({ config_entry: { integration: 'telegram_bot' } })), {
      replies: { 'config_entries/get': () => [{ entry_id: 'b', domain: 'telegram_bot', title: 'Work bot', state: 'loaded' }, { entry_id: 'a', domain: 'telegram_bot', title: 'Home bot', state: 'setup_error' }] },
    })
    const options = await openOptions(host)
    expect(callWS).toHaveBeenCalledWith({ type: 'config_entries/get', domain: 'telegram_bot' })
    expect(options.map(o => o.textContent)).toEqual(['Home botsetup error', 'Work bot'])
    options[1].click()
    expect(commit).toHaveBeenCalledWith('b')
  })
  it('shows a stored entry that no longer exists by its id', async () => {
    const { host } = mount(widgetFor(field({ config_entry: {} })), { value: 'gone', replies: { 'config_entries/get': () => [{ entry_id: 'a', domain: 'x', title: 'A' }] } })
    await openOptions(host)
    expect(host.querySelector<HTMLInputElement>('input[role="combobox"]')!.value).toBe('gone')
  })
  it('offers Home Assistant\'s default theme when the selector asks for it', async () => {
    const { host } = mount(widgetFor(field({ theme: { include_default: true } })), { hass: { themes: { themes: { Midnight: {}, Dark: {} } } } })
    expect((await openOptions(host)).map(o => o.textContent)).toEqual(['Home Assistant default', 'Dark', 'Midnight'])
  })
  it('lists the conversation agents for the selector\'s language', async () => {
    const { host, callWS } = mount(widgetFor(field({ conversation_agent: { language: 'de' } })), {
      replies: { 'conversation/agent/list': () => ({ agents: [{ id: 'conversation.home_assistant', name: 'Home Assistant' }] }) },
    })
    expect((await openOptions(host)).map(o => o.textContent)).toEqual(['Home Assistantconversation.home_assistant'])
    expect(callWS).toHaveBeenCalledWith({ type: 'conversation/agent/list', language: 'de' })
  })
  it('names languages, in their own language when the selector asks', async () => {
    const { host } = mount(widgetFor(field({ language: { languages: ['fr', 'de'], native_name: true } })))
    expect((await openOptions(host)).map(o => o.querySelector('span')!.textContent)).toEqual(['Deutsch', 'français'])
    expect(valueSummary(widgetFor(field({ language: {} })), 'de')).toBe('German')
  })
  it('lets the id be typed when Home Assistant cannot list the choices', async () => {
    const { host, commit } = mount(widgetFor(field({ config_entry: {} })), { replies: { 'config_entries/get': () => { throw new Error('denied') } } })
    await vi.waitFor(() => expect(host.querySelector('input[role="combobox"]')).toBeNull())
    const input = host.querySelector<HTMLInputElement>('input#field')!
    input.value = '01JX'; input.dispatchEvent(new InputEvent('input', { bubbles: true })); input.dispatchEvent(new FocusEvent('blur'))
    expect(commit).toHaveBeenCalledWith('01JX')
  })
})

const library = {
  title: 'Big boy', media_content_id: '', media_content_type: 'library', can_play: false, can_expand: true,
  children: [
    { title: 'Albums', media_class: 'directory', media_content_id: 'albums', media_content_type: 'albums', can_play: false, can_expand: true },
    { title: 'Radio 1', media_class: 'channel', media_content_id: 'radio:1', media_content_type: 'channel', can_play: true, can_expand: false, thumbnail: '/img/r1.png' },
  ],
}
const albums = {
  title: 'Albums', media_content_id: 'albums', media_content_type: 'albums', can_play: false, can_expand: true,
  children: [{ title: 'Blue', media_class: 'album', media_content_id: 'album:blue', media_content_type: 'album', can_play: true, can_expand: true }],
}

describe('media selector', () => {
  it('maps the media selector, edited in the panel', () => {
    const widget = widgetFor(field({ media: { accept: ['audio/*'] } }))
    expect(widget).toEqual({ kind: 'media', multiple: false, accept: ['audio/*'] })
    expect(inlineWidget(widget)).toBe(false)
    expect(fitsWidget(widget, { media_content_id: 'x', media_content_type: 'music' })).toBe(true)
    // A value of another shape (written by hand before) stays editable as JSON.
    expect(fitsWidget(widget, 'media-source://x')).toBe(false)
    expect(valueSummary(widget, { media_content_id: 'radio:1', media_content_type: 'channel', metadata: { title: 'Radio 1' } })).toBe('Radio 1')
    expect(valueSummary(widget, { media_content_id: 'radio:1', media_content_type: 'channel' })).toBe('radio:1')
    expect(acceptsMedia(['audio/*'], 'audio/mpeg')).toBe(true)
    expect(acceptsMedia(['audio/*'], 'video/mp4')).toBe(false)
    expect(acceptsMedia(undefined, 'video/mp4')).toBe(true)
  })
  it('signs only Home Assistant\'s own thumbnail paths, and leaves them out when it cannot', async () => {
    expect(await thumbnailUrl(undefined, 'https://example.org/a.png')).toBe('https://example.org/a.png')
    expect(await thumbnailUrl(undefined, '/api/media_player_proxy/x?token=t&authSig=s')).toBe('/api/media_player_proxy/x?token=t&authSig=s')
    expect(await thumbnailUrl(undefined, '/api/brands/integration/tts/logo.png')).toBeUndefined()
    const refused = { callWS: vi.fn(async () => { throw new Error('no') }) }
    expect(await thumbnailUrl(refused, '/api/brands/integration/never/logo.png')).toBeUndefined()
  })
  it('browses the media player the step acts on, opens folders and stores the picked item as Home Assistant\'s picker does', async () => {
    const { host, commit, callWS } = mount(widgetFor(field({ media: {} })), {
      targets: ['media_player.big_boy'], required: true,
      replies: { 'media_player/browse_media': msg => msg.media_content_id === 'albums' ? albums : library },
    })
    expect(host.textContent).toContain('Not set')
    // A required field has nothing to clear.
    expect(button(host, 'Clear')).toBeUndefined()
    button(host, 'Choose media…')!.click()
    await vi.waitFor(() => expect(button(host, 'Choose Radio 1')).toBeDefined())
    expect(callWS).toHaveBeenCalledWith({ type: 'media_player/browse_media', entity_id: 'media_player.big_boy' })
    button(host, 'Open Albums')!.click()
    await vi.waitFor(() => expect(button(host, 'Choose Blue')).toBeDefined())
    expect(callWS).toHaveBeenCalledWith({ type: 'media_player/browse_media', entity_id: 'media_player.big_boy', media_content_id: 'albums', media_content_type: 'albums' })
    expect(host.textContent).toContain('Player library › Albums')
    button(host, 'Choose Blue')!.click()
    expect(commit).toHaveBeenCalledWith({ entity_id: 'media_player.big_boy', media_content_id: 'album:blue', media_content_type: 'album', metadata: { title: 'Blue', media_class: 'album' } })
    expect(host.querySelector('dialog')).toBeNull()
  })
  it('browses the media sources for a selector with accepted types, and offers only those', async () => {
    const { host, commit, callWS } = mount(widgetFor(field({ media: { accept: ['audio/*'] } })), {
      targets: ['assist_satellite.kitchen'],
      replies: {
        'media_source/browse_media': () => ({
          title: 'Media', media_content_id: '', media_content_type: '', can_play: false, can_expand: true, children: [
            { title: 'chime.mp3', media_content_id: 'media-source://media_source/local/chime.mp3', media_content_type: 'audio/mpeg', can_play: true, can_expand: false },
            { title: 'clip.mp4', media_content_id: 'media-source://media_source/local/clip.mp4', media_content_type: 'video/mp4', can_play: true, can_expand: false },
          ],
        }),
      },
    })
    button(host, 'Choose media…')!.click()
    await vi.waitFor(() => expect(button(host, 'Choose chime.mp3')).toBeDefined())
    expect(callWS).toHaveBeenCalledWith({ type: 'media_source/browse_media' })
    expect(button(host, 'Choose clip.mp4')).toBeUndefined()
    button(host, 'Choose chime.mp3')!.click()
    expect(commit).toHaveBeenCalledWith({ media_content_id: 'media-source://media_source/local/chime.mp3', media_content_type: 'audio/mpeg', metadata: { title: 'chime.mp3' } })
  })
  it('falls back to the media sources when the player cannot be browsed', async () => {
    const { host } = mount(widgetFor(field({ media: {} })), {
      targets: ['media_player.off'],
      replies: {
        'media_player/browse_media': () => { throw { code: 'home_assistant_error', message: 'The player is off' } },
        'media_source/browse_media': () => library,
      },
    })
    button(host, 'Choose media…')!.click()
    await vi.waitFor(() => expect(host.querySelector('[role="alert"]')?.textContent).toBe('Could not browse media: The player is off'))
    button(host, 'Browse media sources')!.click()
    await vi.waitFor(() => expect(button(host, 'Choose Radio 1')).toBeDefined())
  })
  it('shows the chosen item and lets its content ID and type be entered by hand', async () => {
    const value = { entity_id: 'media_player.big_boy', media_content_id: 'radio:1', media_content_type: 'channel', metadata: { title: 'Radio 1', thumbnail: '/img/r1.png' } }
    const { host, commit, callWS } = mount(widgetFor(field({ media: {} })), {
      value, targets: ['media_player.big_boy'], replies: { 'auth/sign_path': msg => ({ path: `${msg.path}?authSig=signed` }) },
    })
    expect(host.querySelector('.hs-media-value__title')!.textContent).toBe('Radio 1channel')
    // Home Assistant serves its own paths only when signed, as its media browser signs them.
    await vi.waitFor(() => expect(host.querySelector<HTMLImageElement>('.hs-media-value img')?.getAttribute('src')).toBe('/img/r1.png?authSig=signed'))
    expect(callWS).toHaveBeenCalledWith({ type: 'auth/sign_path', path: '/img/r1.png', expires: 600 })
    const id = host.querySelector<HTMLInputElement>('input[aria-label="Content ID"]')!
    id.value = 'radio:2'; id.dispatchEvent(new InputEvent('input', { bubbles: true })); id.dispatchEvent(new FocusEvent('blur'))
    // The browser's title named the old item, so it goes.
    expect(commit).toHaveBeenLastCalledWith({ entity_id: 'media_player.big_boy', media_content_id: 'radio:2', media_content_type: 'channel' })
    button(host, 'Clear')!.click()
    expect(commit).toHaveBeenLastCalledWith(undefined)
  })
  it('is entered by hand outside Home Assistant', () => {
    const { host, commit } = mount(widgetFor(field({ media: {} })), { hass: null })
    expect(button(host, 'Choose media…')).toBeUndefined()
    const type = host.querySelector<HTMLInputElement>('input[aria-label="Content type"]')!
    type.value = 'music'; type.dispatchEvent(new InputEvent('input', { bubbles: true })); type.dispatchEvent(new FocusEvent('blur'))
    expect(commit).toHaveBeenCalledWith({ media_content_type: 'music', media_content_id: '' })
  })
})
