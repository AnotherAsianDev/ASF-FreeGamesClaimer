using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FreeGamesClaimer;

/// <summary>
/// Reads the comments of the ASFinfo Reddit account. That bot answers every free-game post with a ready-made
/// "!addlicense asf a/ID" command, which this class hands over exactly as posted (minus the chat prefix).
/// One request to Reddit per check.
/// </summary>
internal sealed partial class RedditFreeGames : IDisposable {
	private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);

	private readonly HttpClient Http;
	private readonly string BaseUrl;
	private readonly int MaxAgeDays;

	private IReadOnlyCollection<string>? Cached;
	private DateTime CachedAt;

	internal RedditFreeGames(string userUrl, int maxAgeDays) {
		BaseUrl = userUrl.TrimEnd('/');
		MaxAgeDays = maxAgeDays;

		// Plain SocketsHttpHandler: honours the system/env proxy by itself, no custom proxy code
		// (a WebProxy constructor call is exactly what broke the previous plugin on .NET 10).
#pragma warning disable CA2000 // HttpClient takes ownership of the handler
		SocketsHttpHandler handler = new() {
			AutomaticDecompression = DecompressionMethods.All,
			ConnectTimeout = TimeSpan.FromSeconds(20),
			PooledConnectionLifetime = TimeSpan.FromMinutes(5)
		};

		Http = new HttpClient(handler, true) { Timeout = TimeSpan.FromSeconds(30) };
#pragma warning restore CA2000
		// Reddit rejects requests without a descriptive User-Agent
		Http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeGamesClaimer/1.0 (ArchiSteamFarm plugin)");
	}

	public void Dispose() => Http.Dispose();

	/// <summary>Never throws for network/parse problems. Error is set only when Reddit could not be read at all.</summary>
	internal async Task<(IReadOnlyCollection<string> Commands, string? Error)> FetchAsync(CancellationToken cancellationToken) {
		// Reddit throttles unauthenticated clients hard: repeated manual runs reuse the last answer for a few minutes
		if ((Cached != null) && ((DateTime.UtcNow - CachedAt) < CacheDuration)) {
			return (Cached, null);
		}

		string rssError;

		// The RSS feed first: Reddit serves it almost everywhere, while it often refuses the JSON endpoint to servers
		try {
			string rss = await Http.GetStringAsync(new Uri($"{BaseUrl}/comments.rss?limit=25"), cancellationToken).ConfigureAwait(false);

			return Remember(ParseFeed(rss, DateTimeOffset.UtcNow, MaxAgeDays));
		} catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.TooManyRequests) {
			// Asking again through the JSON endpoint would only make the throttling worse
			return ([], "Reddit is rate limiting this connection (HTTP 429), try again in a few minutes");
		} catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
			rssError = e.Message;
		}

		try {
			string json = await Http.GetStringAsync(new Uri($"{BaseUrl}/comments.json?limit=25&raw_json=1"), cancellationToken).ConfigureAwait(false);

			return Remember(ParseJson(json, DateTimeOffset.UtcNow, MaxAgeDays));
		} catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
			return ([], $"reddit rss: {rssError}; reddit json: {e.Message}");
		}
	}

	private (IReadOnlyCollection<string> Commands, string? Error) Remember(List<string> commands) {
		Cached = commands;
		CachedAt = DateTime.UtcNow;

		return (commands, null);
	}

	/// <summary>Parses the Atom feed (.../comments.rss), keeping entries newer than maxAgeDays.</summary>
	internal static List<string> ParseFeed(string feed, DateTimeOffset now, int maxAgeDays) {
		if (!feed.Contains("<feed", StringComparison.Ordinal) && !feed.Contains("<rss", StringComparison.Ordinal)) {
			throw new InvalidOperationException("unexpected response (not a feed)");
		}

		StringBuilder text = new();

		foreach (Match entry in EntryRegex().Matches(feed)) {
			Match updated = UpdatedRegex().Match(entry.Value);

			if (updated.Success && DateTimeOffset.TryParse(updated.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset when) && ((now - when).TotalDays > maxAgeDays)) {
				continue;
			}

			Match content = ContentRegex().Match(entry.Value);

			if (!content.Success) {
				continue;
			}

			// The comment is HTML-escaped inside the XML: unescape it and drop the tags to get plain text
			string plain = TagRegex().Replace(WebUtility.HtmlDecode(content.Groups[1].Value), " ");

			text.Append(WebUtility.HtmlDecode(plain)).Append('\n');
		}

		return ParseText(text.ToString());
	}

	/// <summary>Parses the listing returned by .../comments.json, keeping comments newer than maxAgeDays.</summary>
	internal static List<string> ParseJson(string json, DateTimeOffset now, int maxAgeDays) {
		StringBuilder text = new();

		using JsonDocument doc = JsonDocument.Parse(json);

		if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || !data.TryGetProperty("children", out JsonElement children) || (children.ValueKind != JsonValueKind.Array)) {
			throw new InvalidOperationException("unexpected response (no data.children)");
		}

		foreach (JsonElement child in children.EnumerateArray()) {
			if (!child.TryGetProperty("data", out JsonElement post)) {
				continue;
			}

			if (post.TryGetProperty("created_utc", out JsonElement created) && created.TryGetDouble(out double seconds) && ((now - DateTimeOffset.FromUnixTimeSeconds((long) seconds)).TotalDays > maxAgeDays)) {
				continue;
			}

			if (post.TryGetProperty("body", out JsonElement body) && (body.ValueKind == JsonValueKind.String)) {
				text.Append(body.GetString()).Append('\n');
			}
		}

		return ParseText(text.ToString());
	}

	/// <summary>
	/// Returns every "addlicense ..." command exactly as the bot wrote it, without the "!" chat prefix.
	/// Each one appears once; the newest comes first.
	/// </summary>
	internal static List<string> ParseText(string text) {
		Dictionary<string, string> found = new(StringComparer.OrdinalIgnoreCase);

		foreach (Match command in CommandRegex().Matches(text)) {
			string normalized = WhitespaceRegex().Replace(command.Value, " ").Trim();

			found.TryAdd(normalized, normalized);
		}

		return found.Values.ToList();
	}

	// "addlicense asf a/123" or "addlicense a/123,s/456" - ends at the first thing that isn't a license token
	[GeneratedRegex("""addlicense\s+(?:asf\s+)?(?:(?:app|sub|a|s)/\d+(?:\s*,\s*)?)+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex CommandRegex();

	[GeneratedRegex("""\s+""", RegexOptions.CultureInvariant)]
	private static partial Regex WhitespaceRegex();

	[GeneratedRegex("""<entry>.*?</entry>""", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
	private static partial Regex EntryRegex();

	[GeneratedRegex("""<content type="html">(.*?)</content>""", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
	private static partial Regex ContentRegex();

	[GeneratedRegex("""<[^>]+>""", RegexOptions.CultureInvariant)]
	private static partial Regex TagRegex();

	[GeneratedRegex("""<updated>([^<]+)</updated>""", RegexOptions.CultureInvariant)]
	private static partial Regex UpdatedRegex();
}
