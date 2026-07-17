# Security Policy

Linkuity processes personally identifiable information (PII) such as names,
emails, phone numbers, and postal addresses during entity resolution. We take
the security of the project and of the data our users entrust to it seriously.

## Supported Versions

Linkuity is currently in **beta** (pre-1.0). Security fixes are applied to the
latest release on the `develop` branch. There is no long-term-support branch
yet; beta users should track the latest release.

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues,
pull requests, or discussions.**

Instead, report them privately using **GitHub's private vulnerability
reporting**:

1. Go to the repository's **Security** tab.
2. Click **Report a vulnerability**.
3. Provide a description of the issue, the affected version/commit, reproduction
   steps, and any potential impact.

This gives us a private channel to work with you on a fix before any public
disclosure.

### What to expect

- **Acknowledgement:** we aim to acknowledge a report within **3 business days**.
- **Assessment:** we will investigate and let you know whether the report is
  accepted, along with an expected timeline for a fix.
- **Disclosure:** we practice coordinated disclosure. Please give us a
  reasonable opportunity to release a fix before any public disclosure. We are
  happy to credit reporters who wish to be acknowledged.

## Scope

Reports are especially welcome for issues that could lead to:

- Unauthorized disclosure of ingested records or golden records.
- Injection (SQL, CSV, path traversal) via ingested data or configuration.
- Deserialization or configuration-loading flaws.
- Credential or connection-string exposure through logs or artifacts.

Local development defaults (for example the `postgres/postgres` credentials and
the well-known Azurite emulator key in `docker-compose.local.yml`) are intended
for local, non-production use only and are not considered vulnerabilities.
