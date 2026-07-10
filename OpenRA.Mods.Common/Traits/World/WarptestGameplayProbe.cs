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
		static readonly Regex SafeIdRegex = new("^[A-Za-z0-9_.-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
		static FuzzSession fuzzSession;

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
		JsonObject fuzzState;
		ProbeAction current;
		bool active;
		bool setupApplied;
		bool finished;
		bool fuzzReadyPending;
		bool fuzzCandidateActive;
		long fuzzSequence;
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

		public static void ConfigureFuzzSession(string request, string report, string ready, string map, string screenshotDirectory)
		{
			if (string.IsNullOrEmpty(request) || string.IsNullOrEmpty(report) || string.IsNullOrEmpty(ready))
			{
				fuzzSession = null;
				return;
			}

			fuzzSession = new FuzzSession(request, report, ready, map, screenshotDirectory);
		}

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			if (fuzzSession != null)
			{
				InitializeFuzzSessionWorld(w);
				return;
			}

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
			if (fuzzSession != null)
			{
				TickFuzzSession();
				return;
			}

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

		void InitializeFuzzSessionWorld(World w)
		{
			active = true;
			world = w;
			spawnMapActors = world.WorldActor.TraitOrDefault<SpawnMapActors>();
			fuzzReadyPending = true;
			CaptureInitialActors();
		}

		void TickFuzzSession()
		{
			if (!active || finished)
				return;

			try
			{
				// Publish readiness from the first world tick, rather than WorldLoaded, so the
				// external harness only observes a world that has started accepting orders.
				if (fuzzReadyPending)
				{
					fuzzReadyPending = false;
					WriteFuzzReady();
					return;
				}

				if (!fuzzCandidateActive)
				{
					TryStartFuzzCandidate();
					return;
				}

				if (!setupApplied)
				{
					ApplyFuzzState();
					setupApplied = true;
				}

				if (current == null && actionIndex < actions.Count)
					StartAction(actions[actionIndex] as JsonObject);

				if (current != null)
					TickAction();

				if (current == null && actionIndex >= actions.Count)
				{
					var success = checks.All(c => c.Status == "success") && errors.Count == 0;
					CompleteFuzzCandidate(
						success ? "success" : "robust",
						success ? "completed" : ClassifyRobustFailure(),
						success ? "OpenRA C3 fuzz candidate completed." : errors.FirstOrDefault() ?? "OpenRA C3 fuzz candidate was rejected by gameplay preconditions.");
				}
			}
			catch (Exception e)
			{
				CompleteFuzzCandidate("engine_error", "exception", e.ToString());
			}
		}

		void TryStartFuzzCandidate()
		{
			if (!File.Exists(fuzzSession.RequestPath))
				return;

			string requestText;
			string requestFingerprint;
			try
			{
				var requestInfo = new FileInfo(fuzzSession.RequestPath);
				if (requestInfo.Length > FuzzSession.MaxRequestBytes)
				{
					RejectFuzzRequest(fuzzSession.LastCompletedSequence + 1,
						$"C3 fuzz request exceeds the {FuzzSession.MaxRequestBytes} byte protocol limit.",
						requestInfo.Length + ":" + requestInfo.LastWriteTimeUtc.Ticks);
					return;
				}

				requestText = File.ReadAllText(fuzzSession.RequestPath);
				requestFingerprint = requestInfo.Length + ":" + requestInfo.LastWriteTimeUtc.Ticks;
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Unable to read WarpTest C3 fuzz request: {e.Message}");
				return;
			}

			JsonObject root;
			try
			{
				root = JsonNode.Parse(requestText) as JsonObject;
				if (root == null)
					throw new InvalidDataException("Request JSON must be an object.");
			}
			catch (Exception e)
			{
				RejectFuzzRequest(fuzzSession.LastCompletedSequence + 1,
					"Unable to parse C3 fuzz request JSON: " + e.Message, requestFingerprint);
				return;
			}

			if (!TryValidateFuzzRequest(root, out var request, out var sequence, out var detail))
			{
				if (sequence <= fuzzSession.LastCompletedSequence)
					return;

				RejectFuzzRequest(sequence > 0 ? sequence : fuzzSession.LastCompletedSequence + 1, detail, requestFingerprint);
				return;
			}

			if (request.Sequence <= fuzzSession.LastCompletedSequence)
				return;

			fuzzSequence = request.Sequence;
			fuzzState = request.FuzzState;
			actions = fuzzState["actions"] as JsonArray ?? [];
			assertions = [];
			setup = null;
			current = null;
			actionIndex = 0;
			setupApplied = false;
			fuzzCandidateActive = true;
			checks.Add(ProbeCheck.Success("session.sequence", $"Accepted C3 fuzz request sequence {fuzzSequence}."));
		}

		void RejectFuzzRequest(long sequence, string detail, string fingerprint)
		{
			if (string.Equals(fuzzSession.LastRejectedRequestFingerprint, fingerprint, StringComparison.Ordinal))
				return;

			fuzzSession.LastRejectedRequestFingerprint = fingerprint;
			fuzzSequence = sequence;
			actions = [];
			assertions = [];
			Fail("session.protocol", detail);
			CompleteFuzzCandidate("rejected", "protocol", detail);
		}

		void ApplyFuzzState()
		{
			var players = fuzzState?["players"] as JsonArray ?? [];
			for (var i = 0; i < players.Count; i++)
			{
				var state = players[i] as JsonObject;
				var id = ReadString(state, "id");
				var player = FindPlayer(id);
				if (player == null)
				{
					Fail($"players.{i}.id", $"Unknown player: {id}");
					continue;
				}

				primaryPlayer ??= player;
				var resources = player.PlayerActor.TraitOrDefault<PlayerResources>();
				if (TryReadInt(state, "cash", out var cash))
				{
					if (resources == null)
						Fail($"players.{i}.cash", $"Player {id} does not expose PlayerResources.");
					else
						resources.ChangeCash(cash - resources.GetCashAndResources());
				}

				var developerMode = player.PlayerActor.TraitOrDefault<DeveloperMode>();
				if (TryReadBool(state, "fast_build", out var fastBuild))
					SetFuzzDeveloperFlag(i, id, player, developerMode, fastBuild,
						developerMode?.FastBuild ?? false, DeveloperMode.Orders.FastBuild, "fast_build");
				if (TryReadBool(state, "build_anywhere", out var buildAnywhere))
					SetFuzzDeveloperFlag(i, id, player, developerMode, buildAnywhere,
						developerMode?.BuildAnywhere ?? false, DeveloperMode.Orders.BuildAnywhere, "build_anywhere");
			}
		}

		void SetFuzzDeveloperFlag(int playerIndex, string playerId, Player player, DeveloperMode developerMode,
			bool requested, bool currentValue, string order, string field)
		{
			if (developerMode == null)
			{
				Fail($"players.{playerIndex}.{field}", $"Player {playerId} does not expose DeveloperMode.");
				return;
			}

			if (requested == currentValue)
				return;

			player.PlayerActor.ResolveOrder(new Order(order, player.PlayerActor, false));
			var appliedValue = order == DeveloperMode.Orders.FastBuild
				? developerMode.FastBuild
				: developerMode.BuildAnywhere;
			if (requested != appliedValue)
				Fail($"players.{playerIndex}.{field}", $"Player {playerId} did not apply requested {field} state.");
		}

		void CompleteFuzzCandidate(string status, string category, string detail)
		{
			if (finished)
				return;

			finished = true;
			active = false;
			fuzzCandidateActive = false;
			PrepareScreenshotView();

			if (!string.IsNullOrEmpty(fuzzSession.ScreenshotDirectory))
			{
				try
				{
					var screenshotPath = Path.Combine(fuzzSession.ScreenshotDirectory, $"candidate-{fuzzSequence}.png");
					Game.RequestWarptestFuzzScreenshot(screenshotPath,
						capturedPath => FinalizeFuzzCandidate(status, category, detail, capturedPath));
					return;
				}
				catch (Exception e)
				{
					Log.Write("debug", $"Failed to schedule WarpTest C3 fuzz screenshot: {e}");
				}
			}

			FinalizeFuzzCandidate(status, category, detail, null);
		}

		void FinalizeFuzzCandidate(string status, string category, string detail, string screenshotPath)
		{
			var report = new JsonObject
			{
				["version"] = FuzzSession.ProtocolVersion,
				["sequence"] = fuzzSequence,
				["status"] = status,
				["category"] = category,
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

			if (!string.IsNullOrEmpty(screenshotPath))
				report["screenshot_path"] = screenshotPath;

			try
			{
				WriteJsonAtomically(fuzzSession.ReportPath, report);
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to write WarpTest C3 fuzz report: {e}");
			}

			fuzzSession.LastCompletedSequence = fuzzSequence;
			Game.RunAfterTick(RestartFuzzSessionMap);
		}

		void RestartFuzzSessionMap()
		{
			try
			{
				Game.RestartGamePreservingSeed();
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to reset WarpTest C3 fuzz session: {e}");
			}
		}

		static void WriteFuzzReady()
		{
			var ready = new JsonObject
			{
				["version"] = FuzzSession.ProtocolVersion,
				["status"] = "ready",
				["sequence"] = fuzzSession.LastCompletedSequence,
			};

			if (!string.IsNullOrEmpty(fuzzSession.Map))
				ready["map"] = fuzzSession.Map;

			try
			{
				WriteJsonAtomically(fuzzSession.ReadyPath, ready);
			}
			catch (Exception e)
			{
				Log.Write("debug", $"Failed to write WarpTest C3 fuzz ready signal: {e}");
			}
		}

		string ClassifyRobustFailure()
		{
			var detail = errors.FirstOrDefault() ?? "";
			if (detail.Contains("Unknown player", StringComparison.OrdinalIgnoreCase))
				return "unknown_player";
			if (detail.Contains("Unknown actor", StringComparison.OrdinalIgnoreCase) || detail.Contains("Unknown or dead map actor", StringComparison.OrdinalIgnoreCase))
				return "unknown_actor";
			if (detail.Contains("prerequisite", StringComparison.OrdinalIgnoreCase))
				return "prerequisite_rejected";
			if (detail.Contains("queue", StringComparison.OrdinalIgnoreCase))
				return "queue_rejected";

			return "action_rejected";
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

		static bool TryValidateFuzzRequest(JsonObject root, out FuzzRequest request, out long sequence, out string detail)
		{
			request = null;

			if (!TryReadLong(root, "sequence", out sequence) || sequence < 1)
			{
				detail = "C3 fuzz request sequence must be a positive integer.";
				return false;
			}

			if (!HasOnlyProperties(root, "version", "sequence", "fuzz_state"))
			{
				detail = "C3 fuzz request contains fields outside the session protocol.";
				return false;
			}

			if (!TryReadString(root, "version", out var version) || !string.Equals(version, FuzzSession.ProtocolVersion, StringComparison.Ordinal))
			{
				detail = $"C3 fuzz request version must be {FuzzSession.ProtocolVersion}.";
				return false;
			}

			if (!root.TryGetPropertyValue("fuzz_state", out var stateNode) || stateNode is not JsonObject state)
			{
				detail = "C3 fuzz request fuzz_state must be an object.";
				return false;
			}

			if (!ValidateFuzzState(state, out detail))
				return false;

			request = new FuzzRequest(sequence, state);
			return true;
		}

		static bool ValidateFuzzState(JsonObject state, out string detail)
		{
			detail = null;
			if (!HasOnlyProperties(state, "players", "actions"))
			{
				detail = "fuzz_state contains fields outside the supported players/actions whitelist.";
				return false;
			}

			if (!state.ContainsKey("players") || !state.ContainsKey("actions"))
			{
				detail = "fuzz_state must contain both players and actions arrays.";
				return false;
			}

			if (state.TryGetPropertyValue("players", out var playerNodes))
			{
				if (playerNodes is not JsonArray players)
				{
					detail = "fuzz_state.players must be an array.";
					return false;
				}

				if (players.Count > FuzzSession.MaxPlayers)
				{
					detail = $"fuzz_state.players may contain at most {FuzzSession.MaxPlayers} entries.";
					return false;
				}

				var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				for (var i = 0; i < players.Count; i++)
					if (!ValidateFuzzPlayer(players[i] as JsonObject, i, ids, out detail))
						return false;
			}

			if (state.TryGetPropertyValue("actions", out var actionNodes))
			{
				if (actionNodes is not JsonArray fuzzActions)
				{
					detail = "fuzz_state.actions must be an array.";
					return false;
				}

				if (fuzzActions.Count > FuzzSession.MaxActions)
				{
					detail = $"fuzz_state.actions may contain at most {FuzzSession.MaxActions} entries.";
					return false;
				}

				for (var i = 0; i < fuzzActions.Count; i++)
					if (!ValidateFuzzAction(fuzzActions[i] as JsonObject, i, out detail))
						return false;
			}

			return true;
		}

		static bool ValidateFuzzPlayer(JsonObject player, int index, HashSet<string> ids, out string detail)
		{
			if (player == null || !HasOnlyProperties(player, "id", "cash", "fast_build", "build_anywhere"))
			{
				detail = $"fuzz_state.players[{index}] must be an object limited to id, cash, fast_build, and build_anywhere.";
				return false;
			}

			if (!TryReadString(player, "id", out var id) || !IsSafeId(id))
			{
				detail = $"fuzz_state.players[{index}].id must be a safe id with at most {FuzzSession.MaxIdLength} characters.";
				return false;
			}

			if (!ids.Add(id))
			{
				detail = $"fuzz_state.players[{index}].id duplicates a previous player id.";
				return false;
			}

			if (!player.ContainsKey("cash") || !player.ContainsKey("fast_build") || !player.ContainsKey("build_anywhere"))
			{
				detail = $"fuzz_state.players[{index}] must include cash, fast_build, and build_anywhere.";
				return false;
			}

			if (!ValidateRequiredInt(player, "cash", 0, FuzzSession.MaxCash, out detail) ||
				!ValidateRequiredBool(player, "fast_build", out detail) ||
				!ValidateRequiredBool(player, "build_anywhere", out detail))
			{
				detail = $"fuzz_state.players[{index}]: {detail}";
				return false;
			}

			detail = null;
			return true;
		}

		static bool ValidateFuzzAction(JsonObject action, int index, out string detail)
		{
			detail = null;
			if (action == null || !TryReadString(action, "type", out var type))
			{
				detail = $"fuzz_state.actions[{index}].type must name an existing OpenRA gameplay action.";
				return false;
			}

			switch (type)
			{
				case "openra_wait_ticks":
					if (!HasOnlyProperties(action, "type", "ticks") || !ValidateRequiredInt(action, "ticks", 0, FuzzSession.MaxTicks, out detail))
					{
						detail = $"fuzz_state.actions[{index}]: {detail ?? "openra_wait_ticks only accepts a bounded ticks field."}";
						return false;
					}

					return true;

				case "openra_deploy_actor":
					if (!HasOnlyProperties(action, "type", "player", "actor_id", "wait_until_actor", "timeout_ticks") ||
						!ValidateRequiredSafeId(action, "player", out detail) ||
						!ValidateRequiredSafeId(action, "actor_id", out detail) ||
						!ValidateOptionalSafeId(action, "wait_until_actor", out detail) ||
						!ValidateOptionalInt(action, "timeout_ticks", 0, FuzzSession.MaxTicks, out detail))
					{
						detail = $"fuzz_state.actions[{index}]: {detail ?? "invalid openra_deploy_actor fields."}";
						return false;
					}

					return true;

				case "openra_build_actor":
					if (!HasOnlyProperties(action, "type", "player", "actor", "queue", "timeout_ticks", "place_at", "place_near", "post_complete_grace_ticks") ||
						!ValidateRequiredSafeId(action, "player", out detail) ||
						!ValidateRequiredSafeId(action, "actor", out detail) ||
						!ValidateOptionalSafeId(action, "queue", out detail) ||
						!ValidateOptionalInt(action, "timeout_ticks", 0, FuzzSession.MaxTicks, out detail) ||
						!ValidateOptionalInt(action, "post_complete_grace_ticks", 0, FuzzSession.MaxTicks, out detail) ||
						!ValidateOptionalCell(action, "place_at", out detail) ||
						!ValidateOptionalPlaceNear(action, out detail))
					{
						detail = $"fuzz_state.actions[{index}]: {detail ?? "invalid openra_build_actor fields."}";
						return false;
					}

					return true;

				default:
					detail = $"fuzz_state.actions[{index}].type {type} is not an exposed OpenRA gameplay action.";
					return false;
			}
		}

		static bool ValidateRequiredSafeId(JsonObject node, string key, out string detail)
		{
			detail = null;
			if (TryReadString(node, key, out var value) && IsSafeId(value))
				return true;

			detail = $"{key} must be a safe id with at most {FuzzSession.MaxIdLength} characters.";
			return false;
		}

		static bool ValidateOptionalSafeId(JsonObject node, string key, out string detail)
		{
			detail = null;
			if (!node.TryGetPropertyValue(key, out _))
				return true;

			if (TryReadString(node, key, out var text) && IsSafeId(text))
				return true;

			detail = $"{key} must be a safe id with at most {FuzzSession.MaxIdLength} characters.";
			return false;
		}

		static bool ValidateRequiredInt(JsonObject node, string key, int minimum, int maximum, out string detail)
		{
			detail = null;
			if (TryReadInt(node, key, out var value) && value >= minimum && value <= maximum)
				return true;

			detail = $"{key} must be an integer from {minimum} to {maximum}.";
			return false;
		}

		static bool ValidateOptionalInt(JsonObject node, string key, int minimum, int maximum, out string detail)
		{
			detail = null;
			if (!node.TryGetPropertyValue(key, out _))
				return true;

			if (TryReadInt(node, key, out var number) && number >= minimum && number <= maximum)
				return true;

			detail = $"{key} must be an integer from {minimum} to {maximum}.";
			return false;
		}

		static bool ValidateRequiredBool(JsonObject node, string key, out string detail)
		{
			detail = null;
			if (TryReadBool(node, key, out _))
				return true;

			detail = $"{key} must be a boolean.";
			return false;
		}

		static bool ValidateOptionalCell(JsonObject node, string key, out string detail)
		{
			detail = null;
			if (!node.TryGetPropertyValue(key, out _))
				return true;

			if (TryReadString(node, key, out var cell) && cell.Length <= FuzzSession.MaxIdLength && TryParseCell(cell, out _))
				return true;

			detail = $"{key} must be a coordinate in x,y form.";
			return false;
		}

		static bool ValidateOptionalPlaceNear(JsonObject action, out string detail)
		{
			detail = null;
			if (!action.TryGetPropertyValue("place_near", out var value))
				return true;

			if (value is not JsonObject placeNear || !HasOnlyProperties(placeNear, "anchor", "radius"))
			{
				detail = "place_near must be an object limited to anchor and radius.";
				return false;
			}

			if (!ValidateOptionalCell(placeNear, "anchor", out detail) || !placeNear.ContainsKey("anchor"))
			{
				detail ??= "place_near.anchor must be a coordinate in x,y form.";
				return false;
			}

			return ValidateOptionalInt(placeNear, "radius", 0, FuzzSession.MaxPlacementRadius, out detail);
		}

		static bool HasOnlyProperties(JsonObject node, params string[] allowed)
		{
			foreach (var property in node)
				if (!allowed.Any(a => string.Equals(a, property.Key, StringComparison.Ordinal)))
					return false;

			return true;
		}

		static bool IsSafeId(string value)
		{
			return !string.IsNullOrEmpty(value) &&
				value.Length <= FuzzSession.MaxIdLength &&
				SafeIdRegex.IsMatch(value) &&
				!value.Contains("..", StringComparison.Ordinal);
		}

		static bool TryReadString(JsonObject obj, string key, out string value)
		{
			value = null;
			if (obj == null || !obj.TryGetPropertyValue(key, out var node) || node == null)
				return false;

			try
			{
				value = node.GetValue<string>();
				return value != null;
			}
			catch (Exception)
			{
				return false;
			}
		}

		static bool TryReadInt(JsonObject obj, string key, out int value)
		{
			value = 0;
			if (obj == null || !obj.TryGetPropertyValue(key, out var node) || node == null)
				return false;

			try
			{
				value = node.GetValue<int>();
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		static bool TryReadLong(JsonObject obj, string key, out long value)
		{
			value = 0;
			if (obj == null || !obj.TryGetPropertyValue(key, out var node) || node == null)
				return false;

			try
			{
				value = node.GetValue<long>();
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		static bool TryReadBool(JsonObject obj, string key, out bool value)
		{
			value = false;
			if (obj == null || !obj.TryGetPropertyValue(key, out var node) || node == null)
				return false;

			try
			{
				value = node.GetValue<bool>();
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		static void WriteJsonAtomically(string path, JsonObject data)
		{
			var directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				File.WriteAllText(temporaryPath, data.ToJsonString(JsonOptions));
				File.Move(temporaryPath, path, true);
			}
			finally
			{
				if (File.Exists(temporaryPath))
					File.Delete(temporaryPath);
			}
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

		sealed class FuzzSession
		{
			public const string ProtocolVersion = "c3-session-v1";
			public const int MaxRequestBytes = 64 * 1024;
			public const int MaxPlayers = 32;
			public const int MaxActions = 8;
			public const int MaxCash = 1_000_000;
			public const int MaxTicks = 900;
			public const int MaxPlacementRadius = 64;
			public const int MaxIdLength = 64;

			public readonly string RequestPath;
			public readonly string ReportPath;
			public readonly string ReadyPath;
			public readonly string Map;
			public readonly string ScreenshotDirectory;
			public long LastCompletedSequence;
			public string LastRejectedRequestFingerprint;

			public FuzzSession(string requestPath, string reportPath, string readyPath, string map, string screenshotDirectory)
			{
				RequestPath = requestPath;
				ReportPath = reportPath;
				ReadyPath = readyPath;
				Map = map;
				ScreenshotDirectory = screenshotDirectory;
			}
		}

		sealed record FuzzRequest(long Sequence, JsonObject FuzzState);

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
