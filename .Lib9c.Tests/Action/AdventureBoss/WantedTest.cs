namespace Lib9c.Tests.Action.AdventureBoss
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Numerics;
    using Libplanet.Action.State;
    using Libplanet.Crypto;
    using Libplanet.Mocks;
    using Libplanet.Types.Assets;
    using Nekoyume;
    using Nekoyume.Action;
    using Nekoyume.Action.AdventureBoss;
    using Nekoyume.Action.Exceptions.AdventureBoss;
    using Nekoyume.Model.AdventureBoss;
    using Nekoyume.Model.State;
    using Nekoyume.Module;
    using Nekoyume.TableData;
    using Xunit;

    public class WantedTest
    {
        private const int InitialBalance = 1_000_000;

        private static readonly Dictionary<string, string> Sheets =
            TableSheetsImporter.ImportSheets();

        private static readonly TableSheets TableSheets = new TableSheets(Sheets);
#pragma warning disable CS0618
        // Use of obsolete method Currency.Legacy(): https://github.com/planetarium/lib9c/discussions/1419
        private static readonly Currency NCG = Currency.Legacy("NCG", 2, null);
#pragma warning restore CS0618
        private static readonly Address AgentAddress = new PrivateKey().Address;
        private static readonly Address AvatarAddress = Addresses.GetAvatarAddress(AgentAddress, 0);

        private static readonly AvatarState AvatarState = new (
            AvatarAddress, AgentAddress, 0L, TableSheets.GetAvatarSheets(), new PrivateKey().Address
        );

        private static readonly Address
            AvatarAddress2 = Addresses.GetAvatarAddress(AgentAddress, 1);

        private static readonly AvatarState AvatarState2 = new (
            AvatarAddress2, AgentAddress, 0L, TableSheets.GetAvatarSheets(),
            new PrivateKey().Address
        );

        private static readonly GoldCurrencyState GoldCurrencyState = new (NCG);

        private static readonly AgentState AgentState = new (AgentAddress)
        {
            avatarAddresses =
            {
                [0] = AvatarAddress,
                [1] = AvatarAddress2,
            },
        };

        private readonly IWorld _initialState = new World(MockUtil.MockModernWorldState)
            .SetAvatarState(AvatarAddress, AvatarState)
            .SetAvatarState(AvatarAddress2, AvatarState2)
            .SetLegacyState(Addresses.GoldCurrency, GoldCurrencyState.Serialize())
            .SetAgentState(AgentAddress, AgentState)
            .MintAsset(new ActionContext(), AgentAddress, InitialBalance * NCG);

        [Fact]
        public void Execute()
        {
            var state = Stake(_initialState);
            var startBalance = state.GetBalance(AgentAddress, NCG);

            // Set active season
            var season = new SeasonInfo(1, 0L);
            state = state.SetSeasonInfo(season);
            state = state.SetLatestAdventureBossSeason(season);

            var action = new Wanted
            {
                Season = 1,
                AvatarAddress = AvatarAddress,
                Bounty = Wanted.MinBounty * NCG,
            };
            var nextState = action.Execute(new ActionContext
            {
                PreviousState = state,
                Signer = AgentAddress,
                BlockIndex = 0L,
            });
            Assert.Equal(
                startBalance - Wanted.MinBounty * NCG,
                nextState.GetBalance(AgentAddress, NCG)
            );
            Assert.Equal(Wanted.MinBounty * NCG, nextState.GetBalance(Addresses.BountyBoard, NCG));
            var bountyBoard = nextState.GetBountyBoard(1);
            Assert.NotNull(bountyBoard);
            var investor = Assert.Single(bountyBoard.Investors);
            Assert.Equal(AvatarAddress, investor.AvatarAddress);
            Assert.Equal(Wanted.MinBounty * NCG, investor.Price);
            Assert.Equal(1, investor.Count);

            action.AvatarAddress = AvatarAddress2;
            nextState = action.Execute(new ActionContext
            {
                PreviousState = nextState,
                Signer = AgentAddress,
                BlockIndex = 1L,
            });

            Assert.Equal(
                startBalance - Wanted.MinBounty * 2 * NCG,
                nextState.GetBalance(AgentAddress, NCG)
            );
            Assert.Equal(
                Wanted.MinBounty * 2 * NCG,
                nextState.GetBalance(Addresses.BountyBoard, NCG)
            );
            bountyBoard = nextState.GetBountyBoard(1);
            Assert.NotNull(bountyBoard);
            Assert.Equal(2, bountyBoard.Investors.Count);
            investor = bountyBoard.Investors.First(i => i.AvatarAddress == AvatarAddress2);
            Assert.Equal(Wanted.MinBounty * NCG, investor.Price);
            Assert.Equal(1, investor.Count);

            action.AvatarAddress = AvatarAddress;
            nextState = action.Execute(new ActionContext
            {
                PreviousState = nextState,
                Signer = AgentAddress,
                BlockIndex = 2L,
            });

            Assert.Equal(
                startBalance - Wanted.MinBounty * 3 * NCG,
                nextState.GetBalance(AgentAddress, NCG)
            );
            Assert.Equal(
                Wanted.MinBounty * 3 * NCG,
                nextState.GetBalance(Addresses.BountyBoard, NCG)
            );
            bountyBoard = nextState.GetBountyBoard(1);
            Assert.NotNull(bountyBoard);
            Assert.Equal(2, bountyBoard.Investors.Count);
            investor = bountyBoard.Investors.First(i => i.AvatarAddress == AvatarAddress);
            Assert.Equal(Wanted.MinBounty * 2 * NCG, investor.Price);
            Assert.Equal(2, investor.Count);
        }

        [Fact]
        public void CreateNewSeason()
        {
            var state = Stake(_initialState);
            // Validate no prev. season
            var latestSeasonInfo = state.GetLatestAdventureBossSeason();
            Assert.Equal(0, latestSeasonInfo.SeasonId);
            Assert.Equal(0, latestSeasonInfo.StartBlockIndex);
            Assert.Equal(0, latestSeasonInfo.EndBlockIndex);
            Assert.Equal(0, latestSeasonInfo.NextStartBlockIndex);

            var action = new Wanted
            {
                Season = 1,
                AvatarAddress = AvatarAddress,
                Bounty = Wanted.MinBounty * NCG,
            };
            var nextState = action.Execute(new ActionContext
            {
                PreviousState = state,
                Signer = AgentAddress,
                BlockIndex = 0L,
            });

            // Validate new season
            latestSeasonInfo = nextState.GetLatestAdventureBossSeason();
            Assert.Equal(1, latestSeasonInfo.SeasonId);
            Assert.Equal(0L, latestSeasonInfo.StartBlockIndex);
            Assert.Equal(SeasonInfo.BossActiveBlockInterval, latestSeasonInfo.EndBlockIndex);
            Assert.Equal(
                SeasonInfo.BossActiveBlockInterval + SeasonInfo.BossInactiveBlockInterval,
                latestSeasonInfo.NextStartBlockIndex
            );

            var season1 = nextState.GetSeasonInfo(1);
            Assert.Equal(latestSeasonInfo.SeasonId, season1.Season);
            Assert.Equal(latestSeasonInfo.StartBlockIndex, season1.StartBlockIndex);
            Assert.Equal(latestSeasonInfo.EndBlockIndex, season1.EndBlockIndex);
            Assert.Equal(latestSeasonInfo.NextStartBlockIndex, season1.NextStartBlockIndex);
        }

        [Fact]
        public void SeasonAlreadyInProgress()
        {
            var state = Stake(_initialState);
            // Set active season
            var seasonInfo = new SeasonInfo(1, 0L);
            state = state.SetSeasonInfo(seasonInfo);
            state = state.SetLatestAdventureBossSeason(seasonInfo);
            var latestSeasonInfo = state.GetLatestAdventureBossSeason();
            Assert.Equal(1, latestSeasonInfo.SeasonId);
            Assert.Equal(0L, latestSeasonInfo.StartBlockIndex);
            Assert.Equal(SeasonInfo.BossActiveBlockInterval, latestSeasonInfo.EndBlockIndex);
            Assert.Equal(
                SeasonInfo.BossActiveBlockInterval + SeasonInfo.BossInactiveBlockInterval,
                latestSeasonInfo.NextStartBlockIndex
            );

            // Try to create new season within season 1
            var action = new Wanted
            {
                Season = 2,
                AvatarAddress = AvatarAddress,
                Bounty = Wanted.MinBounty * NCG,
            };
            Assert.Throws<InvalidAdventureBossSeasonException>(() => action.Execute(
                new ActionContext
                {
                    PreviousState = state,
                    Signer = AgentAddress,
                    BlockIndex = 100L,
                }
            ));
        }

        [Fact]
        public void CannotCreateSeason0()
        {
            var state = Stake(_initialState);
            var action = new Wanted
            {
                Season = 0,
                AvatarAddress = AvatarAddress,
                Bounty = Wanted.MinBounty * NCG,
            };

            Assert.Throws<InvalidAdventureBossSeasonException>(() =>
                action.Execute(new ActionContext
                    {
                        PreviousState = state,
                        Signer = AgentAddress,
                        BlockIndex = 0L,
                    }
                )
            );
        }

        [Fact]
        public void InsufficientStaking()
        {
            var state = Stake(_initialState, 100);
            // Set active season
            var seasonInfo = new SeasonInfo(1, 0L);
            state = state.SetSeasonInfo(seasonInfo);
            state = state.SetLatestAdventureBossSeason(seasonInfo);

            // Try to create new season
            var action = new Wanted
            {
                Season = 1,
                AvatarAddress = AvatarAddress,
                Bounty = Wanted.MinBounty * NCG,
            };
            Assert.Throws<InsufficientStakingException>(() => action.Execute(
                new ActionContext
                {
                    PreviousState = state,
                    Signer = AgentAddress,
                    BlockIndex = 100L,
                }
            ));
        }

        [Fact]
        public void InsufficientBalance()
        {
            var state = Stake(_initialState);

            // Burn all balance to test
            state = state.BurnAsset(
                new ActionContext { PreviousState = state },
                AgentAddress,
                state.GetBalance(AgentAddress, NCG)
            );

            // Try to create new season
            var action = new Wanted
            {
                Season = 1,
                AvatarAddress = AvatarAddress,
                Bounty = Wanted.MinBounty * NCG,
            };
            Assert.Throws<InsufficientBalanceException>(() => action.Execute(
                new ActionContext
                {
                    PreviousState = state,
                    Signer = AgentAddress,
                    BlockIndex = 100L,
                }
            ));
        }

        [Theory]
        [InlineData(Wanted.MinBounty - 1)]
        [InlineData(Wanted.MaxBounty + 1)]
        public void InvalidBounty(int bounty)
        {
            var state = Stake(_initialState);
            // Try to create new season
            var action = new Wanted
            {
                Season = 1,
                AvatarAddress = AvatarAddress,
                Bounty = bounty * NCG,
            };
            Assert.Throws<InvalidBountyException>(() => action.Execute(
                new ActionContext
                {
                    PreviousState = state,
                    Signer = AgentAddress,
                    BlockIndex = 100L,
                }
            ));
        }

        private IWorld Stake(IWorld world, int amount = 0)
        {
            foreach (var (key, value) in Sheets)
            {
                world = world.SetLegacyState(Addresses.TableSheet.Derive(key), value.Serialize());
            }

            if (amount == 0)
            {
                var stakeSheet = world.GetSheet<MonsterCollectionSheet>();
                amount = stakeSheet.OrderedList.First(
                    row => row.Level == Wanted.RequiredStakingLevel
                ).RequiredGold;
            }

            var action = new Stake(new BigInteger(amount));
            return action.Execute(new ActionContext
            {
                PreviousState = world,
                Signer = AgentAddress,
                BlockIndex = 0L,
            });
        }
    }
}
