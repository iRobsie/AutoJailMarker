﻿using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Utility;

using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Linq;
using System.Linq.Expressions;
using System.IO;

using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Objects.Types;

using Dalamud.Hooking;
using Dalamud.Logging;

using AutoJailMarker.Helper;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;


namespace AutoJailMarker
{
    public sealed partial class AutoJailMarker : IDalamudPlugin
    {
        public string Name => "Auto Jail Marker";

        private const string commandName = "/jailmarker";
        public static uint collectionTimeout = 15000;
        public static uint jailCount = 3;
        public static bool printSkillID = false;

        public static uint[] skillIds = new uint[] { 645, 1652, 11115, 11116 };
        public List<String> collectionTargets;
        public List<int> markedInds;
        public List<string> orderedPartyList;

        public DateTime collectionExpireTime;
        public bool isCollecting = false;
        public bool marked = false;
        

        private DalamudPluginInterface PluginInterface { get; init; }
        private CommandManager CommandManager { get; init; }
        public static SigScanner SigScanner { get; private set; }
        private Configuration Configuration { get; init; }
        private PartyList PList { get; init; }
        public Character myTitan { get; set; }
        public bool titanLocked = false;
        private GameObject titanLastTarget { get; set; }
        public string titanName { get => myTitan == null ? myTitan.Name.ToString() : ""; }
        private PluginUi PluginUi { get; init; }

        public static bool PlayerExists => DalamudApi.ClientState?.LocalPlayer != null;

        private bool pluginReady = false;

        //actioneffect
        private delegate void ReceiveActionEffectDelegate(int sourceId, IntPtr sourceCharacter, IntPtr pos, IntPtr effectHeader, IntPtr effectArray, IntPtr effectTrail);
        private Hook<ReceiveActionEffectDelegate> ReceiveActionEffectHook;

        public AutoJailMarker(
            [RequiredVersion("1.0")] DalamudPluginInterface pluginInterface,
            [RequiredVersion("1.0")] CommandManager commandManager,
            SigScanner sigscanner)
        {
            this.PluginInterface = pluginInterface;
            this.CommandManager = commandManager;
            DalamudApi.Initialize(this, pluginInterface);
            PrintEcho("-Initializing Plugin-");
            SigScanner = sigscanner;


            if (!FFXIVClientStructs.Resolver.Initialized) FFXIVClientStructs.Resolver.Initialize(); //ocealot

            this.Configuration = this.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();


            IntPtr receiveActionEffectFuncPtr = SigScanner.ScanText("4C 89 44 24 ?? 55 56 57 41 54 41 55 41 56 48 8D 6C 24"); //ocealot
            ReceiveActionEffectHook = new Hook<ReceiveActionEffectDelegate>(receiveActionEffectFuncPtr, ReceiveActionEffect);
            ReceiveActionEffectHook.Enable();

            // you might normally want to embed resources and load them from the manifest stream
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            var imagePath = Path.Combine(Path.GetDirectoryName(assemblyLocation)!, "Titanos_45.png");
            var goatImage = this.PluginInterface.UiBuilder.LoadImage(imagePath);
            PluginUi = new PluginUi(this.Configuration, goatImage, this);

            DalamudApi.Framework.Update += Update;
            Game.Initialize();
            this.pluginReady = true;

            this.CommandManager.AddHandler(commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "A scuffed jail automarker"
            });

            

            this.PluginInterface.UiBuilder.Draw += DrawUI;
            this.PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUI;
        }

        public static float RunTime => (float)DalamudApi.PluginInterface.LoadTimeDelta.TotalSeconds;
        public static long FrameCount => (long)DalamudApi.PluginInterface.UiBuilder.FrameCount;
        private void Update(Framework framework)
        {
            if (!pluginReady) return;

            Game.ReadyCommand();

            //ocealot
            if (isCollecting || marked)
            {
                UpdateCollectionTime();
            }
        }
        private void InitializeJailCollection()
        {
            collectionTargets = new List<String>();
        }
        private void UpdateCollectionTime()
        {
            isCollecting = DateTime.Now <= collectionExpireTime ? true : false;
            if (marked && !isCollecting)
            {
                ClearMarkers();
            }
        }

        private void ExecuteMarkers()
        {
            PrintEcho("---Marking Players---");
            int playersMarked = 0;
            List<NameInd> PartyPrioList = new List<NameInd>();
            UpdateOrderedParty(false);
            //PrintEcho($"ordered party list size: {orderedPartyList.Count}");
            for(int i=0; i<8; i++)
            {
                for(int j=0; j<orderedPartyList.Count; j++)
                {
                    //PrintEcho($"{Configuration.prio[i]} <> {orderedPartyList[j]}");
                    if(orderedPartyList[j].Contains(Configuration.Prio[i]))
                    {
                        NameInd tpair = new NameInd(Configuration.Prio[i], (j+1));
                        PartyPrioList.Add(tpair);
                        PrintEcho($"Added {tpair.name} to NameInd list as {tpair.partynum.ToString()}. ");
                        break;
                    }
                }
            }

            PrintEcho("---Begin Matching Targets---");
            markedInds = new List<int>();
            for(int i=0; i<PartyPrioList.Count; i++)
            {
                PrintEcho($">start match for {PartyPrioList[i].name}");
                if (collectionTargets.Contains(PartyPrioList[i].name))
                {
                    PrintEcho($"--> FOUND");
                    string commandbuilder = $"/mk attack{(playersMarked+1)} <{PartyPrioList[i].partynum}>";
                    markedInds.Add(PartyPrioList[i].partynum);
                    Game.ExecuteCommand(commandbuilder);
                    playersMarked++;
                    if(playersMarked >= jailCount)
                    {
                        break;
                    }
                } else PrintEcho($"--> NOT FOUND");
            }


            //PrintEcho("Finished Marking");
            marked = true;
        }
        
        public unsafe void UpdateOrderedParty(bool echo = true)
        {
            AddonPartyList* plist = (AddonPartyList*)DalamudApi.GameGui.GetAddonByName("_PartyList", 1);
            var partyMembers = plist->PartyMember;

            orderedPartyList = new List<string>();
            if(echo) PrintEcho($"Updating party list order:");

            for (var i = 0; i < DalamudApi.PartyList.Length; i++)
            {
                var listLength = orderedPartyList.Count;
                AddonPartyList.PartyListMemberStruct partyMember = partyMembers[i];

                string[] aPartyMemberName = partyMember.Name->NodeText.ToString().Split(' ');

                //aPartyMemberName[3] = aPartyMemberName[3].Replace(".", "");
                for (var p = 0; p < aPartyMemberName.Length; p++)
                {
                    aPartyMemberName[p] = aPartyMemberName[p].Replace(".", "");
                }

                foreach (var partyListMember in DalamudApi.PartyList)
                {
                    var partyListName = partyListMember.Name.TextValue;
                    var aPartyListMemberName = partyListName.Split(' ');

                    if (aPartyListMemberName[0] != aPartyMemberName[1]) continue;
                    if (!aPartyListMemberName[1].StartsWith(aPartyMemberName[2])) continue;

                    orderedPartyList.Add(partyListName);
                    if(echo) PrintEcho($"Added: {partyListName}");
                    break;
                }

                if (listLength != orderedPartyList.Count) continue;

                var partyMemberName = $"{aPartyMemberName[2]} {aPartyMemberName[3]}";
                orderedPartyList.Add(partyMemberName);
            }
        }

        public void ClearMarkers()
        {
            PrintEcho("---Clearing Marks---");
            foreach(int ind in markedInds)
            {
                Game.ExecuteCommand($"/mk clear <{ind}>");
            }
            marked = false;
            UpdateCollectionTime();
        }

        public void Dispose()
        {
            ReceiveActionEffectHook.Dispose();
            PluginUi.Dispose();
            DalamudApi.Framework.Update -= Update;
            CommandManager.RemoveHandler(commandName);
            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi -= DrawConfigUI;

        }

        private void OnCommand(string command, string args)
        {
            // in response to the slash command, just display our main ui
            PluginUi.SettingsVisible = true;
        }

        private void DrawUI()
        {
            this.PluginUi.Draw();
        }

        private void DrawConfigUI()
        {
            PluginUi.SettingsVisible = !PluginUi.SettingsVisible;
        }

        public static void PrintEcho(string message) => DalamudApi.ChatGui.Print($"[JailMarker] {message}");
        public static void PrintError(string message) => DalamudApi.ChatGui.PrintError($"[JailMarker] {message}");
    }
}

public static class Extensions
{
    public static object Cast(this Type Type, object data)
    {
        var DataParam = Expression.Parameter(typeof(object), "data");
        var Body = Expression.Block(Expression.Convert(Expression.Convert(DataParam, data.GetType()), Type));

        var Run = Expression.Lambda(Body, DataParam).Compile();
        var ret = Run.DynamicInvoke(data);
        return ret;
    }

    public static bool In<T>(this T item, params T[] list)
    {
        return list.Contains(item);
    }
}

public struct NameInd
{
    public string name;
    public int partynum;

    public NameInd(string n, int i)
    {
        name = n;
        partynum = i;
    }
}