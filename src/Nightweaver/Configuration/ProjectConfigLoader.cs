using System.Text;
using System.Text.Json;

namespace Nightweaver.Configuration;

internal static class ProjectConfigLoader
{
    internal static ProjectConfig Load(string configFilePath, ProjectSlug? expectedName, HostPaths hostPaths)
    {
        string fullConfigPath;
        try
        {
            fullConfigPath = Path.GetFullPath(configFilePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectConfigurationException(exception.Message, null, configFilePath);
        }

        string json;
        try
        {
            json = File.ReadAllText(fullConfigPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            var detail = expectedName is null
                ? "Project configuration not found"
                : $"Project configuration not found; expected path '{fullConfigPath}' in projects directory '{hostPaths.ProjectsDirectory}'";
            throw new ProjectConfigurationException(detail, null, fullConfigPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new ProjectConfigurationException(exception.Message, null, fullConfigPath);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        catch (JsonException exception)
        {
            var line = (exception.LineNumber ?? 0) + 1;
            var bytePosition = exception.BytePositionInLine ?? 0;
            throw new ProjectConfigurationException(
                $"malformed JSON at line {line}, byte position {bytePosition}: {exception.Message}",
                "$",
                fullConfigPath);
        }

        using (document)
        {
            RejectDuplicateProperties(document.RootElement, "$", fullConfigPath);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Error("must be a JSON object", "$", fullConfigPath);
            }

            var root = document.RootElement;
            var nameElement = RequiredProperty(root, "name", "$.name", fullConfigPath);
            if (nameElement.ValueKind != JsonValueKind.String)
            {
                throw Error("must be a string", "$.name", fullConfigPath);
            }

            var nameText = nameElement.GetString()!;
            if (!ProjectSlug.TryParse(nameText, out var name, out var reason))
            {
                throw Error($"invalid project slug '{nameText}': {reason}", "$.name", fullConfigPath);
            }

            if (expectedName is { } expected && !string.Equals(name.Value, expected.Value, StringComparison.Ordinal))
            {
                throw Error($"name '{name.Value}' does not match project slug '{expected.Value}'", "$.name", fullConfigPath);
            }

            var targetElement = RequiredProperty(root, "target_repository", "$.target_repository", fullConfigPath);
            if (targetElement.ValueKind != JsonValueKind.Object)
            {
                throw Error("must be a JSON object", "$.target_repository", fullConfigPath);
            }

            var targetPathElement = RequiredProperty(targetElement, "path", "$.target_repository.path", fullConfigPath);
            if (targetPathElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(targetPathElement.GetString()))
            {
                throw Error("must be a non-blank string", "$.target_repository.path", fullConfigPath);
            }

            string targetPath;
            try
            {
                var configDirectory = Path.GetDirectoryName(fullConfigPath)!;
                targetPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPathElement.GetString()!, configDirectory));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw Error($"contains an invalid path: {exception.Message}", "$.target_repository.path", fullConfigPath);
            }

            var defaultBranch = "main";
            if (targetElement.TryGetProperty("default_branch", out var branchElement))
            {
                if (branchElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(branchElement.GetString()))
                {
                    throw Error("must be a non-blank string", "$.target_repository.default_branch", fullConfigPath);
                }

                defaultBranch = branchElement.GetString()!;
            }

            EnsureHostOutsideTarget(hostPaths.Root, targetPath, fullConfigPath);

            // ns-sfl.2: strict schema validation
            return new ProjectConfig(name, fullConfigPath, new TargetRepositoryConfig(targetPath, defaultBranch));
        }
    }

    private static JsonElement RequiredProperty(JsonElement element, string propertyName, string jsonPath, string configFilePath)
    {
        if (element.TryGetProperty(propertyName, out var value))
        {
            return value;
        }

        throw Error("is required", jsonPath, configFilePath);
    }

    private static void RejectDuplicateProperties(JsonElement element, string jsonPath, string configFilePath)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = AppendPropertyPath(jsonPath, property.Name);
                if (!names.Add(property.Name))
                {
                    throw Error($"duplicate property '{property.Name}'", propertyPath, configFilePath);
                }

                RejectDuplicateProperties(property.Value, propertyPath, configFilePath);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{jsonPath}[{index}]", configFilePath);
                index++;
            }
        }
    }

    private static string AppendPropertyPath(string path, string propertyName) =>
        propertyName.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            ? $"{path}.{propertyName}"
            : $"{path}['{propertyName.Replace("'", "\\'", StringComparison.Ordinal)}']";

    private static void EnsureHostOutsideTarget(string hostRoot, string targetPath, string configFilePath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var targetPrefix = Path.EndsInDirectorySeparator(targetPath)
            ? targetPath
            : targetPath + Path.DirectorySeparatorChar;
        if (string.Equals(hostRoot, targetPath, comparison) || hostRoot.StartsWith(targetPrefix, comparison))
        {
            throw Error(
                $"Nightweaver host directory '{hostRoot}' must be outside the Target Repository (SPEC 1.4)",
                "$.target_repository.path",
                configFilePath);
        }
    }

    private static ProjectConfigurationException Error(string message, string? jsonPath, string configFilePath) =>
        new(message, jsonPath, configFilePath);
}
