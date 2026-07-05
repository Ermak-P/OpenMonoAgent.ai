using System.Text.Json;
using OpenMono.Permissions;
using OpenMono.Playbooks;

namespace OpenMono.Tools;

/// <summary>
/// Запускает playbook по имени и передает ему разобранные аргументы.
/// </summary>
public sealed class PlaybookTool : ToolBase
{
    /// <summary>
    /// Получает имя инструмента.
    /// </summary>
    public override string Name => "Playbook";

    /// <summary>
    /// Получает описание назначения инструмента.
    /// </summary>
    public override string Description => "Invoke a playbook by name. Playbooks are multi-step, typed, composable workflows.";

    /// <summary>
    /// Получает признак того, что выполнение не откладывается.
    /// </summary>
    public override bool IsDeferred => false;

    /// <summary>
    /// Реестр доступных playbook-ов.
    /// </summary>
    private readonly PlaybookRegistry _registry;

    /// <summary>
    /// Исполнитель, отвечающий за запуск playbook-а.
    /// </summary>
    private readonly PlaybookExecutor _executor;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="PlaybookTool"/>.
    /// </summary>
    /// <param name="registry">Реестр доступных playbook-ов.</param>
    /// <param name="executor">Исполнитель playbook-ов.</param>
    public PlaybookTool(PlaybookRegistry registry, PlaybookExecutor executor)
    {
        _registry = registry;
        _executor = executor;
    }

    /// <summary>
    /// Описывает схему входных параметров инструмента.
    /// </summary>
    /// <returns>Построитель схемы для запуска playbook-а.</returns>
    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("name", "Name of the playbook to run")
        .AddString("arguments", "Arguments to pass to the playbook")
        .AddBoolean("resume", "Resume from last checkpoint (default: false)")
        .Require("name");

    /// <summary>
    /// Возвращает список возможностей, необходимых для запуска playbook-а.
    /// </summary>
    /// <param name="input">JSON-аргументы вызова инструмента.</param>
    /// <returns>Пустой список, так как проверка доступа выполняется внутри шагов playbook-а.</returns>
    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) => [];

    /// <summary>
    /// Запускает выбранный playbook с указанными аргументами.
    /// </summary>
    /// <param name="input">JSON-аргументы вызова инструмента.</param>
    /// <param name="context">Контекст выполнения инструмента.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>Результат выполнения playbook-а.</returns>
    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var name = input.GetProperty("name").GetString()!;
        var arguments = input.TryGetProperty("arguments", out var argsEl) ? argsEl.GetString() ?? "" : "";
        var resume = input.TryGetProperty("resume", out var r) && r.GetBoolean();

        var playbook = _registry.Resolve(name);
        if (playbook is null)
        {
            var available = string.Join(", ", _registry.All.Select(p => p.Name));
            return ToolResult.Error($"Playbook '{name}' not found. Available: {available}");
        }

        var parameters = ParseArguments(arguments, playbook);

        PlaybookState? state = null;
        if (resume)
        {
            state = await PlaybookState.LoadAsync(
                context.Config.DataDirectory, name, context.Session.Id, ct);
        }

        var result = await _executor.ExecuteAsync(playbook, parameters, state, ct);
        return ToolResult.Success(result);
    }

    /// <summary>
    /// Разбирает строку аргументов командного вида в словарь параметров playbook-а.
    /// </summary>
    /// <param name="args">Строка аргументов в формате <c>--key value</c> или <c>--key=value</c>.</param>
    /// <param name="playbook">Определение playbook-а, используемое для обработки позиционных аргументов.</param>
    /// <returns>Словарь распознанных параметров.</returns>
    private static Dictionary<string, object> ParseArguments(string args, PlaybookDefinition playbook)
    {
        var result = new Dictionary<string, object>();
        if (string.IsNullOrWhiteSpace(args)) return result;

        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("--") && parts[i].Contains('='))
            {
                var kv = parts[i][2..].Split('=', 2);
                result[kv[0]] = kv[1];
            }
            else if (parts[i].StartsWith("--") && i + 1 < parts.Length)
            {
                result[parts[i][2..]] = parts[i + 1];
                i++;
            }
            else if (!result.ContainsKey("_positional"))
            {
                // Первый позиционный аргумент привязывается к первому обязательному параметру,
                // чтобы playbook можно было запускать в более короткой форме.
                var firstParam = playbook.Parameters.FirstOrDefault(p => p.Value.Required);
                if (firstParam.Key is not null)
                    result[firstParam.Key] = parts[i];
                else
                    result["_positional"] = parts[i];
            }
        }

        return result;
    }
}
