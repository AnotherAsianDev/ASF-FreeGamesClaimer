using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;

namespace FreeGamesClaimer;

/// <summary>Sends a short message to Telegram and/or Gotify. Every failure is only logged, notifications must never disturb the claiming.</summary>
internal sealed class Notifier : IDisposable {
	private readonly HttpClient Http;
	private readonly PluginConfig Config;
	private readonly string TelegramApiBase;

	internal Notifier(PluginConfig config, string telegramApiBase = "https://api.telegram.org") {
		Config = config;
		TelegramApiBase = telegramApiBase.TrimEnd('/');

#pragma warning disable CA2000 // HttpClient takes ownership of the handler
		SocketsHttpHandler handler = new() {
			AutomaticDecompression = DecompressionMethods.All,
			ConnectTimeout = TimeSpan.FromSeconds(15)
		};

		Http = new HttpClient(handler, true) { Timeout = TimeSpan.FromSeconds(20) };
#pragma warning restore CA2000
	}

	public void Dispose() => Http.Dispose();

	internal bool IsConfigured => IsTelegramConfigured || IsGotifyConfigured;

	private bool IsTelegramConfigured => !string.IsNullOrEmpty(Config.TelegramBotToken) && !string.IsNullOrEmpty(Config.TelegramChatId);
	private bool IsGotifyConfigured => !string.IsNullOrEmpty(Config.GotifyUrl) && !string.IsNullOrEmpty(Config.GotifyToken);

	/// <summary>Returns a short human readable result per service, for logs and the "freegames test" command.</summary>
	internal async Task<IReadOnlyCollection<string>> SendAsync(string title, string message, CancellationToken cancellationToken) {
		List<string> results = [];

		if (IsTelegramConfigured) {
			results.Add(await Try("Telegram", () => SendTelegramAsync(title, message, cancellationToken)).ConfigureAwait(false));
		}

		if (IsGotifyConfigured) {
			results.Add(await Try("Gotify", () => SendGotifyAsync(title, message, cancellationToken)).ConfigureAwait(false));
		}

		return results;
	}

	private static async Task<string> Try(string service, Func<Task> send) {
		try {
			await send().ConfigureAwait(false);

			return $"{service}: sent";
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception e) {
			string reason = e is HttpRequestException { StatusCode: not null } http ? $"HTTP {(int) http.StatusCode.Value}" : e.Message;

			ASF.ArchiLogger.LogGenericWarning($"{nameof(FreeGamesClaimer)}: {service} notification failed ({reason})");

			return $"{service}: failed ({reason})";
		}
	}

	private async Task SendTelegramAsync(string title, string message, CancellationToken cancellationToken) {
		using FormUrlEncodedContent content = new([
			new KeyValuePair<string, string>("chat_id", Config.TelegramChatId),
			new KeyValuePair<string, string>("text", $"{title}\n{message}"),
			new KeyValuePair<string, string>("disable_web_page_preview", "true")
		]);

		using HttpResponseMessage response = await Http.PostAsync(new Uri($"{TelegramApiBase}/bot{Config.TelegramBotToken}/sendMessage"), content, cancellationToken).ConfigureAwait(false);

		response.EnsureSuccessStatusCode();
	}

	private async Task SendGotifyAsync(string title, string message, CancellationToken cancellationToken) {
		using FormUrlEncodedContent content = new([
			new KeyValuePair<string, string>("title", title),
			new KeyValuePair<string, string>("message", message),
			new KeyValuePair<string, string>("priority", "5")
		]);

		using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{Config.GotifyUrl.TrimEnd('/')}/message")) { Content = content };

		// The token goes into a header, not the URL, so it doesn't end up in proxy logs
		request.Headers.Add("X-Gotify-Key", Config.GotifyToken);

		using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);

		response.EnsureSuccessStatusCode();
	}

	/// <summary>Builds the notification text from the claimed items, e.g. "https://store.steampowered.com/app/123 (Alt, Main)".</summary>
	internal static string BuildMessage(IEnumerable<(string Type, string Id, string Bot)> claimed) {
		Dictionary<string, List<string>> grouped = new(StringComparer.Ordinal);

		foreach ((string type, string id, string bot) in claimed) {
			string link = $"https://store.steampowered.com/{type}/{id}";

			if (!grouped.TryGetValue(link, out List<string>? bots)) {
				grouped[link] = bots = [];
			}

			bots.Add(bot);
		}

		StringBuilder text = new();

		foreach ((string link, List<string> bots) in grouped) {
			text.Append(link).Append(" (").AppendJoin(", ", bots).Append(")\n");
		}

		return text.ToString().TrimEnd();
	}
}
