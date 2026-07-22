# HTTP API

Linkuity ships an ASP.NET Core Web API that runs a batch match **synchronously**: post
a CSV plus a match configuration and get the merged golden records back in the response
body. It shares the same normalize → match → cluster → merge pipeline as the CLI.

For the full endpoint list (including the durable project/source/batch metadata
endpoints) and request/response shapes, see
[architecture.md](architecture.md#http-api).

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| POST | `/run` | Run a batch match synchronously. Multipart form: `config` (JSON text field) + `file` (`text/csv`). Returns golden records as `text/csv`. Max input 400 KiB. |
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

Run a synchronous batch match. Note `config` is sent as a **text form field** (`<`),
not a file upload (`@`):

```powershell
curl.exe -X POST http://localhost:5017/run `
  -F "config=<samples/people-multi-source/match-config.json;type=application/json" `
  -F "file=@samples/people-multi-source/sample.csv;type=text/csv" `
  -o golden-records.csv
```

`golden-records.csv` now contains the 10 merged golden records for the bundled sample.
