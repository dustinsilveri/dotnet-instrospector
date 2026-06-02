# .NET Runtime Introspector

A small web app that:

1. Lets you choose a .NET runtime version tag.
2. Pulls `mcr.microsoft.com/dotnet/runtime:<version>` with Docker.
3. Copies runtime assemblies from the image.
4. Introspects assembly metadata to list namespaces.
5. Displays assemblies and namespaces in a searchable UI.
6. Provides a dedicated CWE lookup page backed by MITRE descriptions.

## Requirements

- .NET SDK 10 (or newer that can build `net10.0`)
- Docker CLI + Docker socket running
- SQL Server (when running outside compose)

## Run

```bash
docker compose up -d mssql
cd DotnetIntrospector
dotnet run
```

Open the URL shown in the console (default from launch settings is usually `http://localhost:5000` or similar).

When running the app locally, the default development connection string targets SQL Server on `localhost:1433`, so the `mssql` compose service should be running first.
Ensure the Docker daemon is running and the Docker socket is available (typically at `unix:///var/run/docker.sock`).

## Run With Docker Compose

```bash
docker compose up --build
```

Open `http://localhost:5053`.

To stop:

```bash
docker compose down
```

The compose setup mounts a Docker socket into the app container so introspection can pull and inspect runtime images.
Compose also starts SQL Server for persistence.

## API

- `GET /api/versions`
  - Returns selectable runtime versions.
- `POST /api/introspect`
  - Body:
    ```json
    { "version": "8.0" }
    ```
  - Pulls container image, inspects assemblies, returns namespaces.
- `GET /api/introspect/export-csv/{version}`
  - Exports a flattened CSV for the selected version.
  - Includes organized rows for assembly, namespace, class, method, and property elements.
- `POST /api/introspect/persist-all`
  - Body (optional):
    ```json
    {
      "versions": ["8.0", "9.0", "10.0"],
      "forceRefresh": false
    }
    ```
  - If no body or no versions are provided, the app introspects all supported versions.
  - Persists each version snapshot into SQL Server.
  - Upserts by version so repeated runs refresh existing rows.
- `GET /api/risky/{version}`
  - Returns saved risky annotations for the provided runtime version.
- `PUT /api/risky`
  - Body:
    ```json
    {
      "version": "10.0",
      "assemblyPath": "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Console.dll",
      "typeFullName": "System.Console",
      "itemKind": "method",
      "itemName": "System.Void WriteLine()",
      "isRisky": true
    }
    ```
  - Saves or clears a risky flag on a class, property, or method.
- `GET /api/cwe?ids=79,89,787`
  - Looks up MITRE CWE entries for the provided IDs.
  - Returns each CWE ID, name, description, and source URL.
  - Saves fetched entries into SQL Server for later reuse.
- `PUT /api/cwe/{id}`
  - Updates a persisted CWE entry.
- `DELETE /api/cwe/{id}`
  - Soft-deletes a persisted CWE entry so it stays removed on later lookups.

## CWE Page

- Open `/cwe.html` in the app.
- Enter one or more CWE IDs (comma, space, or newline separated).
- Click "Load CWEs" or press Ctrl+Enter to fetch entries from MITRE.
- The page loads MITRE-backed CWE names/descriptions into a table.
- Row edits and deletes are persisted in SQL Server immediately via API calls.
- **Page navigation/reload**: Previously viewed CWEs are automatically restored from localStorage, so you'll see your view persists.
- Click "Edit" on any CWE to modify name/description; click "Save" to persist or "Cancel" to discard.
- Click "Delete" to soft-delete (data stays removed even after app restart).

## Notes

- First run for a version can take longer because it pulls an image.
- Results are cached in-memory per app process for repeated requests.
- CWE data is fetched from MITRE on first lookup, then persisted to SQL Server for future access.
- Introspection uses `Mono.Cecil`, so it reads metadata without executing assemblies.
- Persistence stores one snapshot row per version in SQL Server (`IntrospectionSnapshots` table), with assemblies serialized as JSON in NVARCHAR columns.
- CWE persistence stores edited/deleted entries in SQL Server (`CweCatalog` table) and restores them on subsequent lookups.
- Configure connection string with `Persistence:ConnectionString`.
