using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.IO;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Serialization.Json.Converters;
using Database = SocketJack.Net.Database.Database;

namespace SocketJack.Net.Database {

    /// <summary>
    /// MSSQL Server Emulator implementing TDS (Tabular Data Stream) protocol.
    /// <para>
    /// Can run standalone (inherits <see cref="TcpServer"/>) or be registered
    /// as a protocol handler on <see cref="MutableTcpServer"/> via
    /// <see cref="TdsProtocolHandler"/>.
    /// </para>
    /// <para>
    /// All databases, tables and rows live in memory and are persisted to
    /// <see cref="DataPath"/> as JSON. Use <see cref="ImportFromMssql"/> to
    /// import schema and data from an existing SQL Server instance.
    /// </para>
    /// </summary>
    public class DataServer : TcpServer {

        #region Properties

        /// <summary>
        /// Server instance name (e.g., "MSSQLSERVER")
        /// </summary>
        public string InstanceName { get; set; } = "MSSQLSERVER";

        /// <summary>
        /// Server version to report to clients (e.g., "17.00.4025" for SQL Server 2025)
        /// </summary>
        public string ServerVersion { get; set; } = "17.00.4025.3";

        /// <summary>
        /// Default username for SQL authentication
        /// </summary>
        public string Username { get; set; } = "sa";

        /// <summary>
        /// Default password for SQL authentication
        /// </summary>
        public string Password { get; set; } = "";

        /// <summary>
        /// Default database name
        /// </summary>
        public string DefaultDatabase { get; set; } = "db";

        /// <summary>
        /// Server name to report to clients
        /// </summary>
        public string ServerName { get; set; } = Environment.MachineName;

        /// <summary>
        /// Enable Windows authentication (currently defaults to SQL authentication)
        /// </summary>
        public bool AllowWindowsAuth { get; set; } = false;

        /// <summary>
        /// Enable SQL Server authentication
        /// </summary>
        public bool AllowSqlAuth { get; set; } = true;

        /// <summary>
        /// TDS encryption mode negotiated during Pre-Login.
        /// <list type="bullet">
        /// <item><c>0x00</c> – <b>ENCRYPT_OFF</b>: Encrypt the login packet only, then revert to plaintext.</item>
        /// <item><c>0x01</c> – <b>ENCRYPT_ON</b>: Encrypt all traffic after the TLS handshake.</item>
        /// <item><c>0x02</c> – <b>ENCRYPT_NOT_SUP</b>: No encryption at all (clients requiring encryption will refuse).</item>
        /// <item><c>0x03</c> – <b>ENCRYPT_REQ</b>: Server requires full encryption.</item>
        /// </list>
        /// </summary>
        public byte EncryptionMode { get; set; } = 0x00; // ENCRYPT_OFF (login-only TLS, then plaintext)

        /// <summary>
        /// X.509 certificate used for TDS encryption (TLS handshake).
        /// When <see langword="null"/> and <see cref="EncryptionMode"/> is not <c>0x02</c>,
        /// a self-signed certificate is generated automatically on first use.
        /// </summary>
        public X509Certificate2 Certificate {
            get {
                if (_certificate == null && EncryptionMode != 0x02)
                    _certificate = GenerateSelfSignedCertificate();
                return _certificate;
            }
            set { _certificate = value; }
        }
        private X509Certificate2 _certificate;

        /// <summary>
        /// Generates a self-signed X.509 certificate suitable for TDS encryption.
        /// The certificate is valid for 10 years and uses a 2048-bit RSA key.
        /// </summary>
        public static X509Certificate2 GenerateSelfSignedCertificate(string subjectName = null) {
            subjectName = subjectName ?? "CN=" + Environment.MachineName;
            using (var rsa = RSA.Create(2048)) {
                var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // Server Authentication
                var sanBuilder = new SubjectAlternativeNameBuilder();
                sanBuilder.AddDnsName(Environment.MachineName);
                sanBuilder.AddDnsName("localhost");
                request.CertificateExtensions.Add(sanBuilder.Build());
                var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(10));
                // Export and re-import so the private key is stored in a way
                // compatible with SslStream on all platforms.
                // Use MachineKeySet (not EphemeralKeySet) for .NET Framework compatibility.
                return new X509Certificate2(
                    cert.Export(X509ContentType.Pfx),
                    (string)null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet
                );
            }
        }

        /// <summary>
        /// Collection of registered users with their passwords
        /// </summary>
        public ConcurrentDictionary<string, string> Users { get; set; } = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Databases available on this server
        /// </summary>
        public ConcurrentDictionary<string, Database> Databases { get; set; } = new ConcurrentDictionary<string, Database>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Active sessions mapped by connection ID
        /// </summary>
        internal ConcurrentDictionary<Guid, SqlSession> Sessions = new ConcurrentDictionary<Guid, SqlSession>();

        /// <summary>
        /// File path used to persist database state.
        /// When set, data is loaded from this file on startup and saved on every mutation.
        /// </summary>
        public string DataPath { get; set; } = "dataserver.json";

        /// <summary>
        /// When <see langword="true"/>, every data mutation (table/row add/remove) is
        /// automatically flushed to <see cref="DataPath"/>.
        /// </summary>
        public bool AutoSave { get; set; } = true;

        /// <summary>
        /// Interval in milliseconds to debounce auto-save writes.
        /// Only the last mutation within this window triggers a write.
        /// </summary>
        public int AutoSaveDebounceMs { get; set; } = 500;

        private readonly object _saveLock = new object();
        private Timer _debounceTimer;

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new TypeConverter() }
        };

        #endregion

        #region Events

        public event QueryExecutingEventHandler QueryExecuting;
        public delegate void QueryExecutingEventHandler(SqlSession session, string query, ref QueryResult result);

        public event AuthenticationEventHandler Authentication;
        public delegate void AuthenticationEventHandler(string username, string password, ref bool authenticated);

        /// <summary>
        /// Raised when a TDS client connects (standalone mode only).
        /// </summary>
        public event Action<SqlSession> TdsClientConnected;

        /// <summary>
        /// Raised when a TDS client disconnects (standalone mode only).
        /// </summary>
        public event Action<SqlSession> TdsClientDisconnected;

        #endregion

        #region Constructors

        /// <summary>
        /// Initialize a new MSSQL Server Emulator (standalone mode — owns the TCP listener).
        /// </summary>
        /// <param name="Port">Port to listen on (default MSSQL port is 1433)</param>
        /// <param name="Name">Server name for logging</param>
        public DataServer(int Port = 1433, string Name = "DataServer") : base(Port, Name) {
            InitDefaults();

            // Hook into client events for standalone mode
            ClientConnected += DataServer_ClientConnected;
            ClientDisconnected += DataServer_ClientDisconnected;
        }

        /// <summary>
        /// Creates a <see cref="DataServer"/> in hosted mode (no TCP listener).
        /// <para>
        /// The <see cref="MutableTcpServer"/> owns the listener; this instance
        /// only provides the in-memory database engine, persistence, and import
        /// capabilities. No port is bound, no standalone client event hooks are
        /// installed.
        /// </para>
        /// </summary>
        /// <param name="Name">Server name for logging.</param>
        /// <param name="hosted">Must be <see langword="true"/>. Distinguishes this from the standalone constructor.</param>
        internal DataServer(string Name, bool hosted) : base(0, Name) {
            InitDefaults();
        }

        private void InitDefaults() {
            RawTcpMode = true;
            SuppressConnectionTest = true;
            Users.TryAdd(Username, Password);

            // Ensure default databases exist
            Databases.TryAdd("master", new Database("master"));
            Databases.TryAdd("db", new Database("db"));

            // Load persisted state (merges into defaults)
            Load();

            // Sync Users dictionary into the db.Users table so every configured user is visible
            if (Databases.TryGetValue("db", out var configDb)) {
                var usersTable = new Table("Users") {
                    Columns = new List<Column> {
                        new Column("Username", typeof(string), 255),
                        new Column("Password", typeof(string), 255)
                    }
                };
                // Start from existing rows if the table was already persisted
                if (configDb.Tables.TryGetValue("Users", out var existingTable)) {
                    foreach (var row in existingTable.Rows) {
                        var name = row.Length > 0 ? row[0]?.ToString() : null;
                        if (name != null) usersTable.Rows.Add(row);
                    }
                }
                // Add any users from the dictionary that aren't already in the table
                foreach (var kvp in Users) {
                    bool found = false;
                    for (int i = 0; i < usersTable.Rows.Count; i++) {
                        if (string.Equals(usersTable.Rows[i][0]?.ToString(), kvp.Key, StringComparison.OrdinalIgnoreCase)) {
                            usersTable.Rows[i] = new object[] { kvp.Key, kvp.Value };
                            found = true;
                            break;
                        }
                    }
                    if (!found) usersTable.Rows.Add(new object[] { kvp.Key, kvp.Value });
                }
                configDb.Tables["Users"] = usersTable;
            }
        }

        #endregion

        #region Event Handlers (Standalone Mode)

        private void DataServer_ClientConnected(ConnectedEventArgs e) {
            e.Connection._Protocol = TcpProtocol.Tds;
            e.Connection.SuppressConnectionTest = true;
            var session = new SqlSession {
                ConnectionId = e.Connection.ID,
                CurrentDatabase = DefaultDatabase,
                ServerName = ServerName,
                ServerVersion = ServerVersion
            };

            Sessions.TryAdd(e.Connection.ID, session);
            TdsClientConnected?.Invoke(session);

            // Start handling TDS protocol
            Task.Run(() => TdsProtocolHandler.RunTdsLoop(this, e.Connection, session));
        }

        private void DataServer_ClientDisconnected(DisconnectedEventArgs e) {
            if (Sessions.TryRemove(e.Connection.ID, out var session))
                TdsClientDisconnected?.Invoke(session);
        }

        #endregion

        #region Internal Auth / Query Helpers

        internal bool Authenticate(string username, string password) {
            bool authenticated = false;
            if (AllowSqlAuth) {
                if (Users.TryGetValue(username, out string storedPassword)) {
                    authenticated = (password == storedPassword);
                }
            }
            Authentication?.Invoke(username, password, ref authenticated);
            return authenticated;
        }

        internal QueryResult ExecuteQuery(SqlSession session, string query) {
            var result = new QueryResult();
            QueryExecuting?.Invoke(session, query, ref result);
            return result;
        }

        #endregion

        #region Persistence

        /// <summary>
        /// Saves the current server state (users, databases, tables, rows) to <see cref="DataPath"/>.
        /// </summary>
        public void Save() {
            if (string.IsNullOrWhiteSpace(DataPath)) return;
            try {
                var snapshot = new DataServerSnapshot {
                    Users = new Dictionary<string, string>(Users, StringComparer.OrdinalIgnoreCase),
                    Databases = new Dictionary<string, DatabaseSnapshot>(StringComparer.OrdinalIgnoreCase)
                };

                foreach (var kvp in Databases) {
                    var dbSnap = new DatabaseSnapshot { Name = kvp.Value.Name };
                    foreach (var tkvp in kvp.Value.Tables) {
                        var tblSnap = new TableSnapshot {
                            Name = tkvp.Value.Name,
                            Columns = tkvp.Value.Columns.Select(c => new ColumnSnapshot {
                                Name = c.Name,
                                DataTypeName = c.DataType?.FullName ?? typeof(string).FullName,
                                MaxLength = c.MaxLength
                            }).ToList(),
                            Rows = tkvp.Value.Rows.Select(r =>
                                r.Select(v => v?.ToString()).ToArray()
                            ).ToList()
                        };
                        dbSnap.Tables[tkvp.Key] = tblSnap;
                    }
                    snapshot.Databases[kvp.Key] = dbSnap;
                }

                string json = System.Text.Json.JsonSerializer.Serialize(snapshot, _jsonOptions);
                lock (_saveLock) {
                    string dir = Path.GetDirectoryName(Path.GetFullPath(DataPath));
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(DataPath, json, Encoding.UTF8);
                }
                LogFormat("[{0}] Data saved to {1}", new[] { Name, DataPath });
            } catch (Exception ex) {
                LogFormat("[{0}] Save failed: {1}", new[] { Name, ex.Message });
            }
        }

        /// <summary>
        /// Loads server state from <see cref="DataPath"/> into memory.
        /// </summary>
        public void Load() {
            if (string.IsNullOrWhiteSpace(DataPath)) return;
            if (!File.Exists(DataPath)) return;
            try {
                string json;
                lock (_saveLock) {
                    json = File.ReadAllText(DataPath, Encoding.UTF8);
                }

                var snapshot = System.Text.Json.JsonSerializer.Deserialize<DataServerSnapshot>(json, _jsonOptions);
                if (snapshot == null) return;

                if (snapshot.Users != null) {
                    foreach (var kvp in snapshot.Users)
                        Users[kvp.Key] = kvp.Value;
                }

                if (snapshot.Databases != null) {
                    foreach (var dbKvp in snapshot.Databases) {
                        var db = Databases.GetOrAdd(dbKvp.Key, _ => new Database(dbKvp.Value.Name));
                        if (dbKvp.Value.Tables != null) {
                            foreach (var tblKvp in dbKvp.Value.Tables) {
                                var table = new Table(tblKvp.Value.Name);
                                if (tblKvp.Value.Columns != null) {
                                    table.Columns = tblKvp.Value.Columns.Select(c => new Column(
                                        c.Name,
                                        Type.GetType(c.DataTypeName) ?? typeof(string),
                                        c.MaxLength
                                    )).ToList();
                                }
                                if (tblKvp.Value.Rows != null) {
                                    table.Rows = tblKvp.Value.Rows
                                        .Select(r => r.Cast<object>().ToArray())
                                        .ToList();
                                }
                                db.Tables[tblKvp.Key] = table;
                            }
                        }
                    }
                }

                LogFormat("[{0}] Data loaded from {1}", new[] { Name, DataPath });
            } catch (Exception ex) {
                LogFormat("[{0}] Load failed: {1}", new[] { Name, ex.Message });
            }
        }

        /// <summary>
        /// Schedules a debounced save. Multiple rapid calls within
        /// <see cref="AutoSaveDebounceMs"/> collapse into a single write.
        /// </summary>
        public void ScheduleSave() {
            if (!AutoSave || string.IsNullOrWhiteSpace(DataPath)) return;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => Save(), null, AutoSaveDebounceMs, Timeout.Infinite);
        }

        protected override void Dispose(bool disposing) {
            _debounceTimer?.Dispose();
            if (AutoSave) Save();
            base.Dispose(disposing);
        }

        #endregion

        #region Import From MSSQL

        /// <summary>
        /// Imports schema and data from an existing MSSQL database into this
        /// DataServer's in-memory store.
        /// <para>
        /// Pass any <see cref="DbConnection"/> (e.g. <c>Microsoft.Data.SqlClient.SqlConnection</c>).
        /// The connection should already be opened or the method will open it.
        /// </para>
        /// <example>
        /// <code>
        /// // Install Microsoft.Data.SqlClient in your application project:
        /// //   dotnet add package Microsoft.Data.SqlClient
        ///
        /// using Microsoft.Data.SqlClient;
        ///
        /// var conn = new SqlConnection("Server=.;Database=MyDb;Trusted_Connection=True;TrustServerCertificate=True;");
        /// server.ImportFromMssql(conn);
        /// // or import only specific tables:
        /// server.ImportFromMssql(conn, tableFilter: new[] { "Users", "Orders" });
        /// </code>
        /// </example>
        /// </summary>
        /// <param name="connection">An open (or openable) <see cref="DbConnection"/> to the source MSSQL server.</param>
        /// <param name="databaseName">
        /// Override the target database name in the DataServer. When <see langword="null"/>,
        /// the source connection's <see cref="DbConnection.Database"/> name is used.
        /// </param>
        /// <param name="tableFilter">
        /// Optional list of table names to import. When <see langword="null"/> or empty,
        /// all user tables are imported.
        /// </param>
        /// <param name="importData">
        /// When <see langword="true"/> (default), all rows are imported.
        /// Set to <see langword="false"/> to import only schema (columns).
        /// </param>
        /// <param name="maxRowsPerTable">
        /// Maximum number of rows to import per table. 0 = unlimited.
        /// </param>
        public void ImportFromMssql(
            DbConnection connection,
            string databaseName = null,
            string[] tableFilter = null,
            bool importData = true,
            int maxRowsPerTable = 0) {

            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            if (connection.State != ConnectionState.Open)
                connection.Open();

            string dbName = databaseName ?? connection.Database ?? "Imported";
            var db = Databases.GetOrAdd(dbName, _ => new Database(dbName));

            LogFormat("[{0}] Starting import from MSSQL database '{1}'...", new[] { Name, dbName });

            // --- Discover tables ---
            var tableNames = new List<string>();
            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = @"
                    SELECT TABLE_SCHEMA, TABLE_NAME
                    FROM INFORMATION_SCHEMA.TABLES
                    WHERE TABLE_TYPE = 'BASE TABLE'
                    ORDER BY TABLE_SCHEMA, TABLE_NAME";

                using (var reader = cmd.ExecuteReader()) {
                    while (reader.Read()) {
                        string schema = reader.GetString(0);
                        string tableName = reader.GetString(1);
                        string fullName = schema + "." + tableName;

                        if (tableFilter != null && tableFilter.Length > 0) {
                            bool match = tableFilter.Any(f =>
                                string.Equals(f, tableName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(f, fullName, StringComparison.OrdinalIgnoreCase));
                            if (!match) continue;
                        }

                        tableNames.Add(fullName);
                    }
                }
            }

            int totalTables = tableNames.Count;
            int imported = 0;

            foreach (string fullTableName in tableNames) {
                imported++;
                var table = new Table(fullTableName);

                // --- Import columns ---
                string[] parts = fullTableName.Split('.');
                string schemaName = parts[0];
                string tblName = parts.Length > 1 ? parts[1] : parts[0];

                using (var cmd = connection.CreateCommand()) {
                    cmd.CommandText = string.Format(@"
                        SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH
                        FROM INFORMATION_SCHEMA.COLUMNS
                        WHERE TABLE_SCHEMA = '{0}' AND TABLE_NAME = '{1}'
                        ORDER BY ORDINAL_POSITION",
                        schemaName.Replace("'", "''"),
                        tblName.Replace("'", "''"));

                    using (var reader = cmd.ExecuteReader()) {
                        while (reader.Read()) {
                            string colName = reader.GetString(0);
                            string dataType = reader.GetString(1);
                            int maxLen = reader.IsDBNull(2) ? -1 : Convert.ToInt32(reader.GetValue(2));
                            table.Columns.Add(new Column(colName, MapSqlType(dataType), maxLen));
                        }
                    }
                }

                // --- Import rows ---
                if (importData) {
                    using (var cmd = connection.CreateCommand()) {
                        string quotedName = "[" + schemaName + "].[" + tblName + "]";
                        cmd.CommandText = maxRowsPerTable > 0
                            ? string.Format("SELECT TOP {0} * FROM {1}", maxRowsPerTable, quotedName)
                            : string.Format("SELECT * FROM {0}", quotedName);

                        try {
                            using (var reader = cmd.ExecuteReader()) {
                                int fieldCount = reader.FieldCount;
                                while (reader.Read()) {
                                    var row = new object[fieldCount];
                                    for (int i = 0; i < fieldCount; i++) {
                                        row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                                    }
                                    table.Rows.Add(row);
                                }
                            }
                        } catch (Exception ex) {
                            LogFormat("[{0}] Warning: could not read data from {1}: {2}",
                                new[] { Name, fullTableName, ex.Message });
                        }
                    }
                }

                db.Tables[fullTableName] = table;
                LogFormat("[{0}] Imported table {1}/{2}: {3} ({4} columns, {5} rows)",
                    new[] { Name, imported.ToString(), totalTables.ToString(),
                            fullTableName, table.Columns.Count.ToString(), table.Rows.Count.ToString() });
            }

            LogFormat("[{0}] Import complete. {1} tables imported into database '{2}'.",
                new[] { Name, totalTables.ToString(), dbName });

            // Auto-save after import
            if (AutoSave) ScheduleSave();
        }

        /// <summary>
        /// Maps a SQL Server data type name to a .NET <see cref="Type"/>.
        /// </summary>
        private static Type MapSqlType(string sqlType) {
            switch ((sqlType ?? "").ToLowerInvariant()) {
                case "bit": return typeof(bool);
                case "tinyint": return typeof(byte);
                case "smallint": return typeof(short);
                case "int": return typeof(int);
                case "bigint": return typeof(long);
                case "real": return typeof(float);
                case "float": return typeof(double);
                case "decimal":
                case "numeric":
                case "money":
                case "smallmoney": return typeof(decimal);
                case "date":
                case "datetime":
                case "datetime2":
                case "smalldatetime": return typeof(DateTime);
                case "datetimeoffset": return typeof(DateTimeOffset);
                case "time": return typeof(TimeSpan);
                case "uniqueidentifier": return typeof(Guid);
                case "binary":
                case "varbinary":
                case "image":
                case "timestamp":
                case "rowversion": return typeof(byte[]);
                default: return typeof(string);
            }
        }

        #endregion
    }
}
