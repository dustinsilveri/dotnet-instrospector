using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace DotnetIntrospector.Services;

public sealed class IntrospectionPersistenceService
{
    private const int MaxConnectionAttempts = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IReadOnlyList<string> _connectionStrings;
    private readonly IReadOnlyList<string> _masterConnectionStrings;
    private readonly string _databaseName;
    private readonly string _databaseTarget;

    public IntrospectionPersistenceService(IConfiguration configuration)
    {
        var configuredConnectionString = configuration["Persistence:ConnectionString"]
            ?? "Server=localhost,1433;Database=introspector;User Id=sa;Password=SuperCr@zyPa55word;TrustServerCertificate=True;Encrypt=True";

        var builder = new SqlConnectionStringBuilder(configuredConnectionString);
        if (builder.ConnectTimeout <= 0 || builder.ConnectTimeout > 5)
        {
            builder.ConnectTimeout = 5;
        }

        _databaseName = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "introspector" : builder.InitialCatalog;
        _connectionStrings = BuildConnectionStringCandidates(builder);

        var masterBuilder = new SqlConnectionStringBuilder(builder.ConnectionString)
        {
            InitialCatalog = "master"
        };

        _masterConnectionStrings = BuildConnectionStringCandidates(masterBuilder);
        _databaseTarget = $"MSSQL/{string.Join(" | ", _connectionStrings.Select(GetDataSource))}/{_databaseName}";
    }

    public string DatabaseTarget => _databaseTarget;

    public async Task SaveSnapshotAsync(IntrospectionResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureDatabaseAndSchemaAsync(cancellationToken);

        await using var connection = await OpenTargetConnectionAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE IntrospectionSnapshots
            SET
                Image = @image,
                GeneratedAt = @generatedAt,
                RuntimeFoldersJson = @runtimeFoldersJson,
                WarningsJson = @warningsJson,
                AssembliesJson = @assembliesJson,
                SavedAt = @savedAt
            WHERE Version = @version;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO IntrospectionSnapshots (
                    Version,
                    Image,
                    GeneratedAt,
                    RuntimeFoldersJson,
                    WarningsJson,
                    AssembliesJson,
                    SavedAt
                )
                VALUES (
                    @version,
                    @image,
                    @generatedAt,
                    @runtimeFoldersJson,
                    @warningsJson,
                    @assembliesJson,
                    @savedAt
                );
            END;
            """;

        command.Parameters.AddWithValue("@version", result.Version);
        command.Parameters.AddWithValue("@image", result.Image);
        command.Parameters.AddWithValue("@generatedAt", result.GeneratedAt.UtcDateTime);
        command.Parameters.AddWithValue("@runtimeFoldersJson", JsonSerializer.Serialize(result.RuntimeFolders, JsonOptions));
        command.Parameters.AddWithValue("@warningsJson", JsonSerializer.Serialize(result.Warnings, JsonOptions));
        command.Parameters.AddWithValue("@assembliesJson", JsonSerializer.Serialize(result.Assemblies, JsonOptions));
        command.Parameters.AddWithValue("@savedAt", DateTimeOffset.UtcNow.UtcDateTime);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RiskAnnotationItem>> GetRiskAnnotationsAsync(string version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureDatabaseAndSchemaAsync(cancellationToken);

        await using var connection = await OpenTargetConnectionAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                Version,
                AssemblyPath,
                TypeFullName,
                ItemKind,
                ItemName,
                IsRisky,
                UpdatedAt
            FROM RiskAnnotations
            WHERE Version = @version;
            """;

        command.Parameters.AddWithValue("@version", version);

        var items = new List<RiskAnnotationItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new RiskAnnotationItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.GetDateTimeOffset(6)));
        }

        return items;
    }

    public async Task SetRiskAnnotationAsync(RiskAnnotationUpsertRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var itemName = request.ItemName?.Trim() ?? string.Empty;

        await EnsureDatabaseAndSchemaAsync(cancellationToken);

        await using var connection = await OpenTargetConnectionAsync(cancellationToken);

        if (request.IsRisky)
        {
            var upsertCommand = connection.CreateCommand();
            upsertCommand.CommandText =
                """
                UPDATE RiskAnnotations
                SET
                    IsRisky = 1,
                    UpdatedAt = @updatedAt
                WHERE
                    Version = @version
                    AND AssemblyPath = @assemblyPath
                    AND TypeFullName = @typeFullName
                    AND ItemKind = @itemKind
                    AND ItemName = @itemName;

                IF @@ROWCOUNT = 0
                BEGIN
                    INSERT INTO RiskAnnotations (
                        Version,
                        AssemblyPath,
                        TypeFullName,
                        ItemKind,
                        ItemName,
                        IsRisky,
                        UpdatedAt
                    )
                    VALUES (
                        @version,
                        @assemblyPath,
                        @typeFullName,
                        @itemKind,
                        @itemName,
                        1,
                        @updatedAt
                    );
                END;
                """;

            upsertCommand.Parameters.AddWithValue("@version", request.Version);
            upsertCommand.Parameters.AddWithValue("@assemblyPath", request.AssemblyPath);
            upsertCommand.Parameters.AddWithValue("@typeFullName", request.TypeFullName);
            upsertCommand.Parameters.AddWithValue("@itemKind", request.ItemKind);
            upsertCommand.Parameters.AddWithValue("@itemName", itemName);
            upsertCommand.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.UtcDateTime);

            await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        var deleteCommand = connection.CreateCommand();
        deleteCommand.CommandText =
            """
            DELETE FROM RiskAnnotations
            WHERE Version = @version
                AND AssemblyPath = @assemblyPath
                AND TypeFullName = @typeFullName
                AND ItemKind = @itemKind
                AND ItemName = @itemName;
            """;

        deleteCommand.Parameters.AddWithValue("@version", request.Version);
        deleteCommand.Parameters.AddWithValue("@assemblyPath", request.AssemblyPath);
        deleteCommand.Parameters.AddWithValue("@typeFullName", request.TypeFullName);
        deleteCommand.Parameters.AddWithValue("@itemKind", request.ItemKind);
        deleteCommand.Parameters.AddWithValue("@itemName", itemName);

        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StoredCweItem>> GetCweItemsAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ids.Count == 0)
        {
            return Array.Empty<StoredCweItem>();
        }

        await EnsureDatabaseAndSchemaAsync(cancellationToken);

        await using var connection = await OpenTargetConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();

        var parameterNames = ids.Select((id, index) =>
        {
            var parameterName = $"@id{index}";
            command.Parameters.AddWithValue(parameterName, id);
            return parameterName;
        }).ToArray();

        command.CommandText =
            $"""
            SELECT
                Id,
                Name,
                Description,
                SourceUrl,
                IsDeleted,
                UpdatedAt
            FROM CweCatalog
            WHERE Id IN ({string.Join(", ", parameterNames)});
            """;

        var items = new List<StoredCweItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new StoredCweItem(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetDateTimeOffset(5)));
        }

        return items;
    }

    public async Task UpsertCweItemAsync(CweItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureDatabaseAndSchemaAsync(cancellationToken);

        await using var connection = await OpenTargetConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE CweCatalog
            SET
                Name = @name,
                Description = @description,
                SourceUrl = @sourceUrl,
                IsDeleted = 0,
                UpdatedAt = @updatedAt
            WHERE Id = @id;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO CweCatalog (
                    Id,
                    Name,
                    Description,
                    SourceUrl,
                    IsDeleted,
                    UpdatedAt
                )
                VALUES (
                    @id,
                    @name,
                    @description,
                    @sourceUrl,
                    0,
                    @updatedAt
                );
            END;
            """;

        command.Parameters.AddWithValue("@id", item.Id);
        command.Parameters.AddWithValue("@name", item.Name);
        command.Parameters.AddWithValue("@description", item.Description);
        command.Parameters.AddWithValue("@sourceUrl", item.SourceUrl);
        command.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.UtcDateTime);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteCweItemAsync(int id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureDatabaseAndSchemaAsync(cancellationToken);

        await using var connection = await OpenTargetConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE CweCatalog
            SET
                IsDeleted = 1,
                UpdatedAt = @updatedAt
            WHERE Id = @id;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO CweCatalog (
                    Id,
                    Name,
                    Description,
                    SourceUrl,
                    IsDeleted,
                    UpdatedAt
                )
                VALUES (
                    @id,
                    @name,
                    @description,
                    @sourceUrl,
                    1,
                    @updatedAt
                );
            END;
            """;

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@name", $"CWE-{id}");
        command.Parameters.AddWithValue("@description", string.Empty);
        command.Parameters.AddWithValue("@sourceUrl", $"https://cwe.mitre.org/data/definitions/{id}.html");
        command.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.UtcDateTime);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenTargetConnectionAsync(CancellationToken cancellationToken)
    {
        return await OpenConnectionWithRetryAsync(_connectionStrings, cancellationToken);
    }

    private async Task EnsureDatabaseAndSchemaAsync(CancellationToken cancellationToken)
    {
        await EnsureDatabaseAsync(cancellationToken);

        await using var connection = await OpenTargetConnectionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
    }

    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionWithRetryAsync(_masterConnectionStrings, cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            IF DB_ID(@databaseName) IS NULL
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'CREATE DATABASE [' + REPLACE(@databaseName, ']', ']]') + N']';
                EXEC(@sql);
            END;
            """;
        command.Parameters.AddWithValue("@databaseName", _databaseName);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<string> BuildConnectionStringCandidates(SqlConnectionStringBuilder template)
    {
        var candidates = new List<string>();
        var (host, suffix) = SplitDataSource(template.DataSource);
        var normalizedHost = host.ToLowerInvariant();

        var hosts = new List<string> { host };

        if (normalizedHost is "localhost" or "127.0.0.1" or "mssql")
        {
            hosts.Add("localhost");
            hosts.Add("127.0.0.1");
            hosts.Add("mssql");
        }

        foreach (var candidateHost in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var builder = new SqlConnectionStringBuilder(template.ConnectionString)
            {
                DataSource = $"{candidateHost}{suffix}"
            };

            candidates.Add(builder.ConnectionString);
        }

        return candidates;
    }

    private static (string Host, string Suffix) SplitDataSource(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return ("localhost", ",1433");
        }

        var commaIndex = dataSource.IndexOf(',');
        if (commaIndex < 0)
        {
            return (dataSource, string.Empty);
        }

        return (dataSource[..commaIndex], dataSource[commaIndex..]);
    }

    private static string GetDataSource(string connectionString)
    {
        return new SqlConnectionStringBuilder(connectionString).DataSource;
    }

    private static async Task<SqlConnection> OpenConnectionWithRetryAsync(
        IReadOnlyList<string> connectionStrings,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxConnectionAttempts; attempt += 1)
        {
            foreach (var connectionString in connectionStrings)
            {
                try
                {
                    var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync(cancellationToken);
                    return connection;
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    lastError = ex;
                }
            }

            if (attempt < MaxConnectionAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Unable to connect to SQL Server using configured endpoints: {string.Join(", ", connectionStrings.Select(GetDataSource))}.",
            lastError);
    }

    private static async Task EnsureSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            """
            IF OBJECT_ID(N'dbo.IntrospectionSnapshots', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.IntrospectionSnapshots (
                    Version NVARCHAR(64) NOT NULL PRIMARY KEY,
                    Image NVARCHAR(256) NOT NULL,
                    GeneratedAt DATETIMEOFFSET(7) NOT NULL,
                    RuntimeFoldersJson NVARCHAR(MAX) NOT NULL,
                    WarningsJson NVARCHAR(MAX) NOT NULL,
                    AssembliesJson NVARCHAR(MAX) NOT NULL,
                    SavedAt DATETIMEOFFSET(7) NOT NULL
                );
            END;

            IF OBJECT_ID(N'dbo.RiskAnnotations', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.RiskAnnotations (
                    Version NVARCHAR(64) NOT NULL,
                    AssemblyPath NVARCHAR(1024) NOT NULL,
                    TypeFullName NVARCHAR(1024) NOT NULL,
                    ItemKind NVARCHAR(32) NOT NULL,
                    ItemName NVARCHAR(2048) NOT NULL,
                    IsRisky BIT NOT NULL,
                    UpdatedAt DATETIMEOFFSET(7) NOT NULL,
                    CONSTRAINT PK_RiskAnnotations PRIMARY KEY (Version, AssemblyPath, TypeFullName, ItemKind, ItemName)
                );
            END;

            IF OBJECT_ID(N'dbo.CweCatalog', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.CweCatalog (
                    Id INT NOT NULL PRIMARY KEY,
                    Name NVARCHAR(1024) NOT NULL,
                    Description NVARCHAR(MAX) NOT NULL,
                    SourceUrl NVARCHAR(2048) NOT NULL,
                    IsDeleted BIT NOT NULL,
                    UpdatedAt DATETIMEOFFSET(7) NOT NULL
                );
            END;
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
