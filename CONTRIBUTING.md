# Contributing to Linkuity

Thank you for your interest in contributing to Linkuity. This document explains
how to propose changes and the legal terms that apply to contributions.

## Contributor License Agreement (CLA) — required

**All contributors must sign the Linkuity Contributor License Agreement before
any contribution can be merged.** This applies to every contribution to the
source code, documentation, and other project materials, regardless of size.

- **Individuals** sign the Individual CLA.
- Contributors who are contributing **on behalf of an employer** (or whose
  employer may claim rights in the work) must ensure their employer signs the
  Corporate CLA, in addition to the individual signing the Individual CLA.

The agreement text is in [`CLA.md`](CLA.md). Signing the CLA lets the project
accept and redistribute your contribution under the project
[LICENSE](LICENSE) (Apache License 2.0) while confirming you have the right to
make the contribution. It does **not** change who owns the copyright in your
contribution — you retain ownership; you grant Linkuity a license.

### How to sign

Signing is handled automatically on your pull request by the **CLA Assistant**
GitHub Action:

1. Open your pull request.
2. If you have not signed yet, the bot comments on the PR and the CLA status
   check stays red.
3. Read [`CLA.md`](CLA.md), then post this exact comment on the pull request:

   > I have read the CLA Document and I hereby sign the CLA

4. The bot records your signature and turns the CLA check green. You only sign
   once — future pull requests are recognized automatically.

If you are contributing on behalf of an employer, make sure the Corporate CLA is
also in place (see [`CLA.md`](CLA.md)). Pull requests cannot be merged until the
CLA check passes.

## Code of Conduct

This project is governed by the [Code of Conduct](CODE_OF_CONDUCT.md). By
participating, you agree to uphold it.

## How to contribute

### Reporting bugs and requesting features

Open a GitHub issue. For bugs, include the Linkuity version/commit, your OS and
.NET SDK version, a minimal reproduction (a small CSV plus the match config is
ideal), and the observed vs. expected behavior.

For security issues, **do not open a public issue** — follow
[SECURITY.md](SECURITY.md).

### Development setup

Prerequisites:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (optional —
  used by the PostgreSQL integration/conformance tests via Testcontainers, and
  by the private-server Docker Compose path)

Build and test:

```powershell
dotnet build Linkuity.slnx -c Release -warnaserror
dotnet test Linkuity.slnx -c Release
```

Run the sample end-to-end batch job:

```powershell
dotnet run --project src/Linkuity.Cli -- run --input samples/people-multi-source/sample.csv --profile samples/people-multi-source/people-multi-source.profile.json --merge-policy samples/people-multi-source/people-multi-source.merge.json --output ./data/output/people-multi-source
```

See [`docs/how-matching-works.md`](docs/how-matching-works.md) to understand the
engine and [`docs/tutorials/`](docs/tutorials/) for hands-on guides.

### Pull requests

1. Fork the repository and create a topic branch off `develop`.
2. Keep changes focused; one logical change per pull request.
3. Add or update tests for any behavior change. New features and bug fixes
   should come with tests.
4. Ensure `dotnet build Linkuity.slnx -c Release -warnaserror` and
   `dotnet test Linkuity.slnx -c Release` both pass locally. CI runs the same
   checks (build with warnings-as-errors, full test suite including the
   PostgreSQL backend).
5. Match the style of the surrounding code. Keep comment density and naming
   idiomatic to the file you are editing.
6. Update relevant documentation (`README.md`, `docs/`) when behavior changes.
7. Open the pull request against `develop` and describe what changed and why.
   Confirm your CLA is signed.

### Commit and PR hygiene

- Write clear commit messages that explain the intent of the change.
- Reference related issues in the pull request description.
- Do not commit secrets, credentials, customer data, or build artifacts
  (`bin/`, `obj/`, and local data directories are gitignored).

## License of contributions

By submitting a contribution, you agree that it is licensed under the project's
[Apache License 2.0](LICENSE) and is subject to the terms of the signed
[CLA](CLA.md).
