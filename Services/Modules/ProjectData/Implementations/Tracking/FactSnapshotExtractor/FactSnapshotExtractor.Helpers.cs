using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TM.Services.Modules.ProjectData.Models.Tracking;

namespace TM.Services.Modules.ProjectData.Implementations
{
    public partial class FactSnapshotExtractor
    {
        #region 私有方法 - 辅助

        private string GetPreviousChapterId(string chapterId)
        {
            var parsed = ChapterParserHelper.ParseChapterId(chapterId);
            if (parsed == null)
                return string.Empty;

            var (vol, ch) = parsed.Value;

            if (ch > 1)
            {
                return ChapterParserHelper.BuildChapterId(vol, ch - 1);
            }
            else if (vol > 1)
            {
                var lastChapterOfPrevVolume = GetLastChapterOfVolume(vol - 1);
                if (lastChapterOfPrevVolume > 0)
                {
                    return ChapterParserHelper.BuildChapterId(vol - 1, lastChapterOfPrevVolume);
                }
                TM.App.Log($"[FactSnapshotExtractor] 无法确定卷{vol - 1}的最后一章，跳过跨卷状态抽取");
                return string.Empty;
            }

            return string.Empty;
        }

        private int GetLastChapterOfVolume(int volumeNumber)
        {
            try
            {
                var chaptersPath = StoragePathHelper.GetProjectChaptersPath();
                if (!System.IO.Directory.Exists(chaptersPath))
                    return 0;

                var volumePrefix = $"vol{volumeNumber}_ch";
                var chapterFiles = System.IO.Directory.GetFiles(chaptersPath, $"vol{volumeNumber}_ch*.md");

                if (chapterFiles.Length == 0)
                    return 0;

                var maxChapter = chapterFiles
                    .Select(f => System.IO.Path.GetFileNameWithoutExtension(f))
                    .Select(name => ChapterParserHelper.ParseChapterId(name))
                    .Where(p => p != null)
                    .Select(p => p!.Value.chapterNumber)
                    .DefaultIfEmpty(0)
                    .Max();

                return maxChapter;
            }
            catch (Exception ex)
            {
                TM.App.Log($"[FactSnapshotExtractor] 查询卷{volumeNumber}最后一章失败: {ex.Message}");
                return 0;
            }
        }

        private static bool IsChapterBefore(string? entryChapter, string? targetChapterId)
        {
            if (string.IsNullOrWhiteSpace(targetChapterId))
                return true;
            if (string.IsNullOrWhiteSpace(entryChapter))
                return false;
            return ChapterParserHelper.CompareChapterId(entryChapter, targetChapterId) < 0;
        }

        private async Task<List<LocationStateSnapshot>> ExtractLocationStatesAsync(
            List<string>? locationIds, LayeredContextConfigSnapshot cfg, bool allVolumes = false, string? prevChapterId = null, string? chapterId = null)
        {
            var result = new List<LocationStateSnapshot>();
            try
            {
                var guide = await AggregateLocationStateGuideAsync(allVolumes).ConfigureAwait(false);
                var comparer = Comparer<string>.Create(ChapterParserHelper.CompareChapterId);
                var cutoffChapter = !string.IsNullOrWhiteSpace(chapterId) ? chapterId : null;

                var resolvedLocations = new Dictionary<string, (string Status, string Chapter)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (id, entry) in guide.Locations)
                {
                    if (entry.StateHistory == null || entry.StateHistory.Count == 0)
                    {
                        resolvedLocations[id] = (entry.CurrentStatus, string.Empty);
                        continue;
                    }

                    var validStates = cutoffChapter == null
                        ? entry.StateHistory
                        : entry.StateHistory.Where(s => IsChapterBefore(s.Chapter, cutoffChapter)).ToList();

                    if (validStates.Count == 0)
                    {
                        resolvedLocations[id] = ("normal", string.Empty);
                    }
                    else
                    {
                        var lastValid = validStates.OrderBy(s => s.Chapter, comparer).Last();
                        resolvedLocations[id] = (
                            !string.IsNullOrWhiteSpace(lastValid.Status) ? lastValid.Status : entry.CurrentStatus,
                            lastValid.Chapter ?? string.Empty);
                    }
                }

                if (locationIds == null || locationIds.Count > 0)
                {
                    var filterIds = (locationIds != null && locationIds.Count > 0)
                        ? new HashSet<string>(locationIds, StringComparer.OrdinalIgnoreCase)
                        : null;
                    foreach (var (id, entry) in guide.Locations)
                    {
                        if (filterIds != null && !filterIds.Contains(id)) continue;
                        var resolved = resolvedLocations[id];
                        result.Add(new LocationStateSnapshot
                        {
                            Id = id,
                            Name = entry.Name,
                            Status = resolved.Status,
                            ChapterId = resolved.Chapter
                        });
                    }
                }

                if (!string.IsNullOrEmpty(prevChapterId) && !allVolumes)
                {
                    var existingIds = new HashSet<string>(result.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
                    var chaptersPerVol = GetChaptersPerVol();
                    var activeSnapshots = new List<LocationStateSnapshot>();

                    foreach (var (id, entry) in guide.Locations)
                    {
                        if (existingIds.Contains(id)) continue;
                        if (entry.StateHistory == null || entry.StateHistory.Count == 0) continue;
                        var resolved = resolvedLocations[id];
                        if (string.IsNullOrEmpty(resolved.Chapter)) continue;
                        if (!IsActiveInRecentChapters(resolved.Chapter, prevChapterId, cfg.ActiveEntityWindowChapters, chaptersPerVol))
                            continue;
                        activeSnapshots.Add(new LocationStateSnapshot
                        {
                            Id = id,
                            Name = entry.Name,
                            Status = resolved.Status,
                            ChapterId = resolved.Chapter
                        });
                    }

                    var injected = activeSnapshots
                        .OrderByDescending(s => s.ChapterId, comparer)
                        .Take(cfg.ActiveEntityWindowMaxCount)
                        .ToList();
                    result.AddRange(injected);
                    if (injected.Count > 0)
                        TM.App.Log($"[FactSnapshotExtractor] 注入近期活跃地点: {injected.Count}条");
                }
            }
            catch (Exception ex)
            {
                TM.App.Log($"[FactSnapshotExtractor] 抽取地点状态失败: {ex.Message}");
            }
            return result;
        }

        private async Task<List<FactionStateSnapshot>> ExtractFactionStatesAsync(
            LayeredContextConfigSnapshot cfg, bool applyLimit = false, bool allVolumes = false, List<string>? priorityIds = null, string? prevChapterId = null, string? chapterId = null)
        {
            var result = new List<FactionStateSnapshot>();
            try
            {
                var guide = await AggregateFactionStateGuideAsync(allVolumes).ConfigureAwait(false);
                var factionComparer = Comparer<string>.Create(ChapterParserHelper.CompareChapterId);
                var cutoffChapter = !string.IsNullOrWhiteSpace(chapterId) ? chapterId : null;
                var resolvedChapters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (id, entry) in guide.Factions)
                {
                    string status;
                    string resolvedChapter;

                    if (entry.StateHistory == null || entry.StateHistory.Count == 0)
                    {
                        status = entry.CurrentStatus;
                        resolvedChapter = string.Empty;
                    }
                    else
                    {
                        var validStates = cutoffChapter == null
                            ? entry.StateHistory
                            : entry.StateHistory.Where(s => IsChapterBefore(s.Chapter, cutoffChapter)).ToList();

                        if (validStates.Count == 0)
                        {
                            status = "active";
                            resolvedChapter = string.Empty;
                        }
                        else
                        {
                            var lastValid = validStates.OrderBy(s => s.Chapter, factionComparer).Last();
                            status = !string.IsNullOrWhiteSpace(lastValid.Status) ? lastValid.Status : entry.CurrentStatus;
                            resolvedChapter = lastValid.Chapter ?? string.Empty;
                        }
                    }

                    resolvedChapters[id] = resolvedChapter;
                    result.Add(new FactionStateSnapshot
                    {
                        Id = id,
                        Name = entry.Name,
                        Status = status,
                        ChapterId = resolvedChapter
                    });
                }

                if (!string.IsNullOrEmpty(prevChapterId) && !allVolumes)
                {
                    var chaptersPerVol = GetChaptersPerVol();
                    var activeIds = new List<string>();
                    foreach (var (id, resolvedChapter) in resolvedChapters)
                    {
                        if (string.IsNullOrEmpty(resolvedChapter)) continue;
                        if (IsActiveInRecentChapters(resolvedChapter, prevChapterId, cfg.ActiveEntityWindowChapters, chaptersPerVol))
                            activeIds.Add(id);
                    }
                    if (activeIds.Count > 0)
                    {
                        priorityIds = priorityIds != null
                            ? priorityIds.Union(activeIds).Distinct().ToList()
                            : activeIds;
                        TM.App.Log($"[FactSnapshotExtractor] 近期活跃势力补入优先池: {activeIds.Count}条");
                    }
                }

                if (applyLimit)
                {
                    var max = cfg.SnapshotMaxFactionInject;
                    if (result.Count > max)
                    {
                        if (priorityIds != null && priorityIds.Count > 0)
                        {
                            var prioritySet = new HashSet<string>(priorityIds, StringComparer.OrdinalIgnoreCase);
                            var priority = result.Where(f => prioritySet.Contains(f.Id)).ToList();
                            var others = result
                                .Where(f => !prioritySet.Contains(f.Id))
                                .OrderByDescending(f => f.ChapterId, factionComparer)
                                .Take(Math.Max(0, max - priority.Count))
                                .ToList();
                            result = priority.Concat(others).ToList();
                        }
                        else
                        {
                            result = result
                                .OrderByDescending(f => f.ChapterId, factionComparer)
                                .Take(max)
                                .ToList();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TM.App.Log($"[FactSnapshotExtractor] 抽取势力状态失败: {ex.Message}");
            }
            return result;
        }

        private async Task<List<TimelineSnapshot>> ExtractTimelineAsync(LayeredContextConfigSnapshot cfg, bool allVolumes = false, string? chapterId = null)
        {
            var result = new List<TimelineSnapshot>();
            try
            {
                var guide = await AggregateTimelineGuideAsync(allVolumes).ConfigureAwait(false);
                var comparer = Comparer<string>.Create(ChapterParserHelper.CompareChapterId);
                var recentCount = cfg.SnapshotMaxTimelineInject;
                var cutoffChapter = !string.IsNullOrWhiteSpace(chapterId) ? chapterId : null;
                var recent = guide.ChapterTimeline
                    .Where(t => cutoffChapter == null || IsChapterBefore(t.ChapterId, cutoffChapter))
                    .OrderByDescending(t => t.ChapterId, comparer)
                    .Take(recentCount)
                    .OrderBy(t => t.ChapterId, comparer)
                    .ToList();

                foreach (var entry in recent)
                {
                    result.Add(new TimelineSnapshot
                    {
                        ChapterId = entry.ChapterId,
                        TimePeriod = entry.TimePeriod,
                        ElapsedTime = entry.ElapsedTime,
                        KeyTimeEvent = entry.KeyTimeEvent
                    });
                }
            }
            catch (Exception ex)
            {
                TM.App.Log($"[FactSnapshotExtractor] 抽取时间线失败: {ex.Message}");
            }
            return result;
        }

        private async Task<List<CharacterLocationSnapshot>> ExtractCharacterLocationsAsync(string prevChapterId, LayeredContextConfigSnapshot cfg, bool skipWindowFilter = false, List<string>? forceIncludeCharacterIds = null, string? chapterId = null)
        {
            var result = new List<CharacterLocationSnapshot>();
            try
            {
                var guide = await AggregateTimelineGuideAsync().ConfigureAwait(false);
                var comparer = Comparer<string>.Create(ChapterParserHelper.CompareChapterId);
                var cutoffChapter = !string.IsNullOrWhiteSpace(chapterId) ? chapterId : null;
                var forceSet = forceIncludeCharacterIds != null && forceIncludeCharacterIds.Count > 0
                    ? new System.Collections.Generic.HashSet<string>(forceIncludeCharacterIds, StringComparer.Ordinal)
                    : null;
                foreach (var (id, entry) in guide.CharacterLocations)
                {
                    string location = entry.CurrentLocation;
                    string lastUpdatedChapter = entry.LastUpdatedChapter;

                    if (cutoffChapter != null)
                    {
                        if (entry.MovementHistory != null && entry.MovementHistory.Count > 0)
                        {
                            var validMoves = entry.MovementHistory
                                .Where(m => IsChapterBefore(m.Chapter, cutoffChapter))
                                .OrderBy(m => m.Chapter, comparer)
                                .ToList();

                            if (validMoves.Count == 0)
                                continue;

                            var lastValidMove = validMoves.Last();
                            location = !string.IsNullOrWhiteSpace(lastValidMove.ToLocation)
                                ? lastValidMove.ToLocation
                                : entry.CurrentLocation;
                            lastUpdatedChapter = lastValidMove.Chapter;
                        }
                        else if (!string.IsNullOrWhiteSpace(entry.LastUpdatedChapter)
                                 && !IsChapterBefore(entry.LastUpdatedChapter, cutoffChapter))
                        {
                            continue;
                        }
                    }

                    if (!skipWindowFilter
                        && (forceSet == null || !forceSet.Contains(id))
                        && !IsActiveInRecentChapters(lastUpdatedChapter, prevChapterId, cfg.ActiveEntityWindowChapters, GetChaptersPerVol()))
                        continue;

                    result.Add(new CharacterLocationSnapshot
                    {
                        CharacterId = id,
                        CharacterName = entry.CharacterName,
                        CurrentLocation = location,
                        ChapterId = lastUpdatedChapter
                    });
                }
            }
            catch (Exception ex)
            {
                TM.App.Log($"[FactSnapshotExtractor] 抽取角色位置失败: {ex.Message}");
            }
            return result;
        }

        private async Task<List<ItemStateSnapshot>> ExtractItemStatesAsync(LayeredContextConfigSnapshot cfg, List<string>? characterIds = null, bool applyLimit = true, bool allVolumes = false, string? chapterId = null)
        {
            var result = new List<ItemStateSnapshot>();
            try
            {
                var guide = await AggregateItemStateGuideAsync(allVolumes).ConfigureAwait(false);
                var itemComparer = Comparer<string>.Create(ChapterParserHelper.CompareChapterId);
                var cutoffChapter = !string.IsNullOrWhiteSpace(chapterId) ? chapterId : null;
                var all = new List<ItemStateSnapshot>(guide.Items.Count);
                foreach (var (id, entry) in guide.Items)
                {
                    if (entry.StateHistory != null && entry.StateHistory.Count > 0 && cutoffChapter != null)
                    {
                        var validStates = entry.StateHistory
                            .Where(s => IsChapterBefore(s.Chapter, cutoffChapter))
                            .OrderBy(s => s.Chapter, itemComparer)
                            .ToList();

                        if (validStates.Count == 0)
                            continue;

                        var lastValid = validStates.Last();
                        all.Add(new ItemStateSnapshot
                        {
                            Id = id,
                            Name = entry.Name,
                            CurrentHolder = !string.IsNullOrWhiteSpace(lastValid.Holder) ? lastValid.Holder : entry.CurrentHolder,
                            Status = !string.IsNullOrWhiteSpace(lastValid.Status) ? lastValid.Status : entry.CurrentStatus,
                            ChapterId = lastValid.Chapter ?? string.Empty
                        });
                    }
                    else
                    {
                        var lastState = entry.StateHistory?.LastOrDefault();
                        all.Add(new ItemStateSnapshot
                        {
                            Id = id,
                            Name = entry.Name,
                            CurrentHolder = entry.CurrentHolder,
                            Status = entry.CurrentStatus,
                            ChapterId = lastState?.Chapter ?? string.Empty
                        });
                    }
                }

                if (!applyLimit)
                {
                    return all;
                }

                var maxInject = cfg.SnapshotMaxItemInject;
                if (characterIds != null && characterIds.Count > 0)
                {
                    var charSet = new System.Collections.Generic.HashSet<string>(characterIds, StringComparer.Ordinal);
                    var related = all.Where(i => charSet.Contains(i.CurrentHolder)).ToList();
                    var others = all
                        .Where(i => !charSet.Contains(i.CurrentHolder))
                        .OrderByDescending(i => i.ChapterId, itemComparer)
                        .ToList();
                    result.AddRange(related);
                    var remaining = maxInject - related.Count;
                    if (remaining > 0 && others.Count > 0)
                        result.AddRange(others.Take(remaining));
                    TM.App.Log($"[FactSnapshotExtractor] 物品注入: 关联{related.Count}条 + 补充{result.Count - related.Count}条");
                }
                else
                {
                    result = all.Count > maxInject
                        ? all.OrderByDescending(i => i.ChapterId, itemComparer).Take(maxInject).ToList()
                        : all;
                }
            }
            catch (Exception ex)
            {
                TM.App.Log($"[FactSnapshotExtractor] 抽取物品状态失败: {ex.Message}");
            }
            return result;
        }

        #endregion
    }
}
