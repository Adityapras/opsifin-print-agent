using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Agent.Hub;
using Agent.Printing;

namespace Agent.Runtime;

public sealed class JobProcessor(
    SignedDocumentDownloader downloader,
    EventOutbox outbox,
    IPrintEngine printEngine,
    SpoolerJobMonitor spoolerMonitor,
    ISpoolerJobController spoolerController,
    PrintJobJournal journal,
    JobProcessorOptions options,
    TimeProvider timeProvider)
{
    public async Task<JobProcessResult> ProcessAsync(
        ClaimedJob job,
        string windowsPrinterName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsPrinterName);
        Directory.CreateDirectory(options.DocumentsDirectory);
        var documentPath = Path.Combine(
            options.DocumentsDirectory,
            $"{job.JobId:N}_{job.AttemptId:N}.pdf");
        PrintSubmission? submission = null;
        var journalSaved = false;

        try
        {
            if (!string.Equals(job.Print.Engine, "pdfium", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"Print engine '{job.Print.Engine}' belum didukung build agent ini.");
            }

            await EnqueueAsync(
                job,
                "download_started",
                new DownloadStartedData(job.Document.SizeBytes, job.Document.Pages),
                RuntimeJsonContext.Default.DownloadStartedData,
                cancellationToken);

            var downloadStarted = Stopwatch.GetTimestamp();
            var document = await downloader.DownloadAsync(job.Document, documentPath, cancellationToken);
            await EnqueueAsync(
                job,
                "download_completed",
                new DownloadCompletedData(
                    document.SizeBytes,
                    document.Sha256,
                    (long)Stopwatch.GetElapsedTime(downloadStarted).TotalMilliseconds),
                RuntimeJsonContext.Default.DownloadCompletedData,
                cancellationToken);

            submission = await printEngine.SubmitAsync(
                new PrintRequest(
                    document.Path,
                    windowsPrinterName,
                    $"Opsifin {job.JobId:D}",
                    job.Print.Copies,
                    job.Print.Duplex,
                    job.Print.Tray,
                    ParseScale(job.Print.Scale),
                    job.Document.Pages),
                cancellationToken);

            var expectedPages = checked(job.Document.Pages * job.Print.Copies);
            if (submission.PageCount != expectedPages)
            {
                throw new InvalidDataException(
                    $"Engine mengirim {submission.PageCount} halaman, claim mengharapkan {expectedPages}.");
            }

            await journal.SaveAsync(
                new ActivePrintRecord(
                    job.JobId,
                    windowsPrinterName,
                    submission.SpoolerJobId,
                    submission.PageCount,
                    timeProvider.GetUtcNow()),
                cancellationToken);
            journalSaved = true;

            await EnqueueAsync(
                job,
                "spooled",
                new SpooledData(submission.SpoolerJobId, submission.PageCount),
                RuntimeJsonContext.Default.SpooledData,
                cancellationToken);

            var monitorResult = await spoolerMonitor.MonitorAsync(
                windowsPrinterName,
                submission.SpoolerJobId,
                (observation, token) => EnqueueAsync(
                    job,
                    "spooler_status",
                    new SpoolerStatusData(
                        submission.SpoolerJobId,
                        observation.JobExists,
                        observation.JobStatus,
                        observation.PrinterStatus,
                        observation.PagesPrinted,
                        observation.TotalPages,
                        observation.StatusText),
                    RuntimeJsonContext.Default.SpoolerStatusData,
                    token),
                pollInterval: options.SpoolerPollInterval,
                verificationWindow: options.CompletionVerificationWindow,
                cancellationToken: cancellationToken);

            return await CompleteFromMonitorAsync(job, submission.SpoolerJobId, monitorResult, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (submission is not null)
            {
                await spoolerController.CancelAsync(
                    windowsPrinterName,
                    submission.SpoolerJobId,
                    CancellationToken.None);
            }

            return new JobProcessResult(
                JobProcessOutcome.Aborted,
                submission?.SpoolerJobId,
                null,
                "Pekerjaan dihentikan sebelum selesai.");
        }
        catch (Exception ex)
        {
            var errorCode = ClassifyError(ex);
            await EnqueueAsync(
                job,
                "print_error",
                new PrintErrorData(errorCode, SafeMessage(ex)),
                RuntimeJsonContext.Default.PrintErrorData,
                CancellationToken.None);
            return new JobProcessResult(
                JobProcessOutcome.Failed,
                submission?.SpoolerJobId,
                errorCode,
                SafeMessage(ex));
        }
        finally
        {
            if (journalSaved)
            {
                journal.Remove(job.JobId);
            }

            TryDelete(documentPath);
        }
    }

    public async Task<JobProcessResult> ResumeAsync(
        ClaimedJob job,
        ActivePrintRecord record,
        CancellationToken cancellationToken)
    {
        if (record.JobId != job.JobId)
        {
            throw new InvalidOperationException("Jurnal spool tidak cocok dengan job claim.");
        }

        try
        {
            // Event baru untuk attempt lease saat ini memberi Hub konteks JobId spooler lama
            // tanpa mengirim PDF yang sama untuk kedua kalinya.
            await EnqueueAsync(
                job,
                "spooled",
                new SpooledData(record.SpoolerJobId, record.PageCount),
                RuntimeJsonContext.Default.SpooledData,
                cancellationToken);
            var monitorResult = await spoolerMonitor.MonitorAsync(
                record.WindowsPrinterName,
                record.SpoolerJobId,
                (observation, token) => EnqueueAsync(
                    job,
                    "spooler_status",
                    new SpoolerStatusData(
                        record.SpoolerJobId,
                        observation.JobExists,
                        observation.JobStatus,
                        observation.PrinterStatus,
                        observation.PagesPrinted,
                        observation.TotalPages,
                        observation.StatusText),
                    RuntimeJsonContext.Default.SpoolerStatusData,
                    token),
                pollInterval: options.SpoolerPollInterval,
                verificationWindow: options.CompletionVerificationWindow,
                jobWasPreviouslyObserved: true,
                cancellationToken: cancellationToken);
            return await CompleteFromMonitorAsync(
                job,
                record.SpoolerJobId,
                monitorResult,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await spoolerController.CancelAsync(
                record.WindowsPrinterName,
                record.SpoolerJobId,
                CancellationToken.None);
            return new JobProcessResult(
                JobProcessOutcome.Aborted,
                record.SpoolerJobId,
                null,
                "Pekerjaan pemulihan dihentikan.");
        }
        catch (Exception ex)
        {
            var errorCode = ClassifyError(ex);
            await EnqueueAsync(
                job,
                "print_error",
                new PrintErrorData(errorCode, SafeMessage(ex)),
                RuntimeJsonContext.Default.PrintErrorData,
                CancellationToken.None);
            return new JobProcessResult(
                JobProcessOutcome.Failed,
                record.SpoolerJobId,
                errorCode,
                SafeMessage(ex));
        }
        finally
        {
            journal.Remove(job.JobId);
        }
    }

    private async Task<JobProcessResult> CompleteFromMonitorAsync(
        ClaimedJob job,
        uint spoolerJobId,
        SpoolerMonitorResult monitor,
        CancellationToken cancellationToken)
    {
        var eventType = monitor.Outcome switch
        {
            SpoolerMonitorOutcome.Completed => "completed",
            SpoolerMonitorOutcome.PrintedUnverified => "printed_unverified",
            SpoolerMonitorOutcome.Failed or SpoolerMonitorOutcome.NotObserved => "print_error",
            _ => throw new ArgumentOutOfRangeException(nameof(monitor)),
        };

        if (eventType == "print_error")
        {
            await EnqueueAsync(
                job,
                eventType,
                new PrintErrorData("SPOOLER_FAILED", monitor.Detail ?? "Spooler gagal."),
                RuntimeJsonContext.Default.PrintErrorData,
                cancellationToken);
            return new JobProcessResult(
                JobProcessOutcome.Failed,
                spoolerJobId,
                "SPOOLER_FAILED",
                monitor.Detail);
        }

        await EnqueueAsync(
            job,
            eventType,
            new PrintCompletedData(spoolerJobId, monitor.PagesPrinted),
            RuntimeJsonContext.Default.PrintCompletedData,
            cancellationToken);
        return new JobProcessResult(
            eventType == "completed" ? JobProcessOutcome.Completed : JobProcessOutcome.PrintedUnverified,
            spoolerJobId,
            null,
            monitor.Detail);
    }

    private async ValueTask EnqueueAsync<T>(
        ClaimedJob job,
        string eventType,
        T data,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var element = JsonSerializer.SerializeToElement(data, typeInfo);
        await outbox.EnqueueAsync(
            job.JobId,
            new JobEvent(
                Guid.CreateVersion7(),
                job.AttemptId,
                eventType,
                timeProvider.GetUtcNow(),
                element),
            cancellationToken);
    }

    private static PrintScaleMode ParseScale(string value) => value.ToLowerInvariant() switch
    {
        "noscale" => PrintScaleMode.NoScale,
        "fit" => PrintScaleMode.Fit,
        "shrink" => PrintScaleMode.Shrink,
        _ => throw new InvalidDataException($"Mode skala '{value}' tidak dikenal."),
    };

    private static string ClassifyError(Exception exception) => exception switch
    {
        InvalidDataException => "PDF_INVALID",
        FileNotFoundException => "FILE_NOT_FOUND",
        NotSupportedException => "PRINT_SETTINGS_UNSUPPORTED",
        _ => "PRINT_FAILED",
    };

    private static string SafeMessage(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        return message.Length <= 1000 ? message : message[..1000];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Cleanup terbaik-usaha; nama file per attempt mencegah file tertinggal dipakai
            // oleh pekerjaan lain dan pemeliharaan berkala dapat menghapusnya kemudian.
        }
        catch (UnauthorizedAccessException)
        {
            // Kegagalan cleanup tidak boleh mengubah hasil cetak yang sudah final.
        }
    }
}
