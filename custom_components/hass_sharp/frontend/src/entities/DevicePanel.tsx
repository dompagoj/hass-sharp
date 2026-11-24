import { createEffect, createMemo, createSignal, createUniqueId, ErrorBoundary, For, Match, on, onCleanup, Show, Switch, untrack } from 'solid-js'
import { useQueryClient } from '@tanstack/solid-query'
import { Button } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { useHass } from '../context'
import { NumberWidget } from '../visual/widgets/basic'
import { describedBy } from '../visual/widgets/common'
import { EntityApiError, entityError } from './api'
import { countsChanged, noteDeviceRead } from './deviceCounts'
import { deviceUiApi, type DeviceActResult, type DeviceCard, type DeviceField, type DeviceList, type DevicePageData } from './deviceUi'
import { FieldErrors, SelectField, SwitchField, TextField } from './formFields'
import { PickField, pickChoice, sameList, type FormValue, type PickChoice, type PickOptions } from './PickField'

/** How often the open tab reads the pages. */
const pollMs = 2000

/** A field's value: as drawn, or as the person changed it; a pick field's is its choice. */
type FieldValue = DeviceField['value'] | PickChoice
/** What the panel shows of the definition: nothing read yet, not running, or the pages of its running revision. */
type Shown = { kind: 'reading' } | { kind: 'off' } | { kind: 'pages'; pages: DevicePageData[] }
/** How a press ended for its card: the device's answer, or why it could not be sent. */
type Outcome = DeviceActResult | { status: 'error'; message: string }
type Press = (nodeKey: string, list: string, card: string, action: string, values: Record<string, unknown>) => Promise<Outcome | undefined>

/** The button of each action style. */
const variants = { default: 'secondary', primary: 'primary', danger: 'danger' } as const
/** What the failure line says when a read's pages could not be drawn. */
const drawFailure = 'The device page could not be drawn. It is read again in a moment.'
const notRunning = (error: unknown) => error instanceof EntityApiError && error.code === 'not_running'

/**
 * The Device tab: the pages the running revision of an entity definition's C# nodes draw, read every 2 seconds while
 * the tab and the browser's page are shown, and once more after each press. A page is a section under its title, each
 * list a heading with its card count, and each card its image, texts, fields and buttons. A card, and what the person
 * changed in its fields, stays as long as a card of that list and id is drawn: a refresh never re-creates it, so what
 * is typed, focus and the caret survive. A press names its card by the ids drawn. Everything the device drew is shown
 * as text; an image only as the `data:` URL built from its JPEG, fetched once per hash while a card shows it, or a pick
 * field shows it as an option (the one chosen, or a row its open dialog has rendered). Each read
 * hands its pages' attention to the page's shared counts (none once not running), and each press that answers makes
 * them stale.
 */
export function DevicePanel(props: { definitionId: string | undefined; visible: boolean }) {
  const api = deviceUiApi(useHass())
  const queryClient = useQueryClient()
  const [pageShown, setPageShown] = createSignal(document.visibilityState !== 'hidden')
  const visibility = () => setPageShown(document.visibilityState !== 'hidden')
  document.addEventListener('visibilitychange', visibility)
  onCleanup(() => document.removeEventListener('visibilitychange', visibility))

  const [shown, setShown] = createSignal<Shown>({ kind: 'reading' })
  const [failure, setFailure] = createSignal<string>()
  // By hash: the data URL, or null for an image the device no longer shows.
  const [images, setImages] = createSignal<ReadonlyMap<string, string | null>>(new Map())
  const requested = new Set<string>()
  // The option pictures each pick field shows, by the field (an object of its own), with its page's node key.
  const [shownOptions, setShownOptions] = createSignal<ReadonlyMap<object, { nodeKey: string; hashes: readonly string[] }>>(new Map())
  const want = (owner: object, nodeKey: string, hashes: readonly string[]) => setShownOptions(now => {
    const was = now.get(owner)
    if (hashes.length ? was?.nodeKey === nodeKey && sameList(was.hashes, hashes) : !was) return now
    const next = new Map(now)
    if (hashes.length) next.set(owner, { nodeKey, hashes })
    else next.delete(owner)
    return next
  })
  let revision: string | undefined
  // While the pages' error boundary shows nothing for pages it could not draw: what draws them again.
  let redraw: (() => void) | undefined
  // The definition shown; a new one, or disposal, ends every answer still to come for the one before.
  let epoch = 0
  let current: string | undefined
  // Reads in the order sent: an answer to an older one never replaces a newer one's.
  let issued = 0, applied = 0
  onCleanup(() => { epoch++ })

  const keepImages = (id: string, pages: DevicePageData[]) => {
    const wanted = new Map<string, string>()
    for (const page of pages) for (const list of page.lists) for (const card of list.cards)
      if (card.image && !wanted.has(card.image)) wanted.set(card.image, page.nodeKey)
    const nodeKeys = new Set(pages.map(page => page.nodeKey))
    for (const { nodeKey, hashes } of untrack(shownOptions).values())
      if (nodeKeys.has(nodeKey)) for (const hash of hashes) if (!wanted.has(hash)) wanted.set(hash, nodeKey)
    for (const hash of [...requested]) if (!wanted.has(hash)) requested.delete(hash)
    setImages(cached => [...cached.keys()].every(hash => wanted.has(hash)) ? cached : new Map([...cached].filter(([hash]) => wanted.has(hash))))
    for (const [hash, nodeKey] of wanted) {
      if (requested.has(hash)) continue
      requested.add(hash)
      const at = epoch
      api.image(id, nodeKey, hash).then(
        url => { if (at === epoch && requested.has(hash)) setImages(cached => new Map(cached).set(hash, url)) },
        error => {
          if (at !== epoch || !requested.has(hash)) return
          // Gone from the card already: shown without it. Any other failure is tried again with the next revision.
          if (error instanceof EntityApiError && error.code === 'not_found') setImages(cached => new Map(cached).set(hash, null))
          else requested.delete(hash)
        })
    }
  }
  // A pick field that shows other pictures (its dialog opened or closed, another option chosen) asks for them at once.
  createEffect(on(shownOptions, () => { const now = shown(); if (current && now.kind === 'pages') keepImages(current, now.pages) }, { defer: true }))
  const stopped = (id: string) => { revision = undefined; setShown({ kind: 'off' }); keepImages(id, []); noteDeviceRead(queryClient, id, undefined) }

  const refresh = async (id: string) => {
    const at = epoch, mine = ++issued
    try {
      const answer = await api.read(id, revision)
      if (at !== epoch || mine < applied) return
      applied = mine
      setFailure()
      if (!answer.running) stopped(id)
      else if ('pages' in answer) {
        // A read counts once its pages are drawn whole; else `revision` stays, so the next read answers them again and
        // they are drawn afresh.
        const again = redraw
        redraw = undefined
        try {
          setShown({ kind: 'pages', pages: answer.pages })
          again?.()
          keepImages(id, answer.pages)
          noteDeviceRead(queryClient, id, answer.pages)
        } catch (error) {
          drawFailed(error)
        }
        if (redraw) { setFailure(drawFailure); return }
        revision = answer.revision
      }
    } catch (error) {
      if (at !== epoch || mine < applied) return
      if (notRunning(error)) { applied = mine; setFailure(); stopped(id) }
      else setFailure(entityError(error))
    }
  }

  createEffect(() => {
    const id = props.definitionId
    const active = props.visible && pageShown()
    untrack(() => {
      if (id === current) return
      current = id
      epoch++
      revision = undefined
      redraw = undefined
      requested.clear()
      setImages(new Map())
      setShown({ kind: 'reading' })
      setFailure()
    })
    if (!id || !active) return
    let timer: ReturnType<typeof setTimeout> | undefined
    let ended = false
    const tick = async () => {
      await refresh(id)
      if (!ended) timer = setTimeout(() => void tick(), pollMs)
    }
    void tick()
    onCleanup(() => { ended = true; clearTimeout(timer) })
  })

  const press: Press = async (nodeKey, list, card, action, values) => {
    const id = props.definitionId
    if (!id) return
    const at = epoch
    try {
      const result = await api.act(id, nodeKey, list, card, action, values)
      countsChanged(queryClient)
      if (at !== epoch) return
      void refresh(id)
      return result
    } catch (error) {
      if (at !== epoch) return
      // A read sent before the press no longer counts once it says the definition stopped.
      if (notRunning(error)) { applied = ++issued; stopped(id); return }
      return { status: 'error', message: entityError(error) }
    }
  }

  const pages = createMemo(() => { const now = shown(); return now.kind === 'pages' ? now.pages : undefined })
  // What threw while the pages were drawn (in a computation, or a cleanup as a card goes) is caught by the error boundary
  // around them, so the rest of the update completes: one that reached `setShown`'s caller would leave the computations
  // it had queued stale, never to run again, and the tab would show the pages it had until the browser's page reloads.
  const drawFailed = (error: unknown, reset: () => void = () => {}) => {
    console.error('HassSharp could not draw the device page.', error)
    redraw = reset
    return undefined
  }
  return (
    <div class="hs-device">
      <Show when={props.definitionId} fallback={<p class="hs-drawer__empty">Save this entity definition to see its device pages.</p>}>
        <Show when={failure()}>{message => <p class="hs-error hs-device__failure" role="alert">{message()}</p>}</Show>
        <Switch>
          <Match when={shown().kind === 'off'}>
            <p class="hs-drawer__empty">This entity definition is not running. Its device pages show while it is on.</p>
          </Match>
          <Match when={pages()?.length === 0}>
            <p class="hs-drawer__empty">None of its C# devices has drawn a page.</p>
          </Match>
          <Match when={pages()}>
            {all => (
              <ErrorBoundary fallback={drawFailed}>
                <For each={all().map(page => page.nodeKey)}>
                  {nodeKey => (
                    <Show when={all().find(page => page.nodeKey === nodeKey)}>
                      {page => <PageSection page={page()} images={images()} press={press} want={want} />}
                    </Show>
                  )}
                </For>
              </ErrorBoundary>
            )}
          </Match>
        </Switch>
      </Show>
    </div>
  )
}

/** A node's page: its title, then its lists. */
function PageSection(props: {
  page: DevicePageData
  images: ReadonlyMap<string, string | null>
  press: Press
  want(owner: object, nodeKey: string, hashes: readonly string[]): void
}) {
  // A field lets go of its pictures as it goes, maybe with this page: what that reads must not be the page's accessor,
  // which throws once its <Show> no longer has the page. The page's <For> keys it by node key, so it never changes.
  const nodeKey = props.page.nodeKey
  const options: PickOptions = {
    get sets() { return props.page.options ?? [] },
    get images() { return props.images },
    want: (owner, hashes) => props.want(owner, nodeKey, hashes),
  }
  return (
    <section class="hs-device__page">
      <h3 class="hs-device__title">{props.page.title}</h3>
      <For each={props.page.lists.map(list => list.id)}>
        {listId => (
          <Show when={props.page.lists.find(list => list.id === listId)}>
            {list => <ListSection nodeKey={props.page.nodeKey} list={list()} images={props.images} options={options} press={props.press} />}
          </Show>
        )}
      </For>
    </section>
  )
}

/** A list: its title and card count, then its cards or its `empty` text. */
function ListSection(props: { nodeKey: string; list: DeviceList; images: ReadonlyMap<string, string | null>; options: PickOptions; press: Press }) {
  return (
    <section class="hs-device__list" classList={{ 'hs-device__list--attention': props.list.attention }} data-list-id={props.list.id}>
      <h4 class="hs-device__list-title">{props.list.title} ({props.list.cards.length})</h4>
      <Show when={props.list.cards.length} fallback={<Show when={props.list.empty}>{empty => <p class="hs-drawer__empty">{empty()}</p>}</Show>}>
        <ul class="hs-device__cards">
          <For each={props.list.cards.map(card => card.id)}>
            {cardId => (
              <Show when={props.list.cards.find(card => card.id === cardId)}>
                {card => (
                  <DeviceCardView nodeKey={props.nodeKey} listId={props.list.id} card={card()}
                    image={card().image ? props.images.get(card().image!) ?? undefined : undefined} options={props.options} press={props.press} />
                )}
              </Show>
            )}
          </For>
        </ul>
      </Show>
    </section>
  )
}

const emptyValue = (field: DeviceField, value: FieldValue) =>
  field.type === 'number' || field.type === 'pick' ? value === null : field.type === 'toggle' ? false : typeof value !== 'string' || value.trim() === ''
/**
 * A field's value as a press sends it: a text or a choice as text, a number or null, a toggle as true or false, a pick
 * field's `{picked: id}`, `{new: {key: value}}` with each of its form's fields, or null for none (sent: a pick field left
 * out reads as the device drew it).
 */
const sentValue = (field: DeviceField, value: FieldValue): unknown => {
  if (field.type !== 'pick')
    return field.type === 'number' ? (typeof value === 'number' ? value : null) : field.type === 'toggle' ? value === true : typeof value === 'string' ? value : ''
  const choice = value as PickChoice
  if (!choice) return null
  if ('picked' in choice) return { picked: choice.picked }
  return { new: Object.fromEntries((field.add?.fields ?? []).map(form => [form.key, sentValue(form, choice.new.has(form.key) ? choice.new.get(form.key)! : form.value)])) }
}
/** Why a field's value cannot be sent, as HassSharp says it: empty while required, or a number outside its range. */
const fieldProblem = (field: DeviceField, value: FieldValue) =>
  field.required && emptyValue(field, value) ? `${field.label} is required.`
    : field.type === 'number' && typeof value === 'number' && field.minimum !== undefined && value < field.minimum ? `${field.label} must be at least ${field.minimum}.`
      : field.type === 'number' && typeof value === 'number' && field.maximum !== undefined && value > field.maximum ? `${field.label} must be at most ${field.maximum}.`
        : undefined

/**
 * A card: its image, title, detail, fields and buttons, and what its last press said. What the person changed in a
 * field wins over the device's value until the card goes, or a press of it answers done.
 */
function DeviceCardView(props: { nodeKey: string; listId: string; card: DeviceCard; image: string | undefined; options: PickOptions; press: Press }) {
  let root!: HTMLLIElement
  // By field key (any text a device chose: a map, never an object's members).
  const [edits, setEdits] = createSignal<ReadonlyMap<string, FieldValue>>(new Map())
  const [missing, setMissing] = createSignal<ReadonlySet<string>>(new Set())
  const [pressing, setPressing] = createSignal<string>()
  const [asking, setAsking] = createSignal<string>()
  const [message, setMessage] = createSignal<{ tone: 'error' | 'note'; text: string }>()
  let disposed = false
  onCleanup(() => { disposed = true })

  // A pick field's choice only while its set has the option, or its form is there: one that went is none.
  const valueOf = (field: DeviceField): FieldValue => {
    const value = edits().has(field.key) ? edits().get(field.key)! : field.value
    return field.type === 'pick' ? pickChoice(field, value, props.options.sets) : value
  }
  const edit = (key: string, value: FieldValue) => {
    setEdits(current => new Map(current).set(key, value))
    if (missing().has(key)) setMissing(current => new Set([...current].filter(k => k !== key)))
  }
  const action = (id: string | undefined) => props.card.actions.find(a => a.id === id)
  // An action drawn without `validate` (by an older backend) validates.
  const validates = (id: string) => action(id)?.validate !== false

  const send = async (actionId: string) => {
    setAsking()
    setMessage()
    setPressing(actionId)
    // An action that does not validate is given the card's values as drawn: what was typed is not sent.
    const values = validates(actionId) ? Object.fromEntries(props.card.fields.map(field => [field.key, sentValue(field, valueOf(field))])) : {}
    const outcome = await props.press(props.nodeKey, props.listId, props.card.id, actionId, values)
    if (disposed) return
    setPressing()
    if (!outcome) return
    if (outcome.status === 'done') setEdits(new Map())
    else if (outcome.status === 'changed') setMessage({ tone: 'note', text: 'This changed meanwhile; the page was refreshed.' })
    else setMessage({ tone: 'error', text: outcome.message || (outcome.status === 'refused' ? 'The device did not take it.' : 'The action failed.') })
  }
  const start = (actionId: string) => {
    const empty = validates(actionId) ? props.card.fields.filter(field => field.required && emptyValue(field, valueOf(field))).map(field => field.key) : []
    if (empty.length) {
      setMissing(new Set(empty))
      const first = [...root.querySelectorAll<HTMLElement>('[data-field]')].find(el => el.dataset.field === empty[0])
      first?.querySelector<HTMLElement>('input, select, button')?.focus()
      return
    }
    // A press that goes ahead, one that does not validate too, clears the marks an earlier stopped press left.
    if (missing().size) setMissing(new Set<string>())
    if (action(actionId)?.confirm) setAsking(actionId)
    else void send(actionId)
  }

  return (
    <li ref={root} class="hs-device-card" data-card-id={props.card.id}>
      <Show when={props.image}>{src => <img class="hs-device-card__image" src={src()} alt="" />}</Show>
      <div class="hs-device-card__body">
        <Show when={props.card.title}>{title => <strong class="hs-device-card__title">{title()}</strong>}</Show>
        <Show when={props.card.detail}>{detail => <p class="hs-device-card__detail">{detail()}</p>}</Show>
        <Show when={props.card.fields.length}>
          <div class="hs-device-card__fields">
            <For each={props.card.fields.map(field => field.key)}>
              {key => (
                <Show when={props.card.fields.find(field => field.key === key)}>
                  {field => <CardField field={field()} value={valueOf(field())} missing={missing().has(key)} edited={edits().has(key)} options={props.options} onEdit={value => edit(key, value)} />}
                </Show>
              )}
            </For>
          </div>
        </Show>
        <Show when={props.card.actions.length}>
          <div class="hs-device-card__actions">
            <For each={props.card.actions.map(a => a.id)}>
              {actionId => (
                <Show when={action(actionId)}>
                  {shown => (
                    <Button variant={variants[shown().style] ?? 'secondary'} disabled={!!pressing()} busy={pressing() === actionId}
                      onClick={() => start(actionId)}>
                      {shown().label}
                    </Button>
                  )}
                </Show>
              )}
            </For>
          </div>
        </Show>
        <Show when={message()}>
          {said => <p class="hs-device-card__message" classList={{ 'hs-error': said().tone === 'error' }} role={said().tone === 'error' ? 'alert' : 'status'}>{said().text}</p>}
        </Show>
      </div>
      <Dialog open={!!asking()} title={action(asking())?.label ?? ''} width="small" onClose={() => setAsking()} footer={<>
        <Button onClick={() => setAsking()}>Cancel</Button>
        <Button variant={action(asking())?.style === 'danger' ? 'danger' : 'primary'} onClick={() => { const id = asking(); if (id) void send(id) }}>
          {action(asking())?.label}
        </Button>
      </>}>
        <p>{action(asking())?.confirm}</p>
      </Dialog>
    </li>
  )
}

/**
 * A field of a card in the definition form's controls, a required one marked as the form marks one (the hint
 * "Required.", and a text's placeholder), or of a pick field's form, with the `problem` its **Use** found. A text
 * records each keystroke, so what is typed counts at once; a number holds its value from the first keystroke until its
 * text is taken (on blur or Enter), so a refresh does not replace the text being typed. A pick field is a
 * {@link PickField} over its page's `options`.
 */
function CardField(props: {
  field: DeviceField
  value: FieldValue
  missing: boolean
  problem?: string
  edited: boolean
  options?: PickOptions
  onEdit(value: FieldValue): void
}) {
  const id = createUniqueId(), hintId = `${id}-hint`, errorsId = `${id}-errors`, listId = `${id}-suggestions`
  // A toggle is never empty: nothing to mark.
  const hint = () => (props.field.required && props.field.type !== 'toggle' ? 'Required.' : undefined)
  // Only a change of the value reaches the control: a new revision with the same value leaves it alone.
  const value = createMemo(() => props.value)
  const errors = createMemo(() => (props.missing ? [`${props.field.label} is required.`] : props.problem ? [props.problem] : []), undefined, { equals: (a, b) => a.join('\n') === b.join('\n') })
  const text = () => (typeof value() === 'string' ? value() as string : '')
  const choices = createMemo(() => [
    { value: '', label: props.field.required ? 'Choose…' : 'None' },
    ...(props.field.options ?? []).map(option => ({ value: option, label: option })),
  ], undefined, { equals: (a, b) => JSON.stringify(a) === JSON.stringify(b) })
  const suggestions = createMemo(() => props.field.suggestions ?? [], undefined, { equals: (a, b) => JSON.stringify(a) === JSON.stringify(b) })
  return (
    <Switch>
      <Match when={props.field.type === 'text'}>
        <div class="hs-device-card__field" onInput={event => props.onEdit((event.target as HTMLInputElement).value)}>
          <TextField name={props.field.key} label={props.field.label} text={text()} required={props.field.required} errors={errors()}
            hint={hint()} placeholder={props.field.required ? 'Required' : undefined}
            list={suggestions().length ? listId : undefined} commit={typed => props.onEdit(typed)} />
          <Show when={suggestions().length}>
            <datalist id={listId}><For each={suggestions()}>{suggestion => <option value={suggestion} />}</For></datalist>
          </Show>
        </div>
      </Match>
      <Match when={props.field.type === 'select'}>
        <div class="hs-device-card__field">
          <SelectField name={props.field.key} label={props.field.label} value={text()} choices={choices()} hint={hint()} errors={errors()} onChange={chosen => props.onEdit(chosen)} />
        </div>
      </Match>
      <Match when={props.field.type === 'number'}>
        <div class="hs-device-card__field" onInput={() => { if (!props.edited) props.onEdit(value()) }}>
          <div class="hs-insp-field hs-def-field" data-field={props.field.key} aria-invalid={errors().length > 0 || undefined}>
            <label class="hs-insp-field__label" for={id}>{props.field.label}</label>
            <NumberWidget id={id} label={props.field.label} widget={{ kind: 'number', multiple: false, min: props.field.minimum, max: props.field.maximum }}
              value={typeof value() === 'number' ? value() as number : undefined} commit={typed => props.onEdit(typeof typed === 'number' ? typed : null)}
              describedBy={describedBy(!!hint() && hintId, errors().length > 0 && errorsId)} invalid={errors().length > 0} disabled={false} compact={false} required={props.field.required} />
            <Show when={hint()}>{said => <small id={hintId} class="hs-field-hint">{said()}</small>}</Show>
            <FieldErrors id={errorsId} list={errors()} />
          </div>
        </div>
      </Match>
      <Match when={props.field.type === 'toggle'}>
        <div class="hs-device-card__field">
          <SwitchField name={props.field.key} label={props.field.label} checked={value() === true} onChange={checked => props.onEdit(checked)} />
        </div>
      </Match>
      <Match when={props.field.type === 'pick' && props.options}>
        {options => (
          <div class="hs-device-card__field">
            {/* Edited: the form holds its values from the moment it shows, so no refresh replaces a number typed in it. */}
            <PickField field={props.field} choice={value() as PickChoice} options={options()} hint={hint()} errors={errors()} onChoose={props.onEdit} problem={fieldProblem}
              Field={form => <CardField field={form.field} value={form.value} missing={false} problem={form.problem} edited onEdit={value => form.onEdit(value as FormValue)} />} />
          </div>
        )}
      </Match>
    </Switch>
  )
}
