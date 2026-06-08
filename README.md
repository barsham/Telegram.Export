# Telegram.Export

Console tool to download media from a Telegram chat, group, channel, or user.

## Setup

1. Create Telegram API credentials at <https://my.telegram.org/apps>.
2. Build the tool:

```powershell
dotnet restore
dotnet build
```

3. Run it. The first run asks for API id/hash, phone number, login code, and two-step password if enabled. A reusable session is stored inside the output directory.

```powershell
dotnet run --project Telegram.Export.WinForm -- --chat "Chat title" --out C:\Exports\Telegram
```

You can also pass environment variables instead of typing credentials:

```powershell
$env:TELEGRAM_API_ID="123456"
$env:TELEGRAM_API_HASH="your_api_hash"
$env:TELEGRAM_PHONE="+61400000000"
dotnet run --project Telegram.Export.WinForm -- --chat "@channel_username"
```

If `--chat` does not match anything, the tool prints the available chats with ids, usernames, and titles.

## Options

```text
--chat <id|username|title>  Chat, channel, group, or user to export.
--out <directory>           Output directory. Default: ./export
--offset-id <id>            Start before this message id, useful for resuming older pages.
--min-id <id>               Stop before or at this message id.
--max-messages <count>      Scan at most this many messages.
--overwrite                 Replace files that already exist.
--stop-on-error             Stop at the first failed media download.
```

Downloaded files are grouped by chat and month. A `media-manifest.jsonl` file records message id, date, grouped album id, local file path, media type, and caption text.
