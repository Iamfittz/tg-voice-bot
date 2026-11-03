# Voice Bot

A Telegram bot that turns voice messages, audio files and video notes into text. Speech recognition runs on your own server with [whisper.cpp](https://github.com/ggml-org/whisper.cpp), so audio does not go to a third-party recognition service.

It also receives Microsoft Teams call records through Microsoft Graph and rates call quality: see [Teams call records](#teams-call-records).

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
| `GET /api/calls` | Teams calls with `quality`, `limit` and `offset` |
| `GET /api/calls/{id}` | One Teams call |

## Teams call records

When a Teams call ends, the bot gets a notification from Microsoft Graph, reads the call record, rates the audio quality, saves the result and sends a summary to a Telegram chat.

**Status:** tested with unit tests and a local simulator of Graph written from the public documentation. It has not been run against a real Microsoft 365 tenant yet, so real payloads may reveal differences.

```text
Teams call ends -> Graph -> POST /graph/notifications -> table of pending records -> 202
                                                                 |
                                               worker -> Graph: call record, all pages
                                                                 |
                              quality rating -> database -> message in Telegram
```

- **Fast webhook.** Graph allows 3 seconds, and a late answer can get an endpoint throttled. The webhook only checks the notification and stores it. Records are fetched later.
- **Stored before the answer.** Graph does not resend an acknowledged notification, so pending work is kept in the database and survives a restart. If storing fails, the webhook answers 5xx and Graph retries.
- **Authenticity.** `clientState` is compared in constant time, and the tenant must match.
- **No duplicate work.** A record is identified by its `id` and `version`. A repeated notification changes nothing. A new version updates the record, and sends a message only if the quality rating changed.
- **Throttling.** `429`, `503` and `504` are retried after `Retry-After`, or with exponential backoff when the header is missing.
- **Paging.** Sessions and participants are read through every `@odata.nextLink`. The access token is sent only to the configured Graph host.
- **Failed fetches.** Retried with growing delays for about 4 hours, then kept with the error for investigation.
- **Subscription lifecycle.** The bot looks up its subscription in Graph, then renews it or creates a new one, so a restart does not cause `409 Conflict`. It renews on a timer and on `reauthorizationRequired`. Call records subscriptions live at most 4230 minutes.

### Call quality

Audio streams are rated with the [Teams Call Quality Dashboard rules](https://learn.microsoft.com/microsoftteams/stream-classification-in-call-quality-dashboard). A stream with more than 500 packets is poor if at least one value is over its limit:

| Metric | Limit |
|---|---|
| Round trip | 500 ms |
| Packet loss rate | 0.1 |
| Jitter | 30 ms |

A call is poor if any of its audio streams is poor. As Microsoft notes, this describes network conditions and does not prove that people heard a problem.

### Try it without a tenant

Needs only Docker: no Microsoft 365 tenant, Telegram token or speech model.

```bash
docker compose -f docker-compose.demo.yml up --build -d
curl -X POST "http://localhost:5081/simulate/calls?quality=poor&participants=3"
curl http://localhost:5082/api/calls
docker compose -f docker-compose.demo.yml logs voice-bot
docker compose -f docker-compose.demo.yml down
```

The simulator also has `POST /simulate/calls/{id}/update?quality=good`, `POST /simulate/calls/{id}/resend` and `POST /simulate/lifecycle`. It throttles the first read of each record, so the logs show the retry. Unlike real Graph, it accepts plain HTTP and does not check permissions.

### Connect a real tenant

1. Register an application in Microsoft Entra ID and create a client secret.
2. Add the application permission `CallRecords.Read.All` and grant admin consent.
3. Publish `/graph/*` over HTTPS through a reverse proxy, and keep `/api` and `/swagger` private.
4. Fill in the `GRAPH_*` values in `.env` and restart.

Microsoft documents an average delay under 30 minutes and a maximum of 150 minutes before the first notification about a call. Call records stay available for 30 days.

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
