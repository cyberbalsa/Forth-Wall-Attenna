using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ForthWall.Core;
using ForthWall.Protocol;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.GUI.TextPanel;
using VRage.Utils;
using VRageMath;
using Surface = Sandbox.ModAPI.Ingame.IMyTextSurface;
using Surfaces = Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;
using TerminalBlock = Sandbox.ModAPI.IMyTerminalBlock;

namespace ForthWall.Plugin
{
    // All methods run on the game thread. No network, hashing, or filesystem work.
    // Route counts are display-only snapshots; Dispatcher still authorizes each command.
    internal sealed class HubPresentation : IDisposable
    {
        private sealed class View
        {
            internal MyProgrammableBlock Block = null!;
            internal int Programs, Controllers;
            internal bool Sampled, Scanning, ActivityFailed;
            internal long Owner, NextScan, NextPaint;
            internal string Activity = "Waiting for a command", Text = "", RenderKey = "";
        }
        private readonly GameWorld world;
        private readonly Plugin plugin;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Dictionary<long, View> views = new Dictionary<long, View>();
        private readonly List<View> order = new List<View>();
        private readonly IMyTerminalControlButton refresh;
        private IReadOnlyList<long>? knownHubs, scanIds;
        private View? scanning;
        private int paintCursor, scanCursor, index, programs, controllers;
        private bool disposed;
        private static readonly HashSet<string> BasicControls = new HashSet<string>(StringComparer.Ordinal)
        { "OnOff", "ShowInTerminal", "ShowInToolbarConfig", "ShowOnHUD", "Name" };
        private static readonly HashSet<string> BasicActions = new HashSet<string>(StringComparer.Ordinal)
        { "OnOff", "OnOff_On", "OnOff_Off", "ShowOnHUD", "ShowOnHUD_On", "ShowOnHUD_Off" };
        private static readonly Color Background = new Color(8, 16, 24);
        private static readonly Color Panel = new Color(17, 31, 43);
        private static readonly Color Muted = new Color(140, 164, 181);
        private static readonly Color White = new Color(227, 239, 246);
        private static readonly Color Cyan = new Color(77, 208, 225);
        private static readonly Color Green = new Color(91, 220, 153);
        private static readonly Color Amber = new Color(255, 194, 86);
        private static readonly Color Red = new Color(255, 112, 112);

        internal HubPresentation(GameWorld world, Plugin plugin)
        {
            this.world = world; this.plugin = plugin;
            refresh = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyProgrammableBlock>("ForthWall.Refresh");
            refresh.Title = MyStringId.GetOrCompute("Refresh antenna status");
            refresh.Tooltip = MyStringId.GetOrCompute("Refresh the cached count of owned blocks reachable through radio, laser, and relay routes.");
            refresh.SupportsMultipleBlocks = false;
            refresh.Enabled = b => b.IsWorking && b.OwnerId == world.LocalOwner;
            refresh.Action = b => { if (views.TryGetValue(b.EntityId, out var v)) { v.NextScan = 0; v.NextPaint = 0; } };
            MyAPIGateway.TerminalControls.CustomControlGetter += Controls;
            MyAPIGateway.TerminalControls.CustomActionGetter += Actions;
            plugin.Dispatcher.Completed += Completed;
        }
        private static bool IsHub(TerminalBlock block) => block is MyProgrammableBlock pb && GameWorld.IsHub(pb);
        private void Controls(TerminalBlock block, List<IMyTerminalControl> controls)
        {
            if (disposed || !IsHub(block)) return;
            // Filter this instance's menu, never the global PB control definitions.
            controls.RemoveAll(c => !BasicControls.Contains(c.Id));
            controls.Add(refresh);
        }
        private void Actions(TerminalBlock block, List<IMyTerminalAction> actions)
        {
            if (!disposed && IsHub(block)) actions.RemoveAll(a => !BasicActions.Contains(a.Id));
        }
        internal void Update()
        {
            if (disposed) return;
            SyncHubs();
            if (order.Count == 0) return;
            long now = clock.ElapsedMilliseconds;
            AdvanceScan(now);
            // At most one hub can paint per frame, normally at most once per second.
            paintCursor %= order.Count;
            var view = order[paintCursor++];
            if (now >= view.NextPaint)
            {
                view.NextPaint = now + 1000;
                Paint(view);
            }
        }
        private void SyncHubs()
        {
            var ids = world.HubIds;
            if (ReferenceEquals(ids, knownHubs)) return;
            knownHubs = ids;
            var current = new HashSet<long>(ids);
            foreach (var pair in views.ToArray())
            {
                if (current.Contains(pair.Key) && world.HubBlock(pair.Key) == pair.Value.Block) continue;
                pair.Value.Block.AppendingCustomInfo -= CustomInfo;
                if (scanning == pair.Value) scanning = null;
                views.Remove(pair.Key);
            }
            foreach (long id in ids)
            {
                if (views.ContainsKey(id)) continue;
                var block = world.HubBlock(id);
                if (block == null) continue;
                var view = new View { Block = block, Owner = block.OwnerId };
                views.Add(id, view);
                block.AppendingCustomInfo += CustomInfo;
                plugin.Log("Hub status displays attached to " + id + "; " + ((Surfaces)block).SurfaceCount + " surfaces.");
            }
            order.Clear(); order.AddRange(views.Values);
        }
        private bool Eligible(View view)
        {
            var block = world.Find(view.Block.EntityId);
            return block != null && block.Info.Working && block.Accessible && block.Owner == world.LocalOwner && block.Owner != 0;
        }
        private void AdvanceScan(long now)
        {
            if (scanning == null)
            {
                scanCursor %= order.Count;
                var next = order[scanCursor++];
                if (now < next.NextScan || !Eligible(next)) return;
                scanning = next; next.Scanning = true;
                scanIds = world.BlockIds; index = programs = controllers = 0;
            }
            var view = scanning;
            if (!Eligible(view)) { Invalidate(view); scanning = null; return; }
            long start = Stopwatch.GetTimestamp();
            // Up to two candidates or 0.35 ms of scheduling per frame, across all hubs.
            for (int n = 0; index < scanIds!.Count && n < 2; n++)
            {
                var block = world.Find(scanIds[index++]);
                if (block != null && !block.Info.Hub && block.Accessible && block.Owner != 0 && block.Owner == world.LocalOwner &&
                    world.Reachable(view.Block.EntityId, block.Info.EntityId) && world.Reachable(block.Info.EntityId, view.Block.EntityId))
                {
                    if (block.Info.RemoteControl) controllers++; else programs++;
                }
                if ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency >= 0.35) break;
            }
            if (index < scanIds.Count) return;
            view.Programs = programs; view.Controllers = controllers; view.Sampled = true; view.Scanning = false;
            view.NextScan = now + 5000; view.NextPaint = 0;
            scanning = null; scanIds = null;
        }
        private static void Invalidate(View view)
        {
            view.Sampled = view.Scanning = false; view.Programs = view.Controllers = 0; view.NextScan = 0;
        }
        private void Paint(View view)
        {
            var block = world.Find(view.Block.EntityId);
            if (block == null) return;
            bool owned = block.Owner != 0 && block.Owner == world.LocalOwner && block.Accessible;
            if (view.Owner != block.Owner || !owned || !block.Info.Working)
            {
                Invalidate(view); view.Owner = block.Owner;
                if (scanning == view) { scanning = null; scanIds = null; }
                if (!owned) view.Activity = "Waiting for local ownership";
            }
            string state;
            Color accent;
            if (!view.Block.IsFunctional) { state = "INCOMPLETE"; accent = Red; }
            else if (!view.Block.Enabled) { state = "DISABLED"; accent = Muted; }
            else if (!block.Info.Working) { state = "NO POWER"; accent = Red; }
            else if (!owned) { state = "LOCKED"; accent = Amber; }
            else if (plugin.ListenerStatus != "Listening") { state = "GATEWAY OFFLINE"; accent = Red; }
            else if (!world.ScriptsEnabled) { state = "SCRIPTS DISABLED"; accent = Amber; }
            else { state = "READY"; accent = Green; }
            int clients = plugin.Dispatcher.ClientCount, queued = plugin.Dispatcher.PendingCount;
            string pbCount = view.Sampled ? view.Programs.ToString() : "--";
            string rcCount = view.Sampled ? view.Controllers.ToString() : "--";
            string sampling = view.Scanning ? "Refreshing routes" : view.Sampled ? "Radio / laser / relays" : "Waiting for route scan";
            var text = new StringBuilder();
            text.AppendLine("Forth Wall Attenna").AppendLine("Antenna gateway").AppendLine();
            text.Append("Hub: ").AppendLine(state);
            text.Append("Gateway: ").AppendLine(plugin.ListenerStatus);
            text.Append("Access: ").AppendLine(owned ? "Local owner" : "Local ownership required");
            text.Append("PB scripts: ").AppendLine(world.ScriptsEnabled ? "Enabled" : "Disabled in world settings");
            text.AppendLine().Append("Reachable PBs: ").AppendLine(pbCount);
            text.Append("Remote Controls: ").AppendLine(rcCount);
            text.Append("Routes: ").AppendLine(sampling);
            text.AppendLine("Counts are cached; commands recheck routes.");
            text.AppendLine().Append("Connected clients: ").Append(clients).AppendLine();
            text.Append("Gateway queue: ").Append(queued).AppendLine();
            text.Append("Local endpoint: 127.0.0.1:").Append(plugin.ListenerPort).AppendLine();
            text.Append("Hub ID: ").Append(view.Block.EntityId).AppendLine();
            text.AppendLine().Append("Last command: ").AppendLine(view.Activity);
            string updated = text.ToString();
            if (updated != view.Text)
            {
                view.Text = updated;
                view.Block.RefreshCustomInfo();
                view.Block.RaisePropertiesChanged();
            }
            string key = state + "|" + pbCount + "|" + rcCount + "|" + clients + "|" + queued + "|" + sampling + "|" + view.Activity;
            if (key == view.RenderKey) return;
            view.RenderKey = key;
            var surfaces = (Surfaces)view.Block;
            for (int i = 0; i < surfaces.SurfaceCount; i++)
            {
                var surface = surfaces.GetSurface(i);
                surface.ContentType = ContentType.SCRIPT;
                surface.Script = "";
                surface.ScriptBackgroundColor = Background;
                surface.ScriptForegroundColor = White;
                if (i == 0) DrawMain(surface, state, accent, pbCount, rcCount, clients, sampling, view.Activity, view.ActivityFailed);
                else DrawStrip(surface, state, accent, clients, queued);
            }
        }
        private void CustomInfo(MyTerminalBlock block, StringBuilder text)
        {
            if (!views.TryGetValue(block.EntityId, out var view)) return;
            block.DetailedInfo.Clear();
            text.Append(view.Text);
        }
        private void Completed(Request request, Response response)
        {
            if (!views.TryGetValue(request.HubId, out var view)) return;
            string? action = request.Deploy != null ? "Deploy program" : request.Run != null ? "Run program" :
                request.SetWaypoints != null ? "Upload waypoints" : request.SetAutopilot != null ? (request.SetAutopilot.Enabled ? "Start autopilot" : "Stop autopilot") : null;
            if (action == null) return;
            view.ActivityFailed = response.Error != null;
            view.Activity = action + (response.Error == null ? " / OK" : " / " + response.Error.Code);
            view.NextPaint = 0;
        }
        private static void DrawMain(Surface surface, string state, Color accent, string programs, string controls, int clients, string route, string activity, bool failed)
        {
            var size = surface.SurfaceSize;
            float scale = Math.Min(size.X / 512f, size.Y / 320f);
            var origin = (surface.TextureSize - new Vector2(512, 320) * scale) * .5f;
            using (var frame = surface.DrawFrame())
            {
                Rect(frame, origin, scale, 0, 0, 512, 320, Background);
                Rect(frame, origin, scale, 0, 0, 6, 320, accent);
                Text(frame, origin, scale, "FORTH WALL ATTENNA", 24, 18, .78f, Cyan);
                Text(frame, origin, scale, "ANTENNA GATEWAY", 25, 49, .48f, Muted);
                Rect(frame, origin, scale, 24, 80, 464, 53, Panel);
                Text(frame, origin, scale, state, 38, 88, .95f, accent);
                Metric(frame, origin, scale, 24, programs, "PROGRAMS");
                Metric(frame, origin, scale, 186, controls, "CONTROLLERS");
                Metric(frame, origin, scale, 348, clients.ToString(), "CLIENTS");
                Text(frame, origin, scale, route, 25, 230, .52f, Muted);
                Rect(frame, origin, scale, 24, 255, 464, 1, Panel);
                Text(frame, origin, scale, "LAST COMMAND", 25, 265, .43f, Muted);
                Text(frame, origin, scale, Clip(activity, 44), 25, 286, .53f, failed ? Amber : White);
            }
        }
        private static void DrawStrip(Surface surface, string state, Color accent, int clients, int queued)
        {
            float scale = Math.Min(surface.SurfaceSize.X / 512f, surface.SurfaceSize.Y / 150f);
            var origin = (surface.TextureSize - new Vector2(512, 150) * scale) * .5f;
            using (var frame = surface.DrawFrame())
            {
                Rect(frame, origin, scale, 0, 0, 512, 150, Background);
                Rect(frame, origin, scale, 10, 15, 6, 120, accent);
                Text(frame, origin, scale, state, 32, 17, .95f, accent);
                Text(frame, origin, scale, clients + " CLIENTS   /   " + queued + " QUEUED", 32, 68, .65f, White);
                Text(frame, origin, scale, "FORTH WALL ATTENNA", 32, 108, .47f, Muted);
            }
        }
        private static void Metric(MySpriteDrawFrame frame, Vector2 origin, float scale, float x, string value, string label)
        {
            Rect(frame, origin, scale, x, 147, 140, 72, Panel);
            Text(frame, origin, scale, value, x + 12, 151, 1.1f, White);
            Text(frame, origin, scale, label, x + 12, 193, .42f, Muted);
        }
        private static void Rect(MySpriteDrawFrame frame, Vector2 origin, float scale, float x, float y, float w, float h, Color color)
        {
            var sprite = MySprite.CreateSprite("SquareSimple", origin + new Vector2(x + w / 2, y + h / 2) * scale, new Vector2(w, h) * scale);
            sprite.Color = color; frame.Add(sprite);
        }
        private static void Text(MySpriteDrawFrame frame, Vector2 origin, float scale, string value, float x, float y, float font, Color color)
        {
            var sprite = MySprite.CreateText(value, "Debug", color, font * scale, TextAlignment.LEFT);
            sprite.Position = origin + new Vector2(x, y) * scale; frame.Add(sprite);
        }
        private static string Clip(string value, int max) => value.Length <= max ? value : value.Substring(0, max - 3) + "...";
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            plugin.Dispatcher.Completed -= Completed;
            MyAPIGateway.TerminalControls.CustomControlGetter -= Controls;
            MyAPIGateway.TerminalControls.CustomActionGetter -= Actions;
            foreach (var view in views.Values) view.Block.AppendingCustomInfo -= CustomInfo;
            views.Clear(); order.Clear(); scanning = null; scanIds = knownHubs = null;
        }
    }
}
