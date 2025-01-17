using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Bencodex.Types;
using Libplanet;
using Libplanet.Action;
using Libplanet.Blockchain;
using Libplanet.Blocks;
using Libplanet.Tx;
using Nekoyume.Action;
using Nekoyume.Model.State;
using static Libplanet.Blocks.BlockMarshaler;
using NCAction = Libplanet.Action.PolymorphicAction<Nekoyume.Action.ActionBase>;

namespace Nekoyume.BlockChain.Policy
{
    // Collection of helper methods not directly used as a pluggable component.
    public partial class BlockPolicySource
    {
        /// <summary>
        /// <para>
        /// Checks if <paramref name="transaction"/> includes any <see cref="IAction"/> that is
        /// obsolete according to <see cref="ActionObsoleteAttribute"/> attached.
        /// </para>
        /// <para>
        /// Due to a bug, an <see cref="IAction"/> is considered obsolete starting from
        /// <see cref="ActionObsoleteAttribute.ObsoleteIndex"/> + 2.
        /// </para>
        /// </summary>
        /// <param name="transaction">The <see cref="Transaction{T}"/> to consider.</param>
        /// <param name="actionTypeLoader">The loader to use <see cref="IAction"/>s included
        /// in <paramref name="transaction"/>.</param>
        /// <param name="blockIndex">Either the index of a prospective block to include
        /// <paramref name="transaction"/> or the index of a <see cref="Block{T}"/> containing
        /// <paramref name="transaction"/>.</param>
        /// <returns><see langword="true"/> if <paramref name="transaction"/> includes any
        /// <see cref="IAction"/> that is obsolete, <see langword="false"/> otherwise.</returns>
        /// <seealso cref="ActionObsoleteAttribute"/>
        internal static bool IsObsolete(
            ITransaction transaction,
            IActionTypeLoader actionTypeLoader,
            long blockIndex
        )
        {
            if (!(transaction.Actions is { } customActions))
            {
                return false;
            }

            var types = actionTypeLoader.Load(new ActionTypeLoaderContext(blockIndex));

            // Comparison with ObsoleteIndex + 2 is intended to have backward
            // compatibility with a bugged original implementation.
            return customActions.Any(
                ca => ca is Dictionary dictionary
                    && dictionary.TryGetValue((Text)"type_id", out IValue typeIdValue)
                    && typeIdValue is Text typeId
                    && types.TryGetValue(typeId, out Type actionType)
                    && actionType.IsDefined(typeof(ActionObsoleteAttribute), false)
                    && actionType.GetCustomAttributes()
                        .OfType<ActionObsoleteAttribute>()
                        .FirstOrDefault()?.ObsoleteIndex + 2 <= blockIndex
            );
        }

        internal static bool IsAdminTransaction(
            BlockChain<NCAction> blockChain, Transaction transaction)
        {
            return GetAdminState(blockChain) is AdminState admin
                && admin.AdminAddress.Equals(transaction.Signer);
        }

        internal static AdminState GetAdminState(
            BlockChain<NCAction> blockChain)
        {
            try
            {
                return blockChain.GetState(AdminState.Address) is Dictionary rawAdmin
                    ? new AdminState(rawAdmin)
                    : null;
            }
            catch (IncompleteBlockStatesException)
            {
                return null;
            }
        }

        private static InvalidBlockBytesLengthException ValidateTransactionsBytesRaw(
            Block block,
            IVariableSubPolicy<long> maxTransactionsBytesPolicy)
        {
            long maxTransactionsBytes = maxTransactionsBytesPolicy.Getter(block.Index);
            long transactionsBytes = block.MarshalBlock().EncodingLength;

            if (transactionsBytes > maxTransactionsBytes)
            {
                return new InvalidBlockBytesLengthException(
                    $"The size of block #{block.Index} {block.Hash} is too large where " +
                    $"the maximum number of bytes allowed for transactions is " +
                    $"{maxTransactionsBytes}: {transactionsBytes}",
                    transactionsBytes);
            }

            return null;
        }

        private static BlockPolicyViolationException ValidateTxCountPerBlockRaw(
            Block block,
            IVariableSubPolicy<int> minTransactionsPerBlockPolicy,
            IVariableSubPolicy<int> maxTransactionsPerBlockPolicy)
        {
            int minTransactionsPerBlock =
                minTransactionsPerBlockPolicy.Getter(block.Index);
            int maxTransactionsPerBlock =
                maxTransactionsPerBlockPolicy.Getter(block.Index);

            if (block.Transactions.Count < minTransactionsPerBlock)
            {
                return new InvalidBlockTxCountException(
                    $"Block #{block.Index} {block.Hash} should include " +
                    $"at least {minTransactionsPerBlock} transaction(s): " +
                    $"{block.Transactions.Count}",
                    block.Transactions.Count);
            }
            else if (block.Transactions.Count > maxTransactionsPerBlock)
            {
                return new InvalidBlockTxCountException(
                    $"Block #{block.Index} {block.Hash} should include " +
                    $"at most {maxTransactionsPerBlock} transaction(s): " +
                    $"{block.Transactions.Count}",
                    block.Transactions.Count);
            }

            return null;
        }

        private static BlockPolicyViolationException ValidateTxCountPerSignerPerBlockRaw(
            Block block,
            IVariableSubPolicy<int> maxTransactionsPerSignerPerBlockPolicy)
        {
            int maxTransactionsPerSignerPerBlock =
                maxTransactionsPerSignerPerBlockPolicy.Getter(block.Index);
            var groups = block.Transactions
                .GroupBy(tx => tx.Signer)
                .Where(group => group.Count() > maxTransactionsPerSignerPerBlock);
            var offendingGroup = groups.FirstOrDefault();

            if (!(offendingGroup is null))
            {
                int offendingGroupCount = offendingGroup.Count();
                return new InvalidBlockTxCountPerSignerException(
                    $"Block #{block.Index} {block.Hash} includes too many " +
                    $"transactions from signer {offendingGroup.Key} where " +
                    $"the maximum number of transactions allowed by a single signer " +
                    $"per block is {maxTransactionsPerSignerPerBlock}: " +
                    $"{offendingGroupCount}",
                    offendingGroup.Key,
                    offendingGroupCount);
            }

            return null;
        }
    }
}
