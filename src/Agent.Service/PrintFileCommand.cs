using Agent.Printing;

namespace Agent.Service;

internal static class PrintFileCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var file = ReadOption(args, "--file");
        var printer = ReadOption(args, "--printer");
        var copiesText = ReadOption(args, "--copies") ?? "1";
        var scaleText = ReadOption(args, "--scale") ?? "noscale";
        if (file is null || printer is null
            || !int.TryParse(copiesText, out var copies)
            || !TryParseScale(scaleText, out var scale))
        {
            WriteUsage();
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var fullPath = Path.GetFullPath(file);
            IPrintEngine engine = new PdfiumPrintEngine();
            var submission = await engine.SubmitAsync(
                new PrintRequest(
                    fullPath,
                    printer,
                    $"Opsifin test - {Path.GetFileName(fullPath)}",
                    copies,
                    Scale: scale),
                cancellation.Token);

            Console.WriteLine(
                $"Dokumen masuk spooler. JobId={submission.SpoolerJobId}, halaman={submission.PageCount}.");
            if (args.Contains("--no-monitor", StringComparer.OrdinalIgnoreCase))
            {
                return 0;
            }

            Console.WriteLine("Memantau JobId dan kesehatan printer selama 60 detik setelah job hilang...");
            var monitor = new SpoolerJobMonitor(new Win32SpoolerStatusSource());
            var result = await monitor.MonitorAsync(
                printer,
                submission.SpoolerJobId,
                OnObservationChanged,
                cancellationToken: cancellation.Token);

            Console.WriteLine(
                $"Hasil={result.Outcome}, pages_printed={result.PagesPrinted}, "
                + $"job_status=0x{result.LastJobStatus:X8}, printer_status=0x{result.LastPrinterStatus:X8}.");
            Console.WriteLine(result.Detail);
            return result.Outcome == SpoolerMonitorOutcome.Completed ? 0 : 1;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Pengujian dibatalkan.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Pengujian cetak gagal: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static ValueTask OnObservationChanged(
        SpoolerObservation observation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine(
            $"spooler: exists={observation.JobExists}, job=0x{observation.JobStatus:X8}, "
            + $"printer=0x{observation.PrinterStatus:X8}, pages={observation.PagesPrinted}/{observation.TotalPages}");
        return ValueTask.CompletedTask;
    }

    private static bool TryParseScale(string value, out PrintScaleMode scale)
    {
        scale = value.ToLowerInvariant() switch
        {
            "noscale" => PrintScaleMode.NoScale,
            "fit" => PrintScaleMode.Fit,
            "shrink" => PrintScaleMode.Shrink,
            _ => (PrintScaleMode)(-1),
        };
        return Enum.IsDefined(scale);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void WriteUsage() => Console.Error.WriteLine(
        "Pemakaian: Agent.Service.exe print-file --file <PDF> --printer <NAMA> "
        + "[--copies 1] [--scale noscale|fit|shrink] [--no-monitor]");
}
