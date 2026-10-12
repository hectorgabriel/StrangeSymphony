using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Symphony.Core.Workflow;

/// <summary>Parsed WORKFLOW.md (SPEC 4.1.2).</summary>
public sealed record WorkflowDefinition(IReadOnlyDictionary<string, object?> Config, string PromptTemplate);

public static class WorkflowErrorCodes
{
    public const string MissingWorkflowFile = "missing_workflow_file";
    public const string WorkflowParseError = "workflow_parse_error";
    public const string FrontMatterNotAMap = "workflow_front_matter_not_a_map";
    public const string TemplateParseError = "template_parse_error";
    public const string TemplateRenderError = "template_render_error";
}

/// <summary>Typed workflow/config error. <see cref="Code"/> is one of the SPEC 5.5 / 6.3 classes.</summary>
public sealed class WorkflowException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>Reads WORKFLOW.md and splits YAML front matter from the prompt body (SPEC 5.2).</summary>
public static class WorkflowLoader
{
    public static WorkflowDefinition Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkflowException(WorkflowErrorCodes.MissingWorkflowFile, $"cannot read workflow file {path}: {ex.Message}", ex);
        }

        return Parse(text);
    }

    public static WorkflowDefinition Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != "---")
        {
            return new WorkflowDefinition(new Dictionary<string, object?>(), text.Trim());
        }

        var end = Array.FindIndex(lines, 1, l => l.TrimEnd() == "---");
        if (end < 0)
        {
            throw new WorkflowException(WorkflowErrorCodes.WorkflowParseError, "front matter starts with '---' but has no closing '---'");
        }

        var yaml = string.Join('\n', lines[1..end]);
        var body = string.Join('\n', lines[(end + 1)..]).Trim();
        return new WorkflowDefinition(ParseFrontMatter(yaml), body);
    }

    private static Dictionary<string, object?> ParseFrontMatter(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new Dictionary<string, object?>();
        }

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new WorkflowException(WorkflowErrorCodes.WorkflowParseError, $"invalid YAML front matter: {ex.Message}", ex);
        }

        if (stream.Documents.Count == 0)
        {
            return new Dictionary<string, object?>();
        }

        object? root;
        try
        {
            root = ToPlain(stream.Documents[0].RootNode);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException)
        {
            // Duplicate keys or non-scalar keys.
            throw new WorkflowException(WorkflowErrorCodes.WorkflowParseError, $"invalid YAML front matter: {ex.Message}", ex);
        }

        return root as Dictionary<string, object?>
            ?? throw new WorkflowException(WorkflowErrorCodes.FrontMatterNotAMap, "YAML front matter must decode to a map");
    }

    /// <summary>
    /// Converts YAML to plain maps/lists/scalars. Scalars stay strings unless plain (unquoted) and
    /// clearly numeric/boolean/null, so the config layer can coerce them.
    /// </summary>
    private static object? ToPlain(YamlNode node) => node switch
    {
        YamlMappingNode m => m.Children.ToDictionary(
            kv => ((YamlScalarNode)kv.Key).Value ?? string.Empty,
            kv => ToPlain(kv.Value)),
        YamlSequenceNode s => s.Children.Select(ToPlain).ToList(),
        YamlScalarNode sc => ScalarValue(sc),
        _ => null,
    };

    private static object? ScalarValue(YamlScalarNode sc)
    {
        var v = sc.Value;
        if (sc.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
        {
            return v;
        }

        if (v is null or "" or "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        if (v is "true" or "True" or "TRUE") return true;
        if (v is "false" or "False" or "FALSE") return false;
        if (long.TryParse(v, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return v;
    }
}
