using Symphony.Core.Domain;
using Symphony.Core.Templating;
using Symphony.Core.Workflow;

namespace Symphony.Tests;

public class PromptRendererTests
{
    private static readonly Issue Issue = TestIssues.Make("1", "WI-1", labels: ["Backend", "api"]);

    [Fact]
    public void Renders_issue_fields_labels_and_attempt()
    {
        var text = PromptRenderer.Render(
            "{{ issue.identifier }}: {{ issue.title }}\n{% for l in issue.labels %}[{{ l }}]{% endfor %}\n{% if attempt %}retry {{ attempt }}{% else %}first{% endif %}",
            Issue, attempt: null);

        Assert.Equal("WI-1: Title 1\n[backend][api]\nfirst", text);
        Assert.EndsWith("retry 3", PromptRenderer.Render("{% if attempt %}retry {{ attempt }}{% endif %}", Issue, 3));
    }

    [Fact]
    public void Standard_filters_work()
    {
        Assert.Equal("WI-1", PromptRenderer.Render("{{ issue.identifier | upcase }}", Issue, null));
    }

    [Theory]
    [InlineData("{{ nope }}")]
    [InlineData("{{ issue.nope }}")]
    [InlineData("{{ issue.title | no_such_filter }}")]
    public void Unknown_variables_members_and_filters_fail(string template)
    {
        var ex = Assert.Throws<WorkflowException>(() => PromptRenderer.Render(template, Issue, null));
        Assert.Equal(WorkflowErrorCodes.TemplateRenderError, ex.Code);
    }

    [Fact]
    public void Syntax_errors_are_parse_errors()
    {
        var ex = Assert.Throws<WorkflowException>(() => PromptRenderer.Render("{% if %}", Issue, null));
        Assert.Equal(WorkflowErrorCodes.TemplateParseError, ex.Code);
    }

    [Fact]
    public void Empty_body_uses_default_prompt()
    {
        Assert.Equal(PromptRenderer.DefaultPrompt, PromptRenderer.Render("  ", Issue, null));
    }
}
