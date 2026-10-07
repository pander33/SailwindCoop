namespace SailwindCoop.Sync
{
    /// <summary>
    /// How a payout is divided between the players of a session. Everyone gets the same whole amount;
    /// what does not divide stays with the host, whose wallet the game paid.
    /// </summary>
    public static class WalletShare
    {
        /// <summary>One guest's part of <paramref name="total"/> among <paramref name="players"/> (host included).</summary>
        public static int Each(int total, int players)
        {
            return players <= 1 ? total : total / players;
        }

        /// <summary>What the host keeps after every guest took <see cref="Each"/>.</summary>
        public static int HostKeeps(int total, int players)
        {
            return players <= 1 ? total : total - Each(total, players) * (players - 1);
        }
    }
}
