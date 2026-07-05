using System.Text.Json;
using OpenMono.Lsp;

namespace OpenMono.Tools;

/// <summary>
/// Выполняет запросы к языковому серверу для получения семантической информации по коду.
/// </summary>
public sealed class LspTool : ToolBase
{
    /// <summary>
    /// Получает имя инструмента.
    /// </summary>
    public override string Name => "Lsp";

    /// <summary>
    /// Получает описание назначения инструмента.
    /// </summary>
    public override string Description => "Query a language server for code intelligence: hover info, go-to-definition, find references.";

    /// <summary>
    /// Получает признак безопасного параллельного выполнения.
    /// </summary>
    public override bool IsConcurrencySafe => true;

    /// <summary>
    /// Получает признак того, что инструмент не изменяет состояние проекта.
    /// </summary>
    public override bool IsReadOnly => true;

    /// <summary>
    /// Получает уровень разрешений по умолчанию.
    /// </summary>
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    /// <summary>
    /// Менеджер языковых серверов, подбирающий клиент по типу файла.
    /// </summary>
    private readonly LspServerManager _lspManager;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="LspTool"/>.
    /// </summary>
    /// <param name="lspManager">Менеджер активных LSP-клиентов.</param>
    public LspTool(LspServerManager lspManager)
    {
        _lspManager = lspManager;
    }

    /// <summary>
    /// Описывает схему входных параметров инструмента.
    /// </summary>
    /// <returns>Построитель схемы для LSP-запросов.</returns>
    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddEnum("action", "The LSP action to perform", "hover", "definition", "references")
        .AddString("file_path", "Absolute path to the file")
        .AddInteger("line", "Line number (0-based)")
        .AddInteger("character", "Column number (0-based)")
        .Require("action", "file_path", "line", "character");

    /// <summary>
    /// Выполняет LSP-запрос для указанной позиции в файле.
    /// </summary>
    /// <param name="input">JSON-аргументы вызова инструмента.</param>
    /// <param name="context">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат выполнения запроса к языковому серверу.</returns>
    /// <remarks>
    /// Координаты строки и столбца ожидаются в нулевой индексации, как в протоколе LSP.
    /// </remarks>
    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var action = input.GetProperty("action").GetString()!;
        var filePath = Path.GetFullPath(input.GetProperty("file_path").GetString()!, context.WorkingDirectory);
        var line = input.GetProperty("line").GetInt32();
        var character = input.GetProperty("character").GetInt32();

        var client = await _lspManager.GetClientAsync(filePath, ct);
        if (client is null)
            return ToolResult.Error($"No language server available for {Path.GetExtension(filePath)} files");

        try
        {
            return action switch
            {
                "hover" => await HandleHoverAsync(client, filePath, line, character, ct),
                "definition" => await HandleDefinitionAsync(client, filePath, line, character, ct),
                "references" => await HandleReferencesAsync(client, filePath, line, character, ct),
                _ => ToolResult.Error($"Unknown LSP action: {action}. Use: hover, definition, references"),
            };
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"LSP query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Получает hover-информацию для символа в указанной позиции.
    /// </summary>
    /// <param name="client">LSP-клиент, обслуживающий файл.</param>
    /// <param name="filePath">Путь к анализируемому файлу.</param>
    /// <param name="line">Номер строки в нулевой индексации.</param>
    /// <param name="character">Номер столбца в нулевой индексации.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат с текстом hover-описания или сообщением об отсутствии данных.</returns>
    private static async Task<ToolResult> HandleHoverAsync(
        LspClient client, string filePath, int line, int character, CancellationToken ct)
    {
        var result = await client.HoverAsync(filePath, line, character, ct);
        return result is not null
            ? ToolResult.Success($"Hover at {filePath}:{line + 1}:{character + 1}\n\n{result}")
            : ToolResult.Success("No hover information available at this position.");
    }

    /// <summary>
    /// Находит определения символа в указанной позиции.
    /// </summary>
    /// <param name="client">LSP-клиент, обслуживающий файл.</param>
    /// <param name="filePath">Путь к анализируемому файлу.</param>
    /// <param name="line">Номер строки в нулевой индексации.</param>
    /// <param name="character">Номер столбца в нулевой индексации.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат со списком найденных определений.</returns>
    private static async Task<ToolResult> HandleDefinitionAsync(
        LspClient client, string filePath, int line, int character, CancellationToken ct)
    {
        var locations = await client.DefinitionAsync(filePath, line, character, ct);
        if (locations.Count == 0)
            return ToolResult.Success("No definition found at this position.");

        var output = locations.Select(l => $"  {l}").ToList();
        return ToolResult.Success($"Definition(s):\n{string.Join('\n', output)}");
    }

    /// <summary>
    /// Находит ссылки на символ в указанной позиции.
    /// </summary>
    /// <param name="client">LSP-клиент, обслуживающий файл.</param>
    /// <param name="filePath">Путь к анализируемому файлу.</param>
    /// <param name="line">Номер строки в нулевой индексации.</param>
    /// <param name="character">Номер столбца в нулевой индексации.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат со списком найденных ссылок.</returns>
    private static async Task<ToolResult> HandleReferencesAsync(
        LspClient client, string filePath, int line, int character, CancellationToken ct)
    {
        var locations = await client.ReferencesAsync(filePath, line, character, ct);
        if (locations.Count == 0)
            return ToolResult.Success("No references found at this position.");

        var output = locations.Select(l => $"  {l}").ToList();
        return ToolResult.Success($"{locations.Count} reference(s):\n{string.Join('\n', output)}");
    }
}
