using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TM.Services.Modules.ProjectData.Models.Guides;
using TM.Services.Modules.ProjectData.Models.Tracking;

namespace TM.Services.Modules.ProjectData.Implementations
{
    public partial class FactSnapshotExtractor
    {
        #region 私有方法 - 伏笔状态抽取

        private async Task<List<ForeshadowingStatusSnapshot>> ExtractForeshadowingStatusAsync(
            List<string> setupIds,
            List<string> payoffIds,
            string? chapterId = null)
        {
            var result = new List<ForeshadowingStatusSnapshot>();

            var allIds = new HashSet<string>();
            if (setupIds != null) allIds.UnionWith(setupIds);
            if (payoffIds != null) allIds.UnionWith(payoffIds);

            if (allIds.Count == 0)
                return result;

            try
            {
                var guide = await _guideManager.GetGuideAsync<ForeshadowingStatusGuide>(ForeshadowingStatusGuideFileName).ConfigureAwait(false);

                foreach (var foreshadowId in allIds)
                {
                    if (!guide.Foreshadowings.TryGetValue(foreshadowId, out var entry))
                        continue;

                    var setupAllowed = string.IsNullOrWhiteSpace(chapterId)
                        || string.IsNullOrWhiteSpace(entry.ActualSetupChapter)
                        || IsChapterBefore(entry.ActualSetupChapter, chapterId);
                    var effectiveIsSetup = entry.IsSetup && setupAllowed;

                    var payoffAllowed = effectiveIsSetup
                        && (string.IsNullOrWhiteSpace(chapterId)
                            || string.IsNullOrWhiteSpace(entry.ActualPayoffChapter)
                            || IsChapterBefore(entry.ActualPayoffChapter, chapterId));
                    var effectiveIsResolved = entry.IsResolved && payoffAllowed;

                    result.Add(new ForeshadowingStatusSnapshot
                    {
                        Id = foreshadowId,
                        Name = entry.Name,
                        IsSetup = effectiveIsSetup,
                        IsResolved = effectiveIsResolved,
                        IsOverdue = effectiveIsSetup && !effectiveIsResolved && entry.IsOverdue,
                        SetupChapterId = effectiveIsSetup ? entry.ActualSetupChapter : string.Empty,
                        PayoffChapterId = effectiveIsResolved ? entry.ActualPayoffChapter : string.Empty
                    });
                }
            }
            catch (Exception ex)
            {
                TM.App.Log($"[FactSnapshotExtractor] 抽取伏笔状态失败: {ex.Message}");
            }

            return result;
        }

        #endregion
    }
}
