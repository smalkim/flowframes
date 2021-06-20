﻿using Flowframes.Data;
using Flowframes.Main;
using Flowframes.MiscUtils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Flowframes.IO
{
    class ModelDownloader
    {

        static string GetMdlUrl (string ai, string relPath)
        {
            string baseUrl = Config.Get(Config.Key.mdlBaseUrl);
            return Path.Combine(baseUrl, ai.ToLower(), relPath);
        }

        static string GetMdlFileUrl(string ai, string model, string relPath)
        {
            return Path.Combine(GetMdlUrl(ai, model), relPath);
        }

        static string GetLocalPath(string ai, string model)
        {
            return Path.Combine(Paths.GetPkgPath(), ai, model);
        }

        static async Task DownloadTo (string url, string saveDirOrPath, int retries = 3)
        {
            string savePath = saveDirOrPath;

            if (IOUtils.IsPathDirectory(saveDirOrPath))
                savePath = Path.Combine(saveDirOrPath, Path.GetFileName(url));

            IOUtils.TryDeleteIfExists(savePath);
            Directory.CreateDirectory(Path.GetDirectoryName(savePath));
            Logger.Log($"Downloading '{url}' to '{savePath}'", true);
            Stopwatch sw = new Stopwatch();
            sw.Restart();
            bool completed = false;
            int lastProgPercentage = -1;
            var client = new WebClient();

            client.DownloadProgressChanged += (sender, args) =>
            {
                if (sw.ElapsedMilliseconds > 200 && args.ProgressPercentage != lastProgPercentage)
                {
                    sw.Restart();
                    lastProgPercentage = args.ProgressPercentage;
                    Logger.Log($"Downloading model file '{Path.GetFileName(url)}'... {args.ProgressPercentage}%", false, true);
                }
            };
            client.DownloadFileCompleted += (sender, args) =>
            {
                if (args.Error != null)
                    Logger.Log("Download failed: " + args.Error.Message);
                completed = true;
            };

            client.DownloadFileTaskAsync(url, savePath).ConfigureAwait(false);

            while (!completed)
            {
                if (Interpolate.canceled)
                {
                    client.CancelAsync();
                    client.Dispose();
                    return;
                }
                if (sw.ElapsedMilliseconds > 6000)
                {
                    client.CancelAsync();
                    if(retries > 0)
                    {
                        await DownloadTo(url, saveDirOrPath, retries--);
                    }
                    else
                    {
                        Interpolate.Cancel("Model download failed.");
                        return;
                    }
                }
                await Task.Delay(500);
            }
            Logger.Log($"Downloaded '{Path.GetFileName(url)}' ({IOUtils.GetFilesize(savePath) / 1024} KB)", true);
        }

        class ModelFile
        {
            public string filename;
            public string dir;
            public long size;
            public string crc32;
        }

        static List<ModelFile> GetModelFilesFromJson (string json)
        {
            List<ModelFile> modelFiles = new List<ModelFile>();

            try
            {
                dynamic data = JsonConvert.DeserializeObject(json);

                foreach (var item in data)
                {
                    string dirString = ((string)item.dir).Replace(@"\", @"/");
                    if (dirString.Length > 0 && dirString[0] == '/') dirString = dirString.Remove(0, 1);
                    long sizeLong = long.Parse((string)item.size);
                    modelFiles.Add(new ModelFile { filename = item.filename, dir = dirString, size = sizeLong, crc32 = item.crc32 });
                }
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to parse model file list from JSON: {e.Message}", true);
            } 

            return modelFiles;
        }

        public static async Task DownloadModelFiles (AI ai, string modelDir)
        {
            string aiDir = ai.pkgDir;
            Logger.Log($"DownloadModelFiles(string ai = {ai}, string model = {modelDir})", true);

            try
            {
                string mdlDir = GetLocalPath(aiDir, modelDir);

                if (AreFilesValid(aiDir, modelDir))
                    return;

                Logger.Log($"Downloading '{modelDir}' model files...");
                Directory.CreateDirectory(mdlDir);

                await DownloadTo(GetMdlFileUrl(aiDir, modelDir, "files.json"), mdlDir);

                List<ModelFile> modelFiles = GetModelFilesFromJson(File.ReadAllText(Path.Combine(mdlDir, "files.json")));

                if (modelFiles.Count < 1)
                {
                    Interpolate.Cancel($"Error: Can't download model files because no entries were loaded from files.json. Please try again.");
                    return;
                }

                foreach (ModelFile mf in modelFiles)
                {
                    string relPath = Path.Combine(mf.dir, mf.filename).Replace("\\", "/");
                    await DownloadTo(GetMdlFileUrl(aiDir, modelDir, relPath), Path.Combine(mdlDir, relPath));
                }

                Logger.Log($"Downloaded \"{modelDir}\" model files.", false, true);

                if (!AreFilesValid(aiDir, modelDir))
                    Interpolate.Cancel($"Model files are invalid! Please try again.");
            }
            catch (Exception e)
            {
                Logger.Log($"DownloadModelFiles Error: {e.Message}\nStack Trace:\n{e.StackTrace}");
                Interpolate.Cancel($"Error downloading model files: {e.Message}");
            }
        }

        public static void DeleteAllModels ()
        {
            foreach(string modelFolder in GetAllModelFolders())
            {
                string size = FormatUtils.Bytes(IOUtils.GetDirSize(modelFolder, true));
                if (IOUtils.TryDeleteIfExists(modelFolder))
                    Logger.Log($"Deleted cached model '{Path.GetFileName(modelFolder.GetParentDir())}/{Path.GetFileName(modelFolder)}' ({size})");
            }
        }

        public static List<string> GetAllModelFolders()
        {
            List<string> modelPaths = new List<string>();

            foreach (AI ai in Networks.networks)
            {
                string aiPkgFolder = Path.Combine(Paths.GetPkgPath(), ai.pkgDir);
                ModelCollection aiModels = AiModels.GetModels(ai);

                foreach(ModelCollection.ModelInfo model in aiModels.models)
                {
                    string mdlFolder = Path.Combine(aiPkgFolder, model.dir);

                    if (Directory.Exists(mdlFolder))
                        modelPaths.Add(mdlFolder);
                }
            }

            return modelPaths;
        }

        public static bool AreFilesValid (string ai, string model)
        {
            string mdlDir = GetLocalPath(ai, model);

            if (!Directory.Exists(mdlDir))
            {
                Logger.Log($"Files for model {model} not valid: {mdlDir} does not exist.", true);
                return false;
            }

            // TODO UNCOMMENT
            //if (Debugger.IsAttached)    // Disable MD5 check in dev environment
            //    return true;

            string md5FilePath = Path.Combine(mdlDir, "files.json");

            if (!File.Exists(md5FilePath) || IOUtils.GetFilesize(md5FilePath) < 32)
            {
                Logger.Log($"Files for model {model} not valid: {mdlDir} does not exist or is incomplete.", true);
                return false;
            }

            List<ModelFile> modelFiles = GetModelFilesFromJson(File.ReadAllText(Path.Combine(mdlDir, "files.json")));

            if (modelFiles.Count < 1)
            {
                Logger.Log($"Files for model {model} not valid: JSON contains {modelFiles.Count} entries.", true);
                return false;
            }

            foreach (ModelFile mf in modelFiles)
            {
                string crc = IOUtils.GetHash(Path.Combine(mdlDir, mf.dir, mf.filename), IOUtils.Hash.CRC32);

                if (crc.Trim() != mf.crc32.Trim())
                {
                    Logger.Log($"Files for model {model} not valid: CRC32 of {mf.filename} ({crc.Trim()}) does not equal validation CRC32 ({mf.crc32.Trim()}).", true);
                    return false;
                }
            }

            return true;
        }

        static Dictionary<string, string> GetDict (string[] lines, char sep = ':')
        {
            Dictionary<string, string> dict = new Dictionary<string, string>();

            foreach (string line in lines)
            {
                if (line.Length < 3) continue;
                string[] keyValuePair = line.Split(':');
                dict.Add(keyValuePair[0], keyValuePair[1]);
            }

            return dict;
        }
    }
}
