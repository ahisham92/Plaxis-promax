# Plaxis ProMax

An AI assistant for PLAXIS 2D and 3D, powered by Claude. Ask in plain language, and it builds, calculates and reports on the open Plaxis project through typed tools, on your normal Claude plan (no API key). It is built the way [Sketch ProMax](https://github.com/ahisham92/sketch-promax) is built for Revit, and it reuses that project's chat code.

See [docs/PLAN.md](docs/PLAN.md) for the architecture, the lessons it takes from Sketch ProMax, and the roadmap.

## Install

1. Install [Claude Code](https://claude.com/claude-code) and sign in once with your Claude account.
2. Download the latest `PlaxisProMax-<version>.zip` from Releases, unzip it, and run `Install.cmd`.
   It installs to `%LOCALAPPDATA%\PlaxisProMax\app`, puts `Plaxis ProMax.py` in `Documents\Plaxis ProMax`, and adds a Start menu shortcut.

## Start it from Plaxis

In PLAXIS Input: **Expert > Python > Run script**, then pick `Documents\Plaxis ProMax\Plaxis ProMax.py`.
The chat window opens already connected to that Plaxis (port and password are passed on, never typed).
Started from the Start menu instead, it uses the port and password in Settings.

## What is reused from Sketch ProMax

| Part | From Sketch ProMax | New for Plaxis |
|---|---|---|
| Chat engine (Claude Code, stream-json, resume) | ported | UI thread abstracted |
| MCP server over HTTP with a token | ported | Plaxis tool list |
| Approval cards and levels | ported | Plaxis risky commands |
| Kept code library | idea and procedure | matched by Plaxis identifiers, near repeats held |
| Chat window, Markdown, theme | ported | Plaxis state header |
| Plaxis link and tools | | Python worker on Plaxis's own Python |
| Self-update (release lookup, resumable download, checksum) | ported | the next start hands over to the downloaded version; Restart to update |
| Report bug, bug log, Export chats & bugs, activity log | ported | picture of the chat window, Plaxis versions |
| The crew: coordinator, workers as parallel jobs (`chat_jobs`), a teammate checking finished work, Pause all, budgets, automatic model and effort | ported | Plaxis runs one command at a time, so the jobs think side by side and their Plaxis calls take turns; one job changes the project at a time |
| Autopilot: a goal, a plan approved once, then the coordinator and workers carry on unattended; pauses at usage limits and carries on when they reset; a review team checks the goal before the run ends | ported | New chat > New autopilot chat. The backup is a copy of the project file and its data folder in `Backup` beside it. Not ported: sharing a run across windows, answering Revit dialogs |
| Chat history (search, rename, pin, export, delete), notes (`memory_note`), earlier chats (`recall_chats`), routines at a set time, saved prompts, plan usage | ported | All under the Menu button. Notes are kept per project file. Ready-made routines: nightly calculation, results to Excel, model check. Not ported yet: a chat summarising itself for the next one, the home screen with prompt ideas |
| Big models: `calculate` one phase at a time with a save after each and few stored steps; `results_to_excel` for every phase and result in one call | new for Plaxis | Not yet tried on a real Plaxis: the name of the stored-steps setting and how a failed phase reads are checked against the stand-in only |

## Build and test

```
dotnet run --project tests/PlaxisProMax.Tests     # C# checks (exit code = failures)
python -m unittest discover tests/worker          # worker against a fake Plaxis
pwsh ./build.ps1                                  # self-contained win-x64 into dist/
```

## Status

0.1.0, first version. Tested against a fake Plaxis only; the field test on real PLAXIS 2D and 3D is next.
