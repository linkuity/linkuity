# Examples

End-to-end demos that resolve **real, public** data with Linkuity — larger and more
narrative than the [samples](../samples/README.md), each self-contained with its own
acquire → run → validate flow.

| Example | Sources | What it shows |
|---------|---------|---------------|
| [company-resolution](company-resolution/README.md) | SEC EDGAR + GLEIF | Resolve real companies across two public systems that share no identifier, using only names and addresses; validated against a held-out CIK/LEI crosswalk. |

Each example ships a `run-demo.ps1`; see its README for prerequisites and options.
