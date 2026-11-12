using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AutoTranslator.Core;

namespace AutoTranslator.Desktop;

/// <summary>
/// Stores the Anthropic API key encrypted with Windows DPAPI (current user) in
/// %AppData%/AutoTranslator/claude.key. Only the Windows account that saved the key can read it.
/// Thread-safe; never throws to callers and never writes the key to the log.
/// </summary>
internal sealed class ApiKeyStore
{
    // Not a secret: only makes the blob useless to other DPAPI users of the same account by accident.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AutoTranslator.ClaudeApiKey.v1");

    private readonly object _gate = new object();
    private readonly string _path;
    private bool _loaded;
    private string? _key;

    public ApiKeyStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppSettings.DefaultDirectory, "claude.key");
    }

    /// <summary>True when a key is configured.</summary>
    public bool HasKey
    {
        get { return !string.IsNullOrEmpty(Get()); }
    }

    /// <summary>Returns the key, or null when none is stored (or it cannot be decrypted).</summary>
    public string? Get()
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                _key = Read();
                _loaded = true;
            }

            return _key;
        }
    }

    /// <summary>
    /// Stores the key; null or empty removes it. Returns false when the file could not be
    /// written or deleted (the new value is still used until the app exits).
    /// </summary>
    public bool Set(string? key)
    {
        string? value = string.IsNullOrWhiteSpace(key) ? null : key.Trim();

        lock (_gate)
        {
            _key = value;
            _loaded = true;

            try
            {
                if (value == null)
                {
                    if (File.Exists(_path))
                    {
                        File.Delete(_path);
                    }

                    return true;
                }

                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                byte[] plain = Encoding.UTF8.GetBytes(value);
                byte[] encrypted;
                try
                {
                    encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                }
                finally
                {
                    Array.Clear(plain, 0, plain.Length);
                }

                // Write to a temporary file first so that a crash cannot leave a half-written key.
                string temp = _path + ".tmp";
                File.WriteAllBytes(temp, encrypted);
                File.Move(temp, _path, true);
                return true;
            }
            catch (Exception ex)
            {
                // Only the exception type: messages must not end up containing key material.
                Log.Error("Saving the API key failed (" + ex.GetType().Name + ")");
                return false;
            }
        }
    }

    private string? Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            byte[] encrypted = File.ReadAllBytes(_path);
            if (encrypted.Length == 0)
            {
                return null;
            }

            byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                string value = Encoding.UTF8.GetString(plain).Trim();
                return value.Length > 0 ? value : null;
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }
        catch (Exception ex)
        {
            // E.g. the file was copied from another PC or Windows account.
            Log.Error("Reading the API key failed (" + ex.GetType().Name + ")");
            return null;
        }
    }
}
