# Changelog

Notable changes in each RunPad release. Downloads are on the
[releases page](https://github.com/campforge/run-pad/releases).

## [Unreleased]

### Added

- **Toggle Line Comment** (Ctrl+/, Cmd+/ on macOS) comments or uncomments every line the selection
  touches, or the caret's line, with `//` in C# and `'` in Visual Basic. It is one undo step, and its
  key can be changed in Preferences → Keyboard.

### Changed

- Typing `{` in C# no longer inserts `}`. Instead, pressing Enter in a new, still empty class,
  struct, record or interface body offers a property template: Tab inserts it, any other key leaves
  the code as it is.
- The `prop` and `propg` snippets have fields: after inserting one, Tab moves from the type to the
  name, and Enter or Escape leaves the snippet.
- **Wrap long lines in results** moved from Preferences to the gear menu beside the output tabs,
  now called **Output settings**, next to the exception break options.
- In Tools → Connections, the test status under a connection shows the whole test result as a
  tooltip, so a status cut short in a narrow list can still be read.

### Fixed

- With result wrapping on, ordinary output and errors wrap at the pane width too, long unbroken text
  included, so no horizontal scroll bar into empty space is left; spaces in output are kept.
- The classes RunPad generates for a gRPC connection use a nullable-annotations-only context
  (`#nullable enable annotations`), so they keep their nullable annotations without reporting
  nullable warnings of their own — CS8669 and the warnings generated code shouldn't raise.
  Descriptions cached by an earlier RunPad are read again, so scripts that already have one get the
  new directive too. RunPad's own connection wrapper is unchanged.

## [0.9.5] — 2026-10-24

### Changed

- C# completion now suggests values that fit method and constructor arguments and object initializer
  properties, including compatible variables, enum members and common literals.
- Adding a connection that needs NuGet packages now asks for confirmation in a separate dialog.
- In `--compile-only` mode, warnings and errors go to stderr, leaving stdout free of diagnostics.

### Fixed

- The Help menu and its separator follow the active theme.
- UTF-32 big-endian files with a BOM retain their encoding when opened and saved.
- A failed run keeps the IL from the last successful build available until another build succeeds.
- gRPC connection testing works with reflection servers that ignore an empty service-list request,
  including the Postman Echo server, and explains common HTTP/2 and TLS mismatches.
- Generated gRPC clients no longer show nullable-context warnings from their generated code.
- Closing Dependencies while a connection confirmation is pending cannot add the connection later.

## [0.9.4] — 2026-10-23

### Added

- Assembly Explorer now keeps large assembly trees responsive while loading and highlights IL in
  the code pane and the script editor's IL tab.
- Dependencies now shows package dependencies, license information, and the project or homepage
  link when NuGet supplies them.
- Dependencies can create and add connections directly, with a confirmation that lists any new
  NuGet packages and versions before changing the script.
- Script launch progress now identifies restore, connection contract generation, compilation, and
  execution stages, and reports which stage failed.
- Dependencies → Connections has **Refresh** for a gRPC connection: it reads the server's services
  again after they change, and open scripts that use the connection get the new classes at once.
  The command-line runner does the same with `--refresh-connections`.

### Changed

- gRPC connection schemas and IntelliSense context updates no longer block the editor while they
  are being prepared; stale completion and analysis results are discarded when newer code or
  connection context is available.

### Fixed

- Saving the Secret Store while another RunPad window saves it, or while a running script reads it,
  now waits a moment and tries again; if the store stays in use, the window says so instead of asking
  to check access and disk space.
- A Secret Store file that is a symbolic link stays one when saved: the file it points to is updated.
- A temporary file left by a Secret Store save that was interrupted is removed by the next save.
- In Tools → Connections, editing a connection while Test Connection is still waiting no longer
  shows the answer for the old values afterwards, nor reads a gRPC server's services for values that
  were never tested.
- Saving a script keeps front matter settings RunPad doesn't know intact in more cases: a block of
  text marked to keep its final empty lines (`|+`) keeps them, and a YAML alias (`*name`) of another
  setting is saved as the value it stood for instead of a dangling reference. A script whose
  settings use such an alias, including in its connections, now opens instead of being reported as
  invalid.
- The startup notice about drafts kept unchanged no longer says their data couldn't be read when it
  could, and only the session that had taken them over can't be checked now; it says so instead.

## [0.9.3] — 2026-10-23

### Changed

- Long lines in results now wrap at the window width, including long unbroken text, with a new
  "Wrap long lines in results" setting under Editor preferences; the existing editor setting is
  renamed "Wrap long lines in code editor" and only affects the code.
- `.Dump()` no longer cuts a string off after 500 characters: the whole value is shown.
- Adding a connection in Dependencies that needs new NuGet packages now asks for confirmation,
  listing the packages and versions, before writing them to the script.

### Fixed

- Closing the Connections or Secret Store window with Cancel, Escape or the close button now asks
  whether to save unsaved edits instead of silently discarding them.
- The cached description RunPad reads for a gRPC connection (its services) no longer accumulates
  forever on disk for connections removed, renamed or changed: unused files older than 30 days are
  reclaimed at startup.
- A Program script whose first lines are `using` directives — including the one RunPad writes when
  switching a script from Statements to Program — now compiles instead of failing to build.
- A Program script whose entry point is a `static Main` no longer shows a spurious "entry point of
  the program is global code" warning in build results or the output panel. RunPad's generated
  type name `__RunPadProgram` is now reserved in C# scripts; the synthetic method name
  `__RunPadRun` is reserved in Program scripts. Declaring either produces a clear diagnostic.
- Build errors and warnings now point at the matching line of your code. They used to be off by
  the lines RunPad adds around it (front-matter namespaces, the generated Program wrapper); a
  message about RunPad's generated text now has no line number instead of a wrong one.

## [0.9.2] — 2026-10-21

### Added

- A startup screen appears while RunPad checks for the .NET SDK and debugger before opening
  the workspace.
- C# and Visual Basic scripts define the `RUNPAD` preprocessor symbol, so source can conditionally
  include RunPad-specific code.

### Changed

- Breakpoint conditions are edited in a dialog centered on the window; Escape cancels the edit.
- Go to Line uses a compact dialog with rounded corners and a themed border.

### Fixed

- Startup failures keep a working Close button, and quitting during SDK checks does not resume startup.
- Borderless dialogs use an opaque themed background when window transparency is unavailable.

## [0.9.1] — 2026-10-21

Bug fixes for breakpoints, indentation and large gRPC contracts.

### Fixed

- Breakpoint conditions now open in a small popover next to the breakpoint, with C# validation
  and suggestions for names and members in scope. An invalid condition cannot be applied; Escape
  or clicking outside cancels the edit.
- Changes to a breakpoint's condition or enabled state now reach a running debug session.
- Control-click in the breakpoint margin on macOS opens the condition editor instead of toggling
  the breakpoint.
- Editor preferences now control tabs versus spaces and indentation width consistently for typing,
  automatic indentation and C# formatting, and one editor's settings no longer change another's.
- Cached runs restore missing or changed runtime dependency files before starting the script.
- Scripts with large gRPC connections stay responsive: the generated messages and clients compile
  once into a cached assembly that is reused when only the script changes, and IntelliSense uses
  it after the first build. Changing the target or dependencies rebuilds it.
- Connection schemas that have not changed are no longer re-read on every editor refresh, and a
  replaced schema file is picked up.
- The script's IL viewer stays responsive on large disassemblies: it shows only the visible lines
  and builds its text in the background.

## [0.9.0] — 2026-10-20

Connections and secrets: keep your HTTP APIs, gRPC servers and SQLite databases in one place,
give a script a typed client for each, and keep passwords and tokens out of scripts.

### Added

- Tools → Secret Store keeps passwords, tokens and keys out of scripts: save them by name and read
  them with `Util.GetSecret("name")`. The store belongs to you on this computer, encrypted for
  your account on Windows and readable only by you on macOS and Linux; values stay hidden until
  you choose Show. A missing secret fails with a message naming it and the store, and a store
  changed in another window is never overwritten.

- Connections: Tools → Connections keeps your own HTTP APIs, gRPC servers and SQLite databases in
  one window, with a form for each kind and a Test Connection button. Passwords and tokens are
  written as `${secret:name}` (from the Secret Store) and other values as `${env:NAME}`, so the
  connection itself holds no secret.

- Dependencies → Connections adds your connections to a C# script, which keeps its own copy under
  `connections:` in its front matter and gets a client for each in its code, with completion as
  soon as the change is saved; Import to Your Connections keeps a script's connection, for
  example one from a shared script. The libraries a connection needs are added to the script's
  packages, like any other package. The command-line runner builds connections too.
  - An HTTP connection is an `HttpClient` with its base address, headers and timeout set:
    `Connections.Billing.GetStringAsync("invoices")`.
  - A gRPC connection has a client for each of the server's services, such as
    `Connections.Inventory.Stock.GetItemAsync(…)`, with the messages' classes. RunPad reads the
    services through gRPC server reflection and writes the classes with `protoc` from the
    Grpc.Tools package; Test Connection reads them again after the server changes.
  - A SQLite connection names a database file, relative to the script's folder or a full path,
    and is an Entity Framework Core context with a set for each table and view, such as
    `Connections.Shop.Customers.Where(c => c.City == "Oslo")`, saved with `SaveChanges()`. Views
    and tables without a usable primary key are read only, and the whole file can be opened read
    only.

  Secrets stay out of scripts and their files: a script reads them when it runs, and RunPad reads
  them only to test a connection or to read a gRPC server's services. A connection that can't be
  read fails the build with the reason, which the Connections tab shows too.

- C# scripts can allow unsafe code (pointers, `sizeof` of your own structs) with "Allow unsafe
  code" on the Dependencies window's Compilation tab, saved as `allowUnsafe: true`. It is off
  unless a script turns it on, also for imported LINQPad queries. It is a compiler setting, not a
  sandbox.

- Execute Selection also runs selected statements in Program scripts, such as lines inside
  `Main`, on their own. A selection that uses a method or variable declared outside it explains
  the compile error instead of running the whole program.

### Changed

- With draft recovery on, closing RunPad with unsaved tabs asks once: Save All (the usual Save and
  Save As, including the question about files changed on disk), Keep in Stash (close without
  writing the files; the changes reopen next time) or Cancel. A cancelled or failed save, or a
  stash that could not be written, keeps RunPad open.

- Starting RunPad restores unsaved drafts without a message box, including drafts of another
  RunPad window that was closed meanwhile. A draft of a file that is already open comes back as an
  untitled copy naming the file, so neither version is overwritten: it keeps finding the file's
  relative references and can't be saved over that file. Draft data that cannot be read is kept
  unchanged and named in a dismissible notice; sessions with only saved files no longer cause a
  warning on every start.

- Saving a script keeps front matter keys this version of RunPad doesn't know, such as settings
  from a newer RunPad, instead of dropping them.

- A Program script written as a classic C# program (a class with a `static Main`, with using
  directives and namespaces) runs as written, as in LINQPad; scripts with a LINQPad-style `Main`
  are unchanged. A missing or ambiguous entry point is reported by the compiler.

- Settings windows have Save and Cancel buttons that follow the selected theme, and the exit
  dialog lists unsaved tabs on separate lines.

### Fixed

- Packages with native libraries (such as SQLite's) work in scripts on Linux with a .NET built by
  the distribution (for example Ubuntu's `dotnet` packages), which reports its own runtime
  identifier: the libraries for the portable one (`linux-x64`) are now placed next to the script.

- C# Program scripts can declare extension methods and custom awaiters alongside Main, as in
  LINQPad: static classes are compiled outside the generated class, other types stay nested in it,
  so a type an extension uses (such as its receiver) must be internal or public. The editor keeps
  navigation, diagnostics and code actions aligned with the original script.

- Program scripts choose and invoke their entry point using the selected framework's conditional
  compilation symbols, including `DEBUG` and `TRACE`.

- A C# Statements script with nothing to run (only comments, using directives or preprocessor
  lines, like many LINQPad readme queries) runs and finishes without output instead of failing
  with CS5001. Syntax errors and scripts with only declarations still fail as before.

- Per-script C# imports work with global using directives in the source, in both compilation
  and editor diagnostics.

- Importing C# LINQPad queries keeps LINQPad's standard namespace imports alongside the query's
  own, so names such as StringBuilder and XDocument resolve without editing code.

## [0.8.0] — 2026-10-19

### Added

- Run > Execute Selection runs selected Statements without saving the script. Stop and Run Again
  waits for the previous run to finish cancelling before starting a fresh run. Both commands have
  configurable shortcuts.

- Draft recovery can keep open tabs and their unsaved text locally on normal exit, including
  script arguments and references, without writing those changes to the original files.

- File > Open (Ctrl+O, or Cmd+O on macOS) opens .rpad scripts from any folder. Recent Files keeps
  the last 50 paths across restarts and can be cleared without deleting files. Opening an already
  open script activates its tab and preserves unsaved edits.
- A Markdown preview of the script, as in VS Code: View → Markdown Preview (Ctrl+Shift+V) shows the
  whole document in place of the editor, and View → Markdown Preview to the Side (Ctrl+Alt+V) beside
  it; a button on the tab's toolbar shows or hides it. The header is shown as a table of settings,
  the text around the code as formatted Markdown and the code highlighted in the editor's colors;
  the preview follows the text as you type. Side by side, the editor and the preview scroll together.
- An open script follows its file when another program changes it. A tab without unsaved changes
  reloads by itself when you come back to RunPad; one with unsaved changes asks whether to reload or
  keep your version, and doesn't ask again about the same change. A reload can be undone with Undo.
  File → Revert to Saved goes back to the script as saved on disk, asking first if that drops
  unsaved changes.

### Fixed

- Match feedback remains visible in compact search. Recovery warnings ignore other sessions with
  empty workspaces while retaining warnings about unreadable draft data.

- Execute Selection excludes unselected fragments of rectangular selections. Narrow search panels
  keep navigation and replacement actions accessible; flyout text follows the interface palette.
- Recovery skips damaged sessions when an older readable session exists, reports other retained
  sessions, and blocks native-menu actions during startup restoration.

- Session recovery waits for pending Save As and file moves before closing, preserves untitled
  tab titles, and falls back to the previous checkpoint when the latest snapshot is damaged.

- Search and replacement shortcuts can be changed or disabled in Preferences, and their button
  hints follow the assigned keys. Assembly Explorer navigation hints follow your keys too.

- Selected result text can be copied with the system keyboard shortcut, including Cmd+C on
  macOS; Select All also follows the platform shortcut.

- Markdown preview mode and editor, output and debugger split positions are shared across tabs,
  including newly opened tabs; toggling preview keeps the chosen split.

- Opening or reloading scripts preserves literal `${0}` text. Reload and Revert to Saved leave
  tabs alone if a run or save starts, or their saved version changes, while a dialog is open.

- Opening a script no longer fails while Recent Files refreshes the native macOS menu.
- Input sent to a script handles failures after Stop or process exit without leaving an unobserved
  task error. Hidden input fields are cleared, and late submissions cannot change a newer prompt.

## [0.7.0] — 2026-10-18

### Added

- A script's command-line arguments can be set with `args:` in its header. The script reads them
  from `args` — in C# scripts, in `Main(string[] args)` and in Visual Basic scripts of statements —
  when it runs in RunPad, under the debugger and from the `runpad` command-line runner.
- `runpad` understands the command lines of LINQPad's `lprun` and NetPad's `npad`: `runpad script.rpad
  Hello World` passes `Hello` and `World` to the script, `run` may come first, and
  `-lang=e`/`--kind expression`, `-s`/`--sdk 9` and `-compileonly` override the script's kind and SDK
  or only compile it. These options are read even after the script, up to `--`; everything after
  `--` goes to the script as it is. A missing or unrecognized `--sdk` or `--kind` value stops the run
  with an error, and options it can't handle yet, such as `-format` or `-e`, are reported instead of
  being passed to the script.

### Changed

- Search uses a compact editor-wide strip with replacement on a second row. Search fields,
  quick-fix menus, tooltips and flyouts now follow the selected interface palette.

- `runpad` exits with code 2 instead of 1 when the script did not run — a bad command line or
  file, no .NET SDK, or a compile error — the same code as NetPad's `npad`.

### Fixed

- Closing a running script no longer faults while cancellation finishes; unexpected run and stop
  errors are logged and reported without escaping the editor action.

- In the installed RunPad app, the editor no longer marks `.Dump()` as an error once it has loaded the
  script's .NET target, while the script itself runs fine.
- Switching the theme recolours keywords, strings and semantic colours in every open script and code
  window: at once in the one shown, and in other tabs when they are next shown. A tab opened only
  after the switch no longer appears in the previous theme's colours.
- The unsaved-changes dialog separates the file name from its explanation and gives Save, Cancel
  and Don’t Save clearer, consistent actions in light and dark themes.

- Fixed a startup crash in self-contained macOS builds when framework assemblies appeared more
  than once while opening a script or selecting its .NET target.

- Saving a script preserves reference paths containing YAML punctuation, whitespace and Unicode
  characters instead of corrupting the file or changing the path.

- Editor diagnostics and refactorings use the selected .NET target's C# version and conditional
  compilation symbols, matching compilation when switching targets, and update as soon as another
  SDK is selected instead of after the next edit.
- The editor no longer reports errors for code completed by the .NET source generators, such as
  `[GeneratedRegex]` methods and System.Text.Json serializer contexts, and completes their members.
  The errors and warnings those generators report themselves, such as an invalid regex pattern, are
  now shown in the editor and listed with their line in the build output.
- In Program-kind scripts, lightbulb actions such as Add braces, Add accessibility modifiers or Wrap
  expression no longer indent the code they change one level too deep.
- A cancelled analysis no longer leaves cached lightbulb actions from an earlier edit.
- Saving no longer silently replaces a script that another program changed after RunPad opened or
  last saved it. RunPad asks whether to save under another name, overwrite the file or cancel, also
  when changing the script's settings, running or debugging it, closing, quitting, or renaming,
  moving or deleting it; a script that wasn't saved that way doesn't run.
- A script's relative `references:` are found next to the script itself, the same way when running
  in RunPad, with the `runpad` command and in the editor, which now also knows their types. Paths
  are saved with `/` on every system, Windows paths are still read, and a reference that can't be
  used here (a Windows path on macOS or Linux, `C:lib/...`, or a relative path in an unsaved script)
  is reported instead of silently ignored. Save As into another folder keeps relative references
  pointing at the same files.

## [0.6.0] — 2026-10-17

### Added

- **Preferences → Interface → Interface scale** makes everything in RunPad's windows larger or
  smaller — text, icons, spacing and click targets of the panels, tabs, results, debugger, dialogs,
  menus, tooltips and code completion, and the code itself. Choose 90% to 200%; **Reset to 100%**
  brings back the usual size. The menu bar at the top of the screen on macOS stays as the system
  draws it.

### Changed

- The debugger keeps Threads and Call Stack in permanent tables with compact text headings and
  a draggable divider, beside Locals and Watch.
  An always-visible settings button opens exception break options. Panel views use soft
  theme-defined selectors. Watch actions use compact plus/minus buttons with tooltips; the settings
  icon has a trailing inset.
- The status bar uses a neutral theme background and readable text, with a small coloured state
  indicator. Light themes use darker status colours. Scripts that have not run yet show Ready
  with neutral grey text and indicator.

- Keyboard focus now uses one rounded theme-defined ring across buttons, tabs, dropdowns, trees
  and text fields. Each theme controls its colours, thickness and corner radius independently
  of selection colours; primary buttons no longer add a second focus border.

- Editor preferences are grouped into font and zoom, display, editing, and new-file defaults,
  with aligned fields and the same settings and keyboard behavior.

- Code zoom (**View → Zoom In/Out**, Ctrl+wheel or Cmd+wheel on macOS, and the percentage in the
  status bar) is now one setting for all editors: every open tab and every tab opened later shows
  code at the same zoom, and it is kept when RunPad restarts. It works on top of the interface
  scale — 100% is the interface-scaled font size — and the wheel moves through the same steps as
  the menu. **Preferences → Editor → Code zoom** shows it and resets it on its own. Files opened in
  their own editor window now use the editor font, size and zoom too.
- In dialogs, the button that **Enter** presses (OK, Save, Rename…) is now shown in the theme's
  accent colour, with a visible ring when it has keyboard focus, so the main action stands out;
  **Enter** and **Escape** work as before. The dividers between the side panel, the editor and the
  Results panel are now thin lines; they are dragged in the same place as before.

### Fixed

- IntelliSense checks framework APIs against the script's selected .NET target when its reference
  pack is available, including when a script without packages is first opened, before any run.

- IntelliSense no longer reports duplicate-type errors when a NuGet package includes an assembly
  also provided by the framework; package updates use the same overlap selection as compilation.

- Editor diagnostics, quick fixes and refactoring titles stay in English, matching the interface,
  instead of following the operating system language.

- Panel dividers now meet their adjacent panels without empty bands on either side of the line,
  including in Preferences. The invisible drag target still extends over both panel edges.
- Debugger tabs wrap when the panel is narrow, keeping every tab visible and clickable.

- **Queries** no longer lists folders whose names start with a dot, such as `.obsidian` or `.git`,
  or anything inside them, so a queries folder that is also an Obsidian vault or a Git repository
  shows only your scripts. Folders and files on disk are not touched.
- **Queries** now updates when a folder is created, renamed, moved in or deleted outside RunPad —
  in Finder, a terminal or Obsidian — also for folder names with a dot, such as `project.v2`. Before,
  only changes to `.rpad` files were noticed. Changes inside dot folders such as `.obsidian` and
  to other kinds of files no longer cause a refresh.
- Easier to read: the hit-count hint of the breakpoint condition dialog wraps instead of being
  cut off, a long version in **About RunPad** ends with "…" and shows in full as a tooltip, error
  messages name what failed and show the details in normal text instead of all red, and grey
  secondary text is a little darker in the VS Code, Rider, Visual Studio and RunPad Warm light
  themes so that it stays readable. Selected items in Rider Light, RunPad Midnight and RunPad Warm
  Light are easier to read as well.

## [0.5.0] — 2026-10-15

### Added

- A Program script's `Main` can take the command-line arguments and a cancellation token —
  `Main(string[] args)`, `Main(CancellationToken ct)` or `Main(string[] args, CancellationToken ct)` —
  and can return `int` or `Task<int>` as the exit code. With several `Main` overloads, the one that
  takes the most is called.
- `Util.CancellationToken`, cancelled when you press **Stop**. A script that uses it — or a `Main`
  that takes a token — gets up to 3 seconds to close files, connections and child processes before
  RunPad ends it; other scripts still stop at once.
- A new Program script starts with a `Main` method, with the caret inside it. Switching a script
  to **Program** wraps its code in `Main` — `async Task Main()` when the code awaits — and keeps
  methods and types after it; one **Undo** brings the old code back. Visual Basic scripts get a
  `Module` with `Sub Main()`.
- An **Examples** tab next to **Queries** in the panel on the left, with sample scripts that come
  with RunPad. Clicking one opens it as a new, unsaved script: it does not run, and nothing is
  written until you save it, which asks where to put the copy.

### Changed

- A script runs on the .NET version its `targetSdk` names, in the RunPad window and the
  command-line runner alike. The toolbar's SDK list shows it — **latest (.NET 10.0.100)** keeps
  `targetSdk: latest`, any other entry writes its version — and a version that isn't installed runs on
  the newest SDK with a warning in Results and *(not installed)* in the status bar. Before, `targetSdk`
  was only shown and the newest SDK always ran. New scripts get `targetSdk: latest`.
- The File menu's import command is now called **Import .linq Query...**.
- A new Statements or Expression script created from the file tree starts empty, like one created
  with **File → New File**.
- The editor's gutter is more compact and calmer: a thin line separates it from the code, the current
  line's row is highlighted, and breakpoints and the lightbulb are sized to the text. Fold arrows of
  open blocks appear while the pointer is over the fold column; collapsed blocks always show theirs.
  On a wrapped line, its number, breakpoint, lightbulb and fold arrow sit next to its first row — the
  lightbulb used to appear lower, in the middle of the whole line.
- With the caret at a brace, both braces of the pair are highlighted in the theme's colors, and the
  guide between them is emphasized in the code and in the gutter.
- The panel on the left has **Queries** and **Examples** tabs, styled like the editor's document tabs,
  instead of the *FILES* heading; the file tree is on **Queries**, with everything it did before.
- The lightbulb's column is narrower: just wide enough for the icon, which keeps its size and its
  click area around it.

### Fixed

- When the debugger stops on an exception, RunPad now says so: the status bar shows
  *Paused on FormatException* instead of a plain *Paused*, and the **Debugger** tab shows the
  exception's type, message and stack trace, and whether anything catches it. Stepping from an
  exception nothing catches no longer leaves the debugger waiting with no reply: you are told the
  step isn't possible there and can **Continue** or **Stop**. A debug run that ends with such an
  exception finishes as *Unhandled FormatException*, not *Success*.
- A script's `args` no longer contains RunPad's own internal arguments (`--pid …`); it is empty.
- A .NET SDK installed by the first-run setup is found again after RunPad restarts — on macOS,
  also when RunPad is started from Finder or the Dock. Its path is now saved in
  **Preferences → Languages → .NET**.
- **Cancel** in the first-run setup stops the installer instead of leaving it running in the
  background, so a second install can no longer start next to the first.
- Braces opened and closed on the same line, such as `if (ok) { Run(); }`, no longer get a fold
  marker in the editor's margin; braces spanning several lines fold as before.
- The quick-fix lightbulb is easier to click: an invisible square around it, larger than the icon,
  opens its menu, and it stays the right size when you zoom. Clicks beside the square no longer do,
  and its menu now opens right below the lightbulb — on macOS it could appear in the top-left corner
  of the screen instead.
- Completion, error highlighting and automatic `using`s now see the types of NuGet packages
  added as `latest`, with a floating version (`1.*`) or with a version range: they used to work
  only for an exact version, although such scripts compiled and ran. After a run, the editor uses
  the versions the restore actually chose, including the packages they depend on.
- Code is shown in a real monospace font on macOS: the editor's default font resolved to
  Helvetica there. With **Preferences → Editor → Font family** left at `monospace`, RunPad now uses
  a fixed-width font that comes with the system — Menlo on macOS, Cascadia Mono or Consolas on
  Windows, DejaVu Sans Mono or a similar font on Linux — in the editor, the quick-fix menu, the IL
  view and the breakpoint condition, rename and package dialogs. A font you chose yourself is kept;
  one that isn't installed falls back to that default. Font sizes are unchanged.

## [0.4.2] — 2026-10-14

A new main menu, the same on every system, and shortcuts you can change for every command. On
macOS, RunPad starts again: the 0.4.1 app quit as soon as it was opened.

### Changed

- The main menu has a conventional layout — File, Edit, View, Run, Tools and Help — the same on
  every system. Every toolbar button and shortcut is now in the menu too: undo and clipboard
  commands, formatting, zoom, running and debugging. Dependencies, Assembly Explorer and Preferences
  moved to **Tools**, and **Help** links to the user guide, the release notes and the issue tracker.
- Every keyboard shortcut can be changed in **Preferences → Keyboard**: the page now lists all
  commands — including the editor's (completion, Go to Definition…) and the Assembly Explorer's —
  grouped by area, with **Reset** and a warning when two commands share a key.
- New default shortcuts follow common IDE conventions: **Stop** is **Shift+F5** (was Esc), **Run to Cursor** is
  **Ctrl+F10** and **Ctrl+R** shows or hides the output panel. Shortcuts you changed before are kept.
- On macOS, shortcuts use the Command key where that is the Mac convention (⌘S, ⌘N, ⌘W, ⌘, for
  Preferences…), and ⌘ + mouse wheel zooms the editor.
- On macOS, the menu bar shows each command's shortcut, as the menu does on Windows and Linux, and
  holds only RunPad's own commands: no Hide, Show All, Quit or second About in the application
  menu, and no AutoFill, Start Dictation… or Emoji & Symbols in **Edit**. About is under **Help**,
  and **File → Exit** (⌘Q) quits, as on other systems; ⌘Q also works from Preferences, text editor
  windows and the Assembly Explorer.
- A shortcut changed in Preferences takes effect at once in every open window — the menu, text
  editor windows and the Assembly Explorer — without reopening them.
- RunPad's own analyzers (`RPAD…` warnings) are now an experimental feature, off by default. Turn
  them on in **Preferences → Experimental**.

### Fixed

- The macOS app starts again. In 0.4.1 it quit at once because it could not find its own .NET
  runtime inside the app bundle.
- Quitting asks about unsaved changes in text editor windows together with unsaved tabs, then
  quits; before, answering for a text editor window stopped the quit and RunPad stayed open.
- Updated a component of the in-app NuGet restore that had known denial-of-service
  vulnerabilities (System.Security.Cryptography.Pkcs 8.0.2).
- In the light theme, the children of a selected item in the Preferences tree and in the file
  tree no longer turn white.
- Preferences → Keyboard warns when a file-tree or Assembly Explorer shortcut is also used by a
  command the same keypress reaches, such as the file tree's Delete rebound to New File's key.

## [0.4.1] — 2026-10-14

The first public release.

### Changed

- RunPad is now developed in the open at [campforge/run-pad](https://github.com/campforge/run-pad),
  with a [roadmap](ROADMAP.md) and a [contributing guide](CONTRIBUTING.md).
- Every package ships the license texts and `THIRD-PARTY-NOTICES.txt`, which lists every
  third-party component with its license, next to the application.

### Fixed

- Quick fixes that add a `using` directive no longer fail with newer .NET SDKs, which report the
  inserted line as a character-level change.

## [0.4.0] — 2026-10-13

### Added

- A plain text editor window for configuration and other text files.
- The [user guide](https://campforge.github.io/run-pad-docs/), with screenshots and downloadable
  examples.
- The installers show an end-user license agreement.

### Changed

- The `RunPad.Runtime` library — the one your scripts are compiled against — is now licensed under
  MIT, so programs written in RunPad can be distributed under any license. RunPad itself stays under
  GPL-3.0.
- The status strip and the command-line runner report the script's real exit code: a script that
  calls `Environment.Exit(7)` shows *Exit code 7* instead of *Success*, and a failed compile shows
  *Compilation failed*. Text on standard error alone no longer fails a CLI run.
- Smoother typing in the script editor.
- Avalonia updated to 12.1.4.

### Fixed

- macOS app bundle layout and its ad-hoc signature verification.

## [0.3.0] — 2026-10-12

### Added

- **Editor:** Go to Definition and Go to Implementation (F12), Find References, smart rename,
  symbol occurrence highlighting, inlay hints, object initializer completion, Resolve All Usings
  (Ctrl+Alt+U), zoom in the status strip, and an option to accept completion when typing
  punctuation.
- **Diagnostics:** Roslyn IDE analyzers and refactorings in the lightbulb, grouped by kind with
  icons, and shown only on the caret's line; a master switch for code fixes and refactorings.
- **RunPad analyzers** (`RPAD…`): more than 500 checks for code that always throws at run time —
  constant arguments out of range, invalid literals, wrong argument validation — plus LINQ,
  performance and simplification hints.
- **Assembly Explorer** in the style of ILSpy: several assemblies at once, decompiled C# or IL,
  resources, tabs with back/forward history, search across types and members, Save Code, and Analyze
  (Used By, Uses, Instantiated By, Implemented By, Overridden By and more).
- **Runtime helpers:** interactive input (`Util.ReadLine`, `Util.GetPassword`),
  `Util.ProgressBar`, `Util.Cache`/`CacheAsync`, file helpers (`Util.SaveString`, `LoadString`, …),
  `Util.CurrentQueryPath` and wider compatibility with imported `.linq` scripts.
- **`.linq` import:** a File menu command and `runpad convert` turn `.linq` files into
  `.rpad` scripts.
- **NuGet manager** with package source configuration and authentication.
- **First-run setup** that finds or installs the .NET SDK and the netcoredbg debugger.
- **Installers:** MSI for Windows, `.deb` and AppImage for Linux, unsigned DMG images for Apple
  Silicon and Intel Macs, and portable archives.
- **File tree:** New File, drag and drop to move files and folders.
- The native macOS title bar and system menus; the same application menus on every desktop
  platform; the app icon on every window.

### Changed

- Scripts compile in-process with Roslyn instead of MSBuild — faster runs, with build output cached
  between runs of an unchanged script.
- Program-kind C# scripts are wrapped in a generated class, so `Main` may be static or not and
  return `void` or `Task`.
- Roslyn updated to 5.9, NuGet to 7.9, ICSharpCode.Decompiler to 11.1; `RunPad.Runtime` no longer
  depends on the System.Text.Json package.

### Fixed

- Unsaved work is safe: closing, renaming, moving or deleting a tab writes its changes first, and a
  failed write keeps the tab and its text.
- A file with invalid front matter opens as plain text and is never run with default settings.
- macOS debugging: launching under the debugger and breakpoints in Roslyn-compiled scripts.
- Results rendering after display scaling changes; font rendering in the editor.
- File tree errors on unreadable folders no longer crash the app.

## [0.2.0] — 2026-07-18

### Added

- **Visual Basic** scripts: compile, run and debug, with keyword-based folding.
- **Debugger** over the Debug Adapter Protocol, with conditional breakpoints and hit counts; badges
  and outlines for conditional and unverified breakpoints.
- **IntelliSense:** real Roslyn code fixes, automatic import of namespaces (including types from
  NuGet packages), completion icons, overload counts, override stubs, contextual keywords, type
  filtering inside generic arguments, and snippets.
- Formatting and navigation commands with keyboard shortcuts.
- **Command-line runner** (`RunPad.Cli`) for running `.rpad` files from a terminal.
- More `.Dump()` overloads and documented `DumpOptions`.
- Path macros in references, such as `$(CurrentQueryPath)`.
- File tree with New Folder and per-language icons; New RPad File for C# and Visual Basic.
- Save, Save As (Ctrl+Shift+S), confirmation before closing unsaved tabs, restoring open tabs on
  start, and a drop-down of all open tabs.
- A persistent build cache for compiled scripts.

### Changed

- The editor is built on a fork of AvaloniaEdit and AvaloniaEdit.TextMate.
- A shared base for every compiled .NET language host.
- C# scripts get the SDK's implicit usings in both the compiler and IntelliSense.

### Fixed

- Stale output after a failed build; debugger line offsets in Program scripts; duplicate app icon on
  Linux; completion races and accidental replacement of declaration names.

## [0.1.0] — 2026-07-05

### Added

- Avalonia 12 desktop app with a C# editor, syntax highlighting and themes.
- Running scripts and rendering `.Dump()` results as HTML tables.
- First C# IntelliSense: completion, signature help and diagnostics.
- First debugger integration with breakpoints.
- The `.rpad` file format: Markdown with YAML front matter and one runnable code block.
- A scripts folder shown as a file tree that follows changes on disk.
- Preferences, keyboard shortcuts, logs and the app icon.

## [0.0.0] — 2024-04-30

- The project started as **DotPad**: an Avalonia desktop scratchpad for C# with a code editor
  based on AvaloniaEdit and an HTML renderer for results.

## Before that

The first plan was to extend RoslynPad rather than start from scratch: a
[fork of RoslynPad](https://github.com/hobobyte/roslynpad) was meant to gain a debugger and the
other features RunPad has today. The work in the fork, in April 2024, prepared the ground:

- removed the Windows-only WPF front end (its editor, dialogs and glyphs), keeping only the
  cross-platform Avalonia one;
- dropped support for old .NET SDKs and added detection of the installed SDKs;
- removed problematic parts, refactored, and moved shared helpers into a utilities project.

Even this groundwork showed how large the rest of the changes would be — the debugger and the other
features had not been started yet — so writing a new application from scratch turned out to be the
better choice. RunPad began as DotPad two days after the last commit to the fork.

[0.5.0]: https://github.com/campforge/run-pad/releases/tag/v0.5.0
[0.4.2]: https://github.com/campforge/run-pad/releases/tag/v0.4.2
[0.4.1]: https://github.com/campforge/run-pad/releases/tag/v0.4.1
[0.4.0]: https://github.com/campforge/run-pad/releases/tag/v0.4.0
[0.3.0]: https://github.com/campforge/run-pad/releases/tag/v0.3.0
[0.2.0]: https://github.com/campforge/run-pad/releases/tag/v0.2.0

[0.6.0]: https://github.com/campforge/run-pad/releases/tag/v0.6.0

[0.7.0]: https://github.com/campforge/run-pad/releases/tag/v0.7.0

[0.8.0]: https://github.com/campforge/run-pad/releases/tag/v0.8.0

[0.9.0]: https://github.com/campforge/run-pad/releases/tag/v0.9.0

[0.9.1]: https://github.com/campforge/run-pad/releases/tag/v0.9.1

[0.9.2]: https://github.com/campforge/run-pad/releases/tag/v0.9.2

[0.9.3]: https://github.com/campforge/run-pad/releases/tag/v0.9.3
[0.9.4]: https://github.com/campforge/run-pad/releases/tag/v0.9.4

[0.9.5]: https://github.com/campforge/run-pad/releases/tag/v0.9.5
