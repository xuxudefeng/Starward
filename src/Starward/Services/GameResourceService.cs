﻿using Microsoft.Extensions.Logging;
using Starward.Core;
using Starward.Core.Gacha;
using Starward.Core.Launcher;
using Starward.Models;
using Starward.Services.Cache;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Starward.Services;

internal class GameResourceService
{


    private readonly ILogger<GameResourceService> _logger;


    private readonly LauncherClient _launcherClient;


    public GameResourceService(ILogger<GameResourceService> logger, LauncherClient launcherClient)
    {
        _logger = logger;
        _launcherClient = launcherClient;
    }







    public string? GetGameInstallPath(GameBiz biz)
    {
        var path = AppConfig.GetGameInstallPath(biz);
        if (string.IsNullOrWhiteSpace(path))
        {
            path = GachaLogClient.GetGameInstallPathFromRegistry(biz);
        }
        if (Directory.Exists(path))
        {
            return path;
        }
        else
        {
            _logger.LogInformation("Game uninstalled path not found ({biz})", biz);
            AppConfig.SetGameInstallPath(biz, null);
            return null;
        }
    }





    public bool IsGameExeExists(GameBiz biz)
    {
        var path = GetGameInstallPath(biz);
        if (path != null)
        {
            var exe = Path.Combine(path, GetGameExeName(biz));
            return File.Exists(exe);
        }
        return false;
    }



    public static string GetGameExeName(GameBiz biz)
    {
        return biz switch
        {
            GameBiz.hk4e_cn => "YuanShen.exe",
            GameBiz.hk4e_global => "GenshinImpact.exe",
            GameBiz.hk4e_cloud => "Genshin Impact Cloud Game.exe",
            _ => biz.ToGame() switch
            {
                GameBiz.StarRail => "StarRail.exe",
                GameBiz.Honkai3rd => "BH3.exe",
                _ => throw new ArgumentOutOfRangeException($"Unknown region {biz}"),
            },
        };
    }



    public async Task<Version?> GetGameLocalVersionAsync(GameBiz biz, string? installPath = null)
    {
        installPath ??= GetGameInstallPath(biz);
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }
        Version? version = null;
        var config = Path.Join(installPath, "config.ini");
        if (File.Exists(config))
        {
            var str = await File.ReadAllTextAsync(config);
            var ver = Regex.Match(str, @"game_version=(.+)").Groups[1].Value;
            Version.TryParse(ver, out version);
        }
        else
        {
            _logger.LogWarning("config.ini not found: {path}", config);
        }
        _logger.LogInformation("Local game version is {version} (gameBiz: {biz})", version, biz);
        return version;
    }



    public async Task<LauncherGameResource> GetGameResourceAsync(GameBiz biz)
    {
        var resource = MemoryCache.Instance.GetItem<LauncherGameResource>($"LauncherResource_{biz}", TimeSpan.FromSeconds(10));
        if (resource is null)
        {
            resource = await _launcherClient.GetLauncherGameResourceAsync(biz);
            MemoryCache.Instance.SetItem($"LauncherResource_{biz}", resource);
        }
        return resource;
    }



    public async Task<(Version? LatestVersion, Version? PreDownloadVersion)> GetGameResourceVersionAsync(GameBiz biz)
    {
        var resource = await GetGameResourceAsync(biz);
        _ = Version.TryParse(resource.Game?.Latest?.Version, out Version? latest);
        _ = Version.TryParse(resource.PreDownloadGame?.Latest?.Version, out Version? preDownload);
        return (latest, preDownload);
    }



    public async Task<bool> CheckPreDownloadIsOKAsync(GameBiz biz)
    {
        string? installPath = GetGameInstallPath(biz);
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return false;
        }
        var resource = await GetGameResourceAsync(biz);
        if (resource.PreDownloadGame != null)
        {
            var localVersion = await GetGameLocalVersionAsync(biz, installPath);
            if (resource.PreDownloadGame.Diffs?.FirstOrDefault(x => x.Version == localVersion?.ToString()) is DiffPackage diff)
            {
                string file = Path.Combine(installPath, diff.Name);
                if (!File.Exists(file))
                {
                    return false;
                }
                var flag = await GetVoiceLanguageAsync(biz, installPath);
                foreach (var lang in Enum.GetValues<VoiceLanguage>())
                {
                    if (flag.HasFlag(lang))
                    {
                        if (diff.VoicePacks.FirstOrDefault(x => x.Language == lang.ToDescription()) is VoicePack pack)
                        {
                            file = Path.Combine(installPath, pack.Name);
                            if (!File.Exists(file))
                            {
                                return false;
                            }
                        }
                    }
                }
                return true;
            }
            else
            {
                string file = Path.Combine(installPath, resource.PreDownloadGame.Latest.Name);
                if (!File.Exists(file))
                {
                    return false;
                }
                var flag = await GetVoiceLanguageAsync(biz, installPath);
                foreach (var lang in Enum.GetValues<VoiceLanguage>())
                {
                    if (flag.HasFlag(lang))
                    {
                        if (resource.PreDownloadGame.Latest.VoicePacks.FirstOrDefault(x => x.Language == lang.ToDescription()) is VoicePack pack)
                        {
                            file = Path.Combine(installPath, pack.Name);
                            if (!File.Exists(file))
                            {
                                return false;
                            }
                        }
                    }
                }
                return true;
            }
        }
        return false;
    }



    public async Task<DownloadGameResource?> CheckDownloadGameResourceAsync(GameBiz biz, string installPath)
    {
        var localVersion = await GetGameLocalVersionAsync(biz, installPath);
        (Version? latestVersion, Version? preDownloadVersion) = await GetGameResourceVersionAsync(biz);
        var resource = await GetGameResourceAsync(biz);
        GameResource? gameResource = null;

        if (localVersion is null)
        {
            gameResource = resource.Game;
        }
        else if (preDownloadVersion != null)
        {
            gameResource = resource.PreDownloadGame;
        }
        else if (latestVersion > localVersion)
        {
            gameResource = resource.Game;
        }


        if (gameResource != null)
        {
            var downloadGameResource = new DownloadGameResource();
            downloadGameResource.FreeSpace = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(installPath))!).AvailableFreeSpace;
            if (gameResource.Diffs?.FirstOrDefault(x => x.Version == localVersion?.ToString()) is DiffPackage diff)
            {
                downloadGameResource.Game = CheckDownloadPackage(diff, installPath);
                foreach (var pack in diff.VoicePacks)
                {
                    var state = CheckDownloadPackage(pack, installPath);
                    state.Name = pack.Language;
                    downloadGameResource.Voices.Add(state);
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(gameResource.Latest.Path))
                {
                    var state = new DownloadPackageState
                    {
                        PackageSize = gameResource.Latest.PackageSize,
                        DecompressedSize = gameResource.Latest.Size,
                    };
                    var size = gameResource.Latest.Segments.Sum(x => CheckDownloadPackage(Path.GetFileName(x.Path), installPath));
                    state.DownloadedSize = size;
                    downloadGameResource.Game = state;
                }
                else
                {
                    downloadGameResource.Game = CheckDownloadPackage(gameResource.Latest, installPath);
                }
                foreach (var pack in gameResource.Latest.VoicePacks)
                {
                    var state = CheckDownloadPackage(pack, installPath);
                    state.Name = pack.Language;
                    downloadGameResource.Voices.Add(state);
                }
            }
            return downloadGameResource;
        }

        return null;

    }


    private DownloadPackageState CheckDownloadPackage(IGamePackage package, string installPath)
    {
        var state = new DownloadPackageState
        {
            Name = Path.GetFileName(package.Path),
            Url = package.Path,
            PackageSize = package.PackageSize,
            DecompressedSize = package.Size,
        };
        var file = Path.Join(installPath, state.Name);
        if (File.Exists(file))
        {
            state.DownloadedSize = new FileInfo(file).Length;
        }
        else
        {
            file = Path.Join(installPath, $"{state.Name}_tmp");
            if (File.Exists(file))
            {
                state.DownloadedSize = new FileInfo(file).Length;
            }
            else
            {
                state.DownloadedSize = 0;
            }
        }
        return state;
    }


    private long CheckDownloadPackage(string name, string installPath)
    {
        var file = Path.Join(installPath, name);
        if (File.Exists(file))
        {
            return new FileInfo(file).Length;
        }
        else
        {
            file = Path.Join(installPath, $"{name}_tmp");
            if (File.Exists(file))
            {
                return new FileInfo(file).Length;
            }
            else
            {
                return 0;
            }
        }
    }



    public async Task<VoiceLanguage> GetVoiceLanguageAsync(GameBiz biz, string? installPath = null)
    {
        installPath ??= GetGameInstallPath(biz);
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return VoiceLanguage.None;
        }
        var file = biz switch
        {
            GameBiz.hk4e_cn => Path.Join(installPath, @"YuanShen_Data\Persistent\audio_lang_14"),
            GameBiz.hk4e_global => Path.Join(installPath, @"GenshinImpact_Data\Persistent\audio_lang_14"),
            GameBiz.hkrpg_cn or GameBiz.hkrpg_global => Path.Join(installPath, @"StarRail_Data\Persistent\AudioLaucherRecord.txt"),
            _ => ""
        };
        if (!File.Exists(file))
        {
            file = biz switch
            {
                GameBiz.hk4e_global => Path.Join(installPath, @"YuanShen_Data\Persistent\audio_lang_14"),
                GameBiz.hk4e_cn => Path.Join(installPath, @"GenshinImpact_Data\Persistent\audio_lang_14"),
                GameBiz.hkrpg_cn or GameBiz.hkrpg_global => Path.Join(installPath, @"StarRail_Data\Persistent\AudioLaucherRecord.txt"),
                _ => ""
            };
        }
        var flag = VoiceLanguage.None;
        if (File.Exists(file))
        {
            var lines = await File.ReadAllLinesAsync(file);
            if (lines.Any(x => x.Contains("Chinese"))) { flag |= VoiceLanguage.Chinese; }
            if (lines.Any(x => x.Contains("English(US)"))) { flag |= VoiceLanguage.English; }
            if (lines.Any(x => x.Contains("Japanese"))) { flag |= VoiceLanguage.Japanese; }
            if (lines.Any(x => x.Contains("Korean"))) { flag |= VoiceLanguage.Korean; }
        }
        return flag;
    }



    public async Task SetVoiceLanguageAsync(GameBiz biz, string installPath, VoiceLanguage lang)
    {
        if (biz is GameBiz.hk4e_cn or GameBiz.hk4e_global or GameBiz.hkrpg_cn or GameBiz.hkrpg_global)
        {
            var file = biz switch
            {
                GameBiz.hk4e_cn => Path.Join(installPath, @"YuanShen_Data\Persistent\audio_lang_14"),
                GameBiz.hk4e_global => Path.Join(installPath, @"GenshinImpact_Data\Persistent\audio_lang_14"),
                GameBiz.hkrpg_cn or GameBiz.hkrpg_global => Path.Join(installPath, @"StarRail_Data\Persistent\AudioLaucherRecord.txt"),
                _ => ""
            };
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var lines = new List<string>(4);
            if (lang.HasFlag(VoiceLanguage.Chinese)) { lines.Add("Chinese"); }
            if (lang.HasFlag(VoiceLanguage.English)) { lines.Add("English(US)"); }
            if (lang.HasFlag(VoiceLanguage.Japanese)) { lines.Add("Japanese"); }
            if (lang.HasFlag(VoiceLanguage.Korean)) { lines.Add("Korean"); }
            await File.WriteAllLinesAsync(file, lines);
        }
    }





}