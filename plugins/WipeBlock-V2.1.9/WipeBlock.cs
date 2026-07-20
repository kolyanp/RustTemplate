using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using UnityEngine.UI;

namespace Oxide.Plugins;

[Info("Wipe Block", "Mevent", "2.1.9")]
internal class WipeBlock : RustPlugin
{
    #region Fields

    [PluginReference] private Plugin
        Notify = null,
        UINotify = null,
        ServerPanel = null;

    private static WipeBlock Instance;

    private const string
        Layer = "UI.WipeBlock",
        ScreenLayer = "UI.WipeBlock.Screen",
        IgnorePermission = "WipeBlock.ignore",
        UnlockNotifyPermission = "WipeBlock.unlocknotify";

    private (bool spStatus, int categoryID)
        _serverPanelCategory = (false, -1); // key - use serverPanel, value - category id

    private readonly List<ItemConf> ItemsAll = new();

    private readonly Dictionary<ItemConf, int> CooldownByItems = new();

    private readonly Dictionary<int, List<ItemConf>> ItemsByCooldown = new();

    private class ItemsData
    {
        public string Category;

        public List<ItemConf> Items;
    }

    private bool anyBlocked;

    #endregion

    #region Config

    private Configuration _config;

    private class Configuration
    {
        [JsonProperty(PropertyName = "ServerPanel Template (V1, V2)")]
        public PatternServerMenu Pattern = PatternServerMenu.Fullscreen;

        [JsonProperty(PropertyName = "Commands", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public string[] Commands = {"block", "wipeblock"};

        [JsonProperty(PropertyName = "Work with Notify?")]
        public bool UseNotify = true;

        [JsonProperty(PropertyName = "Time Indent (seconds)")]
        public float Indent;

        [JsonProperty(PropertyName = "Prevent to use blocked items")]
        public bool BlockUse = true;

        [JsonProperty(PropertyName = "Prevent to craft blocked items")]
        public bool BlockCraft = false;

        [JsonProperty(PropertyName = "Settings", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<Category> Categories = new()
        {
            new Category
            {
                LangKey = "Weapons",
                Items = new Dictionary<int, List<ItemConf>>
                {
                    [3600] = new()
                    {
                        new ItemConf("pistol.revolver", 0),
                        new ItemConf("shotgun.double", 0)
                    },
                    [7200] = new()
                    {
                        new ItemConf("pistol.semiauto", 0),
                        new ItemConf("pistol.python", 0),
                        new ItemConf("shotgun.pump", 0)
                    },
                    [10800] = new()
                    {
                        new ItemConf("pistol.m92", 0),
                        new ItemConf("shotgun.spas12", 0),
                        new ItemConf("rifle.semiauto", 0)
                    },
                    [14400] = new()
                    {
                        new ItemConf("smg.2", 0),
                        new ItemConf("smg.mp5", 0),
                        new ItemConf("smg.thompson", 0),
                        new ItemConf("rifle.m39", 0)
                    },
                    [21600] = new()
                    {
                        new ItemConf("rifle.ak", 0),
                        new ItemConf("rifle.lr300", 0),
                        new ItemConf("rifle.bolt", 0),
                        new ItemConf("rifle.l96", 0)
                    },
                    [86400] = new()
                    {
                        new ItemConf("lmg.m249", 0)
                    }
                }
            },
            new Category
            {
                LangKey = "Explosives",
                Items = new Dictionary<int, List<ItemConf>>
                {
                    [14400] = new()
                    {
                        new ItemConf("grenade.beancan", 0)
                    },
                    [64800] = new()
                    {
                        new ItemConf("explosive.satchel", 0)
                    },
                    [86400] = new()
                    {
                        new ItemConf("ammo.rifle.explosive", 0),
                        new ItemConf("explosive.timed", 0),
                        new ItemConf("ammo.grenadelauncher.he", 0),
                        new ItemConf("ammo.rocket.basic", 0)
                    }
                }
            },
            new Category
            {
                LangKey = "Attire",
                Items = new Dictionary<int, List<ItemConf>>
                {
                    [10800] = new()
                    {
                        new ItemConf("coffeecan.helmet", 0),
                        new ItemConf("roadsign.jacket", 0),
                        new ItemConf("roadsign.kilt", 0)
                    },
                    [14400] = new()
                    {
                        new ItemConf("metal.facemask", 0),
                        new ItemConf("metal.plate.torso", 0)
                    },
                    [21600] = new()
                    {
                        new ItemConf("heavy.plate.helmet", 0),
                        new ItemConf("heavy.plate.jacket", 0),
                        new ItemConf("heavy.plate.pants", 0)
                    },
                    [86400] = new()
                    {
                        new ItemConf("lmg.m249", 0)
                    }
                }
            }
        };

        [JsonProperty(PropertyName = "Interface")]
        public UserInterface UI = new()
        {
            Gradients = new List<string>
            {
                "#4F965F",
                "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B",
                "#FFD01B",
                "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B",
                "#FFD01B",
                "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B",
                "#FFD01B",
                "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B",
                "#FFD01B",
                "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B", "#FFD01B",
                "#FFD01B",
                "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060",
                "#FF6060",
                "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060",
                "#FF6060",
                "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060",
                "#FF6060",
                "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060",
                "#FF6060",
                "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060", "#FF6060",
                "#FF6060"
            },
            OnScreen = new OnScreenSettings
            {
                Enabled = true,
                Position = new InterfacePosition
                {
                    AnchorMin = "1 1",
                    AnchorMax = "1 1",
                    OffsetMin = "-150 -40",
                    OffsetMax = "-20 -10"
                }
            }
        };

        public VersionNumber Version;
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum PatternServerMenu
    {
        V1,
        V2,
        V4,
        Fullscreen,
        FS
    }

    private class OnScreenSettings
    {
        [JsonProperty(PropertyName = "Show on screen?")]
        public bool Enabled = true;

        [JsonProperty(PropertyName = "Position Settings")]
        public InterfacePosition Position;
    }

    private class InterfacePosition
    {
        public string AnchorMin;

        public string AnchorMax;

        public string OffsetMin;

        public string OffsetMax;
    }

    private class UserInterface
    {
        [JsonProperty(PropertyName = "Gradients", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> Gradients;

        [JsonProperty(PropertyName = "OnScreen Settings")]
        public OnScreenSettings OnScreen;
    }

    private class Category
    {
        [JsonProperty(PropertyName = "Lang Key")]
        public string LangKey;

        [JsonProperty(PropertyName = "Items", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<int, List<ItemConf>> Items;
    }

    private class ItemConf
    {
        [JsonProperty(PropertyName = "ShortName")]
        public string ShortName;

        [JsonProperty(PropertyName = "Skin")] public ulong Skin;

        [JsonIgnore] public readonly string GUID = CuiHelper.GetGuid();

        public ItemConf(string shortName, ulong skin)
        {
            ShortName = shortName;
            Skin = skin;
        }

        [JsonIgnore] private int _itemId = -1;

        [JsonIgnore]
        public int itemId
        {
            get
            {
                if (_itemId == -1)
                    _itemId = ItemManager.FindItemDefinition(ShortName)?.itemid ?? -1;

                return _itemId;
            }
        }
    }

    protected override void LoadConfig()
    {
        base.LoadConfig();
        try
        {
            _config = Config.ReadObject<Configuration>();
            if (_config == null) throw new Exception();

            if (_config.Version < Version)
                UpdateConfigValues();

            SaveConfig();
        }
        catch
        {
            PrintError("Your configuration file contains an error. Using default configuration values.");
            LoadDefaultConfig();
        }
    }

    protected override void SaveConfig()
    {
        Config.WriteObject(_config);
    }

    protected override void LoadDefaultConfig()
    {
        _config = new Configuration();
    }

    private void UpdateConfigValues()
    {
        PrintWarning("Config update detected! Updating config values...");

        var baseConfig = new Configuration();

        if (_config.Version == default && _config.Version < new VersionNumber(1, 0, 4))
        {
            var val = Config["Show on screen?"];
            if (val != null)
                _config.UI.OnScreen.Enabled = Convert.ToBoolean(val);
        }

        _config.Version = Version;
        PrintWarning("Config update completed!");
    }

    #endregion

    #region Hooks

    private void Init()
    {
        Instance = this;

        Unsubscribe(nameof(OnPlayerConnected));

        UnsubscribeHooks();

        LoadTemplate();
    }

    private void OnServerInitialized()
    {
        LoadWipeTime();

        FillingItems();

        LoadServerPanel();

        if (_config.UI.Gradients.Count < 101)
            PrintError("Gradients less than 101. Check the config!!!");

        if (!permission.PermissionExists(IgnorePermission))
            permission.RegisterPermission(IgnorePermission, this);

        if (!permission.PermissionExists(UnlockNotifyPermission))
            permission.RegisterPermission(UnlockNotifyPermission, this);

        AddCovalenceCommand(_config.Commands, nameof(CmdOpenBlock));

        AddCovalenceCommand("wb.indent", nameof(CmdChangeIndent));

        UnlockItemsController();

        CheckActive();

        CheckPlayers();

        if (anyBlocked && _config.UI.OnScreen.Enabled)
        {
            Subscribe(nameof(OnPlayerConnected));

            foreach (var player in BasePlayer.activePlayerList)
                OnScreenUi(player);
        }
    }

    private void Unload()
    {
        foreach (var player in BasePlayer.activePlayerList)
        {
            CuiHelper.DestroyUi(player, Layer);
            CuiHelper.DestroyUi(player, ScreenLayer);
            CuiHelper.DestroyUi(player, "WipeBlock.Background");
        }

        Instance = null;
    }

    private void API_SP_SaveCategory(int categoryID)
    {
        LoadServerPanel();
    }

    private void API_SP_RemoveCategory(int categoryID)
    {
        NextTick(LoadServerPanel);
    }

    private void OnPluginLoaded(Plugin plugin)
    {
        switch (plugin.Name)
        {
            // CHANGE: Replaced nameof(ServerPanel) with string literal to fix compiler error 'A constant value is expected'
            case "ServerPanel":
                timer.In(1, LoadServerPanel);
                break;
        }
    }

    private void OnPluginUnloaded(Plugin plugin)
    {
        switch (plugin.Name)
        {
            // CHANGE: Replaced nameof(ServerPanel) with string literal to fix compiler error 'A constant value is expected'
            case "ServerPanel":
                _serverPanelCategory.spStatus = false;
                _serverPanelCategory.categoryID = -1;

                NextTick(() => UpdateTemplateRenderer());
                break;
        }
    }

    private void OnNewSave()
    {
        PrintWarning("Wipe detected");
        WipeTime = DateTime.UtcNow;
        SaveWipeTime();

        _config.Indent = 0;
        SaveConfig();
    }

    private void OnPlayerConnected(BasePlayer player)
    {
        if (player == null) return;

        if (anyBlocked && _config.UI.OnScreen.Enabled)
            OnScreenUi(player);
    }

    private object CanWearItem(PlayerInventory inventory, Item item, int targetSlot)
    {
        var player = inventory.GetComponent<BasePlayer>();
        if (!IsValid(player)) return null;

        if (IsBlocked(item.info)) return false;

        return null;
    }

    private object CanEquipItem(PlayerInventory inventory, Item item, int targetPos)
    {
        return CanWearItem(inventory, item, targetPos);
    }

    private object CanMoveItem(Item item, PlayerInventory inventory, ItemContainerId itemContainer, int targetPosition,
        int itemAmount, ItemMoveModifier itemMoveModifier)
    {
        if (inventory == null || item == null) return null;

        var player = inventory.GetComponent<BasePlayer>();
        if (!IsValid(player)) return null;

        ItemContainer container = null;
        if (targetPosition == -1 && player.inventory.loot?.containers?.Count > 0)
            container = player.inventory.loot.containers[0];
        else
            container = player.inventory.FindContainer(itemContainer);

        if (container == null) return null;

        if (container.entityOwner != null && IsItemBlocked(item, container))
        {
            SendNotify(player, ItemLocked, 1);
            return false;
        }

        if ((container.uid == player.inventory.containerBelt.uid ||
             container.uid == player.inventory.containerWear.uid) &&
            IsBlocked(item.info.shortname, item.skin))
        {
            SendNotify(player, ItemLocked, 1);
            return false;
        }

        return null;
    }

    private object CanAcceptItem(ItemContainer container, Item item, int targetPosition)
    {
        if (container == null || item == null || container.entityOwner == null)
            return null;

        var player = item.GetOwnerPlayer();
        if (!IsValid(player)) return null;

        if (IsItemBlocked(item, container))
        {
            SendNotify(player, ItemLocked, 1);
            return ItemContainer.CanAcceptResult.CannotAcceptRightNow;
        }

        return null;
    }

    private object OnWeaponReload(BaseProjectile weapon, BasePlayer player)
    {
        if (!IsValid(player)) return null;

        weapon.SwitchAmmoTypesIfNeeded(player.inventory);
        if (IsBlocked(weapon.primaryMagazine.ammoType.shortname))
        {
            SendNotify(player, AmmoLocked, 1);
            return true;
        }

        return null;
    }

    private object OnMagazineReload(BaseProjectile weapon, int desiredAmount, BasePlayer player)
    {
        if (!IsValid(player)) return null;

        NextTick(() =>
        {
            if (IsBlocked(weapon.primaryMagazine.ammoType))
            {
                player.GiveItem(ItemManager.CreateByItemID(weapon.primaryMagazine.ammoType.itemid,
                    weapon.primaryMagazine.contents));
                weapon.primaryMagazine.contents = 0;
                weapon.GetItem().LoseCondition(weapon.GetItem().maxCondition);
                weapon.SendNetworkUpdate();
                player.SendNetworkUpdate();
            }
        });

        return null;
    }

    private object OnItemCraft(ItemCraftTask task, BasePlayer player, Item fromTempBlueprint)
    {
        var target = task.blueprint.targetItem;
        if (task == null || target == null || task?.instanceData?.dataInt != null || task?.amount == 0) return null;

        if (!IsValid(player)) return null;

        if (IsItemBlocked(target, task.skinID <= 0 ? 0 : task.skinID))
        {
            SendNotify(player, ItemBlockedCraft, 1);
            ReturnItems(task, player);
            return true;
        }

        return null;
    }

    #endregion

    #region Commands

    private void CmdOpenBlock(IPlayer cov, string command, string[] args)
    {
        var player = cov?.Object as BasePlayer;
        if (player == null) return;

        //MainUi(player, GetStartPage(), true);

        if (_serverPanelCategory.spStatus && _serverPanelCategory.categoryID != -1)
            ServerPanel?.Call("API_OnServerPanelOpenCategoryByID", player, _serverPanelCategory.categoryID);
        else
            UpdateUI(player, container => { templateRenderer.Render(player, container); });
    }

    private void CmdChangeIndent(IPlayer cov, string command, string[] args)
    {
        if (!cov.IsAdmin) return;

        if (args.Length == 0 || !int.TryParse(args[0], out var seconds))
        {
            cov.Reply($"Error syntax! Use: /{command} [seconds]");
            return;
        }

        _config.Indent += seconds;
        SaveConfig();

        cov.Reply($"Indent from wipe date changed on '{seconds}'!");

        CheckActive();
    }

    [ConsoleCommand("UI_WipeBlock")]
    private void CmdConsoleWipeBlock(ConsoleSystem.Arg arg)
    {
        var player = arg.Player();
        if (player == null || !arg.HasArgs()) return;

        switch (arg.Args[0])
        {
            case "close":
            {
                break;
            }

            case "page":
            {
                if (!int.TryParse(arg.Args[1], out var type)) return;

                UpdateUI(player, container =>
                {
                    templateRenderer.DrawHeader(player, container, type);
                    templateRenderer.DrawItems(player, container, type);
                });
                break;
            }

            case "item_update":
            {
                var type = arg.GetInt(1);

                UpdateUI(player, container => templateRenderer.DrawItems(player, container, type));
                break;
            }
        }
    }

    #endregion

    #region Interface

    private void OnScreenUi(BasePlayer player)
    {
        var container = new CuiElementContainer();

        container.Add(new CuiPanel
        {
            RectTransform =
            {
                AnchorMin = _config.UI.OnScreen.Position.AnchorMin,
                AnchorMax = _config.UI.OnScreen.Position.AnchorMax,
                OffsetMin = _config.UI.OnScreen.Position.OffsetMin,
                OffsetMax = _config.UI.OnScreen.Position.OffsetMax
            },
            Image = {Color = "0 0 0 0"}
        }, "Hud", ScreenLayer, ScreenLayer);

        container.Add(new CuiLabel
        {
            RectTransform =
            {
                AnchorMin = "0 0.5", AnchorMax = "1 1"
            },
            Text =
            {
                Text = Msg(player, OnScreenTitle),
                Align = TextAnchor.LowerCenter,
                Font = "robotocondensed-regular.ttf",
                FontSize = 14,
                Color = "1 1 1 1"
            }
        }, ScreenLayer);

        container.Add(new CuiLabel
        {
            RectTransform =
            {
                AnchorMin = "0 0", AnchorMax = "1 0.5"
            },
            Text =
            {
                Text = Msg(player, OnScreenDescription),
                Align = TextAnchor.UpperCenter,
                Font = "robotocondensed-regular.ttf",
                FontSize = 10,
                Color = "1 1 1 0.5"
            }
        }, ScreenLayer);

        container.Add(new CuiButton
        {
            RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
            Text = {Text = ""},
            Button =
            {
                Color = "0 0 0 0",
                Command = _config.Commands[0]
            }
        }, ScreenLayer);

        CuiHelper.AddUi(player, container);
    }

    #endregion

    #region Interfave v2

    private ITemplateRenderer templateRenderer;

    private interface ITemplateRenderer
    {
        void Render(BasePlayer player, CuiElementContainer container);

        void DrawBackground(CuiElementContainer container, string parent, string contentLayer);

        void DrawHeader(BasePlayer player, CuiElementContainer container, int current);

        void DrawItems(BasePlayer player, CuiElementContainer container, int type);
    }

    private static class TemplateRenderUniversal
    {
        public static void DrawItem(BasePlayer player, CuiElementContainer container, ItemConf item, int type,
            string offsetMin, string offsetMax, string parent)
        {
            container.Add(new CuiElement
            {
                Name = Layer + $".Item.{item.GUID}",
                Parent = parent,
                Components =
                {
                    new CuiImageComponent {Color = HexToCuiColor("#303030", 50)},
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = offsetMin,
                        OffsetMax = offsetMax
                    }
                }
            });

            container.Add(new CuiElement
            {
                Parent = Layer + $".Item.{item.GUID}",
                Components =
                {
                    new CuiImageComponent
                    {
                        Color = HexToCuiColor(Instance.GetGradient(item), 50),
                        Sprite = "assets/content/ui/ui.background.transparent.linear.psd"
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1"
                    }
                }
            });

            container.Add(new CuiElement
            {
                Parent = Layer + $".Item.{item.GUID}",
                Components =
                {
                    new CuiImageComponent {ItemId = item.itemId},
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "14 -60",
                        OffsetMax = "64 -10"
                    }
                }
            });

            container.Add(new CuiElement
            {
                Parent = Layer + $".Item.{item.GUID}",
                Components =
                {
                    new CuiImageComponent {Color = HexToCuiColor("#000000", 0)},
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "2 -74",
                        OffsetMax = "77 -64"
                    }
                }
            });

            container.Add(new CuiElement
            {
                Parent = Layer + $".Item.{item.GUID}",
                Name = Layer + $".Item.{item.GUID}.Title",
                DestroyUi = Layer + $".Item.{item.GUID}.Title",
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = Instance.IsBlocked(item)
                            ? "<b>%TIME_LEFT%</b>"
                            : "AVAILABLE",
                        Align = TextAnchor.MiddleCenter,
                        Font = "robotocondensed-bold.ttf",
                        FontSize = 10,
                        Color = "1 1 1 1"
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "0 -79",
                        OffsetMax = "79 -63"
                    },
                    new CuiCountdownComponent
                    {
                        EndTime = 0,
                        StartTime = Instance.LeftTime(item),
                        Step = 1,
                        TimerFormat = TimerFormat.HoursMinutesSeconds,
                        DestroyIfDone = true,
                        Command = $"UI_WipeBlock item_update {type}"
                    }
                }
            });
        }
    }

    private class TemplateFullscreenRenderer : ITemplateRenderer
    {
        public void Render(BasePlayer player, CuiElementContainer container)
        {
            DrawBackground(container, "Overlay", "WipeBlock.Background");

            DrawHeader(player, container, 2);

            DrawItems(player, container);
        }

        public void DrawBackground(CuiElementContainer container, string parent, string contentLayer)
        {
            container.Add(new CuiPanel
            {
                RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
                Image =
                {
                    Color = HexToCuiColor("#191919", 90),
                    Sprite = "assets/content/ui/UI.Background.TileTex.psd",
                    Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat"
                },
                CursorEnabled = true
            }, parent, contentLayer, contentLayer);

            container.Add(new CuiElement
            {
                Name = Layer,
                DestroyUi = Layer,
                Parent = contentLayer,
                Components =
                {
                    new CuiImageComponent
                    {
                        Color = HexToCuiColor("#191919", 50),
                        Material = "assets/content/ui/uibackgroundblur.mat"
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0.5 0.5",
                        AnchorMax = "0.5 0.5",
                        OffsetMin = "-600 -298",
                        OffsetMax = "600 293"
                    }
                }
            });
        }

        public void DrawHeader(BasePlayer player, CuiElementContainer container, int current)
        {
            #region Header

            container.Add(new CuiPanel
            {
                RectTransform = {AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -50", OffsetMax = "0 0"},
                Image =
                {
                    Color = HexToCuiColor("#494949"),
                    Sprite = "assets/content/ui/UI.Background.Transparent.LinearLTR.tga"
                }
            }, Layer, Layer + ".Header", Layer + ".Header");

            container.Add(new CuiLabel
            {
                RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "20 0", OffsetMax = "0 0"},
                Text =
                {
                    Text = Msg(player, TitleMenu), Align = TextAnchor.MiddleLeft, Color = HexToCuiColor("#E2DBD3"),
                    FontSize = 22
                }
            }, Layer + ".Header");

            container.Add(new CuiButton
            {
                RectTransform = {AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-40 -40", OffsetMax = "0 0"},
                Button = {Color = HexToCuiColor("#CF432D", 90), Close = "WipeBlock.Background"},
                Text = {Text = "✖", Align = TextAnchor.MiddleCenter, FontSize = 26}
            }, Layer + ".Header");

            #endregion

            #region Buttons

            container.Add(new CuiElement
            {
                Name = Layer + ".Buttons",
                DestroyUi = Layer + ".Buttons",
                Parent = Layer,
                Components =
                {
                    new CuiImageComponent {Color = "0 0 0 0"},
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "20 -100",
                        OffsetMax = "500 -70"
                    },
                    new CuiHorizontalLayoutGroupComponent
                    {
                        Spacing = 10f,
                        Padding = "0 0 0 0",
                        ChildAlignment = TextAnchor.MiddleLeft,
                        ChildForceExpandWidth = false,
                        ChildForceExpandHeight = true,
                        ChildControlWidth = false,
                        ChildControlHeight = true
                    }
                }
            });

            AddLayoutButton(container, Layer + ".Buttons", Layer + ".Button.AllItems",
                "UI_WipeBlock page 2", Msg(player, AllItems), current == 2);

            AddLayoutButton(container, Layer + ".Buttons", Layer + ".Button.UnlokedItems",
                "UI_WipeBlock page 1", Msg(player, UnlokedItems), current == 1);

            AddLayoutButton(container, Layer + ".Buttons", Layer + ".Button.BlockedItems",
                "UI_WipeBlock page 0", Msg(player, BlockedItems), current == 0);

            #endregion
        }

        private static void AddLayoutButton(CuiElementContainer container, string parent, string name,
            string command, string text, bool isActive)
        {
            var buttonWidth = CalcTextWidth(text.Length, 14, 15);

            container.Add(new CuiButton
            {
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "0 1",
                    OffsetMin = "0 0",
                    OffsetMax = $"{buttonWidth} 0"
                },
                Button =
                {
                    Command = command, Color = isActive ? HexToCuiColor("#D74933") : HexToCuiColor("#2F2F2F"),
                    Sprite = "assets/content/ui/ui.background.tile.psd",
                    Material = "assets/content/ui/namefontmaterial.mat"
                },
                Text =
                {
                    Text = text,
                    Align = TextAnchor.MiddleCenter,
                    Font = "robotocondensed-bold.ttf",
                    FontSize = 14,
                    Color = HexToCuiColor("#FFFFFF", 90)
                }
            }, parent, name);
        }

        public void DrawItems(BasePlayer player, CuiElementContainer container, int type = 2)
        {
            var scrollRect = new CuiRectTransform
            {
                AnchorMin = "0 1",
                AnchorMax = "1 1",
                OffsetMin = "0 0",
                OffsetMax = "0 0"
            };

            container.Add(new CuiElement
            {
                Name = Layer + ".Content",
                DestroyUi = Layer + ".Content",
                Parent = Layer,
                Components =
                {
                    new CuiImageComponent {Color = "0 0 0 0"},
                    new CuiScrollViewComponent
                    {
                        MovementType = ScrollRect.MovementType.Clamped,
                        Vertical = true,
                        Inertia = true,
                        Horizontal = false,
                        Elasticity = 0.25f,
                        DecelerationRate = 0.3f,
                        ScrollSensitivity = 24f,
                        ContentTransform = scrollRect,
                        VerticalScrollbar = new CuiScrollbar
                        {
                            AutoHide = true,
                            Size = 3,
                            HandleColor = HexToCuiColor("#D74933"),
                            HighlightColor = HexToCuiColor("#D74933"),
                            PressedColor = HexToCuiColor("#D74933"),
                            HandleSprite = "assets/content/ui/UI.Background.TileTex.psd",
                            TrackColor = HexToCuiColor("#38393F", 40),
                            TrackSprite = "assets/content/ui/UI.Background.TileTex.psd"
                        }
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "20 -570",
                        OffsetMax = "1185 -115"
                    }
                }
            });

            container.Add(new CuiElement
            {
                Parent = Layer + ".Content",
                Components =
                {
                    new CuiImageComponent {Color = HexToCuiColor("#000000", 0)},
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1"
                    }
                }
            });

            var HeaderHeight = 21f;
            var HeaderMarginY = 6f;

            var ItemHeight = 79f;
            var ItemMarginY = 9f;
            var ItemWidth = 79f;
            var ItemMarginX = 10f;
            var AmountOnString = 13f;


            var LastitemMarginY = 20f;

            var offsetY = 0f;
            var offsetX = 0f;

            #region Items

            var categories = Instance.GetItems(type);

            if (categories == null || categories.Count == 0)
                container.Add(new CuiLabel
                {
                    RectTransform =
                    {
                        AnchorMin = "0 0", AnchorMax = "1 1",
                        OffsetMin = "0 25", OffsetMax = "0 -85"
                    },
                    Text =
                    {
                        Text = Msg(player,
                            type == 0 ? TitleItemsUnlocked : type == 1 ? TitleItemsLocked : TitleItemsMissing),
                        Align = TextAnchor.MiddleCenter,
                        Font = "robotocondensed-bold.ttf",
                        FontSize = 20,
                        Color = "1 1 1 0.45"
                    }
                }, Layer + ".Content");
            else
                foreach (var category in categories)
                {
                    offsetX = 0;
                    container.Add(new CuiLabel
                    {
                        RectTransform =
                        {
                            AnchorMin = "0 1", AnchorMax = "0 1",
                            OffsetMin = $"{offsetX} {offsetY - HeaderHeight}",
                            OffsetMax = $"{offsetX + 600} {offsetY}"
                        },
                        Text =
                        {
                            Text = $"{Msg(player, category.Category)}",
                            Align = TextAnchor.MiddleLeft,
                            Font = "robotocondensed-bold.ttf",
                            FontSize = 14,
                            Color = "1 1 1 1"
                        }
                    }, Layer + ".Content");

                    offsetY = offsetY - HeaderHeight - HeaderMarginY;
                    offsetX = 0;

                    for (var i = 0; i < category.Items.Count; i++)
                    {
                        var item = category.Items[i];

                        TemplateRenderUniversal.DrawItem(player, container, item, type,
                            $"{offsetX} {offsetY - ItemHeight}",
                            $"{offsetX + ItemWidth} {offsetY}",
                            Layer + ".Content");


                        if ((i + 1) % AmountOnString == 0 && i + 1 != category.Items.Count)
                        {
                            offsetX = 0f;
                            offsetY = offsetY - ItemHeight - ItemMarginY;
                        }
                        else
                        {
                            offsetX = offsetX + ItemWidth + ItemMarginX;
                        }
                    }


                    offsetY = offsetY - ItemHeight - ItemMarginY - LastitemMarginY;
                }

            scrollRect.OffsetMin = $"0 {Math.Min(-455, offsetY)}";

            #endregion
        }
    }

    private class TemplateV1Renderer : ITemplateRenderer
    {
        public void Render(BasePlayer player, CuiElementContainer container)
        {
            DrawBackground(container, "UI.Server.Panel.Content", "UI.Server.Panel.Content.Plugin");

            DrawHeader(player, container, 2);

            DrawItems(player, container);
        }

        public void DrawBackground(CuiElementContainer container, string parent, string contentLayer)
        {
            container.Add(new CuiPanel
            {
                RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
                Image = {Color = "0 0 0 0"}
            }, parent, contentLayer, contentLayer);

            container.Add(new CuiPanel
            {
                RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
                Image = {Color = "0 0 0 0"}
            }, contentLayer, Layer, Layer);
        }

        public void DrawHeader(BasePlayer player, CuiElementContainer container, int current)
        {
            container.Add(new CuiElement
            {
                Name = Layer + ".Buttons",
                DestroyUi = Layer + ".Buttons",
                Parent = Layer,
                Components =
                {
                    new CuiImageComponent {Color = "0 0 0 0"},
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "40 -53",
                        OffsetMax = "500 -23"
                    },
                    new CuiHorizontalLayoutGroupComponent
                    {
                        Spacing = 10f,
                        Padding = "0 0 0 0",
                        ChildAlignment = TextAnchor.MiddleLeft,
                        ChildForceExpandWidth = false,
                        ChildForceExpandHeight = true,
                        ChildControlWidth = false,
                        ChildControlHeight = true
                    }
                }
            });

            AddLayoutButton(container, Layer + ".Buttons", Layer + ".Button.AllItems",
                "UI_WipeBlock page 2", Msg(player, AllItems), current == 2);

            AddLayoutButton(container, Layer + ".Buttons", Layer + ".Button.UnlokedItems",
                "UI_WipeBlock page 1", Msg(player, UnlokedItems), current == 1);

            AddLayoutButton(container, Layer + ".Buttons", Layer + ".Button.BlockedItems",
                "UI_WipeBlock page 0", Msg(player, BlockedItems), current == 0);
        }

        private static void AddLayoutButton(CuiElementContainer container, string parent, string name,
            string command, string text, bool isActive)
        {
            var buttonWidth = CalcTextWidth(text.Length, 14, 15);

            container.Add(new CuiButton
            {
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "0 1",
                    OffsetMin = "0 0",
                    OffsetMax = $"{buttonWidth} 0"
                },
                Button =
                {
                    Command = command, Color = isActive ? HexToCuiColor("#D74933") : HexToCuiColor("#2F2F2F"),
                    Sprite = "assets/content/ui/ui.background.tile.psd",
                    Material = "assets/content/ui/namefontmaterial.mat"
                },
                Text =
                {
                    Text = text,
                    Align = TextAnchor.MiddleCenter,
                    Font = "robotocondensed-bold.ttf",
                    FontSize = 14,
                    Color = HexToCuiColor("#FFFFFF", 90)
                }
            }, parent, name);
        }

        public void DrawItems(BasePlayer player, CuiElementContainer container, int type = 2)
        {
            var scrollRect = new CuiRectTransform
            {
                AnchorMin = "0 1",
                AnchorMax = "1 1",
                OffsetMin = "0 0",
                OffsetMax = "0 0"
            };

            container.Add(new CuiElement
            {
                Name = Layer + ".Content",
                DestroyUi = Layer + ".Content",
                Parent = Layer,
                Components =
                {
                    new CuiImageComponent {Color = "0 0 0 0"},
                    new CuiScrollViewComponent
                    {
                        MovementType = ScrollRect.MovementType.Clamped,
                        Vertical = true,
                        Inertia = true,
                        Horizontal = false,
                        Elasticity = 0.25f,
                        DecelerationRate = 0.3f,
                        ScrollSensitivity = 24f,
                        ContentTransform = scrollRect,
                        VerticalScrollbar = new CuiScrollbar
                        {
                            AutoHide = true,
                            Size = 3,
                            HandleColor = HexToCuiColor("#D74933"),
                            HighlightColor = HexToCuiColor("#D74933"),
                            PressedColor = HexToCuiColor("#D74933"),
                            HandleSprite = "assets/content/ui/UI.Background.TileTex.psd",
                            TrackColor = HexToCuiColor("#38393F", 40),
                            TrackSprite = "assets/content/ui/UI.Background.TileTex.psd"
                        }
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "40 -535",
                        OffsetMax = "1222 -63"
                    }
                }
            });

            container.Add(new CuiElement
            {
                Parent = Layer + ".Content",
                Components =
                {
                    new CuiImageComponent {Color = HexToCuiColor("#000000", 0)},
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1"
                    }
                }
            });

            var HeaderHeight = 21f;
            var HeaderMarginY = 6f;

            var ItemHeight = 79f;
            var ItemMarginY = 9f;
            var ItemWidth = 79f;
            var ItemMarginX = 10f;
            var AmountOnString = 13f;


            var LastitemMarginY = 20f;

            var offsetY = 0f;
            var offsetX = 0f;

            #region Items

            var categories = Instance.GetItems(type);

            if (categories == null || categories.Count == 0)
                container.Add(new CuiLabel
                {
                    RectTransform =
                    {
                        AnchorMin = "0 0", AnchorMax = "1 1",
                        OffsetMin = "0 25", OffsetMax = "0 -85"
                    },
                    Text =
                    {
                        Text = Msg(player,
                            type == 0 ? TitleItemsUnlocked : type == 1 ? TitleItemsLocked : TitleItemsMissing),
                        Align = TextAnchor.MiddleCenter,
                        Font = "robotocondensed-bold.ttf",
                        FontSize = 20,
                        Color = "1 1 1 0.45"
                    }
                }, Layer + ".Content");
            else
                foreach (var category in categories)
                {
                    offsetX = 0;
                    container.Add(new CuiLabel
                    {
                        RectTransform =
                        {
                            AnchorMin = "0 1", AnchorMax = "0 1",
                            OffsetMin = $"{offsetX} {offsetY - HeaderHeight}",
                            OffsetMax = $"{offsetX + 600} {offsetY}"
                        },
                        Text =
                        {
                            Text = $"{Msg(player, category.Category)}",
                            Align = TextAnchor.MiddleLeft,
                            Font = "robotocondensed-bold.ttf",
                            FontSize = 14,
                            Color = "1 1 1 1"
                        }
                    }, Layer + ".Content");

                    offsetY = offsetY - HeaderHeight - HeaderMarginY;
                    offsetX = 0;

                    for (var i = 0; i < category.Items.Count; i++)
                    {
                        var item = category.Items[i];

                        TemplateRenderUniversal.DrawItem(player, container, item, type,
                            $"{offsetX} {offsetY - ItemHeight}",
                            $"{offsetX + ItemWidth} {offsetY}",
                            Layer + ".Content");


                        if ((i + 1) % AmountOnString == 0 && i + 1 != category.Items.Count)
                        {
                            offsetX = 0f;
                            offsetY = offsetY - ItemHeight - ItemMarginY;
                        }
                        else
                        {
                            offsetX = offsetX + ItemWidth + ItemMarginX;
                        }
                    }


                    offsetY = offsetY - ItemHeight - ItemMarginY - LastitemMarginY;
                }

            scrollRect.OffsetMin = $"0 {Math.Min(-472, offsetY)}";

            #endregion
        }
    }

    private class TemplateV2Renderer : ITemplateRenderer
    {
        public void Render(BasePlayer player, CuiElementContainer container)
        {
            DrawBackground(container, "UI.Server.Panel.Content", "UI.Server.Panel.Content.Plugin");

            DrawHeader(player, container, 2);

            DrawItems(player, container);
        }

        public void DrawBackground(CuiElementContainer container, string parent, string contentLayer)
        {
            container.Add(new CuiPanel
            {
                RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
                Image = {Color = "0 0 0 0"}
            }, parent, contentLayer, contentLayer);

            container.Add(new CuiPanel
            {
                RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
                Image = {Color = "0 0 0 0"}
            }, contentLayer, Layer, Layer);
        }

        public void DrawHeader(BasePlayer player, CuiElementContainer container, int current)
        {
            container.Add(new CuiElement
            {
                Name = Layer + ".Header",
                DestroyUi = Layer + ".Header",
                Parent = Layer,
                Components =
                {
                    new CuiImageComponent {Color = HexToCuiColor("#000000", 0)},
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "0 -122",
                        OffsetMax = "919 -22"
                    }
                }
            });

            container.Add(new CuiElement
            {
                Parent = Layer + ".Header",
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = Msg(player, TitleMenu), Font = "robotocondensed-bold.ttf", FontSize = 32,
                        Align = TextAnchor.UpperLeft, Color = HexToCuiColor("#CF432D", 90)
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "40 -40",
                        OffsetMax = "919 0"
                    }
                }
            });

            container.Add(new CuiElement
            {
                Parent = Layer + ".Header",
                Components =
                {
                    new CuiImageComponent {Color = HexToCuiColor("#373737", 50)},
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "40 -41",
                        OffsetMax = "919 -40"
                    }
                }
            });

            container.Add(new CuiElement
            {
                Name = Layer + ".Buttons",
                DestroyUi = Layer + ".Buttons",
                Parent = Layer + ".Header",
                Components =
                {
                    new CuiImageComponent {Color = "0 0 0 0"},
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "40 -94",
                        OffsetMax = "500 -70"
                    },
                    new CuiHorizontalLayoutGroupComponent
                    {
                        Spacing = 10f,
                        Padding = "0 0 0 0",
                        ChildAlignment = TextAnchor.MiddleLeft,
                        ChildForceExpandWidth = false,
                        ChildForceExpandHeight = true,
                        ChildControlWidth = false,
                        ChildControlHeight = true
                    }
                }
            });

            AddLayoutButton(container, Layer + ".Buttons", Layer + ".Button.AllItems",
                "UI_WipeBlock page 2", Msg(player, AllItems), current == 2);

            AddLayoutButton(container, Layer + ".Buttons", Layer + ".Button.UnlokedItems",
                "UI_WipeBlock page 1", Msg(player, UnlokedItems), current == 1);

            AddLayoutButton(container, Layer + ".Buttons", Layer + ".Button.BlockedItems",
                "UI_WipeBlock page 0", Msg(player, BlockedItems), current == 0);
        }

        private static void AddLayoutButton(CuiElementContainer container, string parent, string name,
            string command, string text, bool isActive)
        {
            var buttonWidth = CalcTextWidth(text.Length, 14, 15);

            container.Add(new CuiButton
            {
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "0 1",
                    OffsetMin = "0 0",
                    OffsetMax = $"{buttonWidth} 0"
                },
                Button =
                {
                    Command = command, Color = isActive ? HexToCuiColor("#D74933") : HexToCuiColor("#2F2F2F"),
                    Sprite = "assets/content/ui/ui.background.tile.psd",
                    Material = "assets/content/ui/namefontmaterial.mat"
                },
                Text =
                {
                    Text = text,
                    Align = TextAnchor.MiddleCenter,
                    Font = "robotocondensed-bold.ttf",
                    FontSize = 14,
                    Color = HexToCuiColor("#FFFFFF", 90)
                }
            }, parent, name);
        }

        public void DrawItems(BasePlayer player, CuiElementContainer container, int type = 2)
        {
            var scrollRect = new CuiRectTransform
            {
                AnchorMin = "0 1",
                AnchorMax = "1 1",
                OffsetMin = "0 0",
                OffsetMax = "0 0"
            };

            container.Add(new CuiElement
            {
                Name = Layer + ".Content",
                DestroyUi = Layer + ".Content",
                Parent = Layer,
                Components =
                {
                    new CuiImageComponent {Color = "0 0 0 0"},
                    new CuiScrollViewComponent
                    {
                        MovementType = ScrollRect.MovementType.Clamped,
                        Vertical = true,
                        Inertia = true,
                        Horizontal = false,
                        Elasticity = 0.25f,
                        DecelerationRate = 0.3f,
                        ScrollSensitivity = 24f,
                        ContentTransform = scrollRect,
                        VerticalScrollbar = new CuiScrollbar
                        {
                            AutoHide = true,
                            Size = 3,
                            HandleColor = HexToCuiColor("#D74933"),
                            HighlightColor = HexToCuiColor("#D74933"),
                            PressedColor = HexToCuiColor("#D74933"),
                            HandleSprite = "assets/content/ui/UI.Background.TileTex.psd",
                            TrackColor = HexToCuiColor("#38393F", 40),
                            TrackSprite = "assets/content/ui/UI.Background.TileTex.psd"
                        }
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "40 -627",
                        OffsetMax = "955 -130"
                    }
                }
            });

            container.Add(new CuiElement
            {
                Parent = Layer + ".Content",
                Components =
                {
                    new CuiImageComponent {Color = HexToCuiColor("#000000", 0)},
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1"
                    }
                }
            });

            var HeaderHeight = 21f;
            var HeaderMarginY = 6f;

            var ItemHeight = 79f;
            var ItemMarginY = 9f;
            var ItemWidth = 79f;
            var ItemMarginX = 10f;
            var AmountOnString = 10f;


            var LastitemMarginY = 20f;

            var offsetY = 0f;
            var offsetX = 0f;

            #region Items

            var categories = Instance.GetItems(type);

            if (categories == null || categories.Count == 0)
                container.Add(new CuiLabel
                {
                    RectTransform =
                    {
                        AnchorMin = "0 0", AnchorMax = "1 1",
                        OffsetMin = "0 25", OffsetMax = "0 -85"
                    },
                    Text =
                    {
                        Text = Msg(player,
                            type == 0 ? TitleItemsUnlocked : type == 1 ? TitleItemsLocked : TitleItemsMissing),
                        Align = TextAnchor.MiddleCenter,
                        Font = "robotocondensed-bold.ttf",
                        FontSize = 20,
                        Color = "1 1 1 0.45"
                    }
                }, Layer + ".Content");
            else
                foreach (var category in categories)
                {
                    offsetX = 0;
                    container.Add(new CuiLabel
                    {
                        RectTransform =
                        {
                            AnchorMin = "0 1", AnchorMax = "0 1",
                            OffsetMin = $"{offsetX} {offsetY - HeaderHeight}",
                            OffsetMax = $"{offsetX + 600} {offsetY}"
                        },
                        Text =
                        {
                            Text = $"{Msg(player, category.Category)}",
                            Align = TextAnchor.MiddleLeft,
                            Font = "robotocondensed-bold.ttf",
                            FontSize = 14,
                            Color = "1 1 1 1"
                        }
                    }, Layer + ".Content");

                    offsetY = offsetY - HeaderHeight - HeaderMarginY;
                    offsetX = 0;

                    for (var i = 0; i < category.Items.Count; i++)
                    {
                        var item = category.Items[i];

                        TemplateRenderUniversal.DrawItem(player, container, item, type,
                            $"{offsetX} {offsetY - ItemHeight}",
                            $"{offsetX + ItemWidth} {offsetY}",
                            Layer + ".Content");


                        if ((i + 1) % AmountOnString == 0 && i + 1 != category.Items.Count)
                        {
                            offsetX = 0f;
                            offsetY = offsetY - ItemHeight - ItemMarginY;
                        }
                        else
                        {
                            offsetX = offsetX + ItemWidth + ItemMarginX;
                        }
                    }


                    offsetY = offsetY - ItemHeight - ItemMarginY - LastitemMarginY;
                }

            scrollRect.OffsetMin = $"0 {Math.Min(-497, offsetY)}";

            #endregion
        }
    }

    private class TemplateV4Renderer : ITemplateRenderer
    {
        public void Render(BasePlayer player, CuiElementContainer container)
        {
            DrawBackground(container, "UI.Server.Panel.Content", "UI.Server.Panel.Content.Plugin");

            DrawHeader(player, container, 2);

            DrawItems(player, container);
        }

        public void DrawBackground(CuiElementContainer container, string parent, string contentLayer)
        {
            container.Add(new CuiPanel
            {
                RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
                Image = {Color = "0 0 0 0"}
            }, parent, contentLayer, contentLayer);

            container.Add(new CuiPanel
            {
                RectTransform = {AnchorMin = "0 0", AnchorMax = "1 1"},
                Image = {Color = "0 0 0 0"}
            }, contentLayer, Layer, Layer);
        }

        public void DrawHeader(BasePlayer player, CuiElementContainer container, int current)
        {
            container.Add(new CuiElement
            {
                Name = Layer + ".Header",
                DestroyUi = Layer + ".Header",
                Parent = Layer,
                Components =
                {
                    new CuiImageComponent {Color = "0 0 0 0"},
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "40 -53",
                        OffsetMax = "500 -23"
                    },
                    new CuiHorizontalLayoutGroupComponent
                    {
                        Spacing = 10f,
                        Padding = "0 0 0 0",
                        ChildAlignment = TextAnchor.MiddleLeft,
                        ChildForceExpandWidth = false,
                        ChildForceExpandHeight = true,
                        ChildControlWidth = false,
                        ChildControlHeight = true
                    }
                }
            });

            AddLayoutButton(container, Layer + ".Header", Layer + ".Button.AllItems",
                "UI_WipeBlock page 2", Msg(player, AllItems), current == 2);

            AddLayoutButton(container, Layer + ".Header", Layer + ".Button.UnlokedItems",
                "UI_WipeBlock page 1", Msg(player, UnlokedItems), current == 1);

            AddLayoutButton(container, Layer + ".Header", Layer + ".Button.BlockedItems",
                "UI_WipeBlock page 0", Msg(player, BlockedItems), current == 0);
        }

        private static void AddLayoutButton(CuiElementContainer container, string parent, string name,
            string command, string text, bool isActive)
        {
            var buttonWidth = CalcTextWidth(text.Length, 14, 15f);

            container.Add(new CuiButton
            {
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "0 1",
                    OffsetMin = "0 0",
                    OffsetMax = $"{buttonWidth} 0"
                },
                Button =
                {
                    Command = command,
                    Color = isActive ? HexToCuiColor("#5D7238") : HexToCuiColor("#222222"),
                    Sprite = "assets/content/ui/ui.background.tile.psd",
                    Material = "assets/content/ui/namefontmaterial.mat"
                },
                Text =
                {
                    Text = text,
                    Align = TextAnchor.MiddleCenter,
                    Font = "robotocondensed-bold.ttf",
                    FontSize = 14,
                    Color = HexToCuiColor("#FFFFFF", 80)
                }
            }, parent, name);
        }

        public void DrawItems(BasePlayer player, CuiElementContainer container, int type = 2)
        {
            var scrollRect = new CuiRectTransform
            {
                AnchorMin = "0 1",
                AnchorMax = "1 1",
                OffsetMin = "0 0",
                OffsetMax = "0 0"
            };

            container.Add(new CuiElement
            {
                Name = Layer + ".Content",
                DestroyUi = Layer + ".Content",
                Parent = Layer,
                Components =
                {
                    new CuiImageComponent {Color = "0 0 0 0"},
                    new CuiScrollViewComponent
                    {
                        MovementType = ScrollRect.MovementType.Clamped,
                        Vertical = true,
                        Inertia = true,
                        Horizontal = false,
                        Elasticity = 0.25f,
                        DecelerationRate = 0.3f,
                        ScrollSensitivity = 24f,
                        ContentTransform = scrollRect,
                        VerticalScrollbar = new CuiScrollbar
                        {
                            AutoHide = true,
                            Size = 3,
                            HandleColor = HexToCuiColor("#D74933"),
                            HighlightColor = HexToCuiColor("#D74933"),
                            PressedColor = HexToCuiColor("#D74933"),
                            HandleSprite = "assets/content/ui/UI.Background.TileTex.psd",
                            TrackColor = HexToCuiColor("#38393F", 40),
                            TrackSprite = "assets/content/ui/UI.Background.TileTex.psd"
                        }
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 1",
                        AnchorMax = "0 1",
                        OffsetMin = "40 -545",
                        OffsetMax = "1222 -65"
                    }
                }
            });

            container.Add(new CuiElement
            {
                Parent = Layer + ".Content",
                Components =
                {
                    new CuiImageComponent {Color = HexToCuiColor("#000000", 0)},
                    new CuiRectTransformComponent
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1"
                    }
                }
            });

            var HeaderHeight = 21f;
            var HeaderMarginY = 6f;

            var ItemHeight = 79f;
            var ItemMarginY = 9f;
            var ItemWidth = 79f;
            var ItemMarginX = 10f;
            var AmountOnString = 13f;


            var LastitemMarginY = 20f;

            var offsetY = 0f;
            var offsetX = 0f;

            #region Items

            var categories = Instance.GetItems(type);

            if (categories == null || categories.Count == 0)
                container.Add(new CuiLabel
                {
                    RectTransform =
                    {
                        AnchorMin = "0 0", AnchorMax = "1 1",
                        OffsetMin = "0 25", OffsetMax = "0 -85"
                    },
                    Text =
                    {
                        Text = Msg(player,
                            type == 0 ? TitleItemsUnlocked : type == 1 ? TitleItemsLocked : TitleItemsMissing),
                        Align = TextAnchor.MiddleCenter,
                        Font = "robotocondensed-bold.ttf",
                        FontSize = 20,
                        Color = "1 1 1 0.45"
                    }
                }, Layer + ".Content");
            else
                foreach (var category in categories)
                {
                    offsetX = 0;
                    container.Add(new CuiLabel
                    {
                        RectTransform =
                        {
                            AnchorMin = "0 1", AnchorMax = "0 1",
                            OffsetMin = $"{offsetX} {offsetY - HeaderHeight}",
                            OffsetMax = $"{offsetX + 600} {offsetY}"
                        },
                        Text =
                        {
                            Text = $"{Msg(player, category.Category)}",
                            Align = TextAnchor.MiddleLeft,
                            Font = "robotocondensed-bold.ttf",
                            FontSize = 14,
                            Color = "1 1 1 1"
                        }
                    }, Layer + ".Content");

                    offsetY = offsetY - HeaderHeight - HeaderMarginY;
                    offsetX = 0;

                    for (var i = 0; i < category.Items.Count; i++)
                    {
                        var item = category.Items[i];

                        TemplateRenderUniversal.DrawItem(player, container, item, type,
                            $"{offsetX} {offsetY - ItemHeight}",
                            $"{offsetX + ItemWidth} {offsetY}",
                            Layer + ".Content");


                        if ((i + 1) % AmountOnString == 0 && i + 1 != category.Items.Count)
                        {
                            offsetX = 0f;
                            offsetY = offsetY - ItemHeight - ItemMarginY;
                        }
                        else
                        {
                            offsetX = offsetX + ItemWidth + ItemMarginX;
                        }
                    }


                    offsetY = offsetY - ItemHeight - ItemMarginY - LastitemMarginY;
                }

            scrollRect.OffsetMin = $"0 {Math.Min(-480, offsetY)}";

            #endregion
        }
    }

    #region Helpers

    private static int CalcTextWidth(int length, int fontSize, float padding = 0)
    {
        return Mathf.CeilToInt(length * fontSize * 0.5f + padding * 2) + 1;
    }

    private void UpdateTemplateRenderer(string serverPanelTemplate = null)
    {
        var oldPattern = _config.Pattern;

        if (_serverPanelCategory.spStatus && _serverPanelCategory.categoryID != -1)
        {
            serverPanelTemplate ??= string.Empty;

            _config.Pattern = serverPanelTemplate switch
            {
                "t1" or "t1_1" => PatternServerMenu.V1,
                "t2" => PatternServerMenu.V2,
                "t4" => PatternServerMenu.V4,
                _ => PatternServerMenu.Fullscreen
            };
        }
        else
        {
            _config.Pattern = PatternServerMenu.Fullscreen;
        }

        if (oldPattern != _config.Pattern)
        {
            SaveConfig();

            LoadTemplate();
        }
    }

    private void LoadTemplate()
    {
        switch (_config.Pattern)
        {
            case PatternServerMenu.V1:
                templateRenderer = new TemplateV1Renderer();
                return;
            case PatternServerMenu.V2:
                templateRenderer = new TemplateV2Renderer();
                return;
            case PatternServerMenu.V4:
                templateRenderer = new TemplateV4Renderer();
                return;
            default:
                templateRenderer = new TemplateFullscreenRenderer();
                return;
        }
    }

    private void LoadServerPanel()
    {
        _serverPanelCategory.spStatus = ServerPanel is {IsLoaded: true};
        _serverPanelCategory.categoryID = -1;

        if (_serverPanelCategory.spStatus)
        {
            var categoryInfo = ServerPanel?.Call("API_OnServerPanelGetCategoryInfo", Name);
            if (categoryInfo == null)
                PrintWarning("You are using an old version of ServerPanel. Please update to the latest version.");

            if (categoryInfo != null && categoryInfo is (int categoryID, string template))
            {
                _serverPanelCategory.categoryID = categoryID;

                UpdateTemplateRenderer(template);
            }
            else
            {
                UpdateTemplateRenderer();
            }
        }
        else
        {
            UpdateTemplateRenderer();
        }
    }

    private static void UpdateUI(BasePlayer player, Action<CuiElementContainer> callback)
    {
        if (player == null) return;

        var container = Pool.Get<CuiElementContainer>();

        callback?.Invoke(container);

        CuiHelper.AddUi(player, container);

        FreeUnmanaged(ref container);
    }

    private static void FreeUnmanaged(ref CuiElementContainer obj)
    {
        if (obj == null) throw new ArgumentNullException();

        obj.Clear();
        Pool.FreeUnsafe(ref obj);
    }

    private CuiElementContainer API_OpenPlugin(BasePlayer player)
    {
        var container = new CuiElementContainer();

        templateRenderer?.Render(player, container);

        return container;
    }

    private static string HexToCuiColor(string hex, float alpha = 100)
    {
        if (string.IsNullOrEmpty(hex)) hex = "#FFFFFF";

        var str = hex.Trim('#');
        if (str.Length != 6) throw new Exception(hex);
        var r = byte.Parse(str.Substring(0, 2), NumberStyles.HexNumber);
        var g = byte.Parse(str.Substring(2, 2), NumberStyles.HexNumber);
        var b = byte.Parse(str.Substring(4, 2), NumberStyles.HexNumber);

        return $"{(double) r / 255} {(double) g / 255} {(double) b / 255} {alpha / 100f}";
    }

    #endregion

    #endregion

    #region Utils

    private void UnsubscribeHooks()
    {
        if (!_config.BlockUse)
        {
            Unsubscribe(nameof(OnMagazineReload));
            Unsubscribe(nameof(OnWeaponReload));
            Unsubscribe(nameof(CanAcceptItem));
            Unsubscribe(nameof(CanMoveItem));
            Unsubscribe(nameof(CanEquipItem));
            Unsubscribe(nameof(CanWearItem));
        }

        if (!_config.BlockCraft) Unsubscribe(nameof(OnItemCraft));
    }

    private void ReturnItems(ItemCraftTask task, BasePlayer player)
    {
        task.cancelled = true;
        foreach (var item in task.takenItems)
            if (item.amount > 0)
                player.GiveItem(item);
    }

    private void FillingItems()
    {
        _config.Categories.ForEach(category =>
        {
            foreach (var check in category.Items)
                check.Value.ForEach(item =>
                {
                    CooldownByItems[item] = check.Key;
                    ItemsAll.Add(item);

                    if (!ItemsByCooldown.ContainsKey(check.Key))
                        ItemsByCooldown.Add(check.Key, new List<ItemConf>());

                    if (!ItemsByCooldown[check.Key].Contains(item))
                        ItemsByCooldown[check.Key].Add(item);
                });
        });
    }

    private List<ItemsData> GetItems(int type)
    {
        var list = new List<ItemsData>();

        _config.Categories.ForEach(category =>
        {
            var data = new ItemsData
            {
                Category = category.LangKey,
                Items = new List<ItemConf>()
            };

            foreach (var items in category.Items.Values)
                items.ForEach(item =>
                {
                    switch (type)
                    {
                        case 0:
                        {
                            if (IsBlocked(item))
                                data.Items.Add(item);
                            break;
                        }
                        case 1:
                        {
                            if (!IsBlocked(item))
                                data.Items.Add(item);
                            break;
                        }
                        case 2:
                        {
                            data.Items.Add(item);
                            break;
                        }
                    }
                });

            if (data.Items.Count > 0)
                list.Add(data);
        });

        return list;
    }

    private bool IsValid(BasePlayer player)
    {
        return player != null && player.userID.IsSteamId() &&
               !permission.UserHasPermission(player.UserIDString, IgnorePermission);
    }

    private void CheckActive()
    {
        if (ItemsAll.Exists(IsBlocked))
        {
            anyBlocked = true;

            SubscribeHooks(true);
        }
        else
        {
            anyBlocked = false;

            SubscribeHooks(false);

            foreach (var player in BasePlayer.activePlayerList)
                CuiHelper.DestroyUi(player, ScreenLayer);

            Interface.Oxide.CallHook("OnWipeBlockEnded");
        }
    }

    private void SubscribeHooks(bool subscribe)
    {
        var action =
            subscribe
                ? Subscribe
                : new Action<string>(Unsubscribe);

        action(nameof(CanWearItem));
        action(nameof(CanEquipItem));
        action(nameof(OnWeaponReload));
        action(nameof(OnMagazineReload));
        action(nameof(CanAcceptItem));
        action(nameof(CanMoveItem));
    }

    private int GetStartPage()
    {
        return anyBlocked ? 0 : 1;
    }

    private void CheckPlayers()
    {
        if (!_config.BlockUse) return;

        foreach (var player in BasePlayer.activePlayerList)
        {
            player.inventory.containerBelt.itemList.ToList().ForEach(item => CheckBlockedItem(player, item));
            player.inventory.containerWear.itemList.ToList().ForEach(item => CheckBlockedItem(player, item));
        }
    }

    private void CheckBlockedItem(BasePlayer player, Item item)
    {
        if (!IsBlocked(item.info)) return;

        SendNotify(player, ItemLocked, 1);

        if (item.MoveToContainer(player.inventory.containerMain))
            player.Command("note.inv", item.info.itemid, item.amount,
                !string.IsNullOrEmpty(item.name) ? item.name : string.Empty,
                (int) BaseEntity.GiveItemReason.PickedUp);
        else
            item.Drop(player.inventory.containerMain.dropPosition,
                player.inventory.containerMain.dropVelocity);
    }

    private bool IsItemBlocked(Item item, ItemContainer container)
    {
        return (container.entityOwner is AutoTurret || container.entityOwner is DroneStorage ||
                IsContainerMLRS(container.entityOwner)) &&
               IsBlocked(item.info.shortname, item.skin);
    }

    private bool IsItemBlocked(ItemDefinition item, int skinId)
    {
        return IsBlocked(item.shortname, (ulong) skinId);
    }

    private bool IsContainerMLRS(BaseEntity owner)
    {
        return owner is StorageContainer storageContainer && storageContainer.parentEntity.IsValid(true) &&
               storageContainer.parentEntity.Get(true) is MLRS;
    }

    #endregion

    #region WIPE DateTime

    private DateTime WipeTime;

    private void SaveWipeTime()
    {
        Interface.Oxide.DataFileSystem.WriteObject(Name + Path.DirectorySeparatorChar + "wipe", WipeTime);
    }

    private void LoadWipeTime()
    {
        WipeTime = Interface.Oxide.DataFileSystem.ReadObject<DateTime>(Name + Path.DirectorySeparatorChar + "wipe");

        if (WipeTime == DateTime.MinValue)
        {
            WipeTime = SaveRestore.SaveCreatedTime.ToUniversalTime();
            SaveWipeTime();
        }
    }

    #endregion

    #region API

    private bool AnyBlocked()
    {
        return anyBlocked;
    }

    private int SecondsFromWipe()
    {
        return (int) DateTime.UtcNow
            .Subtract(WipeTime.AddSeconds(_config.Indent)).TotalSeconds;
    }

    private bool IsBlocked(ItemDefinition def)
    {
        return IsBlocked(def.shortname);
    }

    private bool IsBlocked(string shortName, ulong skin = 0)
    {
        var item = ItemsAll.Find(x => x.ShortName == shortName && (x.Skin == 0 || x.Skin == skin));
        return item != null && IsBlocked(item);
    }

    private bool IsBlocked(ItemConf item)
    {
        return CooldownByItems.ContainsKey(item) && IsBlocked(CooldownByItems[item]);
    }

    private bool IsBlocked(int cooldown)
    {
        return SecondsFromWipe() < cooldown;
    }

    private int LeftTime(string shortName, ulong skin = 0)
    {
        var item = ItemsAll.Find(x => x.ShortName == shortName && (skin == 0 || x.Skin == skin));
        return item != null ? LeftTime(CooldownByItems[item]) : 0;
    }

    private int LeftTime(ItemConf item)
    {
        return LeftTime(CooldownByItems[item]);
    }

    private int LeftTime(int cooldown)
    {
        var seconds = cooldown - SecondsFromWipe();
        return seconds < 0 ? 0 : seconds;
    }

    private string GetGradient(ItemConf item)
    {
        var percent = Mathf.CeilToInt((float) LeftTime(item) / CooldownByItems[item] * 100f);
        return percent >= 0 && _config.UI.Gradients.Count > percent
            ? _config.UI.Gradients[percent]
            : _config.UI.Gradients.LastOrDefault();
    }

    #endregion

    #region Lang

    private const string
        ItemWasUnlocked = "ItemWasUnlocked",
        CloseButton = "CloseButton",
        TitleMenu = "TitleMenu2",
        AllItems = "AllItems2",
        BlockedItems = "BlockItems2",
        UnlokedItems = "UnlockedItems2",
        TitleItemsUnlocked = "TitleItemsUnlocked",
        TitleItemsLocked = "TitleItemsLocked",
        TitleItemsMissing = "TitleItemsMissing",
        ItemLocked = "ItemLocked",
        AmmoLocked = "AmmoLocked",
        OnScreenTitle = "OnScreenTitle",
        OnScreenDescription = "OnScreenDescription",
        ItemBlockedCraft = "ItemBlockedCraft";

    protected override void LoadDefaultMessages()
    {
        lang.RegisterMessages(new Dictionary<string, string>
        {
            [CloseButton] = "✕",
            [TitleMenu] = "WIPE BLOCK",
            [AllItems] = "ALL ITEMS",
            [BlockedItems] = "BLOCKED ITEMS",
            [UnlokedItems] = "UNLOCKED ITEMS",
            [TitleItemsUnlocked] = "All items are unlocked!",
            [TitleItemsLocked] = "All items are locked :(",
            [TitleItemsMissing] = "Items missing :(",
            [ItemLocked] = "This item is currently locked!",
            [AmmoLocked] = "You cannot use this ammo type yet!",
            [OnScreenTitle] = "BLOCK AFTER WIPE",
            [OnScreenDescription] = "Some items are locked!",
            [ItemWasUnlocked] = "'{0}' has been unlocked!",
            [ItemBlockedCraft] = "This item cannot be crafted yet!",
            ["Weapons"] = "Weapons",
            ["Explosives"] = "Explosives",
            ["Attire"] = "Attire"
        }, this);

        lang.RegisterMessages(new Dictionary<string, string>
        {
            [CloseButton] = "✕",
            [TitleMenu] = "БЛОКИРОВКА ПОСЛЕ ВАЙПА",
            [AllItems] = "ВСЕ",
            [BlockedItems] = "ЗАПРЕЩЕНО",
            [UnlokedItems] = "РАЗРЕШЕНО",
            [TitleItemsUnlocked] = "Все предметы разблокированы!",
            [TitleItemsLocked] = "Все предметы заблокированы :(",
            [TitleItemsMissing] = "Предметы отсутствуют :(",
            [ItemLocked] = "Предмет, который вы хотите взять, заблокирован",
            [AmmoLocked] = "Вы не можете использовать этот тип боеприпасов!",
            [OnScreenTitle] = "ВАЙП БЛОК",
            [OnScreenDescription] = "Есть запрещенные предметы",
            [ItemWasUnlocked] = "'{0}' был разблокирован!",
            [ItemBlockedCraft] = "Этот предмет нельзя скрафтить еще",
            ["Weapons"] = "Оружие",
            ["Explosives"] = "Взрывчатка",
            ["Attire"] = "Амуниция"
        }, this, "ru");
    }

    private static string Msg(BasePlayer player, string key, params object[] obj)
    {
        return string.Format(Instance.lang.GetMessage(key, Instance, player.UserIDString), obj);
    }

    private void Reply(BasePlayer player, string key, params object[] obj)
    {
        SendReply(player, Msg(player, key, obj));
    }

    private void SendNotify(BasePlayer player, string key, int type, params object[] obj)
    {
        if (_config.UseNotify && (Notify != null || UINotify != null))
            Interface.Oxide.CallHook("SendNotify", player, type, Msg(player, key, obj));
        else
            Reply(player, key, obj);
    }

    #endregion

    #region Unlock Items

    private readonly List<string> UnlockedItems = new();

    private Timer UnlockTimer;

    private void UnlockItemsController()
    {
        UnlockTimer?.Destroy();

        var blockedItems = Pool.Get<List<int>>();
        try
        {
            foreach (var item in ItemsByCooldown)
                if (IsBlocked(item.Key))
                    blockedItems.Add(item.Key);

            if (blockedItems.Count == 0) return;

            blockedItems.Sort((a, b) => LeftTime(a).CompareTo(LeftTime(b)));

            var minLeftTime = blockedItems[0];

            var leftTime = LeftTime(minLeftTime);

            if (leftTime == 0) return;

            UnlockTimer = timer.In(leftTime + 1, () =>
            {
                if (ItemsByCooldown.TryGetValue(minLeftTime, out var items))
                    items.ForEach(item => UnlockItem(item.ShortName));

                UnlockItemsController();

                CheckActive();
            });
        }
        finally
        {
            Pool.FreeUnmanaged(ref blockedItems);
        }
    }

    private void UnlockItem(string item)
    {
        if (UnlockedItems.Contains(item))
            return;

        UnlockedItems.Add(item);

        var name = ItemManager.FindItemDefinition(item).displayName.english;

        foreach (var player in BasePlayer.activePlayerList)
            if (permission.UserHasPermission(player.UserIDString, UnlockNotifyPermission))
                SendNotify(player, Msg(player, ItemWasUnlocked, Msg(player, name)), 0);
    }

    #endregion
}
