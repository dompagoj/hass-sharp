# The built-in entities' source in the entity help

## Intent

A person writing a C# entity node derives one of HassSharp's entity classes (`SharpLight`, `SharpSwitch`, ...) and
overrides its methods. The quickest way to learn what there is to override, and what the class does with a command,
is to read the class. The "Entity definition help" dialog gets a list of the built-in entities; clicking one shows
its source, exactly as it is compiled into the library.

## Goals

- The dialog lists every built-in entity type by the name the editor uses for it: Binary sensor, Sensor, Switch,
  Button, Number, Select, Text, Light, Cover, Camera.
- Clicking a name shows that class's whole declaration with its documentation comments, read-only, with C#
  colouring; clicking it again hides it. Several can be open at once.
- Below the ten, under a line such as "What they share": `SharpEntity`, `SharpComponent` and `SharpDevice`, shown the
  same way. `Setup()`, `Start()`, `Stop()`, `Every`, `Track` and `Services` live there, so the list is not complete
  without them. (Controller's call, not asked of the owner.)
- The text shown cannot drift from the code that runs: it is taken from the `.cs` files of the same checkout when
  the frontend is built, never copied by hand.
- One short paragraph above the list says what the reader is looking at and how to use it: this is the code that
  runs; to make an entity of your own, add a C# entity node, derive the class and override its methods; members
  marked `internal` are the library's own and a node's class cannot override or call them.

## Non-goals

- No editing, no "copy as a starting point" button, no search.
- No change to any C# file, to Python, to the catalog or to what the library does.
- Not shown in the automation editor's help or the node library's help.

## Anti-goals

- A hand-kept copy of the source, or a list of types that a new entity class can be left out of unnoticed.
- The source in the panel's first load: it is fetched only when the first item is opened.

## Approaches

1. **Take the source at frontend build time** (chosen). The frontend imports the files under
   `custom_components/hass_sharp/HassSharp/Devices/` as text and cuts each class out. No backend, no Python, nothing
   new at run time; the frontend and the library are built from one checkout in every release. Cost: the cut is
   done by text, not by a parser, so a test must hold it to the files.
2. Embed the files in the library and serve a class's text through a new operation, cut exactly with Roslyn. Exact,
   and an MCP assistant could read it too, but it adds a C# operation, a Python websocket command and their tests
   for a help text.
3. Link to the files on GitHub. Nothing to build, but it needs a network, shows `main` and not the installed
   version, and leaves the editor.

## Design

- **Where the text comes from.** A module of its own, loaded on demand (`import()`), holds the files' text (Vite's
  `?raw` import of the `.cs` files; if the build or the test runner refuses a file outside the frontend's folder, a
  small generated module written by a build script and checked by a test is the fallback) and one function that
  returns a class's declaration: from the first line of its documentation comment (and any attributes) to the
  closing `}` in column 0. Every listed class is a top-level declaration in a file-scoped namespace, so that brace is
  its end. `SharpEntity` is `partial`: its parts are shown one after another, in a fixed order, each headed by a
  comment line naming its file.
- **The list.** A table in the frontend of `{ label, className, file }` for the ten types, with labels read from the
  catalog's `entityDomains` so they are the editor's own words, and the three shared classes.
- **The view.** The existing read-only code view (`components/Editor/CodeView.tsx`, already loaded on demand by
  `visual/typeDefinition.tsx`) with a bounded height and its own scrolling; on a phone it scrolls sideways, the page
  does not. Each item is a button with `aria-expanded`; Enter and Space toggle it.
- **Held to the code by tests** (frontend):
  - each listed class is found, begins with `/// <summary>`, contains `class <Name>`, ends with `}`, and does not
    contain another listed class's declaration;
  - every `public class ... : SharpEntity` in the files is in the list, and the list's domains are exactly the
    catalog's `entityDomains` keys, so a new type cannot be left out;
  - the dialog shows the thirteen names; opening Switch shows text with `class SharpSwitch` and
    `protected virtual Task TurnOn()`; opening it again hides it; nothing of the source is requested before the first
    click.
- **Words.** The dialog's paragraph, and one sentence in `docs/custom-entities.md` where C# entity nodes are
  introduced, saying the help lists the built-in classes with their source.

## Left to the builder

Where in the dialog the section sits (after "What an entity definition is" unless it reads badly), the exact
sentences, the height of the code view, and the names of the module and its functions.
