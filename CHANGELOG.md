# Changelog

All notable changes to this project are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/) and the project uses [Semantic Versioning](https://semver.org/).

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
