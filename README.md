# Voice Bot

A Telegram bot that turns voice messages, audio files and video notes into text. Speech recognition runs on your own server with [whisper.cpp](https://github.com/ggml-org/whisper.cpp), so audio does not go to a third-party recognition service.

Built with C#, .NET 10, ASP.NET Core, EF Core and SQLite.

## How it works

```text
Telegram -> UpdateRouter -> limits check -> bounded queue -> worker
                                                              |
                                  download -> ffmpeg -> whisper.cpp
                                                              |
                                  save to database -> reply to the user
```

- **No public port for the bot.** It uses long polling, which needs only outbound connections.
- **One worker, bounded queue.** Jobs do not compete for the CPU, and overload gets a clear answer instead of a hang.
- **Limits.** Maximum duration, file size and pending jobs per user.
- **Job timeout.** One limit covers download, conversion and recognition.
- **Results are saved before the reply**, so a Telegram failure does not lose finished work.
- **Restart safety.** Jobs interrupted by a restart are marked as failed instead of staying queued forever.
- **Long results** are split to fit the Telegram limit of 4096 characters.
- **Language choice.** Buttons on `/start` and `/lang`, or the commands `/uk`, `/ru`, `/en` and `/auto`. Without a choice, the bot uses `DEFAULT_LANGUAGE` (`uk` by default): automatic detection is unreliable on short clips, and the language of the Telegram app says nothing about the spoken language.
- **Privacy.** Temporary audio is deleted after processing, and `/forget` removes a user's data.

## REST API

Published on `127.0.0.1:5080` only.

| Endpoint | Description |
|---|---|
| `GET /swagger` | API documentation |
| `GET /health` | Database and Whisper availability |
| `GET /api/transcriptions` | List with `userId`, `limit` and `offset` |
| `GET /api/transcriptions/{id}` | One transcription |
| `GET /api/stats` | Totals and average processing time |

## Run

1. Create a bot with [@BotFather](https://t.me/BotFather).
2. Download a model:

   ```bash
   mkdir -p models && curl -L -o models/ggml-small.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin
   ```

3. Copy `.env.example` to `.env` and set `TELEGRAM_BOT_TOKEN`.
4. Start:

   ```bash
   docker compose up -d --build
   ```

## Tests

```bash
dotnet test
```
