using System;
using PKHeX.Core;

namespace SysBot.Pokemon
{
    public sealed class BotFactory8BS : BotFactory<PB8>
    {
        public override PokeRoutineExecutorBase CreateBot(PokeTradeHub<PB8> Hub, PokeBotState cfg) => cfg.NextRoutineType switch
        {
            PokeRoutineType.FlexTrade or PokeRoutineType.Idle
            or PokeRoutineType.Clone
            or PokeRoutineType.LinkTrade
            or PokeRoutineType.SpecialRequest
            => new PokeTradeBotBS(Hub, cfg),

            _ => throw new ArgumentException(nameof(cfg.NextRoutineType)),
        };
    }
}
