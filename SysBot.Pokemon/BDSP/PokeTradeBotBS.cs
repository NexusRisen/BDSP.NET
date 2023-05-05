﻿using PKHeX.Core;
using PKHeX.Core.Searching;
using SysBot.Base;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.BasePokeDataOffsetsBS;
using static SysBot.Pokemon.SpecialRequests;

namespace SysBot.Pokemon
{
    public class PokeTradeBotBS : PokeRoutineExecutor8BS, ICountBot
    {
        private readonly PokeTradeHub<PB8> Hub;
        private readonly TradeSettings TradeSettings;

        public ICountSettings Counts => TradeSettings;

        /// <summary>
        /// Folder to dump received trade data to.
        /// </summary>
        /// <remarks>If null, will skip dumping.</remarks>
        private readonly IDumper DumpSetting;

        /// <summary>
        /// Synchronized start for multiple bots.
        /// </summary>
        public bool ShouldWaitAtBarrier { get; private set; }

        /// <summary>
        /// Tracks failed synchronized starts to attempt to re-sync.
        /// </summary>
        public int FailedBarrier { get; private set; }


        public PokeTradeBotBS(PokeTradeHub<PB8> hub, PokeBotState cfg) : base(cfg)
        {
            Hub = hub;
            TradeSettings = hub.Config.Trade;
            DumpSetting = hub.Config.Folder;
        }

        public override async Task MainLoop(CancellationToken token)
        {
            try
            {
                await InitializeHardware(Hub.Config.Trade, token).ConfigureAwait(false);

                Log("Identifying trainer data of the host console.");
                var sav = await IdentifyTrainer(token).ConfigureAwait(false);

                await RestartGameIfCantLeaveUnionRoom(token).ConfigureAwait(false);

                Log($"Starting main {nameof(PokeTradeBotBS)} loop.");
                await InnerLoop(sav, token).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception e)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                Log(e.Message);
            }

            Log($"Ending {nameof(PokeTradeBotBS)} loop.");
            await HardStop().ConfigureAwait(false);
        }

        public override async Task HardStop()
        {
            UpdateBarrier(false);
            await CleanExit(TradeSettings, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task InnerLoop(SAV8BS sav, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Config.IterateNextRoutine();
                var task = Config.CurrentRoutineType switch
                {
                    PokeRoutineType.Idle => DoNothing(token),
                    _ => DoTrades(sav, token),
                };
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (SocketException e)
                {
                    Log(e.Message);
                    Connection.Reset();
                }
            }
        }

        private async Task DoNothing(CancellationToken token)
        {
            int waitCounter = 0;
            while (!token.IsCancellationRequested && Config.NextRoutineType == PokeRoutineType.Idle)
            {
                if (waitCounter == 0)
                    Log("No task assigned. Waiting for new task assignment.");
                waitCounter++;
                if (waitCounter % 10 == 0 && Hub.Config.AntiIdle)
                    await Click(B, 1_000, token).ConfigureAwait(false);
                else
                    await Task.Delay(1_000, token).ConfigureAwait(false);
            }
        }

        private async Task DoTrades(SAV8BS sav, CancellationToken token)
        {
            var type = Config.CurrentRoutineType;
            int waitCounter = 0;
            while (!token.IsCancellationRequested && Config.NextRoutineType == type)
            {
                await AttemptClearTradePartnerPointer(token).ConfigureAwait(false);
                var (detail, priority) = GetTradeData(type);
                if (detail is null)
                {
                    await WaitForQueueStep(waitCounter++, token).ConfigureAwait(false);
                    continue;
                }
                waitCounter = 0;

                detail.IsProcessing = true;
                await RestartGameIfCantLeaveUnionRoom(token).ConfigureAwait(false);
                string tradetype = $" ({detail.Type})";
                Log($"Starting next {type}{tradetype} Bot Trade. Getting data...");
                await Task.Delay(500, token).ConfigureAwait(false); // hack number 301923 for getting web to work
                Hub.Config.Stream.StartTrade(this, detail, Hub);
                Hub.Queues.StartTrade(this, detail);

                await PerformTrade(sav, detail, type, priority, token).ConfigureAwait(false);
                await RestartGameIfCantLeaveUnionRoom(token).ConfigureAwait(false);
            }
        }

        private async Task WaitForQueueStep(int waitCounter, CancellationToken token)
        {
            if (waitCounter == 0)
            {
                // Updates the assets.
                Hub.Config.Stream.IdleAssets(this);
                Log("Nothing to check, waiting for new users...");
            }

            const int interval = 10;
            if (waitCounter % interval == interval - 1 && Hub.Config.AntiIdle)
                await Click(B, 1_000, token).ConfigureAwait(false);
            else
                await Task.Delay(1_000, token).ConfigureAwait(false);
        }

        protected virtual (PokeTradeDetail<PB8>? detail, uint priority) GetTradeData(PokeRoutineType type)
        {
            if (Hub.Queues.TryDequeue(out var detail, out var priority))
                return (detail, priority);
            if (Hub.Queues.TryDequeueLedy(out detail))
                return (detail, PokeTradePriorities.TierFree);
            return (null, PokeTradePriorities.TierFree);
        }

        private async Task PerformTrade(SAV8BS sav, PokeTradeDetail<PB8> detail, PokeRoutineType type, uint priority, CancellationToken token)
        {
            PokeTradeResult result;
            try
            {
                result = await PerformLinkCodeTrade(sav, detail, token).ConfigureAwait(false);
                if (result == PokeTradeResult.Success)
                    return;
            }
            catch (SocketException socket)
            {
                Log(socket.Message);
                result = PokeTradeResult.ExceptionConnection;
                HandleAbortedTrade(detail, type, priority, result);
                throw; // let this interrupt the trade loop. re-entering the trade loop will recheck the connection.
            }
            catch (Exception e)
            {
                Log(e.Message);
                result = PokeTradeResult.ExceptionInternal;
            }

            HandleAbortedTrade(detail, type, priority, result);
        }

        private void HandleAbortedTrade(PokeTradeDetail<PB8> detail, PokeRoutineType type, uint priority, PokeTradeResult result)
        {
            detail.IsProcessing = false;
            if (result.ShouldAttemptRetry() && detail.Type != PokeTradeType.Random && !detail.IsRetry)
            {
                detail.IsRetry = true;
                Hub.Queues.Enqueue(type, detail, Math.Min(priority, PokeTradePriorities.Tier2));
                detail.SendNotification(this, "Oops! Something happened. I'll requeue you for another attempt.");
            }
            else
            {
                detail.SendNotification(this, $"Oops! Something happened. Canceling the trade: {result}.");
                detail.TradeCanceled(this, result);
            }
        }

        private void SetText(SAV8BS sav, string text)
        {
            System.IO.File.WriteAllText($"code{sav.OT}-{sav.DisplayTID}.txt", text);
        }

        private async Task<PokeTradeResult> PerformLinkCodeTrade(SAV8BS sav, PokeTradeDetail<PB8> poke, CancellationToken token)
        {
            //var pkm = AutoALMPokeGen<PB8>.Generate(214, "hj", (int)GameVersion.BD, (int)LanguageID.ChineseS, 2166, 344121, 1, "dave");
            //if (pkm != null)
            //    Log(pkm.Species.ToString());
            // Update Barrier Settings
            UpdateBarrier(poke.IsSynchronized);
            poke.TradeInitialize(this);
            await RestartGameIfCantLeaveUnionRoom(token).ConfigureAwait(false);
            Hub.Config.Stream.EndEnterCode(this);

            if (await CheckIfSoftBanned(token).ConfigureAwait(false))
                await Unban(token).ConfigureAwait(false);

            if (poke.FirstData.Species != 0)
                await SetBoxPokemon(poke.FirstData, token, sav).ConfigureAwait(false);

            if (poke.Type == PokeTradeType.Random)
                SetText(sav, $"Trade code: {poke.Code:0000 0000}\r\nSending: {(Species)poke.FirstData.Species}");
            else
                SetText(sav, "Running a\nSpecific trade.");

            // Go into the union room and start telling everyone we're looking for a trade partner
            if (poke.Type != PokeTradeType.Random)
                Hub.Config.Stream.StartEnterCode(this);
            if (!await EnterUnionRoomWithCode(poke, poke.Code, token).ConfigureAwait(false))
            {
                await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                return PokeTradeResult.RecoverOpenBox;
            }

            Hub.Config.Stream.EndEnterCode(this);
            poke.TradeSearching(this);

            // Keep pressing A until we're in boxes (in a trade) or until the timer runs out
            int inBoxChecks = Hub.Config.Trade.TradeWaitTime * 2;
            float currentRotation = 0;
            while (!await IsTrue(Offsets.UnionWorkIsTalkingPointer, token).ConfigureAwait(false) && inBoxChecks > 0)
            {
                for (int i = 0; i < 2; ++i)
                {
                    await Click(A, 0_200, token).ConfigureAwait(false);
                    currentRotation += 45f;
                    if (currentRotation > 360f)
                        currentRotation -= 360f;
                    await SetRotationEuler(new Vector3(0, currentRotation, 0), token).ConfigureAwait(false);
                }

                inBoxChecks--;

                if (await CanPlayerMove(token).ConfigureAwait(false))
                    return PokeTradeResult.TrainerTooSlow;
            }

            // Keep pressing A until TargetTranerParam is loaded (when we hit the box).
            while (!await IsPartnerParamLoaded(token).ConfigureAwait(false) && inBoxChecks > 0)
            {
                for (int i = 0; i < 2; ++i)
                    await Click(A, 0_450, token).ConfigureAwait(false);

                // Can be false if they talked and quit.
                if (!await IsTrue(Offsets.UnionWorkIsTalkingPointer, token).ConfigureAwait(false))
                    break;
                if (--inBoxChecks <= 0)
                    return PokeTradeResult.TrainerTooSlow;
            }

            // Still going through dialog and box opening.
            await Task.Delay(2_000, token).ConfigureAwait(false);

            // Can happen if they quit out of talking to us.
            if (!await IsPartnerParamLoaded(token).ConfigureAwait(false))
                return PokeTradeResult.TrainerTooSlow;

            var tradePartner = await GetTradePartnerInfo(token).ConfigureAwait(false);
            var tradePartnerNID = await GetTradePartnerNID(token).ConfigureAwait(false);

            bool IsSafe = true;// poke.Trainer.ID == 0 || tradePartner.IDHash == 0 || NewAntiAbuse.Instance.LogUser(tradePartner.IDHash, tradePartnerNID, poke.Trainer.ID.ToString(), poke.Trainer.TrainerName, Hub.Config.Trade.MultiAbuseEchoMention);
            if (!IsSafe)
            {
                Log($"Found known abuser: {tradePartner.TrainerName}-{tradePartner.SID}-{tradePartner.TID} ({poke.Trainer.TrainerName}) (NID: {tradePartnerNID})");
                poke.SendNotification(this, $"Your savedata is associated with a known abuser. Consider not being an abuser, and you will no longer see this message.");
                await Task.Delay(1_000, token).ConfigureAwait(false);
                return PokeTradeResult.TrainerTooSlow;
            }

            Log($"Found trading partner: {tradePartner.TrainerName}-{tradePartner.SID}-{tradePartner.TID} ({poke.Trainer.TrainerName}) (NID: {tradePartnerNID})");
            poke.SendNotification(this, $"Found Trading Partner: {tradePartner.TrainerName} SID: {tradePartner.SID:0000} TID: {tradePartner.TID:000000}. Waiting for a Pokémon...");

            if (poke.Type == PokeTradeType.Dump)
                return await ProcessDumpTradeAsync(poke, token).ConfigureAwait(false);

            int tradeCount = 0;
            foreach (var send in poke.TradeData)
            {
                // Looping check
                int checks = 120;
                while (poke.TradeData.Length > 1 && BitConverter.ToUInt64(await SwitchConnection.PointerPeek(8, Offsets.LinkTradePartnerPokemonPointer, token).ConfigureAwait(false), 0) == 0)
                {
                    await Click(B, 0_550, token).ConfigureAwait(false);
                    if (checks-- < 1)
                        return PokeTradeResult.TrainerTooSlow;
                }

                var toSend = send;
                if (toSend.Species != 0)
                    await SetBoxPokemon(toSend, token, sav).ConfigureAwait(false);

                // Confirm Box 1 Slot 1
                if (poke.Type == PokeTradeType.Specific)
                {
                    for (int i = 0; i < 5; i++)
                        await Click(A, 0_500, token).ConfigureAwait(false);
                }
                else if (poke.Type is PokeTradeType.Clone or PokeTradeType.Seed or PokeTradeType.Random)
                {
                    for (int i = 0; i < 2; ++i)
                        await Click(B, 0_200, token).ConfigureAwait(false);
                }

                var offered = await ReadUntilPresentPointer(Offsets.LinkTradePartnerPokemonPointer, 25_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);
                Log("Pointer is present with a pokemon.");

                var offset = await SwitchConnection.PointerAll(Offsets.LinkTradePartnerPokemonPointer, token).ConfigureAwait(false);
                var oldEC = await SwitchConnection.ReadBytesAbsoluteAsync(offset, 4, token).ConfigureAwait(false);
                if (offered is null)
                    return PokeTradeResult.TrainerTooSlow;

                if (poke.Type == PokeTradeType.Random)
                {
                    Log($"Offered nickname is {offered.Nickname}");
                    if (offered.IsNicknamed)
                    {
                        if (offered.Nickname.Length == 6 && uint.TryParse(offered.Nickname[..3], out _))
                        {
                            var generatedMon = AutoALMPokeGen<PB8>.Generate(int.Parse(offered.Nickname[..3]), offered.Nickname[3..5], offered.Version, offered.Language, offered.TrainerSID7, offered.TrainerID7, offered.OT_Gender, offered.OT_Name);
                            if (generatedMon != null)
                            {
                                await SetBoxPokemon(generatedMon, token, sav).ConfigureAwait(false);
                                SetText(sav, $"Trade code: {poke.Code:0000 0000}\r\n!request for {tradePartner.TrainerName}");
                                poke.Code = Hub.Config.Trade.GetRandomTradeCode();
                                Hub.Queues.Enqueue(PokeRoutineType.LinkTrade, poke, 3);
                            }
                        }
                    }
                }

                SpecialTradeType itemReq = SpecialTradeType.None;
                if (poke.Type == PokeTradeType.Seed)
                    itemReq = CheckItemRequest(ref offered, this, poke, tradePartner, sav);
                if (itemReq == SpecialTradeType.FailReturn)
                    return PokeTradeResult.IllegalTrade;

                if (poke.Type == PokeTradeType.Seed && itemReq == SpecialTradeType.None)
                {
                    // Immediately exit, we aren't trading anything.
                    poke.SendNotification(this, "No held item or valid request, the BDSP bots are special request only!");
                    return await EndQuickTradeAsync(poke, offered, token).ConfigureAwait(false);
                }

                PokeTradeResult update;
                var trainer = new PartnerDataHolder(tradePartnerNID, tradePartner.TrainerName, tradePartner.TID.ToString());
                (toSend, update) = await GetEntityToSend(sav, poke, offered, oldEC, toSend, trainer, poke.Type == PokeTradeType.Seed ? itemReq : null, token).ConfigureAwait(false);
                if (update != PokeTradeResult.Success)
                {
                    if (itemReq != SpecialTradeType.None)
                    {
                        poke.SendNotification(this, "Your request isn't legal. Please try a different Pokémon or request.");
                        if (!string.IsNullOrWhiteSpace(Hub.Config.Web.URIEndpoint))
                            AddToPlayerLimit(tradePartner.IDHash.ToString(), -1);
                    }

                    return update;
                }

                if (itemReq == SpecialTradeType.WonderCard)
                    poke.SendNotification(this, "Distribution success!");
                else if (itemReq != SpecialTradeType.None && itemReq != SpecialTradeType.Shinify)
                    poke.SendNotification(this, "Special request successful!");
                else if (itemReq == SpecialTradeType.Shinify)
                    poke.SendNotification(this, "Shinify success! Thanks for being part of the community!");
                if (tradeCount++ > 3)
                    break;

                var tradeResult = await ConfirmAndStartTrading(poke, token).ConfigureAwait(false);
                if (tradeResult != PokeTradeResult.Success)
                    return tradeResult;
                else
                    await AttemptClearTradePartnerPointer(token).ConfigureAwait(false);

                if (token.IsCancellationRequested)
                    return PokeTradeResult.RoutineCancel;

                // Trade was Successful!
                var receivedNext = await ReadBoxPokemon(1, 1, token).ConfigureAwait(false);
                // Pokémon in b1s1 is same as the one they were supposed to receive (was never sent).
                if (SearchUtil.HashByDetails(receivedNext) == SearchUtil.HashByDetails(send))
                {
                    Log("User did not complete the trade.");
                    return PokeTradeResult.TrainerTooSlow;
                }

                poke.SendNotification(this, receivedNext, $"You sent me {(Species)receivedNext.Species} for {(Species)send.Species}!");
            }


            // Trade was Successful!
            var received = await ReadBoxPokemon(1, 1, token).ConfigureAwait(false);
            // Pokémon in b1s1 is same as the one they were supposed to receive (was never sent).
            if (SearchUtil.HashByDetails(received) == SearchUtil.HashByDetails(poke.FirstData))
            {
                Log("User did not complete the trade.");
                return PokeTradeResult.TrainerTooSlow;
            }

            // As long as we got rid of our inject in b1s1, assume the trade went through.
            Log("User completed the trade.");
            poke.TradeFinished(this, received);

            return PokeTradeResult.Success;
        }

        private async Task<PokeTradeResult> ConfirmAndStartTrading(PokeTradeDetail<PB8> detail, CancellationToken token)
        {
            var oldPKData = await SwitchConnection.PointerPeek(344, Offsets.BoxStartPokemonPointer, token).ConfigureAwait(false);

            await Click(A, 3_000, token).ConfigureAwait(false);
            for (int i = 0; i < 10; i++)
            {
                if (await IsUserBeingShifty(detail, token).ConfigureAwait(false))
                    return PokeTradeResult.SuspiciousActivity;
                await Click(A, 1_500, token).ConfigureAwait(false);
            }

            await Click(A, 3_000, token).ConfigureAwait(false);
            var tradeCounter = 0;
            while (await IsTrue(Offsets.UnionWorkIsTalkingPointer, token).ConfigureAwait(false))
            {
                await Click(A, 1_000, token).ConfigureAwait(false);
                tradeCounter++;

                if (await SwitchConnection.PointerPeek(344, Offsets.BoxStartPokemonPointer, token).ConfigureAwait(false) != oldPKData)
                {
                    await Task.Delay(14_000, token).ConfigureAwait(false);
                    return PokeTradeResult.Success;
                }

                // We've somehow failed out of the Union Room -- can happen with connection error.
                if (!await IsTrue(Offsets.UnionWorkIsGamingPointer, token).ConfigureAwait(false))
                    return PokeTradeResult.TrainerTooSlow;
                if (tradeCounter >= Hub.Config.Trade.TradeAnimationMaxDelaySeconds)
                    break;
            }

            // If we don't detect a B1S1 change, the trade didn't go through in that time.
            return PokeTradeResult.TrainerTooSlow;
        }

        private async Task<bool> EnterUnionRoomWithCode(PokeTradeDetail<PB8> poke, int tradeCode, CancellationToken token)
        {
            var currentScene = await GetUnitySceneStream(token).ConfigureAwait(false);
            if (currentScene == UnitySceneStream.Unknown)
                return false;

            Log($"Currently in scene: {currentScene}. Begin counter talk sequence.");

            if (currentScene is UnitySceneStream.PokeCentreDownstairsGlobal or UnitySceneStream.PokeCentreUpstairsLocal)
            {
                // warp to clerk
                await SetPosition(new Vector3(-7f, 0, 6f), token).ConfigureAwait(false);
                await SetRotationEuler(new Vector3(0, 180f, 0), token).ConfigureAwait(false);
            }
            else
            {
                // open ycomm
                await Click(Y, 0_800, token).ConfigureAwait(false);
                await Click(DRIGHT, 0_300, token).ConfigureAwait(false);
            }

            int doubleACount = currentScene == UnitySceneStream.PokeCentreUpstairsLocal ? 3 : 2; // local requires one extra press
            if (GameLang is LanguageID.French)
                doubleACount--;

            Log($"Begin dialogue with clerk...");
            for (int i = 0; i < doubleACount; i++)
            {
                await Click(A, 0_300, token).ConfigureAwait(false);
                await Click(A, 0_700, token).ConfigureAwait(false);
            }

            await Click(A, 0_900, token).ConfigureAwait(false); // Would you like to enter? Screen

            // Link code selection index
            await Click(DDOWN, 0_350, token).ConfigureAwait(false);
            await Click(DDOWN, 0_300, token).ConfigureAwait(false);

            int numTries = 56;
            Log($"Awaiting keyboard open...");
            while (!await IsKeyboardOpen(token).ConfigureAwait(false))
            {
                await Click(ZR, 0_500, token).ConfigureAwait(false); // ZR=A but not for keyboard

                if (numTries-- < 0)
                    return false;
            }

            await Task.Delay(0_800, token).ConfigureAwait(false);

            Log($"Entering Link Trade code: {tradeCode:0000 0000}...");
            poke.SendNotification(this, $"Entering Link Trade Code: {tradeCode:0000 0000}...");
            await EnterLinkCode(tradeCode, Hub.Config, token).ConfigureAwait(false);
            // Wait for Barrier to trigger all bots simultaneously.
            WaitAtBarrierIfApplicable(token);

            await Click(PLUS, 0_600, token).ConfigureAwait(false);
            await Click(PLUS, 0_100, token).ConfigureAwait(false); // in case eaten

            int tries = 100;
            while (!await IsTrue(Offsets.UnionWorkIsGamingPointer, token).ConfigureAwait(false))
            {
                await Click(A, 0_300, token).ConfigureAwait(false);

                if (await GetUnitySceneStream(token).ConfigureAwait(false) == UnitySceneStream.LocalUnionRoom)
                {
                    await Task.Delay(1_000, token).ConfigureAwait(false);
                    await SetPosition(GetUnionRoomCenter(currentScene), token).ConfigureAwait(false);
                }

                if (--tries < 1)
                    return false;
            }

            Log("Mashing Y until we can no longer move");
            while (await CanPlayerMove(token).ConfigureAwait(false))
                await Click(Y, 1_000, token).ConfigureAwait(false);
                
            await Click(A, 0_450,token).ConfigureAwait(false);
            await Click(DDOWN, 0_450, token).ConfigureAwait(false);
            await Click(DDOWN, 0_450, token).ConfigureAwait(false);
            await Click(A, 0_100, token).ConfigureAwait(false);

            return true; // we are now searching
        }

        protected virtual async Task<bool> IsUserBeingShifty(PokeTradeDetail<PB8> detail, CancellationToken token)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return false;
        }

        private async Task AttemptClearTradePartnerPointer(CancellationToken token)
        {
            (var valid, var offs) = await ValidatePointerAll(Offsets.LinkTradePartnerPokemonPointer, token).ConfigureAwait(false);
            if (valid)
            {
                if (BitConverter.ToUInt32(await SwitchConnection.ReadBytesAbsoluteAsync(offs, 4, token).ConfigureAwait(false), 0) == 0)
                    return;
                if (BitConverter.ToUInt16(await SwitchConnection.ReadBytesAbsoluteAsync(offs + 4, 2, token).ConfigureAwait(false), 0) != 0)
                    return;
                await SwitchConnection.WriteBytesAbsoluteAsync(new byte[0x148], offs, token).ConfigureAwait(false);
            }
        }

        private async Task RestartGameIfCantLeaveUnionRoom(CancellationToken token)
        {
            if (!await EnsureOutsideOfUnionRoom(token).ConfigureAwait(false))
            {
                await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
            }    
        }

        private async Task<bool> EnsureOutsideOfUnionRoom(CancellationToken token)
        {
            if (await GetUnitySceneStream(token).ConfigureAwait(false) is UnitySceneStream.Unknown)
            {
                // Possibly title screen
                await Click(A, 3_000, token).ConfigureAwait(false);
                await Click(A, 3_000, token).ConfigureAwait(false);
                int tries = 20;
                while (await GetUnitySceneStream(token).ConfigureAwait(false) is UnitySceneStream.Unknown)
                {
                    await Click(B, 0_300, token).ConfigureAwait(false);
                    await Click(B, 0_300, token).ConfigureAwait(false);

                    if (tries-- < 0)
                        return false;
                }
                if (!await IsPlayerInstantiated(token).ConfigureAwait(false))
                    await Task.Delay(7_000, token).ConfigureAwait(false);
            }

            if (!await IsTrue(Offsets.UnionWorkIsGamingPointer, token).ConfigureAwait(false))
                return true;

            if (!await ExitBoxToUnionRoom(token).ConfigureAwait(false))
                return false;
            if (!await ExitUnionRoomToOverworld(token).ConfigureAwait(false))
                return false;
            if (!await CanPlayerMove(token).ConfigureAwait(false))
            {
                int tries = 30;
                while (!await CanPlayerMove(token).ConfigureAwait(false))
                {
                    await Click(B, 0_300, token).ConfigureAwait(false);
                    await Click(B, 0_300, token).ConfigureAwait(false);

                    if (tries-- < 0)
                        return false;
                }
            }

            return true;
        }

        private async Task<bool> ExitBoxToUnionRoom(CancellationToken token)
        {
            if (await IsTrue(Offsets.UnionWorkIsTalkingPointer, token).ConfigureAwait(false))
            {
                Log("Exiting box...");
                int tries = 32;
                while (await IsTrue(Offsets.UnionWorkIsTalkingPointer, token).ConfigureAwait(false))
                {
                    await Click(B, 0_500, token).ConfigureAwait(false);
                    await Click(DUP, 0_200, token).ConfigureAwait(false);
                    await Click(A, 0_500, token).ConfigureAwait(false);
                    // Keeps regular quitting a little faster, only need this for trade evolutions + moves.
                    if (tries < 10)
                        await Click(B, 0_500, token).ConfigureAwait(false);
                    await Click(B, 0_500, token).ConfigureAwait(false);
                    tries--;
                    if (tries < 0)
                        return false;
                }
            }
            await Task.Delay(2_000, token).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> ExitUnionRoomToOverworld(CancellationToken token)
        {
            if (await IsTrue(Offsets.UnionWorkIsGamingPointer, token).ConfigureAwait(false))
            {
                Log("Exiting Union Room...");
                for (int i = 0; i < 3; ++i)
                    await Click(B, 0_200, token).ConfigureAwait(false);

                await Click(Y, 1_000, token).ConfigureAwait(false);
                await Click(DDOWN, 0_200, token).ConfigureAwait(false);
                for (int i = 0; i < 3; ++i)
                    await Click(A, 0_400, token).ConfigureAwait(false);

                int tries = 10;
                while (await IsTrue(Offsets.UnionWorkIsGamingPointer, token).ConfigureAwait(false))
                {
                    await Task.Delay(0_400, token).ConfigureAwait(false);
                    tries--;
                    if (tries < 0)
                        return false;
                }
                await Task.Delay(4_000, token).ConfigureAwait(false);
            }
            return true;
        }

        private async Task<bool> CanPlayerMove(CancellationToken token)
        {
            var movementState = BitConverter.ToUInt16(await SwitchConnection.PointerPeek(0x2, Offsets.PlayerMovementPointer, token).ConfigureAwait(false), 0);
            return movementState == 0x0101;
        }

        private async Task<PokeTradeResult> ProcessDumpTradeAsync(PokeTradeDetail<PB8> detail, CancellationToken token)
        {
            int ctr = 0;
            var time = TimeSpan.FromSeconds(Hub.Config.Trade.MaxDumpTradeTime);
            var start = DateTime.Now;
            var pkprev = new PB8();
            while (ctr < Hub.Config.Trade.MaxDumpsPerTrade && DateTime.Now - start < time)
            {
                var pk = await ReadUntilPresentPointer(Offsets.LinkTradePartnerPokemonPointer, 3_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);
                if (pk == null || pk.Species < 1 || !pk.ChecksumValid || SearchUtil.HashByDetails(pk) == SearchUtil.HashByDetails(pkprev))
                    continue;

                // Save the new Pokémon for comparison next round.
                pkprev = pk;

                // Send results from separate thread; the bot doesn't need to wait for things to be calculated.
                if (DumpSetting.Dump)
                {
                    var subfolder = detail.Type.ToString().ToLower();
                    DumpPokemon(DumpSetting.DumpFolder, subfolder, pk); // received
                }

                var la = new LegalityAnalysis(pk);
                var verbose = la.Report(true);
                Log($"Shown Pokémon is: {(la.Valid ? "Valid" : "Invalid")}.");

                detail.SendNotification(this, pk, verbose);
                ctr++;
            }

            Log($"Ended Dump loop after processing {ctr} Pokémon.");
            if (ctr == 0)
                return PokeTradeResult.TrainerTooSlow;

            TradeSettings.AddCompletedDumps();
            detail.Notifier.SendNotification(this, detail, $"Dumped {ctr} Pokémon.");
            detail.Notifier.TradeFinished(this, detail, new PB8()); // blank pb8
            return PokeTradeResult.Success;
        }

        private async Task<TradePartnerBS> GetTradePartnerInfo(CancellationToken token)
        {
            var id = await SwitchConnection.PointerPeek(4, Offsets.LinkTradePartnerIDPointer, token).ConfigureAwait(false);
            var name = await SwitchConnection.PointerPeek(TradePartnerBS.MaxByteLengthStringObject, Offsets.LinkTradePartnerNamePointer, token).ConfigureAwait(false);
            return new TradePartnerBS(id, name);
        }

        protected virtual async Task<(PB8 toSend, PokeTradeResult check)> GetEntityToSend(SAV8BS sav, PokeTradeDetail<PB8> poke, PB8 offered, byte[] oldEC, PB8 toSend, PartnerDataHolder partnerID, SpecialTradeType? stt, CancellationToken token)
        {
            return poke.Type switch
            {
                PokeTradeType.Random => await HandleRandomLedy(sav, poke, offered, toSend, partnerID, token).ConfigureAwait(false),
                PokeTradeType.Clone => await HandleClone(sav, poke, offered, oldEC, token).ConfigureAwait(false),
                PokeTradeType.Seed when stt is not SpecialTradeType.WonderCard => await HandleClone(sav, poke, offered, oldEC, token).ConfigureAwait(false),
                PokeTradeType.Seed when stt is SpecialTradeType.WonderCard => await JustInject(sav, offered, token).ConfigureAwait(false),
                _ => (toSend, PokeTradeResult.Success),
            };
        }

        private async Task<(PB8 toSend, PokeTradeResult check)> HandleClone(SAV8BS sav, PokeTradeDetail<PB8> poke, PB8 offered, byte[] oldEC, CancellationToken token)
        {
            if (Hub.Config.Discord.ReturnPKMs)
                poke.SendNotification(this, offered, "Here's what you showed me!");

            var la = new LegalityAnalysis(offered);
            if (!la.Valid)
            {
                Log($"Clone request (from {poke.Trainer.TrainerName}) has detected an invalid Pokémon: {(Species)offered.Species}.");
                if (DumpSetting.Dump)
                    DumpPokemon(DumpSetting.DumpFolder, "hacked", offered);

                var report = la.Report();
                Log(report);
                poke.SendNotification(this, "This Pokémon is not legal per PKHeX's legality checks. I am forbidden from cloning this. Exiting trade.");
                poke.SendNotification(this, report);

                return (offered, PokeTradeResult.IllegalTrade);
            }
            
            // Inject the shown Pokémon.
            var clone = (PB8)offered.Clone();
            if (Hub.Config.Legality.ResetHOMETracker)
                clone.Tracker = 0;

            poke.SendNotification(this, $"**Cloned your {(Species)clone.Species}!**\nNow press B to cancel your offer and trade me a Pokémon you don't want.");
            Log($"Cloned a {(Species)clone.Species}. Waiting for user to change their Pokémon...");

            // Separate this out from WaitForPokemonChanged since we compare to old EC from original read.
            var valid = false;
            var offset = 0ul;
            while (!valid)
            {
                await Task.Delay(0_500, token).ConfigureAwait(false);
                (valid, offset) = await ValidatePointerAll(Offsets.LinkTradePartnerPokemonPointer, token).ConfigureAwait(false);
            }
            var partnerFound = await ReadUntilChanged(offset, oldEC, 15_000, 0_200, false, true, token).ConfigureAwait(false);

            if (!partnerFound)
            {
                poke.SendNotification(this, "**HEY CHANGE IT NOW OR I AM LEAVING!!!**");
                // They get one more chance.
                partnerFound = await ReadUntilChanged(offset, oldEC, 15_000, 0_200, false, true, token).ConfigureAwait(false);
            }

            var pk2 = await ReadUntilPresent(offset, 3_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);
            if (!partnerFound || pk2 == null || SearchUtil.HashByDetails(pk2) == SearchUtil.HashByDetails(offered))
            {
                Log("Trade partner did not change their Pokémon.");
                return (offered, PokeTradeResult.TrainerTooSlow);
            }

            await SetBoxPokemon(clone, token, sav).ConfigureAwait(false);
            await Click(A, 0_800, token).ConfigureAwait(false);

            for (int i = 0; i < 5; i++)
                await Click(A, 0_500, token).ConfigureAwait(false);

            return (clone, PokeTradeResult.Success);
        }

        private async Task<(PB8 toSend, PokeTradeResult check)> HandleRandomLedy(SAV8BS sav, PokeTradeDetail<PB8> poke, PB8 offered, PB8 toSend, PartnerDataHolder partner, CancellationToken token)
        {
            // Allow the trade partner to do a Ledy swap.
            var config = Hub.Config.Distribution;
            var trade = Hub.Ledy.GetLedyTrade(offered, partner.TrainerOnlineID, config.LedySpecies);
            if (trade != null)
            {
                if (trade.Type == LedyResponseType.AbuseDetected)
                {
                    var msg = $"Found {partner.TrainerName} has been detected for abusing Ledy trades.";
                    EchoUtil.Echo(msg);

                    return (toSend, PokeTradeResult.SuspiciousActivity);
                }

                toSend = trade.Receive;
                poke.FirstData = toSend;

                poke.SendNotification(this, "Injecting the requested Pokémon.");
                await Click(A, 0_800, token).ConfigureAwait(false);
                await SetBoxPokemon(toSend, token, sav).ConfigureAwait(false);
                await Task.Delay(2_500, token).ConfigureAwait(false);
            }
            else if (config.LedyQuitIfNoMatch)
            {
                return (toSend, PokeTradeResult.TrainerRequestBad);
            }

            for (int i = 0; i < 5; i++)
            {
                await Click(A, 0_500, token).ConfigureAwait(false);
            }

            return (toSend, PokeTradeResult.Success);
        }

        private async Task<(PB8 toSend, PokeTradeResult check)> JustInject(SAV8BS sav, PB8 offered, CancellationToken token)
        {
            await Click(A, 0_800, token).ConfigureAwait(false);
            await SetBoxPokemon(offered, token, sav).ConfigureAwait(false);

            for (int i = 0; i < 5; i++)
                await Click(A, 0_500, token).ConfigureAwait(false);

            return (offered, PokeTradeResult.Success);
        }

        private async Task<PokeTradeResult> EndQuickTradeAsync(PokeTradeDetail<PB8> detail, PB8 pk, CancellationToken token)
        {
            await RestartGameIfCantLeaveUnionRoom(token).ConfigureAwait(false);

            detail.TradeFinished(this, pk);

            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                DumpPokemon(DumpSetting.DumpFolder, "quick", pk);

            return PokeTradeResult.Success;
        }

        private void WaitAtBarrierIfApplicable(CancellationToken token)
        {
            if (!ShouldWaitAtBarrier)
                return;
            var opt = Hub.Config.Distribution.SynchronizeBots;
            if (opt == BotSyncOption.NoSync)
                return;

            var timeoutAfter = Hub.Config.Distribution.SynchronizeTimeout;
            if (FailedBarrier == 1) // failed last iteration
                timeoutAfter *= 2; // try to re-sync in the event things are too slow.

            var result = Hub.BotSync.Barrier.SignalAndWait(TimeSpan.FromSeconds(timeoutAfter), token);

            if (result)
            {
                FailedBarrier = 0;
                return;
            }

            FailedBarrier++;
            Log($"Barrier sync timed out after {timeoutAfter} seconds. Continuing.");
        }

        /// <summary>
        /// Checks if the barrier needs to get updated to consider this bot.
        /// If it should be considered, it adds it to the barrier if it is not already added.
        /// If it should not be considered, it removes it from the barrier if not already removed.
        /// </summary>
        private void UpdateBarrier(bool shouldWait)
        {
            if (ShouldWaitAtBarrier == shouldWait)
                return; // no change required

            ShouldWaitAtBarrier = shouldWait;
            if (shouldWait)
            {
                Hub.BotSync.Barrier.AddParticipant();
                Log($"Joined the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
            else
            {
                Hub.BotSync.Barrier.RemoveParticipant();
                Log($"Left the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
        }
    }

    public class PartnerDataHolder
    {
        public readonly ulong TrainerOnlineID;
        public readonly string TrainerName;
        public readonly string TrainerTID;

        public PartnerDataHolder(ulong trainerNid, string trainerName, string trainerTid)
        {
            TrainerOnlineID = trainerNid;
            TrainerName = trainerName;
            TrainerTID = trainerTid;
        }
    }
}
