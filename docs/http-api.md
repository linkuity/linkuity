# HTTP API

Linkuity ships an ASP.NET Core Web API that runs a batch match **synchronously**: post
a CSV plus a matching profile (and optional merge policy) and get the merged golden
records back in the response body. It shares the same normalize → match → cluster →
merge pipeline as the CLI.

For the full endpoint list (including the durable project/source/batch metadata
endpoints) and request/response shapes, see
[architecture.md](architecture.md#http-api). For the profile and merge-policy schema,
see [docs/configuration.md](configuration.md).

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| POST | `/run` | Run a batch match synchronously. Multipart form: `profile` (built-in name or profile JSON text) + `merge-policy` (optional, JSON text) + `file` (`text/csv`). Returns golden records as `text/csv`. Max input 400 KiB. |
| GET | `/health` | Health check (`200 OK`). |

The 400 KiB cap on `/run` exists because within-batch matching is O(n²)-ish; larger
inputs risk exceeding a synchronous request's time budget. Use the CLI (`linkuity run`)
for larger files. An asynchronous variant for large inputs is on the
[roadmap](roadmap/PLAN.md).

## Quick start

Start the API:

```powershell
dotnet run --project src/Linkuity.Api
```

Check health:

```powershell
curl http://localhost:5017/health
```

Run a synchronous batch match. Note `profile` and `merge-policy` are sent as **text
form fields** (`<`), not file uploads (`@`):

```powershell
curl.exe -X POST http://localhost:5017/run `
  -F "profile=<samples/people-multi-source/people-multi-source.profile.json" `
  -F "merge-policy=<samples/people-multi-source/people-multi-source.merge.json" `
  -F "file=@samples/people-multi-source/sample.csv;type=text/csv" `
  -o golden-records.csv
```

`profile` can equally be a built-in name (e.g. `-F "profile=organization"`) instead of
JSON text; `merge-policy` is optional — omit it for consensus merging.

`golden-records.csv` now contains the 10 merged golden records for the bundled sample.
