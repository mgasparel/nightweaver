using System.Globalization;
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
            return new ProjectConfigWalker(fullConfigPath, expectedName, hostPaths).Build(document.RootElement);
        }
    }

    private sealed class ProjectConfigWalker(string configFilePath, ProjectSlug? expectedName, HostPaths hostPaths)
    {
        private readonly List<ConfigurationError> errors = [];

        internal ProjectConfig Build(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                AddError("must be a JSON object", "$");
                ThrowIfInvalid();
            }

            ProjectSlug? name = null;
            TargetRepositoryConfig? targetRepository = null;
            var concurrency = new ConcurrencyConfig(
                ProjectConfigDefaults.MaxConcurrentPlanning,
                ProjectConfigDefaults.MaxConcurrentDelivery,
                ProjectConfigDefaults.MaxActivePlanBeads);
            var limits = new LimitsConfig(ProjectConfigDefaults.MaxRepairCycles, ProjectConfigDefaults.MaxOperationalRetries);
            var agentDefaultsOverride = AgentSettingsOverride.None;
            var roleOverrides = new Dictionary<AgentRole, AgentSettingsOverride>();
            var sawName = false;
            var sawTargetRepository = false;
            var rootNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in root.EnumerateObject())
            {
                var path = AppendPropertyPath("$", property.Name);
                if (!rootNames.Add(property.Name))
                {
                    AddError($"duplicate property '{property.Name}'", path);
                }

                switch (property.Name)
                {
                    case "name":
                        sawName = true;
                        name = ReadName(property.Value, path);
                        break;
                    case "target_repository":
                        sawTargetRepository = true;
                        targetRepository = ReadTargetRepository(property.Value, path);
                        break;
                    case "concurrency":
                        concurrency = ReadConcurrency(property.Value, path);
                        break;
                    case "limits":
                        limits = ReadLimits(property.Value, path);
                        break;
                    case "agent_defaults":
                        agentDefaultsOverride = ReadAgentSettings(property.Value, path);
                        break;
                    case "roles":
                        roleOverrides = ReadRoles(property.Value, path);
                        break;
                    default:
                        AddUnknownProperty(property, path);
                        break;
                }
            }

            if (!sawName)
            {
                AddError("is required", "$.name");
            }

            if (!sawTargetRepository)
            {
                AddError("is required", "$.target_repository");
            }

            var resolvedSettings = AgentSettingsResolver.Resolve(agentDefaultsOverride, roleOverrides);
            ThrowIfInvalid();

            return new ProjectConfig(
                name!.Value,
                configFilePath,
                targetRepository!,
                concurrency,
                limits,
                resolvedSettings.AgentDefaults,
                resolvedSettings.Roles);
        }

        private ProjectSlug? ReadName(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                AddError("must be a string", path);
                return null;
            }

            var value = element.GetString() ?? string.Empty;
            if (!ProjectSlug.TryParse(value, out var name, out var reason))
            {
                AddError($"invalid project slug '{value}': {reason}", path);
                return null;
            }

            if (expectedName is { } expected && !string.Equals(name.Value, expected.Value, StringComparison.Ordinal))
            {
                AddError($"name '{name.Value}' does not match project slug '{expected.Value}'", path);
            }

            return name;
        }

        private TargetRepositoryConfig? ReadTargetRepository(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                AddError("must be a JSON object", path);
                return null;
            }

            string? targetPath = null;
            var defaultBranch = ProjectConfigDefaults.DefaultBranch;
            var sawPath = false;
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = AppendPropertyPath(path, property.Name);
                if (!names.Add(property.Name))
                {
                    AddError($"duplicate property '{property.Name}'", propertyPath);
                }

                switch (property.Name)
                {
                    case "path":
                        sawPath = true;
                        targetPath = ReadTargetPath(property.Value, propertyPath);
                        break;
                    case "default_branch":
                        defaultBranch = ReadDefaultBranch(property.Value, propertyPath);
                        break;
                    default:
                        AddUnknownProperty(property, propertyPath);
                        break;
                }
            }

            if (!sawPath)
            {
                AddError("is required", AppendPropertyPath(path, "path"));
            }

            return targetPath is null ? null : new TargetRepositoryConfig(targetPath, defaultBranch);
        }

        private string? ReadTargetPath(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            {
                AddError("must be a non-blank string", path);
                return null;
            }

            string targetPath;
            try
            {
                var configDirectory = Path.GetDirectoryName(configFilePath)!;
                targetPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(element.GetString()!, configDirectory));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                AddError($"contains an invalid path: {exception.Message}", path);
                return null;
            }

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var targetPrefix = Path.EndsInDirectorySeparator(targetPath)
                ? targetPath
                : targetPath + Path.DirectorySeparatorChar;
            if (string.Equals(hostPaths.Root, targetPath, comparison) || hostPaths.Root.StartsWith(targetPrefix, comparison))
            {
                AddError(
                    $"Nightweaver host directory '{hostPaths.Root}' must be outside the Target Repository (SPEC 1.4)",
                    path);
            }

            return targetPath;
        }

        private string ReadDefaultBranch(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                AddError("must be a valid Git branch name", path);
                return ProjectConfigDefaults.DefaultBranch;
            }

            var branch = element.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(branch) || !string.Equals(branch, branch.Trim(), StringComparison.Ordinal))
            {
                AddError("must be non-blank and have no leading or trailing whitespace", path);
                return ProjectConfigDefaults.DefaultBranch;
            }

            return branch;
        }

        private ConcurrencyConfig ReadConcurrency(JsonElement element, string path)
        {
            var result = new ConcurrencyConfig(
                ProjectConfigDefaults.MaxConcurrentPlanning,
                ProjectConfigDefaults.MaxConcurrentDelivery,
                ProjectConfigDefaults.MaxActivePlanBeads);
            if (element.ValueKind != JsonValueKind.Object)
            {
                AddError("must be a JSON object", path);
                return result;
            }

            var planning = result.MaxConcurrentPlanning;
            var delivery = result.MaxConcurrentDelivery;
            var activePlanBeads = result.MaxActivePlanBeads;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = AppendPropertyPath(path, property.Name);
                if (!names.Add(property.Name))
                {
                    AddError($"duplicate property '{property.Name}'", propertyPath);
                }

                switch (property.Name)
                {
                    case "max_concurrent_planning":
                        planning = ReadInteger(property.Value, propertyPath, 1);
                        break;
                    case "max_concurrent_delivery":
                        delivery = ReadInteger(property.Value, propertyPath, 1);
                        break;
                    case "max_active_plan_beads":
                        activePlanBeads = ReadInteger(property.Value, propertyPath, 1);
                        break;
                    default:
                        AddUnknownProperty(property, propertyPath);
                        break;
                }
            }

            return new ConcurrencyConfig(planning, delivery, activePlanBeads);
        }

        private LimitsConfig ReadLimits(JsonElement element, string path)
        {
            var result = new LimitsConfig(ProjectConfigDefaults.MaxRepairCycles, ProjectConfigDefaults.MaxOperationalRetries);
            if (element.ValueKind != JsonValueKind.Object)
            {
                AddError("must be a JSON object", path);
                return result;
            }

            var repairCycles = result.MaxRepairCycles;
            var operationalRetries = result.MaxOperationalRetries;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = AppendPropertyPath(path, property.Name);
                if (!names.Add(property.Name))
                {
                    AddError($"duplicate property '{property.Name}'", propertyPath);
                }

                switch (property.Name)
                {
                    case "max_repair_cycles":
                        repairCycles = ReadInteger(property.Value, propertyPath, 0);
                        break;
                    case "max_operational_retries":
                        operationalRetries = ReadInteger(property.Value, propertyPath, 0);
                        break;
                    default:
                        AddUnknownProperty(property, propertyPath);
                        break;
                }
            }

            return new LimitsConfig(repairCycles, operationalRetries);
        }

        private Dictionary<AgentRole, AgentSettingsOverride> ReadRoles(JsonElement element, string path)
        {
            var result = new Dictionary<AgentRole, AgentSettingsOverride>();
            if (element.ValueKind != JsonValueKind.Object)
            {
                AddError("must be a JSON object", path);
                return result;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = AppendPropertyPath(path, property.Name);
                if (!names.Add(property.Name))
                {
                    AddError($"duplicate property '{property.Name}'", propertyPath);
                }

                if (!AgentRoles.TryParseConfigKey(property.Name, out var role))
                {
                    AddError($"unknown Agent role '{property.Name}'", propertyPath);
                    CollectNestedDuplicates(property.Value, propertyPath);
                    continue;
                }

                result[role] = ReadAgentSettings(property.Value, propertyPath);
            }

            return result;
        }

        private AgentSettingsOverride ReadAgentSettings(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                AddError("must be a JSON object", path);
                return AgentSettingsOverride.None;
            }

            var cli = Optional<AgentCli>.Absent;
            var model = Optional<string?>.Absent;
            var effort = Optional<string>.Absent;
            var timeoutMinutes = Optional<int>.Absent;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = AppendPropertyPath(path, property.Name);
                if (!names.Add(property.Name))
                {
                    AddError($"duplicate property '{property.Name}'", propertyPath);
                }

                switch (property.Name)
                {
                    case "cli":
                        cli = ReadAgentCli(property.Value, propertyPath);
                        break;
                    case "model":
                        model = ReadModel(property.Value, propertyPath);
                        break;
                    case "effort":
                        effort = ReadAgentText(property.Value, propertyPath, "effort");
                        break;
                    case "timeout_minutes":
                        var value = ReadInteger(property.Value, propertyPath, 1);
                        if (IsValidInteger(property.Value, 1))
                        {
                            timeoutMinutes = Optional<int>.Of(value);
                        }

                        break;
                    default:
                        AddUnknownProperty(property, propertyPath);
                        break;
                }
            }

            return new AgentSettingsOverride(cli, model, effort, timeoutMinutes);
        }

        private Optional<AgentCli> ReadAgentCli(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                AddError("must be exactly 'claude' or 'codex'", path);
                return Optional<AgentCli>.Absent;
            }

            var value = element.GetString() ?? string.Empty;
            if (!AgentCliNames.TryParseConfigValue(value, out var cli))
            {
                AddError("must be exactly 'claude' or 'codex'", path);
                return Optional<AgentCli>.Absent;
            }

            return Optional<AgentCli>.Of(cli);
        }

        private Optional<string?> ReadModel(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                return Optional<string?>.Of(null);
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                AddError("must be a non-blank string or null", path);
                return Optional<string?>.Absent;
            }

            var value = element.GetString() ?? string.Empty;
            if (!IsValidAgentText(value))
            {
                AddError("must be non-blank, have no leading or trailing whitespace, and contain no control characters", path);
                return Optional<string?>.Absent;
            }

            return Optional<string?>.Of(value);
        }

        private Optional<string> ReadAgentText(JsonElement element, string path, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                AddError($"must be a non-blank {propertyName} string", path);
                return Optional<string>.Absent;
            }

            var value = element.GetString() ?? string.Empty;
            if (!IsValidAgentText(value))
            {
                AddError("must be non-blank, have no leading or trailing whitespace, and contain no control characters", path);
                return Optional<string>.Absent;
            }

            return Optional<string>.Of(value);
        }

        private int ReadInteger(JsonElement element, string path, int minimum)
        {
            if (!TryReadInteger(element, out var value))
            {
                AddError("must be an Int32 integer literal", path);
                return minimum;
            }

            if (value < minimum)
            {
                AddError(minimum == 0 ? "must be non-negative" : $"must be at least {minimum}", path);
                return minimum;
            }

            return value;
        }

        private static bool IsValidInteger(JsonElement element, int minimum) =>
            TryReadInteger(element, out var value) && value >= minimum;

        private static bool TryReadInteger(JsonElement element, out int value)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            var raw = element.GetRawText();
            if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
            {
                return false;
            }

            return int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        private static bool IsValidAgentText(string value) =>
            !string.IsNullOrWhiteSpace(value)
            && string.Equals(value, value.Trim(), StringComparison.Ordinal)
            && !value.Any(character => character <= 0x1f || character == 0x7f);

        private void AddUnknownProperty(JsonProperty property, string path)
        {
            AddError($"unknown property '{property.Name}'", path);
            CollectNestedDuplicates(property.Value, path);
        }

        private void CollectNestedDuplicates(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = AppendPropertyPath(path, property.Name);
                    if (!names.Add(property.Name))
                    {
                        AddError($"duplicate property '{property.Name}'", propertyPath);
                    }

                    CollectNestedDuplicates(property.Value, propertyPath);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    CollectNestedDuplicates(item, $"{path}[{index}]");
                    index++;
                }
            }
        }

        private void AddError(string message, string? jsonPath) => errors.Add(new ConfigurationError(jsonPath, message));

        private void ThrowIfInvalid()
        {
            if (errors.Count > 0)
            {
                throw new ProjectConfigurationException(errors, configFilePath);
            }
        }

        private static string AppendPropertyPath(string path, string propertyName) =>
            propertyName.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                ? $"{path}.{propertyName}"
                : $"{path}['{propertyName.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}']";
    }
}
