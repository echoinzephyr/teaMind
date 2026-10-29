# teaMind development status

## Implemented

- Native WinUI 3 / Win2D editor, local home screen, recent files, favorites and file thumbnails.
- Native text input, graph editing, undo/redo, multiple sheets, file recovery, images and attachments.
- Native document archive, modern Xmind conversion with compatibility reports, PNG/SVG/PDF/Markdown export, configurable AI preview and application.
- Dedicated window dragging area, native maximize/restore handling, constrained file menu, and a text editor without Fluent form-field chrome.
- Committing an in-progress edit before selecting a different node; retaining edit focus across internal control focus changes.
- Automatic assignment of new main branches based on visible subtree height, persistent side assignments, and an undoable rebalance command.
- Cached and viewport-limited drawing, cached dirty-state checks, and background recovery writes.

## Validation

- 33 core tests cover document integrity, undo, safe archives, conversion, mocked AI responses, draft layout and branch balancing.
- Native smoke checks exercise Chinese rendering and the document/PNG/SVG/PDF/Xmind export pipeline.
- Native window regression checks exercise caption hit testing, maximize/restore, and text input at 100%, 150% and 200% canvas zoom.
- Window movement diagnostics compare title-bar configurations using temporary test windows; these measurements are not end-to-end pointer latency or display FPS.

## Open work requested in the review

- Finish investigating the window-drag stutter shown in the user's recording; do not treat passing core tests as proof of smooth interaction.
- Add the template picker to the new-document flow, with basic, planning/review and knowledge-management templates.
- Separate node styling and whole-canvas settings; add visual theme previews and an explicit balance toggle.
- Add contextual node actions and convenient child/sibling creation controls.
- Verify rich Xmind round trips in the real client, keyboard/IME interactions, accessibility, clean-machine distribution and multiple display scales.

Reference images and the user recording are kept outside source control. Generated diagnostics, local settings, credentials, package caches and build outputs are excluded.
