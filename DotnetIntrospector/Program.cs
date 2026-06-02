using DotnetIntrospector;
using DotnetIntrospector.Services;
using Microsoft.Data.SqlClient;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DockerIntrospectionService>();
builder.Services.AddSingleton<IntrospectionPersistenceService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<CweLookupService>();

var app = builder.Build();

var supportedRuntimeVersions = new[]
{
	"6.0",
	"7.0",
	"8.0",
	"9.0",
	"10.0"
};

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/versions", () =>
{
	return Results.Ok(supportedRuntimeVersions);
});

app.MapGet("/api/cwe", async (string? ids, CweLookupService cweLookupService, CancellationToken cancellationToken) =>
{
	if (string.IsNullOrWhiteSpace(ids))
	{
		return Results.BadRequest(new { error = "Provide at least one CWE ID via the ids query parameter." });
	}

	var parsedIds = ids
		.Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
		.Select(value => value.StartsWith("CWE-", StringComparison.OrdinalIgnoreCase) ? value[4..] : value)
		.Where(value => int.TryParse(value, out var parsed) && parsed > 0)
		.Select(int.Parse)
		.Distinct()
		.Take(100)
		.ToArray();

	if (parsedIds.Length == 0)
	{
		return Results.BadRequest(new { error = "No valid CWE IDs were found. Example: ids=79,89,787" });
	}

	var response = await cweLookupService.LookupAsync(parsedIds, cancellationToken);
	return Results.Ok(response);
});

app.MapPut("/api/cwe/{id:int}", async (int id, CweUpsertRequest request, CweLookupService cweLookupService, CancellationToken cancellationToken) =>
{
	if (id <= 0 || request.Id != id)
	{
		return Results.BadRequest(new { error = "The route ID must match the body ID." });
	}

	if (string.IsNullOrWhiteSpace(request.Name))
	{
		return Results.BadRequest(new { error = "Name is required." });
	}

	if (string.IsNullOrWhiteSpace(request.Description))
	{
		return Results.BadRequest(new { error = "Description is required." });
	}

	var sourceUrl = string.IsNullOrWhiteSpace(request.SourceUrl)
		? $"https://cwe.mitre.org/data/definitions/{id}.html"
		: request.SourceUrl.Trim();

	try
	{
		var item = new CweItem(id, request.Name.Trim(), request.Description.Trim(), sourceUrl);
		await cweLookupService.UpsertAsync(item, cancellationToken);
		return Results.Ok(item);
	}
	catch (SqlException ex)
	{
		return Results.Problem($"CWE persistence failed: {ex.Message}");
	}
	catch (Exception ex)
	{
		return Results.Problem($"CWE persistence failed: {ex.Message}");
	}
});

app.MapDelete("/api/cwe/{id:int}", async (int id, CweLookupService cweLookupService, CancellationToken cancellationToken) =>
{
	if (id <= 0)
	{
		return Results.BadRequest(new { error = "A positive CWE ID is required." });
	}

	try
	{
		await cweLookupService.DeleteAsync(id, cancellationToken);
		return Results.Ok(new { deleted = true, id });
	}
	catch (SqlException ex)
	{
		return Results.Problem($"CWE delete failed: {ex.Message}");
	}
	catch (Exception ex)
	{
		return Results.Problem($"CWE delete failed: {ex.Message}");
	}
});

app.MapPost("/api/introspect", async (IntrospectionRequest request, DockerIntrospectionService service, CancellationToken cancellationToken) =>
{
	if (string.IsNullOrWhiteSpace(request.Version))
	{
		return Results.BadRequest(new { error = "A .NET version is required." });
	}

	try
	{
		var result = await service.IntrospectAsync(request.Version.Trim(), cancellationToken);
		return Results.Ok(result);
	}
	catch (ArgumentException ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
	catch (InvalidOperationException ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
	catch (Exception ex)
	{
		return Results.Problem($"Introspection failed: {ex.Message}");
	}
});

app.MapGet("/api/introspect/export-csv/{version}", async (string version, DockerIntrospectionService service, CancellationToken cancellationToken) =>
{
	if (string.IsNullOrWhiteSpace(version))
	{
		return Results.BadRequest(new { error = "A .NET version is required." });
	}

	var normalizedVersion = version.Trim();
if (!supportedRuntimeVersions.Contains(normalizedVersion, StringComparer.OrdinalIgnoreCase))
	{
		return Results.BadRequest(new
		{
			error = $"Unsupported version: {normalizedVersion}",
			supportedVersions = supportedRuntimeVersions
		});
	}

	try
	{
		var result = await service.IntrospectAsync(normalizedVersion, cancellationToken);
		var csv = BuildIntrospectionCsv(result);
		var bytes = Encoding.UTF8.GetBytes(csv);
		var fileName = $"dotnet-introspection-{normalizedVersion.Replace('.', '-')}.csv";

		return Results.File(bytes, "text/csv; charset=utf-8", fileName);
	}
	catch (ArgumentException ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
	catch (InvalidOperationException ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
	catch (Exception ex)
	{
		return Results.Problem($"CSV export failed: {ex.Message}");
	}
});

app.MapPost(
	"/api/introspect/persist-all",
	async (
		PersistAllIntrospectionRequest? request,
		DockerIntrospectionService introspectionService,
		IntrospectionPersistenceService persistenceService,
		CancellationToken cancellationToken) =>
	{
		var requestedVersions = request?.Versions is { Count: > 0 }
			? request.Versions
				.Where(version => !string.IsNullOrWhiteSpace(version))
				.Select(version => version.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray()
			: supportedRuntimeVersions;

		var unsupportedVersions = requestedVersions
			.Where(version => !supportedRuntimeVersions.Contains(version, StringComparer.OrdinalIgnoreCase))
			.ToArray();

		if (unsupportedVersions.Length > 0)
		{
			return Results.BadRequest(new
			{
				error = $"Unsupported versions: {string.Join(", ", unsupportedVersions)}",
				supportedVersions = supportedRuntimeVersions
			});
		}

		var failures = new List<PersistAllIntrospectionFailure>();
		var savedVersions = new List<string>();

		foreach (var version in requestedVersions)
		{
			try
			{
				var introspection = await introspectionService.IntrospectAsync(version, cancellationToken, request?.ForceRefresh ?? false);
				await persistenceService.SaveSnapshotAsync(introspection, cancellationToken);
				savedVersions.Add(version);
			}
			catch (Exception ex)
			{
				failures.Add(new PersistAllIntrospectionFailure(version, ex.Message));
			}
		}

		var result = new PersistAllIntrospectionResult(
			persistenceService.DatabaseTarget,
			requestedVersions.Length,
			savedVersions.Count,
			savedVersions,
			failures,
			DateTimeOffset.UtcNow);

		return failures.Count == 0 ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status207MultiStatus);
	});

app.MapGet(
	"/api/risky/{version}",
	async (string version, IntrospectionPersistenceService persistenceService, CancellationToken cancellationToken) =>
	{
		try
		{
			if (string.IsNullOrWhiteSpace(version))
			{
				return Results.BadRequest(new { error = "A .NET version is required." });
			}

			var normalizedVersion = version.Trim();
			if (!supportedRuntimeVersions.Contains(normalizedVersion, StringComparer.OrdinalIgnoreCase))
			{
				return Results.BadRequest(new
				{
					error = $"Unsupported version: {normalizedVersion}",
					supportedVersions = supportedRuntimeVersions
				});
			}

			var items = await persistenceService.GetRiskAnnotationsAsync(normalizedVersion, cancellationToken);
			return Results.Ok(items);
		}
		catch (SqlException ex)
		{
			app.Logger.LogWarning(ex, "Risk annotation lookup unavailable for version {Version}. Returning empty result.", version);
			return Results.Ok(Array.Empty<RiskAnnotationItem>());
		}
		catch (Exception ex)
			{
				app.Logger.LogWarning(ex, "Risk annotation lookup failed for version {Version}. Returning empty result.", version);
				return Results.Ok(Array.Empty<RiskAnnotationItem>());
		}
	});

app.MapPut(
	"/api/risky",
	async (RiskAnnotationUpsertRequest request, IntrospectionPersistenceService persistenceService, CancellationToken cancellationToken) =>
	{
		try
		{
			if (string.IsNullOrWhiteSpace(request.Version))
			{
				return Results.BadRequest(new { error = "Version is required." });
			}

			if (string.IsNullOrWhiteSpace(request.AssemblyPath))
			{
				return Results.BadRequest(new { error = "AssemblyPath is required." });
			}

			if (string.IsNullOrWhiteSpace(request.TypeFullName))
			{
				return Results.BadRequest(new { error = "TypeFullName is required." });
			}

			if (string.IsNullOrWhiteSpace(request.ItemKind))
			{
				return Results.BadRequest(new { error = "ItemKind is required." });
			}

			var normalizedVersion = request.Version.Trim();
			if (!supportedRuntimeVersions.Contains(normalizedVersion, StringComparer.OrdinalIgnoreCase))
			{
				return Results.BadRequest(new
				{
					error = $"Unsupported version: {normalizedVersion}",
					supportedVersions = supportedRuntimeVersions
				});
			}

			var normalizedKind = request.ItemKind.Trim().ToLowerInvariant();
			if (normalizedKind is not ("class" or "property" or "method"))
			{
				return Results.BadRequest(new { error = "ItemKind must be class, property, or method." });
			}

			if (normalizedKind is "property" or "method" && string.IsNullOrWhiteSpace(request.ItemName))
			{
				return Results.BadRequest(new { error = "ItemName is required for property and method annotations." });
			}

			var normalizedRequest = request with
			{
				Version = normalizedVersion,
				AssemblyPath = request.AssemblyPath.Trim(),
				TypeFullName = request.TypeFullName.Trim(),
				ItemKind = normalizedKind,
				ItemName = request.ItemName?.Trim()
			};

			await persistenceService.SetRiskAnnotationAsync(normalizedRequest, cancellationToken);
			return Results.Ok(new { saved = true });
		}
		catch (SqlException ex)
		{
			return Results.Problem($"Risk annotation save failed: {ex.Message}");
		}
		catch (Exception ex)
			{
				return Results.Problem($"Risk annotation save failed: {ex.Message}");
		}
	});

app.Run();

static string BuildIntrospectionCsv(IntrospectionResult result)
{
	var builder = new StringBuilder();
	builder.AppendLine("Version,RuntimeFolder,AssemblyName,AssemblyDll,AssemblyPath,Namespace,TypeFullName,ElementType,ElementName");

	foreach (var assembly in result.Assemblies)
	{
		builder.AppendLine(ToCsvRow(
			result.Version,
			assembly.RuntimeFolder,
			assembly.AssemblyName,
			assembly.AssemblyDll,
			assembly.AssemblyPath,
			string.Empty,
			string.Empty,
			"assembly",
			assembly.AssemblyName));

		foreach (var ns in assembly.Namespaces)
		{
			builder.AppendLine(ToCsvRow(
				result.Version,
				assembly.RuntimeFolder,
				assembly.AssemblyName,
				assembly.AssemblyDll,
				assembly.AssemblyPath,
				ns,
				string.Empty,
				"namespace",
				ns));
		}

		foreach (var type in assembly.Types)
		{
			builder.AppendLine(ToCsvRow(
				result.Version,
				assembly.RuntimeFolder,
				assembly.AssemblyName,
				assembly.AssemblyDll,
				assembly.AssemblyPath,
				type.Namespace,
				type.FullName,
				"class",
				type.Name));

			foreach (var method in type.Methods)
			{
				builder.AppendLine(ToCsvRow(
					result.Version,
					assembly.RuntimeFolder,
					assembly.AssemblyName,
					assembly.AssemblyDll,
					assembly.AssemblyPath,
					type.Namespace,
					type.FullName,
					"method",
					method));
			}

			foreach (var property in type.Properties)
			{
				builder.AppendLine(ToCsvRow(
					result.Version,
					assembly.RuntimeFolder,
					assembly.AssemblyName,
					assembly.AssemblyDll,
					assembly.AssemblyPath,
					type.Namespace,
					type.FullName,
					"property",
					property));
			}
		}
	}

	return builder.ToString();
}

static string ToCsvRow(params string[] values)
{
	return string.Join(',', values.Select(EscapeCsv));
}

static string EscapeCsv(string? value)
{
	if (string.IsNullOrEmpty(value))
	{
		return string.Empty;
	}

	var needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
	if (!needsQuotes)
	{
		return value;
	}

	return $"\"{value.Replace("\"", "\"\"")}\"";
}
