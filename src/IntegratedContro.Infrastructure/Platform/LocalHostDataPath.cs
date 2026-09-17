namespace IntegratedContro.Infrastructure;

// Local disk and reparse-point policy; independent of the database engine.
public static class LocalHostDataPath
{
    public static string Validate(string path, bool initialize)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") ||
            path.StartsWith("//") || path.StartsWith(@"\?"))
            throw new ArgumentException("명시적인 로컬 절대 데이터 폴더가 필요합니다.");
        var full = Path.GetFullPath(path);
        if (new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Fixed)
            throw new ArgumentException("고정 로컬 디스크의 데이터 폴더를 선택하세요.");
        // Reject reparse-point ancestors so a local-looking path cannot redirect to a share.
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("재분석 지점/심볼릭 링크 데이터 경로는 지원하지 않습니다.");
        if (!Directory.Exists(full))
        {
            if (!initialize) throw new DirectoryNotFoundException("설정한 데이터 폴더가 없습니다. 경로를 복구하세요.");
            Directory.CreateDirectory(full);
        }
        return full;
    }
}
