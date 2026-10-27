using NINA.Core.Utility.Notification;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NINA.Plugin.TargetScheduler.Database {
    public interface ISchedulerDatabaseInteraction
    {
        ISchedulerDatabaseContext GetContext();
    }

    public class SchedulerDatabaseInteraction : ISchedulerDatabaseInteraction {
        private static readonly string DATABASE_BASENAME = "schedulerdb";
        public static readonly string DATABASE_SUFFIX = "sqlite";
        public static readonly string DATABASE_FILENAME = $"{DATABASE_BASENAME}.{DATABASE_SUFFIX}";
        private static readonly int DATABASE_BACKUPS = 3;

        private string connectionString;

        public SchedulerDatabaseInteraction()
            : this(string.Format(@"Data Source={0};", Environment.ExpandEnvironmentVariables($@"{Common.PLUGIN_HOME}\{DATABASE_FILENAME}"))) {
        }

        public SchedulerDatabaseInteraction(string connectionString) {
            this.connectionString = connectionString;
        }

        public ISchedulerDatabaseContext GetContext() {
            return new SchedulerDatabaseContext(connectionString);
        }

        public static void BackupDatabase() {
            string sourceFile = Path.Combine(Common.PLUGIN_HOME, DATABASE_FILENAME);
            if (!File.Exists(sourceFile)) {
                return;
            }

            string newFileName = null;
            try {
                string renamed = $"{DATABASE_BASENAME}-{DateTime.Now:yyyy-MM-dd-HH-mm-ss-fff}-{Guid.NewGuid():N}-backup.{DATABASE_SUFFIX}";
                newFileName = Path.Combine(Common.PLUGIN_HOME, renamed);
                TSLogger.Debug($"backing up Target Scheduler database to {newFileName}");
                File.Copy(sourceFile, newFileName);

                // Keep only the most recent N backups
                List<FileInfo> dbFiles = new DirectoryInfo(Common.PLUGIN_HOME).GetFiles($"*-backup.{DATABASE_SUFFIX}")
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                if (dbFiles.Count <= DATABASE_BACKUPS) {
                    return;
                }

                for (int i = DATABASE_BACKUPS; i < dbFiles.Count; i++) {
                    string filename = dbFiles[i].FullName;
                    TSLogger.Debug($"removing older backup database file: {filename}");
                    File.Delete(filename);
                }
            } catch (Exception e) {
                TSLogger.Error($"failed to back up database from {sourceFile} to {newFileName ?? Common.PLUGIN_HOME}", e);
                string logDirectory = Path.Combine(Common.PLUGIN_HOME, "Logs");
                Notification.ShowWarning($"Failed to back up Target Scheduler database: {e.Message}. See {logDirectory} for details.");
            }
        }
    }
}