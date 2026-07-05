namespace OpenMono.Rendering;

/// <summary>
/// Определяет абстракцию над терминалом для чтения ввода и записи вывода.
/// </summary>
public interface ITerminal
{
    /// <summary>
    /// Получает текущую ширину окна терминала.
    /// </summary>
    int WindowWidth  { get; }

    /// <summary>
    /// Получает текущую высоту окна терминала.
    /// </summary>
    int WindowHeight { get; }

    /// <summary>
    /// Получает значение, указывающее, перенаправлен ли вывод.
    /// </summary>
    bool IsOutputRedirected { get; }

    /// <summary>
    /// Возникает, когда терминал сообщает о пользовательском прерывании.
    /// </summary>
    event Action<ConsoleSpecialKey>? InterruptRequested;

    /// <summary>
    /// Асинхронно записывает строку без перевода строки.
    /// </summary>
    /// <param name="value">Текст для записи.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Ожидаемая задача записи.</returns>
    ValueTask WriteAsync(string value, CancellationToken ct = default);

    /// <summary>
    /// Асинхронно записывает строку с переводом строки.
    /// </summary>
    /// <param name="value">Текст для записи.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Ожидаемая задача записи.</returns>
    ValueTask WriteLineAsync(string value, CancellationToken ct = default);

    /// <summary>
    /// Пытается прочитать нажатую клавишу без блокировки потока.
    /// </summary>
    /// <returns>Информация о нажатой клавише или <see langword="null"/>, если клавиша недоступна.</returns>
    ConsoleKeyInfo? TryReadKey();

    /// <summary>
    /// Асинхронно ожидает и возвращает следующую нажатую клавишу.
    /// </summary>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Информация о нажатой клавише.</returns>
    ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct = default);
}
