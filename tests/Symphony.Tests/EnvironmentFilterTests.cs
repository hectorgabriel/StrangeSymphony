using Symphony.Core.Config;
using Symphony.Core.Util;
using Symphony.Core.Workflow;

namespace Symphony.Tests;

public class EnvironmentFilterTests
{
    [Theory]
    [InlineData("DATABASE_URL", "DATABASE_URL", true)]
    [InlineData("database_url", "DATABASE_URL", true)]
    [InlineData("DATABASE_URL2", "DATABASE_URL", false)]
    [InlineData("ConnectionStrings__Ventas", "ConnectionStrings__*", true)]
    [InlineData("ConnectionStrings", "ConnectionStrings__*", false)]
    [InlineData("APP_DB_PASSWORD", "*_PASSWORD", true)]
    [InlineData("PASSWORD", "*_PASSWORD", false)]
    [InlineData("MY_SQL_CONN_X", "*SQL*CONN*", true)]
    [InlineData("CONN_SQL", "*SQL*CONN*", false)]
    [InlineData("AA", "A*A", true)]
    [InlineData("A", "A*A", false)]
    public void Matches_names_and_star_patterns_ignoring_case(string name, string pattern, bool expected) =>
        Assert.Equal(expected, EnvironmentFilter.Matches(name, pattern));

    [Fact]
    public void Removes_the_matching_variables_and_keeps_the_rest()
    {
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = "/bin", ["ConnectionStrings__Ventas"] = "Server=db", ["PGPASSWORD"] = "x", ["HOME"] = "/home/a",
        };

        var removed = EnvironmentFilter.Remove(env, ["ConnectionStrings__*", "PGPASSWORD", "NOT_SET"]);

        Assert.Equal(["ConnectionStrings__Ventas", "PGPASSWORD"], removed.Order());
        Assert.Equal(["HOME", "PATH"], env.Keys.Order());
    }

    [Fact]
    public void Lists_database_looking_variables_that_are_not_stripped()
    {
        string[] names = ["PATH", "ConnectionStrings__Ventas", "DATABASE_URL", "PGPASSWORD", "MONGODB_URI", "WRIKE_TOKEN"];

        Assert.Equal(["DATABASE_URL", "MONGODB_URI", "PGPASSWORD"],
            EnvironmentFilter.UnstrippedDatabaseCredentials(names, ["ConnectionStrings__*"]));
        Assert.Empty(EnvironmentFilter.UnstrippedDatabaseCredentials(["PATH", "HOME", "WRIKE_TOKEN", "ANTHROPIC_API_KEY"], []));
    }
}

public class StripEnvConfigTests
{
    private static ServiceConfig Build(string workspace) =>
        new ConfigBuilder([new FakeTrackerFactory(new FakeTracker())], _ => null)
            .Build(WorkflowLoader.Parse($"---\ntracker: {{kind: fake}}\nworkspace:\n{workspace}\n---\nbody"), "WORKFLOW.md");

    [Fact]
    public void Absent_means_nothing_is_stripped()
    {
        Assert.Empty(Build("  root: ws").StrippedEnvironment);
    }

    [Fact]
    public void Takes_a_list_or_one_name_and_drops_duplicates()
    {
        Assert.Equal(["ConnectionStrings__*", "PGPASSWORD"],
            Build("  strip_env: [ConnectionStrings__*, PGPASSWORD, pgpassword]").StrippedEnvironment);
        Assert.Equal(["DATABASE_URL"], Build("  strip_env: DATABASE_URL").StrippedEnvironment);
    }

    [Theory]
    [InlineData("[\"*\"]")]
    [InlineData("[\"P*\"]")]
    [InlineData("[\"*OM*\"]")]
    [InlineData("[\"\"]")]
    [InlineData("[\" DB_URL\"]")]
    [InlineData("[\"DB_URL=x\"]")]
    public void Rejects_patterns_that_are_empty_malformed_or_would_strip_path_or_home(string value)
    {
        var ex = Assert.Throws<WorkflowException>(() => Build($"  strip_env: {value}"));
        Assert.Contains("workspace.strip_env", ex.Message, StringComparison.Ordinal);
    }
}
