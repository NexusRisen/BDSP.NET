namespace SysBot.Pokemon
{
    /// <summary>
    /// Type of routine the Bot carries out.
    /// </summary>
    public enum PokeRoutineType
    {
        /// <summary> Sits idle waiting to be re-tasked. </summary>
        Idle = 0,

        // Add your own custom bots here so they don't clash for future main-branch bot releases.
        FlexTrade = 10000,
        SpecialRequest = 10001,
        LinkTrade = 10002,
        Clone = 10003,
        Dump = 10004
    }

    public static class PokeRoutineTypeExtensions
    {
        public static bool IsTradeBot(this PokeRoutineType type) => type is >=PokeRoutineType.FlexTrade and <=PokeRoutineType.Dump;
    }
}