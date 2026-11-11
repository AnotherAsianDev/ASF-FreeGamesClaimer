using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;

namespace FreeGamesClaimer;

#pragma warning disable CA1812 // ASF uses this class during runtime
// Deliberately NOT IDisposable: ASF calls Dispose() on disposable plugins, which would kill the background loop.
// These objects live exactly as long as the ASF process does.
#pragma warning disable CA1001

[UsedImplicitly]
internal sealed partial class FreeGamesClaimerPlugin : IBotCommand {
	private readonly SemaphoreSlim RunLock = new(1, 1);
	private readonly CancellationTokenSource Shutdown = new();

	private PluginConfig Config = new();
	private ClaimHistory History = new();
	private RedditFreeGames? Source;
	private Notifier? Notifications;
	private DateTime NextAutoCheck;

	public string Name => "FreeGamesClaimer";

	public Version Version => typeof(FreeGamesClaimerPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	public Task OnLoaded(CancellationToken cancellationToken = default) {
		try {
			Config = PluginStorage.LoadConfig();
			History = PluginStorage.LoadHistory();
			Source = new RedditFreeGames(Config.RedditUserUrl, Config.MaxPostAgeDays, Config.ClaimFreeToPlay);
			Notifications = new Notifier(Config);

			if (Config.Enabled) {
				NextAutoCheck = DateTime.Now.AddMinutes(2);

				ASF.ArchiLogger.LogGenericInfo($"{Name} {Version} loaded. Automatic check every {Config.CheckIntervalMinutes} min, the first one at {Clock(NextAutoCheck)}. Manual check: freegames");

				CancellationToken token = Shutdown.Token;

				_ = Task.Run(() => LoopAsync(token), token);
			} else {
				ASF.ArchiLogger.LogGenericInfo($"{Name} {Version} loaded. Automatic checks are disabled in config/freegames.json.config. Manual check: freegames");
			}

			if (Notifications.IsConfigured) {
				ASF.ArchiLogger.LogGenericInfo($"{Name}: Notifications are on (Telegram and/or Gotify). Test them with: freegames test");
			}
		} catch (Exception e) {
			// Never let the plugin take ASF down
			ASF.ArchiLogger.LogGenericException(e);
		}

		return Task.CompletedTask;
	}

	public async Task<string?> OnBotCommand(Bot bot, EBotUserAccess access, string message, string[] args, ulong steamID = 0, CancellationToken cancellationToken = default) {
		if ((args.Length == 0) || !args[0].Equals("freegames", StringComparison.OrdinalIgnoreCase)) {
			return null;
		}

		if (access < EBotUserAccess.Master) {
			return null;
		}

		if ((args.Length > 1) && args[1].Equals("test", StringComparison.OrdinalIgnoreCase)) {
			if ((Notifications == null) || !Notifications.IsConfigured) {
				return "No notification is set up. Fill in TelegramBotToken + TelegramChatId and/or GotifyUrl + GotifyToken in config/freegames.json.config and restart ASF.";
			}

			return string.Join(Environment.NewLine, await Notifications.SendAsync(Name, "Test notification: this is how a claimed game will look.", Shutdown.Token).ConfigureAwait(false));
		}

		string? result = await RunOnceAsync(true, Shutdown.Token).ConfigureAwait(false);

		if (Config.Enabled && (result != null)) {
			result += $"{Environment.NewLine}Next automatic check at {Clock(NextAutoCheck)}.";
		}

		return result;
	}

	private static string Clock(DateTime time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

	private async Task LoopAsync(CancellationToken cancellationToken) {
		try {
			// Give the bots time to log in after ASF starts
			await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);

			while (!cancellationToken.IsCancellationRequested) {
				try {
					// RunOnceAsync writes its own log lines
					await RunOnceAsync(false, cancellationToken).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					throw;
				} catch (Exception e) {
					ASF.ArchiLogger.LogGenericException(e);
				}

				NextAutoCheck = DateTime.Now.AddMinutes(Config.CheckIntervalMinutes);

				ASF.ArchiLogger.LogGenericInfo($"{Name}: Next automatic check at {Clock(NextAutoCheck)} (in {Config.CheckIntervalMinutes} min).");

				await Task.Delay(TimeSpan.FromMinutes(Config.CheckIntervalMinutes), cancellationToken).ConfigureAwait(false);
			}
		} catch (OperationCanceledException) {
			// shutting down
		} catch (Exception e) {
			// This runs unobserved in the background: nothing may escape, ASF treats that as FATAL
			ASF.ArchiLogger.LogGenericException(e);
		}
	}

	/// <param name="force">Manual run: ignore the "already run" cooldown.</param>
	private async Task<string?> RunOnceAsync(bool force, CancellationToken cancellationToken) {
		string kind = force ? "Manual check" : "Automatic check";

		if (Source == null) {
			return $"{Name} is not initialised.";
		}

		if (!await RunLock.WaitAsync(0, cancellationToken).ConfigureAwait(false)) {
			return force ? "A check is already running, try again in a moment." : null;
		}

		try {
			Bot? worker = Bot.BotsReadOnly?.Values.FirstOrDefault(static x => x.IsConnectedAndLoggedOn);

			if (worker == null) {
				ASF.ArchiLogger.LogGenericInfo($"{Name}: {kind} skipped, no bot is connected right now.");

				return force ? "No bot is connected right now." : null;
			}

			(IReadOnlyCollection<string> commands, int skippedFreeToPlay, string? error) = await Source.FetchAsync(cancellationToken).ConfigureAwait(false);

			if (error != null) {
				ASF.ArchiLogger.LogGenericWarning($"{Name}: {kind} failed, could not read Reddit ({error})");

				return force ? $"Could not read Reddit: {error}" : null;
			}

			DateTime now = DateTime.UtcNow;

			// forget old entries
			foreach (string key in History.Attempted.Where(x => (now - x.Value).TotalDays > 30).Select(static x => x.Key).ToList()) {
				History.Attempted.Remove(key);
			}

			TimeSpan cooldown = TimeSpan.FromHours(Config.RetryAfterHours);

			List<string> todo = commands.Where(x => force || !History.Attempted.TryGetValue(x, out DateTime last) || ((now - last) >= cooldown)).ToList();
			int skipped = commands.Count - todo.Count;

			string found = $"{commands.Count} {(Config.ClaimFreeToPlay ? "" : "limited-time ")}command(s) on Reddit from the last {Config.MaxPostAgeDays} day(s)" + (skippedFreeToPlay > 0 ? $" ({skippedFreeToPlay} free-to-play game(s) skipped)" : "");

			if (todo.Count == 0) {
				string nothing = commands.Count == 0 ? $"{Name}: {kind}: {found}, nothing to claim." : $"{Name}: {kind}: {found}, all already run in the last {Config.RetryAfterHours} h, nothing to do.";

				ASF.ArchiLogger.LogGenericInfo(nothing);

				return force ? nothing : null;
			}

			ASF.ArchiLogger.LogGenericInfo($"{Name}: {kind} started: {found}, running {todo.Count}" + (skipped > 0 ? $", skipping {skipped} (already run in the last {Config.RetryAfterHours} h)." : "."));

			StringBuilder report = new();
			int sent = 0;
			int added = 0;
			List<(string Type, string Id, string Bot)> claimed = [];

			foreach (string command in todo) {
				cancellationToken.ThrowIfCancellationRequested();

				// The command exactly as ASFinfo posted it ("addlicense asf a/123"). ASF applies it to all bots.
				// Commands.Response takes the bare command, without the "!" chat prefix.
				string? response = await worker.Commands.Response(EBotUserAccess.Operator, command, 0, cancellationToken).ConfigureAwait(false);

				if (!string.IsNullOrWhiteSpace(response)) {
					foreach (Match ok in ClaimedRegex().Matches(response)) {
						claimed.Add((ok.Groups["type"].Value, ok.Groups["id"].Value, ok.Groups["bot"].Value));
					}

					// One log line per bot. "<" and ">" are swapped for brackets: ASF's web log viewer swallows <BotName> as an HTML tag
					foreach (string line in response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
						ASF.ArchiLogger.LogGenericInfo($"{Name}: {kind}: {command} -> {line.Replace('<', '[').Replace('>', ']')}");

						if (line.Contains("Status: OK", StringComparison.Ordinal)) {
							added++;
						}
					}

					report.AppendLine(response.Trim());

					// Steam throttles activations: stop now and retry this command next cycle
					if (response.Contains("RateLimitExceeded", StringComparison.OrdinalIgnoreCase)) {
						ASF.ArchiLogger.LogGenericWarning($"{Name}: Steam rate limit reached, the rest follows in the next check.");

						break;
					}
				}

				History.Attempted[command] = now;
				sent++;

				await Task.Delay(TimeSpan.FromSeconds(Config.DelayBetweenClaimsSeconds), cancellationToken).ConfigureAwait(false);
			}

			PluginStorage.SaveHistory(History);

			if ((claimed.Count > 0) && (Notifications != null) && Notifications.IsConfigured) {
				IReadOnlyCollection<string> sentTo = await Notifications.SendAsync($"{Name}: {claimed.Count} new license(s)", Notifier.BuildMessage(claimed), cancellationToken).ConfigureAwait(false);

				ASF.ArchiLogger.LogGenericInfo($"{Name}: Notification: {string.Join(", ", sentTo)}");
			}

			string summary = $"{Name}: {kind} finished: ran {sent} command(s), {added} new license(s) added" + (added == 0 ? " (everything was already owned or not available)." : ".");

			ASF.ArchiLogger.LogGenericInfo(summary);

			return force && (report.Length > 0) ? summary + Environment.NewLine + report : summary;
		} finally {
			RunLock.Release();
		}
	}

	// ASF answers "<Bot> ID: app/123 | Status: OK | Items: ..." for every license that was really added
	[GeneratedRegex("""<(?<bot>[^>]+)> ID: (?<type>app|sub)/(?<id>\d+) \| Status: OK""", RegexOptions.CultureInvariant)]
	private static partial Regex ClaimedRegex();
}

#pragma warning restore CA1001
#pragma warning restore CA1812 // ASF uses this class during runtime
