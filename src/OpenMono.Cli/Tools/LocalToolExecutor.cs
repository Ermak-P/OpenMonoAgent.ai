using System.Diagnostics;
using System.Text.Json;
using OpenMono.Acp;
using OpenMono.Config;
using OpenMono.Hooks;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Tools;

/// <summary>
/// Выполняет инструменты локально, обеспечивая проверку схем, разрешений, хуков и кэширования.
/// </summary>
public sealed class LocalToolExecutor : IToolExecutor
{
    /// <summary>
    /// Журнал хода, в который записываются этапы обработки вызова инструмента.
    /// </summary>
    private readonly TurnJournal _journal;

    /// <summary>
    /// Выходной канал для отображения статуса выполнения инструмента.
    /// </summary>
    private readonly IOutputSink _output;

    /// <summary>
    /// Конфигурация приложения, включая рабочий каталог.
    /// </summary>
    private readonly AppConfig _config;

    /// <summary>
    /// Состояние текущей сессии.
    /// </summary>
    private readonly SessionState _session;

    /// <summary>
    /// Компонент проверки разрешений и возможностей инструмента.
    /// </summary>
    private readonly PermissionEngine _permissions;

    /// <summary>
    /// Кэш результатов для повторяемых вызовов инструментов только на чтение.
    /// </summary>
    private readonly ToolResultCache _cache;

    /// <summary>
    /// Хранилище артефактов для крупных результатов инструментов.
    /// </summary>
    private readonly ArtifactStore _artifactStore;

    /// <summary>
    /// Исполнитель хуков до и после вызова инструмента.
    /// </summary>
    private readonly HookRunner _hookRunner;

    /// <summary>
    /// Необязательный приемник ACP-событий для телеметрии вызовов инструментов.
    /// </summary>
    private readonly IAcpEventSink? _sink;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="LocalToolExecutor"/>.
    /// </summary>
    /// <param name="journal">Журнал текущего хода.</param>
    /// <param name="output">Выходной канал для пользовательских сообщений.</param>
    /// <param name="config">Конфигурация приложения.</param>
    /// <param name="session">Состояние текущей сессии.</param>
    /// <param name="permissions">Движок проверки разрешений.</param>
    /// <param name="cache">Кэш результатов инструментов.</param>
    /// <param name="artifactStore">Хранилище артефактов крупных результатов.</param>
    /// <param name="hookRunner">Исполнитель хуков вокруг вызова инструмента.</param>
    /// <param name="sink">Необязательный приемник ACP-событий.</param>
    public LocalToolExecutor(
        TurnJournal journal,
        IOutputSink output,
        AppConfig config,
        SessionState session,
        PermissionEngine permissions,
        ToolResultCache cache,
        ArtifactStore artifactStore,
        HookRunner hookRunner,
        IAcpEventSink? sink = null)
    {
        _journal = journal;
        _output = output;
        _config = config;
        _session = session;
        _permissions = permissions;
        _cache = cache;
        _artifactStore = artifactStore;
        _hookRunner = hookRunner;
        _sink = sink;
    }

    /// <summary>
    /// Выполняет один вызов инструмента с полной цепочкой валидации, авторизации и постобработки.
    /// </summary>
    /// <param name="call">Описание вызова инструмента.</param>
    /// <param name="tool">Экземпляр инструмента или <see langword="null"/>, если инструмент не зарегистрирован.</param>
    /// <param name="ctx">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат выполнения инструмента.</returns>
    /// <remarks>
    /// Метод учитывает режим планирования, запускает pre/post hooks, пишет телеметрию и инвалидирует связанные кэши после операций записи.
    /// </remarks>
    public async Task<ToolResult> ExecuteAsync(ToolCall call, ITool? tool, ToolContext ctx, CancellationToken ct)
    {
        if (tool is null)
            return ToolResult.Error($"Unknown tool: {call.Name}");

        _journal.RecordToolCallReceived(call.Id, call.Name, call.Arguments);

        JsonElement input;
        try
        {
            input = JsonDocument.Parse(call.Arguments).RootElement;
        }
        catch (JsonException ex)
        {
            _journal.RecordSchemaRejected(call.Id, $"json_parse: {ex.Message}");
            return ToolResult.Error(
                $"Invalid JSON arguments for {call.Name}: {ex.Message}\nRaw: {call.Arguments[..Math.Min(200, call.Arguments.Length)]}");
        }

        var validationError = SchemaValidator.Validate(tool.Name, tool.InputSchema, input);
        if (validationError is not null)
        {
            _journal.RecordSchemaRejected(call.Id, validationError);
            _output.WriteToolDenied(call.Name, validationError);
            Log.Warn($"Tool schema rejected: {call.Name} — {validationError}");
            return ToolResult.Error(validationError);
        }
        _journal.RecordSchemaValidated(call.Id);

        var sanityError = SanityCheck.Check(call.Name, input, _config.WorkingDirectory);
        if (sanityError is not null)
        {
            _journal.RecordSanityRejected(call.Id, sanityError);
            _output.WriteToolDenied(call.Name, sanityError);
            Log.Warn($"Tool sanity-rejected: {call.Name} — {sanityError}");
            return ToolResult.Error(sanityError);
        }
        _journal.RecordSanityChecked(call.Id);

        if (_session.Meta.PlanMode && !tool.IsReadOnly)
        {
            // В режиме планирования запрещаем любые записи, чтобы агент сначала согласовал подход.
            var planModeError = $"Plan mode is active — investigate and write a plan, do not edit files. " +
                                $"Call ExitPlanMode with your completed plan to resume, then retry {call.Name}.";
            _journal.RecordPermissionDecided(call.Id, false, "plan_mode_active");
            _output.WriteToolDenied(call.Name, planModeError);
            return ToolResult.Error(planModeError);
        }

        var capabilities = tool.RequiredCapabilities(input);
        bool allowed;
        string? reason;

        if (capabilities.Count > 0)
        {
            var capDecision = await _permissions.CheckCapabilitiesAsync(tool.Name, capabilities, ct);
            allowed = capDecision.Allowed;
            reason = capDecision.Reason;
        }
        else
        {
            var permLevel = tool.RequiredPermission(input);
            var legacyDecision = await _permissions.CheckAsync(tool.Name, input, permLevel, ct);
            allowed = legacyDecision.Allowed;
            reason = legacyDecision.Reason;
        }

        if (!allowed)
        {
            _journal.RecordPermissionDecided(call.Id, false, reason);
            _output.WriteToolDenied(call.Name, reason ?? "Permission denied");
            Log.Info($"Tool denied: {call.Name} — {reason ?? "User denied"}");
            return ToolResult.Error(
                $"Permission denied for {call.Name}: {reason ?? "User denied"}. " +
                $"Do not retry this tool call. Ask the user how to proceed instead.");
        }
        _journal.RecordPermissionDecided(call.Id, true);

        if (tool.IsReadOnly && _cache.TryGet(call.Name, input, out var cachedResult) && cachedResult is not null)
        {
            // Для инструментов только на чтение возвращаем кэшированный результат без повторного запуска.
            _journal.RecordToolStarted(call.Id);
            _journal.RecordToolCompleted(call.Id, cachedResult.Class, cachedResult.Artifacts.Select(a => a.Id).ToList());
            _output.WriteToolStart(call.Name, call.Arguments);
            _output.WriteToolSuccess(call.Name);
            Log.Debug($"Tool cache hit: {call.Name}");
            if (_sink is not null)
            {
                await _sink.OnToolStartAsync(call.Id, call.Name, SummarizeToolArgs(call.Arguments));
                await _sink.OnToolEndAsync(call.Id, call.Name, ok: true, durationMs: 0.0);
            }
            return cachedResult with { ModelPreview = $"[cached] {cachedResult.ModelPreview}" };
        }

        _output.WriteToolStart(call.Name, call.Arguments);
        _session.Meta.TokenTracker?.RecordToolUse(call.Name);
        _journal.RecordToolStarted(call.Id);

        var stopwatch = Stopwatch.StartNew();
        if (_sink is not null)
            await _sink.OnToolStartAsync(call.Id, call.Name, SummarizeToolArgs(call.Arguments));

        ToolResult result;
        try
        {
            await _hookRunner.RunPreToolUseHooksAsync(call.Name, call.Arguments, ct);

            Log.Debug($"Tool executing: {call.Name}");
            result = await tool.ExecuteAsync(input, ctx, ct);

            await _hookRunner.RunPostToolUseHooksAsync(call.Name, result.Content, ct);

            if (result.Class == ResultClass.Success && result.ModelPreview.Length > _artifactStore.LargeOutputThreshold)
            {
                // Крупный вывод переносится в артефакт, чтобы не перегружать основной канал ответа.
                result = _artifactStore.PersistAndReplace(result, call.Name);
                Log.Debug($"Tool output persisted as artifact: {call.Name}");
            }

            if (tool.IsReadOnly && result.Class == ResultClass.Success)
            {
                _cache.Put(call.Name, input, result);
            }

            if (!tool.IsReadOnly && call.Name is "FileWrite" or "FileEdit" or "ApplyPatch")
            {
                if (input.TryGetProperty("file_path", out var pathEl) && pathEl.GetString() is { } filePath)
                {
                    var resolvedPath = Path.GetFullPath(filePath, _config.WorkingDirectory);
                    // После записи сбрасываем все кэши, завязанные на конкретный файл.
                    _cache.InvalidatePath(resolvedPath);
                    FileReadTool.InvalidateCache(resolvedPath);
                }
            }

            var artifactIds = result.Artifacts.Select(a => a.Id).ToList();
            _journal.RecordToolCompleted(call.Id, result.Class, artifactIds);

            if (result.IsError)
            {
                _output.WriteToolError(call.Name, result.ErrorMessage ?? "Unknown error");
                Log.Warn($"Tool error: {call.Name} — {result.ErrorMessage}");
            }
            else
            {
                _output.WriteToolSuccess(call.Name);

                if (result.Diff is not null)
                    _output.WriteToolDiff(result.Diff);

                if (call.Name is "FileRead" or "FileWrite" &&
                    input.TryGetProperty("file_path", out var fpProp) &&
                    fpProp.GetString() is { } filePath)
                {
                    var content = call.Name == "FileWrite"
                        ? (input.TryGetProperty("content", out var cp) ? cp.GetString() ?? "" : "")
                        : result.ModelPreview;
                    _output.WriteToolContent(call.Name, filePath, content);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _journal.RecordToolCrashed(call.Id, "OperationCanceledException", "cancelled");
            Log.Info($"Tool cancelled: {call.Name}");
            result = ToolResult.Cancelled($"{call.Name} was cancelled");
        }
        catch (Exception ex)
        {
            _journal.RecordToolCrashed(call.Id, ex.GetType().Name, ex.Message);
            _output.WriteToolError(call.Name, ex.Message);
            Log.Error($"Tool exception: {call.Name}", ex);
            result = ToolResult.Crash($"Tool execution failed: {ex.Message}", "Try with different parameters or report this as a bug.");
        }

        stopwatch.Stop();
        if (_sink is not null)
            await _sink.OnToolEndAsync(call.Id, call.Name, ok: !result.IsError, durationMs: stopwatch.Elapsed.TotalMilliseconds);

        return result;
    }

    /// <summary>
    /// Подготавливает короткое однострочное представление аргументов для логов и телеметрии.
    /// </summary>
    /// <param name="arguments">Исходная строка JSON-аргументов.</param>
    /// <returns>Усеченная и нормализованная строка аргументов.</returns>
    internal static string SummarizeToolArgs(string arguments)
    {
        if (string.IsNullOrEmpty(arguments)) return "";
        var trimmed = arguments.AsSpan().Trim();
        if (trimmed.Length == 0) return "";
        var snippet = trimmed.Length <= 120 ? trimmed.ToString() : trimmed[..120].ToString() + "...";

        // Переводим многострочный JSON в компактный фрагмент, удобный для логирования.
        return string.Join(" ", snippet.Split(new[] { '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));
    }
}