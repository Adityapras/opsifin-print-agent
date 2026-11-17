using System.Windows.Input;

namespace Agent.Desktop.Infrastructure;

internal sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? onException = null) : ICommand
{
    private bool isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !isRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        isRunning = true;
        try
        {
            RaiseCanExecuteChanged();
            await execute();
        }
        catch (OperationCanceledException)
        {
            // Menutup window atau membatalkan operasi panjang adalah jalur normal.
        }
        catch (Exception exception)
        {
            ReportException(exception);
        }
        finally
        {
            isRunning = false;
            try
            {
                RaiseCanExecuteChanged();
            }
            catch (Exception exception)
            {
                // Handler event milik UI juga tidak boleh keluar dari async void.
                ReportException(exception);
            }
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private void ReportException(Exception exception)
    {
        try
        {
            if (onException is not null)
            {
                onException(exception);
            }
            else
            {
                System.Diagnostics.Trace.TraceError(exception.ToString());
            }
        }
        catch (Exception reportingException)
        {
            // Reporter adalah batas terakhir; kegagalannya dicatat tanpa menutup GUI.
            System.Diagnostics.Trace.TraceError(exception.ToString());
            System.Diagnostics.Trace.TraceError(reportingException.ToString());
        }
    }
}
