using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace BetterSanctum;

public class BetterSanctumPlugin : BaseSettingsPlugin<BetterSanctumSettings>
{
    private readonly Stopwatch _sinceLastReloadStopwatch = Stopwatch.StartNew();

    private bool dataCalculated;
    private List<(int, int)> bestPath;

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

    public override void Render()
    {
        var entityList = GameController?.EntityListWrapper?.ValidEntitiesByType[EntityType.Effect].Where(x => x.Metadata.Contains("/Effects/Effect")) ?? Enumerable.Empty<Entity>();

        foreach (var entity in entityList)
        {
            if (!(entity.TryGetComponent<Animated>(out var animatedComp) && animatedComp != null && animatedComp.BaseAnimatedObjectEntity != null && animatedComp.BaseAnimatedObjectEntity.Metadata != null)) continue;

            var entityPos = RemoteMemoryObject.pTheGame.IngameState.Camera.WorldToScreen(entity.PosNum);

            if (animatedComp.BaseAnimatedObjectEntity.Metadata.Contains("League_Sanctum/hazards/hazard_meteor"))
            {
                Graphics.DrawTextWithBackground("Meteor", entityPos, Color.Red, FontAlign.Center, Color.Black);
                Graphics.DrawCircleInWorld(entity.PosNum, 140.0f, Color.Red, 5);
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
