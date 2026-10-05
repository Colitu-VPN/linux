using System.Diagnostics;
using Process = System.Diagnostics.Process;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace v2rayN.Desktop.Services;

/// <summary>
/// In-app live support against the panel's <c>/api/v1/support</c> API:
/// conversations, messages with attachments, automatic diagnostics and the
/// unread counter that drives the nav badge and tray notification.
/// </summary>
public sealed partial class ColituSupportService
{
    public static ColituSupportService Instance { get; } = new();

    public const int MaxFileBytes = 10 << 20;
    public const int MaxFilesPerMessage = 5;

    /// <summary>Largest attachment the app downloads (support files are at most 10 MB; replies from the team a bit more).</summary>
    private const long MaxDownloadBytes = 50L << 20;

    /// <summary>File types the panel stores (it re-checks the content itself).</summary>
    public static readonly string[] AllowedExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".pdf", ".txt", ".log", ".zip", ".json", ".gz"];

    private readonly ColituAuthService _auth = ColituAuthService.Instance;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private ColituSupportService() { }

    /// <summary>Where opened attachments are saved; the file manager shows this folder, nothing is executed.</summary>
    public static string DownloadFolder => Path.Combine(Utils.GetTempPath(), "ColituSupport");

    public async Task<List<ColituSupportConversation>> ListAsync()
    {
        using var response = await _auth.SendAuthorizedRequestAsync(() => new HttpRequestMessage(HttpMethod.Get, _auth.ApiUri("/support/conversations")));
        return (await _auth.ReadResponseJsonAsync<Envelope<List<ColituSupportConversation>>>(response))?.Data ?? [];
    }

    public async Task<ColituSupportThread> ThreadAsync(string id)
    {
        using var response = await _auth.SendAuthorizedRequestAsync(() => new HttpRequestMessage(HttpMethod.Get, _auth.ApiUri($"/support/conversations/{Uri.EscapeDataString(id)}")));
        return (await _auth.ReadResponseJsonAsync<Envelope<ColituSupportThread>>(response))?.Data ?? new();
    }

    public async Task<int> UnreadAsync()
    {
        using var response = await _auth.SendAuthorizedRequestAsync(() => new HttpRequestMessage(HttpMethod.Get, _auth.ApiUri("/support/unread")));
        return (await _auth.ReadResponseJsonAsync<Envelope<UnreadDto>>(response))?.Data?.Unread ?? 0;
    }

    public async Task<ColituSupportConversation?> CreateAsync(string subject, string message, IReadOnlyList<string> files, ColituSupportDiagnostics? diagnostics)
    {
        var payload = new { subject, message, locale = Loc.I.Language, diagnostics };
        using var response = await _auth.SendAuthorizedRequestAsync(() => Multipart(HttpMethod.Post, "/support/conversations", payload, files));
        return (await _auth.ReadResponseJsonAsync<Envelope<ColituSupportConversation>>(response))?.Data;
    }

    public async Task<ColituSupportMessage?> ReplyAsync(string conversationId, string body, IReadOnlyList<string> files)
    {
        using var response = await _auth.SendAuthorizedRequestAsync(() => Multipart(HttpMethod.Post, $"/support/conversations/{Uri.EscapeDataString(conversationId)}/messages", new { body }, files));
        return (await _auth.ReadResponseJsonAsync<Envelope<ColituSupportMessage>>(response))?.Data;
    }

    /// <summary>Downloads an attachment into the temp folder and returns its path.</summary>
    public async Task<string> DownloadAsync(ColituSupportAttachment attachment)
    {
        using var response = await _auth.SendAuthorizedRequestAsync(() => new HttpRequestMessage(HttpMethod.Get, _auth.ApiUri($"/support/attachments/{Uri.EscapeDataString(attachment.Id)}")));
        var folder = DownloadFolder;
        Directory.CreateDirectory(folder);
        var name = string.Concat(Path.GetFileName(attachment.FileName ?? "file").Split(Path.GetInvalidFileNameChars()));
        // The id comes from the server: only letters, digits and '-' reach the file name.
        var idPart = new string(attachment.Id.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-').Take(8).ToArray());
        var path = Path.Combine(folder, $"{(idPart.Length == 0 ? "att" : idPart)}-{(name.Length == 0 ? "file" : name)}");
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            throw new InvalidOperationException("Attachment is too large.");
        }
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var file = File.Create(path))
        {
            // The server's size header is not trusted: stop writing past the limit.
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                written += read;
                if (written > MaxDownloadBytes)
                {
                    throw new InvalidOperationException("Attachment is too large.");
                }
                await file.WriteAsync(buffer.AsMemory(0, read));
            }
        }
        return path;
    }

    public async Task<byte[]> DownloadBytesAsync(string attachmentId)
    {
        using var response = await _auth.SendAuthorizedRequestAsync(() => new HttpRequestMessage(HttpMethod.Get, _auth.ApiUri($"/support/attachments/{Uri.EscapeDataString(attachmentId)}")));
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            throw new InvalidOperationException("Attachment is too large.");
        }
        // Same limit as DownloadAsync, enforced while reading: the size header is not trusted.
        await using var source = await response.Content.ReadAsStreamAsync();
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            if (memory.Length + read > MaxDownloadBytes)
            {
                throw new InvalidOperationException("Attachment is too large.");
            }
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    /// <summary>Returns a localized reason when a file cannot be attached, otherwise null.</summary>
    public static string? CheckFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes || !AllowedExtensions.Contains(info.Extension.ToLowerInvariant()))
            {
                return Loc.I["support.err.file"];
            }
            return null;
        }
        catch
        {
            return Loc.I["support.err.file"];
        }
    }

    private HttpRequestMessage Multipart(HttpMethod method, string path, object payload, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return new HttpRequestMessage(method, _auth.ApiUri(path))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }

        var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload");
        foreach (var file in files)
        {
            var content = new ByteArrayContent(File.ReadAllBytes(file));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "file", Path.GetFileName(file));
        }
        return new HttpRequestMessage(method, _auth.ApiUri(path)) { Content = form };
    }

    /// <summary>
    /// What support needs to reproduce a problem: versions, connection mode and
    /// state, the last error and the tail of today's log. Credentials inside
    /// share links are masked, and core lines about user traffic are removed
    /// with other host names masked (<see cref="ColituLogPrivacy"/>), so the
    /// sites a user visited never reach the panel.
    /// </summary>
    public static ColituSupportDiagnostics CollectDiagnostics()
    {
        var vpn = ColituVpnService.Instance;
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(vpn.LastError))
        {
            errors.Add(Redact(vpn.LastError!));
        }
        var server = vpn.ConnectedServer;
        return new ColituSupportDiagnostics
        {
            Platform = ColituAuthService.ClientPlatform,
            AppVersion = ColituAuthService.ClientVersion,
            Os = $"{LinuxDistribution()} · {Environment.OSVersion.VersionString} ({RuntimeInformation.OSArchitecture})",
            Device = "Linux PC",
            Network = vpn.Preferences.IsTunMode ? "tun" : "proxy",
            Server = server == null ? (vpn.IsAutoSelection ? "auto" : vpn.SavedServerId) : $"{server.Country ?? server.CountryCode} {server.City} ({server.Id})".Trim(),
            Protocol = vpn.ConnectedProtocol,
            Connected = vpn.Status == ColituVpnStatus.Connected,
            LastErrors = errors,
            Logs = ReadLogTail()
        };
    }

    /// <summary>PRETTY_NAME from /etc/os-release ("Ubuntu 24.04.1 LTS").</summary>
    private static string LinuxDistribution()
    {
        try
        {
            var line = File.ReadLines("/etc/os-release").FirstOrDefault(item => item.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
            return line == null ? "Linux" : line["PRETTY_NAME=".Length..].Trim('"');
        }
        catch
        {
            return "Linux";
        }
    }

    private static string? ReadLogTail(int maxChars = 24_000)
    {
        try
        {
            var latest = new DirectoryInfo(Utils.GetLogPath())
                .EnumerateFiles("*.txt")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest == null)
            {
                return null;
            }
            using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxChars * 2)
            {
                stream.Seek(-maxChars * 2, SeekOrigin.End);
            }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            if (text.Length > maxChars)
            {
                text = text[^maxChars..];
            }
            var firstLine = text.IndexOf('\n');
            if (firstLine > 0 && firstLine < 400)
            {
                text = text[(firstLine + 1)..];
            }
            return Redact(text);
        }
        catch
        {
            return null;
        }
    }

    public static string Redact(string text)
    {
        text = ColituLogPrivacy.StripTraffic(text);
        text = ShareLinkSecret().Replace(text, "$1***@");
        text = BearerToken().Replace(text, "Bearer ***");
        text = UrlCredentials().Replace(text, "$1***@");
        text = QuerySecret().Replace(text, "$1***");
        text = Email().Replace(text, "***@***");
        return JsonSecret().Replace(text, "$1\"***\"");
    }

    [GeneratedRegex(@"((?:vless|vmess|trojan|hysteria2|hy2|ss|tuic|socks|socks5)://)[^@\s/]+@", RegexOptions.IgnoreCase)]
    private static partial Regex ShareLinkSecret();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-_.=]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(""(?:password|uuid|id|auth|auth_str|psk|token|access_token|refresh_token|private_key|privateKey|publicKey|shortId|short_id)""\s*:\s*)""[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecret();

    /// <summary>user:password@ in http(s) URLs.</summary>
    [GeneratedRegex(@"(https?://)[^@\s/]+:[^@\s/]*@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentials();

    /// <summary>token=, key=, password=, pbk=, sid= ... in links and query strings.</summary>
    [GeneratedRegex(@"(\b(?:password|pass|pwd|token|access_token|refresh_token|key|auth|code|sig|pbk|sid|uuid)=)[^&\s""]+", RegexOptions.IgnoreCase)]
    private static partial Regex QuerySecret();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}\b")]
    private static partial Regex Email();

    private sealed class Envelope<T>
    {
        [JsonPropertyName("data")] public T? Data { get; set; }
    }

    private sealed class UnreadDto
    {
        [JsonPropertyName("unread")] public int Unread { get; set; }
    }
}

public sealed class ColituSupportDiagnostics
{
    [JsonPropertyName("device")] public string? Device { get; set; }
    [JsonPropertyName("os")] public string? Os { get; set; }
    [JsonPropertyName("app_version")] public string? AppVersion { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("network")] public string? Network { get; set; }
    [JsonPropertyName("server")] public string? Server { get; set; }
    [JsonPropertyName("protocol")] public string? Protocol { get; set; }
    [JsonPropertyName("connected")] public bool? Connected { get; set; }
    [JsonPropertyName("last_errors")] public List<string>? LastErrors { get; set; }
    [JsonPropertyName("logs")] public string? Logs { get; set; }
}

public sealed class ColituSupportConversation
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("unread")] public int Unread { get; set; }
    [JsonPropertyName("last_message")] public string? LastMessage { get; set; }
    [JsonPropertyName("last_message_at")] public DateTimeOffset LastMessageAt { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ColituSupportThread
{
    [JsonPropertyName("conversation")] public ColituSupportConversation? Conversation { get; set; }
    [JsonPropertyName("messages")] public List<ColituSupportMessage>? Messages { get; set; }
}

public sealed class ColituSupportMessage
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sender")] public string? Sender { get; set; }
    [JsonPropertyName("admin_name")] public string? AdminName { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("attachments")] public List<ColituSupportAttachment>? Attachments { get; set; }
}

public sealed class ColituSupportAttachment
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("content_type")] public string? ContentType { get; set; }
    [JsonPropertyName("size_bytes")] public long Size { get; set; }
    [JsonPropertyName("is_image")] public bool IsImage { get; set; }
}
