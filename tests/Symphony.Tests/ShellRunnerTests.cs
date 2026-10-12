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
}
