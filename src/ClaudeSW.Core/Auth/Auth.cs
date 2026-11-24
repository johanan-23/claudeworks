using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeSW.Core.Auth
{
    /// <summary>What a model client needs to authenticate a request.</summary>
    public sealed class AuthCredentials
    {
        public string ApiKey { get; set; }
        public override string ToString() => "AuthCredentials(***)"; // never leak secrets into logs
    }

    public sealed class AuthResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public static AuthResult Success() => new AuthResult { Ok = true };
        public static AuthResult Fail(string error) => new AuthResult { Ok = false, Error = error };
    }

    /// <summary>
    /// Swappable authentication provider. Today only <see cref="ApiKeyAuthProvider"/> ships:
    /// Anthropic does not permit third-party apps to offer claude.ai (subscription) sign-in
    /// without prior approval, so there is deliberately no "Sign in with Claude" provider.
    /// </summary>
    public interface IAuthProvider
    {
        string Id { get; }
        string DisplayName { get; }
        bool IsSignedIn { get; }
        /// <summary>Masked hint safe to show in the UI, e.g. "sk-ant-…9f2c".</summary>
        string Hint { get; }
        Task<AuthResult> SignInAsync(string credential, CancellationToken ct);
        void SignOut();
        /// <summary>Returns credentials or null if not signed in.</summary>
        AuthCredentials GetCredentials();
    }

    /// <summary>Persists small secrets for the current Windows user.</summary>
    public interface ISecretStore
    {
        void Save(string name, string secret);
        string Load(string name);
        void Delete(string name);
    }

    /// <summary>Validates a credential against the service (e.g. GET /v1/models).</summary>
    public interface ICredentialValidator
    {
        Task<AuthResult> ValidateAsync(AuthCredentials credentials, CancellationToken ct);
    }

    /// <summary>DPAPI (CurrentUser scope) encrypted files under %APPDATA%\ClaudeForSolidWorks\secrets.</summary>
    public sealed class DpapiSecretStore : ISecretStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClaudeForSolidWorks.v1");
        private readonly string _dir;

        public DpapiSecretStore(string directory) { _dir = directory; }

        private string PathFor(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(_dir, name + ".bin");
        }

        public void Save(string name, string secret)
        {
            Directory.CreateDirectory(_dir);
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(PathFor(name), data);
        }

        public string Load(string name)
        {
            var p = PathFor(name);
            if (!File.Exists(p)) return null;
            try
            {
                var data = ProtectedData.Unprotect(File.ReadAllBytes(p), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (CryptographicException) { return null; } // other user / corrupted
        }

        public void Delete(string name)
        {
            var p = PathFor(name);
            if (File.Exists(p)) File.Delete(p);
        }
    }

    /// <summary>In-memory store for tests.</summary>
    public sealed class MemorySecretStore : ISecretStore
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _d = new System.Collections.Generic.Dictionary<string, string>();
        public void Save(string name, string secret) => _d[name] = secret;
        public string Load(string name) => _d.TryGetValue(name, out var v) ? v : null;
        public void Delete(string name) => _d.Remove(name);
    }

    /// <summary>Anthropic Console API key, validated once on sign-in and stored with DPAPI.</summary>
    public sealed class ApiKeyAuthProvider : IAuthProvider
    {
        internal const string SecretName = "anthropic_api_key";
        private readonly ISecretStore _store;
        private readonly ICredentialValidator _validator;
        private string _cached;

        public ApiKeyAuthProvider(ISecretStore store, ICredentialValidator validator)
        {
            _store = store;
            _validator = validator;
            _cached = _store.Load(SecretName);
        }

        public string Id => "api_key";
        public string DisplayName => "Anthropic API key";
        public bool IsSignedIn => !string.IsNullOrEmpty(_cached);
        public string Hint => Mask(_cached);

        public static string Mask(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var tail = key.Length > 4 ? key.Substring(key.Length - 4) : "";
            var head = key.StartsWith("sk-ant-", StringComparison.Ordinal) ? "sk-ant-" : "";
            return head + "…" + tail;
        }

        /// <summary>Basic shape check so obvious paste mistakes fail fast without a network call.</summary>
        public static string ShapeError(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "Paste your API key.";
            if (key.Trim().Length < 20) return "That key looks too short.";
            if (key.Trim().IndexOfAny(new[] { ' ', '\n', '\r', '\t' }) >= 0) return "The key must not contain spaces.";
            return null;
        }

        public async Task<AuthResult> SignInAsync(string credential, CancellationToken ct)
        {
            var key = (credential ?? "").Trim();
            var shape = ShapeError(key);
            if (shape != null) return AuthResult.Fail(shape);
            var result = _validator == null
                ? AuthResult.Success()
                : await _validator.ValidateAsync(new AuthCredentials { ApiKey = key }, ct).ConfigureAwait(false);
            if (!result.Ok) return result;
            _store.Save(SecretName, key);
            _cached = key;
            return AuthResult.Success();
        }

        public void SignOut()
        {
            _store.Delete(SecretName);
            _cached = null;
        }

        public AuthCredentials GetCredentials() => IsSignedIn ? new AuthCredentials { ApiKey = _cached } : null;
    }
}
