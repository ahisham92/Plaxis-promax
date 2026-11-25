# Plaxis ProMax: plan

_Draft 1, 24 Nov 2026_

An AI assistant for PLAXIS 2D and 3D (the current version and the one before), powered by Claude, that works the way
[Sketch ProMax](https://github.com/ahisham92/sketch-promax) works in Revit: you ask in plain language and it builds,
calculates and reports on the open Plaxis project through typed tools. It asks before risky changes, and every step it
takes can be seen.

## 1. Approach: reuse what already works and leave out what went wrong

Sketch ProMax has about 28,000 lines of chat code, and only 4 of its 80 chat files touch the Revit API. Plaxis ProMax
starts from that code instead of rewriting it. The Revit-only parts are replaced by one Plaxis link.

| Taken from Sketch ProMax (kept as is, or close to it) | Replaced for Plaxis |
|---|---|
| Chat engine: Claude Code in print mode with stream-json (`ClaudeCli`, `ChatEngine`, `StreamJson`, `StreamRender`), sign-in and connection checks (`SignInFlow`, `SignInHealth`, `ConnectionProbe`, `ConnectionWatch`), plan usage | The Revit dockable pane becomes a standalone window of its own |
| Chat window pieces: Markdown view, history drawer and search, saved prompts, home view, model picker | The Revit ribbon becomes a Plaxis Expert menu entry plus a Start menu shortcut |
| MCP server served on 127.0.0.1 with a token (`McpHttp`, `ChatMcp`), approvals (`Approvals`, `ApprovalPolicy`) | `RevitDispatcher` (Revit's main thread) becomes `PlaxisLink` (the Plaxis remote scripting server) |
| Settings, safe file writes, bug log and bug reports, updates from GitHub Releases (`Updater`, `ReleaseSource`) | The `IRevitTool` set becomes an `IPlaxisTool` set |
| Procedure: bug log with statuses, release notes per version, a field test, and a release workflow on main | |

## 2. Architecture

```
Plaxis Input ── Expert > Python > Run script: "Plaxis ProMax.py" ──┐   (Plaxis hands the script its server port and password)
Start menu ─────────────────────────────────────────────────────────┤
                                                                    ▼
                 PlaxisProMax.exe (WPF, .NET 8): the chat window, in its own process, beside Plaxis
                    │ runs Claude Code (your Claude plan, no API key), stream-json in and out
                    │ serves the tools over MCP on 127.0.0.1 with a new token each start
                    ▼
                 IPlaxisTool ── approval policy ── PlaxisLink
                                                     │ JSON lines over stdin/stdout
                                                     ▼
                 plaxis_worker.py, run on Plaxis's own Python, through Bentley's plxscripting
                    ├─ Input server  (s_i, g_i): soils, structures, mesh, phases, calculate
                    └─ Output server (s_o, g_o): results (port from g_i.view(phase))
```

Why it is built this way:
- **Bentley's own wrapper talks to Plaxis.** The remote scripting protocol is encrypted, and the official
  `plxscripting` package that ships with Plaxis handles that. A small Python worker uses it, so we never re-implement
  the protocol, and the same worker covers both 2D and 3D across versions.
- **The chat window is a separate process.** In Sketch ProMax, Revit drew the chat, so a long Revit call froze the chat
  too (bug 110). Here the window stays responsive while Plaxis calculates.
- **The tools read Plaxis's own state.** `s_i.is_2d` / `is_3d` and the version come from the server, and the tools
  read collections such as `Phases`, `Materials` and `Boreholes` directly. Nothing about the model is guessed.

## 3. Mistakes from Sketch ProMax this design avoids from day one (from its bug log)

1. **Fresh code instead of what was already written** (bugs 98, 127 to 136). The jobs wrote new code about 1,500 times
   a day and used working code only 42 times. In Plaxis ProMax:
   - Ready-made tools come first. Common reads, such as listing phases, reading a material or getting a result, are
     tools, not code.
   - Every script that works is kept, with its inputs, and searched by what it does (object types and commands), not
     by shared words in its description.
   - A kept script that is reused twice is offered as a named tool.
   - New code that ignores a close kept match is held once, with that match shown.
2. **The user's rules didn't reach every job** (bugs 64, 100, 115). Project rules live in one file per project that
   every chat and job reads.
3. **Too many approvals** (bug 32). The default asks only before risky steps: running code, starting a new project or
   opening one over unsaved work, deletes, and overwriting files.
4. **Dialogs that wait for a click while nobody is there** (bugs 79, 80, 112). Nothing we show blocks, and a
   modal dialog in Plaxis is reported in the chat instead of being clicked away.
5. **Calls with no time limit** (bugs 81, 82, 87). Every call to Plaxis has a time limit and can be cancelled.
   Calculations run as jobs that report their progress.
6. **Helper programs inheriting handles** (bug 72). The worker and Claude Code start without inheriting the window's
   handles.
7. **"Done" with nothing checked** (bugs 95, 96, 123, 132). Tools return what changed: ids, counts and values. Status
   lines come from what the tools reported, never from memory.
8. **Answers too long for the chat** (bug 78). Every list is paged.
9. **Network blocks shown as expired sign-ins** (bugs 71, 137). Errors are told apart, as in Sketch ProMax 0.35.117.
10. **Heavy updates** (bugs 76, 99, 105, 134). One small package per release, switched in when the window restarts,
    with no waiting on idle time.
11. **Coordinator complexity before the basics worked** (bugs 88, 94, 97, 113, 131). The first versions are a simple
    chat. Parallel jobs come only once the simple chat has been proven on real projects.

From CSI ProMax's field test on SAFE (25 Nov 2026):

12. **A name from one program used in its sibling.** The test read the table "Story Definitions", which ETABS has and
    SAFE does not. Here every name that differs between PLAXIS 2D and 3D, or between versions, is tried from a list
    of candidates, and what Plaxis does not have is listed in the answer instead of failing the whole step. The
    self-test only calls tools that need no names.
13. **Results reported when nothing was calculated.** SAFE's check said "slab design: pass" with the analysis not
    run, because an input table had rows. Here results are read only from phases that ended OK: Output opens on the
    last of those, a phase with no results says how it ended, a run where some phases failed says so instead of
    "Calculated", and the Viewer marks a phase that did not end OK as not a final result, in the picture too.
14. **The wrong program's name in a message** ("ETABS refused" in SAFE, an "etabs" version in a SAFE model). The
    product shown is read from Plaxis itself (is_2d or is_3d), never assumed.
15. **The plug-in missing from the newer program** (ETABS 22 had it, ETABS 23 did not). Plaxis needs nothing put
    into each Plaxis version: one script in Documents is run from any version's Expert menu.

## 4. Roadmap

- **0.1 Connect and look:** the chat window, sign-in, connection to Input and Output, `plaxis_status`,
  `inspect_object`, `list_model`, `run_commands` (Plaxis command line), `run_plaxis_code` (always asks), new, open and
  save, and undo.
- **0.2 Build:** soil contour, boreholes and layers from a borehole log (PDF, Excel or photo), soil materials
  (Mohr-Coulomb, Hardening Soil, HS small), plates, anchors, geogrids, embedded beams, interfaces, loads, the mesh,
  phases (staged construction, water levels, activation), and calculating.
- **0.3 Results and reports:** displacements, forces in structures, safety factors and curves, exported to Excel, Word
  or PDF reports with plots.
- **0.4 Reuse:** the kept-code library, learned tools and recipes (rule 1 above).
- **0.4 Modeller and Viewer:** a model is built and its results are read with no Plaxis and no licence, as in ETABS
  ProMax; only Calculate in Plaxis opens Plaxis, for as long as the run takes (docs/MODELLER.md).
- **0.5 Jobs:** parallel workers, only after 0.1 to 0.4 have been proven.

## 5. Open questions

- The exact PLAXIS versions on the PC (to be read from the installed programs).
- Whether the licence includes remote scripting (Expert > Configure remote scripting server will show this).
- Where this Plaxis version lists your own Python tools in the Expert menu. `Run script` works in every version.
