using OpenMono.Commands;
using OpenMono.Permissions;

namespace OpenMono.Rendering;

/// <summary>
/// Предоставляет пустую реализацию чтения ввода для окружений без прямого доступа к пользователю.
/// </summary>
internal sealed class NullInputReader : IInputReader
{
    /// <summary>
    /// Игнорирует включение подсказок команд.
    /// </summary>
    /// <param name="registry">Реестр команд.</param>
    public void EnableCommandSuggestions(CommandRegistry registry) { }

    /// <summary>
    /// Возвращает пустую строку вместо чтения пользовательского ввода.
    /// </summary>
    /// <returns>Пустая строка.</returns>
    public string ReadInput() => string.Empty;

    /// <summary>
    /// Не показывает палитру команд и всегда возвращает отсутствие выбора.
    /// </summary>
    /// <param name="registry">Реестр команд.</param>
    /// <returns>Всегда <see langword="null"/>.</returns>
    public string? ShowCommandPicker(CommandRegistry registry) => null;

    /// <summary>
    /// Возвращает фиксированное сообщение о невозможности задать вопрос пользователю.
    /// </summary>
    /// <param name="question">Текст вопроса.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Сообщение-заглушка для подагента.</returns>
    public Task<string> AskUserAsync(string question, CancellationToken ct) =>
        // Подагент не может взаимодействовать с человеком напрямую, поэтому возвращается безопасный текст-заглушка.
        Task.FromResult("[Sub-agent cannot ask user questions. Make a decision based on available information and continue.]");

    /// <summary>
    /// Всегда отклоняет запрос на выполнение инструмента.
    /// </summary>
    /// <param name="toolName">Имя инструмента.</param>
    /// <param name="summary">Краткое описание действия.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Всегда <see cref="PermissionResponse.Deny"/>.</returns>
    public Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct) =>
        // В отсутствии пользователя безопаснее по умолчанию запрещать интерактивные разрешения.
        Task.FromResult(PermissionResponse.Deny);
}
