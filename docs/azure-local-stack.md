# Optional Azure-compatible local stack

Linkuity is local-first. Azure Blob Storage is an **optional** artifact-store adapter,
not the default runtime — you never need it for local or private-server use. This page
is only relevant if you are developing or testing the Azure-compatible path.

For local development of that path, .NET Aspire can orchestrate the API together with
the Azurite blob emulator, so you don't need a real Azure account.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) running locally

## Start the stack

```powershell
dotnet run --project src/Linkuity.AppHost
```

Aspire starts the Azure Storage blob emulator (Azurite) in Docker, waits for it to be
healthy, and launches the API. The Aspire dashboard opens at `http://localhost:15888`.

Verify the API:

```powershell
curl http://localhost:5017/health
```

Azure mode only changes **where job artifacts are stored** (Azure Blob Storage /
Azurite instead of the local filesystem). It does not change the matching pipeline. The
adapter is selected with `Linkuity:RuntimeMode=Azure`. See
[architecture.md](architecture.md#optional-azure-artifact-store-adapter) for details.
