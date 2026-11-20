using System.Net.Http;
using System.IO;
using System.Text;

namespace SmartPrint.Agent;

public sealed class LocalNetworkPreferenceStore
{
    private readonly string preferencePath;
    private readonly object sync = new();

    public LocalNetworkPreferenceStore(string? preferencePath = null)
    {
        this.preferencePath = string.IsNullOrWhiteSpace(preferencePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QRtoPrint", "local-network.preference")
            : Path.GetFullPath(preferencePath);
    }

    public string? GetPreferredNetworkId()
    {
        lock (sync)
        {
            if (!File.Exists(preferencePath)) return null;
            var value = File.ReadAllText(preferencePath, Encoding.UTF8).Trim();
            return value.Length is > 0 and <= 256 && !value.Contains('\n') && !value.Contains('\r') ? value : null;
        }
    }

    public void SetPreferredNetworkId(string networkId)
    {
        if (string.IsNullOrWhiteSpace(networkId) || networkId.Length > 256 ||
            networkId.Contains('\n') || networkId.Contains('\r'))
            throw new ArgumentException("Select an available shop connection.", nameof(networkId));

        lock (sync)
        {
            var directory = Path.GetDirectoryName(preferencePath)
                ?? throw new InvalidOperationException("Could not determine the network preference directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = preferencePath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, networkId, new UTF8Encoding(false));
                File.Move(temporaryPath, preferencePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }
}
