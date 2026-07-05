using System.Text.Json;
using OpenMono.Session;

namespace OpenMono.Tools;

/// <summary>
/// Включает режим планирования, в котором агент должен сначала подготовить план действий.
/// </summary>
public sealed class EnterPlanModeTool : ToolBase
{
    /// <summary>
    /// Получает имя инструмента.
    /// </summary>
    public override string Name => "EnterPlanMode";

    /// <summary>
    /// Получает описание назначения инструмента.
    /// </summary>
    public override string Description =>
        """
        Use this tool proactively before starting any non-trivial implementation task.
        Getting user sign-off on your approach before writing code prevents wasted effort.

        ## When to use EnterPlanMode

        Use it when ANY of these apply:

        - New feature that involves architectural decisions (where does it go? what pattern?)
        - Multiple valid approaches exist and the choice meaningfully affects the codebase
        - Changes that touch more than 2-3 files
        - Unclear requirements — you need to explore before you can understand the scope
        - High-impact restructuring where the wrong approach causes significant rework
        - You would normally ask a clarifying question about the approach — plan instead

        ## When NOT to use EnterPlanMode

        Skip it for simple tasks:
        - Single-line or few-line fixes, typos, obvious bugs
        - The user gave specific, detailed instructions and the path is clear
        - Pure research/exploration (use the Agent tool with Explore type instead)
        - The user said "just do it" or "go ahead" — start working

        ## Examples

        GOOD — use EnterPlanMode:
          "Add user authentication" — session vs JWT, middleware structure, many files
          "Improve performance" — need to profile first, multiple strategies possible
          "Refactor the data layer" — architectural decisions, high impact

        BAD — do not use EnterPlanMode:
          "Fix the typo in the README"
          "Add a console.log to debug this"
          "What files handle routing?" — this is research, not implementation
        """;

    /// <summary>
    /// Получает уровень разрешений по умолчанию.
    /// </summary>
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    /// <summary>
    /// Получает признак того, что инструмент не изменяет проектные файлы.
    /// </summary>
    public override bool IsReadOnly => true;

    /// <summary>
    /// Описывает схему входных параметров инструмента.
    /// </summary>
    /// <returns>Построитель схемы для причины входа в режим планирования.</returns>
    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("reason", "Why you are entering plan mode — what task are you planning?")
        .Require("reason");

    /// <summary>
    /// Активирует режим планирования для текущей сессии.
    /// </summary>
    /// <param name="input">JSON-аргументы вызова инструмента.</param>
    /// <param name="context">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат активации режима планирования.</returns>
    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var reason = input.GetProperty("reason").GetString()!;

        if (context.Session.Meta.PlanMode)
            return Task.FromResult(ToolResult.Error("Already in plan mode. Use ExitPlanMode to leave."));

        context.Session.Meta.PlanMode = true;

        return Task.FromResult(ToolResult.Success(PlanModeInstructions.Activation(reason)));
    }
}

/// <summary>
/// Выключает режим планирования и сохраняет подготовленный план в состоянии сессии.
/// </summary>
public sealed class ExitPlanModeTool : ToolBase
{
    /// <summary>
    /// Получает имя инструмента.
    /// </summary>
    public override string Name => "ExitPlanMode";

    /// <summary>
    /// Получает описание назначения инструмента.
    /// </summary>
    public override string Description =>
        """
        Exit plan mode and present the implementation plan to the user for approval.
        Call this when your plan is complete and ready for the user to review.

        The `plan` argument must be a structured numbered plan — not vague prose.
        It should list: the approach, every file that changes, risks, and complexity.
        """;

    /// <summary>
    /// Получает уровень разрешений по умолчанию.
    /// </summary>
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    /// <summary>
    /// Получает признак того, что инструмент не изменяет проектные файлы.
    /// </summary>
    public override bool IsReadOnly => true;

    /// <summary>
    /// Описывает схему входных параметров инструмента.
    /// </summary>
    /// <returns>Построитель схемы для плана реализации.</returns>
    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("plan", "The full numbered implementation plan to present to the user")
        .Require("plan");

    /// <summary>
    /// Завершает режим планирования и публикует сформированный план в выводе сессии.
    /// </summary>
    /// <param name="input">JSON-аргументы вызова инструмента.</param>
    /// <param name="context">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат завершения режима планирования с разрывом хода.</returns>
    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var plan = input.GetProperty("plan").GetString()!;

        if (!context.Session.Meta.PlanMode)
            return Task.FromResult(ToolResult.Error(
                "Plan mode is not currently active — the previous plan was already presented to the user. " +
                "If you need to plan again, call EnterPlanMode first, then call ExitPlanMode with the new plan."));

        context.Session.Meta.PlanMode = false;
        context.Session.Meta.LastPlan = plan;

        // План дублируется в выводе, чтобы пользователь увидел его вне структурированного результата инструмента.
        context.WriteOutput($"\n## Plan\n\n{plan}\n");

        return Task.FromResult(ToolResult.Success(
            $"Exited plan mode. Present the plan below to the user, then stop — " +
            $"write tools will be available on the next turn.\n\n{plan}")
            .WithBreakTurn());
    }
}
