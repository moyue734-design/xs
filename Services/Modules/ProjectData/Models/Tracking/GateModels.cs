using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace TM.Services.Modules.ProjectData.Models.Tracking
{
    public enum FailureType
    {
        Protocol,

        Consistency
    }

    public class GateFailure
    {
        [JsonPropertyName("Type")] public FailureType Type { get; set; }

        [JsonPropertyName("Errors")] public List<string> Errors { get; set; } = new();

        [JsonPropertyName("ConsistencyIssues")] public List<ConsistencyIssue> ConsistencyIssues { get; set; } = new();
    }

    public class GateResult
    {
        private static int ChangesTopLevelFieldCount => ChapterChanges.TopLevelFieldCount;
        private static readonly Regex ChangesProtocolViolationRegex = new(@"CHANGES协议违规：(.+?)必须为 ShortId", RegexOptions.Compiled);
        private static readonly Regex EmptyLedgerViolationRegex = new(@"CHANGES协议违规：(.+?)\s*账本无追踪记录，字段\s*(\S+)\s*应留空", RegexOptions.Compiled);
        private static readonly Regex MissingFieldRegex = new(@"CHANGES缺失必需字段:\s*([A-Za-z0-9_]+)", RegexOptions.Compiled);
        private static readonly Regex EntityColonRegex = new(@"实体:\s*([^,]+)", RegexOptions.Compiled);
        private static readonly Regex EntityIdRegex = new(@"EntityId[:\s]+([^\s,\]]+)", RegexOptions.Compiled);
        private static readonly Regex CharacterNotInRegex = new(@"角色\s+(\S+)\s+不在", RegexOptions.Compiled);
        private static readonly Regex ShortIdScrubRegex = new(@"\b[A-Z][0-9A-Z]{12}\b", RegexOptions.Compiled);

        private static string ScrubShortIds(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return ShortIdScrubRegex.Replace(text, m =>
            {
                var id = m.Value;
                var resolved = EntityNameResolver.Resolve(id);
                if (!string.IsNullOrWhiteSpace(resolved) && resolved != "未知实体")
                    return $"{resolved}({id})";
                return $"{id}(不在账本中)";
            });
        }

        [JsonPropertyName("ChapterId")] public string ChapterId { get; set; } = string.Empty;
        [JsonPropertyName("Success")] public bool Success { get; set; }
        [JsonPropertyName("Failures")] public List<GateFailure> Failures { get; set; } = new();
        [JsonPropertyName("ParsedChanges")] public ChapterChanges? ParsedChanges { get; set; }
        [JsonPropertyName("ContentWithoutChanges")] public string? ContentWithoutChanges { get; set; }

        public void AddFailure(FailureType type, IEnumerable<string> errors)
        {
            Failures.Add(new GateFailure
            {
                Type = type,
                Errors = errors.ToList()
            });
        }

        public void AddConsistencyFailure(List<ConsistencyIssue> issues)
        {
            Failures.Add(new GateFailure
            {
                Type = FailureType.Consistency,
                Errors = issues.Select(i => i.ToString()).ToList(),
                ConsistencyIssues = issues
            });
        }

        public void AddFailure(FailureType type, string error)
        {
            Failures.Add(new GateFailure
            {
                Type = type,
                Errors = new List<string> { error }
            });
        }

        public List<string> GetTopFailures(int count)
        {
            return Failures
                .SelectMany(f => f.Errors.Select(e => $"[{f.Type}] {e}"))
                .Take(count)
                .ToList();
        }

        public List<string> GetAllFailures()
        {
            return Failures
                .SelectMany(f => f.Errors.Select(e => $"[{f.Type}] {e}"))
                .ToList();
        }

        public List<string> GetHumanReadableFailures(int count)
        {
            return Failures
                .SelectMany(f =>
                {
                    if (f.Type == FailureType.Consistency && f.ConsistencyIssues.Count > 0)
                    {
                        return f.ConsistencyIssues.Select(i =>
                        {
                            var entityName = EntityNameResolver.Resolve(i.EntityId);
                            if (ConsistencyIssueRegistry.All.TryGetValue(i.IssueType, out var desc))
                            {
                                var detail = string.Equals(i.IssueType, IssueTypes.OmittedDeclaration, StringComparison.OrdinalIgnoreCase)
                                    ? i.Expected
                                    : i.Expected + "|" + i.Actual;
                                return desc.Humanize(entityName, detail);
                            }
                            return i.ToString();
                        });
                    }
                    return f.Errors.Select(e => HumanizeError(f.Type, e));
                })
                .Select(ScrubShortIds)
                .Take(count)
                .ToList();
        }

        private static string HumanizeError(FailureType type, string error)
        {
            if (type == FailureType.Protocol)
            {
                if (error.Contains("未识别到CHANGES区域"))
                    return $"正文末尾缺少变更摘要：请在正文结尾用成对的 {ChapterChanges.ChangesXmlOpen}...{ChapterChanges.ChangesXmlClose} 标签包裹合法 JSON 对象（仅半角字符、不得简写）。";
                if (error.Contains("CHANGES解析失败") || error.Contains("CHANGES JSON 不是对象") || error.Contains("CHANGES对象为空"))
                    return "CHANGES段的JSON格式错误，请检查JSON语法（括号、逗号、引号）";
                if (error.Contains("CHANGES缺失必需字段"))
                {
                    var mf = MissingFieldRegex.Match(error);
                    var missingField = mf.Success ? mf.Groups[1].Value : string.Empty;
                    var specificPart = string.IsNullOrEmpty(missingField) ? string.Empty : $"缺失字段：`{missingField}`。";
                    return $"CHANGES的JSON缺少必需字段（{specificPart}必须使用英文顶级键名，即使为空数组 [] 或空对象 {{}}）。请确保显式包含全部 {ChangesTopLevelFieldCount} 个顶级字段：角色状态变化(CharacterStateChanges)、冲突进度(ConflictProgress)、新情节(NewPlotPoints)、伏笔动作(ForeshadowingActions)、地点状态变化(LocationStateChanges)、势力状态变化(FactionStateChanges)、时间推进(TimeProgression)、角色移动(CharacterMovements)、物品流转(ItemTransfers)、秘密知情变化(SecretRevealChanges)、承诺约束变化(PledgeConstraintChanges)、倒计时约束变化(DeadlineConstraintChanges)";
                }
                if (error.Contains("CHANGES协议违规："))
                {
                    if (error.Contains("账本无追踪记录"))
                    {
                        var em = EmptyLedgerViolationRegex.Match(error);
                        if (em.Success)
                        {
                            var cn = em.Groups[1].Value.Trim();
                            var rawField = em.Groups[2].Value.Trim();
                            return $"{cn} 账本当前无追踪记录，字段 {HumanizeShortIdFieldPath(rawField)} 必须留空（输出空数组 [] 或空字符串 \"\"），禁止自造或强行引用实体。";
                        }
                        return error;
                    }
                    var m = ChangesProtocolViolationRegex.Match(error);
                    var fieldRaw = m.Success ? m.Groups[1].Value.Trim() : "实体ID字段";
                    var field = HumanizeShortIdFieldPath(fieldRaw);
                    if (error.Contains("提供ItemName", StringComparison.OrdinalIgnoreCase))
                        return $"{field} 不在账本中。若本章确实引入新物品，请同时填写 ItemName（显示名称），系统将自动创建物品并生成ShortId；否则请改为账本中已有物品";
                    if (error.Contains("提供SecretName", StringComparison.OrdinalIgnoreCase))
                        return $"{field} 不在账本中。若本章确实引入新秘密，请同时填写 SecretName（显示名称），系统将自动创建秘密并生成ShortId；否则请改为账本中已有秘密";
                    if (error.Contains("PledgeName", StringComparison.OrdinalIgnoreCase))
                        return $"{field} 不在账本中。若本章确实引入新承诺/契约，请设置 Action=\"create\" 并填写 PledgeName（显示名称），系统将自动创建并生成ShortId；若非新建则 PledgeId 必须来自账本中已有承诺";
                    if (error.Contains("DeadlineName", StringComparison.OrdinalIgnoreCase))
                        return $"{field} 不在账本中。若本章确实引入新倒计时/时限，请设置 Action=\"create\" 并填写 DeadlineName（显示名称），系统将自动创建并生成ShortId；若非新建则 DeadlineId 必须来自账本中已有倒计时";
                    if (error.Contains("LocationName", StringComparison.OrdinalIgnoreCase) || error.Contains("ToLocationName", StringComparison.OrdinalIgnoreCase))
                        return $"{field} 不在账本中。若本章角色移动到新地点，请同时填写 LocationName 或 ToLocationName（显示名称），系统将自动创建地点并生成ShortId；否则请改为账本中已有地点";
                    return $"{field} 指向的实体在账本中无法识别（名称匹配失败或ShortId不存在）。请确认该实体确实存在于本章事实账本中，或将该条目从 CHANGES 中移除";
                }
                return $"协议格式问题：{error}";
            }

            static string HumanizeShortIdFieldPath(string fieldRaw)
            {
                if (string.IsNullOrWhiteSpace(fieldRaw))
                    return "实体ID字段";
                var cn = ShortIdFieldRegistry.GetChineseName(fieldRaw);
                return string.Equals(cn, fieldRaw, StringComparison.Ordinal)
                    ? fieldRaw
                    : $"{cn}({fieldRaw})";
            }

            var issueType = ExtractIssueTypeFromError(error);
            if (!string.IsNullOrEmpty(issueType) && ConsistencyIssueRegistry.All.TryGetValue(issueType, out var descriptor))
            {
                var entityId = ExtractEntityId(error);
                var entityName = issueType == IssueTypes.PayoffBeforeSetup || issueType == IssueTypes.ForeshadowingRollback
                    ? EntityNameResolver.ResolveForeshadowing(entityId)
                    : issueType == IssueTypes.ConflictStatusSkip
                        ? EntityNameResolver.ResolveConflict(entityId)
                        : EntityNameResolver.ResolveCharacter(entityId);
                return descriptor.Humanize(entityName, error);
            }

            if (error.Contains("正文引入未登记实体"))
            {
                var entity = error;
                var colonIdx = entity.IndexOf(':');
                if (colonIdx >= 0 && colonIdx < entity.Length - 1)
                {
                    entity = entity[(colonIdx + 1)..].Trim();
                }
                entity = entity
                    .Replace("正文引入未登记实体(有剧情作用)", string.Empty)
                    .Replace("正文引入未登记实体(龙套)", string.Empty)
                    .Replace("正文引入未登记实体", string.Empty)
                    .Trim();
                return $"正文中出现了未在设定中登记的实体'{entity}'。请使用已登记的角色/地点名称，或移除该实体";
            }

            if (error.Contains("发色矛盾") || error.Contains("性格矛盾") || error.Contains("特征矛盾"))
            {
                return $"正文描述与设定不符：{error}。请修改正文使其与角色/地点设定一致";
            }

            if (error.Contains("世界观硬约束违反"))
            {
                return $"违反世界观设定：{error}。请修改正文使其符合世界观规则";
            }

            return error;
        }

        private static string ExtractEntityId(string error)
        {
            var match = EntityColonRegex.Match(error);
            if (match.Success)
                return match.Groups[1].Value.Trim();

            match = EntityIdRegex.Match(error);
            if (match.Success)
                return match.Groups[1].Value;

            match = CharacterNotInRegex.Match(error);
            if (match.Success)
                return match.Groups[1].Value;

            var colonIdx = error.IndexOf(':');
            if (colonIdx > 0 && colonIdx < error.Length - 1)
                return error.Substring(colonIdx + 1).Trim().Split(new[] { ' ', ',' })[0];

            return "未知实体";
        }

        private static string ExtractIssueTypeFromError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return string.Empty;
            var start = error.IndexOf('[');
            var end = error.IndexOf(']');
            if (start >= 0 && end > start)
                return error.Substring(start + 1, end - start - 1).Trim();
            return string.Empty;
        }
    }

    public class DesignElementNames
    {
        [JsonPropertyName("CharacterNames")] public List<string> CharacterNames { get; set; } = new();
        [JsonPropertyName("FactionNames")] public List<string> FactionNames { get; set; } = new();
        [JsonPropertyName("LocationNames")] public List<string> LocationNames { get; set; } = new();
        [JsonPropertyName("PlotKeyNames")] public List<string> PlotKeyNames { get; set; } = new();
        [JsonPropertyName("PovCharacterNames")] public List<string> PovCharacterNames { get; set; } = new();
    }
}
