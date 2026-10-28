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

- Work on a `feature/<name>` branch (or `fix/<name>`) and open a pull request to `develop`. `main` only receives releases.
- Commits follow [Conventional Commits](https://www.conventionalcommits.org/) in English, subject up to 72 characters: `feat(deploy): ...`, `fix(gui): ...`, `test: ...`, `docs: ...`, `refactor: ...`.
- Keep a pull request to one idea. Include tests when behaviour changes.

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

## Pull request checklist

- [ ] `dotnet test` passes
- [ ] The shell suite passes if you touched the remote script
- [ ] Conventional commit subjects
- [ ] README updated if a feature or command changed

## Security

Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md), not in a public issue.

## Conduct

Be kind and assume good faith; see [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
