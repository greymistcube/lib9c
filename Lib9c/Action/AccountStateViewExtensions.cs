using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Bencodex.Types;
using Libplanet;
using Libplanet.Action;
using Libplanet.Assets;
using LruCacheNet;
using Nekoyume.Model.Arena;
using Nekoyume.Helper;
using Nekoyume.Model.State;
using Nekoyume.TableData;
using Serilog;
using static Lib9c.SerializeKeys;

namespace Nekoyume.Action
{
    public static class AccountStateViewExtensions
    {
        private const int SheetsCacheSize = 100;
        private static readonly LruCache<string, ISheet> SheetsCache = new LruCache<string, ISheet>(SheetsCacheSize);

        public static bool TryGetState<T>(this IAccountStateView states, Address address, out T result)
            where T : IValue
        {
            IValue raw = states.GetState(address);
            if (raw is T v)
            {
                result = v;
                return true;
            }

            Log.Error(
                "Expected a {0}, but got invalid state ({1}): ({2}) {3}",
                typeof(T).Name,
                address.ToHex(),
                raw?.GetType().Name,
                raw
            );
            result = default;
            return false;
        }

        public static IDictionary<Address, IValue> GetStatesAsDict(this IAccountStateView states, params Address[] addresses)
        {
            var result = new ConcurrentDictionary<Address, IValue>();
            Parallel.For(0, addresses.Length, i =>
            {
                var address = addresses[i];
                var value = states.GetState(address);
                result.TryAdd(address, value ?? Null.Value);
            });
            return result;
        }

        public static AgentState GetAgentState(this IAccountStateView states, Address address)
        {
            var serializedAgent = states.GetState(address);
            if (serializedAgent is null)
            {
                Log.Warning("No agent state ({0})", address.ToHex());
                return null;
            }

            try
            {
                return new AgentState((Bencodex.Types.Dictionary)serializedAgent);
            }
            catch (InvalidCastException e)
            {
                Log.Error(
                    e,
                    "Invalid agent state ({0}): {1}",
                    address.ToHex(),
                    serializedAgent
                );

                return null;
            }
        }

        public static bool TryGetGoldBalance(
            this IAccountStateView states,
            Address address,
            Currency currency,
            out FungibleAssetValue balance)
        {
            try
            {
                balance = states.GetBalance(address, currency);
                return true;
            }
            catch (BalanceDoesNotExistsException)
            {
                balance = default;
                return false;
            }
        }

        public static GoldBalanceState GetGoldBalanceState(
            this IAccountStateView states,
            Address address,
            Currency currency
        ) => new GoldBalanceState(address, states.GetBalance(address, currency));

        public static Currency GetGoldCurrency(this IAccountStateView states)
        {
            if (states.TryGetState(GoldCurrencyState.Address, out Dictionary asDict))
            {
                return new GoldCurrencyState(asDict).Currency;
            }

            throw new InvalidOperationException(
                "The states doesn't contain gold currency.\n" +
                "Check the genesis block."
            );
        }

        public static AvatarState GetAvatarState(this IAccountStateView states, Address address)
        {
            var serializedAvatar = states.GetState(address);
            if (serializedAvatar is null)
            {
                Log.Warning("No avatar state ({AvatarAddress})", address.ToHex());
                return null;
            }

            try
            {
                return new AvatarState((Bencodex.Types.Dictionary)serializedAvatar);
            }
            catch (InvalidCastException e)
            {
                Log.Error(
                    e,
                    "Invalid avatar state ({AvatarAddress}): {SerializedAvatar}",
                    address.ToHex(),
                    serializedAvatar
                );

                return null;
            }
        }

        public static AvatarState GetAvatarStateV2(
            this IAccountStateView states,
            Address address) => states.GetAvatarStateV2(address, new Stopwatch(), out _);

        public static AvatarState GetAvatarStateV2(this IAccountStateView states,
            Address address,
            Stopwatch getStateSw,
            out int getStateCount, Dictionary avatarDictionary = null)
        {
            string[] keys =
            {
                LegacyInventoryKey,
                LegacyWorldInformationKey,
                LegacyQuestListKey,
            };
            bool avatarDictExist = !(avatarDictionary is null);
            var addressMap = new Dictionary<int, Address>()
            {
                [0] = address
            };
            for (int i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                addressMap[i + 1] = address.Derive(key);
            }
            var serializedValues = new ConcurrentDictionary<int, IValue>();
            if (avatarDictExist)
            {
                serializedValues.TryAdd(0, avatarDictionary);
            }
            getStateCount = avatarDictExist ? addressMap.Count - 1 : addressMap.Count;
            getStateSw.Start();
            Parallel.For(0, addressMap.Count, i =>
            {
                if (serializedValues.ContainsKey(i))
                {
                    return;
                }
                var value = states.GetState(addressMap[i]);
                serializedValues.TryAdd(i, value);
            });
            getStateSw.Stop();
            if (!(serializedValues[0] is Dictionary serializedAvatar))
            {
                Log.Warning("No avatar state ({AvatarAddress})", address.ToHex());
                return null;
            }

            for (var i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                var serializedValue = serializedValues[i + 1];
                if (serializedValue is null)
                {
                    throw new FailedLoadStateException($"failed to load {key}.");
                }

                serializedAvatar = serializedAvatar.SetItem(key, serializedValue);
            }

            try
            {
                var sw = new Stopwatch();
                sw.Start();
                var avatarState = new AvatarState(serializedAvatar);
                sw.Stop();
                Log.Verbose("Deserialize AvatarState: {Elapsed}", sw.Elapsed);
                return avatarState;
            }
            catch (InvalidCastException e)
            {
                Log.Error(
                    e,
                    "Invalid avatar state ({AvatarAddress}): {SerializedAvatar}",
                    address.ToHex(),
                    serializedAvatar
                );

                return null;
            }
        }

        public static bool TryGetAvatarState(this IAccountStateView states,
            Address agentAddress,
            Address avatarAddress,
            out AvatarState avatarState, Dictionary serializedAvatar = null)
        {
            avatarState = null;
            if (serializedAvatar is null)
            {
                var value = states.GetState(avatarAddress);
                if (value is null)
                {
                    return false;
                }

                try
                {
                    serializedAvatar = (Dictionary) value;
                }
                catch (InvalidCastException)
                {
                    return false;
                }
            }

            try
            {
                if (serializedAvatar["agentAddress"].ToAddress() != agentAddress)
                {
                    return false;
                }

                avatarState = new AvatarState(serializedAvatar);
                return true;
            }
            catch (InvalidCastException)
            {
                return false;
            }
            catch (KeyNotFoundException)
            {
                return false;
            }
        }

        public static bool TryGetAvatarStateV2(
            this IAccountStateView states,
            Address agentAddress,
            Address avatarAddress,
            out AvatarState avatarState,
            out bool migrationRequired
        ) => states.TryGetAvatarStateV2(
            agentAddress,
            avatarAddress,
            new Stopwatch(),
            out avatarState,
            out migrationRequired,
            out _);

        public static bool TryGetAvatarStateV2(
            this IAccountStateView states,
            Address agentAddress,
            Address avatarAddress,
            Stopwatch getStateSw,
            out AvatarState avatarState,
            out bool migrationRequired,
            out int getStateCount
        )
        {
            avatarState = null;
            migrationRequired = false;
            getStateCount = 1;
            getStateSw.Start();
            var tempAvatarState = states.GetState(avatarAddress);
            getStateSw.Stop();
            if (tempAvatarState is Dictionary serializedAvatar)
            {
                try
                {
                    if (serializedAvatar[AgentAddressKey].ToAddress() != agentAddress)
                    {
                        return false;
                    }

                    avatarState = GetAvatarStateV2(states, avatarAddress, getStateSw, out int gsc, serializedAvatar);
                    getStateCount += gsc;
                    return true;
                }
                catch (Exception e)
                {
                    // BackWardCompatible.
                    if (e is KeyNotFoundException || e is FailedLoadStateException)
                    {
                        migrationRequired = true;
                        return states.TryGetAvatarState(agentAddress, avatarAddress,
                            out avatarState, serializedAvatar);
                    }

                    return false;
                }
            }

            return false;
        }

        public static bool TryGetAgentAvatarStates(
            this IAccountStateView states,
            Address agentAddress,
            Address avatarAddress,
            out AgentState agentState,
            out AvatarState avatarState
        )
        {
            avatarState = null;
            agentState = states.GetAgentState(agentAddress);
            if (agentState is null)
            {
                return false;
            }

            if (!agentState.avatarAddresses.ContainsValue(avatarAddress))
            {
                throw new AgentStateNotContainsAvatarAddressException(
                    $"The avatar {avatarAddress.ToHex()} does not belong to the agent {agentAddress.ToHex()}.");
            }

            avatarState = states.GetAvatarState(avatarAddress);
            return !(avatarState is null);
        }

        public static bool TryGetAgentAvatarStatesV2(
            this IAccountStateView states,
            Address agentAddress,
            Address avatarAddress,
            out AgentState agentState,
            out AvatarState avatarState,
            out bool avatarMigrationRequired
        )
        {
            avatarState = null;
            avatarMigrationRequired = false;
            agentState = states.GetAgentState(agentAddress);
            if (agentState is null)
            {
                return false;
            }

            if (!agentState.avatarAddresses.ContainsValue(avatarAddress))
            {
                throw new AgentStateNotContainsAvatarAddressException(
                    $"The avatar {avatarAddress.ToHex()} does not belong to the agent {agentAddress.ToHex()}.");
            }

            try
            {
                avatarState = states.GetAvatarStateV2(avatarAddress, new Stopwatch(), out _);
            }
            catch (FailedLoadStateException)
            {
                // BackWardCompatible.
                avatarState = states.GetAvatarState(avatarAddress);
                avatarMigrationRequired = true;
            }

            return !(avatarState is null);
        }

        public static WeeklyArenaState GetWeeklyArenaState(this IAccountStateView states, Address address)
        {
            var iValue = states.GetState(address);
            if (iValue is null)
            {
                Log.Warning("No weekly arena state ({0})", address.ToHex());
                return null;
            }

            try
            {
                return new WeeklyArenaState(iValue);
            }
            catch (InvalidCastException e)
            {
                Log.Error(
                    e,
                    "Invalid weekly arena state ({0}): {1}",
                    address.ToHex(),
                    iValue
                );

                return null;
            }
        }

        public static WeeklyArenaState GetWeeklyArenaState(this IAccountStateView states, int index)
        {
            var address = WeeklyArenaState.DeriveAddress(index);
            return GetWeeklyArenaState(states, address);
        }

        public static CombinationSlotState GetCombinationSlotState(
            this IAccountStateView states,
            Address avatarAddress,
            int index)
        {
            var address = avatarAddress.Derive(
                string.Format(
                    CultureInfo.InvariantCulture,
                    CombinationSlotState.DeriveFormat,
                    index
                )
            );
            var value = states.GetState(address);
            if (value is null)
            {
                Log.Warning("No combination slot state ({0})", address.ToHex());
                return null;
            }

            try
            {
                return new CombinationSlotState((Dictionary)value);
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unexpected error occurred during {nameof(GetCombinationSlotState)}()");
                throw;
            }
        }

        public static GameConfigState GetGameConfigState(this IAccountStateView states)
        {
            var value = states.GetState(GameConfigState.Address);
            if (value is null)
            {
                Log.Warning("No game config state ({0})", GameConfigState.Address.ToHex());
                return null;
            }

            try
            {
                return new GameConfigState((Dictionary)value);
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unexpected error occurred during {nameof(GetCombinationSlotState)}()");
                throw;
            }
        }

        public static RedeemCodeState GetRedeemCodeState(this IAccountStateView states)
        {
            var value = states.GetState(RedeemCodeState.Address);
            if (value is null)
            {
                Log.Warning("RedeemCodeState is null. ({0})", RedeemCodeState.Address.ToHex());
                return null;
            }

            try
            {
                return new RedeemCodeState((Dictionary)value);
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unexpected error occurred during {nameof(GetCombinationSlotState)}()");
                throw;
            }
        }

        public static IEnumerable<GoldDistribution> GetGoldDistribution(
            this IAccountStateView states)
        {
            var value = states.GetState(Addresses.GoldDistribution);
            if (value is null)
            {
                Log.Warning($"{nameof(GoldDistribution)} is null ({0})", Addresses.GoldDistribution.ToHex());
                return null;
            }

            try
            {
                var goldDistributions = (Bencodex.Types.List)value;
                return goldDistributions.Select(v => new GoldDistribution(v));
            }
            catch (Exception e)
            {
                Log.Error(e, $"Unexpected error occurred during {nameof(GetGoldDistribution)}()");
                throw;
            }
        }

        public static T GetSheet<T>(this IAccountStateView states) where T : ISheet, new()
        {
            var address = Addresses.GetSheetAddress<T>();

            try
            {
                var csv = GetSheetCsv<T>(states);
                byte[] hash;
                using (var sha256 = SHA256.Create())
                {
                    hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(csv));
                }

                var cacheKey = address.ToHex() + ByteUtil.Hex(hash);
                if (SheetsCache.TryGetValue(cacheKey, out var cached))
                {
                    return (T)cached;
                }

                var sheet = new T();
                sheet.Set(csv);
                SheetsCache.AddOrUpdate(cacheKey, sheet);
                return sheet;
            }
            catch (Exception e)
            {
                Log.Error(e, "Unexpected error occurred during GetSheet<{TypeName}>()", typeof(T).FullName);
                throw;
            }
        }

        public static bool TryGetSheet<T>(this IAccountStateView states, out T sheet) where T : ISheet, new()
        {
            try
            {
                sheet = states.GetSheet<T>();
                return true;
            }
            catch (Exception)
            {
                sheet = default;
                return false;
            }
        }

        public static Dictionary<Type, (Address address, ISheet sheet)> GetSheets(
            this IAccountStateView states,
            bool containAvatarSheets = false,
            bool containItemSheet = false,
            bool containQuestSheet = false,
            bool containSimulatorSheets = false,
            bool containStageSimulatorSheets = false,
            bool containRankingSimulatorSheets = false,
            bool containArenaSimulatorSheets = false,
            bool containValidateItemRequirementSheets = false,
            bool containRaidSimulatorSheets = false,
            IEnumerable<Type> sheetTypes = null) => states.GetSheets(
            new Stopwatch(),
            out _,
            containAvatarSheets,
            containItemSheet,
            containQuestSheet,
            containSimulatorSheets,
            containStageSimulatorSheets,
            containRankingSimulatorSheets,
            containArenaSimulatorSheets,
            containValidateItemRequirementSheets,
            containRaidSimulatorSheets,
            sheetTypes);

        public static Dictionary<Type, (Address address, ISheet sheet)> GetSheets(
            this IAccountStateView states,
            Stopwatch getStateSw,
            out int getStateCount,
            bool containAvatarSheets = false,
            bool containItemSheet = false,
            bool containQuestSheet = false,
            bool containSimulatorSheets = false,
            bool containStageSimulatorSheets = false,
            bool containRankingSimulatorSheets = false,
            bool containArenaSimulatorSheets = false,
            bool containValidateItemRequirementSheets = false,
            bool containRaidSimulatorSheets = false,
            IEnumerable<Type> sheetTypes = null)
        {
            var sheetTypeList = sheetTypes?.ToList() ?? new List<Type>();
            if (containAvatarSheets)
            {
                // AvatarSheets need QuestSheet
                containQuestSheet = true;
                sheetTypeList.Add(typeof(WorldSheet));
                sheetTypeList.Add(typeof(QuestRewardSheet));
                sheetTypeList.Add(typeof(QuestItemRewardSheet));
                sheetTypeList.Add(typeof(EquipmentItemRecipeSheet));
                sheetTypeList.Add(typeof(EquipmentItemSubRecipeSheet));
            }

            if (containItemSheet)
            {
                sheetTypeList.Add(typeof(ConsumableItemSheet));
                sheetTypeList.Add(typeof(CostumeItemSheet));
                sheetTypeList.Add(typeof(EquipmentItemSheet));
                sheetTypeList.Add(typeof(MaterialItemSheet));
            }

            if (containQuestSheet)
            {
                sheetTypeList.Add(typeof(WorldQuestSheet));
                sheetTypeList.Add(typeof(CollectQuestSheet));
                sheetTypeList.Add(typeof(CombinationQuestSheet));
                sheetTypeList.Add(typeof(TradeQuestSheet));
                sheetTypeList.Add(typeof(MonsterQuestSheet));
                sheetTypeList.Add(typeof(ItemEnhancementQuestSheet));
                sheetTypeList.Add(typeof(GeneralQuestSheet));
                sheetTypeList.Add(typeof(ItemGradeQuestSheet));
                sheetTypeList.Add(typeof(ItemTypeCollectQuestSheet));
                sheetTypeList.Add(typeof(GoldQuestSheet));
                sheetTypeList.Add(typeof(CombinationEquipmentQuestSheet));
            }

            if (containSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(StatBuffSheet));
                sheetTypeList.Add(typeof(SkillActionBuffSheet));
                sheetTypeList.Add(typeof(ActionBuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
                sheetTypeList.Add(typeof(RuneOptionSheet));
            }

            if (containStageSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(StatBuffSheet));
                sheetTypeList.Add(typeof(SkillActionBuffSheet));
                sheetTypeList.Add(typeof(ActionBuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
                sheetTypeList.Add(typeof(StageSheet));
                sheetTypeList.Add(typeof(StageWaveSheet));
                sheetTypeList.Add(typeof(EnemySkillSheet));
                sheetTypeList.Add(typeof(RuneOptionSheet));
            }

            if (containRankingSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(StatBuffSheet));
                sheetTypeList.Add(typeof(SkillActionBuffSheet));
                sheetTypeList.Add(typeof(ActionBuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
                sheetTypeList.Add(typeof(WeeklyArenaRewardSheet));
                sheetTypeList.Add(typeof(RuneOptionSheet));
            }

            if (containArenaSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(StatBuffSheet));
                sheetTypeList.Add(typeof(SkillActionBuffSheet));
                sheetTypeList.Add(typeof(ActionBuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
                sheetTypeList.Add(typeof(WeeklyArenaRewardSheet));
                sheetTypeList.Add(typeof(CostumeStatSheet));
                sheetTypeList.Add(typeof(RuneOptionSheet));
            }

            if (containValidateItemRequirementSheets)
            {
                sheetTypeList.Add(typeof(ItemRequirementSheet));
                sheetTypeList.Add(typeof(EquipmentItemRecipeSheet));
                sheetTypeList.Add(typeof(EquipmentItemSubRecipeSheetV2));
                sheetTypeList.Add(typeof(EquipmentItemOptionSheet));
            }

            if (containRaidSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(StatBuffSheet));
                sheetTypeList.Add(typeof(SkillActionBuffSheet));
                sheetTypeList.Add(typeof(ActionBuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
                sheetTypeList.Add(typeof(WorldBossCharacterSheet));
                sheetTypeList.Add(typeof(EnemySkillSheet));
                sheetTypeList.Add(typeof(WorldBossBattleRewardSheet));
                sheetTypeList.Add(typeof(RuneWeightSheet));
                sheetTypeList.Add(typeof(RuneSheet));
                sheetTypeList.Add(typeof(RuneOptionSheet));
            }

            return states.GetSheets(getStateSw, out getStateCount, sheetTypeList.Distinct().ToArray());
        }

        public static Dictionary<Type, (Address address, ISheet sheet)> GetSheets(
            this IAccountStateView states,
            params Type[] sheetTypes) => states.GetSheets(new Stopwatch(), out _, sheetTypes);

        public static Dictionary<Type, (Address address, ISheet sheet)> GetSheets(
                this IAccountStateView states,
                Stopwatch getStateSw,
                out int getStateCount,
                params Type[] sheetTypes)
            {
                ConcurrentDictionary<Type, (Address address, ISheet sheet)> result = new ConcurrentDictionary<Type, (Address address, ISheet sheet)>();
            getStateCount = sheetTypes.Length;
            getStateSw.Start();
            Parallel.For(0, sheetTypes.Length, i =>
            {
                var sheetType = sheetTypes[i];
                var address = Addresses.GetSheetAddress(sheetType.Name);
                var csvValue = states.GetState(address);
                if (csvValue is null)
                {
                    throw new FailedLoadStateException(address, sheetType);
                }

                var csv = csvValue.ToDotnetString();
                byte[] hash;
                using (var sha256 = SHA256.Create())
                {
                    hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(csv));
                }

                var cacheKey = address.ToHex() + ByteUtil.Hex(hash);
                if (SheetsCache.TryGetValue(cacheKey, out var cached))
                {
                    result.TryAdd(sheetType, (address, cached));
                    return;
                }

                var sheetConstructorInfo = sheetType.GetConstructor(Type.EmptyTypes);
                if (!(sheetConstructorInfo?.Invoke(Array.Empty<object>()) is ISheet sheet))
                {
                    throw new FailedLoadSheetException(sheetType);
                }

                sheet.Set(csv);
                SheetsCache.AddOrUpdate(cacheKey, sheet);
                result.TryAdd(sheetType, (address, sheet));
            });
            getStateSw.Stop();

            return result.ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        public static string GetSheetCsv<T>(this IAccountStateView states) where T : ISheet, new()
        {
            var address = Addresses.GetSheetAddress<T>();
            var value = states.GetState(address);
            if (value is null)
            {
                Log.Warning("{TypeName} is null ({Address})", typeof(T).FullName, address.ToHex());
                throw new FailedLoadStateException(typeof(T).FullName);
            }

            try
            {
                return value.ToDotnetString();
            }
            catch (Exception e)
            {
                Log.Error(e, "Unexpected error occurred during GetSheetCsv<{TypeName}>()", typeof(T).FullName);
                throw;
            }
        }

        public static ItemSheet GetItemSheet(this IAccountStateView states)
        {
            var sheet = new ItemSheet();
            sheet.Set(GetSheet<ConsumableItemSheet>(states), false);
            sheet.Set(GetSheet<CostumeItemSheet>(states), false);
            sheet.Set(GetSheet<EquipmentItemSheet>(states), false);
            sheet.Set(GetSheet<MaterialItemSheet>(states));
            return sheet;
        }

        public static StageSimulatorSheetsV1 GetStageSimulatorSheetsV1(this IAccountStateView states)
        {
            return new StageSimulatorSheetsV1(
                GetSheet<MaterialItemSheet>(states),
                GetSheet<SkillSheet>(states),
                GetSheet<SkillBuffSheet>(states),
                GetSheet<StatBuffSheet>(states),
                GetSheet<SkillActionBuffSheet>(states),
                GetSheet<ActionBuffSheet>(states),
                GetSheet<CharacterSheet>(states),
                GetSheet<CharacterLevelSheet>(states),
                GetSheet<EquipmentItemSetEffectSheet>(states),
                GetSheet<StageSheet>(states),
                GetSheet<StageWaveSheet>(states),
                GetSheet<EnemySkillSheet>(states)
            );
        }

        public static StageSimulatorSheets GetStageSimulatorSheets(this IAccountStateView states)
        {
            return new StageSimulatorSheets(
                GetSheet<MaterialItemSheet>(states),
                GetSheet<SkillSheet>(states),
                GetSheet<SkillBuffSheet>(states),
                GetSheet<StatBuffSheet>(states),
                GetSheet<SkillActionBuffSheet>(states),
                GetSheet<ActionBuffSheet>(states),
                GetSheet<CharacterSheet>(states),
                GetSheet<CharacterLevelSheet>(states),
                GetSheet<EquipmentItemSetEffectSheet>(states),
                GetSheet<StageSheet>(states),
                GetSheet<StageWaveSheet>(states),
                GetSheet<EnemySkillSheet>(states),
                GetSheet<RuneOptionSheet>(states)
            );
        }

        public static RankingSimulatorSheetsV1 GetRankingSimulatorSheetsV1(this IAccountStateView states)
        {
            return new RankingSimulatorSheetsV1(
                GetSheet<MaterialItemSheet>(states),
                GetSheet<SkillSheet>(states),
                GetSheet<SkillBuffSheet>(states),
                GetSheet<StatBuffSheet>(states),
                GetSheet<SkillActionBuffSheet>(states),
                GetSheet<ActionBuffSheet>(states),
                GetSheet<CharacterSheet>(states),
                GetSheet<CharacterLevelSheet>(states),
                GetSheet<EquipmentItemSetEffectSheet>(states),
                GetSheet<WeeklyArenaRewardSheet>(states)
            );
        }

        public static RankingSimulatorSheets GetRankingSimulatorSheets(this IAccountStateView states)
        {
            return new RankingSimulatorSheets(
                GetSheet<MaterialItemSheet>(states),
                GetSheet<SkillSheet>(states),
                GetSheet<SkillBuffSheet>(states),
                GetSheet<StatBuffSheet>(states),
                GetSheet<SkillActionBuffSheet>(states),
                GetSheet<ActionBuffSheet>(states),
                GetSheet<CharacterSheet>(states),
                GetSheet<CharacterLevelSheet>(states),
                GetSheet<EquipmentItemSetEffectSheet>(states),
                GetSheet<WeeklyArenaRewardSheet>(states),
                GetSheet<RuneOptionSheet>(states)
            );
        }

        public static QuestSheet GetQuestSheet(this IAccountStateView states)
        {
            var questSheet = new QuestSheet();
            questSheet.Set(GetSheet<WorldQuestSheet>(states), false);
            questSheet.Set(GetSheet<CollectQuestSheet>(states), false);
            questSheet.Set(GetSheet<CombinationQuestSheet>(states), false);
            questSheet.Set(GetSheet<TradeQuestSheet>(states), false);
            questSheet.Set(GetSheet<MonsterQuestSheet>(states), false);
            questSheet.Set(GetSheet<ItemEnhancementQuestSheet>(states), false);
            questSheet.Set(GetSheet<GeneralQuestSheet>(states), false);
            questSheet.Set(GetSheet<ItemGradeQuestSheet>(states), false);
            questSheet.Set(GetSheet<ItemTypeCollectQuestSheet>(states), false);
            questSheet.Set(GetSheet<GoldQuestSheet>(states), false);
            questSheet.Set(GetSheet<CombinationEquipmentQuestSheet>(states));
            return questSheet;
        }

        public static AvatarSheets GetAvatarSheets(this IAccountStateView states)
        {
            return new AvatarSheets(
                GetSheet<WorldSheet>(states),
                GetQuestSheet(states),
                GetSheet<QuestRewardSheet>(states),
                GetSheet<QuestItemRewardSheet>(states),
                GetSheet<EquipmentItemRecipeSheet>(states),
                GetSheet<EquipmentItemSubRecipeSheet>(states)
            );
        }

        public static RankingState GetRankingState(this IAccountStateView states)
        {
            var value = states.GetState(Addresses.Ranking);
            if (value is null)
            {
                throw new FailedLoadStateException(nameof(RankingState0));
            }

            return new RankingState((Dictionary)value);
        }

        public static RankingState1 GetRankingState1(this IAccountStateView states)
        {
            var value = states.GetState(Addresses.Ranking);
            if (value is null)
            {
                throw new FailedLoadStateException(nameof(RankingState1));
            }

            return new RankingState1((Dictionary)value);
        }

        public static RankingState0 GetRankingState0(this IAccountStateView states)
        {
            var value = states.GetState(Addresses.Ranking);
            if (value is null)
            {
                throw new FailedLoadStateException(nameof(RankingState0));
            }

            return new RankingState0((Dictionary)value);
        }

        public static ShopState GetShopState(this IAccountStateView states)
        {
            var value = states.GetState(Addresses.Shop);
            if (value is null)
            {
                throw new FailedLoadStateException(nameof(ShopState));
            }

            return new ShopState((Dictionary)value);
        }

        public static (Address arenaInfoAddress, ArenaInfo arenaInfo, bool isNewArenaInfo) GetArenaInfo(
            this IAccountStateView states,
            Address weeklyArenaAddress,
            AvatarState avatarState,
            CharacterSheet characterSheet,
            CostumeStatSheet costumeStatSheet)
        {
            var arenaInfoAddress = weeklyArenaAddress.Derive(avatarState.address.ToByteArray());
            var isNew = false;
            ArenaInfo arenaInfo;
            if (states.TryGetState(arenaInfoAddress, out Dictionary rawArenaInfo))
            {
                arenaInfo = new ArenaInfo(rawArenaInfo);
            }
            else
            {
                arenaInfo = new ArenaInfo(avatarState, characterSheet, costumeStatSheet, true);
                isNew = true;
            }

            return (arenaInfoAddress, arenaInfo, isNew);
        }

        public static bool TryGetStakeState(
            this IAccountStateView states,
            Address agentAddress,
            out StakeState stakeState)
        {
            if (states.TryGetState(StakeState.DeriveAddress(agentAddress), out Dictionary dictionary))
            {
                stakeState = new StakeState(dictionary);
                return true;
            }

            stakeState = null;
            return false;
        }

        public static ArenaParticipants GetArenaParticipants(this IAccountStateView states,
            Address arenaParticipantsAddress, int id, int round)
        {
            return states.TryGetState(arenaParticipantsAddress, out List list)
                ? new ArenaParticipants(list)
                : new ArenaParticipants(id, round);
        }

        public static ArenaAvatarState GetArenaAvatarState(this IAccountStateView states,
            Address arenaAvatarStateAddress, AvatarState avatarState)
        {
            return states.TryGetState(arenaAvatarStateAddress, out List list)
                ? new ArenaAvatarState(list)
                : new ArenaAvatarState(avatarState);
        }

        public static bool TryGetArenaParticipants(this IAccountStateView states,
            Address arenaParticipantsAddress, out ArenaParticipants arenaParticipants)
        {
            if (states.TryGetState(arenaParticipantsAddress, out List list))
            {
                arenaParticipants = new ArenaParticipants(list);
                return true;
            }

            arenaParticipants = null;
            return false;
        }

        public static bool TryGetArenaAvatarState(this IAccountStateView states,
            Address arenaAvatarStateAddress, out ArenaAvatarState arenaAvatarState)
        {
            if (states.TryGetState(arenaAvatarStateAddress, out List list))
            {
                arenaAvatarState = new ArenaAvatarState(list);
                return true;
            }

            arenaAvatarState = null;
            return false;
        }

        public static bool TryGetArenaScore(this IAccountStateView states,
            Address arenaScoreAddress, out ArenaScore arenaScore)
        {
            if (states.TryGetState(arenaScoreAddress, out List list))
            {
                arenaScore = new ArenaScore(list);
                return true;
            }

            arenaScore = null;
            return false;
        }

        public static bool TryGetArenaInformation(this IAccountStateView states,
            Address arenaInformationAddress, out ArenaInformation arenaInformation)
        {
            if (states.TryGetState(arenaInformationAddress, out List list))
            {
                arenaInformation = new ArenaInformation(list);
                return true;
            }

            arenaInformation = null;
            return false;
        }

        public static AvatarState GetEnemyAvatarState(this IAccountStateView states, Address avatarAddress)
        {
            AvatarState enemyAvatarState;
            try
            {
                enemyAvatarState = states.GetAvatarStateV2(avatarAddress, new Stopwatch(), out _);
            }
            // BackWard compatible.
            catch (FailedLoadStateException)
            {
                enemyAvatarState = states.GetAvatarState(avatarAddress);
            }

            if (enemyAvatarState is null)
            {
                throw new FailedLoadStateException(
                    $"Aborted as the avatar state of the opponent ({avatarAddress}) was failed to load.");
            }

            return enemyAvatarState;
        }

        public static CrystalCostState GetCrystalCostState(this IAccountStateView states,
            Address address)
        {
            return states.TryGetState(address, out List rawState)
                ? new CrystalCostState(address, rawState)
                : new CrystalCostState(address, 0 * CrystalCalculator.CRYSTAL);
        }

        public static (
            CrystalCostState DailyCostState,
            CrystalCostState WeeklyCostState,
            CrystalCostState PrevWeeklyCostState,
            CrystalCostState BeforePrevWeeklyCostState
            ) GetCrystalCostStates(this IAccountStateView states, long blockIndex, long interval)
        {
            int dailyCostIndex = (int) (blockIndex / CrystalCostState.DailyIntervalIndex);
            int weeklyCostIndex = (int) (blockIndex / interval);
            Address dailyCostAddress = Addresses.GetDailyCrystalCostAddress(dailyCostIndex);
            CrystalCostState dailyCostState = states.GetCrystalCostState(dailyCostAddress);
            Address weeklyCostAddress = Addresses.GetWeeklyCrystalCostAddress(weeklyCostIndex);
            CrystalCostState weeklyCostState = states.GetCrystalCostState(weeklyCostAddress);
            CrystalCostState prevWeeklyCostState = null;
            CrystalCostState beforePrevWeeklyCostState = null;
            if (weeklyCostIndex > 1)
            {
                Address prevWeeklyCostAddress =
                    Addresses.GetWeeklyCrystalCostAddress(weeklyCostIndex - 1);
                prevWeeklyCostState = states.GetCrystalCostState(prevWeeklyCostAddress);
                Address beforePrevWeeklyCostAddress =
                    Addresses.GetWeeklyCrystalCostAddress(weeklyCostIndex - 2);
                beforePrevWeeklyCostState = states.GetCrystalCostState(beforePrevWeeklyCostAddress);
            }

            return (dailyCostState, weeklyCostState, prevWeeklyCostState,
                beforePrevWeeklyCostState);
        }

        public static void ValidateWorldId(this IAccountStateView states, Address avatarAddress, int worldId)
        {
            if (worldId > 1)
            {
                if (worldId == GameConfig.MimisbrunnrWorldId)
                {
                    throw new InvalidWorldException();
                }

                var unlockedWorldIdsAddress = avatarAddress.Derive("world_ids");

                // Unlock First.
                if (!states.TryGetState(unlockedWorldIdsAddress, out List rawIds))
                {
                    throw new InvalidWorldException();
                }

                List<int> unlockedWorldIds = rawIds.ToList(StateExtensions.ToInteger);
                if (!unlockedWorldIds.Contains(worldId))
                {
                    throw new InvalidWorldException();
                }
            }
        }

        public static RaiderState GetRaiderState(this IAccountStateView states,
            Address avatarAddress, int raidId)
        {
            return GetRaiderState(states, Addresses.GetRaiderAddress(avatarAddress, raidId));
        }

        public static RaiderState GetRaiderState(this IAccountStateView states,
            Address raiderAddress)
        {
            if (states.TryGetState(raiderAddress, out List rawRaider))
            {
                return new RaiderState(rawRaider);
            }

            throw new FailedLoadStateException("can't find RaiderState.");
        }

        public static Dictionary<Type, (Address address, ISheet sheet)> GetSheetsV100291(
            this IAccountStateView states,
            bool containAvatarSheets = false,
            bool containItemSheet = false,
            bool containQuestSheet = false,
            bool containSimulatorSheets = false,
            bool containStageSimulatorSheets = false,
            bool containRankingSimulatorSheets = false,
            bool containArenaSimulatorSheets = false,
            bool containValidateItemRequirementSheets = false,
            IEnumerable<Type> sheetTypes = null)
        {
            var sheetTypeList = sheetTypes?.ToList() ?? new List<Type>();
            if (containAvatarSheets)
            {
                // AvatarSheets need QuestSheet
                containQuestSheet = true;
                sheetTypeList.Add(typeof(WorldSheet));
                sheetTypeList.Add(typeof(QuestRewardSheet));
                sheetTypeList.Add(typeof(QuestItemRewardSheet));
                sheetTypeList.Add(typeof(EquipmentItemRecipeSheet));
                sheetTypeList.Add(typeof(EquipmentItemSubRecipeSheet));
            }

            if (containItemSheet)
            {
                sheetTypeList.Add(typeof(ConsumableItemSheet));
                sheetTypeList.Add(typeof(CostumeItemSheet));
                sheetTypeList.Add(typeof(EquipmentItemSheet));
                sheetTypeList.Add(typeof(MaterialItemSheet));
            }

            if (containQuestSheet)
            {
                sheetTypeList.Add(typeof(WorldQuestSheet));
                sheetTypeList.Add(typeof(CollectQuestSheet));
                sheetTypeList.Add(typeof(CombinationQuestSheet));
                sheetTypeList.Add(typeof(TradeQuestSheet));
                sheetTypeList.Add(typeof(MonsterQuestSheet));
                sheetTypeList.Add(typeof(ItemEnhancementQuestSheet));
                sheetTypeList.Add(typeof(GeneralQuestSheet));
                sheetTypeList.Add(typeof(ItemGradeQuestSheet));
                sheetTypeList.Add(typeof(ItemTypeCollectQuestSheet));
                sheetTypeList.Add(typeof(GoldQuestSheet));
                sheetTypeList.Add(typeof(CombinationEquipmentQuestSheet));
            }

            if (containSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(BuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
            }

            if (containStageSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(BuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
                sheetTypeList.Add(typeof(StageSheet));
                sheetTypeList.Add(typeof(StageWaveSheet));
                sheetTypeList.Add(typeof(EnemySkillSheet));
            }

            if (containRankingSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(BuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
                sheetTypeList.Add(typeof(WeeklyArenaRewardSheet));
            }

            if (containArenaSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(BuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
                sheetTypeList.Add(typeof(WeeklyArenaRewardSheet));
                sheetTypeList.Add(typeof(CostumeStatSheet));
            }

            if (containValidateItemRequirementSheets)
            {
                sheetTypeList.Add(typeof(ItemRequirementSheet));
                sheetTypeList.Add(typeof(EquipmentItemRecipeSheet));
                sheetTypeList.Add(typeof(EquipmentItemSubRecipeSheetV2));
                sheetTypeList.Add(typeof(EquipmentItemOptionSheet));
            }

            return states.GetSheets(new Stopwatch(), out _, sheetTypeList.Distinct().ToArray());
        }

        public static Dictionary<Type, (Address address, ISheet sheet)> GetSheetsV1(
            this IAccountStateView states,
            bool containAvatarSheets = false,
            bool containItemSheet = false,
            bool containQuestSheet = false,
            bool containSimulatorSheets = false,
            bool containStageSimulatorSheets = false,
            bool containRankingSimulatorSheets = false,
            bool containArenaSimulatorSheets = false,
            bool containValidateItemRequirementSheets = false,
            bool containRaidSimulatorSheets = false,
            IEnumerable<Type> sheetTypes = null)
        {
            var sheetTypeList = sheetTypes?.ToList() ?? new List<Type>();
            if (containAvatarSheets)
            {
                // AvatarSheets need QuestSheet
                containQuestSheet = true;
                sheetTypeList.Add(typeof(WorldSheet));
                sheetTypeList.Add(typeof(QuestRewardSheet));
                sheetTypeList.Add(typeof(QuestItemRewardSheet));
                sheetTypeList.Add(typeof(EquipmentItemRecipeSheet));
                sheetTypeList.Add(typeof(EquipmentItemSubRecipeSheet));
            }

            if (containItemSheet)
            {
                sheetTypeList.Add(typeof(ConsumableItemSheet));
                sheetTypeList.Add(typeof(CostumeItemSheet));
                sheetTypeList.Add(typeof(EquipmentItemSheet));
                sheetTypeList.Add(typeof(MaterialItemSheet));
            }

            if (containQuestSheet)
            {
                sheetTypeList.Add(typeof(WorldQuestSheet));
                sheetTypeList.Add(typeof(CollectQuestSheet));
                sheetTypeList.Add(typeof(CombinationQuestSheet));
                sheetTypeList.Add(typeof(TradeQuestSheet));
                sheetTypeList.Add(typeof(MonsterQuestSheet));
                sheetTypeList.Add(typeof(ItemEnhancementQuestSheet));
                sheetTypeList.Add(typeof(GeneralQuestSheet));
                sheetTypeList.Add(typeof(ItemGradeQuestSheet));
                sheetTypeList.Add(typeof(ItemTypeCollectQuestSheet));
                sheetTypeList.Add(typeof(GoldQuestSheet));
                sheetTypeList.Add(typeof(CombinationEquipmentQuestSheet));
            }

            if (containSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(StatBuffSheet));
                sheetTypeList.Add(typeof(SkillActionBuffSheet));
                sheetTypeList.Add(typeof(ActionBuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
            }

            if (containStageSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(StatBuffSheet));
                sheetTypeList.Add(typeof(SkillActionBuffSheet));
                sheetTypeList.Add(typeof(ActionBuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
                sheetTypeList.Add(typeof(StageSheet));
                sheetTypeList.Add(typeof(StageWaveSheet));
                sheetTypeList.Add(typeof(EnemySkillSheet));
            }

            if (containRankingSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(StatBuffSheet));
                sheetTypeList.Add(typeof(SkillActionBuffSheet));
                sheetTypeList.Add(typeof(ActionBuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
                sheetTypeList.Add(typeof(WeeklyArenaRewardSheet));
            }

            if (containArenaSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(StatBuffSheet));
                sheetTypeList.Add(typeof(SkillActionBuffSheet));
                sheetTypeList.Add(typeof(ActionBuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
                sheetTypeList.Add(typeof(WeeklyArenaRewardSheet));
                sheetTypeList.Add(typeof(CostumeStatSheet));
            }

            if (containValidateItemRequirementSheets)
            {
                sheetTypeList.Add(typeof(ItemRequirementSheet));
                sheetTypeList.Add(typeof(EquipmentItemRecipeSheet));
                sheetTypeList.Add(typeof(EquipmentItemSubRecipeSheetV2));
                sheetTypeList.Add(typeof(EquipmentItemOptionSheet));
            }

            if (containRaidSimulatorSheets)
            {
                sheetTypeList.Add(typeof(MaterialItemSheet));
                sheetTypeList.Add(typeof(SkillSheet));
                sheetTypeList.Add(typeof(SkillBuffSheet));
                sheetTypeList.Add(typeof(StatBuffSheet));
                sheetTypeList.Add(typeof(SkillActionBuffSheet));
                sheetTypeList.Add(typeof(ActionBuffSheet));
                sheetTypeList.Add(typeof(CharacterSheet));
                sheetTypeList.Add(typeof(CharacterLevelSheet));
                sheetTypeList.Add(typeof(EquipmentItemSetEffectSheet));
                sheetTypeList.Add(typeof(WorldBossCharacterSheet));
                sheetTypeList.Add(typeof(EnemySkillSheet));
                sheetTypeList.Add(typeof(WorldBossBattleRewardSheet));
                sheetTypeList.Add(typeof(RuneWeightSheet));
                sheetTypeList.Add(typeof(RuneSheet));
            }

            return states.GetSheets(new Stopwatch(), out _, sheetTypeList.Distinct().ToArray());
        }
    }
}
