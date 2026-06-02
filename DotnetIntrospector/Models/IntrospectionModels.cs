namespace DotnetIntrospector;

public sealed record IntrospectionRequest(string Version);

public sealed record PersistAllIntrospectionRequest(
    IReadOnlyList<string>? Versions,
    bool ForceRefresh = false);

public sealed record PersistAllIntrospectionFailure(
    string Version,
    string Error);

public sealed record PersistAllIntrospectionResult(
    string DatabaseTarget,
    int RequestedVersions,
    int SavedVersions,
    IReadOnlyList<string> Saved,
    IReadOnlyList<PersistAllIntrospectionFailure> Failed,
    DateTimeOffset CompletedAt);

public sealed record RiskAnnotationUpsertRequest(
    string Version,
    string AssemblyPath,
    string TypeFullName,
    string ItemKind,
    string? ItemName,
    bool IsRisky);

public sealed record RiskAnnotationItem(
    string Version,
    string AssemblyPath,
    string TypeFullName,
    string ItemKind,
    string ItemName,
    bool IsRisky,
    DateTimeOffset UpdatedAt);

public sealed record IntrospectionResult(
    string Version,
    string Image,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<string> RuntimeFolders,
    IReadOnlyList<AssemblyNamespaceInfo> Assemblies,
    IReadOnlyList<string> Warnings);

public sealed record AssemblyNamespaceInfo(
    string AssemblyName,
    string AssemblyDll,
    string AssemblyPath,
    string RuntimeFolder,
    int NamespaceCount,
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<TypeDetails> Types);

public sealed record TypeDetails(
    string TypeId,
    string FullName,
    string Namespace,
    string Name,
    IReadOnlyList<string> Methods,
    IReadOnlyList<string> Properties);

public sealed record CweLookupResponse(
    DateTimeOffset FetchedAt,
    IReadOnlyList<CweItem> Items,
    IReadOnlyList<string> Warnings);

public sealed record CweItem(
    int Id,
    string Name,
    string Description,
    string SourceUrl);

public sealed record CweUpsertRequest(
    int Id,
    string Name,
    string Description,
    string SourceUrl);

public sealed record StoredCweItem(
    int Id,
    string Name,
    string Description,
    string SourceUrl,
    bool IsDeleted,
    DateTimeOffset UpdatedAt);
