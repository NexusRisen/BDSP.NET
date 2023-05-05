﻿namespace SysBot.Pokemon
{
    /// <summary>
    /// Type of routine the Bot carries out.
    /// </summary>
    public enum PokeRoutineType
    {
        /// <summary> Sits idle waiting to be re-tasked. </summary>
        Idle = 0,

        // Add your own custom bots here so they don't clash for future main-branch bot releases.
        BDSPFlexTrade = 10000,
        BDSPSpecialRequest = 10001,
        LinkTrade = 10002,
        BDSPClone = 10003,
        BDSPDump = 10004
    }

    public static class PokeRoutineTypeExtensions
    {
        public static bool IsTradeBot(this PokeRoutineType type) => type is >=PokeRoutineType.BDSPFlexTrade and <=PokeRoutineType.BDSPDump;
    }
}