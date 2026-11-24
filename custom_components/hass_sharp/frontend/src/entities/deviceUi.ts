import type { HomeAssistant } from '../types'
import { EntityApiError, homeAssistantError } from './api'

/**
 * A field of a card as its device drew it: a text, a select (one of `options`, or the empty text for nothing chosen),
 * a number (null when empty), an on/off toggle, or a pick field (the id of an option of the page's set `set`, or null
 * for none). `options`, `suggestions`, `minimum` and `maximum` are absent when it has none. A pick field's `except` is
 * the options of the set it does not offer, and `add` the form of a new one, when it allows one: its fields are texts,
 * selects, numbers and toggles.
 */
export interface DeviceField {
  key: string
  label: string
  type: 'text' | 'select' | 'number' | 'toggle' | 'pick'
  value: string | number | boolean | null
  options?: string[]
  suggestions?: string[]
  minimum?: number
  maximum?: number
  set?: string
  except?: string[]
  add?: { title: string; fields: DeviceField[] }
  required: boolean
}
/** An option a pick field chooses from: its picture by SHA-256 (fetched with `image`, as a card's), its title and detail. */
export interface DeviceOption { id: string; image: string | null; title: string; detail: string | null }
/** A set of options drawn once for a page, which its cards' pick fields name by `id`. */
export interface DeviceOptionSet { id: string; items: DeviceOption[] }
/**
 * A button of a card; with `confirm`, the panel asks that before it sends the press. With `validate` false, a press is
 * not checked against the card's fields and sends none of their values. An answer without `validate` (an older
 * backend) reads as `true`.
 */
export interface DeviceAction { id: string; label: string; style: 'default' | 'primary' | 'danger'; confirm: string | null; validate: boolean }
/** A card of a list: its image by SHA-256 (fetched with `image`), its texts, fields and buttons. */
export interface DeviceCard { id: string; image: string | null; title: string | null; detail: string | null; fields: DeviceField[]; actions: DeviceAction[] }
/** A list of a page, its cards in the order first drawn; `empty` is what it says without cards. */
export interface DeviceList { id: string; title: string; attention: boolean; empty: string | null; cards: DeviceCard[] }
/** The page of one C# node of a running definition; an older backend's page has no `options`, which reads as none. */
export interface DevicePageData { nodeKey: string; title: string; attention: number; lists: DeviceList[]; options?: DeviceOptionSet[] }
/**
 * `read`'s answer: not running (off, failed, still starting or unknown), the pages unchanged since the revision the
 * panel has, or the pages of a newer revision.
 */
export type DeviceRead = { running: false } | { running: true; revision: string; unchanged: true } | { running: true; revision: string; pages: DevicePageData[] }
/**
 * `counts`' answer: by running definition, the cards waiting for the person on each of its nodes (by node key), and by
 * definition the node keys that hold stored data.
 */
export interface DeviceCounts { attention: Record<string, Record<string, number>>; stored: Record<string, string[]> }
/** A shared data store: its name, its size, and the definitions that use it. */
export interface SharedStore { name: string; bytes: number; users: string[] }
/**
 * How a press ended: `done`, `changed` (the card changed since it was drawn: nothing ran), `refused` (the device's
 * sentence for the person, as `message`) or `failed` (its handler threw or ran out of time).
 */
export type DeviceActResult = { status: 'done' | 'changed' | 'refused' | 'failed'; message?: string | null }

type Envelope<T> = { version: 1; ok: true; result: T } | { version: 1; ok: false; error: { code: string; detail: unknown } }

/** Base64 as the backend writes it: never anything that could end the data URL it goes into. */
const base64 = /^[A-Za-z0-9+/]*={0,2}$/

/**
 * The device pages' websocket API (`hass_sharp/device_ui`, admins only). Each call throws an {@link EntityApiError}
 * for a refused request: the backend's `{code, detail}` (`not_running`, `not_found`, `in_use`, `invalid_request`,
 * `storage`, `internal`) or Home Assistant's own refusal.
 */
export function deviceUiApi(hass: Pick<HomeAssistant, 'callWS'>) {
  const request = async <T>(operation: string, fields = {}): Promise<T> => {
    let response: Envelope<T>
    try {
      response = await hass.callWS<Envelope<T>>({ type: 'hass_sharp/device_ui', request: { operation, ...fields } })
    } catch (error) {
      throw homeAssistantError(error) ?? error
    }
    if (!response.ok) throw new EntityApiError(response.error.code, response.error.detail)
    return response.result
  }
  return {
    /** The running definition's pages, or `unchanged` when its revision is still `knownRevision`. */
    read: (definitionId: string, knownRevision?: string) =>
      request<DeviceRead>('read', { definitionId, ...(knownRevision === undefined ? {} : { knownRevision }) }),
    /** The JPEG a current card or option of the node's page shows, as a `data:` URL; `not_found` once none shows it. */
    image: async (definitionId: string, nodeKey: string, hash: string): Promise<string> => {
      const { jpeg } = await request<{ jpeg: string }>('image', { definitionId, nodeKey, hash })
      if (typeof jpeg !== 'string' || !base64.test(jpeg)) throw new EntityApiError('internal', 'The device page answered an image that cannot be shown.')
      return `data:image/jpeg;base64,${jpeg}`
    },
    /** Runs the press of `action` on the card drawn as `list`/`card`, with the card's field values by key. */
    act: (definitionId: string, nodeKey: string, list: string, card: string, action: string, values: Record<string, unknown>) =>
      request<DeviceActResult>('act', { definitionId, nodeKey, list, card, action, values }),
    counts: () => request<DeviceCounts>('counts'),
    stores: async () => (await request<{ shared: SharedStore[] }>('stores')).shared,
    /** Deletes a shared store; `in_use` while a definition that is on uses it. */
    deleteStore: async (name: string): Promise<void> => { await request<{ deleted: true }>('delete_store', { name }) },
  }
}
