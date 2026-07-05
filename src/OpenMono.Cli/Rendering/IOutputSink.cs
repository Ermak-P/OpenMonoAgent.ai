using OpenMono.Session;

namespace OpenMono.Rendering;

/// <summary>
/// Содержит метрики, собранные во время ответа ассистента.
/// </summary>
public sealed record TurnMetrics
{
    /// <summary>
    /// Количество токенов, использованных во входной подсказке.
    /// </summary>
    public int PromptTokens { get; init; }

    /// <summary>
    /// Количество токенов, сгенерированных в ответе.
    /// </summary>
    public int CompletionTokens { get; init; }

    /// <summary>
    /// Время до получения первого токена ответа.
    /// </summary>
    public TimeSpan TimeToFirstToken { get; init; }

    /// <summary>
    /// Полная длительность генерации ответа.
    /// </summary>
    public TimeSpan TotalElapsed { get; init; }
}

/// <summary>
/// Определяет контракт для вывода сообщений, статусов и артефактов рендеринга.
/// </summary>
public interface IOutputSink
{
    /// <summary>
    /// Получает или задаёт признак подробного режима вывода.
    /// </summary>
    bool Verbose { get; set; }

    /// <summary>
    /// Обозначает начало потокового ответа ассистента.
    /// </summary>
    void StartAssistantResponse();

    /// <summary>
    /// Добавляет очередную порцию текста в потоковый ответ.
    /// </summary>
    /// <param name="text">Фрагмент текста ответа.</param>
    void StreamText(string text);

    /// <summary>
    /// Завершает потоковый ответ ассистента.
    /// </summary>
    /// <param name="metrics">Необязательные метрики завершённого ответа.</param>
    void EndAssistantResponse(TurnMetrics? metrics = null);

    /// <summary>
    /// Добавляет текст размышления без указания метки агента.
    /// </summary>
    /// <param name="text">Текст размышления.</param>
    void AppendThinking(string text) => AppendThinking(text, null);

    /// <summary>
    /// Добавляет текст размышления с необязательной меткой агента.
    /// </summary>
    /// <param name="text">Текст размышления.</param>
    /// <param name="agentLabel">Метка агента, от имени которого выводится размышление.</param>
    void AppendThinking(string text, string? agentLabel);

    /// <summary>
    /// Сворачивает указанное количество символов размышления без указания метки агента.
    /// </summary>
    /// <param name="charCount">Количество символов, которые следует свернуть.</param>
    void CollapseThinking(int charCount) => CollapseThinking(charCount, null);

    /// <summary>
    /// Сворачивает указанное количество символов размышления.
    /// </summary>
    /// <param name="charCount">Количество символов, которые следует свернуть.</param>
    /// <param name="agentLabel">Метка агента, к которому относится сворачивание.</param>
    void CollapseThinking(int charCount, string? agentLabel);

    /// <summary>
    /// Показывает индикатор ожидания без явной метки агента.
    /// </summary>
    /// <param name="label">Необязательный текст индикатора.</param>
    void ShowWaitingIndicator(string? label = null) => ShowWaitingIndicator(label, null);

    /// <summary>
    /// Показывает индикатор ожидания.
    /// </summary>
    /// <param name="label">Необязательный текст индикатора.</param>
    /// <param name="agentLabel">Необязательная метка агента.</param>
    void ShowWaitingIndicator(string? label, string? agentLabel);

    /// <summary>
    /// Скрывает индикатор ожидания без указания метки агента.
    /// </summary>
    void ClearWaitingIndicator() => ClearWaitingIndicator(null);

    /// <summary>
    /// Скрывает индикатор ожидания.
    /// </summary>
    /// <param name="agentLabel">Метка агента, чей индикатор нужно скрыть.</param>
    void ClearWaitingIndicator(string? agentLabel);

    /// <summary>
    /// Выводит приветственное сообщение интерфейса.
    /// </summary>
    /// <param name="model">Имя используемой модели.</param>
    /// <param name="endpoint">Адрес конечной точки подключения.</param>
    void WriteWelcome(string model, string endpoint);

    /// <summary>
    /// Выводит Markdown-содержимое.
    /// </summary>
    /// <param name="markdown">Markdown-текст для отображения.</param>
    void WriteMarkdown(string markdown);

    /// <summary>
    /// Выводит отладочное сообщение.
    /// </summary>
    /// <param name="message">Текст отладочного сообщения.</param>
    void WriteDebug(string message);

    /// <summary>
    /// Сообщает о начале выполнения инструмента.
    /// </summary>
    /// <param name="toolName">Имя инструмента.</param>
    /// <param name="args">Аргументы запуска инструмента.</param>
    void WriteToolStart(string toolName, string args);

    /// <summary>
    /// Сообщает об успешном завершении инструмента.
    /// </summary>
    /// <param name="toolName">Имя инструмента.</param>
    void WriteToolSuccess(string toolName);

    /// <summary>
    /// Сообщает об ошибке выполнения инструмента.
    /// </summary>
    /// <param name="toolName">Имя инструмента.</param>
    /// <param name="error">Описание ошибки.</param>
    void WriteToolError(string toolName, string error);

    /// <summary>
    /// Сообщает об отказе в выполнении инструмента.
    /// </summary>
    /// <param name="toolName">Имя инструмента.</param>
    /// <param name="reason">Причина отказа.</param>
    void WriteToolDenied(string toolName, string reason);

    /// <summary>
    /// Выводит текстовый diff инструмента.
    /// </summary>
    /// <param name="diff">Строковое представление diff.</param>
    void WriteToolDiff(string diff);

    /// <summary>
    /// Выводит содержимое файла, возвращённое инструментом.
    /// </summary>
    /// <param name="toolName">Имя инструмента.</param>
    /// <param name="filePath">Путь к файлу.</param>
    /// <param name="content">Содержимое файла.</param>
    void WriteToolContent(string toolName, string filePath, string content) { }

    /// <summary>
    /// Выводит предупреждение.
    /// </summary>
    /// <param name="message">Текст предупреждения.</param>
    void WriteWarning(string message);

    /// <summary>
    /// Выводит сообщение об ошибке.
    /// </summary>
    /// <param name="message">Текст ошибки.</param>
    void WriteError(string message);

    /// <summary>
    /// Выводит информационное сообщение.
    /// </summary>
    /// <param name="message">Текст информационного сообщения.</param>
    void WriteInfo(string message);

    /// <summary>
    /// Выводит список задач.
    /// </summary>
    /// <param name="todos">Коллекция задач для отображения.</param>
    void WriteTodos(IReadOnlyList<TodoItem> todos);

    /// <summary>
    /// Очищает текущую отображаемую беседу.
    /// </summary>
    void ClearConversation();
}
