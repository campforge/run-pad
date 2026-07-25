# Icons audit (TODO.md #81)

Full audit of every resource key in `src/main/RunPad/Resources/Icons.axaml`, done by grepping all of
`src/` for `StaticResource <key>`, `DynamicResource <key>`, and code-behind lookups
(`TryGetResource`/`LoadResource<T>` string literals, `ResourceKeyToGeometryConverter` bindings). Two
independent, unrelated icon sets lived in that one file; this table is the record of which keys are
real and which were dead weight, produced before deleting the dead set (see "Old set" below).

## Active set — `Icon.*` `StreamGeometry` (Material-Design-style, 24×24 viewbox)

All keys below are used. `PathIcon`/`IconData`/`Data` bindings are XAML; "code-behind" means the
key string is looked up at runtime via `TryGetResource`/`LoadResource<T>` rather than bound in XAML.

| Key | Used in |
|---|---|
| `Icon.Play` | `EditorTabItemControl.axaml:69` |
| `Icon.Pause` | `EditorTabItemControl.axaml:152` |
| `Icon.Stop` | `EditorTabItemControl.axaml:75` |
| `Icon.Bug` | `EditorTabItemControl.axaml:108` |
| `Icon.Close` | `EditorTabItemControl.axaml:19` |
| `Icon.Plus` | `EditorTabsControl.axaml:94` |
| `Icon.DebugStepOver` | `EditorTabItemControl.axaml:128` |
| `Icon.DebugStepInto` | `EditorTabItemControl.axaml:136` |
| `Icon.DebugStepOut` | `EditorTabItemControl.axaml:144` |
| `Icon.RunToCursor` | `EditorTabItemControl.axaml:159` |
| `Icon.Breakpoint` | `BreakPointMargin.cs:72` (code-behind) |
| `Icon.BreakpointCondition` | `BreakPointMargin.cs:75` (code-behind) |
| `Icon.DebugArrow` | `BreakPointMargin.cs:73` (code-behind) |
| `Icon.Lightbulb` | `CodeFixMargin.cs:48` (code-behind) |
| `Icon.Hammer` | `CodeFixMargin.cs:121` (code-behind) — quick-fix menu item icon, TODO #79 |
| `Icon.SidebarLeft` | `MainWindow.axaml:86` |
| `Icon.PanelBottom` | `EditorTabItemControl.axaml:115` |
| `Icon.Rename` | `FileTreeView.axaml:33` |
| `Icon.Delete` | `FileTreeView.axaml:38` |
| `Icon.FolderOpen` | `FileTreeView.axaml:44,49,100` |
| `Icon.LanguageCSharp` | bound via `ResourceKeyToGeometryConverter` from `ILanguageHostFactory.EditorProfile.IconResourceKey` (C# host) — see below |
| `Icon.LanguageVb` | same, VB host |

## Old set — `DrawingImage` (multi-color, `DrawingGroup`/`GeometryDrawing`, Noun Project /
Visual Studio Image Library sourced) — **all 31 confirmed unused, deleted**

None of the keys below matched any `StaticResource`/`DynamicResource`/`TryGetResource`/`LoadResource<T>`
reference anywhere in `src/`: `Save`, `Bulb`, `Rocket`, `Copy`, `Run`, `Folder`, `OpenFolder`,
`MoveFolder`, `CsFile`, `Stop`, `Feedback`, `Update`, `FontSize`, `ExceptionMarker`, `WarningMarker`,
`FormatDocument`, `CommentCode`, `UncommentCode`, `BuildSolution`, `NuGet`, `Rename`,
`RegularExpression`, `Search`, `FileCollection`, `Close`, `Time`, `MatchBrace`, `PackageFolder`,
`Favorite`, `SetPlatform`, `Bug`, `ClearContent`. Every one of these concepts that's actually used
today (bulb, close, stop, bug, run, rename, ...) already has a live `Icon.*` replacement in the active
set above — the old set was a full leftover import, not a partial one, so it was removed outright
rather than trimmed key-by-key.

## Per-language icon — moved out of the UI layer

`FileTreeViewNode.LanguageIconKey` used to be a `switch` on `RpadLanguage` hardcoded in the main UI
project (`src/main/RunPad`), the same class of problem already fixed for snippets/`DisplayName`
(TODO #41/#49/#58) and CodeFolding (TODO #76). It's now `EditorLanguageProfile.IconResourceKey` —
each `ILanguageHostFactory` (`CSharpLanguageHostFactory`/`VbLanguageHostFactory`) supplies its own
icon key, and `FileTreeViewModel` resolves it through `ILanguageHostRegistry` when building a node, the
same seam TODO #76 used for `FoldingRegionsProvider`.

## Re-running this audit

```bash
grep -rhoE "x:Key=\"[^\"]+\"" src/main/RunPad/Resources/Icons.axaml
grep -rn "StaticResource <key>\|DynamicResource <key>" src/ --include=*.axaml
grep -rn "\"<key>\"" src/ --include=*.cs   # code-behind TryGetResource/LoadResource<T> lookups
```
