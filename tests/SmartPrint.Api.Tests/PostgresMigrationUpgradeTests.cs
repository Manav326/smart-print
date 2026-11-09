using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartPrint.Api.Data;
using SmartPrint.Api.Domain;
using SmartPrint.Api.Migrations;
using SmartPrint.Api.Services;

namespace SmartPrint.Api.Tests;

public sealed class PostgresMigrationUpgradeTests
{
    [Fact]
    public async Task ExistingSchemaUpgradesAndSeedsPlatformOperationalSettings()
    {
        var connectionString = Environment.GetEnvironmentVariable("SMARTPRINT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var options = new DbContextOptionsBuilder<SmartPrintDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(
                typeof(InitialPlatform).Assembly.GetName().Name))
            .ReplaceService<IMigrationsIdGenerator, LegacyCompatibleMigrationsIdGenerator>()
            .Options;

        await using var db = new SmartPrintDbContext(options);
        await db.Database.EnsureDeletedAsync();

        var discoveredMigrations = db.GetService<IMigrationsAssembly>();
        Assert.Equal(
            "202610310010_CustomerPrintAccounts",
            discoveredMigrations.FindMigrationId("202610310010_CustomerPrintAccounts"));

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("202610310010_CustomerPrintAccounts");
        await migrator.MigrateAsync();

        var settings = await db.PlatformOperationalSettings.SingleAsync(
            item => item.Id == PlatformOperationalSettings.SingletonIdValue);

        Assert.Equal(3000, settings.TrialMaxMonthlyPrintJobs);
        Assert.Equal(30, settings.CloudCompletedDocumentRetentionDays);
        Assert.Equal(30, settings.AgentCompletedDocumentRetentionDays);
        Assert.Equal(30, settings.PendingLocalUploadRetentionDays);
        Assert.True(settings.AllowPaidJobClaimsWhenSuspended);

        Assert.Contains(
            "202611100001_AddRazorpayShopSubscriptionPayments",
            await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains(
            "202611010003_AddPlatformOperationalSettings",
            await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains(
            "202611010004_ActiveAgentFingerprints",
            await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains(
            "202611020001_AddBatchCheckoutPayments",
            await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains(
            "202611020002_AddShopInstantPaymentPreference",
            await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains(
            "202611020003_AddPrintJobPrinterIdentitySnapshot",
            await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains(
            "202611030001_AddPrintBatchNames",
            await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains(
            "202611030002_AddPhotoPrintingPricingAndJobSnapshots",
            await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains(
            "202611070001_AddPhotoSheetDensitySnapshot",
            await db.Database.GetAppliedMigrationsAsync());

        // Reproduce the local-upload completion transaction at the exact monthly quota boundary.
        settings.TrialMaxMonthlyPrintJobs = 1;
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant
        {
            Slug = $"quota-boundary-{Guid.NewGuid():N}",
            BusinessName = "Quota boundary test shop"
        };
        var agent = new AgentDevice
        {
            TenantId = tenant.Id,
            DeviceName = "Quota test Agent",
            FingerprintHash = new string('a', 64),
            SecretHash = new string('b', 64)
        };
        var revokedAgent = new AgentDevice
        {
            TenantId = tenant.Id,
            DeviceName = "Previously revoked Agent",
            FingerprintHash = new string('a', 64),
            SecretHash = new string('c', 64),
            RevokedUtc = now
        };
        var printer = new PrinterDevice
        {
            TenantId = tenant.Id,
            AgentDeviceId = agent.Id,
            DisplayName = "Quota test printer",
            Manufacturer = "Test",
            Model = "Test printer",
            QueueName = "Quota-Test-Queue"
        };
        var station = new QrStation
        {
            TenantId = tenant.Id,
            PrinterDeviceId = printer.Id,
            DisplayName = "Quota test station",
            PublicCodeHash = new string('c', 64),
            PublicCodeHint = "QT"
        };
        var session = new CustomerPrintSession
        {
            TenantId = tenant.Id,
            QrStationId = station.Id,
            TokenHash = new string('d', 64),
            ExpiresUtc = now.AddMinutes(15)
        };
        var subscription = new TenantSubscription
        {
            TenantId = tenant.Id,
            PlanCode = "TRIAL",
            Status = "Trial",
            PeriodStartUtc = now.AddDays(-1),
            PeriodEndUtc = now.AddDays(7)
        };
        var ticket = new LocalPrintUploadTicket
        {
            TenantId = tenant.Id,
            CustomerSessionId = session.Id,
            QrStationId = station.Id,
            PrinterDeviceId = printer.Id,
            AgentDeviceId = agent.Id,
            PrintJobId = Guid.NewGuid(),
            JobNumber = "SP-QUOTA-BOUNDARY",
            IdempotencyKey = "quota-boundary",
            GrantHash = new string('e', 64),
            OriginalFileName = "quota-test.pdf",
            MediaType = "application/pdf",
            FileExtension = ".pdf",
            SizeBytes = 1024,
            Sha256 = new string('f', 64),
            ExpiresUtc = now.AddMinutes(15)
        };

        // A revoked machine identity can be re-enrolled, while the active record
        // for that same fingerprint remains unique within this tenant.
        db.AddRange(tenant, agent, revokedAgent, printer, station, session, subscription, ticket);
        await db.SaveChangesAsync();

        // Rollback must remain safe when revoked and replacement records share a fingerprint.
        const string fingerprintIndexSql =
            "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = current_schema() " +
            "AND indexname = 'IX_AgentDevices_TenantId_FingerprintHash'";

        var indexBeforeRollback = await db.Database.SqlQueryRaw<string>(fingerprintIndexSql).SingleAsync();
        Assert.Contains("WHERE", indexBeforeRollback, StringComparison.OrdinalIgnoreCase);

        await migrator.MigrateAsync("202611010003_AddPlatformOperationalSettings");
        var indexAfterRollback = await db.Database.SqlQueryRaw<string>(fingerprintIndexSql).SingleAsync();
        Assert.Contains("RevokedUtc", indexAfterRollback, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", indexAfterRollback, StringComparison.OrdinalIgnoreCase);

        await migrator.MigrateAsync();
        var indexAfterReapply = await db.Database.SqlQueryRaw<string>(fingerprintIndexSql).SingleAsync();
        Assert.Contains("RevokedUtc", indexAfterReapply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", indexAfterReapply, StringComparison.OrdinalIgnoreCase);

                var entitlements = new TenantEntitlementService(db);
        Assert.Equal(1L, await entitlements.GetMonthlyPrintJobUsageAsync(
            tenant.Id, CancellationToken.None));

        await using var transaction = await db.Database.BeginTransactionAsync();
        ticket.CompletedUtc = now;
        await db.SaveChangesAsync();

        await entitlements.AcquireQuotaLockAsync(
            tenant.Id, TenantQuotaResource.MonthlyPrintJob, CancellationToken.None);
        var decision = await entitlements.CheckCapacityAsync(
            tenant.Id, TenantQuotaResource.MonthlyPrintJob, CancellationToken.None);

        Assert.True(decision.Allowed, decision.Message);
        Assert.Equal(0L, decision.Current);
        Assert.Equal(1L, decision.Maximum);

        // Materialising the completed reservation as a PrintJob must preserve the quota count.
        db.PrintJobs.Add(new PrintJob
        {
            Id = ticket.PrintJobId,
            TenantId = ticket.TenantId,
            QrStationId = ticket.QrStationId,
            PrinterDeviceId = ticket.PrinterDeviceId,
            CustomerSessionId = ticket.CustomerSessionId,
            JobNumber = ticket.JobNumber,
            Status = "AWAITING_CONFIGURATION",
            IdempotencyKey = $"LOCAL-{ticket.Id:N}",
            PageCount = 1,
            Copies = 1,
            Currency = "INR",
            QuotedAmountMinor = 0,
            CreatedUtc = now,
            UpdatedUtc = now
        });
        await db.SaveChangesAsync();

        var afterMaterialisation = await entitlements.CheckCapacityAsync(
            tenant.Id, TenantQuotaResource.MonthlyPrintJob, CancellationToken.None);
        Assert.False(afterMaterialisation.Allowed);
        Assert.Equal(1L, afterMaterialisation.Current);
        Assert.Equal(1L, afterMaterialisation.Maximum);
        Assert.Equal(1L, await entitlements.GetMonthlyPrintJobUsageAsync(
            tenant.Id, CancellationToken.None));
        await transaction.RollbackAsync();

        await VerifyPrinterIdentitySnapshotBackfillAsync(
            options, tenant.Id, station.Id, session.Id, printer.Id, now);
        await VerifyConcurrentLocalBatchReservationsStayOnOneComputerAsync(
            options, tenant.Id, agent.Id, station.Id, session.Id, printer.Id, now);
        await VerifyConcurrentBatchAssignmentsRemainAtomicAsync(
            options, tenant.Id, station.Id, session.Id, printer.Id, now);
        await VerifyAgentReferencesCannotCrossTenantAsync(options, now);

        // The saved layout density is part of the quote snapshot and must survive a fresh DbContext.
        var photoJobId = Guid.NewGuid();
        var legacyPhotoJobId = Guid.NewGuid();
        db.PrintJobs.AddRange(
            new PrintJob
            {
                Id = photoJobId, TenantId = tenant.Id, QrStationId = station.Id,
                CustomerSessionId = session.Id, JobNumber = "SP-PHOTO-DENSITY",
                IdempotencyKey = "PHOTO-DENSITY-SNAPSHOT", Status = "AWAITING_PAYMENT",
                PageCount = 6, Copies = 1, Currency = "INR",
                QuotedAmountMinor = 8750, PrintKind = "PHOTO", PhotoSizeCode = "35x45",
                PhotoCount = 50, PhotosPerPage = 9, PhotoUnitPriceMinorSnapshot = 175,
                CreatedUtc = now, UpdatedUtc = now
            },
            new PrintJob
            {
                Id = legacyPhotoJobId, TenantId = tenant.Id, QrStationId = station.Id,
                CustomerSessionId = session.Id, JobNumber = "SP-PHOTO-LEGACY",
                IdempotencyKey = "PHOTO-LEGACY-NO-DENSITY", Status = "AWAITING_PAYMENT",
                PageCount = 2, Copies = 1, Currency = "INR",
                QuotedAmountMinor = 8750, PrintKind = "PHOTO", PhotoSizeCode = "35x45",
                PhotoCount = 50, PhotosPerPage = null, PhotoUnitPriceMinorSnapshot = 175,
                CreatedUtc = now, UpdatedUtc = now
            });
        await db.SaveChangesAsync();

        await using var photoSnapshotVerification = new SmartPrintDbContext(options);
        var savedPhotoJob = await photoSnapshotVerification.PrintJobs.AsNoTracking()
            .SingleAsync(item => item.Id == photoJobId);
        var legacyPhotoJob = await photoSnapshotVerification.PrintJobs.AsNoTracking()
            .SingleAsync(item => item.Id == legacyPhotoJobId);
        Assert.Equal(50, savedPhotoJob.PhotoCount);
        Assert.Equal(9, savedPhotoJob.PhotosPerPage);
        Assert.Equal(6, savedPhotoJob.PageCount);
        Assert.Equal(50, legacyPhotoJob.PhotoCount);
        Assert.Null(legacyPhotoJob.PhotosPerPage); // Existing records are not assigned a guessed density.

        // The database must reject a layout density that the selected physical size cannot fit.
        var invalidDensityScenarios = new[]
        {
            (PhotoSizeCode: "35x45", PhotoCount: 50, PhotosPerPage: 26, PrintKind: "PHOTO"),
            (PhotoSizeCode: "4x6", PhotoCount: 2, PhotosPerPage: 2, PrintKind: "PHOTO"),
            (PhotoSizeCode: (string?)null, PhotoCount: 0, PhotosPerPage: 1, PrintKind: "DOCUMENT")
        };
        foreach (var scenario in invalidDensityScenarios)
        {
            await using var invalidDensityContext = new SmartPrintDbContext(options);
            invalidDensityContext.PrintJobs.Add(new PrintJob
            {
                Id = Guid.NewGuid(), TenantId = tenant.Id, QrStationId = station.Id,
                CustomerSessionId = session.Id, JobNumber = $"SP-PHOTO-INVALID-{Guid.NewGuid():N}"[..24],
                IdempotencyKey = $"PHOTO-INVALID-DENSITY-{Guid.NewGuid():N}",
                Status = "AWAITING_PAYMENT", PageCount = 1, Copies = 1,
                Currency = "INR", QuotedAmountMinor = 100,
                PrintKind = scenario.PrintKind, PhotoSizeCode = scenario.PhotoSizeCode,
                PhotoCount = scenario.PhotoCount, PhotosPerPage = scenario.PhotosPerPage,
                PhotoUnitPriceMinorSnapshot = scenario.PrintKind == "PHOTO" ? 100 : 0,
                CreatedUtc = now, UpdatedUtc = now
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => invalidDensityContext.SaveChangesAsync());
        }

        await VerifyRazorpayPaymentRecordsAreDurableAndUniqueAsync(options);
    }

    private static async Task VerifyRazorpayPaymentRecordsAreDurableAndUniqueAsync(
        DbContextOptions<SmartPrintDbContext> options)
    {
        var tenantId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var tenant = new Tenant
        {
            Id = tenantId,
            Slug = $"razorpay-payment-{suffix}",
            BusinessName = "Razorpay subscription test shop"
        };
        var plan = new SubscriptionPlan
        {
            Code = $"RZPY_{suffix.ToUpperInvariant()}",
            Name = "Razorpay test plan",
            IsActive = true
        };
        var version = new SubscriptionPlanVersion
        {
            PlanId = plan.Id,
            Version = 1,
            Status = "Published",
            Currency = "INR",
            MonthlyPriceMinor = 49900,
            AnnualPriceMinor = 499900,
            CloudAssistedPrintingEnabled = true
        };
        var orderId = $"order_{Guid.NewGuid():N}";
        var eventId = $"evt_{Guid.NewGuid():N}";
        await using (var seed = new SmartPrintDbContext(options))
        {
            seed.AddRange(
                tenant,
                plan,
                version,
                new ShopSubscriptionPayment
                {
                    TenantId = tenant.Id,
                    PlanVersionId = version.Id,
                    PlanCode = plan.Code,
                    PlanName = plan.Name,
                    BillingInterval = "Monthly",
                    AmountMinor = 49900,
                    Currency = "INR",
                    IdempotencyKey = $"razorpay-order-{Guid.NewGuid():N}",
                    RazorpayOrderId = orderId,
                    Status = "Pending"
                },
                new RazorpayWebhookEvent
                {
                    EventId = eventId,
                    EventType = "payment.captured",
                    RazorpayOrderId = orderId,
                    Status = "Pending"
                });
            await seed.SaveChangesAsync();
        }

        await using (var duplicateOrder = new SmartPrintDbContext(options))
        {
            duplicateOrder.ShopSubscriptionPayments.Add(new ShopSubscriptionPayment
            {
                TenantId = tenant.Id,
                PlanVersionId = version.Id,
                PlanCode = plan.Code,
                PlanName = plan.Name,
                BillingInterval = "Monthly",
                AmountMinor = 49900,
                Currency = "INR",
                IdempotencyKey = $"other-key-{Guid.NewGuid():N}",
                RazorpayOrderId = orderId,
                Status = "Pending"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicateOrder.SaveChangesAsync());
        }

        await using (var duplicateEvent = new SmartPrintDbContext(options))
        {
            duplicateEvent.RazorpayWebhookEvents.Add(new RazorpayWebhookEvent
            {
                EventId = eventId,
                EventType = "payment.captured",
                RazorpayOrderId = orderId,
                Status = "Pending"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicateEvent.SaveChangesAsync());
        }

        await using var verification = new SmartPrintDbContext(options);
        Assert.Equal(1, await verification.ShopSubscriptionPayments.CountAsync(item => item.TenantId == tenant.Id));
        Assert.Equal(1, await verification.RazorpayWebhookEvents.CountAsync(item => item.EventId == eventId));
    }

    private static async Task VerifyConcurrentLocalBatchReservationsStayOnOneComputerAsync(
        DbContextOptions<SmartPrintDbContext> options,
        Guid tenantId,
        Guid firstAgentId,
        Guid stationId,
        Guid sessionId,
        Guid firstPrinterId,
        DateTimeOffset now)
    {
        var batchId = Guid.NewGuid();
        var secondAgentId = Guid.NewGuid();
        var secondPrinterId = Guid.NewGuid();
        var localJobId = Guid.NewGuid();

        await using (var setup = new SmartPrintDbContext(options))
        {
            setup.AgentDevices.Add(new AgentDevice
            {
                Id = secondAgentId,
                TenantId = tenantId,
                DeviceName = "Second shop computer",
                FingerprintHash = Guid.NewGuid().ToString("N").PadLeft(64, 'a'),
                SecretHash = Guid.NewGuid().ToString("N").PadLeft(64, 'b')
            });
            setup.PrinterDevices.Add(new PrinterDevice
            {
                Id = secondPrinterId,
                TenantId = tenantId,
                AgentDeviceId = secondAgentId,
                DisplayName = "Second computer printer",
                Manufacturer = "Test",
                Model = "Concurrent queue",
                ConnectionType = "WindowsQueue",
                QueueName = $"SECOND-{Guid.NewGuid():N}"[..24],
                Status = "Ready",
                IsActive = true,
                LastSeenUtc = now
            });
            await setup.SaveChangesAsync();
        }

        await using var firstContext = new SmartPrintDbContext(options);
        await using var firstTransaction = await firstContext.Database.BeginTransactionAsync();
        await new TenantEntitlementService(firstContext).AcquireQuotaLockAsync(
            tenantId, TenantQuotaResource.MonthlyPrintJob, CancellationToken.None);

        firstContext.PrintJobs.Add(new PrintJob
        {
            Id = localJobId,
            TenantId = tenantId,
            QrStationId = stationId,
            CustomerSessionId = sessionId,
            BatchId = batchId,
            PrinterDeviceId = firstPrinterId,
            JobNumber = $"SP-LOCAL-{Guid.NewGuid():N}"[..22],
            IdempotencyKey = $"LOCAL-BATCH-{Guid.NewGuid():N}",
            Status = "AWAITING_CONFIGURATION",
            PageCount = 1,
            Copies = 1,
            Currency = "INR",
            QuotedAmountMinor = 0,
            CreatedUtc = now,
            UpdatedUtc = now
        });
        firstContext.PrintDocuments.Add(new PrintDocument
        {
            TenantId = tenantId,
            PrintJobId = localJobId,
            OriginalFileName = "local-batch-test.pdf",
            MediaType = "application/pdf",
            FileExtension = ".pdf",
            SizeBytes = 128,
            Sha256 = Guid.NewGuid().ToString("N").PadLeft(64, 'c'),
            StorageKey = $"integration/{localJobId:N}",
            StorageMode = "LOCAL_AGENT",
            AgentDeviceId = firstAgentId,
            CreatedUtc = now
        });
        firstContext.LocalPrintUploadTickets.Add(new LocalPrintUploadTicket
        {
            TenantId = tenantId,
            CustomerSessionId = sessionId,
            BatchId = batchId,
            QrStationId = stationId,
            PrinterDeviceId = firstPrinterId,
            AgentDeviceId = firstAgentId,
            PrintJobId = localJobId,
            JobNumber = $"SP-TICKET-{Guid.NewGuid():N}"[..24],
            IdempotencyKey = $"LOCAL-TICKET-{Guid.NewGuid():N}",
            GrantHash = Guid.NewGuid().ToString("N").PadLeft(64, 'd'),
            OriginalFileName = "local-batch-test.pdf",
            MediaType = "application/pdf",
            FileExtension = ".pdf",
            SizeBytes = 128,
            Sha256 = Guid.NewGuid().ToString("N").PadLeft(64, 'e'),
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(10)
        });
        await firstContext.SaveChangesAsync();

        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDecision = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTask = Task.Run(async () =>
        {
            await using var secondContext = new SmartPrintDbContext(options);
            await using var secondTransaction = await secondContext.Database.BeginTransactionAsync();
            secondStarted.TrySetResult(true);
            await new TenantEntitlementService(secondContext).AcquireQuotaLockAsync(
                tenantId, TenantQuotaResource.MonthlyPrintJob, CancellationToken.None);

            var ticketAgents = await secondContext.LocalPrintUploadTickets.AsNoTracking()
                .Where(item => item.TenantId == tenantId &&
                               item.CustomerSessionId == sessionId &&
                               item.BatchId == batchId &&
                               (item.CompletedUtc != null || item.ExpiresUtc > now))
                .Select(item => (Guid?)item.AgentDeviceId)
                .ToListAsync();
            var documentAgents = await (
                from batchJob in secondContext.PrintJobs.AsNoTracking()
                join batchDocument in secondContext.PrintDocuments.AsNoTracking()
                    on new { batchJob.TenantId, PrintJobId = batchJob.Id }
                    equals new { batchDocument.TenantId, batchDocument.PrintJobId }
                where batchJob.TenantId == tenantId &&
                      batchJob.CustomerSessionId == sessionId &&
                      batchJob.BatchId == batchId &&
                      batchDocument.StorageMode == "LOCAL_AGENT"
                select batchDocument.AgentDeviceId).ToListAsync();

            var conflict = BatchLocalUploadRules.FindAgentConflict(
                ticketAgents.Concat(documentAgents),
                secondAgentId);
            secondDecision.TrySetResult(conflict);
            await secondTransaction.CommitAsync();
        });

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        Assert.False(
            secondDecision.Task.IsCompleted,
            "A second local upload reservation must wait for the first tenant quota transaction.");

        await firstTransaction.CommitAsync();
        var observedConflict = await secondDecision.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("batch_local_agent_mismatch", observedConflict);
    }

    private static async Task VerifyConcurrentBatchAssignmentsRemainAtomicAsync(
        DbContextOptions<SmartPrintDbContext> options,
        Guid tenantId,
        Guid stationId,
        Guid sessionId,
        Guid firstPrinterId,
        DateTimeOffset now)
    {
        var batchId = Guid.NewGuid();
        var secondPrinterId = Guid.NewGuid();
        await using (var setup = new SmartPrintDbContext(options))
        {
            var firstPrinter = await setup.PrinterDevices.AsNoTracking()
                .SingleAsync(item => item.Id == firstPrinterId);
            setup.PrinterDevices.Add(new PrinterDevice
            {
                Id = secondPrinterId,
                TenantId = tenantId,
                AgentDeviceId = firstPrinter.AgentDeviceId,
                DisplayName = "Concurrent alternate queue",
                Manufacturer = "Test",
                Model = "Same computer alternate queue",
                ConnectionType = "WindowsQueue",
                QueueName = $"ALT-{Guid.NewGuid():N}"[..22],
                Status = "Ready",
                IsActive = true,
                LastSeenUtc = now
            });
            setup.PrintJobs.AddRange(
                CreateConcurrentBatchJob(tenantId, stationId, sessionId, firstPrinterId, batchId, "A", now),
                CreateConcurrentBatchJob(tenantId, stationId, sessionId, firstPrinterId, batchId, "B", now));
            await setup.SaveChangesAsync();
        }

        await using var firstContext = new SmartPrintDbContext(options);
        await using var firstTransaction = await firstContext.Database.BeginTransactionAsync();
        var firstLockedRows = await firstContext.PrintJobs.FromSqlInterpolated(
            $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"BatchId\" = {batchId} FOR UPDATE")
            .ToListAsync();
        Assert.Equal(2, firstLockedRows.Count);
        foreach (var row in firstLockedRows)
            row.PrinterDeviceId = secondPrinterId;
        await firstContext.SaveChangesAsync();

        var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondObserved = new TaskCompletionSource<Guid?[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTask = Task.Run(async () =>
        {
            await using var secondContext = new SmartPrintDbContext(options);
            await using var secondTransaction = await secondContext.Database.BeginTransactionAsync();
            secondStarted.TrySetResult(true);
            var rows = await secondContext.PrintJobs.FromSqlInterpolated(
                $"SELECT * FROM \"PrintJobs\" WHERE \"TenantId\" = {tenantId} AND \"BatchId\" = {batchId} FOR UPDATE")
                .ToListAsync();
            secondObserved.TrySetResult(rows.Select(row => row.PrinterDeviceId).ToArray());
            Assert.Equal(2, rows.Count);
            foreach (var row in rows)
                row.PrinterDeviceId = firstPrinterId;
            await secondContext.SaveChangesAsync();
            await secondTransaction.CommitAsync();
        });

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        Assert.False(
            secondObserved.Task.IsCompleted,
            "A second whole-batch assignment must wait for the first transaction's row locks.");

        await firstTransaction.CommitAsync();
        var observedAssignments = await secondObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, observedAssignments.Length);
        Assert.All(observedAssignments, printerId => Assert.Equal(secondPrinterId, printerId));

        await using var verification = new SmartPrintDbContext(options);
        var finalAssignments = await verification.PrintJobs.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.BatchId == batchId)
            .Select(item => item.PrinterDeviceId)
            .ToListAsync();
        Assert.Equal(2, finalAssignments.Count);
        Assert.All(finalAssignments, printerId => Assert.Equal(firstPrinterId, printerId));
    }

    private static PrintJob CreateConcurrentBatchJob(
        Guid tenantId,
        Guid stationId,
        Guid sessionId,
        Guid printerId,
        Guid batchId,
        string suffix,
        DateTimeOffset now)
    {
        return new PrintJob
        {
            TenantId = tenantId,
            QrStationId = stationId,
            CustomerSessionId = sessionId,
            BatchId = batchId,
            PrinterDeviceId = printerId,
            JobNumber = $"SP-RACE-{Guid.NewGuid():N}"[..20],
            IdempotencyKey = $"BATCH-RACE-{batchId:N}-{suffix}",
            Status = "PAID_AUTHORISED",
            PageCount = 1,
            Copies = 1,
            Currency = "INR",
            QuotedAmountMinor = 100,
            CreatedUtc = now,
            UpdatedUtc = now
        };
    }

    private static async Task VerifyPrinterIdentitySnapshotBackfillAsync(
        DbContextOptions<SmartPrintDbContext> options,
        Guid tenantId,
        Guid stationId,
        Guid sessionId,
        Guid printerId,
        DateTimeOffset now)
    {
        var jobId = Guid.NewGuid();
        var submittedUtc = now.AddMinutes(-2);
        await using (var seed = new SmartPrintDbContext(options))
        {
            seed.PrintJobs.Add(new PrintJob
            {
                Id = jobId,
                TenantId = tenantId,
                QrStationId = stationId,
                CustomerSessionId = sessionId,
                PrinterDeviceId = printerId,
                JobNumber = $"SP-SNAPSHOT-{Guid.NewGuid():N}"[..23],
                IdempotencyKey = $"SNAPSHOT-{Guid.NewGuid():N}",
                Status = "COMPLETED",
                PageCount = 1,
                Copies = 1,
                Currency = "INR",
                QuotedAmountMinor = 100,
                SpoolerSubmittedUtc = submittedUtc,
                OutputConfirmedUtc = now,
                UpdatedUtc = now
            });
            await seed.SaveChangesAsync();
        }

        await using (var migrationContext = new SmartPrintDbContext(options))
        {
            var migrator = migrationContext.GetService<IMigrator>();
            await migrator.MigrateAsync("202611020002_AddShopInstantPaymentPreference");
            await migrator.MigrateAsync("202611020003_AddPrintJobPrinterIdentitySnapshot");
            await migrator.MigrateAsync("202611030001_AddPrintBatchNames");
            // Restore the latest schema before querying with the current EF model.
            await migrator.MigrateAsync();
        }

        await using var verification = new SmartPrintDbContext(options);
        var historicalJob = await verification.PrintJobs.AsNoTracking()
            .SingleAsync(item => item.Id == jobId);
        Assert.Equal("Quota test printer", historicalJob.PrinterNameSnapshot);
        Assert.Equal("Quota-Test-Queue", historicalJob.PrinterQueueNameSnapshot);
        Assert.Equal("Quota test Agent", historicalJob.PrinterAgentNameSnapshot);
        // Compare against the timestamp read back from PostgreSQL, whose storage
        // precision is microseconds rather than .NET's 100-nanosecond ticks.
        Assert.Equal(historicalJob.SpoolerSubmittedUtc, historicalJob.PrinterSnapshotUtc);

        var originalPrinter = await verification.PrinterDevices.SingleAsync(item => item.Id == printerId);
        originalPrinter.DisplayName = "Renamed queue after printing";
        originalPrinter.QueueName = "Renamed-Windows-Queue";
        await verification.SaveChangesAsync();

        var unchangedHistory = await verification.PrintJobs.AsNoTracking()
            .SingleAsync(item => item.Id == jobId);
        Assert.Equal("Quota test printer", unchangedHistory.PrinterNameSnapshot);
        Assert.Equal("Quota-Test-Queue", unchangedHistory.PrinterQueueNameSnapshot);
        Assert.Equal("Quota test Agent", unchangedHistory.PrinterAgentNameSnapshot);
        Assert.Equal(historicalJob.PrinterSnapshotUtc, unchangedHistory.PrinterSnapshotUtc);
    }

    private static async Task VerifyAgentReferencesCannotCrossTenantAsync(
        DbContextOptions<SmartPrintDbContext> options,
        DateTimeOffset now)
    {
        var tenantA = new Tenant
        {
            Slug = $"agent-isolation-a-{Guid.NewGuid():N}",
            BusinessName = "Agent isolation shop A"
        };
        var tenantB = new Tenant
        {
            Slug = $"agent-isolation-b-{Guid.NewGuid():N}",
            BusinessName = "Agent isolation shop B"
        };
        var agentA = CreateIsolationAgent(tenantA.Id, "Agent A");
        var agentB = CreateIsolationAgent(tenantB.Id, "Agent B");
        var printerA = new PrinterDevice
        {
            TenantId = tenantA.Id,
            AgentDeviceId = agentA.Id,
            DisplayName = "Shop A printer",
            Manufacturer = "Test",
            Model = "Isolation printer",
            QueueName = "ISOLATION-A",
            Status = "Ready",
            LastSeenUtc = now
        };
        var stationA = new QrStation
        {
            TenantId = tenantA.Id,
            PrinterDeviceId = printerA.Id,
            DisplayName = "Shop A station",
            PublicCodeHash = HashForIsolationTest("station-a"),
            PublicCodeHint = "SA"
        };
        var sessionA = new CustomerPrintSession
        {
            TenantId = tenantA.Id,
            QrStationId = stationA.Id,
            TokenHash = HashForIsolationTest("session-a"),
            ExpiresUtc = now.AddMinutes(15)
        };

        await using (var seed = new SmartPrintDbContext(options))
        {
            seed.AddRange(tenantA, tenantB, agentA, agentB, printerA, stationA, sessionA);
            await seed.SaveChangesAsync();
        }

        await using (var wrongPrinterContext = new SmartPrintDbContext(options))
        {
            wrongPrinterContext.PrinterDevices.Add(new PrinterDevice
            {
                TenantId = tenantA.Id,
                AgentDeviceId = agentB.Id,
                DisplayName = "Cross-tenant printer",
                Manufacturer = "Test",
                Model = "Invalid",
                QueueName = "CROSS-TENANT-QUEUE"
            });

            await Assert.ThrowsAsync<DbUpdateException>(
                () => wrongPrinterContext.SaveChangesAsync());
        }

        await using (var wrongClaimContext = new SmartPrintDbContext(options))
        {
            wrongClaimContext.PrintJobs.Add(new PrintJob
            {
                TenantId = tenantA.Id,
                QrStationId = stationA.Id,
                CustomerSessionId = sessionA.Id,
                ClaimedByAgentDeviceId = agentB.Id,
                PrinterDeviceId = printerA.Id,
                JobNumber = $"SP-CROSS-{Guid.NewGuid():N}"[..20],
                IdempotencyKey = $"CROSS-TENANT-{Guid.NewGuid():N}",
                Status = "LOCAL_QUEUE_CLAIMED",
                PageCount = 1,
                Copies = 1,
                Currency = "INR",
                QuotedAmountMinor = 100,
                CreatedUtc = now,
                UpdatedUtc = now
            });

            await Assert.ThrowsAsync<DbUpdateException>(
                () => wrongClaimContext.SaveChangesAsync());
        }
    }

    private static AgentDevice CreateIsolationAgent(Guid tenantId, string name) => new()
    {
        TenantId = tenantId,
        DeviceName = name,
        FingerprintHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{name}-{Guid.NewGuid():N}"))).ToLowerInvariant(),
        SecretHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{name}-secret-{Guid.NewGuid():N}"))).ToLowerInvariant(),
        Status = "Online"
    };

    private static string HashForIsolationTest(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{value}-{Guid.NewGuid():N}"))).ToLowerInvariant();

}
