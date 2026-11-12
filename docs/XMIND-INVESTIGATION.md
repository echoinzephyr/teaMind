# Xmind investigation and teaMind changes

## Scope and evidence

Inspected the locally installed Windows executable reporting `26.5.1107.202609082009`.
The archive package reports `26.05.01107`. This is one installed version, not a claim
about every Xmind release. Application source maps are absent. Minified identifiers
and bundle locations may change between releases.

`scripts/inspect-xmind.py` reads the archive without extracting or modifying it. It
outputs entrypoint dependencies, hashes and character offsets of selected symbols.
Application code, assets and extracted archive contents are not included in this
repository. The implementation changes in teaMind are independently written.

Example (replace the installation path as needed):

```powershell
python scripts/inspect-xmind.py "$env:LOCALAPPDATA\Programs\Xmind\resources\app.asar" --output artifacts/xmind-inspection.json
```

## Confirmed by static inspection

- **Packaging and UI entrypoints.** Electron application with separate editor,
  editor-frame, welcome/new, map-template and theme-editor HTML/JavaScript
  entrypoints. Shared chunks are loaded by those pages. This alone does not establish
  which processes or windows are active at runtime.
- **Balanced branch allocation.** The map structure implementation in
  `renderer/1857.js` (`sC`, character offset 1867708 in the inspected version)
  evaluates actual branch heights and spacing. It accounts for boundary and
  summary allocation groups while selecting the split between the two sides.
  It also supports unbalanced structures with an explicit first-half child count.
  It is more than a fixed split by child count.
- **Direction and order.** Layout factors consult previous child directions or
  offsets, then coordinate final directions with computed offsets. The clockwise
  structure lays out the first side right/down and the second side left/up.
  This is not evidence that existing branch sides never change in balanced mode.
- **Drag guidance and navigation.** Each side provides insertion targets,
  direction-specific drop indicators, constrained topic ranges and keyboard
  navigation rules. A left branch's outward direction differs from a right branch's.
- **Viewport input.** Gesture requests explicitly distinguish start, change and end.
  Wheel handling schedules viewport updates with `requestAnimationFrame`; there are
  separate inertia and reveal helpers. This supports separating view movement from
  document editing. It does not prove a universal one-render-per-frame guarantee.
- **Text composition.** Text input handles `compositionstart`, `compositionend`,
  `beforeinput`, selection and focus as distinct states. teaMind retains its native
  TextBox input engine and only schedules draft geometry updates.
- **Message scheduling.** `renderer/editor.js` includes a queued message-processing
  helper using animation-frame scheduling, batch strategies and time slicing.
  Its presence does not prove that every graph update is incremental.

SVG and Canvas APIs appear in shared code. Their presence alone cannot identify
the primary on-screen renderer, GPU execution cost or actual frame rate.

## First implementation batch

- Merge pending document and draft changes before the next native canvas draw.
  Hit testing, editing and export flush pending geometry synchronously when needed.
- Bound draft geometry updates to one scheduled pass per 50 ms while typing;
  native text input remains immediate, and committing/cancelling uses the latest text.
- Coalesce document-driven UI refreshes on the dispatcher. Undo history still
  records each document command independently; saving and recovery semantics remain
  document operations.
- Skip subtree measurement when all main branches already have explicit sides.
- Cache the visible scene in a bounded GPU bitmap, including 300 screen DIP of
  overscan. Panning reuses that texture. Zooming temporarily reuses it within a
  resolution band and regenerates at the final scale after 120 ms of inactivity.
  Selection and marquee overlays are still drawn separately. Vector exports use
  the original scene renderer. Cache backing storage is capped at 16 million pixels
  and 8192 pixels per dimension, accounting for display DPI.

## Reproducible native diagnostics

```powershell
$env:TEAMIND_DATA_DIRECTORY = "$PWD\artifacts\interaction-test-data"
artifacts/teaMind-interaction-fix/teaMind.exe --interaction-perf-test "$PWD\artifacts\interaction-test" --close-after-test
```

The diagnostic uses an isolated app window and synthetic documents with 20, 1000
and 5000 nodes. It exercises pan/zoom, native TextBox changes, a burst of 12 document
commands, resizing, and homepage window movement. A background probe measures time
waiting for the UI dispatcher; separate counters track layout, draw/cache work and
UI refreshes. It also verifies draft isolation, latest text, single-step text undo,
and independent command undo after coalesced refreshes.

These are scripted native-window measurements. CPU draw callbacks and dispatcher
delay are not display FPS, GPU timing or mouse-to-display latency. Programmatic
window movement does not reproduce an actual caption drag. The reported homepage
drag stutter remains unresolved; the homepage measurements contain no graph draws
or graph layouts, so graph optimization alone cannot explain or fix it.

### Local before/after sample

One sequential comparison on this machine, .NET runtime 10.0.4, hardware rendering
enabled. The baseline contains only the diagnostic counters, before optimization.
Wall-clock values vary with scheduling and are not a statistical benchmark.

| Scripted scenario / measure | Baseline | Optimized |
| --- | ---: | ---: |
| 1000 nodes, 12 commands: layout passes | 12 | 1 |
| 1000 nodes, 12 commands: UI refreshes | 12 | 1 |
| 1000 nodes, 12 commands: cumulative layout CPU time | 37.6 ms | 1.2 ms |
| 1000 nodes, 20 text changes: layout passes | 20 | 1 |
| 1000 nodes, text changes plus settling wait: elapsed | 297.5 ms | 152.5 ms |
| 5000 nodes, pan/zoom: maximum dispatcher queue delay | 63.5 ms | 1.4 ms |
| 5000 nodes, text changes: maximum dispatcher queue delay | 32.3 ms | 54.8 ms |
| Homepage window movement: maximum dispatcher queue delay | 15.1 ms | 13.3 ms |

The text-change elapsed value includes the same 100 ms settling wait in both
runs. The increased maximum pause during a 5000-node cache rebuild is still a
limitation: the work is less frequent, but one regeneration can be expensive.
Homepage movement has no meaningful demonstrated improvement.

Raw reports are generated under `artifacts/interaction-baseline`,
`artifacts/interaction-final` and `artifacts/interaction-comparison.json` and are
excluded from source control. Native window checks additionally wait for the
latest canvas draw and exercise cancelling a queued draft.

## Remaining work

Validate real pointer traces and composition timing for window movement, then
implement the template picker, separate node/canvas settings and contextual node
actions. Test Xmind branch behavior with actual operations and saved documents
before making further compatibility changes based on static code alone.

Detailed command and theme findings are recorded in [XMIND-BEHAVIOR.md](XMIND-BEHAVIOR.md).
