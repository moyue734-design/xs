using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TM.Services.Modules.ProjectData.Models.Guides;
using TM.Services.Modules.ProjectData.Models.Tracking;

namespace TM.Services.Modules.ProjectData.Implementations
{
    public partial class FactSnapshotExtractor
    {
        #region 私有方法 - 冲突进度抽取

        private async Task<List<ConflictProgressSnapshot>> ExtractConflictProgressAsync(
            List<string> conflictIds,
            string? chapterId = null)
        {
            var result = new List<ConflictProgressSnapshot>();

            if (conflictIds == null || conflictIds.Count == 0)
                return result;

            try
            {
                var guide = await AggregateConflictProgressGuideAsync().ConfigureAwait(false);
                var comparer = Comparer<string>.Create(ChapterParserHelper.CompareChapterId);

                foreach (var conflictId in conflictIds)
                {
                    if (!guide.Conflicts.TryGetValue(conflictId, out var conflictEntry))
                        continue;

                    var allPoints = conflictEntry.ProgressPoints ?? new List<ConflictProgressPoint>();
                    List<ConflictProgressPoint> effectivePoints;
                    string effectiveStatus;

                    if (!string.IsNullOrWhiteSpace(chapterId) && allPoints.Count > 0)
                    {
                        effectivePoints = allPoints
                            .Where(p => IsChapterBefore(p.Chapter, chapterId))
                            .OrderBy(p => p.Chapter, comparer)
                            .ToList();

                        effectiveStatus = effectivePoints.Count == 0
                            ? "pending"
                            : (effectivePoints.LastOrDefault(p => !string.IsNullOrWhiteSpace(p.Status))?.Status ?? "active");
                    }
                    else
                    {
                        effectivePoints = allPoints;
                        effectiveStatus = conflictEntry.Status;
                    }

                    var recentProgress = effectivePoints
                        .Where(p => !string.IsNullOrWhiteSpace(p.Event))
                        .TakeLast(10)
                        .Select(p => $"{p.Chapter}: {p.Event}")
                        .ToList();

                    result.Add(new ConflictProgressSnapshot
                    {
                        Id = conflictId,
                        Name = conflictEntry.Name,
                        Status = effectiveStatus,
                        RecentProgress = recentProgress
                    });
                }
            }
            catch (Exception ex)
            {
                TM.App.Log($"[FactSnapshotExtractor] 抽取冲突进度失败: {ex.Message}");
            }

            return result;
        }

        #endregion
    }
}
