using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TM.Framework.Common.Helpers.Id;
using TM.Services.Modules.ProjectData.Models.Guides;
using TM.Services.Modules.ProjectData.Models.Tracking;

namespace TM.Services.Modules.ProjectData.Implementations
{
    public class SecretRevealService
    {
        private readonly GuideManager _guideManager;

        public SecretRevealService(GuideManager guideManager)
        {
            _guideManager = guideManager;
        }

        private const string BaseFileName = "secret_reveal_guide.json";
        private static string VolumeFileName(string chapterId) =>
            GuideManager.GetVolumeFileName(BaseFileName,
                ChapterParserHelper.ParseChapterIdOrDefault(chapterId).volumeNumber);

        public async Task UpdateSecretRevealAsync(string chapterId, SecretRevealChange change)
        {
            if (string.IsNullOrWhiteSpace(change.SecretId) && string.IsNullOrWhiteSpace(change.SecretName)) return;
            if (change.NewKnowerIds == null || change.NewKnowerIds.Count == 0) return;

            var volFile = VolumeFileName(chapterId);
            var guide = await _guideManager.GetGuideAsync<SecretRevealGuide>(volFile).ConfigureAwait(false);

            var systemId = FindExistingSecretId(guide, change.SecretId, change.SecretName);

            if (systemId == null)
            {
                systemId = ShortIdGenerator.IsLikelyId(change.SecretId)
                    ? change.SecretId
                    : ShortIdGenerator.New("S");

                var displayName = !string.IsNullOrWhiteSpace(change.SecretName)
                    ? change.SecretName
                    : (!string.IsNullOrWhiteSpace(change.SecretId) ? change.SecretId : systemId);
                if (!guide.Secrets.ContainsKey(systemId))
                {
                    guide.Secrets[systemId] = new SecretRevealEntry
                    {
                        Name = displayName,
                        CurrentStatus = "hidden"
                    };
                    TM.App.Log($"[SecretReveal] 自动创建秘密条目: {systemId} ({displayName})");
                }
            }

            var entry = guide.Secrets[systemId];

            if (!string.IsNullOrWhiteSpace(change.SecretName))
                entry.Name = change.SecretName;

            foreach (var knowerId in change.NewKnowerIds)
            {
                if (!string.IsNullOrWhiteSpace(knowerId) && !entry.KnowerIds.Contains(knowerId, StringComparer.OrdinalIgnoreCase))
                    entry.KnowerIds.Add(knowerId);
            }

            entry.CurrentStatus = ComputeStatus(entry.KnowerIds.Count);

            entry.RevealHistory.Add(new SecretRevealPoint
            {
                Chapter = chapterId,
                NewKnowerIds = change.NewKnowerIds.ToList(),
                Method = change.Method,
                KeyEvent = change.KeyEvent,
                Importance = string.IsNullOrWhiteSpace(change.Importance) ? "normal" : change.Importance,
                CausedBy = change.CausedBy ?? string.Empty
            });

            _guideManager.MarkDirty(volFile);
            TM.App.Log($"[SecretReveal] {systemId} 在 {chapterId} 新增知情者: {string.Join(",", change.NewKnowerIds)}, 状态={entry.CurrentStatus}");
        }

        public async Task RemoveChapterDataAsync(string chapterId)
        {
            var volFile = VolumeFileName(chapterId);
            var guide = await _guideManager.GetGuideAsync<SecretRevealGuide>(volFile).ConfigureAwait(false);
            var modified = false;

            foreach (var (_, entry) in guide.Secrets)
            {
                var removed = entry.RevealHistory.RemoveAll(p =>
                    string.Equals(p.Chapter, chapterId, StringComparison.Ordinal));

                if (removed > 0)
                {
                    entry.KnowerIds = entry.RevealHistory
                        .SelectMany(p => p.NewKnowerIds)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    entry.CurrentStatus = ComputeStatus(entry.KnowerIds.Count);
                    modified = true;
                }
            }

            if (modified)
            {
                _guideManager.MarkDirty(volFile);
                TM.App.Log($"[SecretReveal] 已移除章节 {chapterId} 的秘密揭示记录并重算知情状态");
            }
        }

        public async Task<List<SecretStateSnapshot>> ExtractSnapshotAsync(string chapterId, bool includeCurrentChapter = false)
        {
            var result = new List<SecretStateSnapshot>();

            try
            {
                var volNumbers = _guideManager.GetExistingVolumeNumbers(BaseFileName);
                if (volNumbers.Count == 0) return result;

                var comparer = Comparer<string>.Create(ChapterParserHelper.CompareChapterId);
                var hasCutoff = !string.IsNullOrWhiteSpace(chapterId);

                foreach (var vol in volNumbers)
                {
                    var guide = await _guideManager.GetGuideAsync<SecretRevealGuide>(
                        GuideManager.GetVolumeFileName(BaseFileName, vol)).ConfigureAwait(false);

                    foreach (var (id, entry) in guide.Secrets)
                    {
                        if (result.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))) continue;

                        List<string> knowerIds;
                        string status;
                        string latestChapter;

                        if (hasCutoff && entry.RevealHistory != null && entry.RevealHistory.Count > 0)
                        {
                            var validPoints = entry.RevealHistory
                                .Where(p => !string.IsNullOrWhiteSpace(p.Chapter)
                                    && (includeCurrentChapter
                                        ? ChapterParserHelper.CompareChapterId(p.Chapter, chapterId) <= 0
                                        : ChapterParserHelper.CompareChapterId(p.Chapter, chapterId) < 0))
                                .OrderBy(p => p.Chapter, comparer)
                                .ToList();

                            if (validPoints.Count == 0) continue;

                            knowerIds = validPoints
                                .SelectMany(p => p.NewKnowerIds ?? Enumerable.Empty<string>())
                                .Where(k => !string.IsNullOrWhiteSpace(k))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            if (knowerIds.Count == 0) continue;

                            status = ComputeStatus(knowerIds.Count);
                            latestChapter = validPoints.Last().Chapter;
                        }
                        else
                        {
                            if (entry.KnowerIds.Count == 0) continue;
                            knowerIds = entry.KnowerIds.ToList();
                            status = entry.CurrentStatus;
                            latestChapter = entry.RevealHistory?.LastOrDefault()?.Chapter ?? string.Empty;
                        }

                        result.Add(new SecretStateSnapshot
                        {
                            Id = id,
                            Name = entry.Name,
                            KnowerIds = knowerIds,
                            Status = status,
                            ChapterId = latestChapter
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                TM.App.Log($"[SecretReveal] 快照提取失败: {ex.Message}");
            }

            return result;
        }

        private static string? FindExistingSecretId(SecretRevealGuide guide, string aiSecretId, string? aiSecretName)
        {
            if (guide.Secrets.ContainsKey(aiSecretId))
                return aiSecretId;

            foreach (var (id, entry) in guide.Secrets)
            {
                if (string.Equals(entry.Name, aiSecretId, StringComparison.OrdinalIgnoreCase))
                    return id;
                if (!string.IsNullOrWhiteSpace(aiSecretName) &&
                    string.Equals(entry.Name, aiSecretName, StringComparison.OrdinalIgnoreCase))
                    return id;
            }

            return null;
        }

        private static string ComputeStatus(int knowerCount) => knowerCount switch
        {
            0 => "hidden",
            1 => "hidden",
            <= 3 => "partially_known",
            _ => "widely_known"
        };
    }
}
