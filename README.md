# DataPortStudio

A lightweight, Navicat-style database manager for **SQL Server**, **SQLite**, **MySQL**,
**MariaDB**, **Firebird**, **Oracle**, **MongoDB**, **Excel (.xls/.xlsx)** and **Clarion TPS / DAT** files, built with **C# / WPF (.NET 9)**.

Add connection strings, browse the server tree (databases → schemas → tables), open a table, and
view & edit its records in place — including adding and deleting rows — with changes pushed back to
the database.

![Status](https://img.shields.io/badge/status-v1.0.13-blue) ![Platform](https://img.shields.io/badge/platform-Windows-informational) ![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)

## Download

Grab the latest release from the
[**Releases**](https://github.com/robertorenz/DataPortStudio/releases/latest) page.

**Option 1 — Installer** (`DataPortStudio-1.0.13-Setup.exe`, ~84 MB)
- Installs to `Program Files`, creates a Start Menu entry, and adds an optional Desktop shortcut.
- Includes a proper uninstaller (Add/Remove Programs).

**Option 2 — Portable exe** (`DataPortStudio.exe`, ~85 MB)
- No install needed. Download and double-click — runs from any folder.
- Native DLLs are bundled inside the exe and extracted to a temp cache on first run; subsequent launches are fast.
- If native DLLs are missing (e.g. after some antivirus quarantines them), use the installer instead.

Both options are **self-contained** — no .NET runtime install required.

## Features

- **Multiple database engines** — pick the engine when creating a connection. Each connection is
  tagged with an engine icon in the tree so you can tell them apart at a glance.
  - **SQL Server**, **SQLite**, **MySQL**, **MariaDB**, **Firebird**, **Oracle**, **MongoDB** and
    **Clarion TPS / DAT** files are supported today; **PostgreSQL** is selectable now and being wired up next.
  - **SQLite**: just point to a `.db` / `.sqlite` file — browse tables & views, view and edit rows
    (primary-key or rowid-safe), **design tables** (create new, or alter existing via a safe
    table-rebuild), and inspect structure/DDL.
  - **MongoDB**: connect with a connection string (`mongodb://…` or `mongodb+srv://…`) and browse
    **databases → collections → documents**. Documents are flattened into a **read-only** grid
    (top-level fields become columns; nested objects/arrays show as JSON), with filter, sort and
    export. The structure panel samples the collection and lists its fields & types.
  - **MySQL** / **MariaDB**: connect to a server (host / port / database / user / password) — browse
    **databases → tables / views / functions / procedures**, view and edit rows (primary-key keyed,
    `LIMIT`-paged), inspect structure (Info, the `SHOW CREATE TABLE` DDL, and foreign-key
    relationships), run ad-hoc SQL in the query window, build joins visually, and copy/paste tables
    (`CREATE TABLE … LIKE` + `INSERT … SELECT`). Both engines share the same high-performance
    driver, with backtick (`` ` ``) identifier quoting.
  - **Firebird**: connect to a server (host / port / database path or alias / user / password) —
    browse tables & views, view and edit rows (primary-key keyed), and inspect structure (Info,
    a reconstructed DDL, and foreign-key relationships). Tick **Embedded (no server)** to open a
    local `.fdb` directly — this needs Firebird's native engine DLLs (`fbclient.dll` + `plugins\`,
    `intl\`, `tzdata\`, `firebird.conf`/`firebird.msg`, `ib_util.dll`) from the official Firebird
    **ZIP kit** (64-bit) placed next to `DataPortStudio.exe`.
  - **Oracle**: connect with host / port / **service name** / user / password (Easy Connect). Browse
    your schema's **tables & views**, **view and edit rows** (primary-key or all-columns keyed),
    inspect structure (Info, reconstructed DDL, indexes), run SQL in the query window, drop tables, and
    copy tables to/from any SQL database. Identifiers are double-quoted; uses Oracle's bundled managed
    driver. (The visual query builder isn't wired for Oracle yet — use the query window.)
  - **Clarion TPS**: point a connection at a **folder** and every `.tps` file in it shows up as a
    table — pick one like you'd pick a table. Selecting the connection lists its files (with size and
    date) in the **Objects** tab. DataPortStudio decodes the TopSpeed binary format directly
    (no ODBC driver, no install) into an **editable** grid with filter, sort and export, and detects
    Clarion `LONG` date/time fields automatically. **Cell edits are written back to the `.tps` binary
    file** (UPDATE only — adding/deleting rows is not supported, as those require rebuilding the TPS
    index files). Use **Copy** on a `.tps` table and paste it onto any SQL database to migrate the
    data across (**TPS → SQL**); the schema and rows are created for you.
  - **Excel (.xls / .xlsx)**: point a connection at a **folder** and every worksheet in every Excel
    file in it shows up as a table. First row = column headers; empty rows are skipped; all values
    are read and written as text. **Fully editable** — add rows, edit cells, delete rows, and
    **Save** writes the changes back to the worksheet (replaces the sheet's data rows; header row
    and other sheets are untouched). Use **Copy** to migrate a sheet into any SQL database.
    `.xlsx`/`.xlsm` use ClosedXML; `.xls` uses NPOI. No extra install needed.
  - **Clarion DAT**: the *classic* Clarion ISAM format (pre-TopSpeed, `.dat`). Same folder model —
    point at a folder and each `.dat` file is a table. DataPortStudio decodes the format from its public
    spec (Clarion Technical Bulletin 117): header, field descriptors and fixed-length records,
    including packed-BCD `DECIMAL` fields, with dates/times surfaced as Clarion `LONG`s. Read-only,
    and a copy *source* into SQL just like TPS. (Keys/indexes in `.K??`/`.I??` files and `.MEM`
    memos aren't read.)
- **Connection manager** — add, edit, and remove connections.
  - SQL Server: Windows Authentication or SQL Server login.
  - Field-based builder *or* a raw connection string.
  - **Test Connection** before saving.
  - Connections are saved to `%AppData%\DataPortStudio\connections.json`; passwords are
    encrypted at rest with **Windows DPAPI** (current-user scope).
- **Object tree** — lazily loads databases, schemas, and tables, color-coded by type.
- **Tabbed data view** — double-click a table to open it in its own tab (configurable row
  limit). Re-opening a table just switches to its existing tab. Tabs flag unsaved changes
  and can be closed individually.
  - **In-place editing** of cells.
  - **Add** new rows and **delete** rows.
  - **Save changes** writes everything back. Tables **without a primary key** are supported too —
    pick a **row identity** (the columns that identify a row) for safe updates and deletes.
  - **Filter** and multi-column **Sort** builders (Navicat-style) per tab.
  - **Spreadsheet-friendly copy & paste** — select rows/cells and **Ctrl+C** (or right-click →
    Copy / Copy with headers); pastes cleanly into Excel/Sheets with each value in its own cell.
    **Ctrl+V** pastes a block back into the grid from the top-left of the selection (adding rows
    past the end). A single clipboard value, or typing into one cell, fills **all selected
    cells**. Clarion date/time text is parsed back to the stored integer.
  - **Cell View panel** — a resizable panel below the grid that shows the full content of the
    selected cell, with a **View** drop button to render it as **Text** (editable), **Hex**,
    **Image**, or **Web** (HTML via WebView2). **Auto-detect** picks the best mode. Rows are
    capped to a single line in the grid so long values don't blow up row height.
- **Clarion date & time support** — automatically detects integer columns that hold
  [Clarion Standard Dates/Times](#clarion-dates--times) and displays them as real dates (📅)
  and times (🕒), while keeping them editable. Toggle per tab.
- **Object tree (Navicat-style)** — Server → Database → Schema → **Tables / Views / Functions /
  Procedures** folders, with a **filter/locator** box to jump to a table by name. Open tables or
  views to browse rows. For SQL Server, set a connection's **default database** to collapse the
  tree to *connection → schema → Tables* (the database level is skipped); leave it blank to browse
  all databases on the server.
- **Command bar** — a Navicat-style toolbar across the top: **Connection**, **New Query**, and
  **Table / View / Function** buttons that jump straight to the current database's tables, views or
  functions section, plus **Refresh**.
- **Objects tab** — click a **database**, a **schema**, or a **Tables** folder (or a MongoDB
  database) and its tables appear in a persistent **Objects** tab, Navicat-style: **Name, Schema,
  Rows, Modified Date, Comment**, with a toolbar (Open / Design / New / Delete / Refresh).
  Double-click a row to open the table in its own tab — the Objects tab stays put as the first tab
  instead of overlaying your open tables. Clicking a SQL Server database lists every table across
  its schemas.
- **Structure inspector** — a dockable/pinnable side panel showing **Info** (connection, database,
  schema, OID/object_id, rows, created/modified dates, comment, columns), **DDL** (`CREATE TABLE` +
  indexes + foreign keys), and **Relationships**.
- **SQL query window** — **New Query** opens a window to run arbitrary SQL (Run / F5) with a
  results grid, row counts and timing.
- **Visual query designer** — compose SELECTs visually (tables, columns, joins with FK
  auto-detect, filters, sort) with live SQL. Works with SQL Server, SQLite, MySQL/MariaDB and
  Firebird; open it from the **Visual Designer** button on the command bar.
- **Export** — export the current grid to **DBase (.dbf), Text (.txt), CSV, TSV, HTML,
  Excel 97-2003 (.xls), Excel (.xlsx), SQL script (INSERTs), XML or JSON**, with per-column
  selection and an optional header row. When a **filter** is active you can choose **all rows** or
  **just the filtered rows**. **JSON** has extra options: legacy `{"RECORDS":[…]}` wrapper,
  date order (DMY/MDY/YMD) with custom date/time delimiters, zero-padding, a custom decimal symbol,
  and **Base64 or hex** binary encoding.
- **Import data** — load a **CSV or Excel (.xlsx)** file into a table, with first-row-header
  detection and a source-to-column mapping that auto-maps by name. The whole import runs in a
  single transaction (all-or-nothing).
- **Generate INSERT script** — right-click a table to produce a ready-to-run `INSERT` script for
  its rows (wrapped in `SET IDENTITY_INSERT` when needed), viewable, copyable and savable as `.sql`.
- **Copy & paste a table** — right-click a table → **Copy** (or Ctrl+C in the tree), then **Paste**
  (Ctrl+V) onto a Tables folder / database. It asks whether to copy the **structure only** or
  **structure + data**, and auto-names the copy (`name_copy`, `name_copy2`, …) if the name is taken.
  Paste **into the same connection**, **a different connection of the same engine**, or even
  **a different relational engine** (e.g. SQLite → SQL Server, Firebird → SQLite), or migrate a
  **Clarion TPS / DAT file into any SQL database** (Clarion → SQL): DataPortStudio maps each column to a
  compatible type, creates the table, and copies the rows. Cross-connection SQL Server copies use
  **SqlBulkCopy** (streaming) so large tables move fast. (MongoDB ↔ relational isn't supported —
  documents and tables aren't interchangeable; Clarion files are read-only, so they're copy sources
  only.)
  - **Review & tweak the column types** — when copying a Clarion TPS/DAT file into SQL, a dialog
    shows every column and its Clarion type next to the proposed **SQL type**, chosen from an
    engine-specific **dropdown** plus a **size / precision** box (length for text, `precision,scale`
    for decimal). Adjust anything before the table is created, or **Reset to suggested**.
  - **Clarion date/time → real SQL `date`/`time`** — Clarion stores dates and times as `LONG`
    integers. DataPortStudio auto-detects those columns and pre-maps them to SQL `date` / `time`, and the
    copy **converts the value** (Clarion Standard Date/Time → a real `DateTime`/`TimeSpan`) instead
    of dumping the raw number. Don't want the conversion? Just set the column back to `int` in the
    dialog.
- **Users & roles** — a **User Manager** (command-bar **Users** button) to manage logins, users and
  roles across the relational engines:
  - **List** users and roles, **create** / **drop** them, **set/change passwords**, and
    **lock/unlock** accounts.
  - **Role membership** — tick the roles a principal belongs to (granted/revoked on save).
  - **Privilege editor** — a checkbox grid to **grant/revoke** privileges, scoped **globally** or
    **per database** (with *Grant all* / *Revoke all*).
  - **MySQL/MariaDB**: `user@host` accounts, global (`*.*`) and per-database privileges, MySQL 8 &
    MariaDB roles. **SQL Server**: server logins & roles, server-level permissions, and
    per-database permissions (granted to the mapped database user). **Firebird**: server-level
    users and role membership (per-table grants stay in the query window).
- **Table designer** — create and alter tables: columns, types, nullability, defaults, primary
  key and indexes, with a copyable generated script.
- **Edit routines & views** — open and edit **functions, stored procedures and views**; create
  new ones from templates; execute or drop them.
- **Safe drop** — dropping a table/view/routine first checks `sys.sql_expression_dependencies`
  and warns you about objects that reference it.
- **Localization** — the interface is fully translatable; ships with **English and Spanish**.
  Switch language in **Settings** and the UI updates live (no restart).
- **Built-in User Guide** — **Help ▸ User Guide** opens full documentation inside the app (covering
  connections, every engine, viewing/editing, queries, copy &amp; the Clarion → SQL mapping, export,
  users and more), in **English or Spanish** to match the selected language.
- **Settings** — defaults for row limit, which panels open with a table, and the UI language.
- **Adaptive toolbar** — the per-table command bar is responsive: as the window narrows, button
  labels collapse to icons (with tooltips), and any buttons that still don't fit move into a
  **»** overflow menu — so the toolbar stays usable at any size.
- **Professional UI** — clean blue/slate theme, dark sidebar, button icons, styled modal dialogs.

## Clarion dates & times

Many Clarion-prepared tables store dates and times as integers:

- **Date** — the **number of days since December 28, 1800** (so `4` = 1801‑01‑01).
- **Time** — the **number of centiseconds since midnight, plus one** (so `1` = 00:00:00,
  `8,640,001` = 24:00:00).

DataPortStudio detects these columns heuristically and shows them as `yyyy-MM-dd` dates (📅) and
`HH:mm:ss` times (🕒) on the column header. Dates are detected by name and/or value range;
times are detected mainly by name (their value range overlaps too much ordinary data to
trust values alone). Editing a converted cell accepts a normal date/time and writes the
correct integer back to the database.

Detection recognizes English and Spanish name hints (e.g. `FECHA` → date, `HORA` → time).
Use the **Clarion dates/times** checkbox in a table's toolbar to toggle all conversions on/off,
or **right-click any column header** to force it to **date**, **time**, or **plain number**, or
back to **auto-detect**. Empty values (stored as `0`) display as blank.

## Requirements

- Windows
- [.NET 9 SDK](https://dotnet.microsoft.com/download) (to build) or the .NET 9 Desktop Runtime (to run)
- Network access to a SQL Server instance

## Getting started

```powershell
# Build
dotnet build

# Run
dotnet run --project DataPortStudio.csproj
```

Then:

1. Click **New Connection**, fill in your server (e.g. `localhost\SQLEXPRESS`), choose
   authentication, and **Test** it.
2. Expand the connection in the tree to browse **databases → schemas → tables**.
3. **Double-click a table** to load its records.
4. Edit cells directly, add or delete rows, then click **Save changes**.

> In-place editing uses the table's **primary key** to generate updates and deletes. For tables
> without one, use the **Row identity…** picker in the toolbar to choose the identifying columns.

## Tech stack

| Concern            | Choice                                |
|--------------------|---------------------------------------|
| UI                 | WPF (.NET 9), MVVM (CommunityToolkit) |
| SQL Server access  | `Microsoft.Data.SqlClient`            |
| Editable grid      | `DataTable` + `SqlDataAdapter` + `SqlCommandBuilder` |
| Password storage   | DPAPI (`System.Security.Cryptography.ProtectedData`) |

## Project layout

```
Models/      ConnectionProfile
Services/    SqlServerService, EditableTableSession, ConnectionStore
ViewModels/  MainViewModel, DbTreeNode
Views/       ConnectionDialog, ModalDialog, Dialogs
Converters/  Tree icon / color converters
Themes/      Theme.xaml (palette + control styles)
```

## Roadmap ideas

- Ad-hoc SQL query editor with results grid
- Filtering / sorting / paging on large tables
- Export (CSV / JSON), and view table structure (columns, indexes, keys)
- Support for other engines (PostgreSQL)
