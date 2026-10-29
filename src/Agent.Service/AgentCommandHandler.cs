using System.Text.Json;
using Agent.Hub;
using Agent.Printing;
using Agent.Runtime;

namespace Agent.Service;

public sealed class AgentCommandHandler(
    ILogger<AgentCommandHandler> logger,
    AgentHubClient hubClient,
    JobCoordinator jobCoordinator,
    DeviceIdentityStore identityStore,
    AgentLogArchive logArchive,
    CommandJournal journal,
    TimeProvider timeProvider)
{
    public async Task HandleAsync(
        DeviceIdentity identity,
        IReadOnlyList<AgentCommand> commands,
        CancellationToken cancellationToken)
    {
        foreach (var command in commands)
        {
            var record = await journal.LoadAsync(command.Id, cancellationToken);
            if (record is not null)
            {
                await ReportExistingAsync(identity, command, record, cancellationToken);
                continue;
            }

            await journal.SaveAsync(
                new CommandExecutionRecord(
                    command.Id,
                    command.Type,
                    CommandExecutionState.Started,
                    null,
                    null,
                    timeProvider.GetUtcNow()),
                cancellationToken);

            CommandResult result;
            try
            {
                result = await ExecuteAsync(identity, command, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // State Started sengaja dipertahankan. Setelah restart command tidak akan
                // dijalankan ulang karena efek samping sebelumnya mungkin sudah terjadi.
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Command {CommandId} ({CommandType}) gagal", command.Id, command.Type);
                result = new CommandResult("failed", "Command gagal dijalankan oleh agent.");
            }

            var completed = new CommandExecutionRecord(
                command.Id,
                command.Type,
                CommandExecutionState.Completed,
                result.Status,
                result.Message,
                timeProvider.GetUtcNow(),
                result.AttachmentPath,
                result.AttachmentName,
                result.AttachmentMediaType);
            await journal.SaveAsync(completed, cancellationToken);
            await ReportAsync(identity, completed, cancellationToken);
        }
    }

    private async Task<CommandResult> ExecuteAsync(
        DeviceIdentity identity,
        AgentCommand command,
        CancellationToken cancellationToken) => command.Type switch
        {
            "reload_config" => new CommandResult(
                "succeeded",
                "Konfigurasi terbaru sudah diterapkan dari heartbeat."),
            "test_page" => await PrintTestPageAsync(identity, command, cancellationToken),
            "upload_logs" => await UploadLogsAsync(command, cancellationToken),
            "update" => new CommandResult(
                "failed",
                "Updater menunggu spesifikasi kunci publik dan signature Hub."),
            "rotate_secret" => await RotateSecretAsync(identity, command, cancellationToken),
            _ => new CommandResult("failed", $"Tipe command '{command.Type}' tidak dikenal."),
        };

    private async Task<CommandResult> PrintTestPageAsync(
        DeviceIdentity identity,
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryReadPrinterId(command.Args, out var printerId))
        {
            return new CommandResult("failed", "Argumen printer_id tidak valid atau tidak tersedia.");
        }

        var result = await jobCoordinator.PrintTestPageAsync(
            command.Id,
            identity.AgentId,
            printerId,
            cancellationToken);
        return result.Outcome == SpoolerMonitorOutcome.Completed
            ? new CommandResult(
                "succeeded",
                $"Test page selesai dicetak (spooler JobId {result.SpoolerJobId}).")
            : new CommandResult(
                "failed",
                $"Test page tidak terverifikasi: {result.Outcome}. {result.Message}");
    }

    private async Task<CommandResult> UploadLogsAsync(
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryReadHours(command.Args, out var hours))
        {
            return new CommandResult("failed", "Argumen hours harus berupa angka 1 sampai 168.");
        }

        var archivePath = await logArchive.CreateAsync(command.Id, hours, cancellationToken);
        return new CommandResult(
            "succeeded",
            $"Log {hours} jam terakhir disertakan.",
            archivePath,
            $"opsifin-agent-logs-{command.Id:N}.zip",
            "application/zip");
    }

    private async Task<CommandResult> RotateSecretAsync(
        DeviceIdentity identity,
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryReadString(command.Args, "new_secret", out var newSecret))
        {
            return new CommandResult("failed", "Argumen new_secret tidak valid atau tidak tersedia.");
        }

        // Identity baru dipersistenkan lebih dulu. Bila proses mati setelah titik ini, restart
        // akan memakai secret baru dan jurnal mencegah command rotasi dijalankan dua kali.
        await identityStore.SaveAsync(identity.WithDeviceSecret(newSecret), cancellationToken);
        identity.UseDeviceSecret(newSecret);
        return new CommandResult("succeeded", "Device secret berhasil dirotasi.");
    }

    private async Task ReportExistingAsync(
        DeviceIdentity identity,
        AgentCommand command,
        CommandExecutionRecord record,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(record.CommandType, command.Type, StringComparison.Ordinal))
        {
            logger.LogError(
                "Command ID {CommandId} dipakai ulang untuk tipe {OldType} dan {NewType}",
                command.Id,
                record.CommandType,
                command.Type);
            await ReportAsync(
                identity,
                record with
                {
                    State = CommandExecutionState.Completed,
                    ResultStatus = "failed",
                    ResultMessage = "Command ID dipakai ulang dengan tipe berbeda.",
                    AttachmentPath = null,
                    AttachmentName = null,
                    AttachmentMediaType = null,
                },
                cancellationToken);
            return;
        }

        if (record.State == CommandExecutionState.Started)
        {
            // Efek samping mungkin sudah terjadi sebelum proses mati; mengulang otomatis
            // lebih berbahaya daripada meminta operator memeriksa hasil sebelumnya.
            record = record with
            {
                State = CommandExecutionState.Completed,
                ResultStatus = "failed",
                ResultMessage = "Command sebelumnya terputus dan tidak dijalankan ulang untuk mencegah duplikasi.",
                UpdatedAt = timeProvider.GetUtcNow(),
            };
            await journal.SaveAsync(record, cancellationToken);
        }

        await ReportAsync(identity, record, cancellationToken);
    }

    private async Task ReportAsync(
        DeviceIdentity identity,
        CommandExecutionRecord record,
        CancellationToken cancellationToken)
    {
        var status = record.ResultStatus ?? "failed";
        var message = record.ResultMessage;
        CommandResultAttachment? attachment = null;
        if (record.AttachmentPath is not null)
        {
            if (File.Exists(record.AttachmentPath))
            {
                attachment = new CommandResultAttachment(
                    new FileStream(
                        record.AttachmentPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read),
                    record.AttachmentName ?? Path.GetFileName(record.AttachmentPath),
                    record.AttachmentMediaType ?? "application/octet-stream");
            }
            else
            {
                status = "failed";
                message = "Lampiran command tidak tersedia lagi di penyimpanan lokal agent.";
            }
        }

        await hubClient.SendCommandResultAsync(
            identity.HubBaseAddress,
            identity.BearerToken,
            AgentMetadata.Version,
            record.CommandId,
            status,
            message,
            attachment,
            cancellationToken);
        logger.LogInformation(
            "Command {CommandId} ({CommandType}) dilaporkan {CommandStatus}",
            record.CommandId,
            record.CommandType,
            record.ResultStatus);
    }

    private static bool TryReadPrinterId(JsonElement? args, out Guid printerId)
    {
        printerId = Guid.Empty;
        return args is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty("printer_id", out var property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out printerId);
    }

    private static bool TryReadHours(JsonElement? args, out int hours)
    {
        hours = 24;
        if (args is null)
        {
            return true;
        }

        return args is { ValueKind: JsonValueKind.Object } value
            && (!value.TryGetProperty("hours", out var property)
                || (property.TryGetInt32(out hours) && hours is >= 1 and <= 168));
    }

    private static bool TryReadString(JsonElement? args, string name, out string value)
    {
        value = string.Empty;
        if (args is not { ValueKind: JsonValueKind.Object } objectValue
            || !objectValue.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private sealed record CommandResult(
        string Status,
        string Message,
        string? AttachmentPath = null,
        string? AttachmentName = null,
        string? AttachmentMediaType = null);
}
