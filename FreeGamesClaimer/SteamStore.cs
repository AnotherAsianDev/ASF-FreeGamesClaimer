using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FreeGamesClaimer;

internal enum StoreVerdict : byte {
	/// <summary>Costs money normally and is 100% off right now.</summary>
	PaidFreeNow,

	/// <summary>Free to play, or not (fully) discounted.</summary>
	NotPaidFreeNow,

	/// <summary>Steam's public store API has no data for it.</summary>
	Unlisted,

	/// <summary>Steam could not be asked (network, rate limit). Try again later.</summary>
	Error
}

/// <summary>
/// Asks Steam's public store API whether a game that ASFinfo posted is a paid game that is 100% off right now.
/// One small request per app/package, no login needed.
/// </summary>
internal sealed partial class SteamStore : IDisposable {
	private readonly HttpClient Http;
	private readonly string ApiBase;

	internal SteamStore(string apiBase = "https://store.steampowered.com") {
		ApiBase = apiBase.TrimEnd('/');

#pragma warning disable CA2000 // HttpClient takes ownership of the handler
		SocketsHttpHandler handler = new() {
			AutomaticDecompression = DecompressionMethods.All,
			ConnectTimeout = TimeSpan.FromSeconds(15)
		};

		Http = new HttpClient(handler, true) { Timeout = TimeSpan.FromSeconds(20) };
#pragma warning restore CA2000
		Http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeGamesClaimer/2.2 (ArchiSteamFarm plugin)");
	}

	public void Dispose() => Http.Dispose();

	/// <summary>
	/// Decides which part of an "addlicense asf a/1,s/2" command should run.
	/// Approved is the command to run (the original if everything passed, otherwise only the passing ids) or null if nothing passed.
	/// Failed means Steam could not be asked for at least one id, so nothing is decided yet.
	/// </summary>
	internal async Task<(string? Approved, string Note, bool Failed)> ClassifyAsync(string command, CancellationToken cancellationToken) {
		MatchCollection ids = IdRegex().Matches(command);

		if (ids.Count == 0) {
			return (command, "no id found, running as posted", false);
		}

		List<string> approved = [];
		List<string> notes = [];

		for (int i = 0; i < ids.Count; i++) {
			if (i > 0) {
				await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
			}

			string token = ids[i].Value;
			bool isApp = ids[i].Groups["type"].Value[0] is 'a' or 'A';
			uint id = uint.Parse(ids[i].Groups["id"].Value, CultureInfo.InvariantCulture);

			(StoreVerdict verdict, string detail) = await CheckAsync(isApp, id, cancellationToken).ConfigureAwait(false);

			switch (verdict) {
				case StoreVerdict.Error:
					return (null, $"{token}: Steam could not be asked ({detail}), will try again next check", true);
				case StoreVerdict.PaidFreeNow:
					approved.Add(token);
					notes.Add($"{token}: {detail}");

					break;
				case StoreVerdict.Unlisted when !isApp:
					// Limited-time packages are usually hidden from the public store API: claim them, ASF reports if Steam refuses
					approved.Add(token);
					notes.Add($"{token}: package not listed by Steam, can't verify, trying anyway");

					break;
				default:
					notes.Add($"{token}: {detail}");

					break;
			}
		}

		string note = string.Join("; ", notes);

		if (approved.Count == 0) {
			return (null, note, false);
		}

		if (approved.Count == ids.Count) {
			return (command, note, false);
		}

		// Some ids passed, some did not: run only the passing ones
		string prefix = command[..ids[0].Index].TrimEnd();

		return ($"{prefix} {string.Join(",", approved)}", note, false);
	}

	internal async Task<(StoreVerdict Verdict, string Detail)> CheckAsync(bool isApp, uint id, CancellationToken cancellationToken) {
		Uri url = isApp ? new Uri($"{ApiBase}/api/appdetails?appids={id}&cc=us&filters=basic,price_overview") : new Uri($"{ApiBase}/api/packagedetails?packageids={id}&cc=us");

		try {
			using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);

			if (response.StatusCode == HttpStatusCode.TooManyRequests) {
				return (StoreVerdict.Error, "Steam rate limit, HTTP 429");
			}

			if (!response.IsSuccessStatusCode) {
				// A package id that doesn't exist publicly makes Steam answer 400
				return !isApp && (response.StatusCode == HttpStatusCode.BadRequest) ? (StoreVerdict.Unlisted, "no data") : (StoreVerdict.Error, $"HTTP {(int) response.StatusCode}");
			}

			string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

			return Evaluate(json, id, isApp);
		} catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
			throw;
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) {
			return (StoreVerdict.Error, e.Message);
		}
	}

	/// <summary>Reads Steam's answer. Public for the tests.</summary>
	internal static (StoreVerdict Verdict, string Detail) Evaluate(string json, uint id, bool isApp) {
		using JsonDocument doc = JsonDocument.Parse(json);

		if (!doc.RootElement.TryGetProperty(id.ToString(CultureInfo.InvariantCulture), out JsonElement entry) || !entry.TryGetProperty("success", out JsonElement success) || (success.ValueKind != JsonValueKind.True) || !entry.TryGetProperty("data", out JsonElement data) || (data.ValueKind != JsonValueKind.Object)) {
			return (StoreVerdict.Unlisted, "not listed by Steam");
		}

		if (isApp && data.TryGetProperty("is_free", out JsonElement isFree) && (isFree.ValueKind == JsonValueKind.True)) {
			return (StoreVerdict.NotPaidFreeNow, "free to play");
		}

		if (!data.TryGetProperty(isApp ? "price_overview" : "price", out JsonElement price) || (price.ValueKind != JsonValueKind.Object)) {
			return (StoreVerdict.NotPaidFreeNow, "has no price (free or not for sale)");
		}

		long initial = price.TryGetProperty("initial", out JsonElement i) && i.TryGetInt64(out long iv) ? iv : 0;
		long final = price.TryGetProperty("final", out JsonElement f) && f.TryGetInt64(out long fv) ? fv : initial;
		int discount = price.TryGetProperty("discount_percent", out JsonElement d) && d.TryGetInt32(out int dv) ? dv : 0;

		if ((initial > 0) && ((discount >= 100) || (final == 0))) {
			return (StoreVerdict.PaidFreeNow, $"paid game, 100% off now (normally {initial / 100m:0.00} USD)");
		}

		return (StoreVerdict.NotPaidFreeNow, discount > 0 ? $"only {discount}% off, not free" : "not discounted");
	}

	[GeneratedRegex("""\b(?<type>app|sub|a|s)/(?<id>\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex IdRegex();
}
