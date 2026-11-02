# FreeGamesClaimer

ArchiSteamFarm plugin that claims temporarily free Steam games on all your bots. Replacement for ASFFreeGames.

**Why a rewrite:** ASFFreeGames stopped loading on ASF 6.3.x / .NET 10 (`Method not found: WebProxy..ctor(String, Boolean)`, issue #143). This plugin has no proxy code, so that crash cannot happen. It sends one request to Reddit per check and none to Steam.

## How it works

The Reddit account u/ASFinfo answers every free-game post with a ready-made command, e.g. `!addlicense asf a/4983490`. The plugin takes exactly that command from the comment, removes the `!` chat prefix and runs it in ASF. Because the command says `asf`, ASF applies it to all bots. Nothing else is done.

## Install (prebuilt DLL)

1. Delete the old `ASFFreeGames.dll` from ASF's `plugins` folder (both react to `freegames`).
2. Copy `FreeGamesClaimer.dll` into ASF's `plugins` folder (Docker: the mounted `plugins` directory).
3. Restart ASF. The log should show `FreeGamesClaimer ... has been loaded successfully!`.

Built against ASF 6.3.5.1 on .NET 10.

## Use

- Every 30 minutes (first check 2 minutes after start) it reads the newest u/ASFinfo comments (max 3 days old) and runs each `addlicense ...` command found.
- `freegames` (Master access) forces a check right now and runs everything again, ignoring the cooldown.
- `freegames test` sends a test notification.
- Commands already run are remembered in `config/freegames.history.json.config` and run again only after 12 h.

## Config: `config/freegames.json.config` (created on first start)

| Option | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Automatic checks. The `freegames` command works either way. |
| `CheckIntervalMinutes` | `30` | 5 to 1440 |
| `RedditUserUrl` | `https://www.reddit.com/user/ASFinfo` | Reddit account to read |
| `MaxPostAgeDays` | `3` | Ignore older comments |
| `DelayBetweenClaimsSeconds` | `5` | Pause between commands (Steam allows ~50 activations/hour) |
| `RetryAfterHours` | `12` | Cooldown before the same command is run again |
| `TelegramBotToken` / `TelegramChatId` | empty | Telegram notification (bot token from @BotFather, your chat id). Both must be set. |
| `GotifyUrl` / `GotifyToken` | empty | Gotify notification (e.g. `https://gotify.example.com` and an application token). Both must be set. |

## Notifications

With Telegram and/or Gotify filled in, you get one message per check in which a license was really added (`Status: OK`), for example:

```
FreeGamesClaimer: 1 new license(s)
https://store.steampowered.com/app/4983490 (Alt, Main)
```

Games you already own, and checks with nothing new, send nothing. Restart ASF after editing the config, then run `freegames test` to send a test message. A failing notification only writes a warning to the ASF log and never stops the claiming.

## Build

The plugin compiles against the ASF source, exactly like the official plugin template. Pin the ASF version to the one you run (check the tag, e.g. `6.3.5.1`).

```
git init
git submodule add https://github.com/JustArchiNET/ArchiSteamFarm.git ArchiSteamFarm
git -C ArchiSteamFarm checkout 6.3.5.1
dotnet publish FreeGamesClaimer -c Release -o out
```

Copy `out/FreeGamesClaimer.dll` into ASF's `plugins` folder (or `plugins/FreeGamesClaimer/`), **remove the old ASFFreeGames dll**, restart ASF. The GitHub Actions workflow in `.github/workflows` does the same build for you if you push this to a repo (with the submodule committed).

Requires .NET 10 SDK.
