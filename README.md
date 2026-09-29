# WPAD / PAC File Manager

A portable, dependency-free Windows desktop tool for managing corporate **PAC files**
(Proxy Auto-Config) distributed via **WPAD**. It replaces hand-editing of the
`FindProxyForURL` JavaScript with a structured, validated and auditable workflow.

- **Single portable `.exe`** — no installer, no admin rights, no .NET SDK, no runtime
  beyond what Windows 10/11 already ships with.
- **Zero third-party dependencies** — custom JavaScript parser, generator, validator,
  JSON store and DNS. Nothing to download, easy to security-review.
- **No code execution** — PAC files are JavaScript, but this tool only ever *parses and
  analyzes* them statically. It never runs the script.

> Bilingual UI: **Russian / English** (switchable at runtime).

## Why

A PAC file is evaluated top-to-bottom and the **first matching rule wins**. A mistake in
rule order or syntax can misroute traffic for the whole organization. This tool makes the
rules visible, checks them, and keeps a per-file history so changes are safe and reversible.

## Features

- Import / export real `.pac` / `.dat` files with lossless round-trip (unknown blocks kept verbatim).
- Structured rule table instead of raw JavaScript; add / edit / delete / enable / disable rules.
- Editable **default action** (the final fall-through `return`).
- Condition & action **builder** — type a domain, URL pattern, subnet or proxy; the
  PAC expression is generated for you (and stays hand-editable).
- **Validation** in three passes: security (forbids `eval`/`Function`/host objects,
  prototype-chain escapes, redefinition of PAC built-ins, DNS lookups of computed names,
  unknown calls and JS constructs the parser cannot vet), structure (unknown functions with
  suggestions, argument counts, IP/mask, port ranges, unreachable code), and **shadowing**
  (unreachable / duplicate / conflicting rules). Every write — save, export, restore from
  history, CLI export — goes through one gate that vets the exact text being written; a file
  with warnings or errors **cannot be saved, exported, written back by a history restore, or simulated**.
- **Duplicate / overlap warning** when adding a rule already covered by an existing one.
- **Simulator** — three-valued (true / false / unknown) evaluation of a URL against the
  rules, with a per-rule trace. No JavaScript executed, no network needed.
- **DNS check** — resolve the domains used in rules to spot stale entries.
- **Per-file version history** with restore; **multiple files open at once** without re-importing.
- Headless **CLI** for batch validation in CI / deployment scripts.

## Requirements

- Windows 10 / 11 (x64)
- .NET Framework 4.x — already built into Windows 10/11
- No administrator rights

## Quick start

Run the app from any **writable** folder (it stores its workspace next to the `.exe`):

```
WpadManager.exe
```

Then **Import .pac/.dat** (try [`examples/sample.pac`](examples/sample.pac)), edit rules,
press **Check**, and **Save to file**.

### Command line

```
WpadManager.exe --validate <file.pac>             # safety + structure + shadowing; exit 1 on errors
WpadManager.exe --simulate <file> <url> <host>    # route trace and result
WpadManager.exe --export <store.json> <out.pac>   # export rules from a JSON store; exit 1 (nothing written) on errors
WpadManager.exe --help
```

## Build from source

No .NET SDK, MSBuild or NuGet required — the build uses the **in-box** `csc.exe` compiler.

```powershell
powershell -ExecutionPolicy Bypass -File build/build.ps1            # Core.dll + run tests
powershell -ExecutionPolicy Bypass -File build/build.ps1 -App       # + WpadManager.exe (GUI)
powershell -ExecutionPolicy Bypass -File build/build.ps1 -NoTests   # skip running tests
```

`-App` compiles the Core and App sources together into one portable `winexe`
(`build/out/WpadManager.exe`), so no separate `Core.dll` is needed in production.
The C# language level is kept at 5.0 for in-box-compiler compatibility.

## Architecture

Two layers: a dependency-free **Core** engine (all logic) and a thin **App** layer
(WinForms GUI + CLI). Both compile into one executable.

```
IMPORT   .pac/.dat text
            |
            v
         Lexer -> Parser (AST) -> Safety check (AST walk)
                              \
                               -> Recognizer -> RuleSet
                                  (rules + default action + verbatim unparsed blocks)

EDIT     RuleSet <-> GUI table / rule editor
ANALYZE  RuleSet  -> Validator + Shadowing + Simulator
EXPORT   RuleSet  -> Generator -> .pac/.dat text
PERSIST  Store(RuleSet + Versions + Audit) <-> JSON sidecar
```

Because PAC is JavaScript, the tool uses its own lexer, recursive-descent parser and AST
rather than text matching; constructs it does not model are preserved verbatim, so
import/export is lossless.

## Project structure

| Path | Contents |
|------|----------|
| `src/WpadManager.Core/Parser`    | Lexer, JS parser, recognizer (AST → rules), importer |
| `src/WpadManager.Core/Generator` | PAC generator (rules → `.pac` text) |
| `src/WpadManager.Core/Model`     | Rule/action model, action parse/format, PAC functions |
| `src/WpadManager.Core/Validate`  | Structural validation + import-time security gate |
| `src/WpadManager.Core/Analyze`   | Shadowing / duplicate analysis |
| `src/WpadManager.Core/Simulate`  | Three-valued simulator |
| `src/WpadManager.Core/Storage`   | JSON store: versions, audit, workspace |
| `src/WpadManager.Core/Resolve`   | DNS check of rule domains |
| `src/WpadManager.App`            | WinForms GUI, dialogs, CLI, i18n, entry point |
| `tests/WpadManager.Tests`        | Test suite (113 checks) |
| `build/build.ps1`                | Build script |

## Data storage

Plain, human-readable JSON, created next to the app and next to each file:

- `wpad-workspace.json` (next to the `.exe`) — open files, active file, UI language.
- `<name>.dat.history.json` (next to each file) — that file's version snapshots and audit log.

These are git-ignored in this repo (they hold environment-specific data).

## Contributing

Issues and pull requests are welcome. Please keep the **zero-dependency** and
**C# 5.0 / in-box compiler** constraints, and make sure `build/build.ps1` is green
(all tests pass) before submitting.

## License

[MIT](LICENSE) © 2026 rkubenov
