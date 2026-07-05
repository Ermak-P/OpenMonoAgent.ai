using System.Text.Json;
using OpenMono.Permissions;

namespace OpenMono.Tools;

/// <summary>
/// Возвращает список файлов и каталогов по указанному пути.
/// </summary>
public sealed class ListDirectoryTool : ToolBase
{
    /// <summary>
    /// Получает имя инструмента.
    /// </summary>
    public override string Name => "ListDirectory";

    /// <summary>
    /// Получает описание назначения инструмента.
    /// </summary>
    public override string Description => "List files and directories at a given path. Shows file sizes and modification times.";

    /// <summary>
    /// Получает признак безопасного параллельного выполнения.
    /// </summary>
    public override bool IsConcurrencySafe => true;

    /// <summary>
    /// Получает признак того, что инструмент только читает файловую систему.
    /// </summary>
    public override bool IsReadOnly => true;

    /// <summary>
    /// Получает уровень разрешений по умолчанию.
    /// </summary>
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    /// <summary>
    /// Описывает схему входных параметров инструмента.
    /// </summary>
    /// <returns>Построитель схемы для перечисления каталогов.</returns>
    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("path", "Directory path to list (default: working directory)")
        .AddBoolean("recursive", "List recursively (default: false)")
        .AddInteger("max_entries", "Maximum entries to return (default: 200)");

    /// <summary>
    /// Возвращает возможности, необходимые для чтения выбранного каталога.
    /// </summary>
    /// <param name="input">JSON-аргументы вызова инструмента.</param>
    /// <returns>Список возможностей, требуемых для чтения каталога.</returns>
    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var dirPath = input.TryGetProperty("path", out var p) ? p.GetString() : ".";
        if (string.IsNullOrEmpty(dirPath))
            dirPath = ".";
        return [new FileReadCap(dirPath)];
    }

    /// <summary>
    /// Выполняет перечисление содержимого каталога.
    /// </summary>
    /// <param name="input">JSON-аргументы вызова инструмента.</param>
    /// <param name="context">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат со списком каталогов и файлов.</returns>
    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var dirPath = input.TryGetProperty("path", out var p)
            ? Path.GetFullPath(p.GetString()!, context.WorkingDirectory)
            : context.WorkingDirectory;
        var recursive = input.TryGetProperty("recursive", out var r) && r.GetBoolean();
        var maxEntries = input.TryGetProperty("max_entries", out var m) ? m.GetInt32() : 200;

        if (PathGuard.ValidateDirectory(dirPath, context.WorkingDirectory) is { } guardError)
            return Task.FromResult(ToolResult.Error(guardError));

        if (!Directory.Exists(dirPath))
            return Task.FromResult(ToolResult.Error($"Directory not found: {dirPath}"));

        try
        {
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var entries = new List<string>();

            foreach (var dir in Directory.EnumerateDirectories(dirPath, "*", searchOption))
            {
                if (entries.Count >= maxEntries) break;
                var rel = Path.GetRelativePath(dirPath, dir);
                entries.Add($"  {rel}/");
            }

            foreach (var file in Directory.EnumerateFiles(dirPath, "*", searchOption))
            {
                if (entries.Count >= maxEntries) break;
                var rel = Path.GetRelativePath(dirPath, file);
                var info = new FileInfo(file);
                var size = FormatSize(info.Length);
                entries.Add($"  {rel}  ({size})");
            }

            if (entries.Count == 0)
                return Task.FromResult(ToolResult.Success($"{dirPath}/ (empty)"));

            var truncated = entries.Count >= maxEntries ? $"\n... (truncated at {maxEntries} entries)" : "";
            var header = $"{dirPath}/ ({entries.Count} entries)";
            return Task.FromResult(ToolResult.Success($"{header}\n{string.Join('\n', entries)}{truncated}"));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(ToolResult.Error($"Permission denied: {dirPath}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"Error listing directory: {ex.Message}"));
        }
    }

    /// <summary>
    /// Преобразует размер файла в компактное человекочитаемое представление.
    /// </summary>
    /// <param name="bytes">Размер файла в байтах.</param>
    /// <returns>Строковое представление размера файла.</returns>
    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1}MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1}GB",
    };
}
