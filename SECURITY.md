# Security policy

stackdev connects to servers over SSH and handles keys and passwords, so security reports matter.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private reporting: the **Security** tab of the repository, then **Report a vulnerability**. Include what you found, how to reproduce it and what it could allow.

You will get an answer as soon as possible; this is a volunteer project, so please allow a few days.

## What counts

- Secrets showing up in a log, a file in the repository or a package that is uploaded.
- A way around the pinned SSH host key or the publish guard.
- Command injection through values that reach the remote script (service names, domains, file names).
- Weak handling of the local encrypted files.

## Supported versions

Only the latest release and the `develop` branch receive fixes.
