using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenMono.Config;

namespace OpenMono.Tools;

/// <summary>
/// Определяет доступность веб-сервисов на стороне inference через capability-эндпоинт шлюза.
/// </summary>
public static class GatewayCapabilities
{
    /// <summary>
    /// Перечисляет веб-сервисы, поддерживаемые шлюзом.
    /// </summary>
    public enum WebService
    {
        /// <summary>
        /// Сервис поиска.
        /// </summary>
        Search,

        /// <summary>
        /// Сервис скрейпинга страниц.
        /// </summary>
        Scrape,
    }

    /// <summary>
    /// Представляет набор возможностей, возвращаемых шлюзом.
    /// </summary>
    /// <param name="Search">Признак доступности сервиса поиска.</param>
    /// <param name="Scrape">Признак доступности сервиса скрейпинга.</param>
    private readonly record struct Capabilities(bool Search, bool Scrape);

    /// <summary>
    /// HTTP-клиент для короткого запроса к эндпоинту <c>/services</c>.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// Кэширует результаты пробного запроса по URL шлюза.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Task<Capabilities>> Cache = new();

    /// <summary>
    /// Определяет базовый URL шлюза по конфигурации.
    /// </summary>
    /// <param name="config">Конфигурация приложения.</param>
    /// <returns>URL шлюза или конечной точки LLM, если явный шлюз не задан.</returns>
    public static string? ResolveGateway(AppConfig config) =>
        !string.IsNullOrEmpty(config.Web.Gateway) ? config.Web.Gateway : config.Llm.Endpoint;

    /// <summary>
    /// Определяет, должен ли указанный сервис маршрутизироваться через шлюз.
    /// </summary>
    /// <param name="config">Конфигурация приложения.</param>
    /// <param name="service">Проверяемый веб-сервис.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns><see langword="true" />, если сервис доступен; иначе <see langword="false" />.</returns>
    /// <remarks>
    /// Явные флаги <c>web.search</c> и <c>web.scrape</c> имеют приоритет над автоматическим
    /// определением через шлюз.
    /// </remarks>
    public static async Task<bool> IsEnabledAsync(
        AppConfig config, WebService service, CancellationToken ct)
    {
        var configOverride = service == WebService.Search
            ? config.Web.SearchEnabled
            : config.Web.ScrapeEnabled;
        if (configOverride.HasValue)
            return configOverride.Value;

        var gateway = ResolveGateway(config);
        if (string.IsNullOrEmpty(gateway))
            return false;

        var caps = await ProbeAsync(gateway, config.Llm.ApiKey).WaitAsync(ct);
        return service == WebService.Search ? caps.Search : caps.Scrape;
    }

    /// <summary>
    /// Возвращает задачу пробного опроса шлюза с учетом кэша.
    /// </summary>
    /// <param name="gateway">Базовый URL шлюза.</param>
    /// <param name="apiKey">Необязательный API-ключ.</param>
    /// <returns>Задача, возвращающая обнаруженные возможности.</returns>
    private static Task<Capabilities> ProbeAsync(string gateway, string? apiKey) =>
        // Запрос кэшируется без внешнего токена отмены, чтобы отмена одного вызывающего
        // не испортила общий результат для остальных запросов.
        Cache.GetOrAdd(gateway.TrimEnd('/'), g => FetchAsync(g, apiKey));

    /// <summary>
    /// Выполняет фактический HTTP-запрос к шлюзу и разбирает ответ.
    /// </summary>
    /// <param name="gateway">Базовый URL шлюза.</param>
    /// <param name="apiKey">Необязательный API-ключ.</param>
    /// <returns>Набор возможностей, обнаруженных у шлюза.</returns>
    private static async Task<Capabilities> FetchAsync(string gateway, string? apiKey)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{gateway}/services");
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return default;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new Capabilities(
                Search: IsTrue(root, "search"),
                Scrape: IsTrue(root, "scrape"));
        }
        catch
        {
            // При любой ошибке вызывающий должен мягко откатиться к встроенному поведению.
            return default;
        }
    }

    /// <summary>
    /// Интерпретирует значение capability-поля как булево.
    /// </summary>
    /// <param name="root">Корневой JSON-объект ответа.</param>
    /// <param name="name">Имя свойства для проверки.</param>
    /// <returns><see langword="true" />, если свойство трактуется как включенное.</returns>
    private static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind switch
        {
            JsonValueKind.True => true,
            // Caddy подставляет значения окружения как есть, поэтому здесь допустимы и строки.
            JsonValueKind.String => el.GetString()?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on",
            _ => false,
        };
}
