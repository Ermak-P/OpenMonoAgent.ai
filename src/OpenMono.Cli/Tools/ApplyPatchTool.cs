using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenMono.Tools;

/// <summary>
/// Применяет unified diff-патч к одному или нескольким существующим файлам.
/// </summary>
public sealed partial class ApplyPatchTool : ToolBase
{
    /// <summary>
    /// Возвращает имя инструмента.
    /// </summary>
    public override string Name => "ApplyPatch";

    /// <summary>
    /// Возвращает описание назначения инструмента.
    /// </summary>
    public override string Description => "Apply a unified diff patch to one or more files. Supports standard unified diff format.";

    /// <summary>
    /// Описывает JSON-схему входных параметров инструмента.
    /// </summary>
    /// <returns>Построитель схемы входных данных.</returns>
    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("patch", "The unified diff patch content")
        .AddBoolean("dry_run", "Preview changes without writing (default: false)")
        .Require("patch");

    /// <summary>
    /// Разбирает и применяет патч к указанным файлам.
    /// </summary>
    /// <param name="input">JSON с телом патча и флагом пробного запуска.</param>
    /// <param name="context">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат применения патча или сообщение об ошибке.</returns>
    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var patch = input.GetProperty("patch").GetString()!;
        var dryRun = input.TryGetProperty("dry_run", out var dr) && dr.GetBoolean();

        try
        {
            var hunks = ParsePatch(patch);
            if (hunks.Count == 0)
                return ToolResult.Error("No valid hunks found in patch");

            var results = new List<string>();
            var filesModified = 0;

            var fileHunks = hunks.GroupBy(h => h.FilePath);

            foreach (var group in fileHunks)
            {
                var filePath = Path.GetFullPath(group.Key, context.WorkingDirectory);

                if (!File.Exists(filePath))
                {
                    results.Add($"SKIP {group.Key}: file not found");
                    continue;
                }

                var originalLines = (await File.ReadAllLinesAsync(filePath, ct)).ToList();
                var modifiedLines = new List<string>(originalLines);
                var offset = 0;

                foreach (var hunk in group.OrderBy(h => h.StartLine))
                {
                    var adjustedStart = hunk.StartLine - 1 + offset;

                    // Смещение пересчитывается после каждого предыдущего hunk-а,
                    // чтобы номера строк оставались корректными в уже измененном буфере.
                    var contextMatch = VerifyContext(modifiedLines, adjustedStart, hunk);
                    if (!contextMatch)
                    {
                        results.Add($"FAIL {group.Key}:{hunk.StartLine}: context mismatch");
                        continue;
                    }

                    var (newLines, linesRemoved, linesAdded) = ApplyHunk(modifiedLines, adjustedStart, hunk);
                    modifiedLines = newLines;
                    offset += linesAdded - linesRemoved;
                }

                if (!dryRun)
                {

                    context.FileHistory?.RecordBefore(filePath, Name, context.Session.Messages.Count);

                    await File.WriteAllLinesAsync(filePath, modifiedLines, ct);

                    context.FileHistory?.RecordAfter(filePath);
                }

                var verb = dryRun ? "Would modify" : "Modified";
                results.Add($"OK {verb} {group.Key} ({group.Count()} hunk(s))");
                filesModified++;
            }

            var summary = dryRun
                ? $"Dry run: {filesModified} file(s) would be modified"
                : $"Applied patch: {filesModified} file(s) modified";

            return ToolResult.Success($"{summary}\n{string.Join('\n', results)}");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Patch application failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Разбирает текст unified diff и выделяет из него набор hunk-блоков.
    /// </summary>
    /// <param name="patch">Текст патча.</param>
    /// <returns>Список разобранных hunk-блоков.</returns>
    private static List<PatchHunk> ParsePatch(string patch)
    {
        var hunks = new List<PatchHunk>();
        var lines = patch.Split('\n');
        string? currentFile = null;
        PatchHunk? currentHunk = null;

        foreach (var line in lines)
        {

            if (line.StartsWith("+++ "))
            {
                // В стандартном diff путь новой версии часто идет как b/path,
                // поэтому служебный префикс удаляется.
                var path = line[4..].TrimStart('b', '/').Trim();
                currentFile = path;
                continue;
            }

            if (line.StartsWith("--- ")) continue;

            var hunkMatch = HunkHeaderPattern().Match(line);
            if (hunkMatch.Success && currentFile is not null)
            {
                currentHunk = new PatchHunk
                {
                    FilePath = currentFile,
                    StartLine = int.Parse(hunkMatch.Groups[1].Value),
                };
                hunks.Add(currentHunk);
                continue;
            }

            if (currentHunk is not null &&
                (line.StartsWith('+') || line.StartsWith('-') || line.StartsWith(' ')))
            {
                currentHunk.Lines.Add(line);
            }
        }

        return hunks;
    }

    /// <summary>
    /// Проверяет, совпадает ли контекст hunk-блока с текущим содержимым файла.
    /// </summary>
    /// <param name="fileLines">Строки файла, к которому применяется патч.</param>
    /// <param name="startIndex">Начальный индекс применения в текущем буфере.</param>
    /// <param name="hunk">Проверяемый hunk-блок.</param>
    /// <returns><see langword="true" />, если контекст совпадает; иначе <see langword="false" />.</returns>
    private static bool VerifyContext(List<string> fileLines, int startIndex, PatchHunk hunk)
    {
        var fileIdx = startIndex;
        foreach (var line in hunk.Lines)
        {
            if (line.StartsWith(' ') || line.StartsWith('-'))
            {
                if (fileIdx >= fileLines.Count) return false;
                var expected = line[1..];
                if (fileLines[fileIdx] != expected) return false;
                fileIdx++;
            }
        }
        return true;
    }

    /// <summary>
    /// Применяет один hunk-блок к набору строк.
    /// </summary>
    /// <param name="lines">Текущие строки файла.</param>
    /// <param name="startIndex">Индекс начала применения.</param>
    /// <param name="hunk">Применяемый hunk-блок.</param>
    /// <returns>Новый набор строк и количество удаленных и добавленных строк.</returns>
    private static (List<string> Result, int Removed, int Added) ApplyHunk(
        List<string> lines, int startIndex, PatchHunk hunk)
    {
        var result = new List<string>(lines[..startIndex]);
        var removed = 0;
        var added = 0;
        var sourceIdx = startIndex;

        foreach (var line in hunk.Lines)
        {
            if (line.StartsWith(' '))
            {
                // Контекстная строка копируется как есть и продвигает обе позиции.
                result.Add(lines[sourceIdx]);
                sourceIdx++;
            }
            else if (line.StartsWith('-'))
            {
                // Удаление только пропускает строку источника, не добавляя ее в результат.
                sourceIdx++;
                removed++;
            }
            else if (line.StartsWith('+'))
            {

                result.Add(line[1..]);
                added++;
            }
        }

        result.AddRange(lines[sourceIdx..]);
        return (result, removed, added);
    }

    /// <summary>
    /// Возвращает регулярное выражение для разбора заголовка hunk-блока unified diff.
    /// </summary>
    /// <returns>Скомпилированное регулярное выражение.</returns>
    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeaderPattern();

    /// <summary>
    /// Представляет один блок изменений внутри патча.
    /// </summary>
    private sealed class PatchHunk
    {
        /// <summary>
        /// Возвращает путь к файлу, к которому относится блок.
        /// </summary>
        public required string FilePath { get; init; }

        /// <summary>
        /// Возвращает номер стартовой строки в исходном файле.
        /// </summary>
        public required int StartLine { get; init; }

        /// <summary>
        /// Возвращает строки блока с префиксами unified diff.
        /// </summary>
        public List<string> Lines { get; } = [];
    }
}
