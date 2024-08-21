using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.Elements.Sanctum;
using ExileCore.PoEMemory.FilesInMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Models;
using ExileCore.Shared.Enums;
using GameOffsets.Native;
using GameOffsets;
using ImGuiNET;
using SharpDX;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using ExileCore.PoEMemory.FilesInMemory.Sanctum;

namespace BetterSanctum;

public class BetterSanctumPlugin : BaseSettingsPlugin<BetterSanctumSettings>
{
    private readonly Stopwatch _sinceLastReloadStopwatch = Stopwatch.StartNew();

    private bool dataCalculated;
    private List<(int, int)> bestPath;
    private PathFinder pathFinder;

    private bool IsEntityWithinScreen(Vector2 entityPos, RectangleF screensize, float allowancePX)
    {
        // Check if the entity position is within the screen bounds with allowance
        float leftBound = screensize.Left - allowancePX;
        float rightBound = screensize.Right + allowancePX;
        float topBound = screensize.Top - allowancePX;
        float bottomBound = screensize.Bottom + allowancePX;

        return entityPos.X >= leftBound && entityPos.X <= rightBound &&
               entityPos.Y >= topBound && entityPos.Y <= bottomBound;
    }

    public override void AreaChange(AreaInstance area)
    {
        // Might help with refreshing files so data exists.
        var files = new FilesContainer(GameController.Game.M);
        var isDiff = false;
        if (files.SanctumRooms.EntriesList.Count != GameController.Files.SanctumRooms.EntriesList.Count)
        {
            isDiff = true;
        }
        else
        {
            if (files.SanctumRooms.EntriesList.Where((t, i) => t.Address != GameController.Files.SanctumRooms.EntriesList[i].Address).Any())
            {
                isDiff = true;
            }
        }

        if (isDiff)
        {
            GameController.Game.ReloadFiles();
        }
    }

    public override void Render()
    {
        var effectEntityList = GameController?.EntityListWrapper?.ValidEntitiesByType[EntityType.Effect]
            .Where(x => x.Metadata.Contains("/Effects/Effect") &&
                        x.TryGetComponent<Animated>(out var animComp) &&
                        animComp?.BaseAnimatedObjectEntity.Metadata != null) ?? [];

        var terrainEntityList = GameController?.EntityListWrapper?.ValidEntitiesByType[EntityType.Terrain] ?? [];

        foreach (var entity in effectEntityList)
        {
            var animComp = entity.GetComponent<Animated>();
            var metadata = animComp.BaseAnimatedObjectEntity.Metadata;
            var pos = RemoteMemoryObject.pTheGame.IngameState.Camera.WorldToScreen(entity.PosNum);

            if (metadata.Contains("League_Sanctum/hazards/hazard_meteor"))
            {
                DrawHazard("Meteor", pos, entity.PosNum, 140.0f, 30);
            }
            else if (metadata.Contains("League_Sanctum/hazards/totem_holy_beam_impact"))
            {
                DrawHazard("ZAP!", pos, entity.PosNum, 40.0f, 30);
            }
            else if (metadata.Contains("League_Necropolis/LyciaBoss/ao/lightning_strike_scourge"))
            {
                if (entity.TryGetComponent<AnimationController>(out var animController) &&
                    animController.AnimationProgress is > 0.0f and < 0.3f)
                {
                    DrawHazard("Dodge", pos, entity.PosNum, 100.0f, 60);
                }
            }
        }

        foreach (var entity in terrainEntityList)
        {
            var pos = RemoteMemoryObject.pTheGame.IngameState.Camera.WorldToScreen(entity.PosNum);

            if (entity.Metadata.Contains("/Sanctum/Objects/Spawners/SanctumSpawner") || entity.Metadata.Contains("/Sanctum/Objects/SanctumSpawner"))
            {
                entity.TryGetComponent<StateMachine>(out var stateComponent);

                var isActive = false;
                if (stateComponent != null)
                {
                    var activeState = stateComponent.States.FirstOrDefault(x => x.Name == "active");
                    isActive = activeState is { Value: 1 };
                }

                switch (isActive)
                {
                    case true:
                        DrawHazard("Spawner", pos, entity.PosNum, 60.0f, 4, Color.Lime);
                        break;
                    case false:
                        DrawHazard(" + ", pos, entity.PosNum, 20.0f, 4, Color.LightBlue);
                        break;
                }
            }
        }

        void DrawHazard(string text, Vector2 screenPos, Vector3 worldPos, float radius, int segments, Color color = default)
        {
            if (color == default)
            {
                color = Color.Red;
            }

            var textSize = ImGui.CalcTextSize(text);
            var textPosition = screenPos with { Y = screenPos.Y - textSize.Y / 2 };

            Graphics.DrawTextWithBackground(text, textPosition, color, FontAlign.Center, Color.Black with { A = 200 });
            Graphics.DrawFilledCircleInWorld(worldPos, radius, color with { A = 150 }, segments);
        }

        DrawRewards();

        var floorWindow = GameController.IngameState.IngameUi.SanctumFloorWindow;
        if (!floorWindow.IsVisible)
        {
            dataCalculated = false;
            return;
        }

        if (!GameController.Files.SanctumRooms.EntriesList.Any() && _sinceLastReloadStopwatch.Elapsed > TimeSpan.FromSeconds(5))
        {
            GameController.Files.LoadFiles();
            _sinceLastReloadStopwatch.Restart();
        }

        var roomsByLayer = floorWindow.RoomsByLayer;
        pathFinder = new PathFinder(this);

        pathFinder.CreateRoomWeightMap();

        if (!this.dataCalculated)
        {
            dataCalculated = true;
            pathFinder.CreateRoomWeightMap();
            this.bestPath = pathFinder.FindBestPath();
        }
        else
        {
            foreach (var room in this.bestPath)
            {
                if (room.Item1 == pathFinder.playerLayerIndex && room.Item2 == pathFinder.playerRoomIndex) continue;

                var sanctumRoom = roomsByLayer[room.Item1][room.Item2];
                Graphics.DrawFrame(sanctumRoom.GetClientRectCache, Settings.BestPathColor, Settings.FrameThickness);
            }
        }
    }

    private class Reward
    {
        public string Name;
        public int Count;
        public double Value;
    }

    private void DrawRewards()
    {
        var gameState = GameController.IngameState;
        bool usingDivinity = gameState.Data.MapStats.Any(x => x.Key.ToString() == "MapLycia2DuplicateUpToXDeferredRewards");

        var sanctumRewardWindow = gameState.IngameUi.SanctumRewardWindow;
        if (!sanctumRewardWindow.IsVisible || sanctumRewardWindow.RewardElements.Count == 0 || sanctumRewardWindow.RewardElements[0].ChildCount >= 3)
            return;

        var floorWindow = gameState.IngameUi.SanctumFloorWindow;
        if (floorWindow == null)
            return;

        string bossId = floorWindow?.Rooms?.Last()?.Data?.FightRoom?.Id;
        if (bossId == null)
            return;

        if (pathFinder == null)
            pathFinder = new PathFinder(this);

        var floor = pathFinder.CalculateFloorNumber(bossId);
        var rewardValues = GetRewardValues(sanctumRewardWindow.RewardElements);

        DrawDivinityText(usingDivinity, sanctumRewardWindow.PositionNum, floor);

        if (usingDivinity)
        {
            if (floor <= 2)
            {
                DrawRewardsForFloors1And2(rewardValues, sanctumRewardWindow, floor);
            }
            else if (floor == 3)
            {
                DrawRewardsForFloor3(rewardValues, sanctumRewardWindow, floorWindow);
            }
            else if (floor == 4)
            {
                DrawRewardsForFloor4(rewardValues, sanctumRewardWindow, floorWindow);
            }
        }
        else
        {
            DrawRewardsForNoDivinity(rewardValues, sanctumRewardWindow, floor);
        }
    }

    private Dictionary<Element, Reward> GetRewardValues(IEnumerable<Element> rewardElements)
    {
        var rewardValues = new Dictionary<Element, Reward>();

        foreach (var reward in rewardElements)
        {
            var match = Regex.Match(reward.Children[1].Text, @"(Receive)\s((?'rewardcount'(\d+))x(?'rewardname'.*))\s(right now|at the end of the next Floor|at the end of the Floor|on completing the Sanctum)$");
            if (!match.Success)
                continue;

            var rewardName = match.Groups["rewardname"].Value.Trim();
            if (!int.TryParse(match.Groups["rewardcount"].ValueSpan.Trim(), out var stackSize))
                continue;

            var data = new BaseItemType
            {
                BaseName = rewardName.Replace("Orbs", "Orb").TrimEnd('s'),
                ClassName = "StackableCurrency",
                Metadata = ""
            };

            var fn = GameController.PluginBridge.GetMethod<Func<BaseItemType, double>>("NinjaPrice.GetBaseItemTypeValue");
            if (fn == null)
                continue;

            var value = fn(data) * stackSize;
            rewardValues.Add(reward, new Reward { Name = rewardName, Count = stackSize, Value = value });
        }

        return rewardValues;
    }

    private bool IsInPactRoom(SanctumFloorWindow floorWindow, PathFinder pathFinder)
    {
        var currentRoom = floorWindow.RoomsByLayer[pathFinder.playerLayerIndex][pathFinder.playerRoomIndex];
        return currentRoom?.Data?.RewardRoom?.Id.Contains("_Deal") ?? false;
    }


    private void DrawDivinityText(bool usingDivinity, Vector2 position, int floor)
    {
        string text = usingDivinity ? "Divinity Detected" : "No Divinity Detected";
        Graphics.DrawText(text, position - new Vector2(100, 40), Color.White, 45, FontAlign.Center);
    }

    private void DrawRewardsForFloors1And2(Dictionary<Element, Reward> rewardValues, SanctumRewardWindow sanctumRewardWindow, int floor)
    {
        var bestReward = GetBestReward(rewardValues, sanctumRewardWindow.RewardElements.Last().Address);
        DrawRewardElements(rewardValues, bestReward, sanctumRewardWindow, floor);
    }

    private void DrawRewardsForFloor3(Dictionary<Element, Reward> rewardValues, SanctumRewardWindow sanctumRewardWindow, SanctumFloorWindow floorWindow)
    {
        var bestReward = DetermineBestRewardForFloor3(rewardValues, sanctumRewardWindow, floorWindow);
        DrawRewardElements(rewardValues, bestReward, sanctumRewardWindow, 3);
    }

    private void DrawRewardsForFloor4(Dictionary<Element, Reward> rewardValues, SanctumRewardWindow sanctumRewardWindow, SanctumFloorWindow floorWindow)
    {
        var (divCounter, pactCounter, path, rewardsWeCanTake) = CalculateFloor4Metrics(floorWindow);

        DrawFloor4InfoText(sanctumRewardWindow.PositionNum, pactCounter, rewardsWeCanTake, divCounter);

        if (divCounter <= 1 && rewardsWeCanTake >= 1 && !IsInPactRoom(floorWindow, pathFinder))
        {
            DrawSelectedRewardsForFloor4(rewardValues, sanctumRewardWindow, floorWindow, path, rewardsWeCanTake);
        }
        else
        {
            if (rewardsWeCanTake >= 1 && pactCounter == 0)
            {
                DrawRewardElements(rewardValues, rewardValues.OrderByDescending(x => x.Value.Value).FirstOrDefault(), sanctumRewardWindow, 4);
            }
            else
            {
                DrawRewardElements(rewardValues, rewardValues.FirstOrDefault(), sanctumRewardWindow, 4);
            }
        }
    }

    private void DrawRewardsForNoDivinity(Dictionary<Element, Reward> rewardValues, SanctumRewardWindow sanctumRewardWindow, int floor)
    {
        var bestReward = GetBestReward(rewardValues, sanctumRewardWindow.RewardElements.Last().Address);
        DrawRewardElements(rewardValues, bestReward, sanctumRewardWindow, floor);
    }

    private KeyValuePair<Element, Reward> GetBestReward(Dictionary<Element, Reward> rewardValues, long lastRewardAddress)
    {
        return rewardValues.Where(x => x.Key.Address != lastRewardAddress)
                            .OrderByDescending(x => x.Value.Value).FirstOrDefault();
    }

    private void DrawRewardElements(Dictionary<Element, Reward> rewardValues, KeyValuePair<Element, Reward> bestReward, SanctumRewardWindow sanctumRewardWindow, int floor)
    {
        foreach (var reward in rewardValues)
        {
            var rewardPos = reward.Key.Children[1].GetClientRectCache;

            if (reward.Key.Address == bestReward.Key.Address)
            {
                DrawBestReward(reward, rewardPos, floor);
            }
            else if (reward.Key.Address != sanctumRewardWindow.RewardElements.Last().Address || floor == 4)
            {
                DrawNonBestReward(reward, rewardPos);
            }
            else
            {
                DrawLastReward(reward, rewardPos, floor);
            }
        }
    }

    private void DrawBestReward(KeyValuePair<Element, Reward> reward, RectangleF rewardPos, int floor)
    {
        Color boxColor = reward.Value.Name.Contains("Divine Orb") ? Color.Magenta : Color.DarkGreen;
        string text = reward.Value.Name.Contains("Divine Orb") ? "TAKE THE DIVINES..." : "TAKE THIS";

        Graphics.DrawBox(rewardPos, boxColor);
        Graphics.DrawText(text, rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
        Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name} ({reward.Value.Value})", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);
    }

    private void DrawNonBestReward(KeyValuePair<Element, Reward> reward, RectangleF rewardPos)
    {
        Graphics.DrawBox(rewardPos, Color.DarkRed);
        Graphics.DrawText("DONT TAKE", rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
        Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);
    }

    private void DrawLastReward(KeyValuePair<Element, Reward> reward, RectangleF rewardPos, int floor)
    {
        Graphics.DrawBox(rewardPos, Color.DarkRed);
        Graphics.DrawText($"THIS IS FLOOR {floor}...", rewardPos.Center - new SharpDX.Vector2(0, 10), Color.White, 45, FontAlign.Center);
        Graphics.DrawText("DONT TAKE LAST REWARD", rewardPos.Center + new SharpDX.Vector2(0, 5), Color.White, 45, FontAlign.Center);
        Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 20), Color.White, 45, FontAlign.Center);
    }

    private KeyValuePair<Element, Reward> DetermineBestRewardForFloor3(Dictionary<Element, Reward> rewardValues, SanctumRewardWindow sanctumRewardWindow, SanctumFloorWindow floorWindow)
    {
        if (floorWindow.FloorData.RoomChoices.Count == 8)
        {
            if (rewardValues.All(x => !x.Value.Name.Contains("Divine Orb")))
            {
                return rewardValues.Where(x => x.Key.Address == sanctumRewardWindow.RewardElements.First().Address)
                    .OrderByDescending(x => x.Value.Value).FirstOrDefault();
            }
            else
            {
                return rewardValues.OrderByDescending(x => x.Value.Value).FirstOrDefault();
            }
        }
        
        return rewardValues.Where(x => x.Key.Address != sanctumRewardWindow.RewardElements.Last().Address
        || (x.Key.Address == sanctumRewardWindow.RewardElements.Last().Address && x.Value.Name.Contains("Divine Orb")))
            .OrderByDescending(x => x.Value.Value).FirstOrDefault();
    }

    private (int divCounter, int pactCounter, List<(int, int)> path, int rewardsWeCanTake) CalculateFloor4Metrics(SanctumFloorWindow floorWindow)
    {
        int divCounter = 0;
        int pactCounter = 0;

        List<(int, int)> path = new List<(int, int)>();
        if (!dataCalculated)
        {
            pathFinder = new PathFinder(this);
            pathFinder.CreateRoomWeightMap(false);

            path = pathFinder.FindBestPath();
        }
        else
        {
            path = this.bestPath;
        }

        foreach (var room in path)
        {
            if (IsCurrentPlayerRoom(room, pathFinder)) continue;

            var sanctumRoom = floorWindow.RoomsByLayer[room.Item1][room.Item2];

            if (sanctumRoom?.Data?.RewardRoom?.Id.Contains("_Deal") ?? false)
            {
                pactCounter++;
            }

            if (new[] { sanctumRoom?.Data?.Reward1, sanctumRoom?.Data?.Reward2, sanctumRoom?.Data?.Reward3 }.Any(x => x != null && x.CurrencyName.Contains("Divine Orb")))
            {
                divCounter++;
            }
        }

        var floorData = RemoteMemoryObject.GetObjectStatic<SanctumFloorData>(floorWindow.FloorData.Address - 0x88);
        var rewards = floorData.M.ReadStdVectorStride<long>(floorData.M.Read<StdVector>(floorData.Address + 0x70), 0x10)
            .Select(TheGame.pTheGame.Files.SanctumDeferredRewards.GetByAddressOrReload)
            .Where(x => x != null).ToList();


        int currentRewardCount = rewards.Count(x => x.DeferralCategory.Id.Contains("FinalBoss"));

        int rewardsWeCanTake = 2 - currentRewardCount - pactCounter - divCounter;

        return (divCounter, pactCounter, path, rewardsWeCanTake);
    }

    private bool IsCurrentPlayerRoom((int, int) room, PathFinder pathFinder)
    {
        return (room.Item1 == pathFinder.playerLayerIndex && room.Item2 == pathFinder.playerRoomIndex);
    }

    private void DrawFloor4InfoText(Vector2 position, int pactCounter, int rewardsWeCanTake, int divCounter)
    {
        Graphics.DrawText($"Divines Remaining: {divCounter}", position - new Vector2(160, 0), Color.White, 25, FontAlign.Left);
        Graphics.DrawText($"Pacts Remaining: {pactCounter}", position - new Vector2(160, -20), Color.White, 25, FontAlign.Left);
        Graphics.DrawText($"Rewards We Can Take: {rewardsWeCanTake}", position - new Vector2(160, -40), Color.White, 25, FontAlign.Left);
    }

    private void DrawSelectedRewardsForFloor4(Dictionary<Element, Reward> rewardValues, SanctumRewardWindow sanctumRewardWindow, SanctumFloorWindow floorWindow, List<(int, int)> path, int rewardsWeCanTake)
    {
        var selectedRewards = rewardValues.OrderByDescending(x => x.Value.Value)
                                          .Take(rewardsWeCanTake);
        if(selectedRewards.Count() == 0)
        {
            DrawRewardElements(rewardValues, rewardValues.FirstOrDefault(), sanctumRewardWindow, 4);
            return;
        }

        DrawRewardElements(rewardValues, selectedRewards.FirstOrDefault(), sanctumRewardWindow, 4);
    }

    private void DrawRewardElementsForFloor4(Dictionary<Element, Reward> rewardValues, SanctumRewardWindow sanctumRewardWindow)
    {
        foreach (var reward in rewardValues)
        {
            var rewardPos = reward.Key.Children[1].GetClientRectCache;
            Graphics.DrawBox(rewardPos, Color.DarkRed);
            Graphics.DrawText("DONT TAKE", rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
            Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);
        }
    }

}

