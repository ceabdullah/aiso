using Aiso.Infrastructure.Llm;
using Aiso.Infrastructure.Runtime;
using Aiso.Infrastructure.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aiso.Tests;

public class ClaudeCliOrchestratorTests
{
    private static readonly LlmGatewayOptions Llm = new() { BaseUrl = "https://llm-gateway.corp.local/", ApiKey = "sk-test", Model = "claude-3-5-sonnet-20241022" };
    private static readonly WorkspaceOptions Workspace = new() { RootPath = Path.GetTempPath() };

    [Fact]
    public void BuildStartInfo_uses_headless_print_mode_with_least_privilege_tools()
    {
        var psi = ClaudeCliOrchestrator.BuildStartInfo(new ClaudeCliOptions(), Llm, Workspace);
        var args = psi.ArgumentList.ToList();

        Assert.Equal("claude.exe", psi.FileName);
        Assert.Equal(Workspace.RootPath, psi.WorkingDirectory);
        Assert.True(psi.RedirectStandardInput);
        Assert.Contains("-p", args);
        Assert.Equal("json", args[args.IndexOf("--output-format") + 1]);
        Assert.Equal(Llm.Model, args[args.IndexOf("--model") + 1]);
        Assert.Equal("default", args[args.IndexOf("--permission-mode") + 1]);
        Assert.Equal(["Read", "Glob", "Grep"], args.Skip(args.IndexOf("--allowedTools") + 1).Take(3));
        Assert.Contains("Bash", args.Skip(args.IndexOf("--disallowedTools") + 1));
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
    }

    [Fact]
    public void BuildStartInfo_maps_gateway_variables_and_pins_every_model_tier()
    {
        var psi = ClaudeCliOrchestrator.BuildStartInfo(new ClaudeCliOptions { ExtraCaCertificatePath = @"C:\certs\corp-root.pem" }, Llm, Workspace);

        Assert.Equal("https://llm-gateway.corp.local", psi.Environment["ANTHROPIC_BASE_URL"]);
        Assert.Equal("sk-test", psi.Environment["ANTHROPIC_API_KEY"]);
        Assert.Equal(Llm.Model, psi.Environment["ANTHROPIC_SMALL_FAST_MODEL"]);
        Assert.Equal(Llm.Model, psi.Environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
        Assert.Equal("1", psi.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        Assert.Equal(@"C:\certs\corp-root.pem", psi.Environment["NODE_EXTRA_CA_CERTS"]);
    }

    [Fact]
    public void BuildStartInfo_does_not_leak_inherited_anthropic_variables()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "should-not-leak");
        try
        {
            var psi = ClaudeCliOrchestrator.BuildStartInfo(new ClaudeCliOptions(), Llm, Workspace);
            Assert.False(psi.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
        }
    }

    [Fact]
    public void BuildStartInfo_requires_gateway_configuration() =>
        Assert.Throws<InvalidOperationException>(() =>
            ClaudeCliOrchestrator.BuildStartInfo(new ClaudeCliOptions(), new LlmGatewayOptions(), Workspace));

    [Fact]
    public void ParseOutput_reads_success_result()
    {
        const string stdout = """{"type":"result","subtype":"success","is_error":false,"result":"{\"items\":[]}","session_id":"abc","num_turns":3,"total_cost_usd":0}""";
        var r = ClaudeCliOrchestrator.ParseOutput(0, stdout, "", TimeSpan.FromSeconds(1), 1);

        Assert.True(r.Success);
        Assert.Equal("{\"items\":[]}", r.ResultText);
        Assert.Equal("abc", r.SessionId);
        Assert.Equal(3, r.NumTurns);
    }

    [Fact]
    public void ParseOutput_flags_error_results_and_non_json_output()
    {
        var error = ClaudeCliOrchestrator.ParseOutput(1,
            """{"type":"result","subtype":"error_max_turns","is_error":true,"result":"","session_id":"x"}""", "", TimeSpan.Zero, 1);
        Assert.False(error.Success);

        var garbage = ClaudeCliOrchestrator.ParseOutput(1, "Invalid API key", "401", TimeSpan.Zero, 1);
        Assert.False(garbage.Success);
        Assert.Equal("401", garbage.StandardError);
    }

    [Fact]
    public async Task RunAsync_pipes_prompt_via_stdin_and_parses_the_result()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a POSIX shell script as a stand-in for claude.exe.");

        var dir = Directory.CreateTempSubdirectory("aiso-cli-");
        var fake = Path.Combine(dir.FullName, "claude");
        // Echoes the stdin prompt length and the gateway URL back inside a Claude-shaped JSON result.
        await File.WriteAllTextAsync(fake, """
            #!/bin/sh
            PROMPT=$(cat)
            printf '{"type":"result","subtype":"success","is_error":false,"result":"%s|%s|%s","session_id":"fake-1","num_turns":1}\n' "$ANTHROPIC_BASE_URL" "$(printf %s "$PROMPT" | wc -c | tr -d ' ')" "$(pwd)"
            """.Replace("\r\n", "\n", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var cli = new ClaudeCliOptions { ExecutablePath = fake, MaxAttempts = 1, TimeoutMinutes = 1 };
        using var orchestrator = new ClaudeCliOrchestrator(Options.Create(cli), Options.Create(Llm),
            Options.Create(new WorkspaceOptions { RootPath = dir.FullName }), NullLogger<ClaudeCliOrchestrator>.Instance);

        var prompt = "Analyze the scraped results in data/ — türkçe karakterler: ğüşiöç";
        var result = await orchestrator.RunAsync(prompt, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.StandardError);
        Assert.Equal("fake-1", result.SessionId);
        var parts = result.ResultText.Split('|');
        Assert.Equal("https://llm-gateway.corp.local", parts[0]);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(prompt).ToString(System.Globalization.CultureInfo.InvariantCulture), parts[1]);
        Assert.Equal(dir.FullName, parts[2]);
    }

    [Fact]
    public async Task RunAsync_reports_a_clear_error_when_the_cli_is_missing()
    {
        var cli = new ClaudeCliOptions { ExecutablePath = "definitely-not-installed-claude-xyz", MaxAttempts = 1 };
        using var orchestrator = new ClaudeCliOrchestrator(Options.Create(cli), Options.Create(Llm),
            Options.Create(Workspace), NullLogger<ClaudeCliOrchestrator>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.RunAsync("hi", TestContext.Current.CancellationToken));
        Assert.Contains("ExecutablePath", ex.Message, StringComparison.Ordinal);
    }
}
