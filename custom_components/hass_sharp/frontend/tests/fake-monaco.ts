// Monaco's DOM/worker host is the boundary double. Editor actions and Solid lifecycle run unchanged.
export const completions: any[] = []
export const hovers: any[] = []
export const editors: FakeEditor[] = []
export const markers = new Map<string, any[]>()
let nextId = 0
const disposable = (remove: () => void) => ({ dispose: remove })
export class FakeModel {
  uri = { toString: () => `model:${this.id}` }
  id = ++nextId
  version = 1
  disposed = false
  constructor(public value: string) {}
  getValue() { return this.value }
  getVersionId() { return this.version }
  isDisposed() { return this.disposed }
  dispose() { this.disposed = true }
  getPositionAt(offset: number) {
    const lines = this.value.slice(0, offset).split('\n')
    return { lineNumber: lines.length, column: lines.at(-1)!.length + 1 }
  }
  getOffsetAt(p: any) { return this.value.split('\n').slice(0, p.lineNumber - 1).join('\n').length + (p.lineNumber > 1 ? 1 : 0) + p.column - 1 }
  getFullModelRange() { return { startLineNumber: 1, startColumn: 1, endLineNumber: this.getPositionAt(this.value.length).lineNumber, endColumn: this.getPositionAt(this.value.length).column } }
  getWordUntilPosition(p: any) { return { startColumn: 1, endColumn: p.column } }
  getLineContent(line: number) { return this.value.split('\n')[line - 1] }
  getLineFirstNonWhitespaceColumn() { return 1 }
}
export class FakeEditor {
  model: FakeModel
  disposed = false
  listeners = new Set<() => void>()
  modelListeners = new Set<() => void>()
  keyListeners = new Set<(e: any) => void>()
  cursorListeners = new Set<(e: any) => void>()
  position = { lineNumber: 1, column: 1 }
  actions = new Map<string, any>()
  options: any
  constructor(options: any) { this.options = options; this.model = new FakeModel(options.value ?? '') }
  getModel() { return this.model }
  hasTextFocus() { return true }
  getPosition() { return this.position }
  onKeyDown(fn: (e: any) => void) { this.keyListeners.add(fn); return disposable(() => this.keyListeners.delete(fn)) }
  onDidChangeCursorPosition(fn: (e: any) => void) { this.cursorListeners.add(fn); return disposable(() => this.cursorListeners.delete(fn)) }
  move(position: any, source = "keyboard", reason = 3) { this.position = position; this.cursorListeners.forEach(fn => fn({ position, source, reason })) }
  key(keyCode: number) { this.keyListeners.forEach(fn => fn({ keyCode })) }
  selection: any
  triggers: string[] = []
  setSelection(range: any) { this.selection = range }
  revealRangeInCenter() {}
  trigger(_source: string, action: string) { this.triggers.push(action) }
  setPosition() {}
  focus() {}
  updateOptions(options: any) { Object.assign(this.options, options) }
  pushUndoStop() {}
  executeEdits(_source: string, edits: any[]) {
    for (const edit of edits) {
      const start = this.model.getOffsetAt({ lineNumber: edit.range.startLineNumber, column: edit.range.startColumn })
      const end = this.model.getOffsetAt({ lineNumber: edit.range.endLineNumber, column: edit.range.endColumn })
      this.model.value = this.model.value.slice(0, start) + edit.text + this.model.value.slice(end)
    }
    this.model.version++
    this.listeners.forEach(fn => fn())
  }
  change(value: string) { this.model.value = value; this.model.version++; this.listeners.forEach(fn => fn()) }
  setModel(model: FakeModel) { this.model = model; this.modelListeners.forEach(fn => fn()) }
  onDidChangeModelContent(fn: () => void) { this.listeners.add(fn); return disposable(() => this.listeners.delete(fn)) }
  onDidChangeModel(fn: () => void) { this.modelListeners.add(fn); return disposable(() => this.modelListeners.delete(fn)) }
  addAction(action: any) { this.actions.set(action.id, action); return disposable(() => this.actions.delete(action.id)) }
  dispose() { this.disposed = true }
}
function provider(list: any[], value: any) { list.push(value); return disposable(() => list.splice(list.indexOf(value), 1)) }
export const host = {
  KeyMod: { CtrlCmd: 2048 }, KeyCode: { KeyS: 49, Escape: 9 },
  MarkerSeverity: { Error: 8, Warning: 4, Info: 2 },
  languages: {
    CompletionItemKind: { Method: 0, Function: 1, Field: 3, Variable: 4, Class: 5, Struct: 6, Interface: 7, Module: 8, Property: 9, Enum: 15, Keyword: 17, TypeParameter: 24, Snippet: 28 },
    registerCompletionItemProvider: (_language: string, value: any) => provider(completions, value),
    registerHoverProvider: (_language: string, value: any) => provider(hovers, value),
  },
  editor: {
    CursorChangeReason: { NotSet: 0, Explicit: 3 },
    defineTheme: () => {},
    create: (_element: any, options: any) => { const editor = new FakeEditor(options); editors.push(editor); return editor },
    setModelMarkers: (model: FakeModel, _owner: string, value: any[]) => markers.set(model.uri.toString(), value),
    getModelMarkers: ({ resource }: any) => resource ? markers.get(resource.toString()) ?? [] : [...markers.values()].flat(),
  },
}
export function resetHost() { completions.length = hovers.length = editors.length = 0; markers.clear() }
