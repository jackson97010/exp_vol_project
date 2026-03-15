using System;

namespace StrongestVwap.Core.Models
{
    public class GlobalState
    {
        public bool MarketEnabled { get; set; } = true;
        public string DisableReason { get; set; } = "";
    }

    public class SignalAState
    {
        public bool NearVwap { get; set; }
        public double LowSinceNear { get; set; } = double.MaxValue;
        public bool Triggered { get; set; }
        public bool Forbidden { get; set; }

        public void Reset()
        {
            NearVwap = false;
            LowSinceNear = double.MaxValue;
        }
    }

    public class MatchInfo
    {
        public string GroupName { get; set; } = "";
        public int GroupRank { get; set; }
        public int TotalMemberRank { get; set; }
        public int MemberRank { get; set; }
        public int RawMemberRank { get; set; }
        public string M1Symbol { get; set; } = "";
        /// <summary>
        /// Selected members at entry time, e.g. "M1:2368|M2:3715|M3:5469"
        /// </summary>
        public string GroupMembers { get; set; } = "";
    }
}
