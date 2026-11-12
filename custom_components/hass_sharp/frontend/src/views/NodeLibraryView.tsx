import { createEffect, createSignal, For, onCleanup, Show } from 'solid-js'
import { useBeforeLeave } from '@solidjs/router'
import { useHass } from '../context'
import { Button, Icon } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { Editor } from '../components/Editor/Editor'
import { librariesChanged, libraryApi, type LibraryDefinition, type LibraryRange } from '../visual/library-api'
import { createLibrarySession } from '../visual/library-session'
import { LibraryPreview } from '../visual/LibraryPreview'

export const NodeLibraryView = () => {
  const [dirty, setDirty] = createSignal(false)
  const [leave, setLeave] = createSignal<() => void>()
  useBeforeLeave(event => { if (dirty()) { event.preventDefault(); setLeave(() => () => event.retry(true)) } })
  return <><NodeLibraryEditor onDirtyChange={setDirty} />
    <Dialog open={!!leave()} title="Unsaved library files" onClose={() => setLeave(undefined)} footer={<>
      <Button onClick={() => setLeave(undefined)}>Keep editing</Button><Button variant="danger" onClick={() => { const retry = leave(); setLeave(undefined); retry?.() }}>Discard changes</Button>
    </>}><p>Leaving discards unsaved changes in all library files. Save them or download your drafts first.</p></Dialog>
  </>
}
export const NodeLibraryEditor = (props: { onDirtyChange?: (dirty: boolean) => void }) => {
  const hass = useHass()
  return <Show when={hass.user?.is_admin} fallback={<p class="hs-notice" role="alert">Node libraries require a Home Assistant administrator.</p>}><LibraryWorkspace {...props} /></Show>
}
const LibraryWorkspace = (props: { onDirtyChange?: (dirty: boolean) => void }) => {
  const hass = useHass()
  const session = createLibrarySession(libraryApi(hass))
  const [fullScreen, setFullScreen] = createSignal(false)
  const [newFile, setNewFile] = createSignal(false)
  const [newName, setNewName] = createSignal('Nodes.cs')
  const [confirm, setConfirm] = createSignal<'delete' | 'reload'>()
  const [refreshing, setRefreshing] = createSignal(false)
  const [refreshMessage, setRefreshMessage] = createSignal('')
  let navigateRange: ((range: LibraryRange) => void) | undefined
  const locate = (definition: LibraryDefinition) => {
    if (!session.files().some(file => file.id === definition.id)) return
    session.select(definition.id)
    navigateRange?.(definition.range)
  }
  const download = () => {
    const data = session.files().filter(session.dirtyFile).map(({ id, name, source, revision }) => ({ id, name, source, revision }))
    const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }))
    const a = document.createElement('a'); a.href = url; a.download = 'hass-sharp-library-drafts.json'; a.click(); URL.revokeObjectURL(url)
  }
  const refreshEntities = async () => {
    if (refreshing()) return
    setRefreshing(true); setRefreshMessage('Refreshing entity IDs…')
    try {
      const result = await hass.callWS<{ success: boolean }>({ type: 'hass_sharp/reload_entities' })
      if (!result.success) throw new Error('Refresh failed')
      librariesChanged()
      setRefreshMessage('Entity IDs refreshed. Language services now use the updated symbols.')
    } catch { setRefreshMessage('Entity refresh failed. Try again.') }
    finally { setRefreshing(false) }
  }
  const beforeUnload = (event: BeforeUnloadEvent) => { if (session.dirty()) { event.preventDefault(); event.returnValue = '' } }
  window.addEventListener('beforeunload', beforeUnload)
  onCleanup(() => window.removeEventListener('beforeunload', beforeUnload))
  createEffect(() => props.onDirtyChange?.(session.dirty()))
  return <section class="hs-node-library" classList={{ 'hs-node-library--fullscreen': fullScreen() }} aria-label="Node library">
    <header class="hs-library-heading"><div><p class="hs-eyebrow">REUSABLE C#</p><h1>Node library</h1></div>
      <div class="hs-library-tools"><Button busy={refreshing()} onClick={() => void refreshEntities()}>Refresh entity IDs</Button><Button onClick={() => setFullScreen(!fullScreen())}>{fullScreen() ? 'Exit full screen' : 'Full screen'}</Button></div>
    </header>
    <p class="hs-library-intro">A node is a public static method of a public class marked <code>[Node("Title")]</code>. Parameters are inputs and the return value is the result. Write namespace contents here: all files share <code>NodeLib</code>. XML summary, param and returns comments describe your nodes.</p>
    <Show when={refreshMessage()}><p role="status">{refreshMessage()}</p></Show>
    <Show when={session.conflict() && !session.error()}><div class="hs-notice hs-notice--danger" role="alert">This saved file changed elsewhere. Your draft and original revision are preserved. Download your drafts, then reload to resolve the conflict. <Button onClick={() => setConfirm('reload')}>Reload selected file</Button></div></Show>
    <Show when={session.error()}><div class="hs-notice hs-notice--danger" role="alert">{session.error()} <Show when={session.conflict()}>Your draft is preserved. Download it before reloading.</Show><Button onClick={() => setConfirm('reload')}>Reload selected file</Button><Button onClick={() => void session.refresh()}>Refresh file list</Button></div></Show>
    <Show when={session.activations().length}><section class="hs-library-activations" aria-label="Dependent automations"><For each={session.activations()}>{activation => <p class={activation.active ? 'hs-notice' : 'hs-notice hs-notice--danger'} role={activation.active ? 'status' : 'alert'}>{activation.id}: {activation.active ? 'Saved and active' : `Saved; activation failed: ${activation.activationError ?? 'current revision is not active'}`}</p>}</For></section></Show>
    <Show when={session.loading()}><p role="status">Loading library files…</p></Show>
    <div class="hs-library-layout">
      <aside class="hs-library-files" aria-label="Library files"><h2>Files</h2><ul><For each={session.files()}>{file => <li><button type="button" classList={{ 'hs-library-file--selected': file.id === session.selectedId() }} aria-current={file.id === session.selectedId() ? 'page' : undefined} onClick={() => session.select(file.id)}>
        <span>{file.name}</span><Show when={session.dirtyFile(file)}><span class="hs-library-dirty" aria-label="Unsaved changes">●</span></Show><Show when={file.conflict}><span aria-label="Revision conflict">!</span></Show>
      </button></li>}</For></ul><Button onClick={() => { setNewName('Nodes.cs'); setNewFile(true) }}><Icon name="plus" />Add file</Button><Show when={session.dirty()}><Button onClick={download}>Download drafts</Button></Show></aside>
      <main class="hs-library-source"><Show when={session.selected()} fallback={<div class="hs-empty-state"><h2>Create your first node library</h2><p>Add a file to define types, helpers and reusable nodes.</p><Button onClick={() => session.create()}>Create Nodes.cs</Button></div>}>
        <div class="hs-library-file-toolbar"><label>File name<input class="hs-input" aria-label="Library file name" value={session.selected()!.name} maxLength={120} onInput={e => session.rename(e.currentTarget.value)} /></label>
          <span class="hs-muted">{session.selected()!.revision ? `Revision ${session.selected()!.revision}` : 'New file'}{session.dirtyFile(session.selected()!) ? ' · Unsaved' : ' · Saved'}</span>
          <Button variant="danger" disabled={session.saving()} onClick={() => setConfirm('delete')}>Delete file</Button>
        </div>
        <Editor fileName={session.selected()!.name} documentId={session.selectedId()} initial={session.selected()!.source}
          library={{ context: session.context, sourceGeneration: session.sourceGeneration, documents: session.files, onDefinition: locate }}
          onChange={session.edit} onNavigator={navigate => { navigateRange = navigate }} saveDisabled={!session.canSave()}
          onSave={async () => { await session.validatePreview(); await session.save() }} />
      </Show></main>
      <LibraryPreview catalog={session.preview()?.catalog} sourceId={session.selectedId() ?? ''} pending={session.previewing()} />
    </div>
    <section class="hs-library-diagnostics" aria-label="Library workspace diagnostics" aria-live="polite"><h2>Workspace diagnostics</h2>
      <p>{session.previewing() ? 'Checking workspace…' : `${session.diagnostics().filter(d => d.severity === 3).length} errors · ${session.diagnostics().filter(d => d.severity === 2).length} warnings`}</p>
      <ul><For each={session.diagnostics()}>{d => <li><button type="button" onClick={() => locate({ id: d.sourceId, range: { start: 0, length: 0, startLine: d.startLine, startColumn: d.startColumn, endLine: d.endLine, endColumn: d.endColumn } })}>{d.severity === 3 ? 'Error' : 'Warning'} · {session.files().find(f => f.id === d.sourceId)?.name ?? d.sourceId} {d.startLine}:{d.startColumn} · {d.message}</button></li>}</For>
      <For each={session.validation()?.automations}>{graph => <For each={graph.diagnostics}>{d => <li>{graph.id}{d.nodeId ? ` · ${d.nodeId}` : ''}{d.inputId ? ` · ${d.inputId}` : ''}: {d.message}</li>}</For>}</For></ul>
    </section>
    <p class="hs-muted hs-library-persistence">Saving validates every saved visual automation, advances its revision and activates the rebuilt generation. Running invocations keep their prepared source snapshot. Unsaved files are used together for previews; save each file to publish it.</p>
    <Dialog open={newFile()} title="Add library file" onClose={() => setNewFile(false)} footer={<><Button onClick={() => setNewFile(false)}>Cancel</Button><Button variant="primary" disabled={!newName().trim() || /[\/\\\0\r\n]/.test(newName())} onClick={() => { session.create(newName()); setNewFile(false) }}>Create file</Button></>}><label>File name<input class="hs-input" autofocus value={newName()} maxLength={120} onInput={e => setNewName(e.currentTarget.value)} /></label></Dialog>
    <Dialog open={!!confirm()} title={confirm() === 'delete' ? 'Delete library file?' : 'Reload library file?'} onClose={() => setConfirm(undefined)} footer={<><Button onClick={() => setConfirm(undefined)}>Keep editing</Button><Button variant="danger" onClick={() => { const action = confirm(); setConfirm(undefined); if (action === 'delete') void session.remove(); else if (session.selectedId()) void session.reload(session.selectedId()!) }}>{confirm() === 'delete' ? 'Delete file' : 'Discard draft and reload'}</Button></>}><p>{session.selected()?.name}: {confirm() === 'delete' ? 'Delete this file and discard its draft. Saved dependent automations must still compile before deletion can succeed.' : 'Discard this file’s draft and load the current saved revision. Other files keep their unsaved changes.'}</p></Dialog>
  </section>
}
