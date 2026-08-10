using STranslate.Views;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace STranslate.Tests;

public class MouseSelectionIconWindowTests
{
    [Fact]
    public void FadeInAnimationHasWindowAsImplicitTarget()
    {
        RunOnStaThread(() =>
        {
            var window = new MouseSelectionIconWindow();
            window.StartFadeIn();
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
