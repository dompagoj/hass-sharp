import { createSignal, onCleanup, onMount } from 'solid-js'
import * as monaco from 'monaco-editor'

import { useHass } from '../../context'
import type { Diagnostic } from '../../types'
import { customModelUri, useEditorActions, type CustomCheck } from './editor-actions'
import { applyEditorSettings, codeEditorOptions, followTheme, useEditorSettings } from './monaco-setup'

/** What the code editor's dialog reads and asks of the Monaco editor inside it. */
export interface CustomCodeHandle {
  /** The text being edited. */
  source(): string
  checking(): boolean
  /** Why the latest check failed (not the code's own problems), or null. */
  error(): string | null
  diagnostics(): Diagnostic[]
  /** The latest check; kept while the next one runs. */
  check(): CustomCheck | undefined
  retry(): void
  goTo(diagnostic: Diagnostic): void
  /** Replaces the whole text as one step of the editor's own undo. */
  replace(text: string): void
  focus(): void
  vim(): boolean
  setVim(on: boolean): void
}

let sessions = 0

/**
 * Monaco on a Custom C# node's code, in the language-service mode for custom code (see `useEditorActions`): the
 * node's own model, completion, hover, signature help, go to definition and formatting for its unsaved code, and
 * checks by `custom_preview`. Loaded only when the code editor opens; nothing else on the editor page imports it.
 */
export function CustomCodeSurface(props: {
  nodeId: string
  initial: string
  readOnly?: boolean
  /** The editor's accessible name. */
  label: string
  onReady(handle: CustomCodeHandle): void
  /** Ctrl/⌘ S. */
  onApply(): void
}) {
  const hass = useHass()
  let editorRef!: HTMLDivElement
  let statusBarRef!: HTMLDivElement
  const [settings, setSettings] = useEditorSettings()
  const [source, setSource] = createSignal(props.initial)
  // One editing session: language-service replies name it, so a reply to an earlier session is never used.
  const context = { id: props.nodeId, source: '', workspaceVersion: `custom-${++sessions}`, generation: '', sources: [] }
  const actions = useEditorActions(hass, { context: () => context, custom: { readOnly: props.readOnly } })

  onMount(() => {
    const uri = customModelUri(props.nodeId)
    monaco.editor.getModel?.(uri)?.dispose()
    const model = monaco.editor.createModel(props.initial, 'csharp', uri)
    const editor = monaco.editor.create(editorRef, {
      ...codeEditorOptions,
      model,
      readOnly: !!props.readOnly,
      ariaLabel: props.label,
    })
    followTheme(editor, editorRef)
    const changes = editor.onDidChangeModelContent(() => setSource(model.getValue()))
    actions.register(editor, async () => {}, async () => props.onApply())
    applyEditorSettings(editor, settings, () => statusBarRef)
    onCleanup(() => {
      changes.dispose()
      actions.dispose()
      editor.dispose()
      model.dispose()
    })
    props.onReady({
      source,
      checking: actions.validating,
      error: actions.validationError,
      diagnostics: actions.diagnostics,
      check: actions.customCheck,
      retry: actions.revalidate,
      goTo: actions.goToDiagnostic,
      replace: text => {
        editor.pushUndoStop()
        editor.executeEdits('reset', [{ range: model.getFullModelRange(), text }])
        editor.pushUndoStop()
        editor.focus()
      },
      focus: () => editor.focus(),
      vim: () => settings().vimEnabled,
      setVim: on => setSettings(previous => ({ ...previous, vimEnabled: on })),
    })
  })

  return (
    <div class="hs-code-surface">
      <div class="hs-monaco hs-code-monaco" ref={editorRef} />
      <footer class="hs-editor-status hs-code-surface__status">
        <span>C# · Roslyn language services</span>
        <div ref={statusBarRef} />
      </footer>
    </div>
  )
}
