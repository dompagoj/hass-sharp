# Monaco shadow DOM hover patch

`monaco-editor@0.57.0.patch` is pinned through Bun `patchedDependencies` and
`bun.lock`. It changes the document mousemove recovery listener to inspect the
composed event path before emitting a synthetic mouseleave. Home Assistant hosts
the panel beneath open shadow roots, so the document-level target is a shadow
host even though the pointer remains inside Monaco. The false mouseleave cancels
compiler and symbol hover. The panel's own light DOM does not avoid ancestor
shadow retargeting.

Upstream reports and fix proposal:
- https://github.com/microsoft/monaco-editor/issues/3409
- https://github.com/microsoft/vscode/pull/284890

The local patch uses `composedPath()` rather than coordinate hit testing, keeping
real outside moves and document event propagation intact. Closed shadow roots
hide their internal path and are outside this workaround's scope. Revisit when
upgrading Monaco; remove once its global mouseleave recovery handles shadow DOM.
`tests/monaco-shadow-hover.test.ts` extracts and executes the installed package's
actual listener with nested open-shadow event paths, so an unapplied patch fails.
For native preview use `tests/preview.html?view=library&shadow=1`.
