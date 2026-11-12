import * as monaco from 'monaco-editor'
import type { CompletionItem, CompletionTextEdit, Diagnostic, HomeAssistant } from '../../types'
import type { MessageBase } from 'home-assistant-js-websocket'
import { createSignal, onCleanup } from 'solid-js'

function itemToLabel(item: CompletionItem) {
  if (!item.displayTextPrefix && !item.displayTextSuffix) return item.displayText

  const res = `${item.displayTextPrefix}${item.displayText}${item.displayTextSuffix}`

  return res
}

const tagToKindMap: { [key: string]: monaco.languages.CompletionItemKind } = {
  Method: monaco.languages.CompletionItemKind.Method,
  Class: monaco.languages.CompletionItemKind.Class,
  Property: monaco.languages.CompletionItemKind.Property,
  Enum: monaco.languages.CompletionItemKind.Enum,
  Delegate: monaco.languages.CompletionItemKind.Function,
  Keyword: monaco.languages.CompletionItemKind.Keyword,
  Structure: monaco.languages.CompletionItemKind.Struct,
  ExtensionMethod: monaco.languages.CompletionItemKind.Function,
  Local: monaco.languages.CompletionItemKind.Variable,
  Interface: monaco.languages.CompletionItemKind.Interface,
  Snippet: monaco.languages.CompletionItemKind.Snippet,
  TypeParameter: monaco.languages.CompletionItemKind.TypeParameter,
  Namespace: monaco.languages.CompletionItemKind.Module,
  Field: monaco.languages.CompletionItemKind.Field,
  Parameter: monaco.languages.CompletionItemKind.Variable,
}

function tagsToKind(tags: string[]) {
  const first = tags[0]
  const found = tagToKindMap[first]

  if (found !== undefined) return found

  console.warn('Unknown tag: ', first)
  return monaco.languages.CompletionItemKind.Snippet
}

// Each registration owns its requests and providers; responses are tied to the exact model revision.
export const useEditorActions = (hass: HomeAssistant) => {
  const [validating, setValidating] = createSignal(true)
  const [hasErrors, setHasErrors] = createSignal(false)
  const [diagnostics, setDiagnostics] = createSignal<Diagnostic[]>([])
  const [validationError, setValidationError] = createSignal<string | null>(null)
  let navigate: (diagnostic: Diagnostic) => void = () => {}
  const [saving, setSaving] = createSignal(false)
  let disposeRegistration: (() => void) | undefined
  let saveCommand: () => Promise<void> = async () => {}

  const register = (
    editor: monaco.editor.IStandaloneCodeEditor,
    onSave: (model: monaco.editor.ITextModel) => Promise<unknown>,
    runSave: () => Promise<unknown> = () => saveCommand(),
  ) => {
    disposeRegistration?.()
    let disposed = false
    let validationTimer: ReturnType<typeof setTimeout> | undefined
    let completionTimer: ReturnType<typeof setTimeout> | undefined
    let completionPosition: monaco.IPosition | null = null
    const cancelCompletionRetry = () => {
      clearTimeout(completionTimer)
      completionTimer = undefined
      completionPosition = null
    }
    let savingPromise: Promise<void> | undefined
    let markedModel = editor.getModel()
    const pending = new Map<string, () => void>()
    const latest = new Map<string, number>()
    let nextRequest = 0
    let epoch = 0
    type Reply<T> = { value: T; isCurrent: () => boolean }

    const invalidate = () => {
      epoch++
      for (const cancel of pending.values()) cancel()
      pending.clear()
    }
    const owns = (model: monaco.editor.ITextModel) =>
      !disposed && !model.isDisposed() && editor.getModel() === model

    // callWS has no transport abort API. Cancellation settles the consumer immediately;
    // the late server reply is ignored, including after model changes or disposal.
    const request = <T>(kind: string, model: monaco.editor.ITextModel, fields: object = {}, token?: monaco.CancellationToken) => {
      pending.get(kind)?.()
      if (!owns(model) || token?.isCancellationRequested) return Promise.resolve<Reply<T> | null>(null)
      const version = model.getVersionId()
      const requestEpoch = epoch
      const requestId = ++nextRequest
      latest.set(kind, requestId)
      const isCurrent = () => owns(model) && model.getVersionId() === version && epoch === requestEpoch
        && latest.get(kind) === requestId && !token?.isCancellationRequested
      return new Promise<Reply<T> | null>((resolve, reject) => {
        let settled = false
        let subscription: monaco.IDisposable | undefined
        const finish = (value: Reply<T> | null, error?: unknown) => {
          if (settled) return
          settled = true
          subscription?.dispose()
          if (pending.get(kind) === cancel) pending.delete(kind)
          if (error) reject(error)
          else resolve(value)
        }
        const cancel = () => finish(null)
        pending.set(kind, cancel)
        subscription = token?.onCancellationRequested(cancel)
        if (settled) { subscription?.dispose(); return }
        const message: MessageBase = {
          type: `hass_sharp/${kind}`,
          source: model.getValue(),
          modelUri: model.uri.toString(),
          modelVersion: version,
          ...fields,
        }
        hass.callWS<T>(message).then(
          value => finish(isCurrent() ? { value, isCurrent } : null),
          error => finish(null, isCurrent() ? error : undefined),
        )
      })
    }

    const clearDiagnostics = () => {
      setDiagnostics([])
      setValidationError(null)
      setHasErrors(false)
      const model = editor.getModel()
      if (model && owns(model)) monaco.editor.setModelMarkers(model, 'csharp', [])
    }
    navigate = diagnostic => {
      if (disposed || !diagnostics().includes(diagnostic)) return
      const range = { startLineNumber: diagnostic.startLine, startColumn: diagnostic.startColumn,
        endLineNumber: diagnostic.endLine, endColumn: diagnostic.endColumn }
      editor.setSelection(range)
      editor.revealRangeInCenter(range)
      editor.focus()
    }
    const validate = async (): Promise<boolean> => {
      clearTimeout(validationTimer)
      const model = editor.getModel()
      if (!model || !owns(model)) return false
      setValidating(true)
      try {
        const diagnostics = await request<Diagnostic[]>('get_diagnostics', model)
        if (!diagnostics?.isCurrent()) return false
        const markers: monaco.editor.IMarkerData[] = diagnostics.value.map(d => ({
          startLineNumber: d.startLine,
          startColumn: d.startColumn,
          endLineNumber: d.endLine,
          endColumn: d.endColumn,
          message: d.message,
          origin: 'Compiler',
          severity: d.severity === 3 ? monaco.MarkerSeverity.Error
            : d.severity === 2 ? monaco.MarkerSeverity.Warning : monaco.MarkerSeverity.Info,
        }))
        monaco.editor.setModelMarkers(model, 'csharp', markers)
        setDiagnostics(diagnostics.value)
        setValidationError(null)
        const errors = markers.some(m => m.severity === monaco.MarkerSeverity.Error)
        setHasErrors(errors)
        setValidating(false)
        return !errors
      } catch (error) {
        if (owns(model)) { setDiagnostics([]); monaco.editor.setModelMarkers(model, 'csharp', []); setValidationError(String(error)); setHasErrors(true); setValidating(false) }
        console.error('Failed to get diagnostics', error)
        return false
      }
    }

    saveCommand = () => {
      if (savingPromise) return savingPromise
      const model = editor.getModel()
      if (!model || !owns(model)) return Promise.resolve()
      setSaving(true)
      savingPromise = (async () => {
        const version = model.getVersionId()
        if (!await validate() || !owns(model) || model.getVersionId() !== version) return
        const formatted = await request<string>('format_source', model)
        if (!formatted?.isCurrent()) return
        editor.pushUndoStop()
        editor.executeEdits('format', [{ range: model.getFullModelRange(), text: formatted.value }])
        editor.pushUndoStop()
        const formattedVersion = model.getVersionId()
        if (!await validate() || !owns(model) || model.getVersionId() !== formattedVersion) return
        await onSave(model)
      })().finally(() => { savingPromise = undefined; setSaving(false) })
      return savingPromise
    }

    const completionProvider = monaco.languages.registerCompletionItemProvider('csharp', {
      triggerCharacters: ['.'],
      provideCompletionItems: async (model, position, _context, token) => {
        if (!owns(model)) return null
        try {
          const items = await request<CompletionItem[]>('get_completions', model,
            { position: model.getOffsetAt(position) }, token)
          if (!items?.isCurrent()) return { suggestions: [] }
          return { incomplete: true, suggestions: items.value.map(item => {
            const toRange = (edit: CompletionTextEdit) => {
              const start = model.getPositionAt(edit.start)
              const end = model.getPositionAt(edit.start + edit.length)
              return { startLineNumber: start.lineNumber, startColumn: start.column,
                endLineNumber: end.lineNumber, endColumn: end.column }
            }
            return {
              label: itemToLabel(item),
              filterText: item.filterText,
              kind: tagsToKind(item.tags),
              insertText: item.textEdit.newText,
              range: toRange(item.textEdit),
              additionalTextEdits: item.additionalTextEdits.map(edit => ({ range: toRange(edit), text: edit.newText })),
            }
          }) }
        } catch (error) {
          console.error('Failed to get completions', error)
          return { suggestions: [] }
        }
      },
    })
    const hoverProvider = monaco.languages.registerHoverProvider('csharp', {
      provideHover: async (model, position, token) => {
        if (!owns(model)) return null
        try {
          const text = await request<string | null>('get_hover', model,
            { position: model.getOffsetAt(position) }, token)
          return text?.isCurrent() && text.value ? { contents: [{ value: text.value }] } : null
        } catch (error) {
          console.error('Failed to get hover', error)
          return null
        }
      },
    })
    const contentSubscription = editor.onDidChangeModelContent(() => {
      const retryCompletion = pending.has('get_completions') || completionTimer !== undefined
      invalidate()
      clearDiagnostics()
      cancelCompletionRetry()
      if (retryCompletion) {
        const model = editor.getModel()
        const version = model?.getVersionId()
        completionPosition = editor.getPosition()
        completionTimer = setTimeout(() => {
          completionTimer = undefined
          const position = editor.getPosition()
          if (model && owns(model) && model.getVersionId() === version && editor.hasTextFocus()
            && position && completionPosition && position.lineNumber === completionPosition.lineNumber
            && position.column === completionPosition.column) editor.trigger('typing-recovery', 'editor.action.triggerSuggest', {})
          completionPosition = null
        }, 100)
      }
      setValidating(true)
      clearTimeout(validationTimer)
      validationTimer = setTimeout(() => { void validate() }, 500)
    })
    const keySubscription = editor.onKeyDown(event => {
      if (event.keyCode === monaco.KeyCode.Escape) cancelCompletionRetry()
    })
    const cursorSubscription = editor.onDidChangeCursorPosition(event => {
      if (completionTimer === undefined) return
      // Ordinary typing updates the cursor after the content event; explicit navigation
      // (including keyboard arrows), mouse movement and programmatic selection end recovery.
      if (event.source === 'keyboard' && event.reason === monaco.editor.CursorChangeReason.NotSet) completionPosition = event.position
      else cancelCompletionRetry()
    })
    const modelSubscription = editor.onDidChangeModel(() => {
      invalidate()
      if (markedModel && !markedModel.isDisposed()) monaco.editor.setModelMarkers(markedModel, 'csharp', [])
      markedModel = editor.getModel()
      cancelCompletionRetry()
      clearDiagnostics()
      void validate()
    })
    const saveAction = editor.addAction({
      id: 'hass-sharp.save', label: 'Save automation', keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS],
      run: async () => {
        try { await runSave() } catch (error) { console.error('Failed to save automation', error) }
      },
    })
    void validate()
    disposeRegistration = () => {
      if (disposed) return
      disposed = true
      cancelCompletionRetry()
      clearTimeout(validationTimer)
      invalidate()
      completionProvider.dispose()
      hoverProvider.dispose()
      keySubscription.dispose()
      cursorSubscription.dispose()
      contentSubscription.dispose()
      modelSubscription.dispose()
      saveAction.dispose()
      if (markedModel && !markedModel.isDisposed()) monaco.editor.setModelMarkers(markedModel, 'csharp', [])
    }
  }

  onCleanup(() => disposeRegistration?.())
  return {
    register,
    diagnostics,
    validationError,
    validating,
    goToDiagnostic: (diagnostic: Diagnostic) => navigate(diagnostic),
    save: () => saveCommand(),
    dispose: () => disposeRegistration?.(),
    saveDisabled: () => validating() || hasErrors() || saving(),
  }
}
