using System.Text.Json;
using OpenMono.Memory;
using OpenMono.Permissions;

namespace OpenMono.Tools;

/// <summary>
/// Сохраняет запись памяти, которая должна переживать завершение текущей сессии.
/// </summary>
public sealed class MemorySaveTool : ToolBase
{
    /// <summary>
    /// Получает имя инструмента.
    /// </summary>
    public override string Name => "MemorySave";

    /// <summary>
    /// Получает описание назначения инструмента.
    /// </summary>
    public override string Description => "Save a memory that persists across sessions. Use for user preferences, project context, or important decisions.";

    /// <summary>
    /// Получает уровень разрешений по умолчанию.
    /// </summary>
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    /// <summary>
    /// Хранилище, в которое записываются сохраненные воспоминания.
    /// </summary>
    private readonly MemoryStore _store;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="MemorySaveTool"/>.
    /// </summary>
    /// <param name="store">Хранилище долговременной памяти.</param>
    public MemorySaveTool(MemoryStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Описывает схему входных параметров инструмента.
    /// </summary>
    /// <returns>Построитель схемы с обязательными полями для сохранения памяти.</returns>
    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("name", "Short name for the memory (kebab-case)")
        .AddEnum("type", "Memory type", "user", "feedback", "project", "reference")
        .AddString("description", "One-line description of what this memory contains")
        .AddString("content", "The memory content to persist")
        .Require("name", "type", "description", "content");

    /// <summary>
    /// Возвращает возможности, необходимые для записи памяти указанного типа.
    /// </summary>
    /// <param name="input">JSON-аргументы вызова инструмента.</param>
    /// <returns>Список возможностей, требуемых для операции записи.</returns>
    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var type = input.TryGetProperty("type", out var t) ? t.GetString() : "project";
        return [new MemoryCap(type ?? "project", "write")];
    }

    /// <summary>
    /// Выполняет сохранение записи памяти.
    /// </summary>
    /// <param name="input">JSON-аргументы с именем, типом и содержимым памяти.</param>
    /// <param name="context">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат успешного сохранения памяти.</returns>
    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var name = input.GetProperty("name").GetString()!;
        var type = input.GetProperty("type").GetString()!;
        var description = input.GetProperty("description").GetString()!;
        var content = input.GetProperty("content").GetString()!;

        await _store.SaveAsync(name, type, description, content, ct);
        return ToolResult.Success($"Memory saved: {name} ({type})");
    }
}
