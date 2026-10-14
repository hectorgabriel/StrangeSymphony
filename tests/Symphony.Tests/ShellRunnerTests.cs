using Symphony.Core.Util;

namespace Symphony.Tests;

public class ShellRunnerTests
{
    private const int Max = ShellRunner.MaxCapturedChars;
    private const string Marker = "…[truncated]";

    private static Task<ShellResult> Run(string script) =>
        ShellRunner.RunAsync(script, Path.GetTempPath(), 10000, default);

    [Fact]
    public async Task Small_output_is_captured_whole_without_marker()
    {
        var result = await Run("echo one; echo two >&2; exit 3");
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("one", result.Output);
        Assert.Contains("two", result.Output);
        Assert.DoesNotContain(Marker, result.Output);
    }

    [Fact]
    public async Task Output_that_exactly_fills_the_cap_then_continues_is_marked()
    {
        // Two lines fill the cap exactly and the rest are dropped. The old check only marked
        // output when the final string was longer than the cap, so this came back unmarked.
        var line = new string('x', Max / 2 - Environment.NewLine.Length);
        var result = await Run($"for i in 1 2 3 4; do echo {line}; done");
        Assert.EndsWith(Marker, result.Output);
        Assert.True(result.Output.Length <= Max + Marker.Length);
    }

    [Fact]
    public async Task Dropped_lines_after_an_exact_fill_are_marked()
    {
        var line = new string('y', Max - Environment.NewLine.Length);
        var result = await Run($"echo {line}; echo dropped");
        Assert.DoesNotContain("dropped", result.Output);
        Assert.EndsWith(Marker, result.Output);
    }

    [Fact]
    public async Task One_huge_line_is_capped()
    {
        var result = await Run("head -c 200000 /dev/zero | tr '\\0' z; echo");
        Assert.Equal(Max + Marker.Length, result.Output.Length);
        Assert.EndsWith(Marker, result.Output);
    }

    [Fact]
    public async Task Output_up_to_the_cap_is_not_marked()
    {
        var line = new string('w', Max - Environment.NewLine.Length);
        var result = await Run($"echo {line}");
        Assert.Equal(line, result.Output);
    }

    [Fact]
    public async Task Auto_runs_bash_syntax_on_every_os()
    {
        // `&&` is a parse error in Windows PowerShell 5.1; the example hooks rely on it.
        var result = await Run("true && echo \"ok-$((40+2))\"");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok-42", result.Output);
    }

    [Fact]
    public async Task A_long_script_runs_whole()
    {
        // Git Bash given more than ~8 KB through -c runs nothing and exits 0.
        var filler = string.Join('\n', Enumerable.Range(0, 2000).Select(i => $": filler line {i}"));
        var result = await Run($"{filler}\necho reached-the-end");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("reached-the-end", result.Output);
    }

    [Fact]
    public async Task Hook_output_is_read_as_utf8()
    {
        var result = await Run("printf 'En Proceso – año\\n'");
        Assert.Equal("En Proceso – año", result.Output);
    }
}

public sealed class ShellResolutionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("symphony-shell-").FullName;

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("BASH", true)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", true)]
    [InlineData("zsh", false)]
    [InlineData("bin/bash", false)]
    public void Settings_are_keywords_or_absolute_paths(string value, bool valid) =>
        Assert.Equal(valid, ShellRunner.IsValidSetting(value));

    [Fact]
    public void Explicit_path_is_used_as_given()
    {
        var bash = Touch("custom", "bash.exe");
        Assert.Equal(new Shell(ShellKind.Bash, bash), ShellRunner.Resolve(bash, _ => null));
    }

    [Fact]
    public void Git_bash_override_wins()
    {
        var configured = Touch("override", "bash.exe");
        var env = new Dictionary<string, string?> { ["CLAUDE_CODE_GIT_BASH_PATH"] = configured };
        Assert.Equal(configured, ShellRunner.FindGitBash(env.GetValueOrDefault));
    }

    [Fact]
    public void Git_bash_is_found_next_to_git_on_path()
    {
        Touch("Git", "cmd", "git.exe");
        var bash = Touch("Git", "bin", "bash.exe");
        var env = new Dictionary<string, string?> { ["PATH"] = Path.Combine(_dir, "Git", "cmd") };
        Assert.Equal(bash, ShellRunner.FindGitBash(env.GetValueOrDefault));
    }

    [Fact]
    public void The_real_bash_wins_over_the_redirector_that_truncates_long_scripts()
    {
        Touch("Git", "cmd", "git.exe");
        Touch("Git", "bin", "bash.exe");
        var real = Touch("Git", "usr", "bin", "bash.exe");
        Assert.Equal(real, ShellRunner.FindGitBash(new Dictionary<string, string?> { ["PATH"] = Path.Combine(_dir, "Git", "cmd") }.GetValueOrDefault));
        Assert.Equal(real, ShellRunner.FindGitBash(new Dictionary<string, string?> { ["CLAUDE_CODE_GIT_BASH_PATH"] = Path.Combine(_dir, "Git", "bin", "bash.exe") }.GetValueOrDefault));
    }

    [Fact]
    public void The_wsl_launcher_under_the_windows_folder_is_never_picked()
    {
        // Same layout as C:\Windows\System32\bash.exe with a git.exe beside it.
        Touch("Windows", "System32", "git.exe");
        Touch("Windows", "bin", "bash.exe");
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = Path.Combine(_dir, "Windows", "System32"),
            ["SystemRoot"] = Path.Combine(_dir, "Windows"),
            ["CLAUDE_CODE_GIT_BASH_PATH"] = Touch("Windows", "System32", "bash.exe"),
        };
        Assert.Null(ShellRunner.FindGitBash(env.GetValueOrDefault));
    }

    [Fact]
    public void Auto_falls_back_to_powershell_on_windows_only_when_git_bash_is_missing()
    {
        var shell = ShellRunner.Resolve("auto", _ => null);
        Assert.Equal(OperatingSystem.IsWindows() ? ShellKind.PowerShell : ShellKind.Bash, shell.Kind);
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<FileNotFoundException>(() => ShellRunner.Resolve("bash", _ => null));
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
