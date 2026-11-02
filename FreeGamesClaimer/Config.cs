using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeGamesClaimer;

/// <summary>User settings, stored in config/freegames.json.config (created on first start).</summary>
internal sealed class PluginConfig {
	/// <summary>Master switch for the periodic background check. The "freegames" command works regardless.</summary>
	public bool Enabled { get; set; } = true;

	/// <summary>Minutes between automatic checks.</summary>
	public int CheckIntervalMinutes { get; set; } = 30;

	/// <summary>Reddit account whose comments contain the addlicense commands.</summary>
	public string RedditUserUrl { get; set; } = "https://www.reddit.com/user/ASFinfo";

	/// <summary>Ignore Reddit comments older than this many days (the promotions are over by then).</summary>
	public int MaxPostAgeDays { get; set; } = 3;

	/// <summary>Seconds to wait between two commands (Steam allows roughly 50 activations per hour).</summary>
	public int DelayBetweenClaimsSeconds { get; set; } = 5;

	/// <summary>Hours before a command that was already run is run again.</summary>
	public int RetryAfterHours { get; set; } = 12;

	internal void Normalize() {
		CheckIntervalMinutes = Math.Clamp(CheckIntervalMinutes, 5, 24 * 60);
		MaxPostAgeDays = Math.Clamp(MaxPostAgeDays, 1, 30);
		DelayBetweenClaimsSeconds = Math.Clamp(DelayBetweenClaimsSeconds, 1, 120);
		RetryAfterHours = Math.Clamp(RetryAfterHours, 1, 24 * 14);
		RedditUserUrl = string.IsNullOrWhiteSpace(RedditUserUrl) ? "https://www.reddit.com/user/ASFinfo" : RedditUserUrl.Trim();
	}
}

/// <summary>Remembers which commands were already run so they aren't re-sent on every cycle.</summary>
internal sealed class ClaimHistory {
	[JsonPropertyName("attempted")]
	public Dictionary<string, DateTime> Attempted { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal static class PluginStorage {
	private static readonly JsonSerializerOptions JsonOptions = new() {
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	// ASF runs with its own folder as working directory, so "config" is ASF's config folder.
	// The ".config" ending keeps ASF from mistaking these files for bot configs.
	private static string ConfigDir => "config";
	private static string ConfigPath => Path.Combine(ConfigDir, "freegames.json.config");
	private static string HistoryPath => Path.Combine(ConfigDir, "freegames.history.json.config");

	internal static PluginConfig LoadConfig() {
		PluginConfig config = new();

		try {
			if (File.Exists(ConfigPath)) {
				config = JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(ConfigPath), JsonOptions) ?? new PluginConfig();
			}

			config.Normalize();

			// Write back so the file always lists the current options
			Directory.CreateDirectory(ConfigDir);
			File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
		} catch (Exception) {
			// A broken file must not stop the plugin: fall back to defaults
			config = new PluginConfig();
			config.Normalize();
		}

		return config;
	}

	internal static ClaimHistory LoadHistory() {
		try {
			if (File.Exists(HistoryPath)) {
				ClaimHistory? history = JsonSerializer.Deserialize<ClaimHistory>(File.ReadAllText(HistoryPath), JsonOptions);

				if (history != null) {
					// Old versions keyed the history by "a/123"; case-insensitive copy keeps working either way
					return new ClaimHistory { Attempted = new Dictionary<string, DateTime>(history.Attempted, StringComparer.OrdinalIgnoreCase) };
				}
			}
		} catch (Exception) {
			// ignore, start with empty history
		}

		return new ClaimHistory();
	}

	internal static void SaveHistory(ClaimHistory history) {
		try {
			Directory.CreateDirectory(ConfigDir);
			File.WriteAllText(HistoryPath, JsonSerializer.Serialize(history, JsonOptions));
		} catch (Exception) {
			// history is best effort
		}
	}
}
