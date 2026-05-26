using System;
using System.Collections.Generic;
using System.Linq;

using CitizenFX.Core;

using MenuAPI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using vMenuClient.data;

using static CitizenFX.Core.Native.API;
using static vMenuClient.CommonFunctions;
using static vMenuShared.PermissionsManager;

namespace vMenuClient.menus
{
    public class VehicleSpawner
    {
        private Menu menu;

        public bool SpawnInVehicle { get; private set; } = UserDefaults.VehicleSpawnerSpawnInside;
        public bool ReplaceVehicle { get; private set; } = UserDefaults.VehicleSpawnerReplacePrevious;

        private string SearchTerm = "";
        public static List<bool> allowedCategories;

        // modelHash -> gameName (vehicles.meta / GXT resolved name)
        public static Dictionary<uint, string> VehicleGameNames = new();
        
        private static HashSet<uint> _resolvedNames = new();

        private void CreateMenu()
        {
            menu = new Menu(Game.Player.Name, "Vehicle Spawner");
            RefreshSpawnableVehicles(menu);
        }

        /// <summary>
        /// Gets best display name:
        /// custom addon name → cached gameName → native label → model fallback
        /// </summary>
        private static string GetVehicleName(string model)
        {
            // 0. custom addons.json display name
            if (VehicleData.Vehicles.CustomVehicleNames.TryGetValue(model.ToLower(), out var customName))
            {
                if (!string.IsNullOrWhiteSpace(customName))
                {
                    return customName;
                }
            }

            uint hash = (uint)GetHashKey(model);

            // 1. cached vehicles.meta / resolved gameName
            if (VehicleGameNames.TryGetValue(hash, out var cachedName))
            {
                if (!string.IsNullOrWhiteSpace(cachedName) &&
                    cachedName != "NULL" &&
                    cachedName != "CARNOTFOUND")
                {
                    return cachedName;
                }
            }

            // 2. native fallback (GXT label)
            var label = GetDisplayNameFromVehicleModel(hash);
            var gxt = GetLabelText(label);

            if (!string.IsNullOrWhiteSpace(gxt) &&
                gxt != "NULL" &&
                gxt != "CARNOTFOUND")
            {
                return gxt;
            }

            // 3. last resort
            return model;
        }

        /// <summary>
        /// Ensures addon vehicles also get cached gameName once per model
        /// </summary>
        private static void EnsureVehicleNameCached(string model)
        {
            uint hash = (uint)GetHashKey(model);

            if (_resolvedNames.Contains(hash))
            {
                return;
            }

            _resolvedNames.Add(hash);

            var label = GetDisplayNameFromVehicleModel(hash);
            var gameName = GetLabelText(label);

            if (!string.IsNullOrWhiteSpace(gameName) &&
                gameName != "NULL" &&
                gameName != "CARNOTFOUND")
            {
                VehicleGameNames[hash] = gameName;
            }
        }

        private void RefreshSpawnableVehicles(Menu menu)
        {
            menu.ClearMenuItems(true);

            var spawnByName = new MenuItem("Spawn Vehicle By Model Name",
                "Enter the name of a vehicle to spawn.");

            var searchButton = new MenuItem("Search for Vehicle",
                "Search through available vehicles.");

            var spawnInVeh = new MenuCheckboxItem("Spawn Inside Vehicle",
                "Teleport into vehicle when spawned.", SpawnInVehicle);

            var replacePrev = new MenuCheckboxItem("Replace Previous Vehicle",
                "Deletes last spawned vehicle automatically.", ReplaceVehicle);

            if (IsAllowed(Permission.VSSpawnByName))
            {
                menu.AddMenuItem(spawnByName);
            }

            menu.AddMenuItem(searchButton);
            menu.AddMenuItem(spawnInVeh);
            menu.AddMenuItem(replacePrev);

            // Load addon vehicles (unchanged)
            var jsonData = LoadResourceFile(GetCurrentResourceName(), "config/addons.json") ?? "{}";
            var addons = JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonData);

            if (addons != null && addons.ContainsKey("vehicles"))
            {
                var addonVehicles = JObject.FromObject(addons["vehicles"])
                    .ToObject<Dictionary<string, string>>();

                if (addonVehicles != null)
                {
                    // Send only spawn names into vehicle processing
                    VehicleData.Vehicles.ProcessAddonVehicles(
                        addonVehicles.Values.ToList()
                    );

                    // Cache custom display names
                    foreach (var kvp in addonVehicles)
                    {
                        // kvp.Key   = display name
                        // kvp.Value = spawn name

                        VehicleData.Vehicles.CustomVehicleNames[kvp.Value.ToLower()] = kvp.Key;
                    }

                    Debug.WriteLine($"[VMENU] Loaded {addonVehicles.Count} addon vehicles");
                }
            }

            for (var vehClass = 0; vehClass < 23; vehClass++)
            {
                var className = GetLabelText($"VEH_CLASS_{vehClass}");

                var btn = new MenuItem(className,
                    $"Spawn vehicles from {className}") { Label = "→→→" };

                var vehicleClassMenu = new Menu("Vehicle Spawner", className);

                MenuController.AddSubmenu(menu, vehicleClassMenu);
                menu.AddMenuItem(btn);

                if (allowedCategories[vehClass])
                {
                    MenuController.BindMenuItem(menu, vehicleClassMenu, btn);
                }
                else
                {
                    btn.Enabled = false;
                    btn.LeftIcon = MenuItem.Icon.LOCK;
                    btn.Description = "Disabled by server owner.";
                }

                var duplicateVehNames = new Dictionary<string, int>();

                foreach (var veh in VehicleData.Vehicles.VehicleClasses[className])
                {
                    EnsureVehicleNameCached(veh);

                    var vehName = GetVehicleName(veh);

                    if (!string.IsNullOrWhiteSpace(SearchTerm) &&
                        vehName.IndexOf(SearchTerm, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    uint model = (uint)GetHashKey(veh);

                    var topSpeed = Map(GetVehicleModelEstimatedMaxSpeed(model), 0f, 60f, 0f, 1f);
                    var acceleration = Map(GetVehicleModelAcceleration(model), 0f, 1f, 0f, 1f);
                    var braking = Map(GetVehicleModelMaxBraking(model), 0f, 1f, 0f, 1f);
                    var traction = Map(GetVehicleModelMaxTraction(model), 0f, 3f, 0f, 1f);

                    // duplicate handling
                    if (duplicateVehNames.ContainsKey(vehName))
                    {
                        duplicateVehNames[vehName]++;
                        vehName += $" ({duplicateVehNames[vehName]})";
                    }
                    else
                    {
                        duplicateVehNames[vehName] = 1;
                    }

                    MenuItem vehBtn;

                    // AFTER
                    var stats = DoesModelExist(veh)
                        ? new float[] { topSpeed, acceleration, braking, traction }
                        : new float[] { 0f, 0f, 0f, 0f };

                    if (DoesModelExist(veh))
                    {
                        vehBtn = new MenuItem(vehName)
                        {
                            Enabled = true,
                            Label = $"({veh.ToLower()})",
                            ItemData = new VehicleMenuItemData(veh, stats)
                        };
                    }
                    else
                    {
                        vehBtn = new MenuItem(vehName,
                            "Missing model (not streamed or not installed)")
                        {
                            Enabled = false,
                            Label = $"({veh.ToLower()})",
                            ItemData = new VehicleMenuItemData(null, stats),
                            RightIcon = MenuItem.Icon.LOCK
                        };
                    }

                    vehicleClassMenu.AddMenuItem(vehBtn);
                }

                vehicleClassMenu.ShowVehicleStatsPanel = vehicleClassMenu.Size > 0;

                // AFTER
                vehicleClassMenu.OnItemSelect += async (_, item, _) =>
                {
                    // Guard: only spawn if the item carries a valid model name
                    if (item?.ItemData is not VehicleMenuItemData itemData || string.IsNullOrEmpty(itemData.Model))
                    {
                        Debug.WriteLine("[VMENU] Spawn blocked — item has no valid model name.");
                        return;
                    }

                    if (!DoesModelExist(itemData.Model))
                    {
                        Debug.WriteLine($"[VMENU] Spawn blocked — model not loaded: {itemData.Model}");
                        Notify.Alert($"~r~Cannot spawn ~w~{itemData.Model}~r~ — model not loaded.");
                        return;
                    }

                    await SpawnVehicle(itemData.Model, SpawnInVehicle, ReplaceVehicle);
                };

                void HandleStats(Menu m, MenuItem item)
                {
                    if (item?.ItemData is VehicleMenuItemData d)
                    {
                        m.ShowVehicleStatsPanel = true;
                        m.SetVehicleStats(d.Stats[0], d.Stats[1], d.Stats[2], d.Stats[3]);
                        m.SetVehicleUpgradeStats(0f, 0f, 0f, 0f);
                    }
                    else
                    {
                        m.ShowVehicleStatsPanel = false;
                    }
                }

                vehicleClassMenu.OnMenuOpen += m =>
                    HandleStats(m, m.GetCurrentMenuItem());

                vehicleClassMenu.OnIndexChange += (m, _, newItem, _, _) =>
                    HandleStats(m, newItem);
            }

            menu.OnItemSelect += async (_, item, _) =>
            {
                if (item == spawnByName)
                {
                    await SpawnVehicle("custom", SpawnInVehicle, ReplaceVehicle);
                }
                else if (item == searchButton)
                {
                    SearchTerm = await GetUserInput("Search Term (blank resets)", 100);
                    RefreshSpawnableVehicles(menu);
                    SearchTerm = "";
                }
            };

            menu.OnCheckboxChange += (_, item, _, checkedState) =>
            {
                if (item == spawnInVeh)
                {
                    SpawnInVehicle = checkedState;
                }
                else if (item == replacePrev)
                {
                    ReplaceVehicle = checkedState;
                }
            };
        }

        public Menu GetMenu()
        {
            if (menu == null)
            {
                CreateMenu();
            }

            return menu;
        }
    }

    internal class VehicleMenuItemData
    {
        public string Model { get; }
        public float[] Stats { get; }

        public VehicleMenuItemData(string model, float[] stats)
        {
            Model = model;
            Stats = stats;
        }
    }
}