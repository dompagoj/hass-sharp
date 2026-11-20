// The C# files of the library's entity classes, as text, taken from this checkout when the frontend is built. Loaded
// only when the entity help first shows a class (a chunk of its own).
import camera from '../../../HassSharp/Devices/SharpCamera.cs?raw'
import component from '../../../HassSharp/Devices/SharpComponent.cs?raw'
import controls from '../../../HassSharp/Devices/SharpControls.cs?raw'
import device from '../../../HassSharp/Devices/SharpDevice.cs?raw'
import entities from '../../../HassSharp/Devices/SharpEntities.cs?raw'
import entity from '../../../HassSharp/Devices/SharpEntity.cs?raw'

/** The files' text, by their names in HassSharp/Devices/. */
export const sourceFiles: Readonly<Record<string, string>> = {
  'SharpCamera.cs': camera,
  'SharpComponent.cs': component,
  'SharpControls.cs': controls,
  'SharpDevice.cs': device,
  'SharpEntities.cs': entities,
  'SharpEntity.cs': entity,
}

/**
 * A top-level class of one file, from the first line of its documentation comment and attributes to its closing `}`
 * in column 0 (each class is in a file-scoped namespace, so that brace ends it); undefined when the file declares none.
 */
function cut(text: string, className: string): string | undefined {
  const lines = text.replace(/\r\n/g, '\n').split('\n')
  const declaration = new RegExp(`^(?:\\w+ )*class ${className}\\b`)
  const at = lines.findIndex(line => declaration.test(line))
  if (at < 0) return
  let start = at
  while (start > 0 && /^(\/\/\/|\[)/.test(lines[start - 1]!)) start--
  const end = lines.findIndex((line, index) => index > at && line === '}')
  return end < 0 ? undefined : lines.slice(start, end + 1).join('\n')
}

/**
 * The declaration of a class, as {@link cut} finds it in each of `files` that has a part of it: one part as it is,
 * several one after another, each under a comment line naming its file. Undefined when no file declares it.
 */
export function classSource(className: string, files: readonly string[], from: Readonly<Record<string, string>> = sourceFiles): string | undefined {
  const parts = files.flatMap(file => { const part = cut(from[file] ?? '', className); return part === undefined ? [] : [{ file, part }] })
  if (!parts.length) return
  return parts.length === 1 ? parts[0]!.part : parts.map(({ file, part }) => `// ${file}\n${part}`).join('\n\n')
}
