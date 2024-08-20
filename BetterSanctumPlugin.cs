using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using ImGuiNET;
using SharpDX;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

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
    };
    public class CurrencyData
    {
        public bool IsShard;
        public int StackSize = 1;
        public int MaxStackSize = 0;
    }

    public class CustomItem
    {
        public string BaseName;
        public int ItemType;
        public Element Element;

        public CurrencyData CurrencyInfo { get; set; } = new CurrencyData();
    }

    private void DrawRewards()
    {
        bool usingDivinity = GameController.IngameState.Data.MapStats.Any(x => x.Key.ToString() == "MapLycia2DuplicateUpToXDeferredRewards");

        var sanctumRewardwindow = GameController.IngameState.IngameUi.SanctumRewardWindow;
        if (!sanctumRewardwindow.IsVisible)
            return;

        // they use the fucking "reward" window for affliction pact
        if (sanctumRewardwindow.RewardElements.Count == 0 || sanctumRewardwindow.RewardElements[0].ChildCount >= 3)
            return;

        SanctumFloorWindow floorWindow = GameController.IngameState.IngameUi.SanctumFloorWindow;
        if (floorWindow == null)
            return;

        string bossId = floorWindow?.Rooms?.Last()?.Data?.FightRoom?.Id;
        if (bossId == null)
            return;

        if (pathFinder == null)
            pathFinder = new PathFinder(this);

        var floor = pathFinder.CalculateFloorNumber(bossId);

        Dictionary<Element, Reward> rewardValues = new Dictionary<Element, Reward>();

        foreach (var reward in sanctumRewardwindow.RewardElements)
        {
            var match = Regex.Match(reward.Children[1].Text, @"(Receive)\s((?'rewardcount'(\d+))x(?'rewardname'.*))\s(right now|at the end of the next Floor|at the end of the Floor|on completing the Sanctum)$");
            if (!match.Success)
                continue;

            var rewardName = match.Groups["rewardname"].Value.Trim();
            var rewardCount = match.Groups["rewardcount"].ValueSpan.Trim();

            if (!int.TryParse(rewardCount, out var stackSize))
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

        if (usingDivinity)
        {
            Graphics.DrawText("Divinity Detected", sanctumRewardwindow.PositionNum - new Vector2(100, 40), Color.White, 45, FontAlign.Center);

            if (floor <= 2)
            {
                var bestReward = rewardValues.Where(x => x.Key.Address != sanctumRewardwindow.RewardElements.Last().Address)
                    .OrderByDescending(x => x.Value.Value).FirstOrDefault();

                var otherRewards = rewardValues.Where(x => x.Key.Address != bestReward.Key.Address).Select(x => x.Value)
                .OrderByDescending(x => x.Value).ToList();

                foreach (var reward in rewardValues)
                {
                    var rewardPos = reward.Key.Children[1].GetClientRectCache;

                    if (reward.Key.Address == bestReward.Key.Address)
                    {
                        Graphics.DrawBox(rewardPos, Color.DarkGreen);
                        Graphics.DrawText($"TAKE THIS", rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
                        Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name} ({reward.Value.Value})", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);

                    }
                    else if (reward.Key.Address != rewardValues.Last().Key.Address)
                    {
                        Graphics.DrawBox(rewardPos, Color.DarkRed);
                        Graphics.DrawText($"DONT TAKE", rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
                        Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);
                    }
                    else
                    {
                        Graphics.DrawBox(rewardPos, Color.DarkRed);
                        Graphics.DrawText($"THIS IS FLOOR {floor}...", rewardPos.Center - new SharpDX.Vector2(0, 10), Color.White, 45, FontAlign.Center);
                        Graphics.DrawText($"DONT TAKE LAST REWARD", rewardPos.Center + new SharpDX.Vector2(0, 5), Color.White, 45, FontAlign.Center);
                        Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 20), Color.White, 45, FontAlign.Center);
                    }
                }
            }
            else if (floor == 3)
            {
                KeyValuePair<Element, Reward> bestReward = new KeyValuePair<Element, Reward>();

                if (floorWindow.FloorData.RoomChoices.Count == 8)
                {
                    if (rewardValues.All(x => !x.Value.Name.Contains("Divine")))
                    {
                        bestReward = rewardValues.Where(x => x.Key.Address == sanctumRewardwindow.RewardElements.First().Address)
                            .OrderByDescending(x => x.Value.Value).FirstOrDefault();
                    }
                    else
                    {
                        bestReward = rewardValues.OrderByDescending(x => x.Value.Value).FirstOrDefault();
                    }
                }
                else
                {
                    bestReward = rewardValues.Where(x => x.Key.Address != sanctumRewardwindow.RewardElements.Last().Address
                    || (x.Key.Address != sanctumRewardwindow.RewardElements.Last().Address && x.Value.Name.Contains("Divine")))
                        .OrderByDescending(x => x.Value.Value).FirstOrDefault();
                }

                var otherRewards = rewardValues.Where(x => x.Key.Address != bestReward.Key.Address).Select(x => x.Value)
                .OrderByDescending(x => x.Value).ToList();

                foreach (var reward in rewardValues)
                {
                    var rewardPos = reward.Key.Children[1].GetClientRectCache;

                    if (reward.Key.Address == bestReward.Key.Address)
                    {
                        if (!reward.Value.Name.Contains("Divine"))
                        {
                            Graphics.DrawBox(rewardPos, Color.DarkGreen);
                            Graphics.DrawText($"TAKE THIS", rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
                            Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);
                        }
                        else
                        {
                            Graphics.DrawBox(rewardPos, Color.Magenta);
                            Graphics.DrawText($"TAKE THE DIVINES...", rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
                            Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);
                        }

                    }
                    else if (reward.Key.Address != rewardValues.Last().Key.Address)
                    {
                        Graphics.DrawBox(rewardPos, Color.DarkRed);
                        Graphics.DrawText($"DONT TAKE", rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
                        Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);
                    }
                    else
                    {
                        Graphics.DrawBox(rewardPos, Color.DarkRed);
                        Graphics.DrawText($"THIS IS FLOOR {floor}...", rewardPos.Center - new SharpDX.Vector2(0, 10), Color.White, 45, FontAlign.Center);
                        Graphics.DrawText($"DONT TAKE LAST REWARD", rewardPos.Center + new SharpDX.Vector2(0, 5), Color.White, 45, FontAlign.Center);
                        Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 20), Color.White, 45, FontAlign.Center);
                    }
                }
            }
            else if (floor == 4)
            {
                bool containsDivines = false;
                int pactCounter = 0;
                var roomsByLayer = floorWindow.RoomsByLayer;

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
                    if (room.Item1 == pathFinder.playerLayerIndex && room.Item2 == pathFinder.playerRoomIndex) continue;

                    var sanctumRoom = roomsByLayer[room.Item1][room.Item2];

                    if (new[] { sanctumRoom?.Data?.Reward1, sanctumRoom?.Data?.Reward2, sanctumRoom?.Data?.Reward3 }.Any(x => x != null && x.CurrencyName.Contains("Divine")))
                    {
                        containsDivines = true;
                    }

                    if (sanctumRoom.Data?.RewardRoom?.Id?.Contains("_Deal") ?? false)
                    {
                        pactCounter++;
                    }
                }

                if (!containsDivines)
                {
                    //seems to always be index Children[15]
                    var elements = GameController.IngameState.IngameUi.Children.Where(x => x.ChildCount == 5
                    && x.Type == (ElementType)7
                    && x.PositionNum != new Vector2(0, 0)
                    && x.PathFromRoot == x.IndexInParent.ToString()).ToList();

                    if (elements.Count != 1)
                        return;

                    var sanctumUi = elements[0];
                    var currentRewards1 = sanctumUi?.Children[3]?.Children[4]?.Tooltip?.GetChildFromIndices(0, 0, 1);
                    var currentRewards2 = sanctumUi?.Children[3]?.Children[5]?.Tooltip?.GetChildFromIndices(0, 0, 1);
                    int currentRewardCount = (int)(currentRewards1?.ChildCount ?? 0) + (int)(currentRewards2?.ChildCount ?? 0);

                    bool inPactRoom = roomsByLayer[pathFinder.playerLayerIndex][pathFinder.playerRoomIndex].Data.RewardRoom.Id.Contains("_Deal");
                    int rewardsWeCanTake = 2 - currentRewardCount - pactCounter;

                    Graphics.DrawText($"currentRewardCount: {currentRewardCount}\npactCounter: {pactCounter}\nrewardsWeCanTake: {rewardsWeCanTake}\ninPactRoom: {inPactRoom.ToString()}",
                                sanctumRewardwindow.PositionNum, Color.White, 45, FontAlign.Center);

                    if (rewardsWeCanTake >= 1 && !inPactRoom)
                    {
                        var rewards = new List<(List<(int, int)>, int)>();

                        foreach (var step in path)
                        {
                            var reward1 = roomsByLayer[step.Item1][step.Item2].Data.Reward1;
                            var reward2 = roomsByLayer[step.Item1][step.Item2].Data.Reward2;
                            var reward3 = roomsByLayer[step.Item1][step.Item2].Data.Reward3;

                            if (reward1 != null)
                            {
                                int rewardWeight1 = Settings.GetCurrencyWeight(reward1 + "_Now");
                                int rewardWeight2 = Settings.GetCurrencyWeight(reward2 + "_EndOfFloor");
                                int rewardWeight3 = Settings.GetCurrencyWeight(reward3 + "_EndOfSanctum");
                                int maxRewardWeight = Math.Max(Math.Max(rewardWeight1, rewardWeight2), rewardWeight3);

                                rewards.Add((new List<(int, int)> { step }, maxRewardWeight));
                            }
                        }

                        rewards = rewards.OrderByDescending(x => x.Item2).Take(rewardsWeCanTake).ToList();

                        foreach (var reward in rewards)
                        {
                            SanctumRoomElement room = roomsByLayer[reward.Item1[0].Item1][reward.Item1[0].Item2];


                            var playerLayerIndex = pathFinder.playerLayerIndex;
                            var playerRoomIndex = pathFinder.playerRoomIndex;

                            if (playerLayerIndex == reward.Item1[0].Item1 && playerRoomIndex == reward.Item1[0].Item2)
                            {
                                var bestReward = rewardValues.OrderByDescending(x => x.Value.Value).FirstOrDefault();

                                foreach (var rewardValue in rewardValues)
                                {
                                    var rewardPos = rewardValue.Key.Children[1].GetClientRectCache;

                                    if (rewardValue.Key.Address == bestReward.Key.Address)
                                    {
                                        Graphics.DrawBox(rewardPos, Color.DarkGreen);
                                        Graphics.DrawText($"TAKE THIS", rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
                                        Graphics.DrawText($"{rewardValue.Value.Count}x {rewardValue.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);
                                    }
                                    else
                                    {
                                        Graphics.DrawBox(rewardPos, Color.DarkRed);
                                        Graphics.DrawText($"DONT TAKE", rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
                                        Graphics.DrawText($"{rewardValue.Value.Count}x {rewardValue.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        KeyValuePair<Element, Reward> bestReward = new KeyValuePair<Element, Reward>();

                        if (!inPactRoom)
                        {
                            bestReward = rewardValues.Where(x => x.Key.Address == sanctumRewardwindow.RewardElements.First().Address)
                                .OrderByDescending(x => x.Value.Value).FirstOrDefault();
                        }
                        else
                        {
                            bestReward = rewardValues.OrderByDescending(x => x.Value.Value).FirstOrDefault();
                        }

                        foreach (var reward in rewardValues)
                        {
                            var rewardPos = reward.Key.Children[1].GetClientRectCache;

                            if (reward.Key.Address == bestReward.Key.Address)
                            {
                                Graphics.DrawBox(rewardPos, Color.DarkGreen);
                                Graphics.DrawText($"TAKE THIS", rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
                                Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);
                            }
                            else
                            {
                                Graphics.DrawBox(rewardPos, Color.DarkRed);
                                Graphics.DrawText($"DONT TAKE", rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
                                Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);
                            }
                        }

                    }
                }
            }
        }
        else
        {
            Graphics.DrawText("No Divinity Detected", sanctumRewardwindow.PositionNum - new Vector2(100, 40), Color.White, 45, FontAlign.Center);

            var bestReward = rewardValues.OrderByDescending(x => x.Value.Value).FirstOrDefault();

            var otherRewards = rewardValues.Where(x => x.Key.Address != bestReward.Key.Address).Select(x => x.Value)
            .OrderByDescending(x => x.Value).ToList();

            foreach (var reward in rewardValues)
            {
                var rewardPos = reward.Key.Children[1].GetClientRectCache;

                if (reward.Key.Address == bestReward.Key.Address)
                {
                    Graphics.DrawBox(rewardPos, Color.DarkGreen);
                    Graphics.DrawText($"TAKE THIS", rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
                    Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name} ({reward.Value.Value})", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);

                }
                else if (reward.Key.Address != rewardValues.Last().Key.Address)
                {
                    Graphics.DrawBox(rewardPos, Color.DarkRed);
                    Graphics.DrawText($"DONT TAKE", rewardPos.Center - new SharpDX.Vector2(0, 7), Color.White, 45, FontAlign.Center);
                    Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 8), Color.White, 45, FontAlign.Center);
                }
                else
                {
                    Graphics.DrawBox(rewardPos, Color.DarkRed);
                    Graphics.DrawText($"THIS IS FLOOR {floor}...", rewardPos.Center - new SharpDX.Vector2(0, 10), Color.White, 45, FontAlign.Center);
                    Graphics.DrawText($"DONT TAKE LAST REWARD", rewardPos.Center + new SharpDX.Vector2(0, 5), Color.White, 45, FontAlign.Center);
                    Graphics.DrawText($"{reward.Value.Count}x {reward.Value.Name}", rewardPos.Center + new SharpDX.Vector2(0, 20), Color.White, 45, FontAlign.Center);
                }
            }
        }
    }
}

