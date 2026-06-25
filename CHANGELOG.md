# Changelog

All notable changes to DataPortStudio are documented here.

---

## v1.0.5 — 2026-06-25

### Fixed
- **TPS editing — null fields on non-RLE pages not persisting** — when a record lived on an uncompressed (non-RLE) TPS page and its key field (e.g. `CLASSNAME`) was all-spaces / null, the previous code used `Array.IndexOf` to locate the record's bytes inside the raw page data. Because the null-field pattern (a block of spaces) can appear at multiple positions in the page, `IndexOf` matched the wrong occurrence — the write went to the wrong bytes, the file appeared to save (patched count = 1, no error dialog), but reloading from disk showed the original null value. The fix replaces `IndexOf` with the same sequential `decPos`-walk used for RLE pages, computing the exact file offset for each record regardless of its byte content.

---

## v1.0.4 — 2026-06-25

### Fixed
- **TPS editing — null FString fields not persisting** — CLASSNAME, FIELDNAME, and similar FString fields whose bytes all fall inside RLE run blocks now save correctly and survive a close/reopen cycle. Three bugs were fixed together:
  1. **Direct-patch / re-encode conflict**: when a record had one field that could be direct-patched and another that required re-encoding, the re-encoding phase silently overwrote the direct-patched bytes. All field changes for RLE pages are now routed through the decoded working copy so re-encoding picks them all up.
  2. **Premature patched counter**: `patched` was incremented when a re-encoding was *staged*, before the encoding phase ran. If encoding later failed (new size too large), the counter was still > 0 and the unchanged file was written. `patched` is now counted only when re-encoding actually succeeds.
  3. **AcceptChanges on failure**: `DataTable.AcceptChanges()` was called unconditionally, causing the grid to display the user's new values even when the file was not updated. The grid now always reloads from disk after a save, showing the true file contents.

---

## v1.0.3 — 2026-06-25

### Fixed
- **TPS editing — RLE run bytes** — editing FString fields (e.g. `CLASSNAME`) on RLE-compressed pages no longer produces "stored in RLE run — cannot patch without page recompression" warnings. The writer now performs full page RLE re-encoding when any changed byte lands in a run block: it decodes the page, applies all field changes to the decoded bytes, re-encodes with the exact Clarion greedy algorithm, writes the new compressed data, and updates the 2-byte page-size field in the page header if the encoded size decreased. Changing a value to a longer string that exceeds the original page space reports a clear error instead of silently failing.

---

## v1.0.2 — 2026-06-25

### Fixed
- **TPS editing — RLE-compressed pages** — records in TPS tables with long string fields (e.g. `CLASSNAME`, `FIELDNAME`) stored in RLE-compressed pages could not be located for write-back, producing *"could not locate in file"* warnings for records like 596–602 and 175. The writer now decodes the TPS run-length encoding layer, walks records sequentially through the decoded space using the correct delta-preamble sizes, and patches each field byte at its literal-block encoded offset.

---

## v1.0.1 — 2026-06-25

### Added
- **TPS editing** — Clarion TPS records now open in an editable grid. Cell changes are serialized back to the binary `.tps` file using direct byte patching (all field types supported: integer, float, string, date, time, BCD decimal). Adding and deleting rows is not supported (requires index-file maintenance). DAT files remain read-only.

---

## v1.0.0 — 2026-06-25

### Changed
- Project rebranded from **NavMeCat** to **DataPortStudio**.
- New public repository at [github.com/robertorenz/DataPortStudio](https://github.com/robertorenz/DataPortStudio).
- All namespaces, window titles, AppData paths, and references updated to `DataPortStudio`.

---

## v1.59.1 — 2026-06-20

### Fixed
- Opening a **Firebird** table no longer fails with *"Could not open table — Failed to enable constraints."* The table was loaded via `DataTable.Load()`, which imports the provider's primary-key/NOT-NULL schema and re-enables it after loading — throwing whenever the stored data violates it (NULLs in a column the engine reports as a key, duplicate keys after charset folding, etc.). Firebird is now read into a constraint-free `DataTable`, reading each cell defensively (mirrors the existing Oracle fix).

---

## v1.59.0 — 2026-06-19

### Added
- **SQL syntax highlighting** — AvalonEdit with a custom `.xshd` definition: keywords, functions, strings, comments, operators each in a distinct color. Applies to the Query Window and Routine Editor.
- **SQL autocompletion** — triggers on typing (2+ chars) or `Ctrl+Space`. Suggests keywords, table names from the active schema, and column names from tables referenced in the current query. Dot-triggered: `table.` → columns, `alias.` → resolves alias to columns.
- **Multi-schema autocomplete** — `dbo.` lists tables in that schema; `dbo.Table.` lists columns; schema names are also suggested as completion items.
- **Dark theme toggle** — switchable between Light and Dark in Settings; persists across sessions. Dark theme styled to VS Code palette.
- **SQL Beautifier** (`Ctrl+Shift+F`) — custom tokenizer, no external dependencies. Works on selected text or the entire editor. Available in Query Window and Routine Editor.
- **Query History** — last 50 queries per connection, persisted to AppData. Popup dropdown: single-click loads, double-click runs.
- **Multiple resultsets** — `TabControl` replaces the single DataGrid; each `SELECT` in a batch gets its own tab.
- **Schema Diff** — compare two databases on the same connection; expandable UI showing missing/differing tables and columns.
- **ER Diagram** — WebView2 canvas with force-directed layout, drag nodes, pan, zoom, and Bézier FK arrows.
- **Find in editor** (`Ctrl+F`) — AvalonEdit `SearchPanel` with a fully custom themed template; dropdown for Match case / Whole words / Regex; `◄ ►` navigation; `✕` to close.
- **Session memory** — last active database per connection is saved and restored automatically on next launch.
- **Diff and ER buttons** in the main toolbar (ribbon).
- **Export button** in the Query Window toolbar.
- **App icon** wiring (`Assets/AppIcon.ico`, `csproj`, `App.xaml.cs`).

### Fixed
- **Dark theme — Menu bar** invisible (black text on dark background) → full `MenuItem`/`ContextMenu`/`Separator` template override.
- **Dark theme — SearchPanel** buttons showed empty borders (AvalonEdit paths used `SystemColors.ControlTextBrush`) → replaced with Unicode icons (`◄ ► ✕ ▾`) via custom `ControlTemplate`.
- `SystemColors` overrides added to dark theme so any WPF control using system colors renders correctly.
- `AppSettings.Clone()` was a shallow copy, causing the `LastDatabases` dictionary to be shared between instances → now deep-copies the dictionary.
- `SearchPanel.MarkerBrush` applied via XAML Style threw `NullReferenceException` (panel not yet attached to `TextArea`) → moved to code-behind, set after `Install()`.

---

## v1.58.2 — 2026-06-18

### Fixed
- Opening Oracle tables with out-of-range `DATE` values no longer crashes.

---

## v1.58.1

### Fixed
- "Copy table failed" (empty schema) when pasting TPS/DAT into SQL Server.

---

## v1.58.0

### Added
- Full Oracle editing: row edits, `DROP TABLE`, and copy as paste target.

---

## v1.57.0

### Added
- Oracle connections (read-only).

---

## v1.56.1

### Fixed
- Grid jumping to the end when changing a column "Show as" mode.

---

## v1.56.0

### Changed
- Structure panel now shows **Indexes** instead of Relationships.

---

## v1.55.4

### Fixed
- SQLite `DROP TABLE` failing with a `FOREIGN KEY` constraint error.

---

## v1.55.3

### Fixed
- Refresh both the tree and Objects list after pasting a table.

---

## v1.55.2

### Changed
- Hide **Paste** for read-only engines in the Objects list.

---

## v1.55.1

### Changed
- Unified "Drop" wording; hide Drop for read-only engines in the Objects list.

---

## v1.55.0

### Added
- Tree table context menu matches the Objects list.

### Fixed
- Slow SQLite drop confirmation.

---

## v1.54.3

### Added
- Credits & licenses in the User Guide; external links open in browser.

---

## v1.54.2

### Added
- FAQ section in the User Guide (English & Spanish).

---

## v1.54.1

### Added
- `F1` opens the User Guide.

---

## v1.54.0

### Added
- Built-in User Guide (English & Spanish) in the Help menu.

---

## v1.53.2

### Added
- Column-mapping dialog: SQL type dropdown + size field.

---

## v1.53.1

### Fixed
- Convert Clarion `LONG` date/time to real SQL date/time on copy.

---

## v1.53.0

### Added
- Read classic Clarion `.DAT` files.
- Editable TPS/DAT → SQL type mapping.

---

## v1.52.1

### Added
- TPS: list `.tps` files in the Objects tab on the connection.

---

## v1.52.0

### Added
- Read Clarion TPS files (folder connection).
- Copy TPS → SQL Server/SQLite.

---

## v1.51.1

### Added
- Export complete: **Open file** / **Open folder** buttons.

---

## v1.51.0

### Added
- Export: filter scope choice + JSON formatting options.

---

## v1.50.0

### Added
- Export formats: DBF, TXT, XLS (legacy Excel), and SQL (`INSERT` statements).

---

## v1.49.1

### Fixed
- User manager: SQL Server principal load (bit cast error).

---

## v1.49.0

### Added
- User & role manager: logins, roles, privileges.

---

## v1.48.1

### Fixed
- MySQL/MariaDB: don't pin to the default database for server-level operations.

---

## v1.48.0

### Added
- MySQL and MariaDB support.

---

## v1.47.0 – v1.47.3

### Added / Changed
- Colored command-bar icons; iterative icon polish.

---

## v1.46.1

### Fixed
- No crash on `SELECT *` cross joins in query results.

---

## v1.46.0

### Added
- Visual query designer works with SQLite and Firebird.
- Command-bar button for Query Builder.

---

## v1.45.0

### Added
- Query window works with SQLite (and Firebird).

---

## v1.44.0

### Added
- Navicat-style command bar.

---

## v1.43.0 – v1.43.1

### Added
- Copy/paste tables from the Objects list.
- Right-click context menu on the Objects list.

---

## v1.42.0 – v1.42.4

### Added
- Multi-cell live fill while typing.

### Added
- Info tab: object header, owner, collation, size.

---

## v1.39.0 – v1.41.0

### Added
- Objects tab (persistent, replaces overlay).
- Object list on database and schema nodes.
- Richer Info tab (Navicat-style).
- Tab tooltips showing table origin.

---

## v1.37.0 – v1.38.0

### Added
- SQL Server tree: skip database level when a default DB is set.
- Object list on database & schema nodes.

---

## v1.35.0 – v1.36.0

### Added
- Cross-engine table copy.
- Navicat-style object list.
- Stronger drop warning.

---

## v1.32.0 – v1.34.0

### Added
- Copy & paste a table (same connection).
- Paste into a different connection (same engine).
- `SqlBulkCopy` for cross-connection SQL Server paste.

---

## v1.31.0

### Added
- MongoDB connection + document viewer.

---

## v1.29.0 – v1.30.0

### Added
- Firebird connections.
- Firebird embedded (no-server) mode.

---

## v1.27.0 – v1.28.0

### Added
- Multi-engine connections + SQLite support.
- Table designer for SQLite.

---

## v1.25.0 – v1.26.1

### Added
- Data import/export tools.
- Safe drop.
- Spanish localization.
- Adaptive overflow command bar.

---

## v1.0.0

### Added
- Initial WPF SQL Server database manager.
- Table browser with tabs, editable grid, clipboard copy/paste.
- Clarion date/time/timestamp detection.
- SQL query window.
- Graphical query builder.
- Structure inspector, SQL preview, cell detail pane.
- Tree filter/locator.
- Settings screen.
- Menu bar with keyboard shortcuts.
- Export (CSV, TSV, JSON, XML, HTML, XLSX).
- Stored procedure / function / view editor.
- Table designer.
