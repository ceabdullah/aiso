using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Aiso.Infrastructure.Llm;
using Aiso.Infrastructure.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aiso.Infrastructure.Runtime;

public interface IClaudeCliOrchestrator
{
    Task<ClaudeCliResult> RunAsync(string prompt, CancellationToken cancellationToken);
}

/// <summary>
/// Launches the on-premises Claude Code CLI in headless ("print") mode inside the workspace and
/// routes it through the corporate LiteLLM gateway.
///
/// Equivalent command line:
/// <code>
///   cd "C:\PROJECTS\LOCAL LLM DEMO"
///   set ANTHROPIC_BASE_URL=https://&lt;gateway&gt;  &amp;  set ANTHROPIC_API_KEY=***
///   echo "&lt;prompt&gt;" | claude.exe -p --output-format json --model &lt;alias&gt;
///       --permission-mode default --allowedTools Read Glob Grep --disallowedTools Bash WebFetch WebSearch
///       --max-turns 25
/// </code>
/// The prompt is written to stdin rather than passed as an argument, which sidesteps Windows
/// command-line quoting and the 32K argument length limit.
/// </summary>
public sealed class ClaudeCliOrchestrator(
    IOptions<ClaudeCliOptions> cliOptions,
    IOptions<LlmGatewayOptions> llmOptions,
    IOptions<WorkspaceOptions> workspaceOptions,
    ILogger<ClaudeCliOrchestrator> logger) : IClaudeCliOrchestrator, IDisposable
{
    // One agent session at a time: the local model gateway is a shared, capacity-limited resource.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ClaudeCliResult> RunAsync(string prompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var options = cliOptions.Value;

        if (!options.Enabled)
        {
            throw new InvalidOperationException("Claude CLI runtime is disabled (Aiso:ClaudeCli:Enabled=false).");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ClaudeCliResult? last = null;
            for (var attempt = 1; attempt <= Math.Max(1, options.MaxAttempts); attempt++)
            {
                last = await RunOnceAsync(prompt, attempt, cancellationToken);
                if (last.Success)
                {
                    return last;
                }

                logger.LogWarning("claude.exe attempt {Attempt}/{Max} failed (exit {ExitCode}): {Error}",
                    attempt, options.MaxAttempts, last.ExitCode, Truncate(last.StandardError, 500));

                if (attempt < options.MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5), cancellationToken);
                }
            }

            return last!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ClaudeCliResult> RunOnceAsync(string prompt, int attempt, CancellationToken cancellationToken)
    {
        var options = cliOptions.Value;
        var startInfo = BuildStartInfo(options, llmOptions.Value, workspaceOptions.Value);
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) { stdout.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) { stderr.AppendLine(e.Data); } } };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("claude.exe did not start.");
            }
        }
        catch (Win32Exception ex)
        {
            // Not installed / not on PATH: retrying will not help, surface a clear message.
            throw new InvalidOperationException(
                $"Unable to launch '{startInfo.FileName}'. Install Claude Code on this host or set Aiso:ClaudeCli:ExecutablePath. ({ex.Message})", ex);
        }

        logger.LogInformation("Started claude.exe (pid {Pid}, attempt {Attempt}) in {Directory}.",
            process.Id, attempt, startInfo.WorkingDirectory);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(options.TimeoutMinutes));

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            TryKill(process);
            if (!timedOut)
            {
                throw;
            }
        }

        // Ensures async stdout/stderr handlers have drained.
        if (!timedOut)
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }

        stopwatch.Stop();
        string output, errors;
        lock (stdout) { output = stdout.ToString(); }
        lock (stderr) { errors = stderr.ToString(); }

        if (timedOut)
        {
            return new ClaudeCliResult(false, -1, string.Empty, null, null, null,
                $"Timed out after {options.TimeoutMinutes} minute(s). {errors}", stopwatch.Elapsed, attempt);
        }

        return ParseOutput(process.ExitCode, output, errors, stopwatch.Elapsed, attempt);
    }

    /// <summary>Builds the process definition. Public for unit testing of argument/env wiring.</summary>
    public static ProcessStartInfo BuildStartInfo(ClaudeCliOptions cli, LlmGatewayOptions llm, WorkspaceOptions workspace)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(workspace);

        if (!llm.IsConfigured)
        {
            throw new InvalidOperationException("LLM gateway is not configured: set Aiso:Llm:BaseUrl and Aiso:Llm:ApiKey.");
        }

        var workingDirectory = string.IsNullOrWhiteSpace(cli.WorkingDirectory) ? workspace.RootPath : cli.WorkingDirectory;

        var psi = new ProcessStartInfo(cli.ExecutablePath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(llm.Model);
        psi.ArgumentList.Add("--permission-mode");
        psi.ArgumentList.Add(cli.PermissionMode);
        psi.ArgumentList.Add("--max-turns");
        psi.ArgumentList.Add(cli.MaxTurns.ToString(CultureInfo.InvariantCulture));
        if (cli.AllowedTools.Count > 0)
        {
            psi.ArgumentList.Add("--allowedTools");
            foreach (var tool in cli.AllowedTools)
            {
                psi.ArgumentList.Add(tool);
            }
        }

        if (cli.DisallowedTools.Count > 0)
        {
            psi.ArgumentList.Add("--disallowedTools");
            foreach (var tool in cli.DisallowedTools)
            {
                psi.ArgumentList.Add(tool);
            }
        }

        var env = psi.Environment;

        // Never inherit credentials or endpoints from the host account's environment.
        foreach (var key in env.Keys.Where(k => k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
                                                || k.StartsWith("CLAUDE_CODE_USE_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            env.Remove(key);
        }

        env["ANTHROPIC_BASE_URL"] = llm.BaseUrl.TrimEnd('/');
        env["ANTHROPIC_API_KEY"] = llm.ApiKey;
        env["ANTHROPIC_MODEL"] = llm.Model;

        // Claude Code also issues "small/fast" background calls with a separate model name; pin every
        // tier to the gateway alias so no request asks LiteLLM for a model it does not route.
        env["ANTHROPIC_SMALL_FAST_MODEL"] = llm.Model;
        env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = llm.Model;
        env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = llm.Model;
        env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = llm.Model;

        if (cli.DisableNonEssentialTraffic)
        {
            env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
            env["DISABLE_AUTOUPDATER"] = "1";
        }

        if (!string.IsNullOrWhiteSpace(cli.ExtraCaCertificatePath))
        {
            env["NODE_EXTRA_CA_CERTS"] = cli.ExtraCaCertificatePath;
        }

        if (!string.IsNullOrWhiteSpace(cli.HttpsProxy))
        {
            env["HTTPS_PROXY"] = cli.HttpsProxy;
        }

        return psi;
    }

    internal static ClaudeCliResult ParseOutput(int exitCode, string stdout, string stderr, TimeSpan duration, int attempt)
    {
        // --output-format json prints a single JSON object; tolerate leading noise by taking the last line that parses.
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                var json = JsonSerializer.Deserialize<ClaudeJsonOutput>(line);
                if (json?.Type == "result")
                {
                    var ok = exitCode == 0 && !json.IsError && json.Subtype == "success";
                    return new ClaudeCliResult(ok, exitCode, json.Result ?? string.Empty, json.SessionId,
                        json.NumTurns, json.TotalCostUsd, stderr, duration, attempt);
                }
            }
            catch (JsonException)
            {
                // keep scanning
            }
        }

        return new ClaudeCliResult(false, exitCode, stdout.Trim(), null, null, null,
            string.IsNullOrWhiteSpace(stderr) ? "claude.exe produced no JSON result." : stderr, duration, attempt);
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "Process already exited while killing.");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    public void Dispose() => _gate.Dispose();
}
