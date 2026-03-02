// Copyright (c) RazorConsole. All rights reserved.

using Spectre.Console;
using Spectre.Console.Rendering;
using static RazorConsole.Core.Utilities.AnsiSequences;

namespace RazorConsole.Core.Renderables;

internal class DiffRenderable : Renderable
{
    // Cannot use Lock for NET9+ because Render method uses yield return which is incompatible with Lock.Scope
    private readonly object _lock = new();
    private readonly IAnsiConsole _console;
    private IRenderable _renderable;
    private SegmentShape _shape = new(0, 0);
    private List<SegmentLine> _previousLines = new();
    private int _lastMaxWidth = -1;
    /// <summary>
    /// Whether the cursor was repositioned for a cursor hint in the previous
    /// render. When <see langword="true"/>, the next render must restore the
    /// saved cursor position before calculating diffs.
    /// </summary>
    private bool _cursorPositionSaved;

    public bool DidOverflow { get; private set; }

    /// <summary>
    /// Gets or sets an optional hint describing where the terminal cursor should be
    /// positioned after rendering (e.g. at the end of a focused text input value).
    /// When <see langword="null"/>, the cursor is hidden after rendering.
    /// </summary>
    public CursorHint? CursorHint { get; set; }

    /// <summary>
    /// Initializes a new instance of the DiffRenderable class to display the differences between two renderable objects
    /// using the specified console.
    /// </summary>
    public DiffRenderable(IAnsiConsole console, IRenderable renderable)
    {
        _renderable = renderable;
        _console = console;
    }

    public void UpdateRenderable(IRenderable renderable)
    {
        lock (_lock)
        {
            _renderable = renderable;
        }
    }

    protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        // Cannot use Lock.Scope with yield return, must use regular lock
        lock (_lock)
        {
            yield return Segment.Control(RM(DECTCEM));
            DidOverflow = false;

            // If the cursor was repositioned for a cursor hint in the previous
            // render, restore it to the saved bottom-of-output position so that
            // the diff calculations work correctly.
            if (_cursorPositionSaved)
            {
                yield return Segment.Control(DECRC());
                _cursorPositionSaved = false;
            }

            bool widthChanged = _lastMaxWidth != -1 && _lastMaxWidth != maxWidth;
            _lastMaxWidth = maxWidth;

            var segments = _renderable.Render(options, maxWidth);
            var segmentLines = Segment.SplitLines(segments);
            var shape = SegmentShape.Calculate(options, segmentLines);

            // Check for overflow
            if (shape.Height > options.ConsoleSize.Height || shape.Width > options.ConsoleSize.Width)
            {
                DidOverflow = true;
            }

            var previousLines = _previousLines ?? EmptyLines;
            var totalLines = segmentLines.Count;
            var renderFromLine = 0;
            for (renderFromLine = 0; renderFromLine < totalLines; renderFromLine++)
            {
                var line = segmentLines[renderFromLine];
                var previousLine = renderFromLine < previousLines.Count
                    ? previousLines[renderFromLine]
                    : EmptyLine;
                if (!LinesAreEqual(line, previousLine))
                {
                    break;
                }
            }

            // Move cursor to the first different line in the viewport
            int linesToMoveUp = _shape.Height - renderFromLine;

            bool needFullClear = NeedsFullClear(linesToMoveUp) || widthChanged;

            if (needFullClear)
            {
                // The previous content is larger than the current console height, OR resize happened.
                // We need to clear everything to avoid artifacts.
                yield return Segment.Control(ED(2) + ED(3) + CUP(1, 1));
                previousLines = EmptyLines;
                renderFromLine = 0;
            }
            else
            {
                for (var i = 0; i < linesToMoveUp; i++)
                {
                    var previousLineIndex = previousLines.Count - i;
                    if (previousLineIndex >= totalLines)
                    {
                        // The previous line is beyond the current total lines, move up and clear
                        yield return Segment.Control(EL(2) + CUU(1));
                    }
                    else
                    {
                        // just move up
                        yield return Segment.Control(CUU(1));
                    }
                }
            }

            // Render from the first different line
            for (var i = renderFromLine; i < totalLines; i++)
            {
                var line = segmentLines[i];
                var previousLine = i < previousLines.Count
                    ? previousLines[i]
                    : EmptyLine;

                if (!LinesAreEqual(line, previousLine))
                {
                    foreach (var segment in RenderLineDiff(line, previousLine))
                    {
                        yield return segment;
                    }
                }

                yield return Segment.Control(NEL());
            }

            // Cleaning residual lines from below
            if (!needFullClear && previousLines.Count > totalLines)
            {
                var remaining = previousLines.Count - totalLines;
                for (var i = 0; i < remaining; i++)
                {
                    yield return Segment.Control(EL(2)); // Clean line
                    yield return Segment.Control(NEL()); // Go to next line
                }

                yield return Segment.Control(CUU(remaining));
            }

            // Update the previous lines for next comparison
            _previousLines = CloneLines(segmentLines);
            _shape = shape;

            // Position the cursor at the focused text input (if any) and show it,
            // otherwise leave the cursor hidden so it doesn't flash at the bottom.
            var cursorControl = BuildCursorPositionControl(segmentLines, totalLines);
            if (cursorControl is not null)
            {
                // Save the current cursor position (bottom of output) so the next
                // render can restore it with DECRC before doing diff calculations.
                yield return Segment.Control(DECSC());
                _cursorPositionSaved = true;
                yield return Segment.Control(cursorControl);
            }
        }
    }

    /// <summary>
    /// Builds an ANSI control string that moves the cursor to the end of the
    /// focused text input value and makes it visible. Returns <see langword="null"/>
    /// when the cursor should stay hidden (no text input focused or value not found).
    /// </summary>
    private string? BuildCursorPositionControl(List<SegmentLine> segmentLines, int totalLines)
    {
        var hint = CursorHint;
        if (hint is null)
        {
            return null;
        }

        // Find the content line inside the focused TextInput's panel by matching
        // the unique focused border colour. A Panel typically renders as:
        //   line 0: ╭──────╮   (top border — has the border colour)
        //   line 1: │ text │   (content — the left │ has the border colour)
        //   line 2: ╰──────╯   (bottom border)
        // We want the content line (the one between top and bottom borders).
        // When a Label is present the label and value may share a single line
        // (via Columns layout), so we scan segments to find the actual value
        // position rather than assuming it starts at the content area edge.
        var borderColor = hint.FocusedBorderColor;
        int matchedLineIndex = -1;
        int matchedContentStart = -1;

        for (var lineIndex = 0; lineIndex < totalLines; lineIndex++)
        {
            var line = segmentLines[lineIndex];
            var contentStartColumn = FindContentStartColumn(line, borderColor);
            if (contentStartColumn >= 0)
            {
                matchedLineIndex = lineIndex;
                matchedContentStart = contentStartColumn;
            }
        }

        if (matchedLineIndex < 0)
        {
            // Focused panel not found — keep cursor hidden
            return null;
        }

        // Scan the matched content line to find the display content text.
        // This locates the value/placeholder regardless of where the Columns
        // layout placed it relative to the label.
        var cursorColumn = FindDisplayContentColumn(
            segmentLines[matchedLineIndex],
            hint.DisplayContent,
            hint.ValueCellLength,
            matchedContentStart,
            hint.ContentLeftPadding);

        // Move cursor from current position (after the last NEL) to the target
        var linesUp = totalLines - matchedLineIndex;
        // Column is 0-based; CHA (and our helper) expects 1-based
        return BuildCursorMoveSequence(linesUp, cursorColumn + 1);
    }

    /// <summary>
    /// Scans a segment line to find the cell column where the cursor should be
    /// placed. Searches for the <paramref name="displayContent"/> text within
    /// segments and returns the column at the end of the value text. When the
    /// display content cannot be located (e.g. the field is empty and has no
    /// placeholder), falls back to the original calculation from the content
    /// start position.
    /// </summary>
    private static int FindDisplayContentColumn(
        SegmentLine line,
        string displayContent,
        int valueCellLength,
        int contentStart,
        int contentLeftPadding)
    {
        if (string.IsNullOrEmpty(displayContent))
        {
            // No display content to search for — place cursor at content start + padding.
            return contentStart + contentLeftPadding;
        }

        // Build a list of (cellOffset, segment) pairs so we can search backwards.
        // In a Columns layout the label appears before the value, so the last
        // matching segment is the value/placeholder — not the label.
        var entries = new List<(int CellOffset, Segment Segment)>();
        int runningOffset = 0;
        foreach (var segment in line)
        {
            if (segment.IsControlCode)
            {
                continue;
            }

            var segWidth = Segment.CellCount(new List<Segment> { segment });
            entries.Add((runningOffset, segment));
            runningOffset += segWidth;
        }

        // Search backwards to find the rightmost segment matching the display content.
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var (cellOffset, segment) = entries[i];
            var text = segment.Text;
            if (text.Length == 0)
            {
                continue;
            }

            var trimmed = text.TrimEnd();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // Match when:
            //   1. The segment text starts with the full display content, OR
            //   2. The display content starts with the trimmed segment text
            //      (the content was truncated to fit a Columns column width).
            if (text.StartsWith(displayContent, StringComparison.Ordinal)
                || displayContent.StartsWith(trimmed, StringComparison.Ordinal))
            {
                // The cursor sits right after the value text.
                return cellOffset + valueCellLength;
            }
        }


        // Fallback: display content not found in segments — use original calculation.
        return contentStart + contentLeftPadding + valueCellLength;
    }


    /// <summary>
    /// Scans a segment line for a panel content line that starts with a border
    /// character in the specified <paramref name="borderColor"/>. Returns the
    /// 0-based cell column where content starts (after the border character),
    /// or -1 if this line does not match.
    /// </summary>
    /// <remarks>
    /// A "content line" is one where the first styled segment is a single border
    /// character (like │ or ┃) in the target colour, and subsequent segments
    /// contain the actual content. This distinguishes it from top/bottom border
    /// lines (which are entirely border characters like ╭──╮).
    /// </remarks>
    internal static int FindContentStartColumn(SegmentLine line, Spectre.Console.Color borderColor)
    {
        int cellOffset = 0;
        bool foundBorderChar = false;

        foreach (var segment in line)
        {
            if (segment.IsControlCode)
            {
                continue;
            }

            var segWidth = Segment.CellCount(new List<Segment> { segment });

            if (!foundBorderChar)
            {
                // Look for a non-whitespace segment with the border colour.
                // Skip whitespace/padding segments and segments belonging to
                // outer (non-target) panel borders so that nested panels are
                // handled correctly.
                var trimmed = segment.Text.Trim();
                if (trimmed.Length == 0)
                {
                    cellOffset += segWidth;
                    continue;
                }

                if (segment.Style.Foreground == borderColor && trimmed.Length <= 2)
                {
                    // This is a border character segment (│, ║, ┃, etc.)
                    // Check that remaining content follows — i.e., this is not a
                    // top/bottom border line (which would have long runs of ─ chars).
                    foundBorderChar = true;
                    cellOffset += segWidth;
                    continue;
                }

                // Visible segment doesn't match the target border colour.
                // It could be an outer panel's border or other content — keep
                // scanning so we can find a nested panel with the target colour.
                cellOffset += segWidth;
                continue;
            }
            else
            {
                // We found the border char; the next content starts here.
                // But verify it's actually a content line — not a top/bottom border.
                // Top/bottom borders have segments that are all border-coloured
                // horizontal line chars (─, ═, etc.)
                if (segment.Style.Foreground == borderColor)
                {
                    // Still border-coloured — this might be a top/bottom border line
                    // or padding inside the panel that happens to match. Check if
                    // the text is all box-drawing horizontal chars.
                    var text = segment.Text.Trim();
                    if (text.Length > 0 && IsHorizontalBorder(text))
                    {
                        return -1; // Top/bottom border line
                    }
                }

                // This is the content start
                return cellOffset;
            }
        }

        return -1;
    }

    /// <summary>
    /// Checks whether the text consists entirely of horizontal box-drawing characters
    /// (used in top/bottom panel borders).
    /// </summary>
    private static bool IsHorizontalBorder(string text)
    {
        foreach (var ch in text)
        {
            // Common horizontal box-drawing characters: ─ ━ ═ ╌ ╍ ┄ ┅ ┈ ┉
            if (ch != '─' && ch != '━' && ch != '═' && ch != '╌' && ch != '╍' &&
                ch != '┄' && ch != '┅' && ch != '┈' && ch != '┉' && ch != '╶' &&
                ch != '╴' && ch != '╸' && ch != '╺')
            {
                return false;
            }
        }

        return text.Length > 0;
    }

    /// <summary>
    /// Produces an ANSI sequence that moves the cursor up <paramref name="linesUp"/>
    /// lines from the current position, then to column <paramref name="column"/> (1-based),
    /// and makes the cursor visible.
    /// </summary>
    private static string BuildCursorMoveSequence(int linesUp, int column)
    {
        var sb = new System.Text.StringBuilder();
        if (linesUp > 0)
        {
            sb.Append(CUU(linesUp));
        }

        // Move to absolute column using CHA (Cursor Horizontal Absolute)
        sb.Append($"{CSI}{column}G");
        sb.Append(SM(DECTCEM));
        return sb.ToString();
    }

    private bool NeedsFullClear(int linesToMoveUp)
    {
        // Console.CursorTop is not supported in WebAssembly, always full clear
        if (OperatingSystem.IsBrowser())
        {
            return true;
        }

        return linesToMoveUp > Console.CursorTop;
    }

    private static bool LinesAreEqual(SegmentLine line1, SegmentLine line2)
    {
        if (line1.Count != line2.Count)
        {
            return false;
        }

        for (var i = 0; i < line1.Count; i++)
        {
            var segment1 = line1[i];
            var segment2 = line2[i];

            if (!SegmentsAreEqual(segment1, segment2))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SegmentsAreEqual(Segment segment1, Segment segment2)
    {
        return string.Equals(segment1.Text, segment2.Text, StringComparison.Ordinal)
               && Equals(segment1.Style, segment2.Style);
    }

    internal static IEnumerable<Segment> RenderLineDiff(SegmentLine line, SegmentLine previousLine)
    {
        if (line is null)
        {
            throw new ArgumentNullException(nameof(line));
        }

        if (previousLine is null)
        {
            throw new ArgumentNullException(nameof(previousLine));
        }

        var minSegmentCount = Math.Min(line.Count, previousLine.Count);
        var firstDifferentSegmentIndex = 0;
        for (; firstDifferentSegmentIndex < minSegmentCount; firstDifferentSegmentIndex++)
        {
            if (!SegmentsAreEqual(line[firstDifferentSegmentIndex], previousLine[firstDifferentSegmentIndex]))
            {
                break;
            }
        }

        var prefixWidth = firstDifferentSegmentIndex > 0
            ? Segment.CellCount(line.GetRange(0, firstDifferentSegmentIndex))
            : 0;

        if (prefixWidth > 0)
        {
            yield return Segment.Control(CUF(prefixWidth));
        }

        for (var segmentIndex = firstDifferentSegmentIndex; segmentIndex < line.Count; segmentIndex++)
        {
            yield return line[segmentIndex];
        }

        var currentLineWidth = Segment.CellCount(line);
        var previousLineWidth = Segment.CellCount(previousLine);
        if (currentLineWidth < previousLineWidth)
        {
            yield return Segment.Control(EL(0));
        }
    }

    private static List<SegmentLine> CloneLines(List<SegmentLine> source)
    {
        var result = new List<SegmentLine>(source.Count);
        foreach (var line in source)
        {
            result.Add(new SegmentLine(line));
        }

        return result;
    }

    private static readonly List<SegmentLine> EmptyLines = new(0);
    private static readonly SegmentLine EmptyLine = new();
}
