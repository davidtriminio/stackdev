# Contributing to stackdev

Thanks for helping. This page is short on purpose.

## Before you start

- Open an issue for anything bigger than a small fix, so we agree on the idea before you spend time on it.
- Bugs: say what you did, what you expected and what happened. For deploy problems paste the log of the window ("Copy log"); secrets are already masked, but read it before you post.

## Set up

You need the .NET SDK 10. The window (WPF) builds on Windows only for now; the core, the tests and the remote script do not depend on it.

```powershell
dotnet build stackdev.slnx
dotnet test
bash tests/remote/remote-script.test.sh     # Linux, WSL or Git Bash on Windows
```

## Branches and commits

- Work on a `feature/<name>`, `fix/<name>` or `docs/<name>` branch and open a pull request to `develop`. `main` only receives releases.
- Commits follow [Conventional Commits](https://www.conventionalcommits.org/) in English, subject up to 72 characters: `feat(deploy): ...`, `fix(gui): ...`, `test: ...`, `docs: ...`, `refactor: ...`.
- Keep a pull request to one idea. Include tests when behaviour changes.

## How the code is organised

- Logic lives in `StackDev.Core` and talks to the person only through `IConsoleUi` (write, ask, confirm, choose). That is what makes a feature work in the WPF window, the Avalonia window and the command line at once: ask through `IConsoleUi`, do not open a dialog from the core, and behave sensibly when `Interactive` is false.
- Do not write into the user's projects. Configuration, logs, backups and temporary copies (such as the web proxy) go to `%APPDATA%\stackdev`; a stackdev feature must not leave modified or new files in someone's git. If a project file really has to change, ask first.
- Anything saved per project is keyed by the project's path (`StateStore.Key`). When you add a new per-project file, name it `...-<key>.<ext>` or put it in a `<key>` folder, so `ProjectRelocation` moves it with the project.
- Parsers of tool output (`adb mdns services`, `netstat`, logs) are plain static functions with a test that feeds them real sample text.
- In WPF, never call `Show`, `ShowDialog` or `Close` from a `Closing`, `Deactivated` or `Closed` handler without a guard: it throws while the window is closing. Defer with `await Task.Yield()` or close only once.

## Code

- Code and identifiers in English. The user interface is in Spanish today and will be translatable; do not hard-code text in new places without a reason.
- Match the surrounding style: comment density, naming, idiom.
- The remote script `stackdev-remote.sh` must stay plain bash that works on Debian/Ubuntu and RHEL-like systems. Every message it prints needs a translation rule in `RemoteText.cs` (a test checks this), and every new subcommand needs cases in `tests/remote/remote-script.test.sh`.
- Known WPF pitfalls: subclasses of `Window` do not inherit implicit styles (use `SetResourceReference`), avoid nesting quotes inside interpolated strings, and do not set `IsDefault` on a Close button (Enter would close forms).
- Never log secrets. Anything that goes to a log passes through `SecretMask`.

## Project profiles

A profile teaches stackdev to recognise a kind of project and what to run for it. It is plain JSON, so adding a language or a framework needs no C#. The built-in ones are in `src/StackDev.Core/Detection/Profiles/profiles.json`; a person's own go in `%APPDATA%\stackdev\profiles\*.json` and are tried first. A complete example is in [`examples/profiles/go.json`](examples/profiles/go.json).

```json
{
  "profiles": [ {
    "id": "python-fastapi", "title": "Python · FastAPI", "role": "api",
    "match": { "files": ["requirements.txt", "pyproject.toml"], "contains": ["fastapi"] },
    "url": "http://localhost:8000", "open": "/docs",
    "commands": {
      "start": [ { "when": "app/main.py", "command": "uvicorn app.main:app --reload" }, { "command": "uvicorn main:app --reload" } ],
      "test": "pytest"
    }
  } ]
}
```

- `role` is `api` or `web`: the slot of `dev-menu.json` the profile fills. .NET, Angular and Flutter keep their own detection and take precedence.
- `match.files` are file names to look for (up to four folders deep); `contains` and `notContains` are case-insensitive regular expressions tested against the file; `alsoFiles` must exist next to it.
- The first profile that matches wins, in file order, so put the specific ones (NestJS) before the generic ones (Express). Among the files of a profile, the one closest to the root wins.
- A command is a string or a list of alternatives; the first whose `when` file exists is used. `{pm}` becomes `npm`, `pnpm` or `yarn` after the lockfile. `portFrom` reads the port from a file.
- Add a test in `tests/StackDev.Core.Tests/ProfileTests.cs` with a few fake files; a built-in profile must load without warnings.

## Releases

Maintainers only. A release branch comes from `develop`:

1. `git checkout -b release/x.y.z develop`
2. Add the version to `CHANGELOG.md` (Added, Changed, Fixed) and set `<Version>` in `StackDev.Gui`, `StackDev.Cli` and `StackDev.App`. Commit as `chore(release): x.y.z`.
3. Run `dotnet test` and try the window by hand: start all, a busy port, stop all, and (if you touched it) the deploy.
4. Merge into `main` with `--no-ff`, tag `vx.y.z`, and merge the branch back into `develop`.
5. Publish the single-file window (see the README), close any running stackdev first, and attach the `.exe` to the GitHub release.

## Pull request checklist

- [ ] `dotnet test` passes
- [ ] The shell suite passes if you touched the remote script
- [ ] Conventional commit subjects
- [ ] README (English and Spanish) and CHANGELOG updated if a feature or command changed
- [ ] Nothing new is written into the user's project folders

## Security

Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md), not in a public issue.

## Conduct

Be kind and assume good faith; see [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
