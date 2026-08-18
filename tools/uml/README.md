# Noks UML browser

A Class View for this repository: a Roslyn extractor that reads every project in `Noks.slnx` and a local web server that renders the result as browsable UML.

## Use it

```sh
node tools/uml/server.mjs          # http://localhost:5252
```

The server generates the model on first request if it is missing. Pass a port as the first argument, or set `NOKS_UML_PORT`.

To regenerate the model and the standalone diagrams without the server:

```sh
dotnet run tools/uml/NoksUml.cs
```

Options: `--out <dir>` (default `artifacts/uml`), `--include-vendor` to include `src/vendor/` trees such as BouncyCastle, `--no-tests` to drop `*.Tests` projects, `--no-svg` to skip Graphviz rendering.

## Output

Everything lands in `artifacts/uml/`, which is git-ignored:

- `model.json`: projects, namespaces, types, members, and the inheritance, implementation, and usage edges.
- `dot/*.dot`: one project-dependency graph plus one class diagram per namespace.
- `svg/*.svg`: the same graphs rendered, if Graphviz `dot` is on PATH.

## In the browser

Diagrams are focused, not exhaustive. A namespace with 109 types cannot be drawn legibly on one screen, so every diagram is built around the selected type and capped at a node budget, ranked by how strongly each type relates to it. The status pill always states what was left out, for example `Dct3Machine: 14 of 30 related types`. Raise `max` to see more, at the cost of readability.

- Left pane is the tree: group, project, namespace, type, member. Click a label to select and expand. The search box filters by type or member name and reveals matches. Public and protected members are listed by default; `private members` adds the rest.
- Middle pane is the diagram. Drag to pan, scroll or use `-`/`+` to zoom, `Fit` to reset, `SVG` to download the current view. Solid arrows are inheritance, dashed are interface implementation, dotted are usage, with line weight following the number of references.
- Every box has two controls. The triangle on the left opens that box to list its public members. The `+` on the right, with the count of linked types not yet drawn, pulls those types into the diagram; it becomes `-` and removes them again. Clicking the body of a box selects it and updates the detail pane without disturbing the rest of the diagram, so you can walk the graph one hop at a time and keep what you have already opened. `Reset` returns to the starting set, and each `+` adds at most `max` boxes, up to 80 in total.
- Right pane is the detail view: signature, doc comment, source path with a `vscode://` link, base and derived types, `Uses` and `Used by` with reference counts, and members by kind.
- The diagram selector offers the neighbourhood of the selected type (`hops` controls how far), its inheritance tree, a namespace map ranked by connection count, the project dependency graph, and the namespace dependency graph.
- The two buttons at the far left and next to `Regenerate` collapse the side panes when a diagram needs the width.
- `Regenerate` re-runs the extractor, so the diagrams follow the code without restarting the server.

## How it works

`NoksUml.cs` is a single-file .NET program that parses `Noks.slnx` for projects, reads each `.csproj` for references and target frameworks, and parses every `.cs` file with the Roslyn syntax API. It is syntax-only: no build, no restore of the analysed projects, about two seconds for the whole repository. Type references resolve by simple name, preferring the same namespace, then the same project, then the referenced projects.

The server has no npm dependencies. It serves `viewer/`, proxies DOT to `dot -Tsvg`, and shells out to `dotnet run` for regeneration.

## Requirements

.NET 10 SDK, Node 18 or newer, and Graphviz for diagram rendering (`brew install graphviz`). Without Graphviz the model and the tree still work, and the diagram pane reports that `dot` is missing.
