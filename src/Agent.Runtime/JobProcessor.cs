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
    public Task<JobProcessResult> ProcessAsync(
        ClaimedJob job,
        string windowsPrinterName,
        CancellationToken cancellationToken) => ProcessAsync(
            job,
            windowsPrinterName,
            progressReporter: null,
            cancellationToken);

    public async Task<JobProcessResult> ProcessAsync(
        ClaimedJob job,
        string windowsPrinterName,
        Func<JobProgressUpdate, CancellationToken, Task>? progressReporter,
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
                    $"Print engine '{job.Print.Engine}' is not supported by this agent build.");
            }

            await TryReportProgressAsync(
                progressReporter,
                new JobProgressUpdate(JobProgressStages.Downloading),
                cancellationToken);
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

            await TryReportProgressAsync(
                progressReporter,
                new JobProgressUpdate(JobProgressStages.SendingToPrinter),
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
                    $"The engine submitted {submission.PageCount} pages, but the claim expects {expectedPages}.");
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

            await TryReportProgressAsync(
                progressReporter,
                new JobProgressUpdate(JobProgressStages.Printing, submission.SpoolerJobId),
                cancellationToken);

            await EnqueueAsync(
                job,
                "spooled",
                new SpooledData(submission.SpoolerJobId, submission.PageCount),
                RuntimeJsonContext.Default.SpooledData,
                cancellationToken);

            var verificationReported = false;
            var monitorResult = await spoolerMonitor.MonitorAsync(
                windowsPrinterName,
                submission.SpoolerJobId,
                async (observation, token) =>
                {
                    if (!observation.JobExists && !verificationReported)
                    {
                        verificationReported = true;
                        await TryReportProgressAsync(
                            progressReporter,
                            new JobProgressUpdate(
                                JobProgressStages.VerifyingCompletion,
                                submission.SpoolerJobId),
                            token);
                    }

                    await EnqueueAsync(
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
                        token);
                },
                pollInterval: options.SpoolerPollInterval,
                verificationWindow: options.CompletionVerificationWindow,
                // StartDoc/EndDoc yang mengembalikan JobId membuktikan job pernah masuk
                // spooler, meskipun printer cepat sudah menghapusnya sebelum poll pertama.
                jobWasPreviouslyObserved: true,
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
                "The job was stopped before completion.");
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

    public Task<JobProcessResult> ResumeAsync(
        ClaimedJob job,
        ActivePrintRecord record,
        CancellationToken cancellationToken) => ResumeAsync(
            job,
            record,
            progressReporter: null,
            cancellationToken);

    public async Task<JobProcessResult> ResumeAsync(
        ClaimedJob job,
        ActivePrintRecord record,
        Func<JobProgressUpdate, CancellationToken, Task>? progressReporter,
        CancellationToken cancellationToken)
    {
        if (record.JobId != job.JobId)
        {
            throw new InvalidOperationException("The spool journal does not match the claimed job.");
        }

        try
        {
            await TryReportProgressAsync(
                progressReporter,
                new JobProgressUpdate(JobProgressStages.Printing, record.SpoolerJobId),
                cancellationToken);
            // Event baru untuk attempt lease saat ini memberi Hub konteks JobId spooler lama
            // tanpa mengirim PDF yang sama untuk kedua kalinya.
            await EnqueueAsync(
                job,
                "spooled",
                new SpooledData(record.SpoolerJobId, record.PageCount),
                RuntimeJsonContext.Default.SpooledData,
                cancellationToken);
            var verificationReported = false;
            var monitorResult = await spoolerMonitor.MonitorAsync(
                record.WindowsPrinterName,
                record.SpoolerJobId,
                async (observation, token) =>
                {
                    if (!observation.JobExists && !verificationReported)
                    {
                        verificationReported = true;
                        await TryReportProgressAsync(
                            progressReporter,
                            new JobProgressUpdate(
                                JobProgressStages.VerifyingCompletion,
                                record.SpoolerJobId),
                            token);
                    }

                    await EnqueueAsync(
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
                        token);
                },
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
                "The recovered job was stopped.");
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

    private static async Task TryReportProgressAsync(
        Func<JobProgressUpdate, CancellationToken, Task>? progressReporter,
        JobProgressUpdate update,
        CancellationToken cancellationToken)
    {
        if (progressReporter is null)
        {
            return;
        }

        try
        {
            await progressReporter(update, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Pembatalan job akan ditangani jalur utama; snapshot UI tidak boleh mengubah hasilnya.
        }
        catch
        {
            // Status lokal bersifat observabilitas dan tidak boleh menggagalkan pencetakan.
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
                new PrintErrorData("SPOOLER_FAILED", monitor.Detail ?? "The spooler failed."),
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
        _ => throw new InvalidDataException($"Scale mode '{value}' is not recognized."),
    };

    private static string ClassifyError(Exception exception) => exception switch
    {
        UnattendedPrintingNotSupportedException => "PRINTER_REQUIRES_INTERACTION",
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
