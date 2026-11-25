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

## Build and test

```
dotnet run --project tests/PlaxisProMax.Tests     # C# checks (exit code = failures)
python -m unittest discover tests/worker          # worker against a fake Plaxis
pwsh ./build.ps1                                  # self-contained win-x64 into dist/
```

## Status

0.1.0, first version. Tested against a fake Plaxis only; the field test on real PLAXIS 2D and 3D is next.
