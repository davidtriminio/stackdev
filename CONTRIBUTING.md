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

## Pull request checklist

- [ ] `dotnet test` passes
- [ ] The shell suite passes if you touched the remote script
- [ ] Conventional commit subjects
- [ ] README updated if a feature or command changed

## Security

Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md), not in a public issue.

## Conduct

Be kind and assume good faith; see [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
