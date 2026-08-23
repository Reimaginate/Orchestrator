using System.Text.Json;

namespace Reimaginate.Orchestrator.SourceGenerator.CatalogTool;

internal sealed class CatalogFileStore
{
    internal const string WarningCode = "ORCHCAT001";
    private const int MaxIoRetryAttempts = 3;
    private const string GeneratedAtUtcPropertyName = "generatedAtUtc";

    public async Task<CatalogWriteResult> WriteCatalogAsync(
        string catalogPath,
        string serializedCatalog,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaxIoRetryAttempts; attempt += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tempPath = catalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                var existingContent = await TryReadAllTextAsync(catalogPath, cancellationToken);
                if (existingContent is not null && CatalogContentMatches(existingContent, serializedCatalog))
                {
                    File.SetLastWriteTimeUtc(catalogPath, DateTime.UtcNow);
                    return CatalogWriteResult.Success();
                }

                await File.WriteAllTextAsync(tempPath, serializedCatalog, cancellationToken);

                if (File.Exists(catalogPath))
                {
                    File.Move(tempPath, catalogPath, overwrite: true);
                }
                else
                {
                    File.Move(tempPath, catalogPath);
                }

                return CatalogWriteResult.Success();
            }
            catch (Exception ex) when (IsRetryableCatalogIoException(ex))
            {
                TryDeleteTempFile(tempPath);

                if (attempt == MaxIoRetryAttempts)
                {
                    return CatalogWriteResult.Warning(
                        WarningCode,
                        $"Workflow action catalog emission skipped because '{catalogPath}' is locked by another process.");
                }

                await Task.Delay(GetRetryDelay(attempt), cancellationToken);
            }
            catch
            {
                TryDeleteTempFile(tempPath);
                throw;
            }
        }

        return CatalogWriteResult.Success();
    }

    public async Task<CatalogWriteResult> DeleteCatalogIfExistsAsync(
        string catalogPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(catalogPath))
        {
            return CatalogWriteResult.Success();
        }

        for (var attempt = 1; attempt <= MaxIoRetryAttempts; attempt += 1)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!File.Exists(catalogPath))
                {
                    return CatalogWriteResult.Success();
                }

                File.Delete(catalogPath);
                return CatalogWriteResult.Success();
            }
            catch (Exception ex) when (IsRetryableCatalogIoException(ex))
            {
                if (attempt == MaxIoRetryAttempts)
                {
                    return CatalogWriteResult.Warning(
                        WarningCode,
                        $"Workflow action catalog cleanup skipped because '{catalogPath}' is locked by another process.");
                }

                await Task.Delay(GetRetryDelay(attempt), cancellationToken);
            }
        }

        return CatalogWriteResult.Success();
    }

    private static async Task<string?> TryReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    private static bool CatalogContentMatches(string existingContent, string newContent)
    {
        if (string.Equals(existingContent, newContent, StringComparison.Ordinal))
        {
            return true;
        }

        return TryNormalizeCatalogJson(existingContent, out var normalizedExisting)
               && TryNormalizeCatalogJson(newContent, out var normalizedNew)
               && string.Equals(normalizedExisting, normalizedNew, StringComparison.Ordinal);
    }

    private static bool TryNormalizeCatalogJson(string content, out string normalizedContent)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                normalizedContent = string.Empty;
                return false;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, GeneratedAtUtcPropertyName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            normalizedContent = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (JsonException)
        {
            normalizedContent = string.Empty;
            return false;
        }
    }

    private static bool IsRetryableCatalogIoException(Exception ex)
    {
        return ex switch
        {
            IOException ioEx => IsRetryableHResult(ioEx.HResult) || IsLockMessage(ioEx.Message),
            UnauthorizedAccessException unauthorizedAccessEx => IsRetryableHResult(unauthorizedAccessEx.HResult) || IsLockMessage(unauthorizedAccessEx.Message),
            _ => false
        };
    }

    private static bool IsRetryableHResult(int hresult)
    {
        var errorCode = hresult & 0xFFFF;
        return errorCode is 5 or 32 or 33;
    }

    private static bool IsLockMessage(string message)
        => message.Contains("used by another process", StringComparison.OrdinalIgnoreCase)
           || message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
           || message.Contains("because it is being used", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan GetRetryDelay(int attempt)
        => TimeSpan.FromMilliseconds(50 * attempt);

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
        }
    }
}
