using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace DotnetIntrospector.Services;

public sealed class DockerIntrospectionService
{
    private static readonly Regex VersionRegex = new("^\\d+(\\.\\d+)?$", RegexOptions.Compiled);
    private static readonly Lazy<string> ContainerCli = new(ResolveContainerCli);
    private readonly ConcurrentDictionary<string, IntrospectionResult> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IntrospectionResult> IntrospectAsync(string version, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (!VersionRegex.IsMatch(version))
        {
            throw new ArgumentException("Version format is invalid. Use values like 8.0, 9.0, or 10.0.");
        }

        if (!forceRefresh && _cache.TryGetValue(version, out var cached))
        {
            return cached;
        }

        var image = $"mcr.microsoft.com/dotnet/runtime:{version}";
        var extractionRoot = Path.Combine(Path.GetTempPath(), "dotnet-introspector", version, Guid.NewGuid().ToString("N"));
        var containerName = $"dotnet-introspector-{Guid.NewGuid():N}";
        Directory.CreateDirectory(extractionRoot);

        try
        {
            await RunContainerCliAsync(args =>
            {
                args.Add("pull");
                args.Add(image);
            }, cancellationToken);

            await RunContainerCliAsync(args =>
            {
                args.Add("create");
                args.Add("--name");
                args.Add(containerName);
                args.Add(image);
                args.Add("sh");
            }, cancellationToken);

            var sharedDestination = Path.Combine(extractionRoot, "shared");
            Directory.CreateDirectory(sharedDestination);

            await RunContainerCliAsync(args =>
            {
                args.Add("cp");
                args.Add($"{containerName}:/usr/share/dotnet/shared");
                args.Add(sharedDestination);
            }, cancellationToken);

            var copiedRoot = Path.Combine(sharedDestination, "shared");
            var runtimeFolders = Directory.Exists(copiedRoot)
                ? Directory.GetDirectories(copiedRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();

            var assemblyItems = new List<AssemblyNamespaceInfo>();
            var warnings = new List<string>();

            foreach (var runtimeFolder in runtimeFolders)
            {
                var runtimeLabel = Path.GetFileName(runtimeFolder);
                var assemblyFiles = Directory.GetFiles(runtimeFolder, "*.dll", SearchOption.AllDirectories);

                foreach (var assemblyPath in assemblyFiles)
                {
                    try
                    {
                        var relativeAssemblyPath = Path.GetRelativePath(copiedRoot, assemblyPath).Replace('\\', '/');
                        var containerAssemblyPath = $"/usr/share/dotnet/shared/{relativeAssemblyPath}";
                        var assemblyDll = Path.GetFileName(assemblyPath);

                        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters
                        {
                            ReadSymbols = false,
                            ReadingMode = ReadingMode.Deferred
                        });

                        var flatNamespaces = assembly.MainModule.Types
                            .Where(type => !type.IsSpecialName && !string.IsNullOrWhiteSpace(type.Namespace))
                            .Select(type => type.Namespace)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(ns => ns, StringComparer.Ordinal)
                            .ToArray();

                        var typeDetails = assembly.MainModule.Types
                            .Where(type =>
                                !type.IsSpecialName &&
                                !type.IsNested &&
                                !string.IsNullOrWhiteSpace(type.Namespace) &&
                                !type.Name.StartsWith("<", StringComparison.Ordinal))
                            .Select(type =>
                            {
                                var fullTypeName = $"{type.Namespace}.{type.Name}";
                                var typeId = $"{containerAssemblyPath}|{fullTypeName}";

                                return new TypeDetails(
                                typeId,
                                fullTypeName,
                                type.Namespace,
                                type.Name,
                                type.Methods
                                    .Where(method =>
                                        !method.IsConstructor &&
                                        !method.IsGetter &&
                                        !method.IsSetter &&
                                        !method.IsAddOn &&
                                        !method.IsRemoveOn &&
                                        !method.IsSpecialName)
                                    .Select(FormatMethodSignature)
                                    .Distinct(StringComparer.Ordinal)
                                    .OrderBy(name => name, StringComparer.Ordinal)
                                    .ToArray(),
                                type.Properties
                                    .Select(property => property.Name)
                                    .Distinct(StringComparer.Ordinal)
                                    .OrderBy(name => name, StringComparer.Ordinal)
                                    .ToArray());
                            })
                            .OrderBy(type => type.FullName, StringComparer.Ordinal)
                            .ToArray();

                        assemblyItems.Add(new AssemblyNamespaceInfo(
                            assembly.Name?.Name ?? Path.GetFileNameWithoutExtension(assemblyPath),
                            assemblyDll,
                            containerAssemblyPath,
                            runtimeLabel,
                            flatNamespaces.Length,
                            flatNamespaces,
                            typeDetails));
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Could not read '{Path.GetFileName(assemblyPath)}': {ex.Message}");
                    }
                }
            }

            var result = new IntrospectionResult(
                version,
                image,
                DateTimeOffset.UtcNow,
                runtimeFolders.Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().ToArray(),
                assemblyItems
                    .OrderBy(item => item.RuntimeFolder, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.AssemblyName, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                warnings);

            _cache[version] = result;
            return result;
        }
        finally
        {
            try
            {
                await RunContainerCliAsync(args =>
                {
                    args.Add("rm");
                    args.Add("-f");
                    args.Add(containerName);
                }, CancellationToken.None, throwOnFailure: false);
            }
            catch
            {
                // Ignore cleanup failures.
            }

            try
            {
                if (Directory.Exists(extractionRoot))
                {
                    Directory.Delete(extractionRoot, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }
    }

    private static string FormatMethodSignature(MethodDefinition method)
    {
        var genericSuffix = method.HasGenericParameters
            ? $"<{string.Join(", ", method.GenericParameters.Select(parameter => parameter.Name))}>"
            : string.Empty;

        var parameters = string.Join(
            ", ",
            method.Parameters.Select(parameter =>
            {
                var typeName = FormatTypeName(parameter.ParameterType);
                return string.IsNullOrWhiteSpace(parameter.Name)
                    ? typeName
                    : $"{typeName} {parameter.Name}";
            }));

        return $"{FormatTypeName(method.ReturnType)} {method.Name}{genericSuffix}({parameters})";
    }

    private static string FormatTypeName(TypeReference type)
    {
        return type switch
        {
            GenericParameter genericParameter => genericParameter.Name,
            ByReferenceType byReference => $"{FormatTypeName(byReference.ElementType)}&",
            PointerType pointer => $"{FormatTypeName(pointer.ElementType)}*",
            ArrayType array => $"{FormatTypeName(array.ElementType)}[{new string(',', array.Rank - 1)}]",
            GenericInstanceType genericInstance =>
                $"{FormatNamespaceQualifiedName(genericInstance.Namespace, StripArity(genericInstance.Name))}<{string.Join(", ", genericInstance.GenericArguments.Select(FormatTypeName))}>",
            OptionalModifierType optionalModifier => FormatTypeName(optionalModifier.ElementType),
            RequiredModifierType requiredModifier => FormatTypeName(requiredModifier.ElementType),
            _ => FormatNamespaceQualifiedName(type.Namespace, StripArity(type.Name))
        };
    }

    private static string FormatNamespaceQualifiedName(string? ns, string typeName)
    {
        return string.IsNullOrWhiteSpace(ns) ? typeName : $"{ns}.{typeName}";
    }

    private static string StripArity(string typeName)
    {
        var markerIndex = typeName.IndexOf('`', StringComparison.Ordinal);
        return markerIndex >= 0 ? typeName[..markerIndex] : typeName;
    }

    private static async Task RunContainerCliAsync(Action<ICollection<string>> buildArgs, CancellationToken cancellationToken, bool throwOnFailure = true)
    {
        var cli = ContainerCli.Value;
        var processStartInfo = new ProcessStartInfo
        {
            FileName = cli,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var args = new List<string>();
        buildArgs(args);
        foreach (var arg in args)
        {
            processStartInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException($"Failed to start {cli} process.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode == 0 || !throwOnFailure)
        {
            return;
        }

        throw new InvalidOperationException($"{cli} {string.Join(' ', args)} failed with exit code {process.ExitCode}: {stdErr}{Environment.NewLine}{stdOut}".Trim());
    }

    private static string ResolveContainerCli()
    {
        var configuredCli = Environment.GetEnvironmentVariable("CONTAINER_CLI");
        if (!string.IsNullOrWhiteSpace(configuredCli))
        {
            return configuredCli;
        }

        if (CanRunCli("docker"))
        {
            return "docker";
        }

        throw new InvalidOperationException("No supported container CLI found. Install docker or set CONTAINER_CLI to a valid executable.");
    }

    private static bool CanRunCli(string cli)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = cli,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "--version" }
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
