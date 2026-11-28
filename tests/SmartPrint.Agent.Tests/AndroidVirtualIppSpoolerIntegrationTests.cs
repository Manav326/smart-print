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
        using (var heading = new SKPaint { Color = SKColors.Black, TextSize = 34, IsAntialias = true, Typeface = SKTypeface.Default })
        using (var body = new SKPaint { Color = SKColors.DarkSlateGray, TextSize = 22, IsAntialias = true, Typeface = SKTypeface.Default })
        using (var accent = new SKPaint { Color = new SKColor(25, 105, 180), IsAntialias = true })
        using (var pale = new SKPaint { Color = SKColors.LightGray, IsAntialias = true })
        {
            canvas.DrawText("SMARTPRINT IPP RECEIVER TEST", 32, 70, heading);
            canvas.DrawText("Windows Agent -> Windows spooler -> IPP -> Android", 32, 120, body);
            canvas.DrawText("This is a controlled CI diagnostic page.", 32, 165, body);
            canvas.DrawRect(new SKRect(32, 210, 608, 250), accent);
            canvas.DrawRect(new SKRect(32, 280, 220, 430), accent);
            canvas.DrawRect(new SKRect(250, 280, 608, 430), pale);
            canvas.DrawText("END-TO-END RECEIPT", 32, 510, heading);
            canvas.DrawText("Unique document: " + Guid.NewGuid().ToString("N"), 32, 555, body);
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
