using Spectre.Console;

namespace SiteHarvest.Ui;

public static class Term
{
    public const string Accent = "turquoise2";
    public const string AccentDim = "darkturquoise";
    public const string Muted = "grey66";
    public const string Ok = "chartreuse3";
    public const string Warn = "gold3";
    public const string Danger = "red1";
    public const string Info = "skyblue1";
    public const string Title = "white";

    public static void Rule(string? title = null)
    {
        var rule = string.IsNullOrWhiteSpace(title)
            ? new Rule($"[{Muted}]────────────────────────────────[/]")
            : new Rule($"[{Accent}]{Escape(title)}[/]");
        rule.Style = Style.Parse(Muted);
        rule.Justification = Justify.Left;
        AnsiConsole.Write(rule);
    }

    public static void Header(string dataRoot)
    {
        AnsiConsole.Write(new FigletText("harvest")
            .Color(Color.Turquoise2)
            .LeftJustified());

        AnsiConsole.Write(new Panel(
                new Markup(
                    $"[bold {Title}]site-harvest[/]  [{Muted}]teach & harvest repeating page items[/]\n" +
                    $"[{Muted}]data[/] [{AccentDim}]{Escape(dataRoot)}[/]"))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.DarkCyan)
            .Padding(1, 0));
        AnsiConsole.WriteLine();
    }

    public static void Success(string message) =>
        AnsiConsole.MarkupLine($"[{Ok}]✓[/] {Escape(message)}");

    public static void WarnMsg(string message) =>
        AnsiConsole.MarkupLine($"[{Warn}]![/] {Escape(message)}");

    public static void Error(string message) =>
        AnsiConsole.MarkupLine($"[{Danger}]✗[/] {Escape(message)}");

    public static void Hint(string message) =>
        AnsiConsole.MarkupLine($"[{Muted}]{Escape(message)}[/]");

    public static void InfoMsg(string message) =>
        AnsiConsole.MarkupLine($"[{Info}]→[/] {Escape(message)}");

    public static void Pause()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Markup($"[{Muted}]Press Enter to continue…[/]");
        Console.ReadLine();
        AnsiConsole.WriteLine();
    }

    public static string Escape(string? text) =>
        Markup.Escape(text ?? "");

    public static Table NewTable()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.DarkCyan)
            .Expand();
        return table;
    }

    public static SelectionPrompt<T> SelectPrompt<T>(string title)
        where T : notnull
    {
        return new SelectionPrompt<T>()
            .Title($"[{Accent}]{Escape(title)}[/]")
            .HighlightStyle(new Style(Color.Black, Color.Turquoise2))
            .PageSize(12)
            .EnableSearch()
            .SearchPlaceholderText($"[{Muted}]search…[/]")
            .MoreChoicesText($"[{Muted}]↑↓ move · type to search[/]")
            .UseConverter(x => x?.ToString() ?? "");
    }

    public static string AskText(string label, string? defaultValue = null, bool optional = false)
    {
        var prompt = new TextPrompt<string>($"[{Accent}]{Escape(label)}[/]")
            .PromptStyle(Accent)
            .AllowEmpty();

        if (defaultValue != null)
            prompt.DefaultValue(defaultValue);

        while (true)
        {
            var value = AnsiConsole.Prompt(prompt).Trim();
            if (!string.IsNullOrWhiteSpace(value) || optional)
                return value;

            WarnMsg("This field cannot be empty.");
        }
    }

    public static bool Confirm(string question, bool defaultValue = false) =>
        AnsiConsole.Confirm($"[{Accent}]{Escape(question)}[/]", defaultValue);

    public static string StatusColor(string status) => status.ToLowerInvariant() switch
    {
        "succeeded" or "success" or "ok" => Ok,
        "failed" or "error" => Danger,
        "running" or "pending" => Warn,
        _ => Muted,
    };
}
