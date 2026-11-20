import * as monaco from 'monaco-editor'
import type { CompletionItem, CompletionList, CompletionResolution, CompletionTextEdit, Diagnostic, HomeAssistant } from '../../types'
import { createSignal, onCleanup } from 'solid-js'
import { customDocumentId, libraryApi, type CodeKind, type CustomPreview, type LibraryContext, type LibraryDiagnostic, type LibraryReply, type LibraryPreview, type LibraryDefinition, type LibraryRange, type LibrarySignatureHelp } from '../../visual/library-api'

function itemToLabel(item: CompletionItem) {
  if (!item.displayTextPrefix && !item.displayTextSuffix) return item.displayText

  const res = `${item.displayTextPrefix}${item.displayText}${item.displayTextSuffix}`

  return res
}

/** Roslyn's kind tags (`WellKnownTags`). The rest of an item's tags say its accessibility and the like. */
const tagToKindMap: { [key: string]: monaco.languages.CompletionItemKind } = {
  Method: monaco.languages.CompletionItemKind.Method,
  ExtensionMethod: monaco.languages.CompletionItemKind.Function,
  Delegate: monaco.languages.CompletionItemKind.Function,
  Class: monaco.languages.CompletionItemKind.Class,
  Record: monaco.languages.CompletionItemKind.Class,
  Structure: monaco.languages.CompletionItemKind.Struct,
  RecordStruct: monaco.languages.CompletionItemKind.Struct,
  Interface: monaco.languages.CompletionItemKind.Interface,
  Enum: monaco.languages.CompletionItemKind.Enum,
  EnumMember: monaco.languages.CompletionItemKind.EnumMember,
  Property: monaco.languages.CompletionItemKind.Property,
  Field: monaco.languages.CompletionItemKind.Field,
  Constant: monaco.languages.CompletionItemKind.Constant,
  Event: monaco.languages.CompletionItemKind.Event,
  Operator: monaco.languages.CompletionItemKind.Operator,
  Local: monaco.languages.CompletionItemKind.Variable,
  Parameter: monaco.languages.CompletionItemKind.Variable,
  RangeVariable: monaco.languages.CompletionItemKind.Variable,
  Label: monaco.languages.CompletionItemKind.Reference,
  TypeParameter: monaco.languages.CompletionItemKind.TypeParameter,
  Keyword: monaco.languages.CompletionItemKind.Keyword,
  Intrinsic: monaco.languages.CompletionItemKind.Keyword,
  Namespace: monaco.languages.CompletionItemKind.Module,
  Module: monaco.languages.CompletionItemKind.Module,
  Assembly: monaco.languages.CompletionItemKind.Module,
  Snippet: monaco.languages.CompletionItemKind.Snippet,
}

/**
 * The item's kind, from the first of its tags that names one. An item of a kind not listed shows as text: the editor
 * hides snippets (`showSnippets: false`), so taking an unknown kind for a snippet hid it (an enum's members were lost so).
 */
function tagsToKind(tags: string[]) {
  for (const tag of tags) {
    const found = tagToKindMap[tag]
    if (found !== undefined) return found
  }
  return monaco.languages.CompletionItemKind.Text
}

/** What a completion list depends on besides the code before its position: the file, the saved libraries and the other drafts. */
const completionKey = (context: LibraryContext) => JSON.stringify({ id: context.id, generation: context.generation, sources: context.sources })

/** The text of a documentation element, with an empty reference (`<see cref/>`, `<paramref/>`, `<see langword/>`) read as the name it points at. */
function documentationText(node: Node): string {
  if (node.nodeType !== Node.ELEMENT_NODE) return node.textContent ?? ''
  const element = node as Element
  const reference = element.getAttribute('cref') ?? element.getAttribute('langword') ?? (/^(type)?paramref$/.test(element.tagName) ? element.getAttribute('name') : null)
  // Roslyn writes an unresolved reference as `!:Name`, and a method's generic arity with two backticks (``1).
  if (reference && !element.textContent) return reference.replace(/^[\w!]:/, '').replace(/^global::/, '').replace(/\(.*$/, '').replace(/`+\d+$/, '').split('.').pop() ?? reference
  return Array.from(element.childNodes).map(documentationText).join('')
}

function signatureDocumentation(xml: string) {
  if (!xml.trim().startsWith('<')) return xml
  const document = new DOMParser().parseFromString(`<documentation>${xml}</documentation>`, 'text/xml')
  if (document.querySelector('parsererror')) return xml
  const sections = Array.from(document.querySelectorAll('summary,param,returns,remarks'))
  return (sections.length ? sections.map(section => `${section.tagName === 'param' ? `${section.getAttribute('name')}: ` : section.tagName === 'returns' ? 'Returns: ' : ''}${documentationText(section)}`).join('\n\n') : documentationText(document.documentElement)).trim()
}

/** Why a check failed, in the words of the server or the connection. */
const customReason = (error: unknown) => {
  const e = error as { message?: unknown; code?: unknown } | null
  return typeof e?.message === 'string' && e.message ? e.message : typeof e?.code === 'string' ? e.code : String(error)
}
export const libraryModelUri = (id: string) => monaco.Uri.parse(`hass-sharp-library:///${encodeURIComponent(id)}.cs`)
/** The model of a Custom C# node's code, by the node's id. */
export const customModelUri = (nodeId: string) => monaco.Uri.parse(`hass-sharp-custom:///${encodeURIComponent(nodeId)}.cs`)

export interface LibraryEditorContext {
  /** In the custom mode, `id` is the node's id and `workspaceVersion` names this editing session; the rest is unused. */
  context: () => LibraryContext | undefined
  sourceGeneration?: () => string | undefined
  documents?: () => { id: string; source: string }[]
  onDefinition?: (definition: LibraryDefinition) => void
  /**
   * The third mode, beside a library file: a Custom C# node's code. Diagnostics come from `custom_preview` (about
   * 400 ms after the last keystroke) and the language services from the `custom: true` requests, both for this node's
   * unsaved code beside the saved libraries. Read-only code is never checked. `kind` is the node's kind, which the
   * check reads the code as (a Custom C# step's method unless told: `custom`, or a C# entity's or C# device's class);
   * the language services read every kind's code alike.
   */
  custom?: { readOnly?: boolean; kind?: CodeKind }
}
/** The latest check of a node's code: the text checked, whether it compiles, and the signature it gives (of its kind). */
export interface CustomCheck { source: string; valid: boolean; signature: unknown }

// Language services of the node library editor. Each registration owns its requests and providers;
// responses are tied to the exact model revision and library workspace.
export const useEditorActions = (hass: HomeAssistant, library: LibraryEditorContext) => {
  const api = libraryApi(hass)
  let invalidateContext = () => {}
  const [validating, setValidating] = createSignal(true)
  const [hasErrors, setHasErrors] = createSignal(false)
  const [diagnostics, setDiagnostics] = createSignal<Diagnostic[]>([])
  const [validationError, setValidationError] = createSignal<string | null>(null)
  const [customCheck, setCustomCheck] = createSignal<CustomCheck>()
  const custom = library.custom
  const validationDelay = custom ? 400 : 500
  let navigate: (diagnostic: Diagnostic) => void = () => {}
  let revalidate: () => void = () => {}
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
    let savingPromise: Promise<void> | undefined
    let markedModel = editor.getModel()
    const pending = new Map<string, () => void>()
    /** Whether the completion request in flight is still good after an edit (see `invalidate`). */
    let completionHolds: (() => boolean) | undefined
    const latest = new Map<string, number>()
    let nextRequest = 0
    let epoch = 0
    let semanticIdentity: { context: string; sourceGeneration: string } | undefined
    type Reply<T> = { value: T; isCurrent: () => boolean }

    /**
     * Ends the requests in flight. An edit keeps a completion request: its list stays good while the person goes on
     * typing the word (it checks that itself when it arrives), and Monaco filters it by what they typed meanwhile.
     */
    const invalidate = (keepCompletion = false) => {
      epoch++
      for (const [kind, cancel] of [...pending]) {
        if (keepCompletion && kind === 'completion' && completionHolds?.()) continue
        cancel(); pending.delete(kind)
      }
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
      // A completion list is for the code before its position, in the same file and workspace: typing on after that
      // position (which advances the model and, for a library, its workspace version) leaves it good.
      const typing = kind === 'completion'
      const at = (fields as { position?: number }).position ?? 0
      const before = typing ? model.getValue().slice(0, at) : ''
      const keyOf = (context: LibraryContext) => typing ? completionKey(context) : JSON.stringify(context)
      const contextKey = keyOf(capturedContext)
      const version = model.getVersionId()
      const requestEpoch = epoch
      let replySourceGeneration: string | undefined
      const requestId = ++nextRequest
      latest.set(kind, requestId)
      const sameContext = () => { const context = library.context(); return !!context && keyOf(context) === contextKey }
      if (typing) completionHolds = () => owns(model) && sameContext() && model.getValue().slice(0, at) === before
      const isCurrent = () => owns(model) && latest.get(kind) === requestId && !token?.isCancellationRequested && sameContext()
        && (typing ? model.getValue().slice(0, at) === before
          : model.getVersionId() === version && epoch === requestEpoch
            && (!replySourceGeneration || !library.sourceGeneration?.() || library.sourceGeneration() === replySourceGeneration))
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
        // A Custom C# node's code: the reply names the node and the editing session; it carries no source generation.
        const customRequest = async () => {
          const code = { nodeId: capturedContext.id, source: model.getValue(), workspaceVersion: capturedContext.workspaceVersion }
          const position = (fields as { position?: number }).position ?? 0
          if (kind === 'diagnostics') {
            const preview = await api.customPreview(code, custom?.kind ?? 'custom')
            return isCurrent() ? { value: preview as T, isCurrent } : null
          }
          let reply
          switch (kind) {
            case 'completion': reply = await api.customCompletion(code, position); break
            case 'hover': reply = await api.customHover(code, position); break
            case 'signature': reply = await api.customSignature(code, position); break
            case 'definition': reply = await api.customDefinition(code, position); break
            default: reply = await api.customFormat(code)
          }
          if (reply.saved !== false || reply.id !== code.nodeId || reply.workspaceVersion !== code.workspaceVersion || !isCurrent()) return null
          return { value: reply.data as T, isCurrent }
        }
        const libraryRequest = async () => {
          if (custom) return customRequest()
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
          if (typing) return isCurrent() ? { value: reply.data as T, isCurrent } : null
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
      if (custom?.readOnly) { setValidating(false); return false }
      setValidating(true)
      const validationEpoch = epoch
      const validationContext = JSON.stringify(library.context())
      try {
        const checked = model.getValue()
        const reply = await request<LibraryDiagnostic[] | CustomPreview>('diagnostics', model)
        if (!reply?.isCurrent()) return false
        const preview = custom ? reply.value as CustomPreview : undefined
        const diagnostics = { value: preview ? preview.diagnostics : reply.value as LibraryDiagnostic[] }
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
        // Custom code that does not compile with the libraries, or gives no node, is invalid without an error of its own.
        const errors = diagnostics.value.some(d => d.severity === 3) || (preview !== undefined && !preview.valid)
        if (preview) setCustomCheck({ source: checked, valid: preview.valid, signature: preview.signature })
        setHasErrors(errors)
        setValidating(false)
        return !errors
      } catch (error) {
        if (owns(model) && validationEpoch === epoch && validationContext === JSON.stringify(library.context())) { setDiagnostics([]); monaco.editor.setModelMarkers(model, 'csharp', []); setValidationError(custom ? customReason(error) : String(error)); setHasErrors(true); setValidating(false) }
        console.error('Failed to get diagnostics', error)
        return false
      }
    }

    revalidate = () => { void validate() }
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

    // The list an item came from: the code and position it was made for, to resolve the item against the same code.
    type ListedItem = { model: monaco.editor.ITextModel; item: CompletionItem; source: string; position: number; key: string }
    const listed = new WeakMap<monaco.languages.CompletionItem, ListedItem>()
    const resolveItem = async (listedItem: ListedItem, token: monaco.CancellationToken): Promise<CompletionResolution | null> => {
      const { model, item, source, position, key } = listedItem
      const context = library.context()
      if (!owns(model) || token.isCancellationRequested || !context || completionKey(context) !== key) return null
      const reply = custom
        ? await api.customCompletionResolve({ nodeId: context.id, source, workspaceVersion: context.workspaceVersion }, position, item)
        : await api.completionResolve({ ...context, source }, position, item)
      const now = library.context()
      if (!owns(model) || token.isCancellationRequested || !now || completionKey(now) !== key || reply.saved !== false || reply.id !== context.id) return null
      return reply.data
    }
    const completionProvider = monaco.languages.registerCompletionItemProvider('csharp', {
      triggerCharacters: ['.'],
      provideCompletionItems: async (model, position, _context, token) => {
        if (!owns(model)) return null
        const offset = model.getOffsetAt(position)
        const source = model.getValue()
        const context = library.context()
        try {
          const list = await request<CompletionList>('completion', model, { position: offset }, token)
          if (!list?.isCurrent() || !context) return { suggestions: [] }
          const toRange = (edit: CompletionTextEdit) => {
            const start = model.getPositionAt(edit.start)
            const end = model.getPositionAt(edit.start + edit.length)
            return { startLineNumber: start.lineNumber, startColumn: start.column,
              endLineNumber: end.lineNumber, endColumn: end.column }
          }
          // The whole list for the word: Monaco filters and sorts it as the word grows, without asking again.
          return { incomplete: list.value.incomplete, suggestions: list.value.items.map(item => {
            const suggestion: monaco.languages.CompletionItem = {
              label: item.inlineDescription ? { label: itemToLabel(item), description: item.inlineDescription } : itemToLabel(item),
              filterText: item.filterText,
              ...(item.sortText ? { sortText: item.sortText } : {}),
              ...(item.preselect ? { preselect: true } : {}),
              kind: tagsToKind(item.tags),
              insertText: item.textEdit.newText,
              range: toRange(item.textEdit),
              additionalTextEdits: item.additionalTextEdits.map(edit => ({ range: toRange(edit), text: edit.newText })),
            }
            listed.set(suggestion, { model, item, source, position: offset, key: completionKey(context) })
            return suggestion
          }) }
        } catch (error) {
          console.error('Failed to get completions', error)
          return { suggestions: [] }
        }
      },
      // Monaco resolves the item it shows and the one inserted: its description, and the using line of a type of a
      // namespace the code does not import (applied as it is inserted, or just after).
      resolveCompletionItem: async (suggestion, token) => {
        const listedItem = listed.get(suggestion)
        if (!listedItem) return suggestion
        try {
          const resolved = await resolveItem(listedItem, token)
          if (!resolved) return suggestion
          const [detail, ...rest] = resolved.documentation.split('\n')
          if (detail) suggestion.detail = detail
          const documentation = rest.join('\n').trim()
          if (documentation) suggestion.documentation = documentation
          // The edits are offsets in the list's code: they hold while the code before the list's position is as it was.
          const { model, source, position } = listedItem
          if (listedItem.item.resolve && model.getValue().slice(0, position) === source.slice(0, position))
            suggestion.additionalTextEdits = resolved.additionalTextEdits.map(edit => {
              const start = model.getPositionAt(edit.start), end = model.getPositionAt(edit.start + edit.length)
              return { range: { startLineNumber: start.lineNumber, startColumn: start.column, endLineNumber: end.lineNumber, endColumn: end.column }, text: edit.newText }
            })
        } catch (error) {
          console.error('Failed to resolve a completion', error)
        }
        return suggestion
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
          // Custom code has its own model; definitions in library files it cannot show are left out.
          const targets = custom ? reply.value.filter(target => target.id === customDocumentId) : reply.value
          return targets.map(target => ({ uri: custom ? model.uri : libraryModelUri(target.id), range: {
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
      // A library edit advances its workspace: a completion list in flight checks its own file and workspace when it arrives.
      invalidate(true); clearDiagnostics(); setValidating(true)
      clearTimeout(validationTimer); validationTimer = setTimeout(() => void validate(), validationDelay)
    }
    const contentSubscription = editor.onDidChangeModelContent(() => {
      invalidate(true)
      clearDiagnostics()
      setValidating(true)
      clearTimeout(validationTimer)
      validationTimer = setTimeout(() => { void validate() }, validationDelay)
    })
    const modelSubscription = editor.onDidChangeModel(() => {
      invalidate()
      if (markedModel && !markedModel.isDisposed()) monaco.editor.setModelMarkers(markedModel, 'csharp', [])
      markedModel = editor.getModel()
      clearDiagnostics()
      void validate()
    })
    const saveAction = editor.addAction({
      id: 'hass-sharp.save', label: custom ? 'Apply code' : 'Save node library', keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS],
      run: async () => {
        try { await runSave() } catch (error) { console.error('Failed to save node library', error) }
      },
    })
    void validate()
    disposeRegistration = () => {
      if (disposed) return
      disposed = true
      clearTimeout(validationTimer)
      invalidate()
      completionProvider.dispose()
      hoverProvider.dispose()
      signatureProvider.dispose(); definitionProvider.dispose(); formatProvider.dispose()
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
    /** The custom mode's latest check (kept while the next one runs, so the pins it gave stay on view). */
    customCheck,
    /** Checks the code again at once (after a failed check). */
    revalidate: () => revalidate(),
    goToDiagnostic: (diagnostic: Diagnostic) => navigate(diagnostic),
    save: () => saveCommand(),
    dispose: () => disposeRegistration?.(),
    saveDisabled: () => validating() || hasErrors() || saving(),
  }
}
