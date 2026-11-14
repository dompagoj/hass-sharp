import { createContext, createSignal, For, Match, Show, Switch, useContext, type JSX } from 'solid-js'
import { Dynamic } from 'solid-js/web'
import { Button } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { createLoader, LoadFailed } from '../components/LazyLoad'
import type { VisualCatalogSnapshot } from './contract'
import { csharpTypeParts, csharpTypes, type CSharpPart } from './csharpType'
import { nodeTitle } from './flow/flowModel'
import type { VisualGraph } from './graph'
import type { TypeDefinition } from './library-api'

// Monaco shows the definition: it loads when the first one is shown ({@link DefinitionCode} mounts), never with the
// page or the panel; later ones show at once.
const loadView = createLoader(() => import('../components/Editor/CodeView').then(module => module.CodeView))

/** The definition's code, read only: Monaco once loaded (it starts loading here), plain text until then. */
function DefinitionCode(props: { text: string; name: string }) {
  const view = loadView()
  const plain = () => <pre class="hs-typedef__plain"><code>{props.text}</code></pre>
  return (
    <Show when={view.component()} fallback={
      <Show when={view.failed()} fallback={plain()}>
        <LoadFailed what="code view" error={view.error()} onRetry={view.retry} />
        {plain()}
      </Show>
    }>
      {component => <Dynamic component={component()} text={props.text} label={`C# definition of ${props.name}`} />}
    </Show>
  )
}

/** What opens a type's definition: given by the editor page and the trace view to the panel inside them. */
interface Links { show(type: string, label: string): void }
const LinksContext = createContext<Links>()

export interface TypeDefinitionsProps {
  /** Asks `type_definition`; a type a Custom C# node declares goes with that node's code. */
  load(type: string, code?: { nodeId: string; source: string }): Promise<TypeDefinition>
  /** The graph on view: a Custom C# node's own types are read from its code there. */
  graph(): VisualGraph | undefined
  catalog(): VisualCatalogSnapshot
  /** A run on view (read only): its graph is the run's, the libraries are as they are now. */
  run?: boolean
  /** Opens a node library file in the node library view, at a line. */
  onOpenLibrary?(id: string, line?: number): void
  /** Opens a Custom C# node's code in its code editor. */
  onEditCode?(nodeId: string): void
  children: JSX.Element
}

/**
 * The Custom C# node of the graph whose code declares the type: its signature lists the type, and the type's id is in
 * a Custom C# node's namespace (`NodeLib.Custom_<id>`). A library type a node's pins use is the library's.
 */
export function declaringNode(graph: VisualGraph | undefined, type: string) {
  if (!graph || !type.includes('NodeLib.Custom_')) return
  const ids = [type, type.endsWith('?') ? type.slice(0, -1) : `${type}?`]
  return graph.nodes.find(node => {
    const types = (node.config.signature as { types?: unknown } | undefined)?.types
    return node.kind === 'custom' && typeof node.config.source === 'string' && !!types && typeof types === 'object' && ids.some(id => Object.hasOwn(types, id))
  })
}

type Shown = { type: string; label: string; nodeId?: string; source?: string }
type Answer = { state: 'loading' } | { state: 'ready'; definition: TypeDefinition } | { state: 'failed'; message: string }

/**
 * Makes the C# types in the panel inside it links ({@link CSharpTypeText}) and shows the definition a link opens: the
 * type's C# name, where it is declared (with "Open in library" or "Edit code" where that applies), and the code, read
 * only, with C# colouring. Closing it (Escape, Close) gives focus back to the link.
 */
export function TypeDefinitions(props: TypeDefinitionsProps) {
  const [shown, setShown] = createSignal<Shown>()
  const [answer, setAnswer] = createSignal<Answer>({ state: 'loading' })
  let asked = 0
  const fetch = (target: Shown) => {
    const mine = ++asked
    setAnswer({ state: 'loading' })
    props.load(target.type, target.nodeId !== undefined ? { nodeId: target.nodeId, source: target.source ?? '' } : undefined).then(
      definition => { if (mine === asked) setAnswer({ state: 'ready', definition }) },
      (error: unknown) => { if (mine === asked) setAnswer({ state: 'failed', message: error instanceof Error && error.message ? error.message : 'the request failed' }) },
    )
  }
  const show = (type: string, label: string) => {
    const node = declaringNode(props.graph(), type)
    const target: Shown = { type, label, nodeId: node?.id, source: node ? (node.config.source as string) : undefined }
    setShown(target)
    fetch(target)
  }
  const close = () => { asked++; setShown(undefined) }
  const definition = () => { const a = answer(); return a.state === 'ready' ? a.definition : undefined }
  const node = () => { const id = definition()?.origin.nodeId; return id === undefined ? undefined : props.graph()?.nodes.find(n => n.id === id) }
  const origin = () => {
    const found = definition()
    if (!found) return ''
    if (found.kind === 'metadata') return 'From HassSharp. Only the signatures are shown, not the code inside them.'
    if (found.origin.library !== undefined) return `Declared in the node library ${found.origin.file ?? found.origin.library}.`
    const step = node()
    return step ? `Declared in the code of the step ${nodeTitle(step, props.catalog(), 'Custom C#')}.` : 'Declared in the code of a Custom C# step.'
  }
  const action = (): JSX.Element => {
    const found = definition()
    if (!found) return undefined
    if (found.origin.library !== undefined && props.onOpenLibrary)
      return <Button variant="primary" onClick={() => { const library = found.origin.library!, line = found.line; close(); props.onOpenLibrary?.(library, line) }}>Open in library</Button>
    const step = node()
    if (found.origin.nodeId !== undefined && step && props.onEditCode && !props.run)
      return <Button variant="primary" onClick={() => { close(); props.onEditCode?.(step.id) }}>Edit code</Button>
    return undefined
  }
  return (
    <LinksContext.Provider value={{ show }}>
      {props.children}
      <Dialog open={!!shown()} title={definition()?.name ?? shown()?.label ?? ''} width="large" onClose={close}
        footer={<>{action()}<Button onClick={close}>Close</Button></>}>
        <div class="hs-typedef">
          <Switch>
            <Match when={answer().state === 'loading'}><p class="hs-muted" role="status">Loading the definition…</p></Match>
            <Match when={answer().state === 'failed' && answer()}>
              {failed => (
                <div class="hs-typedef__failed" role="alert">
                  <span>Could not show the definition: {(failed() as { message: string }).message}</span>
                  <Button onClick={() => { const target = shown(); if (target) fetch(target) }}>Try again</Button>
                </div>
              )}
            </Match>
            <Match when={definition()}>
              {found => (
                <>
                  <p class="hs-typedef__origin">{origin()}</p>
                  <Show when={props.run && found().origin.library !== undefined}>
                    <p class="hs-typedef__note">This is the node library as it is now; it may have changed since this run.</p>
                  </Show>
                  <Show when={props.run && found().origin.nodeId !== undefined}>
                    <p class="hs-typedef__note">From the code this run used.</p>
                  </Show>
                  <DefinitionCode text={found().text} name={found().name} />
                  <Show when={found().truncated}><p class="hs-typedef__note">The definition is longer than 64,000 characters; the rest is not shown.</p></Show>
                </>
              )}
            </Match>
          </Switch>
        </div>
      </Dialog>
    </LinksContext.Provider>
  )
}

/**
 * The C# type of one or more catalog types in code style, read after the friendly name as ", C# type bool" (several:
 * "C# types double or string"). Inside {@link TypeDefinitions}, the part of each that names a library, Custom C# or
 * HassSharp type is a link to its definition; elsewhere, and for C#'s own types, it is plain text. A link has the
 * text's metrics, so the row does not move when it appears.
 */
export function CSharpTypeText(props: { types: readonly string[]; catalog: VisualCatalogSnapshot }) {
  const links = useContext(LinksContext)
  const written = () => {
    const seen = new Set<string>(), list: CSharpPart[][] = []
    for (const id of props.types) {
      const parts = csharpTypeParts(id, props.catalog)
      const text = parts?.map(p => p.text).join('')
      if (!parts || text === undefined || seen.has(text)) continue
      seen.add(text)
      list.push(parts)
    }
    return list
  }
  return (
    <Show when={written().length}>
      <span class="hs-visually-hidden">, C# {written().length > 1 ? 'types' : 'type'} </span>
      <code class="hs-csharp-type">
        <For each={written()}>
          {(parts, index) => (
            <>
              {index() > 0 ? ' or ' : ''}
              <For each={parts}>
                {part => (
                  <Show when={links && part.type} fallback={part.text}>
                    <button type="button" class="hs-csharp-link" aria-label={`Show the C# definition of ${part.text}`}
                      onClick={event => { event.stopPropagation(); links!.show(part.type!, part.text) }}>{part.text}</button>
                  </Show>
                )}
              </For>
            </>
          )}
        </For>
      </code>
    </Show>
  )
}

/** The C# text of catalog types, as {@link CSharpTypeText} shows it, for a title or an accessible name. */
export function csharpText(types: readonly string[], catalog: VisualCatalogSnapshot): string {
  return csharpTypes(types, catalog).join(' or ')
}
