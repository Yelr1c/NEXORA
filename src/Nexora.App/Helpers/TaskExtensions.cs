namespace Nexora.App.Helpers;

public static class TaskExtensions
{
    /// <summary>
    /// Runs a task without awaiting it, but never lets its failure go unnoticed: errors are logged with the
    /// operation name and shown to the user instead of surfacing later as an unobserved exception.
    /// </summary>
    public static async void Forget(this Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected.
        }
        catch (Exception ex)
        {
            App.ReportBackgroundError(operation, ex);
        }
    }
}
