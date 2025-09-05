using log4net;
using Mono.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace nntpAutoposter
{
    class DBHandler
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly Lazy<DBHandler> _lazyInstance = new Lazy<DBHandler>(() => new DBHandler());
        public static DBHandler Instance => _lazyInstance.Value;
        private static readonly object _lock = new object();
        private readonly string _connectionString;

        private DBHandler()
        {
            _connectionString = BuildConnectionString();
            InitializeDatabase();
        }

        private static string BuildConnectionString()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dbFilePath = Path.Combine(baseDir, "nntpAutoPoster.Sqlite3.db");
            return $"URI=file:{dbFilePath},version=3";
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities")]
        private void InitializeDatabase()
        {
            var scripts = LoadDbScripts();
            if (scripts.Count == 0)
            {
                log.Debug("No DB scripts found; leaving database as-is.");
                return;
            }

            long highestScriptVersion = (long)Math.Floor(scripts[0].ScriptNumber);

            lock (_lock)
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();

                    long dbVersion;
                    using (var versionCmd = conn.CreateCommand())
                    {
                        versionCmd.CommandText = "PRAGMA user_version";
                        dbVersion = Convert.ToInt64(versionCmd.ExecuteScalar());
                        log.DebugFormat("Database version {0}", dbVersion);
                    }

                    if (dbVersion >= highestScriptVersion) return;

                    log.DebugFormat("Updating database to version {0}", highestScriptVersion);

                    using (var trans = conn.BeginTransaction())
                    using (var ddlCmd = conn.CreateCommand())
                    {
                        ddlCmd.Transaction = trans;

                        for (int i = scripts.Count - 1; i >= 0; i--)
                        {
                            var s = scripts[i];
                            if (s.ScriptNumber >= dbVersion + 1)
                            {
                                ddlCmd.CommandText = s.DdlStatement;
                                ddlCmd.Parameters.Clear();
                                ddlCmd.ExecuteNonQuery();
                            }
                        }

                        ddlCmd.CommandText = "PRAGMA user_version = " + highestScriptVersion;
                        ddlCmd.Parameters.Clear();
                        ddlCmd.ExecuteNonQuery();

                        trans.Commit();
                    }
                }
        }

        private List<DBScript> LoadDbScripts()
        {
            var scripts = new List<DBScript>(8);
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var scriptFolder = new DirectoryInfo(Path.Combine(baseDir, "dbScripts"));

            if (!scriptFolder.Exists) return scripts;

            foreach (var scriptFile in scriptFolder.EnumerateFiles("*.sql", SearchOption.TopDirectoryOnly))
            {
                decimal scriptNumber;
                if (!decimal.TryParse(Path.GetFileNameWithoutExtension(scriptFile.Name),
                                      NumberStyles.AllowDecimalPoint,
                                      CultureInfo.InvariantCulture,
                                      out scriptNumber))
                {
                    continue;
                }

                string ddl;
                using (var reader = scriptFile.OpenText())
                {
                    ddl = reader.ReadToEnd();
                }

                scripts.Add(new DBScript
                {
                    ScriptNumber = scriptNumber,
                    DdlStatement = ddl
                });
            }

            scripts.Sort((a, b) => b.ScriptNumber.CompareTo(a.ScriptNumber));
            return scripts;
        }

        public void CleanUploadEntries(int keepDays)
        {
            DateTime cutOffDateTime = DateTime.Now.AddDays(-keepDays);

            lock (_lock)
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"DELETE FROM UploadEntries 
                                        WHERE CreatedAt <= @cutOffDateTime";
                        cmd.Parameters.Add(new SqliteParameter("@cutOffDateTime", GetDbValue(cutOffDateTime)));
                        int deleted = cmd.ExecuteNonQuery();
                        log.InfoFormat("Cleaned {0} old entries from the database.", deleted);
                    }
                }
        }

        internal void Vacuum()
        {
            lock (_lock)
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "VACUUM";
                        cmd.ExecuteNonQuery();
                        log.Info("Vacuumed the database.");
                    }
                }
        }

        public UploadEntry GetNextUploadEntryToUpload()
        {
            lock (_lock)
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"SELECT * FROM UploadEntries 
                                        WHERE UploadedAt IS NULL 
                                          AND Cancelled = 0
                                        ORDER BY PriorityNum DESC, CreatedAt ASC
                                        LIMIT 1";
                        using (var reader = cmd.ExecuteReader())
                        {
                            return reader.Read() ? GetUploadEntryFromReader(reader) : null;
                        }
                    }
                }
        }

        public List<UploadEntry> GetUploadEntriesToNotifyIndexer()
        {
            var list = new List<UploadEntry>(16);
            lock (_lock)
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"SELECT * FROM UploadEntries 
                                        WHERE ObscuredName IS NOT NULL 
                                          AND NotifiedIndexerAt IS NULL
                                          AND UploadedAt IS NOT NULL
                                          AND Cancelled = 0
                                        ORDER BY CreatedAt ASC";
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                                list.Add(GetUploadEntryFromReader(reader));
                        }
                    }
                }
            return list;
        }

        public List<UploadEntry> GetUploadEntriesToVerify()
        {
            var list = new List<UploadEntry>(16);
            lock (_lock)
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"SELECT * FROM UploadEntries 
                                        WHERE UploadedAt IS NOT NULL
                                          AND SeenOnIndexerAt IS NULL
                                          AND Cancelled = 0
                                          AND (
                                            ObscuredName IS NULL
                                            OR 
                                            (ObscuredName IS NOT NULL AND NotifiedIndexerAt IS NOT NULL)
                                          )
                                        ORDER BY CreatedAt ASC";
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                                list.Add(GetUploadEntryFromReader(reader));
                        }
                    }
                }
            return list;
        }

        public UploadEntry GetActiveUploadEntry(string name)
        {
            lock (_lock)
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"SELECT * FROM UploadEntries 
                                        WHERE Name = @name 
                                          AND Cancelled = 0";
                        cmd.Parameters.Add(new SqliteParameter("@name", name));
                        using (var reader = cmd.ExecuteReader())
                        {
                            UploadEntry entry = null;
                            if (reader.Read())
                            {
                                entry = GetUploadEntryFromReader(reader);
                                if (reader.Read())
                                    throw new Exception("Got more than one result matching this name. The database is not consistent.");
                            }
                            return entry;
                        }
                    }
                }
        }

        public void AddNewUploadEntry(UploadEntry uploadEntry)
        {
            if (uploadEntry == null) throw new ArgumentNullException(nameof(uploadEntry));

            lock (_lock)
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var trans = conn.BeginTransaction())
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = trans;

                        cmd.CommandText = @"UPDATE UploadEntries 
                                        SET Cancelled = 1 
                                        WHERE Name = @name AND Cancelled = 0";
                        cmd.Parameters.Add(new SqliteParameter("@name", uploadEntry.Name));
                        int cancelled = cmd.ExecuteNonQuery();
                        if (cancelled > 0)
                            log.InfoFormat("{0} upload entries were cancelled by a re-add of an existing upload.", cancelled);

                        cmd.Parameters.Clear();
                        cmd.CommandText = @"INSERT INTO UploadEntries(
                                            Name, 
                                            Size,
                                            CleanedName,
                                            ObscuredName,
                                            RemoveAfterVerify, 
                                            CreatedAt,
                                            UploadedAt,
                                            NotifiedIndexerAt,
                                            SeenOnIndexerAt,
                                            Cancelled,
                                            WatchFolderShortName,
                                            UploadAttempts,
                                            RarPassword,
                                            PriorityNum,
                                            NzbContents,
                                            IsRepost,
                                            NotificationCount,
                                            CurrentLocation,
                                            HasNfo)
                                        VALUES(
                                            @name,
                                            @size,
                                            @cleanedName,
                                            @obscuredName,
                                            @removeAfterVerify,
                                            @createdAt, 
                                            @uploadedAt,
                                            @notifiedIndexerAt,
                                            @seenOnIndexerAt,
                                            @cancelled,
                                            @watchFolderShortName,
                                            @uploadAttempts,
                                            @rarPassword,
                                            @priorityNum,
                                            @nzbContents,
                                            @isRepost,
                                            @notificationCount,
                                            @currentLocation,
                                            @hasNfo)";
                        cmd.Parameters.Add(new SqliteParameter("@name", uploadEntry.Name));
                        cmd.Parameters.Add(new SqliteParameter("@size", uploadEntry.Size));
                        cmd.Parameters.Add(new SqliteParameter("@cleanedName", uploadEntry.CleanedName));
                        cmd.Parameters.Add(new SqliteParameter("@obscuredName", uploadEntry.ObscuredName));
                        cmd.Parameters.Add(new SqliteParameter("@removeAfterVerify", GetDbValue(uploadEntry.RemoveAfterVerify)));
                        cmd.Parameters.Add(new SqliteParameter("@createdAt", GetDbValue(uploadEntry.CreatedAt)));
                        cmd.Parameters.Add(new SqliteParameter("@uploadedAt", GetDbValue(uploadEntry.UploadedAt)));
                        cmd.Parameters.Add(new SqliteParameter("@notifiedIndexerAt", GetDbValue(uploadEntry.NotifiedIndexerAt)));
                        cmd.Parameters.Add(new SqliteParameter("@seenOnIndexerAt", GetDbValue(uploadEntry.SeenOnIndexAt)));
                        cmd.Parameters.Add(new SqliteParameter("@cancelled", GetDbValue(uploadEntry.Cancelled)));
                        cmd.Parameters.Add(new SqliteParameter("@watchFolderShortName", uploadEntry.WatchFolderShortName));
                        cmd.Parameters.Add(new SqliteParameter("@uploadAttempts", uploadEntry.UploadAttempts));
                        cmd.Parameters.Add(new SqliteParameter("@rarPassword", uploadEntry.RarPassword));
                        cmd.Parameters.Add(new SqliteParameter("@priorityNum", uploadEntry.PriorityNum));
                        cmd.Parameters.Add(new SqliteParameter("@nzbContents", uploadEntry.NzbContents));
                        cmd.Parameters.Add(new SqliteParameter("@isRepost", GetDbValue(uploadEntry.IsRepost)));
                        cmd.Parameters.Add(new SqliteParameter("@notificationCount", uploadEntry.NotificationCount));
                        cmd.Parameters.Add(new SqliteParameter("@currentLocation", GetDbValue(uploadEntry.CurrentLocation)));
                        cmd.Parameters.Add(new SqliteParameter("@hasNfo", GetDbValue(uploadEntry.HasNfo)));
                        cmd.ExecuteNonQuery();

                        cmd.Parameters.Clear();
                        cmd.CommandText = "SELECT last_insert_rowid()";
                        uploadEntry.ID = Convert.ToInt64(cmd.ExecuteScalar());

                        trans.Commit();
                    }
                }
        }

        public void UpdateUploadEntry(UploadEntry uploadEntry)
        {
            if (uploadEntry == null) throw new ArgumentNullException(nameof(uploadEntry));

            lock (_lock)
                using (var conn = new SqliteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"UPDATE UploadEntries SET 
                                            Name = @name,
                                            CleanedName = @cleanedName,
                                            ObscuredName = @obscuredName,
                                            RemoveAfterVerify = @removeAfterVerify,
                                            UploadedAt = @uploadedAt,
                                            NotifiedIndexerAt = @notifiedIndexerAt,
                                            SeenOnIndexerAt = @seenOnIndexerAt,
                                            Cancelled = @cancelled,
                                            WatchFolderShortName = @watchFolderShortName,
                                            UploadAttempts = @uploadAttempts,
                                            RarPassword = @rarPassword,
                                            PriorityNum = @priorityNum,
                                            NzbContents = @nzbContents,
                                            IsRepost = @isRepost,
                                            NotificationCount = @notificationCount,
                                            CurrentLocation = @currentLocation,
                                            HasNfo = @hasNfo
                                        WHERE RowIDAlias = @rowIDAlias";
                        cmd.Parameters.Add(new SqliteParameter("@name", uploadEntry.Name));
                        cmd.Parameters.Add(new SqliteParameter("@cleanedName", uploadEntry.CleanedName));
                        cmd.Parameters.Add(new SqliteParameter("@obscuredName", uploadEntry.ObscuredName));
                        cmd.Parameters.Add(new SqliteParameter("@removeAfterVerify", GetDbValue(uploadEntry.RemoveAfterVerify)));
                        cmd.Parameters.Add(new SqliteParameter("@uploadedAt", GetDbValue(uploadEntry.UploadedAt)));
                        cmd.Parameters.Add(new SqliteParameter("@notifiedIndexerAt", GetDbValue(uploadEntry.NotifiedIndexerAt)));
                        cmd.Parameters.Add(new SqliteParameter("@seenOnIndexerAt", GetDbValue(uploadEntry.SeenOnIndexAt)));
                        cmd.Parameters.Add(new SqliteParameter("@cancelled", GetDbValue(uploadEntry.Cancelled)));
                        cmd.Parameters.Add(new SqliteParameter("@watchFolderShortName", uploadEntry.WatchFolderShortName));
                        cmd.Parameters.Add(new SqliteParameter("@uploadAttempts", uploadEntry.UploadAttempts));
                        cmd.Parameters.Add(new SqliteParameter("@rarPassword", uploadEntry.RarPassword));
                        cmd.Parameters.Add(new SqliteParameter("@priorityNum", uploadEntry.PriorityNum));
                        cmd.Parameters.Add(new SqliteParameter("@nzbContents", uploadEntry.NzbContents));
                        cmd.Parameters.Add(new SqliteParameter("@isRepost", GetDbValue(uploadEntry.IsRepost)));
                        cmd.Parameters.Add(new SqliteParameter("@notificationCount", uploadEntry.NotificationCount));
                        cmd.Parameters.Add(new SqliteParameter("@currentLocation", GetDbValue(uploadEntry.CurrentLocation)));
                        cmd.Parameters.Add(new SqliteParameter("@hasNfo", GetDbValue(uploadEntry.HasNfo)));
                        cmd.Parameters.Add(new SqliteParameter("@rowIDAlias", uploadEntry.ID));
                        cmd.ExecuteNonQuery();
                    }
                }
        }

        private static UploadEntry GetUploadEntryFromReader(SqliteDataReader reader)
        {
            var uploadEntry = new UploadEntry
            {
                ID = Convert.ToInt64(reader["RowIDAlias"]),
                Name = reader["Name"] as string,
                Size = Convert.ToInt64(reader["Size"]),
                CleanedName = reader["CleanedName"] as string,
                ObscuredName = reader["ObscuredName"] as string,
                RemoveAfterVerify = GetBoolean(reader["RemoveAfterVerify"]),
                CreatedAt = GetDateTime(reader["CreatedAt"]),
                UploadedAt = GetNullableDateTime(reader["UploadedAt"]),
                NotifiedIndexerAt = GetNullableDateTime(reader["NotifiedIndexerAt"]),
                SeenOnIndexAt = GetNullableDateTime(reader["SeenOnIndexerAt"]),
                Cancelled = GetBoolean(reader["Cancelled"]),
                WatchFolderShortName = reader["WatchFolderShortName"] as string,
                UploadAttempts = Convert.ToInt64(reader["UploadAttempts"]),
                RarPassword = reader["RarPassword"] as string,
                PriorityNum = Convert.ToInt64(reader["PriorityNum"]),
                NzbContents = reader["NzbContents"] as string,
                IsRepost = GetBoolean(reader["IsRepost"]),
                NotificationCount = Convert.ToInt64(reader["NotificationCount"]),
                CurrentLocation = GetLocation(reader["CurrentLocation"]),
                HasNfo = GetBoolean(reader["HasNfo"])
            };

            return uploadEntry;
        }

        private static object GetDbValue(bool boolean) => boolean ? 1 : 0;

        private static object GetDbValue(DateTime? dateTime)
        {
            if (!dateTime.HasValue) return DBNull.Value;
            return dateTime.Value.ToString("o", CultureInfo.InvariantCulture);
        }

        private static object GetDbValue(Location currentLocation) => (long)currentLocation;

        private static bool GetBoolean(object dbValue) => Convert.ToInt64(dbValue) == 1;

        private static DateTime GetDateTime(object dbValue)
        {
            var s = dbValue as string;
            return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        private static DateTime? GetNullableDateTime(object dbValue)
        {
            var s = dbValue as string;
            if (s == null) return null;
            DateTime result;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result) ? (DateTime?)result : null;
        }

        private static Location GetLocation(object dbValue) => (Location)Convert.ToInt64(dbValue);
    }
}