import { readFileSync } from 'node:fs'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { editors, host, resetHost } from './fake-monaco'
import { EntityHelp } from '../src/entities/EntityHelp'

vi.mock('monaco-editor', async () => (await import('./fake-monaco')).host)
vi.mock('monaco-vim', () => ({ initVimMode: () => ({ dispose() {} }) }))
// Counts imports of the classes' source and of the code view: none until the first class is opened.
const loads = vi.hoisted(() => ({ source: 0, view: 0 }))
vi.mock('../src/entities/builtinSource', async importOriginal => { loads.source++; return importOriginal() })
vi.mock('../src/components/Editor/CodeView', async importOriginal => { loads.view++; return importOriginal() })

let dispose: (() => void) | undefined
beforeEach(() => resetHost())
afterEach(async () => {
  dispose?.(); dispose = undefined
  vi.restoreAllMocks()
  document.body.innerHTML = ''
  await new Promise(resolve => setTimeout(resolve, 0))
})

function open() {
  const el = document.createElement('div'); el.className = 'hs-app'; document.body.append(el)
  dispose = render(() => <EntityHelp open onClose={() => {}} />, el)
  return document.querySelector<HTMLDialogElement>('dialog[open]')!
}
const items = (dialog: Element) => [...dialog.querySelectorAll<HTMLButtonElement>('.hs-entity-source__toggle')]
const item = (dialog: Element, name: string) => items(dialog).find(b => b.querySelector('.hs-entity-source__name')!.textContent === name)!
/** The text each code view on show was given, in order. */
const shownCode = () => editors.map(editor => editor.model.getValue())

// First in the file: the source's module is imported once per file, by the first class opened.
it('lists the built-in entities and what they share, and loads their source only when the first one is opened', async () => {
  const dialog = open()
  expect(items(dialog).map(b => b.querySelector('.hs-entity-source__name')!.textContent)).toEqual([
    'Binary sensor', 'Sensor', 'Switch', 'Button', 'Number', 'Select', 'Text', 'Light', 'Cover', 'Camera',
    'SharpEntity', 'SharpComponent', 'SharpDevice'])
  expect(item(dialog, 'Switch').querySelector('code')!.textContent).toBe('SharpSwitch')
  expect(dialog.querySelector('.hs-entity-source__shared')!.textContent).toBe('What they share')
  for (const b of items(dialog)) {
    expect(b.type).toBe('button')
    expect(b.getAttribute('aria-expanded')).toBe('false')
  }
  await new Promise(resolve => setTimeout(resolve, 20))
  const createModel = vi.spyOn(host.editor, 'createModel')
  expect(loads, 'nothing of the source before the first click').toEqual({ source: 0, view: 0 })

  // Switch opens on its source, read only, with C# colouring.
  item(dialog, 'Switch').click()
  expect(item(dialog, 'Switch').getAttribute('aria-expanded')).toBe('true')
  await vi.waitFor(() => expect(shownCode()).toHaveLength(1))
  expect(shownCode()[0]).toContain('public class SharpSwitch : SharpEntity')
  expect(shownCode()[0]).toContain('protected virtual Task TurnOn()')
  expect(shownCode()[0]).not.toContain('class SharpButton')
  // The class as the library's file has it, from its summary to its closing brace.
  expect(shownCode()[0]!.startsWith('/// <summary>\n/// A switch:')).toBe(true)
  expect(readFileSync('../HassSharp/Devices/SharpEntities.cs', 'utf8')).toContain(shownCode()[0])
  expect(editors[0]!.options).toMatchObject({ readOnly: true, domReadOnly: true, ariaLabel: 'C# source of SharpSwitch' })
  expect(createModel).toHaveBeenCalledWith(shownCode()[0], 'csharp')
  const region = document.getElementById(item(dialog, 'Switch').getAttribute('aria-controls')!)!
  expect(region.querySelector('.hs-code-view')).not.toBeNull()
  expect(loads).toEqual({ source: 1, view: 1 })

  // Several open at once; opening one again hides it.
  item(dialog, 'SharpComponent').click()
  await vi.waitFor(() => expect(dialog.querySelectorAll('.hs-code-view')).toHaveLength(2))
  expect(editors.at(-1)!.model.getValue()).toContain('public abstract class SharpComponent')
  item(dialog, 'Switch').click()
  expect(item(dialog, 'Switch').getAttribute('aria-expanded')).toBe('false')
  expect(item(dialog, 'Switch').getAttribute('aria-controls')).toBeNull()
  expect(dialog.querySelectorAll('.hs-code-view')).toHaveLength(1)
  expect(dialog.textContent).not.toContain('protected virtual Task TurnOn()')
  expect(item(dialog, 'SharpComponent').getAttribute('aria-expanded')).toBe('true')
})

it('says what the list is and how to use it', () => {
  const text = open().textContent!
  expect(text).toContain('Built-in entities')
  expect(text).toContain('This is the code HassSharp runs for each type of entity')
  expect(text).toContain('add a C# entity node, derive its class from one of these and override its methods')
  expect(text).toContain('Setup, Start, Stop, Every, Track and Services come from the classes under What they share.')
  expect(text).toContain('A member marked internal alone, not protected internal, is the library\'s own: a node\'s class cannot override or call it.')
  expect(text).toContain('Select a name to show its code, and select it again to hide it.')
})
