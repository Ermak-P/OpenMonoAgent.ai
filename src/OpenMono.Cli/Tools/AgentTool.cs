using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenMono.Agents;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;

namespace OpenMono.Tools;

/// <summary>
/// Запускает вложенного агента для выполнения сложной подзадачи в отдельном контексте диалога.
/// </summary>
public sealed class AgentTool : ToolBase
{
    /// <summary>
    /// Возвращает имя инструмента.
    /// </summary>
    public override string Name => "Agent";

    /// <summary>
    /// Возвращает описание назначения инструмента.
    /// </summary>
    public override string Description => "Spawn a sub-agent to handle a complex task. The sub-agent has its own conversation context and returns a summary when done.";

    /// <summary>
    /// Указывает, что инструмент безопасен для параллельного выполнения.
    /// </summary>
    public override bool IsConcurrencySafe => true;

    /// <summary>
    /// Ограничивает общее число одновременно выполняемых вложенных агентов.
    /// </summary>
    private static SemaphoreSlim? _slot;

    /// <summary>
    /// Хранит текущую емкость глобального семафора.
    /// </summary>
    private static int _slotCapacity;

    /// <summary>
    /// Содержит количество агентов, ожидающих освобождения слота.
    /// </summary>
    private static int _queued;

    /// <summary>
    /// Синхронизирует переинициализацию глобального семафора.
    /// </summary>
    private static readonly object _slotInitLock = new();

    /// <summary>
    /// Отслеживает количество активных дочерних агентов для каждой родительской сессии.
    /// </summary>
    private static readonly ConditionalWeakTable<SessionState, StrongBox<int>> _perParent = new();

    /// <summary>
    /// Возвращает семафор с требуемой емкостью, создавая его при необходимости.
    /// </summary>
    /// <param name="requested">Запрошенное число параллельных слотов.</param>
    /// <returns>Семафор для ограничения параллелизма.</returns>
    private static SemaphoreSlim EnsureSlot(int requested)
    {
        var capacity = Math.Max(1, requested);
        if (_slot is { } existing && _slotCapacity == capacity)
            return existing;
        lock (_slotInitLock)
        {
            if (_slot is null || _slotCapacity != capacity)
            {
                _slot?.Dispose();
                _slot = new SemaphoreSlim(capacity, capacity);
                _slotCapacity = capacity;
            }
            return _slot;
        }
    }

    /// <summary>
    /// Описывает JSON-схему входных параметров инструмента.
    /// </summary>
    /// <returns>Построитель схемы входных данных.</returns>
    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("description", "Short description of the task (3-5 words)")
        .AddString("prompt", "Detailed instructions for the sub-agent")
        .AddEnum("agent_type", "Agent type determines available tools (default: general-purpose)",
            "general-purpose", "Explore", "Plan", "Coder", "Verify")
        .Require("description", "prompt");

    /// <summary>
    /// Возвращает набор возможностей, необходимых для запуска дочернего агента.
    /// </summary>
    /// <param name="input">JSON с параметрами вызова инструмента.</param>
    /// <returns>Список требуемых возможностей.</returns>
    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var description = input.TryGetProperty("description", out var d) ? d.GetString() : "task";
        var agentType = input.TryGetProperty("agent_type", out var at) ? at.GetString() : "general-purpose";
        return [new AgentSpawnCap(agentType ?? "general-purpose", description ?? "task")];
    }

    /// <summary>
    /// Запускает вложенного агента и возвращает сводку по завершении его работы.
    /// </summary>
    /// <param name="input">JSON с описанием задачи, промптом и типом агента.</param>
    /// <param name="context">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат запуска вложенного агента.</returns>
    /// <remarks>
    /// Метод применяет ограничения на глубину вложенности, количество дочерних агентов
    /// у одного родителя и общий размер очереди ожидания.
    /// </remarks>
    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var description = input.GetProperty("description").GetString()!;
        var prompt = input.GetProperty("prompt").GetString()!;
        var agentType = input.TryGetProperty("agent_type", out var at) ? at.GetString()! : "general-purpose";

        if (!BuiltInAgents.All.TryGetValue(agentType, out var agentDef))
            return ToolResult.Error($"Unknown agent type: {agentType}. Valid: {string.Join(", ", BuiltInAgents.All.Keys)}");

        var depth = context.AgentDepth;
        var agentsCfg = context.Config.Agents;
        if (depth >= agentsCfg.MaxNestingDepth)
            return ToolResult.Error(
                $"Agent nesting depth limit ({agentsCfg.MaxNestingDepth}) reached at depth {depth}. " +
                "Sub-agents cannot spawn further sub-agents beyond this level.");

        var perParent = _perParent.GetValue(context.Session, _ => new StrongBox<int>(0));
        var newPerParent = Interlocked.Increment(ref perParent.Value);
        if (newPerParent > agentsCfg.MaxConcurrentPerParent)
        {
            Interlocked.Decrement(ref perParent.Value);
            return ToolResult.Error(
                $"Per-parent sub-agent fan-out limit ({agentsCfg.MaxConcurrentPerParent}) reached. " +
                "Wait for an in-flight sub-agent from this conversation to finish before spawning another.");
        }

        var newQueued = Interlocked.Increment(ref _queued);
        if (newQueued > agentsCfg.MaxQueuedAgents)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Decrement(ref perParent.Value);
            return ToolResult.Error(
                $"Sub-agent queue is full ({agentsCfg.MaxQueuedAgents}). " +
                "Too many sub-agents are already waiting; try again after some complete.");
        }

        var slot = EnsureSlot(agentsCfg.MaxConcurrentAgents);
        context.WriteOutput($"[Agent: {description}] Queuing {agentType} sub-agent (depth {depth}, queued {newQueued}/{agentsCfg.MaxQueuedAgents})...");
        try
        {
            await slot.WaitAsync(ct);
        }
        catch
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Decrement(ref perParent.Value);
            throw;
        }
        Interlocked.Decrement(ref _queued);
        try
        {
            context.WriteOutput($"[Agent: {description}] Starting...");

            var subSession = new SessionState();
            subSession.Meta.TokenTracker = new TokenTracker();
            var systemPrompt = agentDef.SystemPrompt
                ?? "You are a helpful coding assistant. Complete the task described below.";
            subSession.AddMessage(new Message { Role = MessageRole.System, Content = systemPrompt });

            var subTools = new ToolRegistry();
            foreach (var tool in context.ToolRegistry.All)
            {
                if (tool.Name == "Agent") continue;
                if (IsToolAllowed(tool.Name, agentDef.AllowedTools))
                    subTools.Register(tool);
            }

            var sink = new SubAgentOutputSink(description, context.WriteOutput, context.Output);
            var inputReader = new NullInputReader();
            // Дочерний агент работает без собственной консоли, поэтому нельзя допускать,
            // чтобы он дошел до интерактивного запроса разрешений родителя и завис.
            var childPermissions = context.Permissions.CreateChildEngine(sink, inputReader);
            var llm = new OpenAiCompatClient(context.Config.Llm) { ApiKey = context.Config.Llm.ApiKey };

            try
            {
                using var childLoop = new ConversationLoop(
                    llm:           llm,
                    tools:         subTools,
                    permissions:   childPermissions,
                    output:        sink,
                    input:         inputReader,
                    liveFeedback:  null,
                    config:        context.Config,
                    session:       subSession,
                    maxIterations: agentDef.MaxTurns,
                    agentDepth:    depth + 1);

                await childLoop.RunTurnAsync(prompt, null, ct);
            }
            finally
            {
                llm.Dispose();
            }

            var result = sink.CapturedText.Trim();
            if (string.IsNullOrEmpty(result))
                result = "Sub-agent completed but produced no text output. Check tool results above.";

            return ToolResult.Success(
                $"[Sub-agent '{description}' ({agentType}) completed]\n\n{result}");
        }
        finally
        {
            slot.Release();
            Interlocked.Decrement(ref perParent.Value);
            context.Output?.ClearWaitingIndicator();
            context.WriteOutput($"[Agent: {description}] Done.");
        }
    }

    /// <summary>
    /// Проверяет, разрешен ли инструмент для выбранного типа дочернего агента.
    /// </summary>
    /// <param name="toolName">Имя проверяемого инструмента.</param>
    /// <param name="allowedTools">Список разрешенных шаблонов имен.</param>
    /// <returns><see langword="true" />, если инструмент разрешен; иначе <see langword="false" />.</returns>
    private static bool IsToolAllowed(string toolName, string[] allowedTools)
    {
        foreach (var entry in allowedTools)
        {
            if (entry == "*") return true;
            if (entry.EndsWith('*'))
            {
                var prefix = entry[..^1];
                if (toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (toolName.Equals(entry, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
