using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TL;
using WTelegram;

namespace Telegram.Export;

internal static partial class Program
{
    private const int PageSize = 100;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            var options = ExportOptions.Parse(args);
            if (options.ShowHelp)
            {
                ExportOptions.PrintHelp();
                return 0;
            }

            Directory.CreateDirectory(options.OutputDirectory);

            using var client = new Client(what =>
            {
                return what switch
                {
                    "api_id" => ReadSetting("TELEGRAM_API_ID", "API id", secret: false),
                    "api_hash" => ReadSetting("TELEGRAM_API_HASH", "API hash", secret: false),
                    "phone_number" => ReadSetting("TELEGRAM_PHONE", "Phone number", secret: false),
                    "verification_code" => ReadSetting("TELEGRAM_CODE", "Verification code", secret: false),
                    "password" => ReadSetting("TELEGRAM_PASSWORD", "Two-step password", secret: true),
                    "session_pathname" => Path.Combine(options.OutputDirectory, "telegram.session"),
                    _ => null
                };
            });

            var user = await client.LoginUserIfNeeded();
            Console.WriteLine($"Logged in as {user}");

            var dialogs = await client.Messages_GetAllDialogs();
            var target = ResolvePeer(dialogs, options.Chat);
            if (target is null)
            {
                PrintChats(dialogs);
                return 2;
            }

            var (peer, chatTitle) = target.Value;
            var mediaDirectory = Path.Combine(options.OutputDirectory, MakeSafeFileName(chatTitle));
            Directory.CreateDirectory(mediaDirectory);

            Console.WriteLine($"Exporting media from: {chatTitle}");
            Console.WriteLine($"Output directory: {mediaDirectory}");

            var manifestPath = Path.Combine(mediaDirectory, "media-manifest.jsonl");
            await using var manifest = new StreamWriter(
                new FileStream(manifestPath, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));

            var stats = await ExportMedia(client, peer, mediaDirectory, manifest, options);
            Console.WriteLine();
            Console.WriteLine($"Scanned {stats.MessagesScanned:N0} messages.");
            Console.WriteLine($"Downloaded {stats.Downloaded:N0} media files.");
            Console.WriteLine($"Skipped {stats.SkippedExisting:N0} existing files.");
            Console.WriteLine($"Failed {stats.Failed:N0} downloads.");
            Console.WriteLine($"Manifest: {manifestPath}");

            return stats.Failed == 0 ? 0 : 1;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            ExportOptions.PrintHelp();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<ExportStats> ExportMedia(
        Client client,
        InputPeer peer,
        string mediaDirectory,
        TextWriter manifest,
        ExportOptions options)
    {
        var stats = new ExportStats();
        var offsetId = options.StartOffsetId;

        while (true)
        {
            var history = await client.Messages_GetHistory(peer, offsetId, limit: PageSize, min_id: options.MinMessageId);
            var messages = history.Messages.OfType<Message>().ToArray();
            if (messages.Length == 0)
                break;

            foreach (var message in messages)
            {
                stats.MessagesScanned++;

                if (message.media is not { } media)
                    continue;

                var outputPath = GetOutputPath(mediaDirectory, message);
                if (outputPath is null)
                    continue;

                if (File.Exists(outputPath) && !options.Overwrite)
                {
                    stats.SkippedExisting++;
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    await DownloadMedia(client, media, output);

                    stats.Downloaded++;
                    await WriteManifest(manifest, message, outputPath, media);
                    Console.WriteLine($"[{message.ID}] {Path.GetFileName(outputPath)}");
                }
                catch (Exception ex)
                {
                    stats.Failed++;
                    Console.Error.WriteLine($"[{message.ID}] Failed: {ex.Message}");
                    if (!options.ContinueOnError)
                        throw;
                }
            }

            offsetId = history.Messages[^1].ID;

            if (options.MaxMessages > 0 && stats.MessagesScanned >= options.MaxMessages)
                break;
        }

        return stats;
    }

    private static Task DownloadMedia(Client client, MessageMedia media, Stream output)
    {
        return media switch
        {
            MessageMediaPhoto { photo: Photo photo } => client.DownloadFileAsync(photo, output),
            MessageMediaDocument { document: Document document } => client.DownloadFileAsync(document, output),
            _ => Task.CompletedTask
        };
    }

    private static string? GetOutputPath(string mediaDirectory, Message message)
    {
        var dateFolder = message.Date.ToLocalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var directory = Path.Combine(mediaDirectory, dateFolder);

        return message.media switch
        {
            MessageMediaPhoto { photo: Photo } => Path.Combine(directory, $"{message.ID:D10}.jpg"),
            MessageMediaDocument { document: Document document } => Path.Combine(directory, BuildDocumentFileName(message.ID, document)),
            _ => null
        };
    }

    private static string BuildDocumentFileName(int messageId, Document document)
    {
        var originalName = document.attributes?.OfType<DocumentAttributeFilename>().FirstOrDefault()?.file_name;
        var extension = Path.GetExtension(originalName);

        if (string.IsNullOrWhiteSpace(extension))
            extension = MimeTypeToExtension(document.mime_type);

        var stem = !string.IsNullOrWhiteSpace(originalName)
            ? Path.GetFileNameWithoutExtension(originalName)
            : GetDocumentKind(document);

        return $"{messageId:D10}_{MakeSafeFileName(stem)}{extension}";
    }

    private static string GetDocumentKind(Document document)
    {
        foreach (var attribute in document.attributes ?? [])
        {
            switch (attribute)
            {
                case DocumentAttributeVideo:
                    return "video";
                case DocumentAttributeAudio audio:
                    return audio.flags.HasFlag(DocumentAttributeAudio.Flags.voice) ? "voice" : "audio";
                case DocumentAttributeSticker:
                    return "sticker";
                case DocumentAttributeAnimated:
                    return "animation";
            }
        }

        return "document";
    }

    private static string MimeTypeToExtension(string? mimeType)
    {
        return mimeType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "video/mp4" => ".mp4",
            "audio/mpeg" => ".mp3",
            "audio/ogg" => ".ogg",
            "audio/mp4" => ".m4a",
            "application/pdf" => ".pdf",
            _ => ".bin"
        };
    }

    private static (InputPeer Peer, string Title)? ResolvePeer(Messages_Dialogs dialogs, string? chat)
    {
        if (string.IsNullOrWhiteSpace(chat))
            return null;

        var needle = chat.Trim().TrimStart('@');
        foreach (var chatBase in dialogs.chats.Values)
        {
            if (chatBase.ID.ToString(CultureInfo.InvariantCulture) == needle ||
                string.Equals(chatBase.MainUsername, needle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(chatBase.Title, chat, StringComparison.OrdinalIgnoreCase))
            {
                return (chatBase, chatBase.Title);
            }
        }

        foreach (var user in dialogs.users.Values)
        {
            var name = $"{user.first_name} {user.last_name}".Trim();
            if (user.ID.ToString(CultureInfo.InvariantCulture) == needle ||
                string.Equals(user.MainUsername, needle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(user.username, needle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, chat, StringComparison.OrdinalIgnoreCase))
            {
                return (user, name);
            }
        }

        return null;
    }

    private static void PrintChats(Messages_Dialogs dialogs)
    {
        Console.WriteLine("Pass --chat with one of these ids, usernames, or titles:");
        foreach (var chat in dialogs.chats.Values.OrderBy(c => c.Title))
            Console.WriteLine($"{chat.ID,14}  {chat.MainUsername,-24}  {chat.Title}");

        foreach (var user in dialogs.users.Values.OrderBy(u => u.first_name).ThenBy(u => u.last_name))
        {
            var name = $"{user.first_name} {user.last_name}".Trim();
            if (!string.IsNullOrWhiteSpace(name))
                Console.WriteLine($"{user.ID,14}  {user.username,-24}  {name}");
        }
    }

    private static Task WriteManifest(TextWriter writer, Message message, string outputPath, MessageMedia media)
    {
        var entry = new
        {
            message_id = message.ID,
            date = message.Date,
            file = outputPath,
            grouped_id = message.grouped_id,
            media_type = media.GetType().Name,
            text = message.message
        };

        return writer.WriteLineAsync(JsonSerializer.Serialize(entry));
    }

    private static string? ReadSetting(string environmentVariable, string prompt, bool secret)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        Console.Write($"{prompt}: ");
        return secret ? ReadSecret() : Console.ReadLine();
    }

    private static string ReadSecret()
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
            {
                buffer.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar))
                buffer.Append(key.KeyChar);
        }
    }

    private static string MakeSafeFileName(string? value)
    {
        var cleaned = InvalidFileNameCharsRegex().Replace(value ?? "untitled", "_").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "untitled" : cleaned;
    }

    [GeneratedRegex(@"[<>:""/\\|?*\p{C}]+")]
    private static partial Regex InvalidFileNameCharsRegex();
}

internal sealed record ExportOptions
{
    public string? Chat { get; private init; }
    public string OutputDirectory { get; private init; } = Path.Combine(Environment.CurrentDirectory, "export");
    public int StartOffsetId { get; private init; }
    public int MinMessageId { get; private init; }
    public int MaxMessages { get; private init; }
    public bool ContinueOnError { get; private init; } = true;
    public bool Overwrite { get; private init; }
    public bool ShowHelp { get; private init; }

    public static ExportOptions Parse(string[] args)
    {
        var options = new ExportOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    options = options with { ShowHelp = true };
                    break;
                case "--chat":
                    options = options with { Chat = ReadValue(args, ref i, arg) };
                    break;
                case "--out":
                    options = options with { OutputDirectory = Path.GetFullPath(ReadValue(args, ref i, arg)) };
                    break;
                case "--offset-id":
                    options = options with { StartOffsetId = ReadInt(args, ref i, arg) };
                    break;
                case "--min-id":
                    options = options with { MinMessageId = ReadInt(args, ref i, arg) };
                    break;
                case "--max-messages":
                    options = options with { MaxMessages = ReadInt(args, ref i, arg) };
                    break;
                case "--stop-on-error":
                    options = options with { ContinueOnError = false };
                    break;
                case "--overwrite":
                    options = options with { Overwrite = true };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Telegram.Export - download media from a Telegram chat/channel/group");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project Telegram.Export.WinForm -- --chat \"Chat title\" --out C:\\Exports\\Telegram");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --chat <id|username|title>  Chat, channel, group, or user to export.");
        Console.WriteLine("  --out <directory>           Output directory. Default: ./export");
        Console.WriteLine("  --offset-id <id>            Start before this message id, useful for resuming older pages.");
        Console.WriteLine("  --min-id <id>               Stop before or at this message id.");
        Console.WriteLine("  --max-messages <count>      Scan at most this many messages.");
        Console.WriteLine("  --overwrite                 Replace files that already exist.");
        Console.WriteLine("  --stop-on-error             Stop at the first failed media download.");
        Console.WriteLine();
        Console.WriteLine("Authentication can be entered interactively or via TELEGRAM_API_ID, TELEGRAM_API_HASH, TELEGRAM_PHONE, TELEGRAM_CODE, TELEGRAM_PASSWORD.");
    }

    private static string ReadValue(string[] args, ref int index, string name)
    {
        if (++index >= args.Length)
            throw new ArgumentException($"{name} requires a value.");

        return args[index];
    }

    private static int ReadInt(string[] args, ref int index, string name)
    {
        var value = ReadValue(args, ref index, name);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < 0)
            throw new ArgumentException($"{name} requires a non-negative integer.");

        return result;
    }
}

internal sealed class ExportStats
{
    public int MessagesScanned { get; set; }
    public int Downloaded { get; set; }
    public int SkippedExisting { get; set; }
    public int Failed { get; set; }
}
