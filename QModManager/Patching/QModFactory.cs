﻿namespace QModManager.Patching
{
    using Oculus.Newtonsoft.Json;
    using QModManager.API;
    using QModManager.DataStructures;
    using QModManager.Utility;
    using System;
    using System.Collections.Generic;
    using System.IO;

    internal class QModFactory : IQModFactory
    {
        internal IManifestValidator Validator { get; set; } = new ManifestValidator();

        /// <summary>
        /// Searches through all folders in the provided directory and returns an ordered list of mods to load.<para/>
        /// Mods that cannot be loaded will have an unsuccessful <see cref="QMod.Status"/> value.
        /// </summary>
        /// <param name="qmodsDirectory">The QMods directory</param>
        /// <returns>A new, sorted <see cref="List{QMod}"/> ready to be initialized or skipped.</returns>
        public List<QMod> BuildModLoadingList(string qmodsDirectory)
        {
            if (!Directory.Exists(qmodsDirectory))
            {
                Logger.Info("QMods directory was not found! Creating...");
                Directory.CreateDirectory(qmodsDirectory);

                return new List<QMod>(0);
            }

            string[] subDirectories = Directory.GetDirectories(qmodsDirectory);
            var modSorter = new SortedCollection<string, QMod>();
            var earlyErrors = new List<QMod>(subDirectories.Length);

            LoadModsFromDirectories(subDirectories, modSorter, earlyErrors);

            List<QMod> modsToLoad = modSorter.GetSortedList();

            return CreateModStatusList(earlyErrors, modsToLoad);
        }

        internal void LoadModsFromDirectories(string[] subDirectories, SortedCollection<string, QMod> modSorter, List<QMod> earlyErrors)
        {
            foreach (string subDir in subDirectories)
            {
                string[] dllFiles = Directory.GetFiles(subDir, "*.dll", SearchOption.TopDirectoryOnly);

                if (dllFiles.Length < 1)
                    continue;

                string jsonFile = Path.Combine(subDir, "mod.json");

                string folderName = new DirectoryInfo(subDir).Name;

                if (!File.Exists(jsonFile))
                {
                    Logger.Error($"Unable to set up mod in folder \"{folderName}\"");
                    earlyErrors.Add(new QModPlaceholder(folderName, ModStatus.MissingCoreInfo));
                    continue;
                }

                QMod mod = CreateFromJsonManifestFile(subDir);

                this.Validator.CheckRequiredMods(mod);

                Logger.Debug($"Sorting mod {mod.Id}");
                bool added = modSorter.AddSorted(mod);
                if (!added)
                {
                    Logger.Debug($"DuplicateId on mod {mod.Id}");
                    mod.Status = ModStatus.DuplicateIdDetected;
                    earlyErrors.Add(mod);
                }
            }
        }

        internal List<QMod> CreateModStatusList(List<QMod> earlyErrors, List<QMod> modsToLoad)
        {
            var modList = new List<QMod>(modsToLoad.Count + earlyErrors.Count);

            foreach (QMod mod in modsToLoad)
            {
                Logger.Debug($"{mod.Id} ready to load");
                modList.Add(mod);
            }

            foreach (QMod erroredMod in earlyErrors)
            {
                Logger.Debug($"{erroredMod.Id} had an early error");
                modList.Add(erroredMod);
            }

            foreach (QMod mod in modList)
            {
                if (mod.Status != ModStatus.Success)
                    continue;

                if (mod.RequiredMods != null)
                {
                    ValidateDependencies(modsToLoad, mod);
                }

                if (mod.Status == ModStatus.Success)
                    this.Validator.ValidateManifest(mod);
            }

            return modList;
        }

        private void ValidateDependencies(List<QMod> modsToLoad, QMod mod)
        {
            // Check the mod dependencies
            foreach (RequiredQMod requiredMod in mod.RequiredMods)
            {
                QMod dependency = modsToLoad.Find(d => d.Id == requiredMod.Id);

                if (dependency == null || dependency.Status != ModStatus.Success)
                {
                    // Dependency not found or failed
                    Logger.Error($"{mod.Id} cannot be loaded because it is missing a dependency. Missing mod: {requiredMod.Id}");
                    mod.Status = ModStatus.MissingDependency;
                    break;
                }

                if (dependency.LoadedAssembly == null)
                {
                    // Dependency hasn't been validated yet
                    this.Validator.ValidateManifest(dependency);
                }

                if (dependency.Status != ModStatus.Success)
                {
                    // Dependency failed to load successfully
                    // Treat it as missing
                    Logger.Error($"{mod.Id} cannot be loaded because its dependency failed to load. Failed mod: {requiredMod.Id}");
                    mod.Status = ModStatus.MissingDependency;
                    break;
                }

                if (dependency.ParsedVersion < requiredMod.MinimumVersion)
                {
                    // Dependency version is older than the version required by this mod
                    Logger.Error($"{mod.Id} cannot be loaded because its dependency is out of date. Outdated mod: {requiredMod.Id}");
                    mod.Status = ModStatus.OutOfDateDependency;
                    break;
                }
            }
        }

        private static QMod CreateFromJsonManifestFile(string subDirectory)
        {
            string jsonFile = Path.Combine(subDirectory, "mod.json");

            if (!File.Exists(jsonFile))
            {
                return null;
            }

            try
            {
                var settings = new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore
                };

                string jsonText = File.ReadAllText(jsonFile);

                QMod mod = JsonConvert.DeserializeObject<QMod>(jsonText);

                mod.SubDirectory = subDirectory;

                return mod;
            }
            catch (Exception e)
            {
                Logger.Error($"\"mod.json\" deserialization failed for file \"{jsonFile}\"!");
                Logger.Exception(e);

                return null;
            }
        }
    }
}
