using Microsoft.Extensions.Logging.Abstractions;
using STranslate.Core;
using STranslate.Services;
using System.Drawing;

namespace STranslate.Tests;

public class MouseSelectionServiceTests
{
    [Fact]
    public void MouseHookSettingsDefaultToDisabledDirectMode()
    {
        var settings = new Settings();

        Assert.False(settings.IsMouseHook);
        Assert.False(settings.ShowIconAfterMouseSelection);
        Assert.Null(typeof(Settings).GetProperty("ShowMouseHookIcon"));
    }

    [Fact]
    public void DragDetectorIgnoresClickAndMovementBelowSystemThreshold()
    {
        var detector = new MouseDragDetector(4, 4);

        detector.OnLeftButtonDown(new Point(10, 10), isIBeam: true);
        detector.OnMouseMove(new Point(13, 13), isIBeam: true);

        Assert.False(detector.TryComplete(new Point(13, 13), isIBeam: true, out _));
    }

    [Fact]
    public void DragDetectorAcceptsThresholdMovementWhenEitherEndpointUsesIBeam()
    {
        var detector = new MouseDragDetector(4, 4);

        detector.OnLeftButtonDown(new Point(10, 10), isIBeam: false);
        detector.OnMouseMove(new Point(14, 10), isIBeam: false);

        Assert.True(detector.TryComplete(new Point(14, 10), isIBeam: true, out var completedPoint));
        Assert.Equal(new Point(14, 10), completedPoint);
        Assert.False(detector.TryComplete(completedPoint, isIBeam: true, out _));
    }

    [Fact]
    public void DragDetectorAcceptsTextCursorObservedBetweenNonTextEndpoints()
    {
        var detector = new MouseDragDetector(4, 4);

        detector.OnLeftButtonDown(new Point(20, 10), isIBeam: false);
        detector.OnMouseMove(new Point(18, 10), isIBeam: true);
        detector.OnMouseMove(new Point(10, 10), isIBeam: false);

        Assert.True(detector.TryComplete(new Point(10, 10), isIBeam: false, out _));
    }

    [Fact]
    public async Task DirectModeCapturesTextWithoutRequestingIcon()
    {
        var hook = new FakeMouseHookService();
        var settings = new Settings { ShowIconAfterMouseSelection = false };
        var selectedText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCount = 0;
        using var service = CreateService(hook, settings, _ =>
        {
            captureCount++;
            return Task.FromResult<string?>("selected");
        });
        var iconRequestCount = 0;
        service.TextSelected += (_, text) => selectedText.TrySetResult(text);
        service.IconRequested += (_, _) => iconRequestCount++;

        Assert.True(service.Start());
        hook.RaiseSelectionCompleted(new Point(20, 30));

        Assert.Equal("selected", await selectedText.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, captureCount);
        Assert.Equal(0, iconRequestCount);
    }

    [Fact]
    public async Task IconModeDefersTextCaptureUntilRequested()
    {
        var hook = new FakeMouseHookService();
        var settings = new Settings { ShowIconAfterMouseSelection = true };
        var captureCount = 0;
        using var service = CreateService(hook, settings, _ =>
        {
            captureCount++;
            return Task.FromResult<string?>("selected");
        });
        Point? iconPoint = null;
        service.IconRequested += (_, point) => iconPoint = point;

        Assert.True(service.Start());
        hook.RaiseSelectionCompleted(new Point(20, 30));

        Assert.Equal(new Point(20, 30), iconPoint);
        Assert.Equal(0, captureCount);
        Assert.Equal("selected", await service.CaptureSelectedTextAsync());
        Assert.Equal(1, captureCount);
    }

    [Fact]
    public void PersistentAndIncrementalConsumersShareHookLifetime()
    {
        var hook = new FakeMouseHookService();
        using var service = CreateService(hook, new Settings(), _ => Task.FromResult<string?>(null));

        Assert.True(service.Start());
        Assert.True(service.StartIncrementalCapture());
        Assert.Equal(1, hook.StartCount);

        service.Stop();
        Assert.Equal(0, hook.StopCount);

        service.StopIncrementalCapture();
        Assert.Equal(1, hook.StopCount);
    }

    [Fact]
    public void RepeatedStartAndStopRemainIdempotent()
    {
        var hook = new FakeMouseHookService();
        using var service = CreateService(hook, new Settings(), _ => Task.FromResult<string?>(null));

        Assert.True(service.Start());
        Assert.True(service.Start());
        service.Stop();
        service.Stop();

        Assert.Equal(1, hook.StartCount);
        Assert.Equal(1, hook.StopCount);
    }

    [Fact]
    public void FailedHookStartDoesNotEnablePersistentConsumer()
    {
        var hook = new FakeMouseHookService { StartSucceeds = false };
        using var service = CreateService(hook, new Settings(), _ => Task.FromResult<string?>(null));

        Assert.False(service.Start());
        service.Stop();

        Assert.Equal(1, hook.StartCount);
        Assert.Equal(0, hook.StopCount);
    }

    [Fact]
    public async Task IncrementalCaptureTakesPriorityOverPersistentMode()
    {
        var hook = new FakeMouseHookService();
        var settings = new Settings { ShowIconAfterMouseSelection = true };
        var incrementalText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateService(hook, settings, _ => Task.FromResult<string?>("incremental"));
        var iconRequestCount = 0;
        service.IncrementalTextSelected += (_, text) => incrementalText.TrySetResult(text);
        service.IconRequested += (_, _) => iconRequestCount++;

        Assert.True(service.Start());
        Assert.True(service.StartIncrementalCapture());
        hook.RaiseSelectionCompleted(new Point(20, 30));

        Assert.Equal("incremental", await incrementalText.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, iconRequestCount);
    }

    [Fact]
    public void ModeChangeDoesNotRestartHookAndDismissesExistingIcon()
    {
        var hook = new FakeMouseHookService();
        using var service = CreateService(hook, new Settings(), _ => Task.FromResult<string?>(null));
        var dismissCount = 0;
        service.IconDismissRequested += (_, _) => dismissCount++;

        Assert.True(service.Start());
        service.ApplyModeChange();

        Assert.Equal(1, hook.StartCount);
        Assert.Equal(0, hook.StopCount);
        Assert.Equal(1, dismissCount);
    }

    [Fact]
    public void MouseDownIsRelayedWithoutDismissingIcon()
    {
        var hook = new FakeMouseHookService();
        using var service = CreateService(hook, new Settings(), _ => Task.FromResult<string?>(null));
        Point? startedPoint = null;
        var dismissCount = 0;
        service.SelectionStarted += (_, point) => startedPoint = point;
        service.IconDismissRequested += (_, _) => dismissCount++;

        Assert.True(service.Start());
        hook.RaiseSelectionStarted(new Point(12, 34));

        Assert.Equal(new Point(12, 34), startedPoint);
        Assert.Equal(0, dismissCount);
    }

    private static MouseSelectionService CreateService(
        IMouseHookService hook,
        Settings settings,
        Func<int, Task<string?>> getSelectedTextAsync) =>
        new(hook, settings, NullLogger<MouseSelectionService>.Instance, getSelectedTextAsync);

    private sealed class FakeMouseHookService : IMouseHookService
    {
        public event EventHandler<Point>? SelectionStarted;
        public event EventHandler<MouseDragCompletedEventArgs>? SelectionCompleted;

        public bool IsRunning { get; private set; }
        public bool StartSucceeds { get; init; } = true;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public bool Start()
        {
            StartCount++;
            IsRunning = StartSucceeds;
            return StartSucceeds;
        }

        public void Stop()
        {
            StopCount++;
            IsRunning = false;
        }

        public void RaiseSelectionStarted(Point point) => SelectionStarted?.Invoke(this, point);

        public void RaiseSelectionCompleted(Point point) =>
            SelectionCompleted?.Invoke(this, new MouseDragCompletedEventArgs(point));

        public void Dispose() => Stop();
    }
}
