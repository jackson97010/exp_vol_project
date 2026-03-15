using System;
using System.Collections.Generic;
using System.Linq;
using StrongestVwap.Core.Models;

namespace StrongestVwap.Strategy
{
    public class GroupDefinition
    {
        public string GroupName { get; set; } = "";
        public List<string> MemberStockIds { get; set; } = new();
    }

    public class GroupState
    {
        public string GroupName { get; set; } = "";
        public double TotalMonthlyAvgTradingValue { get; set; }
        public double AveragePctChange { get; set; }
        public double TodayTradingValueRatio { get; set; }
        public bool IsValid { get; set; }
        public int Rank { get; set; }
        public List<MemberState> Members { get; set; } = new();
    }

    public class MemberState
    {
        public string StockId { get; set; } = "";
        public double VwapChangePct { get; set; }
        public double Vwap5mChangePct { get; set; }
        public double PriceChangePct { get; set; }
        public double MonthlyAvgTradingValue { get; set; }
        public double TodayCumulativeValue { get; set; }
        public int TotalRank { get; set; }       // All members (including limit-up locked)
        public int RawRank { get; set; }          // Excluding limit-up locked
        public int FilteredRank { get; set; }     // Excluding limit-up + disposition + prev limit-up
        public bool IsLimitUpLocked { get; set; }
        public bool IsDisposition { get; set; }
        public bool IsPrevDayLimitUp { get; set; }
        public DateTime? FirstLimitUpTime { get; set; }
        public bool PassesCond1 { get; set; } = true;
        public bool PassesCond2 { get; set; } = true;
        public bool PassesCond4 { get; set; } = true;
    }

    public class StrongGroupScreener
    {
        private readonly StrategyConfig _config;
        private readonly Dictionary<string, GroupDefinition> _groups;
        private readonly Dictionary<string, GroupState> _groupStates = new();

        private readonly double _memberMinMonthVal;
        private readonly double _groupMinMonthVal;
        private readonly double _groupMinAvgPctChg;
        private readonly double _groupMinValRatio;
        private readonly bool _isWeightedAvg;
        private readonly int _groupValidTopN;
        private readonly int _topGroupRankThreshold;
        private readonly int _topGroupMaxSelect;
        private readonly int _normalGroupMaxSelect;
        private readonly double _entryMinVwapPctChg;
        private readonly bool _requireRawM1;
        private readonly bool _cond1Enabled;
        private readonly bool _cond2Enabled;
        private readonly bool _cond4Enabled;
        private readonly double _memberStrongVolRatio;
        private readonly double _memberStrongTradingVal;
        private readonly double _memberVwapPctChgThreshold;
        private readonly double _groupVolExemptThreshold;
        private readonly bool _excludePrevLimitUp;
        private readonly bool _excludeDisposition;

        // Mode E: dynamic member selection
        private readonly bool _modeEEnabled;
        private readonly int _modeELargeGroupThreshold;
        private readonly int _modeELargeGroupSelect;
        private readonly int _modeESmallGroupSelect;
        private readonly bool _modeELimitUpCascade;
        private readonly int _modeEMaxLimitUpSkip;

        // Bypass group screening: all stocks pass as-is
        private readonly bool _bypassGroupScreening;

        // Member rank field: "vwap" (default) or "vwap_5m"
        private readonly string _memberRankField;

        public StrongGroupScreener(StrategyConfig config, Dictionary<string, GroupDefinition> groups)
        {
            _config = config;
            _groups = groups;

            _memberMinMonthVal = config.GetDouble("member_min_month_trading_val", 200_000_000);
            _groupMinMonthVal = config.GetDouble("group_min_month_trading_val", 3_000_000_000);
            _groupMinAvgPctChg = config.GetDouble("group_min_avg_pct_chg", 0.01);
            _groupMinValRatio = config.GetDouble("group_min_val_ratio", 1.2);
            _isWeightedAvg = config.GetBool("is_weighted_avg", false);
            _groupValidTopN = config.GetInt("group_valid_top_n", 20);
            _topGroupRankThreshold = config.GetInt("top_group_rank_threshold", 10);
            _topGroupMaxSelect = config.GetInt("top_group_max_select", 1);
            _normalGroupMaxSelect = config.GetInt("normal_group_max_select", 1);
            _entryMinVwapPctChg = config.GetDouble("entry_min_vwap_pct_chg", 0.04);
            _requireRawM1 = config.GetBool("require_raw_m1", true);
            _cond1Enabled = config.GetBool("member_cond1_enabled", false);
            _cond2Enabled = config.GetBool("member_cond2_enabled", false);
            _cond4Enabled = config.GetBool("member_cond4_enabled", false);
            _memberStrongVolRatio = config.GetDouble("member_strong_vol_ratio", 1.5);
            _memberStrongTradingVal = config.GetDouble("member_strong_trading_val", 2_000_000_000);
            _memberVwapPctChgThreshold = config.GetDouble("member_vwap_pct_chg_threshold", 0.03);
            _groupVolExemptThreshold = config.GetDouble("group_vol_ratio_exempt_threshold", 30_000_000_000);
            _excludePrevLimitUp = config.GetBool("exclude_prev_limit_up_from_rank", false);
            _excludeDisposition = config.GetBool("exclude_disposition_from_rank", false);

            _modeEEnabled = config.GetBool("mode_e_enabled", false);
            _modeELargeGroupThreshold = config.GetInt("mode_e_large_group_threshold", 5);
            _modeELargeGroupSelect = config.GetInt("mode_e_large_group_select", 3);
            _modeESmallGroupSelect = config.GetInt("mode_e_small_group_select", 2);
            _modeELimitUpCascade = config.GetBool("mode_e_limit_up_cascade", true);
            _modeEMaxLimitUpSkip = config.GetInt("mode_e_max_limit_up_skip", 5);

            _bypassGroupScreening = config.GetBool("bypass_group_screening", false);
            _memberRankField = config.GetString("member_rank_field", "vwap");
        }

        public MatchInfo? OnTick(string stockId, Dictionary<string, IndexData> allStocks)
        {
            // Bypass mode: skip group ranking, allow all stocks in _groups
            if (_bypassGroupScreening)
            {
                // Check if this stock is in any group
                foreach (var gd in _groups.Values)
                {
                    if (gd.MemberStockIds.Contains(stockId))
                    {
                        return new MatchInfo
                        {
                            GroupName = gd.GroupName,
                            GroupRank = 1,
                            MemberRank = 1,
                            RawMemberRank = 1,
                            M1Symbol = stockId,
                            GroupMembers = $"M1:{stockId}"
                        };
                    }
                }
                return null;
            }

            UpdateGroupStates(allStocks);

            MatchInfo? bestMatch = null;

            foreach (var gs in _groupStates.Values)
            {
                if (!gs.IsValid || gs.Rank <= 0 || gs.Rank > _groupValidTopN)
                    continue;

                int maxSelect;
                List<MemberState> selectedMembers;

                if (_modeEEnabled)
                {
                    selectedMembers = GetSelectedMembersModeE(gs);
                    maxSelect = selectedMembers.Count;
                }
                else
                {
                    maxSelect = gs.Rank <= _topGroupRankThreshold
                        ? _topGroupMaxSelect
                        : _normalGroupMaxSelect;
                    selectedMembers = GetSelectedMembers(gs, maxSelect);
                }

                var member = selectedMembers.FirstOrDefault(m => m.StockId == stockId);
                if (member == null) continue;

                if (!_modeEEnabled)
                {
                    if (member.VwapChangePct < _entryMinVwapPctChg) continue;
                    if (_requireRawM1 && member.RawRank != 1) continue;
                }

                if (bestMatch == null || gs.Rank < bestMatch.GroupRank)
                {
                    string m1Symbol = gs.Members
                        .Where(m => m.FilteredRank == 1)
                        .Select(m => m.StockId)
                        .FirstOrDefault() ?? "";

                    // Build selected members string: "M1:2368|M2:3715|M3:5469"
                    string groupMembers = string.Join("|",
                        selectedMembers.OrderBy(m => m.FilteredRank)
                            .Select(m => $"M{m.FilteredRank}:{m.StockId}"));

                    bestMatch = new MatchInfo
                    {
                        GroupName = gs.GroupName,
                        GroupRank = gs.Rank,
                        TotalMemberRank = member.TotalRank,
                        MemberRank = member.FilteredRank,
                        RawMemberRank = member.RawRank,
                        M1Symbol = m1Symbol,
                        GroupMembers = groupMembers
                    };
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Mode E selection: dynamic max_select based on member count,
        /// skip limit-up stocks and cascade to next, cap at max_limit_up_skip.
        /// Disposition stocks excluded from candidates.
        /// </summary>
        private List<MemberState> GetSelectedMembersModeE(GroupState gs)
        {
            int validMemberCount = gs.Members.Count(m => m.FilteredRank > 0);
            int targetSelect = validMemberCount >= _modeELargeGroupThreshold
                ? _modeELargeGroupSelect
                : _modeESmallGroupSelect;

            var candidates = gs.Members
                .Where(m => m.FilteredRank > 0 && !m.IsDisposition)
                .OrderBy(m => m.FilteredRank)
                .ToList();

            var selected = new List<MemberState>();
            int limitUpSkipCount = 0;

            foreach (var m in candidates)
            {
                if (selected.Count >= targetSelect)
                    break;

                if (m.IsLimitUpLocked)
                {
                    limitUpSkipCount++;
                    if (limitUpSkipCount > _modeEMaxLimitUpSkip)
                        break; // Stop selecting entirely
                    continue; // Cascade to next
                }

                selected.Add(m);
            }

            return selected;
        }

        private void UpdateGroupStates(Dictionary<string, IndexData> allStocks)
        {
            var validGroups = new List<GroupState>();

            foreach (var gd in _groups.Values)
            {
                var gs = GetOrCreateGroupState(gd.GroupName);
                gs.Members.Clear();

                double totalMonthVal = 0;
                double sumPctChg = 0;
                double sumWeight = 0;
                double todayCumVal = 0;
                double monthlyAvgTotal = 0;
                int validMemberCount = 0;

                foreach (var sid in gd.MemberStockIds)
                {
                    if (!allStocks.TryGetValue(sid, out var idx)) continue;
                    if (idx.MonthlyAvgTradingValue < _memberMinMonthVal) continue;

                    totalMonthVal += idx.MonthlyAvgTradingValue;
                    todayCumVal += idx.TodayCumulativeValue;
                    monthlyAvgTotal += idx.MonthlyAvgTradingValue;

                    var ms = new MemberState
                    {
                        StockId = sid,
                        VwapChangePct = idx.VwapChangePct,
                        Vwap5mChangePct = idx.Vwap5mChangePct,
                        PriceChangePct = idx.PriceChangePct,
                        MonthlyAvgTradingValue = idx.MonthlyAvgTradingValue,
                        TodayCumulativeValue = idx.TodayCumulativeValue,
                        IsLimitUpLocked = idx.IsLimitUpLocked,
                        IsDisposition = idx.SecurityType == "RR",
                        IsPrevDayLimitUp = idx.PrevDayLimitUp,
                        FirstLimitUpTime = idx.FirstLimitUpTime
                    };

                    if (_cond1Enabled)
                    {
                        bool volRatioOk = idx.MonthlyAvgTradingValue > 0 &&
                            idx.TodayCumulativeValue / idx.MonthlyAvgTradingValue >= _memberStrongVolRatio;
                        bool bigValOk = idx.MonthlyAvgTradingValue > _memberStrongTradingVal;
                        bool exempt = totalMonthVal > _groupVolExemptThreshold;
                        ms.PassesCond1 = volRatioOk || bigValOk || exempt;
                    }
                    if (_cond2Enabled)
                        ms.PassesCond2 = idx.PriceChangePct > 0.02 && idx.VwapChangePct > 0.01;
                    if (_cond4Enabled)
                        ms.PassesCond4 = idx.VwapChangePct > _memberVwapPctChgThreshold;

                    gs.Members.Add(ms);
                    validMemberCount++;

                    double memberRankVal = _memberRankField == "vwap_5m"
                        ? idx.Vwap5mChangePct : idx.VwapChangePct;

                    if (_isWeightedAvg)
                    {
                        sumPctChg += memberRankVal * idx.MonthlyAvgTradingValue;
                        sumWeight += idx.MonthlyAvgTradingValue;
                    }
                    else
                    {
                        sumPctChg += memberRankVal;
                        sumWeight += 1;
                    }
                }

                gs.TotalMonthlyAvgTradingValue = totalMonthVal;
                gs.AveragePctChange = sumWeight > 0 ? sumPctChg / sumWeight : 0;
                gs.TodayTradingValueRatio = monthlyAvgTotal > 0 ? todayCumVal / monthlyAvgTotal : 0;

                gs.IsValid = totalMonthVal >= _groupMinMonthVal
                    && gs.AveragePctChange > _groupMinAvgPctChg
                    && gs.TodayTradingValueRatio >= _groupMinValRatio
                    && validMemberCount > 0;

                if (gs.IsValid)
                {
                    RankMembers(gs);
                    validGroups.Add(gs);
                }
                else
                {
                    gs.Rank = 0;
                }
            }

            validGroups.Sort((a, b) => b.AveragePctChange.CompareTo(a.AveragePctChange));
            for (int i = 0; i < validGroups.Count; i++)
                validGroups[i].Rank = i + 1;
        }

        private double GetRankValue(MemberState m)
        {
            return _memberRankField == "vwap_5m" ? m.Vwap5mChangePct : m.VwapChangePct;
        }

        /// <summary>
        /// Compare two members for ranking: descending by rank value,
        /// tiebreaker by FirstLimitUpTime ascending (earlier = stronger, null = last).
        /// </summary>
        private int CompareMembersForRank(MemberState a, MemberState b)
        {
            int cmp = GetRankValue(b).CompareTo(GetRankValue(a)); // descending
            if (cmp != 0) return cmp;

            // Tiebreaker: earlier limit-up time is stronger (ascending)
            bool aHas = a.FirstLimitUpTime.HasValue;
            bool bHas = b.FirstLimitUpTime.HasValue;
            if (aHas && bHas) return a.FirstLimitUpTime!.Value.CompareTo(b.FirstLimitUpTime!.Value);
            if (aHas) return -1; // a has limit-up time, b doesn't → a ranks higher
            if (bHas) return 1;
            return 0;
        }

        private void RankMembers(GroupState gs)
        {
            // 1) TotalRank: all members with min monthly val (including limit-up locked)
            var allCandidates = gs.Members
                .Where(m => m.MonthlyAvgTradingValue >= _memberMinMonthVal)
                .ToList();
            allCandidates.Sort(CompareMembersForRank);
            for (int i = 0; i < allCandidates.Count; i++)
                allCandidates[i].TotalRank = i + 1;

            // 2) RawRank: exclude limit-up locked + price change >= 8.5%
            var rawCandidates = allCandidates
                .Where(m => !m.IsLimitUpLocked && m.PriceChangePct < 0.085)
                .ToList();
            // Already sorted by CompareMembersForRank order
            for (int i = 0; i < rawCandidates.Count; i++)
                rawCandidates[i].RawRank = i + 1;

            // 3) FilteredRank: further exclude by conditions
            var filteredCandidates = rawCandidates
                .Where(m => m.PassesCond1 && m.PassesCond2 && m.PassesCond4
                    && (!_excludeDisposition || !m.IsDisposition)
                    && (!_excludePrevLimitUp || !m.IsPrevDayLimitUp))
                .ToList();
            for (int i = 0; i < filteredCandidates.Count; i++)
                filteredCandidates[i].FilteredRank = i + 1;

            // Clear ranks for non-participating members
            foreach (var m in gs.Members)
            {
                if (!allCandidates.Contains(m))
                    m.TotalRank = 0;
                if (!filteredCandidates.Contains(m))
                    m.FilteredRank = 0;
                if (!rawCandidates.Contains(m))
                    m.RawRank = 0;
            }
        }

        private List<MemberState> GetSelectedMembers(GroupState gs, int maxSelect)
        {
            return gs.Members
                .Where(m => m.FilteredRank > 0 && m.FilteredRank <= maxSelect)
                .ToList();
        }

        private GroupState GetOrCreateGroupState(string groupName)
        {
            if (!_groupStates.TryGetValue(groupName, out var gs))
            {
                gs = new GroupState { GroupName = groupName };
                _groupStates[groupName] = gs;
            }
            return gs;
        }

        public Dictionary<string, GroupState> GetGroupStates() => _groupStates;

        /// <summary>
        /// Get the current group rank for a stock by its group name.
        /// Returns 0 if group is no longer valid/ranked.
        /// </summary>
        public int GetCurrentGroupRank(string groupName)
        {
            if (_groupStates.TryGetValue(groupName, out var gs) && gs.IsValid)
                return gs.Rank;
            return 0;
        }
    }
}
