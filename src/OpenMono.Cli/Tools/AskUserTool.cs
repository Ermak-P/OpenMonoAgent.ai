using System.Text.Json;
using OpenMono.Permissions;

namespace OpenMono.Tools;

/// <summary>
/// Задает пользователю вопрос и ожидает текстовый ответ.
/// </summary>
public sealed class AskUserTool : ToolBase
{
    /// <summary>
    /// Возвращает имя инструмента.
    /// </summary>
    public override string Name => "AskUser";

    /// <summary>
    /// Возвращает описание назначения инструмента.
    /// </summary>
    public override string Description => "Ask the user a question and wait for their response. Use when you need clarification or a decision.";

    /// <summary>
    /// Указывает, что инструмент не изменяет внешнее состояние.
    /// </summary>
    public override bool IsReadOnly => true;

    /// <summary>
    /// Возвращает уровень разрешений по умолчанию.
    /// </summary>
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    /// <summary>
    /// Описывает JSON-схему входных параметров инструмента.
    /// </summary>
    /// <returns>Построитель схемы входных данных.</returns>
    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("question", "The question to ask the user")
        .AddArray("options", "Optional list of choices for the user to pick from", new { type = "string" })
        .Require("question");

    /// <summary>
    /// Возвращает список возможностей, требуемых для вызова инструмента.
    /// </summary>
    /// <param name="input">JSON с параметрами вызова.</param>
    /// <returns>Пустой список возможностей.</returns>
    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) => [];

    /// <summary>
    /// Формирует вопрос, при необходимости добавляет варианты ответа и ожидает ответ пользователя.
    /// </summary>
    /// <param name="input">JSON с вопросом и необязательными вариантами ответа.</param>
    /// <param name="context">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Ответ пользователя, завернутый в результат инструмента.</returns>
    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var question = input.GetProperty("question").GetString()!;
        var hasOptions = input.TryGetProperty("options", out var opts);

        var prompt = question;
        if (hasOptions)
        {
            var options = opts.EnumerateArray().Select(o => o.GetString()!).ToList();
            // Варианты нумеруются заранее, чтобы пользователю было проще ответить номером.
            prompt += "\n" + string.Join('\n', options.Select((o, i) => $"  [{i + 1}] {o}"));
        }

        var response = await context.AskUser(prompt, ct);
        return ToolResult.Success(response);
    }
}
