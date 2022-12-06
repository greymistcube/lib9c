using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Libplanet;
using Libplanet.Action;
using Libplanet.Assets;
using Libplanet.Blockchain;
using Libplanet.Blockchain.Policies;
using Libplanet.Blocks;
using Libplanet.Consensus;
using Libplanet.Crypto;
using Libplanet.Tx;
using Nekoyume.Action;

namespace Nekoyume.BlockChain.Policy
{
    public class DebugPolicy : IBlockPolicy<PolymorphicAction<ActionBase>>
    {
        public DebugPolicy()
        {
        }

        public IAction BlockAction { get; } = new RewardGold();

        public TxPolicyViolationException ValidateNextBlockTx(
            BlockChain<PolymorphicAction<ActionBase>> blockChain,
            Transaction<PolymorphicAction<ActionBase>> transaction)
        {
            return null;
        }

        public BlockPolicyViolationException ValidateNextBlock(
            BlockChain<PolymorphicAction<ActionBase>> blockChain,
            Block<PolymorphicAction<ActionBase>> nextBlock)
        {
            return null;
        }

        public long GetMaxTransactionsBytes(long index) => long.MaxValue;

        public int GetMinTransactionsPerBlock(long index) => 0;

        public int GetMaxTransactionsPerBlock(long index) => int.MaxValue;

        public int GetMaxTransactionsPerSignerPerBlock(long index) => int.MaxValue;

        public IImmutableSet<Currency> NativeTokens => ImmutableHashSet<Currency>.Empty;

        public ValidatorSet GetValidatorSet(long index) => BlockPolicySource.DebugValidatorSet;
    }
}
