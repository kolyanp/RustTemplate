using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SyringeBoost", "kolyan", "1.1.4")]
    [Description("Модифицированные шприцы")]
    public class SyringeBoost : RustPlugin
    {

        #region Configuration
        private static Configuration config = new Configuration();
        private class Configuration
        {
            internal class Syringe
            {
                [JsonProperty("Отображаемое имя")]
                public string DisplayName;
                [JsonProperty("Скин шприца")]
                public ulong skinid;
                [JsonProperty("Название предмета который он будет заменять")]
                public string ReplaceShortName;
                [JsonProperty("Шанс выпадения")]
                public int DropChance;
                [JsonProperty("Параметры (Не менять)")]
                public float Parameter;
                [JsonProperty("Ящик в котором будет падать этот шприц")]
                public List<string> crate;
                

                public class Setings
                {
                    [JsonProperty("Время действия шприца от холода")]
                    public int SyringeColdTime;
                }

                public Item Copy(int amount )
                {
                    Item x = ItemManager.CreateByPartialName(ReplaceShortName, amount);
                    x.skin = skinid;
                    x.name = DisplayName;

                    return x;
                }

                public void CreateItem(BasePlayer player, Vector3 position, int amount)
                {
                    Item x = ItemManager.CreateByPartialName(ReplaceShortName, amount);
                    x.skin = skinid;
                    x.name = DisplayName;

                    if (player != null)
                    {
                        if (player.inventory.containerMain.itemList.Count < 24)
                            x.MoveToContainer(player.inventory.containerMain);
                        else
                            x.Drop(player.transform.position, Vector3.zero);
                        return;
                    }

                    if (position != Vector3.zero)
                    {
                        x.Drop(position, Vector3.down);
                        return;
                    }
                }
            }

            [JsonProperty("Шприцы")]
            public Dictionary<string, Syringe> syringe = new Dictionary<string, Syringe>();

            [JsonProperty("Настройки")]
            public Syringe.Setings setings;


            public static Configuration GetNewConfiguration()
            {
                return new Configuration
                {
                    setings = new Syringe.Setings
                    {
                        SyringeColdTime = 10,
                    },
                    syringe = new Dictionary<string, Syringe>
                    {
                        ["heal"] = new Syringe
                        {
                            DisplayName = "Full Heal",
                            ReplaceShortName = "syringe.medical",
                            skinid = 1977071162,
                            DropChance = 50,
                            Parameter = 100f,
                            crate = new List<string>
                              {
                                  "foodbox"
                              }
                        },
                        ["rad"] = new Syringe
                        {
                            DisplayName = "AntiRadiation",
                            ReplaceShortName = "syringe.medical",
                            skinid = 1977071544,
                            DropChance = 50,
                            Parameter = 0f,
                            crate = new List<string>
                              {
                                  "foodbox"
                              }
                        },
                        ["cold"] = new Syringe
                        {
                            DisplayName = "AntiCold",
                            ReplaceShortName = "syringe.medical",
                            skinid = 1977071907,
                            DropChance = 50,
                            Parameter = 0.5f,
                            crate = new List<string>
                              {
                                  "foodbox"
                              }
                        }
                    }
                };

            }
        }


        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) LoadDefaultConfig();
            }
            catch
            {
                PrintWarning("Ошибка#1973 чтения конфигурации 'oxide/config/', создаём новую конфигурацию!!");
                LoadDefaultConfig();
            }

            NextTick(SaveConfig);
        }
        
        protected override void LoadDefaultConfig() => config = Configuration.GetNewConfiguration();
        protected override void SaveConfig() => Config.WriteObject(config);

        #endregion

        [ChatCommand("go")]
        void goch (BasePlayer player)
        {
            if (!player.IsAdmin) return;
            foreach(var cfgs in config.syringe)
            {
                var item = cfgs.Value.Copy(1);
                item.MoveToContainer(player.inventory.containerMain);
            }
        }

        [ConsoleCommand("Give_Syringe")]
        void GiveSyringe(ConsoleSystem.Arg args)
        {
            if (args.Player() != null || !args.HasArgs(3))
            {
                PrintWarning($"Неверный синтаксис, используйте Give_Syringe StemId64 NameSyringe колличевство");
                return;
            }

            ulong targetId;
            string name = (string)args.Args[1];
            int amount;
            if (ulong.TryParse((string)args.Args[0], out targetId) && int.TryParse((string)args.Args[2], out amount))
            {
                if (config.syringe.ContainsKey(name))
                {
                    BasePlayer target = BasePlayer.FindByID(targetId);
                    if (target != null && target.IsConnected)
                        config.syringe[name].CreateItem(target, Vector3.zero, amount);
                }
            }
        }

        #region Hooks

        object CanCombineDroppedItem(DroppedItem item, DroppedItem targetItem)
        {
            if (item.GetItem().skin != targetItem.GetItem().skin) return false;
            return null;
        }

        object CanStackItem(Item item, Item targetItem)
        {
            if (item.skin != targetItem.skin) return false;
            return null;
        }

        private Item OnItemSplit(Item item, int amount)
        {
            if (plugins.Find("Stacks") || plugins.Find("CustomSkinsStacksFix")) return null;
            var customItem = config.syringe.FirstOrDefault(p => p.Value.DisplayName == item.name);
            if (customItem.Value != null && customItem.Value.skinid == item.skin)
            {
                Item x = ItemManager.CreateByPartialName(customItem.Value.ReplaceShortName, amount);
                x.name = customItem.Value.DisplayName;
                x.skin = customItem.Value.skinid;
                x.amount = amount;

                item.amount -= amount;
                return x;
            }
            return null;
        }

        void OnEntitySpawned(BaseNetworkable entity)
        {
            if (entity.GetComponent<LootContainer>() == null) return;
            int random = UnityEngine.Random.Range(0, config.syringe.Count);

            var CfgItem = config.syringe.ElementAt(random);

            if (CfgItem.Value.crate.Contains(entity.GetComponent<LootContainer>().ShortPrefabName.ToLower()))
            {
                bool goodChance = UnityEngine.Random.Range(0, 100) >= (100 - CfgItem.Value.DropChance);
                if (goodChance)
                {
                    var item = CfgItem.Value.Copy(1);
                    item?.MoveToContainer(entity.GetComponent<LootContainer>().inventory);
                }   
            }
        }

        

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (ColdPlayer.Contains(player))
            {
                ColdPlayer.Remove(player);
            }
        }
        void OnRunPlayerMetabolism(PlayerMetabolism metabolism, BaseCombatEntity entity)
        {
            var player = entity as BasePlayer;
            if (!(entity is BasePlayer)) return;
            if (ColdPlayer.Contains(player))
            {
                if (player.metabolism.temperature.value < 20)
                {
                    player.metabolism.temperature.value = 21;
                }
            }
        }

        List<BasePlayer> ColdPlayer = new List<BasePlayer>();
        object OnHealingItemUse(MedicalTool tool, BasePlayer player)
        {       
            if (player.GetActiveItem()?.skin != 0)
            {
                var cfg = config.syringe.FirstOrDefault(x => x.Value.skinid == player.GetActiveItem()?.skin);
                if (cfg.Value == null) return null;
                switch (cfg.Key)
                {
                    case "rad":
                        {
                            player.metabolism.radiation_poison.value = cfg.Value.Parameter;
                            return false;
                        }
                    case "heal":
                        {
                            player.health = cfg.Value.Parameter;
                            return false;
                        }
                    case "cold":
                        {
                            ColdPlayer.Add(player);
                            timer.Once(config.setings.SyringeColdTime, () => ColdPlayer.Remove(player));
                            PrintToChat(player, $"Ви захищені від холоду на {config.setings.SyringeColdTime} сек");
                            return false;
                        }
                    default:
                        {
                            return null;
                        }
                }
            }
            return null;
        }
        #endregion

    }
}
