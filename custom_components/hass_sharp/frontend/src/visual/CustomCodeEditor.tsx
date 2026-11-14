import { createEffect, createMemo, createSignal, For, onCleanup, onMount, Show } from 'solid-js'
import { delegateEvents, Dynamic } from 'solid-js/web'
import { Button } from '../components/Controls'
import { Dialog, openModal, trapTab } from '../components/Dialog'
import { createLoader, LoadFailed } from '../components/LazyLoad'
import { Menu, type MenuItem } from '../components/Menu'
import type { CustomCodeHandle } from '../components/Editor/CustomCode'
import { customSignature, wellFormedSignature, type CustomSignature, type GraphNode, type InputSpec, type VisualCatalogSnapshot } from './contract'
import { exampleSource } from './customCode'
import { nodeTitle } from './flow/flowModel'
import { Glyph } from './Glyph'
import { TypeChip } from './InputRow'
import { icons } from './icons'
import { typeColor, typeIsList, typeLabel } from './typeLabels'
import { displayValue } from './widgets/json'

// Monaco and the language services load only when the code editor opens: never in the editor page's first paint. A
// failed load says so, with Try again.
const loadSurface = createLoader(() => import('../components/Editor/CustomCode').then(module => module.CustomCodeSurface))

const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value)
const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`
/** Code that compiles neither with an error of its own: the libraries beside it do not. */
const librariesBroken = 'The node libraries have errors, so this code cannot be used until they are fixed in Node libraries'

export interface CustomCodeEditorProps {
  node: GraphNode
  catalog: VisualCatalogSnapshot
  /** A run on view: the code is shown, never checked or applied. */
  readOnly?: boolean
  /** The code and the pins its latest check gave; the dialog closes after it. */
  onApply?(source: string, signature: CustomSignature): void
  onClose(): void
  /**
   * Whether the text differs from the node's code (the page counts it as unsaved), with the text while it is open;
   * on close, without it.
   */
  onDirtyChange?(dirty: boolean, source?: string): void
  /** Unapplied code to open with (restored from a draft); unapplied, so it counts as unsaved until it is applied. */
  draft?: string
}

/**
 * The code editor of a Custom C# node: a full-screen dialog with the node's title, the state of the latest check,
 * Apply and Cancel; Monaco on the code; the pins the code gives right now; and its problems (each goes to its line).
 * Apply writes the code and its pins as one undo step (the page does that); Cancel with unapplied changes asks first.
 */
export function CustomCodeEditor(props: CustomCodeEditorProps) {
  let dialog!: HTMLDialogElement
  const surface = loadSurface()
  const initial = typeof props.node.config.source === 'string' ? props.node.config.source : ''
  const [handle, setHandle] = createSignal<CustomCodeHandle>()
  const [confirm, setConfirm] = createSignal(false)
  const [help, setHelp] = createSignal(false)
  // The text the editor opens with: the node's code, or unapplied code taken back from a draft.
  const opening = !props.readOnly && props.draft !== undefined ? props.draft : initial
  const source = () => handle()?.source() ?? opening
  const dirty = () => !props.readOnly && source() !== initial
  createEffect(() => props.onDirtyChange?.(dirty(), source()))
  onCleanup(() => props.onDirtyChange?.(false))

  const check = () => handle()?.check()
  const error = () => handle()?.error() ?? null
  const checking = () => !props.readOnly && !surface.failed() && (!handle() || handle()!.checking())
  /** The latest check is of the text as it is now (not one from before an edit). */
  const current = () => { const c = check(); return c && c.source === source() ? c : undefined }
  const checkedSignature = () => { const c = check(); return c && wellFormedSignature(c.signature) ? c.signature : undefined }
  const canApply = () => !props.readOnly && !checking() && !error() && !!current()?.valid && !!checkedSignature()
  const problems = createMemo(() => (props.readOnly ? [] : (handle()?.diagnostics() ?? []).filter(d => d.severity >= 2)))
  /** Invalid code with no problem of its own to show. */
  const unexplained = () => !checking() && !error() && current()?.valid === false && !problems().some(d => d.severity === 3)
  const problemCount = () => problems().length + (unexplained() ? 1 : 0)
  const status = () => {
    if (props.readOnly) return 'Read only'
    if (error() || surface.failed()) return 'Not checked'
    if (checking()) return 'Checking…'
    return problemCount() ? plural(problemCount(), 'problem') : 'No problems'
  }
  const tone = () => (props.readOnly ? 'quiet' : error() || surface.failed() || problems().some(d => d.severity === 3) || unexplained() ? 'error' : checking() ? 'busy' : problemCount() ? 'warning' : 'ok')
  // The pins of the latest code that compiled, else the node's own.
  const shown = () => (checkedSignature() ?? customSignature(props.node, props.catalog))
  const outdated = () => !props.readOnly && !checking() && !!check() && !current()?.valid
  const title = () => nodeTitle(props.node, props.catalog, 'Custom C#')
  // Screen readers hear a change of result (the number of problems, or none), not every "Checking…" in between.
  const announced = createMemo<string>(previous => (props.readOnly || checking() ? previous : status()), '')

  const close = () => props.onClose()
  const cancel = () => (dirty() ? setConfirm(true) : close())
  let applied = false
  const apply = () => {
    const signature = checkedSignature()
    if (applied || !canApply() || !signature) return
    applied = true
    props.onApply?.(source(), structuredClone(signature))
    close()
  }
  /** Ctrl/⌘+S anywhere in the dialog is Apply (in Monaco, its own key binding is), never the browser's Save page. */
  const saveKey = (event: KeyboardEvent) => {
    if (!(event.ctrlKey || event.metaKey) || event.altKey || event.key.toLowerCase() !== 's') return
    if ((event.target as Element).closest?.('.monaco-editor')) return
    event.preventDefault()
    apply()
  }

  // Native modality keeps the rest of the page (and Home Assistant) inert, also over a full-screen editor.
  onMount(() => onCleanup(openModal(dialog)))
  createEffect(() => handle()?.focus())

  const keys = (event: KeyboardEvent) => {
    // Home Assistant's shortcuts and the editor page's never see keys typed here.
    event.stopPropagation()
    if (event.defaultPrevented) return
    const target = event.target as Element
    if (event.key === 'Escape') {
      // Escape that Monaco (a suggestion list, Vim) or a menu used is theirs.
      if (target.closest?.('[role="menu"]')) return
      event.preventDefault()
      cancel()
      return
    }
    trapTab(dialog, event)
  }

  const menu = (): MenuItem[] => [
    ...(props.readOnly ? [] : [{ label: 'Reset to example', onSelect: () => handle()?.replace(exampleSource(props.node.kind, props.catalog)) }]),
    ...(handle() ? [{ label: handle()!.vim() ? 'Turn off Vim mode' : 'Turn on Vim mode', onSelect: () => handle()?.setVim(!handle()!.vim()) }] : []),
  ]

  return (
    <dialog
      ref={element => {
        dialog = element
        delegateEvents(['keydown', 'keyup'], element as unknown as Document)
        // Before the nested dialogs (help, the confirmation) stop the key.
        element.addEventListener('keydown', saveKey, true)
      }}
      class="hs-dialog hs-code-dialog"
      aria-label={`Code of ${title()}${props.readOnly ? ', read only' : ''}`}
      aria-modal="true"
      onCancel={event => { event.preventDefault(); cancel() }}
      on:keydown={keys}
      on:keyup={event => event.stopPropagation()}
      on:keypress={event => event.stopPropagation()}
    >
      <header class="hs-code-bar">
        <span class="hs-code-bar__icon" aria-hidden="true"><Glyph paths={icons.code} /></span>
        <div class="hs-code-bar__titles">
          <span class="hs-code-bar__kind">{props.readOnly ? 'Custom C# code, read only' : 'Custom C# code'}</span>
          <h2 class="hs-code-bar__title">{title()}</h2>
        </div>
        <span class={`hs-code-state hs-code-state--${tone()}`}>{status()}</span>
        <span class="hs-visually-hidden" role="status">{announced()}</span>
        <div class="hs-code-bar__actions">
          <Button variant="ghost" class="hs-icon-button hs-code-help" aria-label="How to write a node" title="How to write a node" onClick={() => setHelp(true)}>?</Button>
          <Show when={menu().length}><Menu label="Code actions" items={menu()} /></Show>
          <Show when={!props.readOnly} fallback={<Button variant="primary" onClick={close}>Close</Button>}>
            <Button onClick={cancel}>Cancel</Button>
            <Button variant="primary" disabled={!canApply()} title={canApply() ? 'Write this code and its pins to the node' : 'Apply when the code has been checked and has no errors'} onClick={apply}>Apply</Button>
          </Show>
        </div>
      </header>
      <div class="hs-code-body">
        <div class="hs-code-main">
          <Show when={surface.component()} fallback={
            <Show when={surface.failed()} fallback={<p class="hs-code-loading">Loading the code editor…</p>}>
              <LoadFailed class="hs-code-failed" what="code editor" error={surface.error()} onRetry={surface.retry} />
            </Show>
          }>
            {component => <Dynamic component={component()} nodeId={props.node.id} initial={opening} readOnly={props.readOnly} label={`Code of ${title()}`} onReady={setHandle} onApply={apply} />}
          </Show>
          <Show when={error()}>
            {reason => (
              <div class="hs-code-failed" role="alert">
                <span>Could not check the code: {reason()}</span>
                <Button onClick={() => handle()?.retry()}>Try again</Button>
              </div>
            )}
          </Show>
          <Show when={problemCount()}>
            <section class="hs-code-problems" aria-label="Problems">
              <ul>
                <For each={problems()}>
                  {problem => (
                    <li>
                      <button type="button" class="hs-code-problem" classList={{ 'hs-code-problem--warning': problem.severity !== 3 }} onClick={() => handle()?.goTo(problem)}>
                        <Glyph paths={problem.severity === 3 ? icons.problem : icons.warning} />
                        <span class="hs-code-problem__where">Line {problem.startLine}, column {problem.startColumn}</span>
                        <span class="hs-code-problem__message">{problem.message}</span>
                      </button>
                    </li>
                  )}
                </For>
                <Show when={unexplained()}>
                  <li><span class="hs-code-problem"><Glyph paths={icons.problem} /><span class="hs-code-problem__message">{librariesBroken}</span></span></li>
                </Show>
              </ul>
            </section>
          </Show>
        </div>
        <Pins signature={shown()} catalog={props.catalog} outdated={outdated()} />
      </div>
      <Dialog open={confirm()} title="Discard your changes to the code?" width="small" onClose={() => setConfirm(false)}
        footer={<><Button onClick={() => setConfirm(false)}>Keep editing</Button><Button variant="danger" onClick={() => { setConfirm(false); close() }}>Discard</Button></>}>
        <p>The node keeps its code as it was before you opened the editor.</p>
      </Dialog>
      <CustomCodeHelp open={help()} onClose={() => setHelp(false)} />
    </dialog>
  )
}

/** The pins the code gives, as the node will show them: inputs, outputs, and whether it is a value node or a step. */
function Pins(props: { signature?: CustomSignature; catalog: VisualCatalogSnapshot; outdated: boolean }) {
  // The code's own types are named from its signature.
  const types = () => (props.signature && Object.keys(props.signature.types).length ? { ...props.catalog, types: { ...props.signature.types, ...props.catalog.types } } : props.catalog)
  const chip = (type: string) => <TypeChip text={typeLabel(type, types())} color={typeColor(type, types())} list={typeIsList(type, types())} />
  const fallback = (input: InputSpec) => {
    const value = record(input.default) && input.default.kind === 'literal' ? input.default.value : undefined
    return value === undefined ? undefined : value === null ? 'none' : displayValue(value as never) || 'empty text'
  }
  return (
    <aside class="hs-code-pins" aria-label="Pins">
      <h3 class="hs-insp-heading">Pins</h3>
      <Show when={props.signature} fallback={<p class="hs-muted">The code gives no pins yet.</p>}>
        {signature => (
          <>
            <Show when={props.outdated}><p class="hs-code-pins__note">From the last code that compiled.</p></Show>
            <p class="hs-code-pins__kind">
              {signature().pure ? 'Value node: worked out each time a step uses it, outside the step order.' : 'Step: runs in the step order.'}
            </p>
            <h4 class="hs-code-pins__heading">Inputs</h4>
            <Show when={Object.keys(signature().inputs).length} fallback={<p class="hs-muted">None</p>}>
              <ul class="hs-code-pins__list">
                <For each={Object.entries(signature().inputs)}>
                  {([id, input]) => (
                    <li class="hs-code-pin" data-pin-id={id}>
                      <span class="hs-code-pin__name">{input.label ?? id}</span>
                      <span class="hs-code-pin__types">
                        <For each={input.types}>{type => chip(type)}</For>
                      </span>
                      <span class="hs-code-pin__meta">
                        {input.required ? 'Required' : 'Optional'}
                        <Show when={fallback(input)}>{text => <> · default {text()}</>}</Show>
                      </span>
                    </li>
                  )}
                </For>
              </ul>
            </Show>
            <h4 class="hs-code-pins__heading">Outputs</h4>
            <Show when={Object.keys(signature().outputs).length} fallback={<p class="hs-muted">None</p>}>
              <ul class="hs-code-pins__list">
                <For each={Object.entries(signature().outputs)}>
                  {([id, output]) => (
                    <li class="hs-code-pin" data-pin-id={id}>
                      <span class="hs-code-pin__name">{output.label ?? id}</span>
                      <span class="hs-code-pin__types">{chip(output.type)}</span>
                    </li>
                  )}
                </For>
              </ul>
            </Show>
          </>
        )}
      </Show>
    </aside>
  )
}

/**
 * The step whose code was open went away under the editor (a reload of the automation, say) while its code had
 * unapplied changes: says so, and offers the code to copy.
 */
export function CodeLeftBehind(props: { source?: string; onClose(): void }) {
  const [copied, setCopied] = createSignal<'idle' | 'copied' | 'failed'>('idle')
  let text: HTMLTextAreaElement | undefined
  const copy = async () => {
    try { await navigator.clipboard.writeText(props.source ?? ''); setCopied('copied') }
    catch { setCopied('failed'); text?.focus(); text?.select() }
  }
  const close = () => { setCopied('idle'); props.onClose() }
  return (
    <Dialog open={props.source !== undefined} title="This step no longer exists" width="medium" onClose={close}
      footer={<>
        <Button onClick={close}>Close</Button>
        <Show when={typeof navigator.clipboard?.writeText === 'function'}>
          <Button variant="primary" onClick={() => void copy()}>{copied() === 'copied' ? 'Copied' : 'Copy the code'}</Button>
        </Show>
      </>}>
      <p>The Custom C# step whose code you were editing was removed, so your changes to its code were not applied. Copy the code to keep it.</p>
      <Show when={copied() === 'failed'}><p class="hs-muted" role="alert">Could not copy: select the code below and copy it.</p></Show>
      <textarea ref={text} class="hs-code-left" readOnly aria-label="The code that was not applied" rows={10} value={props.source ?? ''} />
    </Dialog>
  )
}

/** A short reference for writing a node's code, in the style of the editor's help. */
export function CustomCodeHelp(props: { open: boolean; onClose(): void }) {
  return (
    <Dialog open={props.open} title="Writing a node in C#" width="large" onClose={props.onClose}>
      <p>The code is one static class with exactly one method marked <code>[Node("Title")]</code>. Helper methods and types can sit beside it, and <code>using</code> lines go at the top.</p>
      <h3>Inputs</h3>
      <p>Each parameter is an input pin. The parameter name is the pin's id; its label is the name in words (<code>roomName</code> becomes "Room name"), or the text of <code>[Port("Label")]</code> on the parameter. Optional parameters keep their C# defaults.</p>
      <h3>Outputs</h3>
      <p>The return value is the output <code>result</code>, labelled "Result" or by <code>[return: Port("Label")]</code>. A tuple whose elements are all named gives one output per element: <code>(double Average, double Max)</code> gives <code>average</code> and <code>max</code>. <code>void</code>, <code>Task</code> and <code>ValueTask</code> give no output; a <code>Task&lt;T&gt;</code> or <code>ValueTask&lt;T&gt;</code> is awaited and its value is the output.</p>
      <h3>Value nodes</h3>
      <p><code>[Node("Title", Pure = true)]</code> makes the method a value node, like Constant or Compare: it has no place in the step order and is worked out each time a step uses its result. It must return a value.</p>
      <h3>Supplied by the run</h3>
      <p>A <code>CancellationToken</code> or <code>NodeContext</code> parameter is not a pin; the run passes it in. <code>NodeContext</code> has:</p>
      <ul>
        <li><code>CancellationToken</code>: cancelled when the run stops;</li>
        <li><code>RunId</code>: as the run history shows it;</li>
        <li><code>Trigger</code>: the trigger's Data output;</li>
        <li><code>Error</code>: the most recent failure on this path, or null;</li>
        <li><code>State(entityId)</code>: reads an entity as Read entity does;</li>
        <li><code>Services</code>: this Home Assistant's services as typed methods, such as <code>context.Services.Light.TurnOn(...)</code>;</li>
        <li><code>CallService(service, target, data, returnResponse)</code>: calls a service by its id, as Call service does, and waits for it;</li>
        <li><code>Log(message, level)</code>: writes to the Home Assistant log.</li>
      </ul>
      <h3>States and services</h3>
      <p>Read a state with <code>context.State("sensor.x")</code>.</p>
      <p>Call a service with <code>await context.Services.Light.TurnOn(light, brightnessPct: 30)</code>; the methods follow this Home Assistant's own services. Domains and services are in PascalCase and fields in camelCase (<code>brightness_pct</code> is <code>brightnessPct</code>); the call sends the original ids. The target comes first: an entity ID, a list such as <code>["light.a", "light.b"]</code>, or a <code>ServiceTarget</code> such as <code>ServiceTarget.Areas("kitchen")</code>. Only the fields you give are sent.</p>
      <p>For a service id known only at run time, use <code>context.CallService("domain.service", target, data)</code>.</p>
      <p>If a service goes away, code that calls it stops compiling at its next compile; running automations keep running.</p>
      <h3>Descriptions</h3>
      <p>The XML <code>&lt;summary&gt;</code> is the node's description, each <code>&lt;param&gt;</code> describes an input and <code>&lt;returns&gt;</code> describes the output.</p>
    </Dialog>
  )
}
