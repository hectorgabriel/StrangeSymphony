using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;
using Symphony.Core.Domain;
using Symphony.Core.Workflow;

namespace Symphony.Core.Templating;

/// <summary>
/// Strict Liquid-compatible prompt rendering (SPEC 5.4, 12). Unknown variables, unknown members and
/// unknown filters fail rendering.
/// </summary>
public static class PromptRenderer
{
    public const string DefaultPrompt = "You are working on an issue from the configured tracker.";

    /// <summary>Parses once so template syntax errors surface as <c>template_parse_error</c>.</summary>
    public static Template Parse(string templateText)
    {
        var template = Template.ParseLiquid(string.IsNullOrWhiteSpace(templateText) ? DefaultPrompt : templateText);
        if (template.HasErrors)
        {
            throw new WorkflowException(WorkflowErrorCodes.TemplateParseError,
                string.Join("; ", template.Messages.Select(m => m.ToString())));
        }

        return template;
    }

    public static string Render(string templateText, Issue issue, int? attempt)
    {
        var template = Parse(templateText);

        var globals = new ScriptObject();
        globals.SetValue("issue", ToScript(issue.ToTemplateModel()), true);
        globals.SetValue("attempt", attempt, true);

        var context = new LiquidTemplateContext
        {
            StrictVariables = true,
            EnableRelaxedMemberAccess = false,
            EnableNullIndexer = false,
            LoopLimit = 10000,
            RecursiveLimit = 64,
        };
        context.PushGlobal(globals);

        try
        {
            return template.Render(context).Trim();
        }
        catch (ScriptRuntimeException ex)
        {
            throw new WorkflowException(WorkflowErrorCodes.TemplateRenderError, ex.OriginalMessage, ex);
        }
    }

    private static object? ToScript(object? value) => value switch
    {
        IDictionary<string, object?> map => ToScriptObject(map),
        IEnumerable<object?> list when value is not string => new ScriptArray(list.Select(ToScript)),
        System.Collections.IEnumerable list when value is not string => new ScriptArray(list.Cast<object?>().Select(ToScript)),
        _ => value,
    };

    private static ScriptObject ToScriptObject(IDictionary<string, object?> map)
    {
        var obj = new ScriptObject();
        foreach (var (k, v) in map)
        {
            obj.SetValue(k, ToScript(v), true);
        }

        return obj;
    }
}
