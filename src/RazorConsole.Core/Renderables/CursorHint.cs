// Copyright (c) RazorConsole. All rights reserved.

using Spectre.Console;

namespace RazorConsole.Core.Renderables;

/// <summary>
/// Provides information about where the terminal cursor should be positioned
/// after rendering, typically at the end of a focused text input's value.
/// </summary>
/// <param name="FocusedBorderColor">
/// The border colour of the focused text input's panel. Used to locate the
/// correct panel in the rendered segment grid since only the focused input
/// has this colour.
/// </param>
/// <param name="ValueCellLength">
/// The cell length of the displayed value (after masking, if applicable).
/// The cursor is placed this many cells after the content area starts.
/// </param>
/// <param name="ContentLeftPadding">
/// Number of padding cells between the border character and the content.
/// </param>
/// <param name="DisplayContent">
/// The text currently shown in the input area (value, masked value, or
/// placeholder). Used to locate the exact cell column of the value within
/// the rendered segment line.
/// </param>
internal sealed record CursorHint(
    Color FocusedBorderColor,
    int ValueCellLength,
    int ContentLeftPadding,
    string DisplayContent);
