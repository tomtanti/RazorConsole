// Copyright (c) RazorConsole. All rights reserved.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RazorConsole.Core.Controllers;
using RazorConsole.Core.Focus;
using RazorConsole.Core.Input;
using RazorConsole.Core.Renderables;
using RazorConsole.Core.Rendering;
using RazorConsole.Core.Utilities;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RazorConsole.Core;

/// <summary>
/// Extension methods for wiring Razor Console into generic host builders.
/// </summary>
public static class HostBuilderExtension
{
    /// <summary>
    /// Adds Razor Console services to the specified <see cref="IHostBuilder"/> using the provided root component.
    /// </summary>
    /// <typeparam name="TComponent">The Razor component that acts as the application's root component.</typeparam>
    /// <param name="hostBuilder">The host builder to configure.</param>
    /// <param name="configure">An optional callback to perform additional configuration.</param>
    /// <returns>The configured <see cref="IHostBuilder"/> instance.</returns>
    public static IHostBuilder UseRazorConsole
        <[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>
    (
        this IHostBuilder hostBuilder,
        Action<IHostBuilder>? configure = null
    )
        where TComponent : IComponent
    {
        RuntimeEncoding.EnsureUtf8();
        hostBuilder.ConfigureServices(RegisterDefaults<TComponent>);
        configure?.Invoke(hostBuilder);

        return hostBuilder;
    }

    /// <summary>
    /// Adds Razor Console services to the specified <see cref="IHostApplicationBuilder"/> using the provided root component.
    /// </summary>
    /// <typeparam name="TComponent">The Razor component that acts as the application's root component.</typeparam>
    /// <param name="hostBuilder">The host application builder to configure.</param>
    /// <param name="configure">An optional callback to perform additional configuration.</param>
    /// <returns>The configured <see cref="IHostApplicationBuilder"/> instance.</returns>
    public static IHostApplicationBuilder UseRazorConsole
    <[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>
    (
        this IHostApplicationBuilder hostBuilder,
        Action<IHostApplicationBuilder>? configure = null
    )
        where TComponent : IComponent
    {
        RuntimeEncoding.EnsureUtf8();
        RegisterDefaults<TComponent>(hostBuilder.Services);

        configure?.Invoke(hostBuilder);

        return hostBuilder;
    }

    private static void RegisterDefaults<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    TComponent>(IServiceCollection services) where TComponent : IComponent
    {
        services.AddRazorConsoleServices();
        services.AddHostedService<ComponentService<TComponent>>();

        // clear all log providers because it would interfere with console rendering
        services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
        });
    }
}

internal class ComponentService<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>(
    ConsoleAppOptions options,
    ConsoleRenderer consoleRenderer,
    FocusManager focusManager,
    KeyboardEventManager keyboardEventManager,
    TerminalMonitor terminalMonitor) : BackgroundService where TComponent : IComponent
{
    private readonly SemaphoreSlim _renderLock = new(1, 1);

    /// <summary>
    /// Ensures exceptions that occur during component execution are surfaced when the host stops.
    /// </summary>
    /// <param name="cancellationToken">A token that requests the stop operation to cancel.</param>
    /// <returns>A task that completes when background processing has stopped.</returns>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Bubble exceptions up into Host.StopAsync, invoked when Host self-stops when a BackgroundService throws
        if (ExecuteTask?.Exception is not null)
        {
            var flattened = ExecuteTask.Exception.Flatten();
            if (flattened.InnerException is not null)
            {
                throw flattened.InnerException;
            }
            else
            {
                throw flattened;
            }
        }

        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var initialView = await RenderComponentInternalAsync(token).ConfigureAwait(false);

        var callback = options.AfterRenderAsync ?? ConsoleAppOptions.DefaultAfterRenderAsync;

        if (options.AutoClearConsole)
        {
            AnsiConsole.Clear();
        }

        // FocusManager must subscribe to the renderer BEFORE ConsoleLiveDisplayContext
        // so that focus targets are updated with the latest VNode attributes (including
        // the text input value) before ApplyCursorHint reads them for cursor positioning.
        using var focusSubscription = consoleRenderer.Subscribe(focusManager);
        using var liveContext = new ConsoleLiveDisplayContext(
            new LiveDisplayCanvas(
                AnsiConsole.Console,
                () => ResolveCursorHint(focusManager)
            ),
            consoleRenderer,
            terminalMonitor
        );
        using var focusSession = focusManager.BeginSession(liveContext, initialView, token);
        await focusSession.InitializationTask.ConfigureAwait(false);
        var keyListenerTask = keyboardEventManager.RunAsync(token);
        if (options.EnableTerminalResizing)
        {
            terminalMonitor.Start(token);
        }

        await callback(liveContext, initialView, token).ConfigureAwait(false);

        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
    }

    private async Task<ConsoleViewResult> RenderComponentInternalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _renderLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var parameterView = CreateParameterView();
            var snapshot = await consoleRenderer.MountComponentAsync<TComponent>(parameterView, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return ConsoleViewResult.FromSnapshot(snapshot);
        }
        finally
        {
            _renderLock.Release();
        }
    }

    private static ParameterView CreateParameterView() => ParameterView.Empty;

    /// <summary>
    /// Reads the current focus state and builds a <see cref="CursorHint"/> that tells
    /// <see cref="DiffRenderable"/> where to position the terminal cursor.
    /// Returns <see langword="null"/> when no text input is focused.
    /// </summary>
    private static CursorHint? ResolveCursorHint(FocusManager fm)
    {
        if (!fm.TryGetFocusedTarget(out var target) || target is null)
        {
            return null;
        }

        if (!target.Attributes.TryGetValue("data-text-input", out var isTextInput)
            || !string.Equals(isTextInput, "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Parse the focused border colour from the data attribute (R,G,B format)
        var borderColor = Color.Yellow; // fallback default
        if (target.Attributes.TryGetValue("data-focused-border-color", out var colorStr)
            && colorStr is not null)
        {
            var parts = colorStr.Split(',');
            if (parts.Length == 3
                && byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
                && byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g)
                && byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
            {
                borderColor = new Color(r, g, b);
            }
        }

        // Determine the displayed value length in cells
        var value = string.Empty;
        if (target.Attributes.TryGetValue("value", out var rawValue) && rawValue is not null)
        {
            value = rawValue;
        }

        // If the input is masked, the display shows bullet characters instead
        if (target.Attributes.TryGetValue("data-mask-input", out var maskAttr)
            && string.Equals(maskAttr, "true", StringComparison.OrdinalIgnoreCase)
            && value.Length > 0)
        {
            value = new string('•', value.Length);
        }

        // Build the display content string (mirrors TextInput.DisplayContent logic)
        var displayContent = value;
        if (value.Length == 0)
        {
            target.Attributes.TryGetValue("data-placeholder", out var placeholder);
            displayContent = placeholder ?? string.Empty;
        }

        // Calculate the cell width of the value
        var valueCellLength = 0;
        if (value.Length > 0)
        {
            valueCellLength = Segment.CellCount(
                new List<Segment> { new(value) });
        }

        // Parse content left padding (from ContentPadding.Left on TextInput)
        var contentLeftPadding = 1; // default ContentPadding is (1, 0, 1, 0)
        if (target.Attributes.TryGetValue("data-content-left-padding", out var padStr)
            && padStr is not null
            && int.TryParse(padStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var padVal))
        {
            contentLeftPadding = padVal;
        }

        // Parse border left padding (from BorderPadding.Left on TextInput)
        var borderLeftPadding = 0; // default BorderPadding is (0, 0, 0, 0)
        if (target.Attributes.TryGetValue("data-border-left-padding", out var bPadStr)
            && bPadStr is not null
            && int.TryParse(bPadStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bPadVal))
        {
            borderLeftPadding = bPadVal;
        }

        return new CursorHint(borderColor, valueCellLength, contentLeftPadding + borderLeftPadding, displayContent);
    }
}
