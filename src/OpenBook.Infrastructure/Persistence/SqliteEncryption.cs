using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenBook.Domain.Common;

namespace OpenBook.Infrastructure.Persistence;

/// <summary>
/// Converts a company file between plain SQLite and SQLCipher encryption with <c>sqlcipher_export</c>, which
/// copies the schema (triggers included) and data into a new file. Run with OpenBook stopped.
/// </summary>
public static class SqliteEncryption
{
    /// <summary>True when the file opens without a key, i.e. it's plain SQLite.</summary>
    public static bool IsPlain(string path)
    {
        var header = new byte[16];
        using (var file = File.OpenRead(path))
        {
            if (file.Read(header, 0, header.Length) < header.Length)
                return false;
        }

        return System.Text.Encoding.ASCII.GetString(header) == "SQLite format 3\0";
    }

    /// <summary>Encrypts a plain company file in place with <paramref name="key"/>.</summary>
    /// <returns>Where the original, unencrypted file was kept. Delete it once the encrypted file works.</returns>
    public static Task<string> EncryptAsync(string path, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new DomainException("encryption.key_required", "Set OpenBook:EncryptionKey first.");
        if (!IsPlain(path))
            throw new DomainException("encryption.already_encrypted", "That file is already encrypted (or isn't a database).");
        return ConvertAsync(path, sourceKey: null, targetKey: key, "unencrypted", ct);
    }

    /// <summary>Decrypts a company file in place, e.g. to stop using encryption or to change the key.</summary>
    /// <returns>Where the original, encrypted file was kept.</returns>
    public static Task<string> DecryptAsync(string path, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new DomainException("encryption.key_required", "Set OpenBook:EncryptionKey to the file's current key.");
        if (IsPlain(path))
            throw new DomainException("encryption.not_encrypted", "That file isn't encrypted.");
        return ConvertAsync(path, sourceKey: key, targetKey: string.Empty, "encrypted", ct);
    }

    private static async Task<string> ConvertAsync(string path, string? sourceKey, string targetKey, string keptAs, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        var temporary = full + ".converting";
        File.Delete(temporary);

        // ReadWriteCreate: attached databases inherit the open flags, and the target must be created.
        var source = new SqliteConnectionStringBuilder { DataSource = full, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate };
        if (sourceKey is not null)
            source.Password = sourceKey;
        await using (var connection = new SqliteConnection(source.ToString()))
        {
            try
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    ATTACH DATABASE $path AS target KEY $key;
                    SELECT sqlcipher_export('target');
                    DETACH DATABASE target;
                    """;
                command.Parameters.AddWithValue("$path", temporary);
                command.Parameters.AddWithValue("$key", targetKey);
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException ex)
            {
                File.Delete(temporary);
                throw new DomainException("encryption.failed", $"The file couldn't be converted (wrong key, or OpenBook is still running?): {ex.Message}");
            }
        }

        SqliteConnection.ClearAllPools();
        var kept = $"{full}.{keptAs}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.bak";
        File.Move(full, kept);
        File.Move(temporary, full);
        return kept;
    }
}
