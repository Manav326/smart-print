using System.Security.Cryptography;
using System.Text;

namespace SmartPrint.Api.Services;

public static class DataTransferIdentity
{
    public static Guid ForCustomerUpload(Guid tenantId, Guid sessionId, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (tenantId == Guid.Empty || sessionId == Guid.Empty)
            throw new ArgumentException("A tenant and customer session are required for a transfer identity.");

        var identity = Encoding.UTF8.GetBytes($"{tenantId:N}:{sessionId:N}:{idempotencyKey}");
        try
        {
            return new Guid(SHA256.HashData(identity).AsSpan(0, 16));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identity);
        }
    }
}
