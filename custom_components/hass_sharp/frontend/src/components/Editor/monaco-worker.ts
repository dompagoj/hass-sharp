import EditorWorker from 'monaco-editor/editor/editor.worker?worker'

// The default Monaco 0.57 blob bootstrap imports relative modules. Bundle its
// worker explicitly so it also resolves under HA's nested static route.
globalThis.MonacoEnvironment = {
  ...globalThis.MonacoEnvironment,
  getWorker: () => new EditorWorker(),
}
