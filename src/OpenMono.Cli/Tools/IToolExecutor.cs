using OpenMono.Session;

namespace OpenMono.Tools;

/// <summary>
/// Определяет компонент, отвечающий за запуск инструментов и обработку их результатов.
/// </summary>
public interface IToolExecutor
{
    /// <summary>
    /// Выполняет вызов инструмента в заданном контексте.
    /// </summary>
    /// <param name="call">Описание вызова инструмента.</param>
    /// <param name="tool">Экземпляр инструмента или <see langword="null"/>, если инструмент не найден.</param>
    /// <param name="ctx">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат выполнения инструмента.</returns>
    Task<ToolResult> ExecuteAsync(ToolCall call, ITool? tool, ToolContext ctx, CancellationToken ct);

    /// <summary>
    /// Получает признак того, что исполнитель должен приостанавливать ход после выдачи результата.
    /// </summary>
    bool PausesAfterEmit => false;
}
