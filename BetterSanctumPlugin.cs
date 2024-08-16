using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;

namespace BetterSanctum;

public class BetterSanctumPlugin : BaseSettingsPlugin<BetterSanctumSettings>
{
    private readonly Stopwatch _sinceLastReloadStopwatch = Stopwatch.StartNew();

    private bool dataCalculated;
    private List<(int, int)> bestPath;

    public override void Render()
    {
        // METEORS
        DebugWindow.LogError($"START RENDER");
        var entityList = GameController?.EntityListWrapper?.ValidEntitiesByType[EntityType.Effect].Where(x => x.Metadata.Contains("/Effects/Effect")) ?? Enumerable.Empty<Entity>();

        foreach (var entity in entityList)
        {
            DebugWindow.LogError($"ENTITY FOUND");
            entity.TryGetComponent<Animated>(out var animatedComp);
            if (animatedComp == null) continue;

            DebugWindow.LogError($"animatedComp FOUND");
            System.Numerics.Vector2 entityPos = RemoteMemoryObject.pTheGame.IngameState.Camera.WorldToScreen(entity.PosNum);

            if (animatedComp.BaseAnimatedObjectEntity.Metadata.Contains("League_Sanctum/hazards/hazard_meteor"))
            {
                Graphics.DrawTextWithBackground("Meteor", entityPos, Color.Red, FontAlign.Center, Color.Black);
                Graphics.DrawCircleInWorld(entity.PosNum, 120.0f, Color.Red, 5.0f);
            }
        }

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
        var pathFinder = new PathFinder(this);

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
}
