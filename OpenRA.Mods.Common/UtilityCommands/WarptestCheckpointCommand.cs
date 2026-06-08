#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenRA.FileSystem;

namespace OpenRA.Mods.Common.UtilityCommands
{
	sealed class WarptestCheckpointCommand : IUtilityCommand
	{
		static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		};

		static readonly Regex ObjectiveRegex = new(
			@"Add(?<type>Primary|Secondary)Objective\s*\(\s*(?<owner>[A-Za-z0-9_]+)\s*,\s*""(?<id>[^""]*)""",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		static readonly Regex SafeIdRegex = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

		string IUtilityCommand.Name => "--warptest-checkpoint";

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length == 3;
		}

		[Desc("<SPEC_JSON> <OUT_JSON>", "Validate an OpenRA WarpTest map checkpoint and write a JSON report.")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			var modData = Game.ModData = utility.ModData;
			var requestPath = args[1];
			var outputPath = args[2];
			var report = new CheckReport();

			try
			{
				var request = JsonNode.Parse(File.ReadAllText(requestPath)) as JsonObject;
				var spec = request?["spec"] as JsonObject ?? request;
				if (spec == null)
				{
					report.Add("request.spec", false, "Request JSON must be a mapping or contain a spec mapping.");
					WriteReport(outputPath, report);
					Environment.Exit(1);
					return;
				}

				var target = spec["target"] as JsonObject;
				var mod = ReadString(target, "mod");
				var map = ReadString(target, "map");
				report.Mod = mod;
				report.Map = map;

				report.Add("target.mod_declared", !string.IsNullOrEmpty(mod), "Spec declares target.mod.", mod, modData.Manifest.Id);
				report.Add("target.map_declared", !string.IsNullOrEmpty(map), "Spec declares target.map.", map, map);
				report.Add("target.requires_mod", Same(mod, modData.Manifest.Id), "Utility mod matches target.mod.", mod, modData.Manifest.Id);
				report.Add("target.mod_path_safe", IsSafeId(mod), "target.mod is a simple OpenRA mod id.", mod);
				report.Add("target.map_path_safe", IsSafeId(map), "target.map is a simple OpenRA map directory id.", map);
				if (!IsSafeId(mod) || !IsSafeId(map))
				{
					WriteReport(outputPath, report);
					Environment.Exit(1);
					return;
				}

				var mapsRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "mods", mod, "maps"));
				var mapDir = Path.GetFullPath(Path.Combine(mapsRoot, map));
				var mapYamlPath = Path.Combine(mapDir, "map.yaml");
				report.Add("target.map_path_confined", mapDir.StartsWith(mapsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Map path stays inside the selected mod maps directory.", mapDir, mapsRoot);
				if (!mapDir.StartsWith(mapsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
				{
					WriteReport(outputPath, report);
					Environment.Exit(1);
					return;
				}
				report.Add("target.map_exists", File.Exists(mapYamlPath), "Map directory contains map.yaml.", mapYamlPath, File.Exists(mapYamlPath));
				if (!File.Exists(mapYamlPath))
				{
					WriteReport(outputPath, report);
					Environment.Exit(1);
					return;
				}

				Map loadedMap = null;
				try
				{
					using var rootPackage = new Folder(Directory.GetCurrentDirectory());
					using var package = rootPackage.OpenPackage(Path.Combine("mods", mod, "maps", map), modData.ModFiles);
					if (package != null)
						loadedMap = new Map(modData, package);

					report.Add("target.map_loads", loadedMap != null, "OpenRA Map loaded through the engine map package path.", map, loadedMap?.Title);
				}
				catch (Exception e)
				{
					report.Add("target.map_loads", false, "OpenRA Map load failed.", map, e.Message);
				}
				finally
				{
					loadedMap?.Dispose();
				}

				var mapYaml = new MiniYaml(null, MiniYaml.FromFile(mapYamlPath)).ToDictionary();
				var players = ReadPlayers(mapYaml);
				var actors = ReadActors(mapYaml);
				var objectives = ReadObjectives(mapDir);

				report.Summary["title"] = TopValue(mapYaml, "Title");
				report.Summary["visibility"] = TopValue(mapYaml, "Visibility");
				report.Summary["playerCount"] = players.Count;
				report.Summary["actorCount"] = actors.Count;
				report.Summary["objectiveCount"] = objectives.Count;

				CheckTarget(target, mapYaml, players, actors, objectives, report);
				CheckActions(spec["actions"] as JsonArray, mod, map, report);
				CheckAssertions(spec["assertions"] as JsonArray, mod, map, players, actors, objectives, report);
				WriteReport(outputPath, report);
				if (!report.Success)
					Environment.Exit(1);
			}
			catch (Exception e)
			{
				report.Add("utility.exception", false, "WarpTest checkpoint command failed with an exception.", null, e.ToString());
				WriteReport(outputPath, report);
				Environment.Exit(1);
			}
		}

		static void CheckTarget(
			JsonObject target,
			Dictionary<string, MiniYaml> mapYaml,
			Dictionary<string, PlayerInfo> players,
			List<ActorInfo> actors,
			List<ObjectiveInfo> objectives,
			CheckReport report)
		{
			CheckTopValue(target, mapYaml, "requires_mod", "RequiresMod", "target.requires_mod_yaml", report);
			CheckTopValue(target, mapYaml, "title", "Title", "target.title", report);
			CheckTopValue(target, mapYaml, "visibility", "Visibility", "target.visibility", report);
			CheckCategories(target, mapYaml, report);

			foreach (var playerNode in ReadArray(target, "players"))
				if (playerNode is JsonObject player)
					CheckPlayer("target.players", player, players, report);

			foreach (var actorNode in ReadArray(target, "actors"))
				if (actorNode is JsonObject actor)
					CheckActor("target.actors", actor, actors, report);

			foreach (var objectiveNode in ReadArray(target, "objectives"))
				if (objectiveNode is JsonObject objective)
					CheckObjective("target.objectives", objective, objectives, report);
		}

		static void CheckActions(JsonArray actions, string mod, string map, CheckReport report)
		{
			if (actions == null)
				return;

			for (var i = 0; i < actions.Count; i++)
			{
				if (actions[i] is not JsonObject action)
					continue;

				var type = ReadString(action, "type");
				if (type == "openra_validate_map")
				{
					var ok = Same(ReadString(action, "mod"), mod) && Same(ReadString(action, "map"), map);
					report.Add(
						$"action[{i}].openra_validate_map",
						ok,
						"Smoke action references the loaded OpenRA map.",
						new { mod, map },
						new { Mod = ReadString(action, "mod"), Map = ReadString(action, "map") });
				}
				else
					report.Add($"action[{i}].unsupported", false, "Unsupported OpenRA action type.", "openra_validate_map", type);
			}
		}

		static void CheckAssertions(
			JsonArray assertions,
			string mod,
			string map,
			Dictionary<string, PlayerInfo> players,
			List<ActorInfo> actors,
			List<ObjectiveInfo> objectives,
			CheckReport report)
		{
			if (assertions == null)
				return;

			for (var i = 0; i < assertions.Count; i++)
			{
				if (assertions[i] is not JsonObject assertion)
					continue;

				var type = ReadString(assertion, "type");
				if (type == "openra_map_loaded")
				{
					var ok = Same(ReadString(assertion, "mod"), mod) && Same(ReadString(assertion, "map"), map);
					report.Add(
						$"assertion[{i}].openra_map_loaded",
						ok,
						"Oracle references the loaded OpenRA map.",
						new { mod, map },
						new { Mod = ReadString(assertion, "mod"), Map = ReadString(assertion, "map") });
				}
				else if (type == "openra_player_exists")
					CheckPlayer($"assertion[{i}].openra_player_exists", assertion, players, report);
				else if (type == "openra_actor_count")
					CheckActor($"assertion[{i}].openra_actor_count", assertion, actors, report);
				else if (type == "openra_objective_declared")
					CheckObjective($"assertion[{i}].openra_objective_declared", assertion, objectives, report);
				else if (type == "no_openra_utility_errors")
					report.Add($"assertion[{i}].no_openra_utility_errors", report.Success, "No previous OpenRA utility checks failed.");
				else
					report.Add($"assertion[{i}].unsupported", false, "Unsupported OpenRA assertion type.", null, type);
			}
		}

		static void CheckTopValue(JsonObject target, Dictionary<string, MiniYaml> mapYaml, string targetKey, string yamlKey, string checkName, CheckReport report)
		{
			var expected = ReadString(target, targetKey);
			if (expected == null)
				return;

			var actual = TopValue(mapYaml, yamlKey);
			report.Add(checkName, Same(expected, actual), $"Map {yamlKey} matches target.{targetKey}.", expected, actual);
		}

		static bool IsSafeId(string value)
		{
			return !string.IsNullOrEmpty(value) && SafeIdRegex.IsMatch(value) && !value.Contains("..", StringComparison.Ordinal);
		}

		static void CheckCategories(JsonObject target, Dictionary<string, MiniYaml> mapYaml, CheckReport report)
		{
			var expected = ReadStrings(target, "categories");
			if (expected.Count == 0)
				return;

			var actual = TopValue(mapYaml, "Categories") ?? "";
			var actualTokens = actual.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
			var ok = expected.All(e => actualTokens.Any(a => Same(a, e)));
			report.Add("target.categories", ok, "Map categories contain all expected categories.", expected, actualTokens);
		}

		static void CheckPlayer(string name, JsonObject expected, Dictionary<string, PlayerInfo> players, CheckReport report)
		{
			var id = ReadString(expected, "id") ?? ReadString(expected, "player") ?? ReadString(expected, "name");
			if (id == null)
			{
				report.Add($"{name}.id", false, "Player check requires id/player/name.");
				return;
			}

			var exists = players.TryGetValue(id, out var player);
			report.Add($"{name}.{id}.exists", exists, "PlayerReference entry exists.", id, exists ? player.Id : null);
			if (!exists)
				return;

			CheckPlayerField(name, id, expected, player, "faction", "Faction", report);
			CheckPlayerField(name, id, expected, player, "bot", "Bot", report);
			CheckPlayerBool(name, id, expected, player, "playable", "Playable", report);
			CheckPlayerBool(name, id, expected, player, "required", "Required", report);
		}

		static void CheckPlayerField(string name, string id, JsonObject expected, PlayerInfo player, string jsonKey, string yamlKey, CheckReport report)
		{
			var value = ReadString(expected, jsonKey);
			if (value == null)
				return;

			player.Fields.TryGetValue(yamlKey, out var actual);
			report.Add($"{name}.{id}.{jsonKey}", Same(value, actual), $"Player {id} {yamlKey} matches.", value, actual);
		}

		static void CheckPlayerBool(string name, string id, JsonObject expected, PlayerInfo player, string jsonKey, string yamlKey, CheckReport report)
		{
			var value = ReadBool(expected, jsonKey);
			if (value == null)
				return;

			player.Fields.TryGetValue(yamlKey, out var actualText);
			var actual = Same(actualText, "true");
			report.Add($"{name}.{id}.{jsonKey}", value == actual, $"Player {id} {yamlKey} matches.", value, actual);
		}

		static void CheckActor(string name, JsonObject expected, List<ActorInfo> actors, CheckReport report)
		{
			var type = ReadString(expected, "actor") ?? ReadString(expected, "type");
			if (type == null)
			{
				report.Add($"{name}.type", false, "Actor check requires type/actor.");
				return;
			}

			var owner = ReadString(expected, "owner");
			var location = ReadString(expected, "location");
			var minCount = ReadInt(expected, "min_count") ?? ReadInt(expected, "count") ?? 1;
			var matchPrefix = ReadBool(expected, "match_prefix") ?? false;
			var matching = actors.Where(a =>
				ActorTypeMatches(a.Type, type, matchPrefix) &&
				(owner == null || Same(a.Owner, owner)) &&
				(location == null || Same(a.Location, location))).ToArray();
			report.Add(
				$"{name}.{type}.count",
				matching.Length >= minCount,
				"Actor count/location requirement matches.",
				new { Type = type, Owner = owner, Location = location, MinCount = minCount, MatchPrefix = matchPrefix },
				new { Count = matching.Length, Samples = matching.Take(5).ToArray() });
		}

		static void CheckObjective(string name, JsonObject expected, List<ObjectiveInfo> objectives, CheckReport report)
		{
			var id = ReadString(expected, "id") ?? ReadString(expected, "objective");
			if (id == null)
			{
				report.Add($"{name}.id", false, "Objective check requires id/objective.");
				return;
			}

			var owner = ReadString(expected, "owner");
			var type = ReadString(expected, "objective_type") ?? ReadString(expected, "type");
			var matching = objectives.Where(o => Same(o.Id, id) && (owner == null || Same(o.Owner, owner)) && (type == null || Same(o.Type, type))).ToArray();
			report.Add(
				$"{name}.{id}.declared",
				matching.Length > 0,
				"Lua objective declaration exists.",
				new { Id = id, Owner = owner, Type = type },
				matching);
		}

		static Dictionary<string, PlayerInfo> ReadPlayers(Dictionary<string, MiniYaml> mapYaml)
		{
			var result = new Dictionary<string, PlayerInfo>(StringComparer.OrdinalIgnoreCase);
			if (!mapYaml.TryGetValue("Players", out var playersYaml))
				return result;

			foreach (var node in playersYaml.Nodes)
			{
				const string PlayerReferencePrefix = "PlayerReference@";
				if (!node.Key.StartsWith(PlayerReferencePrefix, StringComparison.Ordinal))
					continue;

				var id = node.Key[PlayerReferencePrefix.Length..];
				var fields = node.Value.Nodes.ToDictionary(n => n.Key, n => n.Value.Value ?? "", StringComparer.OrdinalIgnoreCase);
				result[id] = new PlayerInfo { Id = id, Fields = fields };
			}

			return result;
		}

		static List<ActorInfo> ReadActors(Dictionary<string, MiniYaml> mapYaml)
		{
			var result = new List<ActorInfo>();
			if (!mapYaml.TryGetValue("Actors", out var actorsYaml))
				return result;

			foreach (var node in actorsYaml.Nodes)
			{
				var fields = node.Value.Nodes.ToDictionary(n => n.Key, n => n.Value.Value ?? "", StringComparer.OrdinalIgnoreCase);
				fields.TryGetValue("Owner", out var owner);
				fields.TryGetValue("Location", out var location);
				result.Add(new ActorInfo { Id = node.Key, Type = node.Value.Value ?? "", Owner = owner, Location = location });
			}

			return result;
		}

		static List<ObjectiveInfo> ReadObjectives(string mapDir)
		{
			var result = new List<ObjectiveInfo>();
			foreach (var luaPath in Directory.EnumerateFiles(mapDir, "*.lua", SearchOption.TopDirectoryOnly))
			{
				var text = File.ReadAllText(luaPath);
				foreach (Match match in ObjectiveRegex.Matches(text))
				{
					var id = match.Groups["id"].Value;
					if (string.IsNullOrEmpty(id))
						continue;

					result.Add(new ObjectiveInfo
					{
						Type = match.Groups["type"].Value.ToLowerInvariant(),
						Owner = match.Groups["owner"].Value,
						Id = id,
						Source = Path.GetFileName(luaPath),
					});
				}
			}

			return result;
		}

		static string TopValue(Dictionary<string, MiniYaml> yaml, string key)
		{
			return yaml.TryGetValue(key, out var value) ? value.Value : null;
		}

		static JsonArray ReadArray(JsonObject obj, string key)
		{
			return obj?[key] as JsonArray ?? [];
		}

		static List<string> ReadStrings(JsonObject obj, string key)
		{
			if (obj?[key] is not JsonArray array)
				return [];

			return array.Select(n => n?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToList();
		}

		static string ReadString(JsonObject obj, string key)
		{
			var node = obj?[key];
			return node?.ToString();
		}

		static int? ReadInt(JsonObject obj, string key)
		{
			var text = ReadString(obj, key);
			return int.TryParse(text, out var value) ? value : null;
		}

		static bool? ReadBool(JsonObject obj, string key)
		{
			var text = ReadString(obj, key);
			return bool.TryParse(text, out var value) ? value : null;
		}

		static bool Same(string expected, string actual)
		{
			return string.Equals(expected?.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);
		}

		static bool ActorTypeMatches(string actual, string expected, bool matchPrefix)
		{
			if (Same(actual, expected))
				return true;

			return matchPrefix && actual != null && expected != null && actual.StartsWith(expected + ".", StringComparison.OrdinalIgnoreCase);
		}

		static void WriteReport(string outputPath, CheckReport report)
		{
			report.Status = report.Success ? "success" : "failure";
			var directory = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			File.WriteAllText(outputPath, JsonSerializer.Serialize(report, JsonOptions));
			Console.WriteLine($"WarpTest OpenRA checkpoint {report.Status}: {outputPath}");
		}
	}

	sealed class CheckReport
	{
		public string Status { get; set; } = "failure";
		public string Mod { get; set; }
		public string Map { get; set; }
		public List<CheckResult> Checks { get; } = [];
		public Dictionary<string, object> Summary { get; } = [];
		public bool Success => Checks.All(c => c.Status == "success");

		public void Add(string name, bool success, string detail, object expected = null, object actual = null)
		{
			Checks.Add(new CheckResult
			{
				Name = name,
				Status = success ? "success" : "failure",
				Detail = detail,
				Expected = expected,
				Actual = actual,
			});
		}
	}

	sealed class CheckResult
	{
		public string Name { get; set; }
		public string Status { get; set; }
		public string Detail { get; set; }
		public object Expected { get; set; }
		public object Actual { get; set; }
	}

	sealed class PlayerInfo
	{
		public string Id { get; set; }
		public Dictionary<string, string> Fields { get; set; }
	}

	sealed class ActorInfo
	{
		public string Id { get; set; }
		public string Type { get; set; }
		public string Owner { get; set; }
		public string Location { get; set; }
	}

	sealed class ObjectiveInfo
	{
		public string Type { get; set; }
		public string Owner { get; set; }
		public string Id { get; set; }
		public string Source { get; set; }
	}
}
