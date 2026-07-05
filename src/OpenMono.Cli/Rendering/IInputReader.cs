using OpenMono.Commands;
using OpenMono.Permissions;

namespace OpenMono.Rendering;

/// <summary>
/// Определяет контракт для чтения пользовательского ввода в интерфейсе приложения.
/// </summary>
public interface IInputReader
{
    /// <summary>
    /// Включает подсказки по доступным командам.
    /// </summary>
    /// <param name="registry">Реестр команд, используемый для построения подсказок.</param>
    void EnableCommandSuggestions(CommandRegistry registry);

    /// <summary>
    /// Считывает строку пользовательского ввода синхронно.
    /// </summary>
    /// <returns>Введённая пользователем строка.</returns>
    string ReadInput();

    /// <summary>
    /// Отображает палитру выбора команд и возвращает выбранную команду.
    /// </summary>
    /// <param name="registry">Реестр команд, доступных для выбора.</param>
    /// <returns>Выбранная команда или <see langword="null"/>, если выбор отменён.</returns>
    string? ShowCommandPicker(CommandRegistry registry);

    /// <summary>
    /// Асинхронно задаёт пользователю вопрос и ожидает ответ.
    /// </summary>
    /// <param name="question">Текст вопроса.</param>
    /// <param name="ct">Токен отмены ожидания.</param>
    /// <returns>Ответ пользователя.</returns>
    Task<string> AskUserAsync(string question, CancellationToken ct);

    /// <summary>
    /// Асинхронно запрашивает у пользователя решение по разрешению на выполнение инструмента.
    /// </summary>
    /// <param name="toolName">Имя инструмента, для которого требуется решение.</param>
    /// <param name="summary">Краткое описание предполагаемого действия.</param>
    /// <param name="ct">Токен отмены ожидания.</param>
    /// <returns>Ответ пользователя по разрешению.</returns>
    Task<PermissionResponse> AskPermissionAsync(string toolName, string summary, CancellationToken ct);
}
