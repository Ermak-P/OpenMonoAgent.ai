using System.Diagnostics;
using System.Text.Json;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed class BashTool : ToolBase
{
    public override string Name => "Bash";
    public override string Description =>
        "Execute a shell command. The working directory persists between calls. " +
        "Use for git, build tools, and other system operations. " +
        "For long-running processes that do not exit on their own (servers, watchers, " +
        "`dotnet run`, `npm start`, etc.) set background=true — that spawns the process " +
        "detached, writes stdout+stderr to a log file under ~/.openmono/bg/, and returns " +
        "the PID immediately so the conversation can continue.";

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddProperty("command", new { type = "string", minLength = 1, description = "The bash command to execute" })
        .AddInteger("timeout_ms", "Timeout in milliseconds (default: 120000, max: 600000). Ignored when background=true.", minimum: 1, maximum: 600000)
        .AddBoolean("background", "If true, launch the process detached, write stdout+stderr to a log file under ~/.openmono/bg/, and return the PID + log path immediately. Use for servers, watchers, or anything that never exits on its own.")
        .Require("command");

    public override PermissionLevel RequiredPermission(JsonElement input)
    {
        var command = input.GetProperty("command").GetString() ?? "";

        return SanityCheck.IsDestructiveCommand(command) ? PermissionLevel.Deny : PermissionLevel.Ask;
    }

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var command = input.TryGetProperty("command", out var cmd) ? cmd.GetString() : null;
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var parseResult = BashParser.Parse(command);
        var caps = new List<Capability>(BashParser.ToCapabilities(parseResult));

        foreach (var seg in parseResult.Segments)
        {
            var vcsCap = DetectVcsMutation(seg);
            if (vcsCap is not null)
                caps.Add(vcsCap);
        }

        return caps;
    }

    private static VcsMutationCap? DetectVcsMutation(CommandSegment seg)
    {
        if (!seg.Binary.Equals("git", StringComparison.OrdinalIgnoreCase))
            return null;

        if (seg.Args.Count == 0)
            return null;

        var subcommand = seg.Args[0].ToLowerInvariant();

        return subcommand switch
        {
            "push" => new VcsMutationCap(".", "push"),
            "commit" => new VcsMutationCap(".", "commit"),
            "merge" => new VcsMutationCap(".", "merge"),
            "rebase" => new VcsMutationCap(".", "rebase"),
            "reset" => new VcsMutationCap(".", "reset"),
            "stash" => new VcsMutationCap(".", "stash"),
            "cherry-pick" => new VcsMutationCap(".", "cherry-pick"),
            "checkout" when seg.Args.Contains("-b") => new VcsMutationCap(".", "branch"),
            "branch" when seg.Args.Any(a => a is "-d" or "-D") => new VcsMutationCap(".", "branch-delete"),
            _ => null
        };
    }

    /// <summary>Returns the shell executable and the argument prefix for the current platform.</summary>
    /// <remarks>
    /// On Windows the agent uses <c>cmd.exe /c</c> so that commands typed by the user in
    /// Windows-style syntax work out of the box.  On Unix the original <c>/bin/bash -c</c>
    /// behaviour is preserved.
    /// </remarks>
    internal static (string Shell, string ShellFlag) GetPlatformShell()
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c")
            : ("/bin/bash", "-c");

    private static void ApplyShellEnvironment(ProcessStartInfo psi)
    {
        if (OperatingSystem.IsWindows())
        {
            // On Windows HOME is not a standard variable; USERPROFILE is canonical.
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            psi.Environment["HOME"] = userProfile;
            psi.Environment["USERPROFILE"] = userProfile;
        }
        else
        {
            psi.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? "/root";
            psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
        }
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var command = input.GetProperty("command").GetString()!;
        var background = input.TryGetProperty("background", out var b)
            && b.ValueKind == JsonValueKind.True;

        if (background)
            return RunBackground(command, context);

        var timeoutMs = input.TryGetProperty("timeout_ms", out var t) ? t.GetInt32() : 120_000;
        if (timeoutMs <= 0) timeoutMs = 120_000;
        timeoutMs = Math.Min(timeoutMs, 600_000);

        var (shell, shellFlag) = GetPlatformShell();
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            ArgumentList = { shellFlag, command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = context.WorkingDirectory,
        };

        ApplyShellEnvironment(psi);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to start process: {ex.Message}");
        }

        if (process is null)
            return ToolResult.Error($"Failed to start process for command: {command}");

        using (process)
        {

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {

                await KillProcessTreeAsync(process);

                if (ct.IsCancellationRequested)
                    return ToolResult.Error($"Command cancelled by user: {command}");

                return ToolResult.Error(
                    $"Command timed out after {timeoutMs}ms and was terminated (entire process tree killed): {command}");
            }
            catch (Exception ex)
            {
                await KillProcessTreeAsync(process);
                return ToolResult.Error($"Failed while awaiting process: {ex.Message}");
            }

            string stdout, stderr;
            try
            {
                stdout = await stdoutTask;
                stderr = await stderrTask;
            }
            catch (OperationCanceledException)
            {
                stdout = string.Empty;
                stderr = string.Empty;
            }

            var output = new List<string>();
            if (!string.IsNullOrWhiteSpace(stdout))
                output.Add(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr))
                output.Add($"[stderr]\n{stderr.TrimEnd()}");

            var content = output.Count > 0 ? string.Join('\n', output) : "(no output)";

            if (process.ExitCode != 0)
                content = $"Exit code: {process.ExitCode}\n{content}";

            const int maxLength = 50_000;
            if (content.Length > maxLength)
                content = content[..maxLength] + $"\n... (truncated, {content.Length} total chars)";

            return process.ExitCode == 0
                ? ToolResult.Success(content)
                : ToolResult.Error(content);
        }
    }

    private static ToolResult RunBackground(string command, ToolContext context)
    {
        var home = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("USERPROFILE")
               ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            : (Environment.GetEnvironmentVariable("HOME") ?? "/root");

        var bgDir = Path.Combine(home, ".openmono", "bg");
        try { Directory.CreateDirectory(bgDir); }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to create background log directory {bgDir}: {ex.Message}");
        }

        var logName = $"bg-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.log";
        var logPath = Path.Combine(bgDir, logName);

        // Create the log file up-front so readers can tail it immediately.
        try { File.WriteAllText(logPath, string.Empty); }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to create log file {logPath}: {ex.Message}");
        }

        var (shell, shellFlag) = GetPlatformShell();

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            RedirectStandardInput = false,
            // Redirect output so we can pipe it to the log file from managed code —
            // this avoids bash-specific shell redirection syntax (exec >>... 2>&1)
            // and works identically on Windows and Unix.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = context.WorkingDirectory,
        };
        psi.ArgumentList.Add(shellFlag);
        psi.ArgumentList.Add(command);

        ApplyShellEnvironment(psi);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to start background process: {ex.Message}");
        }
        if (process is null)
            return ToolResult.Error($"Failed to start background process for command: {command}");

        var pid = process.Id;

        // Fire-and-forget tasks that drain stdout/stderr into the log file.
        // Both use FileMode.Append + FileShare.ReadWrite so they interleave
        // safely and external readers can follow the file while it grows.
        _ = PipeStreamToFileAsync(process.StandardOutput, logPath);
        _ = PipeStreamToFileAsync(process.StandardError, logPath);

        string killHint = OperatingSystem.IsWindows()
            ? $"  taskkill /PID {pid}      # stop the process\n" +
              $"  taskkill /F /PID {pid}   # force-kill if the process won't stop\n"
            : $"  kill {pid}             # stop the process\n" +
              $"  kill -9 {pid}          # force-kill if the process won't stop\n";

        string tailHint = OperatingSystem.IsWindows()
            ? $"  Get-Content -Tail 50 '{logPath}'   # peek at output\n" +
              $"  Get-Content -Wait '{logPath}'       # stream output\n"
            : $"  tail -n 50 {logPath}   # peek at output\n" +
              $"  tail -f {logPath}      # stream output (avoid in agent — use sleep+tail -n instead)\n";

        var summary =
            $"Started in background — PID {pid}\n" +
            $"Log: {logPath}\n" +
            "\n" +
            "Follow-ups:\n" +
            tailHint +
            killHint;
        return ToolResult.Success(summary);
    }

    /// <summary>
    /// Drains <paramref name="reader"/> line-by-line into <paramref name="logPath"/>,
    /// appending to the file so that stdout and stderr interleave naturally.
    /// Runs as a background task — exceptions are swallowed intentionally.
    /// </summary>
    private static async Task PipeStreamToFileAsync(StreamReader reader, string logPath)
    {
        try
        {
            await using var fs = new FileStream(
                logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            await using var writer = new StreamWriter(fs) { AutoFlush = true };

            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
                await writer.WriteLineAsync(line);
        }
        catch
        {
            // Background log task — swallow all errors so they don't surface
            // as unobserved task exceptions.
        }
    }

    private static async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {

        }
        catch (Exception)
        {

        }

        try
        {
            using var graceCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(graceCts.Token);
        }
        catch
        {

        }
    }

}
