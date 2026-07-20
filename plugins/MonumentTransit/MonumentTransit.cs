using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("MonumentTransit", "kolyanp", "2.2.2")]   
    [Description("Paid teleport terminals on monuments with dynamic economy and UI")]
    public class MonumentTransit : RustPlugin
    {
        public static MonumentTransit Instance { get; private set; }

        // Optional dependencies
        [PluginReference]
        private Plugin ServerRewards, Economics, IQEconomic, Kits;

        private void Init()
        {
            Instance = this;
            LoadConfig();
            LoadData();
            RegisterPermissions();
            Puts("MonumentTransit initialized.");
        }

        private void Unload()
        {
            // CHANGE: Kill all terminals on unload to prevent orphaned NPCs and console spam
            if (pluginData != null && pluginData.Terminals != null)
            {
                foreach (var terminal in pluginData.Terminals.Values)
                {
                    KillTerminal(terminal);
                }
            }
            SaveData();
            Instance = null;
        }

        private void CleanupOrphanedTerminals()
        {
            // CHANGE: Find and destroy any previously orphaned terminal NPCs that are causing NREs
            var orphaned = BaseNetworkable.serverEntities.OfType<BasePlayer>()
                .Where(p => p.IsNpc && p.displayName != null && p.displayName.Contains("Transit Terminal"))
                .ToList();
                
            foreach (var npc in orphaned)
            {
                npc.Kill();
            }
            if (orphaned.Count > 0) Puts($"Cleaned up {orphaned.Count} orphaned NPC terminals.");
        }

        private void OnServerInitialized()
        {
            CleanupOrphanedTerminals();
            InitializeMonuments();
            InitializeTerminals();
            GenerateMapImage();
            Puts($"Server Initialized. Loaded {cachedMonuments.Count} monuments and {pluginData.Terminals.Count} terminals.");
        }
        
        private string autoMapImageId = "";

        private void GenerateMapImage()
        {
            if (string.IsNullOrEmpty(config.MapImageUrl))
            {
                // CHANGE: Run on a slight delay to allow the server to finish starting up
                timer.Once(5f, () => 
                {
                    try
                    {
                        int width, height;
                        Color background;
                        // Use a lower scale (0.15f) and 0 ocean margin to prevent main-thread freeze (11s lag) and fix UI coordinate alignment
                        byte[] imageBytes = MapImageRenderer.Render(out width, out height, out background, 0.15f, true, false, 0);
                        if (imageBytes != null)
                        {
                            uint imageId = FileStorage.server.Store(imageBytes, FileStorage.Type.jpg, CommunityEntity.ServerInstance.net.ID, 0);
                            autoMapImageId = imageId.ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        Puts($"Failed to auto-generate map image: {ex.Message}");
                    }
                });
            }
        }
        
        private void RegisterPermissions()
        {
            permission.RegisterPermission("monumenttransit.use", this);
            permission.RegisterPermission("monumenttransit.admin", this);
        }

        // --- API ---

        [HookMethod("OpenTerminal")]
        public void API_OpenTerminal(BasePlayer player, string terminalId) => OpenPlayerUI(player, terminalId);

        [HookMethod("TeleportPlayer")]
        public bool API_TeleportPlayer(BasePlayer player, string destinationId) => ProcessTeleport(player, destinationId);

        [HookMethod("CalculatePrice")]
        public float API_CalculatePrice(BasePlayer player, string destinationId, string terminalId) => GetFinalPrice(player, destinationId, terminalId);

        [HookMethod("GetDestinations")]
        public Dictionary<string, object> API_GetDestinations()
        {
            var result = new Dictionary<string, object>();
            foreach (var kvp in pluginData.Destinations)
            {
                result[kvp.Key] = new Dictionary<string, object>
                {
                    { "Name", kvp.Value.Name },
                    { "Position", kvp.Value.Position },
                    { "Permission", kvp.Value.Permission }
                };
            }
            return result;
        }

        [HookMethod("CreateDestination")]
        public string API_CreateDestination(string name, Vector3 position, string permission = "")
        {
            string id = System.Guid.NewGuid().ToString("N");
            pluginData.Destinations[id] = new DestinationData
            {
                Id = id,
                Name = name,
                Position = position,
                Permission = permission,
                PopularitySurcharge = 0f,
                LastDecayTime = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            foreach (var term in pluginData.Terminals.Values)
            {
                if (term.AllowedDestinations != null && term.AllowedDestinations.Count > 0 && !term.AllowedDestinations.Contains("NONE"))
                {
                    term.AllowedDestinations.Add(id);
                }
            }

            SaveDataAsync();
            return id;
        }

        [HookMethod("CreateTerminal")]
        public string API_CreateTerminal(Vector3 position, Vector3 rotation, string type = "NPC")
        {
            if (!System.Enum.TryParse(type, true, out TerminalType tType))
                tType = TerminalType.Invisible;

            string id = System.Guid.NewGuid().ToString("N");
            pluginData.Terminals[id] = new TerminalData
            {
                Id = id,
                Type = tType,
                Position = position,
                Rotation = rotation,
                MonumentId = GetNearestMonument(position)?.Id ?? "unknown"
            };
            SaveDataAsync();
            SpawnTerminal(pluginData.Terminals[id]);
            return id;
        }

        [HookMethod("RemoveTerminal")]
        public bool API_RemoveTerminal(string id)
        {
            if (pluginData.Terminals.TryGetValue(id, out var terminal))
            {
                KillTerminal(terminal);
                pluginData.Terminals.Remove(id);
                SaveDataAsync();
                return true;
            }
            return false;
        }

        // --- Config ---

        private PluginConfig config;

        public class PluginConfig
        {
            [JsonProperty("Economy Provider (Scrap, Economics, ServerRewards, IQEconomic)")]
            [JsonConverter(typeof(StringEnumConverter))]
            public EconomyType EconomyProvider { get; set; } = EconomyType.Scrap;

            [JsonProperty("Message Display Type (Chat, GameTip, TopScreen)")]
            [JsonConverter(typeof(StringEnumConverter))]
            public MessageDisplayType MessageType { get; set; } = MessageDisplayType.TopScreen;

            [JsonProperty("Message Duration (seconds)")]
            public float MessageDuration { get; set; } = 4f;

            [JsonProperty("Base Price")]
            public float BasePrice { get; set; } = 50f;

            [JsonProperty("Distance Multiplier (Price per meter)")]
            public float DistanceMultiplier { get; set; } = 0.05f;

            [JsonProperty("Min Price")]
            public float MinPrice { get; set; } = 10f;

            [JsonProperty("Max Price")]
            public float MaxPrice { get; set; } = 1000f;

            [JsonProperty("Popularity Multiplier (Max surcharge)")]
            public float MaxPopularityMultiplier { get; set; } = 2.0f;

            [JsonProperty("Popularity Decay (per hour)")]
            public float PopularityDecayPerHour { get; set; } = 0.5f;

            [JsonProperty("Popularity Increase per Teleport")]
            public float PopularityIncreasePerTeleport { get; set; } = 0.1f;

            [JsonProperty("Default NPC Kit")]
            public string DefaultNPCKit { get; set; } = "";

            [JsonProperty("Default Prefab")]
            public string DefaultPrefab { get; set; } = "assets/prefabs/deployable/vendingmachine/vendingmachine.prefab";

            [JsonProperty("Marker Color")]
            public string MarkerColor { get; set; } = "0.2 0.8 0.2 1";
            
            [JsonProperty("Marker Size")]
            public float MarkerSize { get; set; } = 15f;

            [JsonProperty("UI Main Color")]
            public string UIMainColor { get; set; } = "0.1 0.1 0.1 0.95";
            
            [JsonProperty("Map Image URL (leave empty for none)")]
            public string MapImageUrl { get; set; } = "";

            [JsonProperty("Reset Teleport Limits Daily")]
            public bool ResetLimitsDaily { get; set; } = false;

            [JsonProperty("Permission Settings (Priority, Daily Limit, Teleport Delay)")]
            public Dictionary<string, PermSetting> PermissionSettings { get; set; } = new Dictionary<string, PermSetting>
            {
                { "monumenttransit.vip", new PermSetting { Priority = 1, DailyLimit = 100, TeleportDelay = 5f } },
                { "default", new PermSetting { Priority = 0, DailyLimit = 10, TeleportDelay = 15f } }
            };

            [JsonProperty("Teleport Cancellation Settings")]
            public CancelSettings TeleportCancellation { get; set; } = new CancelSettings();

            [JsonProperty("TopScreen UI Settings")]
            public TopScreenSettings TopScreenUI { get; set; } = new TopScreenSettings();
        }

        public class PermSetting
        {
            public int Priority { get; set; }
            public int DailyLimit { get; set; }
            public float TeleportDelay { get; set; }
        }

        public class CancelSettings
        {
            [JsonProperty("Cancel on Damage (combat, fall, etc)")]
            public bool CancelOnDamage { get; set; } = true;
            [JsonProperty("Cancel if Bleeding")]
            public bool CancelIfBleeding { get; set; } = true;
            [JsonProperty("Cancel if Freezing")]
            public bool CancelIfFreezing { get; set; } = true;
            [JsonProperty("Cancel if Starving")]
            public bool CancelIfStarving { get; set; } = true;
            [JsonProperty("Cancel on Movement Radius (meters)")]
            public float CancelMovementRadius { get; set; } = 5.0f;
        }

        public class TopScreenSettings
        {
            [JsonProperty("Anchor Min (Position and Left/Bottom size)")]
            public string AnchorMin { get; set; } = "0.25 0.86";
            
            [JsonProperty("Anchor Max (Position and Right/Top size)")]
            public string AnchorMax { get; set; } = "0.75 0.92";

            [JsonProperty("Background Color")]
            public string BackgroundColor { get; set; } = "0.24 0.44 0.65 0.95";

            [JsonProperty("Text Color")]
            public string TextColor { get; set; } = "1 1 1 1";

            [JsonProperty("Text Size")]
            public int TextSize { get; set; } = 14;
        }

        public enum EconomyType { Scrap, Economics, ServerRewards, IQEconomic }
        public enum MessageDisplayType { Chat, GameTip, TopScreen }

        protected override void LoadDefaultConfig()
        {
            config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<PluginConfig>();
                if (config == null) LoadDefaultConfig();
            }
            catch
            {
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        // --- Data ---

        private PluginData pluginData;
        private DynamicConfigFile dataFile;

        public class PluginData
        {
            public Dictionary<string, TerminalData> Terminals = new Dictionary<string, TerminalData>();
            public Dictionary<string, DestinationData> Destinations = new Dictionary<string, DestinationData>();
            public Dictionary<ulong, PlayerUsageData> PlayerUsage = new Dictionary<ulong, PlayerUsageData>();
            public List<string> Categories = new List<string> { "General", "Settlements", "Metro Stations" };
        }

        public class PlayerUsageData
        {
            public int TeleportsUsed { get; set; }
            public string LastResetDate { get; set; } = "";
        }

        public class TerminalData
        {
            public string Id { get; set; }
            [JsonConverter(typeof(StringEnumConverter))]
            public TerminalType Type { get; set; }
            public Vector3 Position { get; set; }
            public Vector3 Rotation { get; set; }
            public string MonumentId { get; set; }
            
            public string Prefab { get; set; }
            public ulong NetworkId { get; set; }
            public ulong MarkerNetworkId { get; set; }
            public string Name { get; set; } = "Transit Terminal";
            public string Kit { get; set; } = "";
            public List<string> AllowedDestinations { get; set; } = new List<string>();
        }

        public enum TerminalType { NPC, Prop, Invisible }

        public class DestinationData
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Category { get; set; } = "None";
            public Vector3 Position { get; set; }
            public string Permission { get; set; }
            public float PopularitySurcharge { get; set; } 
            public double LastDecayTime { get; set; }
        }

        public class PlayerUIState
        {
            public string TerminalId;
            public string SelectedCategory = "None";
            public string SelectedDestId = "";
            public bool IsTeleporting = false;
            public int DestPage = 0;
            public int AdminTermDestPage = 0;
            public int AdminMainTermPage = 0;
            public int AdminMainDestPage = 0;
        }

        private Dictionary<ulong, PlayerUIState> playerUIStates = new Dictionary<ulong, PlayerUIState>();

        private void LoadData()
        {
            pluginData = new PluginData();
            
            string oldFileName = Name;
            if (Interface.Oxide.DataFileSystem.ExistsDatafile(oldFileName))
            {
                Puts($"Found old monolithic data file '{oldFileName}.json'. Migrating to new separated structure in 'oxide/data/{Name}/'...");
                try
                {
                    var oldDataFile = Interface.Oxide.DataFileSystem.GetFile(oldFileName);
                    var oldData = oldDataFile.ReadObject<PluginData>();
                    if (oldData != null) pluginData = oldData;
                    
                    SaveData();
                    
                    string oldPath = oldDataFile.Filename;
                    try { System.IO.File.Move(oldPath, oldPath.Replace(".json", ".old.json")); } catch { }
                    Puts("Migration complete.");
                }
                catch (Exception ex)
                {
                    Puts($"Error migrating old data: {ex.Message}");
                }
            }
            else
            {
                pluginData.Terminals = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, TerminalData>>($"{Name}/Terminals") ?? new Dictionary<string, TerminalData>();
                pluginData.Destinations = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, DestinationData>>($"{Name}/Destinations") ?? new Dictionary<string, DestinationData>();
                pluginData.PlayerUsage = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerUsageData>>($"{Name}/PlayerUsage") ?? new Dictionary<ulong, PlayerUsageData>();
                pluginData.Categories = Interface.Oxide.DataFileSystem.ReadObject<List<string>>($"{Name}/Categories");
                if (pluginData.Categories == null || pluginData.Categories.Count == 0)
                {
                    pluginData.Categories = new List<string> { "None", "Settlements", "Metro Stations" };
                }
            }

            // Ensure legacy destinations have a category
            foreach (var dest in pluginData.Destinations.Values)
            {
                if (string.IsNullOrEmpty(dest.Category) || dest.Category == "General") dest.Category = "None";
            }

            if (pluginData.Categories != null)
            {
                for (int i = 0; i < pluginData.Categories.Count; i++)
                {
                    if (pluginData.Categories[i] == "General") pluginData.Categories[i] = "None";
                }
                pluginData.Categories = pluginData.Categories.Distinct().ToList();
                if (!pluginData.Categories.Contains("None")) pluginData.Categories.Insert(0, "None");
            }
        }

        private void SaveData()
        {
            if (pluginData != null)
            {
                Interface.Oxide.DataFileSystem.WriteObject($"{Name}/Terminals", pluginData.Terminals);
                Interface.Oxide.DataFileSystem.WriteObject($"{Name}/Destinations", pluginData.Destinations);
                Interface.Oxide.DataFileSystem.WriteObject($"{Name}/PlayerUsage", pluginData.PlayerUsage);
                Interface.Oxide.DataFileSystem.WriteObject($"{Name}/Categories", pluginData.Categories);
            }
        }

        private void LogAction(string type, string message)
        {
            LogToFile(type, $"[{DateTime.Now:HH:mm:ss}] {message}", this);
        }

        // --- Localization ---

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You don't have permission to use this.",
                ["NotEnoughFunds"] = "You do not have enough funds to teleport. Required: {0}",
                ["TeleportSuccess"] = "You have been teleported to {0}!",
                ["TerminalCreated"] = "Terminal created successfully.",
                ["TerminalDeleted"] = "Terminal deleted.",
                ["DestinationCreated"] = "Destination {0} created.",
                ["DestinationDeleted"] = "Destination {0} deleted.",
                ["EditorNoTerminal"] = "No terminal selected.",
                ["ButtonTeleport"] = "TELEPORT",
                ["CostText"] = "Cost: {0}",
                ["PopularityText"] = "Popularity Surcharge: +{0}%",
                ["MapTitle"] = "Teleport Terminal",
                ["MapSubtitle"] = "Select a destination on the map or in the list.",
                ["AdminMenuTitle"] = "TERMINAL EDITOR",
                ["AlreadyTeleporting"] = "You are already teleporting!",
                ["LimitReached"] = "You have reached your teleport limit ({0}/{1}).",
                ["TeleportingIn"] = "Teleporting in {0} seconds. Stay near the terminal and avoid damage!",
                ["TeleportCancelled"] = "Teleport cancelled!",
                ["UI_Sections"] = "SECTIONS",
                ["UI_Destinations"] = "DESTINATIONS",
                ["UI_Cost"] = "COST",
                ["UI_CancelButton"] = "Cancel",
                ["UI_Teleporting"] = "Teleporting..."
            }, this, "en");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "У вас нет разрешения на использование этого.",
                ["NotEnoughFunds"] = "У вас недостаточно средств для телепортации. Нужно: {0}",
                ["TeleportSuccess"] = "Вы были телепортированы в {0}!",
                ["TerminalCreated"] = "Терминал успешно создан.",
                ["TerminalDeleted"] = "Терминал удален.",
                ["DestinationCreated"] = "Точка назначения {0} создана.",
                ["DestinationDeleted"] = "Точка назначения {0} удалена.",
                ["EditorNoTerminal"] = "Терминал не выбран.",
                ["ButtonTeleport"] = "ТЕЛЕПОРТИРОВАТЬСЯ",
                ["CostText"] = "Стоимость: {0}",
                ["PopularityText"] = "Надбавка за популярность: +{0}%",
                ["MapTitle"] = "Терминал Телепортации",
                ["MapSubtitle"] = "Выберите точку назначения на карте или в списке.",
                ["AdminMenuTitle"] = "РЕДАКТОР ТЕРМИНАЛОВ",
                ["AlreadyTeleporting"] = "Вы уже телепортируетесь!",
                ["LimitReached"] = "Вы исчерпали свой лимит телепортаций ({0}/{1}).",
                ["TeleportingIn"] = "Телепортация через {0} секунд. Оставайтесь возле терминала и избегайте урона!",
                ["TeleportCancelled"] = "Телепортация отменена!",
                ["UI_Sections"] = "РАЗДЕЛЫ",
                ["UI_Destinations"] = "ТОЧКИ НАЗНАЧЕНИЯ",
                ["UI_Cost"] = "ЦЕНА",
                ["UI_CancelButton"] = "Отмена",
                ["UI_Teleporting"] = "Телепортация..."
            }, this, "ru");
            
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "У вас немає дозволу на використання цього.",
                ["NotEnoughFunds"] = "У вас недостатньо коштів для телепортації. Потрібно: {0}",
                ["TeleportSuccess"] = "Вас було телепортовано до {0}!",
                ["TerminalCreated"] = "Термінал успішно створено.",
                ["TerminalDeleted"] = "Термінал видалено.",
                ["DestinationCreated"] = "Точку призначення {0} створено.",
                ["DestinationDeleted"] = "Точку призначення {0} видалено.",
                ["EditorNoTerminal"] = "Термінал не вибрано.",
                ["ButtonTeleport"] = "ТЕЛЕПОРТУВАТИСЬ",
                ["CostText"] = "Вартість: {0}",
                ["PopularityText"] = "Надбавка за популярність: +{0}%",
                ["MapTitle"] = "Термінал Телепортації",
                ["MapSubtitle"] = "Виберіть точку призначення на карті або у списку.",
                ["AdminMenuTitle"] = "РЕДАКТОР ТЕРМІНАЛІВ",
                ["AlreadyTeleporting"] = "Ви вже телепортуєтесь!",
                ["LimitReached"] = "Ви вичерпали свій ліміт телепортацій ({0}/{1}).",
                ["TeleportingIn"] = "Телепортація через {0} секунд. Залишайтесь біля терміналу та уникайте шкоди!",
                ["TeleportCancelled"] = "Телепорт скасовано!",
                ["UI_Sections"] = "РОЗДІЛИ",
                ["UI_Destinations"] = "ТОЧКИ ПРИЗНАЧЕННЯ",
                ["UI_Cost"] = "ВАРТІСТЬ",
                ["UI_CancelButton"] = "Скасувати",
                ["UI_Teleporting"] = "Телепортація..."
            }, this, "uk");
        }

        private string GetMsg(string key, string userId = null) => lang.GetMessage(key, this, userId);

        private void SendMsg(BasePlayer player, string text)
        {
            float duration = config.MessageDuration;
            if (config.MessageType == MessageDisplayType.GameTip)
            {
                player.SendConsoleCommand("gametip.showgametip", text);
                timer.Once(duration, () => { if (player != null && player.IsConnected) player.SendConsoleCommand("gametip.hidegametip"); });
            }
            else if (config.MessageType == MessageDisplayType.TopScreen)
            {
                string uiName = "MonumentTransit.TopMsg";
                CuiHelper.DestroyUi(player, uiName);

                var elements = new CuiElementContainer();
                
                elements.Add(new CuiPanel
                {
                    Image = { Color = config.TopScreenUI.BackgroundColor },
                    RectTransform = { AnchorMin = config.TopScreenUI.AnchorMin, AnchorMax = config.TopScreenUI.AnchorMax },
                    FadeOut = 0.5f
                }, "Overlay", uiName);

                elements.Add(new CuiElement
                {
                    Parent = uiName,
                    Components =
                    {
                        new CuiTextComponent { Text = text.ToUpper(), FontSize = config.TopScreenUI.TextSize, Align = TextAnchor.MiddleCenter, Color = config.TopScreenUI.TextColor, FadeIn = 0.2f },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }
                });

                CuiHelper.AddUi(player, elements);
                timer.Once(duration, () => { if (player != null && player.IsConnected) CuiHelper.DestroyUi(player, uiName); });
            }
            else
            {
                player.ChatMessage(text);
            }
        }

        // --- Economy ---

        private bool HasFunds(BasePlayer player, float amount)
        {
            if (amount <= 0) return true;

            int intAmount = (int)Math.Ceiling(amount);
            EconomyType currentProvider = config.EconomyProvider;

            if (currentProvider == EconomyType.ServerRewards && ServerRewards != null && ServerRewards.IsLoaded)
            {
                var points = ServerRewards.Call<int>("CheckPoints", player.userID);
                return points >= intAmount;
            }

            if (currentProvider == EconomyType.Economics && Economics != null && Economics.IsLoaded)
            {
                var balance = Economics.Call<double>("Balance", player.userID);
                return balance >= amount;
            }

            if (currentProvider == EconomyType.IQEconomic && IQEconomic != null && IQEconomic.IsLoaded)
            {
                var balance = IQEconomic.Call<int>("API_GET_BALANCE", player.userID);
                return balance >= intAmount;
            }

            int scrapId = ItemManager.FindItemDefinition("scrap").itemid;
            int scrapAmount = player.inventory.GetAmount(scrapId);
            return scrapAmount >= intAmount;
        }

        private bool WithdrawFunds(BasePlayer player, float amount)
        {
            if (!HasFunds(player, amount)) return false;
            if (amount <= 0) return true;

            int intAmount = (int)Math.Ceiling(amount);
            EconomyType currentProvider = config.EconomyProvider;

            if (currentProvider == EconomyType.ServerRewards && ServerRewards != null && ServerRewards.IsLoaded)
            {
                ServerRewards.Call("TakePoints", player.userID, intAmount);
                return true;
            }

            if (currentProvider == EconomyType.Economics && Economics != null && Economics.IsLoaded)
            {
                Economics.Call("Withdraw", player.userID, (double)amount);
                return true;
            }

            if (currentProvider == EconomyType.IQEconomic && IQEconomic != null && IQEconomic.IsLoaded)
            {
                IQEconomic.Call("API_REMOVE_BALANCE", player.userID, intAmount);
                return true;
            }

            int scrapId = ItemManager.FindItemDefinition("scrap").itemid;
            player.inventory.Take(null, scrapId, intAmount);
            return true;
        }
        
        private string GetCurrencyName()
        {
            EconomyType currentProvider = config.EconomyProvider;
            if (currentProvider == EconomyType.ServerRewards && ServerRewards != null && ServerRewards.IsLoaded) return "RP";
            if (currentProvider == EconomyType.Economics && Economics != null && Economics.IsLoaded) return "Coins";
            if (currentProvider == EconomyType.IQEconomic && IQEconomic != null && IQEconomic.IsLoaded) return "Balance";
            return "Scrap";
        }

        // --- Monuments ---

        private Dictionary<string, MonumentInfoData> cachedMonuments = new Dictionary<string, MonumentInfoData>();

        public class MonumentInfoData
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public Vector3 Position { get; set; }
        }

        private void InitializeMonuments()
        {
            cachedMonuments.Clear();

            if (TerrainMeta.Path != null && TerrainMeta.Path.Monuments != null)
            {
                foreach (var monument in TerrainMeta.Path.Monuments)
                {
                    if (monument == null) continue;
                    
                    string lowerName = monument.name.ToLower();
                    if (lowerName.Contains("cave") || lowerName.Contains("swamp") || lowerName.Contains("substation") || 
                        lowerName.Contains("lake") || lowerName.Contains("water well") || lowerName.Contains("ruin") || 
                        lowerName.Contains("oasis") || lowerName.Contains("canyon") || lowerName.Contains("tunnel link"))
                    {
                        continue;
                    }
                    
                    string name = monument.name.ToLower();
                    if (!cachedMonuments.ContainsKey(name))
                    {
                        cachedMonuments.Add(name, new MonumentInfoData
                        {
                            Id = name,
                            Name = monument.displayPhrase?.english ?? monument.name,
                            Position = monument.transform.position
                        });
                    }
                }
            }
        }
        
        private MonumentInfoData GetNearestMonument(Vector3 position)
        {
            MonumentInfoData nearest = null;
            float minDistance = float.MaxValue;
            foreach (var mon in cachedMonuments.Values)
            {
                float dist = Vector3.Distance(position, mon.Position);
                if (dist < minDistance)
                {
                    minDistance = dist;
                    nearest = mon;
                }
            }
            return nearest;
        }

        // --- Destinations ---

        private Timer saveTimer;

        private float GetFinalPrice(BasePlayer player, string destinationId, string terminalId)
        {
            if (!pluginData.Destinations.TryGetValue(destinationId, out var destination)) return 0f;
            if (!pluginData.Terminals.TryGetValue(terminalId, out var terminal)) return 0f;

            float price = config.BasePrice;
            float distance = Vector3.Distance(terminal.Position, destination.Position);
            price += distance * config.DistanceMultiplier;

            UpdatePopularityDecay(destination);
            float popularityMultiplier = 1f + destination.PopularitySurcharge;
            price *= popularityMultiplier;

            float discount = GetPlayerDiscount(player);
            price *= (1f - discount);

            return Math.Clamp(price, config.MinPrice, config.MaxPrice);
        }

        private void UpdatePopularityDecay(DestinationData destination)
        {
            double now = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            double hoursPassed = (now - destination.LastDecayTime) / 3600.0;
            
            if (hoursPassed > 0)
            {
                float decayAmount = (float)(hoursPassed * config.PopularityDecayPerHour);
                destination.PopularitySurcharge = Math.Max(0f, destination.PopularitySurcharge - decayAmount);
                destination.LastDecayTime = now;
            }
        }

        private void IncreasePopularity(string destinationId)
        {
            if (pluginData.Destinations.TryGetValue(destinationId, out var destination))
            {
                UpdatePopularityDecay(destination);
                destination.PopularitySurcharge = Math.Min(
                    config.MaxPopularityMultiplier - 1f, 
                    destination.PopularitySurcharge + config.PopularityIncreasePerTeleport
                );
                SaveDataAsync();
            }
        }

        private float GetPlayerDiscount(BasePlayer player)
        {
            float maxDiscount = 0f;
            foreach (var perm in permission.GetUserPermissions(player.UserIDString))
            {
                if (perm.StartsWith("monumenttransit.discount."))
                {
                    string pctStr = perm.Replace("monumenttransit.discount.", "");
                    if (float.TryParse(pctStr, out float pct))
                    {
                        float val = pct / 100f;
                        if (val > maxDiscount) maxDiscount = val;
                    }
                }
            }
            return Math.Clamp(maxDiscount, 0f, 1f);
        }
        
        private void SaveDataAsync()
        {
            if (saveTimer != null && !saveTimer.Destroyed) return;
            saveTimer = timer.Once(2f, () => 
            {
                SaveData();
                saveTimer = null;
            });
        }

        // --- Terminals & NPC ---

        private void InitializeTerminals()
        {
            foreach (var terminal in pluginData.Terminals.Values) SpawnTerminal(terminal);
        }

        private void SpawnTerminal(TerminalData terminal)
        {
            if (terminal.Type == TerminalType.NPC) SpawnNPCTerminal(terminal);
            else if (terminal.Type == TerminalType.Prop) SpawnPropTerminal(terminal);
            else if (terminal.Type == TerminalType.Invisible) SpawnInvisibleTerminal(terminal);
        }

        private void SpawnPropTerminal(TerminalData terminal)
        {
            var prefab = string.IsNullOrEmpty(terminal.Prefab) ? config.DefaultPrefab : terminal.Prefab;
            var ent = GameManager.server.CreateEntity(prefab, terminal.Position, Quaternion.Euler(terminal.Rotation));
            if (ent == null) return;
            ent.enableSaving = false;
            ent.Spawn();
            terminal.NetworkId = ent.net.ID.Value;
        }

        private void SpawnInvisibleTerminal(TerminalData terminal)
        {
            var ent = GameManager.server.CreateEntity("assets/prefabs/deployable/small stash/small_stash_deployed.prefab", terminal.Position, Quaternion.Euler(terminal.Rotation));
            if (ent == null) return;
            ent.enableSaving = false;
            ent.Spawn();
            terminal.NetworkId = ent.net.ID.Value;
        }

        private void KillTerminal(TerminalData terminal)
        {
            if (terminal.Type == TerminalType.Prop || terminal.Type == TerminalType.Invisible)
            {
                var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(terminal.NetworkId)) as BaseEntity;
                if (ent != null) ent.Kill();
            }
            else if (terminal.Type == TerminalType.NPC) KillNPCTerminal(terminal);
        }
        
        private bool ProcessTeleport(BasePlayer player, string destinationId)
        {
            if (!pluginData.Destinations.TryGetValue(destinationId, out var dest)) return false;
            
            if (!string.IsNullOrEmpty(dest.Permission) && !permission.UserHasPermission(player.UserIDString, dest.Permission))
            {
                player.ChatMessage(GetMsg("NoPermission", player.UserIDString));
                return false;
            }
            
            player.Teleport(dest.Position);
            player.StartSleeping();
            SendMsg(player, string.Format(GetMsg("TeleportSuccess", player.UserIDString), dest.Name));
            IncreasePopularity(destinationId);
            return true;
        }

        private void SpawnNPCTerminal(TerminalData terminal)
        {
            var prefab = string.IsNullOrEmpty(terminal.Prefab) ? "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab" : terminal.Prefab;
            var ent = GameManager.server.CreateEntity(prefab, terminal.Position, Quaternion.Euler(terminal.Rotation));
            var npc = ent as BasePlayer;
            if (npc != null)
            {
                npc.enableSaving = false;
                npc.Spawn();
                string termName = string.IsNullOrEmpty(terminal.Name) || terminal.Name == "Transit Terminal" ? "Термінал" : terminal.Name;
                npc.displayName = termName;
                npc.SendNetworkUpdate();
                terminal.NetworkId = npc.net.ID.Value;
                
                // CHANGE: Disable and destroy AI components to prevent NREs from plugins like NpcMovingControl
                npc.CancelInvoke("TryThink");
                var brain = npc.GetComponent<BaseAIBrain>();
                if (brain != null)
                {
                    brain.enabled = false;
                    UnityEngine.Object.Destroy(brain);
                }
                var nav = npc.GetComponent<BaseNavigator>();
                if (nav != null)
                {
                    nav.enabled = false;
                    UnityEngine.Object.Destroy(nav);
                }
                
                npc.health = 10000f; // High health to prevent dying easily
                
                if (!string.IsNullOrEmpty(terminal.Kit) && Kits != null && Kits.IsLoaded)
                {
                    timer.Once(0.5f, () => 
                    {
                        if (npc == null || npc.IsDestroyed) return;
                        
                        // CHANGE: Очищуємо дефолтний інвентар НПС (наприклад, хазмат), щоб новий одяг міг одягнутися
                        npc.inventory.Strip();
                        
                        Kits.Call("GiveKit", npc, terminal.Kit);
                        
                        var npcPlayer = npc as NPCPlayer;
                        if (npcPlayer != null) npcPlayer.EquipWeapon(false);
                        
                        npc.SendNetworkUpdateImmediate();
                    });
                }

                // CHANGE: Додано маркер на ігрову мапу (G) для терміналів (не parent, щоб бачили всі)
                var marker = GameManager.server.CreateEntity("assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab", npc.transform.position, Quaternion.identity);
                if (marker != null)
                {
                    marker.Spawn();
                    terminal.MarkerNetworkId = marker.net.ID.Value;
                    var vMarker = marker as VendingMachineMapMarker;
                    if (vMarker != null)
                    {
                        vMarker.markerShopName = termName;
                        vMarker.SendNetworkUpdate();
                    }
                }
            }
        }
        
        private void KillNPCTerminal(TerminalData terminal)
        {
            var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(terminal.NetworkId)) as BaseEntity;
            if (ent != null) ent.Kill();
            
            if (terminal.MarkerNetworkId != 0)
            {
                var markerEnt = BaseNetworkable.serverEntities.Find(new NetworkableId(terminal.MarkerNetworkId)) as BaseEntity;
                if (markerEnt != null) markerEnt.Kill();
                terminal.MarkerNetworkId = 0;
            }
        }

        // --- Player UI ---

        private const string PlayerUIName = "MonumentTransit.UI";

        private void OpenPlayerUI(BasePlayer player, string terminalId, bool refresh = false)
        {
            if (!refresh)
            {
                CuiHelper.DestroyUi(player, PlayerUIName);
            }
            string contentParent = PlayerUIName + ".Content";
            CuiHelper.DestroyUi(player, contentParent);

            if (!pluginData.Terminals.TryGetValue(terminalId, out var currentTerminal)) return;

            if (!playerUIStates.TryGetValue(player.userID, out var state) || state.TerminalId != terminalId)
            {
                state = new PlayerUIState { TerminalId = terminalId };
                playerUIStates[player.userID] = state;
            }

            var allowedDestinations = pluginData.Destinations.Values.Where(d => 
                (string.IsNullOrEmpty(d.Permission) || permission.UserHasPermission(player.UserIDString, d.Permission)) &&
                (currentTerminal.AllowedDestinations == null || currentTerminal.AllowedDestinations.Count == 0 || currentTerminal.AllowedDestinations.Contains(d.Id))
            ).ToList();

            var categories = allowedDestinations.Select(d => string.IsNullOrEmpty(d.Category) ? "None" : d.Category).Distinct().OrderBy(c => c).ToList();
            if (!categories.Contains(state.SelectedCategory) && categories.Count > 0)
            {
                state.SelectedCategory = categories[0];
            }

            var destsInCategory = allowedDestinations.Where(d => (string.IsNullOrEmpty(d.Category) ? "None" : d.Category) == state.SelectedCategory).ToList();
            
            if (string.IsNullOrEmpty(state.SelectedDestId) || !destsInCategory.Any(d => d.Id == state.SelectedDestId))
            {
                state.SelectedDestId = destsInCategory.FirstOrDefault()?.Id ?? "";
            }

            var elements = new CuiElementContainer();

            if (!refresh)
            {
                elements.Add(new CuiPanel { Image = { Color = "0.1 0.1 0.1 0.95" }, RectTransform = { AnchorMin = "0.1 0.1", AnchorMax = "0.9 0.9" }, CursorEnabled = true }, "Overlay", PlayerUIName);
            }

            elements.Add(new CuiPanel { Image = { Color = "0 0 0 0" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" } }, PlayerUIName, contentParent);

            elements.Add(new CuiElement { Parent = contentParent, Components = { new CuiTextComponent { Text = GetMsg("MapTitle", player.UserIDString), FontSize = 12, Align = TextAnchor.UpperLeft, Color = "0.8 0.7 0.5 1" }, new CuiRectTransformComponent { AnchorMin = "0.02 0.94", AnchorMax = "0.98 0.98" } } });
            elements.Add(new CuiElement { Parent = contentParent, Components = { new CuiTextComponent { Text = GetMsg("MapSubtitle", player.UserIDString), FontSize = 22, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" }, new CuiRectTransformComponent { AnchorMin = "0.02 0.86", AnchorMax = "0.98 0.93" } } });
            elements.Add(new CuiButton { Button = { Command = "monumenttransit.closeui", Color = "0.8 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.96 0.94", AnchorMax = "0.99 0.98" }, Text = { Text = "X", FontSize = 16, Align = TextAnchor.MiddleCenter } }, contentParent);

            if (permission.UserHasPermission(player.UserIDString, "monumenttransit.admin"))
            {
                elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.editterminal {terminalId}", Color = "0.8 0.5 0.2 0.8" }, RectTransform = { AnchorMin = "0.8 0.94", AnchorMax = "0.95 0.98" }, Text = { Text = "Settings", FontSize = 14, Align = TextAnchor.MiddleCenter } }, contentParent);
            }

            bool hideCategories = categories.Count == 0 || (categories.Count == 1 && categories[0].Equals("None", StringComparison.OrdinalIgnoreCase));
            float sectionStartY = 0.82f;
            float categoriesEnd = sectionStartY;

            if (!hideCategories)
            {
                elements.Add(new CuiElement { Parent = contentParent, Components = { new CuiTextComponent { Text = GetMsg("UI_Sections", player.UserIDString), FontSize = 12, Align = TextAnchor.LowerLeft, Color = "0.6 0.6 0.6 1" }, new CuiRectTransformComponent { AnchorMin = "0.02 0.83", AnchorMax = "0.3 0.86" } } });
                
                for (int i = 0; i < categories.Count; i++)
                {
                    string cat = categories[i];
                    bool isSelected = cat == state.SelectedCategory;
                    string color = isSelected ? "0.4 0.6 0.2 0.3" : "0.2 0.2 0.2 0.5";
                    string boxColor = isSelected ? "0.5 0.8 0.2 0.8" : "0.3 0.3 0.3 0.8";
                    
                    float yMax = sectionStartY - (i * 0.08f);
                    float yMin = yMax - 0.07f;

                    string catPanel = contentParent + $".Cat_{i}";
                    elements.Add(new CuiButton { Button = { Command = $"monumenttransit.ui.category {cat}", Color = color }, RectTransform = { AnchorMin = $"0.02 {yMin}", AnchorMax = $"0.45 {yMax}" }, Text = { Text = "" } }, contentParent, catPanel);
                    
                    elements.Add(new CuiPanel { Image = { Color = boxColor }, RectTransform = { AnchorMin = "0.01 0.1", AnchorMax = "0.08 0.9" } }, catPanel, catPanel + ".Box");
                    elements.Add(new CuiElement { Parent = catPanel + ".Box", Components = { new CuiTextComponent { Text = $"{i+1}", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0 0 0 1" }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" } } });
                    elements.Add(new CuiElement { Parent = catPanel, Components = { new CuiTextComponent { Text = cat, FontSize = 16, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" }, new CuiRectTransformComponent { AnchorMin = "0.1 0", AnchorMax = "1 1" } } });
                }

                categoriesEnd = sectionStartY - (categories.Count * 0.08f);
                if (categoriesEnd < 0.6f) categoriesEnd = 0.6f;
            }

            float destTitleYMax = hideCategories ? 0.86f : categoriesEnd - 0.02f;
            float destTitleYMin = destTitleYMax - 0.03f;

            elements.Add(new CuiElement { Parent = contentParent, Components = { new CuiTextComponent { Text = GetMsg("UI_Destinations", player.UserIDString), FontSize = 12, Align = TextAnchor.LowerLeft, Color = "0.6 0.6 0.6 1" }, new CuiRectTransformComponent { AnchorMin = $"0.02 {destTitleYMin}", AnchorMax = $"0.3 {destTitleYMax}" } } });

            float availableHeight = destTitleYMin - 0.15f;
            int itemsPerPage = Mathf.FloorToInt(availableHeight / 0.08f);
            if (itemsPerPage < 1) itemsPerPage = 1;

            int totalPages = Mathf.CeilToInt((float)destsInCategory.Count / itemsPerPage);
            if (totalPages == 0) totalPages = 1;
            if (state.DestPage >= totalPages) state.DestPage = totalPages - 1;
            if (state.DestPage < 0) state.DestPage = 0;

            if (totalPages > 1)
            {
                string pageTxt = $"{state.DestPage + 1} / {totalPages}";
                elements.Add(new CuiElement { Parent = contentParent, Components = { new CuiTextComponent { Text = pageTxt, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" }, new CuiRectTransformComponent { AnchorMin = $"0.35 {destTitleYMin}", AnchorMax = $"0.43 {destTitleYMax}" } } });

                if (state.DestPage > 0)
                {
                    elements.Add(new CuiButton { Button = { Command = "monumenttransit.ui.destpage -1", Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = $"0.31 {destTitleYMin}", AnchorMax = $"0.35 {destTitleYMax}" }, Text = { Text = "<", FontSize = 12, Align = TextAnchor.MiddleCenter } }, contentParent);
                }
                if (state.DestPage < totalPages - 1)
                {
                    elements.Add(new CuiButton { Button = { Command = "monumenttransit.ui.destpage 1", Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = $"0.43 {destTitleYMin}", AnchorMax = $"0.47 {destTitleYMax}" }, Text = { Text = ">", FontSize = 12, Align = TextAnchor.MiddleCenter } }, contentParent);
                }
            }

            float destStartY = destTitleYMin - 0.01f;
            int startIndex = state.DestPage * itemsPerPage;
            int endIndex = Math.Min(startIndex + itemsPerPage, destsInCategory.Count);

            for (int i = startIndex; i < endIndex; i++)
            {
                var dest = destsInCategory[i];
                int displayIndex = i - startIndex;
                bool isSelected = dest.Id == state.SelectedDestId;
                string color = isSelected ? "0.4 0.6 0.2 0.3" : "0.2 0.2 0.2 0.5";
                string boxColor = isSelected ? "0.5 0.8 0.2 0.8" : "0.3 0.3 0.3 0.8";
                
                float price = GetFinalPrice(player, dest.Id, terminalId);
                string priceTxt = $"{Math.Ceiling(price)} {GetCurrencyName()}";

                float yMax = destStartY - (displayIndex * 0.08f);
                float yMin = yMax - 0.07f;
                if (yMin < 0.15f) break;

                string destPanel = contentParent + $".Dest_{i}";
                elements.Add(new CuiButton { Button = { Command = $"monumenttransit.ui.select {dest.Id}", Color = color }, RectTransform = { AnchorMin = $"0.02 {yMin}", AnchorMax = $"0.45 {yMax}" }, Text = { Text = "" } }, contentParent, destPanel);
                
                elements.Add(new CuiPanel { Image = { Color = boxColor }, RectTransform = { AnchorMin = "0.01 0.1", AnchorMax = "0.08 0.9" } }, destPanel, destPanel + ".Box");
                elements.Add(new CuiElement { Parent = destPanel + ".Box", Components = { new CuiTextComponent { Text = $"{i+1}", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0 0 0 1" }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" } } });
                elements.Add(new CuiElement { Parent = destPanel, Components = { new CuiTextComponent { Text = dest.Name, FontSize = 16, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" }, new CuiRectTransformComponent { AnchorMin = "0.1 0", AnchorMax = "0.7 1" } } });
                elements.Add(new CuiElement { Parent = destPanel, Components = { new CuiTextComponent { Text = priceTxt, FontSize = 14, Align = TextAnchor.MiddleRight, Color = "0.8 0.8 0.8 1" }, new CuiRectTransformComponent { AnchorMin = "0.7 0", AnchorMax = "0.98 1" } } });
            }

            if (state.IsTeleporting)
            {
                elements.Add(new CuiPanel { Image = { Color = "0 0 0 0" }, RectTransform = { AnchorMin = "0.02 0.02", AnchorMax = "0.45 0.12" } }, contentParent, contentParent + ".Status");
                elements.Add(new CuiElement { Parent = contentParent + ".Status", Components = { new CuiTextComponent { Text = GetMsg("UI_Teleporting", player.UserIDString), FontSize = 18, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" } } });

                elements.Add(new CuiButton { Button = { Command = "monumenttransit.ui.cancel", Color = "0.8 0.3 0.2 0.9" }, RectTransform = { AnchorMin = "0.75 0.02", AnchorMax = "0.98 0.12" }, Text = { Text = GetMsg("UI_CancelButton", player.UserIDString), FontSize = 20, Align = TextAnchor.MiddleCenter } }, contentParent);
            }
            else
            {
                if (!string.IsNullOrEmpty(state.SelectedDestId))
                {
                    float price = GetFinalPrice(player, state.SelectedDestId, terminalId);
                    elements.Add(new CuiElement { Parent = contentParent, Components = { new CuiTextComponent { Text = GetMsg("UI_Cost", player.UserIDString), FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.6 0.6 0.6 1" }, new CuiRectTransformComponent { AnchorMin = "0.02 0.02", AnchorMax = "0.09 0.12" } } });
                    elements.Add(new CuiPanel { Image = { Color = "0.1 0.1 0.1 0.8" }, RectTransform = { AnchorMin = "0.10 0.02", AnchorMax = "0.20 0.12" } }, contentParent, contentParent + ".CostBox");
                    elements.Add(new CuiElement { Parent = contentParent + ".CostBox", Components = { new CuiTextComponent { Text = Math.Ceiling(price).ToString(), FontSize = 22, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" } } });
                    elements.Add(new CuiElement { Parent = contentParent, Components = { new CuiTextComponent { Text = GetCurrencyName(), FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "0.6 0.6 0.6 1" }, new CuiRectTransformComponent { AnchorMin = "0.22 0.02", AnchorMax = "0.45 0.12" } } });

                    elements.Add(new CuiButton { Button = { Command = "monumenttransit.pay", Color = "0.4 0.6 0.2 0.9" }, RectTransform = { AnchorMin = "0.75 0.02", AnchorMax = "0.98 0.12" }, Text = { Text = GetMsg("ButtonTeleport", player.UserIDString), FontSize = 20, Align = TextAnchor.MiddleCenter } }, contentParent);
                }
            }

            string mapParent = contentParent + ".Map";
            if (!string.IsNullOrEmpty(config.MapImageUrl) || !string.IsNullOrEmpty(autoMapImageId))
            {
                var rawImage = new CuiRawImageComponent { Color = "1 1 1 1" };
                if (!string.IsNullOrEmpty(config.MapImageUrl)) rawImage.Url = config.MapImageUrl;
                else rawImage.Png = autoMapImageId;
                elements.Add(new CuiElement { Parent = contentParent, Name = mapParent, Components = { rawImage, new CuiRectTransformComponent { AnchorMin = "0.48 0.15", AnchorMax = "0.98 0.88" } } });
            }
            else
            {
                elements.Add(new CuiPanel { Image = { Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.48 0.15", AnchorMax = "0.98 0.88" } }, contentParent, mapParent);
            }

            float mapSize = TerrainMeta.Size != null ? TerrainMeta.Size.x : 4000f;
            float halfMap = mapSize / 2f;
            foreach (var dest in destsInCategory)
            {
                float relX = (dest.Position.x + halfMap) / mapSize;
                float relZ = (dest.Position.z + halfMap) / mapSize;

                float sizeX = config.MarkerSize / 600f;
                float sizeY = config.MarkerSize / 600f;

                bool isSelected = dest.Id == state.SelectedDestId;
                string markerColor = isSelected ? "0.5 0.8 0.2 1" : "0.4 0.6 0.2 0.6"; // Bright green if selected, faded green if not
                string iconColor = isSelected ? "1 1 1 1" : "1 1 1 0.6";

                string markerName = mapParent + $".Marker_{dest.Id}";

                // Background Circle (Clickable)
                elements.Add(new CuiButton { Button = { Command = $"monumenttransit.ui.select {dest.Id}", Color = markerColor, Sprite = "assets/icons/circle_closed.png" }, RectTransform = { AnchorMin = $"{relX - sizeX} {relZ - sizeY}", AnchorMax = $"{relX + sizeX} {relZ + sizeY}" }, Text = { Text = "", FontSize = 10 } }, mapParent, markerName);

                // Inner Icon (Check/Shield)
                elements.Add(new CuiElement { Parent = markerName, Components = { new CuiImageComponent { Color = iconColor, Sprite = "assets/icons/check.png" }, new CuiRectTransformComponent { AnchorMin = "0.2 0.2", AnchorMax = "0.8 0.8" } } });

                // CHANGE: Додано підпис точки призначення на UI мапі
                elements.Add(new CuiElement { Parent = mapParent, Components = { new CuiTextComponent { Text = dest.Name, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }, new CuiRectTransformComponent { AnchorMin = $"{relX - 0.1f} {relZ + sizeY}", AnchorMax = $"{relX + 0.1f} {relZ + sizeY + 0.04f}" }, new CuiOutlineComponent { Distance = "1 -1", Color = "0 0 0 1" } } });
            }

            // CHANGE: Додано назви монументів на UI мапу
            foreach (var mon in cachedMonuments.Values)
            {
                float relX = (mon.Position.x + halfMap) / mapSize;
                float relZ = (mon.Position.z + halfMap) / mapSize;
                
                elements.Add(new CuiElement { Parent = mapParent, Components = { new CuiTextComponent { Text = mon.Name, FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 0.8" }, new CuiRectTransformComponent { AnchorMin = $"{relX - 0.1f} {relZ - 0.02f}", AnchorMax = $"{relX + 0.1f} {relZ + 0.02f}" }, new CuiOutlineComponent { Distance = "1 -1", Color = "0 0 0 0.8" } } });
            }

            CuiHelper.AddUi(player, elements);
        }

        [ConsoleCommand("monumenttransit.closeui")]
        private void CmdClosePlayerUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, PlayerUIName);
        }

        [ConsoleCommand("monumenttransit.ui.category")]
        private void CmdUICategory(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length == 0) return;
            string category = string.Join(" ", arg.Args.Select(x => x.ToString()));
            
            if (playerUIStates.TryGetValue(player.userID, out var state))
            {
                if (state.SelectedCategory != category)
                {
                    state.SelectedCategory = category;
                    state.SelectedDestId = "";
                    state.DestPage = 0;
                }
                OpenPlayerUI(player, state.TerminalId, true);
            }
        }

        [ConsoleCommand("monumenttransit.ui.destpage")]
        private void CmdDestPage(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length < 1) return;
            
            if (playerUIStates.TryGetValue(player.userID, out var state))
            {
                if (int.TryParse(arg.Args[0].ToString(), out int change))
                {
                    state.DestPage += change;
                    OpenPlayerUI(player, state.TerminalId, true);
                }
            }
        }

        [ConsoleCommand("monumenttransit.ui.select")]
        private void CmdUISelect(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || arg.Args == null || arg.Args.Length == 0) return;
            
            if (playerUIStates.TryGetValue(player.userID, out var state))
            {
                state.SelectedDestId = arg.Args[0].ToString();
                OpenPlayerUI(player, state.TerminalId, true);
            }
        }

        [ConsoleCommand("monumenttransit.ui.cancel")]
        private void CmdUICancel(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            if (playerUIStates.TryGetValue(player.userID, out var state))
            {
                if (state.IsTeleporting)
                {
                    CancelTeleport(player.userID);
                    SendMsg(player, GetMsg("TeleportCancelled", player.UserIDString));
                    state.IsTeleporting = false;
                    OpenPlayerUI(player, state.TerminalId, true);
                }
            }
        }

        private Dictionary<ulong, Timer> activeTeleports = new Dictionary<ulong, Timer>();

        [ConsoleCommand("monumenttransit.pay")]
        private void CmdPayTeleport(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            if (!playerUIStates.TryGetValue(player.userID, out var state) || string.IsNullOrEmpty(state.SelectedDestId)) return;

            string destinationId = state.SelectedDestId;
            string terminalId = state.TerminalId;

            if (activeTeleports.ContainsKey(player.userID) || state.IsTeleporting)
            {
                SendMsg(player, GetMsg("AlreadyTeleporting", player.UserIDString));
                CuiHelper.DestroyUi(player, PlayerUIName);
                return;
            }

            PermSetting permSetting = GetPlayerPermSetting(player);
            if (!CheckLimit(player, permSetting))
            {
                int used = pluginData.PlayerUsage.ContainsKey(player.userID) ? pluginData.PlayerUsage[player.userID].TeleportsUsed : 0;
                SendMsg(player, string.Format(GetMsg("LimitReached", player.UserIDString), used, permSetting.DailyLimit));
                CuiHelper.DestroyUi(player, PlayerUIName);
                return;
            }

            float price = GetFinalPrice(player, destinationId, terminalId);

            if (!HasFunds(player, price))
            {
                SendMsg(player, string.Format(GetMsg("NotEnoughFunds", player.UserIDString), Math.Ceiling(price) + " " + GetCurrencyName()));
                CuiHelper.DestroyUi(player, PlayerUIName);
                return;
            }

            state.IsTeleporting = true;
            CuiHelper.DestroyUi(player, PlayerUIName);

            StartTeleportProcess(player, destinationId, terminalId, price, permSetting, state);
        }

        private PermSetting GetPlayerPermSetting(BasePlayer player)
        {
            PermSetting bestSetting = null;
            if (config.PermissionSettings != null)
            {
                foreach (var kvp in config.PermissionSettings)
                {
                    if (kvp.Key == "default" || permission.UserHasPermission(player.UserIDString, kvp.Key))
                    {
                        if (bestSetting == null || kvp.Value.Priority > bestSetting.Priority)
                        {
                            bestSetting = kvp.Value;
                        }
                    }
                }
            }
            return bestSetting ?? new PermSetting { DailyLimit = 10, TeleportDelay = 15f };
        }

        private bool CheckLimit(BasePlayer player, PermSetting permSetting)
        {
            if (permSetting.DailyLimit < 0) return true; // -1 for unlimited

            if (!pluginData.PlayerUsage.TryGetValue(player.userID, out var usage))
            {
                usage = new PlayerUsageData();
                pluginData.PlayerUsage[player.userID] = usage;
            }

            if (config.ResetLimitsDaily)
            {
                string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                if (usage.LastResetDate != today)
                {
                    usage.TeleportsUsed = 0;
                    usage.LastResetDate = today;
                }
            }

            return usage.TeleportsUsed < permSetting.DailyLimit;
        }

        private void StartTeleportProcess(BasePlayer player, string destinationId, string terminalId, float price, PermSetting permSetting, PlayerUIState state)
        {
            Vector3 startPos = player.transform.position;
            float timeRemaining = permSetting.TeleportDelay;

            if (timeRemaining <= 0)
            {
                state.IsTeleporting = false;
                ExecuteTeleport(player, destinationId, price);
                return;
            }

            SendMsg(player, string.Format(GetMsg("TeleportingIn", player.UserIDString), timeRemaining));

            activeTeleports[player.userID] = timer.Repeat(1f, (int)timeRemaining + 1, () =>
            {
                if (player == null || !player.IsConnected)
                {
                    CancelTeleport(player?.userID ?? 0);
                    return;
                }

                if (timeRemaining <= 0)
                {
                    state.IsTeleporting = false;
                    ExecuteTeleport(player, destinationId, price);
                    CancelTeleport(player.userID);
                    return;
                }

                if (CheckTeleportCancellation(player, startPos))
                {
                    SendMsg(player, GetMsg("TeleportCancelled", player.UserIDString));
                    CancelTeleport(player.userID);
                    return;
                }

                timeRemaining--;
            });
        }

        private void ExecuteTeleport(BasePlayer player, string destinationId, float price)
        {
            if (WithdrawFunds(player, price))
            {
                if (pluginData.PlayerUsage.TryGetValue(player.userID, out var usage))
                {
                    usage.TeleportsUsed++;
                    SaveDataAsync();
                }
                
                string destName = pluginData.Destinations.TryGetValue(destinationId, out var d) ? d.Name : destinationId;
                LogAction("teleports", $"Player {player.displayName} ({player.userID}) teleported to {destName}. Paid: {price} {GetCurrencyName()}");
                
                ProcessTeleport(player, destinationId);
            }
            else
            {
                SendMsg(player, string.Format(GetMsg("NotEnoughFunds", player.UserIDString), price + " " + GetCurrencyName()));
            }
        }

        private void CancelTeleport(ulong userId)
        {
            if (activeTeleports.TryGetValue(userId, out var t))
            {
                t?.Destroy();
                activeTeleports.Remove(userId);
            }
            if (playerUIStates.TryGetValue(userId, out var state))
            {
                if (state.IsTeleporting)
                {
                    state.IsTeleporting = false;
                    var p = BasePlayer.FindByID(userId) ?? BasePlayer.FindAwakeOrSleeping(userId.ToString());
                    if (p != null && p.IsConnected)
                    {
                        OpenPlayerUI(p, state.TerminalId, true);
                    }
                }
            }
        }

        private bool CheckTeleportCancellation(BasePlayer player, Vector3 startPos)
        {
            if (config.TeleportCancellation.CancelMovementRadius > 0 && Vector3.Distance(player.transform.position, startPos) > config.TeleportCancellation.CancelMovementRadius) return true;
            if (config.TeleportCancellation.CancelIfBleeding && player.metabolism.bleeding.value > 0) return true;
            if (config.TeleportCancellation.CancelIfFreezing && player.metabolism.temperature.value < 5f) return true;
            if (config.TeleportCancellation.CancelIfStarving && (player.metabolism.calories.value < 20f || player.metabolism.hydration.value < 20f)) return true;
            return false;
        }

        // --- Admin UI ---

        private const string AdminUIName = "MonumentTransit.AdminUI";

        private void OpenAdminUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, AdminUIName);
            var elements = new CuiElementContainer();

            elements.Add(new CuiPanel { Image = { Color = "0.1 0.1 0.1 0.95" }, RectTransform = { AnchorMin = "0.1 0.1", AnchorMax = "0.9 0.9" }, CursorEnabled = true }, "Overlay", AdminUIName);
            elements.Add(new CuiElement { Parent = AdminUIName, Components = { new CuiTextComponent { Text = GetMsg("AdminMenuTitle", player.UserIDString), FontSize = 24, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }, new CuiRectTransformComponent { AnchorMin = "0 0.95", AnchorMax = "1 1" } } });
            elements.Add(new CuiButton { Button = { Command = "monumenttransit.admin.close", Color = "0.8 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.96 0.96", AnchorMax = "0.99 0.99" }, Text = { Text = "X", FontSize = 18, Align = TextAnchor.MiddleCenter } }, AdminUIName);

            elements.Add(new CuiButton { Button = { Command = "monumenttransit.admin.createnpc", Color = "0.2 0.6 0.2 0.8" }, RectTransform = { AnchorMin = "0.05 0.88", AnchorMax = "0.3 0.94" }, Text = { Text = "Create NPC Terminal", FontSize = 16, Align = TextAnchor.MiddleCenter } }, AdminUIName);
            elements.Add(new CuiButton { Button = { Command = "monumenttransit.admin.createprop", Color = "0.2 0.6 0.2 0.8" }, RectTransform = { AnchorMin = "0.35 0.88", AnchorMax = "0.6 0.94" }, Text = { Text = "Create Prop Terminal", FontSize = 16, Align = TextAnchor.MiddleCenter } }, AdminUIName);
            elements.Add(new CuiButton { Button = { Command = "monumenttransit.admin.createdest", Color = "0.2 0.4 0.8 0.8" }, RectTransform = { AnchorMin = "0.65 0.88", AnchorMax = "0.95 0.94" }, Text = { Text = "Create Destination Here", FontSize = 16, Align = TextAnchor.MiddleCenter } }, AdminUIName);

            if (!playerUIStates.TryGetValue(player.userID, out var state))
            {
                state = new PlayerUIState();
                playerUIStates[player.userID] = state;
            }

            elements.Add(new CuiLabel { Text = { Text = "TERMINALS", FontSize = 18, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.05 0.8", AnchorMax = "0.25 0.85" } }, AdminUIName);
            
            var termList = pluginData.Terminals.Values.ToList();
            int mainItemsPerPage = 14; 
            int termPages = Mathf.CeilToInt((float)termList.Count / mainItemsPerPage);
            if (termPages == 0) termPages = 1;
            if (state.AdminMainTermPage >= termPages) state.AdminMainTermPage = termPages - 1;
            if (state.AdminMainTermPage < 0) state.AdminMainTermPage = 0;
            
            if (termPages > 1)
            {
                elements.Add(new CuiElement { Parent = AdminUIName, Components = { new CuiTextComponent { Text = $"{state.AdminMainTermPage + 1}/{termPages}", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" }, new CuiRectTransformComponent { AnchorMin = "0.3 0.8", AnchorMax = "0.38 0.85" } } });
                if (state.AdminMainTermPage > 0) elements.Add(new CuiButton { Button = { Command = "monumenttransit.admin.page term -1", Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.26 0.8", AnchorMax = "0.29 0.85" }, Text = { Text = "<", FontSize = 12, Align = TextAnchor.MiddleCenter } }, AdminUIName);
                if (state.AdminMainTermPage < termPages - 1) elements.Add(new CuiButton { Button = { Command = "monumenttransit.admin.page term 1", Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.39 0.8", AnchorMax = "0.42 0.85" }, Text = { Text = ">", FontSize = 12, Align = TextAnchor.MiddleCenter } }, AdminUIName);
            }

            int startTerm = state.AdminMainTermPage * mainItemsPerPage;
            int endTerm = Math.Min(startTerm + mainItemsPerPage, termList.Count);

            for (int i = startTerm; i < endTerm; i++)
            {
                var term = termList[i];
                int displayIndex = i - startTerm;
                float yMax = 0.78f - (displayIndex * 0.05f);
                float yMin = yMax - 0.04f;
                
                string bg = AdminUIName + $".TermBg_{i}";
                elements.Add(new CuiPanel { Image = { Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = $"0.05 {yMin}", AnchorMax = $"0.45 {yMax}" } }, AdminUIName, bg);
                elements.Add(new CuiLabel { Text = { Text = $"{term.Name} ({term.Type})", FontSize = 12, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.49 1" } }, bg);
                
                elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.tpterminal {term.Id}", Color = "0.2 0.6 0.2 0.8" }, RectTransform = { AnchorMin = "0.51 0.1", AnchorMax = "0.66 0.9" }, Text = { Text = "TP", FontSize = 12, Align = TextAnchor.MiddleCenter } }, bg);
                elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.editterminal {term.Id}", Color = "0.2 0.5 0.8 0.8" }, RectTransform = { AnchorMin = "0.68 0.1", AnchorMax = "0.83 0.9" }, Text = { Text = "EDIT", FontSize = 12, Align = TextAnchor.MiddleCenter } }, bg);
                elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.globaldeleteterminal {term.Id}", Color = "0.8 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.85 0.1", AnchorMax = "0.98 0.9" }, Text = { Text = "DEL", FontSize = 12, Align = TextAnchor.MiddleCenter } }, bg);
            }

            elements.Add(new CuiLabel { Text = { Text = "DESTINATIONS", FontSize = 18, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.55 0.8", AnchorMax = "0.75 0.85" } }, AdminUIName);

            var destList = pluginData.Destinations.Values.ToList();
            int destPages = Mathf.CeilToInt((float)destList.Count / mainItemsPerPage);
            if (destPages == 0) destPages = 1;
            if (state.AdminMainDestPage >= destPages) state.AdminMainDestPage = destPages - 1;
            if (state.AdminMainDestPage < 0) state.AdminMainDestPage = 0;
            
            if (destPages > 1)
            {
                elements.Add(new CuiElement { Parent = AdminUIName, Components = { new CuiTextComponent { Text = $"{state.AdminMainDestPage + 1}/{destPages}", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" }, new CuiRectTransformComponent { AnchorMin = "0.8 0.8", AnchorMax = "0.88 0.85" } } });
                if (state.AdminMainDestPage > 0) elements.Add(new CuiButton { Button = { Command = "monumenttransit.admin.page dest -1", Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.76 0.8", AnchorMax = "0.79 0.85" }, Text = { Text = "<", FontSize = 12, Align = TextAnchor.MiddleCenter } }, AdminUIName);
                if (state.AdminMainDestPage < destPages - 1) elements.Add(new CuiButton { Button = { Command = "monumenttransit.admin.page dest 1", Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.89 0.8", AnchorMax = "0.92 0.85" }, Text = { Text = ">", FontSize = 12, Align = TextAnchor.MiddleCenter } }, AdminUIName);
            }

            int startDest = state.AdminMainDestPage * mainItemsPerPage;
            int endDest = Math.Min(startDest + mainItemsPerPage, destList.Count);

            for (int j = startDest; j < endDest; j++)
            {
                var dest = destList[j];
                int displayIndex = j - startDest;
                float yMax = 0.78f - (displayIndex * 0.05f);
                float yMin = yMax - 0.04f;
                
                string bg = AdminUIName + $".DestBg_{j}";
                elements.Add(new CuiPanel { Image = { Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = $"0.55 {yMin}", AnchorMax = $"0.95 {yMax}" } }, AdminUIName, bg);
                elements.Add(new CuiLabel { Text = { Text = $"{dest.Name} ({dest.Category})", FontSize = 12, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.49 1" } }, bg);
                
                elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.tpdest {dest.Id}", Color = "0.2 0.6 0.2 0.8" }, RectTransform = { AnchorMin = "0.51 0.1", AnchorMax = "0.66 0.9" }, Text = { Text = "TP", FontSize = 12, Align = TextAnchor.MiddleCenter } }, bg);
                elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.editdest {dest.Id}", Color = "0.2 0.5 0.8 0.8" }, RectTransform = { AnchorMin = "0.68 0.1", AnchorMax = "0.83 0.9" }, Text = { Text = "EDIT", FontSize = 12, Align = TextAnchor.MiddleCenter } }, bg);
                elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.deletedest {dest.Id}", Color = "0.8 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.85 0.1", AnchorMax = "0.98 0.9" }, Text = { Text = "DEL", FontSize = 12, Align = TextAnchor.MiddleCenter } }, bg);
            }

            CuiHelper.AddUi(player, elements);
        }

        [ConsoleCommand("monumenttransit.admin.close")]
        private void CmdCloseAdminUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, AdminUIName);
        }
        
        [ConsoleCommand("monumenttransit.admin.createnpc")]
        private void CmdAdminCreateNpc(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin")) return;
            API_CreateTerminal(player.transform.position, player.eyes.rotation.eulerAngles, "NPC");
            LogAction("admin", $"Admin {player.displayName} ({player.userID}) created an NPC terminal.");
            SendMsg(player, GetMsg("TerminalCreated", player.UserIDString));
            OpenAdminUI(player);
        }

        [ConsoleCommand("monumenttransit.admin.createprop")]
        private void CmdAdminCreateProp(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin")) return;
            API_CreateTerminal(player.transform.position, player.eyes.rotation.eulerAngles, "Prop");
            LogAction("admin", $"Admin {player.displayName} ({player.userID}) created a Prop terminal.");
            SendMsg(player, GetMsg("TerminalCreated", player.UserIDString));
            OpenAdminUI(player);
        }

        [ConsoleCommand("monumenttransit.admin.createdest")]
        private void CmdAdminCreateDest(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin")) return;
            string name = "Dest_" + UnityEngine.Random.Range(1000, 9999);
            API_CreateDestination(name, player.transform.position);
            LogAction("admin", $"Admin {player.displayName} ({player.userID}) created destination {name}.");
            SendMsg(player, string.Format(GetMsg("DestinationCreated", player.UserIDString), name));
            OpenAdminUI(player);
        }

        [ConsoleCommand("monumenttransit.admin.globaldeleteterminal")]
        private void CmdGlobalDeleteTerminal(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length == 0) return;
            string termId = arg.Args[0].ToString();
            
            LogAction("admin", $"Admin {player.displayName} ({player.userID}) deleted terminal {termId}.");
            API_RemoveTerminal(termId);
            SendMsg(player, GetMsg("TerminalDeleted", player.UserIDString));
            OpenAdminUI(player);
        }

        [ConsoleCommand("monumenttransit.admin.deletedest")]
        private void CmdDeleteDest(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length == 0) return;
            string destId = arg.Args[0].ToString();
            
            LogAction("admin", $"Admin {player.displayName} ({player.userID}) deleted destination {destId}.");
            if (pluginData.Destinations.Remove(destId))
            {
                foreach(var term in pluginData.Terminals.Values)
                {
                    if (term.AllowedDestinations != null) term.AllowedDestinations.Remove(destId);
                }
                SaveDataAsync();
                SendMsg(player, string.Format(GetMsg("DestinationDeleted", player.UserIDString), destId));
                OpenAdminUI(player);
            }
        }

        [ConsoleCommand("monumenttransit.admin.tpterminal")]
        private void CmdTpTerminal(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length == 0) return;
            string termId = arg.Args[0].ToString();
            
            if (pluginData.Terminals.TryGetValue(termId, out var term))
            {
                player.Teleport(term.Position);
                CuiHelper.DestroyUi(player, AdminUIName);
                SendMsg(player, "Teleported to terminal: " + term.Name);
            }
        }

        [ConsoleCommand("monumenttransit.admin.tpdest")]
        private void CmdTpDest(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length == 0) return;
            string destId = arg.Args[0].ToString();
            
            if (pluginData.Destinations.TryGetValue(destId, out var dest))
            {
                player.Teleport(dest.Position);
                CuiHelper.DestroyUi(player, AdminUIName);
                SendMsg(player, "Teleported to destination: " + dest.Name);
            }
        }

        // --- Terminal Settings UI ---
        private const string TerminalAdminUIName = "MonumentTransit.TerminalAdminUI";

        [ConsoleCommand("monumenttransit.admin.editterminal")]
        private void CmdEditTerminal(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length == 0) return;
            OpenTerminalAdminUI(player, arg.Args[0].ToString());
        }

        private void OpenTerminalAdminUI(BasePlayer player, string terminalId)
        {
            CuiHelper.DestroyUi(player, TerminalAdminUIName);
            if (!pluginData.Terminals.TryGetValue(terminalId, out var terminal)) return;

            var elements = new CuiElementContainer();
            elements.Add(new CuiPanel { Image = { Color = "0.1 0.1 0.1 0.95" }, RectTransform = { AnchorMin = "0.3 0.2", AnchorMax = "0.7 0.8" }, CursorEnabled = true }, "Overlay", TerminalAdminUIName);
            
            elements.Add(new CuiElement { Parent = TerminalAdminUIName, Components = { new CuiTextComponent { Text = "TERMINAL SETTINGS", FontSize = 20, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }, new CuiRectTransformComponent { AnchorMin = "0 0.9", AnchorMax = "1 1" } } });
            elements.Add(new CuiButton { Button = { Command = "monumenttransit.admin.closeterminalui", Color = "0.8 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.9 0.92", AnchorMax = "0.98 0.98" }, Text = { Text = "X", FontSize = 18, Align = TextAnchor.MiddleCenter } }, TerminalAdminUIName);

            elements.Add(new CuiLabel { Text = { Text = "Name:", FontSize = 14, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.05 0.8", AnchorMax = "0.3 0.88" } }, TerminalAdminUIName);
            elements.Add(new CuiPanel { Image = { Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.3 0.8", AnchorMax = "0.7 0.88" } }, TerminalAdminUIName, TerminalAdminUIName + ".NameBg");
            elements.Add(new CuiElement { Parent = TerminalAdminUIName + ".NameBg", Components = { new CuiInputFieldComponent { Text = terminal.Name, Command = $"monumenttransit.admin.setname {terminalId}", FontSize = 14, Align = TextAnchor.MiddleLeft }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" } } });
            elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.setname {terminalId} ", Color = "0.2 0.6 0.2 0.8" }, RectTransform = { AnchorMin = "0.75 0.8", AnchorMax = "0.95 0.88" }, Text = { Text = "SAVE (Press Enter)", FontSize = 12, Align = TextAnchor.MiddleCenter } }, TerminalAdminUIName);

            elements.Add(new CuiLabel { Text = { Text = "Kit Name:", FontSize = 14, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.05 0.7", AnchorMax = "0.3 0.78" } }, TerminalAdminUIName);
            elements.Add(new CuiPanel { Image = { Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.3 0.7", AnchorMax = "0.7 0.78" } }, TerminalAdminUIName, TerminalAdminUIName + ".KitBg");
            elements.Add(new CuiElement { Parent = TerminalAdminUIName + ".KitBg", Components = { new CuiInputFieldComponent { Text = terminal.Kit, Command = $"monumenttransit.admin.setkit {terminalId}", FontSize = 14, Align = TextAnchor.MiddleLeft }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" } } });

            elements.Add(new CuiLabel { Text = { Text = "Allowed Destinations:", FontSize = 14, Align = TextAnchor.MiddleCenter }, RectTransform = { AnchorMin = "0.05 0.6", AnchorMax = "0.95 0.68" } }, TerminalAdminUIName);
            
            if (!playerUIStates.TryGetValue(player.userID, out var state))
            {
                state = new PlayerUIState { TerminalId = terminalId };
                playerUIStates[player.userID] = state;
            }

            var destList = pluginData.Destinations.Values.ToList();
            int termItemsPerPage = 5; 
            int termPages = Mathf.CeilToInt((float)destList.Count / termItemsPerPage);
            if (termPages == 0) termPages = 1;
            if (state.AdminTermDestPage >= termPages) state.AdminTermDestPage = termPages - 1;
            if (state.AdminTermDestPage < 0) state.AdminTermDestPage = 0;

            if (termPages > 1)
            {
                string pageTxt = $"{state.AdminTermDestPage + 1} / {termPages}";
                elements.Add(new CuiElement { Parent = TerminalAdminUIName, Components = { new CuiTextComponent { Text = pageTxt, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" }, new CuiRectTransformComponent { AnchorMin = "0.7 0.6", AnchorMax = "0.8 0.68" } } });

                if (state.AdminTermDestPage > 0)
                {
                    elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.page termdest -1 {terminalId}", Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.65 0.6", AnchorMax = "0.69 0.68" }, Text = { Text = "<", FontSize = 12, Align = TextAnchor.MiddleCenter } }, TerminalAdminUIName);
                }
                if (state.AdminTermDestPage < termPages - 1)
                {
                    elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.page termdest 1 {terminalId}", Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.81 0.6", AnchorMax = "0.85 0.68" }, Text = { Text = ">", FontSize = 12, Align = TextAnchor.MiddleCenter } }, TerminalAdminUIName);
                }
            }

            int startDest = state.AdminTermDestPage * termItemsPerPage;
            int endDest = Math.Min(startDest + termItemsPerPage, destList.Count);

            for (int i = startDest; i < endDest; i++)
            {
                var dest = destList[i];
                int displayIndex = i - startDest;
                bool allowed = terminal.AllowedDestinations == null || terminal.AllowedDestinations.Count == 0 || terminal.AllowedDestinations.Contains(dest.Id);
                string color = allowed ? "0.2 0.8 0.2 0.8" : "0.8 0.2 0.2 0.8";

                float yMax = 0.58f - (displayIndex * 0.08f);
                float yMin = yMax - 0.07f;

                // Toggle Button (ON/OFF)
                elements.Add(new CuiButton
                {
                    Button = { Command = $"monumenttransit.admin.toggledest {terminalId} {dest.Id}", Color = color },
                    RectTransform = { AnchorMin = $"0.05 {yMin}", AnchorMax = $"0.25 {yMax}" },
                    Text = { Text = allowed ? "ON" : "OFF", FontSize = 14, Align = TextAnchor.MiddleCenter }
                }, TerminalAdminUIName);

                // Input Field for Destination Name
                string bgName = TerminalAdminUIName + $".DestBg_{i}";
                elements.Add(new CuiPanel { Image = { Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = $"0.27 {yMin}", AnchorMax = $"0.51 {yMax}" } }, TerminalAdminUIName, bgName);
                elements.Add(new CuiElement { Parent = bgName, Components = { new CuiInputFieldComponent { Text = dest.Name, Command = $"monumenttransit.admin.uisetdestname {terminalId} {dest.Id}", FontSize = 14, Align = TextAnchor.MiddleLeft }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" } } });

                // Cycle Button for Category
                elements.Add(new CuiButton
                {
                    Button = { Command = $"monumenttransit.admin.cyclecategory {terminalId} {dest.Id}", Color = "0.2 0.4 0.6 0.8" },
                    RectTransform = { AnchorMin = $"0.53 {yMin}", AnchorMax = $"0.75 {yMax}" },
                    Text = { Text = dest.Category, FontSize = 14, Align = TextAnchor.MiddleCenter }
                }, TerminalAdminUIName);

                // EDIT Button
                elements.Add(new CuiButton
                {
                    Button = { Command = $"monumenttransit.admin.editdest {dest.Id} {terminalId}", Color = "0.2 0.5 0.8 0.8" },
                    RectTransform = { AnchorMin = $"0.77 {yMin}", AnchorMax = $"0.95 {yMax}" },
                    Text = { Text = "EDIT", FontSize = 14, Align = TextAnchor.MiddleCenter }
                }, TerminalAdminUIName);
            }

            elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.deleteterminal {terminalId}", Color = "0.8 0.1 0.1 0.8" }, RectTransform = { AnchorMin = "0.05 0.02", AnchorMax = "0.45 0.08" }, Text = { Text = "DELETE TERMINAL", FontSize = 14, Align = TextAnchor.MiddleCenter } }, TerminalAdminUIName);
            elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.categoryui {terminalId}", Color = "0.2 0.5 0.8 0.8" }, RectTransform = { AnchorMin = "0.55 0.02", AnchorMax = "0.95 0.08" }, Text = { Text = "MANAGE CATEGORIES", FontSize = 14, Align = TextAnchor.MiddleCenter } }, TerminalAdminUIName);

            CuiHelper.AddUi(player, elements);
        }

        [ConsoleCommand("monumenttransit.admin.closeterminalui")]
        private void CmdCloseTerminalAdminUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) CuiHelper.DestroyUi(player, TerminalAdminUIName);
        }

        [ConsoleCommand("monumenttransit.admin.page")]
        private void CmdAdminPage(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 2) return;
            
            string type = arg.Args[0].ToString();
            if (int.TryParse(arg.Args[1].ToString(), out int change))
            {
                if (playerUIStates.TryGetValue(player.userID, out var state))
                {
                    if (type == "term") state.AdminMainTermPage += change;
                    else if (type == "dest") state.AdminMainDestPage += change;
                    else if (type == "termdest") state.AdminTermDestPage += change;

                    if (type == "termdest" && arg.Args.Length > 2)
                    {
                        OpenTerminalAdminUI(player, arg.Args[2].ToString());
                    }
                    else
                    {
                        OpenAdminUI(player);
                    }
                }
            }
        }

        // --- Destination Settings UI ---
        private const string DestinationAdminUIName = "MonumentTransit.DestinationAdminUI";

        [ConsoleCommand("monumenttransit.admin.editdest")]
        private void CmdEditDest(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length == 0) return;
            string returnTermId = arg.Args.Length > 1 ? arg.Args[1].ToString() : "MAIN";
            OpenDestinationAdminUI(player, arg.Args[0].ToString(), returnTermId);
        }

        private void OpenDestinationAdminUI(BasePlayer player, string destId, string returnTermId = "MAIN")
        {
            CuiHelper.DestroyUi(player, AdminUIName);
            CuiHelper.DestroyUi(player, DestinationAdminUIName);
            CuiHelper.DestroyUi(player, TerminalAdminUIName);

            if (!pluginData.Destinations.TryGetValue(destId, out var dest)) return;

            var elements = new CuiElementContainer();
            elements.Add(new CuiPanel { Image = { Color = "0.1 0.1 0.1 0.95" }, RectTransform = { AnchorMin = "0.3 0.2", AnchorMax = "0.7 0.8" }, CursorEnabled = true }, "Overlay", DestinationAdminUIName);
            
            elements.Add(new CuiElement { Parent = DestinationAdminUIName, Components = { new CuiTextComponent { Text = "DESTINATION SETTINGS", FontSize = 20, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }, new CuiRectTransformComponent { AnchorMin = "0 0.9", AnchorMax = "1 1" } } });
            elements.Add(new CuiButton { Button = { Command = "monumenttransit.admin.closedestui", Color = "0.8 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.9 0.92", AnchorMax = "0.98 0.98" }, Text = { Text = "X", FontSize = 18, Align = TextAnchor.MiddleCenter } }, DestinationAdminUIName);

            string backCommand = returnTermId == "MAIN" ? "monumenttransit.admin.openmainui" : $"monumenttransit.admin.editterminal {returnTermId}";
            elements.Add(new CuiButton { Button = { Command = backCommand, Color = "0.4 0.4 0.4 0.8" }, RectTransform = { AnchorMin = "0.02 0.92", AnchorMax = "0.15 0.98" }, Text = { Text = "< BACK", FontSize = 14, Align = TextAnchor.MiddleCenter } }, DestinationAdminUIName);

            // Name
            elements.Add(new CuiLabel { Text = { Text = "Name:", FontSize = 14, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.05 0.7", AnchorMax = "0.3 0.8" } }, DestinationAdminUIName);
            elements.Add(new CuiPanel { Image = { Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.3 0.7", AnchorMax = "0.7 0.8" } }, DestinationAdminUIName, DestinationAdminUIName + ".NameBg");
            elements.Add(new CuiElement { Parent = DestinationAdminUIName + ".NameBg", Components = { new CuiInputFieldComponent { Text = dest.Name, Command = $"monumenttransit.admin.uisetdestname2 {destId} {returnTermId}", FontSize = 14, Align = TextAnchor.MiddleLeft }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" } } });
            elements.Add(new CuiButton { Button = { Command = "", Color = "0.2 0.6 0.2 0.8" }, RectTransform = { AnchorMin = "0.75 0.7", AnchorMax = "0.95 0.8" }, Text = { Text = "PRESS ENTER\nTO SAVE", FontSize = 9, Align = TextAnchor.MiddleCenter } }, DestinationAdminUIName);

            // Category
            elements.Add(new CuiLabel { Text = { Text = "Category:", FontSize = 14, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.05 0.5", AnchorMax = "0.3 0.6" } }, DestinationAdminUIName);
            elements.Add(new CuiButton
            {
                Button = { Command = $"monumenttransit.admin.destcyclecategory {destId} {returnTermId}", Color = "0.2 0.4 0.6 0.8" },
                RectTransform = { AnchorMin = "0.3 0.5", AnchorMax = "0.7 0.6" },
                Text = { Text = dest.Category, FontSize = 14, Align = TextAnchor.MiddleCenter }
            }, DestinationAdminUIName);

            // Permission
            elements.Add(new CuiLabel { Text = { Text = "Permission:", FontSize = 14, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.05 0.3", AnchorMax = "0.3 0.4" } }, DestinationAdminUIName);
            elements.Add(new CuiPanel { Image = { Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.3 0.3", AnchorMax = "0.7 0.4" } }, DestinationAdminUIName, DestinationAdminUIName + ".PermBg");
            elements.Add(new CuiElement { Parent = DestinationAdminUIName + ".PermBg", Components = { new CuiInputFieldComponent { Text = dest.Permission ?? "", Command = $"monumenttransit.admin.setdestperm {destId} {returnTermId}", FontSize = 14, Align = TextAnchor.MiddleLeft }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" } } });
            elements.Add(new CuiButton { Button = { Command = "", Color = "0.2 0.6 0.2 0.8" }, RectTransform = { AnchorMin = "0.75 0.3", AnchorMax = "0.95 0.4" }, Text = { Text = "PRESS ENTER\nTO SAVE", FontSize = 9, Align = TextAnchor.MiddleCenter } }, DestinationAdminUIName);

            elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.deletedest {destId}", Color = "0.8 0.1 0.1 0.8" }, RectTransform = { AnchorMin = "0.3 0.05", AnchorMax = "0.7 0.15" }, Text = { Text = "DELETE DESTINATION", FontSize = 14, Align = TextAnchor.MiddleCenter } }, DestinationAdminUIName);

            CuiHelper.AddUi(player, elements);
        }

        [ConsoleCommand("monumenttransit.admin.closedestui")]
        private void CmdCloseDestUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) CuiHelper.DestroyUi(player, DestinationAdminUIName);
        }

        [ConsoleCommand("monumenttransit.admin.openmainui")]
        private void CmdOpenMainUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, DestinationAdminUIName);
            OpenAdminUI(player);
        }

        [ConsoleCommand("monumenttransit.admin.destcyclecategory")]
        private void CmdDestCycleCategory(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 1) return;
            string destId = arg.Args[0].ToString();
            string returnTermId = arg.Args.Length > 1 ? arg.Args[1].ToString() : "MAIN";

            if (pluginData.Destinations.TryGetValue(destId, out var dest))
            {
                int currentIdx = pluginData.Categories.IndexOf(dest.Category);
                if (currentIdx == -1 || currentIdx >= pluginData.Categories.Count - 1)
                {
                    dest.Category = pluginData.Categories.Count > 0 ? pluginData.Categories[0] : "None";
                }
                else
                {
                    dest.Category = pluginData.Categories[currentIdx + 1];
                }
                SaveDataAsync();
                OpenDestinationAdminUI(player, destId, returnTermId);
            }
        }

        [ConsoleCommand("monumenttransit.admin.setdestperm")]
        private void CmdSetDestPerm(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 2) return;
            string destId = arg.Args[0].ToString();
            string returnTermId = arg.Args[1].ToString();
            string perm = arg.Args.Length > 2 ? string.Join(" ", arg.Args.Select(x => x.ToString()).Skip(2)) : "";

            if (pluginData.Destinations.TryGetValue(destId, out var dest))
            {
                dest.Permission = perm;
                SaveDataAsync();
                OpenDestinationAdminUI(player, destId, returnTermId);
            }
        }

        [ConsoleCommand("monumenttransit.admin.uisetdestname2")]
        private void CmdUISetDestName2(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 2) return;
            string destId = arg.Args[0].ToString();
            string returnTermId = arg.Args[1].ToString();
            string name = string.Join(" ", arg.Args.Select(x => x.ToString()).Skip(2));
            
            if (pluginData.Destinations.TryGetValue(destId, out var dest))
            {
                dest.Name = name;
                SaveDataAsync();
                OpenDestinationAdminUI(player, destId, returnTermId);
            }
        }

        // --- Category Management UI ---
        private const string CategoryAdminUIName = "MonumentTransit.CategoryAdminUI";

        [ConsoleCommand("monumenttransit.admin.categoryui")]
        private void CmdOpenCategoryUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length == 0) return;
            OpenCategoryAdminUI(player, arg.Args[0].ToString());
        }

        private void OpenCategoryAdminUI(BasePlayer player, string terminalId)
        {
            CuiHelper.DestroyUi(player, TerminalAdminUIName);
            CuiHelper.DestroyUi(player, CategoryAdminUIName);

            var elements = new CuiElementContainer();
            elements.Add(new CuiPanel { Image = { Color = "0.1 0.1 0.1 0.95" }, RectTransform = { AnchorMin = "0.3 0.2", AnchorMax = "0.7 0.8" }, CursorEnabled = true }, "Overlay", CategoryAdminUIName);
            
            elements.Add(new CuiElement { Parent = CategoryAdminUIName, Components = { new CuiTextComponent { Text = "CATEGORY MANAGER", FontSize = 20, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }, new CuiRectTransformComponent { AnchorMin = "0 0.9", AnchorMax = "1 1" } } });
            elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.closecategoryui", Color = "0.8 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.9 0.92", AnchorMax = "0.98 0.98" }, Text = { Text = "X", FontSize = 18, Align = TextAnchor.MiddleCenter } }, CategoryAdminUIName);

            elements.Add(new CuiButton { Button = { Command = $"monumenttransit.admin.editterminal {terminalId}", Color = "0.4 0.4 0.4 0.8" }, RectTransform = { AnchorMin = "0.02 0.92", AnchorMax = "0.15 0.98" }, Text = { Text = "< BACK", FontSize = 14, Align = TextAnchor.MiddleCenter } }, CategoryAdminUIName);

            int i = 0;
            foreach (var category in pluginData.Categories.ToList())
            {
                float yMax = 0.85f - (i * 0.08f);
                float yMin = yMax - 0.07f;
                if (yMin < 0.2f) break;

                string b64Cat = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(category));

                // Input Field for Category Name
                string bgName = CategoryAdminUIName + $".CatBg_{i}";
                elements.Add(new CuiPanel { Image = { Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = $"0.05 {yMin}", AnchorMax = $"0.6 {yMax}" } }, CategoryAdminUIName, bgName);
                elements.Add(new CuiElement { Parent = bgName, Components = { new CuiInputFieldComponent { Text = category, Command = $"monumenttransit.admin.renamecategory {terminalId} {b64Cat}", FontSize = 16, Align = TextAnchor.MiddleLeft }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" } } });

                // Rename Button
                elements.Add(new CuiButton
                {
                    Button = { Command = "", Color = "0.2 0.6 0.2 0.8" },
                    RectTransform = { AnchorMin = $"0.62 {yMin}", AnchorMax = $"0.82 {yMax}" },
                    Text = { Text = "PRESS ENTER\nTO RENAME", FontSize = 9, Align = TextAnchor.MiddleCenter }
                }, CategoryAdminUIName);

                // Delete Button
                elements.Add(new CuiButton
                {
                    Button = { Command = $"monumenttransit.admin.deletecategory {terminalId} {b64Cat}", Color = "0.8 0.2 0.2 0.8" },
                    RectTransform = { AnchorMin = $"0.84 {yMin}", AnchorMax = $"0.95 {yMax}" },
                    Text = { Text = "DELETE", FontSize = 14, Align = TextAnchor.MiddleCenter }
                }, CategoryAdminUIName);

                i++;
            }

            elements.Add(new CuiLabel { Text = { Text = "New Category:", FontSize = 14, Align = TextAnchor.MiddleLeft }, RectTransform = { AnchorMin = "0.05 0.05", AnchorMax = "0.3 0.12" } }, CategoryAdminUIName);
            string newCatBg = CategoryAdminUIName + ".NewCatBg";
            elements.Add(new CuiPanel { Image = { Color = "0.2 0.2 0.2 0.8" }, RectTransform = { AnchorMin = "0.3 0.05", AnchorMax = "0.7 0.12" } }, CategoryAdminUIName, newCatBg);
            elements.Add(new CuiElement { Parent = newCatBg, Components = { new CuiInputFieldComponent { Text = "", Command = $"monumenttransit.admin.addcategory {terminalId}", FontSize = 14, Align = TextAnchor.MiddleLeft }, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" } } });
            elements.Add(new CuiButton { Button = { Command = "", Color = "0.2 0.6 0.2 0.8" }, RectTransform = { AnchorMin = "0.75 0.05", AnchorMax = "0.95 0.12" }, Text = { Text = "PRESS ENTER\nTO ADD", FontSize = 9, Align = TextAnchor.MiddleCenter } }, CategoryAdminUIName);

            CuiHelper.AddUi(player, elements);
        }

        [ConsoleCommand("monumenttransit.admin.closecategoryui")]
        private void CmdCloseCategoryUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) CuiHelper.DestroyUi(player, CategoryAdminUIName);
        }

        [ConsoleCommand("monumenttransit.admin.renamecategory")]
        private void CmdRenameCategory(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 3) return;
            string termId = arg.Args[0].ToString();
            string oldCategory = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(arg.Args[1].ToString()));
            string newCategory = string.Join(" ", arg.Args.Select(x => x.ToString()).Skip(2));

            if (!string.IsNullOrEmpty(newCategory) && oldCategory != newCategory)
            {
                int idx = pluginData.Categories.IndexOf(oldCategory);
                if (idx != -1)
                {
                    pluginData.Categories[idx] = newCategory;
                    
                    foreach (var dest in pluginData.Destinations.Values)
                    {
                        if (dest.Category == oldCategory) dest.Category = newCategory;
                    }
                    SaveDataAsync();
                }
            }
            OpenCategoryAdminUI(player, termId);
        }

        [ConsoleCommand("monumenttransit.admin.deletecategory")]
        private void CmdDeleteCategory(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 2) return;
            string termId = arg.Args[0].ToString();
            string category = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(arg.Args[1].ToString()));

            if (pluginData.Categories.Contains(category))
            {
                pluginData.Categories.RemoveAll(c => c == category);
                
                foreach (var dest in pluginData.Destinations.Values)
                {
                    if (dest.Category == category) dest.Category = "None";
                }
                
                if (pluginData.Categories.Count == 0) pluginData.Categories.Add("None");
                
                SaveDataAsync();
            }
            OpenCategoryAdminUI(player, termId);
        }

        [ConsoleCommand("monumenttransit.admin.setname")]
        private void CmdSetTerminalName(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 2) return;
            string termId = arg.Args[0].ToString();
            string name = string.Join(" ", arg.Args.Select(x => x.ToString()).Skip(1));
            
            if (pluginData.Terminals.TryGetValue(termId, out var term))
            {
                term.Name = name;
                SaveDataAsync();
                KillTerminal(term);
                SpawnTerminal(term);
                OpenTerminalAdminUI(player, termId);
            }
        }

        [ConsoleCommand("monumenttransit.admin.setkit")]
        private void CmdSetTerminalKit(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 2) return;
            string termId = arg.Args[0].ToString();
            string kit = string.Join(" ", arg.Args.Select(x => x.ToString()).Skip(1));
            
            if (pluginData.Terminals.TryGetValue(termId, out var term))
            {
                term.Kit = kit;
                SaveDataAsync();
                KillTerminal(term);
                SpawnTerminal(term);
                OpenTerminalAdminUI(player, termId);
            }
        }

        [ConsoleCommand("monumenttransit.admin.toggledest")]
        private void CmdToggleTerminalDest(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 2) return;
            string termId = arg.Args[0].ToString();
            string destId = arg.Args[1].ToString();

            if (pluginData.Terminals.TryGetValue(termId, out var term))
            {
                if (term.AllowedDestinations == null) term.AllowedDestinations = new List<string>();
                
                // If it's effectively "all allowed", populate it with all current destinations
                if (term.AllowedDestinations.Count == 0)
                {
                    foreach (var d in pluginData.Destinations.Values) term.AllowedDestinations.Add(d.Id);
                }

                if (term.AllowedDestinations.Contains(destId)) 
                {
                    term.AllowedDestinations.Remove(destId);
                }
                else 
                {
                    term.AllowedDestinations.Add(destId);
                    term.AllowedDestinations.Remove("NONE"); // Remove dummy flag if we're adding a real destination
                }
                
                // If list is empty after removing, add a dummy flag to prevent it from reverting to "all allowed"
                if (term.AllowedDestinations.Count == 0)
                {
                    term.AllowedDestinations.Add("NONE");
                }
                
                SaveDataAsync();
                OpenTerminalAdminUI(player, termId);
            }
        }

        [ConsoleCommand("monumenttransit.admin.deleteterminal")]
        private void CmdDeleteTerminalSettings(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length == 0) return;
            string termId = arg.Args[0].ToString();
            
            API_RemoveTerminal(termId);
            CuiHelper.DestroyUi(player, TerminalAdminUIName);
            player.ChatMessage(GetMsg("TerminalDeleted", player.UserIDString));
        }

        [ConsoleCommand("monumenttransit.admin.setdestname")]
        private void CmdSetDestName(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 2) return;
            string destId = arg.Args[0].ToString();
            string name = string.Join(" ", arg.Args.Select(x => x.ToString()).Skip(1));
            
            if (pluginData.Destinations.TryGetValue(destId, out var dest))
            {
                dest.Name = name;
                SaveDataAsync();
                player.ChatMessage($"Destination {destId} renamed to '{name}'");
            }
            else
            {
                player.ChatMessage($"Destination {destId} not found.");
            }
        }

        // --- Commands ---

        [ConsoleCommand("monumenttransit.admin.uisetdestname")]
        private void CmdUISetDestName(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 3) return;
            string termId = arg.Args[0].ToString();
            string destId = arg.Args[1].ToString();
            string name = string.Join(" ", arg.Args.Select(x => x.ToString()).Skip(2));
            
            if (pluginData.Destinations.TryGetValue(destId, out var dest))
            {
                dest.Name = name;
                SaveDataAsync();
                OpenTerminalAdminUI(player, termId);
            }
        }

        // CHANGE: Add missing console command for setting destination category via UI input
        [ConsoleCommand("monumenttransit.admin.uisetdestcategory")]
        private void CmdUISetDestCategory(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 3) return;
            string termId = arg.Args[0].ToString();
            string destId = arg.Args[1].ToString();
            string category = string.Join(" ", arg.Args.Select(x => x.ToString()).Skip(2));
            
            if (pluginData.Destinations.TryGetValue(destId, out var dest))
            {
                dest.Category = category;
                
                if (!string.IsNullOrEmpty(category) && !pluginData.Categories.Contains(category))
                {
                    pluginData.Categories.Add(category);
                }
                
                SaveDataAsync();
                OpenTerminalAdminUI(player, termId);
            }
        }

        [ConsoleCommand("monumenttransit.admin.addcategory")]
        private void CmdAdminAddCategory(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 2) return;
            string termId = arg.Args[0].ToString();
            string category = string.Join(" ", arg.Args.Select(x => x.ToString()).Skip(1));
            
            if (!string.IsNullOrEmpty(category) && !pluginData.Categories.Contains(category))
            {
                pluginData.Categories.Add(category);
                SaveDataAsync();
            }
            OpenCategoryAdminUI(player, termId);
        }

        [ConsoleCommand("monumenttransit.admin.cyclecategory")]
        private void CmdAdminCycleCategory(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !permission.UserHasPermission(player.UserIDString, "monumenttransit.admin") || arg.Args == null || arg.Args.Length < 2) return;
            string termId = arg.Args[0].ToString();
            string destId = arg.Args[1].ToString();
            
            if (pluginData.Destinations.TryGetValue(destId, out var dest))
            {
                if (pluginData.Categories.Count > 0)
                {
                    int currentIndex = pluginData.Categories.IndexOf(dest.Category);
                    int nextIndex = (currentIndex + 1) % pluginData.Categories.Count;
                    dest.Category = pluginData.Categories[nextIndex];
                    SaveDataAsync();
                }
                OpenTerminalAdminUI(player, termId);
            }
        }

        [ChatCommand("tterminal")]
        private void CmdChatAdmin(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, "monumenttransit.admin"))
            {
                player.ChatMessage(GetMsg("NoPermission", player.UserIDString));
                return;
            }

            if (args.Length == 0) OpenAdminUI(player);
        }

        // --- Hooks ---

        [HookMethod("OnEntityKill")]
        void OnEntityKill(BaseNetworkable entity)
        {
            if (entity == null) return;
        }

        [HookMethod("OnEntityTakeDamage")]
        void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity is BasePlayer player && activeTeleports.ContainsKey(player.userID))
            {
                if (!config.TeleportCancellation.CancelOnDamage) return;
                if (info == null || info.damageTypes == null) return;

                if (info.damageTypes.Has(Rust.DamageType.Cold) && !config.TeleportCancellation.CancelIfFreezing) return;
                if ((info.damageTypes.Has(Rust.DamageType.Hunger) || info.damageTypes.Has(Rust.DamageType.Thirst)) && !config.TeleportCancellation.CancelIfStarving) return;
                if (info.damageTypes.Has(Rust.DamageType.Bleeding) && !config.TeleportCancellation.CancelIfBleeding) return;

                SendMsg(player, GetMsg("TeleportCancelled", player.UserIDString));
                CancelTeleport(player.userID);
            }
        }

        [HookMethod("OnEntityUse")]
        object OnEntityUse(BaseEntity entity, BasePlayer player)
        {
            if (entity == null || player == null) return null;
            
            foreach (var term in pluginData.Terminals.Values)
            {
                if (term.NetworkId == entity.net.ID.Value)
                {
                    if (permission.UserHasPermission(player.UserIDString, "monumenttransit.use")) OpenPlayerUI(player, term.Id);
                    else player.ChatMessage(GetMsg("NoPermission", player.UserIDString));
                    return false;
                }
            }
            return null;
        }

        [HookMethod("OnUseNPC")]
        object OnUseNPC(BasePlayer npc, BasePlayer player)
        {
            if (npc == null || player == null) return null;
            
            foreach (var term in pluginData.Terminals.Values)
            {
                if (term.NetworkId == npc.net.ID.Value)
                {
                    if (permission.UserHasPermission(player.UserIDString, "monumenttransit.use")) OpenPlayerUI(player, term.Id);
                    else player.ChatMessage(GetMsg("NoPermission", player.UserIDString));
                    return false;
                }
            }
            return null;
        }

        [HookMethod("OnPlayerInput")]
        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null) return;
            if (input.WasJustPressed(BUTTON.USE))
            {
                RaycastHit hit;
                int layerMask = UnityEngine.LayerMask.GetMask("Player (Server)", "Player (Model)", "Default", "Deployed", "Construction", "Prevent Building");
                if (UnityEngine.Physics.SphereCast(player.eyes.HeadRay(), 0.25f, out hit, 3.5f, layerMask))
                {
                    var entity = hit.GetEntity();
                    if (entity != null)
                    {
                        foreach (var term in pluginData.Terminals.Values)
                        {
                            if (term.NetworkId == entity.net.ID.Value)
                            {
                                if (permission.UserHasPermission(player.UserIDString, "monumenttransit.use")) 
                                    OpenPlayerUI(player, term.Id);
                                else 
                                    player.ChatMessage(GetMsg("NoPermission", player.UserIDString));
                                return;
                            }
                        }
                    }
                }
            }
        }
    }
}
