using FluentAssertions;
using NINA.Core.Utility;
using NINA.Plugin.TargetScheduler.Database;
using NUnit.Framework;
using System.Data.SQLite;
using System.IO;

namespace NINA.Plugin.TargetScheduler.Test.Database {

    /// <summary>
    /// Tests for columns added at startup if missing (rather than by numbered migration script), including resetting the
    /// version of databases migrated by development builds that used (since removed) migrations 24 and 25.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class DatabaseColumnAdditionsTest {
        private const int LAST_MIGRATION = 23;

        [OneTimeSetUp]
        public void OneTimeSetUp() {
            DllLoader.LoadDll(Path.Combine("SQLite", "SQLite.Interop.dll"));
        }

        [Test]
        public void TestNewDatabase() {
            string path = NewDatabasePath("new");
            Initialize(path, 1);

            UserVersion(path).Should().Be(LAST_MIGRATION);
            ApplicationId(path).Should().Be(SchedulerDatabaseContext.COLUMN_ADDITIONS_MARKER);
            ColumnExists(path, "target", "prioritizeThrough").Should().BeTrue();
            ColumnExists(path, "exposureplan", "skipWhilePrioritized").Should().BeTrue();
        }

        [Test]
        public void TestResetFromRemovedMigrations() {
            // Simulate a database migrated by a development build: version 25, columns present, no marker
            string path = NewDatabasePath("removed");
            Initialize(path, 1);
            Execute(path, "PRAGMA user_version = 25");
            Execute(path, "PRAGMA application_id = 0");

            Initialize(path, 2);
            UserVersion(path).Should().Be(LAST_MIGRATION);
            ApplicationId(path).Should().Be(SchedulerDatabaseContext.COLUMN_ADDITIONS_MARKER);
            ColumnExists(path, "target", "prioritizeThrough").Should().BeTrue();
        }

        [Test]
        public void TestNoResetForUpstreamVersion() {
            // Simulate a database migrated to 24 by an upstream release that never had the added columns
            string path = NewDatabasePath("upstream");
            Initialize(path, 1);
            Execute(path, "ALTER TABLE target DROP COLUMN prioritizeThrough");
            Execute(path, "ALTER TABLE exposureplan DROP COLUMN skipWhilePrioritized");
            Execute(path, "PRAGMA user_version = 24");
            Execute(path, "PRAGMA application_id = 0");

            Initialize(path, 2);
            UserVersion(path).Should().Be(24);
            ColumnExists(path, "target", "prioritizeThrough").Should().BeTrue();
            ColumnExists(path, "exposureplan", "skipWhilePrioritized").Should().BeTrue();
            ApplicationId(path).Should().Be(SchedulerDatabaseContext.COLUMN_ADDITIONS_MARKER);
        }

        [Test]
        public void TestNoResetWhenMarked() {
            // Simulate a marked database (columns present) later migrated to 24 by an upstream release
            string path = NewDatabasePath("marked");
            Initialize(path, 1);
            Execute(path, "PRAGMA user_version = 24");

            Initialize(path, 2);
            UserVersion(path).Should().Be(24);
        }

        private string NewDatabasePath(string name) {
            string path = Path.Combine(Path.GetTempPath(), $"scheduler-column-additions-{name}.sqlite");
            if (File.Exists(path)) {
                File.Delete(path);
            }

            return path;
        }

        /// <summary>
        /// Run the database initializer.  EF only initializes once per connection string, so a distinct (but equivalent)
        /// connection string is used for each pass.
        /// </summary>
        private void Initialize(string path, int pass) {
            string connectionString = pass == 1 ? $"Data Source={path};" : $"Data Source={path};Version=3;";
            using (var context = new SchedulerDatabaseInteraction(connectionString).GetContext()) {
                context.Database.Initialize(false);
            }
        }

        private void Execute(string path, string sql) {
            using (var connection = new SQLiteConnection($"Data Source={path}")) {
                connection.Open();
                using (var command = new SQLiteCommand(sql, connection)) {
                    command.ExecuteNonQuery();
                }
            }
        }

        private long Query(string path, string sql) {
            using (var connection = new SQLiteConnection($"Data Source={path}")) {
                connection.Open();
                using (var command = new SQLiteCommand(sql, connection)) {
                    return (long)command.ExecuteScalar();
                }
            }
        }

        private long UserVersion(string path) => Query(path, "PRAGMA user_version");

        private long ApplicationId(string path) => Query(path, "PRAGMA application_id");

        private bool ColumnExists(string path, string table, string column) {
            return Query(path, $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'") > 0;
        }
    }
}
