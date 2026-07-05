using System.Text;
using System.Text.RegularExpressions;
using OpenMono.Permissions;

namespace OpenMono.Tools;

/// <summary>
/// Разбирает shell-команды на сегменты, перенаправления и вложенные подкоманды.
/// </summary>
public static class BashParser
{
    /// <summary>
    /// Разбирает строку shell-команды в структурированное представление.
    /// </summary>
    /// <param name="command">Исходная командная строка.</param>
    /// <returns>Результат разбора команды.</returns>
    public static BashParseResult Parse(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new BashParseResult([], [], []);

        var segments = new List<CommandSegment>();
        var redirections = new List<Redirection>();
        var subshells = new List<string>();

        var (processed, extractedSubshells) = ExtractSubshells(command);
        subshells.AddRange(extractedSubshells);

        var rawSegments = SplitOnCompoundOperators(processed);

        foreach (var (raw, op) in rawSegments)
        {

            var (cleaned, segRedirections) = ExtractRedirections(raw);
            redirections.AddRange(segRedirections);

            var (binary, args) = ParseSimpleCommand(cleaned);
            if (!string.IsNullOrWhiteSpace(binary))
            {
                segments.Add(new CommandSegment(binary, args, op));
            }
        }

        foreach (var subshell in subshells.Take(50))
        {
            // Вложенные команды разбираются рекурсивно, но с ограничением,
            // чтобы искусственно глубокий ввод не раздувал объем анализа.
            var subResult = Parse(subshell);
            segments.AddRange(subResult.Segments);
            redirections.AddRange(subResult.Redirections);
        }

        return new BashParseResult(segments, redirections, subshells);
    }

    /// <summary>
    /// Преобразует результат разбора команды в список требуемых возможностей.
    /// </summary>
    /// <param name="result">Структурированный результат разбора.</param>
    /// <returns>Список возможностей, соответствующих сегментам и перенаправлениям.</returns>
    public static IReadOnlyList<Capability> ToCapabilities(BashParseResult result)
    {
        var caps = new List<Capability>();

        foreach (var seg in result.Segments)
        {
            caps.Add(new ProcessExecCap(seg.Binary, seg.Args));
        }

        foreach (var redir in result.Redirections)
        {
            if (redir.IsInput)
                caps.Add(new FileReadCap(redir.Target));
            else
                caps.Add(new FileWriteCap(redir.Target, redir.IsAppend ? "modify" : "create"));
        }

        return caps;
    }

    /// <summary>
    /// Проверяет разобранную команду на потенциально разрушительные шаблоны.
    /// </summary>
    /// <param name="result">Результат разбора shell-команды.</param>
    /// <returns>Текст причины, если команда опасна; иначе <see langword="null" />.</returns>
    public static string? CheckDestructive(BashParseResult result)
    {
        var segments = result.Segments.ToList();
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];

            var reason = CheckSegmentDestructive(seg);
            if (reason is not null)
                return reason;

            if (seg.Operator == CompoundOp.Pipe && i + 1 < segments.Count)
            {
                var nextSeg = segments[i + 1];
                if (IsShellInterpreter(nextSeg.Binary))
                    return $"Pipe to shell interpreter ({nextSeg.Binary}) is a potential injection vector";
            }
        }

        if (segments.Count > 1)
        {
            var lastSeg = segments[^1];
            if (IsShellInterpreter(lastSeg.Binary))
            {
                // Дополнительная проверка нужна, потому что оператор pipe хранится
                // у предыдущего сегмента, а потенциально опасен последний интерпретатор.
                for (var i = 0; i < segments.Count - 1; i++)
                {
                    if (segments[i].Operator == CompoundOp.Pipe)
                        return $"Pipe to shell interpreter ({lastSeg.Binary}) is a potential injection vector";
                }
            }
        }

        foreach (var redir in result.Redirections)
        {
            if (!redir.IsInput && IsProtectedPath(redir.Target))
                return $"Write redirection to protected path: {redir.Target}";
        }

        return null;
    }

    /// <summary>
    /// Определяет, является ли бинарник shell-интерпретатором.
    /// </summary>
    /// <param name="binary">Имя или путь до исполняемого файла.</param>
    /// <returns><see langword="true" />, если это shell-интерпретатор; иначе <see langword="false" />.</returns>
    private static bool IsShellInterpreter(string binary)
    {
        var shellInterpreters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sh", "bash", "zsh", "fish", "csh", "tcsh", "ksh", "dash",
            "/bin/sh", "/bin/bash", "/bin/zsh", "/usr/bin/sh", "/usr/bin/bash"
        };
        return shellInterpreters.Contains(binary);
    }

    /// <summary>
    /// Проверяет отдельный сегмент команды на опасные операции.
    /// </summary>
    /// <param name="seg">Сегмент команды для анализа.</param>
    /// <returns>Причина блокировки или <see langword="null" />, если сегмент безопасен.</returns>
    private static string? CheckSegmentDestructive(CommandSegment seg)
    {
        var binary = seg.Binary.ToLowerInvariant();
        var args = seg.Args.Select(a => a.ToLowerInvariant()).ToList();
        var fullCmd = $"{binary} {string.Join(" ", args)}".Trim();

        if (binary is "rm")
        {
            var hasRecursiveForce = args.Any(a => a.Contains('r') && a.Contains('f') && a.StartsWith('-'));
            if (hasRecursiveForce)
            {
                var targets = args.Where(a => !a.StartsWith('-')).ToList();
                foreach (var target in targets)
                {
                    if (target is "/" or "~" or "." or "*" or "$home" or "${home}")
                        return $"Destructive rm pattern: rm -rf {target}";
                    if (IsProtectedPath(target))
                        return $"rm -rf targeting protected path: {target}";
                }
            }
        }

        if (fullCmd.Contains(":(){:|:&};:"))
            return "Fork bomb detected";

        if (binary is "dd")
        {
            var ofArg = args.FirstOrDefault(a => a.StartsWith("of="));
            if (ofArg is not null)
            {
                var target = ofArg[3..];
                if (target.StartsWith("/dev/sd") || target.StartsWith("/dev/nvme") || target.StartsWith("/dev/hd"))
                    return $"dd writing to block device: {target}";
            }
        }

        if (binary is "shutdown" or "reboot" or "halt" or "poweroff" or "init")
            return $"System control command: {binary}";

        if (binary is "mkfs" || binary.StartsWith("mkfs."))
            return $"Filesystem creation command: {binary}";

        if (binary is "kill" or "pkill" && args.Contains("-1"))
            return "Kill all processes pattern detected";

        if (binary is "chmod" or "chown")
        {
            if (args.Any(a => a is "/" or "-r" or "-R"))
            {
                var targets = args.Where(a => !a.StartsWith('-')).ToList();
                if (targets.Any(t => t is "/" || IsProtectedPath(t)))
                    return $"{binary} on protected path";
            }
        }

        if (binary is "xargs")
        {
            var safeXargsTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "echo", "printf", "wc", "grep", "head", "tail" };

            var flagsTakingArg = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "-I", "-n", "-P", "-d", "-s", "--max-args", "--max-procs", "--delimiter", "--replace" };
            string? targetCmd = null;
            var argList = seg.Args.ToList();
            for (var i = 0; i < argList.Count; i++)
            {
                // У этих флагов следующий токен является аргументом флага, а не именем команды.
                if (flagsTakingArg.Contains(argList[i])) { i++; continue; }
                if (argList[i].StartsWith('-')) continue;
                targetCmd = argList[i];
                break;
            }

            if (targetCmd is not null && !safeXargsTargets.Contains(targetCmd))
                return $"xargs with unsafe target '{targetCmd}': only echo/printf/wc/grep/head/tail are permitted. " +
                       "Use an explicit loop or direct command instead.";
        }

        return null;
    }

    /// <summary>
    /// Проверяет, относится ли путь к защищенным системным расположениям.
    /// </summary>
    /// <param name="path">Путь для проверки.</param>
    /// <returns><see langword="true" />, если путь считается защищенным; иначе <see langword="false" />.</returns>
    private static bool IsProtectedPath(string path)
    {
        var normalized = path.Replace('\\', '/').ToLowerInvariant();

        if (normalized is "/dev/null" or "/dev/stdout" or "/dev/stderr" or "/dev/tty" or "/dev/zero")
            return false;
        if (normalized.StartsWith("/dev/fd/"))
            return false;

        string[] protectedPrefixes =
        [
            "/etc/", "/usr/", "/bin/", "/sbin/", "/boot/",
            "/sys/", "/proc/", "/dev/", "/system/", "/library/"
        ];
        return protectedPrefixes.Any(p => normalized.StartsWith(p) || normalized == p.TrimEnd('/'));
    }

    /// <summary>
    /// Извлекает подкоманды из конструкций <c>$(...)</c>, <c>`...`</c> и <c>(...)</c>.
    /// </summary>
    /// <param name="command">Исходная командная строка.</param>
    /// <returns>Команда с маркерами подкоманд и список извлеченных тел подкоманд.</returns>
    private static (string Processed, List<string> Subshells) ExtractSubshells(string command)
    {
        var subshells = new List<string>();
        var result = new StringBuilder();
        var i = 0;

        while (i < command.Length)
        {

            if (i < command.Length - 1 && command[i] == '$' && command[i + 1] == '(')
            {
                var (content, endIdx) = ExtractParenContent(command, i + 1);
                if (content is not null)
                {
                    subshells.Add(content);
                    result.Append("__SUBSHELL__");
                    i = endIdx + 1;
                    continue;
                }
            }

            if (command[i] == '`')
            {
                var endTick = command.IndexOf('`', i + 1);
                if (endTick > i)
                {
                    subshells.Add(command[(i + 1)..endTick]);
                    result.Append("__SUBSHELL__");
                    i = endTick + 1;
                    continue;
                }
            }

            if (command[i] == '(' && (i == 0 || command[i - 1] != '$'))
            {
                var (content, endIdx) = ExtractParenContent(command, i);
                if (content is not null)
                {
                    subshells.Add(content);
                    result.Append("__SUBSHELL__");
                    i = endIdx + 1;
                    continue;
                }
            }

            result.Append(command[i]);
            i++;
        }

        return (result.ToString(), subshells);
    }

    /// <summary>
    /// Извлекает содержимое скобочной группы с учетом вложенности и кавычек.
    /// </summary>
    /// <param name="s">Строка, содержащая группу.</param>
    /// <param name="openParen">Индекс открывающей скобки.</param>
    /// <returns>Содержимое группы и индекс закрывающей скобки.</returns>
    private static (string? Content, int EndIndex) ExtractParenContent(string s, int openParen)
    {
        if (openParen >= s.Length || s[openParen] != '(')
            return (null, openParen);

        var depth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = openParen; i < s.Length; i++)
        {
            var c = s[i];

            if (c == '\\' && i + 1 < s.Length)
            {
                i++;
                continue;
            }

            if (c == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
            else if (c == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;

            if (!inSingleQuote && !inDoubleQuote)
            {
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                        return (s[(openParen + 1)..i], i);
                }
            }
        }

        return (null, s.Length);
    }

    /// <summary>
    /// Делит командную строку по составным операторам shell вне кавычек.
    /// </summary>
    /// <param name="command">Командная строка для разбиения.</param>
    /// <returns>Список сегментов и операторов, следующих за ними.</returns>
    private static List<(string Segment, CompoundOp Operator)> SplitOnCompoundOperators(string command)
    {
        var result = new List<(string, CompoundOp)>();
        var current = new StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var i = 0;

        while (i < command.Length)
        {
            var c = command[i];

            if (c == '\\' && i + 1 < command.Length)
            {
                current.Append(c);
                current.Append(command[i + 1]);
                i += 2;
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                current.Append(c);
                i++;
                continue;
            }
            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                current.Append(c);
                i++;
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote)
            {
                // Операторы учитываются только вне кавычек, иначе строки вроде
                // "echo a && b" были бы разобраны неверно.
                if (i + 1 < command.Length && command[i] == '&' && command[i + 1] == '&')
                {
                    result.Add((current.ToString().Trim(), CompoundOp.And));
                    current.Clear();
                    i += 2;
                    continue;
                }
                if (i + 1 < command.Length && command[i] == '|' && command[i + 1] == '|')
                {
                    result.Add((current.ToString().Trim(), CompoundOp.Or));
                    current.Clear();
                    i += 2;
                    continue;
                }
                if (command[i] == ';')
                {
                    result.Add((current.ToString().Trim(), CompoundOp.Sequence));
                    current.Clear();
                    i++;
                    continue;
                }
                if (command[i] == '|')
                {
                    result.Add((current.ToString().Trim(), CompoundOp.Pipe));
                    current.Clear();
                    i++;
                    continue;
                }
            }

            current.Append(c);
            i++;
        }

        var final = current.ToString().Trim();
        if (!string.IsNullOrEmpty(final))
            result.Add((final, CompoundOp.None));

        return result;
    }

    /// <summary>
    /// Извлекает перенаправления ввода-вывода из сегмента команды.
    /// </summary>
    /// <param name="segment">Сегмент команды без разбиения на токены.</param>
    /// <returns>Очищенный сегмент и найденные перенаправления.</returns>
    private static (string Cleaned, List<Redirection> Redirections) ExtractRedirections(string segment)
    {
        var redirections = new List<Redirection>();
        var cleaned = segment;

        var redirectPattern = new Regex(
            @"(?<fd>\d*)(>>?|<)(?<target>\S+)",
            RegexOptions.Compiled);

        var matches = redirectPattern.Matches(cleaned);
        foreach (Match m in matches)
        {
            var op = m.Groups[1].Value;
            var target = m.Groups["target"].Value.Trim('"', '\'');

            // Перенаправления вида 2>&1 меняют файловый дескриптор, а не открывают путь.
            if (target.StartsWith('&'))
                continue;

            var isInput = op == "<";
            var isAppend = op == ">>";

            redirections.Add(new Redirection(target, isInput, isAppend));
        }

        cleaned = redirectPattern.Replace(cleaned, "").Trim();

        return (cleaned, redirections);
    }

    /// <summary>
    /// Разбирает простой командный сегмент на исполняемый файл и аргументы.
    /// </summary>
    /// <param name="segment">Сегмент команды без операторов и перенаправлений.</param>
    /// <returns>Имя бинарника и список аргументов.</returns>
    private static (string Binary, IReadOnlyList<string> Args) ParseSimpleCommand(string segment)
    {
        var tokens = Tokenize(segment);
        if (tokens.Count == 0)
            return ("", []);

        var skip = 0;
        while (skip < tokens.Count && IsEnvVarAssignment(tokens[skip]))
            skip++;

        var effective = tokens.Skip(skip).ToList();
        if (effective.Count == 0)
            return ("", []);

        return (effective[0], effective.Skip(1).ToList());
    }

    /// <summary>
    /// Определяет, является ли токен присваиванием переменной окружения.
    /// </summary>
    /// <param name="token">Токен для проверки.</param>
    /// <returns><see langword="true" />, если токен имеет вид присваивания; иначе <see langword="false" />.</returns>
    private static bool IsEnvVarAssignment(string token)
    {
        var eq = token.IndexOf('=');
        if (eq <= 0) return false;
        var name = token[..eq];
        return name.Length > 0 && char.IsLetter(name[0]) &&
               name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>
    /// Токенизирует shell-строку с учетом кавычек и экранирования.
    /// </summary>
    /// <param name="input">Исходная строка сегмента.</param>
    /// <returns>Список токенов.</returns>
    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var i = 0;

        while (i < input.Length)
        {
            var c = input[i];

            if (c == '\\' && i + 1 < input.Length && !inSingleQuote)
            {
                current.Append(input[i + 1]);
                i += 2;
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                i++;
                continue;
            }
            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inSingleQuote && !inDoubleQuote)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }
}

/// <summary>
/// Содержит итог разбора shell-команды.
/// </summary>
/// <param name="Segments">Последовательность командных сегментов.</param>
/// <param name="Redirections">Список перенаправлений ввода-вывода.</param>
/// <param name="Subshells">Извлеченные подкоманды.</param>
public sealed record BashParseResult(
    IReadOnlyList<CommandSegment> Segments,
    IReadOnlyList<Redirection> Redirections,
    IReadOnlyList<string> Subshells);

/// <summary>
/// Описывает один сегмент shell-команды.
/// </summary>
/// <param name="Binary">Имя исполняемого файла.</param>
/// <param name="Args">Аргументы команды.</param>
/// <param name="Operator">Оператор, следующий за сегментом.</param>
public sealed record CommandSegment(
    string Binary,
    IReadOnlyList<string> Args,
    CompoundOp Operator);

/// <summary>
/// Описывает одно перенаправление ввода-вывода.
/// </summary>
/// <param name="Target">Целевой путь перенаправления.</param>
/// <param name="IsInput">Признак перенаправления на чтение.</param>
/// <param name="IsAppend">Признак режима дозаписи.</param>
public sealed record Redirection(string Target, bool IsInput, bool IsAppend);

/// <summary>
/// Перечисляет составные операторы shell между сегментами команды.
/// </summary>
public enum CompoundOp
{
    /// <summary>
    /// Оператор отсутствует.
    /// </summary>
    None,

    /// <summary>
    /// Логическое И <c>&amp;&amp;</c>.
    /// </summary>
    And,

    /// <summary>
    /// Логическое ИЛИ <c>||</c>.
    /// </summary>
    Or,

    /// <summary>
    /// Последовательное выполнение через <c>;</c>.
    /// </summary>
    Sequence,

    /// <summary>
    /// Конвейер через <c>|</c>.
    /// </summary>
    Pipe
}
