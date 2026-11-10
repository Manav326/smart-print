using System.Security.Cryptography;
using SmartPrint.Agent;

namespace SmartPrint.Agent.Tests;

public sealed class AgentDocumentEncryptionKeyStoreTests
{
    [Fact]
    public void PublicKeyWrapsPerUploadAesKeyAndPersistsAcrossStoreInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartprint-private-key-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "agent-document-key.dpapi");
        var aesKey = RandomNumberGenerator.GetBytes(32);
        byte[]? wrapped = null;
        byte[]? recovered = null;
        try
        {
            var firstStore = new AgentDocumentEncryptionKeyStore(path);
            var firstPublicKey = firstStore.GetOrCreatePublicKey();
            using (var publicRsa = RSA.Create())
            {
                publicRsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(firstPublicKey), out _);
                wrapped = publicRsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);
            }

            recovered = firstStore.DecryptWrappedAesKey(Convert.ToBase64String(wrapped));
            Assert.Equal(aesKey, recovered);

            var secondStore = new AgentDocumentEncryptionKeyStore(path);
            var secondPublicKey = secondStore.GetOrCreatePublicKey();
            Assert.Equal(firstPublicKey, secondPublicKey);
            var secondRecovered = secondStore.DecryptWrappedAesKey(Convert.ToBase64String(wrapped));
            try { Assert.Equal(aesKey, secondRecovered); }
            finally { CryptographicOperations.ZeroMemory(secondRecovered); }

            Assert.True(File.Exists(path));
            var protectedPrivateKey = File.ReadAllBytes(path);
            try
            {
                Assert.NotEmpty(protectedPrivateKey);
                Assert.DoesNotContain(aesKey, protectedPrivateKey);
            }
            finally { CryptographicOperations.ZeroMemory(protectedPrivateKey); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
            if (wrapped is not null) CryptographicOperations.ZeroMemory(wrapped);
            if (recovered is not null) CryptographicOperations.ZeroMemory(recovered);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InvalidWrappedKeyIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartprint-private-key-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new AgentDocumentEncryptionKeyStore(Path.Combine(root, "agent-document-key.dpapi"));
            Assert.Throws<CryptographicException>(() => store.DecryptWrappedAesKey("not-base64"));
            Assert.Throws<CryptographicException>(() => store.DecryptWrappedAesKey(string.Empty));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
