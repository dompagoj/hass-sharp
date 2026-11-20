import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, it } from 'vitest'
import { nodeCatalog } from '../src/visual/contract'
import { builtinEntities, sharedClasses } from '../src/entities/builtinEntities'
import { classSource, sourceFiles } from '../src/entities/builtinSource'

// The entity help shows the built-in entity classes as the library compiles them: the text is cut from the C# files of
// this checkout when the frontend is built, so these tests hold the list and the cut to those files.
const library = '../HassSharp'
const listed = [...builtinEntities, ...sharedClasses]
/** Every C# file of the library, by its path under HassSharp/ (no build output). */
function csharpFiles(dir = library): { path: string; text: string }[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
    const path = join(dir, entry.name)
    if (entry.isDirectory()) return entry.name === 'bin' || entry.name === 'obj' ? [] : csharpFiles(path)
    return entry.name.endsWith('.cs') ? [{ path: path.slice(library.length + 1), text: readFileSync(path, 'utf8') }] : []
  })
}
const declaration = (name: string) => new RegExp(`^(?:\\w+ )*class ${name}\\b`, 'm')

it('takes each file as it is in the library, unchanged', () => {
  for (const [file, text] of Object.entries(sourceFiles))
    expect(text, file).toBe(readFileSync(`${library}/Devices/${file}`, 'utf8'))
  for (const item of listed)
    for (const file of item.files) expect(Object.keys(sourceFiles), `${item.className}: ${file}`).toContain(file)
})

it('cuts each listed class from its documentation comment to its closing brace, and nothing of another listed class', () => {
  for (const item of listed) {
    expect(classSource(item.className, item.files), item.className).toBeDefined()
    // Each part, as one file has it.
    for (const file of item.files) {
      const text = classSource(item.className, [file])
      expect(text, item.className).toBeDefined()
      expect(text!.startsWith('/// <summary>'), item.className).toBe(true)
      expect(text, item.className).toMatch(declaration(item.className))
      expect(text!.endsWith('\n}'), item.className).toBe(true)
      expect(sourceFiles[file], item.className).toContain(text)
      for (const other of listed)
        if (other !== item) expect(text, `${item.className} holds ${other.className}`).not.toMatch(declaration(other.className))
    }
  }
  expect(classSource('SharpNothing', ['SharpEntities.cs'])).toBeUndefined()
})

it('lists every entity class the library derives from SharpEntity, and every domain the editor offers', () => {
  const files = csharpFiles()
  const derived = files.flatMap(f => [...f.text.matchAll(/^public (?:\w+ )*class (\w+)\s*:\s*SharpEntity\b/gm)].map(m => m[1]))
  expect(derived.length).toBeGreaterThan(0)
  expect(builtinEntities.map(e => e.className).sort()).toEqual([...derived].sort())
  // The domains are the catalog's, in its order, each with the editor's label.
  expect(builtinEntities.map(e => e.domain)).toEqual(Object.keys(nodeCatalog.entityDomains!))
  expect(builtinEntities.map(e => e.label)).toEqual(Object.values(nodeCatalog.entityDomains!).map(d => d.label))
  // Each class is the one the library gives that domain (SharpDomains in SharpEntities.cs).
  const typed = Object.fromEntries([...readFileSync(`${library}/Devices/SharpEntities.cs`, 'utf8').matchAll(/\(typeof\((\w+)\), "(\w+)"\)/g)].map(m => [m[2], m[1]]))
  expect(Object.fromEntries(builtinEntities.map(e => [e.domain, e.className]))).toEqual(typed)
  // Every file that declares a part of a listed class is one it is read from.
  for (const item of listed) {
    const declaring = files.filter(f => declaration(item.className).test(f.text)).map(f => f.path.replace(/^Devices\//, ''))
    expect(item.files, item.className).toEqual(declaring)
  }
  expect(sharedClasses.map(c => c.className)).toEqual(['SharpEntity', 'SharpComponent', 'SharpDevice'])
})

it('shows each part of a partial class under a line naming its file, in the order listed', () => {
  const files = { 'A.cs': 'namespace X;\n\n/// <summary>A.</summary>\npublic partial class Thing\n{\n    int a;\n}\n', 'B.cs': 'namespace X;\n\n/// <summary>B.</summary>\n[Obsolete]\npartial class Thing\n{\n    int b;\n}\n\n/// <summary>Other.</summary>\nclass Other\n{\n}\n' }
  expect(classSource('Thing', ['A.cs', 'B.cs'], files)).toBe(
    '// A.cs\n/// <summary>A.</summary>\npublic partial class Thing\n{\n    int a;\n}\n\n// B.cs\n/// <summary>B.</summary>\n[Obsolete]\npartial class Thing\n{\n    int b;\n}')
})
