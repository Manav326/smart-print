using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SmartPrint.Agent;

public sealed class AgentLocalQueueStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QRtoPrint.Agent.LocalQueue.Document.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string queueRoot;
    private readonly bool applyWindowsAcl;
    private readonly SemaphoreSlim stagedUploadLeaseGate = new(1, 1);

    public AgentLocalQueueStore(string? rootOverride = null)
    {
        queueRoot = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "QRtoPrint",
            "LocalQueue");
        applyWindowsAcl = rootOverride is null;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(queueRoot);
        if (applyWindowsAcl)
        {
            SecureDirectory(queueRoot);
        }

        foreach (var temporaryFile in Directory.EnumerateFiles(queueRoot, "*.tmp", SearchOption.TopDirectoryOnly))
        {
            File.Delete(temporaryFile);
        }

        foreach (var documentFile in Directory.EnumerateFiles(queueRoot, "*.qdoc", SearchOption.TopDirectoryOnly))
        {
            var manifestPath = Path.ChangeExtension(documentFile, ".json");
            if (!File.Exists(manifestPath))
            {
                File.Delete(documentFile);
                var orphanedPhotoSource = documentFile + ".photo.source";
                if (File.Exists(orphanedPhotoSource)) File.Delete(orphanedPhotoSource);
            }
        }
        foreach (var photoSourceFile in Directory.EnumerateFiles(queueRoot, "*.qdoc.photo.source", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var documentFile = photoSourceFile[..^".photo.source".Length];
            var jobIdText = Path.GetFileNameWithoutExtension(documentFile);
            if (!Guid.TryParseExact(jobIdText, "N", out var jobId))
                continue;

            var manifestPath = GetManifestPath(jobId);
            if (!File.Exists(manifestPath))
            {
                File.Delete(photoSourceFile);
                continue;
            }

            var entry = Find(jobId);
            if (entry is null || !string.IsNullOrWhiteSpace(entry.PhotoSourceSha256))
                continue; // The source is now the committed retained original.

            // Recover a first-edit interruption. The old manifest has no retained
            // source metadata yet; if its normal .qdoc no longer matches the
            // original, restore the encrypted source sidecar before the queue is
            // made available again.
            var documentStillOriginal = false;
            try
            {
                var currentBytes = ReadDocument(jobId);
                try
                {
                    documentStillOriginal =
                        currentBytes.LongLength == entry.SizeBytes &&
                        string.Equals(
                            Convert.ToHexString(SHA256.HashData(currentBytes)).ToLowerInvariant(),
                            entry.Sha256,
                            StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(currentBytes);
                }
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException or
                InvalidDataException or CryptographicException)
            {
                // The queue manifest still describes the original image, but the
                // primary payload was moved or replaced before its manifest commit.
            }

            if (documentStillOriginal)
            {
                File.Delete(photoSourceFile); // Interrupted before the original move was needed.
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.DocumentPath))
            {
                File.Delete(photoSourceFile);
                continue;
            }
            var documentPath = Path.GetFullPath(entry.DocumentPath);
            var queuePrefix = Path.GetFullPath(queueRoot).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!documentPath.StartsWith(queuePrefix, comparison))
                throw new InvalidDataException("The interrupted photo document path is outside the protected queue.");

            if (File.Exists(documentPath)) File.Delete(documentPath);
            File.Move(photoSourceFile, documentPath);
        }

        await Task.CompletedTask;
    }

    public AgentLocalQueueEntry? Find(Guid jobId)
    {
        var manifestPath = GetManifestPath(jobId);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var json = File.ReadAllText(manifestPath, Encoding.UTF8);
        return JsonSerializer.Deserialize<AgentLocalQueueEntry>(json, JsonOptions)
            ?? throw new InvalidDataException("A local print queue manifest is invalid.");
    }

    private IReadOnlyList<AgentLocalQueueEntry> ReadEntries()
    {
        var result = new List<AgentLocalQueueEntry>();
        foreach (var path in Directory.EnumerateFiles(queueRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            var entry = JsonSerializer.Deserialize<AgentLocalQueueEntry>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
            if (entry is not null) result.Add(entry);
        }
        return result.OrderBy(x => x.ReceivedUtc).ToArray();
    }

    public IReadOnlyList<AgentLocalQueueEntry> GetAcknowledgedJobs() =>
        ReadEntries().Where(entry =>
            entry.State == "ACKNOWLEDGED" ||
            (entry.State == "OUTPUT_UNCONFIRMED" &&
             (entry.OutputMode == PrinterOutputModeRules.PdfExport ||
              PrinterOutputModeRules.IsPdfExport(entry.QueueName, entry.PrinterName))))
        .ToArray();

    public IReadOnlyList<AgentLocalQueueEntry> GetLegacyPdfQueueJobs() =>
        ReadEntries().Where(entry =>
            PrinterOutputModeRules.IsPdfExport(entry.QueueName, entry.PrinterName) &&
            (entry.State is "SUBMITTING" or "SPOOLER_ACCEPTED" or "OUTPUT_UNCONFIRMED"))
        .ToArray();

    public IReadOnlyList<AgentLocalQueueEntry> GetPdfExportRecoveryJobs() =>
        ReadEntries().Where(entry =>
            (entry.OutputMode == PrinterOutputModeRules.PdfExport ||
             PrinterOutputModeRules.IsPdfExport(entry.QueueName, entry.PrinterName)) &&
            (entry.State is "PDF_EXPORTING" or "OUTPUT_UNCONFIRMED") &&
            !string.IsNullOrWhiteSpace(entry.OutputFilePath))
        .ToArray();

    public IReadOnlyList<AgentLocalQueueEntry> GetPendingPdfExportReceipts() =>
        ReadEntries().Where(entry =>
            entry.OutputMode == PrinterOutputModeRules.PdfExport &&
            entry.State == "PDF_EXPORTED" &&
            !string.IsNullOrWhiteSpace(entry.OutputFilePath) &&
            !string.IsNullOrWhiteSpace(entry.OutputFileSha256) &&
            entry.OutputFileSizeBytes is > 0)
        .ToArray();

    public IReadOnlyList<AgentLocalQueueEntry> GetInterruptedSubmissions() =>
        ReadEntries().Where(x => x.State == "SUBMITTING").ToArray();

    public IReadOnlyList<AgentLocalQueueEntry> GetSpoolerTrackingJobs() =>
        ReadEntries().Where(x => x.State == "SPOOLER_ACCEPTED" && x.SpoolerJobId is > 0).ToArray();

    public IReadOnlyList<AgentLocalQueueEntry> GetPrivatePurgePendingJobs() =>
        ReadEntries().Where(entry => entry.PrivatePrinting &&
            (entry.State == "PRIVATE_PURGE_PENDING" || entry.PrivatePurgePending)).ToArray();

    private static async Task EraseProtectedQueueFileAsync(
        string path,
        string queuePrefix,
        StringComparison comparison,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(queuePrefix, comparison))
            throw new InvalidDataException("A private document path is outside the protected queue.");
        if (!File.Exists(fullPath)) return;

        await using (var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.WriteThrough | FileOptions.SequentialScan))
        {
            var zeroes = new byte[64 * 1024];
            long remaining = stream.Length;
            stream.Position = 0;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(zeroes.Length, remaining);
                await stream.WriteAsync(zeroes.AsMemory(0, count), cancellationToken);
                remaining -= count;
            }
            stream.Flush(flushToDisk: true);
            CryptographicOperations.ZeroMemory(zeroes);
        }
        File.Delete(fullPath);
    }

    public async Task PurgeUnindexedPrivateSourceAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("A private job ID is required.", nameof(jobId));
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(queueRoot)) return;

        var rootFull = Path.GetFullPath(queueRoot);
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var documentPath = Path.Combine(rootFull, jobId.ToString("N") + ".qdoc");
        var paths = new[]
        {
            documentPath,
            documentPath + ".tmp",
            documentPath + ".photo-revision.bak",
            documentPath + ".photo.source",
            GetManifestPath(jobId) + ".tmp"
        };

        foreach (var candidate in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(candidate);
            if (!fullPath.StartsWith(prefix, comparison))
                throw new InvalidDataException("The orphaned private document path is outside the protected queue.");
            if (!File.Exists(fullPath)) continue;

            await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                var zeroes = new byte[64 * 1024];
                long remaining = stream.Length;
                stream.Position = 0;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(zeroes.Length, remaining);
                    await stream.WriteAsync(zeroes.AsMemory(0, count), cancellationToken);
                    remaining -= count;
                }
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
                CryptographicOperations.ZeroMemory(zeroes);
            }
            File.Delete(fullPath);
        }
    }

    public IReadOnlyList<AgentLocalQueueEntry> GetStagedLocalUploads() =>
        ReadEntries().Where(entry => entry.State == "LOCAL_UPLOAD_STAGED").ToArray();

    public IReadOnlyList<AgentLocalQueueEntry> GetPendingPaymentLocalUploads() =>
        ReadEntries().Where(entry => entry.State == "LOCAL_UPLOAD_PENDING_PAYMENT").ToArray();

    public IReadOnlyList<AgentLocalQueueEntry> GetPendingAcknowledgements()
    {
        var entries = new List<AgentLocalQueueEntry>();
        foreach (var path in Directory.EnumerateFiles(queueRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var entry = JsonSerializer.Deserialize<AgentLocalQueueEntry>(json, JsonOptions);
            if (entry is not null && entry.State == "ACK_PENDING")
            {
                entries.Add(entry);
            }
        }

        return entries.OrderBy(entry => entry.ReceivedUtc).ToArray();
    }

    public Task<AgentLocalQueueEntry> EnqueueAsync(
        AgentClaimedJob job,
        ReadOnlyMemory<byte> documentBytes,
        CancellationToken cancellationToken) =>
        EnqueueCoreAsync(job, documentBytes, "ACK_PENDING", cancellationToken);

    public Task<AgentLocalQueueEntry> EnqueueLocalUploadAsync(
        AgentClaimedJob job,
        ReadOnlyMemory<byte> documentBytes,
        CancellationToken cancellationToken) =>
        EnqueueCoreAsync(job, documentBytes, "LOCAL_UPLOAD_STAGED", cancellationToken);

    private async Task<AgentLocalQueueEntry> EnqueueCoreAsync(
        AgentClaimedJob job,
        ReadOnlyMemory<byte> documentBytes,
        string initialState,
        CancellationToken cancellationToken)
    {
        if (job.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(job.JobNumber) ||
            documentBytes.Length != job.SizeBytes ||
            job.SizeBytes is <= 0 or > UploadLimits.MaxFileBytes)
        {
            throw new InvalidDataException("The claimed job metadata or document size is invalid.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(documentBytes.Span)).ToLowerInvariant();
        if (!string.Equals(actualHash, job.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded document SHA-256 does not match the server's original.");
        }

        var existing = Find(job.Id);
        if (existing is not null)
        {
            if (!string.Equals(existing.Sha256, actualHash, StringComparison.OrdinalIgnoreCase) ||
                existing.SizeBytes != job.SizeBytes)
            {
                throw new InvalidDataException("A different document is already stored for this print job.");
            }

            if (!File.Exists(existing.DocumentPath))
            {
                throw new FileNotFoundException("The local queue manifest exists but its protected document is missing.", existing.DocumentPath);
            }

            return existing;
        }

        await InitializeAsync(cancellationToken);
        var documentPath = Path.Combine(queueRoot, job.Id.ToString("N") + ".qdoc");
        var temporaryDocumentPath = documentPath + ".tmp";
        if (File.Exists(documentPath))
        {
            File.Delete(documentPath);
        }

        var plaintext = documentBytes.ToArray();
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        try
        {
            await using (var stream = new FileStream(
                temporaryDocumentPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(protectedBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryDocumentPath, documentPath);
        }
        catch
        {
            if (File.Exists(temporaryDocumentPath)) File.Delete(temporaryDocumentPath);
            if (File.Exists(documentPath)) File.Delete(documentPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }

        var entry = new AgentLocalQueueEntry(
            job.Id,
            job.JobNumber,
            job.PrinterDeviceId,
            job.PrinterName,
            job.QueueName,
            job.DocumentName,
            job.MediaType,
            actualHash,
            job.SizeBytes,
            job.PageCount,
            job.Copies,
            job.ColourMode,
            job.PaperSize,
            job.IsDuplex,
            job.Currency,
            job.QuotedAmountMinor,
            documentPath,
            initialState,
            DateTimeOffset.UtcNow,
            job.LeaseExpiresUtc,
            OutputMode: job.OutputMode,
            Orientation: job.Orientation,
            PdfBrandingEnabled: job.PdfBrandingEnabled,
            PrivatePrinting: job.PrivatePrinting,
            PrintKind: job.PrintKind);

        try
        {
            await SaveManifestAsync(entry, cancellationToken);
            return entry;
        }
        catch
        {
            if (File.Exists(documentPath)) File.Delete(documentPath);
            throw;
        }
    }

    public async Task RefreshStagedLocalUploadLeaseAsync(
        Guid jobId,
        string sha256,
        long sizeBytes,
        DateTimeOffset leaseExpiresUtc,
        CancellationToken cancellationToken)
    {
        await stagedUploadLeaseGate.WaitAsync(cancellationToken);
        try
        {
            var entry = Find(jobId)
                ?? throw new FileNotFoundException("The staged local upload is missing from the protected queue.");
            if (!string.Equals(entry.Sha256, sha256, StringComparison.OrdinalIgnoreCase) ||
                entry.SizeBytes != sizeBytes ||
                !File.Exists(entry.DocumentPath))
            {
                throw new InvalidDataException("The staged local upload no longer matches its authorised document.");
            }
            if (entry.State != "LOCAL_UPLOAD_STAGED" || leaseExpiresUtc <= entry.LeaseExpiresUtc)
            {
                return;
            }

            await SaveManifestAsync(entry with { LeaseExpiresUtc = leaseExpiresUtc }, cancellationToken);
        }
        finally
        {
            stagedUploadLeaseGate.Release();
        }
    }

    public async Task MarkLocalUploadFinalizedAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await stagedUploadLeaseGate.WaitAsync(cancellationToken);
        try
        {
            var entry = Find(jobId)
                ?? throw new FileNotFoundException("The staged local upload is missing from the protected queue.");
            if (!File.Exists(entry.DocumentPath))
            {
                throw new FileNotFoundException("The staged local upload document is missing.", entry.DocumentPath);
            }
            if (entry.State is "LOCAL_UPLOAD_PENDING_PAYMENT" or "ACK_PENDING" or "ACKNOWLEDGED" or
                "SUBMITTING" or "SPOOLER_ACCEPTED" or "PRINTING" or "OUTPUT_UNCONFIRMED" or "OUTPUT_CONFIRMED")
            {
                return;
            }
            if (entry.State != "LOCAL_UPLOAD_STAGED")
            {
                throw new InvalidOperationException($"Cannot finalise a local upload in state {entry.State}.");
            }

            await SaveManifestAsync(entry with { State = "LOCAL_UPLOAD_PENDING_PAYMENT" }, cancellationToken);
        }
        finally
        {
            stagedUploadLeaseGate.Release();
        }
    }

    public async Task<bool> DeleteExpiredLocalUploadAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await stagedUploadLeaseGate.WaitAsync(cancellationToken);
        try
        {
            var entry = Find(jobId);
            if (entry is null) return false;
            // A worker may finalize the same entry after the sweep took its
            // snapshot. Treat that stale cleanup request as a no-op.
            if (entry.State != "LOCAL_UPLOAD_STAGED") return false;
            // Re-read the current lease while holding the same gate as renewal.
            // A stale expiry-sweep snapshot must never delete a renewed document.
            if (entry.LeaseExpiresUtc > now) return false;

            var queueRootFull = Path.GetFullPath(queueRoot);
            var queuePrefix = queueRootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var documentPath = Path.GetFullPath(entry.DocumentPath);
            if (!documentPath.StartsWith(queuePrefix, comparison))
                throw new InvalidDataException("The staged local upload document path is outside the protected queue.");

            var manifestPath = GetManifestPath(jobId);
            var photoSourcePath = Path.GetFullPath(entry.PhotoSourcePath ?? entry.DocumentPath + ".photo.source");
            if (!photoSourcePath.StartsWith(queuePrefix, comparison))
                throw new InvalidDataException("The retained photo source path is outside the protected queue.");
            if (File.Exists(documentPath)) File.Delete(documentPath);
            if (File.Exists(photoSourcePath)) File.Delete(photoSourcePath);
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
            return true;
        }
        finally
        {
            stagedUploadLeaseGate.Release();
        }
    }

    public async Task DeleteExpiredPendingPaymentUploadAsync(
        Guid jobId,
        DateTimeOffset deleteAfterUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deleteAfterUtc > now)
            throw new InvalidOperationException("The platform retention deadline has not been reached.");

        var entry = Find(jobId);
        if (entry is null) return;
        if (entry.State != "LOCAL_UPLOAD_PENDING_PAYMENT")
            throw new InvalidOperationException("Only a server-approved pending-payment local upload can be removed.");

        var queueRootFull = Path.GetFullPath(queueRoot);
        var queuePrefix = queueRootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var documentPath = Path.GetFullPath(entry.DocumentPath);
        if (!documentPath.StartsWith(queuePrefix, comparison))
            throw new InvalidDataException("The local upload document path is outside the protected queue.");

        var manifestPath = GetManifestPath(jobId);
        var photoSourcePath = Path.GetFullPath(entry.PhotoSourcePath ?? entry.DocumentPath + ".photo.source");
        if (!photoSourcePath.StartsWith(queuePrefix, comparison))
            throw new InvalidDataException("The retained photo source path is outside the protected queue.");
        if (File.Exists(documentPath)) File.Delete(documentPath);
        if (File.Exists(photoSourcePath)) File.Delete(photoSourcePath);
        if (File.Exists(manifestPath)) File.Delete(manifestPath);
        await Task.CompletedTask;
    }

    public string EnsurePrivateTestOutputDirectory()
    {
        Directory.CreateDirectory(queueRoot);
        if (applyWindowsAcl) SecureDirectory(queueRoot);
        var directory = Path.Combine(queueRoot, "PrivateTestExports");
        Directory.CreateDirectory(directory);
        if (applyWindowsAcl) SecureDirectory(directory);
        return Path.GetFullPath(directory);
    }

    public Task<int> PruneRetainedEntriesAsync(
        DateTimeOffset now,
        TimeSpan completedRetention,
        string pdfExportDirectory,
        CancellationToken cancellationToken) =>
        PruneRetainedEntriesAsync(now, completedRetention, pdfExportDirectory, null, cancellationToken);

    public async Task<int> PruneRetainedEntriesAsync(
        DateTimeOffset now,
        TimeSpan completedRetention,
        string pdfExportDirectory,
        string? privateTestOutputDirectory,
        CancellationToken cancellationToken)
    {
        if (completedRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(completedRetention));
        if (string.IsNullOrWhiteSpace(pdfExportDirectory) || !Path.IsPathFullyQualified(pdfExportDirectory))
            throw new ArgumentException("A fully qualified PDF export directory is required.", nameof(pdfExportDirectory));

        var queueRootFull = Path.GetFullPath(queueRoot);
        var queuePrefix = queueRootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var outputRootFull = Path.GetFullPath(pdfExportDirectory);
        var outputPrefix = outputRootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var cutoff = now - completedRetention;
        var privateTestCutoff = now - TimeSpan.FromHours(24);
        var privateTestPrefix = string.IsNullOrWhiteSpace(privateTestOutputDirectory)
            ? null
            : Path.GetFullPath(privateTestOutputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var removed = 0;

        foreach (var entry in ReadEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.PrivatePrinting &&
                entry.OutputMode == PrinterOutputModeRules.PdfExport &&
                entry.CompletedUtc is DateTimeOffset privateTestCompletedUtc &&
                privateTestCompletedUtc <= privateTestCutoff &&
                !entry.PrivatePurgePending &&
                string.IsNullOrWhiteSpace(entry.DocumentPath))
            {
                // Test output is a local sensitive artifact, not a customer PDF.
                // Keep it for 24 hours after the private source purge is confirmed,
                // then securely erase it and its sanitized reconciliation manifest.
                if (privateTestPrefix is null) continue;
                if (!string.IsNullOrWhiteSpace(entry.OutputFilePath))
                {
                    var outputPath = Path.GetFullPath(entry.OutputFilePath);
                    if (!outputPath.StartsWith(privateTestPrefix, comparison)) continue;
                    await EraseProtectedQueueFileAsync(outputPath, queuePrefix, comparison, cancellationToken);
                }
                var privateManifestPath = GetManifestPath(entry.JobId);
                if (File.Exists(privateManifestPath)) File.Delete(privateManifestPath);
                removed++;
                continue;
            }

            if (entry.State == "LOCAL_UPLOAD_STAGED" && entry.LeaseExpiresUtc <= now)
            {
                if (await DeleteExpiredLocalUploadAsync(entry.JobId, now, cancellationToken))
                    removed++;
                continue;
            }

            if (entry.State is not ("OUTPUT_CONFIRMED" or "PDF_EXPORT_REPORTED") ||
                entry.CompletedUtc is not DateTimeOffset completedUtc ||
                completedUtc > cutoff)
                continue;

            // Private Printing keeps a sanitized completion manifest for job
            // reconciliation after its source document has already been erased.
            if (string.IsNullOrWhiteSpace(entry.DocumentPath))
                continue;

            var documentPath = Path.GetFullPath(entry.DocumentPath);
            if (!documentPath.StartsWith(queuePrefix, comparison))
                continue;

            if (!string.IsNullOrWhiteSpace(entry.OutputFilePath))
            {
                var outputPath = Path.GetFullPath(entry.OutputFilePath);
                if (!outputPath.StartsWith(outputPrefix, comparison))
                    continue;
                if (File.Exists(outputPath)) File.Delete(outputPath);
            }

            var photoSourcePath = Path.GetFullPath(entry.PhotoSourcePath ?? entry.DocumentPath + ".photo.source");
            if (!photoSourcePath.StartsWith(queuePrefix, comparison))
                continue;
            var manifestPath = GetManifestPath(entry.JobId);
            if (File.Exists(documentPath)) File.Delete(documentPath);
            if (File.Exists(photoSourcePath)) File.Delete(photoSourcePath);
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
            removed++;
        }

        return removed;
    }

    public async Task UpdateFromClaimAsync(AgentClaimedJob job, CancellationToken cancellationToken)
    {
        var entry = Find(job.Id)
            ?? throw new FileNotFoundException("The pre-staged local upload is missing from the protected queue.");
        if (!string.Equals(entry.Sha256, job.Sha256, StringComparison.OrdinalIgnoreCase) ||
            entry.SizeBytes != job.SizeBytes ||
            job.PrinterDeviceId is null ||
            string.IsNullOrWhiteSpace(job.PrinterName) ||
            string.IsNullOrWhiteSpace(job.QueueName))
        {
            throw new InvalidDataException("The paid job no longer matches the local upload or has an invalid printer assignment.");
        }
        if (entry.State is not ("LOCAL_UPLOAD_PENDING_PAYMENT" or "LOCAL_UPLOAD_STAGED" or "ACK_PENDING" or "ACKNOWLEDGED"))
        {
            throw new InvalidOperationException($"Cannot update print options for a local upload in state {entry.State}.");
        }
        if (!File.Exists(entry.DocumentPath))
        {
            throw new FileNotFoundException("The pre-staged local upload is missing its protected document.", entry.DocumentPath);
        }

        await SaveManifestAsync(entry with
        {
            State = "ACK_PENDING",
            PrinterDeviceId = job.PrinterDeviceId,
            PrinterName = job.PrinterName,
            QueueName = job.QueueName,
            PageCount = job.PageCount,
            Copies = job.Copies,
            ColourMode = job.ColourMode,
            PaperSize = job.PaperSize,
            IsDuplex = job.IsDuplex,
            Orientation = job.Orientation,
            Currency = job.Currency,
            QuotedAmountMinor = job.QuotedAmountMinor,
            LeaseExpiresUtc = job.LeaseExpiresUtc,
            OutputMode = job.OutputMode,
            PdfBrandingEnabled = job.PdfBrandingEnabled,
            PrivatePrinting = job.PrivatePrinting,
            PrintKind = job.PrintKind
        }, cancellationToken);
    }

    public async Task MarkAcknowledgedAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var entry = Find(jobId);
        if (entry is null)
        {
            throw new InvalidOperationException("Cannot acknowledge a job that is not stored in the local queue.");
        }

        if (entry.State == "ACKNOWLEDGED")
        {
            return;
        }
        if (entry.State is not ("ACK_PENDING" or "LOCAL_UPLOAD_STAGED"))
        {
            throw new InvalidOperationException($"Cannot acknowledge a local queue item in state {entry.State}.");
        }

        await SaveManifestAsync(entry with { State = "ACKNOWLEDGED" }, cancellationToken);
    }


    public async Task UpdateRejectedPagesAsync(Guid jobId, IReadOnlyList<int> rejectedPageNumbers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rejectedPageNumbers);
        var entry = Find(jobId) ?? throw new FileNotFoundException("The local print job does not exist.");
        var pages = rejectedPageNumbers.Distinct().Order().ToArray();
        if (pages.Length != rejectedPageNumbers.Count || pages.Any(page => page < 1 || page > entry.PageCount))
            throw new InvalidDataException("The server returned an invalid rejected-page selection.");
        await SaveManifestAsync(entry with { RejectedPageNumbers = pages }, ct);
    }

    public async Task MarkSubmissionStartedAsync(Guid jobId, string spoolerJobName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spoolerJobName) || spoolerJobName.Length > 180)
            throw new ArgumentException("A stable Windows spooler document name is required.", nameof(spoolerJobName));
        var entry = Find(jobId) ?? throw new FileNotFoundException("The local print job does not exist.");
        if (entry.State == "SUBMITTING" && entry.SpoolerJobName == spoolerJobName) return;
        if (entry.State != "ACKNOWLEDGED")
            throw new InvalidOperationException($"Cannot submit a local job in state {entry.State}.");
        await SaveManifestAsync(entry with
        {
            State = "SUBMITTING", SpoolerJobName = spoolerJobName, SpoolerJobId = null,
            SpoolerSubmittedUtc = null, SpoolerStatus = "SUBMISSION_STARTED", FailureCode = null
        }, ct);
    }

    public async Task MarkPdfExportStartedAsync(Guid jobId, string outputPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outputPath) || outputPath.Length > 1024 || !Path.IsPathFullyQualified(outputPath))
            throw new ArgumentException("A fully qualified PDF export destination is required.", nameof(outputPath));
        var entry = Find(jobId) ?? throw new FileNotFoundException("The local print job does not exist.");
        if (entry.State == "PDF_EXPORTING" &&
            string.Equals(entry.OutputFilePath, outputPath, StringComparison.OrdinalIgnoreCase))
            return;
        if (entry.State is not ("ACKNOWLEDGED" or "OUTPUT_UNCONFIRMED"))
            throw new InvalidOperationException($"Cannot export a local job in state {entry.State}.");
        await SaveManifestAsync(entry with
        {
            State = "PDF_EXPORTING",
            OutputMode = PrinterOutputModeRules.PdfExport,
            OutputFilePath = outputPath,
            OutputFileName = Path.GetFileName(outputPath),
            OutputFileSha256 = null,
            OutputFileSizeBytes = null,
            FailureCode = null,
            SpoolerJobId = null,
            SpoolerJobName = null,
            SpoolerSubmittedUtc = null,
            SpoolerStatus = null
        }, ct);
    }

    public async Task MarkPdfExportedAsync(Guid jobId, PdfExportReceipt receipt, CancellationToken ct)
    {
        var entry = Find(jobId) ?? throw new FileNotFoundException("The local print job does not exist.");
        if ((entry.State is "PDF_EXPORTED" or "PDF_EXPORT_REPORTED") &&
            string.Equals(entry.OutputFileSha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase))
            return;
        if (entry.State is not ("PDF_EXPORTING" or "OUTPUT_UNCONFIRMED"))
            throw new InvalidOperationException($"Cannot complete a PDF export from state {entry.State}.");
        await SaveManifestAsync(entry with
        {
            State = "PDF_EXPORTED",
            OutputMode = PrinterOutputModeRules.PdfExport,
            OutputFilePath = receipt.OutputPath,
            OutputFileName = receipt.FileName,
            OutputFileSha256 = receipt.Sha256,
            OutputFileSizeBytes = receipt.SizeBytes,
            FailureCode = null
        }, ct);
    }

    public async Task MarkPdfExportReceiptReportedAsync(Guid jobId, CancellationToken ct)
    {
        var entry = Find(jobId) ?? throw new FileNotFoundException("The local PDF export manifest does not exist.");
        if (entry.State == "PDF_EXPORT_REPORTED")
        {
            if (entry.CompletedUtc is null)
                await SaveManifestAsync(entry with { CompletedUtc = DateTimeOffset.UtcNow }, ct);
            return;
        }
        if (entry.State != "PDF_EXPORTED")
            throw new InvalidOperationException($"Cannot acknowledge a PDF export receipt from state {entry.State}.");
        await SaveManifestAsync(entry with { State = "PDF_EXPORT_REPORTED", CompletedUtc = DateTimeOffset.UtcNow }, ct);
    }

    public async Task MarkPdfExportReadyToRetryAsync(Guid jobId, CancellationToken ct)
    {
        var entry = Find(jobId) ?? throw new FileNotFoundException("The local print job does not exist.");
        if (entry.State is not ("PDF_EXPORTING" or "OUTPUT_UNCONFIRMED"))
            throw new InvalidOperationException($"Cannot recover a PDF export from state {entry.State}.");
        if (!string.IsNullOrWhiteSpace(entry.OutputFilePath) && File.Exists(entry.OutputFilePath))
            throw new InvalidOperationException("The PDF export file exists; it must be reconciled, not retried.");
        await SaveManifestAsync(entry with
        {
            State = "ACKNOWLEDGED",
            OutputMode = PrinterOutputModeRules.PdfExport,
            OutputFilePath = null,
            OutputFileName = null,
            OutputFileSha256 = null,
            OutputFileSizeBytes = null,
            FailureCode = null,
            SpoolerStatus = null
        }, ct);
    }

    public async Task MarkSpoolerAcceptedAsync(Guid jobId, int spoolerJobId, string spoolerJobName, CancellationToken ct)
    {
        if (spoolerJobId <= 0) throw new ArgumentOutOfRangeException(nameof(spoolerJobId));
        var entry = Find(jobId) ?? throw new FileNotFoundException("The local print job does not exist.");
        if (entry.State == "SPOOLER_ACCEPTED" && entry.SpoolerJobId == spoolerJobId) return;
        if (entry.State != "SUBMITTING")
            throw new InvalidOperationException($"Cannot mark a local job spooler accepted from state {entry.State}.");
        await SaveManifestAsync(entry with
        {
            State = "SPOOLER_ACCEPTED", SpoolerJobId = spoolerJobId,
            SpoolerJobName = spoolerJobName, SpoolerSubmittedUtc = DateTimeOffset.UtcNow,
            SpoolerStatus = "QUEUED", FailureCode = null
        }, ct);
    }

    public async Task MarkReadyAfterBlockedAsync(Guid jobId, string reasonCode, CancellationToken ct)
    {
        var entry = Find(jobId) ?? throw new FileNotFoundException("The local print job does not exist.");
        if (entry.State == "ACKNOWLEDGED") return;
        if (entry.State is not ("SUBMITTING" or "PDF_EXPORTING"))
            throw new InvalidOperationException($"Cannot safely unblock a local job from state {entry.State}.");
        await SaveManifestAsync(entry with
        {
            State = "ACKNOWLEDGED", SpoolerJobId = null, SpoolerJobName = null,
            SpoolerSubmittedUtc = null, SpoolerStatus = "BLOCKED_BEFORE_SUBMISSION",
            OutputFilePath = null, OutputFileName = null, OutputFileSha256 = null, OutputFileSizeBytes = null,
            FailureCode = reasonCode
        }, ct);
    }

    public async Task MarkOutputUnconfirmedAsync(
        Guid jobId,
        string reasonCode,
        CancellationToken ct,
        string? outputMode = null)
    {
        var entry = Find(jobId) ?? throw new FileNotFoundException("The local print job does not exist.");
        if (entry.State == "OUTPUT_UNCONFIRMED" &&
            (outputMode is null || entry.OutputMode == outputMode))
            return;
        if (entry.State is not ("SUBMITTING" or "SPOOLER_ACCEPTED" or "PDF_EXPORTING" or "OUTPUT_UNCONFIRMED"))
            throw new InvalidOperationException($"Cannot reconcile a local job from state {entry.State}.");
        await SaveManifestAsync(entry with
        {
            State = "OUTPUT_UNCONFIRMED",
            OutputMode = outputMode ?? entry.OutputMode,
            SpoolerStatus = "OUTPUT_UNCONFIRMED",
            FailureCode = reasonCode
        }, ct);
    }

    public async Task MarkOutputConfirmedAsync(Guid jobId, CancellationToken ct)
    {
        var entry = Find(jobId) ?? throw new FileNotFoundException("The local print job does not exist.");
        if (entry.State == "OUTPUT_CONFIRMED")
        {
            if (entry.CompletedUtc is null)
                await SaveManifestAsync(entry with { CompletedUtc = DateTimeOffset.UtcNow }, ct);
            return;
        }
        if (entry.State is not ("SPOOLER_ACCEPTED" or "PRINTING" or "OUTPUT_UNCONFIRMED"))
            throw new InvalidOperationException($"Cannot confirm local output from state {entry.State}.");
        await SaveManifestAsync(entry with
        {
            State = entry.PrivatePrinting ? "PRIVATE_PURGE_PENDING" : "OUTPUT_CONFIRMED",
            SpoolerStatus = "SHOP_CONFIRMED_OUTPUT",
            CompletedUtc = DateTimeOffset.UtcNow
        }, ct);
        if (entry.PrivatePrinting)
            await PurgePrivateSourceAfterConfirmedOutputAsync(jobId, ct);
    }

    public async Task PurgePrivateSourceAfterConfirmedOutputAsync(Guid jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await stagedUploadLeaseGate.WaitAsync(ct);
        try
        {
            var entry = Find(jobId) ?? throw new FileNotFoundException("The private print manifest is unavailable.");
            if (!entry.PrivatePrinting ||
                entry.State is not ("PRIVATE_PURGE_PENDING" or "OUTPUT_CONFIRMED" or "PDF_EXPORT_REPORTED" or "TEST_OUTPUT_SOURCE_PURGED") ||
                entry.CompletedUtc is null)
                throw new InvalidOperationException("A private source can be purged only after verified physical output or a separately recorded test output.");

            var queueRootFull = Path.GetFullPath(queueRoot);
            var queuePrefix = queueRootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.IsNullOrWhiteSpace(entry.DocumentPath))
            {
                var documentPath = Path.GetFullPath(entry.DocumentPath);
                if (!documentPath.StartsWith(queuePrefix, comparison))
                    throw new InvalidDataException("The private document path is outside the protected queue.");
                await EraseProtectedQueueFileAsync(documentPath, queuePrefix, comparison, ct);
            }

            // The deterministic sidecar name lets a purge retry remove a
            // leftover source even when an older/partially sanitized manifest
            // has already lost both DocumentPath and PhotoSourcePath.
            var photoSourcePath = entry.PhotoSourcePath;
            if (string.IsNullOrWhiteSpace(photoSourcePath))
                photoSourcePath = !string.IsNullOrWhiteSpace(entry.DocumentPath)
                    ? entry.DocumentPath + ".photo.source"
                    : Path.Combine(queueRootFull, jobId.ToString("N") + ".qdoc.photo.source");
            await EraseProtectedQueueFileAsync(photoSourcePath, queuePrefix, comparison, ct);

            await SaveManifestAsync(entry with
            {
                State = entry.OutputMode == PrinterOutputModeRules.PdfExport &&
                    entry.State is ("PDF_EXPORT_REPORTED" or "TEST_OUTPUT_SOURCE_PURGED")
                    ? "TEST_OUTPUT_SOURCE_PURGED"
                    : "OUTPUT_CONFIRMED",
                DocumentPath = string.Empty,
                DocumentName = "Private document",
                MediaType = string.Empty,
                Sha256 = string.Empty,
                SizeBytes = 0,
                PhotoSourcePath = null,
                PhotoSourceDocumentName = null,
                PhotoSourceMediaType = null,
                PhotoSourceSha256 = null,
                PhotoSourceSizeBytes = null,
                PrivatePurgePending = true
            }, ct);
        }
        finally
        {
            stagedUploadLeaseGate.Release();
        }
    }

    public async Task MarkPrivatePurgeAcknowledgedAsync(Guid jobId, CancellationToken ct)
    {
        var entry = Find(jobId) ?? throw new FileNotFoundException("The private print manifest is unavailable.");
        if (!entry.PrivatePrinting ||
            entry.State is not ("OUTPUT_CONFIRMED" or "TEST_OUTPUT_SOURCE_PURGED") ||
            !entry.PrivatePurgePending ||
            !string.IsNullOrWhiteSpace(entry.DocumentPath) || entry.SizeBytes != 0 || !string.IsNullOrEmpty(entry.Sha256) ||
            !string.IsNullOrWhiteSpace(entry.PhotoSourcePath) ||
            entry.PhotoSourceDocumentName is not null || entry.PhotoSourceMediaType is not null ||
            entry.PhotoSourceSha256 is not null || entry.PhotoSourceSizeBytes is not null)
            throw new InvalidOperationException("The private document and any retained photo source must be removed before purge acknowledgement.");
        await SaveManifestAsync(entry with { PrivatePurgePending = false }, ct);
    }

    public async Task UpdateSpoolerStatusAsync(Guid jobId, string status, CancellationToken ct)
    {
        var entry = Find(jobId) ?? throw new FileNotFoundException("The local print job does not exist.");
        if (entry.State != "SPOOLER_ACCEPTED") return;
        await SaveManifestAsync(entry with { SpoolerStatus = status }, ct);
    }

    public async Task ReplaceStagedPhotoDocumentAsync(
        Guid jobId,
        string expectedOriginalSha256,
        long expectedOriginalSizeBytes,
        string replacementDocumentName,
        ReadOnlyMemory<byte> replacementBytes,
        int pageCount,
        long quotedAmountMinor,
        Func<Task> commitServerRevisionAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commitServerRevisionAsync);
        if (string.IsNullOrWhiteSpace(expectedOriginalSha256) ||
            expectedOriginalSizeBytes <= 0 ||
            string.IsNullOrWhiteSpace(replacementDocumentName) ||
            replacementDocumentName.Length > 255 ||
            !string.Equals(Path.GetExtension(replacementDocumentName), ".pdf", StringComparison.OrdinalIgnoreCase) ||
            replacementBytes.Length <= 0 || replacementBytes.Length > UploadLimits.MaxFileBytes ||
            pageCount is < 1 or > 100 ||
            quotedAmountMinor <= 0)
            throw new InvalidDataException("The prepared local photo revision metadata is invalid.");

        await stagedUploadLeaseGate.WaitAsync(cancellationToken);
        try
        {
            var entry = Find(jobId)
                ?? throw new FileNotFoundException("The original local photo is missing from the protected queue.");
            if (entry.PrivatePrinting)
                throw new InvalidOperationException("Private Printing photos and editor metadata must remain on the customer's device.");
            if (entry.State is not ("LOCAL_UPLOAD_STAGED" or "LOCAL_UPLOAD_PENDING_PAYMENT"))
                throw new InvalidOperationException("A local photo can only be enhanced before payment and printer handoff.");

            var sourceName = entry.PhotoSourceDocumentName ?? entry.DocumentName;
            var sourceMediaType = entry.PhotoSourceMediaType ?? entry.MediaType;
            var sourceSha256 = entry.PhotoSourceSha256 ?? entry.Sha256;
            var sourceSizeBytes = entry.PhotoSourceSizeBytes ?? entry.SizeBytes;
            if (sourceMediaType is not ("image/jpeg" or "image/png") ||
                !string.Equals(sourceSha256, expectedOriginalSha256, StringComparison.OrdinalIgnoreCase) ||
                sourceSizeBytes != expectedOriginalSizeBytes ||
                !File.Exists(entry.DocumentPath))
                throw new InvalidDataException("The local original photo changed or is no longer available. Reopen its preview before saving.");

            var queueRootFull = Path.GetFullPath(queueRoot);
            var queuePrefix = queueRootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var documentPath = Path.GetFullPath(entry.DocumentPath);
            if (!documentPath.StartsWith(queuePrefix, comparison))
                throw new InvalidDataException("The local photo document path is outside the protected queue.");

            var retainedSourcePath = entry.PhotoSourcePath ??
                entry.DocumentPath + ".photo.source";
            retainedSourcePath = Path.GetFullPath(retainedSourcePath);
            if (!retainedSourcePath.StartsWith(queuePrefix, comparison))
                throw new InvalidDataException("The retained photo source path is outside the protected queue.");

            var alreadyRetained = !string.IsNullOrWhiteSpace(entry.PhotoSourceSha256);
            var sourceMoved = false;
            if (alreadyRetained)
            {
                if (entry.PhotoSourceSizeBytes is not > 0 ||
                    string.IsNullOrWhiteSpace(entry.PhotoSourceMediaType) ||
                    string.IsNullOrWhiteSpace(entry.PhotoSourceDocumentName) ||
                    !File.Exists(retainedSourcePath))
                    throw new InvalidDataException("The retained original photo is incomplete. Do not overwrite it; contact support.");
                var verifiedSource = ReadPhotoSource(jobId);
                try
                {
                    if (verifiedSource.LongLength != expectedOriginalSizeBytes ||
                        !string.Equals(
                            Convert.ToHexString(SHA256.HashData(verifiedSource)).ToLowerInvariant(),
                            expectedOriginalSha256,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The retained original photo failed its integrity check.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(verifiedSource);
                }
            }
            else
            {
                // On the first edit, the manifest still describes the original file,
                // but verify the protected bytes before moving them into the retained
                // source sidecar. A stale or corrupted queue file must never become the
                // supposedly immutable source for future edits.
                byte[] verifiedOriginal;
                try
                {
                    verifiedOriginal = ReadDocument(jobId);
                }
                catch (Exception exception) when (exception is IOException or CryptographicException or InvalidDataException)
                {
                    throw new InvalidDataException(
                        "The local original photo failed its integrity check. Reopen its preview before saving.",
                        exception);
                }

                CryptographicOperations.ZeroMemory(verifiedOriginal);
            }

            var newHash = Convert.ToHexString(SHA256.HashData(replacementBytes.Span)).ToLowerInvariant();
            var plaintext = replacementBytes.ToArray();
            byte[] protectedBytes;
            try
            {
                protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            var temporaryPath = entry.DocumentPath + ".tmp";
            var backupPath = entry.DocumentPath + ".photo-revision.bak";
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);
            try
            {
                try
                {
                    if (!alreadyRetained)
                    {
                        // Move only after the replacement is safely encrypted in
                        // memory. If encryption fails, the original remains at its
                        // normal queue path and no orphan sidecar is left behind.
                        if (File.Exists(retainedSourcePath))
                            throw new InvalidDataException("An unexpected retained photo source already exists.");
                        File.Move(documentPath, retainedSourcePath);
                        sourceMoved = true;
                    }
                    await using (var stream = new FileStream(
                        temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await stream.WriteAsync(protectedBytes, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                    }
                    if (sourceMoved)
                        File.Move(temporaryPath, entry.DocumentPath);
                    else
                        File.Replace(temporaryPath, entry.DocumentPath, backupPath, ignoreMetadataErrors: true);
                }
                catch
                {
                    if (sourceMoved && File.Exists(retainedSourcePath) && !File.Exists(documentPath))
                        File.Move(retainedSourcePath, documentPath);
                    throw;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }

                var replacementEntry = entry with
                {
                    DocumentName = replacementDocumentName,
                    MediaType = "application/pdf",
                    Sha256 = newHash,
                    SizeBytes = replacementBytes.Length,
                    PageCount = pageCount,
                    Copies = 1,
                    ColourMode = "Colour",
                    PaperSize = "A4",
                    IsDuplex = false,
                    Orientation = "AUTO",
                    QuotedAmountMinor = quotedAmountMinor,
                    PhotoSourcePath = retainedSourcePath,
                    PhotoSourceDocumentName = sourceName,
                    PhotoSourceMediaType = sourceMediaType,
                    PhotoSourceSha256 = sourceSha256,
                    PhotoSourceSizeBytes = sourceSizeBytes
                };

                try
                {
                    await SaveManifestAsync(replacementEntry, cancellationToken);
                    await commitServerRevisionAsync();
                }
                catch
                {
                    if (File.Exists(backupPath))
                    {
                        if (File.Exists(entry.DocumentPath)) File.Delete(entry.DocumentPath);
                        File.Move(backupPath, entry.DocumentPath);
                    }
                    else if (sourceMoved && File.Exists(retainedSourcePath))
                    {
                        if (File.Exists(entry.DocumentPath)) File.Delete(entry.DocumentPath);
                        File.Move(retainedSourcePath, entry.DocumentPath);
                    }
                    await SaveManifestAsync(entry, CancellationToken.None);
                    throw;
                }

                // The server and manifest now point to the replacement. Cleanup
                // failure must not roll back the file after the server has committed.
                try
                {
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                }
                catch (IOException)
                {
                    // Keep the encrypted backup for a later maintenance cleanup.
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep the encrypted backup for a later maintenance cleanup.
                }
            }
            catch
            {
                // A first-edit failure restores the source to its original queue
                // path in the inner rollback. Do not delete a retained original.
                throw;
            }
        }
        finally
        {
            stagedUploadLeaseGate.Release();
        }
    }

    public byte[] ReadPhotoSource(Guid jobId)
    {
        var entry = Find(jobId)
            ?? throw new FileNotFoundException("The print job is not present in the local queue.");
        if (string.IsNullOrWhiteSpace(entry.PhotoSourceSha256))
        {
            if (entry.MediaType is not ("image/jpeg" or "image/png"))
                throw new FileNotFoundException("This job has no retained original photo source.");
            return ReadDocument(jobId);
        }

        if (entry.PhotoSourceSizeBytes is not > 0 ||
            entry.PhotoSourceMediaType is not ("image/jpeg" or "image/png"))
            throw new InvalidDataException("The retained original photo metadata is invalid.");

        var sourcePath = entry.PhotoSourcePath ?? entry.DocumentPath + ".photo.source";
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var queueRootFull = Path.GetFullPath(queueRoot);
        var queuePrefix = queueRootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullSourcePath.StartsWith(queuePrefix, comparison))
            throw new InvalidDataException("The retained original photo path is outside the protected queue.");

        var protectedBytes = File.ReadAllBytes(fullSourcePath);
        try
        {
            var plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            var actualHash = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
            if (plaintext.LongLength != entry.PhotoSourceSizeBytes.Value ||
                !string.Equals(actualHash, entry.PhotoSourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new InvalidDataException("The retained original photo failed its integrity check.");
            }
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public byte[] ReadDocument(Guid jobId)
    {
        var entry = Find(jobId)
            ?? throw new FileNotFoundException("The print job is not present in the local queue.");
        var protectedBytes = File.ReadAllBytes(entry.DocumentPath);
        try
        {
            var plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            var actualHash = Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant();
            if (plaintext.LongLength != entry.SizeBytes ||
                !string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new InvalidDataException("The protected local document failed its integrity check.");
            }

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private async Task SaveManifestAsync(AgentLocalQueueEntry entry, CancellationToken cancellationToken)
    {
        var manifestPath = GetManifestPath(entry.JobId);
        var temporaryPath = manifestPath + ".tmp";
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            await writer.WriteAsync(json.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        if (File.Exists(manifestPath))
        {
            File.Replace(temporaryPath, manifestPath, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, manifestPath);
        }
    }

    private string GetManifestPath(Guid jobId) =>
        Path.Combine(queueRoot, jobId.ToString("N") + ".json");

    private static void SecureDirectory(string directory)
    {
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(
            systemSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administratorsSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
}
