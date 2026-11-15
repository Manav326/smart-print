using System.Diagnostics;
using SmartPrint.Documents;

namespace SmartPrint.Agent;

/// <summary>
/// Scans a local-LAN upload with the Windows Defender engine before a document parser
/// or renderer is allowed to consume it. A missing, disabled or failing scanner blocks
/// the upload rather than silently accepting an unscanned document.
/// </summary>
public sealed class WindowsDefenderDocumentThreatScanner
{
    private const int MaximumDocumentBytes = 20 * 1024 * 1024;
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(120);

    public async Task ScanAsync(
        ReadOnlyMemory<byte> document,
        string extension,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new DocumentThreatScanException(
                "Windows Defender scanning is available only on the Windows shop Agent.",
                threatDetected: false);
        }

        if (document.IsEmpty || document.Length > MaximumDocumentBytes ||
            extension is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
        {
            throw new DocumentThreatScanException(
                "The document is outside the supported local security-scanning limits.",
                threatDetected: false);
        }

        var scannerPath = FindMpCmdRun();
        if (scannerPath is null)
        {
            throw new DocumentThreatScanException(
                "Microsoft Defender Antivirus is unavailable. The local upload was blocked.",
                threatDetected: false);
        }

        var scanDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "QRtoPrint",
            "SecurityScans");
        Directory.CreateDirectory(scanDirectory);
        SecureScanDirectory(scanDirectory);
        RemoveStaleScanFiles(scanDirectory);
        var scanPath = Path.Combine(scanDirectory, Guid.NewGuid().ToString("N") + extension);

        try
        {
            await File.WriteAllBytesAsync(scanPath, document, cancellationToken);

            var startInfo = new ProcessStartInfo(scannerPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(scannerPath)!
            };
            startInfo.ArgumentList.Add("-Scan");
            startInfo.ArgumentList.Add("-ScanType");
            startInfo.ArgumentList.Add("3");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scanPath);
            // Do not let an on-demand scan silently remove or modify the staged source.
            // With remediation disabled, Defender reports detections to this process.
            startInfo.ArgumentList.Add("-DisableRemediation");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Windows Defender scan process did not start.");
            }

            // Do not throttle Defender to an arbitrary percentage. Let Windows
            // schedule the scan below normal print/spooler priority and consume
            // available CPU when the shop PC is otherwise idle.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(ScanTimeout);
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("Windows Defender did not finish scanning the document within two minutes.");
            }

            var output = (await standardOutput) + Environment.NewLine + (await standardError);
            var decision = ClassifyResult(process.ExitCode, output);
            if (decision is null)
            {
                return;
            }

            throw decision;
        }
        catch (DocumentThreatScanException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DocumentThreatScanException(
                "Microsoft Defender could not complete the document scan. The local upload was blocked.",
                threatDetected: false,
                innerException: exception);
        }
        finally
        {
            try
            {
                if (File.Exists(scanPath)) File.Delete(scanPath);
            }
            catch (IOException)
            {
                // Do not make a failed cleanup turn an accepted upload into a false success;
                // the restricted SecurityScans directory is swept on service startup.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }



    /// <summary>
    /// MpCmdRun returns 2 for both detections and scanning errors. With
    /// -DisableRemediation, a detection is printed as a Threat information block.
    /// Only an explicit clean result with exit code 0 may be accepted.
    /// </summary>
    public static DocumentThreatScanException? ClassifyResult(int exitCode, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new DocumentThreatScanException(
                "Microsoft Defender returned no scan result. The local upload was blocked.",
                threatDetected: false);
        }

        var threatReported = System.Text.RegularExpressions.Regex.IsMatch(
            output,
            @"(?im)^\s*Threat\s*:\s*\S+");

        if (threatReported)
        {
            return new DocumentThreatScanException(
                "Microsoft Defender detected a threat. The local upload was rejected.",
                threatDetected: true);
        }

        // The English CLI reports a clean custom scan as:
        // "Scanning <path> found no threats."
        // Unknown/localized/indeterminate output is intentionally fail-closed.
        var explicitCleanResult = System.Text.RegularExpressions.Regex.IsMatch(
            output,
            @"(?im)^\s*Scanning\s+.+\s+found\s+no\s+threats?\s*\.\s*$");

        if (exitCode == 0 && explicitCleanResult && !threatReported)
        {
            return null;
        }

        return new DocumentThreatScanException(
            "Microsoft Defender could not confirm that the document is safe. The local upload was blocked.",
            threatDetected: false);
    }

    private static void SecureScanDirectory(string directory)
    {
        var systemSid = new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
        var serviceSid = System.Security.Principal.WindowsIdentity.GetCurrent().User;

        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administratorsSid);
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            systemSid,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit |
            System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            administratorsSid,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit |
            System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));
        if (serviceSid is not null && serviceSid != systemSid && serviceSid != administratorsSid)
        {
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                serviceSid,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
        }
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void RemoveStaleScanFiles(string directory)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // A busy/locked scan file is not reused; the current scan uses a fresh GUID path.
            }
            catch (UnauthorizedAccessException)
            {
                // The scan fails closed if the current file cannot be created below.
            }
        }
    }

    private static string? FindMpCmdRun()
    {
        var platformRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft",
            "Windows Defender",
            "Platform");

        if (Directory.Exists(platformRoot))
        {
            var latest = Directory.EnumerateDirectories(platformRoot)
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.Combine(path, "MpCmdRun.exe"))
                .FirstOrDefault(File.Exists);
            if (latest is not null) return latest;
        }

        var inbox = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Windows Defender",
            "MpCmdRun.exe");
        return File.Exists(inbox) ? inbox : null;
    }
}
