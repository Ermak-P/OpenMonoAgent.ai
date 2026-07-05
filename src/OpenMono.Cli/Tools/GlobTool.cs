using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using OpenMono.Permissions;

namespace OpenMono.Tools;

/// <summary>
/// Ищет файлы по glob-шаблону в указанном каталоге.
/// </summary>
public sealed class GlobTool : ToolBase
{
    /// <summary>
    /// Возвращает имя инструмента.
    /// </summary>
    public override string Name => "Glob";

    /// <summary>
    /// Возвращает описание назначения инструмента.
    /// </summary>
    public override string Description => "Find files matching a glob pattern. Returns paths sorted by modification time.";

    /// <summary>
    /// Указывает, что инструмент безопасен для параллельного выполнения.
    /// </summary>
    public override bool IsConcurrencySafe => true;

    /// <summary>
    /// Указывает, что инструмент не изменяет внешнее состояние.
    /// </summary>
    public override bool IsReadOnly => true;

    /// <summary>
    /// Возвращает уровень разрешений по умолчанию.
    /// </summary>
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    /// <summary>
    /// Описывает JSON-схему входных параметров инструмента.
    /// </summary>
    /// <returns>Построитель схемы входных данных.</returns>
    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("pattern", "Glob pattern (e.g. **/*.cs, src/**/*.json)")
        .AddString("path", "Directory to search in (default: working directory)")
        .Require("pattern");

    /// <summary>
    /// Возвращает возможности, необходимые для поиска в каталоге.
    /// </summary>
    /// <param name="input">JSON с параметрами вызова инструмента.</param>
    /// <returns>Список требуемых возможностей.</returns>
    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var searchPath = input.TryGetProperty("path", out var p) ? p.GetString() : ".";
        if (string.IsNullOrEmpty(searchPath))
            searchPath = ".";
        return [new FileReadCap(searchPath)];
    }

    /// <summary>
    /// Выполняет поиск файлов по glob-шаблону.
    /// </summary>
    /// <param name="input">JSON с шаблоном и необязательным каталогом поиска.</param>
    /// <param name="context">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат поиска файлов.</returns>
    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var pattern = input.GetProperty("pattern").GetString()!;
        var searchPath = input.TryGetProperty("path", out var p)
            ? Path.GetFullPath(p.GetString()!, context.WorkingDirectory)
            : context.WorkingDirectory;

        if (PathGuard.ValidateDirectory(searchPath, context.WorkingDirectory) is { } guardError)
            return Task.FromResult(ToolResult.Error(guardError));

        if (!Directory.Exists(searchPath))
            return Task.FromResult(ToolResult.Error($"Directory not found: {searchPath}"));

        try
        {
            var matcher = new Matcher();
            matcher.AddInclude(pattern);

            var directoryInfo = new DirectoryInfoWrapper(new DirectoryInfo(searchPath));
            var result = matcher.Execute(directoryInfo);

            var files = result.Files
                .Select(f => Path.Combine(searchPath, f.Path))
                .Where(File.Exists)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Take(250)
                .ToList();

            if (files.Count == 0)
                return Task.FromResult(ToolResult.Success($"No files matching '{pattern}' in {searchPath}"));

            var output = string.Join('\n', files);
            return Task.FromResult(ToolResult.Success($"Found {files.Count} file(s):\n{output}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"Glob error: {ex.Message}"));
        }
    }
}
