using System.Text.Json;
using OpenMono.Permissions;
using OpenMono.Utils;

namespace OpenMono.Tools;

/// <summary>
/// Выполняет точечную замену текста в существующем файле.
/// </summary>
public sealed class FileEditTool : ToolBase
{
    /// <summary>
    /// Возвращает имя инструмента.
    /// </summary>
    public override string Name => "FileEdit";

    /// <summary>
    /// Возвращает описание назначения инструмента.
    /// </summary>
    public override string Description => "Perform an exact string replacement in a file. The old_string must match exactly one location in the file.";

    /// <summary>
    /// Описывает JSON-схему входных параметров инструмента.
    /// </summary>
    /// <returns>Построитель схемы входных данных.</returns>
    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("file_path", "Absolute path to the file to edit")
        .AddString("old_string", "The exact text to find and replace")
        .AddString("new_string", "The replacement text")
        .AddBoolean("replace_all", "Replace all occurrences (default: false)")
        .Require("file_path", "old_string", "new_string");

    /// <summary>
    /// Возвращает возможности, необходимые для редактирования указанного файла.
    /// </summary>
    /// <param name="input">JSON с параметрами вызова инструмента.</param>
    /// <returns>Список требуемых возможностей.</returns>
    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var filePath = input.TryGetProperty("file_path", out var fp) ? fp.GetString() : null;
        if (string.IsNullOrEmpty(filePath))
            return [];
        return [new FileWriteCap(filePath, "modify")];
    }

    /// <summary>
    /// Выполняет замену текста в файле.
    /// </summary>
    /// <param name="input">JSON с путем к файлу и строками замены.</param>
    /// <param name="context">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат редактирования файла.</returns>
    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var filePath = input.GetProperty("file_path").GetString()!;
        var oldString = input.GetProperty("old_string").GetString()!;
        var newString = input.GetProperty("new_string").GetString()!;
        var replaceAll = input.TryGetProperty("replace_all", out var ra) && ra.GetBoolean();

        if (string.IsNullOrEmpty(oldString))
            return ToolResult.Error(
                "old_string must not be empty. Use FileWrite to create a new file, " +
                "or supply non-empty text to find and replace.");

        if (oldString == newString)
            return ToolResult.Error("old_string and new_string are identical — nothing to replace.");

        var resolvedPath = Path.GetFullPath(filePath, context.WorkingDirectory);

        if (PathGuard.Validate(resolvedPath, context.WorkingDirectory) is { } guardError)
            return ToolResult.Error(guardError);

        if (!File.Exists(resolvedPath))
            return ToolResult.Error($"File not found: {resolvedPath}");

        try
        {
            var content = await File.ReadAllTextAsync(resolvedPath, ct);
            var occurrences = CountOccurrences(content, oldString);

            if (occurrences == 0)
                return ToolResult.Error($"old_string not found in {resolvedPath}");

            if (occurrences > 1 && !replaceAll)
                return ToolResult.Error(
                    $"old_string found {occurrences} times in {resolvedPath}. " +
                    "Provide more context to make it unique, or set replace_all=true.");

            string updated;
            if (replaceAll)
                updated = content.Replace(oldString, newString);
            else
                updated = ReplaceFirst(content, oldString, newString);

            var secrets = SecretScanner.Scan(newString);
            var secretWarning = secrets.Count > 0
                ? $"\n⚠ Potential secret(s) detected in replacement text: {string.Join(", ", secrets.Select(SecretScanner.RuleIdToLabel))}. " +
                  "Verify this file should contain credentials before committing."
                : string.Empty;

            context.FileHistory?.RecordBefore(resolvedPath, Name, context.Session.Messages.Count);

            await File.WriteAllTextAsync(resolvedPath, updated, ct);

            context.FileHistory?.RecordAfter(resolvedPath);

            var replacements = replaceAll ? occurrences : 1;
            var diff = InlineDiff.FromEdit(oldString, newString, resolvedPath);
            return ToolResult.Success(
                $"Replaced {replacements} occurrence(s) in {resolvedPath}{secretWarning}")
                .WithDiff(diff);
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Error(DiagnoseWriteFailure(resolvedPath));
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020) ||
                                     ex.Message.Contains("being used by another process"))
        {
            return ToolResult.Error($"Cannot edit '{resolvedPath}': file is locked by another process.");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Error editing file: {ex.Message}");
        }
    }

    /// <summary>
    /// Формирует подсказку по диагностике отказа записи.
    /// </summary>
    /// <param name="path">Путь к файлу, запись в который завершилась ошибкой.</param>
    /// <returns>Текст рекомендации для пользователя.</returns>
    private static string DiagnoseWriteFailure(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).IsReadOnly)
            {
                return OperatingSystem.IsWindows()
                    ? $"Cannot edit '{path}': file is read-only. Run in your terminal: attrib -r \"{path}\""
                    : $"Cannot edit '{path}': file has no write permission. Run in your terminal: chmod u+w {path}";
            }
        }
        catch { }

        return $"Cannot edit '{path}': access denied. Check ownership with: ls -la {path}";
    }

    /// <summary>
    /// Подсчитывает количество точных вхождений подстроки в тексте.
    /// </summary>
    /// <param name="text">Текст для поиска.</param>
    /// <param name="search">Искомая подстрока.</param>
    /// <returns>Количество найденных вхождений.</returns>
    private static int CountOccurrences(string text, string search)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    /// <summary>
    /// Заменяет только первое точное вхождение строки.
    /// </summary>
    /// <param name="text">Исходный текст.</param>
    /// <param name="oldValue">Строка, которую нужно заменить.</param>
    /// <param name="newValue">Новая строка.</param>
    /// <returns>Обновленный текст.</returns>
    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0) return text;
        return string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }
}
