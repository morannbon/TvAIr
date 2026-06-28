using System.IO.Compression;

namespace TvAIr.Core;

public sealed class AirhythmBackupService
{
    private readonly Database db;
    private readonly AirhythmProfileService profileService;
    private readonly object syncRoot = new();

    public AirhythmBackupService(Database db, AirhythmProfileService profileService)
    {
        this.db = db;
        this.profileService = profileService;
    }

    public AirhythmBackupInfo GetInfo()
    {
        var snapshotDir = GetSnapshotDirectory();
        Directory.CreateDirectory(snapshotDir);
        var latest = Directory.GetFiles(snapshotDir, "TvAIr_Backup_*.zip", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
        return new AirhythmBackupInfo
        {
            DataDirectory = db.DataDirectory,
            SnapshotDirectory = snapshotDir,
            LatestBackupPath = latest ?? string.Empty,
            LatestBackupAt = latest is null ? null : File.GetLastWriteTime(latest)
        };
    }

    public AirhythmBackupInfo CreateSnapshot()
    {
        lock (syncRoot)
        {
            var info = GetInfo();
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var zipPath = Path.Combine(info.SnapshotDirectory, $"TvAIr_Backup_{ts}.zip");
            var stagingDir = Path.Combine(info.SnapshotDirectory, $"_staging_{ts}");
            Directory.CreateDirectory(stagingDir);

            try
            {
                var dataDir = Path.Combine(stagingDir, "Data");
                Directory.CreateDirectory(dataDir);

                CopyIfExists(db.DbPath, Path.Combine(dataDir, Path.GetFileName(db.DbPath)));
                CopyIfExists(profileService.GetProfilePath(), Path.Combine(dataDir, Path.GetFileName(profileService.GetProfilePath())));

                foreach (var name in new[] { "tvair.db-wal", "tvair.db-shm", "TvAIr.ini", "appsettings.json" })
                {
                    var src = Path.Combine(db.DataDirectory, name);
                    CopyIfExists(src, Path.Combine(dataDir, name));
                }

                var memoPath = Path.Combine(stagingDir, "handover.txt");
                File.WriteAllText(memoPath, BuildHandoverText());

                if (File.Exists(zipPath)) File.Delete(zipPath);
                ZipFile.CreateFromDirectory(stagingDir, zipPath, CompressionLevel.Fastest, false);
            }
            finally
            {
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            }

            return GetInfo();
        }
    }

    private string GetSnapshotDirectory() => Path.Combine(db.DataDirectory, "Backups");

    private static void CopyIfExists(string src, string dest)
    {
        if (!File.Exists(src)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(src, dest, true);
    }

    private string BuildHandoverText()
    {
        var profile = profileService.Get();
        return $"""
TvAIr AI-rhythm バックアップメモ
生成日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

[含まれるもの]
- tvair.db
- airhythm-profile.json
- tvair.db-wal / tvair.db-shm（存在する場合）
- TvAIr.ini / appsettings.json（DataDirectory配下に存在する場合）

[AI-rhythm 設定]
- ユーザー呼称: {profile.UserNickname}
- AI呼称: {profile.AssistantNickname}

[目的]
- 引き継ぎ時の復旧
- AI-rhythm の傾向データ保持
- AI-rhythm 関連データの退避
""";
    }
}
