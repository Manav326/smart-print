using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPrint.Agent;
using SkiaSharp;

namespace SmartPrint.Agent.Tests;

/// <summary>
/// Runs only in the Windows CI job after a real Android IppVirtual receiver
/// has been booted, forwarded to localhost, and installed as an IPP queue.
/// This deliberately exercises the production GDI/spooler executor, not a
/// mock IPP server or a synthetic spooler API.
/// </summary>
public sealed class AndroidVirtualIppSpoolerIntegrationTests
{
    [Fact]
    public void SmartPrintWindowsExecutorSubmitsDocumentToAndroidIppVirtualReceiver()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SMARTPRINT_RUN_ANDROID_IPP_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
            return;

        Assert.True(OperatingSystem.IsWindows(), "The Android IPP receiver integration requires the real Windows spooler.");

        var queueName = Environment.GetEnvironmentVariable("SMARTPRINT_ANDROID_IPP_QUEUE");
        Assert.False(string.IsNullOrWhiteSpace(queueName), "SMARTPRINT_ANDROID_IPP_QUEUE must identify the temporary Android IPP queue.");

        // Verify the real installed queue is discovered as a virtual output,
        // not accidentally promoted to a physical printer by the Agent.
        var discovery = new WindowsPrinterDiscovery(
            NullLogger<WindowsPrinterDiscovery>.Instance,
            executeInCurrentProcess: true);
        var inventory = discovery.DiscoverInProcessAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(inventory.Success, "Windows Agent queue discovery failed for the installed Android IPP queue.");
        var discoveredQueue = Assert.Single(inventory.Printers, item =>
            string.Equals(item.QueueName, queueName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PrinterOutputModeRules.UnsupportedVirtual, discoveredQueue.OutputMode);
        Assert.True(discoveredQueue.DriverName.Contains("IPP", StringComparison.OrdinalIgnoreCase));

        using var bitmap = new SKBitmap(640, 840);
        bitmap.Erase(SKColors.White);
        using (var canvas = new SKCanvas(bitmap))
        using (var accent = new SKPaint { Color = new SKColor(25, 105, 180), IsAntialias = true })
        using (var pale = new SKPaint { Color = SKColors.LightGray, IsAntialias = true })
        using (var dark = new SKPaint { Color = SKColors.DarkSlateGray, IsAntialias = true })
        {
            // Deliberately use vector shapes only: this is a transport/driver
            // test, not a font-rendering test, and remains stable across SDKs.
            canvas.DrawRect(new SKRect(24, 24, 616, 64), dark);
            canvas.DrawRect(new SKRect(24, 88, 220, 300), accent);
            canvas.DrawRect(new SKRect(244, 88, 616, 300), pale);
            canvas.DrawRect(new SKRect(24, 324, 616, 364), accent);
            for (var index = 0; index < 8; index++)
            {
                var left = 24 + (index * 74);
                var shade = new SKPaint
                {
                    Color = new SKColor((byte)(index * 30), (byte)(255 - index * 28), (byte)(index * 18)),
                    IsAntialias = true
                };
                using (shade)
                    canvas.DrawRect(new SKRect(left, 400, left + 62, 500), shade);
            }
            canvas.DrawRect(new SKRect(24, 540, 616, 580), dark);
            canvas.DrawRect(new SKRect(24, 604, 616, 804), pale);
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        Assert.NotNull(encoded);
        var bytes = encoded!.ToArray();
        var jobId = Guid.NewGuid();
        var documentName = "SmartPrint-Android-IPP-E2E-" + jobId.ToString("N");
        var now = DateTimeOffset.UtcNow;
        var entry = new AgentLocalQueueEntry(
            jobId,
            "SP-ANDROID-IPP-E2E",
            Guid.NewGuid(),
            "Android IppVirtual CI Receiver",
            queueName!,
            "smartprint-android-ipp-e2e.png",
            "image/png",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength,
            1,
            1,
            "Colour",
            "A4",
            false,
            "INR",
            100,
            "ci-android-ipp-diagnostic.png",
            "ACKNOWLEDGED",
            now,
            now.AddMinutes(5),
            SpoolerJobName: documentName)
        {
            OutputMode = PrinterOutputModeRules.UnsupportedVirtual
        };

        try
        {
            var executor = new WindowsPrintExecutor();
            var authorizationBoundaryCalls = 0;
            using var document = new MemoryStream(bytes, writable: false);

            // This is the same final pre-StartDoc callback boundary used by the
            // isolated Agent worker. The live server-grant protocol is separately
            // covered by the authorization/pipe tests in this test project.
            var receipt = executor.Print(
                entry,
                document,
                CancellationToken.None,
                () => Interlocked.Increment(ref authorizationBoundaryCalls));

            Assert.True(receipt.JobId > 0, "Windows did not return a real spooler job ID.");
            Assert.Equal(1, authorizationBoundaryCalls);
            Assert.Equal(documentName, receipt.DocumentName);

            WindowsSpoolerJob? terminalJob = null;
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                var current = executor.GetJob(queueName!, receipt.JobId);
                if (current is not null)
                {
                    if (current.Status is "ERROR" or "OFFLINE" or "PAPEROUT" or "USERINTERVENTION")
                        Assert.True(false, $"Windows spooler reported {current.Status} for Android IPP job {receipt.JobId}.");

                    if ((current.Status is "PRINTED" or "RETAINED") && current.PagesPrinted >= 1)
                    {
                        terminalJob = current;
                        break;
                    }
                }

                Thread.Sleep(500);
            }

            Assert.NotNull(
                terminalJob,
                $"Windows did not observe a completed Android IPP spooler job. Queue={queueName}; jobId={receipt.JobId}.");
            Assert.True(terminalJob!.PagesPrinted >= 1, "The Windows spooler did not report a printed page.");
            Assert.True(terminalJob.TotalPages >= 1, "The Windows spooler did not report the expected page count.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
