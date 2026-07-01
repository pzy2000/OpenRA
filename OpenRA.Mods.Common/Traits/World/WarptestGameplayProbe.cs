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
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Internal WarpTest harness: executes deterministic gameplay probe actions when configured by launch arguments.")]
	public class WarptestGameplayProbeInfo : TraitInfo<WarptestGameplayProbe> { }

	public sealed class WarptestGameplayProbe : IWorldLoaded, ITick
	{
		static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		};

		static string requestPath;
		static string reportPath;

		readonly List<ProbeCheck> checks = [];
		readonly List<string> errors = [];
		readonly List<BuildAttempt> buildAttempts = [];
		readonly Dictionary<(string Player, string Actor), HashSet<uint>> initialActorIds = [];

		World world;
		SpawnMapActors spawnMapActors;
		JsonObject spec;
		JsonArray actions;
		JsonArray assertions;
		JsonObject setup;
		ProbeAction current;
		bool active;
		bool setupApplied;
		bool finished;
		int actionIndex;

		Player primaryPlayer;

		public static void Configure(string request, string report)
		{
			requestPath = request;
			reportPath = report;

			// Tell the engine harness a deterministic probe is driving the world so any
			// configured WarpTest screenshot is deferred until the probe's actions finish.
			Game.NotifyWarptestGameplayProbeConfigured(!string.IsNullOrEmpty(request));
		}

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(reportPath))
				return;

			active = true;
			world = w;
			spawnMapActors = world.WorldActor.TraitOrDefault<SpawnMapActors>();

			try
			{
				var root = JsonNode.Parse(File.ReadAllText(requestPath)) as JsonObject;
				spec = root?["spec"] as JsonObject ?? root;
				if (spec == null)
					throw new InvalidDataException("Request JSON must be a mapping or contain a spec mapping.");

				actions = spec["actions"] as JsonArray ?? [];
				assertions = spec["assertions"] as JsonArray ?? [];
				setup = (spec["setup"] as JsonObject)?["openra_gameplay_probe"] as JsonObject;
				CaptureInitialActors();
			}
			catch (Exception e)
			{
				Fail("probe.request", $"Unable to initialize OpenRA gameplay probe: {e.Message}");
				Finish(false, "OpenRA gameplay probe initialization failed.");
			}
		}

		void ITick.Tick(Actor self)
		{
			if (!active || finished)
				return;

			try
			{
				if (!setupApplied)
				{
					ApplySetup();
					setupApplied = true;
				}

				if (current == null && actionIndex < actions.Count)
					StartAction(actions[actionIndex] as JsonObject);

				if (current != null)
					TickAction();

				if (current == null && actionIndex >= actions.Count)
				{
					EvaluateAssertions();
					var success = checks.All(c => c.Status == "success") && errors.Count == 0;
					Finish(success, success ? "OpenRA gameplay probe completed successfully." : "OpenRA gameplay probe checks failed.");
				}
			}
			catch (Exception e)
			{
				Fail("probe.exception", e.ToString());
				Finish(false, "OpenRA gameplay probe failed with an exception.");
			}
		}

		void CaptureInitialActors()
		{
			foreach (var actor in world.Actors.Where(a => a.IsInWorld && !a.IsDead && a.Owner != null))
			{
				var key = (actor.Owner.InternalName.ToLowerInvariant(), actor.Info.Name.ToLowerInvariant());
				if (!initialActorIds.TryGetValue(key, out var ids))
					initialActorIds[key] = ids = [];

				ids.Add(actor.ActorID);
			}
		}

		void ApplySetup()
		{
			if (setup == null)
				return;

			var cash = ReadInt(setup, "cash", 0);
			var fastBuild = ReadBool(setup, "fast_build", false);
			var buildAnywhere = ReadBool(setup, "build_anywhere", false);

			foreach (var player in world.Players.Where(p => !p.NonCombatant))
			{
				if (cash > 0)
					player.PlayerActor.TraitOrDefault<PlayerResources>()?.ChangeCash(cash);

				var dev = player.PlayerActor.TraitOrDefault<DeveloperMode>();
				if (dev == null)
					continue;

				if (fastBuild && !dev.FastBuild)
					player.PlayerActor.ResolveOrder(new Order(DeveloperMode.Orders.FastBuild, player.PlayerActor, false));
				if (buildAnywhere && !dev.BuildAnywhere)
					player.PlayerActor.ResolveOrder(new Order(DeveloperMode.Orders.BuildAnywhere, player.PlayerActor, false));
			}
		}

		void StartAction(JsonObject action)
		{
			if (action == null)
			{
				Fail($"actions.{actionIndex}", "Action must be a JSON object.");
				actionIndex++;
				return;
			}

			current = new ProbeAction(actionIndex, action);
			switch (current.Type)
			{
				case "openra_wait_ticks":
					current.RemainingTicks = ReadInt(action, "ticks", 0);
					checks.Add(ProbeCheck.Success($"actions.{actionIndex}.start", $"Started wait for {current.RemainingTicks} ticks."));
					break;
				case "openra_deploy_actor":
					StartDeployAction(current);
					break;
				case "openra_build_actor":
					StartBuildAction(current);
					break;
				default:
					Fail($"actions.{actionIndex}.type", $"Unsupported OpenRA gameplay action type: {current.Type}");
					CompleteCurrentAction(false);
					break;
			}
		}

		void TickAction()
		{
			current.ElapsedTicks++;
			if (current.TimeoutTicks > 0 && current.ElapsedTicks > current.TimeoutTicks)
			{
				Fail($"actions.{current.Index}.timeout", $"Timed out waiting for {current.Type} after {current.TimeoutTicks} ticks.");
				CompleteCurrentAction(false);
				return;
			}

			switch (current.Type)
			{
				case "openra_wait_ticks":
					if (--current.RemainingTicks <= 0)
					{
						checks.Add(ProbeCheck.Success($"actions.{current.Index}.complete", "Wait action completed."));
						CompleteCurrentAction(true);
					}

					break;
				case "openra_deploy_actor":
					TickDeployAction(current);
					break;
				case "openra_build_actor":
					TickBuildAction(current);
					break;
			}
		}

		void StartDeployAction(ProbeAction action)
		{
			action.PlayerId = ReadString(action.Node, "player");
			action.ActorId = ReadString(action.Node, "actor_id");
			action.ExpectedActor = ReadString(action.Node, "wait_until_actor");
			action.TimeoutTicks = ReadInt(action.Node, "timeout_ticks", 900);
			action.Player = FindPlayer(action.PlayerId);
			action.Subject = FindMapActor(action.ActorId);
			action.BaselineCount = CountActors(action.Player, action.ExpectedActor);

			if (action.Player == null)
			{
				Fail($"actions.{action.Index}.player", $"Unknown player: {action.PlayerId}");
				CompleteCurrentAction(false);
				return;
			}

			primaryPlayer ??= action.Player;

			if (action.Subject == null || action.Subject.IsDead)
			{
				Fail($"actions.{action.Index}.actor_id", $"Unknown or dead map actor: {action.ActorId}");
				CompleteCurrentAction(false);
				return;
			}

			var deploy = action.Subject.TraitsImplementing<IIssueDeployOrder>()
				.FirstOrDefault(d => d.CanIssueDeployOrder(action.Subject, false));
			if (deploy == null)
			{
				Fail($"actions.{action.Index}.deploy", $"Actor {action.ActorId} cannot issue a deploy order.");
				CompleteCurrentAction(false);
				return;
			}

			var order = deploy.IssueDeployOrder(action.Subject, false);
			if (order == null)
			{
				Fail($"actions.{action.Index}.deploy", $"Actor {action.ActorId} returned no deploy order.");
				CompleteCurrentAction(false);
				return;
			}

			action.Subject.ResolveOrder(order);
			checks.Add(ProbeCheck.Success($"actions.{action.Index}.start", $"Issued deploy order for {action.ActorId}."));
		}

		void TickDeployAction(ProbeAction action)
		{
			if (string.IsNullOrEmpty(action.ExpectedActor))
			{
				checks.Add(ProbeCheck.Success($"actions.{action.Index}.complete", "Deploy order issued."));
				CompleteCurrentAction(true);
				return;
			}

			var count = CountActors(action.Player, action.ExpectedActor);
			if (count > action.BaselineCount)
			{
				checks.Add(ProbeCheck.Success($"actions.{action.Index}.complete", $"Observed deployed actor {action.ExpectedActor}.", action.BaselineCount + 1, count));
				CompleteCurrentAction(true);
			}
		}

		void StartBuildAction(ProbeAction action)
		{
			action.PlayerId = ReadString(action.Node, "player");
			action.ActorType = ReadString(action.Node, "actor");
			action.QueueType = ReadString(action.Node, "queue") ?? "Building";
			action.TimeoutTicks = ReadInt(action.Node, "timeout_ticks", 900);
			action.Player = FindPlayer(action.PlayerId);
			action.ActorInfo = FindActorInfo(action.ActorType);

			if (action.Player == null)
			{
				Fail($"actions.{action.Index}.player", $"Unknown player: {action.PlayerId}");
				CompleteCurrentAction(false);
				return;
			}

			primaryPlayer ??= action.Player;

			if (action.ActorInfo == null)
			{
				Fail($"actions.{action.Index}.actor", $"Unknown actor type: {action.ActorType}");
				CompleteCurrentAction(false);
				return;
			}

			action.Queue = world.ActorsWithTrait<ProductionQueue>()
				.Where(q => q.Actor.Owner == action.Player && q.Trait.Info.Type == action.QueueType)
				.Select(q => q.Trait)
				.FirstOrDefault(q => q.BuildableItems().Any(i => i.Name == action.ActorType));

			if (action.Queue == null)
			{
				var candidate = world.ActorsWithTrait<ProductionQueue>()
					.Where(q => q.Actor.Owner == action.Player && q.Trait.Info.Type == action.QueueType)
					.Select(q => q.Trait)
					.FirstOrDefault();
				var buildable = candidate?.BuildableItems()
					.OrderBy(i => i.Name)
					.Select(i => i.Name)
					.ToArray() ?? [];
				Fail(
					$"actions.{action.Index}.queue",
					$"No {action.QueueType} queue can currently build {action.ActorType}.",
					action.ActorType,
					string.Join(",", buildable));
				buildAttempts.Add(new BuildAttempt(action.PlayerId, action.ActorType, false, false));
				CompleteCurrentAction(false);
				return;
			}

			var prerequisiteRespected = action.Queue.CanBuild(action.ActorInfo);
			buildAttempts.Add(new BuildAttempt(action.PlayerId, action.ActorType, prerequisiteRespected, true));
			if (!prerequisiteRespected)
			{
				Fail($"actions.{action.Index}.prerequisite", $"{action.PlayerId} does not satisfy prerequisites for {action.ActorType}.");
				CompleteCurrentAction(false);
				return;
			}

			CancelQueuedProduction(action.Queue);
			action.BaselineCount = CountNewActors(action.PlayerId, action.ActorType);
			action.QueuedCountBefore = CountQueued(action.Queue, action.ActorType);
			action.Queue.Actor.ResolveOrder(Order.StartProduction(action.Queue.Actor, action.ActorType, 1, false));

			var queuedAfter = CountQueued(action.Queue, action.ActorType);
			if (queuedAfter <= action.QueuedCountBefore && CountNewActors(action.PlayerId, action.ActorType) <= action.BaselineCount)
			{
				Fail(
					$"actions.{action.Index}.queue",
					$"{action.QueueType} queue rejected production order for {action.ActorType}.",
					action.QueuedCountBefore + 1,
					queuedAfter);
				CompleteCurrentAction(false);
				return;
			}

			checks.Add(ProbeCheck.Success($"actions.{action.Index}.start", $"Queued {action.ActorType} on {action.QueueType}."));
		}

		void TickBuildAction(ProbeAction action)
		{
			var isBuilding = action.ActorInfo.HasTraitInfo<BuildingInfo>();
			if (isBuilding)
			{
				var item = action.Queue.AllQueued().FirstOrDefault(i => i.Done && i.Item == action.ActorType);
				if (item != null && !action.PlacementIssued)
				{
					if (!TryFindPlacement(action, out var cell, out var placementError))
					{
						Fail($"actions.{action.Index}.place", placementError);
						CompleteCurrentAction(false);
						return;
					}

					action.PlacementIssued = true;
					action.Player.PlayerActor.ResolveOrder(new Order("PlaceBuilding", action.Player.PlayerActor, Target.FromCell(world, cell), false)
					{
						TargetString = action.ActorType,
						ExtraData = action.Queue.Actor.ActorID,
						ExtraLocation = CPos.Zero,
						SuppressVisualFeedback = true
					});
					checks.Add(ProbeCheck.Success($"actions.{action.Index}.place", $"Issued placement order for {action.ActorType} at {cell}."));
				}
			}

			var built = CountNewActors(action.PlayerId, action.ActorType);
			if (built > action.BaselineCount)
			{
				checks.Add(ProbeCheck.Success($"actions.{action.Index}.complete", $"Observed new {action.ActorType}.", action.BaselineCount + 1, built));
				CompleteCurrentAction(true);
				return;
			}

			if (!isBuilding && action.Queue.AllQueued().Any(i => i.Done && i.Item == action.ActorType))
			{
				if (!action.ProductionDoneObserved)
				{
					action.ProductionDoneObserved = true;
					action.PostCompleteGraceTicks = ReadInt(action.Node, "post_complete_grace_ticks", 30);
				}

				if (++action.ProductionDoneElapsedTicks >= action.PostCompleteGraceTicks)
				{
					checks.Add(ProbeCheck.Success(
						$"actions.{action.Index}.complete",
						$"Production item completed for {action.ActorType}."));
					CompleteCurrentAction(true);
				}
			}
		}

		bool TryFindPlacement(ProbeAction action, out CPos cell, out string error)
		{
			var bi = action.ActorInfo.TraitInfo<BuildingInfo>();
			var placeAt = ReadString(action.Node, "place_at");
			if (TryParseCell(placeAt, out cell) && CanPlace(action, bi, cell))
			{
				error = null;
				return true;
			}

			var placeNear = action.Node["place_near"] as JsonObject;
			var anchorText = ReadString(placeNear, "anchor");
			var radius = ReadInt(placeNear, "radius", 0);
			if (TryParseCell(anchorText, out var anchor))
			{
				foreach (var candidate in CandidateCells(anchor, Math.Max(radius, 0)))
				{
					if (!CanPlace(action, bi, candidate))
						continue;

					cell = candidate;
					error = null;
					return true;
				}
			}

			foreach (var producer in world.ActorsWithTrait<Production>().Where(p => p.Actor.Owner == action.Player))
			{
				foreach (var candidate in CandidateCells(producer.Actor.Location, 12))
				{
					if (!CanPlace(action, bi, candidate))
						continue;

					cell = candidate;
					error = null;
					return true;
				}
			}

			cell = CPos.Zero;
			error = $"No valid placement cell found for {action.ActorType}.";
			return false;
		}

		bool CanPlace(ProbeAction action, BuildingInfo bi, CPos cell)
		{
			return world.CanPlaceBuilding(cell, action.ActorInfo, bi, null)
				&& bi.IsCloseEnoughToBase(world, action.Player, action.ActorInfo, cell);
		}

		static void CancelQueuedProduction(ProductionQueue queue)
		{
			foreach (var group in queue.AllQueued().Select(i => i.Item).GroupBy(i => i).ToArray())
				queue.Actor.ResolveOrder(Order.CancelProduction(queue.Actor, group.Key, group.Count()));
		}

		static int CountQueued(ProductionQueue queue, string actor)
		{
			return queue.AllQueued().Count(i => i.Item == actor);
		}

		static IEnumerable<CPos> CandidateCells(CPos anchor, int radius)
		{
			yield return anchor;
			for (var r = 1; r <= radius; r++)
				for (var dy = -r; dy <= r; dy++)
					for (var dx = -r; dx <= r; dx++)
					{
						if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
							continue;

						yield return new CPos(anchor.X + dx, anchor.Y + dy);
					}
		}

		void CompleteCurrentAction(bool success)
		{
			if (!success)
				errors.Add($"Action {current?.Index ?? actionIndex} failed.");

			current = null;
			actionIndex++;
		}

		void EvaluateAssertions()
		{
			for (var i = 0; i < assertions.Count; i++)
			{
				if (assertions[i] is not JsonObject assertion)
				{
					Fail($"assertions.{i}", "Assertion must be a JSON object.");
					continue;
				}

				switch (ReadString(assertion, "type"))
				{
					case "openra_actor_exists":
						CheckActorExists(i, assertion);
						break;
					case "openra_actor_built":
						CheckActorBuilt(i, assertion);
						break;
					case "openra_production_prerequisite_respected":
						CheckPrerequisiteRespected(i, assertion);
						break;
					case "no_openra_gameplay_probe_errors":
						AddCheck($"assertions.{i}.no_probe_errors", errors.Count == 0, "Gameplay probe recorded no errors.", 0, errors.Count);
						break;
					default:
						Fail($"assertions.{i}.type", $"Unsupported assertion type: {ReadString(assertion, "type")}");
						break;
				}
			}
		}

		void CheckActorExists(int index, JsonObject assertion)
		{
			var actorId = ReadString(assertion, "actor_id");
			var actor = FindMapActor(actorId);
			var exists = actor != null && actor.IsInWorld && !actor.IsDead;
			AddCheck(
				$"assertions.{index}.actor_exists",
				exists,
				$"Map actor {actorId} exists in-world.",
				true,
				exists);
		}

		void CheckActorBuilt(int index, JsonObject assertion)
		{
			var player = ReadString(assertion, "player");
			var actor = ReadString(assertion, "actor");
			var minDelta = ReadInt(assertion, "min_delta", 1);
			var built = CountNewActors(player, actor);
			AddCheck($"assertions.{index}.actor_built", built >= minDelta, $"{player} built at least {minDelta} new {actor}.", minDelta, built);
		}

		void CheckPrerequisiteRespected(int index, JsonObject assertion)
		{
			var player = ReadString(assertion, "player");
			var actor = ReadString(assertion, "actor");
			var attempts = buildAttempts.Where(a => a.Player == player && a.Actor == actor).ToArray();
			var ok = attempts.Length > 0 && attempts.All(a => a.QueueAccepted && a.PrerequisiteRespected);
			AddCheck($"assertions.{index}.prerequisite_respected", ok, $"{player} only produced {actor} after prerequisites were available.", true, ok);
		}

		void Finish(bool success, string detail)
		{
			if (finished)
				return;

			finished = true;
			var report = new JsonObject
			{
				["status"] = success ? "success" : "failure",
				["detail"] = detail,
				["checks"] = new JsonArray(checks.Select(c => c.ToJson()).ToArray()),
				["errors"] = new JsonArray(errors.Select(e => JsonValue.Create(e)).ToArray()),
				["summary"] = new JsonObject
				{
					["actionsExecuted"] = Math.Min(actionIndex, actions?.Count ?? 0),
					["actionsTotal"] = actions?.Count ?? 0,
					["worldTick"] = world?.WorldTick ?? 0,
				}
			};

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
				File.WriteAllText(reportPath, report.ToJsonString(JsonOptions));
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to write WarpTest gameplay report: {e}");
			}

			PrepareScreenshotView();

			// Hand off to the engine: when a WarpTest screenshot is configured this defers
			// the process exit until the post-action frame has been captured; otherwise the
			// engine exits immediately with this code (preserving the previous behaviour).
			Game.CompleteWarptestGameplayProbe(success ? 0 : 1);
		}

		void PrepareScreenshotView()
		{
			try
			{
				// Reveal the map for the render player's camera. Production tasks may act on a
				// player other than the rendered one (e.g. building the enemy's units from the
				// human's perspective), whose base would otherwise sit under fog and capture an
				// all-black frame. This is a harness-only capture (dev cheats already enabled).
				if (world.RenderPlayer != null)
					world.RenderPlayer.Shroud.Disabled = true;

				// Center on the most recently created building owned by the acting player so the
				// frame focuses on the constructed base and the newly produced units beside it.
				if (primaryPlayer != null)
				{
					var building = world.Actors
						.Where(a => a.IsInWorld && !a.IsDead && a.Owner == primaryPlayer && a.Info.HasTraitInfo<BuildingInfo>())
						.OrderByDescending(a => a.ActorID)
						.FirstOrDefault();
					if (building != null)
						Game.SetWarptestGameplayProbeCenter(building.CenterPosition);
				}
			}
			catch (Exception e)
			{
				Log.Write("debug", $"WarpTest gameplay probe could not prepare the screenshot view: {e.Message}");
			}
		}

		void Fail(string name, string detail, object expected = null, object actual = null)
		{
			AddCheck(name, false, detail, expected, actual);
		}

		void AddCheck(string name, bool success, string detail, object expected = null, object actual = null)
		{
			checks.Add(new ProbeCheck(name, success ? "success" : "failure", detail, expected, actual));
			if (!success)
				errors.Add(detail);
		}

		Player FindPlayer(string id)
		{
			if (string.IsNullOrEmpty(id))
				return null;

			return world.Players.FirstOrDefault(p => string.Equals(p.InternalName, id, StringComparison.OrdinalIgnoreCase));
		}

		ActorInfo FindActorInfo(string actor)
		{
			if (string.IsNullOrEmpty(actor))
				return null;

			world.Map.Rules.Actors.TryGetValue(actor.ToLowerInvariant(), out var info);
			return info;
		}

		Actor FindMapActor(string id)
		{
			if (string.IsNullOrEmpty(id))
				return null;

			if (spawnMapActors?.Actors.TryGetValue(id, out var actor) == true)
				return actor;

			return world.Actors.FirstOrDefault(a => string.Equals(a.Info.Name, id, StringComparison.OrdinalIgnoreCase));
		}

		int CountActors(Player player, string actor)
		{
			if (player == null || string.IsNullOrEmpty(actor))
				return 0;

			return world.Actors.Count(a => a.IsInWorld && !a.IsDead && a.Owner == player && string.Equals(a.Info.Name, actor, StringComparison.OrdinalIgnoreCase));
		}

		int CountNewActors(string player, string actor)
		{
			if (string.IsNullOrEmpty(player) || string.IsNullOrEmpty(actor))
				return 0;

			initialActorIds.TryGetValue((player.ToLowerInvariant(), actor.ToLowerInvariant()), out var initial);
			initial ??= [];
			return world.Actors.Count(a => a.IsInWorld && !a.IsDead && a.Owner != null
				&& string.Equals(a.Owner.InternalName, player, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(a.Info.Name, actor, StringComparison.OrdinalIgnoreCase)
				&& !initial.Contains(a.ActorID));
		}

		static string ReadString(JsonObject obj, string key)
		{
			return obj != null && obj.TryGetPropertyValue(key, out var value) ? value?.GetValue<string>() : null;
		}

		static int ReadInt(JsonObject obj, string key, int fallback)
		{
			if (obj == null || !obj.TryGetPropertyValue(key, out var value) || value == null)
				return fallback;

			return value.GetValue<int>();
		}

		static bool ReadBool(JsonObject obj, string key, bool fallback)
		{
			if (obj == null || !obj.TryGetPropertyValue(key, out var value) || value == null)
				return fallback;

			return value.GetValue<bool>();
		}

		static bool TryParseCell(string value, out CPos cell)
		{
			cell = CPos.Zero;
			if (string.IsNullOrEmpty(value))
				return false;

			var parts = value.Split(',');
			if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y))
				return false;

			cell = new CPos(x, y);
			return true;
		}

		sealed class ProbeAction
		{
			public readonly int Index;
			public readonly JsonObject Node;
			public readonly string Type;
			public int TimeoutTicks;
			public int ElapsedTicks;
			public int RemainingTicks;
			public int BaselineCount;
			public int QueuedCountBefore;
			public int PostCompleteGraceTicks;
			public int ProductionDoneElapsedTicks;
			public bool PlacementIssued;
			public bool ProductionDoneObserved;
			public string PlayerId;
			public string ActorId;
			public string ActorType;
			public string QueueType;
			public string ExpectedActor;
			public Player Player;
			public Actor Subject;
			public ActorInfo ActorInfo;
			public ProductionQueue Queue;

			public ProbeAction(int index, JsonObject node)
			{
				Index = index;
				Node = node;
				Type = ReadString(node, "type");
			}
		}

		sealed record BuildAttempt(string Player, string Actor, bool PrerequisiteRespected, bool QueueAccepted);

		sealed record ProbeCheck(string Name, string Status, string Detail, object Expected = null, object Actual = null)
		{
			public static ProbeCheck Success(string name, string detail, object expected = null, object actual = null)
			{
				return new ProbeCheck(name, "success", detail, expected, actual);
			}

			public JsonObject ToJson()
			{
				var json = new JsonObject
				{
					["name"] = Name,
					["status"] = Status,
					["detail"] = Detail,
				};

				if (Expected != null)
					json["expected"] = JsonValue.Create(Expected.ToString());
				if (Actual != null)
					json["actual"] = JsonValue.Create(Actual.ToString());

				return json;
			}
		}
	}
}
