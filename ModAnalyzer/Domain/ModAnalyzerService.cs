﻿using ModAnalyzer.Utils;
using Newtonsoft.Json;
using SharpCompress.Archive;
using SharpCompress.Common;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace ModAnalyzer.Domain {
    public class ModAnalyzerService {
        private readonly BackgroundWorker _backgroundWorker;
        private readonly AssetArchiveAnalyzer _assetArchiveAnalyzer;
        private readonly PluginAnalyzer _pluginAnalyzer;
        private ModAnalysis _modAnalysis;
        private List<EntryAnalysisJob> entryAnalysisJobs;
        private readonly string[] entryJobExtensions = { ".BA2", ".BSA", ".ESP", ".ESM" };

        public event EventHandler<MessageReportedEventArgs> MessageReported;
        public event EventHandler<EventArgs> AnalysisCompleted;

        public ModAnalyzerService() {
            // prepare background worker
            _backgroundWorker = new BackgroundWorker { WorkerReportsProgress = true };
            _backgroundWorker.DoWork += BackgroundWork;
            _backgroundWorker.ProgressChanged += BackgroundProgress;

            // prepare analyzers and job queues
            _assetArchiveAnalyzer = new AssetArchiveAnalyzer(_backgroundWorker);
            _pluginAnalyzer = new PluginAnalyzer(_backgroundWorker);
            entryAnalysisJobs = new List<EntryAnalysisJob>();

            // prepare directories
            Directory.CreateDirectory("output");
        }

        private void BackgroundProgress(object sender, ProgressChangedEventArgs e) {
            MessageReportedEventArgs eventArgs = e.UserState as MessageReportedEventArgs;

            MessageReported?.Invoke(this, eventArgs);
        }

        private string GetOutputFilename(List<ModOption> archiveModOptions) {
            ModOption baseOption = archiveModOptions.Find(modOption => modOption.Default);
            if (baseOption == null) {
                baseOption = archiveModOptions.First();
            }
            return baseOption.Name;
        }
         
        // Background job to analyze a mod
        private void BackgroundWork(object sender, DoWorkEventArgs e) {
            _modAnalysis = new ModAnalysis();
            List<ModOption> archiveModOptions = e.Argument as List<ModOption>;

            // analyze each archive
            try {
                foreach (ModOption archiveModOption in archiveModOptions) {
                    _backgroundWorker.ReportMessage("Analyzing " + archiveModOption.Name + "...", true);
                    _backgroundWorker.ReportMessage("Calculating MD5 Hash.", false);
                    archiveModOption.GetMD5Hash();
                    AnalyzeArchive(archiveModOption);
                    AnalyzeEntries();
                    _backgroundWorker.ReportMessage(Environment.NewLine, false);
                }

                // save output
                SaveOutputFile(GetOutputFilename(archiveModOptions));
            } catch (Exception x) {
                _backgroundWorker.ReportMessage(Environment.NewLine + x.Message, false);
                _backgroundWorker.ReportMessage(x.StackTrace, false);
                _backgroundWorker.ReportMessage("Analysis failed.", true);
            }

            // tell the view model we're done analyzing things
            AnalysisCompleted?.Invoke(this, EventArgs.Empty);
        }

        // performs the enqueued entry analysis jobs
        // TODO: Raise exception if job fails
        private void AnalyzeEntries() {
            foreach (EntryAnalysisJob job in entryAnalysisJobs) {
                string ext = job.Entry.GetEntryExtension();
                switch (ext) {
                    case ".BSA":
                    case ".BA2":
                        List<String> assets = _assetArchiveAnalyzer.GetAssetPaths(job.Entry);
                        if (assets != null) {
                            job.AddArchiveAssetPaths(Path.GetFileName(job.Entry.Key), assets);
                        }
                        break;
                    case ".ESP":
                    case ".ESM":
                        PluginDump dump = _pluginAnalyzer.GetPluginDump(job.Entry);
                        if (dump != null) {
                            job.AddPluginDump(dump);
                        } else {
                            throw new Exception("Plugin dump failed.");
                        }
                        break;
                }
            }

            // clear the entry analysis jobs - they've been performed
            entryAnalysisJobs.Clear();
        }

        private void AnalyzeArchive(ModOption modOption) {
            if (modOption.IsFomodArchive) {
                List<ModOption> fomodOptions = AnalyzeFomodArchive(modOption);
                fomodOptions.ForEach(mo => {
                    mo.MD5Hash = modOption.MD5Hash;
                    mo.Default = mo.Default && modOption.Default;
                });
                _modAnalysis.ModOptions.Add(modOption);
                _modAnalysis.ModOptions.AddRange(fomodOptions);
            } else if (modOption.IsBainArchive) {
                List<ModOption> bainOptions = AnalyzeBainArchive(modOption);
                bainOptions.ForEach(mo => { mo.MD5Hash = modOption.MD5Hash; });
                _modAnalysis.ModOptions.Add(modOption);
                _modAnalysis.ModOptions.AddRange(bainOptions);
            } else {
                AnalyzeNormalArchive(modOption);
            }
        }

        private void MapEntryToFomodOption(List<Tuple<FomodFile, ModOption>> map, IArchiveEntry entry, string fomodBasePath) {
            string entryPath = entry.GetPath();
            if (fomodBasePath.Length > 0) {
                entryPath = entryPath.Replace(fomodBasePath, "");
            }
            foreach (Tuple<FomodFile, ModOption> mapping in map) {
                FomodFile fileNode = mapping.Item1;
                ModOption option = mapping.Item2;

                if (fileNode.MatchesPath(entryPath)) {
                    string mappedPath = fileNode.MappedPath(entryPath);
                    if (mappedPath == "") continue;
                    if (option.Assets.IndexOf(mappedPath) > -1) continue;
                    option.Assets.Add(mappedPath);
                    option.Size += entry.Size;
                    _backgroundWorker.ReportMessage("  " + option.Name + " -> " + mappedPath, false);

                    // enqueue jobs for analyzing archives and plugins
                    EnqueueAnalysisJob(entry, option);
                }
            }
        }

        private void MapEntryToBainOption(List<Tuple<string, ModOption>> map, IArchiveEntry entry) {
            string entryPath = entry.GetPath();
            foreach (Tuple<string, ModOption> mapping in map) {
                string bainPath = mapping.Item1 + @"\";
                ModOption option = mapping.Item2;

                if (entryPath.StartsWith(bainPath)) {
                    string mappedPath = entryPath.Replace(bainPath, "");
                    if (mappedPath == "") continue;
                    option.Assets.Add(mappedPath);
                    option.Size += entry.Size;
                    _backgroundWorker.ReportMessage("  " + option.Name + " -> " + mappedPath, false);

                    // enqueue jobs for analyzing archives and plugins
                    EnqueueAnalysisJob(entry, option);
                }
            }
        }

        private List<ModOption> AnalyzeBainArchive(ModOption archiveModOption) {
            IArchive archive = archiveModOption.Archive;
            _backgroundWorker.ReportMessage("Parsing BAIN Options", true);
            List<ModOption> bainOptions = new List<ModOption>();
            List<Tuple<string, ModOption>> bainMap = new List<Tuple<string, ModOption>>();

            // STEP 1. Find BAIN directories and build mod options for them
            foreach (string bainDirectory in archiveModOption.GetValidBainDirectories()) {
                string bainOptionName = Path.GetFileName(bainDirectory);
                _backgroundWorker.ReportMessage("Found BAIN Option " + bainOptionName, false);
                ModOption bainOption = new ModOption(bainOptionName, false, true);
                bainMap.Add(new Tuple<string, ModOption>(bainDirectory, bainOption));
                bainOptions.Add(bainOption);
            }

            // STEP 2: Map entries to bain options
            _backgroundWorker.ReportMessage(Environment.NewLine + "Mapping assets to BAIN Options", true);
            foreach (IArchiveEntry entry in archive.Entries) {
                MapEntryToBainOption(bainMap, entry);
            }

            // Return the mod options we built
            _backgroundWorker.ReportMessage("Done.  " + bainOptions.Count + " BAIN Options found.", true);
            return bainOptions.OrderBy(x => x.Name).ToList();
        }

        private List<ModOption> AnalyzeFomodArchive(ModOption archiveModOption) {
            IArchive archive = archiveModOption.Archive;
            _backgroundWorker.ReportMessage("Parsing FOMOD Options", true);
            List<ModOption> fomodOptions = new List<ModOption>();

            // STEP 1: Find the fomod/ModuleConfig.xml file and extract it
            IArchiveEntry configEntry = archive.FindArchiveEntry(@"fomod\ModuleConfig.xml");
            _backgroundWorker.ReportMessage("Found FOMOD Config at " + configEntry.GetPath(), false);

            Directory.CreateDirectory(@".\fomod");
            configEntry.WriteToDirectory(@".\fomod", ExtractOptions.Overwrite);
            _backgroundWorker.ReportMessage("FOMOD Config Extracted", true);

            // STEP 2: Parse ModuleConfig.xml and determine what the mod options are
            FomodConfig fomodConfig = new FomodConfig(@".\fomod\ModuleConfig.xml");
            fomodOptions = fomodConfig.BuildModOptions(archiveModOption.BaseInstallerPath);

            // STEP 3: Loop through the archive's assets appending them to mod options per mapping
            _backgroundWorker.ReportMessage(Environment.NewLine + "Mapping assets to FOMOD Options", true);
            foreach (IArchiveEntry entry in archive.Entries) {
                MapEntryToFomodOption(fomodConfig.FileMap, entry, archiveModOption.BaseInstallerPath);
            }

            // STEP 4: Delete any options that have no assets or plugins in them
            _backgroundWorker.ReportMessage(Environment.NewLine + "Cleaning up...", true);
            fomodOptions.RemoveAll(ModOption.IsEmpty);

            // Return the mod options we built
            _backgroundWorker.ReportMessage("Done.  " + fomodOptions.Count + " FOMOD Options found.", true);
            return fomodOptions;
        }

        private void AnalyzeNormalArchive(ModOption archiveModOption) {
            IArchive archive = archiveModOption.Archive;
            foreach (IArchiveEntry modArchiveEntry in archive.Entries) {
                if (modArchiveEntry.IsDirectory)
                    continue;

                // append entry path to option assets
                string entryPath = modArchiveEntry.GetPath();
                archiveModOption.Assets.Add(entryPath);
                _backgroundWorker.ReportMessage(entryPath, false);

                // enqueue jobs for analyzing archives and plugins
                EnqueueAnalysisJob(modArchiveEntry, archiveModOption);
            }

            _modAnalysis.ModOptions.Add(archiveModOption);
        }

        public void AnalyzeMod(List<ModOption> archiveModOptions) {
            _backgroundWorker.RunWorkerAsync(archiveModOptions);
        }

        private void EnqueueAnalysisJob(IArchiveEntry entry, ModOption option) {
            if (entryJobExtensions.Contains(entry.GetEntryExtension())) {
                EntryAnalysisJob foundJob = entryAnalysisJobs.Find(job => job.Entry.Equals(entry));
                if (foundJob != null) {
                    foundJob.AddOption(option);
                }
                else {
                    entryAnalysisJobs.Add(new EntryAnalysisJob(entry, option));
                }
            }
        }

        private void SaveOutputFile(string filePath) {
            string filename = Path.Combine("output", Path.GetFileNameWithoutExtension(filePath));

            _backgroundWorker.ReportMessage("Saving JSON to " + filename + ".json...", true);
            File.WriteAllText(filename + ".json", JsonConvert.SerializeObject(_modAnalysis));
            _backgroundWorker.ReportMessage("All done.  JSON file saved to " + filename + ".json", true);
        }
    }
}
