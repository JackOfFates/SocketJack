namespace SocketJack.Net.Runescape {

    /// <summary>
    /// Holds the player credentials for a player.
    /// </summary>
    public sealed class PlayerCredentials {

        /// <summary>
        /// Gets or sets the encoded username.
        /// </summary>
        public long EncodedUsername { get; set; }

        /// <summary>
        /// Gets or sets the password.
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        /// Gets or sets the uid.
        /// </summary>
        public int Uid { get; set; }

        /// <summary>
        /// Gets or sets the username.
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// Gets or sets the username hash.
        /// </summary>
        public string UsernameHash { get; set; }

        /// <summary>
        /// Gets or sets the host address.
        /// </summary>
        public string HostAddress { get; set; }
    }
}
