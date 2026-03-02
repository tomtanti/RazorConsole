// Copyright (c) RazorConsole. All rights reserved.

using RazorConsole.Core.Renderables;
using Spectre.Console;
using Spectre.Console.Rendering;
using static RazorConsole.Core.Utilities.AnsiSequences;

namespace RazorConsole.Tests.Renderables;

public sealed class DiffRenderableTests
{
    [Fact]
    public void RenderLineDiff_SkipsUnchangedSegmentsAndWritesDiff()
    {
        var previousLine = new SegmentLine
        {
            new("prefix "),
            new("value"),
        };

        var nextLine = new SegmentLine
        {
            new("prefix "),
            new("changed"),
        };

        var result = DiffRenderable.RenderLineDiff(nextLine, previousLine).ToList();

        var controlSegments = result
            .Select(segment => segment.Text)
            .Where(text => text.Contains(ESC, StringComparison.Ordinal))
            .ToList();

        var prefixWidth = Segment.CellCount(new List<Segment>
        {
            new("prefix "),
        });

        controlSegments.ShouldContain(text => text.Contains(CUF(prefixWidth), StringComparison.Ordinal));
        controlSegments.ShouldNotContain(text => text.Contains(EL(2), StringComparison.Ordinal));
        result.ShouldContain(segment => segment.Text == "changed");
    }

    [Fact]
    public void RenderLineDiff_ClearsTailWhenLineShrinks()
    {
        var previousLine = new SegmentLine
        {
            new("prefix "),
            new("value"),
        };

        var nextLine = new SegmentLine
        {
            new("prefix "),
        };

        var result = DiffRenderable.RenderLineDiff(nextLine, previousLine).ToList();

        var controlSegments = result
            .Select(segment => segment.Text)
            .Where(text => text.Contains(ESC, StringComparison.Ordinal))
            .ToList();

        controlSegments.ShouldContain(text => text.Contains(EL(0), StringComparison.Ordinal));
    }

    [Fact]
    public void FindContentStartColumn_ReturnsColumnAfterBorderChar()
    {
        // Simulates a Panel content line: "│ Hello │"
        // The │ has the focused border colour (Yellow), content starts after it.
        var borderStyle = new Style(Color.Yellow);
        var line = new SegmentLine
        {
            new("│", borderStyle),
            new(" Hello "),
            new("│", borderStyle),
        };

        var column = DiffRenderable.FindContentStartColumn(line, Color.Yellow);

        // "│" = 1 cell → content starts at column 1
        column.ShouldBe(1);
    }

    [Fact]
    public void FindContentStartColumn_ReturnsNegativeOneForTopBorder()
    {
        // Simulates a Panel top border line: "╭──────╮"
        var borderStyle = new Style(Color.Yellow);
        var line = new SegmentLine
        {
            new("╭", borderStyle),
            new("──────", borderStyle),
            new("╮", borderStyle),
        };

        var column = DiffRenderable.FindContentStartColumn(line, Color.Yellow);

        // Top border — should not match as content line
        column.ShouldBe(-1);
    }

    [Fact]
    public void FindContentStartColumn_ReturnsNegativeOneWhenBorderColorMismatch()
    {
        // Border is grey, not yellow
        var greyStyle = new Style(Color.Grey);
        var line = new SegmentLine
        {
            new("│", greyStyle),
            new(" Hello "),
            new("│", greyStyle),
        };

        var column = DiffRenderable.FindContentStartColumn(line, Color.Yellow);

        column.ShouldBe(-1);
    }

    [Fact]
    public void FindContentStartColumn_SkipsLeadingWhitespace()
    {
        // Simulates indented panel content: "  │ Hello │"
        var borderStyle = new Style(Color.Yellow);
        var line = new SegmentLine
        {
            new("  "),
            new("│", borderStyle),
            new(" Hello "),
            new("│", borderStyle),
        };

        var column = DiffRenderable.FindContentStartColumn(line, Color.Yellow);

        // "  " = 2 cells, "│" = 1 cell → content starts at column 3
        column.ShouldBe(3);
    }

    [Fact]
    public void FindContentStartColumn_SkipsControlCodes()
    {
        var borderStyle = new Style(Color.Yellow);
        var line = new SegmentLine
        {
            Segment.Control("\u001b[33m"),
            new("│", borderStyle),
            new(" Hello "),
        };

        var column = DiffRenderable.FindContentStartColumn(line, Color.Yellow);

        // "│" = 1 cell → content starts at column 1
        column.ShouldBe(1);
    }

    [Fact]
    public void FindContentStartColumn_SkipsOuterPanelBorderToFindNestedPanel()
    {
        // Simulates a TextInput nested inside an outer Panel (e.g. LoginForm).
        // The rendered line looks like: "│ │ Hello │ │"
        // where the outer │ is grey and the inner │ is the focused colour (Yellow).
        var outerBorderStyle = new Style(Color.Grey);
        var innerBorderStyle = new Style(Color.Yellow);
        var line = new SegmentLine
        {
            new("│", outerBorderStyle),
            new(" "),
            new("│", innerBorderStyle),
            new(" Hello "),
            new("│", innerBorderStyle),
            new(" "),
            new("│", outerBorderStyle),
        };

        var column = DiffRenderable.FindContentStartColumn(line, Color.Yellow);

        // outer "│" = 1, " " = 1, inner "│" = 1 → content starts at column 3
        column.ShouldBe(3);
    }

    [Fact]
    public void Render_WithCursorHint_EmitsCursorPositionAndShow()
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(TextWriter.Null),
        });

        // Wrap content in a Panel with Yellow border so the cursor hint can find it
        var panel = new Panel(new Markup("Hello"))
        {
            BorderStyle = new Style(Color.Yellow),
        };
        var diff = new DiffRenderable(console, panel);
        diff.CursorHint = new CursorHint(Color.Yellow, ValueCellLength: 5, ContentLeftPadding: 1, DisplayContent: "Hello");

        var options = new RenderOptions(console.Profile.Capabilities, new Size(40, 25));
        var segments = ((IRenderable)diff).Render(options, 40).ToList();

        var controlTexts = segments
            .Where(s => s.IsControlCode)
            .Select(s => s.Text)
            .ToList();

        // Should contain SM (show cursor) since a text input panel was found
        controlTexts.ShouldContain(text =>
            text.Contains(SM(DECTCEM), StringComparison.Ordinal),
            "Cursor should be shown when a text input is focused");
    }

    [Fact]
    public void Render_WithoutCursorHint_DoesNotShowCursor()
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(TextWriter.Null),
        });

        var inner = new Markup("Hello");
        var diff = new DiffRenderable(console, inner);
        // No CursorHint set — cursor should stay hidden

        var options = new RenderOptions(console.Profile.Capabilities, new Size(40, 25));
        var segments = ((IRenderable)diff).Render(options, 40).ToList();

        var controlTexts = segments
            .Where(s => s.IsControlCode)
            .Select(s => s.Text)
            .ToList();

        // Should contain RM (hide cursor) at start but NOT SM (show cursor)
        controlTexts.ShouldContain(text =>
            text.Contains(RM(DECTCEM), StringComparison.Ordinal),
            "Cursor should be hidden at start of render");

        controlTexts.ShouldNotContain(text =>
            text.Contains(SM(DECTCEM), StringComparison.Ordinal),
            "Cursor should remain hidden when no text input is focused");
    }
}
