import { batch, createEffect, createMemo, createSignal, createUniqueId, For, Match, onCleanup, Show, Switch, type JSX } from 'solid-js'
import { Button } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { describedBy } from '../visual/widgets/common'
import type { DeviceField, DeviceOption, DeviceOptionSet } from './deviceUi'
import { FieldErrors } from './formFields'

/** The most rows the dialog renders; the others are counted, for the person to narrow down by typing. */
const shownRows = 50
/** The active row when it is the form's, "+ {title}", under the options. */
const addRow = Symbol('add')

/** A value of a field of a pick field's form: a text or an option, a number or null, a toggle. */
export type FormValue = DeviceField['value']
/** A pick field's choice: one of its set's options, the values of a new one's form by key, or none. */
export type PickChoice = { picked: string } | { new: ReadonlyMap<string, FormValue> } | null

/** What a page's pick fields draw from: its sets of options, the pictures fetched by hash, and what asks for them. */
export interface PickOptions {
  readonly sets: readonly DeviceOptionSet[]
  readonly images: ReadonlyMap<string, string | null>
  /** The pictures, by hash, that `owner` shows now; none lets go of every one it asked for. */
  want(owner: object, hashes: readonly string[]): void
}

/** A field of a pick field's form, drawn as a card's field is, with the problem **Use** found in it. */
export interface FormFieldProps { field: DeviceField; value: FormValue; problem: string | undefined; onEdit(value: FormValue): void }

export const sameList = (a: readonly unknown[], b: readonly unknown[]) => a.length === b.length && a.every((item, i) => item === b[i])
const itemsOf = (field: DeviceField, sets: readonly DeviceOptionSet[]) => sets.find(set => set.id === field.set)?.items ?? []

/**
 * A pick field's choice as it stands: an option while the field's set has it and the field offers it, a new one while
 * the field has a form, else none. `value` is the id the device drew (or null), or a choice the person made.
 */
export function pickChoice(field: DeviceField, value: DeviceField['value'] | PickChoice, sets: readonly DeviceOptionSet[]): PickChoice {
  const choice = typeof value === 'string' ? { picked: value } : typeof value === 'object' ? value : null
  if (!choice) return null
  if ('picked' in choice) return !field.except?.includes(choice.picked) && itemsOf(field, sets).some(option => option.id === choice.picked) ? choice : null
  return field.add ? choice : null
}

/** A new one's summary: its form's filled text fields, joined by a space. */
const summary = (field: DeviceField, values: ReadonlyMap<string, FormValue>) =>
  (field.add?.fields ?? []).filter(form => form.type === 'text').map(form => values.get(form.key)).filter((text): text is string => typeof text === 'string' && text.trim() !== '').map(text => text.trim()).join(' ')

/**
 * A card's pick field: under its label, a button showing the option chosen (its picture, title and detail), a new
 * one's summary with a "New" mark, or "Choose…", marked as the card's other fields are. The button opens a dialog
 * titled with the label: a search box (a combobox, focused) over a list of the set's options the field offers, at most
 * 50 rendered and the rest counted, filtered by title and detail without regard to case. Arrow keys move, Enter or a
 * click chooses, which closes the dialog and gives the focus back to the button; Escape or Cancel closes it with no
 * change. With a form, a last row "+ {title}" shows the form in place of the list, its first text field holding what
 * was typed; **Use** checks it as a card's fields are checked and closes with the new one chosen, **Back** returns to
 * the list. The dialog's state lives as long as the field does, so a refresh leaves it, its text and its scroll, and
 * drops from it only the options that went. The field asks for the chosen option's picture, and while the dialog is
 * open for the pictures of the rows it has rendered since it opened: never for a row it did not render.
 */
export function PickField(props: {
  field: DeviceField
  choice: PickChoice
  options: PickOptions
  hint: string | undefined
  errors: string[]
  onChoose(choice: PickChoice): void
  /** Draws a field of the form. */
  Field(props: FormFieldProps): JSX.Element
  /** Why a form field's value cannot be used, as a card's press would be refused: none when it can. */
  problem(field: DeviceField, value: FormValue): string | undefined
}) {
  const id = createUniqueId(), labelId = `${id}-label`, hintId = `${id}-hint`, errorsId = `${id}-errors`, listId = `${id}-list`
  const FormField = props.Field
  const owner = {}
  let button!: HTMLButtonElement, body!: HTMLDivElement, searchBox: HTMLInputElement | undefined
  const [open, setOpen] = createSignal(false)
  const [query, setQuery] = createSignal('')
  // The row the arrow keys are on: an option's id, or the form's row; the first row while it is none of those shown.
  const [active, setActive] = createSignal<string | typeof addRow>()
  // The options whose rows the open dialog has rendered, by id.
  const [seen, setSeen] = createSignal<ReadonlySet<string>>(new Set())
  // While the form is shown in place of the list: its values by key (any text a device chose: a map), and its problems.
  const [form, setForm] = createSignal<ReadonlyMap<string, FormValue>>()
  const [problems, setProblems] = createSignal<ReadonlyMap<string, string>>(new Map())

  const items = createMemo(() => itemsOf(props.field, props.options.sets))
  const byId = createMemo(() => new Map(items().map(option => [option.id, option])))
  const offered = createMemo(() => props.field.except?.length ? items().filter(option => !props.field.except!.includes(option.id)) : items())
  const matches = createMemo(() => {
    const q = query().trim().toLocaleLowerCase()
    return q ? offered().filter(option => `${option.title} ${option.detail ?? ''}`.toLocaleLowerCase().includes(q)) : offered()
  })
  const rendered = createMemo(() => matches().slice(0, shownRows).map(option => option.id), [], { equals: sameList })
  const more = () => matches().length - rendered().length
  const rows = createMemo((): (string | typeof addRow)[] => props.field.add ? [...rendered(), addRow] : rendered())
  const activeIndex = () => { const at = rows().indexOf(active()!); return at >= 0 ? at : rows().length ? 0 : -1 }
  const rowId = (index: number) => rows()[index] === addRow ? `${listId}-add` : `${listId}-${index}`
  const picked = () => (props.choice && 'picked' in props.choice ? props.choice.picked : undefined)
  const chosen = () => { const option = picked(); return option === undefined ? undefined : byId().get(option) }
  const made = () => (props.choice && 'new' in props.choice ? props.choice.new : undefined)
  const picture = (hash: string | null) => (hash ? props.options.images.get(hash) ?? undefined : undefined)

  createEffect(() => {
    if (!open()) return
    const ids = rendered()
    setSeen(now => ids.every(option => now.has(option)) ? now : new Set([...now, ...ids]))
  })
  const pictures = createMemo(() => {
    const hashes = new Set<string>()
    const image = chosen()?.image
    if (image) hashes.add(image)
    if (open()) for (const option of seen()) { const shown = byId().get(option)?.image; if (shown) hashes.add(shown) }
    return [...hashes]
  }, [], { equals: sameList })
  // Taken now: the cleanup runs as the field's card, list or page goes, when reading a prop would go through a <Show>
  // whose condition no longer holds and throw.
  const { want } = props.options
  createEffect(() => want(owner, pictures()))
  onCleanup(() => want(owner, []))

  const show = () => {
    if (open()) return
    batch(() => {
      setQuery(''); setActive(picked()); setSeen(new Set<string>()); setForm(); setProblems(new Map())
      setOpen(true)
    })
  }
  const close = () => { setOpen(false); button.focus() }
  // One update: the picture of the option chosen is never let go of between the dialog closing and the choice.
  const choose = (choice: PickChoice) => { batch(() => { props.onChoose(choice); setOpen(false) }); button.focus() }
  const formFields = () => props.field.add?.fields ?? []
  const formValue = (field: DeviceField) => { const values = form(); return values?.has(field.key) ? values.get(field.key)! : field.value }
  const showForm = () => {
    const earlier = made()
    const values = new Map(formFields().map(field => [field.key, earlier?.has(field.key) ? earlier.get(field.key)! : field.value]))
    const typed = query().trim(), first = formFields().find(field => field.type === 'text')
    if (first && typed) values.set(first.key, typed)
    batch(() => { setForm(values); setProblems(new Map()) })
    body.querySelector<HTMLElement>('input, select')?.focus()
  }
  const back = () => { batch(() => { setForm(); setProblems(new Map()) }); searchBox?.focus() }
  const editForm = (key: string, value: FormValue) => {
    setForm(now => now && new Map(now).set(key, value))
    if (problems().has(key)) setProblems(now => new Map([...now].filter(([k]) => k !== key)))
  }
  const use = () => {
    const found = new Map<string, string>()
    for (const field of formFields()) { const problem = props.problem(field, formValue(field)); if (problem) found.set(field.key, problem) }
    if (found.size) {
      setProblems(found)
      const first = [...found.keys()][0]
      const shown = [...body.querySelectorAll<HTMLElement>('[data-field]')].find(el => el.dataset.field === first)
      shown?.querySelector<HTMLElement>('input, select')?.focus()
      return
    }
    choose({ new: new Map(formFields().map(field => [field.key, formValue(field)])) })
  }
  // Enter in a text or a number of the form presses Use; a select and a toggle keep their own keys.
  const formKeydown = (event: KeyboardEvent) => {
    const input = event.target
    if (event.key !== 'Enter' || event.isComposing || !(input instanceof HTMLInputElement) || input.type === 'checkbox') return
    event.preventDefault()
    // Blurred, the field takes its text, as a number holds it until then; one it cannot take keeps the focus and its message.
    input.blur()
    const key = input.closest<HTMLElement>('[data-field]')?.dataset.field
    if (input.getAttribute('aria-invalid') === 'true' && key !== undefined && !problems().has(key)) { input.focus(); return }
    use()
  }
  const keydown = (event: KeyboardEvent & { currentTarget: HTMLInputElement }) => {
    const count = rows().length
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      if (!count) return
      setActive(rows()[(activeIndex() + (event.key === 'ArrowDown' ? 1 : -1) + count) % count])
      ;(event.currentTarget.getRootNode() as Document | ShadowRoot).getElementById?.(rowId(activeIndex()))?.scrollIntoView?.({ block: 'nearest' })
    } else if (event.key === 'Enter' && !event.isComposing && count) {
      event.preventDefault()
      const row = rows()[activeIndex()]
      if (row === addRow) showForm()
      else choose({ picked: row })
    }
  }

  const Face = (face: { option: DeviceOption }) => <>
    <Show when={picture(face.option.image)} fallback={<span class="hs-pick__picture" aria-hidden="true" />}>{src => <img class="hs-pick__picture" src={src()} alt="" />}</Show>
    <span class="hs-pick__text"><strong>{face.option.title}</strong><Show when={face.option.detail}>{detail => <small>{detail()}</small>}</Show></span>
  </>
  return <>
    <div class="hs-insp-field hs-def-field" data-field={props.field.key} aria-invalid={props.errors.length > 0 || undefined}>
      <label id={labelId} class="hs-insp-field__label" for={id}>{props.field.label}</label>
      <button ref={button} id={id} type="button" class="hs-input hs-pick__button" aria-haspopup="dialog" aria-expanded={open()}
        aria-labelledby={`${labelId} ${id}`} aria-describedby={describedBy(!!props.hint && hintId, props.errors.length > 0 && errorsId)}
        aria-invalid={props.errors.length > 0 || undefined} onClick={show}>
        <Switch fallback={<span class="hs-pick__text hs-muted">Choose…</span>}>
          <Match when={chosen()}>{option => <Face option={option()} />}</Match>
          <Match when={made()}>{values => <span class="hs-pick__text"><span><span class="hs-pick__new">New</span> {summary(props.field, values())}</span></span>}</Match>
        </Switch>
        <span class="hs-pick__caret" aria-hidden="true">▾</span>
      </button>
      <Show when={props.hint}>{said => <small id={hintId} class="hs-field-hint">{said()}</small>}</Show>
      <FieldErrors id={errorsId} list={props.errors} />
    </div>
    <Dialog open={open()} title={props.field.label} onClose={close} footer={
      <Show when={form()} fallback={<Button onClick={close}>Cancel</Button>}>
        <Button onClick={back}>Back</Button>
        <Button variant="primary" onClick={use}>Use</Button>
      </Show>
    }>
      <div ref={body} class="hs-pick">
        <Show when={open()}>
          <Show when={form()} fallback={<>
            <input ref={searchBox} autofocus class="hs-input" role="combobox" aria-label="Search" aria-autocomplete="list" aria-expanded="true"
              aria-controls={listId} aria-activedescendant={activeIndex() >= 0 ? rowId(activeIndex()) : undefined}
              placeholder="Search…" autocomplete="off" value={query()}
              onInput={event => { setQuery(event.currentTarget.value); setActive() }} on:keydown={keydown} />
            <Show when={!rendered().length}><p class="hs-muted" role="status">No match</p></Show>
            <div class="hs-pick__list" role="listbox" id={listId} aria-label={`${props.field.label} options`} onMouseDown={event => event.preventDefault()}>
              <For each={rendered()}>
                {(option, index) => (
                  <Show when={byId().get(option)}>
                    {shown => (
                      <div role="option" id={rowId(index())} class="hs-pick__row" classList={{ 'hs-search-current': option === picked() }}
                        aria-selected={activeIndex() === index()} onClick={() => choose({ picked: option })}>
                        <Face option={shown()} />
                        <Show when={option === picked()}><span class="hs-pick__chosen">Chosen</span></Show>
                      </div>
                    )}
                  </Show>
                )}
              </For>
              <Show when={more() > 0}><div class="hs-pick__more hs-muted" role="presentation">{more()} more: type to narrow</div></Show>
              <Show when={props.field.add}>
                {add => (
                  <div role="option" id={`${listId}-add`} class="hs-pick__row hs-pick__add" aria-selected={activeIndex() === rows().length - 1} onClick={showForm}>
                    + {add().title}
                  </div>
                )}
              </Show>
            </div>
          </>}>
            <h3 class="hs-pick__form-title">{props.field.add?.title}</h3>
            {/* Native: Dialog stops keydown at the dialog, so a delegated handler, which listens on the document, never runs. */}
            <div class="hs-pick__form" on:keydown={formKeydown}>
              <For each={formFields().map(field => field.key)}>
                {key => (
                  <Show when={formFields().find(field => field.key === key)}>
                    {field => <FormField field={field()} value={formValue(field())} problem={problems().get(key)} onEdit={value => editForm(key, value)} />}
                  </Show>
                )}
              </For>
            </div>
          </Show>
        </Show>
      </div>
    </Dialog>
  </>
}
