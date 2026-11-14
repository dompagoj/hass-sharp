import { createResource, createSignal, Show, type Component, type JSX } from 'solid-js'
import { Dynamic } from 'solid-js/web'
import { Button } from './Controls'

/** Why a file did not load, in words (a failed `import()` gives a TypeError naming the file). */
export const loadReason = (error: unknown) =>
  error instanceof Error && error.message ? error.message : typeof error === 'string' && error ? error : 'the file did not arrive'

export interface Loaded<P extends Record<string, any>> {
  /** The component, once it has loaded. */
  component(): Component<P> | undefined
  /** The load failed: the error (`failed()` says whether there is one). */
  error(): unknown
  failed(): boolean
  /** Loads it again after a failure. */
  retry(): void
}

/**
 * A component loaded on first use (a separate chunk). Unlike Solid's `lazy`, which keeps a failed load for good, a
 * failure is shown and can be tried again: a tab left open across an upgrade, or one that went offline, recovers.
 */
export function createLoader<P extends Record<string, any>>(load: () => Promise<Component<P>>): () => Loaded<P> {
  let loaded: Component<P> | undefined
  return () => {
    const [attempt, setAttempt] = createSignal(1)
    const [component] = createResource(attempt, async () => loaded ?? (loaded = await load()))
    return {
      component: () => (component.state === 'ready' ? component() : undefined),
      error: () => (component.state === 'errored' ? component.error : undefined),
      failed: () => component.state === 'errored',
      retry: () => setAttempt(n => n + 1),
    }
  }
}

/** "Could not load the …: <reason>" with Try again. */
export function LoadFailed(props: { what: string; error: unknown; onRetry(): void; class?: string }) {
  return (
    <div class={`hs-load-failed ${props.class ?? ''}`} role="alert">
      <span>Could not load the {props.what}: {loadReason(props.error)}</span>
      <Button onClick={() => props.onRetry()}>Try again</Button>
    </div>
  )
}

/** A whole view (a route) loaded on first use, with a message and Try again when it does not load. */
export function lazyView<P extends Record<string, any>>(load: () => Promise<Component<P>>, what: string, loading?: JSX.Element): Component<P> {
  const loader = createLoader(load)
  return props => {
    const view = loader()
    return (
      <Show when={view.component()} fallback={<Show when={view.failed()} fallback={loading}><LoadFailed class="hs-load-failed--view" what={what} error={view.error()} onRetry={view.retry} /></Show>}>
        {component => <Dynamic component={component()} {...props} />}
      </Show>
    )
  }
}
