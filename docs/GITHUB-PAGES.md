# GitHub Pages user documentation — maintainer notes

The user guide is built with [MkDocs](https://www.mkdocs.org/) and
[Material for MkDocs](https://squidfunk.github.io/mkdocs-material/) and prepared for publication to
<https://coddwrench.github.io/run-pad/> by `.github/workflows/docs.yml`.

This file is not part of the published site.

## What is published

- Source: `mkdocs.yml` and `docs/site/**` only (`docs_dir: docs/site`). MkDocs reads
  nothing else, so `docs/handoffs`, `docs/reviews`, `docs/archive`, the plans in `docs/`,
  `TODO.md`, `ANALYSIS.md` and the root `README.md` are never copied into the site.
- Downloadable examples: `docs/site/examples/*.rpad` and `*.linq` are copied as static
  files.
- Icons: `docs/site/assets/app-icon.svg` (copy of `docs/img/app-icon.svg`) and
  `docs/site/assets/favicon.png` (copy of `src/main/RunPad/Assets/runpad.png`).
- The theme uses no web fonts (`theme.font: false`) and no repository widget, so the
  pages make no third-party requests. `scripts/docs/check_site_links.py` fails the build if
  a page loads a stylesheet, script, image or font from another origin.

## Workflow

`.github/workflows/docs.yml` runs only when `docs/site/**`, `mkdocs.yml`,
`requirements-docs.txt`, `scripts/docs/**` or the workflow itself change, or when started
manually.

| Event | Build (`mkdocs build --strict` + link check) | Deploy |
| --- | --- | --- |
| Pull request | Yes, with `contents: read` only | No |
| Push to `main` | Yes, and uploads the Pages artifact | Yes |
| Manual run (*Run workflow*) on `main` | Yes, and uploads the Pages artifact | Yes |
| Manual run on another branch | Yes | No |

The deploy job alone gets `pages: write` and `id-token: write`, runs in the
`github-pages` environment, and uses `actions/configure-pages@v5` and
`actions/deploy-pages@v4`; the build job uploads with `actions/upload-pages-artifact@v4`.

The existing `CI` workflow (`dotnet-desktop.yml`) has no path filters, so documentation
changes also trigger the full .NET CI. That is unchanged by this task.

## One-time setup (repository admin)

1. **Settings → Pages → Build and deployment → Source: GitHub Actions.** Do not choose
   "Deploy from a branch"; no `gh-pages` branch is used.
2. The first deployment creates the `github-pages` environment. In
   **Settings → Environments → github-pages**, keep the deployment branch rule limited to
   `main` (the default for Pages).
3. Push a change under `docs/site/` to `main`, or run the **Docs** workflow manually on
   `main`. The site appears at <https://coddwrench.github.io/run-pad/>.

`site_url` in `mkdocs.yml` and `SITE_URL` in the workflow must match the Pages URL. If the
repository is renamed or a custom domain is configured, update both.

### Private repositories

- GitHub Pages is available for **public** repositories on every plan, but for
  **private** repositories only with GitHub Pro, GitHub Team, GitHub Enterprise Cloud or
  GitHub Enterprise Server. On GitHub Free, a private repository cannot publish Pages; the
  deploy job then fails at *Configure Pages* or *Deploy*, while PR builds keep working.
- A Pages site is **public on the internet even when the repository is private**, unless
  the repository belongs to an organization on GitHub Enterprise Cloud that restricts
  Pages access. Only the built `docs/site` content is published; internal documents in
  the repository stay private.
- The guide links to the releases page and source paths on GitHub. While the repository
  is private, anonymous readers cannot open those links.

## Building locally

Use an isolated virtual environment, not a global install:

```bash
python3 -m venv /tmp/runpad-docs-venv
/tmp/runpad-docs-venv/bin/pip install -r requirements-docs.txt
/tmp/runpad-docs-venv/bin/mkdocs serve              # preview at http://127.0.0.1:8000/run-pad/
/tmp/runpad-docs-venv/bin/mkdocs build --strict     # output in ./site (git-ignored)
/tmp/runpad-docs-venv/bin/python scripts/docs/check_site_links.py site --site-url https://coddwrench.github.io/run-pad/
```

`requirements-docs.txt` pins `mkdocs`, `mkdocs-material`, `pymdown-extensions` and
`Pygments`. When updating, check the latest stable versions on PyPI, rebuild with
`--strict` and rerun the link check. Material for MkDocs prints a notice about MkDocs 2.0
during builds; the pinned MkDocs 1.6 line is what this site uses.

## Checking the examples

Every `.rpad` example except `analyzer-diagnostics.rpad` should run cleanly with the
command-line runner (no network, files or packages needed):

```bash
dotnet build src/main/RunPad.Cli -c Release
for f in docs/site/examples/*.rpad; do dotnet src/main/RunPad.Cli/bin/Release/net10.0/runpad.dll "$f"; done
```

`analyzer-diagnostics.rpad` is expected to stop with `error CS0103` (exit code 1). The
expected output of each example is described in `docs/site/examples.md`; update it when
an example changes.

## Screenshot manifest

Screenshots live in `docs/site/assets/screenshots/`. They are real native-window captures of the installed RunPad application, not mock-ups. All seven files are included; `mkdocs build --strict` fails if an image link is broken.

| File | Page | Captured state |
| --- | --- | --- |
| `overview.png` | Overview | Main window, a product-list script, and two Dump tables |
| `editor-highlighting.png` | Editor | C# comments, strings, LINQ calls and syntax colors |
| `diagnostics.png` | Diagnostics and analyzers | Warning underline on int.Parse("not-a-number") (RPAD0356); no quick-fix menu pictured |
| `debugging.png` | Debugging | Paused on total += i, with total=0 and i=1 in Locals |
| `dependencies.png` | Packages and references | Newtonsoft.Json search, cached packages and version details; nothing added |
| `assembly-explorer.png` | Assembly Explorer | System.Linq search for Enumerable.Where, tree selection and C# code |
| `preferences.png` | Reference | Interface settings: appearance and theme selectors |

| Field | Value |
| --- | --- |
| Status | Complete: seven original PNG captures |
| Captured application | Installed /Applications/RunPad.app, CFBundleShortVersionString 0.4.0; source documentation checked against main 11a9f33 |
| Operating system | macOS 26.5.1, Apple Silicon |
| Theme and size | Dark: RunPad Midnight / Dark+; original window captures (2268×1384 main, 2200×1496 explorer, 2000×1336 Dependencies) |
| Samples | Self-contained product list, integer summation loop, non-numeric Parse literal, LINQ over names; public System.Linq.dll copied into a temporary directory |
| Captured | 2026-10-13 |

The screenshots illustrate the controls and workflows, and are not all exact executions of the downloadable samples. Captions describe the captured state. Completion is documented from source; the Editor image shows highlighting, not an open completion popup.

Keep personal data out of captures: use the example scripts in a fresh tab, and avoid
showing user names in file paths, recent files or other open tabs.
