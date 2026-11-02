using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
internal sealed class FreeGamesClaimerPlugin : IBotCommand2 {
	private readonly SemaphoreSlim RunLock = new(1, 1);
	private readonly CancellationTokenSource Shutdown = new();

	private PluginConfig Config = new();
	private ClaimHistory History = new();
	private RedditFreeGames? Source;
	private DateTime NextAutoCheck;

	public string Name => "FreeGamesClaimer";

	public Version Version => typeof(FreeGamesClaimerPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	public Task OnLoaded() {
		try {
			Config = PluginStorage.LoadConfig();
			History = PluginStorage.LoadHistory();
			Source = new RedditFreeGames(Config.RedditUserUrl, Config.MaxPostAgeDays);

			if (Config.Enabled) {
				NextAutoCheck = DateTime.Now.AddMinutes(2);

				ASF.ArchiLogger.LogGenericInfo($"{Name} {Version} loaded. Automatic check every {Config.CheckIntervalMinutes} min, the first one at {Clock(NextAutoCheck)}. Manual check: freegames");

				CancellationToken token = Shutdown.Token;

				_ = Task.Run(() => LoopAsync(token));
			} else {
				ASF.ArchiLogger.LogGenericInfo($"{Name} {Version} loaded. Automatic checks are disabled in config/freegames.json.config. Manual check: freegames");
			}
		} catch (Exception e) {
			// Never let the plugin take ASF down
			ASF.ArchiLogger.LogGenericException(e);
		}

		return Task.CompletedTask;
	}

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		if ((args.Length == 0) || !args[0].Equals("freegames", StringComparison.OrdinalIgnoreCase)) {
			return null;
		}

		if (access < EAccess.Master) {
			return null;
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

			(IReadOnlyCollection<string> commands, string? error) = await Source.FetchAsync(cancellationToken).ConfigureAwait(false);

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

			string found = $"{commands.Count} command(s) on Reddit from the last {Config.MaxPostAgeDays} day(s)";

			if (todo.Count == 0) {
				string nothing = $"{Name}: {kind}: {found}, all already run in the last {Config.RetryAfterHours} h, nothing to do.";

				ASF.ArchiLogger.LogGenericInfo(nothing);

				return force ? nothing : null;
			}

			ASF.ArchiLogger.LogGenericInfo($"{Name}: {kind} started: {found}, running {todo.Count}" + (skipped > 0 ? $", skipping {skipped} (already run in the last {Config.RetryAfterHours} h)." : "."));

			StringBuilder report = new();
			int sent = 0;
			int added = 0;

			foreach (string command in todo) {
				cancellationToken.ThrowIfCancellationRequested();

				// The command exactly as ASFinfo posted it ("addlicense asf a/123"). ASF applies it to all bots.
				// Commands.Response takes the bare command, without the "!" chat prefix.
				string? response = await worker.Commands.Response(EAccess.Operator, command).ConfigureAwait(false);

				if (!string.IsNullOrWhiteSpace(response)) {
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

			string summary = $"{Name}: {kind} finished: ran {sent} command(s), {added} new license(s) added" + (added == 0 ? " (everything was already owned or not available)." : ".");

			ASF.ArchiLogger.LogGenericInfo(summary);

			return force && (report.Length > 0) ? summary + Environment.NewLine + report : summary;
		} finally {
			RunLock.Release();
		}
	}
}

#pragma warning restore CA1001
#pragma warning restore CA1812 // ASF uses this class during runtime
