using System.Text.Json;
using OpenMono.Permissions;

namespace OpenMono.Tools;

/// <summary>
/// Определяет уровень разрешения, необходимый инструменту для выполнения.
/// </summary>
public enum PermissionLevel
{
    /// <summary>
    /// Разрешение выдается автоматически.
    /// </summary>
    AutoAllow,

    /// <summary>
    /// Для выполнения требуется явное подтверждение.
    /// </summary>
    Ask,

    /// <summary>
    /// Выполнение запрещено.
    /// </summary>
    Deny
}

/// <summary>
/// Определяет контракт инструмента, который может быть вызван средой выполнения.
/// </summary>
public interface ITool
{
    /// <summary>
    /// Получает человекочитаемое имя инструмента.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Получает описание назначения инструмента.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Получает JSON-схему входных аргументов инструмента.
    /// </summary>
    JsonElement InputSchema { get; }

    /// <summary>
    /// Получает признак того, что инструмент безопасно запускать параллельно с другими вызовами.
    /// </summary>
    bool IsConcurrencySafe { get; }

    /// <summary>
    /// Получает признак того, что инструмент не изменяет состояние и файловую систему.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Получает признак того, что инструмент может быть отложен до более позднего шага.
    /// </summary>
    bool IsDeferred => false;

    /// <summary>
    /// Выполняет инструмент с указанным входом и контекстом.
    /// </summary>
    /// <param name="input">JSON-аргументы вызова инструмента.</param>
    /// <param name="context">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат выполнения инструмента.</returns>
    Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct);

    /// <summary>
    /// Определяет требуемый уровень разрешений для указанного входа.
    /// </summary>
    /// <param name="input">JSON-аргументы вызова инструмента.</param>
    /// <returns>Требуемый уровень разрешений.</returns>
    PermissionLevel RequiredPermission(JsonElement input);

    /// <summary>
    /// Возвращает список возможностей, необходимых для выполнения инструмента.
    /// </summary>
    /// <param name="input">JSON-аргументы вызова инструмента.</param>
    /// <returns>Список требуемых возможностей.</returns>
    IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) => [];
}
