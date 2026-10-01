using System.Runtime.ExceptionServices;

namespace DiskSweeper.Core;

/// <summary>Runs a function on a fresh single-threaded-apartment thread, which some shell APIs (like the Recycle Bin) expect.</summary>
internal static class StaThread
{
    public static T Run<T>(Func<T> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return work();

        T result = default!;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        error?.Throw();
        return result;
    }
}
