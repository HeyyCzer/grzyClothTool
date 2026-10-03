# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

grzyClothTool: Windows WPF app (.NET 10, `net10.0-windows`, win-x64) for creating/managing GTA V addon clothing packs (drawables `.ydd`, textures `.ytd`, cloth physics `.yld`) and building them as FiveM / alt:V / Singleplayer resources. Uses CodeWalker (in-repo copy under `CodeWalker/`) for all GTA file formats and the 3D previewer. Also ships `grzyOptimizer`, a standalone CLI that optimizes textures and generates missing LODs for any resource folder.

## Build / test

```powershell
dotnet build grzyClothTool.sln                     # builds CodeWalker first, then everything
dotnet test grzyClothTool.UnitTests                 # xunit unit tests
dotnet test grzyClothTool.UnitTests --filter "FullyQualifiedName~FolderOptimizerTests"   # single class
dotnet test grzyClothTool.UnitTests --filter "FullyQualifiedName~FolderOptimizerTests.SomeTest"  # single test
dotnet test grzyClothTool.Tests.Wpf                 # FlaUI UI tests (launch the real exe, need a desktop session)
dotnet publish grzyClothTool.Optimizer.Cli -c Release   # single-file self-contained grzyOptimizer.exe
```

- **`packages/` is a build output, not NuGet.** CodeWalker projects set `BaseOutputPath` to `..\..\packages\` and a post-build step moves DLLs there; every other project references `..\packages\CodeWalker(.Core).dll` via `HintPath`. Only `grzyClothTool.csproj` has a solution-level dependency on the CodeWalker projects, so on a fresh clone build the solution (or CodeWalker) before building a single project in isolation. `packages/` is gitignored.
- WPF tests locate `grzyClothTool/bin/<Config>/net10.0-windows/win-x64/grzyClothTool.exe` — build the app first in the same configuration. They isolate state by setting `GRZYCLOTHTOOL_LOCALAPPDATA` (honored by `AppDataHelper`).
- `external/Sollumz` is a git submodule (`git submodule update --init`). Optional: the CLI builds without it, only `--sollumz bundled` mode needs it (copied next to the exe as `sollumz/`).

## Projects

| Project | Role |
|---|---|
| `grzyClothTool` | WPF app (WinExe). AvalonDock, Material.Icons, Magick.NET, Sentry. |
| `grzyClothTool.Optimization` | UI-free `net10.0` lib shared by app and CLI: texture rules/codec, `FolderOptimizer`, Blender LOD pipeline. Keep it free of WPF deps. |
| `grzyClothTool.Optimizer.Cli` | `grzyOptimizer.exe <folder>`; hand-rolled arg parsing in `CliOptions.cs`. |
| `grzyClothTool.Shared` | Plugin interfaces (`IPlugin`, `IPatreonPlugin`) for MEF plugins downloaded to `%LOCALAPPDATA%/grzyClothTool/plugins` (plugin loading currently commented out in `App.xaml.cs`). |
| `grzyClothTool.UnitTests` | xunit; references both the app and Optimization. |
| `grzyClothTool.Tests.Wpf` | FlaUI (UIA3) end-to-end tests. |
| `CodeWalker/*` | Vendored CodeWalker (dexyfex). Treat as third-party; change only when necessary. |

## App architecture

- **Global state is static.** `MainWindow.AddonManager` (the open project) and `MainWindow.Instance` are accessed from helpers, models and the build pipeline directly. Many helpers are static singletons (`SaveHelper`, `LogHelper`, `SettingsHelper.Instance`, `PersistentSettingsHelper.Instance`). No DI container, no strict MVVM — views have substantial code-behind; models implement `INotifyPropertyChanged`.
- **Project model**: `AddonManager` → `Addons` (`Addon`, max 128 drawables each; overflow auto-splits into further addons) → `GDrawable` (+ `GDrawableDetails`, `GDrawableReserved` placeholders) → `GTexture` / `GTextureEmbedded` (+ `GTextureDetails`). Drawable import is processed on a background `BlockingCollection` queue inside `AddonManager`; use `AddonManager.AddonsLock` when mutating collections off the UI thread.
- **Persistence**: projects live under `PersistentSettingsHelper.Instance.MainProjectsFolder/<ProjectName>/` as JSON (`autosave.json`, or `autosave.external.json` for "external" projects whose files stay at original locations instead of being copied in). `SaveHelper` autosaves every 60s when `HasUnsavedChanges`, writes atomically and keeps rotating backups in `save-backups/`. Set `SaveHelper.SavingPaused` during long operations. Properties marked `[JsonIgnore]` are UI-only.
- **Build pipeline**: `Helpers/BuildResourceHelper.cs` has one path per `BuildResourceType` (FiveM / AltV / Singleplayer RPF). Work is parallel, bounded by a CPU semaphore, IO semaphore and `MemoryBudget`; progress is weighted via `BuildReporter`. The first worker failure cancels the whole build (`Fail` + linked CTS) and is rethrown instead of the resulting `OperationCanceledException`. `ClaimOutput` throws if two items map to the same output path — preserve these invariants when adding build steps.
- **Textures**: `Helpers/TextureOptimizer.cs` adapts the shared `grzyClothTool.Optimization.TextureRules` to app settings (per-type resolution limits); `ImgHelper` does conversion. Optimization can be deferred to build time (`IsOptimizedDuringBuild`), which is why YDDs with modified embedded textures get resaved during build.
- Themes: `Themes/Dark.xaml` / `Light.xaml` + `Shared.xaml`, swapped at runtime by `App.ChangeTheme`.

## Optimizer / LOD generation

`FolderOptimizer` walks a folder (in place or to an `_optimized` copy), optimizes every `.ytd` and embedded `.ydd` textures (power-of-two within limit, DXT5 for uncompressed, full mips), and — with `LodGenerationOptions` — adds missing Medium/Low LODs:

1. `LodGenerator` exports the YDD to CodeWalker XML in a temp dir.
2. `BlenderWorkerPool` keeps long-lived Blender processes running the embedded `Lods/blender_lod_worker.py` (extracted to temp at runtime). Protocol: JSON job per line on stdin; answers on stdout prefixed with `@@GRZY@@ `. Sollumz "Generate LODs" decimates the High mesh.
3. `LodGrafter` copies only the new LOD models back into the original YDD.

YDDs with a sibling `.yld` (cloth physics) are skipped. Blender located by `BlenderLocator` or `--blender`; Sollumz is `installed`, `bundled`, or a folder path.

In the app, **View > LOD Generator** (`Views/LodGeneratorWindow`, `Helpers/LodGenerationHelper`) runs the same `LodGenerator` on project drawables missing Med/Low models (drawables with `ClothPhysicsPath` skipped). The result is written to project assets as `{drawable.Id}.ydd` and `FilePath` is repointed, so external projects' originals are never touched. Its Blender/Sollumz choices persist in `PersistentSettingsHelper.LodGenerator` (settings.json); the app also ships the Sollumz submodule as `sollumz/` next to the exe.

## Conventions

- Releases: `./scripts/bump-version.ps1 patch|minor|major [-Push]` bumps `<FileVersion>` in `grzyClothTool.csproj` (the only version the updater reads), commits `:bookmark: vX.Y.Z` and tags. The tag push runs `.github/workflows/release.yml`, which publishes `grzyClothTool.zip` (flat, exe at root — the updater requires that name/layout) and `grzyOptimizer.zip`.
- Commit messages use gitmoji prefixes (`:sparkles:`, `:bug:`, `:zap:`, …).
- Newer code (Optimization, CLI, recent helpers) uses file-scoped namespaces, nullable enabled, and explanatory `///` comments on non-obvious behavior; older app code is block-scoped without nullable — match the file you're in.
