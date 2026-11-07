# Changelog

All notable changes to this project are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/) and the project uses [Semantic Versioning](https://semver.org/).

## [0.3.0] - 2026-11-02

### Added
- Busy ports are caught before the API or the web starts: you see who holds the port and choose another free port (saved in the configuration), one you type, closing the process, or using it as it is.
- Services that stop to ask something while running in the background (for example pnpm's "Y/n") are noticed: the card shows "waiting for your answer" with the question and offers to continue in a terminal.
- Running the mobile app asks which API it talks to: this PC, the server of the project or any address.
- Phones with wireless debugging (Android 11+) are found on the network and connected with no address to type; pairing needs only the six-digit code.
- Relocate a project whose folder was renamed or moved (its configuration and history follow it) and copy a project's configuration to another folder or when opening one that has none.
- The web is started with a temporary copy of its proxy file when it points to another API port than the one in use; the project's own file is never edited.

### Changed
- Windows' own message boxes are replaced by dialogs in the application's theme, and option lists have rounded rows.
- `dotnet run` is started with `--urls` so the API listens on the configured address instead of the one in `launchSettings.json`.
- The README (English and Spanish) and CONTRIBUTING describe the new features, the design rules and the release steps.

### Fixed
- The terminal no longer freezes the window with a large amount of output: it is sent in short slices and only as fast as the page draws it.
- Closing the tray menu or the window no longer shows "Cannot ... while a Window is closing".
- A phone that adb had already connected by itself was reported as not found.

## [0.2.0] - 2026-10-27

### Added
- Project profiles: Node (NestJS, Express, Fastify, Next.js, Nuxt, SvelteKit, Create React App, Vue CLI, Vite with React, Vue or Svelte) and Python (Django, FastAPI, Flask) are recognised from plain JSON files. Personal profiles go in `%APPDATA%\stackdev\profiles\` and are tried first. See `CONTRIBUTING.md` and `examples/profiles/go.json`.
- Credentials in the project panel can be reordered by dragging them.
- `examples/` with a complete `dev-menu.json`.

### Fixed
- The command line crashed at start-up because an option template used a `:`; every `stackdev` command works again.
- The last lines of a captured process could be lost.

### Changed
- All code comments are in English.
- The themed tray menu and open menu replace the Windows ones.

## [0.1.0] - 2026-10-24

First public release: local development panel and command line, and deployment of .NET APIs to your own server over SSH (upload with rollback, create an API, edit its `.env`, trial start, diagnose and repair the public address).
