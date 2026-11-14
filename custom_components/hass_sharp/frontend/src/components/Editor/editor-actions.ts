import * as monaco from 'monaco-editor'
import type { CompletionItem, CompletionTextEdit, Diagnostic, HomeAssistant } from '../../types'
import { createSignal, onCleanup } from 'solid-js'
import { libraryApi, type LibraryContext, type LibraryDiagnostic, type LibraryReply, type LibraryPreview, type LibraryDefinition, type LibraryRange, type LibrarySignatureHelp } from '../../visual/library-api'

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

function signatureDocumentation(xml: string) {
  if (!xml.trim().startsWith('<')) return xml
  const document = new DOMParser().parseFromString(`<documentation>${xml}</documentation>`, 'text/xml')
  if (document.querySelector('parsererror')) return xml
  const sections = Array.from(document.querySelectorAll('summary,param,returns,remarks'))
  return (sections.length ? sections.map(section => `${section.tagName === 'param' ? `${section.getAttribute('name')}: ` : section.tagName === 'returns' ? 'Returns: ' : ''}${section.textContent ?? ''}`).join('\n\n') : document.documentElement.textContent ?? '').trim()
}

export const libraryModelUri = (id: string) => monaco.Uri.parse(`hass-sharp-library:///${encodeURIComponent(id)}.cs`)

export interface LibraryEditorContext {
  context: () => LibraryContext | undefined
  sourceGeneration?: () => string | undefined
  documents?: () => { id: string; source: string }[]
  onDefinition?: (definition: LibraryDefinition) => void
}

// Language services of the node library editor. Each registration owns its requests and providers;
// responses are tied to the exact model revision and library workspace.
export const useEditorActions = (hass: HomeAssistant, library: LibraryEditorContext) => {
  const api = libraryApi(hass)
  let invalidateContext = () => {}
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
    let observedModel = markedModel
    let observedVersion = observedModel?.getVersionId()
    const pending = new Map<string, () => void>()
    const latest = new Map<string, number>()
    let nextRequest = 0
    let epoch = 0
    let semanticIdentity: { context: string; sourceGeneration: string } | undefined
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
      const capturedContext = library.context()
      if (!capturedContext) return Promise.resolve<Reply<T> | null>(null)
      const contextKey = JSON.stringify(capturedContext)
      const version = model.getVersionId()
      const requestEpoch = epoch
      let replySourceGeneration: string | undefined
      const requestId = ++nextRequest
      latest.set(kind, requestId)
      const isCurrent = () => owns(model) && model.getVersionId() === version && epoch === requestEpoch
        && latest.get(kind) === requestId && !token?.isCancellationRequested && JSON.stringify(library.context()) === contextKey
        && (!replySourceGeneration || !library.sourceGeneration?.() || library.sourceGeneration() === replySourceGeneration)
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
        const libraryRequest = async () => {
          const c = { ...capturedContext, source: model.getValue() }
          const position = (fields as { position?: number }).position ?? 0
          let reply: LibraryReply<unknown>
          switch (kind) {
            case 'completion': reply = await api.completion(c, position); break
            case 'hover': reply = await api.hover(c, position); break
            case 'signature': reply = await api.signature(c, position); break
            case 'definition': reply = await api.definition(c, position); break
            case 'format': reply = await api.format(c); break
            default: reply = await api.preview(c)
          }
          if (reply.saved !== false || reply.id !== c.id || reply.workspaceVersion !== c.workspaceVersion || reply.generation !== c.generation || typeof reply.sourceGeneration !== 'string') return null
          const expectedSource = library.sourceGeneration?.() ?? (semanticIdentity?.context === contextKey ? semanticIdentity.sourceGeneration : undefined)
          if (expectedSource && expectedSource !== reply.sourceGeneration) return null
          if (!isCurrent()) return null
          replySourceGeneration = reply.sourceGeneration
          semanticIdentity = { context: contextKey, sourceGeneration: reply.sourceGeneration }
          let value = reply.data
          if (kind === 'diagnostics') value = (value as LibraryPreview).diagnostics
          return { value: value as T, isCurrent }
        }
        libraryRequest().then(
          reply => finish(isCurrent() ? reply : null),
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
      const validationEpoch = epoch
      const validationContext = JSON.stringify(library.context())
      try {
        const diagnostics = await request<LibraryDiagnostic[]>('diagnostics', model)
        if (!diagnostics?.isCurrent()) return false
        const selectedDiagnostics = diagnostics.value.filter(d => d.sourceId === library.context()?.id)
        const markers: monaco.editor.IMarkerData[] = selectedDiagnostics.map(d => ({
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
        setDiagnostics(selectedDiagnostics)
        setValidationError(null)
        const errors = diagnostics.value.some(d => d.severity === 3)
        setHasErrors(errors)
        setValidating(false)
        return !errors
      } catch (error) {
        if (owns(model) && validationEpoch === epoch && validationContext === JSON.stringify(library.context())) { setDiagnostics([]); monaco.editor.setModelMarkers(model, 'csharp', []); setValidationError(String(error)); setHasErrors(true); setValidating(false) }
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
        const workspace = JSON.stringify(library.context())
        if (!await validate() || JSON.stringify(library.context()) !== workspace || !owns(model) || model.getVersionId() !== version) return
        const formatted = await request<string>('format', model)
        if (!formatted?.isCurrent()) return
        editor.pushUndoStop()
        editor.executeEdits('format', [{ range: model.getFullModelRange(), text: formatted.value }])
        editor.pushUndoStop()
        const formattedVersion = model.getVersionId()
        const formattedWorkspace = JSON.stringify(library.context())
        if (!await validate() || JSON.stringify(library.context()) !== formattedWorkspace || !owns(model) || model.getVersionId() !== formattedVersion) return
        await onSave(model)
      })().finally(() => { savingPromise = undefined; setSaving(false) })
      return savingPromise
    }

    const completionProvider = monaco.languages.registerCompletionItemProvider('csharp', {
      triggerCharacters: ['.'],
      provideCompletionItems: async (model, position, _context, token) => {
        if (!owns(model)) return null
        try {
          const items = await request<CompletionItem[]>('completion', model,
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
          const text = await request<{ text?: string; range?: LibraryRange } | null>('hover', model,
            { position: model.getOffsetAt(position) }, token)
          if (!text?.isCurrent() || !text.value) return null
          const value = text.value
          if (!value.text) return null
          return { contents: [{ value: value.text }], ...(value.range ? { range: {
            startLineNumber: value.range.startLine, startColumn: value.range.startColumn,
            endLineNumber: value.range.endLine, endColumn: value.range.endColumn,
          } } : {}) }
        } catch (error) {
          console.error('Failed to get hover', error)
          return null
        }
      },
    })
    const signatureProvider = monaco.languages.registerSignatureHelpProvider('csharp', {
      signatureHelpTriggerCharacters: ['(', ','], signatureHelpRetriggerCharacters: [')'],
      provideSignatureHelp: async (model, position, token) => {
        if (!owns(model)) return null
        try {
          const reply = await request<LibrarySignatureHelp>('signature', model, { position: model.getOffsetAt(position) }, token)
          if (!reply?.isCurrent() || !reply.value.signatures.length) return null
          return { value: { ...reply.value, signatures: reply.value.signatures.map(s => ({ ...s, documentation: { value: signatureDocumentation(s.documentation) }, parameters: s.parameters.map(label => ({ label })) })) }, dispose() {} }
        } catch { return null }
      },
    })
    const definitionProvider = monaco.languages.registerDefinitionProvider('csharp', {
      provideDefinition: async (model, position, token) => {
        if (!owns(model)) return null
        try {
          const reply = await request<LibraryDefinition[]>('definition', model, { position: model.getOffsetAt(position) }, token)
          if (!reply?.isCurrent()) return null
          return reply.value.map(target => ({ uri: libraryModelUri(target.id), range: {
            startLineNumber: target.range.startLine, startColumn: target.range.startColumn,
            endLineNumber: target.range.endLine, endColumn: target.range.endColumn,
          } }))
        } catch { return null }
      },
    })
    const formatProvider = monaco.languages.registerDocumentFormattingEditProvider('csharp', {
      provideDocumentFormattingEdits: async (model, _options, token) => {
        if (!owns(model)) return []
        try {
          const reply = await request<string>('format', model, {}, token)
          return reply?.isCurrent() ? [{ range: model.getFullModelRange(), text: reply.value }] : []
        } catch { return [] }
      },
    })
    invalidateContext = () => {
      // A library onChange may advance context before Monaco invokes this registration's
      // content listener. Let that listener retain its pending-completion typing recovery.
      if (observedModel === editor.getModel() && observedModel?.getVersionId() !== observedVersion) return
      invalidate(); cancelCompletionRetry(); clearDiagnostics(); setValidating(true)
      clearTimeout(validationTimer); validationTimer = setTimeout(() => void validate(), 500)
    }
    const contentSubscription = editor.onDidChangeModelContent(() => {
      observedModel = editor.getModel(); observedVersion = observedModel?.getVersionId()
      const retryCompletion = pending.has('completion') || completionTimer !== undefined
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
      observedModel = markedModel; observedVersion = observedModel?.getVersionId()
      cancelCompletionRetry()
      clearDiagnostics()
      void validate()
    })
    const saveAction = editor.addAction({
      id: 'hass-sharp.save', label: 'Save node library', keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS],
      run: async () => {
        try { await runSave() } catch (error) { console.error('Failed to save node library', error) }
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
      signatureProvider.dispose(); definitionProvider.dispose(); formatProvider.dispose()
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
    invalidateContext: () => invalidateContext(),
    diagnostics,
    validationError,
    validating,
    goToDiagnostic: (diagnostic: Diagnostic) => navigate(diagnostic),
    save: () => saveCommand(),
    dispose: () => disposeRegistration?.(),
    saveDisabled: () => validating() || hasErrors() || saving(),
  }
}
