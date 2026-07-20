using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("BuildingSpawn", "ukrrustua", "2.6.1")]
    [Description("Automatically or manually spawns buildings using CopyPaste to attract players.")]
    public class BuildingSpawn : RustPlugin
    {
        [PluginReference]
        private Plugin CopyPaste;
        
        private Timer _autoSpawnTimer;
        private List<ActiveBuilding> _activeBuildings = new List<ActiveBuilding>();
        private int _currentBuildingIndex = 0; // CHANGE: Індекс для послідовного спавну

        #region Configuration

        private Configuration config;

        private class Configuration
        {
            [JsonProperty("1. Увімкнути автоматичний спавн (Enable Auto Spawn)")]
            public bool EnableAutoSpawn = true;

            [JsonProperty("2. Інтервал авто-спавну у хвилинах (Auto Spawn Interval Minutes)")]
            public float AutoSpawnIntervalMinutes = 60f;

            [JsonProperty("3. Час життя будівлі у хвилинах (Building Lifetime Minutes)")]
            public float BuildingLifetimeMinutes = 120f;

            [JsonProperty("4. Список файлів будівель (List of building filenames from copypaste data)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BuildingFiles = new List<string> { "base1", "base2" };

            [JsonProperty("4.1. Вибирати будівлі зі списку випадково (Spawn Buildings Randomly)")]
            public bool SpawnBuildingsRandomly = false;

            [JsonProperty("5. Використовувати випадкові координати (Use Random Locations)")]
            public bool UseRandomLocations = true;

            [JsonProperty("6. Фіксовані координати (Fixed Locations)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<Vector3> FixedLocations = new List<Vector3> { new Vector3(0, 0, 0) };
            
            [JsonProperty("7. Радіус очистки при видаленні (Cleanup Radius)")]
            public float CleanupRadius = 50f;

            [JsonProperty("8. Максимальна кількість активних будівель (Max Concurrent Buildings)")]
            public int MaxConcurrentBuildings = 3;

            [JsonProperty("9. Час видалення після рейду у хвилинах (Despawn Time After Raid Minutes)")]
            public float DespawnTimeAfterRaidMinutes = 10f;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception();
            }
            catch
            {
                PrintError("Failed to load config, creating a new one!");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            config = new Configuration();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config);
        }

        #endregion

        #region Classes

        private class ActiveBuilding
        {
            public Vector3 Position;
            public Timer DespawnTimer;
            public string FileName;
            public bool IsRaided = false; // CHANGE: Відстеження статусу рейду
        }

        #endregion

        #region Hooks

        private void OnServerInitialized()
        {
            if (CopyPaste == null)
            {
                PrintError("CopyPaste plugin is not loaded! BuildingSpawn requires it to function.");
                return;
            }

            // CHANGE: Додано перевірку наявності будівель у конфігу та папці CopyPaste
            if (config.BuildingFiles == null || config.BuildingFiles.Count == 0)
            {
                PrintError("Увага: У конфігурації відсутні назви будівель (BuildingFiles)!");
            }
            else
            {
                int missingCount = 0;
                foreach (var file in config.BuildingFiles)
                {
                    string cleanName = file.Replace(".json", "");
                    if (!Interface.Oxide.DataFileSystem.ExistsDatafile($"copypaste/{cleanName}"))
                    {
                        PrintWarning($"Увага: Файл '{cleanName}.json' не знайдено у папці oxide/data/copypaste!");
                        missingCount++;
                    }
                }

                if (missingCount == config.BuildingFiles.Count)
                {
                    PrintError("КРИТИЧНА ПОМИЛКА: Жодної будівлі з конфігу не знайдено в папці CopyPaste. Плагін не зможе нічого заспавнити!");
                }
            }

            if (config.EnableAutoSpawn)
            {
                StartAutoSpawnTimer();
            }
        }

        private void Unload()
        {
            if (_autoSpawnTimer != null)
            {
                _autoSpawnTimer.Destroy();
            }

            foreach (var building in _activeBuildings)
            {
                if (building.DespawnTimer != null)
                    building.DespawnTimer.Destroy();
                
                RemoveBuilding(building.Position, config.CleanupRadius);
            }
            _activeBuildings.Clear();
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return;

            // Перевіряємо, чи зруйнована шафа
            if (entity is BuildingPrivlidge)
            {
                var basePos = entity.transform.position;
                
                foreach (var building in _activeBuildings)
                {
                    if (building.IsRaided) continue;

                    if (Vector3.Distance(basePos, building.Position) <= config.CleanupRadius)
                    {
                        // CHANGE: Базу зарейдили. Змінюємо таймер і доспавнюємо нову
                        building.IsRaided = true;
                        
                        PrintWarning($"[BuildingSpawn] Базу '{building.FileName}' за координатами {building.Position} було зарейджено! Вона зникне через {config.DespawnTimeAfterRaidMinutes} хвилин.");

                        if (building.DespawnTimer != null) building.DespawnTimer.Destroy();
                        building.DespawnTimer = timer.Once(config.DespawnTimeAfterRaidMinutes * 60f, () =>
                        {
                            RemoveBuilding(building.Position, config.CleanupRadius);
                            _activeBuildings.Remove(building);
                        });

                        MaintainBuildingCount();
                        break;
                    }
                }
            }
        }

        #endregion

        #region Timers & Spawning

        private void MaintainBuildingCount()
        {
            int currentActive = _activeBuildings.Count(b => !b.IsRaided);
            int toSpawn = config.MaxConcurrentBuildings - currentActive;

            for (int i = 0; i < toSpawn; i++)
            {
                SpawnBuilding();
            }
        }

        private void StartAutoSpawnTimer()
        {
            // CHANGE: Замість спавну 1 будівлі підтримуємо їхню кількість
            MaintainBuildingCount();

            // Перевіряємо кожну хвилину, щоб завжди підтримувати ліміт
            _autoSpawnTimer = timer.Every(60f, () =>
            {
                MaintainBuildingCount();
            });
        }

        /// <summary>
        /// Initiates the spawning process of a random or fixed building.
        /// </summary>
        /// <param name="forceRandom">Forces a random position if true.</param>
        private void SpawnBuilding(bool forceRandom = false)
        {
            if (config.BuildingFiles.Count == 0)
            {
                PrintError("No building files specified in the config!");
                return;
            }

            string fileName;
            // CHANGE: Логіка вибору бази (випадково або по черзі зі списку)
            if (config.SpawnBuildingsRandomly || forceRandom)
            {
                fileName = config.BuildingFiles[UnityEngine.Random.Range(0, config.BuildingFiles.Count)];
            }
            else
            {
                if (_currentBuildingIndex >= config.BuildingFiles.Count) _currentBuildingIndex = 0;
                fileName = config.BuildingFiles[_currentBuildingIndex];
                _currentBuildingIndex = (_currentBuildingIndex + 1) % config.BuildingFiles.Count;
            }

            Vector3 position = Vector3.zero;

            if (config.UseRandomLocations || forceRandom || config.FixedLocations.Count == 0)
            {
                position = GetRandomPosition();
                if (position == Vector3.zero)
                {
                    PrintError("Failed to find a valid random position for the building.");
                    return;
                }
            }
            else
            {
                position = config.FixedLocations[UnityEngine.Random.Range(0, config.FixedLocations.Count)];
                // Correct Y just in case
                position.y = TerrainMeta.HeightMap.GetHeight(position);
            }

            PasteBuilding(fileName, position);
        }

        /// <summary>
        /// Pastes the building via CopyPaste API and starts despawn timer.
        /// </summary>
        private void PasteBuilding(string fileName, Vector3 position)
        {
            fileName = fileName.Replace(".json", "");

            string[] args = new string[] 
            { 
                "height", "0",
                "stability", "true"
            };

            var success = CopyPaste.Call("TryPasteFromVector3", position, 0f, fileName, args);

            // CHANGE: Added validation for successful CopyPaste return true
            if (success is bool && (bool)success)
            {
                // CHANGE: Додано вивід у консоль при успішному спавні будівлі
                PrintWarning($"[BuildingSpawn] Успішно заспавнено будівлю '{fileName}' за координатами {position}!");

                ActiveBuilding activeBuilding = new ActiveBuilding
                {
                    Position = position,
                    FileName = fileName
                };

                activeBuilding.DespawnTimer = timer.Once(config.BuildingLifetimeMinutes * 60f, () =>
                {
                    RemoveBuilding(activeBuilding.Position, config.CleanupRadius);
                    _activeBuildings.Remove(activeBuilding);
                    
                    // CHANGE: Після природнього зникнення бази, відразу спавнимо нову
                    MaintainBuildingCount();
                });

                _activeBuildings.Add(activeBuilding);
            }
            else
            {
                if (success is string errorMsg)
                {
                    PrintError($"CopyPaste повернув помилку: {errorMsg}");
                }
                else if (success == null)
                {
                    PrintError("CopyPaste повернув null! Можливо, такий метод більше не підтримується або передані невірні аргументи.");
                }

                // CHANGE: Додано вивід у консоль при відсутності або помилці спавну будівлі
                PrintError($"Не вдалося заспавнити будівлю '{fileName}' через CopyPaste за координатами {position}. Переконайтеся, що файл '{fileName}.json' існує у папці oxide/data/copypaste!");
            }
        }

        /// <summary>
        /// Removes all entities belonging to this plugin in a given radius.
        /// </summary>
        private void RemoveBuilding(Vector3 position, float radius)
        {
            List<BaseEntity> entities = new List<BaseEntity>();
            Vis.Entities(position, radius, entities, LayerMask.GetMask("Construction", "Deployable", "Default"));

            int removedCount = 0;
            foreach (var entity in entities)
            {
                if (entity == null || entity.IsDestroyed) continue;

                // Видаляємо всі будівельні блоки та об'єкти (DecayEntity) в цьому радіусі.
                // Оскільки бази спавняться в порожніх місцях, це безпечно і гарантовано видалить базу,
                // незалежно від того, який OwnerID зберігся у файлі CopyPaste.
                if (entity is DecayEntity)
                {
                    entity.Kill(BaseNetworkable.DestroyMode.Gib);
                    removedCount++;
                }
            }

            Puts($"Removed building at {position}. Cleaned up {removedCount} entities.");
        }

        #endregion

        #region Utility

        /// <summary>
        /// Finds a random valid point on the map.
        /// </summary>
        /// <returns>A valid Vector3 position or Vector3.zero if failed.</returns>
        private Vector3 GetRandomPosition()
        {
            float mapSize = TerrainMeta.Size.x / 2f;
            // Leave a margin from edges
            float margin = 200f;

            for (int i = 0; i < 50; i++)
            {
                float x = UnityEngine.Random.Range(-mapSize + margin, mapSize - margin);
                float z = UnityEngine.Random.Range(-mapSize + margin, mapSize - margin);
                Vector3 pos = new Vector3(x, 0, z);
                
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);

                // Ignore if underwater
                if (WaterLevel.Test(pos, true, false)) continue;

                // Check topology to avoid monuments, roads, rivers
                int topology = TerrainMeta.TopologyMap.GetTopology(pos);
                if ((topology & (int)(TerrainTopology.Enum.Monument | TerrainTopology.Enum.Road | TerrainTopology.Enum.River | TerrainTopology.Enum.Lake)) != 0)
                    continue;

                // Check slope - shouldn't be too steep
                float slope = TerrainMeta.HeightMap.GetSlope(pos);
                if (slope > 10f) continue;

                // Check nearby entities (avoid spawning on players or other bases)
                if (Physics.CheckSphere(pos, 30f, LayerMask.GetMask("Construction", "Deployable", "Player (Server)"))) 
                    continue;

                return pos;
            }

            return Vector3.zero;
        }

        #endregion

        #region Commands

        [ChatCommand("spawnbase")]
        private void CmdSpawnBase(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                player.ChatMessage("You do not have permission to use this command.");
                return;
            }

            if (args.Length > 0 && args[0].ToLower() == "random")
            {
                SpawnBuilding(true);
                player.ChatMessage("Initiated random building spawn.");
                return;
            }

            if (args.Length > 0)
            {
                // Try spawn specific file at look position
                string fileName = args[0];
                Vector3 lookPos = player.eyes.position + player.eyes.HeadForward() * 10f;
                lookPos.y = TerrainMeta.HeightMap.GetHeight(lookPos);
                
                PasteBuilding(fileName, lookPos);
                player.ChatMessage($"Attempted to spawn '{fileName}' at your look position.");
            }
            else
            {
                player.ChatMessage("Usage: /spawnbase random | /spawnbase <filename>");
            }
        }

        [ChatCommand("clearbases")]
        private void CmdClearBases(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;

            int count = _activeBuildings.Count;
            // CHANGE: ToList used to prevent collection modification during iteration
            foreach (var building in _activeBuildings.ToList()) 
            {
                if (building.DespawnTimer != null) building.DespawnTimer.Destroy();
                RemoveBuilding(building.Position, config.CleanupRadius);
            }
            _activeBuildings.Clear();

            player.ChatMessage($"Cleared {count} active spawned buildings.");
        }

        #endregion
    }
}
