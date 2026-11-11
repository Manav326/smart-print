using System.Security.Cryptography;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentLocalQueueStoreTests
{
    [Fact]
    public async Task PersistsTheDocumentEncryptedAndAcknowledgesIdempotently()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var content = "Private print document bytes"u8.ToArray();
            var job = CreateJob(content);

            var entry = await store.EnqueueAsync(job, content, CancellationToken.None);
            var encryptedOnDisk = await File.ReadAllBytesAsync(entry.DocumentPath);

            Assert.False(content.AsSpan().SequenceEqual(encryptedOnDisk));
            Assert.Equal("ACK_PENDING", entry.State);
            Assert.True(content.AsSpan().SequenceEqual(store.ReadDocument(job.Id)));
            Assert.Single(store.GetPendingAcknowledgements());

            await store.MarkAcknowledgedAsync(job.Id, CancellationToken.None);
            Assert.Equal("ACKNOWLEDGED", store.Find(job.Id)!.State);
            Assert.Empty(store.GetPendingAcknowledgements());

            var duplicate = await store.EnqueueAsync(job, content, CancellationToken.None);
            Assert.Equal(job.Id, duplicate.JobId);
            Assert.Equal("ACKNOWLEDGED", duplicate.State);
            Assert.Single(Directory.GetFiles(root, "*.qdoc"));
            Assert.Single(Directory.GetFiles(root, "*.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalUploadIsStagedUntilPaidJobClaimUpdatesItsPrintSettings()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Local-only customer document"u8.ToArray();
            var upload = CreateJob(bytes) with
            {
                Status = "LOCAL_UPLOAD_STAGED",
                StorageMode = "LOCAL_AGENT",
                Copies = 1,
                ColourMode = "Mono",
                PaperSize = "A3",
                IsDuplex = false,
                Orientation = "LANDSCAPE"
            };

            var staged = await store.EnqueueLocalUploadAsync(upload, bytes, CancellationToken.None);

            Assert.Equal("LOCAL_UPLOAD_STAGED", staged.State);
            Assert.Equal("A3", staged.PaperSize);
            Assert.Equal("LANDSCAPE", staged.Orientation);
            Assert.Empty(store.GetAcknowledgedJobs());
            Assert.Empty(store.GetPendingAcknowledgements());
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(upload.Id)));

            var paidClaim = upload with
            {
                Status = "LOCAL_QUEUE_CLAIMED",
                Copies = 3,
                ColourMode = "Colour",
                PaperSize = "A5",
                IsDuplex = true,
                Orientation = "PORTRAIT",
                QuotedAmountMinor = 900,
                LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(3)
            };
            await store.UpdateFromClaimAsync(paidClaim, CancellationToken.None);

            var refreshed = store.Find(upload.Id)!;
            Assert.Equal("ACK_PENDING", refreshed.State);
            Assert.Single(store.GetPendingAcknowledgements());
            Assert.Equal(3, refreshed.Copies);
            Assert.Equal("Colour", refreshed.ColourMode);
            Assert.Equal("A5", refreshed.PaperSize);
            Assert.Equal("PORTRAIT", refreshed.Orientation);
            Assert.True(refreshed.IsDuplex);
            Assert.Equal(900, refreshed.QuotedAmountMinor);
            Assert.Empty(store.GetAcknowledgedJobs());

            await store.MarkAcknowledgedAsync(upload.Id, CancellationToken.None);
            Assert.Equal("ACKNOWLEDGED", store.Find(upload.Id)!.State);
            Assert.Single(store.GetAcknowledgedJobs());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RenewedLocalUploadLeaseKeepsTheOriginalStagedDocumentUntilRetryCompletes()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Original staged upload reused after LAN lease renewal"u8.ToArray();
            var expiredLease = DateTimeOffset.UtcNow.AddMinutes(-1);
            var job = CreateJob(bytes) with
            {
                Status = "LOCAL_UPLOAD_STAGED",
                StorageMode = "LOCAL_AGENT",
                LeaseExpiresUtc = expiredLease
            };
            await store.EnqueueLocalUploadAsync(job, bytes, CancellationToken.None);

            var renewedLease = DateTimeOffset.UtcNow.AddMinutes(15);
            await store.RefreshStagedLocalUploadLeaseAsync(
                job.Id, job.Sha256, job.SizeBytes, renewedLease, CancellationToken.None);

            // Simulate the background sweep acting on its earlier expired snapshot.
            // The store must re-read the manifest and leave the renewed file intact.
            Assert.False(await store.DeleteExpiredLocalUploadAsync(
                job.Id, expiredLease.AddSeconds(5), CancellationToken.None));

            var refreshed = store.Find(job.Id)!;
            Assert.Equal(renewedLease, refreshed.LeaseExpiresUtc);
            Assert.Equal("LOCAL_UPLOAD_STAGED", refreshed.State);
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(job.Id)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReassignedCloudJobRefreshesItsAcknowledgedQueueTargetWithoutRedownloading()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Cloud-backed job already staged on this Agent"u8.ToArray();
            var original = CreateJob(bytes) with
            {
                StorageMode = "CLOUD",
                PaperSize = "A4",
                Orientation = "AUTO"
            };

            var staged = await store.EnqueueAsync(original, bytes, CancellationToken.None);
            await store.MarkAcknowledgedAsync(original.Id, CancellationToken.None);
            Assert.Equal("ACKNOWLEDGED", store.Find(original.Id)!.State);

            var reassigned = original with
            {
                PrinterDeviceId = Guid.NewGuid(),
                PrinterName = "Alternate printer on this shop computer",
                QueueName = "Alternate Windows Queue",
                Copies = 4,
                PaperSize = "A3",
                Orientation = "LANDSCAPE",
                LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(3)
            };
            await store.UpdateFromClaimAsync(reassigned, CancellationToken.None);

            var refreshed = store.Find(original.Id)!;
            Assert.Equal("ACK_PENDING", refreshed.State);
            Assert.Equal(reassigned.PrinterDeviceId, refreshed.PrinterDeviceId);
            Assert.Equal("Alternate printer on this shop computer", refreshed.PrinterName);
            Assert.Equal("Alternate Windows Queue", refreshed.QueueName);
            Assert.Equal(4, refreshed.Copies);
            Assert.Equal("A3", refreshed.PaperSize);
            Assert.Equal("LANDSCAPE", refreshed.Orientation);
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(original.Id)));
            Assert.True(File.Exists(staged.DocumentPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReassignedCloudJobRefreshesAnAcknowledgementPendingQueueTarget()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Cloud-backed document awaiting queue acknowledgement"u8.ToArray();
            var original = CreateJob(bytes) with { StorageMode = "CLOUD" };

            await store.EnqueueAsync(original, bytes, CancellationToken.None);
            Assert.Equal("ACK_PENDING", store.Find(original.Id)!.State);

            var reassigned = original with
            {
                PrinterDeviceId = Guid.NewGuid(),
                PrinterName = "Replacement printer",
                QueueName = "Replacement queue",
                Copies = 5,
                Orientation = "LANDSCAPE",
                LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(3)
            };
            await store.UpdateFromClaimAsync(reassigned, CancellationToken.None);

            var refreshed = store.Find(original.Id)!;
            Assert.Equal("ACK_PENDING", refreshed.State);
            Assert.Equal(reassigned.PrinterDeviceId, refreshed.PrinterDeviceId);
            Assert.Equal("Replacement queue", refreshed.QueueName);
            Assert.Equal(5, refreshed.Copies);
            Assert.Equal("LANDSCAPE", refreshed.Orientation);
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(original.Id)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalUploadCanMoveToAnotherQueueWhenTheServerAuthorisesTheSameComputer()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Printer-bound local document"u8.ToArray();
            var upload = CreateJob(bytes) with { StorageMode = "LOCAL_AGENT" };
            await store.EnqueueLocalUploadAsync(upload, bytes, CancellationToken.None);

            // The API verifies that the new queue belongs to this same Agent before issuing a claim.
            var changedAssignment = upload with
            {
                PrinterDeviceId = Guid.NewGuid(),
                PrinterName = "Alternate shop printer",
                QueueName = "Alternate Windows Queue"
            };

            await store.UpdateFromClaimAsync(changedAssignment, CancellationToken.None);

            var refreshed = store.Find(upload.Id)!;
            Assert.Equal("ACK_PENDING", refreshed.State);
            Assert.Equal(changedAssignment.PrinterDeviceId, refreshed.PrinterDeviceId);
            Assert.Equal("Alternate shop printer", refreshed.PrinterName);
            Assert.Equal("Alternate Windows Queue", refreshed.QueueName);
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(upload.Id)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalUploadClaimRequiresACompletePrinterIdentity()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Printer-bound local document"u8.ToArray();
            var upload = CreateJob(bytes) with { StorageMode = "LOCAL_AGENT" };
            await store.EnqueueLocalUploadAsync(upload, bytes, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.UpdateFromClaimAsync(upload with { PrinterDeviceId = null }, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.UpdateFromClaimAsync(upload with { PrinterName = string.Empty }, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.UpdateFromClaimAsync(upload with { QueueName = null }, CancellationToken.None));

            Assert.Equal("LOCAL_UPLOAD_STAGED", store.Find(upload.Id)!.State);
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(upload.Id)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizedLocalUploadsSurviveTicketExpiryCleanup()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Finalized local document"u8.ToArray();
            var job = CreateJob(bytes) with
            {
                StorageMode = "LOCAL_AGENT",
                LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            await store.EnqueueLocalUploadAsync(job, bytes, CancellationToken.None);

            await store.MarkLocalUploadFinalizedAsync(job.Id, CancellationToken.None);
            Assert.Equal("LOCAL_UPLOAD_PENDING_PAYMENT", store.Find(job.Id)!.State);
            Assert.Empty(store.GetStagedLocalUploads());

            Assert.False(await store.DeleteExpiredLocalUploadAsync(
                job.Id, DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(job.Id)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExpiredLocalUploadCannotDeleteAFileOutsideTheProtectedQueue()
    {
        var root = CreateTemporaryDirectory();
        var outsidePath = Path.Combine(Path.GetDirectoryName(root)!, $"outside-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(outsidePath, "must remain untouched");
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Tampered manifest path"u8.ToArray();
            var job = CreateJob(bytes) with
            {
                StorageMode = "LOCAL_AGENT",
                LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            await store.EnqueueLocalUploadAsync(job, bytes, CancellationToken.None);

            var manifestPath = Directory.GetFiles(root, "*.json").Single();
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(manifestPath));
            Assert.NotNull(manifest);
            manifest["documentPath"] = outsidePath;
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.DeleteExpiredLocalUploadAsync(job.Id, DateTimeOffset.UtcNow, CancellationToken.None));

            Assert.True(File.Exists(outsidePath));
            Assert.NotNull(store.Find(job.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (File.Exists(outsidePath)) File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task DeletesOnlyExpiredUnfinalizedLocalUploads()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Expired local upload"u8.ToArray();
            var job = CreateJob(bytes) with
            {
                StorageMode = "LOCAL_AGENT",
                LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            var entry = await store.EnqueueLocalUploadAsync(job, bytes, CancellationToken.None);

            await store.DeleteExpiredLocalUploadAsync(job.Id, DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.Null(store.Find(job.Id));
            Assert.False(File.Exists(entry.DocumentPath));
            Assert.Empty(Directory.GetFiles(root, "*.json"));
            Assert.Empty(Directory.GetFiles(root, "*.qdoc"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsChangedContentForAnExistingJobId()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var firstContent = "Original bytes"u8.ToArray();
            var firstJob = CreateJob(firstContent);
            await store.EnqueueAsync(firstJob, firstContent, CancellationToken.None);

            var changedContent = "Different bytes"u8.ToArray();
            var changedJob = CreateJob(changedContent) with { Id = firstJob.Id };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.EnqueueAsync(changedJob, changedContent, CancellationToken.None));
            Assert.True(firstContent.AsSpan().SequenceEqual(store.ReadDocument(firstJob.Id)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsAHashMismatchBeforeWritingAnything()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var content = "Untrusted bytes"u8.ToArray();
            var job = CreateJob("Different bytes"u8.ToArray());
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.EnqueueAsync(job, content, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    [Fact]
    public async Task SpoolerSubmissionTransitionsAreDurableAndNotAutomaticallyRequeued()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "One source document"u8.ToArray();
            var job = CreateJob(bytes);
            await store.EnqueueAsync(job, bytes, CancellationToken.None);
            await store.MarkAcknowledgedAsync(job.Id, CancellationToken.None);
            Assert.Single(store.GetAcknowledgedJobs());

            await store.MarkSubmissionStartedAsync(job.Id, "SmartPrint-Test-StableName", CancellationToken.None);
            Assert.Equal("SUBMITTING", store.Find(job.Id)!.State);
            Assert.Single(store.GetInterruptedSubmissions());
            Assert.Empty(store.GetAcknowledgedJobs());

            await store.MarkSpoolerAcceptedAsync(job.Id, 123, "SmartPrint-Test-StableName", CancellationToken.None);
            Assert.Equal("SPOOLER_ACCEPTED", store.Find(job.Id)!.State);
            Assert.Single(store.GetSpoolerTrackingJobs());
            Assert.Empty(store.GetInterruptedSubmissions());

            await store.UpdateSpoolerStatusAsync(job.Id, "PRINTING", CancellationToken.None);
            Assert.Equal("PRINTING", store.Find(job.Id)!.SpoolerStatus);
            await store.MarkOutputUnconfirmedAsync(job.Id, "SPOOLER_JOB_NO_LONGER_VISIBLE", CancellationToken.None);
            Assert.Equal("OUTPUT_UNCONFIRMED", store.Find(job.Id)!.State);
            Assert.Empty(store.GetAcknowledgedJobs());
            Assert.Empty(store.GetSpoolerTrackingJobs());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RetentionPurgesConfirmedOutputOnlyAfterTheRetentionWindow()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Confirmed print output retention"u8.ToArray();
            var job = CreateJob(bytes);
            var entry = await store.EnqueueAsync(job, bytes, CancellationToken.None);
            await store.MarkAcknowledgedAsync(job.Id, CancellationToken.None);
            await store.MarkSubmissionStartedAsync(job.Id, "SmartPrint-Retention-Test", CancellationToken.None);
            await store.MarkSpoolerAcceptedAsync(job.Id, 321, "SmartPrint-Retention-Test", CancellationToken.None);
            await store.MarkOutputConfirmedAsync(job.Id, CancellationToken.None);

            var completed = store.Find(job.Id)!.CompletedUtc;
            Assert.NotNull(completed);
            Assert.Equal(0, await store.PruneRetainedEntriesAsync(
                completed!.Value.AddDays(29), TimeSpan.FromDays(30), root, CancellationToken.None));
            Assert.True(File.Exists(entry.DocumentPath));

            Assert.Equal(1, await store.PruneRetainedEntriesAsync(
                completed.Value.AddDays(31), TimeSpan.FromDays(30), root, CancellationToken.None));
            Assert.Null(store.Find(job.Id));
            Assert.False(File.Exists(entry.DocumentPath));
            Assert.Empty(Directory.GetFiles(root, "*.json"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RetentionNeverPurgesOutputUnconfirmedJobs()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Unconfirmed output must remain recoverable"u8.ToArray();
            var job = CreateJob(bytes);
            var entry = await store.EnqueueAsync(job, bytes, CancellationToken.None);
            await store.MarkAcknowledgedAsync(job.Id, CancellationToken.None);
            await store.MarkSubmissionStartedAsync(job.Id, "SmartPrint-Unconfirmed-Test", CancellationToken.None);
            await store.MarkSpoolerAcceptedAsync(job.Id, 654, "SmartPrint-Unconfirmed-Test", CancellationToken.None);
            await store.MarkOutputUnconfirmedAsync(job.Id, "SPOOLER_JOB_NO_LONGER_VISIBLE", CancellationToken.None);

            var future = DateTimeOffset.UtcNow.AddDays(365);
            Assert.Equal(0, await store.PruneRetainedEntriesAsync(
                future, TimeSpan.FromDays(30), root, CancellationToken.None));
            Assert.Equal("OUTPUT_UNCONFIRMED", store.Find(job.Id)!.State);
            Assert.True(File.Exists(entry.DocumentPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UnconfirmedPdfExportCanBeExplicitlyRequeuedFromTheProtectedLocalCopy()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "PDF export retry document"u8.ToArray();
            var job = CreateJob(bytes) with
            {
                QueueName = "Microsoft Print to PDF",
                OutputMode = PrinterOutputModeRules.PdfExport
            };
            await store.EnqueueAsync(job, bytes, CancellationToken.None);
            await store.MarkAcknowledgedAsync(job.Id, CancellationToken.None);

            var outputPath = Path.Combine(root, "SP-TEST-receipt.pdf");
            await store.MarkPdfExportStartedAsync(job.Id, outputPath, CancellationToken.None);
            await store.MarkOutputUnconfirmedAsync(
                job.Id, "LEGACY_MICROSOFT_PDF_DIALOG_INTERRUPTED",
                CancellationToken.None, PrinterOutputModeRules.PdfExport);

            Assert.Equal("OUTPUT_UNCONFIRMED", store.Find(job.Id)!.State);
            Assert.Equal(PrinterOutputModeRules.PdfExport, store.Find(job.Id)!.OutputMode);
            Assert.Single(store.GetAcknowledgedJobs());
            Assert.Single(store.GetPdfExportRecoveryJobs());
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(job.Id)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PreSubmissionBlockReturnsJobToAcknowledgedStateForExplicitRetry()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Retry-safe document"u8.ToArray();
            var job = CreateJob(bytes);
            await store.EnqueueAsync(job, bytes, CancellationToken.None);
            await store.MarkAcknowledgedAsync(job.Id, CancellationToken.None);
            await store.MarkSubmissionStartedAsync(job.Id, "SmartPrint-Test-Retry", CancellationToken.None);
            await store.MarkReadyAfterBlockedAsync(job.Id, "PRINTER_OFFLINE", CancellationToken.None);
            Assert.Equal("ACKNOWLEDGED", store.Find(job.Id)!.State);
            Assert.Single(store.GetAcknowledgedJobs());
            Assert.Empty(store.GetInterruptedSubmissions());
        }
        finally { Directory.Delete(root, recursive: true); }
    }


    [Fact]
    public async Task ShopOutputConfirmationStopsLocalSpoolerMonitoring()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Output checked"u8.ToArray();
            var job = CreateJob(bytes);
            await store.EnqueueAsync(job, bytes, CancellationToken.None);
            await store.MarkAcknowledgedAsync(job.Id, CancellationToken.None);
            await store.MarkSubmissionStartedAsync(job.Id, "SmartPrint-OutputChecked", CancellationToken.None);
            await store.MarkSpoolerAcceptedAsync(job.Id, 456, "SmartPrint-OutputChecked", CancellationToken.None);
            await store.MarkOutputConfirmedAsync(job.Id, CancellationToken.None);

            Assert.Equal("OUTPUT_CONFIRMED", store.Find(job.Id)!.State);
            Assert.Empty(store.GetSpoolerTrackingJobs());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PendingPaymentUploadIsRemovedOnlyAfterPlatformDeadlineAuthorisesIt()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Pending payment source remains protected"u8.ToArray();
            var job = CreateJob(bytes);
            var entry = await store.EnqueueLocalUploadAsync(job, bytes, CancellationToken.None);
            await store.MarkLocalUploadFinalizedAsync(job.Id, CancellationToken.None);

            Assert.Contains(store.GetPendingPaymentLocalUploads(), item => item.JobId == job.Id);
            Assert.Equal(0, await store.PruneRetainedEntriesAsync(
                DateTimeOffset.UtcNow.AddDays(365), TimeSpan.FromDays(1), root, CancellationToken.None));
            Assert.True(File.Exists(entry.DocumentPath));

            var deleteAfter = DateTimeOffset.UtcNow.AddDays(-1);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.DeleteExpiredPendingPaymentUploadAsync(
                    job.Id, deleteAfter, deleteAfter.AddDays(-1), CancellationToken.None));

            await store.DeleteExpiredPendingPaymentUploadAsync(
                job.Id, deleteAfter, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Null(store.Find(job.Id));
            Assert.False(File.Exists(entry.DocumentPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CloudRecoveredTicketPromotesTheOriginalLocalCopyAfterPayment()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Original shop Wi-Fi copy retained for HTTPS recovery"u8.ToArray();
            var stagedJob = CreateJob(bytes) with { StorageMode = "LOCAL_AGENT" };
            var staged = await store.EnqueueLocalUploadAsync(stagedJob, bytes, CancellationToken.None);

            await store.MarkLocalUploadFinalizedAsync(stagedJob.Id, CancellationToken.None);
            Assert.Equal("LOCAL_UPLOAD_PENDING_PAYMENT", store.Find(stagedJob.Id)!.State);

            // Cloud recovery stored this ticket's canonical document, but the Agent
            // still has the same hash-verified source. Promote it after paid claim.
            var paidClaim = stagedJob with
            {
                StorageMode = "CLOUD",
                Status = "LOCAL_QUEUE_CLAIMED",
                Copies = 2,
                ColourMode = "Colour",
                IsDuplex = true,
                QuotedAmountMinor = 500
            };
            await store.UpdateFromClaimAsync(paidClaim, CancellationToken.None);

            var acknowledged = store.Find(stagedJob.Id)!;
            Assert.Equal("ACK_PENDING", acknowledged.State);
            Assert.Equal(2, acknowledged.Copies);
            Assert.Equal("Colour", acknowledged.ColourMode);
            Assert.True(acknowledged.IsDuplex);

            await store.MarkAcknowledgedAsync(stagedJob.Id, CancellationToken.None);
            Assert.Equal("ACKNOWLEDGED", store.Find(stagedJob.Id)!.State);
            Assert.Single(store.GetAcknowledgedJobs());
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(stagedJob.Id)));
            Assert.True(File.Exists(staged.DocumentPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalPhotoRevisionReplacesTheProtectedDocumentAndCommitsTheServerSnapshot()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var originalBytes = "Original local image bytes"u8.ToArray();
            var job = CreateJob(originalBytes) with
            {
                Status = "AWAITING_SHOP_APPROVAL",
                StorageMode = "LOCAL_AGENT",
                MediaType = "image/jpeg",
                DocumentName = "original.jpg"
            };
            var staged = await store.EnqueueLocalUploadAsync(job, originalBytes, CancellationToken.None);
            await store.MarkLocalUploadFinalizedAsync(job.Id, CancellationToken.None);
            var replacement = "%PDF-1.4\\nshop-prepared-a4-photo-sheet"u8.ToArray();
            var committed = false;

            await store.ReplaceStagedPhotoDocumentAsync(
                job.Id,
                staged.Sha256,
                staged.SizeBytes,
                "photo-sheet.pdf",
                replacement,
                2,
                750,
                () =>
                {
                    committed = true;
                    Assert.True(replacement.AsSpan().SequenceEqual(store.ReadDocument(job.Id)));
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            var updated = store.Find(job.Id)!;
            Assert.True(committed);
            Assert.Equal("photo-sheet.pdf", updated.DocumentName);
            Assert.Equal("application/pdf", updated.MediaType);
            Assert.Equal(replacement.LongLength, updated.SizeBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(replacement)).ToLowerInvariant(), updated.Sha256);
            Assert.Equal(2, updated.PageCount);
            Assert.Equal(1, updated.Copies);
            Assert.Equal("Colour", updated.ColourMode);
            Assert.Equal("A4", updated.PaperSize);
            Assert.False(updated.IsDuplex);
            Assert.Equal(750, updated.QuotedAmountMinor);
            Assert.True(replacement.AsSpan().SequenceEqual(store.ReadDocument(job.Id)));
            Assert.Empty(Directory.GetFiles(root, "*.photo-revision.bak"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedCloudCommitRollsBackTheLocalPhotoBytesAndManifest()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var originalBytes = "Original local image bytes"u8.ToArray();
            var job = CreateJob(originalBytes) with
            {
                Status = "AWAITING_SHOP_APPROVAL",
                StorageMode = "LOCAL_AGENT",
                MediaType = "image/jpeg",
                DocumentName = "original.jpg"
            };
            var staged = await store.EnqueueLocalUploadAsync(job, originalBytes, CancellationToken.None);
            await store.MarkLocalUploadFinalizedAsync(job.Id, CancellationToken.None);
            var replacement = "%PDF-1.4\nshop-prepared-a4-photo-sheet"u8.ToArray();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ReplaceStagedPhotoDocumentAsync(
                    job.Id,
                    staged.Sha256,
                    staged.SizeBytes,
                    "photo-sheet.pdf",
                    replacement,
                    1,
                    750,
                    () => throw new InvalidOperationException("Cloud metadata commit was rejected."),
                    CancellationToken.None));

            var restored = store.Find(job.Id)!;
            Assert.Equal(staged.Sha256, restored.Sha256);
            Assert.Equal(staged.SizeBytes, restored.SizeBytes);
            Assert.Equal("original.jpg", restored.DocumentName);
            Assert.Equal("image/jpeg", restored.MediaType);
            Assert.True(originalBytes.AsSpan().SequenceEqual(store.ReadDocument(job.Id)));
            Assert.Empty(Directory.GetFiles(root, "*.photo-revision.bak"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrivateSourceIsRemovedOnlyAfterConfirmedPhysicalOutputAndServerAcknowledgement()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Sensitive customer private-print document"u8.ToArray();
            var job = CreateJob(bytes) with { PrivatePrinting = true, StorageMode = "LOCAL_AGENT" };
            var entry = await store.EnqueueAsync(job, bytes, CancellationToken.None);

            await store.MarkAcknowledgedAsync(job.Id, CancellationToken.None);
            await store.MarkSubmissionStartedAsync(job.Id, "SmartPrint-Private-Purge-Test", CancellationToken.None);
            await store.MarkSpoolerAcceptedAsync(job.Id, 789, "SmartPrint-Private-Purge-Test", CancellationToken.None);
            Assert.True(File.Exists(entry.DocumentPath));
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(job.Id)));

            await store.MarkOutputConfirmedAsync(job.Id, CancellationToken.None);

            var sanitized = store.Find(job.Id)!;
            Assert.Equal("OUTPUT_CONFIRMED", sanitized.State);
            Assert.True(sanitized.PrivatePrinting);
            Assert.True(sanitized.PrivatePurgePending);
            Assert.Equal("Private document", sanitized.DocumentName);
            Assert.Equal(string.Empty, sanitized.DocumentPath);
            Assert.Equal(string.Empty, sanitized.Sha256);
            Assert.Equal(0, sanitized.SizeBytes);
            Assert.False(File.Exists(entry.DocumentPath));
            Assert.Contains(store.GetPrivatePurgePendingJobs(), item => item.JobId == job.Id);

            await store.MarkPrivatePurgeAcknowledgedAsync(job.Id, CancellationToken.None);
            Assert.False(store.Find(job.Id)!.PrivatePurgePending);
            Assert.Empty(store.GetPrivatePurgePendingJobs());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrivateSourceRemainsAvailableWhenPhysicalOutputIsUnconfirmed()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "Keep private print available for output reconciliation"u8.ToArray();
            var job = CreateJob(bytes) with { PrivatePrinting = true, StorageMode = "LOCAL_AGENT" };
            var entry = await store.EnqueueAsync(job, bytes, CancellationToken.None);

            await store.MarkAcknowledgedAsync(job.Id, CancellationToken.None);
            await store.MarkSubmissionStartedAsync(job.Id, "SmartPrint-Private-Unconfirmed", CancellationToken.None);
            await store.MarkSpoolerAcceptedAsync(job.Id, 790, "SmartPrint-Private-Unconfirmed", CancellationToken.None);
            await store.MarkOutputUnconfirmedAsync(job.Id, "SPOOLER_JOB_NO_LONGER_VISIBLE", CancellationToken.None);

            Assert.Equal("OUTPUT_UNCONFIRMED", store.Find(job.Id)!.State);
            Assert.True(File.Exists(entry.DocumentPath));
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(job.Id)));
            Assert.Empty(store.GetPrivatePurgePendingJobs());
            Assert.Equal(0, await store.PruneRetainedEntriesAsync(
                DateTimeOffset.UtcNow.AddDays(365), TimeSpan.FromDays(1), root, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrivatePurgeRemovesOrphanedDocumentAndTemporaryManifestFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            var jobId = Guid.NewGuid();
            var documentPath = Path.Combine(root, jobId.ToString("N") + ".qdoc");
            var temporaryDocumentPath = documentPath + ".tmp";
            var temporaryManifestPath = Path.Combine(root, jobId.ToString("N") + ".json.tmp");
            await File.WriteAllBytesAsync(documentPath, RandomNumberGenerator.GetBytes(256));
            await File.WriteAllBytesAsync(temporaryDocumentPath, RandomNumberGenerator.GetBytes(128));
            await File.WriteAllTextAsync(temporaryManifestPath, "{\"documentName\":\"private-document.pdf\",\"sha256\":\"secret-hash\"}");

            Assert.Null(store.Find(jobId));
            await store.PurgeUnindexedPrivateSourceAsync(jobId, CancellationToken.None);

            Assert.False(File.Exists(documentPath));
            Assert.False(File.Exists(temporaryDocumentPath));
            Assert.False(File.Exists(temporaryManifestPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreservesPrivateIdDocumentKindInTheProtectedAgentQueue()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var store = new AgentLocalQueueStore(root);
            await store.InitializeAsync(CancellationToken.None);
            var bytes = "%PDF-1.7\\nprivate ID sheet"u8.ToArray();
            var job = CreateJob(bytes) with
            {
                PrintKind = "ID_DOCUMENT",
                PageCount = 1,
                Copies = 1,
                ColourMode = "Colour",
                PaperSize = "A4",
                IsDuplex = false,
                Orientation = "PORTRAIT"
            };

            var entry = await store.EnqueueAsync(job, bytes, CancellationToken.None);
            var persisted = store.Find(job.Id)!;

            Assert.Equal("ID_DOCUMENT", entry.PrintKind);
            Assert.Equal("ID_DOCUMENT", persisted.PrintKind);
            Assert.True(bytes.AsSpan().SequenceEqual(store.ReadDocument(job.Id)));

            var updatedClaim = job with
            {
                Status = "LOCAL_QUEUE_CLAIMED",
                QuotedAmountMinor = 500,
                LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(3)
            };
            await store.UpdateFromClaimAsync(updatedClaim, CancellationToken.None);

            var claimed = store.Find(job.Id)!;
            Assert.Equal("ID_DOCUMENT", claimed.PrintKind);
            Assert.Equal("A4", claimed.PaperSize);
            Assert.Equal("PORTRAIT", claimed.Orientation);
            Assert.False(claimed.IsDuplex);
            Assert.Equal(1, claimed.Copies);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentClaimedJob CreateJob(byte[] content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return new AgentClaimedJob(
            Guid.NewGuid(),
            "SP-261030-TEST0001",
            "LOCAL_QUEUE_CLAIMED",
            1,
            1,
            "Mono",
            "A4",
            false,
            "INR",
            200,
            Guid.NewGuid(),
            "Shop Printer",
            "Brother HL-L2350DW",
            "receipt.pdf",
            "application/pdf",
            content.LongLength,
            hash,
            DateTimeOffset.UtcNow.AddMinutes(3));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "qrtoprint-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
