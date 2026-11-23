import { createEffect, createSignal, createUniqueId, For, on, onCleanup, Show } from 'solid-js'
import { useHass } from '../context'
import { Button, Field } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { focusSoon } from '../visual/soon'
import { externalApi, type ExternalImportItem, type ExternalImportResult, type ExternalInspection, type ExternalItem, type ExternalPick } from './api'

export interface AddExternalDialogProps {
  open: boolean
  onClose: () => void
  /** After an import, whatever became of each item: libraries may have been stored even when a definition failed. */
  onImported: (result: ExternalImportResult) => void
}

const warning = 'Node libraries and entity definitions run C# inside Home Assistant with full access to it: its devices, its data, its files and your network. Import only from repositories you trust.'
/** An item's choice: `new`, or `overwrite:<id>` of one of its `existing`; no choice is Skip. */
type Choice = 'new' | `overwrite:${string}`
const key = (item: { kind: string; path: string }) => `${item.kind}:${item.path}`
const githubHref = (owner: string, repo: string, sha: string, path: string) =>
  `https://github.com/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/blob/${sha}/${path.split('/').map(encodeURIComponent).join('/')}`
/** What is picked when the list is shown: what is not stored yet; anything stored is skipped until it is checked. */
const initialChoice = (item: ExternalItem): Choice | undefined => item.problem || item.existing.length ? undefined : 'new'
/** What checking an item picks: a stored item is overwritten (the first of its name), else imported as new. */
const checkedChoice = (item: ExternalItem): Choice => item.existing.length ? `overwrite:${item.existing[0].id}` : 'new'
const pickOf = (item: ExternalItem, choice: Choice): ExternalPick => {
  const target = choice !== 'new' ? item.existing.find(existing => `overwrite:${existing.id}` === choice) : undefined
  return target ? { kind: item.kind, path: item.path, mode: 'overwrite', target: target.id, expectedRevision: target.revision }
    : { kind: item.kind, path: item.path, mode: 'new' }
}
const resultLine = (item: ExternalImportItem) =>
  item.outcome === 'failed' ? `${item.name}: not imported${item.message ? ` — ${item.message}` : ''}`
    : item.outcome === 'overwritten' ? `${item.name}: overwritten`
      : item.enabled === false ? `${item.name}: imported, turned off` : `${item.name}: imported`
const message = (error: unknown) => (error instanceof Error ? error.message : String(error))

/**
 * Add external: a public GitHub repository's address is inspected (its hass_sharp.toml, at one commit), the node
 * libraries and entity definitions it lists are picked, and the picked ones are imported from that commit. The steps
 * are the address, the list and the result; a refused inspect or import says why and stays on its step, and Back on
 * the list or the result goes to the address again, as it was typed, to inspect it again. It cannot be closed while a
 * request is on its way.
 */
export const AddExternalDialog = (props: AddExternalDialogProps) => {
  const api = externalApi(useHass())
  const [step, setStep] = createSignal<'url' | 'list' | 'result'>('url')
  const [url, setUrl] = createSignal('')
  const [busy, setBusy] = createSignal(false)
  const [error, setError] = createSignal<string>()
  const [inspection, setInspection] = createSignal<ExternalInspection>()
  const [choices, setChoices] = createSignal<Record<string, Choice>>({})
  const [result, setResult] = createSignal<ExternalImportResult>()
  let body!: HTMLDivElement
  let disposed = false
  onCleanup(() => { disposed = true })
  createEffect(on(() => props.open, open => {
    if (!open) return
    setStep('url'); setUrl(''); setBusy(false); setError(undefined); setInspection(undefined); setChoices({}); setResult(undefined)
  }))
  const close = () => { if (!busy()) props.onClose() }
  const footerButton = (selector = 'button') => body.closest('dialog')?.querySelector<HTMLElement>(`.hs-dialog-footer ${selector}`)
  const picks = () => (inspection()?.items ?? []).flatMap(item => { const choice = choices()[key(item)]; return choice ? [pickOf(item, choice)] : [] })
  const choose = (item: ExternalItem, choice: Choice | undefined) =>
    setChoices(current => { const next = { ...current }; if (choice) next[key(item)] = choice; else delete next[key(item)]; return next })

  const inspect = async () => {
    const address = url().trim()
    if (!address || busy()) return
    setBusy(true); setError(undefined)
    try {
      const answer = await api.inspect(address)
      if (disposed) return
      setInspection(answer)
      setChoices(Object.fromEntries(answer.items.flatMap(item => { const choice = initialChoice(item); return choice ? [[key(item), choice]] : [] })))
      setStep('list')
      // Inspect is gone with its step: focus goes to the first item that can be picked, else to Cancel.
      focusSoon(() => body.querySelector<HTMLElement>('input[type="checkbox"]:not(:disabled)') ?? footerButton(), body)
    } catch (refused) {
      if (disposed) return
      setError(message(refused))
      // Back at the address, to correct it or try again.
      focusSoon(() => body.querySelector<HTMLElement>('input'), body)
    } finally {
      if (!disposed) setBusy(false)
    }
  }
  const runImport = async () => {
    const inspected = inspection(), picked = picks()
    if (!inspected || !picked.length || busy()) return
    setBusy(true); setError(undefined)
    try {
      const answer = await api.import({ owner: inspected.owner, repo: inspected.repo, sha: inspected.sha, picks: picked })
      if (disposed) return
      setResult(answer)
      setStep('result')
      props.onImported(answer)
      focusSoon(() => footerButton('.hs-button--primary'), body)
    } catch (refused) {
      if (!disposed) setError(message(refused))
    } finally {
      if (!disposed) setBusy(false)
    }
  }

  /** To the address, kept as it was, from the list or the result: a library that changed since is inspected again. */
  const back = () => {
    if (busy()) return
    setStep('url'); setError(undefined); setInspection(undefined); setChoices({}); setResult(undefined)
    focusSoon(() => body.querySelector<HTMLElement>('input'), body)
  }

  /** The repository the list is from, at its ref (when the address named one) and commit. */
  const Repository = (props: { at: { owner: string; repo: string; sha: string }; gitRef: string | null | undefined }) =>
    <p class="hs-external__repo"><strong>{props.at.owner}/{props.at.repo}</strong>{' '}
      <span class="hs-muted">{props.gitRef ? `${props.gitRef} · ` : ''}<code>{props.at.sha.slice(0, 7)}</code></span></p>
  const Group = (group: { title: string; kind: ExternalItem['kind'] }) => {
    const items = () => (inspection()?.items ?? []).filter(item => item.kind === group.kind)
    const headingId = createUniqueId()
    return <Show when={items().length}>
      <section class="hs-external__group" aria-labelledby={headingId}><h3 id={headingId}>{group.title}</h3>
        <ul class="hs-external__items"><For each={items()}>{item => <Item item={item} />}</For></ul>
      </section>
    </Show>
  }
  const Item = (row: { item: ExternalItem }) => {
    const id = createUniqueId()
    const item = row.item, inspected = inspection()!
    const choice = () => choices()[key(item)]
    const described = [item.description && `${id}-description`, item.problem && `${id}-problem`].filter(Boolean).join(' ') || undefined
    return <li class="hs-external__item">
      <label class="hs-external__pick"><input type="checkbox" checked={!!choice()} disabled={!!item.problem || busy()} aria-describedby={described}
        onChange={event => choose(item, event.currentTarget.checked ? checkedChoice(item) : undefined)} /><span id={`${id}-name`}>{item.name}</span></label>
      <Show when={item.description}><p id={`${id}-description`} class="hs-muted">{item.description}</p></Show>
      <Show when={item.problem}><p id={`${id}-problem`} class="hs-error">{item.problem}</p></Show>
      <Show when={choice() && item.existing.length}>
        <select class="hs-input" aria-label={`How to import ${item.name}`} disabled={busy()}
          onChange={event => choose(item, event.currentTarget.value as Choice)}>
          <For each={item.existing}>{existing => <option value={`overwrite:${existing.id}`} selected={choice() === `overwrite:${existing.id}`}>Overwrite {existing.name} ({existing.id})</option>}</For>
          {/* A library has no second copy: every library file shares the NodeLib namespace. */}
          <Show when={item.kind === 'entity'}><option value="new" selected={choice() === 'new'}>Add as new</option></Show>
        </select>
      </Show>
      <a href={githubHref(inspected.owner, inspected.repo, inspected.sha, item.path)} target="_blank" rel="noreferrer" aria-describedby={`${id}-name`}>View on GitHub</a>
    </li>
  }

  return <Dialog open={props.open} title="Add external" preventClose={busy()} onClose={close} footer={
    step() === 'result' ? <><Button variant="ghost" onClick={back}>Back</Button><Button variant="primary" onClick={close}>Close</Button></>
      : <>
        <Button variant="ghost" disabled={busy()} onClick={close}>Cancel</Button>
        <Show when={step() === 'list'}><Button variant="ghost" disabled={busy()} onClick={back}>Back</Button></Show>
        <Show when={step() === 'url'} fallback={<Button variant="primary" busy={busy()} disabled={!picks().length} onClick={() => void runImport()}>Import</Button>}>
          <Button variant="primary" busy={busy()} disabled={!url().trim()} onClick={() => void inspect()}>Inspect</Button>
        </Show>
      </>
  }>
    <div ref={body} class="hs-external" aria-busy={busy() ? 'true' : undefined}>
      <p class="hs-notice hs-external__warning" role="note">{warning}</p>
      <Show when={error()}><p class="hs-notice hs-notice--danger" role="alert">{error()}</p></Show>
      <Show when={step() === 'url'}>
        <form class="hs-stack" onSubmit={event => { event.preventDefault(); void inspect() }}>
          <p>Paste the address of a public GitHub repository with a hass_sharp.toml at its root, such as https://github.com/owner/repo or https://github.com/owner/repo/tree/main.</p>
          <Field label="Repository URL" type="text" inputMode="url" name="url" autofocus readOnly={busy()} value={url()}
            autocomplete="off" spellcheck={false} onInput={event => setUrl(event.currentTarget.value)} />
        </form>
      </Show>
      <Show when={step() === 'list' && inspection()}>{inspected => <>
        <Repository at={inspected()} gitRef={inspected().ref} />
        <Show when={!inspected().items.length}><p class="hs-muted">Its hass_sharp.toml lists nothing to import.</p></Show>
        <Group title="Node libraries" kind="library" />
        <Group title="Entity definitions" kind="entity" />
      </>}</Show>
      <Show when={step() === 'result' && result()}>{done => <>
        <Repository at={done()} gitRef={inspection()?.ref} />
        <div role="status"><ul class="hs-external__result" aria-label="Import result"><For each={done().items}>{item =>
          <li classList={{ 'hs-error': item.outcome === 'failed' }}>{resultLine(item)}</li>}</For></ul></div>
      </>}</Show>
    </div>
  </Dialog>
}
